using Microsoft.Extensions.Logging;

namespace Galbox.App.Services;

/// <summary>
/// Service providing common game utility operations.
/// Used by multiple ViewModels to avoid code duplication.
/// </summary>
public class GameUtilityService : IGameUtilityService
{
    private readonly ILogger<GameUtilityService> _logger;

    /// <summary>
    /// Creates a GameUtilityService with injected logger.
    /// </summary>
    public GameUtilityService(ILogger<GameUtilityService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Finds an executable file in a folder.
    /// Priority order: .exe files that look like game executables.
    /// </summary>
    public string? FindExecutableInFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !System.IO.Directory.Exists(folderPath))
        {
            _logger.LogWarning("Invalid folder path: {FolderPath}", folderPath);
            return null;
        }

        try
        {
            // Get all .exe files in the folder (not subdirectories)
            var exeFiles = System.IO.Directory.GetFiles(folderPath, "*.exe", System.IO.SearchOption.TopDirectoryOnly);

            if (exeFiles.Length == 0)
            {
                _logger.LogDebug("No executable files found in {FolderPath}", folderPath);
                return null;
            }

            // Filter out common non-game executables
            var gameExecutables = exeFiles.Where(f =>
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                // Exclude common utility/setup files
                return !name.Contains("setup") &&
                       !name.Contains("install") &&
                       !name.Contains("uninstall") &&
                       !name.Contains("config") &&
                       !name.Contains("launcher") &&
                       !name.Contains("patch") &&
                       !name.StartsWith("readme") &&
                       !name.Contains("update") &&
                       !name.Contains("crack") &&
                       !name.Contains("keygen");
            }).ToList();

            // If no good candidates, use first exe
            if (gameExecutables.Count == 0 && exeFiles.Length > 0)
            {
                _logger.LogDebug("No game-like executables found, using first exe in {FolderPath}", folderPath);
                return exeFiles[0];
            }

            var selectedExe = gameExecutables.FirstOrDefault();
            if (selectedExe != null)
            {
                _logger.LogDebug("Selected executable: {Executable} from {FolderPath}",
                    System.IO.Path.GetFileName(selectedExe), folderPath);
            }

            return selectedExe;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error finding executable in {FolderPath}", folderPath);
            return null;
        }
    }

    /// <summary>
    /// Calculates the total size of a folder.
    /// </summary>
    public long CalculateFolderSize(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !System.IO.Directory.Exists(folderPath))
        {
            _logger.LogWarning("Invalid folder path for size calculation: {FolderPath}", folderPath);
            return 0;
        }

        try
        {
            var dirInfo = new System.IO.DirectoryInfo(folderPath);
            var size = dirInfo.EnumerateFiles("*", System.IO.SearchOption.AllDirectories)
                .Sum(file => file.Length);

            _logger.LogDebug("Calculated folder size: {Size} bytes for {FolderPath}", size, folderPath);
            return size;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Access denied when calculating folder size for {FolderPath}", folderPath);
            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calculating folder size for {FolderPath}", folderPath);
            return 0;
        }
    }
}