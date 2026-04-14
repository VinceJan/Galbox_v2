using Galbox.App.ViewModels;
using Galbox.Data.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;
using WinRT;

namespace Galbox.App.Views;

/// <summary>
/// Library/Game Library page for Galbox application.
/// Supports grid and table view with search and filtering.
/// </summary>
public sealed partial class LibraryPage : Page
{
    /// <summary>
    /// The ViewModel for this page, obtained via DI.
    /// </summary>
    public LibraryViewModel ViewModel { get; }

    /// <summary>
    /// Currently hovered game for quick launch button.
    /// </summary>
    private GameInfo? _hoveredGame;

    /// <summary>
    /// Creates a LibraryPage and obtains the ViewModel via DI.
    /// </summary>
    public LibraryPage()
    {
        InitializeComponent();

        // Get ViewModel from DI container
        ViewModel = App.Current.Services.GetRequiredService<LibraryViewModel>();

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
            await ViewModel.LoadDataAsync(e.Parameter);
        }
        catch (System.Exception ex)
        {
            ViewModel.ErrorMessage = $"Failed to load page: {ex.Message}";
        }
    }

    /// <summary>
    /// Handles game item click in both grid and table views.
    /// </summary>
    private void OnGameItemClick(object sender, RoutedEventArgs e)
    {
        GameInfo? game = null;

        // Get the game from different sender types
        if (sender is Button button && button.DataContext is GameInfo buttonGame)
        {
            game = buttonGame;
        }
        else if (sender is ListView listView && e is ItemClickEventArgs args)
        {
            game = args.ClickedItem as GameInfo;
        }

        if (game != null)
        {
            ViewModel.NavigateToGameCommand.Execute(game);
        }
    }

    /// <summary>
    /// Handles view toggle button click.
    /// </summary>
    private void OnToggleViewClick(object sender, RoutedEventArgs e)
    {
        ViewModel.ToggleViewCommand.Execute(null);
    }

    /// <summary>
    /// Handles refresh button click.
    /// </summary>
    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        ViewModel.RefreshCommand.Execute(null);
    }

    /// <summary>
    /// Handles add game button click - opens folder picker.
    /// </summary>
    private async void OnAddGameClick(object sender, RoutedEventArgs e)
    {
        try
        {
            // Create folder picker
            var folderPicker = new FolderPicker();
            folderPicker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
            folderPicker.FileTypeFilter.Add("*"); // Required for folder picker

            // Get the window handle for the picker
            var window = GetWindowForElement(this);
            if (window != null)
            {
                // Initialize with window handle
                var hWnd = window.As<IWindowNative>().WindowHandle;
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
    /// Handles quick launch button click.
    /// </summary>
    private async void OnQuickLaunchClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is Button button && button.Tag is GameInfo game)
            {
                await ViewModel.QuickLaunchGameCommand.ExecuteAsync(game);
            }
        }
        catch (System.Exception ex)
        {
            ViewModel.ErrorMessage = $"Failed to quick launch: {ex.Message}";
        }
    }

    /// <summary>
    /// Handles game card pointer entered - shows quick launch button.
    /// </summary>
    private void OnGameCardPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid grid)
        {
            // Find the quick launch button in the grid
            var quickLaunchButton = FindQuickLaunchButton(grid);
            if (quickLaunchButton != null)
            {
                quickLaunchButton.Visibility = Visibility.Visible;
            }
        }
    }

    /// <summary>
    /// Handles game card pointer exited - hides quick launch button.
    /// </summary>
    private void OnGameCardPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid grid)
        {
            // Find the quick launch button in the grid
            var quickLaunchButton = FindQuickLaunchButton(grid);
            if (quickLaunchButton != null)
            {
                quickLaunchButton.Visibility = Visibility.Collapsed;
            }
        }
    }

    /// <summary>
    /// Handles quick launch button pointer entered - keeps button visible.
    /// </summary>
    private void OnQuickLaunchButtonEnter(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Button button)
        {
            button.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// Handles quick launch button pointer exited - hides button.
    /// </summary>
    private void OnQuickLaunchButtonExit(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Button button)
        {
            button.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Handles table header click for sorting.
    /// </summary>
    private void OnTableHeaderClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string columnName)
        {
            ViewModel.SetSortFromColumn(columnName);
        }
    }

    /// <summary>
    /// Finds the quick launch button in a grid.
    /// </summary>
    private Button? FindQuickLaunchButton(Grid grid)
    {
        foreach (var child in grid.Children)
        {
            if (child is Button button && button.Name == "QuickLaunchOverlayButton")
            {
                return button;
            }
        }
        return null;
    }

    /// <summary>
    /// Gets the parent window for an element.
    /// In WinUI3, we use App.Current.MainWindow instead of walking the visual tree.
    /// </summary>
    private Window? GetWindowForElement(UIElement element)
    {
        // WinUI3: Use the main window from App.Current
        // The visual tree approach doesn't reliably find Window in WinUI3
        return (App.Current as App)?.MainWindow;
    }
}

/// <summary>
/// Interface for getting the window handle.
/// Required for WinUI 3 folder/file pickers.
/// </summary>
[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("EECDBF0E-BA22-4221-9C1D-13A9F8EF7D99")]
internal interface IWindowNative
{
    IntPtr WindowHandle { get; }
}