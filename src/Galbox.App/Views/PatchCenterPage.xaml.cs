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

        // W3: every other page sets DataContext, this one did not - so every classic
        // {Binding ...} on this page (including the ScrollViewer that hosts the patch list)
        // silently resolved to null and the patch area stayed collapsed forever.
        DataContext = ViewModel;

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
    /// <remarks>
    /// PatchDetailText is filled here rather than through an {x:Bind} in the XAML (see the comment
    /// on &lt;Flyout x:Key="PatchDetailsFlyoutKey"&gt;): a compiled binding inside a Flyout resource
    /// is walked by the generated bindings object while the flyout content is still unattached, and
    /// the resulting NullReferenceException terminates the process with 0xC000027B. Writing the
    /// text at show time cannot hit that window, because fetching the flyout from Resources forces
    /// its content to be created.
    /// </remarks>
    public void ShowPatchDetailsFlyout(FrameworkElement target)
    {
        var flyout = (Flyout)Resources["PatchDetailsFlyoutKey"];
        PatchDetailText.Text = ViewModel.PatchDetailContent ?? string.Empty;
        flyout.ShowAt(target);
    }

    /// <summary>
    /// Forwards the clicked patch row to the ViewModel's details command.
    /// </summary>
    /// <remarks>
    /// The patch cards live in a DataTemplate, which has its own XAML namescope: the previous
    /// <c>{Binding ViewModel.ShowPatchDetailsCommand, ElementName=RootGrid}</c> could not resolve
    /// (RootGrid is a Grid with no ViewModel property) and the button was a no-op. The row is
    /// passed through Tag instead.
    /// </remarks>
    private void OnPatchDetailsClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: PatchRecord patch })
        {
            ViewModel.ShowPatchDetailsCommand.Execute(patch);
        }
    }
}