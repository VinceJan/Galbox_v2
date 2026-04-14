using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace Galbox.Data.Entities;

/// <summary>
/// Represents a detected error/issue record for a game.
/// Stores error history for tracking and resolution.
/// </summary>
[Index(nameof(GameInfoId))]
[Index(nameof(Category))]
[Index(nameof(Severity))]
[Index(nameof(IsResolved))]
public class GameErrorRecord
{
    /// <summary>
    /// Unique identifier for the error record.
    /// </summary>
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// The game this error is associated with.
    /// </summary>
    [Required]
    public int GameInfoId { get; set; }

    /// <summary>
    /// Category of the error (ChineseDirectory, LocaleRequirement, etc.).
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string Category { get; set; } = string.Empty;

    /// <summary>
    /// Severity level of the error (Critical, Major, Minor, Info).
    /// </summary>
    [Required]
    [MaxLength(20)]
    public string Severity { get; set; } = string.Empty;

    /// <summary>
    /// Short title describing the error.
    /// </summary>
    [Required]
    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Detailed description of the error.
    /// </summary>
    [Required]
    [MaxLength(2000)]
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Type of solution available (AutoFix, ManualFix, ExternalTool, None).
    /// </summary>
    [Required]
    [MaxLength(30)]
    public string SolutionType { get; set; } = string.Empty;

    /// <summary>
    /// Step-by-step instructions to resolve the error.
    /// </summary>
    [MaxLength(2000)]
    public string? SolutionInstructions { get; set; }

    /// <summary>
    /// Download URL for external tools if needed.
    /// </summary>
    [MaxLength(500)]
    public string? DownloadUrl { get; set; }

    /// <summary>
    /// Name of the external tool if required.
    /// </summary>
    [MaxLength(100)]
    public string? ToolName { get; set; }

    /// <summary>
    /// Time when the error was detected.
    /// </summary>
    public DateTime DetectedTime { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Whether this error has been resolved.
    /// </summary>
    public bool IsResolved { get; set; }

    /// <summary>
    /// Time when the error was resolved (if applicable).
    /// </summary>
    public DateTime? ResolvedTime { get; set; }

    /// <summary>
    /// The game this error belongs to.
    /// </summary>
    public GameInfo GameInfo { get; set; } = null!;
}