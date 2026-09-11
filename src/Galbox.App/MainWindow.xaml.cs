using Galbox.App.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.InteropServices;
using WinRT.Interop;

namespace Galbox.App;

/// <summary>
/// Main window with navigation view.
/// </summary>
public sealed partial class MainWindow : Window
{
    #region Win32 API for Window Subclass

    private const int WM_HOTKEY = 0x0312;
    private const uint SUBCLASS_ID = 12345;

    // NOTE: SetWindowSubclass / RemoveWindowSubclass / DefSubclassProc are exported by
    // comctl32.dll, NOT user32.dll. Declaring them against user32.dll compiles fine but
    // throws EntryPointNotFoundException at first call (i.e. in the MainWindow constructor),
    // which used to be swallowed by the startup exception handler and left the app running
    // with no visible window. Verified by binary export scan of both DLLs.
    [DllImport("comctl32.dll", EntryPoint = "SetWindowSubclass", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProcDelegate pSubclassProc, uint uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll", EntryPoint = "RemoveWindowSubclass", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProcDelegate pSubclassProc, uint uIdSubclass);

    [DllImport("comctl32.dll", EntryPoint = "DefSubclassProc", SetLastError = true)]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    private delegate IntPtr SubclassProcDelegate(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, uint uIdSubclass, IntPtr dwRefData);

    private SubclassProcDelegate? _subclassProc;
    private bool _subclassInstalled;

    #endregion

    /// <summary>
    /// The navigation service for page navigation.
    /// </summary>
    private INavigationService? _navigationService;

    /// <summary>
    /// Gets the window handle for Win32 API usage.
    /// </summary>
    public IntPtr WindowHandle => WindowNative.GetWindowHandle(this);

    /// <summary>
    /// The window caption, shown in the taskbar, Alt-Tab and the window switcher.
    /// </summary>
    /// <remarks>
    /// The window draws its own title bar with a TextBlock, but that only paints inside the
    /// client area: without an explicit <see cref="Window.Title"/> the shell falls back to the
    /// host default and the taskbar entry reads "WinUI Desktop". The version is appended so a
    /// bug report can identify the build from a screenshot.
    /// </remarks>
    public static string ApplicationTitle => BuildApplicationTitle();

    /// <summary>
    /// Creates the MainWindow.
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();

        // Title is set before the window is activated so the taskbar never shows the host
        // default ("WinUI Desktop") even for one frame.
        Title = ApplicationTitle;

        // Set custom title bar
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // Set initial size.
        //
        // AppWindow sizes are PHYSICAL pixels. With a fixed 1200x800 the application opened an
        // 800x533-logical window on this machine's 150% display, which left the patch centre's
        // right-hand panel about 130 logical pixels wide and clipped it at the window edge
        // (measured with UI Automation: the 补丁中心 card's content extended past the window's right
        // border). Scale the design size by the window's own DPI and clamp it to the work area so the
        // window is actually the size the layout assumes.
        var appWindow = AppWindow;
        appWindow.Resize(CalculateInitialWindowSize(appWindow));

        // Subscribe to window closed event for cleanup
        Closed += OnWindowClosed;

        // Setup window subclass for WM_HOTKEY handling
        SetupWindowSubclass();
    }

    /// <summary>Design size of the main window, in logical (96 dpi) pixels.</summary>
    private const int DesignWidth = 1200;

    /// <summary>Design height of the main window, in logical (96 dpi) pixels.</summary>
    private const int DesignHeight = 800;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public System.Drawing.Rectangle rcMonitor;
        public System.Drawing.Rectangle rcWork;
        public uint dwFlags;
    }

