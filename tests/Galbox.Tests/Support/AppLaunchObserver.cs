using System.Diagnostics;
using System.Text;
using Galbox.App.Services;

namespace Galbox.Tests.Support;

/// <summary>Everything observed during one application launch attempt.</summary>
internal sealed record AppLaunchObservations
{
    public required string RepositoryRoot { get; init; }

    public required string ExecutablePath { get; init; }

    public required DateTime BuildUtc { get; init; }

    public required DateTime NewestSourceUtc { get; init; }

    /// <summary>True when the binary is older than the sources, so it cannot be evidence about them.</summary>
    public bool BuildIsStale => NewestSourceUtc > BuildUtc;

    /// <summary>Notes gathered before the launch (leftover processes, cache state, ...).</summary>
    public required IReadOnlyList<string> PreflightNotes { get; init; }

    /// <summary>Number of <c>search_*.json</c> files in the scrape cache when the app was started.</summary>
    public int ScrapeCacheFiles { get; init; }

    public bool StartFailed { get; init; }

    public string? StartError { get; init; }

    public int? ProcessId { get; init; }

    /// <summary>True when the process exited on its own before any window appeared.</summary>
    public bool ExitedBeforeWindow { get; init; }

    public int? ExitCode { get; init; }

    /// <summary>
    /// True when the process was terminated by somebody else rather than exiting on its own.
    ///
    /// <c>-1</c> (0xFFFFFFFF) is the exit code <see cref="Process.Kill()"/> writes via
    /// <c>TerminateProcess</c>, and this application cannot produce it: it contains no
    /// <c>Environment.Exit</c> and no <c>Environment.FailFast</c>, <c>Program.Main</c> returns only 0
    /// or 1, an unhandled managed exception exits with a CLR code, and a WinUI crash exits with an
    /// NTSTATUS such as 0xC000027B. Observed live: other Galbox checkouts on this machine run test
    /// harnesses that terminate every <c>Galbox.App</c> process by name, which kills this test's
    /// process mid-flight.
    /// </summary>
    public bool ExternallyTerminated => ExitCode == -1;

    /// <summary>Time until the first window was observed, or null when none ever appeared.</summary>
    public TimeSpan? TimeToFirstWindow { get; init; }

    /// <summary>Total time the observation loop ran.</summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>
    /// The window that made the poll loop stop: either the first visible unowned top-level window
    /// or, when there was none, the process's main-window handle as reported by the framework.
    /// </summary>
    public NativeWindows.WindowInfo? CandidateWindow { get; init; }

    /// <summary>True when <see cref="CandidateWindow"/> came from <c>Process.MainWindowHandle</c>.</summary>
    public bool CandidateCameFromMainWindowHandle { get; init; }

    /// <summary>Visible, unowned top-level windows after the settle period.</summary>
    public IReadOnlyList<NativeWindows.WindowInfo> WindowsAfterSettle { get; init; } = Array.Empty<NativeWindows.WindowInfo>();

    public bool AliveAfterSettle { get; init; }

    /// <summary>Every window owned by the process at verdict time, including invisible and modal ones.</summary>
    public IReadOnlyList<NativeWindows.OwnedWindow> AllWindows { get; init; } = Array.Empty<NativeWindows.OwnedWindow>();

    /// <summary>Number of <c>#32770</c> dialogs owned by the process at verdict time.</summary>
    public int DialogCount { get; init; }

    /// <summary>Tail of the shared startup log, for diagnosis only - see the note in <see cref="Report"/>.</summary>
    public IReadOnlyList<string> StartupLogTail { get; init; } = Array.Empty<string>();

    /// <summary>Window handle observed for this process, used to correlate the log.</summary>
    public IntPtr ObservedHandle { get; init; }

    /// <summary>
    /// True when the shared startup log contains the "window created and activated" line for the
    /// handle this test observed - per-process proof that the startup sequence reached the point
    /// where the main window was activated, not merely that some window exists.
    /// </summary>
    public bool LogConfirmsThisProcessActivatedItsWindow { get; init; }

