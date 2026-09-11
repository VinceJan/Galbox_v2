// The mapping layer: RenpySaveAnalysis (Galbox.Core.Saves) -> SaveNodeMetadata (Galbox.Data) + evidence JSON.
//
// This file holds every product judgement the scan makes, and nothing else, so that the judgements can be
// reviewed and unit-tested without a database, a UI or a file system. The three product rules it encodes
// (`_product/design/save-node-integration-plan.md` §3.2 and §5):
//
//   1. CG data is GAME-LEVEL. Ren'Py keeps CG unlocks in the shared `persistent` file, so the same numbers
//      are written to every node and mean "as of this scan". They are not a property of the individual save.
//   2. One node per slot, not one per file. Ren'Py's MultiLocation writes every save to two directories;
//      the analyzer already picks the newest copy, and the other copies are recorded as mirrors.
//   3. A parse failure must be stored, not skipped. ParseStatus/ParseError are always written, and the
//      failure text explains what is missing in the product's own language.
//
// and the honesty red lines of §5:
//   * a route name is only ever written with the "疑似" prefix and only when the scene-set clustering
//     produced a genuine group (never from a route variable: none of the 12 reference saves has one);
//   * "chapter progress" is scenes-unlocked / scenes-total, never a script line percentage, and it stays
//     null when the denominator is unknown (unknown is not 0%);
//   * failures are visible in the database.
using System.Text.Json;
using Galbox.Core.Saves;
using Galbox.Data.Entities;

namespace Galbox.Services.Saves;

/// <summary>One save slot, already mapped to the data layer's contract.</summary>
public sealed class MappedSaveSlot
{
    /// <summary>Ren'Py slot key derived from the file name (<c>auto-3-LT1</c>); the stable slot identity.</summary>
    public required string SlotKey { get; init; }

    /// <summary>Effective file path plus every mirrored copy, in analyzer order (effective copy first).</summary>
    public required IReadOnlyList<string> AllPaths { get; init; }

    /// <summary>The values to hand to <see cref="SaveNode.ApplyMetadata"/>.</summary>
    public required SaveNodeMetadata Metadata { get; init; }

    /// <summary>The evidence payload for <see cref="SaveNode.ExtendedMetadataJson"/>.</summary>
    public required SaveNodeExtendedMetadata Extended { get; init; }

    /// <summary>True when the parser could not read the save at all (still persisted, on purpose).</summary>
    public bool IsParseFailure => Metadata.ParseStatus == SaveParseStatus.Failed;
}

/// <summary>A whole scan of one game's save directory, ready to be upserted.</summary>
public sealed class SaveNodeMapping
{
    /// <summary>Mapped slots, in analyzer order (newest save first).</summary>
    public required IReadOnlyList<MappedSaveSlot> Slots { get; init; }

    /// <summary>Game-level diagnostics: parser warnings plus the mapping's own remarks.</summary>
    public required IReadOnlyList<string> Diagnostics { get; init; }

    /// <summary>Unlocked CG count as of the scan (game-level; 0 when unknown).</summary>
    public int CgUnlockedCount { get; init; }

    /// <summary>Total CG count declared by the gallery script (0 when unknown).</summary>
    public int CgTotalCount { get; init; }

    /// <summary>Unlocked CG ids, sorted by the parser.</summary>
    public IReadOnlyList<string> CgUnlockedIds { get; init; } = Array.Empty<string>();

    /// <summary>Scenes unlocked as of the scan (analyzer definition incl. helper labels).</summary>
    public int UnlockedSceneCount { get; init; }

    /// <summary>Labels declared by the scripts (the denominator of the scene progress).</summary>
    public int TotalSceneCount { get; init; }

    /// <summary>Story-only unlocked scene count (helper labels excluded).</summary>
    public int UnlockedStorySceneCount { get; init; }

    /// <summary>Story-only total scene count.</summary>
    public int TotalStorySceneCount { get; init; }

    /// <summary>Save directory name from <c>config.save_directory</c>, e.g. <c>dreamin_her-1631775296</c>.</summary>
    public string? SaveDirectoryName { get; init; }

    /// <summary>Number of clusters the scene-set clustering produced.</summary>
    public int SceneClusterCount { get; init; }

    /// <summary>Number of clusters that contain two or more slots, i.e. that produced a suspected group.</summary>
    public int SuspectedRouteGroupCount { get; init; }
}

