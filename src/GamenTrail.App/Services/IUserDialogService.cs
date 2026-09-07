namespace GamenTrail.App.Services;

public interface IUserDialogService
{
    string? SelectFolder(string? initialDirectory);

    void ShowError(string title, string message);
}
