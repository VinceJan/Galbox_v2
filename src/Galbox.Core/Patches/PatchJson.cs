using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Galbox.Core.Patches;

/// <summary>
/// Centralized JSON settings for every serializable patch object.
/// The objects produced by this namespace are meant to be persisted by callers
/// (database row, file, IPC payload) without this assembly depending on any entity model.
/// </summary>
public static class PatchJson
{
    /// <summary>
    /// Indented, camelCase, null-skipping options used for manifests and previews on disk.
    /// Non-ASCII text is written verbatim so that the in-game manifest stays readable for the user
    /// (patch names are usually Chinese or Japanese).
    /// </summary>
    public static readonly JsonSerializerOptions HumanReadable = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>Compact options used for nested payloads (journal records, hashes).</summary>
    public static readonly JsonSerializerOptions Compact = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>Serializes an object with the human readable profile.</summary>
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, HumanReadable);

    /// <summary>Serializes an object with the compact profile.</summary>
    public static string SerializeCompact<T>(T value) => JsonSerializer.Serialize(value, Compact);

    /// <summary>
    /// Deserializes a payload. Enum values written by an older/unknown engine are tolerated
    /// because every enum in this namespace maps unknown numbers to its default member.
    /// </summary>
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, HumanReadable);
}
