using Galbox.App.Models;
using Galbox.App.Services;
using Galbox.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Galbox.Acceptance.Checks;

/// <summary>
/// A74 - the user-visible text of the health diagnosis is Chinese.
///
/// The product spec records the defect verbatim: "错误标题与方案文案还是英文，未本地化". Every string a
/// user reads - title, description, suggested solution, category, severity, solution level, the
/// filter labels of the report page, and the rows that were persisted - is checked here for two
/// things at once: it contains Chinese characters, and it contains none of the retired English
/// fragments. The second half matters because the naive fix for this defect is translating the eight
/// factories and forgetting the strings the page builds itself.
/// </summary>
public sealed class A74DiagnosisLocalizationCheck : IAcceptanceCheck
{
    /// <inheritdoc />
    public string Id => "A74";

    /// <inheritdoc />
    public string Title => "诊断文案中文化：标题、说明、方案、分级与持久化记录均无英文残留";

    /// <inheritdoc />
    public async Task<CheckResult> RunAsync(AcceptanceContext context, CancellationToken cancellationToken)
    {
        var expected = "8 个诊断项的 Title/Description/SolutionInstructions、严重度与分类显示名、解决方式显示名、"
                     + "报告页筛选标签、以及写入 ErrorRecords 的记录都含中文且不含未本地化的英文片段";

        var details = new List<string>();
        var failures = new List<string>();
        var createdGameIds = new List<int>();

        var samples = new (string Label, GameErrorInfo Finding)[]
        {
            ("路径含中文字符", GameErrorInfo.CreateChineseDirectoryError(0, @"D:\游戏\Probe", "游戏, 戏")),
            ("区域设置要求", GameErrorInfo.CreateLocaleRequirementError(0, "Japanese (Shift-JIS)")),
            ("DirectX 缺失", GameErrorInfo.CreateDirectXMissingError(0, "d3dx9_43.dll", "9.0c")),
            ("视频解码器缺失", GameErrorInfo.CreateKLiteCodecMissingError(0)),
            ("Windows 兼容性", GameErrorInfo.CreateWindowsCompatibilityError(0, "Windows XP/Vista", "Windows 7")),
            ("运行库缺失", GameErrorInfo.CreateRuntimeMissingError(0, "Visual C++", "2015-2022")),
            ("权限问题", GameErrorInfo.CreatePermissionIssueError(0, @"C:\Program Files\Probe")),
            ("杀软拦截", GameErrorInfo.CreateAntivirusBlockingWarning(0, "game.exe"))
        };

        details.Add("=== 用例 1：8 个诊断项的正文 ===");
        foreach (var (label, finding) in samples)
        {
            details.Add($"  --- {label} ---");
            details.Add($"      Title       : {finding.Title}");
            details.Add($"      Description : {finding.Description}");
            details.Add($"      Solution    : {finding.SolutionInstructions}");

            Report(failures, HealthCheckSupport.CheckLocalized($"{label}/Title", finding.Title));
            Report(failures, HealthCheckSupport.CheckLocalized($"{label}/Description", finding.Description));
            Report(failures, HealthCheckSupport.CheckLocalized($"{label}/SolutionInstructions", finding.SolutionInstructions));
        }

        details.Add(string.Empty);
        details.Add("=== 用例 2：分级与分类的显示名 ===");
        foreach (var category in Enum.GetValues<ErrorCategory>())
        {
            var finding = new GameErrorInfo { Category = category };
            details.Add($"  {category,-22} -> CategoryDisplay=\"{finding.CategoryDisplay}\"");
            Report(failures, HealthCheckSupport.CheckLocalized($"{category}/CategoryDisplay", finding.CategoryDisplay));
        }

        foreach (var severity in Enum.GetValues<ErrorSeverity>())
        {
            var finding = new GameErrorInfo { Severity = severity };
            details.Add($"  {severity,-22} -> SeverityDisplay=\"{finding.SeverityDisplay}\"");
            Report(failures, HealthCheckSupport.CheckLocalized($"{severity}/SeverityDisplay", finding.SeverityDisplay));
        }

        foreach (var solutionType in Enum.GetValues<SolutionType>())
        {
            var finding = new GameErrorInfo { SolutionType = solutionType };
            details.Add($"  {solutionType,-22} -> SolutionTypeDisplay=\"{finding.SolutionTypeDisplay}\"");
            Report(failures, HealthCheckSupport.CheckLocalized($"{solutionType}/SolutionTypeDisplay", finding.SolutionTypeDisplay));
        }

        // The "library-wide" list on the report page shows the raw values stored in the database,
        // so the stored names need a Chinese label as well.
        details.Add(string.Empty);
        details.Add("=== 用例 3：数据库中的字符串（严重度/分类）也有中文显示名 ===");
        var textType = ReflectionBridge.FindType("Galbox.App.Models.DiagnosisText");
        details.Add($"  DiagnosisText 类型 : {(textType is null ? "不存在（FAIL）" : textType.FullName)}");
        if (textType is null)
        {
            failures.Add("用例3：DiagnosisText 不存在，数据库中存的 Critical/Major 等字符串没有中文显示名");
        }
        else
        {
            foreach (var stored in new[] { "Critical", "Major", "Minor", "Info" })
            {
                var label = InvokeStaticString(textType, "StoredSeverityLabel", stored);
                details.Add($"  StoredSeverityLabel(\"{stored}\") = \"{label}\"");
                Report(failures, HealthCheckSupport.CheckLocalized($"StoredSeverityLabel({stored})", label));
            }

            foreach (var stored in new[] { "ChineseDirectory", "LocaleRequirement", "DirectXMissing", "KLiteCodecMissing", "WindowsCompatibility", "RuntimeMissing", "GameDependency", "PermissionIssue", "AntivirusBlocking" })
            {
                var label = InvokeStaticString(textType, "StoredCategoryLabel", stored);
                details.Add($"  StoredCategoryLabel(\"{stored}\") = \"{label}\"");
                Report(failures, HealthCheckSupport.CheckLocalized($"StoredCategoryLabel({stored})", label));
            }

            var (convertersOk, converterLine) = CheckConvertersDelegateToDiagnosisText();
            details.Add(converterLine);
            if (!convertersOk)
            {
                failures.Add("用例3：报告页的严重度/分类转换器没有复用 DiagnosisText，存在两份会各自漂移的表");
            }
        }

        details.Add(string.Empty);
        details.Add("=== 用例 4：错误报告页自身的文案（真实 ViewModel 实例） ===");
        var viewModel = TryCreateReportViewModel(context, details);
        if (viewModel is null)
        {
            failures.Add("用例4：无法构造 ErrorReportViewModel");
        }
        else
        {
            foreach (var (propertyName, label) in new[]
                     {
                         ("SeverityFilterOptions", "严重程度筛选"),
                         ("CategoryFilterOptions", "类别筛选")
                     })
            {
                var options = ReflectionBridge.Property(viewModel, propertyName) as System.Collections.IEnumerable;
                var count = 0;
                if (options is not null)
                {
                    foreach (var option in options)
                    {
                        count++;
                        var displayName = ReflectionBridge.String(option, "DisplayName");
                        details.Add($"  {label} [{count}] = \"{displayName}\"");
                        Report(failures, HealthCheckSupport.CheckLocalized($"{label}[{count}]", displayName));
                    }
                }

                if (count == 0)
                {
                    failures.Add($"用例4：{propertyName} 为空，界面上的筛选下拉框没有可显示的中文标签");
                }
            }

            // Status messages the user sees when the page is used with nothing selected.
            // Both methods take a single GameInfo? argument.
            foreach (var methodName in new[] { "CheckGameErrorsAsync", "LoadGameErrorHistoryAsync" })
            {
                var (ok, _, error) = await ReflectionBridge
                    .CallAsync(viewModel, methodName, new object?[] { null })
                    .ConfigureAwait(false);

                var status = ReflectionBridge.String(viewModel, "StatusMessage");
                details.Add($"  {methodName}(null) ok={ok}{(error is null ? string.Empty : $" ({error})")} StatusMessage=\"{status}\"");
                Report(failures, HealthCheckSupport.CheckLocalized($"{methodName}/StatusMessage", status));

                if (!ok)
                {
                    failures.Add($"用例4：{methodName} 调用失败 - {error}");
                }
            }
        }

        details.Add(string.Empty);
        details.Add("=== 用例 5：写进数据库的诊断记录同样是中文 ===");
        var work = AcceptanceWork.Create("a74");
        var folder = Path.Combine(work, "中文游戏目录");
        var exePath = AcceptanceWork.WritePattern(Path.Combine(folder, "probe.exe"), 512, 0x4D);

        var gameId = await HealthCheckSupport
            .InsertGameAsync(context, "A74 本地化探针", folder, exePath, null, cancellationToken)
            .ConfigureAwait(false);
        createdGameIds.Add(gameId);

        try
        {
            GameInfo game;
            await using (var scope = context.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<GalboxDbContext>();
                game = await db.Games.AsNoTracking().FirstAsync(g => g.Id == gameId, cancellationToken).ConfigureAwait(false);
            }

            var errorChecking = context.Get<IErrorCheckingService>();
            await errorChecking.CheckGameAsync(game, cancellationToken).ConfigureAwait(false);

            List<GameErrorRecord> records;
            await using (var scope = context.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<GalboxDbContext>();
                records = await db.ErrorRecords
                    .AsNoTracking()
                    .Where(r => r.GameInfoId == gameId)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            details.Add($"  持久化记录数 : {records.Count}");
            if (records.Count == 0)
            {
                failures.Add("用例5：fixture 没有产生任何错误记录，无法验证持久化文案");
            }

            foreach (var record in records)
            {
                details.Add($"  --- 记录 {record.Id} [{record.Category}/{record.Severity}/{record.SolutionType}] ---");
                details.Add($"      Title       : {record.Title}");
                details.Add($"      Description : {record.Description}");
                details.Add($"      Solution    : {record.SolutionInstructions}");

                Report(failures, HealthCheckSupport.CheckLocalized($"记录{record.Id}/Title", record.Title));
                Report(failures, HealthCheckSupport.CheckLocalized($"记录{record.Id}/Description", record.Description));
                Report(failures, HealthCheckSupport.CheckLocalized($"记录{record.Id}/SolutionInstructions", record.SolutionInstructions));
            }
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
            ? "全部用户可见文案均为中文且无英文残留"
            : $"{failures.Count} 处文案未通过";

        return (failures.Count == 0
                ? CheckResult.Pass(Id, Title, expected, actual)
                : CheckResult.Fail(Id, Title, expected, actual))
            .With(details.ToArray());
    }

    private static void Report(List<string> failures, string? problem)
    {
        if (problem is not null)
        {
            failures.Add(problem);
        }
    }

    private static string InvokeStaticString(Type type, string methodName, string argument)
    {
        var method = type.GetMethod(methodName, new[] { typeof(string) });
        if (method is null)
        {
            return $"(方法 {methodName}(string) 不存在)";
        }

        try
        {
            return method.Invoke(null, new object[] { argument }) as string ?? "(null)";
        }
        catch (Exception ex)
        {
            return $"(调用失败: {ex.Message})";
        }
    }

    /// <summary>
    /// The two XAML converters of the report page must delegate to <c>DiagnosisText</c>; a second,
    /// hand-maintained table is how a translation drifts apart from the model.
    /// </summary>
    private static (bool Ok, string Line) CheckConvertersDelegateToDiagnosisText()
    {
        var repoRoot = RepoLocator.FindRepoRoot();
        if (repoRoot is null)
        {
            return (false, "  [FAIL] 无法定位仓库根目录，未检查转换器");
        }

        var path = Path.Combine(repoRoot, "src", "Galbox.App", "Converters", "CommonConverters.cs");
        if (!File.Exists(path))
        {
            return (false, $"  [FAIL] {path} 不存在");
        }

        var text = File.ReadAllText(path);
        var hasSeverity = text.Contains("ErrorSeverityDisplayConverter", StringComparison.Ordinal)
                       && text.Contains("DiagnosisText.SeverityLabel", StringComparison.Ordinal);
        var hasCategory = text.Contains("ErrorCategoryDisplayConverter", StringComparison.Ordinal)
                       && text.Contains("DiagnosisText.CategoryLabel", StringComparison.Ordinal);

        var ok = hasSeverity && hasCategory;
        return (ok,
            $"  [{(ok ? "OK  " : "FAIL")}] 转换器 ErrorSeverityDisplayConverter / "
            + $"ErrorCategoryDisplayConverter 复用 DiagnosisText（严重度={hasSeverity}, 分类={hasCategory}）");
    }

    private static object? TryCreateReportViewModel(AcceptanceContext context, List<string> details)
    {
        var viewModelType = ReflectionBridge.FindType("Galbox.App.ViewModels.ErrorReportViewModel");
        if (viewModelType is null)
        {
            details.Add("  ErrorReportViewModel 类型不存在。");
            return null;
        }

        var constructor = viewModelType
            .GetConstructors()
            .OrderByDescending(candidate => candidate.GetParameters().Length)
            .First();

        try
        {
            var arguments = constructor
                .GetParameters()
                .Select(parameter => context.Services.GetRequiredService(parameter.ParameterType))
                .ToArray();

            return constructor.Invoke(arguments);
        }
        catch (Exception ex)
        {
            details.Add($"  构造 ErrorReportViewModel 失败：{ex.Message}");
            return null;
        }
    }
}
