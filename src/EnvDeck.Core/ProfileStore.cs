using System.Globalization;
using System.Text.RegularExpressions;

namespace EnvDeck.Core;

public sealed record ProfileVersionInfo(int Version, DateTimeOffset? SavedAt, int VariableCount, string FilePath);

/// <summary>
/// profiles/&lt;name&gt;.json is the current version; history/&lt;name&gt;/v0001.json... keeps every saved version.
/// Hand edits to the JSON files are picked up as new versions by <see cref="SyncExternalEdits"/>.
/// </summary>
public sealed partial class ProfileStore(DeckPaths paths, DeckSettings settings)
{
    public const string SchemaReference = "../profile.schema.json";

    static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex NameRegex();

    [GeneratedRegex(@"^v(\d+)\.json$", RegexOptions.IgnoreCase)]
    private static partial Regex HistoryFileRegex();

    /// <summary>Files under profiles/ that could not be parsed during the last <see cref="List"/>.</summary>
    public List<string> LoadErrors { get; } = [];

    public static bool IsValidName(string? name) =>
        name is not null && NameRegex().IsMatch(name) && !ReservedNames.Contains(name.Split('.')[0]);

    public static void ValidateName(string? name)
    {
        if (!IsValidName(name))
            throw new DeckException($"Invalid profile name '{name}'. Use letters, digits, '.', '_' or '-' (max 64).");
    }

    /// <summary>Parses "name" or "name@3".</summary>
    public static (string Name, int? Version) ParseRef(string reference)
    {
        var at = reference.LastIndexOf('@');
        if (at < 0) return (reference, null);
        var versionText = reference[(at + 1)..].TrimStart('v', 'V');
        if (!int.TryParse(versionText, NumberStyles.None, CultureInfo.InvariantCulture, out var version))
            throw new DeckException($"Invalid version in '{reference}'. Use name@<number>.");
        return (reference[..at], version);
    }

    public string FileOf(string name) => Path.Combine(paths.ProfilesDir, name + ".json");

    string HistoryDirOf(string name) => Path.Combine(paths.HistoryDir, name);

    public bool Exists(string name) => IsValidName(name) && File.Exists(FileOf(name));

