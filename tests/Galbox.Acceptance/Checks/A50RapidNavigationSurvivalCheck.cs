using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace Galbox.Acceptance.Checks;

/// <summary>
/// A50 - Rapid continuous navigation of the shipping GUI must not kill the process.
///
/// WHY THIS CHECK EXISTS
/// A user-visible crash (0xC000027B / STATUS_STOWED_EXCEPTION, faulting module
/// Microsoft.UI.Xaml.dll) was reproduced by clicking the navigation menu items back to back.
/// The root cause was NOT navigation logic: <c>PatchCenterPage.xaml</c> had an
/// <c>{x:Bind}</c> whose target lived inside a <c>&lt;Flyout&gt;</c> declared in
/// <c>Page.Resources</c>. Compiled bindings in a resource dictionary are driven by the generated
/// bindings object's <c>Initialize()</c>/<c>Update()</c>, which WinUI invokes from
/// <c>FrameworkElement.Loading</c> - and at that moment the lazily created flyout content has not
/// been attached yet, so <c>Connect()</c> has not stored the element and
/// <c>XamlBindingSetters.Set_...TextBlock_Text</c> dereferences null. The
/// <see cref="NullReferenceException"/> is thrown from inside a XAML callback, WinUI turns it into
/// a stowed exception and fail-fasts the process. Slow, single-page navigation hides it; rapid
/// switching decides which page is live when the broken callback lands, which is why only the
/// rapid case was ever seen.
///
/// WHAT IS ASSERTED
///   A50.1 (source guard, deterministic): no shipping XAML view may declare an <c>{x:Bind}</c>
///         inside a resource dictionary. This is the exact defect class and it costs milliseconds,
///         so the regression is caught even when the machine is too busy for the runtime part.
///   A50.2 (runtime): the real Galbox.App.exe is launched, every navigation menu item reachable
///         through UI Automation is selected in a rapid round robin for N switches, and the
///         process must still be alive at the end. A crash is reported with its exit code.
///
/// The pacing and switch count deliberately mirror the standalone probe used to diagnose the bug
/// (200 switches, 40 ms apart). Under heavy machine load the loop stops early at
/// <see cref="MinimumSwitches"/>, which is still well above the 10-55 switch band in which the
/// unfixed build always died.
///
/// <para><b>The 200 switches happen off-screen.</b> Nothing about the stress is reduced for it: the
/// same real window is created, the same UI Automation selects the same menu items the same number
/// of times at the same 40 ms pace. The only difference is that the window's rectangle is outside
/// every monitor (<see cref="OffscreenWindow"/>), because a developer who runs the acceptance suite -
/// or several work lines doing it at once - must not have a window popping up and paging through the
/// product on their desk. The placement is measured on the live window and the check fails if it
/// turns up on a monitor.</para>
/// </summary>
public sealed class A50RapidNavigationSurvivalCheck : IAcceptanceCheck
{
    /// <summary>Override for the switch target, mostly so a slow machine can be diagnosed.</summary>
    public const string SwitchesVariable = "GALBOX_A50_SWITCHES";

    private const int DefaultTargetSwitches = 200;

    /// <summary>
    /// Floor the loop never goes below. The unfixed build died between switch 10 and switch 55 in
    /// every observed run, so this is >2x the worst failure point.
    /// </summary>
    private const int MinimumSwitches = 120;

    /// <summary>Pacing between two selections; identical to the diagnosing probe.</summary>
    private const int StepMilliseconds = 40;

    /// <summary>Wall-clock budget for the stress loop on a busy machine.</summary>
    private static readonly TimeSpan StressBudget = TimeSpan.FromSeconds(150);

    private static readonly TimeSpan WindowTimeout = TimeSpan.FromSeconds(60);

    private const uint GW_OWNER = 4;

    /// <inheritdoc />
    public string Id => "A50";

    /// <inheritdoc />
    public string Title => "Rapid continuous navigation keeps the process alive";

