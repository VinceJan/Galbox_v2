using System.IO.Compression;
using System.Security.Cryptography;
using Galbox.Acceptance.Support;
using Galbox.App.Services;
using Galbox.App.ViewModels;
using Galbox.Data.Entities;
using Galbox.Services.Saves;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Galbox.Acceptance.Checks;

/// <summary>
/// A80 - the whole user journey over the real game, in one chain.
///
/// A0-A42 measure the product one feature at a time. Every one of them can be green while the
/// product is still unusable, because none of them asks the only question a user asks: "can I get
/// from a folder on disk to a backed-up save?" This check is that question, and it is a CHAIN:
/// every step consumes the artifact the previous step produced, and a step that does not produce
/// its artifact stops the run with the step id in the failure text.
///
///   A80.1 add the game from its installation folder through the real
///         <see cref="LibraryViewModel"/> (the Library page's own button handler) and assert the
///         row it wrote: install path, main executable, detected engine = Ren'Py, folder size.
///   A80.2 scrape it through the real <see cref="IAutoScrapingService"/> pipeline (all four live
///         sources, details fetch, auto-accept threshold, image download) and assert the metadata
///         that landed in the database row A80.1 created.
///   A80.3 locate the save folder through <see cref="ISaveManagementService"/> and assert the 12
///         real save files were found.
///   A80.4 parse and scan those saves through <see cref="ISaveNodeScanService"/> and assert the
///         story nodes that were persisted - reading them back out of the database.
///   A80.5 render the timeline through the real <see cref="SaveManagerViewModel"/> the Save Manager
///         page binds to, and assert the rendered entries ARE the node rows of A80.4.
///   A80.6 back the save folder up and assert the archive on disk really holds those saves
///         (per-entry SHA-256 against the files on disk).
///   A80.7 restore that archive into an isolated copy of the save folder and assert the result is
///         byte-identical to the state before the backup.
///
/// <para><b>Data safety.</b> The game installation <c>D:\GAME\Dreamin'_Her</c> is READ ONLY for this
/// check; the check re-hashes every save file at the end and FAILS if a single byte changed. Saves
/// are backed up and restored against a byte-identical clone under the acceptance scratch folder,
/// because restoring into the real game directory is never allowed. The acceptance database, and the
/// backup archive, live under the run's own directory; the real
/// <c>%LocalAppData%\Galbox\galbox.db</c> and <c>%LocalAppData%\Galbox\SaveBackups</c> are measured
/// before and after and must be untouched.</para>
///
/// <para><b>Why A80.1 removes a row first.</b> A0 plants a library row for this same install folder as
/// its own fixture, and the add flow correctly refuses a folder that is already in the library. That
/// fixture is created by this harness (not by the user), and every check that consumes it has already
/// run, so A80.1 removes it and lets the real add flow create the row that the rest of the chain
/// uses. The removal is printed with its row count; nothing else in the database is touched.</para>
/// </summary>
public sealed class A80FullChainCheck : IAcceptanceCheck
{
    /// <summary>Ground truth: the reference game has 12 save slots (identical to A10).</summary>
    private const int ExpectedNodeCount = 12;

    /// <summary>Ground truth: the reference game ships 12 <c>*.save</c> files plus <c>persistent</c>.</summary>
    private const int ExpectedSaveFileCount = 12;

    /// <inheritdoc />
    public string Id => "A80";

    /// <inheritdoc />
    public string Title =>
        "Full chain on the real game: add -> scrape -> locate saves -> scan nodes -> timeline -> backup -> restore";

