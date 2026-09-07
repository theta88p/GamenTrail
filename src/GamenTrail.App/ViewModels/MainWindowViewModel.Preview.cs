using System.ComponentModel;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GamenTrail.Core.Video;
using GamenTrail.Platform.Windows.Video;

namespace GamenTrail.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private CancellationTokenSource? _previewCancellation;
    private Task _previewTask = Task.CompletedTask;
    private bool _previewReady;
    private bool _previewSuspended;

    [ObservableProperty]
    private BitmapSource? previewImage;

    [ObservableProperty]
    private string previewStatus = "対象を選択するとプレビューを表示します";

    private void PreviewPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SelectedTarget) or nameof(SelectedCaptureMode) or
            nameof(RegionX) or nameof(RegionY) or nameof(RegionWidth) or nameof(RegionHeight) or
            nameof(IncludeCursor) or nameof(DisableWindowCornerRounding))
        {
            QueuePreview();
        }
    }

    [RelayCommand]
    private void RefreshPreview() => QueuePreview();

    private void QueuePreview()
    {
        if (!_previewReady || _previewSuspended)
        {
            return;
        }

        _previewCancellation?.Cancel();
        var previous = _previewTask;
        var cancellation = new CancellationTokenSource();
        _previewCancellation = cancellation;
        PreviewImage = null;
        PreviewStatus = "ライブプレビューを開始しています…（対象の最小化を解除してください）";
        _previewTask = UpdatePreviewAsync(previous, cancellation);
    }

    private async Task UpdatePreviewAsync(Task previous, CancellationTokenSource cancellation)
    {
        try
        {
            await previous.ConfigureAwait(true);
            await Task.Delay(350, cancellation.Token).ConfigureAwait(true);
            var target = CreateCaptureTarget(resolveWindow: false);
            var options = new VideoCaptureOptions(target, 10, IncludeCursor, DrawBorder: false,
                DisableWindowCornerRounding: DisableWindowCornerRounding && !IsRecording,
                CaptureOverlappingWindows: CaptureOverlappingWindows && IsRecording);
            WriteableBitmap? bitmap = null;
            await foreach (var frame in WindowsCapturePreview.StreamAsync(options, cancellation.Token)
                .ConfigureAwait(true))
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (bitmap is null || bitmap.PixelWidth != frame.Width || bitmap.PixelHeight != frame.Height)
                {
                    bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null);
                    PreviewImage = bitmap;
                }
                bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Pixels,
                    checked(frame.Width * 4), 0);
                PreviewStatus = $"ライブ · {frame.Width} × {frame.Height} px · 最大10 fps · 最終更新 {DateTime.Now:HH:mm:ss}";
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            if (!cancellation.IsCancellationRequested)
            {
                PreviewImage = null;
                PreviewStatus = $"プレビューを表示できません: {error.Message}";
            }
        }
        finally
        {
            if (ReferenceEquals(_previewCancellation, cancellation))
            {
                _previewCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    public async Task SuspendPreviewAsync()
    {
        _previewSuspended = true;
        _previewCancellation?.Cancel();
        await _previewTask.ConfigureAwait(true);
    }

    public void ResumePreview()
    {
        _previewSuspended = false;
        QueuePreview();
    }
}
