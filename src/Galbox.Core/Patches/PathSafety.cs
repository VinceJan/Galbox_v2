using System.Text;

namespace Galbox.Core.Patches;

/// <summary>How the engine reacts to a file name that Windows cannot represent verbatim.</summary>
public enum PatchNamePolicy
{
    /// <summary>Mangle the name deterministically and surface a warning in the preview (default).</summary>
    SanitizeWithWarning = 0,

    /// <summary>Refuse the entry entirely and record the rejection.</summary>
    Reject = 1
}

/// <summary>Outcome of mapping a raw archive entry name onto a safe relative path.</summary>
public sealed class PathSafetyResult
{
    /// <summary>Raw name exactly as it appeared in the archive.</summary>
    public required string OriginalName { get; init; }

    /// <summary>Normalised relative path (backslash separated), null when the entry was rejected.</summary>
    public string? RelativePath { get; init; }

    /// <summary>True when the entry must not be extracted / written.</summary>
    public required bool Rejected { get; init; }

    /// <summary>Why the entry was rejected.</summary>
    public PatchSecurityCode Code { get; init; } = PatchSecurityCode.Unspecified;

    /// <summary>Human readable explanation of the rejection.</summary>
    public string? RejectionReason { get; init; }

    /// <summary>True when the name was altered to be representable on Windows.</summary>
    public bool Sanitized { get; init; }

    /// <summary>What was changed, e.g. <c>invalid characters ':' '&gt;' replaced with '_'</c>.</summary>
    public string? SanitizationNote { get; init; }

    /// <summary>True when harmless <c>..</c> segments were collapsed (path could not escape).</summary>
    public bool ParentSegmentsCollapsed { get; init; }

    /// <summary>Convenience factory for a rejected entry.</summary>
    public static PathSafetyResult Reject(string originalName, PatchSecurityCode code, string reason, string? sanitizationNote = null)
        => new()
        {
            OriginalName = originalName,
            Rejected = true,
            Code = code,
            RejectionReason = reason,
            SanitizationNote = sanitizationNote
        };
}

