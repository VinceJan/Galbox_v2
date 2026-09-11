using System.ComponentModel.DataAnnotations;

namespace Galbox.Data.Entities;

/// <summary>
/// How a <see cref="SaveNode"/> record came into existence.
/// </summary>
public enum SaveNodeSource
{
    /// <summary>
    /// Manually registered by the user (including user-created snapshots).
    /// </summary>
    Manual = 0,

    /// <summary>
    /// Discovered by scanning the game's save directory with an engine-specific parser.
    /// </summary>
    EngineScan = 1,

    /// <summary>
    /// Imported from an already existing save file or backup that Galbox did not create.
    /// </summary>
    Imported = 2
}

/// <summary>
/// Outcome of the engine-specific save parsing for a <see cref="SaveNode"/>.
/// </summary>
/// <remarks>
/// The data layer never parses saves itself; it only stores the outcome so the UI can explain
/// "why does this node have no scene label?" (product rule from §3.2: failures must be explainable).
/// </remarks>
public enum SaveParseStatus
{
    /// <summary>
    /// No parser was run (node created manually or the engine is not supported yet).
    /// </summary>
    NotAttempted = 0,

    /// <summary>
    /// The parser ran and filled in all fields it knows how to produce.
    /// </summary>
    Parsed = 1,

    /// <summary>
    /// The parser ran but only part of the story metadata could be recovered.
    /// </summary>
    Partial = 2,

    /// <summary>
    /// The parser failed; <see cref="SaveNode.ParseError"/> explains why.
    /// </summary>
    Failed = 3
}

/// <summary>
/// A save node — one save slot / snapshot together with the story position it represents.
/// </summary>
/// <remarks>
/// Product source: `_product/Galbox-产品知识总纲.md` §3.3 and §4.2. This is the entity that was missing from the
/// original implementation: §8.1 records that "存档节点标记" — automatically naming a save after the story node it
/// was taken at, and drawing the resulting timeline — was the number-one differentiator and was dropped entirely,
/// together with chapter progress and CG unlock rate ("能力已经到手，却没有产品化").
/// <para>
/// <b>Story metadata (filled by an engine parser, never by this data layer):</b>
/// <list type="bullet">
/// <item><description>Ren'Py: <see cref="SceneLabel"/> from the save's <c>_last_say_who</c>/label stack (or the
/// label recorded in the pickle), <see cref="SlotName"/> from the <c>.save</c> file name,
/// <see cref="SaveModifiedTime"/> from the file timestamp, <see cref="PlayTimeSeconds"/> from
/// <c>persistent.playtime</c>/<c>_playtime</c>, <see cref="CgUnlockedCount"/> and <see cref="CgUnlockedIdsJson"/>
/// from <c>persistent._seen_images</c>.</description></item>
/// <item><description>Tyrano: same fields read from the JSON save file.</description></item>
/// <item><description>krkr: binary <c>.ksd</c>; only <see cref="SlotName"/>/timestamps are expected at first,
/// with <see cref="ParseStatus"/> set to <see cref="SaveParseStatus.Partial"/>.</description></item>
/// </list>
/// Parser output is pushed in through <see cref="SaveNodeMetadata"/> + <see cref="ApplyMetadata"/> so the parsing
/// workstream never has to change the schema to add a value.
/// </para>
/// <para>
/// <b>Relationship to <see cref="GameSaveBackup"/>:</b> a node describes <i>where the player is in the story</i>,
/// a backup describes <i>files that were copied to disk</i>. They are deliberately separate entities; a backup may
/// optionally point back at the node it protects through <see cref="GameSaveBackup.SaveNodeId"/>.
/// </para>
/// </remarks>
public class SaveNode
{
    /// <summary>
    /// Unique identifier of the save node.
    /// </summary>
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// Identifier of the game this node belongs to.
    /// </summary>
    [Required]
    public int GameInfoId { get; set; }

    /// <summary>
    /// Optional branch/route group this node is filed into. Null when the node is not grouped yet;
    /// nodes are preserved (not deleted) if their group is removed.
    /// </summary>
    public int? SaveGroupId { get; set; }

