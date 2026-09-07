using GamenTrail.Core.Video;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;

namespace GamenTrail.Platform.Windows.Video;

public sealed class WindowsCaptureFrameBuffer : IVideoFrameBuffer
{
    private Direct3D11CaptureFrame? _frame;

    internal WindowsCaptureFrameBuffer(Direct3D11CaptureFrame frame, int cropX, int cropY)
    {
        _frame = frame ?? throw new ArgumentNullException(nameof(frame));
        CropX = cropX;
        CropY = cropY;
        Width = frame.ContentSize.Width - cropX;
        Height = frame.ContentSize.Height - cropY;
    }

    internal int CropX { get; }

    internal int CropY { get; }

    public int Width { get; }

    public int Height { get; }

    public VideoPixelFormat PixelFormat => VideoPixelFormat.Bgra32;

    public IDirect3DSurface Surface =>
        (_frame ?? throw new ObjectDisposedException(nameof(WindowsCaptureFrameBuffer))).Surface;

    public void Dispose()
    {
        Interlocked.Exchange(ref _frame, null)?.Dispose();
    }
}
