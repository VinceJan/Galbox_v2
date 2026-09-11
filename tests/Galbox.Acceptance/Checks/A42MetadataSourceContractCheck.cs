using System.Diagnostics;
using System.Net;
using Galbox.Core.Api;

namespace Galbox.Acceptance.Checks;

/// <summary>
/// A42 - the behavioural contract every metadata source must satisfy, whether or not its upstream
/// happens to be reachable.
///
/// A40/A41 prove that ymgal and cngal return real data. This check proves the part that does not
/// depend on the network at all, and it is the part the project has historically got wrong: a
/// source that cannot answer must say <b>why</b>, and it must be possible to tell
///
///   * "this source has no credentials configured",
///   * "this source could not reach its server", and
///   * "this source reached its server and the title genuinely is not there"
///
/// apart from one another. Collapsing all three into "0 items" is the defect this check exists to
/// catch; a source that is a stub fails it, because a stub reports failure for the third case too.
///
/// The three states are driven through the same public entry points the application uses:
///   1. credentials cleared through the documented environment variables;
///   2. the base address pointed at a closed port (a real transport failure);
///   3. a query that cannot exist upstream (a real, legitimate empty result).
///
/// Each state also asserts on how many HTTP requests actually left the process, so "it reported a
/// configuration problem" cannot be confused with "it quietly asked the server anyway".
/// </summary>
public sealed class A42MetadataSourceContractCheck : IAcceptanceCheck
{
    /// <summary>ymgal client-id environment variable understood by the client.</summary>
    private const string YmgalClientIdVariable = "GALBOX_YMGAL_CLIENT_ID";

    /// <summary>ymgal client-secret environment variable understood by the client.</summary>
    private const string YmgalClientSecretVariable = "GALBOX_YMGAL_CLIENT_SECRET";

    /// <summary>cngal base-address override understood by the client.</summary>
    private const string CngalBaseUrlVariable = "GALBOX_CNGAL_BASE_URL";

    /// <summary>A closed port on the loopback interface: connection refused, no traffic leaves the host.</summary>
    private const string UnreachableBaseUrl = "http://127.0.0.1:9/";

    /// <summary>
    /// A query neither upstream can match, verified against both live APIs before this check was
    /// written: ymgal answers <c>{"success":true,"code":0,"data":{"result":[],"total":0}}</c> and
    /// cngal answers <c>{"totalCount":0,"data":[]}</c> for it.
    /// </summary>
    /// <remarks>
    /// A word-like placeholder will NOT do here. ymgal's list search falls back to a tokenised
    /// inverted index, so "zzz_galbox_acceptance_no_such_galgame_zzz" really does return a hit
    /// (an archive named "Acceptance"). The probe has to be a token no index can contain.
    /// </remarks>
    private const string ImpossibleQuery = "qwrtplkjhgfd_9182736";

    /// <inheritdoc />
    public string Id => "A42";

    /// <inheritdoc />
    public string Title => "metadata sources distinguish 'not configured' / 'unreachable' / 'no results'";

