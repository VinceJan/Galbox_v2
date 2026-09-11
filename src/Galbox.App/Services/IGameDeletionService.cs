using Galbox.Data.Entities;

namespace Galbox.App.Services;

/// <summary>
/// Removes a game from the library.
/// </summary>
/// <remarks>
/// Before this service existed the product had no way to remove a game at all: the only "delete" in
/// the whole repository removed an entry from the settings list of scan folders. A game that was
/// added by mistake - or a folder full of non-game executables picked up by a scan - stayed in the
/// library forever.
///
/// The contract is deliberately one-sided: this removes DATABASE records only. The game folder, its
/// executable and its save files on disk are never touched, and the result reports that fact rather
/// than merely promising it.
/// </remarks>
public interface IGameDeletionService
{
    /// <summary>
    /// Deletes a game and every record that belongs to it (characters, documents, media files,
    /// screenshots, save-backup records, error records, patch records).
    /// </summary>
    /// <param name="gameId">Database id of the game to remove.</param>
    /// <param name="options">Deletion options; <c>null</c> means "keep backup files".</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What was removed, and whether the game files are still on disk.</returns>
    Task<GameDeletionResult> DeleteGameAsync(
        int gameId,
        GameDeletionOptions? options = null,
        CancellationToken cancellationToken = default);
}
