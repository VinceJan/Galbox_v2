using System.IO;
using Galbox.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Galbox.App.Services;

/// <summary>
/// Downloads scraped images to disk and records their local paths.
/// </summary>
/// <remarks>
/// Design notes:
///   * Bytes are validated against known image signatures (JPEG/PNG/GIF/BMP/WEBP) before the file
///     is accepted. HTML error pages, empty responses and truncated downloads are rejected
///     instead of being stored as a "cover" that the UI then fails to render.
///   * Files are written to a temporary name and moved into place, so a cancelled or failed
///     download can never leave a half-written image behind.
///   * Every failure is reported, never thrown: an image that cannot be downloaded must not break
///     metadata application, which already succeeded by that point.
/// </remarks>
public sealed class GameImageService : IGameImageService
{
    /// <summary>Named HttpClient used for image traffic (registered in App.xaml.cs).</summary>
    public const string HttpClientName = "ImageDownload";

    /// <summary>Smallest payload accepted as an image (a 1x1 JPEG is ~120 bytes).</summary>
    private const int MinimumImageBytes = 64;

    private const int HeaderLength = 12;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IDbContextFactory<GalboxDbContext> _dbContextFactory;
    private readonly ILogger<GameImageService> _logger;

    /// <summary>
    /// Creates a GameImageService.
    /// </summary>
    /// <param name="httpClientFactory">Factory for the image HttpClient.</param>
    /// <param name="dbContextFactory">Factory used to create a database context per operation.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="imageRootDirectory">
    /// Root folder for downloaded images. Defaults to <c>%LocalAppData%\Galbox</c>; the acceptance
    /// harness points it at an isolated folder so a test run can never overwrite the images of the
    /// user's real library.
    /// </param>
    public GameImageService(
        IHttpClientFactory httpClientFactory,
        IDbContextFactory<GalboxDbContext> dbContextFactory,
        ILogger<GameImageService> logger,
        string? imageRootDirectory = null)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _dbContextFactory = dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var root = string.IsNullOrWhiteSpace(imageRootDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Galbox")
            : imageRootDirectory!;

        CoversDirectory = Path.Combine(root, "Covers");
        BackgroundsDirectory = Path.Combine(root, "Backgrounds");
        CharacterImagesDirectory = Path.Combine(root, "Characters");
    }

    /// <inheritdoc />
    public string CoversDirectory { get; }

    /// <inheritdoc />
    public string BackgroundsDirectory { get; }

    /// <inheritdoc />
    public string CharacterImagesDirectory { get; }

