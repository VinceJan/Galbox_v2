using Galbox.App.Services;

namespace Galbox.App.Models;

/// <summary>
/// Represents a detected error or issue with a game.
/// </summary>
public class GameErrorInfo
{
    /// <summary>
    /// Unique identifier for this error instance.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// The game ID this error is associated with.
    /// </summary>
    public int GameId { get; set; }

    /// <summary>
    /// The category of error detected.
    /// </summary>
    public ErrorCategory Category { get; set; }

    /// <summary>
    /// Severity level of the error.
    /// </summary>
    public ErrorSeverity Severity { get; set; }

    /// <summary>
    /// Short title describing the error.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Detailed description of the error.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Type of solution available for this error.
    /// </summary>
    public SolutionType SolutionType { get; set; }

    /// <summary>
    /// Step-by-step instructions to resolve the error.
    /// </summary>
    public string SolutionInstructions { get; set; } = string.Empty;

    /// <summary>
    /// Whether an automatic fix is available.
    /// </summary>
    public bool AutoFixAvailable { get; set; }

    /// <summary>
    /// Action to perform for auto-fix (e.g., rename folder, download link).
    /// </summary>
    public string? FixAction { get; set; }

    /// <summary>
    /// Download URL for external tools if needed.
    /// </summary>
    public string? DownloadUrl { get; set; }

    /// <summary>
    /// Tool name for external tools.
    /// </summary>
    public string? ToolName { get; set; }

    /// <summary>
    /// Additional context or details about the error.
    /// </summary>
    public Dictionary<string, object> ContextData { get; set; } = new();

