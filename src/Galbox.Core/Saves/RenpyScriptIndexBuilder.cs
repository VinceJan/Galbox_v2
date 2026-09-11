// Step 3 — static script knowledge from the game's .rpa archives.
//
// Three things are built here:
//   1. the label dictionary        label name -> definition line (and its logical script name)
//   2. the call-site dictionary    "_call_characg_78" -> "だーれだ"
//   3. the CG gallery slot list    gallery.image("0101") -> "0101"
//
// PATCH PRIORITY (design report §7.1): the same script exists in both game/archive.rpa and
// 00patch/00patch.rpa, and their line numbers drift by one. Three independent signals show the
// 00patch copy is the one the engine executes (environment.txt search path, the file name
// recorded in Context.current, and the file name used as a persistent._seen_ever key), so patch
// copies win whenever an entry name appears in both archives. Both copies are still reported so
// callers can see the duplication.
using System.Text.RegularExpressions;
using Galbox.Core.Saves.Pickle;
using Galbox.Core.Saves.Rpa;

namespace Galbox.Core.Saves;

/// <summary>Builds the static label / call-site / gallery dictionary for a Ren'Py game.</summary>
public static partial class RenpyScriptIndexBuilder
{
    /// <summary>Script files that may declare gallery slots, in search order.</summary>
    private static readonly string[] GalleryScriptNames = { "gallery.rpy", "moviegallery.rpy" };

    /// <summary>Builds the index by reading every <c>.rpy</c> member of the game's archives.</summary>
    /// <param name="gameRoot">Installation root (contains <c>00patch</c>).</param>
    /// <param name="gameFolder">The <c>game</c> folder (contains <c>archive.rpa</c>).</param>
    /// <param name="warnings">Collects non-fatal problems.</param>
    public static RenpyScriptIndex Build(string gameRoot, string gameFolder, List<string>? warnings = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameFolder);
        warnings ??= new List<string>();

        var definitions = new List<RenpyLabelDefinition>();
        var labelLines = new Dictionary<string, int>(StringComparer.Ordinal);
        var labelFiles = new Dictionary<string, string>(StringComparer.Ordinal);
        var callSites = new Dictionary<string, string>(StringComparer.Ordinal);
        var sourceArchives = new Dictionary<string, string>(StringComparer.Ordinal);
        var labelCountByFile = new Dictionary<string, int>(StringComparer.Ordinal);
        var entryNameOwners = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var gallerySlots = new List<string>();

