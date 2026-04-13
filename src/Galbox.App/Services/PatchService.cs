namespace Galbox.App.Services;

/// <summary>
/// Service for managing patches.
/// </summary>
public class PatchService
{
    // TODO: Implement moyu.moe API integration

    /// <summary>
    /// Search patches for a game.
    /// </summary>
    public async Task<List<PatchSearchResult>> SearchPatchesAsync(string gameName)
    {
        // TODO: Call moyu.moe API
        return new List<PatchSearchResult>();
    }

    /// <summary>
    /// Download a patch.
    /// </summary>
    public async Task<bool> DownloadPatchAsync(string patchUrl, string downloadPath)
    {
        // TODO: Implement download logic
        return false;
    }

    /// <summary>
    /// Install a patch to a game directory.
    /// </summary>
    public bool InstallPatch(string patchPath, string gamePath)
    {
        // Most patches are simple unzip and overwrite
        // TODO: Implement unzip and file copy

        return false;
    }

    /// <summary>
    /// Check if a patch is already installed.
    /// </summary>
    public bool CheckPatchInstalled(string gamePath, string patchName)
    {
        // TODO: Check for patch installation traces
        return false;
    }
}

/// <summary>
/// Result from patch search operation.
/// </summary>
public class PatchSearchResult
{
    public string PatchId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string? DownloadUrl { get; set; }
    public long FileSize { get; set; }
    public string? Description { get; set; }
    public DateTime? UpdateDate { get; set; }
}