    /// <summary>
    /// Computes the startup size in physical pixels: the design size scaled by this window's DPI and
    /// clamped to the monitor's work area, so the window is never larger than the screen and never
    /// smaller than a usable minimum.
    /// </summary>
    /// <remarks>
    /// The clamp uses <c>GetMonitorInfo</c> rather than WinUI's <c>DisplayArea</c>: on this machine
    /// several virtual display adapters are present and <c>DisplayArea</c> reported a work area
    /// (1800x1200) larger than the desktop the window actually appears on (1707x960), so the clamp
    /// silently did nothing.
    /// </remarks>
    private Windows.Graphics.SizeInt32 CalculateInitialWindowSize(Microsoft.UI.Windowing.AppWindow appWindow)
    {
        var scale = 1.0;
        var hwnd = WindowHandle;
        if (hwnd != IntPtr.Zero)
        {
            var dpi = GetDpiForWindow(hwnd);
            if (dpi > 0) scale = dpi / 96.0;
        }

        var width = (int)Math.Round(DesignWidth * scale);
        var height = (int)Math.Round(DesignHeight * scale);

        if (hwnd != IntPtr.Zero)
        {
            var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            if (monitor != IntPtr.Zero)
            {
                var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
                if (GetMonitorInfo(monitor, ref info))
                {
                    var workWidth = info.rcWork.Right - info.rcWork.Left;
                    var workHeight = info.rcWork.Bottom - info.rcWork.Top;
                    if (workWidth > 0) width = Math.Min(width, workWidth);
                    if (workHeight > 0) height = Math.Min(height, workHeight);
                }
            }
        }

        return new Windows.Graphics.SizeInt32(Math.Max(960, width), Math.Max(640, height));
    }

    /// <summary>
    /// Composes the window caption from the product name and the assembly version.
    /// </summary>
    internal static string BuildApplicationTitle()
    {
        var version = typeof(MainWindow).Assembly.GetName().Version;

        // A version of 0.0.0.0 means the build did not stamp one; showing "Galbox 0.0.0"
        // would be worse than showing no version at all.
        if (version is null || version.Major <= 0)
        {
            return "Galbox";
        }

        return $"Galbox {version.ToString(3)}";
    }

