using System.Diagnostics;

namespace Galbox.Core.Api;

/// <summary>How a watch ended.</summary>
public enum MoyuDownloadWatchState
{
    /// <summary>A file matching the expected size settled in the watched folder and was adopted.</summary>
    Found,

    /// <summary>The timeout elapsed with nothing matching. The user may simply not have downloaded it yet.</summary>
    TimedOut,

    /// <summary>The caller cancelled.</summary>
    Cancelled,

    /// <summary>The folder could not be watched at all (missing, unreadable).</summary>
    Failed
}

/// <summary>The outcome of one watch.</summary>
public sealed class MoyuDownloadWatchResult
{
    /// <summary>How it ended.</summary>
    public required MoyuDownloadWatchState State { get; init; }

    /// <summary>Absolute path of the adopted file, when <see cref="State"/> is Found.</summary>
    public string? File { get; init; }

    /// <summary>Size of the adopted file in bytes.</summary>
    public long FileSizeBytes { get; init; }

    /// <summary>How long the watch ran.</summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>
    /// Why it ended this way, and what was compared. Printed to the user, so it names the expected
    /// size and the folder.
    /// </summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>How many files were considered but not adopted, with the reason.</summary>
    public IReadOnlyList<string> Considered { get; init; } = Array.Empty<string>();

    /// <summary>True when a file was adopted.</summary>
    public bool Found => State == MoyuDownloadWatchState.Found;
}

/// <summary>Pacing and matching policy for <see cref="MoyuDownloadWatcher"/>.</summary>
public sealed class MoyuDownloadWatchOptions
{
    /// <summary>How often the folder is scanned. Downloads are seconds-scale, so this stays coarse.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long a file's size must stay unchanged before it counts as complete. Browsers write the
    /// destination file directly (the partial name is a separate <c>.crdownload</c> file), so a
    /// growing file is exactly what a download in progress looks like, and adopting one would hand
    /// the patch engine a truncated archive.
    /// </summary>
    public TimeSpan StabilityWindow { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Relative size tolerance when matching against the provider's reported size, as a fraction.
    /// The upstream field is a rounded binary-unit string, so an exact comparison would fail on
    /// almost everything; 2% is far smaller than any two different patches and far larger than the
    /// rounding error.
    /// </summary>
    public double SizeTolerance { get; set; } = 0.02;

    /// <summary>Extensions that are never a finished package, and are skipped outright.</summary>
    public IReadOnlyCollection<string> IgnoredExtensions { get; set; } = new[]
    {
        ".crdownload", ".part", ".partial", ".tmp", ".download", ".opdownload", ".!ut", ".aria2"
    };
}

/// <summary>What to watch for.</summary>
public sealed class MoyuDownloadWatchRequest
{
    /// <summary>
    /// Folder to watch. Null or blank means the user's configured download folder
    /// (<see cref="MoyuOptions.DownloadsFolder"/>).
    /// </summary>
    public string? DirectoryPath { get; set; }

    /// <summary>
    /// The provider's reported size in bytes (<c>MoyuResource.SizeBytes</c>). Files bigger than
    /// 2 GB are matched on size alone, because the upstream field is far too coarse to identify
    /// anything that large. 0 means "unknown", in which case <see cref="NameHint"/> is required to
    /// avoid adopting an unrelated download.
    /// </summary>
    public long? ExpectedSizeBytes { get; set; }

    /// <summary>
    /// Optional substring the file name should contain. Useful for a large patch whose size cannot
    /// discriminate, and required when <see cref="ExpectedSizeBytes"/> is unknown.
    /// </summary>
    public string? NameHint { get; set; }

    /// <summary>The patch's display name, for the report.</summary>
    public string? PatchName { get; set; }

    /// <summary>The page that was opened, for the report.</summary>
    public string? WebUrl { get; set; }

    /// <summary>How long to wait. There is always a bound: the user may never download anything.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Scan interval; keeps the deterministic test fast.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Matching policy overrides.</summary>
    public MoyuDownloadWatchOptions? Options { get; set; }

