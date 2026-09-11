using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Galbox.App.Services;
using Galbox.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Galbox.App.ViewModels;

/// <summary>
/// ViewModel for the Game Detail Page.
/// Uses CommunityToolkit.Mvvm for MVVM implementation.
/// </summary>
public partial class GameDetailViewModel : ObservableObject
{
    private readonly IDbContextFactory<GalboxDbContext> _dbContextFactory;
    private readonly ISaveManagementService _saveManagementService;
    private readonly ILogger<GameDetailViewModel> _logger;
    private Process? _runningProcess;

    /// <summary>
    /// The game being displayed.
    /// </summary>
    [ObservableProperty]
    private GameInfo? _game;

    /// <summary>
    /// Unique identifier for the game.
    /// </summary>
    [ObservableProperty]
    private int _gameId;

    /// <summary>
    /// Whether the page is loading data.
    /// </summary>
    [ObservableProperty]
    private bool _isLoading;

    /// <summary>
    /// Whether a game launch operation is in progress.
    /// </summary>
    [ObservableProperty]
    private bool _isLaunching;

    /// <summary>
    /// Error message to display if loading fails.
    /// </summary>
    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>
    /// Display name of the game (Chinese or original).
    /// </summary>
    [ObservableProperty]
    private string _displayName = string.Empty;

    /// <summary>
    /// Game description.
    /// </summary>
    [ObservableProperty]
    private string? _description;

    /// <summary>
    /// Path to the cover image.
    /// </summary>
    [ObservableProperty]
    private string? _coverImagePath;

    /// <summary>
    /// Path to the background image.
    /// </summary>
    [ObservableProperty]
    private string? _backgroundImagePath;

    /// <summary>
    /// Whether the game's installation folder or executable is gone (W14).
    /// </summary>
    [ObservableProperty]
    private bool _isInstallationMissing;

    /// <summary>
    /// Short badge text shown when the installation is missing ("文件夹丢失" / "可执行文件丢失").
    /// </summary>
    [ObservableProperty]
    private string? _installationStatusText;

    /// <summary>
    /// Explanation and repair hint shown when the installation is missing.
    /// </summary>
    [ObservableProperty]
    private string? _installationNotice;

    /// <summary>
    /// Developer name.
    /// </summary>
    [ObservableProperty]
    private string? _developer;

    /// <summary>
    /// Release date.
    /// </summary>
    [ObservableProperty]
    private DateTime? _releaseDate;

    /// <summary>
    /// Rating score.
    /// </summary>
    [ObservableProperty]
    private double? _rating;

    /// <summary>
    /// Total play time formatted.
    /// </summary>
    [ObservableProperty]
    private string _formattedPlayTime = "0m";

    /// <summary>
    /// Number of launches.
    /// </summary>
    [ObservableProperty]
    private int _launchCount;

    /// <summary>
    /// Last session time.
    /// </summary>
    [ObservableProperty]
    private DateTime? _lastSessionTime;

    /// <summary>
    /// Game size formatted.
    /// </summary>
    [ObservableProperty]
    private string _formattedSize = "0 B";

    /// <summary>
    /// Whether the game is a favorite.
    /// </summary>
    [ObservableProperty]
    private bool _isFavorite;

    /// <summary>
    /// Whether the game is currently running.
    /// </summary>
    [ObservableProperty]
    private bool _isRunning;

    /// <summary>
    /// Main executable path.
    /// </summary>
    [ObservableProperty]
    private string _mainExecutable = string.Empty;

    /// <summary>
    /// Available executables for multi-launch.
    /// </summary>
    public ObservableCollection<ExecutableItem> Executables { get; } = new();

    /// <summary>
    /// Selected executable for launch.
    /// </summary>
    [ObservableProperty]
    private ExecutableItem? _selectedExecutable;

    /// <summary>
    /// Tags associated with the game.
    /// </summary>
    public ObservableCollection<string> Tags { get; } = new();

    /// <summary>
    /// Characters associated with the game (max 3 visible).
    /// </summary>
    public ObservableCollection<GameCharacter> Characters { get; } = new();

