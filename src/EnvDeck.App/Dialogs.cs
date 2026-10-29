using System.Windows;
using Microsoft.Win32;

namespace EnvDeck.App;

static class Dialogs
{
    const string Title = "EnvDeck";

    static Window? Owner => Application.Current?.MainWindow is { IsLoaded: true } w ? w : null;

    public static bool Confirm(string message) =>
        Show(message, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public static void Info(string message) => Show(message, MessageBoxButton.OK, MessageBoxImage.Information);

    public static void Error(string message) => Show(message, MessageBoxButton.OK, MessageBoxImage.Warning);

    public static string? Prompt(string title, string label, string initial = "", IEnumerable<string>? suggestions = null)
    {
        var dialog = new PromptWindow(title, label, initial, suggestions ?? []) { Owner = Owner };
        return dialog.ShowDialog() == true ? dialog.Value : null;
    }

    public static string? OpenFile(string filter)
    {
        var dialog = new OpenFileDialog { Filter = filter };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public static string? SaveFile(string filter, string fileName)
    {
        var dialog = new SaveFileDialog { Filter = filter, FileName = fileName };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public static string? PickFolder(string initial)
    {
        var dialog = new OpenFolderDialog { InitialDirectory = initial };
        return dialog.ShowDialog(Owner) == true ? dialog.FolderName : null;
    }

    static MessageBoxResult Show(string message, MessageBoxButton buttons, MessageBoxImage image) =>
        Owner is { } owner
            ? MessageBox.Show(owner, message, Title, buttons, image)
            : MessageBox.Show(message, Title, buttons, image);
}
