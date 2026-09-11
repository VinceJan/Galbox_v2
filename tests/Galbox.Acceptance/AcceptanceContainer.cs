using Galbox.App.Services;
using Galbox.Core;
using Galbox.Core.Api;
using Galbox.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Galbox.Acceptance;

/// <summary>
/// Headless replica of the Galbox application's dependency-injection container.
///
/// This mirrors <c>ConfigureServices</c> in <c>src/Galbox.App/App.xaml.cs</c> line for
/// line, with exactly two deliberate differences:
///
///   1. The database file lives under
///      <c>%LocalAppData%\Galbox\acceptance\run-{pid}\acceptance.db</c>
///      instead of the real <c>%LocalAppData%\Galbox\galbox.db</c>, so a run can never
///      touch the user's real library. The database is deleted and recreated by A0.
///
///      The <c>run-{pid}</c> segment matters: several work lines run this harness in
///      parallel, and the database path used to be a single shared file. SQLite locks it
///      for the whole run, so concurrent runs failed with an unhandled <c>IOException</c>
///      in A0 - a failure that has nothing to do with the code under test. Giving every
///      process its own directory removes that false negative entirely.
///   2. <see cref="Galbox.App.Services.INavigationService"/>/<c>NavigationService</c> and
///      the ViewModels are NOT registered: they are the only registrations that depend on
///      <c>Microsoft.UI.Xaml.Controls</c>, which cannot be loaded outside a WinUI host.
///      They are not needed to drive any business service, so nothing under test is lost.
///
/// Everything else - loggers, typed/named HttpClients with their base addresses and
/// user agents, API clients and business services - is identical to the shipping app.
/// </summary>
public static class AcceptanceContainer
{
    /// <summary>Folder (under %LocalAppData%\Galbox) that holds the isolated test database.</summary>
    public const string AcceptanceFolderName = "acceptance";

    /// <summary>File name of the isolated acceptance database.</summary>
    public const string AcceptanceDatabaseName = "acceptance.db";

    /// <summary>Absolute path of the isolated acceptance database.</summary>
    public static string DatabasePath { get; private set; } = string.Empty;

    /// <summary>
    /// Builds the service provider. <paramref name="logSink"/> receives every log record
    /// produced by the services so service-internal warnings/errors can be surfaced in the
    /// acceptance report instead of being swallowed.
    /// </summary>
    public static ServiceProvider Build(CollectingLoggerProvider logSink)
    {
        // One database directory per process. Several work lines run this harness at the
        // same time; a single shared acceptance.db made SQLite lock it for the whole run,
        // so a concurrent run died in A0 with an IOException that had nothing to do with
        // the code under test. The pid suffix is the same isolation the image cache uses.
        var dbDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Galbox",
            AcceptanceFolderName,
            $"run-{Environment.ProcessId}");
        Directory.CreateDirectory(dbDirectory);
        DatabasePath = Path.Combine(dbDirectory, AcceptanceDatabaseName);

        var services = new ServiceCollection();

        // --- Logging (mirrors App.xaml.cs) ---------------------------------------------
        services.AddLogging(builder =>
        {
            builder.AddDebug();
            builder.SetMinimumLevel(LogLevel.Information);
            // Extra sink so the acceptance report can show real service diagnostics.
            builder.AddProvider(logSink);
        });

