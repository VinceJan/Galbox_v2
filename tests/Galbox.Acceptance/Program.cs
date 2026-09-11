using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Galbox.Acceptance.Checks;
using Galbox.App.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Galbox.Acceptance;

/// <summary>
/// Headless acceptance runner for Galbox.
///
/// It builds the real dependency-injection container with the UI services removed, drives the
/// real business services against a real installed game, and prints a PASS/FAIL report with
/// measured values. Exit code is 0 only when every check passed.
///
/// Usage: Galbox.Acceptance.exe [--game &lt;folder&gt;] [--name &lt;query&gt;] [--verbose] [--timeout &lt;seconds&gt;]
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch
        {
            // Redirected consoles can reject the encoding change; the report is ASCII-safe anyway.
        }

        var options = AcceptanceOptions.Parse(args);
        var timeoutSeconds = ParseTimeoutSeconds(args);

        var logs = new CollectingLoggerProvider();
        var services = AcceptanceContainer.Build(logs);
        AcceptanceContainer.DisableScrapingCache(services);

        var context = new AcceptanceContext
        {
            Services = services,
            Options = options,
            Logs = logs,
            Traffic = services.GetRequiredService<HttpTrafficRecorder>()
        };

        PrintEnvironment(context);

        var checks = new IAcceptanceCheck[]
        {
            new A0DatabaseCheck(),
            new A1ExecutableCheck(),
            new A2FolderSizeCheck(),
            new A3SaveLocationCheck(),
            new A4ErrorCheckCheck(),
            new A5ScrapingSearchCheck(),
            new A6MetadataFieldsCheck(),
            new A7UpstreamContractCheck(),
            new A8NavigationPageCheck(),
            new A9StartupWithCacheCheck(),
            // --- Save nodes (A10) ---------------------------------------------------------
            // The product's differentiating feature: names the story node each save belongs to.
            new A10SaveNodeScanCheck(),

            // Regression checks for the user-visible and data-safety defects of the W13-W21 backlog.
            // Every one of them FAILS against the revision it was written for and PASSES against the
            // fixed revision; the two runs are recorded in _product\design\acceptance-runs.
            new A11QuickSwitchSafetyCheck(),
            new A12RestoreRollbackCheck(),
            new A13InstallRootSavePathCheck(),
            new A14RestoreVerificationCheck(),
            new A15CoverImageDownloadCheck(),
            new A16DeleteGameCheck(),
            new A17MissingFolderCheck(),
            new A18WindowTitleCheck(),
            new A19PageLoadSmokeCheck(),

            // --- Patch centre wiring (A30+; A20-A29 are reserved for other work lines) -----
            new A30PatchCenterWiringCheck(),
            new A31PatchInstallRoundTripCheck(),
            new A32PatchRejectionVisibleCheck(),

            // --- Metadata sources (A40+; A33-A39 are reserved for other work lines) --------
            // The other half of "four sources": ymgal and cngal were honest stubs that returned
            // Success=false with a "pending API research" message, and nothing ever called them
            // with a real contract. These three checks pin the contract down.
            new A40YmgalSearchCheck(),
            new A41CngalSearchCheck(),
            new A42MetadataSourceContractCheck()
        };

        using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        int exitCode;
        try
        {
            exitCode = await CheckRunner.RunAsync(checks, context, timeoutSource.Token).ConfigureAwait(false);
        }
        finally
        {
            services.Dispose();
        }

        return exitCode;
    }

    private static int ParseTimeoutSeconds(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--timeout" && i + 1 < args.Length && int.TryParse(args[i + 1], out var seconds) && seconds > 0)
            {
                return seconds;
            }
        }

        return 300;
    }

    /// <summary>
    /// Prints the environment and proves that the UI-free container can construct every
    /// non-UI service the shipping app registers. A resolution failure here is reported before
    /// any check runs, because it would make every later result meaningless.
    /// </summary>
    private static void PrintEnvironment(AcceptanceContext context)
    {
        var width = 100;
        Console.WriteLine(new string('=', width));
        Console.WriteLine(" GALBOX HEADLESS ACCEPTANCE RUN");
        Console.WriteLine(new string('=', width));
        Console.WriteLine($" UTC started        : {DateTime.UtcNow:u}");
        Console.WriteLine($" OS                 : {RuntimeInformation.OSDescription}");
        Console.WriteLine($" Process arch       : {RuntimeInformation.ProcessArchitecture}");
        Console.WriteLine($" Runtime            : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($" Executable         : {Environment.ProcessPath}");
        Console.WriteLine($" Working directory  : {Environment.CurrentDirectory}");
        Console.WriteLine($" Game folder        : {context.Options.GameFolder} (exists: {Directory.Exists(context.Options.GameFolder)})");
        Console.WriteLine($" Scraping query     : \"{context.Options.GameName}\"");
        Console.WriteLine($" Acceptance DB      : {AcceptanceContainer.DatabasePath}");
        Console.WriteLine($" Real app DB        : {Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Galbox", "galbox.db")} (untouched)");
        Console.WriteLine($" Verbose logs       : {context.Options.Verbose}");
        Console.WriteLine();

        Console.WriteLine(" Dependency-injection preflight (UI-free replica of App.xaml.cs ConfigureServices):");
        var serviceTypes = new (string Name, Type Type)[]
        {
            ("IGameUtilityService", typeof(IGameUtilityService)),
            ("ISaveManagementService", typeof(ISaveManagementService)),
            ("IGameScrapingService", typeof(IGameScrapingService)),
            ("IErrorCheckingService", typeof(IErrorCheckingService)),
            ("IScrapingCacheService", typeof(IScrapingCacheService)),
            ("IBangumiAuthService", typeof(IBangumiAuthService)),
            ("IAutoScrapingService", typeof(IAutoScrapingService)),
            ("IProcessMonitorService", typeof(IProcessMonitorService))
        };

        var failures = 0;
        foreach (var (name, type) in serviceTypes)
        {
            try
            {
                var instance = context.Services.GetService(type);
                if (instance is null)
                {
                    failures++;
                    Console.WriteLine($"   [MISSING] {name,-24} resolved to null");
                }
                else
                {
                    Console.WriteLine($"   [OK]      {name,-24} {instance.GetType().Name}");
                }
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine($"   [FAILED]  {name,-24} {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Prove the WinUI dependency is genuinely absent from the container.
        var navigationType = Type.GetType("Galbox.App.Services.INavigationService, Galbox.App");
        Console.WriteLine($"   [INFO]    INavigationService type present in Galbox.App assembly: {navigationType is not null}"
                        + " (intentionally not registered: it requires Microsoft.UI.Xaml.Controls)");
        Console.WriteLine($" Preflight failures : {failures}");
        Console.WriteLine(new string('=', width));
    }
}
