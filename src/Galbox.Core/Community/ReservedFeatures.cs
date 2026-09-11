namespace Galbox.Core.Community;

/// <summary>
/// The two P2 features that the first version reserves instead of building.
/// </summary>
/// <remarks>
/// Product source: <c>_product/Galbox-产品知识总纲.md</c> lines 173-174 —
/// <c>| P2 | 流程图追踪 | 🔧 第一版只预留接口，前端隐藏 |</c> and
/// <c>| P2 | 社区成就系统 | 🔧 第一版只预留接口，前端隐藏 |</c>.
/// <para>
/// Both features are specified as <b>remote-service-first</b>: the flowchart is meant to become a
/// published API standard that the community contributes to (§6.1, 判断 3), and the achievement
/// system is meant to be an objective, server-judged record (§6.2). Neither can be built without a
/// backend that does not exist yet, which is exactly why the specification asks for an interface
/// layer and not for a feature.
/// </para>
/// </remarks>
public enum ReservedFeature
{
    /// <summary>流程图追踪 (spec §6.1).</summary>
    FlowchartTracking = 0,

    /// <summary>社区成就系统 (spec §6.2).</summary>
    CommunityAchievements = 1
}

/// <summary>
/// How far a <see cref="ReservedFeature"/> has actually got.
/// </summary>
/// <remarks>
/// One member today, and that is the point: an enum with a value the code could return would be an
/// invitation to claim progress that does not exist. When a feature is really built, the
/// implementation arrives with a new member here and the catalog has to be updated with it.
/// </remarks>
public enum ReservedFeatureState
{
    /// <summary>
    /// The data model and the interfaces are defined and documented; no behaviour is implemented and
    /// no user interface exposes the feature.
    /// </summary>
    InterfaceOnly = 0
}

/// <summary>
/// The honest status of one reserved feature: off, not implemented, and why.
/// </summary>
/// <remarks>
/// This type exists so that "the feature is off" is a fact with a reason attached, not the absence
/// of a fact. A switch that cannot say what it is waiting for is indistinguishable from a switch
/// that was forgotten, and the latter is how features end up half-present.
/// </remarks>
public sealed record ReservedFeatureInfo
{
    /// <summary>Which feature this describes.</summary>
    public required ReservedFeature Feature { get; init; }

    /// <summary>User-facing name, in the product's own language.</summary>
    public required string DisplayName { get; init; }

    /// <summary>How far the feature has actually got.</summary>
    public required ReservedFeatureState State { get; init; }

    /// <summary>
    /// Whether any code in the shipping product implements this feature.
    /// </summary>
    /// <remarks>False for every reserved feature in this version. It is a stored fact rather than a
    /// computed guess so that claiming otherwise requires editing this line on purpose.</remarks>
    public required bool IsImplemented { get; init; }

    /// <summary>Whether the feature is switched on for this build.</summary>
    public required bool IsEnabled { get; init; }

    /// <summary>Why the feature is not available, in the product's own language.</summary>
    public required string UnavailableReason { get; init; }

    /// <summary>
    /// What a future implementation still needs from a server, one capability per entry.
    /// </summary>
    /// <remarks>
    /// This is the answer to "还缺什么", kept next to the interface it constrains. It is deliberately
    /// a list of capabilities rather than of endpoints: an endpoint can be re-cut, but "the server
    /// has to own the rarity statistic" is a decision that shapes the whole feature.
    /// </remarks>
    public required IReadOnlyList<string> RequiredServerCapabilities { get; init; }

    /// <summary>Where the requirement comes from: the specification, by section and line.</summary>
    public required IReadOnlyList<string> SpecReferences { get; init; }
}

