using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Galbox.App.Models;
using Galbox.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Galbox.App.Services;

/// <summary>
/// Service for detecting common game errors and compatibility issues.
/// Implements comprehensive error detection for galgame scenarios.
/// </summary>
public class ErrorCheckingService : IErrorCheckingService
{
    private readonly ILogger<ErrorCheckingService> _logger;

    // Pre-compiled regex patterns for efficiency
    private static readonly Regex ChineseCharacterPattern = new(
        @"[\u4e00-\u9fff\u3400-\u4dbf]",
        RegexOptions.Compiled);

    private static readonly Regex JapaneseLocaleIndicatorsPattern = new(
        @"(\.jp|locale.*jp|japan|shift.?jis|cp932|ms932)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DirectXDllPattern = new(
        @"d3dx9_\d+\.dll",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex XpVistaIndicatorPattern = new(
        @"\b(?:windows\s+(?:xp|vista|98|95)|win\s?(?:xp|vista|98|95)|xp|vista)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex VideoFilePattern = new(
        @"\.(avi|mp4|wmv|mkv|mov|flv|mpg|mpeg)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Japanese character pattern (Hiragana, Katakana, and Kanji)
    private static readonly Regex JapaneseCharacterPattern = new(
        @"[\u3040-\u309f\u30a0-\u30ff\u4e00-\u9fff]",
        RegexOptions.Compiled);

    // Common DirectX DLLs
    private static readonly HashSet<string> KnownDirectXDlls = new()
    {
        "d3dx9_24.dll", "d3dx9_25.dll", "d3dx9_26.dll", "d3dx9_27.dll",
        "d3dx9_28.dll", "d3dx9_29.dll", "d3dx9_30.dll", "d3dx9_31.dll",
        "d3dx9_32.dll", "d3dx9_33.dll", "d3dx9_34.dll", "d3dx9_35.dll",
        "d3dx9_36.dll", "d3dx9_37.dll", "d3dx9_38.dll", "d3dx9_39.dll",
        "d3dx9_40.dll", "d3dx9_41.dll", "d3dx9_42.dll", "d3dx9_43.dll"
    };

    // Runtime dependencies patterns
    private static readonly Dictionary<string, string> RuntimeIndicators = new()
    {
        ["mscorlib.dll"] = ".NET Framework",
        ["System.dll"] = ".NET Framework",
        ["System.Core.dll"] = ".NET Framework",
        ["mscoree.dll"] = ".NET Framework",
        ["msvcp140.dll"] = "Visual C++ 2015-2022",
        ["vcruntime140.dll"] = "Visual C++ 2015-2022",
        ["vcruntime140_1.dll"] = "Visual C++ 2015-2022",
        ["msvcp120.dll"] = "Visual C++ 2013",
        ["msvcr120.dll"] = "Visual C++ 2013",
        ["msvcp110.dll"] = "Visual C++ 2012",
        ["msvcr110.dll"] = "Visual C++ 2012",
        ["msvcp100.dll"] = "Visual C++ 2010",
        ["msvcr100.dll"] = "Visual C++ 2010",
        ["msvcp90.dll"] = "Visual C++ 2008",
        ["msvcr90.dll"] = "Visual C++ 2008",
        ["java.dll"] = "Java Runtime",
        ["jvm.dll"] = "Java Runtime"
    };

    // Japanese locale detection patterns
    private static readonly HashSet<string> JapaneseLocaleFiles = new()
    {
        "locale.jp", "region.jp", "japanese.ini", "lang_jp.ini",
        "shiftjis.txt", "cp932.cfg"
    };

    /// <summary>
    /// Factory for the (Singleton) error-checking service.
    ///
    /// Previously this service was registered as a Singleton while consuming a Scoped
    /// <c>GalboxDbContext</c>: the container handed it the one root-resolved context, which then
    /// lived - and was used - for the whole process. Creating a short-lived context per operation
    /// is what removes the "A second operation was started on this context instance" failure.
    /// </summary>
    private readonly IDbContextFactory<GalboxDbContext> _dbContextFactory;

    /// <summary>
    /// The repair implementation behind the "一键修复" button.
    /// </summary>
    /// <remarks>
    /// Injected rather than implemented here so that detection stays a pure read-only operation and
    /// every repair lives in one auditable place that can report what it changed and how to undo it.
    /// </remarks>
    private readonly IGameHealthFixService _fixService;

    /// <summary>
    /// Creates an ErrorCheckingService with injected dependencies.
    /// </summary>
    public ErrorCheckingService(
        IDbContextFactory<GalboxDbContext> dbContextFactory,
        IGameHealthFixService fixService,
        ILogger<ErrorCheckingService> logger)
    {
        _dbContextFactory = dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
        _fixService = fixService ?? throw new ArgumentNullException(nameof(fixService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Run comprehensive error check on a game.
    /// </summary>
    public async Task<List<GameErrorInfo>> CheckGameAsync(GameInfo gameInfo, CancellationToken cancellationToken = default)
    {
        if (gameInfo == null)
        {
            throw new ArgumentNullException(nameof(gameInfo));
        }

        var errors = new List<GameErrorInfo>();

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Run all checks in parallel for efficiency
            var chineseDirCheck = CheckChineseDirectoryAsync(gameInfo, cancellationToken);
            var localeCheck = CheckLocaleRequirementAsync(gameInfo, cancellationToken);
            var directXCheck = CheckDirectXAsync(gameInfo, cancellationToken);
            var codecCheck = CheckKLiteCodecAsync(gameInfo, cancellationToken);
            var windowsCheck = CheckWindowsCompatibilityAsync(gameInfo, cancellationToken);
            var runtimeCheck = CheckRuntimeDependenciesAsync(gameInfo, cancellationToken);
            var permissionCheck = CheckPermissionsAsync(gameInfo, cancellationToken);
            var antivirusCheck = CheckAntivirusBlockingAsync(gameInfo, cancellationToken);

            await Task.WhenAll(
                chineseDirCheck, localeCheck, directXCheck, codecCheck,
                windowsCheck, runtimeCheck, permissionCheck, antivirusCheck
            ).ConfigureAwait(false);

            // Collect results
            var chineseDirError = await chineseDirCheck.ConfigureAwait(false);
            if (chineseDirError != null) errors.Add(chineseDirError);

            var localeError = await localeCheck.ConfigureAwait(false);
            if (localeError != null) errors.Add(localeError);

            var directXError = await directXCheck.ConfigureAwait(false);
            if (directXError != null) errors.Add(directXError);

            var codecError = await codecCheck.ConfigureAwait(false);
            if (codecError != null) errors.Add(codecError);

            var windowsError = await windowsCheck.ConfigureAwait(false);
            if (windowsError != null) errors.Add(windowsError);

            var runtimeError = await runtimeCheck.ConfigureAwait(false);
            if (runtimeError != null) errors.Add(runtimeError);

            var permissionError = await permissionCheck.ConfigureAwait(false);
            if (permissionError != null) errors.Add(permissionError);

            var antivirusError = await antivirusCheck.ConfigureAwait(false);
            if (antivirusError != null) errors.Add(antivirusError);

            // Save detected errors to database
            await SaveErrorsToDatabaseAsync(gameInfo.Id, errors, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Completed error check for game {GameId}: {ErrorCount} issues found",
                gameInfo.Id, errors.Count);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Error check cancelled for game {GameId}", gameInfo.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during error check for game {GameId}", gameInfo.Id);
        }

        return errors;
    }

    /// <summary>
    /// Check for specific error category on a game.
    /// </summary>
    public async Task<GameErrorInfo?> CheckCategoryAsync(GameInfo gameInfo, ErrorCategory category, CancellationToken cancellationToken = default)
    {
        if (gameInfo == null)
        {
            throw new ArgumentNullException(nameof(gameInfo));
        }

        cancellationToken.ThrowIfCancellationRequested();

        return category switch
        {
            ErrorCategory.ChineseDirectory => await CheckChineseDirectoryAsync(gameInfo, cancellationToken).ConfigureAwait(false),
            ErrorCategory.LocaleRequirement => await CheckLocaleRequirementAsync(gameInfo, cancellationToken).ConfigureAwait(false),
            ErrorCategory.DirectXMissing => await CheckDirectXAsync(gameInfo, cancellationToken).ConfigureAwait(false),
            ErrorCategory.KLiteCodecMissing => await CheckKLiteCodecAsync(gameInfo, cancellationToken).ConfigureAwait(false),
            ErrorCategory.WindowsCompatibility => await CheckWindowsCompatibilityAsync(gameInfo, cancellationToken).ConfigureAwait(false),
            ErrorCategory.RuntimeMissing => await CheckRuntimeDependenciesAsync(gameInfo, cancellationToken).ConfigureAwait(false),
            ErrorCategory.PermissionIssue => await CheckPermissionsAsync(gameInfo, cancellationToken).ConfigureAwait(false),
            ErrorCategory.AntivirusBlocking => await CheckAntivirusBlockingAsync(gameInfo, cancellationToken).ConfigureAwait(false),
            _ => null
        };
    }

    /// <summary>
    /// Get error history for a game.
    /// </summary>
    public async Task<List<GameErrorRecord>> GetErrorHistoryAsync(int gameId, CancellationToken cancellationToken = default)
    {
        using var db = _dbContextFactory.CreateDbContext();

        return await db.ErrorRecords
            .Where(e => e.GameInfoId == gameId)
            .AsNoTracking()
            .OrderByDescending(e => e.DetectedTime)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Mark an error as resolved.
    /// </summary>
    public async Task<bool> MarkErrorResolvedAsync(int errorRecordId, CancellationToken cancellationToken = default)
    {
        using var db = _dbContextFactory.CreateDbContext();

        var errorRecord = await db.ErrorRecords
            .FirstOrDefaultAsync(e => e.Id == errorRecordId, cancellationToken)
            .ConfigureAwait(false);

        if (errorRecord == null)
        {
            _logger.LogWarning("Error record {Id} not found", errorRecordId);
            return false;
        }

        errorRecord.IsResolved = true;
        errorRecord.ResolvedTime = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Marked error {Id} as resolved", errorRecordId);
        return true;
    }

    /// <summary>
    /// Attempt automatic fix for an error.
    /// </summary>
    /// <remarks>
    /// The dispatch is deliberately conservative: a category only reaches a repair when
    /// <see cref="IGameHealthFixService.CanAutoFix"/> says a repair exists AND the finding itself is
    /// flagged as auto-fixable. Anything else returns a failure with an explanation, because a
    /// success that did not happen is worse than an honest refusal - that is precisely the defect the
    /// product spec records ("8 个诊断项全部标记为不可自动修复").
    /// </remarks>
    public async Task<AutoFixResult> AttemptAutoFixAsync(GameErrorInfo error, CancellationToken cancellationToken = default)
    {
        if (error == null)
        {
            throw new ArgumentNullException(nameof(error));
        }

        var categoryLabel = DiagnosisText.CategoryLabel(error.Category);

        if (!_fixService.CanAutoFix(error.Category))
        {
            return new AutoFixResult
            {
                Success = false,
                Message = $"「{categoryLabel}」没有自动修复方案。请按“解决步骤”里的说明手动处理，"
                        + "或点“下载所需组件”打开官方下载页。"
            };
        }

        if (!error.AutoFixAvailable)
        {
            return new AutoFixResult
            {
                Success = false,
                Message = $"这一条「{categoryLabel}」当前不满足自动修复的条件（例如中文出现在上层目录）。"
                        + "请按“解决步骤”里的说明处理。"
            };
        }

        if (error.GameId <= 0)
        {
            return new AutoFixResult
            {
                Success = false,
                Message = "这条诊断记录没有关联到具体的游戏（GameId 为空），无法自动修复。请先重新检测该游戏。"
            };
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var result = error.Category switch
            {
                ErrorCategory.ChineseDirectory =>
                    await _fixService.FixChineseInstallPathAsync(error.GameId, cancellationToken).ConfigureAwait(false),

                ErrorCategory.WindowsCompatibility =>
                    await _fixService
                        .ApplyWindowsCompatibilityModeAsync(error.GameId, ResolveCompatibilityMode(error), cancellationToken)
                        .ConfigureAwait(false),

                _ => GameHealthFixResult.Failure($"「{categoryLabel}」还没有实现自动修复。")
            };

            if (result.Success)
            {
                _logger.LogInformation("Auto fix applied for game {GameId}, category {Category}", error.GameId, error.Category);
            }
            else
            {
                _logger.LogWarning("Auto fix refused for game {GameId}, category {Category}: {Message}",
                    error.GameId, error.Category, result.Message);
            }

            return new AutoFixResult
            {
                Success = result.Success,
                Message = result.Message,
                Details = result.Notes.Count == 0 ? null : string.Join(Environment.NewLine, result.Notes),
                Fix = result
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auto fix failed for game {GameId}, category {Category}", error.GameId, error.Category);
            return new AutoFixResult
            {
                Success = false,
                Message = $"自动修复出错：{ex.Message}",
                Details = ex.ToString()
            };
        }
    }

    /// <summary>
    /// Reads the compatibility mode the diagnosis recommended out of the finding's context data,
    /// falling back to the mode used for XP-era games.
    /// </summary>
    private static string ResolveCompatibilityMode(GameErrorInfo error)
    {
        if (error.ContextData.TryGetValue("CompatibilityMode", out var value) && value is string text && !string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        return "Windows 7";
    }

    /// <summary>
    /// Run batch error check on multiple games.
    /// </summary>
    public async Task<Dictionary<int, List<GameErrorInfo>>> BatchCheckGamesAsync(IEnumerable<GameInfo> games, CancellationToken cancellationToken = default)
    {
        var results = new ConcurrentDictionary<int, List<GameErrorInfo>>();
        var gameList = games.ToList();

        _logger.LogInformation("Starting batch error check for {Count} games", gameList.Count);

        // Process games with limited parallelism to avoid resource exhaustion
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = 4,
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(gameList, parallelOptions, async (game, ct) =>
        {
            var errors = await CheckGameAsync(game, ct).ConfigureAwait(false);
            results[game.Id] = errors;
        }).ConfigureAwait(false);

        return new Dictionary<int, List<GameErrorInfo>>(results);
    }

    /// <summary>
    /// Get all unresolved errors across all games.
    /// </summary>
    public async Task<List<GameErrorRecord>> GetAllUnresolvedErrorsAsync(CancellationToken cancellationToken = default)
    {
        using var db = _dbContextFactory.CreateDbContext();

        // Severity is stored as text, so ordering by it would sort alphabetically
        // (Minor > Major > Info > Critical) and put Critical last. Order by the parsed enum
        // severity instead, newest first inside the same severity.
        var records = await db.ErrorRecords
            .Where(e => !e.IsResolved)
            .Include(e => e.GameInfo)
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return records
            .OrderByDescending(e => ParseSeverity(e.Severity))
            .ThenByDescending(e => e.DetectedTime)
            .ToList();
    }

    /// <summary>
    /// Parses the stored severity text, treating an unrecognised value as the least severe.
    /// </summary>
    private static ErrorSeverity ParseSeverity(string severity)
    {
        return Enum.TryParse<ErrorSeverity>(severity, ignoreCase: true, out var parsed)
            ? parsed
            : ErrorSeverity.Info;
    }

    #region Private Detection Methods

    /// <summary>
    /// Check for Chinese characters in directory path.
    /// </summary>
    private async Task<GameErrorInfo?> CheckChineseDirectoryAsync(GameInfo gameInfo, CancellationToken cancellationToken)
    {
        await Task.Yield(); // Ensure async context
        cancellationToken.ThrowIfCancellationRequested();

        var path = gameInfo.InstallPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        // Find all Chinese character segments in path
        var matches = ChineseCharacterPattern.Matches(path);
        if (matches.Count > 0)
        {
            var chineseSegments = string.Join(", ", matches.Select(m => m.Value).Distinct());
            _logger.LogDebug("Found Chinese characters in path for game {GameId}: {Segments}",
                gameInfo.Id, chineseSegments);

            // The one-click repair renames the game folder itself. When the Chinese characters only
            // appear in an ancestor folder, that repair cannot help, and promising it anyway would put
            // a button on the screen that cannot do what it says.
            var (leafWithChinese, ancestorsWithChinese) = GamePathNaming.AnalysePath(path);
            var canAutoFix = leafWithChinese is not null;

            if (!canAutoFix && ancestorsWithChinese.Count > 0)
            {
                _logger.LogDebug(
                    "Chinese characters are only in ancestor folders for game {GameId}: {Ancestors}",
                    gameInfo.Id, string.Join(" / ", ancestorsWithChinese));
            }

            return GameErrorInfo.CreateChineseDirectoryError(gameInfo.Id, path, chineseSegments, canAutoFix);
        }

        return null;
    }

    /// <summary>
    /// Check for Japanese locale requirement indicators.
    /// </summary>
    private async Task<GameErrorInfo?> CheckLocaleRequirementAsync(GameInfo gameInfo, CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();

        var gamePath = gameInfo.InstallPath;
        if (string.IsNullOrWhiteSpace(gamePath) || !Directory.Exists(gamePath))
        {
            return null;
        }

        // Check for locale indicator files
        try
        {
            var files = Directory.GetFiles(gamePath, "*", SearchOption.TopDirectoryOnly);
            foreach (var file in files)
            {
                var fileName = Path.GetFileName(file).ToLowerInvariant();
                if (JapaneseLocaleFiles.Contains(fileName))
                {
                    _logger.LogDebug("Found locale indicator file for game {GameId}: {File}",
                        gameInfo.Id, fileName);
                    return GameErrorInfo.CreateLocaleRequirementError(gameInfo.Id, "Japanese (Shift-JIS)");
                }
            }

            // Check file names and content patterns
            var iniFiles = files.Where(f => f.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) ||
                                            f.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase) ||
                                            f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                                 .Take(10); // Limit to prevent excessive IO

            foreach (var iniFile in iniFiles)
            {
                try
                {
                    var content = await File.ReadAllTextAsync(iniFile, cancellationToken).ConfigureAwait(false);
                    if (JapaneseLocaleIndicatorsPattern.IsMatch(content))
                    {
                        _logger.LogDebug("Found locale indicator in file content for game {GameId}",
                            gameInfo.Id);
                        return GameErrorInfo.CreateLocaleRequirementError(gameInfo.Id, "Japanese (Shift-JIS)");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not read file {File}", iniFile);
                }
            }

            // Check game name for Japanese locale indicators
            var gameName = gameInfo.NameOriginal;
            if (!string.IsNullOrWhiteSpace(gameName) && JapaneseLocaleIndicatorsPattern.IsMatch(gameName))
            {
                return GameErrorInfo.CreateLocaleRequirementError(gameInfo.Id, "Japanese (Shift-JIS)");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not check locale requirement for game {GameId}", gameInfo.Id);
        }

        return null;
    }

    /// <summary>
    /// Check for missing DirectX dependencies.
    /// </summary>
    private async Task<GameErrorInfo?> CheckDirectXAsync(GameInfo gameInfo, CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();

        var gamePath = gameInfo.InstallPath;
        if (string.IsNullOrWhiteSpace(gamePath) || !Directory.Exists(gamePath))
        {
            return null;
        }

        try
        {
            // Check for DirectX DLLs in game folder (indicating requirement)
            var dllFiles = Directory.GetFiles(gamePath, "*.dll", SearchOption.TopDirectoryOnly);
            var requiredDxDlls = new List<string>();

            foreach (var dll in dllFiles)
            {
                var dllName = Path.GetFileName(dll).ToLowerInvariant();
                if (DirectXDllPattern.IsMatch(dllName))
                {
                    requiredDxDlls.Add(dllName);
                }
            }

            if (requiredDxDlls.Count > 0)
            {
                // Check if the DLLs exist in system folders
                foreach (var requiredDll in requiredDxDlls)
                {
                    if (!IsDirectXDllInstalled(requiredDll))
                    {
                        _logger.LogDebug("Missing DirectX DLL for game {GameId}: {Dll}",
                            gameInfo.Id, requiredDll);
                        return GameErrorInfo.CreateDirectXMissingError(gameInfo.Id, requiredDll, "9.0c");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not check DirectX for game {GameId}", gameInfo.Id);
        }

        return null;
    }

    /// <summary>
    /// Check if a DirectX DLL is installed in system folders.
    /// </summary>
    private static bool IsDirectXDllInstalled(string dllName)
    {
        var systemPaths = new[]
        {
            Environment.SystemDirectory,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64")
        };

        foreach (var systemPath in systemPaths)
        {
            var fullPath = Path.Combine(systemPath, dllName);
            if (File.Exists(fullPath))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Check for video files that may require codec pack.
    /// </summary>
    private async Task<GameErrorInfo?> CheckKLiteCodecAsync(GameInfo gameInfo, CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();

        var gamePath = gameInfo.InstallPath;
        if (string.IsNullOrWhiteSpace(gamePath) || !Directory.Exists(gamePath))
        {
            return null;
        }

        try
        {
            // Search for video files in game folder
            var hasVideoFiles = false;
            var searchDirectories = new[] { gamePath };

            // Check common video directories within game folder
            var possibleVideoDirs = new[] { "video", "movies", "movie", "media", "bgm", "assets" };
            foreach (var dirName in possibleVideoDirs)
            {
                var fullPath = Path.Combine(gamePath, dirName);
                if (Directory.Exists(fullPath))
                {
                    searchDirectories = searchDirectories.Append(fullPath).ToArray();
                }
            }

            foreach (var dir in searchDirectories.Take(5)) // Limit directories to check
            {
                try
                {
                    var files = Directory.GetFiles(dir, "*.*", SearchOption.TopDirectoryOnly)
                                         .Take(50); // Limit files per directory

                    foreach (var file in files)
                    {
                        var extension = Path.GetExtension(file);
                        if (VideoFilePattern.IsMatch(extension))
                        {
                            hasVideoFiles = true;
                            break;
                        }
                    }

                    if (hasVideoFiles) break;
                }
                catch (Exception)
                {
                    // Skip inaccessible directories
                }
            }

            if (hasVideoFiles)
            {
                // Check if K-Lite codec is likely installed by checking for common codec DLLs
                if (!IsCodecPackLikelyInstalled())
                {
                    _logger.LogDebug("Found video files without codec pack for game {GameId}", gameInfo.Id);
                    return GameErrorInfo.CreateKLiteCodecMissingError(gameInfo.Id);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not check codec requirement for game {GameId}", gameInfo.Id);
        }

        return null;
    }

    /// <summary>
    /// Check if a codec pack is likely installed.
    /// </summary>
    private static bool IsCodecPackLikelyInstalled()
    {
        // Check for common codec-related DLLs or applications
        var codecIndicators = new[]
        {
            "LAVFilters.dll", "ffdshow.ax", "HaaliMediaSplitter.ax"
        };

        var systemPath = Environment.SystemDirectory;
        foreach (var indicator in codecIndicators)
        {
            if (File.Exists(Path.Combine(systemPath, indicator)))
            {
                return true;
            }
        }

        // Check for K-Lite installation directory
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var klitePath = Path.Combine(programFiles, "K-Lite Codec Pack");
        if (Directory.Exists(klitePath))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Check for Windows compatibility issues.
    /// </summary>
    private async Task<GameErrorInfo?> CheckWindowsCompatibilityAsync(GameInfo gameInfo, CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();

        var gamePath = gameInfo.InstallPath;
        var gameName = gameInfo.NameOriginal;

        // Never report a compatibility problem for a game that already carries a compatibility layer:
        // the diagnosis describes something the user still has to do, and after a successful repair
        // there is nothing left to do. Reporting it again would leave a Critical item in the list
        // forever, with a repair button that repairs nothing.
        GameErrorInfo? ReportIfNotAlreadyHandled()
        {
            if (_fixService.IsCompatibilityModeApplied(gameInfo.MainExecutable))
            {
                _logger.LogDebug(
                    "Windows compatibility issue for game {GameId} is already handled by a compatibility layer",
                    gameInfo.Id);
                return null;
            }

            return GameErrorInfo.CreateWindowsCompatibilityError(gameInfo.Id, "Windows XP/Vista", "Windows 7");
        }

        // Check game name for XP/Vista indicators
        if (!string.IsNullOrWhiteSpace(gameName) && XpVistaIndicatorPattern.IsMatch(gameName))
        {
            _logger.LogDebug("Found XP/Vista indicator in game name for game {GameId}", gameInfo.Id);
            return ReportIfNotAlreadyHandled();
        }

        if (string.IsNullOrWhiteSpace(gamePath) || !Directory.Exists(gamePath))
        {
            return null;
        }

        try
        {
            // Check readme and documentation files for compatibility info
            var docFiles = Directory.GetFiles(gamePath, "*.*", SearchOption.TopDirectoryOnly)
                .Where(f => f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".readme", StringComparison.OrdinalIgnoreCase) ||
                            f.Contains("readme", StringComparison.OrdinalIgnoreCase) ||
                            f.Contains("manual", StringComparison.OrdinalIgnoreCase))
                .Take(5);

            foreach (var docFile in docFiles)
            {
                try
                {
                    var content = await File.ReadAllTextAsync(docFile, cancellationToken).ConfigureAwait(false);
                    if (XpVistaIndicatorPattern.IsMatch(content))
                    {
                        _logger.LogDebug("Found XP/Vista indicator in documentation for game {GameId}",
                            gameInfo.Id);
                        return ReportIfNotAlreadyHandled();
                    }
                }
                catch (Exception)
                {
                    // Skip files that can't be read
                }
            }

            // Check executable file version for old Windows compatibility
            var exePath = gameInfo.MainExecutable;
            if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
            {
                try
                {
                    var versionInfo = FileVersionInfo.GetVersionInfo(exePath);
                    // Very old file version may indicate XP/Vista era game
                    if ((versionInfo.FileVersion != null &&
                         versionInfo.FileVersion.StartsWith("1.0")) ||
                        (versionInfo.ProductVersion != null &&
                         XpVistaIndicatorPattern.IsMatch(versionInfo.ProductVersion)))
                    {
                        return ReportIfNotAlreadyHandled();
                    }
                }
                catch (Exception)
                {
                    // Skip if version info cannot be retrieved
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not check Windows compatibility for game {GameId}", gameInfo.Id);
        }

        return null;
    }

    /// <summary>
    /// Check for missing runtime dependencies.
    /// </summary>
    private async Task<GameErrorInfo?> CheckRuntimeDependenciesAsync(GameInfo gameInfo, CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();

        var exePath = gameInfo.MainExecutable;
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            return null;
        }

        try
        {
            // Check game folder for runtime DLL requirements
            var gamePath = gameInfo.InstallPath;
            if (!string.IsNullOrWhiteSpace(gamePath) && Directory.Exists(gamePath))
            {
                var dllFiles = Directory.GetFiles(gamePath, "*.dll", SearchOption.TopDirectoryOnly);

                foreach (var dll in dllFiles)
                {
                    var dllName = Path.GetFileName(dll).ToLowerInvariant();

                    if (RuntimeIndicators.TryGetValue(dllName, out var runtimeName))
                    {
                        // Check if the runtime is installed
                        if (!IsRuntimeInstalled(dllName))
                        {
                            _logger.LogDebug("Missing runtime DLL for game {GameId}: {Dll}",
                                gameInfo.Id, dllName);
                            return GameErrorInfo.CreateRuntimeMissingError(gameInfo.Id, runtimeName, GetRuntimeVersion(dllName));
                        }
                    }
                }
            }

            // Check for Unity games that might need .NET/Mono
            if (IsUnityGame(gamePath))
            {
                // Unity games generally work, but may need VC++ redistributables
                var unityPlayerDll = Path.Combine(gamePath, "UnityPlayer.dll");
                if (File.Exists(unityPlayerDll))
                {
                    if (!IsRuntimeInstalled("msvcp140.dll"))
                    {
                        return GameErrorInfo.CreateRuntimeMissingError(gameInfo.Id, "Visual C++", "2015-2022");
                    }
                }
            }

            // Check for Ren'Py games that might need Python (usually self-contained)
            // Check for RPG Maker games
            if (IsRpgMakerGame(gamePath))
            {
                // RPG Maker MV/VX Ace games have their own runtimes usually
                // But older versions may need specific fonts or DLLs
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not check runtime dependencies for game {GameId}", gameInfo.Id);
        }

        return null;
    }

    /// <summary>
    /// Check if a runtime DLL is installed in system.
    /// </summary>
    private static bool IsRuntimeInstalled(string dllName)
    {
        var systemPaths = new[]
        {
            Environment.SystemDirectory,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64")
        };

        foreach (var systemPath in systemPaths)
        {
            var fullPath = Path.Combine(systemPath, dllName);
            if (File.Exists(fullPath))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Get runtime version from DLL name.
    /// </summary>
    private static string GetRuntimeVersion(string dllName)
    {
        return dllName switch
        {
            "msvcp140.dll" or "vcruntime140.dll" or "vcruntime140_1.dll" => "2015-2022",
            "msvcp120.dll" or "msvcr120.dll" => "2013 (12.0)",
            "msvcp110.dll" or "msvcr110.dll" => "2012 (11.0)",
            "msvcp100.dll" or "msvcr100.dll" => "2010 (10.0)",
            "msvcp90.dll" or "msvcr90.dll" => "2008 (9.0)",
            _ => "Unknown"
        };
    }

    /// <summary>
    /// Check if game is a Unity game.
    /// </summary>
    private static bool IsUnityGame(string? gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath) || !Directory.Exists(gamePath))
        {
            return false;
        }

        var unityIndicatorFiles = new[]
        {
            "UnityPlayer.dll", "GameData.dll", "sharedassets0.assets"
        };

        foreach (var indicator in unityIndicatorFiles)
        {
            if (File.Exists(Path.Combine(gamePath, indicator)))
            {
                return true;
            }
        }

        // Check for Unity data folder - should be {exeName}_Data
        // Find executable files in the directory and check for corresponding _Data folder
        try
        {
            var exeFiles = Directory.GetFiles(gamePath, "*.exe", SearchOption.TopDirectoryOnly);
            foreach (var exeFile in exeFiles)
            {
                var exeName = Path.GetFileNameWithoutExtension(exeFile);
                var unityDataPath = Path.Combine(gamePath, exeName + "_Data");
                if (Directory.Exists(unityDataPath))
                {
                    return true;
                }
            }
        }
        catch (Exception)
        {
            // Ignore directory access errors
        }

        return false;
    }

    /// <summary>
    /// Check if game is an RPG Maker game.
    /// </summary>
    private static bool IsRpgMakerGame(string? gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath) || !Directory.Exists(gamePath))
        {
            return false;
        }

        var rpgMakerIndicatorFiles = new[]
        {
            "RPG_RT.exe", "Game.exe", "nw.exe" // RPG Maker 2000/2003, MV
        };

        foreach (var indicator in rpgMakerIndicatorFiles)
        {
            if (File.Exists(Path.Combine(gamePath, indicator)))
            {
                return true;
            }
        }

        // Check for RPG Maker data files
        var rpgMakerDataFiles = new[] { "RPG_RT.ldb", "RPG_RT.lmt", "Data" };
        foreach (var indicator in rpgMakerDataFiles)
        {
            if (File.Exists(Path.Combine(gamePath, indicator)) ||
                Directory.Exists(Path.Combine(gamePath, indicator)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Check for file permission issues.
    /// </summary>
    private async Task<GameErrorInfo?> CheckPermissionsAsync(GameInfo gameInfo, CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();

        var gamePath = gameInfo.InstallPath;
        if (string.IsNullOrWhiteSpace(gamePath) || !Directory.Exists(gamePath))
        {
            return null;
        }

        try
        {
            // Check if game is in Program Files (potential permission issues)
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

            if (gamePath.StartsWith(programFiles, StringComparison.OrdinalIgnoreCase) ||
                gamePath.StartsWith(programFilesX86, StringComparison.OrdinalIgnoreCase))
            {
                // Try to write a test file to check permissions
                var testFile = Path.Combine(gamePath, ".permission_test");
                try
                {
                    await File.WriteAllTextAsync(testFile, "test", cancellationToken).ConfigureAwait(false);
                    File.Delete(testFile);
                }
                catch (UnauthorizedAccessException)
                {
                    _logger.LogDebug("Permission issue detected for game {GameId} in Program Files",
                        gameInfo.Id);
                    return GameErrorInfo.CreatePermissionIssueError(gameInfo.Id, gamePath);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not check permissions for game {GameId}", gameInfo.Id);
        }

        return null;
    }

    /// <summary>
    /// Check for potential antivirus blocking.
    /// </summary>
    private async Task<GameErrorInfo?> CheckAntivirusBlockingAsync(GameInfo gameInfo, CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();

        // This is informational only - we cannot actually detect antivirus blocking
        // We can provide guidance for common cases

        var exePath = gameInfo.MainExecutable;
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            return null;
        }

        var exeName = Path.GetFileName(exePath);

        // Check for patterns that often trigger antivirus
        var suspiciousPatterns = new[]
        {
            "crack", "patch", "loader", "keygen", "activator"
        };

        var exeNameLower = exeName.ToLowerInvariant();
        foreach (var pattern in suspiciousPatterns)
        {
            if (exeNameLower.Contains(pattern))
            {
                return GameErrorInfo.CreateAntivirusBlockingWarning(gameInfo.Id, exeName);
            }
        }

        // For Japanese games, provide general warning
        if (!string.IsNullOrWhiteSpace(gameInfo.NameOriginal) &&
            ContainsJapaneseCharacters(gameInfo.NameOriginal))
        {
            // Japanese games often use packing methods that antivirus dislikes
            return GameErrorInfo.CreateAntivirusBlockingWarning(gameInfo.Id, exeName);
        }

        return null;
    }

    /// <summary>
    /// Check if text contains Japanese characters.
    /// </summary>
    private static bool ContainsJapaneseCharacters(string text)
    {
        return JapaneseCharacterPattern.IsMatch(text);
    }

    /// <summary>
    /// Save detected errors to database.
    /// </summary>
    private async Task SaveErrorsToDatabaseAsync(int gameId, List<GameErrorInfo> errors, CancellationToken cancellationToken)
    {
        if (errors.Count == 0)
        {
            return;
        }

        using var db = _dbContextFactory.CreateDbContext();

        foreach (var error in errors)
        {
            var record = new GameErrorRecord
            {
                GameInfoId = gameId,
                Category = error.Category.ToString(),
                Severity = error.Severity.ToString(),
                Title = error.Title,
                Description = error.Description,
                SolutionType = error.SolutionType.ToString(),
                SolutionInstructions = error.SolutionInstructions,
                DownloadUrl = error.DownloadUrl,
                ToolName = error.ToolName,
                DetectedTime = DateTime.UtcNow,
                IsResolved = false
            };

            db.ErrorRecords.Add(record);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    #endregion
}