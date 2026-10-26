using System.Globalization;
using System.Text;
using Harness.Contracts;
using Harness.SqlServer;
using Microsoft.Data.SqlClient;

namespace Harness.Host.SqlServer;

/// <summary>
/// The SQL Server version of <see cref="IAgentStepStore"/>: <c>run_steps</c>
/// (<c>messages.sqlserver-baseline-1</c>), <see cref="AgentStepStore"/> statement for statement, each row
/// written and read by <see cref="MonitorRows"/>, shared with it. Kind, tool name and time only; the
/// log's purge prunes the rows (<c>IMessageLog.DeleteAsync</c>), as on SQLite.
///
/// <para>
/// A TEAM AND A MEMBER FOLD CASE, as SQLite's <c>COLLATE NOCASE</c> columns do, and are matched with
/// their length (<see cref="SqlServerSql.Same"/>), so a trailing space reads and removes nothing.
/// </para>
///
/// <para>
/// No read-then-write: a write inserts new rows and sets the count of rows it inserted itself, and every
/// removal is one statement per key, so each holds only the rows it touches.
/// </para>
/// </summary>
public sealed class SqlServerAgentStepStore(string connectionString) : IAgentStepStore
{
    private readonly string _connectionString = SqlServerOpen.ConnectionString(connectionString);

    public Task WriteAsync(
        string team, string member, long delivery, long? workflow,
        IReadOnlyCollection<NewAgentStep> added, IReadOnlyCollection<NewAgentStep> counted, CancellationToken ct = default)
    {
        if (added.Count == 0 && counted.Count == 0) return Task.CompletedTask;

        return SqlServerOpen.RunAsync(
            _connectionString,
            async (connection, token) =>
            {
                await using var transaction = await SqlServerOpen.BeginAsync(connection, token);

                await using (var insert = connection.CreateCommand())
                {
                    insert.Transaction = transaction;
                    insert.CommandText =
                        """
                        INSERT INTO run_steps (team, member, delivery, workflow, at, kind, label, mcp_tool, count)
                        OUTPUT inserted.id
                        VALUES (@team, @member, @delivery, @workflow, @at, @kind, @label, @mcp, @count)
                        """;
                    insert.Parameters.AddWithValue("@team", team);
                    insert.Parameters.AddWithValue("@member", member);
                    insert.Parameters.AddWithValue("@delivery", delivery);
                    insert.Parameters.AddWithValue("@workflow", (object?)workflow ?? DBNull.Value);
                    var at = insert.Parameters.AddWithValue("@at", "");
                    var kind = insert.Parameters.AddWithValue("@kind", "");
                    var label = insert.Parameters.AddWithValue("@label", "");
                    var mcp = insert.Parameters.Add("@mcp", System.Data.SqlDbType.NVarChar, -1);
                    var count = insert.Parameters.AddWithValue("@count", 1L);

                    foreach (var step in added)
                    {
                        at.Value = MonitorRows.Instant(step.At);
                        kind.Value = step.Kind;
                        label.Value = step.Label;
                        mcp.Value = (object?)step.McpTool ?? DBNull.Value;
                        count.Value = (long)step.Count;
                        step.Id = (long)(await insert.ExecuteScalarAsync(token))!;
                    }
                }

                await using (var update = connection.CreateCommand())
                {
                    update.Transaction = transaction;
                    update.CommandText = "UPDATE run_steps SET count = @count WHERE id = @id";
                    var id = update.Parameters.AddWithValue("@id", 0L);
                    var count = update.Parameters.AddWithValue("@count", 1L);

                    foreach (var step in counted.Where(s => s.Id > 0))
                    {
                        id.Value = step.Id;
                        count.Value = (long)step.Count;
                        await update.ExecuteNonQueryAsync(token);
                    }
                }

                await SqlServerOpen.CommitAsync(transaction, token);
                return true;
            },
            ct);
    }

