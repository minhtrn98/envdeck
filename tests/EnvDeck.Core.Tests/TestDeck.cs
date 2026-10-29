namespace EnvDeck.Core.Tests;

/// <summary>A DeckService over an in-memory environment and a throwaway data folder.</summary>
sealed class TestDeck : IDisposable
{
    public TestDeck(DeckSettings? settings = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "envdeck-tests", Guid.NewGuid().ToString("N"));
        Env = new MemoryEnvStore();
        Settings = settings ?? new DeckSettings();
        Service = Create();
    }

    public string Root { get; }

    public MemoryEnvStore Env { get; }

    public DeckSettings Settings { get; }

    public DeckService Service { get; private set; }

    public DeckPaths Paths => Service.Paths;

    /// <summary>Simulates starting EnvDeck again (re-runs startup work such as syncing hand edits).</summary>
    public DeckService Restart() => Service = Create();

    public Profile Save(string name, params ProfileVariable[] variables) =>
        Service.Profiles.Save(new Profile { Name = name, Variables = variables.ToList() }).Profile;

    public Profile Save(Profile profile) => Service.Profiles.Save(profile).Profile;

    public string? Get(string name, EnvScope scope = EnvScope.User) => Env.Get(scope, name)?.Value;

    public void Set(string name, string value, EnvScope scope = EnvScope.User, ValueKind kind = ValueKind.String) =>
        Env.Set(scope, name, value, kind);

    public static ProfileVariable Var(string name, string? value, VarOp op = VarOp.Set, EnvScope scope = EnvScope.User) =>
        new() { Name = name, Value = value, Op = op, Scope = scope };

    DeckService Create() => new(new DeckPaths(Path.Combine(Root, "data"), Path.Combine(Root, "state")), Settings, Env);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
