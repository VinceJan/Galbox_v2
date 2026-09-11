using System.Diagnostics;

namespace Galbox.Core.Api;

/// <summary>What the client-side rate limiter decided about a request.</summary>
public enum MoyuRateLimitDecision
{
    /// <summary>The request may be sent now.</summary>
    Proceed,

    /// <summary>The request must wait a moment; <see cref="MoyuRateLimitVerdict.RetryAfter"/> says how long.</summary>
    Wait,

    /// <summary>The rolling-minute budget is spent; the wait is longer than the caller allows.</summary>
    BudgetExhausted
}

/// <summary>The rate limiter's answer, with the wait it wants and the reason it gave.</summary>
public sealed class MoyuRateLimitVerdict
{
    /// <summary>The decision.</summary>
    public required MoyuRateLimitDecision Decision { get; init; }

    /// <summary>How long to wait before retrying.</summary>
    public TimeSpan RetryAfter { get; init; }

    /// <summary>Human-readable explanation, for the report and for the user.</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>True when the request may be sent immediately.</summary>
    public bool CanProceed => Decision == MoyuRateLimitDecision.Proceed;
}

/// <summary>
/// Client-side pacing.
///
/// <para>
/// This is self-restraint, not a fix for a server problem. moyu.moe is a free community project on
/// Cloudflare + Backblaze B2: its data endpoints cost it real money and it explicitly expresses —
/// through <c>robots.txt</c>, its OpenAPI description and a per-endpoint rate limiter — that it does
/// not want to be harvested in bulk. Galbox needs a handful of requests for one user looking at one
/// game, and must be unable to produce anything that looks like a crawl even if a caller makes a
/// mistake.
/// </para>
///
/// <para>
/// The limiter therefore enforces two things at once: a minimum spacing between requests, and a
/// rolling one-minute budget. It is the token-bucket the research report §6.6 asks for (steady state
/// ≤ 1 request/second, ≤ 30/minute).
/// </para>
/// </summary>
public interface IMoyuRateLimiter
{
    /// <summary>
    /// Reserves a slot if one is available now. <b>Not blocking</b>: a caller that gets
    /// <see cref="MoyuRateLimitDecision.Wait"/> decides for itself whether to wait, which is what
    /// keeps an unbounded delay out of a UI-bound call path.
    /// </summary>
    /// <param name="maxWait">The longest wait the caller is willing to accept.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<MoyuRateLimitVerdict> AcquireAsync(TimeSpan maxWait, CancellationToken cancellationToken = default);

    /// <summary>Requests sent in the current rolling minute.</summary>
    int RequestsInCurrentMinute { get; }
}

/// <summary>
/// The default <see cref="IMoyuRateLimiter"/>: a rolling-window limiter with a minimum interval.
/// </summary>
/// <remarks>
/// A single instance must be shared by every caller — it is registered as a singleton for exactly
/// that reason. A per-call limiter would enforce nothing.
/// <para>
/// Timestamps come from <see cref="Stopwatch"/>, not <see cref="DateTime"/>, so moving the system
/// clock cannot hand out extra budget.
/// </para>
/// </remarks>
public sealed class MoyuRateLimiter : IMoyuRateLimiter
{
    private readonly object _gate = new();
    private readonly Queue<long> _minuteWindow = new();
    private readonly TimeSpan _minRequestInterval;
    private readonly int _maxRequestsPerMinute;
    private long _nextAllowedTimestamp;

    /// <summary>Creates a limiter with an explicit policy.</summary>
    public MoyuRateLimiter(TimeSpan minRequestInterval, int maxRequestsPerMinute)
    {
        if (minRequestInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(minRequestInterval));
        }

        if (maxRequestsPerMinute < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRequestsPerMinute));
        }

        _minRequestInterval = minRequestInterval;
        _maxRequestsPerMinute = maxRequestsPerMinute;
    }

    /// <summary>Creates a limiter from the application's options.</summary>
    public MoyuRateLimiter(MoyuOptions options)
        : this(
            (options ?? throw new ArgumentNullException(nameof(options))).MinRequestInterval,
            options.MaxRequestsPerMinute)
    {
    }

    /// <inheritdoc />
    public int RequestsInCurrentMinute
    {
        get
        {
            lock (_gate)
            {
                TrimMinuteWindow(Stopwatch.GetTimestamp());
                return _minuteWindow.Count;
            }
        }
    }

    /// <inheritdoc />
    public Task<MoyuRateLimitVerdict> AcquireAsync(
        TimeSpan maxWait,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var now = Stopwatch.GetTimestamp();
            TrimMinuteWindow(now);

            if (_minuteWindow.Count >= _maxRequestsPerMinute)
            {
                // The oldest request in the window leaves it after one minute; that is when the
                // next slot opens.
                var reopenAt = _minuteWindow.Peek() + (long)(Stopwatch.Frequency * 60d);
                var wait = ToTimeSpan(reopenAt - now);
                return Task.FromResult(new MoyuRateLimitVerdict
                {
                    Decision = MoyuRateLimitDecision.BudgetExhausted,
                    RetryAfter = wait > TimeSpan.Zero ? wait : TimeSpan.Zero,
                    Reason = $"the client's own budget of {_maxRequestsPerMinute} requests/minute is spent"
                });
            }

            if (_nextAllowedTimestamp > now)
            {
                var wait = ToTimeSpan(_nextAllowedTimestamp - now);
                if (wait > maxWait)
                {
                    return Task.FromResult(new MoyuRateLimitVerdict
                    {
                        Decision = MoyuRateLimitDecision.BudgetExhausted,
                        RetryAfter = wait,
                        Reason = $"pacing wants {wait.TotalSeconds:F1}s between requests, "
                                 + $"which is longer than the {maxWait.TotalSeconds:F1}s this call allows"
                    });
                }

                // Reserve the slot that opens at _nextAllowedTimestamp.
                _nextAllowedTimestamp += IntervalTicks;
                _minuteWindow.Enqueue(_nextAllowedTimestamp);
                return Task.FromResult(new MoyuRateLimitVerdict
                {
                    Decision = MoyuRateLimitDecision.Wait,
                    RetryAfter = wait,
                    Reason = $"pacing: {_minRequestInterval.TotalSeconds:F1}s between requests"
                });
            }

            _nextAllowedTimestamp = now + IntervalTicks;
            _minuteWindow.Enqueue(now);
            return Task.FromResult(new MoyuRateLimitVerdict
            {
                Decision = MoyuRateLimitDecision.Proceed,
                Reason = "within the client's self-imposed pacing"
            });
        }
    }

    private long IntervalTicks => (long)(Stopwatch.Frequency * _minRequestInterval.TotalSeconds);

    private static TimeSpan ToTimeSpan(long ticks) =>
        ticks <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds((double)ticks / Stopwatch.Frequency);

    private void TrimMinuteWindow(long now)
    {
        var cutoff = now - (long)(Stopwatch.Frequency * 60d);
        while (_minuteWindow.Count > 0 && _minuteWindow.Peek() < cutoff)
        {
            _minuteWindow.Dequeue();
        }
    }
}
