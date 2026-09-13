using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using GamenTrail.Platform.Windows.Interop;

namespace GamenTrail.App;

internal static partial class VirtualScreenOverlay
{
    private const int VirtualScreenLeft = 76;
    private const int VirtualScreenTop = 77;
    private const int VirtualScreenWidth = 78;
    private const int VirtualScreenHeight = 79;
    private const uint NoActivate = 0x0010;
    private const uint PreserveZOrder = 0x0004;

    public static void ApplyBounds(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0)
        {
            return;
        }

        using var dpiAwareness = new ThreadDpiAwarenessScope();
        var width = GetSystemMetrics(VirtualScreenWidth);
        var height = GetSystemMetrics(VirtualScreenHeight);
        if (width <= 0 || height <= 0)
        {
            return;
        }

        _ = SetWindowPos(
            handle,
            0,
            GetSystemMetrics(VirtualScreenLeft),
            GetSystemMetrics(VirtualScreenTop),
            width,
            height,
            PreserveZOrder | NoActivate);
    }

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(
        nint window,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
