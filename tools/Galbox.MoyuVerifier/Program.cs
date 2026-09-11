using System.Diagnostics;
using System.Text;
using Galbox.Core.Api;
using Galbox.Core.Patches;

namespace Galbox.MoyuVerifier;

/// <summary>
/// One-command verification of the moyu patch-discovery layer.
///
/// <para>
/// Run it after minting an <c>nmk_</c> key to check the whole feature end to end:
/// </para>
/// <code>
/// set GALBOX_MOYU_API_KEY=nmk_live_...
/// dotnet run --project tools\Galbox.MoyuVerifier -c Debug
/// </code>
///
/// <para>
/// With <b>no</b> key configured it still verifies everything that does not need the network: the
/// compliance guard, the parsers, the DPAPI store and the downloads-folder takeover. Only the live
/// call section is skipped, and it says so.
/// </para>
///
/// <para>
/// <b>The key is never printed.</b> Every diagnostic here is built from
/// <see cref="IMoyuKeyStore.FingerprintOf"/>, which is a one-way digest, or from the store's own
/// <c>ToString</c>, which was written to describe presence rather than value.
/// </para>
///
/// <para>
/// Network discipline: at most <b>two</b> requests, only when a key is configured, each triggered by
/// this explicit run. One for a VNDB anchor, and - only if that one found a patch page - one for
/// that page's resources. Nothing is polled, prefetched or traversed.
/// </para>
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch
        {
            // A redirected console can refuse the encoding change.
        }

        var keyToSave = ReadOption(args, "--save-key");
        var anchorText = ReadOption(args, "--ref") ?? "vndb:v4";

        Console.WriteLine(new string('=', 96));
        Console.WriteLine(" GALBOX x moyu.moe  patch discovery verification");
        Console.WriteLine(new string('=', 96));
        Console.WriteLine();

        var failures = 0;

        failures += VerifyComplianceGuard();
        failures += VerifyParsers();
        failures += VerifyKeyStore();
        failures += VerifyDownloadsFolderTakeover();
        failures += VerifyPatchEngineHandover();

        if (keyToSave is not null)
        {
            failures += SaveKey(keyToSave);
        }

        failures += await VerifyLiveCallAsync(anchorText).ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine(new string('=', 96));
        Console.WriteLine(failures == 0
            ? $" RESULT: {_passed} check(s) passed, 0 failed. {(keyToSave is null ? string.Empty : "Key stored. ")}"
            : $" RESULT: {failures} check(s) FAILED (of {_passed + failures}).");
        Console.WriteLine(new string('=', 96));

        return failures == 0 ? 0 : 1;
    }

    private static int _passed;

    /// <summary>Prints a PASS/FAIL line and returns 1 when it failed.</summary>
    private static int Verdict(bool ok, string label, string measured)
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}");
        Console.WriteLine($"         {measured}");
        if (ok)
        {
            _passed++;
            return 0;
        }

        return 1;
    }

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine($"--- {title} " + new string('-', Math.Max(0, 90 - title.Length)));
    }

    private static string? ReadOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                return args[i + 1];
            }
        }

        return null;
    }

    // ---------------------------------------------------------------------------------------
    // 1. Compliance guard
    // ---------------------------------------------------------------------------------------

    private static int VerifyComplianceGuard()
    {
        Section("Compliance guard (robots.txt says `Disallow: /api`)");

        var failures = 0;

        // The forbidden surface. These are the real endpoints the research report measured as
        // reachable - listed here precisely because "reachable" is not "allowed".
        string[] forbidden =
        {
            "https://www.moyu.moe/api/v1/search?keywords=CLANNAD&type=resource",
            "https://www.moyu.moe/api/v1/patch/86",
            "https://www.moyu.moe/api/v1/patch/86/resource",
            "https://www.moyu.moe/api/v1/patch/resource/223/link",
            "https://www.moyu.moe/api/v1/galgame",
            "https://api.nextmoe.dev/api/v1/moyu/patches",
            "http://api.nextmoe.dev/v2/moyu/patches",
            "https://evil.example/v2/moyu/patches"
        };

        foreach (var url in forbidden)
        {
            var allowed = MoyuComplianceGuard.IsAllowedApiUri(new Uri(url));
            failures += Verdict(!allowed, $"refuses {url}", allowed ? "ALLOWED - would request the forbidden surface" : "refused");
        }

        string[] allowedUrls =
        {
            "https://api.nextmoe.dev/v2/moyu/patches?refs=vndb%3Av4",
            "https://api.nextmoe.dev/v2/moyu/patches/86/resources?limit=50"
        };

        foreach (var url in allowedUrls)
        {
            var allowed = MoyuComplianceGuard.IsAllowedApiUri(new Uri(url));
            failures += Verdict(allowed, $"allows {url}", allowed ? "allowed" : "REJECTED - the client could not work");
        }

        var threw = false;
        try
        {
            MoyuComplianceGuard.EnsureApiUri(MoyuOptions.DefaultBaseAddress, "/api/v1/patch/resource/223/link");
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }

        failures += Verdict(threw,
            "EnsureApiUri refuses to construct a /api URI",
            threw ? "threw InvalidOperationException (the URI cannot reach the wire)" : "returned a URI - the guard is bypassable");

        Console.WriteLine($"         User-Agent: {MoyuComplianceGuard.UserAgent}");

        return failures;
    }

    // ---------------------------------------------------------------------------------------
    // 2. Parsers
    // ---------------------------------------------------------------------------------------

    private static int VerifyParsers()
    {
        Section("Parsers (binary-unit size and game anchor)");

        var failures = 0;

        // 18825520 bytes is the byte count the research report measured for the resource whose
        // `size` field reads "17.953 MB". The unit is binary, so 1 MB = 1048576.
        var parsed = MoyuSize.ParseBytes("17.953 MB");
        const long trueBytes = 18825520L;
        var delta = trueBytes - parsed;
        failures += Verdict(Math.Abs(delta) <= 525,
            "size \"17.953 MB\"",
            $"{parsed} bytes; the file is {trueBytes} bytes (delta {delta}). The upstream field is a "
            + "3-decimal display string, so the download matcher uses a tolerance rather than equality.");

        failures += Verdict(MoyuSize.ParseBytes("1.5 GB") == 1610612736L,
            "size \"1.5 GB\" is bit-exact",
            $"{MoyuSize.ParseBytes("1.5 GB")} bytes (want 1610612736)");

        failures += Verdict(MoyuSize.ParseBytes("abc") == 0 && MoyuSize.ParseBytes("") == 0,
            "unparseable sizes return 0 without throwing",
            "abc -> 0, \"\" -> 0");

        var anchor = MoyuRef.Parse("vndb:v4");
        failures += Verdict(anchor is not null && anchor.ToString() == "vndb:v4",
            "anchor \"vndb:v4\" parses",
            anchor?.ToString() ?? "(null)");

        failures += Verdict(MoyuRef.Parse("1") is null && MoyuRef.Parse("bangumi:1234") is null,
            "a bare number or a Bangumi id is refused as an anchor",
            "the public face accepts only vndb:vXXXX and catalog:<id>; a Bangumi id is not convertible");

        failures += Verdict(MoyuRef.PickAnchor(null, null) is null,
            "a game with neither a VNDB nor a catalog id produces no anchor (and sends no request)",
            "reported to the caller as MoyuFailureCode.NoAnchor");

        return failures;
    }

    // ---------------------------------------------------------------------------------------
    // 3. Key store
    // ---------------------------------------------------------------------------------------

    private static int VerifyKeyStore()
    {
        Section("Key storage (Windows DPAPI)");

        var failures = 0;
        var store = new MoyuDpapiKeyStore();

        Console.WriteLine($"         Store path : {store.StorePath}");
        Console.WriteLine($"         Configured : {store.IsConfigured}");

        var scratch = Path.Combine(Path.GetTempPath(), "GalboxMoyuVerifier", Guid.NewGuid().ToString("N"));
        try
        {
            var probePath = Path.Combine(scratch, "secrets", "probe.bin");
            var probeStore = new MoyuDpapiKeyStore(probePath);
            const string probeKey = "nmk_live_VERIFIERprobe_0000000000000";

            probeStore.Set(probeKey);
            var raw = File.ReadAllBytes(probePath);

            var leaked = Encoding.UTF8.GetString(raw).Contains(probeKey, StringComparison.Ordinal);
            failures += Verdict(!leaked, "the stored file contains no plaintext",
                $"{raw.Length}-byte blob; plaintext present: {leaked}");

            var decrypted = System.Security.Cryptography.ProtectedData.Unprotect(
                raw[MoyuDpapiKeyStore.HeaderLength..],
                MoyuDpapiKeyStore.AdditionalEntropy,
                System.Security.Cryptography.DataProtectionScope.CurrentUser);
            var roundTripped = Encoding.UTF8.GetString(decrypted) == probeKey;
            failures += Verdict(roundTripped, "the blob is a real DPAPI(CurrentUser) ciphertext",
                "decrypts only for this Windows user, and only with the application's entropy");

            probeStore.Set(null);
            failures += Verdict(!File.Exists(probePath), "Set(null) clears the key", "file removed");
        }
        catch (Exception ex)
        {
            failures += Verdict(false, "DPAPI probe", $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try
            {
                if (Directory.Exists(scratch))
                {
                    Directory.Delete(scratch, recursive: true);
                }
            }
            catch
            {
                // Ignored.
            }
        }

        return failures;
    }

    // ---------------------------------------------------------------------------------------
    // 4. Downloads-folder takeover
    // ---------------------------------------------------------------------------------------

    private static int VerifyDownloadsFolderTakeover()
    {
        Section("Downloads-folder takeover (synthetic file, nothing downloaded)");

        var failures = 0;
        var scratch = Path.Combine(Path.GetTempPath(), "GalboxMoyuVerifier", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);

        try
        {
            const int sizeBytes = 4096;
            var filePath = Path.Combine(scratch, "verifier-synthetic-patch.rar");
            var watcher = new MoyuDownloadWatcher();

            var request = new MoyuDownloadWatchRequest
            {
                DirectoryPath = scratch,
                ExpectedSizeBytes = sizeBytes,
                PatchName = "verifier synthetic patch",
                Timeout = TimeSpan.FromSeconds(20),
                PollInterval = TimeSpan.FromMilliseconds(150),
                Options = new MoyuDownloadWatchOptions
                {
                    PollInterval = TimeSpan.FromMilliseconds(150),
                    StabilityWindow = TimeSpan.FromMilliseconds(300)
                }
            };

            var writer = Task.Run(async () =>
            {
                await Task.Delay(500).ConfigureAwait(false);
                var payload = new byte[sizeBytes];
                System.Security.Cryptography.RandomNumberGenerator.Fill(payload);
                await File.WriteAllBytesAsync(filePath, payload).ConfigureAwait(false);
            });

            var stopwatch = Stopwatch.StartNew();
            var outcome = watcher.WatchAsync(request, null, CancellationToken.None).GetAwaiter().GetResult();
            stopwatch.Stop();
            writer.GetAwaiter().GetResult();

            failures += Verdict(
                outcome.State == MoyuDownloadWatchState.Found
                && string.Equals(outcome.File, filePath, StringComparison.OrdinalIgnoreCase),
                "a newly downloaded file is adopted by size",
                $"State={outcome.State}, File={outcome.File ?? "(none)"}, {stopwatch.ElapsedMilliseconds} ms");

            Console.WriteLine($"         Reason: {outcome.Reason}");

            // A wrong-sized file must not be adopted.
            var strayDirectory = Path.Combine(scratch, "stray");
            Directory.CreateDirectory(strayDirectory);
            File.WriteAllBytes(Path.Combine(strayDirectory, "unrelated-download.rar"), new byte[64 * 1024]);

            var strayOutcome = watcher.WatchAsync(
                new MoyuDownloadWatchRequest
                {
                    DirectoryPath = strayDirectory,
                    ExpectedSizeBytes = 170L * 1024 * 1024,
                    Timeout = TimeSpan.FromMilliseconds(500),
                    PollInterval = TimeSpan.FromMilliseconds(100),
                    Options = request.Options
                },
                null,
                CancellationToken.None).GetAwaiter().GetResult();

            failures += Verdict(strayOutcome.State == MoyuDownloadWatchState.TimedOut,
                "a 64 KB file is NOT adopted when the provider declares 170 MB",
                $"State={strayOutcome.State}");

            // A watch that sees nothing ends by itself.
            var emptyDirectory = Path.Combine(scratch, "empty");
            Directory.CreateDirectory(emptyDirectory);
            var emptyOutcome = watcher.WatchAsync(
                new MoyuDownloadWatchRequest
                {
                    DirectoryPath = emptyDirectory,
                    ExpectedSizeBytes = 1024L * 1024 * 1024,
                    Timeout = TimeSpan.FromMilliseconds(600),
                    PollInterval = TimeSpan.FromMilliseconds(100),
                    Options = request.Options
                },
                null,
                CancellationToken.None).GetAwaiter().GetResult();

            failures += Verdict(emptyOutcome.State == MoyuDownloadWatchState.TimedOut,
                "a watch with nothing to find ends with TimedOut instead of running forever",
                $"State={emptyOutcome.State}");

            Console.WriteLine($"         Default download folder: {MoyuDownloadsFolder.Resolve()}");
        }
        catch (Exception ex)
        {
            failures += Verdict(false, "downloads-folder takeover", $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try
            {
                if (Directory.Exists(scratch))
                {
                    Directory.Delete(scratch, recursive: true);
                }
            }
            catch
            {
                // Ignored.
            }
        }

        return failures;
    }

    // ---------------------------------------------------------------------------------------
    // 5. Patch-engine handover
    // ---------------------------------------------------------------------------------------

    private static int VerifyPatchEngineHandover()
    {
        Section("Handover to the local patch engine");

        var engine = new PatchInstaller();
        var resource = new MoyuResource
        {
            Id = "6262",
            PatchId = "86",
            Name = "CLANNAD FULL VOICE 汉化补丁",
            Storage = "s3",
            SizeText = "17.953 MB",
            SizeBytes = MoyuSize.ParseBytes("17.953 MB"),
            LocalizationGroupName = "Key Fans Club",
            Types = new[] { "manual" },
            Languages = new[] { "zh-Hans" },
            Platforms = new[] { "windows" },
            WebUrl = "https://www.moyu.moe/patch/86/resources/6262"
        };

        var source = resource.ToPatchSourceInfo();
        Console.WriteLine($"         PatchSourceInfo : kind={source.Kind}, patch={source.PatchId}, resource={source.ResourceId}");
        Console.WriteLine($"         web_url         : {source.WebUrl}");
        Console.WriteLine($"         ExternalId      : {resource.ExternalId}");

        var failures = Verdict(
            source.Kind == "moyu-moe-v2" && source.PatchId == "86" && source.ResourceId == "6262",
            "a discovered resource produces the provider metadata the engine records in its manifest",
            "the layer hands over a path and metadata; the engine owns extraction, overwrite and rollback");

        // The type vocabulary, including the rule that a `save` resource never goes through the
        // overwrite path.
        var saveIsSave = MoyuPatchTypes.IsSaveType(new[] { "save" });
        var mixedWithSave = MoyuPatchTypes.IsSaveType(new[] { "manual", "save" });
        var manualIsNotSave = MoyuPatchTypes.IsSaveType(new[] { "manual" });
        failures += Verdict(saveIsSave && !manualIsNotSave && mixedWithSave,
            "a `save` resource is identifiable as such, and only a `save` one is",
            $"IsSaveType([save])={saveIsSave}, IsSaveType([manual])={manualIsNotSave}, "
            + $"IsSaveType([manual,save])={mixedWithSave} "
            + "(the engine rejects `save` by declared type; routing it is the save workstream's job)");

        Console.WriteLine($"         IPatchEngine instance: {engine.GetType().Name}");

        return failures;
    }

    // ---------------------------------------------------------------------------------------
    // 6. Optionally store a key
    // ---------------------------------------------------------------------------------------

    private static int SaveKey(string apiKey)
    {
        Section("Storing the key with DPAPI");

        var store = new MoyuDpapiKeyStore();
        store.Set(apiKey);

        var reloaded = new MoyuDpapiKeyStore();
        var ok = reloaded.IsConfigured && reloaded.Get() == apiKey;

        var failure = Verdict(ok, "the key round-trips through the encrypted store",
            $"path={store.StorePath}, configured={reloaded.IsConfigured}, "
            + $"fingerprint={IMoyuKeyStore.FingerprintOf(apiKey)} (the key itself is never printed)");

        return failure;
    }

    // ---------------------------------------------------------------------------------------
    // 7. The live call
    // ---------------------------------------------------------------------------------------

    private static async Task<int> VerifyLiveCallAsync(string anchorText)
    {
        Section("Live call against the official /v2/moyu face");

        var keyStore = new MoyuDpapiKeyStore();
        var options = MoyuOptions.FromKeyStore(keyStore);

        Console.WriteLine($"         Key      : {options.DescribeKey()}");
        Console.WriteLine($"         Base     : {options.BaseAddress}");
        Console.WriteLine($"         Pacing   : {options.MinRequestInterval.TotalSeconds:F1}s between requests, "
                          + $"{options.MaxRequestsPerMinute} per rolling minute");

        var anchor = MoyuRef.Parse(anchorText);
        if (anchor is null)
        {
            Console.WriteLine();
            Console.WriteLine($"  The anchor \"{anchorText}\" is not a legal moyu ref (only vndb:vXXXX and catalog:<id>).");
            Console.WriteLine("  Nothing was requested.");
            return 0;
        }

        if (!options.HasApiKey)
        {
            Console.WriteLine();
            Console.WriteLine("  SKIPPED - no nmk_ key is configured, so no request was sent.");
            Console.WriteLine("  (This is the correct behaviour, not a failure: a missing key must never be");
            Console.WriteLine("   reported as an empty result. Mint one free at https://developer.nextmoe.dev");
            Console.WriteLine("   and set GALBOX_MOYU_API_KEY, or pass --save-key \"nmk_live_...\".)");
            _passed++;
            return 0;
        }

        using var httpClient = new HttpClient { BaseAddress = MoyuOptions.DefaultBaseAddress };
        httpClient.DefaultRequestHeaders.Add("User-Agent", MoyuComplianceGuard.UserAgent);
        httpClient.Timeout = TimeSpan.FromSeconds(30);

        var api = new MoyuApi(new MoyuHttpClient(httpClient), options, new MoyuRateLimiter(options));

        Console.WriteLine($"         Requesting: GET /v2/moyu/patches?refs={anchor} (one request)");
        Console.WriteLine();

        var result = await api.FindPatchesAsync(new[] { anchor }, includeResources: true).ConfigureAwait(false);

        if (result.Failed)
        {
            Console.WriteLine($"  [FAIL] the live call did not succeed");
            Console.WriteLine($"         Code       : {result.Failure!.Code}");
            Console.WriteLine($"         Message    : {result.Failure.Message}");
            Console.WriteLine($"         HTTP status: {result.Failure.HttpStatus?.ToString() ?? "(none)"}");
            Console.WriteLine($"         Upstream   : {result.Failure.UpstreamCode ?? "(none)"}");
            Console.WriteLine($"         Retry-After: {result.Failure.RetryAfter?.TotalSeconds.ToString() ?? "(none)"}s");

            if (!string.IsNullOrEmpty(result.Failure.ResponseBody))
            {
                Console.WriteLine($"         Body       : {Trim(result.Failure.ResponseBody)}");
            }

            Console.WriteLine();
            Console.WriteLine("  This is a measured failure, reported as measured. It is not an empty result:");
            Console.WriteLine("  Galbox distinguishes 'no key', 'rejected key', 'quota spent', 'network down' and");
            Console.WriteLine("  'genuinely no patches' from each other, and never renders them as 0 rows.");
            return 1;
        }

        var batch = result.Value!;
        Console.WriteLine($"  [PASS] the face answered: {batch.Items.Count} patch page(s), "
                          + $"{batch.Missing.Count} anchor(s) in missing[]");

        foreach (var patch in batch.Items)
        {
            Console.WriteLine();
            Console.WriteLine($"    patch id          : {patch.Id}");
            Console.WriteLine($"    vndb_id           : {patch.VndbId ?? "(null)"}");
            Console.WriteLine($"    catalog_work_id   : {patch.CatalogWorkId ?? "(null)"}");
            Console.WriteLine($"    content_limit     : {patch.ContentLimit ?? "(null)"}");
            Console.WriteLine($"    release_date      : {patch.ReleaseDate ?? "(null)"}");
            Console.WriteLine($"    type              : [{string.Join(", ", patch.Types)}] -> "
                              + $"[{string.Join(", ", patch.TypeLabels)}]");
            Console.WriteLine($"    language/platform : [{string.Join(",", patch.Languages)}] / [{string.Join(",", patch.Platforms)}]");
            Console.WriteLine($"    resource_count    : {patch.ResourceCount}");
            Console.WriteLine($"    resource_updated  : {patch.ResourceUpdatedAt ?? "(null)"}");
            Console.WriteLine($"    web_url           : {patch.WebUrl ?? "(null)"}");

            Console.WriteLine($"    resources on this page ({patch.Resources.Count}):");
            foreach (var resource in patch.Resources)
            {
                Console.WriteLine($"      r{resource.Id}  \"{Trim(resource.DisplayName, 60)}\"");
                Console.WriteLine($"           size={resource.SizeText} ({resource.SizeBytes} bytes)  storage={resource.Storage}"
                                  + (resource.IsExternallyHosted ? "  [externally hosted: the page may need an extraction code]" : string.Empty));
                Console.WriteLine($"           hash={(string.IsNullOrEmpty(resource.Hash) ? "(empty)" : Trim(resource.Hash, 16) + "...")}"
                                  + $"  group=\"{Trim(resource.LocalizationGroupName, 40)}\"");
                Console.WriteLine($"           web_url={resource.WebUrl ?? "(null)"}");
                Console.WriteLine($"           ExternalId={resource.ExternalId}");
            }
        }

        if (batch.Missing.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("    missing[] (normal information, NOT an error - these anchors have no patch page):");
            foreach (var missing in batch.Missing)
            {
                Console.WriteLine($"      {missing}");
            }
        }

        // A second request only if the first one found a page with resources - still user-triggered
        // by this single run, and still inside the client's own pacing.
        var first = batch.Items.FirstOrDefault(p => p.ResourceCount > 0 && p.Resources.Count == 0);
        if (first is not null)
        {
            Console.WriteLine();
            Console.WriteLine($"         Requesting: GET /v2/moyu/patches/{first.Id}/resources (one more request)");

            var resources = await api.ListResourcesAsync(first.Id, limit: 50).ConfigureAwait(false);
            if (resources.Failed)
            {
                Console.WriteLine($"  [FAIL] {resources.Failure!.Code}: {resources.Failure.Message}");
                return 1;
            }

            Console.WriteLine($"  [PASS] {resources.Value!.Items.Count} resource(s)");
            _passed++;
        }

        Console.WriteLine();
        Console.WriteLine($"         Requests sent by this run: {api.RequestsSent}");
        if (!string.IsNullOrEmpty(api.LastRateLimitNotice))
        {
            Console.WriteLine($"         Rate/quota headers      : {api.LastRateLimitNotice}");
        }

        Console.WriteLine();
        Console.WriteLine("  To download one of these: open its web_url in a browser (MoyuBrowserLauncher does");
        Console.WriteLine("  exactly that), download the file, and let MoyuDownloadWatcher adopt it out of your");
        Console.WriteLine("  downloads folder. The public face deliberately carries no direct link.");

        _passed++;
        return 0;
    }

    private static string Trim(string? value, int max = 200)
        => string.IsNullOrEmpty(value) ? "(empty)"
            : value.Length <= max ? value
            : value[..max] + "...";
}