    /// <summary>
    /// Save slot name as it appears to the player or in the engine's save directory,
    /// e.g. <c>slot 1</c>, <c>auto-1</c>, <c>quick</c>, or a raw Ren'Py file name.
    /// </summary>
    [MaxLength(200)]
    public string? SlotName { get; set; }

    /// <summary>
    /// Story position identifier (剧情位置标识) — the "where am I in the script" label.
    /// For Ren'Py this is the <c>scene_label</c>; for other engines the scene name recovered by the parser.
    /// This is the field that makes a timeline of story progress possible.
    /// </summary>
    [MaxLength(500)]
    public string? SceneLabel { get; set; }

    /// <summary>
    /// Branch/route name recorded on the node itself, e.g. <c>A route</c>, <c>grand line</c>, <c>kakoroute</c>.
    /// Kept on the node (not only on <see cref="SaveGroup"/>) so an imported or ungrouped save still carries the
    /// route it was taken on, and so the information survives group deletion.
    /// Use <see cref="EffectiveRouteName"/> to read "node value, falling back to the group value".
    /// </summary>
    [MaxLength(200)]
    public string? RouteName { get; set; }

    /// <summary>
    /// Human-readable chapter/scene title for this node, e.g. "第二章 魔女的夜宴".
    /// Complements <see cref="ChapterProgressPercent"/>, which is only a number.
    /// </summary>
    [MaxLength(200)]
    public string? ChapterName { get; set; }

    /// <summary>
    /// Chapter/story progress of this save, 0-100 as required by §4.2.
    /// Null when the engine does not expose a progress figure (unknown is not the same as 0%).
    /// </summary>
    public int? ChapterProgressPercent { get; set; }

    /// <summary>
    /// Number of unlocked CG images at this node ("已解锁数"). Authoritative together with
    /// <see cref="CgTotalCount"/>: the product must be able to say how many CG are still missing,
    /// which a bare percentage cannot express.
    /// </summary>
    public int CgUnlockedCount { get; set; }

    /// <summary>
    /// Total number of CG images the game contains ("总数"). Zero means "not known yet".
    /// </summary>
    public int CgTotalCount { get; set; }

    /// <summary>
    /// CG unlock rate, 0-100, as reported by the engine when it exposes a percentage directly.
    /// Null when the rate has to be computed from the counts — see <see cref="EffectiveCgUnlockPercent"/>.
    /// </summary>
    public int? CgUnlockPercent { get; set; }

    /// <summary>
    /// JSON array of the CG/gallery identifiers unlocked at this node (engine-specific, e.g. Ren'Py
    /// <c>persistent._seen_images</c> names). This is what allows the product to answer
    /// "还差哪几张" instead of only "解锁了 63%".
    /// Identifiers are raw engine asset names; mapping them to thumbnails needs an asset catalog (§6, future).
    /// </summary>
    public string? CgUnlockedIdsJson { get; set; }

    /// <summary>
    /// Play time recorded inside the save itself, in seconds (§4.2 "存档内游玩时长").
    /// This is the game's own counter at the moment of saving, not Galbox's session total.
    /// </summary>
    public long PlayTimeSeconds { get; set; }

    /// <summary>
    /// Path of the save file (or save slot directory) this node describes. Null for nodes that only exist
    /// as metadata (for example a snapshot description typed by the user).
    /// </summary>
    [MaxLength(2000)]
    public string? SaveFilePath { get; set; }

    /// <summary>
    /// Size of the save file(s) on disk in bytes (§4.2 存档元数据).
    /// </summary>
    public long SaveSizeBytes { get; set; }

    /// <summary>
    /// Number of files that make up this save slot (§4.2 存档元数据).
    /// </summary>
    public int SaveFileCount { get; set; }

    /// <summary>
    /// Creation time of the save itself (from the engine metadata or the file system).
    /// Distinct from <see cref="CreatedTime"/>, which is when this row was written.
    /// </summary>
    public DateTime? SaveCreatedTime { get; set; }

    /// <summary>
    /// Last modification time of the save itself (from the engine metadata or the file system).
    /// Distinct from <see cref="UpdatedTime"/>, which is when this row was written.
    /// </summary>
    public DateTime? SaveModifiedTime { get; set; }

