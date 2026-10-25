using Harness.SqlServer;
using Microsoft.Data.Sqlite;
using Microsoft.Data.SqlClient;

namespace Harness.Tests.Contracts;

/// <summary>
/// WHETHER THE TWO PROVIDERS' TABLES HAVE THE SAME COLUMNS: the same names, in exactly the same case,
/// and the same nullability, table by table. A store written against one provider's spelling of a
/// column fails on the other's at the first query that names it; this fails first, naming it.
///
/// <para>
/// A PRIMARY KEY COLUMN IS NOT NULL ON BOTH. SQLite's <c>table_info</c> reports only a declared
/// <c>NOT NULL</c>, so its <c>INTEGER PRIMARY KEY</c> (the rowid, never null) and a <c>TEXT PRIMARY
/// KEY</c> read as nullable there, while SQL Server requires every key column NOT NULL. The stores
/// never write a null key, so a key column counts as not null on the SQLite side.
/// </para>
///
/// <para>
/// AND THE SAME COLLATION INTENT. A SQLite column declared <c>COLLATE NOCASE</c> folds case in its key,
/// its index and every comparison, so its SQL Server twin must carry <see cref="SqlServerSql.NoCase"/>; a
/// SQLite column with the default BINARY collation compares by code point, so its twin must NOT carry
/// it. A column that folds on one provider and not the other is a uniqueness or a lookup that differs
/// (<c>Alpha</c> and <c>alpha</c> one row there, two here), which no column-name check would see.
/// </para>
///
/// <para>Types are not compared: they differ by design (see the SQL Server baseline steps).</para>
///
/// <para>
/// A COMPUTED KEY COLUMN SQL SERVER ALONE HAS IS EXEMPT, BY NAME: <see cref="SqlServerOnlyKeys"/>. Each
/// is the persisted hash a SQL Server key stands on where SQLite keys the text itself, so SQLite has
/// no twin of it. Only the columns listed are left out, and each must be a computed column on SQL
/// Server, or this fails naming it: an exemption cannot hide an ordinary column.
/// </para>
/// </summary>
internal static class ProviderSchemaComparison
{
    /// <summary>SQL Server's computed key columns with no SQLite twin, as <c>table.column</c>.</summary>
    internal static readonly IReadOnlySet<string> SqlServerOnlyKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "unfinished_removals.path_key",
        "backlog_dispatch_tips.repo_key",
        "backlog_dispatch_bases.repo_key",
        "backlog_dispatch_missed_starts.repo_key",
    };

    public static async Task AssertSameColumnsAsync(
        string sqlitePath, string sqlServerConnection, IReadOnlyList<string> tables)
    {
        var ct = TestContext.Current.CancellationToken;

        foreach (var table in tables)
        {
            var sqlite = await SqliteColumnsAsync(sqlitePath, table, ct);
            var sqlServer = await SqlServerColumnsAsync(sqlServerConnection, table, ct);

            foreach (var exempt in SqlServerOnlyKeys.Where(key => key.StartsWith(table + ".", StringComparison.Ordinal)))
            {
                var column = exempt[(table.Length + 1)..];
                Assert.True(await IsComputedAsync(sqlServerConnection, table, column, ct), $"SQL Server's {exempt} is exempt but is not a computed column.");
                sqlServer = sqlServer.Where(c => c.Key != column).ToDictionary(c => c.Key, c => c.Value, StringComparer.Ordinal);
            }

            Assert.True(sqlite.Count > 0, $"SQLite has no table '{table}'.");
            Assert.True(sqlServer.Count > 0, $"SQL Server has no table '{table}'.");
            Assert.Equal(Describe(table, sqlite), Describe(table, sqlServer));
        }
    }

    /// <summary>Every table of a SQLite database, by name, the engine's own <c>sqlite_*</c> tables left out.</summary>
    public static async Task<IReadOnlyList<string>> SqliteTablesAsync(string path, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite!_%' ESCAPE '!' ORDER BY name";

        var tables = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) tables.Add(reader.GetString(0));

        return tables;
    }

    /// <summary>Every user table of a SQL Server database, by name.</summary>
    public static async Task<IReadOnlyList<string>> SqlServerTablesAsync(string connectionString, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sys.tables WHERE is_ms_shipped = 0 ORDER BY name";

        var tables = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) tables.Add(reader.GetString(0));

        return tables;
    }

    /// <summary>What is compared of one column: whether it may be null, and whether it folds case.</summary>
    private readonly record struct Column(bool Nullable, bool NoCase);

    private static string[] Describe(string table, IReadOnlyDictionary<string, Column> columns) =>
        [.. columns.OrderBy(c => c.Key, StringComparer.Ordinal)
            .Select(c => $"{table}.{c.Key} {(c.Value.Nullable ? "NULL" : "NOT NULL")}{(c.Value.NoCase ? " NOCASE" : "")}")];

    private static async Task<IReadOnlyDictionary<string, Column>> SqliteColumnsAsync(string path, string table, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();

        // table_xinfo, not table_info: a generated column (messages.team) is hidden from the other.
        command.CommandText = "SELECT name, \"notnull\", pk FROM pragma_table_xinfo($table)";
        command.Parameters.AddWithValue("$table", table);

        var nullable = new Dictionary<string, bool>(StringComparer.Ordinal);
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct)) nullable[reader.GetString(0)] = reader.GetInt64(1) == 0 && reader.GetInt64(2) == 0;
        }

        // The declared collation is SQLite's own answer (sqlite3_table_column_metadata), not a parse of
        // the CREATE statement, so a column added later by ALTER TABLE is read the same way.
        var columns = new Dictionary<string, Column>(StringComparer.Ordinal);
        foreach (var (name, isNullable) in nullable)
        {
            var result = SQLitePCL.raw.sqlite3_table_column_metadata(
                connection.Handle, "main", table, name, out _, out var collation, out _, out _, out _);
            Assert.True(result == SQLitePCL.raw.SQLITE_OK, $"SQLite could not describe {table}.{name} (result {result}).");
            columns[name] = new Column(isNullable, string.Equals(collation, "NOCASE", StringComparison.OrdinalIgnoreCase));
        }

        return columns;
    }

    private static async Task<bool> IsComputedAsync(string connectionString, string table, string column, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sys.computed_columns WHERE object_id = OBJECT_ID(@table) AND name = @column";
        command.Parameters.AddWithValue("@table", table);
        command.Parameters.AddWithValue("@column", column);
        return (int)(await command.ExecuteScalarAsync(ct))! == 1;
    }

    private static async Task<IReadOnlyDictionary<string, Column>> SqlServerColumnsAsync(string connectionString, string table, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COLUMN_NAME, IS_NULLABLE, COLLATION_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = @table";
        command.Parameters.AddWithValue("@table", table);

        var columns = new Dictionary<string, Column>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var collation = reader.IsDBNull(2) ? null : reader.GetString(2);
            columns[reader.GetString(0)] = new Column(reader.GetString(1) == "YES", string.Equals(collation, SqlServerSql.NoCase, StringComparison.OrdinalIgnoreCase));
        }

        return columns;
    }
}
