using System.Text.Json;
using Galbox.App.Saves;
using Galbox.App.ViewModels;
using Galbox.Data.Entities;
using Galbox.Services.Saves;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Galbox.Acceptance.Checks;

/// <summary>
/// A10 - The save-node scan: the product's headline feature, measured end to end.
///
/// This is the check the feature was built against. It drives the real
/// <see cref="ISaveNodeScanService"/> exactly the way the application does - resolved from the
/// app-shaped container, not constructed by hand - against the real 12 saves of the reference
/// game, and it asserts the ground-truth numbers of
/// <c>_product/design/save-node-integration-plan.md</c> §4:
///
///   A10.1 the service is reachable: <see cref="ISaveNodeScanService"/> resolves from the container.
///         This is the wiring assertion. Until <c>src/Galbox.App/App.xaml.cs</c> registers it (and
///         this harness mirrors that registration) the resolution returns null and A10 FAILS - which
///         is the point: an engine that no page can reach is not a delivered feature.
///   A10.2 one scan produces exactly 12 nodes (NOT 24: Ren'Py writes every save to two directories
///         and the mirror pair must collapse into one node).
///   A10.3 every node carries a non-empty <see cref="SaveNode.SceneLabel"/>, and the label is a real
///         label of the game's script.
///   A10.4 the save stored under the slot key <c>auto-3</c> is named 孤独感 - the exact value the
///         plan demands, and the one an implementation that reads the (empty) slot name fails.
///   A10.5 CG progress is 6/27 and the unlocked-id set is exactly {0101,0301,0401,0501,0801,2401}.
///   A10.6 the scan is idempotent: a second scan of an unchanged directory adds NOTHING. The node
///         count must still be 12 and no new row id may appear.
///   A10.7 a failure is visible AND clean: scanning a directory that does not exist returns a report
///         with a concrete reason instead of throwing, and leaves the node table exactly as it was
///         (no dirty rows, no deletions).
///
/// Every expectation is a number or a set, printed with the value that was actually measured, so a
/// failure can be diagnosed from the report alone.
/// </summary>
public sealed class A10SaveNodeScanCheck : IAcceptanceCheck
{
    /// <summary>Ground truth from the design report §4.</summary>
    private const int ExpectedNodeCount = 12;

    /// <summary>Ground truth CG ids for the reference game (§4).</summary>
    private static readonly string[] ExpectedCgIds = { "0101", "0301", "0401", "0501", "0801", "2401" };

    /// <summary>Ground truth CG totals for the reference game (§4).</summary>
    private const int ExpectedCgUnlocked = 6;
    private const int ExpectedCgTotal = 27;

    /// <summary>The scene label the <c>auto-3</c> save must resolve to (§4).</summary>
    private const string ExpectedAuto3Scene = "孤独感";

    /// <summary>A path that must not exist, used for the "failure is visible" assertion.</summary>
    private const string MissingGameFolder = @"D:\GAME\__galbox_a10_missing_game__";

    /// <inheritdoc />
    public string Id => "A10";

    /// <inheritdoc />
    public string Title => "Save-node scan produces the real timeline data (12 nodes, labels, CG 6/27)";

