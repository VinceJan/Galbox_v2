using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Galbox.App.Services;
using Galbox.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Galbox.App.ViewModels;

/// <summary>
/// ViewModel for the Patch Center Page.
///
/// Patch sources are not wired up yet (the moYu/NextMoe integration is a separate work line), so
/// this ViewModel deliberately shows only what really exists in the database and says so in the
/// UI. It used to generate fabricated patches ("{game} 汉化补丁", "https://moyu.moe/patches/example",
/// ExternalId <c>stub_trans_*</c>) with pseudo-random probability and write them into the user's
/// real SQLite database; that generation has been removed completely.
/// </summary>
public partial class PatchCenterViewModel : ObservableObject
{
    /// <summary>
    /// Factory for short-lived contexts; this ViewModel is resolved from the root container and
    /// must not hold a scoped DbContext.
    /// </summary>
    private readonly IDbContextFactory<GalboxDbContext> _dbContextFactory;
    private readonly INavigationService _navigationService;
    private readonly ILogger<PatchCenterViewModel> _logger;

    /// <summary>
    /// The DispatcherQueue of the UI thread, captured once while this ViewModel is being built on
    /// the UI thread.
    /// </summary>
    /// <remarks>
    /// THREAD AFFINITY. <c>DispatcherQueue.GetForCurrentThread()</c> answers with the queue of the
    /// CALLING thread and returns null on a thread-pool thread. The background paths below used to
    /// call it from a pool thread, so it always came back null and every "UI update" they made
    /// landed on the thread pool instead:
    ///
    ///   * <c>ObservableCollection&lt;T&gt;.Clear()/Add()</c> raises CollectionChanged, which the
    ///     XAML item controls have subscribed to (they are bound to these collections).
    ///   * A raised PropertyChanged is delivered through
    ///     <c>ABI.System.ComponentModel.PropertyChangedEventHandler.NativeDelegateWrapper</c>, which
    ///     has to create the WinRT PropertyChangedEventArgs - activation that needs the thread to
    ///     have joined an apartment. The pool thread has not, so it throws InvalidCastException
    ///     ("不支持此接口", E_NOINTERFACE) and COMException, and the process dies.
    ///
    /// Capturing the queue here is possible because a page's ViewModel is resolved in the page
    /// constructor, which the Frame runs on the UI thread. (ScrapingProgressViewModel already
    /// captures the queue the same way; these two paths simply missed it.)
    /// </remarks>
    private readonly Microsoft.UI.Dispatching.DispatcherQueue? _uiDispatcher;

    /// <summary>
    /// Whether the page is loading data.
    /// </summary>
    [ObservableProperty]
    private bool _isLoading;

    /// <summary>
    /// Error message to display if loading fails.
    /// </summary>
    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>
    /// Success message to display after operation.
    /// </summary>
    [ObservableProperty]
    private string? _successMessage;

    /// <summary>
    /// Search query text for filtering games.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredGamesNeedingPatches))]
    private string _searchQuery = string.Empty;

    /// <summary>
    /// Selected patch type filter.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredPatches))]
    private PatchTypeFilter _patchTypeFilter = PatchTypeFilter.All;

    /// <summary>
    /// Selected game that needs patches.
    /// </summary>
    [ObservableProperty]
    private GameInfo? _selectedGame;

    /// <summary>
    /// All games in the library that need patches.
    /// </summary>
    public ObservableCollection<GameInfo> GamesNeedingPatches { get; } = new();

    /// <summary>
    /// Filtered games needing patches based on search query.
    /// </summary>
    public ObservableCollection<GameInfo> FilteredGamesNeedingPatches { get; } = new();

    /// <summary>
    /// Patches available for the selected game.
    /// </summary>
    public ObservableCollection<PatchRecord> AvailablePatches { get; } = new();

    /// <summary>
    /// Filtered patches based on type filter.
    /// </summary>
    public ObservableCollection<PatchRecord> FilteredPatches { get; } = new();

    /// <summary>
    /// All installed patches across all games.
    /// </summary>
    public ObservableCollection<PatchRecord> InstalledPatches { get; } = new();

    /// <summary>
    /// Total games needing patches count.
    /// </summary>
    [ObservableProperty]
    private int _gamesNeedingPatchesCount;

