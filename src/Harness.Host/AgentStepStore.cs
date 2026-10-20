using System.Globalization;
using System.Text;
using Harness.Contracts;
using Harness.Messaging;
using Microsoft.Data.Sqlite;

namespace Harness.Host;

/// <summary>
/// One step a running agent took, as its transcript shows it: what kind of step (one of
/// <see cref="LiveLineKinds.Steps"/>), the tool's name for a tool call (<c>MCP</c>, with the called
/// tool in <see cref="McpTool"/>), when, and how many steps it stands for (<see cref="Count"/> is
/// above 1 when steps over the per-second cap were folded into it). <see cref="Run"/> is the run's
/// terminal row once it is written, else the run's delivery; <see cref="Workflow"/> the run's
/// workflow when known. <see cref="Id"/> is the replay's paging cursor.
/// </summary>
public sealed record AgentStep(
    long Id,
    string Team,
    string Member,
    long Run,
    long? Workflow,
    DateTimeOffset At,
    string Kind,
    string Label,
    string? McpTool,
    int Count);

/// <summary>A step as the follow records it, before it has a row.</summary>
public sealed record NewAgentStep(DateTimeOffset At, string Kind, string Label, string? McpTool)
{
    public int Count { get; set; } = 1;

    /// <summary>The row once written; 0 before.</summary>
    public long Id { get; set; }
}

/// <summary>
/// <c>run_steps</c> (schema step <c>messages-005</c>) over the log's own file: what
/// <see cref="AgentStepFollow"/> read from every agent run's transcript, kept for the Monitor.
///
/// <para>
/// KIND, TOOL NAME AND TIME ONLY - never a command, path, argument, result, prose, user line or note
/// body. Rows are pruned by the log's purge (<c>IMessageLog.DeleteAsync</c>) and nothing else here,
/// removed with the team (<see cref="DeleteForTeamAsync"/>) and by Reset's Delete memory
/// (<see cref="DeleteForMembersAsync"/>).
/// </para>
/// </summary>
public sealed class AgentStepStore(string databasePath)
{
    private readonly string _connectionString = SqliteOpen.ConnectionString(databasePath);

