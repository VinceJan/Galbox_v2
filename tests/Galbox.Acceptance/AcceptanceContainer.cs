using Galbox.App.Services;
using Galbox.Core.Api;
using Galbox.Data.Entities;
using Galbox.Services.Saves;
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
///   1. The database file lives under <c>%LocalAppData%\Galbox\acceptance\acceptance.db</c>
///      instead of the real <c>%LocalAppData%\Galbox\galbox.db</c>, so a run can never
///      touch the user's real library. The database is deleted and recreated by A0.
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
    /// <summary>
    /// Folder (under %LocalAppData%\Galbox) that holds the isolated test database.
    /// </summary>
    public const string AcceptanceFolderName = "acceptance";

    /// <summary>
    /// File name of the isolated acceptance database.
    /// </summary>
    public const string AcceptanceDatabaseName = "acceptance.db";

    /// <summary>
    /// Environment variable that redirects the isolated database to another folder.
    /// </summary>
    /// <remarks>
    /// The default path is shared by every worktree of this repository on the machine, and two
    /// harnesses running at the same time corrupt each other: the first A0 failed with
    /// <c>IOException: the file is being used by another process</c>, and a run seeded for a
    /// screenshot picked up another worktree's game row. Setting
    /// <c>GALBOX_ACCEPTANCE_DIR</c> to an absolute path gives a run its own database. Unset, the
    /// behaviour is exactly what it always was.
    /// </remarks>
    public const string DatabaseDirectoryVariable = "GALBOX_ACCEPTANCE_DIR";

    /// <summary>Absolute path of the isolated acceptance database.</summary>
    public static string DatabasePath { get; private set; } = string.Empty;

    /// <summary>
    /// Builds the service provider. <paramref name="logSink"/> receives every log record
    /// produced by the services so service-internal warnings/errors can be surfaced in the
    /// acceptance report instead of being swallowed.
    /// </summary>
    public static ServiceProvider Build(CollectingLoggerProvider logSink)
    {
        var overrideDirectory = Environment.GetEnvironmentVariable(DatabaseDirectoryVariable);

        var dbDirectory = string.IsNullOrWhiteSpace(overrideDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Galbox",
                AcceptanceFolderName)
            : Path.GetFullPath(overrideDirectory);

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

        // --- API clients (verbatim) ---------------------------------------------------
        services.AddTransient<BangumiApi>();
        services.AddTransient<VndbApi>();
        services.AddTransient<YmgalApi>();
        services.AddTransient<CngalApi>();

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

        // --- Save-node scan (verbatim from App.xaml.cs) --------------------------------
        // Mirrors the registration in src/Galbox.App/App.xaml.cs line for line. A10 asserts that
        // this resolves; if the shipping app ever stops registering the service, this mirror has to
        // be kept in step, and A10's first assertion is what makes that visible.
        services.AddScoped<ISaveNodeScanService>(sp => new SaveNodeScanService(
            sp.GetRequiredService<GalboxDbContext>()));

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
