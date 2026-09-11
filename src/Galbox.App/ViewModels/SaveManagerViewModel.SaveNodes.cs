// The save-node half of SaveManagerViewModel: the "存档节点（游玩时间线）" section of the page.
//
// Kept in its own file so the feature's UI state stays reviewable on its own. Deliberately free of
// WinUI types (no DispatcherQueue, no Brush, no Page): Galbox.Acceptance's A10 constructs this
// ViewModel directly and drives the same code path the page's buttons call, which is the only way
// "the timeline really renders" can be measured instead of asserted in prose.
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Galbox.App.Saves;
using Galbox.Data.Entities;
using Galbox.Services.Saves;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Galbox.App.ViewModels;

/// <summary>Save-node scan, timeline, CG gallery and snapshot state of the save manager page.</summary>
public partial class SaveManagerViewModel
{
    /// <summary>Scope factory used to obtain one <see cref="ISaveNodeScanService"/> per scan.</summary>
    private IServiceScopeFactory? _scopeFactory;

    /// <summary>Game the currently displayed timeline belongs to (0 = none).</summary>
    private int _timelineGameId;

    /// <summary>Install path the currently displayed timeline was read for.</summary>
    private string _timelineInstallPath = string.Empty;

    /// <summary>Timeline entries (oldest save first); the page binds a horizontal ItemsControl to it.</summary>
    public ObservableCollection<SaveNodeTimelineEntry> TimelineItems { get; } = new();

    /// <summary>CG ids the parser saw unlocked.</summary>
    public ObservableCollection<string> CgUnlockedIds { get; } = new();

    /// <summary>CG ids still missing.</summary>
    public ObservableCollection<string> CgLockedIds { get; } = new();

    /// <summary>True while a scan is running.</summary>
    [ObservableProperty]
    private bool _isScanningSaveNodes;

    /// <summary>One-line state text of the section ("尚未扫描存档（点击「扫描存档」）", ...).</summary>
    [ObservableProperty]
    private string _saveNodeStatusText = SaveTimelineModel.NeverScanned.StatusText;

    /// <summary>Second status line (counts of parse problems, stale nodes, ...).</summary>
    [ObservableProperty]
    private string? _saveNodeHintText = SaveTimelineModel.NeverScanned.HintText;

    /// <summary>Concrete failure reason / unsupported-engine notice; null when there is nothing wrong.</summary>
    [ObservableProperty]
    private string? _saveNodeErrorText;

    /// <summary>True when <see cref="SaveNodeErrorText"/> must be shown.</summary>
    [ObservableProperty]
    private bool _hasSaveNodeError;

    /// <summary>True when the failure is "this engine has no parser" (different wording, no fake data).</summary>
    [ObservableProperty]
    private bool _saveNodeErrorIsUnsupportedEngine;

    /// <summary>True once a timeline exists.</summary>
    [ObservableProperty]
    private bool _hasTimeline;

    /// <summary>True when the section must draw its empty state instead of a timeline.</summary>
    [ObservableProperty]
    private bool _isTimelineEmpty = true;

    /// <summary>CG headline, e.g. "6 / 27 (22.2%)".</summary>
    [ObservableProperty]
    private string _cgHeadlineText = new SaveCgProgressView().HeadlineText;

    /// <summary>CG progress bar value, 0-100.</summary>
    [ObservableProperty]
    private double _cgProgressValue;

    /// <summary>True when the gallery size is known, so a figure may be shown at all.</summary>
    [ObservableProperty]
    private bool _hasCgProgress;

    /// <summary>Remaining CG count as text ("还差 21 张"), or the honest unknown wording.</summary>
    [ObservableProperty]
    private string _cgRemainingText = "未知";

    /// <summary>True when the scan stored the still-locked ids, so they can be listed.</summary>
    [ObservableProperty]
    private bool _hasCgLockedIds;

    /// <summary>Scene-progress headline, e.g. "122 / 345 (35.4%)".</summary>
    [ObservableProperty]
    private string _sceneProgressText = new SaveSceneProgressView().HeadlineText;

    /// <summary>Scene-progress bar value, 0-100.</summary>
    [ObservableProperty]
    private double _sceneProgressValue;

    /// <summary>True when a scene-progress denominator was parsed.</summary>
    [ObservableProperty]
    private bool _hasSceneProgress;