    /// <summary>
    /// Writes the steps with no row yet and sets the count of those that have one, in one
    /// transaction; each new step is given its <see cref="NewAgentStep.Id"/>.
    /// </summary>
    public async Task WriteAsync(
        string team, string member, long delivery, long? workflow,
        IReadOnlyCollection<NewAgentStep> added, IReadOnlyCollection<NewAgentStep> counted, CancellationToken ct = default)
    {
        if (added.Count == 0 && counted.Count == 0) return;

        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO run_steps (team, member, delivery, workflow, at, kind, label, mcp_tool, count)
                VALUES ($team, $member, $delivery, $workflow, $at, $kind, $label, $mcp, $count)
                RETURNING id
                """;
            insert.Parameters.AddWithValue("$team", team);
            insert.Parameters.AddWithValue("$member", member);
            insert.Parameters.AddWithValue("$delivery", delivery);
            insert.Parameters.AddWithValue("$workflow", (object?)workflow ?? DBNull.Value);
            var at = insert.Parameters.AddWithValue("$at", "");
            var kind = insert.Parameters.AddWithValue("$kind", "");
            var label = insert.Parameters.AddWithValue("$label", "");
            var mcp = insert.Parameters.AddWithValue("$mcp", DBNull.Value);
            var count = insert.Parameters.AddWithValue("$count", 1);

            foreach (var step in added)
            {
                at.Value = MessageSchema.FileChangeInstant(step.At);
                kind.Value = step.Kind;
                label.Value = step.Label;
                mcp.Value = (object?)step.McpTool ?? DBNull.Value;
                count.Value = step.Count;
                step.Id = (long)(await insert.ExecuteScalarAsync(ct))!;
            }
        }

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE run_steps SET count = $count WHERE id = $id";
            var id = update.Parameters.AddWithValue("$id", 0L);
            var count = update.Parameters.AddWithValue("$count", 1);

            foreach (var step in counted.Where(s => s.Id > 0))
            {
                id.Value = step.Id;
                count.Value = step.Count;
                await update.ExecuteNonQueryAsync(ct);
            }
        }

        await transaction.CommitAsync(ct);
    }

    /// <summary>
    /// The rows after <paramref name="after"/> whose time is within <paramref name="from"/> ..
    /// <paramref name="to"/>, oldest first, at most <paramref name="take"/>. Only a team named in
    /// <paramref name="since"/>, and only its rows at or after that instant - its creation - so a
    /// team re-created under the same name reads none of its predecessor's. An empty map reads none.
    /// Each row's run is its delivery's terminal row when the log has it, else the delivery.
    /// </summary>
    public async Task<IReadOnlyList<AgentStep>> ReadAsync(
        DateTimeOffset from, DateTimeOffset to, long after, int take,
        IReadOnlyDictionary<string, DateTimeOffset> since, CancellationToken ct = default)
    {
        if (take <= 0 || since.Count == 0) return [];

        await using var connection = Open();
        await using var command = connection.CreateCommand();

        var scope = new StringBuilder();
        var i = 0;
        foreach (var (team, created) in since)
        {
            if (i > 0) scope.Append(" OR ");
            scope.Append(CultureInfo.InvariantCulture, $"(s.team = $t{i} AND s.at >= $s{i})");
            command.Parameters.AddWithValue($"$t{i}", team);
            command.Parameters.AddWithValue($"$s{i}", MessageSchema.FileChangeInstant(created));
            i++;
        }

        // THE RUN'S TERMINAL ROW, found by causation (ix_messages_causation): the completed or
        // failed row its delivery caused. Until it is written, the delivery stands for the run.
        command.CommandText =
            $"""
             SELECT s.id, s.team, s.member,
                    COALESCE((SELECT MIN(m.seq) FROM messages m
                              WHERE m.causation_seq = s.delivery AND m.type IN ($completed, $failed)), s.delivery),
                    s.workflow, s.at, s.kind, s.label, s.mcp_tool, s.count
             FROM run_steps s
             WHERE s.id > $after AND s.at >= $from AND s.at <= $to AND ({scope})
             ORDER BY s.id
             LIMIT $take
             """;
        command.Parameters.AddWithValue("$after", after);
        command.Parameters.AddWithValue("$from", MessageSchema.FileChangeInstant(from));
        command.Parameters.AddWithValue("$to", MessageSchema.FileChangeInstant(to));
        command.Parameters.AddWithValue("$take", take);
        command.Parameters.AddWithValue("$completed", MessageTypes.Completed);
        command.Parameters.AddWithValue("$failed", MessageTypes.Failed);

        var rows = new List<AgentStep>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new AgentStep(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetInt64(4),
                DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
                reader.GetString(6),
                reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.GetInt32(9)));
        }

        return rows;
    }

    /// <summary>Removes every row of <paramref name="team"/>; answers how many went.</summary>
    public async Task<int> DeleteForTeamAsync(string team, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM run_steps WHERE team = $team";
        command.Parameters.AddWithValue("$team", team);

        return await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Removes the rows of these members of <paramref name="team"/>; answers how many went.</summary>
    public async Task<int> DeleteForMembersAsync(string team, IReadOnlyCollection<string> members, CancellationToken ct = default)
    {
        if (members.Count == 0) return 0;

        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM run_steps WHERE team = $team AND member = $member";
        command.Parameters.AddWithValue("$team", team);
        var member = command.Parameters.AddWithValue("$member", "");

        var removed = 0;
        foreach (var name in members)
        {
            member.Value = name;
            removed += await command.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return removed;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        SqlitePragmas.Apply(connection);
        return connection;
    }
}
