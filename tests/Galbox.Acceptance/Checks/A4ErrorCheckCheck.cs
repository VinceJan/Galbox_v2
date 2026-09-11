using Galbox.App.Models;
using Galbox.App.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Galbox.Acceptance.Checks;

/// <summary>
/// A4 - <see cref="IErrorCheckingService.CheckGameAsync"/> on the real game.
///
/// <c>CheckGameAsync</c> swallows every exception internally and returns the partial list it
/// has built so far, so "no exception escaped" is not enough to call it healthy. The verdict
/// is therefore: the call returns, AND the service wrote no WARNING/ERROR to the logger, AND
/// the returned items are internally well formed (title/category/severity populated).
/// An empty list is a valid, passing result and is reported as such.
/// </summary>
public sealed class A4ErrorCheckCheck : IAcceptanceCheck
{
    /// <inheritdoc />
    public string Id => "A4";

    /// <inheritdoc />
    public string Title => "CheckGameAsync runs without internal failure";

    /// <inheritdoc />
    public async Task<CheckResult> RunAsync(AcceptanceContext context, CancellationToken cancellationToken)
    {
        var game = context.PersistedGame ?? A0DatabaseCheck.BuildTestGame(context.Options);
        var errorChecking = context.Get<IErrorCheckingService>();
        var details = new List<string>();

        var expected = "call returns a list, no internal ERROR is logged, "
                     + "and every item has a title, category and severity (0 items is a valid result)";

        details.Add($"GameInfo.Id        : {game.Id}");
        details.Add($"GameInfo.InstallPath  : {game.InstallPath}");
        details.Add($"GameInfo.MainExecutable: {game.MainExecutable}");
        details.Add($"GameInfo.NameOriginal : {game.NameOriginal}");

        var errors = await errorChecking.CheckGameAsync(game, cancellationToken).ConfigureAwait(false);

        details.Add($"Findings           : {errors.Count}");

        if (errors.Count == 0)
        {
            details.Add("(no issues reported - this is a valid outcome for an English-path Ren'Py game)");
        }

        for (var i = 0; i < errors.Count; i++)
        {
            var e = errors[i];
            details.Add($"--- finding [{i}] ---");
            details.Add($"  Title              : {e.Title}");
            details.Add($"  Severity           : {e.Severity} ({e.SeverityDisplay})");
            details.Add($"  Category           : {e.Category} ({e.CategoryDisplay})");
            details.Add($"  SolutionType       : {e.SolutionType} ({e.SolutionTypeDisplay})");
            details.Add($"  AutoFixAvailable   : {e.AutoFixAvailable}");
            details.Add($"  FixAction          : {e.FixAction ?? "(null)"}");
            details.Add($"  DownloadUrl        : {e.DownloadUrl ?? "(null)"}");
            details.Add($"  ToolName           : {e.ToolName ?? "(null)"}");
            details.Add($"  Description        : {e.Description}");
            details.Add($"  SolutionInstructions: {e.SolutionInstructions}");
            details.Add($"  ContextData        : {string.Join(", ", e.ContextData.Select(kv => $"{kv.Key}={kv.Value}"))}");
            details.Add($"  GameId             : {e.GameId}");
        }

        // Persistence side effect: CheckGameAsync writes detected errors to the DB.
        int persistedRecords;
        await using (var scope = context.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Galbox.Data.Entities.GalboxDbContext>();
            persistedRecords = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
                .CountAsync(db.ErrorRecords, cancellationToken)
                .ConfigureAwait(false);
        }

        details.Add($"ErrorRecords rows persisted for this game: {persistedRecords}");

        // --- Verdict -------------------------------------------------------------------
        var malformed = errors
            .Where(e => string.IsNullOrWhiteSpace(e.Title) || string.IsNullOrWhiteSpace(e.Description))
            .ToList();

        if (malformed.Count > 0)
        {
            details.Add($"FAIL REASON: {malformed.Count} finding(s) have an empty Title/Description.");
            return CheckResult.Fail(Id, Title, expected, $"{malformed.Count} malformed finding(s)")
                .With(details.ToArray());
        }

        if (errors.Count != persistedRecords)
        {
            details.Add($"FAIL REASON: {errors.Count} finding(s) returned but {persistedRecords} row(s) persisted - "
                      + "the error records are not being written consistently.");
            return CheckResult.Fail(Id, Title, expected,
                $"returned={errors.Count}, persisted={persistedRecords}").With(details.ToArray());
        }

        return CheckResult.Pass(Id, Title, expected, $"{errors.Count} finding(s), {persistedRecords} row(s) persisted")
            .With(details.ToArray());
    }
}
