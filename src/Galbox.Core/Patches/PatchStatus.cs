namespace Galbox.Core.Patches;

/// <summary>Result of the status evaluation. Never let inference masquerade as fact.</summary>
public enum PatchStatus
{
    /// <summary>
    /// Nothing could be concluded. Used when the game directory is unreadable, the ledger is corrupt,
    /// or the caller asked about an install id that does not exist.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// No Galbox ledger entry exists for this game and no third-party trace was found.
    /// This is a fact <b>about Galbox's ledger</b>, not proof that nobody ever patched the game by hand.
    /// </summary>
    NotInstalled = 1,

    /// <summary>
    /// A committed Galbox manifest exists and every recorded file still matches its recorded hash.
    /// Only Galbox's own ledger can produce this verdict.
    /// </summary>
    Installed = 2,

    /// <summary>
    /// A committed Galbox manifest exists but some recorded files are missing or changed
    /// (game update, another patch, antivirus). The changed files are listed.
    /// </summary>
    ModifiedSinceInstall = 3,

    /// <summary>
    /// A pending (never committed) manifest exists: the previous install was interrupted.
    /// Recoverable through <see cref="PatchInstaller.RecoverAsync"/>.
    /// </summary>
    Interrupted = 4,

    /// <summary>A committed manifest exists and was explicitly rolled back.</summary>
    RolledBack = 5,

    /// <summary>
    /// No ledger entry, but heuristic traces were found. <b>Inference only</b> - never reported as installed.
    /// </summary>
    Suspected = 6,

    /// <summary>The supplied package is a save-data pack, which by design does not go through the overlay installer.</summary>
    NotApplicable = 7
}

/// <summary>Reliability class of the evidence behind a verdict. This is the fact/inference firewall.</summary>
public enum PatchEvidenceClass
{
    /// <summary>No evidence at all.</summary>
    None = 0,

    /// <summary>Heuristics, naming conventions, timestamps: may only ever justify "suspected" or "unknown".</summary>
    Inference = 1,

    /// <summary>Galbox's own journal/manifest plus a hash re-check of the files it wrote.</summary>
    Fact = 2
}

/// <summary>Which question a verdict actually answers.</summary>
public enum PatchEvidenceScope
{
    /// <summary>No scope.</summary>
    None = 0,

    /// <summary>Only says what Galbox itself recorded and wrote; says nothing about installs done by other tools.</summary>
    GalboxLedger = 1,

    /// <summary>Read-only observation of the game directory; cannot attribute anything.</summary>
    GameDirectoryHeuristics = 2,

    /// <summary>The caller's own package metadata (e.g. a declared <c>save</c> type).</summary>
    PackageMetadata = 3
}

/// <summary>State of a single file that a manifest claims to have installed.</summary>
public enum PatchFileVerificationState
{
    /// <summary>File exists and matches the recorded hash.</summary>
    Match = 0,

    /// <summary>File exists but its content differs from the recorded hash.</summary>
    HashMismatch = 1,

    /// <summary>File recorded by the manifest is gone.</summary>
    Missing = 2,

    /// <summary>File exists but could not be read (locked, permissions).</summary>
    Unreadable = 3
}

/// <summary>Re-check result for one manifest line.</summary>
public sealed class PatchFileVerification
{
    /// <summary>Path relative to the game root.</summary>
    public required string RelativePath { get; init; }

    /// <summary>Hash recorded at install time.</summary>
    public required string ExpectedSha256 { get; init; }

    /// <summary>Hash found on disk now (null when missing/unreadable).</summary>
    public string? ActualSha256 { get; init; }

    /// <summary>Size recorded at install time.</summary>
    public long ExpectedSizeBytes { get; init; }

    /// <summary>Size found on disk now (null when missing/unreadable).</summary>
    public long? ActualSizeBytes { get; init; }

    /// <summary>Outcome.</summary>
    public required PatchFileVerificationState State { get; init; }

    /// <summary>Detail for <see cref="PatchFileVerificationState.Unreadable"/>.</summary>
    public string? Note { get; init; }
}

/// <summary>Confidence attached to a heuristic trace. Even "high" only ever justifies "suspected".</summary>
public enum PatchHeuristicConfidence
{
    /// <summary>Weak signal (a marketing text file, a locale DLL).</summary>
    Low = 0,

    /// <summary>Stronger signal (engine-specific patch slot such as <c>patch.xp3</c>).</summary>
    Medium = 1
}

