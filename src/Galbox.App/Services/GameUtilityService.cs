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

            // If no good candidates, fall back to every exe in the folder.
            if (gameExecutables.Count == 0)
            {
                _logger.LogDebug("No game-like executables found, ranking all exes in {FolderPath}", folderPath);
                gameExecutables = exeFiles.ToList();
            }

            // Rank candidates instead of trusting OS enumeration order.
            // Motivation: a game may ship several launchers (e.g. "game.exe" and
            // "game-32.exe"). Picking whichever the OS happens to enumerate first is
            // non-deterministic across machines and may launch the wrong build.
            var selectedExe = gameExecutables
                .Select(f => new
                {
                    Path = f,
                    Score = ScoreExecutableCandidate(f, folderPath),
                    Size = GetFileSizeSafe(f)
                })
                .OrderByDescending(x => x.Score)      // best name match first
                .ThenByDescending(x => x.Size)        // a main game exe is usually the largest
                .ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase) // deterministic tie-break
                .Select(x => x.Path)
                .FirstOrDefault();

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
    /// Scores an executable candidate so that the most likely game launcher wins.
    /// Higher is better. The score is deterministic and only depends on the file name
    /// and the containing folder name, so the same folder always yields the same pick.
    /// </summary>
    private static int ScoreExecutableCandidate(string filePath, string folderPath)
    {
        var fileName = System.IO.Path.GetFileNameWithoutExtension(filePath) ?? string.Empty;
        var lower = fileName.ToLowerInvariant();

        var folderRaw = System.IO.Path.GetFileName(folderPath.TrimEnd(
            System.IO.Path.DirectorySeparatorChar,
            System.IO.Path.AltDirectorySeparatorChar)) ?? string.Empty;

        var normalizedName = NormalizeToken(lower);
        var normalizedFolder = NormalizeToken(folderRaw);

        var score = 0;

        // 1. An exact match with the folder name is the strongest signal.
        if (normalizedFolder.Length > 0 && normalizedName == normalizedFolder)
        {
            score += 1000;
        }
        else if (normalizedFolder.Length > 0 && normalizedName.StartsWith(normalizedFolder, StringComparison.Ordinal))
        {
            score += 500;
        }
        else if (normalizedFolder.Length > 0 && normalizedFolder.StartsWith(normalizedName, StringComparison.Ordinal))
        {
            score += 250;
        }

        // 2. An architecture / bitness suffix ("-32", "_x64", "64", "x86") marks an
        //    alternate build rather than the primary launcher, so it is penalised.
        var withoutArchSuffix = System.Text.RegularExpressions.Regex.Replace(
            lower,
            @"[_\-\s]?(x86|x64|32|64)$",
            string.Empty,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (!string.Equals(withoutArchSuffix, lower, StringComparison.Ordinal))
        {
            score -= 300;

            // If stripping the suffix yields the folder name, this is a secondary build
            // of the same game: keep it clearly ahead of unrelated executables.
            if (normalizedFolder.Length > 0 && NormalizeToken(withoutArchSuffix) == normalizedFolder)
            {
                score += 400;
            }
        }

        // 3. Prefer shorter, simpler names; a long appended suffix usually means a helper.
        score -= Math.Min(lower.Length, 40);

        return score;
    }

    /// <summary>
    /// Lowercases the value and strips everything that is not a letter or a digit,
    /// so that "Dreamin'_Her" and "dreaminher" compare equal.
    /// </summary>
    private static string NormalizeToken(string value)
    {
        var buffer = new System.Text.StringBuilder(value.Length);

        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                buffer.Append(char.ToLowerInvariant(c));
            }
        }

        return buffer.ToString();
    }

    /// <summary>
    /// Returns the size of a file, or 0 when it cannot be read. Never throws.
    /// </summary>
    private static long GetFileSizeSafe(string filePath)
    {
        try
        {
            return new System.IO.FileInfo(filePath).Length;
        }
        catch
        {
            return 0;
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