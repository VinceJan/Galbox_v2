using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Galbox.App.Models;
using Galbox.App.Services;
using Galbox.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Galbox.App.ViewModels;

/// <summary>
/// ViewModel for error reporting and management.
/// Handles display and interaction with detected game errors.
/// </summary>
public partial class ErrorReportViewModel : ObservableObject
{
    private readonly IErrorCheckingService _errorCheckingService;

    /// <summary>
    /// The repair implementation, used for the "撤销" command. The repair itself is reached through
    /// <see cref="IErrorCheckingService.AttemptAutoFixAsync"/> (the same call this page makes for
    /// "一键修复"), so that a fix and its undo can never diverge.
    /// </summary>
    private readonly IGameHealthFixService _fixService;

    /// <summary>
    /// Factory for short-lived contexts.
    ///
    /// The ViewModels are resolved from the root container, so they must not take a scoped
    /// <c>GalboxDbContext</c>: that registration produced one context shared by the whole
    /// application, which is the documented cause of the random
    /// "A second operation was started on this context instance" error banner.
    /// </summary>
    private readonly IDbContextFactory<GalboxDbContext> _dbContextFactory;
    private readonly ILogger<ErrorReportViewModel> _logger;

    /// <summary>
    /// Whether errors are being loaded.
    /// </summary>
    [ObservableProperty]
    private bool _isLoading;

    /// <summary>
    /// Whether a fix is being attempted.
    /// </summary>
    [ObservableProperty]
    private bool _isFixing;

    /// <summary>
    /// Status message for display.
    /// </summary>
    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>
    /// Error message if operation fails.
    /// </summary>
    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>
    /// Selected game for error viewing.
    /// </summary>
    [ObservableProperty]
    private GameInfo? _selectedGame;

    /// <summary>
    /// Selected error for detail viewing.
    /// </summary>
    [ObservableProperty]
    private GameErrorInfo? _selectedError;

    /// <summary>
    /// Filter for error severity.
    /// </summary>
    [ObservableProperty]
    private ErrorSeverityFilter _severityFilter = ErrorSeverityFilter.All;

    /// <summary>
    /// Filter for error category.
    /// </summary>
    [ObservableProperty]
    private ErrorCategoryFilter _categoryFilter = ErrorCategoryFilter.All;

    /// <summary>
    /// Filter for resolved status.
    /// </summary>
    [ObservableProperty]
    private ResolvedFilter _resolvedFilter = ResolvedFilter.UnresolvedOnly;

    /// <summary>
    /// Total count of unresolved critical errors.
    /// </summary>
    [ObservableProperty]
    private int _criticalErrorCount;

    /// <summary>
    /// Total count of unresolved major errors.
    /// </summary>
    [ObservableProperty]
    private int _majorErrorCount;

    /// <summary>
    /// Total count of all unresolved errors.
    /// </summary>
    [ObservableProperty]
    private int _totalUnresolvedCount;

    /// <summary>
    /// The repair that was applied last and can still be undone.
    /// </summary>
    /// <remarks>
    /// Kept on the ViewModel so the "撤销" button can be shown directly next to the message that
    /// reports the repair. <see cref="IGameHealthFixService"/> keeps the durable record, so the undo
    /// still works after a restart.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUndo))]
    private GameHealthFixResult? _lastFix;

    /// <summary>
    /// Step-by-step log of the last repair, shown under the status message.
    /// </summary>
    [ObservableProperty]
    private string? _fixDetailText;

    /// <summary>
    /// Whether "撤销" is currently offered.
    /// </summary>
    public bool CanUndo => LastFix is { Success: true, CanUndo: true };

    /// <summary>
    /// List of all unresolved errors.
    /// </summary>
    public ObservableCollection<GameErrorRecord> AllUnresolvedErrors { get; } = new();

    /// <summary>
    /// Errors for selected game.
    /// </summary>
    public ObservableCollection<GameErrorInfo> GameErrors { get; } = new();

    /// <summary>
    /// Filtered errors for display.
    /// </summary>
    public ObservableCollection<GameErrorInfo> FilteredErrors { get; } = new();

