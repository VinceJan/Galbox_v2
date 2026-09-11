using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Galbox.Core.Api;
using Galbox.Data.Entities;
using Microsoft.Extensions.Logging;

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
    private readonly IScrapingSettingsProvider? _settingsProvider;
    private readonly ILogger<GameScrapingService>? _logger;

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
    private const int BangumiRateLimitMs = 200; // Bangumi official limit: 5 requests/second
    private const int VndbRateLimitMs = 250; // VNDB recommended: 4 requests/second
    private DateTime _lastBangumiRequest = DateTime.MinValue;
    private DateTime _lastVndbRequest = DateTime.MinValue;

    // Match threshold for auto-accept. The effective value comes from
    // UserSettings.MatchThresholdPercent via IScrapingSettingsProvider (D9);
    // this constant is only the fallback when no provider is injected.
    private const double DefaultAutoAcceptThreshold = ScrapingSettingsProvider.DefaultThresholdPercent;

    /// <summary>
    /// Creates a GameScrapingService with injected API clients and optional cache service.
    /// </summary>
    /// <param name="bangumiApi">Bangumi API client</param>
    /// <param name="vndbApi">VNDB API client</param>
    /// <param name="ymgalApi">ymgal API client</param>
    /// <param name="cngalApi">cngal API client</param>
    /// <param name="cacheService">Optional cache service for persistent caching</param>
    /// <param name="settingsProvider">Optional provider for user scraping settings (threshold, enabled sources, priority)</param>
    /// <param name="logger">Optional logger</param>
    public GameScrapingService(
        BangumiApi bangumiApi,
        VndbApi vndbApi,
        YmgalApi ymgalApi,
        CngalApi cngalApi,
        IScrapingCacheService? cacheService = null,
        IScrapingSettingsProvider? settingsProvider = null,
        ILogger<GameScrapingService>? logger = null)
    {
        _bangumiApi = bangumiApi ?? throw new ArgumentNullException(nameof(bangumiApi));
        _vndbApi = vndbApi ?? throw new ArgumentNullException(nameof(vndbApi));
        _ymgalApi = ymgalApi ?? throw new ArgumentNullException(nameof(ymgalApi));
        _cngalApi = cngalApi ?? throw new ArgumentNullException(nameof(cngalApi));
        _cacheService = cacheService;
        _settingsProvider = settingsProvider;
        _logger = logger;
    }

    /// <summary>
    /// Effective auto-accept threshold, read from user settings when available.
    /// </summary>
    private double AutoAcceptThreshold => _settingsProvider?.MatchThresholdPercent ?? DefaultAutoAcceptThreshold;

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

        // Resolve the user's source toggles / priority for this run (D9, D10).
        if (_settingsProvider != null)
        {
            await _settingsProvider.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }

        var enabledSources = ResolveEnabledSources();

        // Search every enabled source in parallel (but respect rate limits)
        var searchTasks = enabledSources
            .Select(source => new KeyValuePair<ScraperSource, Task<SourceScrapingResult>>(
                source, SearchFromSourceAsync(cleanName, source, cancellationToken)))
            .ToList();

        await Task.WhenAll(searchTasks.Select(t => t.Value)).ConfigureAwait(false);

        foreach (var (source, task) in searchTasks)
        {
            result.SourceResults[source] = await task.ConfigureAwait(false);
        }

        // Collect errors from every source that reported one, so a failure is
        // distinguishable from a legitimate empty result (D12).
        foreach (var sourceResult in result.SourceResults)
        {
            if (!sourceResult.Value.Success && !string.IsNullOrEmpty(sourceResult.Value.ErrorMessage))
            {
                result.Errors.Add($"{sourceResult.Key}: {sourceResult.Value.ErrorMessage}");
            }
        }

        // Find best match across all sources (priority: Bangumi > VNDB > ymgal > cngal)
        result.BestMatch = FindBestMatch(gameName, result.SourceResults, enabledSources);

        // Cache the result in both caches.
        // D7 fix: only successful, complete results are cached. Caching an empty or
        // half-failed result is what turned a single network hiccup into a 7-day
        // lockout for that game name.
        if (result.HasResults && result.Errors.Count == 0)
        {
            AddToMemoryCache(cleanName, result);
            if (_cacheService != null)
            {
                _cacheService.CacheResult(cleanName, result);
            }
        }
        else
        {
            _logger?.LogInformation(
                "Not caching scraping result for '{GameName}': hasResults={HasResults}, errors={ErrorCount}",
                cleanName, result.HasResults, result.Errors.Count);
        }

        return result;
    }

    /// <summary>
    /// Returns the sources to query for this run: the user's enabled sources in the
    /// configured priority order. Falls back to all sources in default order.
    /// </summary>
    private List<ScraperSource> ResolveEnabledSources()
    {
        if (_settingsProvider == null)
        {
            return new List<ScraperSource>
            {
                ScraperSource.Bangumi,
                ScraperSource.Vndb,
                ScraperSource.Ymgal,
                ScraperSource.Cngal
            };
        }

        var enabled = _settingsProvider.SourcePriority
            .Where(_settingsProvider.IsSourceEnabled)
            .ToList();

        // Never end up with an empty source list: fall back to the configured priority.
        return enabled.Count > 0 ? enabled : _settingsProvider.SourcePriority.ToList();
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
                    // ymgal builds its own request interval into YmgalApi, and enforces it on the
                    // token request too, so no extra rate limiter is applied here.
                    return await GetYmgalDetailsAsync(sourceId, cancellationToken).ConfigureAwait(false);

                case ScraperSource.Cngal:
                    return await GetCngalDetailsAsync(sourceId, cancellationToken).ConfigureAwait(false);
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

        // Expose the per-source outcome (query, success flag, error text, timings)
        // so the UI can explain why a scrape failed (D12).
        result.SourceResults = scrapingResult.SourceResults;

        // Find best match
        var bestMatch = result.AllMatches.OrderByDescending(m => m.MatchScore).FirstOrDefault();

        if (bestMatch != null)
        {
            result.BestMatch = bestMatch;
            result.BestMatchSource = bestMatch.Source;

            // Product spec 3.2: after the match is chosen, fetch the full detail record
            // (description / tags / characters / developer) before writing.
            // GetGameDetailsAsync existed but no code path ever called it, which is why
            // search-only fields such as Developer and Tags stayed empty.
            var details = await GetGameDetailsAsync(bestMatch.SourceId, bestMatch.Source, cancellationToken)
                .ConfigureAwait(false);

            if (details != null)
            {
                MergeDetails(bestMatch, details);
            }

            // Auto-accept when the match reaches the user-configured threshold (D9).
            if (bestMatch.MatchScore >= AutoAcceptThreshold)
            {
                result.AutoAccepted = true;
            }
            else
            {
                _logger?.LogInformation(
                    "Best match for '{SearchName}' scored {Score:F2} which is below the configured threshold {Threshold:F0}",
                    searchName, bestMatch.MatchScore, AutoAcceptThreshold);
            }
        }

        return result;
    }

    /// <summary>
    /// Merges detail-record fields into a search result, never overwriting a value the
    /// search already provided.
    /// </summary>
    private static void MergeDetails(GameMetadata target, GameMetadata details)
    {
        target.TitleCn = string.IsNullOrWhiteSpace(target.TitleCn) ? details.TitleCn : target.TitleCn;
        target.TitleOriginal = string.IsNullOrWhiteSpace(target.TitleOriginal) ? details.TitleOriginal : target.TitleOriginal;
        target.Description = string.IsNullOrWhiteSpace(target.Description) ? details.Description : target.Description;
        target.CoverImageUrl = string.IsNullOrWhiteSpace(target.CoverImageUrl) ? details.CoverImageUrl : target.CoverImageUrl;
        target.BannerImageUrl = string.IsNullOrWhiteSpace(target.BannerImageUrl) ? details.BannerImageUrl : target.BannerImageUrl;
        target.Developer = string.IsNullOrWhiteSpace(target.Developer) ? details.Developer : target.Developer;
        target.ReleaseDate ??= details.ReleaseDate;
        target.Rating ??= details.Rating;

        foreach (var title in details.Titles)
        {
            if (!string.IsNullOrWhiteSpace(title) && !target.Titles.Contains(title))
            {
                target.Titles.Add(title);
            }
        }

        if (details.Tags.Count > 0)
        {
            target.Tags = details.Tags;
        }

        if (details.Characters.Count > 0)
        {
            target.Characters = details.Characters;
        }

        foreach (var pair in details.ExtendedData)
        {
            target.ExtendedData[pair.Key] = pair.Value;
        }
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
                    CoverImageUrl = UpgradeToHttps(item.Images?.Large ?? item.Images?.Common),
                    // v0 search results carry an infobox, so the developer and release date
                    // are available without a second detail request. Tags only exist here too
                    // (the dedicated tags endpoint answers 404).
                    Developer = item.GetDeveloper(),
                    ReleaseDate = item.GetReleaseDate(),
                    Rating = item.Rating?.Score is > 0 ? item.Rating!.Score : null,
                    Tags = item.GetTopTags(),
                    MatchScore = CalculateMatchScore(gameName, allTitles)
                };

                result.Items.Add(metadata);
            }
        }
        else
        {
            ApplyApiFailure(_bangumiApi, result, "Bangumi");
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
                    CoverImageUrl = UpgradeToHttps(vn.Image?.Url),
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
        else
        {
            ApplyApiFailure(_vndbApi, result, "VNDB");
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

        var searchResponse = await _ymgalApi.SearchAsync(gameName, cancellationToken).ConfigureAwait(false);

        foreach (var item in searchResponse?.Items ?? new List<YmgalGameItem>())
        {
            var titles = new List<string>();
            if (!string.IsNullOrWhiteSpace(item.Title)) titles.Add(item.Title);
            if (!string.IsNullOrWhiteSpace(item.TitleCn)) titles.Add(item.TitleCn);

            var metadata = new GameMetadata
            {
                SourceId = item.Id,
                Source = ScraperSource.Ymgal,
                TitleCn = item.TitleCn,
                TitleOriginal = item.Title,
                Titles = titles,
                Description = item.Description,
                CoverImageUrl = UpgradeToHttps(item.CoverUrl)
            };

            result.Items.Add(metadata);
        }

        // `Success == true` means ymgal answered, so an empty result is a legitimate "this title is
        // not in the archive". Anything else is a failure and must be reported: returning zero
        // items without an ErrorMessage is the silent-empty-result defect.
        if (searchResponse is null || !searchResponse.Success)
        {
            ApplyApiFailure(_ymgalApi, result, "ymgal");
            if (string.IsNullOrEmpty(result.ErrorMessage))
            {
                result.ErrorMessage = $"ymgal request failed: {searchResponse?.Message ?? "the source returned no response"}";
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

        var searchResponse = await _cngalApi.SearchAsync(gameName, cancellationToken).ConfigureAwait(false);

        foreach (var item in searchResponse?.Items ?? new List<CngalGameItem>())
        {
            var titles = new List<string>();
            if (!string.IsNullOrWhiteSpace(item.Title)) titles.Add(item.Title);
            if (!string.IsNullOrWhiteSpace(item.TitleCn)) titles.Add(item.TitleCn);

            var metadata = new GameMetadata
            {
                SourceId = item.Id,
                Source = ScraperSource.Cngal,
                TitleCn = item.TitleCn,
                TitleOriginal = item.Title,
                Titles = titles,
                Description = item.Description,
                CoverImageUrl = UpgradeToHttps(item.CoverUrl)
            };

            result.Items.Add(metadata);
        }

        // See the note in SearchYmgalAsync: only a non-answering source is an error.
        if (searchResponse is null || !searchResponse.Success)
        {
            ApplyApiFailure(_cngalApi, result, "cngal");
            if (string.IsNullOrEmpty(result.ErrorMessage))
            {
                result.ErrorMessage = $"cngal request failed: {searchResponse?.Message ?? "the source returned no response"}";
            }
        }

        return result;
    }

    private async Task<GameMetadata?> GetYmgalDetailsAsync(
        string gameId,
        CancellationToken cancellationToken)
    {
        var detail = await _ymgalApi.GetGameAsync(gameId, cancellationToken).ConfigureAwait(false);
        if (detail == null)
        {
            // ymgal reports why it could not answer through LastError; log it so a detail lookup
            // that failed for a reason (no credentials, rate limit, bad id) is not mistaken for
            // "this archive has no extra data".
            if (!string.IsNullOrEmpty(_ymgalApi.LastError))
            {
                _logger?.LogWarning("ymgal detail unavailable for {GameId}: {Error}", gameId, _ymgalApi.LastError);
            }

            return null;
        }

        var metadata = new GameMetadata
        {
            SourceId = detail.Id,
            Source = ScraperSource.Ymgal,
            TitleCn = detail.TitleCn,
            TitleOriginal = detail.Title,
            Description = detail.Description,
            CoverImageUrl = UpgradeToHttps(detail.CoverUrl),
            ReleaseDate = ParseSourceDate(detail.ReleaseDate),
            Developer = detail.Developer
        };

        // The search hit carries the developer name (orgName); the detail object only carries the
        // org id, so keep whichever one is present.
        if (string.IsNullOrWhiteSpace(metadata.Developer) && !string.IsNullOrWhiteSpace(detail.DeveloperId))
        {
            metadata.ExtendedData["DeveloperId"] = detail.DeveloperId;
        }

        foreach (var title in detail.GetAllTitles())
        {
            if (!metadata.Titles.Contains(title))
            {
                metadata.Titles.Add(title);
            }
        }

        foreach (var alias in detail.Aliases.Where(a => !string.IsNullOrWhiteSpace(a)))
        {
            if (!metadata.Titles.Contains(alias))
            {
                metadata.Titles.Add(alias);
            }
        }

        if (!string.IsNullOrWhiteSpace(detail.ChineseReleaseName))
        {
            metadata.ExtendedData["ChineseRelease"] = detail.ChineseReleaseName!;
        }

        if (detail.Staff.Count > 0)
        {
            metadata.ExtendedData["Staff"] = string.Join(" / ", detail.Staff);
        }

        if (detail.Websites.Count > 0)
        {
            metadata.ExtendedData["Websites"] = string.Join(" | ", detail.Websites);
        }

        metadata.Characters = detail.Characters.Select(c => new CharacterInfo
        {
            // ymgal knows both names; NameCn only when ymgal actually recorded a Chinese one.
            Name = string.IsNullOrWhiteSpace(c.NameOriginal) ? c.Name : c.NameOriginal!,
            NameCn = string.IsNullOrWhiteSpace(c.NameOriginal) ? null : c.Name,
            ImageUrl = UpgradeToHttps(c.ImageUrl)
        }).ToList();

        return metadata;
    }

    private async Task<GameMetadata?> GetCngalDetailsAsync(
        string gameId,
        CancellationToken cancellationToken)
    {
        var detail = await _cngalApi.GetGameAsync(gameId, cancellationToken).ConfigureAwait(false);
        if (detail == null)
        {
            if (!string.IsNullOrEmpty(_cngalApi.LastError))
            {
                _logger?.LogWarning("cngal detail unavailable for {GameId}: {Error}", gameId, _cngalApi.LastError);
            }

            return null;
        }

        var metadata = new GameMetadata
        {
            SourceId = detail.Id,
            Source = ScraperSource.Cngal,
            TitleCn = detail.TitleCn,
            TitleOriginal = detail.Title,
            Description = detail.Description,
            CoverImageUrl = UpgradeToHttps(detail.CoverUrl),
            ReleaseDate = ParseSourceDate(detail.ReleaseDate),
            Developer = detail.Developer,
            Tags = detail.Tags.ToList()
        };

        foreach (var title in detail.GetAllTitles())
        {
            if (!metadata.Titles.Contains(title))
            {
                metadata.Titles.Add(title);
            }
        }

        // CnGal stores aliases as one comma separated string ("三色绘恋，Tricolour Lovestory").
        foreach (var alias in SplitAliases(detail.AnotherName))
        {
            if (!metadata.Titles.Contains(alias))
            {
                metadata.Titles.Add(alias);
            }
        }

        if (!string.IsNullOrWhiteSpace(detail.Engine))
        {
            metadata.ExtendedData["Engine"] = detail.Engine!;
        }

        if (detail.Information.Count > 0)
        {
            metadata.ExtendedData["Information"] = string.Join(" | ", detail.Information);
        }

        if (detail.ReleaseNames.Count > 0)
        {
            metadata.ExtendedData["Releases"] = string.Join(" / ", detail.ReleaseNames);
        }

        metadata.Characters = detail.Characters.Select(c => new CharacterInfo
        {
            // CnGal is a Chinese-language database, so its character name is already the Chinese
            // one; there is no separate original name to fill in.
            Name = c.Name,
            NameCn = c.Name,
            ImageUrl = UpgradeToHttps(c.ImageUrl),
            Role = c.VoiceActor
        }).ToList();

        return metadata;
    }

    /// <summary>
    /// Splits a CnGal alias list, which uses both the ASCII and the full-width comma.
    /// </summary>
    private static IEnumerable<string> SplitAliases(string? anotherName)
    {
        if (string.IsNullOrWhiteSpace(anotherName))
        {
            return Array.Empty<string>();
        }

        return anotherName
            .Split(new[] { ',', '，', '、', ';', '；' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(a => a.Trim())
            .Where(a => a.Length > 0);
    }

    /// <summary>
    /// Parses the <c>yyyy-MM-dd</c> (or <c>yyyy-MM</c> / <c>yyyy</c>) dates the sources return,
    /// widening a partial date to the start of its period.
    /// </summary>
    private static DateTime? ParseSourceDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var value = raw.Trim();
        const System.Globalization.DateTimeStyles styles = System.Globalization.DateTimeStyles.None;
        var culture = System.Globalization.CultureInfo.InvariantCulture;

        if (DateTime.TryParseExact(value, "yyyy-MM-dd", culture, styles, out var exact))
            return exact;

        if (DateTime.TryParseExact(value, "yyyy-MM", culture, styles, out var monthOnly))
            return monthOnly;

        if (int.TryParse(value, out var year) && year is > 1900 and < 2200)
            return new DateTime(year, 1, 1);

        return DateTime.TryParse(value, culture, styles, out var parsed) ? parsed : null;
    }

    private async Task<GameMetadata?> GetBangumiDetailsAsync(
        int subjectId,
        CancellationToken cancellationToken)
    {
        var subject = await _bangumiApi.GetSubjectAsync(subjectId, cancellationToken).ConfigureAwait(false);
        if (subject == null) return null;

        // Get additional data (characters). This endpoint is optional: a failure here must not
        // discard the subject data that was already retrieved successfully.
        // Tags are NOT fetched here - /v0/subjects/{id}/tags answers 404; they come from the
        // search response instead (BangumiSearchItem.Tags).
        List<BangumiCharacter> characters;

        try
        {
            characters = await _bangumiApi.GetCharactersAsync(subjectId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Bangumi characters unavailable for subject {SubjectId}", subjectId);
            characters = new List<BangumiCharacter>();
        }

        var metadata = new GameMetadata
        {
            SourceId = subject.Id.ToString(),
            Source = ScraperSource.Bangumi,
            TitleCn = subject.NameCn,
            TitleOriginal = subject.Name,
            Description = subject.Summary,
            CoverImageUrl = UpgradeToHttps(subject.Images?.Large ?? subject.Images?.Common),
            ReleaseDate = subject.GetReleaseDate(),
            Developer = subject.GetDeveloper()
        };

        // Add titles
        if (!string.IsNullOrWhiteSpace(subject.Name))
            metadata.Titles.Add(subject.Name);
        if (!string.IsNullOrWhiteSpace(subject.NameCn))
            metadata.Titles.Add(subject.NameCn);

        // Tags are intentionally left empty here: the Bangumi tags endpoint returns 404, so the
        // search result's tags are kept instead (MergeDetails only overwrites non-empty values).

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
            CoverImageUrl = UpgradeToHttps(vn.Image?.Url),
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
    /// </summary>
    /// <remarks>
    /// D8 fix: matching is delegated to <see cref="GameNameMatcher"/>, which normalizes
    /// case, width, spaces and punctuation before comparing. The previous implementation
    /// compared raw strings, so names differing only by spacing/punctuation scored below the
    /// auto-accept threshold and were discarded.
    /// </remarks>
    private static double CalculateMatchScore(string searchName, List<string> potentialTitles)
    {
        return GameNameMatcher.CalculateMatchScore(searchName, potentialTitles);
    }

    /// <summary>
    /// Records why a source returned nothing, distinguishing a broken/failed response from
    /// a legitimate empty result (D12).
    /// </summary>
    private static void ApplyApiFailure(ApiClient client, SourceScrapingResult result, string sourceName)
    {
        if (!string.IsNullOrEmpty(client.LastError))
        {
            result.ErrorMessage = $"{sourceName} request failed: {client.LastError}";
            result.ExtendedErrorInfo = string.IsNullOrEmpty(client.LastErrorBody)
                ? client.LastError
                : $"{client.LastError}{Environment.NewLine}Response body: {client.LastErrorBody}";
        }
    }

    /// <summary>
    /// Bangumi image URLs are served over http; upgrade them so WinUI can load them
    /// (the same hosts answer on https).
    /// </summary>
    private static string? UpgradeToHttps(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return url;
        }

        return url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            ? "https://" + url.Substring("http://".Length)
            : url;
    }

    /// <summary>
    /// Find the best match across all sources.
    /// Priority: Bangumi > VNDB > ymgal > cngal for equal scores (D10: order is configurable).
    /// </summary>
    private static SourceScrapingResult? FindBestMatch(
        string gameName,
        Dictionary<ScraperSource, SourceScrapingResult> sourceResults,
        IReadOnlyList<ScraperSource> priorityOrder)
    {
        GameMetadata? bestMatch = null;
        ScraperSource bestSource = ScraperSource.Bangumi;
        double bestScore = 0;

        foreach (var source in priorityOrder)
        {
            if (!sourceResults.TryGetValue(source, out var result) || result.Items.Count == 0)
                continue;

            var sourceBest = result.Items.OrderByDescending(i => i.MatchScore).FirstOrDefault();
            if (sourceBest == null)
                continue;

            // Higher score wins; equal scores keep the earlier (higher priority) source.
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