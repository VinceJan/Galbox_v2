namespace Galbox.Core.Api;

/// <summary>
/// Everything the moyu client needs that is not the HttpClient: the key, the pacing, and the
/// headless defaults.
///
/// <para>
/// <b>Bring your own key.</b> Galbox ships no <c>nmk_</c> key. The user mints one for free at
/// <c>https://developer.nextmoe.dev</c>; it then identifies *them* to the service and is spent
/// against *their* quota. A key compiled into the product would be a shared secret that leaks on
/// the first release and burns somebody else's budget.
/// </para>
///
/// <para>
/// <b>The key never appears in a log, a message or a diagnostic.</b> <see cref="ApiKey"/> has a
/// setter and no getter-side formatting: <see cref="ToString"/> deliberately describes the key's
/// <i>presence and fingerprint</i> only. Anything that needs to show something to the user should
/// use <see cref="DescribeKey"/>.
/// </para>
/// </summary>
public sealed class MoyuOptions
{
    /// <summary>
    /// Environment variable that overrides the API key wholesale. Intended for a developer's own
    /// machine and for CI; the shipping application does not set it.
    /// </summary>
    public const string ApiKeyEnvironmentVariable = "GALBOX_MOYU_API_KEY";

    /// <summary>
    /// Default origin of the official public face. The client's HttpClient BaseAddress carries the
    /// trailing slash; requests are built relative to it.
    /// </summary>
    public static readonly Uri DefaultBaseAddress = new("https://api.nextmoe.dev/");

    private string? _apiKey;

    /// <summary>
    /// The <c>nmk_live_…</c> / <c>nmk_test_…</c> application key, or <c>null</c> when unconfigured.
    /// Set once at startup from <see cref="IMoyuKeyStore"/>.
    /// </summary>
    public string? ApiKey
    {
        get => _apiKey;
        set => _apiKey = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>
    /// Where the key came from, for the diagnostics log. One of <c>none</c>, <c>environment</c>,
    /// <c>windows-dpapi</c>. Never the value itself.
    /// </summary>
    public string ApiKeySource { get; set; } = "none";

    /// <summary>API origin. Must satisfy <see cref="MoyuComplianceGuard.IsAllowedApiUri"/> for the root.</summary>
    public Uri BaseAddress { get; set; } = DefaultBaseAddress;

    /// <summary>True when a key is configured, i.e. requests may be attempted at all.</summary>
    public bool HasApiKey => !string.IsNullOrEmpty(_apiKey);

    /// <summary>
    /// Minimum spacing between two requests. The site is a free community project; one user cannot
    /// generate useful traffic faster than this, and pacing is cheap.
    /// </summary>
    public TimeSpan MinRequestInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Requests allowed in any rolling minute, on top of <see cref="MinRequestInterval"/>.</summary>
    public int MaxRequestsPerMinute { get; set; } = 30;

    /// <summary>
    /// Longest the client is willing to *wait inside a call* for its own rate limiter. Beyond this
    /// the call returns <see cref="MoyuFailureCode.ClientThrottled"/> instead of blocking a UI-bound
    /// thread for an unbounded time.
    /// </summary>
    public TimeSpan MaxRateLimitWait { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How many times a transient failure (5xx / 429 / transport) is retried.</summary>
    public int MaxRetries { get; set; } = 2;

    /// <summary>
    /// Upper bound applied to an upstream <c>Retry-After</c> before the client gives up and reports
    /// <see cref="MoyuFailureCode.RateLimited"/>. The report's §6.2 finding: the shared
    /// <c>ApiClient</c> retry loop ignores <c>Retry-After</c> entirely, so this client reads it and
    /// refuses to sit on a long one.
    /// </summary>
    public TimeSpan MaxHonouredRetryAfter { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Whether a successful response is cached in memory and revalidated with <c>If-None-Match</c>.</summary>
    public bool EnableConditionalRequests { get; set; } = true;

    /// <summary>
    /// How long an ETag entry is trusted before it is dropped. The service advertises
    /// <c>Cache-Control: public, max-age=300</c>, so five minutes matches its own stated freshness.
    /// </summary>
    public TimeSpan CacheEntryLifetime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>User-Agent sent on every request.</summary>
    public string UserAgent { get; set; } = MoyuComplianceGuard.UserAgent;

    /// <summary>
    /// Folder adopted as the browser's download destination. Resolved lazily so that overriding it
    /// later (and the watcher reading it) always sees the same value.
    /// </summary>
    public string DownloadsFolder { get; set; } = MoyuDownloadsFolder.Resolve();

    /// <summary>
    /// Builds options for the supplied key store, reading the key (and nothing else) from it.
    ///
    /// <para>
    /// This is what both <c>App.xaml.cs</c> and the acceptance replica call, so the shipping app and
    /// the harness cannot drift on where the key comes from.
    /// </para>
    /// </summary>
    public static MoyuOptions FromKeyStore(IMoyuKeyStore? keyStore)
    {
        var options = new MoyuOptions();

        var fromEnvironment = Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            options.ApiKey = fromEnvironment;
            options.ApiKeySource = "environment";
            return options;
        }

        var fromStore = keyStore?.Get();
        if (!string.IsNullOrWhiteSpace(fromStore))
        {
            options.ApiKey = fromStore;
            options.ApiKeySource = keyStore!.Kind;
        }

        return options;
    }

    /// <summary>
    /// A description of the configured key that is safe to log or display: presence, origin and a
    /// short one-way fingerprint. Never the key.
    /// </summary>
    public string DescribeKey()
    {
        if (!HasApiKey)
        {
            return "not configured";
        }

        var fingerprint = IMoyuKeyStore.FingerprintOf(_apiKey!);
        return $"configured (source={ApiKeySource}, fingerprint={fingerprint})";
    }

    /// <inheritdoc />
    /// <remarks>
    /// Overridden so that an accidental <c>log.Info(options)</c> or a debugger dump cannot leak the
    /// key through the default property reflection.
    /// </remarks>
    public override string ToString() =>
        $"MoyuOptions {{ ApiKey = {DescribeKey()}, BaseAddress = {BaseAddress}, "
        + $"Pacing = {MinRequestInterval.TotalSeconds:F1}s / {MaxRequestsPerMinute} per minute }}";
}
