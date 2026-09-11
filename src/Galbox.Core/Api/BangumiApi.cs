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
    /// <remarks>
    /// D2 fix: this used to call the undocumented legacy endpoint
    /// <c>GET /search/subject/{kw}?type=4&amp;responseGroup=large</c>, which answers 200 with
    /// <c>{"results":N,"list":[...]}</c>. <see cref="BangumiSearchResponse"/> expects the v0 shape
    /// (<c>data/total/limit/offset</c>), so <c>Data</c> was always null and the source always
    /// returned zero items without reporting any error. The official v0 endpoint returns
    /// exactly the shape the model already describes.
    /// </remarks>
    public async Task<BangumiSearchResponse?> SearchAsync(
        string title,
        int type = 4, // 4 = game
        CancellationToken cancellationToken = default)
    {
        var request = new BangumiSearchRequest
        {
            Keyword = title,
            Filter = new BangumiSearchFilter { Type = new[] { type } }
        };

        return await PostJsonAsync<BangumiSearchRequest, BangumiSearchResponse>(
            $"{BaseUrl}/v0/search/subjects",
            request,
            cancellationToken);
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
    /// <remarks>
    /// Not usable: <c>GET /v0/subjects/{id}/tags</c> answers <b>404</b> for anonymous requests
    /// (verified against the live API), so this method always returns an empty list.
    /// Tag data is available from the search response instead
    /// (see <see cref="BangumiSearchItem.Tags"/>).
    /// </remarks>
    public async Task<List<BangumiTag>> GetTagsAsync(
        int subjectId,
        CancellationToken cancellationToken = default)
    {
        var url = $"{BaseUrl}/v0/subjects/{subjectId}/tags";
        return await GetJsonAsync<List<BangumiTag>>(url, cancellationToken) ?? new();
    }
}

#region Bangumi API Response Models

/// <summary>
/// Request body for <c>POST /v0/search/subjects</c>.
/// </summary>
public class BangumiSearchRequest
{
    [JsonPropertyName("keyword")]
    public string Keyword { get; set; } = string.Empty;

    [JsonPropertyName("filter")]
    public BangumiSearchFilter Filter { get; set; } = new();

    [JsonPropertyName("sort")]
    public string? Sort { get; set; }
}

/// <summary>
/// Subject type filter, e.g. <c>{"type":[4]}</c> for games.
/// </summary>
public class BangumiSearchFilter
{
    [JsonPropertyName("type")]
    public int[] Type { get; set; } = new[] { 4 };
}

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

    /// <summary>
    /// Infobox entries. D18 fix: the JSON key is <c>infobox</c>, the previous mapping used
    /// <c>info</c> which no Bangumi endpoint ever returns, so this was a permanently dead field.
    /// </summary>
    [JsonPropertyName("infobox")]
    public List<BangumiInfoItem>? Infobox { get; set; }

    [JsonPropertyName("date")]
    public string? Date { get; set; }

    /// <summary>
    /// Subject tags as returned by the v0 search response (ordered by popularity).
    /// This is the only working source of Bangumi tags - the dedicated
    /// <c>/v0/subjects/{id}/tags</c> endpoint returns 404.
    /// </summary>
    [JsonPropertyName("tags")]
    public List<BangumiTag>? Tags { get; set; }

    public List<string> GetAllTitles()
    {
        var titles = new List<string>();
        if (!string.IsNullOrWhiteSpace(Name))
            titles.Add(Name);
        if (!string.IsNullOrWhiteSpace(NameCn))
            titles.Add(NameCn);
        return titles;
    }

    /// <summary>
    /// Returns the most popular tag names.
    /// </summary>
    /// <param name="maxCount">Maximum number of tags to keep.</param>
    public List<string> GetTopTags(int maxCount = 15)
    {
        if (Tags == null || Tags.Count == 0)
        {
            return new List<string>();
        }

        return Tags
            .Where(t => !string.IsNullOrWhiteSpace(t.Name))
            .OrderByDescending(t => t.Count)
            .Take(maxCount)
            .Select(t => t.Name)
            .ToList();
    }

    /// <summary>
    /// Developer / publisher taken from the search result infobox, if the endpoint returned one.
    /// </summary>
    public string? GetDeveloper() => BangumiInfoboxReader.GetValue(Infobox, BangumiInfoboxReader.DeveloperKeys);

    /// <summary>
    /// Release date parsed from the search result, if available.
    /// </summary>
    public DateTime? GetReleaseDate() => BangumiInfoboxReader.ParseDate(Date);
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
        return BangumiInfoboxReader.ParseDate(Date);
    }

    /// <summary>
    /// Developer / publisher.
    /// </summary>
    /// <remarks>
    /// D5+D6 fixes: the real Bangumi infobox key is <c>开发</c> (there is no <c>developer</c> or
    /// <c>制作公司</c> key on any subject), and the value can be an array of
    /// <c>{"k":..,"v":..}</c> objects. The old code looked up the wrong keys and then called
    /// <c>ToString()</c> on the raw <c>JsonElement</c>, which yields the JSON text
    /// (e.g. <c>[{"v":"Key"}]</c>) instead of the value.
    /// </remarks>
    public string? GetDeveloper() => BangumiInfoboxReader.GetValue(Infobox, BangumiInfoboxReader.DeveloperKeys);
}

