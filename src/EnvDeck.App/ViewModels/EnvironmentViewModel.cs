using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EnvDeck.App.Localization;
using EnvDeck.Core;

namespace EnvDeck.App.ViewModels;

public enum EnvRowState { Unchanged, Modified, New, Deleted }

/// <summary>
/// The live environment, editable in place. Edits stay pending until Apply, which previews them, backs up and
/// writes. Variables and list entries that a profile which is on manages are locked.
/// </summary>
public partial class EnvironmentViewModel : ObservableObject
{
    readonly MainViewModel _main;
    EnvOwnership _ownership = new();
    bool _syncingEntries;

    public EnvironmentViewModel(MainViewModel main)
    {
        _main = main;
        View = CollectionViewSource.GetDefaultView(Rows);
        // Pending rows stay visible whatever the filter, so a new row does not vanish while its name is typed.
        View.Filter = o => o is EnvRowViewModel r
                           && (r.State != EnvRowState.Unchanged
                               || ((ScopeFilter == "All" || r.Scope.ToString() == ScopeFilter)
                                   && (string.IsNullOrWhiteSpace(Filter)
                                       || r.Name.Contains(Filter.Trim(), StringComparison.OrdinalIgnoreCase)
                                       || r.Value.Contains(Filter.Trim(), StringComparison.OrdinalIgnoreCase))));
        Entries.CollectionChanged += OnEntriesChanged;
    }

    DeckService Service => _main.Service;

    public ObservableCollection<EnvRowViewModel> Rows { get; } = [];

    public ICollectionView View { get; }

    /// <summary>Entries of the selected list variable (PATH...), editable one by one.</summary>
    public ObservableCollection<EnvEntryViewModel> Entries { get; } = [];

    [ObservableProperty]
    public partial string Filter { get; set; } = "";

