using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Galbox.Data.Entities;
using Microsoft.Extensions.Logging;

namespace Galbox.App.Services;

/// <summary>
/// Implementation of process monitoring service.
/// Provides game process tracking, boss key functionality, and screenshot capture.
/// Uses Windows Process API and Win32 API for window handle tracking.
/// </summary>
public class ProcessMonitorService : IProcessMonitorService, IDisposable
{
    #region Win32 API Imports

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, IntPtr lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsIconic(IntPtr hWnd); // Check if minimized

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsZoomed(IntPtr hWnd); // Check if maximized

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);

    [DllImport("gdi32.dll")]
    private static extern IntPtr GetWindowDC(IntPtr hWnd);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hDC, int nWidth, int nHeight);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hObject, int nXDest, int nYDest, int nWidth, int nHeight, IntPtr hObjSource, int nXSrc, int nYSrc, int dwRop);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hDC);

    [DllImport("user32.dll")]
    private static extern bool ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, int nFlags);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    // ShowWindow constants
    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;
    private const int SW_SHOWNA = 8;
    private const int SW_RESTORE = 9;
    private const int SW_MINIMIZE = 6;
    private const int SW_SHOWMAXIMIZED = 3;
    private const int SW_SHOWNORMAL = 1;

    // BitBlt constant
    private const int SRCCOPY = 0x00CC0020;

    // Hotkey ID
    private const int HOTKEY_ID = 0x0001;

    // PrintWindow flags
    private const int PW_CLIENTONLY = 0x1;

    /// <summary>
    /// Stores window handle and its original state for proper restoration.
    /// </summary>
    private struct WindowRestoreInfo
    {
        public IntPtr Handle;
        public WindowState OriginalState;
    }

    #endregion

    private readonly ILogger<ProcessMonitorService> _logger;
    private readonly ConcurrentDictionary<int, MonitoredGameInfo> _registeredGames;
    private readonly ConcurrentDictionary<int, MonitoredProcessState> _processStates;
    private readonly ConcurrentDictionary<int, List<Process>> _trackedProcesses;
    private readonly ConcurrentDictionary<int, List<WindowRestoreInfo>> _hiddenWindows;
    private readonly ConcurrentDictionary<int, PerformanceCounter?> _cpuCounters;
    private readonly ConcurrentDictionary<int, float> _cpuSampledValues;  // Stores sampled CPU values
    private readonly ConcurrentDictionary<string, byte> _screenshots;

    private Task? _cpuSamplingTask;  // Background CPU sampling task

    private ProcessMonitorConfig _config;
    private CancellationTokenSource? _monitoringCts;
    private Task? _monitoringTask;
    private IntPtr _hotkeyWindowHandle;
    private bool _isBossKeyRegistered;
    private bool _areWindowsHidden;
    private bool _disposed;
    private bool _isRunning;

    private readonly string _defaultScreenshotDirectory;

    /// <summary>
    /// Creates a ProcessMonitorService with injected logger.
    /// </summary>
    /// <param name="logger">Logger for diagnostics</param>
    public ProcessMonitorService(ILogger<ProcessMonitorService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _registeredGames = new ConcurrentDictionary<int, MonitoredGameInfo>();
        _processStates = new ConcurrentDictionary<int, MonitoredProcessState>();
        _trackedProcesses = new ConcurrentDictionary<int, List<Process>>();
        _hiddenWindows = new ConcurrentDictionary<int, List<WindowRestoreInfo>>();
        _cpuCounters = new ConcurrentDictionary<int, PerformanceCounter?>();
        _cpuSampledValues = new ConcurrentDictionary<int, float>();
        _screenshots = new ConcurrentDictionary<string, byte>();

        _config = new ProcessMonitorConfig();

        // Initialize default screenshot directory
        _defaultScreenshotDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Galbox",
            "Screenshots");
        _config.ScreenshotDirectory = _defaultScreenshotDirectory;

        EnsureScreenshotDirectoryExists();

        _logger.LogInformation("ProcessMonitorService initialized");
    }

    #region Configuration

    /// <inheritdoc/>
    public ProcessMonitorConfig Config => _config;

    /// <inheritdoc/>
    public void UpdateConfig(ProcessMonitorConfig config)
    {
        if (config == null)
        {
            throw new ArgumentNullException(nameof(config));
        }

        _logger.LogInformation("Updating process monitor configuration");

        // Unregister old boss key if registered
        if (_isBossKeyRegistered)
        {
            UnregisterBossKey();
        }

        _config = config;

        // Ensure screenshot directory exists
        if (!string.IsNullOrEmpty(config.ScreenshotDirectory))
        {
            EnsureScreenshotDirectoryExists();
        }

        // Register new boss key if enabled
        if (config.EnableBossKey && _isRunning)
        {
            RegisterBossKey();
        }

        _logger.LogInformation("Process monitor configuration updated");
    }

    /// <inheritdoc/>
    public string ScreenshotDirectory => _config.ScreenshotDirectory;

    /// <inheritdoc/>
    public void SetScreenshotDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Screenshot directory path cannot be empty", nameof(path));
        }

        _config.ScreenshotDirectory = path;
        EnsureScreenshotDirectoryExists();
        _logger.LogInformation("Screenshot directory set to: {Path}", path);
    }

    private void EnsureScreenshotDirectoryExists()
    {
        try
        {
            var path = _config.ScreenshotDirectory;
            if (!string.IsNullOrEmpty(path) && !Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
                _logger.LogInformation("Created screenshot directory: {Path}", path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create screenshot directory");
            // Fallback to default
            _config.ScreenshotDirectory = _defaultScreenshotDirectory;
        }
    }

    #endregion

    #region Game Registration

    /// <inheritdoc/>
    public bool RegisterGame(GameInfo game)
    {
        if (game == null)
        {
            _logger.LogWarning("Cannot register null game");
            return false;
        }

        var processName = GetProcessNameFromExecutable(game.MainExecutable);
        if (string.IsNullOrEmpty(processName))
        {
            _logger.LogWarning("Cannot determine process name for game: {GameId}", game.Id);
            return false;
        }

        var gameInfo = new MonitoredGameInfo
        {
            GameId = game.Id,
            GameName = game.DisplayName,
            ProcessName = processName,
            ExecutablePath = game.MainExecutable,
            WindowTitlePattern = null
        };

        return RegisterGame(gameInfo);
    }

    /// <inheritdoc/>
    public bool RegisterGame(MonitoredGameInfo gameInfo)
    {
        if (gameInfo == null)
        {
            _logger.LogWarning("Cannot register null game info");
            return false;
        }

        if (string.IsNullOrEmpty(gameInfo.ProcessName))
        {
            _logger.LogWarning("Cannot register game with empty process name: {GameId}", gameInfo.GameId);
            return false;
        }

        _registeredGames.TryAdd(gameInfo.GameId, gameInfo);

        // Initialize state
        var state = new MonitoredProcessState
        {
            GameId = gameInfo.GameId,
            GameName = gameInfo.GameName,
            ProcessName = gameInfo.ProcessName,
            State = ProcessState.NotRunning,
            LastUpdateTime = DateTime.UtcNow
        };
        _processStates.TryAdd(gameInfo.GameId, state);

        _logger.LogInformation(
            "Registered game for monitoring: {GameName} (ID: {GameId}, Process: {ProcessName})",
            gameInfo.GameName,
            gameInfo.GameId,
            gameInfo.ProcessName);

        return true;
    }

    /// <inheritdoc/>
    public bool UnregisterGame(int gameId)
    {
        if (!_registeredGames.TryRemove(gameId, out _))
        {
            _logger.LogWarning("Game not registered: {GameId}", gameId);
            return false;
        }

        _processStates.TryRemove(gameId, out _);

        // Clean up tracked processes (now a list)
        if (_trackedProcesses.TryRemove(gameId, out var processList))
        {
            foreach (var process in processList)
            {
                try
                {
                    process?.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error disposing process for game: {GameId}", gameId);
                }
            }
        }

        // Clean up CPU counter
        if (_cpuCounters.TryRemove(gameId, out var counter))
        {
            try
            {
                counter?.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing CPU counter for game: {GameId}", gameId);
            }
        }

        // Remove from hidden windows tracking
        _hiddenWindows.TryRemove(gameId, out _);

        _logger.LogInformation("Unregistered game from monitoring: {GameId}", gameId);
        return true;
    }

    /// <inheritdoc/>
    public IReadOnlyList<int> GetRegisteredGames()
    {
        return _registeredGames.Keys.ToList().AsReadOnly();
    }

    /// <inheritdoc/>
    public bool IsGameRegistered(int gameId)
    {
        return _registeredGames.ContainsKey(gameId);
    }

    private static string GetProcessNameFromExecutable(string executablePath)
    {
        if (string.IsNullOrEmpty(executablePath))
        {
            return string.Empty;
        }

        try
        {
            var fileName = Path.GetFileNameWithoutExtension(executablePath);
            return fileName ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Gets the correct PerformanceCounter instance name for a process.
    /// When multiple processes have the same name, PerformanceCounter uses names like "process#1", "process#2".
    /// </summary>
    /// <param name="process">The process to get the instance name for</param>
    /// <returns>The PerformanceCounter instance name</returns>
    private static string GetPerformanceCounterInstanceName(Process process)
    {
        var processName = process.ProcessName;

        // Get all processes with the same name to determine the instance index
        var processesWithSameName = Process.GetProcessesByName(processName);

        // Find the index of our process
        int instanceIndex = -1;
        for (int i = 0; i < processesWithSameName.Length; i++)
        {
            if (processesWithSameName[i].Id == process.Id)
            {
                instanceIndex = i;
                break;
            }
            try
            {
                processesWithSameName[i]?.Dispose();
            }
            catch { }
        }

        // Dispose remaining processes
        for (int i = instanceIndex + 1; i < processesWithSameName.Length; i++)
        {
            try
            {
                processesWithSameName[i]?.Dispose();
            }
            catch { }
        }

        // For the first instance (or single instance), use the process name directly
        // For subsequent instances, use "processName#1", "processName#2", etc.
        if (instanceIndex <= 0)
        {
            return processName;
        }
        else
        {
            return $"{processName}#{instanceIndex}";
        }
    }

    #endregion

    #region Process State

    /// <inheritdoc/>
    public MonitoredProcessState? GetProcessState(int gameId)
    {
        _processStates.TryGetValue(gameId, out var state);
        return state;
    }

    /// <inheritdoc/>
    public IReadOnlyDictionary<int, MonitoredProcessState> GetAllProcessStates()
    {
        return new Dictionary<int, MonitoredProcessState>(_processStates).AsReadOnly();
    }

    /// <inheritdoc/>
    public bool IsProcessRunning(string processName)
    {
        if (string.IsNullOrEmpty(processName))
        {
            return false;
        }

        try
        {
            var processes = Process.GetProcessesByName(processName);
            var isRunning = processes.Length > 0;

            // Dispose all process instances to avoid resource leak
            foreach (var process in processes)
            {
                try
                {
                    process?.Dispose();
                }
                catch { }
            }

            return isRunning;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error checking if process is running: {ProcessName}", processName);
            return false;
        }
    }

    /// <inheritdoc/>
    public int? GetProcessId(int gameId)
    {
        if (_processStates.TryGetValue(gameId, out var state) && state.ProcessId.HasValue)
        {
            return state.ProcessId.Value;
        }
        return null;
    }

    /// <inheritdoc/>
    public IntPtr GetWindowHandle(int gameId)
    {
        if (_processStates.TryGetValue(gameId, out var state))
        {
            return state.WindowHandle;
        }
        return IntPtr.Zero;
    }

    /// <inheritdoc/>
    public string? GetWindowTitle(int gameId)
    {
        if (_processStates.TryGetValue(gameId, out var state))
        {
            return state.WindowTitle;
        }
        return null;
    }

    #endregion

    #region Monitoring Control

    /// <inheritdoc/>
    public bool IsRunning => _isRunning;

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_isRunning)
        {
            _logger.LogWarning("Process monitor is already running");
            return;
        }

        _logger.LogInformation("Starting process monitor");

        _monitoringCts = new CancellationTokenSource();
        _isRunning = true;

        // Register boss key if enabled
        if (_config.EnableBossKey)
        {
            RegisterBossKey();
        }

        // Start monitoring loop
        _monitoringTask = Task.Run(() => MonitoringLoop(_monitoringCts.Token), cancellationToken);

        // Start background CPU sampling loop (runs independently to avoid blocking main loop)
        if (_config.EnableAdvancedMonitoring)
        {
            _cpuSamplingTask = Task.Run(() => CpuSamplingLoop(_monitoringCts.Token), cancellationToken);
        }

        _logger.LogInformation("Process monitor started");

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!_isRunning)
        {
            _logger.LogWarning("Process monitor is not running");
            return;
        }

        _logger.LogInformation("Stopping process monitor");

        // Unregister boss key
        if (_isBossKeyRegistered)
        {
            UnregisterBossKey();
        }

        // Stop monitoring loop
        _monitoringCts?.Cancel();

        if (_monitoringTask != null)
        {
            try
            {
                await _monitoringTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during monitoring task shutdown");
            }
        }

        // Restore any hidden windows
        if (_areWindowsHidden)
        {
            RestoreAllWindows();
        }

        // Clean up tracked processes (now lists)
        foreach (var kvp in _trackedProcesses)
        {
            foreach (var process in kvp.Value)
            {
                try
                {
                    process?.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error disposing tracked process for game: {GameId}", kvp.Key);
                }
            }
        }
        _trackedProcesses.Clear();

        // Clean up CPU counters
        foreach (var kvp in _cpuCounters)
        {
            try
            {
                kvp.Value?.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing CPU counter for game: {GameId}", kvp.Key);
            }
        }
        _cpuCounters.Clear();
        _cpuSampledValues.Clear();

        _isRunning = false;
        _monitoringCts?.Dispose();
        _monitoringCts = null;

        _logger.LogInformation("Process monitor stopped");
    }

    /// <inheritdoc/>
    public async Task<MonitoredProcessState?> DetectProcessStateAsync(int gameId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_registeredGames.TryGetValue(gameId, out var gameInfo))
        {
            _logger.LogWarning("Game not registered for detection: {GameId}", gameId);
            return null;
        }

        try
        {
            var state = await DetectProcessStateInternalAsync(gameInfo, cancellationToken).ConfigureAwait(false);
            _processStates[gameId] = state;
            return state;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error detecting process state for game: {GameId}", gameId);
            MonitoringError?.Invoke(this, ex);
            return null;
        }
    }

    private async Task MonitoringLoop(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Monitoring loop started");

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await UpdateAllProcessStatesAsync(cancellationToken).ConfigureAwait(false);

                // Wait for next interval
                await Task.Delay(_config.MonitoringIntervalMs, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in monitoring loop");
                MonitoringError?.Invoke(this, ex);

                // Wait before retrying
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
            }
        }

        _logger.LogInformation("Monitoring loop stopped");
    }

    private async Task UpdateAllProcessStatesAsync(CancellationToken cancellationToken)
    {
        foreach (var kvp in _registeredGames)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var gameInfo = kvp.Value;
                var previousState = _processStates.TryGetValue(gameInfo.GameId, out var state) ? state : null;
                var previousProcessState = previousState?.State ?? ProcessState.NotRunning;

                var newState = await DetectProcessStateInternalAsync(gameInfo, cancellationToken).ConfigureAwait(false);
                _processStates[gameInfo.GameId] = newState;

                // Fire events if state changed
                if (previousProcessState != newState.State)
                {
                    var eventArgs = new ProcessStateChangedEventArgs
                    {
                        GameId = gameInfo.GameId,
                        GameName = gameInfo.GameName,
                        PreviousState = previousProcessState,
                        NewState = newState.State,
                        ProcessId = newState.ProcessId,
                        WindowHandle = newState.WindowHandle,
                        Timestamp = DateTime.UtcNow,
                        SessionDurationSeconds = newState.DurationSeconds
                    };

                    ProcessStateChanged?.Invoke(this, eventArgs);

                    if (newState.State == ProcessState.Running && previousProcessState != ProcessState.Running)
                    {
                        _logger.LogInformation("Process started: {GameName} (PID: {ProcessId})", gameInfo.GameName, newState.ProcessId);
                        ProcessStarted?.Invoke(this, eventArgs);
                    }

                    if (newState.State == ProcessState.Exited && previousProcessState == ProcessState.Running)
                    {
                        _logger.LogInformation("Process exited: {GameName} (Duration: {Duration}s)", gameInfo.GameName, newState.DurationSeconds);
                        ProcessExited?.Invoke(this, eventArgs);

                        // Auto screenshot on exit if configured
                        if (_config.AutoScreenshotOnExit && previousState?.WindowHandle != IntPtr.Zero)
                        {
                            var windowHandle = previousState!.WindowHandle;
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await CaptureScreenshotAsync(windowHandle, gameInfo.GameName, CancellationToken.None)
                                        .ConfigureAwait(false);
                                }
                                catch (Exception screenshotEx)
                                {
                                    _logger.LogWarning(screenshotEx, "Auto screenshot on exit failed for: {GameName}", gameInfo.GameName);
                                }
                            });
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating process state for game: {GameId}", kvp.Key);
            }
        }
    }

    private async Task<MonitoredProcessState> DetectProcessStateInternalAsync(
        MonitoredGameInfo gameInfo,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var state = new MonitoredProcessState
        {
            GameId = gameInfo.GameId,
            GameName = gameInfo.GameName,
            ProcessName = gameInfo.ProcessName,
            LastUpdateTime = DateTime.UtcNow
        };

        // Check if process is running
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(gameInfo.ProcessName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error getting processes for: {ProcessName}", gameInfo.ProcessName);
            state.State = ProcessState.Unknown;
            return state;
        }

        if (processes.Length == 0)
        {
            // Process not running - check if it was previously running (exited)
            var previousState = _processStates.TryGetValue(gameInfo.GameId, out var ps) ? ps : null;

            if (previousState?.State == ProcessState.Running)
            {
                state.State = ProcessState.Exited;
                state.ExitTime = DateTime.UtcNow;
                state.StartTime = previousState.StartTime;
                state.DurationSeconds = previousState.DurationSeconds;

                // Clean up tracked processes (now a list)
                if (_trackedProcesses.TryRemove(gameInfo.GameId, out var trackedProcessList))
                {
                    foreach (var trackedProcess in trackedProcessList)
                    {
                        try
                        {
                            trackedProcess?.Dispose();
                        }
                        catch { }
                    }
                }

                // Clean up CPU counter
                if (_cpuCounters.TryRemove(gameInfo.GameId, out var counter))
                {
                    try
                    {
                        counter?.Dispose();
                    }
                    catch { }
                }
            }
            else
            {
                state.State = ProcessState.NotRunning;
            }

            return state;
        }

        // Process is running - track all instances
        state.State = ProcessState.Running;

        // Create or update the list of tracked processes
        var trackedList = new List<Process>();
        foreach (var proc in processes)
        {
            trackedList.Add(proc);
        }

        // Remove old tracked processes if they exist
        if (_trackedProcesses.TryGetValue(gameInfo.GameId, out var oldList))
        {
            foreach (var oldProc in oldList)
            {
                // Check if old process is still in new list
                var stillRunning = trackedList.Any(p => p.Id == oldProc.Id);
                if (!stillRunning)
                {
                    try
                    {
                        oldProc?.Dispose();
                    }
                    catch { }
                }
            }
        }

        _trackedProcesses[gameInfo.GameId] = trackedList;

        // Use the first process for primary state tracking
        var primaryProcess = processes[0];
        state.ProcessId = primaryProcess.Id;

        // Get process start time
        try
        {
            state.StartTime = primaryProcess.StartTime;
            state.DurationSeconds = (long)(DateTime.UtcNow - primaryProcess.StartTime).TotalSeconds;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error getting process start time for: {ProcessName}", gameInfo.ProcessName);
            // Use cached start time if available
            var prevState = _processStates.TryGetValue(gameInfo.GameId, out var prev) ? prev : null;
            if (prevState?.StartTime.HasValue == true)
            {
                state.StartTime = prevState.StartTime;
                state.DurationSeconds = (long)(DateTime.UtcNow - prevState.StartTime.Value).TotalSeconds;
            }
        }

        // Find window handle - search all instances
        state.WindowHandle = IntPtr.Zero;
        foreach (var proc in processes)
        {
            var hWnd = FindProcessWindow(proc.Id, gameInfo.WindowTitlePattern);
            if (hWnd != IntPtr.Zero)
            {
                state.WindowHandle = hWnd;
                break; // Use first found window
            }
        }

        // Get window info
        if (state.WindowHandle != IntPtr.Zero)
        {
            state.WindowTitle = GetWindowTitleFromHandle(state.WindowHandle);
            state.WindowState = DetermineWindowState(state.WindowHandle);
            state.IsFocused = state.WindowHandle == GetForegroundWindow();
            state.IsHiddenByBossKey = _hiddenWindows.ContainsKey(gameInfo.GameId);
        }

        // Advanced monitoring (CPU/Memory/GPU)
        if (_config.EnableAdvancedMonitoring)
        {
            // Create CPU counter if not exists (this is a quick operation)
            if (!_cpuCounters.ContainsKey(gameInfo.GameId))
            {
                try
                {
                    var instanceName = GetPerformanceCounterInstanceName(primaryProcess);
                    var counter = new PerformanceCounter("Process", "% Processor Time", instanceName);
                    counter.NextValue(); // First call returns 0, initialize counter
                    _cpuCounters[gameInfo.GameId] = counter;
                    _logger.LogInformation("Created CPU counter for game: {GameName} (Instance: {InstanceName})", gameInfo.GameName, instanceName);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not create CPU counter for: {ProcessName}", primaryProcess.ProcessName);
                    _cpuCounters[gameInfo.GameId] = null;
                }
            }

            await UpdateAdvancedMetricsAsync(state, primaryProcess, cancellationToken).ConfigureAwait(false);
        }

        // Dispose other process instances (keep only the tracked one)
        for (int i = 1; i < processes.Length; i++)
        {
            try
            {
                processes[i]?.Dispose();
            }
            catch { }
        }

        return state;
    }

    private Task UpdateAdvancedMetricsAsync(
        MonitoredProcessState state,
        Process process,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            // Memory usage - this is non-blocking
            process.Refresh();
            state.MemoryUsageBytes = process.WorkingSet64;

            // CPU usage - read pre-sampled value (non-blocking)
            // The actual sampling is done by the background CpuSamplingLoop
            if (_cpuCounters.ContainsKey(state.GameId))
            {
                // Read the latest sampled CPU value
                if (_cpuSampledValues.TryGetValue(state.GameId, out var cpuValue))
                {
                    state.CpuUsagePercent = cpuValue;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error updating advanced metrics for game: {GameId}", state.GameId);
        }

        // GPU usage is more complex - would need NvAPI or similar
        // For now, leave as null (not supported in basic implementation)
        state.GpuUsagePercent = null;

        return Task.CompletedTask;
    }

    /// <summary>
    /// Background CPU sampling loop - runs independently from main monitoring loop.
    /// This avoids blocking the main loop with CPU measurement delays.
    /// </summary>
    private async Task CpuSamplingLoop(CancellationToken cancellationToken)
    {
        _logger.LogInformation("CPU sampling loop started");

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Sample CPU for all registered games with counters
                foreach (var kvp in _cpuCounters)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    var gameId = kvp.Key;
                    var counter = kvp.Value;

                    if (counter != null)
                    {
                        try
                        {
                            // Sample CPU value
                            var cpuValue = counter.NextValue();
                            _cpuSampledValues[gameId] = cpuValue;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Error sampling CPU for game: {GameId}", gameId);
                            _cpuSampledValues.TryRemove(gameId, out _);
                        }
                    }
                }

                // Wait before next sampling cycle
                await Task.Delay(_config.CpuSamplingIntervalMs, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in CPU sampling loop");
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
            }
        }

        _logger.LogInformation("CPU sampling loop stopped");
    }

    private static IntPtr FindProcessWindow(int processId, string? titlePattern)
    {
        IntPtr windowHandle = IntPtr.Zero;

        EnumWindows((hWnd, lParam) =>
        {
            GetWindowThreadProcessId(hWnd, out uint pid);

            if (pid == processId && IsWindowVisible(hWnd))
            {
                var title = GetWindowTitleFromHandle(hWnd);

                if (!string.IsNullOrEmpty(titlePattern) && title != null)
                {
                    if (title.Contains(titlePattern, StringComparison.OrdinalIgnoreCase))
                    {
                        windowHandle = hWnd;
                        return false; // Stop enumeration
                    }
                }
                else if (!string.IsNullOrEmpty(title))
                {
                    // Take the first visible window with a title
                    windowHandle = hWnd;
                    return false;
                }
            }

            return true; // Continue enumeration
        }, IntPtr.Zero);

        return windowHandle;
    }

    private static string? GetWindowTitleFromHandle(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero)
        {
            return null;
        }

        var length = GetWindowTextLength(hWnd);
        if (length == 0)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal((length + 1) * sizeof(char));
        try
        {
            GetWindowText(hWnd, buffer, length + 1);
            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static WindowState DetermineWindowState(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero)
        {
            return WindowState.Unknown;
        }

        if (!IsWindowVisible(hWnd))
        {
            return WindowState.Hidden;
        }

        if (IsIconic(hWnd))
        {
            return WindowState.Minimized;
        }

        if (IsZoomed(hWnd))
        {
            return WindowState.Maximized;
        }

        return WindowState.Normal;
    }

    #endregion

    #region Boss Key

    /// <inheritdoc/>
    public bool IsBossKeyRegistered => _isBossKeyRegistered;

    /// <inheritdoc/>
    public bool AreWindowsHidden => _areWindowsHidden;

    /// <inheritdoc/>
    public bool RegisterBossKey()
    {
        if (_isBossKeyRegistered)
        {
            _logger.LogWarning("Boss key already registered");
            return true;
        }

        if (_hotkeyWindowHandle == IntPtr.Zero)
        {
            _logger.LogWarning("No window handle available for hotkey registration");
            return false;
        }

        try
        {
            var modifiers = (uint)_config.BossKeyModifiers;
            var vk = _config.BossKeyVirtualKey;

            var result = RegisterHotKey(_hotkeyWindowHandle, HOTKEY_ID, modifiers, vk);

            if (result)
            {
                _isBossKeyRegistered = true;
                _logger.LogInformation(
                    "Boss key registered: {Modifiers} + {Key}",
                    _config.BossKeyModifiers,
                    (char)_config.BossKeyVirtualKey);
                return true;
            }
            else
            {
                var error = Marshal.GetLastWin32Error();
                _logger.LogError("Failed to register boss key. Error code: {ErrorCode}", error);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error registering boss key");
            return false;
        }
    }

    /// <inheritdoc/>
    public bool UnregisterBossKey()
    {
        if (!_isBossKeyRegistered)
        {
            return true;
        }

        try
        {
            var result = UnregisterHotKey(_hotkeyWindowHandle, HOTKEY_ID);

            if (result)
            {
                _isBossKeyRegistered = false;
                _logger.LogInformation("Boss key unregistered");
                return true;
            }
            else
            {
                var error = Marshal.GetLastWin32Error();
                _logger.LogWarning("Failed to unregister boss key. Error code: {ErrorCode}", error);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error unregistering boss key");
            return false;
        }
    }

    /// <summary>
    /// Sets the window handle to use for hotkey registration.
    /// This should be called from the main window after it's created.
    /// </summary>
    /// <param name="windowHandle">Window handle for hotkey registration</param>
    public void SetHotkeyWindowHandle(IntPtr windowHandle)
    {
        _hotkeyWindowHandle = windowHandle;

        // Re-register boss key if needed
        if (_config.EnableBossKey && _isRunning && !_isBossKeyRegistered)
        {
            RegisterBossKey();
        }

        _logger.LogInformation("Hotkey window handle set: {Handle}", windowHandle);
    }

    /// <inheritdoc/>
    public int HideAllWindows()
    {
        var windowsHidden = 0;
        _hiddenWindows.Clear();

        foreach (var kvp in _processStates)
        {
            var state = kvp.Value;

            if (state.State == ProcessState.Running &&
                state.WindowHandle != IntPtr.Zero &&
                IsWindowVisible(state.WindowHandle))
            {
                // Store original window state for restoration
                var originalState = state.WindowState;
                var originalHandle = state.WindowHandle;

                // Hide the window
                ShowWindow(state.WindowHandle, SW_HIDE);

                // Track hidden window with its original state
                if (!_hiddenWindows.ContainsKey(kvp.Key))
                {
                    _hiddenWindows[kvp.Key] = new List<WindowRestoreInfo>();
                }
                _hiddenWindows[kvp.Key].Add(new WindowRestoreInfo
                {
                    Handle = originalHandle,
                    OriginalState = originalState
                });

                windowsHidden++;
                state.IsHiddenByBossKey = true;
                state.WindowState = WindowState.Hidden;

                _logger.LogInformation("Hidden window for game: {GameName}", state.GameName);
            }
        }

        _areWindowsHidden = true;

        var eventArgs = new BossKeyEventArgs
        {
            IsHidden = true,
            WindowsAffected = windowsHidden,
            Timestamp = DateTime.UtcNow
        };
        BossKeyActivated?.Invoke(this, eventArgs);

        _logger.LogInformation("Boss key activated - {Count} windows hidden", windowsHidden);
        return windowsHidden;
    }

    /// <inheritdoc/>
    public int RestoreAllWindows()
    {
        var windowsRestored = 0;

        foreach (var kvp in _hiddenWindows)
        {
            foreach (var windowInfo in kvp.Value)
            {
                if (windowInfo.Handle != IntPtr.Zero)
                {
                    // Restore the window with correct state
                    int showCmd;
                    switch (windowInfo.OriginalState)
                    {
                        case WindowState.Maximized:
                            showCmd = SW_SHOWMAXIMIZED;
                            break;
                        case WindowState.Minimized:
                            showCmd = SW_MINIMIZE;
                            break;
                        case WindowState.Normal:
                            showCmd = SW_SHOWNORMAL;
                            break;
                        default:
                            showCmd = SW_RESTORE;
                            break;
                    }

                    ShowWindow(windowInfo.Handle, showCmd);

                    // Update state
                    if (_processStates.TryGetValue(kvp.Key, out var state))
                    {
                        state.IsHiddenByBossKey = false;
                        state.WindowState = windowInfo.OriginalState;
                    }

                    windowsRestored++;
                    _logger.LogInformation("Restored window for game: {GameId} (State: {State})", kvp.Key, windowInfo.OriginalState);
                }
            }
        }

        _hiddenWindows.Clear();
        _areWindowsHidden = false;

        var eventArgs = new BossKeyEventArgs
        {
            IsHidden = false,
            WindowsAffected = windowsRestored,
            Timestamp = DateTime.UtcNow
        };
        BossKeyActivated?.Invoke(this, eventArgs);

        _logger.LogInformation("Boss key deactivated - {Count} windows restored", windowsRestored);
        return windowsRestored;
    }

    /// <inheritdoc/>
    public bool ToggleBossKey()
    {
        if (_areWindowsHidden)
        {
            RestoreAllWindows();
            return false;
        }
        else
        {
            HideAllWindows();
            return true;
        }
    }

    /// <summary>
    /// Handles hotkey message from the window.
    /// This should be called when the window receives a WM_HOTKEY message.
    /// </summary>
    public void HandleHotkeyPressed()
    {
        ToggleBossKey();
    }

    #endregion

    #region Screenshot

    /// <inheritdoc/>
    public async Task<ScreenshotResult> CaptureScreenshotAsync(int gameId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var state = GetProcessState(gameId);
        if (state == null || state.WindowHandle == IntPtr.Zero)
        {
            var result = new ScreenshotResult
            {
                Success = false,
                ErrorMessage = "Game not running or no window found"
            };
            _logger.LogWarning("Cannot capture screenshot: game not running or no window. GameId: {GameId}", gameId);
            return result;
        }

        return await CaptureScreenshotAsync(state.WindowHandle, state.GameName ?? "Unknown", cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<ScreenshotResult> CaptureScreenshotAsync(IntPtr windowHandle, string gameName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var result = new ScreenshotResult
        {
            Timestamp = DateTime.UtcNow
        };

        if (windowHandle == IntPtr.Zero)
        {
            result.Success = false;
            result.ErrorMessage = "Invalid window handle";
            return result;
        }

        try
        {
            // Get window bounds
            if (!GetWindowRect(windowHandle, out RECT rect))
            {
                result.Success = false;
                result.ErrorMessage = "Could not get window rectangle";
                return result;
            }

            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;

            if (width <= 0 || height <= 0)
            {
                result.Success = false;
                result.ErrorMessage = "Invalid window dimensions";
                return result;
            }

            // Capture the window
            Bitmap? bitmap = null;

            try
            {
                bitmap = await CaptureWindowBitmapAsync(windowHandle, width, height, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to capture window using PrintWindow, trying alternative method");
                bitmap = await CaptureWindowBitmapAlternativeAsync(windowHandle, width, height, cancellationToken).ConfigureAwait(false);
            }

            if (bitmap == null)
            {
                result.Success = false;
                result.ErrorMessage = "Failed to capture window bitmap";
                return result;
            }

            // Generate filename
            var sanitizedGameName = SanitizeFileName(gameName);
            var timestamp = _config.IncludeTimestampInFilename
                ? $"_{DateTime.UtcNow:yyyyMMdd_HHmmss}"
                : "";
            var extension = _config.ScreenshotFormat == ScreenshotFormat.Png ? ".png" : ".jpg";
            var fileName = $"{sanitizedGameName}{timestamp}{extension}";
            var filePath = Path.Combine(_config.ScreenshotDirectory, fileName);

            // Save the bitmap
            await SaveBitmapAsync(bitmap, filePath, _config.ScreenshotFormat, _config.JpgQuality, cancellationToken).ConfigureAwait(false);

            // Extract dimensions before disposing bitmap
            int bitmapWidth = bitmap.Width;
            int bitmapHeight = bitmap.Height;

            // Dispose bitmap immediately after saving to free resources
            bitmap.Dispose();
            bitmap = null;

            result.Success = true;
            result.FilePath = filePath;
            result.Width = bitmapWidth;
            result.Height = bitmapHeight;
            result.FileSizeBytes = new FileInfo(filePath).Length;

            // Track screenshot
            _screenshots.TryAdd(filePath, 0);

            _logger.LogInformation("Screenshot captured: {FilePath} ({Width}x{Height})", filePath, bitmapWidth, bitmapHeight);

            // Trigger event after bitmap is disposed
            ScreenshotCaptured?.Invoke(this, result);

            return result;
        }
        catch (OperationCanceledException)
        {
            result.Success = false;
            result.ErrorMessage = "Operation cancelled";
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error capturing screenshot");
            result.Success = false;
            result.ErrorMessage = ex.Message;
            return result;
        }
    }

    /// <inheritdoc/>
    public async Task<ScreenshotResult> CaptureFocusedWindowScreenshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var focusedWindow = GetForegroundWindow();
        if (focusedWindow == IntPtr.Zero)
        {
            return new ScreenshotResult
            {
                Success = false,
                ErrorMessage = "No focused window found"
            };
        }

        var title = GetWindowTitleFromHandle(focusedWindow) ?? "FocusedWindow";
        return await CaptureScreenshotAsync(focusedWindow, title, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> GetScreenshotsForGame(int gameId)
    {
        var gameName = _registeredGames.TryGetValue(gameId, out var info) ? info.GameName : null;
        if (string.IsNullOrEmpty(gameName))
        {
            return new List<string>().AsReadOnly();
        }

        var sanitizedGameName = SanitizeFileName(gameName);
        var files = Directory.GetFiles(_config.ScreenshotDirectory, $"{sanitizedGameName}*")
            .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
            .ToList();

        return files.AsReadOnly();
    }

    /// <inheritdoc/>
    public bool DeleteScreenshot(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            _logger.LogWarning("Screenshot file not found: {FilePath}", filePath);
            return false;
        }

        try
        {
            File.Delete(filePath);
            _screenshots.TryRemove(filePath, out _);
            _logger.LogInformation("Screenshot deleted: {FilePath}", filePath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting screenshot: {FilePath}", filePath);
            return false;
        }
    }

    private Task<Bitmap?> CaptureWindowBitmapAsync(IntPtr hWnd, int width, int height, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Bitmap? bitmap = null;

        try
        {
            bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);

            // Get window bounds for screen coordinates
            GetWindowRect(hWnd, out RECT rect);

            using (var graphics = Graphics.FromImage(bitmap))
            {
                // Use CopyFromScreen which properly captures visible window content
                // This works reliably for most windows including hardware-accelerated ones
                graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, new System.Drawing.Size(width, height), CopyPixelOperation.SourceCopy);
            }

            return Task.FromResult<Bitmap?>(bitmap);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error capturing window bitmap using CopyFromScreen");
            bitmap?.Dispose();
            return Task.FromResult<Bitmap?>(null);
        }
    }

    private Task<Bitmap?> CaptureWindowBitmapAlternativeAsync(IntPtr hWnd, int width, int height, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Bitmap? bitmap = null;

        try
        {
            bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);

            using (var graphics = Graphics.FromImage(bitmap))
            {
                // Copy from screen - this captures what's visible on screen
                // Works better for hardware-accelerated windows
                GetWindowRect(hWnd, out RECT rect);
                graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, new System.Drawing.Size(width, height));
            }

            return Task.FromResult<Bitmap?>(bitmap);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error capturing window bitmap using CopyFromScreen");
            bitmap?.Dispose();
            return Task.FromResult<Bitmap?>(null);
        }
    }

    private static async Task SaveBitmapAsync(Bitmap bitmap, string filePath, ScreenshotFormat format, int jpgQuality, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (format == ScreenshotFormat.Png)
        {
            await Task.Run(() => bitmap.Save(filePath, ImageFormat.Png), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var encoderParams = new EncoderParameters(1);
            try
            {
                encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, jpgQuality);
                var jpegEncoder = GetEncoder(ImageFormat.Jpeg);
                await Task.Run(() => bitmap.Save(filePath, jpegEncoder, encoderParams), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                encoderParams.Dispose();
            }
        }
    }

    private static ImageCodecInfo GetEncoder(ImageFormat format)
    {
        var codecs = ImageCodecInfo.GetImageDecoders();
        return codecs.First(c => c.FormatID == format.Guid);
    }

    private static string SanitizeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return "Screenshot";
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(fileName.Where(c => !invalidChars.Contains(c)).ToArray());

        // Limit length
        if (sanitized.Length > 50)
        {
            sanitized = sanitized.Substring(0, 50);
        }

        return sanitized;
    }

    #endregion

    #region Performance Metrics

    /// <inheritdoc/>
    public double? GetCpuUsage(int gameId)
    {
        if (_processStates.TryGetValue(gameId, out var state))
        {
            return state.CpuUsagePercent;
        }
        return null;
    }

    /// <inheritdoc/>
    public long? GetMemoryUsage(int gameId)
    {
        if (_processStates.TryGetValue(gameId, out var state))
        {
            return state.MemoryUsageBytes;
        }
        return null;
    }

    /// <inheritdoc/>
    public double? GetGpuUsage(int gameId)
    {
        if (_processStates.TryGetValue(gameId, out var state))
        {
            return state.GpuUsagePercent;
        }
        return null;
    }

    /// <inheritdoc/>
    public Dictionary<string, object> GetPerformanceMetrics(int gameId)
    {
        var metrics = new Dictionary<string, object>();

        if (_processStates.TryGetValue(gameId, out var state))
        {
            metrics["ProcessId"] = state.ProcessId ?? 0;
            metrics["State"] = state.State.ToString();
            metrics["DurationSeconds"] = state.DurationSeconds;

            if (state.CpuUsagePercent.HasValue)
            {
                metrics["CpuUsagePercent"] = state.CpuUsagePercent.Value;
            }

            if (state.MemoryUsageBytes.HasValue)
            {
                metrics["MemoryUsageBytes"] = state.MemoryUsageBytes.Value;
                metrics["MemoryUsageMB"] = state.MemoryUsageBytes.Value / (1024.0 * 1024.0);
            }

            if (state.GpuUsagePercent.HasValue)
            {
                metrics["GpuUsagePercent"] = state.GpuUsagePercent.Value;
            }

            metrics["WindowState"] = state.WindowState.ToString();
            metrics["IsFocused"] = state.IsFocused;
            metrics["IsHidden"] = state.IsHiddenByBossKey;
        }

        return metrics;
    }

    #endregion

    #region Events

    /// <inheritdoc/>
    public event EventHandler<ProcessStateChangedEventArgs>? ProcessStateChanged;

    /// <inheritdoc/>
    public event EventHandler<ProcessStateChangedEventArgs>? ProcessStarted;

    /// <inheritdoc/>
    public event EventHandler<ProcessStateChangedEventArgs>? ProcessExited;

    /// <inheritdoc/>
    public event EventHandler<BossKeyEventArgs>? BossKeyActivated;

    /// <inheritdoc/>
    public event EventHandler<ScreenshotResult>? ScreenshotCaptured;

    /// <inheritdoc/>
    public event EventHandler<Exception>? MonitoringError;

    #endregion

    #region IDisposable

    /// <summary>
    /// Disposes the process monitor service.
    /// Note: This is a synchronous dispose that does not wait for async tasks.
    /// For proper async cleanup, use StopAsync() before calling Dispose().
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Cancel monitoring task but do NOT wait for it (avoids deadlock)
        _monitoringCts?.Cancel();

        // Unregister boss key
        UnregisterBossKey();

        // Restore hidden windows if any
        if (_areWindowsHidden)
        {
            try
            {
                RestoreAllWindows();
            }
            catch { }
        }

        // Dispose tracked processes directly without waiting (now lists)
        foreach (var kvp in _trackedProcesses)
        {
            foreach (var process in kvp.Value)
            {
                try
                {
                    process?.Dispose();
                }
                catch { }
            }
        }
        _trackedProcesses.Clear();

        // Dispose CPU counters
        foreach (var kvp in _cpuCounters)
        {
            try
            {
                kvp.Value?.Dispose();
            }
            catch { }
        }
        _cpuCounters.Clear();
        _cpuSampledValues.Clear();

        _monitoringCts?.Dispose();
        _monitoringCts = null;

        _logger.LogInformation("ProcessMonitorService disposed");
    }

    #endregion
}