using System.Diagnostics;
using GamenTrail.Core.Video;
using GamenTrail.Core.Media;
using GamenTrail.Platform.Windows.Interop;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace GamenTrail.Platform.Windows.Video;

public sealed class WindowsGraphicsCapture : IVideoCapture
{
    private const int FramePoolSize = 3;
    private const string CaptureSessionTypeName = "Windows.Graphics.Capture.GraphicsCaptureSession";
    private readonly object _sync = new();
    private GraphicsCaptureItem? _item;
    private IDirect3DDevice? _device;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private WindowCornerPreferenceScope? _windowCornerPreferenceScope;
    private VideoFormat? _format;
    private SizeInt32 _poolSize;
    private TimeSpan? _firstSystemRelativeTime;
    private long _fallbackTimestampOrigin;
    private long _frameIntervalTicks;
    private long _nextFrameTimestampTicks;
    private int _cropX;
    private int _cropY;
    private nint _windowToSquare;
    private nint _screenRegionWindow;
    private bool _started;

    public event EventHandler<VideoFrame>? FrameArrived;

    public event EventHandler<CaptureFaultedEventArgs>? CaptureFaulted;

    public VideoFormat Format => _format ?? throw new InvalidOperationException("Capture is not initialized.");

    public ValueTask InitializeAsync(VideoCaptureOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.FramesPerSecond, 0);
        cancellationToken.ThrowIfCancellationRequested();

        if (!GraphicsCaptureSession.IsSupported())
        {
            throw new PlatformNotSupportedException("Windows Graphics Capture is not supported on this system.");
        }

        lock (_sync)
        {
            ResetResources();
            _windowToSquare = options.DisableWindowCornerRounding &&
                options.Target is CaptureTarget.Window window
                    ? window.Handle
                    : 0;
            var captureTarget = options.Target;
            if (options.CaptureOverlappingWindows &&
                options.Target is CaptureTarget.Window screenRegionWindow)
            {
                captureTarget = WindowsWindowScreenRegion.Create(screenRegionWindow.Handle);
                _screenRegionWindow = screenRegionWindow.Handle;
            }

            _item = GraphicsCaptureItemFactory.Create(captureTarget);
            _item.Closed += OnCaptureItemClosed;
            _poolSize = _item.Size;
            ValidateSize(_poolSize);
            var outputSize = GetOutputSize(captureTarget, _poolSize, out _cropX, out _cropY);
            _format = new VideoFormat(
                outputSize.Width,
                outputSize.Height,
                options.FramesPerSecond,
                VideoPixelFormat.Bgra32);
            _frameIntervalTicks = Math.Max(1, checked((long)Math.Round(TimeSpan.TicksPerSecond / options.FramesPerSecond)));
            _device = Direct3D11DeviceFactory.Create();
            _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _device,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                FramePoolSize,
                _poolSize);
            _session = _framePool.CreateCaptureSession(_item);
            _session.IsCursorCaptureEnabled = options.IncludeCursor;