    /// <summary>Speculative route groups, one line each.</summary>
    [ObservableProperty]
    private string _routeGroupsText = "未发现任何疑似路线分组";

    /// <summary>True when at least one speculative route group exists.</summary>
    [ObservableProperty]
    private bool _hasRouteGroups;

    /// <summary>How many nodes carry a visible parse problem.</summary>
    [ObservableProperty]
    private int _parseProblemCount;

    /// <summary>Node currently selected in the timeline; the snapshot action targets it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanMarkSnapshot))]
    [NotifyPropertyChangedFor(nameof(SelectedNodeSummary))]
    private SaveNodeTimelineEntry? _selectedTimelineNode;

    /// <summary>User-typed snapshot description.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanMarkSnapshot))]
    private string _snapshotDescriptionInput = string.Empty;

    /// <summary>True when a node is selected and a description was typed.</summary>
    public bool CanMarkSnapshot =>
        SelectedTimelineNode is not null && !string.IsNullOrWhiteSpace(SnapshotDescriptionInput);

    /// <summary>One-line summary of the selected node, for the snapshot panel.</summary>
    public string SelectedNodeSummary => SelectedTimelineNode is null
        ? "先在时间线上点一个节点，再把它标记为快照"
        : $"{SelectedTimelineNode.DisplayName} · {SelectedTimelineNode.TimeText} · {SelectedTimelineNode.SlotKindText}";

