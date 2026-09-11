using System.Diagnostics;
using Galbox.App.Models;
using Galbox.App.Services;
using Galbox.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Galbox.Acceptance.Checks;

/// <summary>
/// A71 - the "中文路径" diagnosis can actually be fixed: the game folder is renamed to an ASCII name,
/// the three path fields of the game row are updated in the same operation, and a failure in either
/// half leaves the library exactly as it was.
///
/// Spec §3.5 lists "一键重命名目录" as the suggested solution for the Chinese-directory item, and
/// §4.4 repeats it. The revision this check was written against marks the item
/// <c>AutoFixAvailable = false</c> and has no fix service at all, so the check FAILS there and PASSES
/// once the fix exists.
///
/// Four situations are exercised, all on fixtures created under the acceptance scratch folder:
///   1. happy path   - the folder is renamed, all path fields (including a child-table document path)
///                     follow, and a re-run of the diagnosis no longer reports the problem;
///   2. rollback     - the database commit is refused (another game row already claims the target
///                     path), so the already-performed rename must be undone;
///   3. conflict     - the target folder already exists on disk, so nothing may be touched;
///   4. running game - a process with the game's executable name is running, so the fix must refuse.
/// </summary>
public sealed class A71ChinesePathRenameCheck : IAcceptanceCheck
{
    /// <inheritdoc />
    public string Id => "A71";

    /// <inheritdoc />
    public string Title => "中文路径一键重命名：目录真的改名、数据库三个路径字段同步、失败可回滚";

