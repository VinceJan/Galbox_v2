using Galbox.App.Models;
using Galbox.App.Services;
using Galbox.Data.Entities;

namespace Galbox.Acceptance.Checks;

/// <summary>
/// A70 - the eight diagnosis items of product spec §4.4 fire exactly when their trigger is present,
/// with the severity the spec assigns, and stay silent when it is not.
///
/// The defect this guards against is a diagnosis that is either blind (misses a real problem, so the
/// user never learns why the game will not start) or noisy (flags a Critical problem that is not
/// there). The check therefore drives <see cref="IErrorCheckingService.CheckCategoryAsync"/> with
/// hand-built fixtures whose trigger state is known by construction, and derives the expected verdict
/// from an independent oracle (the trigger conditions written out in this file), never from the
/// service's own answer.
///
/// Differential note: the "Expansion Probe" fixture FAILS on the revision this check was written for.
/// The pre-fix <c>XpVistaIndicatorPattern</c> searched for the bare substring "xp", so the name
/// "Expansion Pack Probe" was reported as a Critical Windows-compatibility problem.
/// </summary>
public sealed class A70HealthDiagnosisCheck : IAcceptanceCheck
{
    /// <inheritdoc />
    public string Id => "A70";

    /// <inheritdoc />
    public string Title => "健康诊断：按触发条件准确报出 8 项、严重度符合规格、且不误报";