    /// <summary>
    /// UTC time when this node row was created.
    /// </summary>
    public DateTime CreatedTime { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// UTC time when this node row was last updated.
    /// </summary>
    public DateTime UpdatedTime { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// True when the node is a snapshot — a key story point the player deliberately marked
    /// (§3.3 "存档快照：玩家主动创建关键节点存档备份").
    /// </summary>
    public bool IsSnapshot { get; set; }

    /// <summary>
    /// User-facing description of the snapshot ("快照描述"), e.g. "第一次遇见宁宁，之后选 A 线".
    /// Only meaningful when <see cref="IsSnapshot"/> is true.
    /// </summary>
    [MaxLength(1000)]
    public string? SnapshotDescription { get; set; }

    /// <summary>
    /// How this node was created. Defaults to <see cref="SaveNodeSource.Manual"/> so that older rows and
    /// user-typed nodes keep their meaning.
    /// </summary>
    public SaveNodeSource Source { get; set; } = SaveNodeSource.Manual;

    /// <summary>
    /// Outcome of the engine save parsing for this node; see <see cref="SaveParseStatus"/>.
    /// </summary>
    public SaveParseStatus ParseStatus { get; set; } = SaveParseStatus.NotAttempted;

    /// <summary>
    /// Human-readable reason when <see cref="ParseStatus"/> is <see cref="SaveParseStatus.Failed"/> or
    /// <see cref="SaveParseStatus.Partial"/>, so a failure can always be explained to the user.
    /// </summary>
    [MaxLength(1000)]
    public string? ParseError { get; set; }

    /// <summary>
    /// Escape hatch for engine-specific values that do not have a dedicated column yet (JSON object).
    /// The data layer never interprets it; a value that the product starts using must be promoted to a real
    /// column with a migration.
    /// </summary>
    public string? ExtendedMetadataJson { get; set; }

    /// <summary>
    /// Navigation property to the owning game.
    /// </summary>
    public GameInfo GameInfo { get; set; } = null!;

    /// <summary>
    /// Navigation property to the optional branch/route group.
    /// </summary>
    public SaveGroup? SaveGroup { get; set; }

    /// <summary>
    /// Backups that protect this node (usually zero or one); see <see cref="GameSaveBackup.SaveNodeId"/>.
    /// </summary>
    public ICollection<GameSaveBackup> Backups { get; set; } = new List<GameSaveBackup>();

    /// <summary>
    /// Route/branch name to show for this node: the node's own <see cref="RouteName"/> when set,
    /// otherwise the name of its <see cref="SaveGroup"/>.
    /// </summary>
    /// <remarks>Requires <see cref="SaveGroup"/> to be loaded; returns the node value otherwise.</remarks>
    public string? EffectiveRouteName =>
        !string.IsNullOrWhiteSpace(RouteName) ? RouteName : SaveGroup?.RouteName;

    /// <summary>
    /// CG unlock rate in percent, preferring the engine-reported <see cref="CgUnlockPercent"/> and otherwise
    /// computing it from <see cref="CgUnlockedCount"/> / <see cref="CgTotalCount"/>.
    /// Returns null when the total is unknown (0).
    /// </summary>
    public int? EffectiveCgUnlockPercent
    {
        get
        {
            if (CgUnlockPercent.HasValue)
            {
                return CgUnlockPercent.Value;
            }

            if (CgTotalCount <= 0)
            {
                return null;
            }

            return (int)Math.Round(CgUnlockedCount * 100.0 / CgTotalCount, MidpointRounding.AwayFromZero);
        }
    }

    /// <summary>
    /// Number of CG images still locked at this node, or null when the total is unknown.
    /// Answers the product requirement "tell the user what is still missing".
    /// </summary>
    public int? RemainingCgCount =>
        CgTotalCount <= 0 ? null : Math.Max(0, CgTotalCount - CgUnlockedCount);

    /// <summary>
    /// Short label for the node, preferring the snapshot description, then the scene label, then the slot name.
    /// </summary>
    public string DisplayName
    {
        get
        {
            if (IsSnapshot && !string.IsNullOrWhiteSpace(SnapshotDescription))
            {
                return SnapshotDescription!;
            }

            if (!string.IsNullOrWhiteSpace(SceneLabel))
            {
                return SceneLabel!;
            }

            if (!string.IsNullOrWhiteSpace(ChapterName))
            {
                return ChapterName!;
            }

            if (!string.IsNullOrWhiteSpace(SlotName))
            {
                return SlotName!;
            }

            return $"Save #{Id}";
        }
    }

    /// <summary>
    /// Play time stored in this node, formatted as a human-readable string.
    /// </summary>
    public string FormattedPlayTime
    {
        get
        {
            var hours = PlayTimeSeconds / 3600;
            var minutes = (PlayTimeSeconds % 3600) / 60;
            return hours > 0 ? $"{hours}h {minutes}m" : $"{minutes}m";
        }
    }

    /// <summary>
    /// Creates a user-marked snapshot node for a game (the "关键节点存档备份" flow of §3.3).
    /// </summary>
    /// <param name="gameInfoId">Identifier of the game the snapshot belongs to.</param>
    /// <param name="description">User-facing snapshot description.</param>
    /// <param name="slotName">Optional save slot name the snapshot was taken from.</param>
    /// <returns>A new, not yet persisted <see cref="SaveNode"/>.</returns>
    public static SaveNode CreateSnapshot(int gameInfoId, string description, string? slotName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        var now = DateTime.UtcNow;
        return new SaveNode
        {
            GameInfoId = gameInfoId,
            SlotName = slotName,
            IsSnapshot = true,
            SnapshotDescription = description.Trim(),
            Source = SaveNodeSource.Manual,
            ParseStatus = SaveParseStatus.NotAttempted,
            CreatedTime = now,
            UpdatedTime = now
        };
    }

    /// <summary>
    /// Copies the values a save parser was able to recover into this node.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Integration seam for the parsing workstream.</b> A parser (Ren'Py / Tyrano / krkr) builds a
    /// <see cref="SaveNodeMetadata"/> from the save file and hands it over here; only non-null values overwrite
    /// existing data, so a partial parse never erases information a previous, better parse had produced.
    /// </para>
    /// <para>
    /// Percentages are clamped into 0-100 and counters into &gt;= 0 before being stored, so a buggy parser cannot
    /// poison the database with out-of-range values.
    /// </para>
    /// </remarks>
    /// <param name="metadata">Values recovered from the save; null members are ignored.</param>
    /// <param name="utcNow">Current UTC time; defaults to <see cref="DateTime.UtcNow"/>.</param>
    /// <returns>True when at least one stored value changed.</returns>
    public bool ApplyMetadata(SaveNodeMetadata metadata, DateTime? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        var changed = false;

        changed |= SetIfNotNull(metadata.SlotName, v => SlotName = v);
        changed |= SetIfNotNull(metadata.SceneLabel, v => SceneLabel = v);
        changed |= SetIfNotNull(metadata.RouteName, v => RouteName = v);
        changed |= SetIfNotNull(metadata.ChapterName, v => ChapterName = v);
        changed |= SetIfNotNull(metadata.SaveFilePath, v => SaveFilePath = v);
        changed |= SetIfNotNull(metadata.CgUnlockedIdsJson, v => CgUnlockedIdsJson = v);
        changed |= SetIfNotNull(metadata.ExtendedMetadataJson, v => ExtendedMetadataJson = v);
        changed |= SetIfNotNull(metadata.ParseError, v => ParseError = v);

        changed |= SetIfNotNull(
            metadata.ChapterProgressPercent,
            v => ChapterProgressPercent = ClampPercent(v));

        changed |= SetIfNotNull(
            metadata.CgUnlockPercent,
            v => CgUnlockPercent = ClampPercent(v));

        changed |= SetIfNotNull(metadata.CgUnlockedCount, v => CgUnlockedCount = Math.Max(0, v));
        changed |= SetIfNotNull(metadata.CgTotalCount, v => CgTotalCount = Math.Max(0, v));
        changed |= SetIfNotNull(metadata.PlayTimeSeconds, v => PlayTimeSeconds = Math.Max(0L, v));
        changed |= SetIfNotNull(metadata.SaveSizeBytes, v => SaveSizeBytes = Math.Max(0L, v));
        changed |= SetIfNotNull(metadata.SaveFileCount, v => SaveFileCount = Math.Max(0, v));
        changed |= SetIfNotNull(metadata.SaveCreatedTime, v => SaveCreatedTime = v);
        changed |= SetIfNotNull(metadata.SaveModifiedTime, v => SaveModifiedTime = v);
        changed |= SetIfNotNull(metadata.ParseStatus, v => ParseStatus = v);

        // A node that was populated from a real save file is no longer a purely manual entry.
        if (changed && Source == SaveNodeSource.Manual && metadata.ParseStatus.HasValue &&
            metadata.ParseStatus.Value != SaveParseStatus.NotAttempted)
        {
            Source = SaveNodeSource.EngineScan;
        }

        if (changed)
        {
            UpdatedTime = utcNow ?? DateTime.UtcNow;
        }

        return changed;
    }

    private static bool SetIfNotNull<T>(T? value, Action<T> setter) where T : struct
    {
        if (!value.HasValue)
        {
            return false;
        }

        setter(value.Value);
        return true;
    }

    private static bool SetIfNotNull(string? value, Action<string> setter)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        setter(value);
        return true;
    }

    private static int ClampPercent(int value) => Math.Clamp(value, 0, 100);
}

/// <summary>
/// Values a engine-specific save parser can recover for one save slot.
/// </summary>
/// <remarks>
/// This is the documented contract between the data layer and the future save-parsing workstream
/// (see <see cref="SaveNode.ApplyMetadata"/>). It deliberately mirrors the fields of the save metadata DTO that
/// already exists in the application layer (<c>Galbox.App.Services.SaveMetadata</c>), so the mapping between the
/// two stays mechanical. Null members mean "the parser could not determine this value" and are never written.
/// </remarks>
public class SaveNodeMetadata
{
    /// <summary>
    /// Save slot name or number.
    /// </summary>
    public string? SlotName { get; set; }

