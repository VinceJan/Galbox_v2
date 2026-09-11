using System.Net;
using System.Text;
using System.Text.Json;
using Galbox.Core.Api;

namespace Galbox.Acceptance.Checks;

/// <summary>
/// A7 - upstream API contract probe.
///
/// A5 tells us that the *application's* scraping path returns nothing. That alone cannot say
/// whether the world changed or the client is wrong. This check answers that by separating the
/// two sides:
///
///   1. it dumps the HttpClient configuration the DI container actually built, which shows
///      which sources even have a base address;
///   2. it prints the traffic the application's own API clients produced during A5, captured by
///      a transparent recording handler on each pipeline. That is the real request shape
///      Galbox sends right now - no copied literals that go stale when a client is fixed -
///      together with the HTTP status and the top-level JSON keys the server really returned;
///   3. it issues a corrected request to the same upstream services, with no Galbox code in the
///      path, to establish whether the metadata for this game exists upstream at all.
///
/// PASS means: both upstream services are reachable AND both return at least one hit for this
/// game when asked in a shape they accept. Combined with A5, a PASS here and a FAIL there is
/// proof that the defect is in Galbox, not in the network or the upstream data.
/// </summary>
public sealed class A7UpstreamContractCheck : IAcceptanceCheck
{
    private const string UserAgent = "Galbox/1.0";

    private static readonly string[] SourceClients =
    {
        "BangumiHttpClient", "VndbHttpClient", "YmgalHttpClient", "CngalHttpClient"
    };

    /// <inheritdoc />
    public string Id => "A7";

    /// <inheritdoc />
    public string Title => "Upstream APIs are reachable and hold this game (app-shaped vs corrected request)";

