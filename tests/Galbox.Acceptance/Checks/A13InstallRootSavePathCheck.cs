using Galbox.App.Services;
using Galbox.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Galbox.Acceptance.Checks;

/// <summary>
/// A13 - The game installation folder must never be treated as a save folder (W20).
///
/// The defect: the RPG Maker and KiriKiri branches returned <c>game.InstallPath</c> itself whenever
/// they found save files directly inside the game folder. Every consumer of
/// <c>SaveLocationResult.PrimarySavePath</c> then worked on the whole installation: a backup zipped
/// the entire game (gigabytes), and a restore/rollback first DELETED every file under that path
/// before copying the archive back - which can destroy the installation permanently.
///
/// The check drives the three cases that matter:
///   1. RPG Maker saves in the installation root   -> must be refused, with a reason
///   2. KiriKiri saves in the installation root    -> must be refused, with a reason
///   3. saves in a proper subfolder                -> must still be accepted (no over-correction)
/// It also proves the refusal reaches the backup path: <c>CreateBackupAsync</c> must not produce a
/// backup whose archive contains the game's own files.
/// </summary>
public sealed class A13InstallRootSavePathCheck : IAcceptanceCheck
{
    /// <inheritdoc />
    public string Id => "A13";

    /// <inheritdoc />
    public string Title => "Save detection refuses the game installation folder as a save folder";

