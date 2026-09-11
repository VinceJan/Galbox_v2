using Galbox.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Galbox.Data.Migrations;

/// <summary>
/// Snapshot of the objects (tables, columns, indexes) that actually exist in a database, used to decide whether a
/// migration operation still has to run.
/// </summary>
public sealed class DatabaseSchemaState
{
    /// <summary>Existing table names.</summary>
    public HashSet<string> Tables { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Existing columns per table.</summary>
    public Dictionary<string, HashSet<string>> Columns { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Declared storage type per table and column, as SQLite reports it.</summary>
    public Dictionary<string, Dictionary<string, string>> ColumnTypes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Existing index names.</summary>
    public HashSet<string> Indexes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Reads the current state of the database behind the given context.
    /// </summary>
    /// <param name="context">Context whose connection is inspected.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The objects that exist right now.</returns>
    public static async Task<DatabaseSchemaState> CaptureAsync(
        GalboxDbContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var state = new DatabaseSchemaState();

        foreach (var table in await SchemaIntegrityChecker.ListTablesAsync(context, cancellationToken).ConfigureAwait(false))
        {
            state.Tables.Add(table);
            state.Columns[table] = await SchemaIntegrityChecker
                .ListColumnsAsync(context, table, cancellationToken)
                .ConfigureAwait(false);
            state.ColumnTypes[table] = await SchemaIntegrityChecker
                .ListColumnTypesAsync(context, table, cancellationToken)
                .ConfigureAwait(false);
        }

        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index' AND name NOT LIKE 'sqlite_%';";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            state.Indexes.Add(reader.GetString(0));
        }

        return state;
    }

    /// <summary>
    /// Checks whether a table exists.
    /// </summary>
    /// <param name="table">Table name.</param>
    /// <returns>True when the table exists.</returns>
    public bool HasTable(string table) => Tables.Contains(table);

    /// <summary>
    /// Checks whether a column exists.
    /// </summary>
    /// <param name="table">Table name.</param>
    /// <param name="column">Column name.</param>
    /// <returns>True when both the table and the column exist.</returns>
    public bool HasColumn(string table, string column) =>
        Columns.TryGetValue(table, out var columns) && columns.Contains(column);

    /// <summary>
    /// Gets the declared storage type of a column.
    /// </summary>
    /// <param name="table">Table name.</param>
    /// <param name="column">Column name.</param>
    /// <returns>The declared type, or null when the column does not exist.</returns>
    public string? GetColumnType(string table, string column) =>
        ColumnTypes.TryGetValue(table, out var types) && types.TryGetValue(column, out var type) ? type : null;

    /// <summary>
    /// Maps a declared SQLite type to its storage class ("affinity"), which is what SQLite actually compares.
    /// </summary>
    /// <param name="declaredType">A declared column type, possibly empty.</param>
    /// <returns>One of INTEGER, TEXT, BLOB, REAL or NUMERIC.</returns>
    public static string GetAffinity(string? declaredType)
    {
        var type = (declaredType ?? string.Empty).ToUpperInvariant();

        if (type.Contains("INT", StringComparison.Ordinal))
        {
            return "INTEGER";
        }

        if (type.Contains("CHAR", StringComparison.Ordinal) ||
            type.Contains("CLOB", StringComparison.Ordinal) ||
            type.Contains("TEXT", StringComparison.Ordinal))
        {
            return "TEXT";
        }

        if (type.Length == 0 || type.Contains("BLOB", StringComparison.Ordinal))
        {
            return "BLOB";
        }

        if (type.Contains("REAL", StringComparison.Ordinal) ||
            type.Contains("FLOA", StringComparison.Ordinal) ||
            type.Contains("DOUB", StringComparison.Ordinal))
        {
            return "REAL";
        }

        return "NUMERIC";
    }
}
