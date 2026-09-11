// Step 1 — save metadata, read straight from the ZIP container with zero pickle involvement.
//
// A Ren'Py 7.4 .save is a ZIP archive (ZIP_DEFLATED) with five entries:
//   screenshot.png  -> thumbnail
//   extra_info      -> UTF-8 user-typed save name (frequently empty)
//   json            -> PLAIN TEXT metadata: _save_name / _renpy_version / _version
//   renpy_version   -> e.g. "Ren'Py 7.4.11.2266"
//   log             -> the game state (a Python pickle; see RenpyLogReader)
//
// There is no _save_time inside the archive: Ren'Py itself uses the file mtime, so we do too.
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Galbox.Core.Saves;

/// <summary>Reads the metadata entries of a <c>.save</c> file without touching its pickle.</summary>
public static class RenpySaveMetadataReader
{
    /// <summary>Reads metadata for one save file.</summary>
    /// <param name="saveFilePath">Path to the <c>.save</c> file.</param>
    /// <param name="loadScreenshot">Whether to read <c>screenshot.png</c> into memory.</param>
    /// <exception cref="InvalidDataException">The file is not a readable save ZIP.</exception>
    public static RenpySaveMetadata Read(string saveFilePath, bool loadScreenshot = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(saveFilePath);

        var info = new FileInfo(saveFilePath);

        if (!info.Exists)
        {
            throw new FileNotFoundException($"Save file '{saveFilePath}' does not exist.", saveFilePath);
        }

        using var stream = new FileStream(
            saveFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);

        var entries = new List<string>();
        string? jsonText = null;
        string? renpyVersionText = null;
        string? extraInfo = null;
        byte[]? screenshot = null;
        var screenshotLength = 0;
        var logLength = 0;

        foreach (var entry in zip.Entries)
        {
            entries.Add(entry.FullName);

            switch (entry.FullName)
            {
                case "json":
                    jsonText = ReadEntryText(entry);
                    break;

                case "renpy_version":
                    renpyVersionText = ReadEntryText(entry)?.Trim();
                    break;

                case "extra_info":
                    extraInfo = ReadEntryText(entry);
                    break;

                case "screenshot.png":
                    screenshotLength = (int)Math.Min(entry.Length, int.MaxValue);

                    if (loadScreenshot)
                    {
                        using var png = entry.Open();
                        using var buffer = new MemoryStream();
                        png.CopyTo(buffer);
                        screenshot = buffer.ToArray();
                    }

                    break;

                case "log":
                    logLength = (int)Math.Min(entry.Length, int.MaxValue);
                    break;
            }
        }

        var parsed = ParseJsonMetadata(jsonText);
        var effectiveRenpyVersion = parsed.RenpyVersion
            ?? (NormalizeRenpyVersion(renpyVersionText) is { Length: > 0 } fromEntry ? fromEntry : null);

        return new RenpySaveMetadata
        {
            FilePath = info.FullName,
            SlotName = SlotNameFromFileName(info.Name),
            FileSize = info.Length,
            LastWriteTimeUtc = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
            SaveName = parsed.SaveName,
            GameVersion = parsed.GameVersion,
            RenpyVersion = effectiveRenpyVersion,
            RenpyVersionText = renpyVersionText,
            RenpyVersionTuple = parsed.Tuple,
            ExtraInfo = extraInfo,
            ScreenshotLength = screenshotLength,
            ScreenshotPng = screenshot,
            LogLength = logLength,
            ZipEntries = entries,
            JsonText = jsonText,
        };
    }

    /// <summary>Reads just the raw <c>log</c> pickle bytes of a save file.</summary>
    public static byte[]? ReadLogBytes(string saveFilePath)
    {
        using var stream = new FileStream(
            saveFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var entry = zip.GetEntry("log");

        if (entry is null)
        {
            return null;
        }

        using var log = entry.Open();
        using var buffer = new MemoryStream(
            entry.Length is > 0 and < int.MaxValue ? (int)entry.Length : 0);
        log.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// Derives the slot name from the file name the way Ren'Py does: <c>auto-3-LT1.save</c> is
    /// slot <c>auto-3-LT1</c>. The trailing <c>-LT1</c> is the multi-location suffix and the
    /// leading number is the save page.
    /// </summary>
    public static string SlotNameFromFileName(string fileName)
        => fileName.EndsWith(".save", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^".save".Length]
            : fileName;

    /// <summary>Renders <c>[7, 4, 11, 2266]</c> as <c>7.4.11.2266</c>.</summary>
    public static string CompactVersion(IEnumerable<int> parts)
        => string.Join('.', parts);

    /// <summary>Strips the leading <c>Ren'Py </c> from a <c>renpy_version</c> entry.</summary>
    public static string? NormalizeRenpyVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim();
        const string prefix = "Ren'Py ";

        return trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? trimmed[prefix.Length..]
            : trimmed;
    }

    private static string? ReadEntryText(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var buffer = new MemoryStream(
            entry.Length is > 0 and < int.MaxValue ? (int)entry.Length : 0);
        stream.CopyTo(buffer);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static (string? SaveName, string? GameVersion, string? RenpyVersion, IReadOnlyList<int> Tuple)
        ParseJsonMetadata(string? jsonText)
    {
        if (string.IsNullOrWhiteSpace(jsonText))
        {
            return (null, null, null, Array.Empty<int>());
        }

        try
        {
            using var document = JsonDocument.Parse(jsonText);
            var root = document.RootElement;

            var saveName = root.TryGetProperty("_save_name", out var sn) && sn.ValueKind == JsonValueKind.String
                ? sn.GetString()
                : null;

            var gameVersion = root.TryGetProperty("_version", out var gv) && gv.ValueKind == JsonValueKind.String
                ? gv.GetString()
                : null;

            var tuple = new List<int>();

            if (root.TryGetProperty("_renpy_version", out var rv) && rv.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in rv.EnumerateArray())
                {
                    if (part.TryGetInt32(out var value))
                    {
                        tuple.Add(value);
                    }
                }
            }

            var renpyVersion = tuple.Count > 0 ? CompactVersion(tuple) : null;
            return (saveName, gameVersion, renpyVersion, tuple);
        }
        catch (JsonException)
        {
            return (null, null, null, Array.Empty<int>());
        }
    }
}
