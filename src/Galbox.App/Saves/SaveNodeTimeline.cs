// The presentation layer of the save-node timeline: pure data shaping, no WinUI, no database.
//
// Why it is separate from the ViewModel: this is where the three honesty red lines of
// `_product/design/save-node-integration-plan.md` §5 are turned into text a user reads, and that
// has to be verifiable without starting a GUI. `Galbox.Acceptance`'s A10 drives these types
// directly, so "the UI shows a made-up route name" or "a failed parse is invisible" is a test
// failure and not a code review opinion.
//
// The rules encoded here:
//   * red line 1 - a branch name is speculation. Every value carrying the mapper's
//     `疑似路线` prefix is rendered with the explicit marker `（推测，非确证）`; the raw value is
//     never shown as if it were a fact, and an empty RouteName stays empty.
//   * red line 2 - "chapter progress" is unlocked-scenes / total-scenes, never a script line
//     percentage. The exact method is printed next to the number.
//   * red line 3 - a parse failure is visible: ParseStatus Failed/Partial always produces text,
//     and a failure without a stored reason says so instead of silently showing nothing.
//   * §6.1 - the timeline label is NEVER blank. The reference game stores 12/12 slot names as the
//     empty string, so a display name that fell back to the slot name would render nothing.
using Galbox.Data.Entities;
using Galbox.Services.Saves;

namespace Galbox.App.Saves;

/// <summary>Which kind of engine slot a node was read from.</summary>
public enum SaveSlotKind
{
    /// <summary>A numbered player slot, e.g. <c>1-1-LT1</c>.</summary>
    Manual,

    /// <summary>An autosave slot, e.g. <c>auto-3-LT1</c>.</summary>
    Auto,

    /// <summary>A quicksave slot, e.g. <c>quick-1-LT1</c>.</summary>
    Quick,

    /// <summary>The slot name does not follow any known convention.</summary>
    Unknown,
}

