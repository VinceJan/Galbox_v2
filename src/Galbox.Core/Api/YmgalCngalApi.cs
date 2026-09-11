using System.Globalization;
using System.Text.Json.Serialization;

namespace Galbox.Core.Api;

/// <summary>
/// API client for ymgal (月幕Galgame) - https://www.ymgal.games/.
/// </summary>
/// <remarks>
/// <para>
/// Contract established against the live service and the official documentation at
/// <c>https://www.ymgal.games/developer</c>. Every request shape below was executed during
/// development; the samples are real responses, not reconstructions.
/// </para>
/// <list type="bullet">
///   <item><description><b>Auth.</b> OAuth2 client-credentials.
///     <c>GET /oauth/token?grant_type=client_credentials&amp;client_id=..&amp;client_secret=..&amp;scope=public</c>
///     answers <c>{"access_token":"..","token_type":"bearer","expires_in":3184,"scope":"public"}</c>.
///     A rejected client answers <c>{"error":"invalid_client","error_description":".."}</c>.
///     Tokens last one hour; the documentation asks callers <i>not</i> to poll on a timer but to
///     react to a rejection, which is what <see cref="AcquireTokenAsync"/> does (it reuses the
///     cached token and only re-authenticates when it is missing or expired).</description></item>
///   <item><description><b>Headers.</b> Every API call needs <c>version: 1</c>,
///     <c>Accept: application/json;charset=utf-8</c> and <c>Authorization: Bearer &lt;token&gt;</c>.
///     Without a token the service answers <b>HTTP 401</b> <c>{"success":false,"code":401,"msg":"No token"}</c>
///     (verified against the live endpoint).</description></item>
///   <item><description><b>Envelope.</b> <c>{"success":bool,"code":int,"msg":string,"data":..}</c>.
///     Application errors keep HTTP 200 and put the reason in <c>code</c>/<c>msg</c>; only 401/403
///     change the HTTP status. <c>code == 0</c> is success.</description></item>
///   <item><description><b>Search.</b> <c>GET /open/archive/search-game?mode=list&amp;keyword=..&amp;pageNum=1&amp;pageSize=1..20</c>,
///     whose <c>data</c> is a page <c>{result:[],total,hasNext,pageNum,pageSize}</c>.
///     A title that genuinely does not exist answers <c>success:true,code:0</c> with an empty
///     <c>result</c> - that is exactly the shape <see cref="YmgalSearchResponse.Success"/> preserves.</description></item>
///   <item><description><b>Detail.</b> <c>GET /open/archive?gid=&lt;id&gt;</c>, whose <c>data</c> is
///     <c>{game:{..},cidMapping:{..},pidMapping:{..}}</c>.</description></item>
/// </list>
/// <para>
/// <b>Rate limiting.</b> The documentation states the API is rate limited and asks callers to
/// avoid concurrent or looping access ("API访问有速率限制以便控制服务器资源，请避免使用并发、循环等调用方式").
/// No numeric quota is published, so requests are serialised behind
/// <see cref="MinimumRequestInterval"/> rather than fired in parallel, and the User-Agent carries
/// the application name so the operators can see who is calling.
/// </para>
/// </remarks>
public class YmgalApi : ApiClient
{
    /// <summary>Documented API version, sent as the mandatory <c>version</c> header.</summary>
    private const string ApiVersion = "1";

    private const string TokenPath = "/oauth/token";
    private const string SearchPath = "/open/archive/search-game";
    private const string ArchivePath = "/open/archive";

    /// <summary>OAuth2 scope covering the public archive endpoints.</summary>
    private const string Scope = "public";

    /// <summary>ymgal refuses keywords of 96 characters or more.</summary>
    private const int MaximumKeywordLength = 96;

    /// <summary>Minimum spacing between two requests this process sends to ymgal.</summary>
    private static readonly TimeSpan MinimumRequestInterval = TimeSpan.FromMilliseconds(1200);

    private static readonly SemaphoreSlim RequestGate = new(1, 1);
    private static readonly SemaphoreSlim TokenGate = new(1, 1);
    private static readonly object TokenLock = new();

    private static string? _cachedToken;
    private static string? _cachedTokenClientId;
    private static DateTimeOffset _cachedTokenExpiresAt = DateTimeOffset.MinValue;
    private static DateTimeOffset _lastRequestAt = DateTimeOffset.MinValue;

    private readonly YmgalEndpointOptions _options;

    /// <summary>
    /// Creates a ymgal API client with a typed HttpClient wrapper, using the credentials and host
    /// resolved from the environment (see <see cref="YmgalEndpointOptions.Resolve"/>).
    /// </summary>
    /// <param name="httpClientWrapper">Typed HttpClient wrapper with User-Agent header configured</param>
    public YmgalApi(YmgalHttpClient httpClientWrapper) : this(httpClientWrapper, YmgalEndpointOptions.Resolve())
    {
    }

    /// <summary>
    /// Creates a ymgal API client with an explicit configuration.
    /// </summary>
    /// <param name="httpClientWrapper">Typed HttpClient wrapper with User-Agent header configured</param>
    /// <param name="options">Credentials and host to use</param>
    public YmgalApi(YmgalHttpClient httpClientWrapper, YmgalEndpointOptions options)
        : base(httpClientWrapper.HttpClient)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>The configuration this instance was built with.</summary>
    public YmgalEndpointOptions Options => _options;

    /// <summary>Failure kind of the most recent call, mirroring <see cref="ApiClient.LastError"/>.</summary>
    public MetadataSourceFailureKind LastFailureKind { get; private set; }

    /// <summary>
    /// Searches the ymgal archive by title (or Chinese title).
    /// </summary>
    /// <param name="title">Query keyword. ymgal rejects keywords of 96 characters or more.</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>
    /// A response that always describes what happened: <see cref="YmgalSearchResponse.Success"/> is
    /// true when ymgal answered (with any number of items, including zero), and false with a
    /// <see cref="YmgalSearchResponse.FailureKind"/> and a human readable
    /// <see cref="YmgalSearchResponse.Message"/> when ymgal could not be asked or refused to answer.
    /// </returns>
    /// <remarks>
    /// An unreachable or refusing server deliberately does <b>not</b> throw here: a caller that only
    /// catches exceptions cannot tell "the source is misconfigured" from "the source has no data",
    /// which is the defect this surface exists to prevent. Cancellation by the caller is the one
    /// exception and is still propagated.
    /// </remarks>
    public async Task<YmgalSearchResponse?> SearchAsync(
        string title,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return FailSearch(
                MetadataSourceFailureKind.InvalidRequest,
                "ymgal search was called with an empty title.");
        }

        var keyword = title.Trim();
        if (keyword.Length >= MaximumKeywordLength)
        {
            return FailSearch(
                MetadataSourceFailureKind.InvalidRequest,
                $"ymgal rejects keywords of {MaximumKeywordLength} characters or more (this one is {keyword.Length}).");
        }

