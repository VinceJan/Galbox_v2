using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Galbox.App.Views;

/// <summary>
/// Save manager page for managing game saves, snapshots, and branches.
/// </summary>
public sealed partial class SaveManagerPage : Page
{
    public SaveManagerPage()
    {
        InitializeComponent();
        LoadGames();
    }

    private void LoadGames()
    {
        // TODO: Load games that have saves
        GameSelector.ItemsSource = new List<object>();
    }

    private void GameSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // TODO: Load saves for selected game
    }

    private void CreateSnapshot_Click(object sender, RoutedEventArgs e)
    {
        // TODO: Create a new save snapshot
    }

    private void CreateBranch_Click(object sender, RoutedEventArgs e)
    {
        // TODO: Create a new save branch
    }
}