    /// <inheritdoc />
    public async Task<CheckResult> RunAsync(AcceptanceContext context, CancellationToken cancellationToken)
    {
        var expected =
            $"ISaveNodeScanService resolves from the app-shaped container, one scan of "
            + $"'{context.Options.GameFolder}' yields exactly {ExpectedNodeCount} nodes with non-empty "
            + $"SceneLabel, the auto-3 node is '{ExpectedAuto3Scene}', CG is {ExpectedCgUnlocked}/{ExpectedCgTotal} "
            + $"with ids {{{string.Join(",", ExpectedCgIds)}}}, a second scan inserts nothing, and a scan of a "
            + "missing directory fails with a concrete reason without writing any row";

        var details = new List<string>();
        var failures = new List<string>();

        var game = context.PersistedGame;
        if (game is null)
        {
            details.Add("FAIL REASON: A0 did not persist a test game, so A10 has no library row to scan for.");
            return CheckResult.Fail(Id, Title, expected, "no persisted GameInfo available")
                .With(details.ToArray());
        }

        details.Add($"GameInfo.Id          : {game.Id}");
        details.Add($"GameInfo.NameOriginal: {game.NameOriginal}");
        details.Add($"GameInfo.InstallPath : {game.InstallPath}");
        details.Add($"GameInfo.EngineType  : {game.EngineType}");
        details.Add($"Acceptance DB        : {AcceptanceContainer.DatabasePath}");
        details.Add($"Missing-path probe   : {MissingGameFolder} (exists: {Directory.Exists(MissingGameFolder)})");

        if (Directory.Exists(MissingGameFolder))
        {
            // The probe path must not exist, otherwise "scan a missing directory" would be a lie.
            details.Add("FAIL REASON: the probe path already exists on this machine; A10.7 would not "
                      + "measure a missing directory.");
            return CheckResult.Fail(Id, Title, expected, "probe path unexpectedly exists")
                .With(details.ToArray());
        }

        // ------------------------------------------------------------------ A10.1 the wiring itself
        details.Add(string.Empty);
        details.Add("--- A10.1 ISaveNodeScanService is registered and resolvable ---");

        ISaveNodeScanService? scan = null;

        // The scope lives for the whole check, not just for the resolution: the service owns the
        // DbContext it was constructed with, so disposing the scope before scanning fails with
        // "Cannot access a disposed context instance" (observed here on the first run of this check).
        using var scanScope = context.Services.CreateAsyncScope();

        try
        {
            scan = scanScope.ServiceProvider.GetService<ISaveNodeScanService>();

            details.Add($"  resolved type      : {scan?.GetType().FullName ?? "(null)"}");
        }
        catch (Exception ex)
        {
            failures.Add($"A10.1: resolving ISaveNodeScanService threw {ex.GetType().Name}: {ex.Message}");
            details.Add($"  [FAIL] resolution threw: {ex.GetType().Name}: {ex.Message}");
            details.Add($"FAIL REASON: the DI registration itself is broken: {ex.Message}");
            return CheckResult.Fail(Id, Title, expected, $"resolution threw {ex.GetType().Name}")
                .With(details.ToArray());
        }

        if (scan is null)
        {
            failures.Add("A10.1: ISaveNodeScanService did not resolve - the scan engine is not wired into the container.");
            details.Add("  [FAIL] the container returned null for ISaveNodeScanService.");
            details.Add("FAIL REASON: the save-node scan (Galbox.Services.Saves.SaveNodeScanService) exists and is "
                      + "verified, but nothing registers it (src/Galbox.App/App.xaml.cs) and nothing calls it, so "
                      + "the feature has no door. Every later assertion in A10 is '(not measured)'.");
            return CheckResult.Fail(
                    Id, Title, expected,
                    "ISaveNodeScanService resolved to null (not registered in the app-shaped container)")
                .With(details.ToArray());
        }

        details.Add("  [OK]   ISaveNodeScanService resolved.");

        // ------------------------------------------------------------- A10.2 - A10.5 the first scan
        details.Add(string.Empty);
        details.Add("--- A10.2 first scan ---");

        var firstReport = await scan.ScanAsync(game.Id, game.InstallPath, cancellationToken).ConfigureAwait(false);

        details.Add("Scan report (raw):");
        foreach (var line in firstReport.Describe().Split('\n'))
        {
            details.Add($"    {line.TrimEnd('\r')}");
        }

        if (!firstReport.Succeeded)
        {
            failures.Add($"A10.2: the scan failed ({firstReport.FailureKind}): {firstReport.FailureReason}");
            details.Add($"FAIL REASON: scan #1 did not succeed: {firstReport.FailureKind} - {firstReport.FailureReason}");
            return CheckResult.Fail(Id, Title, expected, $"scan #1 failed: {firstReport.FailureKind}")
                .With(details.ToArray());
        }

        details.Add($"  report.SlotCount          : {firstReport.SlotCount}");
        details.Add($"  report.NodeCountAfterScan : {firstReport.NodeCountAfterScan}");
        details.Add($"  report added/updated/skipped/failed : {firstReport.AddedCount}/{firstReport.UpdatedCount}/"
                  + $"{firstReport.SkippedCount}/{firstReport.FailedCount}");
        details.Add($"  report.CgUnlockedIds      : {{{string.Join(",", firstReport.CgUnlockedIds)}}}");
        details.Add($"  report scene progress     : {firstReport.UnlockedSceneCount}/{firstReport.TotalSceneCount}");
        details.Add($"  report clusters / groups  : {firstReport.SceneClusterCount} / {firstReport.SuspectedRouteGroupCount}");

        var nodesAfterFirstScan = await ReadNodesAsync(context, game.Id, cancellationToken).ConfigureAwait(false);
        var countAfterFirstScan = nodesAfterFirstScan.Count;

        details.Add(string.Empty);
        details.Add($"--- A10.2 node count after scan #1: {countAfterFirstScan} (expected {ExpectedNodeCount}) ---");
        details.Add("  SlotName                SceneLabel                       RouteName     Status   SaveModifiedTime (UTC)");
        foreach (var node in nodesAfterFirstScan)
        {
            details.Add($"  {Pad(node.SlotName),-23} {Pad(node.SceneLabel),-32} {Pad(node.RouteName),-13} "
                      + $"{node.ParseStatus,-8} {node.SaveModifiedTime:yyyy-MM-dd HH:mm:ss}");
        }

        if (countAfterFirstScan != ExpectedNodeCount)
        {
            failures.Add($"A10.2: expected {ExpectedNodeCount} nodes after the first scan, measured {countAfterFirstScan}.");
        }

        // ------------------------------------------------------------------ A10.3 scene labels
        details.Add(string.Empty);
        details.Add("--- A10.3 every node carries a non-empty SceneLabel ---");

        var withoutScene = nodesAfterFirstScan
            .Where(node => string.IsNullOrWhiteSpace(node.SceneLabel))
            .ToList();

        details.Add($"  nodes with a scene label : {nodesAfterFirstScan.Count - withoutScene.Count}/{nodesAfterFirstScan.Count}");
        details.Add($"  nodes WITHOUT a label    : {withoutScene.Count}"
                  + (withoutScene.Count == 0 ? string.Empty : $" -> {string.Join(", ", withoutScene.Select(n => n.SlotName))}"));

        if (withoutScene.Count > 0)
        {
            failures.Add($"A10.3: {withoutScene.Count} node(s) have an empty SceneLabel: "
                       + $"{string.Join(", ", withoutScene.Select(n => n.SlotName ?? "(null)"))}.");
        }

        // ------------------------------------------------------ A10.4 auto-3 resolves to 孤独感
        details.Add(string.Empty);
        details.Add("--- A10.4 the auto-3 save is named 孤独感 ---");

        var auto3 = nodesAfterFirstScan.FirstOrDefault(node => IsAuto3Slot(node));
        details.Add($"  auto-3 node found        : {auto3 is not null}");
        details.Add($"  auto-3 SlotName          : {auto3?.SlotName ?? "(none)"}");
        details.Add($"  auto-3 SceneLabel        : {auto3?.SceneLabel ?? "(none)"}");
        details.Add($"  auto-3 DisplayName       : {auto3?.DisplayName ?? "(none)"}");

        var auto3SlotIsEmptyString = auto3 is not null && string.IsNullOrEmpty(auto3.SlotName);
        details.Add($"  auto-3 stored slot name is an empty string (the trap): {auto3SlotIsEmptyString}");

        // The trap, stated exactly: the reference game writes NOTHING into json._save_name, so the
        // player-visible slot name is empty on every save and the label can only come from the scene
        // label. The stored SlotName is the file-derived key the mapper fell back to.
        var playerNames = nodesAfterFirstScan
            .Select(node => SaveNodeExtendedMetadata.FromJson(node.ExtendedMetadataJson)?.PlayerSaveName)
            .ToList();

        var emptyPlayerNames = playerNames.Count(name => string.IsNullOrWhiteSpace(name));

        details.Add($"  json._save_name on the 12 saves      : {emptyPlayerNames}/12 are empty "
                  + $"(the game never names a save, so the slot name cannot be the label)");
        details.Add($"  SlotName column actually holds       : {string.Join(", ", nodesAfterFirstScan.Select(n => n.SlotName).Distinct())}");

        // The requirement in one line: there is no player-visible save name anywhere, and the label
        // must STILL be non-blank - which is exactly what DisplayName's fallback chain is for.
        var blankDisplayNames = nodesAfterFirstScan
            .Where(node => string.IsNullOrWhiteSpace(node.DisplayName))
            .ToList();

        details.Add($"  nodes whose DisplayName is blank     : {blankDisplayNames.Count} (must be 0)");

        if (emptyPlayerNames > 0 && blankDisplayNames.Count > 0)
        {
            failures.Add($"A10.4: {blankDisplayNames.Count} node(s) have a blank DisplayName while the game stores no "
                       + "player save name - the timeline would render empty boxes.");
        }

        if (auto3 is null)
        {
            failures.Add("A10.4: no node for the slot key 'auto-3' (auto-3-LT1) was stored.");
        }
        else
        {
            if (!auto3.DisplayName.Contains(ExpectedAuto3Scene, StringComparison.Ordinal))
            {
                failures.Add($"A10.4: the auto-3 node's DisplayName is '{auto3.DisplayName}', which does not contain "
                           + $"'{ExpectedAuto3Scene}'.");
            }

            if (string.IsNullOrWhiteSpace(auto3.DisplayName))
            {
                failures.Add("A10.4: the auto-3 node's DisplayName is blank - the UI would render an empty timeline node.");
            }
        }

        // ------------------------------------------------------------------------- A10.5 CG progress
        details.Add(string.Empty);
        details.Add("--- A10.5 CG progress is game-level and exactly {0101,0301,0401,0501,0801,2401} ---");

        var cgUnlockedIds = new SortedSet<string>(StringComparer.Ordinal);
        var cgTotalCounts = new SortedSet<int>();
        var cgUnlockedCounts = new SortedSet<int>();
        var cgJsonMissing = new List<string>();

        foreach (var node in nodesAfterFirstScan)
        {
            cgTotalCounts.Add(node.CgTotalCount);
            cgUnlockedCounts.Add(node.CgUnlockedCount);

            if (string.IsNullOrWhiteSpace(node.CgUnlockedIdsJson))
            {
                cgJsonMissing.Add(node.SlotName ?? "(null)");
                continue;
            }

            foreach (var id in JsonSerializer.Deserialize<string[]>(node.CgUnlockedIdsJson!) ?? Array.Empty<string>())
            {
                cgUnlockedIds.Add(id);
            }
        }

        var expectedCgIdSet = new SortedSet<string>(ExpectedCgIds, StringComparer.Ordinal);

        details.Add($"  CgUnlockedCount values on the 12 nodes : {{{string.Join(",", cgUnlockedCounts)}}} (expected {{{ExpectedCgUnlocked}}})");
        details.Add($"  CgTotalCount values on the 12 nodes    : {{{string.Join(",", cgTotalCounts)}}} (expected {{{ExpectedCgTotal}}})");
        details.Add($"  union of CgUnlockedIdsJson             : {{{string.Join(",", cgUnlockedIds)}}}");
        details.Add($"  expected id set                        : {{{string.Join(",", expectedCgIdSet)}}}");
        details.Add($"  set equality                           : {cgUnlockedIds.SetEquals(expectedCgIdSet)}");
        details.Add($"  nodes with no CgUnlockedIdsJson        : {cgJsonMissing.Count}");
        details.Add($"  report.CgUnlockedCount/CgTotalCount    : {firstReport.CgUnlockedCount}/{firstReport.CgTotalCount}");

        if (!cgUnlockedIds.SetEquals(expectedCgIdSet))
        {
            failures.Add($"A10.5: the CG id set is {{{string.Join(",", cgUnlockedIds)}}}, expected "
                       + $"{{{string.Join(",", expectedCgIdSet)}}}.");
        }

        if (!cgUnlockedCounts.SetEquals(new[] { ExpectedCgUnlocked }))
        {
            failures.Add($"A10.5: CgUnlockedCount is {{{string.Join(",", cgUnlockedCounts)}}}, expected {ExpectedCgUnlocked}.");
        }

        if (!cgTotalCounts.SetEquals(new[] { ExpectedCgTotal }))
        {
            failures.Add($"A10.5: CgTotalCount is {{{string.Join(",", cgTotalCounts)}}}, expected {ExpectedCgTotal}.");
        }

        // ------------------------------------------------------------------------- A10.6 idempotency
        details.Add(string.Empty);
        details.Add("--- A10.6 idempotency: a second scan must add nothing ---");

        var secondReport = await scan.ScanAsync(game.Id, game.InstallPath, cancellationToken).ConfigureAwait(false);

        foreach (var line in secondReport.Describe().Split('\n'))
        {
            details.Add($"    {line.TrimEnd('\r')}");
        }

        var nodesAfterSecondScan = await ReadNodesAsync(context, game.Id, cancellationToken).ConfigureAwait(false);
        var idsBefore = nodesAfterFirstScan.Select(n => n.Id).OrderBy(id => id).ToList();
        var idsAfter = nodesAfterSecondScan.Select(n => n.Id).OrderBy(id => id).ToList();
        var sameIds = idsBefore.SequenceEqual(idsAfter);

        details.Add($"  node count after scan #1 : {countAfterFirstScan}");
        details.Add($"  node count after scan #2 : {nodesAfterSecondScan.Count} (expected {ExpectedNodeCount})");
        details.Add($"  report#2 added/updated/skipped : {secondReport.AddedCount}/{secondReport.UpdatedCount}/{secondReport.SkippedCount}");
        details.Add($"  row ids identical        : {sameIds}  before=[{string.Join(",", idsBefore)}] after=[{string.Join(",", idsAfter)}]");

        if (nodesAfterSecondScan.Count != ExpectedNodeCount)
        {
            failures.Add($"A10.6: after a second scan the node count is {nodesAfterSecondScan.Count}, "
                       + $"expected {ExpectedNodeCount} (a duplicate insert would show 24).");
        }

        if (secondReport.AddedCount != 0)
        {
            failures.Add($"A10.6: the second scan added {secondReport.AddedCount} row(s); an unchanged save directory "
                       + "must be a no-op.");
        }

        if (!sameIds)
        {
            failures.Add("A10.6: the set of stored node ids changed between two scans of an unchanged directory.");
        }

        // ------------------------------------------------------- A10.7 failure is visible and clean
        details.Add(string.Empty);
        details.Add("--- A10.7 a missing directory fails with a reason and leaves no dirty data ---");

        var countBeforeFailedScan = (await ReadNodesAsync(context, game.Id, cancellationToken).ConfigureAwait(false)).Count;

        SaveNodeScanReport? failedReport = null;
        Exception? failureThrown = null;

        try
        {
            failedReport = await scan
                .ScanAsync(game.Id, MissingGameFolder, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            failureThrown = ex;
        }

        var countAfterFailedScan = (await ReadNodesAsync(context, game.Id, cancellationToken).ConfigureAwait(false)).Count;

        details.Add($"  report.Succeeded         : {failedReport?.Succeeded.ToString() ?? "(none: threw)"}");
        details.Add($"  report.FailureKind       : {failedReport?.FailureKind.ToString() ?? "(none: threw)"}");
        details.Add($"  report.FailureReason     : {failedReport?.FailureReason ?? "(null)"}");
        details.Add($"  report.SlotCount         : {failedReport?.SlotCount.ToString() ?? "(none: threw)"}");
        details.Add($"  report.Elapsed           : {failedReport?.Elapsed.TotalMilliseconds.ToString("F0") ?? "(none: threw)"} ms");
        details.Add($"  node count before/after  : {countBeforeFailedScan}/{countAfterFailedScan} (must be equal)");

        if (failureThrown is not null)
        {
            details.Add($"  [FAIL] the scan threw {failureThrown.GetType().Name}: {failureThrown.Message}");
            failures.Add($"A10.7: scanning a missing directory threw {failureThrown.GetType().Name} "
                       + $"instead of returning a report: {failureThrown.Message}");
        }
        else if (failedReport is not null)
        {
            if (failedReport.Succeeded)
            {
                failures.Add("A10.7: scanning a directory that does not exist reported Success=true.");
            }

            if (failedReport.FailureKind == SaveNodeScanFailureKind.None)
            {
                failures.Add("A10.7: a failed scan left FailureKind at None, so the UI could not tell the user why.");
            }

            if (string.IsNullOrWhiteSpace(failedReport.FailureReason))
            {
                failures.Add("A10.7: a failed scan returned an empty FailureReason "
                           + "(the UI would have to show '扫描失败' with no explanation).");
            }
            else
            {
                details.Add("  reason is user-facing text and non-empty: true");
            }
        }

        if (countAfterFailedScan != countBeforeFailedScan)
        {
            failures.Add($"A10.7: the failed scan changed the node table from {countBeforeFailedScan} to "
                       + $"{countAfterFailedScan} row(s).");
        }

        // The same must hold for a game id that does not exist at all.
        details.Add(string.Empty);
        details.Add("--- A10.7b a game id that is not in the library is refused too ---");

        var unknownGameReport = await scan
            .ScanAsync(999_999, game.InstallPath, cancellationToken)
            .ConfigureAwait(false);

        details.Add($"  Succeeded     : {unknownGameReport.Succeeded}");
        details.Add($"  FailureKind   : {unknownGameReport.FailureKind}");
        details.Add($"  FailureReason : {unknownGameReport.FailureReason ?? "(null)"}");

        if (unknownGameReport.Succeeded || unknownGameReport.FailureKind != SaveNodeScanFailureKind.GameNotFound)
        {
            failures.Add($"A10.7b: scanning for unknown game id 999999 returned "
                       + $"Succeeded={unknownGameReport.Succeeded}, FailureKind={unknownGameReport.FailureKind}; "
                       + "expected GameNotFound.");
        }

        // A directory that exists but holds no save at all must be a clean "nothing here" failure
        // (the empty state the page then shows), not an exception and not a silent empty success.
        details.Add(string.Empty);
        details.Add("--- A10.7c an existing but empty game folder ---");

        var emptyFolder = Path.Combine(Path.GetTempPath(), $"galbox-a10-empty-{Guid.NewGuid():N}");
        Directory.CreateDirectory(emptyFolder);

        try
        {
            var emptyFolderReport = await scan
                .ScanAsync(game.Id, emptyFolder, cancellationToken)
                .ConfigureAwait(false);

            var countAfterEmptyScan = (await ReadNodesAsync(context, game.Id, cancellationToken).ConfigureAwait(false)).Count;

            details.Add($"  folder                   : {emptyFolder} (created, holds {Directory.GetFileSystemEntries(emptyFolder).Length} entries)");
            details.Add($"  report.Succeeded         : {emptyFolderReport.Succeeded}");
            details.Add($"  report.FailureKind       : {emptyFolderReport.FailureKind}");
            details.Add($"  report.FailureReason     : {emptyFolderReport.FailureReason ?? "(null)"}");
            details.Add($"  node count before/after  : {countBeforeFailedScan}/{countAfterEmptyScan} (must be equal)");

            if (emptyFolderReport.Succeeded)
            {
                failures.Add("A10.7c: scanning an empty folder reported Success=true.");
            }

            if (emptyFolderReport.FailureKind == SaveNodeScanFailureKind.None
                || string.IsNullOrWhiteSpace(emptyFolderReport.FailureReason))
            {
                failures.Add($"A10.7c: an empty folder produced FailureKind="
                           + $"{emptyFolderReport.FailureKind} with reason "
                           + $"'{emptyFolderReport.FailureReason ?? "(null)"}' - the page could not explain itself.");
            }

            if (countAfterEmptyScan != countBeforeFailedScan)
            {
                failures.Add("A10.7c: scanning an empty folder changed the node table.");
            }
        }
        finally
        {
            try
            {
                Directory.Delete(emptyFolder, recursive: true);
            }
            catch (Exception ex)
            {
                details.Add($"  cleanup warning: could not remove '{emptyFolder}': {ex.Message}");
            }
        }

        // ============================================================= A10.8 the presentation layer
        // Everything above measured what the database holds. This section measures what the user
        // would actually READ, by driving the real SaveManagerViewModel the page is bound to - the
        // same object the "扫描存档" button and the timeline ItemsControl use.
        details.Add(string.Empty);
        details.Add("--- A10.8 the timeline the page renders (real SaveManagerViewModel) ---");

        var pageModel = SaveNodeTimelineBuilder.Build(nodesAfterSecondScan);

        details.Add($"  builder entries          : {pageModel.Entries.Count}");
        details.Add($"  builder status           : {pageModel.StatusText}");
        details.Add($"  builder hint             : {pageModel.HintText ?? "(none)"}");
        details.Add($"  builder cg               : {pageModel.Cg.HeadlineText} ({pageModel.Cg.RemainingCount} remaining)");
        details.Add($"  builder scene progress   : {pageModel.Scene.HeadlineText}");
        details.Add($"  builder route groups     : {pageModel.RouteGroups.Count}");
        foreach (var group in pageModel.RouteGroups)
        {
            details.Add($"      {group}");
        }

        // --- blank-label assertions: the trap this game sets -------------------------------
        var blankLabels = pageModel.Entries.Where(entry => string.IsNullOrWhiteSpace(entry.DisplayName)).ToList();
        var blankSceneTexts = pageModel.Entries
            .Where(entry => string.IsNullOrWhiteSpace(entry.SceneLabelText))
            .ToList();

        details.Add($"  entries with a blank DisplayName    : {blankLabels.Count} (must be 0)");
        details.Add($"  entries with a blank SceneLabelText : {blankSceneTexts.Count} (must be 0)");

        if (blankLabels.Count > 0)
        {
            failures.Add($"A10.8: {blankLabels.Count} timeline entries render a blank name - the page would show "
                       + "empty boxes (the reference game stores 12/12 slot names as \"\").");
        }

        if (blankSceneTexts.Count > 0)
        {
            failures.Add($"A10.8: {blankSceneTexts.Count} timeline entries have a blank scene text.");
        }

        // --- the label of every node, as the user would see it ----------------------------
        details.Add("  --- rendered timeline (oldest first) ---");
        foreach (var entry in pageModel.Entries)
        {
            details.Add($"  [{entry.TimeText}] {entry.DisplayName} | {entry.SlotKindText} | slot={entry.SlotKeyText} "
                      + $"| route={entry.RouteText ?? "(none)"} | parse={entry.ParseStatusText}"
                      + (entry.HasParseProblem ? " !!" : string.Empty));
        }

        // --- auto vs manual separation ----------------------------------------------------
        var renderedAuto3 = pageModel.Entries.FirstOrDefault(entry => IsAuto3Entry(entry));

        details.Add($"  auto-3 entry             : {renderedAuto3?.DisplayName ?? "(none)"} "
                  + $"/ kind={renderedAuto3?.SlotKindText ?? "(none)"} / isAuto={renderedAuto3?.IsAutoSave.ToString() ?? "(none)"}");
        details.Add($"  auto slots / manual slots: {pageModel.AutoSaveCount} / {pageModel.Entries.Count - pageModel.AutoSaveCount}");

        if (renderedAuto3 is null)
        {
            failures.Add("A10.8: the timeline has no entry for the auto-3 save.");
        }
        else
        {
            if (!renderedAuto3.IsAutoSave || renderedAuto3.SlotKindText != "自动档")
            {
                failures.Add($"A10.8: the auto-3 entry is rendered as '{renderedAuto3.SlotKindText}' "
                           + $"(IsAutoSave={renderedAuto3.IsAutoSave}); the UI could not tell an autosave "
                           + "from a manual slot.");
            }

            if (!renderedAuto3.DisplayName.Contains(ExpectedAuto3Scene, StringComparison.Ordinal))
            {
                failures.Add($"A10.8: the rendered auto-3 label is '{renderedAuto3.DisplayName}'.");
            }
        }

        var manualEntries = pageModel.Entries.Where(entry => !entry.IsAutoSave).ToList();
        details.Add($"  manual slots render as   : {string.Join(", ", manualEntries.Take(4).Select(e => e.SlotKindText))}");

        if (manualEntries.Any(entry => entry.SlotKindText != "手动档"))
        {
            failures.Add("A10.8: a numbered manual slot was not recognised as 手动档 "
                       + $"(kinds: {string.Join(", ", manualEntries.Select(e => e.SlotKindText))}).");
        }

        // --- red line 1: a branch name must be shown as a guess ---------------------------
        details.Add(string.Empty);
        details.Add("--- A10.8 red line 1: 分支名只能标「疑似」 ---");

        var speculative = pageModel.Entries.Where(entry => entry.RouteIsSpeculative).ToList();
        details.Add($"  entries with a route text      : {pageModel.Entries.Count(e => e.HasRoute)}");
        details.Add($"  of which speculative (疑似)    : {speculative.Count}");
        details.Add($"  entries without a route (honest): {pageModel.Entries.Count(e => !e.HasRoute)}");

        foreach (var entry in pageModel.Entries.Where(e => e.HasRoute).Take(6))
        {
            details.Add($"      {entry.SlotKeyText,-16} -> {entry.RouteText}");
        }

        foreach (var entry in speculative)
        {
            if (!entry.RouteText!.Contains("疑似", StringComparison.Ordinal)
                || !entry.RouteText.Contains("推测", StringComparison.Ordinal))
            {
                failures.Add($"A10.8: speculative route '{entry.RouteText}' is not marked as a guess "
                           + "(red line 1: it must read 疑似…（推测，非确证）).");
            }
        }

        // --- red line 2: progress is scenes, not script lines ------------------------------
        details.Add(string.Empty);
        details.Add("--- A10.8 red line 2: 进度用「已解锁场景/总场景」，不是剧本行号 ---");
        details.Add($"  scene progress           : {pageModel.Scene.HeadlineText}");
        details.Add($"  method note              : {SaveSceneProgressView.MethodNote}");

        if (!pageModel.Scene.IsKnown)
        {
            failures.Add("A10.8: the story progress could not be computed (no denominator), so the page would "
                       + "show '未知' - that is honest, but the reference game does have a label table.");
        }

        // --- red line 3: parse failures are visible ----------------------------------------
        details.Add(string.Empty);
        details.Add("--- A10.8 red line 3: 解析失败/部分解析必须在界面上可见 ---");

        var problemEntries = pageModel.Entries.Where(entry => entry.HasParseProblem).ToList();
        details.Add($"  entries with a parse problem : {problemEntries.Count} of {pageModel.Entries.Count}");

        foreach (var entry in problemEntries)
        {
            details.Add($"      {entry.SlotKeyText}: {entry.ParseStatusText} -> {entry.ParseProblemText}");
        }

        if (problemEntries.Any(entry => string.IsNullOrWhiteSpace(entry.ParseProblemText)))
        {
            failures.Add("A10.8: an entry reports a parse problem but renders no explanation.");
        }

        if (pageModel.ParseProblemCount != problemEntries.Count)
        {
            failures.Add($"A10.8: the model counts {pageModel.ParseProblemCount} parse problems but "
                       + $"{problemEntries.Count} entries carry one.");
        }

        // --- CG figure and the "what is still missing" list --------------------------------
        details.Add(string.Empty);
        details.Add("--- A10.8 CG 图鉴数字与「还差哪几张」 ---");
        details.Add($"  rendered headline        : {pageModel.Cg.HeadlineText}");
        details.Add($"  rendered remaining       : {pageModel.Cg.RemainingCount}");
        details.Add($"  unlocked ids             : {{{string.Join(",", pageModel.Cg.UnlockedIds)}}}");
        details.Add($"  locked ids ({pageModel.Cg.LockedIds.Count})    : {{{string.Join(",", pageModel.Cg.LockedIds)}}}");

        var expectedHeadline = $"{ExpectedCgUnlocked} / {ExpectedCgTotal} (22.2%)";

        if (pageModel.Cg.HeadlineText != expectedHeadline)
        {
            failures.Add($"A10.8: the CG headline is '{pageModel.Cg.HeadlineText}', expected '{expectedHeadline}'.");
        }

        if (pageModel.Cg.RemainingCount != ExpectedCgTotal - ExpectedCgUnlocked)
        {
            failures.Add($"A10.8: the 'still missing' count is {pageModel.Cg.RemainingCount}, "
                       + $"expected {ExpectedCgTotal - ExpectedCgUnlocked}.");
        }

        if (pageModel.Cg.LockedIds.Count == 0)
        {
            failures.Add("A10.8: the page cannot list which CG are still missing (no locked ids were stored), "
                       + "but it must be able to answer 还差哪几张.");
        }

        // --- the empty state, before anything is loaded ------------------------------------
        details.Add(string.Empty);
        details.Add("--- A10.8 空态：尚未扫描过 ---");

        var viewModel = BuildViewModel(context);
        details.Add($"  fresh VM status          : {viewModel.SaveNodeStatusText}");
        details.Add($"  fresh VM IsTimelineEmpty : {viewModel.IsTimelineEmpty}");
        details.Add($"  fresh VM HasTimeline     : {viewModel.HasTimeline}");

        if (!viewModel.IsTimelineEmpty || viewModel.HasTimeline)
        {
            failures.Add("A10.8: a fresh ViewModel claims to have a timeline before anything was loaded.");
        }

        if (!viewModel.SaveNodeStatusText.Contains("尚未扫描", StringComparison.Ordinal))
        {
            failures.Add($"A10.8: the empty state reads '{viewModel.SaveNodeStatusText}', which does not tell the "
                       + "user that nothing has been scanned yet.");
        }

        // --- the same ViewModel, loaded from the database -----------------------------------
        await viewModel.LoadSaveNodesAsync(game.Id, cancellationToken).ConfigureAwait(false);

        details.Add(string.Empty);
        details.Add("--- A10.8 the real ViewModel after LoadSaveNodesAsync ---");
        details.Add($"  TimelineItems.Count      : {viewModel.TimelineItems.Count}");
        details.Add($"  HasTimeline              : {viewModel.HasTimeline}");
        details.Add($"  IsTimelineEmpty          : {viewModel.IsTimelineEmpty}");
        details.Add($"  SaveNodeStatusText       : {viewModel.SaveNodeStatusText}");
        details.Add($"  SaveNodeHintText         : {viewModel.SaveNodeHintText ?? "(none)"}");
        details.Add($"  CgHeadlineText           : {viewModel.CgHeadlineText}");
        details.Add($"  CgRemainingText          : {viewModel.CgRemainingText}");
        details.Add($"  CgProgressValue          : {viewModel.CgProgressValue:F3}");
        details.Add($"  CgUnlockedIds / CgLockedIds : {viewModel.CgUnlockedIds.Count} / {viewModel.CgLockedIds.Count}");
        details.Add($"  SceneProgressText        : {viewModel.SceneProgressText}");
        details.Add($"  HasRouteGroups           : {viewModel.HasRouteGroups}");
        details.Add($"  RouteGroupsText          : {viewModel.RouteGroupsText.Replace(Environment.NewLine, " | ")}");
        details.Add($"  ParseProblemCount        : {viewModel.ParseProblemCount}");
        details.Add($"  HasSaveNodeError         : {viewModel.HasSaveNodeError}");
        details.Add($"  first 3 labels           : {string.Join(" / ", viewModel.TimelineItems.Take(3).Select(e => e.DisplayName))}");

        if (viewModel.TimelineItems.Count != ExpectedNodeCount)
        {
            failures.Add($"A10.8: the ViewModel timeline holds {viewModel.TimelineItems.Count} items, "
                       + $"expected {ExpectedNodeCount}.");
        }

        if (viewModel.TimelineItems.Any(item => string.IsNullOrWhiteSpace(item.DisplayName)))
        {
            failures.Add("A10.8: the ViewModel timeline contains an item with a blank label.");
        }

        if (viewModel.CgHeadlineText != expectedHeadline)
        {
            failures.Add($"A10.8: the ViewModel shows '{viewModel.CgHeadlineText}' for CG, expected '{expectedHeadline}'.");
        }

        if (viewModel.HasSaveNodeError)
        {
            failures.Add($"A10.8: loading a healthy timeline left an error banner open: {viewModel.SaveNodeErrorText}");
        }

        // --- the failed scan, as the page shows it -----------------------------------------
        details.Add(string.Empty);
        details.Add("--- A10.8 失败态：界面必须显示具体原因 ---");

        await viewModel.ScanSaveNodesAsync(game.Id, MissingGameFolder, cancellationToken).ConfigureAwait(false);

        details.Add($"  StatusText               : {viewModel.SaveNodeStatusText}");
        details.Add($"  ErrorText                : {viewModel.SaveNodeErrorText}");
        details.Add($"  HasSaveNodeError         : {viewModel.HasSaveNodeError}");
        details.Add($"  IsUnsupportedEngine      : {viewModel.SaveNodeErrorIsUnsupportedEngine}");
        details.Add($"  timeline items kept      : {viewModel.TimelineItems.Count}");

        if (!viewModel.HasSaveNodeError || string.IsNullOrWhiteSpace(viewModel.SaveNodeErrorText))
        {
            failures.Add("A10.8: a failed scan did not surface a reason on the page.");
        }
        else if (viewModel.SaveNodeErrorText!.Length <= 12)
        {
            failures.Add($"A10.8: the failure text '{viewModel.SaveNodeErrorText}' is too short to be a concrete reason.");
        }

        if (viewModel.TimelineItems.Count != ExpectedNodeCount)
        {
            failures.Add($"A10.8: a failed re-scan blanked the timeline ({viewModel.TimelineItems.Count} items left).");
        }

        // --- unsupported engine, as the page shows it --------------------------------------
        details.Add(string.Empty);
        details.Add("--- A10.8 不支持的引擎：不能显示假数据 ---");

        var nonRenpyGame = await PersistForeignEngineGameAsync(context, cancellationToken).ConfigureAwait(false);
        var unsupportedReport = await scan
            .ScanAsync(nonRenpyGame.Id, nonRenpyGame.InstallPath ?? string.Empty, cancellationToken)
            .ConfigureAwait(false);

        var unsupportedModel = SaveNodeTimelineBuilder.FromScanReport(
            unsupportedReport, Array.Empty<SaveNode>());

        var unsupportedViewModel = BuildViewModel(context);
        await unsupportedViewModel
            .ScanSaveNodesAsync(nonRenpyGame.Id, nonRenpyGame.InstallPath ?? string.Empty, cancellationToken)
            .ConfigureAwait(false);

        var foreignNodeCount = (await ReadNodesAsync(context, nonRenpyGame.Id, cancellationToken).ConfigureAwait(false)).Count;

        details.Add($"  probe game               : id={nonRenpyGame.Id} engine={nonRenpyGame.EngineType}");
        details.Add($"  report.FailureKind       : {unsupportedReport.FailureKind}");
        details.Add($"  report.FailureReason     : {unsupportedReport.FailureReason ?? "(null)"}");
        details.Add($"  model status             : {unsupportedModel.StatusText}");
        details.Add($"  model error              : {unsupportedModel.ErrorText ?? "(null)"}");
        details.Add($"  model has data           : {unsupportedModel.HasTimeline}");
        details.Add($"  VM status                : {unsupportedViewModel.SaveNodeStatusText}");
        details.Add($"  VM error                 : {unsupportedViewModel.SaveNodeErrorText}");
        details.Add($"  VM IsUnsupportedEngine   : {unsupportedViewModel.SaveNodeErrorIsUnsupportedEngine}");
        details.Add($"  VM timeline items        : {unsupportedViewModel.TimelineItems.Count}");
        details.Add($"  rows written for that game: {foreignNodeCount} (must be 0)");

        if (unsupportedReport.FailureKind != SaveNodeScanFailureKind.UnsupportedEngine)
        {
            failures.Add($"A10.8: a KiriKiri game produced {unsupportedReport.FailureKind} instead of UnsupportedEngine.");
        }

        if (unsupportedViewModel.SaveNodeErrorText is null
            || !unsupportedViewModel.SaveNodeErrorText.Contains("暂不支持", StringComparison.Ordinal))
        {
            failures.Add("A10.8: the unsupported-engine state does not say 暂不支持 X 引擎的存档解析.");
        }

        if (unsupportedViewModel.TimelineItems.Count != 0 || unsupportedViewModel.HasTimeline)
        {
            failures.Add("A10.8: an unsupported engine produced timeline data. Fabricated data is never acceptable.");
        }

        if (foreignNodeCount != 0)
        {
            failures.Add($"A10.8: an unsupported engine wrote {foreignNodeCount} node row(s).");
        }

        // --- snapshot flow (§6.2) -----------------------------------------------------------
        details.Add(string.Empty);
        details.Add("--- A10.8 快照：标记 + 描述 + 时间线上的不同样式 ---");

        var snapshotTarget = viewModel.TimelineItems.FirstOrDefault(item => IsAuto3Entry(item));
        viewModel.SelectedTimelineNode = snapshotTarget;

        details.Add($"  SelectedNodeSummary      : {viewModel.SelectedNodeSummary}");
        details.Add($"  CanMarkSnapshot (empty description) : {viewModel.CanMarkSnapshot} (must be false)");

        if (viewModel.CanMarkSnapshot)
        {
            failures.Add("A10.8: the snapshot button is enabled with an empty description.");
        }

        const string snapshotText = "A10 验收快照：第一次到孤独感";

        viewModel.SnapshotDescriptionInput = snapshotText;
        details.Add($"  CanMarkSnapshot (typed description): {viewModel.CanMarkSnapshot} (must be true)");

        if (!viewModel.CanMarkSnapshot)
        {
            failures.Add("A10.8: the snapshot button stays disabled although a node is selected and a description was typed.");
        }

        var marked = await viewModel.MarkSelectedNodeAsSnapshotAsync(null, cancellationToken).ConfigureAwait(false);
        var snapshotEntry = viewModel.TimelineItems.FirstOrDefault(item => item.NodeId == snapshotTarget?.NodeId);

        details.Add($"  MarkSelectedNodeAsSnapshotAsync : {marked}");
        details.Add($"  timeline entry IsSnapshot       : {snapshotEntry?.IsSnapshot}");
        details.Add($"  timeline entry DisplayName      : {snapshotEntry?.DisplayName}");
        details.Add($"  timeline entry SnapshotDescription : {snapshotEntry?.SnapshotDescription}");
        details.Add($"  model SnapshotCount             : {SaveNodeTimelineBuilder.Build(
            await ReadNodesAsync(context, game.Id, cancellationToken).ConfigureAwait(false)).SnapshotCount}");

        if (!marked || snapshotEntry is null || !snapshotEntry.IsSnapshot)
        {
            failures.Add("A10.8: marking a node as a snapshot did not survive the reload.");
        }
        else if (snapshotEntry.DisplayName != snapshotText)
        {
            failures.Add($"A10.8: the snapshot renders as '{snapshotEntry.DisplayName}' instead of its description "
                       + $"(expected '{snapshotText}') - the timeline must label a snapshot with what the user typed.");
        }

        // Clear it again so the stored state matches the freshly scanned one.
        viewModel.SelectedTimelineNode = snapshotEntry;
        var cleared = await viewModel.ClearSelectedSnapshotAsync(cancellationToken).ConfigureAwait(false);

        details.Add($"  ClearSelectedSnapshotAsync      : {cleared}");
        details.Add($"  entry after clearing            : "
                  + $"{viewModel.TimelineItems.FirstOrDefault(i => i.NodeId == snapshotTarget?.NodeId)?.DisplayName} "
                  + $"(IsSnapshot="
                  + $"{viewModel.TimelineItems.FirstOrDefault(i => i.NodeId == snapshotTarget?.NodeId)?.IsSnapshot})");

        if (!cleared)
        {
            failures.Add("A10.8: the snapshot mark could not be removed again.");
        }

        // --------------------------------------------------------------------------------- verdict
        details.Add(string.Empty);
        details.Add($"Assertion failures : {failures.Count}");

        foreach (var failure in failures)
        {
            details.Add($"  - {failure}");
        }

        var actual =
            $"service=resolved, nodes={countAfterFirstScan} (scan#2 still {nodesAfterSecondScan.Count}), "
            + $"emptySceneLabels={withoutScene.Count}, auto3.DisplayName='{auto3?.DisplayName ?? "(none)"}', "
            + $"cg={cgUnlockedCounts.FirstOrDefault()}/{cgTotalCounts.FirstOrDefault()} "
            + $"ids={{{string.Join(",", cgUnlockedIds)}}}, "
            + $"missingDir={failedReport?.FailureKind.ToString() ?? "threw"}/"
            + $"{failedReport?.FailureReason?.Length ?? 0} chars, "
            + $"rows unchanged by the failed scan={countBeforeFailedScan == countAfterFailedScan}";

        if (failures.Count > 0)
        {
            details.Add("FAIL REASON: " + failures[0]);
            return CheckResult.Fail(Id, Title, expected, actual).With(details.ToArray());
        }

        return CheckResult.Pass(Id, Title, expected, actual).With(details.ToArray());
    }

