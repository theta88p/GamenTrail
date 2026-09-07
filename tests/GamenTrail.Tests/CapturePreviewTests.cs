using GamenTrail.Core.Video;
using GamenTrail.Platform.Windows.Interop;
using GamenTrail.Platform.Windows.Video;

internal static class CapturePreviewTests
{
    public static async Task RunAsync()
    {
        byte[] bottomUp = [1, 2, 3, 255, 4, 5, 6, 255, 7, 8, 9, 255, 10, 11, 12, 255];
        byte[] expected = [7, 8, 9, 255, 10, 11, 12, 255, 1, 2, 3, 255, 4, 5, 6, 255];
        Assert(WindowsCapturePreview.ToTopDownPixels(bottomUp, 2, 2).SequenceEqual(expected),
            "Preview pixels must preserve channels and display rows upright.");

        var monitor = new WindowsCaptureTargetProvider().GetMonitors()[0];
        var captureSize = GraphicsCaptureItemFactory.Create(monitor.Target).Size;
        var whole = await WindowsCapturePreview.CaptureAsync(new VideoCaptureOptions(monitor.Target, 5));
        Assert(whole.Width == captureSize.Width && whole.Height == captureSize.Height,
            "Monitor preview dimensions must match the capture target.");
        Assert(whole.Pixels.Length == whole.Width * whole.Height * 4, "Preview must contain BGRA pixels.");

        var handle = ((CaptureTarget.Monitor)monitor.Target).Handle;
        var region = new CaptureTarget.Region(handle, 10, 20, 64, 48);
        var cropped = await WindowsCapturePreview.CaptureAsync(new VideoCaptureOptions(region, 5));
        Assert(cropped.Width == 64 && cropped.Height == 48 && cropped.Pixels.Length == 64 * 48 * 4,
            "Region preview must be cropped to the requested dimensions.");

        using (var liveCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            await using var live = WindowsCapturePreview.StreamAsync(
                new VideoCaptureOptions(region, 10), liveCancellation.Token).GetAsyncEnumerator();
            for (var index = 0; index < 3; index++)
            {
                Assert(await live.MoveNextAsync(), "Live preview must keep producing frames in one session.");
                Assert(live.Current.Width == 64 && live.Current.Height == 48,
                    "Every live preview frame must retain the selected crop.");
            }
            var simultaneous = await WindowsCapturePreview.CaptureAsync(new VideoCaptureOptions(region, 10));
            Assert(simultaneous.Width == 64, "A second capture must work while live preview is active.");
            for (var index = 0; index < 2; index++)
            {
                Assert(await live.MoveNextAsync(), "Live preview must survive disposal of a separate capture session.");
            }
            liveCancellation.Cancel();
            try
            {
                await live.MoveNextAsync();
                throw new InvalidOperationException("Cancellation must stop a running live preview.");
            }
            catch (OperationCanceledException)
            {
            }
        }

        // Reconnecting after disposal must release and recreate the capture resources successfully.
        var reconnected = await WindowsCapturePreview.CaptureAsync(new VideoCaptureOptions(region, 10));
        Assert(reconnected.Pixels.Length == 64 * 48 * 4, "Preview must reconnect after stopping a live session.");

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await WindowsCapturePreview.CaptureAsync(new VideoCaptureOptions(region, 5), cancellation.Token);
            throw new InvalidOperationException("Cancelled preview must not succeed.");
        }
        catch (OperationCanceledException)
        {
        }

        try
        {
            await WindowsCapturePreview.CaptureAsync(new VideoCaptureOptions(region with { OffsetX = -1 }, 5));
            throw new InvalidOperationException("Invalid preview region must not succeed.");
        }
        catch (ArgumentOutOfRangeException)
        {
        }

        Console.WriteLine("Capture preview tests passed.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}