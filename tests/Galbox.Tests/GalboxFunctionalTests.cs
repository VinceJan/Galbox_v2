using Galbox.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace Galbox.Tests;

/// <summary>
/// The step-by-step functional walk-through, rewritten so that every step either verifies
/// something real or says out loud that it does not.
///
/// <para>
/// AUDIT OF THE PREVIOUS VERSION. The old file wrote "Step N PASSED: ..." lines that no
/// observation backed. Concretely:
/// </para>
/// <list type="bullet">
///   <item><description>
///     six sites in the old <c>FullIntegrationTest</c> handed <c>return true</c> to the runner as the
///     result of a step, so every step was reported successful no matter what happened;
///   </description></item>
///   <item><description>
///     the old <c>RunTestStep</c> printed <c>"{name}: PASSED"</c> without looking at the value it had
///     just been given, so it logged PASSED even for a step that returned <c>false</c>;
///   </description></item>
///   <item><description>
///     the old navigation helper searched for the class name <c>"NavigationView"</c>, which matches
///     nothing in a WinUI 3 accessibility tree, so it silently clicked nothing - and the test still
///     printed "Step 7 PASSED: All pages navigated successfully";
///   </description></item>
///   <item><description>
///     the old <c>AppPath</c> was the hard-coded absolute path
///     <c>E:\tmp\Galbox_v2\src\...\Galbox.App.exe</c>, i.e. a different checkout, so the binary under
///     test was not necessarily the one built from these sources;
///   </description></item>
///   <item><description>
///     the old file's "Step 8: Special Features" claimed to test the boss key, screenshots and error
///     checking while only listing the text of whatever elements happened to exist.
///   </description></item>
/// </list>
///
/// <para>
/// CURRENT STATUS OF EACH STEP. What is real is asserted; what cannot be verified here is an
/// explicit <c>Skip</c> carrying its reason, never a silent pass.
/// </para>
/// <list type="bullet">
///   <item><description>Step 1 start the application - REAL, now <see cref="AppLaunchSmokeTests"/>.</description></item>
///   <item><description>Step 2 add a game directory - SKIP: needs the interactive shell folder picker.</description></item>
///   <item><description>Step 3 game library - SKIP: control-level UIA is not a reliable gate here.</description></item>
///   <item><description>Step 6 save manager - SKIP: same reason.</description></item>
///   <item><description>Step 7 page navigation - SKIP: same reason.</description></item>
///   <item><description>Step 8 boss key / screenshots - SKIP: needs a running game and real global hotkey input.</description></item>
///   <item><description>Step 9 logs and errors - REAL, verified without UIA.</description></item>
///   <item><description>Full integration aggregator - REMOVED: it aggregated steps that could not fail.</description></item>
/// </list>
/// </summary>
public sealed class GalboxFunctionalTests
{
    private readonly ITestOutputHelper _output;

    public GalboxFunctionalTests(ITestOutputHelper output) => _output = output;

    // =====================================================================================
    // Step 2 - add a game directory
    // =====================================================================================

    /// <summary>
    /// SKIPPED. Adding a directory goes through the Windows folder picker, a modal shell dialog owned
    /// by the shell rather than by Galbox. A UIA test can open it but cannot choose a path in a way
    /// that is stable across machines, and the application exposes no headless alternative entry
    /// point. The directory-addition behaviour is therefore NOT verified by this project; the old
    /// file reported the step as "PARTIAL PASS" without verifying anything either.
    /// </summary>
    [Fact(Skip = "SKIP: 需要交互式 Windows 文件夹选择器（shell 对话框），自动化无法稳定选择路径；应用也没有无头入口。"
               + "因此「添加游戏目录」这一行为未被本项目验证。")]
    public void Step2_AddGameDirectory()
    {
        // Deliberately empty. Filling this with navigation-only assertions would recreate the
        // original defect: reporting the step as exercised when the behaviour under test - adding a
        // directory - was never reached.
    }

    // =====================================================================================
    // Steps 3, 6, 7 - control-level navigation
    // =====================================================================================

    /// <summary>
    /// SKIPPED (measurements in <see cref="AppSession"/>). Navigates to the library page and requires
    /// that page's own content to be rendered.
    /// </summary>
    [Fact(Skip = "SKIP: FlaUI 控件级自动化在本机不确定 —— SetForegroundWindow 失败导致 Click() 落在别的窗口上，"
               + "且 UIA 树在一次导航扫描中会整体失效（FindAllDescendants 返回 0），而进程仍存活并正常响应窗口消息。"
               + "实测记录见 Support/AppSession.cs。")]
    public void Step3_VerifyGameLibrary()
    {
        using var session = AppSession.Start(AppLaunchObserver.DefaultWindowTimeout);

        var ok = session.NavigateTo("游戏库", out var observed);
        _output.WriteLine($"library page after navigation: {observed}");
        _output.WriteLine($"descendants: {session.DescendantCount()}");

        Assert.True(ok, $"The library page did not render. Observed text: {observed}");
    }

    /// <summary>
    /// SKIPPED (same reason). Navigates to the save manager page and requires that page's own content
    /// to be rendered.
    /// </summary>
    [Fact(Skip = "SKIP: FlaUI 控件级自动化在本机不确定，理由同 Step3。")]
    public void Step6_TestSaveManager()
    {
        using var session = AppSession.Start(AppLaunchObserver.DefaultWindowTimeout);

        var ok = session.NavigateTo("存档管理", out var observed);
        _output.WriteLine($"save manager page after navigation: {observed}");

        Assert.True(ok, $"The save manager page did not render. Observed text: {observed}");
    }

