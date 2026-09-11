using Galbox.App.Services;
using Microsoft.UI.Xaml.Controls;

namespace Galbox.Acceptance.Support;

/// <summary>
/// A real <see cref="INavigationService"/> implementation that never touches WinUI.
///
/// The patch centre page resolves <c>PatchCenterViewModel</c> from the container exactly like the
/// shipping app does; this stub stands in for the one dependency that would otherwise require a live
/// XAML host, so the same ViewModel, the same commands and the same services run headlessly.
/// Verified: the interface and this implementation load without initialising the Windows App SDK
/// (only <i>instantiating</i> a WinUI control would need it, and this type never does).
/// </summary>
public sealed class HeadlessNavigationService : INavigationService
{
    /// <summary>Navigation requests observed, as <c>key:parameter</c> strings.</summary>
    public List<string> Requests { get; } = new();

    /// <inheritdoc />
    public Frame? ContentFrame => null;

    /// <inheritdoc />
    public NavigationView? NavigationView => null;

    /// <inheritdoc />
    public bool CanGoBack => false;

    /// <inheritdoc />
    public void Initialize(Frame frame, NavigationView navigationView) { }

    /// <inheritdoc />
    public bool NavigateTo(Type pageType, object? parameter = null)
    {
        Requests.Add($"{pageType.Name}:{parameter}");
        return true;
    }

    /// <inheritdoc />
    public bool NavigateTo(string navigationKey, object? parameter = null)
    {
        Requests.Add($"{navigationKey}:{parameter}");
        return true;
    }

    /// <inheritdoc />
    public void GoBack() { }

    /// <inheritdoc />
    public void ClearHistory() { }

    /// <inheritdoc />
    public Type? CurrentPageType => null;

    /// <inheritdoc />
    public object? CurrentParameter => null;

    /// <inheritdoc />
    public void SelectNavigationItem(string key) { }
}
