using System.Text;
using Galbox.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Galbox.Data.Migrations;

/// <summary>
/// Outcome of <see cref="LegacySchemaRepair.RepairAsync"/>.
/// </summary>
public class LegacySchemaRepairResult
{
    /// <summary>
    /// Columns that were added, formatted as <c>Table.Column</c>.
    /// </summary>
    public List<string> AddedColumns { get; } = new();

    /// <summary>
    /// Tables that were missing and were created from the baseline DDL.
    /// </summary>
    public List<string> CreatedTables { get; } = new();

    /// <summary>
    /// Indexes that were created together with a missing table.
    /// </summary>
    public List<string> CreatedIndexes { get; } = new();

    /// <summary>
    /// The exact DDL statements that were executed (kept for logging and verification evidence).
    /// </summary>
    public List<string> ExecutedStatements { get; } = new();

    /// <summary>
    /// Columns the database has that the baseline does not declare — either columns of an older model that is no
    /// longer used, or objects added outside the migration system (including objects owned by a still pending
    /// migration). They are left completely untouched.
    /// </summary>
    public List<string> UnknownColumns { get; } = new();

    /// <summary>
    /// True when the repair did not have to change anything.
    /// </summary>
    public bool IsNoOp => AddedColumns.Count == 0 && CreatedTables.Count == 0;
}

