using System.Text.Json.Serialization;

namespace EnvDeck.Core;

[JsonConverter(typeof(JsonStringEnumConverter<EnvScope>))]
public enum EnvScope
{
    /// <summary>HKCU\Environment: current user, no admin rights needed.</summary>
    User,

    /// <summary>HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment: all users, admin only.</summary>
    Machine,
}

/// <summary>What applying a profile variable does to the target environment.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<VarOp>))]
public enum VarOp
{
    /// <summary>Create or overwrite the variable with <see cref="ProfileVariable.Value"/>.</summary>
    Set,

    /// <summary>Delete the variable.</summary>
    Remove,

    /// <summary>Put the ';'-separated entries of the value at the front of a list variable (PATH-like).</summary>
    Prepend,

    /// <summary>Add the ';'-separated entries of the value at the end of a list variable, if missing.</summary>
    Append,
}

/// <summary>Registry value type of an environment variable.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ValueKind>))]
public enum ValueKind
{
    /// <summary>REG_SZ</summary>
    String,

    /// <summary>REG_EXPAND_SZ: %VAR% references are expanded by Windows.</summary>
    ExpandString,
}

public sealed class ProfileVariable
{
    public string Name { get; set; } = "";

    public string? Value { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public EnvScope Scope { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public VarOp Op { get; set; }

    /// <summary>Null means automatic: REG_EXPAND_SZ when the value contains %VAR% references.</summary>
    public ValueKind? Kind { get; set; }

    public string? Note { get; set; }

    public ProfileVariable Clone() => (ProfileVariable)MemberwiseClone();
}

public sealed class Profile
{
    [JsonPropertyName("$schema")]
    public string? Schema { get; set; }

    public string Name { get; set; } = "";

    public string? Description { get; set; }

    /// <summary>Profiles sharing a group are mutually exclusive: turning one on turns the others off.</summary>
    public string? Group { get; set; }

    public List<string>? Tags { get; set; }

    /// <summary>Managed by <see cref="ProfileStore"/>; bumped on every content change.</summary>
    public int Version { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }

    public List<ProfileVariable> Variables { get; set; } = [];

    public Profile Clone() => new()
    {
        Schema = Schema,
        Name = Name,
        Description = Description,
        Group = Group,
        Tags = Tags?.ToList(),
        Version = Version,
        UpdatedAt = UpdatedAt,
        Variables = Variables.Select(v => v.Clone()).ToList(),
    };
}

/// <summary>A variable as it currently exists in the registry.</summary>
public sealed class EnvValue
{
    public EnvValue() { }

    public EnvValue(string name, string value, ValueKind kind)
    {
        Name = name;
        Value = value;
        Kind = kind;
    }

    public string Name { get; set; } = "";

    public string Value { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ValueKind Kind { get; set; }
}

/// <summary>A full copy of the User and/or Machine environment, stored under backups/.</summary>
public sealed class EnvSnapshot
{
    public string Id { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }

    public string? MachineName { get; set; }

    public string? UserName { get; set; }

    public string? Note { get; set; }

    /// <summary>Why the backup was taken, e.g. "manual" or "before apply java17".</summary>
    public string? Reason { get; set; }

    /// <summary>Automatic backups (before apply/restore) are pruned; manual ones are kept.</summary>
    public bool Auto { get; set; }

    [JsonPropertyName("user")]
    public List<EnvValue>? UserVariables { get; set; }

    [JsonPropertyName("machine")]
    public List<EnvValue>? MachineVariables { get; set; }

    /// <summary>Which profiles were on when the backup was taken, so a restore also restores the on/off state.</summary>
    public List<AppliedProfile>? Applied { get; set; }

    public List<EnvValue>? Get(EnvScope scope) => scope == EnvScope.User ? UserVariables : MachineVariables;

    public IEnumerable<EnvScope> Scopes
    {
        get
        {
            if (UserVariables is not null) yield return EnvScope.User;
            if (MachineVariables is not null) yield return EnvScope.Machine;
        }
    }
}

/// <summary>Undo record for one variable touched by an applied profile.</summary>
public sealed class AppliedChange
{
    public EnvScope Scope { get; set; }

    public string Name { get; set; } = "";

    public bool ExistedBefore { get; set; }

    public string? Before { get; set; }

    public ValueKind? BeforeKind { get; set; }

    /// <summary>Value written by the profile; null when the profile deleted the variable.</summary>
    public string? After { get; set; }

    public ValueKind? AfterKind { get; set; }

    /// <summary>List entries (Prepend/Append) that did not exist before; removed again on turn-off.</summary>
    public List<string>? AddedEntries { get; set; }

    public AppliedChange Clone() => new()
    {
        Scope = Scope,
        Name = Name,
        ExistedBefore = ExistedBefore,
        Before = Before,
        BeforeKind = BeforeKind,
        After = After,
        AfterKind = AfterKind,
        AddedEntries = AddedEntries?.ToList(),
    };
}

public sealed class AppliedProfile
{
    public string Profile { get; set; } = "";

    public int Version { get; set; }

    public string? Group { get; set; }

    public DateTimeOffset AppliedAt { get; set; }

    public List<AppliedChange> Changes { get; set; } = [];

    public AppliedProfile Clone() => new()
    {
        Profile = Profile,
        Version = Version,
        Group = Group,
        AppliedAt = AppliedAt,
        Changes = Changes.Select(c => c.Clone()).ToList(),
    };
}

/// <summary>Machine-local journal of profiles that are currently on (state.json).</summary>
public sealed class DeckState
{
    public List<AppliedProfile> Applied { get; set; } = [];
}

public sealed class DeckSettings
{
    /// <summary>Where profiles, history and backups live. Point it at OneDrive or a git repo to sync between machines.</summary>
    public string? DataDir { get; set; }

    public int HistoryLimit { get; set; } = 50;

    public bool AutoBackup { get; set; } = true;

    public int AutoBackupLimit { get; set; } = 30;

    /// <summary>Command used by "envdeck edit"; defaults to VS Code when available, otherwise Notepad.</summary>
    public string? Editor { get; set; }

    /// <summary>UI language of the desktop app ("en", "vi"); null follows Windows.</summary>
    public string? Language { get; set; }
}

public sealed class BundleManifest
{
    public string Format { get; set; } = "envdeck-bundle";

    public int FormatVersion { get; set; } = 1;

    public DateTimeOffset ExportedAt { get; set; }

    public string? MachineName { get; set; }

    public string? ToolVersion { get; set; }

    public List<string> Profiles { get; set; } = [];

    public bool IncludesHistory { get; set; }

    public bool IncludesBackups { get; set; }
}
