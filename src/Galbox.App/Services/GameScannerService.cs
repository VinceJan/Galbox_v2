using Galbox.Data.Database;
using Galbox.Data.Entities;

namespace Galbox.App.Services;

/// <summary>
/// Service for scanning and detecting games.
/// </summary>
public class GameScannerService
{
    private readonly GalboxDbContext _dbContext;

    public GameScannerService(GalboxDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// Scan a directory for games.
    /// </summary>
    public List<GameInfo> ScanDirectory(string directoryPath)
    {
        var games = new List<GameInfo>();

        if (!Directory.Exists(directoryPath))
            return games;

        // Find executable files
        var executables = Directory.GetFiles(directoryPath, "*.exe", SearchOption.AllDirectories)
            .Where(f => !IsSystemFile(f))
            .ToList();

        foreach (var exePath in executables)
        {
            var gamePath = Path.GetDirectoryName(exePath) ?? directoryPath;
            var gameName = Path.GetFileNameWithoutExtension(exePath);

            // Detect engine type
            var engineType = DetectEngineType(gamePath);

            var game = new GameInfo
            {
                NameCn = gameName,
                NameOriginal = gameName,
                InstallPath = gamePath,
                ExecutablePath = exePath,
                EngineType = engineType,
                Status = GameStatus.NeverPlayed,
                AddedDate = DateTime.Now
            };

            games.Add(game);
        }

        return games;
    }

    /// <summary>
    /// Detect the game engine type from directory structure.
    /// </summary>
    private string DetectEngineType(string gamePath)
    {
        // Renpy: has game/saves/ directory or .rpy files
        if (Directory.Exists(Path.Combine(gamePath, "game", "saves")) ||
            Directory.GetFiles(gamePath, "*.rpy", SearchOption.AllDirectories).Any())
        {
            return "Renpy";
        }

        // Krkr: has .xp3 files
        if (Directory.GetFiles(gamePath, "*.xp3", SearchOption.AllDirectories).Any())
        {
            return "Krkr";
        }

        // Tyrano: has tyrano/*.js files or data/save/ directory
        if (Directory.Exists(Path.Combine(gamePath, "tyrano")) ||
            Directory.Exists(Path.Combine(gamePath, "data", "save")))
        {
            return "Tyrano";
        }

        return "Unknown";
    }

    /// <summary>
    /// Check if a file is a system file (should not be considered a game).
    /// </summary>
    private bool IsSystemFile(string filePath)
    {
        var fileName = Path.GetFileName(filePath).ToLowerInvariant();
        var systemFiles = new[] { "setup.exe", "uninstall.exe", "config.exe", "autorun.exe" };
        return systemFiles.Contains(fileName);
    }

    /// <summary>
    /// Add a game to the library.
    /// </summary>
    public void AddGame(GameInfo game)
    {
        _dbContext.Games.Add(game);
        _dbContext.SaveChanges();
    }

    /// <summary>
    /// Get all games in the library.
    /// </summary>
    public List<GameInfo> GetAllGames()
    {
        return _dbContext.Games.OrderByDescending(g => g.LastPlayedDate ?? g.AddedDate).ToList();
    }

    /// <summary>
    /// Get games by status.
    /// </summary>
    public List<GameInfo> GetGamesByStatus(GameStatus status)
    {
        return _dbContext.Games.Where(g => g.Status == status).ToList();
    }

    /// <summary>
    /// Search games by name.
    /// </summary>
    public List<GameInfo> SearchGames(string query)
    {
        return _dbContext.Games
            .Where(g => g.NameCn.Contains(query) || g.NameOriginal.Contains(query))
            .ToList();
    }
}