using System.Runtime.InteropServices;
using GamenTrail.Core.Video;

namespace GamenTrail.Platform.Windows.Video;

public sealed partial class WindowsCaptureTargetProvider : ICaptureTargetProvider
{
    public IReadOnlyList<CaptureTargetDescriptor> GetMonitors()
    {
        var result = new List<CaptureTargetDescriptor>();
        var index = 0;
        EnumDisplayMonitors(0, 0, Callback, 0);
        return result;

        bool Callback(nint monitor, nint deviceContext, ref NativeRect bounds, nint data)
        {
            index++;
            result.Add(new CaptureTargetDescriptor(
                $"monitor:{monitor:X}",
                $"Display {index}",
                new CaptureTarget.Monitor(monitor),
                Math.Max(0, bounds.Right - bounds.Left),
                Math.Max(0, bounds.Bottom - bounds.Top),
                bounds.Left,
                bounds.Top));
            return true;
        }
    }

    public IReadOnlyList<CaptureTargetDescriptor> GetWindows()
    {
        var result = new List<CaptureTargetDescriptor>();
        EnumWindows(Callback, 0);
        return result;

        bool Callback(nint window, nint data)
        {
            var descriptor = GetWindow(window);
            if (descriptor is not null)
            {
                result.Add(descriptor);
            }

            return true;
        }
    }

    public static CaptureTargetDescriptor? GetWindow(nint window)
    {
        if (!IsWindowVisible(window) || GetWindowTextLength(window) == 0)
        {
            return null;
        }

        var title = new char[GetWindowTextLength(window) + 1];
        var titleLength = GetWindowText(window, title, title.Length);
        if (titleLength <= 0 || !GetWindowRect(window, out var bounds))
        {
            return null;
        }

        var width = bounds.Right - bounds.Left;
        var height = bounds.Bottom - bounds.Top;
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        _ = GetWindowThreadProcessId(window, out var processId);
        return new CaptureTargetDescriptor(
            $"window:{window:X}",
            new string(title, 0, titleLength),
            new CaptureTarget.Window(window),
            width,
            height,
            bounds.Left,
            bounds.Top,
            ProcessId: processId == 0 ? null : checked((int)processId));
    }

    public static CaptureTargetDescriptor? GetWindowScreenRegion(nint window)
    {
        var windowDescriptor = GetWindow(window);
        if (windowDescriptor is null)
        {
            return null;
        }

        CaptureTarget.Region region;
        try
        {
            region = WindowsWindowScreenRegion.Create(window);
        }
        catch (Exception error) when (error is InvalidOperationException or ExternalException)
        {
            return null;
        }

        var monitorDescriptor = new WindowsCaptureTargetProvider()
            .GetMonitors()
            .FirstOrDefault(candidate =>
                candidate.Target is CaptureTarget.Monitor monitor &&
                monitor.Handle == region.MonitorHandle);
        if (monitorDescriptor is null)
        {
            return null;
        }

        return windowDescriptor with
        {
            Target = region,
            X = monitorDescriptor.X + region.OffsetX,
            Y = monitorDescriptor.Y + region.OffsetY,
            Width = region.Width,
            Height = region.Height,
        };
    }

    public static CaptureTargetDescriptor? GetWindowAtPoint(int x, int y, nint excludedWindow)
    {
        CaptureTargetDescriptor? result = null;
        EnumWindows(Callback, 0);
        return result;

        bool Callback(nint window, nint data)
        {
            if (window == excludedWindow)
            {
                return true;
            }

            var descriptor = GetWindow(window);
            if (descriptor is null ||
                x < descriptor.X || y < descriptor.Y ||
                x >= (long)descriptor.X + descriptor.Width ||
                y >= (long)descriptor.Y + descriptor.Height)
            {
                return true;
            }

            result = descriptor;
            return false;
        }
    }

    public static CaptureTargetDescriptor? GetWindowAtCursor(nint excludedWindow)
    {
        return GetCursorPos(out var point)
            ? GetWindowAtPoint(point.X, point.Y, excludedWindow)
            : null;
    }

    private delegate bool MonitorEnumerationCallback(
        nint monitor,
        nint deviceContext,
        ref NativeRect bounds,
        nint data);

    private delegate bool WindowEnumerationCallback(nint window, nint data);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumDisplayMonitors(
        nint deviceContext,
        nint clipRectangle,
        MonitorEnumerationCallback callback,
        nint data);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumWindows(WindowEnumerationCallback callback, nint data);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint window);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
    private static partial int GetWindowTextLength(nint window);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetWindowText(nint window, [Out] char[] text, int maximumLength);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint window, out NativeRect rectangle);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;

        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;

        public int Top;

        public int Right;

        public int Bottom;
    }
}