    [ObservableProperty]
    public partial string ScopeFilter { get; set; } = "All";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteVariableCommand), nameof(AddEntryCommand), nameof(EditInProfileCommand), nameof(CopyValueCommand))]
    [NotifyPropertyChangedFor(nameof(EntriesTitle), nameof(ShowEntries))]
    public partial EnvRowViewModel? Selected { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveEntryCommand))]
    public partial EnvEntryViewModel? SelectedEntry { get; set; }

    public int PendingCount => Rows.Count(r => r.State != EnvRowState.Unchanged);

    public bool HasPending => PendingCount > 0;

    public string ApplyText => HasPending ? L.T("ApplyPending", PendingCount) : L.T("Apply");

    public bool ShowEntries => Selected is { IsListLike: true, IsDeleted: false } row && (!row.IsSecret || row.IsRevealed);

    public string EntriesTitle => Selected is null ? L.T("Entries") : L.T("EntriesOf", Selected.Name);

    partial void OnFilterChanged(string value) => RefreshView();

    partial void OnScopeFilterChanged(string value) => RefreshView();

    void RefreshView()
    {
        EndEdit();
        View.Refresh();
    }

    /// <summary>The view refuses to refresh or reset in the middle of a cell edit; finish it first.</summary>
    void EndEdit()
    {
        if (View is not IEditableCollectionView editable) return;
        if (editable.IsAddingNew) editable.CommitNew();
        if (editable.IsEditingItem) editable.CommitEdit();
    }

    partial void OnSelectedChanged(EnvRowViewModel? value)
    {
        if (_previousSelected is not null) _previousSelected.PropertyChanged -= SelectedRow_PropertyChanged;
        _previousSelected = value;
        RebuildEntries();
        if (value is not null) value.PropertyChanged += SelectedRow_PropertyChanged;
    }

    EnvRowViewModel? _previousSelected;

    void SelectedRow_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EnvRowViewModel.IsRevealed) or nameof(EnvRowViewModel.IsSecret))
            OnPropertyChanged(nameof(ShowEntries));
    }

    /// <summary>After the user marked a name as secret or plain.</summary>
    public void RefreshSecrets()
    {
        foreach (var row in Rows) row.RefreshSecret();
        RebuildEntries();
        OnPropertyChanged(nameof(ShowEntries));
    }

    [RelayCommand]
    void ToggleSecret(EnvRowViewModel? target)
    {
        if ((target ?? Selected) is not { } row || string.IsNullOrWhiteSpace(row.Name)) return;
        var secret = !row.IsSecret;
        var name = row.Name.Trim();
        if (!_main.Run(() => Secrecy.Set(name, secret))) return;
        RefreshSecrets();
        _main.Profiles.RefreshSecrets();
        _main.Status = L.T(secret ? "MarkedSecret" : "MarkedPlain", name);
    }

    /// <summary>Rebuilds the text made in code after a language change; pending edits are kept.</summary>
    public void RefreshTexts()
    {
        OnPropertyChanged(nameof(ApplyText));
        OnPropertyChanged(nameof(EntriesTitle));
        foreach (var row in Rows) row.RefreshTexts();
        foreach (var entry in Entries) entry.RefreshTexts();
    }

    // ---------------------------------------------------------------- loading

    /// <summary>Reloads unless there are pending edits (used after profiles change the environment).</summary>
    public void RefreshIfClean()
    {
        if (!HasPending) Reload();
    }

    [RelayCommand]
    void Refresh()
    {
        if (HasPending && !Dialogs.ConfirmDanger(L.T("EnvDiscardHeading"), L.T("EnvDiscardBody", PendingCount), L.T("Discard")))
            return;
        Reload();
    }

    [RelayCommand(CanExecute = nameof(HasPending))]
    void Discard()
    {
        if (!Dialogs.ConfirmDanger(L.T("EnvDiscardHeading"), L.T("EnvDiscardBody", PendingCount), L.T("Discard"))) return;
        Reload();
    }

    void Reload()
    {
        var keep = Selected;
        EndEdit();
        if (View is ListCollectionView grouped) grouped.CustomSort = null;
        foreach (var row in Rows) row.Changed -= OnRowChanged;
        Rows.Clear();
        _main.Run(() =>
        {
            _ownership = Service.GetOwnership();
            foreach (var scope in Options.Scopes)
            foreach (var v in Service.Env.GetAll(scope))
                Track(new EnvRowViewModel(scope, v, _ownership));
        });
        Selected = keep is null ? null : Rows.FirstOrDefault(r => r.Scope == keep.Scope && SameName(r.Name, keep.Name));
        PendingChanged();
    }

    void Track(EnvRowViewModel row)
    {
        row.Changed += OnRowChanged;
        Rows.Add(row);
    }

    // ---------------------------------------------------------------- editing variables

    /// <summary>Raised when the view order is no longer the one a column header shows.</summary>
    public event Action? GroupingApplied;

    /// <summary>
    /// Shows variables with similar names next to each other (Redis__Url, redis_pw, acc_redis). Only the view order
    /// changes - the environment has no order of its own - and it lasts until Refresh, Apply or a header sort.
    /// </summary>
    [RelayCommand]
    void GroupRelated()
    {
        EndEdit();
        var rank = NameGroups.Group(Rows.ToList(), r => r.Name)
            .Select((row, index) => (row, index)).ToDictionary(x => x.row, x => x.index);
        if (View is ListCollectionView list) list.CustomSort = new RankComparer(rank);
        GroupingApplied?.Invoke();
        _main.Status = L.T("EnvGrouped");
    }

    sealed class RankComparer(Dictionary<EnvRowViewModel, int> rank) : System.Collections.IComparer
    {
        // Rows added after grouping have no rank and go last.
        int Rank(object? o) => o is EnvRowViewModel r && rank.TryGetValue(r, out var i) ? i : int.MaxValue;

        public int Compare(object? x, object? y) => Rank(x).CompareTo(Rank(y));
    }

    [RelayCommand]
    void AddVariable()
    {
        var scope = ScopeFilter == nameof(EnvScope.Machine) ? EnvScope.Machine : EnvScope.User;
        var row = new EnvRowViewModel(scope, null, _ownership);
        Track(row);
        Selected = row;
        PendingChanged();
    }

    bool CanDelete(EnvRowViewModel? row) => (row ?? Selected) is { CanDelete: true };

    /// <summary>
    /// Marks a variable for deletion (again: unmarks it); a new row is just dropped. The trash button of a row passes
    /// that row; the Delete key and the context menu act on the selected one.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDelete))]
    void DeleteVariable(EnvRowViewModel? target)
    {
        if ((target ?? Selected) is not { } row) return;
        if (row.IsNew)
        {
            row.Changed -= OnRowChanged;
            Rows.Remove(row);
            Selected = null;
        }
        else
        {
            row.IsDeleted = !row.IsDeleted;
        }
        PendingChanged();
    }

    bool HasOwner() => Selected?.OwnerProfile is not null;

    /// <summary>Opens the profile that manages the selected variable (or some of its entries).</summary>
    [RelayCommand(CanExecute = nameof(HasOwner))]
    void EditInProfile()
    {
        if (Selected?.OwnerProfile is not { } profile) return;
        _main.Profiles.Reload(profile);
        _main.SelectedTab = Tabs.Profiles;
    }

    bool HasSelection() => Selected is not null;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    void CopyValue()
    {
        if (Selected is null) return;
        if (!Secrecy.Copy(Selected.Value, Selected.IsSecret))
        {
            _main.Status = L.T("ClipboardBusy");
            return;
        }
        _main.Status = Selected.IsSecret
            ? L.T("CopiedSecret", Selected.Name, Secrecy.ClipboardSeconds)
            : L.T("CopiedValue", Selected.Name);
    }

    /// <summary>Ctrl+C on the grid: puts NAME=value of the selected variable on the clipboard.</summary>
    [RelayCommand]
    void CopyRow()
    {
        if (Selected is not { } row || string.IsNullOrWhiteSpace(row.Name)) return;
        var name = row.Name.Trim();
        if (!Secrecy.Copy($"{name}={row.Value}", row.IsSecret))
        {
            _main.Status = L.T("ClipboardBusy");
            return;
        }
        _main.Status = row.IsSecret ? L.T("CopiedRowSecret", name, Secrecy.ClipboardSeconds) : L.T("CopiedRow", name);
    }

    [RelayCommand(CanExecute = nameof(HasPending))]
    void Apply() => ApplyPending();

    /// <summary>Previews, confirms and writes the pending edits; false when nothing was written.</summary>
    public bool ApplyPending()
    {
        var pending = Rows.Where(r => r.State != EnvRowState.Unchanged).ToList();
        if (pending.Count == 0) return true;

        foreach (var row in pending.Where(r => !r.IsDeleted))
        {
            var name = row.Name.Trim();
            if (name.Length == 0 || name.Contains('='))
            {
                Selected = row;
                Dialogs.Error(L.T("EnvInvalidName"));
                return false;
            }
            if (Rows.Count(r => r != row && !r.IsDeleted && r.Scope == row.Scope && SameName(r.Name.Trim(), name)) > 0)
            {
                Selected = row;
                Dialogs.Error(L.T("EnvDuplicate", $"{row.Scope}\\{name}"));
                return false;
            }
        }

        var edits = pending.Select(r => r.ToChange()).ToList();
        var applied = false;
        _main.Run(() =>
        {
            var preview = Service.ApplyDirect(edits, dryRun: true);
            if (preview.Changes.Count > 0)
            {
                var summary = string.Join("\n", preview.Changes.Take(15).Select(c =>
                    $"{(c.Action == ChangeAction.Delete ? "−" : c.Action == ChangeAction.Create ? "+" : "~")} {c.Scope}\\{c.Name}"));
                if (preview.Changes.Count > 15) summary += "\n" + L.T("AndMore", preview.Changes.Count - 15);
                if (!Dialogs.Confirm(L.T("EnvApplyHeading", preview.Changes.Count), L.T("EnvApplyBody", summary), L.T("Apply")))
                    return;
                var result = Service.ApplyDirect(edits);
                _main.Status = L.T("EnvApplied", result.Changes.Count);
            }
            applied = true;
        });
        if (!applied) return false;

        Reload();
        _main.Backups.Refresh();
        _main.Profiles.RefreshActive(); // the profile preview depends on the environment
        return true;
    }

    void OnRowChanged(EnvRowViewModel row)
    {
        if (row == Selected && !_syncingEntries) RebuildEntries();
        PendingChanged();
    }

    void PendingChanged()
    {
        OnPropertyChanged(nameof(PendingCount));
        OnPropertyChanged(nameof(HasPending));
        OnPropertyChanged(nameof(ApplyText));
        OnPropertyChanged(nameof(ShowEntries));
        OnPropertyChanged(nameof(EntriesTitle));
        ApplyCommand.NotifyCanExecuteChanged();
        DiscardCommand.NotifyCanExecuteChanged();
        DeleteVariableCommand.NotifyCanExecuteChanged();
    }

    // ---------------------------------------------------------------- editing list entries

    void RebuildEntries()
    {
        _syncingEntries = true;
        try
        {
            foreach (var e in Entries) e.PropertyChanged -= OnEntryChanged;
            Entries.Clear();
            if (Selected is not { IsListLike: true } row) return;
            foreach (var text in PathList.Split(row.Value))
                AddEntryRow(new EnvEntryViewModel(text, row.IsNew ? null : _ownership.EntryOwner(row.Scope, row.Name, text)));
        }
        finally
        {
            _syncingEntries = false;
        }
    }

    void AddEntryRow(EnvEntryViewModel entry)
    {
        entry.PropertyChanged += OnEntryChanged;
        Entries.Add(entry);
    }

    bool CanAddEntry() => Selected is { IsLocked: false, IsDeleted: false };

    [RelayCommand(CanExecute = nameof(CanAddEntry))]
    void AddEntry()
    {
        var entry = new EnvEntryViewModel("", null);
        AddEntryRow(entry);
        SelectedEntry = entry;
    }

    bool CanRemoveEntry(EnvEntryViewModel? entry) => (entry ?? SelectedEntry) is { IsLocked: false };

    [RelayCommand(CanExecute = nameof(CanRemoveEntry))]
    void RemoveEntry(EnvEntryViewModel? target)
    {
        if ((target ?? SelectedEntry) is not { IsLocked: false } entry) return;
        entry.PropertyChanged -= OnEntryChanged;
        Entries.Remove(entry);
    }

    /// <summary>Keyboard alternative to dragging an entry (Alt+Up / Alt+Down).</summary>
    public void MoveEntry(int delta)
    {
        if (SelectedEntry is not { IsLocked: false } entry) return;
        var index = Entries.IndexOf(entry);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= Entries.Count) return;
        Entries.Move(index, target);
        SelectedEntry = entry;
    }

    void OnEntryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EnvEntryViewModel.Text)) SyncValueFromEntries();
    }

    void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e) => SyncValueFromEntries();

    void SyncValueFromEntries()
    {
        if (_syncingEntries || Selected is null) return;
        _syncingEntries = true;
        try
        {
            Selected.Value = PathList.Join(Entries.Select(e => e.Text.Trim()).Where(t => t.Length > 0));
        }
        finally
        {
            _syncingEntries = false;
        }
    }

    static bool SameName(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>One variable of the live environment, with its pending edit.</summary>
public partial class EnvRowViewModel : ObservableObject
{
    public EnvRowViewModel(EnvScope scope, EnvValue? value, EnvOwnership ownership)
    {
        Scope = scope;
        Name = value?.Name ?? "";
        Value = value?.Value ?? "";
        Kind = value?.Kind.ToString() ?? "Auto";
        IsNew = value is null;
        OriginalValue = value?.Value;
        OriginalKind = Kind;
        if (value is null) return;

        Owner = ownership.WholeOwner(scope, value.Name);
        var entryOwners = ownership.OwnedEntries(scope, value.Name)
            .Where(e => PathList.Contains(PathList.Split(value.Value), e.Entry))
            .Select(e => e.Profile).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        EntryOwners = entryOwners.Count > 0 ? string.Join(", ", entryOwners) : null;
    }

    public event Action<EnvRowViewModel>? Changed;

    [ObservableProperty]
    public partial EnvScope Scope { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsListLike), nameof(IsSecret), nameof(ValueShown), nameof(SecretToggleText))]
    public partial string Name { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(State), nameof(Marker), nameof(StateTip), nameof(IsListLike), nameof(ValueShown))]
    public partial string Value { get; set; }

    /// <summary>The value is masked until the eye button is pressed (per row, forgotten on reload).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ValueShown))]
    public partial bool IsRevealed { get; set; }

    public bool IsSecret => Secrecy.IsSecret(Name);

    public string ValueShown => IsRevealed ? Value : Secrecy.Show(Name, Value);

    public string SecretToggleText => L.T(IsSecret ? "TreatAsPlain" : "TreatAsSecret");

    public void RefreshSecret()
    {
        OnPropertyChanged(nameof(IsSecret));
        OnPropertyChanged(nameof(ValueShown));
        OnPropertyChanged(nameof(SecretToggleText));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(State), nameof(Marker), nameof(StateTip))]
    public partial string Kind { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(State), nameof(Marker), nameof(StateTip), nameof(CanEditKind))]
    public partial bool IsDeleted { get; set; }

    public bool IsNew { get; }

    public string? OriginalValue { get; }

    public string OriginalKind { get; }

    /// <summary>Profile that set or removed the whole variable: the row is read-only.</summary>
    public string? Owner { get; }

    /// <summary>Profiles that added some of the list entries: those entries are read-only.</summary>
    public string? EntryOwners { get; }

    public bool IsLocked => Owner is not null;

    public bool IsPartlyLocked => !IsLocked && EntryOwners is not null;

    public bool CanDelete => !IsLocked && !IsPartlyLocked;

    /// <summary>The scope is fixed once a variable exists.</summary>
    public bool CanEditScope => IsNew;

    public bool CanEditKind => !IsLocked && !IsDeleted;

    public string? OwnerProfile => Owner ?? EntryOwners?.Split(", ")[0];

    public string? LockTip =>
        IsLocked ? L.T("EnvLockedTip", Owner) : IsPartlyLocked ? L.T("EnvPartlyLockedTip", EntryOwners) : null;

    /// <summary>
    /// Shown entry by entry below the grid: PATH-like names or values with ';'. Stays so when an edit leaves a
    /// single entry, so the entry editor does not vanish while it is used.
    /// </summary>
    public bool IsListLike =>
        Value.Contains(';') || OriginalValue?.Contains(';') == true || Name.EndsWith("PATH", StringComparison.OrdinalIgnoreCase);

    public EnvRowState State =>
        IsDeleted ? EnvRowState.Deleted
        : IsNew ? EnvRowState.New
        : Value != OriginalValue || Kind != OriginalKind ? EnvRowState.Modified
        : EnvRowState.Unchanged;

    public string Marker => State switch
    {
        EnvRowState.New => "+",
        EnvRowState.Modified => "●",
        EnvRowState.Deleted => "−",
        _ => "",
    };

    /// <summary>Tooltip of the marker column: who locks the row, and what Apply will do with it.</summary>
    public string? StateTip
    {
        get
        {
            var state = State switch
            {
                EnvRowState.New => L.T("EnvStateNew"),
                EnvRowState.Modified => L.T("EnvStateModified"),
                EnvRowState.Deleted => L.T("EnvStateDeleted"),
                _ => null,
            };
            return LockTip is null ? state : state is null ? LockTip : LockTip + "\n" + state;
        }
    }

    public void RefreshTexts()
    {
        OnPropertyChanged(nameof(SecretToggleText));
        OnPropertyChanged(nameof(LockTip));
        OnPropertyChanged(nameof(StateTip));
    }

    public DirectChange ToChange() => new(
        Scope,
        Name.Trim(),
        IsDeleted ? null : Value,
        Enum.TryParse<ValueKind>(Kind, out var kind) ? kind : null,
        IsNew,
        OriginalValue);

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(Scope) or nameof(Name) or nameof(Value) or nameof(Kind) or nameof(IsDeleted) or nameof(IsRevealed))
            Changed?.Invoke(this);
    }
}

/// <summary>One entry of a list variable; entries a profile that is on added are read-only.</summary>
public partial class EnvEntryViewModel(string text, string? owner) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Exists), nameof(Tip))]
    public partial string Text { get; set; } = text;

    public string? Owner { get; } = owner;

    public bool IsLocked => Owner is not null;

    public bool Exists
    {
        get
        {
            var expanded = Environment.ExpandEnvironmentVariables(Text).Trim('"');
            return expanded.Length == 0 || Directory.Exists(expanded) || File.Exists(expanded);
        }
    }

    public string Tip => IsLocked ? L.T("EntryLockedTip", Owner) : Environment.ExpandEnvironmentVariables(Text);

    public void RefreshTexts() => OnPropertyChanged(nameof(Tip));
}