    /// <inheritdoc />
    public async Task<CheckResult> RunAsync(AcceptanceContext context, CancellationToken cancellationToken)
    {
        var expected =
            "one chain over '" + context.Options.GameFolder + "': the real add flow writes a Ren'Py game row, "
            + "the real scrape pipeline fills it, the save folder with " + ExpectedSaveFileCount
            + " saves is located, the scan stores " + ExpectedNodeCount + " story nodes, the real "
            + "SaveManagerViewModel renders exactly those nodes, a backup archive holding those saves is "
            + "produced, and restoring it reproduces the save folder byte for byte - with the game folder "
            + "and the real user database unchanged";

        var details = new List<string>();
        var completedSteps = new List<string>();
        var work = AcceptanceWork.Create("a80");

        var realAppData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Galbox");
        var realDatabasePath = Path.Combine(realAppData, "galbox.db");
        var realBackupStorePath = Path.Combine(realAppData, "SaveBackups");

        // ------------------------------------------------------------------ safety: before state
        var realDatabaseBefore = FileSnapshot.Capture(realDatabasePath);
        var realBackupStoreBefore = FolderSnapshot.Capture(realBackupStorePath, recursive: false);

        // Both places the reference game keeps its saves in. The game writes every save to both
        // (Ren'Py's user savedir and the portable game\saves), which is why the save-node parser
        // treats them as one mirror pair. Both must survive this check unchanged.
        var portableSaveFolder = Path.Combine(context.Options.GameFolder, "game", "saves");
        var appDataSaveFolders = FindMirroredApplicationDataSaveFolders(portableSaveFolder, out var appDataCandidates);

        var readOnlyFolders = new List<(string Label, string Path, Dictionary<string, FileSnapshot> State)>
        {
            ("game\\saves (in the installation)", portableSaveFolder, FolderSnapshot.Capture(portableSaveFolder, recursive: true))
        };

        foreach (var mirror in appDataSaveFolders)
        {
            readOnlyFolders.Add(("%APPDATA%\\RenPy mirror", mirror, FolderSnapshot.Capture(mirror, recursive: true)));
        }

        details.Add($"Scratch folder       : {work}");
        details.Add($"Acceptance DB        : {AcceptanceContainer.DatabasePath}");
        details.Add($"Real app DB          : {realDatabasePath} ({realDatabaseBefore})");
        details.Add($"Real backup store    : {realBackupStorePath} "
                  + $"({realBackupStoreBefore.Count} file(s), {realBackupStoreBefore.Values.Sum(v => Math.Max(v.SizeBytes, 0))} bytes)");
        details.Add($"Acceptance backup    : {AcceptanceContainer.IsolatedBackupRoot} (isolated: "
                  + $"{!PathsEqual(AcceptanceContainer.IsolatedBackupRoot, realBackupStorePath)})");
        details.Add($"Game folder (READ ONLY) : {context.Options.GameFolder}");
        details.Add($"  game\\saves         : {FileSnapshot.Describe(readOnlyFolders[0].State, ExpectedSaveFileCount + 1)}");
        details.Add($"  %APPDATA%\\RenPy folders holding *.save files: {appDataCandidates.Count}");
        foreach (var candidate in appDataCandidates)
        {
            details.Add($"      {candidate}");
        }

        if (appDataSaveFolders.Count == 0)
        {
            details.Add("  (no Ren'Py user savedir mirrors the installation's save folder, so only game\\saves is watched)");
        }
        else
        {
            for (var i = 1; i < readOnlyFolders.Count; i++)
            {
                details.Add($"  mirror in use       : {readOnlyFolders[i].Path} "
                          + $"({FileSnapshot.Describe(readOnlyFolders[i].State, ExpectedSaveFileCount + 1)})");
            }
        }

        if (PathsEqual(AcceptanceContainer.DatabasePath, realDatabasePath))
        {
            details.Add("FAIL REASON: the acceptance database IS the user's real database.");
            return CheckResult.Fail(Id, Title, expected, "acceptance DB is the real galbox.db")
                .With(details.ToArray());
        }

        if (!Directory.Exists(context.Options.GameFolder))
        {
            details.Add($"FAIL REASON: the reference game is not installed at {context.Options.GameFolder}.");
            return CheckResult.Fail(Id, Title, expected, "game folder missing").With(details.ToArray());
        }

        // =============================================================================== A80.1 add
        details.Add(string.Empty);
        details.Add("=== A80.1 add the game from its installation folder (real LibraryViewModel) ===");

        int removedFixtures;
        await using (var scope = context.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GalboxDbContext>();
            removedFixtures = await db.Games
                .Where(g => g.InstallPath == context.Options.GameFolder)
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        details.Add($"  harness fixture rows removed for this install path: {removedFixtures} "
                  + "(A0's seeded row; the real add flow refuses a folder that is already in the library)");

        var navigation = new HeadlessNavigationService();
        var scrapingSettings = new ScrapingSettingsProvider(
            context.Services,
            context.Get<ILogger<ScrapingSettingsProvider>>());

        var library = ActivatorUtilities.CreateInstance<LibraryViewModel>(
            context.Services, navigation, scrapingSettings);

        var addedGameId = await library
            .AddGameAsync(context.Options.GameFolder)
            .ConfigureAwait(false);

        details.Add($"  AddGameAsync returned : {addedGameId}");
        details.Add($"  ViewModel.ErrorMessage: {library.ErrorMessage ?? "(null)"}");
        details.Add($"  ViewModel.SuccessMessage: {library.SuccessMessage ?? "(null)"}");
        details.Add($"  navigation requests   : [{string.Join(", ", navigation.Requests)}]");

        var game = addedGameId > 0 ? await ReadGameAsync(context, addedGameId, cancellationToken).ConfigureAwait(false) : null;

        if (game is null)
        {
            return Broken("A80.1", "the real add flow did not produce a library row for "
                + $"'{context.Options.GameFolder}' (AddGameAsync returned {addedGameId}, "
                + $"ErrorMessage='{library.ErrorMessage ?? "(null)"}')");
        }

        details.Add("  --- the row the add flow wrote ---");
        details.Add($"  Id                   : {game.Id}");
        details.Add($"  NameOriginal         : {game.NameOriginal}");
        details.Add($"  InstallPath          : {game.InstallPath}");
        details.Add($"  MainExecutable       : {game.MainExecutable}");
        details.Add($"  EngineType           : {game.EngineType} (expected {context.Options.ExpectedEngine})");
        details.Add($"  SizeBytes            : {game.SizeBytes} (minimum {context.Options.MinimumFolderSizeBytes})");
        details.Add($"  IsScraped            : {game.IsScraped}");

        if (game.EngineType != context.Options.ExpectedEngine)
        {
            return Broken("A80.1", $"the engine was detected as {game.EngineType}, expected {context.Options.ExpectedEngine}");
        }

        if (!string.Equals(game.InstallPath, context.Options.GameFolder, StringComparison.Ordinal))
        {
            return Broken("A80.1", $"the stored install path is '{game.InstallPath}'");
        }

        if (string.IsNullOrWhiteSpace(game.MainExecutable) || !File.Exists(game.MainExecutable))
        {
            return Broken("A80.1", $"the stored main executable '{game.MainExecutable ?? "(null)"}' does not exist");
        }

        if (game.SizeBytes < context.Options.MinimumFolderSizeBytes)
        {
            return Broken("A80.1", $"the stored folder size {game.SizeBytes} is below the measured minimum");
        }

        completedSteps.Add("A80.1");

        // ============================================================================ A80.2 scrape
        details.Add(string.Empty);
        details.Add("=== A80.2 scrape the metadata through the real auto-scrape pipeline ===");

        var scraper = context.Get<IAutoScrapingService>();

        // Exactly what the "开始刮削" button does: enqueue, then run the batch. The service performs
        // the four-source query, fetches the details record of the winner, applies it when the match
        // clears the configured threshold, and downloads the images.
        scraper.EnqueueGame(game.Id);
        await scraper.StartScrapingAsync(cancellationToken).ConfigureAwait(false);

        var progress = scraper.GetCurrentProgress();
        var stored = await ReadGameAsync(context, game.Id, cancellationToken).ConfigureAwait(false);

        details.Add($"  progress: total={progress.TotalGames}, completed={progress.CompletedGames}, "
                  + $"success={progress.SuccessCount}, failed={progress.FailedCount}, "
                  + $"skipped={progress.SkippedCount}, needsReview={progress.NeedsReviewCount}, "
                  + $"cancelled={progress.IsCancelled}");

        if (stored is null)
        {
            return Broken("A80.2", "the game row disappeared while scraping");
        }

        var coverPathExists = !string.IsNullOrWhiteSpace(stored.CoverImagePath) && File.Exists(stored.CoverImagePath);

        details.Add("  --- the metadata that landed in the database ---");
        details.Add($"  IsScraped            : {stored.IsScraped}");
        details.Add($"  NameCn               : {stored.NameCn ?? "(null)"}");
        details.Add($"  NameOriginal         : {stored.NameOriginal}");
        details.Add($"  SourceType / SourceId: {stored.SourceType ?? "(null)"} / {stored.SourceId ?? "(null)"}");
        details.Add($"  VndbId               : {stored.VndbId ?? "(null)"}");
        details.Add($"  Developer            : {stored.Developer ?? "(null)"}");
        details.Add($"  ReleaseDate          : {(stored.ReleaseDate.HasValue ? stored.ReleaseDate.Value.ToString("yyyy-MM-dd") : "(null)")}");
        details.Add($"  Rating               : {(stored.Rating.HasValue ? stored.Rating.Value.ToString("F1") : "(null)")}");
        details.Add($"  Description          : {(string.IsNullOrWhiteSpace(stored.Description) ? "(empty)" : $"{stored.Description!.Length} chars")}");
        details.Add($"  CoverImageUrl        : {stored.CoverImageUrl ?? "(null)"}");
        details.Add($"  CoverImagePath       : {stored.CoverImagePath ?? "(null)"} (file on disk: {coverPathExists})");

        if (!stored.IsScraped)
        {
            return Broken("A80.2", $"the scrape did not persist metadata (status={progress.FailedCount} failed, "
                + $"{progress.NeedsReviewCount} needing review)");
        }

        if (string.IsNullOrWhiteSpace(stored.SourceType) || string.IsNullOrWhiteSpace(stored.SourceId))
        {
            return Broken("A80.2", "the row has no source type/id, so it cannot be traced back to an upstream record");
        }

        if (string.IsNullOrWhiteSpace(stored.NameCn) && string.IsNullOrWhiteSpace(stored.NameOriginal))
        {
            return Broken("A80.2", "the row has no title at all");
        }

        if (string.IsNullOrWhiteSpace(stored.Description) || string.IsNullOrWhiteSpace(stored.CoverImageUrl))
        {
            return Broken("A80.2", "the row has no description or no cover URL "
                + $"(description={(string.IsNullOrWhiteSpace(stored.Description) ? "empty" : "present")}, "
                + $"coverUrl={(string.IsNullOrWhiteSpace(stored.CoverImageUrl) ? "empty" : "present")})");
        }

        if (!coverPathExists)
        {
            return Broken("A80.2", $"the cover was never downloaded to disk (CoverImagePath='{stored.CoverImagePath ?? "(null)"}')");
        }

        // The image must not have landed anywhere near the user's real library.
        var isolatedImageRoot = Path.Combine(Path.GetDirectoryName(AcceptanceContainer.DatabasePath)!, $"images-{Environment.ProcessId}");
        if (!stored.CoverImagePath!.StartsWith(isolatedImageRoot, StringComparison.OrdinalIgnoreCase))
        {
            return Broken("A80.2", $"the cover was written outside the isolated image root: {stored.CoverImagePath}");
        }

        details.Add($"  cover written inside : {isolatedImageRoot}");
        completedSteps.Add("A80.2");

        // ===================================================================== A80.3 locate saves
        details.Add(string.Empty);
        details.Add("=== A80.3 locate the save folder (real SaveManagementService) ===");

        var saveService = context.Get<ISaveManagementService>();
        var location = await saveService.DetectSaveLocationAsync(stored, cancellationToken).ConfigureAwait(false);

        details.Add($"  Success              : {location.Success}");
        details.Add($"  EngineType           : {location.EngineType}");
        details.Add($"  PrimarySavePath      : {location.PrimarySavePath ?? "(null)"}");
        details.Add($"  AlternativePaths     : {location.AlternativePaths.Count}");
        foreach (var alternative in location.AlternativePaths)
        {
            details.Add($"      {alternative} (exists: {Directory.Exists(alternative)})");
        }

        details.Add($"  SaveFiles            : {location.SaveFiles.Count}");
        details.Add($"  ErrorMessage         : {location.ErrorMessage ?? "(null)"}");
        details.Add($"  Backup storage root  : {saveService.BackupStoragePath}");

        if (!location.Success)
        {
            return Broken("A80.3", $"the save folder could not be located: {location.ErrorMessage ?? "(no reason given)"}");
        }

        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(location.PrimarySavePath))
        {
            candidates.Add(location.PrimarySavePath!);
        }

        candidates.AddRange(location.AlternativePaths);

        var detectedSaveFolder = candidates
            .FirstOrDefault(p => Path.GetFileName(p.TrimEnd(Path.DirectorySeparatorChar))
                .Equals("saves", StringComparison.OrdinalIgnoreCase));

        if (detectedSaveFolder is null)
        {
            return Broken("A80.3", "no reported path ends in 'saves': " + string.Join(" | ", candidates));
        }

        var saveFileCount = Directory.GetFiles(detectedSaveFolder, "*.save").Length;
        details.Add($"  save folder in use   : {detectedSaveFolder}");
        details.Add($"  *.save files there   : {saveFileCount} (expected {ExpectedSaveFileCount})");

        if (saveFileCount != ExpectedSaveFileCount)
        {
            return Broken("A80.3", $"the located folder holds {saveFileCount} save files, expected {ExpectedSaveFileCount}");
        }

        // Honest note, not an assertion: the Ren'Py user savedir is derivable from the game's own
        // config.save_directory, and this machine has one. It is printed so a future failure at
        // A80.6 can be read against the folder the product actually chose.
        foreach (var mirror in appDataSaveFolders)
        {
            if (PathsEqual(mirror, detectedSaveFolder) || candidates.Any(p => PathsEqual(p, mirror)))
            {
                continue;
            }

            details.Add("  NOTE: the game's own Ren'Py user savedir holds the same saves, but the detector");
            details.Add($"        did not report it: {mirror}");
        }

        completedSteps.Add("A80.3");

        // ============================================================================== A80.4 scan
        details.Add(string.Empty);
        details.Add("=== A80.4 parse the saves and scan the story nodes (real SaveNodeScanService) ===");

        SaveNodeScanReport report;
        await using (var scanScope = context.Services.CreateAsyncScope())
        {
            var scan = scanScope.ServiceProvider.GetService<ISaveNodeScanService>();
            if (scan is null)
            {
                return Broken("A80.4", "ISaveNodeScanService is not registered in the app-shaped container");
            }

            report = await scan.ScanAsync(game.Id, game.InstallPath, cancellationToken).ConfigureAwait(false);
            details.Add($"  scan report          : succeeded={report.Succeeded}, kind={report.FailureKind}, "
                      + $"slots={report.SlotCount}, added={report.AddedCount}, updated={report.UpdatedCount}, "
                      + $"skipped={report.SkippedCount}, failed={report.FailedCount}, "
                      + $"nodes={report.NodeCountAfterScan}, elapsed={report.Elapsed.TotalMilliseconds:F0} ms");
            details.Add($"  scene progress       : {report.UnlockedSceneCount}/{report.TotalSceneCount}");
            details.Add($"  CG progress          : {report.CgUnlockedCount}/{report.CgTotalCount} "
                      + $"ids={{{string.Join(",", report.CgUnlockedIds)}}}");

            if (!report.Succeeded)
            {
                return Broken("A80.4", $"the scan failed: {report.FailureKind} - {report.FailureReason ?? "(no reason)"}");
            }
        }

        if (report.NodeCountAfterScan != ExpectedNodeCount)
        {
            return Broken("A80.4", $"the scan stored {report.NodeCountAfterScan} nodes, expected {ExpectedNodeCount}");
        }

        if (report.FailedCount != 0)
        {
            return Broken("A80.4", $"{report.FailedCount} save(s) could not be parsed");
        }

        var nodes = await ReadNodesAsync(context, game.Id, cancellationToken).ConfigureAwait(false);

        details.Add($"  --- the rows in the database ({nodes.Count}) ---");
        details.Add("  Id    SlotName             SceneLabel                     CG      Modified (UTC)");
        foreach (var node in nodes)
        {
            details.Add($"  {node.Id,-5} {(node.SlotName ?? "(null)"),-20} {(node.SceneLabel ?? "(null)"),-30} "
                      + $"{node.CgUnlockedCount}/{node.CgTotalCount,-3} {node.SaveModifiedTime:yyyy-MM-dd HH:mm:ss}");
        }

        if (nodes.Count != ExpectedNodeCount)
        {
            return Broken("A80.4", $"the node table holds {nodes.Count} rows, expected {ExpectedNodeCount}");
        }

        var unlabelled = nodes.Where(n => string.IsNullOrWhiteSpace(n.SceneLabel)).ToList();
        if (unlabelled.Count > 0)
        {
            return Broken("A80.4", $"{unlabelled.Count} node(s) have no scene label: "
                + string.Join(", ", unlabelled.Select(n => n.SlotName ?? "(null)")));
        }

        var nodeIds = nodes.Select(n => n.Id).OrderBy(id => id).ToList();
        var cgUnlockedIds = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (string.IsNullOrWhiteSpace(node.CgUnlockedIdsJson))
            {
                continue;
            }

            foreach (var id in System.Text.Json.JsonSerializer.Deserialize<string[]>(node.CgUnlockedIdsJson!) ?? Array.Empty<string>())
            {
                cgUnlockedIds.Add(id);
            }
        }

