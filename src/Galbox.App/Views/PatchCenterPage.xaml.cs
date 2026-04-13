using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Galbox.App.Views;

/// <summary>
/// Patch center page for searching and downloading game patches.
/// </summary>
public sealed partial class PatchCenterPage : Page
{
    public PatchCenterPage()
    {
        InitializeComponent();
    }

    private void SearchTextBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        // TODO: Search patches from moyu.moe
        var query = args.QueryText;
    }

    private void DownloadPatch_Click(object sender, RoutedEventArgs e)
    {
        // TODO: Download and install patch
    }

    private void ViewPatchDetails_Click(object sender, RoutedEventArgs e)
    {
        // TODO: Show patch details dialog
    }
}