using GamenTrail.Core.Media;

namespace GamenTrail.Core.Audio;

public enum AudioSampleFormat
{
    SignedInteger,
    IeeeFloat,
}

public readonly record struct AudioFormat(
    int SampleRate,
    int ChannelCount,
    int BitsPerSample,
    AudioSampleFormat SampleFormat,
    ReadOnlyMemory<byte> WaveFormat = default,
    string? CodecId = null,
    ReadOnlyMemory<byte> CodecPrivate = default);

public sealed class AudioPacket(
    MediaBuffer buffer,
    TimeSpan timestamp,
    TimeSpan duration) : IDisposable
{
    public MediaBuffer Buffer { get; } = buffer ?? throw new ArgumentNullException(nameof(buffer));

    public TimeSpan Timestamp { get; } = timestamp;

    public TimeSpan Duration { get; } = duration;

    public void Dispose() => Buffer.Dispose();
}
