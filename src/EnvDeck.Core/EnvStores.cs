using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace EnvDeck.Core;

/// <summary>Persistent (registry-backed) environment variables.</summary>
public interface IEnvStore
{
    /// <summary>True when writes go to a sandbox key instead of the real environment.</summary>
    bool IsSandbox { get; }

    IReadOnlyList<EnvValue> GetAll(EnvScope scope);

    EnvValue? Get(EnvScope scope, string name);

    void Set(EnvScope scope, string name, string value, ValueKind kind);

    void Delete(EnvScope scope, string name);

    bool CanWrite(EnvScope scope);

    /// <summary>Tells running programs (Explorer, terminals started later) that the environment changed.</summary>
    void NotifyChanged();
}

public sealed partial class RegistryEnvStore : IEnvStore
{
    const string UserKeyPath = "Environment";
    const string MachineKeyPath = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";

    readonly string? _sandboxRoot;

    /// <param name="sandboxRoot">
    /// HKCU-relative key used for both scopes instead of the real environment (for trying EnvDeck safely).
    /// </param>
    public RegistryEnvStore(string? sandboxRoot = null) => _sandboxRoot = sandboxRoot;

    public bool IsSandbox => _sandboxRoot is not null;

    public static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public bool CanWrite(EnvScope scope) => scope == EnvScope.User || IsSandbox || IsElevated;

    public IReadOnlyList<EnvValue> GetAll(EnvScope scope)
    {
        using var key = Open(scope, writable: false);
        if (key is null) return [];

        var result = new List<EnvValue>();
        foreach (var name in key.GetValueNames())
        {
            if (Read(key, name) is { } value) result.Add(value);
        }
        result.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        return result;
    }

    public EnvValue? Get(EnvScope scope, string name)
    {
        using var key = Open(scope, writable: false);
        return key is null ? null : Read(key, name);
    }

    public void Set(EnvScope scope, string name, string value, ValueKind kind)
    {
        using var key = OpenForWrite(scope);
        key.SetValue(ExistingName(key, name) ?? name, value,
            kind == ValueKind.ExpandString ? RegistryValueKind.ExpandString : RegistryValueKind.String);
    }

    public void Delete(EnvScope scope, string name)
    {
        using var key = OpenForWrite(scope);
        if (ExistingName(key, name) is { } existing) key.DeleteValue(existing, throwOnMissingValue: false);
    }

    public void NotifyChanged()
    {
        if (IsSandbox) return;
        const nint HWND_BROADCAST = 0xffff;
        const uint WM_SETTINGCHANGE = 0x001A;
        const uint SMTO_ABORTIFHUNG = 0x0002;
        SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, 0, "Environment", SMTO_ABORTIFHUNG, 5000, out _);
    }

    RegistryKey? Open(EnvScope scope, bool writable)
    {
        if (_sandboxRoot is not null) return Registry.CurrentUser.CreateSubKey($@"{_sandboxRoot}\{scope}", writable);
        return scope == EnvScope.User
            ? Registry.CurrentUser.OpenSubKey(UserKeyPath, writable)
            : Registry.LocalMachine.OpenSubKey(MachineKeyPath, writable);
    }

    RegistryKey OpenForWrite(EnvScope scope)
    {
        if (!CanWrite(scope)) throw new ElevationRequiredException(scope);
        try
        {
            return Open(scope, writable: true) ?? throw new DeckException($"The {scope} environment key does not exist.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw new ElevationRequiredException(scope);
        }
    }

    static EnvValue? Read(RegistryKey key, string name)
    {
        var existing = ExistingName(key, name);
        if (existing is null) return null;

        var kind = key.GetValueKind(existing);
        if (kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString)) return null;

        // DoNotExpand keeps %VAR% references exactly as stored, so they survive a round trip.
        return key.GetValue(existing, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string value
            ? new EnvValue(existing, value, kind == RegistryValueKind.ExpandString ? ValueKind.ExpandString : ValueKind.String)
            : null;
    }

    /// <summary>Registry value names are case-insensitive; reuse the stored spelling ("Path", not "PATH").</summary>
    static string? ExistingName(RegistryKey key, string name) =>
        name.Length == 0
            ? null
            : key.GetValueNames().FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint SendMessageTimeout(
        nint hWnd, uint msg, nuint wParam, string lParam, uint flags, uint timeout, out nuint result);
}

/// <summary>In-memory store for tests and previews.</summary>
public sealed class MemoryEnvStore : IEnvStore
{
    readonly Dictionary<EnvScope, Dictionary<string, EnvValue>> _scopes = new()
    {
        [EnvScope.User] = new(StringComparer.OrdinalIgnoreCase),
        [EnvScope.Machine] = new(StringComparer.OrdinalIgnoreCase),
    };

    public bool MachineWritable { get; set; } = true;

    public int NotifyCount { get; private set; }

    public bool IsSandbox => true;

    public IReadOnlyList<EnvValue> GetAll(EnvScope scope) =>
        _scopes[scope].Values.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .Select(v => new EnvValue(v.Name, v.Value, v.Kind)).ToList();

    public EnvValue? Get(EnvScope scope, string name) =>
        _scopes[scope].TryGetValue(name, out var v) ? new EnvValue(v.Name, v.Value, v.Kind) : null;

    public void Set(EnvScope scope, string name, string value, ValueKind kind)
    {
        if (!CanWrite(scope)) throw new ElevationRequiredException(scope);
        var stored = _scopes[scope].TryGetValue(name, out var existing) ? existing.Name : name;
        _scopes[scope][name] = new EnvValue(stored, value, kind);
    }

    public void Delete(EnvScope scope, string name)
    {
        if (!CanWrite(scope)) throw new ElevationRequiredException(scope);
        _scopes[scope].Remove(name);
    }

    public bool CanWrite(EnvScope scope) => scope == EnvScope.User || MachineWritable;

    public void NotifyChanged() => NotifyCount++;
}
