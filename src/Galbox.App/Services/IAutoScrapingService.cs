using Galbox.Data.Entities;

namespace Galbox.App.Services;

/// <summary>
/// Interface for automatic background scraping service.
/// Handles batch scraping, progress reporting, and retry mechanisms.
/// </summary>
public interface IAutoScrapingService
{
    /// <summary>
    /// Adds a game to the scraping queue after it's added to library.
    /// </summary>
    /// <param name="gameId">The game ID to scrape</param>
    void EnqueueGame(int gameId);

    /// <summary>
    /// Adds multiple games to the scraping queue for batch processing.
    /// </summary>
    /// <param name="gameIds">Collection of game IDs to scrape</param>
    void EnqueueGames(IEnumerable<int> gameIds);

    /// <summary>
    /// Starts the background scraping process.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task representing the background operation</returns>
    Task StartScrapingAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the current scraping operation.
    /// </summary>
    void StopScraping();

    /// <summary>
    /// Gets the current scraping progress.
    /// </summary>
    ScrapingProgress GetCurrentProgress();

    /// <summary>
    /// Gets whether scraping is currently in progress.
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    /// Event raised when scraping progress changes.
    /// </summary>
    event EventHandler<ScrapingProgressEventArgs>? ProgressChanged;

    /// <summary>
    /// Event raised when a single game scraping completes.
    /// </summary>
    event EventHandler<GameScrapingResultEventArgs>? GameCompleted;

    /// <summary>
    /// Event raised when batch scraping completes.
    /// </summary>
    event EventHandler<BatchScrapingResultEventArgs>? BatchCompleted;

    /// <summary>
    /// Gets games that need manual review (match score < 90%).
    /// </summary>
    /// <returns>List of games requiring manual review</returns>
    Task<List<GameScrapingResult>> GetGamesNeedingReviewAsync();

    /// <summary>
    /// Applies metadata to a game from a selected scraping result.
    /// </summary>
    /// <param name="gameId">The game ID to update</param>
    /// <param name="metadata">The metadata to apply</param>
    /// <param name="preserveUserFields">Fields to preserve from user customization</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <param name="vndbId">VNDB vn id to persist alongside the metadata (cross-system key).</param>
    Task ApplyMetadataAsync(
        int gameId,
        GameMetadata metadata,
        List<string>? preserveUserFields = null,
        CancellationToken cancellationToken = default,
        string? vndbId = null);

    /// <summary>
    /// Merges metadata preview showing what would change.
    /// </summary>
    /// <param name="gameId">The game ID</param>
    /// <param name="metadata">The new metadata</param>
    /// <returns>Preview of changes</returns>
    Task<MetadataMergePreview> PreviewMergeAsync(int gameId, GameMetadata metadata);
}

/// <summary>
/// Progress information for batch scraping.
/// </summary>
public class ScrapingProgress
{
    /// <summary>
    /// Total number of games in the batch.
    /// </summary>
    public int TotalGames { get; set; }

    /// <summary>
    /// Number of games completed (success or failed).
    /// </summary>
    public int CompletedGames { get; set; }

    /// <summary>
    /// Number of games successfully scraped.
    /// </summary>
    public int SuccessCount { get; set; }

    /// <summary>
    /// Number of games that failed to scrape.
    /// </summary>
    public int FailedCount { get; set; }

    /// <summary>
    /// Number of games skipped (already have metadata).
    /// </summary>
    public int SkippedCount { get; set; }

    /// <summary>
    /// Number of games needing manual review.
    /// </summary>
    public int NeedsReviewCount { get; set; }

    /// <summary>
    /// Current game being processed.
    /// </summary>
    public string? CurrentGameName { get; set; }

    /// <summary>
    /// Progress percentage (0-100).
    /// </summary>
    public double ProgressPercentage => TotalGames > 0 ? (CompletedGames * 100.0 / TotalGames) : 0;

    /// <summary>
    /// Whether the operation was cancelled.
    /// </summary>
    public bool IsCancelled { get; set; }

    /// <summary>
    /// Whether the operation is complete.
    /// </summary>
    public bool IsComplete => CompletedGames >= TotalGames || IsCancelled;

    /// <summary>
    /// Time elapsed since operation started.
    /// </summary>
    public TimeSpan ElapsedTime { get; set; }

    /// <summary>
    /// Estimated remaining time based on average processing time.
    /// </summary>
    public TimeSpan? EstimatedRemainingTime { get; set; }
}

/// <summary>
/// Result of scraping a single game.
/// </summary>
public class GameScrapingResult
{
    /// <summary>
    /// The game ID that was scraped.
    /// </summary>
    public int GameId { get; set; }

    /// <summary>
    /// The game name for display.
    /// </summary>
    public string? GameName { get; set; }

    /// <summary>
    /// Status of the scraping operation.
    /// </summary>
    public GameScrapingStatus Status { get; set; }