    /// <summary>
    /// SKIPPED (same reason). Walks every navigation menu entry and requires each one to render its
    /// own page - the check the old file printed as PASSED without performing.
    /// </summary>
    [Fact(Skip = "SKIP: FlaUI 控件级自动化在本机不确定，理由同 Step3。实测中 7 个导航项只有前 3 个能稳定渲染，"
               + "第 4 个（补丁中心）之后整个 UIA 树失效。")]
    public void Step7_TestPageNavigation()
    {
        using var session = AppSession.Start(AppLaunchObserver.DefaultWindowTimeout);

        var labels = session.NavigationItemLabels();
        _output.WriteLine($"navigation menu: {string.Join(", ", labels)}");
        Assert.Equal(AppSession.NavigationItems.Length, labels.Count);

        foreach (var item in AppSession.NavigationItems)
        {
            var ok = session.NavigateTo(item, out var observed);
            _output.WriteLine($"  {item,-6} -> {(ok ? "OK" : "FAILED")} :: {observed}");
            Assert.True(ok, $"Navigation to '{item}' did not render its page. Observed text: {observed}");
        }
    }

    // =====================================================================================
    // Step 8 - boss key, screenshots, error checking
    // =====================================================================================

    /// <summary>
    /// SKIPPED. The boss key is a global hotkey (WM_HOTKEY) that hides the window while a game is
    /// running, and the screenshot feature fires on game exit. Exercising either needs a real game
    /// process registered in the library, a real foreground window to receive the hotkey, and real
    /// keyboard input - none of which this project can produce. The old file listed text elements and
    /// called that "special features tested".
    /// </summary>
    [Fact(Skip = "SKIP: 老板键需要真实前台窗口接收全局热键、截图需要真实游戏进程退出事件，二者都无法在本测试中构造。")]
    public void Step8_TestSpecialFeatures()
    {
        // Deliberately empty - see the Skip reason.
    }

    // =====================================================================================
    // Step 9 - logs and errors (real, no UIA involved)
    // =====================================================================================

    /// <summary>
    /// REAL. Verifies, after a successful launch, that:
    /// <list type="bullet">
    ///   <item><description>the application data folder and a non-empty SQLite database exist;</description></item>
    ///   <item><description>this specific process's startup log reports that it created and activated its main window;</description></item>
    ///   <item><description>the process owns no modal dialog at that point.</description></item>
    /// </list>
    /// The log assertion is keyed on the process's own window handle, and it has to be: the log file
    /// <c>%LocalAppData%\Galbox\logs\startup-*.log</c> is machine-global and every Galbox checkout on
    /// this machine appends to it - observed during this audit, when another work tree's instance
    /// wrote into the same file - so an unqualified "the log says the application started" assertion
    /// can be satisfied by a different application instance.
    /// </summary>
    [Fact]
    public void Step9_StartupDiagnosticsAndDatabaseAreReal()
    {
        var observations = AppLaunchObserver.Launch(
            AppLaunchObserver.DefaultWindowTimeout,
            AppLaunchObserver.DefaultSettlePeriod);

        _output.WriteLine(observations.Report());

        Assert.False(observations.StartFailed, $"The application could not be started: {observations.StartError}");
        Assert.NotNull(observations.CandidateWindow);

        // ------------------------------------------------------------------------------- database
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Galbox");
        var database = Path.Combine(appData, "galbox.db");

        Assert.True(
            File.Exists(database),
            $"The application database is missing: {database}\n" + observations.Report());

        var fileInfo = new FileInfo(database);
        Assert.True(
            fileInfo.Length > 0,
            $"The database file is empty: {database}\n" + observations.Report());

        // A SQLite database always starts with a 16-byte header. Checking it is what separates
        // "a file with that name exists" from "the database is a usable database".
        //
        // FileShare.ReadWrite is deliberate: the running application (and any other Galbox instance
        // on this machine) legitimately holds this SQLite file open, so demanding exclusive access
        // would fail for a reason that has nothing to do with the assertion. This is not a relaxed
        // check - the header bytes are still verified exactly as before; only the incidental
        // file-locking requirement was wrong.
        var header = new byte[16];
        int read;
        using (var stream = new FileStream(
                   database, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            read = stream.Read(header, 0, header.Length);
        }

        Assert.True(
            read == header.Length,
            $"Could only read {read} of {header.Length} header bytes from '{database}', so it is not "
          + "a readable database file.\n" + observations.Report());

        Assert.True(
            System.Text.Encoding.ASCII.GetString(header).StartsWith("SQLite format 3", StringComparison.Ordinal),
            $"'{database}' is not a SQLite database (header: {BitConverter.ToString(header)}).");
        _output.WriteLine($"database: {database} ({fileInfo.Length} bytes, valid SQLite header)");

        // ------------------------------------------------ this process reached window activation
        Assert.True(
            observations.LogConfirmsThisProcessActivatedItsWindow,
            $"No startup-log line reports that THIS process (hwnd 0x{observations.ObservedHandle.ToInt64():X}) "
          + "created and activated its window.\n" + observations.Report());

        // --------------------------------------------------------------------- no failure dialog
        Assert.Equal(0, observations.DialogCount);
    }
}
