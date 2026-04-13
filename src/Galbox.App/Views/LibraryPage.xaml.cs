using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Galbox.App.Views;

/// <summary>
/// Library page showing all games in the collection.
/// </summary>
public sealed partial class LibraryPage : Page
{
    private bool _isGridView = true;

    public LibraryPage()
    {
        InitializeComponent();
        LoadGames();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        LoadGames();
    }

    private void LoadGames()
    {
        // TODO: Load from database with current filter
        GamesGridView.ItemsSource = new List<object>();
    }

    private void SearchTextBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        // TODO: Search games
        var query = args.QueryText;
        LoadGames();
    }

    private void AddGame_Click(object sender, RoutedEventArgs e)
    {
        // TODO: Open add game dialog or folder picker
    }

    private void ToggleView_Click(object sender, RoutedEventArgs e)
    {
        _isGridView = !_isGridView;
        ToggleViewButton.Icon = _isGridView ? new SymbolIcon(Symbol.List) : new SymbolIcon(Symbol.GridView);
        // TODO: Toggle between grid and list view
    }

    private void Filter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag != null)
        {
            var status = button.Tag.ToString();
            // TODO: Filter games by status
            LoadGames();
        }
    }

    private void Game_Click(object sender, ItemClickEventArgs e)
    {
        // TODO: Navigate to game detail page
    }
}