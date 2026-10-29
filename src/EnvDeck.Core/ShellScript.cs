using System.Text;
using System.Text.RegularExpressions;

namespace EnvDeck.Core;

public enum ShellFormat { Pwsh, Cmd }

/// <summary>
/// Renders profiles as a script that changes only the current shell session (nothing is written to the registry),
/// e.g. <c>envdeck shell java17 | Invoke-Expression</c>.
/// </summary>
public static partial class ShellScript
{
    [GeneratedRegex("%([^%;]+)%")]
    private static partial Regex VariableReference();

    public static string Render(IEnumerable<Profile> profiles, ShellFormat format)
    {
        // Values set earlier in the script win over the current process, so %JAVA_HOME%\bin
        // resolves to the JAVA_HOME the same profile just set.
        var overlay = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder();
        foreach (var profile in profiles)
        {
            sb.AppendLine(format == ShellFormat.Pwsh
                ? $"# envdeck: {profile.Name} v{profile.Version}"
                : $"rem envdeck: {profile.Name} v{profile.Version}");

            foreach (var v in profile.Variables)
            {
                var value = v.Value is null ? "" : Expand(v.Value, overlay);
                sb.AppendLine(format == ShellFormat.Pwsh ? Pwsh(v, value) : Cmd(v, value));
                if (v.Op == VarOp.Set) overlay[v.Name] = value;
                else if (v.Op == VarOp.Remove) overlay[v.Name] = null;
            }
        }
        return sb.ToString();
    }

    static string Expand(string value, Dictionary<string, string?> overlay) =>
        VariableReference().Replace(value, m =>
        {
            var name = m.Groups[1].Value;
            var resolved = overlay.TryGetValue(name, out var set) ? set : Environment.GetEnvironmentVariable(name);
            return resolved ?? m.Value;
        });

    static string Pwsh(ProfileVariable v, string value)
    {
        // ${env:...} works for names such as ProgramFiles(x86) that the $env:NAME form cannot express.
        var env = $"${{env:{v.Name}}}";
        return v.Op switch
        {
            VarOp.Set => $"{env} = {Quote(value)}",
            VarOp.Remove => $"Remove-Item -LiteralPath {Quote("Env:" + v.Name)} -ErrorAction SilentlyContinue",
            VarOp.Prepend => $"{env} = {Quote(value + ";")} + {env}",
            _ => $"{env} = {env} + {Quote(";" + value)}",
        };

        static string Quote(string s) => "'" + s.Replace("'", "''") + "'";
    }

    static string Cmd(ProfileVariable v, string value) => v.Op switch
    {
        VarOp.Set => $"set \"{v.Name}={value}\"",
        VarOp.Remove => $"set \"{v.Name}=\"",
        VarOp.Prepend => $"set \"{v.Name}={value};%{v.Name}%\"",
        _ => $"set \"{v.Name}=%{v.Name}%;{value}\"",
    };
}

public static class Portability
{
    static readonly string[] KnownFolders =
        ["LOCALAPPDATA", "APPDATA", "USERPROFILE", "ProgramFiles(x86)", "ProgramFiles", "ProgramData", "SystemRoot"];

    /// <summary>
    /// Rewrites machine-specific prefixes as %VAR% references (C:\Users\me\go → %USERPROFILE%\go),
    /// so a profile still works on another machine or user account.
    /// </summary>
    public static string Portabilize(string value)
    {
        var folders = KnownFolders
            .Select(name => (Name: name, Path: Environment.GetEnvironmentVariable(name)?.TrimEnd('\\')))
            .Where(f => !string.IsNullOrEmpty(f.Path))
            .OrderByDescending(f => f.Path!.Length) // most specific first: LOCALAPPDATA before USERPROFILE
            .ToList();

        var entries = value.Split(';');
        for (var i = 0; i < entries.Length; i++)
        {
            foreach (var (name, path) in folders)
            {
                var e = entries[i];
                if (e.StartsWith(path!, StringComparison.OrdinalIgnoreCase)
                    && (e.Length == path!.Length || e[path.Length] is '\\' or '/'))
                {
                    entries[i] = $"%{name}%{e[path.Length..]}";
                    break;
                }
            }
        }
        return string.Join(';', entries);
    }
}
