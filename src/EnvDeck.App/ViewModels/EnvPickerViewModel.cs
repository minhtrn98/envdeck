using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using EnvDeck.Core;

namespace EnvDeck.App.ViewModels;

/// <summary>Picks variables (or single list entries) of the current environment to add to a profile.</summary>
public partial class EnvPickerViewModel : ObservableObject
{
    public EnvPickerViewModel(IEnvStore env)
    {
        foreach (var scope in Options.Scopes)
        foreach (var value in env.GetAll(scope))
        {
            var item = new PickVariable(scope, value);
            item.Changed += () => OnPropertyChanged(nameof(Summary));
            Items.Add(item);
        }
        View = CollectionViewSource.GetDefaultView(Items);
        View.Filter = o => o is PickVariable v
                           && (ScopeFilter == "All" || v.Scope.ToString() == ScopeFilter)
                           && (string.IsNullOrWhiteSpace(Filter)
                               || v.Name.Contains(Filter.Trim(), StringComparison.OrdinalIgnoreCase)
                               || v.Value.Contains(Filter.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public ObservableCollection<PickVariable> Items { get; } = [];

    public ICollectionView View { get; }

    [ObservableProperty]
    public partial string Filter { get; set; } = "";

    [ObservableProperty]
    public partial string ScopeFilter { get; set; } = "All";

    [ObservableProperty]
    public partial PickVariable? Current { get; set; }

    [ObservableProperty]
    public partial bool Portable { get; set; } = true;

    public string Summary
    {
        get
        {
            var picked = Items.Where(i => i.Mode.Length > 0).ToList();
            return picked.Count == 0 ? "Nothing selected" : $"{picked.Count} variable(s) selected";
        }
    }

    partial void OnFilterChanged(string value) => View.Refresh();

    partial void OnScopeFilterChanged(string value) => View.Refresh();

    /// <summary>Ticked list entries win over the whole value: they become one Prepend entry per variable.</summary>
    public List<ProfileVariable> BuildResult()
    {
        var result = new List<ProfileVariable>();
        foreach (var item in Items)
        {
            var entries = item.Entries.Where(e => e.IsChecked).Select(e => e.Entry).ToList();
            if (entries.Count > 0)
                result.Add(VariableCapture.FromEntries(item.Scope, item.Name, entries, Portable));
            else if (item.IsChecked)
                result.Add(VariableCapture.FromValue(item.Scope, item.Source, Portable));
        }
        return result;
    }
}

public partial class PickVariable : ObservableObject
{
    public PickVariable(EnvScope scope, EnvValue source)
    {
        Scope = scope;
        Source = source;
        var entries = PathList.Split(source.Value);
        if (entries.Count < 2) return;
        foreach (var e in entries)
        {
            var expanded = Environment.ExpandEnvironmentVariables(e).Trim('"');
            var entry = new PickEntry(e, Directory.Exists(expanded) || File.Exists(expanded));
            entry.PropertyChanged += (_, _) => NotifyMode();
            Entries.Add(entry);
        }
    }

    public event Action? Changed;

    public EnvScope Scope { get; }

    public EnvValue Source { get; }

    public string Name => Source.Name;

    public string Value => Source.Value;

    public ObservableCollection<PickEntry> Entries { get; } = [];

    public bool IsList => Entries.Count > 0;

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    /// <summary>How the variable will be added: whole value, some entries, or not at all.</summary>
    public string Mode
    {
        get
        {
            var entries = Entries.Count(e => e.IsChecked);
            return entries > 0 ? $"Prepend {entries} entr{(entries == 1 ? "y" : "ies")}" : IsChecked ? "Set (whole value)" : "";
        }
    }

    partial void OnIsCheckedChanged(bool value) => NotifyMode();

    void NotifyMode()
    {
        OnPropertyChanged(nameof(Mode));
        Changed?.Invoke();
    }
}

public partial class PickEntry(string entry, bool exists) : ObservableObject
{
    public string Entry { get; } = entry;

    public bool Exists { get; } = exists;

    [ObservableProperty]
    public partial bool IsChecked { get; set; }
}
