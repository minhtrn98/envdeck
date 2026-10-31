using System.IO.Compression;
using System.Reflection;
using System.Text.RegularExpressions;

namespace EnvDeck.Core;

public sealed class OperationResult(string target, int version, bool dryRun)
{
    public string Target { get; } = target;

    public int Version { get; } = version;

    public bool DryRun { get; } = dryRun;

    /// <summary>Changes made (or, for a dry run, that would be made) for <see cref="Target"/>.</summary>
    public List<PlannedChange> Changes { get; } = [];

    /// <summary>Profiles turned off first when re-applying the same profile.</summary>
    public List<string> TurnedOff { get; } = [];

    public List<PlannedChange> TurnOffChanges { get; } = [];

    public List<string> Warnings { get; } = [];

    public string? BackupId { get; set; }
}

public sealed record ActiveProfileStatus(AppliedProfile Entry, int? CurrentVersion, IReadOnlyList<string> Drift)
{
    public bool ProfileMissing => CurrentVersion is null;

    public bool Outdated => CurrentVersion is { } v && v != Entry.Version;
}

/// <summary>A variable that two profiles which are on both set; <see cref="NewValue"/> wins while the new one is on.</summary>
public sealed record ProfileConflict(EnvScope Scope, string Name, string OtherProfile, string? OtherValue, string? NewValue);

public sealed record SearchHit(string Source, EnvScope Scope, string Name, string? Value, VarOp? Op);

public sealed class SearchOptions
{
    public bool Regex { get; set; }

    public bool Names { get; set; } = true;

    public bool Values { get; set; } = true;

    public bool Profiles { get; set; } = true;

    public bool Environment { get; set; } = true;

    public bool Backups { get; set; }
}

public enum DoctorSeverity { Info, Warning, Error }

public sealed record DoctorIssue(DoctorSeverity Severity, string Message);

public sealed record BundleResult(List<string> Imported, List<string> Skipped, List<string> Backups);

public sealed class DeckService
{
    public DeckService(DeckPaths paths, DeckSettings settings, IEnvStore env)
    {
        Paths = paths;
        Settings = settings;
        Env = env;
        Profiles = new ProfileStore(paths, settings);
        Backups = new BackupStore(paths, settings);
        State = new StateStore(paths);

        Directory.CreateDirectory(paths.ProfilesDir);
        WriteSchema();
        ExternalEdits = Profiles.SyncExternalEdits();
    }

    public DeckPaths Paths { get; }

    public DeckSettings Settings { get; }

    public IEnvStore Env { get; }

    public ProfileStore Profiles { get; }

    public BackupStore Backups { get; }

    public StateStore State { get; }

    /// <summary>Profiles whose JSON was edited by hand and got a new version at startup.</summary>
    public IReadOnlyList<string> ExternalEdits { get; }

    public static string ToolVersion =>
        typeof(DeckService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0] ?? "0.0.0";

    /// <summary>
    /// Real registry and default folders. Environment overrides:
    /// ENVDECK_HOME = data folder, ENVDECK_SANDBOX=1 = use HKCU\Software\EnvDeck\Sandbox instead of the real environment.
    /// </summary>
    public static DeckService CreateDefault()
    {
        var sandbox = Environment.GetEnvironmentVariable("ENVDECK_SANDBOX") is "1" or "true";
        var stateDir = DeckPaths.DefaultStateDir(sandbox);
        var settingsFile = Path.Combine(stateDir, "settings.json");
        var settings = File.Exists(settingsFile) ? DeckJson.Read<DeckSettings>(settingsFile) : new DeckSettings();

        var dataDir = Environment.GetEnvironmentVariable("ENVDECK_HOME") is { Length: > 0 } home ? home
            : !string.IsNullOrWhiteSpace(settings.DataDir) ? Environment.ExpandEnvironmentVariables(settings.DataDir)
            : DeckPaths.DefaultDataDir(sandbox);

        var env = new RegistryEnvStore(sandbox ? @"Software\EnvDeck\Sandbox" : null);
        return new DeckService(new DeckPaths(dataDir, stateDir), settings, env);
    }

    public void SaveSettings() => DeckJson.Write(Paths.SettingsFile, Settings);

    /// <summary>Whether the UI should mask this variable's value: the user's choice, else what the name suggests.</summary>
    public bool IsSecret(string name)
    {
        if (Settings.PlainNames.Contains(name, StringComparer.OrdinalIgnoreCase)) return false;
        return Settings.SecretNames.Contains(name, StringComparer.OrdinalIgnoreCase) || SecretNames.Looks(name);
    }

