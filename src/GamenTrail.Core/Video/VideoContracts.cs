namespace GamenTrail.Core.Video;

using GamenTrail.Core.Media;

public interface IVideoCapture : IAsyncDisposable
{
    event EventHandler<VideoFrame>? FrameArrived;

    event EventHandler<CaptureFaultedEventArgs>? CaptureFaulted;

    VideoFormat Format { get; }

    ValueTask InitializeAsync(VideoCaptureOptions options, CancellationToken cancellationToken);

    ValueTask StartAsync(CancellationToken cancellationToken);

    ValueTask StopAsync(CancellationToken cancellationToken);
}

public interface IVideoEncoder : IAsyncDisposable
{
    EncodedVideoFormat OutputFormat { get; }

    ValueTask InitializeAsync(
        VideoFormat inputFormat,
        VideoEncoderSettings settings,
        CancellationToken cancellationToken);

    ValueTask<EncodedVideoFrame> EncodeAsync(
        VideoFrame frame,
        TimeSpan outputTimestamp,
        CancellationToken cancellationToken);

    ValueTask FlushAsync(CancellationToken cancellationToken);
}

public sealed record VideoCaptureOptions(
    CaptureTarget Target,
    double FramesPerSecond,
    bool IncludeCursor = true,
    bool DrawBorder = true,
    bool DisableWindowCornerRounding = false,
    bool CaptureOverlappingWindows = false);

public abstract record CaptureTarget
{
    private CaptureTarget()
    {
    }

    public sealed record Monitor(nint Handle) : CaptureTarget;

    public sealed record Window(nint Handle) : CaptureTarget;

    public sealed record Region(
        nint MonitorHandle,
        int OffsetX,
        int OffsetY,
        int Width,
        int Height) : CaptureTarget;
}