    /// <inheritdoc />
    public async Task<CheckResult> RunAsync(AcceptanceContext context, CancellationToken cancellationToken)
    {
        var expected = "FixChineseInstallPathAsync 把游戏目录改成纯 ASCII 名，同步更新 Games.InstallPath / "
                     + "MainExecutable / AlternativeExecutables（以及目录下的文档路径），目标冲突时不动任何东西，"
                     + "数据库提交失败时把目录改回原名，游戏进程在运行时拒绝执行";

        var details = new List<string>();
        var failures = new List<string>();
        var work = AcceptanceWork.Create("a71");
        var errorChecking = context.Get<IErrorCheckingService>();
        var fixService = HealthCheckSupport.ResolveFixService(context, details);

        details.Add($"工作目录        : {work}");

        if (fixService is null)
        {
            details.Add(string.Empty);
            details.Add("FAIL REASON: 没有 IGameHealthFixService 这个服务，所以「中文路径」只能显示为不可自动修复的提示，");
            details.Add("             规格 §3.5 承诺的「一键重命名目录」在代码里根本不存在。");
            return CheckResult.Fail(Id, Title, expected, "IGameHealthFixService 未注册")
                .With(details.ToArray());
        }

        var createdGameIds = new List<int>();
        try
        {
            var translatedLeaf = await RunHappyPathAsync(
                context, errorChecking, fixService, work, details, failures, createdGameIds, cancellationToken)
                .ConfigureAwait(false);

            await RunRollbackCaseAsync(
                context, fixService, work, translatedLeaf, details, failures, createdGameIds, cancellationToken)
                .ConfigureAwait(false);

            await RunTargetConflictCaseAsync(
                context, fixService, work, translatedLeaf, details, failures, createdGameIds, cancellationToken)
                .ConfigureAwait(false);

            await RunRunningGameCaseAsync(
                context, errorChecking, fixService, work, details, failures, createdGameIds, cancellationToken)
                .ConfigureAwait(false);

            await RunUndoCaseAsync(
                context, errorChecking, fixService, work, details, failures, createdGameIds, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            await HealthCheckSupport.DeleteGamesAsync(context, CancellationToken.None, createdGameIds.ToArray())
                .ConfigureAwait(false);
        }

        details.Add(string.Empty);
        details.Add("--- 判读 ---");
        details.Add($"  未通过项 : {failures.Count}");
        foreach (var failure in failures)
        {
            details.Add($"    - {failure}");
        }

        var actual = failures.Count == 0
            ? "目录已改名 + 数据库三字段同步 + 冲突与运行中拒绝 + 提交失败回滚，全部实测通过"
            : $"{failures.Count} 项未通过";

        return (failures.Count == 0
                ? CheckResult.Pass(Id, Title, expected, actual)
                : CheckResult.Fail(Id, Title, expected, actual))
            .With(details.ToArray());
    }

    // ---------------------------------------------------------------------------------------
    // Case 1: happy path, driven through the exact call the UI button makes.
    // ---------------------------------------------------------------------------------------
    private static async Task<string> RunHappyPathAsync(
        AcceptanceContext context,
        IErrorCheckingService errorChecking,
        object fixService,
        string work,
        List<string> details,
        List<string> failures,
        List<int> createdGameIds,
        CancellationToken cancellationToken)
    {
        details.Add(string.Empty);
        details.Add("=== 用例 1：一键重命名（走 AttemptAutoFixAsync，即界面「尝试自动修复」按钮的调用） ===");

        var folder = Path.Combine(work, "case1", "中文游戏目录");
        var mainExe = AcceptanceWork.WritePattern(Path.Combine(folder, "probe.exe"), 512, 0x4D);
        var altExe = AcceptanceWork.WritePattern(Path.Combine(folder, "probe64.exe"), 256, 0x4D);
        var documentPath = AcceptanceWork.WriteText(Path.Combine(folder, "readme.txt"), "文档内容");
        var coverPath = AcceptanceWork.WritePattern(Path.Combine(folder, "cover.png"), 64, 0x89);
        var alternatives = $"{altExe}|{mainExe}";

        var gameId = await HealthCheckSupport
            .InsertGameAsync(context, "A71 中文路径探针", folder, mainExe, alternatives, cancellationToken)
            .ConfigureAwait(false);
        createdGameIds.Add(gameId);

        await using (var scope = context.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GalboxDbContext>();
            db.Set<GameDocument>().Add(new GameDocument
            {
                GameInfoId = gameId,
                FileName = "readme.txt",
                FilePath = documentPath,
                FileType = "txt"
            });
            var row = await db.Games.FirstAsync(g => g.Id == gameId, cancellationToken).ConfigureAwait(false);
            row.CoverImagePath = coverPath;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        var before = await HealthCheckSupport.ReadGamePathsAsync(context, gameId, cancellationToken).ConfigureAwait(false);
        details.Add($"  游戏行 Id        : {gameId}");
        details.Add($"  改名前目录       : {folder}  (存在={Directory.Exists(folder)})");
        details.Add($"  改名前 InstallPath        : {before.InstallPath}");
        details.Add($"  改名前 MainExecutable     : {before.MainExecutable}");
        details.Add($"  改名前 AlternativeExe     : {before.AlternativeExecutables}");
        details.Add($"  改名前 文档路径           : {documentPath}");
        details.Add($"  改名前 CoverImagePath     : {coverPath}");

        // The diagnosis must first report the problem and mark it as auto-fixable.
        var game = await LoadGameAsync(context, gameId, cancellationToken).ConfigureAwait(false);
        var findings = await errorChecking.CheckGameAsync(game, cancellationToken).ConfigureAwait(false);
        var finding = findings.FirstOrDefault(f => f.Category == ErrorCategory.ChineseDirectory);

        details.Add($"  诊断结论         : {(finding is null ? "没有报出中文路径问题（FAIL）" : $"\"{finding.Title}\" 严重度={finding.Severity} AutoFix={finding.AutoFixAvailable} 方案={finding.SolutionType}")}");

        if (finding is null)
        {
            failures.Add("用例1：诊断没有报出中文路径问题");
            return string.Empty;
        }

        if (!finding.AutoFixAvailable)
        {
            failures.Add("用例1：中文路径问题被标记为不可自动修复（AutoFixAvailable=false），界面上不会有可用的修复按钮");
        }

        var (callOk, result, callError) = await ReflectionBridge
            .CallAsync(errorChecking, "AttemptAutoFixAsync", finding, cancellationToken)
            .ConfigureAwait(false);

        details.Add($"  AttemptAutoFixAsync ok={callOk} Success={ReflectionBridge.Bool(result, "Success")}");
        details.Add($"  Message          : {ReflectionBridge.String(result, "Message")}");
        if (callError is not null)
        {
            details.Add($"  Error            : {callError}");
            failures.Add($"用例1：AttemptAutoFixAsync 调用失败 - {callError}");
            return string.Empty;
        }

        if (!ReflectionBridge.Bool(result, "Success"))
        {
            failures.Add($"用例1：自动修复失败 - {ReflectionBridge.String(result, "Message")}");
            return string.Empty;
        }

        var after = await HealthCheckSupport.ReadGamePathsAsync(context, gameId, cancellationToken).ConfigureAwait(false);
        var newFolder = after.InstallPath;
        var translatedLeaf = Path.GetFileName(newFolder.TrimEnd(Path.DirectorySeparatorChar));

        string? documentAfter = null;
        string? coverAfter = null;
        await using (var scope = context.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GalboxDbContext>();
            documentAfter = await db.Set<GameDocument>()
                .Where(d => d.GameInfoId == gameId)
                .Select(d => d.FilePath)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            coverAfter = await db.Games.Where(g => g.Id == gameId).Select(g => g.CoverImagePath)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        details.Add(string.Empty);
        details.Add("  --- 改名后实测 ---");
        details.Add($"  目录             : {newFolder}  (存在={Directory.Exists(newFolder)})");
        details.Add($"  旧目录是否仍在   : {Directory.Exists(folder)}（期望 False）");
        details.Add($"  新目录名         : {translatedLeaf}  纯 ASCII={HealthCheckSupport.IsPureAscii(translatedLeaf)}");
        details.Add($"  新 InstallPath        : {after.InstallPath}");
        details.Add($"  新 MainExecutable     : {after.MainExecutable}");
        details.Add($"  新 AlternativeExe     : {after.AlternativeExecutables}");
        details.Add($"  新 文档路径           : {documentAfter}");
        details.Add($"  新 CoverImagePath     : {coverAfter}");
        details.Add($"  主程序文件存在   : {File.Exists(after.MainExecutable)}");
        details.Add($"  备选程序文件存在 : {File.Exists(Path.Combine(newFolder, "probe64.exe"))}");

        if (Directory.Exists(folder))
        {
            failures.Add("用例1：旧的中文目录仍然存在，重命名没有真正发生");
        }

        if (!Directory.Exists(newFolder))
        {
            failures.Add($"用例1：新目录不存在 - {newFolder}");
        }

        if (!HealthCheckSupport.IsPureAscii(translatedLeaf) || HealthCheckSupport.HasChinese(translatedLeaf))
        {
            failures.Add($"用例1：新目录名不是纯 ASCII - \"{translatedLeaf}\"");
        }

        if (!translatedLeaf.Contains("Game", StringComparison.OrdinalIgnoreCase))
        {
            failures.Add($"用例1：新目录名没有体现规格里的映射示例（游戏 → Game）- \"{translatedLeaf}\"");
        }

        if (HealthCheckSupport.HasChinese(after.InstallPath))
        {
            failures.Add($"用例1：InstallPath 仍含中文 - {after.InstallPath}");
        }

        if (HealthCheckSupport.HasChinese(after.MainExecutable) || !File.Exists(after.MainExecutable))
        {
            failures.Add($"用例1：MainExecutable 未正确同步 - {after.MainExecutable}");
        }

        if (after.AlternativeExecutables is null
            || HealthCheckSupport.HasChinese(after.AlternativeExecutables)
            || !File.Exists(after.AlternativeExecutables.Split('|')[0]))
        {
            failures.Add($"用例1：AlternativeExecutables 未正确同步 - {after.AlternativeExecutables}");
        }

        if (documentAfter is null || HealthCheckSupport.HasChinese(documentAfter) || !File.Exists(documentAfter))
        {
            failures.Add($"用例1：目录内文档路径未同步 - {documentAfter}");
        }

        if (coverAfter is not null && HealthCheckSupport.HasChinese(coverAfter))
        {
            failures.Add($"用例1：CoverImagePath 未同步 - {coverAfter}");
        }

        // Re-diagnosis must now be clean.
        var gameAfter = await LoadGameAsync(context, gameId, cancellationToken).ConfigureAwait(false);
        var findingsAfter = await errorChecking.CheckGameAsync(gameAfter, cancellationToken).ConfigureAwait(false);
        var stillReported = findingsAfter.Any(f => f.Category == ErrorCategory.ChineseDirectory);
        details.Add($"  复诊仍报中文路径 : {stillReported}（期望 False）");
        if (stillReported)
        {
            failures.Add("用例1：修复后复诊仍然报出中文路径问题");
        }

        return translatedLeaf;
    }

    // ---------------------------------------------------------------------------------------
    // Case 2: the database commit is refused -> the completed rename must be undone.
    // ---------------------------------------------------------------------------------------
    private static async Task RunRollbackCaseAsync(
        AcceptanceContext context,
        object fixService,
        string work,
        string translatedLeaf,
        List<string> details,
        List<string> failures,
        List<int> createdGameIds,
        CancellationToken cancellationToken)
    {
        details.Add(string.Empty);
        details.Add("=== 用例 2：提交数据库失败 → 目录必须改回原名（回滚） ===");

        if (string.IsNullOrEmpty(translatedLeaf))
        {
            details.Add("  跳过：用例 1 没有产出可用的目标目录名。");
            return;
        }

        var parent = Path.Combine(work, "case2");
        var folder = Path.Combine(parent, "中文游戏目录");
        var mainExe = AcceptanceWork.WritePattern(Path.Combine(folder, "probe.exe"), 512, 0x4D);
        var targetFolder = Path.Combine(parent, translatedLeaf);

        var gameId = await HealthCheckSupport
            .InsertGameAsync(context, "A71 回滚探针", folder, mainExe, null, cancellationToken)
            .ConfigureAwait(false);
        createdGameIds.Add(gameId);

        // A second row already claims the target install path (real situation: the user renamed the
        // folder in Explorer and re-added the game, or an earlier repair was interrupted between the
        // rename and the commit). The commit must refuse - and the rename that already happened must
        // be undone, otherwise the library points at a folder that no longer exists.
        var decoyId = await HealthCheckSupport
            .InsertGameAsync(context, "A71 占位行", targetFolder, Path.Combine(targetFolder, "probe.exe"), null, cancellationToken)
            .ConfigureAwait(false);
        createdGameIds.Add(decoyId);

        details.Add($"  游戏行 Id        : {gameId}（被修复者）, {decoyId}（占位行，已声明目标路径）");
        details.Add($"  原目录           : {folder}  (存在={Directory.Exists(folder)})");
        details.Add($"  占位行声明的路径 : {targetFolder}  (磁盘上存在={Directory.Exists(targetFolder)}）");

        var before = await HealthCheckSupport.ReadGamePathsAsync(context, gameId, cancellationToken).ConfigureAwait(false);

        var (callOk, result, callError) = await ReflectionBridge
            .CallAsync(fixService, "FixChineseInstallPathAsync", gameId, cancellationToken)
            .ConfigureAwait(false);

        var success = ReflectionBridge.Bool(result, "Success");
        var rolledBack = ReflectionBridge.Bool(result, "RolledBack");

        details.Add($"  调用 ok          : {callOk}{(callError is null ? string.Empty : $" ({callError})")}");
        details.Add($"  Success          : {success}（期望 False）");
        details.Add($"  RolledBack       : {rolledBack}（期望 True）");
        details.Add($"  Message          : {ReflectionBridge.String(result, "Message")}");

        var after = await HealthCheckSupport.ReadGamePathsAsync(context, gameId, cancellationToken).ConfigureAwait(false);

        details.Add($"  回滚后原目录存在 : {Directory.Exists(folder)}（期望 True）");
        details.Add($"  回滚后新目录存在 : {Directory.Exists(targetFolder)}（期望 False）");
        details.Add($"  回滚后 InstallPath    : {after.InstallPath}");
        details.Add($"  回滚后 MainExecutable : {after.MainExecutable}");

        if (callOk && success)
        {
            failures.Add("用例2：数据库存在路径冲突，修复却报告成功");
        }

        if (!Directory.Exists(folder))
        {
            failures.Add("用例2：失败后原中文目录没有恢复（没有回滚）");
        }

        if (Directory.Exists(targetFolder))
        {
            failures.Add("用例2：失败后目标目录仍然存在（回滚没有删掉改名结果）");
        }

        if (after.InstallPath != before.InstallPath || after.MainExecutable != before.MainExecutable)
        {
            failures.Add($"用例2：失败后数据库被改动 - {before.InstallPath} -> {after.InstallPath}");
        }
    }

    // ---------------------------------------------------------------------------------------
    // Case 3: the target folder already exists on disk -> nothing may be touched.
    // ---------------------------------------------------------------------------------------
    private static async Task RunTargetConflictCaseAsync(
        AcceptanceContext context,
        object fixService,
        string work,
        string translatedLeaf,
        List<string> details,
        List<string> failures,
        List<int> createdGameIds,
        CancellationToken cancellationToken)
    {
        details.Add(string.Empty);
        details.Add("=== 用例 3：目标目录已存在 → 拒绝执行且不改动任何东西 ===");

        if (string.IsNullOrEmpty(translatedLeaf))
        {
            details.Add("  跳过：用例 1 没有产出可用的目标目录名。");
            return;
        }

        var parent = Path.Combine(work, "case3");
        var folder = Path.Combine(parent, "中文游戏目录");
        var mainExe = AcceptanceWork.WritePattern(Path.Combine(folder, "probe.exe"), 512, 0x4D);
        var targetFolder = Path.Combine(parent, translatedLeaf);
        AcceptanceWork.WriteText(Path.Combine(targetFolder, "unrelated.txt"), "另一个游戏的目录");

        var gameId = await HealthCheckSupport
            .InsertGameAsync(context, "A71 冲突探针", folder, mainExe, null, cancellationToken)
            .ConfigureAwait(false);
        createdGameIds.Add(gameId);

        var before = await HealthCheckSupport.ReadGamePathsAsync(context, gameId, cancellationToken).ConfigureAwait(false);

        var (callOk, result, callError) = await ReflectionBridge
            .CallAsync(fixService, "FixChineseInstallPathAsync", gameId, cancellationToken)
            .ConfigureAwait(false);

        var success = ReflectionBridge.Bool(result, "Success");
        var after = await HealthCheckSupport.ReadGamePathsAsync(context, gameId, cancellationToken).ConfigureAwait(false);

        details.Add($"  调用 ok          : {callOk}{(callError is null ? string.Empty : $" ({callError})")}");
        details.Add($"  Success          : {success}（期望 False）");
        details.Add($"  Message          : {ReflectionBridge.String(result, "Message")}");
        details.Add($"  原目录仍在       : {Directory.Exists(folder)}（期望 True）");
        details.Add($"  目标目录里的无关文件仍在 : {File.Exists(Path.Combine(targetFolder, "unrelated.txt"))}（期望 True）");
        details.Add($"  InstallPath 未变 : {after.InstallPath == before.InstallPath}");

        if (callOk && success)
        {
            failures.Add("用例3：目标目录冲突却报告修复成功");
        }

        if (!Directory.Exists(folder))
        {
            failures.Add("用例3：冲突时原目录消失了");
        }

        if (!File.Exists(Path.Combine(targetFolder, "unrelated.txt")))
        {
            failures.Add("用例3：冲突时把已存在的目标目录动掉了");
        }

        if (after.InstallPath != before.InstallPath)
        {
            failures.Add("用例3：冲突时数据库仍被改动");
        }
    }

    // ---------------------------------------------------------------------------------------
    // Case 4: the game is running -> the fix must refuse (renaming a running game corrupts saves).
    // ---------------------------------------------------------------------------------------
    private static async Task RunRunningGameCaseAsync(
        AcceptanceContext context,
        IErrorCheckingService errorChecking,
        object fixService,
        string work,
        List<string> details,
        List<string> failures,
        List<int> createdGameIds,
        CancellationToken cancellationToken)
    {
        details.Add(string.Empty);
        details.Add("=== 用例 4：游戏进程正在运行 → 拒绝重命名 ===");

        // The fixture executable is deliberately named after a process that is provably running:
        // this very acceptance host. The monitor matches by process name, which is exactly how the
        // guard has to behave for a real game.
        var runningName = Process.GetCurrentProcess().ProcessName;
        var monitor = context.Get<IProcessMonitorService>();
        var monitorSaysRunning = monitor.IsProcessRunning(runningName);

        details.Add($"  正在运行的进程名 : {runningName}（监控服务 IsProcessRunning={monitorSaysRunning}）");

        var folder = Path.Combine(work, "case4", "中文游戏目录");
        var mainExe = AcceptanceWork.WritePattern(Path.Combine(folder, runningName + ".exe"), 512, 0x4D);

        var gameId = await HealthCheckSupport
            .InsertGameAsync(context, "A71 运行中探针", folder, mainExe, null, cancellationToken)
            .ConfigureAwait(false);
        createdGameIds.Add(gameId);

        // Register the game so the monitor knows about it as well (the shipping app registers every
        // launched game), then ask the diagnosis for the finding and call the fix.
        var game = await LoadGameAsync(context, gameId, cancellationToken).ConfigureAwait(false);
        monitor.RegisterGame(game);

        try
        {
            var finding = (await errorChecking.CheckGameAsync(game, cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(f => f.Category == ErrorCategory.ChineseDirectory);

            details.Add($"  诊断报出中文路径 : {finding is not null}");

            var (callOk, result, callError) = await ReflectionBridge
                .CallAsync(fixService, "FixChineseInstallPathAsync", gameId, cancellationToken)
                .ConfigureAwait(false);

            var success = ReflectionBridge.Bool(result, "Success");

            details.Add($"  调用 ok          : {callOk}{(callError is null ? string.Empty : $" ({callError})")}");
            details.Add($"  Success          : {success}（期望 False）");
            details.Add($"  Message          : {ReflectionBridge.String(result, "Message")}");
            details.Add($"  目录是否仍在原处 : {Directory.Exists(folder)}（期望 True）");

            if (!monitorSaysRunning)
            {
                details.Add("  注：本机没有名为该进程的进程在运行，本用例的前提不成立，已跳过判定。");
                failures.Add("用例4：无法构造「正在运行」的前提（监控服务没有看到该进程）");
                return;
            }

            if (callOk && success)
            {
                failures.Add("用例4：游戏进程正在运行，重命名却报告成功");
            }

            if (!Directory.Exists(folder))
            {
                failures.Add("用例4：游戏进程正在运行时目录仍被改名");
            }
        }
        finally
        {
            monitor.UnregisterGame(gameId);
        }
    }

    // ---------------------------------------------------------------------------------------
    // Case 5: undo. A fix that cannot be undone is a trap.
    // ---------------------------------------------------------------------------------------
    private static async Task RunUndoCaseAsync(
        AcceptanceContext context,
        IErrorCheckingService errorChecking,
        object fixService,
        string work,
        List<string> details,
        List<string> failures,
        List<int> createdGameIds,
        CancellationToken cancellationToken)
    {
        details.Add(string.Empty);
        details.Add("=== 用例 5：撤销（改回中文目录名，数据库同步还原） ===");

        var folder = Path.Combine(work, "case5", "中文游戏目录");
        var mainExe = AcceptanceWork.WritePattern(Path.Combine(folder, "probe.exe"), 512, 0x4D);

        var gameId = await HealthCheckSupport
            .InsertGameAsync(context, "A71 撤销探针", folder, mainExe, null, cancellationToken)
            .ConfigureAwait(false);
        createdGameIds.Add(gameId);

        var (fixOk, fixResult, fixError) = await ReflectionBridge
            .CallAsync(fixService, "FixChineseInstallPathAsync", gameId, cancellationToken)
            .ConfigureAwait(false);

        var renamed = await HealthCheckSupport.ReadGamePathsAsync(context, gameId, cancellationToken).ConfigureAwait(false);
        details.Add($"  修复 ok={fixOk} Success={ReflectionBridge.Bool(fixResult, "Success")}"
                    + $"{(fixError is null ? string.Empty : $" ({fixError})")}");
        details.Add($"  修复后 InstallPath : {renamed.InstallPath}");

        var (undoOk, undoResult, undoError) = await ReflectionBridge
            .CallAsync(fixService, "RevertChineseInstallPathAsync", gameId, cancellationToken)
            .ConfigureAwait(false);

        var restored = await HealthCheckSupport.ReadGamePathsAsync(context, gameId, cancellationToken).ConfigureAwait(false);

        details.Add($"  撤销 ok={undoOk} Success={ReflectionBridge.Bool(undoResult, "Success")}"
                    + $"{(undoError is null ? string.Empty : $" ({undoError})")}");
        details.Add($"  撤销 Message       : {ReflectionBridge.String(undoResult, "Message")}");
        details.Add($"  撤销后 InstallPath : {restored.InstallPath}（期望原中文路径）");
        details.Add($"  撤销后目录存在     : {Directory.Exists(restored.InstallPath)}");

        if (!ReflectionBridge.Bool(fixResult, "Success"))
        {
            failures.Add("用例5：前置的修复没有成功，无法验证撤销");
            return;
        }

        if (!undoOk || !ReflectionBridge.Bool(undoResult, "Success"))
        {
            failures.Add($"用例5：撤销失败 - {undoError ?? ReflectionBridge.String(undoResult, "Message")}");
            return;
        }

        if (restored.InstallPath != folder || !Directory.Exists(folder))
        {
            failures.Add($"用例5：撤销后目录没有回到原名 - {restored.InstallPath}");
        }

        // The diagnosis must report the problem again after the undo - proving the undo really
        // restored the state instead of merely rewriting a database string.
        var game = await LoadGameAsync(context, gameId, cancellationToken).ConfigureAwait(false);
        var findings = await errorChecking.CheckGameAsync(game, cancellationToken).ConfigureAwait(false);
        var reportedAgain = findings.Any(f => f.Category == ErrorCategory.ChineseDirectory);
        details.Add($"  撤销后复诊报中文路径 : {reportedAgain}（期望 True）");

        if (!reportedAgain)
        {
            failures.Add("用例5：撤销后复诊没有报出中文路径问题，说明撤销不完整");
        }
    }

    private static async Task<GameInfo> LoadGameAsync(
        AcceptanceContext context,
        int gameId,
        CancellationToken cancellationToken)
    {
        await using var scope = context.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GalboxDbContext>();
        return await db.Games.AsNoTracking().FirstAsync(g => g.Id == gameId, cancellationToken).ConfigureAwait(false);
    }
}
