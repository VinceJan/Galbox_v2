using System.Runtime.InteropServices;
using System.Text;

namespace Galbox.App.Services;

/// <summary>
/// Startup diagnostics for the WinUI host.
///
/// The audit found the worst possible failure mode: the startup path could throw on a
/// thread-pool thread (any <c>ConfigureAwait(false)</c> before <c>new MainWindow()</c> moves the
/// continuation off the UI thread, and WinUI then throws <c>RPC_E_WRONG_THREAD</c>), the
/// exception was swallowed by a catch that only logged to <c>AddDebug</c> output, and the user
/// was left with a live process, a live message loop and <b>no window and no error</b>.
///
/// This class exists so that can never happen silently again:
///   * every startup step is appended to
///     <c>%LocalAppData%\Galbox\logs\startup-YYYYMMDD.log</c> - or, when <c>GALBOX_DATA_DIR</c> is
///     set, to <c>{that folder}\logs\startup-YYYYMMDD.log</c> (see <see cref="GalboxDataDirectory"/>)
///   * every line carries the process id, so lines written by two instances that share one log file
///     can no longer be mistaken for each other
///   * a failure that happens before the main window exists also raises a modal message box,
///     because at that point there is no UI left to report through
/// </summary>
public static class StartupDiagnostics
{
    /// <summary>
    /// Log marker written when the startup sequence finished normally. Exposed as a constant so
    /// the acceptance harness (A9) can assert on the same string the application writes.
    /// </summary>
    public const string StartupCompletedMarker = "OnLaunched: startup sequence completed";

    /// <summary>
    /// Log marker written when the startup sequence failed. Exposed for the same reason.
    /// </summary>
    public const string StartupFailureMarker = "EXCEPTION in OnLaunched";

    /// <summary>
    /// Caption of the modal failure dialog. A window with this title is a failure report, not a
    /// successfully started application.
    /// </summary>
    public const string StartupFailureCaption = "Galbox 启动失败";

    private const uint MB_OK = 0x00000000;
    private const uint MB_ICONERROR = 0x00000010;
    private const uint MB_TOPMOST = 0x00040000;
    private const uint MB_SETFOREGROUND = 0x00010000;

    private static readonly object SyncRoot = new();

    /// <summary>
    /// Folder that holds the startup logs: <c>%LocalAppData%\Galbox\logs</c>, or
    /// <c>{GALBOX_DATA_DIR}\logs</c> when the data folder was overridden.
    /// </summary>
    /// <remarks>
    /// Following the data folder is what keeps a second instance out of this instance's log: two
    /// Galbox processes that were started against different <c>GALBOX_DATA_DIR</c> values write to
    /// two different files, so a reader can no longer pick up the other process's lines.
    /// </remarks>
    public static string LogDirectory => ResolveWritableLogDirectory();

    /// <summary>
    /// Log file for the current day: <c>startup-YYYYMMDD.log</c>.
    /// </summary>
    public static string CurrentLogPath
        => Path.Combine(LogDirectory, $"{GalboxDataDirectory.StartupLogFilePrefix}{DateTime.Now:yyyyMMdd}.log");

    /// <summary>
    /// Process id of the instance writing, prefixed to every line as <c>[pid:NNNN]</c>.
    /// </summary>
    /// <remarks>
    /// The day-stamped log file is shared by every Galbox instance on the machine that uses the same
    /// data folder, so a line's provenance is not obvious from the file alone. With the pid in the
    /// line, "two startup sequences begin" is immediately readable as two different processes rather
    /// than one process that started twice.
    /// </remarks>
    public static int ProcessId => Environment.ProcessId;

    /// <summary>
    /// Appends one line to the startup log. Never throws: diagnostics must not be
    /// able to break the startup path they are supposed to observe.
    /// </summary>
    /// <remarks>
    /// <see cref="TryAppend"/> adds the shared <c>timestamp [pid] [tid]</c> prefix, so every line of
    /// the log - including the multi-line exception blocks - is attributed to one process.
    /// </remarks>
    public static void Log(string message)
    {
        TryAppend(message);
    }

