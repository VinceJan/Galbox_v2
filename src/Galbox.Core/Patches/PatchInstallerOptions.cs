namespace Galbox.Core.Patches;

/// <summary>
/// Engine-wide configuration. Every default is chosen so that the engine is safe with **no**
/// configuration at all: backups outside the game tree, conservative name policy, save-type
/// patches refused.
/// </summary>
public sealed class PatchInstallerOptions
{
    /// <summary>
    /// Where overwritten files are backed up. Must never sit inside a game directory.
    /// Default: <c>%LOCALAPPDATA%\Galbox\patchbak</c>.
    /// </summary>
    public string BackupRoot { get; init; } = DefaultBackupRoot;

    /// <summary>
    /// Where archives are unpacked for previewing. Default: <c>%TEMP%\Galbox\patchsink</c>.
    /// The sandbox is always wiped unless <see cref="KeepSandboxAfterInstall"/> is set.
    /// </summary>
    public string SandboxRoot { get; init; } = DefaultSandboxRoot;

    /// <summary>
    /// How many installs keep their file backups. The engine only ever *reports* what should be
    /// deleted; nothing is removed silently.
    /// </summary>
    public int BackupRetentionCount { get; init; } = 5;

    /// <summary>How unrepresentable Windows file names are handled.</summary>
    public PatchNamePolicy NamePolicy { get; init; } = PatchNamePolicy.SanitizeWithWarning;

    /// <summary>
    /// When false (default) an archive that looks like a save-data pack is refused with
    /// <see cref="PatchRejectionCode.SaveLikeContent"/>.
    /// </summary>
    public bool AllowSaveLikeContent { get; init; }

    /// <summary>Relative path of the in-game manifest copy.</summary>
    public string InGameManifestRelativePath { get; init; } = @".galbox\patch-manifest.json";

    /// <summary>File name of the per-install manifest inside the backup folder.</summary>
    public string LedgerManifestFileName { get; init; } = "manifest.json";

    /// <summary>Keep the extracted sandbox after a successful install (debugging aid).</summary>
    public bool KeepSandboxAfterInstall { get; init; }

    /// <summary>Default read options applied when a request does not specify its own.</summary>
    public PatchArchiveReadOptions DefaultArchiveRead { get; init; } = new();

    /// <summary>Writes to the copied file are followed by a read-back hash check (kills AV silent failures).</summary>
    public bool VerifyAfterWrite { get; init; } = true;

    /// <summary>Optional log sink; the engine never depends on a logging framework.</summary>
    public Action<string>? Log { get; init; }

    /// <summary>Returns a copy that stores backups under a different root (user-configurable, e.g. a second disk).</summary>
    public PatchInstallerOptions WithBackupRoot(string backupRoot) => Copy(backupRoot: backupRoot);

    /// <summary>Returns a copy that extracts into a different sandbox root.</summary>
    public PatchInstallerOptions WithSandboxRoot(string sandboxRoot) => Copy(sandboxRoot: sandboxRoot);

    /// <summary>Returns a copy with a different log sink.</summary>
    public PatchInstallerOptions WithLog(Action<string>? log) => Copy(log: log);

    private PatchInstallerOptions Copy(
        string? backupRoot = null,
        string? sandboxRoot = null,
        Action<string>? log = null) => new()
        {
            BackupRoot = backupRoot ?? BackupRoot,
            SandboxRoot = sandboxRoot ?? SandboxRoot,
            BackupRetentionCount = BackupRetentionCount,
            NamePolicy = NamePolicy,
            AllowSaveLikeContent = AllowSaveLikeContent,
            InGameManifestRelativePath = InGameManifestRelativePath,
            LedgerManifestFileName = LedgerManifestFileName,
            KeepSandboxAfterInstall = KeepSandboxAfterInstall,
            DefaultArchiveRead = DefaultArchiveRead,
            VerifyAfterWrite = VerifyAfterWrite,
            Log = log ?? Log
        };

    /// <summary><c>%LOCALAPPDATA%\Galbox\patchbak</c> (falls back to the temp folder if LOCALAPPDATA is unavailable).</summary>
    public static string DefaultBackupRoot
    {
        get
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return string.IsNullOrEmpty(local)
                ? Path.Combine(Path.GetTempPath(), "Galbox", "patchbak")
                : Path.Combine(local, "Galbox", "patchbak");
        }
    }

    /// <summary><c>%TEMP%\Galbox\patchsink</c>.</summary>
    public static string DefaultSandboxRoot => Path.Combine(Path.GetTempPath(), "Galbox", "patchsink");
}
