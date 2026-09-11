using Galbox.App.Services;
using Galbox.Core;
using Galbox.Core.Api;
using Galbox.Core.Patches;
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
    /// Root of the acceptance-time patch backup store and extraction sandbox
    /// (<c>%TEMP%\Galbox\acceptance-patchdata</c>). Never the user's real Galbox folder.
    /// </summary>
    public static string PatchDataRoot { get; private set; } = string.Empty;

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

        // Downloaded images are isolated the same way the database is: a test game with id 1 would
        // otherwise overwrite the cover image of the user's real game 1. The process id is part of
        // the folder name because every worktree runs its own copy of this harness against the same
        // %LocalAppData%\Galbox.
        var isolatedImageRoot = Path.Combine(dbDirectory, $"images-{Environment.ProcessId}");
        Directory.CreateDirectory(isolatedImageRoot);

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

        services.AddHttpClient("ImageDownload")
            .AddHttpMessageHandler(sp => new RecordingHttpMessageHandler(sp.GetRequiredService<HttpTrafficRecorder>(), "ImageDownload"))
            .ConfigureHttpClient(client =>
            {
                client.DefaultRequestHeaders.Add("User-Agent", "Galbox/1.0");
                client.Timeout = TimeSpan.FromSeconds(60);
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

        // --- Local patch installer -----------------------------------------------------
        // Same registrations as the shipping app (App.xaml.cs), with ONE deliberate deviation that
        // mirrors the database-path deviation above: the backup store and the extraction sandbox are
        // moved under %TEMP% so an acceptance run can never write into the user's real
        // %LocalAppData%\Galbox\patchbak. The option values cannot affect whether the container can
        // construct the engine, which is what A30 asserts.
        PatchDataRoot = Path.Combine(Path.GetTempPath(), "Galbox", "acceptance-patchdata");
        services.AddGalboxPatches(new PatchInstallerOptions
        {
            BackupRoot = Path.Combine(PatchDataRoot, "patchbak"),
            SandboxRoot = Path.Combine(PatchDataRoot, "patchsink")
        });
        services.AddSingleton<ILocalPatchService, LocalPatchService>();

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

        // --- Features whose "before the fix" behaviour has to stay measurable ---------------
        // These two services (image download / game deletion) are registered by reflection rather
        // than by type name so this harness still COMPILES against a revision of the application
        // that does not contain them yet. That is what makes it possible to run the checks for a
        // new feature against the unmodified code and record them as FAIL, then re-run against the
        // fixed code and record them as PASS - without maintaining two copies of the harness.
        // When the types exist (i.e. in the shipping revision) the registrations are identical to
        // the ones in App.xaml.cs.
        RegisterIfPresent(services, "Galbox.App.Services.IGameDeletionService", "Galbox.App.Services.GameDeletionService");
        RegisterIfPresent(services, "Galbox.App.Services.IGameImageService", "Galbox.App.Services.GameImageService", isolatedImageRoot);

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
    /// Registers <paramref name="implementationTypeName"/> as <paramref name="serviceTypeName"/> when
    /// both types are present in the application assembly.
    /// </summary>
    /// <remarks>
    /// The optional <paramref name="extraConstructorArgument"/> lets the harness construct
    /// <c>GameImageService</c> with an isolated image root, so a run cannot overwrite the images of
    /// the user's real library.
    /// </remarks>
    private static void RegisterIfPresent(
        IServiceCollection services,
        string serviceTypeName,
        string implementationTypeName,
        string? extraConstructorArgument = null)
    {
        var assembly = typeof(Galbox.App.App).Assembly;
        var serviceType = Type.GetType($"{serviceTypeName}, {assembly.GetName().Name}");
        var implementationType = Type.GetType($"{implementationTypeName}, {assembly.GetName().Name}");

        if (serviceType is null || implementationType is null)
        {
            return;
        }

        if (extraConstructorArgument is null)
        {
            services.AddSingleton(serviceType, implementationType);
            return;
        }

        services.AddSingleton(serviceType, sp =>
        {
            var constructor = implementationType
                .GetConstructors()
                .OrderByDescending(c => c.GetParameters().Length)
                .First();

            var arguments = constructor
                .GetParameters()
                .Select(parameter => parameter.ParameterType == typeof(string)
                    ? extraConstructorArgument
                    : sp.GetRequiredService(parameter.ParameterType))
                .ToArray();

            return Activator.CreateInstance(implementationType, arguments)!;
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
