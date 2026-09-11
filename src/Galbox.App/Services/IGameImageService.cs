using Galbox.Data.Entities;

namespace Galbox.App.Services;

/// <summary>
/// Downloads the images a scrape produces (cover, background, character portraits) into the local
/// cache and records the resulting file paths on the game.
/// </summary>
/// <remarks>
/// The scraping services only ever wrote the image *URLs* (<c>CoverImageUrl</c>,
/// <c>BackgroundImageUrl</c>, <c>GameCharacter.ImageUrl</c>), while every <c>&lt;Image&gt;</c> in the
/// UI binds the *path* properties through <c>PathToImageConverter</c>, which requires
/// <c>File.Exists</c>. Nothing in the product downloaded anything, so every cover in the home page,
/// library, detail page, save manager and patch centre stayed blank.
/// </remarks>
public interface IGameImageService
{
    /// <summary>Folder that holds downloaded cover images.</summary>
    string CoversDirectory { get; }

    /// <summary>Folder that holds downloaded background images.</summary>
    string BackgroundsDirectory { get; }

    /// <summary>Folder that holds downloaded character portraits.</summary>
    string CharacterImagesDirectory { get; }

    /// <summary>
    /// Downloads the cover and background of a game plus the portrait of every character that has
    /// an image URL, and writes the resulting local paths back to the database.
    /// </summary>
    /// <param name="gameId">Database id of the game.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A summary of what was downloaded and what failed.</returns>
    Task<ImageDownloadSummary> DownloadForGameAsync(int gameId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads the cover image of an in-memory game and writes the local path back to the
    /// database and to <paramref name="game"/>.
    /// </summary>
    Task<DownloadedImage?> DownloadCoverAsync(GameInfo game, CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads a single image from a URL into a target folder, validating that the response
    /// really is an image before it is written.
    /// </summary>
    /// <param name="url">Absolute http(s) URL.</param>
    /// <param name="targetDirectory">Folder the file is written to (created when missing).</param>
    /// <param name="fileStem">File name without extension; the extension comes from the format.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<DownloadedImage?> DownloadImageAsync(
        string? url,
        string targetDirectory,
        string fileStem,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates that a file exists and starts with a known image signature.
    /// </summary>
    /// <param name="path">File to inspect.</param>
    /// <param name="format">Detected format name ("JPEG", "PNG", ...) when valid.</param>
    /// <param name="error">Reason when invalid.</param>
    bool TryValidateImageFile(string? path, out string format, out string? error);
}