/// <summary>
/// One save node, shaped for the timeline view.
/// </summary>
/// <remarks>
/// A plain immutable class rather than a record with mutable state: the timeline is rebuilt from the
/// database after every scan or snapshot change, which keeps the view and the stored rows in step.
/// </remarks>
public sealed class SaveNodeTimelineEntry
{
    /// <summary>Builds the display view of one stored node.</summary>
    /// <param name="node">Stored node.</param>
    /// <param name="extended">Parsed evidence payload of that node, when it has one.</param>
    public SaveNodeTimelineEntry(SaveNode node, SaveNodeExtendedMetadata? extended)
    {
        ArgumentNullException.ThrowIfNull(node);

        Node = node;
        Extended = extended;

        NodeId = node.Id;

        // §6.1: the label must never be blank. SaveNode.DisplayName already falls back
        // (snapshot description > scene label > chapter name > slot name), and the last resort here
        // covers a row whose slot name is the empty string - which is the normal case for the
        // reference game (12/12 saves store "").
        DisplayName = ResolveDisplayName(node);

        SceneLabelText = string.IsNullOrWhiteSpace(node.SceneLabel)
            ? "(未解析出场景名)"
            : node.SceneLabel!.Trim();

        HasSceneLabel = !string.IsNullOrWhiteSpace(node.SceneLabel);

        var localTime = ToLocal(node.SaveModifiedTime);

        TimeText = localTime?.ToString("M/d HH:mm") ?? "时间未知";
        FullTimeText = localTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "存档没有记录时间";

        SlotKey = extended?.SlotKey ?? node.SlotName ?? string.Empty;
        SlotKind = ClassifySlot(SlotKey, node.SlotName);
        SlotKindText = SlotKind switch
        {
            SaveSlotKind.Auto => "自动档",
            SaveSlotKind.Quick => "快速存档",
            SaveSlotKind.Manual => "手动档",
            _ => "档位",
        };
        SlotKeyText = string.IsNullOrWhiteSpace(SlotKey) ? "(无档位名)" : SlotKey;

        // Red line 1: whatever the mapper wrote in RouteName is a guess unless the user typed it.
        // The prefix is what tells the two apart, and it is never stripped before deciding.
        var routeName = node.RouteName;

        if (!string.IsNullOrWhiteSpace(routeName))
        {
            RouteIsSpeculative = routeName!.StartsWith(
                RenpySaveNodeMapper.SpeculativeRoutePrefix, StringComparison.Ordinal);

            RouteText = RouteIsSpeculative
                ? $"{routeName}（推测，非确证）"
                : routeName;
        }

        IsSnapshot = node.IsSnapshot;
        SnapshotDescription = string.IsNullOrWhiteSpace(node.SnapshotDescription)
            ? null
            : node.SnapshotDescription!.Trim();

        ParseStatusText = node.ParseStatus switch
        {
            SaveParseStatus.Parsed => "解析正常",
            SaveParseStatus.Partial => "部分解析",
            SaveParseStatus.Failed => "解析失败",
            _ => "未解析",
        };

        // Red line 3: a non-successful parse is always visible, and an absent reason is itself
        // reported rather than rendering as "no problem".
        ParseProblemText = node.ParseStatus switch
        {
            SaveParseStatus.Parsed => null,
            SaveParseStatus.NotAttempted => null,
            _ => string.IsNullOrWhiteSpace(node.ParseError)
                ? $"{ParseStatusText}，但解析器没有留下原因（数据层要求 ParseError 必须写明）"
                : $"{ParseStatusText}：{node.ParseError}",
        };

        HasParseProblem = ParseProblemText is not null;

        PlayTimeText = node.PlayTimeSeconds > 0 ? node.FormattedPlayTime : null;

        SizeText = node.SaveSizeBytes > 0
            ? $"{node.SaveSizeBytes / 1024.0:F0} KB"
            : null;

        var version = extended?.Version;

        VersionMismatchText = version is { IsMismatch: true }
            ? $"存档版本 {version.SaveGameVersion ?? "?"} 与已安装版本 {version.InstalledGameVersion ?? "?"} 不一致"
            : null;

        DialogueText = ComposeDialogue(extended?.LastDialogue);

        TooltipText = ComposeTooltip();
    }

    /// <summary>The stored row this entry renders.</summary>
    public SaveNode Node { get; }

    /// <summary>Evidence payload of the row, when it has one.</summary>
    public SaveNodeExtendedMetadata? Extended { get; }

    /// <summary>Database identifier of the node.</summary>
    public int NodeId { get; }

    /// <summary>Timeline label; guaranteed non-blank.</summary>
    public string DisplayName { get; }

    /// <summary>Scene label as stored, or an explicit "(未解析出场景名)".</summary>
    public string SceneLabelText { get; }

    /// <summary>True when a real scene label is stored.</summary>
    public bool HasSceneLabel { get; }

    /// <summary>Short local time of the save, e.g. "4/9 00:18".</summary>
    public string TimeText { get; }

    /// <summary>Full local time of the save.</summary>
    public string FullTimeText { get; }

    /// <summary>Engine slot this node came from (autosave, quicksave, manual slot).</summary>
    public SaveSlotKind SlotKind { get; }

    /// <summary>User-facing name of <see cref="SlotKind"/>.</summary>
    public string SlotKindText { get; }

    /// <summary>True for an autosave slot; drives the distinct timeline styling.</summary>
    public bool IsAutoSave => SlotKind == SaveSlotKind.Auto;

    /// <summary>File-derived slot key, e.g. <c>auto-3-LT1</c>.</summary>
    public string SlotKey { get; }

    /// <summary>Slot key as shown, never blank.</summary>
    public string SlotKeyText { get; }

    /// <summary>Branch/route text as it must be shown, with the speculation marker when it is a guess.</summary>
    public string? RouteText { get; }

    /// <summary>True when <see cref="RouteText"/> carries the mapper's 疑似 prefix.</summary>
    public bool RouteIsSpeculative { get; }

