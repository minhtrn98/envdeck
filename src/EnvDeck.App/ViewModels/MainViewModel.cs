using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EnvDeck.Core;

namespace EnvDeck.App.ViewModels;

public static class Tabs
{
    public const int Profiles = 0, Environment = 1, Backups = 2, Compare = 3, Search = 4, Settings = 5;
}

/// <summary>Values for the combo boxes in the variable grid.</summary>
public static class Options
{
    public static EnvScope[] Scopes { get; } = Enum.GetValues<EnvScope>();

    public static VarOp[] Ops { get; } = Enum.GetValues<VarOp>();

    public static string[] Kinds { get; } = ["Auto", nameof(ValueKind.String), nameof(ValueKind.ExpandString)];

    public static string[] ScopeFilters { get; } = ["All", nameof(EnvScope.User), nameof(EnvScope.Machine)];
}

public partial class MainViewModel : ObservableObject
{
    public MainViewModel()
    {
        Service = DeckService.CreateDefault();
        Profiles = new ProfilesViewModel(this);
        CurrentEnv = new EnvironmentViewModel(this);
        Backups = new BackupsViewModel(this);
        Compare = new CompareViewModel(this);
        Search = new SearchViewModel(this);
        Settings = new SettingsViewModel(this);
        ReloadAll();
        if (Service.ExternalEdits.Count > 0)
            Status = $"Hand edits saved as new versions: {string.Join(", ", Service.ExternalEdits)}";
    }

    public DeckService Service { get; private set; }

    public ProfilesViewModel Profiles { get; }

    public EnvironmentViewModel CurrentEnv { get; }

    public BackupsViewModel Backups { get; }

    public CompareViewModel Compare { get; }

    public SearchViewModel Search { get; }

    public SettingsViewModel Settings { get; }

    [ObservableProperty]
    public partial string Status { get; set; } = "Ready";

    [ObservableProperty]
    public partial int SelectedTab { get; set; }

    public bool IsElevated { get; } = RegistryEnvStore.IsElevated;

    public bool CanElevate => !IsElevated;

    public string Title =>
        $"EnvDeck {DeckService.ToolVersion}{(IsElevated ? " — Administrator" : "")}{(Service.Env.IsSandbox ? " — SANDBOX" : "")}";

    /// <summary>Recreates the service, e.g. after the data folder changed.</summary>
    public void ReloadService()
    {
        Service = DeckService.CreateDefault();
        OnPropertyChanged(nameof(Title));
        ReloadAll();
    }

    public void ReloadAll()
    {
        Profiles.Reload();
        CurrentEnv.Refresh();
        Backups.Refresh();
        Compare.RefreshSources();
        Settings.Load();
    }

    /// <summary>Called when the window gets focus: picks up CLI changes and JSON edited in another editor.</summary>
    public void OnActivated()
    {
        Run(() =>
        {
            var synced = Service.Profiles.SyncExternalEdits();
            if (synced.Count > 0)
            {
                Profiles.Reload();
                Status = $"Hand edits saved as new versions: {string.Join(", ", synced)}";
            }
            else
            {
                Profiles.RefreshActive();
            }
        });
    }

    /// <summary>Runs an operation and reports expected failures in a dialog instead of crashing.</summary>
    public bool Run(Action action, string? success = null)
    {
        try
        {
            action();
            if (success is not null) Status = success;
            return true;
        }
        catch (ElevationRequiredException ex)
        {
            Status = ex.Message;
            if (Dialogs.Confirm($"{ex.Message}\n\nRestart EnvDeck as administrator?")) RestartElevated();
            return false;
        }
        catch (Exception ex) when (ex is DeckException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Status = ex.Message;
            Dialogs.Error(ex.Message);
            return false;
        }
    }

    [RelayCommand]
    void RestartElevated()
    {
        var path = Environment.ProcessPath!;
        // During development the app runs as "dotnet EnvDeck.App.dll"; pass the dll on.
        var args = Path.GetFileNameWithoutExtension(path).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? $"\"{typeof(MainViewModel).Assembly.Location}\""
            : "";
        try
        {
            Process.Start(new ProcessStartInfo(path, args) { UseShellExecute = true, Verb = "runas" });
            Application.Current.Shutdown();
        }
        catch (Win32Exception)
        {
            Status = "Elevation was cancelled.";
        }
    }

    [RelayCommand]
    void OpenDataFolder() => Shell.Open(Service.Paths.DataDir);
}

static class Shell
{
    public static void Open(string path)
    {
        if (Directory.Exists(path) || File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
}