        details.Add($"  node ids             : [{string.Join(",", nodeIds)}]");
        details.Add($"  union of CG ids      : {{{string.Join(",", cgUnlockedIds)}}}");
        completedSteps.Add("A80.4");

        // ========================================================================= A80.5 present it
        details.Add(string.Empty);
        details.Add("=== A80.5 render the timeline (real SaveManagerViewModel) ===");

        var viewModel = new SaveManagerViewModel(
            context.Get<IDbContextFactory<GalboxDbContext>>(),
            saveService,
            context.Get<ILogger<SaveManagerViewModel>>(),
            context.Services.GetRequiredService<IServiceScopeFactory>());

        details.Add($"  before loading: IsTimelineEmpty={viewModel.IsTimelineEmpty}, "
                  + $"status=\"{viewModel.SaveNodeStatusText}\"");

        if (!viewModel.IsTimelineEmpty)
        {
            return Broken("A80.5", "a fresh ViewModel already claims to hold a timeline");
        }

        await viewModel.LoadSaveNodesAsync(game.Id, cancellationToken).ConfigureAwait(false);

        var renderedIds = viewModel.TimelineItems.Select(item => item.NodeId).OrderBy(id => id).ToList();
        var blankLabels = viewModel.TimelineItems.Count(item => string.IsNullOrWhiteSpace(item.DisplayName));

