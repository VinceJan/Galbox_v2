using Galbox.App.Services;

namespace Galbox.Acceptance.Checks;

/// <summary>
/// A6 - metadata completeness of the A5 hits. Answers whether scraping produces *usable*
/// metadata, not merely HTTP 200s: for every item found it prints whether
/// <c>TitleCn</c>, <c>TitleOriginal</c>, <c>Rating</c> and <c>CoverImageUrl</c> are present,
/// plus per-source totals.
///
/// It also records a structural fact about <see cref="ScrapingResult.BestMatch"/>: that
/// property is typed as a whole <see cref="SourceScrapingResult"/>, not as the single winning
/// <see cref="GameMetadata"/>, so consumers that take <c>BestMatch.Items[0]</c> get the first
/// row the API happened to return rather than the row that actually matched. The check prints
/// both rows so the difference is visible instead of theoretical.
/// </summary>
public sealed class A6MetadataFieldsCheck : IAcceptanceCheck
{
    /// <inheritdoc />
    public string Id => "A6";

    /// <inheritdoc />
    public string Title => "Scraped metadata is complete enough to use";

    /// <inheritdoc />
    public Task<CheckResult> RunAsync(AcceptanceContext context, CancellationToken cancellationToken)
    {
        var details = new List<string>();
        var expected = "the best-matching item has a non-empty title AND a non-empty cover image URL";

        var result = context.ScrapingQuery;
        if (result is null)
        {
            return Task.FromResult(CheckResult.Error(Id, Title, expected,
                "A5 produced no ScrapingResult - cannot evaluate metadata",
                new InvalidOperationException("A5 did not run or did not complete")));
        }

        details.Add($"Query              : \"{context.ScrapingQueryText}\"");

        var allItems = new List<(ScraperSource Source, GameMetadata Item)>();
        foreach (var pair in result.SourceResults.OrderBy(p => p.Key.ToString(), StringComparer.Ordinal))
        {
            foreach (var item in pair.Value.Items)
            {
                allItems.Add((pair.Key, item));
            }
        }

        details.Add($"Total items across all sources: {allItems.Count}");
        details.Add(string.Empty);
        details.Add("Per-source totals:");
        details.Add("  source   | items | TitleCn | TitleOriginal | Rating | CoverImageUrl");
        details.Add("  ---------+-------+---------+---------------+--------+--------------");

        foreach (var pair in result.SourceResults.OrderBy(p => p.Key.ToString(), StringComparer.Ordinal))
        {
            var items = pair.Value.Items;
            details.Add($"  {pair.Key,-8} | {items.Count,5} | "
                      + $"{items.Count(i => !string.IsNullOrWhiteSpace(i.TitleCn)),7} | "
                      + $"{items.Count(i => !string.IsNullOrWhiteSpace(i.TitleOriginal)),13} | "
                      + $"{items.Count(i => i.Rating.HasValue),6} | "
                      + $"{items.Count(i => !string.IsNullOrWhiteSpace(i.CoverImageUrl)),13}");
        }

        details.Add(string.Empty);
        details.Add("Per-item field presence:");
        foreach (var (source, item) in allItems)
        {
            details.Add($"  [{source}] SourceId={item.SourceId} MatchScore={item.MatchScore} IsBestMatch={item.IsBestMatch}");
            details.Add($"      TitleCn       : {(string.IsNullOrWhiteSpace(item.TitleCn) ? "MISSING" : item.TitleCn)}");
            details.Add($"      TitleOriginal : {(string.IsNullOrWhiteSpace(item.TitleOriginal) ? "MISSING" : item.TitleOriginal)}");
            details.Add($"      Rating        : {(item.Rating.HasValue ? item.Rating.Value.ToString("F2") : "MISSING")}");
            details.Add($"      CoverImageUrl : {(string.IsNullOrWhiteSpace(item.CoverImageUrl) ? "MISSING" : item.CoverImageUrl)}");
            details.Add($"      Description   : {(string.IsNullOrWhiteSpace(item.Description) ? "MISSING" : $"{item.Description!.Length} chars")}");
            details.Add($"      Tags          : {item.Tags.Count}");
            details.Add($"      Characters    : {item.Characters.Count}");
        }

        // --- Structural note about BestMatch -------------------------------------------
        var bestMatch = result.BestMatch;
        if (bestMatch is null)
        {
            details.Add(string.Empty);
            details.Add("BestMatch is null, so there is no winning row to evaluate.");
            return Task.FromResult(CheckResult.Fail(Id, Title, expected, "BestMatch=null").With(details.ToArray()));
        }

        var firstItem = bestMatch.Items.FirstOrDefault();
        var flaggedBest = bestMatch.Items.FirstOrDefault(i => i.IsBestMatch);

        details.Add(string.Empty);
        details.Add("BestMatch structure (ScrapingResult.BestMatch is a SourceScrapingResult):");
        details.Add($"  BestMatch.Source            : {bestMatch.Source}");
        details.Add($"  BestMatch.Items.Count       : {bestMatch.Items.Count}");
        details.Add($"  BestMatch.Items[0]          : {Describe(firstItem)}");
        details.Add($"  item with IsBestMatch=true  : {Describe(flaggedBest)}");
        if (firstItem is not null && flaggedBest is not null && !ReferenceEquals(firstItem, flaggedBest))
        {
            details.Add("  NOTE: Items[0] and the IsBestMatch row are different objects. Consumers that read");
            details.Add("        BestMatch.Items[0] (e.g. ScrapingCacheService.CacheResult) would record the");
            details.Add($"        wrong entry: {firstItem.TitleOriginal ?? "(null)"} instead of {flaggedBest.TitleOriginal ?? "(null)"}.");
        }

        var best = flaggedBest ?? firstItem;
        if (best is null)
        {
            return Task.FromResult(CheckResult.Fail(Id, Title, expected,
                "BestMatch was set but carries no items").With(details.ToArray()));
        }

        var hasTitle = !string.IsNullOrWhiteSpace(best.TitleCn) || !string.IsNullOrWhiteSpace(best.TitleOriginal);
        var hasCover = !string.IsNullOrWhiteSpace(best.CoverImageUrl);

        details.Add(string.Empty);
        details.Add($"Best-match row title present      : {hasTitle}");
        details.Add($"Best-match row cover URL present  : {hasCover}");

        if (!hasTitle)
        {
            details.Add("FAIL REASON: the best-matching row has neither TitleCn nor TitleOriginal.");
            return Task.FromResult(CheckResult.Fail(Id, Title, expected,
                $"title=MISSING, coverUrl={(hasCover ? "present" : "MISSING")}").With(details.ToArray()));
        }

        if (!hasCover)
        {
            details.Add("FAIL REASON: the best-matching row has no cover image URL, so scraping cannot");
            details.Add("             produce a cover for this game even though it did match a row.");
            return Task.FromResult(CheckResult
                .Fail(Id, Title, expected, "title=present, coverUrl=MISSING")
                .With(details.ToArray()));
        }

        return Task.FromResult(CheckResult
            .Pass(Id, Title, expected, $"title={(best.TitleCn ?? best.TitleOriginal)}, coverUrl=present")
            .With(details.ToArray()));
    }

    private static string Describe(GameMetadata? item) =>
        item is null
            ? "(none)"
            : $"SourceId={item.SourceId}, TitleCn={item.TitleCn ?? "(null)"}, TitleOriginal={item.TitleOriginal ?? "(null)"}, MatchScore={item.MatchScore}";
}
