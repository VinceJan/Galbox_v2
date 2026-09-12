using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Galbox.App.Services;
using Galbox.Core.Patches;
using Microsoft.UI.Dispatching;
using Microsoft.Extensions.Logging;

namespace Galbox.App.ViewModels;

/// <summary>
/// The local patch-package workflow of the patch centre: pick a package the user already downloaded,
/// inspect it, preview every landing spot, install it with a real backup, roll it back, read the
/// ledger and recover an interrupted install.
///
/// This is a presentation shell only. Every decision - what gets refused, what gets backed up, what
/// the evidence class of a verdict is - is made by <see cref="ILocalPatchService"/> and the engine
/// underneath it; this file turns those results into Chinese text and observable collections.
///
/// Online patch sources are explicitly <b>not</b> part of this: there is no download command, no API
/// client and no key anywhere behind this page. The placeholder text says so.
/// </summary>
public partial class PatchCenterViewModel
{
    /// <summary>Conflict paths (relative) the user has explicitly approved for this preview.</summary>
    private readonly HashSet<string> _confirmedConflicts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Cancels the install currently in flight, when there is one.</summary>
    private CancellationTokenSource? _installCts;

    // ===================================================================== 选包与检查

    /// <summary>Path of the patch package the user picked.</summary>
    [ObservableProperty]
    private string? _selectedArchivePath;

    /// <summary>What the engine reported about the container (format, entry count, name encoding, ...).</summary>
    [ObservableProperty]
    private PatchArchiveInfo? _archiveInfo;

    /// <summary>One-line Chinese summary of <see cref="ArchiveInfo"/> for the header of the card.</summary>
    [ObservableProperty]
    private string? _archiveSummary;

    /// <summary>True when the container is a self-extracting executable: the engine never runs it.</summary>
    [ObservableProperty]
    private bool _requiresManualRun;

    /// <summary>What to do instead when <see cref="RequiresManualRun"/> is true.</summary>
    [ObservableProperty]
    private string? _manualRunAdvice;

    /// <summary>
    /// Why the package was refused outright, if it was. Shown <b>instead of</b> a preview: a refused
    /// package must never look like an empty success.
    /// </summary>
    [ObservableProperty]
    private string? _packageRejectionMessage;

    /// <summary>The suggested next step for a refused package, when the engine provides one.</summary>
    [ObservableProperty]
    private string? _packageRejectionRemedy;

    /// <summary>Non-fatal observations about the container.</summary>
    public System.Collections.ObjectModel.ObservableCollection<string> ArchiveWarnings { get; } = new();

    /// <summary>True when an <see cref="ArchiveInfo"/> has been produced.</summary>
    [ObservableProperty]
    private bool _hasArchiveInfo;

    /// <summary>True when the container carries non-fatal observations.</summary>
    [ObservableProperty]
    private bool _hasArchiveWarnings;

    /// <summary>True when a package path has been selected and can be (re-)previewed.</summary>
    [ObservableProperty]
    private bool _canPreview;

    /// <summary>Archive extensions the file picker offers (from the service, not hard-coded here).</summary>
    public IReadOnlyList<string> SupportedArchiveExtensions => _patchService.SupportedArchiveExtensions;

    // ===================================================================== 预览

    /// <summary>The non-throwing preview result, including the rejection code when it was refused.</summary>
    [ObservableProperty]
    private PatchPreviewAttempt? _previewAttempt;

    /// <summary>The full overwrite preview. Null until a package has been previewed successfully.</summary>
    [ObservableProperty]
    private OverwritePreview? _preview;

    /// <summary>True when a preview is available and the install button may be offered.</summary>
    [ObservableProperty]
    private bool _hasPreview;

    /// <summary>Chinese summary of what the preview would do.</summary>
    [ObservableProperty]
    private string? _previewHeadline;

    /// <summary>Files that will replace an existing, trusted file (each with a backup).</summary>
    public System.Collections.ObjectModel.ObservableCollection<PatchPreviewRow> OverwriteEntries { get; } = new();

    /// <summary>Files that do not exist yet.</summary>
    public System.Collections.ObjectModel.ObservableCollection<PatchPreviewRow> CreateEntries { get; } = new();

    /// <summary>Files whose current content has unknown provenance: the user must confirm each one.</summary>
    public System.Collections.ObjectModel.ObservableCollection<PatchPreviewRow> ConflictEntries { get; } = new();

    /// <summary>Files that are already byte identical: installing changes nothing.</summary>
    public System.Collections.ObjectModel.ObservableCollection<PatchPreviewRow> UnchangedEntries { get; } = new();

    /// <summary>
    /// Entries the engine refused. Kept in their own collection on purpose: they are not part of the
    /// "what will be written" numbers, and they are the only place a user can see what was blocked.
    /// </summary>
    public System.Collections.ObjectModel.ObservableCollection<RejectedArchiveEntry> RejectedEntries { get; } = new();

    /// <summary>Non-fatal observations produced while previewing.</summary>
    public System.Collections.ObjectModel.ObservableCollection<string> PreviewWarnings { get; } = new();