/// <summary>
/// The single authority on whether a reserved feature is available in this build.
/// </summary>
/// <remarks>
/// Registered in the application's container (<c>src/Galbox.App/App.xaml.cs</c>) so that the future
/// user interface has exactly one thing to ask. It is intentionally the <i>only</i> registration the
/// reserved layer adds: <c>IFlowchartProvider</c>, <c>IAchievementProvider</c> and
/// <c>IAchievementEvidenceSource</c> are deliberately not registered, because there is nothing to
/// register them with and a placeholder instance in the container is precisely the fake
/// availability this reservation is supposed to avoid.
/// </remarks>
public interface IReservedFeatureCatalog
{
    /// <summary>Every reserved feature this build knows about.</summary>
    IReadOnlyList<ReservedFeatureInfo> All { get; }

    /// <summary>Status of one feature.</summary>
    /// <param name="feature">Feature to look up.</param>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a known feature.</exception>
    ReservedFeatureInfo Get(ReservedFeature feature);

    /// <summary>Whether the feature is switched on. False for every reserved feature.</summary>
    /// <param name="feature">Feature to query.</param>
    bool IsEnabled(ReservedFeature feature);
}

/// <inheritdoc cref="IReservedFeatureCatalog" />
public sealed class ReservedFeatureCatalog : IReservedFeatureCatalog
{
    /// <summary>Specification the two reservations are cut from.</summary>
    private const string Spec = "_product/Galbox-产品知识总纲.md";

    /// <summary>流程图追踪 (spec §6.1).</summary>
    private static readonly ReservedFeatureInfo FlowchartTracking = new()
    {
        Feature = ReservedFeature.FlowchartTracking,
        DisplayName = "流程图追踪",
        State = ReservedFeatureState.InterfaceOnly,
        IsImplemented = false,
        IsEnabled = GalboxFeatureFlags.FlowchartTrackingEnabled,
        UnavailableReason =
            "本机没有流程图服务端：接口与数据模型已按产品共识定稿，但第一版不实现任何需要服务端的能力"
            + "（结构拉取、存档位置同步、社区共建数据的分发），前端因此不出现任何入口。",
        RequiredServerCapabilities = new[]
        {
            "按游戏条目标识（Bangumi 条目 ID，形如 bgm-12345）提供流程图结构的只读接口："
            + "GET /api/v1/flowcharts/{game_id}，支持是否包含隐藏路线与语言两个查询维度",
            "接收存档位置并回传所属节点的同步接口：POST /api/v1/flowcharts/{game_id}/sync —— "
            + "这是「存档节点标记」与「流程图」的咬合点，也是存档位置能落到图上的唯一途径",
            "版本化的图数据存储与分发：流程图是可演进的社区维护数据，必须带版本号与最后更新时间，"
            + "而不是一次写死的静态资源",
            "数据来源与审核流程：谁生产流程图（社区众包还是运营方）、以什么格式提交、谁来校对",
            "统一的认证（API Key / JWT）：所有自定义 API 均需认证，与补丁服务共用同一套后端",
            "按游戏判定「是否存在流程图」的能力，不存在时必须能明确回答「游戏不支持流程图」"
        },
        SpecReferences = new[]
        {
            $"{Spec} 第 173 行（P2 流程图追踪：第一版只预留接口，前端隐藏）",
            $"{Spec} 判断 3（第 69-72 行：流程图以远程 API 服务为主，要定下一套确定的 API 标准）",
            $"{Spec} §6.1（第 696-716 行：节点/连线/路线模型与两个接口共识）",
            $"{Spec} §6.3（第 736-741 行：以远程 API 服务为主、以 Bangumi 条目 ID 为主键、所有 API 需认证）",
            "docs/API_DOCUMENTATION.md「流程图 API」一节（响应模型与 FLOWCHART_NOT_FOUND / GAME_NOT_SUPPORTED 错误码）",
            "_product/Galbox-初衷复原.md 阶段二「流程图上云」"
        }
    };

