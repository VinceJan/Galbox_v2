using Galbox.App.ViewModels;
using Galbox.Data.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Galbox.App.Views;

/// <summary>
/// Patch Center page for managing game patches and translations.
/// </summary>
public sealed partial class PatchCenterPage : Page
{
    /// <summary>
    /// Gets the ViewModel for this page.
    /// </summary>
    public PatchCenterViewModel ViewModel { get; }

    /// <summary>
    /// Creates a PatchCenterPage.
    /// </summary>
    public PatchCenterPage()
    {
        InitializeComponent();

        // Get ViewModel from DI container
        ViewModel = App.Services.GetRequiredService<PatchCenterViewModel>();

        // Initialize data loading
        Loaded += OnPageLoaded;
    }

    /// <summary>
    /// Handles page loaded event to load data.
    /// </summary>
    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.LoadDataAsync();
        }
        catch (Exception ex)
        {
            // Log error but don't crash - ViewModel should handle its own error state
            System.Diagnostics.Debug.WriteLine($"Error loading patch center data: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles game item click to select and load patches.
    /// </summary>
    private async void OnGameItemClick(object sender, ItemClickEventArgs e)
    {
        try
        {
            if (e.ClickedItem is GameInfo game)
            {
                ViewModel.SelectedGame = game;
                await ViewModel.LoadPatchesForGameAsync(game.Id);
            }
        }
        catch (Exception ex)
        {
            // Log error but don't crash - ViewModel should handle its own error state
            System.Diagnostics.Debug.WriteLine($"Error handling game item click: {ex.Message}");
        }
    }

    /// <summary>
    /// Shows patch details flyout when clicking Details button.
    /// </summary>
    public void ShowPatchDetailsFlyout(FrameworkElement target)
    {
        var flyout = (Flyout)Resources["PatchDetailsFlyoutKey"];
        flyout.ShowAt(target);
    }
}