using Galbox.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;

namespace Galbox.App.Views;

/// <summary>
/// Code-behind for ScrapingProgressPage.
/// Entry point of the metadata scraping feature: it can scrape a single game
/// (navigated to from the game detail page, or automatically after adding a game)
/// or a batch of game ids.
/// </summary>
public sealed partial class ScrapingProgressPage : Page
{
    /// <summary>
    /// The ViewModel for this page, obtained via DI.
    /// </summary>
    public ScrapingProgressViewModel ViewModel { get; }

    /// <summary>
    /// Creates a ScrapingProgressPage and obtains the ViewModel via DI.
    /// </summary>
    public ScrapingProgressPage()
    {
        InitializeComponent();

        ViewModel = App.Services.GetRequiredService<ScrapingProgressViewModel>();
        DataContext = ViewModel;
    }

    /// <summary>
    /// Starts scraping for the navigation parameter (a game id or a list of game ids).
    /// </summary>
    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        var gameIds = new List<int>();

        switch (e.Parameter)
        {
            case int singleId when singleId > 0:
                gameIds.Add(singleId);
                break;

            case string singleIdText when int.TryParse(singleIdText, out var parsed) && parsed > 0:
                gameIds.Add(parsed);
                break;

            case IEnumerable<int> ids:
                gameIds.AddRange(ids.Where(id => id > 0));
                break;
        }

        if (gameIds.Count == 0)
        {
            // Opened without a target: keep the page usable, the user can pick from the library.
            ViewModel.ErrorMessage = "未指定要刮削的游戏，请从游戏详情页点击「刮削」。";
            return;
        }

        await ViewModel.ScrapeGamesAsync(gameIds);
    }

    /// <summary>
    /// Cleans up event subscriptions when leaving the page.
    /// </summary>
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.UnsubscribeEvents();
    }

    /// <summary>
    /// Shows the full error detail of a source diagnostic row.
    /// </summary>
    private void OnDiagnosticDetailClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: SourceDiagnosticItem item })
        {
            ViewModel.ShowDiagnosticDetailCommand.Execute(item);
        }
    }

    /// <summary>
    /// Applies the metadata of the clicked candidate to its game.
    /// </summary>
    private void OnApplyCandidateClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: CandidateMatchItem item })
        {
            ViewModel.ApplyCandidateCommand.Execute(item);
        }
    }
}
