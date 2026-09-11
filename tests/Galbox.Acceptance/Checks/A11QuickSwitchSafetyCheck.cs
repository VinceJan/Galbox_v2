using Galbox.App.Services;
using Galbox.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Galbox.Acceptance.Checks;

/// <summary>
/// A11 - Quick switch must abort when the current save cannot be backed up (W18).
///
/// The defect: <c>QuickSwitchSaveAsync</c> called <c>CreateBackupAsync</c> and then went straight to
/// <c>RestoreBackupAsync</c> without checking whether the backup had actually been produced. When it
/// had not (no save location detected, unreadable save files, no disk space), the target backup was
/// still written over the live save files - and the operation reported success. The user's previous
/// save was destroyed with no copy anywhere and no error message.
///
/// The scenario below reaches exactly that state through the real services: a library entry whose
/// installation folder no longer exists (so no save location can be detected, and the pre-switch
/// backup genuinely fails) plus a real target backup whose restore path points at a folder holding
/// the live save. Nothing is faked: the check reads the live file back from disk afterwards.
/// </summary>
public sealed class A11QuickSwitchSafetyCheck : IAcceptanceCheck
{
    private const string OriginalSaveContent = "ORIGINAL-SAVE-CONTENT-A11";
    private const string TargetBackupContent = "TARGET-BACKUP-CONTENT-A11";

    /// <inheritdoc />
    public string Id => "A11";

    /// <inheritdoc />
    public string Title => "Quick switch aborts (and reports failure) when the current save cannot be backed up";

