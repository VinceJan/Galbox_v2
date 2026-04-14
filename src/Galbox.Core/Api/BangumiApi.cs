using System.Text.Json;
using System.Text.Json.Serialization;

namespace Galbox.Core.Api;

/// <summary>
/// API client for Bangumi (bgm.tv) - Chinese-focused game/anime database.
/// </summary>
public class BangumiApi : ApiClient
{
    private const string BaseUrl = "https://api.bgm.tv";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Creates a Bangumi API client with a typed HttpClient wrapper.
    /// </summary>
    public BangumiApi(BangumiHttpClient httpClientWrapper) : base(httpClientWrapper.HttpClient)
    {
    }

    /// <summary>
    /// Search for games by title.
    /// </summary>
    public async Task<BangumiSearchResponse?> SearchAsync(
        string title,
        int type = 4, // 4 = game
        CancellationToken cancellationToken = default)
    {
        var url = $"{BaseUrl}/search/subject/{Uri.EscapeDataString(title)}?type={type}&responseGroup=large";
        return await GetJsonAsync<BangumiSearchResponse>(url, cancellationToken);
    }

    /// <summary>
    /// Get detailed subject information by ID.
    /// </summary>
    public async Task<BangumiSubject?> GetSubjectAsync(
        int subjectId,
        CancellationToken cancellationToken = default)
    {
        var url = $"{BaseUrl}/v0/subjects/{subjectId}";
        return await GetJsonAsync<BangumiSubject>(url, cancellationToken);
    }

    /// <summary>
    /// Get characters for a subject.
    /// </summary>
    public async Task<List<BangumiCharacter>> GetCharactersAsync(
        int subjectId,
        CancellationToken cancellationToken = default)
    {
        var url = $"{BaseUrl}/v0/subjects/{subjectId}/characters";
        return await GetJsonAsync<List<BangumiCharacter>>(url, cancellationToken) ?? new();
    }

    /// <summary>
    /// Get tags for a subject.
    /// </summary>
    public async Task<List<BangumiTag>> GetTagsAsync(
        int subjectId,
        CancellationToken cancellationToken = default)
    {
        var url = $"{BaseUrl}/v0/subjects/{subjectId}/tags";
        return await GetJsonAsync<List<BangumiTag>>(url, cancellationToken) ?? new();
    }
}

#region Bangumi API Response Models

public class BangumiSearchResponse
{
    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("limit")]
    public int Limit { get; set; }

    [JsonPropertyName("offset")]
    public int Offset { get; set; }

    [JsonPropertyName("data")]
    public List<BangumiSearchItem>? Data { get; set; }

    public List<BangumiSearchItem> Items => Data ?? new();
}

public class BangumiSearchItem
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("name_cn")]
    public string? NameCn { get; set; }

    [JsonPropertyName("type")]
    public int Type { get; set; }

    [JsonPropertyName("summary")]
    public string? Summary { get; set; }

    [JsonPropertyName("images")]
    public BangumiImages? Images { get; set; }

    [JsonPropertyName("rating")]
    public BangumiRating? Rating { get; set; }

    [JsonPropertyName("info")]
    public List<BangumiInfoItem>? Info { get; set; }

    public List<string> GetAllTitles()
    {
        var titles = new List<string>();
        if (!string.IsNullOrWhiteSpace(Name))
            titles.Add(Name);
        if (!string.IsNullOrWhiteSpace(NameCn))
            titles.Add(NameCn);
        return titles;
    }
}

public class BangumiSubject
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("name_cn")]
    public string? NameCn { get; set; }

    [JsonPropertyName("summary")]
    public string? Summary { get; set; }

    [JsonPropertyName("images")]
    public BangumiImages? Images { get; set; }

    [JsonPropertyName("rating")]
    public BangumiRating? Rating { get; set; }

    [JsonPropertyName("infobox")]
    public List<BangumiInfoItem>? Infobox { get; set; }

    [JsonPropertyName("date")]
    public string? Date { get; set; }

    public DateTime? GetReleaseDate()
    {
        if (!string.IsNullOrWhiteSpace(Date))
        {
            if (DateTime.TryParse(Date, out var date))
                return date;
        }
        return null;
    }

    public string? GetDeveloper()
    {
        var developerItem = Infobox?.FirstOrDefault(i =>
            i.Key?.ToLowerInvariant() == "developer" ||
            i.Key?.ToLowerInvariant() == "制作公司");
        return developerItem?.Value?.ToString();
    }
}

public class BangumiImages
{
    [JsonPropertyName("small")]
    public string? Small { get; set; }

    [JsonPropertyName("grid")]
    public string? Grid { get; set; }

    [JsonPropertyName("large")]
    public string? Large { get; set; }

    [JsonPropertyName("medium")]
    public string? Medium { get; set; }

    [JsonPropertyName("common")]
    public string? Common { get; set; }
}

public class BangumiRating
{
    [JsonPropertyName("score")]
    public double Score { get; set; }

    [JsonPropertyName("total")]
    public int Total { get; set; }
}

public class BangumiInfoItem
{
    [JsonPropertyName("key")]
    public string? Key { get; set; }

    [JsonPropertyName("value")]
    public object? Value { get; set; }
}

public class BangumiCharacter
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("name_cn")]
    public string? NameCn { get; set; }

    [JsonPropertyName("images")]
    public BangumiImages? Images { get; set; }

    [JsonPropertyName("relation")]
    public string? Relation { get; set; }
}

public class BangumiTag
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("count")]
    public int Count { get; set; }
}

#endregion