        // Archives are visited in Ren'Py search-path order: 00patch first, then game.
        var archives = RenpySaveLocator.EnumerateArchives(gameRoot, gameFolder).ToList();
        var archiveByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var archivePath in archives.Where(File.Exists))
        {
            var logicalFolder = RenpySaveLocator.LogicalSourceName(gameRoot, archivePath);
            archiveByPath[archivePath] = logicalFolder;

            RpaArchive archive;

            try
            {
                archive = RpaArchive.Open(archivePath);
            }
            catch (Exception ex) when (ex is InvalidDataException or PickleScanException or IOException)
            {
                warnings.Add($"Could not open archive '{archivePath}': {ex.Message}");
                continue;
            }

            using (archive)
            {
                List<string> scriptEntries;

                try
                {
                    scriptEntries = archive.FindByExtension(".rpy").ToList();
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException)
                {
                    warnings.Add($"Could not enumerate '{archivePath}': {ex.Message}");
                    continue;
                }

                foreach (var entryName in scriptEntries)
                {
                    if (!entryNameOwners.TryGetValue(entryName, out var owners))
                    {
                        owners = new List<string>();
                        entryNameOwners[entryName] = owners;
                    }

                    owners.Add(archivePath);

                    string? text;

                    try
                    {
                        text = archive.ReadTextEntry(entryName);
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException)
                    {
                        warnings.Add($"Could not read '{entryName}' from '{archivePath}': {ex.Message}");
                        continue;
                    }

                    if (text is null)
                    {
                        continue;
                    }

                    var isEffective = owners.Count == 1;
                    var logicalName = logicalFolder + "/" + entryName;
                    sourceArchives[logicalName] = archivePath;

                    var parsed = ParseScript(text, logicalName, archivePath, isEffective, callSites);

                    definitions.AddRange(parsed.Labels);
                    labelCountByFile[logicalName] = parsed.Labels.Count;

                    foreach (var label in parsed.Labels)
                    {
                        // Patch copies win: only claim the name when nothing has claimed it yet.
                        if (!labelLines.ContainsKey(label.Name))
                        {
                            labelLines[label.Name] = label.Line;
                            labelFiles[label.Name] = label.SourceFile;
                        }
                    }

                    if (GalleryScriptNames.Contains(
                            System.IO.Path.GetFileName(entryName), StringComparer.OrdinalIgnoreCase))
                    {
                        foreach (var slot in parsed.GallerySlots)
                        {
                            if (!gallerySlots.Contains(slot, StringComparer.Ordinal))
                            {
                                gallerySlots.Add(slot);
                            }
                        }
                    }
                }
            }
        }

        var duplicated = entryNameOwners
            .Where(kv => kv.Value.Count > 1)
            .Select(kv => kv.Key)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        if (duplicated.Count > 0)
        {
            warnings.Add(
                $"{duplicated.Count} script(s) exist in more than one archive; the 00patch copy was "
                + "treated as the effective one: " + string.Join(", ", duplicated.Take(10)));
        }

        return new RenpyScriptIndex
        {
            Labels = definitions,
            LabelLines = labelLines,
            LabelFiles = labelFiles,
            StoryLabels = labelFiles
                .Where(kv => !IsHelperScript(kv.Value))
                .Select(kv => kv.Key)
                .ToHashSet(StringComparer.Ordinal),
            CallSiteToLabel = callSites,
            GallerySlots = gallerySlots,
            SourceArchives = sourceArchives,
            LabelCountByFile = labelCountByFile,
            DuplicatedScripts = duplicated,
            Warnings = warnings,
        };
    }

    /// <summary>
    /// True when a script only holds reusable helper labels. Ren'Py games keep their presentation
    /// macros (sprite/background/sound/screen helpers) in <c>macro</c> folders; the labels there
    /// are real but they are not story scenes.
    /// </summary>
    public static bool IsHelperScript(string logicalName)
    {
        foreach (var segment in logicalName.Split('/', '\\'))
        {
            if (segment.Equals("macro", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Parses one <c>.rpy</c> source into labels, call sites and gallery slots.</summary>
    /// <param name="text">Script text.</param>
    /// <param name="logicalName">Logical script name, e.g. <c>00patch/scenario/main_scenario.rpy</c>.</param>
    /// <param name="archivePath">Archive the text came from.</param>
    /// <param name="isEffectiveCopy">Whether this copy is the one the engine loads.</param>
    /// <param name="callSites">Call-site dictionary to extend (shared across scripts).</param>
    public static ParsedScript ParseScript(
        string text,
        string logicalName,
        string archivePath,
        bool isEffectiveCopy,
        Dictionary<string, string> callSites)
    {
        var labels = new List<RenpyLabelDefinition>();
        var gallerySlots = new List<string>();
        var lines = text.Split('\n');

        string? currentLabel = null;
        var lineNumber = 0;

        foreach (var rawLine in lines)
        {
            lineNumber++;

            var labelMatch = LabelRegex().Match(rawLine);

            if (labelMatch.Success)
            {
                currentLabel = labelMatch.Groups[1].Value;
                labels.Add(new RenpyLabelDefinition(currentLabel, logicalName, lineNumber, archivePath, isEffectiveCopy));
            }

            var callMatch = CallSiteRegex().Match(rawLine);

            if (callMatch.Success && currentLabel is not null)
            {
                callSites[callMatch.Groups[1].Value] = currentLabel;
            }

            // gallery.image("0101") declares a CG slot; gallery.unlock_image(...) does not.
            foreach (Match m in GalleryImageRegex().Matches(rawLine))
            {
                gallerySlots.Add(m.Groups[1].Value);
            }
        }

        return new ParsedScript(labels, gallerySlots);
    }

    /// <summary>Result of parsing one script file.</summary>
    /// <param name="Labels">Labels declared in this script.</param>
    /// <param name="GallerySlots">CG slot ids declared by <c>gallery.image(...)</c>.</param>
    public sealed record ParsedScript(
        IReadOnlyList<RenpyLabelDefinition> Labels,
        IReadOnlyList<string> GallerySlots);

    // Ren'Py label statements: "label foo:", "label foo(...):", "    label foo:".
    [GeneratedRegex(@"^\s*label\s+([^\s:(]+)", RegexOptions.CultureInvariant)]
    private static partial Regex LabelRegex();

    // "from _call_characg_78" suffixes produced by Ren'Py's call-site naming.
    [GeneratedRegex(@"from\s+(_call_[A-Za-z0-9_]+)", RegexOptions.CultureInvariant)]
    private static partial Regex CallSiteRegex();

    // Only the slot declarations, i.e. gallery.image("NN01").
    [GeneratedRegex(@"gallery\.image\(\s*[""']([^""']+)[""']\s*\)", RegexOptions.CultureInvariant)]
    private static partial Regex GalleryImageRegex();
}
