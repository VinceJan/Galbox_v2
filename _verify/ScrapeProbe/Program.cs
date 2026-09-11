using System.Text;
using Galbox.App.Services;
using Galbox.Core.Api;
using Galbox.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Galbox.Verify.ScrapeProbe;

/// <summary>
/// Minimal runnable verification for the Galbox metadata scraping link.
/// </summary>
/// <remarks>
/// Modes:
/// <list type="bullet">
///   <item><description><c>contract</c> - real HTTP through the fixed API clients: proves Bangumi and
///     VNDB return non-empty results and prints the match scores.</description></item>
///   <item><description><c>e2e</c> - real DI container + real services + real cache directory + real SQLite
///     database: enqueue games, run the batch, then print the cache directory and the updated rows.</description></item>
///   <item><description><c>all</c> (default) - both.</description></item>
///   <item><description><c>settings</c> - proves the auto-accept threshold and source toggles come from
///     UserSettings (D9/D10). Writes to the settings row, so use a database copy.</description></item>
/// </list>
/// Options: <c>--db &lt;path&gt;</c>, <c>--clear-cache</c>, <c>--add-dreamin</c>, <c>--ids 3,4</c>.
/// </remarks>
internal static class Program
{
    private const string DreaminHerFolder = @"D:\GAME\Dreamin'_Her";

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var mode = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? "all";
        var dbPath = GetOption(args, "--db") ?? DefaultDbPath();
        var clearCache = args.Contains("--clear-cache");
        var addDreamin = args.Contains("--add-dreamin");
        var explicitIds = ParseIds(GetOption(args, "--ids"));

        Console.WriteLine("================================================================================");
        Console.WriteLine($"Galbox 刮削验证程序 (ScrapeProbe)   mode={mode}");
        Console.WriteLine($"数据库: {dbPath}");
        Console.WriteLine($"缓存目录: {CacheDirectory()}");
        Console.WriteLine($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine("================================================================================");
        Console.WriteLine();

        using var provider = BuildServiceProvider(dbPath);

        if (mode is "contract" or "all")
        {
            await RunContractAsync(provider);
        }

        if (mode is "e2e" or "all")
        {
            await RunEndToEndAsync(provider, dbPath, clearCache, addDreamin, explicitIds);
        }

        if (mode is "settings")
        {
            await RunSettingsCheckAsync(provider, dbPath, args.Contains("--allow-live"));
        }

        return 0;
    }

    #region Settings check (D9 / D10)

    /// <summary>
    /// Proves that the auto-accept threshold is read from UserSettings.MatchThresholdPercent
    /// instead of a hard-coded constant, and that the source toggles are honored.
    /// </summary>
    /// <remarks>
    /// This writes to the settings row, so it refuses to run against the live database
    /// unless <c>--allow-live</c> is passed.
    /// </remarks>
    private static async Task RunSettingsCheckAsync(IServiceProvider provider, string dbPath, bool allowLive)
    {
        Console.WriteLine("===== [S] 用户设置生效验证（D9 阈值 / D10 数据源开关） =====");

        if (PathsEqual(dbPath, DefaultDbPath()) && !allowLive)
        {
            Console.WriteLine("拒绝在真实用户库上运行（会修改设置行）。请用 --db <副本路径> 或显式加 --allow-live。");
            return;
        }

        var settings = provider.GetRequiredService<IScrapingSettingsProvider>();

        await PrintSettingsAsync(provider, settings, "初始");

        // Threshold 95 > the 90 achieved by "Dreamin' Her" -> must NOT be auto-accepted.
        await SetThresholdAsync(provider, 95);
        await PrintSettingsAsync(provider, settings, "把 MatchThresholdPercent 改为 95 后");

        var scraping = provider.GetRequiredService<IGameScrapingService>();
        scraping.ClearCache();

        var highBar = await scraping.AutoScrapeAsync(new GameInfo { NameOriginal = "Dreamin' Her" });
        Console.WriteLine(
            $"  阈值 95 时 AutoScrapeAsync(\"Dreamin' Her\") -> " +
            $"bestScore={highBar.BestMatch?.MatchScore:F2} AutoAccepted={highBar.AutoAccepted} " +
            $"source={highBar.BestMatch?.Source}  (期望 AutoAccepted=False)");

        // Back to the documented default -> the same 90 match must be accepted.
        await SetThresholdAsync(provider, 90);
        await PrintSettingsAsync(provider, settings, "把 MatchThresholdPercent 改回 90 后");

        scraping.ClearCache();
        var lowBar = await scraping.AutoScrapeAsync(new GameInfo { NameOriginal = "Dreamin' Her" });
        Console.WriteLine(
            $"  阈值 90 时 AutoScrapeAsync(\"Dreamin' Her\") -> " +
            $"bestScore={lowBar.BestMatch?.MatchScore:F2} AutoAccepted={lowBar.AutoAccepted} " +
            $"source={lowBar.BestMatch?.Source}  (期望 AutoAccepted=True)");

        Console.WriteLine();
    }

