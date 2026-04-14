using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Galbox.Core.Api;

/// <summary>
/// Placeholder API client for ymgal (Yumei Galgame) database.
/// Note: This API needs further research. Currently provides stub implementations.
/// ymgal website: https://www.ymgal.games/
/// </summary>
public class YmgalApi : ApiClient
{
    // Placeholder - needs research for actual API endpoint
    // private const string BaseUrl = "https://www.ymgal.games";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Creates a ymgal API client with a typed HttpClient wrapper.
    /// </summary>
    /// <param name="httpClientWrapper">Typed HttpClient wrapper with User-Agent header configured</param>
    public YmgalApi(YmgalHttpClient httpClientWrapper) : base(httpClientWrapper.HttpClient)
    {
    }

    /// <summary>
    /// Search for games by title.
    /// Note: This is a stub implementation. Actual API structure needs research.
    /// </summary>
    /// <param name="title">Search title</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Search results (stub)</returns>
    public async Task<YmgalSearchResponse?> SearchAsync(
        string title,
        CancellationToken cancellationToken = default)
    {
        // Stub implementation - ymgal API structure needs research
        // Return empty result to indicate no data available yet
        await Task.CompletedTask; // No-op for stub implementation

        return new YmgalSearchResponse
        {
            Success = false,
            Message = "ymgal API integration pending - needs API research",
            Items = new List<YmgalGameItem>()
        };
    }

    /// <summary>
    /// Get game details by ID.
    /// Note: This is a stub implementation.
    /// </summary>
    /// <param name="gameId">ymgal game ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Game details (stub)</returns>
    public async Task<YmgalGameDetail?> GetGameAsync(
        string gameId,
        CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        return null; // Stub - no implementation yet
    }
}

/// <summary>
/// Placeholder API client for cngal (Chinese Galgame) database.
/// Note: This API needs further research. Currently provides stub implementations.
/// cngal website: https://www.cngal.org/
/// </summary>
public class CngalApi : ApiClient
{
    // Placeholder - needs research for actual API endpoint
    // private const string BaseUrl = "https://www.cngal.org";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Creates a cngal API client with a typed HttpClient wrapper.
    /// </summary>
    /// <param name="httpClientWrapper">Typed HttpClient wrapper with User-Agent header configured</param>
    public CngalApi(CngalHttpClient httpClientWrapper) : base(httpClientWrapper.HttpClient)
    {
    }

    /// <summary>
    /// Search for games by title.
    /// Note: This is a stub implementation. Actual API structure needs research.
    /// </summary>
    /// <param name="title">Search title</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Search results (stub)</returns>
    public async Task<CngalSearchResponse?> SearchAsync(
        string title,
        CancellationToken cancellationToken = default)
    {
        // Stub implementation - cngal API structure needs research
        await Task.CompletedTask;

        return new CngalSearchResponse
        {
            Success = false,
            Message = "cngal API integration pending - needs API research",
            Items = new List<CngalGameItem>()
        };
    }

    /// <summary>
    /// Get game details by ID.
    /// Note: This is a stub implementation.
    /// </summary>
    /// <param name="gameId">cngal game ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Game details (stub)</returns>
    public async Task<CngalGameDetail?> GetGameAsync(
        string gameId,
        CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        return null; // Stub - no implementation yet
    }
}

#region ymgal API Response Models (Stub)

/// <summary>
/// ymgal search response (stub model).
/// </summary>
public class YmgalSearchResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("items")]
    public List<YmgalGameItem> Items { get; set; } = new();
}

/// <summary>
/// ymgal game item (stub model).
/// </summary>
public class YmgalGameItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("title_cn")]
    public string? TitleCn { get; set; }

    [JsonPropertyName("cover")]
    public string? CoverUrl { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

/// <summary>
/// ymgal game detail (stub model).
/// </summary>
public class YmgalGameDetail : YmgalGameItem
{
    [JsonPropertyName("developer")]
    public string? Developer { get; set; }

    [JsonPropertyName("release_date")]
    public string? ReleaseDate { get; set; }

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = new();

    [JsonPropertyName("characters")]
    public List<YmgalCharacter> Characters { get; set; } = new();
}

/// <summary>
/// ymgal character (stub model).
/// </summary>
public class YmgalCharacter
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("image")]
    public string? ImageUrl { get; set; }
}

#endregion

#region cngal API Response Models (Stub)

/// <summary>
/// cngal search response (stub model).
/// </summary>
public class CngalSearchResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("items")]
    public List<CngalGameItem> Items { get; set; } = new();
}

/// <summary>
/// cngal game item ( stub model).
/// </summary>
public class CngalGameItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("title_cn")]
    public string? TitleCn { get; set; }

    [JsonPropertyName("cover")]
    public string? CoverUrl { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

/// <summary>
/// cngal game detail (stub model).
/// </summary>
public class CngalGameDetail : CngalGameItem
{
    [JsonPropertyName("developer")]
    public string? Developer { get; set; }

    [JsonPropertyName("release_date")]
    public string? ReleaseDate { get; set; }

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = new();

    [JsonPropertyName("characters")]
    public List<CngalCharacter> Characters { get; set; } = new();
}

/// <summary>
/// cngal character (stub model).
/// </summary>
public class CngalCharacter
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("image")]
    public string? ImageUrl { get; set; }
}

#endregion