using Galbox.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging;

namespace Galbox.Data.Migrations;

/// <summary>
/// How the database was brought up to date by <see cref="GalboxDatabaseInitializer"/>.
/// </summary>
public enum DatabaseInitializationMode
{
    /// <summary>
    /// The database file did not contain any tables; it was created from scratch by running all migrations.
    /// </summary>
    FreshInstall = 0,

    /// <summary>
    /// The database already contained tables but no EF Core migration history (it was created by the old
    /// <c>EnsureCreated()</c> path). The baseline migration was recorded as already applied, and the remaining
    /// migrations were applied on top.
    /// </summary>
    LegacyBaselineStamped = 1,

    /// <summary>
    /// The database already had a migration history and was missing one or more migrations, which were applied.
    /// </summary>
    Upgraded = 2,

    /// <summary>
    /// The database was already fully migrated; nothing was changed.
    /// </summary>
    AlreadyUpToDate = 3
}

/// <summary>
/// Optional behaviour for <see cref="GalboxDatabaseInitializer.InitializeAsync"/>.
/// </summary>
public class DatabaseInitializationOptions
{
    /// <summary>
    /// When true (default), a copy of the database file is made next to it before the first migration is applied
    /// to a database that already contains data. This is a pure safety net: if anything goes wrong during the
    /// upgrade, the user's original file is untouched and the copy can be restored by hand.
    /// </summary>
    public bool CreatePreMigrationBackup { get; set; } = true;

    /// <summary>
    /// Optional logger; when null nothing is logged.
    /// </summary>
    public ILogger? Logger { get; set; }

    /// <summary>
    /// Optional database file path override, used by the verification harness. When null the path is read from the
    /// context's connection string.
    /// </summary>
    public string? DatabasePath { get; set; }
}

/// <summary>
/// Outcome of a <see cref="GalboxDatabaseInitializer.InitializeAsync"/> call. Intended for logging and for the
/// verification harness, which prints it verbatim as evidence.
/// </summary>
public class DatabaseInitializationResult
{
    /// <summary>
    /// Path of the database file that was operated on.
    /// </summary>
    public string DatabasePath { get; set; } = string.Empty;

    /// <summary>
    /// How the database was handled.
    /// </summary>
    public DatabaseInitializationMode Mode { get; set; }

    /// <summary>
    /// Identifier of the baseline migration (the first migration of the assembly).
    /// </summary>
    public string BaselineMigrationId { get; set; } = string.Empty;

    /// <summary>
    /// Migrations recorded as applied before this call.
    /// </summary>
    public List<string> AppliedBefore { get; set; } = new();

    /// <summary>
    /// Migrations applied by this call.
    /// </summary>
    public List<string> AppliedNow { get; set; } = new();

    /// <summary>
    /// Migrations recorded as applied after this call.
    /// </summary>
    public List<string> AppliedAfter { get; set; } = new();

    /// <summary>
    /// True when the baseline migration was written into the migration history without executing its statements.
    /// </summary>
    public bool BaselineWasStamped { get; set; }

    /// <summary>
    /// True when <c>__EFMigrationsHistory</c> already existed before this call. False on a legacy
    /// <c>EnsureCreated()</c> database, which is exactly the case the baseline stamping handles.
    /// </summary>
    public bool HistoryTableExistedBefore { get; set; }

    /// <summary>
    /// Path of the safety copy created before migrating, if one was made.
    /// </summary>
    public string? PreMigrationBackupPath { get; set; }

    /// <summary>
    /// Columns that were missing from a legacy database and were added by <see cref="LegacySchemaRepair"/>,
    /// formatted as <c>Table.Column</c>.
    /// </summary>
    public List<string> RepairedColumns { get; set; } = new();

    /// <summary>
    /// Baseline tables that a legacy database was missing and that were created by <see cref="LegacySchemaRepair"/>.
    /// </summary>
    public List<string> CreatedTables { get; set; } = new();

