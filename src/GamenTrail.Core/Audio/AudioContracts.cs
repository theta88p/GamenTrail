namespace GamenTrail.Core.Audio;

using GamenTrail.Core.Media;

public interface IAudioCapture : IAsyncDisposable
{
    event EventHandler<AudioPacket>? PacketArrived;

    event EventHandler<CaptureFaultedEventArgs>? CaptureFaulted;

    AudioFormat Format { get; }

    ValueTask InitializeAsync(AudioCaptureOptions options, CancellationToken cancellationToken);

    ValueTask StartAsync(CancellationToken cancellationToken);

    ValueTask StopAsync(CancellationToken cancellationToken);
}

public sealed record AudioCaptureOptions(
    string? DeviceId = null,
    int? ProcessId = null,
    bool IncludeProcessTree = true,
    int? SampleRate = null,
    AudioSampleFormat? SampleFormat = null,
    int? BitsPerSample = null,
    int? ChannelCount = null,
    byte[]? EncodedWaveFormat = null,
    ExternalAudioEncoderSettings? ExternalEncoder = null);
