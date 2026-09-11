using System.Security.Cryptography;
using System.Text;

namespace Galbox.PatchVerifier;

/// <summary>One file captured in a snapshot.</summary>
internal sealed record SnapshotFile(string RelativePath, long SizeBytes, string Sha256);

/// <summary>
/// Recursive, hash-based picture of a directory tree. Everything the harness claims about "the state of
/// the game directory" is proven with two of these, never with a visual inspection.
/// </summary>
internal sealed class DirectorySnapshot
{
    private DirectorySnapshot(string root, IReadOnlyList<SnapshotFile> files)
    {
        Root = root;
        Files = files;
    }

    public string Root { get; }

    public IReadOnlyList<SnapshotFile> Files { get; }

    public long TotalBytes => Files.Sum(f => f.SizeBytes);

    public static DirectorySnapshot Capture(string root, IReadOnlyList<string>? excludeRelativePrefixes = null)
    {
        var files = new List<SnapshotFile>();
        if (!Directory.Exists(root)) return new DirectorySnapshot(root, files);

        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, path);

            // The engine's own bookkeeping lives inside the game directory by design
            // (gameRoot\.galbox\patch-manifest.json). It is excluded from the "game state" snapshot
            // and called out explicitly in the report instead of being silently ignored.
            if (excludeRelativePrefixes is not null &&
                excludeRelativePrefixes.Any(prefix => relative.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var info = new FileInfo(path);
            files.Add(new SnapshotFile(relative, info.Length, Hash(path)));
        }

        files.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        return new DirectorySnapshot(root, files);
    }

    public static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>Returns the differences between two snapshots, in both directions.</summary>
    public static IReadOnlyList<string> Diff(DirectorySnapshot before, DirectorySnapshot after)
    {
        var differences = new List<string>();
        var beforeMap = before.Files.ToDictionary(f => f.RelativePath, StringComparer.OrdinalIgnoreCase);
        var afterMap = after.Files.ToDictionary(f => f.RelativePath, StringComparer.OrdinalIgnoreCase);

        foreach (var file in before.Files)
        {
            if (!afterMap.TryGetValue(file.RelativePath, out var now))
            {
                differences.Add($"- MISSING after: {file.RelativePath} (was {file.SizeBytes} bytes {file.Sha256[..16]})");
            }
            else if (!string.Equals(file.Sha256, now.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                differences.Add($"- CHANGED: {file.RelativePath} {file.Sha256[..16]} -> {now.Sha256[..16]} ({file.SizeBytes} -> {now.SizeBytes} bytes)");
            }
        }

        foreach (var file in after.Files)
        {
            if (!beforeMap.ContainsKey(file.RelativePath))
            {
                differences.Add($"+ ADDED: {file.RelativePath} ({file.SizeBytes} bytes {file.Sha256[..16]})");
            }
        }

        return differences;
    }

    public bool IsIdenticalTo(DirectorySnapshot other)
        => Diff(this, other).Count == 0 && Files.Count == other.Files.Count;

    public void Print(Action<string> write, string indent = "    ")
    {
        foreach (var file in Files)
        {
            write($"{indent}{file.RelativePath,-70} {file.SizeBytes,8} B  {file.Sha256}");
        }
        if (Files.Count == 0) write($"{indent}(no files)");
    }
}

/// <summary>Collects assertions so that a failure is reported instead of aborting the run.</summary>
internal sealed class CheckList
{
    private readonly List<string> _failures = new();
    private int _passed;

    public void That(string description, bool condition, string? detail = null)
    {
        if (condition)
        {
            _passed++;
            Console.WriteLine($"  [PASS] {description}");
            return;
        }

        var message = detail is null ? description : $"{description} -- {detail}";
        _failures.Add(message);
        Console.WriteLine($"  [FAIL] {message}");
    }

    public void Equal<T>(string description, T expected, T actual)
        => That(description, EqualityComparer<T>.Default.Equals(expected, actual), $"expected '{expected}', actual '{actual}'");

    public int Passed => _passed;

    public IReadOnlyList<string> Failures => _failures;

    public bool AllPassed => _failures.Count == 0;
}

/// <summary>Console + report-file writer.</summary>
internal sealed class Reporter : IDisposable
{
    private readonly StringBuilder _buffer = new();
    private readonly string? _path;

    public Reporter(string? path)
    {
        _path = path;
        if (path is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        }
    }

    public void Line(string text = "")
    {
        Console.WriteLine(text);
        _buffer.AppendLine(text);
    }

    public void Section(string title)
    {
        Line();
        Line(new string('=', 100));
        Line($"== {title}");
        Line(new string('=', 100));
    }

    public void Sub(string title)
    {
        Line();
        Line($"-- {title}");
    }

    public void Dispose()
    {
        if (_path is null) return;
        File.WriteAllText(_path, _buffer.ToString(), new UTF8Encoding(false));
    }
}
