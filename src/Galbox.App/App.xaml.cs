using Galbox.App.Converters;
using Galbox.App.Services;
using Galbox.App.ViewModels;
using Galbox.Core.Api;
using Galbox.Data.Entities;
using Galbox.Data.Migrations;
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

        services.AddHttpClient<YmgalHttpClient>()
            .ConfigureHttpClient(client =>
            {
                // BaseAddress to be configured when API implementation is complete
                client.DefaultRequestHeaders.Add("User-Agent", "Galbox/1.0");
                client.Timeout = TimeSpan.FromSeconds(30);
            });

        services.AddHttpClient<CngalHttpClient>()
            .ConfigureHttpClient(client =>
            {
                // BaseAddress to be configured when API implementation is complete
                client.DefaultRequestHeaders.Add("User-Agent", "Galbox/1.0");
                client.Timeout = TimeSpan.FromSeconds(30);
            });

        // API Clients
        services.AddTransient<BangumiApi>();
        services.AddTransient<VndbApi>();
        services.AddTransient<YmgalApi>();
        services.AddTransient<CngalApi>();

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

        // Error Checking Service
        services.AddSingleton<IErrorCheckingService, ErrorCheckingService>();

        // Game Utility Service
        services.AddSingleton<IGameUtilityService, GameUtilityService>();

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
    private static string GetAppDataPath()
    {
        var appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Galbox");

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
