// Public data model for Ren'Py save analysis.
//
// Everything here is an immutable record so callers (ViewModels, background services) can read
// it without locking. All types live under Galbox.Core.Saves and are produced by
// RenpySaveAnalyzer; no UI or persistence dependency is involved.
using Galbox.Core.Saves.Pickle;

namespace Galbox.Core.Saves;

/// <summary>Where a save file was found.</summary>
public enum RenpySaveLocationKind
{
    /// <summary><c>&lt;game&gt;\game\saves</c> — the game-local save directory.</summary>
    GameLocal,

    /// <summary><c>%APPDATA%\RenPy\&lt;config.save_directory&gt;</c> — the user save directory.</summary>
    UserAppData,

    /// <summary>A caller-supplied directory.</summary>
    Custom,
}

/// <summary>What kind of position marker a save's <c>Context.current</c> holds.</summary>
public enum RenpyStatementKind
{
    /// <summary>Could not be read.</summary>
    Unknown,

    /// <summary>A label name — directly usable as a scene name.</summary>
    Label,

    /// <summary>A <c>from _call_xxx_N</c> call-site name; resolved through the label dictionary.</summary>
    CallSite,

    /// <summary>A <c>(file, compile_timestamp, statement_serial)</c> tuple.</summary>
    Statement,
}

/// <summary>One save directory candidate and what it contains.</summary>
public sealed record RenpySaveLocationInfo
{
    /// <summary>Which of the well-known locations this is.</summary>
    public required RenpySaveLocationKind Kind { get; init; }

    /// <summary>Absolute directory path.</summary>
    public required string Path { get; init; }

    /// <summary>Whether the directory exists on disk.</summary>
    public bool Exists { get; init; }

    /// <summary>Number of <c>*.save</c> files found.</summary>
    public int SaveFileCount { get; init; }

    /// <summary>Whether a <c>persistent</c> file is present.</summary>
    public bool HasPersistentFile { get; init; }

    /// <summary>Newest write time across the files in this directory.</summary>
    public DateTimeOffset? NewestWriteTimeUtc { get; init; }

    /// <summary>File names found here, sorted.</summary>
    public IReadOnlyList<string> FileNames { get; init; } = Array.Empty<string>();

    /// <summary>Why this location could not be probed, when it could not.</summary>
    public string? FailureReason { get; init; }
}

/// <summary>
/// Result of save-directory discovery (report Step 0). Ren'Py's <c>MultiLocation</c> writes every
/// save to both locations, so callers must pick the newest copy by mtime rather than hard-coding
/// one directory.
/// </summary>
public sealed record RenpySaveDirectorySet
{
    /// <summary>The game's <c>game</c> folder (holds <c>archive.rpa</c>, fonts, ...).</summary>
    public required string GameFolder { get; init; }

    /// <summary>The game installation root (the folder containing <c>game</c>).</summary>
    public required string GameRoot { get; init; }

    /// <summary>Value of <c>config.save_directory</c> (e.g. <c>dreamin_her-1631775296</c>).</summary>
    public string? SaveDirectoryName { get; init; }

    /// <summary>Where <see cref="SaveDirectoryName"/> was read from.</summary>
    public string? SaveDirectorySource { get; init; }

    /// <summary>True when at least one location exists and holds at least one save.</summary>
    public bool Success { get; init; }

    /// <summary>Why discovery failed, when it did.</summary>
    public string? FailureReason { get; init; }

    /// <summary>Every candidate location, in probe order.</summary>
    public IReadOnlyList<RenpySaveLocationInfo> Locations { get; init; } = Array.Empty<RenpySaveLocationInfo>();

    /// <summary>Non-fatal problems observed while locating directories.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>The location with the newest file among all candidates.</summary>
    public RenpySaveLocationInfo? NewestLocation
        => Locations
            .Where(l => l.Exists && l.SaveFileCount > 0)
            .OrderByDescending(l => l.NewestWriteTimeUtc ?? DateTimeOffset.MinValue)
            .FirstOrDefault();
}

/// <summary>Metadata read straight out of the save ZIP container (report Step 1, zero-pickle).</summary>
public sealed record RenpySaveMetadata
{
    /// <summary>Absolute path of the <c>.save</c> file.</summary>
    public required string FilePath { get; init; }

