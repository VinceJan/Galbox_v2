using System.IO;
using System.IO.Compression;
using System.Text;
using Galbox.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Galbox.App.Services;

/// <summary>
/// Implementation of save file management service.
/// Provides auto-detection, backup, and restoration of game save files.
/// </summary>
public class SaveManagementService : ISaveManagementService
{
    /// <summary>
    /// Creates a short-lived context per unit of work.
    ///
    /// This service is a Singleton, so it must never hold a DbContext: a captured context would
    /// live for the whole process and any two overlapping backup/restore operations would throw
    /// "A second operation was started on this context instance". The factory is the singleton
    /// that is safe to hold.
    /// </summary>
    private readonly IDbContextFactory<GalboxDbContext> _dbContextFactory;
    private readonly ILogger<SaveManagementService> _logger;

    private bool _autoBackupEnabled = true;
    private int _maxBackupsPerGame = 10;
    private readonly string _backupStoragePath;

    /// <summary>
    /// Creates a SaveManagementService with injected dependencies.
    /// </summary>
    /// <param name="dbContextFactory">Factory used to create a database context per operation</param>
    /// <param name="logger">Logger for diagnostics</param>
    public SaveManagementService(
        IDbContextFactory<GalboxDbContext> dbContextFactory,
        ILogger<SaveManagementService> logger)
    {
        _dbContextFactory = dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Initialize backup storage path
        var appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Galbox",
            "SaveBackups");
        _backupStoragePath = appDataPath;

