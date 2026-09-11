using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace Galbox.Data.Entities;

/// <summary>
/// Game engine types supported by Galbox.
/// </summary>
public enum GameEngineType
{
    Unknown = 0,
    Renpy = 1,
    Krkr = 2,
    Tyrano = 3,
    Vnm = 4,
    Unity = 5,
    RpgMaker = 6,
    Other = 7
}

/// <summary>
/// Represents a game in the library.
/// </summary>
[Index(nameof(NameCn))]
[Index(nameof(NameOriginal))]
[Index(nameof(InstallPath))]
public class GameInfo
{
    /// <summary>
    /// Unique identifier for the game.
    /// </summary>
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// Chinese name of the game.
    /// </summary>
    [MaxLength(500)]
    public string? NameCn { get; set; }

    /// <summary>
    /// Original name of the game (Japanese, English, etc.).
    /// </summary>
    [Required]
    [MaxLength(500)]
    public string NameOriginal { get; set; } = string.Empty;

    /// <summary>
    /// Path where the game is installed.
    /// </summary>
    [Required]
    [MaxLength(2000)]
    public string InstallPath { get; set; } = string.Empty;

    /// <summary>
    /// Path to the main executable file.
    /// </summary>
    [Required]
    [MaxLength(2000)]
    public string MainExecutable { get; set; } = string.Empty;

    /// <summary>
    /// Alternative executable files for multi-launch.
    /// </summary>
    public string? AlternativeExecutables { get; set; }

    /// <summary>
    /// Description/summary of the game.
    /// </summary>
    [MaxLength(5000)]
    public string? Description { get; set; }

    /// <summary>
    /// Path to the cover image (vertical poster).
    /// </summary>
    [MaxLength(2000)]
    public string? CoverImagePath { get; set; }

    /// <summary>
    /// URL to the cover image from scraping.
    /// </summary>
    [MaxLength(2000)]
    public string? CoverImageUrl { get; set; }

    /// <summary>
    /// Path to the background/wallpaper image.
    /// </summary>
    [MaxLength(2000)]
    public string? BackgroundImagePath { get; set; }

    /// <summary>
    /// URL to the background image from scraping.
    /// </summary>
    [MaxLength(2000)]
    public string? BackgroundImageUrl { get; set; }

    /// <summary>
    /// Developer/publisher of the game.
    /// </summary>
    [MaxLength(200)]
    public string? Developer { get; set; }

    /// <summary>
    /// Release date of the game.
    /// </summary>
    public DateTime? ReleaseDate { get; set; }

    /// <summary>
    /// Rating score (0-10 scale).
    /// </summary>
    public double? Rating { get; set; }

    /// <summary>
    /// Source ID from scraping (Bangumi ID, VNDB ID, etc.).
    /// </summary>
    [MaxLength(100)]
    public string? SourceId { get; set; }

    /// <summary>
    /// Source type (Bangumi, VNDB, ymgal, cngal).
    /// </summary>
    [MaxLength(50)]
    public string? SourceType { get; set; }

    /// <summary>
    /// VNDB visual novel id (e.g. "v28915"), independent of which source provided the
    /// accepted metadata.
    /// </summary>
    /// <remarks>
    /// Added for cross-system linking: downstream features (patch centre) key on the VNDB id
    /// instead of the Bangumi subject id. Recorded whenever any source's candidate carries a
    /// VNDB id, even when Bangumi supplied the accepted result.
    /// </remarks>
    [MaxLength(20)]
    public string? VndbId { get; set; }

    /// <summary>
    /// Tags/genres associated with the game.
    /// JSON serialized list.
    /// </summary>
    public string? TagsJson { get; set; }

    /// <summary>
    /// Total play time in seconds.
    /// </summary>
    public long TotalPlayTimeSeconds { get; set; }

    /// <summary>
    /// Number of times the game has been launched.
    /// </summary>
    public int LaunchCount { get; set; }

    /// <summary>
    /// Date and time of the last session.
    /// </summary>
    public DateTime? LastSessionTime { get; set; }