/// <summary>Maps a <see cref="RenpySaveAnalysis"/> onto the data layer's <see cref="SaveNodeMetadata"/>.</summary>
public static class RenpySaveNodeMapper
{
    /// <summary>
    /// Prefix of every speculative route name. The UI can (and should) use it to render the value as a
    /// guess, and must never strip it before deciding how to present the field.
    /// </summary>
    public const string SpeculativeRoutePrefix = "疑似路线";

    /// <summary>Upper bound on the scene labels copied into the evidence payload.</summary>
    public const int SceneSequenceLimit = 100;

    /// <summary>Column limit of <see cref="SaveNode.ParseError"/>; longer reasons are truncated.</summary>
    public const int ParseErrorLimit = 1000;

    /// <summary>
    /// Why no route variable could be used. Stored next to every speculative group so the reason survives
    /// outside this source file.
    /// </summary>
    public const string RouteVariableUnavailableReason =
        "Ren'Py 只序列化本局被改动过的变量；12/12 实测存档里没有任何路线变量（kakoroute/risounomirai/yumenomirai 等），"
        + "所以分支只能按\"场景集合重叠\"推测，不能当成事实。";

    /// <summary>
    /// Maps a full analysis. The result is a pure function of the analysis: mapping the same save directory
    /// twice produces byte-identical values, which is what makes the scan idempotent.
    /// </summary>
    /// <param name="analysis">Analyzer output for one game installation.</param>
    public static SaveNodeMapping Map(RenpySaveAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);

        var diagnostics = new List<string>(analysis.Warnings);
        var scriptIndex = analysis.ScriptIndex;
        var cg = analysis.CgProgress;
        var persistent = analysis.Persistent;

        // Installed game version, for the "save is older than the game" note. Read once (it opens an archive).
        var installedVersion = TryDetectInstalledVersion(analysis, diagnostics);

        // ---------------------------------------------------------------- game-level values
        // CG and "seen scenes" live in the shared `persistent` file, which is why they are identical on every
        // node. They are computed once here and shared; the payload marks the scope explicitly.
        var unlockedCgIds = cg?.UnlockedSlots ?? (IReadOnlyList<string>)Array.Empty<string>();
        var cgUnlocked = cg?.UnlockedCount ?? 0;
        var cgTotal = cg?.TotalCount ?? 0;
        var cgIdsJson = cg is null ? null : JsonSerializer.Serialize(unlockedCgIds);

        if (cg is null)
        {
            diagnostics.Add(
                "CG progress is unavailable (no gallery script or no readable 'persistent' file); "
                + "CgUnlockedCount/CgTotalCount are left at 0, which means 'unknown', not 'nothing unlocked'.");
        }

        var totalScenes = scriptIndex?.LabelLines.Count ?? 0;
        var unlockedScenes = analysis.UnlockedSceneLabels.Count;
        var unlockedStoryScenes = scriptIndex is null
            ? 0
            : analysis.UnlockedSceneLabels.Count(label => scriptIndex.StoryLabels.Contains(label));
        var totalStoryScenes = scriptIndex?.StoryLabels.Count ?? 0;

        // Unknown denominator must stay null instead of being reported as 0 % (product rule: unknown != 0%).
        int? sceneProgressPercent = totalScenes > 0
            ? (int)Math.Round(analysis.SceneProgressPercent, MidpointRounding.AwayFromZero)
            : null;

        if (sceneProgressPercent is null)
        {
            diagnostics.Add(
                "Scene progress is unavailable (no script index); ChapterProgressPercent is left NULL rather "
                + "than being reported as 0%.");
        }
        else
        {
            diagnostics.Add(
                $"Scene progress (the honest replacement for a chapter percentage): {unlockedScenes}/{totalScenes}"
                + $" = {analysis.SceneProgressPercent:F2}% of all labels, {unlockedStoryScenes}/{totalStoryScenes}"
                + " story scenes. The game defines no chapters, so no chapter figure is invented.");
        }

        var sceneProgress = new SceneProgress
        {
            UnlockedScenes = unlockedScenes,
            TotalScenes = totalScenes,
            UnlockedStoryScenes = unlockedStoryScenes,
            TotalStoryScenes = totalStoryScenes,
            GameDefinesChapters = false,
        };

        // ---------------------------------------------------------------- suspected route clustering
        var clusters = SceneSetClusterer.Cluster(
            analysis.Saves
                .Select(save => new SlotSceneSet(save.SlotName, save.SceneSequence.ToArray()))
                .ToList(),
            SceneSetClusterer.DefaultThreshold);