        // --- Database (only the path differs from the shipping app) --------------------
        // Mirrors App.xaml.cs exactly: an IDbContextFactory (the singleton a long-lived service
        // may hold) plus a scope-local context created from it. Registering a root-resolved
        // scoped context instead is what used to make the whole application share one DbContext.
        services.AddDbContextFactory<GalboxDbContext>(options =>
        {
            options.UseSqlite($"Data Source={DatabasePath}");
        });

        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<GalboxDbContext>>().CreateDbContext());

        // --- HTTP clients (verbatim from App.xaml.cs) ---------------------------------
        // A transparent recording handler is appended to each pipeline so the report can show
        // the requests Galbox actually sends. It observes only; it does not alter behaviour.
        services.AddSingleton<HttpTrafficRecorder>();

        services.AddHttpClient("BangumiAuth")
            .AddHttpMessageHandler(sp => new RecordingHttpMessageHandler(sp.GetRequiredService<HttpTrafficRecorder>(), "BangumiAuth"))
            .ConfigureHttpClient(client =>
            {
                client.BaseAddress = new Uri("https://api.bgm.tv/");
                client.DefaultRequestHeaders.Add("User-Agent", "Galbox/1.0");
                client.Timeout = TimeSpan.FromSeconds(30);
            });

        services.AddHttpClient<BangumiHttpClient>()
            .AddHttpMessageHandler(sp => new RecordingHttpMessageHandler(sp.GetRequiredService<HttpTrafficRecorder>(), "BangumiHttpClient"))
            .ConfigureHttpClient(client =>
            {
                client.BaseAddress = new Uri("https://api.bgm.tv/");
                client.DefaultRequestHeaders.Add("User-Agent", "Galbox/1.0");
                client.Timeout = TimeSpan.FromSeconds(30);
            });

        services.AddHttpClient<VndbHttpClient>()
            .AddHttpMessageHandler(sp => new RecordingHttpMessageHandler(sp.GetRequiredService<HttpTrafficRecorder>(), "VndbHttpClient"))
            .ConfigureHttpClient(client =>
            {
                client.BaseAddress = new Uri("https://api.vndb.org/kana/");
                client.DefaultRequestHeaders.Add("User-Agent", "Galbox/1.0");
                client.Timeout = TimeSpan.FromSeconds(30);
            });

        services.AddHttpClient<YmgalHttpClient>()
            .AddHttpMessageHandler(sp => new RecordingHttpMessageHandler(sp.GetRequiredService<HttpTrafficRecorder>(), "YmgalHttpClient"))
            .ConfigureHttpClient(client =>
            {
                // BaseAddress intentionally not configured - same as the shipping app.
                client.DefaultRequestHeaders.Add("User-Agent", "Galbox/1.0");
                client.Timeout = TimeSpan.FromSeconds(30);
            });

        services.AddHttpClient<CngalHttpClient>()
            .AddHttpMessageHandler(sp => new RecordingHttpMessageHandler(sp.GetRequiredService<HttpTrafficRecorder>(), "CngalHttpClient"))
            .ConfigureHttpClient(client =>
            {
                // BaseAddress intentionally not configured - same as the shipping app.
                client.DefaultRequestHeaders.Add("User-Agent", "Galbox/1.0");
                client.Timeout = TimeSpan.FromSeconds(30);
            });

        // --- moyu patch discovery (verbatim from App.xaml.cs) -------------------------
        // This replica must register the same things the shipping app does: a missing registration
        // here would let the application fail to build its container while every check still
        // passed. A63 relies on the recorder below to assert the requests actually sent.
        services.AddHttpClient<MoyuHttpClient>()
            .AddHttpMessageHandler(sp => new RecordingHttpMessageHandler(sp.GetRequiredService<HttpTrafficRecorder>(), "MoyuHttpClient"))
            .ConfigureHttpClient(client =>
            {
                client.BaseAddress = MoyuOptions.DefaultBaseAddress;
                client.DefaultRequestHeaders.Add("User-Agent", MoyuComplianceGuard.UserAgent);
                client.Timeout = TimeSpan.FromSeconds(30);
            });

        services.AddSingleton<IMoyuKeyStore>(_ => new MoyuDpapiKeyStore());
        services.AddSingleton<IMoyuRateLimiter>(sp => new MoyuRateLimiter(sp.GetRequiredService<MoyuOptions>()));
        services.AddSingleton(sp => MoyuOptions.FromKeyStore(sp.GetRequiredService<IMoyuKeyStore>()));
        services.AddSingleton(sp => new MoyuDownloadWatcher(sp.GetRequiredService<MoyuOptions>()));
        services.AddSingleton<MoyuBrowserLauncher>();

        // The local patch engine, which is where an adopted download goes. Mirrors App.xaml.cs.
        services.AddGalboxPatches();

        // --- API clients (verbatim) ---------------------------------------------------
        services.AddTransient<BangumiApi>();
        services.AddTransient<VndbApi>();
        services.AddTransient<YmgalApi>();
        services.AddTransient<CngalApi>();
        services.AddTransient<MoyuApi>();

        // --- Business services (verbatim minus the UI-bound navigation service) -------
        services.AddTransient<IGameScrapingService, GameScrapingService>();
        services.AddSingleton<ISaveManagementService, SaveManagementService>();
        services.AddSingleton<IProcessMonitorService, ProcessMonitorService>();
        services.AddSingleton<IAutoScrapingService, AutoScrapingService>();

        services.AddSingleton<IBangumiAuthService>(sp =>
        {
            var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
            var httpClient = httpClientFactory.CreateClient("BangumiAuth");
            return new BangumiAuthService(
                sp,
                sp.GetRequiredService<ILogger<BangumiAuthService>>(),
                httpClient);
        });

        services.AddSingleton<IScrapingCacheService, ScrapingCacheService>();
        services.AddSingleton<IErrorCheckingService, ErrorCheckingService>();
        services.AddSingleton<IGameUtilityService, GameUtilityService>();

        // Same validation switches as the shipping app. Without them the harness could happily
        // resolve a graph that the application itself refuses to start with - which is exactly
        // how five captive-dependency defects stayed hidden. A failure here is reported by the
        // preflight before any check runs.
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });
    }

    /// <summary>
    /// Disables the persistent scraping cache on the resolved singleton.
    ///
    /// Deliberate acceptance-time deviation: with the cache disabled,
    /// <c>GetCachedResult</c> always misses and <c>CacheResult</c> is a no-op, so the A5
    /// scraping check is guaranteed to be a live network query and the user's real
    /// <c>%LocalAppData%\Galbox\ScrapingCache</c> is neither read nor written.
    /// </summary>
    public static void DisableScrapingCache(ServiceProvider services)
    {
        var cache = services.GetRequiredService<IScrapingCacheService>();
        cache.IsEnabled = false;
    }

    /// <summary>
    /// Initializes the isolated database: always deletes the previous file and recreates
    /// the schema, giving every run a deterministic starting state.
    /// </summary>
    public static async Task<(bool Deleted, bool Created)> ResetDatabaseAsync(
        ServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GalboxDbContext>();

        var deleted = await db.Database.EnsureDeletedAsync(cancellationToken).ConfigureAwait(false);
        var created = await db.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        return (deleted, created);
    }
}
