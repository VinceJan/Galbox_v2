using Galbox.App.Services;
using Galbox.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Galbox.Acceptance.Checks;

/// <summary>
/// A12 - A restore that fails halfway must put the previous save back (W19).
///
/// The defect: the two catch blocks in <c>RestoreBackupAsync</c> logged
/// <c>"Attempting rollback after error"</c> and contained no rollback code at all, while the
/// <c>finally</c> block deleted the temporary copy of the previous save files. A restore that threw
/// (a corrupted archive, a path that cannot be written, a full disk) therefore left the save folder
/// half-overwritten and destroyed the only copy of the previous state. No database record of a
/// pre-restore backup was created either, so the user had nothing to go back to.
///
/// The failure below is produced without any artificial hook: the target archive contains an entry
/// whose name collides with a DIRECTORY in the live save folder, so the extraction writes the first
/// entry (overwriting a live save file) and then throws while creating the second one.
/// </summary>
public sealed class A12RestoreRollbackCheck : IAcceptanceCheck
{
    private const string OriginalSlot1 = "ORIGINAL-SLOT1-A12";
    private const string OriginalSlot2 = "ORIGINAL-SLOT2-A12";
    private const string PartialSlot1 = "NEW-FROM-BACKUP-A12";

    /// <inheritdoc />
    public string Id => "A12";

    /// <inheritdoc />
    public string Title => "A failed restore rolls the save folder back to its pre-restore state and records it";

