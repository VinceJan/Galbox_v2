using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Galbox.Acceptance.Checks;

/// <summary>
/// A9 - The real GUI application opens a window when the scraping cache is populated.
///
/// This is the only check in the suite that starts the shipping executable, and it exists because
/// the worst defect found so far was invisible to every other check: with a non-empty
/// <c>%LocalAppData%\Galbox\ScrapingCache</c>, <c>ScrapingCacheService.InitializeAsync()</c>
/// genuinely yields, and the old startup path resumed on a thread-pool thread where
/// <c>new MainWindow()</c> threw <c>COMException 0x8001010E (RPC_E_WRONG_THREAD)</c>. The catch
/// swallowed it, so the process stayed alive with a working message loop, no window and no error.
/// Every other check calls a service headlessly and therefore cannot see it.
///
/// Precondition (prepared here, never taken from the user's data):
///   * the cache folder must contain at least one <c>search_*.json</c> file that the service will
///     read. Existing files are left exactly as they are; only when the folder holds none does
///     this check write its own probe file, and it removes that probe file again afterwards.
///
/// Verdict: within the timeout the process must still be alive AND own a visible top-level window
/// (<see cref="Process.MainWindowHandle"/> non-zero, cross-checked with an EnumWindows scan so a
/// Win32-API quirk can never turn a real window into a false failure). On failure the newest
/// <c>%LocalAppData%\Galbox\logs\startup-*.log</c> lines are attached, which is also what proves
/// the startup-failure reporting is no longer silent.
/// </summary>
public sealed class A9StartupWithCacheCheck : IAcceptanceCheck
{
    private const uint GW_OWNER = 4;
    private static readonly TimeSpan WindowTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <inheritdoc />
    public string Id => "A9";

    /// <inheritdoc />
    public string Title => "The GUI application opens a window while scrape-cache data is present";

    /// <inheritdoc />
    public async Task<CheckResult> RunAsync(AcceptanceContext context, CancellationToken cancellationToken)
    {
        var expected = "with at least one search_*.json cache file present, Galbox.App.exe is still "
                     + $"alive and owns a visible top-level window within {WindowTimeout.TotalSeconds:F0}s";

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
                .With($"FAIL REASON: no Galbox.App.exe under '{Path.Combine(repoRoot, "src", "Galbox.App", "bin")}'. "
                    + "Build the solution first (dotnet build Galbox.sln -c Debug).");
        }

        var newestSourceUtc = FindNewestSourceTimestamp(repoRoot);

        details.Add($"Repository root      : {repoRoot}");
        details.Add($"Application          : {applicationPath}");
        details.Add($"Application built    : {buildTimestampUtc:yyyy-MM-dd HH:mm:ss}Z (newest file in the output folder)");
        details.Add($"Newest source file   : {newestSourceUtc:yyyy-MM-dd HH:mm:ss}Z (src/Galbox.App, obj/bin excluded)");

        if (newestSourceUtc > buildTimestampUtc)
        {
            details.Add("FAIL REASON: the application binary is OLDER than the newest source file, so it cannot "
                      + "represent the current sources. Rebuild before accepting this result "
                      + "(dotnet build Galbox.sln -c Debug).");
            return CheckResult.Fail(Id, Title, expected,
                    $"binary built {buildTimestampUtc:u} but sources changed {newestSourceUtc:u}")
                .With(details.ToArray());
        }

