using GamenTrail.Core.Media;

namespace GamenTrail.Core.Video;

public sealed class VideoFrame(IVideoFrameBuffer buffer, TimeSpan timestamp) : IDisposable
{
    public IVideoFrameBuffer Buffer { get; } = buffer ?? throw new ArgumentNullException(nameof(buffer));

    public TimeSpan Timestamp { get; } = timestamp;

    public void Dispose() => Buffer.Dispose();
}

public sealed class EncodedVideoFrame(
    MediaBuffer buffer,
    TimeSpan timestamp,
    TimeSpan duration,
    bool isKeyFrame) : IDisposable
{
    public MediaBuffer Buffer { get; } = buffer ?? throw new ArgumentNullException(nameof(buffer));

    public TimeSpan Timestamp { get; } = timestamp;

    public TimeSpan Duration { get; } = duration;

    public bool IsKeyFrame { get; } = isKeyFrame;

    public void Dispose() => Buffer.Dispose();
}
