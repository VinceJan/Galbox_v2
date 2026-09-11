using System.Text.Json;
using System.Text.Json.Serialization;

namespace Galbox.Core.Api;

/// <summary>
/// Why a metadata source could not answer.
/// </summary>
/// <remarks>
/// Every one of these used to collapse into "the source returned an empty list", which is the
/// defect class this project keeps rediscovering: a broken source is indistinguishable from a
/// source that legitimately has no data for the title. The point of this enum is that the three
/// interesting states - <see cref="NotConfigured"/>, a transport/HTTP failure, and a genuine
/// empty result (<see cref="None"/> with zero items) - can never be confused again.
/// </remarks>
public enum MetadataSourceFailureKind
{
    /// <summary>No failure: the request completed and the answer is real (possibly an empty one).</summary>
    None = 0,

    /// <summary>The source is not usable as configured (missing credential / missing endpoint).</summary>
    NotConfigured,

    /// <summary>The caller asked for something the source cannot express (blank or malformed id).</summary>
    InvalidRequest,

    /// <summary>The upstream rejected the credentials (HTTP 401 / ymgal code 401).</summary>
    Unauthorized,

    /// <summary>The credentials are valid but not permitted for this call (HTTP 403 / ymgal code 403).</summary>
    Forbidden,

    /// <summary>The upstream asked us to slow down (HTTP 429 / ymgal code 429).</summary>
    RateLimited,

    /// <summary>The request never reached a server (DNS, connect, TLS, timeout).</summary>
    NetworkError,

    /// <summary>The server answered with a status the client does not accept.</summary>
    HttpError,

    /// <summary>The server answered, but the body is not the JSON shape the client expects.</summary>
    MalformedResponse,

    /// <summary>The server answered with its own application-level error code.</summary>
    UpstreamError
}

/// <summary>
/// How the ymgal (月幕Galgame) client is configured.
/// </summary>
/// <remarks>
/// ymgal's open API is OAuth2 <c>client_credentials</c>. The documentation at
/// <c>https://www.ymgal.games/developer</c> publishes a shared public client
/// (<c>client_id=ymgal</c>, <c>client_secret=luna0327</c>) explicitly "供您使用" for reading
/// public archive data, so the source works with no user configuration at all. A dedicated
/// client id can be requested through the project's issue tracker; when one is supplied through
/// the environment, it takes precedence.
///
/// The environment variables are read here rather than from the settings database on purpose:
/// a secret does not belong in a file the app synchronises into <c>%LocalAppData%</c>, and the
/// scraping settings row is a user-visible entity the UI round-trips.
/// </remarks>
public sealed class YmgalEndpointOptions
{
    /// <summary>Overrides the API host (a mirror, or a test double).</summary>
    public const string BaseUrlVariable = "GALBOX_YMGAL_BASE_URL";

    /// <summary>Supplies a dedicated ymgal <c>client_id</c>.</summary>
    public const string ClientIdVariable = "GALBOX_YMGAL_CLIENT_ID";

    /// <summary>Supplies the matching ymgal <c>client_secret</c>.</summary>
    public const string ClientSecretVariable = "GALBOX_YMGAL_CLIENT_SECRET";

    /// <summary>Documented API host.</summary>
    public const string DefaultBaseUrl = "https://www.ymgal.games";

    /// <summary>The public <c>client_id</c> published in the ymgal developer documentation.</summary>
    public const string PublicClientId = "ymgal";

    /// <summary>The public <c>client_secret</c> published in the ymgal developer documentation.</summary>
    public const string PublicClientSecret = "luna0327";

    /// <summary>API host without a trailing slash.</summary>
    public string BaseUrl { get; init; } = DefaultBaseUrl;

    /// <summary>OAuth2 client id, or null when the configuration is incomplete.</summary>
    public string? ClientId { get; init; }

    /// <summary>OAuth2 client secret, or null when the configuration is incomplete.</summary>
    public string? ClientSecret { get; init; }

    /// <summary>True when <see cref="ClientId"/>/<see cref="ClientSecret"/> came from the environment.</summary>
    public bool UsesDedicatedClient { get; init; }

    /// <summary>Whether a request can be attempted at all.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl)
        && !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(ClientSecret);

