using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Galbox.App.Services;
using Galbox.Data.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;

namespace Galbox.App.ViewModels;

/// <summary>
/// ViewModel for displaying scraping progress and batch results.
/// Supports progress dialog with cancel option and per-game status display.
/// </summary>
public partial class ScrapingProgressViewModel : ObservableObject, IDisposable
{
    private readonly IAutoScrapingService _autoScrapingService;
    private readonly IGameScrapingService _gameScrapingService;
    private readonly ILogger<ScrapingProgressViewModel> _logger;
    private readonly DispatcherQueue? _dispatcherQueue;

    private CancellationTokenSource? _currentCts;

    /// <summary>
    /// Flag to track whether events have been unsubscribed.
    /// </summary>
    private bool _eventsSubscribed = true;

    /// <summary>
    /// Flag to track whether the object has been disposed.
    /// </summary>
    private bool _disposed;

    /// <summary>
    /// Creates a ScrapingProgressViewModel with injected dependencies.
    /// </summary>
    public ScrapingProgressViewModel(
        IAutoScrapingService autoScrapingService,
        IGameScrapingService gameScrapingService,
        ILogger<ScrapingProgressViewModel> logger)
    {
        _autoScrapingService = autoScrapingService ?? throw new ArgumentNullException(nameof(autoScrapingService));
        _gameScrapingService = gameScrapingService ?? throw new ArgumentNullException(nameof(gameScrapingService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Get DispatcherQueue for cross-thread UI updates
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        // Subscribe to progress events
        SubscribeEvents();
    }

    /// <summary>
    /// Subscribes to scraping service events.
    /// </summary>
    private void SubscribeEvents()
    {
        if (_eventsSubscribed)
        {
            return;
        }

        _autoScrapingService.ProgressChanged += OnProgressChanged;
        _autoScrapingService.GameCompleted += OnGameCompleted;
        _autoScrapingService.BatchCompleted += OnBatchCompleted;
        _eventsSubscribed = true;
    }

    /// <summary>
    /// Unsubscribes from scraping service events.
    /// Call this when the ViewModel is no longer needed to prevent memory leaks.
    /// </summary>
    public void UnsubscribeEvents()
    {
        if (!_eventsSubscribed)
        {
            return;
        }

        _autoScrapingService.ProgressChanged -= OnProgressChanged;
        _autoScrapingService.GameCompleted -= OnGameCompleted;
        _autoScrapingService.BatchCompleted -= OnBatchCompleted;
        _eventsSubscribed = false;
    }

    #region Observable Properties

    /// <summary>
    /// Total number of games in the batch.
    /// </summary>
    [ObservableProperty]
    private int _totalGames;

    /// <summary>
    /// Number of games completed.
    /// </summary>
    [ObservableProperty]
    private int _completedGames;

    /// <summary>
    /// Number of successful scrapes.
    /// </summary>
    [ObservableProperty]
    private int _successCount;

    /// <summary>
    /// Number of failed scrapes.
    /// </summary>
    [ObservableProperty]
    private int _failedCount;

    /// <summary>
    /// Number of skipped games.
    /// </summary>
    [ObservableProperty]
    private int _skippedCount;

    /// <summary>
    /// Number of games needing manual review.
    /// </summary>
    [ObservableProperty]
    private int _needsReviewCount;

    /// <summary>
    /// Current game being processed.
    /// </summary>
    [ObservableProperty]
    private string? _currentGameName;

    /// <summary>
    /// Progress percentage (0-100).
    /// </summary>
    [ObservableProperty]
    private double _progressPercentage;

    /// <summary>
    /// Elapsed time formatted.
    /// </summary>
    [ObservableProperty]
    private string _elapsedTime = "00:00:00";

    /// <summary>
    /// Estimated remaining time formatted.
    /// </summary>
    [ObservableProperty]
    private string? _estimatedRemainingTime;

    /// <summary>
    /// Whether scraping is in progress.
    /// </summary>
    [ObservableProperty]
    private bool _isRunning;

    /// <summary>
    /// Whether the operation was cancelled.
    /// </summary>
    [ObservableProperty]
    private bool _isCancelled;

    /// <summary>
    /// Whether the operation is complete.
    /// </summary>
    [ObservableProperty]
    private bool _isComplete;

    /// <summary>
    /// Summary message after completion.
    /// </summary>
    [ObservableProperty]
    private string? _summaryMessage;

    /// <summary>
    /// Error message if any.
    /// </summary>
    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>
    /// Per-game results for display.
    /// </summary>
    public ObservableCollection<GameScrapingResultItem> GameResults { get; } = new();

    /// <summary>
    /// Games that need manual review.
    /// </summary>
    public ObservableCollection<GameScrapingResultItem> GamesNeedingReview { get; } = new();

    /// <summary>
    /// Selected game for review details.
    /// </summary>
    [ObservableProperty]
    private GameScrapingResultItem? _selectedGameForReview;

    /// <summary>
    /// Available metadata matches for selected game.
    /// </summary>
    public ObservableCollection<MetadataMatchItem> AvailableMatches { get; } = new();

    /// <summary>
    /// Selected metadata match to apply.
    /// </summary>
    [ObservableProperty]
    private MetadataMatchItem? _selectedMatch;

    /// <summary>
    /// Preview of changes for selected match.
    /// </summary>
    public ObservableCollection<MetadataFieldPreview> ChangePreview { get; } = new();

    /// <summary>
    /// Whether preview has changes.
    /// </summary>
    [ObservableProperty]
    private bool _hasPreviewChanges;

    /// <summary>
    /// Fields to preserve when applying metadata.
    /// </summary>
    public ObservableCollection<string> PreserveFields { get; } = new()
    {
        "Tags",
        "Description",
        "Rating"
    };

    /// <summary>
    /// Whether to preserve existing characters.
    /// </summary>
    [ObservableProperty]
    private bool _preserveCharacters;

    #endregion

    #region Commands

    /// <summary>
    /// Starts batch scraping for selected games.
    /// </summary>
    [RelayCommand]
    private async Task StartBatchScrapingAsync(IEnumerable<int>? gameIds)
    {
        if (gameIds == null || !gameIds.Any())
        {
            ErrorMessage = "未选择要刮削的游戏";
            return;
        }

        // Reset state
        ResetState();
        IsRunning = true;

        // Enqueue games
        _autoScrapingService.EnqueueGames(gameIds);

        _currentCts = new CancellationTokenSource();

        try
        {
            await _autoScrapingService.StartScrapingAsync(_currentCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            IsCancelled = true;
            _logger.LogInformation("Batch scraping was cancelled");
        }
        catch (Exception ex)
        {
            ErrorMessage = "刮削失败：{ex.Message}";
            _logger.LogError(ex, "Batch scraping failed");
        }
        finally
        {
            IsRunning = false;
            IsComplete = true;
            UpdateSummaryMessage();
        }
    }

    /// <summary>
    /// Cancels the current scraping operation.
    /// </summary>
    [RelayCommand]
    private void CancelScraping()
    {
        if (_currentCts != null && !_currentCts.Token.IsCancellationRequested)
        {
            _autoScrapingService.StopScraping();
            _currentCts.Cancel();
            _logger.LogInformation("User cancelled scraping operation");

            // Dispose the CancellationTokenSource
            _currentCts.Dispose();
            _currentCts = null;
        }
    }

    /// <summary>
    /// Loads games needing review.
    /// </summary>
    [RelayCommand]
    private async Task LoadGamesNeedingReviewAsync()
    {
        GamesNeedingReview.Clear();

        var results = await _autoScrapingService.GetGamesNeedingReviewAsync().ConfigureAwait(false);

        foreach (var result in results)
        {
            GamesNeedingReview.Add(new GameScrapingResultItem(result));
        }

        _logger.LogInformation("Loaded {Count} games needing review", GamesNeedingReview.Count);
    }

    /// <summary>
    /// Selects a game for review and loads available matches.
    /// </summary>
    [RelayCommand]
    private async Task SelectGameForReviewAsync(GameScrapingResultItem? game)
    {
        if (game == null)
        {
            return;
        }

        SelectedGameForReview = game;
        AvailableMatches.Clear();

        // Load all available matches
        foreach (var match in game.AllMatches)
        {
            AvailableMatches.Add(new MetadataMatchItem(match));
        }

        // Select the best match by default
        SelectedMatch = AvailableMatches.FirstOrDefault(m => m.MatchScore >= game.MatchScore);

        // Load preview if a match is selected
        if (SelectedMatch != null)
        {
            await PreviewSelectedMatchAsync();
        }
    }

    /// <summary>
    /// Shows preview of changes for selected match.
    /// </summary>
    [RelayCommand]
    private async Task PreviewSelectedMatchAsync()
    {
        if (SelectedGameForReview == null || SelectedMatch == null)
        {
            return;
        }

        ChangePreview.Clear();
        HasPreviewChanges = false;

        try
        {
            var preview = await _autoScrapingService.PreviewMergeAsync(
                SelectedGameForReview.GameId,
                SelectedMatch.Metadata).ConfigureAwait(false);

            foreach (var change in preview.Changes)
            {
                ChangePreview.Add(new MetadataFieldPreview(change));
            }

            HasPreviewChanges = preview.HasChanges;

            _logger.LogDebug("Loaded preview with {Changes} changes for game {GameId}",
                preview.ChangesCount, SelectedGameForReview.GameId);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to load preview: {ex.Message}";
            _logger.LogError(ex, "Preview load failed");
        }
    }

    /// <summary>
    /// Applies the selected metadata to the game.
    /// </summary>
    [RelayCommand]
    private async Task ApplySelectedMetadataAsync()
    {
        if (SelectedGameForReview == null || SelectedMatch == null)
        {
            ErrorMessage = "未选择游戏或元数据";
            return;
        }

        // Save game ID before clearing selection
        var gameId = SelectedGameForReview.GameId;

        try
        {
            // Get fields to preserve
            var preserveFields = PreserveFields
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .ToList();

            if (PreserveCharacters)
            {
                preserveFields.Add("Characters");
            }

            await _autoScrapingService.ApplyMetadataAsync(
                gameId,
                SelectedMatch.Metadata,
                preserveFields).ConfigureAwait(false);

            // Remove from review list
            GamesNeedingReview.Remove(SelectedGameForReview);
            SelectedGameForReview = null;
            SelectedMatch = null;
            AvailableMatches.Clear();
            ChangePreview.Clear();

            NeedsReviewCount = GamesNeedingReview.Count;

            _logger.LogInformation("Applied metadata to game {GameId}", gameId);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to apply metadata: {ex.Message}";
            _logger.LogError(ex, "Apply metadata failed");
        }
    }

    /// <summary>
    /// Skips the selected game (keeps existing metadata).
    /// </summary>
    [RelayCommand]
    private void SkipGameReview(GameScrapingResultItem? game)
    {
        if (game == null)
        {
            return;
        }

        GamesNeedingReview.Remove(game);
        NeedsReviewCount = GamesNeedingReview.Count;

        if (SelectedGameForReview == game)
        {
            SelectedGameForReview = null;
            SelectedMatch = null;
            AvailableMatches.Clear();
            ChangePreview.Clear();
        }

        _logger.LogInformation("Skipped review for game {GameId}", game.GameId);
    }

    /// <summary>
    /// Clears all results and resets the view.
    /// </summary>
    [RelayCommand]
    private void ClearResults()
    {
        GameResults.Clear();
        GamesNeedingReview.Clear();
        ResetState();
    }

    /// <summary>
    /// Manually searches for a game.
    /// </summary>
    [RelayCommand]
    private async Task ManualSearchAsync(string? gameName)
    {
        if (string.IsNullOrWhiteSpace(gameName))
        {
            ErrorMessage = "请输入游戏名称进行搜索";
            return;
        }

        try
        {
            AvailableMatches.Clear();
            SelectedMatch = null;
            ChangePreview.Clear();

            var result = await _gameScrapingService.SearchGameAsync(gameName).ConfigureAwait(false);

            if (result.HasResults)
            {
                // Collect all matches from all sources
                foreach (var sourceResult in result.SourceResults)
                {
                    foreach (var item in sourceResult.Value.Items)
                    {
                        AvailableMatches.Add(new MetadataMatchItem(item));
                    }
                }

                // Select best match
                var bestMatch = AvailableMatches.OrderByDescending(m => m.MatchScore).FirstOrDefault();
                SelectedMatch = bestMatch;
            }
            else
            {
                ErrorMessage = "未找到匹配项";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Search failed: {ex.Message}";
            _logger.LogError(ex, "Manual search failed for {GameName}", gameName);
        }
    }

    #endregion

    #region Event Handlers

    private void OnProgressChanged(object? sender, ScrapingProgressEventArgs e)
    {
        // Ensure UI updates happen on the correct thread
        if (_dispatcherQueue != null)
        {
            _dispatcherQueue.TryEnqueue(() => UpdateProgressProperties(e));
        }
        else
        {
            // Direct update if no dispatcher available (e.g., during tests)
            UpdateProgressProperties(e);
        }
    }

    private void UpdateProgressProperties(ScrapingProgressEventArgs e)
    {
        // Update all properties from progress
        TotalGames = e.Progress.TotalGames;
        CompletedGames = e.Progress.CompletedGames;
        SuccessCount = e.Progress.SuccessCount;
        FailedCount = e.Progress.FailedCount;
        SkippedCount = e.Progress.SkippedCount;
        NeedsReviewCount = e.Progress.NeedsReviewCount;
        CurrentGameName = e.Progress.CurrentGameName;
        ProgressPercentage = e.Progress.ProgressPercentage;
        IsCancelled = e.Progress.IsCancelled;
        IsComplete = e.Progress.IsComplete;

        // Format elapsed time
        ElapsedTime = FormatTimeSpan(e.Progress.ElapsedTime);

        // Format estimated remaining time
        EstimatedRemainingTime = e.Progress.EstimatedRemainingTime.HasValue
            ? FormatTimeSpan(e.Progress.EstimatedRemainingTime.Value)
            : null;
    }

    private void OnGameCompleted(object? sender, GameScrapingResultEventArgs e)
    {
        // Ensure UI updates happen on the correct thread
        if (_dispatcherQueue != null)
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                // Add to results collection
                GameResults.Add(new GameScrapingResultItem(e.Result));

                _logger.LogDebug("Game completed: {GameId} - {Status}", e.Result.GameId, e.Result.Status);
            });
        }
        else
        {
            // Direct update if no dispatcher available
            GameResults.Add(new GameScrapingResultItem(e.Result));

            _logger.LogDebug("Game completed: {GameId} - {Status}", e.Result.GameId, e.Result.Status);
        }
    }

    private void OnBatchCompleted(object? sender, BatchScrapingResultEventArgs e)
    {
        // Ensure UI updates happen on the correct thread
        if (_dispatcherQueue != null)
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                // Update final summary
                IsRunning = false;
                IsComplete = true;

                UpdateSummaryMessage();
            });
        }
        else
        {
            // Direct update if no dispatcher available
            IsRunning = false;
            IsComplete = true;

            UpdateSummaryMessage();
        }
    }

    #endregion

    #region Private Methods

    private void ResetState()
    {
        TotalGames = 0;
        CompletedGames = 0;
        SuccessCount = 0;
        FailedCount = 0;
        SkippedCount = 0;
        NeedsReviewCount = 0;
        CurrentGameName = null;
        ProgressPercentage = 0;
        ElapsedTime = "00:00:00";
        EstimatedRemainingTime = null;
        IsRunning = false;
        IsCancelled = false;
        IsComplete = false;
        SummaryMessage = null;
        ErrorMessage = null;
    }

    private void UpdateSummaryMessage()
    {
        if (IsCancelled)
        {
            SummaryMessage = $"Scraping cancelled. Completed {CompletedGames} of {TotalGames} games.";
        }
        else
        {
            var parts = new List<string>();

            if (SuccessCount > 0)
                parts.Add($"{SuccessCount} successful");
            if (NeedsReviewCount > 0)
                parts.Add($"{NeedsReviewCount} need review");
            if (FailedCount > 0)
                parts.Add($"{FailedCount} failed");
            if (SkippedCount > 0)
                parts.Add($"{SkippedCount} skipped");

            SummaryMessage = $"Scraping complete: {string.Join(", ", parts)}";
        }
    }

    private static string FormatTimeSpan(TimeSpan time)
    {
        if (time.TotalSeconds < 60)
        {
            return $"{(int)time.TotalSeconds}s";
        }
        else if (time.TotalMinutes < 60)
        {
            return $"{(int)time.TotalMinutes}m {(int)time.Seconds}s";
        }
        else
        {
            return $"{(int)time.TotalHours}h {(int)time.Minutes}m";
        }
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Releases all resources used by the ScrapingProgressViewModel.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Unsubscribe from events
        UnsubscribeEvents();

        // Dispose CancellationTokenSource if exists
        if (_currentCts != null)
        {
            if (!_currentCts.Token.IsCancellationRequested)
            {
                _currentCts.Cancel();
            }
            _currentCts.Dispose();
            _currentCts = null;
        }

        _logger.LogInformation("ScrapingProgressViewModel disposed");
    }

    #endregion
}