    /// <summary>
    /// Date and time when the game was added to the library.
    /// </summary>
    public DateTime AddedTime { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Date and time when the game info was last updated.
    /// </summary>
    public DateTime UpdatedTime { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Whether the game is a favorite.
    /// </summary>
    public bool IsFavorite { get; set; }

    /// <summary>
    /// Whether the game metadata has been scraped.
    /// </summary>
    public bool IsScraped { get; set; }

    /// <summary>
    /// Size of the game in bytes.
    /// </summary>
    public long SizeBytes { get; set; }

    /// <summary>
    /// Detected game engine type.
    /// </summary>
    public GameEngineType EngineType { get; set; } = GameEngineType.Unknown;

    /// <summary>
    /// Persisted library status of the game (product spec §4.1: 正在游玩 / 已完成 / 待玩 / 暂停中 / 未玩过).
    /// </summary>
    /// <remarks>
    /// This is a stored value, not a display-time derivation. Storing it is what makes the "待玩" (backlog)
    /// and "暂停中" (on hold) states possible at all — neither can be inferred from play statistics.
    /// Use <see cref="GameStatusDerivation.Suggest"/> when a default value is needed.
    /// </remarks>
    public GameStatus Status { get; set; } = GameStatus.NotPlayed;

    /// <summary>
    /// True when <see cref="Status"/> was set by the user explicitly, so automatic suggestions must not
    /// overwrite it. False means the stored value is still allowed to be replaced by a suggested default.
    /// </summary>
    public bool IsStatusUserSet { get; set; }

    /// <summary>
    /// UTC time when <see cref="Status"/> was last changed. Null when the status has never been changed
    /// since the game was added.
    /// </summary>
    public DateTime? StatusChangedTime { get; set; }

    /// <summary>
    /// Collection of characters associated with the game.
    /// </summary>
    public ICollection<GameCharacter> Characters { get; set; } = new List<GameCharacter>();

    /// <summary>
    /// Collection of documents (txt/readme/html) in the game folder.
    /// </summary>
    public ICollection<GameDocument> Documents { get; set; } = new List<GameDocument>();

    /// <summary>
    /// Collection of media files (music/video) associated with the game.
    /// </summary>
    public ICollection<GameMediaFile> MediaFiles { get; set; } = new List<GameMediaFile>();

    /// <summary>
    /// Collection of screenshots for the game.
    /// </summary>
    public ICollection<GameScreenshot> Screenshots { get; set; } = new List<GameScreenshot>();

    /// <summary>
    /// Collection of save backups for the game.
    /// </summary>
    public ICollection<GameSaveBackup> SaveBackups { get; set; } = new List<GameSaveBackup>();

    /// <summary>
    /// Collection of save nodes (story positions / snapshots) recorded for the game.
    /// </summary>
    public ICollection<SaveNode> SaveNodes { get; set; } = new List<SaveNode>();

    /// <summary>
    /// Collection of save groups (branch/route groupings) defined for the game.
    /// </summary>
    public ICollection<SaveGroup> SaveGroups { get; set; } = new List<SaveGroup>();

    /// <summary>
    /// Gets the display name (prefer Chinese name, fallback to original).
    /// </summary>
    public string DisplayName => !string.IsNullOrWhiteSpace(NameCn) ? NameCn : NameOriginal;

    /// <summary>
    /// Gets the total play time formatted as a human-readable string.
    /// </summary>
    public string FormattedPlayTime
    {
        get
        {
            var hours = TotalPlayTimeSeconds / 3600;
            var minutes = (TotalPlayTimeSeconds % 3600) / 60;
            if (hours > 0)
                return $"{hours}h {minutes}m";
            return $"{minutes}m";
        }
    }

    /// <summary>
    /// Gets the game size formatted as a human-readable string.
    /// </summary>
    public string FormattedSize
    {
        get
        {
            const long GB = 1024 * 1024 * 1024;
            const long MB = 1024 * 1024;
            const long KB = 1024;

            if (SizeBytes >= GB)
                return $"{SizeBytes / GB:F2} GB";
            if (SizeBytes >= MB)
                return $"{SizeBytes / MB:F1} MB";
            if (SizeBytes >= KB)
                return $"{SizeBytes / KB:F0} KB";
            return $"{SizeBytes} B";
        }
    }
}

/// <summary>
/// Represents a character associated with a game.
/// </summary>
public class GameCharacter
{
    [Key]
    public int Id { get; set; }

    [Required]
    public int GameInfoId { get; set; }

    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? NameCn { get; set; }

    [MaxLength(2000)]
    public string? ImagePath { get; set; }

    [MaxLength(2000)]
    public string? ImageUrl { get; set; }

    [MaxLength(100)]
    public string? Role { get; set; }

    public GameInfo GameInfo { get; set; } = null!;

    public string DisplayName => !string.IsNullOrWhiteSpace(NameCn) ? NameCn : Name;
}

/// <summary>
/// Represents a document file in the game folder.
/// </summary>
public class GameDocument
{
    [Key]
    public int Id { get; set; }

    [Required]
    public int GameInfoId { get; set; }

    [Required]
    [MaxLength(500)]
    public string FileName { get; set; } = string.Empty;

    [Required]
    [MaxLength(2000)]
    public string FilePath { get; set; } = string.Empty;

    [MaxLength(5000)]
    public string? PreviewContent { get; set; }

    [MaxLength(50)]
    public string? FileType { get; set; }

    public long FileSize { get; set; }

    public GameInfo GameInfo { get; set; } = null!;
}

/// <summary>
/// Represents a media file (music/video) associated with a game.
/// </summary>
public class GameMediaFile
{
    [Key]
    public int Id { get; set; }

    [Required]
    public int GameInfoId { get; set; }

    [Required]
    [MaxLength(500)]
    public string FileName { get; set; } = string.Empty;

    [Required]
    [MaxLength(2000)]
    public string FilePath { get; set; } = string.Empty;

    [MaxLength(50)]
    public string? MediaType { get; set; }

    public long DurationSeconds { get; set; }

    public long FileSize { get; set; }

    public GameInfo GameInfo { get; set; } = null!;

    public string FormattedDuration
    {
        get
        {
            var hours = DurationSeconds / 3600;
            var minutes = (DurationSeconds % 3600) / 60;
            var seconds = DurationSeconds % 60;
            if (hours > 0)
                return $"{hours}:{minutes:D2}:{seconds:D2}";
            return $"{minutes}:{seconds:D2}";
        }
    }
}

/// <summary>
/// Represents a screenshot for a game.
/// </summary>
public class GameScreenshot
{
    [Key]
    public int Id { get; set; }

    [Required]
    public int GameInfoId { get; set; }

    [Required]
    [MaxLength(500)]
    public string FileName { get; set; } = string.Empty;

    [Required]
    [MaxLength(2000)]
    public string FilePath { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string? Url { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public DateTime CapturedTime { get; set; }

    public GameInfo GameInfo { get; set; } = null!;
}

/// <summary>
/// Represents a save backup for a game.
/// </summary>
/// <remarks>
/// This entity stays what it always was: a <b>file-level backup record</b> (which files were copied where, when,
/// and how big they are). Story information (scene label, route, chapter progress, CG rate, snapshot flag) lives
/// on <see cref="SaveNode"/> instead, so that "what the player is playing" and "which bytes were archived" can
/// evolve independently. <see cref="SaveNodeId"/> optionally links a backup to the node it protects.
/// </remarks>
public class GameSaveBackup
{
    [Key]
    public int Id { get; set; }

    [Required]
    public int GameInfoId { get; set; }

    /// <summary>
    /// Optional identifier of the <see cref="SaveNode"/> this backup protects (§3.3 "三重保障" step 2:
    /// back up the current save before replacing it). Null for automatic/timed backups that are not tied to a
    /// specific story node. The backup row is kept (set to null) if the node is deleted.
    /// </summary>
    public int? SaveNodeId { get; set; }

    [Required]
    [MaxLength(500)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [MaxLength(2000)]
    public string BackupPath { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string? OriginalSavePath { get; set; }

    public DateTime CreatedTime { get; set; }

    public long SizeBytes { get; set; }

    [MaxLength(500)]
    public string? Description { get; set; }

    public GameInfo GameInfo { get; set; } = null!;

    /// <summary>
    /// Navigation property to the save node this backup belongs to, if any.
    /// </summary>
    public SaveNode? SaveNode { get; set; }

    public string FormattedSize
    {
        get
        {
            const long MB = 1024 * 1024;
            const long KB = 1024;

            if (SizeBytes >= MB)
                return $"{SizeBytes / MB:F1} MB";
            if (SizeBytes >= KB)
                return $"{SizeBytes / KB:F0} KB";
            return $"{SizeBytes} B";
        }
    }
}