        var token = await AcquireTokenAsync(cancellationToken).ConfigureAwait(false);
        if (token is null)
        {
            return FailSearch(LastFailureKind, LastError!, LastErrorBody);
        }

        var url = $"{_options.BaseUrl}{SearchPath}"
                + $"?mode=list&keyword={Uri.EscapeDataString(keyword)}&pageNum=1&pageSize=10";

        var call = await CallAsync<YmgalPage<YmgalGameItem>>(url, token, cancellationToken).ConfigureAwait(false);
        if (!call.Ok)
        {
            LastFailureKind = call.FailureKind;
            return FailSearch(call.FailureKind, call.Message, LastErrorBody);
        }

        // Success means "ymgal answered", which includes a legitimate zero-hit answer.
        LastFailureKind = MetadataSourceFailureKind.None;
        return new YmgalSearchResponse
        {
            Success = true,
            Code = 0,
            Message = null,
            FailureKind = MetadataSourceFailureKind.None,
            Items = call.Data?.Result ?? new List<YmgalGameItem>(),
            Total = call.Data?.Total ?? 0
        };
    }

    /// <summary>
    /// Gets one ymgal archive by its numeric game id (gid).
    /// </summary>
    /// <param name="gameId">Numeric gid, e.g. <c>"23682"</c> from a <c>/GA23682</c> page or a search hit.</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The game, or null with <see cref="ApiClient.LastError"/> explaining why.</returns>
    public async Task<YmgalGameDetail?> GetGameAsync(
        string gameId,
        CancellationToken cancellationToken = default)
    {
        if (!_options.IsConfigured)
        {
            LastFailureKind = MetadataSourceFailureKind.NotConfigured;
            LastError = _options.ConfigurationProblem;
            return null;
        }

        if (string.IsNullOrWhiteSpace(gameId))
        {
            RecordFailure(MetadataSourceFailureKind.InvalidRequest, "ymgal game id is empty.");
            return null;
        }

        if (!long.TryParse(gameId.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var gid) || gid <= 0)
        {
            // ymgal ids are numeric; the site's page URLs look like /GA31147, where 31147 is the
            // gid. Naming the mistake precisely beats letting it become a 404 that reads like
            // "this game does not exist".
            RecordFailure(
                MetadataSourceFailureKind.InvalidRequest,
                $"\"{gameId}\" is not a numeric ymgal gid (page URLs look like /GA31147 - the gid is 31147).");
            return null;
        }

        var token = await AcquireTokenAsync(cancellationToken).ConfigureAwait(false);
        if (token is null)
        {
            return null;
        }

        var url = $"{_options.BaseUrl}{ArchivePath}?gid={gid}";
        var call = await CallAsync<YmgalArchivePayload>(url, token, cancellationToken).ConfigureAwait(false);
        if (!call.Ok)
        {
            RecordFailure(call.FailureKind, call.Message);
            return null;
        }

        var game = call.Data?.Game;
        if (game is null)
        {
            RecordFailure(MetadataSourceFailureKind.MalformedResponse, "ymgal detail response contained no 'game' object.");
            return null;
        }

        LastFailureKind = MetadataSourceFailureKind.None;
        var detail = game.ToDetail();
        detail.Characters = YmgalArchivePayload.JoinCharacters(game, call.Data!.CharacterMapping);
        return detail;
    }

    /// <summary>
    /// Returns a usable access token, authenticating only when the cached one is missing, expired
    /// or belongs to a different client id.
    /// </summary>
    private async Task<string?> AcquireTokenAsync(CancellationToken cancellationToken)
    {
        if (!_options.IsConfigured)
        {
            RecordFailure(MetadataSourceFailureKind.NotConfigured, _options.ConfigurationProblem);
            return null;
        }

        var clientId = _options.ClientId!;
        if (TryReadCachedToken(clientId, out var cached))
        {
            return cached;
        }

        await TokenGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another caller may have authenticated while this one waited.
            if (TryReadCachedToken(clientId, out cached))
            {
                return cached;
            }

            var url = $"{_options.BaseUrl}{TokenPath}"
                    + $"?grant_type=client_credentials&client_id={Uri.EscapeDataString(clientId)}"
                    + $"&client_secret={Uri.EscapeDataString(_options.ClientSecret!)}&scope={Scope}";

            await ThrottleAsync(cancellationToken).ConfigureAwait(false);

            YmgalTokenResponse? token;
            try
            {
                // The documentation is explicit that the authentication endpoints do not follow the
                // normal request-header rules, so no version / Authorization header is sent here.
                token = await SendJsonAsync<YmgalTokenResponse>(
                    () =>
                    {
                        var request = new HttpRequestMessage(HttpMethod.Get, url);
                        request.Headers.TryAddWithoutValidation("Accept", "application/json;charset=utf-8");
                        return request;
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var kind = ClassifyFailure(ex);
                var detail = string.IsNullOrWhiteSpace(LastError) ? ex.Message : LastError!;
                if (!string.IsNullOrWhiteSpace(LastErrorBody))
                {
                    detail += $" | body: {LastErrorBody}";
                }

                RecordFailure(kind, $"ymgal authentication request failed: {detail}");
                return null;
            }

            if (token is null || string.IsNullOrWhiteSpace(token.AccessToken))
            {
                var detail = LastError ?? "the token endpoint returned no access_token";
                if (!string.IsNullOrWhiteSpace(LastErrorBody))
                {
                    detail += $" | body: {LastErrorBody}";
                }

                var attribution = _options.UsesDedicatedClient
                    ? $"the configured client ({YmgalEndpointOptions.ClientIdVariable})"
                    : "ymgal's public client";

                RecordFailure(MetadataSourceFailureKind.Unauthorized, $"ymgal rejected {attribution}: {detail}");
                return null;
            }

            var lifetime = token.ExpiresIn > 60 ? token.ExpiresIn - 60 : token.ExpiresIn;
            lock (TokenLock)
            {
                _cachedToken = token.AccessToken;
                _cachedTokenClientId = clientId;
                _cachedTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(lifetime);
            }

            return token.AccessToken;
        }
        finally
        {
            TokenGate.Release();
        }
    }

    private static bool TryReadCachedToken(string clientId, out string? token)
    {
        lock (TokenLock)
        {
            if (_cachedToken is not null
                && string.Equals(_cachedTokenClientId, clientId, StringComparison.Ordinal)
                && DateTimeOffset.UtcNow < _cachedTokenExpiresAt)
            {
                token = _cachedToken;
                return true;
            }

            token = null;
            return false;
        }
    }

    /// <summary>
    /// Performs one authenticated ymgal API call and unwraps the global response envelope.
    /// </summary>
    private async Task<YmgalCall<T>> CallAsync<T>(string url, string token, CancellationToken cancellationToken)
    {
        await ThrottleAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var envelope = await SendJsonAsync<YmgalEnvelope<T>>(
                () =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.TryAddWithoutValidation("Accept", "application/json;charset=utf-8");
                    request.Headers.TryAddWithoutValidation("version", ApiVersion);
                    request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
                    return request;
                },
                cancellationToken).ConfigureAwait(false);

            if (envelope is null)
            {
                var detail = LastError ?? "the response body was empty";
                return new YmgalCall<T>(
                    default,
                    false,
                    MetadataSourceFailureKind.MalformedResponse,
                    $"ymgal returned a response the client cannot read: {detail}");
            }

            if (!envelope.Success || envelope.Code != 0)
            {
                var kind = MapYmgalCode(envelope.Code);
                var reason = string.IsNullOrWhiteSpace(envelope.Msg) ? "(no message)" : envelope.Msg!.Trim();
                LastError = $"ymgal error code {envelope.Code}: {reason}";
                return new YmgalCall<T>(
                    default,
                    false,
                    kind,
                    $"{ReasonFor(kind)}: ymgal code {envelope.Code} - {reason}");
            }

            return new YmgalCall<T>(envelope.Data, true, MetadataSourceFailureKind.None, string.Empty);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var kind = ClassifyFailure(ex);
            var detail = LastError ?? ex.Message;
            if (!string.IsNullOrWhiteSpace(LastErrorBody))
            {
                detail += $" | body: {LastErrorBody}";
            }

            if (string.IsNullOrWhiteSpace(LastError))
            {
                LastError = $"ymgal request failed ({kind}): {ex.Message}";
            }

            return new YmgalCall<T>(default, false, kind, $"{ReasonFor(kind)}: {detail}");
        }
    }

    /// <summary>Maps a ymgal application code onto the shared failure vocabulary.</summary>
    private static MetadataSourceFailureKind MapYmgalCode(int code) => code switch
    {
        401 => MetadataSourceFailureKind.Unauthorized,
        403 => MetadataSourceFailureKind.Forbidden,
        429 => MetadataSourceFailureKind.RateLimited,
        614 => MetadataSourceFailureKind.InvalidRequest,   // ILLEGAL_PARAM
        // 7 OTHER, 30 TIME_OUT, 50 SYSTEM_ERROR, 51 ILLEGAL_VERSION, 404 NOT_FOUND, ...
        _ => MetadataSourceFailureKind.UpstreamError
    };

    private static string ReasonFor(MetadataSourceFailureKind kind) => kind switch
    {
        MetadataSourceFailureKind.NotConfigured => "ymgal is not configured",
        MetadataSourceFailureKind.InvalidRequest => "ymgal rejected the request parameters",
        MetadataSourceFailureKind.Unauthorized => "ymgal rejected the access token",
        MetadataSourceFailureKind.Forbidden => "ymgal refused access",
        MetadataSourceFailureKind.RateLimited => "ymgal rate limit reached",
        MetadataSourceFailureKind.NetworkError => "ymgal is unreachable",
        MetadataSourceFailureKind.MalformedResponse => "ymgal returned an unreadable response",
        MetadataSourceFailureKind.UpstreamError => "ymgal reported an error",
        _ => "ymgal request failed"
    };

    /// <summary>Records a failure and returns the matching search response.</summary>
    private YmgalSearchResponse FailSearch(MetadataSourceFailureKind kind, string message, string? body = null)
    {
        RecordFailure(kind, message);
        if (body is not null)
        {
            LastErrorBody = body;
        }

        return new YmgalSearchResponse
        {
            Success = false,
            FailureKind = kind,
            Message = message,
            Items = new List<YmgalGameItem>()
        };
    }

    private void RecordFailure(MetadataSourceFailureKind kind, string message)
    {
        LastFailureKind = kind;
        LastError = message;
    }

    /// <summary>Enforces the minimum spacing between requests so a batch of lookups stays polite.</summary>
    private static async Task ThrottleAsync(CancellationToken cancellationToken)
    {
        await RequestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var wait = MinimumRequestInterval - (DateTimeOffset.UtcNow - _lastRequestAt);
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }

            _lastRequestAt = DateTimeOffset.UtcNow;
        }
        finally
        {
            RequestGate.Release();
        }
    }

    private readonly record struct YmgalCall<T>(
        T? Data,
        bool Ok,
        MetadataSourceFailureKind FailureKind,
        string Message);
}

