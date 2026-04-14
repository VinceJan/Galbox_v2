using System.IO;
using System.Text.Json;
using Galbox.Data.Entities;

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
    private static readonly string[] RpgMakerSavePatterns = { "*.rvdata2", "*.rvdata", "*.lsd", "Save*.rgss*" };

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

        return result;
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
        try
        {
            var rpyFiles = Directory.GetFiles(installPath, "*.rpy", SearchOption.TopDirectoryOnly);
            if (rpyFiles.Length > 0)
            {
                return true;
            }

            var gameFolder = Path.Combine(installPath, "game");
            if (Directory.Exists(gameFolder))
            {
                rpyFiles = Directory.GetFiles(gameFolder, "*.rpy", SearchOption.TopDirectoryOnly);
                if (rpyFiles.Length > 0)
                {
                    return true;
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
            // Directory doesn't exist
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
        try
        {
            var xp3Files = Directory.GetFiles(installPath, "*.xp3", SearchOption.TopDirectoryOnly);
            if (xp3Files.Length > 0)
            {
                return true;
            }
        }
        catch (DirectoryNotFoundException)
        {
            // Directory doesn't exist
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
        var dataFolders = Directory.GetDirectories(installPath, "*_Data", SearchOption.TopDirectoryOnly);
        if (dataFolders.Length > 0)
        {
            return true;
        }

        // Check for sharedassets.assets (Unity asset files)
        try
        {
            var enumerationOptions = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                MaxRecursionDepth = 3
            };
            var assetFiles = Directory.GetFiles(installPath, "sharedassets*.assets", enumerationOptions);
            if (assetFiles.Length > 0)
            {
                return true;
            }
        }
        catch (DirectoryNotFoundException)
        {
            // Ignore
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

        // Check for RPG Maker VX Ace (Ruby-based)
        try
        {
            var rvdataFiles = Directory.GetFiles(installPath, "*.rvdata2", SearchOption.TopDirectoryOnly);
            if (rvdataFiles.Length > 0)
            {
                return true;
            }
        }
        catch (DirectoryNotFoundException)
        {
            // Ignore
        }

        // Check for RPG Maker 2000/2003
        try
        {
            var lsdFiles = Directory.GetFiles(installPath, "*.lsd", SearchOption.TopDirectoryOnly);
            if (lsdFiles.Length > 0)
            {
                return true;
            }
        }
        catch (DirectoryNotFoundException)
        {
            // Ignore
        }

        return false;
    }

    #endregion

    #region Save Detection Methods

    private static SaveLocationResult DetectRenpySaves(GameInfo game)
    {
        var result = new SaveLocationResult { EngineType = GameEngineType.Renpy };
        var gameName = GetGameFolderName(game);

        // Primary location: %APPDATA%/{GameName}/saves/
        var appDataSavePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            gameName,
            "saves");

        if (Directory.Exists(appDataSavePath))
        {
            result.PrimarySavePath = appDataSavePath;
            CollectSaveFiles(result, appDataSavePath, RenpySavePatterns);
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
            var potentialFolders = Directory.GetDirectories(appDataPath)
                .Where(d => !string.IsNullOrEmpty(game.Developer) &&
                            Path.GetFileName(d).Contains(game.Developer, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var folder in potentialFolders)
            {
                result.AlternativePaths.Add(folder);
                CollectSaveFiles(result, folder, KrkrSavePatterns);
            }
        }
        catch (DirectoryNotFoundException)
        {
            // Ignore
        }

        // Check for .ksd files in game folder directly
        try
        {
            var ksdFiles = Directory.GetFiles(game.InstallPath, "*.ksd", SearchOption.TopDirectoryOnly);
            if (ksdFiles.Length > 0)
            {
                if (string.IsNullOrEmpty(result.PrimarySavePath))
                {
                    result.PrimarySavePath = game.InstallPath;
                }
                result.SaveFiles.AddRange(ksdFiles);
            }
        }
        catch (DirectoryNotFoundException)
        {
            // Ignore
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
                var companyFolders = Directory.GetDirectories(localLowPath);
                foreach (var companyFolder in companyFolders)
                {
                    var gameFolders = Directory.GetDirectories(companyFolder)
                        .Where(d => Path.GetFileName(d).Contains(gameName, StringComparison.OrdinalIgnoreCase) ||
                                    Path.GetFileName(d).Contains(game.NameOriginal, StringComparison.OrdinalIgnoreCase))
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
            catch (DirectoryNotFoundException)
            {
                // Ignore
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
        try
        {
            var rvdataFiles = Directory.GetFiles(game.InstallPath, "*.rvdata2", SearchOption.TopDirectoryOnly);
            if (rvdataFiles.Length > 0)
            {
                if (string.IsNullOrEmpty(result.PrimarySavePath))
                {
                    result.PrimarySavePath = game.InstallPath;
                }
                result.SaveFiles.AddRange(rvdataFiles);
            }
        }
        catch (DirectoryNotFoundException)
        {
            // Ignore
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

        // Search for common save file patterns in game folder
        var commonPatterns = new[] { "*.sav", "*.save", "*.dat", "*.bak" };
        CollectSaveFiles(result, game.InstallPath, commonPatterns);

        return result;
    }

    #endregion

    #region Helper Methods

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
            foreach (var pattern in patterns)
            {
                var files = Directory.GetFiles(path, pattern, RecursionOptions);
                foreach (var file in files)
                {
                    if (!result.SaveFiles.Contains(file))
                    {
                        result.SaveFiles.Add(file);
                    }
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Skip inaccessible directories
        }
        catch (DirectoryNotFoundException)
        {
            // Directory doesn't exist
        }
    }

    private static void CollectAllSaveFiles(SaveLocationResult result, string path)
    {
        try
        {
            var files = Directory.GetFiles(path, "*.*", RecursionOptions);
            foreach (var file in files)
            {
                if (!result.SaveFiles.Contains(file))
                {
                    result.SaveFiles.Add(file);
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Skip inaccessible directories
        }
        catch (DirectoryNotFoundException)
        {
            // Directory doesn't exist
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