    /// <summary>The value as it may be displayed: masked when the variable is a secret and the value is not empty.</summary>
    public string? Hide(string name, string? value) =>
        string.IsNullOrEmpty(value) || !IsSecret(name) ? value : SecretNames.Mask;

    /// <summary>Marks a name as secret or plain; only a choice that differs from the name-based guess is stored.</summary>
    public void SetSecret(string name, bool secret)
    {
        name = name.Trim();
        Settings.SecretNames.RemoveAll(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        Settings.PlainNames.RemoveAll(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        if (secret != SecretNames.Looks(name)) (secret ? Settings.SecretNames : Settings.PlainNames).Add(name);
        SaveSettings();
    }

    public bool IsActive(string name) => State.Load().Applied.Any(a => Same(a.Profile, name));

    // ---------------------------------------------------------------- on / off

    public List<PlannedChange> Plan(Profile profile) => ChangePlanner.Plan(profile, Env);

    public OperationResult Apply(string name, bool dryRun = false)
    {
        var profile = Profiles.Get(name);
        ProfileValidator.ThrowIfInvalid(profile);
        var state = State.Load();
        var result = new OperationResult(profile.Name, profile.Version, dryRun);

        var turnOff = state.Applied.Where(a => Same(a.Profile, profile.Name)).ToList();
        var scopes = profile.Variables.Select(v => v.Scope)
            .Concat(turnOff.SelectMany(a => a.Changes).Select(c => c.Scope))
            .Distinct().ToList();
        EnsureWritable(scopes);

        foreach (var c in FindConflicts(profile))
            result.Warnings.Add($"{c.Scope}\\{c.Name} is also set by '{c.OtherProfile}'; '{profile.Name}' overrides it while on.");

        if (dryRun)
        {
            result.TurnedOff.AddRange(turnOff.Select(a => a.Profile));
            if (turnOff.Count > 0)
                result.Warnings.Add($"Preview ignores turning off {string.Join(", ", result.TurnedOff)} first.");
            result.Changes.AddRange(Plan(profile).Where(c => !c.IsNoOp));
            return result;
        }

        if (Settings.AutoBackup && scopes.Count > 0)
            result.BackupId = Backups.Create(Env, scopes, null, $"before apply {profile.Name}", auto: true, state).Id;

        try
        {
            foreach (var other in turnOff)
            {
                TurnOff(state, other.Profile, result.TurnOffChanges, result.Warnings, write: true);
                result.TurnedOff.Add(other.Profile);
            }

            var entry = new AppliedProfile
            {
                Profile = profile.Name,
                Version = profile.Version,
                AppliedAt = DateTimeOffset.Now,
            };
            state.Applied.Add(entry);

            foreach (var change in Plan(profile))
            {
                if (!change.IsNoOp)
                {
                    Write(change);
                    result.Changes.Add(change);
                }
                // Recorded even when unchanged, so stacked profiles hand values back correctly on turn-off.
                entry.Changes.Add(change.ToApplied());
            }
        }
        finally
        {
            State.Save(state);
            Env.NotifyChanged();
        }
        return result;
    }

    public OperationResult Unapply(string name, bool dryRun = false)
    {
        var state = State.Load();
        var entry = state.Applied.FirstOrDefault(a => Same(a.Profile, name))
                    ?? throw new DeckException($"Profile '{name}' is not on.");
        EnsureWritable(entry.Changes.Select(c => c.Scope).Distinct());

        var result = new OperationResult(entry.Profile, entry.Version, dryRun);
        if (dryRun)
        {
            var copy = new DeckState { Applied = state.Applied.Select(a => a.Clone()).ToList() };
            TurnOff(copy, entry.Profile, result.Changes, result.Warnings, write: false);
            return result;
        }

        var scopes = entry.Changes.Select(c => c.Scope).Distinct().ToList();
        if (Settings.AutoBackup && scopes.Count > 0)
            result.BackupId = Backups.Create(Env, scopes, null, $"before unapply {entry.Profile}", auto: true, state).Id;

        try
        {
            TurnOff(state, entry.Profile, result.Changes, result.Warnings, write: true);
        }
        finally
        {
            State.Save(state);
            Env.NotifyChanged();
        }
        return result;
    }

    /// <summary>
    /// Undoes one applied profile using its journal. Values are restored only when nobody changed them since;
    /// list entries (PATH...) the profile added are removed individually; profiles turned on later keep working
    /// because their journals are re-pointed at the values that were there before this profile.
    /// </summary>
    void TurnOff(DeckState state, string name, List<PlannedChange> changes, List<string> warnings, bool write)
    {
        var index = state.Applied.FindIndex(a => Same(a.Profile, name));
        if (index < 0) throw new DeckException($"Profile '{name}' is not on.");
        var entry = state.Applied[index];
        var laterChanges = state.Applied.Skip(index + 1).SelectMany(a => a.Changes).ToList();

        for (var i = entry.Changes.Count - 1; i >= 0; i--)
        {
            var c = entry.Changes[i];
            var current = Env.Get(c.Scope, c.Name);
            var added = c.AddedEntries ?? [];
            var later = laterChanges.Where(l => l.Scope == c.Scope && Same(l.Name, c.Name)).ToList();

            string? target;
            ValueKind? targetKind;
            if (later.Count > 0)
            {
                if (added.Count == 0)
                {
                    // A later profile owns the value now; it will restore our "before" when it is turned off.
                    later[0].ExistedBefore = c.ExistedBefore;
                    later[0].Before = c.Before;
                    later[0].BeforeKind = c.BeforeKind;
                    continue;
                }
                // An entry we added that a later profile also asked for stays in the list: the last such profile
                // takes it over (it is removed when that profile is turned off). The others are removed now.
                var released = new List<string>();
                foreach (var e in added)
                {
                    var owner = later.FindLastIndex(l => PathList.Contains(l.RequestedEntries ?? [], e));
                    if (owner < 0) released.Add(e);
                    else if (!PathList.Contains(later[owner].AddedEntries ?? [], e)) (later[owner].AddedEntries ??= []).Add(e);

                    // Journals up to the owner must no longer expect the entry before them; the owner and the
                    // profiles after it still see it in the list they produced.
                    for (var j = 0; j < later.Count; j++)
                    {
                        if (owner < 0 || j <= owner) later[j].Before = RemoveEntry(later[j].Before, e);
                        if (owner < 0 || j < owner) later[j].After = RemoveEntry(later[j].After, e);
                    }
                }
                foreach (var l in later)
                {
                    if (!c.ExistedBefore && string.IsNullOrEmpty(l.Before))
                    {
                        l.ExistedBefore = false;
                        l.Before = null;
                    }
                }
                target = current is null ? null : PathList.Remove(current.Value, released);
                targetKind = current?.Kind;
            }
            else if (c.After is null ? current is null : current?.Value == c.After)
            {
                target = c.ExistedBefore ? c.Before : null;
                targetKind = c.BeforeKind;
            }
            else if (added.Count > 0 && current is not null)
            {
                // Someone else edited the list since (e.g. an installer added to PATH): remove only our entries.
                target = PathList.Remove(current.Value, added);
                targetKind = current.Kind;
            }
            else
            {
                warnings.Add($"{c.Scope}\\{c.Name} was changed outside EnvDeck after '{name}' was turned on; left as is.");
                continue;
            }

            if (target is "" && !c.ExistedBefore) target = null;
            var change = new PlannedChange(c.Scope, c.Name, current?.Value, current?.Kind, target,
                target is null ? null : targetKind ?? ChangePlanner.AutoKind(target), []);
            if (change.IsNoOp) continue;
            if (write) Write(change);
            changes.Add(change);
        }
        state.Applied.RemoveAt(index);

        static string? RemoveEntry(string? value, string entry) => value is null ? null : PathList.Remove(value, [entry]);
    }

    // ---------------------------------------------------------------- direct edits

    public EnvOwnership GetOwnership()
    {
        var ownership = new EnvOwnership();
        foreach (var applied in State.Load().Applied)
        foreach (var c in applied.Changes)
        {
            var entries = (c.RequestedEntries ?? []).Concat(c.AddedEntries ?? []).ToList();
            if (entries.Count == 0) ownership.SetWhole(c.Scope, c.Name, applied.Profile);
            else foreach (var e in entries) ownership.AddEntry(c.Scope, c.Name, e, applied.Profile);
        }
        return ownership;
    }

    /// <summary>
    /// Writes edits straight to the environment (like the Windows dialog, but checked): variables and list
    /// entries that a profile which is on manages are refused, as is anything changed outside since the editor
    /// loaded it. A backup is taken first.
    /// </summary>
    public OperationResult ApplyDirect(IReadOnlyList<DirectChange> edits, bool dryRun = false)
    {
        var ownership = GetOwnership();
        var result = new OperationResult("environment", 0, dryRun);
        var seen = new HashSet<(EnvScope, string)>();
        foreach (var e in edits)
        {
            if (string.IsNullOrWhiteSpace(e.Name) || e.Name.Contains('=') || e.Name.Trim() != e.Name)
                throw new DeckException($"Invalid variable name '{e.Name}'.");
            if (!seen.Add((e.Scope, e.Name.ToUpperInvariant())))
                throw new DeckException($"{e.Scope}\\{e.Name} is listed twice.");
            if (ownership.WholeOwner(e.Scope, e.Name) is { } owner)
                throw new DeckException($"{e.Scope}\\{e.Name} is set by profile '{owner}', which is on; edit the profile instead.");

            var current = Env.Get(e.Scope, e.Name);
            if (e.IsNew && current is not null)
                throw new DeckException($"{e.Scope}\\{e.Name} already exists.");
            if (!e.IsNew && current?.Value != e.OriginalValue)
                throw new DeckException($"{e.Scope}\\{e.Name} was changed outside EnvDeck since it was loaded; refresh and try again.");

            var currentEntries = PathList.Split(current?.Value);
            var newEntries = PathList.Split(e.Value);
            foreach (var (entry, profile) in ownership.OwnedEntries(e.Scope, e.Name))
            {
                if (PathList.Contains(currentEntries, entry) && !PathList.Contains(newEntries, entry))
                    throw new DeckException($"'{entry}' in {e.Scope}\\{e.Name} belongs to profile '{profile}', which is on.");
            }

            var kind = e.Value is null ? (ValueKind?)null : e.Kind ?? ChangePlanner.AutoKind(e.Value);
            var change = new PlannedChange(e.Scope, current?.Name ?? e.Name, current?.Value, current?.Kind, e.Value, kind, []);
            if (!change.IsNoOp) result.Changes.Add(change);
        }

        var scopes = result.Changes.Select(c => c.Scope).Distinct().ToList();
        EnsureWritable(scopes);
        if (dryRun || result.Changes.Count == 0) return result;

        if (Settings.AutoBackup)
            result.BackupId = Backups.Create(Env, scopes, null, "before editing the environment", auto: true, State.Load()).Id;
        try
        {
            foreach (var change in result.Changes) Write(change);
        }
        finally
        {
            Env.NotifyChanged();
        }
        return result;
    }

    /// <summary>
    /// Variables that turning <paramref name="profile"/> on would overwrite (Set/Remove) while another profile that
    /// is on has set them to something else.
    /// </summary>
    public List<ProfileConflict> FindConflicts(Profile profile)
    {
        var others = State.Load().Applied.Where(a => !Same(a.Profile, profile.Name)).ToList();
        if (others.Count == 0) return [];

        var conflicts = new List<ProfileConflict>();
        foreach (var planned in Plan(profile))
        {
            var overwrites = profile.Variables.Any(v =>
                v.Scope == planned.Scope && Same(v.Name, planned.Name) && v.Op is VarOp.Set or VarOp.Remove);
            if (!overwrites) continue;

            // The profile turned on last is the one whose value is in effect.
            var owner = others.LastOrDefault(a => a.Changes.Any(ch => ch.Scope == planned.Scope && Same(ch.Name, planned.Name)));
            if (owner is null) continue;
            var theirs = owner.Changes.First(ch => ch.Scope == planned.Scope && Same(ch.Name, planned.Name)).After;
            if (theirs != planned.After)
                conflicts.Add(new ProfileConflict(planned.Scope, planned.Name, owner.Profile, theirs, planned.After));
        }
        return conflicts;
    }

    public List<ActiveProfileStatus> Status()
    {
        var state = State.Load();
        var result = new List<ActiveProfileStatus>();
        for (var i = 0; i < state.Applied.Count; i++)
        {
            var entry = state.Applied[i];
            var later = state.Applied.Skip(i + 1).SelectMany(a => a.Changes).ToList();
            var drift = new List<string>();
            foreach (var c in entry.Changes)
            {
                if (later.Any(l => l.Scope == c.Scope && Same(l.Name, c.Name))) continue;
                var current = Env.Get(c.Scope, c.Name);
                var ok = c.AddedEntries is { Count: > 0 } added
                    ? current is not null && added.All(e => PathList.Contains(PathList.Split(current.Value), e))
                    : c.After is null ? current is null : current?.Value == c.After;
                if (!ok) drift.Add($"{c.Scope}\\{c.Name}");
            }
            result.Add(new ActiveProfileStatus(entry, Profiles.TryGet(entry.Profile)?.Version, drift));
        }
        return result;
    }

    // ---------------------------------------------------------------- profiles

    public void DeleteProfile(string name, bool purgeHistory)
    {
        if (IsActive(name)) throw new DeckException($"Profile '{name}' is on; turn it off first.");
        Profiles.Delete(name, purgeHistory);
    }

    public Profile RenameProfile(string oldName, string newName)
    {
        var renamed = Profiles.Rename(oldName, newName);
        var state = State.Load();
        foreach (var a in state.Applied.Where(a => Same(a.Profile, oldName))) a.Profile = renamed.Name;
        State.Save(state);
        return renamed;
    }

    /// <summary>Copies current variables into a profile (created if missing) as Set entries.</summary>
    public (Profile Profile, List<string> Warnings) Capture(
        string profileName, IEnumerable<(EnvScope Scope, string Name)> variables, bool portable)
    {
        ProfileStore.ValidateName(profileName);
        var profile = Profiles.TryGet(profileName) ?? new Profile { Name = profileName };
        var warnings = new List<string>();
        foreach (var (scope, name) in variables)
        {
            var current = Env.Get(scope, name);
            if (current is null)
            {
                warnings.Add($"{scope}\\{name} does not exist; skipped.");
                continue;
            }
            profile.Variables.RemoveAll(v => v.Scope == scope && Same(v.Name, name));
            profile.Variables.Add(VariableCapture.FromValue(scope, current, portable));
        }
        return (Profiles.Save(profile).Profile, warnings);
    }

    // ---------------------------------------------------------------- backup / restore

    public EnvSnapshot Backup(IEnumerable<EnvScope> scopes, string? note) =>
        Backups.Create(Env, scopes, note, "manual", auto: false, State.Load());

    /// <param name="prune">Also delete variables that do not exist in the backup.</param>
    public OperationResult Restore(string id, IReadOnlyCollection<EnvScope>? scopes, bool prune, bool dryRun = false)
    {
        var snapshot = Backups.Get(id);
        var targets = (scopes is { Count: > 0 } ? scopes : snapshot.Scopes).Distinct().ToList();
        foreach (var scope in targets)
        {
            if (snapshot.Get(scope) is null)
                throw new DeckException($"Backup {snapshot.Id} does not contain {scope} variables.");
        }
        if (!dryRun) EnsureWritable(targets);

        var result = new OperationResult(snapshot.Id, 0, dryRun);
        foreach (var scope in targets)
        {
            var wanted = snapshot.Get(scope)!;
            var current = Env.GetAll(scope).ToDictionary(v => v.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var v in wanted)
            {
                current.TryGetValue(v.Name, out var cur);
                if (cur is null || cur.Value != v.Value || cur.Kind != v.Kind)
                    result.Changes.Add(new PlannedChange(scope, v.Name, cur?.Value, cur?.Kind, v.Value, v.Kind, []));
            }
            if (!prune) continue;
            foreach (var cur in current.Values.Where(c => !wanted.Any(w => Same(w.Name, c.Name))))
                result.Changes.Add(new PlannedChange(scope, cur.Name, cur.Value, cur.Kind, null, null, []));
        }
        if (dryRun) return result;

        var state = State.Load();
        if (Settings.AutoBackup)
            result.BackupId = Backups.Create(Env, targets, null, $"before restore {snapshot.Id}", auto: true, state).Id;
        try
        {
            foreach (var change in result.Changes) Write(change);
        }
        finally
        {
            var sameMachine = Same(snapshot.MachineName, Environment.MachineName);
            var fullRestore = snapshot.Scopes.All(targets.Contains);
            if (sameMachine && fullRestore)
            {
                // The environment is back to the backup, so the on/off journal must be too.
                state.Applied = snapshot.Applied ?? [];
                State.Save(state);
            }
            else if (state.Applied.Count > 0)
            {
                result.Warnings.Add("The on/off state was kept as is; check 'envdeck status' for profiles that drifted.");
            }
            Env.NotifyChanged();
        }
        return result;
    }

    // ---------------------------------------------------------------- search / compare

    public List<SearchHit> Search(string query, SearchOptions options)
    {
        Func<string?, bool> match;
        if (options.Regex)
        {
            Regex regex;
            try
            {
                regex = new Regex(query, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException ex)
            {
                throw new DeckException($"Invalid regular expression: {ex.Message}");
            }
            match = s => s is not null && regex.IsMatch(s);
        }
        else
        {
            match = s => s?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
        }
        bool Hit(string name, string? value) => (options.Names && match(name)) || (options.Values && match(value));

        var hits = new List<SearchHit>();
        if (options.Profiles)
        {
            foreach (var p in Profiles.List())
                hits.AddRange(p.Variables.Where(v => Hit(v.Name, v.Value))
                    .Select(v => new SearchHit($"profile:{p.Name}", v.Scope, v.Name, v.Value, v.Op)));
        }
        if (options.Environment)
        {
            foreach (var scope in Enum.GetValues<EnvScope>())
                hits.AddRange(Env.GetAll(scope).Where(v => Hit(v.Name, v.Value))
                    .Select(v => new SearchHit($"env:{scope.ToString().ToLowerInvariant()}", scope, v.Name, v.Value, null)));
        }
        if (options.Backups)
        {
            foreach (var b in Backups.List())
            foreach (var scope in b.Scopes)
                hits.AddRange(b.Get(scope)!.Where(v => Hit(v.Name, v.Value))
                    .Select(v => new SearchHit($"backup:{b.Id}", scope, v.Name, v.Value, null)));
        }
        return hits;
    }

    /// <summary>
    /// Resolves a compare source: "name", "name@3", "profile:name", "env", "env:user", "env:machine",
    /// "backup:&lt;id&gt;" (or "backup:latest"), or a path to a profile/backup .json file.
    /// </summary>
    public DiffSource ResolveSource(string spec)
    {
        var s = spec.Trim();
        if (Same(s, "env"))
            return EnvSource("env", Enum.GetValues<EnvScope>());
        if (s.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
            return EnvSource(s.ToLowerInvariant(), [ParseScope(s[4..])]);

        if (s.StartsWith("backup:", StringComparison.OrdinalIgnoreCase))
            return SnapshotSource(Backups.Get(s[7..]));

        if (s.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && File.Exists(s))
        {
            var json = File.ReadAllText(s);
            using var doc = System.Text.Json.JsonDocument.Parse(json, new() { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return doc.RootElement.TryGetProperty("variables", out _)
                ? EnvDiff.FromProfile(DeckJson.Deserialize<Profile>(json), Path.GetFileName(s))
                : SnapshotSource(DeckJson.Deserialize<EnvSnapshot>(json), Path.GetFileName(s));
        }

        if (s.StartsWith("profile:", StringComparison.OrdinalIgnoreCase)) s = s[8..];
        var profile = Profiles.GetRef(s);
        return EnvDiff.FromProfile(profile, $"{profile.Name}@{profile.Version}");

        DiffSource EnvSource(string label, IEnumerable<EnvScope> scopes) =>
            EnvDiff.FromValues(label, scopes.SelectMany(scope => Env.GetAll(scope).Select(v => (scope, v))));

        static DiffSource SnapshotSource(EnvSnapshot snapshot, string? label = null) =>
            EnvDiff.FromValues(label ?? $"backup:{snapshot.Id}",
                snapshot.Scopes.SelectMany(scope => snapshot.Get(scope)!.Select(v => (scope, v))));
    }

    public (DiffSource Left, DiffSource Right, List<DiffRow> Rows) Diff(string left, string right, bool includeSame = false)
    {
        var l = ResolveSource(left);
        var r = ResolveSource(right);
        return (l, r, EnvDiff.Compare(l, r, includeSame));
    }

    /// <summary>Suggestions for compare pickers: env sources, profiles, profile versions and backups.</summary>
    public List<string> SourceSuggestions(bool includeVersions = true)
    {
        var list = new List<string> { "env:user", "env:machine", "env" };
        foreach (var p in Profiles.List())
        {
            list.Add(p.Name);
            if (includeVersions)
                list.AddRange(Profiles.History(p.Name).Where(h => h.Version != p.Version).Select(h => $"{p.Name}@{h.Version}"));
        }
        list.AddRange(Backups.List().Select(b => $"backup:{b.Id}"));
        return list;
    }

    // ---------------------------------------------------------------- health check

    public List<DoctorIssue> Doctor()
    {
        var issues = new List<DoctorIssue>();
        if (Env.IsSandbox) issues.Add(new(DoctorSeverity.Info, "Sandbox mode: changes go to HKCU\\Software\\EnvDeck\\Sandbox."));

        var profiles = Profiles.List();
        issues.AddRange(Profiles.LoadErrors.Select(e => new DoctorIssue(DoctorSeverity.Error, e)));
        foreach (var p in profiles)
            issues.AddRange(ProfileValidator.Validate(p).Select(e => new DoctorIssue(DoctorSeverity.Error, $"{p.Name}: {e}")));

        var seen = new Dictionary<string, EnvScope>(StringComparer.OrdinalIgnoreCase);
        var totalLength = 0;
        foreach (var scope in Enum.GetValues<EnvScope>())
        {
            var path = Env.Get(scope, "Path");
            if (path is null) continue;
            totalLength += Environment.ExpandEnvironmentVariables(path.Value).Length + 1;
            if (path.Value.Contains(";;")) issues.Add(new(DoctorSeverity.Info, $"{scope} Path contains empty entries."));

            foreach (var entry in PathList.Split(path.Value))
            {
                var key = Environment.ExpandEnvironmentVariables(entry).Trim('"').TrimEnd('\\');
                if (seen.TryGetValue(key, out var firstScope))
                {
                    issues.Add(new(firstScope == scope ? DoctorSeverity.Warning : DoctorSeverity.Info,
                        $"{scope} Path: '{entry}' is a duplicate{(firstScope == scope ? "" : $" of a {firstScope} Path entry")}."));
                    continue;
                }
                seen[key] = scope;
                if (!Directory.Exists(key) && !File.Exists(key))
                    issues.Add(new(DoctorSeverity.Warning, $"{scope} Path: '{entry}' does not exist."));
            }
        }
        if (totalLength > 32767)
            issues.Add(new(DoctorSeverity.Error, $"Combined Path is {totalLength} characters; Windows truncates beyond 32767."));

        foreach (var s in Status())
        {
            if (s.ProfileMissing)
                issues.Add(new(DoctorSeverity.Warning, $"'{s.Entry.Profile}' is on but its profile file was deleted."));
            else if (s.Outdated)
                issues.Add(new(DoctorSeverity.Warning, $"'{s.Entry.Profile}' is on at v{s.Entry.Version} but the profile is now v{s.CurrentVersion}; apply it again."));
            if (s.Drift.Count > 0)
                issues.Add(new(DoctorSeverity.Warning, $"'{s.Entry.Profile}' drifted: {string.Join(", ", s.Drift)} changed outside EnvDeck."));
        }
        return issues;
    }

    // ---------------------------------------------------------------- move between machines

    /// <summary>
    /// Writes a bundle. With a <paramref name="password"/> the whole file is encrypted (see <see cref="BundleCrypto"/>);
    /// without one it is a plain .zip and everything in it, secrets included, is readable.
    /// </summary>
    public BundleManifest Export(string zipPath, IReadOnlyCollection<string>? profileNames, bool withHistory, bool withBackups,
        string? password = null)
    {
        var names = profileNames is { Count: > 0 } ? profileNames.ToList() : Profiles.List().Select(p => p.Name).ToList();
        var manifest = new BundleManifest
        {
            ExportedAt = DateTimeOffset.Now,
            MachineName = Environment.MachineName,
            ToolVersion = ToolVersion,
            IncludesHistory = withHistory,
            IncludesBackups = withBackups,
        };

        var full = Path.GetFullPath(zipPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var tmp = full + ".tmp";
        File.Delete(tmp);

        // Encrypted bundles are built in memory, so the readable zip never touches the disk.
        using (Stream buffer = password is null ? new FileStream(tmp, FileMode.Create) : new MemoryStream())
        {
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var name in names)
                {
                    var profile = Profiles.Get(name);
                    zip.CreateEntryFromFile(Profiles.FileOf(profile.Name), $"profiles/{profile.Name}.json");
                    manifest.Profiles.Add(profile.Name);
                    if (!withHistory) continue;
                    foreach (var (version, file) in Profiles.HistoryFiles(profile.Name))
                        zip.CreateEntryFromFile(file, $"history/{profile.Name}/v{version:D4}.json");
                }
                if (withBackups && Directory.Exists(Paths.BackupsDir))
                {
                    foreach (var file in Directory.EnumerateFiles(Paths.BackupsDir, "*.json"))
                        zip.CreateEntryFromFile(file, $"backups/{Path.GetFileName(file)}");
                }
                using var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open());
                writer.Write(DeckJson.Serialize(manifest));
            }

            if (password is not null)
            {
                var plain = ((MemoryStream)buffer).ToArray();
                try
                {
                    File.WriteAllBytes(tmp, BundleCrypto.Encrypt(plain, password));
                }
                finally
                {
                    System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain);
                }
            }
        }
        File.Move(tmp, full, overwrite: true);
        return manifest;
    }

    /// <summary>Imports a bundle (from <see cref="Export"/>, encrypted or not) or a single profile .json file.</summary>
    /// <exception cref="PasswordRequiredException">The bundle is encrypted and no password was given.</exception>
    /// <exception cref="WrongPasswordException">The password is wrong, or the bundle was changed.</exception>
    public BundleResult Import(string path, bool overwrite, string? password = null)
    {
        if (!BundleCrypto.IsEncrypted(path)) return ImportFile(path, overwrite);
        if (password is null) throw new PasswordRequiredException(path);

        var plain = BundleCrypto.Decrypt(File.ReadAllBytes(path), password);
        try
        {
            using var zip = new ZipArchive(new MemoryStream(plain), ZipArchiveMode.Read);
            return ImportArchive(zip, path, overwrite);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain);
        }
    }

    BundleResult ImportFile(string path, bool overwrite)
    {
        var result = new BundleResult([], [], []);
        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            var profile = DeckJson.Read<Profile>(path);
            if (!ProfileStore.IsValidName(profile.Name)) profile.Name = Path.GetFileNameWithoutExtension(path);
            ImportProfile(result, profile, [], overwrite);
            return result;
        }

        using var zip = ZipFile.OpenRead(path);
        return ImportArchive(zip, path, overwrite);
    }

    BundleResult ImportArchive(ZipArchive zip, string path, bool overwrite)
    {
        var result = new BundleResult([], [], []);
        var manifestEntry = zip.GetEntry("manifest.json") ?? throw new DeckException($"{path} is not an EnvDeck bundle.");
        var manifest = DeckJson.Deserialize<BundleManifest>(ReadEntry(manifestEntry));
        if (manifest.Format != "envdeck-bundle") throw new DeckException($"{path} is not an EnvDeck bundle.");

        foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith("profiles/", StringComparison.Ordinal) && e.Name.EndsWith(".json")))
        {
            var name = Path.GetFileNameWithoutExtension(entry.Name);
            if (!ProfileStore.IsValidName(name))
            {
                result.Skipped.Add(entry.FullName);
                continue;
            }
            var profile = DeckJson.Deserialize<Profile>(ReadEntry(entry));
            profile.Name = name;
            var history = zip.Entries
                .Select(e => (Entry: e, Match: Regex.Match(e.FullName, $"^history/{Regex.Escape(name)}/v(\\d+)\\.json$")))
                .Where(x => x.Match.Success)
                .Select(x => (int.Parse(x.Match.Groups[1].Value), ReadEntry(x.Entry)))
                .ToList();
            ImportProfile(result, profile, history, overwrite);
        }

        foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith("backups/", StringComparison.Ordinal) && e.Name.EndsWith(".json")))
        {
            var id = Path.GetFileNameWithoutExtension(entry.Name);
            if (Backups.Exists(id)) continue;
            var snapshot = DeckJson.Deserialize<EnvSnapshot>(ReadEntry(entry));
            snapshot.Id = id;
            Backups.Save(snapshot);
            result.Backups.Add(id);
        }
        return result;
    }

    void ImportProfile(BundleResult result, Profile profile, List<(int, string)> history, bool overwrite)
    {
        if (!Profiles.Exists(profile.Name))
        {
            if (profile.Version < 1) profile.Version = 1;
            Profiles.ImportWithHistory(profile, history);
            result.Imported.Add(profile.Name);
        }
        else if (overwrite)
        {
            Profiles.Save(profile);
            result.Imported.Add(profile.Name);
        }
        else
        {
            result.Skipped.Add(profile.Name);
        }
    }

    static string ReadEntry(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    // ---------------------------------------------------------------- helpers

    public static EnvScope ParseScope(string text) =>
        Enum.TryParse<EnvScope>(text, ignoreCase: true, out var scope) && Enum.IsDefined(scope)
            ? scope
            : throw new DeckException($"Unknown scope '{text}'. Use user or machine.");

    void EnsureWritable(IEnumerable<EnvScope> scopes)
    {
        foreach (var scope in scopes.Distinct())
        {
            if (!Env.CanWrite(scope)) throw new ElevationRequiredException(scope);
        }
    }

    void Write(PlannedChange change)
    {
        if (change.After is null) Env.Delete(change.Scope, change.Name);
        else Env.Set(change.Scope, change.Name, change.After, change.AfterKind ?? ValueKind.String);
    }

    void WriteSchema()
    {
        using var stream = typeof(DeckService).Assembly.GetManifestResourceStream("EnvDeck.Core.profile.schema.json");
        if (stream is null) return;
        using var reader = new StreamReader(stream);
        var schema = reader.ReadToEnd();
        if (!File.Exists(Paths.SchemaFile) || File.ReadAllText(Paths.SchemaFile) != schema)
            File.WriteAllText(Paths.SchemaFile, schema);
    }

    static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