    /// <summary>True when the game already has files the patch would replace after a backup.</summary>
    [ObservableProperty]
    private bool _hasOverwriteEntries;

    /// <summary>True when the patch would add files.</summary>
    [ObservableProperty]
    private bool _hasCreateEntries;

    /// <summary>True when some targets have unknown provenance and need explicit approval.</summary>
    [ObservableProperty]
    private bool _hasConflictEntries;

    /// <summary>True when some entries were refused by the engine.</summary>
    [ObservableProperty]
    private bool _hasRejectedEntries;

    /// <summary>True when the preview produced non-fatal observations.</summary>
    [ObservableProperty]
    private bool _hasPreviewWarnings;

    /// <summary>Number of conflicts the user has explicitly approved.</summary>
    [ObservableProperty]
    private int _confirmedConflictCount;

    // ===================================================================== 安装

    /// <summary>True while an install is running.</summary>
    [ObservableProperty]
    private bool _isInstalling;

    /// <summary>Completion ratio in [0,1] of the running operation.</summary>
    [ObservableProperty]
    private double _installProgress;

    /// <summary>Human readable phase of the running operation.</summary>
    [ObservableProperty]
    private string? _installPhase;

    /// <summary>The full install result.</summary>
    [ObservableProperty]
    private PatchInstallResult? _installResult;

    /// <summary>Chinese summary of the install.</summary>
    [ObservableProperty]
    private string? _installSummary;

    /// <summary>Every per-file outcome, including the ones that did nothing.</summary>
    public System.Collections.ObjectModel.ObservableCollection<PatchFileOperationResult> InstallOperations { get; } = new();

    /// <summary>
    /// The subset that did not succeed. The page shows this list unconditionally, so a partial
    /// failure can never be hidden behind a single "installed" sentence.
    /// </summary>
    public System.Collections.ObjectModel.ObservableCollection<PatchFileOperationResult> FailedOperations { get; } = new();

    /// <summary>Fatal errors reported by the engine.</summary>
    public System.Collections.ObjectModel.ObservableCollection<string> InstallErrors { get; } = new();

    /// <summary>Non-fatal observations, including read-back mismatches.</summary>
    public System.Collections.ObjectModel.ObservableCollection<string> InstallWarnings { get; } = new();

    /// <summary>Conflicts that blocked an install because they were not confirmed.</summary>
    public System.Collections.ObjectModel.ObservableCollection<string> BlockingConflicts { get; } = new();

    /// <summary>True when at least one file failed or failed its read-back check.</summary>
    [ObservableProperty]
    private bool _hasFailedOperations;

    /// <summary>True when the engine reported fatal errors.</summary>
    [ObservableProperty]
    private bool _hasInstallErrors;

    /// <summary>True when the engine reported non-fatal observations.</summary>
    [ObservableProperty]
    private bool _hasInstallWarnings;

    /// <summary>True when the install was blocked by unconfirmed conflicts.</summary>
    [ObservableProperty]
    private bool _hasBlockingConflicts;

    // ===================================================================== 回滚

    /// <summary>Installs the ledger can still roll back, newest last.</summary>
    public System.Collections.ObjectModel.ObservableCollection<PatchRollbackCandidate> RollbackCandidates { get; } = new();

    /// <summary>True when the ledger offers at least one install to roll back.</summary>
    [ObservableProperty]
    private bool _hasRollbackCandidates;

    /// <summary>Result of the last rollback.</summary>
    [ObservableProperty]
    private PatchRollbackResult? _rollbackResult;

    /// <summary>Chinese summary of the last rollback.</summary>
    [ObservableProperty]
    private string? _rollbackSummary;

    // ===================================================================== 状态台账

    /// <summary>What Galbox's own ledger can prove about this game directory.</summary>
    [ObservableProperty]
    private PatchStatusReport? _statusReport;

    /// <summary>True when a status verdict is available.</summary>
    [ObservableProperty]
    private bool _hasStatusReport;

    /// <summary>Chinese headline of the verdict.</summary>
    [ObservableProperty]
    private string? _statusHeadline;

    /// <summary>
    /// The evidence line. It says out loud whether the verdict is a fact from Galbox's own ledger or
    /// an inference from directory heuristics, because those two must never look the same.
    /// </summary>
    [ObservableProperty]
    private string? _statusEvidenceText;

    /// <summary>Files recorded by the active manifest that no longer match their recorded hash.</summary>
    public System.Collections.ObjectModel.ObservableCollection<PatchFileVerification> ChangedSinceInstallFiles { get; } = new();

    /// <summary>Heuristic traces found in the game directory (inference only).</summary>
    public System.Collections.ObjectModel.ObservableCollection<HeuristicPatchTrace> HeuristicTraces { get; } = new();

    /// <summary>Caveats attached to the verdict.</summary>
    public System.Collections.ObjectModel.ObservableCollection<string> StatusNotes { get; } = new();

    /// <summary>True when a manifest exists and some of its files no longer match.</summary>
    [ObservableProperty]
    private bool _hasChangedSinceInstallFiles;

    /// <summary>True when the directory scan found third-party traces (inference only).</summary>
    [ObservableProperty]
    private bool _hasHeuristicTraces;