/// <summary>
/// Entry-name normalisation and containment checks.
/// This is the single choke point for Zip Slip defence: every path that reaches the disk
/// must have passed through <see cref="Evaluate"/> and <see cref="LongPath.CombineUnder"/>.
/// </summary>
public static class PathSafety
{
    private static readonly char[] InvalidNameChars = { '<', '>', ':', '"', '|', '?', '*', '\0' };

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        "CONIN$", "CONOUT$"
    };

    /// <summary>
    /// Normalises an archive entry name into a relative path that is guaranteed to stay below the
    /// destination root, or rejects it with a recorded reason.
    /// </summary>
    /// <param name="rawName">Entry name as produced by the archive reader (already decoded).</param>
    /// <param name="policy">Whether unrepresentable names are mangled or rejected.</param>
    public static PathSafetyResult Evaluate(string rawName, PatchNamePolicy policy = PatchNamePolicy.SanitizeWithWarning)
    {
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return PathSafetyResult.Reject(rawName ?? string.Empty, PatchSecurityCode.InvalidCharacters, "Entry name is empty.");
        }

        var name = rawName.Replace('/', '\\');

        // Absolute / rooted forms: "\\server\share", "\foo", "C:\foo", "C:foo".
        if (name.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return PathSafetyResult.Reject(rawName, PatchSecurityCode.RootedEntryPath, "Entry name is a UNC path (\\\\server\\share).");
        }
        if (name.StartsWith('\\'))
        {
            return PathSafetyResult.Reject(rawName, PatchSecurityCode.RootedEntryPath, "Entry name is rooted (starts with a directory separator).");
        }
        if (name.Length >= 2 && name[1] == ':' && char.IsLetter(name[0]))
        {
            return PathSafetyResult.Reject(rawName, PatchSecurityCode.RootedEntryPath, $"Entry name carries a drive letter ('{name[0]}').");
        }

        var segments = name.Split('\\', StringSplitOptions.None);
        var stack = new List<string>(segments.Length);
        var notes = new List<string>();
        bool collapsedParent = false;

        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment == ".") continue;

            if (segment == "..")
            {
                if (stack.Count == 0)
                {
                    // Zip Slip: the entry would resolve above the destination root.
                    return PathSafetyResult.Reject(
                        rawName,
                        PatchSecurityCode.ParentTraversal,
                        $"'..' segment escapes the destination root (Zip Slip). Raw name: '{rawName}'.");
                }
                stack.RemoveAt(stack.Count - 1);
                collapsedParent = true;
                continue;
            }

            var sanitizedSegment = SanitizeSegment(segment, rawName, policy, notes, out var rejection);
            if (rejection is not null) return rejection;
            stack.Add(sanitizedSegment!);
        }

        if (stack.Count == 0)
        {
            return PathSafetyResult.Reject(rawName, PatchSecurityCode.InvalidCharacters, "Entry name resolves to the destination root itself.");
        }

        var relative = string.Join('\\', stack);
        var changed = !string.Equals(relative, name, StringComparison.Ordinal) || notes.Count > 0;

        if (collapsedParent)
        {
            notes.Insert(0, $"redundant '..' segments were collapsed: '{rawName}' -> '{relative}'");
        }

        return new PathSafetyResult
        {
            OriginalName = rawName,
            RelativePath = relative,
            Rejected = false,
            Sanitized = changed,
            SanitizationNote = changed ? string.Join("; ", notes) : null,
            ParentSegmentsCollapsed = collapsedParent
        };
    }

    /// <summary>
    /// Final containment assertion used right before any write: the combined path must be
    /// inside <paramref name="root"/> after canonicalisation.
    /// </summary>
    public static bool IsContained(string root, string candidateFullPath) => LongPath.IsUnder(root, candidateFullPath);

    private static string? SanitizeSegment(
        string segment,
        string rawName,
        PatchNamePolicy policy,
        List<string> notes,
        out PathSafetyResult? rejection)
    {
        rejection = null;

        var working = segment;

        // Device names are matched on the part before the first dot: "CON.txt" is still the console device.
        var stem = working;
        var dot = working.IndexOf('.');
        if (dot >= 0) stem = working[..dot];
        if (ReservedDeviceNames.Contains(stem.TrimEnd(' ', '.')))
        {
            if (policy == PatchNamePolicy.Reject)
            {
                rejection = PathSafetyResult.Reject(rawName, PatchSecurityCode.ReservedDeviceName, $"'{segment}' is a reserved Windows device name.");
                return null;
            }
            working = segment[..(dot < 0 ? segment.Length : dot)] + "_" + (dot < 0 ? string.Empty : segment[dot..]);
            notes.Add($"reserved device name '{stem}' rewritten to '{working}'");
        }

        if (working.Contains(':'))
        {
            if (policy == PatchNamePolicy.Reject)
            {
                rejection = PathSafetyResult.Reject(rawName, PatchSecurityCode.AlternateDataStream, $"'{segment}' contains ':' (alternate data stream separator).");
                return null;
            }
            working = working.Replace(':', '_');
            notes.Add("':' replaced with '_' (alternate data stream separator)");
        }

        var hasInvalid = false;
        var builder = new StringBuilder(working.Length);
        foreach (var ch in working)
        {
            if (Array.IndexOf(InvalidNameChars, ch) >= 0 || ch < ' ')
            {
                hasInvalid = true;
                builder.Append('_');
            }
            else
            {
                builder.Append(ch);
            }
        }

        if (hasInvalid)
        {
            if (policy == PatchNamePolicy.Reject)
            {
                rejection = PathSafetyResult.Reject(rawName, PatchSecurityCode.InvalidCharacters, $"'{segment}' contains characters Windows rejects (< > : \" | ? * or control characters).");
                return null;
            }
            working = builder.ToString();
            notes.Add("invalid characters replaced with '_'");
        }

        // Windows silently drops trailing dots and spaces, which would create a path that no longer
        // matches what the archive declared. Make the normalisation explicit instead.
        if (working.Length > 0 && (working[^1] == '.' || working[^1] == ' '))
        {
            if (policy == PatchNamePolicy.Reject)
            {
                rejection = PathSafetyResult.Reject(rawName, PatchSecurityCode.InvalidCharacters, $"'{segment}' ends with a dot or space, which Windows cannot represent.");
                return null;
            }
            working = working.TrimEnd('.', ' ') + "_";
            notes.Add($"trailing dot/space replaced with '_' (now '{working}')");
        }

        if (working.Length == 0)
        {
            rejection = PathSafetyResult.Reject(rawName, PatchSecurityCode.InvalidCharacters, "Entry name segment became empty after sanitisation.");
            return null;
        }

        if (working.Length > 255)
        {
            if (policy == PatchNamePolicy.Reject)
            {
                rejection = PathSafetyResult.Reject(rawName, PatchSecurityCode.InvalidCharacters, $"Name segment is longer than 255 characters ({working.Length}).");
                return null;
            }
            var dot2 = working.LastIndexOf('.');
            var extension = dot2 > 0 && working.Length - dot2 <= 16 ? working[dot2..] : string.Empty;
            var keep = 255 - extension.Length - 8;
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(working)))[..8].ToLowerInvariant();
            working = working[..Math.Max(1, keep)] + "~" + hash + extension;
            notes.Add("overlong name segment truncated with a content hash suffix");
        }

        return working;
    }
}