/// <summary>
/// API client for cngal (CnGal 中文 Galgame 资料站) - https://www.cngal.org/.
/// </summary>
/// <remarks>
/// <para>
/// Contract taken from the service's own OpenAPI document,
/// <c>https://api.cngal.org/swagger/v1/swagger.json</c> (OpenAPI 3.1.1, "CnGal资料站 - 主站 API"),
/// and confirmed against the live service:
/// </para>
/// <list type="bullet">
///   <item><description><b>Auth.</b> None. The API needs no key, token or account - unlike ymgal
///     there is nothing for the user to configure, so the only configuration this client has is
///     the host (<see cref="CngalEndpointOptions"/>).</description></item>
///   <item><description><b>Search.</b> <c>GET /api/home/Search?Types=Game&amp;Text=&lt;q&gt;&amp;Page=1</c>,
///     answering <c>{"pagedResultDto":{"totalCount":N,"totalPages":N,"data":[{entry,article,user,tag,periphery,video}]}}</c>.
///     <c>Types=Game</c> is what narrows the index to games; without it the same call returns
///     articles, tags and users mixed in (a live "三色绘恋" search returned 1038 mixed rows versus
///     84 games). Rows whose <c>entry</c> is null or whose type is not <c>Game</c> are dropped.</description></item>
///   <item><description><b>Detail.</b> <c>GET /api/entries/GetEntryView/&lt;id&gt;?renderMarkdown=false</c>.</description></item>
/// </list>
/// <para>
/// CnGal's search is a fuzzy full-text index: "三色绘恋" legitimately returns 84 games, none of
/// which is rejected here. Ranking is left to <c>GameScrapingService</c>, which scores every hit
/// against the folder name.
/// </para>
/// </remarks>
public class CngalApi : ApiClient
{
    private const string SearchPath = "/api/home/Search";
    private const string EntryViewPath = "/api/entries/GetEntryView";

    /// <summary>Entry type discriminator CnGal uses for games.</summary>
    private const string GameEntryType = "Game";

    /// <summary>Minimum spacing between two requests this process sends to CnGal.</summary>
    private static readonly TimeSpan MinimumRequestInterval = TimeSpan.FromMilliseconds(1000);

    private static readonly SemaphoreSlim RequestGate = new(1, 1);
    private static DateTimeOffset _lastRequestAt = DateTimeOffset.MinValue;

    private readonly CngalEndpointOptions _options;

    /// <summary>
    /// Creates a cngal API client with a typed HttpClient wrapper, using the host resolved from
    /// the environment (see <see cref="CngalEndpointOptions.Resolve"/>).
    /// </summary>
    /// <param name="httpClientWrapper">Typed HttpClient wrapper with User-Agent header configured</param>
    public CngalApi(CngalHttpClient httpClientWrapper) : this(httpClientWrapper, CngalEndpointOptions.Resolve())
    {
    }