/// <summary>A single third-party trace found in the game directory. Evidence of *something*, never evidence of *this patch*.</summary>
public sealed class HeuristicPatchTrace
{
    /// <summary>Stable code, e.g. <c>kirikiri.patch-xp3</c>.</summary>
    public required string Code { get; init; }

    /// <summary>File or directory that triggered the trace.</summary>
    public required string RelativePath { get; init; }

    /// <summary>Why this is a trace.</summary>
    public required string Detail { get; init; }

    /// <summary>Signal strength.</summary>
    public PatchHeuristicConfidence Confidence { get; init; } = PatchHeuristicConfidence.Low;

    /// <summary>Last write time of the traced file, when available.</summary>
    public DateTimeOffset? LastWriteTime { get; init; }
}

/// <summary>
/// The status verdict. <see cref="Evidence"/> and <see cref="EvidenceScope"/> are part of the contract:
/// callers are expected to show them, and the engine guarantees that
/// <see cref="PatchStatus.Installed"/> / <see cref="PatchStatus.ModifiedSinceInstall"/> can only be
/// produced by <see cref="PatchEvidenceClass.Fact"/> evidence.
/// </summary>
public sealed class PatchStatusReport
{
    /// <summary>The verdict.</summary>
    public required PatchStatus Status { get; init; }

    /// <summary>Whether the verdict is a fact or an inference.</summary>
    public required PatchEvidenceClass Evidence { get; init; }

    /// <summary>What the verdict is actually about.</summary>
    public required PatchEvidenceScope EvidenceScope { get; init; }

    /// <summary>Concrete source of the evidence (file path or "directory scan").</summary>
    public string? EvidenceSource { get; init; }

    /// <summary>Plain-language explanation including explicit caveats, meant to be shown verbatim.</summary>
    public required string Explanation { get; init; }

    /// <summary>Game directory the verdict is about.</summary>
    public string? GameRoot { get; init; }

    /// <summary>Install the verdict is about (when one applies).</summary>
    public string? InstallId { get; init; }

    /// <summary>Patch name recorded in the manifest.</summary>
    public string? PatchName { get; init; }

    /// <summary>When the install was committed or started.</summary>
    public DateTimeOffset? InstalledAt { get; init; }

    /// <summary>How many installs Galbox recorded for this game.</summary>
    public int ManagedInstallCount { get; init; }

    /// <summary>Per-file re-check for the active install.</summary>
    public IReadOnlyList<PatchFileVerification> Files { get; init; } = Array.Empty<PatchFileVerification>();

    /// <summary>Subset of <see cref="Files"/> that no longer matches (drives the "changed since install" UI).</summary>
    public IReadOnlyList<PatchFileVerification> ChangedFiles { get; init; } = Array.Empty<PatchFileVerification>();

    /// <summary>Heuristic traces; only present when the verdict came from inference or when explicitly requested.</summary>
    public IReadOnlyList<HeuristicPatchTrace> Traces { get; init; } = Array.Empty<HeuristicPatchTrace>();

    /// <summary>Additional caveats (multiple installs, rolled-back history, ...).</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    /// <summary>True when the verdict is backed by Galbox's own ledger.</summary>
    public bool IsAuthoritative => Evidence == PatchEvidenceClass.Fact;

    /// <summary>True when a managed rollback can be offered.</summary>
    public bool RollbackAvailable => Evidence == PatchEvidenceClass.Fact &&
                                     Status is PatchStatus.Installed or PatchStatus.ModifiedSinceInstall or PatchStatus.Interrupted;
}

/// <summary>Rules that keep inference out of factual verdicts. Public so tests and UI code can assert the same invariants.</summary>
public static class PatchStatusRules
{
    /// <summary>Verdicts that may only be produced by Galbox's own ledger.</summary>
    public static readonly PatchStatus[] FactOnlyStatuses =
    {
        PatchStatus.Installed,
        PatchStatus.ModifiedSinceInstall,
        PatchStatus.Interrupted,
        PatchStatus.RolledBack
    };

    /// <summary>Verdicts that by construction come from heuristics.</summary>
    public static readonly PatchStatus[] InferenceOnlyStatuses =
    {
        PatchStatus.Suspected
    };

    /// <summary>True when a verdict is allowed to carry the given evidence class.</summary>
    public static bool IsCompatible(PatchStatus status, PatchEvidenceClass evidence)
    {
        if (FactOnlyStatuses.Contains(status)) return evidence == PatchEvidenceClass.Fact;
        if (InferenceOnlyStatuses.Contains(status)) return evidence == PatchEvidenceClass.Inference;
        return true;
    }
}
