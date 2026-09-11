using System.IO;
using Galbox.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Galbox.App.Services;

/// <summary>
/// Deletes a game and its related records from the library, leaving the game files untouched.
/// </summary>
public sealed class GameDeletionService : IGameDeletionService
{
    private readonly IDbContextFactory<GalboxDbContext> _dbContextFactory;
    private readonly ILogger<GameDeletionService> _logger;

    /// <summary>
    /// Creates a GameDeletionService.
    /// </summary>
    public GameDeletionService(
        IDbContextFactory<GalboxDbContext> dbContextFactory,
        ILogger<GameDeletionService> logger)
    {
        _dbContextFactory = dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<GameDeletionResult> DeleteGameAsync(
        int gameId,
        GameDeletionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new GameDeletionOptions();
        var result = new GameDeletionResult { GameId = gameId };

        if (gameId <= 0)
        {
            result.ErrorMessage = "Invalid game id";
            return result;
        }

        try
        {
            await using var db = _dbContextFactory.CreateDbContext();

            var game = await db.Games
                .AsNoTracking()
                .FirstOrDefaultAsync(g => g.Id == gameId, cancellationToken)
                .ConfigureAwait(false);

            if (game == null)
            {
                result.ErrorMessage = $"Game {gameId} was not found";
                _logger.LogWarning("Cannot delete game {GameId}: not found", gameId);
                return result;
            }

            result.GameName = game.DisplayName;
            result.InstallPath = game.InstallPath ?? string.Empty;

            // ---------------------------------------------------------------- backup files
            // Read the paths before the rows disappear, so the optional file deletion below can
            // still find them.
            var backupPaths = await db.SaveBackups
                .Where(b => b.GameInfoId == gameId)
                .Select(b => b.BackupPath)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            if (options.DeleteBackupFiles)
            {
                foreach (var path in backupPaths.Where(p => !string.IsNullOrWhiteSpace(p)))
                {
                    try
                    {
                        if (File.Exists(path))
                        {
                            File.Delete(path);
                            result.BackupFilesDeleted++;
                        }
                    }
                    catch (Exception ex)
                    {
                        result.Warnings.Add($"无法删除存档备份文件 {path}：{ex.Message}");
                        _logger.LogWarning(ex, "Could not delete backup file {BackupPath}", path);
                    }
                }
            }

            // ------------------------------------------------------------------- rows
            // Every child table cascades from Games, but the rows are removed explicitly: the counts
            // are part of the result the user sees, and an explicit delete does not silently depend
            // on the foreign-key configuration of a database file that older builds created.
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            result.CharactersDeleted = await db.Characters
                .Where(c => c.GameInfoId == gameId)
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

            result.DocumentsDeleted = await db.Documents
                .Where(d => d.GameInfoId == gameId)
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

            result.MediaFilesDeleted = await db.MediaFiles
                .Where(m => m.GameInfoId == gameId)
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

            result.ScreenshotsDeleted = await db.Screenshots
                .Where(s => s.GameInfoId == gameId)
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

            result.BackupsDeleted = await db.SaveBackups
                .Where(b => b.GameInfoId == gameId)
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

            result.ErrorRecordsDeleted = await db.ErrorRecords
                .Where(e => e.GameInfoId == gameId)
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

            result.PatchesDeleted = await db.Patches
                .Where(p => p.GameInfoId == gameId)
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

            var gameRowsDeleted = await db.Games
                .Where(g => g.Id == gameId)
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            // ------------------------------------------------------------------ verification
            // The verdict is read back from the database instead of being assumed from the number
            // of rows the delete reported.
            var stillThere = await db.Games
                .AsNoTracking()
                .AnyAsync(g => g.Id == gameId, cancellationToken)
                .ConfigureAwait(false);

            result.Success = !stillThere && gameRowsDeleted > 0;

            if (!result.Success)
            {
                result.ErrorMessage = stillThere
                    ? "游戏记录仍然存在，删除未生效"
                    : "未删除任何游戏记录";
                _logger.LogError(
                    "Deleting game {GameId} ({GameName}) did not remove the row (rowsDeleted={RowsDeleted}, stillThere={StillThere})",
                    gameId,
                    result.GameName,
                    gameRowsDeleted,
                    stillThere);
                return result;
            }

            // The game files must still be there. This is measured, not assumed, so the UI can
            // state it truthfully.
            result.GameFilesKept = string.IsNullOrWhiteSpace(game.InstallPath) || Directory.Exists(game.InstallPath);

            _logger.LogInformation(
                "Deleted game {GameId} ({GameName}) from the library: {RelatedRows} related row(s) removed, "
                + "{BackupFiles} backup file(s) removed (requested: {DeleteBackupFiles}), game folder kept: {GameFilesKept}",
                gameId,
                result.GameName,
                result.RelatedRowsDeleted,
                result.BackupFilesDeleted,
                options.DeleteBackupFiles,
                result.GameFilesKept);

            return result;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Deleting game {GameId} was cancelled", gameId);
            throw;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
            _logger.LogError(ex, "Error deleting game {GameId}", gameId);
            return result;
        }
    }
}
