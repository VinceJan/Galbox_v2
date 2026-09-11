using System.Net;
using System.Text;
using System.Text.Json;

namespace Galbox.Core.Api;

/// <summary>
/// Client for the official NextMoe "moyu face" (<c>https://api.nextmoe.dev/v2/moyu/*</c>): the
/// authorised way to discover which patch resources moyu.moe holds for a game.
///
/// <para>
/// <b>What this client can and cannot do, stated up front.</b>
/// </para>
///
/// <para>
/// It <b>can</b> answer "for this game, what patches exist on moyu, with what metadata", and it can
/// produce the page URL the user opens to download. It <b>cannot</b> produce a download link, a
/// share code or an archive password — the contract carries none, and that is deliberate:
/// <c>moyu-openapi.yaml</c> says a link is "a separate, rate-limited, per-resource request whose
/// whole purpose is that links cannot be harvested in bulk". So Galbox's half of the download is a
/// browser hop (<see cref="MoyuBrowserLauncher"/>) followed by adopting the file the user just
/// downloaded (<see cref="MoyuDownloadWatcher"/>), and then handing it to the local patch engine.
/// </para>
///
/// <para>
/// It must never be extended to reach the site's own <c>/api/v1/*</c> endpoints, however available
/// they are. See <see cref="MoyuComplianceGuard"/> for the evidence and for the mechanism that makes
/// it structurally impossible: every URI this class sends is built by
/// <see cref="MoyuComplianceGuard.EnsureApiUri"/>.
/// </para>
///
/// <para>
/// <b>Why this class does not use <see cref="ApiClient"/>'s helpers.</b> The shared base is a good
/// fit for Bangumi and VNDB, which need neither request headers nor conditional requests. It is not
/// usable here, for two concrete reasons recorded in the research report §6.2:
/// </para>
/// <list type="number">
/// <item><description>
/// <see cref="ApiClient.GetJsonAsync{T}"/> sends through <c>HttpClient.GetStringAsync</c> /
/// <c>GetAsync</c> with no way to attach a per-request header, so it cannot carry the
/// <c>X-API-Key</c> this face requires, and it has no <c>If-None-Match</c> / <c>304</c> path.
/// </description></item>
/// <item><description>
/// Its retry loop reads no response headers at all: a <c>429</c> is retried on a 2× backoff while
/// the service's own <c>Retry-After</c> is ignored, which is how a client hammers a
/// rate-limited endpoint politely described as "come back at time T", and it also ignores
/// <c>X-RateLimit-*</c> / <c>X-Quota-*</c>.
/// </description></item>
/// </list>
/// <para>
/// So the class inherits <see cref="ApiClient"/> for the shared <see cref="ApiClient.LastError"/> /
/// <see cref="ApiClient.LastErrorBody"/> diagnostics contract, which callers and diagnostics already
/// understand, and carries its own <c>HttpRequestMessage</c> pipeline:
/// <see cref="System.Net.Http.HttpRequestMessage"/> built per call, key header attached, ETag
/// revalidation, <c>Retry-After</c> honoured, requests serialised and paced.
/// </para>
///
/// <para>
/// <b>Failure handling.</b> Every public method returns a <see cref="MoyuResult{T}"/>; nothing throws
/// for a network or upstream condition, and nothing ever turns a failure into an empty collection.
/// <see cref="MoyuFailureCode.NotConfigured"/> in particular is a first-class answer, reported
/// without sending a request. <see cref="OperationCanceledException"/> is the one exception that
/// still propagates, because a caller's own cancellation is not a failure to report.
/// </para>
/// </summary>
public sealed class MoyuApi : ApiClient
{
    /// <summary>The only surface this client is allowed to use.</summary>
    private const string ApiRoot = "/v2/moyu/";

    /// <summary>The service's own ceiling for one batch lookup.</summary>
    public const int MaxAnchorsPerBatch = 100;

    /// <summary>Lower bound the service accepts for <c>limit</c>.</summary>
    public const int MinPageSize = 1;

    /// <summary>The service's stated ceiling for <c>limit</c> ("over 100 is <c>LIMIT_TOO_LARGE</c>").</summary>
    public const int MaxPageSize = 100;

