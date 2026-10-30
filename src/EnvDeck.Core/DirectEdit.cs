namespace EnvDeck.Core;

/// <summary>
/// Which parts of the live environment the profiles that are on manage: whole values (Set/Remove) and
/// single list entries (Prepend/Append). Direct edits must leave both alone, or turning a profile off
/// could no longer give the previous state back.
/// </summary>
public sealed class EnvOwnership
{
    readonly Dictionary<(EnvScope, string), string> _whole = [];
    readonly Dictionary<(EnvScope, string), List<(string Entry, string Profile)>> _entries = [];

    internal void SetWhole(EnvScope scope, string name, string profile) => _whole[Key(scope, name)] = profile;

    internal void AddEntry(EnvScope scope, string name, string entry, string profile)
    {
        if (!_entries.TryGetValue(Key(scope, name), out var list)) _entries[Key(scope, name)] = list = [];
        list.Add((entry, profile));
    }

    /// <summary>The profile that set or removed the whole variable, or null.</summary>
    public string? WholeOwner(EnvScope scope, string name) => _whole.GetValueOrDefault(Key(scope, name));

    /// <summary>The profile that added or asked for this list entry, or null.</summary>
    public string? EntryOwner(EnvScope scope, string name, string entry) =>
        OwnedEntries(scope, name).LastOrDefault(e => PathList.Same(e.Entry, entry)).Profile;

    public IReadOnlyList<(string Entry, string Profile)> OwnedEntries(EnvScope scope, string name) =>
        _entries.TryGetValue(Key(scope, name), out var list) ? list : [];

    static (EnvScope, string) Key(EnvScope scope, string name) => (scope, name.ToUpperInvariant());
}

/// <summary>
/// One edit of the live environment outside any profile. <see cref="Value"/> null deletes the variable;
/// <see cref="Kind"/> null picks the registry type from the value. <see cref="IsNew"/> and
/// <see cref="OriginalValue"/> describe what the editor loaded, so changes made meanwhile are not overwritten.
/// </summary>
public sealed record DirectChange(EnvScope Scope, string Name, string? Value, ValueKind? Kind, bool IsNew, string? OriginalValue);
