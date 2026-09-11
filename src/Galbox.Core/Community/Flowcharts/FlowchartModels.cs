namespace Galbox.Core.Community.Flowcharts;

/// <summary>
/// The kind of a flowchart node (spec §6.1, 「节点类型」row).
/// </summary>
/// <remarks>
/// The published API carries these as the lower-case tokens <c>start</c> / <c>route</c> /
/// <c>branch</c> / <c>ending</c> / <c>event</c> (<c>docs/API_DOCUMENTATION.md</c>, 「节点类型说明」).
/// This enum deliberately does not pin a serializer policy: the transport layer does not exist yet,
/// and choosing one now would be a decision taken without the code that has to live with it.
/// </remarks>
public enum FlowchartNodeType
{
    /// <summary>起始点 — where a playthrough starts.</summary>
    Start = 0,

    /// <summary>路线节点 — a story node belonging to a route.</summary>
    Route = 1,

    /// <summary>分支节点 — a point where the player chooses.</summary>
    Branch = 2,

    /// <summary>结局节点 — an ending.</summary>
    Ending = 3,

    /// <summary>特殊事件节点 — a special event.</summary>
    Event = 4
}

/// <summary>
/// The flavour of an ending, when the flowchart declares one.
/// </summary>
/// <remarks>The specification gives <c>good</c> as an example and leaves the rest to the data.</remarks>
public enum FlowchartEndingKind
{
    /// <summary>Not stated by the flowchart.</summary>
    Unknown = 0,

    /// <summary>Good end.</summary>
    Good = 1,

    /// <summary>Normal end.</summary>
    Normal = 2,

    /// <summary>Bad end.</summary>
    Bad = 3,

    /// <summary>True end.</summary>
    True = 4
}

/// <summary>
/// A node's position on the canvas (spec §6.1: 节点带二维坐标).
/// </summary>
/// <param name="X">Horizontal coordinate, in the flowchart's own units.</param>
/// <param name="Y">Vertical coordinate, in the flowchart's own units.</param>
public sealed record FlowchartPoint(double X, double Y);

/// <summary>
/// The unlock condition of one choice (spec §6.1: 「解锁条件（flag/value，如 route_b_unlocked）」).
/// </summary>
/// <param name="Flag">Name of the game flag the choice depends on.</param>
/// <param name="Value">Value the flag must have for the choice to be available.</param>
public sealed record FlowchartChoiceCondition(string Flag, bool Value);

/// <summary>
/// One branch option leaving a node (spec §6.1: 「选项（分支）：选项文本 + 跳转目标 + 解锁条件」).
/// </summary>
/// <param name="Id">Identifier of the option within the flowchart.</param>
/// <param name="Text">Option text as the player sees it.</param>
/// <param name="NextNodeId">Identifier of the node the option leads to.</param>
/// <param name="Condition">Unlock condition, or null when the option is always available.</param>
public sealed record FlowchartChoice(
    string Id,
    string Text,
    string NextNodeId,
    FlowchartChoiceCondition? Condition = null);

/// <summary>
/// One node of a game's flowchart (spec §6.1, 「数据形态」and 「节点类型」).
/// </summary>
/// <remarks>
/// The fields the published API nests under a node's <c>metadata</c> object (chapter, description,
/// character, routeType, endingType, cg list) are flattened here, and each one is nullable because
/// a node of a given <see cref="Type"/> only carries the ones that apply to it: a <c>route</c> node
/// carries <see cref="Character"/> and <see cref="RouteType"/>, an <c>ending</c> node carries
/// <see cref="EndingKind"/> and <see cref="CgIds"/>. Splitting them into five node subclasses would
/// make the model look precise and the deserializer fragile.
/// </remarks>
public sealed record FlowchartNode
{
    /// <summary>Identifier of the node within the flowchart.</summary>
    public required string Id { get; init; }

    /// <summary>Label shown on the node.</summary>
    public required string Label { get; init; }

    /// <summary>What kind of node this is.</summary>
    public required FlowchartNodeType Type { get; init; }

    /// <summary>Canvas position, or null when the data does not lay the node out.</summary>
    public FlowchartPoint? Position { get; init; }

    /// <summary>Chapter the node belongs to, when the data names one.</summary>
    public string? Chapter { get; init; }

    /// <summary>Free-text description of the node.</summary>
    public string? Description { get; init; }

    /// <summary>Route node only: the character this node belongs to (spec §6.1, 「路线节点附加」).</summary>
    public string? Character { get; init; }

