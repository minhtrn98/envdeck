using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EnvDeck.App.Localization;
using EnvDeck.Core;

namespace EnvDeck.App.ViewModels;

/// <summary>What the details panel under the variables shows.</summary>
public enum DetailsPanel { Preview, History }

public partial class ProfilesViewModel : ObservableObject
{
    readonly MainViewModel _main;
    readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    bool _reloading;

    public ProfilesViewModel(MainViewModel main)
    {
        _main = main;
        _previewTimer.Tick += (_, _) =>
        {
            _previewTimer.Stop();
            RefreshPreview();
        };
    }

    DeckService Service => _main.Service;

    public ObservableCollection<ProfileItemViewModel> Items { get; } = [];

    public ObservableCollection<ProfileVersionInfo> History { get; } = [];

    public ObservableCollection<ChangeRow> Preview { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DuplicateCommand), nameof(RenameCommand), nameof(DeleteCommand))]
    public partial ProfileItemViewModel? Selected { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(DiscardCommand))]
    public partial ProfileEditorViewModel? Editor { get; set; }

    // Save/Discard are enabled only while the editor has unsaved changes; the preview follows every edit.
    partial void OnEditorChanged(ProfileEditorViewModel? oldValue, ProfileEditorViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.PropertyChanged -= OnEditorPropertyChanged;
            oldValue.Changed -= SchedulePreview;
        }
        if (newValue is not null)
        {
            newValue.PropertyChanged += OnEditorPropertyChanged;
            newValue.Changed += SchedulePreview;
        }
    }

    void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ProfileEditorViewModel.IsDirty)) return;
        SaveCommand.NotifyCanExecuteChanged();
        DiscardCommand.NotifyCanExecuteChanged();
    }

    [ObservableProperty]
    public partial VariableViewModel? SelectedVariable { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CompareVersionCommand), nameof(RollbackCommand))]
    public partial ProfileVersionInfo? SelectedVersion { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailsSummary))]
    public partial string PreviewSummary { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPreviewSelected), nameof(IsHistorySelected), nameof(DetailsSummary))]
    public partial DetailsPanel SelectedPanel { get; set; }

    /// <summary>The details panel starts collapsed so the variables get the room.</summary>
    [ObservableProperty]
    public partial bool IsDetailsOpen { get; set; }

    public bool IsPreviewSelected => SelectedPanel == DetailsPanel.Preview;

    public bool IsHistorySelected => SelectedPanel == DetailsPanel.History;

    /// <summary>One line next to the panel switch, visible even while the panel is collapsed.</summary>
    public string DetailsSummary => IsPreviewSelected ? PreviewSummary : L.T("HistoryHint");

    /// <summary>Picking Preview or History also opens the panel (even when that one was already selected).</summary>
    [RelayCommand]
    void ShowDetails(DetailsPanel panel)
    {
        SelectedPanel = panel;
        IsDetailsOpen = true;
    }

    partial void OnSelectedChanged(ProfileItemViewModel? oldValue, ProfileItemViewModel? newValue)
    {
        if (_reloading) return;
        if (Editor is { IsDirty: true } && !SameName(Editor.Name, newValue?.Name)
            && Dialogs.Confirm(L.T("SaveChangesHeading", Editor.Name), L.T("SaveBeforeSwitchBody"), L.T("Save"), L.T("DontSave")))
            SaveEditor(askToReapply: false);
        LoadEditor();
    }

    public void Reload(string? select = null)
    {
        var keep = select ?? Selected?.Name;
        _reloading = true;
        try
        {
            var active = ActiveNames();
            Items.Clear();
            foreach (var p in Service.Profiles.List()) Items.Add(new ProfileItemViewModel(p, active.Contains(p.Name)));
            Selected = Items.FirstOrDefault(i => SameName(i.Name, keep)) ?? Items.FirstOrDefault();
        }
        finally
        {
            _reloading = false;
        }

        if (Service.Profiles.LoadErrors.Count > 0) _main.Status = L.T("CouldNotRead", string.Join("; ", Service.Profiles.LoadErrors));
        // Keep unsaved edits of the selected profile across a refresh.
        if (select is not null || Editor is not { IsDirty: true } || !SameName(Editor.Name, Selected?.Name)) LoadEditor();
        OnPropertyChanged(nameof(DetailsSummary)); // the history hint follows the language
    }

    public void RefreshActive()
    {
        var active = ActiveNames();
        foreach (var item in Items) item.SyncActive(active.Contains(item.Name));
        UpdateApplyState();
        SchedulePreview(); // turning profiles on/off changes what this one would change
    }

    void LoadEditor()
    {
        History.Clear();
        if (Selected is null)
        {
            Editor = null;
            RefreshPreview();
            UpdateApplyState();
            return;
        }
        _main.Run(() =>
        {
            var profile = Service.Profiles.Get(Selected.Name);
            Editor = new ProfileEditorViewModel(profile);
            foreach (var h in Service.Profiles.History(profile.Name)) History.Add(h);
        });
        RefreshPreview();
        UpdateApplyState();
    }

    /// <summary>The version of <paramref name="name"/> that is written to the environment now, if it is on.</summary>
    int? AppliedVersionOf(string name) =>
        Service.State.Load().Applied.FirstOrDefault(a => SameName(a.Profile, name))?.Version;

    /// <summary>True while the profile is on but the environment still has an older saved version of it.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyEditedCommand))]
    public partial bool NeedsApply { get; set; }

    [ObservableProperty]
    public partial string NotAppliedText { get; set; } = "";

    [ObservableProperty]
    public partial string ApplyEditedText { get; set; } = "";

    void UpdateApplyState()
    {
        int? applied = null;
        if (Editor is { } editor && Selected is { IsActive: true }) applied = AppliedVersionOf(editor.Name);
        if (Editor is { } e && applied is { } a && a != e.Version)
        {
            NotAppliedText = L.T("NotApplied", a, e.Version);
            ApplyEditedText = L.T("ApplyVersion", e.Version);
            NeedsApply = true;
        }
        else
        {
            NotAppliedText = ApplyEditedText = "";
            NeedsApply = false;
        }
    }

    /// <summary>Writes the saved version of the edited profile to the environment (it was left for later when saving).</summary>
    [RelayCommand(CanExecute = nameof(NeedsApply))]
    void ApplyEdited()
    {
        if (Editor is not { } editor) return;
        var name = editor.Name;
        try
        {
            if (!SaveIfEditing(name, "ActionApplying") || !ConfirmOverrides(name)) return;
            _main.Run(() =>
            {
                var result = Service.Apply(name);
                _main.Status = L.T("ReappliedStatus", name, Service.Profiles.Get(name).Version, result.Changes.Count)
                               + (result.Warnings.Count > 0 ? $" {string.Join(" ", result.Warnings)}" : "")
                               + " " + L.T("NewTerminalsSee");
            });
            _main.CurrentEnv.RefreshIfClean();
            _main.Backups.Refresh();
        }
        finally
        {
            RefreshActive();
        }
    }

    void SchedulePreview()
    {
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    /// <summary>
    /// What turning the edited profile on would change, unsaved edits included. Runs by itself after edits,
    /// so an invalid intermediate state is shown in the summary instead of a dialog.
    /// </summary>
    void RefreshPreview()
    {
        _previewTimer.Stop();
        Preview.Clear();
        if (Editor is null)
        {
            PreviewSummary = "";
            return;
        }
        try
        {
            var profile = Editor.ToProfile();
            ProfileValidator.ThrowIfInvalid(profile);
            foreach (var c in Service.Plan(profile).Where(c => !c.IsNoOp)) Preview.Add(new ChangeRow(c));
            PreviewSummary = Preview.Count == 0 ? L.T("PreviewNothing") : L.T("PreviewCount", Preview.Count);
        }
        catch (Exception ex) when (ex is DeckException or IOException or UnauthorizedAccessException)
        {
            PreviewSummary = string.Join(" ", ex.Message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
    }

    bool SaveEditor(bool askToReapply)
    {
        if (Editor is null) return false;
        var editor = Editor;
        var saved = _main.Run(() =>
        {
            var (profile, changed) = Service.Profiles.Save(editor.ToProfile());
            editor.MarkSaved(profile.Version);
            Items.FirstOrDefault(i => SameName(i.Name, profile.Name))?.Update(profile);
            _main.Status = changed ? L.T("SavedAsVersion", profile.Name, profile.Version) : L.T("NoChangesToSave");
            if (!changed || !askToReapply || AppliedVersionOf(profile.Name) is not { } applied) return;

            // The profile is on: say clearly that saving and applying are two steps, and that Later leaves the
            // environment as it is (the Apply button stays available).
            if (Dialogs.Confirm(L.T("ApplyNowHeading", profile.Version, profile.Name), L.T("ApplyNowBody", applied),
                    L.T("ApplyNow"), L.T("Later")))
            {
                Service.Apply(profile.Name);
                _main.CurrentEnv.RefreshIfClean();
                _main.Backups.Refresh();
            }
            else
            {
                _main.Status = L.T("SavedNotApplied", profile.Name, profile.Version, applied);
            }
        });
        if (saved && ReferenceEquals(editor, Editor))
        {
            History.Clear();
            foreach (var h in Service.Profiles.History(editor.Name)) History.Add(h);
            RefreshActive();
        }
        return saved;
    }

    [RelayCommand]
    void New()
    {
        var name = Dialogs.Prompt(L.T("NewProfile"), L.T("NewProfileLabel"));
        if (name is null) return;
        if (_main.Run(() =>
            {
                if (Service.Profiles.Exists(name)) throw new DeckException(L.T("ProfileExists", name));
                Service.Profiles.Save(new Profile { Name = name });
            }, L.T("CreatedProfile", name)))
            Reload(name);
    }

    // Duplicate/Rename/Delete act on the given profile, or on the selection (context menu, F2, Ctrl+D, Del).

    bool HasTarget(ProfileItemViewModel? item) => (item ?? Selected) is not null;

    [RelayCommand(CanExecute = nameof(HasTarget))]
    void Duplicate(ProfileItemViewModel? item)
    {
        item ??= Selected;
        if (item is null || !SaveIfEditing(item.Name, "ActionDuplicating")) return;
        var source = item.Name;
        var name = Dialogs.Prompt(L.T("DuplicateTitle"), L.T("DuplicateLabel", source), source + "-copy");
        if (name is null) return;
        if (_main.Run(() =>
            {
                if (Service.Profiles.Exists(name)) throw new DeckException(L.T("ProfileExists", name));
                var copy = Service.Profiles.Get(source).Clone();
                copy.Name = name;
                copy.Version = 0;
                Service.Profiles.Save(copy);
            }, L.T("CreatedFrom", name, source)))
            Reload(name);
    }

    [RelayCommand(CanExecute = nameof(HasTarget))]
    void Rename(ProfileItemViewModel? item)
    {
        item ??= Selected;
        if (item is null || !SaveIfEditing(item.Name, "ActionRenaming")) return;
        var old = item.Name;
        var name = Dialogs.Prompt(L.T("RenameTitle"), L.T("RenameLabel", old), old);
        if (name is null || name == old) return;
        if (_main.Run(() => Service.RenameProfile(old, name), L.T("Renamed", old, name))) Reload(name);
    }

    [RelayCommand(CanExecute = nameof(HasTarget))]
    void Delete(ProfileItemViewModel? item)
    {
        item ??= Selected;
        if (item is null) return;
        var name = item.Name;
        var active = Service.IsActive(name);
        var details = (active ? L.T("DeleteProfileOnNote") + "\n\n" : "") + L.T("DeleteProfileHistoryNote");
        if (!Dialogs.ConfirmDanger(L.T("DeleteProfileHeading", name), details, L.T("Delete"))) return;

        if (_main.Run(() =>
            {
                if (active) Service.Unapply(name);
                Service.DeleteProfile(name, purgeHistory: false);
            }, L.T(active ? "DeletedProfileTurnedOff" : "DeletedProfile", name)))
        {
            Reload();
            if (active) _main.CurrentEnv.RefreshIfClean();
        }
    }

    /// <summary>Warns before turning on a profile that would overwrite values set by other profiles that are on.</summary>
    bool ConfirmOverrides(string name)
    {
        List<ProfileConflict> conflicts = [];
        if (!_main.Run(() => conflicts = Service.FindConflicts(Service.Profiles.Get(name)))) return false;
        if (conflicts.Count == 0) return true;

        var lines = conflicts.Take(8).Select(c => L.T("ConflictLine", $"{c.Scope}\\{c.Name}", c.OtherProfile,
            Short(c.Name, c.OtherValue), Short(c.Name, c.NewValue))).ToList();
        if (conflicts.Count > 8) lines.Add(L.T("AndMore", conflicts.Count - 8));
        return Dialogs.ConfirmWarning(L.T("ConflictHeading", name), L.T("ConflictBody", string.Join("\n", lines), name),
            L.T("TurnOnAnyway"));

        static string Short(string name, string? value) =>
            value is null ? L.T("ValueDeleted")
            : Secrecy.IsSecret(name) ? Secrecy.Mask
            : value.Length <= 48 ? value : value[..47] + "…";
    }

    /// <summary>Unsaved edits of <paramref name="name"/> would be lost or left out; offer to save them first.</summary>
    bool SaveIfEditing(string name, string action)
    {
        if (Editor is not { IsDirty: true } || !SameName(Editor.Name, name)) return true;
        return Dialogs.Confirm(L.T("SaveChangesHeading", name), L.T("SaveFirstBody", L.T(action)), L.T("Save"))
               && SaveEditor(askToReapply: false);
    }

    [RelayCommand]
    void ToggleActive(ProfileItemViewModel? item)
    {
        item ??= Selected;
        if (item is null) return;
        try
        {
            if (!SaveIfEditing(item.Name, "ActionToggling")) return;

            var name = item.Name;
            var turningOn = !Service.IsActive(name);
            if (turningOn && !ConfirmOverrides(name)) return;
            _main.Run(() =>
            {
                var result = turningOn ? Service.Apply(name) : Service.Unapply(name);
                _main.Status = L.T(turningOn ? "TurnedOnStatus" : "TurnedOffStatus", name, result.Changes.Count)
                               + (result.Warnings.Count > 0 ? $". {string.Join(" ", result.Warnings)}" : ".")
                               + " " + L.T("NewTerminalsSee");
            });
            _main.CurrentEnv.RefreshIfClean();
            _main.Backups.Refresh();
        }
        finally
        {
            // The switch flips as soon as it is clicked; put every switch back to the real state
            // (also after a failure, e.g. missing admin rights, or when the save prompt was declined).
            RefreshActive();
        }
    }

    bool HasUnsavedChanges() => Editor?.IsDirty == true;

    [RelayCommand(CanExecute = nameof(HasUnsavedChanges))]
    void Save() => SaveEditor(askToReapply: true);

    [RelayCommand(CanExecute = nameof(HasUnsavedChanges))]
    void Discard()
    {
        if (Editor is { IsDirty: true } editor
            && !Dialogs.ConfirmDanger(L.T("DiscardHeading"), L.T("DiscardBody", editor.Name), L.T("Discard")))
            return;
        LoadEditor();
    }

    [RelayCommand]
    void AddVariable()
    {
        if (Editor is null) return;
        Editor.VariableFilter = ""; // a new row would be hidden by a filter it does not match
        var variable = new VariableViewModel(new ProfileVariable { Name = "NEW_VARIABLE", Value = "" });
        Editor.Variables.Add(variable);
        SelectedVariable = variable;
    }

    [RelayCommand]
    void AddFromEnvironment()
    {
        if (Editor is null) return;
        var editor = Editor;
        EnvPickerViewModel? picker = null;
        if (!_main.Run(() => picker = new EnvPickerViewModel(Service))) return;

        var window = new EnvPickerWindow(picker!) { Owner = Application.Current.MainWindow };
        if (window.ShowDialog() != true) return;
        editor.VariableFilter = ""; // the picked variables must be visible
        VariableViewModel? last = null;
        foreach (var v in window.Result) last = editor.Merge(v);
        SelectedVariable = last;
        _main.Status = L.T("AddedFromEnv", window.Result.Count, editor.Name);
    }

    /// <summary>After the Environment tab marked a name as secret or plain.</summary>
    public void RefreshSecrets()
    {
        if (Editor is null) return;
        foreach (var v in Editor.Variables) v.RefreshSecret();
    }

    /// <summary>Ctrl+C on the grid: puts NAME=value of the selected variable on the clipboard.</summary>
    [RelayCommand]
    void CopyVariable()
    {
        if (SelectedVariable is not { } v || string.IsNullOrWhiteSpace(v.Name)) return;
        var name = v.Name.Trim();
        if (!Secrecy.Copy($"{name}={v.Value}", v.IsSecret))
        {
            _main.Status = L.T("ClipboardBusy");
            return;
        }
        _main.Status = v.IsSecret ? L.T("CopiedRowSecret", name, Secrecy.ClipboardSeconds) : L.T("CopiedRow", name);
    }

    /// <summary>Masks or unmasks the values of this name everywhere (a setting of the app, not part of the profile).</summary>
    [RelayCommand]
    void ToggleSecret(VariableViewModel? row)
    {
        row ??= SelectedVariable;
        if (Editor is null || row is null || string.IsNullOrWhiteSpace(row.Name)) return;
        var secret = !row.IsSecret;
        var name = row.Name.Trim();
        if (!_main.Run(() => Secrecy.Set(name, secret))) return;
        foreach (var v in Editor.Variables) v.RefreshSecret();
        _main.CurrentEnv.RefreshSecrets();
        _main.Status = L.T(secret ? "MarkedSecret" : "MarkedPlain", name);
    }

    /// <summary>The trash button of a row passes that row; without one, the selected row goes.</summary>
    [RelayCommand]
    void RemoveVariable(VariableViewModel? row)
    {
        row ??= SelectedVariable;
        if (Editor is null || row is null) return;
        Editor.Variables.Remove(row);
    }

    /// <summary>Puts variables with related names (Redis__Url, Redis__Password) next to each other.</summary>
    [RelayCommand]
    void GroupRelated()
    {
        if (Editor is null) return;
        var keep = SelectedVariable;
        var target = NameGroups.Group(Editor.Variables.ToList(), v => v.Name);
        var moved = 0;
        for (var i = 0; i < target.Count; i++)
        {
            var from = Editor.Variables.IndexOf(target[i]);
            if (from == i) continue;
            Editor.Variables.Move(from, i);
            moved++;
        }
        SelectedVariable = keep;
        _main.Status = moved == 0 ? L.T("GroupedAlready") : L.T("GroupedRelated", moved);
    }

    [RelayCommand]
    void MoveUp() => Move(-1);

    [RelayCommand]
    void MoveDown() => Move(1);

    void Move(int delta)
    {
        if (Editor is null || SelectedVariable is null) return;
        var index = Editor.Variables.IndexOf(SelectedVariable);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= Editor.Variables.Count) return;
        var keep = SelectedVariable;
        Editor.Variables.Move(index, target);
        SelectedVariable = keep;
    }

    // History rows: context menu (and double-click to compare).
    bool HasSelectedVersion() => Editor is not null && SelectedVersion is not null;

    [RelayCommand(CanExecute = nameof(HasSelectedVersion))]
    void Rollback()
    {
        if (Editor is null || SelectedVersion is null) return;
        var name = Editor.Name;
        var version = SelectedVersion.Version;
        if (!Dialogs.Confirm(L.T("RollbackHeading", name, version), L.T("RollbackBody"), L.T("RollBack")))
            return;
        if (_main.Run(() =>
            {
                var (profile, changed) = Service.Profiles.Rollback(name, version);
                _main.Status = changed ? L.T("RolledBack", name, version, profile.Version) : L.T("AlreadyMatchesVersion", name, version);
            }))
            Reload(name);
    }

    [RelayCommand(CanExecute = nameof(HasSelectedVersion))]
    void CompareVersion()
    {
        if (Editor is null || SelectedVersion is null) return;
        _main.Compare.Run($"{Editor.Name}@{SelectedVersion.Version}", Editor.Name);
        _main.SelectedTab = Tabs.Compare;
    }

    HashSet<string> ActiveNames() =>
        Service.State.Load().Applied.Select(a => a.Profile).ToHashSet(StringComparer.OrdinalIgnoreCase);

    static bool SameName(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

public partial class ProfileItemViewModel : ObservableObject
{
    public ProfileItemViewModel(Profile profile, bool isActive)
    {
        Name = profile.Name;
        Update(profile);
        IsActive = isActive;
    }

    public string Name { get; }

    [ObservableProperty]
    public partial bool IsActive { get; set; }

    /// <summary>Sets the state and always notifies, so a switch the user flipped is reset even when nothing changed.</summary>
    public void SyncActive(bool active)
    {
        IsActive = active;
        OnPropertyChanged(nameof(IsActive));
    }

    [ObservableProperty]
    public partial string Subtitle { get; set; } = "";

    public void Update(Profile profile)
    {
        Subtitle = $"v{profile.Version} · {L.T("ItemVars", profile.Variables.Count)}";
    }
}

public partial class ProfileEditorViewModel : ObservableObject
{
    public ProfileEditorViewModel(Profile profile)
    {
        VariableView = CollectionViewSource.GetDefaultView(Variables);
        VariableView.Filter = item => item is VariableViewModel variable
            && (string.IsNullOrWhiteSpace(VariableFilter)
                || variable.Name.Contains(VariableFilter.Trim(), StringComparison.OrdinalIgnoreCase)
                || variable.Value.Contains(VariableFilter.Trim(), StringComparison.OrdinalIgnoreCase));
        Name = profile.Name;
        Version = profile.Version;
        UpdatedAt = profile.UpdatedAt;
        foreach (var v in profile.Variables) Track(new VariableViewModel(v));
        Variables.CollectionChanged += (_, e) =>
        {
            foreach (VariableViewModel v in e.NewItems ?? Array.Empty<VariableViewModel>()) v.PropertyChanged += OnVariableChanged;
            MarkChanged();
        };
        IsDirty = false;
    }

    public string Name { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Header))]
    public partial int Version { get; set; }

    public DateTimeOffset? UpdatedAt { get; }

    public string Header => $"v{Version}" + (UpdatedAt is { } at ? " · " + L.T("EditorSavedAt", at.LocalDateTime.ToString("yyyy-MM-dd HH:mm")) : "");

    public ObservableCollection<VariableViewModel> Variables { get; } = [];

    public ICollectionView VariableView { get; }

    [ObservableProperty]
    public partial string VariableFilter { get; set; } = "";

    partial void OnVariableFilterChanged(string value) => VariableView.Refresh();

    [ObservableProperty]
    public partial bool IsDirty { get; set; }

    /// <summary>Raised on every edit (IsDirty only reports the first one).</summary>
    public event Action? Changed;

    void MarkChanged()
    {
        IsDirty = true;
        Changed?.Invoke();
    }

    /// <summary>
    /// Adds a variable the way the validator allows: Set/Remove replace every entry of the same variable;
    /// Prepend/Append replace a Set/Remove and merge into an existing entry with the same operation.
    /// </summary>
    public VariableViewModel Merge(ProfileVariable v)
    {
        bool SameVar(VariableViewModel x) =>
            x.Scope == v.Scope && string.Equals(x.Name.Trim(), v.Name, StringComparison.OrdinalIgnoreCase);

        if (v.Op is VarOp.Prepend or VarOp.Append
            && Variables.FirstOrDefault(x => SameVar(x) && x.Op == v.Op) is { } existing)
        {
            existing.Value = PathList.Join(PathList.Distinct(PathList.Split(existing.Value).Concat(PathList.Split(v.Value))));
            return existing;
        }

        Func<VariableViewModel, bool> replaced = v.Op is VarOp.Set or VarOp.Remove
            ? SameVar
            : x => SameVar(x) && x.Op is VarOp.Set or VarOp.Remove;
        var index = Variables.ToList().FindIndex(x => replaced(x));
        foreach (var old in Variables.Where(replaced).ToList()) Variables.Remove(old);

        var added = new VariableViewModel(v);
        Variables.Insert(index < 0 ? Variables.Count : Math.Min(index, Variables.Count), added);
        return added;
    }

    public void MarkSaved(int version)
    {
        Version = version;
        IsDirty = false;
    }

    public Profile ToProfile() => new()
    {
        Name = Name,
        Version = Version,
        Variables = Variables.Select(v => v.ToModel()).ToList(),
    };

    void Track(VariableViewModel v)
    {
        v.PropertyChanged += OnVariableChanged;
        Variables.Add(v);
    }

    void OnVariableChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Showing or hiding a secret is not an edit of the profile.
        if (e.PropertyName is nameof(VariableViewModel.IsRevealed) or nameof(VariableViewModel.IsSecret)
            or nameof(VariableViewModel.ValueShown) or nameof(VariableViewModel.SecretToggleText)) return;
        MarkChanged();
    }

}