    /// <summary>
    /// Whether there are more characters beyond the visible 3.
    /// </summary>
    [ObservableProperty]
    private bool _hasMoreCharacters;

    /// <summary>
    /// Documents in the game folder.
    /// </summary>
    public ObservableCollection<GameDocument> Documents { get; } = new();

    /// <summary>
    /// Media files (music/video) associated with the game.
    /// </summary>
    public ObservableCollection<GameMediaFile> MediaFiles { get; } = new();

    /// <summary>
    /// Screenshots for the game.
    /// </summary>
    public ObservableCollection<GameScreenshot> Screenshots { get; } = new();

    /// <summary>
    /// Save backups for the game.
    /// </summary>
    public ObservableCollection<GameSaveBackup> SaveBackups { get; } = new();

    /// <summary>
    /// Selected screenshot for preview.
    /// </summary>
    [ObservableProperty]
    private GameScreenshot? _selectedScreenshot;

    /// <summary>
    /// Selected media file for playback.
    /// </summary>
    [ObservableProperty]
    private GameMediaFile? _selectedMediaFile;

    /// <summary>
    /// Selected save backup.
    /// </summary>
    [ObservableProperty]
    private GameSaveBackup? _selectedSaveBackup;

    /// <summary>
    /// Whether a save backup/restore operation is running (guards re-entrancy on the buttons).
    /// </summary>
    [ObservableProperty]
    private bool _isBackupInProgress;

