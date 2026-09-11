using Galbox.Data.Entities;

namespace Galbox.Acceptance.Checks;

/// <summary>
/// A17 - A library entry whose folder is gone is detected, labelled and refused a launch (W14).
///
/// The defect: the only game in the user's library pointed at a folder that had already been
/// deleted, and the UI showed it as healthy. Pressing launch produced a generic error (or nothing at
/// all), with no indication of what was actually wrong or how to repair it.
///
/// The check exercises the shared decision function used by the library card badge, the detail page
/// notice and both launch commands, on four real states: folder gone, executable gone inside an
/// existing folder, a completely healthy game, and a record with no path at all. It then verifies at
/// source level that the badge, the detail-page notice and the two launch guards really use it.
/// </summary>
public sealed class A17MissingFolderCheck : IAcceptanceCheck
{
    /// <inheritdoc />
    public string Id => "A17";

    /// <inheritdoc />
    public string Title => "A missing game folder is detected, surfaced in the UI and blocks the launch with a reason";

    /// <inheritdoc />
    public Task<CheckResult> RunAsync(AcceptanceContext context, CancellationToken cancellationToken)
    {
        var expected = "a game whose folder or executable is missing reports IsMissing=true with a specific status text, "
                     + "a readable explanation naming the path and a repair hint, a healthy game reports IsMissing=false, "
                     + "the launch guard returns a message only for the broken cases, and both pages render the marker";

        var details = new List<string>();
        var work = AcceptanceWork.Create("a17");

        var statusType = ReflectionBridge.FindType("Galbox.App.Services.GameInstallationStatus");
        details.Add("=== GameInstallationStatus ===");
        details.Add($"  resolved           : {(statusType is null ? "NOT FOUND" : statusType.FullName)}");

        if (statusType is null)
        {
            details.Add("  FAIL REASON: nothing in the product can tell a live install from a deleted one, so a dead");
            details.Add("               library entry keeps looking healthy (W14).");
            return Task.FromResult(CheckResult.Fail(Id, Title, expected, "GameInstallationStatus not found")
                .With(details.ToArray()));
        }

        // ---------------------------------------------------------------------------- probes
        var goneFolder = Path.Combine(work, "game-folder-that-was-deleted");
        var goneGame = new GameInfo
        {
            Id = 9001,
            NameOriginal = "A17 gone folder",
            InstallPath = goneFolder,
            MainExecutable = Path.Combine(goneFolder, "probe.exe")
        };

        var presentFolder = Path.Combine(work, "PresentGame");
        var presentExe = AcceptanceWork.WritePattern(Path.Combine(presentFolder, "probe.exe"), 512, 0x4D);
        var healthyGame = new GameInfo
        {
            Id = 9002,
            NameOriginal = "A17 healthy game",
            InstallPath = presentFolder,
            MainExecutable = presentExe
        };

        var exeMissingGame = new GameInfo
        {
            Id = 9003,
            NameOriginal = "A17 missing exe",
            InstallPath = presentFolder,
            MainExecutable = Path.Combine(presentFolder, "deleted.exe")
        };

        var noPathGame = new GameInfo { Id = 9004, NameOriginal = "A17 no path", InstallPath = string.Empty };

        var report = new List<string>();
        var goneIsMissing = false;
        var healthyIsMissing = true;
        var exeMissingIsMissing = false;
        var noPathIsMissing = false;
        var goneMessage = string.Empty;
        var healthyMessage = string.Empty;

        foreach (var (label, probe) in new (string, GameInfo)[]
                 {
                     ("folder deleted", goneGame),
                     ("healthy", healthyGame),
                     ("executable deleted", exeMissingGame),
                     ("no install path", noPathGame)
                 })
        {
            var evaluate = ReflectionBridge.CallStaticAsync(statusType, "Evaluate", probe).GetAwaiter().GetResult();
            if (!evaluate.Ok || evaluate.Result is null)
            {
                report.Add($"  [{label}] Evaluate failed: {evaluate.Error}");
                continue;
            }

            var state = evaluate.Result;
            var isMissing = ReflectionBridge.Bool(state, "IsMissing");
            var statusText = ReflectionBridge.String(state, "StatusText") ?? string.Empty;
            var detailText = ReflectionBridge.String(state, "DetailText") ?? string.Empty;
            var repairHint = ReflectionBridge.String(state, "RepairHint") ?? string.Empty;
            var folderExists = ReflectionBridge.Bool(state, "FolderExists");
            var executableExists = ReflectionBridge.Bool(state, "ExecutableExists");

            report.Add($"  [{label}] IsMissing={isMissing}, StatusText=\"{statusText}\"");
            report.Add($"      FolderExists={folderExists}, ExecutableExists={executableExists}");
            report.Add($"      DetailText  : {detailText}");
            report.Add($"      RepairHint  : {repairHint}");

            switch (label)
            {
                case "folder deleted":
                    goneIsMissing = isMissing && statusText.Length > 0
                                 && detailText.Contains(goneFolder, StringComparison.OrdinalIgnoreCase)
                                 && repairHint.Length > 0;
                    break;
                case "healthy":
                    healthyIsMissing = isMissing;
                    break;
                case "executable deleted":
                    exeMissingIsMissing = isMissing && statusText.Length > 0
                                       && detailText.Contains("deleted.exe", StringComparison.OrdinalIgnoreCase);
                    break;
                case "no install path":
                    noPathIsMissing = isMissing;
                    break;
            }
        }

        // ---------------------------------------------------------------- launch guard messages
        var guardBlocked = ReflectionBridge.CallStaticAsync(statusType, "BuildLaunchBlockMessage", goneGame).GetAwaiter().GetResult();
        var guardAllowed = ReflectionBridge.CallStaticAsync(statusType, "BuildLaunchBlockMessage", healthyGame).GetAwaiter().GetResult();

        goneMessage = guardBlocked.Result as string ?? string.Empty;
        healthyMessage = guardAllowed.Result as string ?? string.Empty;

        details.Add(string.Empty);
        details.Add("--- per-state evaluation ---");
        details.AddRange(report);

        details.Add(string.Empty);
        details.Add("--- launch guard (BuildLaunchBlockMessage) ---");
        details.Add($"  folder deleted -> {(string.IsNullOrEmpty(goneMessage) ? "(null - launch would proceed!)" : goneMessage.Replace("\n", " / "))}");
        details.Add($"  healthy        -> {(healthyMessage.Length == 0 ? "(null - launch proceeds, correct)" : healthyMessage)}");

        var guardCorrect = goneMessage.Contains("无法启动", StringComparison.Ordinal)
                        && goneMessage.Contains(goneFolder, StringComparison.OrdinalIgnoreCase)
                        && string.IsNullOrEmpty(healthyMessage);

        // ------------------------------------------------------------------ UI wiring (sources)
        var uiEvidence = new List<string>();
        var uiOk = true;
        var repoRoot = RepoLocator.FindRepoRoot();

        if (repoRoot is not null)
        {
            var libraryXaml = Path.Combine(repoRoot, "src", "Galbox.App", "Views", "LibraryPage.xaml");
            var detailXaml = Path.Combine(repoRoot, "src", "Galbox.App", "Views", "GameDetailPage.xaml");
            var libraryVm = Path.Combine(repoRoot, "src", "Galbox.App", "ViewModels", "LibraryViewModel.cs");
            var detailVm = Path.Combine(repoRoot, "src", "Galbox.App", "ViewModels", "GameDetailViewModel.cs");

            uiOk &= RequireText(libraryXaml, "MissingFolderBadge", "LibraryPage.xaml: badge on the grid card", uiEvidence);
            uiOk &= RequireText(libraryXaml, "MissingInstallationToVisibilityConverter", "LibraryPage.xaml: badge visibility", uiEvidence);
            uiOk &= RequireText(libraryXaml, "MissingInstallationBadgeConverter", "LibraryPage.xaml: badge text", uiEvidence);
            uiOk &= RequireText(libraryXaml, "MissingFolderPill", "LibraryPage.xaml: badge in the table view", uiEvidence);
            uiOk &= RequireText(detailXaml, "IsInstallationMissing", "GameDetailPage.xaml: notice on the detail page", uiEvidence);
            uiOk &= RequireText(detailXaml, "InstallationNotice", "GameDetailPage.xaml: notice text", uiEvidence);
            uiOk &= RequireText(libraryVm, "GameInstallationStatus.BuildLaunchBlockMessage", "LibraryViewModel: launch guard", uiEvidence);
            uiOk &= RequireText(detailVm, "GameInstallationStatus.BuildLaunchBlockMessage", "GameDetailViewModel: launch guard", uiEvidence);
        }
        else
        {
            uiOk = false;
            uiEvidence.Add("  repository root not found: UI wiring could not be inspected");
        }

        details.Add(string.Empty);
        details.Add("=== UI wiring (source level) ===");
        details.AddRange(uiEvidence);

        details.Add(string.Empty);
        details.Add("--- interpretation ---");
        details.Add($"  deleted folder detected            : {goneIsMissing}");
        details.Add($"  deleted executable detected        : {exeMissingIsMissing}");
        details.Add($"  record without a path detected     : {noPathIsMissing}");
        details.Add($"  healthy game is NOT flagged        : {!healthyIsMissing}");
        details.Add($"  launch guard message is specific   : {guardCorrect}");
        details.Add($"  UI renders the marker on both pages: {uiOk}");

        var pass = goneIsMissing && exeMissingIsMissing && noPathIsMissing && !healthyIsMissing && guardCorrect && uiOk;

        var actual = $"folderGone={goneIsMissing}, exeGone={exeMissingIsMissing}, noPath={noPathIsMissing}, "
                   + $"healthyFlagged={healthyIsMissing}, guardMessage={guardCorrect}, uiWiring={uiOk}";

        return Task.FromResult(pass
            ? CheckResult.Pass(Id, Title, expected, actual).With(details.ToArray())
            : CheckResult.Fail(Id, Title, expected, actual).With(details.ToArray()));
    }

    private static bool RequireText(string filePath, string needle, string description, List<string> evidence)
    {
        if (!File.Exists(filePath))
        {
            evidence.Add($"  [FAIL] {description}: file not found ({filePath})");
            return false;
        }

        var found = File.ReadAllText(filePath).Contains(needle, StringComparison.Ordinal);
        evidence.Add($"  [{(found ? "OK  " : "FAIL")}] {description}: \"{needle}\"");
        return found;
    }
}
