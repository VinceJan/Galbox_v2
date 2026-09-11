using System.IO;
using System.Text.Json;
using Galbox.Data.Entities;
using Microsoft.Extensions.Logging;

namespace Galbox.App.Services;

/// <summary>
/// Engine-specific save location detector.
/// Detects save file locations based on game engine type.
/// </summary>
public static class EngineSaveDetector
{
    // Common save file patterns for different engines
    private static readonly string[] RenpySavePatterns = { "*.save", "*.rpy", "*.py" };
    private static readonly string[] KrkrSavePatterns = { "*.ksd", "*.sav", "*.dat", "savedata*" };
    private static readonly string[] TyranoSavePatterns = { "*.sav", "*.json", "save*.dat" };
    private static readonly string[] VnmSavePatterns = { "*.sav", "*.json", "*.dat" };
    private static readonly string[] UnitySavePatterns = { "*.sav", "*.dat", "*.json", "*.prefs" };
    private static readonly string[] RpgMakerSavePatterns = { "*.rvdata2", "*.rvdata", "*.rxdata", "*.lsd", "Save*.rgss*" };
    private static readonly string[] RpgMakerDetectPatterns = { "*.rvdata2", "*.rvdata", "*.rxdata", "*.lsd" };

    /// <summary>
    /// Gets or sets the logger for diagnostic output.
    /// </summary>
    public static ILogger? Logger { get; set; }

    /// <summary>
    /// Detects the game engine type from the game's installation path and executable.
    /// </summary>
    /// <param name="game">The game information</param>
    /// <returns>The detected engine type</returns>
    public static GameEngineType DetectEngineType(GameInfo game)
    {
        if (game == null || string.IsNullOrWhiteSpace(game.InstallPath))
        {
            return GameEngineType.Unknown;
        }

        var installPath = game.InstallPath;
        var executable = game.MainExecutable?.ToLowerInvariant() ?? string.Empty;

        // Check for Renpy (Python-based)
        if (IsRenpyGame(installPath, executable))
        {
            return GameEngineType.Renpy;
        }

        // Check for Krkr (Kirikiri)
        if (IsKrkrGame(installPath, executable))
        {
            return GameEngineType.Krkr;
        }

        // Check for Tyrano
        if (IsTyranoGame(installPath))
        {
            return GameEngineType.Tyrano;
        }

        // Check for VNM (Visual Novel Maker)
        if (IsVnmGame(installPath))
        {
            return GameEngineType.Vnm;
        }

        // Check for Unity
        if (IsUnityGame(installPath, executable))
        {
            return GameEngineType.Unity;
        }

        // Check for RPG Maker
        if (IsRpgMakerGame(installPath, executable))
        {
            return GameEngineType.RpgMaker;
        }

        return GameEngineType.Unknown;
    }

    /// <summary>
    /// Detects save location for a specific engine type.
    /// </summary>
    /// <param name="game">The game information</param>
    /// <param name="engineType">The engine type to detect saves for</param>
    /// <returns>The detected save location result</returns>
    public static SaveLocationResult DetectSaveLocation(GameInfo game, GameEngineType engineType)
    {
        var result = new SaveLocationResult
        {
            EngineType = engineType
        };

        if (game == null || string.IsNullOrWhiteSpace(game.InstallPath))
        {
            result.ErrorMessage = "Game information or install path is invalid";
            return result;
        }

        try
        {
            switch (engineType)
            {
                case GameEngineType.Renpy:
                    result = DetectRenpySaves(game);
                    break;

                case GameEngineType.Krkr:
                    result = DetectKrkrSaves(game);
                    break;

                case GameEngineType.Tyrano:
                    result = DetectTyranoSaves(game);
                    break;

                case GameEngineType.Vnm:
                    result = DetectVnmSaves(game);
                    break;

                case GameEngineType.Unity:
                    result = DetectUnitySaves(game);
                    break;

                case GameEngineType.RpgMaker:
                    result = DetectRpgMakerSaves(game);
                    break;

                default:
                    result = DetectGenericSaves(game);
                    break;
            }

            result.Success = !string.IsNullOrEmpty(result.PrimarySavePath) || result.SaveFiles.Count > 0;
        }
        catch (Exception ex)
        {
            result.ErrorMessage = $"Error detecting saves: {ex.Message}";
            result.Success = false;
        }

        // Last gate before the result leaves the detector: the game installation folder must never
        // be handed out as a "save folder".
        RejectInstallRootAsSavePath(result, game);

        return result;
    }

