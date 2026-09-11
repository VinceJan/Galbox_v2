using Galbox.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Windows.UI.Text;

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
        ViewModel = App.Services.GetRequiredService<GameDetailViewModel>();

        // Set DataContext for any binding fallback
        DataContext = ViewModel;

        // Subscribe to Executables collection changes for dynamic menu population
        ViewModel.Executables.CollectionChanged += OnExecutablesCollectionChanged;
    }

    /// <summary>
    /// Handles changes to the Executables collection and updates the MenuFlyout.
    /// </summary>
    private void OnExecutablesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        PopulateAlternativeExecutablesMenu();
    }

    /// <summary>
    /// Populates the AlternativeExecutablesMenu with MenuFlyoutItem elements.
    /// Note: MenuFlyoutItemsRepeater doesn't exist in WinUI3, so we populate dynamically.
    /// </summary>
    private void PopulateAlternativeExecutablesMenu()
    {
        if (AlternativeExecutablesMenu == null)
            return;

        AlternativeExecutablesMenu.Items.Clear();

        foreach (var executable in ViewModel.Executables)
        {
            var menuItem = new MenuFlyoutItem
            {
                Text = executable.Name,
                Command = ViewModel.LaunchAlternativeCommand,
                CommandParameter = executable
            };

            // Set icon based on whether it's the default executable
            menuItem.Icon = new FontIcon
            {
                Glyph = executable.IsDefault ? "\uE73E" : "\uE75C" // Checkmark or Play
            };

            AlternativeExecutablesMenu.Items.Add(menuItem);
        }
    }

    /// <summary>
    /// Navigates to the scraping view for the currently displayed game.
    /// D1: this button is the user-facing entry point of metadata scraping.
    /// </summary>
    private void OnScrapeClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.GameId <= 0)
        {
            ViewModel.ErrorMessage = "游戏尚未加载完成，无法刮削";
            return;
        }

        var navigationService = App.Services.GetRequiredService<Galbox.App.Services.INavigationService>();
        navigationService.NavigateTo("ScrapingProgress", ViewModel.GameId);
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
                // Populate the menu after data is loaded
                PopulateAlternativeExecutablesMenu();
            }
            else if (e.Parameter is string gameIdStr && int.TryParse(gameIdStr, out var parsedGameId))
            {
                await ViewModel.LoadGameDataAsync(parsedGameId);
                // Populate the menu after data is loaded
                PopulateAlternativeExecutablesMenu();
            }
            else
            {
                // Invalid parameter - show error
                ViewModel.ErrorMessage = "提供了无效的游戏 ID";
            }
        }
        catch (Exception ex)
        {
            ViewModel.ErrorMessage = $"加载页面失败：{ex.Message}";
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