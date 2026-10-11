using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Harness.Contracts;

namespace Harness.Identity;

/// <summary>
/// Teams and their members, over the same SQLite file the message log and the accounts use.
///
/// Sibling of <see cref="SqliteUserStore"/> in every mechanical respect - same connection string,
/// same Pooling=false, same reliance on AuthSchema's steps having been applied by SchemaMigrator
/// before this is constructed - with one addition that is not optional: <see cref="Open"/> also
/// turns foreign_keys ON, because team_members' ON DELETE CASCADE needs it and that pragma is
/// per-connection.
/// </summary>
public sealed class SqliteTeamStore : ITeamStore
{
    private const long TenantInteractiveSettingsRowId = 1;
    private readonly string _connectionString;

    /// <summary>Absence means the safe value. Only the test fixture asks for anything else.</summary>
    private readonly SqliteDurability _durability;

    public SqliteTeamStore(string databasePath, SqliteDurability durability = SqliteDurability.SurvivesPowerLoss)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,

            // Off, matching SqliteUserStore.
            Pooling = false,
        }.ToString();

        _durability = durability;
    }

    public async Task SaveTeamAsync(PersistedTeam team, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        // Upsert: created_utc is left alone on conflict, so a re-save of an existing team does not
        // reset when it was first created.
        //
        // budget_tokens IS DELIBERATELY ABSENT FROM BOTH HALVES BELOW, and that is narrow writing
        // rather than an oversight. SetBudgetAsync is its ONLY writer, so a full-record save - which several callers do, reading a row and
        // writing it back - cannot silently clear a figure it never read. paused is narrow for the
        // same reason. The same argument that
        // keeps SetFloorAsync narrow rather than reusing SaveMemberAsync.
        //
        // The column is therefore never written on the INSERT half either, so a newly created team
        // starts at NULL - which is "has chosen nothing" and NOT "unlimited". A team created with
        // a figure gets it through the narrow setter, from CreateAsync, before anything wakes.
        //
        // A C# COMMENT, NOT AN SQL ONE. SQLite has no `//`: inside the raw string it would make
        // every team creation answer 500 with `near "/": syntax error`.
        command.CommandText =
            """
            INSERT INTO teams
                (id, name, member_agent, member_agents, additional_instructions, root, repos, env, created_utc)
            VALUES
                ($id, $name, $memberAgent, $memberAgents, $instructions, $root, $repos, $env, $createdUtc)
            ON CONFLICT(id) DO UPDATE SET
                name                   = excluded.name,
                member_agent           = excluded.member_agent,
                member_agents          = excluded.member_agents,
                additional_instructions = excluded.additional_instructions,
                root                   = excluded.root,
                repos                  = excluded.repos,
                env                    = excluded.env
            """;

        command.Parameters.AddWithValue("$id", team.Id);
        command.Parameters.AddWithValue("$name", (object?)team.Name ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$memberAgent",
            team.MemberAgent is { } memberAgent
                ? memberAgent
                : team.MemberAgents?.FirstOrDefault() is { } first
                    ? first
                    : DBNull.Value);
        command.Parameters.AddWithValue(
            "$memberAgents",
            team.MemberAgents is null ? DBNull.Value : JsonSerializer.Serialize(team.MemberAgents));
        command.Parameters.AddWithValue(
            "$instructions",
            string.IsNullOrWhiteSpace(team.AdditionalInstructions) ? DBNull.Value : team.AdditionalInstructions);
        command.Parameters.AddWithValue("$root", (object?)team.Root ?? DBNull.Value);
        command.Parameters.AddWithValue("$repos", JsonSerializer.Serialize(team.Repos ?? []));
        command.Parameters.AddWithValue(
            "$env",
            JsonSerializer.Serialize(
                team.Env ?? new Dictionary<string, string>(StringComparer.Ordinal)));
        command.Parameters.AddWithValue("$createdUtc", DateTimeOffset.UtcNow.ToString("O"));

        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task SetNameAsync(string team, string? name, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText = "UPDATE teams SET name = $name WHERE id = $id";
        command.Parameters.AddWithValue("$name", (object?)name ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", team);

        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<TenantConciergeSettings> ConciergeSettingsAsync(
        CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT interactive_agent
            FROM tenant_interactive_agent_settings
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", TenantInteractiveSettingsRowId);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return new TenantConciergeSettings(null);
        }

        return new TenantConciergeSettings(reader.IsDBNull(0) ? null : reader.GetString(0));
    }

    public Task SetConciergeSettingsAsync(string? agent, TriggerAudit? audit = null, CancellationToken ct = default) =>
        WriteAsync(
            command =>
            {
                command.CommandText =
                    """
                    INSERT INTO tenant_interactive_agent_settings (id, interactive_agent)
                    VALUES ($id, $agent)
                    ON CONFLICT(id) DO UPDATE SET
                        interactive_agent = excluded.interactive_agent
                    """;
                command.Parameters.AddWithValue("$id", TenantInteractiveSettingsRowId);
                command.Parameters.AddWithValue("$agent", (object?)agent ?? DBNull.Value);
            },
            audit,
            ct);

    /// <summary>One statement and, when given, its `tenant_events` row, in one transaction: a
    /// setting with no record of who changed it does not land.</summary>
    private async Task WriteAsync(Action<SqliteCommand> statement, TriggerAudit? audit, CancellationToken ct)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            statement(command);
            await command.ExecuteNonQueryAsync(ct);
        }

        if (audit is not null) await TenantAuditRow.AppendAsync(connection, transaction, audit, ct);
        await transaction.CommitAsync(ct);
    }

    public Task SetAdditionalInstructionsAsync(
        string team, string? text, TriggerAudit? audit = null, CancellationToken ct = default) =>
        WriteAsync(
            command =>
            {
                command.CommandText = "UPDATE teams SET additional_instructions = $text WHERE id = $id COLLATE NOCASE";
                command.Parameters.AddWithValue("$id", team);
                command.Parameters.AddWithValue(
                    "$text", string.IsNullOrWhiteSpace(text) ? DBNull.Value : text.Trim());
            },
            audit,
            ct);

    /// <summary>Narrow: the two member-agent columns only, so a save of this choice cannot write
    /// any other column back as it was when the row was read.</summary>
    public Task SetMemberAgentsAsync(
        string team, IReadOnlyList<string> memberAgents, TriggerAudit audit, CancellationToken ct = default) =>
        WriteAsync(
            command =>
            {
                command.CommandText =
                    "UPDATE teams SET member_agent = $memberAgent, member_agents = $memberAgents WHERE id = $id COLLATE NOCASE";
                command.Parameters.AddWithValue("$id", team);
                command.Parameters.AddWithValue("$memberAgent", (object?)memberAgents.FirstOrDefault() ?? DBNull.Value);
                command.Parameters.AddWithValue("$memberAgents", JsonSerializer.Serialize(memberAgents));
            },
            audit,
            ct);

    public async Task SetReposAsync(string team, IReadOnlyList<string> repos, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText = "UPDATE teams SET repos = $repos WHERE id = $id";
        command.Parameters.AddWithValue("$repos", JsonSerializer.Serialize(repos));
        command.Parameters.AddWithValue("$id", team);

        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Replaces a team's environment wholesale. A narrow single-column update rather than
    /// a re-save of the whole row: composing a PersistedTeam from a row read a moment earlier is how
    /// an edit comes to write every other column back as it was THEN.</summary>
    public async Task SetEnvAsync(
        string team, IReadOnlyDictionary<string, string> env, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText = "UPDATE teams SET env = $env WHERE id = $id";
        command.Parameters.AddWithValue("$env", JsonSerializer.Serialize(env));
        command.Parameters.AddWithValue("$id", team);

        await command.ExecuteNonQueryAsync(ct);
    }

    public Task SetPausedAsync(string team, bool paused, TriggerAudit? audit = null, CancellationToken ct = default) =>
        WriteAsync(
            command =>
            {
                command.CommandText = "UPDATE teams SET paused = $paused WHERE id = $id";
                command.Parameters.AddWithValue("$paused", paused ? 1 : 0);
                command.Parameters.AddWithValue("$id", team);
            },
            audit,
            ct);

    /// <summary>Narrow: the two archive columns and, when archiving, <c>paused</c>. See
    /// <see cref="ITeamStore.SetArchivedAsync"/>.</summary>
    public Task SetArchivedAsync(
        string team, DateTimeOffset? archivedAt, string? archivedBy, TriggerAudit audit, CancellationToken ct = default) =>
        WriteAsync(
            command =>
            {
                command.CommandText = archivedAt is null
                    ? "UPDATE teams SET archived_at = NULL, archived_by = NULL WHERE id = $id COLLATE NOCASE"
                    : "UPDATE teams SET archived_at = $at, archived_by = $by, paused = 1 WHERE id = $id COLLATE NOCASE";
                command.Parameters.AddWithValue("$id", team);
                command.Parameters.AddWithValue(
                    "$at", archivedAt is { } at ? at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) : DBNull.Value);
                command.Parameters.AddWithValue("$by", (object?)archivedBy ?? DBNull.Value);
            },
            audit,
            ct);

    /// <summary>Narrow, single column. See <see cref="ITeamStore.SetBudgetAsync"/>.</summary>
    public Task SetBudgetAsync(string team, long? budgetTokens, TriggerAudit? audit = null, CancellationToken ct = default) =>
        WriteAsync(command => BindBudget(command, team, budgetTokens), audit, ct);

    private static void BindBudget(SqliteCommand command, string team, long? budgetTokens)
    {
        command.CommandText = "UPDATE teams SET budget_tokens = $budget WHERE id = $id";

        // 0 IS STORED AS 0, AND THAT IS THE WHOLE POINT OF THIS LINE.
        //
        // Writing `budgetTokens is > 0 ? value : DBNull` would be wrong, because NULL and 0 mean
        // different things: NULL means the team has
        // chosen nothing and inherits the instance figure, 0 means the team explicitly chose
        // unlimited. Collapsing here would take a person who typed 0 and hand them a bound of
        // 100,000,000 instead, with nothing anywhere saying so. STORE WHAT WAS TYPED; the three
        // states are resolved to one in TeamRegistry.EffectiveWorkflowBudgetFor and nowhere else.
        //
        // A negative value never reaches here - both routes refuse it with 400 before writing.
        command.Parameters.AddWithValue("$budget", (object?)budgetTokens ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", team);
    }

    public async Task SaveMemberAsync(PersistedMember member, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        BindMember(command, member);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task SaveMemberAsync(
        PersistedMember member, IReadOnlyList<TriggerAudit> audits, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            BindMember(command, member);
            await command.ExecuteNonQueryAsync(ct);
        }

        foreach (var audit in audits) await TenantAuditRow.AppendAsync(connection, transaction, audit, ct);
        await transaction.CommitAsync(ct);
    }

    private static void BindMember(SqliteCommand command, PersistedMember member)
    {
        // Upsert: created_utc is left alone on conflict, matching SaveTeamAsync.
        command.CommandText =
            """
            INSERT INTO team_members
                (team, name, label, agent, system_prompt, subscribes, permits, floor_seq, hired_for,
                 system_prompt_set_by, system_prompt_set_by_kind, system_prompt_set_at, created_utc)
            VALUES
                ($team, $name, $label, $agent, $systemPrompt, $subscribes, $permits,
                 $floorSeq, $hiredFor, $setBy, $setByKind, $setAt, $createdUtc)
            ON CONFLICT(team, name) DO UPDATE SET
                label                     = excluded.label,
                agent                     = excluded.agent,
                system_prompt             = excluded.system_prompt,
                subscribes                = excluded.subscribes,
                permits                   = excluded.permits,
                floor_seq                 = excluded.floor_seq,
                hired_for                 = excluded.hired_for,
                system_prompt_set_by      = excluded.system_prompt_set_by,
                system_prompt_set_by_kind = excluded.system_prompt_set_by_kind,
                system_prompt_set_at      = excluded.system_prompt_set_at
            """;

        command.Parameters.AddWithValue("$team", member.Team);
        command.Parameters.AddWithValue("$name", member.Name);
        command.Parameters.AddWithValue("$label", (object?)member.Label ?? DBNull.Value);
        command.Parameters.AddWithValue("$agent", member.Agent);
        command.Parameters.AddWithValue("$systemPrompt", (object?)member.SystemPrompt ?? DBNull.Value);
        command.Parameters.AddWithValue("$subscribes", JsonSerializer.Serialize(member.Subscribes.ToArray()));
        command.Parameters.AddWithValue("$permits", JsonSerializer.Serialize(member.Permits.ToArray()));
        command.Parameters.AddWithValue("$floorSeq", member.FloorSeq);
        command.Parameters.AddWithValue("$hiredFor", (object?)member.HiredFor ?? DBNull.Value);
        command.Parameters.AddWithValue("$setBy", (object?)member.SystemPromptSetBy?.By ?? DBNull.Value);
        command.Parameters.AddWithValue("$setByKind", (object?)member.SystemPromptSetBy?.Kind ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$setAt",
            (object?)member.SystemPromptSetBy?.At.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)
                ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdUtc", DateTimeOffset.UtcNow.ToString("O"));
    }

    public async Task SetFloorAsync(
        string team, string name, long floorSeq, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        // COLLATE NOCASE explicitly on both halves: the columns carry no collation of their own, and
        // ContainerId equality folds case everywhere else.
        command.CommandText =
            """
            UPDATE team_members
            SET floor_seq = $floorSeq
            WHERE team = $team COLLATE NOCASE
              AND name = $name COLLATE NOCASE
            """;

        command.Parameters.AddWithValue("$floorSeq", floorSeq);
        command.Parameters.AddWithValue("$team", team);
        command.Parameters.AddWithValue("$name", name);

        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<DateTimeOffset?> CreatedAtAsync(string team, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText = "SELECT created_utc FROM teams WHERE id = $id";
        command.Parameters.AddWithValue("$id", team);

        return await command.ExecuteScalarAsync(ct) is string created
            ? DateTimeOffset.Parse(created, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            : null;
    }

    public async Task<IReadOnlyList<PersistedTeam>> TeamsAsync(CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText =
            "SELECT id, name, member_agent, member_agents, additional_instructions, root, repos, env, paused, "
            + "budget_tokens, archived_at, archived_by "
            + "FROM teams";

        var teams = new List<PersistedTeam>();
        await using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            teams.Add(new PersistedTeam(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3)
                    ? null
                    : JsonSerializer.Deserialize<string[]>(reader.GetString(3)) ?? [],
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6)
                    ? []
                    : JsonSerializer.Deserialize<string[]>(reader.GetString(6)) ?? [],
                reader.IsDBNull(7)
                    ? new Dictionary<string, string>(StringComparer.Ordinal)
                    : JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(7))
                      ?? new Dictionary<string, string>(StringComparer.Ordinal),
                // `member_agent` IS NOT UNREAD: it is selected above and Teams.cs reads it back, so it
                // is not a drop candidate.
                !reader.IsDBNull(8) && reader.GetInt64(8) != 0,

                // The budget is WRITTEN by a narrow setter that does not go through
                // SaveTeamAsync, so a column missing from this SELECT is a setting that works all
                // session and is gone at the next restart. NULL IS NOT 0: null means the team has
                // chosen nothing, 0 means it chose unlimited.
                reader.IsDBNull(9) ? null : reader.GetInt64(9),

                // Written by the narrow archive setter only, so read here or lost at a restart.
                reader.IsDBNull(10)
                    ? null
                    : DateTimeOffset.Parse(reader.GetString(10), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.IsDBNull(11) ? null : reader.GetString(11)));
        }

        return teams;
    }

    public async Task<IReadOnlyList<PersistedMember>> MembersAsync(CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText =
            "SELECT team, name, label, agent, system_prompt, subscribes, permits, floor_seq, hired_for, "
            + "system_prompt_set_by, system_prompt_set_by_kind, system_prompt_set_at "
            + "FROM team_members";

        var members = new List<PersistedMember>();
        await using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            members.Add(new PersistedMember(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                JsonSerializer.Deserialize<string[]>(reader.GetString(5)) ?? [],
                JsonSerializer.Deserialize<string[]>(reader.GetString(6)) ?? [],
                reader.GetInt64(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),

                // All three or none: they are written together. A row from before `auth-009`
                // holds NULL in each and reads as nobody known.
                reader.IsDBNull(9) || reader.IsDBNull(10) || reader.IsDBNull(11)
                    ? null
                    : new SystemPromptSetter(
                        reader.GetString(9),
                        reader.GetString(10),
                        DateTimeOffset.Parse(
                            reader.GetString(11), CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind))));
        }

        return members;
    }

    public async Task DeleteTeamAsync(string team, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        // team_members carries ON DELETE CASCADE, and Open() below turns foreign_keys ON for this
        // connection - without that this statement removes the team row and leaves every member.
        command.CommandText = "DELETE FROM teams WHERE id = $id";
        command.Parameters.AddWithValue("$id", team);

        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteMemberAsync(string team, string name, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        // Both keys match under BINARY collation, which is exact and is what this wants: the caller
        // resolves the STORED spelling of the team and of the member before it gets here, the same
        // way MemberDeletion builds every path and credential id from it. A COLLATE NOCASE here
        // would forgive a caller that had not, and would then delete a row it was not asked for.
        command.CommandText = "DELETE FROM team_members WHERE team = $team AND name = $name";
        command.Parameters.AddWithValue("$team", team);
        command.Parameters.AddWithValue("$name", name);

        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<RepoDefaultBranch>> RepoDefaultBranchesAsync(CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText = "SELECT team, repo, from_remote, set_by_person FROM team_repo_default_branches";

        var rows = new List<RepoDefaultBranch>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new RepoDefaultBranch(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return rows;
    }

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
            command =>
            {
                command.CommandText =
                    $"""
                    INSERT INTO team_repo_default_branches (team, repo, {column})
                    VALUES ($team, $repo, $branch)
                    ON CONFLICT(team, repo) DO UPDATE SET {column} = excluded.{column}
                    """;
                command.Parameters.AddWithValue("$team", team);
                command.Parameters.AddWithValue("$repo", repo);
                command.Parameters.AddWithValue("$branch", (object?)branch ?? DBNull.Value);
            },
            audit,
            ct);

    public async Task<IReadOnlyList<RepoContributor>> RepoContributorsAsync(CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT team, repo, upstream_url, fork_owner, dco_sign_off, cla_signed_note,
                   pr_url, pr_number, pr_state, pr_read_at
            FROM team_repo_contributors
            """;

        var rows = new List<RepoContributor>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            // A pull request is recorded whole or not at all.
            var pullRequest = reader.IsDBNull(6) || reader.IsDBNull(7) || reader.IsDBNull(8) || reader.IsDBNull(9)
                ? null
                : new RepoPullRequest(
                    reader.GetString(6),
                    reader.GetInt32(7),
                    reader.GetString(8),
                    DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));

            rows.Add(new RepoContributor(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt64(4) != 0,
                reader.IsDBNull(5) ? null : reader.GetString(5),
                pullRequest));
        }

        return rows;
    }

    /// <summary>Narrow: the settings columns only, so a recorded pull request survives a settings change.</summary>
    public async Task SetRepoContributorAsync(RepoContributor settings, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText =
            """
            INSERT INTO team_repo_contributors (team, repo, upstream_url, fork_owner, dco_sign_off, cla_signed_note)
            VALUES ($team, $repo, $upstream, $forkOwner, $dco, $cla)
            ON CONFLICT(team, repo) DO UPDATE SET
                upstream_url    = excluded.upstream_url,
                fork_owner      = excluded.fork_owner,
                dco_sign_off    = excluded.dco_sign_off,
                cla_signed_note = excluded.cla_signed_note
            """;
        command.Parameters.AddWithValue("$team", settings.Team);
        command.Parameters.AddWithValue("$repo", settings.Repo);
        command.Parameters.AddWithValue("$upstream", (object?)settings.UpstreamUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("$forkOwner", (object?)settings.ForkOwner ?? DBNull.Value);
        command.Parameters.AddWithValue("$dco", settings.DcoSignOff ? 1 : 0);
        command.Parameters.AddWithValue("$cla", (object?)settings.ClaSignedNote ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Narrow: the pull-request columns only, so recording one never changes a setting.</summary>
    public async Task SetRepoPullRequestAsync(
        string team, string repo, RepoPullRequest? pullRequest, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText =
            """
            INSERT INTO team_repo_contributors (team, repo, pr_url, pr_number, pr_state, pr_read_at)
            VALUES ($team, $repo, $url, $number, $state, $readAt)
            ON CONFLICT(team, repo) DO UPDATE SET
                pr_url     = excluded.pr_url,
                pr_number  = excluded.pr_number,
                pr_state   = excluded.pr_state,
                pr_read_at = excluded.pr_read_at
            """;
        command.Parameters.AddWithValue("$team", team);
        command.Parameters.AddWithValue("$repo", repo);
        command.Parameters.AddWithValue("$url", (object?)pullRequest?.Url ?? DBNull.Value);
        command.Parameters.AddWithValue("$number", (object?)pullRequest?.Number ?? DBNull.Value);
        command.Parameters.AddWithValue("$state", (object?)pullRequest?.State ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$readAt", (object?)pullRequest?.ReadAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// busy_timeout AND foreign_keys. The second is per-CONNECTION and defaults OFF, so without it
    /// team_members' ON DELETE CASCADE parses, applies as schema and deletes nothing.
    /// </summary>
    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        SqlitePragmas.Apply(connection, durability: _durability);

        return connection;
    }
}