    /// <inheritdoc />
    public async Task<CheckResult> RunAsync(AcceptanceContext context, CancellationToken cancellationToken)
    {
        var target = ReadSwitchTarget();
        var expected = $"{MinimumSwitches}-{target} rapid navigation switches with no crash "
                     + "(no {x:Bind} inside a resource dictionary, and Galbox.App.exe still alive at the end) "
                     + "in a window that is outside every monitor (the run must not disturb the desktop)";

        var details = new List<string>();

        var repoRoot = RepoLocator.FindRepoRoot();
        if (repoRoot is null)
        {
            return CheckResult.Fail(Id, Title, expected, "repository root not found")
                .With($"FAIL REASON: could not find {RepoLocator.SolutionMarker} above '{AppContext.BaseDirectory}'.");
        }

        details.Add($"Repository root      : {repoRoot}");

        // ---------------------------------------------------------------- A50.1 source guard
        var offenders = FindCompiledBindingsInResourceDictionaries(repoRoot);
        details.Add("--- A50.1 compiled bindings declared inside a resource dictionary ---");
        if (offenders.Count == 0)
        {
            details.Add("  [OK]   no shipping XAML view declares {x:Bind} inside *.Resources");
        }
        else
        {
            foreach (var offender in offenders)
            {
                details.Add($"  [BAD]  {offender}");
            }
        }

        if (offenders.Count > 0)
        {
            return CheckResult.Fail(Id, Title, expected,
                    $"{offenders.Count} {{x:Bind}} declaration(s) inside a resource dictionary")
                .With(details.ToArray())
                .With("FAIL REASON: a compiled binding inside a resource dictionary is resolved by the")
                .With("generated Initialize()/Update() while the lazily created resource content is still")
                .With("unattached. Connect() has not stored the target element yet, so the generated setter")
                .With("dereferences null, the NullReferenceException is raised from a XAML callback and WinUI")
                .With("fail-fasts the process with 0xC000027B. Move the binding to the code-behind or bind to")
                .With("an element of the normal visual tree.");
        }

        // ---------------------------------------------------------------- A50.2 runtime stress
        var applicationPath = FindApplicationExecutable(repoRoot);
        if (applicationPath is null)
        {
            return CheckResult.Fail(Id, Title, expected, "Galbox.App.exe not found")
                .With(details.ToArray())
                .With($"FAIL REASON: no Galbox.App.exe under '{Path.Combine(repoRoot, "src", "Galbox.App", "bin")}'.")
                .With("Build the solution first (dotnet build Galbox.sln -c Release).");
        }

        details.Add($"Application          : {applicationPath}");

        // The menu items are read from MainWindow.xaml so the check keeps working when a page is
        // added or renamed, instead of silently navigating to fewer and fewer places.
        var menuItems = ReadNavigationMenuItems(repoRoot);
        details.Add($"Navigation menu items declared in MainWindow.xaml: {menuItems.Count}"
                  + (menuItems.Count == 0 ? string.Empty : $" [{string.Join(", ", menuItems)}]"));

        if (menuItems.Count < 2)
        {
            return CheckResult.Fail(Id, Title, expected, "fewer than 2 navigation menu items were declared")
                .With(details.ToArray());
        }

        StressOutcome outcome;
        try
        {
            outcome = await RunStressAsync(applicationPath, menuItems, target, details, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return CheckResult.Error(Id, Title, expected, $"unhandled {ex.GetType().Name}", ex)
                .With(details.ToArray());
        }

        details.AddRange(outcome.Details);

        if (outcome.Crashed)
        {
            // Decode the well-known WinUI fail-fast so the report says what actually happened.
            var meaning = outcome.ExitCode == unchecked((int)0xC000027B)
                ? "0xC000027B = STATUS_STOWED_EXCEPTION (WinUI fail-fast on a stowed exception)"
                : $"0x{outcome.ExitCode:X8}";

            return CheckResult.Fail(Id, Title, expected,
                    $"the process died after {outcome.SwitchesCompleted} switch(es), exit {meaning}")
                .With(details.ToArray())
                .With($"FAIL REASON: Galbox.App.exe exited after {outcome.SwitchesCompleted} of "
                    + $"{outcome.SwitchesAttempted} rapid navigation switch(es) (last item '{outcome.LastItem}').")
                .With("A managed exception escaping a XAML callback, or a WinRT call made from the wrong")
                .With("thread, ends the process this way. Check %LocalAppData%\\Galbox\\logs\\firstchance.log")
                .With("(set GALBOX_FIRSTCHANCE_TRACE=1) and the Application event log for the faulting module.");
        }

        // Surviving is not enough on its own: the survival must not have been bought by putting the
        // 200 switches on somebody's desktop. The window really exists, is really visible and was
        // really driven - it is simply outside every monitor. A window on a monitor is a FAIL.
        if (outcome.Placement is not { IsWindowVisible: true, IsOffscreen: true })
        {
            return CheckResult.Fail(Id, Title, expected,
                    $"the stress loop ran in a window that was not off-screen: "
                  + $"{outcome.Placement?.Describe() ?? "(never measured - the window may never have appeared)"}")
                .With(details.ToArray())
                .With($"FAIL REASON: {OffscreenWindow.WouldBeVisibleReason}");
        }

        return CheckResult.Pass(Id, Title, expected,
                $"survived {outcome.SwitchesCompleted} rapid switch(es) over {outcome.Elapsed.TotalSeconds:F1}s, "
              + $"exit code never observed, window off-screen at {outcome.Placement.Rect} with "
              + $"IsWindowVisible={outcome.Placement.IsWindowVisible}")
            .With(details.ToArray());
    }

    /// <summary>What the stress loop observed.</summary>
    private sealed class StressOutcome
    {
        public bool Crashed { get; init; }

        public int ExitCode { get; init; }

        public int SwitchesCompleted { get; init; }

        public int SwitchesAttempted { get; init; }

        public string LastItem { get; init; } = "(none)";

        public TimeSpan Elapsed { get; init; }

        /// <summary>Where the driven window really was, measured before the loop started.</summary>
        public WindowPlacement? Placement { get; init; }

        public List<string> Details { get; init; } = new();
    }

    /// <summary>
    /// Launches the application and drives the navigation menu as fast as the standalone probe did.
    /// All UI Automation work happens on a dedicated STA thread: the automation client is a COM
    /// API and the acceptance host's thread-pool threads are MTA.
    /// </summary>
    private async Task<StressOutcome> RunStressAsync(
        string applicationPath,
        IReadOnlyList<string> menuItems,
        int target,
        List<string> details,
        CancellationToken cancellationToken)
    {
        using var process = Process.Start(CreateStartInfo(applicationPath));

        if (process is null)
        {
            throw new InvalidOperationException($"could not start '{applicationPath}'");
        }

        var processId = process.Id;
        details.Add($"Launched             : pid {processId}");

        var completion = new TaskCompletionSource<StressOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);

        var worker = new Thread(() =>
        {
            try
            {
                completion.SetResult(DriveNavigation(process, processId, menuItems, target, details, cancellationToken));
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "A50-uia"
        };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();

        StressOutcome outcome;
        try
        {
            outcome = await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // The process may have died between the check and the kill; nothing to do.
            }
        }

        return outcome;
    }

    /// <summary>The STA-thread body: find the window, resolve the menu, select items in a loop.</summary>
    private static StressOutcome DriveNavigation(
        Process process,
        int processId,
        IReadOnlyList<string> menuItems,
        int target,
        List<string> details,
        CancellationToken cancellationToken)
    {
        var localDetails = new List<string>();

        var window = WaitForWindow(process, processId, localDetails);
        if (window is null)
        {
            return new StressOutcome
            {
                Crashed = true,
                ExitCode = process.HasExited ? process.ExitCode : 0,
                SwitchesCompleted = 0,
                SwitchesAttempted = 0,
                Details = localDetails
            };
        }

        // Where the window actually is, measured on the live window before the loop starts. This is
        // the check's guarantee that 200 page switches are not performed in front of a developer:
        // the window is real, visible and driven by UI Automation, but its rectangle is outside
        // every monitor. GetWindowRect is only readable while the process is alive, so it is taken
        // here and carried in the outcome.
        var placement = OffscreenWindow.Measure(new IntPtr(window.Current.NativeWindowHandle));
        localDetails.Add($"Window placement     : {placement.Describe()}");
        localDetails.Add($"Off-screen monitors  : {placement.MonitorSummary}");

        // Resolve the menu items. A NavigationViewItem exposes SelectionItemPattern; an item that
        // does not is not something a user can click, so it is not driven either.
        var selections = new List<(string Name, SelectionItemPattern Pattern)>();
        foreach (var name in menuItems)
        {
            var condition = new PropertyCondition(AutomationElement.NameProperty, name);
            var element = window.FindFirst(TreeScope.Descendants, condition);
            if (element is null)
            {
                continue;
            }

            if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var raw) && raw is SelectionItemPattern pattern)
            {
                selections.Add((name, pattern));
            }
        }

