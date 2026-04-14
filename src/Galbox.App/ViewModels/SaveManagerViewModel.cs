using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Galbox.App.Services;
using Galbox.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Galbox.App.ViewModels;

/// <summary>
/// ViewModel for the Save Manager Page.
/// Manages game save backups, restoration, and quick switching.
/// </summary>
public partial class SaveManagerViewModel : ObservableObject
{
    /// <summary>
    /// Delay in milliseconds before clearing success messages.
    /// </summary>
    private const int SuccessMessageDelayMs = 3000;

    private readonly GalboxDbContext _dbContext;
    private readonly ISaveManagementService _saveManagementService;
    private readonly ILogger<SaveManagerViewModel> _logger;

    /// <summary>
    /// Whether the page is loading data.
    /// </summary>
    [ObservableProperty]
    private bool _isLoading;

    /// <summary>
    /// Whether backups are being loaded for selected game.
    /// </summary>
    [ObservableProperty]
    private bool _isLoadingBackups;

    /// <summary>
    /// Whether a backup operation is in progress.
    /// </summary>
    [ObservableProperty]
    private bool _isBackupInProgress;

    /// <summary>
    /// Current backup operation progress percentage.
    /// </summary>
    [ObservableProperty]
    private int _backupProgress;

    /// <summary>
    /// Current backup operation phase description.
    /// </summary>
    [ObservableProperty]
    private string? _backupPhaseDescription;

    /// <summary>
    /// Error message to display.
    /// </summary>
    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>
    /// Success message to display.
    /// </summary>
    [ObservableProperty]
    private string? _successMessage;

    /// <summary>
    /// Search query for filtering games.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredGames))]
    private string _searchQuery = string.Empty;

    /// <summary>
    /// Currently selected game.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedGame))]
    [NotifyPropertyChangedFor(nameof(SelectedGameDisplayName))]
    [NotifyPropertyChangedFor(nameof(SelectedGameBackupCount))]
    [NotifyPropertyChangedFor(nameof(SelectedBackup))]
    private GameSaveInfo? _selectedGame;

    /// <summary>
    /// Whether a game is selected.
    /// </summary>
    public bool HasSelectedGame => SelectedGame != null;

    /// <summary>
    /// Display name of selected game.
    /// </summary>
    public string SelectedGameDisplayName => SelectedGame?.DisplayName ?? "No Game Selected";

    /// <summary>
    /// Backup count for selected game.
    /// </summary>
    public int SelectedGameBackupCount => SelectedGame?.BackupCount ?? 0;

    /// <summary>
    /// Currently selected backup for viewing details.
    /// </summary>
    [ObservableProperty]
    private GameSaveBackup? _selectedBackup;

    /// <summary>
    /// Description for new backup.
    /// </summary>
    [ObservableProperty]
    private string _newBackupDescription = string.Empty;

    /// <summary>
    /// Whether auto-backup is enabled globally.
    /// </summary>
    [ObservableProperty]
    private bool _autoBackupEnabled;

    /// <summary>
    /// All games with save backup capability.
    /// </summary>
    public ObservableCollection<GameSaveInfo> AllGames { get; } = new();

    /// <summary>
    /// Filtered games for display.
    /// </summary>
    public ObservableCollection<GameSaveInfo> FilteredGames { get; } = new();

    /// <summary>
    /// Backups for selected game.
    /// </summary>
    public ObservableCollection<GameSaveBackup> SelectedGameBackups { get; } = new();

    /// <summary>
    /// Total games count.
    /// </summary>
    [ObservableProperty]
    private int _totalGamesCount;

    /// <summary>
    /// Filtered games count.
    /// </summary>
    [ObservableProperty]
    private int _filteredGamesCount;