    /// <summary>
    /// Success/neutral status text shown to the user after a backup operation.
    /// </summary>
    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>
    /// Creates a GameDetailViewModel with injected dependencies.
    /// </summary>
    public GameDetailViewModel(
        IDbContextFactory<GalboxDbContext> dbContextFactory,
        ISaveManagementService saveManagementService,
        ILogger<GameDetailViewModel> logger)
    {
        _dbContextFactory = dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
        _saveManagementService = saveManagementService ?? throw new ArgumentNullException(nameof(saveManagementService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Loads game data asynchronously.
    /// Note: Does NOT use ConfigureAwait(false) to stay on UI thread for data binding.
    /// </summary>
    public async Task LoadGameDataAsync(int gameId)
    {
        if (gameId <= 0)
        {
            Debug.WriteLine($"[GameDetailViewModel] Invalid gameId: {gameId}");
            ErrorMessage = "无效的游戏 ID";
            return;
        }

        GameId = gameId;
        IsLoading = true;
        ErrorMessage = null;

        try
        {
            // Load game with all related data
            GameInfo? game;
            using (var db = _dbContextFactory.CreateDbContext())
            {
                game = await db.Games
                    .AsNoTracking()
                    .Include(g => g.Characters)
                    .Include(g => g.Documents)
                    .Include(g => g.MediaFiles)
                    .Include(g => g.Screenshots)
                    .Include(g => g.SaveBackups)
                    .FirstOrDefaultAsync(g => g.Id == gameId);
            }

            if (game == null)
            {
                Debug.WriteLine($"[GameDetailViewModel] Game not found with ID: {gameId}");
                ErrorMessage = "未找到游戏";
                IsLoading = false;
                return;
            }

            // Set the game entity
            Game = game;

            // Update all observable properties
            DisplayName = game.DisplayName;
            Description = game.Description;
            CoverImagePath = game.CoverImagePath;
            BackgroundImagePath = game.BackgroundImagePath;
            Developer = game.Developer;
            ReleaseDate = game.ReleaseDate;
            Rating = game.Rating;
            FormattedPlayTime = game.FormattedPlayTime;
            LaunchCount = game.LaunchCount;
            LastSessionTime = game.LastSessionTime;
            FormattedSize = game.FormattedSize;
            IsFavorite = game.IsFavorite;
            MainExecutable = game.MainExecutable;

            // W14: the library entry can outlive the folder it points at. Surface that here instead
            // of letting the launch button fail with a generic error.
            var installation = GameInstallationStatus.Evaluate(game);
            IsInstallationMissing = installation.IsMissing;
            InstallationStatusText = installation.StatusText;
            InstallationNotice = installation.IsMissing
                ? $"{installation.DetailText}\n{installation.RepairHint}"
                : null;

            // Load executables for multi-launch
            Executables.Clear();
            Executables.Add(new ExecutableItem
            {
                Name = "主程序",
                Path = game.MainExecutable,
                IsDefault = true
            });

            if (!string.IsNullOrWhiteSpace(game.AlternativeExecutables))
            {
                var altExes = game.AlternativeExecutables.Split('|', StringSplitOptions.RemoveEmptyEntries);
                foreach (var exe in altExes)
                {
                    Executables.Add(new ExecutableItem
                    {
                        Name = System.IO.Path.GetFileNameWithoutExtension(exe),
                        Path = exe,
                        IsDefault = false
                    });
                }
            }
            SelectedExecutable = Executables.FirstOrDefault();

            // Load tags
            Tags.Clear();
            if (!string.IsNullOrWhiteSpace(game.TagsJson))
            {
                try
                {
                    var jsonOptions = new System.Text.Json.JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    };
                    var tags = System.Text.Json.JsonSerializer.Deserialize<List<string>>(game.TagsJson, jsonOptions);
                    if (tags != null)
                    {
                        foreach (var tag in tags)
                        {
                            Tags.Add(tag);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[GameDetailViewModel] Failed to parse tags: {ex.Message}");
                }
            }

            // Load characters (show all, UI will limit display to 3)
            Characters.Clear();
            if (game.Characters != null)
            {
                foreach (var character in game.Characters)
                {
                    Characters.Add(character);
                }
                HasMoreCharacters = game.Characters.Count > 3;
            }

            // Load documents
            Documents.Clear();
            if (game.Documents != null)
            {
                foreach (var doc in game.Documents)
                {
                    Documents.Add(doc);
                }
            }

            // Load media files
            MediaFiles.Clear();
            if (game.MediaFiles != null)
            {
                foreach (var media in game.MediaFiles)
                {
                    MediaFiles.Add(media);
                }
            }

            // Load screenshots
            Screenshots.Clear();
            if (game.Screenshots != null)
            {
                foreach (var screenshot in game.Screenshots.OrderBy(s => s.CapturedTime))
                {
                    Screenshots.Add(screenshot);
                }
                SelectedScreenshot = Screenshots.FirstOrDefault();
            }

            // Load save backups
            SaveBackups.Clear();
            if (game.SaveBackups != null)
            {
                foreach (var backup in game.SaveBackups.OrderByDescending(b => b.CreatedTime))
                {
                    SaveBackups.Add(backup);
                }
            }

            _logger.LogInformation("Loaded game data for {GameName} (ID: {GameId})", DisplayName, gameId);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GameDetailViewModel] Error loading game: {ex}");
            _logger.LogError(ex, "Error loading game with ID {GameId}", gameId);
            ErrorMessage = $"加载游戏失败：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Launches the game with the selected executable.
    /// </summary>
    [RelayCommand]
    private async Task LaunchGameAsync()
    {
        if (IsLaunching || IsRunning)
        {
            Debug.WriteLine("[GameDetailViewModel] Game already launching or running");
            return;
        }

        var executablePath = SelectedExecutable?.Path ?? MainExecutable;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            Debug.WriteLine("[GameDetailViewModel] No executable path available");
            ErrorMessage = "无可用的可执行文件路径";
            return;
        }

        // W14: a missing folder (or a missing executable inside an existing folder) must produce a
        // message that says which path is gone and how to fix it - never a silent failure.
        if (Game != null)
        {
            var installation = GameInstallationStatus.Evaluate(Game);
            IsInstallationMissing = installation.IsMissing;
            InstallationStatusText = installation.StatusText;
            InstallationNotice = installation.IsMissing
                ? $"{installation.DetailText}\n{installation.RepairHint}"
                : null;

            if (installation.IsMissing)
            {
                ErrorMessage = GameInstallationStatus.BuildLaunchBlockMessage(Game);
                _logger.LogWarning(
                    "Refusing to launch game {GameId} ({GameName}): {Detail}",
                    Game.Id,
                    DisplayName,
                    installation.DetailText);
                return;
            }
        }

        if (!System.IO.File.Exists(executablePath))
        {
            Debug.WriteLine($"[GameDetailViewModel] Executable not found: {executablePath}");
            ErrorMessage = $"未找到可执行文件：{executablePath}\n请在库中确认游戏位置，或重新添加游戏文件夹。";
            return;
        }

        IsLaunching = true;
        ErrorMessage = null;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = System.IO.Path.GetDirectoryName(executablePath) ?? (Game?.InstallPath ?? string.Empty),
                UseShellExecute = true
            };

            // Validate WorkingDirectory exists
            if (!string.IsNullOrWhiteSpace(startInfo.WorkingDirectory) && !System.IO.Directory.Exists(startInfo.WorkingDirectory))
            {
                _logger.LogWarning("Working directory does not exist: {WorkingDirectory}", startInfo.WorkingDirectory);
                startInfo.WorkingDirectory = string.Empty;
            }

            _runningProcess = Process.Start(startInfo);
            if (_runningProcess != null)
            {
                IsRunning = true;
                _logger.LogInformation("Launched game {GameName} with executable {Executable}", DisplayName, executablePath);

                // Update launch count and start time
                if (Game != null)
                {
                    Game.LaunchCount++;
                    Game.LastSessionTime = DateTime.UtcNow;
                    LaunchCount = Game.LaunchCount;
                    LastSessionTime = Game.LastSessionTime;
                }

                // Wait for process to exit
                await _runningProcess.WaitForExitAsync();

                // Update play time when process exits
                if (Game != null)
                {
                    var exitTime = DateTime.UtcNow;
                    var sessionDuration = exitTime - (Game.LastSessionTime ?? exitTime);
                    Game.TotalPlayTimeSeconds += (long)sessionDuration.TotalSeconds;
                    FormattedPlayTime = Game.FormattedPlayTime;

                    using (var db = _dbContextFactory.CreateDbContext())
                    {
                        var tracked = await db.Games.FindAsync(Game.Id);
                        if (tracked != null)
                        {
                            tracked.LaunchCount = Game.LaunchCount;
                            tracked.LastSessionTime = Game.LastSessionTime;
                            tracked.TotalPlayTimeSeconds = Game.TotalPlayTimeSeconds;
                            await db.SaveChangesAsync();
                        }
                    }

                    _logger.LogInformation("Game {GameName} exited after {Duration} seconds", DisplayName, sessionDuration.TotalSeconds);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GameDetailViewModel] Error launching game: {ex}");
            _logger.LogError(ex, "Error launching game {GameName}", DisplayName);
            ErrorMessage = $"启动游戏失败：{ex.Message}";
        }
        finally
        {
            IsLaunching = false;
            IsRunning = false;

            // Safely dispose the process - check before accessing
            try
            {
                if (_runningProcess != null)
                {
                    // Check if process is still accessible (not disposed)
                    var hasExited = false;
                    try
                    {
                        hasExited = _runningProcess.HasExited;
                    }
                    catch (System.ComponentModel.Win32Exception)
                    {
                        // Process is no longer accessible, skip dispose
                        _logger.LogDebug("Process already disposed or inaccessible");
                    }
                    catch (InvalidOperationException)
                    {
                        // Process has been disposed
                        _logger.LogDebug("Process already disposed");
                    }

                    _runningProcess.Dispose();
                    _runningProcess = null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing process");
                _runningProcess = null;
            }
        }
    }

    /// <summary>
    /// Launches the game with an alternative executable.
    /// </summary>
    [RelayCommand]
    private async Task LaunchAlternativeAsync(ExecutableItem executable)
    {
        if (executable == null)
        {
            Debug.WriteLine("[GameDetailViewModel] No alternative executable selected");
            return;
        }

        SelectedExecutable = executable;
        await LaunchGameAsync();
    }

    /// <summary>
    /// Toggles the favorite status of the game.
    /// </summary>
    [RelayCommand]
    private async Task ToggleFavoriteAsync()
    {
        if (Game == null)
        {
            Debug.WriteLine("[GameDetailViewModel] No game to toggle favorite");
            return;
        }

        try
        {
            Game.IsFavorite = !Game.IsFavorite;
            IsFavorite = Game.IsFavorite;

            using (var db = _dbContextFactory.CreateDbContext())
            {
                var tracked = await db.Games.FindAsync(Game.Id);
                if (tracked != null)
                {
                    tracked.IsFavorite = Game.IsFavorite;
                    await db.SaveChangesAsync();
                }
            }

            _logger.LogInformation("Toggled favorite status for {GameName}: {IsFavorite}", DisplayName, IsFavorite);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GameDetailViewModel] Error toggling favorite: {ex}");
            _logger.LogError(ex, "Error toggling favorite for {GameName}", DisplayName);

            // Revert the change
            Game.IsFavorite = !Game.IsFavorite;
            IsFavorite = Game.IsFavorite;
        }
    }

    /// <summary>
    /// Opens the selected document.
    /// </summary>
    [RelayCommand]
    private void OpenDocument(GameDocument? document)
    {
        if (document == null || string.IsNullOrWhiteSpace(document.FilePath))
        {
            Debug.WriteLine("[GameDetailViewModel] No document to open");
            return;
        }

        try
        {
            if (System.IO.File.Exists(document.FilePath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = document.FilePath,
                    UseShellExecute = true
                });
                _logger.LogInformation("Opened document {FileName}", document.FileName);
            }
            else
            {
                Debug.WriteLine($"[GameDetailViewModel] Document file not found: {document.FilePath}");
                ErrorMessage = "未找到文档文件";
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GameDetailViewModel] Error opening document: {ex}");
            _logger.LogError(ex, "Error opening document {FileName}", document.FileName);
            ErrorMessage = $"打开文档失败：{ex.Message}";
        }
    }

    /// <summary>
    /// Opens the screenshot in full view.
    /// </summary>
    [RelayCommand]
    private void OpenScreenshot(GameScreenshot? screenshot)
    {
        if (screenshot == null || string.IsNullOrWhiteSpace(screenshot.FilePath))
        {
            Debug.WriteLine("[GameDetailViewModel] No screenshot to open");
            return;
        }

        try
        {
            if (System.IO.File.Exists(screenshot.FilePath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = screenshot.FilePath,
                    UseShellExecute = true
                });
                _logger.LogInformation("Opened screenshot {FileName}", screenshot.FileName);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GameDetailViewModel] Error opening screenshot: {ex}");
            _logger.LogError(ex, "Error opening screenshot {FileName}", screenshot.FileName);
        }
    }

    /// <summary>
    /// Plays the selected media file.
    /// </summary>
    [RelayCommand]
    private void PlayMedia(GameMediaFile? media)
    {
        if (media == null || string.IsNullOrWhiteSpace(media.FilePath))
        {
            Debug.WriteLine("[GameDetailViewModel] No media to play");
            return;
        }

        try
        {
            if (System.IO.File.Exists(media.FilePath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = media.FilePath,
                    UseShellExecute = true
                });
                _logger.LogInformation("Playing media {FileName}", media.FileName);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GameDetailViewModel] Error playing media: {ex}");
            _logger.LogError(ex, "Error playing media {FileName}", media.FileName);
        }
    }

