using System.Diagnostics;
using Galbox.App.Services;

namespace Galbox.Acceptance.Checks;

/// <summary>
/// A5 - live scraping query. Calls <see cref="IGameScrapingService.SearchGameAsync"/> with the
/// real game name and prints the raw, untrimmed result of every one of the four sources.
///
/// This is the check that answers "does scraping actually work". Two failure modes are
/// deliberately kept apart because they mean different things:
///   * <c>SourceScrapingResult.Success</c> is set by <c>SearchFromSourceAsync</c> from
///     <c>Items.Count &gt; 0</c>, so a source reports <c>Success == false</c> either WITH a
///     populated <c>ErrorMessage</c> (network/API broken) or WITHOUT one (API reachable,
///     zero hits).
///   * <c>ScrapingResult.BestMatch</c> only fills in when some source beats
///     <c>MatchScore &gt; 0</c>, so hits can exist while BestMatch stays null (matching broken).
///
/// The persistent cache is disabled by <see cref="AcceptanceContainer.DisableScrapingCache"/>,
/// so this is always a real network round trip and the user's real cache is untouched.
/// </summary>
public sealed class A5ScrapingSearchCheck : IAcceptanceCheck
{
    private static readonly ScraperSource[] AllSources =
    {
        ScraperSource.Bangumi, ScraperSource.Vndb, ScraperSource.Ymgal, ScraperSource.Cngal
    };

    /// <inheritdoc />
    public string Id => "A5";

    /// <inheritdoc />
    public string Title => "SearchGameAsync returns live results from the four sources";

    /// <inheritdoc />
    public async Task<CheckResult> RunAsync(AcceptanceContext context, CancellationToken cancellationToken)
    {
        var scraper = context.Get<IGameScrapingService>();
        var query = context.Options.GameName;
        var details = new List<string>();

        var expected = "at least one source returns >= 1 item AND ScrapingResult.BestMatch != null";
        details.Add($"Query string       : \"{query}\"");
        details.Add("Persistent cache   : DISABLED for this run (guarantees a live network query)");
        details.Add("HttpClient timeout : 30s per request, as configured by the container");

        // A7 consumes the traffic this call produces, so start from a clean slate.
        context.Traffic.Clear();

        var stopwatch = Stopwatch.StartNew();
        var result = await scraper.SearchGameAsync(query, cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();

        context.ScrapingQuery = result;
        context.ScrapingQueryText = query;

        details.Add($"Total elapsed      : {stopwatch.ElapsedMilliseconds} ms");
        details.Add($"SourceResults keys : {string.Join(", ", result.SourceResults.Keys)}");
        details.Add($"HasResults         : {result.HasResults}");
        details.Add($"BestMatch          : {(result.BestMatch is null
            ? "(null)"
            : $"{result.BestMatch.Source} ({result.BestMatch.Items.Count} items)")}");
        details.Add($"Result.Errors      : {result.Errors.Count}");
        foreach (var error in result.Errors)
        {
            details.Add($"  error: {error}");
        }

        var totalItems = 0;
        foreach (var source in AllSources)
        {
            totalItems += RecordSource(details, result, source);
        }

        // --- Verdict -------------------------------------------------------------------
        var anyResults = result.HasResults;
        var hasBestMatch = result.BestMatch is not null;

        if (!anyResults)
        {
            details.Add("FAIL REASON: no source returned a single item.");
            return CheckResult.Fail(Id, Title, expected,
                $"HasResults=false, BestMatch={(hasBestMatch ? "set" : "null")}, total items=0")
                .With(details.ToArray());
        }

        if (!hasBestMatch)
        {
            details.Add("FAIL REASON: sources returned items but BestMatch is null - nothing scored above 0.");
            return CheckResult.Fail(Id, Title, expected,
                $"HasResults=true, BestMatch=null, total items={totalItems}")
                .With(details.ToArray());
        }

        return CheckResult.Pass(Id, Title, expected,
            $"HasResults=true, BestMatch={result.BestMatch!.Source}, total items={totalItems}")
            .With(details.ToArray());
    }

    /// <summary>Prints one source's raw result and returns how many items it contributed.</summary>
    private static int RecordSource(List<string> details, ScrapingResult result, ScraperSource source)
    {
        details.Add(string.Empty);
        details.Add($"================ SOURCE: {source} ================");

        if (!result.SourceResults.TryGetValue(source, out var sourceResult))
        {
            details.Add("  NOT PRESENT in SourceResults (the source was never queried)");
            return 0;
        }

        details.Add($"  SearchQuery         : \"{sourceResult.SearchQuery}\"");
        details.Add($"  Success             : {sourceResult.Success}");
        details.Add($"  ErrorMessage        : {sourceResult.ErrorMessage ?? "(null)"}");
        details.Add($"  ElapsedMilliseconds : {sourceResult.ElapsedMilliseconds}");
        details.Add($"  Items               : {sourceResult.Items.Count}");
        details.Add($"  ExtendedErrorInfo   : {(sourceResult.ExtendedErrorInfo is null ? "(null)" : "printed below, raw")}");

        if (sourceResult.ExtendedErrorInfo is not null)
        {
            // Kept raw so the report is auditable; capped only to avoid a wall of text.
            const int cap = 4000;
            var text = sourceResult.ExtendedErrorInfo.Length > cap
                ? sourceResult.ExtendedErrorInfo[..cap]
                  + $" ... [TRUNCATED, total {sourceResult.ExtendedErrorInfo.Length} chars]"
                : sourceResult.ExtendedErrorInfo;

            foreach (var line in text.Split('\n'))
            {
                details.Add($"    {line.TrimEnd('\r')}");
            }
        }

        if (sourceResult.Items.Count == 0)
        {
            details.Add("  (no items)");
            return 0;
        }

        details.Add("  Items (raw, in returned order):");
        for (var i = 0; i < sourceResult.Items.Count; i++)
        {
            var item = sourceResult.Items[i];
            details.Add($"    [{i}] IsBestMatch={item.IsBestMatch} MatchScore={item.MatchScore}");
            details.Add($"        SourceId      : {item.SourceId}");
            details.Add($"        TitleCn       : {item.TitleCn ?? "(null)"}");
            details.Add($"        TitleOriginal : {item.TitleOriginal ?? "(null)"}");
            details.Add($"        Titles        : [{string.Join(" | ", item.Titles.Select(t => $"\"{t}\""))}]");
            details.Add($"        Rating        : {(item.Rating.HasValue ? item.Rating.Value.ToString("F2") : "(null)")}");
            details.Add($"        CoverImageUrl : {item.CoverImageUrl ?? "(null)"}");
            details.Add($"        ReleaseDate   : {(item.ReleaseDate.HasValue ? item.ReleaseDate.Value.ToString("yyyy-MM-dd") : "(null)")}");
            details.Add($"        Developer     : {item.Developer ?? "(null)"}");
            details.Add($"        Description   : {(item.Description is null ? "(null)" : Truncate(item.Description, 160))}");
        }

        return sourceResult.Items.Count;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "...";
}
