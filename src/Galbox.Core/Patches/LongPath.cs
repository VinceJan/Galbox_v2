namespace Galbox.Core.Patches;

/// <summary>
/// File-system helpers that survive Windows-specific limits (MAX_PATH, reserved device names,
/// path canonicalisation) without leaking the technique into call sites.
/// </summary>
public static class LongPath
{
    private const string ExtendedPrefix = @"\\?\";
    private const string ExtendedUncPrefix = @"\\?\UNC\";

    /// <summary>
    /// Returns a form of <paramref name="path"/> that Windows will not truncate at 260 characters.
    /// Relative paths and paths that are already extended are returned unchanged.
    /// </summary>
    public static string Ensure(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        if (path.StartsWith(ExtendedPrefix, StringComparison.Ordinal)) return path;
        if (!Path.IsPathFullyQualified(path)) return path;

        var full = Path.GetFullPath(path);
        if (full.StartsWith(ExtendedPrefix, StringComparison.Ordinal)) return full;

        return full.StartsWith(@"\\", StringComparison.Ordinal)
            ? ExtendedUncPrefix + full[2..]
            : ExtendedPrefix + full;
    }

    /// <summary>Strips the extended-length prefix, producing a path that is safe to display to the user.</summary>
    public static string Strip(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        if (path.StartsWith(ExtendedUncPrefix, StringComparison.Ordinal)) return @"\\" + path[ExtendedUncPrefix.Length..];
        if (path.StartsWith(ExtendedPrefix, StringComparison.Ordinal)) return path[ExtendedPrefix.Length..];
        return path;
    }

    /// <summary>Canonicalises a path (no trailing separator, no <c>..</c>) for comparison purposes.</summary>
    public static string Canonical(string path)
    {
        var full = Path.GetFullPath(Strip(path));
        var root = Path.GetPathRoot(full);
        if (!string.IsNullOrEmpty(root) && full.Length > root.Length)
        {
            full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        return full;
    }

    /// <summary>True when <paramref name="candidate"/> is the same directory as, or nested inside, <paramref name="root"/>.</summary>
    public static bool IsUnder(string root, string candidate)
    {
        var r = Canonical(root);
        var c = Canonical(candidate);
        if (string.Equals(r, c, StringComparison.OrdinalIgnoreCase)) return true;
        var prefix = r.EndsWith(Path.DirectorySeparatorChar) ? r : r + Path.DirectorySeparatorChar;
        return c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Combines a root and a relative path and asserts the result stays inside the root.</summary>
    public static string CombineUnder(string root, string relativePath)
    {
        var combined = Canonical(Path.Combine(Canonical(root), relativePath));
        if (!IsUnder(root, combined))
        {
            throw new PatchSecurityException(
                PatchSecurityCode.PathEscapesRoot,
                $"Resolved path '{Strip(combined)}' escapes root '{Strip(root)}'.");
        }
        return combined;
    }

    /// <summary>Returns the relative path of <paramref name="fullPath"/> under <paramref name="root"/>, or null when not nested.</summary>
    public static string? RelativeUnder(string root, string fullPath)
    {
        if (!IsUnder(root, fullPath)) return null;
        var r = Canonical(root);
        var c = Canonical(fullPath);
        if (string.Equals(r, c, StringComparison.OrdinalIgnoreCase)) return string.Empty;
        return c[(r.Length + 1)..];
    }

    /// <summary>True when any component of the path chain (from <paramref name="root"/> down to the file) is a reparse point.</summary>
    public static bool HasReparsePointInChain(string root, string fullPath)
    {
        var r = Canonical(root);
        var c = Canonical(fullPath);
        if (!IsUnder(r, c)) return true;

        var relative = RelativeUnder(r, c);
        if (string.IsNullOrEmpty(relative)) return false;

        var current = r;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            try
            {
                if (!File.Exists(current) && !Directory.Exists(current)) continue;
                var attributes = File.GetAttributes(LongPath.Ensure(current));
                if ((attributes & FileAttributes.ReparsePoint) != 0) return true;
            }
            catch (IOException)
            {
                // Unreadable intermediate: treat as suspicious rather than silently continuing.
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        }
        return false;
    }
}
