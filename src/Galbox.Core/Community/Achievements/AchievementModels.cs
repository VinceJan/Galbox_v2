namespace Galbox.Core.Community.Achievements;

/// <summary>
/// The five achievement types (spec §6.2: story / normal / hidden / rare / community).
/// </summary>
/// <remarks>
/// The published API carries these as the lower-case tokens of the same names
/// (<c>docs/API_DOCUMENTATION.md</c>, 「成就类型说明」). As with the flowchart enums, the serializer
/// policy is left to the transport layer that does not exist yet.
/// </remarks>
public enum AchievementType
{
    /// <summary>剧情成就 — earned by reaching a point in the story.</summary>
    Story = 0,

    /// <summary>普通成就 — an ordinary achievement.</summary>
    Normal = 1,

    /// <summary>隐藏成就 — its name and description stay hidden until it is unlocked.</summary>
    Hidden = 2,

    /// <summary>稀有成就 — earned by few players.</summary>
    Rare = 3,

    /// <summary>社区成就 — a community-defined achievement.</summary>
    Community = 4
}

/// <summary>
/// How an achievement was judged to be earned.
/// </summary>
/// <remarks>
/// <para>
/// The product owner chose these two paths in person and rejected the obvious third:
/// 「成就系统的话还是不让玩家自己手动申报，这样的话没有什么意义了」and「成就系统的话，就以存档
/// 解析推断和进程内存监控两个为主」(spec 判断 2, lines 62-67).
/// </para>
/// <para>
/// <b>There is deliberately no manual-declaration member.</b> A self-reported achievement is worth
/// nothing, and leaving the value out of the vocabulary is the cheapest way to make it
/// inexpressible: a future implementer who wants to add a "player says so" path has to add a member
/// here, in a type whose documentation says the product owner refused exactly that.
/// </para>
/// </remarks>
public enum AchievementEvidenceKind
{
    /// <summary>Not known — evidence that predates the distinction, or a judgement made from neither path.</summary>
    Unknown = 0,

    /// <summary>存档解析推断 — inferred from the save files and the engine's own flags.</summary>
    SaveAnalysis = 1,

    /// <summary>
    /// 进程内存监控 — inferred from the running game. Off by default: the corresponding setting is
    /// 「内存行为监控（用于成就追踪）」, documented as default-off (spec §4.5, line 637).
    /// </summary>
    ProcessMemory = 2
}

/// <summary>
/// One achievement, as defined by the service (spec §6.2, 「单条成就属性」).
/// </summary>
/// <remarks>
/// <see cref="IconUrl"/> and <see cref="LockedIconUrl"/> are two separate resources because the
/// specification asks for 「图标 + 独立未解锁图标」; a client that only downloads the unlocked icon
/// cannot render a locked achievement correctly.
/// <para>
/// <see cref="Rarity"/> is a server-side statistic (it is the share of players who have it), so it
/// is data the client receives and never computes. <see cref="ProgressTotal"/> is nullable because
/// the specification allows a progress figure to be absent (「进度（当前/总数/百分比，可为空）」):
/// some achievements are boolean and have no meaningful denominator.
/// </para>
/// </remarks>
public sealed record AchievementDefinition
{
    /// <summary>Identifier of the achievement.</summary>
    public required string Id { get; init; }

    /// <summary>Cross-system identifier of the game (a Bangumi subject id, e.g. <c>bgm-12345</c>).</summary>
    public required string GameId { get; init; }

    /// <summary>Name shown to the player. Never shown for a locked <see cref="AchievementType.Hidden"/> one.</summary>
    public required string Name { get; init; }

    /// <summary>Description shown to the player. Never shown for a locked hidden one.</summary>
    public string? Description { get; init; }

    /// <summary>Icon shown once the achievement is unlocked.</summary>
    public string? IconUrl { get; init; }

    /// <summary>Icon shown while the achievement is still locked.</summary>
    public string? LockedIconUrl { get; init; }

    /// <summary>Which of the five types this is.</summary>
    public required AchievementType Type { get; init; }

    /// <summary>Points the achievement is worth.</summary>
    public int Points { get; init; }

    /// <summary>
    /// Whether the achievement is hidden. True for <see cref="AchievementType.Hidden"/>; stored
    /// separately as well, because the published model carries both and a type value alone would
    /// make the two sources of truth disagree silently.
    /// </summary>
    public bool IsHidden { get; init; }

