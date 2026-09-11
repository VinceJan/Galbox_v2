namespace Galbox.App.Services;

/// <summary>
/// One successfully downloaded image file.
/// </summary>
public sealed class DownloadedImage
{
    /// <summary>What the image is for ("Cover", "Background", "Character").</summary>
    public string ImageKind { get; set; } = string.Empty;

    /// <summary>URL the bytes came from.</summary>
    public string SourceUrl { get; set; } = string.Empty;

    /// <summary>Absolute path of the file written to disk.</summary>
    public string LocalPath { get; set; } = string.Empty;

    /// <summary>Size of the written file in bytes.</summary>
    public long SizeBytes { get; set; }

    /// <summary>Format detected from the file signature ("JPEG", "PNG", ...).</summary>
    public string DetectedFormat { get; set; } = string.Empty;

    /// <summary>The first bytes of the file, proving it is a real image rather than an empty file.</summary>
    public byte[] HeaderBytes { get; set; } = Array.Empty<byte>();
}

/// <summary>
/// Outcome of <see cref="IGameImageService.DownloadForGameAsync"/>.
/// </summary>
public sealed class ImageDownloadSummary
{
    /// <summary>Game the download was for.</summary>
    public int GameId { get; set; }

    /// <summary>Local path recorded on the game, when a cover was downloaded.</summary>
    public string? CoverPath { get; set; }

    /// <summary>Local path recorded on the game, when a background was downloaded.</summary>
    public string? BackgroundPath { get; set; }

    /// <summary>Number of character portraits downloaded.</summary>
    public int CharacterImageCount { get; set; }

    /// <summary>Number of images downloaded in total.</summary>
    public int DownloadedCount => (CoverPath is null ? 0 : 1) + (BackgroundPath is null ? 0 : 1) + CharacterImageCount;

    /// <summary>Human readable reasons for every image that could not be downloaded.</summary>
    public List<string> Errors { get; set; } = new();
}
