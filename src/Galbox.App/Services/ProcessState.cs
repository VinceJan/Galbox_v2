using System;

namespace Galbox.App.Services;

/// <summary>
/// Enumeration of process states for monitored games.
/// </summary>
public enum ProcessState
{
    /// <summary>
    /// Process is not running.
    /// </summary>
    NotRunning = 0,

    /// <summary>
    /// Process is currently running.
    /// </summary>
    Running = 1,

    /// <summary>
    /// Process has exited.
    /// </summary>
    Exited = 2,

    /// <summary>
    /// Process state is unknown (error during detection).
    /// </summary>
    Unknown = 3
}

/// <summary>
/// Window state information for a monitored process.
/// </summary>
public enum WindowState
{
    /// <summary>
    /// Window is normal (not minimized or maximized).
    /// </summary>
    Normal = 0,

    /// <summary>
    /// Window is minimized.
    /// </summary>
    Minimized = 1,

    /// <summary>
    /// Window is maximized.
    /// </summary>
    Maximized = 2,

    /// <summary>
    /// Window is hidden (by boss key).
    /// </summary>
    Hidden = 3,

    /// <summary>
    /// Window state cannot be determined.
    /// </summary>
    Unknown = 4
}

/// <summary>
/// Represents the state of a monitored game process.
/// </summary>
public class MonitoredProcessState
{
    /// <summary>
    /// Unique identifier for this monitoring session.
    /// </summary>
    public int GameId { get; set; }

    /// <summary>
    /// Name of the game being monitored.
    /// </summary>
    public string? GameName { get; set; }

    /// <summary>
    /// The process name being monitored (executable name without extension).
    /// </summary>
    public string? ProcessName { get; set; }

    /// <summary>
    /// Current state of the process.
    /// </summary>
    public ProcessState State { get; set; }

    /// <summary>
    /// The process ID if running.
    /// </summary>
    public int? ProcessId { get; set; }

    /// <summary>
    /// Main window handle if available.
    /// </summary>
    public IntPtr WindowHandle { get; set; }

    /// <summary>
    /// Window title if available.
    /// </summary>
    public string? WindowTitle { get; set; }

    /// <summary>
    /// Current window state.
    /// </summary>
    public WindowState WindowState { get; set; }

    /// <summary>
    /// When the process started (if running).
    /// </summary>
    public DateTime? StartTime { get; set; }

    /// <summary>
    /// When the process exited (if exited).
    /// </summary>
    public DateTime? ExitTime { get; set; }

    /// <summary>
    /// Total running duration in seconds.
    /// </summary>
    public long DurationSeconds { get; set; }

    /// <summary>
    /// Current CPU usage percentage (0-100).
    /// Requires advanced monitoring to be enabled.
    /// </summary>
    public double? CpuUsagePercent { get; set; }

    /// <summary>
    /// Current memory usage in bytes.
    /// Requires advanced monitoring to be enabled.
    /// </summary>
    public long? MemoryUsageBytes { get; set; }

    /// <summary>
    /// Current GPU usage percentage (0-100), if available.
    /// Requires advanced monitoring to be enabled.
    /// </summary>
    public double? GpuUsagePercent { get; set; }

    /// <summary>
    /// Whether the window is currently focused.
    /// </summary>
    public bool IsFocused { get; set; }

    /// <summary>
    /// Whether the window is hidden by boss key.
    /// </summary>
    public bool IsHiddenByBossKey { get; set; }

    /// <summary>
    /// Last update time for this state.
    /// </summary>
    public DateTime LastUpdateTime { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Gets the formatted duration string.
    /// </summary>
    public string FormattedDuration
    {
        get
        {
            var hours = DurationSeconds / 3600;
            var minutes = (DurationSeconds % 3600) / 60;
            var seconds = DurationSeconds % 60;
            if (hours > 0)
                return $"{hours}h {minutes}m {seconds}s";
            if (minutes > 0)
                return $"{minutes}m {seconds}s";
            return $"{seconds}s";
        }
    }

    /// <summary>
    /// Gets the formatted memory usage string.
    /// </summary>
    public string FormattedMemoryUsage
    {
        get
        {
            if (!MemoryUsageBytes.HasValue)
                return "N/A";

            const long GB = 1024 * 1024 * 1024;
            const long MB = 1024 * 1024;
            const long KB = 1024;

            var bytes = MemoryUsageBytes.Value;
            if (bytes >= GB)
                return $"{bytes / GB:F2} GB";
            if (bytes >= MB)
                return $"{bytes / MB:F1} MB";
            if (bytes >= KB)
                return $"{bytes / KB:F0} KB";
            return $"{bytes} B";
        }
    }
}

/// <summary>
/// Configuration options for process monitoring.
/// </summary>
public class ProcessMonitorConfig
{
    /// <summary>
    /// Enable basic process state monitoring (running/not running/exited).
    /// </summary>
    public bool EnableBasicMonitoring { get; set; } = true;

    /// <summary>
    /// Enable advanced monitoring (CPU/memory/GPU tracking).
    /// </summary>
    public bool EnableAdvancedMonitoring { get; set; } = false;

    /// <summary>
    /// Enable memory behavior monitoring for achievement tracking.
    /// </summary>
    public bool EnableMemoryMonitoring { get; set; } = false;

    /// <summary>
    /// Enable window state tracking (minimized/maximized/focused).
    /// </summary>
    public bool EnableWindowStateTracking { get; set; } = true;

