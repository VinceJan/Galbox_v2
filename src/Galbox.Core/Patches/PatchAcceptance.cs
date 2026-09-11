namespace Galbox.Core.Patches;

/// <summary>What kind of payload a package carries, from the point of view of the overlay installer.</summary>
public enum PatchContentKind
{
    /// <summary>Looks like an overlay patch: game data, scripts, executables, translations.</summary>
    Overlay = 0,

    /// <summary>Save data (full-CG unlocks, clear saves). Belongs to save management, not to the overlay installer.</summary>
    SaveData = 1,

    /// <summary>Could not be classified.</summary>
    Unknown = 2
}

/// <summary>Why a package was sniffed as save data.</summary>
public sealed class SaveContentSniff
{
    /// <summary>Sniffed kind.</summary>
    public required PatchContentKind Kind { get; init; }

    /// <summary>Confidence in [0,1]; only used for reporting, the decision itself is binary.</summary>
    public double Confidence { get; init; }

    /// <summary>Ratio of file entries that matched a save-data pattern.</summary>
    public double MatchedRatio { get; init; }

    /// <summary>Examples of the files that triggered the sniff.</summary>
    public IReadOnlyList<string> Examples { get; init; } = Array.Empty<string>();

    /// <summary>Human readable justification, shown verbatim in the refusal message.</summary>
    public required string Reason { get; init; }
}

/// <summary>
/// Decides whether a package may go through the overlay installer at all.
/// A declared <c>save</c> type is a hard refusal: full-CG saves are save data, and dropping them into the
/// game directory is both wrong and dangerous (it can overwrite the user's own progress).
/// </summary>
public static class PatchAcceptance
{
    /// <summary>Patch type ids that mean "this is save data, not an overlay patch".</summary>
    public static readonly string[] SaveTypeIds = { "save", "savedata", "存档", "全cg存档" };

    /// <summary>True when the caller's declared types mark this resource as save data.</summary>
    public static bool IsDeclaredSave(IEnumerable<string>? declaredTypes)
        => declaredTypes is not null && declaredTypes.Any(t =>
            !string.IsNullOrWhiteSpace(t) && SaveTypeIds.Contains(t.Trim().ToLowerInvariant()));

    /// <summary>Runs the declared-type gate. Throws when the package must not be installed.</summary>
    public static void EnsureNotDeclaredSave(IEnumerable<string>? declaredTypes)
    {
        if (!IsDeclaredSave(declaredTypes)) return;
        throw new PatchRejectedException(
            PatchRejectionCode.SaveTypePatch,
            "This resource is declared as a save-data patch (type 'save'), so it must not be laid over the game directory. " +
            "Full-CG / clear saves belong to save management, where they are copied into a save folder and backed up.",
            "Open the save manager and import the archive there instead.");
    }

    /// <summary>Sniffs extracted file names for save-data patterns.</summary>
    public static SaveContentSniff Sniff(IEnumerable<ExtractedArchiveFile> files)
    {
        var list = files.ToList();
        if (list.Count == 0)
        {
            return new SaveContentSniff
            {
                Kind = PatchContentKind.Unknown,
                Reason = "No files were extracted."
            };
        }

        var saveDirectoryNames = new[] { "save", "saves", "savedata", "save_data", "存档", "セーブ", "cg", "cgmode", "cleardata" };
        var saveExtensions = new[] { ".sav", ".save", ".ksd", ".asd", ".qsd", ".gsd", ".dat.bak", ".sav.bak" };

        var matches = new List<string>();
        foreach (var file in list)
        {
            var relative = file.RelativePath;
            var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fileName = segments[^1];
            var lowerName = fileName.ToLowerInvariant();
            var extension = Path.GetExtension(fileName).ToLowerInvariant();

            var inSaveFolder = segments.Length >= 2 && saveDirectoryNames.Contains(segments[^2].Trim().ToLowerInvariant());
            var saveExtension = saveExtensions.Contains(extension);
            var saveFileName = lowerName.StartsWith("save", StringComparison.Ordinal) ||
                               lowerName.Contains("セーブ") ||
                               lowerName.Contains("存档") ||
                               lowerName.Contains("全cg") ||
                               lowerName.Contains("global.sav");

            if (inSaveFolder || saveExtension || saveFileName) matches.Add(relative);
        }

        var ratio = matches.Count / (double)list.Count;
        var looksLikeSave = ratio >= 0.7 && matches.Count > 0;

        return new SaveContentSniff
        {
            Kind = looksLikeSave ? PatchContentKind.SaveData : PatchContentKind.Overlay,
            Confidence = ratio,
            MatchedRatio = ratio,
            Examples = matches.Take(5).ToList(),
            Reason = looksLikeSave
                ? $"{matches.Count}/{list.Count} entries look like save data (e.g. {string.Join(", ", matches.Take(3))})."
                : $"Only {matches.Count}/{list.Count} entries look like save data; the package is treated as an overlay patch."
        };
    }

    /// <summary>Runs the content gate on an already extracted package.</summary>
    public static void EnsureNotSaveLikeContent(IEnumerable<ExtractedArchiveFile> files, bool allowSaveLikeContent)
    {
        if (allowSaveLikeContent) return;
        var sniff = Sniff(files);
        if (sniff.Kind != PatchContentKind.SaveData) return;

        throw new PatchRejectedException(
            PatchRejectionCode.SaveLikeContent,
            $"The archive looks like save data rather than an overlay patch. {sniff.Reason} " +
            "Overlaying save files onto the game directory would overwrite the user's own progress, so the install is refused.",
            "Import the archive through the save manager, or set PatchInstallerOptions.AllowSaveLikeContent if you are certain it is an overlay patch.");
    }
}
