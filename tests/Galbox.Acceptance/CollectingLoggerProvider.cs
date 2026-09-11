using Microsoft.Extensions.Logging;

namespace Galbox.Acceptance;

/// <summary>
/// Minimal in-memory <see cref="ILoggerProvider"/> that keeps every log record emitted by
/// the services under test. The acceptance report prints WARNING and above by default
/// (and everything in verbose mode), so diagnostics that the real app writes to the
/// debugger are not silently lost when the UI is absent.
/// </summary>
public sealed class CollectingLoggerProvider : ILoggerProvider
{
    private readonly List<LogRecord> _records = new();
    private readonly object _gate = new();

    /// <summary>One captured log record.</summary>
    public sealed record LogRecord(DateTime TimestampUtc, LogLevel Level, string Category, string Message, string? Exception);

    /// <summary>Snapshot of everything captured so far.</summary>
    public IReadOnlyList<LogRecord> Records
    {
        get
        {
            lock (_gate)
            {
                return _records.ToArray();
            }
        }
    }

    /// <summary>Drops captured records (used between phases).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _records.Clear();
        }
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new CollectingLogger(this, categoryName);

    /// <inheritdoc />
    public void Dispose()
    {
    }

    private void Add(LogRecord record)
    {
        lock (_gate)
        {
            _records.Add(record);
        }
    }

    private sealed class CollectingLogger : ILogger
    {
        private readonly CollectingLoggerProvider _owner;
        private readonly string _category;

        public CollectingLogger(CollectingLoggerProvider owner, string category)
        {
            _owner = owner;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            _owner.Add(new LogRecord(
                DateTime.UtcNow,
                logLevel,
                _category,
                message,
                exception?.ToString()));
        }
    }
}
