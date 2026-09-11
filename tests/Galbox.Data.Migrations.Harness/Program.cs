using System.Globalization;
using System.Text;
using Galbox.Data.Entities;
using Galbox.Data.Migrations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Galbox.Data.Migrations.Harness;

/// <summary>
/// Reproducible evidence tool for the Galbox database migration path.
/// </summary>
/// <remarks>
/// It never touches the source database: every scenario works on a copy inside <c>--workdir</c>. Run it through
/// <c>tools/verify-db-migration.ps1</c>, which copies the real user database to a temporary directory first.
/// </remarks>
internal static class Program
{
    private static int _checks;
    private static int _failures;

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var options = HarnessOptions.Parse(args);
        if (options is null)
        {
            Console.Error.WriteLine(
                "usage: Galbox.Data.Migrations.Harness --workdir <dir> --source-db <path to a COPY of galbox.db>");
            return 2;
        }

        Directory.CreateDirectory(options.WorkDir);

        Console.WriteLine("=================================================================================");
        Console.WriteLine("GALBOX DATABASE MIGRATION VERIFICATION");
        Console.WriteLine("=================================================================================");
        Console.WriteLine($"run at            : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine($"harness           : {typeof(Program).Assembly.GetName().Name}");
        Console.WriteLine($"EF Core           : {ProductInfo.GetVersion()}");
        Console.WriteLine($"work directory    : {options.WorkDir}");
        Console.WriteLine($"source database   : {options.SourceDatabasePath}");
        Console.WriteLine();

        if (!File.Exists(options.SourceDatabasePath))
        {
            Console.Error.WriteLine($"source database not found: {options.SourceDatabasePath}");
            return 2;
        }

        // Defensive: refuse to operate directly on the live user database.
        var livePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Galbox",
            "galbox.db");

        if (string.Equals(
                Path.GetFullPath(options.SourceDatabasePath),
                Path.GetFullPath(livePath),
                StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine(
                "refusing to run: --source-db points at the live user database. Copy it first (the PowerShell " +
                "wrapper does this automatically).");
            return 2;
        }

        await RunLegacyUpgradeScenarioAsync(options).ConfigureAwait(false);
        await RunFreshInstallScenarioAsync(options).ConfigureAwait(false);
        await RunIdempotencyScenarioAsync(options).ConfigureAwait(false);
        await RunHandTamperedScenarioAsync(options).ConfigureAwait(false);
        await RunFullySatisfiedMigrationScenarioAsync(options).ConfigureAwait(false);
        await RunDataPreservationThroughRebuildScenarioAsync(options).ConfigureAwait(false);
        await RunBaselineSchemaEquivalenceScenarioAsync(options).ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine("=================================================================================");
        Console.WriteLine($"SUMMARY: {_checks - _failures}/{_checks} checks passed, {_failures} failed");
        Console.WriteLine("=================================================================================");

        return _failures == 0 ? 0 : 1;
    }

