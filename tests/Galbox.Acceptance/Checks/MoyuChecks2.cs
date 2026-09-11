using System.Diagnostics;
using System.Text;
using Galbox.Core.Api;
using Galbox.Core.Patches;

namespace Galbox.Acceptance.Checks;

/// <summary>
/// A63 - the compliance guard, asserted three ways.
///
/// <para>
/// <c>https://www.moyu.moe/robots.txt</c> contains <c>Disallow: /api</c>, and every usable
/// first-party data endpoint on that site lives under <c>/api/v1/*</c>. The research report proved
/// those endpoints answer <c>200</c> anonymously; that is precisely why this check exists rather
/// than relying on the endpoint being unreachable. The promise "Galbox never calls /api/v1" is
/// turned into an assertion here:
/// </para>
/// <list type="number">
/// <item><description>the URL guard refuses every <c>/api</c> shape, including an uppercase variant
/// and the right host with the wrong path;</description></item>
/// <item><description>a real client, driven with a real key and a recording transport, produces
/// only <c>/v2/moyu/</c> request lines — so the guard is on the actual code path, not just
/// callable;</description></item>
/// <item><description>every moyu request the application's own pipelines recorded during the whole
/// run stays inside the allow-list.</description></item>
/// </list>
/// </summary>
public sealed class A63MoyuComplianceGuardCheck : IAcceptanceCheck
{
    /// <inheritdoc />
    public string Id => "A63";

    /// <inheritdoc />
    public string Title => "No request path under /api is reachable (robots.txt Disallow: /api)";

