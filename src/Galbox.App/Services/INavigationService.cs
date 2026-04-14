using Microsoft.UI.Xaml.Controls;

namespace Galbox.App.Services;

/// <summary>
/// Navigation service interface for page navigation within the app.
/// </summary>
public interface INavigationService
{
    /// <summary>
    /// Gets the current navigation frame.
    /// </summary>
    Frame? ContentFrame { get; }

    /// <summary>
    /// Gets the NavigationView control.
    /// </summary>
    NavigationView? NavigationView { get; }

    /// <summary>
    /// Gets whether the navigation can go back.
    /// </summary>
    bool CanGoBack { get; }

    /// <summary>
    /// Initializes the navigation service with the frame and navigation view.
    /// </summary>
    void Initialize(Frame frame, NavigationView navigationView);

    /// <summary>
    /// Navigates to a page with the specified type.
    /// </summary>
    bool NavigateTo(Type pageType, object? parameter = null);

    /// <summary>
    /// Navigates to a page by navigation key.
    /// </summary>
    bool NavigateTo(string navigationKey, object? parameter = null);

    /// <summary>
    /// Goes back in navigation history.
    /// </summary>
    void GoBack();

    /// <summary>
    /// Clears navigation history.
    /// </summary>
    void ClearHistory();

    /// <summary>
    /// Gets the current page type.
    /// </summary>
    Type? CurrentPageType { get; }

    /// <summary>
    /// Gets the current navigation parameter.
    /// </summary>
    object? CurrentParameter { get; }

    /// <summary>
    /// Selects a navigation item by key.
    /// </summary>
    void SelectNavigationItem(string key);
}