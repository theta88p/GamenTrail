using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using GamenTrail.Core.Video;
using GamenTrail.Platform.Windows.Video;

namespace GamenTrail.App;

internal sealed class RegionPickerOverlay : Window
{
    private readonly TaskCompletionSource<(
        CaptureTargetDescriptor Monitor,
        int X,
        int Y,
        int Width,
        int Height)?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IReadOnlyList<CaptureTargetDescriptor> _monitors =
        new WindowsCaptureTargetProvider().GetMonitors();
    private readonly Path _shade = new() { Fill = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)) };
    private readonly Border _highlight = new()
    {
        BorderBrush = new SolidColorBrush(Color.FromRgb(56, 211, 159)),
        BorderThickness = new Thickness(3),
        IsHitTestVisible = false,
        Visibility = Visibility.Collapsed,
    };
    private CaptureTargetDescriptor? _dragMonitor;
    private ScreenPoint _start;
    private bool _isDragging;

    private RegionPickerOverlay()
    {
        Title = "録画範囲を選択";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        Cursor = Cursors.Cross;
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        var canvas = new Canvas
        {
            Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
        };
        canvas.Children.Add(_shade);
        canvas.Children.Add(_highlight);

        var root = new Grid { Background = Brushes.Transparent };
        root.Children.Add(canvas);
        root.Children.Add(new Border
        {
            Margin = new Thickness(0, 28, 0, 0),
            Padding = new Thickness(18, 10, 18, 10),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Color.FromArgb(225, 21, 26, 34)),
            CornerRadius = new CornerRadius(8),
            Child = new TextBlock
            {
                Foreground = Brushes.White,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Text = "録画する範囲をドラッグ（Escでキャンセル）",
            },
        });
        Content = root;

        Loaded += OnLoaded;
        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseLeftButtonUp;
        KeyDown += OnKeyDown;
        Closed += OnClosed;
    }

    public static async Task<(
        CaptureTargetDescriptor Monitor,
        int X,
        int Y,
        int Width,
        int Height)?> PickAsync(CancellationToken cancellationToken = default)
    {
        var overlay = new RegionPickerOverlay();
        using var registration = cancellationToken.Register(
            () => overlay.Dispatcher.BeginInvoke(new Action(() => overlay.Complete(null))));
        overlay.Show();
        return await overlay._completion.Task.ConfigureAwait(true);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _shade.Width = ActualWidth;
        _shade.Height = ActualHeight;
        UpdateHighlight(null);
        Activate();
        Focus();
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var point = GetScreenPoint(e);
        _dragMonitor = FindMonitor(point);
        if (_dragMonitor is null)
        {
            return;
        }

        _start = ClampToMonitor(point, _dragMonitor);
        _isDragging = true;
        CaptureMouse();
        UpdateDrag(point);
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_isDragging)
        {
            UpdateDrag(GetScreenPoint(e));
        }
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging || _dragMonitor is null)
        {
            return;
        }

        var rectangle = CreateRectangle(ClampToMonitor(GetScreenPoint(e), _dragMonitor));
        _isDragging = false;
        ReleaseMouseCapture();
        if (rectangle.Width > 0 && rectangle.Height > 0)
        {
            Complete((_dragMonitor, rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height));
        }

        e.Handled = true;
    }

    private void UpdateDrag(ScreenPoint current)
    {
        if (_dragMonitor is null)
        {
            return;
        }

        UpdateHighlight(CreateRectangle(ClampToMonitor(current, _dragMonitor)));
    }

    private void UpdateHighlight(ScreenRectangle? screenRectangle)
    {
        var geometry = new GeometryGroup { FillRule = FillRule.EvenOdd };
        geometry.Children.Add(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)));
        if (screenRectangle is null || screenRectangle.Value.Width <= 0 || screenRectangle.Value.Height <= 0)
        {
            _shade.Data = geometry;
            _highlight.Visibility = Visibility.Collapsed;
            return;
        }

        var value = screenRectangle.Value;
        var topLeft = PointFromScreen(new Point(value.X, value.Y));
        var bottomRight = PointFromScreen(new Point(value.X + value.Width, value.Y + value.Height));
        var rectangle = new Rect(topLeft, bottomRight);
        geometry.Children.Add(new RectangleGeometry(rectangle));
        _shade.Data = geometry;

        Canvas.SetLeft(_highlight, rectangle.Left);
        Canvas.SetTop(_highlight, rectangle.Top);
        _highlight.Width = rectangle.Width;
        _highlight.Height = rectangle.Height;
        _highlight.Visibility = Visibility.Visible;
    }

    private CaptureTargetDescriptor? FindMonitor(ScreenPoint point) =>
        _monitors.FirstOrDefault(monitor =>
            point.X >= monitor.X && point.Y >= monitor.Y &&
            point.X < (long)monitor.X + monitor.Width &&
            point.Y < (long)monitor.Y + monitor.Height);

    private static ScreenPoint ClampToMonitor(ScreenPoint point, CaptureTargetDescriptor monitor) => new(
        Math.Clamp(point.X, monitor.X, monitor.X + monitor.Width),
        Math.Clamp(point.Y, monitor.Y, monitor.Y + monitor.Height));

    private ScreenRectangle CreateRectangle(ScreenPoint end) => new(
        Math.Min(_start.X, end.X),
        Math.Min(_start.Y, end.Y),
        Math.Abs(end.X - _start.X),
        Math.Abs(end.Y - _start.Y));

    private ScreenPoint GetScreenPoint(MouseEventArgs e)
    {
        var point = PointToScreen(e.GetPosition(this));
        return new ScreenPoint(checked((int)Math.Round(point.X)), checked((int)Math.Round(point.Y)));
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Escape)
        {
            Complete(null);
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        ReleaseMouseCapture();
        _completion.TrySetResult(null);
    }

    private void Complete((
        CaptureTargetDescriptor Monitor,
        int X,
        int Y,
        int Width,
        int Height)? result)
    {
        if (_completion.TrySetResult(result))
        {
            Close();
        }
    }

    private readonly record struct ScreenPoint(int X, int Y);

    private readonly record struct ScreenRectangle(int X, int Y, int Width, int Height);
}