    /// <inheritdoc />
    public async Task<CheckResult> RunAsync(AcceptanceContext context, CancellationToken cancellationToken)
    {
        var expected =
            "every /api path is refused and every /v2/moyu path accepted; a driven client issues "
            + "only /v2/moyu requests; no recorded moyu request targets /api";

        var details = new List<string>();
        var problems = new List<string>();

        // --- 1. The guard matrix -----------------------------------------------------------
        var mustReject = new[]
        {
            "https://www.moyu.moe/api/v1/search?keywords=CLANNAD&type=resource&page=1&limit=3",
            "https://www.moyu.moe/api/v1/patch/86/resource",
            "https://www.moyu.moe/api/v1/patch/resource/223/link",
            "https://www.moyu.moe/api/v1/galgame?page=1",
            "https://www.moyu.moe/API/V1/search",
            "https://api.nextmoe.dev/api/v1/moyu/patches",
            "https://api.nextmoe.dev/v1/moyu/patches",
            "http://api.nextmoe.dev/v2/moyu/patches",
            "https://evil.example/v2/moyu/patches"
        };

        var mustAccept = new[]
        {
            "https://api.nextmoe.dev/v2/moyu/patches?refs=vndb%3Av4",
            "https://api.nextmoe.dev/v2/moyu/patches/86?include=resources",
            "https://api.nextmoe.dev/v2/moyu/patches/86/resources?limit=50",
            "https://api.nextmoe.dev/v2/moyu/resources/6262"
        };

        details.Add("URL guard matrix:");
        foreach (var url in mustReject)
        {
            var allowed = MoyuComplianceGuard.IsAllowedApiUri(new Uri(url));
            details.Add($"   {(allowed ? "ALLOWED (wrong)" : "rejected"),-16} {url}");
            if (allowed)
            {
                problems.Add($"the guard allows the forbidden URL {url}");
            }
        }

        foreach (var url in mustAccept)
        {
            var allowed = MoyuComplianceGuard.IsAllowedApiUri(new Uri(url));
            details.Add($"   {(allowed ? "accepted" : "REJECTED (wrong)"),-16} {url}");
            if (!allowed)
            {
                problems.Add($"the guard wrongly rejects the legitimate URL {url}");
            }
        }

        // BuildRequest must be the path a URI takes to the wire. If it ever lets something through,
        // it must throw rather than return an off-surface URI.
        var escaped = false;
        try
        {
            MoyuComplianceGuard.EnsureApiUri(
                MoyuOptions.DefaultBaseAddress,
                "/api/v1/patch/resource/223/link");
            escaped = true;
        }
        catch (InvalidOperationException)
        {
            // Expected.
        }

        details.Add(string.Empty);
        details.Add($"EnsureApiUri('/api/v1/…') threw instead of returning a URI: {!escaped}");
        if (escaped)
        {
            problems.Add("EnsureApiUri returned a forbidden URI instead of refusing to build it");
        }

        // --- 2. Drive a real client and inspect the request lines it produces --------------
        var handler = new MoyuStubHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = path.EndsWith("/resources", StringComparison.Ordinal)
                ? """{"object":"list","items":[{"object":"patch_resource","id":"6262","patch_id":"86","name":"Key Fans Club 汉化","storage":"s3","size":"17.953 MB","hash":"690eac","model_name":"","localization_group_name":"Key Fans Club","note":"","type":["manual"],"language":["zh-Hans"],"platform":["windows"],"download_count":122,"like_count":0,"web_url":"https://www.moyu.moe/patch/86/resources/6262","created_at":"2024-12-13T05:25:54.167Z","updated_at":"2025-11-11T08:31:33.206Z"}],"next_cursor":null,"total":1}"""
                : """{"object":"list","items":[{"object":"patch","id":"86","vndb_id":"v4","catalog_work_id":"86","content_limit":"sfw","release_date":"2004-04-28","type":["manual"],"language":["zh-Hans"],"platform":["windows"],"resource_count":2,"download_count":282,"view_count":0,"favorite_count":0,"comment_count":0,"web_url":"https://www.moyu.moe/patch/86/introduction","created_at":"2024-12-13T05:25:54.167Z","updated_at":"2026-05-27T18:41:40.338Z","resource_updated_at":"2024-12-13T05:25:54.167Z","resources":[{"object":"patch_resource","id":"6262","patch_id":"86","name":"Key Fans Club 汉化","storage":"s3","size":"17.953 MB","hash":"690eac","model_name":"","localization_group_name":"Key Fans Club","note":"","type":["manual"],"language":["zh-Hans"],"platform":["windows"],"download_count":122,"like_count":0,"web_url":"https://www.moyu.moe/patch/86/resources/6262","created_at":"2024-12-13T05:25:54.167Z","updated_at":"2025-11-11T08:31:33.206Z"}]}],"next_cursor":null,"total":1,"missing":[]}""";

            return MoyuStubHandler.Json(body);
        });

        var api = MoyuStubHandler.BuildApi(handler, apiKey: "nmk_live_A63probe_000000000000000000");

        await api.FindPatchesAsync(new[] { MoyuRef.Parse("vndb:v4")! }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        await api.GetPatchAsync("86", includeResources: true, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        await api.ListResourcesAsync("86", limit: 50, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        await api.GetResourceAsync("6262", cancellationToken).ConfigureAwait(false);

        details.Add(string.Empty);
        details.Add($"Requests a driven client produced ({handler.Requests.Count}):");
        foreach (var (method, uri) in handler.Requests)
        {
            var verdict = MoyuComplianceGuard.IsAllowedApiUri(uri) ? "allowed" : "OUTSIDE ALLOW-LIST";
            details.Add($"   {method,-4} {uri.PathAndQuery}");
            details.Add($"        host={uri.Host}  -> {verdict}");

            if (!MoyuComplianceGuard.IsAllowedApiUri(uri))
            {
                problems.Add($"the client sent a request outside the allow-list: {method} {uri}");
            }
        }

        if (handler.Requests.Count == 0)
        {
            problems.Add("the driven client sent no request at all, so this check measured nothing");
        }

        // --- 3. Everything the application's own pipelines recorded ------------------------
        var observed = context.Traffic.Exchanges
            .Where(e => e.Url.Contains("moyu", StringComparison.OrdinalIgnoreCase)
                        || e.Url.Contains("nextmoe.dev", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        details.Add(string.Empty);
        details.Add($"Moyu/nextmoe requests recorded on the application's own pipelines: {observed.Length}");
        foreach (var exchange in observed)
        {
            details.Add($"   [{exchange.ClientName}] {exchange.Method} {exchange.Url} -> {exchange.StatusCode}");

            if (!Uri.TryCreate(exchange.Url, UriKind.Absolute, out var uri)
                || !MoyuComplianceGuard.IsAllowedApiUri(uri))
            {
                problems.Add($"a real request left the allow-listed surface: {exchange.Method} {exchange.Url}");
            }
        }

        // --- Verdict -----------------------------------------------------------------------
        foreach (var problem in problems)
        {
            details.Add($"PROBLEM: {problem}");
        }

        if (problems.Count > 0)
        {
            return CheckResult.Fail(Id, Title, expected,
                $"{problems.Count} compliance violation(s): {string.Join("; ", problems)}")
                .With(details.ToArray());
        }

        return CheckResult.Pass(Id, Title, expected,
            $"{mustReject.Length}/{mustReject.Length} forbidden paths refused, "
            + $"{mustAccept.Length}/{mustAccept.Length} legitimate paths accepted, "
            + $"{handler.Requests.Count}/{handler.Requests.Count} driven requests inside the allow-list, "
            + $"{observed.Length} recorded pipeline request(s) inside the allow-list")
            .With(details.ToArray());
    }
}

/// <summary>
/// A64 - the two pure parsers the online layer depends on: the binary-unit <c>size</c> string and
/// the game anchor.
///
/// <para>
/// <b>What the research report calibrated, and what this check measured.</b> Report §4.2 fixed the
/// <i>unit</i> exactly: the API says <c>"17.953 MB"</c> for a resource whose Range response reports
/// <c>content-range: bytes 0-2047/18825520</c>, and <c>18825520 / 1048576 = 17.953110</c>, so one MB
/// is 1048576 bytes rather than 10^6. That part is settled and is asserted as an exact conversion.
/// </para>
///
/// <para>
/// What is <b>not</b> settled is how many bytes the string represents. This check measured the gap
/// on the one sample where a byte count is independently known:
/// <c>"17.953 MB" -&gt; 18825085</c> against a true 18825520, a 435-byte shortfall. Three decimals of
/// a binary megabyte is 1049 bytes wide, so the upstream field is a <b>3-decimal display string</b>
/// and its 3rd digit does not agree with a true division of the byte count
/// (<c>18825520 / 1048576 = 17.953110</c> would round to <c>17.953</c>, and the site still stores the
/// byte count separately). The conclusion the integration acts on is therefore narrow and defensible:
/// <b>the upstream <c>size</c> is not an exact identity and must not be compared for equality.</b>
/// <see cref="MoyuDownloadWatcher"/> already allows a tolerance for exactly this reason, and A65
/// asserts that a wrong-sized file is refused.
/// </para>
///
/// <para>
/// This check therefore asserts the exact cases exactly, and the calibrated sample as "1 MB of
/// conversion plus at most 0.001 MB of display rounding" — no more. It deliberately does not repeat
/// the site's rounding convention for a second sample, because a second data point would have to be
/// measured against the same file rather than assumed.
/// </para>
/// </summary>
public sealed class A64MoyuSizeAndAnchorCheck : IAcceptanceCheck
{
    /// <summary>
    /// Half a unit in the last place of a 3-decimal MB string (0.0005 * 1048576 = 524.3 bytes),
    /// rounded up by one byte. Beyond this the string and the byte count disagree by more than the
    /// display format can explain.
    /// </summary>
    private const long DisplayRoundingSlackBytes = 525;

    /// <inheritdoc />
    public string Id => "A64";

    /// <inheritdoc />
    public string Title => "size strings parse as binary units and game anchors accept only vndb:/catalog:";

    /// <inheritdoc />
    public Task<CheckResult> RunAsync(AcceptanceContext context, CancellationToken cancellationToken)
    {
        var expected =
            "the calibrated sample \"17.953 MB\" parses to 18825520 within 0.001 MB display-rounding "
            + "slack; exact samples are bit-exact; junk returns 0 without throwing; anchors accept "
            + "vndb:/catalog: and refuse a bare number, a Bangumi id and whitespace-padded forms";

        var details = new List<string>();
        var problems = new List<string>();

        // --- Exact samples: values the binary units can represent precisely ------------------
        var exact = new (string Raw, long Want)[]
        {
            ("1.5 GB", 1610612736L),
            ("512 B", 512L),
            ("2 KB", 2048L),
            ("0.5 MB", 524288L),
            ("1 TB", 1099511627776L),
            ("3 GB", 3221225472L),
            ("1.5 MB", 1572864L)
        };

        details.Add("Exact expectations (values the binary units represent bit-exactly):");
        foreach (var (raw, want) in exact)
        {
            var got = MoyuSize.ParseBytes(raw);
            var ok = got == want;
            details.Add($"   size \"{raw}\" -> {got} (want {want}) {(ok ? "OK" : "<-- MISMATCH")}");
            if (!ok)
            {
                problems.Add($"parsing \"{raw}\" gave {got}, expected {want}");
            }
        }

        // --- The one calibrated real-world sample -------------------------------------------
        // Reference: 18825520 bytes, from the content-range header of the same resource.
        const long calibratedBytes = 18825520L;
        var calibratedValue = MoyuSize.ParseBytes("17.953 MB");
        var calibratedDelta = calibratedBytes - calibratedValue;

        details.Add(string.Empty);
        details.Add($"Calibrated sample (the only one with an independently known byte count):");
        details.Add($"   size \"17.953 MB\" -> {calibratedValue} bytes");
        details.Add($"   true byte count  -> {calibratedBytes} (report section 4.2, content-range total)");
        details.Add($"   delta            -> {calibratedDelta} bytes");
        details.Add($"   18825520 / 1048576 = {calibratedBytes / 1048576d:F6}");

        if (Math.Abs(calibratedDelta) > DisplayRoundingSlackBytes)
        {
            problems.Add($"parsing \"17.953 MB\" gave {calibratedValue}, which is {calibratedDelta} bytes from the "
                         + $"known size {calibratedBytes}; expected within {DisplayRoundingSlackBytes} (0.001 MB)");
        }
        else
        {
            details.Add($"   within the {DisplayRoundingSlackBytes}-byte display-rounding slack: OK");
        }

        // The finding itself is worth pinning down, because the download matcher depends on it:
        // this field is NOT an exact identity.
        details.Add(string.Empty);
        details.Add("Conclusion this check encodes: the upstream `size` string is a display value with "
                    + "millisecond-of-a-megabyte precision, so the download matcher must use a tolerance "
                    + "(MoyuDownloadWatchOptions.SizeTolerance) rather than an equality test. A65 asserts "
                    + "the tolerance refuses a genuinely wrong-sized file.");

        if (calibratedDelta == 0)
        {
            details.Add("NOTE: this sample happened to convert exactly; the tolerance below is still the "
                        + "required behaviour, because the contract does not promise exactness.");
        }

        // --- Whitespace: accepted AND normalised, because the value can be complete -----------
        // A size string padded with whitespace still states a size, and a .Trim() cannot corrupt it.
        // Rejecting it would turn cosmetic upstream padding into "unknown size", which then degrades
        // the download matcher. So this is asserted as deliberate behaviour rather than tolerated.
        var whitespacePadded = new (string Raw, long Want)[]
        {
            (" 17.953 MB", 18825085L),
            ("17.953 MB ", 18825085L),
            ("\u00A017.953 MB", 18825085L),
            ("\t1.5 GB\n", 1610612736L),
            ("  512 B  ", 512L)
        };

        details.Add(string.Empty);
        details.Add("Whitespace-padded input (accepted and trimmed - the value is complete, only padded):");
        foreach (var (raw, want) in whitespacePadded)
        {
            var got = MoyuSize.ParseBytes(raw);
            var shown = raw.Replace("\u00A0", "<NBSP>").Replace("\t", "\\t").Replace("\n", "\\n");
            var ok = got == want;
            details.Add($"   size \"{shown}\" -> {got} (want {want}) {(ok ? "OK" : "<-- MISMATCH")}");
            if (!ok)
            {
                problems.Add($"parsing \"{shown}\" gave {got}, expected {want}");
            }
        }

        // --- Junk: never throws, always 0 ---------------------------------------------------
        var junk = new[]
        {
            "", " ", "\t", "abc", "17.953 MB extra", "MB 17.953", "-5 MB", "-0.5 MB", "1e3 MB", "MB",
            "17.953", "17.953 MiB", "17.953 Mi", "1,5 MB", "999999 TB", "17.953MBx", "+5 MB",
            "1..5 MB", ".5 MB", "5. MB", "0x10 MB"
        };

        details.Add(string.Empty);
        details.Add("Unparseable or malformed input (must return 0 and must not throw):");
        foreach (var raw in junk)
        {
            long got;
            try
            {
                got = MoyuSize.ParseBytes(raw);
            }
            catch (Exception ex)
            {
                details.Add($"   size \"{raw}\" THREW {ex.GetType().Name}");
                problems.Add($"parsing \"{raw}\" threw {ex.GetType().Name} instead of returning 0");
                continue;
            }

            var shown = raw.Replace("\u00A0", "<NBSP>").Replace("\t", "\\t");
            details.Add($"   size \"{shown}\" -> {got} {(got == 0 ? "OK" : "<-- should be 0")}");
            if (got != 0)
            {
                problems.Add($"parsing \"{shown}\" gave {got}, expected 0");
            }
        }

        // --- Round-trip through the formatter the UI will use --------------------------------
        details.Add(string.Empty);
        details.Add("Format() round-trip (the string the UI shows, re-parsed):");
        foreach (var bytes in new[] { 18825520L, 863819448L, 512L, 0L, 1024L })
        {
            var text = MoyuSize.Format(bytes);
            var back = MoyuSize.ParseBytes(text);
            details.Add($"   {bytes} bytes -> \"{text}\" -> {back} bytes");
        }

        // --- Anchor parsing ------------------------------------------------------------------
        details.Add(string.Empty);
        var accepted = new[] { "vndb:v4", "vndb:v65869", "vndb:v4".ToUpperInvariant(), "catalog:86", "catalog:61311", "vndb%3Av4" };
        var refused = new[] { "1", "86", "bangumi:1234", "vndb:", "vndb:abc", "catalog:", "", " ", "v4", "catalog:86x", "vndb:v4 mid", "vndb:v4:catalog:5" };

        details.Add("Anchor accept/refuse matrix (Parse trims, like the size parser, so padding is accepted):");
        foreach (var text in new[] { "vndb:v4 ", " vndb:v4", "\tvndb:v4\n" })
        {
            var parsed = MoyuRef.Parse(text);
            var shown = text.Replace("\t", "\\t").Replace("\n", "\\n");
            details.Add($"   {(parsed is null ? "REJECTED <-- should be accepted" : $"accepted as {parsed}"),-40} \"{shown}\" (padded)");
            if (parsed?.ToString() != "vndb:v4")
            {
                problems.Add($"anchor \"{shown}\" was not normalised to vndb:v4 (got {parsed?.ToString() ?? "(null)"})");
            }
        }

        foreach (var text in accepted)
        {
            var parsed = MoyuRef.Parse(text);
            details.Add($"   {(parsed is null ? "REJECTED <-- should be accepted" : $"accepted ({parsed})"),-40} \"{text}\"");
            if (parsed is null)
            {
                problems.Add($"anchor \"{text}\" was refused but is legal on the public face");
            }
        }

        foreach (var text in refused)
        {
            var parsed = MoyuRef.Parse(text);
            details.Add($"   {(parsed is null ? "rejected (correct)" : $"ACCEPTED ({parsed}) <-- must be refused"),-40} \"{text}\"");
            if (parsed is not null)
            {
                problems.Add($"anchor \"{text}\" was accepted but is not a legal ref");
            }
        }

        // --- The Bangumi-id dependency, stated as a measured fact ---------------------------
        details.Add(string.Empty);
        details.Add("The Bangumi-anchor dependency (report section 6.3):");
        var noAnchor = MoyuRef.PickAnchor(vndbId: null, catalogWorkId: null);
        details.Add($"   PickAnchor(null, null)          -> {(noAnchor is null ? "null (no anchor, no request possible)" : noAnchor.ToString())}");
        if (noAnchor is not null)
        {
            problems.Add("PickAnchor invented an anchor from nothing");
        }

        var catalogOnly = MoyuRef.PickAnchor(vndbId: null, catalogWorkId: "61311");
        details.Add($"   PickAnchor(null, \"61311\")      -> {catalogOnly?.ToString() ?? "(null)"}");
        if (catalogOnly?.ToString() != "catalog:61311")
        {
            problems.Add("the catalog fallback did not produce catalog:61311");
        }

        var vndbWins = MoyuRef.PickAnchor(vndbId: "v4", catalogWorkId: "61311");
        details.Add($"   PickAnchor(\"v4\", \"61311\")     -> {vndbWins?.ToString() ?? "(null)"} (VNDB is moyu's own dedupe key, so it wins)");
        if (vndbWins?.ToString() != "vndb:v4")
        {
            problems.Add("the VNDB anchor did not take precedence over the catalog anchor");
        }

        var fromBare = MoyuRef.FromVndbId("1234");
        details.Add($"   FromVndbId(\"1234\")             -> {fromBare?.ToString() ?? "(null)"}");
        if (fromBare?.ToString() != "vndb:v1234")
        {
            problems.Add("FromVndbId did not normalise a digit-only VNDB id");
        }

        var fromBangumiLike = MoyuRef.FromVndbId("bangumi:1234");
        details.Add($"   FromVndbId(\"bangumi:1234\")    -> {fromBangumiLike?.ToString() ?? "(null)"}");
        if (fromBangumiLike is not null)
        {
            problems.Add("FromVndbId accepted a Bangumi id, which is not a legal moyu anchor");
        }

        var fromVndb = MoyuRef.FromVndbId("v65869");
        details.Add($"   FromVndbId(\"v65869\")          -> {fromVndb?.ToString() ?? "(null)"}");
        if (fromVndb?.ToString() != "vndb:v65869")
        {
            problems.Add($"FromVndbId(\"v65869\") produced {fromVndb?.ToString() ?? "(null)"}, expected vndb:v65869");
        }

        // --- The `save` discriminator, which decides a routing question ---------------------
        // The local engine rejects a `save` resource by declared type, and routing a save to the
        // save-node workstream is a product rule rather than an implementation detail. So the
        // predicate has to be exactly right in both directions.
        details.Add(string.Empty);
        details.Add("The `save` type discriminator:");
        var saveCases = new (string[] Types, bool Want)[]
        {
            (new[] { "save" }, true),
            (new[] { "manual" }, false),
            (new[] { "manual", "save" }, true),
            (new[] { "SAVE" }, true),
            (Array.Empty<string>(), false)
        };

        foreach (var (types, want) in saveCases)
        {
            var got = MoyuPatchTypes.IsSaveType(types);
            var shown = types.Length == 0 ? "(empty)" : string.Join(",", types);
            details.Add($"   IsSaveType([{shown}]) -> {got} (want {want}) {(got == want ? "OK" : "<-- MISMATCH")}");
            if (got != want)
            {
                problems.Add($"IsSaveType([{shown}]) returned {got}, expected {want}");
            }
        }

        // --- The twelve-value vocabulary the spec defines -----------------------------------
        details.Add(string.Empty);
        details.Add("Upstream type vocabulary (12 values, spec line 508) -> Chinese labels:");
        string[] allTypes = { "manual", "ai", "machine_polishing", "machine", "save", "crack", "fix", "mod", "r18", "decensor", "image", "other" };
        foreach (var type in allTypes)
        {
            var label = MoyuPatchTypes.Describe(type);
            details.Add($"   {type,-20} -> {label}");
            if (label == type)
            {
                problems.Add($"type \"{type}\" was not mapped to a Chinese label");
            }
        }

        var unmapped = MoyuPatchTypes.Describe("something_new");
        details.Add($"   {"something_new",-20} -> {unmapped} (unknown values pass through unchanged, "
                    + "so a contract change stays visible instead of being relabelled `其它`)");
        if (unmapped != "something_new")
        {
            problems.Add($"an unknown type was relabelled to \"{unmapped}\" instead of passing through");
        }

        foreach (var problem in problems)
        {
            details.Add($"PROBLEM: {problem}");
        }

        if (problems.Count > 0)
        {
            return Task.FromResult(CheckResult.Fail(Id, Title, expected,
                $"{problems.Count} parser problem(s): {string.Join("; ", problems)}").With(details.ToArray()));
        }

        return Task.FromResult(CheckResult.Pass(Id, Title, expected,
            $"{exact.Length} exact samples bit-exact, the calibrated sample within {DisplayRoundingSlackBytes} bytes "
            + $"of the known size, {junk.Length} malformed inputs returned 0, "
            + $"{accepted.Length} anchors accepted and {refused.Length} refused")
            .With(details.ToArray()));
    }
}

/// <summary>
/// A65 - the downloads-folder takeover, driven against a throw-away directory and a synthetic file.
/// No site is contacted and nothing is downloaded. The file is handed to
/// <see cref="IPatchEngine"/> through its real interface, because the whole point of the takeover is
/// that it stops there: the engine owns extraction, overwrite and rollback.
/// </summary>
public sealed class A65MoyuDownloadWatchCheck : IAcceptanceCheck
{
    private const int SyntheticFileBytes = 4096;

    /// <inheritdoc />
    public string Id => "A65";

    /// <inheritdoc />
    public string Title => "The downloads-folder watcher adopts the new file and times out cleanly";

    /// <inheritdoc />
    public async Task<CheckResult> RunAsync(AcceptanceContext context, CancellationToken cancellationToken)
    {
        var expected =
            "a file appearing in the watched folder is adopted (Found) once its size is stable; a "
            + "watch that sees nothing returns TimedOut; an oversized mismatch is not adopted";

        var details = new List<string>();
        var problems = new List<string>();

        var scratch = MoyuCheckSupport.NewScratchDirectory("watch");

        try
        {
            var watcher = new MoyuDownloadWatcher();
            var policy = new MoyuDownloadWatchOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(100),
                StabilityWindow = TimeSpan.FromMilliseconds(300)
            };

            // ---- 1. Positive case: the file shows up after the watch has started -------------
            var payload = new byte[SyntheticFileBytes];
            Random.Shared.NextBytes(payload);
            var filePath = Path.Combine(scratch, "A65-synthetic-patch.rar");

            var request = new MoyuDownloadWatchRequest
            {
                DirectoryPath = scratch,
                ExpectedSizeBytes = SyntheticFileBytes,
                PatchName = "A65 synthetic patch",
                WebUrl = "https://www.moyu.moe/patch/86/introduction",
                Timeout = TimeSpan.FromSeconds(25),
                PollInterval = policy.PollInterval,
                Options = policy
            };

            details.Add($"Watched folder    : {scratch}");
            details.Add($"Expected size     : {SyntheticFileBytes} bytes");
            details.Add($"Stability window  : {policy.StabilityWindow.TotalMilliseconds:F0} ms");
            details.Add("The file is written 600 ms AFTER the watch starts, on purpose: a watch that "
                        + "adopted a pre-existing file would grab last week's download.");

            var writeTask = Task.Run(async () =>
            {
                await Task.Delay(600, CancellationToken.None).ConfigureAwait(false);
                await File.WriteAllBytesAsync(filePath, payload, CancellationToken.None).ConfigureAwait(false);
            });

            var stopwatch = Stopwatch.StartNew();
            var outcome = await watcher.WatchAsync(request, progress: null, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            await writeTask.ConfigureAwait(false);

            details.Add(string.Empty);
            details.Add($"Positive watch    : State={outcome.State}, File={outcome.File ?? "(none)"}, "
                        + $"Size={outcome.FileSizeBytes}, Elapsed={outcome.Elapsed.TotalMilliseconds:F0} ms");
            details.Add($"  Reason          : {outcome.Reason}");
            foreach (var line in outcome.Considered.Take(10))
            {
                details.Add($"  Considered      : {line}");
            }

            if (outcome.State != MoyuDownloadWatchState.Found)
            {
                problems.Add($"expected State=Found, measured {outcome.State}");
            }

            if (!string.Equals(outcome.File, filePath, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"expected File=\"{filePath}\", measured \"{outcome.File ?? "(none)"}\"");
            }

            if (outcome.FileSizeBytes != SyntheticFileBytes)
            {
                problems.Add($"expected FileSizeBytes={SyntheticFileBytes}, measured {outcome.FileSizeBytes}");
            }

            // ---- 2. The adopted file must be accepted by the real patch engine ----------------
            // This is the seam the whole feature exists for: the watcher does not unpack anything,
            // it hands a path to IPatchEngine. Calling InspectAsync proves the handover compiles
            // against the real interface and that the engine, not this layer, owns the format
            // decision.
            var engine = ((IServiceProvider)context.Services).GetService(typeof(IPatchEngine)) as IPatchEngine;
            details.Add(string.Empty);
            details.Add($"IPatchEngine from DI : {(engine is null ? "(MISSING)" : engine.GetType().Name)}");

            if (engine is null)
            {
                problems.Add("IPatchEngine does not resolve from the container, so an adopted file could not be installed");
            }
            else if (outcome.Found)
            {
                try
                {
                    var archive = await engine.InspectAsync(outcome.File!, null, cancellationToken).ConfigureAwait(false);
                    details.Add($"  InspectAsync(\"{Path.GetFileName(outcome.File!)}\") -> kind={archive.Kind}, "
                                + $"formatId={archive.FormatId}, entries={archive.EntryCount}, "
                                + $"encrypted={archive.IsEncrypted}, requiresManualRun={archive.RequiresManualRun}");
                }
                catch (Exception ex)
                {
                    // The synthetic file is random bytes, so the engine refusing it is CORRECT
                    // behaviour - it must not guess a format. What matters is that it is the engine
                    // answering, with its own typed exception, and not an unpack inside the watcher.
                    details.Add($"  InspectAsync refused the synthetic file: {ex.GetType().Name}: {ex.Message}");
                    details.Add("  (correct: the probe file is random bytes; the engine must not guess a format)");
                }
            }

            // ---- 3. A wrong-sized file must NOT be adopted -----------------------------------
            var mismatchDirectory = MoyuCheckSupport.NewScratchDirectory("watch-mismatch");
            try
            {
                var mismatchFile = Path.Combine(mismatchDirectory, "A65-unrelated-download.rar");
                await File.WriteAllBytesAsync(mismatchFile, new byte[64 * 1024], cancellationToken).ConfigureAwait(false);

                var mismatchRequest = new MoyuDownloadWatchRequest
                {
                    DirectoryPath = mismatchDirectory,
                    ExpectedSizeBytes = 170L * 1024 * 1024,
                    Timeout = TimeSpan.FromMilliseconds(600),
                    PollInterval = TimeSpan.FromMilliseconds(100),
                    Options = policy
                };

                var mismatchOutcome = await watcher.WatchAsync(mismatchRequest, null, cancellationToken).ConfigureAwait(false);

                details.Add(string.Empty);
                details.Add($"Size-mismatch watch: State={mismatchOutcome.State}, File={mismatchOutcome.File ?? "(none)"}");
                foreach (var line in mismatchOutcome.Considered.Take(5))
                {
                    details.Add($"  Considered      : {line}");
                }

                if (mismatchOutcome.State == MoyuDownloadWatchState.Found)
                {
                    problems.Add("a 64 KB file was adopted when the provider declared 170 MB");
                }
            }
            finally
            {
                MoyuCheckSupport.TryDeleteDirectory(mismatchDirectory);
            }

            // ---- 4. Timeout: nothing ever appears ------------------------------------------
            var emptyDirectory = MoyuCheckSupport.NewScratchDirectory("watch-timeout");
            try
            {
                var timeoutRequest = new MoyuDownloadWatchRequest
                {
                    DirectoryPath = emptyDirectory,
                    ExpectedSizeBytes = 1024L * 1024 * 1024,
                    PatchName = "A65 timeout probe",
                    Timeout = TimeSpan.FromMilliseconds(700),
                    PollInterval = TimeSpan.FromMilliseconds(100),
                    Options = policy
                };

                var timeoutStopwatch = Stopwatch.StartNew();
                var timeoutOutcome = await watcher.WatchAsync(timeoutRequest, null, cancellationToken).ConfigureAwait(false);
                timeoutStopwatch.Stop();

                details.Add(string.Empty);
                details.Add($"Empty watch       : State={timeoutOutcome.State}, elapsed={timeoutStopwatch.ElapsedMilliseconds} ms "
                            + $"(timeout was {timeoutRequest.Timeout.TotalMilliseconds:F0} ms)");
                details.Add($"  Reason          : {timeoutOutcome.Reason}");

                if (timeoutOutcome.State != MoyuDownloadWatchState.TimedOut)
                {
                    problems.Add($"a watch that saw nothing returned {timeoutOutcome.State}, expected TimedOut");
                }

                if (timeoutStopwatch.ElapsedMilliseconds > 15000)
                {
                    problems.Add($"the timeout was not honoured ({timeoutStopwatch.ElapsedMilliseconds} ms for a 700 ms timeout)");
                }
            }
            finally
            {
                MoyuCheckSupport.TryDeleteDirectory(emptyDirectory);
            }

            // ---- 5. Cancellation ------------------------------------------------------------
            var cancelDirectory = MoyuCheckSupport.NewScratchDirectory("watch-cancel");
            try
            {
                using var cancelSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
                var cancelRequest = new MoyuDownloadWatchRequest
                {
                    DirectoryPath = cancelDirectory,
                    ExpectedSizeBytes = 50L * 1024 * 1024,
                    Timeout = TimeSpan.FromSeconds(30),
                    PollInterval = TimeSpan.FromMilliseconds(100),
                    Options = policy
                };

                var cancelOutcome = await watcher.WatchAsync(cancelRequest, null, cancelSource.Token).ConfigureAwait(false);

                details.Add(string.Empty);
                details.Add($"Cancelled watch   : State={cancelOutcome.State}");

                if (cancelOutcome.State != MoyuDownloadWatchState.Cancelled)
                {
                    problems.Add($"a cancelled watch returned {cancelOutcome.State}, expected Cancelled");
                }
            }
            finally
            {
                MoyuCheckSupport.TryDeleteDirectory(cancelDirectory);
            }

            // ---- 6. A missing folder is a reported failure, not an exception -----------------
            var missingOutcome = await watcher.WatchAsync(
                new MoyuDownloadWatchRequest
                {
                    DirectoryPath = Path.Combine(Path.GetTempPath(), "GalboxMoyuAcceptance", "does-not-exist-" + Guid.NewGuid().ToString("N")),
                    ExpectedSizeBytes = 1024,
                    Timeout = TimeSpan.FromSeconds(5)
                },
                null,
                cancellationToken).ConfigureAwait(false);

            details.Add(string.Empty);
            details.Add($"Missing folder    : State={missingOutcome.State}, Reason={missingOutcome.Reason}");

            if (missingOutcome.State != MoyuDownloadWatchState.Failed)
            {
                problems.Add($"a missing folder returned {missingOutcome.State}, expected Failed");
            }

            foreach (var problem in problems)
            {
                details.Add($"PROBLEM: {problem}");
            }

            if (problems.Count > 0)
            {
                return CheckResult.Fail(Id, Title, expected, string.Join("; ", problems)).With(details.ToArray());
            }

            return CheckResult.Pass(Id, Title, expected,
                $"adopted \"{Path.GetFileName(outcome.File!)}\" after {outcome.Elapsed.TotalMilliseconds:F0} ms by size match; "
                + "a 64 KB file was refused against a declared 170 MB; the empty watch reported TimedOut; "
                + "cancellation and a missing folder are reported states")
                .With(details.ToArray());
        }
        finally
        {
            MoyuCheckSupport.TryDeleteDirectory(scratch);
        }
    }
}

/// <summary>
/// A66 - what gets handed to the shell. The browser hop is the compliant substitute for a direct
/// link, so the launcher must accept only a real moyu page over HTTPS and refuse anything pointing
/// into the <c>Disallow</c>-ed API surface, the CDN hosts, or a non-web scheme. It also asserts the
/// opposite direction: a refused URL is never reported as a successful launch.
/// </summary>
public sealed class A66MoyuBrowserLaunchCheck : IAcceptanceCheck
{
    /// <inheritdoc />
    public string Id => "A66";

    /// <inheritdoc />
    public string Title => "The browser hop accepts only HTTPS moyu pages and refuses API URLs";

    /// <inheritdoc />
    public Task<CheckResult> RunAsync(AcceptanceContext context, CancellationToken cancellationToken)
    {
        var expected =
            "ValidateWebUrl accepts https://www.moyu.moe/patch/… and refuses http, /api, the API/CDN "
            + "hosts, other schemes and other hosts; a dry run reports success without starting a "
            + "process, and a refused URL is reported as a failure";

        var details = new List<string>();
        var problems = new List<string>();

        var accepts = new[]
        {
            "https://www.moyu.moe/patch/86/introduction",
            "https://www.moyu.moe/patch/223/resources",
            "https://moyu.moe/patch/86/introduction"
        };

        var refuses = new[]
        {
            "http://www.moyu.moe/patch/86/introduction",
            "https://www.moyu.moe/api/v1/patch/resource/223/link",
            "https://www.moyu.moe/api/v1/search?keywords=CLANNAD",
            "https://api.nextmoe.dev/v2/moyu/patches",
            "https://dl.imoe.uk/moyu/ec3ecb28-2e7e-51fa-bb03-c0df69bc9ac7.rar",
            "https://oss.moyu.moe/patch/243/690eac.rar",
            "https://evil.example/patch/86",
            "file:///C:/Windows/System32/calc.exe",
            "javascript:alert(1)",
            "not a url",
            "",
            "https://www.moyu.moe"
        };

        details.Add("URL validation:");
        foreach (var url in accepts)
        {
            var ok = MoyuBrowserLauncher.ValidateWebUrl(url);
            details.Add($"   {(ok ? "accepted" : "REJECTED (wrong)"),-16} {url}");
            if (!ok)
            {
                problems.Add($"the launcher refuses a legitimate patch page: {url}");
            }
        }

        foreach (var url in refuses)
        {
            var ok = MoyuBrowserLauncher.ValidateWebUrl(url);
            var shown = string.IsNullOrEmpty(url) ? "(empty string)" : url;
            details.Add($"   {(ok ? "ACCEPTED (wrong)" : "rejected"),-16} {shown}");
            if (ok)
            {
                problems.Add($"the launcher accepts a URL it must refuse: {shown}");
            }
        }

        // --- Dry run: validated and reported, nothing started -------------------------------
        var launcher = new MoyuBrowserLauncher(new MoyuBrowserLauncherOptions { DryRun = true });
        var launched = launcher.Launch("https://www.moyu.moe/patch/86/introduction");

        details.Add(string.Empty);
        details.Add($"Dry-run Launch    : Succeeded={launched.Succeeded}, DryRun={launched.DryRun}, "
                    + $"ProcessId={launched.ProcessId?.ToString() ?? "(none)"}, Url={launched.Url}");
        details.Add($"  Message         : {launched.Message}");

        if (!launched.Succeeded)
        {
            problems.Add("a dry-run launch of a legitimate page failed");
        }

        if (launched.ProcessId is not null)
        {
            problems.Add($"a dry run started a process (pid {launched.ProcessId})");
        }

        // --- The opposite direction: a refused URL must not look like a successful no-op -----
        var refusedLaunch = launcher.Launch("https://www.moyu.moe/api/v1/patch/resource/223/link");
        details.Add(string.Empty);
        details.Add($"Refused launch    : Succeeded={refusedLaunch.Succeeded}, Message={refusedLaunch.Message}");

        if (refusedLaunch.Succeeded)
        {
            problems.Add("a refused URL was reported as a successful launch");
        }

        // --- The launcher must be drivable from a resource row ------------------------------
        var resource = new MoyuResource
        {
            Id = "6262",
            PatchId = "86",
            Name = "CLANNAD 汉化补丁",
            Storage = "s3",
            SizeText = "17.953 MB",
            SizeBytes = MoyuSize.ParseBytes("17.953 MB"),
            WebUrl = "https://www.moyu.moe/patch/86/resources/6262"
        };

        var buildWebUrl = MoyuApi.BuildWebUrl(resource);
        details.Add(string.Empty);
        details.Add($"MoyuApi.BuildWebUrl(resource) -> {buildWebUrl ?? "(null)"}");

        if (buildWebUrl != resource.WebUrl)
        {
            problems.Add($"BuildWebUrl returned {buildWebUrl ?? "(null)"} for a valid page URL");
        }

        // --- A resource with no page URL must fail loudly, not silently open nothing --------
        var urlLess = new MoyuResource { Id = "1", PatchId = "2", WebUrl = null };
        var noUrlLaunch = launcher.Launch(MoyuApi.BuildWebUrl(urlLess));
        details.Add($"Launch(BuildWebUrl(resource without web_url)) -> Succeeded={noUrlLaunch.Succeeded}, "
                    + $"Message={noUrlLaunch.Message}");

        if (noUrlLaunch.Succeeded)
        {
            problems.Add("a resource with no page URL produced a successful launch");
        }

        foreach (var problem in problems)
        {
            details.Add($"PROBLEM: {problem}");
        }

        if (problems.Count > 0)
        {
            return Task.FromResult(CheckResult.Fail(Id, Title, expected,
                $"{problems.Count} launcher problem(s): {string.Join("; ", problems)}").With(details.ToArray()));
        }

        return Task.FromResult(CheckResult.Pass(Id, Title, expected,
            $"{accepts.Length} legitimate page URL(s) accepted, {refuses.Length} dangerous URL(s) refused, "
            + "dry run started no process, a refused URL reported failure")
            .With(details.ToArray()));
    }
}

/// <summary>
/// A67 - the two behaviours the report calls out as broken in the shared <see cref="ApiClient"/>
/// and that this client has to implement itself: honouring <c>Retry-After</c> on a <c>429</c>, and
/// revalidating with <c>If-None-Match</c> instead of refetching.
///
/// <para>
/// Both matter for the same reason: this is a free community service. A <c>429</c> that is retried on
/// a made-up backoff instead of the time the server asked for is a client hammering a service that
/// told it to wait; a <c>304</c> that costs a full body is bandwidth the operator pays for.
/// </para>
/// </summary>
public sealed class A67MoyuConditionalRequestCheck : IAcceptanceCheck
{
    private const string Payload =
        """{"object":"list","items":[{"object":"patch","id":"86","vndb_id":"v4","catalog_work_id":"86","content_limit":"sfw","release_date":"2004-04-28","type":["manual"],"language":["zh-Hans"],"platform":["windows"],"resource_count":2,"download_count":282,"view_count":0,"favorite_count":0,"comment_count":0,"web_url":"https://www.moyu.moe/patch/86/introduction","created_at":"2024-12-13T05:25:54.167Z","updated_at":"2026-05-27T18:41:40.338Z","resource_updated_at":"2024-12-13T05:25:54.167Z"}],"next_cursor":null,"total":1,"missing":[]}""";

    /// <inheritdoc />
    public string Id => "A67";

    /// <inheritdoc />
    public string Title => "429 Retry-After is honoured and a 304 reuses the cached document";

    /// <inheritdoc />
    public async Task<CheckResult> RunAsync(AcceptanceContext context, CancellationToken cancellationToken)
    {
        var expected =
            "a 429 carrying Retry-After=2 is not retried immediately; a second call sends "
            + "If-None-Match and a 304 returns the cached rows; Retry-After beyond the cap is reported "
            + "instead of waited out";

        var details = new List<string>();
        var problems = new List<string>();

        // ---- 1. ETag / 304 -----------------------------------------------------------------
        var etagCalls = new List<(Uri Uri, string? IfNoneMatch)>();
        var etagHandler = new MoyuStubHandler(request =>
        {
            var ifNoneMatch = request.Headers.TryGetValues("If-None-Match", out var values)
                ? values.FirstOrDefault()
                : null;
            etagCalls.Add((request.RequestUri!, ifNoneMatch));

            if (ifNoneMatch is not null)
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.NotModified)
                {
                    Content = new StringContent(string.Empty)
                };
            }

            return MoyuStubHandler.Json(Payload, etag: "\"etag-a67\"");
        });

        var etagOptions = new MoyuOptions { ApiKey = "nmk_live_A67probe_000000000000000000" };
        var api = MoyuStubHandler.BuildApi(etagHandler, apiKey: null, options: etagOptions);

        var request = new[] { MoyuRef.Parse("vndb:v4")! };

        var first = await api.FindPatchesAsync(request, cancellationToken: cancellationToken).ConfigureAwait(false);
        var second = await api.FindPatchesAsync(request, cancellationToken: cancellationToken).ConfigureAwait(false);

        details.Add($"Call 1            : Failed={first.Failed}, rows={first.Value?.Items.Count ?? -1}, "
                    + $"If-None-Match={etagCalls.ElementAtOrDefault(0).IfNoneMatch ?? "(absent)"}");
        details.Add($"Call 2            : Failed={second.Failed}, rows={second.Value?.Items.Count ?? -1}, "
                    + $"If-None-Match={etagCalls.ElementAtOrDefault(1).IfNoneMatch ?? "(absent)"}");

        if (first.Value?.Items.Count != 1)
        {
            problems.Add($"the first call returned {first.Value?.Items.Count ?? -1} rows, expected 1");
        }

        if (etagCalls.Count < 2)
        {
            problems.Add($"only {etagCalls.Count} request(s) reached the transport, expected 2");
        }
        else
        {
            if (etagCalls[0].IfNoneMatch is not null)
            {
                problems.Add("the first call sent If-None-Match, but nothing was cached yet");
            }

            if (etagCalls[1].IfNoneMatch is null)
            {
                problems.Add("the second call did NOT send If-None-Match, so the ETag was ignored");
            }
        }

        if (second.Value?.Items.Count != 1)
        {
            problems.Add($"the 304 did not return the cached document ({second.Value?.Items.Count ?? -1} rows)");
        }

        // ---- 2. Retry-After honoured on 429 ------------------------------------------------
        var retryCalls = 0;
        var retryHandler = new MoyuStubHandler(_ =>
        {
            retryCalls++;
            if (retryCalls == 1)
            {
                var tooMany = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests)
                {
                    Content = new StringContent("""{"code":"QUOTA_EXCEEDED","message":"slow down"}""", Encoding.UTF8, "application/json")
                };
                tooMany.Headers.TryAddWithoutValidation("Retry-After", "2");
                return tooMany;
            }

            return MoyuStubHandler.Json(Payload, etag: "\"etag-retry\"");
        });

        var retryApi = MoyuStubHandler.BuildApi(
            retryHandler,
            apiKey: null,
            options: new MoyuOptions { ApiKey = "nmk_live_A67probe_000000000000000000", MaxRetries = 2 });

        var retryStopwatch = Stopwatch.StartNew();
        var retried = await retryApi.FindPatchesAsync(request, cancellationToken: cancellationToken).ConfigureAwait(false);
        retryStopwatch.Stop();

        details.Add(string.Empty);
        details.Add($"429 then 200     : attempts={retryCalls}, elapsed={retryStopwatch.ElapsedMilliseconds} ms, "
                    + $"Failed={retried.Failed}, rows={retried.Value?.Items.Count ?? -1}");

        if (retryCalls < 2)
        {
            problems.Add($"the 429 was not retried (attempts={retryCalls}), so a transient quota error is fatal");
        }

        // Retry-After said 2 seconds. Anything under 1.8 s means the client invented its own
        // backoff instead of reading the header - exactly the gap in the shared ApiClient.
        if (retryStopwatch.ElapsedMilliseconds < 1800)
        {
            problems.Add($"the retry happened after {retryStopwatch.ElapsedMilliseconds} ms despite Retry-After: 2, "
                         + "so the header was ignored");
        }

        if (retried.Failed)
        {
            problems.Add($"the retry after the 429 did not succeed: {retried.Failure?.Code}");
        }

        // ---- 3. A Retry-After beyond the cap is reported, not waited out -------------------
        var longRetryCalls = 0;
        var longRetryHandler = new MoyuStubHandler(_ =>
        {
            longRetryCalls++;
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("""{"code":"QUOTA_EXCEEDED","message":"come back tomorrow"}""", Encoding.UTF8, "application/json")
            };
            response.Headers.TryAddWithoutValidation("Retry-After", "3600");
            return response;
        });

        var longRetryApi = MoyuStubHandler.BuildApi(
            longRetryHandler,
            apiKey: null,
            options: new MoyuOptions
            {
                ApiKey = "nmk_live_A67probe_000000000000000000",
                MaxRetries = 2,
                MaxHonouredRetryAfter = TimeSpan.FromSeconds(5)
            });

        var longStopwatch = Stopwatch.StartNew();
        var longOutcome = await longRetryApi.FindPatchesAsync(request, cancellationToken: cancellationToken).ConfigureAwait(false);
        longStopwatch.Stop();

        details.Add(string.Empty);
        details.Add($"Retry-After: 3600 (cap 5s): attempts={longRetryCalls}, elapsed={longStopwatch.ElapsedMilliseconds} ms, "
                    + $"Failed={longOutcome.Failed}, Code={longOutcome.Failure?.Code.ToString() ?? "(none)"}, "
                    + $"RetryAfter={longOutcome.Failure?.RetryAfter?.TotalSeconds.ToString() ?? "(none)"}s");

        if (!longOutcome.Failed)
        {
            problems.Add("a permanent 429 was reported as success");
        }

        if (longOutcome.Failure?.Code != MoyuFailureCode.RateLimited)
        {
            problems.Add($"a 429 produced Failure.Code={longOutcome.Failure?.Code.ToString() ?? "(none)"}, expected RateLimited");
        }

        if (longStopwatch.ElapsedMilliseconds > 4000)
        {
            problems.Add($"the client waited {longStopwatch.ElapsedMilliseconds} ms for a Retry-After beyond its cap");
        }

        if (longRetryCalls > 1)
        {
            problems.Add($"the client retried {longRetryCalls} times against an hour-long Retry-After");
        }

        if (longOutcome.Failure?.RetryAfter is null)
        {
            problems.Add("the failure does not carry the Retry-After it was given, so a UI cannot say when to retry");
        }

        // ---- 4. The key must never appear in an error surface ------------------------------
        var secretKey = "nmk_live_A67probe_000000000000000000";
        var surfaces = new[]
        {
            first.Failure?.Message, second.Failure?.Message, retried.Failure?.Message,
            longOutcome.Failure?.Message, api.LastError, api.LastErrorBody,
            longRetryApi.LastError, longRetryApi.LastErrorBody
        };

        details.Add(string.Empty);
        details.Add($"Key present in any failure/LastError surface: "
                    + $"{surfaces.Any(s => s is not null && s.Contains(secretKey, StringComparison.Ordinal))}");
        details.Add($"lastRateLimitNotice: {longRetryApi.LastRateLimitNotice ?? "(none)"}");

        if (surfaces.Any(s => s is not null && s.Contains(secretKey, StringComparison.Ordinal)))
        {
            problems.Add("the API key appears in a failure message or LastError");
        }

        // ---- 5. Pacing ---------------------------------------------------------------------
        var limiter = new MoyuRateLimiter(TimeSpan.FromMilliseconds(250), maxRequestsPerMinute: 3);
        var paceStopwatch = Stopwatch.StartNew();
        var waits = 0;
        for (var i = 0; i < 3; i++)
        {
            var verdict = await limiter.AcquireAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            if (verdict.Decision == MoyuRateLimitDecision.Wait)
            {
                waits++;
                await Task.Delay(verdict.RetryAfter, cancellationToken).ConfigureAwait(false);
            }
        }

        paceStopwatch.Stop();
        details.Add(string.Empty);
        details.Add($"Pacing            : 3 acquisitions at 250 ms spacing took {paceStopwatch.ElapsedMilliseconds} ms "
                    + $"({waits} required a wait)");

        if (paceStopwatch.ElapsedMilliseconds < 450)
        {
            problems.Add($"the rate limiter allowed 3 requests in {paceStopwatch.ElapsedMilliseconds} ms, "
                         + "so it is not enforcing the minimum interval");
        }

        // The rolling-minute budget must refuse rather than silently allow.
        var budgetLimiter = new MoyuRateLimiter(TimeSpan.Zero, maxRequestsPerMinute: 2);
        var allowed = 0;
        for (var i = 0; i < 5; i++)
        {
            var verdict = await budgetLimiter.AcquireAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            if (verdict.CanProceed)
            {
                allowed++;
            }
        }

        details.Add($"Minute budget     : 2/minute limiter allowed {allowed} of 5 immediate requests");
        if (allowed != 2)
        {
            problems.Add($"a 2/minute limiter allowed {allowed} requests, expected 2");
        }

        foreach (var problem in problems)
        {
            details.Add($"PROBLEM: {problem}");
        }

        if (problems.Count > 0)
        {
            return CheckResult.Fail(Id, Title, expected,
                $"{problems.Count} problem(s): {string.Join("; ", problems)}").With(details.ToArray());
        }

        return CheckResult.Pass(Id, Title, expected,
            "If-None-Match sent on the second call and the 304 reused the cache; "
            + $"Retry-After: 2 honoured ({retryStopwatch.ElapsedMilliseconds} ms); "
            + "Retry-After: 3600 reported as RateLimited without waiting; the key is absent from every "
            + "error surface; pacing and the minute budget both hold")
            .With(details.ToArray());
    }
}
