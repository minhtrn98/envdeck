using System.Windows;
using Microsoft.Win32;

namespace EnvDeck.App;

/// <summary>All app dialogs go through here, so they share the Windows 11 look of <see cref="FluentDialog"/>.</summary>
static class Dialogs
{
    /// <summary>The window the user is looking at (a dialog may open another one), else the main window.</summary>
    static Window? Owner =>
        Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsLoaded)
        ?? (Application.Current?.MainWindow is { IsLoaded: true } main ? main : null);

    public static bool Confirm(string heading, string? body = null, string confirm = "OK", string cancel = "Cancel") =>
        Show(DialogKind.Question, heading, body, confirm, null, cancel) == DialogChoice.Primary;

    /// <summary>For destructive actions: red button, and Enter picks Cancel.</summary>
    public static bool ConfirmDanger(string heading, string? body, string confirm) =>
        Show(DialogKind.Danger, heading, body, confirm, null, "Cancel") == DialogChoice.Primary;

    /// <summary>Three-way question, e.g. Save / Don't save / Cancel.</summary>
    public static DialogChoice Ask(string heading, string? body, string primary, string secondary, string cancel = "Cancel") =>
        Show(DialogKind.Question, heading, body, primary, secondary, cancel);

    public static void Info(string heading, string? body = null) => Show(DialogKind.Info, heading, body, "OK", null, null);

    public static void Error(string body, string heading = "That didn't work") => Show(DialogKind.Error, heading, body, "OK", null, null);

    public static DialogChoice Show(DialogKind kind, string heading, string? body, string primary, string? secondary, string? cancel)
    {
        var dialog = new FluentDialog(kind, heading, body, primary, secondary, cancel);
        if (Owner is { } owner) dialog.Owner = owner;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.ShowDialog();
        return dialog.Choice;
    }

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
}