    /// <summary>
    /// Monitoring interval in milliseconds (default: 1000ms).
    /// </summary>
    public int MonitoringIntervalMs { get; set; } = 1000;

    /// <summary>
    /// CPU sampling interval in milliseconds for advanced monitoring.
    /// </summary>
    public int CpuSamplingIntervalMs { get; set; } = 500;

    /// <summary>
    /// Boss key modifier (Alt, Ctrl, Shift, etc.).
    /// </summary>
    public BossKeyModifiers BossKeyModifiers { get; set; } = BossKeyModifiers.Alt | BossKeyModifiers.Shift;

    /// <summary>
    /// Boss key virtual key code (default: H).
    /// </summary>
    public uint BossKeyVirtualKey { get; set; } = 0x48; // 'H'

    /// <summary>
    /// Enable boss key feature.
    /// </summary>
    public bool EnableBossKey { get; set; } = true;

    /// <summary>
    /// Screenshot save directory.
    /// </summary>
    public string ScreenshotDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Screenshot format (PNG or JPG).
    /// </summary>
    public ScreenshotFormat ScreenshotFormat { get; set; } = ScreenshotFormat.Png;

    /// <summary>
    /// JPG quality (1-100), only used when format is JPG.
    /// </summary>
    public int JpgQuality { get; set; } = 90;

    /// <summary>
    /// Include timestamp in screenshot filename.
    /// </summary>
    public bool IncludeTimestampInFilename { get; set; } = true;

    /// <summary>
    /// Auto-save screenshot on game exit.
    /// </summary>
    public bool AutoScreenshotOnExit { get; set; } = false;

    /// <summary>
    /// Minimize to system tray when using boss key.
    /// </summary>
    public bool MinimizeToTrayOnBossKey { get; set; } = true;

    /// <summary>
    /// Show notification when boss key is activated.
    /// </summary>
    public bool ShowBossKeyNotification { get; set; } = true;
}

/// <summary>
/// Boss key modifiers (keyboard combination).
/// </summary>
[Flags]
public enum BossKeyModifiers
{
    None = 0,
    Alt = 1,
    Ctrl = 2,
    Shift = 4,
    Win = 8
}

/// <summary>
/// Screenshot format options.
/// </summary>
public enum ScreenshotFormat
{
    /// <summary>
    /// PNG format (lossless, larger file size).
    /// </summary>
    Png = 0,

    /// <summary>
    /// JPG format (lossy, smaller file size).
    /// </summary>
    Jpg = 1
}

/// <summary>
/// Result of a screenshot capture operation.
/// </summary>
public class ScreenshotResult
{
    /// <summary>
    /// Whether the screenshot was captured successfully.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Path to the saved screenshot file.
    /// </summary>
    public string? FilePath { get; set; }

    /// <summary>
    /// Width of the captured image.
    /// </summary>
    public int Width { get; set; }

    /// <summary>
    /// Height of the captured image.
    /// </summary>
    public int Height { get; set; }

    /// <summary>
    /// File size in bytes.
    /// </summary>
    public long FileSizeBytes { get; set; }

    /// <summary>
    /// Timestamp when screenshot was taken.
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Error message if capture failed.
    /// </summary>
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Event arguments for process state change events.
/// </summary>
public class ProcessStateChangedEventArgs : EventArgs
{
    /// <summary>
    /// Game ID for the process.
    /// </summary>
    public int GameId { get; set; }

    /// <summary>
    /// Game name.
    /// </summary>
    public string? GameName { get; set; }

    /// <summary>
    /// Previous state.
    /// </summary>
    public ProcessState PreviousState { get; set; }

    /// <summary>
    /// New state.
    /// </summary>
    public ProcessState NewState { get; set; }

    /// <summary>
    /// Process ID (if running or just exited).
    /// </summary>
    public int? ProcessId { get; set; }

    /// <summary>
    /// Window handle (if available).
    /// </summary>
    public IntPtr WindowHandle { get; set; }

    /// <summary>
    /// Timestamp of the state change.
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Duration of the session (for exited events).
    /// </summary>
    public long? SessionDurationSeconds { get; set; }

    /// <summary>
    /// Whether this was a normal exit or crash.
    /// </summary>
    public bool WasCrash { get; set; }
}

/// <summary>
/// Event arguments for boss key events.
/// </summary>
public class BossKeyEventArgs : EventArgs
{
    /// <summary>
    /// Whether windows are now hidden.
    /// </summary>
    public bool IsHidden { get; set; }

    /// <summary>
    /// Number of windows affected.
    /// </summary>
    public int WindowsAffected { get; set; }

    /// <summary>
    /// Timestamp of the event.
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Information about a monitored game for registration.
/// </summary>
public class MonitoredGameInfo
{
    /// <summary>
    /// Game ID from database.
    /// </summary>
    public int GameId { get; set; }

    /// <summary>
    /// Display name of the game.
    /// </summary>
    public string GameName { get; set; } = string.Empty;

    /// <summary>
    /// Process name to monitor (executable name without extension).
    /// </summary>
    public string ProcessName { get; set; } = string.Empty;

    /// <summary>
    /// Full path to the executable.
    /// </summary>
    public string ExecutablePath { get; set; } = string.Empty;

    /// <summary>
    /// Window title pattern for matching (optional).
    /// </summary>
    public string? WindowTitlePattern { get; set; }
}