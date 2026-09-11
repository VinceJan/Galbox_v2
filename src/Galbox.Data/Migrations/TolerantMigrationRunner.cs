using Galbox.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Galbox.Data.Migrations;

/// <summary>
/// One still-pending migration together with the operations it would execute.
/// </summary>
public sealed class PlannedMigration
{
    /// <summary>Migration identifier.</summary>
    public string MigrationId { get; init; } = string.Empty;

    /// <summary>Operations the migration performs, in order.</summary>
    public IReadOnlyList<MigrationOperation> Operations { get; init; } = Array.Empty<MigrationOperation>();

    /// <summary>
    /// Runtime-initialized target model of the migration. It is required by the SQL generation step, because the
    /// SQLite provider needs it to rebuild a table for operations SQLite cannot express directly (for example
    /// adding a foreign key to an existing table).
    /// </summary>
    public IModel? TargetModel { get; init; }

    /// <summary>Operations whose target object already exists (or is already gone) in the database.</summary>
    public List<string> AlreadySatisfied { get; } = new();
}

/// <summary>
/// Outcome of planning the pending migrations against the real database.
/// </summary>
public sealed class MigrationExecutionPlan
{
    /// <summary>Pending migrations with their operations.</summary>
    public List<PlannedMigration> Migrations { get; } = new();

    /// <summary>
    /// True when at least one operation of a pending migration is already satisfied by the database — i.e. the
    /// database contains objects that a migration is about to create (typically because they were added by hand).
    /// </summary>
    public bool HasConflicts => Migrations.Any(m => m.AlreadySatisfied.Count > 0);

    /// <summary>
    /// True when every conflicting operation is of a kind that can safely be skipped (independent objects:
    /// columns, tables, indexes). When false, the migration must not be patched and the initializer fails loudly
    /// instead of risking a half-applied migration.
    /// </summary>
    public bool CanSkipConflicts { get; set; } = true;

    /// <summary>Human readable list of the conflicts found.</summary>
    public List<string> ConflictDescriptions { get; } = new();
}