    /// <summary>
    /// Available patches count for selected game.
    /// </summary>
    [ObservableProperty]
    private int _availablePatchesCount;

    /// <summary>
    /// Installed patches count.
    /// </summary>
    [ObservableProperty]
    private int _installedPatchesCount;

    /// <summary>
    /// Selected patch for details view.
    /// </summary>
    [ObservableProperty]
    private PatchRecord? _selectedPatch;

    /// <summary>
    /// Patch detail content for the selected patch.
    /// </summary>
    [ObservableProperty]
    private string? _patchDetailContent;

    /// <summary>
    /// Creates a PatchCenterViewModel with injected dependencies.
    /// </summary>
    public PatchCenterViewModel(
        IDbContextFactory<GalboxDbContext> dbContextFactory,
        INavigationService navigationService,
        ILogger<PatchCenterViewModel> logger)
    {
        _dbContextFactory = dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
        _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Captured on the UI thread (see the field's remarks). Null only if something ever
        // constructs this ViewModel off the UI thread - in which case the callers below keep their
        // previous behaviour instead of silently dropping updates.
        _uiDispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
    }

    /// <summary>
    /// Loads patch center data asynchronously.
    /// </summary>
    public async Task LoadDataAsync(object? parameter = null)
    {
        IsLoading = true;
        ErrorMessage = null;

        try
        {
            // Load games that need patches
            await LoadGamesNeedingPatchesAsync();

            // Load installed patches
            await LoadInstalledPatchesAsync();

            // Preselect the newest game so the patch area is not empty on first visit.
            // No patch data is invented: until a patch source is configured the list stays empty
            // and the empty state explains why.
            SelectDefaultGame();

            _logger.LogInformation("Loaded patch center: {GamesCount} games needing patches, {InstalledCount} installed patches",
                GamesNeedingPatchesCount, InstalledPatchesCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading patch center data");
            ErrorMessage = $"加载补丁数据失败：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Loads games that may need patches (missing translation or fix patches).
    /// </summary>
    private async Task LoadGamesNeedingPatchesAsync()
    {
        GamesNeedingPatches.Clear();

        using (var db = _dbContextFactory.CreateDbContext())
        {
            // Get all games
            var games = await db.Games
                .AsNoTracking()
                .OrderByDescending(g => g.AddedTime)
                .ToListAsync();

            // Filter games that might need patches
            // For now, we show all games as potential candidates
            foreach (var game in games)
            {
                GamesNeedingPatches.Add(game);
            }
        }

        GamesNeedingPatchesCount = GamesNeedingPatches.Count;

        // Apply initial filtering
        ApplyGameFilters();
    }

    /// <summary>
    /// Loads installed patches across all games.
    /// </summary>
    private async Task LoadInstalledPatchesAsync()
    {
        InstalledPatches.Clear();

        using (var db = _dbContextFactory.CreateDbContext())
        {
            var installed = await db.Patches
                .AsNoTracking()
                .Where(p => p.Status == PatchStatus.Installed)
                .Include(p => p.GameInfo)
                .OrderByDescending(p => p.InstalledTime)
                .ToListAsync();

            foreach (var patch in installed)
            {
                InstalledPatches.Add(patch);
            }
        }

        InstalledPatchesCount = InstalledPatches.Count;
    }

    /// <summary>
    /// Selects the newest library game so the patch panel opens on something meaningful.
    /// </summary>
    /// <remarks>
    /// This replaces the removed stub generator. That method used to also write fabricated patch
    /// rows ("{game} 汉化补丁" / "https://moyu.moe/patches/example" / ExternalId
    /// <c>stub_trans_*</c>) straight into the user's database, with the patch type decided by
    /// pseudo-random name heuristics ("name length is even", "name contains a space").
    /// </remarks>
    private void SelectDefaultGame()
    {
        if (GamesNeedingPatches.Count > 0 && SelectedGame is null)
        {
            SelectedGame = GamesNeedingPatches.FirstOrDefault();
        }
    }

    /// <summary>
    /// Loads the patches that actually exist in the database for a specific game.
    /// </summary>
    /// <remarks>
    /// No patch is ever invented here. Patch sources are not wired up yet (separate work line), so
    /// for a game without stored patches this legitimately finds nothing and the page shows its
    /// "no patch source configured" empty state.
    /// </remarks>
    public async Task LoadPatchesForGameAsync(int gameId)
    {
        AvailablePatches.Clear();

        try
        {
            // Read the patches stored for this game; nothing is generated when the list is empty.
            List<PatchRecord> existingPatches;
            using (var db = _dbContextFactory.CreateDbContext())
            {
                existingPatches = await db.Patches
                    .AsNoTracking()
                    .Where(p => p.GameInfoId == gameId)
                    .ToListAsync();
            }

            // Add to available patches collection
            foreach (var patch in existingPatches)
            {
                AvailablePatches.Add(patch);
            }

            // Apply type filter
            ApplyPatchFilters();

            AvailablePatchesCount = AvailablePatches.Count;

            _logger.LogInformation("Loaded {Count} patches for game {GameId}", AvailablePatchesCount, gameId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading patches for game {GameId}", gameId);
            ErrorMessage = $"加载补丁失败：{ex.Message}";
        }
    }

    /// <summary>
    /// Applies search filter to games needing patches.
    /// </summary>
    public void ApplyGameFilters()
    {
        FilteredGamesNeedingPatches.Clear();

        var filtered = GamesNeedingPatches.ToList();

        if (!string.IsNullOrWhiteSpace(SearchQuery))
        {
            var query = SearchQuery.ToLowerInvariant();
            filtered = filtered.Where(g =>
                (g.DisplayName?.ToLowerInvariant()?.Contains(query) ?? false) ||
                (g.NameOriginal?.ToLowerInvariant()?.Contains(query) ?? false) ||
                (g.Developer?.ToLowerInvariant()?.Contains(query) ?? false)
            ).ToList();
        }

        foreach (var game in filtered)
        {
            FilteredGamesNeedingPatches.Add(game);
        }
    }

    /// <summary>
    /// Applies patch type filter to available patches.
    /// </summary>
    public void ApplyPatchFilters()
    {
        FilteredPatches.Clear();

        var filtered = AvailablePatches.ToList();

        if (PatchTypeFilter != PatchTypeFilter.All)
        {
            var typeString = GetPatchTypeString(PatchTypeFilter);
            filtered = filtered.Where(p => p.PatchType == typeString).ToList();
        }

        foreach (var patch in filtered)
        {
            FilteredPatches.Add(patch);
        }
    }

    /// <summary>
    /// Gets the patch type string from filter enum.
    /// </summary>
    private static string GetPatchTypeString(PatchTypeFilter filter)
    {
        return filter switch
        {
            PatchTypeFilter.Translation => "Translation",
            PatchTypeFilter.Fix => "Fix",
            PatchTypeFilter.Adult => "Adult",
            PatchTypeFilter.Other => "Other",
            _ => ""
        };
    }

    /// <summary>
    /// Handles search query changed.
    /// </summary>
    partial void OnSearchQueryChanged(string value)
    {
        ApplyGameFilters();
    }

    /// <summary>
    /// Handles patch type filter changed.
    /// </summary>
    partial void OnPatchTypeFilterChanged(PatchTypeFilter value)
    {
        ApplyPatchFilters();
    }

    /// <summary>
    /// Handles selected game changed - loads patches for that game.
    /// Uses Dispatcher to ensure UI updates happen on the UI thread.
    /// </summary>
    partial void OnSelectedGameChanged(GameInfo? value)
    {
        if (value != null)
        {
            // The captured UI queue, NOT GetForCurrentThread(): this method is reached from
            // LoadDataAsync and from the item-click handler, but the work below runs on the pool.
            var dispatcher = _uiDispatcher;
            if (dispatcher == null)
            {
                // Fallback: just run directly if no dispatcher
                _ = LoadPatchesForGameSafeAsync(value.Id);
                return;
            }

            // Run the async operation, but ensure UI updates happen on UI thread
            _ = Task.Run(async () =>
            {
                try
                {
                    await LoadPatchesForGameSafeAsync(value.Id).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error loading patches for game: {GameId}", value.Id);

                    // Update error message on UI thread
                    dispatcher.TryEnqueue(() =>
                    {
                        ErrorMessage = $"加载补丁失败：{ex.Message}";
                    });
                }
            });
        }
        else
        {
            AvailablePatches.Clear();
            FilteredPatches.Clear();
            AvailablePatchesCount = 0;
        }
    }

    /// <summary>
    /// Loads available patches for a specific game with thread-safe UI updates.
    /// </summary>
    public async Task LoadPatchesForGameSafeAsync(int gameId)
    {
        try
        {
            // Read the patches stored for this game; nothing is invented when the list is empty.
            List<PatchRecord> existingPatches;
            using (var db = _dbContextFactory.CreateDbContext())
            {
                existingPatches = await db.Patches
                    .AsNoTracking()
                    .Where(p => p.GameInfoId == gameId)
                    .ToListAsync()
                    .ConfigureAwait(false);
            }

            // Get dispatcher for UI thread updates. This method is also entered from the pool
            // (OnSelectedGameChanged -> Task.Run), where GetForCurrentThread() is null and the
            // "fallback" below would mutate bound collections from a non-UI thread.
            var dispatcher = _uiDispatcher;

            if (dispatcher != null)
            {
                // Update UI on dispatcher thread
                dispatcher.TryEnqueue(() =>
                {
                    AvailablePatches.Clear();

                    // Add to available patches collection
                    foreach (var patch in existingPatches)
                    {
                        AvailablePatches.Add(patch);
                    }

                    // Apply type filter
                    ApplyPatchFilters();

                    AvailablePatchesCount = AvailablePatches.Count;
                });
            }
            else
            {
                // No dispatcher available, update directly (fallback)
                AvailablePatches.Clear();

                foreach (var patch in existingPatches)
                {
                    AvailablePatches.Add(patch);
                }

                ApplyPatchFilters();
                AvailablePatchesCount = AvailablePatches.Count;
            }

            _logger.LogInformation("Loaded {Count} patches for game {GameId}", existingPatches.Count, gameId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading patches for game {GameId}", gameId);
            ErrorMessage = $"加载补丁失败：{ex.Message}";
        }
    }

    // NOTE: the previous DownloadPatchAsync / InstallPatchAsync commands were removed.
    // They did not download or install anything: the download slept in a loop while writing a
    // fabricated local path (C:\\Galbox\\Patches\\{name}.zip), and the install only slept and
    // flipped a status column. Binding them to the patch cards would have produced two buttons
    // that claim success and change nothing, so the buttons were removed with them. Real patch
    // sources/downloads are a separate work line.

    /// <summary>
    /// Shows details for a patch.
    /// </summary>
    [RelayCommand]
    private void ShowPatchDetails(PatchRecord? patch)
    {
        if (patch == null)
        {
            _logger.LogWarning("No patch to show details");
            SelectedPatch = null;
            PatchDetailContent = null;
            return;
        }

        SelectedPatch = patch;
        PatchDetailContent = $"补丁名称: {patch.Name}\n" +
                              $"类型: {patch.PatchTypeDisplay}\n" +
                              $"版本: {patch.Version ?? "未知"}\n" +
                              $"大小: {patch.FormattedSize}\n" +
                              $"来源: {patch.Source ?? "未知"}\n" +
                              $"状态: {GetStatusDisplay(patch.Status)}\n" +
                              $"描述:\n{patch.Description ?? "无描述"}";

        _logger.LogInformation("Showing details for patch {PatchName}", patch.Name);
    }

    /// <summary>
    /// Gets the display text for a patch status.
    /// </summary>
    private static string GetStatusDisplay(PatchStatus status)
    {
        return status switch
        {
            PatchStatus.Available => "可下载",
            PatchStatus.Downloading => "下载中",
            PatchStatus.Downloaded => "已下载",
            PatchStatus.Installing => "安装中",
            PatchStatus.Installed => "已安装",
            PatchStatus.Failed => "安装失败",
            PatchStatus.NotApplicable => "不适用",
            _ => "未知"
        };
    }

    /// <summary>
    /// Refreshes the patch center data.
    /// </summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        await LoadDataAsync();
    }

    /// <summary>
    /// Navigates to game detail page.
    /// </summary>
    [RelayCommand]
    private void NavigateToGame(GameInfo? game)
    {
        if (game == null)
        {
            _logger.LogWarning("No game to navigate to");
            return;
        }

        _navigationService.NavigateTo("GameDetail", game.Id);
    }
}

/// <summary>
/// Patch type filter options.
/// </summary>
public enum PatchTypeFilter
{
    All,
    Translation,
    Fix,
    Adult,
    Other
}