    /// <summary>
    /// Share of players who have unlocked it, 0 to 1 (the documented examples are 0.15 and 0.01).
    /// Null when the service has no statistic yet.
    /// </summary>
    public double? Rarity { get; init; }

    /// <summary>Total for a progress-bearing achievement, or null when it has no denominator.</summary>
    public int? ProgressTotal { get; init; }
}

/// <summary>
/// Progress towards one achievement (spec §6.2: 当前/总数/百分比).
/// </summary>
/// <param name="Current">How far the player has got.</param>
/// <param name="Total">How far there is to go.</param>
public sealed record AchievementProgress(int Current, int Total)
{
    /// <summary>Progress as a 0-100 percentage; 0 when the total is unknown.</summary>
    public double Percent => Total <= 0 ? 0 : Math.Clamp(Current * 100.0 / Total, 0, 100);
}

/// <summary>
/// One achievement as the user interface is allowed to show it.
/// </summary>
/// <remarks>
/// <para>
/// This type carries the specification's 社交保护 rule (spec §6.2, 「社交保护」): a hidden
/// achievement that is not unlocked yet must render its name and its description as <c>???</c>.
/// The masking lives here, on the model, rather than in a view, because it is a rule about what may
/// be disclosed and not a formatting choice — putting it in a view would mean every future view has
/// to remember it, and the first one that forgets leaks the name of a hidden achievement.
/// </para>
/// <para>
/// Masking happens on the way to the screen and never on the way in: the underlying
/// <see cref="Definition"/> still holds the real text, so an unlocked achievement shows it
/// immediately without a second round trip. Note that this also means the real text is present in
/// memory, which is correct — the service already sent it, and hiding it client-side only stops the
/// UI from spoiling the player.
/// </para>
/// </remarks>
public sealed record AchievementView
{
    /// <summary>What the service says the achievement is.</summary>
    public required AchievementDefinition Definition { get; init; }

    /// <summary>Whether this player has it.</summary>
    public bool IsUnlocked { get; init; }

    /// <summary>When it was unlocked, or null when it is still locked.</summary>
    public DateTimeOffset? UnlockedAt { get; init; }

    /// <summary>Progress towards it, or null when it has none.</summary>
    public AchievementProgress? Progress { get; init; }

    /// <summary>Text that masks a hidden, still-locked achievement (spec §6.2).</summary>
    public const string HiddenPlaceholder = "???";

    /// <summary>Whether the name and description must be masked.</summary>
    public bool IsMasked => Definition.IsHidden && !IsUnlocked;

    /// <summary>Name to display: the real one, or <c>???</c> while a hidden achievement is locked.</summary>
    public string DisplayName => IsMasked ? HiddenPlaceholder : Definition.Name;

    /// <summary>
    /// Description to display: the real one, or <c>???</c> while a hidden achievement is locked.
    /// Empty when the service sent no description for an achievement that may be shown.
    /// </summary>
    public string DisplayDescription => IsMasked ? HiddenPlaceholder : Definition.Description ?? string.Empty;

    /// <summary>Wraps a definition with no unlock and no progress — the state of a freshly fetched list.</summary>
    /// <param name="definition">The definition to wrap.</param>
    public static AchievementView Locked(AchievementDefinition definition) => new() { Definition = definition };
}

/// <summary>
/// User-level summary of a game's achievements (spec §6.2, 「汇总」).
/// </summary>
/// <remarks>
/// Deliberately counts and points rather than a bare percentage: the product's standing rule is that
/// a progress figure has to be able to answer "what is still missing", which a percentage cannot.
/// </remarks>
public sealed record AchievementSummary
{
    /// <summary>How many achievements the game defines.</summary>
    public int TotalCount { get; init; }

    /// <summary>How many of them this player has.</summary>
    public int UnlockedCount { get; init; }

    /// <summary>Points available in total.</summary>
    public int TotalPoints { get; init; }

    /// <summary>Points this player has earned.</summary>
    public int UnlockedPoints { get; init; }

    /// <summary>Completion in percent, 0 to 100; 0 when the game defines nothing.</summary>
    public double CompletionPercent =>
        TotalCount <= 0 ? 0 : Math.Clamp(UnlockedCount * 100.0 / TotalCount, 0, 100);

