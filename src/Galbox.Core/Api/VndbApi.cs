using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Galbox.Core.Api;

/// <summary>
/// API client for VNDB (Visual Novel Database).
/// Uses the new VNDB API (kana API).
/// </summary>
public class VndbApi : ApiClient
{
    private const string BaseUrl = "https://api.vndb.org/kana";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Creates a VNDB API client with a typed HttpClient wrapper.
    /// </summary>
    public VndbApi(VndbHttpClient httpClientWrapper) : base(httpClientWrapper.HttpClient)
    {
    }

    /// <summary>
    /// Search for visual novels by title.
    /// </summary>
    public async Task<VndbSearchResponse?> SearchByTitleAsync(
        string title,
        CancellationToken cancellationToken = default)
    {
        var request = new VndbSearchRequest
        {
            Filters = $"search ~ \"{title}\"",
            Fields = "id, title, titles, titles.lang, titles.title, titles.official, titles.main, image.url, rating, length_minutes"
        };

        return await PostJsonAsync<VndbSearchRequest, VndbSearchResponse>(
            $"{BaseUrl}/vn",
            request,
            cancellationToken);
    }

    /// <summary>
    /// Get detailed visual novel information by ID.
    /// </summary>
    public async Task<VndbVnResponse?> GetVnByIdAsync(
        string vnId,
        CancellationToken cancellationToken = default)
    {
        var request = new VndbSearchRequest
        {
            Filters = $"id = \"{vnId}\"",
            Fields = "id, title, titles, titles.lang, titles.title, titles.official, titles.main, image.url, rating, length_minutes, description, developers.name, tags.id, tags.name, tags.rating, characters.id, characters.name, characters.original, characters.image.url"
        };

        return await PostJsonAsync<VndbSearchRequest, VndbVnResponse>(
            $"{BaseUrl}/vn",
            request,
            cancellationToken);
    }
}

#region VNDB API Request/Response Models

public class VndbSearchRequest
{
    [JsonPropertyName("filters")]
    public string Filters { get; set; } = string.Empty;

    [JsonPropertyName("fields")]
    public string Fields { get; set; } = string.Empty;

    [JsonPropertyName("results")]
    public int Results { get; set; } = 10;
}

public class VndbSearchResponse
{
    [JsonPropertyName("more")]
    public bool More { get; set; }

    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("results")]
    public List<VndbVn>? Results { get; set; }
}

public class VndbVnResponse
{
    [JsonPropertyName("more")]
    public bool More { get; set; }

    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("results")]
    public List<VndbVn>? Results { get; set; }
}

public class VndbVn
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("titles")]
    public List<VndbTitle>? Titles { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("image")]
    public VndbImage? Image { get; set; }

    [JsonPropertyName("rating")]
    public double? Rating { get; set; }

    [JsonPropertyName("length_minutes")]
    public int? LengthMinutes { get; set; }

    [JsonPropertyName("developers")]
    public List<VndbDeveloper>? Developers { get; set; }

    [JsonPropertyName("tags")]
    public List<VndbTag>? Tags { get; set; }

    [JsonPropertyName("characters")]
    public List<VndbCharacter>? Characters { get; set; }

    public List<string> GetAllTitles()
    {
        var titles = new List<string>();
        if (!string.IsNullOrWhiteSpace(Title))
            titles.Add(Title);

        if (Titles != null)
        {
            foreach (var t in Titles)
            {
                if (!string.IsNullOrWhiteSpace(t.Title) && !titles.Contains(t.Title))
                    titles.Add(t.Title);
            }
        }
        return titles;
    }

    public string? GetChineseTitle()
    {
        return Titles?.FirstOrDefault(t => t.Lang == "zh" || t.Lang == "zh-Hans")?.Title
            ?? Titles?.FirstOrDefault(t => t.Lang?.StartsWith("zh") == true)?.Title;
    }

    public DateTime? GetReleaseDate()
    {
        // VNDB uses a different format for release date
        // This would need to be expanded based on actual API response
        return null;
    }
}

public class VndbTitle
{
    [JsonPropertyName("lang")]
    public string? Lang { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("official")]
    public bool Official { get; set; }

    [JsonPropertyName("main")]
    public bool Main { get; set; }
}

public class VndbImage
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }
}

public class VndbDeveloper
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

public class VndbTag
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("rating")]
    public double Rating { get; set; }
}

public class VndbCharacter
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("original")]
    public string? Original { get; set; }

    [JsonPropertyName("image")]
    public VndbImage? Image { get; set; }
}

#endregion