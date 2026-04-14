using System;
using System.Threading;
using System.Threading.Tasks;
using Galbox.Data.Entities;

namespace Galbox.App.Services;

/// <summary>
/// Interface for process monitoring service.
/// Provides game process tracking, boss key functionality, and screenshot capture.
/// </summary>
public interface IProcessMonitorService
{
    #region Configuration

    /// <summary>
    /// Gets the current monitoring configuration.
    /// </summary>
    ProcessMonitorConfig Config { get; }

    /// <summary>
    /// Updates the monitoring configuration.
    /// </summary>
    /// <param name="config">New configuration to apply</param>
    void UpdateConfig(ProcessMonitorConfig config);

    /// <summary>
    /// Gets the screenshot save directory path.
    /// </summary>
    string ScreenshotDirectory { get; }

    /// <summary>
    /// Sets the screenshot save directory path.
    /// </summary>
    /// <param name="path">Directory path for saving screenshots</param>
    void SetScreenshotDirectory(string path);

    #endregion

    #region Game Registration

    /// <summary>
    /// Registers a game for process monitoring.
    /// </summary>
    /// <param name="game">Game information from database</param>
    /// <returns>True if registration successful</returns>
    bool RegisterGame(GameInfo game);

    /// <summary>
    /// Registers a game for process monitoring with custom settings.
    /// </summary>
    /// <param name="gameInfo">Detailed monitoring information</param>
    /// <returns>True if registration successful</returns>
    bool RegisterGame(MonitoredGameInfo gameInfo);

    /// <summary>
    /// Unregisters a game from process monitoring.
    /// </summary>
    /// <param name="gameId">Game ID to unregister</param>
    /// <returns>True if unregistration successful</returns>
    bool UnregisterGame(int gameId);

    /// <summary>
    /// Gets all registered games.
    /// </summary>
    /// <returns>List of registered game IDs</returns>
    IReadOnlyList<int> GetRegisteredGames();

    /// <summary>
    /// Checks if a game is registered for monitoring.
    /// </summary>
    /// <param name="gameId">Game ID to check</param>
    /// <returns>True if game is registered</returns>
    bool IsGameRegistered(int gameId);

    #endregion

    #region Process State

    /// <summary>
    /// Gets the current state of a monitored process.
    /// </summary>
    /// <param name="gameId">Game ID to query</param>
    /// <returns>Current process state, or null if not monitored</returns>
    MonitoredProcessState? GetProcessState(int gameId);

    /// <summary>
    /// Gets the current state of all monitored processes.
    /// </summary>
    /// <returns>Dictionary of game IDs to process states</returns>
    IReadOnlyDictionary<int, MonitoredProcessState> GetAllProcessStates();

    /// <summary>
    /// Checks if a specific process is currently running.
    /// </summary>
    /// <param name="processName">Process name to check</param>
    /// <returns>True if process is running</returns>
    bool IsProcessRunning(string processName);

    /// <summary>
    /// Gets the process ID for a running game.
    /// </summary>
    /// <param name="gameId">Game ID to query</param>
    /// <returns>Process ID if running, null otherwise</returns>
    int? GetProcessId(int gameId);

    /// <summary>
    /// Gets the main window handle for a running game.
    /// </summary>
    /// <param name="gameId">Game ID to query</param>
    /// <returns>Window handle if available, IntPtr.Zero otherwise</returns>
    IntPtr GetWindowHandle(int gameId);

    /// <summary>
    /// Gets the window title for a running game.
    /// </summary>
    /// <param name="gameId">Game ID to query</param>
    /// <returns>Window title if available, null otherwise</returns>
    string? GetWindowTitle(int gameId);

    #endregion

    #region Monitoring Control

    /// <summary>
    /// Starts the monitoring service.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task representing the start operation</returns>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the monitoring service.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task representing the stop operation</returns>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets whether the monitoring service is currently running.
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    /// Starts monitoring a specific game immediately (one-shot detection).
    /// </summary>
    /// <param name="gameId">Game ID to monitor</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Current state after detection</returns>
    Task<MonitoredProcessState?> DetectProcessStateAsync(int gameId, CancellationToken cancellationToken = default);

    #endregion

    #region Boss Key

    /// <summary>
    /// Registers the global boss key hotkey.
    /// </summary>
    /// <returns>True if hotkey registered successfully</returns>
    bool RegisterBossKey();

    /// <summary>
    /// Unregisters the global boss key hotkey.
    /// </summary>
    /// <returns>True if hotkey unregistered successfully</returns>
    bool UnregisterBossKey();

