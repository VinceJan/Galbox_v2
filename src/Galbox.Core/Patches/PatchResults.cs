namespace Galbox.Core.Patches;

/// <summary>Input for <see cref="PatchInstaller.PreviewAsync"/>.</summary>
public sealed class PatchPreviewRequest
{
    /// <summary>Path of the patch package the user already downloaded.</summary>
    public required string ArchivePath { get; init; }

    /// <summary>Game directory the patch would be laid over.</summary>
    public required string GameRoot { get; init; }

    /// <summary>Caller supplied game identifier; also used for the backup folder layout.</summary>
    public required string GameId { get; init; }

    /// <summary>Display name of the patch.</summary>
    public string? PatchName { get; init; }

    /// <summary>Archive decoding options; overrides the engine default when set.</summary>
    public PatchArchiveReadOptions? ArchiveRead { get; init; }

    /// <summary>
    /// Optional sub-directory of the game the files should land in. Leave null (the default) to lay the
    /// archive out at the game root - the engine deliberately never guesses a top-level folder to strip.
    /// </summary>
    public string? TargetSubDirectory { get; init; }

    /// <summary>Patch types declared by the provider (moyu <c>type[]</c>). A <c>save</c> entry blocks the install.</summary>
    public IReadOnlyList<string> DeclaredTypes { get; init; } = Array.Empty<string>();

    /// <summary>Declared languages, recorded in the manifest.</summary>
    public IReadOnlyList<string> Languages { get; init; } = Array.Empty<string>();

    /// <summary>Declared platforms, recorded in the manifest.</summary>
    public IReadOnlyList<string> Platforms { get; init; } = Array.Empty<string>();

    /// <summary>Where the package came from.</summary>
    public PatchSourceInfo? Source { get; init; }

    /// <summary>BLAKE3 of the container as published by the provider (informational only).</summary>
    public string? SourceBlake3 { get; init; }

    /// <summary>
    /// Optional table of "known original game file" hashes (relative path to SHA-256). When supplied, a
    /// file that matches this table is treated as a safe-to-replace original instead of a conflict.
    /// </summary>
    public IReadOnlyDictionary<string, string>? KnownOriginalHashes { get; init; }

    /// <summary>Override the sandbox root for this request.</summary>
    public string? SandboxRoot { get; init; }
}

/// <summary>Non-throwing preview result, for callers that want to render a refusal instead of catching.</summary>
public sealed class PatchPreviewAttempt
{
    /// <summary>True when a preview was produced.</summary>
    public bool Succeeded { get; init; }

    /// <summary>The preview, when <see cref="Succeeded"/> is true.</summary>
    public OverwritePreview? Preview { get; init; }

    /// <summary>Archive identity, when it could be determined.</summary>
    public PatchArchiveInfo? Archive { get; init; }

    /// <summary>Refusal reason, when the package was rejected outright.</summary>
    public PatchRejectionCode RejectionCode { get; init; } = PatchRejectionCode.None;

    /// <summary>Human readable refusal message.</summary>
    public string? Message { get; init; }

    /// <summary>Suggested next step for the user.</summary>
    public string? Remedy { get; init; }

    /// <summary>Security condition that aborted the preview, when applicable.</summary>
    public PatchSecurityCode? SecurityCode { get; init; }
}

/// <summary>User choices applied to a preview before installing.</summary>
public sealed class PatchInstallDecisions
{
    /// <summary>Conflict paths (relative, as reported by the preview) the user explicitly approved.</summary>
    public IReadOnlyCollection<string> ConfirmedConflicts { get; init; } = Array.Empty<string>();

    /// <summary>Paths the user chose not to install.</summary>
    public IReadOnlyCollection<string> ExcludedPaths { get; init; } = Array.Empty<string>();

    /// <summary>Default decisions: no conflicts approved, nothing excluded.</summary>
    public static PatchInstallDecisions None { get; } = new();
}

/// <summary>Input for <see cref="PatchInstaller.InstallAsync"/>.</summary>
public sealed class PatchInstallRequest
{
    /// <summary>The preview the user approved. Must have been produced by this engine on this machine.</summary>
    public required OverwritePreview Preview { get; init; }

    /// <summary>User decisions about conflicts and exclusions.</summary>
    public PatchInstallDecisions Decisions { get; init; } = PatchInstallDecisions.None;

    /// <summary>When true (default) unconfirmed conflicts abort the install instead of overwriting.</summary>
    public bool RequireConflictConfirmation { get; init; } = true;

