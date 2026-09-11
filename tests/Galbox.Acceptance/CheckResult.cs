namespace Galbox.Acceptance;

/// <summary>Outcome of a single acceptance check.</summary>
public enum CheckStatus
{
    /// <summary>Measured behaviour matched the stated expectation.</summary>
    Pass,

    /// <summary>Measured behaviour did not match the stated expectation.</summary>
    Fail,

    /// <summary>The check could not complete (unhandled exception in the code path).</summary>
    Error
}

/// <summary>
/// Result of one acceptance check: what was expected, what was actually measured, and any
/// raw detail lines that support the verdict. Detail lines are printed verbatim so the
/// report can be audited without re-running the tool.
/// </summary>
public sealed class CheckResult
{
    /// <summary>Stable check identifier, e.g. "A1".</summary>
    public required string Id { get; init; }

    /// <summary>Human readable check name.</summary>
    public required string Title { get; init; }

    /// <summary>Verdict.</summary>
    public required CheckStatus Status { get; init; }

    /// <summary>Expectation the check was written against.</summary>
    public string Expected { get; init; } = string.Empty;

    /// <summary>Measured value the verdict was derived from.</summary>
    public string Actual { get; init; } = string.Empty;

    /// <summary>Raw supporting output (printed indented, in order).</summary>
    public List<string> Details { get; init; } = new();

    /// <summary>Captured exception text when <see cref="Status"/> is Error.</summary>
    public string? Exception { get; init; }

    /// <summary>Wall-clock time the check took.</summary>
    public long ElapsedMilliseconds { get; set; }

    /// <summary>Creates a PASS result.</summary>
    public static CheckResult Pass(string id, string title, string expected, string actual) =>
        new() { Id = id, Title = title, Status = CheckStatus.Pass, Expected = expected, Actual = actual };

    /// <summary>Creates a FAIL result.</summary>
    public static CheckResult Fail(string id, string title, string expected, string actual) =>
        new() { Id = id, Title = title, Status = CheckStatus.Fail, Expected = expected, Actual = actual };

    /// <summary>Creates an ERROR result.</summary>
    public static CheckResult Error(string id, string title, string expected, string actual, Exception ex) =>
        new()
        {
            Id = id,
            Title = title,
            Status = CheckStatus.Error,
            Expected = expected,
            Actual = actual,
            Exception = ex.ToString()
        };

    /// <summary>Fluent helper for adding a raw detail line.</summary>
    public CheckResult With(params string[] lines)
    {
        Details.AddRange(lines);
        return this;
    }
}

/// <summary>A single executable acceptance check.</summary>
public interface IAcceptanceCheck
{
    /// <summary>Stable identifier, e.g. "A5".</summary>
    string Id { get; }

    /// <summary>Human readable name.</summary>
    string Title { get; }

    /// <summary>Runs the check. Exceptions are caught by the runner and reported as ERROR.</summary>
    Task<CheckResult> RunAsync(AcceptanceContext context, CancellationToken cancellationToken);
}
