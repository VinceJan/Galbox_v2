using Galbox.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;

namespace Galbox.App.Views;

/// <summary>
/// Code-behind for GameDetailPage.
/// Uses Dependency Injection to obtain the ViewModel.
/// All UI updates are handled via data binding (x:Bind) - NO manual UpdateUI() method.
/// </summary>
public sealed partial class GameDetailPage : Page
{
    /// <summary>
    /// The ViewModel for this page, obtained via DI.
    /// </summary>
    public GameDetailViewModel ViewModel { get; }

    /// <summary>
    /// Creates a GameDetailPage and obtains the ViewModel via DI.
    /// </summary>
    public GameDetailPage()
    {
        InitializeComponent();

        // Get ViewModel from DI container
        ViewModel = App.Current.Services.GetRequiredService<GameDetailViewModel>();

        // Set DataContext for any binding fallback
        DataContext = ViewModel;
    }

    /// <summary>
    /// Handles navigation to this page and loads the game data.
    /// </summary>
    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        try
        {
            // Get game ID from navigation parameter
            if (e.Parameter is int gameId && gameId > 0)
            {
                await ViewModel.LoadGameDataAsync(gameId);
            }
            else if (e.Parameter is string gameIdStr && int.TryParse(gameIdStr, out var parsedGameId))
            {
                await ViewModel.LoadGameDataAsync(parsedGameId);
            }
            else
            {
                // Invalid parameter - show error
                ViewModel.ErrorMessage = "Invalid game ID provided";
            }
        }
        catch (Exception ex)
        {
            ViewModel.ErrorMessage = $"Failed to load page: {ex.Message}";
        }
    }

    /// <summary>
    /// Handles navigation away from this page.
    /// Cleans up any running processes if necessary.
    /// </summary>
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);

        // Note: We don't force-terminate running games when navigating away
        // The process monitoring continues in the ViewModel
    }
}