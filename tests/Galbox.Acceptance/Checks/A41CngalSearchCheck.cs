using System.Diagnostics;
using System.Net;
using Galbox.App.Services;
using Galbox.Core.Api;

namespace Galbox.Acceptance.Checks;

/// <summary>
/// A41 - cngal (CnGal 中文 Galgame 资料站) metadata source, driven through the real service.
///
/// Same reasoning as <see cref="A40YmgalSearchCheck"/>: the cngal client was a stub that returned
/// a failure message without ever touching the network. This check proves the difference by
/// observing the traffic on the client's own pipeline, not by reading the source.
///
/// It asserts that:
///   1. the container configured a base address and an identifying User-Agent;
///   2. a real request went to the documented search path and answered HTTP 200;
///   3. the payload binds into the model: <c>Success</c>, >= 1 item, and a title that matches the
///      query (CnGal's search is a fuzzy full-text index, so the match is asserted as a
///      containment test rather than an exact equality);
///   4. the entry-detail endpoint answers for the id the search returned.
/// </summary>
public sealed class A41CngalSearchCheck : IAcceptanceCheck
{
    /// <summary>Real, well-known Chinese visual novel used as the probe.</summary>
    private const string Query = "三色绘恋";

    /// <summary>Token every relevant cngal hit must contain.</summary>
    private const string QueryToken = "三色";

    /// <summary>Endpoint path documented by the CnGal main-site API.</summary>
    private const string ExpectedPathFragment = "/api/home/Search";

    /// <inheritdoc />
    public string Id => "A41";

    /// <inheritdoc />
    public string Title => "cngal source performs a real search and returns the real game";

