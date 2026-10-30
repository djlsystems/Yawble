using System.Data.Common;
using Harness.Contracts;
using Harness.Identity;
using Microsoft.Data.SqlClient;

namespace Harness.SqlServer.Identity;

/// <summary>
/// <see cref="ITeamStore"/> over <c>teams</c>, <c>team_members</c>, the two team repository tables and
/// the Concierge's settings row on SQL Server: SQLite's <c>SqliteTeamStore</c> write for write, whose
/// remarks are the argument (narrow setters, an upsert that leaves what it does not name). What a
/// column holds and how a row reads back are <see cref="TeamRows"/>', shared with it.
///
/// <para>
/// A TEAM ID, A MEMBER NAME AND A REPOSITORY FOLD CASE, AS SQLITE'S <c>COLLATE NOCASE</c> COLUMNS DO,
/// because the columns carry the case-insensitive collation: every comparison below is against such a
/// column, so it folds case without saying so, exactly where SQLite's does. A team's name does not.
/// </para>
///
/// <para>
/// EVERY UPSERT IS ONE <c>MERGE</c> UNDER <c>HOLDLOCK</c>. SQLite's <c>ON CONFLICT</c> runs alone; here
/// two saves of one new team, one new member or one new repository row at once would otherwise both
/// find no row and both insert, and one would fail on the key - or, for the two branch columns, one
/// column would be lost. The merge holds the key's range until its transaction ends, so the second
/// waits and updates the row the first made. Every other write is one statement on one row.
/// </para>
///
/// <para>
/// A WRITE AND ITS TENANT ROWS ARE ONE TRANSACTION, through <see cref="SqlServerTenantAuditRow"/>: a
/// change with no record of who made it does not land.
/// </para>
///
/// <para>
/// LOCK ORDER: THE <c>teams</c> ROW, THEN ITS CHILD ROW, THEN <c>tenant_events</c>. A team's deletion
/// locks its row and then, through the cascades, every member and repository row of it; a write of a
/// member or a repository row checks its team for the foreign key after locking its own key. Taken in
/// those two orders, a deletion and a hire of the same team each hold what the other waits for. So
/// every write of a child row first takes its team's row under <c>UPDLOCK, HOLDLOCK</c>
/// (<see cref="TeamLock"/>) in its own transaction, and only then its own key: both orders are teams
/// first. A write of a team row or the settings row locks that one row. <c>tenant_events</c> rows are
/// inserts that read nothing, appended last.
/// </para>
/// </summary>
public sealed class SqlServerTeamStore(string connectionString) : ITeamStore
{
    private const long TenantInteractiveSettingsRowId = 1;

    private readonly string _connectionString = SqlServerOpen.ConnectionString(connectionString);

    private Task<T> RunAsync<T>(Func<SqlConnection, CancellationToken, Task<T>> work, CancellationToken ct) =>
        SqlServerOpen.RunAsync(_connectionString, work, ct);

    private Task<int> ExecuteAsync(string sql, Action<SqlCommand> bind, CancellationToken ct) =>
        SqlServerOpen.ExecuteAsync(_connectionString, sql, bind, ct);

    /// <summary>The team a child row belongs to, taken before the child's own key: see the lock order above.</summary>
    private const string TeamLock = "SELECT COUNT(*) FROM teams WITH (UPDLOCK, HOLDLOCK) WHERE id = @lockTeam";

    /// <summary>
    /// One statement and its <c>tenant_events</c> rows, in one transaction; with no rows and no team to
    /// lock, the statement alone. <paramref name="childOf"/> names the team of a child row the statement
    /// writes, whose row is locked first.
    /// </summary>
    private Task WriteAsync(
        string sql, Action<SqlCommand> bind, IReadOnlyList<TriggerAudit> audits, CancellationToken ct, string? childOf = null) =>
        audits.Count == 0 && childOf is null
            ? ExecuteAsync(sql, bind, ct)
            : RunAsync(
                async (connection, token) =>
                {
                    await using var transaction = await SqlServerOpen.BeginAsync(connection, token);

                    if (childOf is not null)
                    {
                        await using var hold = connection.CreateCommand();
                        hold.Transaction = transaction;
                        hold.CommandText = TeamLock;
                        hold.Parameters.AddWithValue("@lockTeam", childOf);
                        await hold.ExecuteScalarAsync(token);
                    }

                    await using (var command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = sql;
                        bind(command);
                        await command.ExecuteNonQueryAsync(token);
                    }

                    foreach (var audit in audits) await SqlServerTenantAuditRow.AppendAsync(connection, transaction, audit, token);
                    await SqlServerOpen.CommitAsync(transaction, token);
                    return 0;
                },
                ct);

