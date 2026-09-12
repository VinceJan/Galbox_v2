using Galbox.App.Converters;
using Galbox.App.Services;
using Galbox.App.ViewModels;
using Galbox.Core;
using Galbox.Core.Api;
using Galbox.Core.Community;
using Galbox.Data.Entities;
using Galbox.Data.Migrations;
using Galbox.Services.Saves;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using System.IO;

namespace Galbox.App;

/// <summary>
/// Application entry point with Dependency Injection configuration.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// The service provider for dependency injection.
    /// </summary>
    public static IServiceProvider Services { get; private set; } = null!;

    /// <summary>
    /// The main window instance.
    /// </summary>
    public MainWindow? MainWindow { get; private set; }

    /// <summary>
    /// Creates the App instance and initializes DI.
    /// </summary>
    public App()
    {
        InitializeComponent();

        // Configure DI container
        var services = new ServiceCollection();
        ConfigureServices(services);

        try
        {
            // ValidateScopes: resolving a scoped service from the root provider becomes a hard
            // error instead of silently handing out one shared instance forever.
            // ValidateOnBuild: a registration that cannot be constructed fails here and now
            // instead of failing later, in front of the user, inside a page constructor.
            Services = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = true
            });

            StartupDiagnostics.Log("DI container built (ValidateScopes=true, ValidateOnBuild=true)");
        }
        catch (Exception ex)
        {
            // Without a container the application cannot run at all. Fail loudly.
            StartupDiagnostics.ReportStartupFailure(ex, windowCreated: false);
            throw;
        }
    }

    /// <summary>
    /// Configures the DI container with all services and ViewModels.
    /// </summary>
    private static void ConfigureServices(IServiceCollection services)
    {
        // Logging
        services.AddLogging(builder =>
        {
            builder.AddDebug();
            builder.SetMinimumLevel(LogLevel.Information);
        });

        // ---------------------------------------------------------------- Database (SQLite)
        // IDbContextFactory, not a root-resolved DbContext.
        //
        // Every page and every ViewModel is resolved from the root container
        // (App.Services.GetRequiredService<...>()). With the previous
        // AddDbContext<GalboxDbContext>(...) registration (Scoped by default) plus a root
        // provider built without ValidateScopes, the whole application shared ONE context that
        // was never disposed: two overlapping operations on it threw
        // "A second operation was started on this context instance before a previous operation
        // completed", which the audit observed as random red error banners. Singleton services
        // that captured the scoped context (ISaveManagementService, IErrorCheckingService) made
        // it permanent.
        //
        // The factory is the fix: it is a singleton that must not be disposed (EF Core owns the
        // pool), and it hands out a short-lived context per unit of work.
        var dbPath = Path.Combine(GetAppDataPath(), "galbox.db");
        services.AddDbContextFactory<GalboxDbContext>(options =>
        {
            options.UseSqlite($"Data Source={dbPath}");
        });

        // A scope-local context is still registered for the code paths that deliberately run
        // inside `using var scope = Services.CreateScope()` (the startup migration,
        // BangumiAuthService, AutoScrapingService, ScrapingSettingsProvider, the background
        // play-time writer in LibraryViewModel). It is created by the factory, so it is never
        // shared with another scope - and resolving it from the ROOT now throws, as intended.
        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<GalboxDbContext>>().CreateDbContext());

        // HTTP Client Wrappers for APIs
        // Note: Named HttpClient "BangumiAuth" must be registered BEFORE IBangumiAuthService factory
        services.AddHttpClient("BangumiAuth")
            .ConfigureHttpClient(client =>
            {
                client.BaseAddress = new Uri("https://api.bgm.tv/");
                client.DefaultRequestHeaders.Add("User-Agent", "Galbox/1.0");
                client.Timeout = TimeSpan.FromSeconds(30);
            });

        services.AddHttpClient<BangumiHttpClient>()
            .ConfigureHttpClient(client =>
            {
                client.BaseAddress = new Uri("https://api.bgm.tv/");
                client.DefaultRequestHeaders.Add("User-Agent", "Galbox/1.0");
                client.Timeout = TimeSpan.FromSeconds(30);
            });

        services.AddHttpClient<VndbHttpClient>()
            .ConfigureHttpClient(client =>
            {
                client.BaseAddress = new Uri("https://api.vndb.org/kana/");
                client.DefaultRequestHeaders.Add("User-Agent", "Galbox/1.0");
                client.Timeout = TimeSpan.FromSeconds(30);
            });

        // ymgal / cngal metadata sources. The configuration lives in
        // MetadataHttpClientDefaults so the shipping app and the headless acceptance replica
        // (tests/Galbox.Acceptance/AcceptanceContainer.cs) cannot drift apart.
        services.AddHttpClient<YmgalHttpClient>()
            .ConfigureHttpClient(MetadataHttpClientDefaults.ConfigureYmgal);

        services.AddHttpClient<CngalHttpClient>()
            .ConfigureHttpClient(MetadataHttpClientDefaults.ConfigureCngal);

        // Image traffic (covers, backgrounds, character portraits). Separate from the metadata
        // clients so a slow image CDN cannot occupy the API pipelines.
        services.AddHttpClient(GameImageService.HttpClientName)
            .ConfigureHttpClient(client =>
            {
                client.DefaultRequestHeaders.Add("User-Agent", "Galbox/1.0");
                client.Timeout = TimeSpan.FromSeconds(60);
            });

        // ------------------------------------------------- moyu patch discovery (BYOK, /v2/moyu)
        // The ONLY moyu endpoint this application is allowed to reach is the official NextMoe
        // "moyu face" documented at https://developer.nextmoe.dev/specs/moyu-openapi.yaml.
        //
        // The patch site's own data endpoints live under /api/v1/* and its robots.txt says
        // `Disallow: /api`. They answer 200 anonymously - the research report proved that - but
        // "it works" is not "it is allowed", and this product is distributed. MoyuComplianceGuard
        // builds every request URI and refuses anything outside this surface, so the promise is
        // enforced by the code rather than by this comment. Acceptance check A63 asserts it.
        //
        // The face needs the user's own `nmk_` key. Galbox ships none: a key baked into a release
        // would leak immediately and spend somebody else's quota. See MoyuOptions/MoyuDpapiKeyStore.
        //
        // User-Agent: the existing four clients send a bare "Galbox/1.0". That is not enough for a
        // free community site to identify who is calling it, so the moyu client sends the full
        // identifier (version + project URL).
        services.AddHttpClient<MoyuHttpClient>()
            .ConfigureHttpClient(client =>
            {
                client.BaseAddress = MoyuOptions.DefaultBaseAddress;
                client.DefaultRequestHeaders.Add("User-Agent", MoyuComplianceGuard.UserAgent);
                client.Timeout = TimeSpan.FromSeconds(30);
            });

        // The key store is a singleton: DPAPI plus one small file, read once at startup.
        services.AddSingleton<IMoyuKeyStore>(_ => new MoyuDpapiKeyStore());

        // The rate limiter MUST be a singleton - a per-call instance would pace nothing. It is the
        // client-side self-restraint the research report asks for (report section 6.6).
        services.AddSingleton<IMoyuRateLimiter>(sp =>
            new MoyuRateLimiter(sp.GetRequiredService<MoyuOptions>()));

        // Options are built once from the key store, so the application and the acceptance replica
        // cannot disagree about where the key comes from.
        services.AddSingleton(sp =>
            MoyuOptions.FromKeyStore(sp.GetRequiredService<IMoyuKeyStore>()));

        services.AddSingleton<MoyuDownloadWatcher>(sp =>
            new MoyuDownloadWatcher(sp.GetRequiredService<MoyuOptions>()));

        services.AddSingleton<MoyuBrowserLauncher>();

        // ---------------------------------------------------------- local patch installer engine
        // IPatchEngine is what actually unpacks and lays a downloaded patch over a game directory,
        // with a preview, a ledger and a rollback. It was not registered anywhere before, which meant
        // patch discovery had nothing to hand a downloaded package to.
        //
        // The registration itself lives in the "Local patch installer" section further down, next to
        // ILocalPatchService (the patch centre's door onto the engine), so the engine and its door are
        // declared together. Two work lines each added a call to AddGalboxPatches() at a different
        // place; that produced five duplicated singleton registrations, and because
        // PatchStatusEvaluator builds its own PatchLedger the engine and the evaluator would have
        // ended up holding different ledgers. It is called exactly once, below.

        // API Clients
        services.AddTransient<BangumiApi>();
        services.AddTransient<VndbApi>();
        services.AddTransient<YmgalApi>();
        services.AddTransient<CngalApi>();
        services.AddTransient<MoyuApi>();

        // Services
        services.AddSingleton<INavigationService, NavigationService>();

        // Scraping settings (threshold / enabled sources / priority / auto-scrape-on-add).
        // Registered as a singleton so every scraping consumer reads the same user settings.
        services.AddSingleton<IScrapingSettingsProvider, ScrapingSettingsProvider>();

        // Registered as a singleton so the declared Bangumi/VNDB rate limiting and the
        // 30-minute in-memory cache actually apply across a batch (D16).
        services.AddSingleton<IGameScrapingService, GameScrapingService>();
        services.AddSingleton<ISaveManagementService, SaveManagementService>();
        services.AddSingleton<IProcessMonitorService, ProcessMonitorService>();

        // Enhanced Scraping Services
        services.AddSingleton<IAutoScrapingService, AutoScrapingService>();

        // BangumiAuthService - uses "BangumiAuth" HttpClient registered above
        services.AddSingleton<IBangumiAuthService>(sp =>
        {
            var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
            var httpClient = httpClientFactory.CreateClient("BangumiAuth");
            return new BangumiAuthService(sp, sp.GetRequiredService<ILogger<BangumiAuthService>>(), httpClient);
        });

        services.AddSingleton<IScrapingCacheService, ScrapingCacheService>();

        // Health repairs: the two diagnosis items that can genuinely be repaired without installing
        // anything or asking for administrator rights (rename a Chinese game folder / write an HKCU
        // compatibility layer). Everything else stays manual-by-design; see IGameHealthFixService.
        services.AddSingleton<IGameHealthFixService, GameHealthFixService>();

        // Error Checking Service
        services.AddSingleton<IErrorCheckingService, ErrorCheckingService>();

        // Save-node scan: the "存档节点" feature (parser x data). Scoped, not singleton - it holds a
        // DbContext, and a page must get a fresh one per scan. The SaveManagerViewModel therefore
        // resolves it from a scope it creates itself (IServiceScopeFactory) instead of injecting it:
        // injecting a scoped service into a root-resolved ViewModel is exactly the captive-dependency
        // defect the ValidateScopes switch below is there to catch.
        services.AddScoped<ISaveNodeScanService>(sp => new SaveNodeScanService(
            sp.GetRequiredService<GalboxDbContext>()));

        // Game Utility Service
        services.AddSingleton<IGameUtilityService, GameUtilityService>();

        // ------------------------------------------------ Reserved features (interfaces only)
        // 流程图追踪 and 社区成就系统 are P2: 第一版只预留接口，前端隐藏 (_product/Galbox-产品知识总纲.md
        // lines 173-174). Both are specified as remote-service-first - the flowchart as a community
        // maintained API standard, the achievements as an objective server judgement - so the first
        // version ships the data model, the interfaces and this switch, and nothing else.
        //
        // This is the ONLY registration the reserved layer adds, and that is deliberate.
        // IFlowchartProvider, IAchievementProvider and IAchievementEvidenceSource are NOT registered:
        // there is no service to answer them, and an instance in the container would be a stub that
        // returns nothing - indistinguishable, to every caller, from a service that found nothing.
        // Resolving them returns null, so there is no code path that could serve a flowchart or an
        // achievement that was never really fetched. The acceptance run asserts exactly that (A92),
        // that no navigation key, page, menu item or button exposes the two features (A91), and that
        // the reserved model still has the documented shape (A90).
        //
        // The registration is by strong type, so the interfaces, the model and this switch cannot
        // drift apart unnoticed: the application does not build unless they are all present under
        // these names.
        services.AddSingleton<IReservedFeatureCatalog, ReservedFeatureCatalog>();

        // Image download: turns the CoverImageUrl/BackgroundImageUrl/character ImageUrl written by
        // scraping into the local files the UI binds (CoverImagePath, BackgroundImagePath,
        // ImagePath). Without it every cover in the product stays blank.
        services.AddSingleton<IGameImageService, GameImageService>();

        // Game deletion: removes a game and everything that belongs to it from the library without
        // ever touching the game files on disk.
        services.AddSingleton<IGameDeletionService, GameDeletionService>();

        // ------------------------------------------------------- Local patch installer (no network)
        // Galbox.Core.Patches was complete and verified while nothing in the application ever resolved
        // it - no registration, no page, no command. This is that door.
        //
        // Everything behind it is local: it consumes an archive the user already downloaded plus a
        // game directory, and produces serialisable results. No HTTP client, no API key, no download.
        // Registrations are singletons, which is what the engine wants (it is stateless apart from the
        // files it writes, and the ledger scans are cheap to share).
        services.AddGalboxPatches();
        services.AddSingleton<ILocalPatchService, LocalPatchService>();

        // ViewModels
        services.AddTransient<MainViewModel>();
        services.AddTransient<LibraryViewModel>();
        services.AddTransient<GameDetailViewModel>();
        services.AddTransient<SaveManagerViewModel>();
        services.AddTransient<PatchCenterViewModel>();
        services.AddTransient<ScrapingProgressViewModel>();
        services.AddTransient<ErrorReportViewModel>();
        services.AddSingleton<SettingsViewModel>();
    }

    /// <summary>
    /// Gets the application data path for storing database and settings.
    /// </summary>
    /// <remarks>
    /// <c>GALBOX_DATA_DIR</c> overrides the folder when it is set. It exists so the UI can be driven
    /// and captured by a verification run against a THROWAWAY library: without it, the only way to
    /// screenshot a populated Save Manager page is to write into the user's real
    /// <c>%LocalAppData%\Galbox\galbox.db</c>, which is never acceptable. Unset (the normal case)
    /// the behaviour is exactly what it was.
    /// </remarks>
    private static string GetAppDataPath()
    {
        var overridePath = Environment.GetEnvironmentVariable("GALBOX_DATA_DIR");

        var appDataPath = string.IsNullOrWhiteSpace(overridePath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Galbox")
            : Path.GetFullPath(overridePath);

        if (!Directory.Exists(appDataPath))
        {
            Directory.CreateDirectory(appDataPath);
        }

        return appDataPath;
    }

    /// <summary>
    /// Handles application launch.
    /// </summary>
    /// <remarks>
    /// THREAD AFFINITY: everything on this path runs on the UI thread and MUST keep running on it.
    /// WinUI window and UI objects may only be created on the thread that owns the dispatcher, so
    /// every await below uses <c>ConfigureAwait(true)</c> (the default, written out explicitly so
    /// it cannot be "optimised" back to false).
    ///
    /// This is not theoretical: with <c>ConfigureAwait(false)</c> any await that genuinely yields
    /// (e.g. ScrapingCacheService.InitializeAsync once the cache folder contains files) resumed
    /// on a thread-pool thread, <c>new MainWindow()</c> then threw
    /// <c>COMException 0x8001010E (RPC_E_WRONG_THREAD)</c>, and the catch below swallowed it -
    /// leaving a live process with no window and no error message. Verified A/B by the parent
    /// session and re-verified for this change.
    /// </remarks>
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        base.OnLaunched(args);

        try
        {
            StartupDiagnostics.Log("OnLaunched: startup sequence begins");

            await PrepareDatabaseAsync().ConfigureAwait(true);
            await InitializeDeferredServicesAsync().ConfigureAwait(true);

            // Create and initialize the main window - on the UI thread.
            MainWindow = new MainWindow();
            MainWindow.InitializeNavigation();
            MainWindow.Activate();

            // Inert unless GALBOX_TEST_OFFSCREEN_WINDOW=1 (see MainWindow.OffscreenWindowVariable):
            // it re-asserts the off-screen position and the task-switcher hiding the constructor
            // already applied, so activation cannot leave a window on the desktop of whoever is
            // building this.
            MainWindow.ReapplyOffscreenPlacementForTests();

            StartupDiagnostics.Log($"Main window created and activated: handle=0x{MainWindow.WindowHandle.ToInt64():X}");

            // Set the window handle for process monitor hotkey registration
            var processMonitor = Services.GetService<IProcessMonitorService>();
            if (processMonitor != null && MainWindow.WindowHandle != IntPtr.Zero)
            {
                processMonitor.SetHotkeyWindowHandle(MainWindow.WindowHandle);
            }

            // Turn the process monitor on: boss key, real running-state detection and
            // screenshot-on-exit are dead without this call, and every library game has to be
            // registered for the monitor to watch anything.
            var monitorStartup = await ProcessMonitorStartup
                .RegisterLibraryAndStartAsync(Services)
                .ConfigureAwait(true);

            StartupDiagnostics.Log(
                $"Process monitor: started={monitorStartup.Started}, "
                + $"registered={monitorStartup.GamesRegistered}/{monitorStartup.GamesInLibrary}"
                + (monitorStartup.ErrorMessage is null ? string.Empty : $", error={monitorStartup.ErrorMessage}"));

            StartupDiagnostics.Log(StartupDiagnostics.StartupCompletedMarker);
        }
        catch (Exception ex)
        {
            // Never silent again: log to %LocalAppData%\Galbox\logs and, when the window never
            // made it into existence, raise a visible message box.
            StartupDiagnostics.ReportStartupFailure(ex, windowCreated: MainWindow is not null);
        }
    }

    /// <summary>
    /// Creates the SQLite schema when needed and adds the columns that were introduced after the
    /// first release. Runs inside an explicit scope; the context comes from the factory.
    /// </summary>
    private static async Task PrepareDatabaseAsync()
    {
        using var scope = Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GalboxDbContext>();

        StartupDiagnostics.Log($"Preparing database: {dbContext.Database.GetDbConnection().DataSource}");

        // Schema evolution is owned by the EF Core migration mechanism, not by hand-written
        // ALTER TABLE statements. GalboxDatabaseInitializer recognises databases created by the
        // old EnsureCreated() call (tables present, no __EFMigrationsHistory), stamps the baseline
        // migration as already applied and then applies the pending migrations, so existing user
        // data is never re-created or dropped. It also tolerates columns that were added by hand
        // in the meantime.
        var dbLogger = Services.GetService<ILogger<App>>();
        var dbResult = await GalboxDatabaseInitializer.InitializeAsync(
            dbContext,
            new DatabaseInitializationOptions { Logger = dbLogger }).ConfigureAwait(true);

        StartupDiagnostics.Log(
            $"Database initialization: mode={dbResult.Mode}, applied={dbResult.AppliedNow.Count}");

        if (!dbResult.SchemaReport.IsConsistent)
        {
            // Not fatal, but the user must be able to find out about it: writing through a stale
            // schema is the one situation that can lose data.
            StartupDiagnostics.Log(
                "Database schema problems:" + Environment.NewLine +
                string.Join(Environment.NewLine, dbResult.SchemaReport.Problems()));
        }
    }

    /// <summary>
    /// Initializes the services that need an async warm-up before the window is shown.
    /// </summary>
    private static async Task InitializeDeferredServicesAsync()
    {
        var bangumiAuthService = Services.GetService<IBangumiAuthService>() as BangumiAuthService;
        if (bangumiAuthService != null)
        {
            await bangumiAuthService.InitializeAsync().ConfigureAwait(true);
            StartupDiagnostics.Log("BangumiAuthService initialized");
        }

        var scrapingCacheService = Services.GetService<IScrapingCacheService>() as ScrapingCacheService;
        if (scrapingCacheService != null)
        {
            await scrapingCacheService.InitializeAsync().ConfigureAwait(true);
            StartupDiagnostics.Log(
                $"ScrapingCacheService initialized (entries: {scrapingCacheService.GetCacheStats().TotalEntries})");
        }
    }
}