    public IReadOnlyList<Profile> List()
    {
        LoadErrors.Clear();
        if (!Directory.Exists(paths.ProfilesDir)) return [];

        var result = new List<Profile>();
        foreach (var file in Directory.EnumerateFiles(paths.ProfilesDir, "*.json"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (!IsValidName(name))
            {
                LoadErrors.Add($"{file}: '{name}' is not a valid profile name.");
                continue;
            }
            try
            {
                result.Add(Get(name));
            }
            catch (DeckException ex)
            {
                LoadErrors.Add(ex.Message);
            }
        }
        result.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        return result;
    }

    public Profile Get(string name)
    {
        ValidateName(name);
        var file = FileOf(name);
        if (!File.Exists(file)) throw new DeckException($"Profile '{name}' does not exist.");
        var profile = DeckJson.Read<Profile>(file);
        profile.Name = name; // the file name is authoritative
        return profile;
    }

    public Profile? TryGet(string name) => Exists(name) ? Get(name) : null;

    /// <summary>Gets "name" (current) or "name@3" (a version from history).</summary>
    public Profile GetRef(string reference)
    {
        var (name, version) = ParseRef(reference);
        return version is { } v ? GetVersion(name, v) : Get(name);
    }

    /// <summary>Saves a new version when the content changed; returns the stored profile.</summary>
    public (Profile Profile, bool Changed) Save(Profile profile)
    {
        ValidateName(profile.Name);
        ProfileValidator.ThrowIfInvalid(profile);

        var current = TryGet(profile.Name);
        if (current is not null && ContentEquals(current, profile)) return (current, false);

        var saved = profile.Clone();
        saved.Schema = SchemaReference;
        saved.Version = Math.Max(current?.Version ?? 0, LatestHistoryVersion(profile.Name)) + 1;
        saved.UpdatedAt = DateTimeOffset.Now;
        WriteVersion(saved);
        return (saved, true);
    }

    public void Delete(string name, bool purgeHistory)
    {
        ValidateName(name);
        if (!File.Exists(FileOf(name))) throw new DeckException($"Profile '{name}' does not exist.");
        File.Delete(FileOf(name));
        if (purgeHistory && Directory.Exists(HistoryDirOf(name))) Directory.Delete(HistoryDirOf(name), recursive: true);
    }

    public Profile Rename(string oldName, string newName)
    {
        ValidateName(newName);
        var profile = Get(oldName);
        if (Exists(newName) || Directory.Exists(HistoryDirOf(newName)))
            throw new DeckException($"Profile '{newName}' already exists (or has history).");

        if (Directory.Exists(HistoryDirOf(oldName))) Directory.Move(HistoryDirOf(oldName), HistoryDirOf(newName));
        File.Delete(FileOf(oldName));
        profile.Name = newName;
        return Save(profile).Profile;
    }

    public IReadOnlyList<ProfileVersionInfo> History(string name)
    {
        ValidateName(name);
        var dir = HistoryDirOf(name);
        if (!Directory.Exists(dir)) return [];

        var result = new List<ProfileVersionInfo>();
        foreach (var (version, file) in HistoryFiles(name))
        {
            try
            {
                var p = DeckJson.Read<Profile>(file);
                result.Add(new ProfileVersionInfo(version, p.UpdatedAt, p.Variables.Count, file));
            }
            catch (DeckException)
            {
                result.Add(new ProfileVersionInfo(version, null, 0, file));
            }
        }
        return result.OrderByDescending(v => v.Version).ToList();
    }

    public Profile GetVersion(string name, int version)
    {
        ValidateName(name);
        var file = Path.Combine(HistoryDirOf(name), $"v{version:D4}.json");
        if (!File.Exists(file)) throw new DeckException($"Profile '{name}' has no version {version}.");
        var profile = DeckJson.Read<Profile>(file);
        profile.Name = name;
        profile.Version = version;
        return profile;
    }

    /// <summary>Makes an old version current again by saving it as a new version (history is never rewritten).</summary>
    public (Profile Profile, bool Changed) Rollback(string name, int version) => Save(GetVersion(name, version));

    /// <summary>
    /// Records a new version for every profile file that was edited by hand since its last saved version.
    /// Returns the names of the profiles that got a new version.
    /// </summary>
    public List<string> SyncExternalEdits()
    {
        var synced = new List<string>();
        if (!Directory.Exists(paths.ProfilesDir)) return synced;

        foreach (var file in Directory.EnumerateFiles(paths.ProfilesDir, "*.json"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (!IsValidName(name)) continue;
            try
            {
                var onDisk = Get(name);
                var latestVersion = LatestHistoryVersion(name);
                if (latestVersion > 0 && ContentEquals(onDisk, GetVersion(name, latestVersion))) continue;

                // Hand-edited (or brand new) file: keep the user's content, give it a fresh version number.
                onDisk.Schema = SchemaReference;
                onDisk.Version = latestVersion > 0 ? Math.Max(onDisk.Version, latestVersion) + 1 : Math.Max(onDisk.Version, 1);
                onDisk.UpdatedAt = DateTimeOffset.Now;
                WriteVersion(onDisk);
                synced.Add(name);
            }
            catch (DeckException)
            {
                // Broken JSON is reported by List(); keep the file untouched so the user can fix it.
            }
        }
        return synced;
    }

    /// <summary>Copies a profile and its history as-is (used by bundle import onto a fresh machine).</summary>
    internal void ImportWithHistory(Profile profile, IEnumerable<(int Version, string Json)> history)
    {
        foreach (var (version, json) in history)
        {
            var file = Path.Combine(HistoryDirOf(profile.Name), $"v{version:D4}.json");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, json);
        }
        if (LatestHistoryVersion(profile.Name) < profile.Version)
            DeckJson.Write(Path.Combine(HistoryDirOf(profile.Name), $"v{profile.Version:D4}.json"), profile);
        DeckJson.Write(FileOf(profile.Name), profile);
    }

    internal IEnumerable<(int Version, string File)> HistoryFiles(string name)
    {
        var dir = HistoryDirOf(name);
        if (!Directory.Exists(dir)) yield break;
        foreach (var file in Directory.EnumerateFiles(dir, "v*.json"))
        {
            var match = HistoryFileRegex().Match(Path.GetFileName(file));
            if (match.Success) yield return (int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), file);
        }
    }

    int LatestHistoryVersion(string name) => HistoryFiles(name).Select(h => h.Version).DefaultIfEmpty(0).Max();

    void WriteVersion(Profile profile)
    {
        DeckJson.Write(FileOf(profile.Name), profile);
        DeckJson.Write(Path.Combine(HistoryDirOf(profile.Name), $"v{profile.Version:D4}.json"), profile);
        PruneHistory(profile.Name);
    }

    void PruneHistory(string name)
    {
        var limit = Math.Max(1, settings.HistoryLimit);
        foreach (var (_, file) in HistoryFiles(name).OrderByDescending(h => h.Version).Skip(limit))
            File.Delete(file);
    }

    /// <summary>Compares what a profile does, ignoring bookkeeping fields (version, timestamps, schema).</summary>
    public static bool ContentEquals(Profile a, Profile b) => Fingerprint(a) == Fingerprint(b);

    static string Fingerprint(Profile p)
    {
        var copy = p.Clone();
        copy.Schema = null;
        copy.Version = 0;
        copy.UpdatedAt = null;
        if (copy.Tags is { Count: 0 }) copy.Tags = null;
        if (string.IsNullOrEmpty(copy.Description)) copy.Description = null;
        if (string.IsNullOrEmpty(copy.Group)) copy.Group = null;
        return DeckJson.Serialize(copy);
    }
}
