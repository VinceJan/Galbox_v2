using System.IO.Compression;
using System.Security.Cryptography;
using Galbox.App.Services;
using Galbox.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Galbox.Acceptance.Checks;

/// <summary>
/// A100 - backing up a game whose saves exist only in an <b>alternative</b> save folder.
///
/// <para><b>The defect.</b> <see cref="SaveManagementService.CreateBackupAsync"/> required
/// <see cref="SaveLocationResult.PrimarySavePath"/>, but <c>EngineSaveDetector</c> reports the
/// folder it found under <see cref="SaveLocationResult.PrimarySavePath"/> only when that folder is
/// the engine's canonical save folder. For Ren'Py the canonical folder is
/// <c>%APPDATA%\RenPy\&lt;config.save_directory&gt;</c>, so a game that keeps its saves in the
/// portable <c>&lt;install&gt;\game\saves</c> folder produces <c>Success=true</c>,
/// <c>PrimarySavePath=null</c>, <c>AlternativePaths=[&lt;install&gt;\game\saves]</c> and a full
/// <see cref="SaveLocationResult.SaveFiles"/> list - and the Backup button answered
/// "创建备份失败。未检测到存档文件。" while the very same result carried 12 save files.</para>
///
/// <para><b>What is asserted here</b> (A80 measures the same defect over the real game; this check
/// isolates the rule so it stays pinned without a 40 GB installation):</para>
/// <list type="number">
/// <item><description><b>A100.1</b> - a synthetic Ren'Py installation whose saves live only in
/// <c>&lt;install&gt;\game\saves</c> really does come back with <c>PrimarySavePath=null</c> and the
/// saves folder under <c>AlternativePaths</c>. If the fixture ever stops reproducing that shape the
/// check FAILS saying so, because a test that no longer exercises the fallback is not a test.</description></item>
/// <item><description><b>A100.2</b> - the backup is produced, the archive holds exactly the save
/// files of that folder (per-entry SHA-256 against disk), and it does NOT hold the installation's
/// own files (<c>GameA.exe</c>, <c>game\data.bin</c>).</description></item>
/// <item><description><b>A100.3</b> - the folder really used is recorded in
/// <see cref="GameSaveBackup.OriginalSavePath"/>, both on the returned row and in the database -
/// that column is also the restore destination, so a wrong value would make a restore write the
/// saves into a folder the user never backed up.</description></item>
/// <item><description><b>A100.4</b> - the first entry of <c>AlternativePaths</c> is NOT taken on
/// faith: with an existing but EMPTY <c>game\saves</c> first and a populated
/// <c>&lt;install&gt;\saves</c> second, the backup must come from the second one.</description></item>
/// <item><description><b>A100.5</b> - when nothing can be used the call still fails, and now says
/// why: for save files living directly in the installation root the detector refuses the folder and
/// the service hands that refusal to the page instead of the fixed sentence.</description></item>
/// </list>
///
/// <para><b>Data safety.</b> Every fixture is built under the acceptance run's own scratch folder,
/// every archive lands in the run's isolated backup store, and the installation folders of the
/// fixtures are re-hashed at the end of every scenario that touched them (a backup must never write
/// to the game folder). The user's real <c>galbox.db</c> and <c>SaveBackups</c> are measured before
/// and after.</para>
///
/// <para><b>One assertion is source-level on purpose.</b> A100.4's sibling rule - "an alternative
/// folder must not be the game installation folder, nor a folder that contains it" - cannot be
/// provoked through the public API, because the detector never emits such a path (it has its own
/// refusal for exactly that reason, measured by A13). It is therefore pinned by reading the
/// resolver's source and requiring the guard to be present, and that fact is printed as such rather
/// than dressed up as a behavioural result.</para>
/// </summary>
public sealed class A100BackupFallbackCheck : IAcceptanceCheck
{
    /// <inheritdoc />
    public string Id => "A100";

    /// <inheritdoc />
    public string Title =>
        "Backup works when the saves live only in an alternative save folder - proved, recorded and reported";