    /// <summary>
    /// Builds the real <see cref="SaveManagerViewModel"/> the Save Manager page binds to, with the
    /// container's own services. Driving this class is the point: it is the code the page's buttons
    /// and its timeline ItemsControl call.
    /// </summary>
    private static SaveManagerViewModel BuildViewModel(AcceptanceContext context)
    {
        return new SaveManagerViewModel(
            context.Get<IDbContextFactory<GalboxDbContext>>(),
            context.Get<Galbox.App.Services.ISaveManagementService>(),
            context.Get<ILogger<SaveManagerViewModel>>(),
            context.Services.GetRequiredService<IServiceScopeFactory>());
    }

    /// <summary>
    /// Persists a second library game whose engine has no save parser, so A10.8 can prove that the
    /// UI says "暂不支持" instead of showing an empty (or worse, invented) timeline.
    /// </summary>
    private static async Task<GameInfo> PersistForeignEngineGameAsync(
        AcceptanceContext context, CancellationToken cancellationToken)
    {
        var game = new GameInfo
        {
            NameOriginal = "A10 unsupported-engine probe",
            InstallPath = context.Options.GameFolder,
            MainExecutable = Path.Combine(context.Options.GameFolder, "dreaminher.exe"),
            EngineType = GameEngineType.Krkr,
            AddedTime = DateTime.UtcNow,
            UpdatedTime = DateTime.UtcNow,
        };

        using var scope = context.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GalboxDbContext>();
        db.Games.Add(game);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return game;
    }