    /// <summary>
    /// Human readable explanation of what is missing, naming the exact environment variable.
    /// Empty when <see cref="IsConfigured"/> is true.
    /// </summary>
    public string ConfigurationProblem
    {
        get
        {
            if (string.IsNullOrWhiteSpace(BaseUrl))
            {
                return $"ymgal endpoint is not configured: set {BaseUrlVariable} to an absolute URL "
                     + $"(default: {DefaultBaseUrl})";
            }

            if (string.IsNullOrWhiteSpace(ClientId))
            {
                return $"ymgal credentials are not configured: {ClientIdVariable} is empty. "
                     + $"Either provide both {ClientIdVariable} and {ClientSecretVariable} for a dedicated client, "
                     + $"or leave both unset to use ymgal's documented public client ({PublicClientId}).";
            }

            if (string.IsNullOrWhiteSpace(ClientSecret))
            {
                return $"ymgal credentials are not configured: {ClientSecretVariable} is empty. "
                     + $"Either provide both {ClientIdVariable} and {ClientSecretVariable} for a dedicated client, "
                     + $"or leave both unset to use ymgal's documented public client ({PublicClientId}).";
            }

            return string.Empty;
        }
    }

    /// <summary>
    /// Builds the configuration from the environment.
    /// </summary>
    /// <remarks>
    /// Fallback rule: when <b>neither</b> credential variable is set the documented public client
    /// is used, so the source works out of the box. As soon as <b>either</b> variable is set the
    /// caller is opting into their own client, and a half-filled pair is reported as
    /// <see cref="MetadataSourceFailureKind.NotConfigured"/> instead of silently falling back -
    /// quietly ignoring a credential the operator did configure is its own kind of lie.
    /// </remarks>
    public static YmgalEndpointOptions Resolve()
    {
        var baseUrl = Read(BaseUrlVariable);
        var clientId = Read(ClientIdVariable);
        var clientSecret = Read(ClientSecretVariable);

        var optedIn = clientId is not null || clientSecret is not null;

        return new YmgalEndpointOptions
        {
            BaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.Trim().TrimEnd('/'),
            ClientId = optedIn ? clientId : PublicClientId,
            ClientSecret = optedIn ? clientSecret : PublicClientSecret,
            UsesDedicatedClient = optedIn
        };
    }

    /// <summary>Configuration that deliberately has no credentials, for diagnostics and tests.</summary>
    public static YmgalEndpointOptions Unconfigured { get; } = new() { ClientId = null, ClientSecret = null };

    private static string? Read(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return value is null ? null : value.Trim();
    }
}

/// <summary>
/// How the cngal (CnGal 中文 Galgame 资料站) client is configured.
/// </summary>
/// <remarks>
/// The CnGal main-site API documented by <c>https://api.cngal.org/swagger/v1/swagger.json</c> is
/// fully open: no key, no token, no account. The only configuration is therefore the host, which
/// exists so a mirror can be used and so a broken endpoint can be exercised in a test.
/// </remarks>
public sealed class CngalEndpointOptions
{
    /// <summary>Overrides the API host (a mirror, or a test double).</summary>
    public const string BaseUrlVariable = "GALBOX_CNGAL_BASE_URL";

    /// <summary>Documented API host.</summary>
    public const string DefaultBaseUrl = "https://api.cngal.org";

    /// <summary>API host without a trailing slash.</summary>
    public string BaseUrl { get; init; } = DefaultBaseUrl;

    /// <summary>Whether a request can be attempted at all.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl);

    /// <summary>Human readable explanation of what is missing; empty when configured.</summary>
    public string ConfigurationProblem =>
        IsConfigured
            ? string.Empty
            : $"cngal endpoint is not configured: set {BaseUrlVariable} to an absolute URL (default: {DefaultBaseUrl}). "
              + "The CnGal API itself needs no key.";

    /// <summary>Builds the configuration from the environment.</summary>
    public static CngalEndpointOptions Resolve()
    {
        var baseUrl = Environment.GetEnvironmentVariable(BaseUrlVariable);
        return new CngalEndpointOptions
        {
            BaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.Trim().TrimEnd('/')
        };
    }

    /// <summary>Configuration that deliberately has no endpoint, for diagnostics and tests.</summary>
    public static CngalEndpointOptions Unconfigured { get; } = new() { BaseUrl = string.Empty };
}

/// <summary>
/// Reads a JSON value that the upstream may send as either a number or a string into a string.
/// </summary>
/// <remarks>
/// ymgal documents this explicitly: "若出现数字精度大于 2^53 的情况（比如有些模型是通过 SnowFlake
/// 算法生成的 ID），接口中返回的所有数字长整型（int64）都会转变为字符串". A 64-bit snowflake id
/// therefore arrives as <c>23682</c> today and as <c>"23682"</c> once it grows, and a client that
/// only binds one of the two shapes breaks silently on the day that happens.
/// </remarks>
internal sealed class StringOrNumberJsonConverter : JsonConverter<string>
{
    /// <inheritdoc />
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString() ?? string.Empty,
            JsonTokenType.Number => reader.TryGetInt64(out var value)
                ? value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : reader.GetDouble().ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            JsonTokenType.Null => string.Empty,
            _ => throw new JsonException($"Cannot read a metadata id from a {reader.TokenType} token.")
        };

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}
