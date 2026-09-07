using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace GamenTrail.App.Services;

public sealed class UserDialogService : IUserDialogService
{
    public string? SelectFolder(string? initialDirectory)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "録画の保存先を選択",
            InitialDirectory = Directory.Exists(initialDirectory) ? initialDirectory : null,
        };
        return dialog.ShowDialog(Application.Current.MainWindow) == true
            ? dialog.FolderName
            : null;
    }

    public void ShowError(string title, string message) => MessageBox.Show(
        Application.Current.MainWindow,
        message,
        title,
        MessageBoxButton.OK,
        MessageBoxImage.Error);
}
