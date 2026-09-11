using Galbox.App.Models;
using Galbox.App.Services;
using Galbox.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;

namespace Galbox.Acceptance.Checks;

/// <summary>
/// A73 - every diagnosis item is honest about what the application can do for it.
///
/// This is the check for the historical defect recorded in the product spec: a screen full of
/// "one-click repair" promises where nothing was implemented. Two rules are enforced:
///
///   1. classification - the four solution levels (Auto Fix / Manual Fix / External Tool /
///      No Fix Available) match the table in §4.4, <c>AutoFixAvailable</c> is true for exactly the
///      Auto Fix items, and every non-auto item still gives the user something actionable
///      (an official download page, or concrete manual steps);
///   2. the door really opens - for everything advertised as auto-fixable, the very call the UI
///      button makes must actually succeed; for everything else it must refuse instead of reporting
///      a success it did not achieve.
/// </summary>
public sealed class A73AutoFixHonestyCheck : IAcceptanceCheck
{
    /// <inheritdoc />
    public string Id => "A73";

    /// <inheritdoc />
    public string Title => "自动修复不虚标：标为可自动修复的项真的能修，其余项不得谎报成功";

    /// <summary>Solution level each of the eight categories must declare (product spec §4.4).</summary>
    private static readonly (string Label, GameErrorInfo Finding, SolutionType Expected)[] ExpectedTable =
    {
        ("路径含中文字符", GameErrorInfo.CreateChineseDirectoryError(0, @"D:\游戏\Probe", "游戏"), SolutionType.AutoFix),
        ("区域设置要求", GameErrorInfo.CreateLocaleRequirementError(0, "Japanese (Shift-JIS)"), SolutionType.ExternalTool),
        ("DirectX 缺失", GameErrorInfo.CreateDirectXMissingError(0, "d3dx9_43.dll", "9.0c"), SolutionType.ExternalTool),
        ("视频解码器缺失", GameErrorInfo.CreateKLiteCodecMissingError(0), SolutionType.ExternalTool),
        ("Windows 兼容性", GameErrorInfo.CreateWindowsCompatibilityError(0, "Windows XP/Vista", "Windows 7"), SolutionType.AutoFix),
        ("运行库缺失", GameErrorInfo.CreateRuntimeMissingError(0, "Visual C++", "2015-2022"), SolutionType.ExternalTool),
        ("权限问题", GameErrorInfo.CreatePermissionIssueError(0, @"C:\Program Files\Probe"), SolutionType.ManualFix),
        ("杀软拦截", GameErrorInfo.CreateAntivirusBlockingWarning(0, "game.exe"), SolutionType.ManualFix)
    };

    /// <inheritdoc />
    public async Task<CheckResult> RunAsync(AcceptanceContext context, CancellationToken cancellationToken)
    {
        var expected = "8 个诊断项的 SolutionType 与规格表一致；AutoFixAvailable 当且仅当 SolutionType=AutoFix；"
                     + "每个标为可自动修复的项调用 AttemptAutoFixAsync 必须真的成功；其余项必须返回失败而不是假装成功；"
                     + "非自动修复项各自带官方下载地址或可执行的手动步骤";

        var details = new List<string>();
        var failures = new List<string>();
        var errorChecking = context.Get<IErrorCheckingService>();
        var work = AcceptanceWork.Create("a73");
        var createdGameIds = new List<int>();

        details.Add("=== 用例 1：解决方式分级与实际修复能力一致 ===");

        foreach (var (label, finding, solutionType) in ExpectedTable)
        {
            var classificationOk = finding.SolutionType == solutionType;
            var flagOk = finding.AutoFixAvailable == (solutionType == SolutionType.AutoFix);

            details.Add($"  [{(classificationOk && flagOk ? "OK  " : "FAIL")}] {label,-14} 规格={solutionType,-12} "
                      + $"实测={finding.SolutionType,-12} AutoFixAvailable={finding.AutoFixAvailable}");

            if (!classificationOk)
            {
                failures.Add($"{label}: 规格要求 {solutionType}，实测 {finding.SolutionType}");
            }

            if (!flagOk)
            {
                failures.Add($"{label}: AutoFixAvailable={finding.AutoFixAvailable} 与 SolutionType={finding.SolutionType} 不一致");
            }

            // Every item must leave the user with a way forward.
            switch (solutionType)
            {
                case SolutionType.AutoFix:
                    if (string.IsNullOrWhiteSpace(finding.FixAction))
                    {
                        failures.Add($"{label}: 标为自动修复却没有 FixAction");
                    }

                    break;

                case SolutionType.ExternalTool:
                    var url = finding.DownloadUrl;
                    if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        failures.Add($"{label}: 需外部工具但没有 https 下载地址（实测 {url ?? "(空)"}）");
                    }

                    if (string.IsNullOrWhiteSpace(finding.ToolName))
                    {
                        failures.Add($"{label}: 需外部工具但没有工具名");
                    }

                    break;

                case SolutionType.ManualFix:
                    if (string.IsNullOrWhiteSpace(finding.SolutionInstructions))
                    {
                        failures.Add($"{label}: 手动修复却没有给出步骤");
                    }

                    break;
            }

            details.Add($"          FixAction=\"{finding.FixAction ?? "(null)"}\" Tool=\"{finding.ToolName ?? "(null)"}\" Url={finding.DownloadUrl ?? "(null)"}");
        }