    /// <summary>Slot name derived from the file name (e.g. <c>auto-3-LT1</c>).</summary>
    public required string SlotName { get; init; }

    /// <summary>File size in bytes.</summary>
    public long FileSize { get; init; }

    /// <summary>Last write time, which is the save time — Ren'Py does not store one in the ZIP.</summary>
    public DateTimeOffset LastWriteTimeUtc { get; init; }

    /// <summary><c>json._save_name</c>; empty for games that never set <c>save_name</c>.</summary>
    public string? SaveName { get; init; }

    /// <summary><c>json._version</c> — the game version that created the save.</summary>
    public string? GameVersion { get; init; }

    /// <summary><c>json._renpy_version</c> rendered as <c>7.4.11.2266</c>.</summary>
    public string? RenpyVersion { get; init; }

    /// <summary>The raw <c>renpy_version</c> entry text, e.g. <c>Ren'Py 7.4.11.2266</c>.</summary>
    public string? RenpyVersionText { get; init; }

    /// <summary>The <c>json._renpy_version</c> tuple, e.g. <c>[7, 4, 11, 2266]</c>.</summary>
    public IReadOnlyList<int> RenpyVersionTuple { get; init; } = Array.Empty<int>();

    /// <summary>UTF-8 text of the <c>extra_info</c> entry (the user-typed save name).</summary>
    public string? ExtraInfo { get; init; }

    /// <summary>Length in bytes of the <c>screenshot.png</c> entry.</summary>
    public int ScreenshotLength { get; init; }

    /// <summary>Thumbnail bytes; only populated when the caller asked for screenshots.</summary>
    public byte[]? ScreenshotPng { get; init; }

    /// <summary>Length in bytes of the <c>log</c> (pickle) entry.</summary>
    public int LogLength { get; init; }

    /// <summary>All ZIP entry names, in archive order.</summary>
    public IReadOnlyList<string> ZipEntries { get; init; } = Array.Empty<string>();

    /// <summary>The raw <c>json</c> entry text, preserved for diagnostics.</summary>
    public string? JsonText { get; init; }
}

/// <summary>One dialogue-history entry recovered from the save's <c>log</c> pickle.</summary>
/// <param name="Who">Speaker display name.</param>
/// <param name="What">Spoken text.</param>
/// <param name="VoiceFile">Voice file played with the line, when any.</param>
/// <param name="Tlid">Raw <c>voice.tlid</c> value, e.g. <c>孤独感_28a2c752_3</c>.</param>
/// <param name="SceneLabel">Label derived from the tlid prefix, when it resolves.</param>
public sealed record RenpyDialogueEntry(
    string? Who,
    string? What,
    string? VoiceFile,
    string? Tlid,
    string? SceneLabel);

/// <summary>One entry of <c>persistent._chosen</c> — a menu option the player picked.</summary>
/// <param name="ScriptFile">Script file the menu lives in.</param>
/// <param name="CompileTimestamp">Compile timestamp part of the statement id.</param>
/// <param name="StatementSerial">Global statement serial (NOT a line number).</param>
/// <param name="ChoiceText">The option text.</param>
/// <param name="SceneLabel">Enclosing label, resolved via the call-site/label dictionary.</param>
public sealed record RenpyChoiceRecord(
    string ScriptFile,
    long CompileTimestamp,
    long StatementSerial,
    string ChoiceText,
    string? SceneLabel);

/// <summary>Everything Galbox extracts from a single <c>.save</c> file.</summary>
public sealed record RenpySaveSlot
{
    /// <summary>Slot name derived from the file name.</summary>
    public required string SlotName { get; init; }

    /// <summary>Absolute path of the file that was read (the newest of the mirrored copies).</summary>
    public required string FilePath { get; init; }

    /// <summary>Which well-known location the file came from.</summary>
    public RenpySaveLocationKind LocationKind { get; init; }

    /// <summary>Every location that holds a copy of this slot.</summary>
    public IReadOnlyList<string> MirroredPaths { get; init; } = Array.Empty<string>();

    /// <summary>Save time (file mtime).</summary>
    public DateTimeOffset LastWriteTimeUtc { get; init; }