    /// <inheritdoc />
    public async Task<CheckResult> RunAsync(AcceptanceContext context, CancellationToken cancellationToken)
    {
        var expected =
            "for a Ren'Py game whose saves exist only in <install>\\game\\saves (PrimarySavePath is null), "
            + "CreateBackupAsync produces a real archive of exactly those saves and records that folder in "
            + "OriginalSavePath; an alternative folder that holds no detected save file is skipped instead of "
            + "being taken because it is first; and when no folder can be used the call still fails, now with "
            + "the accurate reason instead of the fixed sentence '未检测到存档文件'";

        var details = new List<string>();
        var problems = new List<string>();
        var work = AcceptanceWork.Create("a100");
        var service = context.Get<ISaveManagementService>();

        var realAppData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Galbox");
        var realBackupStore = Path.Combine(realAppData, "SaveBackups");
        var realDatabasePath = Path.Combine(realAppData, "galbox.db");
        var realDatabaseBefore = FileStamp(realDatabasePath);
        var realBackupStoreBefore = ListFilesRelative(realBackupStore);

        details.Add($"Scratch folder        : {work}");
        details.Add($"Isolated backup store : {service.BackupStoragePath}");
        details.Add($"  isolated            : {!PathsEqual(service.BackupStoragePath, realBackupStore)}");
        details.Add($"Real backup store     : {realBackupStore} ({realBackupStoreBefore.Count} file(s), must stay untouched)");
        details.Add($"Real app DB           : {realDatabasePath} ({realDatabaseBefore}, must stay untouched)");

        if (PathsEqual(service.BackupStoragePath, realBackupStore))
        {
            details.Add("FAIL REASON: this run would write into the user's real backup store.");
            return CheckResult
                .Fail(Id, Title, expected, "the acceptance backup store is the user's real one")
                .With(details.ToArray());
        }

        var fixtureRoot = Path.Combine(work, "games");
        var stamp = Guid.NewGuid().ToString("N")[..8];
        var createdGames = new List<int>();

        try
        {
            // ======================================================= A100.1 / .2 / .3: the real case
            details.Add(string.Empty);
            details.Add("=== A100.1 saves that live only in <install>\\game\\saves ===");

            var gameAFolder = Path.Combine(fixtureRoot, $"A100-Portable-{stamp}");
            var gameASaves = Path.Combine(gameAFolder, "game", "saves");

            AcceptanceWork.WriteText(Path.Combine(gameAFolder, "renpy", "renpy.py"), "# renpy runtime marker\n");
            AcceptanceWork.WriteText(Path.Combine(gameAFolder, "GameA.exe"), "MZ fake executable - part of the installation\n");
            AcceptanceWork.WriteText(Path.Combine(gameAFolder, "game", "data.bin"), new string('D', 4096));
            AcceptanceWork.WriteText(Path.Combine(gameASaves, "slot1.save"), "A100-SAVE-SLOT-1-CONTENT");
            AcceptanceWork.WriteText(Path.Combine(gameASaves, "slot2.save"), new string('B', 2048));

            details.Add($"  installation folder : {gameAFolder}");
            details.Add($"  save folder         : game\\saves ({Directory.GetFiles(gameASaves).Length} file(s))");
            details.Add($"  installation files  : GameA.exe, game\\data.bin (decoys: they must never enter the archive)");
            details.Add($"  AppData collision   : {DescribeAppDataCollision(gameAFolder)}");

            var gameARow = await InsertGameAsync(
                context,
                $"A100 portable Ren'Py probe {stamp}",
                gameAFolder,
                Path.Combine(gameAFolder, "GameA.exe"),
                cancellationToken).ConfigureAwait(false);

            createdGames.Add(gameARow.Id);

            var gameAStateBefore = Snapshot(gameAFolder);

            var detectionA = await service.DetectSaveLocationAsync(gameARow, cancellationToken).ConfigureAwait(false);

            details.Add($"  --- what the product's own detection reports ---");
            details.Add($"  Success             : {detectionA.Success}");
            details.Add($"  EngineType          : {detectionA.EngineType}");
            details.Add($"  PrimarySavePath     : {detectionA.PrimarySavePath ?? "(null)"}");
            details.Add($"  AlternativePaths    : [{string.Join(" | ", detectionA.AlternativePaths)}]");
            details.Add($"  SaveFiles           : {detectionA.SaveFiles.Count} [{string.Join(", ", detectionA.SaveFiles.Select(Path.GetFileName))}]");

            var fallbackShapeIsReal = detectionA.Success
                                   && string.IsNullOrWhiteSpace(detectionA.PrimarySavePath)
                                   && detectionA.AlternativePaths.Any(p => PathsEqual(p, gameASaves))
                                   && detectionA.SaveFiles.Count == 2;

            if (!fallbackShapeIsReal)
            {
                problems.Add(
                    "A100 fixture A no longer reproduces the reported situation (expected Success=true, "
                    + $"PrimarySavePath=null, the saves folder under AlternativePaths and 2 save files; measured "
                    + $"Success={detectionA.Success}, PrimarySavePath={detectionA.PrimarySavePath ?? "(null)"}, "
                    + $"AlternativePaths=[{string.Join(" | ", detectionA.AlternativePaths)}], "
                    + $"SaveFiles={detectionA.SaveFiles.Count}). The fallback would not be exercised by this run");
            }

            details.Add(string.Empty);
            details.Add("=== A100.2 / A100.3 back it up and look at what was archived and recorded ===");

            var backupA = await service
                .CreateBackupAsync(gameARow, "A100 portable-source backup", null, cancellationToken)
                .ConfigureAwait(false);

            details.Add($"  CreateBackupAsync   : {(backupA is null ? "null (NO archive was produced)" : backupA.BackupPath)}");

            if (backupA is null)
            {
                details.Add($"  LastBackupFailureReason : {service.LastBackupFailureReason ?? "(null)"}");
                problems.Add("A100: CreateBackupAsync produced no archive for a game whose saves were detected "
                    + $"in an alternative folder. Reason reported: {service.LastBackupFailureReason ?? "(none)"}");
            }
            else
            {
                var archiveA = new FileInfo(backupA.BackupPath);
                var entriesA = ReadZipEntries(backupA.BackupPath);
                var expectedA = Snapshot(gameASaves);
                var recordedA = await ReadBackupOriginalPathAsync(context, backupA.Id, cancellationToken).ConfigureAwait(false);

                details.Add($"  file on disk        : exists={archiveA.Exists}, {archiveA.Length} bytes, row SizeBytes={backupA.SizeBytes}");
                details.Add($"  OriginalSavePath    : {backupA.OriginalSavePath ?? "(null)"}");
                details.Add($"    is the folder that holds the saves : {PathsEqual(backupA.OriginalSavePath, gameASaves)}");
                details.Add($"    same value in the database row     : {recordedA ?? "(null)"} -> {PathsEqual(recordedA, gameASaves)}");
                details.Add($"  archive entries ({entriesA.Count}) :");
                foreach (var (name, hash) in entriesA.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
                {
                    details.Add($"    {name,-24} sha256 {hash[..16]}...");
                }

                details.Add("  --- every save file on disk vs its archive entry ---");
                foreach (var (relative, hash) in expectedA)
                {
                    var match = entriesA.TryGetValue(relative.Replace('\\', '/'), out var entryHash) && entryHash == hash;
                    details.Add($"    {relative,-24} {new FileInfo(Path.Combine(gameASaves, relative)).Length,9} bytes  "
                              + $"sha256 {hash[..16]}...  archive {(match ? "MATCHES" : "MISSING/DIFFERS")}");
                    if (!match)
                    {
                        problems.Add($"A100: {relative} is in the save folder but not byte-identical in the archive");
                    }
                }

                if (!archiveA.Exists || archiveA.Length == 0)
                {
                    problems.Add($"A100: the archive the service reported does not exist on disk: {backupA.BackupPath}");
                }

                if (entriesA.Count != expectedA.Count)
                {
                    problems.Add($"A100: the archive holds {entriesA.Count} entries while the save folder holds "
                        + $"{expectedA.Count} file(s): [{string.Join(", ", entriesA.Keys)}]");
                }

                if (entriesA.Count == 0)
                {
                    problems.Add("A100: the archive is empty");
                }

                // The installation's own files must not be in the archive: this is the difference
                // between "backed up the saves" and "backed up the game".
                foreach (var forbidden in new[] { "GameA.exe", "data.bin", "renpy.py" })
                {
                    var hit = entriesA.Keys.FirstOrDefault(name =>
                        string.Equals(Path.GetFileName(name), forbidden, StringComparison.OrdinalIgnoreCase));

                    details.Add($"    installation file '{forbidden}' in the archive : {hit ?? "no"}");
                    if (hit is not null)
                    {
                        problems.Add($"A100: the archive contains installation file '{hit}' - the backup is not "
                            + "limited to the save folder");
                    }
                }

                if (!PathsEqual(backupA.OriginalSavePath, gameASaves))
                {
                    problems.Add($"A100: OriginalSavePath is '{backupA.OriginalSavePath ?? "(null)"}' instead of the "
                        + $"folder the archive was taken from ('{gameASaves}')");
                }

                if (!PathsEqual(recordedA, gameASaves))
                {
                    problems.Add($"A100: the stored row records OriginalSavePath='{recordedA ?? "(null)"}' instead of "
                        + $"'{gameASaves}' (that column is the restore destination)");
                }

                var gameAStateAfter = Snapshot(gameAFolder);
                var gameADiff = Diff(gameAStateBefore, gameAStateAfter);
                details.Add($"  game folder after the backup : {gameADiff.Count} change(s) "
                          + (gameADiff.Count == 0 ? "(untouched, as a backup must leave it)" : string.Join("; ", gameADiff)));
                if (gameADiff.Count > 0)
                {
                    problems.Add("A100: creating a backup modified the game folder: " + string.Join("; ", gameADiff));
                }
            }

            // ============================================ A100.4 the first alternative is not taken on faith
            details.Add(string.Empty);
            details.Add("=== A100.4 an alternative folder that holds no save file is skipped, not taken ===");

            var gameBFolder = Path.Combine(fixtureRoot, $"A100-SkipEmptyFirst-{stamp}");
            var gameBEmptySaves = Path.Combine(gameBFolder, "game", "saves");
            var gameBRealSaves = Path.Combine(gameBFolder, "saves");

            AcceptanceWork.WriteText(Path.Combine(gameBFolder, "renpy", "renpy.py"), "# renpy runtime marker\n");
            AcceptanceWork.WriteText(Path.Combine(gameBFolder, "GameB.exe"), "MZ fake executable - part of the installation\n");
            Directory.CreateDirectory(gameBEmptySaves); // listed by the detector, but holds nothing
            AcceptanceWork.WriteText(Path.Combine(gameBRealSaves, "slotA.save"), "A100-SAVE-SLOT-A-CONTENT");
            AcceptanceWork.WriteText(Path.Combine(gameBRealSaves, "persistent"), "A100-PERSISTENT-CONTENT");

            details.Add($"  installation folder : {gameBFolder}");
            details.Add($"  alternative[0]      : game\\saves  (exists, EMPTY - must not be chosen)");
            details.Add($"  alternative[1]      : saves\\      (holds slotA.save + persistent - the real saves)");
            details.Add($"  AppData collision   : {DescribeAppDataCollision(gameBFolder)}");

            var gameBRow = await InsertGameAsync(
                context,
                $"A100 skip-empty probe {stamp}",
                gameBFolder,
                Path.Combine(gameBFolder, "GameB.exe"),
                cancellationToken).ConfigureAwait(false);

            createdGames.Add(gameBRow.Id);

            var detectionB = await service.DetectSaveLocationAsync(gameBRow, cancellationToken).ConfigureAwait(false);

            details.Add($"  detection           : Success={detectionB.Success}, PrimarySavePath={detectionB.PrimarySavePath ?? "(null)"}");
            details.Add($"  alternatives ({detectionB.AlternativePaths.Count})   : ");
            foreach (var alternative in detectionB.AlternativePaths)
            {
                details.Add($"      {alternative}  (files: {AcceptanceWork.ListFilesRelative(alternative).Count})");
            }

            details.Add($"  SaveFiles           : {detectionB.SaveFiles.Count} [{string.Join(", ", detectionB.SaveFiles.Select(Path.GetFileName))}]");

            var emptyIsFirst = detectionB.AlternativePaths.Count >= 2
                            && PathsEqual(detectionB.AlternativePaths[0], gameBEmptySaves);

            if (!emptyIsFirst)
            {
                problems.Add("A100 fixture B no longer puts the empty folder first (the detector's order changed), so "
                    + "this run cannot show that a folder without save files is skipped: ["
                    + string.Join(" | ", detectionB.AlternativePaths) + "]");
            }

            var backupB = await service
                .CreateBackupAsync(gameBRow, "A100 skip-empty backup", null, cancellationToken)
                .ConfigureAwait(false);

            details.Add($"  CreateBackupAsync   : {(backupB is null ? "null (NO archive was produced)" : backupB.BackupPath)}");

            if (backupB is null)
            {
                details.Add($"  LastBackupFailureReason : {service.LastBackupFailureReason ?? "(null)"}");
                problems.Add("A100: the backup failed although a later alternative folder really holds the saves. "
                    + $"Reason reported: {service.LastBackupFailureReason ?? "(none)"}");
            }
            else
            {
                var entriesB = ReadZipEntries(backupB.BackupPath);
                var expectedB = Snapshot(gameBRealSaves);

                details.Add($"  OriginalSavePath    : {backupB.OriginalSavePath ?? "(null)"}");
                details.Add($"    is the folder that holds the saves (saves\\) : {PathsEqual(backupB.OriginalSavePath, gameBRealSaves)}");
                details.Add($"    is the empty first alternative              : {PathsEqual(backupB.OriginalSavePath, gameBEmptySaves)}");
                details.Add($"  archive entries     : [{string.Join(", ", entriesB.Keys)}]");

                foreach (var (relative, hash) in expectedB)
                {
                    var match = entriesB.TryGetValue(relative.Replace('\\', '/'), out var entryHash) && entryHash == hash;
                    details.Add($"    {relative,-18} sha256 {hash[..16]}...  archive {(match ? "MATCHES" : "MISSING/DIFFERS")}");
                    if (!match)
                    {
                        problems.Add($"A100: {relative} is in the save folder but not byte-identical in the archive");
                    }
                }

                if (!PathsEqual(backupB.OriginalSavePath, gameBRealSaves))
                {
                    problems.Add($"A100: the backup used '{backupB.OriginalSavePath ?? "(null)"}' although only "
                        + $"'{gameBRealSaves}' holds detected save files (the empty folder must be skipped, the "
                        + "first entry of the list must not be taken on faith)");
                }

                if (entriesB.Count != expectedB.Count)
                {
                    problems.Add($"A100: the second scenario's archive holds {entriesB.Count} entries for "
                        + $"{expectedB.Count} file(s) on disk: [{string.Join(", ", entriesB.Keys)}]");
                }

                if (entriesB.Keys.Any(name => string.Equals(Path.GetFileName(name), "GameB.exe", StringComparison.OrdinalIgnoreCase)))
                {
                    problems.Add("A100: the second scenario's archive contains the installation's GameB.exe");
                }
            }

            // =============================================== A100.5 nothing usable: fail, and say why
            details.Add(string.Empty);
            details.Add("=== A100.5 nothing usable: the call still fails, with the accurate reason ===");

            var gameCFolder = Path.Combine(fixtureRoot, $"A100-InstallRootOnly-{stamp}");
            AcceptanceWork.WriteText(Path.Combine(gameCFolder, "Game.exe"), "MZ fake executable - part of the installation\n");
            AcceptanceWork.WriteText(Path.Combine(gameCFolder, "Save01.rvdata2"), "A100-ROOT-LEVEL-SAVE");
            AcceptanceWork.WriteText(Path.Combine(gameCFolder, "game-data.bin"), new string('X', 8192));

            var gameCRow = await InsertGameAsync(
                context,
                $"A100 install-root-only probe {stamp}",
                gameCFolder,
                Path.Combine(gameCFolder, "Game.exe"),
                cancellationToken).ConfigureAwait(false);

            createdGames.Add(gameCRow.Id);

            var detectionC = await service.DetectSaveLocationAsync(gameCRow, cancellationToken).ConfigureAwait(false);

            details.Add($"  installation folder : {gameCFolder}");
            details.Add($"  layout              : Save01.rvdata2 + game-data.bin + Game.exe, all directly in the installation root");
            details.Add($"  detection           : Success={detectionC.Success}, PrimarySavePath={detectionC.PrimarySavePath ?? "(null)"}, "
                      + $"SaveFiles={detectionC.SaveFiles.Count}");
            details.Add($"  detector message    : {detectionC.ErrorMessage ?? "(null)"}");

            var zipsBefore = ListZips(service.BackupStoragePath);

            var backupC = await service
                .CreateBackupAsync(gameCRow, "A100 install-root probe", null, cancellationToken)
                .ConfigureAwait(false);

            var zipsAfter = ListZips(service.BackupStoragePath);
            var newZips = zipsAfter.Except(zipsBefore, StringComparer.OrdinalIgnoreCase).ToList();
            var reasonC = service.LastBackupFailureReason;

            details.Add($"  CreateBackupAsync   : {(backupC is null ? "null (no archive - correct)" : backupC.BackupPath)}");
            details.Add($"  LastBackupFailureReason : {reasonC ?? "(null)"}");
            details.Add($"    names the installation folder      : {reasonC is not null && reasonC.Contains(gameCFolder, StringComparison.OrdinalIgnoreCase)}");
            details.Add($"    is the old fixed sentence          : {reasonC == "未检测到存档文件。"}");
            details.Add($"  archives written    : {newZips.Count} {(newZips.Count == 0 ? "(none - correct)" : string.Join(", ", newZips))}");

            if (backupC is not null)
            {
                problems.Add($"A100: a backup was produced for a game whose save files live in the installation root "
                    + $"('{backupC.BackupPath}') - the installation must never be treated as a save folder");
            }

            if (newZips.Count > 0)
            {
                problems.Add($"A100: {newZips.Count} archive(s) were written for the refused game: {string.Join(", ", newZips)}");
            }

            if (string.IsNullOrWhiteSpace(reasonC))
            {
                problems.Add("A100: the failed backup left no reason, so the page can only repeat the fixed sentence");
            }
            else if (!reasonC.Contains(gameCFolder, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"A100: the failure reason does not name the folder that was refused: '{reasonC}'");
            }

            // ================================================== wiring: the guards are present in source
            details.Add(string.Empty);
            details.Add("=== guard wiring (source-level: the detector cannot emit an unsafe alternative) ===");

            var repoRoot = RepoLocator.FindRepoRoot();
            var serviceSourcePath = repoRoot is null
                ? null
                : Path.Combine(RepoLocator.AppProject(repoRoot), "Services", "SaveManagementService.cs");

            if (serviceSourcePath is null || !File.Exists(serviceSourcePath))
            {
                details.Add($"  [FAIL] {serviceSourcePath ?? "(repo root not found)"} does not exist");
                problems.Add("A100: SaveManagementService.cs could not be read to check the fallback guards");
            }
            else
            {
                var source = File.ReadAllText(serviceSourcePath);

                var guards = new (string What, string Needle)[]
                {
                    ("the fallback asks whether a candidate is the game installation folder",
                        "EngineSaveDetector.IsGameInstallRoot(candidatePath, game)"),
                    ("a candidate must be a folder that contains the installation to be refused",
                        "IsPathInside(game.InstallPath, candidatePath)"),
                    ("a candidate must hold a save file the detector recorded",
                        "saveLocation.SaveFiles.Count(file => IsPathInside(file, candidate))"),
                    ("a candidate's recorded save files must still be on disk",
                        "IsPathInside(file, candidate) && File.Exists(file)"),
                    ("the folder that was used is recorded on the backup row",
                        "OriginalSavePath = sourceRoot")
                };

                foreach (var (what, needle) in guards)
                {
                    var present = source.Contains(needle, StringComparison.Ordinal);
                    details.Add($"  [{(present ? "OK  " : "FAIL")}] {what}");
                    if (!present)
                    {
                        problems.Add($"A100: the fallback guard is missing from SaveManagementService.cs: {what}");
                    }
                }
            }

            // ============================================================= data safety: the real world
            details.Add(string.Empty);
            details.Add("=== the user's real data ===");

            var realDatabaseAfter = FileStamp(realDatabasePath);
            var realBackupStoreAfter = ListFilesRelative(realBackupStore);
            var realBackupStoreDiff = DiffLists(realBackupStoreBefore, realBackupStoreAfter);

            details.Add($"  real galbox.db   : {realDatabaseBefore} -> {realDatabaseAfter}");
            details.Add($"  real SaveBackups : {realBackupStoreBefore.Count} file(s) -> {realBackupStoreAfter.Count} file(s), "
                      + $"{(realBackupStoreDiff.Count == 0 ? "UNCHANGED" : "CHANGED: " + string.Join("; ", realBackupStoreDiff))}");

            if (realDatabaseBefore != realDatabaseAfter)
            {
                problems.Add($"A100: the user's real database changed ({realDatabaseBefore} -> {realDatabaseAfter})");
            }

            if (realBackupStoreDiff.Count > 0)
            {
                problems.Add("A100: the user's real backup store changed: " + string.Join("; ", realBackupStoreDiff));
            }
        }
        finally
        {
            await CleanupAsync(context, createdGames, details, cancellationToken).ConfigureAwait(false);
        }

        // --------------------------------------------------------------------------------- verdict
        details.Add(string.Empty);
        details.Add("--- interpretation ---");
        details.Add($"  fallback produces an archive for the reported shape : {problems.Count == 0}");
        details.Add($"  assertion failures                                  : {problems.Count}");

        foreach (var problem in problems)
        {
            details.Add($"  - {problem}");
        }

        var actual = problems.Count == 0
            ? "a Ren'Py game whose saves live only in <install>\\game\\saves gets a real archive of exactly those "
            + "saves, the folder used is recorded in OriginalSavePath (returned and persisted), an empty first "
            + "alternative is skipped in favour of the folder that really holds them, and a game whose saves live "
            + "in the installation root still fails - with the accurate reason in LastBackupFailureReason"
            : $"FAIL: {problems[0]}";

        return problems.Count == 0
            ? CheckResult.Pass(Id, Title, expected, actual).With(details.ToArray())
            : CheckResult.Fail(Id, Title, expected, actual).With(details.ToArray());
    }

    // ---------------------------------------------------------------------------- local helpers

    /// <summary>Inserts the game row the service needs, and gives it back with its real Id.</summary>
    private static async Task<GameInfo> InsertGameAsync(
        AcceptanceContext context,
        string name,
        string installPath,
        string executable,
        CancellationToken cancellationToken)
    {
        await using var scope = context.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GalboxDbContext>();

        var row = new GameInfo
        {
            NameOriginal = name,
            InstallPath = installPath,
            MainExecutable = executable,
            AddedTime = DateTime.UtcNow,
            UpdatedTime = DateTime.UtcNow
        };

        db.Games.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return row;
    }

    /// <summary>Reads the stored <c>OriginalSavePath</c> of a backup row.</summary>
    private static async Task<string?> ReadBackupOriginalPathAsync(
        AcceptanceContext context,
        int backupId,
        CancellationToken cancellationToken)
    {
        await using var scope = context.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GalboxDbContext>();

        return await db.SaveBackups
            .AsNoTracking()
            .Where(row => row.Id == backupId)
            .Select(row => row.OriginalSavePath)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Removes this check's rows; the fixtures live in the run's own scratch folder.</summary>
    private static async Task CleanupAsync(
        AcceptanceContext context,
        List<int> gameIds,
        List<string> details,
        CancellationToken cancellationToken)
    {
        if (gameIds.Count == 0)
        {
            return;
        }

        try
        {
            await using var scope = context.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<GalboxDbContext>();

            var archives = await db.SaveBackups
                .Where(row => gameIds.Contains(row.GameInfoId))
                .Select(row => row.BackupPath)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var archive in archives)
            {
                try
                {
                    if (File.Exists(archive))
                    {
                        File.Delete(archive);
                    }
                }
                catch
                {
                    // best effort
                }
            }

            await db.SaveBackups.Where(row => gameIds.Contains(row.GameInfoId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await db.Games.Where(row => gameIds.Contains(row.Id)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            details.Add($"  (cleanup: {gameIds.Count} probe game row(s) and their backup rows removed)");
        }
        catch (Exception ex)
        {
            details.Add($"  Cleanup warning: {ex.Message}");
        }
    }

    /// <summary>Reads a zip into <c>entry name -&gt; SHA-256</c>.</summary>
    private static Dictionary<string, string> ReadZipEntries(string archivePath)
    {
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var extractionRoot = Path.Combine(Path.GetTempPath(), $"galbox-a100-zip-{Guid.NewGuid():N}");
        Directory.CreateDirectory(extractionRoot);

        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name))
                {
                    continue; // directory entry
                }

                var target = Path.Combine(extractionRoot, Guid.NewGuid().ToString("N"));
                entry.ExtractToFile(target, overwrite: true);

                using var stream = File.OpenRead(target);
                hashes[entry.FullName.Replace('\\', '/')] = Convert.ToHexString(SHA256.HashData(stream));
            }
        }
        finally
        {
            try
            {
                Directory.Delete(extractionRoot, recursive: true);
            }
            catch
            {
                // best effort
            }
        }

        return hashes;
    }

