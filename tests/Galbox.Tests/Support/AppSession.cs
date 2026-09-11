using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

namespace Galbox.Tests.Support;

/// <summary>
/// A live, UIA-driven session against the shipping application, for the control-level steps.
///
/// <para>
/// MEASURED LIMITATION - read before un-skipping anything that uses this class.
/// On this machine UIA-driven control interaction is not deterministic enough to be a pass/fail
/// gate. Three independent runs of the same binary produced:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <c>SetForegroundWindow</c> + <c>BringWindowToTop</c> + <c>SetActiveWindow</c> all fail to
///     make the application the foreground window (<c>GetForegroundWindow()</c> still reports
///     another window), because the desktop is shared with other applications. FlaUI's
///     <c>Click()</c> is a real mouse click at screen coordinates, so it then lands on whatever
///     window is on top.
///   </description></item>
///   <item><description>
///     the UIA tree stops responding part-way through a navigation sweep:
///     <c>FindAllDescendants()</c> returns 0 and every later element lookup fails for the rest of
///     the process, while the application is provably alive (window present, responds to WM_NULL
///     within 3 s, no dialogs, no crash in the event log).
///   </description></item>
///   <item><description>
///     identical input sequences produce different outcomes: a sweep of
///     Home -> Library -> SaveManager -> PatchCenter navigated correctly three times and then
///     destroyed the automation tree, while a different run navigated none of them.
///   </description></item>
/// </list>
/// <para>
/// The selectors below were nevertheless verified against the real tree and are correct:
/// navigation items are <c>ListItem</c> elements named 主页 / 游戏库 / 存档管理 / 补丁中心 /
/// 刮削进度 / 错误报告 / 设置, with class name
/// <c>Microsoft.UI.Xaml.Controls.NavigationViewItem</c>. Note that the previous version of this
/// project searched for class name <c>"NavigationView"</c>, which matches nothing in a WinUI 3
/// tree - which is why its navigation helper silently did nothing while the test still reported
/// "all pages navigated successfully".
/// </para>
/// </summary>
internal sealed class AppSession : IDisposable
{
    /// <summary>The navigation menu entries declared in <c>MainWindow.xaml</c>, in order.</summary>
    public static readonly string[] NavigationItems =
        { "主页", "游戏库", "存档管理", "补丁中心", "刮削进度", "错误报告", "设置" };

