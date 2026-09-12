using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Galbox.App.Services;

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
/// <para><b>Isolation (why this check launches the application against a private data folder).</b>
/// The startup log used to live at the single, day-stamped
/// <c>%LocalAppData%\Galbox\logs\startup-YYYYMMDD.log</c>, which every Galbox instance on the
/// machine appends to. Measuring one launch out of that file is not sound: the byte offset this
/// check captured before its own launch is meaningless once another instance has written in
/// between, and the tail it reads can contain another process's
/// <c>OnLaunched: startup sequence begins</c> - or, when that other instance failed to start, its
/// <c>EXCEPTION in OnLaunched</c>, which turned a perfectly healthy launch into a FAIL. Observed in
/// the wild before the fix: two interleaved startup sequences on the same second, from two
/// processes, in one file.</para>
///
/// <para>The check therefore launches <c>Galbox.App.exe</c> with <c>GALBOX_DATA_DIR</c> pointing at
/// a throwaway folder it owns (<see cref="AcceptanceWork"/>), and reads the startup log from
/// <i>that</i> folder. The application already honours the variable for its database; the startup
/// log and the backup store now follow the same data folder
/// (<see cref="GalboxDataDirectory"/>), so the launched process writes its database, its scrape
/// cache and its log inside the private folder and touches none of the user's data. The verdict is
/// unchanged and, thanks to the pid prefix in every log line, provably about this process only.</para>
///
/// Precondition (prepared inside the private data folder, never from the user's data):
///   * the cache folder must contain at least one <c>search_*.json</c> file that the service will
///     read. The private folder starts empty, so this check writes its own probe entry and removes
///     it again afterwards.
///
/// Verdict: within the timeout the process must still be alive AND own a visible top-level window
/// (<see cref="Process.MainWindowHandle"/> non-zero, cross-checked with an EnumWindows scan so a
/// Win32-API quirk can never turn a real window into a false failure). On failure the startup log
/// written by this launch is attached, which is also what proves the startup-failure reporting is
/// no longer silent.
///
/// <para><b>What the window is allowed to do to the desktop: nothing.</b> The requirement above is
/// unchanged - a real, visible, top-level window is still mandatory, because "process alive, window
/// missing" is the defect this check exists for. What the check additionally measures is <i>where</i>
/// that window is: it is started with <see cref="OffscreenWindow.Variable"/> set, so the application
/// puts the window outside every monitor while Win32 still reports it visible and the shell still
/// gives it a title. A window that turns up on a monitor fails the check (see
/// <see cref="OffscreenWindow.WouldBeVisibleReason"/>): this suite is run repeatedly, sometimes by
/// several work lines at once, and it may not flash windows in front of whoever is using the
/// machine.</para>
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
                     + $"alive and owns a visible top-level window within {WindowTimeout.TotalSeconds:F0}s, "
                     + "that window is outside every monitor (the run must not disturb the desktop), "
                     + "and the startup log OF THAT PROCESS (its own private data folder) reports completion";

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

        // ------------------------------------------------- private data folder for this launch
        // Everything the application persists - database, scrape cache, startup log - goes here.
        // AcceptanceWork already owns %LocalAppData%\Galbox\acceptance\work and wipes the folder of
        // this check at the start of every run, so the log read below can only contain this launch.
        var work = AcceptanceWork.Create("a9");
        var dataDirectory = Path.Combine(work, "appdata");

        var defaultLogPath = GalboxDataDirectory.ResolveStartupLogPath(dataDirectoryOverride: null);
        var privateLogPath = GalboxDataDirectory.ResolveStartupLogPath(dataDirectory);
        var privateDatabasePath = Path.Combine(dataDirectory, "galbox.db");
        var realDatabasePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Galbox", "galbox.db");

        var cacheDirectory = Path.Combine(dataDirectory, "ScrapingCache");
        Directory.CreateDirectory(cacheDirectory);

        details.Add($"Private data folder  : {dataDirectory}  (passed to the child as "
                  + $"{GalboxDataDirectory.DataDirectoryVariable})");
        details.Add($"Real app database    : {realDatabasePath} (must stay untouched)");
        details.Add($"Private database     : {privateDatabasePath}");
        details.Add($"Private startup log  : {privateLogPath}");
        details.Add($"Shared startup log   : {defaultLogPath} (NOT read by this check any more)");

        // ------------------------------------------------------------- precondition: cache file
        var probeFile = Path.Combine(cacheDirectory, "search_a9-startup-probe.json");
        await File.WriteAllTextAsync(probeFile, BuildProbeCacheEntry(), cancellationToken).ConfigureAwait(false);
        details.Add("Cache precondition  : private cache folder was EMPTY, wrote a probe entry "
                  + $"(removed again at the end of this check): {Path.GetFileName(probeFile)}");

        var logLengthBefore = File.Exists(privateLogPath) ? new FileInfo(privateLogPath).Length : 0;
        details.Add($"Startup log          : {privateLogPath} (exists: {File.Exists(privateLogPath)}, "
                  + $"bytes before launch: {logLengthBefore})");

        Process? process = null;
        var windowHandle = IntPtr.Zero;
        var windowTitle = string.Empty;
        WindowPlacement? placement = null;
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

            // The child's whole world: database, scrape cache and startup log live under this path.
            startInfo.Environment[GalboxDataDirectory.DataDirectoryVariable] = dataDirectory;

            // The window this check requires must not be on anybody's desktop. It stays a real,
            // visible, top-level window - only its rectangle moves outside every monitor. See
            // OffscreenWindow.
            OffscreenWindow.Request(startInfo);

            process = Process.Start(startInfo);
            if (process is null)
            {
                return CheckResult.Fail(Id, Title, expected, "Process.Start returned null")
                    .With(details.ToArray());
            }

            var launchedProcessId = process.Id;
            details.Add($"Launched             : pid {launchedProcessId}");
            var stopwatch = Stopwatch.StartNew();
            var windowSeen = false;

            // The window becomes visible BEFORE the startup sequence is finished: OnLaunched
            // activates MainWindow and only then runs ProcessMonitorStartup, which writes the
            // "startup completed" marker this check asserts on. The original loop broke out the
            // moment a window appeared and killed the process right there, so the marker was read
            // from a process that had been stopped mid-startup. That made A9 non-deterministic -
            // the same binary passed or failed depending on how quickly the window handle showed
            // up (observed: window at +2.1s -> FAIL, window at +2.7s -> PASS, no source change in
            // between).
            //
            // The loop now keeps polling after the first window until the startup log reports
            // completion or failure, with the same 30s ceiling. Every assertion below is unchanged;
            // only the moment the process is stopped is now deterministic.
            while (stopwatch.Elapsed < WindowTimeout)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);

                if (process.HasExited)
                {
                    exitBeforeWindow = !windowSeen;
                    break;
                }

                process.Refresh();
                windowHandle = process.MainWindowHandle;
                visibleTopLevelWindows = CountVisibleTopLevelWindows(process.Id);

                if (windowHandle != IntPtr.Zero || visibleTopLevelWindows > 0)
                {
                    windowSeen = true;

                    var tail = ReadNewLogLines(privateLogPath, logLengthBefore);

                    if (tail.Any(line => line.Contains(StartupDiagnostics.StartupCompletedMarker, StringComparison.Ordinal))
                        || tail.Any(line => line.Contains(StartupDiagnostics.StartupFailureMarker, StringComparison.Ordinal)))
                    {
                        break;
                    }
                }
            }

            elapsed = stopwatch.Elapsed;

            if (!windowSeen && !exitBeforeWindow)
            {
                // Give a slow cold start one more measured chance and report the final numbers.
                process.Refresh();
                windowHandle = process.MainWindowHandle;
                visibleTopLevelWindows = CountVisibleTopLevelWindows(process.Id);
                windowSeen = windowHandle != IntPtr.Zero || visibleTopLevelWindows > 0;
            }

            // Record liveness BEFORE the cleanup below terminates the process, otherwise the
            // verdict would always read "not alive" no matter how healthy the startup was.
            process.Refresh();
            aliveBeforeCleanup = !process.HasExited;
            windowTitle = windowHandle != IntPtr.Zero ? ReadWindowTitle(windowHandle) : string.Empty;

            // Also before the kill: GetWindowRect can only be read while the window exists.
            placement = OffscreenWindow.Measure(windowHandle);
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

        var processAlive = aliveBeforeCleanup;
        var hasWindow = windowHandle != IntPtr.Zero || visibleTopLevelWindows > 0;

        // A visible window is NOT sufficient: the startup failure handler raises its own modal
        // window, and treating that as "started fine" would invert the verdict of this check.
        var windowIsFailureDialog = windowTitle.Equals(
            StartupDiagnostics.StartupFailureCaption, StringComparison.Ordinal);

        details.Add($"Alive when measured  : {processAlive} (measured before the cleanup kill)");
        details.Add($"MainWindowHandle     : 0x{windowHandle.ToInt64():X} ({(windowHandle == IntPtr.Zero ? "zero" : "non-zero")})");
        details.Add($"Visible top-level windows owned by the process: {visibleTopLevelWindows}");
        details.Add($"Elapsed              : {elapsed.TotalMilliseconds:F0} ms (timeout {WindowTimeout.TotalSeconds:F0}s)");
        details.Add($"Exited before window : {exitBeforeWindow}");
        details.Add($"Cache files now      : {Directory.GetFiles(cacheDirectory, "search_*.json").Length} search_*.json");

        var logTail = ReadNewLogLines(privateLogPath, logLengthBefore);
        details.Add($"--- startup log lines written by this launch ({logTail.Count}) ---");
        if (logTail.Count == 0)
        {
            details.Add("    NONE");
        }
        else
        {
            details.AddRange(logTail.Select(line => $"    {line}"));
        }

        // The application reports a healthy startup by writing StartupCompletedMarker, and a failed
        // one by writing StartupFailureMarker plus (when no window exists) the failure dialog.
        var startupCompleted = logTail.Any(line =>
            line.Contains(StartupDiagnostics.StartupCompletedMarker, StringComparison.Ordinal));
        var startupFailed = logTail.Any(line =>
            line.Contains(StartupDiagnostics.StartupFailureMarker, StringComparison.Ordinal));

        // ------------------------------------------------------------- provenance of every line
        // The log is private to this launch, and every line says which process wrote it. Asserting
        // both is what makes "I saw my own startup" a measurement instead of an assumption.
        var ownProcessId = process?.Id ?? -1;
        var ownPidToken = $"[pid:{ownProcessId}]";
        var foreignLines = logTail.Where(line => !line.Contains(ownPidToken, StringComparison.Ordinal)).ToList();
        var beginCount = logTail.Count(line =>
            line.Contains("OnLaunched: startup sequence begins", StringComparison.Ordinal));

        details.Add($"Log lines from this pid : {logTail.Count - foreignLines.Count}/{logTail.Count} "
                  + $"(expected token \"{ownPidToken}\")");
        details.Add($"Log lines from ANOTHER process: {foreignLines.Count}"
                  + (foreignLines.Count == 0 ? string.Empty : " -> " + string.Join(" | ", foreignLines.Take(5))));
        details.Add($"\"startup sequence begins\" lines in THIS log: {beginCount} (must be exactly 1)");

        // The application really used the private data folder: if GALBOX_DATA_DIR had been ignored,
        // these two files would not exist and the check would be reading the wrong world.
        var privateDatabaseExists = File.Exists(privateDatabasePath);
        var privateLogExists = File.Exists(privateLogPath);
        details.Add($"Private database created by the launch : {privateDatabaseExists} "
                  + (privateDatabaseExists ? $"({new FileInfo(privateDatabasePath).Length} bytes)" : string.Empty));
        details.Add($"Private startup log exists            : {privateLogExists}");

        details.Add($"Window title         : \"{windowTitle}\"");
        details.Add($"Window is the startup-failure dialog: {windowIsFailureDialog}");
        details.Add($"Window placement     : {placement?.Describe() ?? "(no window handle, nothing to measure)"}");
        details.Add($"Off-screen monitors  : {placement?.MonitorSummary ?? "(not measured)"}");
        details.Add($"Startup log says     : completed={startupCompleted}, failed={startupFailed}");

        var isolationProven = foreignLines.Count == 0 && beginCount == 1 && privateDatabaseExists;

        // A window is required; a window ON A MONITOR is not acceptable. The rectangle is measured,
        // never assumed - see OffscreenWindow.
        var offscreenProven = placement is { IsWindowVisible: true, IsOffscreen: true };

        if (processAlive && hasWindow && !windowIsFailureDialog && startupCompleted && !startupFailed
            && isolationProven && offscreenProven)
        {
            return CheckResult.Pass(
                    Id, Title, expected,
                    $"window present: MainWindowHandle=0x{windowHandle.ToInt64():X} (\"{windowTitle}\"), "
                  + $"startup log reports completion, pid alive after {elapsed.TotalMilliseconds:F0} ms, "
                  + $"log isolated to pid {ownProcessId} ({logTail.Count} lines, 0 foreign), "
                  + $"window off-screen at {placement!.Rect} with IsWindowVisible={placement.IsWindowVisible}")
                .With(details.ToArray());
        }

        var reason = exitBeforeWindow
            ? "the application exited before any window appeared"
            : windowIsFailureDialog
                ? "the only window is the startup-failure dialog, so the application did NOT start "
                + "successfully (a window alone must never be accepted as success)"
                : !startupCompleted
                    ? "the startup log never reported completion (the application could not finish "
                    + "starting, and in the pre-fix build it did so silently)"
                    : startupFailed
                        ? "the startup log reported a failure (EXCEPTION in OnLaunched)"
                        : !isolationProven
                            ? "the launch did not prove its own isolation: "
                            + $"{foreignLines.Count} line(s) from another process, {beginCount} "
                            + "\"startup sequence begins\" line(s), private database created="
                            + $"{privateDatabaseExists}"
                            : !offscreenProven
                                ? OffscreenWindow.WouldBeVisibleReason
                                : "the process stayed alive but never created a visible top-level window "
                                + "(the classic 'double-click does nothing' failure: the startup exception "
                                + "was swallowed)";

        details.Add($"FAIL REASON: {reason}");
        return CheckResult.Fail(Id, Title, expected,
                $"MainWindowHandle=0x{windowHandle.ToInt64():X} (\"{windowTitle}\"), "
              + $"visibleTopLevelWindows={visibleTopLevelWindows}, alive={processAlive}, "
              + $"exitedEarly={exitBeforeWindow}, completed={startupCompleted}, failed={startupFailed}, "
              + $"foreignLogLines={foreignLines.Count}, beginsLines={beginCount}, "
              + $"placement=[{placement?.Describe() ?? "(none)"}]")
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextLengthW(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    /// <summary>Reads the title of a window (used to tell the app window from the failure dialog).</summary>
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
