using System.Text.Json.Serialization;

namespace Galbox.Core.Patches;

/// <summary>Snapshot of a destination path taken while building or revalidating a preview.</summary>
public sealed class ExistingPathInfo
{
    /// <summary>True when the path is a directory rather than a file.</summary>
    public required bool IsDirectory { get; init; }

    /// <summary>Size in bytes (0 for a directory).</summary>
    public long SizeBytes { get; init; }

    /// <summary>SHA-256 of the file, null for a directory.</summary>
    public string? Sha256 { get; init; }

    /// <summary>Short description used in preview reasons.</summary>
    [JsonIgnore]
    public string Description => IsDirectory ? "a directory" : $"a file ({SizeBytes} bytes, {Sha256 ?? "unhashed"})";
}
