using System.Globalization;
using System.Text;
using Harness.Contracts;
using Harness.Messaging;
using Microsoft.Data.Sqlite;

namespace Harness.Host;

/// <summary>
/// One recorded read of a member's worktree: the <c>workspaceChanged</c> push as it was sent, with
/// the row's <see cref="Id"/> (the replay's paging cursor) and the run's workflow when it was known.
/// </summary>
public sealed record RecordedWorkspaceChange(
    long Id,
    string Team,
    string Member,
    string Repo,
    DateTimeOffset At,
    IReadOnlyList<WorkspaceFile> Files,
    IReadOnlyList<WorkspaceCommit> Commits,
    int More,
    long? Correlation);

/// <summary>
/// <c>file_changes</c> (schema step <c>messages-004</c>) over the log's own file: what
/// <see cref="WorkspaceFileWatch"/> found during every run, kept for the Monitor's replay.
///
/// <para>
/// PATHS, ACTIONS, SHAS AND SUBJECTS ONLY - the push's own shape, never a file's contents or a
/// diff. Rows are pruned by the log's purge (<c>IMessageLog.DeleteAsync</c>) and nothing else here,
/// removed with the team (<see cref="DeleteForTeamAsync"/>) and by Reset's Delete memory
/// (<see cref="DeleteForMembersAsync"/>).
/// </para>
/// </summary>
public sealed class WorkspaceFileChangeStore(string databasePath) : IWorkspaceFileChangeStore
{
    private const string Columns = MonitorRows.ChangeColumns;

    private readonly string _connectionString = SqliteOpen.ConnectionString(databasePath);

    public async Task AppendAsync(WorkspaceChange change, long? correlation, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO file_changes (team, member, repo, at, correlation, files_json, commits_json, more)
            VALUES ($team, $member, $repo, $at, $correlation, $files, $commits, $more)
            """;
        command.Parameters.AddWithValue("$team", change.Team);
        command.Parameters.AddWithValue("$member", change.Member);
        command.Parameters.AddWithValue("$repo", change.Repo);
        command.Parameters.AddWithValue("$at", MonitorRows.Instant(change.At));
        command.Parameters.AddWithValue("$correlation", (object?)correlation ?? DBNull.Value);
        command.Parameters.AddWithValue("$files", MonitorRows.FilesJson(change));
        command.Parameters.AddWithValue("$commits", MonitorRows.CommitsJson(change));
        command.Parameters.AddWithValue("$more", change.More);

        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// The rows after <paramref name="after"/> whose time is within <paramref name="from"/> ..
    /// <paramref name="to"/>, oldest first, at most <paramref name="take"/>. Only a team named in
    /// <paramref name="since"/>, and only its rows at or after that instant - its creation - so a
    /// team re-created under the same name reads none of its predecessor's. An empty map reads none.
    /// </summary>
    public async Task<IReadOnlyList<RecordedWorkspaceChange>> ReadAsync(
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
            scope.Append(CultureInfo.InvariantCulture, $"(team = $t{i} AND at >= $s{i})");
            command.Parameters.AddWithValue($"$t{i}", team);
            command.Parameters.AddWithValue($"$s{i}", MonitorRows.Instant(created));
            i++;
        }

        command.CommandText =
            $"""
             SELECT {Columns} FROM file_changes
             WHERE id > $after AND at >= $from AND at <= $to AND ({scope})
             ORDER BY id
             LIMIT $take
             """;
        command.Parameters.AddWithValue("$after", after);
        command.Parameters.AddWithValue("$from", MonitorRows.Instant(from));
        command.Parameters.AddWithValue("$to", MonitorRows.Instant(to));
        command.Parameters.AddWithValue("$take", take);

        var rows = new List<RecordedWorkspaceChange>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) rows.Add(MonitorRows.ReadChange(reader));

        return rows;
    }

    /// <summary>Removes every row of <paramref name="team"/>; answers how many went.</summary>
    public async Task<int> DeleteForTeamAsync(string team, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM file_changes WHERE team = $team";
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
        command.CommandText = "DELETE FROM file_changes WHERE team = $team AND member = $member";
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
