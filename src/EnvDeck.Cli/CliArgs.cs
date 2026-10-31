namespace EnvDeck.Cli;

sealed class UsageException(string message) : Exception(message);

/// <summary>Minimal parser: "command positional... --option value --flag".</summary>
sealed class CliArgs
{
    /// <summary>Options that never take a value, so "--dry-run name" keeps "name" as a positional.</summary>
    static readonly HashSet<string> Flags = new(StringComparer.OrdinalIgnoreCase)
    {
        "help", "version", "dry-run", "regex", "keys", "values", "prune", "overwrite", "with-history",
        "with-backups", "encrypt", "reveal", "portable", "json", "purge", "all", "show-same",
    };

    readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _used = new(StringComparer.OrdinalIgnoreCase);

    public string? Command { get; private set; }

    public List<string> Positionals { get; } = [];

    public static CliArgs Parse(string[] argv)
    {
        var args = new CliArgs();
        for (var i = 0; i < argv.Length; i++)
        {
            var token = argv[i];
            if (token == "--")
            {
                args.Positionals.AddRange(argv.Skip(i + 1));
                break;
            }
            if (token is "-h" or "/?") token = "--help";
            if (token.StartsWith("--", StringComparison.Ordinal) && token.Length > 2)
            {
                var name = token[2..];
                string? value = null;
                var eq = name.IndexOf('=');
                if (eq >= 0)
                {
                    value = name[(eq + 1)..];
                    name = name[..eq];
                }
                else if (!Flags.Contains(name))
                {
                    if (i + 1 >= argv.Length) throw new UsageException($"Option --{name} needs a value.");
                    value = argv[++i];
                }
                args._options[name] = value;
            }
            else if (args.Command is null)
            {
                args.Command = token.ToLowerInvariant();
            }
            else
            {
                args.Positionals.Add(token);
            }
        }
        return args;
    }

    public bool Flag(string name)
    {
        _used.Add(name);
        return _options.ContainsKey(name);
    }

    public string? Option(string name)
    {
        _used.Add(name);
        return _options.TryGetValue(name, out var v) ? v : null;
    }

    public string Arg(int index, string label) =>
        index < Positionals.Count ? Positionals[index] : throw new UsageException($"Missing <{label}>. Run 'envdeck help {Command}'.");

    public string? ArgOrNull(int index) => index < Positionals.Count ? Positionals[index] : null;

    /// <summary>Catches typos such as --scop: every option given must have been read by the command.</summary>
    public void EnsureNoUnknownOptions()
    {
        var unknown = _options.Keys.Where(k => !_used.Contains(k)).ToList();
        if (unknown.Count > 0) throw new UsageException($"Unknown option --{unknown[0]} for '{Command}'.");
    }
}
