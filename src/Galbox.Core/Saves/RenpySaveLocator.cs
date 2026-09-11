// Step 0 — save-directory discovery.
//
// Ren'Py 7.x registers two FileLocations and MultiLocation.save() writes to BOTH on every save:
//   1. %APPDATA%\RenPy\<config.save_directory>   (the "user" savedir)
//   2. <gamedir>\saves                            (the "game local" savedir)
// The user directory name is derived from the game's own options.rpy, so it must be read from
// the game rather than guessed. Which of the two to read is decided by file mtime, never by a
// hard-coded preference.
using System.Text.RegularExpressions;
using Galbox.Core.Saves.Pickle;
using Galbox.Core.Saves.Rpa;

namespace Galbox.Core.Saves;

/// <summary>Discovers where a Ren'Py game keeps its saves.</summary>
public static partial class RenpySaveLocator
{
    /// <summary>The Ren'Py options file that carries <c>config.save_directory</c>.</summary>
    private const string OptionsScriptName = "options.rpy";

    /// <summary>Discovers game folders, the save-directory name and every save location.</summary>
    /// <param name="gameDirectory">Game root, the <c>game</c> subfolder, or any path inside it.</param>
    /// <param name="options">Analysis options (explicit directory overrides, extras).</param>
    public static RenpySaveDirectorySet Locate(string gameDirectory, RenpySaveAnalysisOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);
        options ??= RenpySaveAnalysisOptions.Default;

        var warnings = new List<string>();
        var (gameRoot, gameFolder) = NormalizeGameDirectories(gameDirectory, warnings);

        if (!Directory.Exists(gameFolder))
        {
            return new RenpySaveDirectorySet
            {
                GameFolder = gameFolder,
                GameRoot = gameRoot,
                Success = false,
                FailureReason = $"'{gameFolder}' does not exist.",
                Warnings = warnings,
            };
        }

        var (saveDirectoryName, source) = ReadSaveDirectoryName(gameRoot, gameFolder, warnings);

        var candidates = new List<RenpySaveLocationInfo>();

        if (!string.IsNullOrWhiteSpace(options.ExplicitSaveDirectory))
        {
            candidates.Add(Probe(RenpySaveLocationKind.Custom, options.ExplicitSaveDirectory!));
        }

        var userSaveDirectory = BuildUserSaveDirectory(saveDirectoryName);

        if (userSaveDirectory is not null)
        {
            candidates.Add(Probe(RenpySaveLocationKind.UserAppData, userSaveDirectory));
        }
        else if (string.IsNullOrWhiteSpace(options.ExplicitSaveDirectory))
        {
            // config.save_directory could not be read: fall back to a prefix match over
            // %APPDATA%\RenPy so a slightly different value never hides the saves entirely.
            foreach (var guess in GuessUserSaveDirectories(gameRoot))
            {
                warnings.Add($"config.save_directory was unavailable; probing '{guess}' by name similarity.");
                candidates.Add(Probe(RenpySaveLocationKind.UserAppData, guess));
            }
        }

        candidates.Add(Probe(RenpySaveLocationKind.GameLocal, System.IO.Path.Combine(gameFolder, "saves")));

        foreach (var extra in options.AdditionalSaveDirectories)
        {
            if (!string.IsNullOrWhiteSpace(extra))
            {
                candidates.Add(Probe(RenpySaveLocationKind.Custom, extra));
            }
        }

        // De-duplicate by full path, keeping the first (strongest) classification.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var locations = new List<RenpySaveLocationInfo>();

        foreach (var candidate in candidates)
        {
            if (seen.Add(candidate.Path))
            {
                locations.Add(candidate);
            }
        }

        var anySaves = locations.Any(l => l.Exists && l.SaveFileCount > 0);