        // ------------------------------------------------------------- precondition: cache file
        var cacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Galbox",
            "ScrapingCache");
        Directory.CreateDirectory(cacheDirectory);

        var existingCacheFiles = Directory.GetFiles(cacheDirectory, "search_*.json");
        string? probeFile = null;

        if (existingCacheFiles.Length == 0)
        {
            probeFile = Path.Combine(cacheDirectory, "search_a9-startup-probe.json");
            await File.WriteAllTextAsync(probeFile, BuildProbeCacheEntry(), cancellationToken).ConfigureAwait(false);
            details.Add("Cache precondition  : folder was EMPTY, wrote a probe entry "
                      + $"(removed again at the end of this check): {Path.GetFileName(probeFile)}");
        }
        else
        {
            details.Add($"Cache precondition  : {existingCacheFiles.Length} existing search_*.json file(s), left untouched:");
            foreach (var file in existingCacheFiles)
            {
                details.Add($"    {Path.GetFileName(file)} ({new FileInfo(file).Length} bytes)");
            }
        }

        details.Add($"Cache directory      : {cacheDirectory}");

        // The startup log the application writes; the A9 failure report is built from its tail.
        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Galbox", "logs", $"startup-{DateTime.Now:yyyyMMdd}.log");
        var logLengthBefore = File.Exists(logPath) ? new FileInfo(logPath).Length : 0;
        details.Add($"Startup log          : {logPath} (exists: {File.Exists(logPath)}, bytes before launch: {logLengthBefore})");

        Process? process = null;
        var windowHandle = IntPtr.Zero;
        int visibleTopLevelWindows = 0;
        var elapsed = TimeSpan.Zero;
        var exitBeforeWindow = false;
        var aliveBeforeCleanup = false;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = applicationPath,
                WorkingDirectory = Path.GetDirectoryName(applicationPath)!,
                UseShellExecute = false
            };

            process = Process.Start(startInfo);
            if (process is null)
            {
                return CheckResult.Fail(Id, Title, expected, "Process.Start returned null")
                    .With(details.ToArray());
            }

            details.Add($"Launched             : pid {process.Id}");
            var stopwatch = Stopwatch.StartNew();

            while (stopwatch.Elapsed < WindowTimeout)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);

                if (process.HasExited)
                {
                    exitBeforeWindow = true;
                    break;
                }

                process.Refresh();
                windowHandle = process.MainWindowHandle;
                visibleTopLevelWindows = CountVisibleTopLevelWindows(process.Id);

                if (windowHandle != IntPtr.Zero || visibleTopLevelWindows > 0)
                {
                    break;
                }
            }

            elapsed = stopwatch.Elapsed;

            if (windowHandle == IntPtr.Zero && visibleTopLevelWindows == 0 && !exitBeforeWindow)
            {
                // Give a slow cold start one more measured chance and report the final numbers.
                process.Refresh();
                windowHandle = process.MainWindowHandle;
                visibleTopLevelWindows = CountVisibleTopLevelWindows(process.Id);
            }

            // Record liveness BEFORE the cleanup below terminates the process, otherwise the
            // verdict would always read "not alive" no matter how healthy the startup was.
            process.Refresh();
            aliveBeforeCleanup = !process.HasExited;
        }
        finally
        {
            // Cleanup must happen whatever the verdict is.
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

            if (probeFile is not null)
            {
                try
                {
                    File.Delete(probeFile);
                    details.Add($"Probe cache entry removed: {Path.GetFileName(probeFile)}");
                }
                catch (Exception ex)
                {
                    details.Add($"Cleanup warning: could not remove probe cache entry: {ex.Message}");
                }
            }
        }

        var processAlive = aliveBeforeCleanup;
        var hasWindow = windowHandle != IntPtr.Zero || visibleTopLevelWindows > 0;

        details.Add($"Alive when measured  : {processAlive} (measured before the cleanup kill)");
        details.Add($"MainWindowHandle     : 0x{windowHandle.ToInt64():X} ({(windowHandle == IntPtr.Zero ? "zero" : "non-zero")})");
        details.Add($"Visible top-level windows owned by the process: {visibleTopLevelWindows}");
        details.Add($"Elapsed              : {elapsed.TotalMilliseconds:F0} ms (timeout {WindowTimeout.TotalSeconds:F0}s)");
        details.Add($"Exited before window : {exitBeforeWindow}");
        details.Add($"Cache files now      : {Directory.GetFiles(cacheDirectory, "search_*.json").Length} search_*.json");

        var logTail = ReadNewLogLines(logPath, logLengthBefore);
        if (logTail.Count > 0)
        {
            details.Add($"--- startup log lines written by this launch ({logTail.Count}) ---");
            details.AddRange(logTail.Select(line => $"    {line}"));
        }
        else
        {
            details.Add("--- startup log lines written by this launch: NONE ---");
        }

        if (processAlive && hasWindow)
        {
            return CheckResult.Pass(
                    Id, Title, expected,
                    $"window present: MainWindowHandle=0x{windowHandle.ToInt64():X}, "
                  + $"visibleTopLevelWindows={visibleTopLevelWindows}, pid alive after {elapsed.TotalMilliseconds:F0} ms")
                .With(details.ToArray());
        }

        var reason = exitBeforeWindow
            ? "the application exited before any window appeared"
            : "the process stayed alive but never created a visible top-level window (the classic "
            + "'double-click does nothing' failure: the startup exception was swallowed)";

        details.Add($"FAIL REASON: {reason}");
        return CheckResult.Fail(Id, Title, expected,
                $"MainWindowHandle=0x{windowHandle.ToInt64():X}, visibleTopLevelWindows={visibleTopLevelWindows}, "
              + $"alive={processAlive}, exitedEarly={exitBeforeWindow}")
            .With(details.ToArray());
    }

    /// <summary>
    /// Finds the shipping executable whose output folder was built last, so a stale configuration
    /// folder cannot be picked by accident, and reports that folder's build timestamp.
    ///
    /// The timestamp is the newest file in the output folder rather than the timestamp of
    /// <c>Galbox.App.exe</c> itself: for an unpackaged WinUI app the apphost executable is not
    /// rewritten when only managed code changes, so its own timestamp would look stale forever.
    /// </summary>
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
                    .Select(file => File.GetLastWriteTimeUtc(file))
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

    /// <summary>
    /// Newest write time of any compiled/parsed source file of the application project
    /// (<c>*.cs</c>, <c>*.xaml</c>), ignoring build output.
    /// </summary>
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

    /// <summary>
    /// Builds a minimal but valid <c>ScrapingCacheEntry</c> JSON, in the shape
    /// <c>ScrapingCacheService.LoadCacheFromFilesAsync</c> expects (it deserializes
    /// case-insensitively and skips entries whose <c>ExpiresAt</c> is in the past).
    /// </summary>
    private static string BuildProbeCacheEntry()
    {
        var entry = new
        {
            GameName = "A9 startup probe",
            Result = new
            {
                Query = "A9 startup probe",
                SourceResults = new Dictionary<string, object>(),
                Errors = Array.Empty<string>()
            },
            CachedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            BestMatchSource = (string?)null,
            BestMatchScore = (double?)null
        };

        return JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Reads the lines the application appended to its startup log, if any.</summary>
    private static List<string> ReadNewLogLines(string logPath, long lengthBefore)
    {
        var lines = new List<string>();

        try
        {
            if (!File.Exists(logPath))
            {
                return lines;
            }

            var text = File.ReadAllText(logPath);
            if (text.Length <= lengthBefore)
            {
                return lines;
            }

            var appended = text.Substring((int)Math.Min(lengthBefore, text.Length));
            lines.AddRange(appended
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.TrimEnd('\r'))
                .TakeLast(40));
        }
        catch (Exception ex)
        {
            lines.Add($"(could not read the startup log: {ex.Message})");
        }

        return lines;
    }

    /// <summary>
    /// Counts visible, unowned top-level windows belonging to a process. Independent of
    /// <see cref="Process.MainWindowHandle"/> so the two can corroborate each other.
    /// </summary>
    private static int CountVisibleTopLevelWindows(int processId)
    {
        var count = 0;

        try
        {
            EnumWindows((hWnd, _) =>
            {
                GetWindowThreadProcessId(hWnd, out var ownerPid);
                if (ownerPid != (uint)processId)
                {
                    return true;
                }

                if (GetWindow(hWnd, GW_OWNER) != IntPtr.Zero)
                {
                    return true; // owned window: not a top-level application window
                }

                if (IsWindowVisible(hWnd))
                {
                    count++;
                }

                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            // A failed scan simply leaves the count at zero; MainWindowHandle is still evaluated.
        }

        return count;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint command);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsWindowVisible(IntPtr hWnd);
}
