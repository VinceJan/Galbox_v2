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
    /// Creates the MainWindow.
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();

        // Set custom title bar
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // Set initial size
        var appWindow = this.AppWindow;
        appWindow.Resize(new Windows.Graphics.SizeInt32(1200, 800));

        // Subscribe to window closed event for cleanup
        Closed += OnWindowClosed;

        // Setup window subclass for WM_HOTKEY handling
        SetupWindowSubclass();
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