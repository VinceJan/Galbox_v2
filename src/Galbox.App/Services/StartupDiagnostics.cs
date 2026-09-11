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
///   * every startup step is appended to <c>%LocalAppData%\Galbox\logs\startup-YYYYMMDD.log</c>
///   * a failure that happens before the main window exists also raises a modal message box,
///     because at that point there is no UI left to report through
/// </summary>
public static class StartupDiagnostics
{
    private const uint MB_OK = 0x00000000;
    private const uint MB_ICONERROR = 0x00000010;
    private const uint MB_TOPMOST = 0x00040000;
    private const uint MB_SETFOREGROUND = 0x00010000;

    private static readonly object SyncRoot = new();

    /// <summary>
    /// Folder that holds the startup logs: <c>%LocalAppData%\Galbox\logs</c>.
    /// </summary>
    public static string LogDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Galbox",
        "logs");

    /// <summary>
    /// Log file for the current day: <c>startup-YYYYMMDD.log</c>.
    /// </summary>
    public static string CurrentLogPath => Path.Combine(LogDirectory, $"startup-{DateTime.Now:yyyyMMdd}.log");

    /// <summary>
    /// Appends one timestamped line to the startup log. Never throws: diagnostics must not be
    /// able to break the startup path they are supposed to observe.
    /// </summary>
    public static void Log(string message)
    {
        TryAppend($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [tid:{Environment.CurrentManagedThreadId}] {message}");
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
            "Galbox 启动失败",
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

    private static void TryAppend(string text)
    {
        try
        {
            lock (SyncRoot)
            {
                Directory.CreateDirectory(LogDirectory);
                File.AppendAllText(CurrentLogPath, text + Environment.NewLine, Encoding.UTF8);
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
