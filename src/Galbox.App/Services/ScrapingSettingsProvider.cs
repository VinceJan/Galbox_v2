using Galbox.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Galbox.App.Services;

/// <summary>
/// Read-only access to the scraping-related user settings.
/// </summary>
/// <remarks>
/// D9/D10 fix: <c>MatchThresholdPercent</c>, the four source toggles, <c>SourcePriorityJson</c> and
/// <c>AutoScrapeOnAdd</c> were only ever written by the settings page and never read by the
/// scraping services, which used hard-coded constants instead. Scraping now resolves these
/// values through this provider.
/// </remarks>
public interface IScrapingSettingsProvider
{
    /// <summary>
    /// Similarity percentage at or above which a candidate is accepted automatically (0-100).
    /// </summary>
    int MatchThresholdPercent { get; }

    /// <summary>
    /// Whether newly added games should be scraped automatically.
    /// </summary>
    bool AutoScrapeOnAdd { get; }

    /// <summary>
    /// Ordered source priority, highest priority first.
    /// </summary>
    IReadOnlyList<ScraperSource> SourcePriority { get; }

    /// <summary>
    /// Gets whether the given source is enabled by the user.
    /// </summary>
    bool IsSourceEnabled(ScraperSource source);

    /// <summary>
    /// Reloads the settings from the database.
    /// </summary>
    Task RefreshAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Default <see cref="IScrapingSettingsProvider"/> backed by the SQLite user settings row.
/// </summary>
public class ScrapingSettingsProvider : IScrapingSettingsProvider
{
    /// <summary>
    /// Default auto-accept threshold, matching <see cref="UserSettings.MatchThresholdPercent"/>.
    /// </summary>
    public const int DefaultThresholdPercent = 90;

    private static readonly ScraperSource[] DefaultPriority =
    {
        ScraperSource.Bangumi,
        ScraperSource.Vndb,
        ScraperSource.Ymgal,
        ScraperSource.Cngal
    };

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ScrapingSettingsProvider> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private int _thresholdPercent = DefaultThresholdPercent;
    private bool _autoScrapeOnAdd = true;
    private IReadOnlyList<ScraperSource> _sourcePriority = DefaultPriority;
    private HashSet<ScraperSource> _enabledSources = new(DefaultPriority);

    /// <summary>
    /// Creates the provider. Values fall back to the documented defaults until
    /// <see cref="RefreshAsync"/> has run at least once.
    /// </summary>
    public ScrapingSettingsProvider(
        IServiceProvider serviceProvider,
        ILogger<ScrapingSettingsProvider> logger)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public int MatchThresholdPercent => Volatile.Read(ref _thresholdPercent);

    /// <inheritdoc />
    public bool AutoScrapeOnAdd => Volatile.Read(ref _autoScrapeOnAdd);

    /// <inheritdoc />
    public IReadOnlyList<ScraperSource> SourcePriority => Volatile.Read(ref _sourcePriority);

    /// <inheritdoc />
    public bool IsSourceEnabled(ScraperSource source)
    {
        var enabled = Volatile.Read(ref _enabledSources);
        return enabled.Contains(source);
    }

    /// <inheritdoc />
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<GalboxDbContext>();

            // Project only the columns this provider needs. Reading the whole UserSettings
            // entity makes the query depend on every column the entity declares, and the
            // shipped database is missing several of them (BangumiAuthMethod,
            // BangumiRefreshToken, BangumiTokenExpiresAt, BangumiUsername,
            // GameDirectoriesJson) - a full-entity read therefore throws
            // "SQLite Error 1: no such column" and the user's threshold/source settings
            // would silently stay at their defaults.
            var settings = await dbContext.UserSettings
                .AsNoTracking()
                .Select(u => new
                {
                    u.MatchThresholdPercent,
                    u.AutoScrapeOnAdd,
                    u.EnableBangumi,
                    u.EnableVndb,
                    u.EnableYmgal,
                    u.EnableCngal,
                    u.SourcePriorityJson
                })
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            if (settings == null)
            {
                // No settings row yet - keep documented defaults.
                return;
            }

            Volatile.Write(ref _thresholdPercent, ClampThreshold(settings.MatchThresholdPercent));
            Volatile.Write(ref _autoScrapeOnAdd, settings.AutoScrapeOnAdd);
            Volatile.Write(ref _enabledSources, new HashSet<ScraperSource>(BuildEnabledSources(
                settings.EnableBangumi,
                settings.EnableVndb,
                settings.EnableYmgal,
                settings.EnableCngal)));
            Volatile.Write(ref _sourcePriority, ParsePriority(settings.SourcePriorityJson));
        }
        catch (Exception ex)
        {
            // Settings must never break scraping - fall back to whatever is cached.
            _logger.LogWarning(ex, "Failed to refresh scraping settings, keeping previous values");
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private static int ClampThreshold(int value)
    {
        if (value <= 0)
        {
            return DefaultThresholdPercent;
        }

        return value > 100 ? 100 : value;
    }

    private static IEnumerable<ScraperSource> BuildEnabledSources(bool enableBangumi, bool enableVndb, bool enableYmgal, bool enableCngal)
    {
        if (enableBangumi) yield return ScraperSource.Bangumi;
        if (enableVndb) yield return ScraperSource.Vndb;
        if (enableYmgal) yield return ScraperSource.Ymgal;
        if (enableCngal) yield return ScraperSource.Cngal;
    }

    /// <summary>
    /// Parses <see cref="UserSettings.SourcePriorityJson"/> ("[\"Bangumi\",\"VNDB\",...]").
    /// Unknown names are ignored and any missing source is appended in the default order.
    /// </summary>
    private IReadOnlyList<ScraperSource> ParsePriority(string? json)
    {
        var ordered = new List<ScraperSource>();

        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                var names = System.Text.Json.JsonSerializer.Deserialize<List<string>>(json);
                if (names != null)
                {
                    foreach (var name in names)
                    {
                        var source = MapSourceName(name);
                        if (source.HasValue && !ordered.Contains(source.Value))
                        {
                            ordered.Add(source.Value);
                        }
                    }
                }
            }
            catch (System.Text.Json.JsonException ex)
            {
                _logger.LogWarning(ex, "Invalid SourcePriorityJson, using default priority order");
            }
        }

        foreach (var source in DefaultPriority)
        {
            if (!ordered.Contains(source))
            {
                ordered.Add(source);
            }
        }

        return ordered;
    }

    private static ScraperSource? MapSourceName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return name.Trim().ToLowerInvariant() switch
        {
            "bangumi" => ScraperSource.Bangumi,
            "vndb" => ScraperSource.Vndb,
            "ymgal" => ScraperSource.Ymgal,
            "cngal" => ScraperSource.Cngal,
            _ => null
        };
    }
}