    /// <summary>
    /// Creates a cngal API client with an explicit configuration.
    /// </summary>
    /// <param name="httpClientWrapper">Typed HttpClient wrapper with User-Agent header configured</param>
    /// <param name="options">Host to use</param>
    public CngalApi(CngalHttpClient httpClientWrapper, CngalEndpointOptions options)
        : base(httpClientWrapper.HttpClient)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>The configuration this instance was built with.</summary>
    public CngalEndpointOptions Options => _options;

    /// <summary>Failure kind of the most recent call, mirroring <see cref="ApiClient.LastError"/>.</summary>
    public MetadataSourceFailureKind LastFailureKind { get; private set; }

    /// <summary>
    /// Searches CnGal's game index by name.
    /// </summary>
    /// <param name="title">Query keyword.</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>
    /// A response that always describes what happened: <see cref="CngalSearchResponse.Success"/> is
    /// true when CnGal answered (with any number of items, including zero), and false with a
    /// <see cref="CngalSearchResponse.FailureKind"/> when CnGal could not be asked or refused.
    /// </returns>
    public async Task<CngalSearchResponse?> SearchAsync(
        string title,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return FailSearch(
                MetadataSourceFailureKind.InvalidRequest,
                "cngal search was called with an empty title.");
        }

        if (!_options.IsConfigured)
        {
            return FailSearch(MetadataSourceFailureKind.NotConfigured, _options.ConfigurationProblem);
        }

        var url = $"{_options.BaseUrl}{SearchPath}"
                + $"?Types={GameEntryType}&Text={Uri.EscapeDataString(title.Trim())}&Page=1";

        var call = await CallAsync<CngalSearchViewModel>(url, cancellationToken).ConfigureAwait(false);
        if (!call.Ok)
        {
            return FailSearch(call.FailureKind, call.Message, LastErrorBody);
        }

        if (call.Data?.PagedResultDto is null)
        {
            return FailSearch(
                MetadataSourceFailureKind.MalformedResponse,
                "cngal search response contained no 'pagedResultDto' object.",
                LastErrorBody);
        }

        var items = (call.Data.PagedResultDto.Data ?? new List<CngalSearchRow>())
            .Where(row => row.Entry is not null)
            .Where(row => string.Equals(row.Entry!.Type, GameEntryType, StringComparison.OrdinalIgnoreCase))
            .Select(row => row.Entry!.ToGameItem())
            .ToList();

        LastFailureKind = MetadataSourceFailureKind.None;
        return new CngalSearchResponse
        {
            Success = true,
            Message = null,
            FailureKind = MetadataSourceFailureKind.None,
            Items = items,
            Total = call.Data.PagedResultDto.TotalCount
        };
    }

    /// <summary>
    /// Gets one CnGal entry by its numeric id.
    /// </summary>
    /// <param name="gameId">Numeric entry id, e.g. <c>"80"</c> from a search hit.</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The entry, or null with <see cref="ApiClient.LastError"/> explaining why.</returns>
    public async Task<CngalGameDetail?> GetGameAsync(
        string gameId,
        CancellationToken cancellationToken = default)
    {
        if (!_options.IsConfigured)
        {
            RecordFailure(MetadataSourceFailureKind.NotConfigured, _options.ConfigurationProblem);
            return null;
        }

        if (string.IsNullOrWhiteSpace(gameId))
        {
            RecordFailure(MetadataSourceFailureKind.InvalidRequest, "cngal entry id is empty.");
            return null;
        }

        if (!int.TryParse(gameId.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id <= 0)
        {
            RecordFailure(MetadataSourceFailureKind.InvalidRequest, $"\"{gameId}\" is not a numeric cngal entry id.");
            return null;
        }

        var url = $"{_options.BaseUrl}{EntryViewPath}/{id}?renderMarkdown=false";

        var call = await CallAsync<CngalEntryView>(url, cancellationToken).ConfigureAwait(false);
        if (!call.Ok)
        {
            RecordFailure(call.FailureKind, call.Message);
            return null;
        }

        if (call.Data is null || string.IsNullOrWhiteSpace(call.Data.Name))
        {
            RecordFailure(
                MetadataSourceFailureKind.MalformedResponse,
                $"cngal entry {id} response contained no usable entry.");
            return null;
        }

        if (!string.Equals(call.Data.Type, GameEntryType, StringComparison.OrdinalIgnoreCase))
        {
            RecordFailure(
                MetadataSourceFailureKind.InvalidRequest,
                $"cngal entry {id} is a '{call.Data.Type}' entry, not a game.");
            return null;
        }

        LastFailureKind = MetadataSourceFailureKind.None;
        return call.Data.ToGameDetail();
    }

    /// <summary>Performs one cngal API call and classifies any failure.</summary>
    private async Task<CngalCall<T>> CallAsync<T>(string url, CancellationToken cancellationToken)
    {
        await ThrottleAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var payload = await SendJsonAsync<T>(
                () =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.TryAddWithoutValidation("Accept", "application/json;charset=utf-8");
                    return request;
                },
                cancellationToken).ConfigureAwait(false);

            if (payload is null)
            {
                var detail = LastError ?? "the response body was empty";
                return new CngalCall<T>(
                    default,
                    false,
                    MetadataSourceFailureKind.MalformedResponse,
                    $"cngal returned a response the client cannot read: {detail}");
            }

            return new CngalCall<T>(payload, true, MetadataSourceFailureKind.None, string.Empty);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var kind = ClassifyFailure(ex);
            var detail = LastError ?? ex.Message;
            if (!string.IsNullOrWhiteSpace(LastErrorBody))
            {
                detail += $" | body: {LastErrorBody}";
            }

            if (string.IsNullOrWhiteSpace(LastError))
            {
                LastError = $"cngal request failed ({kind}): {ex.Message}";
            }

            return new CngalCall<T>(default, false, kind, $"{ReasonFor(kind)}: {detail}");
        }
    }

    private static string ReasonFor(MetadataSourceFailureKind kind) => kind switch
    {
        MetadataSourceFailureKind.NotConfigured => "cngal is not configured",
        MetadataSourceFailureKind.InvalidRequest => "cngal rejected the request parameters",
        MetadataSourceFailureKind.Unauthorized => "cngal rejected the request",
        MetadataSourceFailureKind.Forbidden => "cngal refused access",
        MetadataSourceFailureKind.RateLimited => "cngal rate limit reached",
        MetadataSourceFailureKind.NetworkError => "cngal is unreachable",
        MetadataSourceFailureKind.MalformedResponse => "cngal returned an unreadable response",
        MetadataSourceFailureKind.UpstreamError => "cngal reported an error",
        _ => "cngal request failed"
    };

    /// <summary>Records a failure and returns the matching search response.</summary>
    private CngalSearchResponse FailSearch(MetadataSourceFailureKind kind, string message, string? body = null)
    {
        RecordFailure(kind, message);
        if (body is not null)
        {
            LastErrorBody = body;
        }

        return new CngalSearchResponse
        {
            Success = false,
            FailureKind = kind,
            Message = message,
            Items = new List<CngalGameItem>()
        };
    }

    private void RecordFailure(MetadataSourceFailureKind kind, string message)
    {
        LastFailureKind = kind;
        LastError = message;
    }

    /// <summary>Enforces the minimum spacing between requests so a batch of lookups stays polite.</summary>
    private static async Task ThrottleAsync(CancellationToken cancellationToken)
    {
        await RequestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var wait = MinimumRequestInterval - (DateTimeOffset.UtcNow - _lastRequestAt);
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }

            _lastRequestAt = DateTimeOffset.UtcNow;
        }
        finally
        {
            RequestGate.Release();
        }
    }

    private readonly record struct CngalCall<T>(
        T? Data,
        bool Ok,
        MetadataSourceFailureKind FailureKind,
        string Message);
}

