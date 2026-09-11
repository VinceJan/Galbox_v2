using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Galbox.Data.Migrations.Harness;

/// <summary>
/// Raw, EF-independent view of a SQLite database file, used to prove what a migration actually did to the bytes
/// on disk (rather than what EF believes it did).
/// </summary>
internal sealed class DbSnapshot
{
    public string DatabasePath { get; init; } = string.Empty;

    /// <summary>Table name -> column names, in the order the database reports them.</summary>
    public Dictionary<string, List<string>> Columns { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Table name -> row count.</summary>
    public Dictionary<string, long> RowCounts { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Index names.</summary>
    public List<string> Indexes { get; } = new();

    /// <summary>Rows of __EFMigrationsHistory (empty when the table does not exist).</summary>
    public List<(string MigrationId, string ProductVersion)> History { get; } = new();

    /// <summary>Table names, sorted.</summary>
    public List<string> Tables => Columns.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList();

    /// <summary>Reads the full state of a database file.</summary>
    public static DbSnapshot Capture(string databasePath)
    {
        var snapshot = new DbSnapshot { DatabasePath = databasePath };

        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        connection.Open();

        foreach (var table in Query(connection, "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;"))
        {
            snapshot.Columns[table] = ReadColumns(connection, table);
            snapshot.RowCounts[table] = Convert.ToInt64(Scalar(connection, $"SELECT COUNT(*) FROM \"{table}\";"), CultureInfo.InvariantCulture);
        }

        foreach (var index in Query(connection, "SELECT name FROM sqlite_master WHERE type='index' AND name NOT LIKE 'sqlite_%' ORDER BY name;"))
        {
            snapshot.Indexes.Add(index);
        }

        if (snapshot.Columns.ContainsKey("__EFMigrationsHistory"))
        {
            foreach (var row in Query(
                connection,
                "SELECT MigrationId || '|' || ProductVersion FROM \"__EFMigrationsHistory\" ORDER BY MigrationId;"))
            {
                var parts = row.Split('|', 2);
                snapshot.History.Add((parts[0], parts.Length > 1 ? parts[1] : string.Empty));
            }
        }

        return snapshot;
    }

    /// <summary>Lists the columns of one table via PRAGMA table_info.</summary>
    public static List<string> ReadColumns(SqliteConnection connection, string table)
    {
        var columns = new List<string>();
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table.Replace("\"", "\"\"")}\");";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    /// <summary>Reads the full PRAGMA table_info output (for the evidence dump).</summary>
    public static List<string> DescribeTable(string databasePath, string table)
    {
        var lines = new List<string>();
        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table.Replace("\"", "\"\"")}\");";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            // cid, name, type, notnull, dflt_value, pk
            lines.Add(string.Format(
                CultureInfo.InvariantCulture,
                "  [{0}] {1} {2} notnull={3} default={4} pk={5}",
                reader.GetInt32(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? "(none)" : reader.GetString(2),
                reader.GetInt32(3),
                reader.IsDBNull(4) ? "NULL" : reader.GetValue(4).ToString(),
                reader.GetInt32(5)));
        }

        return lines;
    }

    /// <summary>
    /// Compares the structure (tables, columns as sets, indexes) of two databases and returns a human readable
    /// description of every difference. Column order is deliberately ignored: the legacy database got its
    /// EngineType column appended by ALTER TABLE, which is not a meaningful difference.
    /// </summary>
    public static List<string> StructuralDiff(DbSnapshot left, DbSnapshot right)
    {
        var differences = new List<string>();

        foreach (var table in left.Tables.Where(t => !IsBookkeeping(t) && !right.Columns.ContainsKey(t)))
        {
            differences.Add($"table '{table}' exists only in {Path.GetFileName(left.DatabasePath)}");
        }

        foreach (var table in right.Tables.Where(t => !IsBookkeeping(t) && !left.Columns.ContainsKey(t)))
        {
            differences.Add($"table '{table}' exists only in {Path.GetFileName(right.DatabasePath)}");
        }

        foreach (var table in left.Tables.Where(t => !IsBookkeeping(t) && right.Columns.ContainsKey(t)))
        {
            var leftColumns = left.Columns[table];
            var rightColumns = right.Columns[table];

            foreach (var column in leftColumns.Where(c => !rightColumns.Contains(c, StringComparer.OrdinalIgnoreCase)))
            {
                differences.Add($"column '{table}.{column}' exists only in {Path.GetFileName(left.DatabasePath)}");
            }

            foreach (var column in rightColumns.Where(c => !leftColumns.Contains(c, StringComparer.OrdinalIgnoreCase)))
            {
                differences.Add($"column '{table}.{column}' exists only in {Path.GetFileName(right.DatabasePath)}");
            }
        }

        foreach (var index in left.Indexes.Where(i => !right.Indexes.Contains(i, StringComparer.OrdinalIgnoreCase)))
        {
            differences.Add($"index '{index}' exists only in {Path.GetFileName(left.DatabasePath)}");
        }

        foreach (var index in right.Indexes.Where(i => !left.Indexes.Contains(i, StringComparer.OrdinalIgnoreCase)))
        {
            differences.Add($"index '{index}' exists only in {Path.GetFileName(right.DatabasePath)}");
        }

        return differences;
    }

    /// <summary>
    /// Objects that <paramref name="expected"/> declares but <paramref name="actual"/> does not have.
    /// Used to state "the database contains at least everything the baseline requires" without demanding that
    /// hand-added objects be absent.
    /// </summary>
    /// <param name="actual">The database that is inspected.</param>
    /// <param name="expected">The reference schema.</param>
    /// <returns>One line per missing table, column or index; empty when nothing is missing.</returns>
    public static List<string> MissingObjects(DbSnapshot actual, DbSnapshot expected)
    {
        var missing = new List<string>();

        foreach (var table in expected.Tables.Where(t => !IsBookkeeping(t) && !actual.Columns.ContainsKey(t)))
        {
            missing.Add($"table {table}");
        }

        foreach (var table in expected.Tables.Where(t => !IsBookkeeping(t) && actual.Columns.ContainsKey(t)))
        {
            foreach (var column in expected.Columns[table]
                         .Where(c => !actual.Columns[table].Contains(c, StringComparer.OrdinalIgnoreCase)))
            {
                missing.Add($"column {table}.{column}");
            }
        }

        foreach (var index in expected.Indexes.Where(i => !actual.Indexes.Contains(i, StringComparer.OrdinalIgnoreCase)))
        {
            missing.Add($"index {index}");
        }

        return missing;
    }

    /// <summary>
    /// Objects that <paramref name="actual"/> has but <paramref name="expected"/> does not declare
    /// (columns added by hand outside the migrations, or leftovers of an older model).
    /// </summary>
    /// <param name="actual">The database that is inspected.</param>
    /// <param name="expected">The reference schema.</param>
    /// <returns>One line per extra table, column or index.</returns>
    public static List<string> ExtraObjects(DbSnapshot actual, DbSnapshot expected)
    {
        var extra = new List<string>();

        foreach (var table in actual.Tables.Where(t => !IsBookkeeping(t) && !expected.Columns.ContainsKey(t)))
        {
            extra.Add($"table {table}");
        }

        foreach (var table in actual.Tables.Where(t => !IsBookkeeping(t) && expected.Columns.ContainsKey(t)))
        {
            foreach (var column in actual.Columns[table]
                         .Where(c => !expected.Columns[table].Contains(c, StringComparer.OrdinalIgnoreCase)))
            {
                extra.Add($"column {table}.{column}");
            }
        }

        foreach (var index in actual.Indexes.Where(i => !expected.Indexes.Contains(i, StringComparer.OrdinalIgnoreCase)))
        {
            extra.Add($"index {index}");
        }

        return extra;
    }

    private static bool IsBookkeeping(string tableName) =>
        string.Equals(tableName, "__EFMigrationsHistory", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> Query(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            yield return reader.GetString(0);
        }
    }

    private static object Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar() ?? 0L;
    }
}
