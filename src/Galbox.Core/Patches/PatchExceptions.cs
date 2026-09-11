namespace Galbox.Core.Patches;

/// <summary>Why a security-relevant condition was triggered. Every code is recorded, never swallowed.</summary>
public enum PatchSecurityCode
{
    /// <summary>Generic fallback.</summary>
    Unspecified = 0,

    /// <summary>Entry name is absolute, UNC-rooted or carries a drive letter.</summary>
    RootedEntryPath = 1,

    /// <summary>Entry name contains <c>..</c> segments that would escape the destination root (Zip Slip).</summary>
    ParentTraversal = 2,

    /// <summary>Normalised path resolved outside the destination root.</summary>
    PathEscapesRoot = 3,

    /// <summary>Entry is a symbolic link / hard link / reparse point.</summary>
    LinkEntry = 4,

    /// <summary>Destination path traverses an existing reparse point.</summary>
    ReparsePointInTargetChain = 5,

    /// <summary>Entry name contains characters Windows rejects.</summary>
    InvalidCharacters = 6,

    /// <summary>Entry name uses a reserved DOS device name (CON, PRN, AUX, NUL, COM1..9, LPT1..9).</summary>
    ReservedDeviceName = 7,

    /// <summary>Entry name contains an alternate data stream separator.</summary>
    AlternateDataStream = 8,

    /// <summary>Two archive entries map onto the same destination file.</summary>
    DuplicateTarget = 9,

    /// <summary>Entry name cannot be decoded with the selected/auto-detected encoding.</summary>
    UndecodableName = 10,

    /// <summary>Archive declares more entries, or more bytes, than the configured safety budget.</summary>
    ArchiveBudgetExceeded = 11,

    /// <summary>Attempt to execute a self-extracting installer was requested.</summary>
    SelfExtractingExecutionForbidden = 12
}

/// <summary>
/// Raised when the engine refuses an operation for security reasons. The installation is aborted
/// before any file in the game directory is touched.
/// </summary>
public sealed class PatchSecurityException : Exception
{
    public PatchSecurityException(PatchSecurityCode code, string message) : base(message) => Code = code;

    /// <summary>Machine-readable reason.</summary>
    public PatchSecurityCode Code { get; }
}

/// <summary>Why a patch package was refused outright, before any preview work happens.</summary>
public enum PatchRejectionCode
{
    /// <summary>Not rejected.</summary>
    None = 0,

    /// <summary>Caller declared the patch type as <c>save</c>: full-CG saves belong to save management, not overlay install.</summary>
    SaveTypePatch = 1,

    /// <summary>Content sniffing says the archive is a save-data pack, not an overlay patch.</summary>
    SaveLikeContent = 2,

    /// <summary>Self-extracting executable: never executed, never auto-extracted.</summary>
    SelfExtractingExecutable = 3,

    /// <summary>Disc image: not supported by the overlay installer.</summary>
    DiscImage = 4,

    /// <summary>Archive format could not be identified or is unsupported.</summary>
    UnsupportedFormat = 5,

    /// <summary>Archive is password protected / encrypted.</summary>
    EncryptedArchive = 6,

    /// <summary>No extractable files (all entries were directories or all were rejected).</summary>
    NothingToInstall = 7,

    /// <summary>The archive is corrupt or truncated.</summary>
    CorruptArchive = 8
}

/// <summary>Raised when a patch must not be installed at all. Carries the reason so the UI can explain it.</summary>
public sealed class PatchRejectedException : Exception
{
    public PatchRejectedException(PatchRejectionCode code, string message, string? remedy = null)
        : base(message)
    {
        Code = code;
        Remedy = remedy;
    }

    /// <summary>Machine-readable reason.</summary>
    public PatchRejectionCode Code { get; }

    /// <summary>Human readable next step, when one exists.</summary>
    public string? Remedy { get; }
}