    /// <summary>
    /// Extension filter applied on top of the size match, e.g. <c>.rar</c>. Empty means "any
    /// extension".
    /// </summary>
    public IReadOnlyCollection<string> AllowedExtensions { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Adopts the file the user just downloaded from the page we opened.
///
/// <para>
/// <b>Why this exists.</b> The compliant path cannot give Galbox a download URL, so the download
/// happens in the user's browser and Galbox's job is to notice the result and hand it to the local
/// patch engine. That is the whole "downloads folder takeover": watch, match, hand over — and do
/// <i>nothing</i> else. No extraction, no overwrite, no guessing which game it belongs to; those
/// belong to <c>IPatchEngine</c>, which already does them with a preview, a ledger and a rollback.
/// </para>
///
/// <para>
/// <b>Matching.</b> Primarily by size, against the provider's <c>size</c> field, with a small
/// relative tolerance because that field is a rounded display string. BLAKE3 is preferable when the
/// row carries a <c>hash</c>, but the research report §8.3 lists its fill rate as unverified, so it
/// cannot be a precondition. Files that existed before the watch started are ignored, so a watch can
/// never adopt an older download — or the patch the user downloaded last week.
/// </para>
///
/// <para>
/// <b>A watch always ends.</b> There is a timeout, a cancellation token, and no background polling
/// once either fires: the user might never download anything, and a watcher that outlives the
/// interaction is both a resource leak and a privacy problem.
/// </para>
/// </summary>
public sealed class MoyuDownloadWatcher
{
    private readonly MoyuOptions? _options;

    /// <summary>Creates a watcher with no configured download folder.</summary>
    public MoyuDownloadWatcher()
    {
    }

    /// <summary>
    /// Creates a watcher that falls back to the configured download folder when a request does not
    /// name one.
    /// </summary>
    public MoyuDownloadWatcher(MoyuOptions options) => _options = options;

    /// <summary>
    /// Watches until a matching file settles, the timeout elapses, or the caller cancels.
    /// Never throws for a filesystem condition: a folder that cannot be watched is reported as
    /// <see cref="MoyuDownloadWatchState.Failed"/>.
    /// </summary>
    /// <param name="request">What to watch for.</param>
    /// <param name="progress">Optional progress reports (one per scan).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<MoyuDownloadWatchResult> WatchAsync(
        MoyuDownloadWatchRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var stopwatch = Stopwatch.StartNew();
        var policy = request.Options ?? new MoyuDownloadWatchOptions();
        var pollInterval = request.PollInterval > TimeSpan.Zero ? request.PollInterval : policy.PollInterval;
        var folder = ResolveFolder(request.DirectoryPath);
        var considered = new List<string>();

        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return new MoyuDownloadWatchResult
            {
                State = MoyuDownloadWatchState.Failed,
                Elapsed = stopwatch.Elapsed,
                Reason = string.IsNullOrWhiteSpace(folder)
                    ? "没有可监视的下载文件夹。"
                    : $"下载文件夹不存在：{folder}"
            };
        }

        // Snapshot first: only files that appear (or change) after this point may be adopted.
        var baseline = SnapshotFolder(folder, policy);
        progress?.Report($"监视 {folder}，等待大小约 {DescribeExpectedSize(request)} 的新文件（超时 {request.Timeout.TotalMinutes:F0} 分钟）");

        // file path -> (size seen at the first observation, time of that observation)
        var candidates = new Dictionary<string, (long Size, DateTimeOffset FirstSeenAt)>(StringComparer.OrdinalIgnoreCase);

        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new MoyuDownloadWatchResult
                {
                    State = MoyuDownloadWatchState.Cancelled,
                    Elapsed = stopwatch.Elapsed,
                    Considered = considered,
                    Reason = "监视已被取消。"
                };
            }