    /// <summary>File size in bytes.</summary>
    public long FileSize { get; init; }

    /// <summary>ZIP-level metadata, always available (zero pickle parsing).</summary>
    public RenpySaveMetadata? Metadata { get; init; }

    /// <summary>Accumulated play time in seconds, from <c>context.runtime</c>.</summary>
    public double? PlaytimeSeconds { get; init; }

    /// <summary><see cref="PlaytimeSeconds"/> as a duration.</summary>
    public TimeSpan? Playtime => PlaytimeSeconds is { } s ? TimeSpan.FromSeconds(s) : null;

    /// <summary>Raw <c>Context.current</c> rendered for display, e.g. <c>('game/x.rpy', 1, 2)</c>.</summary>
    public string? CurrentStatement { get; init; }

    /// <summary>Shape of <see cref="CurrentStatement"/>.</summary>
    public RenpyStatementKind CurrentStatementKind { get; init; } = RenpyStatementKind.Unknown;

    /// <summary>The scene the save sits in, resolved to a script label name.</summary>
    public string? CurrentSceneLabel { get; init; }

    /// <summary>How <see cref="CurrentSceneLabel"/> was derived (for UI transparency).</summary>
    public string? CurrentSceneLabelSource { get; init; }

    /// <summary>
    /// Distinct STORY scene labels touched by this save, in first-occurrence (story) order — the
    /// "recently visited scenes" sequence shown in the product's timeline view. Helper labels from
    /// macro scripts are listed in <see cref="AllResolvedSceneLabels"/> instead.
    /// </summary>
    public IReadOnlyList<string> SceneSequence { get; init; } = Array.Empty<string>();

    /// <summary>Every resolved label, story and helper alike, in first-occurrence order.</summary>
    public IReadOnlyList<string> AllResolvedSceneLabels { get; init; } = Array.Empty<string>();

    /// <summary>Logical script file the resolved <see cref="CurrentSceneLabel"/> is defined in.</summary>
    public string? CurrentSceneLabelFile { get; init; }

    /// <summary>
    /// Newest story label that appears in this save's dialogue history. This is the "where was I"
    /// answer for saves whose <c>Context.current</c> only points at a helper label.
    /// </summary>
    public string? LastDialogueSceneLabel { get; init; }

    /// <summary>Dialogue history entries, oldest first.</summary>
    public IReadOnlyList<RenpyDialogueEntry> Dialogue { get; init; } = Array.Empty<RenpyDialogueEntry>();

    /// <summary>The last few dialogue entries, newest last.</summary>
    public IReadOnlyList<RenpyDialogueEntry> RecentDialogue { get; init; } = Array.Empty<RenpyDialogueEntry>();

    /// <summary>Speaker of the last history entry.</summary>
    public string? LastDialogueWho { get; init; }

    /// <summary>Text of the last history entry — a readable "where am I" description.</summary>
    public string? LastDialogueText { get; init; }

    /// <summary>Number of raw <c>_history_list</c> entries.</summary>
    public int HistoryEntryCount { get; init; }

    /// <summary>Audit of the <c>log</c> pickle scan (opcodes, referenced globals, threat report).</summary>
    public PickleScanAudit? LogAudit { get; init; }

    /// <summary>True when the save's <c>_version</c> differs from the game's current version.</summary>
    public bool IsVersionMismatch { get; init; }

    /// <summary>Non-fatal problems noticed while parsing this save.</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

/// <summary>Contents of the shared <c>persistent</c> file (report Step 5).</summary>
public sealed record RenpyPersistentInfo
{
    /// <summary>Absolute path of the file that was read.</summary>
    public required string FilePath { get; init; }

    /// <summary>File mtime.</summary>
    public DateTimeOffset LastWriteTimeUtc { get; init; }

    /// <summary>Size of the raw (zlib-compressed) file.</summary>
    public int RawLength { get; init; }

    /// <summary>Size after decompression.</summary>
    public int DecompressedLength { get; init; }

    /// <summary>
    /// Keys of <c>_seen_images</c>. All keys in the reference game are tuples — single element
    /// tuples such as <c>('0101',)</c> are plain image names, longer ones are character sprite
    /// composites. Rendered as <c>0101</c> or <c>(a, b, c)</c>.
    /// </summary>
    public IReadOnlyList<string> SeenImageKeys { get; init; } = Array.Empty<string>();

