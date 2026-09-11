using Galbox.App.Models;
using Galbox.App.Services;
using Microsoft.Win32;

namespace Galbox.Acceptance.Checks;

/// <summary>
/// A72 - the "Windows 兼容性" diagnosis can really be applied and really be undone, through the
/// per-application compatibility layer Windows itself uses.
///
/// Spec §3.5/§4.4 list "兼容模式运行" as the suggested solution for old games. The revision this check
/// was written against only prints a paragraph of instructions ("right-click the executable, open
/// Properties, ...") and marks the item <c>AutoFixAvailable = false</c>; the user still has to do it
/// by hand.
///
/// The check writes the layer for an executable IT created, under the acceptance scratch folder, and
/// removes every trace afterwards. It never touches a real game executable, and it asserts that only
/// HKCU is written: HKLM needs elevation and would change the machine for every user.
/// </summary>
public sealed class A72CompatibilityModeCheck : IAcceptanceCheck
{
    /// <inheritdoc />
    public string Id => "A72";

    /// <inheritdoc />
    public string Title => "Windows 兼容性一键写入：HKCU 兼容性层真的写入、且撤销后真的删除";

    /// <inheritdoc />
    public async Task<CheckResult> RunAsync(AcceptanceContext context, CancellationToken cancellationToken)
    {
        var expected = "ApplyWindowsCompatibilityModeAsync 在 HKCU\\...\\AppCompatFlags\\Layers 下以可执行文件全路径为名"
                     + "写入兼容性层（~ WIN7RTM），已有旧值时报告旧值，RevertWindowsCompatibilityModeAsync 还原旧值"
                     + "（无旧值时删除该值），重命名游戏目录时已有的层设置跟着可执行文件的新路径迁移，"
                     + "且全程只写 HKCU、不删除其他应用的兼容性设置";

        var details = new List<string>();
        var failures = new List<string>();
        var work = AcceptanceWork.Create("a72");
        var layersKeyExistedBefore = LayersKeyExists();
        var fixService = HealthCheckSupport.ResolveFixService(context, details);
        var createdGameIds = new List<int>();

        details.Add($"工作目录        : {work}");
        details.Add($"Layers 键原本存在 : {layersKeyExistedBefore}（本用例结束时值一定会被删掉）");

        if (fixService is null)
        {
            details.Add(string.Empty);
            details.Add("FAIL REASON: 没有 IGameHealthFixService，兼容模式只能靠用户自己右键 → 属性 → 兼容性手工设置。");
            return CheckResult.Fail(Id, Title, expected, "IGameHealthFixService 未注册")
                .With(details.ToArray());
        }

        try
        {
            await RunApplyAndRevertAsync(context, fixService, work, details, failures, createdGameIds, cancellationToken)
                .ConfigureAwait(false);

            await RunRestorePreviousValueAsync(context, fixService, work, details, failures, createdGameIds, cancellationToken)
                .ConfigureAwait(false);

            await RunMissingExecutableAsync(context, fixService, work, details, failures, createdGameIds, cancellationToken)
                .ConfigureAwait(false);

            await RunLayerMovesWithFolderAsync(context, fixService, work, details, failures, createdGameIds, cancellationToken)
                .ConfigureAwait(false);

            RunHkcuOnlySourceCheck(details, failures);
        }
        finally
        {
            await HealthCheckSupport.DeleteGamesAsync(context, CancellationToken.None, createdGameIds.ToArray())
                .ConfigureAwait(false);
            CleanUpLayersKey(layersKeyExistedBefore, details);
        }

        details.Add(string.Empty);
        details.Add("--- 判读 ---");
        details.Add($"  未通过项 : {failures.Count}");
        foreach (var failure in failures)
        {
            details.Add($"    - {failure}");
        }

        var actual = failures.Count == 0
            ? "注册表值实测写入 / 撤销 / 还原旧值，全部通过，且只涉及 HKCU"
            : $"{failures.Count} 项未通过";

        return (failures.Count == 0
                ? CheckResult.Pass(Id, Title, expected, actual)
                : CheckResult.Fail(Id, Title, expected, actual))
            .With(details.ToArray());
    }