/// <summary>
/// Brings an old <c>EnsureCreated()</c> database up to the structure the baseline migration claims it already has.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is necessary.</b> <c>EnsureCreated()</c> creates the schema once and never updates it: when the
/// model later gained properties, existing databases never received the matching columns. The live user database
/// is a real example — it is missing <c>UserSettings.BangumiAuthMethod</c>, <c>UserSettings.BangumiRefreshToken</c>,
/// <c>UserSettings.BangumiTokenExpiresAt</c>, <c>UserSettings.BangumiUsername</c> and
/// <c>UserSettings.GameDirectoriesJson</c>, so <c>SELECT BangumiAuthMethod FROM UserSettings</c> fails with
/// <c>no such column</c> against it. Recording such a database as "already at the baseline" would be a lie, and the
/// application would keep failing on the columns it selects.
/// </para>
/// <para>
/// <b>What it does.</b> For every table and column declared by the <i>baseline</i> (see
/// <see cref="BaselineSchema"/>, captured by actually running the baseline migration against a throw-away file):
/// <list type="bullet">
/// <item><description>missing table → created with the exact <c>CREATE TABLE</c> statement EF Core generated, then
/// its indexes are created;</description></item>
/// <item><description>missing column → <c>ALTER TABLE ... ADD COLUMN</c> with the same storage type, NOT NULL flag
/// and default as the baseline declared (SQLite requires a value for a NOT NULL column on a table that already has
/// rows, so a type-appropriate default is used when the baseline has none).</description></item>
/// </list>
/// </para>
/// <para>
/// <b>What it deliberately does not do.</b> It never adds anything that belongs to a later migration — those
/// migrations are still pending and add their own objects, so doing their work here would fail with
/// <c>duplicate column name</c>. It also never drops, renames or rewrites an existing column, and it never touches
/// columns the baseline does not know about (for example <c>Games.VndbId</c> or <c>UserSettings.LastModified</c>,
/// which were added by hand); those keep their data and are simply ignored by EF.
/// </para>
/// <para>
/// The whole repair is idempotent: it compares the live database with the baseline first, so a second run (or a run
/// against an already-correct database) executes no statement at all.
/// </para>
/// </remarks>
public static class LegacySchemaRepair
{
    /// <summary>
    /// Adds missing baseline tables and columns to a legacy database.
    /// </summary>
    /// <param name="context">Context whose connection is the legacy database.</param>
    /// <param name="baselineSchema">The structure the baseline migration produces.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What was created or added, and the statements that ran.</returns>
    public static async Task<LegacySchemaRepairResult> RepairAsync(
        GalboxDbContext context,
        BaselineSchema baselineSchema,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(baselineSchema);

        var result = new LegacySchemaRepairResult();

        var connection = context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        var actualTables = await SchemaIntegrityChecker
            .ListTablesAsync(context, cancellationToken)
            .ConfigureAwait(false);

        var actualTableLookup = new HashSet<string>(actualTables, StringComparer.OrdinalIgnoreCase);
        var actualIndexes = await ListIndexesAsync(context, cancellationToken).ConfigureAwait(false);

        // Pass 1: tables that the baseline declares but the file does not have.
        foreach (var table in baselineSchema.Tables.Values)
        {
            if (actualTableLookup.Contains(table.Name))
            {
                continue;
            }

            await ExecuteAsync(connection, table.CreateSql, cancellationToken).ConfigureAwait(false);
            result.CreatedTables.Add(table.Name);
            result.ExecutedStatements.Add(table.CreateSql);
            logger?.LogWarning(
                "Legacy database was missing table {Table}; created it from the baseline DDL.",
                table.Name);
        }

        // Pass 2: columns (on tables that exist, including the ones just created) and their indexes.
        foreach (var table in baselineSchema.Tables.Values)
        {
            var actualColumns = await SchemaIntegrityChecker
                .ListColumnsAsync(context, table.Name, cancellationToken)
                .ConfigureAwait(false);

            foreach (var column in table.Columns)
            {
                if (actualColumns.Contains(column.Name))
                {
                    continue;
                }

                var statement = BuildAddColumnStatement(table.Name, column);
                await ExecuteAsync(connection, statement, cancellationToken).ConfigureAwait(false);

                result.AddedColumns.Add($"{table.Name}.{column.Name}");
                result.ExecutedStatements.Add(statement);
                logger?.LogWarning(
                    "Legacy database was missing column {Column}; added it with: {Statement}",
                    $"{table.Name}.{column.Name}",
                    statement);
            }

            foreach (var (indexName, indexSql) in table.Indexes)
            {
                if (actualIndexes.Contains(indexName))
                {
                    continue;
                }

                await ExecuteAsync(connection, indexSql, cancellationToken).ConfigureAwait(false);
                result.CreatedIndexes.Add(indexName);
                result.ExecutedStatements.Add(indexSql);
            }

            foreach (var actualColumn in actualColumns)
            {
                if (!table.Columns.Any(c => string.Equals(c.Name, actualColumn, StringComparison.OrdinalIgnoreCase)))
                {
                    result.UnknownColumns.Add($"{table.Name}.{actualColumn}");
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Builds the <c>ALTER TABLE ... ADD COLUMN</c> statement for one baseline column.
    /// </summary>
    /// <param name="tableName">Name of the existing table.</param>
    /// <param name="column">The baseline column to add.</param>
    /// <returns>A DDL statement that is safe to run on a table that already contains rows.</returns>
    internal static string BuildAddColumnStatement(string tableName, BaselineColumn column)
    {
        var storeType = string.IsNullOrWhiteSpace(column.StoreType) ? "TEXT" : column.StoreType;

        var builder = new StringBuilder();
        builder.Append("ALTER TABLE \"").Append(EscapeIdentifier(tableName)).Append('"');
        builder.Append(" ADD COLUMN \"").Append(EscapeIdentifier(column.Name)).Append("\" ");
        builder.Append(storeType);

        if (column.IsNotNull)
        {
            builder.Append(" NOT NULL DEFAULT ").Append(ResolveDefaultLiteral(column, storeType));
        }

        return builder.ToString();
    }

    private static string ResolveDefaultLiteral(BaselineColumn column, string storeType)
    {
        if (!string.IsNullOrWhiteSpace(column.DefaultValueSql))
        {
            return column.DefaultValueSql!;
        }

        // SQLite refuses a NOT NULL column without a default on a non-empty table, so fall back to the
        // "empty" value of the storage class.
        return storeType.ToUpperInvariant() switch
        {
            "TEXT" => "''",
            "BLOB" => "X''",
            "REAL" => "0.0",
            _ => "0"
        };
    }

    private static async Task<HashSet<string>> ListIndexesAsync(
        GalboxDbContext context,
        CancellationToken cancellationToken)
    {
        var indexes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index' AND name NOT LIKE 'sqlite_%';";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            indexes.Add(reader.GetString(0));
        }

        return indexes;
    }

    private static async Task ExecuteAsync(
        System.Data.Common.DbConnection connection,
        string statement,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = statement;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string EscapeIdentifier(string identifier) => identifier.Replace("\"", "\"\"");
}
