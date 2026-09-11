using System.Globalization;
using System.Text;

namespace Galbox.Core.Api;

/// <summary>
/// Normalizes game names and scores how well a candidate title matches a search term.
/// </summary>
/// <remarks>
/// This lives in Galbox.Core (not in the WinUI project) so the matching rules can be
/// exercised by a console probe without starting the shell.
///
/// Fixes D8 of the scraping diagnosis: the previous implementation compared raw strings,
/// so purely cosmetic differences were treated as real mismatches and good candidates were
/// thrown away. Real examples measured against the live APIs:
/// <list type="bullet">
///   <item><description>"SabbatOfTheWitch" vs "Sabbat of the Witch" scored 84.21 (rejected at the 90 threshold).</description></item>
///   <item><description>"千恋万花" vs "千恋＊万花" scored 80.00 (full-width asterisk).</description></item>
///   <item><description>"Dreamin'_Her" vs "Dreamin'Her  -僕は、彼女の夢を見る。-".</description></item>
/// </list>
/// After normalization all three are recognized as the same game.
/// </remarks>
public static class GameNameMatcher
{
    /// <summary>
    /// Score used when two names are identical (or identical after normalization).
    /// </summary>
    public const double ExactMatchScore = 100.0;

    /// <summary>
    /// Score used when one normalized name fully contains the other.
    /// </summary>
    public const double ContainsMatchScore = 90.0;

    /// <summary>
    /// Shortest normalized search term that is still allowed to participate in a
    /// "contains" match. Prevents single-character names from matching everything.
    /// </summary>
    private const int MinimumLengthForContainsMatch = 2;

    /// <summary>
    /// Normalizes a game name into a comparable token.
    /// Case, width (full/half), whitespace, underscores and punctuation are all removed,
    /// while CJK characters and digits are preserved.
    /// </summary>
    /// <param name="name">Raw name (may be null).</param>
    /// <returns>Normalized comparable form; empty string when the input has no letters or digits.</returns>
    public static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var widened = ToCompatibilityForm(name);
        var builder = new StringBuilder(widened.Length);

        foreach (var ch in widened)
        {
            // Keep letters (this includes CJK ideographs and kana) and decimal digits only.
            var category = char.GetUnicodeCategory(ch);
            if (category is UnicodeCategory.UppercaseLetter
                or UnicodeCategory.LowercaseLetter
                or UnicodeCategory.TitlecaseLetter
                or UnicodeCategory.ModifierLetter
                or UnicodeCategory.OtherLetter
                or UnicodeCategory.DecimalDigitNumber)
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Calculates a match score (0-100) between a search term and the candidate titles
    /// returned by a scraping source.
    /// </summary>
    /// <param name="searchName">Name that was searched for.</param>
    /// <param name="potentialTitles">Titles reported by the source (any language / alias).</param>
    /// <returns>The best score across all candidate titles.</returns>
    public static double CalculateMatchScore(string? searchName, IReadOnlyList<string>? potentialTitles)
    {
        if (string.IsNullOrWhiteSpace(searchName) || potentialTitles == null || potentialTitles.Count == 0)
        {
            return 0;
        }

        var searchRaw = searchName.Trim().ToLowerInvariant();
        var searchNormalized = Normalize(searchName);

        var bestScore = 0.0;

        foreach (var title in potentialTitles)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var titleRaw = title.Trim().ToLowerInvariant();

            // Exact match on the raw string.
            if (searchRaw == titleRaw)
            {
                return ExactMatchScore;
            }

            var titleNormalized = Normalize(title);

            // Exact match after removing spaces / punctuation / case / width differences.
            if (searchNormalized.Length > 0 && searchNormalized == titleNormalized)
            {
                return ExactMatchScore;
            }

            // One normalized name contains the other (e.g. an alias inside a long official title).
            if (searchNormalized.Length >= MinimumLengthForContainsMatch &&
                titleNormalized.Length > 0 &&
                (titleNormalized.Contains(searchNormalized, StringComparison.Ordinal) ||
                 searchNormalized.Contains(titleNormalized, StringComparison.Ordinal)))
            {
                bestScore = Math.Max(bestScore, ContainsMatchScore);
                continue;
            }

            // Fall back to edit distance, measured on the normalized forms so that
            // punctuation and spacing no longer count as differences.
            var similarity = CalculateLevenshteinSimilarity(searchNormalized, titleNormalized);
            bestScore = Math.Max(bestScore, similarity);
        }

        return bestScore;
    }

    /// <summary>
    /// Calculates similarity using Levenshtein distance. Returns a score from 0 to 100.
    /// </summary>
    public static double CalculateLevenshteinSimilarity(string? source, string? target)
    {
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(target))
        {
            return 0;
        }

        if (string.Equals(source, target, StringComparison.Ordinal))
        {
            return ExactMatchScore;
        }

        var distance = LevenshteinDistance(source, target);
        var maxLength = Math.Max(source.Length, target.Length);

        if (maxLength == 0)
        {
            return ExactMatchScore;
        }

        var similarity = (1.0 - (double)distance / maxLength) * 100;
        return Math.Round(similarity, 2);
    }

    /// <summary>
    /// Calculates the Levenshtein distance between two strings.
    /// </summary>
    public static int LevenshteinDistance(string source, string target)
    {
        var sourceLength = source.Length;
        var targetLength = target.Length;

        if (sourceLength == 0) return targetLength;
        if (targetLength == 0) return sourceLength;

        // Optimized single-row algorithm.
        var previousRow = new int[targetLength + 1];
        var currentRow = new int[targetLength + 1];

        for (var i = 0; i <= targetLength; i++)
        {
            previousRow[i] = i;
        }

        for (var i = 0; i < sourceLength; i++)
        {
            currentRow[0] = i + 1;

            for (var j = 0; j < targetLength; j++)
            {
                var cost = source[i] == target[j] ? 0 : 1;
                currentRow[j + 1] = Math.Min(
                    Math.Min(currentRow[j] + 1, previousRow[j + 1] + 1),
                    previousRow[j] + cost);
            }

            (previousRow, currentRow) = (currentRow, previousRow);
        }

        return previousRow[targetLength];
    }

    /// <summary>
    /// Applies Unicode compatibility normalization so that full-width forms
    /// (e.g. "＊", "Ａ") and half-width kana collapse onto their canonical counterparts.
    /// </summary>
    private static string ToCompatibilityForm(string value)
    {
        try
        {
            return value.Normalize(NormalizationForm.FormKC);
        }
        catch (ArgumentException)
        {
            // Malformed UTF-16 (unpaired surrogate) - fall back to the raw value.
            return value;
        }
    }
}