        details.Add($"  TimelineItems.Count  : {viewModel.TimelineItems.Count}");
        details.Add($"  node ids rendered    : [{string.Join(",", renderedIds)}]");
        details.Add($"  same rows as A80.4   : {renderedIds.SequenceEqual(nodeIds)}");
        details.Add($"  blank labels         : {blankLabels}");
        details.Add($"  CgHeadlineText       : {viewModel.CgHeadlineText}");
        details.Add($"  CgRemainingText      : {viewModel.CgRemainingText}");
        details.Add($"  SceneProgressText    : {viewModel.SceneProgressText}");
        details.Add($"  StatusText           : {viewModel.SaveNodeStatusText}");
        details.Add($"  ParseProblemCount    : {viewModel.ParseProblemCount}");
        details.Add($"  error banner open    : {viewModel.HasSaveNodeError} ({viewModel.SaveNodeErrorText ?? "(none)"})");
        details.Add("  --- what the page would show, oldest first ---");
        foreach (var entry in viewModel.TimelineItems)
        {
            details.Add($"  [{entry.TimeText}] #{entry.NodeId} {entry.DisplayName} | {entry.SlotKindText} | "
                      + $"slot={entry.SlotKeyText} | parse={entry.ParseStatusText}");
        }

        if (viewModel.TimelineItems.Count != ExpectedNodeCount)
        {
            return Broken("A80.5", $"the timeline renders {viewModel.TimelineItems.Count} entries, expected {ExpectedNodeCount}");
        }

