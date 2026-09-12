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

    /// <summary>Lazily built details flyout (see <see cref="ShowPatchDetailsFlyout"/>).</summary>
    private Flyout? _patchDetailsFlyout;

    /// <summary>The live TextBlock inside <see cref="_patchDetailsFlyout"/>.</summary>
    private TextBlock? _patchDetailsText;

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
    /// Shows the patch-details flyout for the given target.
    /// </summary>
    /// <remarks>
    /// The flyout is built here rather than declared in <c>Page.Resources</c>. See the comment in
    /// PatchCenterPage.xaml: an <c>x:Bind</c> on a resource-dictionary element produced a null
    /// TextBlock in the generated binding pass and killed the process the moment the page loaded
    /// (0xc000027b in Microsoft.UI.Xaml.dll). Assigning the text to a live TextBlock that this method
    /// created cannot hit that path.
    /// </remarks>
    public void ShowPatchDetailsFlyout(FrameworkElement target)
    {
        if (_patchDetailsText is null || _patchDetailsFlyout is null)
        {
            _patchDetailsText = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = 14
            };

            var body = new StackPanel { Spacing = 12 };
            body.Children.Add(new TextBlock { Text = "补丁详情", FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.Bold });
            body.Children.Add(_patchDetailsText);

            _patchDetailsFlyout = new Flyout
            {
                Placement = FlyoutPlacementMode.Bottom,
                ShouldConstrainToRootBounds = false,
                Content = new Border
                {
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(16),
                    MinWidth = 400,
                    MaxWidth = 500,
                    Child = body
                }
            };
        }

        _patchDetailsText.Text = string.IsNullOrEmpty(ViewModel.PatchDetailContent)
            ? "（没有详情）"
            : ViewModel.PatchDetailContent;

        _patchDetailsFlyout.ShowAt(target);
    }

    /// <summary>
    /// Forwards the clicked patch row to the ViewModel's details command and opens the details flyout.
    /// </summary>
    /// <remarks>
    /// The patch cards live in a DataTemplate, which has its own XAML namescope: the previous
    /// <c>{Binding ViewModel.ShowPatchDetailsCommand, ElementName=RootGrid}</c> could not resolve
    /// (RootGrid is a Grid with no ViewModel property) and the button was a no-op. The row is
    /// passed through Tag instead.
    /// </remarks>
    private void OnPatchDetailsClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: PatchRecord patch } element)
        {
            return;
        }

        ViewModel.ShowPatchDetailsCommand.Execute(patch);
        ShowPatchDetailsFlyout(element);
    }

    // ================================================================================
    // 本地补丁包（已下载）
    // ================================================================================

    // ================================================================================
    // 在线补丁源：moyu.moe（官方公开面）
    // ================================================================================

    /// <summary>
    /// Copies the key the user typed into the ViewModel.
    /// </summary>
    /// <remarks>
    /// A <see cref="PasswordBox.Password"/> is not a bindable property, so the value is pushed across
    /// here. It stays in memory only: the ViewModel writes it to the DPAPI store when the user presses
    /// 保存密钥, and never reads it back into the box. Nothing on this path logs it.
    /// </remarks>
    private void OnMoyuKeyPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox box)
        {
            ViewModel.MoyuKeyInput = box.Password;
        }
    }

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