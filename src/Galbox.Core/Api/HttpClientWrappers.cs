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