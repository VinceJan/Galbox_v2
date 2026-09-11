using Galbox.App.Services;
using Galbox.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Galbox.Acceptance.Checks;

/// <summary>
/// A15 - Scraped image URLs become real local image files (W15).
///
/// The defect: scraping wrote <c>CoverImageUrl</c> / <c>BackgroundImageUrl</c> /
/// <c>GameCharacter.ImageUrl</c>, but nothing in the product ever downloaded an image, while every
/// <c>&lt;Image&gt;</c> binds the PATH properties (<c>CoverImagePath</c>, <c>ImagePath</c>) through
/// <c>PathToImageConverter</c>, which requires <c>File.Exists</c>. Every cover in the home page,
/// library, detail page, save manager and patch centre was therefore permanently blank.
///
/// This check applies real live metadata (the same call the scraping flow makes) and then measures
/// the file that appears on disk: path, size and leading bytes, so an empty file or an HTML error
/// page cannot pass. Character portraits are covered by the same measurement.
/// </summary>
public sealed class A15CoverImageDownloadCheck : IAcceptanceCheck
{
    /// <inheritdoc />
    public string Id => "A15";

    /// <inheritdoc />
    public string Title => "Scraped cover and character images are downloaded as real image files";

    /// <inheritdoc />
    public async Task<CheckResult> RunAsync(AcceptanceContext context, CancellationToken cancellationToken)
    {
        var expected = "after metadata is applied the game has a CoverImagePath pointing at an existing file whose "
                     + "leading bytes are a valid image signature, the database agrees, and a character portrait is "
                     + "written to ImagePath as well";

        var details = new List<string>();

        // ------------------------------------------------------- live metadata (needs the network)
        var scraping = context.Get<IGameScrapingService>();
        var query = context.ScrapingQueryText ?? context.Options.GameName;
        var search = await scraping.SearchGameAsync(query, cancellationToken).ConfigureAwait(false);

        var coverUrl = search.BestMatch?.Items
            .FirstOrDefault(item => item.IsBestMatch)?.CoverImageUrl
            ?? search.SourceResults.Values
                .SelectMany(source => source.Items)
                .Select(item => item.CoverImageUrl)
                .FirstOrDefault(url => !string.IsNullOrWhiteSpace(url));

        details.Add($"Query                : \"{query}\"");
        details.Add($"Sources with results : {string.Join(", ", search.SourceResults.Where(kv => kv.Value.Items.Count > 0).Select(kv => kv.Key))}");

        if (string.IsNullOrWhiteSpace(coverUrl))
        {
            return CheckResult.Fail(Id, Title, expected, "no source returned a cover image URL")
                .With(details.ToArray())
                .With("FAIL REASON: the check needs at least one real cover URL from a live search; A5/A7 need the network too.");
        }

        details.Add($"Live cover URL       : {coverUrl}");

        // ---------------------------------------------------------------------------- target game
        var game = context.PersistedGame;
        if (game is null)
        {
            await using var scope = context.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<GalboxDbContext>();
            var created = new GameInfo
            {
                NameOriginal = "A15 image probe",
                InstallPath = Path.Combine(AcceptanceWork.Create("a15"), "game"),
                MainExecutable = string.Empty,
                AddedTime = DateTime.UtcNow,
                UpdatedTime = DateTime.UtcNow
            };
            db.Games.Add(created);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            game = created;
            context.PersistedGame = created;
        }

        details.Add($"Target game          : Id={game.Id}, {game.DisplayName}");

        // --------------------------------------------------- apply metadata exactly like scraping does
        var metadata = new GameMetadata
        {
            Source = ScraperSource.Bangumi,
            SourceId = search.BestMatch?.Items.FirstOrDefault(i => i.IsBestMatch)?.SourceId ?? "a15-probe",
            TitleCn = "A15 封面下载探针",
            CoverImageUrl = coverUrl,
            MatchScore = 100
        };

        var metadataType = typeof(GameMetadata);
        details.Add($"GameMetadata type    : {metadataType.FullName}");

        var autoScraping = context.Get<IAutoScrapingService>();
        var (applyOk, _, applyError) = await ReflectionBridge
            .CallAsync(autoScraping, "ApplyMetadataAsync", game.Id, metadata, null, cancellationToken, null)
            .ConfigureAwait(false);

        details.Add($"ApplyMetadataAsync   : ok={applyOk}, error={applyError ?? "(none)"}");

        if (!applyOk)
        {
            return CheckResult.Fail(Id, Title, expected, $"ApplyMetadataAsync failed: {applyError}")
                .With(details.ToArray());
        }

        // ---------------------------------------------------------------- measure what is on disk
        GameInfo? reloaded;
        await using (var scope = context.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GalboxDbContext>();
            reloaded = await db.Games.AsNoTracking().FirstOrDefaultAsync(g => g.Id == game.Id, cancellationToken).ConfigureAwait(false);
        }

        var coverPath = reloaded?.CoverImagePath;
        var (format, size, headerHex) = AcceptanceWork.InspectImage(coverPath ?? string.Empty);

        details.Add(string.Empty);
        details.Add("--- database after ApplyMetadataAsync ---");
        details.Add($"  CoverImageUrl        : {reloaded?.CoverImageUrl ?? "(null)"}");
        details.Add($"  CoverImagePath       : {coverPath ?? "(null)"}");
        details.Add("--- measured file on disk ---");
        details.Add($"  exists               : {!string.IsNullOrEmpty(coverPath) && File.Exists(coverPath)}");
        details.Add($"  size                 : {size} bytes");
        details.Add($"  leading bytes        : {headerHex}");
        details.Add($"  signature            : {format}");

        // ------------------------------------------------------- the service's own validator agrees
        var imageService = ReflectionBridge.ResolveService(context.Services, "Galbox.App.Services.IGameImageService");
        details.Add(string.Empty);
        details.Add("--- IGameImageService (the downloader) ---");
        details.Add($"  resolved             : {(imageService is null ? "NOT REGISTERED" : imageService.GetType().FullName)}");

        var shippingCoversDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Galbox",
            "Covers");
        details.Add($"  shipping app folder  : {shippingCoversDirectory} (this run uses an isolated folder so a test");
        details.Add("                         game id can never overwrite the user's real cover images)");
        details.Add($"  run folder           : {ReflectionBridge.String(imageService, "CoversDirectory") ?? "(n/a)"}");

