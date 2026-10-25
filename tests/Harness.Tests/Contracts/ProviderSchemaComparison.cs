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
/// <para>Types are not compared: they differ by design (see the SQL Server baseline steps).</para>
/// </summary>
internal static class ProviderSchemaComparison
{
    public static async Task AssertSameColumnsAsync(
        string sqlitePath, string sqlServerConnection, IReadOnlyList<string> tables)
    {
        var ct = TestContext.Current.CancellationToken;

        foreach (var table in tables)
        {
            var sqlite = await SqliteColumnsAsync(sqlitePath, table, ct);
            var sqlServer = await SqlServerColumnsAsync(sqlServerConnection, table, ct);

            Assert.True(sqlite.Count > 0, $"SQLite has no table '{table}'.");
            Assert.True(sqlServer.Count > 0, $"SQL Server has no table '{table}'.");
            Assert.Equal(Describe(table, sqlite), Describe(table, sqlServer));
        }
    }

    private static string[] Describe(string table, IReadOnlyDictionary<string, bool> columns) =>
        [.. columns.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => $"{table}.{c.Key} {(c.Value ? "NULL" : "NOT NULL")}")];

    private static async Task<IReadOnlyDictionary<string, bool>> SqliteColumnsAsync(string path, string table, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();

        // table_xinfo, not table_info: a generated column (messages.team) is hidden from the other.
        command.CommandText = "SELECT name, \"notnull\", pk FROM pragma_table_xinfo($table)";
        command.Parameters.AddWithValue("$table", table);

        var columns = new Dictionary<string, bool>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) columns[reader.GetString(0)] = reader.GetInt64(1) == 0 && reader.GetInt64(2) == 0;

        return columns;
    }

    private static async Task<IReadOnlyDictionary<string, bool>> SqlServerColumnsAsync(string connectionString, string table, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COLUMN_NAME, IS_NULLABLE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = @table";
        command.Parameters.AddWithValue("@table", table);

        var columns = new Dictionary<string, bool>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) columns[reader.GetString(0)] = reader.GetString(1) == "YES";

        return columns;
    }
}
