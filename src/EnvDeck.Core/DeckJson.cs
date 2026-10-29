using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace EnvDeck.Core;

[JsonSerializable(typeof(Profile))]
[JsonSerializable(typeof(EnvSnapshot))]
[JsonSerializable(typeof(DeckState))]
[JsonSerializable(typeof(DeckSettings))]
[JsonSerializable(typeof(BundleManifest))]
internal partial class DeckJsonContext : JsonSerializerContext;

public static class DeckJson
{
    static readonly DeckJsonContext Context = new(new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // Keep paths and non-ASCII text (e.g. Vietnamese descriptions) readable in the files.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    static readonly UTF8Encoding Utf8NoBom = new(false);

    static JsonTypeInfo<T> Info<T>() =>
        Context.GetTypeInfo(typeof(T)) as JsonTypeInfo<T>
        ?? throw new NotSupportedException($"{typeof(T).Name} is not registered in {nameof(DeckJsonContext)}.");

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Info<T>());

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize(json, Info<T>()) ?? throw new DeckException("The JSON document is empty.");

    public static T Read<T>(string path)
    {
        try
        {
            return Deserialize<T>(File.ReadAllText(path));
        }
        catch (JsonException ex)
        {
            throw new DeckException($"Invalid JSON in {path}: {ex.Message}", ex);
        }
    }

    /// <summary>Writes through a temp file so a crash never leaves a half-written profile or state file.</summary>
    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, Serialize(value) + Environment.NewLine, Utf8NoBom);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(tmp, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < 5 && ex is IOException or UnauthorizedAccessException)
            {
                // Antivirus, the search indexer or OneDrive may hold the target for a moment.
                Thread.Sleep(50 * attempt);
            }
        }
    }
}
