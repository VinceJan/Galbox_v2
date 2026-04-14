using System.ComponentModel.DataAnnotations;

namespace Galbox.Data.Entities;

/// <summary>
/// Represents a patch record for a game.
/// Tracks patch installation status and metadata.
/// </summary>
public class PatchRecord
{
    /// <summary>
    /// Unique identifier for the patch record.
    /// </summary>
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// The game this patch is associated with.
    /// </summary>
    [Required]
    public int GameInfoId { get; set; }

    /// <summary>
    /// Name of the patch.
    /// </summary>
    [Required]
    [MaxLength(500)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Type of the patch (Translation, Fix, Adult, Other).
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string PatchType { get; set; } = string.Empty;

    /// <summary>
    /// Version of the patch.
    /// </summary>
    [MaxLength(100)]
    public string? Version { get; set; }

    /// <summary>
    /// Size of the patch in bytes.
    /// </summary>
    public long SizeBytes { get; set; }

    /// <summary>
    /// Source/origin of the patch (moyu.moe, manual, etc).
    /// </summary>
    [MaxLength(200)]
    public string? Source { get; set; }

    /// <summary>
    /// URL to download the patch.
    /// </summary>
    [MaxLength(2000)]
    public string? DownloadUrl { get; set; }

    /// <summary>
    /// Local path where the patch is stored/downloaded.
    /// </summary>
    [MaxLength(2000)]
    public string? LocalPath { get; set; }

    /// <summary>
    /// Installation status of the patch.
    /// </summary>
    public PatchStatus Status { get; set; } = PatchStatus.Available;

    /// <summary>
    /// Description of the patch.
    /// </summary>
    [MaxLength(5000)]
    public string? Description { get; set; }

    /// <summary>
    /// Date and time when the patch was added to the record.
    /// </summary>
    public DateTime AddedTime { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Date and time when the patch was downloaded.
    /// </summary>
    public DateTime? DownloadedTime { get; set; }

    /// <summary>
    /// Date and time when the patch was installed.
    /// </summary>
    public DateTime? InstalledTime { get; set; }

    /// <summary>
    /// Download progress percentage (0-100).
    /// </summary>
    public int DownloadProgress { get; set; }

    /// <summary>
    /// External ID from the patch source API.
    /// </summary>
    [MaxLength(100)]
    public string? ExternalId { get; set; }

    /// <summary>
    /// The game this patch belongs to.
    /// </summary>
    public GameInfo GameInfo { get; set; } = null!;

    /// <summary>
    /// Gets the patch size formatted as a human-readable string.
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

    /// <summary>
    /// Gets the display name for the patch type.
    /// </summary>
    public string PatchTypeDisplay => GetPatchTypeDisplay(PatchType);

    /// <summary>
    /// Gets the display name for a patch type.
    /// </summary>
    public static string GetPatchTypeDisplay(string patchType)
    {
        return patchType switch
        {
            "Translation" => "汉化补丁",
            "Fix" => "修复补丁",
            "Adult" => "18+补丁",
            "Other" => "其他补丁",
            _ => patchType
        };
    }
}

/// <summary>
/// Patch installation status.
/// </summary>
public enum PatchStatus
{
    /// <summary>
    /// Patch is available for download.
    /// </summary>
    Available = 0,

    /// <summary>
    /// Patch is currently downloading.
    /// </summary>
    Downloading = 1,

    /// <summary>
    /// Patch has been downloaded.
    /// </summary>
    Downloaded = 2,

    /// <summary>
    /// Patch is currently being installed.
    /// </summary>
    Installing = 3,

    /// <summary>
    /// Patch has been installed.
    /// </summary>
    Installed = 4,

    /// <summary>
    /// Patch installation failed.
    /// </summary>
    Failed = 5,

    /// <summary>
    /// Patch is not applicable/available for this game.
    /// </summary>
    NotApplicable = 6
}

/// <summary>
/// Patch type enumeration.
/// </summary>
public enum PatchType
{
    /// <summary>
    /// Translation patch (汉化).
    /// </summary>
    Translation = 0,

    /// <summary>
    /// Fix/bug patch (修复).
    /// </summary>
    Fix = 1,

    /// <summary>
    /// Adult content patch (18+).
    /// </summary>
    Adult = 2,

    /// <summary>
    /// Other patches.
    /// </summary>
    Other = 3
}