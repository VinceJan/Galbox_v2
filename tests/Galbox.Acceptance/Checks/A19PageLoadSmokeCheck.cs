using System.Diagnostics;

namespace Galbox.Acceptance.Checks;

/// <summary>
/// A19 - Every page still loads inside the real application (runtime XAML smoke test).
///
/// Why this exists: the startup checks (A9/A18) only ever see the home page, so a page whose XAML
/// stops resolving a resource or a binding fails only when a user navigates to it - after the
/// application has already been reported as "starting fine". The library page and the game detail
/// page carry the markers for a missing game folder and the delete confirmation, so they need to be
/// proven to load, not just to compile. A deliberate mutation (an unregistered
/// <c>Style</c> key in <c>LibraryPage.xaml</c>) is caught by this check and by nothing else in the
/// suite, including the source-level A8.
///
/// The application writes one line per page to the file named by
/// <c>GALBOX_NAVIGATION_SMOKE_FILE</c> (see <c>MainWindow.RunRequestedNavigationSmokeTest</c>). The
/// result therefore travels in a file this check owns, not in the day-stamped startup log that every
/// instance on the machine shares. Pages are only loaded; nothing is changed.
///
/// <para>The eight pages really are loaded inside the running window - that is why the check starts
/// the executable at all. What the run may not do is put that window in front of whoever is using
/// the machine: it is started off-screen (<see cref="OffscreenWindow"/>), which does not change a
/// single page load. A window that lands on a monitor fails the check.</para>
/// </summary>
public sealed class A19PageLoadSmokeCheck : IAcceptanceCheck
{
    private const string NavigationSmokeVariable = "GALBOX_NAVIGATION_SMOKE";
    private const string NavigationSmokeResultFileVariable = "GALBOX_NAVIGATION_SMOKE_FILE";
    private const string NavigationSmokeMarker = "Navigation smoke";

    /// <summary>Pages loaded during the smoke run (id 1 matches the first library game, if any).</summary>
    private static readonly string[] NavigationKeys =
    {
        "Home", "Library", "SaveManager", "PatchCenter", "Settings", "GameDetail:1", "ScrapingProgress", "ErrorReport"
    };

    private static readonly TimeSpan WindowTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ResultTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <inheritdoc />
    public string Id => "A19";

    /// <inheritdoc />
    public string Title => "Every navigation destination loads inside the running application";

    /// <inheritdoc />
    public async Task<CheckResult> RunAsync(AcceptanceContext context, CancellationToken cancellationToken)
    {
        var expected = "with " + NavigationSmokeVariable + " set to every navigation key, the shipping executable "
                     + "reports \"=OK\" for each of them in the result file it was given, inside a real window "
                     + "that is outside every monitor (the run must not disturb the desktop)";

        var details = new List<string>();
        var work = AcceptanceWork.Create("a19");
        var resultFile = Path.Combine(work, "navigation-smoke.txt");

        var repoRoot = RepoLocator.FindRepoRoot();
        if (repoRoot is null)
        {
            return CheckResult.Fail(Id, Title, expected, "repository root not found");
        }

        var applicationPath = FindNewestApplication(repoRoot, out var buildUtc);
        if (applicationPath is null)
        {
            return CheckResult.Fail(Id, Title, expected, "Galbox.App.exe not found")
                .With($"FAIL REASON: no Galbox.App.exe under '{Path.Combine(repoRoot, "src", "Galbox.App", "bin")}'.");
        }

        details.Add($"Repository root      : {repoRoot}");
        details.Add($"Application          : {applicationPath}");
        details.Add($"Application built    : {buildUtc:yyyy-MM-dd HH:mm:ss}Z");
        details.Add($"Navigation keys      : {string.Join(", ", NavigationKeys)}");
        details.Add($"Result file          : {resultFile}");

        var failures = new List<string>();
        var attempts = 0;
        const int maxAttempts = 3;
        var lines = new List<string>();
        var windowHandle = IntPtr.Zero;
        WindowPlacement? placement = null;
        var aliveWhenMeasured = false;

        // Every worktree on this machine runs its own copy of this harness against the same
        // %LocalAppData%\Galbox, and one of them may kill Galbox.App processes while this check is
        // running. A bounded retry keeps a genuine page failure (which is deterministic) apart from
        // that environmental noise (which is not).
        while (attempts < maxAttempts)
        {
            attempts++;
            failures.Clear();
            windowHandle = IntPtr.Zero;
            aliveWhenMeasured = false;

            if (File.Exists(resultFile))
            {
                File.Delete(resultFile);
            }

            Process? process = null;

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = applicationPath,
                    WorkingDirectory = Path.GetDirectoryName(applicationPath)!,
                    UseShellExecute = false
                };
                startInfo.Environment[NavigationSmokeVariable] = string.Join(",", NavigationKeys);
                startInfo.Environment[NavigationSmokeResultFileVariable] = resultFile;

                // The eight pages must really be loaded, but the window that loads them must stay off
                // the developer's desktop: this check runs on every acceptance run, often while
                // somebody is using the machine. See OffscreenWindow.
                OffscreenWindow.Request(startInfo);

                process = Process.Start(startInfo);
                if (process is null)
                {
                    return CheckResult.Fail(Id, Title, expected, "Process.Start returned null").With(details.ToArray());
                }

                details.Add(string.Empty);
                details.Add($"--- attempt {attempts}/{maxAttempts}: pid {process.Id} ---");

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
                    if (windowHandle != IntPtr.Zero && CountSmokeLines(resultFile) >= NavigationKeys.Length)
                    {
                        break;
                    }
                }

