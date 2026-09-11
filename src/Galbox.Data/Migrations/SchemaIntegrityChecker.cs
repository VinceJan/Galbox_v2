using System.Globalization;
using Galbox.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Galbox.Data.Migrations;

/// <summary>
/// Result of comparing the EF Core model with the real SQLite schema.
/// </summary>
/// <remarks>
/// Used after every start-up migration to prove that the database the application is about to write to really has
/// the tables and columns the model expects. This is the check that catches the worst legacy scenario: a database
/// created by <c>EnsureCreated()</c> from an <i>older</i> model version, which the baseline stamping would otherwise
/// silently adopt.
/// </remarks>
public class SchemaIntegrityReport
{
    /// <summary>
    /// Tables the model needs but the database does not have.
    /// </summary>
    public List<string> MissingTables { get; } = new();

    /// <summary>
    /// Columns the model needs but the database does not have, formatted as <c>Table.Column</c>.
    /// </summary>
    public List<string> MissingColumns { get; } = new();

    /// <summary>
    /// Columns present in the database that the model does not define, formatted as <c>Table.Column</c>.
    /// Informational only — EF ignores unknown columns and they are never dropped automatically.
    /// </summary>
    public List<string> ExtraColumns { get; } = new();

    /// <summary>
    /// Tables present in the database that the model does not define. Informational only — extra tables are
    /// never deleted automatically.
    /// </summary>
    public List<string> ExtraTables { get; } = new();

    /// <summary>
    /// True when the database has everything the model needs.
    /// </summary>
    public bool IsConsistent => MissingTables.Count == 0 && MissingColumns.Count == 0;

    /// <summary>
    /// Human readable list of the problems found (empty when the schema is consistent).
    /// </summary>
    /// <returns>One line per problem.</returns>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();

        foreach (var table in MissingTables)
        {
            problems.Add($"missing table: {table}");
        }

        foreach (var column in MissingColumns)
        {
            problems.Add($"missing column: {column}");
        }

        foreach (var column in ExtraColumns)
        {
            problems.Add($"extra column (ignored): {column}");
        }

        foreach (var table in ExtraTables)
        {
            problems.Add($"extra table (ignored): {table}");
        }

        return problems;
    }
}

/// <summary>
/// Compares the EF Core model with the live SQLite schema by reading <c>sqlite_master</c> and <c>PRAGMA table_info</c>.
/// </summary>
public static class SchemaIntegrityChecker
{
    /// <summary>
    /// Runs the comparison.
    /// </summary>
    /// <param name="context">The context whose model is the expectation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The differences found; an empty report means the database matches the model.</returns>
    public static async Task<SchemaIntegrityReport> CheckAsync(
        GalboxDbContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var report = new SchemaIntegrityReport();

        var connection = context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        var actualTables = await ListTablesAsync(context, cancellationToken).ConfigureAwait(false);
        var expectedTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var table in context.Model.GetRelationalModel().Tables)
        {
            expectedTables.Add(table.Name);

            if (!actualTables.Contains(table.Name))
            {
                report.MissingTables.Add(table.Name);
                continue;
            }

            var actualColumns = await ListColumnsAsync(context, table.Name, cancellationToken).ConfigureAwait(false);

            foreach (var column in table.Columns)
            {
                if (!actualColumns.Contains(column.Name))
                {
                    report.MissingColumns.Add($"{table.Name}.{column.Name}");
                }
            }

            foreach (var actualColumn in actualColumns)
            {
                if (!table.Columns.Any(c => string.Equals(c.Name, actualColumn, StringComparison.OrdinalIgnoreCase)))
                {
                    report.ExtraColumns.Add($"{table.Name}.{actualColumn}");
                }
            }
        }

        foreach (var actual in actualTables)
        {
            if (!expectedTables.Contains(actual) &&
                !string.Equals(actual, "__EFMigrationsHistory", StringComparison.OrdinalIgnoreCase))
            {
                report.ExtraTables.Add(actual);
            }
        }

        report.ExtraTables.Sort(StringComparer.Ordinal);
        report.ExtraColumns.Sort(StringComparer.Ordinal);
        report.MissingColumns.Sort(StringComparer.Ordinal);
        report.MissingTables.Sort(StringComparer.Ordinal);
        return report;
    }

    /// <summary>
    /// Lists the user tables of the database (SQLite internal tables are skipped).
    /// </summary>
    /// <param name="context">The context to use for the connection.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Table names as they are stored.</returns>
    public static async Task<List<string>> ListTablesAsync(
        GalboxDbContext context,
        CancellationToken cancellationToken = default)
    {
        var tables = new List<string>();

        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }

    /// <summary>
    /// Reads the declared storage type of every column of one table.
    /// </summary>
    /// <param name="context">The context to use for the connection.</param>
    /// <param name="tableName">Name of the table.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Column name to declared type (as SQLite reports it, possibly empty).</returns>
    public static async Task<Dictionary<string, string>> ListColumnTypesAsync(
        GalboxDbContext context,
        string tableName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        var types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{tableName.Replace("\"", "\"\"")}\");";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            types[reader.GetString(1)] = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
        }

        return types;
    }

    /// <summary>
    /// Lists the column names of one table using <c>PRAGMA table_info</c>.
    /// </summary>
    /// <param name="context">The context to use for the connection.</param>
    /// <param name="tableName">Name of the table; must come from the model or from <see cref="ListTablesAsync"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Column names of the table.</returns>
    public static async Task<HashSet<string>> ListColumnsAsync(
        GalboxDbContext context,
        string tableName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using var command = context.Database.GetDbConnection().CreateCommand();

        // PRAGMA does not accept parameters, so the name is inlined after being quoted for SQLite.
        command.CommandText = $"PRAGMA table_info(\"{tableName.Replace("\"", "\"\"")}\");";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // PRAGMA table_info returns: cid, name, type, notnull, dflt_value, pk
            columns.Add(Convert.ToString(reader["name"], CultureInfo.InvariantCulture) ?? string.Empty);
        }

        return columns;
    }
}
