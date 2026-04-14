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
    public string SeverityDisplay => Severity switch
    {
        ErrorSeverity.Critical => "Critical",
        ErrorSeverity.Major => "Major",
        ErrorSeverity.Minor => "Minor",
        ErrorSeverity.Info => "Info",
        _ => "Unknown"
    };

    /// <summary>
    /// Gets the category display text.
    /// </summary>
    public string CategoryDisplay => Category switch
    {
        ErrorCategory.ChineseDirectory => "Chinese Directory",
        ErrorCategory.LocaleRequirement => "Locale Requirement",
        ErrorCategory.DirectXMissing => "DirectX Missing",
        ErrorCategory.KLiteCodecMissing => "Codec Missing",
        ErrorCategory.WindowsCompatibility => "Windows Compatibility",
        ErrorCategory.RuntimeMissing => "Runtime Missing",
        ErrorCategory.GameDependency => "Game Dependency",
        ErrorCategory.PermissionIssue => "Permission Issue",
        ErrorCategory.AntivirusBlocking => "Antivirus Blocking",
        _ => "Unknown"
    };

    /// <summary>
    /// Gets the solution type display text.
    /// </summary>
    public string SolutionTypeDisplay => SolutionType switch
    {
        SolutionType.AutoFix => "Auto Fix",
        SolutionType.ManualFix => "Manual Fix",
        SolutionType.ExternalTool => "External Tool",
        SolutionType.None => "No Fix Available",
        _ => "Unknown"
    };

    /// <summary>
    /// Creates a Chinese directory error.
    /// </summary>
    public static GameErrorInfo CreateChineseDirectoryError(int gameId, string path, string chineseSegments)
    {
        return new GameErrorInfo
        {
            GameId = gameId,
            Category = ErrorCategory.ChineseDirectory,
            Severity = ErrorSeverity.Major,
            Title = "Chinese Characters in Path",
            Description = $"The game path contains Chinese characters: {chineseSegments}. Some Japanese games may fail to run with Chinese paths.",
            SolutionType = SolutionType.ManualFix,
            SolutionInstructions = "Rename the folder to use English characters or Pinyin. Example: '游戏' -> 'Game' or 'youxi'",
            AutoFixAvailable = false,
            ContextData = new Dictionary<string, object>
            {
                ["Path"] = path,
                ["ChineseSegments"] = chineseSegments
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
            Title = "Locale/Region Requirement",
            Description = $"This game requires {requiredLocale} locale to run properly. Running with wrong locale may cause text display issues or crashes.",
            SolutionType = SolutionType.ExternalTool,
            SolutionInstructions = "Use Locale Emulator or NTLEA to run the game with the required locale. These tools can temporarily change the system locale for the game process.",
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
    public static GameErrorInfo CreateDirectXMissingError(int gameId, string missingDll, string DirectXVersion)
    {
        return new GameErrorInfo
        {
            GameId = gameId,
            Category = ErrorCategory.DirectXMissing,
            Severity = ErrorSeverity.Major,
            Title = "DirectX Dependency Missing",
            Description = $"Missing DirectX file: {missingDll}. This game requires DirectX {DirectXVersion} components.",
            SolutionType = SolutionType.ExternalTool,
            SolutionInstructions = "Download and install DirectX End-User Runtime from Microsoft. This will install all required DirectX components.",
            AutoFixAvailable = false,
            DownloadUrl = "https://www.microsoft.com/en-us/download/details.aspx?id=35",
            ToolName = "DirectX End-User Runtime",
            ContextData = new Dictionary<string, object>
            {
                ["MissingDll"] = missingDll,
                ["DirectXVersion"] = DirectXVersion
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
            Title = "Video Codec Missing",
            Description = "This game contains video files that may require additional codecs for playback. Without proper codecs, in-game videos may not play.",
            SolutionType = SolutionType.ExternalTool,
            SolutionInstructions = "Download and install K-Lite Codec Pack (Basic or Standard version recommended). This provides codecs for various video formats.",
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
            Title = "Windows Compatibility Issue",
            Description = $"This game was designed for {originalOs} and may not run correctly on current Windows version.",
            SolutionType = SolutionType.ManualFix,
            SolutionInstructions = $"Try running in compatibility mode: Right-click executable > Properties > Compatibility > Run this program in compatibility mode for {compatibilityMode}. For games requiring XP/Vista, consider using a virtual machine.",
            AutoFixAvailable = false,
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
            Title = "Runtime Dependency Missing",
            Description = $"Missing {runtimeName} {version}. This game requires this runtime to execute.",
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
                ("https://dotnet.microsoft.com/en-us/download/dotnet-framework",
                 "Download and install the required .NET Framework version from Microsoft."),
            "visual c++" or "vc++" or "msvc" =>
                ("https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist",
                 "Download and install the Visual C++ Redistributable from Microsoft."),
            "java" or "jre" or "jdk" =>
                ("https://adoptium.net/",
                 "Download and install Java Runtime Environment. Adoptium provides free LTS versions."),
            _ =>
                ("", "Search online for the required runtime and install it.")
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
            Title = "Antivirus May Block Game",
            Description = $"Some antivirus software may incorrectly flag game executables as threats. This is common with Japanese games due to unusual packing methods.",
            SolutionType = SolutionType.ManualFix,
            SolutionInstructions = "Add the game folder to your antivirus exclusion list. Right-click antivirus icon > Settings > Exclusions > Add folder.",
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
            Title = "File Permission Issue",
            Description = $"The game may have permission issues accessing files at: {path}. This can prevent the game from saving or reading configuration.",
            SolutionType = SolutionType.ManualFix,
            SolutionInstructions = "Run the game as administrator, or move the game to a folder with proper permissions (e.g., outside Program Files).",
            AutoFixAvailable = false,
            ContextData = new Dictionary<string, object>
            {
                ["Path"] = path
            }
        };
    }
}