    /// <summary>Bytes of a response body kept for diagnostics. Enough to see a shape change, not a flood.</summary>
    private const int MaxRecordedBodyLength = 600;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly MoyuOptions _options;
    private readonly IMoyuRateLimiter _limiter;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly object _cacheGate = new();

    /// <summary>Creates the client.</summary>
    /// <param name="httpClientWrapper">The typed HttpClient carrying the <c>/v2/moyu</c> BaseAddress.</param>
    /// <param name="options">Key, pacing and cache policy.</param>
    /// <param name="limiter">Shared pacing instance. Optional; a private one is created when omitted.</param>
    public MoyuApi(MoyuHttpClient httpClientWrapper, MoyuOptions options, IMoyuRateLimiter? limiter = null)
        : base((httpClientWrapper ?? throw new ArgumentNullException(nameof(httpClientWrapper))).HttpClient)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _limiter = limiter ?? new MoyuRateLimiter(options);
    }

    /// <summary>The options in force, including whether a key is configured.</summary>
    public MoyuOptions Options => _options;

    /// <summary>True when a key is configured, i.e. a call could be attempted.</summary>
    public bool IsConfigured => _options.HasApiKey;

    /// <summary>
    /// The most recent rate-limit or quota hint the service returned
    /// (<c>X-RateLimit-Remaining</c> / <c>X-Quota-Remaining</c>), or <c>null</c>. Diagnostic only.
    /// </summary>
    public string? LastRateLimitNotice { get; private set; }

    /// <summary>Number of requests this client has actually put on the wire. Diagnostic only.</summary>
    public int RequestsSent { get; private set; }

    // ---------------------------------------------------------------------------------------
    // Public surface
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Looks up patch pages for up to <see cref="MaxAnchorsPerBatch"/> game anchors in <b>one</b>
    /// request. This is the shape to prefer: one round trip answers "which of these games do you
    /// have patches for", and anchors that matched nothing come back in
    /// <see cref="MoyuPatchBatch.Missing"/> rather than as an error.
    /// </summary>
    /// <param name="anchors">Anchors such as <c>vndb:v65869</c>. Empty or null yields
    /// <see cref="MoyuFailureCode.NoAnchor"/> without sending anything.</param>
    /// <param name="includeResources">Attach the resources to each patch page.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<MoyuResult<MoyuPatchBatch>> FindPatchesAsync(
        IReadOnlyList<MoyuRef>? anchors,
        bool includeResources = true,
        CancellationToken cancellationToken = default)
    {
        var usable = (anchors ?? Array.Empty<MoyuRef>())
            .Where(anchor => anchor is not null)
            .Distinct()
            .ToArray();

        if (usable.Length == 0)
        {
            // The game has no anchor. This is the documented consequence of the public face not
            // accepting a Bangumi id, and the honest answer is to say so.
            return Task.FromResult(MoyuResult<MoyuPatchBatch>.Fail(
                MoyuFailureCode.NoAnchor,
                "这部作品没有可用于 moyu 的锚点。官方公开面只接受 vndb:vXXXX 与 catalog:<id>，不接受 Bangumi 条目 ID，"
                + "因此无法查询该作品的补丁。"));
        }

        if (usable.Length > MaxAnchorsPerBatch)
        {
            return Task.FromResult(MoyuResult<MoyuPatchBatch>.Fail(
                MoyuFailureCode.TooManyAnchors,
                $"一次最多查询 {MaxAnchorsPerBatch} 个锚点，收到 {usable.Length} 个。"));
        }

        var query = new QueryBuilder()
            .Add("refs", string.Join(",", usable.Select(a => a.ToString())))
            .AddIf(includeResources, "include", "resources")
            .Build();

        return SendAsync<MoyuPatchListDto, MoyuPatchBatch>(
            ApiRoot + "patches",
            query,
            dto => new MoyuPatchBatch
            {
                Items = (dto.Items ?? new List<MoyuPatchDto>()).Select(MoyuPatch.FromDto).ToList(),
                Missing = dto.Missing ?? new List<string>(),
                NextCursor = dto.NextCursor,
                Total = dto.Total
            },
            fail => MoyuResult<MoyuPatchBatch>.Fail(fail),
            cancellationToken);
    }

    /// <summary>
    /// Looks up the patch pages for <b>one</b> game, given what the library knows about it.
    ///
    /// <para>
    /// This is the convenience wrapper the UI will call. Pass the identifiers the scraping stage
    /// stored; a VNDB id is preferred because it is moyu's own dedupe key, and the NextMoe catalog
    /// work id is the fallback. A Bangumi subject id is <b>not</b> accepted — there is no conversion
    /// between the id spaces, and guessing one would produce a confidently wrong patch list.
    /// </para>
    /// </summary>
    public Task<MoyuResult<MoyuPatchBatch>> FindPatchesForGameAsync(
        string? vndbId,
        string? catalogWorkId = null,
        bool includeResources = true,
        CancellationToken cancellationToken = default)
    {
        var anchor = MoyuRef.PickAnchor(vndbId, catalogWorkId);
        if (anchor is null)
        {
            return Task.FromResult(MoyuResult<MoyuPatchBatch>.Fail(
                MoyuFailureCode.NoAnchor,
                "这部作品没有 vndb id（moyu 的公开面不接受 Bangumi 条目 ID），因此无法查询补丁。"
                + "需要先给作品补上 vndb id 才能使用补丁发现。"));
        }

        return FindPatchesAsync(new[] { anchor }, includeResources, cancellationToken);
    }

    /// <summary>
    /// Fetches one patch page by its moyu id. Nothing is attached by default, including the
    /// resources; <c>resource_count</c> says whether asking for them is worthwhile.
    /// </summary>
    public Task<MoyuResult<MoyuPatch>> GetPatchAsync(
        string patchId,
        bool includeResources = false,
        CancellationToken cancellationToken = default)
    {
        if (!IsNumericId(patchId))
        {
            return Task.FromResult(MoyuResult<MoyuPatch>.Fail(
                MoyuFailureCode.BadRequest,
                $"补丁 id 必须是数字，收到 \"{patchId}\"。"));
        }

        var query = new QueryBuilder()
            .AddIf(includeResources, "include", "resources")
            .Build();

        return SendAsync<MoyuPatchDto, MoyuPatch>(
            ApiRoot + "patches/" + patchId,
            query,
            MoyuPatch.FromDto,
            fail => MoyuResult<MoyuPatch>.Fail(fail),
            cancellationToken);
    }

    /// <summary>
    /// Lists the resources on a patch page, newest change first, following the opaque cursor.
    /// Only live resources are ever listed.
    /// </summary>
    public Task<MoyuResult<MoyuResourcePage>> ListResourcesAsync(
        string patchId,
        string? cursor = null,
        int limit = 50,
        bool includeTotal = false,
        CancellationToken cancellationToken = default)
    {
        if (!IsNumericId(patchId))
        {
            return Task.FromResult(MoyuResult<MoyuResourcePage>.Fail(
                MoyuFailureCode.BadRequest,
                $"补丁 id 必须是数字，收到 \"{patchId}\"。"));
        }

        if (limit is < MinPageSize or > MaxPageSize)
        {
            // The service refuses >100 outright ("the value is not clamped"); failing here keeps
            // the mistake local instead of spending a round trip on a guaranteed 400.
            return Task.FromResult(MoyuResult<MoyuResourcePage>.Fail(
                MoyuFailureCode.BadRequest,
                $"limit 必须在 {MinPageSize}..{MaxPageSize} 之间，收到 {limit}。"));
        }

        var query = new QueryBuilder()
            .Add("limit", limit.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .AddIf(!string.IsNullOrWhiteSpace(cursor), "cursor", cursor)
            .AddIf(includeTotal, "include_total", "true")
            .Build();

        return SendAsync<MoyuResourceListDto, MoyuResourcePage>(
            ApiRoot + "patches/" + patchId + "/resources",
            query,
            dto => new MoyuResourcePage
            {
                Items = (dto.Items ?? new List<MoyuResourceDto>()).Select(MoyuResource.FromDto).ToList(),
                NextCursor = dto.NextCursor,
                Total = dto.Total
            },
            fail => MoyuResult<MoyuResourcePage>.Fail(fail),
            cancellationToken);
    }

    /// <summary>Fetches one resource by its id.</summary>
    public Task<MoyuResult<MoyuResource>> GetResourceAsync(
        string resourceId,
        CancellationToken cancellationToken = default)
    {
        if (!IsNumericId(resourceId))
        {
            return Task.FromResult(MoyuResult<MoyuResource>.Fail(
                MoyuFailureCode.BadRequest,
                $"资源 id 必须是数字，收到 \"{resourceId}\"。"));
        }

        return SendAsync<MoyuResourceDto, MoyuResource>(
            ApiRoot + "resources/" + resourceId,
            string.Empty,
            MoyuResource.FromDto,
            fail => MoyuResult<MoyuResource>.Fail(fail),
            cancellationToken);
    }

    /// <summary>
    /// The page a reader opens to obtain the file. This is the compliant substitute for a direct
    /// link, and it is the value <see cref="MoyuBrowserLauncher"/> is given.
    /// </summary>
    /// <returns>The URL, or <c>null</c> when the row carries none or it is not an allowed page.</returns>
    public static string? BuildWebUrl(MoyuResource resource)
    {
        var url = resource?.WebUrl;
        return MoyuComplianceGuard.IsAllowedWebUrl(url) ? url : null;
    }

    /// <summary>Drops the in-memory ETag cache.</summary>
    public void ClearCache()
    {
        lock (_cacheGate)
        {
            _cache.Clear();
        }
    }

    // ---------------------------------------------------------------------------------------
    // Request pipeline
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Sends one GET, with pacing, conditional revalidation and header-aware retries, and maps the
    /// payload. This is the single funnel every request goes through.
    /// </summary>
    private async Task<MoyuResult<TValue>> SendAsync<TDto, TValue>(
        string relativePathAndQuery,
        string query,
        Func<TDto, TValue> map,
        Func<MoyuFailure, MoyuResult<TValue>> fail,
        CancellationToken cancellationToken)
    {
        ResetDiagnostics();

        // A missing key is answered here, before any pacing or I/O, and it is a distinct failure.
        if (!_options.HasApiKey)
        {
            return fail(new MoyuFailure
            {
                Code = MoyuFailureCode.NotConfigured,
                Message = "尚未配置 moyu 的 nmk_ API 密钥，因此没有发起任何请求。"
                          + "请在 https://developer.nextmoe.dev 免费自助铸造一把密钥并填入 Galbox。"
            });
        }

        Uri uri;
        try
        {
            uri = MoyuComplianceGuard.EnsureApiUri(_options.BaseAddress, relativePathAndQuery + query);
        }
        catch (Exception ex)
        {
            // Only reachable through a programming error; surfaced as a failure rather than thrown
            // so that a caller's error path stays uniform.
            return fail(new MoyuFailure
            {
                Code = MoyuFailureCode.ClientError,
                Message = "拒绝发送越界的请求：" + ex.Message
            });
        }

        var cacheKey = uri.ToString();

        for (var attempt = 0; ; attempt++)
        {
            var verdict = await _limiter
                .AcquireAsync(_options.MaxRateLimitWait, cancellationToken)
                .ConfigureAwait(false);

            if (!verdict.CanProceed && verdict.Decision == MoyuRateLimitDecision.BudgetExhausted)
            {
                return fail(new MoyuFailure
                {
                    Code = MoyuFailureCode.ClientThrottled,
                    Message = "Galbox 自身的请求节流器暂不允许发送请求：" + verdict.Reason,
                    RetryAfter = verdict.RetryAfter
                });
            }

            if (verdict.Decision == MoyuRateLimitDecision.Wait && verdict.RetryAfter > TimeSpan.Zero)
            {
                await Task.Delay(verdict.RetryAfter, cancellationToken).ConfigureAwait(false);
            }

            await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            HttpResponseMessage response;
            try
            {
                using var request = BuildRequest(uri, cacheKey);
                RequestsSent++;
                response = await HttpClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The caller cancelled. Not a failure to report; propagate.
                throw;
            }
            catch (OperationCanceledException)
            {
                // The HttpClient timeout, not the caller's token.
                LastError = "moyu request timed out";
                return fail(new MoyuFailure
                {
                    Code = MoyuFailureCode.Timeout,
                    Message = $"请求 moyu 超时（{HttpClient.Timeout.TotalSeconds:F0}s）：{uri.AbsolutePath}"
                });
            }
            catch (HttpRequestException ex)
            {
                LastError = $"moyu transport failure: {ex.Message}";
                return fail(new MoyuFailure
                {
                    Code = MoyuFailureCode.NetworkUnreachable,
                    Message = "无法连接 moyu 的公开接口（网络不可达）。"
                              + "补丁的本地安装功能不受影响，仍然可用。"
                });
            }
            finally
            {
                _requestGate.Release();
            }

            try
            {
                RecordRateLimitNotice(response);

                // 304: the cached copy is still current. Reuse it and do not re-parse.
                if (response.StatusCode == HttpStatusCode.NotModified)
                {
                    if (TryReadCache(cacheKey, out var cachedDocument))
                    {
                        return MapDocument(cachedDocument, map, fail);
                    }

                    // A 304 without a cached body cannot happen unless something else revalidated;
                    // reported rather than treated as an empty page.
                    return fail(new MoyuFailure
                    {
                        Code = MoyuFailureCode.MalformedResponse,
                        Message = "服务返回 304，但本地没有对应的缓存副本。",
                        HttpStatus = 304
                    });
                }

                if (response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    StoreCache(cacheKey, response, body);
                    return MapDocument(body, map, fail);
                }

                var failure = await BuildFailureAsync(response, cancellationToken).ConfigureAwait(false);

                if (ShouldRetry(response.StatusCode) && attempt < _options.MaxRetries)
                {
                    var delay = ComputeRetryDelay(attempt, failure.RetryAfter);
                    if (delay is null)
                    {
                        // The service asked us to wait longer than we are willing to hold a call
                        // open. Report that instead of sleeping through it.
                        return fail(failure);
                    }

                    await Task.Delay(delay.Value, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                return fail(failure);
            }
            finally
            {
                response.Dispose();
            }
        }
    }

    /// <summary>
    /// Builds the request. The key goes on the header and nowhere else; the ETag, when a cached copy
    /// exists, goes on <c>If-None-Match</c> so a re-lookup can cost a <c>304</c> instead of a body.
    /// </summary>
    private HttpRequestMessage BuildRequest(Uri uri, string cacheKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);

        // The gateway authenticates every method and answers X-RateLimit-* / X-Quota-* on every
        // response. The key is read from memory only for this header.
        request.Headers.TryAddWithoutValidation("X-API-Key", _options.ApiKey);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        if (_options.EnableConditionalRequests && TryReadCacheEntry(cacheKey, out var entry) && entry?.ETag is not null)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", entry!.ETag);
        }

        return request;
    }

    /// <summary>Deserialises and maps a response document, keeping the raw text on a shape change.</summary>
    private MoyuResult<TValue> MapDocument<TDto, TValue>(
        string body,
        Func<TDto, TValue> map,
        Func<MoyuFailure, MoyuResult<TValue>> fail)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            var failure = new MoyuFailure
            {
                Code = MoyuFailureCode.MalformedResponse,
                Message = "moyu 返回了空响应体。"
            };
            LastError = failure.Message;
            return fail(failure);
        }

        TDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<TDto>(body, JsonOptions);
        }
        catch (JsonException ex)
        {
            // The important half of this branch: the body is kept, so a contract change shows up as
            // a diagnosable failure instead of an empty list.
            var failure = new MoyuFailure
            {
                Code = MoyuFailureCode.MalformedResponse,
                Message = $"无法解析 moyu 的响应（接口契约可能已变更）：{ex.Message}",
                ResponseBody = Truncate(body)
            };
            LastError = failure.Message;
            LastErrorBody = failure.ResponseBody;
            return fail(failure);
        }

        if (dto is null)
        {
            var failure = new MoyuFailure
            {
                Code = MoyuFailureCode.MalformedResponse,
                Message = "moyu 的响应反序列化为 null。",
                ResponseBody = Truncate(body)
            };
            LastError = failure.Message;
            LastErrorBody = failure.ResponseBody;
            return fail(failure);
        }

        return MoyuResult<TValue>.Ok(map(dto));
    }

    /// <summary>
    /// Turns a non-success response into a failure. Both error bodies are understood: an RFC 9457
    /// problem document, and the gateway's plain <c>{code, message}</c> used for 401/429.
    /// </summary>
    private async Task<MoyuFailure> BuildFailureAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            body = string.Empty;
        }

        var truncated = Truncate(body);
        var retryAfter = ReadRetryAfter(response);
        var code = MapStatusCode(response.StatusCode);

        var upstreamCode = ReadProblemString(body, "code");
        var requestId = ReadProblemString(body, "request_id")
                        ?? (response.Headers.TryGetValues("X-Request-ID", out var requestIds)
                            ? requestIds.FirstOrDefault()
                            : null);
        var detail = ReadProblemString(body, "detail") ?? ReadProblemString(body, "message");

        var (message, effectiveCode) = (code, detail) switch
        {
            (MoyuFailureCode.Unauthorized, _) =>
                ("moyu 拒绝了这把 API 密钥（401）。密钥可能无效或已被吊销，"
                 + "请到 https://developer.nextmoe.dev 重新铸造。", MoyuFailureCode.Unauthorized),
            (MoyuFailureCode.RateLimited, _) =>
                ($"moyu 的请求配额已用尽（429）。"
                 + (retryAfter is null ? "请稍后再试。" : $"请在约 {retryAfter.Value.TotalSeconds:F0} 秒后重试。"),
                    MoyuFailureCode.RateLimited),
            (MoyuFailureCode.NotFound, _) => ("moyu 上没有这个补丁或资源。", MoyuFailureCode.NotFound),
            (MoyuFailureCode.BadRequest, _) =>
                ($"moyu 认为请求参数不合法（400）{(string.IsNullOrWhiteSpace(detail) ? string.Empty : "：" + detail)}",
                    MoyuFailureCode.BadRequest),
            _ => ($"moyu 的接口暂时不可用（HTTP {status}）。", MoyuFailureCode.ServerUnavailable)
        };

        var failure = new MoyuFailure
        {
            Code = effectiveCode,
            Message = message,
            HttpStatus = status,
            UpstreamCode = upstreamCode,
            RequestId = requestId,
            RetryAfter = retryAfter,
            ResponseBody = truncated
        };

        LastError = $"moyu {status}: {upstreamCode ?? "no-code"}";
        LastErrorBody = truncated;
        return failure;
    }

    /// <summary>Maps an HTTP status onto the failure taxonomy.</summary>
    private static MoyuFailureCode MapStatusCode(HttpStatusCode status) => (int)status switch
    {
        400 or 422 => MoyuFailureCode.BadRequest,
        401 or 403 => MoyuFailureCode.Unauthorized,
        404 => MoyuFailureCode.NotFound,
        429 => MoyuFailureCode.RateLimited,
        >= 500 => MoyuFailureCode.ServerUnavailable,
        _ => MoyuFailureCode.ClientError
    };

    /// <summary>5xx, 429 and 503 are worth one more try; the rest are not.</summary>
    private static bool ShouldRetry(HttpStatusCode status) =>
        (int)status >= 500 || status == HttpStatusCode.TooManyRequests;

    /// <summary>
    /// The delay before a retry: the service's own <c>Retry-After</c> when it sent one and we are
    /// willing to wait that long, otherwise an exponential backoff. Returns <c>null</c> when the
    /// requested wait exceeds <see cref="MoyuOptions.MaxHonouredRetryAfter"/> — the caller then
    /// reports the failure instead of holding the call open.
    /// </summary>
    private TimeSpan? ComputeRetryDelay(int attempt, TimeSpan? retryAfter)
    {
        if (retryAfter is not null)
        {
            return retryAfter.Value <= _options.MaxHonouredRetryAfter ? retryAfter.Value : null;
        }

        var seconds = Math.Min(2 * Math.Pow(2, attempt), 30);
        var delay = TimeSpan.FromSeconds(seconds);
        return delay <= _options.MaxHonouredRetryAfter ? delay : null;
    }

    /// <summary>
    /// Reads <c>Retry-After</c>. Both spellings the RFC allows are accepted: delta-seconds and an
    /// HTTP-date. The shared <see cref="ApiClient"/> retry loop reads neither, which is the gap this
    /// class exists to close.
    /// </summary>
    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Retry-After", out var values))
        {
            return null;
        }

        var raw = values.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        raw = raw.Trim();

        if (double.TryParse(
                raw,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var seconds))
        {
            return seconds >= 0 ? TimeSpan.FromSeconds(seconds) : null;
        }

        if (DateTimeOffset.TryParse(
                raw,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal,
                out var when))
        {
            var delta = when - DateTimeOffset.UtcNow;
            return delta > TimeSpan.Zero ? delta : TimeSpan.Zero;
        }

        return null;
    }

    /// <summary>
    /// Records the rate/quota headers. Deliberately stored as text and never parsed into policy
    /// here: the contract does not fix their units across plans, and inventing a quota model from
    /// them would be a guess. They are shown in the diagnostics so a user can see why something
    /// failed.
    /// </summary>
    private void RecordRateLimitNotice(HttpResponseMessage response)
    {
        string[] headerNames = { "X-RateLimit-Remaining", "X-Quota-Remaining", "X-RateLimit-Reset", "X-Quota-Reset" };
        var parts = new List<string>();

        foreach (var name in headerNames)
        {
            if (response.Headers.TryGetValues(name, out var values))
            {
                var value = values.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    parts.Add($"{name}={value}");
                }
            }
        }

        if (parts.Count > 0)
        {
            LastRateLimitNotice = string.Join(", ", parts);
        }
    }

    /// <summary>Reads a string field out of a problem / gateway body without throwing.</summary>
    private static string? ReadProblemString(string? body, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty(propertyName, out var value)
                   && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Clears the per-call diagnostics so a stale error cannot be read as a fresh one.</summary>
    private void ResetDiagnostics()
    {
        LastError = null;
        LastErrorBody = null;
        LastRateLimitNotice = null;
    }

    private static bool IsNumericId(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.All(char.IsAsciiDigit);

    private static string Truncate(string? value) =>
        string.IsNullOrEmpty(value)
            ? string.Empty
            : value.Length <= MaxRecordedBodyLength
                ? value
                : value[..MaxRecordedBodyLength] + $" ... [truncated, {value.Length} chars]";

    // ---------------------------------------------------------------------------------------
    // Conditional-request cache. In-memory only, and only for documents this user actually asked
    // for. The report's rule: never build a local mirror of the site.
    // ---------------------------------------------------------------------------------------

    private sealed class CacheEntry
    {
        public required string Body { get; init; }

        public string? ETag { get; init; }

        public DateTimeOffset StoredAt { get; init; }
    }

    private bool TryReadCacheEntry(string key, out CacheEntry? entry)
    {
        lock (_cacheGate)
        {
            if (_cache.TryGetValue(key, out var found)
                && DateTimeOffset.UtcNow - found.StoredAt <= _options.CacheEntryLifetime)
            {
                entry = found;
                return true;
            }

            if (found is not null)
            {
                _cache.Remove(key);
            }

            entry = null;
            return false;
        }
    }

    private bool TryReadCache(string key, out string body)
    {
        if (TryReadCacheEntry(key, out var entry) && entry is not null)
        {
            body = entry.Body;
            return true;
        }

        body = string.Empty;
        return false;
    }

    private void StoreCache(string key, HttpResponseMessage response, string body)
    {
        if (!_options.EnableConditionalRequests)
        {
            return;
        }

        // A body we could not even parse is not worth revalidating: a 304 would only replay a
        // document that already failed to deserialise.
        if (string.IsNullOrWhiteSpace(body) || body.Length > 1024 * 1024)
        {
            return;
        }

        string? etag = response.Headers.ETag?.ToString();
        if (string.IsNullOrWhiteSpace(etag))
        {
            return;
        }

        lock (_cacheGate)
        {
            // Bounded: this is a handful of documents for the games one user is looking at.
            if (_cache.Count > 200)
            {
                _cache.Clear();
            }

            _cache[key] = new CacheEntry
            {
                Body = body,
                ETag = etag,
                StoredAt = DateTimeOffset.UtcNow
            };
        }
    }

    /// <summary>
    /// Builds the query string. Every value is percent-encoded, including the commas inside
    /// <c>refs</c>, so an anchor list cannot smuggle a second parameter into the URL.
    /// </summary>
    private sealed class QueryBuilder
    {
        private readonly List<string> _parts = new();

        public QueryBuilder Add(string name, string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                _parts.Add($"{Uri.EscapeDataString(name)}={Uri.EscapeDataString(value)}");
            }

            return this;
        }

        public QueryBuilder AddIf(bool condition, string name, string? value)
            => condition ? Add(name, value) : this;

        public string Build() => _parts.Count == 0 ? string.Empty : "?" + string.Join("&", _parts);
    }
}