    /// <summary>Raw key count of <c>_seen_images</c> (283 in the reference game).</summary>
    public int SeenImageKeyCount { get; init; }

    /// <summary>Keys of <c>_seen_images</c> that are single-element tuples, i.e. image names.</summary>
    public IReadOnlyList<string> SeenImageNames { get; init; } = Array.Empty<string>();

    /// <summary>String keys of <c>_seen_ever</c> — label / <c>_call_*</c> names that were read.</summary>
    public IReadOnlyList<string> SeenEverStringKeys { get; init; } = Array.Empty<string>();

    /// <summary>Raw key count of <c>_seen_ever</c> (2887 in the reference game).</summary>
    public int SeenEverKeyCount { get; init; }

    /// <summary>Menu choices recorded in <c>_chosen</c>.</summary>
    public IReadOnlyList<RenpyChoiceRecord> Choices { get; init; } = Array.Empty<RenpyChoiceRecord>();

    /// <summary>Movie-gallery unlock flags (<c>opmv</c>, <c>kakoed</c>, <c>granded</c>).</summary>
    public IReadOnlyDictionary<string, bool> MovieUnlocks { get; init; }
        = new Dictionary<string, bool>(StringComparer.Ordinal);

    /// <summary>Audit of the persistent pickle scan.</summary>
    public PickleScanAudit? Audit { get; init; }
}

/// <summary>CG gallery completion (report Step 5).</summary>
public sealed record RenpyCgProgress
{
    /// <summary>Number of CG slots declared by <c>gallery.image(...)</c> — the denominator.</summary>
    public int TotalCount { get; init; }

    /// <summary>Number of declared slots present in <c>persistent._seen_images</c>.</summary>
    public int UnlockedCount { get; init; }

    /// <summary>Completion percentage (0-100).</summary>
    public double Percent { get; init; }

    /// <summary>Slot ids that are unlocked, sorted.</summary>
    public IReadOnlyList<string> UnlockedSlots { get; init; } = Array.Empty<string>();

    /// <summary>Slot ids that are still locked, in declaration order.</summary>
    public IReadOnlyList<string> LockedSlots { get; init; } = Array.Empty<string>();

    /// <summary>All slot ids declared by the gallery script, in declaration order.</summary>
    public IReadOnlyList<string> DeclaredSlots { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Raw <c>_seen_images</c> key count. Surfaced so it is obvious that the unlocked count is an
    /// intersection with the declared slots and not <c>len(_seen_images)</c>.
    /// </summary>
    public int SeenImageKeyCount { get; init; }

    /// <summary>Explanation of how the number was computed (shown in UI as a tooltip).</summary>
    public string Method { get; init; }
        = "intersection of gallery.image(\"NN01\") slot ids with persistent._seen_images";
}

/// <summary>A label found in a game script.</summary>
/// <param name="Name">Label name.</param>
/// <param name="SourceFile">Logical script name, e.g. <c>00patch/scenario/main_scenario.rpy</c>.</param>
/// <param name="Line">1-based definition line.</param>
/// <param name="ArchivePath">Archive the copy came from.</param>
/// <param name="IsEffectiveCopy">True when this copy is the one Ren'Py actually loads.</param>
public sealed record RenpyLabelDefinition(
    string Name,
    string SourceFile,
    int Line,
    string ArchivePath,
    bool IsEffectiveCopy);

/// <summary>Static script knowledge extracted from the game's RPA archives (report Step 3).</summary>
public sealed record RenpyScriptIndex
{
    /// <summary>Every label, effective copies first.</summary>
    public IReadOnlyList<RenpyLabelDefinition> Labels { get; init; } = Array.Empty<RenpyLabelDefinition>();

