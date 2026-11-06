namespace EnvDeck.Core;

public enum KeyProblemKind
{
    /// <summary>The same variable with the same operation on more than one row.</summary>
    Duplicate,

    /// <summary>Set/Remove next to another row of the same variable.</summary>
    Combined,
}

/// <summary>A variable whose rows clash; <paramref name="Rows"/> are the indexes of the rows to flag.</summary>
public sealed record KeyProblem(KeyProblemKind Kind, EnvScope Scope, string Name, IReadOnlyList<int> Rows);

public static class ProfileValidator
{
    public static List<string> Validate(Profile profile)
    {
        var errors = new List<string>();
        if (!ProfileStore.IsValidName(profile.Name))
            errors.Add($"Invalid profile name '{profile.Name}'. Use letters, digits, '.', '_' or '-' (max 64).");

        for (var i = 0; i < profile.Variables.Count; i++)
        {
            var v = profile.Variables[i];
            var where = $"Variable #{i + 1}";
            if (string.IsNullOrWhiteSpace(v.Name))
            {
                errors.Add($"{where}: name is empty.");
                continue;
            }
            if (v.Name.Contains('=') || v.Name.Trim() != v.Name)
                errors.Add($"{where} '{v.Name}': name must not contain '=' or leading/trailing spaces.");
            if (v.Op != VarOp.Remove && v.Value is null)
                errors.Add($"{where} '{v.Name}': {v.Op} needs a value.");
            if (v.Op is VarOp.Prepend or VarOp.Append && PathList.Split(v.Value).Count == 0)
                errors.Add($"{where} '{v.Name}': {v.Op} needs at least one ';'-separated entry.");
        }

        foreach (var p in FindKeyProblems(profile.Variables))
        {
            var label = $"{p.Scope}\\{p.Name}";
            errors.Add(p.Kind == KeyProblemKind.Duplicate
                ? $"{label} is listed more than once with the same operation. Remove the duplicate row."
                : $"{label}: Set/Remove cannot be combined with other entries for the same variable.");
        }
        return errors;
    }

    /// <summary>
    /// Problems between rows that share a variable. Public so an editor can flag the rows while the name is typed,
    /// with the same rules the save uses.
    /// </summary>
    public static List<KeyProblem> FindKeyProblems(IReadOnlyList<ProfileVariable> variables)
    {
        var problems = new List<KeyProblem>();
        var indexed = variables.Select((v, i) => (v, i)).Where(x => !string.IsNullOrWhiteSpace(x.v.Name));
        foreach (var group in indexed.GroupBy(x => (x.v.Scope, Name: x.v.Name.ToUpperInvariant())))
        {
            var items = group.ToList();
            if (items.Count < 2) continue;
            var name = items[0].v.Name;
            // The same variable listed twice with the same operation is a duplicate key: one row would silently
            // win (Set) or be merged (Prepend/Append), so the user has to pick which one to keep.
            var duplicated = items.GroupBy(x => x.v.Op).Where(g => g.Count() > 1).SelectMany(g => g).ToList();
            if (duplicated.Count > 0)
                problems.Add(new KeyProblem(KeyProblemKind.Duplicate, group.Key.Scope, name, duplicated.Select(x => x.i).ToList()));
            // Set/Remove replace the whole value, so they cannot be combined with other operations on the same variable.
            else if (items.Any(x => x.v.Op is VarOp.Set or VarOp.Remove))
                problems.Add(new KeyProblem(KeyProblemKind.Combined, group.Key.Scope, name, items.Select(x => x.i).ToList()));
        }
        return problems;
    }

    public static void ThrowIfInvalid(Profile profile)
    {
        var errors = Validate(profile);
        if (errors.Count > 0)
            throw new DeckException($"Profile '{profile.Name}' is invalid:{Environment.NewLine}  " +
                                    string.Join(Environment.NewLine + "  ", errors));
    }
}