    /// <inheritdoc />
    public async Task<CheckResult> RunAsync(AcceptanceContext context, CancellationToken cancellationToken)
    {
        var expected = "RestoreBackupAsync returns false, every live save file is byte-identical to its pre-restore "
                     + "content, and a durable pre-restore backup (file + database row) exists";

        var details = new List<string>();
        var work = AcceptanceWork.Create("a12");
        var liveFolder = Path.Combine(work, "live-save");
        var slot1 = AcceptanceWork.WriteText(Path.Combine(liveFolder, "slot1.save"), OriginalSlot1);
        var slot2 = AcceptanceWork.WriteText(Path.Combine(liveFolder, "slot2.save"), OriginalSlot2);

        // A directory with the name of the second archive entry: extracting that entry must fail.
        Directory.CreateDirectory(Path.Combine(liveFolder, "blocker"));

        var hashBefore1 = AcceptanceWork.HashFile(slot1);
        var hashBefore2 = AcceptanceWork.HashFile(slot2);

        var brokenZip = AcceptanceWork.CreateZip(
            Path.Combine(work, "backups", "broken-target.zip"),
            ("slot1.save", System.Text.Encoding.UTF8.GetBytes(PartialSlot1)),
            ("blocker", System.Text.Encoding.UTF8.GetBytes("this entry cannot be extracted: a directory owns the name")));

        var gameFolder = Path.Combine(work, "game-folder");
        Directory.CreateDirectory(gameFolder);

        details.Add($"Scratch folder       : {work}");
        details.Add($"Live save folder     : {liveFolder}");
        details.Add($"  slot1.save         : \"{OriginalSlot1}\" sha256 {hashBefore1}");
        details.Add($"  slot2.save         : \"{OriginalSlot2}\" sha256 {hashBefore2}");
        details.Add($"  blocker\\           : directory (makes the second archive entry fail to extract)");
        details.Add($"Broken target backup : {brokenZip} entries=[{string.Join(", ", AcceptanceWork.ZipEntryNames(brokenZip))}]");
        details.Add($"Partial content it would write into slot1.save: \"{PartialSlot1}\"");

        int gameId;
        int backupId;

        await using (var scope = context.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GalboxDbContext>();

            var game = new GameInfo
            {
                NameOriginal = "A12 rollback probe",
                InstallPath = gameFolder,
                MainExecutable = Path.Combine(gameFolder, "probe.exe"),
                AddedTime = DateTime.UtcNow,
                UpdatedTime = DateTime.UtcNow
            };

            db.Games.Add(game);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            gameId = game.Id;

            var backup = new GameSaveBackup
            {
                GameInfoId = gameId,
                Name = "A12 broken target backup",
                BackupPath = brokenZip,
                OriginalSavePath = liveFolder,
                CreatedTime = DateTime.UtcNow,
                SizeBytes = new FileInfo(brokenZip).Length,
                Description = "A12 acceptance fixture"
            };

            db.SaveBackups.Add(backup);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            backupId = backup.Id;
        }

        details.Add($"Inserted game Id     : {gameId}");
        details.Add($"Inserted backup Id   : {backupId}");

        var service = context.Get<ISaveManagementService>();

        var restoreResult = await service
            .RestoreBackupAsync(backupId, null, cancellationToken)
            .ConfigureAwait(false);

        var content1 = AcceptanceWork.ReadTextOrMissing(slot1);
        var content2 = AcceptanceWork.ReadTextOrMissing(slot2);
        var hashAfter1 = File.Exists(slot1) ? AcceptanceWork.HashFile(slot1) : "(missing)";
        var hashAfter2 = File.Exists(slot2) ? AcceptanceWork.HashFile(slot2) : "(missing)";

        details.Add("--- RestoreBackupAsync result ---");
        details.Add($"  return value       : {restoreResult}  (expected False: the archive cannot be extracted)");
        details.Add("--- live save folder after the failed restore ---");
        details.Add($"  files              : [{string.Join(", ", AcceptanceWork.ListFilesRelative(liveFolder))}]");
        details.Add($"  slot1.save         : \"{content1}\"");
        details.Add($"    sha256 after     : {hashAfter1}");
        details.Add($"    sha256 before    : {hashBefore1}");
        details.Add($"  slot2.save         : \"{content2}\"");
        details.Add($"    sha256 after     : {hashAfter2}");
        details.Add($"    sha256 before    : {hashBefore2}");

        var slot1Restored = hashAfter1 == hashBefore1;
        var slot2Restored = hashAfter2 == hashBefore2;

        // The durable safety copy of the pre-restore state: a database row whose zip really exists.
        var safetyRows = new List<(string Name, string Description, string Path, bool FileExists, long Size)>();
        await using (var scope = context.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GalboxDbContext>();
            var rows = await db.SaveBackups
                .AsNoTracking()
                .Where(b => b.GameInfoId == gameId && b.Id != backupId)
                .OrderByDescending(b => b.CreatedTime)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            safetyRows.AddRange(rows.Select(row => (
                row.Name,
                row.Description ?? string.Empty,
                row.BackupPath,
                File.Exists(row.BackupPath),
                row.SizeBytes)));
        }

        details.Add("--- pre-restore safety backup row(s) in the database ---");
        if (safetyRows.Count == 0)
        {
            details.Add("  (none: nothing recorded a copy of the live save before it was overwritten)");
        }
        else
        {
            foreach (var row in safetyRows)
            {
                details.Add($"  {row.Name}");
                details.Add($"    description      : {row.Description}");
                details.Add($"    zip path         : {row.Path}");
                details.Add($"    zip exists       : {row.FileExists} ({row.Size} bytes)");
            }
        }

        var hasDurableSafetyCopy = safetyRows.Any(row => row.FileExists && row.Size > 0);
        var pass = !restoreResult && slot1Restored && slot2Restored && hasDurableSafetyCopy;

        try
        {
            await using var scope = context.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<GalboxDbContext>();
            var stale = await db.SaveBackups.Where(b => b.GameInfoId == gameId).ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var row in stale)
            {
                try
                {
                    if (File.Exists(row.BackupPath))
                    {
                        File.Delete(row.BackupPath);
                    }
                }
                catch
                {
                    // best effort cleanup
                }
            }

            await db.SaveBackups.Where(b => b.GameInfoId == gameId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await db.Games.Where(g => g.Id == gameId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            details.Add($"Cleanup warning: {ex.Message}");
        }

        details.Add("--- interpretation ---");
        details.Add($"  slot1.save byte-identical to pre-restore state : {slot1Restored}");
        details.Add($"  slot2.save byte-identical to pre-restore state : {slot2Restored}");
        details.Add($"  durable pre-restore backup recorded            : {hasDurableSafetyCopy}");
        if (!pass)
        {
            details.Add("  VERDICT: the failed restore left the live save folder modified and/or no copy of the");
            details.Add("           previous state was recorded - the W19 'log-only rollback' defect.");
        }

        var actual = $"restoreReturned={restoreResult}, slot1Restored={slot1Restored}, slot2Restored={slot2Restored}, "
                   + $"durableSafetyCopy={hasDurableSafetyCopy}";

        return (pass
                ? CheckResult.Pass(Id, Title, expected, actual)
                : CheckResult.Fail(Id, Title, expected, actual))
            .With(details.ToArray());
    }
}
