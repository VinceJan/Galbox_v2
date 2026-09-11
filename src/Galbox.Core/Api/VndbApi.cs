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

    /// <summary>
    /// Field list for search requests. Nested objects must be written with braces
    /// (<c>titles{...}</c>) - a bare <c>titles</c> / <c>developers</c> / <c>tags</c> member is a hard 400.
    /// </summary>
    private const string SearchFields =
        "id, title, alttitle, titles{lang,title,official,main}, image.url, rating, released, length_minutes";

    /// <summary>
    /// Field list for single-VN detail requests (search fields plus description / developers / tags / characters).
    /// </summary>
    private const string DetailFields =
        "id, title, alttitle, titles{lang,title,official,main}, image.url, rating, released, length_minutes, " +
        "description, developers{id,name}, tags{id,name,rating}, characters{id,name,original,image.url}";

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
    /// <remarks>
    /// D3/D4 fixes. The request used to be
    /// <c>{"filters":"search ~ \"...\"","fields":"id, title, titles, titles.lang, ..."}</c>,
    /// which the server rejects with HTTP 400 twice over:
    /// <list type="bullet">
    ///   <item><description><c>filters</c> must be a JSON <b>array</b>, not a string
    ///     (<c>Invalid 'filters' member: Trailing garbage</c>).</description></item>
    ///   <item><description>the <c>search</c> filter requires the <c>=</c> operator, not <c>~</c>.</description></item>
    ///   <item><description>a bare <c>titles</c> member is invalid
    ///     (<c>Invalid 'fields' member: The 'titles' object requires specifying sub-field(s).</c>);
    ///     nested objects need brace syntax.</description></item>
    /// </list>
    /// </remarks>
    public async Task<VndbSearchResponse?> SearchByTitleAsync(
        string title,
        CancellationToken cancellationToken = default)
    {
        var request = new VndbSearchRequest
        {
            Filters = new object[] { "search", "=", title },
            Fields = SearchFields
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
            Filters = new object[] { "id", "=", vnId },
            Fields = DetailFields
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
    /// <summary>
    /// VNDB filter tree. Must serialize as a JSON array, e.g. <c>["search","=","CLANNAD"]</c>.
    /// </summary>
    [JsonPropertyName("filters")]
    public object[] Filters { get; set; } = Array.Empty<object>();

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

    /// <summary>
    /// Alternative main title (usually the Japanese one).
    /// </summary>
    [JsonPropertyName("alttitle")]
    public string? Alttitle { get; set; }

    /// <summary>
    /// Release date as reported by VNDB: "yyyy-MM-dd", "yyyy-MM" or "yyyy" (empty when unannounced).
    /// </summary>
    [JsonPropertyName("released")]
    public string? Released { get; set; }

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

        if (!string.IsNullOrWhiteSpace(Alttitle) && !titles.Contains(Alttitle!))
            titles.Add(Alttitle!);

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

    /// <summary>
    /// Parses the VNDB <c>released</c> string.
    /// </summary>
    /// <remarks>
    /// D15 fix: this method used to hard-code <c>return null</c> and <c>released</c> was never
    /// requested in <c>fields</c>, so the release date was permanently unavailable.
    /// VNDB dates can be partial ("2022", "2022-04"), which are widened to the first of the period.
    /// </remarks>
    public DateTime? GetReleaseDate()
    {
        if (string.IsNullOrWhiteSpace(Released))
        {
            return null;
        }

        var value = Released.Trim();
        const System.Globalization.DateTimeStyles styles = System.Globalization.DateTimeStyles.None;
        var culture = System.Globalization.CultureInfo.InvariantCulture;

        if (DateTime.TryParseExact(value, "yyyy-MM-dd", culture, styles, out var exact))
            return exact;

        if (DateTime.TryParseExact(value, "yyyy-MM", culture, styles, out var monthOnly))
            return monthOnly;

        if (int.TryParse(value, out var year) && year is > 1900 and < 2200)
            return new DateTime(year, 1, 1);

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