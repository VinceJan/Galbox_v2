using System.Globalization;
using System.Text.RegularExpressions;

namespace Galbox.Core.Api;

/// <summary>
/// Parses the upstream <c>size</c> field into bytes.
///
/// <para>
/// <b>The unit is binary, and that was calibrated, not assumed.</b> The research report §4.2
/// confirmed it against a real file: the API reports <c>"17.953 MB"</c> and a Range request for the
/// same resource answers <c>content-range: bytes 0-2047/18825520</c>. And
/// <c>18825520 / 1048576 = 17.953110</c> exactly — so <c>1 MB = 1048576</c> bytes, not 10^6.
/// The report also recorded both spellings in the wild: <c>"17.953 MB"</c> (with a space) and
/// <c>"17.953MB"</c> (without).
/// </para>
///
/// <para>
/// <b>Unparseable input yields 0, never an exception and never a guess.</b> The value feeds the
/// download-matcher, where "unknown size" is a legitimate state; making it throw would turn a
/// cosmetic upstream change into a hard failure of the download hop.
/// </para>
/// </summary>
public static class MoyuSize
{
    /// <summary>Bytes in one binary megabyte. Calibrated by the report (see the type remarks).</summary>
    public const long BytesPerMegabyte = 1024L * 1024L;

    /// <summary>
    /// Accepted shape: a non-negative decimal number, optional whitespace, then one of the
    /// documented units. Anything else (a negative number, extra words, scientific notation) is
    /// rejected rather than partially matched.
    /// </summary>
    private static readonly Regex SizePattern = new(
        @"^(?<value>\d+(?:\.\d+)?)\s*(?<unit>B|KB|MB|GB|TB)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// Refuses absurd magnitudes instead of overflowing. 100 TB is far beyond the documented 20 GB
    /// per-file ceiling, so anything larger means the field is not what we think it is.
    /// </summary>
    private const long MaximumPlausibleBytes = 100L * 1024 * 1024 * 1024 * 1024;

    /// <summary>
    /// Parses a size string such as <c>"17.953 MB"</c>. Returns <c>false</c> for anything it does
    /// not fully understand, leaving <paramref name="bytes"/> at 0.
    /// </summary>
    public static bool TryParseBytes(string? text, out long bytes)
    {
        bytes = 0;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var match = SizePattern.Match(text.Trim());
        if (!match.Success)
        {
            return false;
        }

        if (!double.TryParse(
                match.Groups["value"].Value,
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var value))
        {
            return false;
        }

        var unit = match.Groups["unit"].Value.ToUpperInvariant();
        var multiplier = unit switch
        {
            "B" => 1L,
            "KB" => 1024L,
            "MB" => BytesPerMegabyte,
            "GB" => 1024L * 1024L * 1024L,
            "TB" => 1024L * 1024L * 1024L * 1024L,
            _ => 0L
        };

        if (multiplier == 0)
        {
            return false;
        }

        var computed = value * multiplier;

        // NaN survives a bad parse, and infinity means the multiplication overflowed.
        if (double.IsNaN(computed) || double.IsInfinity(computed) || computed < 0)
        {
            return false;
        }

        if (computed > MaximumPlausibleBytes)
        {
            return false;
        }

        bytes = (long)Math.Round(computed, MidpointRounding.AwayFromZero);

        // `value == 0` keeps "0 MB" parseable while rejecting a magnitude that rounded to zero
        // (e.g. "0.0000001 MB"), which would be a value the field cannot really mean.
        return bytes > 0 || value == 0;
    }

    /// <summary>
    /// Parses a size string, returning 0 when it cannot be understood. The convenience overload for
    /// callers that treat "unknown" and "zero" the same way (the download matcher does).
    /// </summary>
    public static long ParseBytes(string? text) => TryParseBytes(text, out var bytes) ? bytes : 0L;

    /// <summary>
    /// Formats a byte count the same way the upstream field does, so a size the user sees in Galbox
    /// and the size on the site read the same. Uses the same binary unit as
    /// <c>PatchRecord.FormattedSize</c>.
    /// </summary>
    public static string Format(long bytes)
    {
        if (bytes <= 0)
        {
            return "未知";
        }

        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{value:0} {units[unitIndex]}"
            : $"{value:0.###} {units[unitIndex]}";
    }
}