    /// <summary>How many achievements are still locked.</summary>
    public int RemainingCount => Math.Max(0, TotalCount - UnlockedCount);

    /// <summary>Builds the summary of a set of achievement views.</summary>
    /// <param name="achievements">The achievements to summarise.</param>
    public static AchievementSummary From(IReadOnlyList<AchievementView> achievements)
    {
        ArgumentNullException.ThrowIfNull(achievements);

        return new AchievementSummary
        {
            TotalCount = achievements.Count,
            UnlockedCount = achievements.Count(view => view.IsUnlocked),
            TotalPoints = achievements.Sum(view => view.Definition.Points),
            UnlockedPoints = achievements.Where(view => view.IsUnlocked).Sum(view => view.Definition.Points)
        };
    }
}

/// <summary>
/// The evidence one achievement judgement was made from (spec §6.2, 「判定依据」).
/// </summary>
/// <remarks>
/// The specification's report carries 「已解锁 CG 列表」and 「最后解锁时间」; this record is that,
/// plus the two things needed to make a judgement auditable: which of the two local paths produced
/// it, and which scene labels were observed at the time. An achievement whose evidence cannot be
/// shown is an achievement the player has to take on faith, and the product's stated position is
/// that achievements are objective and verifiable.
/// </remarks>
public sealed record AchievementEvidence
{
    /// <summary>Which local path produced this evidence.</summary>
    public required AchievementEvidenceKind Kind { get; init; }

    /// <summary>Identifier of the game in the local library.</summary>
    public required int GameInfoId { get; init; }

    /// <summary>When the evidence was observed, in UTC.</summary>
    public DateTimeOffset ObservedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Story labels observed at the time, when the evidence came from a save.</summary>
    public IReadOnlyList<string> SceneLabels { get; init; } = Array.Empty<string>();

    /// <summary>CG identifiers observed at the time, when the evidence came from a save.</summary>
    public IReadOnlyList<string> CgIds { get; init; } = Array.Empty<string>();

    /// <summary>Free-text note for the report, e.g. which memory region was watched.</summary>
    public string? Note { get; init; }
}

/// <summary>
/// What the client reports, and what it must leave to the server (spec §6.2, 「上报机制」).
/// </summary>
/// <remarks>
/// <b>The client sends evidence, not verdicts.</b> There is no "I unlocked achievement X" field,
/// for the reason the product owner gave when the manual-declaration design was proposed: an
/// achievement the player asserts about themselves is worth nothing (spec 判断 2). The client
/// reports what it observed; whether that adds up to an achievement is the server's call, which is
/// why the answer comes back as notifications rather than as an acknowledgement.
/// </remarks>
public sealed record AchievementReportRequest
{
    /// <summary>Cross-system identifier of the game.</summary>
    public required string GameId { get; init; }

    /// <summary>The unlocked CG identifiers, as the specification requires the report to carry.</summary>
    public IReadOnlyList<string> UnlockedCgIds { get; init; } = Array.Empty<string>();

    /// <summary>When the last achievement was unlocked locally, as the specification requires.</summary>
    public DateTimeOffset? LastUnlockedAtUtc { get; init; }

    /// <summary>The evidence behind the report.</summary>
    public IReadOnlyList<AchievementEvidence> Evidence { get; init; } = Array.Empty<AchievementEvidence>();
}

/// <summary>
/// The unlock notification the server sends back (spec §6.2, 「上报机制」).
/// </summary>
/// <remarks>
/// The specification fixes the title at 「成就解锁！」 and the body at the achievement's name, icon
/// and points, so the shape is pinned here rather than left to a view. <see cref="Title"/> is a
/// property with the documented default instead of a constant so that a localised build can replace
/// it, and so that a test can assert on the string the specification actually names.
/// </remarks>
public sealed record AchievementUnlockNotification
{
    /// <summary>Title of the notification, as the specification names it.</summary>
    public const string DefaultTitle = "成就解锁！";

    /// <summary>Identifier of the achievement that was unlocked.</summary>
    public required string AchievementId { get; init; }

    /// <summary>Name of the achievement.</summary>
    public required string AchievementName { get; init; }

