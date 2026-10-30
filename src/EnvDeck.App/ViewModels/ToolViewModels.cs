using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EnvDeck.App.Localization;
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
    void CopyValue()
    {
        if (Selected is null) return;
        Clipboard.SetText(Selected.Value);
        _main.Status = L.T("CopiedValue", Selected.Name);
    }
}

// ------------------------------------------------------------------ backups

public sealed class BackupRow(EnvSnapshot s)
{
    public string Id { get; } = s.Id;

    public string Created { get; } = s.CreatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");

    public string Type { get; } = L.T(s.Auto ? "BackupAuto" : "BackupManual");

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
        var note = Dialogs.Prompt(L.T("BackupTitle"), L.T("BackupNoteLabel"));
        if (note is null) return;
        main.Run(() =>
        {
            var s = main.Service.Backup(Options.Scopes, note);
            main.Status = L.T("BackupCreated", s.Id, s.UserVariables?.Count, s.MachineVariables?.Count);
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
            Dialogs.Info(L.T("NothingToRestoreHeading"), L.T("NothingToRestoreBody"));
            return;
        }

        main.Run(() =>
        {
            var preview = main.Service.Restore(id, scopes, Prune, dryRun: true);
            if (preview.Changes.Count == 0)
            {
                main.Status = L.T("AlreadyMatchesBackup", id);
                return;
            }
            var summary = string.Join("\n", preview.Changes.Take(15).Select(c =>
                $"{(c.Action == ChangeAction.Delete ? "−" : c.Action == ChangeAction.Create ? "+" : "~")} {c.Scope}\\{c.Name}"));
            if (preview.Changes.Count > 15) summary += "\n" + L.T("AndMore", preview.Changes.Count - 15);
            if (!Dialogs.Confirm(L.T("RestoreHeading", preview.Changes.Count, id), L.T("RestoreBody", summary), L.T("Restore")))
                return;

            var result = main.Service.Restore(id, scopes, Prune);
            main.Status = $"{L.T("Restored", result.Changes.Count, id)} {string.Join(" ", result.Warnings)}";
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
        if (!Dialogs.ConfirmDanger(L.T("DeleteBackupHeading", id), L.T("DeleteBackupBody"), L.T("Delete"))) return;
        main.Run(() => main.Service.Backups.Delete(id), L.T("DeletedBackup", id));
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
    public partial string Summary { get; set; } = L.T("CompareIntro");

    /// <summary>After a language change: rebuild the summary line.</summary>
    public void RefreshTexts()
    {
        if (Rows.Count > 0) Run();
        else Summary = L.T("CompareIntro");
    }

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
            Summary = L.T("CompareSummary", l.Label, r.Label, rows.Count(x => x.Kind == DiffKind.Added),
                rows.Count(x => x.Kind == DiffKind.Removed), rows.Count(x => x.Kind == DiffKind.Changed));
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

    public void RefreshTexts() => Summary = Results.Count > 0 ? L.T("SearchSummary", Results.Count) : "";

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
            Summary = L.T("SearchSummary", hits.Count);
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

public sealed class IssueRow(DoctorIssue issue)
{
    public string Severity { get; } = L.T("Severity" + issue.Severity);

    public string Message { get; } = issue.Message;
}

public partial class SettingsViewModel(MainViewModel main) : ObservableObject
{
    List<DoctorIssue> _issues = [];

    public ObservableCollection<IssueRow> Issues { get; } = [];

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

    /// <summary>UI language; applied and stored right away (it is not part of "Save settings").</summary>
    [ObservableProperty]
    public partial string Language { get; set; } = Loc.Instance.Language;

    partial void OnLanguageChanged(string value)
    {
        if (value == Loc.Instance.Language) return;
        main.Service.Settings.Language = value;
        main.Run(main.Service.SaveSettings);
        Loc.Instance.SetLanguage(value);
    }

    /// <summary>After a language change: rebuild text made in code (the info block and doctor labels).</summary>
    public void RefreshTexts()
    {
        UpdateInfo();
        Issues.Clear();
        foreach (var issue in _issues) Issues.Add(new IssueRow(issue));
    }

    void UpdateInfo()
    {
        var p = main.Service.Paths;
        Info = L.T("SettingsInfo", p.DataDir, p.StateDir, DeckService.ToolVersion)
               + (main.Service.Env.IsSandbox ? " · " + L.T("SandboxMode") : "")
               + (main.IsElevated ? " · " + L.T("RunningAsAdmin") : "");
    }

    public void Load()
    {
        Language = Loc.Instance.Language;
        var s = main.Service.Settings;
        DataDir = s.DataDir ?? "";
        HistoryLimit = s.HistoryLimit;
        AutoBackup = s.AutoBackup;
        AutoBackupLimit = s.AutoBackupLimit;
        UpdateInfo();
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
        var saved = main.Run(main.Service.SaveSettings, L.T("SettingsSaved"));
        SaveCommand.NotifyCanExecuteChanged();
        if (!saved) return;
        if (dirChanged)
        {
            main.ReloadService();
            main.Status = L.T("DataDirChanged");
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
        var file = Dialogs.SaveFile(L.T("ExportFilter"), $"envdeck-{Environment.MachineName}-{DateTime.Now:yyyyMMdd}.zip");
        if (file is null) return;
        var answer = Dialogs.Ask(L.T("ExportBackupsHeading"), L.T("ExportBackupsBody"), L.T("IncludeBackups"), L.T("ProfilesOnly"));
        if (answer == DialogChoice.Cancel) return;
        var withBackups = answer == DialogChoice.Primary;
        main.Run(() =>
        {
            var manifest = main.Service.Export(file, null, withHistory: true, withBackups);
            main.Status = L.T("Exported", manifest.Profiles.Count, file);
        });
    }

    [RelayCommand]
    void Import()
    {
        var file = Dialogs.OpenFile(L.T("ImportFilter"));
        if (file is null) return;
        main.Run(() =>
        {
            var result = main.Service.Import(file, overwrite: false);
            if (result.Skipped.Count > 0
                && Dialogs.Confirm(L.T("ExistingProfilesHeading", result.Skipped.Count),
                    L.T("ExistingProfilesBody", string.Join(", ", result.Skipped)), L.T("Overwrite"), L.T("SkipThem")))
                result = main.Service.Import(file, overwrite: true);
            main.Status = L.T("Imported", result.Imported.Count, result.Backups.Count);
        });
        main.ReloadAll();
    }

    [RelayCommand]
    void RunDoctor()
    {
        Issues.Clear();
        main.Run(() =>
        {
            _issues = main.Service.Doctor();
            foreach (var issue in _issues) Issues.Add(new IssueRow(issue));
            main.Status = Issues.Count == 0 ? L.T("DoctorFine") : L.T("DoctorFindings", Issues.Count);
        });
    }
}