    /// <summary>
    /// A string that only the corresponding page renders. Established by dumping the real UI tree
    /// after navigating to each page during the audit.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> PageSignature = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["主页"] = "欢迎使用 Galbox",
        ["游戏库"] = "1 / 1 个游戏",
        ["存档管理"] = "存档管理",
        ["补丁中心"] = "补丁",
        ["刮削进度"] = "元数据刮削",
        ["错误报告"] = "错误报告",
        ["设置"] = "保存设置"
    };

    private readonly Process _process;
    private readonly UIA3Automation _automation;

    private AppSession(Process process, UIA3Automation automation, Window window, TimeSpan elapsed)
    {
        _process = process;
        _automation = automation;
        Window = window;
        TimeToWindow = elapsed;
    }

    /// <summary>The UIA window of the running application.</summary>
    public Window Window { get; }

    /// <summary>How long the window took to appear.</summary>
    public TimeSpan TimeToWindow { get; }

    /// <summary>The launched process.</summary>
    public Process Process => _process;

    /// <summary>
    /// Launches the application, waits for its window and attaches UIA to it. Throws when no window
    /// appears, because every control-level step is meaningless without one.
    /// </summary>
    public static AppSession Start(TimeSpan windowTimeout)
    {
        var repositoryRoot = RepoLayout.FindRepositoryRoot()
            ?? throw new InvalidOperationException($"Could not find {RepoLayout.SolutionMarker} above {AppContext.BaseDirectory}.");

        var executable = RepoLayout.FindApplicationExecutable(repositoryRoot, out _)
            ?? throw new InvalidOperationException("Galbox.App.exe not found. Build the solution first.");

        RepoLayout.KillLeftoverInstancesOfThisWorkTree(repositoryRoot);

        var process = Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Process.Start returned null");

        var stopwatch = Stopwatch.StartNew();
        IntPtr handle = IntPtr.Zero;

        while (stopwatch.Elapsed < windowTimeout)
        {
            Thread.Sleep(200);
            if (process.HasExited)
            {
                process.Dispose();
                throw new InvalidOperationException("The application exited before showing a window.");
            }

            process.Refresh();
            if (process.MainWindowHandle != IntPtr.Zero)
            {
                handle = process.MainWindowHandle;
                break;
            }
        }

        if (handle == IntPtr.Zero)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            process.Dispose();
            throw new InvalidOperationException($"No window appeared within {windowTimeout.TotalSeconds:F0} s.");
        }

        // Let the first page finish rendering before UIA starts walking the tree.
        Thread.Sleep(2000);

        var automation = new UIA3Automation();
        var window = automation.GetDesktop()
            .FindAllChildren()
            .First(child => ProcessIdOf(child) == process.Id)
            .AsWindow();

        return new AppSession(process, automation, window, stopwatch.Elapsed);
    }

    /// <summary>Finds a navigation menu entry by its visible label.</summary>
    public AutomationElement? FindNavigationItem(string label) =>
        Window.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
              .FirstOrDefault(item => SafeName(item) == label);

    /// <summary>All labels currently present in the navigation menu.</summary>
    public IReadOnlyList<string> NavigationItemLabels() =>
        Window.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
              .Select(SafeName)
              .ToList();

    /// <summary>
    /// Activates a navigation entry and reports whether the expected page became visible.
    ///
    /// The UIA <c>SelectionItem</c> pattern is preferred over <c>Click()</c> because it does not
    /// depend on the window being unobstructed - see the measured limitation in the class remarks.
    /// </summary>
    public bool NavigateTo(string label, out string observed)
    {
        observed = string.Empty;

        var item = FindNavigationItem(label);
        if (item is null)
        {
            observed = "navigation item not found in the accessibility tree";
            return false;
        }

        try
        {
            if (item.Patterns.SelectionItem.IsSupported)
            {
                item.Patterns.SelectionItem.Pattern.Select();
            }
            else
            {
                item.Click();
            }
        }
        catch (Exception ex)
        {
            observed = $"activating the item threw {ex.GetType().Name}: {ex.Message}";
            return false;
        }

        Thread.Sleep(1500);

        var texts = VisibleTexts();
        observed = string.Join(" | ", texts.Where(t => t != "Galbox").Take(6));

        return texts.Any(t => t.Contains(PageSignature[label], StringComparison.Ordinal));
    }

    /// <summary>Every text string currently rendered in the window.</summary>
    public IReadOnlyList<string> VisibleTexts() =>
        Window.FindAllDescendants()
              .Where(element => SafeControlType(element) == ControlType.Text)
              .Select(SafeName)
              .Where(name => !string.IsNullOrWhiteSpace(name))
              .ToList();

    /// <summary>Number of automation elements below the window; 0 means the tree stopped responding.</summary>
    public int DescendantCount() => Window.FindAllDescendants().Length;

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(10_000);
            }
        }
        catch
        {
            // Best effort: the next launch terminates leftovers of this work tree.
        }

        _process.Dispose();
        _automation.Dispose();
    }

    private static int ProcessIdOf(AutomationElement element)
    {
        try { return element.Properties.ProcessId.ValueOrDefault; } catch { return -1; }
    }

    private static string SafeName(AutomationElement element)
    {
        try { return element.Name ?? string.Empty; } catch { return string.Empty; }
    }

    private static ControlType SafeControlType(AutomationElement element)
    {
        try { return element.ControlType; } catch { return ControlType.Custom; }
    }
}
