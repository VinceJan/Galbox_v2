using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Galbox.Data.Entities;
using Microsoft.Extensions.Logging;

namespace Galbox.App.Services;

/// <summary>
/// Interface for scraping cache service.
/// Provides local caching of API responses to reduce redundant API calls.
/// </summary>
public interface IScrapingCacheService
{
    /// <summary>
    /// Gets cached scraping result for a game name.
    /// </summary>
    /// <param name="gameName">The game name to look up</param>
    /// <returns>Cached result if available and not expired, null otherwise</returns>
    ScrapingCacheEntry? GetCachedResult(string gameName);

    /// <summary>
    /// Gets cached detailed metadata for a source ID.
    /// </summary>
    /// <param name="sourceId">The source ID</param>
    /// <param name="source">The scraper source</param>
    /// <returns>Cached metadata if available and not expired, null otherwise</returns>
    GameMetadata? GetCachedDetails(string sourceId, ScraperSource source);

    /// <summary>
    /// Stores scraping result in cache.
    /// </summary>
    /// <param name="gameName">The game name (cache key)</param>
    /// <param name="result">The scraping result to cache</param>
    void CacheResult(string gameName, ScrapingResult result);

    /// <summary>
    /// Stores detailed metadata in cache.
    /// </summary>
    /// <param name="sourceId">The source ID</param>
    /// <param name="source">The scraper source</param>
    /// <param name="metadata">The metadata to cache</param>
    void CacheDetails(string sourceId, ScraperSource source, GameMetadata metadata);

    /// <summary>
    /// Invalidates cache for a specific game (e.g., after user edit).
    /// </summary>
    /// <param name="gameId">The game ID to invalidate</param>
    /// <param name="gameName">The game name to invalidate</param>
    void InvalidateGameCache(int gameId, string? gameName = null);

    /// <summary>
    /// Clears all cached data.
    /// </summary>
    void ClearAllCache();

    /// <summary>
    /// Clears expired cache entries.
    /// </summary>
    /// <returns>Number of entries cleared</returns>
    int ClearExpiredEntries();

    /// <summary>
    /// Gets cache statistics.
    /// </summary>
    ScrapingCacheStats GetCacheStats();

    /// <summary>
    /// Sets the cache expiration duration.
    /// </summary>
    /// <param name="days">Number of days before cache expires</param>
    void SetExpirationDays(int days);

    /// <summary>
    /// Gets or sets whether caching is enabled.
    /// </summary>
    bool IsEnabled { get; set; }

    /// <summary>
    /// Initializes the cache service by loading existing cache from files.
    /// Should be called after DI resolution.
    /// </summary>
    /// <returns>Task representing the initialization</returns>
    Task InitializeAsync();
}

/// <summary>
/// Cached scraping result entry.
/// </summary>
public class ScrapingCacheEntry
{
    /// <summary>
    /// The game name used as cache key.
    /// </summary>
    public string GameName { get; set; } = string.Empty;

    /// <summary>
    /// Cached scraping result.
    /// </summary>
    public ScrapingResult? Result { get; set; }

    /// <summary>
    /// When the entry was cached.
    /// </summary>
    public DateTime CachedAt { get; set; }

    /// <summary>
    /// When the entry expires.
    /// </summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// Whether the entry is expired.
    /// </summary>
    public bool IsExpired => DateTime.UtcNow > ExpiresAt;

    /// <summary>
    /// Source of the best match.
    /// </summary>
    public ScraperSource? BestMatchSource { get; set; }

    /// <summary>
    /// Match score of the best result.
    /// </summary>
    public double? BestMatchScore { get; set; }
}

/// <summary>
/// Cached detailed metadata entry.
/// </summary>
public class DetailsCacheEntry
{
    /// <summary>
    /// The source ID.
    /// </summary>
    public string SourceId { get; set; } = string.Empty;

    /// <summary>
    /// The scraper source.
    /// </summary>
    public ScraperSource Source { get; set; }

    /// <summary>
    /// Cached metadata.
    /// </summary>
    public GameMetadata? Metadata { get; set; }

    /// <summary>
    /// When the entry was cached.
    /// </summary>
    public DateTime CachedAt { get; set; }