                // Give the page loads a moment to finish even if the window appeared early.
                var resultStopwatch = Stopwatch.StartNew();
                while (resultStopwatch.Elapsed < ResultTimeout && CountSmokeLines(resultFile) < NavigationKeys.Length)
                {
                    if (process.HasExited)
                    {
                        break;
                    }

                    await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
                }

                process.Refresh();
                aliveWhenMeasured = !process.HasExited;
                lines = ReadSmokeLines(resultFile);

                // Measured while the process is still alive: the window rectangle is gone once the
                // finally block below kills it.
                placement = OffscreenWindow.Measure(windowHandle);

                details.Add($"The application exited before a window appeared: {exitedEarly}");
                details.Add($"MainWindowHandle     : 0x{windowHandle.ToInt64():X} after {stopwatch.ElapsedMilliseconds} ms");
                details.Add($"Alive when measured  : {aliveWhenMeasured}");
                details.Add($"Window placement     : {placement?.Describe() ?? "(no window handle, nothing to measure)"}");
                details.Add($"Off-screen monitors  : {placement?.MonitorSummary ?? "(not measured)"}");
                details.Add($"--- result lines ({lines.Count}/{NavigationKeys.Length}) ---");
                foreach (var line in lines)
                {
                    details.Add($"    {line}");
                }

                var results = ParseResults(lines);
                foreach (var key in NavigationKeys)
                {
                    var bareKey = key.Split(':')[0];
                    if (results.TryGetValue(bareKey, out var outcome) && outcome.StartsWith("OK", StringComparison.Ordinal))
                    {
                        details.Add($"  [OK]   {key,-18} {outcome}");
                    }
                    else
                    {
                        details.Add($"  [FAIL] {key,-18} {(results.TryGetValue(bareKey, out var value) ? value : "(no line written - the page never loaded)")}");
                        failures.Add(key);
                    }
                }

                if (failures.Count == 0)
                {
                    return Verdict(); // every page loaded
                }

                if (!exitedEarly)
                {
                    // The application is alive but did not report every page: that is a real page
                    // failure, and repeating the run cannot change it.
                    return Verdict();
                }

                // The process was gone before it could report: retry if attempts remain.
                if (attempts < maxAttempts)
                {
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
        }

        return Verdict();

        CheckResult Verdict()
        {
            // The eight pages are the point of this check; the placement requirement is added on top
            // of it, never instead of it: the run may not load them in a window the developer sees.
            var offscreen = placement is { IsWindowVisible: true, IsOffscreen: true };
            var pass = failures.Count == 0 && lines.Count >= NavigationKeys.Length && offscreen;

            details.Add(string.Empty);
            details.Add("--- interpretation ---");
            details.Add($"  pages loaded        : {NavigationKeys.Length - failures.Count}/{NavigationKeys.Length}");
            details.Add($"  window created      : {windowHandle != IntPtr.Zero}");
            details.Add($"  process still alive : {aliveWhenMeasured}");
            details.Add($"  window off-screen   : {placement?.IsOffscreen.ToString() ?? "(not measured)"} "
                      + $"(IsWindowVisible={placement?.IsWindowVisible.ToString() ?? "(not measured)"})");
            details.Add($"  launch attempts     : {attempts}");
            if (failures.Count > 0)
            {
                details.Add("  VERDICT: at least one page cannot be loaded at runtime, so navigating to it in the UI");
                details.Add("           would fail even though the application reports a healthy startup.");
            }
            else if (!offscreen)
            {
                details.Add($"  VERDICT: {OffscreenWindow.WouldBeVisibleReason}");
            }

            var actual = $"pagesLoaded={NavigationKeys.Length - failures.Count}/{NavigationKeys.Length}, "
                       + $"window={windowHandle != IntPtr.Zero}, alive={aliveWhenMeasured}, attempts={attempts}, "
                       + $"placement=[{placement?.Describe() ?? "(none)"}]";

            return pass
                ? CheckResult.Pass(Id, Title, expected, actual).With(details.ToArray())
                : CheckResult.Fail(Id, Title, expected, actual).With(details.ToArray());
        }
    }

    private static int CountSmokeLines(string resultFile)
    {
        return ReadSmokeLines(resultFile).Count;
    }

    private static List<string> ReadSmokeLines(string resultFile)
    {
        try
        {
            if (!File.Exists(resultFile))
            {
                return new List<string>();
            }

            // The application appends while this runs; a partially written line is not a result.
            return File.ReadAllLines(resultFile)
                .Where(line => line.Contains(NavigationSmokeMarker, StringComparison.Ordinal))
                .ToList();
        }
        catch (IOException)
        {
            return new List<string>();
        }
    }

    private static Dictionary<string, string> ParseResults(List<string> lines)
    {
        var results = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in lines)
        {
            var index = line.IndexOf(NavigationSmokeMarker, StringComparison.Ordinal);
            if (index < 0)
            {
                continue;
            }

            var afterMarker = line[(index + NavigationSmokeMarker.Length)..].TrimStart(':', ' ');
            var parts = afterMarker.Split('=', 2);
            if (parts.Length == 2)
            {
                results[parts[0].Trim()] = parts[1].Trim();
            }
        }

        return results;
    }

    private static string? FindNewestApplication(string repoRoot, out DateTime buildUtc)
    {
        buildUtc = DateTime.MinValue;

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
                var newest = Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
                    .Select(File.GetLastWriteTimeUtc)
                    .DefaultIfEmpty(DateTime.MinValue)
                    .Max();
                return (Path: path, BuildUtc: newest);
            })
            .OrderByDescending(candidate => candidate.BuildUtc)
            .ToList();

        if (candidates.Count == 0)
        {
            return null;
        }

        buildUtc = candidates[0].BuildUtc;
        return candidates[0].Path;
    }
}
