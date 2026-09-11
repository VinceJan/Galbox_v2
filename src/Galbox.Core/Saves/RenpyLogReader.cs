// Steps 2-4 — reading the game state out of a save's "log" pickle, safely.
//
// The log entry is a Python pickle of the tuple (roots, rollback_log):
//   roots        : dict keyed "store.<var>", holding only variables changed this session
//                  ("store._history_list" is the interesting one)
//   rollback_log : renpy.python.RollbackLog
//                    .current  -> renpy.python.Rollback
//                       .context -> renpy.execution.Context
//                          .runtime -> play time in SECONDS (the engine's docstring says
//                                      milliseconds; the measured 1194-2415 range only makes
//                                      sense as seconds, so seconds it is)
//                          .current -> a label name, a "from _call_xxx_N" name, or a
//                                      (file, compile_timestamp, statement_serial) tuple
//
// The statement tuple's third element is a GLOBAL STATEMENT SERIAL, not a line number
// (renpy/script.py assign_names uses self.serial). It is recorded but never treated as a line.
//
// Nothing here executes the pickle: PickleScanner decodes opcodes into data only.
using Galbox.Core.Saves.Pickle;

namespace Galbox.Core.Saves;

/// <summary>Raw, unresolved contents of a save's <c>log</c> pickle.</summary>
public sealed record RenpyLogData
{
    /// <summary>Play time in seconds, when <c>context.runtime</c> was readable.</summary>
    public double? RuntimeSeconds { get; init; }

    /// <summary>Raw <c>Context.current</c> value.</summary>
    public PickleValue? ContextCurrent { get; init; }

    /// <summary>Raw <c>roots</c> dictionary (keys look like <c>store.foo</c>).</summary>
    public PickleValue? Roots { get; init; }

    /// <summary>Raw dialogue history entries from <c>store._history_list</c>.</summary>
    public IReadOnlyList<PickleValue> HistoryEntries { get; init; } = Array.Empty<PickleValue>();

    /// <summary>Game store variables that were changed during this session (name to value).</summary>
    public IReadOnlyDictionary<string, PickleValue> StoreVariables { get; init; }
        = new Dictionary<string, PickleValue>(StringComparer.Ordinal);

    /// <summary>Number of rollback checkpoints in the log.</summary>
    public int RollbackCount { get; init; }

    /// <summary>Audit of the scan.</summary>
    public required PickleScanAudit Audit { get; init; }
}

/// <summary>Parses the <c>log</c> pickle of a save into plain data.</summary>
public static class RenpyLogReader
{
    /// <summary>Maximum number of history entries copied out (defensive bound for odd saves).</summary>
    private const int MaxHistoryEntries = 100_000;

    /// <summary>Parses raw <c>log</c> bytes.</summary>
    /// <exception cref="PickleScanException">The pickle is malformed or exceeds resource limits.</exception>
    public static RenpyLogData Read(byte[] logBytes)
    {
        ArgumentNullException.ThrowIfNull(logBytes);

        var scan = PickleScanner.Scan(logBytes);
        var root = scan.Root;

        // Shape: (roots_dict, rollback_log).
        var roots = root.Kind == PickleValueKind.Tuple && root.Items.Count >= 1
            ? root.Items[0]
            : root;

        var rollbackLog = root.Kind == PickleValueKind.Tuple && root.Items.Count >= 2
            ? root.Items[1]
            : null;

        var context = rollbackLog?.GetPath("current.context");

        double? runtime = context?.GetDouble("runtime");
        var contextCurrent = context?.GetMember("current");

        var historyEntryValue = roots.GetMember("store._history_list");
        var history = historyEntryValue is null
            ? new List<PickleValue>()
            : historyEntryValue.EnumerateSequence().Take(MaxHistoryEntries).ToList();

        var store = new Dictionary<string, PickleValue>(StringComparer.Ordinal);

        foreach (var pair in roots.EnumeratePairs())
        {
            if (pair.Key.Kind == PickleValueKind.String && pair.Key.Text is { } key)
            {
                store[key.StartsWith("store.", StringComparison.Ordinal) ? key["store.".Length..] : key] =
                    pair.Value;
            }
        }

        var rollbackCount = rollbackLog?.GetMember("log") is { } logList
            ? logList.Count
            : 0;

        return new RenpyLogData
        {
            RuntimeSeconds = runtime,
            ContextCurrent = contextCurrent,
            Roots = roots,
            HistoryEntries = history,
            StoreVariables = store,
            RollbackCount = rollbackCount,
            Audit = scan.Audit,
        };
    }

    /// <summary>
    /// Renders <c>Context.current</c> for display and classifies its shape. The tuple branch is
    /// deliberately explicit about the third element being a statement serial.
    /// </summary>
    public static (RenpyStatementKind Kind, string? Display) DescribeContextCurrent(PickleValue? value)
    {
        switch (value?.Kind)
        {
            case PickleValueKind.String:
                var text = value.Text ?? string.Empty;

                return (text.StartsWith("_call_", StringComparison.Ordinal)
                        || text.StartsWith("_after_", StringComparison.Ordinal)
                        || text.StartsWith("_before_", StringComparison.Ordinal))
                    ? (RenpyStatementKind.CallSite, text)
                    : (RenpyStatementKind.Label, text);

            case PickleValueKind.Tuple:
                {
                    var parts = new List<string>();
                    var file = value.Items.Count > 0 && value.Items[0].Kind == PickleValueKind.String
                        ? value.Items[0].Text
                        : "?";
                    var timestamp = value.Items.Count > 1 && value.Items[1].Kind == PickleValueKind.Int
                        ? value.Items[1].Integer
                        : 0;
                    var serial = value.Items.Count > 2 && value.Items[2].Kind == PickleValueKind.Int
                        ? value.Items[2].Integer
                        : 0;

                    parts.Add("'" + file + "'");
                    parts.Add(timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    parts.Add(serial.ToString(System.Globalization.CultureInfo.InvariantCulture)
                              + " /* statement serial, NOT a line */");

                    return (RenpyStatementKind.Statement, "(" + string.Join(", ", parts) + ")");
                }

            case null:
                return (RenpyStatementKind.Unknown, null);

            default:
                return (RenpyStatementKind.Unknown, value.ToString());
        }
    }

    /// <summary>Extracts the <c>(file, timestamp, serial)</c> triple of a statement position.</summary>
    public static (string File, long Timestamp, long Serial)? GetStatementIdentity(PickleValue? value)
    {
        if (value?.Kind != PickleValueKind.Tuple || value.Items.Count < 3)
        {
            return null;
        }

        var file = value.Items[0].Kind == PickleValueKind.String ? value.Items[0].Text ?? "?" : "?";
        var timestamp = value.Items[1].Kind == PickleValueKind.Int ? value.Items[1].Integer : 0;
        var serial = value.Items[2].Kind == PickleValueKind.Int ? value.Items[2].Integer : 0;

        return (file, timestamp, serial);
    }

    /// <summary>Pulls the <c>voice.tlid</c> string out of a history entry.</summary>
    public static string? GetTlid(PickleValue historyEntry)
    {
        var voice = historyEntry.GetMember("voice");

        return voice?.GetPathString("tlid");
    }
}
