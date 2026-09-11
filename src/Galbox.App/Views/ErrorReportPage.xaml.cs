using Galbox.App.ViewModels;
using Galbox.Data.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Galbox.App.Views;

/// <summary>
/// Code-behind for ErrorReportPage.
///
/// This page is the missing "door" for <see cref="ErrorReportViewModel"/>: the ViewModel and its
/// <c>ErrorCheckingService</c> (eight real file-system checks) were registered in DI and fully
/// implemented, but no XAML file, navigation key or menu item referenced them, so the whole
/// module was unreachable from the UI.
/// </summary>
public sealed partial class ErrorReportPage : Page
{
    /// <summary>
    /// The ViewModel for this page, obtained via DI.
    /// </summary>
    public ErrorReportViewModel ViewModel { get; }

    /// <summary>
    /// Creates an ErrorReportPage and obtains the ViewModel via DI.
    /// </summary>
    public ErrorReportPage()
    {
        InitializeComponent();

        ViewModel = App.Services.GetRequiredService<ErrorReportViewModel>();
        DataContext = ViewModel;

        Loaded += OnPageLoaded;
    }

    /// <summary>
    /// Loads the stored findings as soon as the page is shown.
    /// </summary>
    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.LoadAllUnresolvedErrorsAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error loading the error report: {ex.Message}");
        }
    }

    /// <summary>Reloads the stored findings.</summary>
    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadAllUnresolvedErrorsAsync();
    }

    /// <summary>Re-runs the full check for the game selected in the list.</summary>
    private async void OnGameSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel.SelectedGame is GameInfo game)
        {
            await ViewModel.CheckGameErrorsAsync(game);
        }
    }

    /// <summary>Applies the chosen severity filter.</summary>
    private void OnSeverityFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.FirstOrDefault() is SeverityFilterOption option)
        {
            ViewModel.SeverityFilter = option.Value;
        }
    }

    /// <summary>Applies the chosen category filter.</summary>
    private void OnCategoryFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.FirstOrDefault() is CategoryFilterOption option)
        {
            ViewModel.CategoryFilter = option.Value;
        }
    }

    // The rows below live in DataTemplates, which own their own XAML namescope; the clicked row is
    // passed through Tag and forwarded to the matching command.

    /// <summary>Marks a persisted error record as resolved.</summary>
    private void OnMarkRecordResolvedClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: GameErrorRecord record })
        {
            ViewModel.MarkRecordResolvedCommand.Execute(record);
        }
    }

    /// <summary>Marks a detected finding as resolved.</summary>
    private void OnMarkResolvedClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Galbox.App.Models.GameErrorInfo error })
        {
            ViewModel.MarkResolvedCommand.Execute(error);
        }
    }

    /// <summary>Opens the download page of the required component.</summary>
    private void OnOpenDownloadUrlClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Galbox.App.Models.GameErrorInfo error })
        {
            ViewModel.OpenDownloadUrlCommand.Execute(error);
        }
    }

    /// <summary>Attempts the automatic fix (only offered when the service reports one exists).</summary>
    private void OnAttemptFixClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Galbox.App.Models.GameErrorInfo error })
        {
            ViewModel.AttemptFixCommand.Execute(error);
        }
    }
}
