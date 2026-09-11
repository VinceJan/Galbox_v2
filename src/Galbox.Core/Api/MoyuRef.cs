using System.Text.RegularExpressions;

namespace Galbox.Core.Api;

/// <summary>
/// A game anchor in the only two spellings the official moyu face accepts:
/// <c>vndb:&lt;id&gt;</c> or <c>catalog:&lt;id&gt;</c>.
///
/// <para>
/// <b>A Bangumi id is not an anchor.</b> This is the sharpest practical constraint of the
/// integration, and the official spec states it explicitly: <c>patch.id</c>, <c>vndb_id</c> and
/// <c>catalog_work_id</c> are "three different id spaces for the same game — never equal, never
/// interchangeable". Galbox's scraping main line is Bangumi, so a game whose metadata carries only
/// a Bangumi subject id <b>cannot be looked up</b> here. The honest answer is to say so
/// (<see cref="MoyuFailureCode.NoAnchor"/>), never to guess an anchor and never to fall back to a
/// search endpoint the site forbids.
/// </para>
///
/// <para>
/// The dependency this creates is reported, not fixed here: the scraping stage needs to persist a
/// <c>vndb_id</c> alongside the Bangumi subject so the patch centre has something to ask with.
/// </para>
/// </summary>
public sealed class MoyuRef
{
    /// <summary>The VNDB source prefix.</summary>
    public const string VndbSource = "vndb";

    /// <summary>The NextMoe catalog source prefix.</summary>
    public const string CatalogSource = "catalog";

    /// <summary>
    /// A VNDB id as the site stores it: a leading <c>v</c> then digits (<c>v4</c>, <c>v65869</c>).
    /// The digit-only form <c>65869</c> is accepted by <see cref="FromVndbId"/> and normalised.
    /// </summary>
    private static readonly Regex VndbIdPattern = new(
        @"^v[0-9]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex CatalogIdPattern = new(
        @"^[0-9]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private MoyuRef(string source, string externalId)
    {
        Source = source;
        ExternalId = externalId;
    }

    /// <summary>Either <c>vndb</c> or <c>catalog</c>.</summary>
    public string Source { get; }

    /// <summary>The id without its source prefix, e.g. <c>v65869</c> or <c>61311</c>.</summary>
    public string ExternalId { get; }

    /// <summary>True for a VNDB anchor.</summary>
    public bool IsVndb => string.Equals(Source, VndbSource, StringComparison.Ordinal);

    /// <summary>True for a NextMoe catalog anchor.</summary>
    public bool IsCatalog => string.Equals(Source, CatalogSource, StringComparison.Ordinal);

    /// <summary>The wire spelling, e.g. <c>vndb:v65869</c>.</summary>
    public override string ToString() => $"{Source}:{ExternalId}";

    /// <summary>
    /// Parses a wire anchor such as <c>vndb:v65869</c> or <c>catalog:61311</c>. Returns <c>null</c>
    /// for anything else — including a bare number, which is <b>not</b> a valid anchor even though
    /// it looks like one.
    /// </summary>
    public static MoyuRef? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // `refs` travels as a query parameter, so a caller may hand us the percent-encoded form.
        var value = Uri.UnescapeDataString(text.Trim());
        var separator = value.IndexOf(':');
        if (separator <= 0 || separator == value.Length - 1)
        {
            return null;
        }

        var source = value[..separator].Trim().ToLowerInvariant();
        var externalId = value[(separator + 1)..].Trim();

        return source switch
        {
            VndbSource when VndbIdPattern.IsMatch(externalId) => new MoyuRef(VndbSource, externalId),
            CatalogSource when CatalogIdPattern.IsMatch(externalId) => new MoyuRef(CatalogSource, externalId),
            _ => null
        };
    }

    /// <summary>Builds a catalog anchor from a NextMoe work id.</summary>
    public static MoyuRef? FromCatalogId(string? catalogWorkId)
    {
        if (string.IsNullOrWhiteSpace(catalogWorkId) || !CatalogIdPattern.IsMatch(catalogWorkId.Trim()))
        {
            return null;
        }

        return new MoyuRef(CatalogSource, catalogWorkId.Trim());
    }

    /// <summary>
    /// Builds a VNDB anchor from an id in either spelling (<c>v65869</c> or <c>65869</c>).
    /// Returns <c>null</c> when the value is not a VNDB id — a Bangumi id lands here, and the
    /// correct outcome for one is "no anchor".
    /// </summary>
    public static MoyuRef? FromVndbId(string? vndbId)
    {
        if (string.IsNullOrWhiteSpace(vndbId))
        {
            return null;
        }

        var value = vndbId.Trim();

        // A value that carries some other source prefix is not a VNDB id.
        if (value.Contains(':'))
        {
            return null;
        }

        if (CatalogIdPattern.IsMatch(value))
        {
            value = "v" + value;
        }

        return VndbIdPattern.IsMatch(value) ? new MoyuRef(VndbSource, "v" + value.TrimStart('v', 'V')) : null;
    }

    /// <summary>
    /// Picks the best anchor from a game's identifiers. A VNDB id wins because it is the site's own
    /// dedupe key; the catalog work id is the fallback.
    ///
    /// <para>
    /// A Bangumi subject id is deliberately <b>not</b> an input to this method: there is no
    /// conversion between the two id spaces, and inventing one would produce confidently wrong
    /// patch lists.
    /// </para>
    /// </summary>
    public static MoyuRef? PickAnchor(string? vndbId, string? catalogWorkId)
        => FromVndbId(vndbId) ?? FromCatalogId(catalogWorkId);

    /// <inheritdoc />
    public override bool Equals(object? obj) =>
        obj is MoyuRef other
        && string.Equals(Source, other.Source, StringComparison.Ordinal)
        && string.Equals(ExternalId, other.ExternalId, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public override int GetHashCode() =>
        HashCode.Combine(Source, ExternalId.ToLowerInvariant());
}