    /// <summary>
    /// When the entry expires.
    /// </summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// Whether the entry is expired.
    /// </summary>
    public bool IsExpired => DateTime.UtcNow > ExpiresAt;
}

/// <summary>
/// Cache statistics.
/// </summary>
public class ScrapingCacheStats
{
    /// <summary>
    /// Total number of cached entries.
    /// </summary>
    public int TotalEntries { get; set; }

    /// <summary>
    /// Number of search result cache entries.
    /// </summary>
    public int SearchEntries { get; set; }

    /// <summary>
    /// Number of details cache entries.
    /// </summary>
    public int DetailsEntries { get; set; }

    /// <summary>
    /// Number of expired entries.
    /// </summary>
    public int ExpiredEntries { get; set; }

    /// <summary>
    /// Total cache size in bytes (for file-based cache).
    /// </summary>
    public long CacheSizeBytes { get; set; }

    /// <summary>
    /// Cache hit count (since app start).
    /// </summary>
    public int HitCount { get; set; }

    /// <summary>
    /// Cache miss count (since app start).
    /// </summary>
    public int MissCount { get; set; }

    /// <summary>
    /// Cache hit rate percentage.
    /// </summary>
    public double HitRate => HitCount + MissCount > 0 ? (HitCount * 100.0 / (HitCount + MissCount)) : 0;

    /// <summary>
    /// Current expiration setting in days.
    /// </summary>
    public int ExpirationDays { get; set; }

    /// <summary>
    /// Whether caching is enabled.
    /// </summary>
    public bool IsEnabled { get; set; }
}

/// <summary>
/// Service for caching scraping API responses.
/// Uses file-based cache for persistence across app restarts.
/// </summary>
public class ScrapingCacheService : IScrapingCacheService
{
    private readonly ILogger<ScrapingCacheService> _logger;
    private readonly ConcurrentDictionary<string, ScrapingCacheEntry> _searchCache = new();
    private readonly ConcurrentDictionary<string, DetailsCacheEntry> _detailsCache = new();

    private readonly string _cacheDirectory;
    private int _expirationDays = 7;
    private bool _isEnabled = true;

    // Statistics tracking
    private int _hitCount;
    private int _missCount;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    /// <summary>
    /// Creates a ScrapingCacheService with injected logger.
    /// Note: Use InitializeAsync() after construction to load existing cache.
    /// </summary>
    public ScrapingCacheService(ILogger<ScrapingCacheService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Set up cache directory in app data folder
        var appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Galbox",
            "ScrapingCache");

        _cacheDirectory = appDataPath;