            if (stopwatch.Elapsed >= request.Timeout)
            {
                return new MoyuDownloadWatchResult
                {
                    State = MoyuDownloadWatchState.TimedOut,
                    Elapsed = stopwatch.Elapsed,
                    Considered = considered,
                    Reason = $"等待 {request.Timeout.TotalMinutes:F0} 分钟后仍未在 {folder} 中发现匹配的文件。"
                             + "如果浏览器还在下载，或者你把它存到了别处，可以从下载文件夹手动选择补丁包安装。"
                };
            }

            ScanOnce(folder, policy, request, baseline, candidates, considered, progress);

            // A stable candidate wins. Stability is measured from the first time this watch saw the
            // file, not from its last-write time, so a file whose timestamp is odd still settles.
            var now = DateTimeOffset.UtcNow;
            foreach (var (path, observation) in candidates.ToArray())
            {
                if (now - observation.FirstSeenAt < policy.StabilityWindow)
                {
                    continue;
                }

                long currentSize;
                try
                {
                    currentSize = new FileInfo(path).Length;
                }
                catch (Exception ex)
                {
                    considered.Add($"{Path.GetFileName(path)}：无法读取（{ex.GetType().Name}）");
                    candidates.Remove(path);
                    continue;
                }

                if (currentSize != observation.Size)
                {
                    // Still growing: restart its stability clock at the new size.
                    candidates[path] = (currentSize, now);
                    continue;
                }

                if (!Matches(path, currentSize, request, policy, considered))
                {
                    candidates.Remove(path);
                    continue;
                }

                return new MoyuDownloadWatchResult
                {
                    State = MoyuDownloadWatchState.Found,
                    File = path,
                    FileSizeBytes = currentSize,
                    Elapsed = stopwatch.Elapsed,
                    Considered = considered,
                    Reason = $"已接管 {Path.GetFileName(path)}（{MoyuSize.Format(currentSize)}，"
                             + $"与来源声明的 {DescribeExpectedSize(request)} 匹配）。"
                             + "该文件将交给本机补丁引擎做预览与安装，Galbox 不会自行解压或覆盖游戏目录。"
                };
            }

