namespace Galbox.Core.Api;

/// <summary>
/// The compliance boundary of the moyu integration, in one place.
///
/// <para><b>Why this type exists at all.</b></para>
///
/// The upstream research report (<c>_product/design/moyu-moe-integration-research.md</c>, §5.1)
/// recorded <c>https://www.moyu.moe/robots.txt</c> verbatim. It contains:
///
/// <code>
/// User-agent: *
/// Allow: /
/// Disallow: /api
/// </code>
///
/// Every usable first-party data endpoint on that site lives under <c>/api/v1/*</c> — search,
/// patch detail, resource list and the link endpoint that hands out a direct download URL. The
/// report proved those endpoints answer <c>200</c> anonymously, so they are <i>technically</i>
/// reachable. That is not the question. The site says no, and a desktop application that ships to
/// users does not get to overrule that. The second and third pieces of evidence point the same way:
/// the official OpenAPI spec states that revealing a link "is a separate, rate-limited,
/// per-resource request whose whole purpose is that links cannot be harvested in bulk", and the
/// server source puts a 30/min rate limiter on exactly that endpoint.
///
/// The authorised alternative is the official NextMoe "moyu face" at
/// <c>https://api.nextmoe.dev/v2/moyu/*</c>: documented, <c>x-stability: stable</c>, self-service
/// <c>nmk_</c> keys, and deliberately published for downstream applications.
///
/// <para><b>What this class guarantees.</b></para>
///
/// <see cref="MoyuApi"/> builds every request URI through <see cref="EnsureApiUri"/> and hands
/// every browser URL through <see cref="IsAllowedWebUrl"/>. Both refuse the forbidden surface, so
/// "we never call <c>/api/v1</c>" is a property of the code rather than a promise in a commit
/// message. The acceptance check A63 asserts the whole matrix, and independent of that the
/// allow-list below is the only construction path a request URI has.
/// </summary>
public static class MoyuComplianceGuard
{
    /// <summary>The one API origin this application is allowed to talk to.</summary>
    public const string ApiHost = "api.nextmoe.dev";

    /// <summary>The only path prefix that may be requested on that origin.</summary>
    public const string ApiPathPrefix = "/v2/moyu/";

    /// <summary>
    /// The path prefix the site forbids via <c>robots.txt</c>. Nothing in this assembly may request
    /// it, and <see cref="EnsureApiUri"/> exists to make that structurally impossible.
    /// </summary>
    public const string ForbiddenPathPrefix = "/api";

    /// <summary>The public site. Pages here are explicitly allowed (`Allow: /`).</summary>
    public const string WebHost = "www.moyu.moe";

    /// <summary>Hosts that may be opened in a browser for a patch page.</summary>
    private static readonly string[] AllowedWebHosts = { "www.moyu.moe", "moyu.moe" };

    /// <summary>
    /// The identifying User-Agent. The site is a free community project; an operator who sees this
    /// traffic in their logs must be able to tell what it is and where to complain.
    /// </summary>
    public const string UserAgent = "Galbox/2.0 (+https://github.com/VinceJan/Galbox_v2)";

    /// <summary>
    /// True only for a request URI this application is allowed to send: HTTPS, the
    /// <c>api.nextmoe.dev</c> origin, under <c>/v2/moyu/</c>, and not under the forbidden
    /// <c>/api</c> prefix.
    /// </summary>
    public static bool IsAllowedApiUri(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri)
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.Equals(uri.Host, ApiHost, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var path = uri.AbsolutePath;

        // Belt and braces: even on the right host, nothing under /api is ever requested. The
        // check runs before the allow-list so that a future mistake in the prefix constant
        // cannot open the door.
        if (path.StartsWith(ForbiddenPathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return path.StartsWith(ApiPathPrefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Builds and validates a request URI. This is the <b>only</b> way
    /// <see cref="MoyuApi"/> produces a URI, so a build that would leave the allow-listed surface
    /// fails loudly here instead of quietly leaving a forbidden request on the wire.
    /// </summary>
    /// <exception cref="InvalidOperationException">The resulting URI is not allow-listed.</exception>
    public static Uri EnsureApiUri(Uri baseAddress, string relativePathAndQuery)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePathAndQuery);

        var combined = new Uri(baseAddress, relativePathAndQuery);
        if (!IsAllowedApiUri(combined))
        {
            throw new InvalidOperationException(
                "Refusing to build a moyu request outside the authorised surface. "
                + $"Allowed: https://{ApiHost}{ApiPathPrefix}*. "
                + $"Forbidden (robots.txt `Disallow: {ForbiddenPathPrefix}`): anything under {ForbiddenPathPrefix}. "
                + $"Attempted: {combined}");
        }

        return combined;
    }

    /// <summary>
    /// True when <paramref name="url"/> may be opened in the user's browser as a patch page:
    /// an absolute HTTPS URL on the moyu site, with a real host and no API path.
    ///
    /// <para>
    /// The API origin is refused on purpose. The compliant download hop is "send a reader to the
    /// page", and a URL that points at an endpoint is not a page.
    /// </para>
    /// </summary>
    public static bool IsAllowedWebUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!AllowedWebHosts.Any(host => string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var path = uri.AbsolutePath;
        if (path.StartsWith(ForbiddenPathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // A bare origin is not a patch page.
        return path.Length > 1;
    }

    /// <summary>
    /// True when <paramref name="uri"/> points into the surface the site asks clients not to
    /// request. Used by the acceptance check to assert that no recorded request ever targeted it.
    /// </summary>
    public static bool IsForbiddenPath(string? pathOrQuery)
    {
        if (string.IsNullOrWhiteSpace(pathOrQuery))
        {
            return false;
        }

        return pathOrQuery.StartsWith(ForbiddenPathPrefix, StringComparison.OrdinalIgnoreCase);
    }
}
