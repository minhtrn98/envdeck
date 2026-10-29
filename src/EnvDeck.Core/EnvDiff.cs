namespace EnvDeck.Core;

public sealed record DiffEntry(EnvScope Scope, string Name, string? Value);

/// <summary>Something that can be compared: a profile (version), the live environment, a backup or a file.</summary>
public sealed record DiffSource(string Label, IReadOnlyList<DiffEntry> Entries);

public enum DiffKind { Same, Added, Removed, Changed }

/// <param name="OnlyLeft">For list variables (PATH...): entries present only on the left side.</param>
/// <param name="OnlyRight">For list variables: entries present only on the right side.</param>
public sealed record DiffRow(
    DiffKind Kind,
    EnvScope Scope,
    string Name,
    string? Left,
    string? Right,
    IReadOnlyList<string> OnlyLeft,
    IReadOnlyList<string> OnlyRight);

public static class EnvDiff
{
    public static List<DiffRow> Compare(DiffSource left, DiffSource right, bool includeSame = false)
    {
        var l = Index(left);
        var r = Index(right);
        var rows = new List<DiffRow>();

        foreach (var key in l.Keys.Union(r.Keys).OrderBy(k => k.Scope).ThenBy(k => k.Name, StringComparer.Ordinal))
        {
            l.TryGetValue(key, out var a);
            r.TryGetValue(key, out var b);
            var name = (a ?? b)!.Name;

            if (a is null)
            {
                rows.Add(new DiffRow(DiffKind.Added, key.Scope, name, null, b!.Value, [], []));
            }
            else if (b is null)
            {
                rows.Add(new DiffRow(DiffKind.Removed, key.Scope, name, a.Value, null, [], []));
            }
            else if (a.Value == b.Value)
            {
                if (includeSame) rows.Add(new DiffRow(DiffKind.Same, key.Scope, name, a.Value, b.Value, [], []));
            }
            else
            {
                IReadOnlyList<string> onlyLeft = [], onlyRight = [];
                if (PathList.Split(a.Value).Count > 1 || PathList.Split(b.Value).Count > 1)
                {
                    var le = PathList.Split(a.Value);
                    var re = PathList.Split(b.Value);
                    onlyLeft = le.Where(e => !PathList.Contains(re, e)).ToList();
                    onlyRight = re.Where(e => !PathList.Contains(le, e)).ToList();
                }
                rows.Add(new DiffRow(DiffKind.Changed, key.Scope, name, a.Value, b.Value, onlyLeft, onlyRight));
            }
        }
        return rows;
    }

    public static DiffSource FromProfile(Profile profile, string label)
    {
        var entries = profile.Variables
            .GroupBy(v => (v.Scope, Key: v.Name.ToUpperInvariant()))
            .Select(g => new DiffEntry(g.Key.Scope, g.First().Name, string.Join(" ", g.Select(Describe))))
            .ToList();
        return new DiffSource(label, entries);

        static string Describe(ProfileVariable v) => v.Op switch
        {
            VarOp.Set => v.Value ?? "",
            VarOp.Remove => "[Remove]",
            _ => $"[{v.Op}] {v.Value}",
        };
    }

    public static DiffSource FromValues(string label, IEnumerable<(EnvScope Scope, EnvValue Value)> values) =>
        new(label, values.Select(v => new DiffEntry(v.Scope, v.Value.Name, v.Value.Value)).ToList());

    static Dictionary<(EnvScope Scope, string Name), DiffEntry> Index(DiffSource source)
    {
        var map = new Dictionary<(EnvScope, string), DiffEntry>();
        foreach (var e in source.Entries) map[(e.Scope, e.Name.ToUpperInvariant())] = e;
        return map;
    }
}