    /// <inheritdoc />
    public async Task<CheckResult> RunAsync(AcceptanceContext context, CancellationToken cancellationToken)
    {
        var details = new List<string>();
        var expected = $"CngalApi.SearchAsync(\"{Query}\") issues a real request to {ExpectedPathFragment}, "
                     + "returns Success=true with >= 1 item whose title contains \"三色\", and "
                     + "GetGameAsync(id) returns that entry's detail";

        // --- 1. HttpClient configuration the container built ---------------------------
        var httpClient = context.Get<CngalHttpClient>().HttpClient;
        details.Add("--- [1] HttpClient configuration built by the container ---");
        details.Add($"  CngalHttpClient     BaseAddress={httpClient.BaseAddress?.ToString() ?? "(null)"}");
        details.Add($"                      Timeout={httpClient.Timeout.TotalSeconds}s");
        details.Add($"                      UserAgent={UserAgentOf(httpClient)}");
        var baseAddressConfigured = httpClient.BaseAddress is not null;
        var userAgentConfigured = !string.IsNullOrWhiteSpace(UserAgentOf(httpClient));

        // --- 2. Real search through the real client ------------------------------------
        var api = context.Get<CngalApi>();
        context.Traffic.Clear();

        details.Add(string.Empty);
        details.Add("--- [2] live search through the DI-resolved CngalApi ---");
        details.Add($"  query                : \"{Query}\"");

        var stopwatch = Stopwatch.StartNew();
        CngalSearchResponse? response = null;
        Exception? thrown = null;
        try
        {
            response = await api.SearchAsync(Query, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        stopwatch.Stop();

        var exchanges = context.Traffic.For("CngalHttpClient");
        details.Add($"  elapsed              : {stopwatch.ElapsedMilliseconds} ms");
        details.Add($"  HTTP requests issued : {exchanges.Count}");
        foreach (var exchange in exchanges)
        {
            details.Add($"      {exchange.Method} {exchange.Url}  ->  HTTP {exchange.StatusCode}"
                      + (exchange.Error is null ? string.Empty : $"  (transport error: {exchange.Error})"));
            details.Add($"        response body : {Truncate(exchange.ResponseBody, 700)}");
        }

        if (thrown is not null)
        {
            details.Add($"  THREW                : {thrown.GetType().Name}: {thrown.Message}");
        }

        details.Add($"  ApiClient.LastError  : {api.LastError ?? "(null)"}");
        details.Add($"  response             : {(response is null ? "(null)" : $"Success={response.Success}, Items={response.Items.Count}, Message=\"{response.Message}\"")}");

        var requestIssued = exchanges.Count > 0;
        var pathHit = exchanges.Any(e => e.Url.Contains(ExpectedPathFragment, StringComparison.OrdinalIgnoreCase));
        var httpOk = exchanges.Any(e => e.StatusCode == (int)HttpStatusCode.OK);
        var items = response?.Items ?? new List<CngalGameItem>();
        foreach (var item in items.Take(5))
        {
            details.Add($"      hit: id={item.Id} title=\"{item.Title}\" titleCn=\"{item.TitleCn}\" cover={(string.IsNullOrWhiteSpace(item.CoverUrl) ? "(none)" : "yes")}");
        }

        var titleMatched = items.Any(i => Contains(i.Title, QueryToken) || Contains(i.TitleCn, QueryToken));
        var searchOk = response is not null
                    && response.Success
                    && items.Count > 0
                    && titleMatched
                    && string.IsNullOrEmpty(api.LastError);

        // --- 3. Detail endpoint for the first hit --------------------------------------
        details.Add(string.Empty);
        details.Add("--- [3] detail endpoint through the DI-resolved CngalApi ---");

        string? detailId = null;
        CngalGameDetail? detail = null;
        var detailOk = false;

        if (items.Count > 0 && !string.IsNullOrWhiteSpace(items[0].Id))
        {
            detailId = items[0].Id;
            DetailProbe probe;
            try
            {
                context.Traffic.Clear();
                var task = api.GetGameAsync(detailId, cancellationToken);
                detail = await task.ConfigureAwait(false);
                probe = new DetailProbe(context.Traffic.For("CngalHttpClient").Count, null);
            }
            catch (Exception ex)
            {
                probe = new DetailProbe(context.Traffic.For("CngalHttpClient").Count, ex);
            }

            details.Add($"  GetGameAsync(\"{detailId}\") requests : {probe.RequestCount}");
            foreach (var exchange in context.Traffic.For("CngalHttpClient"))
            {
                details.Add($"      {exchange.Method} {exchange.Url}  ->  HTTP {exchange.StatusCode}");
            }

            if (probe.Error is not null)
            {
                details.Add($"  GetGameAsync THREW   : {probe.Error.GetType().Name}: {probe.Error.Message}");
            }

            details.Add($"  result               : {(detail is null ? "(null)" : $"id={detail.Id}, title=\"{detail.Title}\", titleCn=\"{detail.TitleCn}\", developer=\"{detail.Developer}\", releaseDate=\"{detail.ReleaseDate}\", tags={detail.Tags.Count}, characters={detail.Characters.Count}")}");
            details.Add($"  ApiClient.LastError  : {api.LastError ?? "(null)"}");

            detailOk = detail is not null
                    && probe.RequestCount > 0
                    && (Contains(detail.Title, QueryToken) || Contains(detail.TitleCn, QueryToken));
        }
        else
        {
            details.Add("  skipped: the search returned no usable id to look up.");
        }

        // --- 4. The application-level detail path --------------------------------------
        details.Add(string.Empty);
        details.Add("--- [4] GameScrapingService.GetGameDetailsAsync (the path the UI uses) ---");

        GameMetadata? appDetail = null;
        var appDetailOk = false;
        if (detailId is not null)
        {
            var scraper = context.Get<IGameScrapingService>();
            try
            {
                appDetail = await scraper.GetGameDetailsAsync(detailId, ScraperSource.Cngal, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                details.Add($"  THREW : {ex.GetType().Name}: {ex.Message}");
            }

            if (appDetail is null)
            {
                details.Add("  result               : (null) - the application path returned nothing for an id the API answered");
            }
            else
            {
                details.Add($"  result               : source={appDetail.Source}, id={appDetail.SourceId}, "
                          + $"titleOriginal=\"{appDetail.TitleOriginal}\", titleCn=\"{appDetail.TitleCn}\", "
                          + $"developer=\"{appDetail.Developer}\", "
                          + $"releaseDate={appDetail.ReleaseDate?.ToString("yyyy-MM-dd") ?? "(null)"}, "
                          + $"tags={appDetail.Tags.Count}, characters={appDetail.Characters.Count}");
                details.Add($"  titles               : {string.Join(" | ", appDetail.Titles)}");
            }

            appDetailOk = appDetail is not null
                       && appDetail.Source == ScraperSource.Cngal
                       && appDetail.Titles.Any(t => Contains(t, QueryToken));
        }
        else
        {
            details.Add("  skipped: the search returned no usable id to look up.");
        }

        // --- 5. Verdict -----------------------------------------------------------------
        var pass = baseAddressConfigured && userAgentConfigured && requestIssued && pathHit && httpOk && searchOk && detailOk && appDetailOk;

        details.Add(string.Empty);
        details.Add("--- verdict inputs ---");
        details.Add($"  BaseAddress configured by DI : {baseAddressConfigured}");
        details.Add($"  User-Agent configured by DI  : {userAgentConfigured}");
        details.Add($"  HTTP request issued          : {requestIssued} ({exchanges.Count} observed)");
        details.Add($"  documented path requested    : {pathHit} ({ExpectedPathFragment})");
        details.Add($"  search Success + title match : {searchOk} (success={response?.Success}, items={items.Count}, titleMatched={titleMatched}, lastError=\"{api.LastError ?? "(null)"}\")");
        details.Add($"  API detail lookup usable     : {detailOk} (id={detailId ?? "(none)"})");
        details.Add($"  service detail lookup usable : {appDetailOk}");

        var actual = $"baseAddress={baseAddressConfigured}, userAgent={userAgentConfigured}, requests={exchanges.Count}, "
                   + $"pathHit={pathHit}, searchOk={searchOk}, items={items.Count}, detailOk={detailOk}, appDetailOk={appDetailOk}";

        return (pass
                ? CheckResult.Pass(Id, Title, expected, actual)
                : CheckResult.Fail(Id, Title, expected, actual))
            .With(details.ToArray());
    }

    private static string UserAgentOf(HttpClient client) =>
        client.DefaultRequestHeaders.TryGetValues("User-Agent", out var values)
            ? string.Join(" ", values)
            : "(none)";

    private static bool Contains(string? haystack, string needle) =>
        !string.IsNullOrWhiteSpace(haystack)
        && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + $" ... [TRUNCATED, total {value.Length} chars]";

    private readonly record struct DetailProbe(int RequestCount, Exception? Error);
}
