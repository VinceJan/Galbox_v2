using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Galbox.Core.Api;
using Galbox.Data.Entities;

namespace Galbox.App.Services;

/// <summary>
/// Service for scraping game metadata from external sources.
/// Priority: Bangumi (Chinese) > VNDB > ymgal > cngal
/// </summary>
public class GameScrapingService : IGameScrapingService
{
    private readonly BangumiApi _bangumiApi;
    private readonly VndbApi _vndbApi;
    private readonly YmgalApi _ymgalApi;
    private readonly CngalApi _cngalApi;
    private readonly IScrapingCacheService? _cacheService;

    // Pre-compiled regex patterns for name cleaning
    private static readonly Regex VersionPattern = new(
        @"\s*[\[\(][vV]?\d+[\.\d]*[\]\)]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BracketPattern = new(
        @"\s*\[[^\]]+\]\s*$",
        RegexOptions.Compiled);

    private static readonly Regex SpecialCharsPattern = new(
        @"[^\w\s\-]",
        RegexOptions.Compiled);

    // Thread-safe in-memory cache using ConcurrentDictionary (short-term)
    private readonly ConcurrentDictionary<string, CacheEntry> _memoryCache = new();
    private readonly TimeSpan _memoryCacheExpiration = TimeSpan.FromMinutes(30);

    // Thread-safe rate limiting using SemaphoreSlim
    private readonly SemaphoreSlim _bangumiRateLimitLock = new(1, 1);
    private readonly SemaphoreSlim _vndbRateLimitLock = new(1, 1);
    private const int BangumiRateLimitMs = 500; // Bangumi has rate limits
    private const int VndbRateLimitMs = 200;
    private DateTime _lastBangumiRequest = DateTime.MinValue;
    private DateTime _lastVndbRequest = DateTime.MinValue;

    // Match threshold for auto-accept (90%)
    private const double AutoAcceptThreshold = 90.0;

    /// <summary>
    /// Creates a GameScrapingService with injected API clients and optional cache service.
    /// </summary>
    /// <param name="bangumiApi">Bangumi API client</param>
    /// <param name="vndbApi">VNDB API client</param>
    /// <param name="ymgalApi">ymgal API client</param>
    /// <param name="cngalApi">cngal API client</param>
    /// <param name="cacheService">Optional cache service for persistent caching</param>
    public GameScrapingService(
        BangumiApi bangumiApi,
        VndbApi vndbApi,
        YmgalApi ymgalApi,
        CngalApi cngalApi,
        IScrapingCacheService? cacheService = null)
    {
        _bangumiApi = bangumiApi ?? throw new ArgumentNullException(nameof(bangumiApi));
        _vndbApi = vndbApi ?? throw new ArgumentNullException(nameof(vndbApi));
        _ymgalApi = ymgalApi ?? throw new ArgumentNullException(nameof(ymgalApi));
        _cngalApi = cngalApi ?? throw new ArgumentNullException(nameof(cngalApi));
        _cacheService = cacheService;
    }