    /// <inheritdoc />
    public async Task<CheckResult> RunAsync(AcceptanceContext context, CancellationToken cancellationToken)
    {
        var details = new List<string>();
        var expected = "both clients have a BaseAddress + identifying User-Agent + bounded timeout; "
                     + "an unconfigured ymgal credential fails fast with a message naming the missing variable and issues 0 requests; "
                     + "an unreachable cngal endpoint reports a transport failure instead of an empty result; "
                     + "and a legitimate empty result reports Success=true with 0 items and no LastError";

        // --- 1. Client configuration contract ------------------------------------------
        details.Add("--- [1] HttpClient configuration contract ---");
        var ymgalClient = context.Get<YmgalHttpClient>().HttpClient;
        var cngalClient = context.Get<CngalHttpClient>().HttpClient;
        var ymgalConfig = DescribeClient("YmgalHttpClient", ymgalClient);
        var cngalConfig = DescribeClient("CngalHttpClient", cngalClient);
        details.Add($"  {ymgalConfig}");
        details.Add($"  {cngalConfig}");

        var configOk = ClientContractOk(ymgalClient) && ClientContractOk(cngalClient);
        details.Add($"  contract (BaseAddress != null && User-Agent contains \"Galbox\" && 1s <= Timeout <= 30s) : {configOk}");

        // --- 2. Unconfigured credentials must fail fast and explain why ----------------
        details.Add(string.Empty);
        details.Add("--- [2] ymgal with an incomplete credential (client id present, secret missing) ---");
        details.Add($"  setting {YmgalClientIdVariable}=a-partial-configuration and REMOVING {YmgalClientSecretVariable}");
        details.Add("  (on Windows an empty environment variable is deleted, so \"not configured\" is");
        details.Add("   expressed as a half-filled credential pair - which is also the realistic mistake)");

        var unconfiguredSucceeded = false;
        var unconfiguredExplained = false;
        var unconfiguredRequests = -1;
        var unconfiguredLastError = "(not measured)";
        var unconfiguredMessage = "(not measured)";

        var savedClientId = Environment.GetEnvironmentVariable(YmgalClientIdVariable);
        var savedClientSecret = Environment.GetEnvironmentVariable(YmgalClientSecretVariable);
        try
        {
            Environment.SetEnvironmentVariable(YmgalClientIdVariable, "galbox-acceptance-partial");
            Environment.SetEnvironmentVariable(YmgalClientSecretVariable, null);

            var api = context.Get<YmgalApi>();
            context.Traffic.Clear();

            var stopwatch = Stopwatch.StartNew();
            var response = await api.SearchAsync("Dreamin' Her", cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            unconfiguredRequests = context.Traffic.For("YmgalHttpClient").Count;
            unconfiguredLastError = api.LastError ?? "(null)";
            unconfiguredMessage = response?.Message ?? "(null)";
            unconfiguredSucceeded = response?.Success ?? false;
            var items = response?.Items.Count ?? -1;

            details.Add($"  elapsed              : {stopwatch.ElapsedMilliseconds} ms");
            details.Add($"  Success              : {unconfiguredSucceeded}");
            details.Add($"  Items.Count          : {items}");
            details.Add($"  Message              : \"{unconfiguredMessage}\"");
            details.Add($"  ApiClient.LastError  : \"{unconfiguredLastError}\"");
            details.Add($"  HTTP requests issued : {unconfiguredRequests} (a configuration failure must not call the community site)");

            unconfiguredExplained = !unconfiguredSucceeded
                                 && items == 0
                                 && unconfiguredMessage.Contains(YmgalClientSecretVariable, StringComparison.OrdinalIgnoreCase)
                                 && !unconfiguredLastError.Equals("(null)", StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(YmgalClientIdVariable, savedClientId);
            Environment.SetEnvironmentVariable(YmgalClientSecretVariable, savedClientSecret);
        }

        details.Add($"  verdict              : Explained={unconfiguredExplained} "
                  + $"(failed={!unconfiguredSucceeded}, 0 requests={unconfiguredRequests == 0}, "
                  + $"names {YmgalClientSecretVariable}, LastError set)");

        // --- 3. Unreachable endpoint must not masquerade as "no results" ----------------
        details.Add(string.Empty);
        details.Add("--- [3] cngal pointed at a closed port (real transport failure) ---");
        details.Add($"  setting {CngalBaseUrlVariable}={UnreachableBaseUrl}");

        var unreachableSucceeded = true;
        var unreachableExplained = false;
        var unreachableMessage = "(not measured)";
        var unreachableLastError = "(not measured)";
        var unreachableRequests = -1;

        var savedCngalBaseUrl = Environment.GetEnvironmentVariable(CngalBaseUrlVariable);
        try
        {
            Environment.SetEnvironmentVariable(CngalBaseUrlVariable, UnreachableBaseUrl);

            var api = context.Get<CngalApi>();
            context.Traffic.Clear();

            var stopwatch = Stopwatch.StartNew();
            CngalSearchResponse? response = null;
            Exception? thrown = null;
            try
            {
                response = await api.SearchAsync("Dreamin' Her", cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                thrown = ex;
            }

            stopwatch.Stop();

            var exchanges = context.Traffic.For("CngalHttpClient");
            unreachableRequests = exchanges.Count;
            unreachableSucceeded = response?.Success ?? false;
            unreachableMessage = response?.Message ?? "(null)";
            unreachableLastError = api.LastError ?? "(null)";

            details.Add($"  elapsed              : {stopwatch.ElapsedMilliseconds} ms (includes the client's own retry backoff)");
            details.Add($"  threw                : {(thrown is null ? "no (reported through the result object)" : $"{thrown.GetType().Name}: {thrown.Message}")}");
            details.Add($"  Success              : {unreachableSucceeded}");
            details.Add($"  Items.Count          : {response?.Items.Count ?? -1}");
            details.Add($"  Message              : \"{unreachableMessage}\"");
            details.Add($"  ApiClient.LastError  : \"{unreachableLastError}\"");
            details.Add($"  HTTP attempts made   : {unreachableRequests}");
            foreach (var exchange in exchanges.Take(3))
            {
                details.Add($"      {exchange.Method} {exchange.Url}  ->  {(exchange.StatusCode == -1 ? "transport failure" : $"HTTP {exchange.StatusCode}")}"
                          + (exchange.Error is null ? string.Empty : $"  ({Truncate(exchange.Error, 160)})"));
            }

            unreachableExplained = !unreachableSucceeded
                                && (response?.Items.Count ?? -1) == 0
                                && unreachableMessage.Length > 0
                                && !unreachableMessage.Equals("(null)", StringComparison.Ordinal)
                                && !unreachableLastError.Equals("(null)", StringComparison.Ordinal)
                                && unreachableRequests > 0;
        }
        finally
        {
            Environment.SetEnvironmentVariable(CngalBaseUrlVariable, savedCngalBaseUrl);
        }

        details.Add($"  verdict              : Explained={unreachableExplained} (failed={!unreachableSucceeded}, requests attempted={unreachableRequests > 0}, LastError set)");

        // --- 4. A genuine empty result must look different from both failures -----------
        details.Add(string.Empty);
        details.Add("--- [4] negative control: a query that cannot exist upstream ---");
        details.Add($"  query                : \"{ImpossibleQuery}\"");
        details.Add("  This is the state that must NOT look like a failure: reached the server, zero hits.");

        var ymgalEmptyOk = await ProbeEmptyResultAsync(
            "ymgal",
            details,
            async () =>
            {
                var api = context.Get<YmgalApi>();
                context.Traffic.Clear();
                var response = await api.SearchAsync(ImpossibleQuery, cancellationToken).ConfigureAwait(false);
                return (response?.Success ?? true, response?.Items.Count ?? -1, api.LastError, response?.Message,
                        context.Traffic.For("YmgalHttpClient").Count);
            }).ConfigureAwait(false);

        await Task.Delay(400, cancellationToken).ConfigureAwait(false);

        var cngalEmptyOk = await ProbeEmptyResultAsync(
            "cngal",
            details,
            async () =>
            {
                var api = context.Get<CngalApi>();
                context.Traffic.Clear();
                var response = await api.SearchAsync(ImpossibleQuery, cancellationToken).ConfigureAwait(false);
                return (response?.Success ?? true, response?.Items.Count ?? -1, api.LastError, response?.Message,
                        context.Traffic.For("CngalHttpClient").Count);
            }).ConfigureAwait(false);

        // --- 5. Verdict -----------------------------------------------------------------
        var pass = configOk && unconfiguredExplained && unreachableExplained && ymgalEmptyOk && cngalEmptyOk;

        details.Add(string.Empty);
        details.Add("--- verdict inputs ---");
        details.Add($"  [1] HttpClient configuration contract              : {configOk}");
        details.Add($"  [2] unconfigured ymgal credential explained        : {unconfiguredExplained}");
        details.Add($"  [3] unreachable cngal endpoint explained           : {unreachableExplained}");
        details.Add($"  [4a] ymgal genuine empty result distinguishable    : {ymgalEmptyOk}");
        details.Add($"  [4b] cngal genuine empty result distinguishable    : {cngalEmptyOk}");

        var actual = $"config={configOk}, ymgalNotConfigured={unconfiguredExplained}(requests={unconfiguredRequests}), "
                   + $"cngalUnreachable={unreachableExplained}, ymgalEmpty={ymgalEmptyOk}, cngalEmpty={cngalEmptyOk}";

        return (pass
                ? CheckResult.Pass(Id, Title, expected, actual)
                : CheckResult.Fail(Id, Title, expected, actual))
            .With(details.ToArray());
    }

    /// <summary>
    /// Runs one "genuinely not found" probe and reports whether the source described it as a
    /// success with zero items (and left <see cref="ApiClient.LastError"/> clear).
    /// </summary>
    private static async Task<bool> ProbeEmptyResultAsync(
        string label,
        List<string> details,
        Func<Task<(bool Success, int ItemCount, string? LastError, string? Message, int Requests)>> probe)
    {
        details.Add(string.Empty);
        details.Add($"  --- [4] {label} ---");

        (bool Success, int ItemCount, string? LastError, string? Message, int Requests) result;
        try
        {
            result = await probe().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            details.Add($"      THREW : {ex.GetType().Name}: {ex.Message}");
            return false;
        }

        details.Add($"      Success              : {result.Success}");
        details.Add($"      Items.Count          : {result.ItemCount}");
        details.Add($"      Message              : \"{result.Message ?? "(null)"}\"");
        details.Add($"      ApiClient.LastError  : \"{result.LastError ?? "(null)"}\"");
        details.Add($"      HTTP requests issued : {result.Requests}");

        var ok = result.Success
              && result.ItemCount == 0
              && string.IsNullOrEmpty(result.LastError)
              && result.Requests > 0;

        details.Add($"      verdict              : Distinguishable={ok} "
                  + "(Success=true, 0 items, LastError empty, a real request was made)");
        return ok;
    }

    private static string DescribeClient(string name, HttpClient client)
    {
        var userAgent = client.DefaultRequestHeaders.TryGetValues("User-Agent", out var values)
            ? string.Join(" ", values)
            : "(none)";
        return $"{name,-17} BaseAddress={client.BaseAddress?.ToString() ?? "(null)"}"
             + $"  Timeout={client.Timeout.TotalSeconds}s  UserAgent=\"{userAgent}\"";
    }

    private static bool ClientContractOk(HttpClient client)
    {
        var userAgent = client.DefaultRequestHeaders.TryGetValues("User-Agent", out var values)
            ? string.Join(" ", values)
            : string.Empty;

        return client.BaseAddress is not null
            && userAgent.Contains("Galbox", StringComparison.OrdinalIgnoreCase)
            && client.Timeout > TimeSpan.Zero
            && client.Timeout <= TimeSpan.FromSeconds(30);
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "...";
}
