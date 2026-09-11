namespace Galbox.App.Services;

/// <summary>
/// Options for removing a game from the library.
/// </summary>
public sealed class GameDeletionOptions
{
    /// <summary>
    /// Also delete the save-backup zip files this game owns.
    /// </summary>
    /// <remarks>
    /// Defaults to <c>false</c> for a reason: backups are the one thing in the library that cannot
    /// be re-created from the game files, and a user who deletes a game entry by mistake must not
    /// silently lose the ability to restore its saves.
    /// </remarks>
    public bool DeleteBackupFiles { get; set; }
}

/// <summary>
/// Result of a game deletion, including what was removed and the proof that the game files on disk
/// were left alone.
/// </summary>
public sealed class GameDeletionResult
{
    /// <summary>Whether the game row is gone from the database.</summary>
    public bool Success { get; set; }

    /// <summary>Id of the game.</summary>
    public int GameId { get; set; }

    /// <summary>Display name of the game at the time of deletion.</summary>
    public string GameName { get; set; } = string.Empty;

    /// <summary>Installation folder of the game.</summary>
    public string InstallPath { get; set; } = string.Empty;

    /// <summary>
    /// True when the installation folder still exists after the deletion. The deletion never
    /// touches game files; this value is measured, not assumed.
    /// </summary>
    public bool GameFilesKept { get; set; }

    /// <summary>Number of character rows removed.</summary>
    public int CharactersDeleted { get; set; }

    /// <summary>Number of document rows removed.</summary>
    public int DocumentsDeleted { get; set; }

    /// <summary>Number of media-file rows removed.</summary>
    public int MediaFilesDeleted { get; set; }

    /// <summary>Number of screenshot rows removed.</summary>
    public int ScreenshotsDeleted { get; set; }

    /// <summary>Number of save-backup rows removed.</summary>
    public int BackupsDeleted { get; set; }

    /// <summary>Number of save-backup zip files deleted (0 unless explicitly requested).</summary>
    public int BackupFilesDeleted { get; set; }

    /// <summary>Number of error-report rows removed.</summary>
    public int ErrorRecordsDeleted { get; set; }

    /// <summary>Number of patch rows removed.</summary>
    public int PatchesDeleted { get; set; }

    /// <summary>Total number of related rows removed.</summary>
    public int RelatedRowsDeleted =>
        CharactersDeleted + DocumentsDeleted + MediaFilesDeleted + ScreenshotsDeleted
        + BackupsDeleted + ErrorRecordsDeleted + PatchesDeleted;

    /// <summary>Non-fatal problems encountered while deleting (e.g. a locked backup file).</summary>
    public List<string> Warnings { get; set; } = new();

    /// <summary>Reason the deletion failed, when <see cref="Success"/> is false.</summary>
    public string? ErrorMessage { get; set; }
}
