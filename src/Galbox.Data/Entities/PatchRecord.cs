namespace Galbox.Data.Entities;

/// <summary>
/// Represents a patch record.
/// </summary>
public class PatchRecord
{
    public int PatchId { get; set; }
    public int GameId { get; set; }
    public string PatchName { get; set; } = string.Empty;
    public string PatchType { get; set; } = string.Empty; // 汉化补丁, 修复补丁, etc.
    public string Version { get; set; } = string.Empty;
    public string? DownloadUrl { get; set; }
    public string? Source { get; set; } // moyu.moe
    public long FileSize { get; set; }
    public string? InstallPath { get; set; }
    public string? Description { get; set; }
    public DateTime? DownloadDate { get; set; }
    public DateTime? InstallDate { get; set; }
    public bool IsInstalled { get; set; }
    public DateTime? UpdateDate { get; set; } // External update date

    // Navigation properties
    public GameInfo Game { get; set; }
}