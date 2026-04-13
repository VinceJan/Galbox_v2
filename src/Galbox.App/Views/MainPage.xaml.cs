using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Galbox.App.Views;

/// <summary>
/// Main page showing quick launch and recent games.
/// </summary>
public sealed partial class MainPage : Page
{
    public MainPage()
    {
        InitializeComponent();
        LoadRecentGames();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        LoadRecentGames();
    }

    private void LoadRecentGames()
    {
        // TODO: Load from database
        // Placeholder: empty list for now
        RecentGamesGridView.ItemsSource = new List<object>();
    }

    private void QuickLaunch_Click(object sender, RoutedEventArgs e)
    {
        // TODO: Launch the most recent game
    }

    private void RecentGame_Click(object sender, ItemClickEventArgs e)
    {
        // TODO: Navigate to game detail page
        if (e.ClickedItem != null)
        {
            // ContentFrame.Navigate(typeof(GameDetailPage), e.ClickedItem);
        }
    }
}