namespace EnvDeck.Core;

/// <summary>
/// DataDir holds what you want to keep and sync (profiles, history, backups).
/// StateDir holds what belongs to this machine only (settings, which profiles are on).
/// </summary>
public sealed class DeckPaths(string dataDir, string stateDir)
{
    public string DataDir { get; } = Path.GetFullPath(dataDir);

    public string StateDir { get; } = Path.GetFullPath(stateDir);

    public string ProfilesDir => Path.Combine(DataDir, "profiles");

    public string HistoryDir => Path.Combine(DataDir, "history");

    public string BackupsDir => Path.Combine(DataDir, "backups");

    public string SchemaFile => Path.Combine(DataDir, "profile.schema.json");

    public string StateFile => Path.Combine(StateDir, "state.json");

    public string SettingsFile => Path.Combine(StateDir, "settings.json");

    public static string DefaultStateDir(bool sandbox)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EnvDeck");
        return sandbox ? Path.Combine(root, "sandbox") : root;
    }

    public static string DefaultDataDir(bool sandbox) =>
        sandbox
            ? Path.Combine(DefaultStateDir(sandbox: true), "data")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".envdeck");
}
