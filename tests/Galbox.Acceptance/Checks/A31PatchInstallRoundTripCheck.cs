using Galbox.App.Services;
using Galbox.App.ViewModels;
using Galbox.Acceptance.Support;
using Galbox.Core.Patches;
using Galbox.Data.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace Galbox.Acceptance.Checks;

/// <summary>
/// A31 - A real patch package goes all the way through the new patch-centre code path.
///
/// The engine itself was already verified elsewhere. What this check proves is that the path the
/// <b>user</b> takes - the one added by this work line - produces the same guarantees:
/// a synthetic game directory is patched, the target bytes are re-read from disk, the install is
/// rolled back and the whole game tree is compared file by file against the pre-install snapshot.
///
/// Phase 1 drives <see cref="ILocalPatchService"/> (the UI's door) and asserts on the engine's own
/// typed results. Phase 2 drives the real <see cref="PatchCenterViewModel"/> - the same type the page
/// resolves from the container - and asserts on its observable presentation surface.
///
/// Nothing here touches a real game directory: both phases use throwaway trees under
/// <c>%TEMP%\Galbox\acceptance-patches</c>.
/// </summary>
public sealed class A31PatchInstallRoundTripCheck : IAcceptanceCheck
{
    /// <inheritdoc />
    public string Id => "A31";

    /// <inheritdoc />
    public string Title => "Local patch round trip through the patch centre: preview -> install -> bytes change -> rollback -> byte identical";

