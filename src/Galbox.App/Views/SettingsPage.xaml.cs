using Galbox.App.ViewModels;
using Galbox.Data.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;
using WinRT;
using WinRT.Interop;

namespace Galbox.App.Views;

/// <summary>
/// Settings page for Galbox application.
/// Allows configuration of various app settings.
/// </summary>
public sealed partial class SettingsPage : Page
{
    /// <summary>
    /// The ViewModel for this page, obtained via DI.
    /// </summary>
    public SettingsViewModel ViewModel { get; }

    /// <summary>
    /// Creates a SettingsPage and obtains the ViewModel via DI.
    /// </summary>
    public SettingsPage()
    {
        InitializeComponent();

        // Get ViewModel from DI container
        ViewModel = App.Services.GetRequiredService<SettingsViewModel>();

        // Set DataContext for any binding fallback
        DataContext = ViewModel;
    }

    /// <summary>
    /// Handles navigation to this page.
    /// </summary>
    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        try
        {
            await ViewModel.LoadSettingsAsync();
        }
        catch (System.Exception ex)
        {
            ViewModel.ErrorMessage = $"Failed to load settings: {ex.Message}";
        }
    }

    /// <summary>
    /// Handles move source up button click.
    /// </summary>
    private void OnMoveSourceUpClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is SourcePriorityItem item)
        {
            ViewModel.MoveSourceUpCommand.Execute(item);
        }
    }

    /// <summary>
    /// Handles move source down button click.
    /// </summary>
    private void OnMoveSourceDownClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is SourcePriorityItem item)
        {
            ViewModel.MoveSourceDownCommand.Execute(item);
        }
    }

    /// <summary>
    /// Handles Bangumi OAuth login button click.
    /// </summary>
    private async void OnBangumiLoginClick(object sender, RoutedEventArgs e)
    {
        // TODO: Implement OAuth login flow for Bangumi
        // This would involve opening a browser window for authentication
        // and handling the callback to receive the access token

        // For now, show a placeholder message
        var dialog = new ContentDialog
        {
            Title = "Bangumi Login",
            Content = "OAuth login will be implemented in a future update. For now, please use the API key option.",
            CloseButtonText = "OK",
            XamlRoot = XamlRoot
        };

        await dialog.ShowAsync();
    }

    /// <summary>
    /// Handles Bangumi API key button click.
    /// </summary>
    private async void OnBangumiApiKeyClick(object sender, RoutedEventArgs e)
    {
        // Show a dialog to enter API key
        var apiKeyTextBox = new TextBox
        {
            PlaceholderText = "Enter your Bangumi API key",
            Width = 300
        };

        var dialog = new ContentDialog
        {
            Title = "Bangumi API Key",
            Content = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = "Enter your Bangumi API access token:",
                        Margin = new Thickness(0, 0, 0, 8)
                    },
                    apiKeyTextBox
                }
            },
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot
        };

        var result = await dialog.ShowAsync();

        if (result == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(apiKeyTextBox.Text))
        {
            ViewModel.BangumiAccessToken = apiKeyTextBox.Text;
            // Note: User needs to click "Save Settings" to persist changes
        }
    }

    /// <summary>
    /// Handles select backup path button click.
    /// </summary>
    private async void OnSelectBackupPathClick(object sender, RoutedEventArgs e)
    {
        try
        {
            // Create folder picker
            var folderPicker = new FolderPicker();
            folderPicker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
            folderPicker.FileTypeFilter.Add("*"); // Required for folder picker

            // Get the window handle for the picker
            var hWnd = GetWindowHandle();
            InitializeWithWindow.Initialize(folderPicker, hWnd);

            // Show picker
            var folder = await folderPicker.PickSingleFolderAsync();
            if (folder != null)
            {
                ViewModel.DefaultBackupPath = folder.Path;
                // Note: User needs to click "Save Settings" to persist changes
            }
        }
        catch (System.Exception ex)
        {
            ViewModel.ErrorMessage = $"Failed to select folder: {ex.Message}";
        }
    }

    /// <summary>
    /// Gets the window handle for Win32 API usage.
    /// </summary>
    private IntPtr GetWindowHandle()
    {
        // Get the main window from App.Current
        var mainWindow = (App.Current as App)?.MainWindow;
        if (mainWindow != null)
        {
            var windowHandle = mainWindow.WindowHandle;
            return windowHandle;
        }

        return IntPtr.Zero;
    }
}