/// <summary>
/// Display item for a game scraping result.
/// </summary>
public class GameScrapingResultItem
{
    /// <summary>
    /// The game ID.
    /// </summary>
    public int GameId { get; }

    /// <summary>
    /// The game name.
    /// </summary>
    public string? GameName { get; }

    /// <summary>
    /// The scraping status.
    /// </summary>
    public GameScrapingStatus Status { get; }

    /// <summary>
    /// Status display text.
    /// </summary>
    public string StatusText => GetStatusText(Status);

    /// <summary>
    /// Status color for UI.
    /// </summary>
    public string StatusColor => GetStatusColor(Status);

    /// <summary>
    /// Match score percentage.
    /// </summary>
    public double MatchScore { get; }

    /// <summary>
    /// Match score formatted.
    /// </summary>
    public string MatchScoreText => MatchScore > 0 ? $"{MatchScore:F0}%" : "N/A";

    /// <summary>
    /// Whether the result was auto-accepted.
    /// </summary>
    public bool AutoAccepted { get; }

    /// <summary>
    /// Best matching source.
    /// </summary>
    public ScraperSource BestMatchSource { get; }

    /// <summary>
    /// All available matches.
    /// </summary>
    public List<GameMetadata> AllMatches { get; }

    /// <summary>
    /// Error message if failed.
    /// </summary>
    public string? ErrorMessage { get; }

