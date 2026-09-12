using Galbox.Data.Entities;

namespace Galbox.App.Services;

/// <summary>
/// Interface for save file management operations.
/// Provides auto-detection, backup, and restoration of game save files.
/// </summary>
public interface ISaveManagementService
{
    /// <summary>
    /// Detects the save file location for a game based on its engine type.
    /// </summary>
    /// <param name="game">The game to detect save location for</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Detected save location information</returns>
    Task<SaveLocationResult> DetectSaveLocationAsync(GameInfo game, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a backup of the current save files for a game.
    /// </summary>
    /// <param name="game">The game to backup saves for</param>
    /// <param name="description">Description for the backup</param>
    /// <param name="progress">Optional progress reporter</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The created backup record</returns>
    Task<GameSaveBackup?> CreateBackupAsync(
        GameInfo game,
        string description,
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates an automatic backup before game launch (if configured).
    /// </summary>
    /// <param name="game">The game to backup saves for</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The created backup record, or null if auto-backup is disabled</returns>
    Task<GameSaveBackup?> CreateAutoBackupAsync(GameInfo game, CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores a previously saved backup.
    /// </summary>
    /// <param name="saveId">The ID of the backup to restore</param>
    /// <param name="progress">Optional progress reporter</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if restoration was successful</returns>
    Task<bool> RestoreBackupAsync(
        int saveId,
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all save backups for a specific game.
    /// </summary>
    /// <param name="gameId">The game ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of save backups</returns>
    Task<List<GameSaveBackup>> GetSaveBackupsAsync(int gameId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a save backup.
    /// </summary>
    /// <param name="saveId">The ID of the backup to delete</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if deletion was successful</returns>
    Task<bool> DeleteBackupAsync(int saveId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Quick switches between save slots - backs up current saves and restores target.
    /// </summary>
    /// <param name="gameId">The game ID</param>
    /// <param name="targetSaveId">The target save backup to restore</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result of the quick switch operation</returns>
    Task<QuickSwitchResult> QuickSwitchSaveAsync(
        int gameId,
        int targetSaveId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets save metadata information (creation time, chapter progress, etc.) if detectable.
    /// </summary>
    /// <param name="savePath">Path to the save file/directory</param>
    /// <param name="engineType">The engine type</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Save metadata information</returns>
    Task<SaveMetadata?> GetSaveMetadataAsync(
        string savePath,
        GameEngineType engineType,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets or sets whether auto-backup is enabled before game launch.
    /// </summary>
    bool AutoBackupEnabled { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of backups to keep per game.
    /// </summary>
    int MaxBackupsPerGame { get; set; }

    /// <summary>
    /// Gets the backup storage path.
    /// </summary>
    string BackupStoragePath { get; }

    /// <summary>
    /// Why the most recent <see cref="CreateBackupAsync"/> returned null, in the user's language,
    /// or null when that call succeeded.
    /// </summary>
    /// <remarks>
    /// A failed backup is reported by returning null, which used to leave the page with nothing to
    /// say except "未检测到存档文件" - a sentence that is simply untrue when save files were found
    /// but no usable save folder could be proved. The reason kept here names the folders that were
    /// tried and why each was refused, so the page can show what actually happened.
    /// </remarks>
    string? LastBackupFailureReason { get; }
}

// Note: GameEngineType is now defined in Galbox.Data.Entities/GameInfo.cs

/// <summary>
/// Result of save location detection.
/// </summary>
public class SaveLocationResult
{
    /// <summary>
    /// Whether the save location was successfully detected.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// The detected engine type.
    /// </summary>
    public GameEngineType EngineType { get; set; }

    /// <summary>
    /// The primary save location path.
    /// </summary>
    public string? PrimarySavePath { get; set; }

    /// <summary>
    /// Alternative save locations if multiple exist.
    /// </summary>
    public List<string> AlternativePaths { get; set; } = new();

    /// <summary>
    /// List of detected save files.
    /// </summary>
    public List<string> SaveFiles { get; set; } = new();

    /// <summary>
    /// Error message if detection failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Additional information about the save structure.
    /// </summary>
    public Dictionary<string, object> ExtendedInfo { get; set; } = new();
}

/// <summary>
/// Progress information for backup/restore operations.
/// </summary>
public class BackupProgress
{
    /// <summary>
    /// Current progress percentage (0-100).
    /// </summary>
    public int Percentage { get; set; }

    /// <summary>
    /// Number of files processed.
    /// </summary>
    public int FilesProcessed { get; set; }

    /// <summary>
    /// Total number of files to process.
    /// </summary>
    public int TotalFiles { get; set; }

    /// <summary>
    /// Bytes transferred so far.
    /// </summary>
    public long BytesTransferred { get; set; }

    /// <summary>
    /// Total bytes to transfer.
    /// </summary>
    public long TotalBytes { get; set; }

    /// <summary>
    /// Current file being processed.
    /// </summary>
    public string? CurrentFile { get; set; }

    /// <summary>
    /// Current operation phase.
    /// </summary>
    public BackupPhase Phase { get; set; }
}

/// <summary>
/// Phase of backup/restore operation.
/// </summary>
public enum BackupPhase
{
    /// <summary>
    /// Scanning for save files.
    /// </summary>
    Scanning = 0,

    /// <summary>
    /// Creating backup directory.
    /// </summary>
    CreatingDirectory = 1,

    /// <summary>
    /// Copying files.
    /// </summary>
    CopyingFiles = 2,

    /// <summary>
    /// Finalizing backup.
    /// </summary>
    Finalizing = 3,

    /// <summary>
    /// Verifying backup integrity.
    /// </summary>
    Verifying = 4,

    /// <summary>
    /// Cleaning up old backups.
    /// </summary>
    CleaningUp = 5
}

/// <summary>
/// Result of a quick switch operation.
/// </summary>
public class QuickSwitchResult
{
    /// <summary>
    /// Whether the quick switch was successful.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// The backup created for current saves before switch.
    /// </summary>
    public GameSaveBackup? CurrentBackup { get; set; }

    /// <summary>
    /// The backup that was restored.
    /// </summary>
    public GameSaveBackup? RestoredBackup { get; set; }

    /// <summary>
    /// Error message if operation failed.
    /// </summary>
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Metadata information about a save.
/// </summary>
public class SaveMetadata
{
    /// <summary>
    /// When the save was created (if detectable).
    /// </summary>
    public DateTime? CreationTime { get; set; }

    /// <summary>
    /// When the save was last modified.
    /// </summary>
    public DateTime? LastModifiedTime { get; set; }

    /// <summary>
    /// Chapter or scene progress (if detectable).
    /// </summary>
    public string? ChapterProgress { get; set; }

    /// <summary>
    /// CG unlock rate percentage (if detectable).
    /// </summary>
    public double? CgUnlockRate { get; set; }

    /// <summary>
    /// Play time in the save (if detectable).
    /// </summary>
    public long? PlayTimeSeconds { get; set; }

    /// <summary>
    /// Save slot name or number.
    /// </summary>
    public string? SlotName { get; set; }

    /// <summary>
    /// Total size of save files.
    /// </summary>
    public long TotalSizeBytes { get; set; }

    /// <summary>
    /// Number of save files.
    /// </summary>
    public int FileCount { get; set; }

    /// <summary>
    /// Additional metadata from the engine.
    /// </summary>
    public Dictionary<string, object> ExtendedData { get; set; } = new();
}