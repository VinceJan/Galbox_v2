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
    public static IServiceProvider Services { get; private set; }

    /// <summary>
    /// The main window instance.
    /// </summary>
    private MainWindow? _mainWindow;

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
        services.AddHttpClient<BangumiHttpClient>()
            .ConfigureHttpClient(client =>
            {
                client.BaseAddress = new Uri("https://api.bgm.tv/");
                client.DefaultRequestHeaders.Add("User-Agent", "Galbox/1.0");
            });

        services.AddHttpClient<VndbHttpClient>()
            .ConfigureHttpClient(client =>
            {
                client.BaseAddress = new Uri("https://api.vndb.org/kana/");
                client.DefaultRequestHeaders.Add("User-Agent", "Galbox/1.0");
            });

        services.AddHttpClient<YmgalHttpClient>()
            .ConfigureHttpClient(client =>
            {
                client.DefaultRequestHeaders.Add("User-Agent", "Galbox/1.0");
            });

        services.AddHttpClient<CngalHttpClient>()
            .ConfigureHttpClient(client =>
            {
                client.DefaultRequestHeaders.Add("User-Agent", "Galbox/1.0");
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
        services.AddSingleton<IBangumiAuthService, BangumiAuthService>();
        services.AddSingleton<IScrapingCacheService, ScrapingCacheService>();

        // Error Checking Service
        services.AddSingleton<IErrorCheckingService, ErrorCheckingService>();

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
            // Ensure database is created
            using var scope = Services.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<GalboxDbContext>();
            await dbContext.Database.EnsureCreatedAsync().ConfigureAwait(false);

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
            _mainWindow = new MainWindow();
            _mainWindow.InitializeNavigation();
            _mainWindow.Activate();

            // Set the window handle for process monitor hotkey registration
            var processMonitor = Services.GetService<IProcessMonitorService>();
            if (processMonitor != null && _mainWindow.WindowHandle != IntPtr.Zero)
            {
                processMonitor.SetHotkeyWindowHandle(_mainWindow.WindowHandle);
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