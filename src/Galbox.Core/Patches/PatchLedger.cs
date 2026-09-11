using System.Text;

namespace Galbox.Core.Patches;

/// <summary>
/// Reads and writes the install ledger.
/// <para>
/// The ledger lives in two places on purpose:
/// <list type="bullet">
/// <item><c>&lt;BackupRoot&gt;\&lt;gameId&gt;\&lt;installId&gt;\manifest.json</c> - the authoritative per-install journal.</item>
/// <item><c>&lt;gameRoot&gt;\.galbox\patch-manifest.json</c> - a bundle of every install for that game directory,
/// so a different machine (or a wiped database) still recognises the patch history.</item>
/// </list>
/// The engine only produces serialisable objects: persisting them into the database is the caller's job.
/// </para>
/// </summary>
public sealed class PatchLedger
{
    private readonly PatchInstallerOptions _options;

    /// <summary>Creates a ledger bound to the given options.</summary>
    public PatchLedger(PatchInstallerOptions options) => _options = options;

    /// <summary>Engine version recorded inside manifests.</summary>
    public static string EngineVersion => typeof(PatchLedger).Assembly.GetName().Version?.ToString() ?? "1.0.0";

    /// <summary>Root folder that holds every install of one game.</summary>
    public string GameFolder(string gameId) => Path.Combine(_options.BackupRoot, SafeGameId(gameId));

    /// <summary>Folder that holds one install (manifest + <c>files/</c> backups).</summary>
    public string InstallFolder(string gameId, string installId) => Path.Combine(GameFolder(gameId), installId);

    /// <summary>Folder that mirrors the relative path of every backed-up file.</summary>
    public string InstallFilesFolder(string gameId, string installId) => Path.Combine(InstallFolder(gameId, installId), "files");

    /// <summary>Path of the per-install manifest.</summary>
    public string ManifestPath(string gameId, string installId) => Path.Combine(InstallFolder(gameId, installId), _options.LedgerManifestFileName);

    /// <summary>Path of the in-game bundle copy.</summary>
    public string GameBundlePath(string gameRoot) => Path.Combine(gameRoot, _options.InGameManifestRelativePath);

    /// <summary>Enumerates every install recorded in the ledger folder, oldest first.</summary>
    public IReadOnlyList<PatchManifest> ReadInstallsFromLedger(string gameId)
    {
        var folder = GameFolder(gameId);
        if (!Directory.Exists(LongPath.Ensure(folder))) return Array.Empty<PatchManifest>();

        var result = new List<PatchManifest>();
        foreach (var dir in Directory.EnumerateDirectories(LongPath.Ensure(folder)))
        {
            var manifestPath = Path.Combine(dir, _options.LedgerManifestFileName);
            if (!File.Exists(LongPath.Ensure(manifestPath))) continue;
            var manifest = TryReadManifest(manifestPath);
            if (manifest is not null) result.Add(manifest);
        }
        return result.OrderBy(m => m.InstallId, StringComparer.Ordinal).ToList();
    }

