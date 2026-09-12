using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using GamenTrail.Platform.Windows.Interop;

namespace GamenTrail.App;

internal sealed partial class RecordingRegionBorderOverlay : Window
{
    private const int BorderWidthInPixels = 3;
    private const int ExtendedWindowStyleIndex = -20;
    private const int NoActivateStyle = 0x08000000;
    private const int ToolWindowStyle = 0x00000080;
    private const int TransparentStyle = 0x00000020;
    private const uint ExcludeFromCaptureAffinity = 0x00000011;
    private const uint NoActivatePositionFlag = 0x0010;
    private static readonly nint TopmostWindow = new(-1);
    private readonly int _x;
    private readonly int _y;
    private readonly int _width;
    private readonly int _height;
    private readonly Border _border = new()
    {
        BorderBrush = Brushes.Yellow,
        IsHitTestVisible = false,
    };

    public RecordingRegionBorderOverlay(int x, int y, int width, int height)
    {
        _x = x;
        _y = y;
        _width = width;
        _height = height;

        Title = "録画範囲";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowActivated = false;
        ShowInTaskbar = false;
        Topmost = true;
        IsHitTestVisible = false;
        Content = _border;

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        DpiChanged += OnDpiChanged;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var extendedStyle = GetWindowLong(handle, ExtendedWindowStyleIndex);
        _ = SetWindowLong(
            handle,
            ExtendedWindowStyleIndex,
            extendedStyle | NoActivateStyle | ToolWindowStyle | TransparentStyle);
        _ = SetWindowDisplayAffinity(handle, ExcludeFromCaptureAffinity);
        ApplyBounds(handle);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        ApplyBounds(handle);
        UpdateBorderThickness(GetDpiForWindow(handle));
    }

    private void OnDpiChanged(object sender, DpiChangedEventArgs e)
    {
        UpdateBorderThickness(e.NewDpi.PixelsPerInchX);

        // WPF preserves the window's logical dimensions when handling WM_DPICHANGED.
        // Reapply the capture rectangle after that handling so the border continues
        // to use the same physical-pixel coordinates as Windows Graphics Capture.
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() => ApplyBounds(new WindowInteropHelper(this).Handle)));
    }

    private void ApplyBounds(nint handle)
    {
        using var dpiAwareness = new ThreadDpiAwarenessScope();
        _ = SetWindowPos(
            handle,
            TopmostWindow,
            _x - BorderWidthInPixels,
            _y - BorderWidthInPixels,
            _width + (BorderWidthInPixels * 2),
            _height + (BorderWidthInPixels * 2),
            NoActivatePositionFlag);
    }

    private void UpdateBorderThickness(double dpi)
    {
        var dpiScale = dpi == 0 ? 1d : dpi / 96d;
        var borderThickness = BorderWidthInPixels / dpiScale;
        _border.BorderThickness = new Thickness(borderThickness);
    }

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static partial int GetWindowLong(nint window, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static partial int SetWindowLong(nint window, int index, int value);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowDisplayAffinity(nint window, uint affinity);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint window);

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