    /// <summary>
    /// Checks if boss key hotkey is currently registered.
    /// </summary>
    bool IsBossKeyRegistered { get; }

    /// <summary>
    /// Hides all monitored game windows (boss key activation).
    /// </summary>
    /// <returns>Number of windows hidden</returns>
    int HideAllWindows();

    /// <summary>
    /// Restores all hidden game windows (boss key deactivation).
    /// </summary>
    /// <returns>Number of windows restored</returns>
    int RestoreAllWindows();

    /// <summary>
    /// Gets whether windows are currently hidden by boss key.
    /// </summary>
    bool AreWindowsHidden { get; }

    /// <summary>
    /// Toggles boss key state (hide if shown, restore if hidden).
    /// </summary>
    /// <returns>True if windows are now hidden</returns>
    bool ToggleBossKey();

    /// <summary>
    /// Sets the window handle to use for hotkey registration.
    /// This should be called from the main window after it's created.
    /// </summary>
    /// <param name="windowHandle">Window handle for hotkey registration</param>
    void SetHotkeyWindowHandle(IntPtr windowHandle);

    #endregion

    #region Screenshot

    /// <summary>
    /// Captures a screenshot of a game window.
    /// </summary>
    /// <param name="gameId">Game ID to capture</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Screenshot result with file path and metadata</returns>
    Task<ScreenshotResult> CaptureScreenshotAsync(int gameId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Captures a screenshot of a specific window handle.
    /// </summary>
    /// <param name="windowHandle">Window handle to capture</param>
    /// <param name="gameName">Game name for filename</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Screenshot result with file path and metadata</returns>
    Task<ScreenshotResult> CaptureScreenshotAsync(IntPtr windowHandle, string gameName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Captures a screenshot of the currently focused window.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Screenshot result with file path and metadata</returns>
    Task<ScreenshotResult> CaptureFocusedWindowScreenshotAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the list of screenshots taken for a specific game.
    /// </summary>
    /// <param name="gameId">Game ID to query</param>
    /// <returns>List of screenshot file paths</returns>
    IReadOnlyList<string> GetScreenshotsForGame(int gameId);

    /// <summary>
    /// Deletes a screenshot file.
    /// </summary>
    /// <param name="filePath">Path to screenshot file</param>
    /// <returns>True if deleted successfully</returns>
    bool DeleteScreenshot(string filePath);

    #endregion

    #region Performance Metrics (Advanced Monitoring)

    /// <summary>
    /// Gets CPU usage for a running game process.
    /// </summary>
    /// <param name="gameId">Game ID to query</param>
    /// <returns>CPU usage percentage (0-100), or null if not available</returns>
    double? GetCpuUsage(int gameId);

    /// <summary>
    /// Gets memory usage for a running game process.
    /// </summary>
    /// <param name="gameId">Game ID to query</param>
    /// <returns>Memory usage in bytes, or null if not available</returns>
    long? GetMemoryUsage(int gameId);

    /// <summary>
    /// Gets GPU usage for a running game process (if supported).
    /// </summary>
    /// <param name="gameId">Game ID to query</param>
    /// <returns>GPU usage percentage (0-100), or null if not available</returns>
    double? GetGpuUsage(int gameId);

    /// <summary>
    /// Gets comprehensive performance metrics for a game.
    /// </summary>
    /// <param name="gameId">Game ID to query</param>
    /// <returns>Performance metrics dictionary</returns>
    Dictionary<string, object> GetPerformanceMetrics(int gameId);

    #endregion

    #region Events

    /// <summary>
    /// Event raised when a process state changes.
    /// </summary>
    event EventHandler<ProcessStateChangedEventArgs>? ProcessStateChanged;

    /// <summary>
    /// Event raised when a game process starts.
    /// </summary>
    event EventHandler<ProcessStateChangedEventArgs>? ProcessStarted;

    /// <summary>
    /// Event raised when a game process exits.
    /// </summary>
    event EventHandler<ProcessStateChangedEventArgs>? ProcessExited;

    /// <summary>
    /// Event raised when boss key is activated/deactivated.
    /// </summary>
    event EventHandler<BossKeyEventArgs>? BossKeyActivated;

    /// <summary>
    /// Event raised when a screenshot is captured.
    /// </summary>
    event EventHandler<ScreenshotResult>? ScreenshotCaptured;

    /// <summary>
    /// Event raised when an error occurs during monitoring.
    /// </summary>
    event EventHandler<Exception>? MonitoringError;

    #endregion
}