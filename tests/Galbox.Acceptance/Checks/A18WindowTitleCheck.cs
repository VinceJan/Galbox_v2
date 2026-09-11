using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Galbox.Acceptance.Checks;

/// <summary>
/// A18 - The window caption is the product name, not the host default (W16).
///
/// The defect: <c>MainWindow.xaml</c> draws its own title bar with a <c>TextBlock</c>, but never set
/// <c>Window.Title</c>. The in-window title bar looked right while the taskbar, Alt-Tab and the
/// window switcher all read "WinUI Desktop" - the host default - which is the first thing a user
/// sees about the product.
///
/// The check starts the shipping executable and reads the real caption from the window handle, so
/// the verdict is the measured value the shell displays, not a source-code claim.
/// </summary>
public sealed class A18WindowTitleCheck : IAcceptanceCheck
{
    private static readonly TimeSpan WindowTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <inheritdoc />
    public string Id => "A18";

    /// <inheritdoc />
    public string Title => "The application window title is the product name, not \"WinUI Desktop\"";

    /// <inheritdoc />
    public async Task<CheckResult> RunAsync(AcceptanceContext context, CancellationToken cancellationToken)
    {
        var expected = "the window the shipping executable creates has a MainWindowTitle that starts with \"Galbox\" "
                     + "and is not the host default \"WinUI Desktop\"";

        var details = new List<string>();

        var repoRoot = RepoLocator.FindRepoRoot();
        if (repoRoot is null)
        {
            return CheckResult.Fail(Id, Title, expected, "repository root not found")
                .With($"FAIL REASON: could not find {RepoLocator.SolutionMarker} above '{AppContext.BaseDirectory}'.");
        }

        var applicationPath = FindApplicationExecutable(repoRoot, out var buildTimestampUtc);
        if (applicationPath is null)
        {
            return CheckResult.Fail(Id, Title, expected, "Galbox.App.exe not found")
                .With($"FAIL REASON: no Galbox.App.exe under '{Path.Combine(repoRoot, "src", "Galbox.App", "bin")}'.");
        }

        details.Add($"Repository root      : {repoRoot}");
        details.Add($"Application          : {applicationPath}");
        details.Add($"Application built    : {buildTimestampUtc:yyyy-MM-dd HH:mm:ss}Z (newest file in the output folder)");

        var newestSourceUtc = FindNewestSourceTimestamp(repoRoot);
        details.Add($"Newest source file   : {newestSourceUtc:yyyy-MM-dd HH:mm:ss}Z");
        if (newestSourceUtc > buildTimestampUtc)
        {
            return CheckResult.Fail(Id, Title, expected,
                    $"binary built {buildTimestampUtc:u} but sources changed {newestSourceUtc:u}")
                .With(details.ToArray())
                .With("FAIL REASON: the application binary is older than the newest source file; rebuild first.");
        }

        Process? process = null;
        var windowHandle = IntPtr.Zero;
        var windowTitle = string.Empty;
        var elapsed = TimeSpan.Zero;
        var attempts = 0;
        const int maxAttempts = 3;

        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Galbox", "logs", $"startup-{DateTime.Now:yyyyMMdd}.log");

        try
        {
            // A WinUI unpackaged start occasionally loses the race with the previous instance being
            // torn down (the process exits before any window exists, with nothing in the startup
            // log). That must not be reported as "the title is wrong", so the launch is retried a
            // bounded number of times; the measured title of the successful attempt is the verdict.
            while (attempts < maxAttempts)
            {
                attempts++;

                process?.Dispose();
                process = Process.Start(new ProcessStartInfo
                {
                    FileName = applicationPath,
                    WorkingDirectory = Path.GetDirectoryName(applicationPath)!,
                    UseShellExecute = false
                });

                if (process is null)
                {
                    return CheckResult.Fail(Id, Title, expected, "Process.Start returned null").With(details.ToArray());
                }

                details.Add($"Launched             : pid {process.Id} (attempt {attempts}/{maxAttempts})");
                var stopwatch = Stopwatch.StartNew();
                var exitedEarly = false;

                while (stopwatch.Elapsed < WindowTimeout)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);

                    if (process.HasExited)
                    {
                        exitedEarly = true;
                        break;
                    }

                    process.Refresh();
                    windowHandle = process.MainWindowHandle;
                    if (windowHandle != IntPtr.Zero)
                    {
                        break;
                    }
                }

                elapsed = stopwatch.Elapsed;

                if (windowHandle == IntPtr.Zero)
                {
                    process.Refresh();
                    windowHandle = process.MainWindowHandle;
                }

                if (windowHandle != IntPtr.Zero)
                {
                    windowTitle = ReadWindowTitle(windowHandle);
                    if (windowTitle.Length > 0)
                    {
                        break;
                    }
                }

                details.Add($"  attempt {attempts}: no titled window after {elapsed.TotalMilliseconds:F0} ms "
                          + $"(exitedEarly={exitedEarly}, handle=0x{windowHandle.ToInt64():X})");

                if (!exitedEarly && windowHandle == IntPtr.Zero)
                {
                    // A window may simply be slow to appear; do not burn a retry on it.
                    break;
                }

                // Give the operating system a moment to finish tearing the previous instance down.
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                if (process is not null && !process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(10_000);
                }
            }
            catch (Exception ex)
            {
                details.Add($"Cleanup warning: could not terminate pid {process?.Id}: {ex.Message}");
            }
        }

        var processTitle = string.Empty;
        try
        {
            processTitle = process?.MainWindowTitle ?? string.Empty;
        }
        catch (Exception ex)
        {
            details.Add($"Process.MainWindowTitle could not be read after the kill: {ex.Message}");
        }

        details.Add($"MainWindowHandle     : 0x{windowHandle.ToInt64():X}");
        details.Add($"Window title (Win32) : \"{windowTitle}\"");
        details.Add($"Elapsed              : {elapsed.TotalMilliseconds:F0} ms (launch attempts: {attempts})");

        if (windowHandle == IntPtr.Zero)
        {
            details.Add("--- tail of the application startup log (why no window appeared) ---");
            foreach (var line in ReadLogTail(logPath, 15))
            {
                details.Add($"    {line}");
            }
        }

        // The title is checked through the Win32 handle, because that is the string the shell shows.
        // "WinUI Desktop" is the value the host assigns when the application never sets one.
        var isHostDefault = windowTitle.Equals("WinUI Desktop", StringComparison.OrdinalIgnoreCase);
        var startsWithProductName = windowTitle.StartsWith("Galbox", StringComparison.Ordinal);
        var pass = windowHandle != IntPtr.Zero && startsWithProductName && !isHostDefault;

        details.Add(string.Empty);
        details.Add("--- interpretation ---");
        details.Add($"  window was created          : {windowHandle != IntPtr.Zero}");
        details.Add($"  title starts with \"Galbox\"  : {startsWithProductName}");
        details.Add($"  title is the host default   : {isHostDefault}");
        if (!pass && windowHandle != IntPtr.Zero)
        {
            details.Add("  VERDICT: the taskbar / Alt-Tab entry shows the host default instead of the product name");
            details.Add("           - the W16 missing Window.Title defect.");
        }

        var actual = $"title=\"{windowTitle}\", startsWithGalbox={startsWithProductName}, hostDefault={isHostDefault}";

        var result = pass
            ? CheckResult.Pass(Id, Title, expected, actual)
            : CheckResult.Fail(Id, Title, expected, actual);

        result.With(details.ToArray());

        if (processTitle.Length > 0)
        {
            result.With($"note: Process.MainWindowTitle measured right after the cleanup kill: \"{processTitle}\"");
        }

        return result;
    }

    private static string? FindApplicationExecutable(string repoRoot, out DateTime buildTimestampUtc)
    {
        buildTimestampUtc = DateTime.MinValue;

        var binRoot = Path.Combine(repoRoot, "src", "Galbox.App", "bin");
        if (!Directory.Exists(binRoot))
        {
            return null;
        }

        var candidates = Directory
            .EnumerateFiles(binRoot, "Galbox.App.exe", SearchOption.AllDirectories)
            .Select(path =>
            {
                var folder = Path.GetDirectoryName(path)!;
                var newestInFolder = Directory
                    .EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
                    .Select(File.GetLastWriteTimeUtc)
                    .DefaultIfEmpty(DateTime.MinValue)
                    .Max();
                return (Path: path, BuildUtc: newestInFolder);
            })
            .OrderByDescending(candidate => candidate.BuildUtc)
            .ToList();

        if (candidates.Count == 0)
        {
            return null;
        }

        buildTimestampUtc = candidates[0].BuildUtc;
        return candidates[0].Path;
    }

    private static DateTime FindNewestSourceTimestamp(string repoRoot)
    {
        var projectFolder = RepoLocator.AppProject(repoRoot);
        if (!Directory.Exists(projectFolder))
        {
            return DateTime.MinValue;
        }

        return Directory
            .EnumerateFiles(projectFolder, "*", SearchOption.AllDirectories)
            .Where(path => (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                         || path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
                        && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                        && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Select(File.GetLastWriteTimeUtc)
            .DefaultIfEmpty(DateTime.MinValue)
            .Max();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextLengthW(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    private static string ReadWindowTitle(IntPtr hWnd)
    {
        try
        {
            var length = GetWindowTextLengthW(hWnd);
            if (length <= 0)
            {
                return string.Empty;
            }

            var buffer = new StringBuilder(length + 1);
            GetWindowTextW(hWnd, buffer, buffer.Capacity);
            return buffer.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>Last lines of the application's startup log, used to explain a missing window.</summary>
    private static List<string> ReadLogTail(string logPath, int lineCount)
    {
        try
        {
            return File.Exists(logPath)
                ? File.ReadAllLines(logPath).TakeLast(lineCount).ToList()
                : new List<string> { $"(no startup log at {logPath})" };
        }
        catch (Exception ex)
        {
            return new List<string> { $"(could not read the startup log: {ex.Message})" };
        }
    }
}
