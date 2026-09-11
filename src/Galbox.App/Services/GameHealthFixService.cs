using Galbox.App.Models;
using Galbox.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Galbox.App.Services;

/// <inheritdoc cref="IGameHealthFixService" />
public class GameHealthFixService : IGameHealthFixService
{
    /// <summary>
    /// Compatibility layer written for XP/Vista-era games: the mode the diagnosis itself recommends.
    /// </summary>
    private const string DefaultCompatibilityMode = "WIN7RTM";

    /// <summary>Category of the compatibility-layer journal entries.</summary>
    private const string JournalKindRename = "RenameInstallPath";

    /// <summary>Category of the folder-rename journal entries.</summary>
    private const string JournalKindCompatibility = "WindowsCompatibility";

    private readonly IDbContextFactory<GalboxDbContext> _dbContextFactory;
    private readonly IProcessMonitorService _processMonitor;
    private readonly ILogger<GameHealthFixService> _logger;
    private readonly GameHealthFixJournal _journal;

    /// <summary>
    /// Creates the service with the production undo-journal location
    /// (<c>%LocalAppData%\Galbox\health-fixes</c>).
    /// </summary>
    public GameHealthFixService(
        IDbContextFactory<GalboxDbContext> dbContextFactory,
        IProcessMonitorService processMonitor,
        ILogger<GameHealthFixService> logger)
        : this(dbContextFactory, processMonitor, logger, null)
    {
    }

