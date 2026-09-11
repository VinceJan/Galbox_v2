using System.Text.RegularExpressions;
using Galbox.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Galbox.Acceptance.Checks;

/// <summary>
/// Shared fixtures, probes and oracles for the game-health-diagnosis checks (A70-A74).
/// </summary>
/// <remarks>
/// Everything here works on folders and database rows the acceptance run creates itself, under
/// <c>%LocalAppData%\Galbox\acceptance\work</c>. No helper touches the user's library, the user's
/// real database, or the user's game folders. The single exception is the AppCompat layer test,
/// which writes a per-executable value into HKCU for an executable this harness created, and
/// removes it again.
/// </remarks>
internal static class HealthCheckSupport
{
    /// <summary>Interface name of the fix service, resolved by reflection so the harness still compiles before it exists.</summary>
    internal const string FixServiceTypeName = "Galbox.App.Services.IGameHealthFixService";

    /// <summary>HKCU key Windows uses for per-application compatibility layers.</summary>
    internal const string AppCompatLayersKey =
        @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";

    /// <summary>CJK ideographs, i.e. what "路径含中文" is about.</summary>
    private static readonly Regex ChinesePattern =
        new(@"[\u4e00-\u9fff\u3400-\u4dbf]", RegexOptions.Compiled);

    /// <summary>Any CJK codepoint (kana + ideographs) - used to prove a string was translated to Chinese.</summary>
    private static readonly Regex CjkPattern =
        new(@"[\u3040-\u30ff\u4e00-\u9fff\u3400-\u4dbf]", RegexOptions.Compiled);

    /// <summary>True when the text contains at least one Chinese ideograph.</summary>
    internal static bool HasChinese(string? text) => !string.IsNullOrEmpty(text) && ChinesePattern.IsMatch(text);

    /// <summary>True when the text contains at least one CJK character (Chinese or Japanese).</summary>
    internal static bool HasCjk(string? text) => !string.IsNullOrEmpty(text) && CjkPattern.IsMatch(text);

    /// <summary>True when every character is in the 7-bit ASCII range.</summary>
    internal static bool IsPureAscii(string? text) =>
        text is not null && text.All(character => character < 128);

    /// <summary>
    /// English fragments of the pre-localisation texts. None of them may survive anywhere in a
    /// user-visible diagnosis string, because the defect being fixed is precisely
    /// "错误标题与方案文案还是英文，未本地化" (product spec, historical-defect table).
    /// </summary>
    internal static readonly string[] RetiredEnglishPhrases =
    {
        "Chinese Characters in Path",
        "The game path contains",
        "Rename the folder to use English",
        "Locale/Region Requirement",
        "This game requires",
        "Use Locale Emulator or NTLEA",
        "These tools can temporarily change",
        "DirectX Dependency Missing",
        "Missing DirectX file",
        "Download and install DirectX End-User Runtime from Microsoft",
        "Video Codec Missing",
        "This game contains video files",
        "Download and install K-Lite Codec Pack (Basic or Standard version recommended)",
        "Windows Compatibility Issue",
        "This game was designed for",
        "Try running in compatibility mode: Right-click",
        "For games requiring XP/Vista, consider using a virtual machine",
        "Runtime Dependency Missing",
        "This game requires this runtime to execute",
        "Search online for the required runtime and install it",
        "Antivirus May Block Game",
        "Some antivirus software may incorrectly flag",
        "Add the game folder to your antivirus exclusion list",
        "File Permission Issue",
        "The game may have permission issues accessing files at",
        "Run the game as administrator, or move the game to a folder",
        "Permission issues require manual intervention",
        "Move the game folder outside of Program Files",
        "Auto fix is not available for this error type",
        "Auto fix implementation not yet available for this error type",
        "No game selected",
        "No error selected",
        "Auto fix not available for this error. Follow manual instructions.",
        "Attempting automatic fix...",
        "Fix applied successfully!",
        "Error marked as resolved",
        "Could not mark error as resolved",
        "Failed to load errors",
        "Loading errors...",
        "Starting batch error check"
    };

    /// <summary>
    /// Asserts that a user-visible diagnosis string is Chinese and carries no untranslated English
    /// fragment. Returns null when the string passes, or the failure reason.
    /// </summary>
    internal static string? CheckLocalized(string field, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return $"{field} 为空";
        }

        if (!HasCjk(value))
        {
            return $"{field} 不含任何中文字符：\"{value}\"";
        }

        var hit = RetiredEnglishPhrases.FirstOrDefault(
            phrase => value.Contains(phrase, StringComparison.OrdinalIgnoreCase));