    private static async Task PrintSettingsAsync(
        IServiceProvider provider,
        IScrapingSettingsProvider settings,
        string label)
    {
        await settings.RefreshAsync();

        Console.WriteLine($"  {label}:");
        Console.WriteLine($"    MatchThresholdPercent = {settings.MatchThresholdPercent}");
        Console.WriteLine($"    AutoScrapeOnAdd       = {settings.AutoScrapeOnAdd}");
        Console.WriteLine($"    启用状态: Bangumi={settings.IsSourceEnabled(ScraperSource.Bangumi)} " +
                          $"VNDB={settings.IsSourceEnabled(ScraperSource.Vndb)} " +
                          $"ymgal={settings.IsSourceEnabled(ScraperSource.Ymgal)} " +
                          $"cngal={settings.IsSourceEnabled(ScraperSource.Cngal)}");
        Console.WriteLine($"    优先级: {string.Join(" > ", settings.SourcePriority)}");

        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GalboxDbContext>();
        var row = await db.UserSettings.AsNoTracking().Select(u => u.Id).FirstOrDefaultAsync();
        Console.WriteLine($"    (库中存在设置行: Id={row})");
    }

    private static async Task SetThresholdAsync(IServiceProvider provider, int threshold)
    {
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GalboxDbContext>();

        // Raw SQL keeps this independent of the entity's other (missing) columns.
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE UserSettings SET MatchThresholdPercent = {0} WHERE Id = 1",
            threshold);
    }

    #endregion

    #region Contract level

    /// <summary>
    /// Runs the real search path against the live APIs and prints what came back.
    /// </summary>
    private static async Task RunContractAsync(IServiceProvider provider)
    {
        var scraping = provider.GetRequiredService<IGameScrapingService>();

        Console.WriteLine("===== [1] 接口层实测（真实 HTTP，走修复后的代码路径） =====");
        Console.WriteLine();

        var names = new[] { "SabbatOfTheWitch", "Dreamin' Her", "Dreamin'_Her" };

        foreach (var name in names)
        {
            Console.WriteLine($"--- IGameScrapingService.SearchGameAsync(\"{name}\") ---");

            var result = await scraping.SearchGameAsync(name);

            foreach (var pair in result.SourceResults.OrderBy(p => p.Key.ToString()))
            {
                var sourceResult = pair.Value;
                var error = string.IsNullOrEmpty(sourceResult.ErrorMessage) ? "(无)" : sourceResult.ErrorMessage;
                Console.WriteLine(
                    $"  [{pair.Key,-7}] success={sourceResult.Success,-5} items={sourceResult.Items.Count,-2} " +
                    $"{sourceResult.ElapsedMilliseconds,5}ms query=\"{sourceResult.SearchQuery}\" error={error}");

                if (!string.IsNullOrEmpty(sourceResult.ExtendedErrorInfo))
                {
                    Console.WriteLine($"            扩展错误: {Trim(sourceResult.ExtendedErrorInfo!, 300)}");
                }

                foreach (var item in sourceResult.Items.OrderByDescending(i => i.MatchScore).Take(3))
                {
                    Console.WriteLine(
                        $"      -> {item.SourceId} | {item.TitleOriginal} | 中文名={item.TitleCn ?? "(无)"} | " +
                        $"匹配分={item.MatchScore:F2}");
                    Console.WriteLine($"         该条目的全部标题: {string.Join(" ‖ ", item.Titles)}");
                    Console.WriteLine(
                        $"         开发商={item.Developer ?? "(无)"} 发售日={item.ReleaseDate?.ToString("yyyy-MM-dd") ?? "(无)"} " +
                        $"评分={item.Rating?.ToString("F1") ?? "(无)"} 封面={(string.IsNullOrEmpty(item.CoverImageUrl) ? "(无)" : item.CoverImageUrl)}");
                }
            }

            var best = result.BestMatch?.Items.OrderByDescending(i => i.MatchScore).FirstOrDefault();
            Console.WriteLine(
                $"  >>> bestMatch = {(best == null ? "(null)" : $"{best.Source} {best.SourceId} \"{best.TitleOriginal}\" 匹配分={best.MatchScore:F2}")}" +
                $" ; HasResults={result.HasResults} ; Errors={result.Errors.Count}");

            if (result.Errors.Count > 0)
            {
                Console.WriteLine($"  >>> errors: {string.Join(" | ", result.Errors)}");
            }

            Console.WriteLine();
            await Task.Delay(1200); // be polite to the public APIs
        }
    }

    #endregion

    #region End to end

    /// <summary>
    /// Runs the real auto-scraping pipeline and reports the observable side effects.
    /// </summary>
    private static async Task RunEndToEndAsync(
        IServiceProvider provider,
        string dbPath,
        bool clearCache,
        bool addDreamin,
        List<int> explicitIds)
    {
        Console.WriteLine("===== [2] 端到端（真实服务 + 真实缓存目录 + 真实 SQLite） =====");
        Console.WriteLine();

        var cacheService = (ScrapingCacheService)provider.GetRequiredService<IScrapingCacheService>();
        await cacheService.InitializeAsync();

        if (clearCache)
        {
            var removed = DeleteCacheFiles();
            Console.WriteLine($"已清空缓存目录，删除 {removed} 个文件。清空后目录内容: {DescribeCacheDirectory()}");
        }
        else
        {
            Console.WriteLine($"缓存目录当前内容: {DescribeCacheDirectory()}");
        }

        Console.WriteLine();

        var ids = new List<int>(explicitIds);

        if (addDreamin)
        {
            var addedId = await EnsureGameAsync(provider, dbPath, DreaminHerFolder);
            if (addedId > 0)
            {
                ids.Add(addedId);
            }
        }

        if (ids.Count == 0)
        {
            // Default target: the record that was never scraped in the user's library.
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<GalboxDbContext>();
            ids.AddRange(await db.Games
                .Where(g => !g.IsScraped)
                .OrderBy(g => g.Id)
                .Select(g => g.Id)
                .ToListAsync());
        }

        ids = ids.Distinct().OrderBy(id => id).ToList();

        if (ids.Count == 0)
        {
            Console.WriteLine("没有需要刮削的游戏，端到端测试跳过。");
            return;
        }

        Console.WriteLine($"目标游戏 ID: {string.Join(", ", ids)}");
        Console.WriteLine();

        var autoScraping = provider.GetRequiredService<IAutoScrapingService>();

        autoScraping.GameCompleted += (_, e) =>
        {
            var result = e.Result;
            Console.WriteLine(
                $"  [GameCompleted] id={result.GameId} name={result.GameName} status={result.Status} " +
                $"score={result.MatchScore:F2} source={result.BestMatchSource} autoAccepted={result.AutoAccepted} " +
                $"elapsed={result.ElapsedMilliseconds}ms");

            if (!string.IsNullOrEmpty(result.ErrorMessage))
            {
                Console.WriteLine($"      error: {result.ErrorMessage}");
            }

            foreach (var pair in result.SourceResults.OrderBy(p => p.Key.ToString()))
            {
                Console.WriteLine(
                    $"      [{pair.Key,-7}] success={pair.Value.Success} items={pair.Value.Items.Count} " +
                    $"query=\"{pair.Value.SearchQuery}\" error={pair.Value.ErrorMessage ?? "(无)"}");
            }
        };

        autoScraping.ProgressChanged += (_, e) =>
        {
            Console.WriteLine(
                $"  [Progress] {e.Progress.CompletedGames}/{e.Progress.TotalGames} " +
                $"success={e.Progress.SuccessCount} failed={e.Progress.FailedCount} skipped={e.Progress.SkippedCount}");
        };

        autoScraping.EnqueueGames(ids);
        await autoScraping.StartScrapingAsync();

        Console.WriteLine();
        Console.WriteLine("===== [3] 客观判据 A：%LocalAppData%\\Galbox\\ScrapingCache\\ 内容 =====");
        Console.WriteLine($"目录: {CacheDirectory()}");
        foreach (var line in ListCacheFiles())
        {
            Console.WriteLine("  " + line);
        }

        Console.WriteLine();
        Console.WriteLine("===== [4] 客观判据 B：数据库中被更新/新增的记录 =====");
        await PrintGamesAsync(provider, ids);

        Console.WriteLine();
        Console.WriteLine("===== [5] 本次运行的真实 HTTP 请求数（缓存目录文件数） =====");
        Console.WriteLine($"search_*.json 文件数: {Directory.GetFiles(CacheDirectory(), "search_*.json").Length}");
    }

    /// <summary>
    /// Inserts a game row using the same steps as LibraryViewModel.AddGameAsync.
    /// </summary>
    private static async Task<int> EnsureGameAsync(IServiceProvider provider, string dbPath, string folderPath)
    {
        if (!Directory.Exists(folderPath))
        {
            Console.WriteLine($"跳过新增游戏：目录不存在 {folderPath}");
            return 0;
        }

        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GalboxDbContext>();
        var gameUtility = provider.GetRequiredService<IGameUtilityService>();

        var existing = await db.Games.FirstOrDefaultAsync(g => g.InstallPath == folderPath);
        if (existing != null)
        {
            Console.WriteLine($"游戏已存在: Id={existing.Id} Name={existing.NameOriginal} (IsScraped={existing.IsScraped})");
            return existing.Id;
        }

        var executable = gameUtility.FindExecutableInFolder(folderPath);
        if (string.IsNullOrWhiteSpace(executable))
        {
            Console.WriteLine($"跳过新增游戏：目录中没有找到可执行文件 {folderPath}");
            return 0;
        }

        var folderName = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar));
        var size = gameUtility.CalculateFolderSize(folderPath);

        var game = new GameInfo
        {
            NameOriginal = folderName,
            InstallPath = folderPath,
            MainExecutable = executable,
            AddedTime = DateTime.UtcNow,
            UpdatedTime = DateTime.UtcNow,
            SizeBytes = size,
            IsScraped = false
        };

        db.Games.Add(game);
        await db.SaveChangesAsync();

        Console.WriteLine(
            $"已新增游戏: Id={game.Id} NameOriginal={game.NameOriginal} exe={game.MainExecutable} " +
            $"size={size / 1024 / 1024} MB (库: {dbPath})");

        return game.Id;
    }

    private static async Task PrintGamesAsync(IServiceProvider provider, List<int> ids)
    {
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GalboxDbContext>();

        var games = await db.Games.Where(g => ids.Contains(g.Id)).OrderBy(g => g.Id).ToListAsync();

        foreach (var game in games)
        {
            Console.WriteLine($"  Id={game.Id}");
            Console.WriteLine($"    NameCn         = {Value(game.NameCn)}");
            Console.WriteLine($"    NameOriginal   = {Value(game.NameOriginal)}");
            Console.WriteLine($"    Developer      = {Value(game.Developer)}");
            Console.WriteLine($"    Rating         = {game.Rating?.ToString("F1") ?? "(null)"}");
            Console.WriteLine($"    ReleaseDate    = {game.ReleaseDate?.ToString("yyyy-MM-dd") ?? "(null)"}");
            Console.WriteLine($"    SourceId       = {Value(game.SourceId)}");
            Console.WriteLine($"    SourceType     = {Value(game.SourceType)}");
            Console.WriteLine($"    VndbId         = {Value(game.VndbId)}");
            Console.WriteLine($"    IsScraped      = {game.IsScraped}");
            Console.WriteLine($"    CoverImageUrl  = {Value(game.CoverImageUrl)}");
            Console.WriteLine($"    DescriptionLen = {game.Description?.Length ?? 0}");
            Console.WriteLine($"    TagsJson       = {Trim(Value(game.TagsJson), 160)}");
            Console.WriteLine($"    Characters     = {db.Characters.Count(c => c.GameInfoId == game.Id)}");
            Console.WriteLine($"    UpdatedTime    = {game.UpdatedTime:yyyy-MM-dd HH:mm:ss} (UTC)");
            Console.WriteLine();
        }
    }

    #endregion

    #region Infrastructure

    /// <summary>
    /// Mirrors App.ConfigureServices so the probe runs the same wiring as the application.
    /// </summary>
    private static ServiceProvider BuildServiceProvider(string dbPath)
    {
        var services = new ServiceCollection();

        services.AddLogging(builder =>
        {
            builder.AddProvider(new ProbeLoggerProvider(LogLevel.Warning));
            builder.SetMinimumLevel(LogLevel.Warning);
        });

        services.AddDbContext<GalboxDbContext>(options => options.UseSqlite($"Data Source={dbPath}"));

        services.AddHttpClient<BangumiHttpClient>().ConfigureHttpClient(client =>
        {
            client.BaseAddress = new Uri("https://api.bgm.tv/");
            client.DefaultRequestHeaders.Add("User-Agent", "Galbox/1.0");
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        services.AddHttpClient<VndbHttpClient>().ConfigureHttpClient(client =>
        {
            client.BaseAddress = new Uri("https://api.vndb.org/kana/");
            client.DefaultRequestHeaders.Add("User-Agent", "Galbox/1.0");
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        services.AddHttpClient<YmgalHttpClient>().ConfigureHttpClient(client =>
        {
            client.DefaultRequestHeaders.Add("User-Agent", "Galbox/1.0");
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        services.AddHttpClient<CngalHttpClient>().ConfigureHttpClient(client =>
        {
            client.DefaultRequestHeaders.Add("User-Agent", "Galbox/1.0");
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        services.AddTransient<BangumiApi>();
        services.AddTransient<VndbApi>();
        services.AddTransient<YmgalApi>();
        services.AddTransient<CngalApi>();

        services.AddSingleton<IScrapingCacheService, ScrapingCacheService>();
        services.AddSingleton<IScrapingSettingsProvider, ScrapingSettingsProvider>();
        services.AddSingleton<IGameScrapingService, GameScrapingService>();
        services.AddSingleton<IAutoScrapingService, AutoScrapingService>();
        services.AddSingleton<IGameUtilityService, GameUtilityService>();

        return services.BuildServiceProvider();
    }

    private static string DefaultDbPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Galbox",
            "galbox.db");
    }

    private static string CacheDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Galbox",
            "ScrapingCache");
    }

    private static int DeleteCacheFiles()
    {
        var removed = 0;
        var directory = CacheDirectory();

        if (!Directory.Exists(directory))
        {
            return 0;
        }

        foreach (var file in Directory.GetFiles(directory, "*.json"))
        {
            File.Delete(file);
            removed++;
        }

        return removed;
    }

    private static string DescribeCacheDirectory()
    {
        var directory = CacheDirectory();

        if (!Directory.Exists(directory))
        {
            return "(目录不存在)";
        }

        var files = Directory.GetFiles(directory, "*.json");
        return files.Length == 0 ? "(空)" : $"{files.Length} 个文件";
    }

    private static IEnumerable<string> ListCacheFiles()
    {
        var directory = CacheDirectory();

        if (!Directory.Exists(directory))
        {
            return Array.Empty<string>();
        }

        var files = Directory.GetFiles(directory, "*")
            .OrderBy(f => f)
            .ToArray();

        if (files.Length == 0)
        {
            return new[] { "(空)" };
        }

        return files.Select(f =>
        {
            var info = new FileInfo(f);
            return $"{info.Name}  ({info.Length} bytes, {info.LastWriteTime:HH:mm:ss})";
        });
    }

    private static string? GetOption(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static List<int> ParseIds(string? raw)
    {
        var ids = new List<int>();

        if (string.IsNullOrWhiteSpace(raw))
        {
            return ids;
        }

        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(part, out var id) && id > 0)
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    private static string Value(string? value) => string.IsNullOrWhiteSpace(value) ? "(null)" : value!;

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string Trim(string value, int max)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "(null)";
        }

        return value.Length <= max ? value : value[..max] + $"...[+{value.Length - max} chars]";
    }

    #endregion
}

/// <summary>
/// Logger provider that mirrors warnings/errors to stderr, so nothing the services log is lost.
/// </summary>
internal sealed class ProbeLoggerProvider : ILoggerProvider
{
    private readonly LogLevel _minimumLevel;

    public ProbeLoggerProvider(LogLevel minimumLevel) => _minimumLevel = minimumLevel;

    public ILogger CreateLogger(string categoryName) => new ProbeLogger(categoryName, _minimumLevel);

    public void Dispose()
    {
    }

    private sealed class ProbeLogger : ILogger
    {
        private readonly string _category;
        private readonly LogLevel _minimumLevel;

        public ProbeLogger(string category, LogLevel minimumLevel)
        {
            _category = category;
            _minimumLevel = minimumLevel;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= _minimumLevel;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            Console.Error.WriteLine($"[{logLevel}] {_category}: {formatter(state, exception)}");

            if (exception != null)
            {
                Console.Error.WriteLine("    " + exception.Message);
            }
        }
    }
}