    /// <inheritdoc />
    public async Task<CheckResult> RunAsync(AcceptanceContext context, CancellationToken cancellationToken)
    {
        var expected = "for save files living directly in the installation root, PrimarySavePath is rejected with "
                     + "Success=false and an explanatory message, CreateBackupAsync produces no archive of the game, "
                     + "while a save file in a real subfolder is still accepted";

        var details = new List<string>();
        var work = AcceptanceWork.Create("a13");

        // ---------------------------------------------------------------- case 1: RPG Maker VX Ace
        var rpgRoot = Path.Combine(work, "RpgMakerGame");
        AcceptanceWork.WritePattern(Path.Combine(rpgRoot, "Save01.rvdata2"), 512);
        AcceptanceWork.WritePattern(Path.Combine(rpgRoot, "game-data.bin"), 256 * 1024, 0x37);
        AcceptanceWork.WritePattern(Path.Combine(rpgRoot, "Game.exe"), 256, 0x4D);

        var rpgGame = new GameInfo
        {
            NameOriginal = "A13 RPG Maker probe",
            InstallPath = rpgRoot,
            MainExecutable = Path.Combine(rpgRoot, "Game.exe")
        };

        var rpgDetectedEngine = EngineSaveDetector.DetectEngineType(rpgGame);
        var rpgResult = EngineSaveDetector.DetectSaveLocation(rpgGame, GameEngineType.RpgMaker);

        details.Add("=== case 1: RPG Maker VX Ace, saves (*.rvdata2) directly in the installation root ===");
        details.Add($"  installation folder  : {rpgRoot}");
        details.Add($"  root-level files     : Save01.rvdata2, game-data.bin (250 KB decoy), Game.exe");
        details.Add($"  DetectEngineType     : {rpgDetectedEngine} (expected RpgMaker)");
        details.Add($"  PrimarySavePath      : {rpgResult.PrimarySavePath ?? "(null - refused)"}");
        details.Add($"  Success              : {rpgResult.Success}");
        details.Add($"  SaveFiles            : {rpgResult.SaveFiles.Count}");
        details.Add($"  ErrorMessage         : {rpgResult.ErrorMessage ?? "(null)"}");
        details.Add($"  ExtendedInfo         : {string.Join(", ", rpgResult.ExtendedInfo.Select(kv => $"{kv.Key}={kv.Value}"))}");

        var rpgRefused = !rpgResult.Success
                      && string.IsNullOrEmpty(rpgResult.PrimarySavePath)
                      && !string.IsNullOrWhiteSpace(rpgResult.ErrorMessage);

        // ---------------------------------------------------------------- case 2: KiriKiri
        var krkrRoot = Path.Combine(work, "KrkrGame");
        AcceptanceWork.WritePattern(Path.Combine(krkrRoot, "data.xp3"), 1024, 0x58);
        AcceptanceWork.WritePattern(Path.Combine(krkrRoot, "savedata.ksd"), 384, 0x4B);

        var krkrGame = new GameInfo
        {
            NameOriginal = "A13 KiriKiri probe",
            InstallPath = krkrRoot,
            MainExecutable = Path.Combine(krkrRoot, "krkr.exe")
        };

        var krkrDetectedEngine = EngineSaveDetector.DetectEngineType(krkrGame);
        var krkrResult = EngineSaveDetector.DetectSaveLocation(krkrGame, GameEngineType.Krkr);

        details.Add(string.Empty);
        details.Add("=== case 2: KiriKiri, saves (*.ksd) directly in the installation root ===");
        details.Add($"  installation folder  : {krkrRoot}");
        details.Add($"  DetectEngineType     : {krkrDetectedEngine} (expected Krkr)");
        details.Add($"  PrimarySavePath      : {krkrResult.PrimarySavePath ?? "(null - refused)"}");
        details.Add($"  Success              : {krkrResult.Success}");
        details.Add($"  SaveFiles            : {krkrResult.SaveFiles.Count}");
        details.Add($"  ErrorMessage         : {krkrResult.ErrorMessage ?? "(null)"}");
        details.Add($"  ExtendedInfo         : {string.Join(", ", krkrResult.ExtendedInfo.Select(kv => $"{kv.Key}={kv.Value}"))}");

        var krkrRefused = !krkrResult.Success
                       && string.IsNullOrEmpty(krkrResult.PrimarySavePath)
                       && !string.IsNullOrWhiteSpace(krkrResult.ErrorMessage);

        // ---------------------------------------------------------------- case 3: control (subfolder)
        var okRoot = Path.Combine(work, "KrkrGameWithSavedataFolder");
        AcceptanceWork.WritePattern(Path.Combine(okRoot, "data.xp3"), 1024, 0x58);
        AcceptanceWork.WritePattern(Path.Combine(okRoot, "savedata", "s1.ksd"), 256, 0x4B);

        var okGame = new GameInfo
        {
            NameOriginal = "A13 control probe",
            InstallPath = okRoot,
            MainExecutable = Path.Combine(okRoot, "krkr.exe")
        };

        var okResult = EngineSaveDetector.DetectSaveLocation(okGame, GameEngineType.Krkr);
        var expectedSavedataPath = Path.Combine(okRoot, "savedata");

        details.Add(string.Empty);
        details.Add("=== case 3: control - the save file lives in a real subfolder and must still be detected ===");
        details.Add($"  installation folder  : {okRoot}");
        details.Add($"  PrimarySavePath      : {okResult.PrimarySavePath ?? "(null)"}");
        details.Add($"  expected             : {expectedSavedataPath}");
        details.Add($"  Success              : {okResult.Success}");
        details.Add($"  SaveFiles            : {okResult.SaveFiles.Count}");
        details.Add($"  ErrorMessage         : {okResult.ErrorMessage ?? "(null)"}");

        var controlAccepted = okResult.Success
                           && string.Equals(okResult.PrimarySavePath, expectedSavedataPath, StringComparison.OrdinalIgnoreCase);

        // ------------------------------------------------- case 4: the refusal reaches the backup path
        int gameId;
        await using (var scope = context.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GalboxDbContext>();
            var row = new GameInfo
            {
                NameOriginal = "A13 RPG Maker probe",
                InstallPath = rpgRoot,
                MainExecutable = Path.Combine(rpgRoot, "Game.exe"),
                EngineType = GameEngineType.RpgMaker,
                AddedTime = DateTime.UtcNow,
                UpdatedTime = DateTime.UtcNow
            };
            db.Games.Add(row);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            gameId = row.Id;
        }

        var service = context.Get<ISaveManagementService>();
        var backup = await service
            .CreateBackupAsync(new GameInfo { Id = gameId, NameOriginal = "A13 RPG Maker probe", InstallPath = rpgRoot }, "A13 probe", null, cancellationToken)
            .ConfigureAwait(false);

        var archiveFiles = backup?.BackupPath is { } zipPath && File.Exists(zipPath)
            ? AcceptanceWork.ZipEntryNames(zipPath)
            : new List<string>();

        details.Add(string.Empty);
        details.Add("=== case 4: CreateBackupAsync for that game ===");
        details.Add($"  backup result        : {(backup is null ? "null (no backup was produced)" : backup.BackupPath)}");
        details.Add($"  archive entries      : [{string.Join(", ", archiveFiles)}]");
        details.Add($"  game files would be in the archive: {archiveFiles.Any(name => name.Contains("game-data.bin", StringComparison.OrdinalIgnoreCase))}");

        var backupRefused = backup is null || !archiveFiles.Any(name => name.Contains("game-data.bin", StringComparison.OrdinalIgnoreCase));

        // Also prove the service-level detection (not only the static detector) refuses it.
        var serviceDetection = await service
            .DetectSaveLocationAsync(new GameInfo { Id = gameId, NameOriginal = "A13 RPG Maker probe", InstallPath = rpgRoot }, cancellationToken)
            .ConfigureAwait(false);

        details.Add($"  service DetectSaveLocationAsync: Success={serviceDetection.Success}, PrimarySavePath={serviceDetection.PrimarySavePath ?? "(null)"}");
        details.Add($"    engine type        : {serviceDetection.EngineType}");
        details.Add($"    error message      : {serviceDetection.ErrorMessage ?? "(null)"}");

        // NOTE: deliberately no reference to the new helper API (IsGameInstallRoot) anywhere in this
        // check: the harness must still compile against the revision that does not contain it.
        var serviceRefused = !serviceDetection.Success
                          && string.IsNullOrEmpty(serviceDetection.PrimarySavePath);

        if (backup is not null && File.Exists(backup.BackupPath))
        {
            try
            {
                File.Delete(backup.BackupPath);
            }
            catch
            {
                // best effort
            }

            await using var scope = context.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<GalboxDbContext>();
            var id = backup.Id;
            await db.SaveBackups.Where(b => b.Id == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await using var scope = context.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<GalboxDbContext>();
            await db.SaveBackups.Where(b => b.GameInfoId == gameId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await db.Games.Where(g => g.Id == gameId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            details.Add($"Cleanup warning: {ex.Message}");
        }

        var pass = rpgRefused && krkrRefused && controlAccepted && backupRefused && serviceRefused;

        details.Add(string.Empty);
        details.Add("--- interpretation ---");
        details.Add($"  RPG Maker root saves refused : {rpgRefused}");
        details.Add($"  KiriKiri root saves refused  : {krkrRefused}");
        details.Add($"  subfolder saves still accepted: {controlAccepted}");
        details.Add($"  CreateBackupAsync refuses    : {backupRefused}");
        details.Add($"  service-level refusal        : {serviceRefused}");

        var actual = $"rpgRefused={rpgRefused}, krkrRefused={krkrRefused}, controlAccepted={controlAccepted}, "
                   + $"backupRefused={backupRefused}, serviceRefused={serviceRefused}";

        return (pass
                ? CheckResult.Pass(Id, Title, expected, actual)
                : CheckResult.Fail(Id, Title, expected, actual))
            .With(details.ToArray());
    }
}
