using System.ComponentModel;
using System.Runtime.InteropServices;

namespace GamenTrail.Platform.Windows.Video;

public static partial class WindowsWindowResizer
{
    public static (int Width, int Height) Resize(nint window, int width, int height)
    {
        if (width is < 1 or > 32767 || height is < 1 or > 32767)
        {
            throw new InvalidOperationException("幅と高さには1～32767の整数を入力してください。");
        }

        if (!IsWindow(window))
        {
            throw new InvalidOperationException("対象ウィンドウは既に閉じられています。対象を選び直してください。");
        }

        if (IsIconic(window) || IsZoomed(window))
        {
            throw new InvalidOperationException("対象ウィンドウの最大化・最小化を解除してから適用してください。");
        }

        // GetWindowRect and SetWindowPos must use physical pixels at any display scale.
        var previousContext = SetThreadDpiAwarenessContext(new nint(-4));
        if (previousContext == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            if (!GetWindowRect(window, out var outer))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var visible = GetVisibleBounds(window);
            // WGC excludes the invisible resize borders included by GetWindowRect.
            // Measure them for this window/DPI instead of assuming a fixed border size.
            var outerWidth = checked(width + (outer.Right - outer.Left) - (visible.Right - visible.Left));
            var outerHeight = checked(height + (outer.Bottom - outer.Top) - (visible.Bottom - visible.Top));
            const uint flags = 0x0002 | 0x0004 | 0x0010; // NOMOVE | NOZORDER | NOACTIVATE
            if (!SetWindowPos(window, 0, 0, 0, outerWidth, outerHeight, flags))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            Marshal.ThrowExceptionForHR(DwmFlush());
            var actual = GetVisibleBounds(window);
            return (actual.Right - actual.Left, actual.Bottom - actual.Top);
        }
        finally
        {
            SetThreadDpiAwarenessContext(previousContext);
        }
    }

    private static NativeRect GetVisibleBounds(nint window)
    {
        const uint extendedFrameBounds = 9;
        Marshal.ThrowExceptionForHR(DwmGetWindowAttribute(
            window, extendedFrameBounds, out var bounds, Marshal.SizeOf<NativeRect>()));
        if (bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top)
        {
            throw new InvalidOperationException("対象ウィンドウの録画サイズを取得できませんでした。");
        }

        return bounds;
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(nint window, uint attribute, out NativeRect value, int size);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmFlush();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsZoomed(nint window);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint SetThreadDpiAwarenessContext(nint context);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(
        nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint window, out NativeRect rectangle);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}