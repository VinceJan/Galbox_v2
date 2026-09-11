namespace Galbox.Core.Patches;

/// <summary>What the installer intends to do with one archive entry.</summary>
public enum PatchPreviewAction
{
    /// <summary>Target file does not exist yet.</summary>
    Create = 0,

    /// <summary>Target exists and is recognised as safe to replace (original game file or a file a previous Galbox install wrote).</summary>
    Overwrite = 1,

    /// <summary>Target exists and is already byte identical to the incoming file: nothing to do.</summary>
    Unchanged = 2,

    /// <summary>Target exists with different content and unknown provenance: the user must confirm.</summary>
    Conflict = 3,

    /// <summary>Entry was refused (Zip Slip, invalid name under the strict policy, link entry, duplicate target).</summary>
    Rejected = 4
}

/// <summary>Provenance of the file that currently occupies the destination path.</summary>
public enum PatchTargetProvenance
{
    /// <summary>Nothing is there.</summary>
    NotPresent = 0,

    /// <summary>Recorded in a previous Galbox manifest for this game: we know where it came from.</summary>
    ManagedByGalbox = 1,

    /// <summary>Matches a hash the caller supplied as "known original game file".</summary>
    KnownOriginal = 2,

    /// <summary>Exists, but neither Galbox nor the caller can explain it.</summary>
    UnknownOrigin = 3
}

/// <summary>One line of the overwrite preview: where an archived file will land and what happens there.</summary>
public sealed class PatchPreviewEntry
{
    /// <summary>Index of the entry inside the archive.</summary>
    public int ArchiveEntryIndex { get; init; }

    /// <summary>Name as it appeared in the archive (after decoding, before sanitisation).</summary>
    public required string ArchiveEntryName { get; init; }

    /// <summary>Relative path inside the sandbox (mirrors <see cref="TargetRelativePath"/> unless sanitised).</summary>
    public string? SandboxRelativePath { get; init; }

    /// <summary><b>Final landing path</b>, relative to the game root. This is what the user confirms.</summary>
    public required string TargetRelativePath { get; init; }

    /// <summary>Absolute landing path (game root + relative path).</summary>
    public required string TargetFullPath { get; init; }

    /// <summary>What will happen.</summary>
    public required PatchPreviewAction Action { get; init; }

    /// <summary>Why the current occupant is trusted or not.</summary>
    public PatchTargetProvenance Provenance { get; init; } = PatchTargetProvenance.NotPresent;

    /// <summary>Human readable justification for <see cref="Action"/> / <see cref="Provenance"/>.</summary>
    public string? AssessmentReason { get; init; }

    /// <summary>Size of the file that is about to be replaced (null when creating).</summary>
    public long? ExistingSizeBytes { get; init; }

    /// <summary>SHA-256 of the file that is about to be replaced (null when creating).</summary>
    public string? ExistingSha256 { get; init; }

    /// <summary>Size of the incoming file.</summary>
    public long IncomingSizeBytes { get; init; }

    /// <summary>SHA-256 of the incoming file.</summary>
    public required string IncomingSha256 { get; init; }

    /// <summary>True when the archive name had to be normalised/sanitised (reserved device name, invalid characters, ...).</summary>
    public bool NameSanitized { get; init; }

    /// <summary>What exactly was changed in the name.</summary>
    public string? SanitizationNote { get; init; }

    /// <summary>True when the user must explicitly approve this line.</summary>
    public bool RequiresConfirmation => Action == PatchPreviewAction.Conflict;
}

/// <summary>An archive entry that will never be written, with the recorded reason.</summary>
public sealed class RejectedArchiveEntry
{
    /// <summary>Index of the entry inside the archive.</summary>
    public int ArchiveEntryIndex { get; init; }

    /// <summary>Raw entry name.</summary>
    public required string ArchiveEntryName { get; init; }

    /// <summary>Machine readable reason.</summary>
    public required PatchSecurityCode Code { get; init; }

    /// <summary>Human readable reason. Always recorded - rejections are never silent.</summary>
    public required string Reason { get; init; }
}

/// <summary>Counts shown in the confirmation dialog.</summary>
public sealed class PatchPreviewSummary
{
    /// <summary>Files that will be created.</summary>
    public int CreateCount { get; init; }

    /// <summary>Files that will be replaced after being backed up.</summary>
    public int OverwriteCount { get; init; }

    /// <summary>Files that are already identical.</summary>
    public int UnchangedCount { get; init; }

    /// <summary>Files that need manual confirmation.</summary>
    public int ConflictCount { get; init; }

    /// <summary>Entries that were refused.</summary>
    public int RejectedCount { get; init; }

    /// <summary>Total bytes that will be written into the game directory.</summary>
    public long BytesToWrite { get; init; }

