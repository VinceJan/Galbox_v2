using Galbox.App.Converters;
using Galbox.App.Services;
using Galbox.App.ViewModels;
using Galbox.Core.Api;
using Galbox.Data.Entities;
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
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        base.OnLaunched(args);

        try
        {
            // Ensure database is created and migrated
            using var scope = Services.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<GalboxDbContext>();
            await dbContext.Database.EnsureCreatedAsync().ConfigureAwait(false);

            // Add missing columns for EngineType / VndbId if not exists (SQLite migration)
            try
            {
                // Columns that were added to GameInfo after the first release. EnsureCreatedAsync
                // does not alter an existing table, so each one is added explicitly.
                var requiredColumns = new (string Name, string Definition)[]
                {
                    ("EngineType", "ALTER TABLE Games ADD COLUMN EngineType INTEGER NOT NULL DEFAULT 0"),
                    ("VndbId", "ALTER TABLE Games ADD COLUMN VndbId TEXT NULL")
                };

                var connection = dbContext.Database.GetDbConnection();
                await connection.OpenAsync().ConfigureAwait(false);

                foreach (var (columnName, alterStatement) in requiredColumns)
                {
                    using var command = connection.CreateCommand();
                    command.CommandText =
                        $"SELECT name FROM pragma_table_info('Games') WHERE name='{columnName}'";
                    var result = await command.ExecuteScalarAsync().ConfigureAwait(false);

                    if (result == null || result == DBNull.Value)
                    {
                        command.CommandText = alterStatement;
                        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }
                }

                await connection.CloseAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Migration failed, log but continue
                System.Diagnostics.Debug.WriteLine($"Database migration warning: {ex.Message}");
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
