// The scan service: analyze a game's save directory, map every slot, upsert one SaveNode per slot.
//
// Placement (see src/Galbox.Services/README.md for the full reasoning): this is orchestration plus
// persistence, so it lives in its own assembly that references both Galbox.Core (parser) and Galbox.Data
// (entities + DbContext). Galbox.Core keeps its zero-persistence dependency set, and the application layer
// only has to reference Galbox.Services when it wires the feature up.
//
// Two behaviours are load-bearing and easy to break:
//   * Idempotency. A slot is matched by its effective file path, then by any of its mirrored paths, then by
//     its slot name. Ren'Py writes every save to two directories, so matching on the path alone would create
//     a second node the moment the "newest copy" flips.
//   * No dirty data. Every expected failure returns before anything is added to the change tracker, and a
//     database failure clears it so a half-applied state can never be committed by a later SaveChanges call.
using System.Data.Common;
using System.Diagnostics;
using Galbox.Core.Saves;
using Galbox.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Galbox.Services.Saves;

/// <summary>Default implementation of <see cref="ISaveNodeScanService"/>.</summary>
public sealed class SaveNodeScanService : ISaveNodeScanService
{
    private readonly GalboxDbContext _db;
    private readonly RenpySaveAnalysisOptions _analysisOptions;

    /// <summary>Creates the service with the default Ren'Py analysis options.</summary>
    /// <param name="db">Database context (injected; owns the connection).</param>
    public SaveNodeScanService(GalboxDbContext db)
        : this(db, RenpySaveAnalysisOptions.Default)
    {
    }

