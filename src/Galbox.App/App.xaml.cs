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
        Services = services.BuildServiceProvider();
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

        // Database Context (SQLite)
        var dbPath = Path.Combine(GetAppDataPath(), "galbox.db");
        services.AddDbContext<GalboxDbContext>(options =>
        {
            options.UseSqlite($"Data Source={dbPath}");
        });

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
        services.AddTransient<IGameScrapingService, GameScrapingService>();
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
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        base.OnLaunched(args);

        try
        {
            // Create the database if needed and bring an existing one up to date through EF Core migrations.
            // GalboxDatabaseInitializer handles the legacy databases that were created by EnsureCreated()
            // (they have all the tables but no __EFMigrationsHistory) by stamping the baseline migration as
            // already applied, so existing user data is never re-created or dropped.
            using var scope = Services.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<GalboxDbContext>();
            var dbLogger = Services.GetService<ILogger<App>>();
            var dbResult = await GalboxDatabaseInitializer.InitializeAsync(
                dbContext,
                new DatabaseInitializationOptions { Logger = dbLogger }).ConfigureAwait(false);

            dbLogger?.LogInformation(
                "Database initialization finished in mode {Mode} ({AppliedCount} migration(s) applied).",
                dbResult.Mode,
                dbResult.AppliedNow.Count);

            if (!dbResult.SchemaReport.IsConsistent)
            {
                // Not fatal, but the user must be able to find out about it: writing through a stale schema is
                // the one situation that can lose data.
                System.Diagnostics.Debug.WriteLine(
                    "Database schema problems:" + Environment.NewLine +
                    string.Join(Environment.NewLine, dbResult.SchemaReport.Problems()));
            }

            // Initialize services that require async initialization
            var bangumiAuthService = Services.GetService<IBangumiAuthService>() as BangumiAuthService;
            if (bangumiAuthService != null)
            {
                await bangumiAuthService.InitializeAsync().ConfigureAwait(false);
            }

            var scrapingCacheService = Services.GetService<IScrapingCacheService>() as ScrapingCacheService;
            if (scrapingCacheService != null)
            {
                await scrapingCacheService.InitializeAsync().ConfigureAwait(false);
            }

            // Create and initialize the main window
            MainWindow = new MainWindow();
            MainWindow.InitializeNavigation();
            MainWindow.Activate();

            // Set the window handle for process monitor hotkey registration
            var processMonitor = Services.GetService<IProcessMonitorService>();
            if (processMonitor != null && MainWindow.WindowHandle != IntPtr.Zero)
            {
                processMonitor.SetHotkeyWindowHandle(MainWindow.WindowHandle);
            }
        }
        catch (Exception ex)
        {
            // Log the exception and show error to user
            var logger = Services.GetService<ILogger<App>>();
            logger?.LogError(ex, "Error during application launch");
        }
    }
}