    // ---------------------------------------------------------------------------------------
    private static async Task RunApplyAndRevertAsync(
        AcceptanceContext context,
        object fixService,
        string work,
        List<string> details,
        List<string> failures,
        List<int> createdGameIds,
        CancellationToken cancellationToken)
    {
        details.Add(string.Empty);
        details.Add("=== 用例 1：写入 → 读回 → 撤销 → 读回 ===");

        var exePath = AcceptanceWork.WritePattern(Path.Combine(work, "case1", "legacy-probe.exe"), 512, 0x4D);
        var gameId = await HealthCheckSupport
            .InsertGameAsync(context, "A72 兼容性探针", Path.GetDirectoryName(exePath)!, exePath, null, cancellationToken)
            .ConfigureAwait(false);
        createdGameIds.Add(gameId);

        var valueBefore = ReadLayer(exePath);
        details.Add($"  可执行文件       : {exePath}");
        details.Add($"  写入前注册表值   : {valueBefore ?? "(不存在)"}");

        var (applyOk, applyResult, applyError) = await ReflectionBridge
            .CallAsync(fixService, "ApplyWindowsCompatibilityModeAsync", gameId, cancellationToken)
            .ConfigureAwait(false);

        var valueAfterApply = ReadLayer(exePath);
        details.Add($"  调用 ok          : {applyOk}{(applyError is null ? string.Empty : $" ({applyError})")}");
        details.Add($"  Success          : {ReflectionBridge.Bool(applyResult, "Success")}");
        details.Add($"  Message          : {ReflectionBridge.String(applyResult, "Message")}");
        details.Add($"  AppliedValue     : {ReflectionBridge.String(applyResult, "AppliedValue")}");
        details.Add($"  写入后注册表值   : {valueAfterApply ?? "(不存在)"}（期望 ~ WIN7RTM）");

        if (!applyOk || !ReflectionBridge.Bool(applyResult, "Success"))
        {
            failures.Add($"用例1：写入兼容性层失败 - {applyError ?? ReflectionBridge.String(applyResult, "Message")}");
            return;
        }

        if (!string.Equals(valueAfterApply, "~ WIN7RTM", StringComparison.Ordinal))
        {
            failures.Add($"用例1：注册表值不正确 - 实测 \"{valueAfterApply ?? "(空)"}\"");
        }

        var (revertOk, revertResult, revertError) = await ReflectionBridge
            .CallAsync(fixService, "RevertWindowsCompatibilityModeAsync", gameId, cancellationToken)
            .ConfigureAwait(false);

        var valueAfterRevert = ReadLayer(exePath);
        details.Add(string.Empty);
        details.Add($"  撤销 ok          : {revertOk}{(revertError is null ? string.Empty : $" ({revertError})")}");
        details.Add($"  Success          : {ReflectionBridge.Bool(revertResult, "Success")}");
        details.Add($"  Message          : {ReflectionBridge.String(revertResult, "Message")}");
        details.Add($"  撤销后注册表值   : {valueAfterRevert ?? "(不存在)"}（期望不存在）");
        details.Add($"  Layers 键仍存在  : {LayersKeyExists()}（期望 True，不能删掉其他应用的设置）");

        if (!revertOk || !ReflectionBridge.Bool(revertResult, "Success"))
        {
            failures.Add($"用例1：撤销失败 - {revertError ?? ReflectionBridge.String(revertResult, "Message")}");
        }

        if (valueAfterRevert is not null)
        {
            failures.Add($"用例1：撤销后注册表值仍然存在 - \"{valueAfterRevert}\"（只写不删正是要避免的问题）");
        }

        if (!LayersKeyExists())
        {
            failures.Add("用例1：撤销时把整个 Layers 键删掉了，其他应用的兼容性设置会一起丢失");
        }
    }

