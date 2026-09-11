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

            // Create zip backup
            await CreateZipBackupAsync(
                filesToBackup,
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
                    "Restore verification failed: {ErrorMessage}. Attempting to rollback.",
                    verificationResult.ErrorMessage);

                // Step 4: Rollback - restore from temporary backup
                if (Directory.Exists(tempRestoreDir) && Directory.Exists(restorePath))
                {
                    await RollbackRestoreAsync(
                        tempRestoreDir,
                        restorePath,
                        cancellationToken).ConfigureAwait(false);
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

            // Attempt rollback on cancellation
            if (tempRestoreDir != null && Directory.Exists(tempRestoreDir))
            {
                _logger.LogInformation("Attempting rollback after cancellation");
            }

            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error restoring backup with ID: {SaveId}",
                saveId);

            // Attempt rollback on error
            if (tempRestoreDir != null && Directory.Exists(tempRestoreDir))
            {
                _logger.LogInformation("Attempting rollback after error");
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
    /// Verifies the integrity of a restore operation by checking file count and sizes.
    /// </summary>
    private Task<RestoreVerificationResult> VerifyRestoreIntegrityAsync(
        string backupPath,
        string restorePath,
        CancellationToken cancellationToken)
    {
        var result = new RestoreVerificationResult();

        try
        {
            using var zipArchive = ZipFile.OpenRead(backupPath);
            var expectedFileCount = zipArchive.Entries.Count(e => !string.IsNullOrEmpty(e.Name));
            var expectedTotalSize = zipArchive.Entries.Sum(e => e.Length);

            var restoredFiles = EngineSaveDetector.GetFilesWithSizes(restorePath);
            var actualFileCount = restoredFiles.Count;
            var actualTotalSize = restoredFiles.Sum(f => f.Size);

            // Allow some tolerance for file count (some entries might be directories)
            if (actualFileCount < expectedFileCount * 0.8)
            {
                result.ErrorMessage = $"File count mismatch: expected ~{expectedFileCount}, got {actualFileCount}";
                return Task.FromResult(result);
            }

            // Allow 10% tolerance for size (compression might affect estimates)
            if (actualTotalSize < expectedTotalSize * 0.9)
            {
                result.ErrorMessage = $"Size mismatch: expected ~{expectedTotalSize} bytes, got {actualTotalSize} bytes";
                return Task.FromResult(result);
            }

            result.Success = true;
            result.FileCount = actualFileCount;
            result.TotalSize = actualTotalSize;

            return Task.FromResult(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error verifying restore integrity");
            result.ErrorMessage = $"Verification error: {ex.Message}";
            return Task.FromResult(result);
        }
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

    private class RestoreVerificationResult
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public int FileCount { get; set; }
        public long TotalSize { get; set; }
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

            // Create backup of current saves first
            var currentBackup = await CreateBackupAsync(
                game,
                $"Quick switch backup - {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}",
                null,
                cancellationToken).ConfigureAwait(false);

            result.CurrentBackup = currentBackup;

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
                    currentBackup?.Id,
                    targetSaveId);
            }
            else
            {
                result.ErrorMessage = "Failed to restore target backup";
                _logger.LogWarning("Quick switch failed: could not restore target backup");
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

    private async Task CreateZipBackupAsync(
        List<(string Path, long Size)> files,
        string zipPath,
        IProgress<BackupProgress>? progress,
        int totalFiles,
        long totalBytes,
        CancellationToken cancellationToken)
    {
        long bytesTransferred = 0;
        int filesProcessed = 0;

        using var zipArchive = ZipFile.Open(zipPath, ZipArchiveMode.Create);

        foreach (var (filePath, _) in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fileInfo = new FileInfo(filePath);
            if (!fileInfo.Exists)
            {
                continue;
            }

            // Create relative entry name
            var entryName = fileInfo.Name;
            var directoryName = fileInfo.Directory?.Name;
            if (!string.IsNullOrEmpty(directoryName))
            {
                entryName = Path.Combine(directoryName, fileInfo.Name);
            }

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

            var entryPath = Path.Combine(extractPath, entry.FullName);

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