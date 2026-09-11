namespace Galbox.Acceptance;

/// <summary>
/// Locates the Galbox source tree (the folder that contains <c>Galbox.sln</c>).
///
/// A8 and A9 inspect source files (XAML pages, App.xaml.cs, NavigationService.cs) because the
/// defects they guard against - a ViewModel with no page, a page that no navigation key can
/// reach, a converter that no XAML dictionary registers - are invisible to a runtime-only test
/// and are exactly the "implemented but no door" class of bug.
/// </summary>
public static class RepoLocator
{
    /// <summary>Marker file that identifies the repository root.</summary>
    public const string SolutionMarker = "Galbox.sln";

    /// <summary>
    /// Finds the repository root by walking up from the test binary location and, as a fallback,
    /// from the current working directory. Returns null when neither contains the marker.
    /// </summary>
    public static string? FindRepoRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, SolutionMarker)))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        return null;
    }

    /// <summary>Path of <c>src/Galbox.App</c> under <paramref name="repoRoot"/>.</summary>
    public static string AppProject(string repoRoot) => Path.Combine(repoRoot, "src", "Galbox.App");

    /// <summary>Path of <c>src/Galbox.App/Views</c> under <paramref name="repoRoot"/>.</summary>
    public static string ViewsFolder(string repoRoot) => Path.Combine(AppProject(repoRoot), "Views");

    /// <summary>
    /// Enumerates the shipping XAML views that define a page class
    /// (<c>x:Class="Galbox.App.Views.X"</c>), skipping build output.
    /// </summary>
    public static IReadOnlyList<string> EnumerateViewFiles(string viewsFolder, string pattern)
    {
        if (!Directory.Exists(viewsFolder))
        {
            return Array.Empty<string>();
        }

        return Directory
            .EnumerateFiles(viewsFolder, pattern, SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                     && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
