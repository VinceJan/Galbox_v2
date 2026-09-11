using System.Text.Json;
using Galbox.Core.Community.Flowcharts;
using Galbox.Data.Entities;

namespace Galbox.Services.Community;

/// <summary>
/// Projects the stored save nodes onto the story positions the reserved flowchart sync consumes.
/// </summary>
/// <remarks>
/// <para>
/// This is the joint between the two halves of the product. "存档节点标记" already works: a scan
/// turns each save of a Ren'Py game into a <see cref="SaveNode"/> row carrying the scene label, the
/// route, the chapter, the unlocked CG list and the play time. The flowchart is documented as the
/// natural destination of exactly that knowledge — <c>POST /api/v1/flowcharts/{game_id}/sync</c> is
/// 「把存档位置同步到流程图节点 —— 这是『存档节点标记』与『流程图』的咬合点，也是整个产品最巧妙
/// 的设计」 (spec §6.1). <see cref="StoryPositionSample"/> is the small vocabulary that joint talks
/// in, and this class is the one place that translates into it.
/// </para>
/// <para>
/// <b>Why the translation is not inline in the flow chart provider.</b> The provider will be written
/// against a remote API and will live in a layer that must not know about EF entities; keeping the
/// mapping here means the wire model stays clean, the mapping can be tested without a network, and
/// the day a second save engine (Tyrano, KiriKiri) starts filling the same columns, only this file
/// has to change. It also mirrors where the existing glue already lives
/// (<c>Galbox.Services.Saves</c>: <c>SaveNodeScanService</c>, <c>RenpySaveNodeMapper</c>).
/// </para>
/// <para>
/// <b>This class implements no part of the reserved feature.</b> It builds no graph, calls no
/// service and renders nothing; it is pure, side-effect free and reads only the fields it was handed.
/// It exists now rather than later because the shape of the seam is exactly what a reservation has
/// to get right — a mis-shaped seam is discovered when the feature is finally built, which is the
/// most expensive moment to discover it.
/// </para>
/// </remarks>
public static class SaveNodePositionProjector
{
    /// <summary>
    /// Projects a set of stored nodes into story order.
    /// </summary>
    /// <remarks>
    /// Story order is <see cref="SaveNode.SaveModifiedTime"/> ascending with nodes that carry no
    /// timestamp last, ties broken by <see cref="SaveNode.Id"/>. Insertion order will not do: the
    /// database hands rows back in whatever order the query produced, and a flowchart that walked a
    /// player's saves in that order would draw a route backwards.
    /// </remarks>
    /// <param name="nodes">The stored nodes to project.</param>
    /// <returns>One sample per node, oldest first.</returns>
    public static IReadOnlyList<StoryPositionSample> Project(IEnumerable<SaveNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        return nodes
            .Where(node => node is not null)
            .OrderBy(node => node.SaveModifiedTime ?? node.SaveCreatedTime ?? DateTime.MaxValue)
            .ThenBy(node => node.Id)
            .Select(ProjectOne)
            .ToList();
    }

    /// <summary>
    /// Projects one stored node onto one story position.
    /// </summary>
    /// <param name="node">The stored node to project.</param>
    /// <returns>The sample the flowchart sync would be given.</returns>
    public static StoryPositionSample ProjectOne(SaveNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        return new StoryPositionSample
        {
            GameInfoId = node.GameInfoId,
            SaveNodeId = node.Id,
            SlotName = node.SlotName,

            // The label is what a node is matched on, so it is carried verbatim or not at all.
            //
            // There used to be a fallback to DisplayName here ("the node has no scene label, so send
            // the best readable description instead"), and it was removed on purpose: DisplayName
            // falls back to the snapshot description, then the chapter, then the slot name, so the
            // server would have received a string that looks like a scene label, cannot match any
            // node, and is indistinguishable from a real label. A save whose scene label is unknown
            // cannot be placed on a flowchart, and the honest outcome is 'cannot be placed'.
            SceneLabel = string.IsNullOrWhiteSpace(node.SceneLabel) ? null : node.SceneLabel,

            // The node's own route name, falling back to its group's, exactly as the timeline reads
            // it: a node must still carry the route it was taken on after its group is deleted.
            RouteName = node.EffectiveRouteName,

            ChapterName = node.ChapterName,
            ChapterProgressPercent = node.ChapterProgressPercent,
            CgUnlockedIds = ReadCgIds(node.CgUnlockedIdsJson),
            PlayTimeSeconds = node.PlayTimeSeconds,
            SavedAtUtc = node.SaveModifiedTime ?? node.SaveCreatedTime
        };
    }

    /// <summary>
    /// Reads the unlocked CG identifiers out of the free-form JSON column.
    /// </summary>
    /// <remarks>
    /// The column is written by an engine parser and holds whatever that parser could recover, so a
    /// malformed value is an expected condition and not an exception: an unreadable list degrades to
    /// "no identifiers known", which is exactly what the honest answer is. Duplicates are dropped
    /// because two saves written from the same gallery state would otherwise inflate every count
    /// built on this list.
    /// </remarks>
    /// <param name="json">Raw column value; may be null, blank or malformed.</param>
    /// <returns>The distinct identifiers in their stored order; empty when nothing could be read.</returns>
    private static IReadOnlyList<string> ReadCgIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<string>();
        }

        string[]? ids;
        try
        {
            ids = JsonSerializer.Deserialize<string[]>(json);
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }

        if (ids is null || ids.Length == 0)
        {
            return Array.Empty<string>();
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>(ids.Length);

        foreach (var id in ids)
        {
            if (!string.IsNullOrWhiteSpace(id) && seen.Add(id))
            {
                result.Add(id);
            }
        }

        return result;
    }
}