        if (!renderedIds.SequenceEqual(nodeIds))
        {
            return Broken("A80.5", "the timeline is not the set of node rows A80.4 stored");
        }

        if (blankLabels > 0)
        {
            return Broken("A80.5", $"{blankLabels} timeline entry/entries render a blank name");
        }

        // The rendered figure must be the figure the scan measured, not an independently invented one.
        var expectedCgHeadline = $"{report.CgUnlockedCount} / {report.CgTotalCount} "
                               + $"({(report.CgTotalCount > 0 ? report.CgUnlockedCount * 100.0 / report.CgTotalCount : 0):F1}%)";

        if (viewModel.CgHeadlineText != expectedCgHeadline)
        {
            return Broken("A80.5", $"the timeline shows '{viewModel.CgHeadlineText}' for the CG progress while the "
                + $"scan measured '{expectedCgHeadline}'");
        }

        if (viewModel.HasSaveNodeError)
        {
            return Broken("A80.5", $"loading a healthy timeline left an error banner open: {viewModel.SaveNodeErrorText}");
        }

        completedSteps.Add("A80.5");

        // ============================================================================= A80.6 backup
        details.Add(string.Empty);
        details.Add("=== A80.6 back the save folder up ===");

        var backup = await saveService
            .CreateBackupAsync(stored, "A80 full-chain acceptance backup", null, cancellationToken)
            .ConfigureAwait(false);

        if (backup is null)
        {
            details.Add("  CreateBackupAsync    : null (no archive was produced)");
            details.Add($"  DetectSaveLocation   : Success={location.Success}, "
                      + $"PrimarySavePath={location.PrimarySavePath ?? "(null)"}, "
                      + $"AlternativePaths={location.AlternativePaths.Count}, SaveFiles={location.SaveFiles.Count}");
            details.Add("  What the user sees   : the Save Manager page reports "
                      + "\"创建备份失败。未检测到存档文件。\" although A80.3 just found "
                      + $"{location.SaveFiles.Count} save files in {detectedSaveFolder}.");

            return Broken("A80.6", "CreateBackupAsync produced no archive. It requires "
                + "SaveLocationResult.PrimarySavePath, and the detector reports the real save folder only "
                + $"under AlternativePaths (PrimarySavePath={location.PrimarySavePath ?? "(null)"}, "
                + $"AlternativePaths=[{string.Join(" | ", location.AlternativePaths)}]), so the backup/restore "
                + "half of the chain cannot run at all for this game");
        }

        var backupOnDisk = new FileInfo(backup.BackupPath);
        var backupEntries = ReadZipEntries(backup.BackupPath);