    /// <summary>Creates the service with explicit analysis options (used by tests and harnesses).</summary>
    /// <param name="db">Database context.</param>
    /// <param name="analysisOptions">Options handed to <see cref="RenpySaveAnalyzer"/>.</param>
    public SaveNodeScanService(GalboxDbContext db, RenpySaveAnalysisOptions analysisOptions)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _analysisOptions = analysisOptions ?? RenpySaveAnalysisOptions.Default;
    }

    /// <inheritdoc />
    public async Task<SaveNodeScanReport> ScanAsync(
        int gameInfoId, string gameInstallPath, CancellationToken cancellationToken = default)
    {
        var started = DateTime.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var installPath = gameInstallPath?.Trim() ?? string.Empty;

        if (gameInfoId <= 0)
        {
            return Failure(
                SaveNodeScanFailureKind.InvalidArgument,
                "游戏 ID 无效（必须大于 0），未执行扫描。",
                gameInfoId,
                installPath,
                started,
                stopwatch);
        }

        if (installPath.Length == 0)
        {
            return Failure(
                SaveNodeScanFailureKind.InvalidArgument,
                "游戏安装路径为空，无法定位存档目录。",
                gameInfoId,
                installPath,
                started,
                stopwatch);
        }

        // ---- the game must exist: nodes are keyed by GameInfoId and must never be written for a phantom game
        GameInfo? game;

        try
        {
            game = await _db.Games
                .AsNoTracking()
                .FirstOrDefaultAsync(g => g.Id == gameInfoId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsDatabaseFailure(ex))
        {
            return Failure(
                SaveNodeScanFailureKind.DatabaseFailure,
                $"读取游戏记录失败：{ex.Message}",
                gameInfoId,
                installPath,
                started,
                stopwatch);
        }

        if (game is null)
        {
            return Failure(
                SaveNodeScanFailureKind.GameNotFound,
                $"库中不存在 ID 为 {gameInfoId} 的游戏，未执行扫描。",
                gameInfoId,
                installPath,
                started,
                stopwatch);
        }

        var diagnostics = new List<string>();

        if (!string.Equals(game.InstallPath, installPath, StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(
                $"The library stores a different install path ('{game.InstallPath}'); the path supplied by the "
                + "caller was scanned and the game row was left untouched.");
        }

        // ---- unsupported engines must say so instead of producing empty/guessed nodes
        if (game.EngineType is not (GameEngineType.Renpy or GameEngineType.Unknown))
        {
            return Failure(
                SaveNodeScanFailureKind.UnsupportedEngine,
                $"暂不支持 {DescribeEngine(game.EngineType)} 引擎的存档解析（当前已实现 Ren'Py），未写入任何节点。",
                gameInfoId,
                installPath,
                started,
                stopwatch,
                diagnostics);
        }

        // ---- analyse
        RenpySaveAnalysis analysis;

        try
        {
            analysis = new RenpySaveAnalyzer(_analysisOptions).Analyze(installPath, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Failure(
                SaveNodeScanFailureKind.AnalysisFailed,
                $"解析存档时发生错误：{ex.GetType().Name}: {ex.Message}",
                gameInfoId,
                installPath,
                started,
                stopwatch,
                diagnostics);
        }

        diagnostics.AddRange(analysis.Warnings);

        foreach (var location in analysis.Directories.Locations)
        {
            diagnostics.Add(
                $"probe {location.Kind}: exists={location.Exists} saves={location.SaveFileCount} "
                + $"persistent={location.HasPersistentFile} path='{location.Path}'"
                + (location.FailureReason is null ? string.Empty : $" failure='{location.FailureReason}'"));
        }

        if (!analysis.Directories.Success || analysis.Saves.Count == 0)
        {
            var reason = analysis.Directories.FailureReason
                         ?? "未在任何候选存档目录里找到 .save 文件。";

            return Failure(
                SaveNodeScanFailureKind.SaveDirectoryNotFound,
                $"{reason}（安装路径：'{installPath}'）。未写入任何节点。",
                gameInfoId,
                installPath,
                started,
                stopwatch,
                diagnostics,
                analysis.Directories.SaveDirectoryName);
        }

        // ---- map
        var mapping = RenpySaveNodeMapper.Map(analysis);
        diagnostics.AddRange(mapping.Diagnostics);

        // ---- upsert
        List<SaveNode> existing;

        try
        {
            existing = await _db.SaveNodes
                .Where(node => node.GameInfoId == gameInfoId)
                .OrderBy(node => node.Id)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsDatabaseFailure(ex))
        {
            return Failure(
                SaveNodeScanFailureKind.DatabaseFailure,
                $"读取已有存档节点失败：{ex.Message}",
                gameInfoId,
                installPath,
                started,
                stopwatch,
                diagnostics,
                mapping.SaveDirectoryName);
        }

        var byPath = new Dictionary<string, SaveNode>(StringComparer.OrdinalIgnoreCase);
        var byMirrorPath = new Dictionary<string, SaveNode>(StringComparer.OrdinalIgnoreCase);
        var bySlotName = new Dictionary<string, SaveNode>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in existing)
        {
            if (!string.IsNullOrWhiteSpace(node.SaveFilePath))
            {
                byPath.TryAdd(node.SaveFilePath, node);
            }

            if (!string.IsNullOrWhiteSpace(node.SlotName))
            {
                bySlotName.TryAdd(node.SlotName, node);
            }

            // The mirrored copy of a previous scan is indexed too, so the node is still recognised when the
            // effective copy flips to the other directory between two scans.
            var stored = SaveNodeExtendedMetadata.FromJson(node.ExtendedMetadataJson);

            if (stored?.Mirrors is { Count: > 0 } mirrors)
            {
                foreach (var mirror in mirrors)
                {
                    if (!string.IsNullOrWhiteSpace(mirror.Path))
                    {
                        byMirrorPath.TryAdd(mirror.Path, node);
                    }
                }
            }
        }

        var now = DateTime.UtcNow;
        var pending = new List<PendingSlotResult>(mapping.Slots.Count);
        var claimedNodeIds = new HashSet<int>();
        var staleExisting = new HashSet<int>(existing.Select(node => node.Id));

        int added = 0, updated = 0, skipped = 0, failed = 0;

        foreach (var slot in mapping.Slots)
        {
            var matchedBy = "New";
            SaveNode? node = null;

            foreach (var path in slot.AllPaths)
            {
                if (byPath.TryGetValue(path, out var byPathNode))
                {
                    node = byPathNode;
                    matchedBy = string.Equals(path, slot.Metadata.SaveFilePath, StringComparison.OrdinalIgnoreCase)
                        ? "FilePath"
                        : "MirrorPath";
                    break;
                }

                if (byMirrorPath.TryGetValue(path, out var byMirrorNode))
                {
                    node = byMirrorNode;
                    matchedBy = "MirrorPath";
                    break;
                }
            }

            if (node is null && !string.IsNullOrWhiteSpace(slot.Metadata.SlotName)
                && bySlotName.TryGetValue(slot.Metadata.SlotName, out var byNameNode))
            {
                node = byNameNode;
                matchedBy = "SlotName";
            }

            if (node is null && bySlotName.TryGetValue(slot.SlotKey, out var byKeyNode))
            {
                node = byKeyNode;
                matchedBy = "SlotName";
            }

            SaveNodeSlotChange change;

            if (node is null)
            {
                node = new SaveNode
                {
                    GameInfoId = gameInfoId,
                    CreatedTime = now,
                    UpdatedTime = now,
                };

                _db.SaveNodes.Add(node);

                // A new row takes every value the parser produced; ApplyMetadata only writes non-null values.
                node.ApplyMetadata(slot.Metadata, now);
                ClearStaleParseError(node, slot.Metadata, now);

                change = SaveNodeSlotChange.Added;
                added++;
            }
            else
            {
                staleExisting.Remove(node.Id);

                if (!claimedNodeIds.Add(node.Id))
                {
                    diagnostics.Add(
                        $"Two slots matched the same node (#{node.Id}); the later one was updated onto it. "
                        + "This should not happen and points at a duplicated save file.");
                }

                if (ApplyIfDifferent(node, slot.Metadata, now))
                {
                    change = SaveNodeSlotChange.Updated;
                    updated++;
                }
                else
                {
                    change = SaveNodeSlotChange.Unchanged;
                    skipped++;
                }
            }

            if (slot.Metadata.ParseStatus == SaveParseStatus.Failed)
            {
                failed++;
            }

            // The node id is only assigned by the write, so the row is materialised into the report afterwards.
            pending.Add(new PendingSlotResult(node, slot.SlotKey, change, matchedBy));
        }

        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _db.ChangeTracker.Clear();
            throw;
        }
        catch (Exception ex) when (IsDatabaseFailure(ex) || ex is DbUpdateException)
        {
            _db.ChangeTracker.Clear();

            return Failure(
                SaveNodeScanFailureKind.DatabaseFailure,
                $"写入存档节点失败，本次扫描未产生任何数据（事务已回滚）：{ex.Message}",
                gameInfoId,
                installPath,
                started,
                stopwatch,
                diagnostics,
                mapping.SaveDirectoryName);
        }

        // Node ids are assigned by the write, so the report rows are built now that SaveChanges has run.
        var slotResults = pending
            .Select(row => new SaveNodeSlotResult
            {
                SlotName = row.Node.SlotName ?? row.SlotKey,
                SaveFilePath = row.Node.SaveFilePath,
                NodeId = row.Node.Id,
                Change = row.Change,
                ParseStatus = row.Node.ParseStatus,
                ParseError = row.Node.ParseError,
                MatchedBy = row.MatchedBy,
                SaveModifiedTimeUtc = row.Node.SaveModifiedTime,
            })
            .ToList();

        // Stale nodes are reported, never deleted: an engine-scanned node whose file disappeared may still be
        // the only record of a story position the user reached, and deleting user data is not this service's call.
        var staleCount = existing.Count(node =>
            staleExisting.Contains(node.Id)
            && node.Source == SaveNodeSource.EngineScan
            && !node.IsSnapshot);

        if (staleCount > 0)
        {
            diagnostics.Add(
                $"{staleCount} previously scanned node(s) of this game are no longer present on disk. They were "
                + "kept (nothing is deleted by a scan) — review them in the UI before removing them.");
        }

        int nodeCountAfterScan;

        try
        {
            nodeCountAfterScan = await _db.SaveNodes
                .CountAsync(node => node.GameInfoId == gameInfoId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (!IsCancellation(ex))
        {
            diagnostics.Add($"The node count could not be read back: {ex.Message}");
            nodeCountAfterScan = pending.Count;
        }

        stopwatch.Stop();

        return new SaveNodeScanReport
        {
            Succeeded = true,
            FailureKind = SaveNodeScanFailureKind.None,
            FailureReason = null,
            GameInfoId = gameInfoId,
            GameInstallPath = installPath,
            SaveDirectoryName = mapping.SaveDirectoryName,
            StartedUtc = started,
            Elapsed = stopwatch.Elapsed,
            SlotCount = slotResults.Count,
            AddedCount = added,
            UpdatedCount = updated,
            SkippedCount = skipped,
            FailedCount = failed,
            StaleNodeCount = staleCount,
            NodeCountAfterScan = nodeCountAfterScan,
            CgUnlockedCount = mapping.CgUnlockedCount,
            CgTotalCount = mapping.CgTotalCount,
            CgUnlockedIds = mapping.CgUnlockedIds,
            UnlockedSceneCount = mapping.UnlockedSceneCount,
            TotalSceneCount = mapping.TotalSceneCount,
            SceneClusterCount = mapping.SceneClusterCount,
            SuspectedRouteGroupCount = mapping.SuspectedRouteGroupCount,
            Slots = slotResults,

            // The parser warnings are reported by the analyzer and repeated by the mapping, so the list is
            // de-duplicated while keeping the first-occurrence order (a diagnostic is shown once).
            Diagnostics = diagnostics.Distinct(StringComparer.Ordinal).ToList(),
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SaveNode>> GetNodesAsync(
        int gameInfoId, CancellationToken cancellationToken = default)
    {
        return await _db.SaveNodes
            .AsNoTracking()
            .Where(node => node.GameInfoId == gameInfoId)
            .OrderBy(node => node.SaveModifiedTime)
            .ThenBy(node => node.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Builds a failure report; the counters stay at zero because nothing was written.</summary>
    private static SaveNodeScanReport Failure(
        SaveNodeScanFailureKind kind,
        string reason,
        int gameInfoId,
        string installPath,
        DateTime startedUtc,
        Stopwatch stopwatch,
        IReadOnlyList<string>? diagnostics = null,
        string? saveDirectoryName = null)
    {
        stopwatch.Stop();

        return new SaveNodeScanReport
        {
            Succeeded = false,
            FailureKind = kind,
            FailureReason = reason,
            GameInfoId = gameInfoId,
            GameInstallPath = installPath,
            SaveDirectoryName = saveDirectoryName,
            StartedUtc = startedUtc,
            Elapsed = stopwatch.Elapsed,
            Diagnostics = diagnostics ?? Array.Empty<string>(),
        };
    }

    /// <summary>True for the database exceptions an expected failure can surface as.</summary>
    private static bool IsDatabaseFailure(Exception ex)
        => ex is DbException or DbUpdateException or InvalidOperationException or NotSupportedException;

    /// <summary>
    /// Writes the metadata only when it would actually change a stored value, and reports whether the row
    /// changed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="SaveNode.ApplyMetadata"/> returns true whenever it *writes* a non-null value, not only when
    /// the value differs, so it cannot be used on its own to answer "is this row already up to date?" — a
    /// second scan of an unchanged save directory would report 12 updates and bump <c>UpdatedTime</c> on
    /// every node forever.
    /// </para>
    /// <para>
    /// The comparison is done by applying the metadata to a copy of the row and comparing the stored columns
    /// afterwards. That way the comparison uses exactly the same clamping and null handling as the real
    /// write, and a value that <see cref="SaveNode.ApplyMetadata"/> would normalise (negative counters,
    /// percentages outside 0-100) cannot make the row look permanently out of date.
    /// </para>
    /// </remarks>
    private static bool ApplyIfDifferent(SaveNode node, SaveNodeMetadata metadata, DateTime now)
    {
        var candidate = CopyStoredValues(node);
        candidate.ApplyMetadata(metadata, now);

        var differs = !SameStoredValues(node, candidate)
                      || ClearStaleParseError(candidate, metadata, now);

        if (!differs)
        {
            return false;
        }

        node.ApplyMetadata(metadata, now);
        ClearStaleParseError(node, metadata, now);
        return true;
    }

    /// <summary>
    /// Removes a failure text that a successful parse has made obsolete. <see cref="SaveNode.ApplyMetadata"/>
    /// can only write non-null values, so without this a node that once failed would show the old error
    /// forever.
    /// </summary>
    private static bool ClearStaleParseError(SaveNode node, SaveNodeMetadata metadata, DateTime now)
    {
        if (metadata.ParseStatus != SaveParseStatus.Parsed || node.ParseError is null)
        {
            return false;
        }

        node.ParseError = null;
        node.UpdatedTime = now;
        return true;
    }

    /// <summary>Copies one row into a detached instance with the same stored values.</summary>
    private static SaveNode CopyStoredValues(SaveNode node) => new()
    {
        Id = node.Id,
        GameInfoId = node.GameInfoId,
        SaveGroupId = node.SaveGroupId,
        SlotName = node.SlotName,
        SceneLabel = node.SceneLabel,
        RouteName = node.RouteName,
        ChapterName = node.ChapterName,
        ChapterProgressPercent = node.ChapterProgressPercent,
        CgUnlockedCount = node.CgUnlockedCount,
        CgTotalCount = node.CgTotalCount,
        CgUnlockPercent = node.CgUnlockPercent,
        CgUnlockedIdsJson = node.CgUnlockedIdsJson,
        PlayTimeSeconds = node.PlayTimeSeconds,
        SaveFilePath = node.SaveFilePath,
        SaveSizeBytes = node.SaveSizeBytes,
        SaveFileCount = node.SaveFileCount,
        SaveCreatedTime = node.SaveCreatedTime,
        SaveModifiedTime = node.SaveModifiedTime,
        CreatedTime = node.CreatedTime,
        UpdatedTime = node.UpdatedTime,
        IsSnapshot = node.IsSnapshot,
        SnapshotDescription = node.SnapshotDescription,
        Source = node.Source,
        ParseStatus = node.ParseStatus,
        ParseError = node.ParseError,
        ExtendedMetadataJson = node.ExtendedMetadataJson,
    };

    /// <summary>
    /// Compares the stored columns of two rows. The identifiers, the row timestamps and the navigation
    /// properties are excluded: they are not values the parser produces.
    /// </summary>
    private static bool SameStoredValues(SaveNode a, SaveNode b)
        => a.GameInfoId == b.GameInfoId
           && a.SaveGroupId == b.SaveGroupId
           && Same(a.SlotName, b.SlotName)
           && Same(a.SceneLabel, b.SceneLabel)
           && Same(a.RouteName, b.RouteName)
           && Same(a.ChapterName, b.ChapterName)
           && a.ChapterProgressPercent == b.ChapterProgressPercent
           && a.CgUnlockedCount == b.CgUnlockedCount
           && a.CgTotalCount == b.CgTotalCount
           && a.CgUnlockPercent == b.CgUnlockPercent
           && Same(a.CgUnlockedIdsJson, b.CgUnlockedIdsJson)
           && a.PlayTimeSeconds == b.PlayTimeSeconds
           && Same(a.SaveFilePath, b.SaveFilePath)
           && a.SaveSizeBytes == b.SaveSizeBytes
           && a.SaveFileCount == b.SaveFileCount
           && a.SaveCreatedTime == b.SaveCreatedTime
           && a.SaveModifiedTime == b.SaveModifiedTime
           && a.IsSnapshot == b.IsSnapshot
           && Same(a.SnapshotDescription, b.SnapshotDescription)
           && a.Source == b.Source
           && a.ParseStatus == b.ParseStatus
           && Same(a.ParseError, b.ParseError)
           && Same(a.ExtendedMetadataJson, b.ExtendedMetadataJson);

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.Ordinal);

    private static bool IsCancellation(Exception ex) => ex is OperationCanceledException;

    private static string DescribeEngine(GameEngineType engine) => engine switch
    {
        GameEngineType.Krkr => "KiriKiri",
        GameEngineType.Tyrano => "TyranoScript",
        GameEngineType.Vnm => "VNM",
        GameEngineType.Unity => "Unity",
        GameEngineType.RpgMaker => "RPG Maker",
        GameEngineType.Other => "其它",
        _ => engine.ToString(),
    };

    /// <summary>A slot outcome captured while the change tracker is still writable (the id comes later).</summary>
    /// <param name="Node">Tracked node entity.</param>
    /// <param name="SlotKey">Parser slot key, used when even the slot name is unknown.</param>
    /// <param name="Change">Insert/update/no-op.</param>
    /// <param name="MatchedBy">How an existing row was found.</param>
    private sealed record PendingSlotResult(
        SaveNode Node, string SlotKey, SaveNodeSlotChange Change, string MatchedBy);
}
