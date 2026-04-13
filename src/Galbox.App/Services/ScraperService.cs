namespace Galbox.App.Services;

/// <summary>
/// Service for scraping game metadata from external sources.
/// </summary>
public class ScraperService
{
    // TODO: Implement actual API calls to Bangumi, VNDB, ymgal, cngal

    /// <summary>
    /// Scrape game info from all available sources.
    /// </summary>
    public async Task<ScrapeResult> ScrapeGameAsync(string gameName)
    {
        var result = new ScrapeResult();

        // Priority: Bangumi (Chinese) > VNDB > ymgal > cngal
        // TODO: Implement actual scraping

        return result;
    }

    /// <summary>
    /// Calculate similarity between two strings (for matching).
    /// </summary>
    public double CalculateSimilarity(string source, string target)
    {
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(target))
            return 0;

        source = source.ToLowerInvariant();
        target = target.ToLowerInvariant();

        // Simple Levenshtein distance-based similarity
        // TODO: Implement proper similarity algorithm

        if (source == target)
            return 100;

        if (source.Contains(target) || target.Contains(source))
            return 90;

        return 50; // Placeholder
    }
}

/// <summary>
/// Result from scraping operation.
/// </summary>
public class ScrapeResult
{
    public string? NameCn { get; set; }
    public string? NameOriginal { get; set; }
    public string? Description { get; set; }
    public string? CoverUrl { get; set; }
    public string? BannerUrl { get; set; }
    public List<string> Tags { get; set; } = new();
    public string Source { get; set; } = string.Empty;
    public double MatchScore { get; set; }
}