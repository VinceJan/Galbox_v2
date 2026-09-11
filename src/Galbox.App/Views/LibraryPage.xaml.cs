using Galbox.App.ViewModels;
using Galbox.Data.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using System.Runtime.InteropServices;
using Windows.Storage.Pickers;
using WinRT;
using WinRT.Interop;

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
    /// Creates a LibraryPage and obtains the ViewModel via DI.
    /// </summary>
    public LibraryPage()
    {
        InitializeComponent();

        // Get ViewModel from DI container
        ViewModel = App.Services.GetRequiredService<LibraryViewModel>();

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
    /// Handles scan folder button click - opens folder picker for batch scan.
    /// </summary>
    private async void OnScanFolderClick(object sender, RoutedEventArgs e)
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
                    await ViewModel.ScanFolderAsync(folder.Path);
                }
            }
        }
        catch (System.Exception ex)
        {
            ViewModel.ErrorMessage = $"Failed to scan folder: {ex.Message}";
        }
    }

    /// <summary>
    /// Handles the delete button of a game card or table row.
    /// </summary>
    /// <remarks>
    /// W13: removing a game is irreversible from the user's point of view, so it is confirmed with a
    /// dialog that states up front what is and is not deleted, and offers the (default: off) option
    /// to remove the save-backup files as well. The dialog lives here because a ContentDialog needs
    /// a XamlRoot, which a ViewModel does not have.
    /// </remarks>
    private async void OnDeleteGameClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not Button button || button.Tag is not GameInfo game)
            {
                return;
            }

            var deleteBackupFiles = false;

            if (XamlRoot is not null)
            {
                var backupCheckBox = new CheckBox
                {
                    Content = "同时删除该游戏的存档备份文件（此操作不可恢复）",
                    IsChecked = false
                };

                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = $"从库中移除「{game.DisplayName}」？",
                    Content = new StackPanel
                    {
                        Spacing = 12,
                        Children =
                        {
                            new TextBlock
                            {
                                Text = "只从游戏库中移除这条记录：不会删除游戏文件夹、游戏本体或磁盘上的存档。\n"
                                     + "与它关联的角色、文档、媒体、截图、错误记录和备份记录会一并清理。",
                                TextWrapping = TextWrapping.Wrap
                            },
                            backupCheckBox
                        }
                    },
                    PrimaryButtonText = "移除",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Close
                };

                var dialogResult = await dialog.ShowAsync();
                if (dialogResult != ContentDialogResult.Primary)
                {
                    return;
                }

                deleteBackupFiles = backupCheckBox.IsChecked == true;
            }

            await ViewModel.DeleteGameCommand.ExecuteAsync(
                new DeleteGameRequest { Game = game, DeleteBackupFiles = deleteBackupFiles });
        }
        catch (System.Exception ex)
        {
            ViewModel.ErrorMessage = $"删除游戏失败：{ex.Message}";
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
            SetOverlayButtonVisibility(grid, Visibility.Visible);
        }
    }

    /// <summary>
    /// Handles game card pointer exited - hides quick launch button.
    /// </summary>
    private void OnGameCardPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid grid)
        {
            SetOverlayButtonVisibility(grid, Visibility.Collapsed);
        }
    }

    /// <summary>
    /// Shows or hides every hover-only overlay button of a game card.
    /// </summary>
    private static void SetOverlayButtonVisibility(Grid grid, Visibility visibility)
    {
        foreach (var child in grid.Children)
        {
            if (child is Button button
                && (button.Name == "QuickLaunchOverlayButton" || button.Name == "DeleteGameOverlayButton"))
            {
                button.Visibility = visibility;
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