// The JSON payload the scan service stores in SaveNode.ExtendedMetadataJson.
//
// This is the "escape hatch" column of the entity (see SaveNode.ExtendedMetadataJson): every value that has
// no dedicated column yet lives here so that adding product information never requires a schema migration.
// Values that the product starts using as a first-class concept must be promoted to a real column later.
//
// Everything in here is *evidence*: where a number came from, what it describes, and how sure the parser is.
// The scan service never writes a speculative value without also writing the method that produced it.
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Galbox.Services.Saves;

/// <summary>One on-disk copy of a save slot (Ren'Py writes every save to two locations).</summary>
/// <param name="Path">Absolute path of the copy.</param>
/// <param name="Location">Which well-known Ren'Py location the copy lives in.</param>
/// <param name="ModifiedUtc">File mtime, which is the save time.</param>
/// <param name="SizeBytes">File size.</param>
/// <param name="IsEffective">True for the copy this node's <c>SaveFilePath</c> points at.</param>
public sealed record SaveMirrorCopy(
    string Path,
    string Location,
    string ModifiedUtc,
    long SizeBytes,
    bool IsEffective);

/// <summary>Last dialogue line recorded in the save, for the "where was I" tooltip.</summary>
/// <param name="Who">Speaker.</param>
/// <param name="Text">Line text.</param>
public sealed record SaveDialoguePreview(string? Who, string? Text);

/// <summary>
/// Progress values that belong to the game as a whole rather than to this one save.
/// </summary>
/// <remarks>
/// Ren'Py keeps CG unlocks, seen scenes and choices in the shared <c>persistent</c> file, which is
/// mutated by the running game. There is no per-save CG state anywhere in a <c>.save</c> file, so the
/// same numbers are written to every node and they mean "the game's progress as of this scan", NOT
/// "the progress this save had when it was written". The UI must therefore show them once per game
/// (labelled as current progress) and never as a per-node attribute.
/// </remarks>
public sealed record GameLevelProgress
{
    /// <summary>File the values were read from.</summary>
    public string? PersistentFile { get; init; }

    /// <summary>mtime of that file (when the game last updated this data).</summary>
    public string? PersistentModifiedUtc { get; init; }

    /// <summary>Explanation shown to the user, e.g. "游戏级数据（persistent），不是这个存档当时的进度".</summary>
    public string Scope { get; init; } = "game-level (shared persistent file), identical on every node";

    /// <summary>How the CG rate was computed.</summary>
    public string? CgMethod { get; init; }

    /// <summary>Raw <c>_seen_images</c> key count, so the CG intersection cannot be mistaken for it.</summary>
    public int? SeenImageKeyCount { get; init; }

    /// <summary>CG ids that are still locked, which is what the product must be able to list.</summary>
    public IReadOnlyList<string>? LockedCgIds { get; init; }

    /// <summary>Movie-gallery unlock flags of the reference galaxy script (<c>opmv</c>, <c>kadoed</c>, ...).</summary>
    public IReadOnlyDictionary<string, bool>? MovieUnlocks { get; init; }

    /// <summary>Menu choices the player has taken (<c>persistent._chosen</c>), with the label when resolvable.</summary>
    public IReadOnlyList<string>? ChosenOptions { get; init; }
}

/// <summary>
/// The honest replacement for "chapter progress": unlocked scenes over total scenes.
/// </summary>
/// <remarks>
/// The reference game has no chapter concept at all (the script is a flat list of labels), so a chapter
/// percentage cannot exist and a line-number percentage must never be invented. Both variants below are
/// the same metric over two denominators: the analyzer's own figure counts every declared label, the
/// story-only figure excludes the helper labels that live in <c>macro</c> scripts.
/// </remarks>
public sealed record SceneProgress
{
    /// <summary>Labels the player has read at least once (analyzer definition, includes helper labels).</summary>
    public int UnlockedScenes { get; init; }

    /// <summary>Labels declared by the game scripts (analyzer denominator).</summary>
    public int TotalScenes { get; init; }

    /// <summary>Story-only variant: unlocked scenes that are real scenes.</summary>
    public int UnlockedStoryScenes { get; init; }

    /// <summary>Story-only variant: total real scenes.</summary>
    public int TotalStoryScenes { get; init; }

    /// <summary>How the numbers were produced.</summary>
    public string Method { get; init; } =
        "persistent._seen_ever (string keys) intersected with the script label dictionary (RenpySaveAnalyzer.UnlockedSceneLabels)";

    /// <summary>Always false for this engine/game family: recorded so the UI does not invent chapters.</summary>
    public bool GameDefinesChapters { get; init; }
}

/// <summary>
/// The scene-set clustering that produced (or failed to produce) a suspected route grouping.
/// </summary>
/// <remarks>
/// Product rule (§5 red line 1): no route variable is readable in the reference saves, so nothing here may
/// be presented as a fact. When the clustering finds no genuine group, <see cref="RouteNameCandidate"/>
/// stays null and the node's <c>RouteName</c> column stays empty — an empty field is honest, a made-up
/// group is not.
/// </remarks>
public sealed record SuspectedRoute
{
    /// <summary>Always true: this value is speculation and must be displayed as such.</summary>
    public bool IsSpeculative { get; init; } = true;