/// <summary>
/// Plans and (when necessary) applies pending EF Core migrations in a way that tolerates a database which already
/// contains some of the objects a migration would create.
/// </summary>
/// <remarks>
/// <para>
/// <b>The problem.</b> EF's migrator blindly executes every operation of every pending migration. If a column was
/// added to a database outside the migrations (this repository has two such precedents: <c>Games.EngineType</c> and
/// <c>Games.VndbId</c> were added by an ad-hoc <c>ALTER TABLE</c> loop in <c>App.xaml.cs</c>), a later migration that
/// also adds that column fails with <c>duplicate column name</c> and the application cannot start.
/// </para>
/// <para>
/// <b>The normal case.</b> When no pending operation is already satisfied — which is the case for every database
/// that was upgraded through this initializer, and for fresh installs — <see cref="GalboxDatabaseInitializer"/>
/// simply calls EF's own <c>Database.Migrate()</c>. Nothing is reinvented.
/// </para>
/// <para>
/// <b>The tolerant case.</b> When conflicts exist, the operation list of each pending migration is filtered: an
/// operation is skipped when its target already exists (column, table, index) or is already gone (dropped objects
/// that are no longer there). Skipping is only allowed for independent, object-scoped operations
/// (<see cref="AddColumnOperation"/>, <see cref="CreateTableOperation"/>, <see cref="CreateIndexOperation"/> and the
/// matching drops); anything else — column type changes, foreign key changes, raw SQL — makes the plan
/// non-tolerable and the initializer throws a precise error instead of guessing. The remaining operations are
/// generated into SQL by EF's own <see cref="IMigrationsSqlGenerator"/> and executed inside the same kind of
/// per-migration transaction EF uses, followed by the migration's history row, so the history stays truthful.
/// </para>
/// </remarks>
public static class TolerantMigrationRunner
{
    /// <summary>
    /// Builds the execution plan for the pending migrations.
    /// </summary>
    /// <param name="context">Context whose database is inspected.</param>
    /// <param name="pendingMigrations">Pending migration identifiers, in application order.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The plan; <see cref="MigrationExecutionPlan.HasConflicts"/> tells whether tolerance is needed.</returns>
    public static async Task<MigrationExecutionPlan> BuildPlanAsync(
        GalboxDbContext context,
        IReadOnlyList<string> pendingMigrations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(pendingMigrations);

        var plan = new MigrationExecutionPlan();
        if (pendingMigrations.Count == 0)
        {
            return plan;
        }

        var assembly = context.GetService<IMigrationsAssembly>();
        var modelDiffer = context.GetService<IMigrationsModelDiffer>();
        var providerName = context.Database.ProviderName
                           ?? throw new GalboxDatabaseInitializationException("The database provider is not configured.");

        var state = await DatabaseSchemaState.CaptureAsync(context, cancellationToken).ConfigureAwait(false);

        // The starting point is the model of the last applied migration (null when none has been applied).
        var applied = (await context.Database.GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(false))
            .ToList();

        IRelationalModel? sourceModel = null;
        if (applied.Count > 0)
        {
            var lastApplied = CreateMigration(assembly, applied[^1], providerName);
            sourceModel = InitializeModel(context, lastApplied).GetRelationalModel();
        }

        foreach (var migrationId in pendingMigrations)
        {
            var migration = CreateMigration(assembly, migrationId, providerName);
            var targetModel = InitializeModel(context, migration);
            var operations = modelDiffer.GetDifferences(sourceModel, targetModel.GetRelationalModel());

            var planned = new PlannedMigration
            {
                MigrationId = migrationId,
                Operations = operations,
                TargetModel = targetModel
            };

            foreach (var operation in operations)
            {
                if (!IsAlreadySatisfied(operation, state))
                {
                    continue;
                }

                var description = Describe(operation);
                planned.AlreadySatisfied.Add(description);

                if (!IsSkippable(operation))
                {
                    plan.CanSkipConflicts = false;
                }

                // A column that exists with an incompatible storage class must not be silently accepted:
                // EF would keep writing values of the wrong kind into it.
                if (operation is AddColumnOperation addColumn &&
                    !string.IsNullOrWhiteSpace(addColumn.ColumnType) &&
                    state.GetColumnType(addColumn.Table, addColumn.Name) is { } actualType &&
                    !string.Equals(
                        DatabaseSchemaState.GetAffinity(actualType),
                        DatabaseSchemaState.GetAffinity(addColumn.ColumnType),
                        StringComparison.Ordinal))
                {
                    plan.CanSkipConflicts = false;
                    plan.ConflictDescriptions.Add(
                        $"{migrationId}: column {addColumn.Table}.{addColumn.Name} already exists but is declared " +
                        $"as '{actualType}' while the migration expects '{addColumn.ColumnType}'");
                }

                plan.ConflictDescriptions.Add($"{migrationId}: {description} already exists in the database");
            }

            // Data operations are executed from generated SQL with literal values, so a migration that moves row
            // data must not be patched by hand.
            if (operations.Any(op => op is InsertDataOperation or UpdateDataOperation or DeleteDataOperation))
            {
                plan.CanSkipConflicts = false;
                plan.ConflictDescriptions.Add(
                    $"{migrationId}: the migration contains a data operation and cannot be applied partially");
            }

            plan.Migrations.Add(planned);
            sourceModel = targetModel.GetRelationalModel();
        }

        return plan;
    }

    /// <summary>
    /// Applies the planned migrations, skipping the operations whose target already exists.
    /// </summary>
    /// <param name="context">Context to migrate.</param>
    /// <param name="plan">Plan produced by <see cref="BuildPlanAsync"/>.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The identifiers of the migrations that were applied.</returns>
    /// <exception cref="GalboxDatabaseInitializationException">
    /// Thrown when a conflict cannot be skipped safely, so that no half-applied migration can corrupt user data.
    /// </exception>
    public static async Task<List<string>> ApplyPlanAsync(
        GalboxDbContext context,
        MigrationExecutionPlan plan,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);

        if (!plan.CanSkipConflicts)
        {
            throw new GalboxDatabaseInitializationException(
                "The database already contains objects that pending migrations would create, and the conflicting " +
                "operations cannot be skipped safely:" + Environment.NewLine +
                string.Join(Environment.NewLine, plan.ConflictDescriptions) + Environment.NewLine +
                "No migration was applied. Restore the pre-migration safety copy next to the database file, or " +
                "inspect the listed objects by hand before upgrading.");
        }

        var generator = context.GetService<IMigrationsSqlGenerator>();
        var historyRepository = context.GetService<IHistoryRepository>();
        var relationalConnection = context.GetService<IRelationalConnection>();
        var applied = new List<string>();