        details.Add(string.Empty);
        details.Add("=== 用例 2：非自动修复项调用修复接口必须如实返回失败 ===");

        foreach (var (label, finding, solutionType) in ExpectedTable)
        {
            if (solutionType == SolutionType.AutoFix)
            {
                continue;
            }

            var result = await errorChecking.AttemptAutoFixAsync(finding, cancellationToken).ConfigureAwait(false);
            details.Add($"  [{(result.Success ? "FAIL" : "OK  ")}] {label,-14} Success={result.Success} Message=\"{result.Message}\"");
            if (result.Success)
            {
                failures.Add($"{label}: 没有实现修复却返回成功");
            }

            if (string.IsNullOrWhiteSpace(result.Message))
            {
                failures.Add($"{label}: 失败时没有给出任何说明");
            }
        }

        // The refusal must not depend on the caller being honest: a finding that wrongly claims
        // AutoFixAvailable=true must still not produce a fake success.
        var mislabelled = GameErrorInfo.CreateDirectXMissingError(0, "d3dx9_43.dll", "9.0c");
        mislabelled.AutoFixAvailable = true;
        var mislabelledResult = await errorChecking.AttemptAutoFixAsync(mislabelled, cancellationToken).ConfigureAwait(false);
        details.Add($"  [{(mislabelledResult.Success ? "FAIL" : "OK  ")}] 被错误标记为可自动修复的 DirectX 项 Success={mislabelledResult.Success} "
                  + $"Message=\"{mislabelledResult.Message}\"");
        if (mislabelledResult.Success)
        {
            failures.Add("DirectX 项被错误标记为可自动修复后，修复接口谎报了成功");
        }

        details.Add(string.Empty);
        details.Add("=== 用例 3：标为可自动修复的项，真的能被修好（界面上那个按钮真的有门） ===");

        var folder = Path.Combine(work, "中文游戏目录");
        var exePath = AcceptanceWork.WritePattern(Path.Combine(folder, "probe.exe"), 512, 0x4D);
        AcceptanceWork.WriteText(
            Path.Combine(folder, "readme.txt"),
            "SYSTEM REQUIREMENTS: Windows XP SP3 required.");

        var gameId = await HealthCheckSupport
            .InsertGameAsync(context, "A73 修复能力探针", folder, exePath, null, cancellationToken)
            .ConfigureAwait(false);
        createdGameIds.Add(gameId);

        var diagnosedPaths = new List<string> { exePath };

