using System.Runtime.InteropServices;
using GamenTrail.Core.Video;
using GamenTrail.Platform.Windows.Interop;

namespace GamenTrail.Platform.Windows.Video;

internal static partial class WindowsWindowScreenRegion
{
    private const uint ExtendedFrameBounds = 9;
    private const uint MonitorDefaultToNearest = 2;

    public static CaptureTarget.Region Create(nint window)
    {
        using var dpiAwareness = new ThreadDpiAwarenessScope();
        if (!IsAvailable(window))
        {
            throw new InvalidOperationException("対象ウィンドウは既に閉じられています。");
        }

        var result = DwmGetWindowAttribute(
            window,
            ExtendedFrameBounds,
            out var windowBounds,
            Marshal.SizeOf<NativeRect>());
        if (result < 0)
        {
            Marshal.ThrowExceptionForHR(result);
        }

        var width = windowBounds.Right - windowBounds.Left;
        var height = windowBounds.Bottom - windowBounds.Top;
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("対象ウィンドウの画面上の範囲を取得できませんでした。");
        }

        var monitor = MonitorFromRect(in windowBounds, MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo
        {
            Size = checked((uint)Marshal.SizeOf<MonitorInfo>()),
        };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref monitorInfo))
        {
            throw new InvalidOperationException("対象ウィンドウがあるモニターを取得できませんでした。");
        }

        var monitorBounds = monitorInfo.Monitor;
        if (windowBounds.Left < monitorBounds.Left ||
            windowBounds.Top < monitorBounds.Top ||
            windowBounds.Right > monitorBounds.Right ||
            windowBounds.Bottom > monitorBounds.Bottom)
        {
            throw new InvalidOperationException(
                "手前のウィンドウも録画する場合、対象ウィンドウを1台のモニター内に収めてください。");
        }

        return new CaptureTarget.Region(
            monitor,
            windowBounds.Left - monitorBounds.Left,
            windowBounds.Top - monitorBounds.Top,
            width,
            height);
    }

    public static bool IsAvailable(nint window) => window != 0 && IsWindow(window);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(
        nint window,
        uint attribute,
        out NativeRect value,
        int size);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(nint window);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromRect(in NativeRect rectangle, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;

        public int Top;

        public int Right;

        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;

        public NativeRect Monitor;

        public NativeRect WorkArea;

        public uint Flags;
    }
}
