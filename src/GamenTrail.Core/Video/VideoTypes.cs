namespace GamenTrail.Core.Video;

public enum VideoPixelFormat
{
    Bgra32,
    Bgr24,
    Yuv420,
}

public readonly record struct VideoFormat(
    int Width,
    int Height,
    double FramesPerSecond,
    VideoPixelFormat PixelFormat);

/// <summary>The uncompressed pixel layout supplied to the video codec.</summary>
public enum VideoEncodingPixelFormat
{
    Bgra32,
    Bgr24,
    Yuy2,
    Yv12,
    Nv12,
}

public sealed record VideoEncoderSettings(
    string CodecFourCc = "UMRG",
    int? ThreadCount = null,
    VideoEncodingPixelFormat PixelFormat = VideoEncodingPixelFormat.Bgra32,
    ReadOnlyMemory<byte> CodecState = default,
    bool AutoSelectInputFormat = false);

public sealed record EncodedVideoFormat(
    string CodecId,
    ReadOnlyMemory<byte> CodecPrivate,
    int Width,
    int Height,
    double FramesPerSecond);

public interface IVideoFrameBuffer : IDisposable
{
    int Width { get; }

    int Height { get; }

    VideoPixelFormat PixelFormat { get; }
}
