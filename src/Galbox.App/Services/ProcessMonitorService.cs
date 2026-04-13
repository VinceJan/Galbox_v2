using System.Diagnostics;

namespace Galbox.App.Services;

/// <summary>
/// Service for monitoring game processes.
/// </summary>
public class ProcessMonitorService
{
    private Process? _currentProcess;
    private DateTime _startTime;
    private bool _isMonitoring;

    /// <summary>
    /// Start monitoring a game process.
    /// </summary>
    public void StartMonitoring(Process gameProcess)
    {
        _currentProcess = gameProcess;
        _startTime = DateTime.Now;
        _isMonitoring = true;
    }

    /// <summary>
    /// Check if the monitored game is still running.
    /// </summary>
    public bool IsGameRunning()
    {
        if (_currentProcess == null)
            return false;

        return !_currentProcess.HasExited;
    }

    /// <summary>
    /// Get the elapsed play time since monitoring started.
    /// </summary>
    public TimeSpan GetPlayTime()
    {
        if (!_isMonitoring || _currentProcess == null)
            return TimeSpan.Zero;

        if (_currentProcess.HasExited)
            return DateTime.Now - _startTime;

        return TimeSpan.Zero;
    }

    /// <summary>
    /// Stop monitoring and return final play time.
    /// </summary>
    public TimeSpan StopMonitoring()
    {
        _isMonitoring = false;
        return GetPlayTime();
    }

    /// <summary>
    /// Check if a game process is running (by executable path).
    /// </summary>
    public bool IsProcessRunning(string executablePath)
    {
        var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executablePath));
        return processes.Length > 0;
    }

    /// <summary>
    /// Launch a game and start monitoring.
    /// </summary>
    public Process? LaunchGame(string executablePath)
    {
        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executablePath,
                    WorkingDirectory = Path.GetDirectoryName(executablePath)
                }
            };

            process.Start();
            StartMonitoring(process);
            return process;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Capture screenshot of the game window.
    /// </summary>
    public void CaptureScreenshot(int gameId)
    {
        // TODO: Implement window capture
    }
}