    private Task WriteAsync(string sql, Action<SqlCommand> bind, TriggerAudit? audit, CancellationToken ct, string? childOf = null) =>
        WriteAsync(sql, bind, audit is null ? [] : [audit], ct, childOf);

    /// <summary>
    /// Upsert: created_utc is left alone on a match, and budget_tokens and paused are absent from both
    /// halves - their narrow setters are their only writers, so a full-record save cannot clear them.
    /// </summary>
    public Task SaveTeamAsync(PersistedTeam team, CancellationToken ct = default) =>
        RunAsync(
            async (connection, token) =>
            {
                await WriteTeamAsync(connection, null, team, token);
                return true;
            },
            ct);

    public Task SaveTeamAsync(
        PersistedTeam team, Func<DbConnection, DbTransaction, CancellationToken, Task> withRow, CancellationToken ct = default) =>
        RunAsync(
            async (connection, token) =>
            {
                await using var transaction = await SqlServerOpen.BeginAsync(connection, token);

                await WriteTeamAsync(connection, transaction, team, token);
                await withRow(connection, transaction, token);

                await SqlServerOpen.CommitAsync(transaction, token);
                return true;
            },
            ct);

    private static async Task WriteTeamAsync(SqlConnection connection, SqlTransaction? transaction, PersistedTeam team, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            MERGE teams WITH (HOLDLOCK) AS t
            USING (SELECT @id AS id) AS n ON t.id = n.id
            WHEN MATCHED THEN UPDATE SET
                name = @name, member_agent = @memberAgent, member_agents = @memberAgents,
                additional_instructions = @instructions, root = @root, repos = @repos, env = @env
            WHEN NOT MATCHED THEN INSERT
                (id, name, member_agent, member_agents, additional_instructions, root, repos, env, created_utc)
                VALUES (@id, @name, @memberAgent, @memberAgents, @instructions, @root, @repos, @env, @createdUtc);
            """;
        command.Parameters.AddWithValue("@id", team.Id);
        command.Parameters.AddWithValue("@name", TeamRows.Text(team.Name));
        command.Parameters.AddWithValue("@memberAgent", TeamRows.MemberAgent(team));
        command.Parameters.AddWithValue("@memberAgents", TeamRows.MemberAgents(team.MemberAgents));
        command.Parameters.AddWithValue("@instructions", TeamRows.Instructions(team.AdditionalInstructions));
        command.Parameters.AddWithValue("@root", TeamRows.Text(team.Root));
        command.Parameters.AddWithValue("@repos", TeamRows.Repos(team.Repos));
        command.Parameters.AddWithValue("@env", TeamRows.Env(team.Env));
        command.Parameters.AddWithValue("@createdUtc", Instant.Now());
        await command.ExecuteNonQueryAsync(ct);
    }

    public Task SetNameAsync(string team, string? name, CancellationToken ct = default) =>
        ExecuteAsync(
            "UPDATE teams SET name = @name WHERE id = @id",
            command =>
            {
                command.Parameters.AddWithValue("@name", TeamRows.Text(name));
                command.Parameters.AddWithValue("@id", team);
            },
            ct);

    public Task<TenantConciergeSettings> ConciergeSettingsAsync(CancellationToken ct = default) =>
        RunAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT interactive_agent FROM tenant_interactive_agent_settings WHERE id = @id";
                command.Parameters.AddWithValue("@id", TenantInteractiveSettingsRowId);

                await using var reader = await command.ExecuteReaderAsync(token);
                return !await reader.ReadAsync(token)
                    ? new TenantConciergeSettings(null)
                    : new TenantConciergeSettings(reader.IsDBNull(0) ? null : reader.GetString(0));
            },
            ct);

    public Task SetConciergeSettingsAsync(string? agent, TriggerAudit? audit = null, CancellationToken ct = default) =>
        WriteAsync(
            """
            MERGE tenant_interactive_agent_settings WITH (HOLDLOCK) AS s
            USING (SELECT @id AS id) AS n ON s.id = n.id
            WHEN MATCHED THEN UPDATE SET interactive_agent = @agent
            WHEN NOT MATCHED THEN INSERT (id, interactive_agent) VALUES (@id, @agent);
            """,
            command =>
            {
                command.Parameters.AddWithValue("@id", TenantInteractiveSettingsRowId);
                command.Parameters.AddWithValue("@agent", TeamRows.Text(agent));
            },
            audit,
            ct);

    public Task SetAdditionalInstructionsAsync(
        string team, string? text, TriggerAudit? audit = null, CancellationToken ct = default) =>
        WriteAsync(
            "UPDATE teams SET additional_instructions = @text WHERE id = @id",
            command =>
            {
                command.Parameters.AddWithValue("@id", team);
                command.Parameters.AddWithValue("@text", TeamRows.Instructions(text?.Trim()));
            },
            audit,
            ct);

    public Task SetMemberAgentsAsync(
        string team, IReadOnlyList<string> memberAgents, TriggerAudit audit, CancellationToken ct = default) =>
        WriteAsync(
            "UPDATE teams SET member_agent = @memberAgent, member_agents = @memberAgents WHERE id = @id",
            command =>
            {
                command.Parameters.AddWithValue("@id", team);
                command.Parameters.AddWithValue("@memberAgent", TeamRows.Text(memberAgents.FirstOrDefault()));
                command.Parameters.AddWithValue("@memberAgents", TeamRows.MemberAgents(memberAgents));
            },
            audit,
            ct);

    public Task SetReposAsync(string team, IReadOnlyList<string> repos, TriggerAudit? audit = null, CancellationToken ct = default) =>
        WriteAsync(
            "UPDATE teams SET repos = @repos WHERE id = @id",
            command =>
            {
                command.Parameters.AddWithValue("@repos", TeamRows.Repos(repos));
                command.Parameters.AddWithValue("@id", team);
            },
            audit,
            ct);

    public Task SetEnvAsync(
        string team, IReadOnlyDictionary<string, string> env, TriggerAudit? audit = null, CancellationToken ct = default) =>
        WriteAsync(
            "UPDATE teams SET env = @env WHERE id = @id",
            command =>
            {
                command.Parameters.AddWithValue("@env", TeamRows.Env(env));
                command.Parameters.AddWithValue("@id", team);
            },
            audit,
            ct);

    public Task SetPausedAsync(string team, bool paused, TriggerAudit? audit = null, CancellationToken ct = default) =>
        WriteAsync(
            "UPDATE teams SET paused = @paused WHERE id = @id",
            command =>
            {
                command.Parameters.AddWithValue("@paused", paused ? 1L : 0L);
                command.Parameters.AddWithValue("@id", team);
            },
            audit,
            ct);

    public Task SetArchivedAsync(
        string team, DateTimeOffset? archivedAt, string? archivedBy, TriggerAudit audit, CancellationToken ct = default) =>
        WriteAsync(
            archivedAt is null
                ? "UPDATE teams SET archived_at = NULL, archived_by = NULL WHERE id = @id"
                : "UPDATE teams SET archived_at = @at, archived_by = @by, paused = 1 WHERE id = @id",
            command =>
            {
                command.Parameters.AddWithValue("@id", team);
                command.Parameters.AddWithValue("@at", TeamRows.Stamp(archivedAt));
                command.Parameters.AddWithValue("@by", TeamRows.Text(archivedBy));
            },
            audit,
            ct);

    /// <summary>
    /// Narrow, single column. 0 IS STORED AS 0 and NULL AS NULL: NULL is "chosen nothing" and 0 is
    /// "chose unlimited", resolved in TeamRegistry.EffectiveWorkflowBudgetFor and nowhere else.
    /// </summary>
    public Task SetBudgetAsync(string team, long? budgetTokens, TriggerAudit? audit = null, CancellationToken ct = default) =>
        WriteAsync(
            "UPDATE teams SET budget_tokens = @budget WHERE id = @id",
            command =>
            {
                command.Parameters.Add("@budget", System.Data.SqlDbType.BigInt).Value = (object?)budgetTokens ?? DBNull.Value;
                command.Parameters.AddWithValue("@id", team);
            },
            audit,
            ct);

    public Task SaveMemberAsync(PersistedMember member, CancellationToken ct = default) =>
        WriteAsync(MemberUpsert, command => BindMember(command, member), [], ct, childOf: member.Team);

    public Task SaveMemberAsync(PersistedMember member, IReadOnlyList<TriggerAudit> audits, CancellationToken ct = default) =>
        WriteAsync(MemberUpsert, command => BindMember(command, member), audits, ct, childOf: member.Team);

    /// <summary>Upsert: created_utc is left alone on a match, matching <see cref="SaveTeamAsync"/>.</summary>
    private const string MemberUpsert =
        """
        MERGE team_members WITH (HOLDLOCK) AS m
        USING (SELECT @team AS team, @name AS name) AS n ON m.team = n.team AND m.name = n.name
        WHEN MATCHED THEN UPDATE SET
            label = @label, agent = @agent, system_prompt = @systemPrompt, subscribes = @subscribes,
            permits = @permits, floor_seq = @floorSeq, hired_for = @hiredFor,
            system_prompt_set_by = @setBy, system_prompt_set_by_kind = @setByKind, system_prompt_set_at = @setAt
        WHEN NOT MATCHED THEN INSERT
            (team, name, label, agent, system_prompt, subscribes, permits, floor_seq, hired_for,
             system_prompt_set_by, system_prompt_set_by_kind, system_prompt_set_at, created_utc)
            VALUES (@team, @name, @label, @agent, @systemPrompt, @subscribes, @permits, @floorSeq, @hiredFor,
             @setBy, @setByKind, @setAt, @createdUtc);
        """;

    private static void BindMember(SqlCommand command, PersistedMember member)
    {
        command.Parameters.AddWithValue("@team", member.Team);
        command.Parameters.AddWithValue("@name", member.Name);
        command.Parameters.AddWithValue("@label", TeamRows.Text(member.Label));
        command.Parameters.AddWithValue("@agent", member.Agent);
        command.Parameters.AddWithValue("@systemPrompt", TeamRows.Text(member.SystemPrompt));
        command.Parameters.AddWithValue("@subscribes", TeamRows.List(member.Subscribes));
        command.Parameters.AddWithValue("@permits", TeamRows.List(member.Permits));
        command.Parameters.AddWithValue("@floorSeq", member.FloorSeq);
        command.Parameters.AddWithValue("@hiredFor", TeamRows.Text(member.HiredFor));
        command.Parameters.AddWithValue("@setBy", TeamRows.Text(member.SystemPromptSetBy?.By));
        command.Parameters.AddWithValue("@setByKind", TeamRows.Text(member.SystemPromptSetBy?.Kind));
        command.Parameters.AddWithValue("@setAt", TeamRows.Stamp(member.SystemPromptSetBy?.At));
        command.Parameters.AddWithValue("@createdUtc", Instant.Now());
    }

    public Task SetFloorAsync(string team, string name, long floorSeq, CancellationToken ct = default) =>
        ExecuteAsync(
            "UPDATE team_members SET floor_seq = @floorSeq WHERE team = @team AND name = @name",
            command =>
            {
                command.Parameters.AddWithValue("@floorSeq", floorSeq);
                command.Parameters.AddWithValue("@team", team);
                command.Parameters.AddWithValue("@name", name);
            },
            ct);

    public Task<DateTimeOffset?> CreatedAtAsync(string team, CancellationToken ct = default) =>
        RunAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT created_utc FROM teams WHERE id = @id";
                command.Parameters.AddWithValue("@id", team);
                return await command.ExecuteScalarAsync(token) is string created ? Instant.Parse(created) : (DateTimeOffset?)null;
            },
            ct);

    public Task<ActivityFrom?> ActivityFromAsync(string team, CancellationToken ct = default) =>
        RunAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT activity_from, activity_from_by FROM teams WHERE id = @id";
                command.Parameters.AddWithValue("@id", team);

                await using var reader = await command.ExecuteReaderAsync(token);
                return await reader.ReadAsync(token) && !reader.IsDBNull(0)
                    ? new ActivityFrom(Instant.Parse(reader.GetString(0)), reader.IsDBNull(1) ? "" : reader.GetString(1))
                    : null;
            },
            ct);

    public Task SetActivityFromAsync(string team, ActivityFrom? from, TriggerAudit audit, CancellationToken ct = default) =>
        WriteAsync(
            "UPDATE teams SET activity_from = @at, activity_from_by = @by WHERE id = @id",
            command =>
            {
                command.Parameters.AddWithValue("@id", team);
                command.Parameters.AddWithValue("@at", TeamRows.Stamp(from?.At));
                command.Parameters.AddWithValue("@by", TeamRows.Text(from?.By));
            },
            audit,
            ct);

    public Task<IReadOnlyList<PersistedTeam>> TeamsAsync(CancellationToken ct = default) =>
        ReadAllAsync($"SELECT {TeamRows.TeamColumns} FROM teams", TeamRows.ReadTeam, ct);

    public Task<IReadOnlyList<PersistedMember>> MembersAsync(CancellationToken ct = default) =>
        ReadAllAsync($"SELECT {TeamRows.MemberColumns} FROM team_members", TeamRows.ReadMember, ct);

    public Task<PersistedMember?> MemberAsync(string team, string name, CancellationToken ct = default) =>
        RunAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = $"SELECT {TeamRows.MemberColumns} FROM team_members WHERE team = @team AND name = @name";
                command.Parameters.AddWithValue("@team", team);
                command.Parameters.AddWithValue("@name", name);

                await using var reader = await command.ExecuteReaderAsync(token);
                return await reader.ReadAsync(token) ? TeamRows.ReadMember(reader) : null;
            },
            ct);

    /// <summary>The members and the repository rows go by the foreign keys' cascades, as on SQLite.</summary>
    public Task DeleteTeamAsync(string team, CancellationToken ct = default) =>
        ExecuteAsync("DELETE FROM teams WHERE id = @id", command => command.Parameters.AddWithValue("@id", team), ct);

    public Task DeleteMemberAsync(string team, string name, CancellationToken ct = default) =>
        ExecuteAsync(
            "DELETE FROM team_members WHERE team = @team AND name = @name",
            command =>
            {
                command.Parameters.AddWithValue("@team", team);
                command.Parameters.AddWithValue("@name", name);
            },
            ct);

    public Task<IReadOnlyList<RepoDefaultBranch>> RepoDefaultBranchesAsync(CancellationToken ct = default) =>
        ReadAllAsync(
            "SELECT team, repo, from_remote, set_by_person FROM team_repo_default_branches", TeamRows.ReadDefaultBranch, ct);

    /// <summary>Narrow: writes `from_remote` only, so a person's choice survives every Fetch.</summary>
    public Task SetRemoteDefaultBranchAsync(string team, string repo, string? branch, CancellationToken ct = default) =>
        UpsertDefaultBranchAsync("from_remote", team, repo, branch, null, ct);

    /// <summary>Narrow: writes `set_by_person` only, so clearing it falls back to what the remote named.</summary>
    public Task SetPersonDefaultBranchAsync(
        string team, string repo, string? branch, TriggerAudit? audit = null, CancellationToken ct = default) =>
        UpsertDefaultBranchAsync("set_by_person", team, repo, branch, audit, ct);

    // `column` is one of the two literals above, never caller input.
    private Task UpsertDefaultBranchAsync(
        string column, string team, string repo, string? branch, TriggerAudit? audit, CancellationToken ct) =>
        WriteAsync(
            $"""
             MERGE team_repo_default_branches WITH (HOLDLOCK) AS b
             USING (SELECT @team AS team, @repo AS repo) AS n ON b.team = n.team AND b.repo = n.repo
             WHEN MATCHED THEN UPDATE SET {column} = @branch
             WHEN NOT MATCHED THEN INSERT (team, repo, {column}) VALUES (@team, @repo, @branch);
             """,
            command =>
            {
                command.Parameters.AddWithValue("@team", team);
                command.Parameters.AddWithValue("@repo", repo);
                command.Parameters.AddWithValue("@branch", TeamRows.Text(branch));
            },
            audit,
            ct,
            childOf: team);

    public Task<IReadOnlyList<RepoContributor>> RepoContributorsAsync(CancellationToken ct = default) =>
        ReadAllAsync($"SELECT {TeamRows.ContributorColumns} FROM team_repo_contributors", TeamRows.ReadContributor, ct);

    /// <summary>Narrow: the settings columns only, so a recorded pull request survives a settings change.</summary>
    public Task SetRepoContributorAsync(RepoContributor settings, CancellationToken ct = default) =>
        WriteAsync(
            """
            MERGE team_repo_contributors WITH (HOLDLOCK) AS c
            USING (SELECT @team AS team, @repo AS repo) AS n ON c.team = n.team AND c.repo = n.repo
            WHEN MATCHED THEN UPDATE SET
                upstream_url = @upstream, fork_owner = @forkOwner, dco_sign_off = @dco, cla_signed_note = @cla
            WHEN NOT MATCHED THEN INSERT (team, repo, upstream_url, fork_owner, dco_sign_off, cla_signed_note)
                VALUES (@team, @repo, @upstream, @forkOwner, @dco, @cla);
            """,
            command =>
            {
                command.Parameters.AddWithValue("@team", settings.Team);
                command.Parameters.AddWithValue("@repo", settings.Repo);
                command.Parameters.AddWithValue("@upstream", TeamRows.Text(settings.UpstreamUrl));
                command.Parameters.AddWithValue("@forkOwner", TeamRows.Text(settings.ForkOwner));
                command.Parameters.AddWithValue("@dco", settings.DcoSignOff ? 1L : 0L);
                command.Parameters.AddWithValue("@cla", TeamRows.Text(settings.ClaSignedNote));
            },
            [],
            ct,
            childOf: settings.Team);

    /// <summary>Narrow: the pull-request columns only, so recording one never changes a setting.</summary>
    public Task SetRepoPullRequestAsync(string team, string repo, RepoPullRequest? pullRequest, CancellationToken ct = default) =>
        WriteAsync(
            """
            MERGE team_repo_contributors WITH (HOLDLOCK) AS c
            USING (SELECT @team AS team, @repo AS repo) AS n ON c.team = n.team AND c.repo = n.repo
            WHEN MATCHED THEN UPDATE SET
                pr_url = @url, pr_number = @number, pr_state = @state, pr_read_at = @readAt
            WHEN NOT MATCHED THEN INSERT (team, repo, pr_url, pr_number, pr_state, pr_read_at)
                VALUES (@team, @repo, @url, @number, @state, @readAt);
            """,
            command =>
            {
                command.Parameters.AddWithValue("@team", team);
                command.Parameters.AddWithValue("@repo", repo);
                command.Parameters.AddWithValue("@url", TeamRows.Text(pullRequest?.Url));
                command.Parameters.Add("@number", System.Data.SqlDbType.BigInt).Value = (object?)pullRequest?.Number ?? DBNull.Value;
                command.Parameters.AddWithValue("@state", TeamRows.Text(pullRequest?.State));
                command.Parameters.AddWithValue("@readAt", TeamRows.Stamp(pullRequest?.ReadAt));
            },
            [],
            ct,
            childOf: team);

    private Task<IReadOnlyList<T>> ReadAllAsync<T>(string sql, Func<System.Data.Common.DbDataReader, T> read, CancellationToken ct) =>
        RunAsync<IReadOnlyList<T>>(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;

                var rows = new List<T>();
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token)) rows.Add(read(reader));

                return rows;
            },
            ct);
}