        details.Add($"  backup row Id        : {backup.Id}");
        details.Add($"  Name                 : {backup.Name}");
        details.Add($"  BackupPath           : {backup.BackupPath}");
        details.Add($"  file on disk         : exists={backupOnDisk.Exists}, {backupOnDisk.Length} bytes, "
                  + $"row SizeBytes={backup.SizeBytes}");
        details.Add($"  OriginalSavePath     : {backup.OriginalSavePath ?? "(null)"}");
        details.Add($"  archive entries      : {backupEntries.Count} [{string.Join(", ", backupEntries.Keys)}]");

        if (!backupOnDisk.Exists || backupOnDisk.Length == 0)
        {
            return Broken("A80.6", $"the archive the service reported does not exist on disk: {backup.BackupPath}");
        }

        if (!backup.BackupPath.StartsWith(saveService.BackupStoragePath, StringComparison.OrdinalIgnoreCase)
            || PathsEqual(saveService.BackupStoragePath, realBackupStorePath))
        {
            return Broken("A80.6", $"the archive was written outside the isolated backup store: {backup.BackupPath}");
        }

        // The archive must hold the save files that are really on disk, byte for byte.
        var sourceSaves = Directory.GetFiles(detectedSaveFolder, "*", SearchOption.TopDirectoryOnly)
            .ToDictionary(p => Path.GetFileName(p)!, p => p, StringComparer.OrdinalIgnoreCase);

        var mismatched = new List<string>();
        foreach (var (name, path) in sourceSaves)
        {
            if (!backupEntries.TryGetValue(name, out var entry))
            {
                mismatched.Add($"{name}: missing from the archive");
                continue;
            }

            var sourceHash = AcceptanceWork.HashFile(path);
            var entryHash = AcceptanceWork.HashFile(entry);
            details.Add($"    {name,-22} {new FileInfo(path).Length,9} bytes  sha256 {sourceHash[..16]}...  "
                      + $"archive {(sourceHash == entryHash ? "MATCHES" : "DIFFERS -> " + entryHash[..16])}");
            if (sourceHash != entryHash)
            {
                mismatched.Add($"{name}: content differs");
            }
        }

        if (mismatched.Count > 0)
        {
            return Broken("A80.6", "the archive does not reproduce the save folder: " + string.Join("; ", mismatched));
        }

        completedSteps.Add("A80.6");

        // ============================================================================ A80.7 restore
        details.Add(string.Empty);
        details.Add("=== A80.7 restore the archive and compare byte for byte ===");

        // The restore destination is an isolated clone of the save folder. Restoring into
        // D:\GAME\Dreamin'_Her is forbidden (the game folder is read-only for this check), and the
        // restore path deletes the target folder's files before copying - which is exactly why the
        // clone exists. The archive, the database row and the restore code path are the real ones.
        var liveClone = Path.Combine(work, "live-saves");
        Directory.CreateDirectory(liveClone);
        foreach (var (name, path) in sourceSaves)
        {
            File.Copy(path, Path.Combine(liveClone, name), overwrite: true);
        }

        var cloneBefore = FolderSnapshot.Capture(liveClone, recursive: false);
        details.Add($"  clone of the save folder : {liveClone} ({FileSnapshot.Describe(cloneBefore, sourceSaves.Count)})");

        if (cloneBefore.Count != sourceSaves.Count
            || cloneBefore.Any(pair => !sourceSaves.TryGetValue(pair.Key, out var source)
                                    || AcceptanceWork.HashFile(source) != pair.Value.Sha256))
        {
            return Broken("A80.7", "the isolated clone is not byte-identical to the save folder");
        }

