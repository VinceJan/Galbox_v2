using Galbox.App.Services;
using Galbox.Data.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace Galbox.Acceptance;

/// <summary>Command line options for the acceptance run.</summary>
public sealed class AcceptanceOptions
{
    /// <summary>Game folder used as the real test material (default: the Dreamin' Her install).</summary>
    public string GameFolder { get; set; } = @"D:\GAME\Dreamin'_Her";

    /// <summary>Display / original name used to build the test <see cref="GameInfo"/>.</summary>
    public string GameName { get; set; } = "Dreamin' Her";

    /// <summary>Dumps every captured log record instead of only WARNING and above.</summary>
    public bool Verbose { get; set; }

    /// <summary>Expected engine type for the A3 save-location check.</summary>
    public GameEngineType ExpectedEngine { get; set; } = GameEngineType.Renpy;

    /// <summary>Lower bound for the A2 folder-size check (900 MB, expressed in bytes).</summary>
    public long MinimumFolderSizeBytes { get; set; } = 900L * 1024 * 1024;

    /// <summary>Parses <c>--game</c>, <c>--name</c> and <c>--verbose</c>.</summary>
    public static AcceptanceOptions Parse(string[] args)
    {
        var options = new AcceptanceOptions();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--game" when i + 1 < args.Length:
                    options.GameFolder = args[++i];
                    break;
                case "--name" when i + 1 < args.Length:
                    options.GameName = args[++i];
                    break;
                case "--verbose" or "-v":
                    options.Verbose = true;
                    break;
            }
        }

        return options;
    }
}

/// <summary>
/// Shared per-run state: the resolved services, the options, the log sink, the
/// <see cref="GameInfo"/> persisted by A0 and the scraping result produced by A5 so that
/// later checks can reuse results instead of re-querying.
/// </summary>
public sealed class AcceptanceContext
{
    /// <summary>The UI-free DI container.</summary>
    public required ServiceProvider Services { get; init; }

    /// <summary>Run options.</summary>
    public required AcceptanceOptions Options { get; init; }

    /// <summary>Captured service log records.</summary>
    public required CollectingLoggerProvider Logs { get; init; }

    /// <summary>HTTP traffic observed on the application's own HttpClient pipelines.</summary>
    public required HttpTrafficRecorder Traffic { get; init; }

    /// <summary>The test game row persisted by A0 (has a real database Id).</summary>
    public GameInfo? PersistedGame { get; set; }

    /// <summary>Raw scraping result produced by A5, consumed by A6.</summary>
    public ScrapingResult? ScrapingQuery { get; set; }

    /// <summary>Query string A5 actually sent to the sources.</summary>
    public string? ScrapingQueryText { get; set; }

    /// <summary>Resolves a service from the container.</summary>
    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();
}
