namespace Galbox.Core.Patches;

/// <summary>Journal state of an installation. The manifest <b>is</b> the journal: it is written before the first byte is touched.</summary>
public enum PatchManifestStatus
{
    /// <summary>Written, but the file operations have not finished. A crash leaves this behind, which is how an interrupted install is detected.</summary>
    Pending = 0,

    /// <summary>All files were written and read back successfully.</summary>
    Committed = 1,

    /// <summary>Rolled back. The backups are intentionally kept.</summary>
    RolledBack = 2,

    /// <summary>Aborted with failures; the state on disk is indeterminate and a rollback is recommended.</summary>
    Failed = 3
}

/// <summary>What happened to a single file during install.</summary>
public enum PatchFileAction
{
    /// <summary>The file did not exist before and was created by this install.</summary>
    Created = 0,

    /// <summary>The file existed and was replaced (a backup was taken first).</summary>
    Overwritten = 1
}

/// <summary>Per-file ledger line. Together with the on-disk hashes this is the only evidence that may support an "installed" verdict.</summary>
public sealed class PatchManifestFileEntry
{
    /// <summary>Path relative to the game root.</summary>
    public required string Rel { get; init; }

    /// <summary>Create or overwrite.</summary>
    public required PatchFileAction Action { get; init; }

    /// <summary>Backup location relative to the install folder (<c>files/...</c>), null for created files.</summary>
    public string? BackupRel { get; init; }

    /// <summary>Size of the replaced file before the install.</summary>
    public long? SizeBefore { get; init; }

    /// <summary>SHA-256 of the replaced file before the install (null for created files).</summary>
    public string? HashBefore { get; init; }

    /// <summary>Size after the install.</summary>
    public long SizeAfter { get; init; }

    /// <summary>SHA-256 after the install. This is what a later status check re-verifies.</summary>
    public required string HashAfter { get; init; }

    /// <summary>True when the file was read back and matched <see cref="HashAfter"/> right after writing.</summary>
    public bool VerifiedAfterWrite { get; init; }

    /// <summary>Explanation when <see cref="VerifiedAfterWrite"/> is false.</summary>
    public string? VerificationNote { get; init; }
}

/// <summary>
/// The authoritative record of one installation. Serialised to
/// <c>%LOCALAPPDATA%\Galbox\patchbak\&lt;gameId&gt;\&lt;installId&gt;\manifest.json</c> and mirrored into
/// <c>&lt;gameRoot&gt;\.galbox\patch-manifest.json</c> so that the state survives a machine or database swap.
/// </summary>
public sealed class PatchManifest
{
    /// <summary>Schema version of this document.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Time-sortable install identifier (<c>yyyyMMddTHHmmssfffZ-xxxxxxxx</c>). Ordering encodes the patch chain.</summary>
    public required string InstallId { get; init; }

    /// <summary>Caller supplied game identifier.</summary>
    public required string GameId { get; init; }

    /// <summary>Game directory this install targeted.</summary>
    public required string GameRoot { get; init; }

    /// <summary>Optional landing sub-directory that was used.</summary>
    public string? TargetSubDirectory { get; init; }

    /// <summary>Display name of the patch.</summary>
    public string? PatchName { get; init; }

    /// <summary>Declared patch types.</summary>
    public IReadOnlyList<string> DeclaredTypes { get; init; } = Array.Empty<string>();

    /// <summary>Declared languages.</summary>
    public IReadOnlyList<string> Languages { get; init; } = Array.Empty<string>();

    /// <summary>Declared platforms.</summary>
    public IReadOnlyList<string> Platforms { get; init; } = Array.Empty<string>();

    /// <summary>Provider information, verbatim.</summary>
    public PatchSourceInfo? Source { get; init; }

    /// <summary>Container identity.</summary>
    public required PatchArchiveRef Archive { get; init; }

    /// <summary>When the install started (the pending manifest is written at this instant).</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When the install finished successfully.</summary>
    public DateTimeOffset? CommittedAt { get; init; }

    /// <summary>When the install was rolled back.</summary>
    public DateTimeOffset? RolledBackAt { get; init; }

    /// <summary>Journal state.</summary>
    public PatchManifestStatus Status { get; init; } = PatchManifestStatus.Pending;

    /// <summary>Every file this install touched.</summary>
    public IReadOnlyList<PatchManifestFileEntry> Files { get; init; } = Array.Empty<PatchManifestFileEntry>();

    /// <summary>
    /// Directories this install had to create, relative to the game root. Recorded so that a rollback can
    /// prune exactly those and nothing else.
    /// </summary>
    public IReadOnlyList<string> CreatedDirectories { get; init; } = Array.Empty<string>();

    /// <summary>Free-form note added by a rollback or recovery run.</summary>
    public string? StatusNote { get; init; }

    /// <summary>Engine version that produced the manifest.</summary>
    public string? EngineVersion { get; init; }

    /// <summary>Warnings recorded at install time.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>Number of files that replaced an existing file.</summary>
    public int OverwrittenCount => Files.Count(f => f.Action == PatchFileAction.Overwritten);

    /// <summary>Number of files that were newly created.</summary>
    public int CreatedCount => Files.Count(f => f.Action == PatchFileAction.Created);

    /// <summary>True when at least one file can be restored from a backup.</summary>
    public bool HasBackups => Files.Any(f => !string.IsNullOrEmpty(f.BackupRel));

    /// <summary>True when this install is the one a status check should verify.</summary>
    public bool IsActive => Status is PatchManifestStatus.Committed or PatchManifestStatus.Pending or PatchManifestStatus.Failed;
}

/// <summary>
/// The in-game copy: every install Galbox has recorded for this game directory.
/// One file per game, so a fresh machine can recognise the patch history without the database.
/// </summary>
public sealed class PatchGameManifestBundle
{
    /// <summary>Schema version of this document.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Game directory the bundle belongs to.</summary>
    public required string GameRoot { get; init; }

    /// <summary>Game identifier.</summary>
    public required string GameId { get; init; }

    /// <summary>Last time the bundle was rewritten.</summary>
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>All installs, newest last.</summary>
    public List<PatchManifest> Installs { get; init; } = new();
}