    /// <summary>
    /// Appends a formatted exception (with inner exceptions and stack traces) to the startup log.
    /// </summary>
    public static void LogException(string context, Exception exception)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"EXCEPTION in {context}: {exception.GetType().FullName}: {exception.Message}");

        var current = exception;
        var depth = 0;
        while (current is not null)
        {
            builder.AppendLine($"--- level {depth} : {current.GetType().FullName} ---");
            builder.AppendLine(current.StackTrace ?? "(no stack trace)");

            if (current is AggregateException aggregate)
            {
                var index = 0;
                foreach (var inner in aggregate.InnerExceptions)
                {
                    builder.AppendLine($"--- AggregateException[{index++}] : {inner.GetType().FullName}: {inner.Message} ---");
                    builder.AppendLine(inner.StackTrace ?? "(no stack trace)");
                }
            }

            current = current.InnerException;
            depth++;
        }

        TryAppend(builder.ToString().TrimEnd());
    }

    /// <summary>
    /// Reports a startup failure. Always writes the log; raises a modal message box when the
    /// failure happened before a window exists, because then nothing else is visible to the user.
    /// </summary>
    /// <param name="exception">The failure.</param>
    /// <param name="windowCreated">True when the main window was already created and activated.</param>
    public static void ReportStartupFailure(Exception exception, bool windowCreated)
    {
        LogException(windowCreated ? "OnLaunched (after the main window was created)" : "OnLaunched (before the main window existed)", exception);

        var summary = windowCreated
            ? "Galbox 启动过程中发生错误；窗口已打开，详情见日志。"
            : "Galbox 启动失败，主窗口无法创建。\n\n详情见日志：";

        TryShowMessageBox(
            StartupFailureCaption,
            $"{summary}\n{CurrentLogPath}\n\n{exception.GetType().Name}: {exception.Message}");
    }

    /// <summary>
    /// Shows a modal Win32 message box. Used only for fatal startup failures: it works before any
    /// WinUI object exists and on whatever thread the failure happened on.
    /// </summary>
    public static void TryShowMessageBox(string caption, string text)
    {
        try
        {
            Log($"Showing startup failure message box: {caption}");
            MessageBoxW(IntPtr.Zero, text, caption, MB_OK | MB_ICONERROR | MB_TOPMOST | MB_SETFOREGROUND);
        }
        catch (Exception ex)
        {
            // A machine without a desktop session (or a locked-down host) cannot show a dialog;
            // the log written above is then the only - and still non-silent - record.
            Log($"Could not show the startup failure message box: {ex.Message}");
        }
    }

    /// <summary>
    /// The log folder this process will write to: the one belonging to its data folder, falling
    /// back to <c>%LocalAppData%\Galbox\logs</c> when that folder cannot be created.
    /// </summary>
    /// <remarks>
    /// The fallback exists so that a broken <c>GALBOX_DATA_DIR</c> cannot silence the diagnostics of
    /// the very startup that is failing: before this change the log always went to the default
    /// folder, and losing it would be a regression in exactly the situation where it is worth most.
    /// </remarks>
    private static string ResolveWritableLogDirectory()
    {
        var preferred = GalboxDataDirectory.ResolveLogDirectory();

        try
        {
            Directory.CreateDirectory(preferred);
            return preferred;
        }
        catch (Exception)
        {
            // fall through to the historical location
        }

        try
        {
            var fallback = GalboxDataDirectory.ResolveLogDirectory(null);
            Directory.CreateDirectory(fallback);
            return fallback;
        }
        catch (Exception)
        {
            return preferred;
        }
    }

    private static void TryAppend(string text)
    {
        try
        {
            lock (SyncRoot)
            {
                // Every line carries the process id, so a reader of a shared log file can tell two
                // instances apart even when they append to the same second.
                var stamp = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [pid:{Environment.ProcessId}] "
                          + $"[tid:{Environment.CurrentManagedThreadId}] ";

                var builder = new StringBuilder();
                foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
                {
                    builder.Append(stamp).Append(line).Append(Environment.NewLine);
                }

                var logPath = CurrentLogPath;
                File.AppendAllText(logPath, builder.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // Never let logging break startup.
        }
    }

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
