using System.IO.Compression;
using Galbox.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Galbox.Acceptance.Checks;

/// <summary>
/// A16 - A game can be removed from the library, with its records, and without its files (W13).
///
/// The defect: the whole repository contained no way to remove a game. The only deletion was
/// "remove an entry from the settings list of scan folders". A game added by mistake - or a folder
/// of non-game executables picked up by a scan - was in the library forever.
///
/// The check inserts a game with a row in every child table (all seven) plus a real backup zip on
/// disk, deletes it through the service, and then re-reads the database AND the file system. It also
/// exercises the "also delete the backup files" option, which must stay off by default.
/// </summary>
public sealed class A16DeleteGameCheck : IAcceptanceCheck
{
    /// <inheritdoc />
    public string Id => "A16";

    /// <inheritdoc />
    public string Title => "Deleting a game removes its records and keeps the game files on disk";

    /// <inheritdoc />
    public async Task<CheckResult> RunAsync(AcceptanceContext context, CancellationToken cancellationToken)
    {
        var expected = "after DeleteGameAsync the game row and every related row are gone, the game folder and its "
                     + "executable still exist, backup files survive by default and are removed only when requested, "
                     + "and the UI exposes the delete command with an explicit confirmation";

        var details = new List<string>();
        var repoRoot = RepoLocator.FindRepoRoot();
        var work = AcceptanceWork.Create("a16");

        var service = ReflectionBridge.ResolveService(context.Services, "Galbox.App.Services.IGameDeletionService");
        details.Add("=== IGameDeletionService ===");
        details.Add($"  resolved           : {(service is null ? "NOT REGISTERED" : service.GetType().FullName)}");

        if (service is null)
        {
            details.Add("  FAIL REASON: no deletion service exists, so a game that was added by mistake can never");
            details.Add("               be removed from the library (W13).");
            return CheckResult.Fail(Id, Title, expected, "IGameDeletionService is not registered")
                .With(details.ToArray());
        }

        // ------------------------------------------------------------------ fixture 1: keep the files
        var gameFolder = Path.Combine(work, "ProbeGame");
        var gameExe = AcceptanceWork.WritePattern(Path.Combine(gameFolder, "probe.exe"), 512, 0x4D);
        var saveInGameFolder = AcceptanceWork.WriteText(Path.Combine(gameFolder, "save", "slot1.save"), "player save data");
        var backupZip = AcceptanceWork.CreateZip(
            Path.Combine(work, "backups", "game-backup.zip"),
            ("slot1.save", System.Text.Encoding.UTF8.GetBytes("backup content")));

        int gameId;
        await using (var scope = context.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GalboxDbContext>();
            var game = new GameInfo
            {
                NameOriginal = "A16 delete probe",
                InstallPath = gameFolder,
                MainExecutable = gameExe,
                AddedTime = DateTime.UtcNow,
                UpdatedTime = DateTime.UtcNow
            };
            db.Games.Add(game);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            gameId = game.Id;

            db.Set<GameCharacter>().Add(new GameCharacter { GameInfoId = gameId, Name = "A16 character" });
            db.Set<GameDocument>().Add(new GameDocument { GameInfoId = gameId, FileName = "readme.txt", FilePath = Path.Combine(gameFolder, "readme.txt") });
            db.Set<GameMediaFile>().Add(new GameMediaFile { GameInfoId = gameId, FileName = "a16.mp4", FilePath = Path.Combine(gameFolder, "a16.mp4") });
            db.Set<GameScreenshot>().Add(new GameScreenshot { GameInfoId = gameId, FileName = "shot.png", FilePath = Path.Combine(gameFolder, "shot.png"), CapturedTime = DateTime.UtcNow });
            db.SaveBackups.Add(new GameSaveBackup
            {
                GameInfoId = gameId,
                Name = "A16 backup",
                BackupPath = backupZip,
                OriginalSavePath = Path.Combine(gameFolder, "save"),
                CreatedTime = DateTime.UtcNow,
                SizeBytes = new FileInfo(backupZip).Length
            });
            db.ErrorRecords.Add(new GameErrorRecord
            {
                GameInfoId = gameId,
                Category = "A16",
                Severity = "Minor",
                Title = "A16 error record",
                Description = "created by the acceptance check",
                SolutionType = "Manual",
                DetectedTime = DateTime.UtcNow
            });
            db.Patches.Add(new PatchRecord
            {
                GameInfoId = gameId,
                Name = "A16 patch",
                PatchType = "Translation",
                Status = PatchStatus.Available,
                AddedTime = DateTime.UtcNow
            });

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var scope = context.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GalboxDbContext>();
            var before = await CountRelatedRowsAsync(db, gameId, cancellationToken).ConfigureAwait(false);
            details.Add(string.Empty);
            details.Add("=== fixture 1: game with a row in every child table ===");
            details.Add($"  game Id            : {gameId}");
            details.Add($"  install path       : {gameFolder} (exists: {Directory.Exists(gameFolder)})");
            details.Add($"  main executable    : {gameExe} (exists: {File.Exists(gameExe)})");
            details.Add($"  save in game folder: {saveInGameFolder} (exists: {File.Exists(saveInGameFolder)})");
            details.Add($"  backup zip         : {backupZip} (exists: {File.Exists(backupZip)})");
            details.Add($"  rows BEFORE        : {string.Join(", ", before.Select(kv => $"{kv.Key}={kv.Value}"))}");
        }

        // ---------------------------------------------------------------- delete (default options)
        var (deleteOk, deleteResult, deleteError) = await ReflectionBridge
            .CallAsync(service, "DeleteGameAsync", gameId, null, cancellationToken)
            .ConfigureAwait(false);

        details.Add(string.Empty);
        details.Add("--- DeleteGameAsync(gameId, options: null) ---");
        details.Add($"  call ok            : {deleteOk}{(deleteError is null ? string.Empty : $" ({deleteError})")}");
        details.Add($"  Success            : {ReflectionBridge.Bool(deleteResult, "Success")}");
        details.Add($"  GameName           : {ReflectionBridge.String(deleteResult, "GameName")}");
        details.Add($"  GameFilesKept      : {ReflectionBridge.Bool(deleteResult, "GameFilesKept")}");
        details.Add($"  CharactersDeleted  : {ReflectionBridge.Int(deleteResult, "CharactersDeleted")}");
        details.Add($"  DocumentsDeleted   : {ReflectionBridge.Int(deleteResult, "DocumentsDeleted")}");
        details.Add($"  MediaFilesDeleted  : {ReflectionBridge.Int(deleteResult, "MediaFilesDeleted")}");
        details.Add($"  ScreenshotsDeleted : {ReflectionBridge.Int(deleteResult, "ScreenshotsDeleted")}");
        details.Add($"  BackupsDeleted     : {ReflectionBridge.Int(deleteResult, "BackupsDeleted")}");
        details.Add($"  ErrorRecordsDeleted: {ReflectionBridge.Int(deleteResult, "ErrorRecordsDeleted")}");
        details.Add($"  PatchesDeleted     : {ReflectionBridge.Int(deleteResult, "PatchesDeleted")}");
        details.Add($"  RelatedRowsDeleted : {ReflectionBridge.Int(deleteResult, "RelatedRowsDeleted")}");
        details.Add($"  BackupFilesDeleted : {ReflectionBridge.Int(deleteResult, "BackupFilesDeleted")}");
        details.Add($"  ErrorMessage       : {ReflectionBridge.String(deleteResult, "ErrorMessage") ?? "(null)"}");

        var afterRows = new Dictionary<string, int>();
        var gameStillThere = false;
        await using (var scope = context.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GalboxDbContext>();
            gameStillThere = await db.Games.AnyAsync(g => g.Id == gameId, cancellationToken).ConfigureAwait(false);
            afterRows = await CountRelatedRowsAsync(db, gameId, cancellationToken).ConfigureAwait(false);
        }

        details.Add(string.Empty);
        details.Add("--- database after the deletion ---");
        details.Add($"  game row exists    : {gameStillThere} (expected False)");
        details.Add($"  related rows       : {string.Join(", ", afterRows.Select(kv => $"{kv.Key}={kv.Value}"))}");

        details.Add("--- file system after the deletion ---");
        details.Add($"  game folder exists : {Directory.Exists(gameFolder)} (expected True)");
        details.Add($"  probe.exe exists   : {File.Exists(gameExe)} (expected True)");
        details.Add($"  in-game save exists: {File.Exists(saveInGameFolder)} (expected True)");
        details.Add($"  backup zip exists  : {File.Exists(backupZip)} (expected True: option defaults to keeping it)");

        var rowsGone = !gameStillThere && afterRows.Values.All(count => count == 0);
        var filesKept = Directory.Exists(gameFolder) && File.Exists(gameExe) && File.Exists(saveInGameFolder);
        var backupKeptByDefault = File.Exists(backupZip);

        // -------------------------------------------------- fixture 2: explicitly delete backup files
        var gameFolder2 = Path.Combine(work, "ProbeGame2");
        AcceptanceWork.WritePattern(Path.Combine(gameFolder2, "probe.exe"), 256, 0x4D);
        var backupZip2 = AcceptanceWork.CreateZip(
            Path.Combine(work, "backups", "game2-backup.zip"),
            ("slot1.save", System.Text.Encoding.UTF8.GetBytes("backup content 2")));

        int gameId2;
        await using (var scope = context.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GalboxDbContext>();
            var game = new GameInfo
            {
                NameOriginal = "A16 delete probe 2",
                InstallPath = gameFolder2,
                MainExecutable = Path.Combine(gameFolder2, "probe.exe"),
                AddedTime = DateTime.UtcNow,
                UpdatedTime = DateTime.UtcNow
            };
            db.Games.Add(game);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            gameId2 = game.Id;

            db.SaveBackups.Add(new GameSaveBackup
            {
                GameInfoId = gameId2,
                Name = "A16 backup 2",
                BackupPath = backupZip2,
                OriginalSavePath = Path.Combine(gameFolder2, "save"),
                CreatedTime = DateTime.UtcNow,
                SizeBytes = new FileInfo(backupZip2).Length
            });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        object? options = null;
        var optionsType = ReflectionBridge.FindType("Galbox.App.Services.GameDeletionOptions");
        if (optionsType is not null)
        {
            options = Activator.CreateInstance(optionsType);
            optionsType.GetProperty("DeleteBackupFiles")?.SetValue(options, true);
        }

        var (delete2Ok, delete2Result, delete2Error) = await ReflectionBridge
            .CallAsync(service, "DeleteGameAsync", gameId2, options, cancellationToken)
            .ConfigureAwait(false);

        details.Add(string.Empty);
        details.Add("=== fixture 2: DeleteBackupFiles = true ===");
        details.Add($"  call ok            : {delete2Ok}{(delete2Error is null ? string.Empty : $" ({delete2Error})")}");
        details.Add($"  Success            : {ReflectionBridge.Bool(delete2Result, "Success")}");
        details.Add($"  BackupFilesDeleted : {ReflectionBridge.Int(delete2Result, "BackupFilesDeleted")} (expected 1)");
        details.Add($"  backup zip exists  : {File.Exists(backupZip2)} (expected False)");
        details.Add($"  game folder exists : {Directory.Exists(gameFolder2)} (expected True)");

        var explicitBackupDeletion = File.Exists(backupZip2) == false && Directory.Exists(gameFolder2);

        // ------------------------------------------------------------ UI wiring (source level)
        var uiEvidence = new List<string>();
        var uiOk = true;

        if (repoRoot is not null)
        {
            var libraryXaml = Path.Combine(repoRoot, "src", "Galbox.App", "Views", "LibraryPage.xaml");
            var libraryCode = Path.Combine(repoRoot, "src", "Galbox.App", "Views", "LibraryPage.xaml.cs");
            var libraryVm = Path.Combine(repoRoot, "src", "Galbox.App", "ViewModels", "LibraryViewModel.cs");

            uiOk &= RequireText(libraryXaml, "OnDeleteGameClick", "LibraryPage.xaml: delete button click handler", uiEvidence);
            uiOk &= RequireText(libraryXaml, "DeleteGameOverlayButton", "LibraryPage.xaml: grid-card delete button", uiEvidence);
            uiOk &= RequireText(libraryXaml, "TableDeleteGameButton", "LibraryPage.xaml: table-row delete button", uiEvidence);
            uiOk &= RequireText(libraryCode, "ContentDialog", "LibraryPage.xaml.cs: confirmation dialog", uiEvidence);
            uiOk &= RequireText(libraryCode, "不会删除游戏文件夹", "LibraryPage.xaml.cs: dialog states game files are not deleted", uiEvidence);
            uiOk &= RequireText(libraryCode, "CheckBox", "LibraryPage.xaml.cs: optional backup-file deletion", uiEvidence);
            uiOk &= RequireText(libraryCode, "DeleteGameCommand.ExecuteAsync", "LibraryPage.xaml.cs: invokes the delete command", uiEvidence);
            // The generated command property lives in the CommunityToolkit-generated partial class, so
            // the source file is inspected for the annotated method instead.
            uiOk &= RequireText(libraryVm, "DeleteGameAsync", "LibraryViewModel: delete method", uiEvidence);
            uiOk &= RequireText(libraryVm, "RelayCommand", "LibraryViewModel: exposed as a command", uiEvidence);
        }
        else
        {
            uiOk = false;
            uiEvidence.Add("  repository root not found: UI wiring could not be inspected");
        }

        details.Add(string.Empty);
        details.Add("=== UI wiring (source level) ===");
        details.AddRange(uiEvidence);

        // -------------------------------------------------------------------------- cleanup
        try
        {
            await using var scope = context.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<GalboxDbContext>();
            await db.Games.Where(g => g.Id == gameId || g.Id == gameId2).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            details.Add($"Cleanup warning: {ex.Message}");
        }

        var pass = ReflectionBridge.Bool(deleteResult, "Success")
                && ReflectionBridge.Bool(deleteResult, "GameFilesKept")
                && rowsGone
                && filesKept
                && backupKeptByDefault
                && explicitBackupDeletion
                && uiOk;

        details.Add(string.Empty);
        details.Add("--- interpretation ---");
        details.Add($"  game + related rows gone : {rowsGone}");
        details.Add($"  game files kept on disk  : {filesKept}");
        details.Add($"  backup kept by default   : {backupKeptByDefault}");
        details.Add($"  explicit backup deletion : {explicitBackupDeletion}");
        details.Add($"  UI offers the command    : {uiOk}");

        var actual = $"rowsGone={rowsGone}, filesKept={filesKept}, backupKeptByDefault={backupKeptByDefault}, "
                   + $"explicitBackupDeletion={explicitBackupDeletion}, uiWiring={uiOk}";

        return (pass
                ? CheckResult.Pass(Id, Title, expected, actual)
                : CheckResult.Fail(Id, Title, expected, actual))
            .With(details.ToArray());
    }

    private static async Task<Dictionary<string, int>> CountRelatedRowsAsync(
        GalboxDbContext db,
        int gameId,
        CancellationToken cancellationToken)
    {
        return new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["Characters"] = await db.Set<GameCharacter>().CountAsync(c => c.GameInfoId == gameId, cancellationToken).ConfigureAwait(false),
            ["Documents"] = await db.Set<GameDocument>().CountAsync(d => d.GameInfoId == gameId, cancellationToken).ConfigureAwait(false),
            ["MediaFiles"] = await db.Set<GameMediaFile>().CountAsync(m => m.GameInfoId == gameId, cancellationToken).ConfigureAwait(false),
            ["Screenshots"] = await db.Set<GameScreenshot>().CountAsync(s => s.GameInfoId == gameId, cancellationToken).ConfigureAwait(false),
            ["SaveBackups"] = await db.SaveBackups.CountAsync(b => b.GameInfoId == gameId, cancellationToken).ConfigureAwait(false),
            ["ErrorRecords"] = await db.ErrorRecords.CountAsync(e => e.GameInfoId == gameId, cancellationToken).ConfigureAwait(false),
            ["Patches"] = await db.Patches.CountAsync(p => p.GameInfoId == gameId, cancellationToken).ConfigureAwait(false)
        };
    }

    private static bool RequireText(string filePath, string needle, string description, List<string> evidence)
    {
        if (!File.Exists(filePath))
        {
            evidence.Add($"  [FAIL] {description}: file not found ({filePath})");
            return false;
        }

        var text = File.ReadAllText(filePath);
        var found = text.Contains(needle, StringComparison.Ordinal);
        evidence.Add($"  [{(found ? "OK  " : "FAIL")}] {description}: \"{needle}\"");
        return found;
    }
}