        // Point the backup row at the clone. Everything else about the row (archive path, name,
        // size) is what the product wrote in A80.6.
        await using (var scope = context.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GalboxDbContext>();
            var row = await db.SaveBackups.SingleAsync(b => b.Id == backup.Id, cancellationToken).ConfigureAwait(false);
            row.OriginalSavePath = liveClone;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        details.Add("  restore destination redirected to the clone (the real game folder is never written)");

        // Simulate what play does: one save changes, another disappears.
        var mutatedFile = Path.Combine(liveClone, sourceSaves.Keys.OrderBy(n => n, StringComparer.Ordinal).First());
        var deletedFile = Path.Combine(liveClone, sourceSaves.Keys.OrderBy(n => n, StringComparer.Ordinal).Last());
        await File.WriteAllTextAsync(mutatedFile, "A80: this save file must be rolled back by the restore", cancellationToken)
            .ConfigureAwait(false);
        File.Delete(deletedFile);

        details.Add($"  mutated before restore   : {Path.GetFileName(mutatedFile)} (overwritten with placeholder text)");
        details.Add($"  deleted before restore   : {Path.GetFileName(deletedFile)}");

        var restored = await saveService
            .RestoreBackupAsync(backup.Id, null, cancellationToken)
            .ConfigureAwait(false);

        var cloneAfter = FolderSnapshot.Capture(liveClone, recursive: false);
        details.Add($"  RestoreBackupAsync       : {restored}");
        details.Add($"  files after restore      : [{string.Join(", ", cloneAfter.Keys)}]");

        var comparesToOriginal = new List<string>();
        foreach (var (name, before) in cloneBefore)
        {
            if (!cloneAfter.TryGetValue(name, out var after))
            {
                comparesToOriginal.Add($"{name}: NOT restored");
                details.Add($"    {name,-22} MISSING after the restore");
                continue;
            }

            var same = before.Sha256 == after.Sha256;
            details.Add($"    {name,-22} {before.SizeBytes,9} bytes  restore {(same ? "IDENTICAL" : "DIFFERENT")}  "
                      + $"{before.Sha256[..16]}... vs {after.Sha256[..16]}...");
            if (!same)
            {
                comparesToOriginal.Add($"{name}: content differs after the restore");
            }
        }

        if (!restored)
        {
            return Broken("A80.7", "RestoreBackupAsync reported failure");
        }

        if (comparesToOriginal.Count > 0)
        {
            return Broken("A80.7", "the restored folder is not byte-identical to the state before the backup: "
                + string.Join("; ", comparesToOriginal));
        }

        if (cloneAfter.Count != cloneBefore.Count)
        {
            return Broken("A80.7", $"the restore left {cloneAfter.Count} files where {cloneBefore.Count} were backed up");
        }

        completedSteps.Add("A80.7");

        // ============================================================ safety: the real world again
        details.Add(string.Empty);
        details.Add("=== read-only verification of the user's real data ===");

        var realDatabaseAfter = FileSnapshot.Capture(realDatabasePath);
        var realBackupStoreAfter = FolderSnapshot.Capture(realBackupStorePath, recursive: false);

        var backupStoreDiff = DiffSnapshots(realBackupStoreBefore, realBackupStoreAfter);

        details.Add($"  real galbox.db       : {realDatabaseBefore} -> {realDatabaseAfter}");
        details.Add($"  real SaveBackups     : {realBackupStoreBefore.Count} file(s) -> "
                  + $"{realBackupStoreAfter.Count} file(s), {(backupStoreDiff.Count == 0 ? "UNCHANGED" : "CHANGED")}");

        foreach (var line in backupStoreDiff)
        {
            details.Add($"      {line}");
        }

        var touchedRealData = new List<string>();

        if (realDatabaseBefore != realDatabaseAfter)
        {
            touchedRealData.Add($"the user's real database changed ({realDatabaseBefore} -> {realDatabaseAfter})");
        }

        if (backupStoreDiff.Count > 0)
        {
            touchedRealData.Add($"the user's real backup store changed: {string.Join("; ", backupStoreDiff.Take(3))}");
        }

        foreach (var (label, path, state) in readOnlyFolders)
        {
            var after = FolderSnapshot.Capture(path, recursive: true);
            var diff = DiffSnapshots(state, after);

            details.Add($"  {label,-34}: {FileSnapshot.Describe(state, state.Count)} -> "
                      + $"{FileSnapshot.Describe(after, after.Count)}  {(diff.Count == 0 ? "UNCHANGED" : "CHANGED")}");

            foreach (var line in diff)
            {
                details.Add($"      {line}");
            }

            if (diff.Count > 0)
            {
                touchedRealData.Add($"{label} changed: {string.Join("; ", diff.Take(3))}");
            }
        }

        if (touchedRealData.Count > 0)
        {
            details.Add("FAIL REASON: A80 wrote to data it must only read:");
            foreach (var item in touchedRealData)
            {
                details.Add($"  - {item}");
            }

            return CheckResult
                .Fail(Id, Title, expected, "the check modified real user data: " + touchedRealData[0])
                .With(details.ToArray());
        }

        // ================================================================================= verdict
        details.Add(string.Empty);
        details.Add($"Chain completed       : {string.Join(" -> ", completedSteps)}");
        details.Add($"Nodes                 : {nodes.Count}, first='{nodes.First().SceneLabel}', "
                  + $"CG ids={cgUnlockedIds.Count}, timeline={viewModel.TimelineItems.Count}");
        details.Add($"Archive               : {backup.BackupPath} ({backupOnDisk.Length} bytes, "
                  + $"{backupEntries.Count} entries, all hashes match the save files)");
        details.Add($"Restore               : {cloneBefore.Count} files byte-identical after restoring over a "
                  + "mutated/deleted folder");

        var actual = $"added game #{game.Id} ({game.EngineType}, {game.SizeBytes} bytes) -> scraped from "
                   + $"{stored.SourceType}/{stored.SourceId} ('{stored.NameCn ?? stored.NameOriginal}') -> "
                   + $"saves at {detectedSaveFolder} ({saveFileCount} files) -> {nodes.Count} nodes "
                   + $"(scene '{nodes.First().SceneLabel}') -> {viewModel.TimelineItems.Count} timeline entries, "
                   + $"CG {viewModel.CgHeadlineText} -> backup {backupEntries.Count} entries -> restore "
                   + $"byte-identical ({cloneBefore.Count} files); real galbox.db and game folder unchanged";

        return CheckResult.Pass(Id, Title, expected, actual).With(details.ToArray());

        // ------------------------------------------------------------------ local helpers
        CheckResult Broken(string step, string reason)
        {
            details.Add(string.Empty);
            details.Add($"CHAIN BROKEN AT {step}: {reason}");
            details.Add($"steps completed before the break: "
                      + (completedSteps.Count == 0 ? "(none)" : string.Join(" -> ", completedSteps)));

            return CheckResult.Fail(Id, Title, expected, $"broken at {step}: {reason}")
                .With(details.ToArray());
        }
    }

