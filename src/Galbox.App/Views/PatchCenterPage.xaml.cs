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
    public void ShowPatchDetailsFlyout(FrameworkElement target)
    {
        var flyout = (Flyout)Resources["PatchDetailsFlyoutKey"];
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

    // ================================================================================
    // 本地补丁包（已下载）
    // ================================================================================

    /// <summary>
    /// Opens the file picker and previews the chosen patch package.
    /// </summary>
    /// <remarks>
    /// The picker needs a window handle: a WinUI 3 <see cref="FileOpenPicker"/> is a shell dialog and
    /// throws without <c>InitializeWithWindow</c>. The extension filter comes from the ViewModel
    /// (which gets it from the patch service), so the dialog can never offer a format the engine
    /// refuses - <c>.exe</c> and <c>.iso</c> are recognised by the engine but never installed.
    /// </remarks>
    private async void OnPickPatchArchiveClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads,
                ViewMode = Windows.Storage.Pickers.PickerViewMode.List
            };

            foreach (var extension in ViewModel.SupportedArchiveExtensions)
            {
                picker.FileTypeFilter.Add(extension);
            }

            if (picker.FileTypeFilter.Count == 0)
            {
                picker.FileTypeFilter.Add("*");
            }

            // The picker is a shell dialog and needs the window handle, exactly like SettingsPage.
            var windowHandle = (App.Current as App)?.MainWindow?.WindowHandle ?? IntPtr.Zero;
            if (windowHandle == IntPtr.Zero)
            {
                ViewModel.ErrorMessage = "无法打开文件选择器：当前没有可用的窗口句柄。";
                return;
            }

            WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);

            var file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                return;
            }

            await ViewModel.SelectPatchArchiveAsync(file.Path);
        }
        catch (Exception ex)
        {
            // Never swallow: a picker failure must leave a visible reason on the page.
            ViewModel.ErrorMessage = $"选择补丁包失败：{ex.Message}";
            System.Diagnostics.Debug.WriteLine($"Patch archive picker failed: {ex}");
        }
    }

    /// <summary>Rolls back the install whose id is in the button's Tag.</summary>
    private async void OnRollbackClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not FrameworkElement { Tag: string installId } || string.IsNullOrEmpty(installId))
            {
                return;
            }

            await ViewModel.RollbackAsync(installId);
        }
        catch (Exception ex)
        {
            ViewModel.ErrorMessage = $"回滚失败：{ex.Message}";
            System.Diagnostics.Debug.WriteLine($"Patch rollback failed: {ex}");
        }
    }

    /// <summary>
    /// Applies the recovery plan whose instance is in the button's Tag.
    /// </summary>
    /// <remarks>
    /// Recovery is a data-safety action: it restores the files an interrupted install had already
    /// replaced and removes the ones it created. It is only ever reached from a detected plan.
    /// </remarks>
    private async void OnRecoverInterruptedClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not FrameworkElement { Tag: Galbox.Core.Patches.PatchRecoveryPlan plan })
            {
                return;
            }

            await ViewModel.RecoverPlanAsync(plan, apply: true);
        }
        catch (Exception ex)
        {
            ViewModel.ErrorMessage = $"恢复失败：{ex.Message}";
            System.Diagnostics.Debug.WriteLine($"Patch recovery failed: {ex}");
        }
    }
}