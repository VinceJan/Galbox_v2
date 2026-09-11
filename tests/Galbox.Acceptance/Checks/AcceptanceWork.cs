using System.IO.Compression;
using System.Security.Cryptography;

namespace Galbox.Acceptance.Checks;

/// <summary>
/// Scratch space and fixture builders shared by the checks that have to create real files.
/// </summary>
/// <remarks>
/// Everything lives under <c>%LocalAppData%\Galbox\acceptance\work</c>, i.e. inside the folder the
/// acceptance run already owns. No check in this file may touch the user's library, the user's
/// database or the user's game folders.
/// </remarks>
internal static class AcceptanceWork
{
    /// <summary>Absolute path of the acceptance scratch root.</summary>
    internal static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Galbox",
        "acceptance",
        "work");

    /// <summary>
    /// Creates (and empties) the scratch folder of one check.
    /// </summary>
    /// <remarks>
    /// The folder name carries the process id on purpose. Every worktree in this repository runs its
    /// own copy of this harness, and they all share <c>%LocalAppData%\Galbox</c>: without the pid, a
    /// check running in a sibling worktree would delete the fixtures of this one mid-run.
    /// </remarks>
    internal static string Create(string checkId)
    {
        var directory = Path.Combine(Root, $"{checkId}-{Environment.ProcessId}");
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>Writes a text file, creating its folder.</summary>
    internal static string WriteText(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Writes a file of the given size filled with one repeated pattern.</summary>
    internal static string WritePattern(string path, int sizeBytes, byte fill = 0x41)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllBytes(path, Enumerable.Repeat(fill, sizeBytes).ToArray());
        return path;
    }

    /// <summary>Reads a file back as text, or reports a readable placeholder.</summary>
    internal static string ReadTextOrMissing(string path)
        => File.Exists(path) ? File.ReadAllText(path) : "(file does not exist)";

    /// <summary>SHA-256 of a file, as uppercase hex.</summary>
    internal static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <summary>Creates a zip archive from literal entry contents, in the given order.</summary>
    internal static string CreateZip(string zipPath, params (string EntryName, byte[] Content)[] entries)
    {
        var directory = Path.GetDirectoryName(zipPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }

        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var (entryName, content) in entries)
        {
            var entry = archive.CreateEntry(entryName, CompressionLevel.NoCompression);
            using var stream = entry.Open();
            stream.Write(content, 0, content.Length);
        }

        return zipPath;
    }

    /// <summary>Lists the file entry names of a zip archive.</summary>
    internal static List<string> ZipEntryNames(string zipPath)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        return archive.Entries
            .Where(entry => !string.IsNullOrEmpty(entry.Name))
            .Select(entry => entry.FullName)
            .ToList();
    }

    /// <summary>Lists the files under a folder, relative to it, sorted.</summary>
    internal static List<string> ListFilesRelative(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return new List<string>();
        }

        return Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(directory, path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Measured image signature of a file: format, size and the leading bytes.</summary>
    internal static (string Format, long Size, string HeaderHex) InspectImage(string path)
    {
        if (!File.Exists(path))
        {
            return ("(missing)", 0, string.Empty);
        }

        var bytes = File.ReadAllBytes(path);
        var header = bytes.Take(Math.Min(12, bytes.Length)).ToArray();

        var format = header switch
        {
            [0xFF, 0xD8, 0xFF, ..] => "JPEG (FF D8 FF)",
            [0x89, 0x50, 0x4E, 0x47, ..] => "PNG (89 50 4E 47)",
            [0x47, 0x49, 0x46, 0x38, ..] => "GIF (47 49 46 38)",
            [0x42, 0x4D, ..] => "BMP (42 4D)",
            [0x52, 0x49, 0x46, 0x46, ..] => "RIFF container (WEBP)",
            _ => "UNKNOWN"
        };

        return (format, bytes.LongLength, Convert.ToHexString(header));
    }
}