            try
            {
                await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new MoyuDownloadWatchResult
                {
                    State = MoyuDownloadWatchState.Cancelled,
                    Elapsed = stopwatch.Elapsed,
                    Considered = considered,
                    Reason = "监视已被取消。"
                };
            }
        }
    }

    /// <summary>
    /// One scan: promote newly appeared files to candidates and track their growth.
    /// </summary>
    private static void ScanOnce(
        string folder,
        MoyuDownloadWatchOptions policy,
        MoyuDownloadWatchRequest request,
        HashSet<string> baseline,
        Dictionary<string, (long Size, DateTimeOffset FirstSeenAt)> candidates,
        List<string> considered,
        IProgress<string>? progress)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(folder);
        }
        catch (Exception ex)
        {
            progress?.Report($"无法枚举 {folder}：{ex.GetType().Name}");
            return;
        }

        foreach (var path in files)
        {
            if (baseline.Contains(path) || candidates.ContainsKey(path))
            {
                continue;
            }

            var extension = Path.GetExtension(path);
            if (policy.IgnoredExtensions.Any(ignored =>
                    string.Equals(ignored, extension, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            long size;
            try
            {
                size = new FileInfo(path).Length;
            }
            catch (Exception)
            {
                continue;
            }

            candidates[path] = (size, DateTimeOffset.UtcNow);

            if (request.ExpectedSizeBytes is > 0)
            {
                considered.Add(
                    $"{Path.GetFileName(path)}：{MoyuSize.Format(size)}，"
                    + $"期望 {MoyuSize.Format(request.ExpectedSizeBytes.Value)}");
            }
            else
            {
                considered.Add($"{Path.GetFileName(path)}：{MoyuSize.Format(size)}（来源未声明大小，按名称匹配）");
            }
        }
    }

    /// <summary>
    /// The size gate. Kept in one place so the rule is auditable: a matching size within tolerance,
    /// or — for a package whose size the contract cannot express usefully — a name match.
    /// </summary>
    private static bool Matches(
        string path,
        long size,
        MoyuDownloadWatchRequest request,
        MoyuDownloadWatchOptions policy,
        List<string> considered)
    {
        var extension = Path.GetExtension(path);
        if (request.AllowedExtensions.Count > 0
            && !request.AllowedExtensions.Any(allowed =>
                string.Equals(allowed, extension, StringComparison.OrdinalIgnoreCase)))
        {
            considered.Add($"{Path.GetFileName(path)}：扩展名 {extension} 不在允许列表内，忽略");
            return false;
        }

        var nameMatchesHint = string.IsNullOrWhiteSpace(request.NameHint)
                              || Path.GetFileName(path).Contains(request.NameHint, StringComparison.OrdinalIgnoreCase);

        var expected = request.ExpectedSizeBytes ?? 0;

        if (expected <= 0)
        {
            if (string.IsNullOrWhiteSpace(request.NameHint))
            {
                // Nothing to match on. Adopting an arbitrary new file would be worse than waiting.
                considered.Add($"{Path.GetFileName(path)}：来源既没有可用大小也没有名称线索，不接管");
                return false;
            }

            considered.Add($"{Path.GetFileName(path)}：按名称线索接管（来源未声明可用大小）");
            return nameMatchesHint;
        }

        // A size the contract cannot express usefully for a multi-gigabyte package (the field is a
        // 3-decimal binary-unit string, so it cannot discriminate at that scale).
        if (expected > 2L * 1024 * 1024 * 1024)
        {
            considered.Add(
                $"{Path.GetFileName(path)}：期望 {MoyuSize.Format(expected)} 超过 2 GB，"
                + "大小不足以区分，改为按名称线索判断");
            return nameMatchesHint;
        }

        var tolerance = Math.Max(0, policy.SizeTolerance);
        var lower = expected * (1 - tolerance);
        var upper = expected * (1 + tolerance);

        if (size < lower || size > upper)
        {
            considered.Add(
                $"{Path.GetFileName(path)}：{MoyuSize.Format(size)} 不在期望的 "
                + $"{MoyuSize.Format((long)lower)}..{MoyuSize.Format((long)upper)} 区间内，忽略");
            return false;
        }

        if (!nameMatchesHint)
        {
            considered.Add($"{Path.GetFileName(path)}：大小匹配但名称不含线索 \"{request.NameHint}\"，暂不接管");
            return false;
        }

        return true;
    }

    /// <summary>Lists the files present before the watch begins, so they can never be adopted.</summary>
    private static HashSet<string> SnapshotFolder(string folder, MoyuDownloadWatchOptions policy)
    {
        var snapshot = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var path in Directory.EnumerateFiles(folder))
            {
                snapshot.Add(path);
            }
        }
        catch (Exception)
        {
            // An unreadable folder produces an empty snapshot; the watch will then consider every
            // file it can see, and the first scan reports the enumeration failure.
        }

        return snapshot;
    }

    /// <summary>The effective watch folder: the request's, else the configured one, else the user's.</summary>
    private string? ResolveFolder(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        return string.IsNullOrWhiteSpace(_options?.DownloadsFolder)
            ? MoyuDownloadsFolder.Resolve()
            : MoyuDownloadsFolder.ResolveForWatching(_options!.DownloadsFolder);
    }

    /// <summary>A phrase for the report describing what is being waited for.</summary>
    private static string DescribeExpectedSize(MoyuDownloadWatchRequest request)
    {
        if (request.ExpectedSizeBytes is > 0)
        {
            return MoyuSize.Format(request.ExpectedSizeBytes.Value);
        }

        return string.IsNullOrWhiteSpace(request.NameHint)
            ? "未知大小"
            : $"名称含 \"{request.NameHint}\" 的文件";
    }
}