            if (ApiInformation.IsPropertyPresent(CaptureSessionTypeName, "IsBorderRequired"))
            {
                _session.IsBorderRequired = ShouldUseSystemCaptureBorder(options);
            }
        }

        return ValueTask.CompletedTask;
    }

    internal static bool ShouldUseSystemCaptureBorder(VideoCaptureOptions options) =>
        options.DrawBorder &&
        options.Target is not CaptureTarget.Region &&
        !(options.CaptureOverlappingWindows && options.Target is CaptureTarget.Window);

    public ValueTask StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            if (_session is null || _framePool is null)
            {
                throw new InvalidOperationException("Capture is not initialized.");
            }

            if (_started)
            {
                throw new InvalidOperationException("Capture is already running.");
            }

            _firstSystemRelativeTime = null;
            _fallbackTimestampOrigin = Stopwatch.GetTimestamp();
            _nextFrameTimestampTicks = 0;
            _framePool.FrameArrived += OnFrameArrived;
            _windowCornerPreferenceScope = WindowCornerPreferenceScope.TryDisable(_windowToSquare);
            try
            {
                _session.StartCapture();
                _started = true;
            }
            catch
            {
                _windowCornerPreferenceScope?.Dispose();
                _windowCornerPreferenceScope = null;
                throw;
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            ResetResources();
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            ResetResources();
            FrameArrived = null;
            CaptureFaulted = null;
        }

        return ValueTask.CompletedTask;
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        Direct3D11CaptureFrame? frame = null;
        VideoFrame? videoFrame = null;
        try
        {
            lock (_sync)
            {
                if (!_started)
                {
                    return;
                }

                if (_screenRegionWindow != 0 && !WindowsWindowScreenRegion.IsAvailable(_screenRegionWindow))
                {
                    throw new InvalidOperationException("対象ウィンドウは既に閉じられています。");
                }

                frame = sender.TryGetNextFrame();
                var contentSize = frame.ContentSize;
                if (contentSize.Width <= 0 || contentSize.Height <= 0)
                {
                    return;
                }

                if (contentSize.Width != _poolSize.Width || contentSize.Height != _poolSize.Height)
                {
                    RecreateFramePool(contentSize);
                    return;
                }

                var timestamp = GetRelativeTimestamp(frame.SystemRelativeTime);
                if (!ShouldEmitFrame(timestamp))
                {
                    return;
                }

                var buffer = new WindowsCaptureFrameBuffer(frame, _cropX, _cropY);
                frame = null;
                videoFrame = new VideoFrame(buffer, timestamp);
            }

            PublishFrame(videoFrame);
            videoFrame = null;
        }
        catch (Exception error)
        {
            CaptureFaulted?.Invoke(this, new CaptureFaultedEventArgs(error));
        }
        finally
        {
            videoFrame?.Dispose();
            frame?.Dispose();
        }
    }

    private bool ShouldEmitFrame(TimeSpan timestamp)
    {
        var timestampWithTolerance = timestamp.Ticks + Math.Max(1, _frameIntervalTicks / 20);
        if (timestampWithTolerance < _nextFrameTimestampTicks)
        {
            return false;
        }

        do
        {
            _nextFrameTimestampTicks += _frameIntervalTicks;
        }
        while (_nextFrameTimestampTicks <= timestampWithTolerance);

        return true;
    }

    private void OnCaptureItemClosed(GraphicsCaptureItem sender, object args)
    {
        CaptureFaulted?.Invoke(
            this,
            new CaptureFaultedEventArgs(new InvalidOperationException("The capture target was closed.")));
    }

    private void RecreateFramePool(SizeInt32 size)
    {
        ValidateSize(size);
        var framePool = _framePool ?? throw new InvalidOperationException("Capture is not initialized.");
        var device = _device ?? throw new InvalidOperationException("Capture is not initialized.");
        _poolSize = size;
        framePool.Recreate(
            device,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            FramePoolSize,
            size);
    }

    private TimeSpan GetRelativeTimestamp(TimeSpan? systemRelativeTime)
    {
        if (systemRelativeTime is { } timestamp)
        {
            _firstSystemRelativeTime ??= timestamp;
            return timestamp - _firstSystemRelativeTime.Value;
        }

        return Stopwatch.GetElapsedTime(_fallbackTimestampOrigin);
    }

    private void PublishFrame(VideoFrame frame)
    {
        var handler = FrameArrived;
        if (handler is null)
        {
            frame.Dispose();
            return;
        }

        handler(this, frame);
    }

    private void ResetResources()
    {
        if (_framePool is not null)
        {
            _framePool.FrameArrived -= OnFrameArrived;
        }

        if (_item is not null)
        {
            _item.Closed -= OnCaptureItemClosed;
        }

        _session?.Dispose();
        _windowCornerPreferenceScope?.Dispose();
        _framePool?.Dispose();
        (_device as IDisposable)?.Dispose();
        _session = null;
        _windowCornerPreferenceScope = null;
        _framePool = null;
        _item = null;
        _device = null;
        _format = null;
        _poolSize = default;
        _firstSystemRelativeTime = null;
        _frameIntervalTicks = 0;
        _nextFrameTimestampTicks = 0;
        _cropX = 0;
        _cropY = 0;
        _windowToSquare = 0;
        _screenRegionWindow = 0;
        _started = false;
    }

    private static SizeInt32 GetOutputSize(
        CaptureTarget target,
        SizeInt32 sourceSize,
        out int cropX,
        out int cropY)
    {
        cropX = 0;
        cropY = 0;
        if (target is not CaptureTarget.Region region)
        {
            return sourceSize;
        }

        ArgumentOutOfRangeException.ThrowIfNegative(region.OffsetX);
        ArgumentOutOfRangeException.ThrowIfNegative(region.OffsetY);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(region.Width, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(region.Height, 0);
        if (region.OffsetX + region.Width > sourceSize.Width ||
            region.OffsetY + region.Height > sourceSize.Height)
        {
            throw new ArgumentOutOfRangeException(
                nameof(target),
                "The capture region must fit inside its monitor.");
        }

        cropX = region.OffsetX;
        cropY = region.OffsetY;
        return new SizeInt32(region.Width, region.Height);
    }

    private static void ValidateSize(SizeInt32 size)
    {
        if (size.Width <= 0 || size.Height <= 0)
        {
            throw new InvalidOperationException("The capture target has an empty content size.");
        }
    }
}