    /// <summary>Route node only: the route type, e.g. <c>romance</c>.</summary>
    public string? RouteType { get; init; }

    /// <summary>Ending node only: the flavour of the ending.</summary>
    public FlowchartEndingKind? EndingKind { get; init; }

    /// <summary>Ending node only: the CG unlocked by reaching it (spec §6.1, 「结局」).</summary>
    public IReadOnlyList<string> CgIds { get; init; } = Array.Empty<string>();

    /// <summary>Branch node only: the options leaving this node.</summary>
    public IReadOnlyList<FlowchartChoice> Choices { get; init; } = Array.Empty<FlowchartChoice>();
}

/// <summary>
/// How an edge is drawn (spec §6.1: 连线带颜色与虚实样式).
/// </summary>
/// <param name="Color">Colour the flowchart asks for, in whatever notation the data uses.</param>
/// <param name="Dashed">True for a dashed line, false for a solid one.</param>
public sealed record FlowchartEdgeStyle(string? Color = null, bool Dashed = false);

/// <summary>
/// One connection between two nodes (spec §6.1: 节点 + 连线 + 路线分组).
/// </summary>
/// <param name="Id">Identifier of the edge.</param>
/// <param name="From">Identifier of the node the edge leaves.</param>
/// <param name="To">Identifier of the node the edge enters.</param>
/// <param name="Label">Label shown on the edge, usually the choice text.</param>
/// <param name="Style">How the edge is drawn.</param>
public sealed record FlowchartEdge(
    string Id,
    string From,
    string To,
    string? Label = null,
    FlowchartEdgeStyle? Style = null);

/// <summary>
/// A route: a named group of nodes (spec §6.1, 「路线」row).
/// </summary>
/// <remarks>
/// <see cref="AchievementId"/> is the coupling the specification calls out explicitly
/// (spec §6.2: 「与流程图的耦合：路线对象携带成就标识 → 走完一条路线 = 解锁对应成就」). It is
/// carried here rather than looked up, so that neither feature has to know the other's storage.
/// </remarks>
public sealed record FlowchartRoute
{
    /// <summary>Identifier of the route.</summary>
    public required string Id { get; init; }

    /// <summary>Route name as the player knows it.</summary>
    public required string Name { get; init; }

    /// <summary>Colour the flowchart asks the route to be drawn in.</summary>
    public string? Color { get; init; }

    /// <summary>Identifiers of the nodes that make up the route.</summary>
    public IReadOnlyList<string> NodeIds { get; init; } = Array.Empty<string>();

    /// <summary>Achievement unlocked by finishing this route, when the route declares one.</summary>
    public string? AchievementId { get; init; }
}

/// <summary>
/// Summary numbers of a flowchart (spec §6.1, 「汇总元数据」row).
/// </summary>
/// <remarks>
/// <see cref="Version"/> and <see cref="LastUpdated"/> are not decoration: the specification states
/// that a flowchart is evolving community-maintained data rather than a static resource, so a
/// client that cannot tell which revision it is showing cannot report a problem against it either.
/// </remarks>
public sealed record FlowchartMetadata
{
    /// <summary>Number of routes in the flowchart.</summary>
    public int TotalRoutes { get; init; }

    /// <summary>Number of endings in the flowchart.</summary>
    public int TotalEndings { get; init; }

    /// <summary>Estimated time to finish the game, as the data states it (the example is <c>20h</c>).</summary>
    public string? EstimatedCompletionTime { get; init; }

    /// <summary>Revision of the flowchart data.</summary>
    public string? Version { get; init; }

    /// <summary>When the flowchart data was last updated.</summary>
    public DateTimeOffset? LastUpdated { get; init; }
}

/// <summary>
/// A whole game's flowchart: nodes, edges and route groups (spec §6.1).
/// </summary>
public sealed record GameFlowchart
{
    /// <summary>Cross-system identifier of the game (a Bangumi subject id, e.g. <c>bgm-12345</c>).</summary>
    public required string GameId { get; init; }

    /// <summary>Revision of this flowchart.</summary>
    public string? Version { get; init; }

    /// <summary>When this flowchart was last updated.</summary>
    public DateTimeOffset? LastUpdated { get; init; }

    /// <summary>Summary numbers, when the data carries them.</summary>
    public FlowchartMetadata? Metadata { get; init; }

    /// <summary>Every node.</summary>
    public IReadOnlyList<FlowchartNode> Nodes { get; init; } = Array.Empty<FlowchartNode>();

    /// <summary>Every edge.</summary>
    public IReadOnlyList<FlowchartEdge> Edges { get; init; } = Array.Empty<FlowchartEdge>();