    /// <summary>
    /// Handles window closed event to cleanup subclass.
    /// </summary>
    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        RemoveWindowSubclass();
    }

    /// <summary>
    /// Sets up window subclass to capture WM_HOTKEY messages.
    /// </summary>
    private void SetupWindowSubclass()
    {
        var hWnd = WindowHandle;
        if (hWnd == IntPtr.Zero)
        {
            return;
        }

        _subclassProc = SubclassProc;
        _subclassInstalled = SetWindowSubclass(hWnd, _subclassProc, SUBCLASS_ID, IntPtr.Zero);

        if (!_subclassInstalled)
        {
            var error = Marshal.GetLastWin32Error();
            System.Diagnostics.Debug.WriteLine($"Failed to set window subclass. Error: {error}");
        }
    }

    /// <summary>
    /// Subclass procedure to handle WM_HOTKEY messages.
    /// </summary>
    private IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, uint uIdSubclass, IntPtr dwRefData)
    {
        if (uMsg == WM_HOTKEY)
        {
            // Forward hotkey to ProcessMonitorService
            try
            {
                var processMonitor = App.Services.GetService<IProcessMonitorService>() as ProcessMonitorService;
                processMonitor?.HandleHotkeyPressed();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error handling hotkey: {ex.Message}");
            }
            return IntPtr.Zero;
        }

        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    /// <summary>
    /// Removes the window subclass when closing.
    /// </summary>
    private void RemoveWindowSubclass()
    {
        if (_subclassInstalled && _subclassProc != null)
        {
            var hWnd = WindowHandle;
            RemoveWindowSubclass(hWnd, _subclassProc, SUBCLASS_ID);
            _subclassInstalled = false;
        }
    }

    /// <summary>
    /// Initializes the navigation service.
    /// Called from App.xaml.cs after the window is created.
    /// </summary>
    public void InitializeNavigation()
    {
        _navigationService = App.Services.GetRequiredService<INavigationService>();
        _navigationService.Initialize(ContentFrame, NavigationView);

        // Navigate to home page on startup
        _navigationService.NavigateTo("Home");

        // Select the Home navigation item
        _navigationService.SelectNavigationItem("Home");

        RunRequestedNavigationSmokeTest();
    }

    /// <summary>
    /// Environment variable that asks the application to load every listed page at startup.
    /// </summary>
    /// <remarks>
    /// A page whose XAML cannot be loaded only fails when somebody navigates to it, and the startup
    /// checks (A9/A18) never leave the home page. This diagnostic loads the listed navigation keys
    /// one by one on the UI thread and writes the outcome to the startup log, so an unattended run
    /// can prove that every page still parses and constructs - including pages the harness cannot
    /// reach headlessly. Format: <c>Library,SaveManager,GameDetail:1</c> (a page may take its
    /// navigation parameter after a colon). It is inert unless the variable is set.
    /// </remarks>
    public const string NavigationSmokeTestVariable = "GALBOX_NAVIGATION_SMOKE";

    /// <summary>
    /// Optional file that receives the smoke-test result lines.
    /// </summary>
    /// <remarks>
    /// The startup log is a day-file shared by every running instance of the application (including
    /// instances started from other checkouts on the same machine), so a caller that must attribute
    /// the result to the process it started points this variable at a file of its own.
    /// </remarks>
    public const string NavigationSmokeResultFileVariable = "GALBOX_NAVIGATION_SMOKE_FILE";

    /// <summary>Log marker written when a page loaded successfully during the smoke test.</summary>
    public const string NavigationSmokeMarker = "Navigation smoke";

    private void RunRequestedNavigationSmokeTest()
    {
        var requested = Environment.GetEnvironmentVariable(NavigationSmokeTestVariable);

        if (string.IsNullOrWhiteSpace(requested) || _navigationService is null)
        {
            return;
        }

        var resultFile = Environment.GetEnvironmentVariable(NavigationSmokeResultFileVariable);

        foreach (var entry in requested.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = entry.Split(':', 2);
            var key = parts[0];
            var parameter = parts.Length > 1 && int.TryParse(parts[1], out var id) ? (object)id : null;
            string line;

            try
            {
                _navigationService.NavigateTo(key, parameter);

                var loaded = _navigationService.CurrentPageType?.Name ?? "(none)";
                line = $"{NavigationSmokeMarker}: {key}=OK (page={loaded})";
            }
            catch (Exception ex)
            {
                // Never let the diagnostic take the application down; the line is the result.
                line = $"{NavigationSmokeMarker}: {key}=FAILED ({ex.GetType().Name}: {ex.Message.Split('\n')[0]})";
            }

            StartupDiagnostics.Log(line);
            AppendSmokeResult(resultFile, line);
        }

        // Leave the application on the page it was going to show anyway.
        try
        {
            _navigationService.NavigateTo("Home");
            _navigationService.SelectNavigationItem("Home");
        }
        catch (Exception ex)
        {
            var line = $"{NavigationSmokeMarker}: return-to-Home FAILED ({ex.GetType().Name}: {ex.Message.Split('\n')[0]})";
            StartupDiagnostics.Log(line);
            AppendSmokeResult(resultFile, line);
        }
    }

    /// <summary>Appends one smoke line to the caller-provided result file, if any.</summary>
    private static void AppendSmokeResult(string? resultFile, string line)
    {
        if (string.IsNullOrWhiteSpace(resultFile))
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(resultFile);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // One write per line, so the caller can watch the file grow while the application runs.
            File.AppendAllText(resultFile, line + Environment.NewLine);
        }
        catch
        {
            // Diagnostics must never break the application.
        }
    }

    /// <summary>
    /// Handles navigation item invocation.
    /// </summary>
    private void NavigationView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer != null)
        {
            var tag = args.InvokedItemContainer.Tag?.ToString();

            // Use navigation service for page navigation
            if (_navigationService != null && !string.IsNullOrEmpty(tag))
            {
                _navigationService.NavigateTo(tag);
            }
        }

        // Handle Settings item
        if (args.IsSettingsInvoked)
        {
            if (_navigationService != null)
            {
                _navigationService.NavigateTo("Settings");
            }
        }
    }

    /// <summary>
    /// Handles back navigation.
    /// </summary>
    private void NavigationView_BackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
    {
        if (_navigationService != null && _navigationService.CanGoBack)
        {
            _navigationService.GoBack();
        }
    }
}