using Galbox.App.Services;
using Galbox.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Galbox.Acceptance.Checks;

/// <summary>
/// A14 - Restore verification compares content, with no tolerance (W21).
///
/// The defect: <c>VerifyRestoreIntegrityAsync</c> compared only the number of files (allowing 20% to
/// be missing) and the sum of their sizes (allowing 10% to be wrong) - a restore that silently
/// dropped a file, or wrote incorrect bytes of the right length, was reported as verified.
///
/// The verification is driven directly through the real private method so the exact comparison can
/// be measured against fixtures that are byte-for-byte identical in count and size:
///   1. right file count, right size, DIFFERENT content   -> must be rejected (pre-fix: accepted)
///   2. one file of ten missing (within the old 20% / 10% tolerance) -> must be rejected (pre-fix: accepted)
///   3. control: identical content                        -> must still be accepted
/// A full <c>RestoreBackupAsync</c> of a valid archive closes the loop on the happy path.
/// </summary>
public sealed class A14RestoreVerificationCheck : IAcceptanceCheck
{
    /// <inheritdoc />
    public string Id => "A14";

    /// <inheritdoc />
    public string Title => "Restore verification rejects same-size/different-content and missing-file restores";

    /// <inheritdoc />
    public async Task<CheckResult> RunAsync(AcceptanceContext context, CancellationToken cancellationToken)
    {
        var expected = "a restored file with the right size but the wrong content is rejected, a restore that "
                     + "lost one file of ten is rejected, and a byte-identical restore is still accepted";

        var details = new List<string>();
        var work = AcceptanceWork.Create("a14");
        var service = context.Get<ISaveManagementService>();

        // ---------------------------------------------------------------- scenario 1: wrong content
        var scenario1 = Path.Combine(work, "scenario1");
        var zip1 = AcceptanceWork.CreateZip(
            Path.Combine(scenario1, "archive.zip"),
            ("slot1.save", Enumerable.Repeat((byte)0x41, 4096).ToArray()));

        var restored1 = scenario1;
        AcceptanceWork.WritePattern(Path.Combine(restored1, "slot1.save"), 4096, 0x42); // same size, other bytes

        var archiveHash1 = AcceptanceWork.HashFile(Path.Combine(scenario1, "archive.zip"));
        var fileHash1 = AcceptanceWork.HashFile(Path.Combine(restored1, "slot1.save"));

        var (ok1, result1, error1) = await ReflectionBridge
            .CallPrivateAsync(service, "VerifyRestoreIntegrityAsync", zip1, restored1, cancellationToken)
            .ConfigureAwait(false);

        details.Add("=== scenario 1: same file count, same size, different content ===");
        details.Add($"  archive            : {zip1} (1 entry, 4096 bytes)");
        details.Add($"  restored file      : {Path.Combine(restored1, "slot1.save")} (4096 bytes, all 0x42 instead of 0x41)");
        details.Add($"  archive sha256     : {archiveHash1}");
        details.Add($"  restored sha256    : {fileHash1}");
        details.Add($"  verifier invoked   : {ok1}{(error1 is null ? string.Empty : $" (error: {error1})")}");
        details.Add($"  Success            : {ReflectionBridge.Bool(result1, "Success")}");
        details.Add($"  ErrorMessage       : {ReflectionBridge.String(result1, "ErrorMessage") ?? "(null)"}");

        var contentMismatchRejected = ok1 && !ReflectionBridge.Bool(result1, "Success");

        // ------------------------------------------------------------- scenario 2: one file missing
        var scenario2 = Path.Combine(work, "scenario2");
        var entries = Enumerable.Range(1, 10)
            .Select(i => ($"slot{i}.save", Enumerable.Repeat((byte)i, 1000).ToArray()))
            .ToArray();
        var zip2 = AcceptanceWork.CreateZip(Path.Combine(scenario2, "archive.zip"), entries);
        var restored2 = Path.Combine(scenario2, "restored");
        foreach (var (name, content) in entries.Take(9))
        {
            AcceptanceWork.WritePattern(Path.Combine(restored2, name), content.Length, content[0]);
        }

        var (ok2, result2, error2) = await ReflectionBridge
            .CallPrivateAsync(service, "VerifyRestoreIntegrityAsync", zip2, restored2, cancellationToken)
            .ConfigureAwait(false);

        details.Add(string.Empty);
        details.Add("=== scenario 2: 9 of 10 files restored (inside the old 20% count / 10% size tolerance) ===");
        details.Add($"  archive            : {zip2} (10 entries x 1000 bytes = 10000 bytes)");
        details.Add($"  restored folder    : {restored2} ({AcceptanceWork.ListFilesRelative(restored2).Count} files, 9000 bytes)");
        details.Add($"  missing file       : slot10.save");
        details.Add($"  verifier invoked   : {ok2}{(error2 is null ? string.Empty : $" (error: {error2})")}");
        details.Add($"  Success            : {ReflectionBridge.Bool(result2, "Success")}");
        details.Add($"  ErrorMessage       : {ReflectionBridge.String(result2, "ErrorMessage") ?? "(null)"}");

        var missingFileRejected = ok2 && !ReflectionBridge.Bool(result2, "Success");

        // ------------------------------------------------------------------- scenario 3: control
        var scenario3 = Path.Combine(work, "scenario3");
        var zip3 = AcceptanceWork.CreateZip(
            Path.Combine(scenario3, "archive.zip"),
            ("slot1.save", Enumerable.Repeat((byte)0x41, 2048).ToArray()),
            ("nested/persistent", Enumerable.Repeat((byte)0x43, 512).ToArray()));

        var restored3 = Path.Combine(scenario3, "restored");
        AcceptanceWork.WritePattern(Path.Combine(restored3, "slot1.save"), 2048, 0x41);
        AcceptanceWork.WritePattern(Path.Combine(restored3, "nested", "persistent"), 512, 0x43);

        var (ok3, result3, error3) = await ReflectionBridge
            .CallPrivateAsync(service, "VerifyRestoreIntegrityAsync", zip3, restored3, cancellationToken)
            .ConfigureAwait(false);

        details.Add(string.Empty);
        details.Add("=== scenario 3: control - identical content must still verify ===");
        details.Add($"  archive            : {zip3} (2 entries: slot1.save, nested/persistent)");
        details.Add($"  restored folder    : [{string.Join(", ", AcceptanceWork.ListFilesRelative(restored3))}]");
        details.Add($"  verifier invoked   : {ok3}{(error3 is null ? string.Empty : $" (error: {error3})")}");
        details.Add($"  Success            : {ReflectionBridge.Bool(result3, "Success")}");
        details.Add($"  ErrorMessage       : {ReflectionBridge.String(result3, "ErrorMessage") ?? "(null)"}");

        var controlAccepted = ok3 && ReflectionBridge.Bool(result3, "Success");

        // ------------------------------------------------- happy path through the real public API
        var liveFolder = Path.Combine(work, "live");
        AcceptanceWork.WriteText(Path.Combine(liveFolder, "old.save"), "old");
        var goodZip = AcceptanceWork.CreateZip(
            Path.Combine(work, "good.zip"),
            ("slot1.save", System.Text.Encoding.UTF8.GetBytes("GOOD-RESTORE-1")),
            ("slot2.save", System.Text.Encoding.UTF8.GetBytes("GOOD-RESTORE-2")));
        var gameFolder = Path.Combine(work, "game");
        Directory.CreateDirectory(gameFolder);

        int gameId;
        int backupId;
        await using (var scope = context.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GalboxDbContext>();
            var game = new GameInfo
            {
                NameOriginal = "A14 verification probe",
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
                Name = "A14 good backup",
                BackupPath = goodZip,
                OriginalSavePath = liveFolder,
                CreatedTime = DateTime.UtcNow,
                SizeBytes = new FileInfo(goodZip).Length
            };
            db.SaveBackups.Add(backup);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            backupId = backup.Id;
        }

        var happyPathRestore = await service.RestoreBackupAsync(backupId, null, cancellationToken).ConfigureAwait(false);
        var happyPathFiles = AcceptanceWork.ListFilesRelative(liveFolder);

        details.Add(string.Empty);
        details.Add("=== scenario 4: a valid restore through the public API (regression guard) ===");
        details.Add($"  RestoreBackupAsync : {happyPathRestore} (expected True)");
        details.Add($"  live folder now    : [{string.Join(", ", happyPathFiles)}]");
        details.Add($"  slot1.save         : \"{AcceptanceWork.ReadTextOrMissing(Path.Combine(liveFolder, "slot1.save"))}\"");
        details.Add($"  slot2.save         : \"{AcceptanceWork.ReadTextOrMissing(Path.Combine(liveFolder, "slot2.save"))}\"");

        var happyPathOk = happyPathRestore
                       && AcceptanceWork.ReadTextOrMissing(Path.Combine(liveFolder, "slot1.save")) == "GOOD-RESTORE-1"
                       && AcceptanceWork.ReadTextOrMissing(Path.Combine(liveFolder, "slot2.save")) == "GOOD-RESTORE-2";

        try
        {
            await using var scope = context.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<GalboxDbContext>();
            var rows = await db.SaveBackups.Where(b => b.GameInfoId == gameId).ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var row in rows)
            {
                try
                {
                    if (File.Exists(row.BackupPath) && row.BackupPath.StartsWith(work, StringComparison.OrdinalIgnoreCase) == false)
                    {
                        File.Delete(row.BackupPath);
                    }
                }
                catch
                {
                    // best effort
                }
            }

            await db.SaveBackups.Where(b => b.GameInfoId == gameId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await db.Games.Where(g => g.Id == gameId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            details.Add($"Cleanup warning: {ex.Message}");
        }

        var pass = contentMismatchRejected && missingFileRejected && controlAccepted && happyPathOk;

        details.Add(string.Empty);
        details.Add("--- interpretation ---");
        details.Add($"  same size / wrong content rejected : {contentMismatchRejected}");
        details.Add($"  missing file rejected              : {missingFileRejected}");
        details.Add($"  correct content accepted           : {controlAccepted}");
        details.Add($"  full restore happy path            : {happyPathOk}");
        if (!pass && (!contentMismatchRejected || !missingFileRejected))
        {
            details.Add("  VERDICT: the verifier accepted a restore whose content does not match the archive -");
            details.Add("           the W21 count/size-with-tolerance defect.");
        }

        var actual = $"contentMismatchRejected={contentMismatchRejected}, missingFileRejected={missingFileRejected}, "
                   + $"controlAccepted={controlAccepted}, happyPathRestore={happyPathOk}";

        return (pass
                ? CheckResult.Pass(Id, Title, expected, actual)
                : CheckResult.Fail(Id, Title, expected, actual))
            .With(details.ToArray());
    }
}
