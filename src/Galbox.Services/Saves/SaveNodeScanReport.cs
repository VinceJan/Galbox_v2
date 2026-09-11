// The scan result the UI (or a verification harness) consumes: what happened, what was written, and why not.
//
// Product rule (`_product/design/save-node-integration-plan.md` §6.4): the empty and error states must be
// honest. A failed scan therefore returns a report with a concrete reason instead of throwing, and a scan
// that succeeded but skipped something says exactly what and why.
using Galbox.Data.Entities;

namespace Galbox.Services.Saves;

/// <summary>Why a scan produced no usable result.</summary>
public enum SaveNodeScanFailureKind
{
    /// <summary>The scan succeeded.</summary>
    None = 0,

    /// <summary>The caller passed an unusable game id or path.</summary>
    InvalidArgument = 1,

    /// <summary>No game with that id exists in the library.</summary>
    GameNotFound = 2,

    /// <summary>The game runs on an engine whose save parser does not exist yet.</summary>
    UnsupportedEngine = 3,

    /// <summary>The installation has no readable save directory or holds no save files.</summary>
    SaveDirectoryNotFound = 4,

    /// <summary>The engine analysis itself failed.</summary>
    AnalysisFailed = 5,

    /// <summary>The database rejected the write; nothing was committed.</summary>
    DatabaseFailure = 6,
}

/// <summary>What the scan did with one slot's row.</summary>
public enum SaveNodeSlotChange
{
    /// <summary>A new node row was inserted.</summary>
    Added = 0,

    /// <summary>An existing node row was updated with new values.</summary>
    Updated = 1,

    /// <summary>An existing node row already held these values; nothing was written.</summary>
    Unchanged = 2,
}

/// <summary>Per-slot outcome, kept so a report can name the slot that failed and why.</summary>
public sealed record SaveNodeSlotResult
{
    /// <summary>Slot name as stored on the node.</summary>
    public required string SlotName { get; init; }

    /// <summary>Effective save file path.</summary>
    public string? SaveFilePath { get; init; }

    /// <summary>Database identifier of the node (0 for a row that was not written).</summary>
    public int NodeId { get; init; }

    /// <summary>Whether the row was inserted, updated or already current.</summary>
    public required SaveNodeSlotChange Change { get; init; }

    /// <summary>Parse outcome persisted for the slot.</summary>
    public required SaveParseStatus ParseStatus { get; init; }

    /// <summary>Persisted explanation when the parse was partial or failed.</summary>
    public string? ParseError { get; init; }

    /// <summary>How the existing row was found: <c>New</c>, <c>FilePath</c>, <c>MirrorPath</c> or <c>SlotName</c>.</summary>
    public required string MatchedBy { get; init; }

    /// <summary>Save time (file mtime) of the effective copy.</summary>
    public DateTime? SaveModifiedTimeUtc { get; init; }
}

/// <summary>Outcome of <see cref="ISaveNodeScanService.ScanAsync"/>.</summary>
public sealed record SaveNodeScanReport
{
    /// <summary>True when the analysis ran and the database write completed.</summary>
    public required bool Succeeded { get; init; }

    /// <summary>Failure category; <see cref="SaveNodeScanFailureKind.None"/> on success.</summary>
    public SaveNodeScanFailureKind FailureKind { get; init; }

    /// <summary>User-presentable reason for a failure (Chinese), or null on success.</summary>
    public string? FailureReason { get; init; }

    /// <summary>Game the scan was requested for.</summary>
    public int GameInfoId { get; init; }

    /// <summary>Installation path that was scanned.</summary>
    public string GameInstallPath { get; init; } = string.Empty;

    /// <summary>Ren'Py save directory name from <c>config.save_directory</c>.</summary>
    public string? SaveDirectoryName { get; init; }

    /// <summary>UTC time the scan started.</summary>
    public required DateTime StartedUtc { get; init; }

