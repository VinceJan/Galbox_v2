namespace Galbox.Data.Entities;

/// <summary>
/// Persisted library status of a game.
/// </summary>
/// <remarks>
/// Product source: `_product/Galbox-产品知识总纲.md` §4.1 — the five values 正在游玩 / 已完成 / 待玩 / 暂停中 / 未玩过.
/// <para>
/// This status is <b>stored</b>, not derived. The previous implementation only produced four ad-hoc display
/// strings from play statistics (<c>LibraryViewModel.GetGameStatus</c>), which silently dropped the two
/// user-intent values 待玩 ("plan to play", the core need of collectors with a backlog) and 暂停中
/// ("on hold"). See <see cref="GameStatusDerivation"/> for the retained automatic suggestion used as a
/// default value only.
/// </para>
/// </remarks>
public enum GameStatus
{
    /// <summary>
    /// 未玩过 — The game was never started (no launch recorded).
    /// </summary>
    NotPlayed = 0,

    /// <summary>
    /// 正在游玩 — The user is currently playing the game.
    /// </summary>
    Playing = 1,

    /// <summary>
    /// 已完成 — The game was finished (all intended routes cleared).
    /// </summary>
    Completed = 2,

    /// <summary>
    /// 待玩 — The user intends to play the game (backlog / "plan to play" list).
    /// This value can only come from the user; it is never derived from statistics.
    /// </summary>
    PlanToPlay = 3,

    /// <summary>
    /// 暂停中 — The game was started but is currently on hold.
    /// </summary>
    OnHold = 4
}

/// <summary>
/// Automatic suggestion rules for <see cref="GameInfo.Status"/>.
/// </summary>
/// <remarks>
/// These rules reproduce the legacy behaviour exactly (the four display strings that used to be computed by
/// <c>LibraryViewModel.GetGameStatus</c>) and map each of them onto one of the five persisted values:
/// <list type="table">
/// <listheader><term>Legacy display text</term><description>Mapped <see cref="GameStatus"/></description></listheader>
/// <item><term>从未游玩 (LaunchCount == 0)</term><description><see cref="GameStatus.NotPlayed"/></description></item>
/// <item><term>已完成 (TotalPlayTimeSeconds &gt; 1h)</term><description><see cref="GameStatus.Completed"/></description></item>
/// <item><term>正在游玩 (LastSessionTime within 7 days)</term><description><see cref="GameStatus.Playing"/></description></item>
/// <item><term>已游玩 (everything else)</term><description><see cref="GameStatus.OnHold"/></description></item>
/// </list>
/// <para>
/// <b>Use this only as a suggested default</b> — for example when a game is added to the library, or when the
/// user asks "what should this be set to?". It must never overwrite a status the user set by hand
/// (see <see cref="GameInfo.IsStatusUserSet"/>). <see cref="GameStatus.PlanToPlay"/> is intentionally never
/// returned: the backlog is a user decision that cannot be inferred from play counters.
/// </para>
/// <para>
/// Known weakness inherited from the legacy rule: "> 1 hour of play time means completed" is a rough proxy, not
/// a real clear. Once the save-node model is populated, a genuine completion signal should come from
/// <see cref="SaveNode.ChapterProgressPercent"/> / <see cref="SaveNode.SceneLabel"/> instead.
/// </para>
/// </remarks>
public static class GameStatusDerivation
{
    /// <summary>
    /// Play time (in seconds) above which the legacy rule reported a game as "已完成" (completed).
    /// </summary>
    public const long CompletedPlayTimeThresholdSeconds = 3600;

    /// <summary>
    /// How recently a game must have been launched to count as "正在游玩" (currently playing).
    /// </summary>
    public static readonly TimeSpan RecentSessionWindow = TimeSpan.FromDays(7);

    /// <summary>
    /// Suggests a <see cref="GameStatus"/> from the game's play statistics, using the legacy rules.
    /// </summary>
    /// <param name="game">The game to derive a suggestion for.</param>
    /// <param name="utcNow">
    /// Current UTC time; defaults to <see cref="DateTime.UtcNow"/>. Pass an explicit value for deterministic tests.
    /// </param>
    /// <returns>The suggested status. Never <see cref="GameStatus.PlanToPlay"/>.</returns>
    public static GameStatus Suggest(GameInfo game, DateTime? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(game);

        var now = utcNow ?? DateTime.UtcNow;

        // Order matters: it mirrors the legacy implementation so that a migrated library keeps showing
        // the same status it showed before the field was persisted.
        if (game.LaunchCount == 0)
        {
            return GameStatus.NotPlayed;
        }

        if (game.TotalPlayTimeSeconds > CompletedPlayTimeThresholdSeconds)
        {
            return GameStatus.Completed;
        }

        if (game.LastSessionTime.HasValue && game.LastSessionTime.Value >= now - RecentSessionWindow)
        {
            return GameStatus.Playing;
        }

        return GameStatus.OnHold;
    }
}