        foreach (var plannedMigration in plan.Migrations)
        {
            // The database changes as migrations are applied, so the state is re-read before each one.
            var state = await DatabaseSchemaState.CaptureAsync(context, cancellationToken).ConfigureAwait(false);

            var operationsToRun = plannedMigration.Operations
                .Where(op => !IsAlreadySatisfied(op, state))
                .ToList();

            var skipped = plannedMigration.Operations.Count - operationsToRun.Count;

            await using var transaction = await relationalConnection
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

            try
            {
                if (operationsToRun.Count > 0)
                {
                    // The migration's own target model is passed along: the SQLite provider needs it to rebuild a
                    // table for operations SQLite cannot apply in place (adding a foreign key, altering a column).
                    var commands = generator.Generate(operationsToRun, plannedMigration.TargetModel);

                    foreach (var command in commands)
                    {
                        // Migration DDL is emitted with literal values (no parameters) by the SQLite generator;
                        // data operations, which could need parameters, make the plan non-tolerable in BuildPlanAsync.
                        await context.Database
                            .ExecuteSqlRawAsync(command.CommandText, cancellationToken)
                            .ConfigureAwait(false);
                    }
                }

                // Record the migration exactly as EF would, so the history stays authoritative.
                await context.Database
                    .ExecuteSqlRawAsync(
                        historyRepository.GetInsertScript(new HistoryRow(plannedMigration.MigrationId, ProductInfo.GetVersion())),
                        cancellationToken)
                    .ConfigureAwait(false);

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                throw;
            }

            applied.Add(plannedMigration.MigrationId);
            logger?.LogWarning(
                "Migration {Migration} applied with {Skipped} operation(s) skipped because the database already " +
                "contained their target ({Total} executed).",
                plannedMigration.MigrationId,
                skipped,
                operationsToRun.Count);
        }

        return applied;
    }

    private static Migration CreateMigration(
        IMigrationsAssembly assembly,
        string migrationId,
        string providerName)
    {
        if (!assembly.Migrations.TryGetValue(migrationId, out var migrationType))
        {
            throw new GalboxDatabaseInitializationException(
                $"Migration '{migrationId}' is recorded in or requested from the database but does not exist in the " +
                "assembly. The database was probably produced by a different build.");
        }

        return assembly.CreateMigration(migrationType, providerName);
    }

    /// <summary>
    /// Gets the runtime-initialized target model of a migration. The model built from a migration's designer file
    /// is not runtime-initialized yet, so it must be initialized before it can be used by the model differ or by
    /// the SQL generator.
    /// </summary>
    private static IModel InitializeModel(GalboxDbContext context, Migration migration)
    {
        var initializer = context.GetService<IModelRuntimeInitializer>();
        return initializer.Initialize(migration.TargetModel, designTime: true, validationLogger: null);
    }

    /// <summary>
    /// True when the object an operation targets already exists (create operations) or is already gone (drop
    /// operations), i.e. running it would fail or is unnecessary.
    /// </summary>
    private static bool IsAlreadySatisfied(MigrationOperation operation, DatabaseSchemaState state) => operation switch
    {
        AddColumnOperation op => state.HasColumn(op.Table, op.Name),
        CreateTableOperation op => state.HasTable(op.Name),
        CreateIndexOperation op => state.Indexes.Contains(op.Name),
        DropColumnOperation op => !state.HasColumn(op.Table, op.Name),
        DropTableOperation op => !state.HasTable(op.Name),
        DropIndexOperation op when op.Name is not null => !state.Indexes.Contains(op.Name),
        _ => false
    };

    /// <summary>
    /// True for operations that only create/drop one independent object, and can therefore be skipped when that
    /// object is already in the expected state.
    /// </summary>
    private static bool IsSkippable(MigrationOperation operation) => operation
        is AddColumnOperation
        or CreateTableOperation
        or CreateIndexOperation
        or DropColumnOperation
        or DropTableOperation
        or DropIndexOperation;

    private static string Describe(MigrationOperation operation) => operation switch
    {
        AddColumnOperation op => $"column {op.Table}.{op.Name}",
        CreateTableOperation op => $"table {op.Name}",
        CreateIndexOperation op => $"index {op.Name}",
        DropColumnOperation op => $"absence of column {op.Table}.{op.Name}",
        DropTableOperation op => $"absence of table {op.Name}",
        DropIndexOperation op => $"absence of index {op.Name}",
        _ => operation.GetType().Name
    };
}
