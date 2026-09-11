namespace Galbox.Core.Patches;

/// <summary>Result of backing one file up.</summary>
public sealed class PatchBackupRecord
{
    /// <summary>Path relative to the game root.</summary>
    public required string RelativePath { get; init; }

    /// <summary>Backup location relative to the install folder, e.g. <c>files\data.xp3</c>.</summary>
    public required string BackupRel { get; init; }

    /// <summary>Absolute backup path.</summary>
    public required string BackupFullPath { get; init; }

    /// <summary>Size of the copied file.</summary>
    public long SizeBytes { get; init; }

    /// <summary>SHA-256 of the source file, computed while copying.</summary>
    public required string Sha256 { get; init; }

    /// <summary>True when the copy was read back from disk and matched <see cref="Sha256"/>.</summary>
    public bool ReadBackVerified { get; init; }

    /// <summary>Hash actually read back (null when the read-back failed).</summary>
    public string? ReadBackSha256 { get; init; }
}

/// <summary>Outcome of restoring one file from its backup.</summary>
public sealed class PatchRestoreRecord
{
    /// <summary>Path relative to the game root.</summary>
    public required string RelativePath { get; init; }

    /// <summary>Outcome.</summary>
    public required PatchRestoreOutcome Outcome { get; init; }

    /// <summary>Hash that was expected (recorded before the install).</summary>
    public string? ExpectedHash { get; init; }

    /// <summary>Hash found after restoration.</summary>
    public string? ActualHash { get; init; }

    /// <summary>True when the restored bytes match the pre-install hash exactly.</summary>
    public bool ByteIdentical { get; init; }

    /// <summary>Detail, especially for skipped/failed files.</summary>
    public string? Message { get; init; }
}

/// <summary>Per-file rollback outcome.</summary>
public enum PatchRestoreOutcome
{
    /// <summary>The previous bytes were restored and verified.</summary>
    Restored = 0,

    /// <summary>A file created by the install was removed.</summary>
    Deleted = 1,

    /// <summary>Nothing had to be done.</summary>
    Skipped = 2,

    /// <summary>Restoration was attempted but the result does not match the pre-install hash.</summary>
    VerificationFailed = 3,

    /// <summary>Restoration could not be performed at all.</summary>
    Failed = 4
}

/// <summary>
/// The backup store. Rules enforced here:
/// <list type="number">
/// <item>Only files that are about to be overwritten are copied - never the whole game directory.</item>
/// <item>Backups mirror the relative path structure instead of being flattened.</item>
/// <item>Every copy is read back and hashed before the install proceeds.</item>
/// <item>Backups are never written inside the game directory.</item>
/// </list>
/// </summary>
public sealed class PatchBackupStore
{
    private readonly PatchInstallerOptions _options;
    private readonly PatchLedger _ledger;

    /// <summary>Creates a store bound to the given options.</summary>
    public PatchBackupStore(PatchInstallerOptions options)
    {
        _options = options;
        _ledger = new PatchLedger(options);
    }

    /// <summary>Absolute path where the backup of <paramref name="relativePath"/> will live.</summary>
    public string BackupPathFor(string gameId, string installId, string relativePath)
        => Path.Combine(_ledger.InstallFilesFolder(gameId, installId), relativePath);

    /// <summary>Relative backup path recorded in the manifest (always forward-slash free, uses <c>files\</c> prefix).</summary>
    public static string BackupRelFor(string relativePath) => Path.Combine("files", relativePath);

    /// <summary>
    /// Refuses a configuration that would place backups inside the game tree. Called before anything is
    /// written, because a backup inside the game directory gets picked up by integrity checks and cloud sync.
    /// </summary>
    public static void AssertOutsideGameRoot(string backupRoot, string gameRoot)
    {
        if (LongPath.IsUnder(gameRoot, backupRoot))
        {
            throw new PatchSecurityException(
                PatchSecurityCode.Unspecified,
                $"Backup root '{LongPath.Strip(backupRoot)}' is inside the game directory '{LongPath.Strip(gameRoot)}'. " +
                "Backups must live outside the game tree so that integrity checks and cloud sync do not treat them as user files.");
        }
    }