    /// <summary>
    /// When true, every target is re-hashed and compared against the preview before the first write, so a
    /// game update that happened between preview and install cannot be silently clobbered.
    /// </summary>
    public bool RevalidateTargets { get; init; } = true;
}

/// <summary>Per-file install outcome.</summary>
public enum PatchFileOperationStatus
{
    /// <summary>The file was written and verified.</summary>
    Ok = 0,

    /// <summary>The file was written but the read-back hash differed (typical of antivirus interference).</summary>
    ReadBackMismatch = 1,

    /// <summary>The file was skipped (excluded by the user, or already identical).</summary>
    Skipped = 2,

    /// <summary>Writing or backing up the file failed.</summary>
    Failed = 3
}

/// <summary>Result for one file of an install.</summary>
public sealed class PatchFileOperationResult
{
    /// <summary>Path relative to the game root.</summary>
    public required string RelativePath { get; init; }

    /// <summary>Planned action.</summary>
    public required PatchPreviewAction Action { get; init; }

    /// <summary>Outcome.</summary>
    public required PatchFileOperationStatus Status { get; init; }

    /// <summary>Backup location relative to the install folder, when a backup was taken.</summary>
    public string? BackupRel { get; init; }

    /// <summary>Hash written to the game directory.</summary>
    public string? HashAfter { get; init; }

    /// <summary>Explanation for non-<see cref="PatchFileOperationStatus.Ok"/> outcomes.</summary>
    public string? Message { get; init; }
}

/// <summary>Full result of an installation attempt.</summary>
public sealed class PatchInstallResult
{
    /// <summary>True when every planned file was written and verified.</summary>
    public bool Success { get; init; }

    /// <summary>Install identifier (also the backup folder name).</summary>
    public required string InstallId { get; init; }

    /// <summary>The manifest that was written, in its final state.</summary>
    public PatchManifest? Manifest { get; init; }

    /// <summary>Ledger manifest path.</summary>
    public string? ManifestPath { get; init; }

    /// <summary>In-game bundle path.</summary>
    public string? InGameManifestPath { get; init; }

    /// <summary>Per-file outcomes.</summary>
    public IReadOnlyList<PatchFileOperationResult> Operations { get; init; } = Array.Empty<PatchFileOperationResult>();

    /// <summary>Files created.</summary>
    public int CreatedCount { get; init; }

    /// <summary>Files replaced (each with a verified backup).</summary>
    public int OverwrittenCount { get; init; }

    /// <summary>Bytes written into the game directory.</summary>
    public long BytesWritten { get; init; }

    /// <summary>Bytes copied into the backup store.</summary>
    public long BytesBackedUp { get; init; }

    /// <summary>Conflicts that blocked the install because the user did not confirm them.</summary>
    public IReadOnlyList<string> BlockingConflicts { get; init; } = Array.Empty<string>();

    /// <summary>Fatal errors.</summary>
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    /// <summary>Non-fatal observations, including read-back mismatches.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>True when a rollback can be offered for this install.</summary>
    public bool RollbackAvailable { get; init; }

    /// <summary>Retention advice (the engine never deletes backups on its own).</summary>
    public PatchRetentionPlan? Retention { get; init; }
}

/// <summary>Input for <see cref="PatchInstaller.RollbackAsync"/>.</summary>
public sealed class PatchRollbackRequest
{
    /// <summary>Game directory.</summary>
    public required string GameRoot { get; init; }

    /// <summary>Game identifier.</summary>
    public required string GameId { get; init; }

    /// <summary>Install to roll back; null means "the newest active install".</summary>
    public string? InstallId { get; init; }

    /// <summary>
    /// When false (default) a <c>Created</c> file whose content no longer matches what the install wrote is
    /// left alone and reported, instead of being deleted.
    /// </summary>
    public bool DeleteModifiedCreatedFiles { get; init; }
}

/// <summary>Result of a rollback or recovery.</summary>
public sealed class PatchRollbackResult
{
    /// <summary>True when every file reached the state it had before the install.</summary>
    public bool Success { get; init; }

    /// <summary>Install that was rolled back.</summary>
    public required string InstallId { get; init; }

    /// <summary>True when there was nothing to roll back (e.g. the install id is unknown).</summary>
    public bool NothingToDo { get; init; }

    /// <summary>Per-file outcomes.</summary>
    public IReadOnlyList<PatchRestoreRecord> Files { get; init; } = Array.Empty<PatchRestoreRecord>();

    /// <summary>Files restored from backup.</summary>
    public int RestoredCount { get; init; }

