// Turns stored SaveNode rows (and one scan report) into the SaveTimelineModel the page renders.
//
// This file is the single place where "what does the user actually read" is decided, which is why
// the acceptance harness drives it instead of asserting on the database columns alone: a scan that
// stores perfect data and renders "扫描失败" for everything is not a delivered feature.
using System.Text.Json;
using Galbox.Data.Entities;
using Galbox.Services.Saves;

namespace Galbox.App.Saves;

/// <summary>Projects <see cref="SaveNode"/> rows and scan reports onto the timeline view model.</summary>
public static class SaveNodeTimelineBuilder
{
    /// <summary>
    /// Builds the timeline from the stored rows of one game.
    /// </summary>
    /// <param name="nodes">Stored nodes of the game; any order (the timeline sorts them by save time).</param>
    public static SaveTimelineModel Build(IReadOnlyList<SaveNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        if (nodes.Count == 0)
        {
            return SaveTimelineModel.NeverScanned;
        }

        // §6.1: the timeline is ordered by the SAVE's modified time (the moment the player saved),
        // never by the row's write time. A node without a stored time sinks to the end instead of
        // being dropped.
        var ordered = nodes
            .OrderBy(node => node.SaveModifiedTime ?? DateTime.MaxValue)
            .ThenBy(node => node.Id)
            .ToList();

        var entries = ordered
            .Select(node => new SaveNodeTimelineEntry(node, ParseEvidence(node)))
            .ToList();

        // §3.2.1: CG progress belongs to the game (the shared `persistent` file), so it is read from
        // ONE node and shown once - not per node. The newest node that actually has a gallery total
        // wins, so a single unreadable save cannot blank the figure.
        var progressSource = ordered
            .Where(node => node.CgTotalCount > 0)
            .OrderByDescending(node => node.SaveModifiedTime ?? DateTime.MinValue)
            .ThenByDescending(node => node.Id)
            .FirstOrDefault();

        var progressEvidence = progressSource is null ? null : ParseEvidence(progressSource);
        var gameLevel = progressEvidence?.GameLevel;

        var cg = progressSource is null
            ? new SaveCgProgressView()
            : new SaveCgProgressView
            {
                UnlockedCount = progressSource.CgUnlockedCount,
                TotalCount = progressSource.CgTotalCount,
                UnlockedIds = ParseIdList(progressSource.CgUnlockedIdsJson),
                LockedIds = gameLevel?.LockedCgIds is { Count: > 0 } locked
                    ? locked.OrderBy(id => id, StringComparer.Ordinal).ToList()
                    : Array.Empty<string>(),
            };

        var sceneProgress = progressEvidence?.SceneProgress;

        var scene = new SaveSceneProgressView
        {
            UnlockedScenes = sceneProgress?.UnlockedScenes ?? 0,
            TotalScenes = sceneProgress?.TotalScenes ?? 0,
        };

        var parseProblems = entries.Count(entry => entry.HasParseProblem);
        var autoSaves = entries.Count(entry => entry.IsAutoSave);
        var snapshots = entries.Count(entry => entry.IsSnapshot);

        var status =
            $"已扫描 {entries.Count} 个存档节点（自动档 {autoSaves} / 手动档 {entries.Count - autoSaves}）"
            + (snapshots > 0 ? $"，其中 {snapshots} 个是快照" : string.Empty);

        var hints = new List<string>();

        if (parseProblems > 0)
        {
            // Red line 3: the count is stated up front; the individual reason is on the node itself.
            hints.Add($"{parseProblems} 个节点的解析有问题，原因已标在对应节点上");
        }

        if (entries.Any(entry => !entry.HasSceneLabel))
        {
            hints.Add($"{entries.Count(entry => !entry.HasSceneLabel)} 个节点没有解析出场景名");
        }

        return new SaveTimelineModel
        {
            State = SaveTimelineState.Ready,
            Entries = entries,
            Cg = cg,
            Scene = scene,
            RouteGroups = BuildRouteGroups(entries),
            StatusText = status,
            HintText = hints.Count == 0 ? null : string.Join("；", hints),
        };
    }

