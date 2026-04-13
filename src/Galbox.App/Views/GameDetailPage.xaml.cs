using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Galbox.App.Views;

/// <summary>
/// Game detail page showing game information, stats, and media.
/// </summary>
public sealed partial class GameDetailPage : Page
{
    public GameDetailPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        // TODO: Load game data from navigation parameter
        if (e.Parameter != null)
        {
            // LoadGameInfo(e.Parameter);
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack)
        {
            Frame.GoBack();
        }
    }

    private void LaunchMain_Click(object sender, RoutedEventArgs e)
    {
        // TODO: Launch main executable
    }

    private void LaunchAlt_Click(object sender, RoutedEventArgs e)
    {
        // TODO: Launch alternative executable
    }

    private void ViewSaves_Click(object sender, RoutedEventArgs e)
    {
        // TODO: Navigate to save manager for this game
    }

    private void QuickBackup_Click(object sender, RoutedEventArgs e)
    {
        // TODO: Quick backup current saves
    }

    private void OpenReadme_Click(object sender, RoutedEventArgs e)
    {
        // TODO: Open readme file in preview window
    }
}