using System.Diagnostics;
using System.Text;
using Galbox.Core.Api;
using Microsoft.Extensions.DependencyInjection;

namespace Galbox.Acceptance.Checks;

/// <summary>
/// A stub transport for the moyu checks.
///
/// <para>
/// It answers the documented <c>/v2/moyu</c> shapes from canned JSON and records every URI it was
/// asked for. Two things depend on it: the checks stay offline (no request ever leaves the machine),
/// and A63 can assert on the <b>paths that were actually requested</b> rather than on a constant.
/// </para>
/// </summary>
internal sealed class MoyuStubHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

    internal MoyuStubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

    /// <summary>Every request this handler was asked to send.</summary>
    internal List<(string Method, Uri Uri)> Requests { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Requests.Add((request.Method.Method, request.RequestUri!));
        return Task.FromResult(_responder(request));
    }

    /// <summary>
    /// Builds a client around this handler with production-shaped defaults.
    /// </summary>
    /// <param name="handler">The recording transport.</param>
    /// <param name="apiKey">
    /// Key to configure. Pass <c>null</c> to leave whatever the supplied options already carry, which
    /// is what a check that pre-configured a key wants; pass an empty string for a key-less client.
    /// </param>
    /// <param name="options">Options to mutate and use, or null for fresh ones.</param>
    /// <param name="minRequestInterval">
    /// Pacing to apply. Defaults to zero so a check that sends several requests does not spend real
    /// seconds waiting; A67 asserts the production pacing separately.
    /// </param>
    internal static MoyuApi BuildApi(
        MoyuStubHandler handler,
        string? apiKey = null,
        MoyuOptions? options = null,
        TimeSpan? minRequestInterval = null)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = MoyuOptions.DefaultBaseAddress };
        httpClient.DefaultRequestHeaders.Add("User-Agent", MoyuComplianceGuard.UserAgent);
        httpClient.Timeout = TimeSpan.FromSeconds(30);

        options ??= new MoyuOptions();
        if (apiKey is not null)
        {
            options.ApiKey = apiKey;
        }

        var limiter = new MoyuRateLimiter(minRequestInterval ?? TimeSpan.Zero, 1000);
        return new MoyuApi(new MoyuHttpClient(httpClient), options, limiter);
    }

    /// <summary>A JSON response with an optional ETag.</summary>
    internal static HttpResponseMessage Json(
        string body,
        string? etag = null,
        System.Net.HttpStatusCode status = System.Net.HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        if (etag is not null)
        {
            response.Headers.TryAddWithoutValidation("ETag", etag);
        }

        return response;
    }
}

/// <summary>
/// A60 - the moyu client is resolvable from the same DI container the shipping app builds, and the
/// typed HttpClient behind it is configured the way the integration requires: the official
/// <c>/v2/moyu</c> origin, an identifying User-Agent, and a bounded timeout. It also asserts that the
/// client holds the container's <b>shared</b> rate limiter, because a per-instance one would pace
/// nothing.
/// </summary>
public sealed class A60MoyuServiceResolutionCheck : IAcceptanceCheck
{
    /// <inheritdoc />
    public string Id => "A60";

    /// <inheritdoc />
    public string Title => "MoyuApi resolves from DI with the official /v2/moyu HttpClient configuration";

