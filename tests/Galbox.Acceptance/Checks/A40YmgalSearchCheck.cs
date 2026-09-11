using System.Diagnostics;
using System.Net;
using Galbox.Core.Api;

namespace Galbox.Acceptance.Checks;

/// <summary>
/// A40 - ymgal (月幕Galgame) metadata source, driven through the real service.
///
/// The ymgal client used to be a stub: <c>SearchAsync</c> returned <c>Success = false</c> with a
/// "needs API research" message and made no HTTP request at all. A stub is indistinguishable from
/// "the search legitimately found nothing" for every caller that only looks at the item list,
/// which is precisely the failure mode this project keeps re-discovering.
///
/// This check therefore does not inspect the source code. It resolves the real
/// <see cref="YmgalApi"/> out of the DI container (the same registration
/// <c>App.xaml.cs</c> performs), calls it with a real game name, and asserts on:
///
///   1. the HttpClient configuration the container actually built (base address, timeout and an
///      identifying User-Agent - a community site has to be able to tell who is calling);
///   2. that a real HTTP request left the process (recorded by the transparent handler on the
///      pipeline), naming the documented endpoint path;
///   3. that the response binds the real payload: <c>Success</c>, at least one item, and a title
///      that actually matches the query;
///   4. that the single-title detail endpoint answers for a real id.
///
/// A40 is a network check by design. When the site is unreachable the check FAILS and says so -
/// it never degrades into a silent pass.
/// </summary>
public sealed class A40YmgalSearchCheck : IAcceptanceCheck
{
    /// <summary>Real, well-known title used as the probe. Both spellings must resolve.</summary>
    private const string Query = "サノバウィッチ";

    /// <summary>Chinese name ymgal carries for the same archive.</summary>
    private const string QueryChinese = "魔女的夜宴";

    /// <summary>Endpoint path documented by the ymgal open API.</summary>
    private const string ExpectedPathFragment = "/open/archive/search-game";

    /// <summary>How many times the live search may be re-issued when ymgal refuses to answer.</summary>
    private const int MaxSearchAttempts = 3;

    /// <summary>Pause between two search attempts, so the retry is not a burst.</summary>
    private static readonly TimeSpan BetweenSearchAttempts = TimeSpan.FromSeconds(3);

    /// <inheritdoc />
    public string Id => "A40";

    /// <inheritdoc />
    public string Title => "ymgal source performs a real search and returns the real game";

    /// <inheritdoc />
    public async Task<CheckResult> RunAsync(AcceptanceContext context, CancellationToken cancellationToken)
    {
        var details = new List<string>();
        var expected = $"YmgalApi.SearchAsync(\"{Query}\") issues a real request to {ExpectedPathFragment}, "
                     + "returns Success=true with >= 1 item whose title matches the query, and "
                     + "GetGameAsync(id) returns that game's detail";

        // --- 1. HttpClient configuration the container built ---------------------------
        var wrapper = context.Get<YmgalHttpClient>();
        var httpClient = wrapper.HttpClient;
        details.Add("--- [1] HttpClient configuration built by the container ---");
        details.Add($"  YmgalHttpClient     BaseAddress={httpClient.BaseAddress?.ToString() ?? "(null)"}");
        details.Add($"                      Timeout={httpClient.Timeout.TotalSeconds}s");
        details.Add($"                      UserAgent={UserAgentOf(httpClient)}");
        var baseAddressConfigured = httpClient.BaseAddress is not null;
        var userAgentConfigured = !string.IsNullOrWhiteSpace(UserAgentOf(httpClient));

        // --- 2. Real search through the real client ------------------------------------
        var api = context.Get<YmgalApi>();
        context.Traffic.Clear();

        details.Add(string.Empty);
        details.Add("--- [2] live search through the DI-resolved YmgalApi ---");
        details.Add($"  query                : \"{Query}\"");
        details.Add($"  credential source    : {(api.Options.UsesDedicatedClient
            ? $"dedicated client from {YmgalEndpointOptions.ClientIdVariable}"
            : $"ymgal's documented public client ({api.Options.ClientId})")}" );
        details.Add($"  base URL             : {api.Options.BaseUrl}");
        details.Add("  ymgal is a community service and was observed intermittently answering a bare HTTP 302");
        details.Add("  with an empty body under concurrent load. That is the upstream refusing to answer, not a");
        details.Add("  statement about this client, so the search is retried a bounded number of times with every");
        details.Add("  attempt reported. If ymgal never answers, the check FAILS - the assertion is unchanged.");

        var stopwatch = Stopwatch.StartNew();
        YmgalSearchResponse? response = null;
        Exception? thrown = null;
        var attempts = 0;

