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

    private readonly IDbContextFactory<GalboxDbContext> _dbContextFactory;
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
    public string SelectedGameDisplayName => SelectedGame?.DisplayName ?? "未选择游戏";

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
        IDbContextFactory<GalboxDbContext> dbContextFactory,
        ISaveManagementService saveManagementService,
        ILogger<SaveManagerViewModel> logger)
    {
        _dbContextFactory = dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
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

            // One context for the whole page load, and one grouped query for the backup counts
            // and last-backup times instead of two queries per game (the previous N+1 pattern).
            using var db = _dbContextFactory.CreateDbContext();

            var games = await db.Games
                .AsNoTracking()
                .OrderByDescending(g => g.LastSessionTime)
                .ToListAsync();

            var backupStats = await db.SaveBackups
                .AsNoTracking()
                .GroupBy(b => b.GameInfoId)
                .Select(g => new
                {
                    GameId = g.Key,
                    Count = g.Count(),
                    LastBackupTime = g.Max(b => b.CreatedTime)
                })
                .ToDictionaryAsync(x => x.GameId);

            // Convert to GameSaveInfo with backup counts
            foreach (var game in games)
            {
                backupStats.TryGetValue(game.Id, out var stats);
                var backupCount = stats?.Count ?? 0;

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
                    LastBackupTime = stats?.LastBackupTime,
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
            ErrorMessage = $"加载游戏失败：{ex.Message}";
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
        using var db = _dbContextFactory.CreateDbContext();

        var lastBackup = await db.SaveBackups
            .AsNoTracking()
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
    /// Uses Dispatcher to ensure UI updates happen on the UI thread.
    /// </summary>
    partial void OnSelectedGameChanged(GameSaveInfo? value)
    {
        if (value != null)
        {
            // Get the dispatcher from the App's main window
            var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            if (dispatcher == null)
            {
                // Fallback: just run directly if no dispatcher
                _ = LoadBackupsForGameSafeAsync(value.Id);
                return;
            }

            // Run the async operation, but ensure UI updates happen on UI thread
            _ = Task.Run(async () =>
            {
                try
                {
                    await LoadBackupsForGameSafeAsync(value.Id).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error loading backups for game: {GameId}", value.Id);

                    // Update error message on UI thread
                    dispatcher.TryEnqueue(() =>
                    {
                        ErrorMessage = $"加载备份失败：{ex.Message}";
                    });
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
    /// Loads backups for a specific game with thread-safe UI updates.
    /// </summary>
    public async Task LoadBackupsForGameSafeAsync(int gameId)
    {
        IsLoadingBackups = true;
        ErrorMessage = null;

        try
        {
            // Load data on background thread
            var backups = await _saveManagementService.GetSaveBackupsAsync(gameId).ConfigureAwait(false);

            // Get dispatcher for UI thread updates
            var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

            if (dispatcher != null)
            {
                // Update UI on dispatcher thread
                dispatcher.TryEnqueue(() =>
                {
                    SelectedGameBackups.Clear();
                    SelectedBackup = null;

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

                    IsLoadingBackups = false;
                });
            }
            else
            {
                // No dispatcher available, update directly (may cause issues but fallback)
                SelectedGameBackups.Clear();
                SelectedBackup = null;

                foreach (var backup in backups)
                {
                    SelectedGameBackups.Add(backup);
                }

                if (SelectedGame != null && SelectedGame.Id == gameId)
                {
                    SelectedGame.BackupCount = backups.Count;
                    SelectedGame.HasSaves = backups.Count > 0;
                    SelectedGame.LastBackupTime = backups.MaxBy(b => b.CreatedTime)?.CreatedTime;
                    OnPropertyChanged(nameof(SelectedGameBackupCount));
                }

                IsLoadingBackups = false;
            }

            _logger.LogInformation("Loaded {Count} backups for game: {GameId}", backups.Count, gameId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading backups for game: {GameId}", gameId);
            ErrorMessage = $"加载备份失败：{ex.Message}";
            IsLoadingBackups = false;
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
            ErrorMessage = $"加载备份失败：{ex.Message}";
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
            ErrorMessage = "请先选择一个游戏";
            return;
        }

        IsBackupInProgress = true;
        BackupProgress = 0;
        BackupPhaseDescription = "正在开始备份...";
        ErrorMessage = null;
        SuccessMessage = null;

        try
        {
            // Get full game info from database
            GameInfo? game;
            using (var db = _dbContextFactory.CreateDbContext())
            {
                game = await db.Games
                    .AsNoTracking()
                    .FirstOrDefaultAsync(g => g.Id == SelectedGame.Id);
            }

            if (game == null)
            {
                ErrorMessage = "数据库中未找到该游戏";
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
                SuccessMessage = $"备份创建成功：{backup.Name}";
                _logger.LogInformation("Backup created: {BackupName} for game {GameId}", backup.Name, game.Id);

                // Clear success message after delay
                await Task.Delay(SuccessMessageDelayMs);
                SuccessMessage = null;
            }
            else
            {
                ErrorMessage = "创建备份失败。未检测到存档文件。";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating backup for game: {GameId}", SelectedGame.Id);
            ErrorMessage = $"创建备份失败：{ex.Message}";
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
            ErrorMessage = "请选择要恢复的备份";
            return;
        }

        IsBackupInProgress = true;
        BackupProgress = 0;
        BackupPhaseDescription = "正在开始恢复...";
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
                SuccessMessage = $"备份恢复成功：{SelectedBackup.Name}";
                _logger.LogInformation("Backup restored: {BackupId}", SelectedBackup.Id);

                await Task.Delay(SuccessMessageDelayMs);
                SuccessMessage = null;
            }
            else
            {
                ErrorMessage = "恢复备份失败";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error restoring backup: {BackupId}", SelectedBackup.Id);
            ErrorMessage = $"恢复备份失败：{ex.Message}";
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
            ErrorMessage = "请选择要删除的备份";
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
                SuccessMessage = "备份已成功删除";
                _logger.LogInformation("Backup deleted: {BackupId}", deletedBackupId);

                await Task.Delay(SuccessMessageDelayMs);
                SuccessMessage = null;
            }
            else
            {
                ErrorMessage = "删除备份失败";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting backup: {BackupId}", SelectedBackup?.Id);
            ErrorMessage = $"删除备份失败：{ex.Message}";
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
            ErrorMessage = "请选择要快速切换的备份";
            return;
        }

        if (SelectedGame == null)
        {
            ErrorMessage = "未选择游戏";
            return;
        }

        IsBackupInProgress = true;
        BackupProgress = 0;
        BackupPhaseDescription = "正在快速切换...";
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

                SuccessMessage = $"已快速切换到：{result.RestoredBackup?.Name}";
                _logger.LogInformation("Quick switch completed: from backup {CurrentId} to {TargetId}",
                    result.CurrentBackup?.Id, backup.Id);

                await Task.Delay(SuccessMessageDelayMs);
                SuccessMessage = null;
            }
            else
            {
                ErrorMessage = result.ErrorMessage ?? "快速切换失败";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error quick switching for game: {GameId}", SelectedGame.Id);
            ErrorMessage = $"快速切换失败：{ex.Message}";
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
            BackupPhase.Scanning => "正在扫描存档文件...",
            BackupPhase.CreatingDirectory => "正在创建备份目录...",
            BackupPhase.CopyingFiles => "正在复制文件...",
            BackupPhase.Finalizing => "正在完成备份...",
            BackupPhase.Verifying => "正在验证备份完整性...",
            BackupPhase.CleaningUp => "正在清理旧备份...",
            _ => "正在处理..."
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
                return "从未备份";
            var diff = DateTime.UtcNow - LastBackupTime.Value;
            if (diff.TotalDays < 1)
                return $"{(int)diff.TotalHours} 小时前";
            if (diff.TotalDays < 7)
                return $"{(int)diff.TotalDays} 天前";
            return LastBackupTime.Value.ToString("yyyy-MM-dd");
        }
    }

    /// <summary>
    /// Status indicator text.
    /// </summary>
    public string StatusText => HasSaves ? $"{BackupCount} 个备份" : "未检测到存档";

    /// <summary>
    /// Status color (for indicator).
    /// </summary>
    public string StatusColor => HasSaves ? "Green" : "Gray";
}