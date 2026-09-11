namespace Galbox.Core.Patches;

/// <summary>Container formats the patch engine can identify. Only <see cref="Zip"/>, <see cref="Rar"/>, <see cref="SevenZip"/> and <see cref="Tar"/> are extractable.</summary>
public enum PatchArchiveKind
{
    /// <summary>Not recognised.</summary>
    Unknown = 0,

    /// <summary>PKZIP container, read through <c>System.IO.Compression</c>.</summary>
    Zip = 1,

    /// <summary>RAR 4/5 container, read-only through SharpCompress.</summary>
    Rar = 2,

    /// <summary>7-Zip container, read through SharpCompress.</summary>
    SevenZip = 3,

    /// <summary>Uncompressed tar.</summary>
    Tar = 4,

    /// <summary>gzip stream (usually wrapping a tar).</summary>
    Gzip = 5,

    /// <summary>Self-extracting executable (PE file). Never executed, never auto-extracted.</summary>
    SelfExtractingExecutable = 6,

    /// <summary>Disc image (iso/mdf/mds/nrg).</summary>
    DiscImage = 7
}

/// <summary>Options controlling how an archive is opened and decoded.</summary>
public sealed class PatchArchiveReadOptions
{
    /// <summary>
    /// Name encoding. <see cref="PatchNameEncoding.Auto"/> probes UTF-8 first and falls back to
    /// CP932 / GBK / Big5 when the decoded names contain replacement characters.
    /// </summary>
    public PatchNameEncoding NameEncoding { get; init; } = PatchNameEncoding.Auto;

    /// <summary>
    /// Apply <see cref="NameEncoding"/> even to entries that carry the "UTF-8 file name" flag.
    /// The flag is unreliable in the wild, so users need a way to override it by hand.
    /// </summary>
    public bool ForceNameEncoding { get; init; }

    /// <summary>Hard cap on the number of entries (zip-bomb / pathological archive guard).</summary>
    public int MaxEntryCount { get; init; } = 200_000;

    /// <summary>Hard cap on the sum of declared uncompressed sizes.</summary>
    public long MaxTotalUncompressedBytes { get; init; } = 64L * 1024 * 1024 * 1024;

    /// <summary>Default options.</summary>
    public static PatchArchiveReadOptions Default { get; } = new();
}

/// <summary>Identity and capability summary of a patch package. Produced before anything is extracted.</summary>
public sealed class PatchArchiveInfo
{
    /// <summary>Full path of the file that was inspected.</summary>
    public required string SourcePath { get; init; }

    /// <summary>File name without directory.</summary>
    public required string FileName { get; init; }

    /// <summary>Size of the container on disk.</summary>
    public long SizeBytes { get; init; }

    /// <summary>SHA-256 of the container (this is the "did I download the right bytes" proof, nothing more).</summary>
    public string? Sha256 { get; init; }

    /// <summary>Detected container kind.</summary>
    public required PatchArchiveKind Kind { get; init; }

    /// <summary>Stable id used in manifests, e.g. <c>zip</c>, <c>rar</c>, <c>7z</c>.</summary>
    public required string FormatId { get; init; }

    /// <summary>What the caller asked for.</summary>
    public PatchNameEncoding RequestedNameEncoding { get; init; } = PatchNameEncoding.Auto;

    /// <summary>Encoding actually used to decode entry names.</summary>
    public PatchNameEncoding EffectiveNameEncoding { get; init; } = PatchNameEncoding.Utf8;

    /// <summary>True when <see cref="EffectiveNameEncoding"/> came from probing rather than an explicit choice.</summary>
    public bool NameEncodingWasAutoDetected { get; init; }

    /// <summary>How the encoding decision was reached (shown to the user; the UTF-8 flag is not trusted).</summary>
    public IReadOnlyList<string> EncodingNotes { get; init; } = Array.Empty<string>();

    /// <summary>Number of entries in the container (files + directories).</summary>
    public int EntryCount { get; init; }

    /// <summary>Number of file entries (directories excluded).</summary>
    public int FileEntryCount { get; init; }

    /// <summary>Sum of the declared uncompressed sizes; may be 0 for formats that do not declare it.</summary>
    public long TotalUncompressedBytes { get; init; }

    /// <summary>True when the container refuses to be read without a password.</summary>
    public bool IsEncrypted { get; init; }

    /// <summary>True for self-extracting executables: the engine refuses to touch them.</summary>
    public bool RequiresManualRun { get; init; }

    /// <summary>Instruction shown when <see cref="RequiresManualRun"/> is true.</summary>
    public string? ManualRunAdvice { get; init; }

    /// <summary>Non-fatal observations (encoding guesses, unusual entries, unsupported extras).</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>True when the engine can extract this container.</summary>
    public bool IsExtractable => Kind is PatchArchiveKind.Zip or PatchArchiveKind.Rar or PatchArchiveKind.SevenZip or PatchArchiveKind.Tar or PatchArchiveKind.Gzip;
}

/// <summary>One entry as reported by the container reader, before any path-safety work.</summary>
public sealed class PatchArchiveEntry
{
    /// <summary>Zero-based index inside the container.</summary>
    public required int Index { get; init; }

    /// <summary>Decoded entry name exactly as the reader produced it.</summary>
    public required string Name { get; init; }

    /// <summary>True for directory entries.</summary>
    public bool IsDirectory { get; init; }

    /// <summary>Declared uncompressed size, when the format provides it.</summary>
    public long SizeBytes { get; init; }

    /// <summary>True when the entry is a symlink / hardlink. Such entries are always rejected.</summary>
    public bool IsLink { get; init; }
}
