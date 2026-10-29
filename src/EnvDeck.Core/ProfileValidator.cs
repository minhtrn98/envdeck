namespace EnvDeck.Core;

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

        // Set/Remove replace the whole value, so they cannot be combined with other operations on the same variable.
        foreach (var group in profile.Variables
                     .Where(v => !string.IsNullOrWhiteSpace(v.Name))
                     .GroupBy(v => (v.Scope, Name: v.Name.ToUpperInvariant())))
        {
            var items = group.ToList();
            if (items.Count > 1 && items.Any(v => v.Op is VarOp.Set or VarOp.Remove))
                errors.Add($"{group.Key.Scope}\\{items[0].Name}: Set/Remove cannot be combined with other entries for the same variable.");
        }
        return errors;
    }

    public static void ThrowIfInvalid(Profile profile)
    {
        var errors = Validate(profile);
        if (errors.Count > 0)
            throw new DeckException($"Profile '{profile.Name}' is invalid:{Environment.NewLine}  " +
                                    string.Join(Environment.NewLine + "  ", errors));
    }
}