    /// <summary>
    /// Time when the error was detected.
    /// </summary>
    public DateTime DetectedTime { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Whether this error has been resolved.
    /// </summary>
    public bool IsResolved { get; set; }

    /// <summary>
    /// Time when the error was resolved (if applicable).
    /// </summary>
    public DateTime? ResolvedTime { get; set; }

    /// <summary>
    /// Gets the severity display text.
    /// </summary>
    public string SeverityDisplay => DiagnosisText.SeverityLabel(Severity);

    /// <summary>
    /// Gets the category display text.
    /// </summary>
    public string CategoryDisplay => DiagnosisText.CategoryLabel(Category);

    /// <summary>
    /// Gets the solution type display text.
    /// </summary>
    public string SolutionTypeDisplay => DiagnosisText.SolutionTypeLabel(SolutionType);

    /// <summary>
    /// Gets a one-line Chinese explanation of what the solution level means for the user.
    /// </summary>
    public string SolutionTypeDescription => DiagnosisText.SolutionTypeDescription(SolutionType);

    /// <summary>
    /// Creates a Chinese directory error.
    /// </summary>
    /// <param name="gameId">The game the finding belongs to.</param>
    /// <param name="path">The install path that contains Chinese characters.</param>
    /// <param name="chineseSegments">The Chinese characters that were found, for the description.</param>
    /// <param name="canAutoFix">
    /// False when the Chinese characters sit in a parent folder rather than in the game folder
    /// itself. Renaming the game folder cannot repair that, and promising a one-click repair in that
    /// case would be exactly the kind of "button without a door" this project has to stop producing.
    /// </param>
    public static GameErrorInfo CreateChineseDirectoryError(
        int gameId,
        string path,
        string chineseSegments,
        bool canAutoFix = true)
    {
        return new GameErrorInfo
        {
            GameId = gameId,
            Category = ErrorCategory.ChineseDirectory,
            Severity = ErrorSeverity.Major,
            Title = canAutoFix ? "游戏路径包含中文字符" : "游戏所在的上层目录包含中文字符",
            Description = canAutoFix
                ? $"游戏路径中含中文字符：{chineseSegments}。部分日文游戏在中文路径下无法运行，或无法读写存档。"
                : $"游戏路径中含中文字符：{chineseSegments}，但它们出现在游戏目录之外的上层目录里。"
                  + "重命名游戏目录解决不了这个问题，需要把游戏整个移到纯英文路径。",
            SolutionType = canAutoFix ? SolutionType.AutoFix : SolutionType.ManualFix,
            SolutionInstructions = canAutoFix
                ? "点「一键修复」把游戏目录改成英文或拼音名（例如 游戏 → Game），程序会同时更新库里的安装路径、"
                  + "主程序路径和备选程序路径。重命名前会自动确认游戏没有在运行，失败会回滚。"
                : "把游戏移动到纯英文路径（例如 D:\\Games\\），然后在库里重新扫描或修正安装路径。",
            AutoFixAvailable = canAutoFix,
            FixAction = canAutoFix ? "RenameInstallPath" : null,
            ContextData = new Dictionary<string, object>
            {
                ["Path"] = path,
                ["ChineseSegments"] = chineseSegments,
                ["ChineseInParentFolder"] = !canAutoFix
            }
        };
    }

    /// <summary>
    /// Creates a locale requirement error.
    /// </summary>
    public static GameErrorInfo CreateLocaleRequirementError(int gameId, string requiredLocale)
    {
        return new GameErrorInfo
        {
            GameId = gameId,
            Category = ErrorCategory.LocaleRequirement,
            Severity = ErrorSeverity.Critical,
            Title = "需要日文区域设置",
            Description = $"该游戏需要 {requiredLocale} 区域设置才能正常运行，区域不对会出现文字乱码、字体缺失或启动即崩溃。",
            SolutionType = SolutionType.ExternalTool,
            SolutionInstructions = "用 Locale Emulator 或 NTLEA 以日文区域启动游戏。这两个工具可以只对单个进程临时改区域，"
                                + "不影响系统全局设置。Galbox 不代你安装它们：它们需要挂钩进程加载，属于系统级工具，"
                                + "安装必须由你确认。",
            AutoFixAvailable = false,
            DownloadUrl = "https://github.com/xupefei/Locale-Emulator",
            ToolName = "Locale Emulator",
            ContextData = new Dictionary<string, object>
            {
                ["RequiredLocale"] = requiredLocale
            }
        };
    }

    /// <summary>
    /// Creates a DirectX missing error.
    /// </summary>
    public static GameErrorInfo CreateDirectXMissingError(int gameId, string missingDll, string directXVersion)
    {
        return new GameErrorInfo
        {
            GameId = gameId,
            Category = ErrorCategory.DirectXMissing,
            Severity = ErrorSeverity.Major,
            Title = "缺少 DirectX 组件",
            Description = $"游戏需要的 {missingDll} 在本机没有找到（游戏要求 DirectX {directXVersion}），"
                        + "缺少它可能表现为启动失败、黑屏或过场动画不播放。",
            SolutionType = SolutionType.ExternalTool,
            SolutionInstructions = "安装微软官方的 DirectX End-User Runtime，它会一次性补齐 DirectX 9.0c 的全部组件。"
                                + "Galbox 不代你安装运行时：那是系统级改动，需要你自己确认。",
            AutoFixAvailable = false,
            DownloadUrl = "https://www.microsoft.com/download/details.aspx?id=35",
            ToolName = "DirectX End-User Runtime",
            ContextData = new Dictionary<string, object>
            {
                ["MissingDll"] = missingDll,
                ["DirectXVersion"] = directXVersion
            }
        };
    }

    /// <summary>
    /// Creates a KLite codec missing error.
    /// </summary>
    public static GameErrorInfo CreateKLiteCodecMissingError(int gameId)
    {
        return new GameErrorInfo
        {
            GameId = gameId,
            Category = ErrorCategory.KLiteCodecMissing,
            Severity = ErrorSeverity.Minor,
            Title = "缺少视频解码器",
            Description = "游戏目录里有视频文件，但本机没有检测到常见的解码器包。游戏本体可以玩，过场动画可能黑屏或没有声音。",
            SolutionType = SolutionType.ExternalTool,
            SolutionInstructions = "安装 K-Lite Codec Pack（推荐 Basic 或 Standard 版）即可播放游戏内的视频。"
                                + "这是可选的：如果不介意过场动画，可以不管这一项。",
            AutoFixAvailable = false,
            DownloadUrl = "https://codecguide.com/download_kl.htm",
            ToolName = "K-Lite Codec Pack",
            ContextData = new Dictionary<string, object>()
        };
    }

    /// <summary>
    /// Creates a Windows compatibility error.
    /// </summary>
    public static GameErrorInfo CreateWindowsCompatibilityError(int gameId, string originalOs, string compatibilityMode)
    {
        return new GameErrorInfo
        {
            GameId = gameId,
            Category = ErrorCategory.WindowsCompatibility,
            Severity = ErrorSeverity.Critical,
            Title = "需要 Windows 兼容模式",
            Description = $"该游戏面向 {originalOs} 时代，在当前 Windows 上可能启动失败、花屏或无法切换全屏。",
            SolutionType = SolutionType.AutoFix,
            SolutionInstructions = $"点「一键修复」为游戏主程序写入 Windows 兼容模式（{compatibilityMode}）。"
                                + "设置只写入当前用户（HKCU），随时可以点「撤销」还原；如果兼容模式仍然无效，"
                                + "XP/Vista 时代的游戏建议放进虚拟机运行。",
            AutoFixAvailable = true,
            FixAction = "ApplyWindowsCompatibilityMode",
            ContextData = new Dictionary<string, object>
            {
                ["OriginalOS"] = originalOs,
                ["CompatibilityMode"] = compatibilityMode
            }
        };
    }

    /// <summary>
    /// Creates a runtime missing error.
    /// </summary>
    public static GameErrorInfo CreateRuntimeMissingError(int gameId, string runtimeName, string version)
    {
        var (downloadUrl, instructions) = GetRuntimeDownloadInfo(runtimeName, version);

        return new GameErrorInfo
        {
            GameId = gameId,
            Category = ErrorCategory.RuntimeMissing,
            Severity = ErrorSeverity.Critical,
            Title = $"缺少运行库 {runtimeName}",
            Description = $"游戏依赖 {runtimeName} {version}，本机没有检测到它。缺少运行库时游戏通常在启动瞬间报错退出。",
            SolutionType = SolutionType.ExternalTool,
            SolutionInstructions = instructions,
            AutoFixAvailable = false,
            DownloadUrl = downloadUrl,
            ToolName = runtimeName,
            ContextData = new Dictionary<string, object>
            {
                ["RuntimeName"] = runtimeName,
                ["Version"] = version
            }
        };
    }

    /// <summary>
    /// Gets download URL and instructions for various runtimes.
    /// </summary>
    private static (string url, string instructions) GetRuntimeDownloadInfo(string runtimeName, string version)
    {
        return runtimeName.ToLowerInvariant() switch
        {
            ".net framework" or "netframework" =>
                ("https://dotnet.microsoft.com/download/dotnet-framework",
                 $"从微软官网下载并安装 .NET Framework {version}。Windows 10/11 自带 4.8，缺失通常是更老的版本。"),

            "visual c++" or "vc++" or "msvc" or "visual c++ 2015-2022" =>
                ("https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist",
                 $"从微软官网下载并安装 Visual C++ Redistributable（x86 与 x64 都装一遍最稳妥），对应版本 {version}。"),

            "java" or "jre" or "jdk" or "java runtime" =>
                ("https://adoptium.net/",
                 "下载并安装 Java 运行时（Adoptium Temurin 的免费 LTS 版本即可）。"),

            _ =>
                ("", $"本机缺少 {runtimeName} {version}，请在搜索引擎中查找该运行库的官方安装包后安装。")
        };
    }

    /// <summary>
    /// Creates an antivirus blocking warning.
    /// </summary>
    public static GameErrorInfo CreateAntivirusBlockingWarning(int gameId, string executableName)
    {
        return new GameErrorInfo
        {
            GameId = gameId,
            Category = ErrorCategory.AntivirusBlocking,
            Severity = ErrorSeverity.Info,
            Title = "可能被安全软件拦截",
            Description = $"日文游戏常见的加壳/自解压打包方式容易被杀毒软件误报，{executableName} 有可能被隔离或删除。"
                        + "如果游戏启动后立刻闪退、或者文件莫名消失，先排查这一类问题。",
            SolutionType = SolutionType.ManualFix,
            SolutionInstructions = "把游戏目录加入杀毒软件的排除列表（Windows 安全中心：病毒和威胁防护 → 管理设置 → 排除项 → 添加文件夹）。"
                                + "Galbox 不会代你修改安全软件设置，那需要你自己判断风险。",
            AutoFixAvailable = false,
            ContextData = new Dictionary<string, object>
            {
                ["ExecutableName"] = executableName
            }
        };
    }

    /// <summary>
    /// Creates a permission issue error.
    /// </summary>
    public static GameErrorInfo CreatePermissionIssueError(int gameId, string path)
    {
        return new GameErrorInfo
        {
            GameId = gameId,
            Category = ErrorCategory.PermissionIssue,
            Severity = ErrorSeverity.Major,
            Title = "游戏目录权限不足",
            Description = $"游戏目录 {path} 可能没有写入权限，游戏可能无法保存设置、无法存档，或因读取配置失败而启动异常。",
            SolutionType = SolutionType.ManualFix,
            SolutionInstructions = "两种处理方式：① 右键游戏主程序 →「以管理员身份运行」；"
                                + "② 把游戏移到 Program Files 之外（例如 D:\\Games\\）后重新扫描。"
                                + "Galbox 不代你移动游戏目录、也不会改 ACL：那属于系统级改动，必须由你确认。",
            AutoFixAvailable = false,
            ContextData = new Dictionary<string, object>
            {
                ["Path"] = path
            }
        };
    }
}