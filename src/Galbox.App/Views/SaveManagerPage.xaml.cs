using Galbox.App.ViewModels;
using Galbox.Data.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Galbox.App.Views;

/// <summary>
/// Save Manager page for managing game save backups.
/// Supports backup creation, restoration, and quick switching.
/// </summary>
public sealed partial class SaveManagerPage : Page
{
    /// <summary>
    /// The ViewModel for this page, obtained via DI.
    /// </summary>
    public SaveManagerViewModel ViewModel { get; }

    /// <summary>
    /// Creates a SaveManagerPage and obtains the ViewModel via DI.
    /// </summary>
    public SaveManagerPage()
    {
        InitializeComponent();

        // Get ViewModel from DI container
        ViewModel = App.Services.GetRequiredService<SaveManagerViewModel>();

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
    /// Handles game item click from the game list.
    /// </summary>
    private void OnGameItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is GameSaveInfo game)
        {
            ViewModel.SelectedGame = game;
        }
    }

    /// <summary>
    /// Handles refresh button click.
    /// </summary>
    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.RefreshCommand.ExecuteAsync(null);
    }

    /// <summary>
    /// Handles clear selection button click.
    /// </summary>
    private void OnClearSelectionClick(object sender, RoutedEventArgs e)
    {
        ViewModel.ClearSelectionCommand.Execute(null);
    }

    /// <summary>
    /// Handles create backup button click.
    /// </summary>
    private async void OnCreateBackupClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.CreateBackupCommand.ExecuteAsync(null);
        }
        catch (System.Exception ex)
        {
            ViewModel.ErrorMessage = $"Failed to create backup: {ex.Message}";
        }
    }

    /// <summary>
    /// Handles restore backup button click.
    /// </summary>
    private async void OnRestoreBackupClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is Button button && button.Tag is GameSaveBackup backup)
            {
                ViewModel.SelectedBackup = backup;
                await ViewModel.RestoreBackupCommand.ExecuteAsync(null);
            }
        }
        catch (System.Exception ex)
        {
            ViewModel.ErrorMessage = $"Failed to restore backup: {ex.Message}";
        }
    }

    /// <summary>
    /// Handles delete backup button click.
    /// </summary>
    private async void OnDeleteBackupClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is Button button && button.Tag is GameSaveBackup backup)
            {
                ViewModel.SelectedBackup = backup;
                await ViewModel.DeleteBackupCommand.ExecuteAsync(null);
            }
        }
        catch (System.Exception ex)
        {
            ViewModel.ErrorMessage = $"Failed to delete backup: {ex.Message}";
        }
    }

    /// <summary>
    /// Handles quick switch button click.
    /// </summary>
    private async void OnQuickSwitchClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is Button button && button.Tag is GameSaveBackup backup)
            {
                await ViewModel.QuickSwitchBackupCommand.ExecuteAsync(backup);
            }
        }
        catch (System.Exception ex)
        {
            ViewModel.ErrorMessage = $"Failed to quick switch: {ex.Message}";
        }
    }
}