public partial class VariableViewModel : ObservableObject
{
    public VariableViewModel(ProfileVariable v)
    {
        Scope = v.Scope;
        Op = v.Op;
        Name = v.Name;
        Value = v.Value ?? "";
        Kind = v.Kind?.ToString() ?? "Auto";
        Note = v.Note ?? "";
    }

    [ObservableProperty]
    public partial EnvScope Scope { get; set; }

    [ObservableProperty]
    public partial VarOp Op { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSecret), nameof(ValueShown), nameof(SecretToggleText))]
    public partial string Name { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ValueShown))]
    public partial string Value { get; set; }

    [ObservableProperty]
    public partial string Kind { get; set; }

    [ObservableProperty]
    public partial string Note { get; set; }

    /// <summary>The value is masked until the eye button is pressed (per row, forgotten when the profile is reloaded).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ValueShown))]
    public partial bool IsRevealed { get; set; }

    public bool IsSecret => Secrecy.IsSecret(Name);

    public string ValueShown => IsRevealed ? Value : Secrecy.Show(Name, Value);

    public string SecretToggleText => L.T(IsSecret ? "TreatAsPlain" : "TreatAsSecret");

    /// <summary>After the user marked a name as secret or plain.</summary>
    public void RefreshSecret()
    {
        OnPropertyChanged(nameof(IsSecret));
        OnPropertyChanged(nameof(ValueShown));
        OnPropertyChanged(nameof(SecretToggleText));
    }

    public ProfileVariable ToModel() => new()
    {
        Scope = Scope,
        Op = Op,
        Name = Name.Trim(),
        Value = Op == VarOp.Remove ? null : Value,
        Kind = Enum.TryParse<ValueKind>(Kind, out var kind) ? kind : null,
        Note = string.IsNullOrWhiteSpace(Note) ? null : Note,
    };
}

public sealed class ChangeRow(PlannedChange change)
{
    public string Action { get; } = L.T("Change" + change.Action);

    public string Variable { get; } = $"{change.Scope}\\{change.Name}";

    public string Before { get; } = change.Before is null ? L.T("ValueNotSet") : Secrecy.Show(change.Name, change.Before);

    public string After { get; } = change.After is null ? L.T("ValueDeleted") : Secrecy.Show(change.Name, change.After);
}
