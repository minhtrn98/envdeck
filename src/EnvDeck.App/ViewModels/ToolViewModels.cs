using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EnvDeck.Core;

namespace EnvDeck.App.ViewModels;

// ------------------------------------------------------------------ current environment

public sealed record EnvRow(EnvScope Scope, string Name, string Value, ValueKind Kind);

public sealed record EntryRow(string Entry, string Expanded, bool Exists);

public partial class EnvironmentViewModel : ObservableObject
{
    readonly MainViewModel _main;

    public EnvironmentViewModel(MainViewModel main)
    {
        _main = main;
        View = CollectionViewSource.GetDefaultView(Rows);
        View.Filter = o => o is EnvRow r
                           && (ScopeFilter == "All" || r.Scope.ToString() == ScopeFilter)
                           && (string.IsNullOrWhiteSpace(Filter)
                               || r.Name.Contains(Filter.Trim(), StringComparison.OrdinalIgnoreCase)
                               || r.Value.Contains(Filter.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public ObservableCollection<EnvRow> Rows { get; } = [];

    public ICollectionView View { get; }

    /// <summary>Entries of the selected variable when it is a list (PATH...), with a check that each folder exists.</summary>
    public ObservableCollection<EntryRow> Entries { get; } = [];

    [ObservableProperty]
    public partial string Filter { get; set; } = "";

    [ObservableProperty]
    public partial string ScopeFilter { get; set; } = "All";

    [ObservableProperty]
    public partial EnvRow? Selected { get; set; }

    partial void OnFilterChanged(string value) => View.Refresh();

    partial void OnScopeFilterChanged(string value) => View.Refresh();

    partial void OnSelectedChanged(EnvRow? value)
    {
        Entries.Clear();
        if (value is null) return;
        var entries = PathList.Split(value.Value);
        if (entries.Count < 2) return;
        foreach (var e in entries)
        {
            var expanded = Environment.ExpandEnvironmentVariables(e).Trim('"');
            Entries.Add(new EntryRow(e, expanded, Directory.Exists(expanded) || File.Exists(expanded)));
        }
    }

    [RelayCommand]
    public void Refresh()
    {
        var keep = Selected;
        Rows.Clear();
        _main.Run(() =>
        {
            foreach (var scope in Options.Scopes)
            foreach (var v in _main.Service.Env.GetAll(scope))
                Rows.Add(new EnvRow(scope, v.Name, v.Value, v.Kind));
        });
        Selected = keep is null ? null : Rows.FirstOrDefault(r => r.Scope == keep.Scope && r.Name == keep.Name);
    }

    [RelayCommand]
    void Capture(IList? selected)
    {
        var rows = selected?.OfType<EnvRow>().ToList() ?? [];
        if (rows.Count == 0)
        {
            Dialogs.Info("Nothing selected", "Select one or more variables to capture (Ctrl+click for several).");
            return;
        }
        var target = Dialogs.Prompt("Capture into profile",
            $"Copy {rows.Count} variable(s) into profile (new or existing):",
            _main.Profiles.Selected?.Name ?? "",
            _main.Profiles.Items.Select(p => p.Name));
        if (target is null) return;
        var answer = Dialogs.Ask("Make paths portable?",
            "C:\\Users\\you\\... becomes %USERPROFILE%\\..., so the profile also works on another machine or account.",
            "Make portable", "Keep as is");
        if (answer == DialogChoice.Cancel) return;
        var portable = answer == DialogChoice.Primary;

        _main.Run(() =>
        {
            var (profile, warnings) = _main.Service.Capture(target, rows.Select(r => (r.Scope, r.Name)), portable);
            _main.Status = $"Captured {rows.Count - warnings.Count} variable(s) into '{profile.Name}' (v{profile.Version}).";
            _main.Profiles.Reload(profile.Name);
            _main.SelectedTab = Tabs.Profiles;
        });
    }

    [RelayCommand]
    void CopyValue()
    {
        if (Selected is null) return;
        Clipboard.SetText(Selected.Value);
        _main.Status = $"Copied the value of {Selected.Name}.";
    }

    [RelayCommand]
    void Backup() => _main.Backups.CreateCommand.Execute(null);
}

// ------------------------------------------------------------------ backups

public sealed class BackupRow(EnvSnapshot s)
{
    public string Id { get; } = s.Id;

    public string Created { get; } = s.CreatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");

    public string Type { get; } = s.Auto ? "auto" : "manual";

    public string Description { get; } = s.Note ?? s.Reason ?? "";

    public string Host { get; } = s.MachineName ?? "";

    public string UserCount { get; } = s.UserVariables?.Count.ToString() ?? "–";

    public string MachineCount { get; } = s.MachineVariables?.Count.ToString() ?? "–";

    public string ActiveProfiles { get; } = string.Join(", ", s.Applied?.Select(a => a.Profile) ?? []);
}

public partial class BackupsViewModel(MainViewModel main) : ObservableObject
{
    public ObservableCollection<BackupRow> Items { get; } = [];

    [ObservableProperty]
    public partial BackupRow? Selected { get; set; }

    [ObservableProperty]
    public partial bool RestoreUser { get; set; } = true;

    [ObservableProperty]
    public partial bool RestoreMachine { get; set; }

    [ObservableProperty]
    public partial bool Prune { get; set; }

    [RelayCommand]
    public void Refresh()
    {
        Items.Clear();
        main.Run(() =>
        {
            foreach (var s in main.Service.Backups.List()) Items.Add(new BackupRow(s));
        });
    }

    [RelayCommand]
    void Create()
    {
        var note = Dialogs.Prompt("Backup", "Note (optional), e.g. \"before reinstalling Windows\":");
        if (note is null) return;
        main.Run(() =>
        {
            var s = main.Service.Backup(Options.Scopes, note);
            main.Status = $"Backup {s.Id}: {s.UserVariables?.Count} user and {s.MachineVariables?.Count} machine variables.";
        });
        Refresh();
        main.Compare.RefreshSources();
    }

    [RelayCommand]
    void Restore()
    {
        if (Selected is null) return;
        var id = Selected.Id;
        var scopes = new List<EnvScope>();
        if (RestoreUser) scopes.Add(EnvScope.User);
        if (RestoreMachine) scopes.Add(EnvScope.Machine);
        if (scopes.Count == 0)
        {
            Dialogs.Info("Nothing to restore", "Tick User and/or Machine to choose what to restore.");
            return;
        }

        main.Run(() =>
        {
            var preview = main.Service.Restore(id, scopes, Prune, dryRun: true);
            if (preview.Changes.Count == 0)
            {
                main.Status = $"The environment already matches backup {id}.";
                return;
            }
            var summary = string.Join("\n", preview.Changes.Take(15).Select(c =>
                $"{(c.Action == ChangeAction.Delete ? "−" : c.Action == ChangeAction.Create ? "+" : "~")} {c.Scope}\\{c.Name}"));
            if (preview.Changes.Count > 15) summary += $"\n… and {preview.Changes.Count - 15} more";
            if (!Dialogs.Confirm($"Restore {preview.Changes.Count} variable(s) from backup {id}?",
                    $"{summary}\n\nA backup of the current state is taken first, so this can be undone.", "Restore"))
                return;

            var result = main.Service.Restore(id, scopes, Prune);
            main.Status = $"Restored {result.Changes.Count} variable(s) from {id}. {string.Join(" ", result.Warnings)}";
        });
        Refresh();
        main.CurrentEnv.Refresh();
        main.Profiles.RefreshActive();
    }

    [RelayCommand]
    void Delete()
    {
        if (Selected is null) return;
        var id = Selected.Id;
        if (!Dialogs.ConfirmDanger($"Delete backup {id}?", "The backup file is removed for good.", "Delete")) return;
        main.Run(() => main.Service.Backups.Delete(id), $"Deleted backup {id}.");
        Refresh();
    }

    [RelayCommand]
    void CompareWithCurrent()
    {
        if (Selected is null) return;
        main.Compare.Run($"backup:{Selected.Id}", "env");
        main.SelectedTab = Tabs.Compare;
    }

    [RelayCommand]
    void OpenFolder() => Shell.Open(main.Service.Paths.BackupsDir);
}

// ------------------------------------------------------------------ compare

public sealed class DiffRowViewModel(DiffRow row)
{
    public DiffKind Kind { get; } = row.Kind;

    public string Symbol { get; } = row.Kind switch
    {
        DiffKind.Added => "+",
        DiffKind.Removed => "−",
        DiffKind.Changed => "~",
        _ => "=",
    };

    public string Variable { get; } = $"{row.Scope}\\{row.Name}";

    public string Left { get; } = row.Left ?? "";

    public string Right { get; } = row.Right ?? "";

    public string Details { get; } = string.Join("   ",
        row.OnlyLeft.Select(e => "− " + e).Concat(row.OnlyRight.Select(e => "+ " + e)));
}

public partial class CompareViewModel(MainViewModel main) : ObservableObject
{
    public ObservableCollection<string> Sources { get; } = [];

    public ObservableCollection<DiffRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    public partial string Left { get; set; } = "";

    [ObservableProperty]
    public partial string Right { get; set; } = "env:user";

    [ObservableProperty]
    public partial bool ShowSame { get; set; }

    [ObservableProperty]
    public partial string Summary { get; set; } = "Pick two sources: a profile, profile@version, env, env:user, env:machine or backup:<id>.";

    public void RefreshSources()
    {
        Sources.Clear();
        main.Run(() =>
        {
            foreach (var s in main.Service.SourceSuggestions()) Sources.Add(s);
        });
    }

    public void Run(string left, string right)
    {
        Left = left;
        Right = right;
        Run();
    }

    [RelayCommand]
    void Run()
    {
        if (string.IsNullOrWhiteSpace(Left) || string.IsNullOrWhiteSpace(Right)) return;
        Rows.Clear();
        main.Run(() =>
        {
            var (l, r, rows) = main.Service.Diff(Left, Right, ShowSame);
            foreach (var row in rows) Rows.Add(new DiffRowViewModel(row));
            Summary = $"{l.Label}  →  {r.Label}:  " +
                      $"{rows.Count(x => x.Kind == DiffKind.Added)} added, " +
                      $"{rows.Count(x => x.Kind == DiffKind.Removed)} removed, " +
                      $"{rows.Count(x => x.Kind == DiffKind.Changed)} changed";
        });
    }

    [RelayCommand]
    void Swap()
    {
        (Left, Right) = (Right, Left);
        Run();
    }
}

// ------------------------------------------------------------------ search

public sealed class SearchHitRow(SearchHit hit)
{
    public SearchHit Hit { get; } = hit;

    public string Source { get; } = hit.Source;

    public string Variable { get; } = $"{hit.Scope}\\{hit.Name}";

    public string Value { get; } = (hit.Op is { } op && op != VarOp.Set ? $"[{op}] " : "") + (hit.Value ?? "");
}

public partial class SearchViewModel(MainViewModel main) : ObservableObject
{
    public ObservableCollection<SearchHitRow> Results { get; } = [];

    [ObservableProperty]
    public partial string Query { get; set; } = "";

    [ObservableProperty]
    public partial bool UseRegex { get; set; }

    [ObservableProperty]
    public partial bool InNames { get; set; } = true;

    [ObservableProperty]
    public partial bool InValues { get; set; } = true;

    [ObservableProperty]
    public partial bool InProfiles { get; set; } = true;

    [ObservableProperty]
    public partial bool InEnvironment { get; set; } = true;

    [ObservableProperty]
    public partial bool InBackups { get; set; }

    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    [RelayCommand]
    void Run()
    {
        if (string.IsNullOrWhiteSpace(Query)) return;
        Results.Clear();
        main.Run(() =>
        {
            var hits = main.Service.Search(Query.Trim(), new SearchOptions
            {
                Regex = UseRegex,
                Names = InNames,
                Values = InValues,
                Profiles = InProfiles,
                Environment = InEnvironment,
                Backups = InBackups,
            });
            foreach (var h in hits) Results.Add(new SearchHitRow(h));
            Summary = $"{hits.Count} match(es)";
        });
    }

    [RelayCommand]
    void Open(SearchHitRow? row)
    {
        if (row is null) return;
        if (row.Source.StartsWith("profile:", StringComparison.Ordinal))
        {
            main.Profiles.Reload(row.Source["profile:".Length..]);
            main.SelectedTab = Tabs.Profiles;
        }
        else if (row.Source.StartsWith("env:", StringComparison.Ordinal))
        {
            main.CurrentEnv.Filter = row.Hit.Name;
            main.SelectedTab = Tabs.Environment;
        }
        else if (row.Source.StartsWith("backup:", StringComparison.Ordinal))
        {
            main.Backups.Selected = main.Backups.Items.FirstOrDefault(b => b.Id == row.Source["backup:".Length..]);
            main.SelectedTab = Tabs.Backups;
        }
    }
}

// ------------------------------------------------------------------ settings & tools

public partial class SettingsViewModel(MainViewModel main) : ObservableObject
{
    public ObservableCollection<DoctorIssue> Issues { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string DataDir { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial int HistoryLimit { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool AutoBackup { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial int AutoBackupLimit { get; set; }

    [ObservableProperty]
    public partial string Info { get; set; } = "";

    public void Load()
    {
        var s = main.Service.Settings;
        DataDir = s.DataDir ?? "";
        HistoryLimit = s.HistoryLimit;
        AutoBackup = s.AutoBackup;
        AutoBackupLimit = s.AutoBackupLimit;
        var p = main.Service.Paths;
        Info = $"Data folder in use: {p.DataDir}\nMachine state: {p.StateDir}\n" +
               $"Version {DeckService.ToolVersion}{(main.Service.Env.IsSandbox ? " · sandbox mode" : "")}" +
               $"{(main.IsElevated ? " · running as administrator" : "")}";
        SaveCommand.NotifyCanExecuteChanged();
    }

    string? NewDataDir => string.IsNullOrWhiteSpace(DataDir) ? null : DataDir.Trim();

    /// <summary>Save settings is enabled only when something differs from what is stored.</summary>
    bool HasChanges()
    {
        var s = main.Service.Settings;
        return !string.Equals(NewDataDir, s.DataDir, StringComparison.OrdinalIgnoreCase)
               || Math.Max(1, HistoryLimit) != s.HistoryLimit
               || AutoBackup != s.AutoBackup
               || Math.Max(1, AutoBackupLimit) != s.AutoBackupLimit;
    }

    [RelayCommand(CanExecute = nameof(HasChanges))]
    void Save()
    {
        var s = main.Service.Settings;
        var newDir = NewDataDir;
        var dirChanged = !string.Equals(newDir, s.DataDir, StringComparison.OrdinalIgnoreCase);
        s.DataDir = newDir;
        s.HistoryLimit = Math.Max(1, HistoryLimit);
        s.AutoBackup = AutoBackup;
        s.AutoBackupLimit = Math.Max(1, AutoBackupLimit);
        var saved = main.Run(main.Service.SaveSettings, "Settings saved.");
        SaveCommand.NotifyCanExecuteChanged();
        if (!saved) return;
        if (dirChanged)
        {
            main.ReloadService();
            main.Status = "Data folder changed. Existing profiles were not moved; use Export/Import or copy the folder.";
        }
    }

    [RelayCommand]
    void Browse()
    {
        if (Dialogs.PickFolder(string.IsNullOrEmpty(DataDir) ? main.Service.Paths.DataDir : DataDir) is { } dir) DataDir = dir;
    }

    [RelayCommand]
    void Export()
    {
        var file = Dialogs.SaveFile("EnvDeck bundle (*.zip)|*.zip", $"envdeck-{Environment.MachineName}-{DateTime.Now:yyyyMMdd}.zip");
        if (file is null) return;
        var answer = Dialogs.Ask("Include backups too?",
            "Useful before reinstalling Windows. Profile history is always included.", "Include backups", "Profiles only");
        if (answer == DialogChoice.Cancel) return;
        var withBackups = answer == DialogChoice.Primary;
        main.Run(() =>
        {
            var manifest = main.Service.Export(file, null, withHistory: true, withBackups);
            main.Status = $"Exported {manifest.Profiles.Count} profile(s) to {file}.";
        });
    }

    [RelayCommand]
    void Import()
    {
        var file = Dialogs.OpenFile("EnvDeck bundle or profile (*.zip;*.json)|*.zip;*.json");
        if (file is null) return;
        main.Run(() =>
        {
            var result = main.Service.Import(file, overwrite: false);
            if (result.Skipped.Count > 0
                && Dialogs.Confirm($"{result.Skipped.Count} profile(s) already exist",
                    $"{string.Join(", ", result.Skipped)}\n\nOverwrite them? Each is saved as a new version, so its history is kept.",
                    "Overwrite", "Skip them"))
                result = main.Service.Import(file, overwrite: true);
            main.Status = $"Imported {result.Imported.Count} profile(s) and {result.Backups.Count} backup(s).";
        });
        main.ReloadAll();
    }

    [RelayCommand]
    void RunDoctor()
    {
        Issues.Clear();
        main.Run(() =>
        {
            foreach (var issue in main.Service.Doctor()) Issues.Add(issue);
            main.Status = Issues.Count == 0 ? "Doctor: everything looks fine." : $"Doctor: {Issues.Count} finding(s).";
        });
    }
}
