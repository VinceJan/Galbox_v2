namespace Galbox.Core.Api;

/// <summary>
/// Why a moyu call did not produce data.
///
/// <para>
/// The whole reason this enum exists: this project's worst historical defect pattern is a source
/// that returns an empty list when it is actually broken, leaving the user unable to tell
/// "this game has no patches" from "the client is misconfigured" or "the service is down".
/// Every failure mode below is therefore a distinct, nameable value, and
/// <see cref="MoyuResult{T}.Failed"/> is the flag a caller checks instead of counting rows.
/// </para>
/// </summary>
public enum MoyuFailureCode
{
    /// <summary>No failure.</summary>
    None = 0,

    /// <summary>
    /// No <c>nmk_</c> API key is configured, so no request was sent.
    /// This is <b>not</b> an empty result and must never be presented as one.
    /// </summary>
    NotConfigured,

    /// <summary>
    /// The game has no usable anchor (<c>vndb:vXXXX</c> / <c>catalog:&lt;id&gt;</c>), so there was
    /// nothing to ask about. The public face does not accept a Bangumi id.
    /// </summary>
    NoAnchor,

    /// <summary>More anchors were supplied than one batch request accepts.</summary>
    TooManyAnchors,

    /// <summary>The API key was rejected (401) - invalid or revoked.</summary>
    Unauthorized,

    /// <summary>Rate or quota limit reached (429). <see cref="MoyuFailure.RetryAfter"/> says when to return.</summary>
    RateLimited,

    /// <summary>The request was refused as malformed (400).</summary>
    BadRequest,

    /// <summary>Nothing exists at that id (404).</summary>
    NotFound,

    /// <summary>Upstream failure (5xx) after retries.</summary>
    ServerUnavailable,

    /// <summary>The request could not reach the service at all.</summary>
    NetworkUnreachable,

    /// <summary>The request timed out.</summary>
    Timeout,

    /// <summary>
    /// The response arrived but did not match the documented contract. The raw body is kept on
    /// <see cref="MoyuFailure.ResponseBody"/> so this never degrades into "0 results".
    /// </summary>
    MalformedResponse,

    /// <summary>Our own client-side rate limiter decided the request should wait longer than allowed.</summary>
    ClientThrottled,

    /// <summary>A programming error in the client itself (e.g. a URI outside the allow-list).</summary>
    ClientError
}

/// <summary>
/// One failure: a code the caller can branch on, a message written for the user, and the evidence.
/// </summary>
public sealed class MoyuFailure
{
    /// <summary>What went wrong.</summary>
    public required MoyuFailureCode Code { get; init; }

    /// <summary>A message suitable for display. Never contains the API key.</summary>
    public required string Message { get; init; }

    /// <summary>HTTP status, when a response was actually received.</summary>
    public int? HttpStatus { get; init; }

    /// <summary>Upstream error code (`code` from an RFC 9457 problem document), when present.</summary>
    public string? UpstreamCode { get; init; }

    /// <summary>Upstream `request_id`, for a support request.</summary>
    public string? RequestId { get; init; }

    /// <summary>How long to wait before retrying, when the service said so.</summary>
    public TimeSpan? RetryAfter { get; init; }

    /// <summary>
    /// Truncated raw response body. Kept because a drifted contract must be *visible*: this is the
    /// field that stops a schema change from silently reading as "no patches".
    /// </summary>
    public string? ResponseBody { get; init; }

    /// <summary>True when retrying later could plausibly succeed.</summary>
    public bool IsRetryable => Code is MoyuFailureCode.RateLimited
        or MoyuFailureCode.ServerUnavailable
        or MoyuFailureCode.NetworkUnreachable
        or MoyuFailureCode.Timeout;

    /// <inheritdoc />
    public override string ToString() => $"moyu: {Code}: {Message}";
}

/// <summary>
/// The outcome of a moyu call: either data, or a failure that says why.
///
/// <para>
/// A successful call with no rows is <b>not</b> a failure - the public face reports anchors that
/// matched nothing in <c>missing[]</c>, and that is normal, expected information. A failed call is
/// never an empty list. <see cref="Failed"/> is the only correct way to tell them apart.
/// </para>
/// </summary>
/// <typeparam name="T">Payload type.</typeparam>
public sealed class MoyuResult<T>
{
    private MoyuResult(T? value, MoyuFailure? failure)
    {
        Value = value;
        Failure = failure;
    }

    /// <summary>The payload, or <c>null</c> when <see cref="Failed"/>.</summary>
    public T? Value { get; }

    /// <summary>Why the call failed, or <c>null</c> on success.</summary>
    public MoyuFailure? Failure { get; }

    /// <summary>True when the call produced no data, for any reason (including "genuinely none").</summary>
    public bool Failed => Failure is not null;

    /// <summary>True when the call succeeded (which may still mean zero rows).</summary>
    public bool Succeeded => Failure is null;

    /// <summary>Wraps a successful payload.</summary>
    public static MoyuResult<T> Ok(T value) => new(value, null);

    /// <summary>Wraps a failure.</summary>
    public static MoyuResult<T> Fail(MoyuFailure failure) =>
        new(default, failure ?? throw new ArgumentNullException(nameof(failure)));

    /// <summary>Convenience constructor for a failure described by a code and a message.</summary>
    public static MoyuResult<T> Fail(MoyuFailureCode code, string message) =>
        Fail(new MoyuFailure { Code = code, Message = message });
}

/// <summary>
/// The outcome of a batch anchor lookup: the rows that matched, plus the anchors that did not.
///
/// <para>
/// <c>missing[]</c> is normal information, not an error. The public face echoes back every anchor
/// it had nothing for, which is exactly how "no patches for this game" is expressed.
/// </para>
/// </summary>
public sealed class MoyuPatchBatch
{
    /// <summary>Patch pages that matched.</summary>
    public IReadOnlyList<MoyuPatch> Items { get; init; } = Array.Empty<MoyuPatch>();

    /// <summary>Anchors that matched nothing, in the spelling that was sent.</summary>
    public IReadOnlyList<string> Missing { get; init; } = Array.Empty<string>();

    /// <summary>Cursor for the next page, when the service returned one.</summary>
    public string? NextCursor { get; init; }

    /// <summary>Collection size; <c>null</c> unless <c>include_total=true</c> was requested.</summary>
    public int? Total { get; init; }
}

/// <summary>A page of patch resources.</summary>
public sealed class MoyuResourcePage
{
    /// <summary>Resources on this page.</summary>
    public IReadOnlyList<MoyuResource> Items { get; init; } = Array.Empty<MoyuResource>();

    /// <summary>Cursor for the next page, or <c>null</c> on the last page.</summary>
    public string? NextCursor { get; init; }

    /// <summary>Collection size; <c>null</c> unless <c>include_total=true</c> was requested.</summary>
    public int? Total { get; init; }
}
