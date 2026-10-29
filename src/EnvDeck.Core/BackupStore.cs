using System.Globalization;

namespace EnvDeck.Core;

public sealed class BackupStore(DeckPaths paths, DeckSettings settings)
{
    public EnvSnapshot Create(IEnvStore env, IEnumerable<EnvScope> scopes, string? note, string reason, bool auto, DeckState? state)
    {
        var snapshot = new EnvSnapshot
        {
            Id = NewId(),
            CreatedAt = DateTimeOffset.Now,
            MachineName = Environment.MachineName,
            UserName = Environment.UserName,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            Reason = reason,
            Auto = auto,
            Applied = state?.Applied.Select(a => a.Clone()).ToList(),
        };
        foreach (var scope in scopes.Distinct())
        {
            var values = env.GetAll(scope).ToList();
            if (scope == EnvScope.User) snapshot.UserVariables = values;
            else snapshot.MachineVariables = values;
        }
        Save(snapshot);
        if (auto) PruneAuto();
        return snapshot;
    }

    public void Save(EnvSnapshot snapshot) => DeckJson.Write(FileOf(snapshot.Id), snapshot);

    public IReadOnlyList<EnvSnapshot> List()
    {
        if (!Directory.Exists(paths.BackupsDir)) return [];
        var result = new List<EnvSnapshot>();
        foreach (var file in Directory.EnumerateFiles(paths.BackupsDir, "*.json"))
        {
            try
            {
                var snapshot = DeckJson.Read<EnvSnapshot>(file);
                snapshot.Id = Path.GetFileNameWithoutExtension(file);
                result.Add(snapshot);
            }
            catch (DeckException)
            {
                // Skip unreadable files; they stay on disk for manual inspection.
            }
        }
        return result.OrderByDescending(s => s.CreatedAt).ThenByDescending(s => s.Id, StringComparer.Ordinal).ToList();
    }

    public bool Exists(string id) => IsSafeId(id) && File.Exists(FileOf(id));

    /// <summary>Accepts a full id, a unique prefix, or "latest".</summary>
    public EnvSnapshot Get(string id)
    {
        if (string.Equals(id, "latest", StringComparison.OrdinalIgnoreCase))
            return List().FirstOrDefault() ?? throw new DeckException("There are no backups yet.");

        if (Exists(id)) return Load(id);

        var matches = List().Where(s => s.Id.StartsWith(id, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new DeckException($"Backup '{id}' does not exist."),
            _ => throw new DeckException($"'{id}' matches {matches.Count} backups; use a longer id."),
        };
    }

    public void Delete(string id) => File.Delete(FileOf(Get(id).Id));

    EnvSnapshot Load(string id)
    {
        var snapshot = DeckJson.Read<EnvSnapshot>(FileOf(id));
        snapshot.Id = id;
        return snapshot;
    }

    void PruneAuto()
    {
        foreach (var old in List().Where(s => s.Auto).Skip(Math.Max(1, settings.AutoBackupLimit)))
            File.Delete(FileOf(old.Id));
    }

    string NewId()
    {
        var baseId = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var id = baseId;
        for (var i = 2; File.Exists(FileOf(id)); i++) id = $"{baseId}-{i}";
        return id;
    }

    string FileOf(string id)
    {
        if (!IsSafeId(id)) throw new DeckException($"Invalid backup id '{id}'.");
        return Path.Combine(paths.BackupsDir, id + ".json");
    }

    static bool IsSafeId(string id) =>
        id.Length > 0 && id.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !id.Contains("..");
}

public sealed class StateStore(DeckPaths paths)
{
    public DeckState Load() => File.Exists(paths.StateFile) ? DeckJson.Read<DeckState>(paths.StateFile) : new DeckState();

    public void Save(DeckState state) => DeckJson.Write(paths.StateFile, state);
}
