using Galbox.Data.Entities;

namespace Galbox.Services.Saves;

/// <summary>
/// Turns a game's save directory into <see cref="SaveNode"/> rows.
/// </summary>
/// <remarks>
/// The service is the "glue" between <c>Galbox.Core.Saves</c> (which knows how to read a Ren'Py save) and
/// <c>Galbox.Data</c> (which knows how to store a story position). It owns no parsing and no presentation.
/// </remarks>
public interface ISaveNodeScanService
{
    /// <summary>
    /// Scans the save directory of a game and upserts one <see cref="SaveNode"/> per save slot.
    /// </summary>
    /// <remarks>
    /// Idempotent: running it twice over an unchanged save directory inserts nothing the second time. It never
    /// throws for an expected failure (missing directory, unreadable save, database error); those come back as
    /// a <see cref="SaveNodeScanReport"/> with <see cref="SaveNodeScanFailureKind"/> and a reason, and no row is
    /// written. Cancellation still throws <see cref="OperationCanceledException"/>.
    /// </remarks>
    /// <param name="gameInfoId">Identifier of the game in the library.</param>
    /// <param name="gameInstallPath">Game installation root, its <c>game</c> folder, or any nested path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<SaveNodeScanReport> ScanAsync(
        int gameInfoId, string gameInstallPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads back the stored nodes of a game, ordered by save time (oldest first) — the timeline view's query.
    /// </summary>
    /// <param name="gameInfoId">Identifier of the game in the library.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<SaveNode>> GetNodesAsync(int gameInfoId, CancellationToken cancellationToken = default);
}