        try
        {
            var game = await LoadGameAsync(context, gameId, cancellationToken).ConfigureAwait(false);
            var findings = await errorChecking.CheckGameAsync(game, cancellationToken).ConfigureAwait(false);

            details.Add($"  诊断结果 : {string.Join(", ", findings.Select(f => $"{f.Category}({f.SolutionType})"))}");

            foreach (var finding in findings)
            {
                var result = await errorChecking.AttemptAutoFixAsync(finding, cancellationToken).ConfigureAwait(false);
                var advertised = finding.AutoFixAvailable;
                var consistent = result.Success == advertised;

                details.Add($"  [{(consistent ? "OK  " : "FAIL")}] {finding.Category,-22} 宣称可自动修复={advertised} "
                          + $"尝试结果 Success={result.Success} Message=\"{result.Message}\"");

                if (!consistent)
                {
                    failures.Add($"{finding.Category}: 宣称可自动修复={advertised}，但尝试修复返回 Success={result.Success}（{result.Message}）");
                }

                if (result.Success)
                {
                    var gameAfter = await LoadGameAsync(context, gameId, cancellationToken).ConfigureAwait(false);
                    diagnosedPaths.Add(gameAfter.MainExecutable);
                }
            }

            if (!findings.Any(f => f.AutoFixAvailable))
            {
                failures.Add("用例3：fixture 没有产生任何可自动修复的问题，因此没有证明「按钮真的有门」");
            }

            // Re-diagnosis: everything that was fixed must be gone.
            var gameFinal = await LoadGameAsync(context, gameId, cancellationToken).ConfigureAwait(false);
            var remaining = await errorChecking.CheckGameAsync(gameFinal, cancellationToken).ConfigureAwait(false);
            var remainingAuto = remaining.Where(f => f.AutoFixAvailable).ToList();

            details.Add($"  修复后仍报出的可自动修复项 : {(remainingAuto.Count == 0 ? "无（OK）" : string.Join(", ", remainingAuto.Select(f => f.Category.ToString())))}");
            if (remainingAuto.Count > 0)
            {
                failures.Add($"用例3：修复后复诊仍报出可自动修复项 {string.Join(", ", remainingAuto.Select(f => f.Category.ToString()))}");
            }
        }
        finally
        {
            await HealthCheckSupport.DeleteGamesAsync(context, CancellationToken.None, createdGameIds.ToArray())
                .ConfigureAwait(false);
            CleanUpCompatibilityLayers(diagnosedPaths, details);
        }

        details.Add(string.Empty);
        details.Add("=== 用例 4：报告页的「一键修复所有可自动修复的问题」（真实 ViewModel + 真实服务） ===");

        var batchFolder = Path.Combine(work, "batch", "中文游戏目录");
        var batchExe = AcceptanceWork.WritePattern(Path.Combine(batchFolder, "probe.exe"), 512, 0x4D);
        AcceptanceWork.WriteText(
            Path.Combine(batchFolder, "readme.txt"),
            "SYSTEM REQUIREMENTS: Windows XP SP3 required.");

        var batchGameId = await HealthCheckSupport
            .InsertGameAsync(context, "A73 批量修复探针", batchFolder, batchExe, null, cancellationToken)
            .ConfigureAwait(false);
        createdGameIds.Add(batchGameId);