    /// <summary>
    /// Time taken for scraping.
    /// </summary>
    public long ElapsedMilliseconds { get; }

    /// <summary>
    /// Best match metadata.
    /// </summary>
    public GameMetadata? BestMatch { get; }

    /// <summary>
    /// Creates a GameScrapingResultItem from a GameScrapingResult.
    /// </summary>
    public GameScrapingResultItem(GameScrapingResult result)
    {
        GameId = result.GameId;
        GameName = result.GameName;
        Status = result.Status;
        MatchScore = result.MatchScore;
        AutoAccepted = result.AutoAccepted;
        BestMatchSource = result.BestMatchSource;
        AllMatches = result.AllMatches;
        ErrorMessage = result.ErrorMessage;
        ElapsedMilliseconds = result.ElapsedMilliseconds;
        BestMatch = result.BestMatch;
    }

    private static string GetStatusText(GameScrapingStatus status)
    {
        return status switch
        {
            GameScrapingStatus.SuccessAutoAccepted => "Success",
            GameScrapingStatus.SuccessNeedsReview => "Needs Review",
            GameScrapingStatus.NoMatch => "No Match",
            GameScrapingStatus.SkippedAlreadyComplete => "Skipped",
            GameScrapingStatus.Failed => "Failed",
            GameScrapingStatus.Queued => "Queued",
            GameScrapingStatus.Processing => "Processing",
            GameScrapingStatus.Cancelled => "Cancelled",
            _ => "Unknown"
        };
    }

