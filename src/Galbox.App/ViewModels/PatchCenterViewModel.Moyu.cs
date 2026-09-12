using System.Globalization;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Galbox.App.Services;
using Galbox.Core.Api;
using Galbox.Core.Patches;
using Galbox.Data.Entities;
using Microsoft.Extensions.Logging;

namespace Galbox.App.ViewModels;

/// <summary>How a moyu query ended. Deliberately one value per outcome: nothing here can be
/// reported as "no results" by accident.</summary>
public enum MoyuQueryState
{
    /// <summary>Nothing has been asked yet (no game selected, or no query run).</summary>
    NotQueried,

    /// <summary>The game carries no anchor the public face accepts, so no request was sent.</summary>
    NoAnchor,

    /// <summary>No <c>nmk_</c> API key is configured, so no request was sent.</summary>
    MissingKey,

    /// <summary>The query was answered and the service holds at least one patch page.</summary>
    Found,

    /// <summary>The query was answered and the service holds <b>no</b> patch page for this game.</summary>
    Empty,

    /// <summary>The query was attempted and failed (network, upstream, quota, contract).</summary>
    Failed
}

/// <summary>How the "wait for the download" hop ended.</summary>
public enum MoyuDownloadUiState
{
    /// <summary>No watch is running and none has run.</summary>
    Idle,

    /// <summary>The downloads folder is being watched for the chosen resource.</summary>
    Watching,

    /// <summary>A file was adopted and handed to the patch engine for preview.</summary>
    Adopted,

    /// <summary>The watch ended without a match — the user may never download it.</summary>
    NotAdopted,

    /// <summary>The watch could not run at all (missing folder, and so on).</summary>
    Failed
}

/// <summary>
/// The online half of the patch centre: discover a patch on moyu.moe, send the reader to its page,
/// adopt the file they downloaded, and hand it to the local engine.
///
/// <para>
/// <b>Why it looks like this.</b> The official NextMoe "moyu face" returns no download link — that is
/// the site's own design, so that links cannot be harvested in bulk — and the site's own endpoints
/// under <c>/api</c> are refused by its <c>robots.txt</c>. The only compliant flow is therefore
/// discovery through <see cref="MoyuApi"/>, a browser hop through
/// <see cref="MoyuBrowserLauncher"/>, adoption of the downloaded file by
/// <see cref="MoyuDownloadWatcher"/>, and finally <c>IPatchEngine</c> (through
/// <see cref="ILocalPatchService"/>), which owns extraction, the overwrite preview, the backup and
/// the rollback. This file invents none of those steps: it drives the four services and turns their
/// results into Chinese sentences.
/// </para>
///
/// <para>
/// <b>The four outcomes are four different states, not four different sentences.</b> "no key",
/// "no vndb id", "no results" and "the query failed" map onto <see cref="MoyuQueryState"/> values
/// and each has its own code, headline and body text. A caller - and the acceptance harness - can
/// branch on the state instead of pattern-matching a string, and a failure is never rendered through
/// the same surface as an empty result: the empty state has no error detail at all, and the failure
/// state always carries the service's own message.
/// </para>
///
/// <para>
/// <b>Nothing is sent when there is nothing to ask.</b> The anchor is resolved before the key is
/// looked at, and both checks happen before any HTTP call: a game without a vndb id produces
/// <see cref="MoyuQueryState.NoAnchor"/> with <b>zero</b> requests, and a missing key produces
/// <see cref="MoyuQueryState.MissingKey"/> with zero requests. There is no demo mode and no sample
/// data for the key-less case.
/// </para>
/// </summary>
public partial class PatchCenterViewModel
{
    /// <summary>Cancels the "wait for the download" hop when the user navigates away or gives up.</summary>
    private CancellationTokenSource? _moyuDownloadCts;

    /// <summary>
    /// The resource whose file was handed to the local engine, so the engine's manifest can record
    /// where the package came from (patch id, resource id, the page the user was sent to).
    /// </summary>
    private MoyuResource? _stagedMoyuResource;

    // ============================================================ 状态

    /// <summary>How the last query ended. The single source of truth for the status text.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MoyuStatusCodeText))]
    private MoyuQueryState _moyuState = MoyuQueryState.NotQueried;

    /// <summary>Machine-readable code shown in the status line, e.g. <c>MOYU_NOT_CONFIGURED</c>.</summary>
    public string MoyuStatusCodeText => MoyuState switch
    {
        MoyuQueryState.NoAnchor => "MOYU_NO_ANCHOR",
        MoyuQueryState.MissingKey => "MOYU_NOT_CONFIGURED",
        MoyuQueryState.Found => "MOYU_FOUND",
        MoyuQueryState.Empty => "MOYU_NO_RESULTS",
        MoyuQueryState.Failed => "MOYU_QUERY_FAILED",
        _ => "MOYU_NOT_QUERIED"
    };