    /// <summary>
    /// Story position identifier (Ren'Py <c>scene_label</c>, other engines' scene name).
    /// </summary>
    public string? SceneLabel { get; set; }

    /// <summary>
    /// Branch/route name the save was taken on.
    /// </summary>
    public string? RouteName { get; set; }

    /// <summary>
    /// Human-readable chapter title.
    /// </summary>
    public string? ChapterName { get; set; }

    /// <summary>
    /// Chapter progress, 0-100. Clamped by <see cref="SaveNode.ApplyMetadata"/>.
    /// </summary>
    public int? ChapterProgressPercent { get; set; }

    /// <summary>
    /// Number of unlocked CG images.
    /// </summary>
    public int? CgUnlockedCount { get; set; }

    /// <summary>
    /// Total number of CG images in the game.
    /// </summary>
    public int? CgTotalCount { get; set; }

    /// <summary>
    /// CG unlock rate in percent, when the engine reports a rate directly.
    /// </summary>
    public int? CgUnlockPercent { get; set; }

    /// <summary>
    /// JSON array of unlocked CG identifiers (raw engine asset names).
    /// </summary>
    public string? CgUnlockedIdsJson { get; set; }

    /// <summary>
    /// Play time recorded inside the save, in seconds.
    /// </summary>
    public long? PlayTimeSeconds { get; set; }

    /// <summary>
    /// Path of the parsed save file.
    /// </summary>
    public string? SaveFilePath { get; set; }

    /// <summary>
    /// Size of the save file(s) in bytes.
    /// </summary>
    public long? SaveSizeBytes { get; set; }

    /// <summary>
    /// Number of files that make up the save slot.
    /// </summary>
    public int? SaveFileCount { get; set; }

    /// <summary>
    /// Creation time of the save itself.
    /// </summary>
    public DateTime? SaveCreatedTime { get; set; }

    /// <summary>
    /// Last modification time of the save itself.
    /// </summary>
    public DateTime? SaveModifiedTime { get; set; }

    /// <summary>
    /// Parse outcome; null leaves the node's current <see cref="SaveNode.ParseStatus"/> untouched.
    /// </summary>
    public SaveParseStatus? ParseStatus { get; set; }

    /// <summary>
    /// Explanation for a failed or partial parse.
    /// </summary>
    public string? ParseError { get; set; }

    /// <summary>
    /// Raw engine-specific extras that have no dedicated column (JSON object).
    /// </summary>
    public string? ExtendedMetadataJson { get; set; }
}
