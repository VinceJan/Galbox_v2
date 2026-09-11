using Galbox.App.Models;
using Galbox.Data.Entities;
namespace Galbox.App.Services;

/// <summary>
/// Interface for game error detection and checking service.
/// Detects common issues that prevent games from running correctly.
/// </summary>
public interface IErrorCheckingService
{
    /// <summary>
    /// Run comprehensive error check on a game.
    /// </summary>
    /// <param name="gameInfo">The game to check</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of detected errors and issues</returns>
    Task<List<GameErrorInfo>> CheckGameAsync(GameInfo gameInfo, CancellationToken cancellationToken = default);

    /// <summary>
    /// Check for specific error category on a game.
    /// </summary>
    /// <param name="gameInfo">The game to check</param>
    /// <param name="category">The error category to check</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Detected error if found, null otherwise</returns>
    Task<GameErrorInfo?> CheckCategoryAsync(GameInfo gameInfo, ErrorCategory category, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get error history for a game.
    /// </summary>
    /// <param name="gameId">The game ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of historical error records</returns>
    Task<List<GameErrorRecord>> GetErrorHistoryAsync(int gameId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Mark an error as resolved.
    /// </summary>
    /// <param name="errorRecordId">The error record ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if successfully marked as resolved</returns>
    Task<bool> MarkErrorResolvedAsync(int errorRecordId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempt automatic fix for an error.
    /// </summary>
    /// <param name="error">The error to fix</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result of the fix attempt</returns>
    Task<AutoFixResult> AttemptAutoFixAsync(GameErrorInfo error, CancellationToken cancellationToken = default);

    /// <summary>
    /// Run batch error check on multiple games.
    /// </summary>
    /// <param name="games">List of games to check</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Dictionary of game ID to detected errors</returns>
    Task<Dictionary<int, List<GameErrorInfo>>> BatchCheckGamesAsync(IEnumerable<GameInfo> games, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get all unresolved errors across all games.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of all unresolved error records</returns>
    Task<List<GameErrorRecord>> GetAllUnresolvedErrorsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Enumeration of error detection categories.
/// </summary>
public enum ErrorCategory
{
    /// <summary>
    /// Chinese characters in directory path.
    /// Some Japanese games fail with Chinese paths.
    /// </summary>
    ChineseDirectory,

    /// <summary>
    /// Locale/region requirement (e.g., Japanese locale).
    /// </summary>
    LocaleRequirement,

    /// <summary>
    /// Missing DirectX dependencies.
    /// </summary>
    DirectXMissing,

    /// <summary>
    /// Missing KLite codec pack for video playback.
    /// </summary>
    KLiteCodecMissing,

    /// <summary>
    /// Windows compatibility issues (XP/Vista only games).
    /// </summary>
    WindowsCompatibility,

    /// <summary>
    /// Missing runtime dependencies (.NET, VC++, Java).
    /// </summary>
    RuntimeMissing,

    /// <summary>
    /// Missing game-specific dependencies.
    /// </summary>
    GameDependency,

    /// <summary>
    /// File permission issues.
    /// </summary>
    PermissionIssue,

    /// <summary>
    /// Anti-virus blocking game execution.
    /// </summary>
    AntivirusBlocking
}

/// <summary>
/// Enumeration of error severity levels.
/// </summary>
public enum ErrorSeverity
{
    /// <summary>
    /// Critical - game will not run at all.
    /// </summary>
    Critical,

    /// <summary>
    /// Major - game will run but with major issues.
    /// </summary>
    Major,

    /// <summary>
    /// Minor - game will run but with minor issues.
    /// </summary>
    Minor,

    /// <summary>
    /// Info - recommendation or suggestion.
    /// </summary>
    Info
}

/// <summary>
/// Enumeration of solution types.
/// </summary>
public enum SolutionType
{
    /// <summary>
    /// Automatic fix available (can be applied by the app).
    /// </summary>
    AutoFix,

    /// <summary>
    /// Manual fix required (user needs to do something).
    /// </summary>
    ManualFix,

    /// <summary>
    /// External tool required (download/install external software).
    /// </summary>
    ExternalTool,

    /// <summary>
    /// No fix available or not applicable.
    /// </summary>
    None
}

/// <summary>
/// Result of an automatic fix attempt.
/// </summary>
public class AutoFixResult
{
    /// <summary>
    /// Whether the fix was successful.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Message describing the result.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Additional details about the fix.
    /// </summary>
    public string? Details { get; set; }

    /// <summary>
    /// The updated error info after fix (if applicable).
    /// </summary>
    public GameErrorInfo? UpdatedError { get; set; }

    /// <summary>
    /// The detailed repair result when the attempted fix is one of the implemented repairs
    /// (folder rename / compatibility mode). Carries what was changed, what value was replaced and
    /// whether the change can be undone - the report page needs all three to offer "撤销".
    /// </summary>
    public GameHealthFixResult? Fix { get; set; }
}