    // ===================================================================== 中断恢复

    /// <summary>Installs left pending by a crash or power loss, each with a dry-run recovery plan.</summary>
    public System.Collections.ObjectModel.ObservableCollection<PatchRecoveryPlan> InterruptedPlans { get; } = new();

    /// <summary>True when an interrupted install was detected and can be recovered.</summary>
    [ObservableProperty]
    private bool _hasInterruptedPlans;

    /// <summary>Result of the last recovery run.</summary>
    [ObservableProperty]
    private PatchRollbackResult? _recoveryResult;

    // ===================================================================== 命令

    /// <summary>Re-runs the preview for the package that is currently selected.</summary>
    [RelayCommand]
    private async Task PreviewArchiveAsync()
    {
        if (!string.IsNullOrEmpty(SelectedArchivePath))
        {
            await SelectPatchArchiveAsync(SelectedArchivePath).ConfigureAwait(true);
        }
    }

    /// <summary>Approves every conflict of the current preview.</summary>
    [RelayCommand]
    private void ConfirmConflicts() => ConfirmAllConflicts();

    /// <summary>Installs the previewed package.</summary>
    [RelayCommand(IncludeCancelCommand = true)]
    private async Task InstallPatchAsync(CancellationToken cancellationToken)
        => await InstallSelectedArchiveAsync(cancellationToken).ConfigureAwait(true);

    /// <summary>Rolls an install back; null means "the newest active install".</summary>
    [RelayCommand]
    private async Task RollbackPatchAsync(string? installId)
        => await RollbackAsync(installId).ConfigureAwait(true);

    /// <summary>Re-reads the ledger, the rollback candidates and the interrupted-install list.</summary>
    [RelayCommand]
    private async Task RefreshPatchStateAsync() => await RefreshPatchLedgerAsync().ConfigureAwait(true);

    /// <summary>Applies a recovery plan for an interrupted install.</summary>
    [RelayCommand]
    private async Task RecoverInterruptedAsync(PatchRecoveryPlan? plan)
    {
        if (plan is not null) await RecoverPlanAsync(plan).ConfigureAwait(true);
    }

    // ===================================================================== 工作流实现

    /// <summary>
    /// Inspects and previews a package for the selected game. Returns true when a preview is
    /// available; a refused package returns false <b>and</b> fills
    /// <see cref="PackageRejectionMessage"/>, so the refusal is visible rather than an empty page.
    /// </summary>
    /// <param name="archivePath">The package the user picked, or the file the online source adopted.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="moyuPatchName">
    /// Set only by the online source: the patch name to record in the install ledger when the package
    /// was discovered on moyu.moe. The local-file path passes nothing and keeps its previous wording.
    /// </param>
    public async Task<bool> SelectPatchArchiveAsync(
        string archivePath,
        CancellationToken ct = default,
        string? moyuPatchName = null)
    {
        ResetPatchWorkflow();

        if (string.IsNullOrWhiteSpace(archivePath))
        {
            ErrorMessage = "没有选择补丁包。";
            return false;
        }

        if (!File.Exists(archivePath))
        {
            ErrorMessage = $"补丁包不存在：{archivePath}";
            return false;
        }

        var game = SelectedGame;
        if (game is null)
        {
            ErrorMessage = "请先在左侧选择一个游戏。";
            return false;
        }

        var gameRoot = game.InstallPath;
        if (string.IsNullOrWhiteSpace(gameRoot) || !Directory.Exists(gameRoot))
        {
            ErrorMessage = $"游戏目录不可用，无法预览补丁：{gameRoot}";
            PackageRejectionMessage = ErrorMessage;
            return false;
        }

        SelectedArchivePath = archivePath;
        CanPreview = true;
        var gameId = GameIdOf(game.Id);

        try
        {
            ArchiveInfo = await _patchService.InspectAsync(archivePath, null, ct).ConfigureAwait(true);
            ArchiveSummary = DescribeArchive(ArchiveInfo);
            RequiresManualRun = ArchiveInfo.RequiresManualRun;
            ManualRunAdvice = ArchiveInfo.ManualRunAdvice;
            foreach (var warning in ArchiveInfo.Warnings) ArchiveWarnings.Add(warning);

            if (!ArchiveInfo.IsExtractable)
            {
                PackageRejectionMessage = RequiresManualRun
                    ? "这是一个自解压可执行文件（SFX）。Galbox 既不运行它，也不自动解压它。"
                    : $"容器格式 '{ArchiveInfo.FormatId}' 无法解压，补丁中心不能安装它。";
                PackageRejectionRemedy = ManualRunAdvice;
                _logger.LogWarning("Patch package {Path} is not extractable (kind={Kind})", archivePath, ArchiveInfo.Kind);
                return false;
            }

            // Where the package came from. For a file the user downloaded from a moyu page, this is
            // the site's own metadata (patch id, resource id and the page they were sent to), so the
            // ledger can say "installed from moyu.moe" rather than "local file".
            var options = _stagedMoyuResource is not null
                ? new PatchPreviewOptions
                {
                    PatchName = moyuPatchName ?? Path.GetFileNameWithoutExtension(archivePath),
                    Source = _stagedMoyuResource.ToPatchSourceInfo()
                }
                : PatchPreviewOptions.ForLocalFile(
                    moyuPatchName ?? Path.GetFileNameWithoutExtension(archivePath));

            var attempt = await _patchService
                .PreviewAsync(archivePath, gameRoot, gameId, options, ct)
                .ConfigureAwait(true);

            PreviewAttempt = attempt;

            if (!attempt.Succeeded || attempt.Preview is null)
            {
                PackageRejectionMessage = attempt.Message ?? "补丁包被引擎拒绝，未给出原因。";
                PackageRejectionRemedy = attempt.Remedy;
                _logger.LogWarning("Patch preview refused: {Code} - {Message}", attempt.RejectionCode, attempt.Message);
                return false;
            }

            ApplyPreview(attempt.Preview);
            _logger.LogInformation("Patch preview ready: {Headline}", PreviewHeadline);
            return true;
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = "预览已取消。";
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Previewing patch package {Path} failed", archivePath);
            ErrorMessage = $"预览补丁失败：{ex.Message}";
            PackageRejectionMessage = ErrorMessage;
            return false;
        }
    }