        var clusterIndexOfSlot = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < clusters.Count; i++)
        {
            foreach (var key in clusters[i].MemberKeys)
            {
                clusterIndexOfSlot[key] = i;
            }
        }

        // Group names are numbered in story order (earliest save first) so the numbering is stable and
        // meaningful, and only genuine groups (two or more slots) get a name at all.
        var groupsInStoryOrder = Enumerable.Range(0, clusters.Count)
            .Where(i => clusters[i].IsGroup)
            .Select(i => new
            {
                Index = i,
                Earliest = analysis.Saves
                    .Where(s => clusters[i].MemberKeys.Contains(s.SlotName, StringComparer.OrdinalIgnoreCase))
                    .Select(s => s.LastWriteTimeUtc)
                    .DefaultIfEmpty(DateTimeOffset.MaxValue)
                    .Min(),
            })
            .OrderBy(x => x.Earliest)
            .ThenBy(x => clusters[x.Index].MemberKeys[0], StringComparer.OrdinalIgnoreCase)
            .ToList();

        var groupNameByClusterIndex = new Dictionary<int, string>();
        for (var i = 0; i < groupsInStoryOrder.Count; i++)
        {
            groupNameByClusterIndex[groupsInStoryOrder[i].Index] = $"{SpeculativeRoutePrefix} {i + 1}";
        }

        if (groupsInStoryOrder.Count == 0)
        {
            diagnostics.Add(
                $"Scene-set clustering produced no group with two or more saves, so no speculative route name "
                + $"was written (RouteName stays empty). Clusters: {clusters.Count}.");
        }
        else
        {
            diagnostics.Add(
                $"Scene-set clustering (Jaccard >= {SceneSetClusterer.DefaultThreshold:F2}) produced "
                + $"{clusters.Count} cluster(s), {groupsInStoryOrder.Count} of which hold two or more saves and "
                + "are exposed as speculative groups: "
                + string.Join(", ", groupsInStoryOrder.Select(g =>
                    $"{groupNameByClusterIndex[g.Index]}={{{string.Join("/", clusters[g.Index].MemberKeys)}}}")));
        }

        var gameLevel = new GameLevelProgress
        {
            PersistentFile = persistent?.FilePath,
            PersistentModifiedUtc = persistent?.LastWriteTimeUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            CgMethod = cg?.Method,
            SeenImageKeyCount = cg?.SeenImageKeyCount,
            LockedCgIds = cg?.LockedSlots,
            MovieUnlocks = persistent is null || persistent.MovieUnlocks.Count == 0 ? null : persistent.MovieUnlocks,
            ChosenOptions = persistent is null || persistent.Choices.Count == 0
                ? null
                : persistent.Choices
                    .Select(c => string.IsNullOrWhiteSpace(c.SceneLabel)
                        ? c.ChoiceText
                        : $"{c.ChoiceText} @ {c.SceneLabel}")
                    .Where(text => !string.IsNullOrWhiteSpace(text))
                    .ToList(),
        };

        // ---------------------------------------------------------------- per-slot mapping
        var slots = new List<MappedSaveSlot>(analysis.Saves.Count);

        foreach (var save in analysis.Saves)
        {
            var hasCluster = clusterIndexOfSlot.TryGetValue(save.SlotName, out var clusterIndex);
            var cluster = hasCluster ? clusters[clusterIndex] : null;
            var routeName = hasCluster && groupNameByClusterIndex.TryGetValue(clusterIndex, out var groupName)
                ? groupName
                : null;

            var extended = new SaveNodeExtendedMetadata
            {
                SlotKey = save.SlotName,
                PlayerSaveName = string.IsNullOrWhiteSpace(save.Metadata?.SaveName) ? null : save.Metadata!.SaveName,
                EffectiveLocation = save.LocationKind.ToString(),
                Mirrors = BuildMirrors(analysis, save, diagnostics),
                SceneLabelSource = save.CurrentSceneLabelSource,
                SceneLabelFile = save.CurrentSceneLabelFile,
                SceneSequence = BuildSceneSequence(save),
                SceneSequenceCount = save.SceneSequence.Count,
                SceneSequenceTruncated = save.SceneSequence.Count > SceneSequenceLimit,
                LastDialogue = save.LastDialogueText is null && save.LastDialogueWho is null
                    ? null
                    : new SaveDialoguePreview(save.LastDialogueWho, save.LastDialogueText),
                GameLevel = gameLevel,
                SceneProgress = sceneProgress,
                SuspectedRouteGroup = cluster is null
                    ? null
                    : new SuspectedRoute
                    {
                        Threshold = SceneSetClusterer.DefaultThreshold,
                        RouteVariablesUnavailableBecause = RouteVariableUnavailableReason,
                        ClusterId = $"R{clusterIndex + 1}",
                        ClusterSize = cluster.MemberKeys.Count,
                        ClusterCount = clusters.Count,
                        MinimumInternalSimilarity = Math.Round(cluster.MinimumInternalSimilarity, 4),
                        MaximumExternalSimilarity = Math.Round(cluster.MaximumExternalSimilarity, 4),
                        RouteNameCandidate = routeName,
                    },
                Version = save.Metadata is null
                    ? null
                    : new SaveVersionInfo(
                        save.Metadata.GameVersion,
                        installedVersion,
                        save.Metadata.RenpyVersion,
                        save.IsVersionMismatch),
                ParserNotes = save.Notes.Count == 0 ? null : save.Notes.ToList(),
            };

            var (status, parseError) = ClassifyParse(save);

            var metadata = new SaveNodeMetadata
            {
                // The player-visible name when the game sets one (json._save_name); the reference game never
                // does, so the file-derived slot name is used and the UI never shows an empty slot name.
                SlotName = FirstNonEmpty(save.Metadata?.SaveName, save.SlotName),

                // The story position, the field the whole feature exists for.
                SceneLabel = save.CurrentSceneLabel,

                // Speculative only (see the honesty rules); never read from a route variable.
                RouteName = routeName,

                // The game has no chapters. A chapter name would be invented, so it stays empty and the
                // entity falls back to SceneLabel.
                ChapterName = null,

                // Game-level, not save-level: see the remarks on GameLevelProgress.
                ChapterProgressPercent = sceneProgressPercent,
                CgUnlockedCount = cg is null ? null : cgUnlocked,
                CgTotalCount = cg is null ? null : cgTotal,

                // Null on purpose: Ren'Py does not report a CG percentage, the entity computes
                // EffectiveCgUnlockPercent from the counts instead of the scan inventing a number.
                CgUnlockPercent = null,
                CgUnlockedIdsJson = cgIdsJson,

                PlayTimeSeconds = save.PlaytimeSeconds is { } seconds
                    ? (long?)Math.Round(seconds, MidpointRounding.AwayFromZero)
                    : null,

                SaveFilePath = save.FilePath,
                SaveSizeBytes = save.FileSize,

                // One logical save slot is one .save file; the mirrored copy is a second file on disk but not
                // part of this node (it is listed under `mirrors` in the evidence payload, with its own size).
                SaveFileCount = 1,

                // Ren'Py stores no save creation time (README rule 2): the save time IS the file mtime, so
                // SaveCreatedTime stays null rather than duplicating the mtime under a lying name.
                SaveCreatedTime = null,
                SaveModifiedTime = save.LastWriteTimeUtc.UtcDateTime,

                ParseStatus = status,
                ParseError = parseError,
                ExtendedMetadataJson = extended.ToJson(),
            };

            slots.Add(new MappedSaveSlot
            {
                SlotKey = save.SlotName,
                AllPaths = BuildPathList(save),
                Metadata = metadata,
                Extended = extended,
            });
        }

        return new SaveNodeMapping
        {
            Slots = slots,
            Diagnostics = diagnostics,
            CgUnlockedCount = cgUnlocked,
            CgTotalCount = cgTotal,
            CgUnlockedIds = unlockedCgIds,
            UnlockedSceneCount = unlockedScenes,
            TotalSceneCount = totalScenes,
            UnlockedStorySceneCount = unlockedStoryScenes,
            TotalStorySceneCount = totalStoryScenes,
            SaveDirectoryName = analysis.Directories.SaveDirectoryName,
            SceneClusterCount = clusters.Count,
            SuspectedRouteGroupCount = groupsInStoryOrder.Count,
        };
    }

    /// <summary>
    /// Decides the stored <see cref="SaveParseStatus"/> and the explanation that goes with it.
    /// A failure is never silent: every non-<see cref="SaveParseStatus.Parsed"/> outcome carries text.
    /// </summary>
    private static (SaveParseStatus Status, string? Error) ClassifyParse(RenpySaveSlot save)
    {
        var hasScene = !string.IsNullOrWhiteSpace(save.CurrentSceneLabel);
        var hasPlaytime = save.PlaytimeSeconds.HasValue;
        var hasContainer = save.Metadata is not null;

        if (!hasContainer && !hasScene)
        {
            var reason = save.Notes.Count > 0
                ? string.Join("；", save.Notes)
                : "存档容器无法读取（可能不是有效的 Ren'Py .save 文件）";

            return (
                SaveParseStatus.Failed,
                Truncate($"解析失败：{reason}。场景位置、游玩时长和文件信息均不可用。", ParseErrorLimit));
        }

        var missing = new List<string>();

        if (!hasScene)
        {
            missing.Add("无法确定剧情位置（Context.current 与对白历史都没有解析出场景标签）");
        }

        if (!hasPlaytime)
        {
            missing.Add("存档内没有记录游玩时长");
        }

        if (!hasContainer)
        {
            missing.Add("存档 ZIP 元数据不可读（json/renpy_version 缺失）");
        }

        if (save.Notes.Count > 0)
        {
            missing.Add("解析器提示：" + string.Join("；", save.Notes));
        }

        return missing.Count == 0
            ? (SaveParseStatus.Parsed, null)
            : (SaveParseStatus.Partial, Truncate("部分解析：" + string.Join("；", missing) + "。", ParseErrorLimit));
    }

    /// <summary>Effective path first, then the mirrored copies, de-duplicated case-insensitively.</summary>
    private static IReadOnlyList<string> BuildPathList(RenpySaveSlot save)
    {
        var paths = new List<string> { save.FilePath };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { save.FilePath };

        foreach (var mirror in save.MirroredPaths)
        {
            if (seen.Add(mirror))
            {
                paths.Add(mirror);
            }
        }

        return paths;
    }

    /// <summary>
    /// Records every on-disk copy of the slot. Product rule §3.2.2: the mirror pair produces ONE node, the
    /// effective copy is the one stored in <see cref="SaveNode.SaveFilePath"/>, and the other copy is kept
    /// here as evidence instead of becoming a second node.
    /// </summary>
    private static IReadOnlyList<SaveMirrorCopy>? BuildMirrors(
        RenpySaveAnalysis analysis, RenpySaveSlot save, List<string> diagnostics)
    {
        var copies = new List<SaveMirrorCopy>();

        foreach (var path in BuildPathList(save))
        {
            try
            {
                var info = new FileInfo(path);

                if (!info.Exists)
                {
                    diagnostics.Add($"Mirror '{path}' disappeared before it could be described.");
                    continue;
                }

                copies.Add(new SaveMirrorCopy(
                    info.FullName,
                    DescribeLocation(analysis, info.DirectoryName),
                    info.LastWriteTimeUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    info.Length,
                    string.Equals(info.FullName, save.FilePath, StringComparison.OrdinalIgnoreCase)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add($"Mirror '{path}' could not be described: {ex.Message}");
            }
        }

        return copies.Count == 0 ? null : copies;
    }

    /// <summary>Names the well-known save location a mirror lives in, for the evidence payload.</summary>
    private static string DescribeLocation(RenpySaveAnalysis analysis, string? directory)
    {
        if (directory is not null)
        {
            foreach (var location in analysis.Directories.Locations)
            {
                if (string.Equals(
                        Path.GetFullPath(location.Path).TrimEnd(Path.DirectorySeparatorChar),
                        Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return location.Kind.ToString();
                }
            }
        }

        return "Unknown";
    }

    /// <summary>Copies the visited story labels, capped so one node cannot bloat a row.</summary>
    private static IReadOnlyList<string>? BuildSceneSequence(RenpySaveSlot save)
    {
        if (save.SceneSequence.Count == 0)
        {
            return null;
        }

        return save.SceneSequence.Count <= SceneSequenceLimit
            ? save.SceneSequence.ToList()
            : save.SceneSequence.Take(SceneSequenceLimit).ToList();
    }

    /// <summary>
    /// Reads <c>config.version</c> of the installation for the evidence payload. Best effort: a failure only
    /// costs a diagnostic line, it must never fail the scan.
    /// </summary>
    private static string? TryDetectInstalledVersion(RenpySaveAnalysis analysis, List<string> diagnostics)
    {
        try
        {
            return RenpySaveAnalyzer.DetectGameVersion(
                analysis.Directories.GameRoot,
                analysis.Directories.GameFolder);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            diagnostics.Add($"The installed game version could not be read: {ex.Message}");
            return null;
        }
    }

    private static string? FirstNonEmpty(params string?[] candidates)
        => candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));

    private static string Truncate(string text, int limit)
        => text.Length <= limit ? text : text[..(limit - 8)] + "…（已截断）";
}