    /// <summary>Builds the multi-line diagnostic block that is attached to every assertion message.</summary>
    public string Report()
    {
        var text = new StringBuilder();
        text.AppendLine("--- Galbox.App launch observations -------------------------------------");
        text.AppendLine($"Repository root              : {RepositoryRoot}");
        text.AppendLine($"Executable                   : {ExecutablePath}");
        text.AppendLine($"Build time (newest bin file) : {BuildUtc:yyyy-MM-dd HH:mm:ss}Z");
        text.AppendLine($"Newest app source            : {NewestSourceUtc:yyyy-MM-dd HH:mm:ss}Z");
        text.AppendLine($"Build is stale               : {BuildIsStale}");

        foreach (var note in PreflightNotes)
        {
            text.AppendLine($"Preflight                    : {note}");
        }

        text.AppendLine($"Scrape cache search_*.json   : {ScrapeCacheFiles} "
                      + "(the historical ConfigureAwait defect only becomes visible while "
                      + "ScrapingCacheService.InitializeAsync genuinely yields, which requires >= 1)");

        if (StartFailed)
        {
            text.AppendLine($"Process.Start                : FAILED - {StartError}");
            return text.ToString();
        }

        text.AppendLine($"Process id                   : {ProcessId}");
        text.AppendLine($"Exited before window         : {ExitedBeforeWindow}"
                      + (ExitCode is int code ? $" (exit code {code})" : string.Empty));
        text.AppendLine($"Time to first window         : {(TimeToFirstWindow is { } t ? $"{t.TotalMilliseconds:F0} ms" : "never")}");
        text.AppendLine($"Candidate window             : {(CandidateWindow is { } w ? w.ToString() : "(none)")}"
                      + (CandidateCameFromMainWindowHandle ? "  [from Process.MainWindowHandle]" : "  [from EnumWindows]"));
        text.AppendLine($"Alive after settle           : {AliveAfterSettle}");
        text.AppendLine($"Visible top-level windows after settle: {WindowsAfterSettle.Count}");
        foreach (var window in WindowsAfterSettle)
        {
            text.AppendLine($"    {window}");
        }

        text.AppendLine($"All windows owned by pid     : {AllWindows.Count}");
        foreach (var window in AllWindows)
        {
            text.AppendLine($"    {window.Window}");
        }

        text.AppendLine($"Modal dialogs (#32770)       : {DialogCount}");
        text.AppendLine($"Startup-failure caption      : \"{StartupDiagnostics.StartupFailureCaption}\"");
        text.AppendLine($"Observed hwnd                : 0x{ObservedHandle.ToInt64():X}");
        text.AppendLine($"Log confirms THIS pid activated its window: {LogConfirmsThisProcessActivatedItsWindow}");

        text.AppendLine("--- shared startup log tail (DIAGNOSTIC ONLY) --------------------------");
        text.AppendLine("NOTE: %LocalAppData%\\Galbox\\logs is machine-global. Every Galbox checkout on this");
        text.AppendLine("      machine appends to the same file, so lines below cannot be attributed to this");
        text.AppendLine("      run. The verdict never depends on them; the per-pid window state above does.");
        if (StartupLogTail.Count == 0)
        {
            text.AppendLine("    (no log lines)");
        }
        else
        {
            foreach (var line in StartupLogTail)
            {
                text.AppendLine($"    {line}");
            }
        }

        text.AppendLine("------------------------------------------------------------------------");
        return text.ToString();
    }
}