    /// <summary>Reads the in-game bundle (used to recognise installs after a database or machine change).</summary>
    public PatchGameManifestBundle? ReadGameBundle(string gameRoot)
    {
        var path = GameBundlePath(gameRoot);
        if (!File.Exists(LongPath.Ensure(path))) return null;
        try
        {
            var json = File.ReadAllText(LongPath.Ensure(path), Encoding.UTF8);
            return PatchJson.Deserialize<PatchGameManifestBundle>(json);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// All installs known for a game, merged from the ledger folder and the in-game bundle.
    /// The ledger entry wins when both describe the same install id.
    /// </summary>
    public IReadOnlyList<PatchManifest> ReadAllInstalls(string gameRoot, string gameId)
    {
        var byId = new Dictionary<string, PatchManifest>(StringComparer.Ordinal);
        var bundle = ReadGameBundle(gameRoot);
        if (bundle is not null)
        {
            foreach (var manifest in bundle.Installs)
            {
                if (!string.IsNullOrEmpty(manifest.InstallId)) byId[manifest.InstallId] = manifest;
            }
        }
        foreach (var manifest in ReadInstallsFromLedger(gameId))
        {
            byId[manifest.InstallId] = manifest;
        }
        return byId.Values.OrderBy(m => m.InstallId, StringComparer.Ordinal).ToList();
    }

    /// <summary>Reads a single install.</summary>
    public PatchManifest? ReadInstall(string gameRoot, string gameId, string installId)
    {
        var path = ManifestPath(gameId, installId);
        if (File.Exists(LongPath.Ensure(path)))
        {
            var manifest = TryReadManifest(path);
            if (manifest is not null) return manifest;
        }
        var bundle = ReadGameBundle(gameRoot);
        return bundle?.Installs.FirstOrDefault(m => string.Equals(m.InstallId, installId, StringComparison.Ordinal));
    }

    /// <summary>The most recent install that is still "active" (committed, pending or failed).</summary>
    public PatchManifest? ReadLatestActive(string gameRoot, string gameId)
    {
        var active = ReadAllInstalls(gameRoot, gameId).Where(m => m.IsActive).ToList();
        return active.Count == 0 ? null : active[^1];
    }

    /// <summary>The pending install, i.e. the one that was interrupted, if any.</summary>
    public IReadOnlyList<PatchManifest> ReadPending(string gameRoot, string gameId)
        => ReadAllInstalls(gameRoot, gameId)
            .Where(m => m.Status == PatchManifestStatus.Pending || m.Status == PatchManifestStatus.Failed)
            .ToList();

    /// <summary>
    /// Writes the manifest to the ledger folder, then upserts it into the in-game bundle.
    /// Returns the ledger path.
    /// </summary>
    public async Task<string> WriteManifestAsync(PatchManifest manifest, CancellationToken ct = default)
    {
        var folder = InstallFolder(manifest.GameId, manifest.InstallId);
        Directory.CreateDirectory(LongPath.Ensure(folder));

        var path = ManifestPath(manifest.GameId, manifest.InstallId);
        await WriteJsonAtomicAsync(path, PatchJson.Serialize(manifest), ct).ConfigureAwait(false);

        await UpsertGameBundleAsync(manifest, ct).ConfigureAwait(false);
        return path;
    }

    /// <summary>Inserts or replaces the install inside <c>&lt;gameRoot&gt;\.galbox\patch-manifest.json</c>.</summary>
    public async Task<string> UpsertGameBundleAsync(PatchManifest manifest, CancellationToken ct = default)
    {
        var path = GameBundlePath(manifest.GameRoot);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(LongPath.Ensure(directory));

        var bundle = ReadGameBundle(manifest.GameRoot) ?? new PatchGameManifestBundle
        {
            GameRoot = manifest.GameRoot,
            GameId = manifest.GameId
        };

        bundle.Installs.RemoveAll(m => string.Equals(m.InstallId, manifest.InstallId, StringComparison.Ordinal));
        bundle.Installs.Add(manifest);
        bundle.Installs.Sort((a, b) => string.CompareOrdinal(a.InstallId, b.InstallId));

        var refreshed = new PatchGameManifestBundle
        {
            GameRoot = bundle.GameRoot,
            GameId = bundle.GameId,
            UpdatedAt = DateTimeOffset.UtcNow,
            Installs = bundle.Installs
        };

        await WriteJsonAtomicAsync(path, PatchJson.Serialize(refreshed), ct).ConfigureAwait(false);
        return path;
    }

    /// <summary>Reads a manifest file, tolerating a truncated write (an interrupted install can leave a partial file).</summary>
    public static PatchManifest? TryReadManifest(string path)
    {
        try
        {
            return PatchJson.Deserialize<PatchManifest>(File.ReadAllText(LongPath.Ensure(path), Encoding.UTF8));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Generates a time-sortable install id: the ordering is what expresses the patch chain.</summary>
    public static string NewInstallId()
    {
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'");
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return $"{stamp}-{suffix}";
    }

    /// <summary>Game ids come from the caller and may contain characters Windows rejects.</summary>
    public static string SafeGameId(string gameId)
    {
        if (string.IsNullOrWhiteSpace(gameId)) return "_unnamed";
        var builder = new StringBuilder(gameId.Length);
        foreach (var ch in gameId)
        {
            builder.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), ch) >= 0 || ch == '\\' || ch == '/' ? '_' : ch);
        }
        var safe = builder.ToString().Trim().TrimEnd('.');
        return string.IsNullOrEmpty(safe) || safe == ".." ? "_unnamed" : safe;
    }

    private static async Task WriteJsonAtomicAsync(string path, string json, CancellationToken ct)
    {
        var extended = LongPath.Ensure(path);
        var temp = extended + ".tmp";
        await File.WriteAllTextAsync(temp, json, new UTF8Encoding(false), ct).ConfigureAwait(false);
        File.Move(temp, extended, overwrite: true);
    }
}

/// <summary>One install that could have its backup folder reclaimed.</summary>
public sealed class PatchRetentionCandidate
{
    /// <summary>Install identifier.</summary>
    public required string InstallId { get; init; }

    /// <summary>Patch name, for a readable confirmation dialog.</summary>
    public string? PatchName { get; init; }

    /// <summary>When the install happened.</summary>
    public DateTimeOffset? InstalledAt { get; init; }

    /// <summary>Bytes currently used by the backup folder.</summary>
    public long BackupBytes { get; init; }

    /// <summary>Manifest status.</summary>
    public required PatchManifestStatus Status { get; init; }

    /// <summary>Why this install is protected from deletion.</summary>
    public string? ProtectedReason { get; init; }

    /// <summary>True when the install must not be deleted.</summary>
    public bool IsProtected => !string.IsNullOrEmpty(ProtectedReason);
}

/// <summary>
/// What the retention policy recommends. Nothing is deleted by the engine on its own: deleting the
/// newest backup of a file can destroy the only copy of an original game asset.
/// </summary>
public sealed class PatchRetentionPlan
{
    /// <summary>Game the plan is about.</summary>
    public required string GameId { get; init; }

    /// <summary>How many installs are kept.</summary>
    public int KeepCount { get; init; }

    /// <summary>Installs that stay.</summary>
    public IReadOnlyList<PatchRetentionCandidate> Kept { get; init; } = Array.Empty<PatchRetentionCandidate>();

    /// <summary>Installs the user may delete.</summary>
    public IReadOnlyList<PatchRetentionCandidate> Reclaimable { get; init; } = Array.Empty<PatchRetentionCandidate>();

    /// <summary>Bytes that would be freed.</summary>
    public long ReclaimableBytes => Reclaimable.Sum(c => c.BackupBytes);

    /// <summary>Total bytes the game currently occupies in the backup root.</summary>
    public long TotalBackupBytes => Kept.Sum(c => c.BackupBytes) + ReclaimableBytes;

    /// <summary>Explanation shown to the user; the engine never deletes silently.</summary>
    public required string Recommendation { get; init; }
}
