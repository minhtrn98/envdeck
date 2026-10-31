using System.Text.RegularExpressions;

namespace EnvDeck.Core;

/// <summary>Guesses from a variable's name whether its value is a secret (password, token, API key...).</summary>
public static partial class SecretNames
{
    // Matched anywhere in the lower-cased name, so GoogleMapsApiKey, DB_PASSWORD and ConnectionStrings__Main all hit.
    static readonly string[] Fragments =
        ["password", "passwd", "passphrase", "secret", "token", "apikey", "privatekey", "connectionstring", "credential"];

    // Short words that are only secrets as a whole part of the name (KEY must not match KEYBOARD).
    static readonly HashSet<string> Words = ["pw", "pwd", "pass", "key", "keys", "cred", "creds", "sas"];

    // A name that ends like this points at a secret (a file, a folder...) without being one.
    static readonly HashSet<string> NotSecretEndings = ["path", "file", "dir", "folder", "name", "url", "uri", "host", "port", "id", "enabled"];

    /// <summary>What is shown instead of a secret's value.</summary>
    public const string Mask = "••••••••";

    public static bool Looks(string name)
    {
        var parts = Split(name);
        if (parts.Count == 0) return false;
        if (NotSecretEndings.Contains(parts[^1]) && parts.Count > 1) return false;

        var lower = name.ToLowerInvariant();
        return Fragments.Any(lower.Contains) || parts.Any(Words.Contains);
    }

    /// <summary>Parts of a name split on separators and camelCase boundaries: GoogleMapsApiKey → google, maps, api, key.</summary>
    static List<string> Split(string name) =>
        CaseBoundary().Replace(name.Trim(), "$1 $2").Split(['_', '-', ' ', '.', ':'], StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.ToLowerInvariant()).ToList();

    [GeneratedRegex("([a-z0-9])([A-Z])")]
    private static partial Regex CaseBoundary();
}
