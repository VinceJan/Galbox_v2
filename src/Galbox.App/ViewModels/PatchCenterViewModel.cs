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
/// Manages game patches and translations from moyu.moe API (stub).
/// </summary>
public partial class PatchCenterViewModel : ObservableObject
{
    private readonly GalboxDbContext _dbContext;
    private readonly INavigationService _navigationService;
    private readonly ILogger<PatchCenterViewModel> _logger;

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
    /// Whether a download operation is in progress.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadPatchCommand))]
    private bool _isDownloading;

    /// <summary>
    /// Whether an install operation is in progress.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallPatchCommand))]
    private bool _isInstalling;

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
        GalboxDbContext dbContext,
        INavigationService navigationService,
        ILogger<PatchCenterViewModel> logger)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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

            // Generate stub data for demonstration
            await GenerateStubPatchDataAsync();

            _logger.LogInformation("Loaded patch center: {GamesCount} games needing patches, {InstalledCount} installed patches",
                GamesNeedingPatchesCount, InstalledPatchesCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading patch center data");
            ErrorMessage = $"Failed to load patch data: {ex.Message}";
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

        // Get all games
        var games = await _dbContext.Games
            .OrderByDescending(g => g.AddedTime)
            .ToListAsync();

        // Filter games that might need patches
        // For now, we show all games as potential candidates
        foreach (var game in games)
        {
            // Check if game has any installed patches
            var hasInstalledPatches = await _dbContext.Patches
                .AnyAsync(p => p.GameInfoId == game.Id && p.Status == PatchStatus.Installed);

            // Add game to list if it doesn't have patches installed
            // In real implementation, this would check against API data
            GamesNeedingPatches.Add(game);
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

        var installed = await _dbContext.Patches
            .Where(p => p.Status == PatchStatus.Installed)
            .Include(p => p.GameInfo)
            .OrderByDescending(p => p.InstalledTime)
            .ToListAsync();

        foreach (var patch in installed)
        {
            InstalledPatches.Add(patch);
        }

        InstalledPatchesCount = InstalledPatches.Count;
    }

    /// <summary>
    /// Generates stub patch data for demonstration purposes.
    /// This simulates data from moyu.moe API until real API is implemented.
    /// </summary>
    private async Task GenerateStubPatchDataAsync()
    {
        // Stub data generation - simulates API response
        // In production, this would call moyu.moe API

        if (GamesNeedingPatches.Count > 0)
        {
            // Select first game to show demo patches
            SelectedGame = GamesNeedingPatches.FirstOrDefault();

            if (SelectedGame != null)
            {
                await LoadPatchesForGameAsync(SelectedGame.Id);
            }
        }
    }

    /// <summary>
    /// Loads available patches for a specific game.
    /// This is a stub method that returns simulated patch data.
    /// </summary>
    public async Task LoadPatchesForGameAsync(int gameId)
    {
        AvailablePatches.Clear();

        try
        {
            // First check if we have patches in the database for this game
            var existingPatches = await _dbContext.Patches
                .Where(p => p.GameInfoId == gameId)
                .ToListAsync();

            // If no existing patches, generate stub data
            if (existingPatches.Count == 0)
            {
                var game = await _dbContext.Games.FindAsync(gameId);
                if (game != null)
                {
                    // Generate stub patches based on game
                    var stubPatches = GenerateStubPatchesForGame(game);
                    foreach (var patch in stubPatches)
                    {
                        _dbContext.Patches.Add(patch);
                    }
                    await _dbContext.SaveChangesAsync();
                    existingPatches = stubPatches;
                }
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
            ErrorMessage = $"Failed to load patches: {ex.Message}";
        }
    }

    /// <summary>
    /// Generates stub patches for a game for demonstration.
    /// </summary>
    private List<PatchRecord> GenerateStubPatchesForGame(GameInfo game)
    {
        var patches = new List<PatchRecord>();

        // Always add a translation patch stub
        patches.Add(new PatchRecord
        {
            GameInfoId = game.Id,
            Name = $"{game.DisplayName} 汉化补丁",
            PatchType = "Translation",
            Version = "v1.0",
            SizeBytes = 50 * 1024 * 1024, // 50 MB
            Source = "moyu.moe",
            DownloadUrl = "https://moyu.moe/patches/example",
            Description = "简体中文汉化补丁，包含完整文本翻译。\n来源：moyu.moe",
            Status = PatchStatus.Available,
            AddedTime = DateTime.UtcNow,
            ExternalId = $"stub_trans_{game.Id}"
        });

        // Add a fix patch stub (50% chance)
        if (game.DisplayName.Length % 2 == 0)
        {
            patches.Add(new PatchRecord
            {
                GameInfoId = game.Id,
                Name = $"{game.DisplayName} 修复补丁",
                PatchType = "Fix",
                Version = "v2.1",
                SizeBytes = 15 * 1024 * 1024, // 15 MB
                Source = "moyu.moe",
                DownloadUrl = "https://moyu.moe/patches/example_fix",
                Description = "修复游戏启动问题和存档兼容性问题。",
                Status = PatchStatus.Available,
                AddedTime = DateTime.UtcNow,
                ExternalId = $"stub_fix_{game.Id}"
            });
        }

        // Add an adult patch stub (30% chance based on game name)
        if (game.DisplayName.Contains(" "))
        {
            patches.Add(new PatchRecord
            {
                GameInfoId = game.Id,
                Name = $"{game.DisplayName} 18+补丁",
                PatchType = "Adult",
                Version = "v1.5",
                SizeBytes = 200 * 1024 * 1024, // 200 MB
                Source = "moyu.moe",
                DownloadUrl = "https://moyu.moe/patches/example_adult",
                Description = "18+内容解锁补丁，恢复完整游戏内容。",
                Status = PatchStatus.Available,
                AddedTime = DateTime.UtcNow,
                ExternalId = $"stub_adult_{game.Id}"
            });
        }

        return patches;
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
    /// </summary>
    partial void OnSelectedGameChanged(GameInfo? value)
    {
        if (value != null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await LoadPatchesForGameAsync(value.Id).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error loading patches for game: {GameId}", value.Id);
                    ErrorMessage = $"Failed to load patches: {ex.Message}";
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
    /// Downloads a patch (stub implementation with simulated progress).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDownloadPatch))]
    private async Task DownloadPatchAsync(PatchRecord? patch)
    {
        if (patch == null)
        {
            _logger.LogWarning("No patch to download");
            return;
        }

        if (patch.Status == PatchStatus.Downloading || patch.Status == PatchStatus.Installed)
        {
            ErrorMessage = "Patch is already downloading or installed";
            return;
        }

        IsDownloading = true;
        ErrorMessage = null;

        try
        {
            // Update status to downloading
            patch.Status = PatchStatus.Downloading;
            patch.DownloadProgress = 0;
            await _dbContext.SaveChangesAsync();

            // Simulate download progress (stub implementation)
            for (int i = 0; i <= 100; i += 10)
            {
                patch.DownloadProgress = i;
                await Task.Delay(200); // Simulate download delay
            }

            // Update status to downloaded
            patch.Status = PatchStatus.Downloaded;
            patch.DownloadedTime = DateTime.UtcNow;
            patch.DownloadProgress = 100;
            patch.LocalPath = $"C:\\Galbox\\Patches\\{patch.Name}.zip"; // Stub path

            await _dbContext.SaveChangesAsync();

            // Update UI
            AvailablePatches.Clear();
            var patches = await _dbContext.Patches.Where(p => p.GameInfoId == patch.GameInfoId).ToListAsync();
            foreach (var p in patches)
            {
                AvailablePatches.Add(p);
            }
            ApplyPatchFilters();

            SuccessMessage = $"Downloaded '{patch.Name}' successfully";
            _logger.LogInformation("Downloaded patch {PatchName} for game {GameId}", patch.Name, patch.GameInfoId);

            // Clear success message after delay
            await Task.Delay(3000);
            SuccessMessage = null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error downloading patch {PatchName}", patch.Name);
            ErrorMessage = $"Failed to download patch: {ex.Message}";

            // Reset status
            patch.Status = PatchStatus.Available;
            patch.DownloadProgress = 0;
            await _dbContext.SaveChangesAsync();
        }
        finally
        {
            IsDownloading = false;
        }
    }

    /// <summary>
    /// Installs a downloaded patch (stub implementation).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanInstallPatch))]
    private async Task InstallPatchAsync(PatchRecord? patch)
    {
        if (patch == null)
        {
            _logger.LogWarning("No patch to install");
            return;
        }

        if (patch.Status != PatchStatus.Downloaded && patch.Status != PatchStatus.Available)
        {
            ErrorMessage = "Patch must be downloaded before installation";
            return;
        }

        IsInstalling = true;
        ErrorMessage = null;

        try
        {
            // Update status to installing
            patch.Status = PatchStatus.Installing;
            await _dbContext.SaveChangesAsync();

            // Simulate installation (stub implementation)
            await Task.Delay(1000);

            // Update status to installed
            patch.Status = PatchStatus.Installed;
            patch.InstalledTime = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync();

            // Update UI collections
            AvailablePatches.Clear();
            var patches = await _dbContext.Patches.Where(p => p.GameInfoId == patch.GameInfoId).ToListAsync();
            foreach (var p in patches)
            {
                AvailablePatches.Add(p);
            }
            ApplyPatchFilters();

            // Reload installed patches
            await LoadInstalledPatchesAsync();

            SuccessMessage = $"Installed '{patch.Name}' successfully";
            _logger.LogInformation("Installed patch {PatchName} for game {GameId}", patch.Name, patch.GameInfoId);

            // Clear success message after delay
            await Task.Delay(3000);
            SuccessMessage = null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error installing patch {PatchName}", patch.Name);
            ErrorMessage = $"Failed to install patch: {ex.Message}";

            // Reset status
            patch.Status = PatchStatus.Downloaded;
            await _dbContext.SaveChangesAsync();
        }
        finally
        {
            IsInstalling = false;
        }
    }

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

    /// <summary>
    /// Checks if a patch can be downloaded.
    /// </summary>
    public bool CanDownloadPatch(PatchRecord patch)
    {
        return patch.Status == PatchStatus.Available && !IsDownloading;
    }

    /// <summary>
    /// Checks if a patch can be installed.
    /// </summary>
    public bool CanInstallPatch(PatchRecord patch)
    {
        return (patch.Status == PatchStatus.Downloaded || patch.Status == PatchStatus.Available) && !IsInstalling;
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