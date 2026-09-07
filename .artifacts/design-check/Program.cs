using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GamenTrail.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var app = new Application();
        var window = new MainWindow();
        var vm = window.ViewModel;
        vm.StatusText = "録画対象を選択してください";
        vm.SelectedCaptureMode = vm.CaptureModes[1];
        var root = (FrameworkElement)window.Content;
        void Render(string name, int width, int height)
        {
            root.Width = width; root.Height = height;
            root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
            root.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(".artifacts/design-check", name + ".png")); png.Save(stream);
        }
        Render("capture", 976, 994);
        static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
        {
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                var child = VisualTreeHelper.GetChild(parent, index);
                yield return child;
                foreach (var nested in Descendants(child)) yield return nested;
            }
        }
        var modes = Descendants(root).OfType<ListBox>().Single();
        modes.SelectedIndex = 2;
        if (!vm.IsRegionMode) throw new InvalidOperationException("Mode selection binding failed");
        modes.SelectedIndex = 1;
        if (!vm.IsWindowMode) throw new InvalidOperationException("Window mode binding failed");
        var cursor = Descendants(root).OfType<CheckBox>().Single(check => (string)check.Content == "マウスカーソルを含める");
        cursor.IsChecked = false;
        if (vm.IncludeCursor) throw new InvalidOperationException("Cursor checkbox binding failed");
        cursor.IsChecked = true;
        Render("compact", 804, 681);
        ((Button)window.FindName("QualityStepButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        if (!((Expander)window.FindName("QualityExpander")).IsExpanded || ((Expander)window.FindName("CaptureExpander")).IsExpanded) throw new InvalidOperationException("Quality navigation failed");
        Render("quality", 976, 994);
        var audioMode = (ComboBox)window.FindName("AudioModeSelector");
        var audioSource = (ComboBox)window.FindName("AudioSourceSelector");
        var audioCodec = (ComboBox)window.FindName("AudioCodecSelector");
        audioMode.SelectedIndex = 2;
        root.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        if (vm.IsAudioSettingsEnabled || audioSource.IsEnabled || audioCodec.IsEnabled)
            throw new InvalidOperationException("No-audio mode must disable source and codec settings");
        audioMode.SelectedIndex = 0;
        root.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        if (!audioSource.IsEnabled || !audioCodec.IsEnabled)
            throw new InvalidOperationException("System audio must enable source and codec settings");
        ((Button)window.FindName("SaveStepButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        if (!((Expander)window.FindName("SaveExpander")).IsExpanded || ((Expander)window.FindName("QualityExpander")).IsExpanded) throw new InvalidOperationException("Save navigation failed");
        Render("save", 976, 994);
        ((Button)window.FindName("CaptureStepButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        vm.SelectedCaptureMode = vm.CaptureModes[2];
        Render("region", 976, 994);
        Console.WriteLine("WPF render and three-step navigation passed.");
        app.Shutdown();
    }
}