    /// <summary>Every route group.</summary>
    public IReadOnlyList<FlowchartRoute> Routes { get; init; } = Array.Empty<FlowchartRoute>();

    /// <summary>Finds a node by id, or null when the flowchart has no such node.</summary>
    /// <param name="nodeId">Node identifier.</param>
    public FlowchartNode? FindNode(string? nodeId) =>
        string.IsNullOrWhiteSpace(nodeId)
            ? null
            : Nodes.FirstOrDefault(node => string.Equals(node.Id, nodeId, StringComparison.Ordinal));

    /// <summary>Every edge leaving a node, in declaration order.</summary>
    /// <param name="nodeId">Node identifier.</param>
    public IReadOnlyList<FlowchartEdge> EdgesFrom(string? nodeId) =>
        string.IsNullOrWhiteSpace(nodeId)
            ? Array.Empty<FlowchartEdge>()
            : Edges.Where(edge => string.Equals(edge.From, nodeId, StringComparison.Ordinal)).ToList();

    /// <summary>The route a node belongs to, or null when no route claims it.</summary>
    /// <param name="nodeId">Node identifier.</param>
    public FlowchartRoute? RouteOf(string? nodeId) =>
        string.IsNullOrWhiteSpace(nodeId)
            ? null
            : Routes.FirstOrDefault(route => route.NodeIds.Contains(nodeId, StringComparer.Ordinal));
}

/// <summary>
/// A flowchart query, matching the published query dimensions (spec §6.1: 「查询维度」).
/// </summary>
/// <param name="GameId">Cross-system identifier of the game.</param>
/// <param name="IncludeHidden">Whether hidden routes are included. False by default, as documented.</param>
/// <param name="Language">Language of the labels. <c>zh-CN</c> by default, as documented.</param>
public sealed record FlowchartQuery(string GameId, bool IncludeHidden = false, string Language = "zh-CN")
{
    /// <summary>The documented default query for a game.</summary>
    /// <param name="gameId">Cross-system identifier of the game.</param>
    public static FlowchartQuery For(string gameId) => new(gameId);
}

/// <summary>
/// One story position taken from a save, in the neutral shape the flowchart sync consumes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this type exists instead of reusing the stored entity.</b> Galbox already knows where the
/// player is: a scan turns each save into a <c>SaveNode</c> row carrying the scene label, the route,
/// the chapter, the unlocked CG and the play time. The flowchart sync is documented as the point
/// where that knowledge meets the graph (spec §6.1: <c>POST /api/v1/flowcharts/{game_id}/sync</c> is
/// 「把存档位置同步到流程图节点 —— 这是『存档节点标记』与『流程图』的咬合点」). Making the remote
/// model take the EF entity directly would drag the database schema into a wire contract that will
/// be implemented by a server and, eventually, by other clients; this record is the small agreed
/// vocabulary in between, and <c>Galbox.Services.Community.SaveNodePositionProjector</c> is the one
/// place that translates.
/// </para>
/// <para>
/// <b>No fabricated fields.</b> Every value here is read from the stored node; nothing is guessed.
/// A null means "the stored node does not know", which the sync must be able to keep apart from
/// "the node knows and the value is empty" - a flowchart cannot honestly place a save it cannot
/// identify, and pretending otherwise would put the player at the wrong node.
/// </para>
/// </remarks>
public sealed record StoryPositionSample
{
    /// <summary>Identifier of the game in the local library.</summary>
    public required int GameInfoId { get; init; }

    /// <summary>Row id of the save node this sample came from.</summary>
    public int? SaveNodeId { get; init; }

    /// <summary>Save slot name or key, as stored.</summary>
    public string? SlotName { get; init; }

    /// <summary>
    /// Story position identifier — the value a flowchart node is matched on: for Ren'Py the
    /// <c>scene_label</c> the save was taken at.
    /// </summary>
    public string? SceneLabel { get; init; }

    /// <summary>Route the save was taken on, when the node knows one.</summary>
    public string? RouteName { get; init; }

    /// <summary>Chapter the save was taken in, when the node knows one.</summary>
    public string? ChapterName { get; init; }

    /// <summary>Chapter progress in percent, or null when the engine exposes no such figure.</summary>
    public int? ChapterProgressPercent { get; init; }

    /// <summary>
    /// Identifiers of the CG unlocked at this save — the "已解锁 CG 列表" the achievement report is
    /// required to carry (spec §6.2).
    /// </summary>
    public IReadOnlyList<string> CgUnlockedIds { get; init; } = Array.Empty<string>();

