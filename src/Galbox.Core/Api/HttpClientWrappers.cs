namespace Galbox.Core.Api;

/// <summary>
/// Typed HttpClient wrapper for Bangumi API.
/// Provides type-safe dependency injection for the Bangumi API client.
/// </summary>
public class BangumiHttpClient
{
    /// <summary>
    /// Gets the underlying HttpClient instance.
    /// </summary>
    public HttpClient HttpClient { get; }

    /// <summary>
    /// Creates a Bangumi HttpClient wrapper.
    /// </summary>
    /// <param name="httpClient">HttpClient configured for Bangumi API</param>
    public BangumiHttpClient(HttpClient httpClient)
    {
        HttpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }
}

/// <summary>
/// Typed HttpClient wrapper for VNDB API.
/// Provides type-safe dependency injection for the VNDB API client.
/// </summary>
public class VndbHttpClient
{
    /// <summary>
    /// Gets the underlying HttpClient instance.
    /// </summary>
    public HttpClient HttpClient { get; }

    /// <summary>
    /// Creates a VNDB HttpClient wrapper.
    /// </summary>
    /// <param name="httpClient">HttpClient configured for VNDB API</param>
    public VndbHttpClient(HttpClient httpClient)
    {
        HttpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }
}

/// <summary>
/// Typed HttpClient wrapper for ymgal API.
/// Provides type-safe dependency injection for the ymgal API client.
/// </summary>
/// <remarks>
/// The wrapper is a plain container: it holds whatever <c>HttpClient</c> the DI container built
/// and configures nothing itself. The base address, User-Agent and timeout belong to the
/// <c>AddHttpClient&lt;YmgalHttpClient&gt;()</c> registration
/// (see <see cref="MetadataHttpClientDefaults.ConfigureYmgal"/>), which is the single place both
/// <c>App.xaml.cs</c> and the acceptance replica call so the two cannot drift apart.
/// </remarks>
public class YmgalHttpClient
{
    /// <summary>
    /// Gets the underlying HttpClient instance.
    /// </summary>
    public HttpClient HttpClient { get; }

    /// <summary>
    /// Creates a ymgal HttpClient wrapper.
    /// </summary>
    /// <param name="httpClient">HttpClient configured for ymgal API</param>
    public YmgalHttpClient(HttpClient httpClient)
    {
        HttpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }
}

/// <summary>
/// Typed HttpClient wrapper for cngal API.
/// Provides type-safe dependency injection for the cngal API client.
/// </summary>
/// <remarks>
/// See the note on <see cref="YmgalHttpClient"/>: configuration lives with the registration, not
/// here.
/// </remarks>
public class CngalHttpClient
{
    /// <summary>
    /// Gets the underlying HttpClient instance.
    /// </summary>
    public HttpClient HttpClient { get; }

    /// <summary>
    /// Creates a cngal HttpClient wrapper.
    /// </summary>
    /// <param name="httpClient">HttpClient configured for cngal API</param>
    public CngalHttpClient(HttpClient httpClient)
    {
        HttpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }
}

/// <summary>
/// The HttpClient configuration shared by the ymgal and cngal metadata sources.
/// </summary>
/// <remarks>
/// Both upstreams are community-run, free services. Both publish a base address that callers must
/// hit, and ymgal's developer notes ask for an identifiable caller and a bounded request rate. The
/// values therefore live in exactly one place, used by <c>App.xaml.cs</c> and by the acceptance
/// replica, so a change cannot land in one and be forgotten in the other.
/// </remarks>
public static class MetadataHttpClientDefaults
{
    /// <summary>Base address of the ymgal open API (absolute URLs are built from the options instead).</summary>
    public static readonly Uri YmgalBaseAddress = new(YmgalEndpointOptions.DefaultBaseUrl + "/");

    /// <summary>Base address of the CnGal main-site API.</summary>
    public static readonly Uri CngalBaseAddress = new(CngalEndpointOptions.DefaultBaseUrl + "/");

    /// <summary>
    /// Identifies this application to both sites, which matters for a site that rate limits and
    /// may need to contact a misbehaving caller.
    /// </summary>
    public const string UserAgent = "Galbox/1.0";

    /// <summary>Per-request timeout, matching the other metadata sources.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>Configures a HttpClient for the ymgal API.</summary>
    public static void ConfigureYmgal(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        client.BaseAddress ??= YmgalBaseAddress;
        client.Timeout = Timeout;
        if (!client.DefaultRequestHeaders.Contains("User-Agent"))
        {
            client.DefaultRequestHeaders.Add("User-Agent", UserAgent);
        }
    }

    /// <summary>Configures a HttpClient for the CnGal API.</summary>
    public static void ConfigureCngal(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        client.BaseAddress ??= CngalBaseAddress;
        client.Timeout = Timeout;
        if (!client.DefaultRequestHeaders.Contains("User-Agent"))
        {
            client.DefaultRequestHeaders.Add("User-Agent", UserAgent);
        }
    }
}

/// Typed HttpClient wrapper for the official NextMoe "moyu face" (<c>/v2/moyu</c>).
/// Provides type-safe dependency injection for <see cref="MoyuApi"/>.
/// </summary>
/// <remarks>
/// This is the only moyu HttpClient in the application, and its BaseAddress is
/// <c>https://api.nextmoe.dev/</c> — never the patch site itself. The site's own data endpoints
/// live under <c>/api</c>, which its <c>robots.txt</c> disallows; see
/// <see cref="MoyuComplianceGuard"/> for why that is the whole design of this integration.
/// </remarks>
public class MoyuHttpClient
{
    /// <summary>
    /// Gets the underlying HttpClient instance.
    /// </summary>
    public HttpClient HttpClient { get; }

    /// <summary>
    /// Creates a moyu HttpClient wrapper.
    /// </summary>
    /// <param name="httpClient">HttpClient configured for the NextMoe moyu face</param>
    public MoyuHttpClient(HttpClient httpClient)
    {
        HttpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }
}
