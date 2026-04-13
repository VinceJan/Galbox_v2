using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Galbox.App.Views;

namespace Galbox.App;

/// <summary>
/// An empty window that can be used on its own or navigated to within a Frame.
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = "Galbox - Galgame 聚合平台";

        // Set default size
        var appWindow = this.AppWindow;
        appWindow.Resize(new Windows.Graphics.SizeInt32(1280, 720));

        // Navigate to main page on startup
        ContentFrame.Navigate(typeof(MainPage));
    }

    private void NavigationView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is NavigationViewItem item)
        {
            var tag = item.Tag?.ToString();
            NavigateToPage(tag);
        }
        else if (args.IsSettingsInvoked)
        {
            ContentFrame.Navigate(typeof(SettingsPage));
        }
    }

    private void NavigateToPage(string? tag)
    {
        if (tag == null) return;

        Type pageType = tag switch
        {
            "MainPage" => typeof(MainPage),
            "LibraryPage" => typeof(LibraryPage),
            "SaveManagerPage" => typeof(SaveManagerPage),
            "PatchCenterPage" => typeof(PatchCenterPage),
            "SettingsPage" => typeof(SettingsPage),
            _ => typeof(MainPage)
        };

        ContentFrame.Navigate(pageType);
    }
}