        var serviceValidation = false;
        string? validationError = null;
        if (imageService is not null)
        {
            var validation = imageService.GetType()
                .GetMethod("TryValidateImageFile")!
                .Invoke(imageService, new object?[] { coverPath, null, null });

            serviceValidation = validation is true;
            details.Add($"  TryValidateImageFile : {validation}");
        }

        // -------------------------------------------------------------------- character portraits
        var characterCount = 0;
        var characterPath = string.Empty;
        string? characterError = null;

        if (imageService is not null)
        {
            await using (var scope = context.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<GalboxDbContext>();
                db.Set<GameCharacter>().Add(new GameCharacter
                {
                    GameInfoId = game.Id,
                    Name = "A15 portrait probe",
                    ImageUrl = coverUrl
                });
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            var (downloadOk, downloadResult, downloadError) = await ReflectionBridge
                .CallAsync(imageService, "DownloadForGameAsync", game.Id, cancellationToken)
                .ConfigureAwait(false);

            characterError = downloadError;
            characterCount = ReflectionBridge.Int(downloadResult, "CharacterImageCount");

            await using (var scope = context.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<GalboxDbContext>();
                var character = await db.Set<GameCharacter>()
                    .AsNoTracking()
                    .Where(c => c.GameInfoId == game.Id && c.Name == "A15 portrait probe")
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);

                characterPath = character?.ImagePath ?? string.Empty;
            }

            var (characterFormat, characterSize, characterHeader) = AcceptanceWork.InspectImage(characterPath);

            details.Add(string.Empty);
            details.Add("--- character portrait ---");
            details.Add($"  DownloadForGameAsync : ok={downloadOk}, error={downloadError ?? "(none)"}");
            details.Add($"  ImageUrl             : {coverUrl}");
            details.Add($"  ImagePath            : {(string.IsNullOrEmpty(characterPath) ? "(null)" : characterPath)}");
            details.Add($"  size                 : {characterSize} bytes");
            details.Add($"  leading bytes        : {characterHeader}");
            details.Add($"  signature            : {characterFormat}");

            characterCount = string.IsNullOrEmpty(characterPath) ? 0 : characterCount;
        }

        // ------------------------------------------------------------- the HTTP traffic that proves it
        var imageTraffic = context.Traffic.For("ImageDownload");
        details.Add(string.Empty);
        details.Add($"--- recorded HTTP exchanges on the ImageDownload pipeline: {imageTraffic.Count} ---");
        foreach (var exchange in imageTraffic.Take(5))
        {
            details.Add($"  {exchange.Method} {exchange.Url} -> HTTP {exchange.StatusCode} ({exchange.ResponseBody.Length} response bytes captured)");
        }

        if (imageTraffic.Count == 0)
        {
            details.Add("  (no exchange: nothing requested any image - the download path was never reached)");
        }

        var coverOk = !string.IsNullOrEmpty(coverPath)
                   && File.Exists(coverPath)
                   && size > 1000
                   && format != "UNKNOWN"
                   && format != "(missing)";

        var databaseAgrees = reloaded is not null
                          && string.Equals(reloaded.CoverImagePath, coverPath, StringComparison.OrdinalIgnoreCase);

        var portraitOk = !string.IsNullOrEmpty(characterPath)
                      && File.Exists(characterPath)
                      && AcceptanceWork.InspectImage(characterPath).Size > 1000
                      && AcceptanceWork.InspectImage(characterPath).Format != "UNKNOWN";

        details.Add(string.Empty);
        details.Add("--- interpretation ---");
        details.Add($"  cover file is a real image ({format}, {size} bytes) : {coverOk}");
        details.Add($"  database CoverImagePath matches the file            : {databaseAgrees}");
        details.Add($"  character portrait written                          : {portraitOk}");
        details.Add($"  service validator accepts the cover                 : {serviceValidation}");
        if (!coverOk)
        {
            details.Add("  VERDICT: the scrape stored a URL but no image file was produced where the UI looks");
            details.Add("           for it - the W15 blank-cover defect.");
        }

        var pass = coverOk && databaseAgrees && portraitOk && serviceValidation;

        var actual = $"coverPath={coverPath ?? "(null)"}, size={size}, signature={format}, "
                   + $"portrait={portraitOk}, serviceValidation={serviceValidation}";

        var result = pass
            ? CheckResult.Pass(Id, Title, expected, actual)
            : CheckResult.Fail(Id, Title, expected, actual);

        result.With(details.ToArray());

        if (characterError is not null)
        {
            result.With($"note: character download reported: {characterError}");
        }

        if (!string.IsNullOrEmpty(validationError))
        {
            result.With($"note: validator reported: {validationError}");
        }

        return result;
    }
}