    /// <summary>True when the node has a route text to show at all.</summary>
    public bool HasRoute => !string.IsNullOrWhiteSpace(RouteText);

    /// <summary>True when the user marked this node as a key snapshot.</summary>
    public bool IsSnapshot { get; }

    /// <summary>User-typed snapshot description.</summary>
    public string? SnapshotDescription { get; }

    /// <summary>Stored parse outcome, as text.</summary>
    public string ParseStatusText { get; }

    /// <summary>Explanation of a partial/failed parse; null when the parse was fine.</summary>
    public string? ParseProblemText { get; }

    /// <summary>True when the parse needs to be shown to the user (red line 3).</summary>
    public bool HasParseProblem { get; }

    /// <summary>Play time recorded inside the save, when the engine stored one.</summary>
    public string? PlayTimeText { get; }

    /// <summary>Save file size, when known.</summary>
    public string? SizeText { get; }

    /// <summary>Warning when the save was written by a different game version.</summary>
    public string? VersionMismatchText { get; }

    /// <summary>Last dialogue line of the save, for the hover tooltip.</summary>
    public string? DialogueText { get; }

    /// <summary>True when the save recorded a last dialogue line.</summary>
    public bool HasDialogue => !string.IsNullOrWhiteSpace(DialogueText);

    /// <summary>Everything the hover tooltip shows.</summary>
    public string TooltipText { get; }

    /// <summary>
    /// Resolves the timeline label, falling back through the stored values and never returning an
    /// empty string.
    /// </summary>
    private static string ResolveDisplayName(SaveNode node)
    {
        // SaveNode.DisplayName is the documented precedence chain
        // (snapshot description > scene label > chapter name > slot name).
        var name = node.DisplayName;

        if (!string.IsNullOrWhiteSpace(name))
        {
            return name.Trim();
        }

        // Unreachable for scanned rows, but a hand-created or imported row with an empty slot name
        // must still render as something a human can act on.
        if (!string.IsNullOrWhiteSpace(node.SaveFilePath))
        {
            return Path.GetFileNameWithoutExtension(node.SaveFilePath);
        }

        return $"存档 #{node.Id}";
    }

    /// <summary>
    /// Classifies the slot from its file-derived key: the reference game writes <c>auto-3-LT1</c>
    /// for autosaves and <c>1-1-LT1</c> for manual slots.
    /// </summary>
    private static SaveSlotKind ClassifySlot(string? slotKey, string? storedSlotName)
    {
        var key = string.IsNullOrWhiteSpace(slotKey) ? storedSlotName : slotKey;

        if (string.IsNullOrWhiteSpace(key))
        {
            return SaveSlotKind.Unknown;
        }

        var trimmed = key.Trim();

        if (trimmed.StartsWith("auto", StringComparison.OrdinalIgnoreCase))
        {
            return SaveSlotKind.Auto;
        }

        if (trimmed.StartsWith("quick", StringComparison.OrdinalIgnoreCase))
        {
            return SaveSlotKind.Quick;
        }

        return char.IsDigit(trimmed[0]) ? SaveSlotKind.Manual : SaveSlotKind.Unknown;
    }

    /// <summary>Renders the stored last dialogue as "speaker: line" (or just the line).</summary>
    private static string? ComposeDialogue(SaveDialoguePreview? dialogue)
    {
        if (dialogue is null)
        {
            return null;
        }

        var text = string.IsNullOrWhiteSpace(dialogue.Text) ? null : dialogue.Text!.Trim();
        var who = string.IsNullOrWhiteSpace(dialogue.Who) ? null : dialogue.Who!.Trim();

        if (text is null && who is null)
        {
            return null;
        }

        return who is null ? text : (text is null ? who : $"{who}：{text}");
    }