    /// <summary>
    /// Creates a SaveManagerViewModel with injected dependencies.
    /// </summary>
    public SaveManagerViewModel(
        GalboxDbContext dbContext,
        ISaveManagementService saveManagementService,
        ILogger<SaveManagerViewModel> logger)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _saveManagementService = saveManagementService ?? throw new ArgumentNullException(nameof(saveManagementService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Initialize auto-backup setting from service
        _autoBackupEnabled = _saveManagementService.AutoBackupEnabled;
    }

    /// <summary>
    /// Loads games with save backup capability asynchronously.
    /// </summary>
    public async Task LoadDataAsync(object? parameter = null)
    {
        IsLoading = true;
        ErrorMessage = null;

        try
        {
            // Load all games
            AllGames.Clear();

            var games = await _dbContext.Games
                .OrderByDescending(g => g.LastSessionTime)
                .ToListAsync();

            // Convert to GameSaveInfo with backup counts
            foreach (var game in games)
            {
                var backupCount = await _dbContext.SaveBackups
                    .CountAsync(b => b.GameInfoId == game.Id);

                var saveInfo = new GameSaveInfo
                {
                    Id = game.Id,
                    DisplayName = game.DisplayName,
                    NameCn = game.NameCn,
                    NameOriginal = game.NameOriginal,
                    CoverImagePath = game.CoverImagePath,
                    InstallPath = game.InstallPath,
                    BackupCount = backupCount,
                    HasSaves = backupCount > 0,
                    LastBackupTime = await GetLastBackupTimeAsync(game.Id),
                    LastSessionTime = game.LastSessionTime,
                    TotalPlayTimeSeconds = game.TotalPlayTimeSeconds
                };

                AllGames.Add(saveInfo);
            }

            TotalGamesCount = AllGames.Count;

            // Apply initial filtering
            ApplyFilters();

            _logger.LogInformation("Loaded save manager: {TotalGames} games", TotalGamesCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading save manager data");
            ErrorMessage = $"Failed to load games: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Gets the last backup time for a game.
    /// </summary>
    private async Task<DateTime?> GetLastBackupTimeAsync(int gameId)
    {
        var lastBackup = await _dbContext.SaveBackups
            .Where(b => b.GameInfoId == gameId)
            .OrderByDescending(b => b.CreatedTime)
            .FirstOrDefaultAsync();

        return lastBackup?.CreatedTime;
    }

    /// <summary>
    /// Applies search filter to the game list.
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
                (g.NameOriginal?.ToLowerInvariant().Contains(query) ?? false) ||
                (g.NameCn?.ToLowerInvariant().Contains(query) ?? false)
            ).ToList();
        }

        // Add to filtered collection
        foreach (var game in filtered)
        {
            FilteredGames.Add(game);
        }

        FilteredGamesCount = FilteredGames.Count;
    }

    /// <summary>
    /// Handles search query changed.
    /// </summary>
    partial void OnSearchQueryChanged(string value)
    {
        ApplyFilters();
    }

    /// <summary>
    /// Handles selected game changed - loads its backups.
    /// </summary>
    partial void OnSelectedGameChanged(GameSaveInfo? value)
    {
        if (value != null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await LoadBackupsForGameAsync(value.Id).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error loading backups for game: {GameId}", value.Id);
                    ErrorMessage = $"Failed to load backups: {ex.Message}";
                }
            });
        }
        else
        {
            SelectedGameBackups.Clear();
            SelectedBackup = null;
        }
    }

    /// <summary>
    /// Handles auto-backup setting changed.
    /// </summary>
    partial void OnAutoBackupEnabledChanged(bool value)
    {
        _saveManagementService.AutoBackupEnabled = value;
        _logger.LogInformation("Auto-backup setting changed to: {Enabled}", value);
    }

    /// <summary>
    /// Loads backups for a specific game.
    /// </summary>
    public async Task LoadBackupsForGameAsync(int gameId)
    {
        IsLoadingBackups = true;
        ErrorMessage = null;

        try
        {
            SelectedGameBackups.Clear();
            SelectedBackup = null;

            var backups = await _saveManagementService.GetSaveBackupsAsync(gameId);

            foreach (var backup in backups)
            {
                SelectedGameBackups.Add(backup);
            }

            // Update backup count in selected game info
            if (SelectedGame != null && SelectedGame.Id == gameId)
            {
                SelectedGame.BackupCount = backups.Count;
                SelectedGame.HasSaves = backups.Count > 0;
                SelectedGame.LastBackupTime = backups.MaxBy(b => b.CreatedTime)?.CreatedTime;
                OnPropertyChanged(nameof(SelectedGameBackupCount));
            }

            _logger.LogInformation("Loaded {Count} backups for game: {GameId}", backups.Count, gameId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading backups for game: {GameId}", gameId);
            ErrorMessage = $"Failed to load backups: {ex.Message}";
        }
        finally
        {
            IsLoadingBackups = false;
        }
    }

    /// <summary>
    /// Creates a new backup for the selected game.
    /// </summary>
    [RelayCommand]
    private async Task CreateBackupAsync()
    {
        if (SelectedGame == null)
        {
            ErrorMessage = "Please select a game first";
            return;
        }

        IsBackupInProgress = true;
        BackupProgress = 0;
        BackupPhaseDescription = "Starting backup...";
        ErrorMessage = null;
        SuccessMessage = null;

        try
        {
            // Get full game info from database
            var game = await _dbContext.Games.FindAsync(SelectedGame.Id);
            if (game == null)
            {
                ErrorMessage = "Game not found in database";
                return;
            }

            // Create progress reporter
            var progress = new Progress<BackupProgress>(p =>
            {
                BackupProgress = p.Percentage;
                BackupPhaseDescription = GetPhaseDescription(p.Phase);
            });

            // Create backup
            var backup = await _saveManagementService.CreateBackupAsync(
                game,
                NewBackupDescription ?? $"Manual backup - {DateTime.UtcNow:yyyy-MM-dd HH:mm}",
                progress);

            if (backup != null)
            {
                SelectedGameBackups.Insert(0, backup);
                SelectedGame.BackupCount = SelectedGameBackups.Count;
                SelectedGame.HasSaves = true;
                SelectedGame.LastBackupTime = backup.CreatedTime;
                OnPropertyChanged(nameof(SelectedGameBackupCount));

                NewBackupDescription = string.Empty;
                SuccessMessage = $"Backup created successfully: {backup.Name}";
                _logger.LogInformation("Backup created: {BackupName} for game {GameId}", backup.Name, game.Id);

                // Clear success message after delay
                await Task.Delay(SuccessMessageDelayMs);
                SuccessMessage = null;
            }
            else
            {
                ErrorMessage = "Failed to create backup. No save files detected.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating backup for game: {GameId}", SelectedGame.Id);
            ErrorMessage = $"Failed to create backup: {ex.Message}";
        }
        finally
        {
            IsBackupInProgress = false;
            BackupProgress = 0;
            BackupPhaseDescription = null;
        }
    }

    /// <summary>
    /// Restores the selected backup.
    /// </summary>
    [RelayCommand]
    private async Task RestoreBackupAsync()
    {
        if (SelectedBackup == null)
        {
            ErrorMessage = "Please select a backup to restore";
            return;
        }

        IsBackupInProgress = true;
        BackupProgress = 0;
        BackupPhaseDescription = "Starting restoration...";
        ErrorMessage = null;
        SuccessMessage = null;

        try
        {
            var progress = new Progress<BackupProgress>(p =>
            {
                BackupProgress = p.Percentage;
                BackupPhaseDescription = GetPhaseDescription(p.Phase);
            });

            var success = await _saveManagementService.RestoreBackupAsync(SelectedBackup.Id, progress);

            if (success)
            {
                SuccessMessage = $"Backup restored successfully: {SelectedBackup.Name}";
                _logger.LogInformation("Backup restored: {BackupId}", SelectedBackup.Id);

                await Task.Delay(SuccessMessageDelayMs);
                SuccessMessage = null;
            }
            else
            {
                ErrorMessage = "Failed to restore backup";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error restoring backup: {BackupId}", SelectedBackup.Id);
            ErrorMessage = $"Failed to restore backup: {ex.Message}";
        }
        finally
        {
            IsBackupInProgress = false;
            BackupProgress = 0;
            BackupPhaseDescription = null;
        }
    }

    /// <summary>
    /// Deletes the selected backup.
    /// </summary>
    [RelayCommand]
    private async Task DeleteBackupAsync()
    {
        if (SelectedBackup == null)
        {
            ErrorMessage = "Please select a backup to delete";
            return;
        }

        ErrorMessage = null;
        SuccessMessage = null;

        try
        {
            var success = await _saveManagementService.DeleteBackupAsync(SelectedBackup.Id);

            if (success)
            {
                // Save backup ID before clearing selection
                var deletedBackupId = SelectedBackup.Id;

                SelectedGameBackups.Remove(SelectedBackup);

                // Update game info
                if (SelectedGame != null)
                {
                    SelectedGame.BackupCount = SelectedGameBackups.Count;
                    SelectedGame.HasSaves = SelectedGameBackups.Count > 0;
                    SelectedGame.LastBackupTime = SelectedGameBackups.MaxBy(b => b.CreatedTime)?.CreatedTime;
                    OnPropertyChanged(nameof(SelectedGameBackupCount));
                }

                SelectedBackup = null;
                SuccessMessage = "Backup deleted successfully";
                _logger.LogInformation("Backup deleted: {BackupId}", deletedBackupId);

                await Task.Delay(SuccessMessageDelayMs);
                SuccessMessage = null;
            }
            else
            {
                ErrorMessage = "Failed to delete backup";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting backup: {BackupId}", SelectedBackup?.Id);
            ErrorMessage = $"Failed to delete backup: {ex.Message}";
        }
    }

    /// <summary>
    /// Quick switches to the selected backup.
    /// </summary>
    [RelayCommand]
    private async Task QuickSwitchBackupAsync(GameSaveBackup? backup)
    {
        if (backup == null)
        {
            ErrorMessage = "Please select a backup to quick switch";
            return;
        }

        if (SelectedGame == null)
        {
            ErrorMessage = "No game selected";
            return;
        }

        IsBackupInProgress = true;
        BackupProgress = 0;
        BackupPhaseDescription = "Quick switching...";
        ErrorMessage = null;
        SuccessMessage = null;

        try
        {
            var result = await _saveManagementService.QuickSwitchSaveAsync(SelectedGame.Id, backup.Id);

            if (result.Success)
            {
                // Add the current backup that was created during quick switch
                if (result.CurrentBackup != null)
                {
                    SelectedGameBackups.Insert(0, result.CurrentBackup);
                    SelectedGame.BackupCount = SelectedGameBackups.Count;
                    OnPropertyChanged(nameof(SelectedGameBackupCount));
                }

                SuccessMessage = $"Quick switched to: {result.RestoredBackup?.Name}";
                _logger.LogInformation("Quick switch completed: from backup {CurrentId} to {TargetId}",
                    result.CurrentBackup?.Id, backup.Id);

                await Task.Delay(SuccessMessageDelayMs);
                SuccessMessage = null;
            }
            else
            {
                ErrorMessage = result.ErrorMessage ?? "Quick switch failed";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error quick switching for game: {GameId}", SelectedGame.Id);
            ErrorMessage = $"Failed to quick switch: {ex.Message}";
        }
        finally
        {
            IsBackupInProgress = false;
            BackupProgress = 0;
            BackupPhaseDescription = null;
        }
    }

    /// <summary>
    /// Refreshes the data.
    /// </summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        await LoadDataAsync();
    }

    /// <summary>
    /// Clears the selection.
    /// </summary>
    [RelayCommand]
    private void ClearSelection()
    {
        SelectedGame = null;
        SelectedBackup = null;
    }

    /// <summary>
    /// Gets a human-readable description for a backup phase.
    /// </summary>
    private static string GetPhaseDescription(BackupPhase phase)
    {
        return phase switch
        {
            BackupPhase.Scanning => "Scanning for save files...",
            BackupPhase.CreatingDirectory => "Creating backup directory...",
            BackupPhase.CopyingFiles => "Copying files...",
            BackupPhase.Finalizing => "Finalizing backup...",
            BackupPhase.Verifying => "Verifying backup integrity...",
            BackupPhase.CleaningUp => "Cleaning up old backups...",
            _ => "Processing..."
        };
    }
}

/// <summary>
/// Information about a game's save status for display in the save manager.
/// </summary>
public class GameSaveInfo : ObservableObject
{
    /// <summary>
    /// Game ID.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Display name (Chinese or original).
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Chinese name.
    /// </summary>
    public string? NameCn { get; set; }

    /// <summary>
    /// Original name.
    /// </summary>
    public string? NameOriginal { get; set; }

    /// <summary>
    /// Cover image path.
    /// </summary>
    public string? CoverImagePath { get; set; }

    /// <summary>
    /// Installation path.
    /// </summary>
    public string? InstallPath { get; set; }

    /// <summary>
    /// Number of backups.
    /// </summary>
    public int BackupCount { get; set; }

    /// <summary>
    /// Whether the game has saves/backups.
    /// </summary>
    public bool HasSaves { get; set; }

    /// <summary>
    /// Last backup creation time.
    /// </summary>
    public DateTime? LastBackupTime { get; set; }

    /// <summary>
    /// Last session time.
    /// </summary>
    public DateTime? LastSessionTime { get; set; }

    /// <summary>
    /// Total play time in seconds.
    /// </summary>
    public long TotalPlayTimeSeconds { get; set; }

    /// <summary>
    /// Formatted last backup time.
    /// </summary>
    public string FormattedLastBackupTime
    {
        get
        {
            if (LastBackupTime == null)
                return "Never";
            var diff = DateTime.UtcNow - LastBackupTime.Value;
            if (diff.TotalDays < 1)
                return $"{(int)diff.TotalHours} hours ago";
            if (diff.TotalDays < 7)
                return $"{(int)diff.TotalDays} days ago";
            return LastBackupTime.Value.ToString("yyyy-MM-dd");
        }
    }

    /// <summary>
    /// Status indicator text.
    /// </summary>
    public string StatusText => HasSaves ? $"{BackupCount} backups" : "No saves detected";

    /// <summary>
    /// Status color (for indicator).
    /// </summary>
    public string StatusColor => HasSaves ? "Green" : "Gray";
}