        var batchViewModel = HealthCheckSupport.TryCreateReportViewModel(context, details);
        if (batchViewModel is null)
        {
            failures.Add("用例4：无法构造 ErrorReportViewModel，未能验证界面上的批量修复入口");
        }
        else
        {
            try
            {
                var batchGame = await LoadGameAsync(context, batchGameId, cancellationToken).ConfigureAwait(false);

                // Exactly what the page does when the user picks the game on the left.
                var (checkOk, _, checkError) = await ReflectionBridge
                    .CallAsync(batchViewModel, "CheckGameErrorsAsync", batchGame)
                    .ConfigureAwait(false);

                details.Add($"  CheckGameErrorsAsync ok={checkOk}{(checkError is null ? string.Empty : $" ({checkError})")}");
                details.Add($"  按钮文案         : \"{ReflectionBridge.String(batchViewModel, "FixAllButtonText")}\"");

                var fixableBefore = ReflectionBridge.Int(batchViewModel, "AutoFixableCount");

                // Exactly what the "一键修复所有符合自动修复条件的问题" button calls.
                var (batchOk, batchResult, batchError) = await ReflectionBridge
                    .CallAsync(batchViewModel, "FixAllAutoFixableInternalAsync")
                    .ConfigureAwait(false);

                details.Add($"  批量调用 ok={batchOk}{(batchError is null ? string.Empty : $" ({batchError})")}");
                details.Add($"  FixableCount     : {ReflectionBridge.Int(batchResult, "FixableCount")}");
                details.Add($"  FixedCount       : {ReflectionBridge.Int(batchResult, "FixedCount")}");
                details.Add($"  FixedItems       : {string.Join(" / ", ReflectionBridge.Strings(batchResult, "FixedItems"))}");
                details.Add($"  ManualItems      : {string.Join(" / ", ReflectionBridge.Strings(batchResult, "ManualItems"))}");
                details.Add($"  FailedItems      : {string.Join(" / ", ReflectionBridge.Strings(batchResult, "FailedItems"))}");
                details.Add($"  Message          : {ReflectionBridge.String(batchResult, "Message")}");

                var paths = await HealthCheckSupport.ReadGamePathsAsync(context, batchGameId, cancellationToken).ConfigureAwait(false);
                var layersValue = HealthCheckSupport.ReadCompatibilityLayer(paths.MainExecutable);

                details.Add($"  原名目录仍在     : {Directory.Exists(batchFolder)}（期望 False）");
                details.Add($"  改名后主程序     : {paths.MainExecutable}  存在={File.Exists(paths.MainExecutable)}");
                details.Add($"  注册表兼容性层   : {layersValue ?? "(不存在)"}（期望 ~ WIN7RTM）");
                details.Add($"  批量后仍可自动修复 : {ReflectionBridge.Int(batchViewModel, "AutoFixableCount")}（期望 0）");

                if (!batchOk || ReflectionBridge.Int(batchResult, "FixedCount") < 1)
                {
                    failures.Add($"用例4：批量修复没有修好任何一项 - {batchError ?? ReflectionBridge.String(batchResult, "Message")}");
                }

                if (fixableBefore < 2)
                {
                    failures.Add($"用例4：fixture 只产生了 {fixableBefore} 个可自动修复项，无法证明批量入口同时处理多项");
                }

                if (Directory.Exists(batchFolder))
                {
                    failures.Add("用例4：批量修复后原中文目录还在");
                }

                if (!File.Exists(paths.MainExecutable))
                {
                    failures.Add($"用例4：批量修复后主程序路径不存在 - {paths.MainExecutable}");
                }

                if (!string.Equals(layersValue, "~ WIN7RTM", StringComparison.Ordinal))
                {
                    failures.Add($"用例4：批量修复没有写入兼容模式 - 实测 \"{layersValue ?? "(不存在)"}\"");
                }

                if (ReflectionBridge.Int(batchViewModel, "AutoFixableCount") != 0)
                {
                    failures.Add("用例4：批量修复后仍报出可自动修复项");
                }

                if (ReflectionBridge.Strings(batchResult, "ManualItems").Count == 0)
                {
                    failures.Add("用例4：批量结果没有区分出「需要手动处理」的项，界面上会看不出还剩什么没解决");
                }

                diagnosedPaths.Add(paths.MainExecutable);
                diagnosedPaths.Add(batchExe);
            }
            finally
            {
                CleanUpCompatibilityLayers(diagnosedPaths, details);
            }
        }

        details.Add(string.Empty);
        details.Add("--- 判读 ---");
        details.Add($"  未通过项 : {failures.Count}");
        foreach (var failure in failures)
        {
            details.Add($"    - {failure}");
        }

        var actual = failures.Count == 0
            ? "分级与规格一致、可自动修复项实测被修好、其余项如实拒绝"
            : $"{failures.Count} 项未通过";

        return (failures.Count == 0
                ? CheckResult.Pass(Id, Title, expected, actual)
                : CheckResult.Fail(Id, Title, expected, actual))
            .With(details.ToArray());
    }

    private static async Task<GameInfo> LoadGameAsync(
        AcceptanceContext context,
        int gameId,
        CancellationToken cancellationToken)
    {
        await using var scope = context.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GalboxDbContext>();
        return await db.Games
            .AsNoTracking()
            .FirstAsync(g => g.Id == gameId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Removes the compatibility-layer values this check may have written for its own fixtures.</summary>
    private static void CleanUpCompatibilityLayers(IEnumerable<string> executablePaths, List<string> details)    {
        var removed = 0;
        foreach (var path in executablePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(HealthCheckSupport.AppCompatLayersKey, writable: true);
                if (key?.GetValue(path) is not null)
                {
                    key.DeleteValue(path, throwOnMissingValue: false);
                    removed++;
                }
            }
            catch (Exception ex)
            {
                details.Add($"清理警告（{path}）：{ex.Message}");
            }
        }

        details.Add($"收尾：清理了 {removed} 条本用例写入的兼容性层设置。");
    }
}