        localDetails.Add($"Navigation items reachable through UI Automation: {selections.Count}"
                       + (selections.Count == 0 ? string.Empty : $" [{string.Join(", ", selections.Select(s => s.Name))}]"));

        if (selections.Count < 2)
        {
            return new StressOutcome
            {
                Crashed = false,
                SwitchesCompleted = 0,
                SwitchesAttempted = 0,
                Placement = placement,
                Details = localDetails
                    .Append("FAIL REASON: fewer than 2 navigation items exposed SelectionItemPattern, so the")
                    .Append("rapid navigation loop could not be driven.")
                    .ToList()
            };
        }

        localDetails.Add($"Pacing               : {StepMilliseconds} ms between selections, target {target} switches");

        var stopwatch = Stopwatch.StartNew();
        var completed = 0;
        var attempted = 0;
        var lastItem = "(none)";
        var crashed = false;

        for (var i = 0; i < target; i++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            // Honour the machine-load budget, but never stop before the floor.
            if (completed >= MinimumSwitches && stopwatch.Elapsed > StressBudget)
            {
                localDetails.Add($"Stopped at the {StressBudget.TotalSeconds:F0}s budget after {completed} switches "
                               + "(machine currently too loaded to continue; the floor is met).");
                break;
            }

            var (name, pattern) = selections[i % selections.Count];
            attempted++;
            try
            {
                pattern.Select();
            }
            catch (ElementNotAvailableException)
            {
                // The window went away between two selections: treat as a crash below.
            }
            catch (InvalidOperationException)
            {
                // Same: the element is gone.
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // UIA returns COM errors when the provider died mid-call.
            }

            completed++;
            lastItem = name;

            if (process.HasExited)
            {
                crashed = true;
                break;
            }

            Thread.Sleep(StepMilliseconds);

            if (process.HasExited)
            {
                crashed = true;
                break;
            }
        }

