namespace Galbox.Core.Community.Achievements;

/// <summary>
/// The achievement service, as specified: definitions by game, progress reporting, share cards.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no implementation of this interface in the first version, and that is the
/// requirement.</b> Product source: <c>_product/Galbox-产品知识总纲.md</c> line 174 —
/// <c>| P2 | 社区成就系统 | 🔧 第一版只预留接口，前端隐藏 |</c>.
/// </para>
/// <para>
/// <b>Why nothing is implemented yet.</b> Three concrete gaps, all outside this code base:
/// </para>
/// <list type="number">
/// <item><description>
/// <b>There is no service.</b> The specifications put the achievement system on the same
/// self-hosted, authenticated backend as the patch service and the flowchart (spec §6.3), and the
/// judgement has to live there: the server has to be able to reject an invalid progress report and
/// to tell the client that an achievement is already unlocked, which means the server is the thing
/// that decides.
/// </description></item>
/// <item><description>
/// <b>Nobody maintains the achievement definitions.</b> The specification's own open question is
/// 「成就定义规范由谁维护？」(spec line 864). A client cannot answer that by inventing definitions.
/// </description></item>
/// <item><description>
/// <b>The two local judgement paths are not wired up.</b> The product owner chose them in person —
/// 存档解析推断 and 进程内存监控 (spec 判断 2). The save side has the raw material (the save-node
/// scan already recovers scene labels, CG lists and play time), but nothing feeds it into an
/// achievement judgement; the process-memory side has a documented, default-off setting
/// (「内存行为监控（用于成就追踪）」, spec §4.5 line 637) and nothing behind it.
/// </description></item>
/// </list>
/// <para>
/// <b>What is deliberately NOT here.</b> No stub and no local achievement catalogue. Inventing ten
/// achievements for a game would produce a screen that looks finished and means nothing, and it
/// would be indistinguishable from a working service — which is the failure mode the product owner
/// rejected when he refused manual declaration.
/// </para>
/// <para>
/// The acceptance run asserts the shape of this interface (A90) and that nothing in the shipping
/// assemblies implements it (A92).
/// </para>
/// </remarks>
public interface IAchievementProvider : IReservedFeatureService
{
    /// <summary>
    /// Fetches one game's achievements for this player.
    /// </summary>
    /// <remarks>
    /// Documented as <c>GET /api/v1/achievements/{game_id}</c>. Hidden achievements are fetched with
    /// their real text and masked by <see cref="AchievementView"/> before display: the service has
    /// to send them so that an unlock can render instantly, and the masking rule belongs to the
    /// presentation of the result, not to the request.
    /// </remarks>
    /// <param name="gameId">Cross-system identifier of the game (a Bangumi subject id).</param>
    /// <param name="includeHidden">Whether hidden achievements are included in the answer.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<AchievementFetchResult> GetAchievementsAsync(
        string gameId, bool includeHidden = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reports what the client observed, and receives back whatever that unlocked.
    /// </summary>
    /// <remarks>
    /// Documented as <c>POST /api/v1/achievements/{game_id}/unlock</c>. The four error cases the
    /// specification names must stay distinguishable in the answer: 未登录, 进度数据无效,
    /// 成就已解锁 and 上报过于频繁 (spec §6.2, 「错误语义」) — which is what
    /// <see cref="AchievementReportResult.FailureKind"/> is for. Note that
    /// <see cref="CommunityServiceFailureKind.AlreadyUnlocked"/> is an <i>outcome</i>, not a
    /// transport failure: the caller has to be able to tell it apart from a broken request.
    /// </remarks>
    /// <param name="request">The game, the observed CG list, the last unlock time and the evidence.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<AchievementReportResult> ReportProgressAsync(
        AchievementReportRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks the service for a share card.
    /// </summary>
    /// <remarks>
    /// Documented as <c>GET /api/v1/achievements/share/{id}</c> and listed as P3 — 后续版本
    /// (spec line 176). Null means the service has no card to give, which is an ordinary answer and
    /// not a failure.
    /// </remarks>
    /// <param name="achievementId">Identifier of the achievement.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<AchievementShareCard?> CreateShareCardAsync(
        string achievementId, CancellationToken cancellationToken = default);
}

/// <summary>
/// One of the two local paths that produce achievement evidence.
/// </summary>
/// <remarks>
/// <para>
/// The product owner fixed these two paths by name: 「成就系统的话，就以存档解析推断和进程内存监控
/// 两个为主」(spec 判断 2, line 67). They are split into an interface of their own — rather than
/// being rolled into <see cref="IAchievementProvider"/> — because they answer a different question.
/// The provider asks a server what achievements exist and what it thinks of a report; an evidence
/// source asks this machine what it observed. The first is useless without a backend; the second is
/// pure local reading, and keeping the two apart is what lets the local half be built and tested
/// before any server exists.
/// </para>
/// <para>
/// <b>No implementation ships in the first version.</b> The interface is reserved so that the local
/// half has an agreed shape on the day it is built, and so that the two paths cannot silently
/// converge into one: they differ in reliability, in cost and in whether the user has consented to
/// them.
/// </para>
/// </remarks>
public interface IAchievementEvidenceSource
{
    /// <summary>Which of the two local paths this source is.</summary>
    AchievementEvidenceKind Kind { get; }

    /// <summary>
    /// Whether this source may run right now.
    /// </summary>
    /// <remarks>
    /// The process-memory path is behind a user setting that is documented as default-off
    /// (「内存行为监控（用于成就追踪）」, spec §4.5 line 637), so an implementation of that kind must
    /// return false until the user turns it on. Reading another process's memory is not something
    /// to do because a feature would find it convenient.
    /// </remarks>
    bool IsEnabled { get; }

    /// <summary>Why it may not run, in the product's own language. Empty when <see cref="IsEnabled"/>.</summary>
    string DisabledReason { get; }

    /// <summary>
    /// Collects the evidence currently observable for one game.
    /// </summary>
    /// <remarks>
    /// An empty list means "this path observed nothing", which is a legitimate answer, and it must
    /// stay distinguishable from <see cref="IsEnabled"/> being false — the caller has to be able to
    /// say "we did not look" and "we looked and there was nothing" apart.
    /// </remarks>
    /// <param name="gameInfoId">Identifier of the game in the local library.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask<IReadOnlyList<AchievementEvidence>> CollectAsync(
        int gameInfoId, CancellationToken cancellationToken = default);
}