    /// <summary>
    /// Creates the service with an explicit undo-journal directory. Used by the acceptance harness so
    /// a test run cannot read or write the real application's journal.
    /// </summary>
    public GameHealthFixService(
        IDbContextFactory<GalboxDbContext> dbContextFactory,
        IProcessMonitorService processMonitor,
        ILogger<GameHealthFixService> logger,
        string? journalDirectory)
    {
        _dbContextFactory = dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
        _processMonitor = processMonitor ?? throw new ArgumentNullException(nameof(processMonitor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var directory = string.IsNullOrWhiteSpace(journalDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Galbox",
                "health-fixes")
            : journalDirectory;

        _journal = new GameHealthFixJournal(directory);
    }

    /// <inheritdoc />
    public bool CanAutoFix(ErrorCategory category) =>
        category is ErrorCategory.ChineseDirectory or ErrorCategory.WindowsCompatibility;

    /// <inheritdoc />
    public bool IsCompatibilityModeApplied(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        var value = ReadCompatibilityLayer(executablePath.Trim());
        return !string.IsNullOrWhiteSpace(value);
    }

    // =============================================================================================
    // 中文路径 → 一键重命名
    // =============================================================================================

    /// <inheritdoc />
    public async Task<GameHealthFixResult> FixChineseInstallPathAsync(
        int gameId,
        CancellationToken cancellationToken = default)
    {
        var result = new GameHealthFixResult
        {
            Kind = GameHealthFixKind.RenameInstallPath,
            GameId = gameId
        };

        // ---- 1. read the game (a snapshot; the authoritative check happens at commit time) --------
        var game = await LoadGameAsync(gameId, cancellationToken).ConfigureAwait(false);
        if (game is null)
        {
            return GameHealthFixResult.Failure($"找不到 Id 为 {gameId} 的游戏记录，无法修复。");
        }

        if (string.IsNullOrWhiteSpace(game.InstallPath))
        {
            return GameHealthFixResult.Failure("该游戏没有记录安装路径，无法判断是否含中文。请先重新检测安装目录。");
        }

        var installPath = game.InstallPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var (leaf, ancestorsWithChinese) = GamePathNaming.AnalysePath(installPath);

        if (leaf is null)
        {
            return ancestorsWithChinese.Count > 0
                ? GameHealthFixResult.Failure(
                    $"中文出现在上层目录（{string.Join(" / ", ancestorsWithChinese)}），重命名游戏目录本身解决不了这个问题。"
                    + "请把游戏移动到纯英文路径（例如 D:\\Games\\）后重新扫描。")
                : GameHealthFixResult.Failure($"安装路径已经是纯 ASCII（{installPath}），不需要修复。");
        }

        if (!Directory.Exists(installPath))
        {
            return GameHealthFixResult.Failure($"安装目录不存在：{installPath}。请先在库里修正路径或移除该游戏。");
        }

        // ---- 2. the game must not be running: renaming a running game corrupts its saves ---------
        if (IsGameRunning(game, out var runningName))
        {
            return GameHealthFixResult.Failure(
                $"检测到游戏进程 {runningName} 正在运行，已取消重命名。请先退出游戏再修复，"
                + "中途改名会让存档路径失效。");
        }

        // ---- 3. compose and validate the target path --------------------------------------------
        var parent = Path.GetDirectoryName(installPath);
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
        {
            return GameHealthFixResult.Failure($"无法确定上级目录（{parent}），已取消。");
        }

        var suggestion = GamePathNaming.SuggestAsciiName(leaf);
        var targetPath = Path.Combine(parent, suggestion.Name);

        result.Notes.Add($"目标目录名：\"{leaf}\" → \"{suggestion.Name}\"");
        result.Notes.AddRange(suggestion.Notes);

        if (string.Equals(targetPath, installPath, StringComparison.OrdinalIgnoreCase))
        {
            return GameHealthFixResult.Failure("转写后的目录名与原名相同，已取消。");
        }

        if (Directory.Exists(targetPath))
        {
            return GameHealthFixResult.Failure(
                $"目标目录已存在：{targetPath}。为避免覆盖另一个游戏，本工具不会动它；"
                + "请先把那个目录改名或移走，再重新修复。");
        }

        if (File.Exists(targetPath))
        {
            return GameHealthFixResult.Failure($"目标位置已存在同名文件：{targetPath}，已取消。");
        }

        result.OldPath = installPath;
        result.NewPath = targetPath;

        // ---- 4. rename the folder ----------------------------------------------------------------
        try
        {
            Directory.Move(installPath, targetPath);
            result.Notes.Add($"已重命名目录：{installPath} → {targetPath}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Renaming {Old} to {New} failed", installPath, targetPath);
            return GameHealthFixResult.Failure($"重命名目录失败：{ex.Message}");
        }

        // ---- 5. keep the database in sync; roll the rename back if that is impossible ------------
        try
        {
            var commit = await CommitRelocationAsync(gameId, installPath, targetPath, cancellationToken)
                .ConfigureAwait(false);

            result.UpdatedFields.AddRange(commit.UpdatedFields);
            result.Notes.Add($"数据库已同步（更新 {commit.UpdatedFields.Count} 个路径字段，"
                           + $"{commit.ChildRowsUpdated} 条关联记录）。");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Committing the rename of game {GameId} failed; rolling the folder back", gameId);

            var rollbackError = TryMoveDirectoryBack(targetPath, installPath);
            result.RolledBack = rollbackError is null;
            result.Notes.Add(rollbackError is null
                ? $"数据库更新失败，已把目录改回原名：{installPath}"
                : $"数据库更新失败，且回滚也失败（{rollbackError}）。目录现在位于 {targetPath}，请手工改回。");

            result.Message = rollbackError is null
                ? $"数据库更新失败（{ex.Message}），目录已回滚到原状，未做任何修改。"
                : $"数据库更新失败（{ex.Message}），且目录回滚失败（{rollbackError}），请手工处理。";

            return result;
        }

        // ---- 6. keep compatibility-layer settings pointing at the executable ---------------------
        var layerWarning = MigrateCompatibilityLayers(installPath, targetPath, result.Notes);
        if (layerWarning is not null)
        {
            result.Notes.Add(layerWarning);
        }

        // ---- 7. record the undo ----------------------------------------------------------------
        var journalWarning = _journal.Record(new HealthFixJournalEntry
        {
            Kind = JournalKindRename,
            GameId = gameId,
            OldPath = installPath,
            NewPath = targetPath,
            AppliedTimeUtc = DateTime.UtcNow
        });

        if (journalWarning is not null)
        {
            result.Notes.Add(journalWarning);
        }

        result.Success = true;
        result.CanUndo = journalWarning is null;
        result.Message = $"已把游戏目录改名为「{suggestion.Name}」，数据库路径已同步。";
        _logger.LogInformation("Renamed game {GameId} install path: {Old} -> {New}", gameId, installPath, targetPath);
        return result;
    }

    /// <inheritdoc />
    public async Task<GameHealthFixResult> RevertChineseInstallPathAsync(
        int gameId,
        CancellationToken cancellationToken = default)
    {
        var result = new GameHealthFixResult
        {
            Kind = GameHealthFixKind.RenameInstallPath,
            GameId = gameId
        };

        var entry = _journal.FindLatest(candidate =>
            string.Equals(candidate.Kind, JournalKindRename, StringComparison.Ordinal) && candidate.GameId == gameId);

        if (entry?.OldPath is null || entry.NewPath is null)
        {
            return GameHealthFixResult.Failure(
                "没有找到该游戏的改名记录，无法自动还原。如果确实需要改回中文目录，请手工重命名后在库里修正路径。");
        }

        var game = await LoadGameAsync(gameId, cancellationToken).ConfigureAwait(false);
        if (game is null)
        {
            return GameHealthFixResult.Failure($"找不到 Id 为 {gameId} 的游戏记录，无法还原。");
        }

        if (IsGameRunning(game, out var runningName))
        {
            return GameHealthFixResult.Failure($"检测到游戏进程 {runningName} 正在运行，已取消还原。");
        }

        if (!Directory.Exists(entry.NewPath))
        {
            return GameHealthFixResult.Failure($"当前目录不存在（{entry.NewPath}），无法还原，请先修复路径。");
        }

        if (Directory.Exists(entry.OldPath) || File.Exists(entry.OldPath))
        {
            return GameHealthFixResult.Failure($"原目录名已被占用（{entry.OldPath}），已取消还原，请先处理该目录。");
        }

        result.OldPath = entry.NewPath;
        result.NewPath = entry.OldPath;

        try
        {
            Directory.Move(entry.NewPath, entry.OldPath);
            result.Notes.Add($"已把目录改回：{entry.NewPath} → {entry.OldPath}");
        }
        catch (Exception ex)
        {
            return GameHealthFixResult.Failure($"还原目录名失败：{ex.Message}");
        }

        try
        {
            var commit = await CommitRelocationAsync(gameId, entry.NewPath, entry.OldPath, cancellationToken)
                .ConfigureAwait(false);
            result.UpdatedFields.AddRange(commit.UpdatedFields);
        }
        catch (Exception ex)
        {
            var rollbackError = TryMoveDirectoryBack(entry.OldPath, entry.NewPath);
            result.RolledBack = rollbackError is null;
            result.Message = rollbackError is null
                ? $"数据库更新失败（{ex.Message}），目录已回滚到原状。"
                : $"数据库更新失败（{ex.Message}），且目录回滚失败（{rollbackError}）。";
            return result;
        }

        MigrateCompatibilityLayers(entry.OldPath, entry.NewPath, result.Notes);
        var journalWarning = _journal.Remove(candidate =>
            string.Equals(candidate.Kind, JournalKindRename, StringComparison.Ordinal) && candidate.GameId == gameId);

        if (journalWarning is not null)
        {
            result.Notes.Add(journalWarning);
        }

        result.Success = true;
        result.CanUndo = false;
        result.Message = $"已把游戏目录改回「{Path.GetFileName(entry.OldPath)}」。";
        return result;
    }

    // =============================================================================================
    // Windows 兼容性 → 写入 HKCU 兼容性层
    // =============================================================================================

    /// <inheritdoc />
    public Task<GameHealthFixResult> ApplyWindowsCompatibilityModeAsync(
        int gameId,
        CancellationToken cancellationToken = default) =>
        ApplyWindowsCompatibilityModeAsync(gameId, DefaultCompatibilityMode, cancellationToken);

    /// <inheritdoc />
    public async Task<GameHealthFixResult> ApplyWindowsCompatibilityModeAsync(
        int gameId,
        string compatibilityMode,
        CancellationToken cancellationToken = default)
    {
        var result = new GameHealthFixResult
        {
            Kind = GameHealthFixKind.WindowsCompatibility,
            GameId = gameId
        };

        var game = await LoadGameAsync(gameId, cancellationToken).ConfigureAwait(false);
        if (game is null)
        {
            return GameHealthFixResult.Failure($"找不到 Id 为 {gameId} 的游戏记录，无法设置兼容模式。");
        }

        var executable = game.MainExecutable;
        if (string.IsNullOrWhiteSpace(executable))
        {
            return GameHealthFixResult.Failure("该游戏没有记录主程序路径，无法设置兼容模式。请先在库里设置主程序。");
        }

        executable = executable.Trim();

        if (!File.Exists(executable))
        {
            return GameHealthFixResult.Failure($"主程序不存在：{executable}，无法设置兼容模式。");
        }

        var layer = NormalizeCompatibilityLayer(compatibilityMode);
        var previous = ReadCompatibilityLayer(executable);

        try
        {
            WriteCompatibilityLayer(executable, layer);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Writing the compatibility layer for {Executable} failed", executable);
            return GameHealthFixResult.Failure($"写入兼容模式失败：{ex.Message}");
        }

        // Read back: a value written into a virtualised or redirected hive would otherwise be
        // reported as a success that Windows never sees.
        var written = ReadCompatibilityLayer(executable);
        if (!string.Equals(written, layer, StringComparison.Ordinal))
        {
            return GameHealthFixResult.Failure(
                $"写入后读回的值不对（期望 \"{layer}\"，实测 \"{written ?? "(空)"}\"），已取消。");
        }

        result.AppliedValue = layer;
        result.PreviousValue = previous;
        result.OldPath = executable;
        result.Notes.Add($"已写入 HKCU 兼容性层：{executable} = {layer}");
        if (previous is not null)
        {
            result.Notes.Add($"原有设置 \"{previous}\" 已覆盖，撤销时会还原。");
        }

        var journalWarning = _journal.Record(new HealthFixJournalEntry
        {
            Kind = JournalKindCompatibility,
            GameId = gameId,
            ExecutablePath = executable,
            AppliedValue = layer,
            PreviousValue = previous,
            AppliedTimeUtc = DateTime.UtcNow
        });

        if (journalWarning is not null)
        {
            result.Notes.Add(journalWarning);
        }

        result.Success = true;
        result.CanUndo = journalWarning is null;
        result.Message = previous is null
            ? $"已为「{Path.GetFileName(executable)}」设置兼容模式 {layer}。"
            : $"已为「{Path.GetFileName(executable)}」设置兼容模式 {layer}（原设置 {previous} 会在撤销时还原）。";
        _logger.LogInformation("Compatibility layer {Layer} applied to {Executable}", layer, executable);
        return result;
    }

    /// <inheritdoc />
    public async Task<GameHealthFixResult> RevertWindowsCompatibilityModeAsync(
        int gameId,
        CancellationToken cancellationToken = default)
    {
        var result = new GameHealthFixResult
        {
            Kind = GameHealthFixKind.WindowsCompatibility,
            GameId = gameId
        };

        var entry = _journal.FindLatest(candidate =>
            string.Equals(candidate.Kind, JournalKindCompatibility, StringComparison.Ordinal) && candidate.GameId == gameId);

        string? executable = entry?.ExecutablePath;
        string? previous = entry?.PreviousValue;

        if (executable is null)
        {
            // No journal: fall back to the game's own executable, but only remove a value this
            // application would have written. Never delete a setting the user made by hand.
            var game = await LoadGameAsync(gameId, cancellationToken).ConfigureAwait(false);
            executable = game?.MainExecutable;
            if (string.IsNullOrWhiteSpace(executable))
            {
                return GameHealthFixResult.Failure("没有找到该游戏的兼容模式记录，且游戏没有主程序路径，无法撤销。");
            }

            var current = ReadCompatibilityLayer(executable);
            if (current is null)
            {
                return GameHealthFixResult.Failure("该游戏的兼容模式设置不存在，无需撤销。");
            }

            if (!string.Equals(current, NormalizeCompatibilityLayer(DefaultCompatibilityMode), StringComparison.Ordinal))
            {
                return GameHealthFixResult.Failure(
                    $"当前兼容模式为 \"{current}\"，不是本工具写入的，已保留不动。如需删除请在注册表编辑器中处理。");
            }

            previous = null;
        }

        var existing = ReadCompatibilityLayer(executable);

        try
        {
            if (previous is null)
            {
                DeleteCompatibilityLayer(executable);
                result.Notes.Add($"已删除 HKCU 兼容性层设置：{executable}");
            }
            else
            {
                WriteCompatibilityLayer(executable, previous);
                result.Notes.Add($"已把 HKCU 兼容性层还原为：{executable} = {previous}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reverting the compatibility layer for {Executable} failed", executable);
            return GameHealthFixResult.Failure($"撤销兼容模式失败：{ex.Message}");
        }

        var after = ReadCompatibilityLayer(executable);

        if (previous is null)
        {
            if (after is not null)
            {
                return GameHealthFixResult.Failure($"撤销后注册表值仍存在（\"{after}\"），撤销未生效。");
            }
        }
        else if (!string.Equals(after, previous, StringComparison.Ordinal))
        {
            return GameHealthFixResult.Failure($"撤销后读回的值不对（期望 \"{previous}\"，实测 \"{after ?? "(空)"}\"）。");
        }

        var journalWarning = _journal.Remove(candidate =>
            string.Equals(candidate.Kind, JournalKindCompatibility, StringComparison.Ordinal) && candidate.GameId == gameId);

        if (journalWarning is not null)
        {
            result.Notes.Add(journalWarning);
        }

        result.Success = true;
        result.CanUndo = false;
        result.AppliedValue = existing;
        result.PreviousValue = previous;
        result.OldPath = executable;
        result.Message = previous is null
            ? "已撤销兼容模式设置。"
            : $"已还原为原来的兼容模式设置 {previous}。";
        return result;
    }

    // =============================================================================================
    // Helpers
    // =============================================================================================

    /// <summary>
    /// Writes the relocated paths for a game in one transaction, and refuses the commit when the
    /// target path is already claimed by another game row.
    /// </summary>
    /// <remarks>
    /// The conflict check deliberately lives HERE, at commit time, and not in the pre-flight
    /// validation: between reading the game and committing the rename another row could appear, and
    /// only the fresh row read inside this method is authoritative. A refusal thrown from here is what
    /// triggers the caller's rollback of the already-performed folder rename.
    /// </remarks>
    private async Task<(List<string> UpdatedFields, int ChildRowsUpdated)> CommitRelocationAsync(
        int gameId,
        string oldRoot,
        string newRoot,
        CancellationToken cancellationToken)
    {
        await using var db = _dbContextFactory.CreateDbContext();

        var game = await db.Games.FirstOrDefaultAsync(g => g.Id == gameId, cancellationToken).ConfigureAwait(false)
                   ?? throw new InvalidOperationException("游戏记录在修复过程中被删除。");

        if (!PathsAreEqual(game.InstallPath, oldRoot))
        {
            throw new InvalidOperationException(
                $"游戏记录的安装路径已被其他操作改成 {game.InstallPath}，本次修复不再适用。");
        }

        var normalizedNewRoot = newRoot.TrimEnd(Path.DirectorySeparatorChar);

        // A library-wide scan of the stored install paths, compared the way Windows compares paths.
        // SQLite compares TEXT with a case-sensitive "=", which would let "D:\Game" and "d:\game"
        // through as two different rows even though they are the same folder.
        var otherGames = await db.Games
            .Where(other => other.Id != gameId)
            .Select(other => new { other.Id, other.NameOriginal, other.InstallPath })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var conflicting = otherGames.FirstOrDefault(other => PathsAreEqual(other.InstallPath, normalizedNewRoot));

        if (conflicting is not null)
        {
            throw new InvalidOperationException(
                $"另一个游戏记录（Id={conflicting.Id}「{conflicting.NameOriginal}」）已经声明了目标路径 {normalizedNewRoot}，"
                + "提交会制造两条指向同一目录的记录。");
        }

        var updatedFields = new List<string>();
        var childRows = 0;

        game.InstallPath = normalizedNewRoot;
        updatedFields.Add("Games.InstallPath");

        game.MainExecutable = Rebase(game.MainExecutable, oldRoot, newRoot);
        updatedFields.Add("Games.MainExecutable");

        if (!string.IsNullOrWhiteSpace(game.AlternativeExecutables))
        {
            game.AlternativeExecutables = RebaseList(game.AlternativeExecutables!, oldRoot, newRoot);
            updatedFields.Add("Games.AlternativeExecutables");
        }

        if (!string.IsNullOrWhiteSpace(game.CoverImagePath))
        {
            game.CoverImagePath = Rebase(game.CoverImagePath!, oldRoot, newRoot);
            updatedFields.Add("Games.CoverImagePath");
        }

        if (!string.IsNullOrWhiteSpace(game.BackgroundImagePath))
        {
            game.BackgroundImagePath = Rebase(game.BackgroundImagePath!, oldRoot, newRoot);
            updatedFields.Add("Games.BackgroundImagePath");
        }

        game.UpdatedTime = DateTime.UtcNow;

        // Child tables can point inside the game folder as well (scanned documents, media, screenshots,
        // save backups and save nodes). Leaving them behind would produce dead links in the detail page.
        foreach (var document in await db.Set<GameDocument>()
                     .Where(row => row.GameInfoId == gameId).ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            var rebased = Rebase(document.FilePath, oldRoot, newRoot);
            if (rebased != document.FilePath)
            {
                document.FilePath = rebased;
                childRows++;
            }
        }

        foreach (var media in await db.Set<GameMediaFile>()
                     .Where(row => row.GameInfoId == gameId).ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            var rebased = Rebase(media.FilePath, oldRoot, newRoot);
            if (rebased != media.FilePath)
            {
                media.FilePath = rebased;
                childRows++;
            }
        }

        foreach (var screenshot in await db.Set<GameScreenshot>()
                     .Where(row => row.GameInfoId == gameId).ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            var rebased = Rebase(screenshot.FilePath, oldRoot, newRoot);
            if (rebased != screenshot.FilePath)
            {
                screenshot.FilePath = rebased;
                childRows++;
            }
        }

        foreach (var backup in await db.SaveBackups
                     .Where(row => row.GameInfoId == gameId).ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!string.IsNullOrWhiteSpace(backup.OriginalSavePath))
            {
                var rebasedOriginal = Rebase(backup.OriginalSavePath!, oldRoot, newRoot);
                if (rebasedOriginal != backup.OriginalSavePath)
                {
                    backup.OriginalSavePath = rebasedOriginal;
                    childRows++;
                }
            }

            if (!string.IsNullOrWhiteSpace(backup.BackupPath))
            {
                var rebasedBackup = Rebase(backup.BackupPath!, oldRoot, newRoot);
                if (rebasedBackup != backup.BackupPath)
                {
                    backup.BackupPath = rebasedBackup;
                    childRows++;
                }
            }
        }

        foreach (var node in await db.SaveNodes
                     .Where(row => row.GameInfoId == gameId).ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(node.SaveFilePath))
            {
                continue;
            }

            var rebased = Rebase(node.SaveFilePath!, oldRoot, newRoot);
            if (rebased != node.SaveFilePath)
            {
                node.SaveFilePath = rebased;
                childRows++;
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return (updatedFields, childRows);
    }

    /// <summary>
    /// Rewrites a stored path when it points inside <paramref name="oldRoot"/>. Paths outside the
    /// renamed folder are returned unchanged: they belong to another game, to the user's documents,
    /// or to the application's own image cache.
    /// </summary>
    private static string Rebase(string path, string oldRoot, string newRoot)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        var trimmedPath = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var trimmedOld = oldRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var trimmedNew = newRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.Equals(trimmedPath, trimmedOld, StringComparison.OrdinalIgnoreCase))
        {
            return newRoot;
        }

        var prefix = trimmedOld + Path.DirectorySeparatorChar;
        if (trimmedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return trimmedNew + Path.DirectorySeparatorChar + trimmedPath[prefix.Length..];
        }

        return path;
    }

    /// <summary>Compares two stored paths the way Windows does: ignoring case, ignoring a trailing separator.</summary>
    private static bool PathsAreEqual(string? left, string? right)
    {
        if (left is null || right is null)
        {
            return false;
        }

        return string.Equals(
            left.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            right.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Rebases every entry of the pipe-separated alternative-executable list.</summary>
    private static string RebaseList(string list, string oldRoot, string newRoot)
    {
        var parts = list.Split('|', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => Rebase(part, oldRoot, newRoot));

        return string.Join('|', parts);
    }

    /// <summary>
    /// Moves compatibility-layer values from the old executable path to the new one.
    /// </summary>
    /// <remarks>
    /// A layer is keyed by the executable's full path. Without this step, renaming the folder would
    /// silently disarm a compatibility mode the user had set up - the repair would undo an earlier
    /// repair. Only values that live under the renamed folder are touched.
    /// </remarks>
    private string? MigrateCompatibilityLayers(string oldRoot, string newRoot, List<string> notes)
    {
        try
        {
            lock (RegistryGate)
            {
                using var key = Registry.CurrentUser.OpenSubKey(CompatibilityLayersKey, writable: true);
                if (key is null)
                {
                    return null;
                }

                var moved = 0;
                foreach (var valueName in key.GetValueNames())
                {
                    var rebased = Rebase(valueName, oldRoot, newRoot);
                    if (string.Equals(rebased, valueName, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var value = key.GetValue(valueName);
                    key.DeleteValue(valueName, throwOnMissingValue: false);
                    if (value is not null)
                    {
                        key.SetValue(rebased, value, RegistryValueKind.String);
                    }

                    moved++;
                }

                if (moved > 0)
                {
                    notes.Add($"已迁移 {moved} 条兼容模式设置到新的可执行文件路径。");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Migrating compatibility layers from {Old} to {New} failed", oldRoot, newRoot);
            return $"兼容模式设置迁移失败（{ex.Message}），如果之前为这个游戏设过兼容模式，请重新设置一次。";
        }

        return null;
    }

    /// <summary>True when the game looks like it is running right now.</summary>
    private bool IsGameRunning(GameInfo game, out string? runningName)
    {
        runningName = null;

        var state = _processMonitor.GetProcessState(game.Id);
        if (state?.State == ProcessState.Running)
        {
            runningName = state.ProcessName ?? game.DisplayName;
            return true;
        }

        if (string.IsNullOrWhiteSpace(game.MainExecutable))
        {
            return false;
        }

        string processName;
        try
        {
            processName = Path.GetFileNameWithoutExtension(game.MainExecutable);
        }
        catch (Exception)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(processName) || !_processMonitor.IsProcessRunning(processName))
        {
            return false;
        }

        runningName = processName;
        return true;
    }

    private static string? TryMoveDirectoryBack(string from, string to)
    {
        try
        {
            if (!Directory.Exists(from))
            {
                return "需要回滚的目录不存在";
            }

            if (Directory.Exists(to) || File.Exists(to))
            {
                return $"原路径 {to} 已被占用";
            }

            Directory.Move(from, to);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private async Task<GameInfo?> LoadGameAsync(int gameId, CancellationToken cancellationToken)
    {
        await using var db = _dbContextFactory.CreateDbContext();
        return await db.Games
            .AsNoTracking()
            .FirstOrDefaultAsync(g => g.Id == gameId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Maps a compatibility mode name onto the token Windows stores in the Layers key.</summary>
    private static string NormalizeCompatibilityLayer(string compatibilityMode)
    {
        var text = (compatibilityMode ?? string.Empty).Trim();
        if (text.StartsWith('~'))
        {
            text = text[1..].Trim();
        }

        var upper = text.ToUpperInvariant().Replace(" ", string.Empty, StringComparison.Ordinal);

        var token = upper switch
        {
            "WINDOWS7" or "WIN7" or "WIN7RTM" or "7" => "WIN7RTM",
            "WINDOWS8" or "WIN8" or "WIN8RTM" or "8" => "WIN8RTM",
            "WINDOWS81" or "WIN81" => "WIN8RTM",
            "WINDOWS10" or "WIN10" => "WIN10RTM",
            "WINDOWSXP" or "WINXP" or "XP" or "WINDOWSXPSP3" or "WINXPSP3" => "WINXPSP3",
            "WINDOWSXPSP2" or "WINXPSP2" => "WINXPSP2",
            "WINDOWSVISTA" or "VISTA" or "WINDOWSVISTASP2" => "VISTASP2",
            "WINDOWS98" or "WIN98" or "98" => "WIN98",
            "WINDOWS95" or "WIN95" or "95" => "WIN95",
            _ => upper
        };

        return token.Length == 0 ? DefaultCompatibilityMode : $"~ {token}";
    }

    /// <summary>HKCU key Windows reads per-application compatibility layers from.</summary>
    private const string CompatibilityLayersKey =
        @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";

    /// <summary>Serialises access to the compatibility-layer key.</summary>
    private static readonly object RegistryGate = new();

    private static string? ReadCompatibilityLayer(string executablePath)
    {
        lock (RegistryGate)
        {
            using var key = Registry.CurrentUser.OpenSubKey(CompatibilityLayersKey);
            return key?.GetValue(executablePath) as string;
        }
    }

    private static void WriteCompatibilityLayer(string executablePath, string value)
    {
        lock (RegistryGate)
        {
            using var key = Registry.CurrentUser.CreateSubKey(CompatibilityLayersKey, writable: true)
                            ?? throw new InvalidOperationException("无法打开 HKCU 兼容性层注册表项。");

            key.SetValue(executablePath, value, RegistryValueKind.String);
        }
    }

    private static void DeleteCompatibilityLayer(string executablePath)
    {
        lock (RegistryGate)
        {
            using var key = Registry.CurrentUser.OpenSubKey(CompatibilityLayersKey, writable: true);
            key?.DeleteValue(executablePath, throwOnMissingValue: false);
        }
    }
}