    /// <summary>
    /// Creates a real save backup through <see cref="ISaveManagementService"/>.
    /// </summary>
    /// <remarks>
    /// This used to write a database row whose <c>BackupPath</c> pointed at a folder that was
    /// never created - the backup list looked populated while nothing existed on disk. The save
    /// management service writes a real ZIP archive of the detected save files, and reports
    /// honestly when it cannot find any save files to back up.
    /// </remarks>
    [RelayCommand]
    private async Task CreateSaveBackupAsync()
    {
        if (Game == null)
        {
            Debug.WriteLine("[GameDetailViewModel] No game to backup");
            return;
        }

        if (IsBackupInProgress)
        {
            return;
        }

        IsBackupInProgress = true;
        ErrorMessage = null;
        StatusMessage = null;

        try
        {
            var backup = await _saveManagementService.CreateBackupAsync(
                Game,
                $"手动备份 - {DateTime.Now:yyyy-MM-dd HH:mm}");

            if (backup == null)
            {
                ErrorMessage = "创建备份失败：未检测到存档文件。";
                _logger.LogWarning("No save files found to back up for {GameName}", DisplayName);
                return;
            }

            SaveBackups.Insert(0, backup);
            StatusMessage = $"已创建备份：{backup.Name}（{backup.FormattedSize}）";
            _logger.LogInformation(
                "Created save backup {BackupName} for {GameName} at {BackupPath}",
                backup.Name, DisplayName, backup.BackupPath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GameDetailViewModel] Error creating backup: {ex}");
            _logger.LogError(ex, "Error creating save backup for {GameName}", DisplayName);
            ErrorMessage = $"创建备份失败：{ex.Message}";
        }
        finally
        {
            IsBackupInProgress = false;
        }
    }