    private static string GetStatusColor(GameScrapingStatus status)
    {
        return status switch
        {
            GameScrapingStatus.SuccessAutoAccepted => "Green",
            GameScrapingStatus.SuccessNeedsReview => "Orange",
            GameScrapingStatus.NoMatch => "Gray",
            GameScrapingStatus.SkippedAlreadyComplete => "Blue",
            GameScrapingStatus.Failed => "Red",
            GameScrapingStatus.Queued => "Gray",
            GameScrapingStatus.Processing => "Blue",
            GameScrapingStatus.Cancelled => "Gray",
            _ => "Gray"
        };
    }
}

/// <summary>
/// Display item for a metadata match option.
/// </summary>
public class MetadataMatchItem
{
    /// <summary>
    /// The metadata.
    /// </summary>
    public GameMetadata Metadata { get; }

    /// <summary>
    /// Match score.
    /// </summary>
    public double MatchScore { get; }

    /// <summary>
    /// Match score formatted.
    /// </summary>
    public string MatchScoreText => $"{MatchScore:F0}%";

    /// <summary>
    /// Title (Chinese or original).
    /// </summary>
    public string? Title { get; }

    /// <summary>
    /// Source name.
    /// </summary>
    public string SourceName { get; }

    /// <summary>
    /// Whether this is the best match.
    /// </summary>
    public bool IsBestMatch { get; }

