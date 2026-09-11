using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Galbox.App.Services;
using Galbox.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Galbox.App.ViewModels;

/// <summary>
/// ViewModel for the Library/Game Library Page.
/// Supports table/list view toggle, search, and filtering.
/// </summary>
public partial class LibraryViewModel : ObservableObject, IDisposable
{
    private readonly IDbContextFactory<GalboxDbContext> _dbContextFactory;
    private readonly INavigationService _navigationService;
    private readonly IGameUtilityService _gameUtilityService;
    private readonly ILogger<LibraryViewModel> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly IScrapingSettingsProvider _scrapingSettings;
    private Process? _runningProcess;

    /// <summary>
    /// Semaphore for thread-safe database operations during quick launch.
    /// </summary>
    private readonly SemaphoreSlim _quickLaunchLock = new(1, 1);

    /// <summary>
    /// Flag to track whether the object has been disposed.
    /// </summary>
    private bool _disposed;

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
    /// Success message to display after adding a game.
    /// </summary>
    [ObservableProperty]
    private string? _successMessage;

    /// <summary>
    /// Search query text.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredGames))]
    private string _searchQuery = string.Empty;

    /// <summary>
    /// Selected view mode: true for Table, false for List (Grid).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGridView))]
    private bool _isTableView = false;

    /// <summary>
    /// Gets whether grid/list view is selected.
    /// </summary>
    public bool IsGridView => !IsTableView;

    /// <summary>
    /// Selected filter for game status.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredGames))]
    private GameStatusFilter _statusFilter = GameStatusFilter.All;

    /// <summary>
    /// Selected sort option.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredGames))]
    private SortOption _sortOption = SortOption.AddedTimeDesc;

    /// <summary>
    /// All games in the library.
    /// </summary>
    public ObservableCollection<GameInfo> AllGames { get; } = new();

    /// <summary>
    /// Filtered and sorted games for display.
    /// </summary>
    public ObservableCollection<GameInfo> FilteredGames { get; } = new();

    /// <summary>
    /// Total games count (before filtering).
    /// </summary>
    [ObservableProperty]
    private int _totalGamesCount;

    /// <summary>
    /// Filtered games count.
    /// </summary>
    [ObservableProperty]
    private int _filteredGamesCount;