    /// <summary>Play time recorded inside the save itself, in seconds.</summary>
    public long PlayTimeSeconds { get; init; }

    /// <summary>When the save was written, in UTC. Null when the node carries no timestamp.</summary>
    public DateTime? SavedAtUtc { get; init; }
}

/// <summary>
/// Where one story position was placed on the flowchart.
/// </summary>
/// <remarks>
/// <see cref="Confidence"/> and <see cref="Reason"/> exist because placing a save on a graph is an
/// inference, not a lookup: a scene label may match a node exactly, match a route by prefix, or
/// match nothing. The product's rule from the save-node work applies unchanged here — a guess must
/// be shown as a guess — so the sync has to report how sure it is instead of asserting a match.
/// </remarks>
/// <param name="Position">The sample that was placed.</param>
/// <param name="NodeId">Identifier of the node it was placed on, or null when it could not be placed.</param>
/// <param name="Confidence">How sure the placement is, 0 to 1.</param>
/// <param name="Reason">Why it was placed there, in the product's own language.</param>
public sealed record FlowchartPositionMatch(
    StoryPositionSample Position,
    string? NodeId,
    double Confidence,
    string? Reason = null);

/// <summary>
/// What the client sends to synchronise its saves with a flowchart.
/// </summary>
/// <param name="GameId">Cross-system identifier of the game.</param>
/// <param name="Positions">The story positions to place, in story order.</param>
public sealed record FlowchartSyncRequest(string GameId, IReadOnlyList<StoryPositionSample> Positions);

/// <summary>
/// The answer to a flowchart structure request.
/// </summary>
/// <remarks>
/// The result envelope is not optional. The specification's error codes make
/// <c>FLOWCHART_NOT_FOUND</c> (the flowchart does not exist) and <c>GAME_NOT_SUPPORTED</c> (this
/// game will never have one) two different statements, and the product's own rule is that a
/// feature which cannot answer says why (spec §6.1: 「不存在时返回『游戏不支持流程图』」).
/// </remarks>
public sealed record FlowchartFetchResult
{
    /// <summary>True when the request completed and the answer is real.</summary>
    public required bool Success { get; init; }

    /// <summary>How the call ended.</summary>
    public CommunityServiceFailureKind FailureKind { get; init; } = CommunityServiceFailureKind.None;

    /// <summary>Explanation of the failure, in the product's own language. Null on success.</summary>
    public string? FailureReason { get; init; }

    /// <summary>The flowchart, or null when there is none to return.</summary>
    public GameFlowchart? Flowchart { get; init; }

    /// <summary>A successful result.</summary>
    /// <param name="flowchart">The flowchart that was fetched.</param>
    public static FlowchartFetchResult Ok(GameFlowchart flowchart) => new() { Success = true, Flowchart = flowchart };

    /// <summary>A failed result. <paramref name="reason"/> is required: the UI has to be able to explain.</summary>
    /// <param name="kind">How the call ended.</param>
    /// <param name="reason">Explanation shown to the user.</param>
    public static FlowchartFetchResult Failed(CommunityServiceFailureKind kind, string reason) =>
        new() { Success = false, FailureKind = kind, FailureReason = reason };
}

/// <summary>
/// The answer to a save-position synchronisation request.
/// </summary>
public sealed record FlowchartSyncResult
{
    /// <summary>True when the request completed and the answer is real.</summary>
    public required bool Success { get; init; }

    /// <summary>How the call ended.</summary>
    public CommunityServiceFailureKind FailureKind { get; init; } = CommunityServiceFailureKind.None;

    /// <summary>Explanation of the failure, in the product's own language. Null on success.</summary>
    public string? FailureReason { get; init; }

    /// <summary>Where each submitted position ended up.</summary>
    public IReadOnlyList<FlowchartPositionMatch> Matches { get; init; } = Array.Empty<FlowchartPositionMatch>();

    /// <summary>A successful result.</summary>
    /// <param name="matches">Where each position was placed.</param>
    public static FlowchartSyncResult Ok(IReadOnlyList<FlowchartPositionMatch> matches) =>
        new() { Success = true, Matches = matches };

    /// <summary>A failed result.</summary>
    /// <param name="kind">How the call ended.</param>
    /// <param name="reason">Explanation shown to the user.</param>
    public static FlowchartSyncResult Failed(CommunityServiceFailureKind kind, string reason) =>
        new() { Success = false, FailureKind = kind, FailureReason = reason };
}