    /// <summary>
    /// Best matching metadata found.
    /// </summary>
    public GameMetadata? BestMatch { get; set; }

    /// <summary>
    /// Match score of the best result (0-100).
    /// </summary>
    public double MatchScore { get; set; }

    /// <summary>
    /// Whether the result was auto-accepted (90%+ match).
    /// </summary>
    public bool AutoAccepted { get; set; }

    /// <summary>
    /// Source that provided the best match.
    /// </summary>
    public ScraperSource BestMatchSource { get; set; }

    /// <summary>
    /// All available matches for user selection.
    /// </summary>
    public List<GameMetadata> AllMatches { get; set; } = new();

    /// <summary>
    /// Error message if scraping failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// All error messages reported by the sources for this game.
    /// </summary>
    public List<string> Errors { get; set; } = new();

    /// <summary>
    /// Per-source diagnostics (query, success, item count, elapsed, error text)
    /// so the UI can explain exactly why a game was not matched (D12).
    /// </summary>
    public Dictionary<ScraperSource, SourceScrapingResult> SourceResults { get; set; } = new();

    /// <summary>
    /// Retry count for failed operations.
    /// </summary>
    public int RetryCount { get; set; }

    /// <summary>
    /// Time taken for scraping in milliseconds.
    /// </summary>
    public long ElapsedMilliseconds { get; set; }
}

/// <summary>
/// Status of a game scraping operation.
/// </summary>
public enum GameScrapingStatus
{
    /// <summary>
    /// Successfully scraped and auto-accepted.
    /// </summary>
    SuccessAutoAccepted,

    /// <summary>
    /// Successfully scraped, needs manual review.
    /// </summary>
    SuccessNeedsReview,

    /// <summary>
    /// Successfully scraped but no match found.
    /// </summary>
    NoMatch,

    /// <summary>
    /// Skipped because game already has complete metadata.
    /// </summary>
    SkippedAlreadyComplete,

    /// <summary>
    /// Failed due to network/API error.
    /// </summary>
    Failed,

    /// <summary>
    /// Queued for processing.
    /// </summary>
    Queued,

    /// <summary>
    /// Currently being processed.
    /// </summary>
    Processing,

    /// <summary>
    /// Cancelled by user.
    /// </summary>
    Cancelled
}

/// <summary>
/// Event args for progress changes.
/// </summary>
public class ScrapingProgressEventArgs : EventArgs
{
    public ScrapingProgress Progress { get; }

    public ScrapingProgressEventArgs(ScrapingProgress progress)
    {
        Progress = progress;
    }
}

/// <summary>
/// Event args for single game completion.
/// </summary>
public class GameScrapingResultEventArgs : EventArgs
{
    public GameScrapingResult Result { get; }

    public GameScrapingResultEventArgs(GameScrapingResult result)
    {
        Result = result;
    }
}

/// <summary>
/// Event args for batch completion.
/// </summary>
public class BatchScrapingResultEventArgs : EventArgs
{
    /// <summary>
    /// All results from the batch.
    /// </summary>
    public List<GameScrapingResult> Results { get; }

    /// <summary>
    /// Summary statistics.
    /// </summary>
    public ScrapingProgress Summary { get; }

    public BatchScrapingResultEventArgs(List<GameScrapingResult> results, ScrapingProgress summary)
    {
        Results = results;
        Summary = summary;
    }
}

/// <summary>
/// Preview of metadata merge showing changes.
/// </summary>
public class MetadataMergePreview
{
    /// <summary>
    /// Game ID being previewed.
    /// </summary>
    public int GameId { get; set; }

    /// <summary>
    /// Fields that would be changed.
    /// </summary>
    public List<MetadataFieldChange> Changes { get; set; } = new();

    /// <summary>
    /// Fields that would remain unchanged (empty new value or preserved).
    /// </summary>
    public List<MetadataFieldChange> Unchanged { get; set; } = new();

    /// <summary>
    /// Number of fields that would change.
    /// </summary>
    public int ChangesCount => Changes.Count;

    /// <summary>
    /// Whether any changes would be made.
    /// </summary>
    public bool HasChanges => Changes.Count > 0;
}

/// <summary>
/// Change detail for a single metadata field.
/// </summary>
public class MetadataFieldChange
{
    /// <summary>
    /// Field name (e.g., "NameCn", "Description", "CoverImageUrl").
    /// </summary>
    public string FieldName { get; set; } = string.Empty;

    /// <summary>
    /// Current value (may be null or empty).
    /// </summary>
    public string? CurrentValue { get; set; }

    /// <summary>
    /// New value from scraping (may be null).
    /// </summary>
    public string? NewValue { get; set; }

    /// <summary>
    /// Whether this field is marked for preservation.
    /// </summary>
    public bool IsPreserved { get; set; }

    /// <summary>
    /// Human-readable description of the change.
    /// </summary>
    public string? ChangeDescription { get; set; }
}