#region ymgal wire models
// These types mirror the JSON the live ymgal API returns. Where the wire name and the
// application-facing name differ, the JsonPropertyName is the contract and the comment says so.

/// <summary>The ymgal global response envelope: <c>{success, code, msg, data}</c>.</summary>
internal sealed class YmgalEnvelope<T>
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("msg")]
    public string? Msg { get; set; }

    [JsonPropertyName("data")]
    public T? Data { get; set; }
}

/// <summary>A ymgal page: <c>{result:[], total, hasNext, pageNum, pageSize}</c>.</summary>
internal sealed class YmgalPage<T>
{
    [JsonPropertyName("result")]
    public List<T>? Result { get; set; }

    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("hasNext")]
    public bool HasNext { get; set; }

    [JsonPropertyName("pageNum")]
    public int PageNum { get; set; }

    [JsonPropertyName("pageSize")]
    public int PageSize { get; set; }
}

/// <summary>The OAuth2 token response, including the error shape for a rejected client.</summary>
internal sealed class YmgalTokenResponse
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("token_type")]
    public string? TokenType { get; set; }

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("scope")]
    public string? Scope { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("error_description")]
    public string? ErrorDescription { get; set; }
}

/// <summary>The <c>data</c> object of <c>GET /open/archive</c>: the game plus its id maps.</summary>
internal sealed class YmgalArchivePayload
{
    [JsonPropertyName("game")]
    public YmgalArchiveGame? Game { get; set; }

    /// <summary>Keyed by cid; supplies the character names and portraits for <see cref="Game"/>.</summary>
    [JsonPropertyName("cidMapping")]
    public Dictionary<string, YmgalArchivePerson>? CharacterMapping { get; set; }

    /// <summary>Keyed by pid; the voice actors and staff referenced by the game.</summary>
    [JsonPropertyName("pidMapping")]
    public Dictionary<string, YmgalArchivePerson>? PersonMapping { get; set; }

    /// <summary>
    /// Joins <c>game.characters[].cid</c> against <c>cidMapping</c>.
    /// </summary>
    /// <remarks>
    /// The game object only carries the relation triples (<c>cid</c>, <c>cvId</c>,
    /// <c>characterPosition</c>); names and portraits live in the sibling map. Reading only
    /// <c>game.characters</c> would yield a list of numbers, which is useless to a UI.
    /// </remarks>
    internal static List<YmgalCharacter> JoinCharacters(
        YmgalArchiveGame game,
        Dictionary<string, YmgalArchivePerson>? mapping)
    {
        var characters = new List<YmgalCharacter>();
        if (game.Characters is null || game.Characters.Count == 0 || mapping is null)
        {
            return characters;
        }

        foreach (var relation in game.Characters)
        {
            var key = relation.Id.ToString(CultureInfo.InvariantCulture);
            if (!mapping.TryGetValue(key, out var person) || person is null)
            {
                continue;
            }

            characters.Add(new YmgalCharacter
            {
                Name = person.ChineseName ?? person.Name ?? string.Empty,
                NameOriginal = person.Name,
                ImageUrl = person.MainImg
            });
        }

        return characters;
    }
}

/// <summary>A person/character entry inside <c>cidMapping</c> or <c>pidMapping</c>.</summary>
internal sealed class YmgalArchivePerson
{
    [JsonPropertyName("cid")]
    [JsonConverter(typeof(StringOrNumberJsonConverter))]
    public string Cid { get; set; } = string.Empty;

    [JsonPropertyName("pid")]
    [JsonConverter(typeof(StringOrNumberJsonConverter))]
    public string Pid { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Chinese name; the live cidMapping carries one for most characters.</summary>
    [JsonPropertyName("chineseName")]
    public string? ChineseName { get; set; }

    [JsonPropertyName("mainImg")]
    public string? MainImg { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("freeze")]
    public bool Freeze { get; set; }
}

/// <summary>A character-to-game relation: <c>{cid, cvId, characterPosition}</c>.</summary>
internal sealed class YmgalCharacterRelation
{
    [JsonPropertyName("cid")]
    public long Id { get; set; }

    [JsonPropertyName("cvId")]
    public long CvId { get; set; }

    [JsonPropertyName("characterPosition")]
    public int CharacterPosition { get; set; }
}

/// <summary>One entry of <c>game.staff</c>: <c>{sid, pid, empName, empDesc, jobName}</c>.</summary>
internal sealed class YmgalStaff
{
    [JsonPropertyName("pid")]
    public long PersonId { get; set; }

    [JsonPropertyName("empName")]
    public string? Name { get; set; }

    /// <summary>The person's credited role, e.g. <c>Script</c>.</summary>
    [JsonPropertyName("empDesc")]
    public string? Description { get; set; }

    [JsonPropertyName("jobName")]
    public string? JobName { get; set; }
}

/// <summary>One entry of <c>game.releases</c> (a localised edition).</summary>
internal sealed class YmgalRelease
{
    [JsonPropertyName("id")]
    [JsonConverter(typeof(StringOrNumberJsonConverter))]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("releaseName")]
    public string? ReleaseName { get; set; }

    [JsonPropertyName("platform")]
    public string? Platform { get; set; }

    /// <summary>Release language, e.g. <c>Chinese</c> / <c>Japanese</c>.</summary>
    [JsonPropertyName("releaseLanguage")]
    public string? Language { get; set; }

    [JsonPropertyName("restrictionLevel")]
    public string? RestrictionLevel { get; set; }

    [JsonPropertyName("relatedLink")]
    public string? RelatedLink { get; set; }
}

/// <summary>One entry of <c>game.website</c>: <c>{title, link}</c>.</summary>
internal sealed class YmgalWebsite
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("link")]
    public string? Link { get; set; }
}

/// <summary>One entry of <c>game.extensionName</c>: an alias or a foreign title.</summary>
internal sealed class YmgalExtensionName
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("desc")]
    public string? Description { get; set; }
}