    /// <summary>What was clustered.</summary>
    public string Signal { get; init; } = "Jaccard similarity of the visited story-scene sets";

    /// <summary>Jaccard threshold used for the single-linkage clustering.</summary>
    public double Threshold { get; init; }

    /// <summary>Why no route variable was used.</summary>
    public string? RouteVariablesUnavailableBecause { get; init; }

    /// <summary>Identifier of this slot's cluster, e.g. <c>R1</c>.</summary>
    public string? ClusterId { get; init; }

    /// <summary>How many slots share this cluster.</summary>
    public int ClusterSize { get; init; }

    /// <summary>Total number of clusters the scan produced.</summary>
    public int ClusterCount { get; init; }

    /// <summary>Lowest similarity to another member of the same cluster (1.0 for a lone slot).</summary>
    public double MinimumInternalSimilarity { get; init; }

    /// <summary>Highest similarity to a slot outside the cluster.</summary>
    public double MaximumExternalSimilarity { get; init; }

    /// <summary>
    /// The speculative group name written to <c>SaveNode.RouteName</c>, or null when the slot is alone in
    /// its cluster (no evidence of a branch).
    /// </summary>
    public string? RouteNameCandidate { get; init; }
}

/// <summary>Version information of the save versus the installed game.</summary>
/// <param name="SaveGameVersion"><c>json._version</c> of the save.</param>
/// <param name="InstalledGameVersion"><c>config.version</c> of the installation.</param>
/// <param name="RenpyVersion">Engine version that wrote the save.</param>
/// <param name="IsMismatch">True when the save was written by a different game version.</param>
public sealed record SaveVersionInfo(
    string? SaveGameVersion,
    string? InstalledGameVersion,
    string? RenpyVersion,
    bool IsMismatch);

/// <summary>Everything in <see cref="Galbox.Data.Entities.SaveNode.ExtendedMetadataJson"/> for one node.</summary>
public sealed record SaveNodeExtendedMetadata
{
    /// <summary>Schema version of this payload; bump when the shape changes.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Save engine the node was produced by.</summary>
    public string Engine { get; init; } = "renpy";

    /// <summary>Parser entry point that produced the values.</summary>
    public string Parser { get; init; } = "Galbox.Core.Saves.RenpySaveAnalyzer";

    // NOTE: there is deliberately no "scan time" member here. The payload must be a pure function of the
    // save directory, so that re-scanning an unchanged game produces byte-identical JSON and the scan is a
    // no-op. The moment a row was last written lives in SaveNode.UpdatedTime, and the report carries the
    // scan duration and start time.

    /// <summary>Ren'Py slot key derived from the file name (<c>auto-3-LT1</c>), the stable identity of the slot.</summary>
    public string? SlotKey { get; init; }

    /// <summary><c>json._save_name</c> — the name the player typed; empty for games that never set one.</summary>
    public string? PlayerSaveName { get; init; }

    /// <summary>Which well-known Ren'Py location the effective copy was read from.</summary>
    public string? EffectiveLocation { get; init; }

    /// <summary>Rule used to pick the effective copy among the mirrors (both locations are written on every save).</summary>
    public string EffectivePathRule { get; init; } = "newest file mtime (Ren'Py MultiLocation writes both copies)";

    /// <summary>Every on-disk copy of this slot, including the effective one.</summary>
    public IReadOnlyList<SaveMirrorCopy>? Mirrors { get; init; }

    /// <summary>How the parser resolved <c>SceneLabel</c> (transparency for the UI tooltip).</summary>
    public string? SceneLabelSource { get; init; }

    /// <summary>Script file that defines the resolved scene label.</summary>
    public string? SceneLabelFile { get; init; }

    /// <summary>Story labels this save has visited, oldest first (truncated to the limit below).</summary>
    public IReadOnlyList<string>? SceneSequence { get; init; }

    /// <summary>Number of visited story labels before truncation.</summary>
    public int SceneSequenceCount { get; init; }

    /// <summary>True when <see cref="SceneSequence"/> was cut short.</summary>
    public bool SceneSequenceTruncated { get; init; }

    /// <summary>Last dialogue line, for the timeline tooltip.</summary>
    public SaveDialoguePreview? LastDialogue { get; init; }

    /// <summary>Game-level progress (see the remarks on <see cref="GameLevelProgress"/>).</summary>
    public GameLevelProgress? GameLevel { get; init; }

    /// <summary>Scene progress (the honest replacement for chapter progress).</summary>
    public SceneProgress? SceneProgress { get; init; }

    /// <summary>Scene-set clustering result for this slot.</summary>
    public SuspectedRoute? SuspectedRouteGroup { get; init; }

    /// <summary>Save version versus installed version.</summary>
    public SaveVersionInfo? Version { get; init; }

    /// <summary>Non-fatal problems the parser reported for this save (kept verbatim for diagnosis).</summary>
    public IReadOnlyList<string>? ParserNotes { get; init; }

    /// <summary>Serializer settings: camelCase, no null members, non-ASCII kept readable.</summary>
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    /// <summary>Serializes this payload for storage in <c>SaveNode.ExtendedMetadataJson</c>.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);

    /// <summary>Reads a payload back; returns null when the text is not a payload of this schema.</summary>
    public static SaveNodeExtendedMetadata? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<SaveNodeExtendedMetadata>(json, SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