    /// <summary>Icon to show with the notification.</summary>
    public string? IconUrl { get; init; }

    /// <summary>Points the achievement is worth.</summary>
    public int Points { get; init; }

    /// <summary>Title to display.</summary>
    public string Title { get; init; } = DefaultTitle;
}

/// <summary>
/// The answer to an achievement list request.
/// </summary>
public sealed record AchievementFetchResult
{
    /// <summary>True when the request completed and the answer is real.</summary>
    public required bool Success { get; init; }

    /// <summary>How the call ended.</summary>
    public CommunityServiceFailureKind FailureKind { get; init; } = CommunityServiceFailureKind.None;

    /// <summary>Explanation of the failure, in the product's own language. Null on success.</summary>
    public string? FailureReason { get; init; }

    /// <summary>The achievements, already masked where they have to be.</summary>
    public IReadOnlyList<AchievementView> Achievements { get; init; } = Array.Empty<AchievementView>();

    /// <summary>User-level summary, computed by the service or from <see cref="Achievements"/>.</summary>
    public AchievementSummary? Summary { get; init; }

    /// <summary>A successful result.</summary>
    /// <param name="achievements">The achievements that came back.</param>
    public static AchievementFetchResult Ok(IReadOnlyList<AchievementView> achievements) =>
        new() { Success = true, Achievements = achievements, Summary = AchievementSummary.From(achievements) };

    /// <summary>A failed result.</summary>
    /// <param name="kind">How the call ended.</param>
    /// <param name="reason">Explanation shown to the user.</param>
    public static AchievementFetchResult Failed(CommunityServiceFailureKind kind, string reason) =>
        new() { Success = false, FailureKind = kind, FailureReason = reason };
}

/// <summary>
/// The answer to a progress report.
/// </summary>
public sealed record AchievementReportResult
{
    /// <summary>True when the report was accepted. Zero notifications is a normal, successful outcome.</summary>
    public required bool Success { get; init; }

    /// <summary>How the call ended (未登录 / 进度数据无效 / 成就已解锁 / 上报过于频繁 are all here).</summary>
    public CommunityServiceFailureKind FailureKind { get; init; } = CommunityServiceFailureKind.None;

    /// <summary>Explanation of the failure, in the product's own language. Null on success.</summary>
    public string? FailureReason { get; init; }

    /// <summary>Achievements the server decided were unlocked by this report.</summary>
    public IReadOnlyList<AchievementUnlockNotification> Notifications { get; init; }
        = Array.Empty<AchievementUnlockNotification>();

    /// <summary>Updated summary, when the service sends one.</summary>
    public AchievementSummary? Summary { get; init; }

    /// <summary>A successful result.</summary>
    /// <param name="notifications">Achievements unlocked by this report; may be empty.</param>
    public static AchievementReportResult Ok(IReadOnlyList<AchievementUnlockNotification>? notifications = null) =>
        new() { Success = true, Notifications = notifications ?? Array.Empty<AchievementUnlockNotification>() };

    /// <summary>A failed result.</summary>
    /// <param name="kind">How the call ended.</param>
    /// <param name="reason">Explanation shown to the user.</param>
    public static AchievementReportResult Failed(CommunityServiceFailureKind kind, string reason) =>
        new() { Success = false, FailureKind = kind, FailureReason = reason };
}

/// <summary>
/// A share card for one achievement.
/// </summary>
/// <remarks>
/// The documented endpoint is <c>GET /api/v1/achievements/share/{id}</c> (spec §6.2, 「接口共识」).
/// 成就分享卡片 is listed as P3 — 后续版本 (spec line 176), so it is part of the service's surface
/// but not part of what the first version of the achievement feature would use. It is declared here
/// because the reserved layer's job is to describe the whole documented surface once, rather than to
/// make the next person discover the fourth endpoint by reading an old document.
/// </remarks>
public sealed record AchievementShareCard
{
    /// <summary>Identifier of the achievement the card is about.</summary>
    public required string AchievementId { get; init; }

    /// <summary>URL of the rendered card image.</summary>
    public string? ImageUrl { get; init; }

    /// <summary>Achievement name to use as the card's heading.</summary>
    public string? Title { get; init; }

    /// <summary>Text to use as the card's subtitle.</summary>
    public string? Description { get; init; }
}