/// <summary>
/// Launches the shipping executable and observes whether a real top-level window appears.
///
/// This exists because this code base had two startup defects that no headless test could see and
/// that were only found by hand, both with the same user-visible symptom - the process is alive,
/// nothing is displayed and nothing is reported:
///
///   1. <c>MainWindow.xaml.cs</c> declared <c>SetWindowSubclass</c> / <c>RemoveWindowSubclass</c> /
///      <c>DefSubclassProc</c> against <c>user32.dll</c> although they are exported by
///      <c>comctl32.dll</c>. The <c>DllImport</c> resolved lazily, so the process started and the
///      first call inside the <c>MainWindow</c> constructor threw
///      <c>EntryPointNotFoundException</c>.
///   2. A <c>ConfigureAwait(false)</c> in the startup path resumed the continuation on a
///      thread-pool thread, so <c>new MainWindow()</c> threw
///      <c>COMException 0x8001010E (RPC_E_WRONG_THREAD)</c>.
///
/// Every assertion the smoke test makes is derived from state that is keyed by the launched
/// process id, so the verdict can only describe the process this helper started.
/// </summary>
internal static class AppLaunchObserver
{
    /// <summary>Default time budget for a window to appear.</summary>
    public static readonly TimeSpan DefaultWindowTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long the window must survive after it first appears. A window that is created and then
    /// torn down by an exception on the first frame is not a successful start.
    /// </summary>
    public static readonly TimeSpan DefaultSettlePeriod = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Launches the application, retrying only when the attempt was cut short by an external kill.
    ///
    /// <para>
    /// WHY THIS RETRY EXISTS AND WHY IT IS NOT A WEAKENED CHECK. This machine runs several Galbox
    /// checkouts at once, and at least one of them runs a test harness that terminates every
    /// <c>Galbox.App</c> process by name (<c>Process.GetProcessesByName("Galbox.App")</c> then
    /// <c>Kill()</c>) - the previous version of this very file did that too, which is why this
    /// project no longer does. That call kills this test's process mid-launch, and the resulting
    /// observation is indistinguishable from a real failure unless it is classified properly.
    /// </para>
    /// <para>
    /// The classification is narrow: an attempt is retried <b>only</b> when the exit code is exactly
    /// <c>-1</c>, which is unreachable for this application (see
    /// <see cref="AppLaunchObservations.ExternallyTerminated"/>). It is never retried for "no window
    /// appeared", "the window did not survive", "the window is the failure dialog" or any other
    /// reason. Every retry is appended to the returned notes and printed, so the interference can
    /// never be hidden, and if every attempt is killed the test still fails - with a message that
    /// says the environment is hostile rather than that the application is broken.
    /// </para>
    /// </summary>
    /// <returns>The final attempt's observations, plus a note for each retried attempt.</returns>
    public static (AppLaunchObservations Observations, IReadOnlyList<string> Notes) LaunchRetryingExternalKills(
        TimeSpan windowTimeout,
        TimeSpan settlePeriod,
        int maxAttempts = 3)
    {
        var notes = new List<string>();

        for (var attempt = 1; ; attempt++)
        {
            var observation = Launch(windowTimeout, settlePeriod);

            if (!observation.ExternallyTerminated || attempt >= maxAttempts)
            {
                if (notes.Count > 0 && observation.ExternallyTerminated)
                {
                    notes.Add(
                        $"all {maxAttempts} attempts were terminated externally; this is an ENVIRONMENT "
                      + "problem (another process is killing every Galbox.App by name), not an "
                      + "application defect.");
                }

                return (observation, notes);
            }

            notes.Add(
                $"attempt {attempt}/{maxAttempts}: observed process {observation.ProcessId} was terminated "
              + "externally (exit code -1, which only Process.Kill() produces and this application cannot "
              + "produce), so the attempt proves nothing about the application. Retrying.");
        }
    }

