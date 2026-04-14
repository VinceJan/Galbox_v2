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
public partial class LibraryViewModel : ObservableObject
{
    private readonly GalboxDbContext _dbContext;
    private readonly INavigationService _navigationService;
    private readonly ILogger<LibraryViewModel> _logger;
    private readonly IServiceProvider _serviceProvider;
    private Process? _runningProcess;

    /// <summary>
    /// Semaphore for thread-safe database operations during quick launch.
    /// </summary>
    private readonly SemaphoreSlim _quickLaunchLock = new(1, 1);

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
    [NotifyPropertyChangedFor(nameof(IsTableView))]
    [NotifyPropertyChangedFor(nameof(IsGridView))]
    private bool _isTableView = false;

    /// <summary>
    /// Gets whether table view is selected.
    /// </summary>
    public bool IsTableView => _isTableView;

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
        GalboxDbContext dbContext,
        INavigationService navigationService,
        ILogger<LibraryViewModel> logger,
        IServiceProvider serviceProvider)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
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
            var games = await _dbContext.Games
                .OrderByDescending(g => g.AddedTime)
                .ToListAsync();

            foreach (var game in games)
            {
                AllGames.Add(game);
            }

            TotalGamesCount = AllGames.Count;

            // Apply initial filtering
            ApplyFilters();

            _logger.LogInformation("Loaded library: {TotalGames} games", TotalGamesCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading library data");
            ErrorMessage = $"Failed to load games: {ex.Message}";
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
    public async Task AddGameAsync(string folderPath, string? executablePath = null)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !System.IO.Directory.Exists(folderPath))
        {
            ErrorMessage = "Invalid folder path";
            return;
        }

        IsLoading = true;
        ErrorMessage = null;
        SuccessMessage = null;

        try
        {
            // Find executable if not provided
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                executablePath = FindExecutableInFolder(folderPath);
            }

            if (string.IsNullOrWhiteSpace(executablePath))
            {
                ErrorMessage = "No executable file found in the folder";
                return;
            }

            // Calculate folder size
            var folderSize = CalculateFolderSize(folderPath);

            // Create game entry
            var game = new GameInfo
            {
                NameOriginal = System.IO.Path.GetFileNameWithoutExtension(executablePath),
                InstallPath = folderPath,
                MainExecutable = executablePath,
                AddedTime = DateTime.UtcNow,
                UpdatedTime = DateTime.UtcNow,
                SizeBytes = folderSize,
                IsScraped = false
            };

            // Check if game already exists
            var existingGame = await _dbContext.Games
                .FirstOrDefaultAsync(g => g.InstallPath == folderPath);

            if (existingGame != null)
            {
                ErrorMessage = "This game folder is already in the library";
                return;
            }

            // Add to database
            _dbContext.Games.Add(game);
            await _dbContext.SaveChangesAsync();

            // Add to collections
            AllGames.Add(game);
            TotalGamesCount = AllGames.Count;

            // Reapply filters
            ApplyFilters();

            SuccessMessage = $"Added '{game.DisplayName}' to library";
            _logger.LogInformation("Added new game: {GameName} from {FolderPath}", game.DisplayName, folderPath);

            // Clear success message after delay
            await Task.Delay(3000);
            SuccessMessage = null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding game from {FolderPath}", folderPath);
            ErrorMessage = $"Failed to add game: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Finds an executable file in a folder.
    /// </summary>
    private string? FindExecutableInFolder(string folderPath)
    {
        // Priority order: .exe files that look like game executables
        var exeFiles = System.IO.Directory.GetFiles(folderPath, "*.exe", System.IO.SearchOption.TopDirectoryOnly);

        // Filter out common non-game executables
        var gameExecutables = exeFiles.Where(f =>
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
            // Exclude common utility/setup files
            return !name.Contains("setup") &&
                   !name.Contains("install") &&
                   !name.Contains("uninstall") &&
                   !name.Contains("config") &&
                   !name.Contains("launcher") &&
                   !name.Contains("patch") &&
                   !name.StartsWith("readme") &&
                   !name.Contains("update");
        }).ToList();

        // If no good candidates, use first exe
        if (gameExecutables.Count == 0 && exeFiles.Length > 0)
        {
            return exeFiles[0];
        }

        return gameExecutables.FirstOrDefault();
    }

    /// <summary>
    /// Calculates the total size of a folder.
    /// </summary>
    private long CalculateFolderSize(string folderPath)
    {
        try
        {
            var dirInfo = new System.IO.DirectoryInfo(folderPath);
            return dirInfo.EnumerateFiles("*", System.IO.SearchOption.AllDirectories)
                .Sum(file => file.Length);
        }
        catch
        {
            return 0;
        }
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
            ErrorMessage = "No executable path for this game";
            return;
        }

        if (!System.IO.File.Exists(game.MainExecutable))
        {
            ErrorMessage = "Executable file not found";
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

                // Update in database using the main context
                var dbGame = await _dbContext.Games.FindAsync(game.Id);
                if (dbGame != null)
                {
                    dbGame.LaunchCount = game.LaunchCount;
                    dbGame.LastSessionTime = game.LastSessionTime;
                    await _dbContext.SaveChangesAsync();
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
            ErrorMessage = $"Failed to launch: {ex.Message}";
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
            return "Never Played";
        if (game.TotalPlayTimeSeconds > 3600)
            return "Completed";
        if (game.LastSessionTime.HasValue && game.LastSessionTime >= DateTime.UtcNow.AddDays(-7))
            return "Playing";
        return "Played";
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