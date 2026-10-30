using System.Diagnostics;
using System.Globalization;
using EnvDeck.Core;

namespace EnvDeck.Cli;

static class Commands
{
    const string SecretMask = "••••••••";

    static string DisplayValue(DeckService service, string name, string? value) =>
        string.IsNullOrEmpty(value) ? "" : service.IsSecret(name) ? SecretMask : value;

    // ---------------------------------------------------------------- profiles

    public static int List(DeckService svc, CliArgs a)
    {
        a.EnsureNoUnknownOptions();
        var active = svc.State.Load().Applied.Select(x => x.Profile).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var profiles = svc.Profiles.List();
        if (profiles.Count == 0)
            Out.Dim("No profiles yet. Create one with: envdeck new <name>   (or: envdeck capture <name> JAVA_HOME ...)");
        else
            Out.Table(["NAME", "VER", "ON", "GROUP", "VARS", "DESCRIPTION"],
                profiles.Select(p => Row(p.Name, $"v{p.Version}", active.Contains(p.Name) ? "on" : "",
                    p.Group ?? "", p.Variables.Count.ToString(CultureInfo.InvariantCulture), p.Description ?? "")));
        foreach (var error in svc.Profiles.LoadErrors) Out.Warn(error);
        Out.Dim($"Data: {svc.Paths.DataDir}");
        return 0;
    }

    public static int Show(DeckService svc, CliArgs a)
    {
        var reference = a.Arg(0, "profile");
        var json = a.Flag("json");
        a.EnsureNoUnknownOptions();

        var p = svc.Profiles.GetRef(reference);
        if (json)
        {
            Out.Line(DeckJson.Serialize(p));
            return 0;
        }

        Out.Write(p.Name, ConsoleColor.Cyan);
        Out.Write($"  v{p.Version}", ConsoleColor.DarkGray);
        if (svc.IsActive(p.Name)) Out.Write("  on", ConsoleColor.Green);
        if (!string.IsNullOrEmpty(p.Group)) Out.Write($"  group: {p.Group}", ConsoleColor.DarkGray);
        Out.Line();
        if (!string.IsNullOrEmpty(p.Description)) Out.Line(p.Description);
        if (p.Tags is { Count: > 0 }) Out.Dim("tags: " + string.Join(", ", p.Tags));
        Out.Line();
        if (p.Variables.Count == 0) Out.Dim("(no variables)   add one: envdeck set " + p.Name + " NAME VALUE");
        else
            Out.Table(["SCOPE", "OP", "NAME", "VALUE"],
                p.Variables.Select(v => Row(v.Scope.ToString(), v.Op.ToString(), v.Name, DisplayValue(svc, v.Name, v.Value))), 100);
        return 0;
    }

    public static int New(DeckService svc, CliArgs a)
    {
        var name = a.Arg(0, "name");
        var from = a.Option("from");
        var desc = a.Option("desc");
        var group = a.Option("group");
        var tags = a.Option("tags");
        a.EnsureNoUnknownOptions();

        ProfileStore.ValidateName(name);
        if (svc.Profiles.Exists(name)) throw new DeckException($"Profile '{name}' already exists.");

        var profile = from is null ? new Profile() : svc.Profiles.GetRef(from).Clone();
        profile.Name = name;
        profile.Version = 0;
        if (desc is not null) profile.Description = desc;
        if (group is not null) profile.Group = group;
        if (tags is not null) profile.Tags = tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        var saved = svc.Profiles.Save(profile).Profile;
        Out.Success($"Created '{saved.Name}' v{saved.Version}: {svc.Profiles.FileOf(saved.Name)}");
        return 0;
    }

    public static int Set(DeckService svc, CliArgs a)
    {
        var profileName = a.Arg(0, "profile");
        var name = a.Arg(1, "NAME");
        var value = a.ArgOrNull(2);
        var scope = Scope(a.Option("scope")) ?? EnvScope.User;
        var op = a.Option("op") is { } o ? ParseEnum<VarOp>(o, "op") : VarOp.Set;
        var kind = a.Option("kind") is { } k ? ParseEnum<ValueKind>(k, "kind") : (ValueKind?)null;
        var note = a.Option("note");
        a.EnsureNoUnknownOptions();
        if (op != VarOp.Remove && value is null) throw new UsageException("Missing <VALUE>.");

        var profile = svc.Profiles.Get(profileName);
        bool SameVar(ProfileVariable v) => v.Scope == scope && string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase);
        Func<ProfileVariable, bool> replaced = op is VarOp.Set or VarOp.Remove
            ? SameVar
            : v => SameVar(v) && (v.Op is VarOp.Set or VarOp.Remove || (v.Op == op && v.Value == value));