    /// <summary>
    /// Search for games across all available sources.
    /// </summary>
    public async Task<ScrapingResult> SearchGameAsync(string gameName, CancellationToken cancellationToken = default)
    {
        var result = new ScrapingResult();

        // Clean the game name for better search results
        var cleanName = CleanGameName(gameName);

        // Check persistent cache first (if available)
        if (_cacheService != null)
        {
            var cachedEntry = _cacheService.GetCachedResult(cleanName);
            if (cachedEntry?.Result != null)
            {
                return cachedEntry.Result;
            }
        }

        // Check in-memory cache
        if (TryGetFromMemoryCache(cleanName, result))
        {
            return result;
        }

        // Search from all sources in parallel (but respect rate limits)
        var bangumiTask = SearchFromSourceAsync(cleanName, ScraperSource.Bangumi, cancellationToken);
        var vndbTask = SearchFromSourceAsync(cleanName, ScraperSource.Vndb, cancellationToken);
        var ymgalTask = SearchFromSourceAsync(cleanName, ScraperSource.Ymgal, cancellationToken);
        var cngalTask = SearchFromSourceAsync(cleanName, ScraperSource.Cngal, cancellationToken);

        await Task.WhenAll(bangumiTask, vndbTask, ymgalTask, cngalTask).ConfigureAwait(false);

        // Collect results
        var bangumiResult = await bangumiTask.ConfigureAwait(false);
        var vndbResult = await vndbTask.ConfigureAwait(false);
        var ymgalResult = await ymgalTask.ConfigureAwait(false);
        var cngalResult = await cngalTask.ConfigureAwait(false);

        result.SourceResults[ScraperSource.Bangumi] = bangumiResult;
        result.SourceResults[ScraperSource.Vndb] = vndbResult;
        result.SourceResults[ScraperSource.Ymgal] = ymgalResult;
        result.SourceResults[ScraperSource.Cngal] = cngalResult;

        // Collect errors
        if (!bangumiResult.Success && !string.IsNullOrEmpty(bangumiResult.ErrorMessage))
            result.Errors.Add($"Bangumi: {bangumiResult.ErrorMessage}");
        if (!vndbResult.Success && !string.IsNullOrEmpty(vndbResult.ErrorMessage))
            result.Errors.Add($"VNDB: {vndbResult.ErrorMessage}");

        // Find best match across all sources (priority: Bangumi > VNDB > ymgal > cngal)
        result.BestMatch = FindBestMatch(gameName, result.SourceResults);

        // Cache the result in both caches
        AddToMemoryCache(cleanName, result);
        if (_cacheService != null)
        {
            _cacheService.CacheResult(cleanName, result);
        }

        return result;
    }

    /// <summary>
    /// Search for games from a specific source.
    /// </summary>
    public async Task<SourceScrapingResult> SearchFromSourceAsync(
        string gameName,
        ScraperSource source,
        CancellationToken cancellationToken = default)
    {
        var result = new SourceScrapingResult
        {
            Source = source,
            SearchQuery = gameName
        };

        var stopwatch = Stopwatch.StartNew();

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (source)
            {
                case ScraperSource.Bangumi:
                    await ApplyRateLimitAsync(ScraperSource.Bangumi, cancellationToken).ConfigureAwait(false);
                    result = await SearchBangumiAsync(gameName, cancellationToken).ConfigureAwait(false);
                    break;

                case ScraperSource.Vndb:
                    await ApplyRateLimitAsync(ScraperSource.Vndb, cancellationToken).ConfigureAwait(false);
                    result = await SearchVndbAsync(gameName, cancellationToken).ConfigureAwait(false);
                    break;

                case ScraperSource.Ymgal:
                    result = await SearchYmgalAsync(gameName, cancellationToken).ConfigureAwait(false);
                    break;

                case ScraperSource.Cngal:
                    result = await SearchCngalAsync(gameName, cancellationToken).ConfigureAwait(false);
                    break;
            }

            result.Success = result.Items.Count > 0;
        }
        catch (OperationCanceledException)
        {
            result.Success = false;
            result.ErrorMessage = "Operation cancelled";
        }
        catch (HttpRequestException ex)
        {
            result.Success = false;
            result.ErrorMessage = $"Network error: {ex.Message}";
            // Preserve full exception info for debugging
            result.ExtendedErrorInfo = ex.ToString();
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = $"Unexpected error: {ex.Message}";
            // Preserve full exception info for debugging
            result.ExtendedErrorInfo = ex.ToString();
        }

        stopwatch.Stop();
        result.ElapsedMilliseconds = stopwatch.ElapsedMilliseconds;

        // Calculate match scores for all items
        foreach (var item in result.Items)
        {
            item.MatchScore = CalculateMatchScore(gameName, item.Titles);
        }

        // Mark best match within source
        var bestInSource = result.Items.OrderByDescending(i => i.MatchScore).FirstOrDefault();
        if (bestInSource != null)
        {
            bestInSource.IsBestMatch = true;
        }