    /// <summary>Total bytes that will be backed up (sum of the overwritten files).</summary>
    public long BytesToBackup { get; init; }

    /// <summary>True when the user is shown a confirmation step.</summary>
    public bool NeedsUserDecision => ConflictCount > 0 || RejectedCount > 0 || OverwriteCount > 0;
}

/// <summary>Where a patch came from - filled by the discovery workstream, opaque to this engine.</summary>
public sealed class PatchSourceInfo
{
    /// <summary>Free-form kind, e.g. <c>nextmoe-moyu</c>, <c>manual</c>, <c>local-file</c>.</summary>
    public string Kind { get; init; } = "local-file";

    /// <summary>Provider side patch page id.</summary>
    public string? PatchId { get; init; }

    /// <summary>Provider side resource id.</summary>
    public string? ResourceId { get; init; }

    /// <summary>Page the user was sent to for the download.</summary>
    public string? WebUrl { get; init; }

    /// <summary>
    /// Provider timestamp of the resource (<c>resource_updated_at</c>). Kept verbatim so that a later
    /// "might have an update" comparison can be honest about what it compared.
    /// </summary>
    public string? ResourceUpdatedAt { get; init; }

    /// <summary>Human readable note, e.g. the localisation group.</summary>
    public string? Note { get; init; }
}

/// <summary>Identity of the container that was installed, including how its file names were decoded.</summary>
public sealed class PatchArchiveRef
{
    /// <summary>Original file name.</summary>
    public required string FileName { get; init; }

    /// <summary>Size of the container.</summary>
    public long SizeBytes { get; init; }

    /// <summary>SHA-256 of the <b>container</b>. Proves the download, not the installation.</summary>
    public string? Sha256 { get; init; }

    /// <summary>Container format id (<c>zip</c>, <c>rar</c>, <c>7z</c>, ...).</summary>
    public required string Format { get; init; }

    /// <summary>Name encoding that was actually used, e.g. <c>cp932</c>.</summary>
    public required string NameEncoding { get; init; }

    /// <summary>Number of file entries in the container.</summary>
    public int FileEntryCount { get; init; }

    /// <summary>
    /// Optional BLAKE3 value supplied by the discovery API. Stored for cross-checking only; the engine
    /// never uses it to decide whether a patch is installed.
    /// </summary>
    public string? SourceBlake3 { get; init; }
}

/// <summary>The full overwrite preview: the product-critical "show every landing spot before writing" artefact.</summary>
public sealed class OverwritePreview
{
    /// <summary>Identifier that ties this preview to the sandbox it was computed from.</summary>
    public required string PreviewId { get; init; }

    /// <summary>Archive that was inspected.</summary>
    public required PatchArchiveInfo Archive { get; init; }

    /// <summary>Game directory the files will land in.</summary>
    public required string GameRoot { get; init; }

    /// <summary>Caller supplied game identifier (used for the backup folder layout).</summary>
    public required string GameId { get; init; }

    /// <summary>Display name of the patch.</summary>
    public string? PatchName { get; init; }

    /// <summary>Declared patch types (moyu <c>type[]</c>), used for the save-type refusal.</summary>
    public IReadOnlyList<string> DeclaredTypes { get; init; } = Array.Empty<string>();

    /// <summary>Declared languages.</summary>
    public IReadOnlyList<string> Languages { get; init; } = Array.Empty<string>();

    /// <summary>Declared platforms.</summary>
    public IReadOnlyList<string> Platforms { get; init; } = Array.Empty<string>();

    /// <summary>Where the patch came from.</summary>
    public PatchSourceInfo? Source { get; init; }

    /// <summary>
    /// Optional landing sub-directory chosen by the user. <c>null</c> means "lay the archive out at the
    /// game root". The engine never guesses a top-level folder to strip.
    /// </summary>
    public string? TargetSubDirectory { get; init; }

    /// <summary>When the preview was produced.</summary>
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Sandbox root that holds the extracted files.</summary>
    public required string SandboxRoot { get; init; }

    /// <summary>Directory inside the sandbox that mirrors the landing layout.</summary>
    public required string SandboxFilesRoot { get; init; }

    /// <summary>Every entry, in archive order.</summary>
    public required IReadOnlyList<PatchPreviewEntry> Entries { get; init; }

    /// <summary>Entries that will never be written.</summary>
    public IReadOnlyList<RejectedArchiveEntry> Rejected { get; init; } = Array.Empty<RejectedArchiveEntry>();

    /// <summary>Counts for the confirmation UI.</summary>
    public required PatchPreviewSummary Summary { get; init; }

    /// <summary>Non-fatal observations (encoding guesses, sanitised names, ...).</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>True when the install must not proceed without explicit user confirmation.</summary>
    public bool RequiresConfirmation => Summary.ConflictCount > 0;
}
