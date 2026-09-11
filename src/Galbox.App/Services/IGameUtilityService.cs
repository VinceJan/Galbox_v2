namespace Galbox.App.Services;

/// <summary>
/// Interface for game utility operations.
/// Provides common game-related helper methods.
/// </summary>
public interface IGameUtilityService
{
    /// <summary>
    /// Finds an executable file in a folder.
    /// Priority order: .exe files that look like game executables.
    /// </summary>
    /// <param name="folderPath">The folder path to search</param>
    /// <returns>The path to the best executable candidate, or null if none found</returns>
    string? FindExecutableInFolder(string folderPath);

    /// <summary>
    /// Calculates the total size of a folder.
    /// </summary>
    /// <param name="folderPath">The folder path to calculate</param>
    /// <returns>Total size in bytes, or 0 if calculation fails</returns>
    long CalculateFolderSize(string folderPath);
}