/// <summary>
/// The <c>game</c> object returned by <c>GET /open/archive</c> (and by <c>mode=accurate</c> search).
/// </summary>
/// <remarks>
/// Field list observed on a live response for gid 23682:
/// <c>publishVersion, publishTime, publisher, name, chineseName, extensionName, introduction,
/// state, weights, mainImg, moreEntry, gid, developerId, haveChinese, typeDesc, releaseDate,
/// restricted, country, website, characters, releases, staff, type, freeze</c>.
/// Note that the detail object carries <c>gid</c>, not <c>id</c>, and no <c>orgName</c> -
/// the organisation name is only returned by the search endpoint, so
/// <see cref="YmgalGameDetail.Developer"/> stays null on a detail-only lookup and
/// <see cref="YmgalGameDetail.DeveloperId"/> carries the org id instead.
/// </remarks>
internal sealed class YmgalArchiveGame : YmgalGameItem
{
    /// <summary>Game archive id.</summary>
    [JsonPropertyName("gid")]
    [JsonConverter(typeof(StringOrNumberJsonConverter))]
    public string Gid { get; set; } = string.Empty;

    [JsonPropertyName("developerId")]
    [JsonConverter(typeof(StringOrNumberJsonConverter))]
    public string DeveloperId { get; set; } = string.Empty;

    [JsonPropertyName("restricted")]
    public bool Restricted { get; set; }

    [JsonPropertyName("country")]
    public string? Country { get; set; }

    [JsonPropertyName("type")]
    public string? ArchiveType { get; set; }

    [JsonPropertyName("typeDesc")]
    public string? TypeDescription { get; set; }

    [JsonPropertyName("freeze")]
    public bool Freeze { get; set; }

    [JsonPropertyName("extensionName")]
    public List<YmgalExtensionName>? ExtensionNames { get; set; }

    [JsonPropertyName("characters")]
    public List<YmgalCharacterRelation>? Characters { get; set; }

    [JsonPropertyName("staff")]
    public List<YmgalStaff>? Staff { get; set; }

    [JsonPropertyName("releases")]
    public List<YmgalRelease>? Releases { get; set; }

    [JsonPropertyName("website")]
    public List<YmgalWebsite>? Websites { get; set; }

    /// <summary>Projects this archive onto the application-facing detail model.</summary>
    internal YmgalGameDetail ToDetail()
    {
        var detail = new YmgalGameDetail
        {
            Id = string.IsNullOrWhiteSpace(Gid) ? Id : Gid,
            Title = Title,
            TitleCn = TitleCn,
            CoverUrl = CoverUrl,
            Description = Description,
            Developer = Developer,
            DeveloperId = DeveloperId,
            DeveloperOrgId = DeveloperOrgId,
            ReleaseDate = ReleaseDate,
            State = State,
            Score = Score,
            HaveChinese = HaveChinese
        };

        if (ExtensionNames is not null)
        {
            detail.Aliases = ExtensionNames
                .Where(e => !string.IsNullOrWhiteSpace(e.Name))
                .Select(e => e.Name!)
                .ToList();
        }

        if (Staff is not null)
        {
            detail.Staff = Staff
                .Where(s => !string.IsNullOrWhiteSpace(s.Name))
                .Select(s => s.Name!)
                .Distinct()
                .ToList();
        }

        if (Releases is not null)
        {
            detail.ReleaseNames = Releases
                .Select(r => r.ReleaseName)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!)
                .ToList();

            detail.ChineseReleaseName = Releases
                .FirstOrDefault(r => string.Equals(r.Language, "Chinese", StringComparison.OrdinalIgnoreCase))
                ?.ReleaseName;
        }

        if (Websites is not null && Websites.Count > 0)
        {
            detail.Websites = Websites
                .Where(w => !string.IsNullOrWhiteSpace(w.Link))
                .Select(w => string.IsNullOrWhiteSpace(w.Title) ? w.Link! : $"{w.Title}: {w.Link}")
                .ToList();

            detail.Website = Websites.FirstOrDefault()?.Link;
        }

        return detail;
    }
}
#endregion

#region cngal wire models
// Field names are those of https://api.cngal.org/swagger/v1/swagger.json.

/// <summary>The <c>SearchViewModel</c> wrapper: <c>{pagedResultDto:{...}}</c>.</summary>
internal sealed class CngalSearchViewModel
{
    [JsonPropertyName("pagedResultDto")]
    public CngalPagedResultDto? PagedResultDto { get; set; }
}

/// <summary>The CnGal paged result: <c>{maxResultCount,currentPage,totalCount,totalPages,data:[]}</c>.</summary>
internal sealed class CngalPagedResultDto
{
    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }

    [JsonPropertyName("totalPages")]
    public int TotalPages { get; set; }

    [JsonPropertyName("currentPage")]
    public int CurrentPage { get; set; }

    [JsonPropertyName("maxResultCount")]
    public int MaxResultCount { get; set; }

    [JsonPropertyName("data")]
    public List<CngalSearchRow>? Data { get; set; }
}

/// <summary>
/// One row of <c>SearchAloneModel</c>. A row carries exactly one populated member; a game hit has
/// <c>entry</c> set and <c>article</c>/<c>user</c>/<c>tag</c>/<c>periphery</c>/<c>video</c> null,
/// so only the member the client consumes is modelled.
/// </summary>
internal sealed class CngalSearchRow
{
    [JsonPropertyName("entry")]
    public CngalEntryTip? Entry { get; set; }
}

/// <summary>The <c>EntryInforTipViewModel</c> a search row carries.</summary>
internal sealed class CngalEntryTip
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary><c>Game</c>, <c>Role</c>, <c>ProductionGroup</c> or <c>Staff</c>.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("mainImage")]
    public string? MainImage { get; set; }

    [JsonPropertyName("briefIntroduction")]
    public string? BriefIntroduction { get; set; }

    [JsonPropertyName("publishTime")]
    public DateTimeOffset? PublishTime { get; set; }

    [JsonPropertyName("lastEditTime")]
    public DateTimeOffset? LastEditTime { get; set; }

    /// <summary>Projects this hit onto the application-facing item model.</summary>
    internal CngalGameItem ToGameItem() => new()
    {
        Id = Id.ToString(CultureInfo.InvariantCulture),
        Title = Name ?? string.Empty,

        // CnGal is a Chinese-language database: an entry's primary name usually *is* its Chinese
        // name, so the same string fills both slots. Leaving TitleCn null would leave the Chinese
        // title column empty for every cngal hit while the Chinese name sat in the "original" one.
        TitleCn = Name,

        CoverUrl = MainImage,
        Description = BriefIntroduction
    };
}