    /// <inheritdoc />
    public async Task<ImageDownloadSummary> DownloadForGameAsync(int gameId, CancellationToken cancellationToken = default)
    {
        var summary = new ImageDownloadSummary { GameId = gameId };

        if (gameId <= 0)
        {
            summary.Errors.Add("Invalid game id");
            return summary;
        }

        GameInfo? game;
        using (var db = _dbContextFactory.CreateDbContext())
        {
            game = await db.Games
                .Include(g => g.Characters)
                .AsNoTracking()
                .FirstOrDefaultAsync(g => g.Id == gameId, cancellationToken)
                .ConfigureAwait(false);
        }

        if (game == null)
        {
            summary.Errors.Add($"Game {gameId} not found");
            return summary;
        }

        // Cover ---------------------------------------------------------------------------------
        if (string.IsNullOrWhiteSpace(game.CoverImageUrl))
        {
            summary.Errors.Add("CoverImageUrl is empty - nothing to download");
        }
        else
        {
            var cover = await DownloadImageAsync(
                game.CoverImageUrl,
                CoversDirectory,
                game.Id.ToString(),
                cancellationToken).ConfigureAwait(false);

            if (cover == null)
            {
                summary.Errors.Add($"Cover download failed: {game.CoverImageUrl}");
            }
            else
            {
                cover.ImageKind = "Cover";
                summary.CoverPath = cover.LocalPath;
                await UpdateGamePathsAsync(game.Id, cover.LocalPath, null, cancellationToken).ConfigureAwait(false);
            }
        }

        // Background ----------------------------------------------------------------------------
        if (!string.IsNullOrWhiteSpace(game.BackgroundImageUrl))
        {
            var background = await DownloadImageAsync(
                game.BackgroundImageUrl,
                BackgroundsDirectory,
                game.Id.ToString(),
                cancellationToken).ConfigureAwait(false);

            if (background == null)
            {
                summary.Errors.Add($"Background download failed: {game.BackgroundImageUrl}");
            }
            else
            {
                background.ImageKind = "Background";
                summary.BackgroundPath = background.LocalPath;
                await UpdateGamePathsAsync(game.Id, null, background.LocalPath, cancellationToken).ConfigureAwait(false);
            }
        }

        // Character portraits --------------------------------------------------------------------
        if (game.Characters.Count > 0)
        {
            var updatedCharacters = new List<(int Id, string Path)>();

            foreach (var character in game.Characters)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(character.ImageUrl))
                {
                    continue;
                }

                // Characters are stored with ImageUrl only (see AutoScrapingService), while the
                // detail page binds ImagePath - so without this the portraits stay blank.
                if (!string.IsNullOrWhiteSpace(character.ImagePath) && File.Exists(character.ImagePath))
                {
                    continue;
                }

                var portrait = await DownloadImageAsync(
                    character.ImageUrl,
                    CharacterImagesDirectory,
                    $"{game.Id}_{character.Id}",
                    cancellationToken).ConfigureAwait(false);

                if (portrait == null)
                {
                    summary.Errors.Add($"Character portrait download failed: {character.Name} ({character.ImageUrl})");
                    continue;
                }

                portrait.ImageKind = "Character";
                updatedCharacters.Add((character.Id, portrait.LocalPath));
            }