    /// <summary>
    /// Creates a MetadataMatchItem from GameMetadata.
    /// </summary>
    public MetadataMatchItem(GameMetadata metadata)
    {
        Metadata = metadata;
        MatchScore = metadata.MatchScore;
        Title = metadata.TitleCn ?? metadata.TitleOriginal ?? "Unknown";
        SourceName = metadata.Source.ToString();
        IsBestMatch = metadata.IsBestMatch;
    }
}

/// <summary>
/// Display item for a metadata field change preview.
/// </summary>
public class MetadataFieldPreview
{
    /// <summary>
    /// Field name.
    /// </summary>
    public string FieldName { get; }

    /// <summary>
    /// Current value.
    /// </summary>
    public string? CurrentValue { get; }

    /// <summary>
    /// New value.
    /// </summary>
    public string? NewValue { get; }

    /// <summary>
    /// Change description.
    /// </summary>
    public string? ChangeDescription { get; }

    /// <summary>
    /// Whether this field is preserved.
    /// </summary>
    public bool IsPreserved { get; }

    /// <summary>
    /// Creates a MetadataFieldPreview from MetadataFieldChange.
    /// </summary>
    public MetadataFieldPreview(MetadataFieldChange change)
    {
        FieldName = change.FieldName;
        CurrentValue = TruncateValue(change.CurrentValue);
        NewValue = TruncateValue(change.NewValue);
        ChangeDescription = change.ChangeDescription;
        IsPreserved = change.IsPreserved;
    }

    private static string? TruncateValue(string? value, int maxLength = 100)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "(empty)";

        return value.Length > maxLength ? value.Substring(0, maxLength) + "..." : value;
    }
}