        var index = profile.Variables.FindIndex(v => replaced(v));
        profile.Variables.RemoveAll(v => replaced(v));
        var variable = new ProfileVariable
        {
            Name = name,
            Value = op == VarOp.Remove ? null : value,
            Scope = scope,
            Op = op,
            Kind = kind,
            Note = note,
        };
        profile.Variables.Insert(index < 0 ? profile.Variables.Count : Math.Min(index, profile.Variables.Count), variable);

        var (saved, changed) = svc.Profiles.Save(profile);
        if (!changed)
        {
            Out.Dim("No change.");
            return 0;
        }
        Out.Success($"{saved.Name} v{saved.Version}: {op} {scope}\\{name}");
        HintReapply(svc, saved.Name);
        return 0;
    }

    public static int Unset(DeckService svc, CliArgs a)
    {
        var profileName = a.Arg(0, "profile");
        var name = a.Arg(1, "NAME");
        var scope = Scope(a.Option("scope"));
        a.EnsureNoUnknownOptions();

        var profile = svc.Profiles.Get(profileName);
        var removed = profile.Variables.RemoveAll(v =>
            string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase) && (scope is null || v.Scope == scope));
        if (removed == 0) throw new DeckException($"'{name}' is not in profile '{profileName}'.");

        var saved = svc.Profiles.Save(profile).Profile;
        Out.Success($"{saved.Name} v{saved.Version}: removed {removed} entr{(removed == 1 ? "y" : "ies")} for {name}");
        HintReapply(svc, saved.Name);
        return 0;
    }

    public static int Capture(DeckService svc, CliArgs a)
    {
        var profileName = a.Arg(0, "profile");
        var scope = Scope(a.Option("scope")) ?? EnvScope.User;
        var portable = a.Flag("portable");
        var desc = a.Option("desc");
        a.EnsureNoUnknownOptions();
        var names = a.Positionals.Skip(1).ToList();
        if (names.Count == 0) throw new UsageException("Give at least one variable NAME to capture.");

        var (profile, warnings) = svc.Capture(profileName, names.Select(n => (scope, n)), portable, desc);
        foreach (var w in warnings) Out.Warn(w);
        Out.Success($"{profile.Name} v{profile.Version}: captured {names.Count - warnings.Count} variable(s) from {scope}");
        return 0;
    }

    public static int Rename(DeckService svc, CliArgs a)
    {
        var oldName = a.Arg(0, "profile");
        var newName = a.Arg(1, "new-name");
        a.EnsureNoUnknownOptions();
        var renamed = svc.RenameProfile(oldName, newName);
        Out.Success($"Renamed '{oldName}' to '{renamed.Name}' (v{renamed.Version}).");
        return 0;
    }

    public static int Delete(DeckService svc, CliArgs a)
    {
        var name = a.Arg(0, "profile");
        var purge = a.Flag("purge");
        a.EnsureNoUnknownOptions();
        svc.DeleteProfile(name, purge);
        Out.Success(purge ? $"Deleted '{name}' and its history." : $"Deleted '{name}' (history kept; --purge removes it).");
        return 0;
    }

    public static int Edit(DeckService svc, CliArgs a)
    {
        var name = a.Arg(0, "profile");
        a.EnsureNoUnknownOptions();
        var file = svc.Profiles.FileOf(svc.Profiles.Get(name).Name);

        var editor = svc.Settings.Editor ?? FindOnPath("code.cmd") ?? "notepad.exe";
        Process.Start(new ProcessStartInfo(editor, $"\"{file}\"") { UseShellExecute = true });
        Out.Line($"Opened {file}");
        Out.Dim("Saved edits become a new version automatically the next time EnvDeck runs.");
        return 0;
    }

    // ---------------------------------------------------------------- on / off

    public static int Apply(DeckService svc, CliArgs a)
    {
        var dryRun = a.Flag("dry-run");
        a.EnsureNoUnknownOptions();
        if (a.Positionals.Count == 0) throw new UsageException("Give the profile(s) to turn on.");

        foreach (var name in a.Positionals)
        {
            var result = svc.Apply(name, dryRun);
            foreach (var off in result.TurnedOff.Where(o => !o.Equals(result.Target, StringComparison.OrdinalIgnoreCase)))
                Out.Line($"{(dryRun ? "Would turn off" : "Turned off")} '{off}' (same group)", ConsoleColor.DarkYellow);
            PrintResult(result, dryRun ? "Would turn on" : "Turned on");
        }
        if (!dryRun)
            Out.Dim($"New terminals and apps see the change. For this terminal: envdeck shell {string.Join(' ', a.Positionals)} | Invoke-Expression");
        return 0;
    }

    public static int Unapply(DeckService svc, CliArgs a)
    {
        var dryRun = a.Flag("dry-run");
        var all = a.Flag("all");
        a.EnsureNoUnknownOptions();

        var names = all
            ? svc.State.Load().Applied.Select(x => x.Profile).Reverse().ToList()
            : a.Positionals;
        if (names.Count == 0)
        {
            if (all)
            {
                Out.Dim("No profile is on.");
                return 0;
            }
            throw new UsageException("Give the profile(s) to turn off, or --all.");
        }
        foreach (var name in names) PrintResult(svc.Unapply(name, dryRun), dryRun ? "Would turn off" : "Turned off");
        return 0;
    }

    public static int Status(DeckService svc, CliArgs a)
    {
        a.EnsureNoUnknownOptions();
        var status = svc.Status();
        if (status.Count == 0)
        {
            Out.Dim("No profile is on.");
            return 0;
        }
        foreach (var s in status)
        {
            Out.Write("● ", ConsoleColor.Green);
            Out.Write(s.Entry.Profile, ConsoleColor.Cyan);
            Out.Write($"  v{s.Entry.Version}  since {s.Entry.AppliedAt.LocalDateTime:yyyy-MM-dd HH:mm}", ConsoleColor.DarkGray);
            if (s.Entry.Group is not null) Out.Write($"  group: {s.Entry.Group}", ConsoleColor.DarkGray);
            Out.Line();
            if (s.ProfileMissing) Out.Warn("profile file deleted; 'envdeck off' still works from the journal");
            else if (s.Outdated) Out.Warn($"profile is now v{s.CurrentVersion}; run 'envdeck apply {s.Entry.Profile}' to update");
            if (s.Drift.Count > 0) Out.Warn("changed outside EnvDeck: " + string.Join(", ", s.Drift));
        }
        return 0;
    }

    // ---------------------------------------------------------------- versions

    public static int History(DeckService svc, CliArgs a)
    {
        var name = a.Arg(0, "profile");
        a.EnsureNoUnknownOptions();
        var current = svc.Profiles.TryGet(name)?.Version;
        var history = svc.Profiles.History(name);
        if (history.Count == 0)
        {
            Out.Dim($"No history for '{name}'.");
            return 0;
        }
        Out.Table(["VERSION", "SAVED", "VARS", ""],
            history.Select(h => Row($"v{h.Version}", h.SavedAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "?",
                h.VariableCount.ToString(CultureInfo.InvariantCulture), h.Version == current ? "current" : "")));
        Out.Dim($"Compare: envdeck diff {name}@<version> {name}    Restore: envdeck rollback {name} <version>");
        return 0;
    }

    public static int Rollback(DeckService svc, CliArgs a)
    {
        var name = a.Arg(0, "profile");
        var versionText = a.Arg(1, "version").TrimStart('v', 'V');
        a.EnsureNoUnknownOptions();
        if (!int.TryParse(versionText, NumberStyles.None, CultureInfo.InvariantCulture, out var version))
            throw new UsageException($"Invalid version '{versionText}'.");

        var (saved, changed) = svc.Profiles.Rollback(name, version);
        if (!changed) Out.Dim($"'{name}' already has the content of v{version}.");
        else Out.Success($"'{name}' restored from v{version} as v{saved.Version}.");
        HintReapply(svc, name);
        return 0;
    }

    // ---------------------------------------------------------------- backup / restore

    public static int Backup(DeckService svc, CliArgs a)
    {
        var scopes = Scopes(a.Option("scope"));
        var note = a.Option("note");
        a.EnsureNoUnknownOptions();
        var snapshot = svc.Backup(scopes, note);
        Out.Success($"Backup {snapshot.Id}: {snapshot.UserVariables?.Count ?? 0} user, {snapshot.MachineVariables?.Count ?? 0} machine variables");
        Out.Dim(Path.Combine(svc.Paths.BackupsDir, snapshot.Id + ".json"));
        return 0;
    }

    public static int Backups(DeckService svc, CliArgs a)
    {
        if (a.ArgOrNull(0) is "rm" or "delete")
        {
            var id = a.Arg(1, "backup-id");
            a.EnsureNoUnknownOptions();
            var snapshot = svc.Backups.Get(id);
            svc.Backups.Delete(snapshot.Id);
            Out.Success($"Deleted backup {snapshot.Id}.");
            return 0;
        }
        a.EnsureNoUnknownOptions();
        var list = svc.Backups.List();
        if (list.Count == 0)
        {
            Out.Dim("No backups yet. Create one with: envdeck backup --note \"before reinstall\"");
            return 0;
        }
        Out.Table(["ID", "CREATED", "USER", "MACHINE", "HOST", "NOTE"],
            list.Select(b => Row(b.Id, b.CreatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                b.UserVariables?.Count.ToString(CultureInfo.InvariantCulture) ?? "-",
                b.MachineVariables?.Count.ToString(CultureInfo.InvariantCulture) ?? "-",
                b.MachineName ?? "", b.Note ?? b.Reason ?? "")));
        return 0;
    }

    public static int Restore(DeckService svc, CliArgs a)
    {
        var id = a.Arg(0, "backup-id");
        var scopeText = a.Option("scope");
        var prune = a.Flag("prune");
        var dryRun = a.Flag("dry-run");
        a.EnsureNoUnknownOptions();

        var result = svc.Restore(id, scopeText is null ? null : Scopes(scopeText), prune, dryRun);
        PrintResult(result, dryRun ? "Would restore" : "Restored");
        return 0;
    }

    // ---------------------------------------------------------------- move between machines

    public static int Export(DeckService svc, CliArgs a)
    {
        var file = a.Arg(0, "file");
        var withHistory = a.Flag("with-history");
        var withBackups = a.Flag("with-backups");
        var encrypt = a.Flag("encrypt");
        var password = a.Option("password") ?? Environment.GetEnvironmentVariable("ENVDECK_PASSWORD");
        a.EnsureNoUnknownOptions();

        // A .envdeck file, --encrypt or a given password all mean "protect it"; a plain .zip is readable by anyone.
        encrypt |= password is not null || file.EndsWith(".envdeck", StringComparison.OrdinalIgnoreCase);
        if (encrypt && password is null)
        {
            password = Password.Ask("Password for the bundle: ");
            if (password != Password.Ask("Repeat the password: ")) throw new DeckException("The passwords do not match.");
        }

        var manifest = svc.Export(file, a.Positionals.Skip(1).ToList(), withHistory, withBackups, encrypt ? password : null);
        Out.Success($"Exported {manifest.Profiles.Count} profile(s){(withHistory ? " with history" : "")}{(withBackups ? " and backups" : "")} to {Path.GetFullPath(file)}");
        if (encrypt)
            Out.Dim("Encrypted (AES-256-GCM). There is no way to recover the password; without it the file cannot be opened.");
        else
            Out.Warn("The bundle is not encrypted: variable values, secrets included, are readable. Use --encrypt (or a .envdeck file name) to protect it.");
        Out.Dim("On the other machine: envdeck import " + Path.GetFileName(file));
        return 0;
    }

    public static int Import(DeckService svc, CliArgs a)
    {
        var file = a.Arg(0, "file");
        var overwrite = a.Flag("overwrite");
        var given = a.Option("password") ?? Environment.GetEnvironmentVariable("ENVDECK_PASSWORD");
        a.EnsureNoUnknownOptions();

        BundleResult result;
        var password = given;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                result = svc.Import(file, overwrite, password);
                break;
            }
            catch (PasswordRequiredException)
            {
                password = Password.Ask("Password: ");
            }
            catch (WrongPasswordException) when (given is null && attempt < 3)
            {
                Out.Warn("Wrong password, or the file was changed or damaged. Try again.");
                password = Password.Ask("Password: ");
            }
        }

        foreach (var name in result.Imported) Out.Success($"  imported {name}");
        foreach (var name in result.Skipped) Out.Warn($"skipped {name} (already exists; use --overwrite to save it as a new version)");
        if (result.Backups.Count > 0) Out.Line($"  {result.Backups.Count} backup(s) imported");
        if (result.Imported.Count > 0) Out.Dim("Turn them on with: envdeck apply <profile>");
        return 0;
    }
    // ---------------------------------------------------------------- search / compare

    public static int Search(DeckService svc, CliArgs a)
    {
        var query = a.Arg(0, "text");
        var options = new SearchOptions { Regex = a.Flag("regex") };
        var keys = a.Flag("keys");
        var values = a.Flag("values");
        if (keys || values)
        {
            options.Names = keys;
            options.Values = values;
        }
        if (a.Option("in") is { } where)
        {
            var parts = where.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            options.Profiles = parts.Contains("profiles", StringComparer.OrdinalIgnoreCase);
            options.Environment = parts.Contains("env", StringComparer.OrdinalIgnoreCase);
            options.Backups = parts.Contains("backups", StringComparer.OrdinalIgnoreCase);
        }
        a.EnsureNoUnknownOptions();

        var hits = svc.Search(query, options);
        if (hits.Count == 0)
        {
            Out.Dim("No matches.");
            return 1;
        }
        Out.Table(["SOURCE", "SCOPE", "NAME", "VALUE"],
            hits.Select(h => Row(h.Source, h.Scope.ToString(), h.Name,
                (h.Op is { } op && op != VarOp.Set ? $"[{op}] " : "") + DisplayValue(svc, h.Name, h.Value))), 100);
        return 0;
    }

    public static int Diff(DeckService svc, CliArgs a)
    {
        var left = a.Arg(0, "left");
        var right = a.Arg(1, "right");
        var showSame = a.Flag("show-same");
        a.EnsureNoUnknownOptions();

        var (l, r, rows) = svc.Diff(left, right, showSame);
        Out.Line($"--- {l.Label}", ConsoleColor.Red);
        Out.Line($"+++ {r.Label}", ConsoleColor.Green);
        if (rows.Count == 0)
        {
            Out.Dim("(identical)");
            return 0;
        }
        foreach (var row in rows)
        {
            var key = $"{row.Scope}\\{row.Name}";
            var secret = svc.IsSecret(row.Name);
            switch (row.Kind)
            {
                case DiffKind.Same:
                    Out.Dim($"  = {key} = {DisplayValue(svc, row.Name, row.Left)}");
                    break;
                case DiffKind.Added:
                    Out.Line($"  + {key} = {DisplayValue(svc, row.Name, row.Right)}", ConsoleColor.Green);
                    break;
                case DiffKind.Removed:
                    Out.Line($"  - {key} = {DisplayValue(svc, row.Name, row.Left)}", ConsoleColor.Red);
                    break;
                case DiffKind.Changed:
                    Out.Line($"  ~ {key}", ConsoleColor.Yellow);
                    if (secret)
                    {
                        Out.Line($"      - {DisplayValue(svc, row.Name, row.Left)}", ConsoleColor.Red);
                        Out.Line($"      + {DisplayValue(svc, row.Name, row.Right)}", ConsoleColor.Green);
                    }
                    else if (PathList.Split(row.Left).Count > 1 || PathList.Split(row.Right).Count > 1)
                    {
                        foreach (var e in row.OnlyLeft) Out.Line($"      - {e}", ConsoleColor.Red);
                        foreach (var e in row.OnlyRight) Out.Line($"      + {e}", ConsoleColor.Green);
                        if (row.OnlyLeft.Count + row.OnlyRight.Count == 0) Out.Dim("      (order changed)");
                    }
                    else
                    {
                        Out.Line($"      - {row.Left}", ConsoleColor.Red);
                        Out.Line($"      + {row.Right}", ConsoleColor.Green);
                    }
                    break;
            }
        }
        var counts = rows.GroupBy(x => x.Kind).ToDictionary(g => g.Key, g => g.Count());
        Out.Dim($"{counts.GetValueOrDefault(DiffKind.Added)} added, {counts.GetValueOrDefault(DiffKind.Removed)} removed, {counts.GetValueOrDefault(DiffKind.Changed)} changed");
        return 0;
    }

    // ---------------------------------------------------------------- misc

    public static int Shell(DeckService svc, CliArgs a)
    {
        var format = a.Option("format") is { } f ? ParseEnum<ShellFormat>(f, "format") : ShellFormat.Pwsh;
        a.EnsureNoUnknownOptions();
        if (a.Positionals.Count == 0) throw new UsageException("Give the profile(s) to load into this shell.");
        Console.Write(ShellScript.Render(a.Positionals.Select(svc.Profiles.GetRef), format));
        return 0;
    }

    public static int Env(DeckService svc, CliArgs a)
    {
        var filter = a.ArgOrNull(0);
        var scope = Scope(a.Option("scope"));
        a.EnsureNoUnknownOptions();

        var rows = (scope is { } s ? [s] : Enum.GetValues<EnvScope>())
            .SelectMany(sc => svc.Env.GetAll(sc).Select(v => (Scope: sc, Value: v)))
            .Where(x => filter is null || x.Value.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // A single list variable (e.g. "envdeck env path") is easier to read one entry per line.
        if (rows.Count is 1 or 2 && rows.All(r => !svc.IsSecret(r.Value.Name) && PathList.Split(r.Value.Value).Count > 1))
        {
            foreach (var (sc, v) in rows)
            {
                Out.Line($"{sc}\\{v.Name} ({v.Kind})", ConsoleColor.Cyan);
                foreach (var e in PathList.Split(v.Value))
                {
                    var exists = Directory.Exists(Environment.ExpandEnvironmentVariables(e).Trim('"'));
                    Out.Line($"  {e}", exists ? ConsoleColor.Gray : ConsoleColor.DarkYellow);
                }
            }
            return 0;
        }
        Out.Table(["SCOPE", "NAME", "VALUE"], rows.Select(r => Row(r.Scope.ToString(), r.Value.Name, DisplayValue(svc, r.Value.Name, r.Value.Value))), 100);
        return 0;
    }

    public static int Doctor(DeckService svc, CliArgs a)
    {
        a.EnsureNoUnknownOptions();
        var issues = svc.Doctor();
        foreach (var issue in issues)
        {
            var (label, color) = issue.Severity switch
            {
                DoctorSeverity.Error => ("error", ConsoleColor.Red),
                DoctorSeverity.Warning => ("warn ", ConsoleColor.Yellow),
                _ => ("info ", ConsoleColor.DarkGray),
            };
            Out.Write(label + "  ", color);
            Out.Line(issue.Message);
        }
        if (issues.Count == 0) Out.Success("Everything looks fine.");
        return issues.Any(i => i.Severity == DoctorSeverity.Error) ? 1 : 0;
    }

    public static int Config(DeckService svc, CliArgs a)
    {
        var key = a.ArgOrNull(0);
        var value = a.ArgOrNull(1);
        a.EnsureNoUnknownOptions();
        var s = svc.Settings;

        if (key is null)
        {
            Out.Table(["KEY", "VALUE"],
            [
                Row("dataDir", s.DataDir ?? $"(default) {svc.Paths.DataDir}"),
                Row("historyLimit", s.HistoryLimit.ToString(CultureInfo.InvariantCulture)),
                Row("autoBackup", s.AutoBackup.ToString().ToLowerInvariant()),
                Row("autoBackupLimit", s.AutoBackupLimit.ToString(CultureInfo.InvariantCulture)),
                Row("editor", s.Editor ?? "(auto: VS Code, else Notepad)"),
            ]);
            Out.Dim($"Settings: {svc.Paths.SettingsFile}");
            Out.Dim($"State:    {svc.Paths.StateFile}");
            if (Environment.GetEnvironmentVariable("ENVDECK_HOME") is { Length: > 0 }) Out.Dim("ENVDECK_HOME overrides dataDir.");
            return 0;
        }

        string Current() => key.ToLowerInvariant() switch
        {
            "datadir" => s.DataDir ?? "",
            "historylimit" => s.HistoryLimit.ToString(CultureInfo.InvariantCulture),
            "autobackup" => s.AutoBackup.ToString().ToLowerInvariant(),
            "autobackuplimit" => s.AutoBackupLimit.ToString(CultureInfo.InvariantCulture),
            "editor" => s.Editor ?? "",
            _ => throw new UsageException($"Unknown setting '{key}'. Keys: dataDir, historyLimit, autoBackup, autoBackupLimit, editor."),
        };

        if (value is null)
        {
            Out.Line(Current());
            return 0;
        }
        _ = Current(); // validates the key
        switch (key.ToLowerInvariant())
        {
            case "datadir": s.DataDir = value.Length == 0 ? null : Path.GetFullPath(Environment.ExpandEnvironmentVariables(value)); break;
            case "historylimit": s.HistoryLimit = PositiveInt(value); break;
            case "autobackup": s.AutoBackup = bool.TryParse(value, out var b) ? b : throw new UsageException("Use true or false."); break;
            case "autobackuplimit": s.AutoBackupLimit = PositiveInt(value); break;
            case "editor": s.Editor = value.Length == 0 ? null : value; break;
        }
        svc.SaveSettings();
        Out.Success($"{key} = {Current()}");
        if (key.Equals("dataDir", StringComparison.OrdinalIgnoreCase))
            Out.Dim("Existing profiles are not moved; copy the old folder or use export/import.");
        return 0;

        static int PositiveInt(string text) =>
            int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0
                ? n
                : throw new UsageException("Use a positive number.");
    }

    public static int Ui(CliArgs a)
    {
        a.EnsureNoUnknownOptions();
        var dir = AppContext.BaseDirectory;
        var exe = Path.Combine(dir, "EnvDeck.App.exe");
        var dll = Path.Combine(dir, "EnvDeck.App.dll");
        if (File.Exists(exe)) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        else if (File.Exists(dll)) Process.Start(new ProcessStartInfo("dotnet", $"\"{dll}\"") { UseShellExecute = false });
        else throw new DeckException($"EnvDeck.App was not found next to the CLI ({dir}).");
        return 0;
    }

    // ---------------------------------------------------------------- helpers

    static IReadOnlyList<string> Row(params string[] cells) => cells;

    static void PrintResult(OperationResult result, string verb)
    {
        Out.Line($"{verb} '{result.Target}'{(result.Version > 0 ? $" v{result.Version}" : "")}:", ConsoleColor.Cyan);
        Out.Changes(result.Changes);
        foreach (var w in result.Warnings) Out.Warn(w);
        if (result.BackupId is not null) Out.Dim($"  backup taken first: {result.BackupId} (envdeck restore {result.BackupId})");
    }

    static void HintReapply(DeckService svc, string name)
    {
        if (svc.IsActive(name)) Out.Dim($"'{name}' is on; run 'envdeck apply {name}' to update the environment.");
    }

    static EnvScope? Scope(string? text) => text is null ? null : DeckService.ParseScope(text);

    static EnvScope[] Scopes(string? text) =>
        text is null || text.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? Enum.GetValues<EnvScope>()
            : [DeckService.ParseScope(text)];

    static T ParseEnum<T>(string text, string option) where T : struct, Enum =>
        Enum.TryParse<T>(text, ignoreCase: true, out var value) && Enum.IsDefined(value)
            ? value
            : throw new UsageException($"Invalid --{option} '{text}'. Use one of: {string.Join(", ", Enum.GetNames<T>())}.");

    static string? FindOnPath(string file) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(';', StringSplitOptions.RemoveEmptyEntries)
        .Select(dir => Path.Combine(dir.Trim('"'), file))
        .FirstOrDefault(File.Exists);
}
