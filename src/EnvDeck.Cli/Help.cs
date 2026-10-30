using EnvDeck.Core;

namespace EnvDeck.Cli;

static class Help
{
    static readonly (string Command, string Usage, string Summary)[] Entries =
    [
        ("list", "list", "List profiles and which ones are on."),
        ("show", "show <profile>[@version] [--json]", "Show a profile (or an old version)."),
        ("new", "new <name> [--desc text] [--group name] [--tags a,b] [--from profile]", "Create a profile."),
        ("set", "set <profile> <NAME> [VALUE] [--scope user|machine] [--op set|remove|prepend|append] [--kind string|expandstring] [--note text]",
            "Add or change a variable in a profile. Prepend/Append add ';'-separated entries to PATH-like variables."),
        ("unset", "unset <profile> <NAME> [--scope user|machine]", "Remove a variable entry from a profile (use 'set NAME --op remove' to delete it on apply)."),
        ("capture", "capture <profile> <NAME>... [--scope user|machine] [--portable] [--desc text]",
            "Copy current variables into a profile. --portable rewrites C:\\Users\\you\\... as %USERPROFILE%\\..."),
        ("rename", "rename <profile> <new-name>", "Rename a profile (history and on/off state follow)."),
        ("delete", "delete <profile> [--purge]", "Delete a profile; --purge also deletes its history."),
        ("edit", "edit <profile>", "Open the profile JSON in your editor; saved edits become a new version."),
        ("apply", "apply <profile>... [--dry-run]", "Turn profiles on (alias: on). Profiles in the same group replace each other."),
        ("unapply", "unapply <profile>... | --all [--dry-run]", "Turn profiles off and restore what they changed (alias: off)."),
        ("status", "status", "Show profiles that are on and whether something changed them since."),
        ("history", "history <profile>", "List saved versions of a profile."),
        ("rollback", "rollback <profile> <version>", "Make an old version current (saved as a new version)."),
        ("backup", "backup [--scope user|machine|all] [--note text]", "Snapshot the environment."),
        ("backups", "backups | backups rm <id>", "List or delete backups."),
        ("restore", "restore <id|latest> [--scope user|machine|all] [--prune] [--dry-run]",
            "Restore variables from a backup; --prune also deletes variables not in the backup."),
        ("export", "export <file.envdeck|file.zip> [profile...] [--with-history] [--with-backups] [--encrypt] [--password <pw>]",
            "Bundle profiles to move to another machine. .envdeck files are encrypted (asks for a password; ENVDECK_PASSWORD also works)."),
        ("import", "import <file.envdeck|file.zip|profile.json> [--overwrite] [--password <pw>]", "Import a bundle (asks for the password when it is encrypted) or a single profile."),
        ("search", "search <text> [--regex] [--keys] [--values] [--in profiles,env,backups]", "Find variables by name and/or value."),
        ("diff", "diff <left> <right> [--show-same]",
            "Compare two of: <profile>, <profile>@<version>, env, env:user, env:machine, backup:<id>, file.json."),
        ("shell", "shell <profile>... [--format pwsh|cmd]",
            "Print a script that loads profiles into the current shell only: envdeck shell java17 | Invoke-Expression"),
        ("env", "env [name-filter] [--scope user|machine]", "Show current variables; a single PATH-like match is listed entry by entry."),
        ("doctor", "doctor", "Check PATH (missing/duplicate entries), profiles and drift."),
        ("config", "config [key] [value]", "Show or change settings: dataDir, historyLimit, autoBackup, autoBackupLimit, editor."),
        ("ui", "ui", "Open the EnvDeck window."),
    ];

    public static void Print(string? command)
    {
        var aliases = new Dictionary<string, string> { ["ls"] = "list", ["rm"] = "delete", ["on"] = "apply", ["off"] = "unapply", ["find"] = "search", ["compare"] = "diff" };
        if (command is not null && aliases.TryGetValue(command, out var real)) command = real;

        var entry = Entries.FirstOrDefault(e => e.Command == command);
        if (entry.Command is not null)
        {
            Out.Line("usage: envdeck " + entry.Usage, ConsoleColor.Cyan);
            Out.Line(entry.Summary);
            return;
        }

        Out.Line($"EnvDeck {DeckService.ToolVersion} — profile-based Windows environment variables", ConsoleColor.Cyan);
        Out.Line();
        Out.Line("usage: envdeck <command> [arguments]   (envdeck help <command> for details)");
        Out.Line();
        foreach (var (cmd, _, summary) in Entries) Out.Line($"  {cmd,-10} {summary}");
        Out.Line();
        Out.Dim("Environment: ENVDECK_HOME=<folder> overrides the data folder; ENVDECK_SANDBOX=1 writes to a sandbox registry key.");
        Out.Dim("Exit codes: 0 ok, 1 error, 2 usage, 3 needs administrator.");
    }
}