    /// <inheritdoc />
    public async Task<CheckResult> RunAsync(AcceptanceContext context, CancellationToken cancellationToken)
    {
        var query = context.Options.GameName;
        var details = new List<string>();
        var expected = "Bangumi and VNDB are both reachable AND both return >= 1 hit for this game "
                     + "when asked with a request shape they accept";

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.Add("User-Agent", UserAgent);

        // --- 1. What the container actually configured --------------------------------
        details.Add("--- HttpClient configuration built by the container ---");
        RecordClientConfig(details, "BangumiHttpClient", context.Get<BangumiHttpClient>().HttpClient);
        RecordClientConfig(details, "VndbHttpClient", context.Get<VndbHttpClient>().HttpClient);
        RecordClientConfig(details, "YmgalHttpClient", context.Get<YmgalHttpClient>().HttpClient);
        RecordClientConfig(details, "CngalHttpClient", context.Get<CngalHttpClient>().HttpClient);

        // --- 2. What the application's own clients actually sent during A5 -------------
        details.Add(string.Empty);
        details.Add("=== [2] traffic recorded from the application's own HttpClient pipelines ===");
        details.Add("    (captured by a transparent recording handler appended to each pipeline)");

        var recordedBodiesSeen = 0;
        foreach (var clientName in SourceClients)
        {
            var exchanges = context.Traffic.For(clientName);

            details.Add(string.Empty);
            details.Add($"  --- {clientName}: {exchanges.Count} HTTP exchange(s) ---");
            if (exchanges.Count == 0)
            {
                details.Add("      NO HTTP REQUEST WAS MADE by this client during A5.");
                details.Add("      A source that reports 0 items without issuing a request is a stub,");
                details.Add("      not a search that found nothing.");
                continue;
            }

            foreach (var exchange in exchanges)
            {
                details.Add($"      {exchange.Method} {exchange.Url}  ->  HTTP {exchange.StatusCode}"
                          + (exchange.Error is null ? string.Empty : $"  (transport error: {exchange.Error})"));
                if (exchange.RequestBody is not null)
                {
                    recordedBodiesSeen++;
                    details.Add($"        request body : {Truncate(exchange.RequestBody, 700)}");
                }

                var keys = TopLevelKeys(exchange.ResponseBody);
                details.Add($"        response top-level JSON keys : [{string.Join(", ", keys)}]");
                details.Add($"        response body : {Truncate(exchange.ResponseBody, 700)}");
            }
        }

        // The Bangumi model binds data/total/limit/offset; a response with neither data nor
        // total cannot be read by it, which is exactly the silent-zero-results failure mode.
        var bangumiExchanges = context.Traffic.For("BangumiHttpClient");
        var bangumiShapeReadable = bangumiExchanges.Count == 0
            ? (bool?)null
            : bangumiExchanges.Any(e => TopLevelKeys(e.ResponseBody).Contains("data"));
        var vndbExchanges = context.Traffic.For("VndbHttpClient");
        var vndbAppStatus = vndbExchanges.Count == 0 ? (int?)null : vndbExchanges[0].StatusCode;

        var ymgalRequestCount = context.Traffic.For("YmgalHttpClient").Count;
        var cngalRequestCount = context.Traffic.For("CngalHttpClient").Count;

        details.Add(string.Empty);
        details.Add("  --- interpretation ---");
        details.Add($"  BangumiResponse readable by BangumiSearchResponse : {bangumiShapeReadable?.ToString() ?? "(no request recorded)"}"
                  + "  (model binds [total, limit, offset, data])");
        details.Add($"  VNDB app-shaped HTTP status                       : {vndbAppStatus?.ToString() ?? "(no request recorded)"}"
                  + "  (a non-200 is an app-side request bug)");
        details.Add($"  ymgal requests issued during A5                   : {ymgalRequestCount}");
        details.Add($"  cngal requests issued during A5                   : {cngalRequestCount}");

        // --- 3. Corrected requests: does the data exist upstream? ---------------------
        details.Add(string.Empty);
        details.Add("=== [3a] Bangumi, corrected request (v0 search API, matches the app's own model) ===");
        var bangumiFixedBody = $"{{\"keyword\":{JsonSerializer.Serialize(query)},\"filter\":{{\"type\":[4]}}}}";
        details.Add($"  POST https://api.bgm.tv/v0/search/subjects");
        details.Add($"  body: {bangumiFixedBody}");
        var bangumiFixed = await SendAsync(http, HttpMethod.Post, "https://api.bgm.tv/v0/search/subjects",
            bangumiFixedBody, cancellationToken).ConfigureAwait(false);
        RecordProbe(details, bangumiFixed);
        var bangumiTotal = ReadInt(bangumiFixed.Body, "total");
        details.Add($"  total reported by Bangumi : {bangumiTotal?.ToString() ?? "(unreadable)"}");
        RecordFirstArrayElement(details, bangumiFixed.Body, "data",
            new[] { "id", "name", "name_cn" });

        details.Add(string.Empty);
        details.Add("=== [3b] VNDB, corrected request (array filter + titles{...} sub-fields) ===");
        var vndbFixedBody = "{\"filters\":[\"search\",\"=\"," + JsonSerializer.Serialize(query) + "],"
                          + "\"fields\":\"id, title, titles{lang,title,official,main}, image.url, rating, length_minutes\","
                          + "\"results\":10}";
        details.Add($"  POST https://api.vndb.org/kana/vn");
        details.Add($"  body: {vndbFixedBody}");
        var vndbFixed = await SendAsync(http, HttpMethod.Post, "https://api.vndb.org/kana/vn", vndbFixedBody, cancellationToken)
            .ConfigureAwait(false);
        RecordProbe(details, vndbFixed);
        // The kana API answers with { more, results } and no "count"; the app's VndbSearchResponse
        // does declare Count, so both are read and reported separately.
        var vndbCount = ReadInt(vndbFixed.Body, "count");
        var vndbResults = ReadArrayLength(vndbFixed.Body, "results");
        details.Add($"  count reported by VNDB    : {vndbCount?.ToString() ?? "(absent - kana API returns 'more'/'results' only)"}");
        details.Add($"  results[] length          : {vndbResults?.ToString() ?? "(unreadable)"}");
        RecordFirstArrayElement(details, vndbFixed.Body, "results",
            new[] { "id", "title", "rating" });
        var vndbHits = vndbResults ?? 0;

        // --- 4. Verdict ---------------------------------------------------------------
        var bangumiReachable = bangumiExchanges.Any(e => e.StatusCode == (int)HttpStatusCode.OK)
                            || bangumiFixed.StatusCode == (int)HttpStatusCode.OK;
        var vndbReachable = vndbFixed.StatusCode == (int)HttpStatusCode.OK;
        var bangumiHasGame = bangumiTotal.GetValueOrDefault() > 0;
        var vndbHasGame = vndbHits > 0;

        details.Add(string.Empty);
        details.Add("--- verdict inputs ---");
        details.Add($"  Bangumi reachable        : {bangumiReachable} (app traffic HTTP "
                  + $"[{string.Join(", ", bangumiExchanges.Select(e => e.StatusCode))}], corrected HTTP {bangumiFixed.StatusCode})");
        details.Add($"  Bangumi holds this game  : {bangumiHasGame} (total={bangumiTotal?.ToString() ?? "?"})");
        details.Add($"  VNDB reachable           : {vndbReachable} (corrected HTTP {vndbFixed.StatusCode})");
        details.Add($"  VNDB holds this game     : {vndbHasGame} (results[]={vndbHits})");

        var pass = bangumiReachable && vndbReachable && bangumiHasGame && vndbHasGame;
        var actual = $"bangumiReachable={bangumiReachable}, bangumiHasGame={bangumiHasGame}, "
                   + $"vndbReachable={vndbReachable}, vndbHasGame={vndbHasGame}";

        return (pass
                ? CheckResult.Pass(Id, Title, expected, actual)
                : CheckResult.Fail(Id, Title, expected, actual))
            .With(details.ToArray());
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + $" ... [TRUNCATED, total {value.Length} chars]";

    private static void RecordClientConfig(List<string> details, string name, HttpClient client)
    {
        details.Add($"  {name,-18} BaseAddress={client.BaseAddress?.ToString() ?? "(null)"}"
                  + $"  Timeout={client.Timeout.TotalSeconds}s"
                  + $"  UserAgent={string.Join(" ", client.DefaultRequestHeaders.GetValues("User-Agent"))}");
    }

    private static void RecordProbe(List<string> details, HttpProbe probe)
    {
        details.Add($"  HTTP status : {probe.StatusCode} {(probe.Failed ? "(transport failure)" : string.Empty)}");
        if (probe.Error is not null)
        {
            details.Add($"  transport error: {probe.Error}");
        }

        var body = probe.Body.Length > 900 ? probe.Body[..900] + $" ... [TRUNCATED, total {probe.Body.Length} chars]" : probe.Body;
        details.Add($"  body        : {body}");
    }

    private static void RecordFirstArrayElement(
        List<string> details,
        string body,
        string arrayProperty,
        string[] fields)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty(arrayProperty, out var array)
                || array.ValueKind != JsonValueKind.Array
                || array.GetArrayLength() == 0)
            {
                details.Add($"  first '{arrayProperty}[0]' : (none)");
                return;
            }