    /// <inheritdoc />
    public async Task<CheckResult> RunAsync(AcceptanceContext context, CancellationToken cancellationToken)
    {
        const string expected =
            "the patch package is inspected and previewed with the overwrite/new/rejected sets kept apart; "
            + "an unconfirmed conflict blocks the install and changes nothing; a confirmed install succeeds "
            + "with every per-file operation reported as Ok and the target bytes really replaced; the rollback "
            + "reports ByteIdenticalToPreInstall and the game tree matches the pre-install snapshot file for file";

        var details = new List<string>();
        var failures = new List<string>();

        var service = context.Services.GetService<ILocalPatchService>();
        if (service is null)
        {
            details.Add("ILocalPatchService did not resolve from the container.");
            details.Add("Without it the patch centre has no way to reach the patch engine, so the whole flow is unreachable.");
            return CheckResult.Fail(Id, Title, expected, "ILocalPatchService is not registered").With(details.ToArray());
        }

        var scratch = PatchTestFixtures.NewScratch("roundtrip");
        var gameRoot = PatchTestFixtures.CreateGameRoot(scratch);
        var archivePath = PatchTestFixtures.CreateOrdinaryPatch(scratch);
        var gameId = "acceptance-a31-" + Guid.NewGuid().ToString("N")[..8];

        details.Add($"Scratch root      : {scratch}");
        details.Add($"Game root         : {gameRoot}");
        details.Add($"Patch package     : {archivePath} ({new FileInfo(archivePath).Length} bytes)");
        details.Add($"Game id (ledger)  : {gameId}");

        var preInstall = PatchTestFixtures.SnapshotGameFiles(gameRoot);
        details.Add($"Pre-install files : {preInstall.Count}");
        foreach (var line in PatchTestFixtures.Describe(preInstall)) details.Add($"    {line}");

        // =============================================================== Phase 1: the service
        details.Add(string.Empty);
        details.Add("=== Phase 1: through ILocalPatchService (the UI's door) ===");

        // ---- inspect
        var archive = await service.InspectAsync(archivePath, null, cancellationToken).ConfigureAwait(false);
        details.Add($"Inspect           : kind={archive.Kind}, format={archive.FormatId}, entries={archive.EntryCount}, "
                     + $"files={archive.FileEntryCount}, encoding={archive.EffectiveNameEncoding} (auto={archive.NameEncodingWasAutoDetected}), "
                     + $"sha256={(archive.Sha256 ?? "null")[..16]}..., extractable={archive.IsExtractable}");
        if (archive.Kind != PatchArchiveKind.Zip || archive.FileEntryCount != 2)
        {
            failures.Add($"Phase 1: inspect reported kind={archive.Kind} fileEntries={archive.FileEntryCount}, expected Zip/2.");
        }

        // ---- preview
        var attempt = await service.PreviewAsync(
            archivePath,
            gameRoot,
            gameId,
            PatchPreviewOptions.ForLocalFile("A31 合成补丁"),
            cancellationToken).ConfigureAwait(false);

        if (!attempt.Succeeded || attempt.Preview is null)
        {
            details.Add($"Preview           : REFUSED code={attempt.RejectionCode} message={attempt.Message}");
            failures.Add($"Phase 1: the ordinary package was refused ({attempt.RejectionCode}: {attempt.Message}).");
            return Finish(failures, details, expected, scratch);
        }

        var preview = attempt.Preview;
        details.Add($"Preview id        : {preview.PreviewId}");
        details.Add($"Summary           : create={preview.Summary.CreateCount}, overwrite={preview.Summary.OverwriteCount}, "
                     + $"conflict={preview.Summary.ConflictCount}, unchanged={preview.Summary.UnchangedCount}, "
                     + $"rejected={preview.Summary.RejectedCount}, bytesToWrite={preview.Summary.BytesToWrite}, "
                     + $"bytesToBackup={preview.Summary.BytesToBackup}");
        foreach (var entry in preview.Entries)
        {
            details.Add($"    [{entry.Action,-9}] {entry.TargetRelativePath}  (provenance={entry.Provenance}) {entry.AssessmentReason}");
        }

        if (preview.Entries.Count != 2) failures.Add($"Phase 1: preview listed {preview.Entries.Count} entries, expected 2.");
        if (preview.Summary.CreateCount != 1) failures.Add($"Phase 1: create count is {preview.Summary.CreateCount}, expected 1 (data/newfile.txt).");
        if (preview.Summary.ConflictCount != 1) failures.Add($"Phase 1: conflict count is {preview.Summary.ConflictCount}, expected 1 (data/original.txt has unknown provenance).");
        if (preview.Summary.RejectedCount != 0) failures.Add($"Phase 1: an ordinary package produced {preview.Summary.RejectedCount} rejections.");
        if (!preview.Summary.NeedsUserDecision) failures.Add("Phase 1: the preview does not ask for a user decision even though a conflict exists.");

        // ---- install without confirming the conflict: must write nothing
        var blocked = await service.InstallAsync(preview, null, null, cancellationToken).ConfigureAwait(false);
        details.Add(string.Empty);
        details.Add($"Install (unconfirmed) : success={blocked.Success}, blockingConflicts={blocked.BlockingConflicts.Count}, errors={blocked.Errors.Count}");
        foreach (var conflict in blocked.BlockingConflicts) details.Add($"    blocked: {conflict}");
        var afterBlocked = PatchTestFixtures.SnapshotGameFiles(gameRoot);
        var blockedDiff = PatchTestFixtures.Diff(preInstall, afterBlocked);
        details.Add($"    game tree changed by the blocked install: {(blockedDiff.Count == 0 ? "no" : "YES")}");
        if (blocked.Success) failures.Add("Phase 1: an install with an unconfirmed conflict reported success.");
        if (blocked.BlockingConflicts.Count != 1) failures.Add($"Phase 1: expected 1 blocking conflict, got {blocked.BlockingConflicts.Count}.");
        if (blockedDiff.Count != 0)
        {
            failures.Add("Phase 1: the blocked install modified the game tree.");
            foreach (var line in blockedDiff) details.Add($"    {line}");
        }

        // ---- install with the conflict confirmed
        var progress = new ProgressRecorder();
        var decisions = new PatchInstallDecisions { ConfirmedConflicts = blocked.BlockingConflicts };
        var install = await service.InstallAsync(preview, decisions, progress, cancellationToken).ConfigureAwait(false);

        details.Add(string.Empty);
        details.Add($"Install (confirmed)   : success={install.Success}, installId={install.InstallId}, "
                     + $"created={install.CreatedCount}, overwritten={install.OverwrittenCount}, "
                     + $"bytesWritten={install.BytesWritten}, bytesBackedUp={install.BytesBackedUp}, "
                     + $"rollbackAvailable={install.RollbackAvailable}");
        details.Add($"    operations ({install.Operations.Count}):");
        foreach (var operation in install.Operations)
        {
            details.Add($"      [{operation.Status,-6}] {operation.Action,-9} {operation.RelativePath}"
                        + (operation.BackupRel is null ? string.Empty : $" (backup: {operation.BackupRel})")
                        + (operation.Message is null ? string.Empty : $" - {operation.Message}"));
        }

        details.Add($"    progress reports  : {progress.Reports.Count}");
        foreach (var phase in progress.Reports.Select(r => r.Phase).Distinct())
        {
            var last = progress.Reports.Last(r => r.Phase == phase);
            details.Add($"      phase {phase,-8} completed={last.Completed}/{last.Total} fraction={(last.Fraction?.ToString("F2") ?? "n/a")}");
        }

        details.Add($"    ledger manifest   : {install.ManifestPath}");
        details.Add($"    in-game manifest  : {install.InGameManifestPath}");

        if (!install.Success) failures.Add($"Phase 1: the confirmed install failed: {string.Join(" | ", install.Errors)}");
        if (install.Operations.Count != 2) failures.Add($"Phase 1: {install.Operations.Count} per-file results were reported, expected 2.");
        if (install.Operations.Any(o => o.Status != PatchFileOperationStatus.Ok))
        {
            failures.Add("Phase 1: at least one per-file operation was not Ok.");
        }

        if (install.OverwrittenCount != 1) failures.Add($"Phase 1: overwritten count is {install.OverwrittenCount}, expected 1.");
        if (install.CreatedCount != 1) failures.Add($"Phase 1: created count is {install.CreatedCount}, expected 1.");
        if (progress.Reports.Count == 0) failures.Add("Phase 1: the install reported no progress at all.");
        if (install.Manifest is null || install.Manifest.Files.Count != 2) failures.Add("Phase 1: the install manifest does not record both files.");

        // ---- the bytes on disk really changed
        var text = await File.ReadAllTextAsync(Path.Combine(gameRoot, "data", "original.txt"), cancellationToken).ConfigureAwait(false);
        var createdExists = File.Exists(Path.Combine(gameRoot, "data", "newfile.txt"));
        var afterInstall = PatchTestFixtures.SnapshotGameFiles(gameRoot);
        details.Add(string.Empty);
        details.Add($"data/original.txt now: \"{text}\"");
        details.Add($"data/newfile.txt now : exists={createdExists}");
        if (!string.Equals(text, "patched script v2 - \u6c49\u5316", StringComparison.Ordinal))
        {
            failures.Add($"Phase 1: data/original.txt is \"{text}\", expected the patched content.");
        }

        if (!createdExists) failures.Add("Phase 1: the patch created no data/newfile.txt.");
        if (afterInstall.ContainsKey(@"data\original.txt") && preInstall.TryGetValue(@"data\original.txt", out var beforeHash)
            && string.Equals(afterInstall[@"data\original.txt"], beforeHash, StringComparison.Ordinal))
        {
            failures.Add("Phase 1: data/original.txt has the same hash as before the install.");
        }

        // ---- status + rollback candidates
        var statusAfterInstall = await service.GetStatusAsync(gameRoot, gameId, true, null, cancellationToken).ConfigureAwait(false);
        details.Add(string.Empty);
        details.Add($"Status after install  : {statusAfterInstall.Status} / evidence={statusAfterInstall.Evidence} / scope={statusAfterInstall.EvidenceScope}");
        details.Add($"    explanation       : {statusAfterInstall.Explanation}");
        details.Add($"    verified files    : {statusAfterInstall.Files.Count} (changed: {statusAfterInstall.ChangedFiles.Count})");
        if (statusAfterInstall.Status != Galbox.Core.Patches.PatchStatus.Installed) failures.Add($"Phase 1: status after install is {statusAfterInstall.Status}, expected Installed.");
        if (statusAfterInstall.Evidence != PatchEvidenceClass.Fact) failures.Add("Phase 1: the Installed verdict is not backed by Fact evidence.");

        var candidates = service.ListRollbackCandidates(gameRoot, gameId);
        details.Add($"Rollback candidates   : {candidates.Count}");
        foreach (var candidate in candidates)
        {
            details.Add($"    {candidate.InstallId} status={candidate.Status} files={candidate.Files.Count} name={candidate.PatchName}");
        }

        if (!candidates.Any(c => c.InstallId == install.InstallId))
        {
            failures.Add("Phase 1: the new install is not offered as a rollback candidate.");
        }

        // ---- rollback
        var rollbackProgress = new ProgressRecorder();
        var rollback = await service.RollbackAsync(gameRoot, gameId, install.InstallId, rollbackProgress, cancellationToken).ConfigureAwait(false);
        details.Add(string.Empty);
        details.Add($"Rollback              : success={rollback.Success}, nothingToDo={rollback.NothingToDo}, "
                     + $"restored={rollback.RestoredCount}, deleted={rollback.DeletedCount}, skipped={rollback.SkippedCount}, "
                     + $"byteIdentical={rollback.ByteIdenticalToPreInstall}");
        foreach (var file in rollback.Files)
        {
            details.Add($"      [{file.Outcome,-22}] {file.RelativePath} byteIdentical={file.ByteIdentical}");
        }

        if (!rollback.Success) failures.Add($"Phase 1: rollback failed: {string.Join(" | ", rollback.Errors)}");
        if (!rollback.ByteIdenticalToPreInstall) failures.Add("Phase 1: rollback did not prove byte identity with the pre-install state.");
        if (rollback.RestoredCount < 1) failures.Add("Phase 1: rollback restored no file.");

        var afterRollback = PatchTestFixtures.SnapshotGameFiles(gameRoot);
        var rollbackDiff = PatchTestFixtures.Diff(preInstall, afterRollback);
        details.Add($"    game tree identical to the pre-install snapshot: {(rollbackDiff.Count == 0 ? "YES" : "NO")}");
        foreach (var line in rollbackDiff) details.Add($"      {line}");
        if (rollbackDiff.Count != 0)
        {
            failures.Add("Phase 1: the game tree does not match the pre-install snapshot after the rollback.");
        }

        var statusAfterRollback = await service.GetStatusAsync(gameRoot, gameId, false, null, cancellationToken).ConfigureAwait(false);
        details.Add($"Status after rollback : {statusAfterRollback.Status} / evidence={statusAfterRollback.Evidence}");
        if (statusAfterRollback.Status != Galbox.Core.Patches.PatchStatus.RolledBack) failures.Add($"Phase 1: status after rollback is {statusAfterRollback.Status}, expected RolledBack.");

        // ============================================================ Phase 2: the ViewModel
        details.Add(string.Empty);
        details.Add("=== Phase 2: through the real PatchCenterViewModel ===");
        var vmScratch = PatchTestFixtures.NewScratch("roundtrip-vm");
        var vmGameRoot = PatchTestFixtures.CreateGameRoot(vmScratch);
        var vmArchive = PatchTestFixtures.CreateOrdinaryPatch(vmScratch);
        var vmPreInstall = PatchTestFixtures.SnapshotGameFiles(vmGameRoot);

        var navigation = new HeadlessNavigationService();
        var viewModel = ActivatorUtilities.CreateInstance<PatchCenterViewModel>(context.Services, navigation);
        var probe = new PatchCenterProbe(viewModel);

        if (!probe.HasAll("SelectPatchArchiveAsync", "InstallSelectedArchiveAsync", "RollbackAsync", "ConfirmAllConflicts",
                          "Preview", "ArchiveInfo", "InstallResult", "RollbackResult", "OverwriteEntries", "CreateEntries",
                          "ConflictEntries", "UnchangedEntries", "RejectedEntries", "InstallOperations", "FailedOperations"))
        {
            failures.Add($"Phase 2: the ViewModel does not expose the patch-centre workflow ({probe.MissingSummary()}).");
            details.Add($"  [FAIL] ViewModel workflow members missing: {probe.MissingSummary()}");
            return Finish(failures, details, expected, scratch);
        }

        details.Add($"  ViewModel         : {viewModel.GetType().FullName} (resolved with ILocalPatchService from the container)");

        viewModel.SelectedGame = new GameInfo
        {
            Id = 900001,
            NameOriginal = "Acceptance Fixture",
            InstallPath = vmGameRoot,
            MainExecutable = "game.exe"
        };

        var selected = await probe.CallAsync("SelectPatchArchiveAsync", vmArchive, CancellationToken.None).ConfigureAwait(false);
        var vmArchiveInfo = probe.Get<PatchArchiveInfo>("ArchiveInfo");
        var vmPreview = probe.Get<OverwritePreview>("Preview");
        details.Add($"  SelectPatchArchive: returned={selected}");
        details.Add($"    ArchiveInfo     : {(vmArchiveInfo is null ? "null" : $"{vmArchiveInfo.FormatId}, {vmArchiveInfo.FileEntryCount} file(s), encoding={vmArchiveInfo.EffectiveNameEncoding}")}");
        details.Add($"    Preview         : {(vmPreview is null ? "null" : $"{vmPreview.Entries.Count} entry/entries, rejected={vmPreview.Rejected.Count}")}");
        details.Add($"    display sets    : overwrite={probe.Count("OverwriteEntries")}, create={probe.Count("CreateEntries")}, "
                     + $"conflict={probe.Count("ConflictEntries")}, unchanged={probe.Count("UnchangedEntries")}, rejected={probe.Count("RejectedEntries")}");

        if (selected is not true) failures.Add("Phase 2: SelectPatchArchiveAsync reported failure.");
        if (vmPreview is null) failures.Add("Phase 2: the ViewModel holds no preview after selecting the package.");
        if (probe.Count("ConflictEntries") != 1) failures.Add($"Phase 2: the conflict set holds {probe.Count("ConflictEntries")} row(s), expected 1.");
        if (probe.Count("CreateEntries") != 1) failures.Add($"Phase 2: the create set holds {probe.Count("CreateEntries")} row(s), expected 1.");
        if (probe.Count("RejectedEntries") != 0) failures.Add("Phase 2: an ordinary package produced a rejected row.");

        // Unconfirmed: the UI must not be able to write yet.
        var vmBlocked = await probe.CallAsync("InstallSelectedArchiveAsync", CancellationToken.None).ConfigureAwait(false);
        var vmBlockedSnapshot = PatchTestFixtures.SnapshotGameFiles(vmGameRoot);
        details.Add($"  Install (unconfirmed): returned={vmBlocked}, game tree changed={(PatchTestFixtures.Diff(vmPreInstall, vmBlockedSnapshot).Count != 0 ? "YES" : "no")}");
        if (vmBlocked is true) failures.Add("Phase 2: the ViewModel installed while the conflict was still unconfirmed.");
        if (PatchTestFixtures.Diff(vmPreInstall, vmBlockedSnapshot).Count != 0) failures.Add("Phase 2: the ViewModel wrote to the game while the conflict was unconfirmed.");

        probe.Call("ConfirmAllConflicts");
        details.Add($"  ConfirmAllConflicts : confirmed={probe.Get("ConfirmedConflictCount")}");

        var vmInstalled = await probe.CallAsync("InstallSelectedArchiveAsync", CancellationToken.None).ConfigureAwait(false);
        var vmInstallResult = probe.Get<PatchInstallResult>("InstallResult");
        details.Add($"  Install (confirmed) : returned={vmInstalled}, success={vmInstallResult?.Success}, "
                     + $"operations={probe.Count("InstallOperations")}, failed={probe.Count("FailedOperations")}");
        details.Add($"    progress        : {probe.Get("InstallProgress")} ({probe.Get("InstallPhase")})");

        if (vmInstalled is not true || vmInstallResult is null || !vmInstallResult.Success)
        {
            failures.Add("Phase 2: the ViewModel did not report a successful confirmed install.");
        }

        if (probe.Count("InstallOperations") != 2) failures.Add($"Phase 2: the ViewModel shows {probe.Count("InstallOperations")} per-file results, expected 2.");
        if (probe.Count("FailedOperations") != 0) failures.Add($"Phase 2: the ViewModel reports {probe.Count("FailedOperations")} failed file(s) on a successful install.");

        var vmText = await File.ReadAllTextAsync(Path.Combine(vmGameRoot, "data", "original.txt"), cancellationToken).ConfigureAwait(false);
        var vmChanged = PatchTestFixtures.Diff(vmPreInstall, PatchTestFixtures.SnapshotGameFiles(vmGameRoot));
        details.Add($"    data/original.txt now: \"{vmText}\"");
        details.Add($"    game tree diffs : {vmChanged.Count}");
        foreach (var line in vmChanged) details.Add($"      {line}");
        if (!string.Equals(vmText, "patched script v2 - \u6c49\u5316", StringComparison.Ordinal)) failures.Add("Phase 2: the ViewModel path did not replace the target file's bytes.");
        if (vmChanged.Count == 0) failures.Add("Phase 2: the game tree is unchanged after a supposedly successful install.");

        // ---- rollback through the ViewModel
        var vmRolledBack = await probe.CallAsync("RollbackAsync", null, CancellationToken.None).ConfigureAwait(false);
        var vmRollbackResult = probe.Get<PatchRollbackResult>("RollbackResult");
        details.Add($"  Rollback            : returned={vmRolledBack}, success={vmRollbackResult?.Success}, "
                     + $"byteIdentical={vmRollbackResult?.ByteIdenticalToPreInstall}, restored={vmRollbackResult?.RestoredCount}, "
                     + $"deleted={vmRollbackResult?.DeletedCount}");

        if (vmRolledBack is not true || vmRollbackResult is null || !vmRollbackResult.Success)
        {
            failures.Add("Phase 2: the ViewModel rollback did not succeed.");
        }

        if (vmRollbackResult is not null && !vmRollbackResult.ByteIdenticalToPreInstall)
        {
            failures.Add("Phase 2: the ViewModel rollback did not prove byte identity.");
        }

        var vmAfterRollback = PatchTestFixtures.SnapshotGameFiles(vmGameRoot);
        var vmRollbackDiff = PatchTestFixtures.Diff(vmPreInstall, vmAfterRollback);
        details.Add($"    game tree identical to the pre-install snapshot: {(vmRollbackDiff.Count == 0 ? "YES" : "NO")}");
        foreach (var line in vmRollbackDiff) details.Add($"      {line}");
        if (vmRollbackDiff.Count != 0) failures.Add("Phase 2: the ViewModel rollback did not restore the game tree byte for byte.");

        // The VM's ledger view must reflect the new state without inventing anything.
        await probe.CallAsync("RefreshPatchLedgerAsync", CancellationToken.None).ConfigureAwait(false);
        var vmStatus = probe.Get<PatchStatusReport>("StatusReport");
        details.Add($"  Status ledger       : {vmStatus?.Status} / evidence={vmStatus?.Evidence} / headline={probe.Get("StatusHeadline")}");
        if (vmStatus is null) failures.Add("Phase 2: the ViewModel holds no status report after refreshing the ledger.");

        return Finish(failures, details, expected, scratch);
    }

    /// <summary>Builds the verdict and appends the failure list.</summary>
    private CheckResult Finish(List<string> failures, List<string> details, string expected, string scratch)
    {
        details.Add(string.Empty);
        details.Add($"Scratch kept for inspection: {scratch}");
        var actual = failures.Count == 0
            ? "preview/install/rollback all behaved: bytes really replaced, rollback byte identical, ViewModel path identical"
            : $"{failures.Count} assertion(s) failed";

        var result = failures.Count == 0
            ? CheckResult.Pass(Id, Title, expected, actual)
            : CheckResult.Fail(Id, Title, expected, actual);

        foreach (var failure in failures) details.Add($"FAIL REASON: {failure}");
        return result.With(details.ToArray());
    }

    /// <summary>Captures engine progress callbacks synchronously (a <see cref="Progress{T}"/> would post them).</summary>
    private sealed class ProgressRecorder : IProgress<PatchProgress>
    {
        /// <summary>Every report, in order.</summary>
        public List<PatchProgress> Reports { get; } = new();

        /// <inheritdoc />
        public void Report(PatchProgress value)
        {
            lock (Reports) Reports.Add(value);
        }
    }
}