    /// <summary>
    /// Restores a save backup through <see cref="ISaveManagementService"/>, which really unpacks
    /// the ZIP archive over the game's save files.
    /// </summary>
    [RelayCommand]
    private async Task RestoreSaveBackupAsync(GameSaveBackup? backup)
    {
        if (backup == null)
        {
            Debug.WriteLine("[GameDetailViewModel] No backup to restore");
            return;
        }

        if (IsBackupInProgress)
        {
            return;
        }

        IsBackupInProgress = true;
        ErrorMessage = null;
        StatusMessage = null;

        try
        {
            _logger.LogInformation("Restoring save backup {BackupName} for {GameName}", backup.Name, DisplayName);

            var restored = await _saveManagementService.RestoreBackupAsync(backup.Id);

            if (restored)
            {
                StatusMessage = $"已恢复备份：{backup.Name}";
            }
            else
            {
                ErrorMessage = $"恢复备份失败：{backup.Name}（备份文件缺失、被占用或校验未通过，详见日志）";
                _logger.LogWarning("Restore reported failure for backup {BackupName}", backup.Name);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GameDetailViewModel] Error restoring backup: {ex}");
            _logger.LogError(ex, "Error restoring save backup {BackupName}", backup.Name);
            ErrorMessage = $"恢复备份失败：{ex.Message}";
        }
        finally
        {
            IsBackupInProgress = false;
        }
    }

