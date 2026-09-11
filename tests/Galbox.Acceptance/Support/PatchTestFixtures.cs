using System.Security.Cryptography;
using System.Text;
using System.IO.Compression;

namespace Galbox.Acceptance.Support;

/// <summary>
/// Synthetic game directories and patch packages for the patch-centre checks.
///
/// Everything is created under a scratch root inside <c>%TEMP%</c>. The real test game
/// (<c>D:\GAME\Dreamin'_Her</c>) is never opened for writing by any of this.
/// </summary>
public static class PatchTestFixtures
{
    /// <summary>Directory inside the game root that the engine owns (its ledger copy).</summary>
    public const string LedgerFolderName = ".galbox";

    /// <summary>Creates a fresh scratch directory.</summary>
    /// <remarks>
    /// Sibling directories older than a day are removed first, so repeated runs of the harness cannot
    /// grow the temp folder without bound. Only this harness's own root is touched.
    /// </remarks>
    public static string NewScratch(string tag)
    {
        var parent = Path.Combine(Path.GetTempPath(), "Galbox", "acceptance-patches");
        SweepOldScratch(parent);
        var root = Path.Combine(parent, $"{tag}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>
    /// A game id that is unique to this run.
    /// </summary>
    /// <remarks>
    /// The engine's ledger is keyed by game id and lives outside the game directory, so it survives a
    /// harness run. Reusing a fixed id made the second run grade a fresh fixture as "a file a previous
    /// Galbox install wrote" (provenance ManagedByGalbox) instead of "unknown origin", which changed
    /// the preview from a conflict to an overwrite. That is correct engine behaviour and a defect in
    /// the fixture, so the fixture has to be as unique as the account it borrows.
    /// </remarks>
    public static int UniqueGameId() => Random.Shared.Next(100_000_000, 2_000_000_000);

    private static void SweepOldScratch(string parent)
    {
        try
        {
            if (!Directory.Exists(parent)) return;
            foreach (var directory in Directory.EnumerateDirectories(parent))
            {
                if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(directory) < TimeSpan.FromDays(1)) continue;
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a scratch folder that cannot be removed is harmless.
        }
    }

    /// <summary>
    /// Creates a fake game install: an executable, a script file and an unrelated data file,
    /// plus a sub-directory, so the preview has both "overwrite" and "create" landing spots.
    /// </summary>
    public static string CreateGameRoot(string scratch, string name = "game")
    {
        var root = Path.Combine(scratch, name);
        Directory.CreateDirectory(Path.Combine(root, "data"));
        File.WriteAllBytes(Path.Combine(root, "game.exe"), Encoding.UTF8.GetBytes("FAKE-EXE-" + new string('x', 64)));
        File.WriteAllText(Path.Combine(root, "data", "original.txt"), "original script v1 - \u539f\u7248", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(root, "data", "keep.dat"), "untouched by every patch", new UTF8Encoding(false));
        return root;
    }

    /// <summary>Writes a zip with the given entries. Entry names are written verbatim (no normalisation).</summary>
    public static string CreateZip(string scratch, string fileName, params (string Name, string Content)[] entries)
    {
        Directory.CreateDirectory(scratch);
        var path = Path.Combine(scratch, fileName);
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (entryName, content) in entries)
        {
            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            using var entryStream = entry.Open();
            var bytes = Encoding.UTF8.GetBytes(content);
            entryStream.Write(bytes, 0, bytes.Length);
        }

        return path;
    }

    /// <summary>
    /// A well-behaved patch: it replaces <c>data/original.txt</c> and adds <c>data/newfile.txt</c>.
    /// </summary>
    public static string CreateOrdinaryPatch(string scratch, string fileName = "patch.zip")
        => CreateZip(
            scratch,
            fileName,
            ("data/original.txt", "patched script v2 - \u6c49\u5316"),
            ("data/newfile.txt", "added by the patch"));

    /// <summary>
    /// A hostile package: one Zip Slip entry (<c>..\escaped.txt</c>) next to one legitimate entry.
    /// The dangerous entry must never reach the disk, and the fact that it was refused must be visible.
    /// </summary>
    public static string CreateZipSlipPatch(string scratch, string fileName = "evil.zip")
        => CreateZip(
            scratch,
            fileName,
            ("data/newfile.txt", "added by the patch"),
            (@"..\escaped.txt", "THIS MUST NEVER BE WRITTEN OUTSIDE THE GAME DIRECTORY"));

    /// <summary>
    /// A save-data package: every entry matches the save sniff (a <c>save</c> folder plus <c>.sav</c>
    /// names), so the engine refuses the whole package before it can overwrite the player's progress.
    /// </summary>
    public static string CreateSaveLikePackage(string scratch, string fileName = "save-data.zip")
        => CreateZip(
            scratch,
            fileName,
            ("save/save01.sav", "SAVEDATA-1"),
            ("save/save02.sav", "SAVEDATA-2"),
            ("save/global.sav", "SAVEDATA-3"));

    /// <summary>
    /// A pre-install snapshot of every file under a game root, excluding Galbox's own ledger folder.
    /// </summary>
    /// <remarks>
    /// The <c>.galbox</c> folder is excluded on purpose: it is the engine's record of the install
    /// (and of the rollback), and it is supposed to survive a rollback. Every other file is compared
    /// by content, which is what "byte identical to the pre-install state" has to mean for the game.
    /// </remarks>
    public static SortedDictionary<string, string> SnapshotGameFiles(string gameRoot)
    {
        var snapshot = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(gameRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(gameRoot, file);
            if (relative.StartsWith(LedgerFolderName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            snapshot[relative] = HashFile(file);
        }

        return snapshot;
    }

    /// <summary>Files present under the game root that are <b>not</b> in the snapshot (used to report leftovers).</summary>
    public static List<string> ExtraFiles(string gameRoot, SortedDictionary<string, string> snapshot)
        => SnapshotGameFiles(gameRoot).Keys.Where(k => !snapshot.ContainsKey(k)).ToList();

    /// <summary>SHA-256 of a file, lower-case hex.</summary>
    public static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>Renders a snapshot as <c>rel=hash</c> lines, for the raw evidence block.</summary>
    public static IEnumerable<string> Describe(SortedDictionary<string, string> snapshot)
        => snapshot.Select(kv => $"{kv.Key} = {kv.Value[..16]}...");

    /// <summary>Difference between two snapshots, as human readable lines (empty when identical).</summary>
    public static List<string> Diff(
        SortedDictionary<string, string> before,
        SortedDictionary<string, string> after)
    {
        var lines = new List<string>();
        foreach (var (key, hash) in before)
        {
            if (!after.TryGetValue(key, out var afterHash)) lines.Add($"MISSING now : {key}");
            else if (!string.Equals(hash, afterHash, StringComparison.Ordinal)) lines.Add($"CHANGED     : {key} ({hash[..12]} -> {afterHash[..12]})");
        }

        foreach (var key in after.Keys)
        {
            if (!before.ContainsKey(key)) lines.Add($"NEW         : {key}");
        }

        return lines;
    }
}