    // ---------------------------------------------------------------------------------------
    private static async Task RunRestorePreviousValueAsync(
        AcceptanceContext context,
        object fixService,
        string work,
        List<string> details,
        List<string> failures,
        List<int> createdGameIds,
        CancellationToken cancellationToken)
    {
        details.Add(string.Empty);
        details.Add("=== 用例 2：用户已手工设置过兼容模式 → 撤销必须还原为原值，而不是一删了之 ===");

        var exePath = AcceptanceWork.WritePattern(Path.Combine(work, "case2", "preexisting-probe.exe"), 512, 0x4D);
        var gameId = await HealthCheckSupport
            .InsertGameAsync(context, "A72 已有设置探针", Path.GetDirectoryName(exePath)!, exePath, null, cancellationToken)
            .ConfigureAwait(false);
        createdGameIds.Add(gameId);

        WriteLayer(exePath, "~ WINXPSP3");
        details.Add($"  用户原有值       : {ReadLayer(exePath)}（模拟用户自己设置过 XP SP3 兼容模式）");

        try
        {
            var (applyOk, applyResult, _) = await ReflectionBridge
                .CallAsync(fixService, "ApplyWindowsCompatibilityModeAsync", gameId, cancellationToken)
                .ConfigureAwait(false);

            var afterApply = ReadLayer(exePath);
            details.Add($"  写入 ok={applyOk} Success={ReflectionBridge.Bool(applyResult, "Success")}");
            details.Add($"  PreviousValue    : {ReflectionBridge.String(applyResult, "PreviousValue")}（期望 ~ WINXPSP3）");
            details.Add($"  写入后注册表值   : {afterApply}（期望 ~ WIN7RTM）");

            if (!applyOk || !ReflectionBridge.Bool(applyResult, "Success"))
            {
                failures.Add("用例2：写入失败");
                return;
            }

            if (!string.Equals(afterApply, "~ WIN7RTM", StringComparison.Ordinal))
            {
                failures.Add($"用例2：写入后注册表值不正确 - \"{afterApply}\"");
            }

            var (revertOk, revertResult, _) = await ReflectionBridge
                .CallAsync(fixService, "RevertWindowsCompatibilityModeAsync", gameId, cancellationToken)
                .ConfigureAwait(false);

            var afterRevert = ReadLayer(exePath);
            details.Add($"  撤销 ok={revertOk} Success={ReflectionBridge.Bool(revertResult, "Success")}");
            details.Add($"  撤销后注册表值   : {afterRevert ?? "(不存在)"}（期望 ~ WINXPSP3，即用户原值）");

            if (!revertOk || !ReflectionBridge.Bool(revertResult, "Success"))
            {
                failures.Add("用例2：撤销失败");
            }

            if (!string.Equals(afterRevert, "~ WINXPSP3", StringComparison.Ordinal))
            {
                failures.Add($"用例2：撤销没有还原用户原有设置 - 实测 \"{afterRevert ?? "(不存在)"}\"");
            }
        }
        finally
        {
            DeleteLayer(exePath);
        }
    }

    // ---------------------------------------------------------------------------------------
    private static async Task RunMissingExecutableAsync(
        AcceptanceContext context,
        object fixService,
        string work,
        List<string> details,
        List<string> failures,
        List<int> createdGameIds,
        CancellationToken cancellationToken)
    {
        details.Add(string.Empty);
        details.Add("=== 用例 3：可执行文件不存在 → 拒绝写入（避免在注册表里留下一堆死路径） ===");

        var folder = Path.Combine(work, "case3");
        Directory.CreateDirectory(folder);
        var missingExe = Path.Combine(folder, "does-not-exist.exe");
        var gameId = await HealthCheckSupport
            .InsertGameAsync(context, "A72 缺文件探针", folder, missingExe, null, cancellationToken)
            .ConfigureAwait(false);
        createdGameIds.Add(gameId);

        var (callOk, result, callError) = await ReflectionBridge
            .CallAsync(fixService, "ApplyWindowsCompatibilityModeAsync", gameId, cancellationToken)
            .ConfigureAwait(false);

        var success = ReflectionBridge.Bool(result, "Success");
        details.Add($"  调用 ok          : {callOk}{(callError is null ? string.Empty : $" ({callError})")}");
        details.Add($"  Success          : {success}（期望 False）");
        details.Add($"  Message          : {ReflectionBridge.String(result, "Message")}");
        details.Add($"  注册表是否被写   : {ReadLayer(missingExe) is not null}（期望 False）");

        if (callOk && success)
        {
            failures.Add("用例3：可执行文件不存在却报告写入成功");
        }

        if (ReadLayer(missingExe) is not null)
        {
            failures.Add("用例3：为不存在的可执行文件写了注册表值");
        }
    }

