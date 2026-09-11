using Galbox.Data.Entities;

namespace Galbox.App.Services;

/// <summary>
/// Interface for game metadata scraping from external sources.
/// </summary>
public interface IGameScrapingService
{
    /// <summary>
    /// Search for games across all available sources.
    /// Priority: Bangumi (Chinese) > VNDB > ymgal > cngal
    /// </summary>
    /// <param name="gameName">The game name to search for</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Aggregated scraping results from all sources</returns>
    Task<ScrapingResult> SearchGameAsync(string gameName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Search for games from a specific source.
    /// </summary>
    /// <param name="gameName">The game name to search for</param>
    /// <param name="source">The source to search from</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Scraping result from the specified source</returns>
    Task<SourceScrapingResult> SearchFromSourceAsync(string gameName, ScraperSource source, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get detailed metadata for a specific game from a source.
    /// </summary>
    /// <param name="sourceId">The ID from the source (e.g., Bangumi ID, VNDB ID)</param>
    /// <param name="source">The source to fetch from</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Detailed game metadata</returns>
    Task<GameMetadata?> GetGameDetailsAsync(string sourceId, ScraperSource source, CancellationToken cancellationToken = default);

    /// <summary>
    /// Auto-scrape game metadata, automatically accepting results with 90%+ match.
    /// </summary>
    /// <param name="gameInfo">The game to scrape metadata for</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Scraping result with auto-accept flag</returns>
    Task<AutoScrapeResult> AutoScrapeAsync(GameInfo gameInfo, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clear the scraping cache.
    /// </summary>
    void ClearCache();
}

/// <summary>
/// Enumeration of available scraping sources.
/// </summary>
public enum ScraperSource
{
    /// <summary>
    /// Bangumi (bgm.tv) - Chinese-focused game/anime database
    /// </summary>
    Bangumi,

    /// <summary>
    /// VNDB - Visual Novel Database
    /// </summary>
    Vndb,

    /// <summary>
    /// ymgal - Yumei Galgame database
    /// </summary>
    Ymgal,

    /// <summary>
    /// cngal - Chinese Galgame database
    /// </summary>
    Cngal
}

/// <summary>
/// Aggregated scraping result from multiple sources.
/// </summary>
public class ScrapingResult
{
    /// <summary>
    /// Results from each source.
    /// </summary>
    public Dictionary<ScraperSource, SourceScrapingResult> SourceResults { get; set; } = new();

    /// <summary>
    /// Best matching result (highest match score).
    /// </summary>
    public SourceScrapingResult? BestMatch { get; set; }

    /// <summary>
    /// Whether any source returned results.
    /// </summary>
    public bool HasResults => SourceResults.Any(r => r.Value.Items.Count > 0);

    /// <summary>
    /// Any errors that occurred during scraping.
    /// </summary>
    public List<string> Errors { get; set; } = new();
}

/// <summary>
/// Scraping result from a single source.
/// </summary>
public class SourceScrapingResult
{
    /// <summary>
    /// The source this result is from.
    /// </summary>
    public ScraperSource Source { get; set; }

    /// <summary>
    /// Search query that was used.
    /// </summary>
    public string SearchQuery { get; set; } = string.Empty;

    /// <summary>
    /// List of matching games found.
    /// </summary>
    public List<GameMetadata> Items { get; set; } = new();

    /// <summary>
    /// Whether this source was queried successfully.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Error message if the query failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Full exception info for debugging purposes.
    /// </summary>
    public string? ExtendedErrorInfo { get; set; }

    /// <summary>
    /// Time taken for the query in milliseconds.
    /// </summary>
    public long ElapsedMilliseconds { get; set; }
}

/// <summary>
/// Detailed game metadata from a scraping source.
/// </summary>
public class GameMetadata
{
    /// <summary>
    /// Unique ID from the source.
    /// </summary>
    public string SourceId { get; set; } = string.Empty;

    /// <summary>
    /// The source this metadata is from.
    /// </summary>
    public ScraperSource Source { get; set; }

    /// <summary>
    /// Chinese title.
    /// </summary>
    public string? TitleCn { get; set; }

    /// <summary>
    /// Original title (Japanese, English, etc.).
    /// </summary>
    public string? TitleOriginal { get; set; }

    /// <summary>
    /// All available titles/aliases.
    /// </summary>
    public List<string> Titles { get; set; } = new();

    /// <summary>
    /// Game description/summary.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Cover image URL.
    /// </summary>
    public string? CoverImageUrl { get; set; }

    /// <summary>
    /// Banner image URL.
    /// </summary>
    public string? BannerImageUrl { get; set; }

    /// <summary>
    /// List of tags/genres.
    /// </summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>
    /// List of characters.
    /// </summary>
    public List<CharacterInfo> Characters { get; set; } = new();

    /// <summary>
    /// Release date.
    /// </summary>
    public DateTime? ReleaseDate { get; set; }

    /// <summary>
    /// Developer/Publisher.
    /// </summary>
    public string? Developer { get; set; }

    /// <summary>
    /// Rating score (0-10 scale typically).
    /// </summary>
    public double? Rating { get; set; }

    /// <summary>
    /// Match score (0-100) against the search query.
    /// </summary>
    public double MatchScore { get; set; }

    /// <summary>
    /// Whether this is the best match from the source.
    /// </summary>
    public bool IsBestMatch { get; set; }

    /// <summary>
    /// Additional metadata from the source.
    /// </summary>
    public Dictionary<string, object> ExtendedData { get; set; } = new();
}

/// <summary>
/// Character information.
/// </summary>
public class CharacterInfo
{
    /// <summary>
    /// Character name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Character name in Chinese.
    /// </summary>
    public string? NameCn { get; set; }

    /// <summary>
    /// Character image URL.
    /// </summary>
    public string? ImageUrl { get; set; }

    /// <summary>
    /// Character role/type.
    /// </summary>
    public string? Role { get; set; }
}

/// <summary>
/// Result of auto-scraping operation.
/// </summary>
public class AutoScrapeResult
{
    /// <summary>
    /// Whether a result was auto-accepted (90%+ match).
    /// </summary>
    public bool AutoAccepted { get; set; }

    /// <summary>
    /// The best matching result found.
    /// </summary>
    public GameMetadata? BestMatch { get; set; }

    /// <summary>
    /// All available results for user selection.
    /// </summary>
    public List<GameMetadata> AllMatches { get; set; } = new();

    /// <summary>
    /// The source that provided the best match.
    /// </summary>
    public ScraperSource BestMatchSource { get; set; }

    /// <summary>
    /// Errors that occurred during scraping.
    /// </summary>
    public List<string> Errors { get; set; } = new();

    /// <summary>
    /// Per-source outcome of the search: query used, success flag, item count,
    /// elapsed time and error text (D12 - lets the UI explain a failure).
    /// </summary>
    public Dictionary<ScraperSource, SourceScrapingResult> SourceResults { get; set; } = new();
}