    /// <summary>Label name to definition line, for effective copies.</summary>
    public IReadOnlyDictionary<string, int> LabelLines { get; init; }
        = new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>Label name to logical source file.</summary>
    public IReadOnlyDictionary<string, string> LabelFiles { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Labels that live in the game's story scripts. Ren'Py games route reusable presentation
    /// work (character sprites, backgrounds, sound, screens) through helper labels that sit in
    /// <c>macro</c> scripts; those names are real labels but they are not story scenes, so they
    /// are excluded from scene sequencing and from the "current scene" display.
    /// </summary>
    public IReadOnlySet<string> StoryLabels { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// <c>from _call_xxx_N</c> call-site name to the enclosing label — this is what turns a
    /// save's <c>Context.current</c> string into a scene name.
    /// </summary>
    public IReadOnlyDictionary<string, string> CallSiteToLabel { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Slot ids declared by <c>gallery.image("...")</c>, in declaration order.</summary>
    public IReadOnlyList<string> GallerySlots { get; init; } = Array.Empty<string>();

    /// <summary>Logical script name to the archive it was read from.</summary>
    public IReadOnlyDictionary<string, string> SourceArchives { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Label count per logical script file.</summary>
    public IReadOnlyDictionary<string, int> LabelCountByFile { get; init; }
        = new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>True for every logical script that exists in more than one archive.</summary>
    public IReadOnlyList<string> DuplicatedScripts { get; init; } = Array.Empty<string>();

    /// <summary>Non-fatal problems observed while indexing scripts.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>Full analysis of one Ren'Py game installation.</summary>
public sealed record RenpySaveAnalysis
{
    /// <summary>Save directory discovery result.</summary>
    public required RenpySaveDirectorySet Directories { get; init; }

    /// <summary>One entry per save slot, newest first.</summary>
    public IReadOnlyList<RenpySaveSlot> Saves { get; init; } = Array.Empty<RenpySaveSlot>();

    /// <summary>Shared persistent data, when readable.</summary>
    public RenpyPersistentInfo? Persistent { get; init; }

    /// <summary>CG completion, when both the gallery script and persistent data are available.</summary>
    public RenpyCgProgress? CgProgress { get; init; }

    /// <summary>Static script index (labels, call sites, gallery slots).</summary>
    public RenpyScriptIndex? ScriptIndex { get; init; }

    /// <summary>Cross-cutting warnings.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>Wall-clock cost of the whole analysis.</summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>
    /// Labels the player has read at least once, i.e. <c>persistent._seen_ever</c> string keys
    /// intersected with the script label dictionary. This is the honest replacement for a
    /// "chapter percentage" — the reference game has no chapter concept.
    /// </summary>
    public IReadOnlyList<string> UnlockedSceneLabels { get; init; } = Array.Empty<string>();

    /// <summary>Unlocked scenes over total labels, as a 0-100 number (0 when unknown).</summary>
    public double SceneProgressPercent { get; init; }
}

/// <summary>Tuning knobs for <see cref="RenpySaveAnalyzer"/>.</summary>
public sealed record RenpySaveAnalysisOptions
{
    /// <summary>Default options.</summary>
    public static readonly RenpySaveAnalysisOptions Default = new();

    /// <summary>Decode <c>screenshot.png</c> into memory (off by default; thumbnails are ~200 KB each).</summary>
    public bool LoadScreenshots { get; init; }

    /// <summary>Parse each save's <c>log</c> pickle for playtime and scene labels.</summary>
    public bool ParseSaveLogs { get; init; } = true;

    /// <summary>Open the RPA archives and build the label / call-site dictionary.</summary>
    public bool BuildScriptIndex { get; init; } = true;

    /// <summary>Read the shared <c>persistent</c> file (CG progress, seen scenes, choices).</summary>
    public bool ReadPersistent { get; init; } = true;

    /// <summary>Override <c>config.save_directory</c> discovery with an explicit directory.</summary>
    public string? ExplicitSaveDirectory { get; init; }

    /// <summary>Extra directories to probe alongside the standard two.</summary>
    public IReadOnlyList<string> AdditionalSaveDirectories { get; init; } = Array.Empty<string>();

    /// <summary>Upper bound on the number of saves parsed in parallel ("0" = processor count).</summary>
    public int MaxParallelism { get; init; }

    /// <summary>How many dialogue-history entries are copied into <see cref="RenpySaveSlot.RecentDialogue"/>.</summary>
    public int RecentDialogueCount { get; init; } = 5;
}
