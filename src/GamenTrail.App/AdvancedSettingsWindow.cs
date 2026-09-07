using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace GamenTrail.App;

public sealed class AdvancedSettingsWindow : Window
{
    private readonly TextBox _lame = new();
    private readonly TextBox _flac = new();
    public string LamePath => _lame.Text.Trim().Trim('"');
    public string FlacPath => _flac.Text.Trim().Trim('"');

    public AdvancedSettingsWindow(Window owner, string lamePath, string flacPath)
    {
        Owner = owner;
        SettingsWindowTheme.Apply(this, owner);
        Title = "詳細設定";
        Width = 650;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(24) };
        Content = panel;
        panel.Children.Add(new TextBlock { Text = "外部音声エンコーダー", FontSize = 20, Margin = new Thickness(0, 0, 0, 10) });
        panel.Children.Add(new TextBlock { Text = "実行ファイルの場所は保存され、次回起動時も引き継ぎます。", Margin = new Thickness(0, 0, 0, 16) });
        _lame.Text = lamePath;
        _flac.Text = flacPath;
        AddPath(panel, "LAME（lame.exe）", _lame, "lame.exe");
        AddPath(panel, "FLAC（flac.exe）", _flac, "flac.exe");
        panel.Children.Add(new TextBlock { Text = "空欄にすると、そのエンコーダーの登録を解除します。", Margin = new Thickness(0, 8, 0, 16) });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var save = new Button { Content = "保存", IsDefault = true, MinWidth = 90, Padding = new Thickness(12, 6, 12, 6) };
        save.SetResourceReference(StyleProperty, "PrimaryButton");
        save.Click += (_, _) =>
        {
            try
            {
                ValidatePath(LamePath);
                ValidatePath(FlacPath);
                DialogResult = true;
            }
            catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
            { MessageBox.Show(this, error.Message, "実行ファイルを確認してください", MessageBoxButton.OK, MessageBoxImage.Warning); }
        };
        buttons.Children.Add(save);
        buttons.Children.Add(new Button { Content = "キャンセル", IsCancel = true, MinWidth = 90, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 6, 12, 6) });
        panel.Children.Add(buttons);
    }

    internal static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path) || !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("存在する .exe ファイルをフルパスで指定してください。");
    }

    private void AddPath(Panel panel, string label, TextBox field, string fileName)
    {
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 8, 0, 6) });
        var row = new DockPanel();
        var browse = new Button { Content = "参照...", MinWidth = 80, Margin = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(browse, Dock.Right);
        browse.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog { Title = label, Filter = "実行ファイル (*.exe)|*.exe", FileName = fileName, CheckFileExists = true };
            if (dialog.ShowDialog(this) == true) field.Text = dialog.FileName;
        };
        row.Children.Add(browse);
        field.Padding = new Thickness(6);
        row.Children.Add(field);
        panel.Children.Add(row);
    }
}

public sealed class AudioEncoderOptionsWindow : Window
{
    private readonly ComboBox _value = new();
    public int Value => (int)_value.SelectedItem;

    public AudioEncoderOptionsWindow(Window owner, bool mp3, int value)
    {
        Owner = owner;
        SettingsWindowTheme.Apply(this, owner);
        Title = mp3 ? "MP3（LAME）の設定" : "FLACの設定";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(24) };
        Content = panel;
        panel.Children.Add(new TextBlock { Text = mp3 ? "ビットレート（kbps・固定）" : "圧縮レベル（0: 速い ～ 8: 小さい）" });
        _value.ItemsSource = mp3 ? new[] { 96, 128, 160, 192, 224, 256, 320 } : Enumerable.Range(0, 9).ToArray();
        _value.SelectedItem = value;
        if (_value.SelectedIndex < 0) _value.SelectedItem = mp3 ? 192 : 5;
        _value.Margin = new Thickness(0, 8, 0, 16);
        panel.Children.Add(_value);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var save = new Button { Content = "保存", IsDefault = true, MinWidth = 85, Padding = new Thickness(10, 6, 10, 6) };
        save.SetResourceReference(StyleProperty, "PrimaryButton");
        save.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(save);
        buttons.Children.Add(new Button { Content = "キャンセル", IsCancel = true, MinWidth = 85, Margin = new Thickness(8, 0, 0, 0) });
        panel.Children.Add(buttons);
    }
}
internal static class SettingsWindowTheme
{
    public static void Apply(Window window, Window owner)
    {
        // Reuse the main window's brushes and control templates, including popup and focus states.
        window.Resources.MergedDictionaries.Add(owner.Resources);
        window.Background = owner.Background;
        window.Foreground = owner.Foreground;
        window.FontFamily = owner.FontFamily;
        window.FontSize = owner.FontSize;
        window.UseLayoutRounding = owner.UseLayoutRounding;
    }
}