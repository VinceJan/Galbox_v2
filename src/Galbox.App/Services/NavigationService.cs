using Galbox.App.Views;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace Galbox.App.Services;

/// <summary>
/// Navigation service implementation for page navigation.
/// </summary>
public class NavigationService : INavigationService
{
    private Frame? _contentFrame;
    private NavigationView? _navigationView;
    private readonly ILogger<NavigationService> _logger;

    /// <summary>
    /// Mapping from navigation keys to page types.
    /// </summary>
    private readonly Dictionary<string, Type> _pageMapping = new()
    {
        { "Home", typeof(MainPage) },
        { "Library", typeof(LibraryPage) },
        { "SaveManager", typeof(SaveManagerPage) },
        { "PatchCenter", typeof(PatchCenterPage) },
        { "Settings", typeof(SettingsPage) },
        { "GameDetail", typeof(GameDetailPage) }
    };

    /// <summary>
    /// Gets the current navigation frame.
    /// </summary>
    public Frame? ContentFrame => _contentFrame;

    /// <summary>
    /// Gets the NavigationView control.
    /// </summary>
    public NavigationView? NavigationView => _navigationView;

    /// <summary>
    /// Gets whether the navigation can go back.
    /// </summary>
    public bool CanGoBack => _contentFrame?.CanGoBack ?? false;

    /// <summary>
    /// Gets the current page type.
    /// </summary>
    public Type? CurrentPageType => _contentFrame?.Content?.GetType();

    /// <summary>
    /// Gets the current navigation parameter.
    /// </summary>
    public object? CurrentParameter { get; private set; }

    /// <summary>
    /// Creates a NavigationService with injected dependencies.
    /// </summary>
    public NavigationService(ILogger<NavigationService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Initializes the navigation service with the frame and navigation view.
    /// </summary>
    public void Initialize(Frame frame, NavigationView navigationView)
    {
        _contentFrame = frame ?? throw new ArgumentNullException(nameof(frame));
        _navigationView = navigationView ?? throw new ArgumentNullException(nameof(navigationView));

        // Subscribe to navigation events
        _contentFrame.Navigated += OnNavigated;

        _logger.LogInformation("Navigation service initialized");
    }

    /// <summary>
    /// Navigates to a page with the specified type.
    /// </summary>
    public bool NavigateTo(Type pageType, object? parameter = null)
    {
        if (_contentFrame == null)
        {
            _logger.LogWarning("ContentFrame is null, cannot navigate");
            return false;
        }

        // Don't navigate to the same page
        if (_contentFrame.Content?.GetType() == pageType && parameter == CurrentParameter)
        {
            _logger.LogDebug("Already on page {PageType}, skipping navigation", pageType.Name);
            return false;
        }

        CurrentParameter = parameter;

        // Use slide animation for navigation
        var transitionInfo = new SlideNavigationTransitionInfo
        {
            Effect = SlideNavigationTransitionEffect.FromLeft
        };

        var result = _contentFrame.Navigate(pageType, parameter, transitionInfo);

        if (result)
        {
            _logger.LogInformation("Navigated to {PageType} with parameter {Parameter}", pageType.Name, parameter);
        }
        else
        {
            _logger.LogWarning("Failed to navigate to {PageType}", pageType.Name);
        }

        return result;
    }

    /// <summary>
    /// Navigates to a page by navigation key.
    /// </summary>
    public bool NavigateTo(string navigationKey, object? parameter = null)
    {
        if (!_pageMapping.TryGetValue(navigationKey, out var pageType))
        {
            _logger.LogWarning("Unknown navigation key: {NavigationKey}", navigationKey);
            return false;
        }

        return NavigateTo(pageType, parameter);
    }

    /// <summary>
    /// Goes back in navigation history.
    /// </summary>
    public void GoBack()
    {
        if (!CanGoBack)
        {
            _logger.LogDebug("Cannot go back, navigation stack is empty");
            return;
        }

        _contentFrame?.GoBack();

        _logger.LogInformation("Navigated back");
    }

    /// <summary>
    /// Clears navigation history.
    /// </summary>
    public void ClearHistory()
    {
        if (_contentFrame == null)
            return;

        // Remove all back stack entries
        while (_contentFrame.CanGoBack)
        {
            _contentFrame.RemoveBackEntry();
        }

        _logger.LogInformation("Navigation history cleared");
    }

    /// <summary>
    /// Handles navigation events to update NavigationView selection.
    /// </summary>
    private void OnNavigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        if (_navigationView == null)
            return;

        // Update back button state
        _navigationView.IsBackEnabled = CanGoBack;

        // Update selected navigation item based on current page
        UpdateNavigationViewSelection(e.SourcePageType);

        _logger.LogDebug("Navigation event: Page={PageType}, CanGoBack={CanGoBack}",
            e.SourcePageType?.Name, CanGoBack);
    }

    /// <summary>
    /// Updates the NavigationView selected item based on the current page.
    /// </summary>
    private void UpdateNavigationViewSelection(Type? pageType)
    {
        if (_navigationView == null || pageType == null)
            return;

        // Find the matching navigation item
        foreach (var item in _navigationView.MenuItems)
        {
            if (item is NavigationViewItem navItem)
            {
                var tag = navItem.Tag?.ToString();
                if (_pageMapping.TryGetValue(tag ?? string.Empty, out var mappedType) && mappedType == pageType)
                {
                    _navigationView.SelectedItem = navItem;
                    return;
                }
            }
        }

        // If page is not a main navigation item (like GameDetailPage), clear selection
        if (pageType == typeof(GameDetailPage) ||
            pageType == typeof(SaveManagerPage) ||
            pageType == typeof(PatchCenterPage))
        {
            // Keep the parent navigation item selected if applicable
            // For GameDetailPage, keep Library selected
            if (pageType == typeof(GameDetailPage))
            {
                SelectNavigationItem("Library");
            }
        }
    }

    /// <summary>
    /// Selects a navigation item by key.
    /// </summary>
    public void SelectNavigationItem(string key)
    {
        if (_navigationView == null)
            return;

        foreach (var item in _navigationView.MenuItems)
        {
            if (item is NavigationViewItem navItem && navItem.Tag?.ToString() == key)
            {
                _navigationView.SelectedItem = navItem;
                return;
            }
        }
    }
}