    /// <summary>
    /// Turns a scan report into the section's state. A failure keeps the concrete reason from the
    /// service; it is never replaced by a generic "扫描失败" (plan §6.4).
    /// </summary>
    /// <param name="report">Report returned by <see cref="ISaveNodeScanService.ScanAsync"/>.</param>
    /// <param name="nodesAfterScan">Nodes stored for the game after the scan.</param>
    public static SaveTimelineModel FromScanReport(
        SaveNodeScanReport report, IReadOnlyList<SaveNode> nodesAfterScan)
    {
        ArgumentNullException.ThrowIfNull(report);

        var reason = string.IsNullOrWhiteSpace(report.FailureReason)
            ? $"扫描没有成功（{DescribeFailure(report.FailureKind)}），但服务没有留下具体原因。"
            : report.FailureReason!;

        // Say which path was scanned. Without it a reason like "Access to the path 'D:\Config.Msi'
        // is denied" (what the Ren'Py locator surfaces when a game folder holds no save directory)
        // is concrete but impossible to act on, because the path in it is not the one the user chose.
        if (report.GameInstallPath.Length > 0
            && !reason.Contains(report.GameInstallPath, StringComparison.OrdinalIgnoreCase))
        {
            reason = $"{reason}（扫描的安装路径：'{report.GameInstallPath}'）";
        }

        if (report.Succeeded)
        {
            var model = Build(nodesAfterScan);

            if (model.State == SaveTimelineState.NeverScanned)
            {
                return new SaveTimelineModel
                {
                    State = SaveTimelineState.NoSaves,
                    StatusText = "扫描完成，但没有找到可用的存档节点",
                    ErrorText = report.SaveDirectoryName is null
                        ? "解析器没有报告存档目录名，也没有找到 .save 文件。"
                        : $"存档目录 '{report.SaveDirectoryName}' 里没有可解析的 .save 文件。",
                };
            }

            var skipped = report.SkippedCount;
            var changed = report.AddedCount + report.UpdatedCount;

            var status = model.StatusText
                + $"；本次扫描 新增 {report.AddedCount} / 更新 {report.UpdatedCount} / 未变 {skipped}"
                + $"（{report.Elapsed.TotalSeconds:F1}s）";

            var hints = model.HintText is null
                ? new List<string>()
                : new List<string> { model.HintText };

            if (report.FailedCount > 0)
            {
                hints.Add($"{report.FailedCount} 个存档解析失败（原因见对应节点）");
            }

            if (report.StaleNodeCount > 0)
            {
                hints.Add($"{report.StaleNodeCount} 个已扫描节点在磁盘上已不存在，Galbox 不会自动删除它们");
            }

            if (report.GameInstallPath.Length > 0 && changed == 0 && skipped > 0)
            {
                hints.Add("存档目录没有变化，重复扫描不会产生重复节点");
            }

            return model with
            {
                StatusText = status,
                HintText = hints.Count == 0 ? null : string.Join("；", hints),
            };
        }

        var state = report.FailureKind switch
        {
            SaveNodeScanFailureKind.UnsupportedEngine => SaveTimelineState.UnsupportedEngine,
            SaveNodeScanFailureKind.SaveDirectoryNotFound => SaveTimelineState.NoSaves,
            SaveNodeScanFailureKind.GameNotFound => SaveTimelineState.NoGame,
            SaveNodeScanFailureKind.InvalidArgument => SaveTimelineState.NoGame,
            _ => SaveTimelineState.Failed,
        };

        var statusText = state switch
        {
            SaveTimelineState.UnsupportedEngine => "暂不支持该引擎的存档解析",
            SaveTimelineState.NoSaves => "没有找到存档文件",
            SaveTimelineState.NoGame => "无法确定要扫描哪个游戏",
            _ => "扫描失败",
        };

        // The existing nodes are still shown when there are any: a failed re-scan must not blank the
        // timeline the user already has.
        var previous = Build(nodesAfterScan);

        return previous with
        {
            State = state,
            StatusText = statusText,
            ErrorText = reason,
            HintText = previous.HasTimeline
                ? $"下面是上一次成功扫描的结果，共 {previous.Entries.Count} 个节点"
                : "没有可显示的数据，也没有编造任何数据",
        };
    }

    /// <summary>User-facing name of a failure category, printed next to the reason.</summary>
    public static string DescribeFailure(SaveNodeScanFailureKind kind) => kind switch
    {
        SaveNodeScanFailureKind.InvalidArgument => "参数无效",
        SaveNodeScanFailureKind.GameNotFound => "游戏不在库中",
        SaveNodeScanFailureKind.UnsupportedEngine => "暂不支持该引擎",
        SaveNodeScanFailureKind.SaveDirectoryNotFound => "找不到存档目录",
        SaveNodeScanFailureKind.AnalysisFailed => "解析过程出错",
        SaveNodeScanFailureKind.DatabaseFailure => "数据库写入失败",
        _ => "未知原因",
    };

    /// <summary>Groups the entries by their (speculative) route name, oldest group first.</summary>
    private static IReadOnlyList<string> BuildRouteGroups(IReadOnlyList<SaveNodeTimelineEntry> entries)
    {
        var groups = entries
            .Where(entry => entry.HasRoute)
            .GroupBy(entry => entry.RouteText!, StringComparer.Ordinal)
            .Select(group => new
            {
                Text = group.Key,
                Count = group.Count(),
                Earliest = group.Min(entry => entry.Node.SaveModifiedTime ?? DateTime.MaxValue),
            })
            .OrderBy(group => group.Earliest)
            .ThenBy(group => group.Text, StringComparer.Ordinal)
            .Select(group => $"{group.Text} — {group.Count} 个存档")
            .ToList();

        return groups;
    }

    /// <summary>Reads the evidence payload of a node, tolerating rows written by an older build.</summary>
    private static SaveNodeExtendedMetadata? ParseEvidence(SaveNode node)
        => SaveNodeExtendedMetadata.FromJson(node.ExtendedMetadataJson);

    /// <summary>Reads the JSON id array stored on a node.</summary>
    private static IReadOnlyList<string> ParseIdList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<string>();
        }

        try
        {
            var ids = JsonSerializer.Deserialize<string[]>(json);

            return ids is null
                ? Array.Empty<string>()
                : ids.OrderBy(id => id, StringComparer.Ordinal).ToList();
        }
        catch (JsonException)
        {
            // A corrupt payload must not blank the section; the count next to it is still real.
            return Array.Empty<string>();
        }
    }
}
