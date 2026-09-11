using Microsoft.Win32;

namespace Galbox.Core.Api;

/// <summary>
/// Finds the user's download folder.
///
/// <para>
/// This is the join between the browser hop and the patch engine: the compliant flow cannot hand
/// out a direct link, so the user downloads the package themselves and Galbox adopts it out of
/// this folder. It therefore has to work when the folder has been relocated by the user, which
/// <see cref="Environment.SpecialFolder"/> alone does not cover — that enum has no
/// <c>Downloads</c> member.
/// </para>
/// </summary>
public static class MoyuDownloadsFolder
{
    /// <summary>Registry location of the per-user shell folder paths.</summary>
    private const string ShellFoldersKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders";

    /// <summary>
    /// Resolves the download folder: the registry's configured location first, then the
    /// conventional <c>%USERPROFILE%\Downloads</c>. Falls back rather than throwing, because a
    /// missing folder must not stop the rest of the client from working.
    /// </summary>
    public static string Resolve()
    {
        var fromRegistry = TryReadFromRegistry();
        if (!string.IsNullOrWhiteSpace(fromRegistry))
        {
            return fromRegistry;
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(profile))
        {
            profile = Path.GetTempPath();
        }

        return Path.Combine(profile, "Downloads");
    }

    /// <summary>
    /// Reads the <c>{374DE290-…}</c> shell-folder value. The stored value may contain an
    /// unexpanded <c>%USERPROFILE%</c>, which is expanded here.
    /// </summary>
    private static string? TryReadFromRegistry()
    {
        try
        {
            var raw = Registry.GetValue(ShellFoldersKey, "{374DE290-123F-4565-9164-39C4925E467B}", null) as string;
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            var expanded = Environment.ExpandEnvironmentVariables(raw);

            // A relative or unrooted value is not usable as a watch path.
            return Path.IsPathRooted(expanded) ? expanded : null;
        }
        catch (Exception)
        {
            // The registry is not always readable (policy, sandbox, non-Windows host). Falling
            // back to the conventional path is the right behaviour, not an error.
            return null;
        }
    }

    /// <summary>
    /// The folder that will actually be watched: the configured one when it exists, otherwise the
    /// resolver's answer. Exposed so the UI can show the user where to expect the watcher to look.
    /// </summary>
    public static string ResolveForWatching(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
        {
            return configured;
        }

        return Resolve();
    }
}