    /// <summary>
    /// Resolves the build, launches it and observes it. Never throws for an application-level
    /// failure - it returns the observations and lets the test turn them into assertions, so the
    /// failure message always contains the full evidence.
    /// </summary>
    public static AppLaunchObservations Launch(
        TimeSpan windowTimeout,
        TimeSpan settlePeriod,
        bool killLeftoverInstances = true)
    {
        var notes = new List<string>();

        var repositoryRoot = RepoLayout.FindRepositoryRoot()
            ?? throw new InvalidOperationException(
                $"Could not find '{RepoLayout.SolutionMarker}' above '{AppContext.BaseDirectory}'. "
                + "The test assembly must run from inside the Galbox work tree.");

        var executable = RepoLayout.FindApplicationExecutable(repositoryRoot, out var buildUtc);
        if (executable is null)
        {
            throw new InvalidOperationException(
                $"No Galbox.App.exe under '{Path.Combine(RepoLayout.AppProject(repositoryRoot), "bin")}'. "
                + "Build the solution first: dotnet build Galbox.sln -c Debug");
        }

        if (killLeftoverInstances)
        {
            var killed = RepoLayout.KillLeftoverInstancesOfThisWorkTree(repositoryRoot);
            notes.Add(killed.Count == 0
                ? "no leftover Galbox.App instance from this work tree"
                : $"terminated leftover instance(s) of this work tree: {string.Join(", ", killed)}");
        }

        var cacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Galbox", "ScrapingCache");
        var cacheFiles = Directory.Exists(cacheDirectory)
            ? Directory.GetFiles(cacheDirectory, "search_*.json").Length
            : 0;
        notes.Add($"scrape cache directory {(Directory.Exists(cacheDirectory) ? "exists" : "missing")}: {cacheDirectory}");

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false
        };

