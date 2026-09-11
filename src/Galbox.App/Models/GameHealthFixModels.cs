namespace Galbox.App.Models;

/// <summary>
/// What kind of repair a <see cref="GameHealthFixResult"/> describes.
/// </summary>
public enum GameHealthFixKind
{
    /// <summary>Renaming the game folder to a path without Chinese characters.</summary>
    RenameInstallPath,

    /// <summary>Writing (or removing) a Windows compatibility layer for the game executable.</summary>
    WindowsCompatibility
}

/// <summary>
/// Outcome of one repair attempt.
/// </summary>
/// <remarks>
/// The two properties that matter beyond success/failure are <see cref="RolledBack"/> and
/// <see cref="PreviousValue"/>: the first says whether a half-performed repair was undone, the second
/// is what an undo has to restore. A repair that cannot state either of them is not auditable.
/// </remarks>
public class GameHealthFixResult
{
    /// <summary>True only when the repair really changed something and the change was verified.</summary>
    public bool Success { get; set; }

    /// <summary>Plain-Chinese explanation shown to the user.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Step-by-step log of what was done, in order.</summary>
    public List<string> Notes { get; set; } = new();

    /// <summary>Which repair this result belongs to.</summary>
    public GameHealthFixKind? Kind { get; set; }

    /// <summary>Game the repair was performed for.</summary>
    public int GameId { get; set; }

    /// <summary>Previous install path (folder rename).</summary>
    public string? OldPath { get; set; }

    /// <summary>New install path (folder rename).</summary>
    public string? NewPath { get; set; }

    /// <summary>True when the repair failed after changing something and undid that change.</summary>
    public bool RolledBack { get; set; }

    /// <summary>Value written by a registry-based repair (the compatibility layer).</summary>
    public string? AppliedValue { get; set; }

    /// <summary>Value that was overwritten by a registry-based repair, so an undo can restore it.</summary>
    public string? PreviousValue { get; set; }

    /// <summary>Database columns/fields that were kept in sync.</summary>
    public List<string> UpdatedFields { get; set; } = new();

    /// <summary>True when the repair can be undone.</summary>
    public bool CanUndo { get; set; }

    /// <summary>Creates a failed result.</summary>
    public static GameHealthFixResult Failure(string message) => new()
    {
        Success = false,
        Message = message
    };

    /// <summary>Adds a log line and returns the same instance, for fluent building.</summary>
    public GameHealthFixResult WithNote(string note)
    {
        Notes.Add(note);
        return this;
    }
}