    /// <summary>Maps every file under <paramref name="directory"/>, relative, to its SHA-256.</summary>
    private static Dictionary<string, string> Snapshot(string directory)
    {
        var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(directory))
        {
            return snapshot;
        }

        foreach (var path in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
        {
            snapshot[Path.GetRelativePath(directory, path)] = AcceptanceWork.HashFile(path);
        }

        return snapshot;
    }

    /// <summary>Reports which files differ between two snapshots.</summary>
    private static List<string> Diff(Dictionary<string, string> before, Dictionary<string, string> after)
    {
        var differences = new List<string>();

        foreach (var (name, hash) in before)
        {
            if (!after.TryGetValue(name, out var now))
            {
                differences.Add($"{name}: deleted");
            }
            else if (now != hash)
            {
                differences.Add($"{name}: content changed");
            }
        }

        foreach (var name in after.Keys.Where(name => !before.ContainsKey(name)))
        {
            differences.Add($"{name}: created");
        }

        return differences;
    }

    private static List<string> ListFilesRelative(string directory) => AcceptanceWork.ListFilesRelative(directory);

    /// <summary>Reports which file names were added or removed between two folder listings.</summary>
    private static List<string> DiffLists(List<string> before, List<string> after)
    {
        var differences = new List<string>();

        differences.AddRange(before.Except(after, StringComparer.OrdinalIgnoreCase).Select(name => $"{name}: removed"));
        differences.AddRange(after.Except(before, StringComparer.OrdinalIgnoreCase).Select(name => $"{name}: added"));

        return differences;
    }