/// <summary>The <c>EntryIndexViewModel</c> returned by <c>GET /api/entries/GetEntryView/{id}</c>.</summary>
internal sealed class CngalEntryView
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Alias list, comma separated, e.g. <c>"三色绘恋，Tricolour Lovestory"</c>.</summary>
    [JsonPropertyName("anotherName")]
    public string? AnotherName { get; set; }

    [JsonPropertyName("briefIntroduction")]
    public string? BriefIntroduction { get; set; }

    [JsonPropertyName("mainPicture")]
    public string? MainPicture { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>制作组 entries.</summary>
    [JsonPropertyName("productionGroups")]
    public List<CngalNamedRef>? ProductionGroups { get; set; }

    /// <summary>Publishers.</summary>
    [JsonPropertyName("publishers")]
    public List<CngalNamedRef>? Publishers { get; set; }

    /// <summary>Infobox rows.</summary>
    [JsonPropertyName("information")]
    public List<CngalInformation>? Information { get; set; }

    /// <summary>Characters (entry type <c>Role</c>).</summary>
    [JsonPropertyName("roles")]
    public List<CngalRole>? Roles { get; set; }

    /// <summary>Tags.</summary>
    [JsonPropertyName("tags")]
    public List<CngalNamedRef>? Tags { get; set; }

    /// <summary>Releases per platform.</summary>
    [JsonPropertyName("releases")]
    public List<CngalRelease>? Releases { get; set; }

    /// <summary>Projects this entry onto the application-facing detail model.</summary>
    internal CngalGameDetail ToGameDetail()
    {
        var detail = new CngalGameDetail
        {
            Id = Id.ToString(CultureInfo.InvariantCulture),
            Title = Name ?? string.Empty,
            TitleCn = Name,
            CoverUrl = MainPicture,
            Description = BriefIntroduction,
            AnotherName = AnotherName,
            EntryType = Type,
            Developer = ProductionGroups?.FirstOrDefault()?.DisplayName
                     ?? Publishers?.FirstOrDefault()?.DisplayName
        };

        if (Tags is not null)
        {
            detail.Tags = Tags
                .Where(t => !string.IsNullOrWhiteSpace(t.Name))
                .Select(t => t.Name!)
                .ToList();
        }

        if (Roles is not null)
        {
            detail.Characters = Roles
                .Where(r => !string.IsNullOrWhiteSpace(r.Name))
                .Select(r => new CngalCharacter
                {
                    Name = r.Name!,
                    ImageUrl = string.IsNullOrWhiteSpace(r.MainImage) ? r.StandingPainting : r.MainImage,
                    VoiceActor = r.Cv
                })
                .ToList();
        }

        if (Information is not null)
        {
            detail.Information = Information
                .Where(i => !string.IsNullOrWhiteSpace(i.Name))
                .Select(i => $"{i.Name}: {i.Value}")
                .ToList();
        }

        if (Releases is not null && Releases.Count > 0)
        {
            detail.ReleaseNames = Releases
                .Select(r => r.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!)
                .ToList();

            // An entry with several platform releases has several dates; the earliest one is the
            // game's release date, which is what the library UI shows.
            var timestamps = Releases
                .Where(r => r.Time.HasValue)
                .Select(r => r.Time!.Value)
                .OrderBy(t => t)
                .ToList();

            detail.ReleaseDate = timestamps.Count == 0
                ? null
                : timestamps[0].ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            detail.Engine = Releases.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.Engine))?.Engine;
        }

        return detail;
    }
}

/// <summary>A generic <c>{id, name/displayName}</c> reference used by several CnGal models.</summary>
internal sealed class CngalNamedRef
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }
}

/// <summary>One infobox row.</summary>
internal sealed class CngalInformation
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("value")]
    public string? Value { get; set; }
}

/// <summary>One character of an entry.</summary>
internal sealed class CngalRole
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("mainImage")]
    public string? MainImage { get; set; }

    [JsonPropertyName("standingPainting")]
    public string? StandingPainting { get; set; }

    /// <summary>Voice actor name.</summary>
    [JsonPropertyName("cv")]
    public string? Cv { get; set; }
}

/// <summary>One release of an entry.</summary>
internal sealed class CngalRelease
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("time")]
    public DateTimeOffset? Time { get; set; }

    [JsonPropertyName("engine")]
    public string? Engine { get; set; }
}
#endregion

#region Application-facing response models

/// <summary>
/// Outcome of a ymgal search.
/// </summary>
/// <remarks>
/// <see cref="Success"/> means "ymgal answered this question", not "there are results": a search
/// for a title ymgal does not have returns <c>Success = true</c> with an empty
/// <see cref="Items"/> (verified live - <c>success:true, code:0, total:0</c>). A source that could
/// not be asked returns <c>Success = false</c> together with a <see cref="FailureKind"/> and a
/// <see cref="Message"/> naming the cause.
/// </remarks>
public class YmgalSearchResponse
{
    /// <summary>True when the source answered (with any number of items, including zero).</summary>
    public bool Success { get; set; }

    /// <summary>ymgal's own application code (<c>0</c> = success).</summary>
    public int Code { get; set; }

    /// <summary>Human readable explanation when <see cref="Success"/> is false.</summary>
    public string? Message { get; set; }

    /// <summary>Why the source could not answer; <see cref="MetadataSourceFailureKind.None"/> on success.</summary>
    public MetadataSourceFailureKind FailureKind { get; set; }

    /// <summary>Search hits.</summary>
    public List<YmgalGameItem> Items { get; set; } = new();

    /// <summary>Total hits ymgal reports, which can exceed <see cref="Items"/>.</summary>
    public int Total { get; set; }
}

/// <summary>
/// One ymgal archive.
/// </summary>
/// <remarks>
/// The property names are the application-facing ones (<c>Title</c>, <c>TitleCn</c>,
/// <c>CoverUrl</c>, <c>Description</c>); the <c>JsonPropertyName</c> attributes carry the wire
/// field ymgal actually returns (<c>name</c>, <c>chineseName</c>, <c>mainImg</c>,
/// <c>introduction</c>) and are the binding contract. This type is deserialized directly by the
/// search endpoint, so the attributes are live, not decorative.
/// </remarks>
public class YmgalGameItem
{
    /// <summary>Game id (ymgal "gid"), always numeric but possibly sent as a JSON string.</summary>
    [JsonPropertyName("id")]
    [JsonConverter(typeof(StringOrNumberJsonConverter))]
    public string Id { get; set; } = string.Empty;

    /// <summary>Original title (wire field <c>name</c>).</summary>
    [JsonPropertyName("name")]
    public string Title { get; set; } = string.Empty;

    /// <summary>Chinese title (wire field <c>chineseName</c>).</summary>
    [JsonPropertyName("chineseName")]
    public string? TitleCn { get; set; }

    /// <summary>Cover image (wire field <c>mainImg</c>).</summary>
    [JsonPropertyName("mainImg")]
    public string? CoverUrl { get; set; }

    /// <summary>
    /// Synopsis (wire field <c>introduction</c>).
    /// </summary>
    /// <remarks>
    /// Only the detail endpoint returns an introduction; the search page has none, so this stays
    /// null for plain search hits.
    /// </remarks>
    [JsonPropertyName("introduction")]
    public string? Description { get; set; }