    /// <inheritdoc />
    public Task<CheckResult> RunAsync(AcceptanceContext context, CancellationToken cancellationToken)
    {
        var expected =
            "MoyuApi + IMoyuKeyStore + MoyuOptions + MoyuDownloadWatcher + MoyuBrowserLauncher resolve; "
            + "BaseAddress=https://api.nextmoe.dev/, UA identifies Galbox and carries a project URL, "
            + "Timeout in (0, 60]s, and MoyuApi shares the container's rate limiter";

        var details = new List<string>();
        var problems = new List<string>();

        void Report(string name, object? instance)
        {
            if (instance is null)
            {
                problems.Add($"{name} resolved to null");
                details.Add($"   [MISSING] {name}");
                return;
            }

            details.Add($"   [OK]      {name,-22} {instance.GetType().Name}");
        }

        details.Add("Resolved from the container:");
        var wrapper = context.Services.GetService<MoyuHttpClient>();
        var api = context.Services.GetService<MoyuApi>();
        var keyStore = context.Services.GetService<IMoyuKeyStore>();
        var options = context.Services.GetService<MoyuOptions>();
        var limiterFromContainer = context.Services.GetService<IMoyuRateLimiter>();
        var watcher = context.Services.GetService<MoyuDownloadWatcher>();
        var launcher = context.Services.GetService<MoyuBrowserLauncher>();

        Report(nameof(MoyuHttpClient), wrapper);
        Report(nameof(MoyuApi), api);
        Report(nameof(IMoyuKeyStore), keyStore);
        Report(nameof(MoyuOptions), options);
        Report(nameof(IMoyuRateLimiter), limiterFromContainer);
        Report(nameof(MoyuDownloadWatcher), watcher);
        Report(nameof(MoyuBrowserLauncher), launcher);

        if (wrapper is null || api is null || options is null)
        {
            return Task.FromResult(CheckResult.Fail(Id, Title, expected,
                $"required registrations missing: {string.Join("; ", problems)}").With(details.ToArray()));
        }

        var httpClient = wrapper.HttpClient;
        var baseAddress = httpClient.BaseAddress?.ToString() ?? "(null)";
        var userAgent = httpClient.DefaultRequestHeaders.UserAgent.ToString();

        details.Add(string.Empty);
        details.Add($"HttpClient.BaseAddress : {baseAddress}");
        details.Add($"HttpClient.User-Agent  : {(string.IsNullOrEmpty(userAgent) ? "(absent)" : userAgent)}");
        details.Add($"HttpClient.Timeout     : {httpClient.Timeout.TotalSeconds:F0}s");
        details.Add($"Key store              : {keyStore}");
        details.Add($"Key                    : {options.DescribeKey()} (source={options.ApiKeySource})");
        details.Add($"Pacing                 : {options.MinRequestInterval.TotalSeconds:F1}s between requests, "
                    + $"{options.MaxRequestsPerMinute} per rolling minute");
        details.Add($"Download folder        : {options.DownloadsFolder}");

        if (httpClient.BaseAddress is null
            || !string.Equals(httpClient.BaseAddress.Host, MoyuComplianceGuard.ApiHost, StringComparison.OrdinalIgnoreCase)
            || httpClient.BaseAddress.Scheme != Uri.UriSchemeHttps)
        {
            problems.Add($"BaseAddress must be https://{MoyuComplianceGuard.ApiHost}/, measured \"{baseAddress}\"");
        }

        // The site is a free community project. Its operator must be able to tell from a log line
        // what this traffic is and where to complain, so a bare "Galbox/1.0" is not enough here.
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            problems.Add("User-Agent is absent");
        }
        else
        {
            if (!userAgent.Contains("Galbox", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"User-Agent \"{userAgent}\" does not identify Galbox");
            }

            if (!userAgent.Contains("http", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"User-Agent \"{userAgent}\" carries no project URL, so its traffic is not attributable");
            }
        }

        if (httpClient.Timeout <= TimeSpan.Zero || httpClient.Timeout > TimeSpan.FromSeconds(60))
        {
            problems.Add($"Timeout {httpClient.Timeout.TotalSeconds:F0}s is outside the sane (0, 60]s window");
        }

        if (options.MinRequestInterval <= TimeSpan.Zero)
        {
            problems.Add("MinRequestInterval is zero; the client would not pace itself at all");
        }

        if (options.MaxRequestsPerMinute > 30)
        {
            problems.Add($"MaxRequestsPerMinute is {options.MaxRequestsPerMinute}, above the self-imposed ceiling of 30");
        }

