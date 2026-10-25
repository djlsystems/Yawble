using System.Globalization;
using System.Text;
using Harness.Contracts;
using Harness.SqlServer;

namespace Harness.Host.SqlServer;

/// <summary>
/// The SQL Server version of <see cref="IWorkspaceFileChangeStore"/>: <c>file_changes</c>
/// (<c>messages.sqlserver-baseline-1</c>), <see cref="WorkspaceFileChangeStore"/> statement for statement,
/// each row written and read by <see cref="MonitorRows"/>, shared with it. Paths, actions, shas and
/// subjects only; the log's purge prunes the rows (<c>IMessageLog.DeleteAsync</c>), as on SQLite.
///
/// <para>
/// A TEAM AND A MEMBER FOLD CASE and are matched with their length (<see cref="SqlServerSql.Same"/>), as
/// in <see cref="SqlServerAgentStepStore"/>. No read-then-write: an append is one insert and every
/// removal one statement per key.
/// </para>
/// </summary>
public sealed class SqlServerWorkspaceFileChangeStore(string connectionString) : IWorkspaceFileChangeStore
{
    private readonly string _connectionString = SqlServerOpen.ConnectionString(connectionString);

    public Task AppendAsync(WorkspaceChange change, long? correlation, CancellationToken ct = default) =>
        SqlServerOpen.ExecuteAsync(
            _connectionString,
            """
            INSERT INTO file_changes (team, member, repo, at, correlation, files_json, commits_json, more)
            VALUES (@team, @member, @repo, @at, @correlation, @files, @commits, @more)
            """,
            command =>
            {
                command.Parameters.AddWithValue("@team", change.Team);
                command.Parameters.AddWithValue("@member", change.Member);
                command.Parameters.AddWithValue("@repo", change.Repo);
                command.Parameters.AddWithValue("@at", MonitorRows.Instant(change.At));
                command.Parameters.AddWithValue("@correlation", (object?)correlation ?? DBNull.Value);
                command.Parameters.AddWithValue("@files", MonitorRows.FilesJson(change));
                command.Parameters.AddWithValue("@commits", MonitorRows.CommitsJson(change));
                command.Parameters.AddWithValue("@more", (long)change.More);
            },
            ct);

    public Task<IReadOnlyList<RecordedWorkspaceChange>> ReadAsync(
        DateTimeOffset from, DateTimeOffset to, long after, int take,
        IReadOnlyDictionary<string, DateTimeOffset> since, CancellationToken ct = default)
    {
        if (take <= 0 || since.Count == 0) return Task.FromResult<IReadOnlyList<RecordedWorkspaceChange>>([]);

        return SqlServerOpen.RunAsync<IReadOnlyList<RecordedWorkspaceChange>>(
            _connectionString,
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();

                var scope = new StringBuilder();
                var i = 0;
                foreach (var (team, created) in since)
                {
                    if (i > 0) scope.Append(" OR ");
                    scope.Append(CultureInfo.InvariantCulture, $"({SqlServerSql.Same("team", $"@t{i}")} AND at >= @s{i})");
                    command.Parameters.AddWithValue($"@t{i}", team);
                    command.Parameters.AddWithValue($"@s{i}", MonitorRows.Instant(created));
                    i++;
                }

                command.CommandText =
                    $"""
                     SELECT TOP (@take) {MonitorRows.ChangeColumns} FROM file_changes
                     WHERE id > @after AND at >= @from AND at <= @to AND ({scope})
                     ORDER BY id
                     """;
                command.Parameters.AddWithValue("@after", after);
                command.Parameters.AddWithValue("@from", MonitorRows.Instant(from));
                command.Parameters.AddWithValue("@to", MonitorRows.Instant(to));
                command.Parameters.AddWithValue("@take", take);

                var rows = new List<RecordedWorkspaceChange>();
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token)) rows.Add(MonitorRows.ReadChange(reader));

                return rows;
            },
            ct);
    }

    public Task<int> DeleteForTeamAsync(string team, CancellationToken ct = default) =>
        SqlServerOpen.ExecuteAsync(
            _connectionString,
            $"DELETE FROM file_changes WHERE {SqlServerSql.Same("team", "@team")}",
            command => command.Parameters.AddWithValue("@team", team),
            ct);

    public Task<int> DeleteForMembersAsync(string team, IReadOnlyCollection<string> members, CancellationToken ct = default) =>
        SqlServerAgentStepStore.DeleteForMembersAsync(_connectionString, "file_changes", team, members, ct);
}