    /// <summary>Builds the multi-line hover text of one timeline node.</summary>
    private string ComposeTooltip()
    {
        var lines = new List<string> { DisplayName };

        if (HasDialogue)
        {
            lines.Add(string.Empty);
            lines.Add(DialogueText!);
        }

        lines.Add(string.Empty);
        lines.Add($"档位：{SlotKeyText}（{SlotKindText}）");
        lines.Add($"存档时间：{FullTimeText}");

        if (!HasSceneLabel)
        {
            lines.Add("场景：未解析出场景名");
        }

        if (PlayTimeText is not null)
        {
            lines.Add($"存档内游玩时长：{PlayTimeText}");
        }

        if (HasRoute)
        {
            lines.Add($"分支：{RouteText}");
        }

        if (IsSnapshot && SnapshotDescription is not null)
        {
            lines.Add($"快照：{SnapshotDescription}");
        }

        if (VersionMismatchText is not null)
        {
            lines.Add(VersionMismatchText);
        }

        if (HasParseProblem)
        {
            lines.Add(ParseProblemText!);
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Converts a stored UTC timestamp to local time for display.</summary>
    private static DateTime? ToLocal(DateTime? utc)
    {
        if (utc is null)
        {
            return null;
        }

        var value = utc.Value;

        return value.Kind switch
        {
            DateTimeKind.Utc => value.ToLocalTime(),
            DateTimeKind.Local => value,
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc).ToLocalTime(),
        };
    }
}

/// <summary>CG gallery progress of a game, as of the last scan.</summary>
public sealed class SaveCgProgressView
{
    /// <summary>Unlocked CG count (0 when unknown).</summary>
    public int UnlockedCount { get; init; }

    /// <summary>Total CG count declared by the gallery (0 when unknown).</summary>
    public int TotalCount { get; init; }

    /// <summary>True when the gallery is known, i.e. a total was parsed.</summary>
    public bool IsKnown => TotalCount > 0;

    /// <summary>Ids the parser saw unlocked, sorted.</summary>
    public IReadOnlyList<string> UnlockedIds { get; init; } = Array.Empty<string>();

    /// <summary>Ids still locked, as recorded by the scan (empty when the parser did not list them).</summary>
    public IReadOnlyList<string> LockedIds { get; init; } = Array.Empty<string>();

    /// <summary>True when the scan stored the locked-id list, so "还差哪几张" can be answered.</summary>
    public bool HasLockedIds => LockedIds.Count > 0;

    /// <summary>Unlock rate at one decimal, e.g. 22.2.</summary>
    public double Percent => IsKnown ? UnlockedCount * 100.0 / TotalCount : 0;

    /// <summary>The headline the page shows, e.g. "6 / 27 (22.2%)".</summary>
    public string HeadlineText => IsKnown
        ? $"{UnlockedCount} / {TotalCount} ({Percent:F1}%)"
        : "未知（未解析到 CG 图鉴）";

    /// <summary>Value for a 0-100 progress bar.</summary>
    public double ProgressValue => IsKnown ? Math.Clamp(Percent, 0, 100) : 0;

    /// <summary>Remaining count, or null when unknown.</summary>
    public int? RemainingCount => IsKnown ? Math.Max(0, TotalCount - UnlockedCount) : null;

    /// <summary>
    /// Why these numbers are game-level rather than a property of one save. Shown next to the figure
    /// so the user is never told that an old save had today's CG progress.
    /// </summary>
    public static string ScopeNote =>
        "游戏级进度（共享的 persistent 文件）：是「最近一次扫描时」的进度，不是某个存档当时的进度";
}

/// <summary>Story progress of a game, as of the last scan.</summary>
public sealed class SaveSceneProgressView
{
    /// <summary>Scenes the player has read at least once.</summary>
    public int UnlockedScenes { get; init; }

    /// <summary>Scenes the scripts declare (the denominator).</summary>
    public int TotalScenes { get; init; }

    /// <summary>True when a denominator was parsed.</summary>
    public bool IsKnown => TotalScenes > 0;

    /// <summary>Unlock rate at one decimal.</summary>
    public double Percent => IsKnown ? UnlockedScenes * 100.0 / TotalScenes : 0;

