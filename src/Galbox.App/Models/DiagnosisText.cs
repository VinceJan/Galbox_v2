using Galbox.App.Services;

namespace Galbox.App.Models;

/// <summary>
/// Chinese labels for the three diagnosis enums, including the names that are persisted in the
/// database.
/// </summary>
/// <remarks>
/// Why this exists as a separate type rather than inside the XAML: the report page shows two
/// different things side by side - freshly detected findings (which carry an enum) and the
/// library-wide list of stored rows (which carries the enum's <i>name</i> as text, e.g. "Critical").
/// Both need the same Chinese label, so the mapping has to live somewhere both can reach. The two
/// value converters of the report page delegate here instead of carrying their own copy.
/// </remarks>
public static class DiagnosisText
{
    /// <summary>Chinese label of a severity (product spec §3.5: Critical / Major / Minor / Info).</summary>
    public static string SeverityLabel(ErrorSeverity severity) => severity switch
    {
        ErrorSeverity.Critical => "严重",
        ErrorSeverity.Major => "重要",
        ErrorSeverity.Minor => "轻微",
        ErrorSeverity.Info => "提示",
        _ => "未知"
    };

    /// <summary>Chinese label of a stored severity name, defaulting to the least severe.</summary>
    public static string StoredSeverityLabel(string? storedSeverity) =>
        Enum.TryParse<ErrorSeverity>(storedSeverity, ignoreCase: true, out var parsed)
            ? SeverityLabel(parsed)
            : SeverityLabel(ErrorSeverity.Info);

    /// <summary>Chinese label of a diagnosis category (product spec §4.4 lists nine).</summary>
    public static string CategoryLabel(ErrorCategory category) => category switch
    {
        ErrorCategory.ChineseDirectory => "路径含中文",
        ErrorCategory.LocaleRequirement => "区域设置要求",
        ErrorCategory.DirectXMissing => "缺 DirectX",
        ErrorCategory.KLiteCodecMissing => "缺 K-Lite 解码器",
        ErrorCategory.WindowsCompatibility => "Windows 兼容性",
        ErrorCategory.RuntimeMissing => "缺运行时",
        ErrorCategory.GameDependency => "缺游戏专属依赖",
        ErrorCategory.PermissionIssue => "权限问题",
        ErrorCategory.AntivirusBlocking => "杀软拦截",
        _ => "未知分类"
    };

    /// <summary>Chinese label of a stored category name.</summary>
    public static string StoredCategoryLabel(string? storedCategory) =>
        Enum.TryParse<ErrorCategory>(storedCategory, ignoreCase: true, out var parsed)
            ? CategoryLabel(parsed)
            : "未知分类";

    /// <summary>Chinese label of a solution level (product spec §3.5: 自动修复 / 手动修复 / 需外部工具 / 无可用方案).</summary>
    public static string SolutionTypeLabel(SolutionType solutionType) => solutionType switch
    {
        SolutionType.AutoFix => "自动修复",
        SolutionType.ManualFix => "手动修复",
        SolutionType.ExternalTool => "需外部工具",
        SolutionType.None => "无可用方案",
        _ => "未知方案"
    };

    /// <summary>Chinese label of a stored solution level name.</summary>
    public static string StoredSolutionTypeLabel(string? storedSolutionType) =>
        Enum.TryParse<SolutionType>(storedSolutionType, ignoreCase: true, out var parsed)
            ? SolutionTypeLabel(parsed)
            : SolutionTypeLabel(SolutionType.None);

    /// <summary>
    /// Chinese sentence describing what the user can expect from a solution level, for the badge next
    /// to a finding.
    /// </summary>
    public static string SolutionTypeDescription(SolutionType solutionType) => solutionType switch
    {
        SolutionType.AutoFix => "可以在本页点「一键修复」直接处理",
        SolutionType.ManualFix => "需要你按说明手工处理",
        SolutionType.ExternalTool => "需要下载并安装外部工具",
        SolutionType.None => "目前没有可靠的解决办法",
        _ => "未知"
    };
}