    /// <inheritdoc />
    public async Task<CheckResult> RunAsync(AcceptanceContext context, CancellationToken cancellationToken)
    {
        var expected = "8 个诊断项各自在触发条件成立时报出、条件不成立时保持沉默，严重度与规格一致"
                     + "（中文路径 Major / 区域设置 Critical / DirectX Major / 解码器 Minor / "
                     + "Windows 兼容性 Critical / 运行库 Critical / 权限 Major / 杀软 Info）";

        var details = new List<string>();
        var failures = new List<string>();
        var service = context.Get<IErrorCheckingService>();
        var work = AcceptanceWork.Create("a70");

        details.Add($"工作目录        : {work}");
        details.Add($"工作目录纯 ASCII : {HealthCheckSupport.IsPureAscii(work)}（中文路径用例需要 ASCII 的上层目录）");

        // Measured, machine-dependent preconditions. The expectation below is derived from them, so
        // a machine that already ships DirectX 9 / the VC++ runtime / a codec pack cannot produce a
        // false FAIL - and cannot hide a missing finding either.
        var codecPack = HealthCheckSupport.CodecPackInstalled();
        var dx9Installed = HealthCheckSupport.SystemDllInstalled("d3dx9_43.dll");
        var vcRuntimeInstalled = HealthCheckSupport.SystemDllInstalled("msvcp140.dll");

        details.Add(string.Empty);
        details.Add("=== 本机前置条件（实测，用于推导期望值） ===");
        details.Add($"  K-Lite 类解码器已安装 : {codecPack}");
        details.Add($"  SysWOW64/System32 有 d3dx9_43.dll : {dx9Installed}");
        details.Add($"  SysWOW64/System32 有 msvcp140.dll : {vcRuntimeInstalled}");

        // ------------------------------------------------------------------ fixtures
        var chineseFolder = Path.Combine(work, "中文测试游戏");
        AcceptanceWork.WritePattern(Path.Combine(chineseFolder, "probe.exe"), 256, 0x4D);

        var localeFolder = Path.Combine(work, "LocaleProbe");
        AcceptanceWork.WritePattern(Path.Combine(localeFolder, "probe.exe"), 256, 0x4D);
        AcceptanceWork.WriteText(Path.Combine(localeFolder, "locale.jp"), "# japanese locale marker");

        var legacyFolder = Path.Combine(work, "LegacyCompatProbe");
        AcceptanceWork.WritePattern(Path.Combine(legacyFolder, "probe.exe"), 256, 0x4D);
        AcceptanceWork.WriteText(
            Path.Combine(legacyFolder, "readme.txt"),
            "SYSTEM REQUIREMENTS: Windows XP SP3 or Windows Vista. Requires DirectX 9.0c.");

        var expansionFolder = Path.Combine(work, "ExpansionProbe");
        AcceptanceWork.WritePattern(Path.Combine(expansionFolder, "probe.exe"), 256, 0x4D);
        AcceptanceWork.WriteText(
            Path.Combine(expansionFolder, "readme.txt"),
            "Expansion pack contents: 3 extra scenarios, 12 new CGs. No OS requirement listed.");

        var videoFolder = Path.Combine(work, "VideoProbe");
        AcceptanceWork.WritePattern(Path.Combine(videoFolder, "probe.exe"), 256, 0x4D);
        AcceptanceWork.WritePattern(Path.Combine(videoFolder, "opening.mp4"), 1024, 0x00);

        var directXFolder = Path.Combine(work, "DirectXProbe");
        AcceptanceWork.WritePattern(Path.Combine(directXFolder, "probe.exe"), 256, 0x4D);
        AcceptanceWork.WritePattern(Path.Combine(directXFolder, "d3dx9_43.dll"), 128, 0x00);

        var runtimeFolder = Path.Combine(work, "RuntimeProbe");
        AcceptanceWork.WritePattern(Path.Combine(runtimeFolder, "probe.exe"), 256, 0x4D);
        AcceptanceWork.WritePattern(Path.Combine(runtimeFolder, "msvcp140.dll"), 128, 0x00);

        var antivirusFolder = Path.Combine(work, "AntivirusProbe");
        AcceptanceWork.WritePattern(Path.Combine(antivirusFolder, "probe.exe"), 256, 0x4D);

        var cleanFolder = Path.Combine(work, "CleanProbe");
        AcceptanceWork.WritePattern(Path.Combine(cleanFolder, "probe.exe"), 256, 0x4D);

        // ------------------------------------------------------------------ cases
        details.Add(string.Empty);
        details.Add("=== 逐项触发条件与判定 ===");

        var chineseGame = BuildGame(1, "Chinese Path Probe", chineseFolder, "probe.exe");
        var localeGame = BuildGame(2, "Locale Probe", localeFolder, "probe.exe");
        var legacyGame = BuildGame(3, "Legacy Compat Probe", legacyFolder, "probe.exe");
        var expansionGame = BuildGame(4, "Expansion Pack Probe", expansionFolder, "probe.exe");
        var videoGame = BuildGame(5, "Video Probe", videoFolder, "probe.exe");
        var directXGame = BuildGame(6, "DirectX Probe", directXFolder, "probe.exe");
        var runtimeGame = BuildGame(7, "Runtime Probe", runtimeFolder, "probe.exe");
        var antivirusGame = BuildGame(8, "日本語ギャルゲー", antivirusFolder, "probe.exe");
        var cleanGame = BuildGame(9, "Clean English Probe", cleanFolder, "probe.exe");

        await AssertCaseAsync(service, "中文路径 fixture（目录名含中文）", chineseGame, ErrorCategory.ChineseDirectory,
            present: true, ErrorSeverity.Major, details, failures, cancellationToken).ConfigureAwait(false);

        await AssertCaseAsync(service, "中文路径 fixture（同一游戏不应误报区域设置）", chineseGame, ErrorCategory.LocaleRequirement,
            present: false, ErrorSeverity.Critical, details, failures, cancellationToken).ConfigureAwait(false);

        await AssertCaseAsync(service, "区域设置 fixture（含 locale.jp）", localeGame, ErrorCategory.LocaleRequirement,
            present: true, ErrorSeverity.Critical, details, failures, cancellationToken).ConfigureAwait(false);

        await AssertCaseAsync(service, "Windows 兼容性 fixture（readme 提到 Windows XP）", legacyGame, ErrorCategory.WindowsCompatibility,
            present: true, ErrorSeverity.Critical, details, failures, cancellationToken).ConfigureAwait(false);

        await AssertCaseAsync(service, "误报 fixture（游戏名 Expansion，readme 无 OS 要求）", expansionGame, ErrorCategory.WindowsCompatibility,
            present: false, ErrorSeverity.Critical, details, failures, cancellationToken).ConfigureAwait(false);

        await AssertCaseAsync(service, $"解码器 fixture（含 mp4，本机已装解码器={codecPack}）", videoGame, ErrorCategory.KLiteCodecMissing,
            present: !codecPack, ErrorSeverity.Minor, details, failures, cancellationToken).ConfigureAwait(false);

        await AssertCaseAsync(service, $"DirectX fixture（含 d3dx9_43.dll，本机已装={dx9Installed}）", directXGame, ErrorCategory.DirectXMissing,
            present: !dx9Installed, ErrorSeverity.Major, details, failures, cancellationToken).ConfigureAwait(false);

        await AssertCaseAsync(service, $"运行库 fixture（含 msvcp140.dll，本机已装={vcRuntimeInstalled}）", runtimeGame, ErrorCategory.RuntimeMissing,
            present: !vcRuntimeInstalled, ErrorSeverity.Critical, details, failures, cancellationToken).ConfigureAwait(false);

        await AssertCaseAsync(service, "杀软 fixture（日文原名 + 可执行文件存在）", antivirusGame, ErrorCategory.AntivirusBlocking,
            present: true, ErrorSeverity.Info, details, failures, cancellationToken).ConfigureAwait(false);

        // PermissionIssue cannot be constructed without administrator rights: the service only probes
        // ACLs when the game sits under %ProgramFiles%, and this process runs at medium integrity.
        // What CAN be asserted - and is asserted - is that a game outside Program Files is never
        // reported as a permission problem.
        await AssertCaseAsync(service, "权限 fixture（非 Program Files，不应误报）", cleanGame, ErrorCategory.PermissionIssue,
            present: false, ErrorSeverity.Major, details, failures, cancellationToken).ConfigureAwait(false);

        details.Add(string.Empty);
        details.Add("=== 干净 fixture：8 项全部保持沉默 ===");
        foreach (var category in Enum.GetValues<ErrorCategory>())
        {
            if (category == ErrorCategory.GameDependency)
            {
                details.Add($"  {category,-22}: 跳过（规格 §4.4 明确只有分类、没有实现）");
                continue;
            }

            var finding = await service.CheckCategoryAsync(cleanGame, category, cancellationToken).ConfigureAwait(false);
            var ok = finding is null;
            details.Add($"  {category,-22}: {(finding is null ? "无（OK）" : $"误报 {finding.Severity} \"{finding.Title}\"（FAIL）")}");
            if (!ok)
            {
                failures.Add($"干净 fixture 误报 {category}: {finding!.Severity} \"{finding.Title}\"");
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
            ? $"全部诊断项按触发条件准确报出（codecPack={codecPack}, dx9={dx9Installed}, vc={vcRuntimeInstalled}）"
            : $"{failures.Count} 项与规格不符";

        return (failures.Count == 0
                ? CheckResult.Pass(Id, Title, expected, actual)
                : CheckResult.Fail(Id, Title, expected, actual))
            .With(details.ToArray());
    }

    private static GameInfo BuildGame(int id, string name, string folder, string executableName) => new()
    {
        Id = id,
        NameOriginal = name,
        InstallPath = folder,
        MainExecutable = Path.Combine(folder, executableName)
    };

    private static async Task AssertCaseAsync(
        IErrorCheckingService service,
        string label,
        GameInfo game,
        ErrorCategory category,
        bool present,
        ErrorSeverity severity,
        List<string> details,
        List<string> failures,
        CancellationToken cancellationToken)
    {
        var finding = await service.CheckCategoryAsync(game, category, cancellationToken).ConfigureAwait(false);

        var expectedText = present ? severity.ToString() : "无";
        var actualText = finding is null ? "无" : finding.Severity.ToString();
        var ok = (finding is not null) == present && (!present || finding!.Severity == severity);

        details.Add($"  [{(ok ? "OK  " : "FAIL")}] {label}");
        details.Add($"         类别={category} 期望={expectedText} 实测={actualText}");

        if (finding is not null)
        {
            details.Add($"         Title=\"{finding.Title}\" SolutionType={finding.SolutionType} AutoFix={finding.AutoFixAvailable}");
        }

        if (!ok)
        {
            failures.Add($"{label}: 类别 {category} 期望 {expectedText}，实测 {actualText}");
        }
    }
}