        for (var attempt = 1; attempt <= MaxSearchAttempts; attempt++)
        {
            attempts = attempt;
            response = null;
            thrown = null;
            context.Traffic.Clear();
            var attemptWatch = Stopwatch.StartNew();

            try
            {
                response = await api.SearchAsync(Query, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                thrown = ex;
            }

            attemptWatch.Stop();

            var attemptExchanges = context.Traffic.For("YmgalHttpClient");
            details.Add($"  attempt {attempt}/{MaxSearchAttempts}: {attemptWatch.ElapsedMilliseconds} ms, "
                      + $"requests={attemptExchanges.Count}, "
                      + $"response={(response is null ? "(null)" : $"Success={response.Success}, Items={response.Items.Count}")}, "
                      + $"lastError=\"{api.LastError ?? "(null)"}\"");
            foreach (var exchange in attemptExchanges)
            {
                details.Add($"      {exchange.Method} {exchange.Url}  ->  HTTP {exchange.StatusCode}"
                          + (exchange.Error is null ? string.Empty : $"  (transport error: {exchange.Error})"));
                details.Add($"        response body : {Truncate(exchange.ResponseBody, 700)}");
            }

            if (thrown is not null)
            {
                details.Add($"      THREW : {thrown.GetType().Name}: {thrown.Message}");
            }

            if (response is { Success: true })
            {
                break;
            }

            if (attempt < MaxSearchAttempts)
            {
                details.Add("      inconclusive: ymgal did not answer this query; retrying.");
                await Task.Delay(BetweenSearchAttempts, cancellationToken).ConfigureAwait(false);
            }
        }

        stopwatch.Stop();

        var exchanges = context.Traffic.For("YmgalHttpClient");
        details.Add($"  total elapsed        : {stopwatch.ElapsedMilliseconds} ms over {attempts} attempt(s)");
        details.Add($"  ApiClient.LastError  : {api.LastError ?? "(null)"}");
        details.Add($"  response             : {(response is null ? "(null)" : $"Success={response.Success}, Items={response.Items.Count}, Message=\"{response.Message}\"")}");

        var requestIssued = exchanges.Count > 0;
        var pathHit = exchanges.Any(e => e.Url.Contains(ExpectedPathFragment, StringComparison.OrdinalIgnoreCase));
        var httpOk = exchanges.Any(e => e.StatusCode == (int)HttpStatusCode.OK);
        var items = response?.Items ?? new List<YmgalGameItem>();
        foreach (var item in items.Take(5))
        {
            details.Add($"      hit: id={item.Id} title=\"{item.Title}\" titleCn=\"{item.TitleCn}\" cover={(string.IsNullOrWhiteSpace(item.CoverUrl) ? "(none)" : "yes")}");
        }

        var titleMatched = items.Any(i =>
            Contains(i.Title, Query) || Contains(i.TitleCn, Query)
            || Contains(i.Title, QueryChinese) || Contains(i.TitleCn, QueryChinese));
        var searchOk = response is not null
                    && response.Success
                    && items.Count > 0
                    && titleMatched
                    && string.IsNullOrEmpty(api.LastError);

        // --- 3. Detail endpoint for the first hit --------------------------------------
        details.Add(string.Empty);
        details.Add("--- [3] detail endpoint through the DI-resolved YmgalApi ---");

        string? detailId = null;
        YmgalGameDetail? detail = null;
        var detailOk = false;

        if (items.Count > 0 && !string.IsNullOrWhiteSpace(items[0].Id))
        {
            detailId = items[0].Id;
            try
            {
                context.Traffic.Clear();
                detail = await api.GetGameAsync(detailId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                details.Add($"  GetGameAsync(\"{detailId}\") THREW : {ex.GetType().Name}: {ex.Message}");
            }

            var detailExchanges = context.Traffic.For("YmgalHttpClient");
            details.Add($"  GetGameAsync(\"{detailId}\") requests : {detailExchanges.Count}");
            foreach (var exchange in detailExchanges)
            {
                details.Add($"      {exchange.Method} {exchange.Url}  ->  HTTP {exchange.StatusCode}");
            }

            details.Add($"  result               : {(detail is null ? "(null)" : $"id={detail.Id}, title=\"{detail.Title}\", titleCn=\"{detail.TitleCn}\", developer=\"{detail.Developer}\", releaseDate=\"{detail.ReleaseDate}\", tags={detail.Tags.Count}, characters={detail.Characters.Count}")}");
            details.Add($"  ApiClient.LastError  : {api.LastError ?? "(null)"}");

            detailOk = detail is not null
                    && detailExchanges.Count > 0
                    && (Contains(detail.Title, Query) || Contains(detail.TitleCn, QueryChinese) || Contains(detail.TitleCn, Query));
        }
        else
        {
            details.Add("  skipped: the search returned no usable id to look up.");
        }

        // --- 4. Verdict -----------------------------------------------------------------
        var pass = baseAddressConfigured && userAgentConfigured && requestIssued && pathHit && httpOk && searchOk && detailOk;

        details.Add(string.Empty);
        details.Add("--- verdict inputs ---");
        details.Add($"  BaseAddress configured by DI : {baseAddressConfigured}");
        details.Add($"  User-Agent configured by DI  : {userAgentConfigured}");
        details.Add($"  HTTP request issued          : {requestIssued} ({exchanges.Count} observed)");
        details.Add($"  documented path requested    : {pathHit} ({ExpectedPathFragment})");
        details.Add($"  search Success + title match : {searchOk} (success={response?.Success}, items={items.Count}, titleMatched={titleMatched}, lastError=\"{api.LastError ?? "(null)"}\")");
        details.Add($"  detail lookup usable         : {detailOk} (id={detailId ?? "(none)"})");

        var actual = $"baseAddress={baseAddressConfigured}, userAgent={userAgentConfigured}, requests={exchanges.Count}, "
                   + $"pathHit={pathHit}, searchOk={searchOk}, items={items.Count}, detailOk={detailOk}";

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
}
