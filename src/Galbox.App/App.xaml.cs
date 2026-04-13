using Microsoft.UI.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Galbox.App.Services;
using Galbox.App.Views;
using Galbox.Data.Database;

namespace Galbox.App;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// Gets the current application instance.
    /// </summary>
    public static App Current => (App)Application.Current;

    /// <summary>
    /// Gets the service provider for dependency injection.
    /// </summary>
    public IServiceProvider Services { get; }

    /// <summary>
    /// Gets the main window of the app.
    /// </summary>
    public MainWindow? MainWindow { get; private set; }

    /// <summary>
    /// Initializes the singleton application object.
    /// </summary>
    public App()
    {
        Services = ConfigureServices();
        InitializeComponent();
    }

    /// <summary>
    /// Configures the services for dependency injection.
    /// </summary>
    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // Services
        services.AddSingleton<GameScannerService>();
        services.AddSingleton<ScraperService>();
        services.AddSingleton<SaveParserService>();
        services.AddSingleton<PatchService>();
        services.AddSingleton<ProcessMonitorService>();

        // Database
        services.AddSingleton<GalboxDbContext>();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new MainWindow();
        MainWindow.Activate();

        // Initialize database
        using (var scope = Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<GalboxDbContext>();
            dbContext.InitializeDatabase();
        }
    }
}