            if (updatedCharacters.Count > 0)
            {
                using var db = _dbContextFactory.CreateDbContext();
                foreach (var (id, path) in updatedCharacters)
                {
                    var tracked = await db.Set<GameCharacter>()
                        .FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
                        .ConfigureAwait(false);

                    if (tracked != null)
                    {
                        tracked.ImagePath = path;
                    }
                }

                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                summary.CharacterImageCount = updatedCharacters.Count;
            }
        }

        _logger.LogInformation(
            "Image download for game {GameId} finished: cover={HasCover}, background={HasBackground}, characters={CharacterCount}, failures={FailureCount}",
            gameId,
            summary.CoverPath != null,
            summary.BackgroundPath != null,
            summary.CharacterImageCount,
            summary.Errors.Count);

        return summary;
    }

    /// <inheritdoc />
    public async Task<DownloadedImage?> DownloadCoverAsync(GameInfo game, CancellationToken cancellationToken = default)
    {
        if (game == null || game.Id <= 0)
        {
            _logger.LogWarning("Cannot download a cover for an unsaved game");
            return null;
        }

        var image = await DownloadImageAsync(
            game.CoverImageUrl,
            CoversDirectory,
            game.Id.ToString(),
            cancellationToken).ConfigureAwait(false);

        if (image == null)
        {
            return null;
        }

        image.ImageKind = "Cover";
        await UpdateGamePathsAsync(game.Id, image.LocalPath, null, cancellationToken).ConfigureAwait(false);
        game.CoverImagePath = image.LocalPath;

        return image;
    }

    /// <inheritdoc />
    public async Task<DownloadedImage?> DownloadImageAsync(
        string? url,
        string targetDirectory,
        string fileStem,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            _logger.LogWarning("Refusing to download an image from a non-http(s) URL: {Url}", url);
            return null;
        }

        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);

            using var response = await client
                .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Image download failed for {Url}: HTTP {StatusCode}",
                    url,
                    (int)response.StatusCode);
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            if (bytes.Length < MinimumImageBytes)
            {
                _logger.LogWarning(
                    "Image download for {Url} returned only {Length} bytes; refusing to store it",
                    url,
                    bytes.Length);
                return null;
            }

            if (!TryDetectImageFormat(bytes, out var format))
            {
                _logger.LogWarning(
                    "Image download for {Url} is not a recognised image (first bytes: {Header})",
                    url,
                    Convert.ToHexString(bytes.AsSpan(0, Math.Min(HeaderLength, bytes.Length))));
                return null;
            }

            Directory.CreateDirectory(targetDirectory);

            var extension = ExtensionForFormat(format);
            var targetPath = Path.Combine(targetDirectory, fileStem + extension);
            var tempPath = targetPath + ".downloading";

            await File.WriteAllBytesAsync(tempPath, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, targetPath, overwrite: true);

            // A previously downloaded image of the same subject in another format would otherwise
            // linger and could still be picked up by a stale path in the database.
            foreach (var stale in Directory.EnumerateFiles(targetDirectory, fileStem + ".*"))
            {
                if (string.Equals(stale, targetPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    File.Delete(stale);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not remove stale image file {Path}", stale);
                }
            }

            var header = bytes.AsSpan(0, Math.Min(HeaderLength, bytes.Length)).ToArray();

            _logger.LogInformation(
                "Downloaded image {Url} -> {Path} ({Length} bytes, {Format})",
                url,
                targetPath,
                bytes.Length,
                format);

            return new DownloadedImage
            {
                SourceUrl = url,
                LocalPath = targetPath,
                SizeBytes = bytes.Length,
                DetectedFormat = format,
                HeaderBytes = header
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Network problems are expected and must never break the caller.
            _logger.LogWarning(ex, "Image download failed for {Url}", url);
            return null;
        }
    }

    /// <inheritdoc />
    public bool TryValidateImageFile(string? path, out string format, out string? error)
    {
        format = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "path is empty";
            return false;
        }

        if (!File.Exists(path))
        {
            error = $"file does not exist: {path}";
            return false;
        }

        try
        {
            var length = new FileInfo(path).Length;
            if (length < MinimumImageBytes)
            {
                error = $"file is only {length} bytes, too small to be an image";
                return false;
            }

            Span<byte> header = stackalloc byte[HeaderLength];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var read = stream.Read(header);
            if (read < 4)
            {
                error = $"file has only {read} readable bytes";
                return false;
            }

            if (!TryDetectImageFormat(header[..read], out format))
            {
                error = $"unrecognised image signature: {Convert.ToHexString(header[..read])}";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"could not read the file: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Detects the image format from the leading bytes of a payload.
    /// </summary>
    internal static bool TryDetectImageFormat(ReadOnlySpan<byte> bytes, out string format)
    {
        format = string.Empty;

        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            format = "JPEG";
            return true;
        }

        if (bytes.Length >= 8
            && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
            && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
        {
            format = "PNG";
            return true;
        }

        if (bytes.Length >= 6
            && bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x38)
        {
            format = "GIF";
            return true;
        }

        if (bytes.Length >= 2 && bytes[0] == 0x42 && bytes[1] == 0x4D)
        {
            format = "BMP";
            return true;
        }

        if (bytes.Length >= 12
            && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46
            && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
        {
            format = "WEBP";
            return true;
        }

        return false;
    }

    /// <summary>File extension (with dot) for a detected format.</summary>
    internal static string ExtensionForFormat(string format) => format switch
    {
        "JPEG" => ".jpg",
        "PNG" => ".png",
        "GIF" => ".gif",
        "BMP" => ".bmp",
        "WEBP" => ".webp",
        _ => ".img"
    };

    /// <summary>
    /// Stores the local cover/background path on the game, writing only the fields that changed.
    /// </summary>
    private async Task UpdateGamePathsAsync(
        int gameId,
        string? coverPath,
        string? backgroundPath,
        CancellationToken cancellationToken)
    {
        try
        {
            using var db = _dbContextFactory.CreateDbContext();
            var game = await db.Games.FirstOrDefaultAsync(g => g.Id == gameId, cancellationToken).ConfigureAwait(false);
            if (game == null)
            {
                return;
            }

            if (coverPath != null)
            {
                game.CoverImagePath = coverPath;
            }

            if (backgroundPath != null)
            {
                game.BackgroundImagePath = backgroundPath;
            }

            game.UpdatedTime = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not record the downloaded image path for game {GameId}", gameId);
        }
    }
}