    /// <summary>社区成就系统 (spec §6.2).</summary>
    private static readonly ReservedFeatureInfo CommunityAchievements = new()
    {
        Feature = ReservedFeature.CommunityAchievements,
        DisplayName = "社区成就系统",
        State = ReservedFeatureState.InterfaceOnly,
        IsImplemented = false,
        IsEnabled = GalboxFeatureFlags.CommunityAchievementsEnabled,
        UnavailableReason =
            "本机没有成就服务端：接口与数据模型已按产品共识定稿，但第一版不实现上报、解锁通知与分享卡片；"
            + "本地两条判定路径（存档解析推断、进程内存监控）也还没有接上，前端因此不出现任何入口。",
        RequiredServerCapabilities = new[]
        {
            "按游戏条目标识提供成就定义的只读接口：GET /api/v1/achievements/{game_id}，"
            + "含图标与未解锁图标两套资源地址",
            "接收进度与元数据上报并回传解锁结果的接口：POST /api/v1/achievements/{game_id}/unlock，"
            + "回传体需携带「成就解锁！」通知所需的成就名、图标与点数",
            "账号体系与跨设备进度存储：未登录、进度数据无效、成就已解锁、上报过于频繁四种情况"
            + "必须能被服务端区分并回传，客户端才能给出准确的说明",
            "服务端侧客观判定：成就以存档解析推断与进程内存监控为判定依据，"
            + "产品明确不接受玩家手动申报，因此判定与稀有度统计必须在服务端完成",
            "分享卡片生成接口：GET /api/v1/achievements/share/{id}（P3「成就分享卡片」，随成就服务一并规划）",
            "统一的认证（API Key / JWT）：与补丁服务、流程图共用同一套后端"
        },
        SpecReferences = new[]
        {
            $"{Spec} 第 174 行（P2 社区成就系统：第一版只预留接口，前端隐藏）",
            $"{Spec} 判断 2（第 62-67 行：不让玩家自己手动申报，以存档解析推断和进程内存监控两个为主）",
            $"{Spec} §6.2（第 718-734 行：五种类型、单条成就属性、隐藏成就的社交保护、汇总、上报机制与四条错误语义）",
            $"{Spec} §6.3（第 736-741 行：以远程 API 服务为主、以 Bangumi 条目 ID 为主键、所有 API 需认证）",
            $"{Spec} §4.5 第 637 行（设置项「内存行为监控（用于成就追踪）」默认关闭，是这个功能的伏笔）",
            "docs/API_DOCUMENTATION.md「成就 API」一节（成就类型说明与 ACHIEVEMENT_* 错误码）"
        }
    };

    /// <inheritdoc />
    public IReadOnlyList<ReservedFeatureInfo> All { get; } = new[] { FlowchartTracking, CommunityAchievements };

    /// <inheritdoc />
    public ReservedFeatureInfo Get(ReservedFeature feature) => feature switch
    {
        ReservedFeature.FlowchartTracking => FlowchartTracking,
        ReservedFeature.CommunityAchievements => CommunityAchievements,
        _ => throw new ArgumentOutOfRangeException(
            nameof(feature), feature, "Unknown reserved feature; add it to ReservedFeatureCatalog first.")
    };

    /// <inheritdoc />
    public bool IsEnabled(ReservedFeature feature) => GalboxFeatureFlags.IsEnabled(feature);
}

/// <summary>
/// The switches of the two reserved features.
/// </summary>
/// <remarks>
/// <para>
/// Both are <c>const false</c> on purpose, and the const-ness is part of the requirement rather than
/// an accident. "第一版只预留接口，前端隐藏" means the feature cannot become visible through a
/// settings row, a database column, an environment variable or a command-line flag - only through a
/// code change that a reviewer sees in a diff. A run-time toggle would make it possible to ship the
/// feature by accident, and this project has already paid for that class of mistake.
/// </para>
/// <para>
/// Nothing reads these switches today; the catalog above is the only reader. That is deliberate:
/// the first thing a future implementation has to do is consult the switch, and the acceptance run
/// asserts that while it is false nothing reachable from the UI mentions the feature at all.
/// </para>
/// </remarks>
public static class GalboxFeatureFlags
{
    /// <summary>流程图追踪 (spec §6.1). Off; not implemented; no entry point.</summary>
    public const bool FlowchartTrackingEnabled = false;

    /// <summary>社区成就系统 (spec §6.2). Off; not implemented; no entry point.</summary>
    public const bool CommunityAchievementsEnabled = false;