    // ---------------------------------------------------------------------------------------
    /// <summary>
    /// A compatibility layer is keyed by the executable's full path. Renaming the game folder
    /// therefore re-points every layer the user already had at a path that no longer exists - the
    /// one-click repair would silently undo an earlier one-click repair. The layer has to travel
    /// with the folder.
    /// </summary>
    private static async Task RunLayerMovesWithFolderAsync(
        AcceptanceContext context,
        object fixService,
        string work,
        List<string> details,
        List<string> failures,
        List<int> createdGameIds,
        CancellationToken cancellationToken)
    {
        details.Add(string.Empty);
        details.Add("=== 用例 5：目录改名时，已有的兼容性层设置必须跟着走（否则一次修复会悄悄废掉上一次修复） ===");

        var folder = Path.Combine(work, "case5", "中文游戏目录");
        var exePath = AcceptanceWork.WritePattern(Path.Combine(folder, "probe.exe"), 512, 0x4D);
        var gameId = await HealthCheckSupport
            .InsertGameAsync(context, "A72 迁移探针", folder, exePath, null, cancellationToken)
            .ConfigureAwait(false);
        createdGameIds.Add(gameId);

        WriteLayer(exePath, "~ WINXPSP3");
        details.Add($"  改名前的层设置   : {exePath} = {ReadLayer(exePath)}");

        string? newExePath = null;
        try
        {
            var (fixOk, fixResult, fixError) = await ReflectionBridge
                .CallAsync(fixService, "FixChineseInstallPathAsync", gameId, cancellationToken)
                .ConfigureAwait(false);

            details.Add($"  重命名 ok={fixOk} Success={ReflectionBridge.Bool(fixResult, "Success")}"
                      + $"{(fixError is null ? string.Empty : $" ({fixError})")}");

            if (!fixOk || !ReflectionBridge.Bool(fixResult, "Success"))
            {
                failures.Add($"用例5：重命名失败，无法验证层设置迁移 - {fixError ?? ReflectionBridge.String(fixResult, "Message")}");
                return;
            }

            var paths = await HealthCheckSupport.ReadGamePathsAsync(context, gameId, cancellationToken).ConfigureAwait(false);
            newExePath = paths.MainExecutable;

            var oldValue = ReadLayer(exePath);
            var newValue = ReadLayer(newExePath);

            details.Add($"  改名后的主程序   : {newExePath}");
            details.Add($"  旧路径上的值     : {oldValue ?? "(已删除)"}（期望已删除）");
            details.Add($"  新路径上的值     : {newValue ?? "(不存在)"}（期望 ~ WINXPSP3）");

            if (oldValue is not null)
            {
                failures.Add($"用例5：旧可执行路径上仍留着兼容性层设置 \"{oldValue}\"");
            }

            if (!string.Equals(newValue, "~ WINXPSP3", StringComparison.Ordinal))
            {
                failures.Add($"用例5：新可执行路径上没有继承兼容性层设置 - 实测 \"{newValue ?? "(不存在)"}\"");
            }
        }
        finally
        {
            DeleteLayer(exePath);
            if (newExePath is not null)
            {
                DeleteLayer(newExePath);
            }
        }
    }

