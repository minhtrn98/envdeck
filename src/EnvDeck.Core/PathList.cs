namespace EnvDeck.Core;

/// <summary>Helpers for ';'-separated list variables such as PATH or PSModulePath.</summary>
public static class PathList
{
    public static List<string> Split(string? value) =>
        string.IsNullOrEmpty(value)
            ? []
            : value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    public static string Join(IEnumerable<string> entries) => string.Join(';', entries);

    /// <summary>Entries are equal ignoring case, surrounding quotes and a trailing slash.</summary>
    public static bool Same(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    public static bool Contains(IEnumerable<string> entries, string entry) => entries.Any(e => Same(e, entry));

    public static string Remove(string value, IEnumerable<string> toRemove)
    {
        var remove = toRemove as IReadOnlyCollection<string> ?? toRemove.ToList();
        return Join(Split(value).Where(e => !Contains(remove, e)));
    }

    public static List<string> Distinct(IEnumerable<string> entries)
    {
        var result = new List<string>();
        foreach (var e in entries)
        {
            if (!Contains(result, e)) result.Add(e);
        }
        return result;
    }

    static string Normalize(string entry) => entry.Trim().Trim('"').TrimEnd('\\', '/');
}