    /// <summary>
    /// Deletes a save backup (archive on disk plus its database row).
    /// </summary>
    [RelayCommand]
    private async Task DeleteSaveBackupAsync(GameSaveBackup? backup)
    {
        if (backup == null)
        {
            return;
        }

        ErrorMessage = null;
        StatusMessage = null;

        try
        {
            var deleted = await _saveManagementService.DeleteBackupAsync(backup.Id);

            if (deleted)
            {
                SaveBackups.Remove(backup);
                StatusMessage = $"已删除备份：{backup.Name}";
            }
            else
            {
                ErrorMessage = $"删除备份失败：{backup.Name}";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting save backup {BackupName}", backup.Name);
            ErrorMessage = $"删除备份失败：{ex.Message}";
        }
    }

    /// <summary>
    /// Shows all characters (beyond the initial 3).
    /// </summary>
    [RelayCommand]
    private void ShowAllCharacters()
    {
        HasMoreCharacters = false;
        // UI will handle showing all characters when HasMoreCharacters is false
    }

    /// <summary>
    /// Navigates to a tag search page.
    /// </summary>
    [RelayCommand]
    private void NavigateToTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            Debug.WriteLine("[GameDetailViewModel] No tag to navigate to");
            return;
        }

        _logger.LogInformation("Navigating to tag search: {Tag}", tag);
        // Navigation would be handled by the page or a navigation service
    }
}

/// <summary>
/// Represents an executable file for multi-launch.
/// </summary>
public class ExecutableItem
{
    /// <summary>
    /// Display name for the executable.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Full path to the executable.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Whether this is the default/main executable.
    /// </summary>
    public bool IsDefault { get; set; }
}