using Galbox.Core.Patches;
using Microsoft.Extensions.Logging;

namespace Galbox.App.Services;

/// <summary>
/// Default <see cref="ILocalPatchService"/>: a thin, UI-free adapter over <see cref="IPatchEngine"/>.
///
/// It adds no behaviour of its own beyond two things the engine deliberately leaves to the caller:
/// <list type="bullet">
/// <item>turning a refused package into a value (<see cref="PatchPreviewAttempt"/>) instead of an
/// exception, so the page can print the reason and the remedy;</item>
/// <item>listing the installs the ledger can still roll back, which is a read of the ledger files.</item>
/// </list>
/// Everything else - preview, backup, install, verify, rollback, recovery - is the engine's own code.
/// This class never guesses, never fabricates a result and never writes anything the engine did not
/// decide to write.
/// </summary>
public sealed class LocalPatchService : ILocalPatchService
{
    /// <summary>
    /// Extensions the file picker offers. Deliberately restricted to containers the engine can really
    /// extract: <c>.exe</c> (self-extracting) and <c>.iso</c> (disc image) are recognised by the engine
    /// but refused by design, so offering them would only produce a refusal.
    /// </summary>
    private static readonly string[] PickerExtensions = { ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz" };

    private readonly IPatchEngine _engine;
    private readonly PatchLedger _ledger;
    private readonly ILogger<LocalPatchService>? _logger;

    /// <summary>Creates the service around a resolved engine.</summary>
    public LocalPatchService(IPatchEngine engine, PatchLedger ledger, ILogger<LocalPatchService>? logger = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _logger = logger;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> SupportedArchiveExtensions => PickerExtensions;

    /// <inheritdoc />
    public Task<PatchArchiveInfo> InspectAsync(
        string archivePath,
        PatchArchiveReadOptions? readOptions = null,
        CancellationToken ct = default)
        => _engine.InspectAsync(archivePath, readOptions, ct);

    /// <inheritdoc />
    public async Task<PatchPreviewAttempt> PreviewAsync(
        string archivePath,
        string gameRoot,
        string gameId,
        PatchPreviewOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);

        var effective = options ?? new PatchPreviewOptions();
        var request = new PatchPreviewRequest
        {
            ArchivePath = archivePath,
            GameRoot = gameRoot,
            GameId = gameId,
            PatchName = effective.PatchName,
            TargetSubDirectory = effective.TargetSubDirectory,
            DeclaredTypes = effective.DeclaredTypes,
            Languages = effective.Languages,
            Platforms = effective.Platforms,
            KnownOriginalHashes = effective.KnownOriginalHashes,
            Source = effective.Source ?? new PatchSourceInfo { Kind = "local-file" }
        };

        var attempt = await _engine.TryPreviewAsync(request, ct).ConfigureAwait(false);
        if (attempt.Succeeded && attempt.Preview is not null)
        {
            var summary = attempt.Preview.Summary;
            _logger?.LogInformation(
                "Patch preview {PreviewId}: create={Create}, overwrite={Overwrite}, conflict={Conflict}, unchanged={Unchanged}, rejected={Rejected}",
                attempt.Preview.PreviewId, summary.CreateCount, summary.OverwriteCount, summary.ConflictCount,
                summary.UnchangedCount, summary.RejectedCount);
        }
        else
        {
            _logger?.LogWarning("Patch package refused: {Code} - {Message}", attempt.RejectionCode, attempt.Message);
        }

        return attempt;
    }

    /// <inheritdoc />
    public async Task<PatchInstallResult> InstallAsync(
        OverwritePreview preview,
        PatchInstallDecisions? decisions = null,
        IProgress<PatchProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(preview);

        var request = new PatchInstallRequest
        {
            Preview = preview,
            Decisions = decisions ?? PatchInstallDecisions.None,
            // The engine refuses unconfirmed conflicts; this is the safety property the page relies on.
            RequireConflictConfirmation = true,
            // Re-hash the targets right before the first write, so a game update between preview and
            // install cannot be silently clobbered.
            RevalidateTargets = true
        };

        var result = await _engine.InstallAsync(request, progress, ct).ConfigureAwait(false);
        _logger?.LogInformation(
            "Patch install {InstallId}: success={Success}, created={Created}, overwritten={Overwritten}, errors={Errors}",
            result.InstallId, result.Success, result.CreatedCount, result.OverwrittenCount, result.Errors.Count);
        return result;
    }

    /// <inheritdoc />
    public async Task<PatchRollbackResult> RollbackAsync(
        string gameRoot,
        string gameId,
        string? installId = null,
        IProgress<PatchProgress>? progress = null,
        CancellationToken ct = default)
    {
        var result = await _engine.RollbackAsync(
            new PatchRollbackRequest { GameRoot = gameRoot, GameId = gameId, InstallId = installId },
            progress,
            ct).ConfigureAwait(false);

        _logger?.LogInformation(
            "Patch rollback {InstallId}: success={Success}, restored={Restored}, deleted={Deleted}, byteIdentical={ByteIdentical}",
            result.InstallId, result.Success, result.RestoredCount, result.DeletedCount, result.ByteIdenticalToPreInstall);
        return result;
    }

    /// <inheritdoc />
    public Task<PatchStatusReport> GetStatusAsync(
        string gameRoot,
        string gameId,
        bool scanHeuristics = true,
        string? installId = null,
        CancellationToken ct = default)
        => _engine.GetStatusAsync(
            new PatchStatusRequest
            {
                GameRoot = gameRoot,
                GameId = gameId,
                ScanHeuristics = scanHeuristics,
                InstallId = installId
            },
            ct);

    /// <inheritdoc />
    public IReadOnlyList<PatchManifest> ListRollbackCandidates(string gameRoot, string gameId)
    {
        if (string.IsNullOrWhiteSpace(gameRoot) || string.IsNullOrWhiteSpace(gameId))
        {
            return Array.Empty<PatchManifest>();
        }

        try
        {
            // IsActive == committed / pending / failed: exactly the installs whose files are (or may be)
            // in the game directory right now. A rolled-back install is intentionally not offered again.
            return _ledger.ReadAllInstalls(gameRoot, gameId)
                .Where(m => m.IsActive && m.Files.Count > 0)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Reading the patch ledger for game {GameId} failed", gameId);
            return Array.Empty<PatchManifest>();
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<PatchRecoveryPlan>> FindInterruptedAsync(
        string gameRoot,
        string? gameId = null,
        CancellationToken ct = default)
        => _engine.FindInterruptedAsync(gameRoot, gameId, ct);

    /// <inheritdoc />
    public Task<PatchRollbackResult> RecoverAsync(
        PatchRecoveryPlan plan,
        bool apply = true,
        IProgress<PatchProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return _engine.RecoverAsync(new PatchRecoverRequest { Plan = plan, Apply = apply }, progress, ct);
    }
}
