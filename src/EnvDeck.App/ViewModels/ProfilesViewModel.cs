using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EnvDeck.Core;

namespace EnvDeck.App.ViewModels;

public partial class ProfilesViewModel : ObservableObject
{
    readonly MainViewModel _main;
    bool _reloading;

    public ProfilesViewModel(MainViewModel main)
    {
        _main = main;
        View = CollectionViewSource.GetDefaultView(Items);
        View.Filter = o => o is ProfileItemViewModel p && p.Matches(Filter);
    }

    DeckService Service => _main.Service;

    public ObservableCollection<ProfileItemViewModel> Items { get; } = [];

    public ICollectionView View { get; }

    public ObservableCollection<ProfileVersionInfo> History { get; } = [];

    public ObservableCollection<ChangeRow> Preview { get; } = [];

    [ObservableProperty]
    public partial string Filter { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DuplicateCommand), nameof(RenameCommand), nameof(DeleteCommand))]
    public partial ProfileItemViewModel? Selected { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(DiscardCommand))]
    public partial ProfileEditorViewModel? Editor { get; set; }

    // Save/Discard are enabled only while the editor has unsaved changes.
    partial void OnEditorChanged(ProfileEditorViewModel? oldValue, ProfileEditorViewModel? newValue)
    {
        if (oldValue is not null) oldValue.PropertyChanged -= OnEditorPropertyChanged;
        if (newValue is not null) newValue.PropertyChanged += OnEditorPropertyChanged;
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
    public partial ProfileVersionInfo? SelectedVersion { get; set; }

    [ObservableProperty]
    public partial string PreviewSummary { get; set; } = "";

    partial void OnFilterChanged(string value) => View.Refresh();

    partial void OnSelectedChanged(ProfileItemViewModel? oldValue, ProfileItemViewModel? newValue)
    {
        if (_reloading) return;
        if (Editor is { IsDirty: true } && !SameName(Editor.Name, newValue?.Name)
            && Dialogs.Confirm($"Save changes to '{Editor.Name}'?",
                "You are switching to another profile; unsaved edits are lost otherwise.", "Save", "Don't save"))
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

        if (Service.Profiles.LoadErrors.Count > 0) _main.Status = "Could not read: " + string.Join("; ", Service.Profiles.LoadErrors);
        // Keep unsaved edits of the selected profile across a refresh.
        if (select is not null || Editor is not { IsDirty: true } || !SameName(Editor.Name, Selected?.Name)) LoadEditor();
    }

    public void RefreshActive()
    {
        var active = ActiveNames();
        foreach (var item in Items) item.SyncActive(active.Contains(item.Name));
    }

    void LoadEditor()
    {
        Preview.Clear();
        PreviewSummary = "";
        History.Clear();
        if (Selected is null)
        {
            Editor = null;
            return;
        }
        _main.Run(() =>
        {
            var profile = Service.Profiles.Get(Selected.Name);
            Editor = new ProfileEditorViewModel(profile);
            foreach (var h in Service.Profiles.History(profile.Name)) History.Add(h);
        });
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
            _main.Status = changed ? $"Saved '{profile.Name}' as v{profile.Version}." : "No changes to save.";
            if (changed && askToReapply && Service.IsActive(profile.Name)
                && Dialogs.Confirm($"Apply v{profile.Version} of '{profile.Name}' now?",
                    "The profile is on, and the environment still has the previous version.", "Apply now", "Later"))
                Service.Apply(profile.Name);
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
        var name = Dialogs.Prompt("New profile", "Profile name (letters, digits, . _ -):");
        if (name is null) return;
        if (_main.Run(() =>
            {
                if (Service.Profiles.Exists(name)) throw new DeckException($"Profile '{name}' already exists.");
                Service.Profiles.Save(new Profile { Name = name });
            }, $"Created '{name}'."))
            Reload(name);
    }

    // Duplicate/Rename/Delete act on the given profile, or on the selection (context menu, F2, Ctrl+D, Del).

    bool HasTarget(ProfileItemViewModel? item) => (item ?? Selected) is not null;

    [RelayCommand(CanExecute = nameof(HasTarget))]
    void Duplicate(ProfileItemViewModel? item)
    {
        item ??= Selected;
        if (item is null || !SaveIfEditing(item.Name, "duplicating")) return;
        var source = item.Name;
        var name = Dialogs.Prompt("Duplicate profile", $"New name for the copy of '{source}':", source + "-copy");
        if (name is null) return;
        if (_main.Run(() =>
            {
                if (Service.Profiles.Exists(name)) throw new DeckException($"Profile '{name}' already exists.");
                var copy = Service.Profiles.Get(source).Clone();
                copy.Name = name;
                copy.Version = 0;
                Service.Profiles.Save(copy);
            }, $"Created '{name}' from '{source}'."))
            Reload(name);
    }

    [RelayCommand(CanExecute = nameof(HasTarget))]
    void Rename(ProfileItemViewModel? item)
    {
        item ??= Selected;
        if (item is null || !SaveIfEditing(item.Name, "renaming")) return;
        var old = item.Name;
        var name = Dialogs.Prompt("Rename profile", $"New name for '{old}':", old);
        if (name is null || name == old) return;
        if (_main.Run(() => Service.RenameProfile(old, name), $"Renamed '{old}' to '{name}'.")) Reload(name);
    }

    [RelayCommand(CanExecute = nameof(HasTarget))]
    void Delete(ProfileItemViewModel? item)
    {
        item ??= Selected;
        if (item is null) return;
        var name = item.Name;
        var active = Service.IsActive(name);
        var details = (active ? "It is on: it will be turned off first, undoing its changes to the environment.\n\n" : "")
                      + "Its version history is kept, so a profile with the same name can be rolled back to it.";
        if (!Dialogs.ConfirmDanger($"Delete profile '{name}'?", details, "Delete")) return;

        if (_main.Run(() =>
            {
                if (active) Service.Unapply(name);
                Service.DeleteProfile(name, purgeHistory: false);
            }, $"Deleted '{name}'{(active ? " (turned off first)" : "")}."))
        {
            Reload();
            if (active) _main.CurrentEnv.Refresh();
        }
    }

    /// <summary>Unsaved edits of <paramref name="name"/> would be lost or left out; offer to save them first.</summary>
    bool SaveIfEditing(string name, string action)
    {
        if (Editor is not { IsDirty: true } || !SameName(Editor.Name, name)) return true;
        return Dialogs.Confirm($"Save changes to '{name}'?", $"They have to be saved before {action}.", "Save")
               && SaveEditor(askToReapply: false);
    }

    [RelayCommand]
    void ToggleActive(ProfileItemViewModel? item)
    {
        item ??= Selected;
        if (item is null) return;
        try
        {
            if (!SaveIfEditing(item.Name, "turning it on or off")) return;

            var name = item.Name;
            var turningOn = !Service.IsActive(name);
            _main.Run(() =>
            {
                var result = turningOn ? Service.Apply(name) : Service.Unapply(name);
                var extra = result.TurnedOff.Where(o => !SameName(o, name)).ToList();
                _main.Status = $"{(turningOn ? "Turned on" : "Turned off")} '{name}': {result.Changes.Count} variable(s) changed"
                               + (extra.Count > 0 ? $"; turned off {string.Join(", ", extra)} (same group)" : "")
                               + (result.Warnings.Count > 0 ? $". {string.Join(" ", result.Warnings)}" : ".")
                               + " New terminals see the change.";
            });
            _main.CurrentEnv.Refresh();
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
            && !Dialogs.ConfirmDanger("Discard unsaved changes?", $"Your edits to '{editor.Name}' will be lost.", "Discard"))
            return;
        LoadEditor();
    }

    [RelayCommand]
    void PreviewChanges()
    {
        if (Editor is null) return;
        var editor = Editor;
        _main.Run(() =>
        {
            var profile = editor.ToProfile();
            ProfileValidator.ThrowIfInvalid(profile);
            Preview.Clear();
            var changes = Service.Plan(profile);
            foreach (var c in changes.Where(c => !c.IsNoOp)) Preview.Add(new ChangeRow(c));
            PreviewSummary = Preview.Count == 0
                ? "Turning this profile on would change nothing."
                : $"Turning this profile on would change {Preview.Count} variable(s):";
        });
    }

    [RelayCommand]
    void AddVariable()
    {
        if (Editor is null) return;
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
        if (!_main.Run(() => picker = new EnvPickerViewModel(Service.Env))) return;

        var window = new EnvPickerWindow(picker!) { Owner = Application.Current.MainWindow };
        if (window.ShowDialog() != true) return;
        VariableViewModel? last = null;
        foreach (var v in window.Result) last = editor.Merge(v);
        SelectedVariable = last;
        _main.Status = $"Added {window.Result.Count} variable(s) from the current environment to '{editor.Name}'. Review, then Save.";
    }

    [RelayCommand]
    void RemoveVariable()
    {
        if (Editor is null || SelectedVariable is null) return;
        Editor.Variables.Remove(SelectedVariable);
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

    [RelayCommand]
    void Rollback()
    {
        if (Editor is null || SelectedVersion is null) return;
        var name = Editor.Name;
        var version = SelectedVersion.Version;
        if (!Dialogs.Confirm($"Roll '{name}' back to v{version}?",
                "It is saved as a new version, so nothing in the history is lost.", "Roll back"))
            return;
        if (_main.Run(() =>
            {
                var (profile, changed) = Service.Profiles.Rollback(name, version);
                _main.Status = changed ? $"'{name}' restored from v{version} as v{profile.Version}." : $"'{name}' already matches v{version}.";
            }))
            Reload(name);
    }

    [RelayCommand]
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

    string _searchText = "";

    public void Update(Profile profile)
    {
        var parts = new List<string> { $"v{profile.Version}", $"{profile.Variables.Count} var" };
        if (!string.IsNullOrEmpty(profile.Group)) parts.Add($"group: {profile.Group}");
        if (!string.IsNullOrEmpty(profile.Description)) parts.Add(profile.Description);
        Subtitle = string.Join(" · ", parts);
        _searchText = string.Join(" ", [profile.Name, profile.Description, profile.Group, .. profile.Tags ?? []]);
    }

    public bool Matches(string filter) =>
        string.IsNullOrWhiteSpace(filter) || _searchText.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase);
}

