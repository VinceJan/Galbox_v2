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
    /// Flag to track whether events are currently subscribed.
    /// </summary>
    /// <remarks>
    /// D13 fix: this used to be initialized to <c>true</c> while <see cref="SubscribeEvents"/>
    /// starts with <c>if (_eventsSubscribed) return;</c>, so the progress events were
    /// <b>never</b> wired up and the view never updated.
    /// </remarks>
    private bool _eventsSubscribed;

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

    /// <summary>
    /// Per-source diagnostics of the last scrape (query, success, item count, elapsed, error).
    /// D12: this is what makes "the API rejected us" different from "the game is not in the database".
    /// </summary>
    public ObservableCollection<SourceDiagnosticItem> SourceDiagnostics { get; } = new();

    /// <summary>
    /// Candidate results from the last scrape, best score first.
    /// D11: results below the auto-accept threshold stay reachable here instead of
    /// being silently discarded.
    /// </summary>
    public ObservableCollection<CandidateMatchItem> Candidates { get; } = new();

    /// <summary>
    /// Whether the last run produced any candidate rows.
    /// </summary>
    [ObservableProperty]
    private bool _hasCandidates;

    /// <summary>
    /// Game id the candidate list belongs to (used when applying a candidate).
    /// </summary>
    [ObservableProperty]
    private int _candidateGameId;

    /// <summary>
    /// Human readable status of the candidate application.
    /// </summary>
    [ObservableProperty]
    private string? _applyStatusMessage;

    /// <summary>
    /// Diagnostic detail of the currently selected source row.
    /// </summary>
    [ObservableProperty]
    private string? _selectedDiagnosticDetail;

    #endregion

    #region Commands

    /// <summary>
    /// Starts batch scraping for selected games.
    /// </summary>
    [RelayCommand]
    private async Task StartBatchScrapingAsync(IEnumerable<int>? gameIds)
    {
        await ScrapeGamesAsync(gameIds ?? Enumerable.Empty<int>());
    }

    /// <summary>
    /// Scrapes the given games and reports progress.
    /// </summary>
    /// <remarks>
    /// This is the single entry point used by the scraping page (game detail "scrape" button,
    /// auto-scrape on add, and the batch command).
    /// </remarks>
    public async Task ScrapeGamesAsync(IEnumerable<int> gameIds)
    {
        var ids = gameIds?.Where(id => id > 0).Distinct().ToList() ?? new List<int>();
        if (ids.Count == 0)
        {
            ErrorMessage = "未选择要刮削的游戏";
            return;
        }

        // Reset state
        ResetState();
        IsRunning = true;

        // Enqueue games
        _autoScrapingService.EnqueueGames(ids);

        _currentCts?.Dispose();
        _currentCts = new CancellationTokenSource();

        try
        {
            // No ConfigureAwait(false) here: this runs from the UI thread and the
            // ObservableProperty setters below must fire on it.
            await _autoScrapingService.StartScrapingAsync(_currentCts.Token);
        }
        catch (OperationCanceledException)
        {
            IsCancelled = true;
            _logger.LogInformation("Batch scraping was cancelled");
        }
        catch (Exception ex)
        {
            ErrorMessage = $"刮削失败：{ex.Message}";
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
    /// Manually searches for a name and shows every candidate, regardless of score.
    /// </summary>
    /// <param name="gameName">Name to search for.</param>
    /// <param name="gameId">Optional game id, so a candidate can be applied afterwards.</param>
    public async Task SearchManuallyAsync(string? gameName, int gameId = 0)
    {
        if (string.IsNullOrWhiteSpace(gameName))
        {
            ErrorMessage = "请输入游戏名称进行搜索";
            return;
        }

        try
        {
            Candidates.Clear();
            SourceDiagnostics.Clear();
            HasCandidates = false;
            ApplyStatusMessage = null;
            ErrorMessage = null;

            var result = await _gameScrapingService.SearchGameAsync(gameName);

            FillDiagnostics(result);
            FillCandidates(result.SourceResults.SelectMany(r => r.Value.Items), gameId);

            if (!result.HasResults)
            {
                // D12: never collapse an API failure into "no results".
                ErrorMessage = result.Errors.Count > 0
                    ? "未找到匹配项（数据源报错）：" + string.Join(" | ", result.Errors)
                    : "未找到匹配项";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"搜索失败：{ex.Message}";
            _logger.LogError(ex, "Manual search failed for {GameName}", gameName);
        }
    }

    /// <summary>
    /// Applies a candidate result to a game (manual review path, D11).
    /// </summary>
    [RelayCommand]
    private async Task ApplyCandidateAsync(CandidateMatchItem? candidate)
    {
        if (candidate == null)
        {
            return;
        }

        var gameId = candidate.GameId > 0 ? candidate.GameId : CandidateGameId;
        if (gameId <= 0)
        {
            ApplyStatusMessage = "无法应用：缺少游戏 ID";
            return;
        }

        try
        {
            // The accepted candidate may come from Bangumi while the VNDB id is the
            // cross-system key, so hand over the best VNDB candidate id as well.
            var vndbId = Candidates
                .Where(c => c.Metadata.Source == ScraperSource.Vndb && !string.IsNullOrWhiteSpace(c.Metadata.SourceId))
                .OrderByDescending(c => c.MatchScore)
                .Select(c => c.Metadata.SourceId)
                .FirstOrDefault();

            await _autoScrapingService.ApplyMetadataAsync(gameId, candidate.Metadata, null, default, vndbId);
            ApplyStatusMessage = $"已应用「{candidate.Title}」到游戏 {gameId}";
            _logger.LogInformation("Applied candidate {Title} to game {GameId}", candidate.Title, gameId);
        }
        catch (Exception ex)
        {
            ApplyStatusMessage = $"应用失败:{ex.Message}";
            _logger.LogError(ex, "Failed to apply candidate metadata to game {GameId}", gameId);
        }
    }

    /// <summary>
    /// Shows the full error detail of a source row.
    /// </summary>
    [RelayCommand]
    private void ShowDiagnosticDetail(SourceDiagnosticItem? diagnostic)
    {
        SelectedDiagnosticDetail = diagnostic == null
            ? null
            : diagnostic.BuildDetail();
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

        var results = await _autoScrapingService.GetGamesNeedingReviewAsync();

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
                SelectedMatch.Metadata);

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
                preserveFields);

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
        await SearchManuallyAsync(gameName, CandidateGameId);
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
            _dispatcherQueue.TryEnqueue(() => HandleGameCompleted(e.Result));
        }
        else
        {
            // Direct update if no dispatcher available (e.g., during tests)
            HandleGameCompleted(e.Result);
        }
    }

    /// <summary>
    /// Adds a finished game to the view and exposes its diagnostics and candidates.
    /// </summary>
    private void HandleGameCompleted(GameScrapingResult result)
    {
        GameResults.Add(new GameScrapingResultItem(result));

        // D12: surface the per-source outcome so a source failure is not shown as "not found".
        if (result.SourceResults.Count > 0)
        {
            FillDiagnostics(result.SourceResults, result.GameName);
        }

        if (result.Errors.Count > 0)
        {
            ErrorMessage = string.Join(" | ", result.Errors);
        }

        // D11: keep every candidate reachable so a below-threshold (but correct)
        // result can still be applied manually.
        FillCandidates(result.AllMatches, result.GameId);

        if (result.Status == GameScrapingStatus.NoMatch && result.Errors.Count == 0)
        {
            ErrorMessage = $"「{result.GameName}」未找到匹配项（各数据源均正常返回，但没有相似结果）";
        }

        _logger.LogDebug("Game completed: {GameId} - {Status}", result.GameId, result.Status);
    }

    /// <summary>
    /// Fills the source diagnostics list from an aggregated search result.
    /// </summary>
    private void FillDiagnostics(ScrapingResult result)
    {
        FillDiagnostics(result.SourceResults, null);
    }

    private void FillDiagnostics(
        Dictionary<ScraperSource, SourceScrapingResult> sourceResults,
        string? gameName)
    {
        SourceDiagnostics.Clear();

        foreach (var pair in sourceResults)
        {
            SourceDiagnostics.Add(new SourceDiagnosticItem(pair.Key, pair.Value, gameName));
        }
    }

    /// <summary>
    /// Fills the candidate list, best score first.
    /// </summary>
    private void FillCandidates(IEnumerable<GameMetadata> matches, int gameId)
    {
        Candidates.Clear();

        foreach (var match in matches.OrderByDescending(m => m.MatchScore))
        {
            Candidates.Add(new CandidateMatchItem(match, gameId));
        }

        CandidateGameId = gameId;
        HasCandidates = Candidates.Count > 0;
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
        SourceDiagnostics.Clear();
        Candidates.Clear();
        HasCandidates = false;
        CandidateGameId = 0;
        ApplyStatusMessage = null;
        SelectedDiagnosticDetail = null;
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

/// <summary>
/// Display row for one scraping source of the last run.
/// Implements the product contract "every request is traceable"
/// (source + query + success flag + error message).
/// </summary>
public class SourceDiagnosticItem
{
    /// <summary>
    /// The source this row describes.
    /// </summary>
    public ScraperSource Source { get; }

    /// <summary>
    /// Source name for display.
    /// </summary>
    public string SourceName { get; }

    /// <summary>
    /// The name that was actually sent to the source.
    /// </summary>
    public string SearchQuery { get; }

    /// <summary>
    /// Whether the source answered with at least one candidate.
    /// </summary>
    public bool HasResults { get; }

    /// <summary>
    /// Number of candidates returned.
    /// </summary>
    public int ItemCount { get; }

    /// <summary>
    /// Round-trip time in milliseconds.
    /// </summary>
    public long ElapsedMilliseconds { get; }

    /// <summary>
    /// Short error text, if the source failed.
    /// </summary>
    public string? ErrorMessage { get; }

    /// <summary>
    /// Full exception / response body, if available.
    /// </summary>
    public string? ExtendedErrorInfo { get; }

    /// <summary>
    /// One-line status text for the row.
    /// </summary>
    public string StatusText => HasResults
        ? $"OK · {ItemCount} 条 · {ElapsedMilliseconds} ms"
        : !string.IsNullOrEmpty(ErrorMessage)
            ? $"失败 · {ElapsedMilliseconds} ms"
            : $"无结果 · {ElapsedMilliseconds} ms";

    /// <summary>
    /// Whether the error details can be expanded.
    /// </summary>
    public bool HasDetail => !string.IsNullOrWhiteSpace(ExtendedErrorInfo) || !string.IsNullOrWhiteSpace(ErrorMessage);

    /// <summary>
    /// Creates a diagnostic row from a source result.
    /// </summary>
    public SourceDiagnosticItem(ScraperSource source, SourceScrapingResult result, string? gameName)
    {
        Source = source;
        SourceName = source.ToString();
        SearchQuery = string.IsNullOrWhiteSpace(result.SearchQuery) ? gameName ?? string.Empty : result.SearchQuery;
        ItemCount = result.Items.Count;
        HasResults = ItemCount > 0;
        ElapsedMilliseconds = result.ElapsedMilliseconds;
        ErrorMessage = result.ErrorMessage;
        ExtendedErrorInfo = result.ExtendedErrorInfo;
    }

    /// <summary>
    /// Builds the expandable detail text for this row.
    /// </summary>
    public string BuildDetail()
    {
        var lines = new List<string>
        {
            $"来源: {SourceName}",
            $"搜索关键词: {SearchQuery}",
            $"返回条目: {ItemCount}",
            $"耗时: {ElapsedMilliseconds} ms"
        };

        if (!string.IsNullOrWhiteSpace(ErrorMessage))
        {
            lines.Add($"错误: {ErrorMessage}");
        }

        if (!string.IsNullOrWhiteSpace(ExtendedErrorInfo))
        {
            lines.Add("完整错误信息:");
            lines.Add(ExtendedErrorInfo!);
        }

        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
/// Display row for one candidate metadata result.
/// </summary>
public class CandidateMatchItem
{
    /// <summary>
    /// The metadata that would be applied.
    /// </summary>
    public GameMetadata Metadata { get; }

    /// <summary>
    /// Game id this candidate was found for.
    /// </summary>
    public int GameId { get; }

    /// <summary>
    /// Match score against the search term.
    /// </summary>
    public double MatchScore { get; }

    /// <summary>
    /// Score formatted for display.
    /// </summary>
    public string MatchScoreText => $"{MatchScore:F2}%";

    /// <summary>
    /// Candidate title (Chinese name preferred).
    /// </summary>
    public string Title { get; }

    /// <summary>
    /// Original title.
    /// </summary>
    public string OriginalTitle { get; }

    /// <summary>
    /// Source name.
    /// </summary>
    public string SourceName { get; }

    /// <summary>
    /// Source id, useful when looking the entry up on the website.
    /// </summary>
    public string SourceId { get; }

    /// <summary>
    /// Whether this candidate would be auto-accepted.
    /// </summary>
    public bool IsAutoAccepted { get; }

    /// <summary>
    /// Short field summary (developer / date / whether a cover exists).
    /// </summary>
    public string Summary { get; }

    /// <summary>
    /// Creates a candidate row.
    /// </summary>
    public CandidateMatchItem(GameMetadata metadata, int gameId, double autoAcceptThreshold = -1)
    {
        Metadata = metadata;
        GameId = gameId;
        MatchScore = metadata.MatchScore;
        Title = metadata.TitleCn ?? metadata.TitleOriginal ?? "Unknown";
        OriginalTitle = metadata.TitleOriginal ?? string.Empty;
        SourceName = metadata.Source.ToString();
        SourceId = metadata.SourceId;
        IsAutoAccepted = autoAcceptThreshold < 0 || MatchScore >= autoAcceptThreshold;

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(metadata.Developer)) parts.Add(metadata.Developer!);
        if (metadata.ReleaseDate.HasValue) parts.Add(metadata.ReleaseDate.Value.ToString("yyyy-MM-dd"));
        if (!string.IsNullOrWhiteSpace(metadata.CoverImageUrl)) parts.Add("有封面");
        Summary = parts.Count > 0 ? string.Join(" · ", parts) : "无附加信息";
    }
}