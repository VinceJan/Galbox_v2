using Galbox.App.Services;
using Galbox.App.ViewModels;
using Galbox.Acceptance.Support;
using Galbox.Core.Patches;
using Galbox.Data.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace Galbox.Acceptance.Checks;

/// <summary>
/// A32 - A refused package must be visibly refused, never silently "successful".
///
/// Two independent refusal paths are exercised end to end through the patch-centre code:
///
///   1. A hostile archive carrying a Zip Slip entry (<c>..\escaped.txt</c>). The engine must refuse
///      that entry, keep it out of the overwrite/create sets, and report it with its reason - while
///      still installing the legitimate entry it sits next to. The strongest part of the assertion is
///      on the file system: nothing may appear outside the game root.
///   2. A package declared as save data (<c>type=save</c>). The engine refuses the whole preview, and
///      the refusal has to reach the UI as a message plus a remedy, not as an empty success.
///
/// Everything is done on throwaway trees under <c>%TEMP%</c>.
/// </summary>
public sealed class A32PatchRejectionVisibleCheck : IAcceptanceCheck
{
    /// <inheritdoc />
    public string Id => "A32";

    /// <inheritdoc />
    public string Title => "Refused patch content stays visible: Zip Slip entries and save-type packages are refused, reported and written nowhere";

