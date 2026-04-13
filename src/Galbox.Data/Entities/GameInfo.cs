namespace Galbox.Data.Entities;

/// <summary>
/// Represents a game in the library.
/// </summary>
public class GameInfo
{
    public int GameId { get; set; }
    public string NameCn { get; set; } = string.Empty;
    public string NameOriginal { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? CoverPath { get; set; }
    public string? BannerPath { get; set; }
    public string InstallPath { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;
    public string? AltExecutables { get; set; } // JSON array of alternative executables
    public DateTime AddedDate { get; set; }
    public DateTime? LastPlayedDate { get; set; }
    public int PlayTimeMinutes { get; set; }
    public int PlayCount { get; set; }
    public GameStatus Status { get; set; }
    public string? Tags { get; set; } // JSON array of tags
    public string EngineType { get; set; } = "Unknown"; // Renpy, Krkr, Tyrano, Unknown
    public string? ScraperSource { get; set; }
    public DateTime? ScraperDate { get; set; }

    // Navigation properties
    public ICollection<SaveRecord> Saves { get; set; } = new List<SaveRecord>();
    public ICollection<SaveGroup> SaveGroups { get; set; } = new List<SaveGroup>();
    public ICollection<PatchRecord> Patches { get; set; } = new List<PatchRecord>();
}

/// <summary>
/// Game status enumeration.
/// </summary>
public enum GameStatus
{
    NeverPlayed = 0,
    Playing = 1,
    Completed = 2,
    ToPlay = 3,
    Paused = 4
}