        EnsureBackupDirectoryExists();
    }

    /// <inheritdoc/>
    public bool AutoBackupEnabled
    {
        get => _autoBackupEnabled;
        set => _autoBackupEnabled = value;
    }

    /// <inheritdoc/>
    public int MaxBackupsPerGame
    {
        get => _maxBackupsPerGame;
        set => _maxBackupsPerGame = Math.Max(1, value);
    }

    /// <inheritdoc/>
    public string BackupStoragePath => _backupStoragePath;

    /// <inheritdoc/>
    public Task<SaveLocationResult> DetectSaveLocationAsync(
        GameInfo game,
        CancellationToken cancellationToken = default)
    {
        if (game == null)
        {
            return Task.FromResult(new SaveLocationResult
            {
                Success = false,
                ErrorMessage = "Game information is null"
            });
        }

        cancellationToken.ThrowIfCancellationRequested();

        _logger.LogInformation(
            "Detecting save location for game: {GameName} (ID: {GameId})",
            game.DisplayName,
            game.Id);

        try
        {
            // First detect engine type
            var engineType = EngineSaveDetector.DetectEngineType(game);

            _logger.LogInformation(
                "Detected engine type: {EngineType} for game: {GameName}",
                engineType,
                game.DisplayName);

            // Then detect save location based on engine type
            var result = EngineSaveDetector.DetectSaveLocation(game, engineType);

            if (result.Success)
            {
                _logger.LogInformation(
                    "Save location detected: {SavePath} with {FileCount} save files",
                    result.PrimarySavePath ?? "Multiple locations",
                    result.SaveFiles.Count);
            }
            else
            {
                _logger.LogWarning(
                    "Save location detection failed for game: {GameName}. Reason: {ErrorMessage}",
                    game.DisplayName,
                    result.ErrorMessage ?? "No save files found");
            }

            return Task.FromResult(result);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Save location detection cancelled for game: {GameId}", game.Id);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error detecting save location for game: {GameName}",
                game.DisplayName);

            return Task.FromResult(new SaveLocationResult
            {
                Success = false,
                ErrorMessage = $"Error: {ex.Message}"
            });
        }
    }

    /// <inheritdoc/>
    public async Task<GameSaveBackup?> CreateBackupAsync(
        GameInfo game,
        string description,
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (game == null)
        {
            _logger.LogWarning("Cannot create backup: game is null");
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        _logger.LogInformation(
            "Creating backup for game: {GameName} (ID: {GameId})",
            game.DisplayName,
            game.Id);

        try
        {
            // Detect save location
            var saveLocation = await DetectSaveLocationAsync(game, cancellationToken).ConfigureAwait(false);

            if (!saveLocation.Success || string.IsNullOrEmpty(saveLocation.PrimarySavePath))
            {
                _logger.LogWarning(
                    "No save location found for game: {GameName}",
                    game.DisplayName);
                return null;
            }

            // Collect files to backup
            var filesToBackup = CollectFilesToBackup(saveLocation);

            if (filesToBackup.Count == 0)
            {
                _logger.LogWarning("No save files found to backup for game: {GameName}", game.DisplayName);
                return null;
            }

            var totalBytes = filesToBackup.Sum(f => f.Size);
            var totalFiles = filesToBackup.Count;

            // Create backup directory first to get the target path
            var backupDir = CreateBackupDirectory(game);
            var backupFileName = GenerateBackupFileName(game, DateTime.UtcNow);
            var backupFilePath = Path.Combine(backupDir, backupFileName);

            // Check disk space before proceeding
            if (!HasEnoughDiskSpace(backupFilePath, totalBytes))
            {
                _logger.LogError(
                    "Insufficient disk space for backup of game: {GameName}. Required: {RequiredBytes} bytes",
                    game.DisplayName,
                    totalBytes);
                return null;
            }

            // Report progress - scanning phase
            progress?.Report(new BackupProgress
            {
                Phase = BackupPhase.Scanning,
                Percentage = 0,
                TotalFiles = totalFiles,
                TotalBytes = totalBytes
            });

            // Report progress - copying files
            progress?.Report(new BackupProgress
            {
                Phase = BackupPhase.CopyingFiles,
                Percentage = 10,
                TotalFiles = totalFiles,
                TotalBytes = totalBytes
            });

            // Create zip backup. Entry names are relative to the save folder(s) so a restore puts
            // every file back where it came from.
            var entryRoots = new List<string>();
            if (!string.IsNullOrWhiteSpace(saveLocation.PrimarySavePath))
            {
                entryRoots.Add(saveLocation.PrimarySavePath);
            }

            entryRoots.AddRange(saveLocation.AlternativePaths.Where(p => !string.IsNullOrWhiteSpace(p)));

            await CreateZipBackupAsync(
                filesToBackup,
                entryRoots,
                backupFilePath,
                progress,
                totalFiles,
                totalBytes,
                cancellationToken).ConfigureAwait(false);

            // Report progress - finalizing
            progress?.Report(new BackupProgress
            {
                Phase = BackupPhase.Finalizing,
                Percentage = 95,
                FilesProcessed = totalFiles,
                TotalFiles = totalFiles,
                BytesTransferred = totalBytes,
                TotalBytes = totalBytes
            });

            // Create database record
            var backup = new GameSaveBackup
            {
                GameInfoId = game.Id,
                Name = GenerateBackupName(game, DateTime.UtcNow),
                BackupPath = backupFilePath,
                OriginalSavePath = saveLocation.PrimarySavePath,
                CreatedTime = DateTime.UtcNow,
                SizeBytes = GetFileSizeSafe(backupFilePath),
                Description = description
            };

            using (var db = _dbContextFactory.CreateDbContext())
            {
                db.SaveBackups.Add(backup);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            // Report progress - cleaning up
            progress?.Report(new BackupProgress
            {
                Phase = BackupPhase.CleaningUp,
                Percentage = 98
            });

            // Clean up old backups if exceeds max
            await CleanupOldBackupsAsync(game.Id, cancellationToken).ConfigureAwait(false);

            // Report completion
            progress?.Report(new BackupProgress
            {
                Phase = BackupPhase.Finalizing,
                Percentage = 100,
                FilesProcessed = totalFiles,
                TotalFiles = totalFiles,
                BytesTransferred = totalBytes,
                TotalBytes = totalBytes
            });

            _logger.LogInformation(
                "Backup created successfully: {BackupPath} ({Size} bytes)",
                backupFilePath,
                backup.SizeBytes);

            return backup;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Backup creation cancelled for game: {GameId}", game.Id);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error creating backup for game: {GameName}",
                game.DisplayName);
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task<GameSaveBackup?> CreateAutoBackupAsync(
        GameInfo game,
        CancellationToken cancellationToken = default)
    {
        if (game == null)
        {
            _logger.LogWarning("Cannot create auto-backup: game is null");
            return null;
        }

        if (!_autoBackupEnabled)
        {
            _logger.LogInformation("Auto-backup disabled, skipping for game: {GameId}", game.Id);
            return null;
        }

        var description = $"Auto backup before launch - {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}";
        return await CreateBackupAsync(game, description, null, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<bool> RestoreBackupAsync(
        int saveId,
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _logger.LogInformation("Restoring backup with ID: {SaveId}", saveId);

        // Temporary directory for atomic restore operation
        string? tempBackupDir = null;
        string? tempRestoreDir = null;

        // Rollback state, visible to the catch blocks.
        //
        //   * safetyBackup    - a real backup (zip + database row) of the live save folder, taken
        //                       BEFORE the first byte is overwritten. This is the authoritative
        //                       rollback source: it survives the finally block and the user can
        //                       find it in the backup list.
        //   * tempRestoreDir  - the copy of the live save folder inside the temporary directory,
        //                       used as a second source when the zip cannot be created.
        //
        // Before this change the two catch blocks logged "Attempting rollback after ..." and did
        // nothing else, while the finally block deleted the only copy of the previous save files.
        GameSaveBackup? safetyBackup = null;
        string? restorePathForRollback = null;
        var liveFileCountBeforeRestore = 0;

        try
        {
            // Get backup record from database
            // Include(b => b.GameInfo) is only needed while the context is alive, so the read is
            // materialised before the context is disposed.
            GameSaveBackup? backup;
            using (var db = _dbContextFactory.CreateDbContext())
            {
                backup = await db.SaveBackups
                    .Include(b => b.GameInfo)
                    .AsNoTracking()
                    .FirstOrDefaultAsync(b => b.Id == saveId, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (backup == null)
            {
                _logger.LogWarning("Backup not found with ID: {SaveId}", saveId);
                return false;
            }

            if (!File.Exists(backup.BackupPath))
            {
                _logger.LogWarning(
                    "Backup file not found: {BackupPath}",
                    backup.BackupPath);
                return false;
            }

            // Report progress - scanning
            progress?.Report(new BackupProgress
            {
                Phase = BackupPhase.Scanning,
                Percentage = 0
            });

            // Determine restore location
            var restorePath = backup.OriginalSavePath;
            if (string.IsNullOrEmpty(restorePath))
            {
                // Re-detect save location - ensure GameInfo is not null
                if (backup.GameInfo == null)
                {
                    _logger.LogWarning("Cannot determine restore location: GameInfo is null for backup ID: {SaveId}", saveId);
                    return false;
                }

                var saveLocation = await DetectSaveLocationAsync(backup.GameInfo, cancellationToken).ConfigureAwait(false);
                restorePath = saveLocation.PrimarySavePath;

                if (string.IsNullOrEmpty(restorePath))
                {
                    _logger.LogWarning("Cannot determine restore location for game: {GameId}", backup.GameInfoId);
                    return false;
                }
            }

            restorePathForRollback = restorePath;

            // Refuse to restore into the game installation folder: the rollback path deletes every
            // file in the restore folder before copying the previous state back, so doing that to
            // an installation root would destroy the game itself. (The detector refuses to produce
            // such a path, but a backup row written by an older build can still carry one.)
            if (backup.GameInfo != null && EngineSaveDetector.IsGameInstallRoot(restorePath, backup.GameInfo))
            {
                _logger.LogError(
                    "Refusing to restore backup {SaveId}: its restore path {RestorePath} is the game installation folder",
                    saveId,
                    restorePath);
                return false;
            }

            // Check for locked files in restore path before proceeding
            if (Directory.Exists(restorePath))
            {
                var lockedFiles = GetLockedFilesInDirectory(restorePath);
                if (lockedFiles.Count > 0)
                {
                    _logger.LogWarning(
                        "Cannot restore backup: {Count} files are locked in restore path: {RestorePath}. Locked files: {LockedFiles}",
                        lockedFiles.Count,
                        restorePath,
                        string.Join(", ", lockedFiles.Take(5)));
                    return false;
                }
            }

            // Report progress - creating directory
            progress?.Report(new BackupProgress
            {
                Phase = BackupPhase.CreatingDirectory,
                Percentage = 5
            });

            // Check disk space for restore operation
            var backupSize = GetFileSizeSafe(backup.BackupPath);
            if (!HasEnoughDiskSpace(restorePath, backupSize))
            {
                _logger.LogError(
                    "Insufficient disk space for restore operation. Required: {RequiredBytes} bytes",
                    backupSize);
                return false;
            }

            // Create temporary directories for atomic operation
            tempBackupDir = Path.Combine(
                _backupStoragePath,
                "TempRestore",
                $"backup_{saveId}_{DateTime.UtcNow:yyyyMMdd_HHmmss}");
            tempRestoreDir = Path.Combine(tempBackupDir, "original");

            Directory.CreateDirectory(tempRestoreDir);

            // Step 1: Backup current saves to temporary directory (if they exist)
            if (Directory.Exists(restorePath))
            {
                var currentFiles = EngineSaveDetector.GetFilesWithSizes(restorePath);
                liveFileCountBeforeRestore = currentFiles.Count;
                if (currentFiles.Count > 0)
                {
                    _logger.LogInformation(
                        "Backing up current saves to temporary directory: {TempDir}",
                        tempRestoreDir);

                    foreach (var (filePath, _) in currentFiles)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var fileInfo = new FileInfo(filePath);
                        if (!fileInfo.Exists)
                        {
                            continue;
                        }

                        var relativePath = filePath.Substring(restorePath.Length).TrimStart(Path.DirectorySeparatorChar);
                        var tempFilePath = Path.Combine(tempRestoreDir, relativePath);

                        var tempFileDir = Path.GetDirectoryName(tempFilePath);
                        if (!string.IsNullOrEmpty(tempFileDir) && !Directory.Exists(tempFileDir))
                        {
                            Directory.CreateDirectory(tempFileDir);
                        }

                        File.Copy(filePath, tempFilePath, true);
                    }

                    // The copy must be complete before a single live file is overwritten: an
                    // incomplete copy is not a rollback source.
                    var copiedFiles = Directory.Exists(tempRestoreDir)
                        ? Directory.GetFiles(tempRestoreDir, "*", SearchOption.AllDirectories).Length
                        : 0;

                    if (copiedFiles < currentFiles.Count)
                    {
                        _logger.LogError(
                            "Aborting restore of backup {SaveId}: the temporary copy of the current save is incomplete "
                            + "({Copied} of {Expected} files). The live save files were not touched.",
                            saveId,
                            copiedFiles,
                            currentFiles.Count);
                        return false;
                    }
                    else
                    {
                        // Step 1b: durable safety backup (zip + database row) of the live save
                        // folder. Restoring will overwrite these files, and the rollback below -
                        // as well as the user, afterwards - needs a copy that outlives this call.
                        safetyBackup = await CreatePreRestoreSafetyBackupAsync(
                            backup,
                            restorePath,
                            saveId,
                            cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            // Report progress - copying files
            progress?.Report(new BackupProgress
            {
                Phase = BackupPhase.CopyingFiles,
                Percentage = 10
            });

            // Ensure restore directory exists
            if (!Directory.Exists(restorePath))
            {
                Directory.CreateDirectory(restorePath);
            }

            // Step 2: Extract backup to restore location
            await ExtractZipBackupAsync(
                backup.BackupPath,
                restorePath,
                progress,
                cancellationToken).ConfigureAwait(false);

            // Step 3: Verify restore integrity
            progress?.Report(new BackupProgress
            {
                Phase = BackupPhase.Finalizing,
                Percentage = 95
            });

            var verificationResult = await VerifyRestoreIntegrityAsync(
                backup.BackupPath,
                restorePath,
                cancellationToken).ConfigureAwait(false);

            if (!verificationResult.Success)
            {
                _logger.LogError(
                    "Restore verification failed: {ErrorMessage}. Rolling back to the save state captured before this restore.",
                    verificationResult.ErrorMessage);

                // Step 4: Rollback - restore the save folder to the state it had before this call
                var rollback = await TryRollbackRestoreAsync(
                    safetyBackup,
                    tempRestoreDir,
                    restorePath,
                    $"verification failed ({verificationResult.ErrorMessage})",
                    CancellationToken.None).ConfigureAwait(false);

                if (!rollback.Success)
                {
                    _logger.LogError(
                        "Rollback after failed verification did NOT complete: {ErrorMessage}",
                        rollback.ErrorMessage);
                }

                return false;
            }

            // Report progress - finalizing
            progress?.Report(new BackupProgress
            {
                Phase = BackupPhase.Finalizing,
                Percentage = 100
            });

            _logger.LogInformation(
                "Backup restored successfully to: {RestorePath}",
                restorePath);

            return true;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Backup restoration cancelled for ID: {SaveId}", saveId);

            // Attempt rollback on cancellation. CancellationToken.None on purpose: the caller's
            // token is already cancelled, and the user's save files must be put back anyway.
            await TryRollbackRestoreAsync(
                safetyBackup,
                tempRestoreDir,
                restorePathForRollback,
                "the restore was cancelled",
                CancellationToken.None).ConfigureAwait(false);

            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error restoring backup with ID: {SaveId}",
                saveId);

            // Attempt rollback on error
            var rollback = await TryRollbackRestoreAsync(
                safetyBackup,
                tempRestoreDir,
                restorePathForRollback,
                $"the restore threw {ex.GetType().Name}",
                CancellationToken.None).ConfigureAwait(false);

            if (!rollback.Success)
            {
                _logger.LogError(
                    "Rollback after restore error did NOT complete: {ErrorMessage}",
                    rollback.ErrorMessage);
            }

            return false;
        }
        finally
        {
            // Step 5: Clean up temporary directory
            if (tempBackupDir != null && Directory.Exists(tempBackupDir))
            {
                try
                {
                    Directory.Delete(tempBackupDir, true);
                    _logger.LogInformation("Cleaned up temporary restore directory: {TempDir}", tempBackupDir);
                }
                catch (Exception cleanupEx)
                {
                    _logger.LogWarning(
                        cleanupEx,
                        "Could not clean up temporary restore directory: {TempDir}",
                        tempBackupDir);
                }
            }
        }
    }

    /// <summary>
    /// Verifies a restore by comparing the CONTENT of every archive entry with the file that was
    /// written to disk (SHA-256, exact size, exact file count).
    /// </summary>
    /// <remarks>
    /// The previous implementation compared only file counts and the sum of sizes, with a 20%
    /// tolerance on the count and a 10% on the size - a restore that dropped a fifth of the save
    /// files, or that wrote garbage of the right length, was reported as verified. Both sides of
    /// this comparison are produced by this service (it wrote the archive and it extracted it),
    /// so there is no reason for any tolerance: every entry must be present, byte for byte.
    /// </remarks>
    private async Task<RestoreVerificationResult> VerifyRestoreIntegrityAsync(
        string backupPath,
        string restorePath,
        CancellationToken cancellationToken)
    {
        var result = new RestoreVerificationResult();

        try
        {
            using var zipArchive = ZipFile.OpenRead(backupPath);

            // Entry names are unique per archive (CreateZipBackupAsync de-duplicates them). A
            // duplicate name in a legacy archive is extracted last-wins, so it is compared the
            // same way, but reported because it means the archive cannot represent both files.
            var expected = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            var duplicateEntryNames = new List<string>();

            foreach (var entry in zipArchive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (string.IsNullOrEmpty(entry.Name))
                {
                    continue; // directory entry
                }

                var relativePath = NormalizeArchiveEntryPath(entry.FullName);
                if (relativePath.Length == 0)
                {
                    result.ErrorMessage = $"Archive entry '{entry.FullName}' has an unusable name.";
                    return result;
                }

                if (!expected.TryAdd(relativePath, entry))
                {
                    duplicateEntryNames.Add(relativePath);
                    expected[relativePath] = entry;
                }
            }

            if (expected.Count == 0)
            {
                result.ErrorMessage = "Backup archive contains no file entries.";
                return result;
            }

            result.ExpectedFileCount = expected.Count;
            result.ExpectedTotalSize = expected.Values.Sum(e => e.Length);

            foreach (var (relativePath, entry) in expected)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var restoredFile = Path.Combine(restorePath, relativePath);
                if (!File.Exists(restoredFile))
                {
                    result.ErrorMessage = $"Restored file is missing: '{relativePath}'.";
                    return result;
                }

                var actualSize = new FileInfo(restoredFile).Length;
                if (actualSize != entry.Length)
                {
                    result.ErrorMessage =
                        $"Size mismatch for '{relativePath}': archive has {entry.Length} bytes, restored file has {actualSize} bytes.";
                    return result;
                }

                var expectedHash = await ComputeEntrySha256Async(entry, cancellationToken).ConfigureAwait(false);
                var actualHash = await ComputeFileSha256Async(restoredFile, cancellationToken).ConfigureAwait(false);

                if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
                {
                    result.ErrorMessage =
                        $"Content hash mismatch for '{relativePath}': archive {expectedHash}, restored file {actualHash}.";
                    return result;
                }
            }

            result.Success = true;
            result.VerifiedFileCount = expected.Count;
            result.VerifiedTotalSize = expected.Values.Sum(e => e.Length);
            result.DuplicateEntryNames = duplicateEntryNames;

            if (duplicateEntryNames.Count > 0)
            {
                _logger.LogWarning(
                    "Restored backup {BackupPath} contains {Count} duplicate archive entry name(s) (last entry wins): {Names}",
                    backupPath,
                    duplicateEntryNames.Count,
                    string.Join(", ", duplicateEntryNames.Take(5)));
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error verifying restore integrity");
            result.ErrorMessage = $"Verification error: {ex.Message}";
            return result;
        }
    }

    /// <summary>
    /// Normalises an archive entry name to a relative Windows path: separators unified, leading
    /// separators removed, and any traversal segment rejected (empty string means "unusable").
    /// </summary>
    private static string NormalizeArchiveEntryPath(string entryFullName)
    {
        if (string.IsNullOrWhiteSpace(entryFullName))
        {
            return string.Empty;
        }

        var parts = entryFullName
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
            .Where(part => part != ".")
            .ToArray();

        if (parts.Length == 0 || parts.Any(part => part == ".."))
        {
            return string.Empty;
        }

        return Path.Combine(parts);
    }

    /// <summary>SHA-256 of an archive entry's content, as an uppercase hex string.</summary>
    private static async Task<string> ComputeEntrySha256Async(ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        using var stream = entry.Open();
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    /// <summary>SHA-256 of a file on disk, as an uppercase hex string.</summary>
    private static async Task<string> ComputeFileSha256Async(string filePath, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            useAsync: true);

        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// Writes a zip copy of the current save folder plus its database record, before the restore
    /// overwrites that folder.
    /// </summary>
    /// <remarks>
    /// The record matters as much as the file: without it the copy would be an orphan on disk, the
    /// user would never see it in the backup list, and nothing would be able to find it again.
    /// </remarks>
    private async Task<GameSaveBackup?> CreatePreRestoreSafetyBackupAsync(
        GameSaveBackup targetBackup,
        string restorePath,
        int targetSaveId,
        CancellationToken cancellationToken)
    {
        try
        {
            var files = EngineSaveDetector.GetFilesWithSizes(restorePath);
            if (files.Count == 0)
            {
                return null;
            }

            var game = targetBackup.GameInfo;
            var gameId = targetBackup.GameInfoId;
            var gameName = game?.DisplayName ?? $"Game {gameId}";

            var gameBackupDir = Path.Combine(_backupStoragePath, $"Game_{gameId}_{SanitizeFileName(gameName)}");
            Directory.CreateDirectory(gameBackupDir);

            var timestamp = DateTime.UtcNow;
            var zipPath = Path.Combine(gameBackupDir, $"prerestore_{timestamp:yyyyMMdd_HHmmss}.zip");

            // A collision would overwrite a previous safety copy; keep trying until the name is free.
            var suffix = 1;
            while (File.Exists(zipPath))
            {
                zipPath = Path.Combine(
                    gameBackupDir,
                    $"prerestore_{timestamp:yyyyMMdd_HHmmss}_{suffix++}.zip");
            }

            await CreateZipBackupCoreAsync(
                files,
                new[] { restorePath },
                zipPath,
                null,
                cancellationToken).ConfigureAwait(false);

            var sizeBytes = GetFileSizeSafe(zipPath);
            if (sizeBytes <= 0)
            {
                _logger.LogError("Pre-restore safety backup produced an empty file: {ZipPath}", zipPath);
                return null;
            }

            var record = new GameSaveBackup
            {
                GameInfoId = gameId,
                Name = $"恢复前自动备份 - {timestamp:yyyy-MM-dd HH:mm:ss}",
                BackupPath = zipPath,
                OriginalSavePath = restorePath,
                CreatedTime = timestamp,
                SizeBytes = sizeBytes,
                Description = $"Safety copy of the current save, taken automatically before restoring backup #{targetSaveId}"
            };

            using (var db = _dbContextFactory.CreateDbContext())
            {
                db.SaveBackups.Add(record);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            _logger.LogInformation(
                "Pre-restore safety backup created: ID {BackupId}, {FileCount} file(s), {SizeBytes} bytes at {ZipPath}",
                record.Id,
                files.Count,
                sizeBytes,
                zipPath);

            // Give the user's backup list a chance to stay bounded, but never let housekeeping
            // break a restore.
            try
            {
                await CleanupOldBackupsAsync(gameId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception cleanupEx)
            {
                _logger.LogWarning(cleanupEx, "Could not clean up old backups after taking a pre-restore safety backup");
            }

            return record;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The temporary copy taken in step 1 is still a valid rollback source, so this is a
            // degraded - not fatal - condition. It is logged loudly because the user loses the
            // visible, durable copy they would otherwise have.
            _logger.LogError(
                ex,
                "Could not create a durable pre-restore safety backup for backup {SaveId}; "
                + "falling back to the temporary copy for rollback",
                targetSaveId);
            return null;
        }
    }

    /// <summary>
    /// Puts the save folder back into the state it had before a failed or cancelled restore.
    /// </summary>
    /// <returns>A result describing whether the previous state was restored.</returns>
    private async Task<RollbackResult> TryRollbackRestoreAsync(
        GameSaveBackup? safetyBackup,
        string? tempRestoreDir,
        string? restorePath,
        string reason,
        CancellationToken cancellationToken)
    {
        var result = new RollbackResult();

        if (string.IsNullOrWhiteSpace(restorePath) || !Directory.Exists(restorePath))
        {
            result.ErrorMessage = "nothing to roll back: the restore path is unknown or does not exist";
            _logger.LogWarning("Rollback skipped: {Reason}", result.ErrorMessage);
            return result;
        }

        _logger.LogInformation("Rolling back restore of '{RestorePath}' because {Reason}", restorePath, reason);

        // Preferred source: the durable zip taken before the overwrite. It is byte-exact and it
        // also lets us delete the files the failed restore created.
        if (safetyBackup != null && !string.IsNullOrWhiteSpace(safetyBackup.BackupPath) && File.Exists(safetyBackup.BackupPath))
        {
            try
            {
                var restoredCount = await RestoreFromSafetyArchiveAsync(
                    safetyBackup.BackupPath,
                    restorePath,
                    cancellationToken).ConfigureAwait(false);

                result.Success = true;
                result.Source = RollbackSource.SafetyBackup;
                result.RestoredFileCount = restoredCount;

                _logger.LogInformation(
                    "Rollback from safety backup {BackupId} completed: {FileCount} file(s) restored",
                    safetyBackup.Id,
                    restoredCount);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Rollback from the safety backup failed; falling back to the temporary copy");
                result.ErrorMessage = $"safety-backup rollback failed: {ex.Message}";
            }
        }

        // Fallback source: the copy made in step 1 of the restore.
        if (!string.IsNullOrWhiteSpace(tempRestoreDir) && Directory.Exists(tempRestoreDir))
        {
            await RollbackRestoreAsync(tempRestoreDir, restorePath, cancellationToken).ConfigureAwait(false);

            var restoredCount = Directory.GetFiles(tempRestoreDir, "*", SearchOption.AllDirectories).Length;

            result.Success = restoredCount > 0;
            result.Source = RollbackSource.TemporaryCopy;
            result.RestoredFileCount = restoredCount;
            result.ErrorMessage = result.Success ? null : "the temporary rollback copy contained no files";

            return result;
        }

        result.ErrorMessage ??= "no rollback source was available (neither a safety backup nor a temporary copy)";
        _logger.LogError(
            "Rollback of '{RestorePath}' could NOT be performed: {ErrorMessage}",
            restorePath,
            result.ErrorMessage);

        return result;
    }

    /// <summary>
    /// Restores the save folder from the pre-restore safety archive: files that the failed restore
    /// created are removed, every archived file is written back, and the result is hash-verified.
    /// </summary>
    private async Task<int> RestoreFromSafetyArchiveAsync(
        string safetyZipPath,
        string restorePath,
        CancellationToken cancellationToken)
    {
        using var zipArchive = ZipFile.OpenRead(safetyZipPath);

        var archivedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zipArchive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            var relativePath = NormalizeArchiveEntryPath(entry.FullName);
            if (relativePath.Length == 0)
            {
                continue;
            }

            archivedFiles.Add(relativePath);
        }

        // 1. Remove files that are not part of the archived state - they were created by the failed
        //    restore (or by the partial extraction) and must not survive the rollback.
        var removedCount = 0;
        foreach (var file in Directory.GetFiles(restorePath, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativePath = Path.GetRelativePath(restorePath, file);
            if (archivedFiles.Contains(relativePath))
            {
                continue;
            }

            try
            {
                File.Delete(file);
                removedCount++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not delete file created by the failed restore: {FilePath}", file);
            }
        }

        // 2. Write every archived file back, overwriting whatever the failed restore left behind.
        var restoredCount = 0;
        foreach (var entry in zipArchive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativePath = NormalizeArchiveEntryPath(entry.FullName);
            if (relativePath.Length == 0)
            {
                continue;
            }

            var targetPath = Path.Combine(restorePath, relativePath);
            var targetDir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            using (var entryStream = entry.Open())
            using (var fileStream = File.Create(targetPath))
            {
                await entryStream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
            }

            // 3. Verify each restored file against the archive: a rollback that silently writes
            //    something else is worse than reporting failure.
            var expectedHash = await ComputeEntrySha256Async(entry, cancellationToken).ConfigureAwait(false);
            var actualHash = await ComputeFileSha256Async(targetPath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException(
                    $"Rollback verification failed for '{relativePath}': expected {expectedHash}, got {actualHash}");
            }

            restoredCount++;
        }

        _logger.LogInformation(
            "Rollback wrote {Restored} file(s) back and removed {Removed} file(s) created by the failed restore",
            restoredCount,
            removedCount);

        return restoredCount;
    }

    /// <summary>Which source a rollback used.</summary>
    private enum RollbackSource
    {
        None,
        SafetyBackup,
        TemporaryCopy
    }

    /// <summary>Outcome of a rollback attempt.</summary>
    private sealed class RollbackResult
    {
        public bool Success { get; set; }
        public RollbackSource Source { get; set; }
        public int RestoredFileCount { get; set; }
        public string? ErrorMessage { get; set; }
    }

    private sealed class RestoreVerificationResult
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public int FileCount { get; set; }
        public long TotalSize { get; set; }
        public int ExpectedFileCount { get; set; }
        public long ExpectedTotalSize { get; set; }
        public int VerifiedFileCount { get; set; }
        public long VerifiedTotalSize { get; set; }
        public List<string> DuplicateEntryNames { get; set; } = new();
    }

    /// <summary>
    /// Rolls back a restore operation by copying files from temporary backup.
    /// </summary>
    private Task RollbackRestoreAsync(
        string tempRestoreDir,
        string restorePath,
        CancellationToken cancellationToken)
    {
        try
        {
            // Clear current restore directory
            if (Directory.Exists(restorePath))
            {
                var currentFiles = Directory.GetFiles(restorePath, "*", SearchOption.AllDirectories);
                foreach (var file in currentFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        File.Delete(file);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Could not delete file during rollback: {FilePath}", file);
                    }
                }
            }

            // Restore from temporary backup
            if (Directory.Exists(tempRestoreDir))
            {
                var tempFiles = Directory.GetFiles(tempRestoreDir, "*", SearchOption.AllDirectories);
                foreach (var tempFile in tempFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var relativePath = tempFile.Substring(tempRestoreDir.Length).TrimStart(Path.DirectorySeparatorChar);
                    var targetPath = Path.Combine(restorePath, relativePath);

                    var targetDir = Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
                    {
                        Directory.CreateDirectory(targetDir);
                    }

                    File.Copy(tempFile, targetPath, true);
                }
            }

            _logger.LogInformation("Rollback completed successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during rollback operation");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task<List<GameSaveBackup>> GetSaveBackupsAsync(
        int gameId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            List<GameSaveBackup> backups;
            using (var db = _dbContextFactory.CreateDbContext())
            {
                backups = await db.SaveBackups
                    .Where(b => b.GameInfoId == gameId)
                    .AsNoTracking()
                    .OrderByDescending(b => b.CreatedTime)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            _logger.LogInformation(
                "Retrieved {Count} backups for game: {GameId}",
                backups.Count,
                gameId);

            return backups;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Get backups cancelled for game: {GameId}", gameId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error getting backups for game: {GameId}",
                gameId);
            return new List<GameSaveBackup>();
        }
    }

    /// <inheritdoc/>
    public async Task<bool> DeleteBackupAsync(
        int saveId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _logger.LogInformation("Deleting backup with ID: {SaveId}", saveId);

        try
        {
            GameSaveBackup? backup;
            using (var db = _dbContextFactory.CreateDbContext())
            {
                backup = await db.SaveBackups
                    .FirstOrDefaultAsync(b => b.Id == saveId, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (backup == null)
            {
                _logger.LogWarning("Backup not found with ID: {SaveId}", saveId);
                return false;
            }

            // Delete backup file
            if (File.Exists(backup.BackupPath))
            {
                File.Delete(backup.BackupPath);
                _logger.LogInformation("Deleted backup file: {BackupPath}", backup.BackupPath);
            }

            // Delete database record
            using (var db = _dbContextFactory.CreateDbContext())
            {
                db.SaveBackups.Attach(backup);
                db.SaveBackups.Remove(backup);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            _logger.LogInformation("Backup deleted successfully: ID {SaveId}", saveId);
            return true;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Delete backup cancelled for ID: {SaveId}", saveId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error deleting backup with ID: {SaveId}",
                saveId);
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<QuickSwitchResult> QuickSwitchSaveAsync(
        int gameId,
        int targetSaveId,
        CancellationToken cancellationToken = default)
    {
        var result = new QuickSwitchResult();

        cancellationToken.ThrowIfCancellationRequested();

        _logger.LogInformation(
            "Quick switching save for game: {GameId} to backup: {TargetSaveId}",
            gameId,
            targetSaveId);

        try
        {
            // Get game info
            GameInfo? game;
            GameSaveBackup? targetBackup;

            using (var db = _dbContextFactory.CreateDbContext())
            {
                game = await db.Games
                    .AsNoTracking()
                    .FirstOrDefaultAsync(g => g.Id == gameId, cancellationToken)
                    .ConfigureAwait(false);

                if (game == null)
                {
                    result.ErrorMessage = "Game not found";
                    _logger.LogWarning("Game not found with ID: {GameId}", gameId);
                    return result;
                }

                // Get target backup
                targetBackup = await db.SaveBackups
                    .AsNoTracking()
                    .FirstOrDefaultAsync(b => b.Id == targetSaveId && b.GameInfoId == gameId, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (targetBackup == null)
            {
                result.ErrorMessage = "Target backup not found";
                _logger.LogWarning("Target backup not found with ID: {TargetSaveId}", targetSaveId);
                return result;
            }

            // Create backup of current saves first.
            //
            // This backup is the ONLY way back: the restore below overwrites the live save files.
            // When it fails (no save location detected, unreadable files, no disk space) the
            // previous code carried on regardless and reported success, so the user's current
            // save was silently destroyed with no copy anywhere. Abort instead.
            GameSaveBackup? currentBackup;
            try
            {
                currentBackup = await CreateBackupAsync(
                    game,
                    $"Quick switch backup - {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}",
                    null,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Quick switch aborted: creating the safety backup of the current save failed for game {GameId}", gameId);
                result.ErrorMessage =
                    "已中止快速切换：无法为当前存档创建安全备份，为避免覆盖后无法回退，未做任何修改。"
                    + $"原因：{ex.Message}";
                return result;
            }

            result.CurrentBackup = currentBackup;

            if (currentBackup == null)
            {
                // CreateBackupAsync returns null when it could not produce a backup file (or a
                // database record for it). Continuing here is what lost saves.
                _logger.LogError(
                    "Quick switch aborted for game {GameId}: the safety backup of the current save could not be created "
                    + "(no save location detected or no save file found). The target backup {TargetSaveId} was NOT applied.",
                    gameId,
                    targetSaveId);

                result.ErrorMessage =
                    "已中止快速切换：未能为当前存档创建安全备份（未检测到存档位置或存档文件），"
                    + "为避免覆盖后无法回退，目标备份未被应用，当前存档保持原样。";
                return result;
            }

            // A backup record without a file on disk is just as unusable as no backup at all.
            if (string.IsNullOrWhiteSpace(currentBackup.BackupPath) || !File.Exists(currentBackup.BackupPath))
            {
                _logger.LogError(
                    "Quick switch aborted for game {GameId}: the safety backup record {BackupId} has no readable file at {BackupPath}",
                    gameId,
                    currentBackup.Id,
                    currentBackup.BackupPath);

                result.ErrorMessage =
                    "已中止快速切换：当前存档的安全备份文件不可读，未做任何修改。";
                return result;
            }

            // Restore target backup
            var restoreSuccess = await RestoreBackupAsync(
                targetSaveId,
                null,
                cancellationToken).ConfigureAwait(false);

            if (restoreSuccess)
            {
                result.RestoredBackup = targetBackup;
                result.Success = true;

                _logger.LogInformation(
                    "Quick switch completed: current backup ID {CurrentId}, restored backup ID {TargetId}",
                    currentBackup.Id,
                    targetSaveId);
            }
            else
            {
                // The restore either aborted before touching the save files or rolled itself back
                // to the state it captured first, so the safety backup above is intact.
                result.ErrorMessage =
                    $"已从备份 #{targetSaveId} 恢复失败，本次操作已回滚；恢复前的存档仍保存在安全备份 #{currentBackup.Id} 中。";
                _logger.LogWarning(
                    "Quick switch failed: could not restore target backup {TargetSaveId} for game {GameId}; "
                    + "the pre-switch state is preserved in safety backup {CurrentId}",
                    targetSaveId,
                    gameId,
                    currentBackup.Id);
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Quick switch cancelled for game: {GameId}", gameId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error during quick switch for game: {GameId}",
                gameId);
            result.ErrorMessage = $"Error: {ex.Message}";
            return result;
        }
    }

    /// <inheritdoc/>
    public async Task<SaveMetadata?> GetSaveMetadataAsync(
        string savePath,
        GameEngineType engineType,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrEmpty(savePath) || !Directory.Exists(savePath))
        {
            return null;
        }

        try
        {
            var metadata = new SaveMetadata();
            var files = EngineSaveDetector.GetFilesWithSizes(savePath);

            metadata.FileCount = files.Count;
            metadata.TotalSizeBytes = files.Sum(f => f.Size);

            // Get creation and modification times
            if (files.Count > 0)
            {
                var creationTimes = files.Select(f => File.GetCreationTime(f.Path)).Where(t => t > DateTime.MinValue);
                var modificationTimes = files.Select(f => File.GetLastWriteTime(f.Path)).Where(t => t > DateTime.MinValue);

                metadata.CreationTime = creationTimes.Any() ? creationTimes.Min() : null;
                metadata.LastModifiedTime = modificationTimes.Any() ? modificationTimes.Max() : null;
            }

            // Try to extract engine-specific metadata
            metadata = await ExtractEngineMetadataAsync(savePath, engineType, metadata, cancellationToken).ConfigureAwait(false);

            return metadata;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting save metadata for path: {SavePath}", savePath);
            return null;
        }
    }

    #region Private Helper Methods

    private static string[] SafeGetFiles(string path, string pattern, SearchOption searchOption)
    {
        try
        {
            return Directory.GetFiles(path, pattern, searchOption);
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
        catch (DirectoryNotFoundException)
        {
            return Array.Empty<string>();
        }
        catch (IOException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Checks if there is enough disk space for the backup operation.
    /// </summary>
    /// <param name="targetPath">Target path for backup</param>
    /// <param name="requiredBytes">Estimated bytes needed</param>
    /// <returns>True if sufficient space available</returns>
    private bool HasEnoughDiskSpace(string targetPath, long requiredBytes)
    {
        try
        {
            var pathRoot = Path.GetPathRoot(targetPath);
            if (string.IsNullOrEmpty(pathRoot))
            {
                _logger.LogWarning("Could not determine drive root for path: {TargetPath}", targetPath);
                return true; // Assume sufficient space if we can't check
            }

            var driveInfo = new DriveInfo(pathRoot);
            var bufferBytes = 100L * 1024 * 1024; // 100MB buffer
            var hasEnoughSpace = driveInfo.AvailableFreeSpace > requiredBytes + bufferBytes;

            if (!hasEnoughSpace)
            {
                _logger.LogWarning(
                    "Insufficient disk space on drive {Drive}. Available: {Available} bytes, Required: {Required} bytes",
                    pathRoot,
                    driveInfo.AvailableFreeSpace,
                    requiredBytes + bufferBytes);
            }

            return hasEnoughSpace;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error checking disk space for path: {TargetPath}", targetPath);
            return true; // Assume sufficient space if check fails
        }
    }

    /// <summary>
    /// Checks if a file is locked by another process.
    /// </summary>
    /// <param name="filePath">Path to check</param>
    /// <returns>True if file is locked</returns>
    private bool IsFileLocked(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return false;
        }

        try
        {
            using var stream = File.Open(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// Checks if any files in a directory are locked.
    /// </summary>
    /// <param name="directoryPath">Directory to check</param>
    /// <returns>List of locked file paths</returns>
    private List<string> GetLockedFilesInDirectory(string directoryPath)
    {
        var lockedFiles = new List<string>();

        if (!Directory.Exists(directoryPath))
        {
            return lockedFiles;
        }

        try
        {
            var files = Directory.GetFiles(directoryPath, "*", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                if (IsFileLocked(file))
                {
                    lockedFiles.Add(file);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error scanning directory for locked files: {DirectoryPath}", directoryPath);
        }

        return lockedFiles;
    }

    private void EnsureBackupDirectoryExists()
    {
        try
        {
            if (!Directory.Exists(_backupStoragePath))
            {
                Directory.CreateDirectory(_backupStoragePath);
                _logger.LogInformation("Created backup storage directory: {BackupPath}", _backupStoragePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create backup storage directory: {BackupPath}", _backupStoragePath);
            throw;
        }
    }

    private long GetFileSizeSafe(string filePath)
    {
        try
        {
            var fileInfo = new FileInfo(filePath);
            return fileInfo.Exists ? fileInfo.Length : 0;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not get file size for: {FilePath}", filePath);
            return 0;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Access denied when getting file size for: {FilePath}", filePath);
            return 0;
        }
    }

    private string CreateBackupDirectory(GameInfo game)
    {
        var gameBackupDir = Path.Combine(
            _backupStoragePath,
            $"Game_{game.Id}_{SanitizeFileName(game.DisplayName)}");

        if (!Directory.Exists(gameBackupDir))
        {
            Directory.CreateDirectory(gameBackupDir);
        }

        return gameBackupDir;
    }

    private static string SanitizeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return "Unknown";
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new StringBuilder(fileName);

        foreach (var invalidChar in invalidChars)
        {
            sanitized.Replace(invalidChar, '_');
        }

        // Limit length
        if (sanitized.Length > 50)
        {
            sanitized.Length = 50;
        }

        return sanitized.ToString();
    }

    private static string GenerateBackupFileName(GameInfo game, DateTime timestamp)
    {
        return $"backup_{timestamp:yyyyMMdd_HHmmss}.zip";
    }

    private static string GenerateBackupName(GameInfo game, DateTime timestamp)
    {
        return $"{game.DisplayName} - {timestamp:yyyy-MM-dd HH:mm:ss}";
    }

    private static List<(string Path, long Size)> CollectFilesToBackup(SaveLocationResult saveLocation)
    {
        var files = new List<(string Path, long Size)>();

        // Collect from primary path
        if (!string.IsNullOrEmpty(saveLocation.PrimarySavePath) && Directory.Exists(saveLocation.PrimarySavePath))
        {
            files.AddRange(EngineSaveDetector.GetFilesWithSizes(saveLocation.PrimarySavePath));
        }

        // Collect from alternative paths (optional, based on user preference)
        foreach (var altPath in saveLocation.AlternativePaths)
        {
            if (Directory.Exists(altPath))
            {
                files.AddRange(EngineSaveDetector.GetFilesWithSizes(altPath));
            }
        }

        // Also include individual save files detected
        foreach (var saveFile in saveLocation.SaveFiles)
        {
            if (File.Exists(saveFile) && !files.Any(f => f.Path == saveFile))
            {
                files.Add((saveFile, new FileInfo(saveFile).Length));
            }
        }

        return files;
    }

    private Task CreateZipBackupAsync(
        List<(string Path, long Size)> files,
        IReadOnlyList<string> entryRoots,
        string zipPath,
        IProgress<BackupProgress>? progress,
        int totalFiles,
        long totalBytes,
        CancellationToken cancellationToken)
    {
        return CreateZipBackupCoreAsync(files, entryRoots, zipPath, progress, cancellationToken, totalFiles, totalBytes);
    }

    /// <summary>
    /// Writes <paramref name="files"/> into a zip, using each file's path relative to the first
    /// matching root in <paramref name="entryRoots"/> as the entry name.
    /// </summary>
    /// <remarks>
    /// The previous naming scheme used the file's immediate parent directory
    /// (<c>saves\1-1-LT1.save</c> for <c>&lt;install&gt;\game\saves\1-1-LT1.save</c>), so restoring
    /// into <c>&lt;install&gt;\game\saves</c> wrote the file to
    /// <c>&lt;install&gt;\game\saves\saves\1-1-LT1.save</c> - next to, not over, the file it was
    /// meant to replace. Relative names also make each entry unique, which is what the content
    /// verification in <see cref="VerifyRestoreIntegrityAsync"/> relies on.
    /// </remarks>
    private async Task CreateZipBackupCoreAsync(
        List<(string Path, long Size)> files,
        IReadOnlyList<string> entryRoots,
        string zipPath,
        IProgress<BackupProgress>? progress,
        CancellationToken cancellationToken,
        int totalFiles = 0,
        long totalBytes = 0)
    {
        long bytesTransferred = 0;
        int filesProcessed = 0;

        using var zipArchive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        var usedEntryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (filePath, _) in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fileInfo = new FileInfo(filePath);
            if (!fileInfo.Exists)
            {
                continue;
            }

            var entryName = BuildBackupEntryName(filePath, entryRoots, usedEntryNames);

            var entry = zipArchive.CreateEntry(entryName);

            using var entryStream = entry.Open();
            using var fileStream = fileInfo.OpenRead();

            await fileStream.CopyToAsync(entryStream, cancellationToken).ConfigureAwait(false);

            bytesTransferred += fileInfo.Length;
            filesProcessed++;

            // Report progress (10% to 90% range for copying)
            // Fix for potential divide-by-zero: use file count when totalBytes is 0
            var percentage = totalBytes > 0
                ? 10 + (int)((bytesTransferred / (double)totalBytes) * 80)
                : 10 + (int)((filesProcessed / (double)Math.Max(totalFiles, 1)) * 80);
            progress?.Report(new BackupProgress
            {
                Phase = BackupPhase.CopyingFiles,
                Percentage = Math.Min(percentage, 90),
                FilesProcessed = filesProcessed,
                TotalFiles = totalFiles,
                BytesTransferred = bytesTransferred,
                TotalBytes = totalBytes,
                CurrentFile = filePath
            });
        }
    }

    /// <summary>
    /// Builds the archive entry name for a file: its path relative to the deepest matching root,
    /// with separators unified and collisions resolved by a numeric suffix.
    /// </summary>
    private static string BuildBackupEntryName(
        string filePath,
        IReadOnlyList<string> entryRoots,
        HashSet<string> usedEntryNames)
    {
        var candidate = string.Empty;

        foreach (var root in entryRoots
                     .Where(r => !string.IsNullOrWhiteSpace(r))
                     .OrderByDescending(r => r.Length))
        {
            var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (filePath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || filePath.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                candidate = Path.GetRelativePath(normalizedRoot, filePath);
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(candidate))
        {
            candidate = Path.GetFileName(filePath);
        }

        candidate = candidate
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);

        if (candidate.Length == 0)
        {
            candidate = Path.GetFileName(filePath);
        }

        // Two files from different roots can map to the same relative name; a duplicate entry name
        // would make one of them unreachable, so disambiguate.
        var unique = candidate;
        var suffix = 1;
        var extension = Path.GetExtension(candidate);
        var withoutExtension = candidate[..^extension.Length];

        while (!usedEntryNames.Add(unique))
        {
            unique = $"{withoutExtension}_{suffix++}{extension}";
        }

        return unique;
    }

    private async Task ExtractZipBackupAsync(
        string zipPath,
        string extractPath,
        IProgress<BackupProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var zipArchive = ZipFile.OpenRead(zipPath);
        var totalEntries = zipArchive.Entries.Count;

        int entriesProcessed = 0;

        foreach (var entry in zipArchive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Normalise the entry name and refuse anything that would resolve outside the restore
            // folder (a "../" entry in a tampered archive would otherwise overwrite arbitrary
            // files). Legacy archives are normalised the same way, so "dir/file" still lands in
            // the right place.
            var relativePath = NormalizeArchiveEntryPath(entry.FullName);
            if (relativePath.Length == 0)
            {
                if (string.IsNullOrEmpty(entry.Name))
                {
                    continue; // directory entry
                }

                throw new IOException($"Unsafe archive entry name: '{entry.FullName}'");
            }

            var entryPath = Path.Combine(extractPath, relativePath);

            var fullExtractPath = Path.GetFullPath(extractPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var fullEntryPath = Path.GetFullPath(entryPath);
            if (!fullEntryPath.StartsWith(fullExtractPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException(
                    $"Archive entry '{entry.FullName}' resolves outside the restore folder: '{fullEntryPath}'");
            }

            // Ensure directory exists
            var entryDir = Path.GetDirectoryName(entryPath);
            if (!string.IsNullOrEmpty(entryDir) && !Directory.Exists(entryDir))
            {
                Directory.CreateDirectory(entryDir);
            }

            // Extract file
            if (!string.IsNullOrEmpty(entry.Name)) // Skip directory entries
            {
                using var entryStream = entry.Open();
                using var fileStream = File.Create(entryPath);
                await entryStream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
            }

            entriesProcessed++;

            // Report progress (10% to 95% range)
            var percentage = 10 + (int)((entriesProcessed / (double)totalEntries) * 85);
            progress?.Report(new BackupProgress
            {
                Phase = BackupPhase.CopyingFiles,
                Percentage = Math.Min(percentage, 95),
                CurrentFile = entry.FullName
            });
        }
    }

    private async Task CleanupOldBackupsAsync(int gameId, CancellationToken cancellationToken)
    {
        try
        {
            using var db = _dbContextFactory.CreateDbContext();

            var backups = await db.SaveBackups
                .Where(b => b.GameInfoId == gameId)
                .OrderByDescending(b => b.CreatedTime)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            if (backups.Count <= _maxBackupsPerGame)
            {
                return;
            }

            var backupsToDelete = backups.Skip(_maxBackupsPerGame).ToList();

            // Use transaction for atomic cleanup
            using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                foreach (var backup in backupsToDelete)
                {
                    // Delete file first
                    if (File.Exists(backup.BackupPath))
                    {
                        try
                        {
                            File.Delete(backup.BackupPath);
                            _logger.LogInformation("Deleted old backup file: {BackupPath}", backup.BackupPath);
                        }
                        catch (IOException ex)
                        {
                            _logger.LogWarning(ex, "Could not delete backup file: {BackupPath}", backup.BackupPath);
                            // Continue with database deletion even if file deletion fails
                        }
                    }

                    // Delete record
                    db.SaveBackups.Remove(backup);
                }

                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                _logger.LogInformation(
                    "Cleaned up {Count} old backups for game: {GameId}",
                    backupsToDelete.Count,
                    gameId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during backup cleanup transaction, rolling back");
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                throw;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cleaning up old backups for game: {GameId}", gameId);
        }
    }

    private async Task<SaveMetadata> ExtractEngineMetadataAsync(
        string savePath,
        GameEngineType engineType,
        SaveMetadata metadata,
        CancellationToken cancellationToken)
    {
        // Engine-specific metadata extraction
        switch (engineType)
        {
            case GameEngineType.Renpy:
                metadata = ExtractRenpyMetadata(savePath, metadata, cancellationToken);
                break;

            case GameEngineType.RpgMaker:
                metadata = await ExtractRpgMakerMetadataAsync(savePath, metadata, cancellationToken).ConfigureAwait(false);
                break;

            // Other engines may require specific parsing
            default:
                // Try generic JSON metadata
                metadata = await TryExtractJsonMetadataAsync(savePath, metadata, cancellationToken).ConfigureAwait(false);
                break;
        }

        return metadata;
    }

    private SaveMetadata ExtractRenpyMetadata(
        string savePath,
        SaveMetadata metadata,
        CancellationToken cancellationToken)
    {
        // Renpy saves often have metadata in the save files themselves
        // Try to read save file headers

        try
        {
            var saveFiles = SafeGetFiles(savePath, "*.save", SearchOption.TopDirectoryOnly);

            foreach (var saveFile in saveFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Try to extract slot name from filename
                var fileName = Path.GetFileNameWithoutExtension(saveFile);
                if (fileName.StartsWith("save-", StringComparison.OrdinalIgnoreCase))
                {
                    metadata.SlotName = fileName;
                }

                // Renpy save files contain Python pickled data which can be complex to parse
                // For simplicity, we'll just track the file info
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error extracting Renpy metadata from: {SavePath}", savePath);
        }

        return metadata;
    }

    private async Task<SaveMetadata> ExtractRpgMakerMetadataAsync(
        string savePath,
        SaveMetadata metadata,
        CancellationToken cancellationToken)
    {
        // RPG Maker saves (MV/MZ) use JSON format
        try
        {
            var jsonFiles = SafeGetFiles(savePath, "*.json", SearchOption.TopDirectoryOnly);

            foreach (var jsonFile in jsonFiles.Take(5)) // Limit to first 5 files
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (jsonFile.Contains("global", StringComparison.OrdinalIgnoreCase))
                {
                    // Global save often contains play time and unlock info
                    try
                    {
                        var content = await File.ReadAllTextAsync(jsonFile, cancellationToken).ConfigureAwait(false);
                        var globalData = System.Text.Json.JsonSerializer.Deserialize<RpgMakerGlobalData>(content);

                        if (globalData != null)
                        {
                            metadata.PlayTimeSeconds = globalData.Playtime ?? 0;
                        }
                    }
                    catch (System.Text.Json.JsonException)
                    {
                        // Ignore parsing errors
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error extracting RPG Maker metadata from: {SavePath}", savePath);
        }

        return metadata;
    }

    private async Task<SaveMetadata> TryExtractJsonMetadataAsync(
        string savePath,
        SaveMetadata metadata,
        CancellationToken cancellationToken)
    {
        // Try to find any JSON files that might contain metadata
        try
        {
            var jsonFiles = SafeGetFiles(savePath, "*.json", SearchOption.TopDirectoryOnly);

            foreach (var jsonFile in jsonFiles.Take(3))
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var content = await File.ReadAllTextAsync(jsonFile, cancellationToken).ConfigureAwait(false);

                    // Look for common metadata patterns
                    using var doc = System.Text.Json.JsonDocument.Parse(content);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("playtime", out var playtime))
                    {
                        metadata.PlayTimeSeconds = playtime.GetInt64();
                    }

                    if (root.TryGetProperty("chapter", out var chapter))
                    {
                        metadata.ChapterProgress = chapter.GetString();
                    }

                    if (root.TryGetProperty("cg_unlock_rate", out var cgRate))
                    {
                        metadata.CgUnlockRate = cgRate.GetDouble();
                    }
                }
                catch (System.Text.Json.JsonException)
                {
                    // Ignore parsing errors
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error extracting JSON metadata from: {SavePath}", savePath);
        }

        return metadata;
    }

    #endregion

    #region JSON Data Classes

    private class RpgMakerGlobalData
    {
        public long? Playtime { get; set; }
    }

    #endregion
}