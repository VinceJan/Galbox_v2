using Galbox.App.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;

namespace Galbox.App;

/// <summary>
/// Main window with navigation view.
/// </summary>
public sealed partial class MainWindow : Window
{
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
    }

    /// <summary>
    /// Initializes the navigation service.
    /// Called from App.xaml.cs after the window is created.
    /// </summary>
    public void InitializeNavigation()
    {
        _navigationService = App.Current.Services.GetRequiredService<INavigationService>();
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