        // The limiter MUST be the container's singleton: a per-call instance would enforce nothing.
        // MoyuApi exposes no public accessor for it on purpose (it is an implementation detail), so
        // the field is read reflectively here. That is a test-only liberty.
        var limiterField = typeof(MoyuApi).GetField(
            "_limiter",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        if (limiterField is null)
        {
            problems.Add("MoyuApi has no _limiter field; the pacing seam has changed and this check cannot see it");
        }
        else if (limiterField.GetValue(api) is not IMoyuRateLimiter sharedLimiter)
        {
            problems.Add("MoyuApi holds no rate limiter");
        }
        else if (ReferenceEquals(sharedLimiter, limiterFromContainer))
        {
            details.Add("MoyuApi shares the container's limiter instance : yes");
        }
        else if (limiterFromContainer is null)
        {
            problems.Add("IMoyuRateLimiter is not registered, so pacing would be per-instance");
        }
        else
        {
            problems.Add("MoyuApi holds a DIFFERENT limiter than the container's singleton, so pacing is not shared");
        }

        foreach (var problem in problems)
        {
            details.Add($"PROBLEM: {problem}");
        }

        if (problems.Count > 0)
        {
            return Task.FromResult(CheckResult.Fail(Id, Title, expected,
                $"{problems.Count} problem(s): {string.Join("; ", problems)}").With(details.ToArray()));
        }

        return Task.FromResult(CheckResult.Pass(Id, Title, expected,
            $"all 7 registrations resolve; BaseAddress={baseAddress}, UA=\"{userAgent}\", "
            + $"Timeout={httpClient.Timeout.TotalSeconds:F0}s, pacing={options.MinRequestInterval.TotalSeconds:F1}s/"
            + $"{options.MaxRequestsPerMinute} per minute on a shared limiter")
            .With(details.ToArray()));
    }
}

/// <summary>
/// A61 - the defect this block exists for: with no API key configured, the client must report a
/// distinct "not configured" failure <b>and send nothing</b>. The historical failure mode is a
/// silent empty result, which is indistinguishable from "this game genuinely has no patches".
///
/// <para>
/// The transport it drives would answer <c>200</c> with a valid payload, so a request that leaked
/// through would look like success — which is what makes "0 requests recorded" a strong assertion
/// rather than a tautology.
/// </para>
/// </summary>
public sealed class A61MoyuMissingKeyCheck : IAcceptanceCheck
{
    /// <inheritdoc />
    public string Id => "A61";

    /// <inheritdoc />
    public string Title => "No configured nmk_ key is reported as a distinct failure, never as an empty result";

