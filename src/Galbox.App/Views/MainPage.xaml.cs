using Galbox.App.ViewModels;
using Galbox.Data.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Galbox.App.Views;

/// <summary>
/// Main/Home page for Galbox application.
/// Displays quick launch, recent games, and currently playing sections.
/// </summary>
public sealed partial class MainPage : Page
{
    /// <summary>
    /// The ViewModel for this page, obtained via DI.
    /// </summary>
    public MainViewModel ViewModel { get; }

    /// <summary>
    /// Creates a MainPage and obtains the ViewModel via DI.
    /// </summary>
    public MainPage()
    {
        InitializeComponent();

        // Get ViewModel from DI container
        ViewModel = App.Services.GetRequiredService<MainViewModel>();

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
            await ViewModel.LoadDataAsync();
        }
        catch (System.Exception ex)
        {
            ViewModel.ErrorMessage = $"Failed to load page: {ex.Message}";
        }
    }

    /// <summary>
    /// Handles game item click in lists.
    /// </summary>
    private void OnGameItemClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is Button button && button.DataContext is GameInfo game)
        {
            ViewModel.NavigateToGameCommand.Execute(game);
        }
    }
}