        return new RenpySaveDirectorySet
        {
            GameFolder = gameFolder,
            GameRoot = gameRoot,
            SaveDirectoryName = saveDirectoryName,
            SaveDirectorySource = source,
            Success = anySaves,
            FailureReason = anySaves
                ? null
                : "No '.save' files were found in any candidate save directory.",
            Locations = locations,
            Warnings = warnings,
        };
    }

    /// <summary>
    /// Splits a user-supplied path into (game root, game folder). Accepts the install root, the
    /// <c>game</c> subfolder, or anything nested beneath them.
    /// </summary>
    public static (string GameRoot, string GameFolder) NormalizeGameDirectories(
        string path, List<string>? warnings = null)
    {
        var full = System.IO.Path.GetFullPath(path).TrimEnd(
            System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);

        // Walk up looking for the folder that directly contains a 'game' directory.
        var probe = full;

        for (var depth = 0; depth < 4 && !string.IsNullOrEmpty(probe); depth++)
        {
            var gameSub = System.IO.Path.Combine(probe, "game");

            if (Directory.Exists(gameSub))
            {
                return (probe, gameSub);
            }

            if (string.Equals(System.IO.Path.GetFileName(probe), "game", StringComparison.OrdinalIgnoreCase))
            {
                var parent = System.IO.Path.GetDirectoryName(probe);
                return (parent ?? probe, probe);
            }

            probe = System.IO.Path.GetDirectoryName(probe);
        }

        // Fall back: treat the supplied folder as the game folder itself.
        var guessRoot = System.IO.Path.GetDirectoryName(full) ?? full;
        warnings?.Add($"'{path}' does not look like a Ren'Py root; assuming it is the game folder.");
        return (guessRoot, full);
    }

    /// <summary><c>%APPDATA%\RenPy\&lt;save_directory&gt;</c>, or <see langword="null"/> when unknown.</summary>
    public static string? BuildUserSaveDirectory(string? saveDirectoryName)
    {
        if (string.IsNullOrWhiteSpace(saveDirectoryName))
        {
            return null;
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        return string.IsNullOrEmpty(appData)
            ? null
            : System.IO.Path.Combine(appData, "RenPy", saveDirectoryName!);
    }

    /// <summary>
    /// Reads <c>config.save_directory</c> from <c>options.rpy</c>, preferring the patch copy the
    /// way Ren'Py's search path does (<c>00patch</c> before <c>game</c>).
    /// </summary>
    public static (string? Value, string? Source) ReadSaveDirectoryName(
        string gameRoot, string gameFolder, List<string>? warnings = null)
    {
        // 1. Loose file next to the executable (some distributions ship scripts unpacked).
        var loose = System.IO.Path.Combine(gameFolder, OptionsScriptName);

        if (File.Exists(loose))
        {
            var text = SafeReadAllText(loose);

            if (text is not null && TryParseSaveDirectory(text, out var value))
            {
                return (value, loose);
            }
        }

        // 2. Archives, patch directory first.
        foreach (var archivePath in EnumerateArchives(gameRoot, gameFolder))
        {
            if (!File.Exists(archivePath))
            {
                continue;
            }

            try
            {
                using var archive = RpaArchive.Open(archivePath);
                var text = archive.ReadTextEntry(OptionsScriptName);

                if (text is null)
                {
                    continue;
                }

                if (TryParseSaveDirectory(text, out var value))
                {
                    var logical = LogicalSourceName(gameRoot, archivePath) + "/" + OptionsScriptName;
                    return (value, logical);
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or PickleScanException or IOException)
            {
                warnings?.Add($"Could not read '{OptionsScriptName}' from '{archivePath}': {ex.Message}");
            }
        }

        warnings?.Add(
            $"config.save_directory was not found; '{OptionsScriptName}.rpy' may be missing and only nested '.rpyc' files are present.");
        return (null, null);
    }

    /// <summary>Applies the Ren'Py script search order to the archives of this installation.</summary>
    public static IEnumerable<string> EnumerateArchives(string gameRoot, string gameFolder)
    {
        // Patch directory first: the report proves with three independent signals that the
        // '00patch' copy is the one the engine actually executes.
        var patchArchive = System.IO.Path.Combine(gameRoot, "00patch", "00patch.rpa");
        yield return patchArchive;

        foreach (var rpa in Directory.Exists(gameFolder)
                     ? Directory.EnumerateFiles(gameFolder, "*.rpa", SearchOption.TopDirectoryOnly)
                     : Enumerable.Empty<string>())
        {
            if (!string.Equals(rpa, patchArchive, StringComparison.OrdinalIgnoreCase))
            {
                yield return rpa;
            }
        }

        foreach (var rpa in Directory.Exists(gameRoot)
                     ? Directory.EnumerateFiles(gameRoot, "*.rpa", SearchOption.AllDirectories)
                     : Enumerable.Empty<string>())
        {
            if (!string.Equals(rpa, patchArchive, StringComparison.OrdinalIgnoreCase)
                && !rpa.StartsWith(gameFolder, StringComparison.OrdinalIgnoreCase))
            {
                yield return rpa;
            }
        }
    }

    /// <summary>Maps an archive path to the logical name Ren'Py uses in save state.</summary>
    public static string LogicalSourceName(string gameRoot, string archivePath)
    {
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(archivePath));
        var folderName = directory is null ? string.Empty : System.IO.Path.GetFileName(directory);

        return string.IsNullOrEmpty(folderName) ? "game" : folderName;
    }

    private static bool TryParseSaveDirectory(string text, out string value)
    {
        value = string.Empty;
        var match = SaveDirectoryRegex().Match(text);

        if (!match.Success)
        {
            return false;
        }

        value = match.Groups["value"].Value;
        return value.Length > 0;
    }

    private static string? SafeReadAllText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static RenpySaveLocationInfo Probe(RenpySaveLocationKind kind, string path)
    {
        var full = System.IO.Path.GetFullPath(path);

        if (!Directory.Exists(full))
        {
            return new RenpySaveLocationInfo
            {
                Kind = kind,
                Path = full,
                Exists = false,
            };
        }

        var fileNames = new List<string>();
        DateTimeOffset? newest = null;
        var hasPersistent = false;

        try
        {
            foreach (var file in Directory.EnumerateFiles(full))
            {
                var name = System.IO.Path.GetFileName(file)
                    ?? throw new IOException("Unexpected unnamed directory entry.");

                if (name.EndsWith(".save", StringComparison.OrdinalIgnoreCase))
                {
                    fileNames.Add(name);
                }
                else if (string.Equals(name, "persistent", StringComparison.OrdinalIgnoreCase))
                {
                    hasPersistent = true;
                }
                else
                {
                    continue;
                }

                var stamp = new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero);

                if (newest is null || stamp > newest)
                {
                    newest = stamp;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new RenpySaveLocationInfo
            {
                Kind = kind,
                Path = full,
                Exists = true,
                FailureReason = ex.Message,
            };
        }

        fileNames.Sort(StringComparer.OrdinalIgnoreCase);

        return new RenpySaveLocationInfo
        {
            Kind = kind,
            Path = full,
            Exists = true,
            SaveFileCount = fileNames.Count,
            HasPersistentFile = hasPersistent,
            NewestWriteTimeUtc = newest,
            FileNames = fileNames,
        };
    }

    private static IEnumerable<string> GuessUserSaveDirectories(string gameRoot)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        if (string.IsNullOrEmpty(appData))
        {
            yield break;
        }

        var renpyRoot = System.IO.Path.Combine(appData, "RenPy");

        if (!Directory.Exists(renpyRoot))
        {
            yield break;
        }

        var folderName = System.IO.Path.GetFileName(gameRoot);
        var needle = new string(folderName
            .ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '_')
            .ToArray());

        foreach (var dir in Directory.EnumerateDirectories(renpyRoot))
        {
            var name = System.IO.Path.GetFileName(dir).ToLowerInvariant();

            if (name.StartsWith(needle, StringComparison.Ordinal) && name.Length > needle.Length)
            {
                yield return dir;
            }
        }
    }

    [GeneratedRegex(
        @"config\.save_directory\s*=\s*(?:u?r?)([""'])(?<value>[^""']*)\1",
        RegexOptions.CultureInvariant)]
    private static partial Regex SaveDirectoryRegex();
}
