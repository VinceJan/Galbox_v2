using System.Diagnostics;

namespace Galbox.Tests.Support;

/// <summary>
/// Locates the work tree this test assembly was built from and the shipping executable inside it.
///
/// The previous version of this project hard-coded
/// <c>E:\tmp\Galbox_v2\src\Galbox.App\bin\...\Galbox.App.exe</c>, which meant that even when it was
/// executed it launched <em>another checkout's</em> binary - the tests and the sources under test
/// were not necessarily the same tree. Everything here is derived by walking up from the test
/// assembly's own location, so the test can only ever launch the application built from the
/// work tree it lives in.
/// </summary>
internal static class RepoLayout
{
    /// <summary>Marker file that identifies the repository root.</summary>
    public const string SolutionMarker = "Galbox.sln";

    /// <summary>
    /// Finds the repository root by walking up from the test binary location, then from the
    /// current directory. Returns null when neither contains the marker.
    /// </summary>
    public static string? FindRepositoryRoot()
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

    /// <summary>Path of <c>src/Galbox.App</c> under <paramref name="repositoryRoot"/>.</summary>
    public static string AppProject(string repositoryRoot) =>
        Path.Combine(repositoryRoot, "src", "Galbox.App");

    /// <summary>
    /// Finds the <c>Galbox.App.exe</c> built from <paramref name="repositoryRoot"/>, choosing the
    /// output folder that was written last so a stale configuration folder can never win, and
    /// reports that folder's build time.
    ///
    /// The build time is the newest file in the output folder rather than the timestamp of
    /// <c>Galbox.App.exe</c> itself: for an unpackaged WinUI application the apphost executable is
    /// not rewritten when only managed code changes, so its own timestamp would look stale forever.
    /// </summary>
    public static string? FindApplicationExecutable(string repositoryRoot, out DateTime buildUtc)
    {
        buildUtc = DateTime.MinValue;

        var binRoot = Path.Combine(AppProject(repositoryRoot), "bin");
        if (!Directory.Exists(binRoot))
        {
            return null;
        }

        var newest = Directory
            .EnumerateFiles(binRoot, "Galbox.App.exe", SearchOption.AllDirectories)
            .Select(path => (Path: path, BuildUtc: NewestFileUtc(Path.GetDirectoryName(path)!)))
            .OrderByDescending(candidate => candidate.BuildUtc)
            .ToList();

        if (newest.Count == 0)
        {
            return null;
        }

        buildUtc = newest[0].BuildUtc;
        return newest[0].Path;
    }

    /// <summary>
    /// Newest write time of any compiled or parsed source of the application project
    /// (<c>*.cs</c>, <c>*.xaml</c>), ignoring build output. A binary older than its sources cannot
    /// be evidence about those sources.
    /// </summary>
    public static DateTime NewestApplicationSourceUtc(string repositoryRoot)
    {
        var projectFolder = AppProject(repositoryRoot);
        if (!Directory.Exists(projectFolder))
        {
            return DateTime.MinValue;
        }

        return Directory
            .EnumerateFiles(projectFolder, "*", SearchOption.AllDirectories)
            .Where(path => (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                         || path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
                        && !IsBuildOutput(path))
            .Select(File.GetLastWriteTimeUtc)
            .DefaultIfEmpty(DateTime.MinValue)
            .Max();
    }

    /// <summary>True when <paramref name="path"/> is inside a <c>bin</c> or <c>obj</c> folder.</summary>
    public static bool IsBuildOutput(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
     || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    private static DateTime NewestFileUtc(string folder) =>
        Directory
            .EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
            .Select(File.GetLastWriteTimeUtc)
            .DefaultIfEmpty(DateTime.MinValue)
            .Max();

    /// <summary>
    /// Terminates leftover <c>Galbox.App</c> processes whose image lives inside
    /// <paramref name="repositoryRoot"/> only.
    ///
    /// Scoped on purpose: this machine runs several checkouts of the same application side by side,
    /// and a test that killed every instance by name would silently interfere with a developer or
    /// another agent working in a different work tree.
    /// </summary>
    public static IReadOnlyList<string> KillLeftoverInstancesOfThisWorkTree(string repositoryRoot)
    {
        var killed = new List<string>();

        foreach (var process in Process.GetProcessesByName("Galbox.App"))
        {
            try
            {
                var imagePath = process.MainModule?.FileName;
                if (imagePath is null ||
                    !imagePath.StartsWith(repositoryRoot, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var id = process.Id;
                process.Kill(entireProcessTree: true);
                process.WaitForExit(10_000);
                killed.Add($"pid {id} ({imagePath})");
            }
            catch (Exception ex)
            {
                killed.Add($"pid {process.Id} could not be terminated: {ex.Message}");
            }
            finally
            {
                process.Dispose();
            }
        }

        return killed;
    }
}