        // This is a real launch of the shipping executable, and it must not put a window on the
        // desktop of whoever ran `dotnet test`. Every assertion below is unaffected: the window is
        // still real, still visible and still owned by this process - it is simply outside every
        // monitor. See OffscreenLaunch.
        OffscreenLaunch.Apply(startInfo);

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            return new AppLaunchObservations
            {
                RepositoryRoot = repositoryRoot,
                ExecutablePath = executable,
                BuildUtc = buildUtc,
                NewestSourceUtc = RepoLayout.NewestApplicationSourceUtc(repositoryRoot),
                PreflightNotes = notes,
                ScrapeCacheFiles = cacheFiles,
                StartFailed = true,
                StartError = $"{ex.GetType().Name}: {ex.Message}"
            };
        }

        if (process is null)
        {
            return new AppLaunchObservations
            {
                RepositoryRoot = repositoryRoot,
                ExecutablePath = executable,
                BuildUtc = buildUtc,
                NewestSourceUtc = RepoLayout.NewestApplicationSourceUtc(repositoryRoot),
                PreflightNotes = notes,
                ScrapeCacheFiles = cacheFiles,
                StartFailed = true,
                StartError = "Process.Start returned null"
            };
        }

        var stopwatch = Stopwatch.StartNew();
        var exitedBeforeWindow = false;
        NativeWindows.WindowInfo? candidate = null;
        var candidateFromMainWindowHandle = false;

        try
        {
            // ---------------------------------------------------------------- wait for a window
            while (stopwatch.Elapsed < windowTimeout)
            {
                Thread.Sleep(PollInterval);

                if (process.HasExited)
                {
                    exitedBeforeWindow = true;
                    break;
                }

                process.Refresh();

                if (process.MainWindowHandle != IntPtr.Zero)
                {
                    candidate = NativeWindows.Describe(process.MainWindowHandle);
                    candidateFromMainWindowHandle = true;
                    break;
                }

                var visible = NativeWindows.VisibleTopLevelWindows(process.Id);
                if (visible.Count > 0)
                {
                    candidate = visible[0];
                    break;
                }
            }

            var timeToFirstWindow = candidate is null ? (TimeSpan?)null : stopwatch.Elapsed;

            // ------------------------------------------------------------------- settle period
            var aliveAfterSettle = false;
            IReadOnlyList<NativeWindows.WindowInfo> afterSettle = Array.Empty<NativeWindows.WindowInfo>();

            if (!exitedBeforeWindow)
            {
                Thread.Sleep(settlePeriod);
                process.Refresh();
                aliveAfterSettle = !process.HasExited;
                afterSettle = NativeWindows.VisibleTopLevelWindows(process.Id);
            }

            var allWindows = NativeWindows.AllWindows(process.Id);
            var dialogCount = NativeWindows.DialogCount(process.Id);
            var handle = candidate?.Handle
                      ?? (process.MainWindowHandle != IntPtr.Zero
                            ? process.MainWindowHandle
                            : afterSettle.FirstOrDefault().Handle);
            var logTail = ReadStartupLogTail();
            var logConfirms = handle != IntPtr.Zero && LogContainsActivationOf(handle);

            int? exitCode = null;
            if (process.HasExited)
            {
                try
                {
                    exitCode = process.ExitCode;
                }
                catch
                {
                    // Exit code is unavailable for a process that was not started by this process
                    // object; it is only ever used in the diagnostic report.
                }
            }

            stopwatch.Stop();

            return new AppLaunchObservations
            {
                RepositoryRoot = repositoryRoot,
                ExecutablePath = executable,
                BuildUtc = buildUtc,
                NewestSourceUtc = RepoLayout.NewestApplicationSourceUtc(repositoryRoot),
                PreflightNotes = notes,
                ScrapeCacheFiles = cacheFiles,
                ProcessId = process.Id,
                ExitedBeforeWindow = exitedBeforeWindow,
                ExitCode = exitCode,
                TimeToFirstWindow = timeToFirstWindow,
                Elapsed = stopwatch.Elapsed,
                CandidateWindow = candidate,
                CandidateCameFromMainWindowHandle = candidateFromMainWindowHandle,
                WindowsAfterSettle = afterSettle,
                AliveAfterSettle = aliveAfterSettle,
                AllWindows = allWindows,
                DialogCount = dialogCount,
                StartupLogTail = logTail,
                ObservedHandle = handle,
                LogConfirmsThisProcessActivatedItsWindow = logConfirms
            };
        }
        finally
        {
            // Cleanup must happen for every verdict, including the ones returned above.
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(10_000);
                }
            }
            catch
            {
                // Best effort: a leftover process would only affect the next launch, and the
                // preflight step of the next run terminates leftovers of this work tree.
            }

            process.Dispose();
        }
    }

    /// <summary>
    /// The newest <c>startup-*.log</c> in the log folder.
    ///
    /// The file name embeds the date, and a run that starts just before midnight and is measured
    /// just after it would otherwise read the wrong file. Picking the newest file in the folder is
    /// immune to that, and is also immune to the application having appended to yesterday's file.
    /// </summary>
    private static string? NewestStartupLogPath()
    {
        try
        {
            var directory = StartupDiagnostics.LogDirectory;
            if (!Directory.Exists(directory))
            {
                return null;
            }

            return Directory
                .EnumerateFiles(directory, "startup-*.log")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the last lines of the newest startup log. The file is machine-global, so this is only
    /// ever attached to failure messages as context - never used as the basis of a verdict.
    /// </summary>
    private static IReadOnlyList<string> ReadStartupLogTail()
    {
        try
        {
            var path = NewestStartupLogPath();
            if (path is null)
            {
                return Array.Empty<string>();
            }

            // The application writes with File.AppendAllText, which can hold the file briefly.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            var lines = new List<string>();
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                lines.Add(line);
            }

            return lines.TakeLast(40).Select(l => $"{Path.GetFileName(path)}: {l}").ToList();
        }
        catch (Exception ex)
        {
            return new[] { $"(could not read the startup log: {ex.Message})" };
        }
    }

    /// <summary>
    /// True when the newest startup log contains the "window created and activated" line for
    /// <paramref name="handle"/>.
    ///
    /// This is the one log-based observation that is safe to use: a window handle is unique among
    /// live windows, so a line carrying this process's handle can only have been written by this
    /// process - unlike the rest of the shared log, which every Galbox checkout on the machine
    /// appends to. It therefore proves the startup sequence reached main-window activation rather
    /// than merely that some window exists.
    /// </summary>
    private static bool LogContainsActivationOf(IntPtr handle)
    {
        try
        {
            var path = NewestStartupLogPath();
            if (path is null)
            {
                return false;
            }

            var expected = $"handle=0x{handle.ToInt64():X}";
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (line.Contains("Main window created and activated", StringComparison.Ordinal)
                 && line.Contains(expected, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }
}
