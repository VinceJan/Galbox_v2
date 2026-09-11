// Scene-set clustering — the ONLY honest signal left for "which branch is this save on".
//
// Why this file exists (product rule, `_product/design/save-node-integration-plan.md` §5 red line 1):
// the game DOES define route flags (kakoroute / risounomirai / yumenomirai in scenario/hensu.rpy), but
// Ren'Py only serialises the variables a play-through has actually changed, and in all 12 real saves of
// the reference game NOT A SINGLE route variable is readable. The remaining signal is how much the set of
// story labels a save has visited overlaps with another save's set.
//
// The output of this class is therefore *speculative by construction*: callers must mark it as such
// (see RenpySaveNodeMapper.SpeculativeRoutePrefix) and must never present it as a fact.
namespace Galbox.Services.Saves;

/// <summary>
/// One slot's visited scene set, as fed into <see cref="SceneSetClusterer"/>.
/// </summary>
/// <param name="Key">Stable slot key (the Ren'Py save slot name).</param>
/// <param name="Scenes">Distinct story scene labels this save has visited.</param>
public sealed record SlotSceneSet(string Key, IReadOnlyCollection<string> Scenes);

/// <summary>
/// One cluster of slots whose visited scene sets overlap enough to be suspected of being the same
/// story branch.
/// </summary>
public sealed record SceneCluster
{
    /// <summary>Member slot keys, in input order.</summary>
    public required IReadOnlyList<string> MemberKeys { get; init; }

    /// <summary>Lowest pairwise Jaccard similarity inside the cluster (1.0 for a single-member cluster).</summary>
    public required double MinimumInternalSimilarity { get; init; }

    /// <summary>Highest Jaccard similarity between a member and a slot outside the cluster (0 when none).</summary>
    public required double MaximumExternalSimilarity { get; init; }

    /// <summary>True when the cluster holds two or more slots, i.e. when it is actual evidence of a group.</summary>
    public bool IsGroup => MemberKeys.Count > 1;
}

/// <summary>
/// Deterministic single-linkage clustering of saves by the Jaccard similarity of their visited scene sets.
/// </summary>
/// <remarks>
/// <para>
/// The algorithm is deliberately trivial and order-deterministic (union-find over the input order), so
/// repeated scans of an unchanged save directory produce byte-identical metadata: the scan service has to
/// be idempotent, and a clustering that shuffles between runs would defeat that.
/// </para>
/// <para>
/// The default threshold is 0.34. It is not a magic product number, it is the value that splits the
/// observed similarity distribution of the reference game's 12 saves at its natural gap (the nearest
/// non-trivial similarities are 0.33 and 0.40), and every cluster it produces is stored next to the
/// threshold that produced it so a later revision can re-derive or contradict the result.
/// </para>
/// </remarks>
public static class SceneSetClusterer
{
    /// <summary>Default Jaccard threshold (see the class remarks for why 0.34).</summary>
    public const double DefaultThreshold = 0.34;

    /// <summary>Jaccard similarity of two sets; two empty sets are treated as identical (1.0).</summary>
    public static double Similarity(IReadOnlyCollection<string> a, IReadOnlyCollection<string> b)
    {
        if (a.Count == 0 && b.Count == 0)
        {
            return 1;
        }

        var setA = a as ISet<string> ?? new HashSet<string>(a, StringComparer.Ordinal);
        var setB = b as ISet<string> ?? new HashSet<string>(b, StringComparer.Ordinal);

        var intersection = 0;

        foreach (var item in setA)
        {
            if (setB.Contains(item))
            {
                intersection++;
            }
        }

        var union = setA.Count + setB.Count - intersection;
        return union == 0 ? 0 : (double)intersection / union;
    }

    /// <summary>
    /// Clusters the supplied slots. Clusters are returned in order of their first member, which keeps the
    /// result stable for a stable input order (the analyzer returns saves newest first, so callers that want
    /// a story-ordered numbering should sort by save time first).
    /// </summary>
    /// <param name="slots">Slot scene sets.</param>
    /// <param name="threshold">Jaccard threshold; values at or above it are linked.</param>
    public static IReadOnlyList<SceneCluster> Cluster(
        IReadOnlyList<SlotSceneSet> slots, double threshold = DefaultThreshold)
    {
        ArgumentNullException.ThrowIfNull(slots);

        if (slots.Count == 0)
        {
            return Array.Empty<SceneCluster>();
        }

        var parent = Enumerable.Range(0, slots.Count).ToArray();

        int Find(int index)
        {
            while (parent[index] != index)
            {
                parent[index] = parent[parent[index]];
                index = parent[index];
            }

            return index;
        }

        for (var i = 0; i < slots.Count; i++)
        {
            for (var j = i + 1; j < slots.Count; j++)
            {
                if (Similarity(slots[i].Scenes, slots[j].Scenes) >= threshold)
                {
                    parent[Find(j)] = Find(i);
                }
            }
        }

        var byRoot = new Dictionary<int, List<int>>();
        var order = new List<int>();

        for (var i = 0; i < slots.Count; i++)
        {
            var root = Find(i);

            if (!byRoot.TryGetValue(root, out var members))
            {
                members = new List<int>();
                byRoot[root] = members;
                order.Add(root);
            }

            members.Add(i);
        }

        var clusters = new List<SceneCluster>(order.Count);

        foreach (var root in order)
        {
            var members = byRoot[root];
            var memberKeys = members.Select(m => slots[m].Key).ToList();

            var minimumInternal = members.Count > 1
                ? members
                    .SelectMany((m, position) => members.Skip(position + 1).Select(n => Similarity(slots[m].Scenes, slots[n].Scenes)))
                    .Min()
                : 1.0;

            var memberSet = members.ToHashSet();
            var maximumExternal = 0.0;

            for (var outside = 0; outside < slots.Count; outside++)
            {
                if (memberSet.Contains(outside))
                {
                    continue;
                }

                foreach (var inside in members)
                {
                    var similarity = Similarity(slots[inside].Scenes, slots[outside].Scenes);

                    if (similarity > maximumExternal)
                    {
                        maximumExternal = similarity;
                    }
                }
            }

            clusters.Add(new SceneCluster
            {
                MemberKeys = memberKeys,
                MinimumInternalSimilarity = minimumInternal,
                MaximumExternalSimilarity = maximumExternal,
            });
        }

        return clusters;
    }
}