    /// <summary>
    /// True when <paramref name="candidatePath"/> is the game's installation folder itself
    /// (after path normalisation, case-insensitively).
    /// </summary>
    /// <remarks>
    /// This distinction is the difference between "back up the save files" and "back up (or, on a
    /// restore, delete and rewrite) the entire game installation". The RPG Maker and KiriKiri
    /// branches used to return the installation folder whenever they found save files directly
    /// inside it, and the restore path deletes every file in the "save folder" before copying the
    /// backup back - which could permanently destroy the game.
    /// </remarks>
    public static bool IsGameInstallRoot(string? candidatePath, GameInfo? game)
    {
        if (string.IsNullOrWhiteSpace(candidatePath) || game == null || string.IsNullOrWhiteSpace(game.InstallPath))
        {
            return false;
        }

        var candidate = NormalizeDirectoryPath(candidatePath);
        var installRoot = NormalizeDirectoryPath(game.InstallPath);

        return candidate.Length > 0
            && installRoot.Length > 0
            && string.Equals(candidate, installRoot, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Normalises a directory path to a fully-qualified form without a trailing separator.
    /// Returns an empty string when the path cannot be normalised.
    /// </summary>
    private static string NormalizeDirectoryPath(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex)
        {
            Logger?.LogDebug(ex, "Could not normalise path: {Path}", path);
            return string.Empty;
        }
    }

    /// <summary>
    /// Clears a detection result that points at the game installation folder and explains why.
    /// </summary>
    private static void RejectInstallRootAsSavePath(SaveLocationResult result, GameInfo game)
    {
        var rootSaveFileCount = result.ExtendedInfo.TryGetValue("RootLevelSaveFileCount", out var raw)
            && raw is int count
                ? count
                : 0;

        if (IsGameInstallRoot(result.PrimarySavePath, game))
        {
            var rejected = result.PrimarySavePath!;

            result.PrimarySavePath = null;
            result.Success = false;
            result.ErrorMessage =
                $"已拒绝把游戏安装目录当作存档目录：{rejected}。" +
                "对该目录执行备份或恢复会打包/覆盖整个游戏安装（恢复时的回滚会先清空该目录），可能永久损坏游戏本体。" +
                "请把存档放到安装目录下的子文件夹（例如 save\\、savedata\\、game\\saves\\），或手动指定存档目录。";
            result.ExtendedInfo["RejectedSavePath"] = rejected;
            result.ExtendedInfo["RejectedSavePathReason"] = "install-root-not-a-save-folder";

            Logger?.LogWarning(
                "Refusing to use the game installation folder as a save folder for '{GameName}': {RejectedPath}",
                game.DisplayName,
                rejected);

            return;
        }

        // The save files exist, but the only place they live is the installation folder itself.
        // No directory in the result can be used for a destructive restore, so report a refusal
        // rather than pretending the (missing) parent folder is a save folder.
        if (string.IsNullOrEmpty(result.PrimarySavePath)
            && result.AlternativePaths.Count == 0
            && rootSaveFileCount > 0)
        {
            result.Success = false;
            result.ErrorMessage =
                $"在游戏安装目录根下找到 {rootSaveFileCount} 个存档文件，但未找到可安全使用的存档子目录" +
                $"（{game.InstallPath}）。" +
                "把安装目录本身当作存档目录会在恢复时清空整个游戏目录，因此已拒绝；" +
                "请手动指定存档目录，或把存档移入安装目录下的子文件夹。";
            result.ExtendedInfo["RejectedSavePath"] = game.InstallPath;
            result.ExtendedInfo["RejectedSavePathReason"] = "save-files-only-in-install-root";

            Logger?.LogWarning(
                "Save files for '{GameName}' were found only in the installation root; refusing to derive a save folder from it",
                game.DisplayName);
        }
    }

    #region Engine Detection Methods

    private static bool IsRenpyGame(string installPath, string executable)
    {
        // Check executable name
        if (executable.Contains("python") || executable.EndsWith(".py"))
        {
            return true;
        }

        // Check for Renpy-specific files
        var renpyFolder = Path.Combine(installPath, "renpy");
        if (Directory.Exists(renpyFolder))
        {
            return true;
        }

        // Check for game/script.rpy
        var scriptRpy = Path.Combine(installPath, "game", "script.rpy");
        if (File.Exists(scriptRpy))
        {
            return true;
        }

        // Check for .rpy files
        var rpyFiles = SafeGetFiles(installPath, "*.rpy", SearchOption.TopDirectoryOnly);
        if (rpyFiles.Length > 0)
        {
            return true;
        }

        var gameFolder = Path.Combine(installPath, "game");
        if (Directory.Exists(gameFolder))
        {
            rpyFiles = SafeGetFiles(gameFolder, "*.rpy", SearchOption.TopDirectoryOnly);
            if (rpyFiles.Length > 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsKrkrGame(string installPath, string executable)
    {
        // Check executable extension
        if (executable.EndsWith(".xp3") || executable.Contains("kirikiri"))
        {
            return true;
        }

        // Check for .xp3 archive files (Krkr signature)
        var xp3Files = SafeGetFiles(installPath, "*.xp3", SearchOption.TopDirectoryOnly);
        if (xp3Files.Length > 0)
        {
            return true;
        }

        // Check for savedata folder
        var savedataFolder = Path.Combine(installPath, "savedata");
        if (Directory.Exists(savedataFolder))
        {
            return true;
        }

        return false;
    }

    private static bool IsTyranoGame(string installPath)
    {
        // Check for TyranoBuilder-specific files/folders
        var tyranoFolder = Path.Combine(installPath, "tyrano");
        if (Directory.Exists(tyranoFolder))
        {
            return true;
        }

        // Check for data folder with Tyrano structure
        var dataFolder = Path.Combine(installPath, "data");
        if (Directory.Exists(dataFolder))
        {
            var scenarioFolder = Path.Combine(dataFolder, "scenario");
            var saveFolder = Path.Combine(dataFolder, "save");
            if (Directory.Exists(scenarioFolder) || Directory.Exists(saveFolder))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsVnmGame(string installPath)
    {
        // Check for Visual Novel Maker project file
        var projectJson = Path.Combine(installPath, "project.json");
        if (File.Exists(projectJson))
        {
            return true;
        }

        // Check for VNM-specific folder structure
        var nwjsFolder = Path.Combine(installPath, "nwjs");
        if (Directory.Exists(nwjsFolder))
        {
            return true;
        }

        return false;
    }

    private static bool IsUnityGame(string installPath, string executable)
    {
        // Check executable - Unity games often have Unity-related naming
        if (executable.Contains("unity"))
        {
            return true;
        }

        // Check for Unity-specific files
        var unityDll = Path.Combine(installPath, "UnityPlayer.dll");
        if (File.Exists(unityDll))
        {
            return true;
        }

        // Check for_Data folder (Unity game data folder)
        try
        {
            var dataFolders = Directory.GetDirectories(installPath, "*_Data", SearchOption.TopDirectoryOnly);
            if (dataFolders.Length > 0)
            {
                return true;
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            Logger?.LogWarning(ex, "Access denied when searching Unity data folders in: {Path}", installPath);
        }

        // Check for sharedassets.assets (Unity asset files)
        try
        {
            var enumerationOptions = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                MaxRecursionDepth = 3
            };
            var assetFiles = SafeGetFiles(installPath, "sharedassets*.assets", enumerationOptions);
            if (assetFiles.Length > 0)
            {
                return true;
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            Logger?.LogWarning(ex, "Access denied when searching Unity assets in: {Path}", installPath);
        }

        return false;
    }

    private static bool IsRpgMakerGame(string installPath, string executable)
    {
        // Check for RPG Maker MV/MZ (JavaScript-based)
        var jsFolder = Path.Combine(installPath, "js");
        if (Directory.Exists(jsFolder))
        {
            var rpgJs = Path.Combine(jsFolder, "rpg_core.js");
            if (File.Exists(rpgJs))
            {
                return true;
            }
        }

        // Check for RPG Maker VX Ace (.rvdata2), VX (.rvdata), XP (.rxdata), 2000/2003 (.lsd)
        foreach (var pattern in RpgMakerDetectPatterns)
        {
            try
            {
                var files = SafeGetFiles(installPath, pattern, SearchOption.TopDirectoryOnly);
                if (files.Length > 0)
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger?.LogWarning(ex, "Error checking RPG Maker pattern: {Pattern}", pattern);
            }
        }

        return false;
    }

    #endregion

    #region Save Detection Methods

    private static SaveLocationResult DetectRenpySaves(GameInfo game)
    {
        var result = new SaveLocationResult { EngineType = GameEngineType.Renpy };
        var gameName = GetGameFolderName(game);

        // Renpy saves can be in multiple locations:
        // 1. %APPDATA%/{GameName}/saves/ (simple format)
        // 2. %APPDATA%/{GameName}-{randomHash}/saves/ (common format for newer games)
        // 3. game/saves/ in game folder (portable mode)

        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        // Primary location: search for matching folders in AppData
        // Renpy games often use format: GameName-xxxxx where xxxxx is a random hash
        try
        {
            var appDataFolders = SafeGetDirectories(appDataPath);
            var matchingFolders = appDataFolders
                .Where(d =>
                {
                    var folderName = Path.GetFileName(d);
                    // Check for exact match or GameName-xxxxx pattern
                    return folderName.Equals(gameName, StringComparison.OrdinalIgnoreCase) ||
                           folderName.StartsWith(gameName + "-", StringComparison.OrdinalIgnoreCase);
                })
                .ToList();

            foreach (var folder in matchingFolders)
            {
                var savesPath = Path.Combine(folder, "saves");
                if (Directory.Exists(savesPath))
                {
                    if (string.IsNullOrEmpty(result.PrimarySavePath))
                    {
                        result.PrimarySavePath = savesPath;
                    }
                    else
                    {
                        result.AlternativePaths.Add(savesPath);
                    }
                    CollectSaveFiles(result, savesPath, RenpySavePatterns);
                    Logger?.LogDebug("Found Renpy saves at: {SavesPath}", savesPath);
                }
            }
        }
        catch (Exception ex)
        {
            Logger?.LogWarning(ex, "Error searching AppData for Renpy saves");
        }

        // Alternative location: game/saves/ in game folder
        var gameFolderSavePath = Path.Combine(game.InstallPath, "game", "saves");
        if (Directory.Exists(gameFolderSavePath))
        {
            result.AlternativePaths.Add(gameFolderSavePath);
            CollectSaveFiles(result, gameFolderSavePath, RenpySavePatterns);
        }

        // Check for saves directly in game folder (some older games)
        var directSavePath = Path.Combine(game.InstallPath, "saves");
        if (Directory.Exists(directSavePath))
        {
            result.AlternativePaths.Add(directSavePath);
            CollectSaveFiles(result, directSavePath, RenpySavePatterns);
        }

        result.ExtendedInfo["GameName"] = gameName;
        return result;
    }

    private static SaveLocationResult DetectKrkrSaves(GameInfo game)
    {
        var result = new SaveLocationResult { EngineType = GameEngineType.Krkr };

        // Primary location: savedata/ in game folder
        var savedataPath = Path.Combine(game.InstallPath, "savedata");
        if (Directory.Exists(savedataPath))
        {
            result.PrimarySavePath = savedataPath;
            CollectSaveFiles(result, savedataPath, KrkrSavePatterns);
        }

        // Alternative location: %APPDATA%/{GameDeveloper}/
        // Krkr games sometimes store saves in appdata with developer name
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        try
        {
            var potentialFolders = SafeGetDirectories(appDataPath)
                .Where(d => !string.IsNullOrEmpty(game.Developer) &&
                            Path.GetFileName(d).Contains(game.Developer, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var folder in potentialFolders)
            {
                result.AlternativePaths.Add(folder);
                CollectSaveFiles(result, folder, KrkrSavePatterns);
            }
        }
        catch (Exception ex)
        {
            Logger?.LogWarning(ex, "Error searching AppData for Krkr saves");
        }

        // Check for .ksd files in game folder directly.
        //
        // These files are reported so the user can see that saves exist, but the installation
        // folder must NOT become the save folder: a destructive restore of "the save folder"
        // would clear the whole game directory. DetectSaveLocation rejects such a result.
        var ksdFiles = SafeGetFiles(game.InstallPath, "*.ksd", SearchOption.TopDirectoryOnly);
        if (ksdFiles.Length > 0)
        {
            result.SaveFiles.AddRange(ksdFiles);
            result.ExtendedInfo["RootLevelSaveFileCount"] = ksdFiles.Length;

            Logger?.LogWarning(
                "KiriKiri save files (*.ksd) were found directly in the installation root of '{GameName}'; "
                + "the installation folder itself is not used as a save folder",
                game.DisplayName);
        }

        return result;
    }

    private static SaveLocationResult DetectTyranoSaves(GameInfo game)
    {
        var result = new SaveLocationResult { EngineType = GameEngineType.Tyrano };

        // Primary location: data/save/ in game folder
        var dataSavePath = Path.Combine(game.InstallPath, "data", "save");
        if (Directory.Exists(dataSavePath))
        {
            result.PrimarySavePath = dataSavePath;
            CollectSaveFiles(result, dataSavePath, TyranoSavePatterns);
        }

        // Alternative: tyrano/save/
        var tyranoSavePath = Path.Combine(game.InstallPath, "tyrano", "save");
        if (Directory.Exists(tyranoSavePath))
        {
            result.AlternativePaths.Add(tyranoSavePath);
            CollectSaveFiles(result, tyranoSavePath, TyranoSavePatterns);
        }

        return result;
    }

    private static SaveLocationResult DetectVnmSaves(GameInfo game)
    {
        var result = new SaveLocationResult { EngineType = GameEngineType.Vnm };

        // Check project.json for save path
        var projectJsonPath = Path.Combine(game.InstallPath, "project.json");
        if (File.Exists(projectJsonPath))
        {
            try
            {
                var jsonContent = File.ReadAllText(projectJsonPath);
                var projectData = JsonSerializer.Deserialize<VnmProjectData>(jsonContent);

                if (projectData?.SavePath != null)
                {
                    var customSavePath = projectData.SavePath;
                    // Resolve relative path
                    if (!Path.IsPathRooted(customSavePath))
                    {
                        customSavePath = Path.Combine(game.InstallPath, customSavePath);
                    }

                    if (Directory.Exists(customSavePath))
                    {
                        result.PrimarySavePath = customSavePath;
                        CollectSaveFiles(result, customSavePath, VnmSavePatterns);
                    }
                }
            }
            catch (JsonException)
            {
                // Invalid JSON, continue with default detection
            }
        }

        // Default VNM save locations
        var defaultSavePath = Path.Combine(game.InstallPath, "save");
        if (Directory.Exists(defaultSavePath))
        {
            if (string.IsNullOrEmpty(result.PrimarySavePath))
            {
                result.PrimarySavePath = defaultSavePath;
            }
            else
            {
                result.AlternativePaths.Add(defaultSavePath);
            }
            CollectSaveFiles(result, defaultSavePath, VnmSavePatterns);
        }

        // Check nwjs save location
        var nwjsSavePath = Path.Combine(game.InstallPath, "nwjs", "save");
        if (Directory.Exists(nwjsSavePath))
        {
            result.AlternativePaths.Add(nwjsSavePath);
            CollectSaveFiles(result, nwjsSavePath, VnmSavePatterns);
        }

        return result;
    }

    private static SaveLocationResult DetectUnitySaves(GameInfo game)
    {
        var result = new SaveLocationResult { EngineType = GameEngineType.Unity };
        var gameName = GetGameFolderName(game);

        // Unity saves typically in:
        // 1. %APPDATA%/../LocalLow/{CompanyName}/{GameName}/
        // 2. Registry (PlayerPrefs) - handled separately
        // 3. game folder sometimes

        // Check LocalLow folder (most common for Unity games)
        // Use UserProfile to get proper LocalLow path
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localLowPath = Path.Combine(userProfile, "AppData", "LocalLow");

        if (Directory.Exists(localLowPath))
        {
            try
            {
                // Find folders matching game name
                var companyFolders = SafeGetDirectories(localLowPath);
                foreach (var companyFolder in companyFolders)
                {
                    var gameFolders = SafeGetDirectories(companyFolder)
                        .Where(d => Path.GetFileName(d).Contains(gameName, StringComparison.OrdinalIgnoreCase) ||
                                    (!string.IsNullOrEmpty(game.NameOriginal) &&
                                     Path.GetFileName(d).Contains(game.NameOriginal, StringComparison.OrdinalIgnoreCase)))
                        .ToList();

                    foreach (var gameFolder in gameFolders)
                    {
                        if (string.IsNullOrEmpty(result.PrimarySavePath))
                        {
                            result.PrimarySavePath = gameFolder;
                        }
                        else
                        {
                            result.AlternativePaths.Add(gameFolder);
                        }
                        CollectSaveFiles(result, gameFolder, UnitySavePatterns);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger?.LogWarning(ex, "Error searching LocalLow for Unity saves");
            }
        }

        // Check game folder for saves
        var gameSaveFolder = Path.Combine(game.InstallPath, "Saves");
        if (Directory.Exists(gameSaveFolder))
        {
            result.AlternativePaths.Add(gameSaveFolder);
            CollectSaveFiles(result, gameSaveFolder, UnitySavePatterns);
        }

        // Note: PlayerPrefs (registry) not included in file-based backup
        result.ExtendedInfo["HasRegistryPrefs"] = true;

        return result;
    }

    private static SaveLocationResult DetectRpgMakerSaves(GameInfo game)
    {
        var result = new SaveLocationResult { EngineType = GameEngineType.RpgMaker };

        // RPG Maker MV/MZ saves in www/save/ or save/
        var wwwSavePath = Path.Combine(game.InstallPath, "www", "save");
        if (Directory.Exists(wwwSavePath))
        {
            result.PrimarySavePath = wwwSavePath;
            CollectSaveFiles(result, wwwSavePath, RpgMakerSavePatterns);
        }

        // Check save/ directly
        var savePath = Path.Combine(game.InstallPath, "save");
        if (Directory.Exists(savePath))
        {
            if (string.IsNullOrEmpty(result.PrimarySavePath))
            {
                result.PrimarySavePath = savePath;
            }
            else
            {
                result.AlternativePaths.Add(savePath);
            }
            CollectSaveFiles(result, savePath, RpgMakerSavePatterns);
        }

        // RPG Maker VX Ace saves in game folder directly (.rvdata2)
        // RPG Maker VX saves (.rvdata), XP saves (.rxdata), 2000/2003 saves (.lsd)
        //
        // Same rule as KiriKiri above: report the files, never hand out the installation folder
        // as the save folder (see RejectInstallRootAsSavePath).
        var rootSaveFileCount = 0;
        foreach (var pattern in RpgMakerDetectPatterns)
        {
            try
            {
                var files = SafeGetFiles(game.InstallPath, pattern, SearchOption.TopDirectoryOnly);
                if (files.Length > 0)
                {
                    rootSaveFileCount += files.Length;
                    result.SaveFiles.AddRange(files);
                }
            }
            catch (Exception ex)
            {
                Logger?.LogWarning(ex, "Error detecting RPG Maker saves with pattern: {Pattern}", pattern);
            }
        }

        if (rootSaveFileCount > 0)
        {
            result.ExtendedInfo["RootLevelSaveFileCount"] = rootSaveFileCount;

            Logger?.LogWarning(
                "RPG Maker save files were found directly in the installation root of '{GameName}'; "
                + "the installation folder itself is not used as a save folder",
                game.DisplayName);
        }

        return result;
    }

    private static SaveLocationResult DetectGenericSaves(GameInfo game)
    {
        var result = new SaveLocationResult { EngineType = GameEngineType.Unknown };

        // Search for common save folder names
        var commonSaveFolders = new[] { "save", "saves", "savedata", "Save", "Saves", "SaveData" };
        foreach (var folderName in commonSaveFolders)
        {
            var saveFolder = Path.Combine(game.InstallPath, folderName);
            if (Directory.Exists(saveFolder))
            {
                if (string.IsNullOrEmpty(result.PrimarySavePath))
                {
                    result.PrimarySavePath = saveFolder;
                }
                else
                {
                    result.AlternativePaths.Add(saveFolder);
                }
                CollectAllSaveFiles(result, saveFolder);
            }
        }

        // Search for common save file patterns in game folder.
        //
        // Root-level save files are reported (so the user learns that saves exist) but they never
        // promote the installation folder to "the save folder" - see RejectInstallRootAsSavePath.
        var commonPatterns = new[] { "*.sav", "*.save", "*.dat", "*.bak" };
        var rootLevelCount = commonPatterns
            .Sum(pattern => SafeGetFiles(game.InstallPath, pattern, SearchOption.TopDirectoryOnly).Length);
        CollectSaveFiles(result, game.InstallPath, commonPatterns);

        if (rootLevelCount > 0)
        {
            result.ExtendedInfo["RootLevelSaveFileCount"] = rootLevelCount;
        }

        return result;
    }

    #endregion

    #region Helper Methods

    private static string[] SafeGetFiles(string path, string pattern, SearchOption searchOption)
    {
        try
        {
            return Directory.GetFiles(path, pattern, searchOption);
        }
        catch (UnauthorizedAccessException ex)
        {
            Logger?.LogWarning(ex, "Access denied when searching files in: {Path}", path);
            return Array.Empty<string>();
        }
        catch (DirectoryNotFoundException)
        {
            return Array.Empty<string>();
        }
        catch (IOException ex)
        {
            Logger?.LogWarning(ex, "IO error when searching files in: {Path}", path);
            return Array.Empty<string>();
        }
    }

    private static string[] SafeGetFiles(string path, string pattern, EnumerationOptions options)
    {
        try
        {
            return Directory.GetFiles(path, pattern, options);
        }
        catch (UnauthorizedAccessException ex)
        {
            Logger?.LogWarning(ex, "Access denied when searching files in: {Path}", path);
            return Array.Empty<string>();
        }
        catch (DirectoryNotFoundException)
        {
            return Array.Empty<string>();
        }
        catch (IOException ex)
        {
            Logger?.LogWarning(ex, "IO error when searching files in: {Path}", path);
            return Array.Empty<string>();
        }
    }

    private static string[] SafeGetDirectories(string path)
    {
        try
        {
            return Directory.GetDirectories(path);
        }
        catch (UnauthorizedAccessException ex)
        {
            Logger?.LogWarning(ex, "Access denied when listing directories in: {Path}", path);
            return Array.Empty<string>();
        }
        catch (DirectoryNotFoundException)
        {
            return Array.Empty<string>();
        }
        catch (IOException ex)
        {
            Logger?.LogWarning(ex, "IO error when listing directories in: {Path}", path);
            return Array.Empty<string>();
        }
    }

    private static string GetGameFolderName(GameInfo game)
    {
        // Use the game folder name or the game name
        if (!string.IsNullOrWhiteSpace(game.InstallPath))
        {
            var folderName = Path.GetFileName(game.InstallPath.TrimEnd(Path.DirectorySeparatorChar));
            if (!string.IsNullOrWhiteSpace(folderName))
            {
                return folderName;
            }
        }

        // Fallback to game name
        return game.NameCn ?? game.NameOriginal ?? "UnknownGame";
    }

    private static readonly EnumerationOptions RecursionOptions = new()
    {
        RecurseSubdirectories = true,
        MaxRecursionDepth = 10
    };

    private static void CollectSaveFiles(SaveLocationResult result, string path, string[] patterns)
    {
        try
        {
            // Use HashSet for deduplication to improve performance
            var newFiles = new HashSet<string>();

            foreach (var pattern in patterns)
            {
                var files = Directory.GetFiles(path, pattern, RecursionOptions);
                foreach (var file in files)
                {
                    newFiles.Add(file);
                }
            }

            // Add to result, avoiding duplicates
            foreach (var file in newFiles)
            {
                result.SaveFiles.Add(file);
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            Logger?.LogWarning(ex, "Access denied when collecting save files from: {Path}", path);
        }
        catch (DirectoryNotFoundException)
        {
            Logger?.LogDebug("Directory not found when collecting save files: {Path}", path);
        }
        catch (IOException ex)
        {
            Logger?.LogWarning(ex, "IO error when collecting save files from: {Path}", path);
        }
    }

    private static void CollectAllSaveFiles(SaveLocationResult result, string path)
    {
        try
        {
            var files = Directory.GetFiles(path, "*.*", RecursionOptions);
            foreach (var file in files)
            {
                result.SaveFiles.Add(file);
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            Logger?.LogWarning(ex, "Access denied when collecting all save files from: {Path}", path);
        }
        catch (DirectoryNotFoundException)
        {
            Logger?.LogDebug("Directory not found when collecting all save files: {Path}", path);
        }
        catch (IOException ex)
        {
            Logger?.LogWarning(ex, "IO error when collecting all save files from: {Path}", path);
        }
    }

    /// <summary>
    /// Gets the list of all files in a directory with their sizes.
    /// </summary>
    /// <param name="directoryPath">The directory path</param>
    /// <returns>List of files with their sizes</returns>
    public static List<(string Path, long Size)> GetFilesWithSizes(string directoryPath)
    {
        var files = new List<(string Path, long Size)>();

        if (!Directory.Exists(directoryPath))
        {
            return files;
        }

        try
        {
            var allFiles = Directory.GetFiles(directoryPath, "*.*", SearchOption.AllDirectories);
            foreach (var file in allFiles)
            {
                try
                {
                    var fileInfo = new FileInfo(file);
                    files.Add((file, fileInfo.Length));
                }
                catch (IOException)
                {
                    files.Add((file, 0)); // Default size on error
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Skip inaccessible files
        }

        return files;
    }

    #endregion

    #region JSON Data Classes

    /// <summary>
    /// VNM project.json data structure for save path detection.
    /// </summary>
    private class VnmProjectData
    {
        public string? SavePath { get; set; }
        public string? GameName { get; set; }
        public string? Version { get; set; }
    }

    #endregion
}