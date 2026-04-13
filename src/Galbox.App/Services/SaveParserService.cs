using Galbox.Data.Entities;

namespace Galbox.App.Services;

/// <summary>
/// Service for parsing game saves.
/// </summary>
public class SaveParserService
{
    /// <summary>
    /// Parse a save file based on engine type.
    /// </summary>
    public SaveParseResult ParseSave(string savePath, string engineType)
    {
        return engineType switch
        {
            "Renpy" => ParseRenpySave(savePath),
            "Krkr" => ParseKrkrSave(savePath),
            "Tyrano" => ParseTyranoSave(savePath),
            _ => new SaveParseResult { Success = false, Message = "Unsupported engine" }
        };
    }

    /// <summary>
    /// Parse Renpy save file.
    /// </summary>
    private SaveParseResult ParseRenpySave(string savePath)
    {
        // Renpy saves are Python pickle format
        // The file contains scene_label which indicates current position
        // TODO: Implement actual parsing

        return new SaveParseResult
        {
            Success = true,
            SceneLabel = "chapter_1_start",
            SaveName = "第一章 - 开始",
            ChapterProgress = 10
        };
    }

    /// <summary>
    /// Parse Krkr save file.
    /// </summary>
    private SaveParseResult ParseKrkrSave(string savePath)
    {
        // Krkr saves are binary .ksd files
        // TODO: Research and implement parsing

        return new SaveParseResult
        {
            Success = true,
            SceneLabel = "",
            SaveName = "存档",
            ChapterProgress = 0
        };
    }

    /// <summary>
    /// Parse Tyrano save file.
    /// </summary>
    private SaveParseResult ParseTyranoSave(string savePath)
    {
        // Tyrano saves are JSON files
        // TODO: Implement JSON parsing

        return new SaveParseResult
        {
            Success = true,
            SceneLabel = "",
            SaveName = "存档",
            ChapterProgress = 0
        };
    }

    /// <summary>
    /// Detect save location for a game based on engine type.
    /// </summary>
    public string? DetectSaveLocation(string gamePath, string engineType)
    {
        return engineType switch
        {
            "Renpy" => DetectRenpySaveLocation(gamePath),
            "Krkr" => Path.Combine(gamePath, "savedata"),
            "Tyrano" => Path.Combine(gamePath, "data", "save"),
            _ => null
        };
    }

    private string? DetectRenpySaveLocation(string gamePath)
    {
        // Renpy saves can be in game/saves/ or in AppData
        var gameSavesPath = Path.Combine(gamePath, "game", "saves");
        if (Directory.Exists(gameSavesPath))
            return gameSavesPath;

        // Check AppData
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var gameName = Path.GetFileName(gamePath);
        var appDataSavePath = Path.Combine(appDataPath, gameName, "saves");
        if (Directory.Exists(appDataSavePath))
            return appDataSavePath;

        return null;
    }
}

/// <summary>
/// Result from save parsing operation.
/// </summary>
public class SaveParseResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? SceneLabel { get; set; }
    public string SaveName { get; set; } = string.Empty;
    public int ChapterProgress { get; set; }
    public int CgUnlockRate { get; set; }
}