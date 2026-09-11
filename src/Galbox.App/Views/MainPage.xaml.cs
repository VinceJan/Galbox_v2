using Galbox.App.ViewModels;
using Galbox.Data.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Galbox.App.Views;

/// <summary>
/// Main/Home page for Galbox application.
/// Displays quick launch, recent games, and currently playing sections.
/// </summary>
public sealed partial class MainPage : Page
{
    /// <summary>
    /// The ViewModel for this page, obtained via DI.
    /// </summary>
    public MainViewModel ViewModel { get; }

    /// <summary>
    /// Creates a MainPage and obtains the ViewModel via DI.
    /// </summary>
    public MainPage()
    {
        InitializeComponent();

        // Get ViewModel from DI container
        ViewModel = App.Services.GetRequiredService<MainViewModel>();

        // Set DataContext for any binding fallback
        DataContext = ViewModel;

        // Subscribe to folder picker request event
        ViewModel.RequestFolderPicker += OnRequestFolderPicker;
    }

    /// <summary>
    /// Handles navigation to this page.
    /// </summary>
    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        try
        {
            await ViewModel.LoadDataAsync();
        }
        catch (System.Exception ex)
        {
            ViewModel.ErrorMessage = $"Failed to load page: {ex.Message}";
        }
    }

    /// <summary>
    /// Handles game item click in lists.
    /// </summary>
    private void OnGameItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.DataContext is GameInfo game)
        {
            ViewModel.NavigateToGameCommand.Execute(game);
        }
    }

    /// <summary>
    /// Handles add game button click from the UI.
    /// </summary>
    private void OnAddGameClick(object sender, RoutedEventArgs e)
    {
        ViewModel.AddGameCommand.Execute(null);
    }

    /// <summary>
    /// Handles folder picker request from ViewModel.
    /// </summary>
    private async void OnRequestFolderPicker(object? sender, EventArgs e)
    {
        try
        {
            // Create folder picker
            var folderPicker = new FolderPicker();
            folderPicker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
            folderPicker.FileTypeFilter.Add("*"); // Required for folder picker

            // Get the window handle for the picker using WinUI 3 built-in method
            var window = GetWindowForElement(this);
            if (window != null)
            {
                // Use WindowNative.GetWindowHandle - WinUI 3 built-in method
                var hWnd = WindowNative.GetWindowHandle(window);
                InitializeWithWindow.Initialize(folderPicker, hWnd);

                // Show picker
                var folder = await folderPicker.PickSingleFolderAsync();
                if (folder != null)
                {
                    await ViewModel.AddGameAsync(folder.Path);
                }
            }
        }
        catch (System.Exception ex)
        {
            ViewModel.ErrorMessage = $"Failed to add game: {ex.Message}";
        }
    }

    /// <summary>
    /// Gets the parent window for an element.
    /// In WinUI3, we use App.Current.MainWindow instead of walking the visual tree.
    /// </summary>
    private Window? GetWindowForElement(UIElement element)
    {
        // WinUI3: Use the main window from App.Current
        return (App.Current as App)?.MainWindow;
    }
}