    // ---------------------------------------------------------------------------------------
    /// <summary>
    /// Source-level proof of a runtime property that cannot be observed directly: the fix service
    /// must never open a machine-wide or other-user hive. A run cannot enumerate every key a process
    /// might have written, but it can prove the code contains no such access at all.
    /// </summary>
    private static void RunHkcuOnlySourceCheck(List<string> details, List<string> failures)
    {
        details.Add(string.Empty);
        details.Add("=== 用例 4：只写 HKCU（源码级反证，无法用运行时枚举证明「没有写别处」） ===");

        var repoRoot = RepoLocator.FindRepoRoot();
        if (repoRoot is null)
        {
            details.Add("  仓库根目录未找到：无法检查。");
            failures.Add("用例4：无法定位仓库根目录以检查源码");
            return;
        }

        var servicePath = Path.Combine(repoRoot, "src", "Galbox.App", "Services", "GameHealthFixService.cs");
        if (!File.Exists(servicePath))
        {
            details.Add($"  文件不存在：{servicePath}");
            failures.Add("用例4：GameHealthFixService.cs 不存在");
            return;
        }

        var text = File.ReadAllText(servicePath);
        var forbidden = new[] { "Registry.LocalMachine", "Registry.ClassesRoot", "Registry.Users", "Registry.PerformanceData", "HKLM:" };
        foreach (var needle in forbidden)
        {
            var found = text.Contains(needle, StringComparison.Ordinal);
            details.Add($"  [{(found ? "FAIL" : "OK  ")}] 源码不含 \"{needle}\"");
            if (found)
            {
                failures.Add($"用例4：修复服务里出现了机器级注册表访问 \"{needle}\"");
            }
        }

        var usesCurrentUser = text.Contains("Registry.CurrentUser", StringComparison.Ordinal);
        details.Add($"  [{(usesCurrentUser ? "OK  " : "FAIL")}] 源码使用 Registry.CurrentUser");
        if (!usesCurrentUser)
        {
            failures.Add("用例4：修复服务没有使用 Registry.CurrentUser");
        }
    }

    // ---------------------------------------------------------------------------------------
    private static string? ReadLayer(string exePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(HealthCheckSupport.AppCompatLayersKey);
        return key?.GetValue(exePath) as string;
    }

    private static void WriteLayer(string exePath, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(HealthCheckSupport.AppCompatLayersKey);
        key.SetValue(exePath, value, RegistryValueKind.String);
    }

    private static void DeleteLayer(string exePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(HealthCheckSupport.AppCompatLayersKey, writable: true);
        key?.DeleteValue(exePath, throwOnMissingValue: false);
    }

    private static bool LayersKeyExists()
    {
        using var key = Registry.CurrentUser.OpenSubKey(HealthCheckSupport.AppCompatLayersKey);
        return key is not null;
    }

    /// <summary>
    /// Removes the Layers key again when this run created it and nothing is left inside. The key is
    /// shared with every application on the machine, so it is only ever removed when it is provably
    /// empty and provably did not exist before the check started.
    /// </summary>
    private static void CleanUpLayersKey(bool existedBefore, List<string> details)
    {
        if (existedBefore)
        {
            return;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(HealthCheckSupport.AppCompatLayersKey, writable: true);
            if (key is null)
            {
                return;
            }

            if (key.SubKeyCount == 0 && key.ValueCount == 0)
            {
                key.Dispose();
                Registry.CurrentUser.DeleteSubKey(HealthCheckSupport.AppCompatLayersKey, throwOnMissingSubKey: false);
                details.Add("收尾：本用例创建的空 Layers 键已删除（原本不存在）。");
            }
            else
            {
                details.Add($"收尾：Layers 键仍有 {key.ValueCount} 个值（其他应用写入），按要求保留。");
            }
        }
        catch (Exception ex)
        {
            details.Add($"收尾警告：{ex.Message}");
        }
    }
}