    /// <summary>Whether the given reserved feature is switched on for this build.</summary>
    /// <param name="feature">Feature to query.</param>
    /// <returns>Always <c>false</c> in the first version.</returns>
    public static bool IsEnabled(ReservedFeature feature) => feature switch
    {
        ReservedFeature.FlowchartTracking => FlowchartTrackingEnabled,
        ReservedFeature.CommunityAchievements => CommunityAchievementsEnabled,
        _ => false
    };
}

/// <summary>
/// How a call to a community service can end.
/// </summary>
/// <remarks>
/// The vocabulary is shared by the two reserved features because they share one backend (spec §6.3)
/// and because the failure they must never repeat is the one this project keeps rediscovering:
/// collapsing "not configured", "server unreachable" and "the server answered, there is genuinely
/// nothing" into one empty result. The metadata sources already had to be taught that distinction
/// (<c>MetadataSourceFailureKind</c>); the reserved interfaces start with it.
/// <para>
/// The last four members are the error semantics the specification names by hand: 未登录 /
/// 进度数据无效 / 成就已解锁 / 上报过于频繁 (spec §6.2).
/// </para>
/// </remarks>
public enum CommunityServiceFailureKind
{
    /// <summary>No failure. The answer is real and may legitimately be an empty one.</summary>
    None = 0,

    /// <summary>The service is not usable as configured (no endpoint, no credential).</summary>
    NotConfigured,

    /// <summary>The caller passed something the service cannot express (blank or malformed id).</summary>
    InvalidRequest,

    /// <summary>The caller is not signed in, or the credential was rejected (未登录).</summary>
    Unauthorized,

    /// <summary>The transport refused the call because it was made too often (HTTP 429).</summary>
    /// <remarks>
    /// Kept apart from <see cref="TooFrequent"/> on purpose. This one is the transport's answer, and
    /// a client that treats it as a permanent failure stops reporting altogether; the other is the
    /// service's answer about the <i>content</i> of a report, and the fix is to send less, not later.
    /// </remarks>
    RateLimited,

    /// <summary>The server refused the report because it was submitted too often (上报过于频繁).</summary>
    TooFrequent,

    /// <summary>The request never reached a server (DNS, connect, TLS, timeout).</summary>
    NetworkError,

    /// <summary>The server answered with a status the client does not accept.</summary>
    HttpError,

    /// <summary>The server answered, but not in the shape the client expects.</summary>
    MalformedResponse,

    /// <summary>The server answered with its own application-level error.</summary>
    UpstreamError,

    /// <summary>The server answered: there is no such record (FLOWCHART_NOT_FOUND / ACHIEVEMENT_NOT_FOUND).</summary>
    NotFound,

    /// <summary>The server answered: this game has no flowchart and is not going to have one (GAME_NOT_SUPPORTED).</summary>
    GameNotSupported,

    /// <summary>The reported progress is not usable (进度数据无效).</summary>
    InvalidProgress,

    /// <summary>The achievement is already unlocked (成就已解锁).</summary>
    AlreadyUnlocked
}

/// <summary>
/// What every implementation of a reserved-feature service must be able to say about itself.
/// </summary>
/// <remarks>
/// The availability contract is inherited by <c>IFlowchartProvider</c> and
/// <c>IAchievementProvider</c> so that "I cannot answer, and here is why" is part of the interface
/// rather than a convention. An implementation that cannot answer must say so before it is called,
/// because a feature that silently returns nothing is indistinguishable from a feature that found
/// nothing, and the two must never be confused in a product whose whole promise is honesty about
/// what it knows.
/// </remarks>
public interface IReservedFeatureService
{
    /// <summary>Which reserved feature this service belongs to.</summary>
    ReservedFeature Feature { get; }

    /// <summary>Whether this instance can answer at all right now.</summary>
    bool IsAvailable { get; }

    /// <summary>Why it cannot answer, in the product's own language. Empty when <see cref="IsAvailable"/>.</summary>
    string UnavailableReason { get; }
}
