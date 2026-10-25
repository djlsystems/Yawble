using Harness.Contracts;
using Harness.Identity;
using Microsoft.Data.SqlClient;

namespace Harness.SqlServer.Identity;

/// <summary>
/// <see cref="IUnfinishedRemovals"/> over <c>unfinished_removals</c> on SQL Server: SQLite's
/// <c>SqliteUnfinishedRemovals</c> statement for statement. A row's columns and its reading are
/// <see cref="UnfinishedRemovalRows"/>', shared with it.
///
/// <para>
/// A RE-RECORD IS ONE <c>MERGE</c> UNDER <c>HOLDLOCK</c>, counting the attempt in the statement that
/// reads it. SQLite's upsert runs alone; here two attempts at one folder recorded at once would
/// otherwise both find no row and one would fail on the key, or both read one count and one attempt
/// would be lost. The merge holds the path until it commits, so the second adds to the first's count.
/// </para>
/// </summary>
public sealed class SqlServerUnfinishedRemovals(string connectionString) : IUnfinishedRemovals
{
    private readonly string _connectionString = SqlServerOpen.ConnectionString(connectionString);

    public Task RecordAsync(UnfinishedRemoval removal, CancellationToken ct = default) =>
        SqlServerOpen.ExecuteAsync(
            _connectionString,
            """
            MERGE unfinished_removals WITH (HOLDLOCK) AS r
            USING (SELECT @path AS path) AS n ON r.path = n.path
            WHEN MATCHED THEN UPDATE SET
                kind = @kind, team = @team, member = @member, remaining = @remaining,
                attempts = r.attempts + 1, host_left = @hostLeft
            WHEN NOT MATCHED THEN INSERT (path, kind, team, member, remaining, recorded_at, attempts, host_left)
                VALUES (@path, @kind, @team, @member, @remaining, @at, 1, @hostLeft);
            """,
            command =>
            {
                command.Parameters.AddWithValue("@path", removal.Path);
                command.Parameters.AddWithValue("@kind", removal.Kind);
                command.Parameters.AddWithValue("@team", removal.Team);
                command.Parameters.AddWithValue("@member", (object?)removal.Member ?? DBNull.Value);
                command.Parameters.AddWithValue("@remaining", UnfinishedRemovalRows.Remaining(removal));
                command.Parameters.AddWithValue("@at", Instant.Stamp(removal.RecordedAt));
                command.Parameters.AddWithValue("@hostLeft", UnfinishedRemovalRows.HostLeft(removal));
            },
            ct);

    public Task<IReadOnlyList<UnfinishedRemoval>> ListAsync(CancellationToken ct = default) =>
        ReadAsync($"SELECT {UnfinishedRemovalRows.Columns} FROM unfinished_removals ORDER BY path", null, ct);

    public async Task<UnfinishedRemoval?> FindAsync(string path, CancellationToken ct = default) =>
        (await ReadAsync($"SELECT {UnfinishedRemovalRows.Columns} FROM unfinished_removals WHERE path = @path", path, ct)).SingleOrDefault();

    public Task ForgetAsync(string path, CancellationToken ct = default) =>
        SqlServerOpen.ExecuteAsync(
            _connectionString,
            "DELETE FROM unfinished_removals WHERE path = @path",
            command => command.Parameters.AddWithValue("@path", path),
            ct);

    private Task<IReadOnlyList<UnfinishedRemoval>> ReadAsync(string sql, string? path, CancellationToken ct) =>
        SqlServerOpen.RunAsync<IReadOnlyList<UnfinishedRemoval>>(
            _connectionString,
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                if (path is not null) command.Parameters.AddWithValue("@path", path);

                var rows = new List<UnfinishedRemoval>();
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token)) rows.Add(UnfinishedRemovalRows.Read(reader));

                return rows;
            },
            ct);
}