    public Task<IReadOnlyList<AgentStep>> ReadAsync(
        DateTimeOffset from, DateTimeOffset to, long after, int take,
        IReadOnlyDictionary<string, DateTimeOffset> since, CancellationToken ct = default)
    {
        if (take <= 0 || since.Count == 0) return Task.FromResult<IReadOnlyList<AgentStep>>([]);

        return SqlServerOpen.RunAsync<IReadOnlyList<AgentStep>>(
            _connectionString,
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();

                var scope = new StringBuilder();
                var i = 0;
                foreach (var (team, created) in since)
                {
                    if (i > 0) scope.Append(" OR ");
                    scope.Append(CultureInfo.InvariantCulture, $"({SqlServerSql.Same("s.team", $"@t{i}")} AND s.at >= @s{i})");
                    command.Parameters.AddWithValue($"@t{i}", team);
                    command.Parameters.AddWithValue($"@s{i}", MonitorRows.Instant(created));
                    i++;
                }

                // THE RUN'S TERMINAL ROW, found by causation (ix_messages_causation): the completed or
                // failed row its delivery caused. Until it is written, the delivery stands for the run.
                command.CommandText =
                    $"""
                     SELECT TOP (@take) s.id, s.team, s.member,
                            COALESCE((SELECT MIN(m.seq) FROM messages m
                                      WHERE m.causation_seq = s.delivery AND m.type IN (@completed, @failed)), s.delivery),
                            s.workflow, s.at, s.kind, s.label, s.mcp_tool, s.count
                     FROM run_steps s
                     WHERE s.id > @after AND s.at >= @from AND s.at <= @to AND ({scope})
                     ORDER BY s.id
                     """;
                command.Parameters.AddWithValue("@after", after);
                command.Parameters.AddWithValue("@from", MonitorRows.Instant(from));
                command.Parameters.AddWithValue("@to", MonitorRows.Instant(to));
                command.Parameters.AddWithValue("@take", take);
                command.Parameters.AddWithValue("@completed", MessageTypes.Completed);
                command.Parameters.AddWithValue("@failed", MessageTypes.Failed);

                var rows = new List<AgentStep>();
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token)) rows.Add(MonitorRows.ReadStep(reader));

                return rows;
            },
            ct);
    }

    public Task<int> DeleteForTeamAsync(string team, CancellationToken ct = default) =>
        SqlServerOpen.ExecuteAsync(
            _connectionString,
            $"DELETE FROM run_steps WHERE {SqlServerSql.Same("team", "@team")}",
            command => command.Parameters.AddWithValue("@team", team),
            ct);

    public Task<int> DeleteForMembersAsync(string team, IReadOnlyCollection<string> members, CancellationToken ct = default) =>
        DeleteForMembersAsync(_connectionString, "run_steps", team, members, ct);

    /// <summary>The rows of these members of <paramref name="team"/> from <paramref name="table"/> (a
    /// constant of the two Monitor stores), in one transaction; how many went.</summary>
    internal static Task<int> DeleteForMembersAsync(
        string connectionString, string table, string team, IReadOnlyCollection<string> members, CancellationToken ct)
    {
        if (members.Count == 0) return Task.FromResult(0);

        return SqlServerOpen.RunAsync(
            connectionString,
            async (connection, token) =>
            {
                await using var transaction = await SqlServerOpen.BeginAsync(connection, token);
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    $"DELETE FROM {table} WHERE {SqlServerSql.Same("team", "@team")} AND {SqlServerSql.Same("member", "@member")}";
                command.Parameters.AddWithValue("@team", team);
                var member = command.Parameters.AddWithValue("@member", "");

                var removed = 0;
                foreach (var name in members)
                {
                    member.Value = name;
                    removed += await command.ExecuteNonQueryAsync(token);
                }

                await SqlServerOpen.CommitAsync(transaction, token);
                return removed;
            },
            ct);
    }
}