    /// <summary>Reads a game row straight from the acceptance database.</summary>
    private static async Task<GameInfo?> ReadGameAsync(
        AcceptanceContext context, int gameId, CancellationToken cancellationToken)
    {
        await using var scope = context.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GalboxDbContext>();

        return await db.Games
            .AsNoTracking()
            .FirstOrDefaultAsync(g => g.Id == gameId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Reads the stored save nodes of the game, oldest first, as the timeline query does.</summary>
    private static async Task<List<SaveNode>> ReadNodesAsync(
        AcceptanceContext context, int gameId, CancellationToken cancellationToken)
    {
        await using var scope = context.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GalboxDbContext>();

        return await db.SaveNodes
            .AsNoTracking()
            .Where(node => node.GameInfoId == gameId)
            .OrderBy(node => node.SaveModifiedTime)
            .ThenBy(node => node.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Finds the Ren'Py user savedirs that mirror the installation's save folder.
    ///
    /// Ren'Py writes to <c>%APPDATA%\RenPy\&lt;config.save_directory&gt;</c>, and the folder name is the
    /// game's own configuration value - NOT the installation folder name, which is why the detector
    /// looks in the wrong place. Here the folder is identified by content instead: a Ren'Py
    /// subdirectory that holds exactly the same set of <c>*.save</c> file names as
    /// <c>&lt;install&gt;\game\saves</c> is the mirror of it for this game.
    /// </summary>
    private static List<string> FindMirroredApplicationDataSaveFolders(
        string portableSaveFolder, out List<string> candidates)
    {
        candidates = new List<string>();
        var mirrors = new List<string>();

        var renpyRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RenPy");

        if (!Directory.Exists(renpyRoot) || !Directory.Exists(portableSaveFolder))
        {
            return mirrors;
        }

        HashSet<string> expected;
        try
        {
            expected = Directory.GetFiles(portableSaveFolder, "*.save")
                .Select(path => Path.GetFileName(path)!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return mirrors;
        }

        try
        {
            foreach (var directory in Directory.GetDirectories(renpyRoot))
            {
                var files = Directory.GetFiles(directory, "*.save");
                if (files.Length == 0)
                {
                    continue;
                }

                var names = files.Select(path => Path.GetFileName(path)!).ToHashSet(StringComparer.OrdinalIgnoreCase);
                candidates.Add($"{directory} ({files.Length} *.save files)");

                if (names.SetEquals(expected))
                {
                    mirrors.Add(directory);
                    candidates[^1] += " <- same *.save file set as the installation's save folder";
                }
            }
        }
        catch (Exception)
        {
            return mirrors;
        }

        return mirrors;
    }

    /// <summary>Reads a zip archive into <c>entry name -> extracted temp file</c>.</summary>
    private static Dictionary<string, string> ReadZipEntries(string archivePath)
    {
        var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var extractionRoot = Path.Combine(Path.GetTempPath(), $"galbox-a80-zip-{Guid.NewGuid():N}");
        Directory.CreateDirectory(extractionRoot);

        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue; // directory entry
            }

            var target = Path.Combine(extractionRoot, entry.Name);
            entry.ExtractToFile(target, overwrite: true);
            entries[entry.Name] = target;
        }

        return entries;
    }

    /// <summary>True when two paths denote the same location.</summary>
    private static bool PathsEqual(string left, string right)
    {
        static string Normalize(string path)
        {
            try
            {
                return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch (Exception)
            {
                return path;
            }
        }

        return string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Line-by-line diff of two folder snapshots.</summary>
    private static List<string> DiffSnapshots(
        Dictionary<string, FileSnapshot> before, Dictionary<string, FileSnapshot> after)
    {
        var differences = new List<string>();

        foreach (var (name, snapshot) in before)
        {
            if (!after.TryGetValue(name, out var now))
            {
                differences.Add($"{name}: deleted");
            }
            else if (now != snapshot)
            {
                differences.Add($"{name}: changed ({snapshot} -> {now})");
            }
        }

        foreach (var name in after.Keys.Where(name => !before.ContainsKey(name)))
        {
            differences.Add($"{name}: created");
        }

        return differences;
    }

    /// <summary>Name, size and SHA-256 of one file, usable as a value comparison.</summary>
    private readonly record struct FileSnapshot(long SizeBytes, string Sha256, DateTime LastWriteUtc)
    {
        /// <summary>Captures a file; a missing file is reported as such instead of throwing.</summary>
        internal static FileSnapshot Capture(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return new FileSnapshot(-1, "(missing)", DateTime.MinValue);
                }

                var info = new FileInfo(path);

                using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return new FileSnapshot(info.Length, Convert.ToHexString(SHA256.HashData(stream)), info.LastWriteTimeUtc);
            }
            catch (Exception ex)
            {
                return new FileSnapshot(-1, $"(unreadable: {ex.GetType().Name})", DateTime.MinValue);
            }
        }

        /// <summary>Adds a measured count to the printable form.</summary>
        internal static string Describe(Dictionary<string, FileSnapshot> snapshot, int expectedCount)
            => $"{snapshot.Count} files (expected {expectedCount}), "
             + $"{snapshot.Values.Where(v => v.SizeBytes >= 0).Sum(v => v.SizeBytes)} bytes";

        /// <inheritdoc />
        public override string ToString()
            => SizeBytes < 0 ? Sha256 : $"{SizeBytes} bytes sha256 {Sha256[..16]}...";
    }

    /// <summary>Relative path -> file snapshot, for every file under a folder.</summary>
    private static class FolderSnapshot
    {
        internal static Dictionary<string, FileSnapshot> Capture(string folder, bool recursive)
        {
            var snapshot = new Dictionary<string, FileSnapshot>(StringComparer.OrdinalIgnoreCase);

            if (!Directory.Exists(folder))
            {
                return snapshot;
            }

            try
            {
                foreach (var file in Directory.GetFiles(
                             folder, "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
                {
                    snapshot[Path.GetRelativePath(folder, file)] = FileSnapshot.Capture(file);
                }
            }
            catch (Exception)
            {
                // An unreadable folder simply snapshots as far as it could be read.
            }

            return snapshot;
        }
    }
}