            var first = array[0];
            var parts = new List<string>();
            foreach (var field in fields)
            {
                parts.Add($"{field}={ValueText(first, field)}");
            }

            details.Add($"  first '{arrayProperty}[0]' : {string.Join(", ", parts)}");
        }
        catch (Exception ex)
        {
            details.Add($"  first '{arrayProperty}[0]' : (unparseable: {ex.Message})");
        }
    }

    private static string ValueText(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? "(null)" : value.ToString()
            : "(absent)";

    private static List<string> TopLevelKeys(string json)
    {
        var keys = new List<string>();
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    keys.Add(property.Name);
                }
            }
            else
            {
                keys.Add($"<{document.RootElement.ValueKind}>");
            }
        }
        catch
        {
            keys.Add("<unparseable>");
        }

        return keys;
    }

    private static int? ReadInt(string json, string property)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty(property, out var value)
                && value.ValueKind == JsonValueKind.Number)
            {
                return value.GetInt32();
            }
        }
        catch
        {
            // Reported as unreadable by the caller.
        }

        return null;
    }

    private static int? ReadArrayLength(string json, string property)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty(property, out var value)
                && value.ValueKind == JsonValueKind.Array)
            {
                return value.GetArrayLength();
            }
        }
        catch
        {
            // Reported as unreadable by the caller.
        }

        return null;
    }

    private static async Task<HttpProbe> SendAsync(
        HttpClient http,
        HttpMethod method,
        string url,
        string? jsonBody,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(method, url);
            if (jsonBody is not null)
            {
                request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            }

            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return new HttpProbe((int)response.StatusCode, body, false, null);
        }
        catch (Exception ex)
        {
            return new HttpProbe(-1, string.Empty, true, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private readonly record struct HttpProbe(int StatusCode, string Body, bool Failed, string? Error);
}
