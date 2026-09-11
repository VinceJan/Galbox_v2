using Galbox.Core.Patches;

namespace Galbox.App.Services;

/// <summary>
/// The patch centre's single door onto the local patch installer (<see cref="IPatchEngine"/>).
///
/// Scope, deliberately narrow: the user picks a patch package they already downloaded, and this
/// service inspects it, previews where every file would land, installs it with a real backup,
/// reports per-file outcomes, rolls it back and reports the install ledger.
///
/// It does <b>not</b> download anything and it has no online patch source: there is no HTTP client,
/// no API key and no "one-click" path anywhere behind this interface. Every method works on local
/// files only.
///
/// The ViewModel layer is a presentation shell on top of this interface; keeping the workflow here
/// (UI-free) is what makes the whole flow reachable from the headless acceptance harness.
/// </summary>
public interface ILocalPatchService
{
    /// <summary>
    /// Archive extensions the file picker should offer. The engine identifies containers by content,
    /// not by extension, so this list is only a filter for the "open file" dialog.
    /// </summary>
    IReadOnlyList<string> SupportedArchiveExtensions { get; }

    /// <summary>Identifies a package without extracting it (format, entry count, name encoding, SFX flag).</summary>
    Task<PatchArchiveInfo> InspectAsync(
        string archivePath,
        PatchArchiveReadOptions? readOptions = null,
        CancellationToken ct = default);

    /// <summary>
    /// Unpacks into a sandbox and computes the landing-spot preview. Never throws for a package the
    /// engine refuses: the refusal is returned inside <see cref="PatchPreviewAttempt"/> so the UI can
    /// render the reason and the suggested remedy instead of an exception dialog.
    /// </summary>
    Task<PatchPreviewAttempt> PreviewAsync(
        string archivePath,
        string gameRoot,
        string gameId,
        PatchPreviewOptions? options = null,
        CancellationToken ct = default);

    /// <summary>Applies an approved preview; overwritten files are backed up first.</summary>
    Task<PatchInstallResult> InstallAsync(
        OverwritePreview preview,
        PatchInstallDecisions? decisions = null,
        IProgress<PatchProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>Undoes an install. <paramref name="installId"/> null means "the newest active install".</summary>
    Task<PatchRollbackResult> RollbackAsync(
        string gameRoot,
        string gameId,
        string? installId = null,
        IProgress<PatchProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>What Galbox's ledger can prove about this game directory right now.</summary>
    Task<PatchStatusReport> GetStatusAsync(
        string gameRoot,
        string gameId,
        bool scanHeuristics = true,
        string? installId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Installs that can still be rolled back, newest last. Read from the ledger files only; this is
    /// the list the rollback UI offers.
    /// </summary>
    IReadOnlyList<PatchManifest> ListRollbackCandidates(string gameRoot, string gameId);

    /// <summary>Installs that were left pending by a crash/power loss, each with a dry-run recovery plan.</summary>
    Task<IReadOnlyList<PatchRecoveryPlan>> FindInterruptedAsync(
        string gameRoot,
        string? gameId = null,
        CancellationToken ct = default);

    /// <summary>Applies (or dry-runs) a recovery plan produced by <see cref="FindInterruptedAsync"/>.</summary>
    Task<PatchRollbackResult> RecoverAsync(
        PatchRecoveryPlan plan,
        bool apply = true,
        IProgress<PatchProgress>? progress = null,
        CancellationToken ct = default);
}

/// <summary>Everything the caller can declare about a package before previewing it.</summary>
public sealed class PatchPreviewOptions
{
    /// <summary>Display name recorded in the manifest.</summary>
    public string? PatchName { get; init; }

    /// <summary>
    /// Landing sub-directory inside the game root. Null (the default) means "lay the archive out at the
    /// game root": the engine deliberately never guesses a top-level folder to strip.
    /// </summary>
    public string? TargetSubDirectory { get; init; }

    /// <summary>Patch types declared by a source. A <c>save</c> entry is refused by the engine.</summary>
    public IReadOnlyList<string> DeclaredTypes { get; init; } = Array.Empty<string>();

    /// <summary>Declared languages, recorded in the manifest.</summary>
    public IReadOnlyList<string> Languages { get; init; } = Array.Empty<string>();

    /// <summary>Declared platforms, recorded in the manifest.</summary>
    public IReadOnlyList<string> Platforms { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Optional "known original game file" hash table. A target that matches it is treated as a safe
    /// original instead of a conflict; Galbox has no such table yet, so conflicts are confirmed by hand.
    /// </summary>
    public IReadOnlyDictionary<string, string>? KnownOriginalHashes { get; init; }

    /// <summary>Provider information; for a file the user picked by hand this is <c>local-file</c>.</summary>
    public PatchSourceInfo? Source { get; init; }

    /// <summary>Defaults for a package the user selected from disk.</summary>
    public static PatchPreviewOptions ForLocalFile(string? patchName = null) => new()
    {
        PatchName = patchName,
        Source = new PatchSourceInfo
        {
            Kind = "local-file",
            Note = "用户在本机选择的补丁压缩包"
        }
    };
}