    /// <summary>Developer/publisher name (wire field <c>orgName</c>; search results only).</summary>
    [JsonPropertyName("orgName")]
    public string? Developer { get; set; }

    /// <summary>Developer organisation id (wire field <c>orgId</c>).</summary>
    [JsonPropertyName("orgId")]
    [JsonConverter(typeof(StringOrNumberJsonConverter))]
    public string DeveloperOrgId { get; set; } = string.Empty;

    /// <summary>Release date as reported by ymgal (<c>yyyy-MM-dd</c>).</summary>
    [JsonPropertyName("releaseDate")]
    public string? ReleaseDate { get; set; }

    /// <summary>Archive state, e.g. <c>PUBLIC_PUBLISHED</c>.</summary>
    [JsonPropertyName("state")]
    public string? State { get; set; }

    /// <summary>Community score, which ymgal sends as a string.</summary>
    [JsonPropertyName("score")]
    public string? Score { get; set; }

    /// <summary>Whether ymgal knows of a Chinese release.</summary>
    [JsonPropertyName("haveChinese")]
    public bool HaveChinese { get; set; }

    /// <summary>Every title this item can be matched against.</summary>
    public List<string> GetAllTitles()
    {
        var titles = new List<string>();
        if (!string.IsNullOrWhiteSpace(Title))
        {
            titles.Add(Title);
        }

        if (!string.IsNullOrWhiteSpace(TitleCn) && !titles.Contains(TitleCn!))
        {
            titles.Add(TitleCn!);
        }

        return titles;
    }
}

/// <summary>Full ymgal archive detail.</summary>
/// <remarks>
/// Built from <see cref="YmgalArchiveGame"/> rather than deserialized directly, because the detail
/// endpoint nests the archive under <c>data.game</c>.
/// </remarks>
public class YmgalGameDetail : YmgalGameItem
{
    /// <summary>Developer organisation id (ymgal <c>developerId</c>).</summary>
    public string DeveloperId { get; set; } = string.Empty;

    /// <summary>Alternative / foreign names (ymgal <c>extensionName[].name</c>).</summary>
    public List<string> Aliases { get; set; } = new();

    /// <summary>Credited staff names (ymgal <c>staff[].empName</c>).</summary>
    public List<string> Staff { get; set; } = new();

    /// <summary>Release names (ymgal <c>releases[].releaseName</c>).</summary>
    public List<string> ReleaseNames { get; set; } = new();

    /// <summary>Name of the Chinese release, when ymgal records one.</summary>
    public string? ChineseReleaseName { get; set; }

    /// <summary>Primary website.</summary>
    public string? Website { get; set; }

    /// <summary>All website links, formatted as <c>title: url</c>.</summary>
    public List<string> Websites { get; set; } = new();

    /// <summary>
    /// Genre/content tags.
    /// </summary>
    /// <remarks>
    /// Always empty: the ymgal archive endpoints documented at
    /// <c>https://www.ymgal.games/developer</c> do not expose archive tags, and the live
    /// <c>game</c> object carries no <c>tags</c> member (verified for gid 23682). The property is
    /// kept so the scraping pipeline has a uniform shape, and it is deliberately not bound to any
    /// JSON name rather than being bound to a field that does not exist.
    /// </remarks>
    public List<string> Tags { get; set; } = new();

    /// <summary>Cast, joined from the game's character relations and <c>cidMapping</c>.</summary>
    public List<YmgalCharacter> Characters { get; set; } = new();
}

/// <summary>One ymgal character.</summary>
public class YmgalCharacter
{
    /// <summary>Display name (Chinese when ymgal knows one, otherwise the original).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Original (usually Japanese) name.</summary>
    public string? NameOriginal { get; set; }

    /// <summary>Portrait.</summary>
    public string? ImageUrl { get; set; }
}

/// <summary>
/// Outcome of a cngal search. <see cref="Success"/> means "CnGal answered", exactly as in
/// <see cref="YmgalSearchResponse"/>.
/// </summary>
public class CngalSearchResponse
{
    /// <summary>True when the source answered (with any number of items, including zero).</summary>
    public bool Success { get; set; }

    /// <summary>Human readable explanation when <see cref="Success"/> is false.</summary>
    public string? Message { get; set; }

    /// <summary>Why the source could not answer; <see cref="MetadataSourceFailureKind.None"/> on success.</summary>
    public MetadataSourceFailureKind FailureKind { get; set; }

    /// <summary>Search hits (games only).</summary>
    public List<CngalGameItem> Items { get; set; } = new();

    /// <summary>Total games CnGal reports, which can exceed <see cref="Items"/>.</summary>
    public int Total { get; set; }
}

/// <summary>One CnGal entry.</summary>
public class CngalGameItem
{
    /// <summary>Numeric entry id, kept as a string so it can be used directly as a source id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Entry name (wire field <c>name</c>).</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Chinese name; for CnGal this is the same string as <see cref="Title"/>.</summary>
    public string? TitleCn { get; set; }

    /// <summary>Cover image (wire field <c>mainImage</c> / <c>mainPicture</c>).</summary>
    public string? CoverUrl { get; set; }

    /// <summary>Synopsis (wire field <c>briefIntroduction</c>).</summary>
    public string? Description { get; set; }

    /// <summary>制作组 / publisher.</summary>
    public string? Developer { get; set; }

    /// <summary>Every title this item can be matched against.</summary>
    public List<string> GetAllTitles()
    {
        var titles = new List<string>();
        if (!string.IsNullOrWhiteSpace(Title))
        {
            titles.Add(Title);
        }

        if (!string.IsNullOrWhiteSpace(TitleCn) && !titles.Contains(TitleCn!))
        {
            titles.Add(TitleCn!);
        }

        return titles;
    }
}

/// <summary>Full CnGal entry detail.</summary>
public class CngalGameDetail : CngalGameItem
{
    /// <summary>Alias list as CnGal stores it (comma separated).</summary>
    public string? AnotherName { get; set; }

    /// <summary>Entry type; <c>Game</c> for a game.</summary>
    public string? EntryType { get; set; }

    /// <summary>Release date (<c>yyyy-MM-dd</c>), taken from the earliest platform release.</summary>
    public string? ReleaseDate { get; set; }

    /// <summary>Engine of the first release that declares one.</summary>
    public string? Engine { get; set; }

    /// <summary>Genre tags.</summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>Cast.</summary>
    public List<CngalCharacter> Characters { get; set; } = new();

    /// <summary>Infobox rows, formatted as <c>name: value</c>.</summary>
    public List<string> Information { get; set; } = new();

    /// <summary>Release names across platforms.</summary>
    public List<string> ReleaseNames { get; set; } = new();
}

/// <summary>One CnGal character.</summary>
public class CngalCharacter
{
    /// <summary>Display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Portrait.</summary>
    public string? ImageUrl { get; set; }

    /// <summary>Voice actor, when CnGal records one.</summary>
    public string? VoiceActor { get; set; }
}

#endregion
