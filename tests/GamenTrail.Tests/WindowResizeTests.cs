using System.Runtime.InteropServices;
using GamenTrail.Platform.Windows.Video;
using GamenTrail.Platform.Windows.Interop;
using GamenTrail.Core.Video;

internal static partial class WindowResizeTests
{
    public static void Run()
    {
        var previousContext = SetThreadDpiAwarenessContext(new nint(-4));
        var window = CreateWindowEx(0, "STATIC", "GamenTrail resize test", 0x00CF0000,
            100, 100, 400, 300, 0, 0, 0, 0);
        SetThreadDpiAwarenessContext(previousContext);
        if (window == 0)
        {
            throw new InvalidOperationException("Could not create resize test window.");
        }

        try
        {
            ShowWindow(window, 4); // Show the test window without activating it.
            var size = WindowsWindowResizer.Resize(window, 1920, 1080);
            if (size != (1920, 1080))
            {
                throw new InvalidOperationException($"Unexpected window size: {size}.");
            }

            var captureSize = GraphicsCaptureItemFactory.Create(new CaptureTarget.Window(window)).Size;
            if (captureSize.Width != 1920 || captureSize.Height != 1080)
            {
                throw new InvalidOperationException(
                    $"Capture must be 1920x1080, got {captureSize.Width}x{captureSize.Height}.");
            }

            var screenRegion = WindowsWindowScreenRegion.Create(window);
            if (screenRegion.Width <= 0 || screenRegion.Height <= 0)
            {
                throw new InvalidOperationException("Window screen region must have a positive size.");
            }

            size = WindowsWindowResizer.Resize(window, 640, 480);
            if (size != (640, 480))
            {
                throw new InvalidOperationException($"Unexpected second window size: {size}.");
            }

            ExpectFailure(() => WindowsWindowResizer.Resize(window, 0, 600));
            ExpectFailure(() => WindowsWindowResizer.Resize(window, 800, -1));
            ExpectFailure(() => WindowsWindowResizer.Resize(window, int.MaxValue, 600));
        }
        finally
        {
            DestroyWindow(window);
        }

        ExpectFailure(() => WindowsWindowResizer.Resize(0, 800, 600));
    }

    private static void ExpectFailure(Action action)
    {
        try
        {
            action();
        }
        catch (InvalidOperationException)
        {
            return;
        }

        throw new InvalidOperationException("Invalid resize request must be rejected.");
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint window, int command);

    [LibraryImport("user32.dll")]
    private static partial nint SetThreadDpiAwarenessContext(nint context);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowEx(uint extendedStyle, string className, string name,
        uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint window);
}
