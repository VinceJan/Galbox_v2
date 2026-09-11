using System.Diagnostics;

namespace Galbox.Core.Api;

/// <summary>Options for <see cref="MoyuBrowserLauncher"/>.</summary>
public sealed class MoyuBrowserLauncherOptions
{
    /// <summary>
    /// When true nothing is started: the call validates the URL and reports what it would have done.
    /// Used by the acceptance check so the harness never opens a real browser window.
    /// </summary>
    public bool DryRun { get; set; }

    /// <summary>
    /// Test seam: the function that actually starts the process. Defaults to
    /// <see cref="Process.Start(ProcessStartInfo)"/> with <c>UseShellExecute = true</c>.
    /// </summary>
    public Func<ProcessStartInfo, Process?>? StartProcess { get; set; }
}

/// <summary>The outcome of a browser hop.</summary>
public sealed class MoyuLaunchResult
{
    /// <summary>True when the URL was accepted; in a dry run this means "would have opened".</summary>
    public required bool Succeeded { get; init; }

    /// <summary>The URL that was opened (or would have been).</summary>
    public string? Url { get; init; }

    /// <summary>Process id of the started browser helper, when one was started.</summary>
    public int? ProcessId { get; init; }

    /// <summary>True when nothing was actually started.</summary>
    public bool DryRun { get; init; }

    /// <summary>Why it did not succeed, or what happened.</summary>
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// Opens a patch page in the user's default browser.
///
/// <para>
/// <b>This is the download half of the integration, and it is not a workaround.</b> The official
/// moyu face returns no download link — deliberately, so that links cannot be harvested in bulk —
/// and it is <i>designed</i> to send a reader to the page instead. So Galbox does exactly that, and
/// then adopts the file the user downloads (<see cref="MoyuDownloadWatcher"/>). Nothing here tries
/// to obtain a direct URL by another route.
/// </para>
///
/// <para>
/// The URL is validated with <see cref="MoyuComplianceGuard.IsAllowedWebUrl"/> before it reaches the
/// shell: HTTPS only, the moyu site only, and nothing under the <c>Disallow</c>-ed <c>/api</c> path.
/// <c>UseShellExecute = true</c> without an <c>ArgumentList</c> is what makes the URL a document to
/// open rather than a command line to run, so a crafted URL cannot turn into argument injection.
/// </para>
/// </summary>
public sealed class MoyuBrowserLauncher
{
    private readonly MoyuBrowserLauncherOptions _options;

    /// <summary>Creates a launcher.</summary>
    public MoyuBrowserLauncher(MoyuBrowserLauncherOptions? options = null)
        => _options = options ?? new MoyuBrowserLauncherOptions();

    /// <summary>The options in force.</summary>
    public MoyuBrowserLauncherOptions Options => _options;

    /// <summary>
    /// True when <paramref name="url"/> is a page this launcher will open. Equivalent to
    /// <see cref="MoyuComplianceGuard.IsAllowedWebUrl"/>, exposed here so callers holding only a
    /// launcher can ask.
    /// </summary>
    public static bool ValidateWebUrl(string? url) => MoyuComplianceGuard.IsAllowedWebUrl(url);

    /// <summary>
    /// Opens <paramref name="url"/> in the default browser.
    /// </summary>
    /// <returns>
    /// A result describing what happened. A rejected URL is reported as
    /// <see cref="MoyuLaunchResult.Succeeded"/> = <c>false</c> with a reason — it is never treated as
    /// a successful no-op.
    /// </returns>
    public MoyuLaunchResult Launch(string? url)
    {
        if (!MoyuComplianceGuard.IsAllowedWebUrl(url))
        {
            return new MoyuLaunchResult
            {
                Succeeded = false,
                Url = url,
                DryRun = _options.DryRun,
                Message = string.IsNullOrWhiteSpace(url)
                    ? "没有可打开的补丁页面地址。"
                    : $"拒绝打开这个地址：只允许在浏览器中打开 https://www.moyu.moe/ 下的补丁页面，"
                      + $"不接受该站点 API 路径、其它主机或非 HTTPS 地址。收到：{url}"
            };
        }

        if (_options.DryRun)
        {
            return new MoyuLaunchResult
            {
                Succeeded = true,
                Url = url,
                DryRun = true,
                Message = "dry run：地址通过校验，未启动浏览器。"
            };
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = url!,
                UseShellExecute = true
            };

            var start = _options.StartProcess ?? (info => Process.Start(info));
            var process = start(startInfo);

            return new MoyuLaunchResult
            {
                Succeeded = true,
                Url = url,
                ProcessId = process?.Id,
                Message = "已在默认浏览器中打开补丁页面；下载完成后 Galbox 会自动接管该文件。"
            };
        }
        catch (Exception ex)
        {
            // A missing browser association must not look like success.
            return new MoyuLaunchResult
            {
                Succeeded = false,
                Url = url,
                DryRun = false,
                Message = $"无法启动浏览器：{ex.GetType().Name}: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Opens the page for a resource. The resource's own <c>web_url</c> is preferred; the patch page
    /// is the fallback when only that is known.
    /// </summary>
    public MoyuLaunchResult LaunchResource(MoyuResource? resource, string? fallbackPatchWebUrl = null)
    {
        var url = (resource is null ? null : MoyuApi.BuildWebUrl(resource)) ?? fallbackPatchWebUrl;
        return Launch(url);
    }
}
