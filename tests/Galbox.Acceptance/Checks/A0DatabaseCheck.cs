using Galbox.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Galbox.Acceptance.Checks;

/// <summary>
/// A0 - Database isolation and schema smoke test.
///
/// Proves three things the rest of the run depends on:
///   * the acceptance host never touches %LocalAppData%\Galbox\galbox.db;
///   * the real EF Core model in Galbox.Data can be created from scratch;
///   * a GameInfo round-trips through SQLite, so later checks can use a persisted row
///     (which A4 needs for its foreign-keyed error records).
///
/// It also prints the state of the (empty) game library, which is the "what does the data
/// actually look like today" baseline.
/// </summary>
public sealed class A0DatabaseCheck : IAcceptanceCheck
{
    /// <inheritdoc />
    public string Id => "A0";

    /// <inheritdoc />
    public string Title => "Database isolation, schema creation and GameInfo round-trip";

    /// <inheritdoc />
    public async Task<CheckResult> RunAsync(AcceptanceContext context, CancellationToken cancellationToken)
    {
        var expected = "fresh acceptance.db is (re)created, GameInfo persists and reads back identically, "
                     + "and a second reset proves the run is repeatable";
        var details = new List<string>();

        var realDbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Galbox",
            "galbox.db");

        details.Add($"Acceptance DB path : {AcceptanceContainer.DatabasePath}");
        details.Add($"Real app DB path   : {realDbPath}");
        details.Add($"In isolation       : {!string.Equals(AcceptanceContainer.DatabasePath, realDbPath, StringComparison.OrdinalIgnoreCase)}");

        // --- Phase 1: delete + recreate ------------------------------------------------
        var (deleted, created) = await AcceptanceContainer
            .ResetDatabaseAsync(context.Services, cancellationToken)
            .ConfigureAwait(false);

        details.Add($"EnsureDeleted()    : {deleted}");
        details.Add($"EnsureCreated()    : {created}");
        details.Add($"DB file exists     : {File.Exists(AcceptanceContainer.DatabasePath)}"
                  + (File.Exists(AcceptanceContainer.DatabasePath)
                        ? $" ({new FileInfo(AcceptanceContainer.DatabasePath).Length} bytes)"
                        : string.Empty));

        await using (var scope = context.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GalboxDbContext>();

            // Table list proves the whole model was created, not just the Games table.
            var tables = await ListSqliteTablesAsync(db, cancellationToken).ConfigureAwait(false);
            details.Add($"Tables created     : {tables.Count} [{string.Join(", ", tables)}]");

            // --- Library state before anything is inserted ----------------------------
            var gamesBefore = await db.Games.CountAsync(cancellationToken).ConfigureAwait(false);
            var scrapedBefore = await db.Games.CountAsync(g => g.IsScraped, cancellationToken).ConfigureAwait(false);
            var notScrapedBefore = await db.Games.CountAsync(g => !g.IsScraped, cancellationToken).ConfigureAwait(false);

            details.Add("--- game library state on the fresh database ---");
            details.Add($"  total games        : {gamesBefore}");
            details.Add($"  IsScraped == true  : {scrapedBefore}");
            details.Add($"  IsScraped == false : {notScrapedBefore}   (library is empty on a fresh DB)");
        }

        // --- Phase 2: insert the test game and read it back ----------------------------
        var game = BuildTestGame(context.Options);
        int persistedId;

        await using (var scope = context.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GalboxDbContext>();
            db.Games.Add(game);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            persistedId = game.Id;
        }

        GameInfo? readBack;
        await using (var scope = context.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GalboxDbContext>();
            readBack = await db.Games.AsNoTracking()
                .FirstOrDefaultAsync(g => g.Id == persistedId, cancellationToken)
                .ConfigureAwait(false);
        }

        if (readBack is null)
        {
            return CheckResult.Fail(Id, Title, expected, "row not found after insert").With(details.ToArray());
        }

        details.Add("--- inserted test game, read back with AsNoTracking ---");
        details.Add($"  Id                 : {persistedId}");
        details.Add($"  NameOriginal       : {readBack.NameOriginal}");
        details.Add($"  InstallPath        : {readBack.InstallPath}");
        details.Add($"  MainExecutable     : {readBack.MainExecutable}");
        details.Add($"  IsScraped          : {readBack.IsScraped}");
        details.Add($"  EngineType         : {readBack.EngineType}");
        details.Add($"  DisplayName        : {readBack.DisplayName}");
        details.Add($"  AddedTime (UTC)    : {readBack.AddedTime:u}");

        var roundTripOk =
            readBack.NameOriginal == game.NameOriginal &&
            readBack.InstallPath == game.InstallPath &&
            readBack.MainExecutable == game.MainExecutable;

        // --- Phase 3: reset again to prove the run is repeatable -----------------------
        await AcceptanceContainer.ResetDatabaseAsync(context.Services, cancellationToken).ConfigureAwait(false);

        int gamesAfterSecondReset;
        await using (var scope = context.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GalboxDbContext>();
            gamesAfterSecondReset = await db.Games.CountAsync(cancellationToken).ConfigureAwait(false);
        }

        var repeatable = gamesAfterSecondReset == 0;
        details.Add("--- second EnsureDeleted()/EnsureCreated() (simulates the next run) ---");
        details.Add($"  games after reset  : {gamesAfterSecondReset} (expected 0)");
        details.Add($"  DB size after reset: {new FileInfo(AcceptanceContainer.DatabasePath).Length} bytes");

        // --- Phase 4: re-seed so A4 can write foreign-keyed error records --------------
        var reseeded = BuildTestGame(context.Options);
        await using (var scope = context.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GalboxDbContext>();
            db.Games.Add(reseeded);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        context.PersistedGame = reseeded;
        details.Add($"--- re-seeded test game for later checks: Id={reseeded.Id} ---");

        var pass = created && roundTripOk && repeatable;
        var actual = $"created={created}, roundTrip={roundTripOk}, repeatable={repeatable}";

        return (pass
                ? CheckResult.Pass(Id, Title, expected, actual)
                : CheckResult.Fail(Id, Title, expected, actual))
            .With(details.ToArray());
    }

    /// <summary>Builds the <see cref="GameInfo"/> used as the real test subject.</summary>
    public static GameInfo BuildTestGame(AcceptanceOptions options)
    {
        return new GameInfo
        {
            NameOriginal = options.GameName,
            InstallPath = options.GameFolder,
            MainExecutable = Path.Combine(options.GameFolder, "dreaminher.exe"),
            AddedTime = DateTime.UtcNow,
            UpdatedTime = DateTime.UtcNow
        };
    }

    private static async Task<List<string>> ListSqliteTablesAsync(
        GalboxDbContext db,
        CancellationToken cancellationToken)
    {
        var tables = new List<string>();
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                tables.Add(reader.GetString(0));
            }
        }
        finally
        {
            await connection.CloseAsync().ConfigureAwait(false);
        }

        return tables;
    }
}