        stopwatch.Stop();

        localDetails.Add($"Switches completed   : {completed} / {attempted} attempted (target {target}, floor {MinimumSwitches})");
        localDetails.Add($"Elapsed              : {stopwatch.Elapsed.TotalSeconds:F1} s");
        localDetails.Add($"Last item selected   : {lastItem}");

        if (process.HasExited)
        {
            process.WaitForExit(5000);
            localDetails.Add($"Exit code            : 0x{unchecked((uint)process.ExitCode):X8}");
        }
        else
        {
            localDetails.Add("Process alive        : True (no exit was observed during the whole loop)");
            localDetails.Add($"Window handle        : 0x{window.Current.NativeWindowHandle:X}");
        }

        return new StressOutcome
        {
            Crashed = crashed || process.HasExited,
            ExitCode = process.HasExited ? process.ExitCode : 0,
            SwitchesCompleted = completed,
            SwitchesAttempted = attempted,
            LastItem = lastItem,
            Elapsed = stopwatch.Elapsed,
            Placement = placement,
            Details = localDetails
        };
    }

    /// <summary>
    /// Builds the launch the stress loop uses.
    /// </summary>
    /// <remarks>
    /// The switch count and the pacing are untouched - this is only about <i>where</i> the window
    /// that gets switched 200 times lives. It is started off-screen (see <see cref="OffscreenWindow"/>)
    /// so the two hundred page changes happen in a real window that nobody has to watch; UI Automation
    /// drives off-screen windows exactly like on-screen ones.
    /// </remarks>
    private static ProcessStartInfo CreateStartInfo(string applicationPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = applicationPath,
            WorkingDirectory = Path.GetDirectoryName(applicationPath)!,
            UseShellExecute = false
        };

        OffscreenWindow.Request(startInfo);
        return startInfo;
    }

    /// <summary>Waits for the process to own a visible top-level window and returns it.</summary>
    private static AutomationElement? WaitForWindow(Process process, int processId, List<string> details)
    {
        var condition = new PropertyCondition(AutomationElement.ProcessIdProperty, processId);
        var root = AutomationElement.RootElement;
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.Elapsed < WindowTimeout)
        {
            if (process.HasExited)
            {
                details.Add($"FAIL REASON: the application exited during startup "
                          + $"(0x{unchecked((uint)process.ExitCode):X8}) after {stopwatch.Elapsed.TotalSeconds:F1}s.");
                return null;
            }

            try
            {
                var window = root.FindFirst(TreeScope.Children, condition);
                if (window is not null)
                {
                    details.Add($"Window acquired      : after {stopwatch.Elapsed.TotalMilliseconds:F0} ms");
                    return window;
                }
            }
            catch (ElementNotAvailableException)
            {
                // The window disappeared between enumeration and access; retry.
            }

            Thread.Sleep(250);
        }

        details.Add($"FAIL REASON: no top-level window owned by pid {processId} appeared within "
                  + $"{WindowTimeout.TotalSeconds:F0}s.");
        return null;
    }

    /// <summary>Switch target, environment-overridable for diagnosing a slow machine.</summary>
    private static int ReadSwitchTarget()
    {
        var raw = Environment.GetEnvironmentVariable(SwitchesVariable);
        return int.TryParse(raw, out var parsed) && parsed > 0 ? parsed : DefaultTargetSwitches;
    }

    /// <summary>
    /// Finds <c>{x:Bind}</c> declarations that live inside a resource dictionary
    /// (<c>Page.Resources</c> / <c>UserControl.Resources</c> / <c>Application.Resources</c>).
    /// XAML comments are stripped first so a comment that merely mentions the pattern is not
    /// reported as a defect.
    /// </summary>
    private static List<string> FindCompiledBindingsInResourceDictionaries(string repoRoot)
    {
        var offenders = new List<string>();
        var viewsFolder = RepoLocator.ViewsFolder(repoRoot);

        var files = RepoLocator.EnumerateViewFiles(viewsFolder, "*.xaml")
            .Concat(Directory.Exists(Path.Combine(repoRoot, "src", "Galbox.App"))
                ? Directory.EnumerateFiles(
                    Path.Combine(repoRoot, "src", "Galbox.App"), "*.xaml", SearchOption.TopDirectoryOnly)
                : Enumerable.Empty<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);

        var resourcesOpen = new Regex(@"<\s*(Page|UserControl|Application|ResourceDictionary)\b[^>]*Resources\b|<\s*ResourceDictionary\b",
            RegexOptions.IgnoreCase);
        var resourcesClose = new Regex(@"</\s*(Page|UserControl|Application)\.Resources\s*>|</\s*ResourceDictionary\s*>",
            RegexOptions.IgnoreCase);

        foreach (var file in files)
        {
            string[] lines;
            try
            {
                lines = File.ReadAllLines(file);
            }
            catch
            {
                continue;
            }

            var depth = 0;
            var inComment = false;

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];

                // Strip XAML comments so documentation about the pattern is never flagged.
                var stripped = new System.Text.StringBuilder();
                var cursor = 0;
                while (cursor < line.Length)
                {
                    if (inComment)
                    {
                        var end = line.IndexOf("-->", cursor, StringComparison.Ordinal);
                        if (end < 0)
                        {
                            cursor = line.Length;
                        }
                        else
                        {
                            inComment = false;
                            cursor = end + 3;
                        }

                        continue;
                    }

                    var start = line.IndexOf("<!--", cursor, StringComparison.Ordinal);
                    if (start < 0)
                    {
                        stripped.Append(line, cursor, line.Length - cursor);
                        break;
                    }

                    stripped.Append(line, cursor, start - cursor);
                    var end2 = line.IndexOf("-->", start + 4, StringComparison.Ordinal);
                    if (end2 < 0)
                    {
                        inComment = true;
                        break;
                    }

                    cursor = end2 + 3;
                }

                var code = stripped.ToString();

                if (depth > 0 && code.Contains("{x:Bind", StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetRelativePath(repoRoot, file)}:{i + 1}: {code.Trim()}");
                }

                depth += resourcesOpen.Matches(code).Count;
                depth -= resourcesClose.Matches(code).Count;
                if (depth < 0)
                {
                    depth = 0;
                }
            }
        }

        return offenders;
    }

    /// <summary>
    /// Reads the navigation menu item captions from <c>MainWindow.xaml</c>, so the check follows
    /// the real menu instead of a hard-coded list that could silently go stale.
    /// </summary>
    private static List<string> ReadNavigationMenuItems(string repoRoot)
    {
        var items = new List<string>();
        var path = Path.Combine(RepoLocator.AppProject(repoRoot), "MainWindow.xaml");
        if (!File.Exists(path))
        {
            return items;
        }

        try
        {
            var text = File.ReadAllText(path);
            foreach (Match match in Regex.Matches(text, @"<NavigationViewItem\b[^>]*?Content=""([^""]+)""",
                         RegexOptions.Singleline))
            {
                var caption = match.Groups[1].Value;
                if (!items.Contains(caption, StringComparer.Ordinal))
                {
                    items.Add(caption);
                }
            }
        }
        catch
        {
            // An unreadable file simply yields no items and the caller reports that.
        }

        return items;
    }

    /// <summary>Newest Galbox.App.exe under the app project's bin folder.</summary>
    private static string? FindApplicationExecutable(string repoRoot)
    {
        var binRoot = Path.Combine(RepoLocator.AppProject(repoRoot), "bin");
        if (!Directory.Exists(binRoot))
        {
            return null;
        }

        return Directory
            .EnumerateFiles(binRoot, "Galbox.App.exe", SearchOption.AllDirectories)
            .Select(path => (Path: path, BuildUtc: Directory
                .EnumerateFiles(Path.GetDirectoryName(path)!, "*", SearchOption.TopDirectoryOnly)
                .Select(File.GetLastWriteTimeUtc)
                .DefaultIfEmpty(DateTime.MinValue)
                .Max()))
            .OrderByDescending(candidate => candidate.BuildUtc)
            .Select(candidate => candidate.Path)
            .FirstOrDefault();
    }
}
