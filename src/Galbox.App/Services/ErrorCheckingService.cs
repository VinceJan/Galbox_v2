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
    private readonly GalboxDbContext _dbContext;
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
        @"(xp|vista|windows\s+(xp|vista)|win\s+(xp|vista))",
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
    /// Creates an ErrorCheckingService with injected dependencies.
    /// </summary>
    public ErrorCheckingService(
        GalboxDbContext dbContext,
        ILogger<ErrorCheckingService> logger)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
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
        return await _dbContext.ErrorRecords
            .Where(e => e.GameInfoId == gameId)
            .OrderByDescending(e => e.DetectedTime)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Mark an error as resolved.
    /// </summary>
    public async Task<bool> MarkErrorResolvedAsync(int errorRecordId, CancellationToken cancellationToken = default)
    {
        var errorRecord = await _dbContext.ErrorRecords
            .FirstOrDefaultAsync(e => e.Id == errorRecordId, cancellationToken)
            .ConfigureAwait(false);

        if (errorRecord == null)
        {
            _logger.LogWarning("Error record {Id} not found", errorRecordId);
            return false;
        }

        errorRecord.IsResolved = true;
        errorRecord.ResolvedTime = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Marked error {Id} as resolved", errorRecordId);
        return true;
    }

    /// <summary>
    /// Attempt automatic fix for an error.
    /// </summary>
    public async Task<AutoFixResult> AttemptAutoFixAsync(GameErrorInfo error, CancellationToken cancellationToken = default)
    {
        if (!error.AutoFixAvailable)
        {
            return new AutoFixResult
            {
                Success = false,
                Message = "Auto fix is not available for this error type."
            };
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            return error.Category switch
            {
                ErrorCategory.PermissionIssue => await FixPermissionIssueAsync(error, cancellationToken).ConfigureAwait(false),
                _ => new AutoFixResult
                {
                    Success = false,
                    Message = "Auto fix implementation not yet available for this error type."
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auto fix failed for error {ErrorId}", error.Id);
            return new AutoFixResult
            {
                Success = false,
                Message = $"Auto fix failed: {ex.Message}",
                Details = ex.ToString()
            };
        }
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

        return results;
    }

    /// <summary>
    /// Get all unresolved errors across all games.
    /// </summary>
    public async Task<List<GameErrorRecord>> GetAllUnresolvedErrorsAsync(CancellationToken cancellationToken = default)
    {
        return await _dbContext.ErrorRecords
            .Where(e => !e.IsResolved)
            .Include(e => e.GameInfo)
            .OrderByDescending(e => e.Severity)
            .ThenByDescending(e => e.DetectedTime)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
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

            return GameErrorInfo.CreateChineseDirectoryError(gameInfo.Id, path, chineseSegments);
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

        // Check game name for XP/Vista indicators
        if (!string.IsNullOrWhiteSpace(gameName) && XpVistaIndicatorPattern.IsMatch(gameName))
        {
            _logger.LogDebug("Found XP/Vista indicator in game name for game {GameId}", gameInfo.Id);
            return GameErrorInfo.CreateWindowsCompatibilityError(gameInfo.Id, "Windows XP/Vista", "Windows 7");
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
                        return GameErrorInfo.CreateWindowsCompatibilityError(gameInfo.Id, "Windows XP/Vista", "Windows 7");
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
                        return GameErrorInfo.CreateWindowsCompatibilityError(gameInfo.Id, "Windows XP/Vista", "Windows 7");
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
    /// Fix permission issues by running with elevated privileges.
    /// </summary>
    private async Task<AutoFixResult> FixPermissionIssueAsync(GameErrorInfo error, CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();

        // Note: This cannot actually fix permissions programmatically in most cases
        // We can provide guidance only

        return new AutoFixResult
        {
            Success = false,
            Message = "Permission issues require manual intervention.",
            Details = "Move the game folder outside of Program Files, or run the game as administrator each time."
        };
    }

    /// <summary>
    /// Save detected errors to database.
    /// </summary>
    private async Task SaveErrorsToDatabaseAsync(int gameId, List<GameErrorInfo> errors, CancellationToken cancellationToken)
    {
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

            _dbContext.ErrorRecords.Add(record);
        }

        if (errors.Count > 0)
        {
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    #endregion
}