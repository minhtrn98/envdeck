namespace EnvDeck.Core;

/// <summary>Turns variables of the current environment into profile entries.</summary>
public static class VariableCapture
{
    /// <summary>A Set entry carrying the variable's whole current value.</summary>
    public static ProfileVariable FromValue(EnvScope scope, EnvValue current, bool portable)
    {
        var value = portable ? Portability.Portabilize(current.Value) : current.Value;
        var kind = value != current.Value ? ValueKind.ExpandString : current.Kind;
        return new ProfileVariable
        {
            Name = current.Name,
            Value = value,
            Scope = scope,
            Kind = kind == ChangePlanner.AutoKind(value) ? null : kind,
        };
    }

    /// <summary>
    /// A Prepend entry with only some entries of a list variable (e.g. two folders of Path),
    /// so applying the profile adds them instead of overwriting the whole list.
    /// </summary>
    public static ProfileVariable FromEntries(EnvScope scope, string name, IEnumerable<string> entries, bool portable) => new()
    {
        Name = name,
        Scope = scope,
        Op = VarOp.Prepend,
        Value = PathList.Join(PathList.Distinct(entries.Select(e => portable ? Portability.Portabilize(e) : e))),
    };
}
