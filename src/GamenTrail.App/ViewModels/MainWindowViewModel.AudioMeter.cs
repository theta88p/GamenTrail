using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using GamenTrail.Core.Audio;
using GamenTrail.Platform.Windows.Audio;

namespace GamenTrail.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private CancellationTokenSource? _audioMeterCancellation;
    private Task _audioMeterTask = Task.CompletedTask;
    private long _pendingAudioPeakBits;
    private bool _audioMeterReady;
    private bool _audioMeterCapturing;

    [ObservableProperty]
    private double audioMeterLevel;

    [ObservableProperty]
    private string audioMeterText = "待機中";

    private void AudioMeterPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SelectedAudioMode) or nameof(SelectedAudioSource))
        {
            QueueAudioMeter();
        }
    }

    private void QueueAudioMeter()
    {
        if (!_audioMeterReady)
        {
            return;
        }

        _audioMeterCancellation?.Cancel();
        var previous = _audioMeterTask;
        var cancellation = new CancellationTokenSource();
        _audioMeterCancellation = cancellation;
        _audioMeterCapturing = false;
        AudioMeterLevel = 0;
        var options = CreateAudioMeterOptions();
        AudioMeterText = SelectedAudioMode?.Mode is AudioMode.None
            ? "音声なし"
            : options is null ? "入力なし" : "接続中…";
        _audioMeterTask = UpdateAudioMeterAsync(previous, options, cancellation);
    }

    private AudioCaptureOptions? CreateAudioMeterOptions()
    {
        if (SelectedAudioMode?.Mode is AudioMode.None || SelectedAudioSource is null)
        {
            return null;
        }

        return SelectedAudioMode?.Mode is AudioMode.Process
            ? new AudioCaptureOptions(
                ProcessId: ((ProcessDescriptor)SelectedAudioSource).ProcessId,
                SampleRate: 48_000,
                SampleFormat: AudioSampleFormat.IeeeFloat,
                BitsPerSample: 32,
                ChannelCount: 2)
            : new AudioCaptureOptions(
                DeviceId: ((AudioDeviceDescriptor)SelectedAudioSource).Id,
                SampleRate: 48_000,
                SampleFormat: AudioSampleFormat.IeeeFloat,
                BitsPerSample: 32,
                ChannelCount: 2);
    }

    private async Task UpdateAudioMeterAsync(
        Task previous,
        AudioCaptureOptions? options,
        CancellationTokenSource cancellation)
    {
        try
        {
            await previous.ConfigureAwait(true);
            if (options is null)
            {
                return;
            }

            await Task.Delay(250, cancellation.Token).ConfigureAwait(true);
            await using var capture = new WasapiLoopbackCapture();
            EventHandler<GamenTrail.Core.Media.CaptureFaultedEventArgs> faulted =
                (_, e) => AudioMeterCaptureFaulted(cancellation, e);
            capture.PacketArrived += AudioMeterPacketArrived;
            capture.CaptureFaulted += faulted;
            try
            {
                await capture.InitializeAsync(options, cancellation.Token).ConfigureAwait(true);
                await capture.StartAsync(cancellation.Token).ConfigureAwait(true);
                _audioMeterCapturing = true;
                AudioMeterText = "-∞ dB";
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token).ConfigureAwait(true);
            }
            finally
            {
                _audioMeterCapturing = false;
                capture.PacketArrived -= AudioMeterPacketArrived;
                capture.CaptureFaulted -= faulted;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            if (!cancellation.IsCancellationRequested)
            {
                AudioMeterLevel = 0;
                AudioMeterText = $"取得できません: {error.Message}";
            }
        }
        finally
        {
            if (ReferenceEquals(_audioMeterCancellation, cancellation))
            {
                _audioMeterCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private void AudioMeterPacketArrived(object? sender, AudioPacket packet)
    {
        using (packet)
        {
            var samples = MemoryMarshal.Cast<byte, float>(packet.Buffer.Memory.Span);
            var peak = 0d;
            foreach (var sample in samples)
            {
                if (float.IsFinite(sample))
                {
                    peak = Math.Max(peak, Math.Abs((double)sample));
                }
            }

            var current = BitConverter.Int64BitsToDouble(Interlocked.Read(ref _pendingAudioPeakBits));
            while (peak > current)
            {
                var observed = Interlocked.CompareExchange(
                    ref _pendingAudioPeakBits,
                    BitConverter.DoubleToInt64Bits(peak),
                    BitConverter.DoubleToInt64Bits(current));
                if (observed == BitConverter.DoubleToInt64Bits(current))
                {
                    break;
                }

                current = BitConverter.Int64BitsToDouble(observed);
            }
        }
    }

    private void AudioMeterCaptureFaulted(
        CancellationTokenSource cancellation,
        GamenTrail.Core.Media.CaptureFaultedEventArgs e)
    {
        _uiContext.Post(_ =>
        {
            if (!_audioMeterReady || !ReferenceEquals(_audioMeterCancellation, cancellation))
            {
                return;
            }

            _audioMeterCapturing = false;
            AudioMeterLevel = 0;
            AudioMeterText = $"取得できません: {e.Error.Message}";
        }, null);
    }

    private void OnAudioMeterTimerTick(object? sender, EventArgs e)
    {
        if (!_audioMeterCapturing)
        {
            return;
        }

        var peak = BitConverter.Int64BitsToDouble(Interlocked.Exchange(ref _pendingAudioPeakBits, 0));
        var previousPeak = AudioMeterLevel <= 0
            ? 0
            : Math.Pow(10, ((AudioMeterLevel / 100 * 60) - 60) / 20);
        var displayedPeak = Math.Clamp(Math.Max(peak, previousPeak * 0.78), 0, 1);
        if (displayedPeak < 0.001)
        {
            AudioMeterLevel = 0;
            AudioMeterText = "-∞ dB";
            return;
        }

        var decibels = Math.Max(-60, 20 * Math.Log10(displayedPeak));
        AudioMeterLevel = (decibels + 60) / 60 * 100;
        AudioMeterText = $"{decibels:0} dB";
    }

    private async Task SuspendAudioMeterAsync()
    {
        _audioMeterCancellation?.Cancel();
        await _audioMeterTask.ConfigureAwait(true);
        _audioMeterCapturing = false;
        AudioMeterLevel = 0;
    }
}