    /// <summary>The line the card always shows: which state the query is in, and which one it is.</summary>
    [ObservableProperty]
    private string _moyuStatusMessage =
        "还没有查询这个作品的在线补丁。点「查找在线补丁」开始。";

    /// <summary>The explanatory paragraph under the status line. Non-empty in the three non-error states.</summary>
    [ObservableProperty]
    private string? _moyuAdvisoryMessage;

    /// <summary>
    /// Why the query failed, verbatim from the service. Only ever set for
    /// <see cref="MoyuQueryState.Failed"/>: an empty result carries no error detail, which is what
    /// keeps the two from looking alike.
    /// </summary>
    [ObservableProperty]
    private string? _moyuErrorDetail;

    /// <summary>True while a query is in flight. Drives the progress ring and the button's state.</summary>
    [ObservableProperty]
    private bool _isMoyuQuerying;

    /// <summary>
    /// The anchor the last query used (<c>vndb:v65869</c>), or null. Shown so a user can see which id
    /// the question was asked with.
    /// </summary>
    [ObservableProperty]
    private string? _moyuAnchorUsed;

    /// <summary>When the last query finished, for the "how fresh is this" line.</summary>
    [ObservableProperty]
    private string? _moyuLastQueryText;

    // ============================================================ 密钥

    /// <summary>
    /// What the key is, where to get it, and whether one is currently configured. Never the key.
    /// </summary>
    public string MoyuKeySummary => _moyuApi.IsConfigured
        ? $"已配置 nmk_ 密钥（来源：{DescribeKeySource()}）。查询会计入你自己的配额。"
        : "未配置 nmk_ 密钥。moyu 的官方公开面（NextMoe）要求每个使用者自备一把免费密钥，"
          + "密钥代表你自己、也消耗你自己的配额，所以 Galbox 不会内置一把共享密钥。";

    /// <summary>The two things a user has to know to unblock themselves.</summary>
    public string MoyuKeyHowTo =>
        "申请地址：https://developer.nextmoe.dev （免费自助铸造）。"
        + "在下面填入 nmk_ 开头的密钥即可；Galbox 用 Windows DPAPI 加密后存到本机，"
        + "不会写进日志、不会写进验收报告，也不会随游戏库一起备份。";

    /// <summary>True when a query can be attempted at all.</summary>
    public bool IsMoyuKeyConfigured => _moyuApi.IsConfigured;

    /// <summary>
    /// The key the user typed, held in memory only. It is written to the DPAPI store when they press
    /// the save button and is never read back into this property.
    /// </summary>
    [ObservableProperty]
    private string _moyuKeyInput = string.Empty;

    /// <summary>The outcome of the last key save, shown next to the box.</summary>
    [ObservableProperty]
    private string? _moyuKeySaveMessage;

    // ============================================================ 结果

    /// <summary>The patch pages found for the selected game.</summary>
    public System.Collections.ObjectModel.ObservableCollection<PatchMoyuRow> MoyuPatches { get; } = new();

    /// <summary>True when at least one patch page was found.</summary>
    [ObservableProperty]
    private bool _hasMoyuPatches;

    /// <summary>The patch the user picked, whose downloadable resources are listed below it.</summary>
    [ObservableProperty]
    private PatchMoyuRow? _selectedMoyuPatch;

    /// <summary>The live resources of <see cref="SelectedMoyuPatch"/>.</summary>
    public System.Collections.ObjectModel.ObservableCollection<PatchMoyuResourceRow> MoyuResources { get; } = new();

    /// <summary>True when the picked patch page has at least one live resource.</summary>
    [ObservableProperty]
    private bool _hasMoyuResources;

    /// <summary>Why a patch page has no resource rows, when that happens.</summary>
    [ObservableProperty]
    private string? _moyuResourcesEmptyReason;

    // ============================================================ 浏览器跳转与下载接管

    /// <summary>The last browser-hop outcome, in words.</summary>
    [ObservableProperty]
    private string? _moyuLaunchMessage;

    /// <summary>How the download hop ended.</summary>
    [ObservableProperty]
    private MoyuDownloadUiState _moyuDownloadState = MoyuDownloadUiState.Idle;

    /// <summary>The download hop's own report (what was watched, for how long, what matched).</summary>
    [ObservableProperty]
    private string? _moyuDownloadMessage;

    /// <summary>True while the downloads folder is being watched; disables the start button.</summary>
    [ObservableProperty]
    private bool _isMoyuWatchingDownload;

    /// <summary>
    /// How long a single download hop waits before it gives up. There is always a bound — a user who
    /// never downloads anything must not leave a watcher running. Nullable only so the acceptance
    /// harness can point it at a shorter one.
    /// </summary>
    public TimeSpan? MoyuDownloadTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// True when the user must be told that the discovery found resources but Galbox cannot tell the
    /// browser what to download — i.e. this hop is genuinely "open the page, then download it
    /// yourself", not "click here and it lands in Galbox".
    /// </summary>
    public string MoyuDownloadModelExplanation =>
        "moyu 的官方公开面按设计不返回下载直链（这样链接才不会被批量抓取）。"
        + "所以这一步是：Galbox 把你的默认浏览器打开到补丁页面，你在页面上下载文件；"
        + "下载完成后 Galbox 会在下载文件夹里认出这个文件并把路径交给本机补丁引擎做预览。";

