using Galbox.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Galbox.App.Services;

/// <summary>
/// Result of the process-monitor startup step.
/// </summary>
/// <param name="GamesInLibrary">Number of games found in the library.</param>
/// <param name="GamesRegistered">Number of games handed to the process monitor.</param>
/// <param name="Started">Whether the monitor reports itself as running afterwards.</param>
/// <param name="ErrorMessage">Failure text when the step could not complete.</param>
public sealed record ProcessMonitorStartupResult(
    int GamesInLibrary,
    int GamesRegistered,
    bool Started,
    string? ErrorMessage);

/// <summary>
/// The single startup step that turns the process monitor on.
///
/// The audit found <c>ProcessMonitorService.StartAsync()</c> had <b>zero callers</b>, which left
/// the boss key, the real running-state detection and screenshot-on-exit dead even though 1851
/// lines of working implementation were present, and left <c>RegisterGame</c> with zero callers
/// so no game was ever tracked.
///
/// The step lives here, in a WinUI-free class, for one reason: the application calls it during
/// <c>OnLaunched</c>, and the headless acceptance harness calls the exact same method, so the
/// acceptance run measures the shipping startup wiring instead of a copy of it.
/// </summary>
public static class ProcessMonitorStartup
{
    /// <summary>
    /// Registers every game in the library with <paramref name="services"/>'s process monitor and
    /// starts monitoring.
    /// </summary>
    /// <param name="services">The application service provider.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<ProcessMonitorStartupResult> RegisterLibraryAndStartAsync(
        IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        var logger = services.GetService<ILoggerFactory>()?.CreateLogger("ProcessMonitorStartup");
        var monitor = services.GetService<IProcessMonitorService>();

        if (monitor is null)
        {
            return new ProcessMonitorStartupResult(0, 0, false, "IProcessMonitorService is not registered");
        }

        try
        {
            var factory = services.GetRequiredService<IDbContextFactory<GalboxDbContext>>();
            await using var dbContext = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

            // AsNoTracking: the monitor only needs the id / name / executable, and the startup
            // path must not leave tracked entities behind for a later scope to trip over.
            var games = await dbContext.Games
                .AsNoTracking()
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var registered = 0;
            foreach (var game in games)
            {
                if (monitor.RegisterGame(game))
                {
                    registered++;
                }
                else
                {
                    logger?.LogWarning(
                        "Process monitor could not register game {GameId} ({GameName}): no usable executable",
                        game.Id, game.DisplayName);
                }
            }

            await monitor.StartAsync(cancellationToken).ConfigureAwait(false);

            logger?.LogInformation(
                "Process monitor started: {Registered}/{Total} library game(s) registered",
                registered, games.Count);

            return new ProcessMonitorStartupResult(games.Count, registered, monitor.IsRunning, null);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to start the process monitor");
            return new ProcessMonitorStartupResult(0, 0, monitor.IsRunning, $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