    /// <summary>The headline, e.g. "122 / 345 (35.4%)".</summary>
    public string HeadlineText => IsKnown
        ? $"{UnlockedScenes} / {TotalScenes} ({Percent:F1}%)"
        : "未知（未解析到剧本标签表）";

    /// <summary>Value for a 0-100 progress bar.</summary>
    public double ProgressValue => IsKnown ? Math.Clamp(Percent, 0, 100) : 0;

    /// <summary>What the number actually means (red line 2), shown under it.</summary>
    public static string MethodNote =>
        "已解锁场景数 / 剧本总场景数（不是剧本行号百分比；该游戏没有章节概念，所以不显示章节数）";
}

/// <summary>Everything the save-node section of the page renders.</summary>
public sealed record SaveTimelineModel
{
    /// <summary>The empty state shown before the first scan of this page session.</summary>
    public static SaveTimelineModel NeverScanned { get; } = new()
    {
        State = SaveTimelineState.NeverScanned,
        StatusText = "尚未扫描存档（点击「扫描存档」）",
        HintText = "扫描后这里会显示每个存档的剧情位置、时间和 CG 图鉴进度。",
    };

    /// <summary>Timeline entries, oldest save first.</summary>
    public IReadOnlyList<SaveNodeTimelineEntry> Entries { get; init; } = Array.Empty<SaveNodeTimelineEntry>();

    /// <summary>Which of the four honest states this model represents.</summary>
    public SaveTimelineState State { get; init; } = SaveTimelineState.NeverScanned;

    /// <summary>One-line status shown under the section header.</summary>
    public string StatusText { get; init; } = string.Empty;

    /// <summary>Second line of the status, when there is something more to say.</summary>
    public string? HintText { get; init; }

    /// <summary>Concrete reason of a failure, or the unsupported-engine notice. Never a generic "失败".</summary>
    public string? ErrorText { get; init; }

    /// <summary>True when there is a timeline to draw.</summary>
    public bool HasTimeline => Entries.Count > 0;

    /// <summary>True when the section must draw the empty state.</summary>
    public bool IsEmpty => Entries.Count == 0;

    /// <summary>CG gallery progress.</summary>
    public SaveCgProgressView Cg { get; init; } = new();

    /// <summary>Story progress.</summary>
    public SaveSceneProgressView Scene { get; init; } = new();

    /// <summary>Speculative route groups, already worded for display.</summary>
    public IReadOnlyList<string> RouteGroups { get; init; } = Array.Empty<string>();

    /// <summary>True when at least one speculative group was found.</summary>
    public bool HasRouteGroups => RouteGroups.Count > 0;

    /// <summary>The red-line note shown next to the route groups.</summary>
    public static string RouteNote =>
        "读不到任何路线变量，所以分支只能按「已访问场景集合」聚类推测，一律标为疑似，不是确定路线";

    /// <summary>How many nodes carry a parse problem (red line 3: they are shown, one by one).</summary>
    public int ParseProblemCount => Entries.Count(entry => entry.HasParseProblem);

    /// <summary>How many nodes are snapshots.</summary>
    public int SnapshotCount => Entries.Count(entry => entry.IsSnapshot);

    /// <summary>How many nodes are autosaves.</summary>
    public int AutoSaveCount => Entries.Count(entry => entry.IsAutoSave);
}

/// <summary>The states the save-node section can be in; each one is rendered differently and honestly.</summary>
public enum SaveTimelineState
{
    /// <summary>Nothing has been scanned in this session yet.</summary>
    NeverScanned,

    /// <summary>Nodes were read from the database.</summary>
    Ready,

    /// <summary>The library game itself could not be found or has no install path.</summary>
    NoGame,

    /// <summary>The engine has no save parser, so no node is shown and no number is invented.</summary>
    UnsupportedEngine,

    /// <summary>The scan failed; <see cref="SaveTimelineModel.ErrorText"/> says why.</summary>
    Failed,

    /// <summary>The scan ran but the installation holds no save file.</summary>
    NoSaves,
}