    /// <summary>Approves every conflict in the current preview.</summary>
    public void ConfirmAllConflicts()
    {
        _confirmedConflicts.Clear();
        foreach (var row in ConflictEntries)
        {
            _confirmedConflicts.Add(row.TargetRelativePath);
        }

        ConfirmedConflictCount = _confirmedConflicts.Count;
        if (ConfirmedConflictCount > 0)
        {
            SuccessMessage = $"已确认覆盖 {ConfirmedConflictCount} 个来历不明的文件；它们会先备份再写入。";
        }
    }

    /// <summary>Withdraws the conflict approvals (the install then stops before writing anything).</summary>
    public void ClearConflictConfirmations()
    {
        _confirmedConflicts.Clear();
        ConfirmedConflictCount = 0;
    }

    /// <summary>
    /// Installs the previewed package. Unconfirmed conflicts block the install entirely - that is the
    /// engine's rule, and this method deliberately does not work around it.
    /// </summary>
    public async Task<bool> InstallSelectedArchiveAsync(CancellationToken ct = default)
    {
        var preview = Preview;
        if (preview is null)
        {
            ErrorMessage = "还没有可安装的预览。";
            return false;
        }

        if (IsInstalling)
        {
            ErrorMessage = "已有安装正在进行。";
            return false;
        }

        IsInstalling = true;
        InstallProgress = 0;
        InstallPhase = "准备中";
        ErrorMessage = null;
        SuccessMessage = null;
        InstallResult = null;
        InstallSummary = null;
        InstallOperations.Clear();
        FailedOperations.Clear();
        InstallErrors.Clear();
        InstallWarnings.Clear();
        BlockingConflicts.Clear();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _installCts = linked;

        try
        {
            var decisions = new PatchInstallDecisions { ConfirmedConflicts = _confirmedConflicts.ToList() };
            var progress = new UiProgress(OnProgress);

            var result = await _patchService
                .InstallAsync(preview, decisions, progress, linked.Token)
                .ConfigureAwait(true);

            InstallResult = result;

            foreach (var operation in result.Operations) InstallOperations.Add(operation);
            foreach (var operation in result.Operations.Where(o => o.Status != PatchFileOperationStatus.Ok))
            {
                FailedOperations.Add(operation);
            }

            foreach (var error in result.Errors) InstallErrors.Add(error);
            foreach (var warning in result.Warnings) InstallWarnings.Add(warning);
            foreach (var conflict in result.BlockingConflicts) BlockingConflicts.Add(conflict);

            InstallSummary = DescribeInstall(result);

            if (result.Success)
            {
                SuccessMessage = InstallSummary;
                InstallPhase = "已完成";
                InstallProgress = 1;
                ClearConflictConfirmations();
            }
            else
            {
                // A failed install must say so, with the per-file reasons attached.
                ErrorMessage = InstallSummary;
                InstallPhase = "失败";
            }

            await RefreshPatchLedgerAsync(linked.Token).ConfigureAwait(true);
            RefreshWorkflowFlags();
            return result.Success;
        }
        catch (OperationCanceledException)
        {
            InstallPhase = "已取消";
            ErrorMessage = "安装已取消。已经写入的文件可能处于中间状态 —— 请用「回滚」恢复到安装前。";
            _logger.LogWarning("Patch install cancelled by the user");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Installing the previewed patch failed");
            ErrorMessage = $"安装失败：{ex.Message}";
            InstallErrors.Add(ErrorMessage);
            return false;
        }
        finally
        {
            _installCts = null;
            IsInstalling = false;
        }
    }

    /// <summary>Cancels the install currently in flight, if any.</summary>
    public void CancelInstall() => _installCts?.Cancel();