    /// <inheritdoc />
    public async Task<CheckResult> RunAsync(AcceptanceContext context, CancellationToken cancellationToken)
    {
        var expected = "when the pre-switch backup of the current save cannot be created, QuickSwitchSaveAsync "
                     + "returns Success=false with a readable reason AND the live save file on disk is byte-identical "
                     + "to what it was before the call";

        var details = new List<string>();
        var work = AcceptanceWork.Create("a11");
        var liveFolder = Path.Combine(work, "live-save");
        var liveSaveFile = AcceptanceWork.WriteText(Path.Combine(liveFolder, "slot1.save"), OriginalSaveContent);
        var targetZip = AcceptanceWork.CreateZip(
            Path.Combine(work, "backups", "target.zip"),
            ("slot1.save", System.Text.Encoding.UTF8.GetBytes(TargetBackupContent)));

        // A game whose installation folder does not exist at all. Save detection therefore finds
        // nothing, and CreateBackupAsync cannot produce a backup.
        var missingGameFolder = Path.Combine(work, "game-folder-that-does-not-exist");

        details.Add($"Scratch folder       : {work}");
        details.Add($"Live save folder     : {liveFolder}");
        details.Add($"Live save file       : {liveSaveFile} ({OriginalSaveContent})");
        details.Add($"Target backup zip    : {targetZip} ({AcceptanceWork.ZipEntryNames(targetZip).Count} entry)");
        details.Add($"Game install path    : {missingGameFolder} (exists: {Directory.Exists(missingGameFolder)})");

        int gameId;
        int backupId;

        await using (var scope = context.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GalboxDbContext>();

            var game = new GameInfo
            {
                NameOriginal = "A11 quick-switch probe",
                InstallPath = missingGameFolder,
                MainExecutable = Path.Combine(missingGameFolder, "nothing.exe"),
                AddedTime = DateTime.UtcNow,
                UpdatedTime = DateTime.UtcNow
            };

            db.Games.Add(game);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            gameId = game.Id;

            var backup = new GameSaveBackup
            {
                GameInfoId = gameId,
                Name = "A11 target backup",
                BackupPath = targetZip,
                OriginalSavePath = liveFolder,
                CreatedTime = DateTime.UtcNow,
                SizeBytes = new FileInfo(targetZip).Length,
                Description = "A11 acceptance fixture"
            };

            db.SaveBackups.Add(backup);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            backupId = backup.Id;
        }

        details.Add($"Inserted game Id     : {gameId}");
        details.Add($"Inserted backup Id   : {backupId} (OriginalSavePath={liveFolder})");

        var service = context.Get<ISaveManagementService>();

        // Prove the precondition first: the safe behaviour depends on this backup genuinely failing.
        var preSwitchBackup = await service
            .CreateBackupAsync(
                new GameInfo
                {
                    Id = gameId,
                    NameOriginal = "A11 quick-switch probe",
                    InstallPath = missingGameFolder,
                    MainExecutable = Path.Combine(missingGameFolder, "nothing.exe")
                },
                "A11 precondition probe",
                null,
                cancellationToken)
            .ConfigureAwait(false);

        details.Add($"--- precondition: CreateBackupAsync for this game ---");
        details.Add($"  result             : {(preSwitchBackup is null ? "null (backup could NOT be created)" : $"UNEXPECTED backup created: {preSwitchBackup.BackupPath}")}");
        details.Add($"  save file before   : \"{AcceptanceWork.ReadTextOrMissing(liveSaveFile)}\"");
        details.Add($"  save file sha256   : {AcceptanceWork.HashFile(liveSaveFile)}");

        var liveSaveHashBefore = AcceptanceWork.HashFile(liveSaveFile);

        var result = await service
            .QuickSwitchSaveAsync(gameId, backupId, cancellationToken)
            .ConfigureAwait(false);

        var liveSaveContentAfter = AcceptanceWork.ReadTextOrMissing(liveSaveFile);
        var liveSaveHashAfter = File.Exists(liveSaveFile) ? AcceptanceWork.HashFile(liveSaveFile) : "(missing)";
        var contentUnchanged = liveSaveContentAfter == OriginalSaveContent;

        details.Add("--- QuickSwitchSaveAsync result ---");
        details.Add($"  Success            : {result.Success}");
        details.Add($"  ErrorMessage       : {result.ErrorMessage ?? "(null)"}");
        details.Add($"  CurrentBackup      : {(result.CurrentBackup is null ? "(null)" : $"Id={result.CurrentBackup.Id}")}");
        details.Add($"  RestoredBackup     : {(result.RestoredBackup is null ? "(null)" : $"Id={result.RestoredBackup.Id}")}");
        details.Add("--- live save file after the call ---");
        details.Add($"  content            : \"{liveSaveContentAfter}\"");
        details.Add($"  sha256             : {liveSaveHashAfter}");
        details.Add($"  hash before        : {liveSaveHashBefore}");
        details.Add($"  byte-identical     : {liveSaveHashBefore == liveSaveHashAfter}");
        details.Add($"  target content was : \"{TargetBackupContent}\" (must NOT have been written)");

        var reportsFailure = !result.Success && !string.IsNullOrWhiteSpace(result.ErrorMessage);
        var failedForTheRightReason = result.ErrorMessage is not null
            && (result.ErrorMessage.Contains("中止", StringComparison.Ordinal)
                || result.ErrorMessage.Contains("安全备份", StringComparison.Ordinal));
        var pass = reportsFailure && contentUnchanged && failedForTheRightReason;

        // Clean up the rows this check created; the database is recreated on the next run anyway,
        // but a half-deleted library makes a failure harder to read.
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

        details.Add("--- interpretation ---");
        details.Add($"  reports failure                 : {reportsFailure}");
        details.Add($"  failure explains the reason     : {failedForTheRightReason}");
        details.Add($"  live save byte-identical        : {contentUnchanged}");
        if (!pass && !reportsFailure && contentUnchanged == false)
        {
            details.Add("  VERDICT: the pre-switch backup failed, the target backup was still applied and the");
            details.Add("           operation reported success - the W18 data-loss defect.");
        }
        else if (!pass)
        {
            details.Add("  VERDICT: see the sub-assertions above.");
        }

        var actual = $"Success={result.Success}, liveSaveUnchanged={contentUnchanged}, "
                   + $"error=\"{result.ErrorMessage ?? "(null)"}\"";

        return (pass
                ? CheckResult.Pass(Id, Title, expected, actual)
                : CheckResult.Fail(Id, Title, expected, actual))
            .With(details.ToArray());
    }
}
