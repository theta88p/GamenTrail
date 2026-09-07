using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using GamenTrail.Core.Video;
using GamenTrail.Platform.Windows.Video;

namespace GamenTrail.App;

internal sealed class WindowPickerOverlay : Window
{
    private readonly TaskCompletionSource<CaptureTargetDescriptor?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Path _shade = new() { Fill = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)) };
    private readonly Border _highlight = new()
    {
        BorderBrush = new SolidColorBrush(Color.FromRgb(56, 211, 159)),
        BorderThickness = new Thickness(3),
        CornerRadius = new CornerRadius(5),
        IsHitTestVisible = false,
        Visibility = Visibility.Collapsed,
    };
    private readonly Canvas _canvas = new()
    {
        // A fully transparent pixel in a layered window can become mouse-transparent.
        // Keep the smallest possible alpha so the overlay remains the hit-test target.
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
    };
    private readonly DispatcherTimer _timer;
    private CaptureTargetDescriptor? _currentTarget;
    private nint _handle;

    private WindowPickerOverlay()
    {
        Title = "録画対象を選択";
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

        var root = new Grid { Background = Brushes.Transparent };
        _canvas.Children.Add(_shade);
        _canvas.Children.Add(_highlight);
        root.Children.Add(_canvas);
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
                Text = "録画するウィンドウをクリック（Escでキャンセル）",
            },
        });
        Content = root;

        _timer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(30),
            DispatcherPriority.Input,
            (_, _) => UpdateTarget(),
            Dispatcher);
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        MouseLeftButtonDown += OnMouseLeftButtonDown;
        KeyDown += OnKeyDown;
        Closed += OnClosed;
    }

    public static async Task<CaptureTargetDescriptor?> PickAsync(CancellationToken cancellationToken = default)
    {
        var overlay = new WindowPickerOverlay();
        using var registration = cancellationToken.Register(
            () => overlay.Dispatcher.BeginInvoke(new Action(() => overlay.Complete(null))));
        overlay.Show();
        return await overlay._completion.Task.ConfigureAwait(true);
    }

    private void OnSourceInitialized(object? sender, EventArgs e) =>
        _handle = new WindowInteropHelper(this).Handle;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _shade.Width = ActualWidth;
        _shade.Height = ActualHeight;
        Activate();
        Focus();
        UpdateTarget();
        _timer.Start();
    }

    private void UpdateTarget()
    {
        _currentTarget = WindowsCaptureTargetProvider.GetWindowAtCursor(_handle);
        UpdateHighlight(_currentTarget);
    }

    private void UpdateHighlight(CaptureTargetDescriptor? target)
    {
        var geometry = new GeometryGroup { FillRule = FillRule.EvenOdd };
        geometry.Children.Add(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)));
        if (target is null)
        {
            _highlight.Visibility = Visibility.Collapsed;
            _shade.Data = geometry;
            return;
        }

        var topLeft = PointFromScreen(new Point(target.X, target.Y));
        var bottomRight = PointFromScreen(new Point(
            target.X + target.Width,
            target.Y + target.Height));
        var rect = new Rect(topLeft, bottomRight);
        geometry.Children.Add(new RectangleGeometry(rect, 5, 5));
        _shade.Data = geometry;

        Canvas.SetLeft(_highlight, rect.Left);
        Canvas.SetTop(_highlight, rect.Top);
        _highlight.Width = rect.Width;
        _highlight.Height = rect.Height;
        _highlight.Visibility = Visibility.Visible;
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        UpdateTarget();
        if (_currentTarget is not null)
        {
            Complete(_currentTarget);
        }
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
        _timer.Stop();
        _completion.TrySetResult(null);
    }

    private void Complete(CaptureTargetDescriptor? result)
    {
        if (!_completion.TrySetResult(result))
        {
            return;
        }

        _timer.Stop();
        Close();
    }
}