public partial class ProfileEditorViewModel : ObservableObject
{
    public ProfileEditorViewModel(Profile profile)
    {
        Name = profile.Name;
        Version = profile.Version;
        UpdatedAt = profile.UpdatedAt;
        Description = profile.Description ?? "";
        Group = profile.Group ?? "";
        Tags = string.Join(", ", profile.Tags ?? []);
        foreach (var v in profile.Variables) Track(new VariableViewModel(v));
        Variables.CollectionChanged += (_, e) =>
        {
            foreach (VariableViewModel v in e.NewItems ?? Array.Empty<VariableViewModel>()) v.PropertyChanged += OnVariableChanged;
            IsDirty = true;
        };
        IsDirty = false;
    }

    public string Name { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Header))]
    public partial int Version { get; set; }

    public DateTimeOffset? UpdatedAt { get; }

    public string Header => $"v{Version}" + (UpdatedAt is { } at ? $" · saved {at.LocalDateTime:yyyy-MM-dd HH:mm}" : "");

    public ObservableCollection<VariableViewModel> Variables { get; } = [];

    [ObservableProperty]
    public partial string Description { get; set; }

    [ObservableProperty]
    public partial string Group { get; set; }

    [ObservableProperty]
    public partial string Tags { get; set; }

    [ObservableProperty]
    public partial bool IsDirty { get; set; }

    partial void OnDescriptionChanged(string value) => IsDirty = true;

    partial void OnGroupChanged(string value) => IsDirty = true;

    partial void OnTagsChanged(string value) => IsDirty = true;

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
        Description = NullIfEmpty(Description),
        Group = NullIfEmpty(Group),
        Tags = Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is { Length: > 0 } tags
            ? tags.ToList()
            : null,
        Variables = Variables.Select(v => v.ToModel()).ToList(),
    };

    void Track(VariableViewModel v)
    {
        v.PropertyChanged += OnVariableChanged;
        Variables.Add(v);
    }

    void OnVariableChanged(object? sender, PropertyChangedEventArgs e) => IsDirty = true;

    static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
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
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial string Value { get; set; }

    [ObservableProperty]
    public partial string Kind { get; set; }

    [ObservableProperty]
    public partial string Note { get; set; }

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
    public string Action { get; } = change.Action.ToString();

    public string Variable { get; } = $"{change.Scope}\\{change.Name}";

    public string Before { get; } = change.Before ?? "(not set)";

    public string After { get; } = change.After ?? "(deleted)";
}
