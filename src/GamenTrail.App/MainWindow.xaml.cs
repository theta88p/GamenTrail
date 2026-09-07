using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using GamenTrail.App.Services;
using GamenTrail.App.ViewModels;
using GamenTrail.Core.Video;
using GamenTrail.Platform.Windows;
using GamenTrail.Platform.Windows.Video;

namespace GamenTrail.App;

public partial class MainWindow : Window
{
    private bool _isClosing;
    private RecordingRegionBorderOverlay? _recordingRegionBorder;

    public MainWindow()
    {
        InitializeComponent();
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        CaptureExpander.Expanded += SettingsExpander_Expanded;
        QualityExpander.Expanded += SettingsExpander_Expanded;
        SaveExpander.Expanded += SettingsExpander_Expanded;
        SetActiveStep(CaptureExpander);
        ViewModel = new MainWindowViewModel(
            new GamenTrailServices(),
            new UserDialogService(),
            new JsonSettingsService());
        DataContext = ViewModel;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    public MainWindowViewModel ViewModel { get; }

    private async void Window_Loaded(object sender, RoutedEventArgs e) =>
        await ViewModel.InitializeAsync().ConfigureAwait(true);

    private async void PickTarget_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.IsVisualPickerEnabled)
        {
            return;
        }

        await ViewModel.SuspendPreviewAsync().ConfigureAwait(true);
        var pickRegion = ViewModel.IsRegionMode;
        Hide();
        try
        {
            if (pickRegion)
            {
                await ViewModel.PickRegionAsync(RegionPickerOverlay.PickAsync).ConfigureAwait(true);
            }
            else
            {
                await ViewModel.PickWindowAsync(WindowPickerOverlay.PickAsync).ConfigureAwait(true);
            }
        }
        catch (Exception error)
        {
            new UserDialogService().ShowError(
                pickRegion ? "範囲を選択できませんでした" : "ウィンドウを選択できませんでした",
                error.Message);
        }
        finally
        {
            Show();
            Activate();
            ViewModel.ResumePreview();
        }
    }

    private void ShowCaptureSettings_Click(object sender, RoutedEventArgs e) =>
        OpenSettingsStep(CaptureExpander);

    private async void OpenAdvancedSettings_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.IsSettingsEnabled) return;
        var dialog = new AdvancedSettingsWindow(this, ViewModel.LameExecutablePath, ViewModel.FlacExecutablePath);
        if (dialog.ShowDialog() != true) return;
        try { await ViewModel.SaveExternalEncoderPathsAsync(dialog.LamePath, dialog.FlacPath).ConfigureAwait(true); }
        catch (Exception error) { new UserDialogService().ShowError("詳細設定を保存できませんでした", error.Message); }
    }

    private async void ConfigureAudioCodec_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanConfigureAudioCodec) return;
        if (ViewModel.SelectedAudioCodec?.Id is "Lame" or "Flac")
        {
            var mp3 = ViewModel.SelectedAudioCodec.Id == "Lame";
            var dialog = new AudioEncoderOptionsWindow(this, mp3, mp3 ? ViewModel.Mp3BitRate : ViewModel.FlacCompressionLevel);
            if (dialog.ShowDialog() != true) return;
            try { await ViewModel.SaveExternalAudioQualityAsync(mp3, dialog.Value).ConfigureAwait(true); }
            catch (Exception error) { new UserDialogService().ShowError("音声設定を保存できませんでした", error.Message); }
            return;
        }
        await ViewModel.ConfigureAudioCodecAsync(new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle())
            .ConfigureAwait(true);
    }
    private async void ConfigureCodec_Click(object sender, RoutedEventArgs e) =>
        await ViewModel.ConfigureCodecAsync(new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle())
            .ConfigureAwait(true);

    private void ShowAdvancedSettings_Click(object sender, RoutedEventArgs e)
    {
        OpenSettingsStep(QualityExpander);
    }

    private void ShowSaveSettings_Click(object sender, RoutedEventArgs e) =>
        OpenSettingsStep(SaveExpander);

    private void OpenSettingsStep(Expander target)
    {
        CaptureExpander.IsExpanded = ReferenceEquals(target, CaptureExpander);
        QualityExpander.IsExpanded = ReferenceEquals(target, QualityExpander);
        SaveExpander.IsExpanded = ReferenceEquals(target, SaveExpander);
        SetActiveStep(target);
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(target.BringIntoView));
    }

    private void SettingsExpander_Expanded(object sender, RoutedEventArgs e)
    {
        if (sender is Expander expander)
        {
            SetActiveStep(expander);
        }
    }

    private void SetActiveStep(Expander activeExpander)
    {
        var accent = (Brush)FindResource("AccentBrush");
        var accentDark = (Brush)FindResource("AccentDarkBrush");
        var subtle = (Brush)FindResource("SubtleBrush");
        var muted = (Brush)FindResource("MutedBrush");
        var foreground = Foreground;

        CaptureStepButton.BorderBrush = ReferenceEquals(activeExpander, CaptureExpander) ? accent : muted;
        QualityStepButton.BorderBrush = ReferenceEquals(activeExpander, QualityExpander) ? accent : muted;
        SaveStepButton.BorderBrush = ReferenceEquals(activeExpander, SaveExpander) ? accent : muted;
        CaptureStepButton.BorderThickness = new Thickness(0, 0, 0, ReferenceEquals(activeExpander, CaptureExpander) ? 3 : 1);
        QualityStepButton.BorderThickness = new Thickness(0, 0, 0, ReferenceEquals(activeExpander, QualityExpander) ? 3 : 1);
        SaveStepButton.BorderThickness = new Thickness(0, 0, 0, ReferenceEquals(activeExpander, SaveExpander) ? 3 : 1);


        UpdateStepVisuals(
            CaptureStepBadge,
            CaptureStepNumber,
            CaptureStepLabel,
            ReferenceEquals(activeExpander, CaptureExpander),
            accent,
            accentDark,
            subtle,
            muted,
            foreground);
        UpdateStepVisuals(
            QualityStepBadge,
            QualityStepNumber,
            QualityStepLabel,
            ReferenceEquals(activeExpander, QualityExpander),
            accent,
            accentDark,
            subtle,
            muted,
            foreground);
        UpdateStepVisuals(
            SaveStepBadge,
            SaveStepNumber,
            SaveStepLabel,
            ReferenceEquals(activeExpander, SaveExpander),
            accent,
            accentDark,
            subtle,
            muted,
            foreground);
        UpdateStepStatus(
            CaptureStepStatus,
            ReferenceEquals(activeExpander, CaptureExpander),
            accent,
            subtle);
        UpdateStepStatus(
            QualityStepStatus,
            ReferenceEquals(activeExpander, QualityExpander),
            accent,
            subtle);
        UpdateStepStatus(
            SaveStepStatus,
            ReferenceEquals(activeExpander, SaveExpander),
            accent,
            subtle);
    }

    private static void UpdateStepVisuals(
        Border badge,
        TextBlock number,
        TextBlock label,
        bool isActive,
        Brush accent,
        Brush accentDark,
        Brush subtle,
        Brush muted,
        Brush foreground)
    {
        badge.Background = isActive ? accent : Brushes.Transparent;
        badge.BorderBrush = isActive ? accent : muted;
        badge.BorderThickness = new Thickness(1);
        number.Foreground = isActive ? accentDark : subtle;
        number.FontWeight = isActive ? FontWeights.Bold : FontWeights.Normal;
        label.Foreground = isActive ? foreground : subtle;
        label.FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal;
    }

    private static void UpdateStepStatus(
        TextBlock status,
        bool isActive,
        Brush accent,
        Brush subtle)
    {
        status.Text = isActive ? "設定中" : "変更";
        status.Foreground = isActive ? accent : subtle;
        status.FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal;
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_isClosing)
        {
            return;
        }

        e.Cancel = true;
        _isClosing = true;
        CloseRecordingRegionBorder();
        try
        {
            await ViewModel.DisposeAsync().ConfigureAwait(true);
        }
        catch (Exception error)
        {
            new UserDialogService().ShowError("終了処理に失敗しました", error.Message);
        }
        finally
        {
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(Close));
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsRecording))
        {
            UpdateRecordingRegionBorder();
        }
    }

    private void UpdateRecordingRegionBorder()
    {
        CloseRecordingRegionBorder();
        if (!ViewModel.IsRecording || !ViewModel.ShowCaptureBorder ||
            !TryGetCustomBorderBounds(out var x, out var y, out var width, out var height))
        {
            return;
        }

        _recordingRegionBorder = new RecordingRegionBorderOverlay(x, y, width, height);
        _recordingRegionBorder.Show();
    }

    private bool TryGetCustomBorderBounds(out int x, out int y, out int width, out int height)
    {
        if (ViewModel.IsRegionMode)
        {
            return TryParseRegion(out x, out y, out width, out height);
        }

        if (ViewModel.IsWindowMode && ViewModel.CaptureOverlappingWindows &&
            ViewModel.SelectedTarget?.Target is CaptureTarget.Window window &&
            WindowsCaptureTargetProvider.GetWindowScreenRegion(window.Handle) is { } region)
        {
            x = region.X;
            y = region.Y;
            width = region.Width;
            height = region.Height;
            return true;
        }

        x = 0;
        y = 0;
        width = 0;
        height = 0;
        return false;
    }

    private bool TryParseRegion(out int x, out int y, out int width, out int height)
    {
        x = 0;
        y = 0;
        width = 0;
        height = 0;
        return int.TryParse(ViewModel.RegionX, NumberStyles.Integer, CultureInfo.InvariantCulture, out x) &&
            int.TryParse(ViewModel.RegionY, NumberStyles.Integer, CultureInfo.InvariantCulture, out y) &&
            int.TryParse(ViewModel.RegionWidth, NumberStyles.Integer, CultureInfo.InvariantCulture, out width) &&
            int.TryParse(ViewModel.RegionHeight, NumberStyles.Integer, CultureInfo.InvariantCulture, out height) &&
            width > 0 && height > 0;
    }

    private void CloseRecordingRegionBorder()
    {
        _recordingRegionBorder?.Close();
        _recordingRegionBorder = null;
    }
}
