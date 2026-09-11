using Galbox.App.Services;
using Galbox.Core;
using Galbox.Core.Api;
using Galbox.Core.Patches;
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
        // Two independent ways to keep concurrent runs out of each other's way, and both are
        // needed:
        //
        //   1. GALBOX_ACCEPTANCE_DIR lets a caller point the harness at a private directory. A
        //      caller that wants a fixed, inspectable location (screenshot seeding, debugging a
        //      single check) sets it.
        //   2. When it is NOT set, the default carries the process id. Every worktree holds its
        //      own copy of this harness but they all share one %LocalAppData%\Galbox, and SQLite
        //      locks acceptance.db for the whole run - so a second, concurrent run used to die in
        //      A0 with an IOException that had nothing to do with the code under test. Measured:
        //      two concurrent runs went from "9 PASS / 1 ERROR" to "10 PASS / 0 ERROR" each.
        var overrideDirectory = Environment.GetEnvironmentVariable(DatabaseDirectoryVariable);

        var dbDirectory = string.IsNullOrWhiteSpace(overrideDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Galbox",
                AcceptanceFolderName,
                $"run-{Environment.ProcessId}")
            : Path.GetFullPath(overrideDirectory);
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

        // Same shared configuration as App.xaml.cs - these two blocks used to be copies that both
        // lacked a BaseAddress, which is exactly the drift the shared helper removes.
        services.AddHttpClient<YmgalHttpClient>()
            .AddHttpMessageHandler(sp => new RecordingHttpMessageHandler(sp.GetRequiredService<HttpTrafficRecorder>(), "YmgalHttpClient"))
            .ConfigureHttpClient(MetadataHttpClientDefaults.ConfigureYmgal);

        services.AddHttpClient<CngalHttpClient>()
            .AddHttpMessageHandler(sp => new RecordingHttpMessageHandler(sp.GetRequiredService<HttpTrafficRecorder>(), "CngalHttpClient"))
            .ConfigureHttpClient(MetadataHttpClientDefaults.ConfigureCngal);

        services.AddHttpClient("ImageDownload")
            .AddHttpMessageHandler(sp => new RecordingHttpMessageHandler(sp.GetRequiredService<HttpTrafficRecorder>(), "ImageDownload"))
            .ConfigureHttpClient(client =>
            {
                client.DefaultRequestHeaders.Add("User-Agent", "Galbox/1.0");
                client.Timeout = TimeSpan.FromSeconds(60);
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
