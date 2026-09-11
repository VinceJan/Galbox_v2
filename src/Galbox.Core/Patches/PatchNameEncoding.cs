using System.Text;

namespace Galbox.Core.Patches;

/// <summary>
/// Name encoding of archive entries. Japanese galgame patches are overwhelmingly packed on
/// CP932 (Shift-JIS) systems and the zip "UTF-8 name" flag is <b>not</b> trustworthy:
/// it is frequently left at 0 by the packer even when the bytes are CP932.
/// </summary>
public enum PatchNameEncoding
{
    /// <summary>Try UTF-8 (honouring the per-entry flag), fall back to CP932 then GBK when decoding produced replacement characters.</summary>
    Auto = 0,

    /// <summary>Force UTF-8 for entries that do not carry the UTF-8 flag.</summary>
    Utf8 = 1,

    /// <summary>Force CP932 / Shift-JIS (Japanese Windows, code page 932).</summary>
    Cp932 = 2,

    /// <summary>Force GBK / code page 936 (Simplified Chinese packers).</summary>
    Gbk = 3,

    /// <summary>Force Big5 / code page 950.</summary>
    Big5 = 4
}

/// <summary>Turns <see cref="PatchNameEncoding"/> values into real <see cref="Encoding"/> instances.</summary>
public static class PatchNameEncodings
{
    private static int _registered;

    /// <summary>
    /// Registers <see cref="CodePagesEncodingProvider"/> exactly once. Callers do not need to call
    /// this (the engine calls it lazily), but it is public so host applications can do it at startup.
    /// </summary>
    public static void EnsureRegistered()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 0)
        {
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            }
            catch (Exception)
            {
                // Registration is idempotent and never fatal; a second provider instance is harmless.
            }
        }
    }

    /// <summary>Resolves the concrete encoding for an explicit (non-auto) selection.</summary>
    public static Encoding Resolve(PatchNameEncoding encoding)
    {
        EnsureRegistered();
        return encoding switch
        {
            PatchNameEncoding.Utf8 => new UTF8Encoding(false, false),
            PatchNameEncoding.Cp932 => GetCodePage(932, "shift_jis"),
            PatchNameEncoding.Gbk => GetCodePage(936, "gb2312"),
            PatchNameEncoding.Big5 => GetCodePage(950, "big5"),
            _ => new UTF8Encoding(false, false)
        };
    }

    /// <summary>The ordered fallback list used by <see cref="PatchNameEncoding.Auto"/>.</summary>
    public static IReadOnlyList<PatchNameEncoding> AutoFallbackOrder { get; } = new[]
    {
        PatchNameEncoding.Utf8,
        PatchNameEncoding.Cp932,
        PatchNameEncoding.Gbk,
        PatchNameEncoding.Big5
    };

    /// <summary>The name used inside manifests and previews.</summary>
    public static string ToId(PatchNameEncoding encoding) => encoding switch
    {
        PatchNameEncoding.Utf8 => "utf-8",
        PatchNameEncoding.Cp932 => "cp932",
        PatchNameEncoding.Gbk => "gbk",
        PatchNameEncoding.Big5 => "big5",
        _ => "auto"
    };

    /// <summary>Parses an id produced by <see cref="ToId"/>.</summary>
    public static PatchNameEncoding FromId(string? id) => id?.Trim().ToLowerInvariant() switch
    {
        "utf-8" or "utf8" => PatchNameEncoding.Utf8,
        "cp932" or "shift_jis" or "shift-jis" or "sjis" or "932" => PatchNameEncoding.Cp932,
        "gbk" or "gb2312" or "936" or "cp936" => PatchNameEncoding.Gbk,
        "big5" or "950" or "cp950" => PatchNameEncoding.Big5,
        _ => PatchNameEncoding.Auto
    };

    /// <summary>
    /// Heuristic used by auto detection: a name that contains U+FFFD is proof that the decoding
    /// candidate was wrong (the byte sequence is not valid in that code page).
    /// </summary>
    public static bool LooksMisdecoded(string? name)
        => !string.IsNullOrEmpty(name) && name.Contains('\uFFFD');

    private static Encoding GetCodePage(int codePage, string webName)
    {
        try
        {
            return Encoding.GetEncoding(codePage);
        }
        catch (ArgumentException)
        {
            try
            {
                return Encoding.GetEncoding(webName);
            }
            catch (ArgumentException)
            {
                // The code pages provider is unavailable in this process; degrade to UTF-8 and let
                // the caller report the mismatch instead of crashing the whole preview.
                return new UTF8Encoding(false, false);
            }
        }
    }
}