    /// <summary>Copies one file into the backup store and verifies the copy by reading it back.</summary>
    public async Task<PatchBackupRecord> BackupAsync(
        string gameId,
        string installId,
        string gameRoot,
        string relativePath,
        PatchFileAction action,
        CancellationToken ct = default)
    {
        if (action == PatchFileAction.Created)
        {
            throw new InvalidOperationException("Files that are created by an install must not be backed up.");
        }

        var source = LongPath.CombineUnder(gameRoot, relativePath);
        if (!File.Exists(LongPath.Ensure(source)))
        {
            throw new FileNotFoundException($"Nothing to back up: '{LongPath.Strip(source)}' does not exist.", source);
        }

        var backupFull = LongPath.CombineUnder(_ledger.InstallFilesFolder(gameId, installId), relativePath);
        await using (var input = new FileStream(LongPath.Ensure(source), FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await PatchHashing.CopyAndHashAsync(input, backupFull, ct).ConfigureAwait(false);
        }

        var sourceHash = await PatchHashing.HashFileAsync(source, ct).ConfigureAwait(false);
        var backupHash = await PatchHashing.HashFileAsync(backupFull, ct).ConfigureAwait(false);
        var size = new FileInfo(LongPath.Ensure(backupFull)).Length;

        if (!string.Equals(sourceHash, backupHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException(
                $"Backup verification failed for '{relativePath}': source={sourceHash} backup={backupHash}. " +
                "The install is aborted before the game directory is modified.");
        }

        return new PatchBackupRecord
        {
            RelativePath = relativePath,
            BackupRel = BackupRelFor(relativePath),
            BackupFullPath = backupFull,
            SizeBytes = size,
            Sha256 = sourceHash,
            ReadBackVerified = true,
            ReadBackSha256 = backupHash
        };
    }

    /// <summary>Verifies that a backup file is still intact before a rollback is attempted.</summary>
    public async Task<bool> VerifyBackupAsync(string gameId, string installId, string backupRel, string expectedHash, CancellationToken ct = default)
    {
        var path = LongPath.CombineUnder(_ledger.InstallFolder(gameId, installId), backupRel);
        return await PatchHashing.VerifyFileAsync(path, expectedHash, ct).ConfigureAwait(false);
    }

    /// <summary>Restores one backed-up file over its target and verifies the restored bytes.</summary>
    public async Task<PatchRestoreRecord> RestoreAsync(
        string gameId,
        string installId,
        string gameRoot,
        PatchManifestFileEntry entry,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(entry.BackupRel))
        {
            return new PatchRestoreRecord
            {
                RelativePath = entry.Rel,
                Outcome = PatchRestoreOutcome.Failed,
                ExpectedHash = entry.HashBefore,
                Message = "The manifest has no backup location for this file; it cannot be restored."
            };
        }

        var backupFull = LongPath.CombineUnder(_ledger.InstallFolder(gameId, installId), entry.BackupRel);
        if (!File.Exists(LongPath.Ensure(backupFull)))
        {
            return new PatchRestoreRecord
            {
                RelativePath = entry.Rel,
                Outcome = PatchRestoreOutcome.Failed,
                ExpectedHash = entry.HashBefore,
                Message = $"Backup file '{LongPath.Strip(backupFull)}' is missing."
            };
        }

        var backupHash = await PatchHashing.HashFileAsync(backupFull, ct).ConfigureAwait(false);
        if (entry.HashBefore is not null && !string.Equals(backupHash, entry.HashBefore, StringComparison.OrdinalIgnoreCase))
        {
            return new PatchRestoreRecord
            {
                RelativePath = entry.Rel,
                Outcome = PatchRestoreOutcome.Failed,
                ExpectedHash = entry.HashBefore,
                ActualHash = backupHash,
                Message = "The backup itself is corrupt (hash mismatch). Nothing was written; a partial rollback is not allowed."
            };
        }

        var target = LongPath.CombineUnder(gameRoot, entry.Rel);
        var parent = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(LongPath.Ensure(parent));

        await using (var input = new FileStream(LongPath.Ensure(backupFull), FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await PatchHashing.CopyAndHashAsync(input, target, ct).ConfigureAwait(false);
        }

        var restoredHash = await PatchHashing.HashFileAsync(target, ct).ConfigureAwait(false);
        var identical = entry.HashBefore is null || string.Equals(restoredHash, entry.HashBefore, StringComparison.OrdinalIgnoreCase);

        return new PatchRestoreRecord
        {
            RelativePath = entry.Rel,
            Outcome = identical ? PatchRestoreOutcome.Restored : PatchRestoreOutcome.VerificationFailed,
            ExpectedHash = entry.HashBefore,
            ActualHash = restoredHash,
            ByteIdentical = identical,
            Message = identical ? null : "Restored bytes do not match the pre-install hash."
        };
    }

    /// <summary>Computes the retention plan. Pure calculation: it never deletes anything.</summary>
    public PatchRetentionPlan PlanRetention(string gameRoot, string gameId)
    {
        var installs = _ledger.ReadAllInstalls(gameRoot, gameId);
        var candidates = new List<PatchRetentionCandidate>();

        foreach (var manifest in installs)
        {
            var folder = _ledger.InstallFolder(gameId, manifest.InstallId);
            var bytes = Directory.Exists(LongPath.Ensure(folder)) ? FolderSize(folder) : 0;

            string? protectedReason = manifest.Status switch
            {
                PatchManifestStatus.Pending => "an interrupted install; recovering it needs the backups",
                PatchManifestStatus.Failed => "a failed install; its backups may be the only way back",
                _ => null
            };

            candidates.Add(new PatchRetentionCandidate
            {
                InstallId = manifest.InstallId,
                PatchName = manifest.PatchName,
                InstalledAt = manifest.CommittedAt ?? manifest.CreatedAt,
                BackupBytes = bytes,
                Status = manifest.Status,
                ProtectedReason = protectedReason
            });
        }

        var ordered = candidates.OrderByDescending(c => c.InstallId, StringComparer.Ordinal).ToList();
        var keep = new List<PatchRetentionCandidate>();
        var reclaimable = new List<PatchRetentionCandidate>();
        var slots = Math.Max(1, _options.BackupRetentionCount);

        foreach (var candidate in ordered)
        {
            if (!candidate.IsProtected && slots > 0)
            {
                keep.Add(candidate);
                slots--;
            }
            else
            {
                reclaimable.Add(candidate);
            }
        }

        return new PatchRetentionPlan
        {
            GameId = gameId,
            KeepCount = Math.Max(1, _options.BackupRetentionCount),
            Kept = keep,
            Reclaimable = reclaimable,
            Recommendation = reclaimable.Count == 0
                ? $"Keeping all {keep.Count} install(s); nothing exceeds the {Math.Max(1, _options.BackupRetentionCount)}-install retention window."
                : $"{reclaimable.Count} install(s) are outside the {Math.Max(1, _options.BackupRetentionCount)}-install retention window " +
                  $"and would free {reclaimable.Sum(c => c.BackupBytes) / (1024.0 * 1024.0):F1} MB. " +
                  "Galbox does not delete backups automatically - deleting one can destroy the only copy of an original game file."
        };
    }

    /// <summary>
    /// Deletes one install's backup folder. Callers must have shown <see cref="PatchRetentionPlan"/> to the
    /// user first; protected installs (pending/failed) are refused.
    /// </summary>
    public void ApplyRetention(string gameRoot, string gameId, string installId)
    {
        var plan = PlanRetention(gameRoot, gameId);
        var candidate = plan.Reclaimable.FirstOrDefault(c => string.Equals(c.InstallId, installId, StringComparison.Ordinal))
                        ?? throw new InvalidOperationException(
                            $"Install '{installId}' is not reclaimable. It is either inside the retention window or protected (interrupted/failed).");

        if (candidate.IsProtected)
        {
            throw new InvalidOperationException($"Install '{installId}' is protected: {candidate.ProtectedReason}.");
        }

        var folder = _ledger.InstallFolder(gameId, installId);
        if (Directory.Exists(LongPath.Ensure(folder)))
        {
            Directory.Delete(LongPath.Ensure(folder), recursive: true);
        }
    }

    /// <summary>Total bytes currently held by the backup store.</summary>
    public long TotalSizeBytes()
        => Directory.Exists(LongPath.Ensure(_options.BackupRoot)) ? FolderSize(_options.BackupRoot) : 0;

    private static long FolderSize(string folder)
    {
        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(LongPath.Ensure(folder), "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(LongPath.Ensure(file)).Length; }
                catch (IOException) { /* a file that vanished mid-scan contributes nothing */ }
            }
        }
        catch (IOException)
        {
            // An unreadable folder is reported as 0 bytes; retention is advisory anyway.
        }
        return total;
    }
}