    /// <summary>
    /// Creates a LibraryViewModel with injected dependencies.
    /// </summary>
    public LibraryViewModel(
        IDbContextFactory<GalboxDbContext> dbContextFactory,
        INavigationService navigationService,
        IGameUtilityService gameUtilityService,
        ILogger<LibraryViewModel> logger,
        IServiceProvider serviceProvider,
        IScrapingSettingsProvider scrapingSettings)
    {
        _dbContextFactory = dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
        _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
        _gameUtilityService = gameUtilityService ?? throw new ArgumentNullException(nameof(gameUtilityService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _scrapingSettings = scrapingSettings ?? throw new ArgumentNullException(nameof(scrapingSettings));
    }

    /// <summary>
    /// Loads library data asynchronously.
    /// </summary>
    public async Task LoadDataAsync(object? parameter = null)
    {
        IsLoading = true;
        ErrorMessage = null;

        try
        {
            // Handle navigation parameter (favorites, playing, etc.)
            if (parameter is string filterParam)
            {
                if (filterParam == "favorites")
                {
                    StatusFilter = GameStatusFilter.Favorites;
                }
                else if (filterParam == "playing")
                {
                    StatusFilter = GameStatusFilter.Playing;
                }
            }

            // Load all games
            AllGames.Clear();
            using (var db = _dbContextFactory.CreateDbContext())
            {
                var games = await db.Games
                    .AsNoTracking()
                    .OrderByDescending(g => g.AddedTime)
                    .ToListAsync();

                foreach (var game in games)
                {
                    AllGames.Add(game);
                }
            }

            TotalGamesCount = AllGames.Count;

            // Apply initial filtering
            ApplyFilters();

            _logger.LogInformation("Loaded library: {TotalGames} games", TotalGamesCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading library data");
            ErrorMessage = $"加载游戏失败：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Applies search and filters to the game list.
    /// </summary>
    public void ApplyFilters()
    {
        FilteredGames.Clear();

        var filtered = AllGames.ToList();

        // Apply search filter
        if (!string.IsNullOrWhiteSpace(SearchQuery))
        {
            var query = SearchQuery.ToLowerInvariant();
            filtered = filtered.Where(g =>
                (g.DisplayName?.ToLowerInvariant()?.Contains(query) ?? false) ||
                (g.Developer?.ToLowerInvariant().Contains(query) ?? false) ||
                (g.NameOriginal?.ToLowerInvariant().Contains(query) ?? false) ||
                (g.NameCn?.ToLowerInvariant().Contains(query) ?? false) ||
                // Search in tags if available
                (g.TagsJson?.ToLowerInvariant().Contains(query) ?? false)
            ).ToList();
        }

        // Apply status filter
        filtered = ApplyStatusFilter(filtered, StatusFilter);

        // Apply sort
        filtered = ApplySort(filtered, SortOption);

        // Add to filtered collection
        foreach (var game in filtered)
        {
            FilteredGames.Add(game);
        }

        FilteredGamesCount = FilteredGames.Count;

        _logger.LogDebug("Applied filters: {FilteredCount} of {TotalCount} games",
            FilteredGamesCount, TotalGamesCount);
    }

    /// <summary>
    /// Applies status filter to the game list.
    /// </summary>
    private List<GameInfo> ApplyStatusFilter(List<GameInfo> games, GameStatusFilter filter)
    {
        return filter switch
        {
            GameStatusFilter.All => games,
            GameStatusFilter.Favorites => games.Where(g => g.IsFavorite).ToList(),
            GameStatusFilter.Playing => games.Where(g => g.LastSessionTime.HasValue &&
                g.LastSessionTime >= DateTime.UtcNow.AddDays(-7)).ToList(),
            GameStatusFilter.Completed => games.Where(g => g.LaunchCount > 0 &&
                g.TotalPlayTimeSeconds > 3600).ToList(), // Played more than 1 hour
            GameStatusFilter.NeverPlayed => games.Where(g => g.LaunchCount == 0).ToList(),
            GameStatusFilter.RecentlyAdded => games.Where(g => g.AddedTime >= DateTime.UtcNow.AddDays(-30)).ToList(),
            _ => games
        };
    }

    /// <summary>
    /// Applies sort to the game list.
    /// </summary>
    private List<GameInfo> ApplySort(List<GameInfo> games, SortOption sort)
    {
        return sort switch
        {
            SortOption.AddedTimeDesc => games.OrderByDescending(g => g.AddedTime).ToList(),
            SortOption.AddedTimeAsc => games.OrderBy(g => g.AddedTime).ToList(),
            SortOption.NameAsc => games.OrderBy(g => g.DisplayName).ToList(),
            SortOption.NameDesc => games.OrderByDescending(g => g.DisplayName).ToList(),
            SortOption.ReleaseDateDesc => games.OrderByDescending(g => g.ReleaseDate ?? DateTime.MinValue).ToList(),
            SortOption.ReleaseDateAsc => games.OrderBy(g => g.ReleaseDate ?? DateTime.MinValue).ToList(),
            SortOption.PlayTimeDesc => games.OrderByDescending(g => g.TotalPlayTimeSeconds).ToList(),
            SortOption.PlayTimeAsc => games.OrderBy(g => g.TotalPlayTimeSeconds).ToList(),
            SortOption.LastPlayedDesc => games.OrderByDescending(g => g.LastSessionTime ?? DateTime.MinValue).ToList(),
            SortOption.LastPlayedAsc => games.OrderBy(g => g.LastSessionTime ?? DateTime.MinValue).ToList(),
            SortOption.RatingDesc => games.OrderByDescending(g => g.Rating ?? 0).ToList(),
            SortOption.RatingAsc => games.OrderBy(g => g.Rating ?? 0).ToList(),
            _ => games.OrderByDescending(g => g.AddedTime).ToList()
        };
    }

    /// <summary>
    /// Handles search query changed.
    /// </summary>
    partial void OnSearchQueryChanged(string value)
    {
        ApplyFilters();
    }

    /// <summary>
    /// Handles status filter changed.
    /// </summary>
    partial void OnStatusFilterChanged(GameStatusFilter value)
    {
        ApplyFilters();
    }

    /// <summary>
    /// Handles sort option changed.
    /// </summary>
    partial void OnSortOptionChanged(SortOption value)
    {
        ApplyFilters();
    }

    /// <summary>
    /// Toggles between table and grid view.
    /// </summary>
    [RelayCommand]
    private void ToggleView()
    {
        IsTableView = !IsTableView;
        _logger.LogInformation("View mode changed to {ViewMode}", IsTableView ? "Table" : "Grid");
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
        _logger.LogInformation("Navigating to game detail: {GameName} (ID: {GameId})", game.DisplayName, game.Id);
    }

    /// <summary>
    /// Refreshes the library data.
    /// </summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        await LoadDataAsync();
    }

    /// <summary>
    /// Adds a game from a selected folder path.
    /// </summary>
    /// <param name="folderPath">The path to the game folder.</param>
    /// <param name="executablePath">Optional path to the main executable.</param>
    /// <returns>The id of the newly added game, or 0 when nothing was added.</returns>
    public async Task<int> AddGameAsync(string folderPath, string? executablePath = null)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !System.IO.Directory.Exists(folderPath))
        {
            ErrorMessage = "无效的文件夹路径";
            return 0;
        }

        IsLoading = true;
        ErrorMessage = null;
        SuccessMessage = null;

        try
        {
            // Find executable if not provided
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                executablePath = _gameUtilityService.FindExecutableInFolder(folderPath);
            }

            if (string.IsNullOrWhiteSpace(executablePath))
            {
                ErrorMessage = "文件夹中没有找到可执行文件";
                return 0;
            }

            // Calculate folder size
            var folderSize = _gameUtilityService.CalculateFolderSize(folderPath);

            // Use folder name as game name (better for most games)
            var folderName = System.IO.Path.GetFileName(folderPath.TrimEnd(System.IO.Path.DirectorySeparatorChar));

            // Create game entry
            var game = new GameInfo
            {
                NameOriginal = folderName, // Use folder name instead of exe name
                InstallPath = folderPath,
                MainExecutable = executablePath,
                AddedTime = DateTime.UtcNow,
                UpdatedTime = DateTime.UtcNow,
                SizeBytes = folderSize,
                IsScraped = false
            };

            // Detect engine type
            game.EngineType = EngineSaveDetector.DetectEngineType(game);
            _logger.LogInformation("Detected engine type: {EngineType} for game: {GameName}", game.EngineType, game.NameOriginal);

            // Check if game already exists
            using (var db = _dbContextFactory.CreateDbContext())
            {
                var existingGame = await db.Games
                    .FirstOrDefaultAsync(g => g.InstallPath == folderPath);

                if (existingGame != null)
                {
                    ErrorMessage = "该游戏文件夹已在游戏库中";
                    return 0;
                }

                // Add to database
                db.Games.Add(game);
                await db.SaveChangesAsync();
            }

            // Add to collections
            AllGames.Add(game);
            TotalGamesCount = AllGames.Count;

            // Reapply filters
            ApplyFilters();

            SuccessMessage = $"已将 '{game.DisplayName}' 添加到游戏库 (引擎: {game.EngineType})";
            _logger.LogInformation("Added new game: {GameName} from {FolderPath}", game.DisplayName, folderPath);

            // D1: honor the "scrape automatically when a game is added" setting, which
            // previously had no consumer at all. Navigating to the scraping view makes
            // the automatic run visible instead of silently happening off-screen.
            await TryStartAutoScrapeAsync(game.Id);

            // Clear success message after delay
            await Task.Delay(3000);
            SuccessMessage = null;

            return game.Id;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding game from {FolderPath}", folderPath);
            ErrorMessage = $"添加游戏失败：{ex.Message}";
            return 0;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Navigates to the scraping view when "add game then auto scrape" is enabled.
    /// </summary>
    private async Task TryStartAutoScrapeAsync(int gameId)
    {
        try
        {
            await _scrapingSettings.RefreshAsync();

            if (!_scrapingSettings.AutoScrapeOnAdd)
            {
                return;
            }

            _logger.LogInformation("AutoScrapeOnAdd is enabled, starting scraping for game {GameId}", gameId);
            _navigationService.NavigateTo("ScrapingProgress", gameId);
        }
        catch (Exception ex)
        {
            // Auto scraping must never break adding a game.
            _logger.LogWarning(ex, "Failed to start automatic scraping for game {GameId}", gameId);
        }
    }

    /// <summary>
    /// Scans a folder for games and adds them to the library.
    /// </summary>
    /// <param name="rootFolder">The root folder to scan for games.</param>
    /// <returns>The number of games found and added.</returns>
    public async Task<int> ScanFolderAsync(string rootFolder)
    {
        if (string.IsNullOrWhiteSpace(rootFolder) || !System.IO.Directory.Exists(rootFolder))
        {
            ErrorMessage = "无效的文件夹路径";
            return 0;
        }

        IsLoading = true;
        ErrorMessage = null;
        SuccessMessage = null;

        int addedCount = 0;
        int skippedCount = 0;

        try
        {
            // Get all subdirectories that might contain games
            var subdirectories = System.IO.Directory.GetDirectories(rootFolder, "*", System.IO.SearchOption.TopDirectoryOnly);

            foreach (var subDir in subdirectories)
            {
                try
                {
                    // Check if this directory contains an executable (potential game)
                    var exeFiles = System.IO.Directory.GetFiles(subDir, "*.exe", System.IO.SearchOption.TopDirectoryOnly);
                    if (exeFiles.Length == 0)
                    {
                        // Skip directories without exe files
                        skippedCount++;
                        continue;
                    }

                    // Find the best executable
                    var executablePath = _gameUtilityService.FindExecutableInFolder(subDir);
                    if (string.IsNullOrWhiteSpace(executablePath))
                    {
                        skippedCount++;
                        continue;
                    }

                    // Check if game already exists
                    using var db = _dbContextFactory.CreateDbContext();

                    var existingGame = await db.Games
                        .FirstOrDefaultAsync(g => g.InstallPath == subDir);
                    if (existingGame != null)
                    {
                        skippedCount++;
                        continue;
                    }

                    // Calculate folder size
                    var folderSize = _gameUtilityService.CalculateFolderSize(subDir);
                    var folderName = System.IO.Path.GetFileName(subDir.TrimEnd(System.IO.Path.DirectorySeparatorChar));

                    // Create game entry
                    var game = new GameInfo
                    {
                        NameOriginal = folderName,
                        InstallPath = subDir,
                        MainExecutable = executablePath,
                        AddedTime = DateTime.UtcNow,
                        UpdatedTime = DateTime.UtcNow,
                        SizeBytes = folderSize,
                        IsScraped = false
                    };

                    // Detect engine type
                    game.EngineType = EngineSaveDetector.DetectEngineType(game);
                    _logger.LogInformation("Detected engine type: {EngineType} for game: {GameName}", game.EngineType, game.NameOriginal);

                    // Add to database
                    db.Games.Add(game);
                    await db.SaveChangesAsync();

                    // Add to collections
                    AllGames.Add(game);
                    addedCount++;

                    _logger.LogInformation("Scanned and added game: {GameName} (Engine: {EngineType})", game.DisplayName, game.EngineType);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to scan folder: {Folder}", subDir);
                    skippedCount++;
                }
            }

            TotalGamesCount = AllGames.Count;
            ApplyFilters();

            SuccessMessage = $"扫描完成：添加 {addedCount} 个游戏，跳过 {skippedCount} 个文件夹";
            _logger.LogInformation("Folder scan completed: {Added} games added, {Skipped} skipped from {RootFolder}", addedCount, skippedCount, rootFolder);

            // Clear success message after delay
            await Task.Delay(5000);
            SuccessMessage = null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error scanning folder {RootFolder}", rootFolder);
            ErrorMessage = $"扫描失败：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }

        return addedCount;
    }

    /// <summary>
    /// Quick launches a game directly.
    /// </summary>
    [RelayCommand]
    private async Task QuickLaunchGameAsync(GameInfo? game)
    {
        if (game == null)
        {
            _logger.LogWarning("No game to quick launch");
            return;
        }

        if (string.IsNullOrWhiteSpace(game.MainExecutable))
        {
            ErrorMessage = "该游戏没有可执行文件路径";
            return;
        }

        if (!System.IO.File.Exists(game.MainExecutable))
        {
            ErrorMessage = "未找到可执行文件";
            return;
        }

        // Ensure thread-safe operation
        await _quickLaunchLock.WaitAsync();
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = game.MainExecutable,
                WorkingDirectory = game.InstallPath,
                UseShellExecute = true
            };

            _runningProcess = Process.Start(startInfo);
            if (_runningProcess != null)
            {
                _logger.LogInformation("Quick launched game: {GameName}", game.DisplayName);

                // Update launch statistics
                game.LaunchCount++;
                game.LastSessionTime = DateTime.UtcNow;

                // Update in database using a short-lived context
                using (var db = _dbContextFactory.CreateDbContext())
                {
                    var dbGame = await db.Games.FindAsync(game.Id);
                    if (dbGame != null)
                    {
                        dbGame.LaunchCount = game.LaunchCount;
                        dbGame.LastSessionTime = game.LastSessionTime;
                        await db.SaveChangesAsync();
                    }
                }

                // Capture game ID for background task
                var gameId = game.Id;
                var startTime = game.LastSessionTime ?? DateTime.UtcNow;

                // Wait for process to exit using a separate scope/context for thread safety
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _runningProcess.WaitForExitAsync();
                        var exitTime = DateTime.UtcNow;
                        var sessionDuration = exitTime - startTime;

                        // Create a new scope for background database operation
                        using var scope = _serviceProvider.CreateScope();
                        var backgroundDbContext = scope.ServiceProvider.GetRequiredService<GalboxDbContext>();

                        // Update play time
                        var dbGameUpdate = await backgroundDbContext.Games.FindAsync(gameId);
                        if (dbGameUpdate != null)
                        {
                            dbGameUpdate.TotalPlayTimeSeconds += (long)sessionDuration.TotalSeconds;
                            await backgroundDbContext.SaveChangesAsync();
                        }

                        _logger.LogInformation("Game session ended: {GameId}, duration: {Duration} seconds",
                            gameId, (long)sessionDuration.TotalSeconds);
                    }
                    catch (Exception bgEx)
                    {
                        _logger.LogError(bgEx, "Error updating play time for game {GameId}", gameId);
                    }
                    finally
                    {
                        _runningProcess?.Dispose();
                        _runningProcess = null;
                    }
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error quick launching game: {GameName}", game.DisplayName);
            ErrorMessage = $"启动失败：{ex.Message}";
        }
        finally
        {
            _quickLaunchLock.Release();
        }
    }

    /// <summary>
    /// Gets the status display text for a game.
    /// </summary>
    public static string GetGameStatus(GameInfo game)
    {
        if (game.LaunchCount == 0)
            return "从未游玩";
        if (game.TotalPlayTimeSeconds > 3600)
            return "已完成";
        if (game.LastSessionTime.HasValue && game.LastSessionTime >= DateTime.UtcNow.AddDays(-7))
            return "正在游玩";
        return "已游玩";
    }

    /// <summary>
    /// Sets the sort option from table header click.
    /// Toggles between ascending and descending for same column.
    /// </summary>
    public void SetSortFromColumn(string columnName)
    {
        var newSort = columnName switch
        {
            "Name" => SortOption == SortOption.NameAsc ? SortOption.NameDesc : SortOption.NameAsc,
            "AddedTime" => SortOption == SortOption.AddedTimeDesc ? SortOption.AddedTimeAsc : SortOption.AddedTimeDesc,
            "LastPlayed" => SortOption == SortOption.LastPlayedDesc ? SortOption.LastPlayedAsc : SortOption.LastPlayedDesc,
            "PlayTime" => SortOption == SortOption.PlayTimeDesc ? SortOption.PlayTimeAsc : SortOption.PlayTimeDesc,
            "ReleaseDate" => SortOption == SortOption.ReleaseDateDesc ? SortOption.ReleaseDateAsc : SortOption.ReleaseDateDesc,
            "Rating" => SortOption == SortOption.RatingDesc ? SortOption.RatingAsc : SortOption.RatingDesc,
            _ => SortOption.AddedTimeDesc
        };

        SortOption = newSort;
    }

    /// <summary>
    /// Releases all resources used by the LibraryViewModel.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Dispose the semaphore
        _quickLaunchLock.Dispose();

        // Dispose any running process
        if (_runningProcess != null)
        {
            try
            {
                if (!_runningProcess.HasExited)
                {
                    _runningProcess.Kill();
                }
                _runningProcess.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing running process");
            }
            _runningProcess = null;
        }

        _logger.LogInformation("LibraryViewModel disposed");
    }
}

/// <summary>
/// Game status filter options.
/// </summary>
public enum GameStatusFilter
{
    All,
    Favorites,
    Playing,
    Completed,
    NeverPlayed,
    RecentlyAdded
}

/// <summary>
/// Sort options for game list.
/// Order matches LibraryPage.xaml ComboBox items.
/// </summary>
public enum SortOption
{
    AddedTimeDesc,      // Added (Newest) - index 0
    AddedTimeAsc,       // Added (Oldest) - index 1
    NameAsc,            // Name (A-Z) - index 2
    NameDesc,           // Name (Z-A) - index 3
    ReleaseDateDesc,    // Release Date (Newest) - index 4
    ReleaseDateAsc,     // Release Date (Oldest)
    PlayTimeDesc,       // Play Time (Most) - index 5
    PlayTimeAsc,        // Play Time (Least)
    LastPlayedDesc,     // Last Played (Recent) - index 6
    LastPlayedAsc,      // Last Played (Old)
    RatingDesc,         // Rating (High) - index 7
    RatingAsc           // Rating (Low)
}