    /// <summary>Files deleted because the install created them.</summary>
    public int DeletedCount { get; init; }

    /// <summary>Files left untouched (changed by the user or another patch, or already absent).</summary>
    public int SkippedCount { get; init; }

    /// <summary>
    /// The hard guarantee: every restored file is byte identical to the pre-install hash recorded in the
    /// manifest. False means the game directory is <b>not</b> provably back to its previous state.
    /// </summary>
    public bool ByteIdenticalToPreInstall { get; init; }

    /// <summary>Directories that were created by the install and could be pruned because they are empty again.</summary>
    public IReadOnlyList<string> PrunedDirectories { get; init; } = Array.Empty<string>();

    /// <summary>Manifest after the rollback (status <see cref="PatchManifestStatus.RolledBack"/>).</summary>
    public PatchManifest? Manifest { get; init; }

    /// <summary>Errors.</summary>
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    /// <summary>Non-fatal observations.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>Input for <see cref="PatchInstaller.GetStatusAsync"/>.</summary>
public sealed class PatchStatusRequest
{
    /// <summary>Game directory.</summary>
    public required string GameRoot { get; init; }

    /// <summary>Game identifier.</summary>
    public required string GameId { get; init; }

    /// <summary>Question about a specific install; null means "the newest active install".</summary>
    public string? InstallId { get; init; }

    /// <summary>Run the heuristic scan when the ledger has nothing to say (default true).</summary>
    public bool ScanHeuristics { get; init; } = true;

    /// <summary>Maximum number of heuristic hits to report.</summary>
    public int MaxTraces { get; init; } = 25;

    /// <summary>
    /// When set, the caller's package declares these types; a <c>save</c> declaration yields
    /// <see cref="PatchStatus.NotApplicable"/> instead of a menu of install states.
    /// </summary>
    public IReadOnlyList<string>? DeclaredTypes { get; init; }
}

/// <summary>What a recovery run would do, computed without touching the disk.</summary>
public sealed class PatchRecoveryPlan
{
    /// <summary>Interrupted install.</summary>
    public required string InstallId { get; init; }

    /// <summary>Game identifier.</summary>
    public required string GameId { get; init; }

    /// <summary>Game directory.</summary>
    public required string GameRoot { get; init; }

    /// <summary>Patch name, when recorded.</summary>
    public string? PatchName { get; init; }

    /// <summary>When the install started.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>Status found in the journal.</summary>
    public required PatchManifestStatus Status { get; init; }

    /// <summary>Why recovery is needed.</summary>
    public required string Reason { get; init; }

    /// <summary>Files whose previous contents can be restored from a verified backup.</summary>
    public int RestorableFileCount { get; init; }

    /// <summary>Files that the interrupted install created and that can be removed.</summary>
    public int RemovableFileCount { get; init; }

    /// <summary>Files whose state cannot be determined (no backup, or changed since).</summary>
    public int IndeterminateFileCount { get; init; }

    /// <summary>Step-by-step description shown to the user before applying.</summary>
    public IReadOnlyList<string> Steps { get; init; } = Array.Empty<string>();

    /// <summary>The manifest backing this plan.</summary>
    public required PatchManifest Manifest { get; init; }
}

/// <summary>Input for <see cref="PatchInstaller.RecoverAsync"/>.</summary>
public sealed class PatchRecoverRequest
{
    /// <summary>The plan to apply, or to merely preview when <see cref="Apply"/> is false.</summary>
    public required PatchRecoveryPlan Plan { get; init; }

    /// <summary>False performs a dry run: the plan is returned as-is and nothing is written.</summary>
    public bool Apply { get; init; } = true;
}

/// <summary>Progress notification payload; the UI workstream can bind to this without a callback contract.</summary>
public sealed class PatchProgress
{
    /// <summary>Phase identifier, e.g. <c>extract</c>, <c>backup</c>, <c>write</c>, <c>verify</c>, <c>rollback</c>.</summary>
    public required string Phase { get; init; }

    /// <summary>Items already processed.</summary>
    public int Completed { get; init; }

    /// <summary>Total items in the phase (0 when unknown).</summary>
    public int Total { get; init; }

    /// <summary>Relative path currently being processed.</summary>
    public string? CurrentItem { get; init; }

    /// <summary>Human readable message.</summary>
    public string? Message { get; init; }

    /// <summary>Completion ratio in [0,1], or null when unknown.</summary>
    public double? Fraction => Total > 0 ? Math.Clamp(Completed / (double)Total, 0, 1) : null;
}