    // ============================================================ 文案

    /// <summary>
    /// What this card is, always shown. It replaces the previous "在线补丁源：未实现" placeholder —
    /// there is no text anywhere on this page claiming the online source is unimplemented.
    /// </summary>
    public string MoyuSourceTitle => "在线补丁源：moyu.moe（官方公开面）";

    /// <summary>The one-paragraph description of the flow, shown above the button.</summary>
    public string MoyuSourceDescription =>
        "用作品的 vndb id 去 moyu.moe 的官方公开面（NextMoe /v2/moyu）查这个作品有哪些补丁，"
        + "列出名称、类型、体积、更新时间和来源页面；你选一个之后 Galbox 打开它的页面，"
        + "你在页面上把文件下载下来，Galbox 接管这个下载并把包交给本机补丁引擎安装。"
        + "Galbox 只调用这一个官方接口，不会去请求该站 robots.txt 里 Disallow 的 /api 路径。";

    /// <summary>Label of the query button.</summary>
    public string MoyuQueryButtonText => IsMoyuQuerying ? "正在查询..." : "查找在线补丁";

    /// <summary>Progress line reported by the download watcher (one per scan).</summary>
    [ObservableProperty]
    private string? _moyuWatchProgress;

    // ============================================================ 查询

    /// <summary>
    /// Exactly the outcomes a caller has to be able to tell apart, each with its own wording. Kept as
    /// one switch so no two states can drift into the same sentence.
    /// </summary>
    private void ApplyMoyuState(MoyuQueryState state, string? errorDetail = null)
    {
        MoyuState = state;

        (MoyuStatusMessage, MoyuAdvisoryMessage, MoyuErrorDetail) = state switch
        {
            MoyuQueryState.NotQueried => (
                "还没有查询这个作品的在线补丁。点「查找在线补丁」开始。",
                null,
                null),

            MoyuQueryState.NoAnchor => (
                "无法查询：该作品没有 vndb 标识。",
                "moyu 的公开面只接受 vndb:vXXXX 与 catalog:<id> 两种锚点，不接受 Bangumi 条目 ID —— "
                + "这是三个互不相通的 id 空间，任何换算都会给出看似确定、实际错误的补丁列表。"
                + "因此 Galbox 没有发出任何请求。要给这部作品补上 vndb id（刮削时选 VNDB 源）之后才能查询。",
                null),

            MoyuQueryState.MissingKey => (
                "未配置 nmk_ 密钥，无法查询。",
                "moyu 的官方公开面要求每个使用者自备一把 nmk_ API 密钥。"
                + "尚未配置，所以这次没有发出任何请求，也没有显示任何“示例数据”。",
                null),

            MoyuQueryState.Found => (
                $"找到 {MoyuPatches.Count} 个补丁页面。",
                "选一个补丁页面查看它的资源：先「打开补丁页」在浏览器里下载，再回来用「接管下载」把文件交给本机补丁引擎。",
                null),

            MoyuQueryState.Empty => (
                "查询成功，但没有找到补丁。",
                "moyu 的公开面对这个 vndb id 没有返回任何补丁页面（服务把它列在了 missing 里）。"
                + "这表示确实没有，不是查询出错 —— 你可以稍后再查，也可以直接在页面下方用「本地补丁包」安装一个已经下载好的补丁。",
                null),

            // The failure branch is the only one that carries the service's own message, and it never
            // shares a sentence with "no results".
            MoyuQueryState.Failed => (
                "查询失败：这次没有拿到结果。",
                "这次查询没有成功，所以“有没有补丁”这个问题仍然没有答案 —— 这不是“没有找到补丁”，请按下面的原因处理后重试。",
                errorDetail),

            _ => ("查询状态未知。", null, null)
        };

        OnPropertyChanged(nameof(MoyuStatusCodeText));
        OnPropertyChanged(nameof(MoyuQueryButtonText));
    }