        // Ensure cache directory exists
        if (!Directory.Exists(_cacheDirectory))
        {
            Directory.CreateDirectory(_cacheDirectory);
            _logger.LogInformation("Created cache directory: {CacheDir}", _cacheDirectory);
        }
    }

    /// <summary>
    /// Initializes the cache service by loading existing cache from files.
    /// </summary>
    public async Task InitializeAsync()
    {
        await LoadCacheFromFilesAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public bool IsEnabled { get => _isEnabled; set => _isEnabled = value; }

    /// <inheritdoc />
    public ScrapingCacheEntry? GetCachedResult(string gameName)
    {
        if (!_isEnabled || string.IsNullOrWhiteSpace(gameName))
        {
            return null;
        }

        var key = NormalizeKey(gameName);

        if (_searchCache.TryGetValue(key, out var entry))
        {
            if (!entry.IsExpired)
            {
                Interlocked.Increment(ref _hitCount);
                _logger.LogDebug("Cache hit for search: {GameName}", gameName);
                return entry;
            }
            else
            {
                // Remove expired entry
                _searchCache.TryRemove(key, out _);
                DeleteCacheFile("search", key);
            }
        }

        Interlocked.Increment(ref _missCount);
        return null;
    }

    /// <inheritdoc />
    public GameMetadata? GetCachedDetails(string sourceId, ScraperSource source)
    {
        if (!_isEnabled || string.IsNullOrWhiteSpace(sourceId))
        {
            return null;
        }

        var key = $"{source}_{sourceId}";

        if (_detailsCache.TryGetValue(key, out var entry))
        {
            if (!entry.IsExpired)
            {
                Interlocked.Increment(ref _hitCount);
                _logger.LogDebug("Cache hit for details: {Source} {SourceId}", source, sourceId);
                return entry.Metadata;
            }
            else
            {
                // Remove expired entry
                _detailsCache.TryRemove(key, out _);
                DeleteCacheFile("details", key);
            }
        }

        Interlocked.Increment(ref _missCount);
        return null;
    }

    /// <inheritdoc />
    public void CacheResult(string gameName, ScrapingResult result)
    {
        if (!_isEnabled || string.IsNullOrWhiteSpace(gameName) || result == null)
        {
            return;
        }

        var key = NormalizeKey(gameName);
        var now = DateTime.UtcNow;
        var expiresAt = now.AddDays(_expirationDays);

        var entry = new ScrapingCacheEntry
        {
            GameName = gameName,
            Result = result,
            CachedAt = now,
            ExpiresAt = expiresAt,
            BestMatchSource = result.BestMatch?.Items?.FirstOrDefault()?.Source,
            BestMatchScore = result.BestMatch?.Items?.FirstOrDefault()?.MatchScore
        };

        _searchCache[key] = entry;
        SaveCacheToFileAsync("search", key, entry);

        _logger.LogDebug("Cached search result for: {GameName}, expires at {ExpiresAt}", gameName, expiresAt);
    }

    /// <inheritdoc />
    public void CacheDetails(string sourceId, ScraperSource source, GameMetadata metadata)
    {
        if (!_isEnabled || string.IsNullOrWhiteSpace(sourceId) || metadata == null)
        {
            return;
        }

        var key = $"{source}_{sourceId}";
        var now = DateTime.UtcNow;
        var expiresAt = now.AddDays(_expirationDays);

        var entry = new DetailsCacheEntry
        {
            SourceId = sourceId,
            Source = source,
            Metadata = metadata,
            CachedAt = now,
            ExpiresAt = expiresAt
        };

        _detailsCache[key] = entry;
        SaveCacheToFileAsync("details", key, entry);

        _logger.LogDebug("Cached details for: {Source} {SourceId}, expires at {ExpiresAt}", source, sourceId, expiresAt);
    }

    /// <inheritdoc />
    public void InvalidateGameCache(int gameId, string? gameName = null)
    {
        // Invalidate by game name if provided
        if (!string.IsNullOrWhiteSpace(gameName))
        {
            var key = NormalizeKey(gameName);
            if (_searchCache.TryRemove(key, out _))
            {
                DeleteCacheFile("search", key);
                _logger.LogInformation("Invalidated search cache for game: {GameName}", gameName);
            }
        }

        // Also clear any details cache associated with this game
        // This would require tracking gameId -> sourceId mapping, which is stored in GameInfo
        // For now, we just clear the search cache
    }

    /// <inheritdoc />
    public void ClearAllCache()
    {
        _searchCache.Clear();
        _detailsCache.Clear();

        // Delete all cache files
        try
        {
            if (Directory.Exists(_cacheDirectory))
            {
                foreach (var file in Directory.GetFiles(_cacheDirectory, "*.json"))
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete cache file: {File}", file);
                    }
                }
            }

            _logger.LogInformation("Cleared all cache data");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error clearing cache files");
        }

        // Reset statistics
        Interlocked.Exchange(ref _hitCount, 0);
        Interlocked.Exchange(ref _missCount, 0);
    }

    /// <inheritdoc />
    public int ClearExpiredEntries()
    {
        var clearedCount = 0;
        var now = DateTime.UtcNow;

        // Clear expired search entries
        var expiredSearchKeys = _searchCache
            .Where(kvp => kvp.Value.IsExpired)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in expiredSearchKeys)
        {
            _searchCache.TryRemove(key, out _);
            DeleteCacheFile("search", key);
            clearedCount++;
        }

        // Clear expired details entries
        var expiredDetailsKeys = _detailsCache
            .Where(kvp => kvp.Value.IsExpired)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in expiredDetailsKeys)
        {
            _detailsCache.TryRemove(key, out _);
            DeleteCacheFile("details", key);
            clearedCount++;
        }

        if (clearedCount > 0)
        {
            _logger.LogInformation("Cleared {Count} expired cache entries", clearedCount);
        }

        return clearedCount;
    }

    /// <inheritdoc />
    public ScrapingCacheStats GetCacheStats()
    {
        var stats = new ScrapingCacheStats
        {
            SearchEntries = _searchCache.Count,
            DetailsEntries = _detailsCache.Count,
            TotalEntries = _searchCache.Count + _detailsCache.Count,
            ExpiredEntries = _searchCache.Count(e => e.Value.IsExpired) + _detailsCache.Count(e => e.Value.IsExpired),
            HitCount = _hitCount,
            MissCount = _missCount,
            ExpirationDays = _expirationDays,
            IsEnabled = _isEnabled
        };

        // Calculate cache size
        try
        {
            if (Directory.Exists(_cacheDirectory))
            {
                stats.CacheSizeBytes = Directory.GetFiles(_cacheDirectory, "*.json")
                    .Sum(f => new FileInfo(f).Length);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error calculating cache size");
        }

        return stats;
    }

    /// <inheritdoc />
    public void SetExpirationDays(int days)
    {
        if (days < 1)
        {
            _logger.LogWarning("Invalid expiration days: {Days}, minimum is 1", days);
            return;
        }

        _expirationDays = days;
        _logger.LogInformation("Set cache expiration to {Days} days", days);
    }

    #region Private Methods

    private static string NormalizeKey(string key)
    {
        // Normalize to lowercase, trim whitespace
        return key.ToLowerInvariant().Trim();
    }

    private async Task LoadCacheFromFilesAsync()
    {
        try
        {
            if (!Directory.Exists(_cacheDirectory))
            {
                return;
            }

            // Load search cache files
            foreach (var file in Directory.GetFiles(_cacheDirectory, "search_*.json"))
            {
                try
                {
                    var content = await File.ReadAllTextAsync(file).ConfigureAwait(false);
                    var entry = JsonSerializer.Deserialize<ScrapingCacheEntry>(content, JsonOptions);

                    if (entry != null && !entry.IsExpired)
                    {
                        var key = NormalizeKey(entry.GameName);
                        _searchCache[key] = entry;
                    }
                    else if (entry != null && entry.IsExpired)
                    {
                        // Delete expired file
                        File.Delete(file);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load cache file: {File}", file);
                }
            }

            // Load details cache files
            foreach (var file in Directory.GetFiles(_cacheDirectory, "details_*.json"))
            {
                try
                {
                    var content = await File.ReadAllTextAsync(file).ConfigureAwait(false);
                    var entry = JsonSerializer.Deserialize<DetailsCacheEntry>(content, JsonOptions);

                    if (entry != null && !entry.IsExpired)
                    {
                        var key = $"{entry.Source}_{entry.SourceId}";
                        _detailsCache[key] = entry;
                    }
                    else if (entry != null && entry.IsExpired)
                    {
                        File.Delete(file);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load cache file: {File}", file);
                }
            }

            _logger.LogInformation("Loaded {Search} search entries and {Details} details entries from cache files",
                _searchCache.Count, _detailsCache.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading cache from files");
        }
    }

    private async void SaveCacheToFileAsync<T>(string type, string key, T entry)
    {
        try
        {
            // Sanitize key for file name (remove special characters)
            var safeKey = SanitizeKeyForFileName(key);
            var fileName = Path.Combine(_cacheDirectory, $"{type}_{safeKey}.json");

            var content = JsonSerializer.Serialize(entry, JsonOptions);
            await File.WriteAllTextAsync(fileName, content).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save cache file for {Type} {Key}", type, key);
        }
    }

    private void DeleteCacheFile(string type, string key)
    {
        try
        {
            var safeKey = SanitizeKeyForFileName(key);
            var fileName = Path.Combine(_cacheDirectory, $"{type}_{safeKey}.json");

            if (File.Exists(fileName))
            {
                File.Delete(fileName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete cache file for {Type} {Key}", type, key);
        }
    }

    private static string SanitizeKeyForFileName(string key)
    {
        // Replace invalid file name characters
        var invalidChars = new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|', ' ' };
        var safeKey = key;

        foreach (var c in invalidChars)
        {
            safeKey = safeKey.Replace(c, '_');
        }

        // Limit length to avoid too long file names
        if (safeKey.Length > 100)
        {
            safeKey = safeKey.Substring(0, 100);
        }

        return safeKey;
    }

    #endregion
}