    /// <summary>
    /// Games with errors for selection.
    /// </summary>
    public ObservableCollection<GameInfo> GamesWithErrors { get; } = new();

    /// <summary>
    /// Available severity filter options.
    /// </summary>
    public ObservableCollection<SeverityFilterOption> SeverityFilterOptions { get; } = new();

    /// <summary>
    /// Available category filter options.
    /// </summary>
    public ObservableCollection<CategoryFilterOption> CategoryFilterOptions { get; } = new();

    /// <summary>
    /// Creates an ErrorReportViewModel with injected dependencies.
    /// </summary>
    public ErrorReportViewModel(
        IErrorCheckingService errorCheckingService,
        IGameHealthFixService fixService,
        IDbContextFactory<GalboxDbContext> dbContextFactory,
        ILogger<ErrorReportViewModel> logger)
    {
        _errorCheckingService = errorCheckingService ?? throw new ArgumentNullException(nameof(errorCheckingService));
        _fixService = fixService ?? throw new ArgumentNullException(nameof(fixService));
        _dbContextFactory = dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        InitializeFilterOptions();
    }

    /// <summary>
    /// Initialize filter option collections.
    /// </summary>
    private void InitializeFilterOptions()
    {
        SeverityFilterOptions.Clear();
        SeverityFilterOptions.Add(new SeverityFilterOption { Value = ErrorSeverityFilter.All, DisplayName = "全部严重程度" });
        SeverityFilterOptions.Add(new SeverityFilterOption { Value = ErrorSeverityFilter.Critical, DisplayName = "只看严重" });
        SeverityFilterOptions.Add(new SeverityFilterOption { Value = ErrorSeverityFilter.Major, DisplayName = "严重 + 重要" });
        SeverityFilterOptions.Add(new SeverityFilterOption { Value = ErrorSeverityFilter.Minor, DisplayName = "含轻微问题" });

        CategoryFilterOptions.Clear();
        CategoryFilterOptions.Add(new CategoryFilterOption { Value = ErrorCategoryFilter.All, DisplayName = "全部类别" });
        CategoryFilterOptions.Add(new CategoryFilterOption { Value = ErrorCategoryFilter.ChineseDirectory, DisplayName = "路径含中文" });
        CategoryFilterOptions.Add(new CategoryFilterOption { Value = ErrorCategoryFilter.LocaleRequirement, DisplayName = "区域设置要求" });
        CategoryFilterOptions.Add(new CategoryFilterOption { Value = ErrorCategoryFilter.DirectXMissing, DisplayName = "缺 DirectX" });
        CategoryFilterOptions.Add(new CategoryFilterOption { Value = ErrorCategoryFilter.KLiteCodecMissing, DisplayName = "缺 K-Lite 解码器" });
        CategoryFilterOptions.Add(new CategoryFilterOption { Value = ErrorCategoryFilter.WindowsCompatibility, DisplayName = "Windows 兼容性" });
        CategoryFilterOptions.Add(new CategoryFilterOption { Value = ErrorCategoryFilter.RuntimeMissing, DisplayName = "缺运行时" });
        CategoryFilterOptions.Add(new CategoryFilterOption { Value = ErrorCategoryFilter.PermissionIssue, DisplayName = "权限问题" });
        CategoryFilterOptions.Add(new CategoryFilterOption { Value = ErrorCategoryFilter.AntivirusBlocking, DisplayName = "杀软拦截" });
    }

    /// <summary>
    /// Load all unresolved errors from database.
    /// </summary>
    public async Task LoadAllUnresolvedErrorsAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        StatusMessage = "正在读取诊断记录…";