        return result;
    }

    /// <summary>
    /// Get detailed metadata for a specific game from a source.
    /// </summary>
    public async Task<GameMetadata?> GetGameDetailsAsync(
        string sourceId,
        ScraperSource source,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            switch (source)
            {
                case ScraperSource.Bangumi:
                    if (int.TryParse(sourceId, out var bangumiId))
                    {
                        await ApplyRateLimitAsync(ScraperSource.Bangumi, cancellationToken).ConfigureAwait(false);
                        return await GetBangumiDetailsAsync(bangumiId, cancellationToken).ConfigureAwait(false);
                    }
                    return null;

                case ScraperSource.Vndb:
                    await ApplyRateLimitAsync(ScraperSource.Vndb, cancellationToken).ConfigureAwait(false);
                    return await GetVndbDetailsAsync(sourceId, cancellationToken).ConfigureAwait(false);

                case ScraperSource.Ymgal:
                    // Stub - return null
                    return null;

                case ScraperSource.Cngal:
                    // Stub - return null
                    return null;
            }
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        return null;
    }

    /// <summary>
    /// Auto-scrape game metadata, automatically accepting results with 90%+ match.
    /// </summary>
    public async Task<AutoScrapeResult> AutoScrapeAsync(GameInfo gameInfo, CancellationToken cancellationToken = default)
    {
        var result = new AutoScrapeResult();

        // Handle null gameInfo or null names - prefer NameCn, fallback to NameOriginal
        var searchName = gameInfo?.NameCn;
        if (string.IsNullOrWhiteSpace(searchName))
        {
            searchName = gameInfo?.NameOriginal ?? string.Empty;
        }
        if (string.IsNullOrWhiteSpace(searchName))
        {
            result.Errors.Add("No game name provided for scraping");
            return result;
        }

        // Search from all sources
        var scrapingResult = await SearchGameAsync(searchName, cancellationToken).ConfigureAwait(false);

        // Collect all matches with their scores
        foreach (var sourceResult in scrapingResult.SourceResults)
        {
            foreach (var item in sourceResult.Value.Items)
            {
                result.AllMatches.Add(item);
            }
        }

        result.Errors = scrapingResult.Errors;

        // Find best match
        var bestMatch = result.AllMatches.OrderByDescending(m => m.MatchScore).FirstOrDefault();

        if (bestMatch != null)
        {
            result.BestMatch = bestMatch;
            result.BestMatchSource = bestMatch.Source;

            // Auto-accept if 90%+ match
            if (bestMatch.MatchScore >= AutoAcceptThreshold)
            {
                result.AutoAccepted = true;
            }
        }

        return result;
    }

    /// <summary>
    /// Clear the scraping cache.
    /// </summary>
    public void ClearCache()
    {
        _memoryCache.Clear();
        if (_cacheService != null)
        {
            _cacheService.ClearAllCache();
        }
    }

    #region Private Search Methods

    private async Task<SourceScrapingResult> SearchBangumiAsync(
        string gameName,
        CancellationToken cancellationToken)
    {
        var result = new SourceScrapingResult
        {
            Source = ScraperSource.Bangumi,
            SearchQuery = gameName
        };

        var searchResponse = await _bangumiApi.SearchAsync(gameName, 4, cancellationToken).ConfigureAwait(false);

        if (searchResponse?.Items != null)
        {
            foreach (var item in searchResponse.Items)
            {
                var allTitles = item.GetAllTitles();
                var metadata = new GameMetadata
                {
                    SourceId = item.Id.ToString(),
                    Source = ScraperSource.Bangumi,
                    TitleCn = item.NameCn,
                    TitleOriginal = item.Name,
                    Titles = new List<string>(allTitles), // Create new list from titles
                    Description = item.Summary,
                    CoverImageUrl = item.Images?.Large ?? item.Images?.Common,
                    MatchScore = CalculateMatchScore(gameName, allTitles)
                };

                result.Items.Add(metadata);
            }
        }

        return result;
    }

    private async Task<SourceScrapingResult> SearchVndbAsync(
        string gameName,
        CancellationToken cancellationToken)
    {
        var result = new SourceScrapingResult
        {
            Source = ScraperSource.Vndb,
            SearchQuery = gameName
        };

        var searchResponse = await _vndbApi.SearchByTitleAsync(gameName, cancellationToken).ConfigureAwait(false);

        if (searchResponse?.Results != null)
        {
            foreach (var vn in searchResponse.Results)
            {
                var allTitles = vn.GetAllTitles();
                var metadata = new GameMetadata
                {
                    SourceId = vn.Id,
                    Source = ScraperSource.Vndb,
                    TitleCn = vn.GetChineseTitle(),
                    TitleOriginal = vn.Title,
                    Titles = new List<string>(allTitles), // Create new list from titles
                    Description = vn.Description,
                    CoverImageUrl = vn.Image?.Url,
                    ReleaseDate = vn.GetReleaseDate(),
                    Rating = vn.Rating,
                    MatchScore = CalculateMatchScore(gameName, allTitles)
                };

                if (vn.LengthMinutes.HasValue)
                {
                    metadata.ExtendedData["LengthMinutes"] = vn.LengthMinutes.Value;
                }

                result.Items.Add(metadata);
            }
        }

        return result;
    }

    private async Task<SourceScrapingResult> SearchYmgalAsync(
        string gameName,
        CancellationToken cancellationToken)
    {
        var result = new SourceScrapingResult
        {
            Source = ScraperSource.Ymgal,
            SearchQuery = gameName
        };

        // Stub - ymgal API needs research
        var searchResponse = await _ymgalApi.SearchAsync(gameName, cancellationToken).ConfigureAwait(false);

        if (searchResponse?.Items != null)
        {
            foreach (var item in searchResponse.Items)
            {
                var metadata = new GameMetadata
                {
                    SourceId = item.Id,
                    Source = ScraperSource.Ymgal,
                    TitleCn = item.TitleCn,
                    TitleOriginal = item.Title,
                    Description = item.Description,
                    CoverImageUrl = item.CoverUrl
                };

                result.Items.Add(metadata);
            }
        }

        return result;
    }

    private async Task<SourceScrapingResult> SearchCngalAsync(
        string gameName,
        CancellationToken cancellationToken)
    {
        var result = new SourceScrapingResult
        {
            Source = ScraperSource.Cngal,
            SearchQuery = gameName
        };

        // Stub - cngal API needs research
        var searchResponse = await _cngalApi.SearchAsync(gameName, cancellationToken).ConfigureAwait(false);

        if (searchResponse?.Items != null)
        {
            foreach (var item in searchResponse.Items)
            {
                var metadata = new GameMetadata
                {
                    SourceId = item.Id,
                    Source = ScraperSource.Cngal,
                    TitleCn = item.TitleCn,
                    TitleOriginal = item.Title,
                    Description = item.Description,
                    CoverImageUrl = item.CoverUrl
                };

                result.Items.Add(metadata);
            }
        }

        return result;
    }

    private async Task<GameMetadata?> GetBangumiDetailsAsync(
        int subjectId,
        CancellationToken cancellationToken)
    {
        var subject = await _bangumiApi.GetSubjectAsync(subjectId, cancellationToken).ConfigureAwait(false);
        if (subject == null) return null;

        // Get additional data (characters, tags)
        var characters = await _bangumiApi.GetCharactersAsync(subjectId, cancellationToken).ConfigureAwait(false);
        var tags = await _bangumiApi.GetTagsAsync(subjectId, cancellationToken).ConfigureAwait(false);

        var metadata = new GameMetadata
        {
            SourceId = subject.Id.ToString(),
            Source = ScraperSource.Bangumi,
            TitleCn = subject.NameCn,
            TitleOriginal = subject.Name,
            Description = subject.Summary,
            CoverImageUrl = subject.Images?.Large ?? subject.Images?.Common,
            ReleaseDate = subject.GetReleaseDate(),
            Developer = subject.GetDeveloper()
        };

        // Add titles
        if (!string.IsNullOrWhiteSpace(subject.Name))
            metadata.Titles.Add(subject.Name);
        if (!string.IsNullOrWhiteSpace(subject.NameCn))
            metadata.Titles.Add(subject.NameCn);

        // Add tags
        metadata.Tags = tags.Select(t => t.Name).ToList();

        // Add characters
        metadata.Characters = characters.Select(c => new CharacterInfo
        {
            Name = c.Name,
            NameCn = c.NameCn,
            ImageUrl = c.Images?.Large ?? c.Images?.Common,
            Role = c.Relation
        }).ToList();

        // Add rating
        if (subject.Rating != null)
        {
            metadata.ExtendedData["Rating"] = subject.Rating.Score;
            metadata.ExtendedData["RatingCount"] = subject.Rating.Total;
        }

        return metadata;
    }

    private async Task<GameMetadata?> GetVndbDetailsAsync(
        string vnId,
        CancellationToken cancellationToken)
    {
        var vnResponse = await _vndbApi.GetVnByIdAsync(vnId, cancellationToken).ConfigureAwait(false);
        var vn = vnResponse?.Results?.FirstOrDefault();
        if (vn == null) return null;

        var allTitles = vn.GetAllTitles();
        var metadata = new GameMetadata
        {
            SourceId = vn.Id,
            Source = ScraperSource.Vndb,
            TitleCn = vn.GetChineseTitle(),
            TitleOriginal = vn.Title,
            Titles = new List<string>(allTitles), // Create new list from titles
            Description = vn.Description,
            CoverImageUrl = vn.Image?.Url,
            ReleaseDate = vn.GetReleaseDate(),
            Rating = vn.Rating
        };

        // Add developer
        if (vn.Developers?.Any() == true)
        {
            metadata.Developer = vn.Developers.FirstOrDefault()?.Name;
        }

        // Add tags
        if (vn.Tags != null)
        {
            metadata.Tags = vn.Tags
                .Where(t => t.Rating > 1.0) // Only include relevant tags
                .OrderByDescending(t => t.Rating)
                .Select(t => t.Name ?? t.Id.ToString())
                .ToList();
        }

        // Add characters
        if (vn.Characters != null)
        {
            metadata.Characters = vn.Characters.Select(c => new CharacterInfo
            {
                Name = c.Name,
                NameCn = c.Original,
                ImageUrl = c.Image?.Url
            }).ToList();
        }

        // Add length info
        if (vn.LengthMinutes.HasValue)
        {
            metadata.ExtendedData["LengthMinutes"] = vn.LengthMinutes.Value;
        }

        return metadata;
    }

    #endregion

    #region Private Helper Methods

    /// <summary>
    /// Clean game name for better search results.
    /// Uses pre-compiled regex patterns.
    /// </summary>
    private static string CleanGameName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        // Remove version patterns
        var clean = VersionPattern.Replace(name, "");

        // Remove bracket suffixes
        clean = BracketPattern.Replace(clean, "");

        // Trim whitespace
        clean = clean.Trim();

        return clean;
    }

    /// <summary>
    /// Calculate match score (0-100) between game name and potential titles.
    /// Uses Levenshtein distance-based similarity.
    /// </summary>
    private static double CalculateMatchScore(string searchName, List<string> potentialTitles)
    {
        if (string.IsNullOrWhiteSpace(searchName) || potentialTitles.Count == 0)
            return 0;

        var searchLower = searchName.ToLowerInvariant();
        var searchClean = CleanGameName(searchName).ToLowerInvariant();

        double bestScore = 0;

        foreach (var title in potentialTitles)
        {
            if (string.IsNullOrWhiteSpace(title))
                continue;

            var titleLower = title.ToLowerInvariant();

            // Exact match
            if (searchLower == titleLower || searchClean == titleLower)
            {
                return 100;
            }

            // Contains match (one contains the other)
            if (searchClean.Contains(titleLower) || titleLower.Contains(searchClean))
            {
                bestScore = Math.Max(bestScore, 90);
                continue;
            }

            // Calculate Levenshtein similarity
            var similarity = CalculateLevenshteinSimilarity(searchClean, titleLower);
            bestScore = Math.Max(bestScore, similarity);
        }

        return bestScore;
    }

    /// <summary>
    /// Calculate similarity using Levenshtein distance.
    /// Returns a score from 0-100.
    /// </summary>
    private static double CalculateLevenshteinSimilarity(string source, string target)
    {
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(target))
            return 0;

        if (source == target)
            return 100;

        var distance = LevenshteinDistance(source, target);
        var maxLength = Math.Max(source.Length, target.Length);

        if (maxLength == 0)
            return 100;

        var similarity = (1.0 - distance / maxLength) * 100;
        return Math.Round(similarity, 2);
    }

    /// <summary>
    /// Calculate Levenshtein distance between two strings.
    /// </summary>
    private static int LevenshteinDistance(string source, string target)
    {
        var sourceLength = source.Length;
        var targetLength = target.Length;

        if (sourceLength == 0) return targetLength;
        if (targetLength == 0) return sourceLength;

        // Use optimized algorithm with single row
        var previousRow = new int[targetLength + 1];
        var currentRow = new int[targetLength + 1];

        // Initialize first row
        for (var i = 0; i <= targetLength; i++)
        {
            previousRow[i] = i;
        }

        for (var i = 0; i < sourceLength; i++)
        {
            currentRow[0] = i + 1;

            for (var j = 0; j < targetLength; j++)
            {
                var cost = source[i] == target[j] ? 0 : 1;
                currentRow[j + 1] = Math.Min(
                    Math.Min(currentRow[j] + 1, previousRow[j + 1] + 1),
                    previousRow[j] + cost);
            }

            // Swap rows
            (previousRow, currentRow) = (currentRow, previousRow);
        }

        return previousRow[targetLength];
    }

    /// <summary>
    /// Find the best match across all sources.
    /// Priority: Bangumi > VNDB > ymgal > cngal for same score.
    /// </summary>
    private static SourceScrapingResult? FindBestMatch(
        string gameName,
        Dictionary<ScraperSource, SourceScrapingResult> sourceResults)
    {
        // Source priority order
        var priorityOrder = new[] { ScraperSource.Bangumi, ScraperSource.Vndb, ScraperSource.Ymgal, ScraperSource.Cngal };

        GameMetadata? bestMatch = null;
        ScraperSource bestSource = ScraperSource.Bangumi;
        double bestScore = 0;

        foreach (var source in priorityOrder)
        {
            if (!sourceResults.TryGetValue(source, out var result) || !result.Success)
                continue;

            var sourceBest = result.Items.OrderByDescending(i => i.MatchScore).FirstOrDefault();
            if (sourceBest == null)
                continue;

            // Higher score wins, or same score with higher priority source
            if (sourceBest.MatchScore > bestScore)
            {
                bestMatch = sourceBest;
                bestScore = sourceBest.MatchScore;
                bestSource = source;
            }
        }

        if (bestMatch != null)
        {
            bestMatch.IsBestMatch = true;
            return sourceResults[bestSource];
        }

        return null;
    }

    /// <summary>
    /// Apply rate limiting for API calls using thread-safe locks.
    /// </summary>
    private async Task ApplyRateLimitAsync(ScraperSource source, CancellationToken cancellationToken)
    {
        var (rateLimitLock, delayMs) = source switch
        {
            ScraperSource.Bangumi => (_bangumiRateLimitLock, BangumiRateLimitMs),
            ScraperSource.Vndb => (_vndbRateLimitLock, VndbRateLimitMs),
            _ => (null, 0)
        };

        if (rateLimitLock != null && delayMs > 0)
        {
            await rateLimitLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var lastRequest = source == ScraperSource.Bangumi ? _lastBangumiRequest : _lastVndbRequest;
                var elapsed = DateTime.UtcNow - lastRequest;
                if (elapsed.TotalMilliseconds < delayMs)
                {
                    var waitTime = delayMs - (int)elapsed.TotalMilliseconds;
                    await Task.Delay(waitTime, cancellationToken).ConfigureAwait(false);
                }

                // Update last request time
                if (source == ScraperSource.Bangumi)
                {
                    _lastBangumiRequest = DateTime.UtcNow;
                }
                else if (source == ScraperSource.Vndb)
                {
                    _lastVndbRequest = DateTime.UtcNow;
                }
            }
            finally
            {
                rateLimitLock.Release();
            }
        }
    }

    /// <summary>
    /// Try to get cached result from memory cache.
    /// </summary>
    private bool TryGetFromMemoryCache(string searchKey, ScrapingResult result)
    {
        if (_memoryCache.TryGetValue(searchKey, out var entry))
        {
            if (DateTime.UtcNow - entry.Timestamp < _memoryCacheExpiration)
            {
                result.SourceResults = entry.SourceResults;
                result.BestMatch = entry.BestMatch;
                result.Errors = entry.Errors;
                return true;
            }
            else
            {
                // Remove expired entry
                _memoryCache.TryRemove(searchKey, out _);
            }
        }
        return false;
    }

    /// <summary>
    /// Add result to memory cache.
    /// </summary>
    private void AddToMemoryCache(string searchKey, ScrapingResult result)
    {
        _memoryCache[searchKey] = new CacheEntry
        {
            Timestamp = DateTime.UtcNow,
            SourceResults = result.SourceResults,
            BestMatch = result.BestMatch,
            Errors = result.Errors
        };
    }

    #endregion

    #region Cache Entry

    private class CacheEntry
    {
        public DateTime Timestamp { get; set; }
        public Dictionary<ScraperSource, SourceScrapingResult> SourceResults { get; set; } = new();
        public SourceScrapingResult? BestMatch { get; set; }
        public List<string> Errors { get; set; } = new();
    }

    #endregion
}