    /// <summary>
    /// Queries moyu for the selected game and fills the list. Never throws for a service condition:
    /// every outcome becomes a state plus a sentence.
    /// </summary>
    /// <param name="game">
    /// The game to ask about. Null means <see cref="PatchCenterViewModel.SelectedGame"/>, which is what
    /// the button passes.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [RelayCommand]
    private async Task FindMoyuPatchesAsync(GameInfo? game = null, CancellationToken cancellationToken = default)
    {
        if (IsMoyuQuerying)
        {
            return;
        }

        var target = game ?? SelectedGame;
        if (target is null)
        {
            ApplyMoyuState(MoyuQueryState.NotQueried);
            MoyuStatusMessage = "请先在左侧选择一个游戏，然后再查询在线补丁。";
            return;
        }

        IsMoyuQuerying = true;
        OnPropertyChanged(nameof(MoyuQueryButtonText));
        ClearMoyuResults();

        try
        {
            // --- Step 1: the anchor. This is decided BEFORE any request is built, and it is a
            // separate condition from "no key": a game that cannot be asked about must not be
            // reported as an unconfigured client, and must not cost a request.
            var anchor = ResolveMoyuAnchor(target, out var anchorReason);
            if (anchor is null)
            {
                MoyuAnchorUsed = null;
                ApplyMoyuState(MoyuQueryState.NoAnchor);
                MoyuAdvisoryMessage = anchorReason;
                _logger.LogInformation(
                    "Moyu query skipped for game {GameId}: no vndb anchor (0 requests sent)", target.Id);
                return;
            }

            MoyuAnchorUsed = anchor.ToString();

            // --- Step 2: the key. Also decided before any request: MoyuApi would answer
            // NotConfigured without sending anything, and this branch keeps that outcome visible as
            // its own state rather than letting it fall through the generic failure path.
            if (!_moyuApi.IsConfigured)
            {
                ApplyMoyuState(MoyuQueryState.MissingKey);
                _logger.LogInformation(
                    "Moyu query skipped for game {GameId}: no nmk_ key configured (0 requests sent)", target.Id);
                return;
            }

            // --- Step 3: the one request this feature is allowed to make.
            var result = await _moyuApi
                .FindPatchesForGameAsync(target.VndbId, catalogWorkId: null, includeResources: true, cancellationToken)
                .ConfigureAwait(true);

            if (result.Failed)
            {
                // A failure stays a failure, with the service's own words attached and the upstream
                // code when there was one. It is never rendered as "no patches".
                var failure = result.Failure!;
                var detail = failure.Message;
                if (failure.HttpStatus is { } status)
                {
                    detail += $"（HTTP {status}";
                    detail += failure.UpstreamCode is { Length: > 0 } code ? $"，上游代码 {code}）" : "）";
                }

                if (failure.RetryAfter is { } retryAfter)
                {
                    detail += $" 服务建议约 {retryAfter.TotalSeconds:F0} 秒后重试。";
                }

                if (failure.Code == MoyuFailureCode.NotConfigured)
                {
                    // Raised by the client itself when the key disappeared between the check above
                    // and the call. The UI state stays "missing key" — it is not a query failure.
                    ApplyMoyuState(MoyuQueryState.MissingKey);
                    return;
                }

                ApplyMoyuState(MoyuQueryState.Failed, detail);
                _logger.LogWarning("Moyu query failed for game {GameId}: {Code}", target.Id, failure.Code);
                return;
            }

            var batch = result.Value!;
            foreach (var patch in batch.Items)
            {
                MoyuPatches.Add(new PatchMoyuRow(patch, this));
            }

            HasMoyuPatches = MoyuPatches.Count > 0;

            if (!HasMoyuPatches)
            {
                // A successful call with no rows is its own outcome. The anchors the service echoed
                // back are shown so the user can see the question was answered.
                var missing = batch.Missing.Count > 0 ? string.Join(", ", batch.Missing) : anchor.ToString();
                ApplyMoyuState(MoyuQueryState.Empty);
                MoyuAdvisoryMessage += $"（服务回显未命中锚点：{missing}）";
            }
            else
            {
                ApplyMoyuState(MoyuQueryState.Found);
                SelectMoyuPatch(MoyuPatches[0]);
            }
        }
        catch (OperationCanceledException)
        {
            ApplyMoyuState(MoyuQueryState.Failed, "查询已取消。");
        }
        catch (Exception ex)
        {
            // The ViewModel's own defect, not a service condition. Reported as a failure with the
            // exception named — never swallowed into an empty list.
            _logger.LogError(ex, "Unexpected error during the moyu query for game {GameId}", target.Id);
            ApplyMoyuState(MoyuQueryState.Failed, $"Galbox 内部错误：{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            IsMoyuQuerying = false;
            MoyuLastQueryText = $"上次查询：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}";
            OnPropertyChanged(nameof(MoyuQueryButtonText));
        }
    }

    /// <summary>
    /// Picks the anchor for a game, and explains the refusal in the terms the user can act on.
    /// </summary>
    /// <remarks>
    /// The VNDB id is the site's own dedupe key, so it is preferred. When there is none, this refuses
    /// <b>by name</b>: a Bangumi subject id is not a moyu anchor, and converting one would produce a
    /// confidently wrong patch list. The out parameter is only written when the answer is "no".
    /// </remarks>
    private static MoyuRef? ResolveMoyuAnchor(GameInfo game, out string? reason)
    {
        reason = null;

        if (!string.IsNullOrWhiteSpace(game.VndbId))
        {
            var fromField = MoyuRef.FromVndbId(game.VndbId);
            if (fromField is not null)
            {
                return fromField;
            }

            reason = $"作品记录里的 vndb id（\"{game.VndbId}\"）不是合法的 vndb 标识，"
                     + "因此没有查询，也没有发出任何请求。";
            return null;
        }

        // The scraping stage stores a VNDB hit in SourceId/SourceType as well as in VndbId; reading it
        // here recovers the anchor for rows scraped before VndbId existed.
        if (string.Equals(game.SourceType, "vndb", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(game.SourceId))
        {
            var fromSource = MoyuRef.FromVndbId(game.SourceId);
            if (fromSource is not null)
            {
                return fromSource;
            }
        }

        reason = string.Equals(game.SourceType, "bangumi", StringComparison.OrdinalIgnoreCase)
                 || !string.IsNullOrWhiteSpace(game.SourceId) && game.SourceType is not null
            ? "这部作品的来源是 Bangumi（条目 ID " + game.SourceId + "），而 moyu 的公开面不接受 Bangumi 条目 ID，"
              + "vndb / Bangumi / catalog 是三个不可互换的 id 空间。没有可用的锚点，所以 Galbox 没有发出任何请求。"
            : "这部作品既没有 vndb id，也没有可用的 catalog id。没有可用的锚点，所以 Galbox 没有发出任何请求。";

        return null;
    }

    /// <summary>Shows the resources of a found patch page.</summary>
    /// <remarks>
    /// Public because a row's own "查看资源" command routes back here: a row lives in a XAML
    /// DataTemplate, whose namescope resolves <c>{Binding}</c> against the row but cannot reach the
    /// page's ViewModel, so the row carries the reference instead.
    /// </remarks>
    [RelayCommand]
    public void SelectMoyuPatch(PatchMoyuRow? row)
    {
        MoyuResources.Clear();
        MoyuResourcesEmptyReason = null;

        SelectedMoyuPatch = row;

        if (row is null)
        {
            HasMoyuResources = false;
            return;
        }

        foreach (var resource in row.Patch.Resources)
        {
            MoyuResources.Add(new PatchMoyuResourceRow(resource, row, this));
        }

        HasMoyuResources = MoyuResources.Count > 0;
        if (!HasMoyuResources)
        {
            MoyuResourcesEmptyReason = row.Patch.ResourceCount > 0
                ? $"服务声明这个补丁页面有 {row.Patch.ResourceCount} 个资源，但这次没有随页面返回（include=resources 未带回）。"
                  + "这不表示没有资源，可以到补丁页面上直接查看。"
                : "这个补丁页面上没有资源记录。";
        }
    }

    /// <summary>
    /// Opens a patch page in the user's default browser. The URL has already passed
    /// <see cref="MoyuComplianceGuard.IsAllowedWebUrl"/> when the row was built, and the launcher
    /// guards it again; the button is disabled when there is no page URL, so it is never a dead button.
    /// </summary>
    /// <remarks>
    /// Public for the same reason as <see cref="SelectMoyuPatch"/>: the row's own command calls it,
    /// and a DataTemplate cannot bind to the page's ViewModel by name.
    /// </remarks>
    [RelayCommand]
    public void OpenMoyuPatchPage(PatchMoyuRow? row)
    {
        var url = row?.WebUrl ?? SelectedMoyuPatch?.WebUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            MoyuLaunchMessage = "这条记录没有可打开的补丁页面地址（上游没有给出 web_url）。";
            return;
        }

        var result = _moyuLauncher.Launch(url);

        MoyuLaunchMessage = result.Succeeded
            ? $"已在默认浏览器中打开补丁页面：{result.Url}\n"
              + "在这个页面上下载补丁文件。下载完成后回到这里点「接管下载」，Galbox 会把刚下载的文件交给本机补丁引擎。"
            : $"打开浏览器失败：{result.Message}";

        _logger.LogInformation("Moyu browser hop: succeeded={Succeeded}, url={Url}", result.Succeeded, result.Url);
    }

    /// <summary>
    /// Watches the downloads folder for the file the user is downloading right now, then hands the
    /// adopted path to the existing local patch flow (preview → install → rollback).
    ///
    /// <para>
    /// The watch always ends: it has a timeout, it can be cancelled, and it matches on the provider's
    /// declared size with the tolerance <see cref="MoyuDownloadWatcher"/> applies, because that field
    /// is a rounded display string. Files that were already in the folder are ignored.
    /// </para>
    /// </summary>
    [RelayCommand]
    public async Task AdoptMoyuDownloadAsync(PatchMoyuResourceRow? row)
    {
        if (IsMoyuWatchingDownload)
        {
            MoyuDownloadMessage = "已经有一个下载监视在运行了。等它结束，或者点「取消等待」。";
            return;
        }

        var resource = row?.Resource ?? SelectedMoyuPatch?.Patch.Resources.FirstOrDefault();
        if (resource is null)
        {
            MoyuDownloadState = MoyuDownloadUiState.Failed;
            MoyuDownloadMessage = "没有选中的资源，无法接管下载。请先在列表里选一个资源。";
            return;
        }

        var timeout = MoyuDownloadTimeout ?? TimeSpan.FromMinutes(10);

        using var linked = new CancellationTokenSource(timeout);
        _moyuDownloadCts = linked;

        IsMoyuWatchingDownload = true;
        MoyuDownloadState = MoyuDownloadUiState.Watching;
        MoyuDownloadMessage = null;
        MoyuWatchProgress = $"正在等待下载完成（最多 {timeout.TotalMinutes:F0} 分钟）...";

        try
        {
            var request = new MoyuDownloadWatchRequest
            {
                ExpectedSizeBytes = resource.SizeBytes > 0 ? resource.SizeBytes : null,
                NameHint = BuildNameHint(resource),
                PatchName = resource.DisplayName,
                WebUrl = resource.WebUrl,
                Timeout = timeout
            };

            var progress = new Progress<string>(line => MoyuWatchProgress = line);
            var outcome = await _moyuWatcher.WatchAsync(request, progress, linked.Token).ConfigureAwait(true);

            MoyuDownloadMessage = outcome.Reason;
            MoyuWatchProgress = null;

            switch (outcome.State)
            {
                case MoyuDownloadWatchState.Found when outcome.File is not null:
                    MoyuDownloadState = MoyuDownloadUiState.Adopted;
                    _stagedMoyuResource = resource;

                    // Hand the adopted file to the SAME local flow the file picker uses: the engine
                    // inspects and previews it, and nothing is written until the user installs.
                    var staged = await SelectPatchArchiveAsync(
                        outcome.File,
                        CancellationToken.None,
                        resource.DisplayName).ConfigureAwait(true);
                    if (!staged)
                    {
                        MoyuDownloadMessage += " 该文件已交给本机补丁引擎，但引擎没有给出可安装的预览（详见上方「本地补丁包」的说明）。";
                    }

                    break;

                case MoyuDownloadWatchState.Cancelled:
                    MoyuDownloadState = MoyuDownloadUiState.NotAdopted;
                    MoyuDownloadMessage = "已停止等待下载。文件已经下到本机的话，也可以用下面的「选择补丁压缩包...」手动指给 Galbox。";
                    break;

                case MoyuDownloadWatchState.TimedOut:
                    MoyuDownloadState = MoyuDownloadUiState.NotAdopted;
                    break;

                default:
                    MoyuDownloadState = MoyuDownloadUiState.Failed;
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            MoyuDownloadState = MoyuDownloadUiState.NotAdopted;
            MoyuDownloadMessage = "已停止等待下载。";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Adopting the moyu download failed");
            MoyuDownloadState = MoyuDownloadUiState.Failed;
            MoyuDownloadMessage = $"接管下载失败：{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            _moyuDownloadCts = null;
            IsMoyuWatchingDownload = false;
            MoyuWatchProgress = null;
        }
    }

    /// <summary>Stops the download watch in flight, if any.</summary>
    [RelayCommand]
    private void CancelMoyuDownload() => _moyuDownloadCts?.Cancel();

    /// <summary>
    /// A substring the downloaded file name is likely to contain, so a multi-gigabyte package (whose
    /// declared size cannot discriminate) is still matched by name.
    /// </summary>
    private static string? BuildNameHint(MoyuResource resource)
    {
        var hint = resource.DisplayName;
        if (string.IsNullOrWhiteSpace(hint) || hint.StartsWith("未命名资源", StringComparison.Ordinal))
        {
            hint = resource.LocalizationGroupName;
        }

        if (string.IsNullOrWhiteSpace(hint))
        {
            return null;
        }

        // The watcher matches a substring of the file name; a long display name with punctuation is
        // unlikely to appear verbatim, so only a leading run of reasonably safe characters is used.
        hint = new string(hint.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim();
        return hint.Length > 24 ? hint[..24] : hint;
    }

    /// <summary>Clears the query results without touching the state text.</summary>
    private void ClearMoyuResults()
    {
        MoyuPatches.Clear();
        MoyuResources.Clear();
        MoyuResourcesEmptyReason = null;
        SelectedMoyuPatch = null;
        HasMoyuPatches = false;
        HasMoyuResources = false;
        MoyuLaunchMessage = null;
        MoyuErrorDetail = null;
    }

    /// <summary>The key store's kind, in words. Never the key.</summary>
    private string DescribeKeySource() => _moyuKeyStore.Kind;

    /// <summary>
    /// Stores the key the user typed: DPAPI-encrypted on this machine, never logged, never shown
    /// back. The next query picks it up without a restart because the client reads the store-backed
    /// options on every call.
    /// </summary>
    [RelayCommand]
    private void SaveMoyuKey()
    {
        var candidate = (MoyuKeyInput ?? string.Empty).Trim();

        if (candidate.Length == 0)
        {
            MoyuKeySaveMessage = "没有输入密钥。要清除已保存的密钥，请先把输入框留空再点「清除密钥」。";
            return;
        }

        if (!candidate.StartsWith("nmk_", StringComparison.OrdinalIgnoreCase))
        {
            // Said out loud rather than saved silently: an obviously wrong value stored as if it were
            // fine would turn into a confusing 401 on the first query.
            MoyuKeySaveMessage = "这个值不像 nmk_ 开头的密钥，没有保存。请到 https://developer.nextmoe.dev 复制完整的密钥。";
            return;
        }

        try
        {
            _moyuKeyStore.Set(candidate);

            // Read back through the store so the message reports what was actually persisted, not
            // what was typed.
            var stored = _moyuKeyStore.Get();
            _moyuApi.Options.ApiKey = stored;
            _moyuApi.Options.ApiKeySource = _moyuKeyStore.Kind;

            MoyuKeyInput = string.Empty;
            MoyuKeySaveMessage = stored is null
                ? "保存失败：密钥没有写进本机存储。"
                : $"已保存（指纹 {IMoyuKeyStore.FingerprintOf(stored)}，存储位置 {_moyuKeyStore.Kind}）。现在可以查询在线补丁了。";

            if (_moyuApi.IsConfigured && MoyuState == MoyuQueryState.MissingKey)
            {
                // The blocking condition is gone; the card must stop claiming it is blocked.
                ApplyMoyuState(MoyuQueryState.NotQueried);
            }

            OnPropertyChanged(nameof(MoyuKeySummary));
            OnPropertyChanged(nameof(IsMoyuKeyConfigured));
            _logger.LogInformation("Moyu API key stored from the patch centre; configured={Configured}", stored is not null);
        }
        catch (Exception ex)
        {
            MoyuKeySaveMessage = $"保存密钥失败：{ex.GetType().Name}: {ex.Message}";
            _logger.LogError(ex, "Storing the moyu API key failed");
        }
    }

    /// <summary>Clears the stored key. Safe to call when nothing is stored.</summary>
    [RelayCommand]
    private void ClearMoyuKey()
    {
        _moyuKeyStore.Set(null);
        _moyuApi.Options.ApiKey = null;
        _moyuApi.Options.ApiKeySource = _moyuKeyStore.Kind;
        MoyuKeyInput = string.Empty;
        MoyuKeySaveMessage = "已清除本机保存的密钥。";
        ApplyMoyuState(MoyuQueryState.MissingKey);
        OnPropertyChanged(nameof(MoyuKeySummary));
        OnPropertyChanged(nameof(IsMoyuKeyConfigured));
    }

    /// <summary>
    /// The engine's source metadata for the resource a download was adopted from, so the install
    /// ledger records where the package came from. Null when the package did not come from moyu.
    /// </summary>
    internal PatchSourceInfo? StagedMoyuSourceInfo => _stagedMoyuResource?.ToPatchSourceInfo();
}

/// <summary>
/// One row of the online result list: one patch page on moyu, with the Chinese labels the page binds
/// to. The service object itself is kept intact as <see cref="Patch"/>.
/// </summary>
public sealed class PatchMoyuRow
{
    private readonly PatchCenterViewModel _owner;

    /// <summary>Creates a row bound to the page that will act on it.</summary>
    public PatchMoyuRow(MoyuPatch patch, PatchCenterViewModel owner)
    {
        Patch = patch;
        _owner = owner;
        RefreshCommand = new RelayCommand(() => _owner.SelectMoyuPatch(this));
        OpenPageCommand = new RelayCommand(
            () => _owner.OpenMoyuPatchPage(this),
            () => HasWebUrl);
    }

    /// <summary>The service object, unchanged.</summary>
    public MoyuPatch Patch { get; }

    /// <summary>Rebinds the resource list to this row. Bound in the DataTemplate, so it works from
    /// inside the template's own namescope (an ElementName lookup there does not resolve).</summary>
    public System.Windows.Input.ICommand RefreshCommand { get; }

    /// <summary>Opens this patch page in the browser. Disabled when the row carries no page URL.</summary>
    public System.Windows.Input.ICommand OpenPageCommand { get; }

    /// <summary>The patch page id on moyu.</summary>
    public string Id => Patch.Id;

    /// <summary>A name that is never blank: the upstream patch list carries no per-page name, so the
    /// row falls back to the anchor and the id rather than rendering an empty line.</summary>
    public string Name
    {
        get
        {
            var anchor = !string.IsNullOrWhiteSpace(Patch.VndbId) ? Patch.VndbId
                : !string.IsNullOrWhiteSpace(Patch.CatalogWorkId) ? $"catalog {Patch.CatalogWorkId}"
                : "无锚点";
            return $"moyu 补丁页 #{Patch.Id}（{anchor}）";
        }
    }

    /// <summary>Chinese labels for the declared patch kinds, or "未标注".</summary>
    public string TypesText => Patch.TypeLabels.Count > 0 ? string.Join("、", Patch.TypeLabels) : "未标注";

    /// <summary>Declared languages, or "未标注".</summary>
    public string LanguagesText => Patch.Languages.Count > 0 ? string.Join("、", Patch.Languages) : "未标注";

    /// <summary>How many resources the page holds.</summary>
    public string ResourceCountText => $"{Patch.ResourceCount} 个资源";

    /// <summary>When a resource on the page last changed — the only honest "may be updated" basis.</summary>
    public string UpdatedText => string.IsNullOrWhiteSpace(Patch.ResourceUpdatedAt)
        ? "更新时间未提供"
        : $"资源更新 {Patch.ResourceUpdatedAt}";

    /// <summary>The page address, or null when the row carries none or it failed the URL guard.</summary>
    public string? WebUrl => Patch.WebUrl;

    /// <summary>True when there is a page to open.</summary>
    public bool HasWebUrl => !string.IsNullOrWhiteSpace(Patch.WebUrl);

    /// <summary>Spelled out for the row: no page URL means no browser hop for this row.</summary>
    public string WebUrlText => HasWebUrl ? Patch.WebUrl! : "上游没有给出页面地址，无法打开";

    /// <summary>Release date as published.</summary>
    public string ReleaseText => string.IsNullOrWhiteSpace(Patch.ReleaseDate) ? "发售日未提供" : Patch.ReleaseDate!;
}

/// <summary>One resource row: the thing the user actually downloads from the opened page.</summary>
public sealed class PatchMoyuResourceRow
{
    private readonly PatchCenterViewModel _owner;

    /// <summary>Creates a row bound to the page that will act on it.</summary>
    public PatchMoyuResourceRow(MoyuResource resource, PatchMoyuRow patch, PatchCenterViewModel owner)
    {
        Resource = resource;
        Patch = patch;
        _owner = owner;

        OpenPageCommand = new RelayCommand(() => _owner.OpenMoyuPatchPage(patch), () => HasWebUrl);
        AdoptCommand = new RelayCommand(() => _ = _owner.AdoptMoyuDownloadAsync(this));
    }

    /// <summary>The service object, unchanged.</summary>
    public MoyuResource Resource { get; }

    /// <summary>The patch page this resource belongs to.</summary>
    public PatchMoyuRow Patch { get; }

    /// <summary>Opens the page the file is obtained from.</summary>
    public System.Windows.Input.ICommand OpenPageCommand { get; }

    /// <summary>Starts watching the downloads folder for this resource's file.</summary>
    public System.Windows.Input.ICommand AdoptCommand { get; }

    /// <summary>Never blank (the upstream name frequently is).</summary>
    public string DisplayName => Resource.DisplayName;

    /// <summary>Chinese labels for the declared kinds.</summary>
    public string TypesText => Resource.TypeLabels.Count > 0 ? string.Join("、", Resource.TypeLabels) : "未标注";

    /// <summary>
    /// The size as declared upstream, verbatim, plus the byte value used for matching — shown
    /// together so a user can see why a file of a slightly different size is still adopted.
    /// </summary>
    public string SizeText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Resource.SizeText))
            {
                return "来源未声明大小（按名称匹配）";
            }

            return Resource.SizeBytes > 0
                ? $"{Resource.SizeText}（按此大小 ±2% 匹配）"
                : Resource.SizeText;
        }
    }

    /// <summary>Where the publisher keeps the file: moyu's own store, or somewhere else.</summary>
    public string StorageText => Resource.IsExternallyHosted
        ? "发布者自持（可能要网盘提取码）"
        : "moyu 自有存储（s3）";

    /// <summary>When the file was last changed.</summary>
    public string UpdatedText => string.IsNullOrWhiteSpace(Resource.UpdatedAt) ? "更新时间未提供" : Resource.UpdatedAt!;

    /// <summary>True when this resource is a save rather than a patch over the game directory.</summary>
    public bool IsSaveType => MoyuPatchTypes.IsSaveType(Resource.Types);

    /// <summary>
    /// The routing note for a save-type resource. Saves are never routed through the overwrite flow:
    /// the engine refuses them by declared type, and the save-node workstream owns what happens
    /// instead. Saying so here is the difference between "routed elsewhere" and "silently ignored".
    /// </summary>
    public string? SaveTypeNotice => IsSaveType
        ? "这是全 CG 存档（type=save），不是覆盖式补丁：Galbox 的补丁引擎会按类型拒绝它，"
          + "所以它不能被「接管下载」当作补丁安装。请到存档管理里处理。"
        : null;

    /// <summary>The page address for this resource, or its patch page as a fallback.</summary>
    public string? WebUrl => Resource.WebUrl ?? Patch.Patch.WebUrl;

    /// <summary>True when there is a page to open.</summary>
    public bool HasWebUrl => !string.IsNullOrWhiteSpace(WebUrl);

    /// <summary>The address shown on the row.</summary>
    public string WebUrlText => WebUrl ?? "上游没有给出页面地址，无法打开";
}