        return hit is null ? null : $"{field} 仍含未本地化的英文片段 \"{hit}\"：\"{value}\"";
    }

    /// <summary>Resolves the (brand-new) fix service, or null when the revision does not have it yet.</summary>
    internal static object? ResolveFixService(AcceptanceContext context) =>
        ReflectionBridge.ResolveService(context.Services, FixServiceTypeName);

    /// <summary>Resolves the fix service and reports it as a detail line.</summary>
    internal static object? ResolveFixService(AcceptanceContext context, List<string> details)
    {
        var service = ResolveFixService(context);
        details.Add($"IGameHealthFixService : {(service is null ? "NOT REGISTERED" : service.GetType().FullName)}");
        return service;
    }

    /// <summary>
    /// Constructs the real report-page ViewModel from the container.
    /// </summary>
    /// <remarks>
    /// Driving the ViewModel - not just the service - is what proves the button on the screen is wired
    /// to a repair that exists. It is resolved by reflection because the ViewModel is a UI type, and
    /// the harness has to keep compiling against revisions in which it does not take a fix service yet.
    /// </remarks>
    internal static object? TryCreateReportViewModel(AcceptanceContext context, List<string> details)
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

    /// <summary>Reads one compatibility-layer value straight from HKCU.</summary>
    internal static string? ReadCompatibilityLayer(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(AppCompatLayersKey);
        return key?.GetValue(executablePath) as string;
    }

    /// <summary>Inserts a game row and returns its database id.</summary>
    internal static async Task<int> InsertGameAsync(
        AcceptanceContext context,
        string name,
        string installPath,
        string mainExecutable,
        string? alternativeExecutables,
        CancellationToken cancellationToken)
    {
        await using var scope = context.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GalboxDbContext>();

        var game = new GameInfo
        {
            NameOriginal = name,
            InstallPath = installPath,
            MainExecutable = mainExecutable,
            AlternativeExecutables = alternativeExecutables,
            AddedTime = DateTime.UtcNow,
            UpdatedTime = DateTime.UtcNow
        };

        db.Games.Add(game);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return game.Id;
    }

    /// <summary>Reads the three path fields of a game row straight from the database.</summary>
    internal static async Task<GamePaths> ReadGamePathsAsync(
        AcceptanceContext context,
        int gameId,
        CancellationToken cancellationToken)
    {
        await using var scope = context.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GalboxDbContext>();

        var game = await db.Games
            .AsNoTracking()
            .FirstOrDefaultAsync(g => g.Id == gameId, cancellationToken)
            .ConfigureAwait(false);

        return game is null
            ? new GamePaths("(row deleted)", "(row deleted)", null)
            : new GamePaths(game.InstallPath, game.MainExecutable, game.AlternativeExecutables);
    }

    /// <summary>Deletes game rows created by a check.</summary>
    internal static async Task DeleteGamesAsync(
        AcceptanceContext context,
        CancellationToken cancellationToken,
        params int[] gameIds)
    {
        try
        {
            await using var scope = context.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<GalboxDbContext>();
            await db.Games
                .Where(g => gameIds.Contains(g.Id))
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // Cleanup is best-effort; a leftover fixture row cannot affect the shipping app.
        }
    }

    /// <summary>The game path fields the rename fix has to keep in sync.</summary>
    internal sealed record GamePaths(string InstallPath, string MainExecutable, string? AlternativeExecutables);

    // -----------------------------------------------------------------------------------------
    // System probes. These are *measured*, printed, and used to derive the expected verdict, so a
    // machine that already has DirectX 9 / the VC++ runtime / a codec pack does not produce a
    // false FAIL. Every probe mirrors the condition the service itself tests.
    // -----------------------------------------------------------------------------------------

    /// <summary>True when a K-Lite-style codec pack is detectable, i.e. the KLite finding must be absent.</summary>
    internal static bool CodecPackInstalled()
    {
        var system32 = Environment.SystemDirectory;
        var indicators = new[] { "LAVFilters.dll", "ffdshow.ax", "HaaliMediaSplitter.ax" };
        if (indicators.Any(name => File.Exists(Path.Combine(system32, name))))
        {
            return true;
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return Directory.Exists(Path.Combine(programFiles, "K-Lite Codec Pack"));
    }

    /// <summary>True when the named DirectX/VC++ DLL exists in a system directory.</summary>
    internal static bool SystemDllInstalled(string dllName)
    {
        var system32 = Environment.SystemDirectory;
        var sysWow64 = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "SysWOW64");

        return File.Exists(Path.Combine(system32, dllName)) || File.Exists(Path.Combine(sysWow64, dllName));
    }
}