    /// <summary>
    /// Operations of pending migrations that were skipped because the database already contained their target
    /// (see <see cref="TolerantMigrationRunner"/>).
    /// </summary>
    public List<string> SkippedConflictOperations { get; set; } = new();

    /// <summary>
    /// Legacy-only columns the model does not know about; they are left in place and ignored by EF.
    /// </summary>
    public List<string> UnknownColumns { get; set; } = new();

    /// <summary>
    /// Structural differences between the EF model and the real database (should be empty).
    /// </summary>
    public SchemaIntegrityReport SchemaReport { get; set; } = new();

    /// <summary>
    /// True when no migration is left to apply for the current model.
    /// </summary>
    public bool IsUpToDate => AppliedAfter.Count > 0 && SchemaReport.IsConsistent;

    /// <summary>
    /// Multi-line, human readable summary used in logs and in the verification output.
    /// </summary>
    public string Describe()
    {
        var lines = new List<string>
        {
            $"database        : {DatabasePath}",
            $"mode            : {Mode}",
            $"baseline        : {BaselineMigrationId}{(BaselineWasStamped ? " (stamped as already applied)" : string.Empty)}",
            $"history table before : {(HistoryTableExistedBefore ? "__EFMigrationsHistory existed" : "no __EFMigrationsHistory (legacy EnsureCreated database)")}",
            $"applied before  : [{string.Join(", ", AppliedBefore)}]",
            $"applied now     : [{string.Join(", ", AppliedNow)}]",
            $"applied after   : [{string.Join(", ", AppliedAfter)}]",
            $"pre-migration backup : {PreMigrationBackupPath ?? "(none)"}",
            $"repaired columns     : [{(RepairedColumns.Count == 0 ? "none needed" : string.Join(", ", RepairedColumns))}]",
            $"created tables       : [{(CreatedTables.Count == 0 ? "none needed" : string.Join(", ", CreatedTables))}]",
            $"skipped conflicting operations : {SkippedConflictOperations.Count}",
            $"schema integrity: {(SchemaReport.IsConsistent ? "OK (model matches database)" : "MISMATCH")}"
        };

        foreach (var conflict in SkippedConflictOperations)
        {
            lines.Add($"  ! {conflict}");
        }

        foreach (var column in UnknownColumns)
        {
            lines.Add($"  i column not declared by the baseline, left untouched: {column}");
        }

        foreach (var problem in SchemaReport.Problems())
        {
            lines.Add($"  ! {problem}");
        }

        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
/// Brings the Galbox SQLite database up to date at application start-up, replacing the old
/// <c>EnsureCreated()</c> + ad-hoc <c>ALTER TABLE</c> code.
/// </summary>
/// <remarks>
/// <para><b>Why this exists.</b> The application was originally shipped with <c>Database.EnsureCreated()</c> and
/// therefore has real user databases in the wild (<c>%LocalAppData%\Galbox\galbox.db</c>) that have all the tables
/// but <b>no</b> <c>__EFMigrationsHistory</c> table. Calling <c>Database.Migrate()</c> on such a database would try
/// to re-create the existing tables and fail (or, worse, leave the schema half-built).</para>
/// <para><b>The strategy</b> (see also <see cref="InitializeAsync"/>):</para>
/// <list type="number">
/// <item><description><b>No tables at all</b> → normal EF migration run; the database is created from scratch.</description></item>
/// <item><description><b>Tables but no migration history</b> → the database is a legacy <c>EnsureCreated()</c>
/// database. The history table is created and the <i>baseline</i> migration (which describes exactly the schema
/// <c>EnsureCreated()</c> produced) is inserted into it <b>without running any DDL</b>. From then on the database
/// is a normal migrated database and only the newer migrations are applied. No existing row is touched.</description></item>
/// <item><description><b>Tables and a migration history</b> → plain <c>Migrate()</c>, which is a no-op when the
/// database is already current.</description></item>
/// </list>
/// <para>
/// Every step is idempotent: running start-up twice produces the same database, and the second run reports
/// <see cref="DatabaseInitializationMode.AlreadyUpToDate"/> with zero pending migrations.
/// </para>
/// </remarks>
public static class GalboxDatabaseInitializer
{
    /// <summary>
    /// Name of the EF Core migration history table. It is the same for every relational provider and is created by
    /// EF itself through <see cref="IHistoryRepository"/>; the literal is only used to probe for its existence.
    /// </summary>
    public const string MigrationsHistoryTable = "__EFMigrationsHistory";

    /// <summary>
    /// Creates the database / brings it up to date. Safe to call on every application start-up.
    /// </summary>
    /// <param name="context">The database context to initialize.</param>
    /// <param name="options">Optional behaviour (logging, pre-migration backup, path override).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A report describing what was done; see <see cref="DatabaseInitializationResult"/>.</returns>
    /// <exception cref="GalboxDatabaseInitializationException">
    /// Thrown when the target file contains tables that are not a Galbox database (refusing to touch it), or when
    /// the migration history is inconsistent with the migrations in the assembly.
    /// </exception>
    public static async Task<DatabaseInitializationResult> InitializeAsync(
        GalboxDbContext context,
        DatabaseInitializationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var opts = options ?? new DatabaseInitializationOptions();
        var logger = opts.Logger;

        var result = new DatabaseInitializationResult
        {
            DatabasePath = opts.DatabasePath ?? TryGetDatabasePath(context) ?? "(unknown)"
        };

        // All migrations known to the assembly, in application order.
        var allMigrations = context.Database.GetMigrations().ToList();
        if (allMigrations.Count == 0)
        {
            throw new GalboxDatabaseInitializationException(
                "No EF Core migrations were found in Galbox.Data. The database cannot be upgraded safely.");
        }

        // The baseline is the first migration ever added; it describes the pre-migration schema.
        result.BaselineMigrationId = allMigrations[0];

        var connection = context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        var hasAnyTable = await AnyTableExistsAsync(context, cancellationToken).ConfigureAwait(false);
        var historyExists = await TableExistsAsync(
            context,
            MigrationsHistoryTable,
            cancellationToken).ConfigureAwait(false);

        var appliedBefore = (await context.Database.GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(false))
            .ToList();
        result.AppliedBefore = appliedBefore;
        result.HistoryTableExistedBefore = historyExists;

        if (!hasAnyTable)
        {
            // Case 1: fresh install. Migrate() creates the schema and the history table.
            result.Mode = DatabaseInitializationMode.FreshInstall;
            logger?.LogInformation("Galbox database not found at {Path}; creating it from migrations.", result.DatabasePath);
        }
        else if (!appliedBefore.Contains(result.BaselineMigrationId, StringComparer.Ordinal))
        {
            // Case 2: legacy EnsureCreated() database (with or without an empty history table).
            await GuardAgainstForeignDatabaseAsync(context, cancellationToken).ConfigureAwait(false);

            result.Mode = DatabaseInitializationMode.LegacyBaselineStamped;

            var pending = (await context.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false))
                .ToList();

            if (pending.Count > 0)
            {
                // Safety net: copy the file before the first statement is executed against real user data.
                result.PreMigrationBackupPath = await TryCreatePreMigrationBackupAsync(opts, result.DatabasePath, logger)
                    .ConfigureAwait(false);
            }

            // The baseline describes the schema EnsureCreated() was meant to produce, but a legacy database may
            // have been created by an older model (EnsureCreated never updates an existing schema). Make the
            // baseline true before recording it as applied, otherwise the history would lie about the file — and
            // the application would keep failing on the columns it selects.
            //
            // The expectation comes from the baseline migration itself (captured in a throw-away database), never
            // from the live model: adding a column that a still-pending migration owns would make that migration
            // fail with "duplicate column name".
            var baselineSchema = await BaselineSchema
                .CaptureAsync(context, result.BaselineMigrationId, cancellationToken)
                .ConfigureAwait(false);

            var repair = await LegacySchemaRepair
                .RepairAsync(context, baselineSchema, logger, cancellationToken)
                .ConfigureAwait(false);

            result.RepairedColumns = repair.AddedColumns;
            result.CreatedTables = repair.CreatedTables;
            result.UnknownColumns = repair.UnknownColumns;

            var stamped = await StampBaselineAsync(context, result.BaselineMigrationId, logger, cancellationToken)
                .ConfigureAwait(false);
            result.BaselineWasStamped = stamped;
        }
        else
        {
            result.Mode = DatabaseInitializationMode.Upgraded;
        }

        // Apply everything that is still pending (a no-op when the database is already current).
        var pendingAfterStamp = (await context.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false))
            .ToList();

        if (pendingAfterStamp.Count > 0)
        {
            if (result.PreMigrationBackupPath is null &&
                result.Mode != DatabaseInitializationMode.FreshInstall &&
                opts.CreatePreMigrationBackup)
            {
                result.PreMigrationBackupPath = await TryCreatePreMigrationBackupAsync(opts, result.DatabasePath, logger)
                    .ConfigureAwait(false);
            }

            // Tolerate databases that already contain some of the objects a pending migration would create
            // (typically because a column was added by an ad-hoc ALTER TABLE outside the migrations).
            var plan = await TolerantMigrationRunner
                .BuildPlanAsync(context, pendingAfterStamp, cancellationToken)
                .ConfigureAwait(false);

            if (plan.HasConflicts)
            {
                result.SkippedConflictOperations = plan.ConflictDescriptions;

                foreach (var conflict in plan.ConflictDescriptions)
                {
                    logger?.LogWarning("Migration conflict: {Conflict}", conflict);
                }

                result.AppliedNow = await TolerantMigrationRunner
                    .ApplyPlanAsync(context, plan, logger, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                foreach (var migrationId in pendingAfterStamp)
                {
                    logger?.LogInformation("Applying migration {Migration}.", migrationId);
                }

                await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
                result.AppliedNow = pendingAfterStamp;
            }

            if (result.Mode == DatabaseInitializationMode.Upgraded)
            {
                // stays Upgraded
            }
        }
        else if (result.Mode == DatabaseInitializationMode.Upgraded)
        {
            result.Mode = DatabaseInitializationMode.AlreadyUpToDate;
        }

        result.AppliedAfter = (await context.Database.GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(false))
            .ToList();

        // Model vs reality: catches a legacy database that was missing something the baseline assumed.
        result.SchemaReport = await SchemaIntegrityChecker.CheckAsync(context, cancellationToken).ConfigureAwait(false);

        if (!result.SchemaReport.IsConsistent)
        {
            foreach (var problem in result.SchemaReport.Problems())
            {
                logger?.LogWarning("Database schema problem: {Problem}", problem);
            }
        }

        logger?.LogInformation("Galbox database ready:{NewLine}{Report}", Environment.NewLine, result.Describe());
        return result;
    }

    /// <summary>
    /// Writes the baseline migration into <c>__EFMigrationsHistory</c> without executing its statements,
    /// so that later migrations can be applied to a database that was originally created by <c>EnsureCreated()</c>.
    /// </summary>
    private static async Task<bool> StampBaselineAsync(
        GalboxDbContext context,
        string baselineMigrationId,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        var historyRepository = context.GetService<IHistoryRepository>();

        // Idempotency guard: never insert the same row twice.
        var applied = await context.Database.GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(false);
        if (applied.Contains(baselineMigrationId, StringComparer.Ordinal))
        {
            return false;
        }

        var connection = context.Database.GetDbConnection();

        await using (var createCommand = connection.CreateCommand())
        {
            createCommand.CommandText = historyRepository.GetCreateIfNotExistsScript();
            await createCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var insertScript = historyRepository.GetInsertScript(
            new HistoryRow(baselineMigrationId, ProductInfo.GetVersion()));

        await using (var insertCommand = connection.CreateCommand())
        {
            insertCommand.CommandText = insertScript;
            await insertCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        logger?.LogInformation(
            "Existing database detected without migration history; baseline {Baseline} marked as applied (no DDL executed).",
            baselineMigrationId);

        return true;
    }

    /// <summary>
    /// Copies the database file next to itself before a migration run. Best effort: a failure is logged and
    /// ignored, because the copy is an extra safety net and not a prerequisite for migrating.
    /// </summary>
    private static Task<string?> TryCreatePreMigrationBackupAsync(
        DatabaseInitializationOptions options,
        string databasePath,
        ILogger? logger)
    {
        if (!options.CreatePreMigrationBackup)
        {
            return Task.FromResult<string?>(null);
        }

        try
        {
            if (string.IsNullOrEmpty(databasePath) || !File.Exists(databasePath))
            {
                return Task.FromResult<string?>(null);
            }

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var backupPath = $"{databasePath}.pre-migration-{stamp}.bak";

            // Keep only the newest few copies so that repeated start-ups cannot fill the disk.
            var directory = Path.GetDirectoryName(databasePath);
            if (!string.IsNullOrEmpty(directory))
            {
                var oldBackups = Directory
                    .GetFiles(directory, $"{Path.GetFileName(databasePath)}.pre-migration-*.bak")
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .Skip(4)
                    .ToList();

                foreach (var old in oldBackups)
                {
                    try
                    {
                        File.Delete(old);
                    }
                    catch (IOException)
                    {
                        // Ignore: an old safety copy that cannot be deleted is not a reason to fail the upgrade.
                    }
                }
            }

            File.Copy(databasePath, backupPath, overwrite: false);
            logger?.LogInformation("Pre-migration safety copy created: {BackupPath}", backupPath);
            return Task.FromResult<string?>(backupPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(ex, "Could not create a pre-migration safety copy of {DatabasePath}.", databasePath);
            return Task.FromResult<string?>(null);
        }
    }

    /// <summary>
    /// Refuses to migrate a file that is not a Galbox database (tables exist but the expected core tables do not),
    /// so that a wrong connection string can never damage an unrelated SQLite file.
    /// </summary>
    private static async Task GuardAgainstForeignDatabaseAsync(
        GalboxDbContext context,
        CancellationToken cancellationToken)
    {
        var hasGames = await TableExistsAsync(context, "Games", cancellationToken).ConfigureAwait(false);
        var hasSettings = await TableExistsAsync(context, "UserSettings", cancellationToken).ConfigureAwait(false);

        if (!hasGames || !hasSettings)
        {
            throw new GalboxDatabaseInitializationException(
                $"The database at '{TryGetDatabasePath(context)}' contains tables but is not a Galbox database " +
                "(the Games and/or UserSettings tables are missing). Refusing to modify it.");
        }
    }

    private static async Task<bool> AnyTableExistsAsync(GalboxDbContext context, CancellationToken cancellationToken)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';";

        var scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(scalar, System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    internal static async Task<bool> TableExistsAsync(
        GalboxDbContext context,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";

        var parameter = command.CreateParameter();
        parameter.ParameterName = "$name";
        parameter.Value = tableName;
        command.Parameters.Add(parameter);

        var scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(scalar, System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    private static string? TryGetDatabasePath(GalboxDbContext context)
    {
        var connectionString = context.Database.GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString);
        return string.IsNullOrWhiteSpace(builder.DataSource) ? null : builder.DataSource;
    }
}

/// <summary>
/// Thrown when the database cannot be migrated safely. The application must not silently continue in that case,
/// because writing through a stale schema would corrupt or lose user data.
/// </summary>
public class GalboxDatabaseInitializationException : Exception
{
    /// <summary>
    /// Creates the exception with a message.
    /// </summary>
    /// <param name="message">Explanation of what is wrong.</param>
    public GalboxDatabaseInitializationException(string message) : base(message)
    {
    }

    /// <summary>
    /// Creates the exception with a message and an inner exception.
    /// </summary>
    /// <param name="message">Explanation of what is wrong.</param>
    /// <param name="innerException">The underlying failure.</param>
    public GalboxDatabaseInitializationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
