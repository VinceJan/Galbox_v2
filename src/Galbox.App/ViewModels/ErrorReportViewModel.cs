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
    private readonly GalboxDbContext _dbContext;
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
        GalboxDbContext dbContext,
        ILogger<ErrorReportViewModel> logger)
    {
        _errorCheckingService = errorCheckingService ?? throw new ArgumentNullException(nameof(errorCheckingService));
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        InitializeFilterOptions();
    }

    /// <summary>
    /// Initialize filter option collections.
    /// </summary>
    private void InitializeFilterOptions()
    {
        SeverityFilterOptions.Clear();
        SeverityFilterOptions.Add(new SeverityFilterOption { Value = ErrorSeverityFilter.All, DisplayName = "All Severities" });
        SeverityFilterOptions.Add(new SeverityFilterOption { Value = ErrorSeverityFilter.Critical, DisplayName = "Critical Only" });
        SeverityFilterOptions.Add(new SeverityFilterOption { Value = ErrorSeverityFilter.Major, DisplayName = "Major and Critical" });
        SeverityFilterOptions.Add(new SeverityFilterOption { Value = ErrorSeverityFilter.Minor, DisplayName = "All Including Minor" });

        CategoryFilterOptions.Clear();
        CategoryFilterOptions.Add(new CategoryFilterOption { Value = ErrorCategoryFilter.All, DisplayName = "All Categories" });
        CategoryFilterOptions.Add(new CategoryFilterOption { Value = ErrorCategoryFilter.ChineseDirectory, DisplayName = "Chinese Directory" });
        CategoryFilterOptions.Add(new CategoryFilterOption { Value = ErrorCategoryFilter.LocaleRequirement, DisplayName = "Locale Requirement" });
        CategoryFilterOptions.Add(new CategoryFilterOption { Value = ErrorCategoryFilter.DirectXMissing, DisplayName = "DirectX Missing" });
        CategoryFilterOptions.Add(new CategoryFilterOption { Value = ErrorCategoryFilter.KLiteCodecMissing, DisplayName = "Codec Missing" });
        CategoryFilterOptions.Add(new CategoryFilterOption { Value = ErrorCategoryFilter.WindowsCompatibility, DisplayName = "Windows Compatibility" });
        CategoryFilterOptions.Add(new CategoryFilterOption { Value = ErrorCategoryFilter.RuntimeMissing, DisplayName = "Runtime Missing" });
        CategoryFilterOptions.Add(new CategoryFilterOption { Value = ErrorCategoryFilter.PermissionIssue, DisplayName = "Permission Issue" });
        CategoryFilterOptions.Add(new CategoryFilterOption { Value = ErrorCategoryFilter.AntivirusBlocking, DisplayName = "Antivirus Warning" });
    }

    /// <summary>
    /// Load all unresolved errors from database.
    /// </summary>
    public async Task LoadAllUnresolvedErrorsAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        StatusMessage = "Loading errors...";

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
            var games = await _dbContext.Games
                .Where(g => gameIds.Contains(g.Id))
                .ToListAsync()
                .ConfigureAwait(false);

            GamesWithErrors.Clear();
            foreach (var game in games)
            {
                GamesWithErrors.Add(game);
            }

            // Convert to GameErrorInfo for display
            UpdateFilteredErrors();

            StatusMessage = $"Loaded {TotalUnresolvedCount} unresolved errors";
            _logger.LogInformation("Loaded {Count} unresolved errors", TotalUnresolvedCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading unresolved errors");
            ErrorMessage = $"Failed to load errors: {ex.Message}";
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
            StatusMessage = "No game selected";
            return;
        }

        IsLoading = true;
        ErrorMessage = null;
        SelectedGame = game;
        StatusMessage = $"Checking errors for {game.DisplayName}...";

        try
        {
            var errors = await _errorCheckingService.CheckGameAsync(game).ConfigureAwait(false);

            GameErrors.Clear();
            foreach (var error in errors)
            {
                GameErrors.Add(error);
            }

            UpdateFilteredErrors();

            StatusMessage = $"Found {errors.Count} issues for {game.DisplayName}";
            _logger.LogInformation("Checked {GameName}: {Count} errors found", game.DisplayName, errors.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking game {GameId}", game.Id);
            ErrorMessage = $"Failed to check errors: {ex.Message}";
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
            StatusMessage = "No game selected";
            return;
        }

        IsLoading = true;
        ErrorMessage = null;
        SelectedGame = game;
        StatusMessage = $"Loading error history for {game.DisplayName}...";

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

            StatusMessage = $"Loaded {history.Count} error records for {game.DisplayName}";
            _logger.LogInformation("Loaded {Count} error records for game {GameId}", history.Count, game.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading history for game {GameId}", game.Id);
            ErrorMessage = $"Failed to load error history: {ex.Message}";
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
            StatusMessage = "No error selected";
            return;
        }

        if (!error.AutoFixAvailable)
        {
            StatusMessage = "Auto fix not available for this error. Follow manual instructions.";
            return;
        }

        IsFixing = true;
        ErrorMessage = null;
        StatusMessage = "Attempting automatic fix...";

        try
        {
            var result = await _errorCheckingService.AttemptAutoFixAsync(error).ConfigureAwait(false);

            if (result.Success)
            {
                StatusMessage = "Fix applied successfully!";
                if (result.UpdatedError != null)
                {
                    // Update the error in the list
                    var index = GameErrors.IndexOf(error);
                    if (index >= 0)
                    {
                        GameErrors[index] = result.UpdatedError;
                    }
                }
                _logger.LogInformation("Auto fix successful for error {ErrorId}", error.Id);
            }
            else
            {
                ErrorMessage = result.Message;
                _logger.LogWarning("Auto fix failed for error {ErrorId}: {Message}", error.Id, result.Message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error attempting fix for {ErrorId}", error.Id);
            ErrorMessage = $"Fix attempt failed: {ex.Message}";
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
            StatusMessage = "No error selected";
            return;
        }

        IsLoading = true;
        ErrorMessage = null;
        StatusMessage = "Marking as resolved...";

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

                StatusMessage = "Error marked as resolved";
                _logger.LogInformation("Marked error {ErrorId} as resolved", error.Id);

                // Refresh counts
                await LoadAllUnresolvedErrorsAsync().ConfigureAwait(false);
            }
            else
            {
                ErrorMessage = "Could not mark error as resolved";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error marking error {ErrorId} as resolved", error.Id);
            ErrorMessage = $"Failed to mark resolved: {ex.Message}";
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
            StatusMessage = "No download URL available";
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = error.DownloadUrl,
                UseShellExecute = true
            });
            StatusMessage = $"Opening download page for {error.ToolName}";
            _logger.LogInformation("Opened download URL for error {ErrorId}", error.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open download URL");
            ErrorMessage = $"Failed to open URL: {ex.Message}";
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
        StatusMessage = "Starting batch error check...";

        try
        {
            var allGames = await _dbContext.Games.ToListAsync().ConfigureAwait(false);
            var results = await _errorCheckingService.BatchCheckGamesAsync(allGames).ConfigureAwait(false);

            var totalErrors = results.Sum(r => r.Value.Count);

            StatusMessage = $"Batch check complete. Found {totalErrors} issues across {results.Count} games.";
            _logger.LogInformation("Batch check complete: {TotalErrors} issues in {GameCount} games", totalErrors, results.Count);

            // Refresh the error list
            await LoadAllUnresolvedErrorsAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during batch check");
            ErrorMessage = $"Batch check failed: {ex.Message}";
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
        return new GameErrorInfo
        {
            Id = record.Id,
            GameId = record.GameInfoId,
            Category = Enum.Parse<ErrorCategory>(record.Category),
            Severity = Enum.Parse<ErrorSeverity>(record.Severity),
            Title = record.Title,
            Description = record.Description,
            SolutionType = Enum.Parse<SolutionType>(record.SolutionType),
            SolutionInstructions = record.SolutionInstructions ?? string.Empty,
            DownloadUrl = record.DownloadUrl,
            ToolName = record.ToolName,
            DetectedTime = record.DetectedTime,
            IsResolved = record.IsResolved,
            ResolvedTime = record.ResolvedTime,
            AutoFixAvailable = false // From database records, auto fix is typically not available
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