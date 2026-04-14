using System.Collections.Concurrent;
using System.Diagnostics;
using Galbox.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Galbox.App.Services;

/// <summary>
/// Service for automatic background scraping of game metadata.
/// Implements batch processing, progress reporting, retry mechanism, and merge logic.
/// </summary>
public class AutoScrapingService : IAutoScrapingService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<AutoScrapingService> _logger;
    private readonly ConcurrentQueue<int> _scrapingQueue = new();
    private readonly ConcurrentDictionary<int, GameScrapingResult> _results = new();
    private readonly SemaphoreSlim _processingLock = new(1, 1);
    private readonly ConcurrentBag<int> _gamesNeedingReview = new();

    private CancellationTokenSource? _currentCts;
    private Stopwatch? _operationTimer;
    private bool _isRunning;

    // Configuration constants
    private const int BatchSize = 5; // Process 5 games at a time
    private const int MaxRetryCount = 3;
    private const double AutoAcceptThreshold = 90.0;
    private const int RetryDelayMs = 2000;

    /// <summary>
    /// Creates an AutoScrapingService with injected dependencies.
    /// </summary>
    public AutoScrapingService(
        IServiceProvider serviceProvider,
        ILogger<AutoScrapingService> logger)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public bool IsRunning => _isRunning;

    /// <inheritdoc />
    public event EventHandler<ScrapingProgressEventArgs>? ProgressChanged;

    /// <inheritdoc />
    public event EventHandler<GameScrapingResultEventArgs>? GameCompleted;

    /// <inheritdoc />
    public event EventHandler<BatchScrapingResultEventArgs>? BatchCompleted;

    /// <inheritdoc />
    public void EnqueueGame(int gameId)
    {
        if (gameId <= 0)
        {
            _logger.LogWarning("Invalid game ID provided: {GameId}", gameId);
            return;
        }

        _scrapingQueue.Enqueue(gameId);
        _logger.LogInformation("Enqueued game {GameId} for scraping", gameId);
    }

    /// <inheritdoc />
    public void EnqueueGames(IEnumerable<int> gameIds)
    {
        foreach (var gameId in gameIds)
        {
            EnqueueGame(gameId);
        }

        _logger.LogInformation("Enqueued {Count} games for batch scraping", gameIds.Count());
    }

    /// <inheritdoc />
    public async Task StartScrapingAsync(CancellationToken cancellationToken = default)
    {
        await _processingLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_isRunning)
            {
                _logger.LogWarning("Scraping is already in progress");
                return;
            }

            _isRunning = true;
            _currentCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _operationTimer = Stopwatch.StartNew();
            _results.Clear();
            _gamesNeedingReview.Clear();

            var totalGames = _scrapingQueue.Count;
            _logger.LogInformation("Starting batch scraping for {TotalGames} games", totalGames);

            // Report initial progress
            ReportProgress(new ScrapingProgress
            {
                TotalGames = totalGames,
                CompletedGames = 0,
                CurrentGameName = "Starting..."
            });

            // Process queue in batches
            var completedCount = 0;
            var successCount = 0;
            var failedCount = 0;
            var skippedCount = 0;
            var needsReviewCount = 0;

            while (!_scrapingQueue.IsEmpty && !_currentCts.Token.IsCancellationRequested)
            {
                // Get batch of games to process
                var batch = new List<int>();
                for (int i = 0; i < BatchSize && _scrapingQueue.TryDequeue(out var gameId); i++)
                {
                    batch.Add(gameId);
                }

                if (batch.Count == 0)
                    break;

                // Process batch in parallel
                var batchTasks = batch.Select(gameId => ProcessGameAsync(gameId, _currentCts.Token)).ToList();
                var batchResults = await Task.WhenAll(batchTasks).ConfigureAwait(false);

                // Process results
                foreach (var result in batchResults)
                {
                    _results[result.GameId] = result;
                    completedCount++;

                    switch (result.Status)
                    {
                        case GameScrapingStatus.SuccessAutoAccepted:
                            successCount++;
                            break;
                        case GameScrapingStatus.SuccessNeedsReview:
                            successCount++;
                            needsReviewCount++;
                            _gamesNeedingReview.Add(result.GameId);
                            break;
                        case GameScrapingStatus.NoMatch:
                            failedCount++;
                            break;
                        case GameScrapingStatus.SkippedAlreadyComplete:
                            skippedCount++;
                            break;
                        case GameScrapingStatus.Failed:
                            failedCount++;
                            break;
                        case GameScrapingStatus.Cancelled:
                            // Don't count cancelled
                            break;
                    }

                    // Raise game completed event
                    GameCompleted?.Invoke(this, new GameScrapingResultEventArgs(result));
                }

                // Calculate estimated remaining time
                var avgTimePerGame = _operationTimer.ElapsedMilliseconds / completedCount;
                var remainingGames = totalGames - completedCount;
                var estimatedRemaining = TimeSpan.FromMilliseconds(avgTimePerGame * remainingGames);

                // Report progress
                var progress = new ScrapingProgress
                {
                    TotalGames = totalGames,
                    CompletedGames = completedCount,
                    SuccessCount = successCount,
                    FailedCount = failedCount,
                    SkippedCount = skippedCount,
                    NeedsReviewCount = needsReviewCount,
                    CurrentGameName = batchResults.FirstOrDefault(r => r.Status == GameScrapingStatus.Processing)?.GameName ?? "Processing...",
                    ElapsedTime = _operationTimer.Elapsed,
                    EstimatedRemainingTime = estimatedRemaining,
                    IsCancelled = _currentCts.Token.IsCancellationRequested
                };

                ReportProgress(progress);
            }

            // Stop timer and report final progress
            _operationTimer.Stop();

            var finalProgress = new ScrapingProgress
            {
                TotalGames = totalGames,
                CompletedGames = completedCount,
                SuccessCount = successCount,
                FailedCount = failedCount,
                SkippedCount = skippedCount,
                NeedsReviewCount = needsReviewCount,
                CurrentGameName = "Complete",
                ElapsedTime = _operationTimer.Elapsed,
                IsCancelled = _currentCts.Token.IsCancellationRequested
            };

            ReportProgress(finalProgress);

            // Raise batch completed event
            BatchCompleted?.Invoke(this, new BatchScrapingResultEventArgs(
                _results.Values.ToList(),
                finalProgress));

            _logger.LogInformation(
                "Batch scraping completed: {Success} success, {Failed} failed, {Skipped} skipped, {Review} needs review",
                successCount, failedCount, skippedCount, needsReviewCount);
        }
        finally
        {
            _isRunning = false;
            _processingLock.Release();
            _currentCts?.Dispose();
            _currentCts = null;
        }
    }

    /// <inheritdoc />
    public void StopScraping()
    {
        if (_currentCts != null && !_currentCts.Token.IsCancellationRequested)
        {
            _logger.LogInformation("Stopping scraping operation");
            _currentCts.Cancel();
        }
    }

    /// <inheritdoc />
    public ScrapingProgress GetCurrentProgress()
    {
        var totalGames = _scrapingQueue.Count + _results.Count;
        var completedGames = _results.Count;

        return new ScrapingProgress
        {
            TotalGames = totalGames,
            CompletedGames = completedGames,
            SuccessCount = _results.Values.Count(r => r.Status == GameScrapingStatus.SuccessAutoAccepted),
            FailedCount = _results.Values.Count(r => r.Status == GameScrapingStatus.Failed || r.Status == GameScrapingStatus.NoMatch),
            SkippedCount = _results.Values.Count(r => r.Status == GameScrapingStatus.SkippedAlreadyComplete),
            NeedsReviewCount = _gamesNeedingReview.Count,
            CurrentGameName = IsRunning ? "Processing..." : "Idle",
            IsCancelled = _currentCts?.Token.IsCancellationRequested ?? false,
            ElapsedTime = _operationTimer?.Elapsed ?? TimeSpan.Zero
        };
    }

    /// <inheritdoc />
    public async Task<List<GameScrapingResult>> GetGamesNeedingReviewAsync()
    {
        return _gamesNeedingReview
            .Select(id => _results.TryGetValue(id, out var result) ? result : null)
            .Where(r => r != null)
            .ToList()!;
    }

    /// <inheritdoc />
    public async Task ApplyMetadataAsync(
        int gameId,
        GameMetadata metadata,
        List<string>? preserveUserFields = null,
        CancellationToken cancellationToken = default)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GalboxDbContext>();

        var game = await dbContext.Games
            .Include(g => g.Characters)
            .FirstOrDefaultAsync(g => g.Id == gameId, cancellationToken)
            .ConfigureAwait(false);

        if (game == null)
        {
            _logger.LogWarning("Game not found with ID {GameId}", gameId);
            return;
        }

        // Apply metadata, preserving user-customized fields
        ApplyMetadataToGame(game, metadata, preserveUserFields);

        // Update scraping status
        game.IsScraped = true;
        game.SourceId = metadata.SourceId;
        game.SourceType = metadata.Source.ToString();
        game.UpdatedTime = DateTime.UtcNow;

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Applied metadata to game {GameId} from source {Source}", gameId, metadata.Source);
    }

    /// <inheritdoc />
    public async Task<MetadataMergePreview> PreviewMergeAsync(int gameId, GameMetadata metadata)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GalboxDbContext>();

        var game = await dbContext.Games
            .Include(g => g.Characters)
            .FirstOrDefaultAsync(g => g.Id == gameId)
            .ConfigureAwait(false);

        if (game == null)
        {
            return new MetadataMergePreview { GameId = gameId };
        }

        var preview = new MetadataMergePreview { GameId = gameId };

        // Compare each field
        CompareField(preview, "NameCn", game.NameCn, metadata.TitleCn);
        CompareField(preview, "Description", game.Description, metadata.Description);
        CompareField(preview, "CoverImageUrl", game.CoverImageUrl, metadata.CoverImageUrl);
        CompareField(preview, "BackgroundImageUrl", game.BackgroundImageUrl, metadata.BannerImageUrl);
        CompareField(preview, "Developer", game.Developer, metadata.Developer);
        CompareField(preview, "ReleaseDate", game.ReleaseDate?.ToString("yyyy-MM-dd"), metadata.ReleaseDate?.ToString("yyyy-MM-dd"));
        CompareField(preview, "Rating", game.Rating?.ToString("F1"), metadata.Rating?.ToString("F1"));

        // Compare tags
        var currentTags = DeserializeTags(game.TagsJson);
        var newTags = metadata.Tags;
        if (currentTags != null && newTags != null)
        {
            var tagsChange = !currentTags.SequenceEqual(newTags);
            preview.Changes.Add(new MetadataFieldChange
            {
                FieldName = "Tags",
                CurrentValue = currentTags.Count > 0 ? $"{currentTags.Count} tags" : "None",
                NewValue = newTags.Count > 0 ? $"{newTags.Count} tags" : "None",
                ChangeDescription = tagsChange ? "Tags will be updated" : "Tags unchanged"
            });
        }

        // Compare characters
        var currentCharacters = game.Characters?.Count ?? 0;
        var newCharacters = metadata.Characters?.Count ?? 0;
        preview.Changes.Add(new MetadataFieldChange
        {
            FieldName = "Characters",
            CurrentValue = currentCharacters > 0 ? $"{currentCharacters} characters" : "None",
            NewValue = newCharacters > 0 ? $"{newCharacters} characters" : "None",
            ChangeDescription = newCharacters > currentCharacters ? "Characters will be added" : "Characters unchanged"
        });

        return preview;
    }

    #region Private Methods

    private async Task<GameScrapingResult> ProcessGameAsync(int gameId, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = new GameScrapingResult
        {
            GameId = gameId,
            Status = GameScrapingStatus.Processing
        };

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Create scope for this operation
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<GalboxDbContext>();
            var scrapingService = scope.ServiceProvider.GetRequiredService<IGameScrapingService>();

            // Get game info
            var game = await dbContext.Games.FindAsync(gameId, cancellationToken).ConfigureAwait(false);
            if (game == null)
            {
                result.Status = GameScrapingStatus.Failed;
                result.ErrorMessage = "Game not found in database";
                return result;
            }

            result.GameName = game.DisplayName;

            // Check if game already has complete metadata
            if (IsMetadataComplete(game))
            {
                result.Status = GameScrapingStatus.SkippedAlreadyComplete;
                _logger.LogInformation("Skipped game {GameId} - already has complete metadata", gameId);
                stopwatch.Stop();
                result.ElapsedMilliseconds = stopwatch.ElapsedMilliseconds;
                return result;
            }

            // Perform scraping with retry mechanism
            AutoScrapeResult? scrapeResult = null;
            for (int retry = 0; retry < MaxRetryCount; retry++)
            {
                try
                {
                    scrapeResult = await scrapingService.AutoScrapeAsync(game, cancellationToken).ConfigureAwait(false);
                    if (scrapeResult != null && scrapeResult.BestMatch != null)
                    {
                        break; // Success
                    }
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogWarning(ex, "Scraping failed for game {GameId}, retry {Retry}", gameId, retry + 1);
                    result.RetryCount = retry + 1;
                    if (retry < MaxRetryCount - 1)
                    {
                        await Task.Delay(RetryDelayMs, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            // Process result
            if (scrapeResult == null || scrapeResult.BestMatch == null)
            {
                result.Status = GameScrapingStatus.NoMatch;
                result.ErrorMessage = scrapeResult?.Errors?.FirstOrDefault() ?? "No matching results found";
                _logger.LogWarning("No match found for game {GameId}: {GameName}", gameId, game.DisplayName);
            }
            else
            {
                result.BestMatch = scrapeResult.BestMatch;
                result.MatchScore = scrapeResult.BestMatch.MatchScore;
                result.BestMatchSource = scrapeResult.BestMatchSource;
                result.AllMatches = scrapeResult.AllMatches;

                if (scrapeResult.AutoAccepted)
                {
                    // Auto-accept high confidence matches
                    result.Status = GameScrapingStatus.SuccessAutoAccepted;
                    result.AutoAccepted = true;

                    // Apply metadata directly
                    ApplyMetadataToGame(game, scrapeResult.BestMatch, null);
                    game.IsScraped = true;
                    game.SourceId = scrapeResult.BestMatch.SourceId;
                    game.SourceType = scrapeResult.BestMatch.Source.ToString();
                    game.UpdatedTime = DateTime.UtcNow;

                    await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                    _logger.LogInformation(
                        "Auto-accepted metadata for game {GameId}: {GameName} (match: {MatchScore}%)",
                        gameId, game.DisplayName, result.MatchScore);
                }
                else
                {
                    // Mark for manual review
                    result.Status = GameScrapingStatus.SuccessNeedsReview;
                    _logger.LogInformation(
                        "Game {GameId}: {GameName} needs manual review (match: {MatchScore}%)",
                        gameId, game.DisplayName, result.MatchScore);
                }
            }
        }
        catch (OperationCanceledException)
        {
            result.Status = GameScrapingStatus.Cancelled;
            result.ErrorMessage = "Operation cancelled";
        }
        catch (Exception ex)
        {
            result.Status = GameScrapingStatus.Failed;
            result.ErrorMessage = ex.Message;
            _logger.LogError(ex, "Error scraping game {GameId}", gameId);
        }

        stopwatch.Stop();
        result.ElapsedMilliseconds = stopwatch.ElapsedMilliseconds;
        return result;
    }

    private static bool IsMetadataComplete(GameInfo game)
    {
        // Consider metadata complete if has: name, description, cover, developer, and source ID
        return !string.IsNullOrWhiteSpace(game.NameCn) &&
               !string.IsNullOrWhiteSpace(game.Description) &&
               !string.IsNullOrWhiteSpace(game.CoverImageUrl) &&
               !string.IsNullOrWhiteSpace(game.Developer) &&
               !string.IsNullOrWhiteSpace(game.SourceId) &&
               game.IsScraped;
    }

    private void ApplyMetadataToGame(GameInfo game, GameMetadata metadata, List<string>? preserveFields)
    {
        preserveFields ??= new List<string>();

        // Apply each field, respecting preservation
        if (!preserveFields.Contains("NameCn") && !string.IsNullOrWhiteSpace(metadata.TitleCn))
        {
            game.NameCn = metadata.TitleCn;
        }

        if (!preserveFields.Contains("NameOriginal") && !string.IsNullOrWhiteSpace(metadata.TitleOriginal))
        {
            // Preserve user-set original name only if explicitly requested
            if (string.IsNullOrWhiteSpace(game.NameOriginal) || preserveFields.Contains("NameOriginal"))
            {
                game.NameOriginal = metadata.TitleOriginal;
            }
        }

        if (!preserveFields.Contains("Description") && !string.IsNullOrWhiteSpace(metadata.Description))
        {
            game.Description = metadata.Description;
        }

        if (!preserveFields.Contains("CoverImageUrl") && !string.IsNullOrWhiteSpace(metadata.CoverImageUrl))
        {
            game.CoverImageUrl = metadata.CoverImageUrl;
        }

        if (!preserveFields.Contains("BackgroundImageUrl") && !string.IsNullOrWhiteSpace(metadata.BannerImageUrl))
        {
            game.BackgroundImageUrl = metadata.BannerImageUrl;
        }

        if (!preserveFields.Contains("Developer") && !string.IsNullOrWhiteSpace(metadata.Developer))
        {
            game.Developer = metadata.Developer;
        }

        if (!preserveFields.Contains("ReleaseDate") && metadata.ReleaseDate.HasValue)
        {
            game.ReleaseDate = metadata.ReleaseDate;
        }

        if (!preserveFields.Contains("Rating") && metadata.Rating.HasValue)
        {
            game.Rating = metadata.Rating;
        }

        // Tags - serialize to JSON
        if (!preserveFields.Contains("Tags") && metadata.Tags?.Count > 0)
        {
            game.TagsJson = System.Text.Json.JsonSerializer.Serialize(metadata.Tags);
        }

        // Characters - add new characters (don't remove existing user-added ones)
        if (!preserveFields.Contains("Characters") && metadata.Characters?.Count > 0)
        {
            var existingNames = game.Characters?.Select(c => c.Name).ToList() ?? new List<string>();
            foreach (var charInfo in metadata.Characters)
            {
                if (!existingNames.Contains(charInfo.Name))
                {
                    game.Characters?.Add(new GameCharacter
                    {
                        GameInfoId = game.Id,
                        Name = charInfo.Name,
                        NameCn = charInfo.NameCn,
                        ImageUrl = charInfo.ImageUrl,
                        Role = charInfo.Role
                    });
                }
            }
        }
    }

    private static void CompareField(
        MetadataMergePreview preview,
        string fieldName,
        string? currentValue,
        string? newValue)
    {
        var hasCurrentValue = !string.IsNullOrWhiteSpace(currentValue);
        var hasNewValue = !string.IsNullOrWhiteSpace(newValue);

        if (hasCurrentValue && hasNewValue && currentValue != newValue)
        {
            preview.Changes.Add(new MetadataFieldChange
            {
                FieldName = fieldName,
                CurrentValue = currentValue,
                NewValue = newValue,
                ChangeDescription = $"Will change from '{TruncateForDisplay(currentValue)}' to '{TruncateForDisplay(newValue)}'"
            });
        }
        else if (!hasCurrentValue && hasNewValue)
        {
            preview.Changes.Add(new MetadataFieldChange
            {
                FieldName = fieldName,
                CurrentValue = "(empty)",
                NewValue = newValue,
                ChangeDescription = $"Will add new value: '{TruncateForDisplay(newValue)}'"
            });
        }
        else if (hasCurrentValue && !hasNewValue)
        {
            preview.Unchanged.Add(new MetadataFieldChange
            {
                FieldName = fieldName,
                CurrentValue = currentValue,
                NewValue = "(empty)",
                ChangeDescription = "New metadata has no value for this field"
            });
        }
        else
        {
            preview.Unchanged.Add(new MetadataFieldChange
            {
                FieldName = fieldName,
                CurrentValue = currentValue ?? "(empty)",
                NewValue = newValue ?? "(empty)",
                ChangeDescription = "Values are identical"
            });
        }
    }

    private static string TruncateForDisplay(string? value, int maxLength = 50)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "(empty)";

        return value.Length > maxLength ? value.Substring(0, maxLength) + "..." : value;
    }

    private static List<string>? DeserializeTags(string? tagsJson)
    {
        if (string.IsNullOrWhiteSpace(tagsJson))
            return new List<string>();

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<string>>(tagsJson);
        }
        catch
        {
            return new List<string>();
        }
    }

    private void ReportProgress(ScrapingProgress progress)
    {
        ProgressChanged?.Invoke(this, new ScrapingProgressEventArgs(progress));
        _logger.LogDebug(
            "Progress: {Completed}/{Total} games, {Success} success, {Failed} failed, {Skipped} skipped",
            progress.CompletedGames, progress.TotalGames, progress.SuccessCount, progress.FailedCount, progress.SkippedCount);
    }

    #endregion
}