    /// <summary>Every backup archive currently under the run's backup store, relative to it.</summary>
    private static List<string> ListZips(string store)
        => ListFilesRelative(store).Where(name => name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>A cheap identity for the user's database (existence + size + write time).</summary>
    private static string FileStamp(string path)
    {
        if (!File.Exists(path))
        {
            return "(absent)";
        }

        var info = new FileInfo(path);
        return $"{info.Length} bytes, written {info.LastWriteTimeUtc:u}";
    }

    /// <summary>
    /// Reports whether a folder under <c>%APPDATA%</c> could shadow the fixture: the Ren'Py branch
    /// of the detector matches <c>%APPDATA%\&lt;install folder name&gt;*</c>, so a fixture name that
    /// has no such folder guarantees the alternative paths under test are the fixture's own.
    /// </summary>
    private static string DescribeAppDataCollision(string installFolder)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var folderName = Path.GetFileName(installFolder.TrimEnd(Path.DirectorySeparatorChar));

        if (!Directory.Exists(appData))
        {
            return "none (no %APPDATA%)";
        }

        try
        {
            var matches = Directory
                .GetDirectories(appData, folderName + "*")
                .Select(Path.GetFileName)
                .ToList();

            return matches.Count == 0
                ? "none (no %APPDATA% folder matches this fixture's name)"
                : string.Join(", ", matches);
        }
        catch (Exception ex)
        {
            return $"could not be checked: {ex.GetType().Name}";
        }
    }

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        static string Normalize(string path)
            => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
    }
}