    /// <summary>Rolls back an install of the selected game.</summary>
    public async Task<bool> RollbackAsync(string? installId = null, CancellationToken ct = default)
    {
        var game = SelectedGame;
        if (game is null)
        {
            ErrorMessage = "请先选择一个游戏。";
            return false;
        }

        RollbackResult = null;
        RollbackSummary = null;

        try
        {
            var progress = new UiProgress(OnProgress);
            var result = await _patchService
                .RollbackAsync(game.InstallPath, GameIdOf(game.Id), installId, progress, ct)
                .ConfigureAwait(true);

            RollbackResult = result;
            RollbackSummary = DescribeRollback(result);

            if (result.Success)
            {
                SuccessMessage = RollbackSummary;
            }
            else
            {
                ErrorMessage = RollbackSummary;
            }

            await RefreshPatchLedgerAsync(ct).ConfigureAwait(true);
            return result.Success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Rolling back patch install {InstallId} failed", installId);
            ErrorMessage = $"回滚失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>Applies a dry-run recovery plan produced by <see cref="RefreshPatchLedgerAsync"/>.</summary>
    public async Task<bool> RecoverPlanAsync(PatchRecoveryPlan plan, bool apply = true, CancellationToken ct = default)
    {
        if (plan is null) return false;

        try
        {
            var progress = new UiProgress(OnProgress);
            var result = await _patchService.RecoverAsync(plan, apply, progress, ct).ConfigureAwait(true);
            RecoveryResult = result;

            if (result.Success)
            {
                SuccessMessage = $"恢复完成：还原 {result.RestoredCount} 个文件，删除 {result.DeletedCount} 个新增文件，"
                                 + (result.ByteIdenticalToPreInstall ? "内容与安装前逐字节一致。" : "但未能证明与安装前逐字节一致。");
            }
            else
            {
                ErrorMessage = $"恢复未成功：{string.Join(" | ", result.Errors)}";
            }

            await RefreshPatchLedgerAsync(ct).ConfigureAwait(true);
            return result.Success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Recovering interrupted install {InstallId} failed", plan.InstallId);
            ErrorMessage = $"恢复失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Re-reads the ledger for the selected game: the status verdict, the rollback candidates and the
    /// interrupted installs. Nothing here is inferred by the ViewModel; it is what the engine reports.
    /// </summary>
    public async Task RefreshPatchLedgerAsync(CancellationToken ct = default)
    {
        var game = SelectedGame;
        RollbackCandidates.Clear();
        InterruptedPlans.Clear();
        ChangedSinceInstallFiles.Clear();
        HeuristicTraces.Clear();
        StatusNotes.Clear();
        StatusReport = null;
        StatusHeadline = null;
        StatusEvidenceText = null;

        if (game is null || string.IsNullOrWhiteSpace(game.InstallPath) || !Directory.Exists(game.InstallPath))
        {
            RefreshWorkflowFlags();
            return;
        }

        var gameId = GameIdOf(game.Id);

        try
        {
            var status = await _patchService.GetStatusAsync(game.InstallPath, gameId, true, null, ct).ConfigureAwait(true);
            StatusReport = status;
            StatusHeadline = DescribeStatus(status);
            StatusEvidenceText = DescribeEvidence(status);

            foreach (var file in status.ChangedFiles) ChangedSinceInstallFiles.Add(file);
            foreach (var trace in status.Traces) HeuristicTraces.Add(trace);
            foreach (var note in status.Notes) StatusNotes.Add(note);

            foreach (var manifest in _patchService.ListRollbackCandidates(game.InstallPath, gameId))
            {
                RollbackCandidates.Add(PatchRollbackCandidate.FromManifest(manifest));
            }

            var plans = await _patchService.FindInterruptedAsync(game.InstallPath, gameId, ct).ConfigureAwait(true);
            foreach (var plan in plans) InterruptedPlans.Add(plan);

            RefreshWorkflowFlags();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reading the patch ledger for game {GameId} failed", gameId);
            ErrorMessage = $"读取补丁台账失败：{ex.Message}";
        }
    }

    // ===================================================================== 内部

    /// <summary>Clears everything that belonged to the previous package.</summary>
    private void ResetPatchWorkflow()
    {
        CanPreview = false;
        ArchiveInfo = null;
        ArchiveSummary = null;
        RequiresManualRun = false;
        ManualRunAdvice = null;
        PackageRejectionMessage = null;
        PackageRejectionRemedy = null;
        ArchiveWarnings.Clear();

        PreviewAttempt = null;
        Preview = null;
        HasPreview = false;
        PreviewHeadline = null;
        OverwriteEntries.Clear();
        CreateEntries.Clear();
        ConflictEntries.Clear();
        UnchangedEntries.Clear();
        RejectedEntries.Clear();
        PreviewWarnings.Clear();
        ClearConflictConfirmations();

        InstallResult = null;
        InstallSummary = null;
        InstallOperations.Clear();
        FailedOperations.Clear();
        InstallErrors.Clear();
        InstallWarnings.Clear();
        BlockingConflicts.Clear();

        RollbackResult = null;
        RollbackSummary = null;

        RefreshWorkflowFlags();
        ErrorMessage = null;
        SuccessMessage = null;
    }

    /// <summary>
    /// Recomputes the boolean view-state the page binds to. Kept explicit (instead of a converter on
    /// <c>Collection.Count</c>) so the page cannot end up in a state where a list is empty but its
    /// heading still claims there is something to look at.
    /// </summary>
    private void RefreshWorkflowFlags()
    {
        HasArchiveInfo = ArchiveInfo is not null;
        HasArchiveWarnings = ArchiveWarnings.Count > 0;
        HasPreview = Preview is not null;
        HasOverwriteEntries = OverwriteEntries.Count > 0;
        HasCreateEntries = CreateEntries.Count > 0;
        HasConflictEntries = ConflictEntries.Count > 0;
        HasRejectedEntries = RejectedEntries.Count > 0;
        HasPreviewWarnings = PreviewWarnings.Count > 0;
        HasFailedOperations = FailedOperations.Count > 0;
        HasInstallErrors = InstallErrors.Count > 0;
        HasInstallWarnings = InstallWarnings.Count > 0;
        HasBlockingConflicts = BlockingConflicts.Count > 0;
        HasRollbackCandidates = RollbackCandidates.Count > 0;
        HasStatusReport = StatusReport is not null;
        HasInterruptedPlans = InterruptedPlans.Count > 0;
        HasChangedSinceInstallFiles = ChangedSinceInstallFiles.Count > 0;
        HasHeuristicTraces = HeuristicTraces.Count > 0;
    }

    /// <summary>Turns an engine preview into the page's collections.</summary>
    private void ApplyPreview(OverwritePreview preview)
    {
        Preview = preview;
        HasPreview = true;

        foreach (var entry in preview.Entries)
        {
            var row = new PatchPreviewRow(entry);
            switch (entry.Action)
            {
                case PatchPreviewAction.Overwrite:
                    OverwriteEntries.Add(row);
                    break;
                case PatchPreviewAction.Create:
                    CreateEntries.Add(row);
                    break;
                case PatchPreviewAction.Conflict:
                    ConflictEntries.Add(row);
                    break;
                case PatchPreviewAction.Unchanged:
                    UnchangedEntries.Add(row);
                    break;
                default:
                    // Rejected entries arrive through preview.Rejected, never as a preview entry.
                    RejectedEntries.Add(new RejectedArchiveEntry
                    {
                        ArchiveEntryIndex = entry.ArchiveEntryIndex,
                        ArchiveEntryName = entry.ArchiveEntryName,
                        Code = PatchSecurityCode.Unspecified,
                        Reason = entry.AssessmentReason ?? "引擎未给出原因。"
                    });
                    break;
            }
        }

        foreach (var rejected in preview.Rejected) RejectedEntries.Add(rejected);
        foreach (var warning in preview.Warnings) PreviewWarnings.Add(warning);

        PreviewHeadline =
            $"将写入 {preview.Summary.CreateCount + preview.Summary.OverwriteCount + preview.Summary.ConflictCount} 个文件"
            + $"（新增 {preview.Summary.CreateCount}，覆盖已识别文件 {preview.Summary.OverwriteCount}，"
            + $"来历不明需确认 {preview.Summary.ConflictCount}，内容已相同 {preview.Summary.UnchangedCount}）；"
            + $"被拒绝 {preview.Summary.RejectedCount} 个条目；"
            + $"预计写入 {FormatBytes(preview.Summary.BytesToWrite)}，备份 {FormatBytes(preview.Summary.BytesToBackup)}。";

        RefreshWorkflowFlags();
    }

    /// <summary>Formats one <see cref="PatchArchiveInfo"/> for the page header.</summary>
    private static string DescribeArchive(PatchArchiveInfo info)
    {
        var encoding = $"{info.EffectiveNameEncoding}" + (info.NameEncodingWasAutoDetected ? "（自动探测）" : "（指定）");
        var text = $"格式 {info.FormatId}，容器 {FormatBytes(info.SizeBytes)}，"
                   + $"条目 {info.EntryCount} 个（文件 {info.FileEntryCount} 个），"
                   + $"解压后约 {FormatBytes(info.TotalUncompressedBytes)}，文件名编码 {encoding}";
        if (info.IsEncrypted) text += "，容器声明了加密条目";
        if (info.RequiresManualRun) text += "，自解压可执行文件";
        return text + "。";
    }

    /// <summary>Formats the install result, always naming the failed files explicitly.</summary>
    private static string DescribeInstall(PatchInstallResult result)
    {
        if (result.BlockingConflicts.Count > 0 && !result.Success)
        {
            return $"安装被拦住：{result.BlockingConflicts.Count} 个文件来历不明，未经你确认，引擎没有写入任何东西。";
        }

        var failed = result.Operations.Count(o => o.Status == PatchFileOperationStatus.Failed);
        var mismatched = result.Operations.Count(o => o.Status == PatchFileOperationStatus.ReadBackMismatch);
        var text = $"安装 {(result.Success ? "成功" : "未成功")}：新建 {result.CreatedCount} 个，覆盖 {result.OverwrittenCount} 个，"
                   + $"写入 {FormatBytes(result.BytesWritten)}，备份 {FormatBytes(result.BytesBackedUp)}。";

        if (failed > 0 || mismatched > 0)
        {
            text += $" 其中失败 {failed} 个、回读校验不符 {mismatched} 个 —— 详见下方逐文件结果。";
        }

        if (result.Errors.Count > 0)
        {
            text += $" 错误：{string.Join(" | ", result.Errors)}";
        }

        text += result.RollbackAvailable ? " 可以回滚。" : " 该次安装没有可用的备份。";
        return text;
    }

    /// <summary>Formats a rollback result.</summary>
    private static string DescribeRollback(PatchRollbackResult result)
    {
        if (result.NothingToDo) return "没有可回滚的记录。";

        var text = $"回滚 {(result.Success ? "成功" : "未完成")}：还原 {result.RestoredCount} 个文件，"
                   + $"删除本次新增 {result.DeletedCount} 个，跳过 {result.SkippedCount} 个。"
                   + (result.ByteIdenticalToPreInstall
                       ? " 已逐字节证明游戏目录回到安装前的状态。"
                       : " ⚠ 未能逐字节证明回到安装前状态，请检查下面逐条结果。");

        if (result.Errors.Count > 0) text += $" 错误：{string.Join(" | ", result.Errors)}";
        return text;
    }

    /// <summary>Chinese headline for a status verdict.</summary>
    private static string DescribeStatus(PatchStatusReport status) => status.Status switch
    {
        PatchStatus.Installed => $"已安装：{status.PatchName ?? "（未命名补丁）"}，且台账记录的每个文件哈希都与磁盘现况一致。",
        PatchStatus.ModifiedSinceInstall => $"安装后被改动：{status.PatchName ?? "（未命名补丁）"}，有 {status.ChangedFiles.Count} 个文件与台账记录不符。",
        PatchStatus.Interrupted => $"上次安装中断：{status.PatchName ?? "（未命名补丁）"}，可用「恢复」回到安装前状态。",
        PatchStatus.RolledBack => $"已回滚：{status.PatchName ?? "（未命名补丁）"}。",
        PatchStatus.Suspected => "疑似打过补丁：在游戏目录里发现了第三方痕迹，但 Galbox 的台账里没有任何记录。",
        PatchStatus.NotInstalled => "未安装：Galbox 台账中没有这个游戏的补丁记录，也没有发现可疑痕迹。",
        PatchStatus.NotApplicable => "不适用：这个包被声明为存档，不走覆盖安装。",
        _ => "无法判定：目录不可读，或台账记录有问题。"
    };

    /// <summary>
    /// The fact/inference firewall, in words. The engine guarantees that only its own ledger can
    /// produce <c>Installed</c> / <c>ModifiedSinceInstall</c> / <c>Interrupted</c> / <c>RolledBack</c>;
    /// this text makes that distinction visible instead of letting a guess look like a fact.
    /// </summary>
    private static string DescribeEvidence(PatchStatusReport status)
    {
        var scope = status.EvidenceScope switch
        {
            PatchEvidenceScope.GalboxLedger => "依据：Galbox 自己的安装台账 + 文件哈希复核",
            PatchEvidenceScope.GameDirectoryHeuristics => "依据：只读扫描游戏目录（无法归因到具体补丁）",
            PatchEvidenceScope.PackageMetadata => "依据：补丁包自身的元数据声明",
            _ => "依据：无"
        };

        var kind = status.Evidence switch
        {
            PatchEvidenceClass.Fact => "结论性质：事实",
            PatchEvidenceClass.Inference => "结论性质：启发式推测（不是事实）",
            _ => "结论性质：无证据"
        };

        return $"{kind} · {scope} · {status.Explanation}";
    }

    /// <summary>Renders an engine progress report into the two progress properties.</summary>
    private void OnProgress(PatchProgress progress)
    {
        InstallPhase = progress.Phase switch
        {
            "extract" => "解压",
            "backup" => "备份",
            "write" => "写入",
            "verify" => "回读校验",
            "rollback" => "回滚",
            "recover" => "恢复",
            _ => progress.Phase
        };

        if (progress.Message is { Length: > 0 })
        {
            InstallPhase = $"{InstallPhase}：{progress.Message}";
        }
        else if (progress.CurrentItem is { Length: > 0 })
        {
            InstallPhase = $"{InstallPhase}：{progress.CurrentItem}";
        }

        if (progress.Fraction is { } fraction) InstallProgress = fraction;
    }

    /// <summary>Game id used for the backup folder layout.</summary>
    private static string GameIdOf(int id) => id.ToString(CultureInfo.InvariantCulture);

    /// <summary>Human readable byte count.</summary>
    private static string FormatBytes(long bytes)
    {
        const long kb = 1024, mb = kb * 1024, gb = mb * 1024;
        if (bytes >= gb) return $"{bytes / (double)gb:F2} GB";
        if (bytes >= mb) return $"{bytes / (double)mb:F1} MB";
        if (bytes >= kb) return $"{bytes / (double)kb:F0} KB";
        return $"{bytes} B";
    }

    /// <summary>
    /// Returns the dispatcher of the current thread, or null when there is none.
    /// </summary>
    /// <remarks>
    /// <c>DispatcherQueue.GetForCurrentThread()</c> throws <c>COMException 0x80040154</c> when the
    /// Windows App SDK runtime is not initialised (measured in the headless acceptance host). A
    /// ViewModel whose property setter throws that is untestable, so the lookup is guarded and every
    /// caller has to cope with "no dispatcher" anyway.
    /// </remarks>
    private static DispatcherQueue? TryGetUiDispatcher()
    {
        try
        {
            return DispatcherQueue.GetForCurrentThread();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Runs an action on the UI thread when there is one, inline otherwise.</summary>
    private static void PostToUi(Action action)
    {
        var dispatcher = TryGetUiDispatcher();
        if (dispatcher is null || dispatcher.HasThreadAccess)
        {
            action();
            return;
        }

        if (!dispatcher.TryEnqueue(() => action()))
        {
            action();
        }
    }

    /// <summary>Marshals engine progress callbacks onto the UI thread.</summary>
    private sealed class UiProgress : IProgress<PatchProgress>
    {
        private readonly Action<PatchProgress> _onReport;

        public UiProgress(Action<PatchProgress> onReport) => _onReport = onReport;

        public void Report(PatchProgress value) => PostToUi(() => _onReport(value));
    }
}

/// <summary>
/// One row of the preview lists. Wraps the engine's <see cref="PatchPreviewEntry"/> and adds the
/// Chinese labels the page binds to; the engine object itself is kept intact as <see cref="Entry"/>.
/// </summary>
public sealed class PatchPreviewRow
{
    /// <summary>Creates a row.</summary>
    public PatchPreviewRow(PatchPreviewEntry entry) => Entry = entry;

    /// <summary>The engine's own preview line.</summary>
    public PatchPreviewEntry Entry { get; }

    /// <summary>Landing path relative to the game root - the thing the user confirms.</summary>
    public string TargetRelativePath => Entry.TargetRelativePath;

    /// <summary>Name as it appeared inside the archive.</summary>
    public string ArchiveEntryName => Entry.ArchiveEntryName;

    /// <summary>Chinese label for the planned action.</summary>
    public string ActionText => Entry.Action switch
    {
        PatchPreviewAction.Overwrite => "覆盖（先备份）",
        PatchPreviewAction.Create => "新增",
        PatchPreviewAction.Conflict => "冲突（需你确认）",
        PatchPreviewAction.Unchanged => "内容已相同",
        PatchPreviewAction.Rejected => "已拒绝",
        _ => "未知"
    };

    /// <summary>Chinese label for the provenance of the file that is there now.</summary>
    public string ProvenanceText => Entry.Provenance switch
    {
        PatchTargetProvenance.NotPresent => "目标不存在",
        PatchTargetProvenance.ManagedByGalbox => "此前由 Galbox 安装",
        PatchTargetProvenance.KnownOriginal => "识别为原版文件",
        PatchTargetProvenance.UnknownOrigin => "来历不明",
        _ => "未知"
    };

    /// <summary>Why the engine graded the line this way.</summary>
    public string? Reason => Entry.AssessmentReason;

    /// <summary>True when the user must approve this line before anything is written.</summary>
    public bool RequiresConfirmation => Entry.RequiresConfirmation;

    /// <summary>True when the archive name had to be normalised for Windows.</summary>
    public bool NameSanitized => Entry.NameSanitized;

    /// <summary>What was changed in the name.</summary>
    public string? SanitizationNote => Entry.SanitizationNote;

    /// <summary>Short hash pair for the row.</summary>
    public string HashText => Entry.ExistingSha256 is null
        ? $"→ {(Entry.IncomingSha256.Length >= 12 ? Entry.IncomingSha256[..12] : Entry.IncomingSha256)}"
        : $"{(Entry.ExistingSha256.Length >= 12 ? Entry.ExistingSha256[..12] : Entry.ExistingSha256)} → "
          + $"{(Entry.IncomingSha256.Length >= 12 ? Entry.IncomingSha256[..12] : Entry.IncomingSha256)}";
}

/// <summary>One rollbackable install as the rollback list shows it.</summary>
public sealed class PatchRollbackCandidate
{
    /// <summary>Ledger install identifier.</summary>
    public required string InstallId { get; init; }

    /// <summary>Patch name recorded at install time.</summary>
    public string? PatchName { get; init; }

    /// <summary>When the install happened.</summary>
    public DateTimeOffset? InstalledAt { get; init; }

    /// <summary>How many files the install touched.</summary>
    public int FileCount { get; init; }

    /// <summary>Journal state of the install.</summary>
    public required PatchManifestStatus Status { get; init; }

    /// <summary>Chinese label for the journal state.</summary>
    public string StatusText => Status switch
    {
        PatchManifestStatus.Committed => "已提交",
        PatchManifestStatus.Pending => "中断（未提交）",
        PatchManifestStatus.RolledBack => "已回滚",
        PatchManifestStatus.Failed => "失败",
        _ => "未知"
    };

    /// <summary>Builds a candidate from a ledger manifest.</summary>
    public static PatchRollbackCandidate FromManifest(PatchManifest manifest) => new()
    {
        InstallId = manifest.InstallId,
        PatchName = manifest.PatchName,
        InstalledAt = manifest.CommittedAt ?? manifest.CreatedAt,
        FileCount = manifest.Files.Count,
        Status = manifest.Status
    };
}
