using System.Text.RegularExpressions;

namespace EnvDeck.Core;

public enum ChangeAction { Unchanged, Create, Update, Delete }

/// <summary>One variable before/after a profile operation.</summary>
public sealed record PlannedChange(
    EnvScope Scope,
    string Name,
    string? Before,
    ValueKind? BeforeKind,
    string? After,
    ValueKind? AfterKind,
    IReadOnlyList<string> AddedEntries)
{
    /// <summary>All list entries the profile asked for (Prepend/Append), whether or not they were already there.</summary>
    public IReadOnlyList<string> RequestedEntries { get; init; } = [];

    public bool IsNoOp => Before == After && (After is null || BeforeKind == AfterKind);

    public ChangeAction Action =>
        IsNoOp ? ChangeAction.Unchanged
        : After is null ? ChangeAction.Delete
        : Before is null ? ChangeAction.Create
        : ChangeAction.Update;

    internal AppliedChange ToApplied() => new()
    {
        Scope = Scope,
        Name = Name,
        ExistedBefore = Before is not null,
        Before = Before,
        BeforeKind = BeforeKind,
        After = After,
        AfterKind = AfterKind,
        AddedEntries = AddedEntries.Count > 0 ? AddedEntries.ToList() : null,
        RequestedEntries = RequestedEntries.Count > 0 ? RequestedEntries.ToList() : null,
    };
}

public static partial class ChangePlanner
{
    [GeneratedRegex("%[^%;]+%")]
    private static partial Regex VariableReference();

    /// <summary>REG_EXPAND_SZ when the value references other variables (%USERPROFILE%...), REG_SZ otherwise.</summary>
    public static ValueKind AutoKind(string value) =>
        VariableReference().IsMatch(value) ? ValueKind.ExpandString : ValueKind.String;

    /// <summary>Computes what applying <paramref name="profile"/> would do to the current environment, without writing.</summary>
    public static List<PlannedChange> Plan(Profile profile, IEnvStore env)
    {
        var result = new List<PlannedChange>();
        foreach (var group in profile.Variables.GroupBy(v => (v.Scope, Key: v.Name.ToUpperInvariant())))
        {
            var first = group.First();
            var current = env.Get(first.Scope, first.Name);
            var original = PathList.Split(current?.Value);
            var after = current?.Value;
            var afterKind = current?.Kind;
            var added = new List<string>();
            var requested = new List<string>();

            foreach (var v in group)
            {
                switch (v.Op)
                {
                    case VarOp.Set:
                        after = v.Value ?? "";
                        afterKind = v.Kind ?? AutoKind(after);
                        break;

                    case VarOp.Remove:
                        after = null;
                        afterKind = null;
                        break;

                    case VarOp.Prepend:
                    case VarOp.Append:
                        var existing = PathList.Split(after);
                        var entries = existing.ToList();
                        var incoming = PathList.Distinct(PathList.Split(v.Value));
                        if (v.Op == VarOp.Prepend)
                        {
                            // Move entries that already exist to the front so the profile wins on lookup order.
                            entries.RemoveAll(e => PathList.Contains(incoming, e));
                            entries.InsertRange(0, incoming);
                        }
                        else
                        {
                            entries.AddRange(incoming.Where(e => !PathList.Contains(existing, e)));
                        }
                        added.AddRange(incoming.Where(e => !PathList.Contains(original, e) && !PathList.Contains(added, e)));
                        requested.AddRange(incoming.Where(e => !PathList.Contains(requested, e)));

                        // Keep the stored text byte-for-byte when the entry order did not actually change.
                        after = entries.SequenceEqual(existing) ? after : PathList.Join(entries);
                        afterKind = v.Kind
                                    ?? (afterKind == ValueKind.ExpandString || AutoKind(after!) == ValueKind.ExpandString
                                        ? ValueKind.ExpandString
                                        : afterKind ?? ValueKind.String);
                        break;
                }
            }

            result.Add(new PlannedChange(first.Scope, current?.Name ?? first.Name, current?.Value, current?.Kind,
                after, after is null ? null : afterKind, added) { RequestedEntries = requested });
        }
        return result;
    }
}