    /// <summary>
    /// True for the timeline entry of the <c>auto-3</c> save; matches both the raw slot key
    /// (<c>auto-3-LT1</c>) the parser produces and a bare <c>auto-3</c>.
    /// </summary>
    private static bool IsAuto3Entry(SaveNodeTimelineEntry entry)
    {
        var key = entry.SlotKey;

        return key.Equals("auto-3", StringComparison.OrdinalIgnoreCase)
            || key.Equals("auto-3-LT1", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("auto-3-", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True for the node of the <c>auto-3</c> save. The slot key is the file name without the
    /// <c>.save</c> suffix (<c>auto-3-LT1</c>), and the stored <see cref="SaveNode.SlotName"/> is
    /// that same key for this game because it sets no player save name.
    /// </summary>
    private static bool IsAuto3Slot(SaveNode node)
    {
        var slot = node.SlotName ?? string.Empty;

        return slot.Equals("auto-3", StringComparison.OrdinalIgnoreCase)
            || slot.Equals("auto-3-LT1", StringComparison.OrdinalIgnoreCase)
            || slot.StartsWith("auto-3-", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Reads the stored nodes of the game straight from the database.</summary>
    private static async Task<List<SaveNode>> ReadNodesAsync(
        AcceptanceContext context, int gameInfoId, CancellationToken cancellationToken)
    {
        using var scope = context.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GalboxDbContext>();

        return await db.SaveNodes
            .AsNoTracking()
            .Where(node => node.GameInfoId == gameInfoId)
            .OrderBy(node => node.SaveModifiedTime)
            .ThenBy(node => node.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Pads a value for the fixed-width detail table without throwing on null.</summary>
    private static string Pad(string? value)
    {
        var text = string.IsNullOrEmpty(value) ? "(empty)" : value;
        return text.Length <= 30 ? text : text[..29] + "…";
    }
}