    // --------------------------------------------------------------------------------------------------------
    // Scenario 1: a copy of the real user database (EnsureCreated, no migration history) is upgraded.
    // --------------------------------------------------------------------------------------------------------
    private static async Task RunLegacyUpgradeScenarioAsync(HarnessOptions options)
    {
        Section("SCENARIO 1 - LEGACY DATABASE UPGRADE (copy of the real user database)");

        var target = Path.Combine(options.WorkDir, "legacy-upgrade.db");
        File.Copy(options.SourceDatabasePath, target, overwrite: true);
        Console.WriteLine($"working copy: {target}  ({new FileInfo(target).Length} bytes)");
        Console.WriteLine();

        var before = DbSnapshot.Capture(target);
        PrintSnapshot("BEFORE (legacy EnsureCreated database)", before);

        // Capture the drift between the model and the legacy file, and demonstrate that the old code path
        // (EnsureCreated, which never alters an existing database) could not serve the current model at all.
        List<string> driftBefore;
        await using (var context = CreateContext(target))
        {
            var drift = await SchemaIntegrityChecker.CheckAsync(context).ConfigureAwait(false);
            driftBefore = drift.MissingColumns.ToList();
        }

        Console.WriteLine("--- pre-existing drift (model expects, legacy file lacks) -----");
        if (driftBefore.Count == 0)
        {
            Console.WriteLine("  (none)");
        }
        else
        {
            foreach (var column in driftBefore)
            {
                Console.WriteLine($"  missing column: {column}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("--- proof that the legacy file could not serve the current model (raw SQL) ---");
        try
        {
            var unused = ScalarLong(target, "SELECT COUNT(*) FROM UserSettings WHERE BangumiAuthMethod IS NOT NULL;");
            Console.WriteLine($"  SELECT BangumiAuthMethod FROM UserSettings succeeded (unexpected), count={unused}");
        }
        catch (SqliteException ex)
        {
            Console.WriteLine($"  SELECT BangumiAuthMethod FROM UserSettings failed as expected: {ex.Message}");
        }

        Console.WriteLine();

        // Before/after image of the original rows: values must survive, not just row counts.
        const string gamesQuery =
            "SELECT Id, IFNULL(NameCn,''), NameOriginal, InstallPath, MainExecutable, TotalPlayTimeSeconds, " +
            "LaunchCount, IFNULL(LastSessionTime,''), AddedTime, EngineType FROM Games ORDER BY Id;";

        var gamesBefore = QueryRows(target, gamesQuery);

        var result = await InitializeAsync(target).ConfigureAwait(false);
        Console.WriteLine("--- GalboxDatabaseInitializer result --------------------------");
        Console.WriteLine(result.Describe());
        Console.WriteLine();

        var after = DbSnapshot.Capture(target);
        PrintSnapshot("AFTER (migrated)", after);

        // --- assertions: nothing was lost ---------------------------------------------------------------
        Console.WriteLine("--- checks: existing data preserved --------------------------");
        foreach (var table in before.Tables)
        {
            Check(
                after.Columns.ContainsKey(table),
                $"table '{table}' still exists after the upgrade");

            if (!after.RowCounts.TryGetValue(table, out var afterRows))
            {
                continue;
            }

            var beforeRows = before.RowCounts[table];
            Check(
                beforeRows == afterRows,
                $"table '{table}' row count unchanged ({beforeRows} -> {afterRows})");
        }

        Check(result.Mode == DatabaseInitializationMode.LegacyBaselineStamped, $"mode is LegacyBaselineStamped (actual: {result.Mode})");
        Check(result.BaselineWasStamped, "baseline migration was stamped instead of executed");
        Check(!result.HistoryTableExistedBefore, "the source database really had no __EFMigrationsHistory table");
        Check(result.PreMigrationBackupPath is not null && File.Exists(result.PreMigrationBackupPath), "a pre-migration safety copy was created");
        Check(result.SchemaReport.IsConsistent, "EF model matches the upgraded database");

        // Split the observed drift into "owned by the baseline" (the repair must add it) and "owned by a later,
        // still pending migration" (the repair must NOT add it, or that migration fails on duplicate column name).
        var baselineOwned = new List<string>();
        var migrationOwned = new List<string>();
        await using (var context = CreateContext(target))
        {
            var baselineId = context.Database.GetMigrations().First();
            var baselineSchema = await BaselineSchema.CaptureAsync(context, baselineId).ConfigureAwait(false);

            foreach (var drift in driftBefore)
            {
                var parts = drift.Split('.', 2);
                var ownedByBaseline = parts.Length == 2 &&
                                      baselineSchema.Tables.TryGetValue(parts[0], out var baselineTable) &&
                                      baselineTable.Columns.Any(c => string.Equals(c.Name, parts[1], StringComparison.OrdinalIgnoreCase));

                (ownedByBaseline ? baselineOwned : migrationOwned).Add(drift);
            }
        }

        Console.WriteLine($"baseline-owned drift : [{string.Join(", ", baselineOwned)}]");
        Console.WriteLine($"migration-owned drift: [{string.Join(", ", migrationOwned)}]");
        Console.WriteLine();

        Check(
            result.RepairedColumns.OrderBy(x => x, StringComparer.Ordinal)
                .SequenceEqual(baselineOwned.OrderBy(x => x, StringComparer.Ordinal), StringComparer.Ordinal),
            "the repair added exactly the baseline-owned missing columns");
        Check(
            !result.RepairedColumns.Intersect(migrationOwned, StringComparer.OrdinalIgnoreCase).Any(),
            "the repair did not touch columns owned by pending migrations");
        Check(result.CreatedTables.Count == 0, "no baseline table was missing (all nine were already present)");

        foreach (var missing in driftBefore)
        {
            var parts = missing.Split('.', 2);
            if (parts.Length == 2)
            {
                CheckColumn(after, parts[0], parts[1]);
            }
        }

        // The values of the original rows must be identical, not just the row counts.
        var gamesAfter = QueryRows(target, gamesQuery);
        Console.WriteLine();

        Check(
            gamesBefore.Select(r => string.Join("|", r)).SequenceEqual(gamesAfter.Select(r => string.Join("|", r)), StringComparer.Ordinal),
            "the existing game row is value-for-value identical after the upgrade");
        foreach (var row in gamesAfter)
        {
            Console.WriteLine($"  game row after upgrade: {string.Join(" | ", row)}");
        }

        // Settings must be readable through the repaired columns now.
        var settingsAfter = QueryRows(target, "SELECT Id, EnableBangumi, MatchThresholdPercent, IFNULL(DefaultScrapingSource,''), LastModified FROM UserSettings ORDER BY Id;");
        Console.WriteLine();
        foreach (var row in settingsAfter)
        {
            Console.WriteLine($"  settings row after upgrade: {string.Join(" | ", row)}");
        }

        Check(ScalarLong(target, "SELECT COUNT(*) FROM UserSettings;") == 1, "the existing settings row is still there");
        CheckColumn(after, "UserSettings", "BangumiAuthMethod");

        // --- assertions: the new model really is there ---------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("--- checks: new tables / columns exist -----------------------");
        Check(after.Columns.ContainsKey("SaveNodes"), "table 'SaveNodes' was created");
        Check(after.Columns.ContainsKey("SaveGroups"), "table 'SaveGroups' was created");

        CheckColumn(after, "Games", "Status");
        CheckColumn(after, "Games", "IsStatusUserSet");
        CheckColumn(after, "Games", "StatusChangedTime");
        CheckColumn(after, "SaveBackups", "SaveNodeId");

        Console.WriteLine();
        foreach (var table in new[] { "Games", "SaveBackups", "SaveNodes", "SaveGroups" })
        {
            Console.WriteLine($"PRAGMA table_info({table}) on the migrated copy:");
            foreach (var line in DbSnapshot.DescribeTable(target, table))
            {
                Console.WriteLine(line);
            }

            Console.WriteLine();
        }

        // --- assertions: migration history ---------------------------------------------------------------
        Console.WriteLine("--- __EFMigrationsHistory after the upgrade -------------------");
        foreach (var (id, productVersion) in after.History)
        {
            Console.WriteLine($"  {id}  (product version {productVersion})");
        }

        Console.WriteLine();
        var expected = Migrations(target);
        Check(
            after.History.Select(h => h.MigrationId).SequenceEqual(expected, StringComparer.Ordinal),
            $"migration history equals the migrations in the assembly [{string.Join(", ", expected)}]");

        // --- assertions: the backfilled status matches the old derived text ------------------------------
        Console.WriteLine("--- Games.Status backfill (raw SQL) --------------------------");
        foreach (var row in QueryRows(
            target,
            "SELECT Id, NameOriginal, LaunchCount, TotalPlayTimeSeconds, IFNULL(LastSessionTime,'(null)'), Status, IsStatusUserSet " +
            "FROM Games ORDER BY Id;"))
        {
            Console.WriteLine($"  Id={row[0]} name={row[1]} launches={row[2]} playtime={row[3]} lastSession={row[4]} Status={row[5]} IsStatusUserSet={row[6]}");
        }

        Console.WriteLine();
        Check(
            ScalarLong(target, "SELECT COUNT(*) FROM Games WHERE Status NOT IN (0,1,2,3,4);") == 0,
            "every Games.Status value is inside the documented 0-4 range");

        var expectedStatusSql =
            "SELECT COUNT(*) FROM Games WHERE IsStatusUserSet = 0 AND Status <> (CASE " +
            "WHEN LaunchCount = 0 THEN 0 " +
            "WHEN TotalPlayTimeSeconds > 3600 THEN 2 " +
            "WHEN LastSessionTime IS NOT NULL AND LastSessionTime >= datetime('now','-7 days') THEN 1 " +
            "ELSE 4 END);";
        Check(
            ScalarLong(target, expectedStatusSql) == 0,
            "backfilled status equals the legacy derivation rule for every game");

        // --- assertions: EF can actually read the upgraded database -------------------------------------
        Console.WriteLine("--- EF Core read-back through the new model ------------------");
        await using (var context = CreateContext(target))
        {
            var games = await context.Games.AsNoTracking().OrderBy(g => g.Id).ToListAsync().ConfigureAwait(false);
            foreach (var game in games)
            {
                Console.WriteLine(
                    $"  EF read: #{game.Id} {game.DisplayName} Status={game.Status} userSet={game.IsStatusUserSet} " +
                    $"suggested={GameStatusDerivation.Suggest(game)}");
            }

            var nodeCount = await context.SaveNodes.CountAsync().ConfigureAwait(false);
            var groupCount = await context.SaveGroups.CountAsync().ConfigureAwait(false);
            Console.WriteLine($"  EF read: SaveNodes={nodeCount}, SaveGroups={groupCount}");

            Check(games.Count == before.RowCounts["Games"], "EF sees the same number of games as raw SQL");
            Check(nodeCount == 0 && groupCount == 0, "new tables start empty on an upgraded database");
        }

        Console.WriteLine();
    }

    // --------------------------------------------------------------------------------------------------------
    // Scenario 5: a database whose schema was tampered with by hand (the "ad-hoc ALTER TABLE" pattern).
    // --------------------------------------------------------------------------------------------------------
    private static async Task RunHandTamperedScenarioAsync(HarnessOptions options)
    {
        Section("SCENARIO 5 - HAND-TAMPERED DATABASE (pending migration objects already present)");

        var target = Path.Combine(options.WorkDir, "hand-tampered.db");
        File.Copy(options.SourceDatabasePath, target, overwrite: true);

        // This repository has two precedents of columns being added outside the migration system
        // (Games.EngineType and Games.VndbId were added by an ad-hoc ALTER TABLE loop in App.xaml.cs).
        // Here the two columns that the pending migration owns are added by hand first, and a sentinel value is
        // written into one of them: a tolerant migration must not fail, and must not overwrite that value.
        Execute(target, "ALTER TABLE \"Games\" ADD COLUMN \"Status\" INTEGER NOT NULL DEFAULT 0;");
        Execute(target, "ALTER TABLE \"Games\" ADD COLUMN \"IsStatusUserSet\" INTEGER NOT NULL DEFAULT 0;");
        Execute(target, "UPDATE \"Games\" SET \"Status\" = 3, \"IsStatusUserSet\" = 1;");

        Console.WriteLine($"working copy: {target} (Games.Status, Games.IsStatusUserSet added by hand, Status=3)");
        Console.WriteLine($"rows before: {ScalarLong(target, "SELECT COUNT(*) FROM Games;")} game(s), " +
                          $"Games columns = {ScalarLong(target, "SELECT COUNT(*) FROM pragma_table_info('Games');")}");
        Console.WriteLine();

        var result = await InitializeAsync(target).ConfigureAwait(false);
        Console.WriteLine("--- GalboxDatabaseInitializer result --------------------------");
        Console.WriteLine(result.Describe());
        Console.WriteLine();

        var after = DbSnapshot.Capture(target);

        Console.WriteLine("--- checks -----------------------------------------------------");
        Check(result.SkippedConflictOperations.Count > 0, $"conflict with the hand-added columns was detected ({result.SkippedConflictOperations.Count} skipped operation(s))");
        Check(result.SchemaReport.IsConsistent, "EF model matches the upgraded database");
        Check(after.Columns.ContainsKey("SaveNodes"), "table 'SaveNodes' was created");
        Check(after.Columns.ContainsKey("SaveGroups"), "table 'SaveGroups' was created");
        CheckColumn(after, "Games", "StatusChangedTime");
        CheckColumn(after, "SaveBackups", "SaveNodeId");
        Check(after.Indexes.Contains("IX_Games_Status"), "index 'IX_Games_Status' was created");

        var expected = Migrations(target);
        Check(
            after.History.Select(h => h.MigrationId).SequenceEqual(expected, StringComparer.Ordinal),
            $"migration history equals the migrations in the assembly [{string.Join(", ", expected)}]");

        var rows = QueryRows(target, "SELECT Id, NameOriginal, Status, IsStatusUserSet FROM Games ORDER BY Id;");
        foreach (var row in rows)
        {
            Console.WriteLine($"  game row after upgrade: Id={row[0]} name={row[1]} Status={row[2]} IsStatusUserSet={row[3]}");
        }

        Check(rows.Count == 1, "the existing game row survived");
        Check(rows.Count == 1 && rows[0][2] == "3", "the value written by hand into Games.Status was preserved (3)");
        Check(rows.Count == 1 && rows[0][3] == "1", "the value written by hand into Games.IsStatusUserSet was preserved (1)");

        var fkList = QueryRows(target, "PRAGMA foreign_key_list('SaveBackups');");
        Console.WriteLine($"  foreign keys on SaveBackups after the upgrade: {fkList.Count}");
        foreach (var fk in fkList)
        {
            Console.WriteLine($"    -> {string.Join(" | ", fk)}");
        }

        Check(fkList.Any(fk => fk.Any(v => string.Equals(v, "SaveNodes", StringComparison.OrdinalIgnoreCase))),
            "the SaveBackups -> SaveNodes foreign key exists after the tolerant migration");

        Console.WriteLine();
    }

    // --------------------------------------------------------------------------------------------------------
    // Scenario 6: rows survive the SQLite table rebuild that EF performs for an added foreign key.
    // --------------------------------------------------------------------------------------------------------
    private static async Task RunDataPreservationThroughRebuildScenarioAsync(HarnessOptions options)
    {
        Section("SCENARIO 6 - DATA PRESERVATION THROUGH THE SQLITE TABLE REBUILD");

        var target = Path.Combine(options.WorkDir, "rebuild-preservation.db");
        File.Copy(options.SourceDatabasePath, target, overwrite: true);

        // Migration 2 adds a foreign key to the existing SaveBackups table. SQLite cannot add a constraint in
        // place, so EF rebuilds the table (create ef_temp_SaveBackups, copy the rows, drop, rename). Seeding rows
        // before the upgrade proves that the copy step keeps the data, which is the riskiest part of the upgrade
        // for a real user.
        Execute(
            target,
            "INSERT INTO \"SaveBackups\" (\"GameInfoId\", \"Name\", \"BackupPath\", \"OriginalSavePath\", \"CreatedTime\", \"SizeBytes\", \"Description\") " +
            "SELECT \"Id\", 'harness-backup-1', 'C:\\galbox\\backups\\1', 'C:\\game\\saves', '2026-01-02 03:04:05', 12345, '第一章结束后的备份 (含中文与 emoji 🎮)' FROM \"Games\" LIMIT 1;");
        Execute(
            target,
            "INSERT INTO \"SaveBackups\" (\"GameInfoId\", \"Name\", \"BackupPath\", \"OriginalSavePath\", \"CreatedTime\", \"SizeBytes\", \"Description\") " +
            "SELECT \"Id\", 'harness-backup-2', 'C:\\galbox\\backups\\2', NULL, '2026-02-03 04:05:06', 67890, NULL FROM \"Games\" LIMIT 1;");

        const string backupQuery =
            "SELECT Id, GameInfoId, Name, BackupPath, IFNULL(OriginalSavePath,''), CreatedTime, SizeBytes, IFNULL(Description,'') " +
            "FROM SaveBackups ORDER BY Id;";

        var beforeRows = QueryRows(target, backupQuery);
        Console.WriteLine($"working copy: {target} (seeded with {beforeRows.Count} save backup row(s))");
        foreach (var row in beforeRows)
        {
            Console.WriteLine($"  before: {string.Join(" | ", row)}");
        }

        Console.WriteLine();
        var result = await InitializeAsync(target).ConfigureAwait(false);
        Console.WriteLine($"initializer mode: {result.Mode}");

        var afterRows = QueryRows(target, backupQuery);
        foreach (var row in afterRows)
        {
            Console.WriteLine($"  after : {string.Join(" | ", row)}");
        }

        Console.WriteLine();
        Check(
            beforeRows.Select(r => string.Join("|", r)).SequenceEqual(afterRows.Select(r => string.Join("|", r)), StringComparer.Ordinal),
            "every seeded SaveBackups row is value-for-value identical after the table rebuild");
        Check(afterRows.Count == beforeRows.Count, "no seeded row was lost by the rebuild");
        CheckColumn(DbSnapshot.Capture(target), "SaveBackups", "SaveNodeId");

        Console.WriteLine();
    }

    // --------------------------------------------------------------------------------------------------------
    // Scenario 7: a migration whose objects all exist already, but whose history row is missing.
    // --------------------------------------------------------------------------------------------------------
    private static async Task RunFullySatisfiedMigrationScenarioAsync(HarnessOptions options)
    {
        Section("SCENARIO 7 - PENDING MIGRATION WHOSE OBJECTS ALL EXIST ALREADY (missing history row)");

        var target = Path.Combine(options.WorkDir, "already-migrated.db");
        File.Copy(options.SourceDatabasePath, target, overwrite: true);

        var first = await InitializeAsync(target).ConfigureAwait(false);
        Console.WriteLine($"setup: database migrated (mode {first.Mode}, applied [{string.Join(", ", first.AppliedNow)}])");

        // Simulates a database whose schema is already at the newest version but whose history lost the row —
        // for example after a restore, or because the objects were created by an ad-hoc ALTER TABLE.
        var pendingId = Migrations(target)[^1];
        Execute(target, $"DELETE FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '{pendingId}';");
        Console.WriteLine($"deleted the history row of '{pendingId}': it is pending again while its objects exist");
        Console.WriteLine();

        var before = DbSnapshot.Capture(target);

        var result = await InitializeAsync(target).ConfigureAwait(false);
        Console.WriteLine("--- GalboxDatabaseInitializer result --------------------------");
        Console.WriteLine(result.Describe());
        Console.WriteLine();

        var after = DbSnapshot.Capture(target);

        Console.WriteLine("--- checks -----------------------------------------------------");
        Check(result.Mode == DatabaseInitializationMode.Upgraded, $"mode is Upgraded (actual: {result.Mode})");
        Check(result.SkippedConflictOperations.Count > 0, $"the existing objects were detected ({result.SkippedConflictOperations.Count} conflict(s))");
        Check(result.AppliedNow.Contains(pendingId), "the migration was recorded as applied again");
        Check(
            after.History.Select(h => h.MigrationId).SequenceEqual(Migrations(target), StringComparer.Ordinal),
            "migration history is complete again");

        var structureDiff = DbSnapshot.StructuralDiff(before, after);
        Check(structureDiff.Count == 0, "the re-application changed nothing structurally");
        foreach (var difference in structureDiff)
        {
            Console.WriteLine($"  difference: {difference}");
        }

        var dataDiff = before.RowCounts
            // The history table is expected to change here: restoring its row is the whole point of the scenario.
            .Where(kv => !string.Equals(kv.Key, "__EFMigrationsHistory", StringComparison.OrdinalIgnoreCase))
            .Where(kv => !after.RowCounts.TryGetValue(kv.Key, out var value) || value != kv.Value)
            .Select(kv => $"{kv.Key}: {kv.Value} -> {after.RowCounts[kv.Key]}")
            .ToList();
        Check(dataDiff.Count == 0, "the re-application changed no application data row count");
        foreach (var difference in dataDiff)
        {
            Console.WriteLine($"  row count difference: {difference}");
        }

        var third = await InitializeAsync(target).ConfigureAwait(false);
        Check(third.Mode == DatabaseInitializationMode.AlreadyUpToDate, $"a further run is a no-op (actual: {third.Mode})");

        Console.WriteLine();
    }

    // --------------------------------------------------------------------------------------------------------
    // Scenario 2: a brand new database in a brand new (nested) directory.
    // --------------------------------------------------------------------------------------------------------
    private static async Task RunFreshInstallScenarioAsync(HarnessOptions options)
    {
        Section("SCENARIO 2 - FRESH INSTALL (empty directory, no database file)");

        var target = Path.Combine(options.WorkDir, "fresh-install", "nested", "galbox.db");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(target))
        {
            File.Delete(target);
        }

        Console.WriteLine($"new database path: {target}");
        Check(!File.Exists(target), "database file does not exist before initialization");
        Console.WriteLine();

        var result = await InitializeAsync(target).ConfigureAwait(false);
        Console.WriteLine("--- GalboxDatabaseInitializer result --------------------------");
        Console.WriteLine(result.Describe());
        Console.WriteLine();

        Check(result.Mode == DatabaseInitializationMode.FreshInstall, $"mode is FreshInstall (actual: {result.Mode})");
        Check(!result.BaselineWasStamped, "baseline migration was executed, not stamped, on a fresh database");
        Check(result.SchemaReport.IsConsistent, "EF model matches the freshly created database");
        Check(File.Exists(target), "database file was created");

        var snapshot = DbSnapshot.Capture(target);
        PrintSnapshot("FRESH DATABASE", snapshot);

        var expected = Migrations(target);
        Check(
            snapshot.History.Select(h => h.MigrationId).SequenceEqual(expected, StringComparer.Ordinal),
            $"all migrations recorded as applied [{string.Join(", ", expected)}]");

        foreach (var table in new[]
                 {
                     "Games", "UserSettings", "Characters", "Documents", "MediaFiles", "Screenshots",
                     "Patches", "SaveBackups", "ErrorRecords", "SaveNodes", "SaveGroups"
                 })
        {
            Check(snapshot.Columns.ContainsKey(table), $"table '{table}' exists in the fresh database");
        }

        Console.WriteLine("--- EF Core write/read round-trip on the fresh database -------");
        await using (var context = CreateContext(target))
        {
            var game = new GameInfo
            {
                NameOriginal = "Harness RoundTrip",
                NameCn = "验证用游戏",
                InstallPath = @"C:\GalboxHarness\RoundTrip",
                MainExecutable = "game.exe",
                LaunchCount = 3,
                TotalPlayTimeSeconds = 7200,
                LastSessionTime = DateTime.UtcNow
            };

            context.Games.Add(game);
            await context.SaveChangesAsync().ConfigureAwait(false);

            var group = new SaveGroup
            {
                GameInfoId = game.Id,
                Name = "A 路线",
                RouteName = "a_route",
                OrderIndex = 0
            };
            context.SaveGroups.Add(group);
            await context.SaveChangesAsync().ConfigureAwait(false);

            var node = new SaveNode
            {
                GameInfoId = game.Id,
                SaveGroupId = group.Id,
                SlotName = "slot 1",
                SceneLabel = "chapter2_nene_meeting",
                ChapterName = "第二章",
                IsSnapshot = true,
                SnapshotDescription = "第一次遇见宁宁",
                SaveModifiedTime = DateTime.UtcNow
            };

            // The parser integration seam: metadata comes from a parser, only non-null values are applied.
            node.ApplyMetadata(new SaveNodeMetadata
            {
                ChapterProgressPercent = 42,
                CgUnlockedCount = 63,
                CgTotalCount = 100,
                PlayTimeSeconds = 5400,
                SaveSizeBytes = 4096,
                SaveFileCount = 2,
                ParseStatus = SaveParseStatus.Parsed
            });

            context.SaveNodes.Add(node);
            await context.SaveChangesAsync().ConfigureAwait(false);

            var backup = new GameSaveBackup
            {
                GameInfoId = game.Id,
                SaveNodeId = node.Id,
                Name = "pre-replace backup",
                BackupPath = @"C:\GalboxHarness\backups\pre-replace",
                OriginalSavePath = @"C:\GalboxHarness\RoundTrip\game\saves",
                CreatedTime = DateTime.UtcNow,
                SizeBytes = 4096
            };
            context.SaveBackups.Add(backup);
            await context.SaveChangesAsync().ConfigureAwait(false);

            Console.WriteLine($"  inserted: Game #{game.Id}, SaveGroup #{group.Id}, SaveNode #{node.Id}, SaveBackup #{backup.Id}");
        }

        await using (var context = CreateContext(target))
        {
            var node = await context.SaveNodes
                .Include(n => n.SaveGroup)
                .Include(n => n.Backups)
                .AsNoTracking()
                .SingleAsync()
                .ConfigureAwait(false);

            Console.WriteLine(
                $"  read back: DisplayName='{node.DisplayName}' SceneLabel='{node.SceneLabel}' " +
                $"route='{node.RouteName}' groupRoute='{node.SaveGroup?.RouteName}' effectiveRoute='{node.EffectiveRouteName}'");
            Console.WriteLine(
                $"  read back: chapter={node.ChapterProgressPercent}% cg={node.CgUnlockedCount}/{node.CgTotalCount} " +
                $"({node.EffectiveCgUnlockPercent}%, remaining={node.RemainingCgCount}) playtime={node.FormattedPlayTime} " +
                $"snapshot={node.IsSnapshot} parse={node.ParseStatus} source={node.Source}");
            Console.WriteLine($"  read back: backups linked to the node = {node.Backups.Count}");

            Check(node.SceneLabel == "chapter2_nene_meeting", "SceneLabel round-tripped");
            Check(node.SaveGroup?.RouteName == "a_route", "SaveGroup route round-tripped");
            Check(node.EffectiveRouteName == "a_route", "EffectiveRouteName falls back to the group");
            Check(node.ChapterProgressPercent == 42, "ChapterProgressPercent round-tripped");
            Check(node.EffectiveCgUnlockPercent == 63, "CG rate computed from counts (63/100)");
            Check(node.RemainingCgCount == 37, "remaining CG count computed (37)");
            Check(node.ParseStatus == SaveParseStatus.Parsed, "parser metadata applied");
            Check(node.Source == SaveNodeSource.EngineScan, "source switched to EngineScan after a successful parse");
            Check(node.Backups.Count == 1, "backup is linked to its save node");

            var suggested = GameStatusDerivation.Suggest(
                await context.Games.AsNoTracking().SingleAsync().ConfigureAwait(false));
            Check(suggested == GameStatus.Completed, $"derivation suggests Completed for 2h of play time (actual: {suggested})");

            // Clean up so that the row counts printed above stay meaningful if the harness is re-run.
            context.SaveBackups.RemoveRange(context.SaveBackups);
            context.SaveNodes.RemoveRange(context.SaveNodes);
            context.SaveGroups.RemoveRange(context.SaveGroups);
            context.Games.RemoveRange(context.Games);
            await context.SaveChangesAsync().ConfigureAwait(false);
        }

        Console.WriteLine();
    }

    // --------------------------------------------------------------------------------------------------------
    // Scenario 3: running the migration path twice on the same database.
    // --------------------------------------------------------------------------------------------------------
    private static async Task RunIdempotencyScenarioAsync(HarnessOptions options)
    {
        Section("SCENARIO 3 - IDEMPOTENCY (same legacy copy migrated twice)");

        var target = Path.Combine(options.WorkDir, "idempotency.db");
        File.Copy(options.SourceDatabasePath, target, overwrite: true);
        Console.WriteLine($"working copy: {target}");
        Console.WriteLine();

        var first = await InitializeAsync(target).ConfigureAwait(false);
        Console.WriteLine("--- RUN 1 ------------------------------------------------------");
        Console.WriteLine(first.Describe());
        Console.WriteLine();

        var afterFirst = DbSnapshot.Capture(target);

        var second = await InitializeAsync(target).ConfigureAwait(false);
        Console.WriteLine("--- RUN 2 ------------------------------------------------------");
        Console.WriteLine(second.Describe());
        Console.WriteLine();

        var afterSecond = DbSnapshot.Capture(target);

        Console.WriteLine("--- checks -----------------------------------------------------");
        Check(second.Mode == DatabaseInitializationMode.AlreadyUpToDate, $"run 2 mode is AlreadyUpToDate (actual: {second.Mode})");
        Check(second.AppliedNow.Count == 0, "run 2 applied zero migrations");
        Check(!second.BaselineWasStamped, "run 2 did not stamp the baseline again");
        Check(second.PreMigrationBackupPath is null, "run 2 created no extra safety copy");

        var structureDiff = DbSnapshot.StructuralDiff(afterFirst, afterSecond);
        Check(structureDiff.Count == 0, "structure is identical after run 1 and run 2");
        foreach (var difference in structureDiff)
        {
            Console.WriteLine($"  difference: {difference}");
        }

        var countDiff = afterFirst.RowCounts
            .Where(kv => !afterSecond.RowCounts.TryGetValue(kv.Key, out var value) || value != kv.Value)
            .Select(kv => $"{kv.Key}: {kv.Value} -> {(afterSecond.RowCounts.TryGetValue(kv.Key, out var v) ? v.ToString(CultureInfo.InvariantCulture) : "missing")}")
            .ToList();
        Check(countDiff.Count == 0, "row counts are identical after run 1 and run 2");
        foreach (var difference in countDiff)
        {
            Console.WriteLine($"  row count difference: {difference}");
        }

        Check(
            afterFirst.History.Select(h => h.MigrationId).SequenceEqual(afterSecond.History.Select(h => h.MigrationId), StringComparer.Ordinal),
            "migration history has no duplicate rows after run 2");

        Console.WriteLine();
        Console.WriteLine("__EFMigrationsHistory after run 2:");
        foreach (var (id, productVersion) in afterSecond.History)
        {
            Console.WriteLine($"  {id}  (product version {productVersion})");
        }

        Console.WriteLine();
    }

    // --------------------------------------------------------------------------------------------------------
    // Scenario 4: the baseline stamp is truthful — legacy file + repair == baseline migration output.
    // --------------------------------------------------------------------------------------------------------
    private static async Task RunBaselineSchemaEquivalenceScenarioAsync(HarnessOptions options)
    {
        Section("SCENARIO 4 - BASELINE EQUIVALENCE (is the stamp truthful?)");

        var baselineOnly = Path.Combine(options.WorkDir, "baseline-only.db");
        if (File.Exists(baselineOnly))
        {
            File.Delete(baselineOnly);
        }

        string baselineId;
        using (var context = CreateContext(baselineOnly))
        {
            baselineId = context.Database.GetMigrations().First();
            Console.WriteLine($"applying ONLY the baseline migration '{baselineId}' to {baselineOnly}");
            context.GetService<IMigrator>().Migrate(baselineId);
        }

        // Run only the legacy repair (no stamping, no later migration) against a copy of the real legacy file, so
        // the comparison isolates exactly what the stamping step claims: "this file is already at the baseline".
        var repaired = Path.Combine(options.WorkDir, "baseline-repaired.db");
        File.Copy(options.SourceDatabasePath, repaired, overwrite: true);

        LegacySchemaRepairResult repairResult;
        await using (var context = CreateContext(repaired))
        {
            var baselineSchema = await BaselineSchema.CaptureAsync(context, baselineId).ConfigureAwait(false);
            repairResult = await LegacySchemaRepair.RepairAsync(context, baselineSchema).ConfigureAwait(false);
        }

        Console.WriteLine();
        Console.WriteLine($"repair added columns : [{string.Join(", ", repairResult.AddedColumns)}]");
        Console.WriteLine($"repair created tables: [{string.Join(", ", repairResult.CreatedTables)}]");
        Console.WriteLine($"repair created indexes: [{string.Join(", ", repairResult.CreatedIndexes)}]");
        Console.WriteLine($"legacy-only columns kept: [{string.Join(", ", repairResult.UnknownColumns)}]");
        Console.WriteLine();

        var legacy = DbSnapshot.Capture(options.SourceDatabasePath);
        var baseline = DbSnapshot.Capture(baselineOnly);
        var repairedSnapshot = DbSnapshot.Capture(repaired);

        Console.WriteLine("A) raw legacy file vs the baseline schema (this is why a repair is required):");
        var rawMissing = DbSnapshot.MissingObjects(legacy, baseline);
        if (rawMissing.Count == 0)
        {
            Console.WriteLine("  (the raw file already matches the baseline)");
        }
        else
        {
            foreach (var difference in rawMissing)
            {
                Console.WriteLine($"  missing from the raw file: {difference}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("B) repaired legacy file vs the baseline schema (what the stamp asserts):");
        var missing = DbSnapshot.MissingObjects(repairedSnapshot, baseline);
        if (missing.Count == 0)
        {
            Console.WriteLine("  (nothing the baseline declares is missing)");
        }
        else
        {
            foreach (var difference in missing)
            {
                Console.WriteLine($"  missing from the repaired file: {difference}");
            }
        }

        var extra = DbSnapshot.ExtraObjects(repairedSnapshot, baseline);
        if (extra.Count > 0)
        {
            Console.WriteLine("  objects the baseline does not declare, kept untouched (EF ignores them):");
            foreach (var difference in extra)
            {
                Console.WriteLine($"    {difference}");
            }
        }

        Console.WriteLine();
        Check(
            missing.Count == 0,
            "after the legacy repair nothing the baseline declares is missing (the stamp is truthful)");

        var dataLost = legacy.RowCounts
            .Where(kv => !repairedSnapshot.RowCounts.TryGetValue(kv.Key, out var value) || value != kv.Value)
            .Select(kv => $"{kv.Key}: {kv.Value} -> {(repairedSnapshot.RowCounts.TryGetValue(kv.Key, out var v) ? v.ToString(CultureInfo.InvariantCulture) : "missing")}")
            .ToList();

        Check(dataLost.Count == 0, "the repair itself changed no row count");
        foreach (var difference in dataLost)
        {
            Console.WriteLine($"  row count difference: {difference}");
        }

        Console.WriteLine();
    }

    // --------------------------------------------------------------------------------------------------------
    // helpers
    // --------------------------------------------------------------------------------------------------------

    private static async Task<DatabaseInitializationResult> InitializeAsync(string databasePath)
    {
        await using var context = CreateContext(databasePath);
        return await GalboxDatabaseInitializer.InitializeAsync(
            context,
            new DatabaseInitializationOptions { DatabasePath = databasePath }).ConfigureAwait(false);
    }

    private static GalboxDbContext CreateContext(string databasePath)
    {
        var options = new DbContextOptionsBuilder<GalboxDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;

        return new GalboxDbContext(options);
    }

    private static IReadOnlyList<string> Migrations(string databasePath)
    {
        using var context = CreateContext(databasePath);
        return context.Database.GetMigrations().ToList();
    }

    private static void CheckColumn(DbSnapshot snapshot, string table, string column)
    {
        var exists = snapshot.Columns.TryGetValue(table, out var columns) &&
                     columns.Contains(column, StringComparer.OrdinalIgnoreCase);
        Check(exists, $"column '{table}.{column}' exists");
    }

    private static void PrintSnapshot(string title, DbSnapshot snapshot)
    {
        Console.WriteLine($"--- {title} ---------------------------------");
        Console.WriteLine($"tables ({snapshot.Tables.Count}): {string.Join(", ", snapshot.Tables)}");
        foreach (var table in snapshot.Tables)
        {
            Console.WriteLine($"  {table,-24} rows={snapshot.RowCounts[table],-6} columns={snapshot.Columns[table].Count}");
        }

        Console.WriteLine($"indexes: {snapshot.Indexes.Count}; migration history rows: {snapshot.History.Count}");
        Console.WriteLine();
    }

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine("=================================================================================");
        Console.WriteLine(title);
        Console.WriteLine("=================================================================================");
    }

    private static void Check(bool condition, string description)
    {
        _checks++;
        if (!condition)
        {
            _failures++;
        }

        Console.WriteLine($"  [{(condition ? "PASS" : "FAIL")}] {description}");
    }

    private static List<string[]> QueryRows(string databasePath, string sql)
    {
        var rows = new List<string[]>();
        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var values = new string[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
            {
                values[i] = reader.IsDBNull(i) ? "(null)" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? string.Empty;
            }

            rows.Add(values);
        }

        return rows;
    }

    private static long ScalarLong(string databasePath, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
    }

    private static void Execute(string databasePath, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

/// <summary>
/// Command line options of the harness.
/// </summary>
internal sealed class HarnessOptions
{
    public string WorkDir { get; private init; } = string.Empty;

    public string SourceDatabasePath { get; private init; } = string.Empty;

    public static HarnessOptions? Parse(string[] args)
    {
        string? workDir = null;
        string? sourceDb = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--workdir" when i + 1 < args.Length:
                    workDir = args[++i];
                    break;
                case "--source-db" when i + 1 < args.Length:
                    sourceDb = args[++i];
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(workDir) || string.IsNullOrWhiteSpace(sourceDb))
        {
            return null;
        }

        return new HarnessOptions
        {
            WorkDir = Path.GetFullPath(workDir),
            SourceDatabasePath = Path.GetFullPath(sourceDb)
        };
    }
}
