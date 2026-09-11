using System.ComponentModel.DataAnnotations;

namespace Galbox.Data.Entities;

/// <summary>
/// Represents a save group — a branch/route grouping of save nodes inside one game.
/// </summary>
/// <remarks>
/// Product source: `_product/Galbox-产品知识总纲.md` §3.3 — "存档组：组名（"A 路线"/"B 路线"）+ 组内存档列表"
/// and §4.2 — "分支名称" is part of the save concept model.
/// <para>
/// A game can be cleared through several mutually exclusive branches (A route / B route / "grand" line /
/// "kakoroute"), and each branch has its own chain of saves. The group is the container that keeps those
/// chains apart in the timeline view; a <see cref="SaveNode"/> that is not filed into any group keeps
/// working on its own (<see cref="SaveNode.SaveGroupId"/> is nullable).
/// </para>
/// </remarks>
public class SaveGroup
{
    /// <summary>
    /// Unique identifier of the save group.
    /// </summary>
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// Identifier of the game this group belongs to.
    /// </summary>
    [Required]
    public int GameInfoId { get; set; }

    /// <summary>
    /// Display name of the group, e.g. "A 路线" / "B 路线" / "Grand 线".
    /// </summary>
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Machine-friendly branch/route identifier used by the engine or the parser,
    /// e.g. <c>a_route</c>, <c>grand</c>, <c>kakoroute</c>. Null when the branch has no such identifier.
    /// </summary>
    [MaxLength(200)]
    public string? RouteName { get; set; }

    /// <summary>
    /// Optional free-form notes about the branch (what it contains, which ending it leads to, ...).
    /// </summary>
    [MaxLength(1000)]
    public string? Description { get; set; }

    /// <summary>
    /// Sort order of the group inside the game's save timeline; groups with a smaller value are shown first.
    /// </summary>
    public int OrderIndex { get; set; }

    /// <summary>
    /// UTC time when the group was created.
    /// </summary>
    public DateTime CreatedTime { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// UTC time when the group was last updated.
    /// </summary>
    public DateTime UpdatedTime { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Navigation property to the owning game.
    /// </summary>
    public GameInfo GameInfo { get; set; } = null!;

    /// <summary>
    /// Save nodes filed into this group. Nodes are kept (not deleted) when the group is removed.
    /// </summary>
    public ICollection<SaveNode> Nodes { get; set; } = new List<SaveNode>();
}
