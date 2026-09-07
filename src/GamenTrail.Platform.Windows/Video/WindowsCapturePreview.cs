using System.Runtime.CompilerServices;
using System.Threading.Channels;
using GamenTrail.Core.Video;

namespace GamenTrail.Platform.Windows.Video;

public sealed record CapturePreviewFrame(int Width, int Height, byte[] Pixels);

public static class WindowsCapturePreview
{
    public static async Task<CapturePreviewFrame> CaptureAsync(
        VideoCaptureOptions options,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await foreach (var frame in StreamAsync(options, timeout.Token).ConfigureAwait(false))
            {
                return frame;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("プレビューを取得できませんでした。対象の最小化を解除してください。");
        }

        throw new InvalidOperationException("キャプチャが終了しました。");
    }

    public static async IAsyncEnumerable<CapturePreviewFrame> StreamAsync(
        VideoCaptureOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var frames = Channel.CreateBounded<CapturePreviewFrame>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        await using var capture = new WindowsGraphicsCapture();
        await Task.Run(async () =>
            await capture.InitializeAsync(options with { DrawBorder = false }, cancellationToken)
                .ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        var format = capture.Format;
        var fixedRegion = options.Target is CaptureTarget.Region || options.CaptureOverlappingWindows;
        using var reader = new D3D11TextureReader();
        var frameGate = new object();
        var stopped = false;
        capture.FrameArrived += (_, frame) =>
        {
            lock (frameGate)
            using (frame)
            {
                if (stopped || cancellationToken.IsCancellationRequested) return;
                try
                {
                    var source = (WindowsCaptureFrameBuffer)frame.Buffer;
                    var width = fixedRegion ? format.Width : source.Width;
                    var height = fixedRegion ? format.Height : source.Height;
                    using var buffer = reader.Read(source, width, height);
                    frames.Writer.TryWrite(new CapturePreviewFrame(width, height,
                        ToTopDownPixels(buffer.Memory.Span, width, height)));
                }
                catch (Exception error)
                {
                    stopped = true;
                    frames.Writer.TryComplete(error);
                }
            }
        };
        capture.CaptureFaulted += (_, args) => frames.Writer.TryComplete(args.Error);
        try
        {
            await capture.StartAsync(cancellationToken).ConfigureAwait(false);
            await foreach (var frame in frames.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return frame;
            }
        }
        finally
        {
            // Drain the GPU read before releasing the reader and capture device.
            lock (frameGate)
            {
                stopped = true;
            }
            frames.Writer.TryComplete();
        }
    }

    internal static byte[] ToTopDownPixels(ReadOnlySpan<byte> bottomUpPixels, int width, int height)
    {
        var stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];
        for (var row = 0; row < height; row++)
        {
            bottomUpPixels.Slice((height - row - 1) * stride, stride).CopyTo(pixels.AsSpan(row * stride, stride));
        }

        return pixels;
    }
}