    /// <summary>Wall-clock duration of the scan.</summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>Slots the analyzer found (after mirror de-duplication).</summary>
    public int SlotCount { get; init; }

    /// <summary>Newly inserted node rows.</summary>
    public int AddedCount { get; init; }

    /// <summary>Existing node rows whose values changed.</summary>
    public int UpdatedCount { get; init; }

    /// <summary>Existing node rows that were already current (nothing written).</summary>
    public int SkippedCount { get; init; }

    /// <summary>Slots whose parse failed, persisted with <see cref="SaveParseStatus.Failed"/> so the UI can explain it.</summary>
    public int FailedCount { get; init; }

    /// <summary>Nodes of this game that were not matched by any current save file (reported, never deleted).</summary>
    public int StaleNodeCount { get; init; }

    /// <summary>Node rows for this game after the scan.</summary>
    public int NodeCountAfterScan { get; init; }

    /// <summary>Game-level CG numbers as of the scan.</summary>
    public int CgUnlockedCount { get; init; }

    /// <summary>Game-level CG total as of the scan.</summary>
    public int CgTotalCount { get; init; }

    /// <summary>Game-level unlocked CG ids.</summary>
    public IReadOnlyList<string> CgUnlockedIds { get; init; } = Array.Empty<string>();

    /// <summary>Scene progress numerator (analyzer definition).</summary>
    public int UnlockedSceneCount { get; init; }

    /// <summary>Scene progress denominator (labels declared by the scripts).</summary>
    public int TotalSceneCount { get; init; }

    /// <summary>Scene-set clusters the scan computed.</summary>
    public int SceneClusterCount { get; init; }

    /// <summary>Clusters holding two or more saves, exposed as speculative route groups.</summary>
    public int SuspectedRouteGroupCount { get; init; }

    /// <summary>Per-slot outcome.</summary>
    public IReadOnlyList<SaveNodeSlotResult> Slots { get; init; } = Array.Empty<SaveNodeSlotResult>();

    /// <summary>Parser warnings and mapping remarks, kept verbatim for logs and the verification harness.</summary>
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    /// <summary>Multi-line developer summary (English; user-facing text lives in <see cref="FailureReason"/>).</summary>
    public string Describe()
    {
        var lines = new List<string>
        {
            $"result        : {(Succeeded ? "SUCCESS" : $"FAILED ({FailureKind})")}",
            $"game          : id={GameInfoId} install='{GameInstallPath}'",
            $"saveDirectory : {SaveDirectoryName ?? "(unknown)"}",
            $"elapsed       : {Elapsed.TotalSeconds:F2}s",
        };

        if (FailureReason is { Length: > 0 } reason)
        {
            lines.Add($"reason        : {reason}");
        }

        lines.Add(
            $"nodes         : slots={SlotCount} added={AddedCount} updated={UpdatedCount} "
            + $"skipped={SkippedCount} failed={FailedCount} stale={StaleNodeCount} afterScan={NodeCountAfterScan}");
        lines.Add(
            $"cg (game-level): {CgUnlockedCount}/{CgTotalCount} unlocked=[{string.Join(",", CgUnlockedIds)}]");
        lines.Add(
            $"scene progress : {UnlockedSceneCount}/{TotalSceneCount} (clusters={SceneClusterCount}, "
            + $"suspectedGroups={SuspectedRouteGroupCount})");
        lines.Add("slots         :");

        foreach (var slot in Slots)
        {
            lines.Add(
                $"  - {slot.SlotName,-16} {slot.Change,-9} {slot.ParseStatus,-13} matchedBy={slot.MatchedBy,-10} "
                + $"node={slot.NodeId}"
                + (slot.ParseError is null ? string.Empty : $" error='{slot.ParseError}'"));
        }

        if (Diagnostics.Count > 0)
        {
            lines.Add("diagnostics   :");

            foreach (var diagnostic in Diagnostics)
            {
                lines.Add($"  ! {diagnostic}");
            }
        }

        return string.Join(Environment.NewLine, lines);
    }
}
