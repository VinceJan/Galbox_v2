using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Galbox.App.Services;
using Galbox.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Galbox.App.ViewModels;

/// <summary>
/// ViewModel for the Main/Home Page.
/// Displays quick launch, recent games, and currently playing sections.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly GalboxDbContext _dbContext;
    private readonly INavigationService _navigationService;
    private readonly IGameUtilityService _gameUtilityService;
    private readonly ILogger<MainViewModel> _logger;

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
    /// Quick launch game (pinned or last played).
    /// </summary>
    [ObservableProperty]
    private GameInfo? _quickLaunchGame;

    /// <summary>
    /// Recent games ordered by last played time.
    /// </summary>
    public ObservableCollection<GameInfo> RecentGames { get; } = new();

    /// <summary>
    /// Currently playing games (games with play sessions).
    /// </summary>
    public ObservableCollection<GameInfo> CurrentlyPlayingGames { get; } = new();

    /// <summary>
    /// Favorite games for quick access.
    /// </summary>
    public ObservableCollection<GameInfo> FavoriteGames { get; } = new();

    /// <summary>
    /// Total games in library.
    /// </summary>
    [ObservableProperty]
    private int _totalGamesCount;

    /// <summary>
    /// Event to request folder picker dialog.
    /// </summary>
    public event EventHandler? RequestFolderPicker;

    /// <summary>
    /// Creates a MainViewModel with injected dependencies.
    /// </summary>
    public MainViewModel(
        GalboxDbContext dbContext,
        INavigationService navigationService,
        IGameUtilityService gameUtilityService,
        ILogger<MainViewModel> logger)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
        _gameUtilityService = gameUtilityService ?? throw new ArgumentNullException(nameof(gameUtilityService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Loads home page data asynchronously.
    /// </summary>
    public async Task LoadDataAsync()
    {
        IsLoading = true;
        ErrorMessage = null;

        try
        {
            // Load total games count
            TotalGamesCount = await _dbContext.Games.CountAsync();

            // Load favorite games (top 5)
            FavoriteGames.Clear();
            var favorites = await _dbContext.Games
                .Where(g => g.IsFavorite)
                .OrderByDescending(g => g.LastSessionTime ?? g.AddedTime)
                .Take(5)
                .ToListAsync();

            foreach (var game in favorites)
            {
                FavoriteGames.Add(game);
            }

            // Load recent games (last played, top 10)
            RecentGames.Clear();
            var recentGames = await _dbContext.Games
                .Where(g => g.LastSessionTime != null)
                .OrderByDescending(g => g.LastSessionTime)
                .Take(10)
                .ToListAsync();

            foreach (var game in recentGames)
            {
                RecentGames.Add(game);
            }

            // Load currently playing games (games played in last 7 days)
            CurrentlyPlayingGames.Clear();
            var lastWeek = DateTime.UtcNow.AddDays(-7);
            var playingGames = await _dbContext.Games
                .Where(g => g.LastSessionTime != null && g.LastSessionTime >= lastWeek)
                .OrderByDescending(g => g.TotalPlayTimeSeconds)
                .Take(10)
                .ToListAsync();

            foreach (var game in playingGames)
            {
                CurrentlyPlayingGames.Add(game);
            }

            // Set quick launch game (favorite with most recent session, or first favorite)
            QuickLaunchGame = FavoriteGames.FirstOrDefault() ?? RecentGames.FirstOrDefault();

            _logger.LogInformation("Loaded home page data: {TotalGames} games, {RecentCount} recent, {PlayingCount} playing",
                TotalGamesCount, RecentGames.Count, CurrentlyPlayingGames.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading home page data");
            ErrorMessage = $"Failed to load data: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
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
    /// Launches the quick launch game.
    /// </summary>
    [RelayCommand]
    private void QuickLaunch()
    {
        if (QuickLaunchGame == null)
        {
            _logger.LogWarning("No quick launch game available");
            return;
        }

        NavigateToGame(QuickLaunchGame);
    }

    /// <summary>
    /// Navigates to library page to see all games.
    /// </summary>
    [RelayCommand]
    private void NavigateToLibrary()
    {
        _navigationService.NavigateTo("Library");
    }

    /// <summary>
    /// Navigates to all currently playing games.
    /// </summary>
    [RelayCommand]
    private void NavigateToCurrentlyPlaying()
    {
        _navigationService.NavigateTo("Library", "playing");
    }

    /// <summary>
    /// Navigates to favorites list.
    /// </summary>
    [RelayCommand]
    private void NavigateToFavorites()
    {
        _navigationService.NavigateTo("Library", "favorites");
    }

    /// <summary>
    /// Requests folder picker to add a new game.
    /// </summary>
    [RelayCommand]
    private void AddGame()
    {
        RequestFolderPicker?.Invoke(this, EventArgs.Empty);
        _logger.LogInformation("Requesting folder picker for adding game");
    }

    /// <summary>
    /// Adds a game from a selected folder path.
    /// </summary>
    /// <param name="folderPath">The path to the game folder.</param>
    public async Task AddGameAsync(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !System.IO.Directory.Exists(folderPath))
        {
            ErrorMessage = "无效的文件夹路径";
            return;
        }

        IsLoading = true;
        ErrorMessage = null;
        SuccessMessage = null;

        try
        {
            // Find executable in folder
            var executablePath = _gameUtilityService.FindExecutableInFolder(folderPath);

            if (string.IsNullOrWhiteSpace(executablePath))
            {
                ErrorMessage = "文件夹中没有找到可执行文件";
                return;
            }

            // Calculate folder size
            var folderSize = _gameUtilityService.CalculateFolderSize(folderPath);

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
                ErrorMessage = "该游戏文件夹已在游戏库中";
                return;
            }

            // Add to database
            _dbContext.Games.Add(game);
            await _dbContext.SaveChangesAsync();

            // Update total count
            TotalGamesCount = await _dbContext.Games.CountAsync();

            // Add to recent games
            RecentGames.Insert(0, game);

            // Set as quick launch game if none exists
            if (QuickLaunchGame == null)
            {
                QuickLaunchGame = game;
            }

            SuccessMessage = $"已将 '{game.DisplayName}' 添加到游戏库";
            _logger.LogInformation("Added new game: {GameName} from {FolderPath}", game.DisplayName, folderPath);

            // Clear success message after delay
            await Task.Delay(3000);
            SuccessMessage = null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding game from {FolderPath}", folderPath);
            ErrorMessage = $"添加游戏失败：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }
}