    /// <inheritdoc />
    public async Task<CheckResult> RunAsync(AcceptanceContext context, CancellationToken cancellationToken)
    {
        var expected =
            "with no key configured, the call reports Failure.Code == NotConfigured with a message "
            + "naming the missing key, and sends ZERO HTTP requests";

        var details = new List<string>();
        var problems = new List<string>();

        context.Traffic.Clear();

        var containerOptions = context.Services.GetRequiredService<MoyuOptions>();
        details.Add($"Container MoyuOptions.ApiKey   : {containerOptions.DescribeKey()}");
        details.Add($"Container ApiKeySource         : {containerOptions.ApiKeySource}");
        details.Add($"Key store                      : {context.Services.GetRequiredService<IMoyuKeyStore>()}");

        var keylessOptions = MoyuOptions.FromKeyStore(new MoyuMemoryKeyStore());
        details.Add(string.Empty);
        details.Add($"Key-less options.HasApiKey     : {keylessOptions.HasApiKey}");
        details.Add($"Key-less options.DescribeKey() : {keylessOptions.DescribeKey()}");

        if (keylessOptions.HasApiKey)
        {
            return CheckResult.Fail(Id, Title, expected,
                "an empty key store produced a configured client, so this check cannot measure anything")
                .With(details.ToArray());
        }

        var handler = new MoyuStubHandler(_ => MoyuStubHandler.Json(
            """{"object":"list","items":[],"next_cursor":null,"total":0,"missing":[]}"""));

        var api = MoyuStubHandler.BuildApi(handler, apiKey: string.Empty, options: keylessOptions);

        var stopwatch = Stopwatch.StartNew();
        var result = await api
            .FindPatchesAsync(new[] { MoyuRef.Parse("vndb:v4")! }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        stopwatch.Stop();

        details.Add(string.Empty);
        details.Add($"Result.Failed              : {result.Failed}");
        details.Add($"Result.Failure.Code        : {result.Failure?.Code.ToString() ?? "(no failure - a silent success)"}");
        details.Add($"Result.Failure.Message     : {result.Failure?.Message ?? "(none)"}");
        details.Add($"Result.Failure.HttpStatus  : {result.Failure?.HttpStatus?.ToString() ?? "(none)"}");
        details.Add($"Result.Value               : {(result.Value is null ? "(null)" : $"{result.Value.Items.Count} row(s)")}");
        details.Add($"HTTP requests actually sent: {handler.Requests.Count}");
        details.Add($"Elapsed                    : {stopwatch.ElapsedMilliseconds} ms");

        if (!result.Failed)
        {
            problems.Add(result.Value is null
                ? "the call returned null instead of a failure"
                : $"the call returned a SUCCESS with {result.Value.Items.Count} row(s) while no key is configured");
        }

        if (result.Failure?.Code != MoyuFailureCode.NotConfigured)
        {
            problems.Add($"Failure.Code is {result.Failure?.Code.ToString() ?? "(none)"}, expected NotConfigured");
        }

        var message = result.Failure?.Message ?? string.Empty;
        if (string.IsNullOrWhiteSpace(message))
        {
            problems.Add("Failure.Message is empty, so the user cannot tell what to fix");
        }
        else if (!message.Contains("nmk_", StringComparison.OrdinalIgnoreCase)
                 && !message.Contains("密钥", StringComparison.Ordinal))
        {
            problems.Add($"Failure.Message does not name the missing key: \"{message}\"");
        }

        if (handler.Requests.Count > 0)
        {
            problems.Add($"{handler.Requests.Count} request(s) were sent without a key: "
                         + string.Join(", ", handler.Requests.Select(r => r.Uri.ToString())));
        }

        var containerTraffic = context.Traffic.Exchanges
            .Where(e => e.Url.Contains(MoyuComplianceGuard.ApiHost, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        details.Add($"Container moyu requests    : {containerTraffic.Length}");
        if (containerTraffic.Length > 0)
        {
            problems.Add($"the container's moyu pipeline sent {containerTraffic.Length} request(s) during a key-less call");
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
            $"Failed=true, Code=NotConfigured, 0 HTTP request(s) sent, {stopwatch.ElapsedMilliseconds} ms")
            .With(details.ToArray());
    }
}

/// <summary>
/// A62 - the <c>nmk_</c> key is sealed with DPAPI before it touches the disk: the plaintext must not
/// appear anywhere in the stored bytes, the blob must be a real
/// <see cref="System.Security.Cryptography.DataProtectionScope.CurrentUser"/> ciphertext bound to the
/// application's entropy, and no diagnostic surface may carry the secret.
/// </summary>
public sealed class A62MoyuKeyProtectionCheck : IAcceptanceCheck
{
    private const string ProbeKey = "nmk_live_A62DPAPIprobe_DO_NOT_USE_0000000000";

    /// <inheritdoc />
    public string Id => "A62";

    /// <inheritdoc />
    public string Title => "The nmk_ key is DPAPI-encrypted on disk and never stored in plaintext";

    /// <inheritdoc />
    public Task<CheckResult> RunAsync(AcceptanceContext context, CancellationToken cancellationToken)
    {
        var expected =
            "Set writes an opaque blob containing no plaintext, DPAPI(CurrentUser) with the app's "
            + "entropy decrypts it, a foreign entropy does not, Get() round-trips through a fresh "
            + "instance, Set(null) clears it, and no printable surface reveals the key";

        var details = new List<string>();
        var problems = new List<string>();

        var scratch = MoyuCheckSupport.NewScratchDirectory("dpapi");
        var storePath = Path.Combine(scratch, "secrets", "moyu-api-key.bin");

        try
        {
            var store = new MoyuDpapiKeyStore(storePath);
            details.Add($"Store type           : {store.GetType().FullName}");
            details.Add($"Kind                 : {store.Kind}");
            details.Add($"Store path           : {store.StorePath}");
            details.Add($"IsConfigured (empty) : {store.IsConfigured}");

            if (store.IsConfigured)
            {
                problems.Add("a fresh, empty store already reports configured");
            }

            store.Set(ProbeKey);

            if (!File.Exists(storePath))
            {
                return Task.FromResult(CheckResult.Fail(Id, Title, expected, $"Set did not create {storePath}")
                    .With(details.ToArray()));
            }

            var raw = File.ReadAllBytes(storePath);
            details.Add(string.Empty);
            details.Add($"File size            : {raw.Length} bytes");
            details.Add($"Header (hex)         : {Convert.ToHexString(raw.AsSpan(0, Math.Min(16, raw.Length)))}");

            var plaintextLeaked = ContainsBytes(raw, Encoding.UTF8.GetBytes(ProbeKey))
                                  || ContainsBytes(raw, Encoding.Unicode.GetBytes(ProbeKey))
                                  || Encoding.UTF8.GetString(raw).Contains(ProbeKey, StringComparison.Ordinal);

            details.Add($"Plaintext in file    : {plaintextLeaked}");
            if (plaintextLeaked)
            {
                problems.Add("the plaintext key is present in the stored file");
            }

            // Prove it is genuinely DPAPI rather than merely "not obviously the key".
            var dpapiRoundTrip = false;
            try
            {
                var plaintext = System.Security.Cryptography.ProtectedData.Unprotect(
                    raw[MoyuDpapiKeyStore.HeaderLength..],
                    MoyuDpapiKeyStore.AdditionalEntropy,
                    System.Security.Cryptography.DataProtectionScope.CurrentUser);
                dpapiRoundTrip = Encoding.UTF8.GetString(plaintext) == ProbeKey;
                details.Add($"DPAPI(CurrentUser) decrypt with the app's entropy : {dpapiRoundTrip}");
            }
            catch (Exception ex)
            {
                details.Add($"DPAPI(CurrentUser) decrypt threw: {ex.GetType().Name}: {ex.Message}");
            }

            if (!dpapiRoundTrip)
            {
                problems.Add("the stored blob is not a DPAPI(CurrentUser) ciphertext of the key under the app's entropy");
            }

            // A different entropy must NOT decrypt it: that is what stops another application
            // running as the same user from reading this file.
            var foreignEntropyWorked = false;
            try
            {
                System.Security.Cryptography.ProtectedData.Unprotect(
                    raw[MoyuDpapiKeyStore.HeaderLength..],
                    Encoding.UTF8.GetBytes("some-other-application"),
                    System.Security.Cryptography.DataProtectionScope.CurrentUser);
                foreignEntropyWorked = true;
            }
            catch (Exception)
            {
                // Expected.
            }

            details.Add($"Decryptable with foreign entropy                  : {foreignEntropyWorked}");
            if (foreignEntropyWorked)
            {
                problems.Add("the blob decrypts without the application's entropy");
            }

            var reloaded = new MoyuDpapiKeyStore(storePath);
            var readBack = reloaded.Get();

            details.Add(string.Empty);
            details.Add($"IsConfigured (after) : {reloaded.IsConfigured}");
            details.Add($"Get() after reload   : {(readBack is null ? "(null)" : readBack == ProbeKey ? "(equals the key that was set)" : "(DIFFERENT VALUE)")}");

            if (!reloaded.IsConfigured || readBack != ProbeKey)
            {
                problems.Add("the key did not round-trip through Set/Get");
            }

            // A truncated or foreign file must read as "not configured", never throw.
            var damagedPath = Path.Combine(scratch, "secrets", "damaged.bin");
            File.WriteAllBytes(damagedPath, new byte[] { 1, 2, 3, 4 });
            var damagedStore = new MoyuDpapiKeyStore(damagedPath);
            details.Add($"Damaged file: IsConfigured={damagedStore.IsConfigured}, Get()={(damagedStore.Get() is null ? "(null)" : "non-null")}");
            if (damagedStore.IsConfigured)
            {
                problems.Add("a 4-byte garbage file reads as a configured key");
            }

            // --- The diagnostic surface ------------------------------------------------------
            var fingerprint = IMoyuKeyStore.FingerprintOf(readBack ?? string.Empty);
            var memoryStore = new MoyuMemoryKeyStore(ProbeKey);
            var optionsWithKey = MoyuOptions.FromKeyStore(memoryStore);

            details.Add(string.Empty);
            details.Add($"Fingerprint                      : {fingerprint}");
            details.Add($"MoyuOptions.ToString()           : {optionsWithKey}");
            details.Add($"MoyuOptions.DescribeKey()        : {optionsWithKey.DescribeKey()}");
            details.Add($"MoyuDpapiKeyStore.ToString()     : {store}");
            details.Add($"MoyuMemoryKeyStore.ToString()    : {memoryStore}");

            foreach (var (label, text) in new[]
                     {
                         ("Fingerprint", fingerprint),
                         ("MoyuOptions.ToString()", optionsWithKey.ToString()),
                         ("MoyuOptions.DescribeKey()", optionsWithKey.DescribeKey()),
                         ("MoyuDpapiKeyStore.ToString()", store.ToString()),
                         ("MoyuMemoryKeyStore.ToString()", memoryStore.ToString())
                     })
            {
                if (text.Contains(ProbeKey, StringComparison.Ordinal))
                {
                    problems.Add($"{label} contains the plaintext key");
                }
            }

            if (fingerprint.Length > 16)
            {
                problems.Add($"the fingerprint is {fingerprint.Length} characters; it is meant to be a short digest");
            }

            if (IMoyuKeyStore.FingerprintOf(ProbeKey) != IMoyuKeyStore.FingerprintOf(ProbeKey + "-other"))
            {
                details.Add("Fingerprint differs for a different key : yes (it is a digest, not a constant)");
            }
            else
            {
                problems.Add("two different keys produced the same fingerprint");
            }

            // --- Clearing ---------------------------------------------------------------------
            reloaded.Set(null);
            details.Add(string.Empty);
            details.Add($"After Set(null): file exists = {File.Exists(storePath)}, IsConfigured = {reloaded.IsConfigured}");
            if (File.Exists(storePath) || reloaded.IsConfigured)
            {
                problems.Add("Set(null) did not clear the stored key");
            }

            foreach (var problem in problems)
            {
                details.Add($"PROBLEM: {problem}");
            }

            if (problems.Count > 0)
            {
                return Task.FromResult(CheckResult.Fail(Id, Title, expected,
                    $"{problems.Count} problem(s): {string.Join("; ", problems)}").With(details.ToArray()));
            }

            return Task.FromResult(CheckResult.Pass(Id, Title, expected,
                $"{raw.Length}-byte DPAPI blob, 0 plaintext occurrences, CurrentUser decrypt under the app's "
                + "entropy succeeds and a foreign entropy fails, round-trip and clear OK, no secret in any diagnostic")
                .With(details.ToArray()));
        }
        catch (Exception ex)
        {
            return Task.FromResult(CheckResult.Error(Id, Title, expected,
                $"unhandled {ex.GetType().Name}", ex).With(details.ToArray()));
        }
        finally
        {
            MoyuCheckSupport.TryDeleteDirectory(scratch);
        }
    }

    /// <summary>Substring search over bytes, in both encodings a plaintext key could appear in.</summary>
    private static bool ContainsBytes(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
        {
            return false;
        }

        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Small helpers shared by the A60+ checks.</summary>
internal static class MoyuCheckSupport
{
    /// <summary>Creates a throw-away directory under the system temp folder.</summary>
    internal static string NewScratchDirectory(string tag)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "GalboxMoyuAcceptance",
            $"{tag}-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Best-effort recursive delete; never throws.</summary>
    internal static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // A leftover temp directory is not a test result.
        }
    }
}
