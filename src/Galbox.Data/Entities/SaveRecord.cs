namespace Galbox.Data.Entities;

/// <summary>
/// Represents a save file record.
/// </summary>
public class SaveRecord
{
    public int SaveId { get; set; }
    public int GameId { get; set; }
    public int? GroupId { get; set; }
    public string SaveName { get; set; } = string.Empty;
    public string SavePath { get; set; } = string.Empty;
    public string? BranchName { get; set; }
    public DateTime CreatedDate { get; set; }
    public DateTime ModifiedDate { get; set; }
    public int ChapterProgress { get; set; } // 0-100
    public int CgUnlockRate { get; set; } // 0-100
    public string? SceneLabel { get; set; }
    public bool IsSnapshot { get; set; }
    public string? SnapshotDescription { get; set; }
    public bool IsBackup { get; set; }

    // Navigation properties
    public GameInfo Game { get; set; }
    public SaveGroup? Group { get; set; }
}