    /// <summary>
    /// Attaches the scope factory used to resolve <see cref="ISaveNodeScanService"/> per scan.
    /// </summary>
    /// <remarks>
    /// Separate from the constructor so the ViewModel can still be constructed before the container
    /// exists (design-time and older call sites), and so a scan never holds a DbContext longer than
    /// the scan itself.
    /// </remarks>
    /// <param name="scopeFactory">Container scope factory.</param>
    public void AttachSaveNodeServices(IServiceScopeFactory scopeFactory)
        => _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));

    /// <summary>
    /// Reads the stored nodes of a game and refreshes the timeline (no scanning, no writes).
    /// </summary>
    /// <param name="gameInfoId">Library identifier of the game.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task LoadSaveNodesAsync(int gameInfoId, CancellationToken cancellationToken = default)
    {
        _timelineGameId = gameInfoId;

        if (_scopeFactory is null)
        {
            ApplyTimelineModel(SaveTimelineModel.NeverScanned);
            return;
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var service = scope.ServiceProvider.GetService<ISaveNodeScanService>();

            if (service is null)
            {
                ApplyTimelineModel(UnwiredModel());
                return;
            }

            var nodes = await service.GetNodesAsync(gameInfoId, cancellationToken).ConfigureAwait(true);

            // Reading the rows again is not the same as scanning: a game that was scanned by a
            // previous session shows its real timeline, and one that never was shows the empty state.
            ApplyTimelineModel(nodes.Count == 0
                ? SaveTimelineModel.NeverScanned
                : SaveNodeTimelineBuilder.Build(nodes));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ApplyTimelineModel(new SaveTimelineModel
            {
                State = SaveTimelineState.Failed,
                StatusText = "读取已保存的存档节点失败",
                ErrorText = $"读取存档节点失败：{ex.GetType().Name}: {ex.Message}",
            });
        }
    }

    /// <summary>
    /// Runs a save-node scan for one game and refreshes the timeline from the result.
    /// </summary>
    /// <param name="gameInfoId">Library identifier of the game.</param>
    /// <param name="gameInstallPath">Installation path to scan.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task ScanSaveNodesAsync(
        int gameInfoId, string? gameInstallPath, CancellationToken cancellationToken = default)
    {
        if (IsScanningSaveNodes)
        {
            return;
        }

        var installPath = gameInstallPath ?? string.Empty;

        IsScanningSaveNodes = true;
        _timelineGameId = gameInfoId;
        _timelineInstallPath = installPath;
        SaveNodeErrorText = null;

        try
        {
            if (_scopeFactory is null)
            {
                ApplyTimelineModel(UnwiredModel());
                return;
            }

            SaveNodeScanReport report;

            using (var scope = _scopeFactory.CreateScope())
            {
                var service = scope.ServiceProvider.GetService<ISaveNodeScanService>();

                if (service is null)
                {
                    ApplyTimelineModel(UnwiredModel());
                    return;
                }

                report = await service
                    .ScanAsync(gameInfoId, installPath, cancellationToken)
                    .ConfigureAwait(true);
            }

            // The report is turned into text by the same builder the timeline uses, so "why is this
            // red" has exactly one implementation.
            IReadOnlyList<SaveNode> nodes = Array.Empty<SaveNode>();

            if (report.Succeeded)
            {
                using var scope = _scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<ISaveNodeScanService>();
                nodes = await service.GetNodesAsync(gameInfoId, cancellationToken).ConfigureAwait(true);
            }
            else
            {
                // A failed scan must not blank an existing timeline; show what is already stored.
                using var scope = _scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetService<ISaveNodeScanService>();

                if (service is not null)
                {
                    nodes = await service.GetNodesAsync(gameInfoId, cancellationToken).ConfigureAwait(true);
                }
            }

            ApplyTimelineModel(SaveNodeTimelineBuilder.FromScanReport(report, nodes));
            _logger.LogInformation(
                "Save-node scan for game {GameId}: {Status} (nodes={Nodes})",
                gameInfoId,
                report.Succeeded ? "success" : $"failed/{report.FailureKind}",
                nodes.Count);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Unexpected: the service is documented never to throw for an expected failure. It still
            // has to be visible, with its real type and message, instead of a generic banner.
            _logger.LogError(ex, "Unexpected save-node scan failure for game {GameId}", gameInfoId);
            ApplyTimelineModel(new SaveTimelineModel
            {
                State = SaveTimelineState.Failed,
                StatusText = "扫描存档时发生异常",
                ErrorText = $"扫描存档异常：{ex.GetType().Name}: {ex.Message}",
            });
        }
        finally
        {
            IsScanningSaveNodes = false;
        }
    }

    /// <summary>
    /// Marks the selected timeline node as a key snapshot and stores the description.
    /// </summary>
    /// <param name="description">User-typed description; falls back to the field on the page.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when the snapshot was written.</returns>
    public async Task<bool> MarkSelectedNodeAsSnapshotAsync(
        string? description = null, CancellationToken cancellationToken = default)
    {
        var node = SelectedTimelineNode;
        if (node is null)
        {
            SaveNodeErrorText = "请先在时间线上选择一个存档节点。";
            HasSaveNodeError = true;
            SaveNodeErrorIsUnsupportedEngine = false;
            return false;
        }

        var text = (description ?? SnapshotDescriptionInput)?.Trim();

        if (string.IsNullOrEmpty(text))
        {
            SaveNodeErrorText = "快照描述不能为空：描述就是这个快照在时间线上的名字。";
            HasSaveNodeError = true;
            SaveNodeErrorIsUnsupportedEngine = false;
            return false;
        }

        try
        {
            using var scope = _scopeFactory!.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<GalboxDbContext>();

            var row = await db.SaveNodes
                .FirstOrDefaultAsync(n => n.Id == node.NodeId, cancellationToken)
                .ConfigureAwait(true);

            if (row is null)
            {
                SaveNodeErrorText = $"节点 #{node.NodeId} 已经不存在了，快照没有写入。";
                HasSaveNodeError = true;
                SaveNodeErrorIsUnsupportedEngine = false;
                return false;
            }

            row.IsSnapshot = true;
            row.SnapshotDescription = text;
            row.UpdatedTime = DateTime.UtcNow;

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            SaveNodeErrorText = $"保存快照失败：{ex.GetType().Name}: {ex.Message}";
            HasSaveNodeError = true;
            SaveNodeErrorIsUnsupportedEngine = false;
            return false;
        }

        SnapshotDescriptionInput = string.Empty;
        SuccessMessage = $"已把「{node.DisplayName}」标记为快照";

        // Re-read from the database so the timeline shows the stored state, not an optimistic guess.
        await LoadSaveNodesAsync(_timelineGameId, cancellationToken).ConfigureAwait(true);
        return true;
    }

    /// <summary>Removes the snapshot mark from the selected node (the description is kept).</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<bool> ClearSelectedSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var node = SelectedTimelineNode;
        if (node is null || _scopeFactory is null)
        {
            return false;
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<GalboxDbContext>();

            var row = await db.SaveNodes
                .FirstOrDefaultAsync(n => n.Id == node.NodeId, cancellationToken)
                .ConfigureAwait(true);

            if (row is null)
            {
                return false;
            }

            row.IsSnapshot = false;
            row.UpdatedTime = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            SaveNodeErrorText = $"取消快照失败：{ex.GetType().Name}: {ex.Message}";
            HasSaveNodeError = true;
            return false;
        }

        await LoadSaveNodesAsync(_timelineGameId, cancellationToken).ConfigureAwait(true);
        return true;
    }

    /// <summary>
    /// Scans the game currently selected in the page. This is what the "扫描存档" button calls.
    /// </summary>
    [RelayCommand]
    private async Task ScanSaveNodesForSelectedGameAsync()
    {
        if (SelectedGame is null)
        {
            SaveNodeErrorText = "请先在左侧选择一个游戏，再扫描它的存档。";
            HasSaveNodeError = true;
            SaveNodeErrorIsUnsupportedEngine = false;
            return;
        }

        var installPath = SelectedGame.InstallPath ?? string.Empty;

        if (string.IsNullOrWhiteSpace(installPath))
        {
            SaveNodeErrorText = "这个游戏没有记录安装路径，无法定位存档目录。";
            HasSaveNodeError = true;
            SaveNodeErrorIsUnsupportedEngine = false;
            return;
        }

        await ScanSaveNodesAsync(SelectedGame.Id, installPath).ConfigureAwait(true);
    }

    /// <summary>Marks the selected node as a snapshot (the page's snapshot button).</summary>
    [RelayCommand]
    private async Task MarkAsSnapshotAsync()
    {
        await MarkSelectedNodeAsSnapshotAsync().ConfigureAwait(true);
    }

    /// <summary>Clears the snapshot mark of the selected node.</summary>
    [RelayCommand]
    private async Task ClearSnapshotAsync()
    {
        await ClearSelectedSnapshotAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// The "the scan engine is not registered" state. It is a distinct state on purpose: it is a
    /// wiring defect, not a scan failure, and it shows no numbers at all.
    /// </summary>
    private static SaveTimelineModel UnwiredModel() => new()
    {
        State = SaveTimelineState.Failed,
        StatusText = "存档节点扫描服务不可用",
        ErrorText = "存档节点扫描服务没有注册到依赖注入容器（ISaveNodeScanService），因此没有扫描、也没有显示任何数据。",
    };

    /// <summary>Copies a built model into the observable state the page binds to.</summary>
    private void ApplyTimelineModel(SaveTimelineModel model)
    {
        TimelineItems.Clear();

        foreach (var entry in model.Entries)
        {
            TimelineItems.Add(entry);
        }

        CgUnlockedIds.Clear();
        CgLockedIds.Clear();

        foreach (var id in model.Cg.UnlockedIds)
        {
            CgUnlockedIds.Add(id);
        }

        foreach (var id in model.Cg.LockedIds)
        {
            CgLockedIds.Add(id);
        }

        HasTimeline = model.HasTimeline;
        IsTimelineEmpty = model.IsEmpty;
        SaveNodeStatusText = model.StatusText;
        SaveNodeHintText = model.HintText;

        var hasError = !string.IsNullOrWhiteSpace(model.ErrorText);
        SaveNodeErrorText = model.ErrorText;
        HasSaveNodeError = hasError;
        SaveNodeErrorIsUnsupportedEngine = model.State == SaveTimelineState.UnsupportedEngine;

        HasCgProgress = model.Cg.IsKnown;
        CgHeadlineText = model.Cg.HeadlineText;
        CgProgressValue = model.Cg.ProgressValue;
        CgRemainingText = model.Cg.RemainingCount is { } remaining
            ? $"还差 {remaining} 张"
            : "未知（没有解析到 CG 图鉴）";
        HasCgLockedIds = model.Cg.HasLockedIds;

        HasSceneProgress = model.Scene.IsKnown;
        SceneProgressText = model.Scene.HeadlineText;
        SceneProgressValue = model.Scene.ProgressValue;

        HasRouteGroups = model.HasRouteGroups;
        RouteGroupsText = model.HasRouteGroups
            ? string.Join(Environment.NewLine, model.RouteGroups)
            : "未发现任何疑似路线分组（保存数量不足以聚类，或每个存档的场景集合都不一样）";

        ParseProblemCount = model.ParseProblemCount;

        // The previous selection may point at an entry that no longer exists after a rebuild.
        SelectedTimelineNode = SelectedTimelineNode is null
            ? null
            : TimelineItems.FirstOrDefault(item => item.NodeId == SelectedTimelineNode.NodeId);
    }
}