    /// <inheritdoc />
    public async Task<CheckResult> RunAsync(AcceptanceContext context, CancellationToken cancellationToken)
    {
        const string expected =
            "the Zip Slip entry is listed in the preview's rejected set with PatchSecurityCode.ParentTraversal and a "
            + "human readable reason, never counted as an overwrite/create, and the file it targets is never created "
            + "outside the game root; a package declared as save data is refused with PatchPreviewAttempt.Succeeded=false "
            + "and a non-empty message plus remedy that the ViewModel surfaces instead of reporting success";

        var details = new List<string>();
        var failures = new List<string>();

        var service = context.Services.GetService<ILocalPatchService>();
        if (service is null)
        {
            details.Add("ILocalPatchService did not resolve from the container, so no refusal can reach a user at all.");
            return CheckResult.Fail(Id, Title, expected, "ILocalPatchService is not registered").With(details.ToArray());
        }

        var scratch = PatchTestFixtures.NewScratch("reject");
        var gameRoot = PatchTestFixtures.CreateGameRoot(scratch);
        var evilArchive = PatchTestFixtures.CreateZipSlipPatch(scratch);
        var ordinaryArchive = PatchTestFixtures.CreateOrdinaryPatch(scratch, "ordinary.zip");
        var gameId = "acceptance-a32-" + Guid.NewGuid().ToString("N")[..8];

        // The traversal target of "..\escaped.txt" relative to <scratch>\game.
        var escapeTarget = Path.Combine(scratch, "escaped.txt");

        details.Add($"Scratch root      : {scratch}");
        details.Add($"Game root         : {gameRoot}");
        details.Add($"Hostile package   : {evilArchive}");
        details.Add($"Escape target     : {escapeTarget} (exists before: {File.Exists(escapeTarget)})");

        var preInstall = PatchTestFixtures.SnapshotGameFiles(gameRoot);

        // ==================================================== 1: Zip Slip must be refused visibly
        details.Add(string.Empty);
        details.Add("=== 1. Zip Slip entry in the patch package ===");

        var archive = await service.InspectAsync(evilArchive, null, cancellationToken).ConfigureAwait(false);
        details.Add($"Inspect           : kind={archive.Kind}, fileEntries={archive.FileEntryCount}");

        var attempt = await service.PreviewAsync(
            evilArchive,
            gameRoot,
            gameId,
            PatchPreviewOptions.ForLocalFile("A32 路径穿越测试包"),
            cancellationToken).ConfigureAwait(false);

        if (!attempt.Succeeded || attempt.Preview is null)
        {
            details.Add($"Preview           : REFUSED ENTIRELY code={attempt.RejectionCode} message={attempt.Message}");
            failures.Add($"1: the hostile package was refused as a whole ({attempt.RejectionCode}), so the rejection is not visible per entry.");
            return Finish(failures, details, expected);
        }

        var preview = attempt.Preview;
        details.Add($"Preview           : entries={preview.Entries.Count}, rejected={preview.Rejected.Count}");
        details.Add($"Summary           : create={preview.Summary.CreateCount}, overwrite={preview.Summary.OverwriteCount}, "
                     + $"conflict={preview.Summary.ConflictCount}, rejected={preview.Summary.RejectedCount}");

        details.Add("  entries that would be written:");
        foreach (var entry in preview.Entries)
        {
            details.Add($"    [{entry.Action,-9}] {entry.TargetRelativePath}");
        }

        details.Add("  entries that will never be written:");
        foreach (var rejected in preview.Rejected)
        {
            details.Add($"    [rejected] raw='{rejected.ArchiveEntryName}' code={rejected.Code} reason={rejected.Reason}");
        }

        if (preview.Rejected.Count != 1)
        {
            failures.Add($"1: expected exactly 1 rejected entry, got {preview.Rejected.Count}.");
        }
        else
        {
            var rejected = preview.Rejected[0];
            if (rejected.Code != PatchSecurityCode.ParentTraversal)
            {
                failures.Add($"1: the Zip Slip entry was rejected with code {rejected.Code}, expected ParentTraversal.");
            }

            if (string.IsNullOrWhiteSpace(rejected.Reason))
            {
                failures.Add("1: the rejected entry carries no human readable reason.");
            }

            if (!rejected.ArchiveEntryName.Contains("..", StringComparison.Ordinal))
            {
                failures.Add($"1: the reported raw name '{rejected.ArchiveEntryName}' does not show what was refused.");
            }
        }

        if (preview.Summary.RejectedCount != preview.Rejected.Count)
        {
            failures.Add($"1: the summary counts {preview.Summary.RejectedCount} rejections but {preview.Rejected.Count} are listed.");
        }

        // The refusal must not be folded into the "will be written" numbers.
        var writtenPaths = preview.Entries.Select(e => e.TargetRelativePath).ToList();
        if (writtenPaths.Any(p => p.Contains("escaped", StringComparison.OrdinalIgnoreCase)))
        {
            failures.Add("1: the Zip Slip entry also appears in the overwrite/create set.");
        }

        if (preview.Summary.CreateCount != 1 || preview.Summary.OverwriteCount != 0 || preview.Summary.ConflictCount != 0)
        {
            failures.Add($"1: expected create=1/overwrite=0/conflict=0 for the surviving entry, got "
                         + $"create={preview.Summary.CreateCount}/overwrite={preview.Summary.OverwriteCount}/conflict={preview.Summary.ConflictCount}.");
        }

        if (File.Exists(escapeTarget))
        {
            failures.Add($"1: the preview wrote '{escapeTarget}' outside the game root.");
        }

        // ---- install the surviving entry; the refusal must persist and keep writing nothing outside
        var install = await service.InstallAsync(preview, null, null, cancellationToken).ConfigureAwait(false);
        details.Add(string.Empty);
        details.Add($"Install           : success={install.Success}, created={install.CreatedCount}, operations={install.Operations.Count}");
        foreach (var operation in install.Operations)
        {
            details.Add($"    [{operation.Status,-6}] {operation.RelativePath}");
        }

        details.Add($"Escape target exists after install: {File.Exists(escapeTarget)}");
        var afterInstall = PatchTestFixtures.SnapshotGameFiles(gameRoot);
        var outsideLines = new List<string>();
        foreach (var file in Directory.EnumerateFiles(scratch, "*", SearchOption.TopDirectoryOnly))
        {
            // The scratch root only ever contained fixture folders and the packages we created.
            var name = Path.GetFileName(file);
            if (name is "escaped.txt") outsideLines.Add(file);
        }

        details.Add($"Files written directly into the scratch root: {(outsideLines.Count == 0 ? "none" : string.Join(", ", outsideLines))}");

        if (File.Exists(escapeTarget))
        {
            failures.Add("1: the install created the file the Zip Slip entry pointed at - the containment check leaked.");
        }

        if (outsideLines.Count != 0)
        {
            failures.Add("1: a file appeared outside the game root after the install.");
        }

        if (!install.Success) failures.Add($"1: the legitimate entry was not installed: {string.Join(" | ", install.Errors)}");
        if (!File.Exists(Path.Combine(gameRoot, "data", "newfile.txt")))
        {
            failures.Add("1: data/newfile.txt (the legitimate entry) was not created.");
        }

        if (PatchTestFixtures.Diff(preInstall, afterInstall).Any(l => l.StartsWith("CHANGED", StringComparison.Ordinal)))
        {
            failures.Add("1: an existing game file was modified even though the package only added a new one.");
        }

        // ============================================ 2: declared save-type package is refused
        details.Add(string.Empty);
        details.Add("=== 2. Package declared as save data ===");

        var saveAttempt = await service.PreviewAsync(
            ordinaryArchive,
            gameRoot,
            gameId,
            new PatchPreviewOptions
            {
                PatchName = "A32 存档类型包",
                DeclaredTypes = new[] { "save" },
                Source = new PatchSourceInfo { Kind = "local-file" }
            },
            cancellationToken).ConfigureAwait(false);

        details.Add($"Preview           : succeeded={saveAttempt.Succeeded}, code={saveAttempt.RejectionCode}");
        details.Add($"    message       : {saveAttempt.Message}");
        details.Add($"    remedy        : {saveAttempt.Remedy}");

        if (saveAttempt.Succeeded) failures.Add("2: a package declared as save data was previewed as if it were an overlay patch.");
        if (saveAttempt.RejectionCode != PatchRejectionCode.SaveTypePatch)
        {
            failures.Add($"2: expected rejection code SaveTypePatch, got {saveAttempt.RejectionCode}.");
        }

        if (string.IsNullOrWhiteSpace(saveAttempt.Message)) failures.Add("2: the refusal carries no message, so a user would see nothing.");
        if (string.IsNullOrWhiteSpace(saveAttempt.Remedy)) failures.Add("2: the refusal carries no remedy.");

        // ==================================================== 3: the same story through the ViewModel
        details.Add(string.Empty);
        details.Add("=== 3. The same refusals as the patch centre page shows them ===");

        var vmScratch = PatchTestFixtures.NewScratch("reject-vm");
        var vmGameRoot = PatchTestFixtures.CreateGameRoot(vmScratch);
        var vmEvilArchive = PatchTestFixtures.CreateZipSlipPatch(vmScratch);
        var vmEscapeTarget = Path.Combine(vmScratch, "escaped.txt");

        var navigation = new HeadlessNavigationService();
        var viewModel = ActivatorUtilities.CreateInstance<PatchCenterViewModel>(context.Services, navigation);
        var probe = new PatchCenterProbe(viewModel);

        if (!probe.HasAll("SelectPatchArchiveAsync", "RejectedEntries", "Preview", "OverwriteEntries",
                          "CreateEntries", "ConflictEntries", "UnchangedEntries", "PackageRejectionMessage"))
        {
            failures.Add($"3: the ViewModel does not expose the refusal surface ({probe.MissingSummary()}).");
            details.Add($"  [FAIL] missing members: {probe.MissingSummary()}");
            return Finish(failures, details, expected);
        }

        viewModel.SelectedGame = new GameInfo
        {
            Id = 900002,
            NameOriginal = "Acceptance Fixture (hostile package)",
            InstallPath = vmGameRoot,
            MainExecutable = "game.exe"
        };

        var selected = await probe.CallAsync("SelectPatchArchiveAsync", vmEvilArchive, CancellationToken.None).ConfigureAwait(false);
        var vmPreview = probe.Get<OverwritePreview>("Preview");
        details.Add($"  SelectPatchArchive: returned={selected}");
        details.Add($"    display sets    : overwrite={probe.Count("OverwriteEntries")}, create={probe.Count("CreateEntries")}, "
                     + $"conflict={probe.Count("ConflictEntries")}, unchanged={probe.Count("UnchangedEntries")}, "
                     + $"rejected={probe.Count("RejectedEntries")}");
        details.Add($"    rejection message: {probe.Get("PackageRejectionMessage")}");

        if (selected is not true) failures.Add("3: SelectPatchArchiveAsync failed for the hostile package.");
        if (vmPreview is null) failures.Add("3: the ViewModel produced no preview for the hostile package.");
        if (probe.Count("RejectedEntries") != 1) failures.Add($"3: the ViewModel's rejected set holds {probe.Count("RejectedEntries")} row(s), expected 1.");

        var vmWritten = probe.Count("OverwriteEntries") + probe.Count("CreateEntries") + probe.Count("ConflictEntries");
        if (vmWritten != 1) failures.Add($"3: the ViewModel offers {vmWritten} file(s) to write, expected 1 (the refused entry must not be among them).");
        if (File.Exists(vmEscapeTarget)) failures.Add($"3: '{vmEscapeTarget}' exists outside the game root.");

        // A package refused outright (content sniffing) must be reported as a refusal, not as an
        // empty-but-successful preview. This variant is reachable from the page: the user really can
        // pick a save-data archive by accident.
        var saveLikeArchive = PatchTestFixtures.CreateSaveLikePackage(vmScratch);
        var saveLikeSelected = await probe.CallAsync("SelectPatchArchiveAsync", saveLikeArchive, CancellationToken.None).ConfigureAwait(false);
        var saveLikeRejection = probe.Get("PackageRejectionMessage") as string;
        var saveLikePreview = probe.Get<OverwritePreview>("Preview");
        details.Add($"  save-like package  : returned={saveLikeSelected}, rejection=\"{saveLikeRejection}\", preview={(saveLikePreview is null ? "null" : "present")}");

        if (saveLikeSelected is true)
        {
            failures.Add("3: the ViewModel reported success for a save-data package.");
        }

        if (string.IsNullOrWhiteSpace(saveLikeRejection))
        {
            failures.Add("3: the ViewModel surfaced no refusal message for a save-data package.");
        }

        if (saveLikePreview is not null)
        {
            failures.Add("3: the ViewModel kept a preview for a package the engine refused.");
        }

        if (probe.Count("RejectedEntries") != 0 || probe.Count("CreateEntries") != 0 || probe.Count("OverwriteEntries") != 0)
        {
            failures.Add("3: the ViewModel still shows files to write for a refused package.");
        }

        return Finish(failures, details, expected);
    }

    /// <summary>Builds the verdict and appends the failure list.</summary>
    private CheckResult Finish(List<string> failures, List<string> details, string expected)
    {
        var actual = failures.Count == 0
            ? "every refused entry/package was reported with a reason and nothing was written outside the game root"
            : $"{failures.Count} assertion(s) failed";

        var result = failures.Count == 0
            ? CheckResult.Pass(Id, Title, expected, actual)
            : CheckResult.Fail(Id, Title, expected, actual);

        foreach (var failure in failures) details.Add($"FAIL REASON: {failure}");
        return result.With(details.ToArray());
    }
}
