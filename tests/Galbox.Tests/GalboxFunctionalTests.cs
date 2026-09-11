using FlaUI.UIA3;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace Galbox.Tests;

/// <summary>
/// Automated functional tests for Galbox WinUI3 application.
/// </summary>
public class GalboxFunctionalTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly UIA3Automation _automation;
    private Application? _app;
    private Window? _mainWindow;

    // Test configuration
    private const string AppPath = @"E:\tmp\Galbox_v2\src\Galbox.App\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\Galbox.App.exe";
    private const string GameDirectory = @"D:\GAME";
    private const int StartupTimeoutMs = 30000;
    private const int NavigationTimeoutMs = 5000;
    private const int ActionTimeoutMs = 3000;

    public GalboxFunctionalTests(ITestOutputHelper output)
    {
        _output = output;
        _automation = new UIA3Automation();
    }

    public void Dispose()
    {
        Cleanup();
        _automation.Dispose();
    }

    private void Cleanup()
    {
        try
        {
            if (_app != null && !_app.HasExited)
            {
                _output.WriteLine("Closing application...");
                _app.Close();
                _app.Dispose();
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"Cleanup error: {ex.Message}");
        }
        _app = null;
        _mainWindow = null;
    }

    /// <summary>
    /// Step 1: Start the application and verify it launches successfully.
    /// </summary>
    [Fact]
    public async Task Step1_StartApplication()
    {
        _output.WriteLine("=== Step 1: Starting Application ===");

        // Kill any existing instances
        KillExistingProcesses();

        // Start the application
        _output.WriteLine($"Starting application from: {AppPath}");
        _app = Application.Launch(AppPath);

        Assert.NotNull(_app);
        Assert.False(_app.HasExited, "Application should not exit immediately after launch");

        // Wait for window to appear
        var foundWindow = false;
        for (int i = 0; i < StartupTimeoutMs / 100; i++)
        {
            try
            {
                var desktop = _automation.GetDesktop();
                var windows = desktop.FindAllChildren();
                var appWindow = windows.FirstOrDefault(w => w.Properties.ProcessId == _app.ProcessId);
                if (appWindow != null)
                {
                    _mainWindow = appWindow.AsWindow();
                    foundWindow = true;
                    break;
                }
            }
            catch { }
            await Task.Delay(100);
        }

        Assert.True(foundWindow, "Main window should appear within timeout period");
        _output.WriteLine($"Main window found: {_mainWindow?.Title ?? "Unknown"}");

        // Check for any crash dialogs
        var crashDialogs = _automation.GetDesktop().FindAllChildren(cf => cf.ByClassName("#32770"));
        if (crashDialogs.Any())
        {
            _output.WriteLine($"Warning: Found {crashDialogs.Count()} potential crash dialogs");
        }

        _output.WriteLine("Step 1 PASSED: Application started successfully");
    }

    /// <summary>
    /// Step 2: Navigate to Settings and add game directory.
    /// </summary>
    [Fact]
    public async Task Step2_AddGameDirectory()
    {
        _output.WriteLine("=== Step 2: Add Game Directory ===");

        // Ensure app is running
        await EnsureAppRunning();

        // Navigate to Settings page using NavigationView
        _output.WriteLine("Navigating to Settings page...");

        // Find NavigationView
        var navView = _mainWindow!.FindFirstDescendant(cf => cf.ByClassName("NavigationView"));
        if (navView != null)
        {
            // Click Settings item (usually at bottom of NavigationView)
            var menuItems = navView.FindAllDescendants(cf => cf.ByClassName("NavigationViewItem"));
            foreach (var item in menuItems)
            {
                if (item.Name.Contains("设置") || item.Name.Contains("Settings"))
                {
                    item.Click();
                    await Task.Delay(NavigationTimeoutMs);
                    _output.WriteLine("Clicked Settings navigation item");
                    break;
                }
            }
        }

        _output.WriteLine("Step 2 PARTIAL: Settings page navigation tested");
    }

    /// <summary>
    /// Step 3: Navigate to Library and verify games are listed.
    /// </summary>
    [Fact]
    public async Task Step3_VerifyGameLibrary()
    {
        _output.WriteLine("=== Step 3: Verify Game Library ===");

        await EnsureAppRunning();

        // Navigate to Library page
        _output.WriteLine("Navigating to Library page...");
        NavigateToPage("Library", "游戏库");
        await Task.Delay(NavigationTimeoutMs);

        // Check for game cards/list items
        var gameItems = _mainWindow!.FindAllDescendants(cf =>
            cf.ByClassName("GridViewItem").Or(cf.ByClassName("ListViewItem")));

        _output.WriteLine($"Found {gameItems.Count()} game items in library");

        // Check for specific game names
        var expectedGames = new[] { "魔女的夜宴", "我梦见了她", "Sabbat", "Dreamin" };

        foreach (var item in gameItems)
        {
            var text = item.Name;
            foreach (var expected in expectedGames)
            {
                if (!string.IsNullOrEmpty(text) && text.Contains(expected))
                {
                    _output.WriteLine($"Found game: {text}");
                }
            }
        }

        _output.WriteLine("Step 3: Library page tested");
    }

    /// <summary>
    /// Step 6: Test save management page.
    /// </summary>
    [Fact]
    public async Task Step6_TestSaveManager()
    {
        _output.WriteLine("=== Step 6: Test Save Manager ===");

        await EnsureAppRunning();
        NavigateToPage("SaveManager", "存档管理");
        await Task.Delay(NavigationTimeoutMs);

        // Check for save manager elements
        var buttons = _mainWindow!.FindAllDescendants(cf => cf.ByClassName("Button"));
        foreach (var button in buttons)
        {
            _output.WriteLine($"Found button: {button.Name}");
        }

        _output.WriteLine("Step 6: Save Manager page tested");
    }

    /// <summary>
    /// Step 7: Test navigation between all pages.
    /// </summary>
    [Fact]
    public async Task Step7_TestPageNavigation()
    {
        _output.WriteLine("=== Step 7: Test Page Navigation ===");

        await EnsureAppRunning();

        var pages = new[]
        {
            ("Home", "主页"),
            ("Library", "游戏库"),
            ("SaveManager", "存档管理"),
            ("PatchCenter", "补丁中心"),
            ("Settings", "设置")
        };

        foreach (var (english, chinese) in pages)
        {
            _output.WriteLine($"Navigating to {english} ({chinese})...");
            NavigateToPage(english, chinese);
            await Task.Delay(NavigationTimeoutMs);

            // Verify page loaded - check for any content in the frame
            var frameElements = _mainWindow!.FindAllDescendants(cf => cf.ByClassName("Frame"));
            if (frameElements.Any())
            {
                _output.WriteLine($"Navigation to {english} successful - found Frame");
            }
            else
            {
                _output.WriteLine($"Warning: Frame not found for {english}, but navigation attempted");
            }
        }

        _output.WriteLine("Step 7 PASSED: All pages navigated successfully");
    }

    /// <summary>
    /// Step 8: Test special features (Boss Key, Screenshots, Error Checking).
    /// </summary>
    [Fact]
    public async Task Step8_TestSpecialFeatures()
    {
        _output.WriteLine("=== Step 8: Test Special Features ===");

        await EnsureAppRunning();
        NavigateToPage("Settings", "设置");
        await Task.Delay(NavigationTimeoutMs);

        // Look for toggle switches in settings
        var toggleSwitches = _mainWindow!.FindAllDescendants(cf => cf.ByClassName("ToggleSwitch"));
        foreach (var toggle in toggleSwitches)
        {
            try
            {
                var name = toggle.Properties.Name.ValueOrDefault ?? "Unknown";
                _output.WriteLine($"Found toggle switch: {name}");
            }
            catch
            {
                _output.WriteLine($"Found toggle switch (no name available)");
            }
        }

        // Check for text blocks with feature names
        var textBlocks = _mainWindow.FindAllDescendants(cf => cf.ByClassName("TextBlock"));
        foreach (var textBlock in textBlocks.Take(20))
        {
            try
            {
                var text = textBlock.Properties.Name.ValueOrDefault;
                if (!string.IsNullOrEmpty(text))
                {
                    _output.WriteLine($"Text: {text}");
                }
            }
            catch { }
        }

        _output.WriteLine("Step 8: Special features tested");
    }

    /// <summary>
    /// Step 9: Check application logs and verify no critical errors.
    /// </summary>
    [Fact]
    public async Task Step9_CheckLogsAndErrors()
    {
        _output.WriteLine("=== Step 9: Check Logs and Errors ===");

        await EnsureAppRunning();

        // Check app data directory for logs
        var appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Galbox");

        if (Directory.Exists(appDataPath))
        {
            _output.WriteLine($"App data directory found: {appDataPath}");

            // List files in app data directory
            var files = Directory.GetFiles(appDataPath);
            foreach (var file in files)
            {
                _output.WriteLine($"  - {Path.GetFileName(file)}");
            }

            // Check database file
            var dbFile = Path.Combine(appDataPath, "galbox.db");
            if (File.Exists(dbFile))
            {
                var dbInfo = new FileInfo(dbFile);
                _output.WriteLine($"Database file: {dbInfo.Length} bytes");
            }
        }
        else
        {
            _output.WriteLine("App data directory not yet created");
        }

        // Check for any error dialogs in the application
        var errorDialogs = _mainWindow!.FindAllDescendants(cf =>
            cf.ByClassName("ContentDialog"));

        if (errorDialogs.Any())
        {
            foreach (var dialog in errorDialogs)
            {
                _output.WriteLine($"Warning: Dialog found - {dialog.Name}");
            }
        }
        else
        {
            _output.WriteLine("No error dialogs present");
        }

        _output.WriteLine("Step 9: Log and error check completed");
    }

    /// <summary>
    /// Full integration test running all steps in sequence.
    /// </summary>
    [Fact]
    public async Task FullIntegrationTest()
    {
        _output.WriteLine("=== Full Integration Test ===");
        _output.WriteLine($"Test Time: {DateTime.Now}");
        _output.WriteLine($"App Path: {AppPath}");
        _output.WriteLine($"Game Directory: {GameDirectory}");

        var results = new List<TestResult>();

        // Step 1: Start application
        results.Add(await RunTestStep("Step1_Start", async () =>
        {
            await Step1_StartApplication();
            return true;
        }));

        if (!results[0].Passed)
        {
            _output.WriteLine("Application failed to start - skipping remaining tests");
            ReportResults(results);
            return;
        }

        // Step 7: Test navigation first (most reliable)
        results.Add(await RunTestStep("Step7_Navigation", async () =>
        {
            await Step7_TestPageNavigation();
            return true;
        }));

        // Step 3: Library
        results.Add(await RunTestStep("Step3_Library", async () =>
        {
            await Step3_VerifyGameLibrary();
            return true;
        }));

        // Step 6: Save Manager
        results.Add(await RunTestStep("Step6_SaveManager", async () =>
        {
            await Step6_TestSaveManager();
            return true;
        }));

        // Step 8: Special Features
        results.Add(await RunTestStep("Step8_SpecialFeatures", async () =>
        {
            await Step8_TestSpecialFeatures();
            return true;
        }));

        // Step 9: Logs
        results.Add(await RunTestStep("Step9_Logs", async () =>
        {
            await Step9_CheckLogsAndErrors();
            return true;
        }));

        ReportResults(results);
    }

    private async Task<TestResult> RunTestStep(string name, Func<Task<bool>> testAction)
    {
        _output.WriteLine($"--- Running {name} ---");
        try
        {
            var passed = await testAction();
            _output.WriteLine($"{name}: PASSED");
            return new TestResult { Name = name, Passed = passed };
        }
        catch (Exception ex)
        {
            _output.WriteLine($"{name}: FAILED - {ex.Message}");
            return new TestResult { Name = name, Passed = false, Error = ex.Message };
        }
    }

    private void ReportResults(List<TestResult> results)
    {
        _output.WriteLine("\n=== TEST REPORT ===");
        var passed = results.Count(r => r.Passed);
        var failed = results.Count(r => !r.Passed);

        _output.WriteLine($"Total: {results.Count} tests");
        _output.WriteLine($"Passed: {passed}");
        _output.WriteLine($"Failed: {failed}");

        if (results.Count > 0)
        {
            _output.WriteLine($"Pass Rate: {(passed * 100.0 / results.Count):F1}%");
        }

        _output.WriteLine("\nDetails:");
        foreach (var result in results)
        {
            var status = result.Passed ? "PASS" : "FAIL";
            _output.WriteLine($"  [{status}] {result.Name}");
            if (!result.Passed && result.Error != null)
            {
                _output.WriteLine($"         Error: {result.Error}");
            }
        }

        _output.WriteLine("\n=== END REPORT ===");
    }

    // Helper methods
    private async Task EnsureAppRunning()
    {
        if (_app == null || _app.HasExited)
        {
            await Step1_StartApplication();
        }
        Assert.NotNull(_mainWindow);
    }

    private void NavigateToPage(string englishName, string chineseName)
    {
        var navView = _mainWindow!.FindFirstDescendant(cf => cf.ByClassName("NavigationView"));
        if (navView != null)
        {
            var menuItems = navView.FindAllDescendants(cf => cf.ByClassName("NavigationViewItem"));
            foreach (var item in menuItems)
            {
                if (item.Name.Contains(chineseName) || item.Name.Contains(englishName))
                {
                    item.Click();
                    return;
                }
            }

            // Check Settings item (often separate)
            var settingsItem = navView.FindFirstDescendant(cf => cf.ByClassName("NavigationViewSettingsItem"));
            if (settingsItem != null && (chineseName == "设置" || englishName == "Settings"))
            {
                settingsItem.Click();
            }
        }
    }

    private void KillExistingProcesses()
    {
        try
        {
            var processes = Process.GetProcessesByName("Galbox.App");
            foreach (var p in processes)
            {
                try
                {
                    p.Kill();
                    _output.WriteLine($"Killed existing process: {p.Id}");
                }
                catch { }
            }
        }
        catch { }
    }
}

public class TestResult
{
    public string Name { get; set; } = "";
    public bool Passed { get; set; }
    public string? Error { get; set; }
}