/// <summary>
/// Reads typed values out of a Bangumi infobox list.
/// </summary>
internal static class BangumiInfoboxReader
{
    /// <summary>
    /// Infobox keys that carry the developer/publisher, in priority order.
    /// <c>开发</c> is the key Bangumi actually uses; the others are kept for compatibility.
    /// </summary>
    internal static readonly string[] DeveloperKeys = { "开发", "开发商", "developer", "制作公司", "发行" };

    /// <summary>
    /// Returns the first non-empty value found for any of the supplied keys.
    /// </summary>
    internal static string? GetValue(List<BangumiInfoItem>? infobox, params string[] keys)
    {
        if (infobox == null || infobox.Count == 0 || keys.Length == 0)
        {
            return null;
        }

        foreach (var key in keys)
        {
            var item = infobox.FirstOrDefault(i =>
                !string.IsNullOrWhiteSpace(i.Key) &&
                string.Equals(i.Key!.Trim(), key, StringComparison.OrdinalIgnoreCase));

            var value = ReadValue(item?.Value);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>
    /// Converts an infobox value (string, array of {k,v}, or nested object) into a display string.
    /// </summary>
    internal static string? ReadValue(object? value)
    {
        switch (value)
        {
            case null:
                return null;

            case string s:
                return string.IsNullOrWhiteSpace(s) ? null : s.Trim();

            case JsonElement element:
                return ReadJsonElement(element);

            case System.Collections.IEnumerable enumerable:
                {
                    var parts = new List<string>();
                    foreach (var entry in enumerable)
                    {
                        var part = ReadValue(entry);
                        if (!string.IsNullOrWhiteSpace(part))
                        {
                            parts.Add(part);
                        }
                    }
                    return parts.Count > 0 ? string.Join(" / ", parts) : null;
                }

            default:
                return value.ToString();
        }
    }

    private static string? ReadJsonElement(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var text = element.GetString();
                return string.IsNullOrWhiteSpace(text) ? null : text.Trim();

            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                return element.ToString();

            case JsonValueKind.Array:
                {
                    var parts = new List<string>();
                    foreach (var entry in element.EnumerateArray())
                    {
                        var part = ReadJsonElement(entry);
                        if (!string.IsNullOrWhiteSpace(part))
                        {
                            parts.Add(part);
                        }
                    }
                    return parts.Count > 0 ? string.Join(" / ", parts) : null;
                }

            case JsonValueKind.Object:
                // Typed infobox entries look like {"k":"Steam","v":"https://..."} or {"v":"PC"}.
                if (element.TryGetProperty("v", out var v) && v.ValueKind != JsonValueKind.Null)
                {
                    return ReadJsonElement(v);
                }
                return null;

            default:
                return null;
        }
    }

    /// <summary>
    /// Parses Bangumi date strings ("2022-04-28", "2022-04", "2022") into a DateTime.
    /// </summary>
    internal static DateTime? ParseDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var value = raw.Trim();

        if (DateTime.TryParseExact(value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var exact))
        {
            return exact;
        }

        if (DateTime.TryParseExact(value, "yyyy-MM", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var monthOnly))
        {
            return monthOnly;
        }

        if (int.TryParse(value, out var year) && year is > 1900 and < 2200)
        {
            return new DateTime(year, 1, 1);
        }

        return DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
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