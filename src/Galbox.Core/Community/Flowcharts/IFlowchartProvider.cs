namespace Galbox.Core.Community.Flowcharts;

/// <summary>
/// The flowchart service, as specified: structure by game, and save-position synchronisation.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no implementation of this interface in the first version, and that is the
/// requirement.</b> Product source: <c>_product/Galbox-产品知识总纲.md</c> line 173 —
/// <c>| P2 | 流程图追踪 | 🔧 第一版只预留接口，前端隐藏 |</c>. The two documented endpoints are
/// 「以远程 API 服务为主」 (spec §6.3, 判断 3): the flowchart is meant to become a published
/// standard that the community maintains, so the client cannot own the data and no local
/// approximation of it would be honest.
/// </para>
/// <para>
/// <b>Why nothing is implemented yet.</b> Two concrete gaps, both outside this code base:
/// </para>
/// <list type="number">
/// <item><description>
/// <b>There is no service.</b> The specification names a self-hosted backend shared with the patch
/// service and the achievement system, authenticated with an API key or JWT (spec §6.3). It does
/// not exist, so a client would have nothing to talk to. See
/// <see cref="IReservedFeatureCatalog"/>.<c>RequiredServerCapabilities</c> for the full list of what
/// the server has to provide before this interface can be implemented.
/// </description></item>
/// <item><description>
/// <b>The data does not exist either.</b> A flowchart is community-maintained, versioned data
/// (spec §6.1: 「流程图是可演进的社区维护数据（带版本号），不是一次写死的静态资源」). Even a
/// perfect client would need somebody to produce the graphs.
/// </description></item>
/// </list>
/// <para>
/// <b>What is deliberately NOT here.</b> No stub, no default instance, no local fallback that
/// approximates a graph from the save data. A local approximation would be worse than nothing: it
/// would look like a flowchart, be wrong about the branches the player has not taken, and be
/// indistinguishable from the real thing in the UI. When this interface is implemented, the
/// implementation has to be able to say <see cref="IReservedFeatureService.IsAvailable"/> = false
/// and name the reason, which is why that contract is inherited rather than restated.
/// </para>
/// <para>
/// The acceptance run asserts the shape of this interface (A90) and that nothing in the shipping
/// assemblies implements it (A92). The day an implementation lands, A92 fails and points at itself,
/// which is the intended prompt to update both the check and this comment.
/// </para>
/// </remarks>
public interface IFlowchartProvider : IReservedFeatureService
{
    /// <summary>
    /// Fetches one game's flowchart.
    /// </summary>
    /// <remarks>
    /// Documented as <c>GET /api/v1/flowcharts/{game_id}</c> with the query dimensions of
    /// <see cref="FlowchartQuery"/>. A game without a flowchart must come back as
    /// <see cref="CommunityServiceFailureKind.NotFound"/> or
    /// <see cref="CommunityServiceFailureKind.GameNotSupported"/> and never as an empty success:
    /// 「不存在时返回『游戏不支持流程图』」 (spec §6.1).
    /// </remarks>
    /// <param name="query">Which game, whether hidden routes are included, and in which language.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<FlowchartFetchResult> GetFlowchartAsync(
        FlowchartQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reports where the player currently is, so a server can map save positions onto graph nodes.
    /// </summary>
    /// <remarks>
    /// Documented as <c>POST /api/v1/flowcharts/{game_id}/sync</c>. This is the joint between the
    /// two halves of the product — 存档节点标记 and 流程图 — and the specification calls it out as
    /// the clever part of the design (spec §6.1).
    /// <para>
    /// A position that cannot be placed must come back with a null
    /// <see cref="FlowchartPositionMatch.NodeId"/> rather than a low-confidence guess dressed up as
    /// a placement, and the local sample it was built from must never be mutated by the server's
    /// answer.
    /// </para>
    /// </remarks>
    /// <param name="request">The game and the story positions to place.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<FlowchartSyncResult> SyncSavePositionsAsync(
        FlowchartSyncRequest request, CancellationToken cancellationToken = default);
}