        try
        {
            var errors = await _errorCheckingService.GetAllUnresolvedErrorsAsync().ConfigureAwait(false);

            AllUnresolvedErrors.Clear();
            foreach (var error in errors)
            {
                AllUnresolvedErrors.Add(error);
            }

            // Calculate statistics
            CriticalErrorCount = errors.Count(e => e.Severity == "Critical");
            MajorErrorCount = errors.Count(e => e.Severity == "Major");
            TotalUnresolvedCount = errors.Count;

            // Load games with errors
            var gameIds = errors.Select(e => e.GameInfoId).Distinct().ToList();
            List<GameInfo> games;
            using (var db = _dbContextFactory.CreateDbContext())
            {
                games = await db.Games
                    .Where(g => gameIds.Contains(g.Id))
                    .AsNoTracking()
                    .ToListAsync()
                    .ConfigureAwait(false);
            }

            GamesWithErrors.Clear();
            foreach (var game in games)
            {
                GamesWithErrors.Add(game);
            }

            // Convert to GameErrorInfo for display
            UpdateFilteredErrors();

            StatusMessage = $"已读取 {TotalUnresolvedCount} 条未解决的问题";
            _logger.LogInformation("Loaded {Count} unresolved errors", TotalUnresolvedCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading unresolved errors");
            ErrorMessage = $"读取诊断记录失败：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Check errors for a specific game.
    /// </summary>
    public async Task CheckGameErrorsAsync(GameInfo? game)
    {
        if (game == null)
        {
            StatusMessage = "请先在左侧选择一个游戏。";
            return;
        }

        IsLoading = true;
        ErrorMessage = null;
        SelectedGame = game;
        StatusMessage = $"正在检测「{game.DisplayName}」…";

        try
        {
            var errors = await _errorCheckingService.CheckGameAsync(game).ConfigureAwait(false);

            GameErrors.Clear();
            foreach (var error in errors)
            {
                GameErrors.Add(error);
            }

            UpdateFilteredErrors();

            StatusMessage = $"「{game.DisplayName}」检测到 {errors.Count} 个问题";
            _logger.LogInformation("Checked {GameName}: {Count} errors found", game.DisplayName, errors.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking game {GameId}", game.Id);
            ErrorMessage = $"检测失败：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Load existing error history for a game.
    /// </summary>
    public async Task LoadGameErrorHistoryAsync(GameInfo? game)
    {
        if (game == null)
        {
            StatusMessage = "请先在左侧选择一个游戏。";
            return;
        }

        IsLoading = true;
        ErrorMessage = null;
        SelectedGame = game;
        StatusMessage = $"正在读取「{game.DisplayName}」的历史记录…";

        try
        {
            var history = await _errorCheckingService.GetErrorHistoryAsync(game.Id).ConfigureAwait(false);

            GameErrors.Clear();
            foreach (var record in history)
            {
                var errorInfo = ConvertRecordToErrorInfo(record);
                GameErrors.Add(errorInfo);
            }

            UpdateFilteredErrors();

            StatusMessage = $"已读取「{game.DisplayName}」的 {history.Count} 条历史记录";
            _logger.LogInformation("Loaded {Count} error records for game {GameId}", history.Count, game.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading history for game {GameId}", game.Id);
            ErrorMessage = $"读取历史记录失败：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Attempt to fix the selected error.
    /// </summary>
    [RelayCommand]
    private async Task AttemptFixAsync(GameErrorInfo? error)
    {
        if (error == null)
        {
            StatusMessage = "请先选择一条要修复的问题";
            return;
        }

        if (!error.AutoFixAvailable)
        {
            StatusMessage = $"「{error.CategoryDisplay}」没有自动修复方案，请按“解决步骤”手动处理。";
            return;
        }

        IsFixing = true;
        ErrorMessage = null;
        StatusMessage = $"正在修复「{error.Title}」…";

        try
        {
            var result = await _errorCheckingService.AttemptAutoFixAsync(error).ConfigureAwait(true);

            if (result.Success)
            {
                LastFix = result.Fix;
                FixDetailText = result.Details;
                StatusMessage = result.Message;

                if (result.UpdatedError != null)
                {
                    // Update the error in the list
                    var index = GameErrors.IndexOf(error);
                    if (index >= 0)
                    {
                        GameErrors[index] = result.UpdatedError;
                    }
                }

                // The detection result of the game changed (a renamed folder is a different path),
                // so re-run it instead of showing a stale list.
                if (SelectedGame != null)
                {
                    await CheckGameErrorsAsync(SelectedGame).ConfigureAwait(true);
                }

                _logger.LogInformation("Auto fix successful for game {GameId} ({Category})", error.GameId, error.Category);
            }
            else
            {
                LastFix = null;
                FixDetailText = null;
                ErrorMessage = result.Message;
                _logger.LogWarning("Auto fix refused for game {GameId}: {Message}", error.GameId, result.Message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error attempting fix for game {GameId}", error.GameId);
            ErrorMessage = $"修复失败：{ex.Message}";
        }
        finally
        {
            IsFixing = false;
        }
    }

    /// <summary>
    /// Undoes the repair that was applied last.
    /// </summary>
    /// <remarks>
    /// A repair the user cannot take back is a trap: renaming a game folder and writing a
    /// compatibility mode both change the user's machine. The undo goes through
    /// <see cref="IGameHealthFixService"/> so that the reverse operation is the same code that the
    /// acceptance harness drives.
    /// </remarks>
    [RelayCommand]
    private async Task UndoFixAsync()
    {
        var fix = LastFix;
        if (fix is null || !fix.Success)
        {
            StatusMessage = "没有可以撤销的修复。";
            return;
        }

        IsFixing = true;
        ErrorMessage = null;

        try
        {
            var result = fix.Kind switch
            {
                GameHealthFixKind.RenameInstallPath =>
                    await _fixService.RevertChineseInstallPathAsync(fix.GameId).ConfigureAwait(true),

                GameHealthFixKind.WindowsCompatibility =>
                    await _fixService.RevertWindowsCompatibilityModeAsync(fix.GameId).ConfigureAwait(true),

                _ => GameHealthFixResult.Failure("这条修复没有实现撤销。")
            };

            if (result.Success)
            {
                StatusMessage = result.Message;
                FixDetailText = result.Notes.Count == 0 ? null : string.Join(Environment.NewLine, result.Notes);
                LastFix = null;

                if (SelectedGame != null)
                {
                    await CheckGameErrorsAsync(SelectedGame).ConfigureAwait(true);
                }
            }
            else
            {
                ErrorMessage = result.Message;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Undo of the last health repair failed");
            ErrorMessage = $"撤销失败：{ex.Message}";
        }
        finally
        {
            IsFixing = false;
        }
    }

    /// <summary>
    /// Mark an error as resolved.
    /// </summary>
    [RelayCommand]
    private async Task MarkResolvedAsync(GameErrorInfo? error)
    {
        if (error == null)
        {
            StatusMessage = "请先选择一条问题记录。";
            return;
        }

        IsLoading = true;
        ErrorMessage = null;
        StatusMessage = "正在标记为已解决…";

        try
        {
            var success = await _errorCheckingService.MarkErrorResolvedAsync(error.Id).ConfigureAwait(false);

            if (success)
            {
                error.IsResolved = true;
                error.ResolvedTime = DateTime.UtcNow;

                // Remove from unresolved list
                GameErrors.Remove(error);
                UpdateFilteredErrors();

                StatusMessage = "已标记为已解决";
                _logger.LogInformation("Marked error {ErrorId} as resolved", error.Id);

                // Refresh counts
                await LoadAllUnresolvedErrorsAsync().ConfigureAwait(false);
            }
            else
            {
                ErrorMessage = "无法标记为已解决：记录可能已被删除";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error marking error {ErrorId} as resolved", error.Id);
            ErrorMessage = $"标记失败：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Mark a persisted error record as resolved.
    /// </summary>
    /// <remarks>
    /// This is the command the "all unresolved errors" list uses. It takes the
    /// <see cref="GameErrorRecord"/> itself because those rows carry a real database id, which is
    /// what <see cref="IErrorCheckingService.MarkErrorResolvedAsync"/> needs (freshly detected
    /// <see cref="GameErrorInfo"/> objects still have Id == 0 until they are reloaded).
    /// </remarks>
    [RelayCommand]
    private async Task MarkRecordResolvedAsync(GameErrorRecord? record)
    {
        if (record == null)
        {
            StatusMessage = "请先选择一条问题记录。";
            return;
        }

        IsLoading = true;
        ErrorMessage = null;

        try
        {
            var success = await _errorCheckingService.MarkErrorResolvedAsync(record.Id).ConfigureAwait(true);

            if (success)
            {
                AllUnresolvedErrors.Remove(record);
                StatusMessage = $"已将「{record.Title}」标记为已解决";
                _logger.LogInformation("Marked error record {RecordId} as resolved", record.Id);

                // Refresh the counters and the per-game list.
                await LoadAllUnresolvedErrorsAsync().ConfigureAwait(true);
            }
            else
            {
                ErrorMessage = "无法标记为已解决：记录可能已被删除";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error marking record {RecordId} as resolved", record.Id);
            ErrorMessage = $"标记失败：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Open download URL for external tool.
    /// </summary>
    [RelayCommand]
    private void OpenDownloadUrl(GameErrorInfo? error)
    {
        if (error == null || string.IsNullOrWhiteSpace(error.DownloadUrl))
        {
            StatusMessage = "这条问题没有可用的下载地址。";
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = error.DownloadUrl,
                UseShellExecute = true
            });
            StatusMessage = $"正在打开 {error.ToolName} 的下载页面…";
            _logger.LogInformation("Opened download URL for error {ErrorId}", error.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open download URL");
            ErrorMessage = $"打开链接失败：{ex.Message}";
        }
    }

    /// <summary>
    /// Batch check all games for errors.
    /// </summary>
    [RelayCommand]
    private async Task BatchCheckAllGamesAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        StatusMessage = "开始检测全部游戏…";

        try
        {
            List<GameInfo> allGames;
            using (var db = _dbContextFactory.CreateDbContext())
            {
                allGames = await db.Games.AsNoTracking().ToListAsync().ConfigureAwait(false);
            }

            var results = await _errorCheckingService.BatchCheckGamesAsync(allGames).ConfigureAwait(false);

            var totalErrors = results.Sum(r => r.Value.Count);

            StatusMessage = $"全库检测完成：{results.Count} 个游戏共发现 {totalErrors} 个问题。";
            _logger.LogInformation("Batch check complete: {TotalErrors} issues in {GameCount} games", totalErrors, results.Count);

            // Refresh the error list
            await LoadAllUnresolvedErrorsAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during batch check");
            ErrorMessage = $"全库检测失败：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Apply current filters to error list.
    /// </summary>
    private void UpdateFilteredErrors()
    {
        FilteredErrors.Clear();

        var filtered = GameErrors.Where(e => MatchesFilter(e)).ToList();

        foreach (var error in filtered)
        {
            FilteredErrors.Add(error);
        }

        OnPropertyChanged(nameof(FilteredErrors));
    }

    /// <summary>
    /// Check if error matches current filters.
    /// </summary>
    private bool MatchesFilter(GameErrorInfo error)
    {
        // Severity filter
        if (!MatchesSeverityFilter(error))
            return false;

        // Category filter
        if (!MatchesCategoryFilter(error))
            return false;

        // Resolved filter
        if (!MatchesResolvedFilter(error))
            return false;

        return true;
    }

    /// <summary>
    /// Check if error matches severity filter.
    /// </summary>
    private bool MatchesSeverityFilter(GameErrorInfo error)
    {
        return SeverityFilter switch
        {
            ErrorSeverityFilter.All => true,
            ErrorSeverityFilter.Critical => error.Severity == ErrorSeverity.Critical,
            ErrorSeverityFilter.Major => error.Severity == ErrorSeverity.Critical || error.Severity == ErrorSeverity.Major,
            ErrorSeverityFilter.Minor => true,
            _ => true
        };
    }

    /// <summary>
    /// Check if error matches category filter.
    /// </summary>
    private bool MatchesCategoryFilter(GameErrorInfo error)
    {
        return CategoryFilter switch
        {
            ErrorCategoryFilter.All => true,
            ErrorCategoryFilter.ChineseDirectory => error.Category == ErrorCategory.ChineseDirectory,
            ErrorCategoryFilter.LocaleRequirement => error.Category == ErrorCategory.LocaleRequirement,
            ErrorCategoryFilter.DirectXMissing => error.Category == ErrorCategory.DirectXMissing,
            ErrorCategoryFilter.KLiteCodecMissing => error.Category == ErrorCategory.KLiteCodecMissing,
            ErrorCategoryFilter.WindowsCompatibility => error.Category == ErrorCategory.WindowsCompatibility,
            ErrorCategoryFilter.RuntimeMissing => error.Category == ErrorCategory.RuntimeMissing,
            ErrorCategoryFilter.PermissionIssue => error.Category == ErrorCategory.PermissionIssue,
            ErrorCategoryFilter.AntivirusBlocking => error.Category == ErrorCategory.AntivirusBlocking,
            _ => true
        };
    }

    /// <summary>
    /// Check if error matches resolved filter.
    /// </summary>
    private bool MatchesResolvedFilter(GameErrorInfo error)
    {
        return ResolvedFilter switch
        {
            ResolvedFilter.All => true,
            ResolvedFilter.UnresolvedOnly => !error.IsResolved,
            ResolvedFilter.ResolvedOnly => error.IsResolved,
            _ => true
        };
    }

    /// <summary>
    /// Handle severity filter change.
    /// </summary>
    partial void OnSeverityFilterChanged(ErrorSeverityFilter value)
    {
        UpdateFilteredErrors();
    }

    /// <summary>
    /// Handle category filter change.
    /// </summary>
    partial void OnCategoryFilterChanged(ErrorCategoryFilter value)
    {
        UpdateFilteredErrors();
    }

    /// <summary>
    /// Handle resolved filter change.
    /// </summary>
    partial void OnResolvedFilterChanged(ResolvedFilter value)
    {
        UpdateFilteredErrors();
    }

    /// <summary>
    /// Convert database record to GameErrorInfo.
    /// </summary>
    private static GameErrorInfo ConvertRecordToErrorInfo(GameErrorRecord record)
    {
        var solutionType = Enum.TryParse<SolutionType>(record.SolutionType, ignoreCase: true, out var parsedSolution)
            ? parsedSolution
            : SolutionType.ManualFix;

        return new GameErrorInfo
        {
            Id = record.Id,
            GameId = record.GameInfoId,
            Category = Enum.Parse<ErrorCategory>(record.Category),
            Severity = Enum.Parse<ErrorSeverity>(record.Severity),
            Title = record.Title,
            Description = record.Description,
            SolutionType = solutionType,
            SolutionInstructions = record.SolutionInstructions ?? string.Empty,
            DownloadUrl = record.DownloadUrl,
            ToolName = record.ToolName,
            DetectedTime = record.DetectedTime,
            IsResolved = record.IsResolved,
            ResolvedTime = record.ResolvedTime,
            // Derived from the stored solution level instead of hard-coded to false: a stored record
            // that says AutoFix describes a repair the service really offers, so hiding the button for
            // it would make a working repair unreachable after a restart.
            AutoFixAvailable = solutionType == SolutionType.AutoFix
        };
    }
}

/// <summary>
/// Severity filter options.
/// </summary>
public enum ErrorSeverityFilter
{
    All,
    Critical,
    Major,
    Minor
}

/// <summary>
/// Category filter options.
/// </summary>
public enum ErrorCategoryFilter
{
    All,
    ChineseDirectory,
    LocaleRequirement,
    DirectXMissing,
    KLiteCodecMissing,
    WindowsCompatibility,
    RuntimeMissing,
    PermissionIssue,
    AntivirusBlocking
}

/// <summary>
/// Resolved status filter options.
/// </summary>
public enum ResolvedFilter
{
    All,
    UnresolvedOnly,
    ResolvedOnly
}

/// <summary>
/// Severity filter option for display.
/// </summary>
public class SeverityFilterOption
{
    public ErrorSeverityFilter Value { get; set; }
    public string DisplayName { get; set; } = string.Empty;
}

/// <summary>
/// Category filter option for display.
/// </summary>
public class CategoryFilterOption
{
    public ErrorCategoryFilter Value { get; set; }
    public string DisplayName { get; set; } = string.Empty;
}