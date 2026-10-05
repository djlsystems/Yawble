using Harness.Backlog;
using Harness.Contracts;
using Harness.Host;
using Harness.Identity;
using Harness.Messaging;
using Microsoft.Data.Sqlite;

namespace Harness.Tests;

/// <summary>
/// The baseline schema: one step per store, applied to an empty data volume, and every
/// store working over what it made.
/// </summary>
public sealed class FreshVolumeTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"harness-fresh-{Guid.NewGuid():N}");

    private string Database => Path.Combine(_directory, "messages.db");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public FreshVolumeTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    /// <summary>
    /// The baseline is one step per store; every later change is a NEW step after them (AGENTS.md,
    /// Data), never an edit. `auth-002` is `tenant_settings`; `auth-003` is the nullable Concierge
    /// agent; `auth-004` is team additional instructions; `auth-005` is the folder-trigger columns;
    /// `auth-006` is the per-repository default branch; `auth-007` is the per-repository contributor
    /// settings; `auth-008` is a plugin member's configuration and secret bindings; `auth-009` is who
    /// last set a member's own instructions, and when; `auth-010` is a trigger's wake choice and daily
    /// token cap; `auth-011` is how many fires the cap skipped today; `auth-012` is the folders whose removal did not finish; `auth-013` is what a removal retry's own deletes left on the directories it judged unwritten; `auth-014` is connections: OAuth providers, connected accounts, pending flows and a plugin member's slot bindings; `auth-015` is a team's sites, their versions and their data; `auth-016` is which solution package each team came from; `skill-004` is team skills; `messages-002` is the run a deferred batch item was deferred from, on its pending delivery; `outcome-001` is the usage ledger (`usage_ledger`, `workflow_ledger`); `outcome-002` is the outcomes and their append-only workflow links; `outcome-003` is a delivery's attribution and each nudge, recorded at delivery (`delivery_ledger`, `nudge_ledger`); `outcome-004` makes a link's outcome nullable, for a person's unlink; `outcome-005` indexes `usage_ledger` by team and end time, for the team activity read; `outcome-006` is every admission hold (`admission_holds`), append-only but for its one close; `auth-017` is the outcome a trigger's fires serve; `backlog-002` is a dispatch's recorded tips, where it started and its stored landed; `backlog-003` is the outcome a backlog item serves; `backlog-004` is a dispatch's start that could not be recorded, retried while its branch is unchanged; `auth-018` is the email of the person who last configured a trigger; `auth-019` is the credential issued to each agent CLI.
    /// </summary>
    [Fact]
    public void The_steps_are_the_squash_and_the_steps_added_after_it()
    {
        Assert.Equal(
            ["messages-001", "messages-002", "auth-001", "auth-002", "auth-003", "auth-004", "auth-005", "auth-006", "auth-007", "auth-008", "auth-009", "auth-010", "auth-011", "auth-012", "auth-013", "auth-014", "auth-015", "auth-016", "auth-017", "auth-018", "auth-019", "skill-001", "skill-002", "skill-003", "skill-004", "backlog-001", "backlog-002", "backlog-003", "backlog-004", "outcome-001", "outcome-002", "outcome-003", "outcome-004", "outcome-005", "outcome-006"],
            SchemaModules.All.Select(s => s.Id));
    }

    [Fact]
    public async Task An_empty_volume_migrates_to_only_the_live_tables()
    {
        var migrator = new SchemaMigrator(Database);

        await migrator.ApplyAsync(SchemaModules.All, ct: Ct);

        Assert.Equal(
            ["auth-001", "auth-002", "auth-003", "auth-004", "auth-005", "auth-006", "auth-007", "auth-008", "auth-009", "auth-010", "auth-011", "auth-012", "auth-013", "auth-014", "auth-015", "auth-016", "auth-017", "auth-018", "auth-019", "backlog-001", "backlog-002", "backlog-003", "backlog-004", "messages-001", "messages-002", "outcome-001", "outcome-002", "outcome-003", "outcome-004", "outcome-005", "outcome-006", "skill-001", "skill-002", "skill-003", "skill-004"],
            await migrator.AppliedAsync(ct: Ct));

        // Nothing pending on the second start, so no backup and no change.
        Assert.Null(await migrator.ApplyAsync(SchemaModules.All, ct: Ct));

        Assert.Contains("ix_usage_ledger_team_ended", await ColumnAsync("SELECT name FROM sqlite_master WHERE type = 'index'"));

        var tables = await ColumnAsync("SELECT name FROM sqlite_master WHERE type = 'table'");
        Assert.DoesNotContain(tables, t => t.StartsWith("auditor_", StringComparison.Ordinal));
        Assert.DoesNotContain("tenant_agents", tables);
        Assert.DoesNotContain("user_grants", tables);
        Assert.Contains("tenant_settings", tables);

        var teams = await ColumnAsync("SELECT name FROM pragma_table_info('teams')");
        Assert.DoesNotContain("scope", teams);
        Assert.DoesNotContain(teams, c => c.StartsWith("interactive_", StringComparison.Ordinal));

        Assert.DoesNotContain("is_admin", await ColumnAsync("SELECT name FROM pragma_table_info('users')"));

        // The tenant Concierge row is seeded with NO agent: nobody has chosen one, so the launcher
        // picks through ConciergeAgentDefault rather than being handed a preset name from the seed.
        Assert.Null((await new SqliteTeamStore(Database).ConciergeSettingsAsync(ct: Ct)).Agent);
    }

    /// <summary>
    /// A volume made before `auth-003` carries the seeded `claude` whether or not anybody chose it.
    /// The tenant log tells the two apart: a person's choice writes `team.concierge-changed`, the
    /// seed never did. The seed becomes NULL; a choice is kept.
    /// </summary>
    [Theory]
    [InlineData(false, null)]
    [InlineData(true, "claude")]
    public async Task A_seeded_concierge_agent_nobody_chose_becomes_null(bool personChose, string? expected)
    {
        var migrator = new SchemaMigrator(Database);
        await migrator.ApplyAsync([.. SchemaModules.All.Where(s => s.Id != "auth-003")], ct: Ct);

        if (personChose)
        {
            await ExecuteAsync(
                $"""
                INSERT INTO tenant_events (occurred_at, action, detail)
                VALUES ('2026-09-01T00:00:00Z', '{TenantActions.ConciergeChanged}', NULL)
                """);
        }

        await ExecuteAsync("UPDATE tenant_interactive_agent_settings SET interactive_prompt_name = 'Concierge'");

        await migrator.ApplyAsync(SchemaModules.All, ct: Ct);

        var settings = await new SqliteTeamStore(Database).ConciergeSettingsAsync(ct: Ct);
        Assert.Equal(expected, settings.Agent);
    }

    /// <summary>
    /// A repository attached before `auth-006` has no stored default branch afterwards: it is
    /// not known until a clone or a Fetch reads one. Nothing backfills `main`.
    /// </summary>
    [Fact]
    public async Task A_repository_from_before_the_default_branch_step_is_not_known_afterwards()
    {
        var migrator = new SchemaMigrator(Database);
        await migrator.ApplyAsync([.. SchemaModules.All.Where(s => s.Id != "auth-006")], ct: Ct);

        await ExecuteAsync(
            """
            INSERT INTO teams (id, repos, created_utc)
            VALUES ('old-team', '["https://github.com/example/Widget.git"]', '2026-09-01T00:00:00Z')
            """);

        await migrator.ApplyAsync(SchemaModules.All, ct: Ct);

        Assert.Empty(await new SqliteTeamStore(Database).RepoDefaultBranchesAsync(Ct));
    }

    /// <summary>
    /// A repository attached before `auth-007` is owned afterwards: no upstream, no sign-off,
    /// no pull request. Nothing is backfilled.
    /// </summary>
    [Fact]
    public async Task A_repository_from_before_the_contributor_step_is_owned_afterwards()
    {
        var migrator = new SchemaMigrator(Database);
        await migrator.ApplyAsync([.. SchemaModules.All.Where(s => s.Id != "auth-007")], ct: Ct);

        await ExecuteAsync(
            """
            INSERT INTO teams (id, repos, created_utc)
            VALUES ('old-team', '["https://github.com/example/Widget.git"]', '2026-09-01T00:00:00Z')
            """);

        await migrator.ApplyAsync(SchemaModules.All, ct: Ct);

        Assert.Empty(await new SqliteTeamStore(Database).RepoContributorsAsync(Ct));
    }

    /// <summary>
    /// `auth-009` on an empty volume: a member's row carries who last set its own instructions and
    /// when, and one saved with nobody reads back with nobody.
    /// </summary>
    [Fact]
    public async Task The_instructions_author_step_applies_to_an_empty_volume_and_round_trips()
    {
        await new SchemaMigrator(Database).ApplyAsync(SchemaModules.All, ct: Ct);

        Assert.Superset(
            new HashSet<string> { "system_prompt_set_by", "system_prompt_set_by_kind", "system_prompt_set_at" },
            (await ColumnAsync("SELECT name FROM pragma_table_info('team_members')")).ToHashSet());

        var store = new SqliteTeamStore(Database);
        await store.SaveTeamAsync(new PersistedTeam("Alpha", null, "claude-headless", ["claude-headless"], null), Ct);

        var at = new DateTimeOffset(2026, 9, 28, 10, 11, 12, TimeSpan.Zero);
        await store.SaveMemberAsync(
            new PersistedMember("Alpha", "Writer", null, "claude-headless", "Write docs.", [], [], 0,
                SystemPromptSetBy: new SystemPromptSetter("Manager", SystemPromptSetter.Manager, at)), Ct);
        await store.SaveMemberAsync(
            new PersistedMember("Alpha", "Nobody", null, "claude-headless", null, [], [], 0), Ct);

        var members = (await store.MembersAsync(Ct)).ToDictionary(m => m.Name);

        Assert.Equal(new SystemPromptSetter("Manager", "manager", at), members["Writer"].SystemPromptSetBy);
        Assert.Null(members["Nobody"].SystemPromptSetBy);
    }

    /// <summary>
    /// `auth-016`: which solution package a team came from. A row lands with its tenant row in one
    /// transaction, reads back whole, is replaced by an update and goes with a delete.
    /// </summary>
    [Fact]
    public async Task The_team_solutions_step_applies_to_an_empty_volume_and_round_trips()
    {
        await new SchemaMigrator(Database).ApplyAsync(SchemaModules.All, ct: Ct);
        var store = new SqliteTeamSolutionStore(Database);
        var at = new DateTimeOffset(2026, 9, 29, 10, 0, 0, TimeSpan.Zero);
        var row = new TeamSolutionRow(
            "JobTracker", "job-tracker", "Job Tracker", "1.0.0", "/data/documents/Alpha/job-tracker", at, "person@example.test", null,
            "{\"format\":1}",
            new Dictionary<string, string> { ["skill:playbook"] = "abc" },
            new Dictionary<string, string> { ["Coordinator"] = "Manager" },
            new Dictionary<string, string> { ["Scan"] = "t1" },
            new Dictionary<string, string> { ["job-board"] = "0.1.0" });

        await store.SaveAsync(row, new TriggerAudit(null, "person@example.test", TenantActions.SolutionInstalled, "JobTracker", "Job Tracker", null), Ct);
        await store.SaveAsync(row with { Version = "1.1.0", UpdatedAt = at }, new TriggerAudit(null, null, TenantActions.SolutionUpdated, "JobTracker", null, null), Ct);

        var read = (await store.FindAsync("jobtracker", Ct))!;
        Assert.Equal(("job-tracker", "1.1.0", at), (read.PackageId, read.Version, read.UpdatedAt));
        Assert.Equal("Manager", read.Members["Coordinator"]);
        Assert.Equal("0.1.0", read.Plugins["job-board"]);
        Assert.Equal("abc", read.Digests["skill:playbook"]);
        Assert.Equal(
            [TenantActions.SolutionInstalled, TenantActions.SolutionUpdated],
            await ColumnAsync("SELECT action FROM tenant_events WHERE action LIKE 'solution.%' ORDER BY seq"));

        Assert.True(await store.DeleteAsync("JobTracker", null, Ct));
        Assert.Empty(await store.ListAsync(Ct));
    }

    /// <summary>
    /// A member hired before `auth-009` keeps its instructions and has NOBODY recorded as their
    /// author - nothing is backfilled - until it is next edited.
    /// </summary>
    [Fact]
    public async Task A_member_from_before_the_instructions_author_step_has_nobody_until_edited()
    {
        var migrator = new SchemaMigrator(Database);
        await migrator.ApplyAsync([.. SchemaModules.All.Where(s => s.Id != "auth-009")], ct: Ct);

        await ExecuteAsync(
            """
            INSERT INTO teams (id, created_utc) VALUES ('old-team', '2026-09-01T00:00:00Z');
            INSERT INTO team_members
                (team, name, agent, system_prompt, subscribes, permits, floor_seq, created_utc)
            VALUES
                ('old-team', 'Writer', 'claude-headless', 'Write docs.', '[]', '[]', 0,
                 '2026-09-01T00:00:00Z');
            """);

        await migrator.ApplyAsync(SchemaModules.All, ct: Ct);

        var store = new SqliteTeamStore(Database);
        var before = Assert.Single(await store.MembersAsync(Ct));

        Assert.Equal("Write docs.", before.SystemPrompt);
        Assert.Null(before.SystemPromptSetBy);

        var at = new DateTimeOffset(2026, 9, 28, 10, 11, 12, TimeSpan.Zero);
        await store.SaveMemberAsync(
            before with
            {
                SystemPrompt = "Write better docs.",
                SystemPromptSetBy = new SystemPromptSetter("person@example.test", SystemPromptSetter.Person, at),
            },
            Ct);

        Assert.Equal(
            new SystemPromptSetter("person@example.test", "person", at),
            Assert.Single(await store.MembersAsync(Ct)).SystemPromptSetBy);
    }

    /// <summary>
    /// `outcome-002`, `auth-017` and `backlog-003` apply to a volume from before them: a trigger and a
    /// backlog item from before serve no outcome until a person names one, and the new tables work.
    /// </summary>
    [Fact]
    public async Task A_trigger_and_an_item_from_before_the_outcome_steps_serve_no_outcome_until_one_is_named()
    {
        var migrator = new SchemaMigrator(Database);
        // `outcome-004` builds `workflow_outcome_links` again, so it waits for the step that made it.
        var outcomeSteps = new[] { "outcome-002", "auth-017", "backlog-003", "outcome-004" };
        await migrator.ApplyAsync([.. SchemaModules.All.Where(s => !outcomeSteps.Contains(s.Id))], ct: Ct);

        await ExecuteAsync(
            """
            INSERT INTO teams (id, created_utc) VALUES ('old-team', '2026-09-01T00:00:00Z');
            INSERT INTO triggers
                (id, team, container, name, instruction, kind, interval_seconds, idle_only, enabled,
                 missed_count, created_at, created_by)
            VALUES
                ('t1', 'old-team', 'Manager', 'Poll', 'look', 'every', 300, 1, 1, 0,
                 '2026-09-01T00:00:00Z', 'person');
            """);
        var item = await new Harness.Backlog.SqliteBacklogStore(Database).CreateAsync(null, "Old item", "spec", "person", Ct);

        await migrator.ApplyAsync(SchemaModules.All, ct: Ct);

        var triggers = new SqliteTriggerStore(Database);
        var backlog = new Harness.Backlog.SqliteBacklogStore(Database);
        Assert.Null((await triggers.FindAsync("t1", Ct))!.OutcomeId);
        Assert.Null((await backlog.GetAsync(item.Id, Ct))!.OutcomeId);

        var outcomes = new SqliteOutcomeStore(Database, TenantAuditRow.AppendAsync);
        var outcome = (await outcomes.CreateAsync("Old work's result", new OutcomeEdit(), new OutcomeActor("person", OutcomeActorKind.Person),
            new TriggerAudit(null, "person", TenantActions.OutcomeCreated, null, null, null), Ct)).Outcome!;

        await triggers.SaveAsync((await triggers.FindAsync("t1", Ct))! with { OutcomeId = outcome.Id }, Ct);
        await backlog.SetOutcomeAsync(item.Id, outcome.Id, Ct);
        Assert.Equal(outcome.Id, (await triggers.FindAsync("t1", Ct))!.OutcomeId);
        Assert.Equal(outcome.Id, (await backlog.GetAsync(item.Id, Ct))!.OutcomeId);
    }

    /// <summary>
    /// `outcome-005` applies to a volume with ledger rows: the rows stay, and a team's read by end
    /// time walks the new index rather than the whole ledger.
    /// </summary>
    [Fact]
    public async Task The_team_ledger_index_applies_to_a_volume_with_ledger_rows()
    {
        var migrator = new SchemaMigrator(Database);
        await migrator.ApplyAsync([.. SchemaModules.All.Where(s => s.Id != "outcome-005")], ct: Ct);

        await ExecuteAsync(
            """
            INSERT INTO usage_ledger (run_seq, correlation, team_id, member, run_outcome, ended_at, measured)
            VALUES (5, 1, 'old-team', 'Dev', 'completed', '2026-09-01T00:00:00.0000000+00:00', 0);
            """);

        await migrator.ApplyAsync(SchemaModules.All, ct: Ct);

        Assert.Contains("outcome-005", await migrator.AppliedAsync(ct: Ct));
        Assert.Equal(["5"], await ColumnAsync("SELECT CAST(run_seq AS TEXT) FROM usage_ledger"));

        await using var connection = new SqliteConnection($"Data Source={Database};Pooling=False");
        await connection.OpenAsync(Ct);
        await using var explain = connection.CreateCommand();
        explain.CommandText =
            """
            EXPLAIN QUERY PLAN SELECT run_seq FROM usage_ledger
            WHERE team_id = 'old-team' AND ended_at >= '2026-01-01' AND ended_at < '2027-01-01'
            """;
        var plan = new List<string>();
        await using (var reader = await explain.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct)) plan.Add(reader.GetString(reader.GetOrdinal("detail")));
        }

        Assert.Contains(plan, step => step.Contains("ix_usage_ledger_team_ended", StringComparison.Ordinal));
    }

    /// <summary>
    /// `outcome-006` applies to an empty volume and to one with ledger rows: the rows stay, and the new
    /// table takes a hold, its one close, and nothing else.
    /// </summary>
    [Fact]
    public async Task The_admission_holds_step_applies_to_an_empty_and_an_existing_volume()
    {
        var migrator = new SchemaMigrator(Database);
        await migrator.ApplyAsync([.. SchemaModules.All.Where(s => s.Id != "outcome-006")], ct: Ct);
        Assert.DoesNotContain("admission_holds", await ColumnAsync("SELECT name FROM sqlite_master WHERE type = 'table'"));

        await ExecuteAsync(
            """
            INSERT INTO usage_ledger (run_seq, correlation, team_id, member, run_outcome, ended_at, measured)
            VALUES (5, 1, 'old-team', 'Dev', 'completed', '2026-09-01T00:00:00.0000000+00:00', 0);
            """);

        await migrator.ApplyAsync(SchemaModules.All, ct: Ct);

        Assert.Contains("outcome-006", await migrator.AppliedAsync(ct: Ct));
        Assert.Equal(["5"], await ColumnAsync("SELECT CAST(run_seq AS TEXT) FROM usage_ledger"));
        await RoundTripAsync();

        // And an empty volume, from nothing.
        SqliteConnection.ClearAllPools();
        File.Delete(Database);
        await new SchemaMigrator(Database).ApplyAsync(SchemaModules.All, ct: Ct);
        await RoundTripAsync();
    }

    private async Task RoundTripAsync()
    {
        var holds = new SqliteAdmissionHolds(Database);
        var at = new DateTimeOffset(2026, 10, 4, 9, 0, 0, TimeSpan.Zero);
        await holds.OpenAsync("old-team", "Dev", 5, at, AdmissionHoldKinds.Slot, "waiting for a slot", Ct);
        await holds.CloseAsync("old-team", "Dev", at.AddMinutes(4), Ct);

        var row = Assert.Single(await holds.ReadTeamAsync("old-team", at, at, at.AddHours(1), Ct));
        Assert.Equal(at.AddMinutes(4), row.ReleasedAt);
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync("DELETE FROM admission_holds"));
    }

    /// <summary>
    /// `outcome-004` applies to a volume with links: every link keeps its id and its fields, the next
    /// link's id follows the last, a link may now name no outcome (a person's unlink), and the table is
    /// still append-only.
    /// </summary>
    [Fact]
    public async Task Links_from_before_the_unlink_step_keep_their_ids_and_the_table_takes_an_unlink_and_stays_append_only()
    {
        var migrator = new SchemaMigrator(Database);
        await migrator.ApplyAsync([.. SchemaModules.All.Where(s => s.Id != "outcome-004")], ct: Ct);

        await ExecuteAsync(
            """
            INSERT INTO outcomes (id, name, name_key, status, created_by, created_by_kind, created_at, updated_at)
            VALUES ('o1', 'Old result', 'old result', 'active', 'person', 'person', '2026-09-01T00:00:00Z', '2026-09-01T00:00:00Z');
            INSERT INTO workflow_outcome_links
                (id, correlation, outcome_id, team_id, team_name_at_link, outcome_name_at_link, set_by, set_by_kind, set_at, how)
            VALUES
                (7, 100, 'o1', 'old-team', 'Old Team', 'Old result', 'person', 'person', '2026-09-01T00:00:00Z', 'person');
            """);

        await migrator.ApplyAsync(SchemaModules.All, ct: Ct);

        var outcomes = new SqliteOutcomeStore(Database, TenantAuditRow.AppendAsync);
        var old = Assert.Single(await outcomes.ReadLinksAsync(Ct));
        Assert.Equal((7L, 100L, "o1", "Old Team", "Old result", OutcomeLinkHow.Person),
            (old.Id, old.Correlation, old.OutcomeId, old.TeamNameAtLink, old.OutcomeNameAtLink, old.How));

        var unlink = (await outcomes.UnlinkAsync(100, "old-team", new OutcomeActor("person", OutcomeActorKind.Person),
            new TriggerAudit(null, "person", TenantActions.WorkflowOutcomeChanged, null, null, null), Ct)).Link!;
        Assert.Equal((8L, (string?)null, (string?)null), (unlink.Id, unlink.OutcomeId, unlink.OutcomeNameAtLink));
        Assert.True((await outcomes.CurrentLinkAsync(100, Ct))!.IsUnlink);

        Assert.Contains("ix_workflow_outcome_links_correlation", await ColumnAsync("SELECT name FROM sqlite_master WHERE type = 'index'"));
        foreach (var sql in new[] { "UPDATE workflow_outcome_links SET how = 'x'", "DELETE FROM workflow_outcome_links" })
        {
            var refused = await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(sql));
            Assert.Contains("append-only", refused.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A trigger made before `auth-018` has no configurer's email - its fires resolve `created_by` as
    /// before - and a save by no person keeps an email a person's save stored.
    /// </summary>
    [Fact]
    public async Task A_trigger_from_before_the_configurer_step_has_no_email_and_a_save_by_no_person_keeps_one()
    {
        var migrator = new SchemaMigrator(Database);
        await migrator.ApplyAsync([.. SchemaModules.All.Where(s => s.Id != "auth-018")], ct: Ct);

        await ExecuteAsync(
            """
            INSERT INTO teams (id, created_utc) VALUES ('old-team', '2026-09-01T00:00:00Z');
            INSERT INTO triggers
                (id, team, container, name, instruction, kind, interval_seconds, idle_only, enabled,
                 missed_count, created_at, created_by)
            VALUES
                ('t1', 'old-team', 'Manager', 'Poll', 'look', 'every', 300, 1, 1, 0,
                 '2026-09-01T00:00:00Z', 'person');
            """);

        await migrator.ApplyAsync(SchemaModules.All, ct: Ct);

        var store = new SqliteTriggerStore(Database);
        var old = (await store.FindAsync("t1", Ct))!;
        Assert.Null(old.ConfiguredByEmail);

        await store.SaveAsync(old with { ConfiguredByEmail = "person@example.test" }, Ct);
        await store.SaveAsync(old with { Name = "Renamed" }, Ct);
        Assert.Equal("person@example.test", (await store.FindAsync("t1", Ct))!.ConfiguredByEmail);
    }

    /// <summary>
    /// A trigger made before `auth-010` keeps `always` - today's behaviour - and no cap, so nothing
    /// changes under anyone until a person chooses otherwise. A new row round-trips both.
    /// </summary>
    [Fact]
    public async Task A_trigger_from_before_the_wake_choice_step_keeps_always_and_no_cap()
    {
        var migrator = new SchemaMigrator(Database);
        await migrator.ApplyAsync([.. SchemaModules.All.Where(s => s.Id != "auth-010")], ct: Ct);

        await ExecuteAsync(
            """
            INSERT INTO teams (id, created_utc) VALUES ('old-team', '2026-09-01T00:00:00Z');
            INSERT INTO triggers
                (id, team, container, name, instruction, kind, interval_seconds, idle_only, enabled,
                 missed_count, created_at, created_by)
            VALUES
                ('t1', 'old-team', 'Manager', 'Poll', 'look', 'every', 300, 1, 1, 0,
                 '2026-09-01T00:00:00Z', 'person');
            """);

        await migrator.ApplyAsync(SchemaModules.All, ct: Ct);

        var store = new SqliteTriggerStore(Database);
        var old = (await store.FindAsync("t1", Ct))!;

        Assert.Equal(WakeManagerPolicy.Always, old.WakeManager);
        Assert.Null(old.DailyTokenCap);

        await store.SaveAsync(old with { WakeManager = WakeManagerPolicy.Never, DailyTokenCap = 50_000 }, Ct);
        var saved = (await store.FindAsync("t1", Ct))!;

        Assert.Equal(WakeManagerPolicy.Never, saved.WakeManager);
        Assert.Equal(50_000, saved.DailyTokenCap);
    }

    /// <summary>
    /// An unfinished removal recorded before `auth-013` has nothing its Host left on record, and is
    /// judged by the reset's time alone; a later attempt records what it left.
    /// </summary>
    [Fact]
    public async Task An_unfinished_removal_from_before_the_host_left_step_has_nothing_left_on_record()
    {
        var migrator = new SchemaMigrator(Database);
        await migrator.ApplyAsync([.. SchemaModules.All.Where(s => s.Id != "auth-013")], ct: Ct);

        await ExecuteAsync(
            """
            INSERT INTO unfinished_removals (path, kind, team, member, remaining, recorded_at, attempts)
            VALUES ('/data/w', 'emptied', 'old-team', NULL, '["/data/w/a/f"]', '2026-09-01T00:00:00.0000000+00:00', 1);
            """);

        await migrator.ApplyAsync(SchemaModules.All, ct: Ct);

        var store = new SqliteUnfinishedRemovals(Database);
        var old = (await store.FindAsync("/data/w", Ct))!;
        Assert.Null(old.HostLeft);
        Assert.Equal(["/data/w/a/f"], old.Remaining);

        await store.RecordAsync(old with { HostLeft = new Dictionary<string, long> { ["/data/w/a"] = 42 } }, Ct);
        var again = (await store.FindAsync("/data/w", Ct))!;
        Assert.Equal(42, again.HostLeft!["/data/w/a"]);
        Assert.Equal(2, again.Attempts);
        Assert.Equal(old.RecordedAt, again.RecordedAt);
    }

    /// <summary>
    /// A trigger made before `auth-011` has skipped nothing today, and a save never writes the
    /// count: only a capped skip does.
    /// </summary>
    [Fact]
    public async Task A_trigger_from_before_the_capped_skip_step_has_skipped_nothing_today()
    {
        var migrator = new SchemaMigrator(Database);
        await migrator.ApplyAsync([.. SchemaModules.All.Where(s => s.Id != "auth-011")], ct: Ct);

        await ExecuteAsync(
            """
            INSERT INTO teams (id, created_utc) VALUES ('old-team', '2026-09-01T00:00:00Z');
            INSERT INTO triggers
                (id, team, container, name, instruction, kind, interval_seconds, idle_only, enabled,
                 missed_count, created_at, created_by, daily_token_cap)
            VALUES
                ('t1', 'old-team', 'Manager', 'Poll', 'look', 'event', NULL, 1, 1, 0,
                 '2026-09-01T00:00:00Z', 'person', 1000);
            """);

        await migrator.ApplyAsync(SchemaModules.All, ct: Ct);

        var store = new SqliteTriggerStore(Database);
        var old = (await store.FindAsync("t1", Ct))!;
        Assert.Null(old.CappedSkipsDay);
        Assert.Equal(0, old.CappedSkips);

        var day = new DateTimeOffset(2026, 9, 28, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(1, await store.CountCappedSkipAsync("t1", day, rearm: false, nextDueAt: null, Ct));
        Assert.Equal(2, await store.CountCappedSkipAsync("t1", day, rearm: false, nextDueAt: null, Ct));

        // A stale whole-row save does not put the count back.
        await store.SaveAsync(old with { Name = "Renamed" }, Ct);
        var saved = (await store.FindAsync("t1", Ct))!;
        Assert.Equal(2, saved.CappedSkips);
        Assert.Equal(day, saved.CappedSkipsDay);

        // The next day counts from one.
        Assert.Equal(1, await store.CountCappedSkipAsync("t1", day.AddDays(1), rearm: false, nextDueAt: null, Ct));
    }

    [Fact]
    public async Task A_chosen_concierge_agent_other_than_the_seed_is_kept()
    {
        var migrator = new SchemaMigrator(Database);
        await migrator.ApplyAsync([.. SchemaModules.All.Where(s => s.Id != "auth-003")], ct: Ct);
        await ExecuteAsync("UPDATE tenant_interactive_agent_settings SET interactive_agent = 'codex'");

        await migrator.ApplyAsync(SchemaModules.All, ct: Ct);

        Assert.Equal("codex", (await new SqliteTeamStore(Database).ConciergeSettingsAsync(ct: Ct)).Agent);
    }

    /// <summary>
    /// After `auth-003` the store takes NULL as well as a name: a blank agent on PUT /api/concierge
    /// clears the choice back to NULL, and a later choice is stored again.
    /// </summary>
    [Fact]
    public async Task The_concierge_agent_can_be_cleared_to_null_and_chosen_again()
    {
        await new SchemaMigrator(Database).ApplyAsync(SchemaModules.All, ct: Ct);
        var store = new SqliteTeamStore(Database);

        await store.SetConciergeSettingsAsync("codex", ct: Ct);
        Assert.Equal("codex", (await store.ConciergeSettingsAsync(ct: Ct)).Agent);

        await store.SetConciergeSettingsAsync(null, ct: Ct);
        Assert.Null((await store.ConciergeSettingsAsync(ct: Ct)).Agent);
    }

    [Fact]
    public async Task A_volume_from_before_the_squash_is_refused_and_left_alone()
    {
        await using (var connection = new SqliteConnection($"Data Source={Database};Pooling=False"))
        {
            await connection.OpenAsync(Ct);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE schema_migrations (id TEXT PRIMARY KEY, applied_at TEXT NOT NULL);
                INSERT INTO schema_migrations VALUES ('auth-001-users', '2026-09-01T00:00:00Z');
                """;
            await command.ExecuteNonQueryAsync(Ct);
        }

        var refused = await Assert.ThrowsAsync<SchemaFromTheFutureException>(
            () => new SchemaMigrator(Database).ApplyAsync(SchemaModules.All, ct: Ct));

        Assert.Equal(["auth-001-users"], refused.UnknownSteps);
        Assert.DoesNotContain("teams", await ColumnAsync("SELECT name FROM sqlite_master WHERE type = 'table'"));
    }

    [Fact]
    public async Task A_team_round_trips_without_the_dropped_columns()
    {
        await new SchemaMigrator(Database).ApplyAsync(SchemaModules.All, ct: Ct);
        var store = new SqliteTeamStore(Database);

        await store.SaveTeamAsync(new PersistedTeam(
            "Alpha", "Alpha team", "claude-headless", ["claude-headless"], "Say hello first.",
            Repos: ["https://example.invalid/a.git"], Env: new Dictionary<string, string> { ["K"] = "V" }), Ct);
        await store.SetBudgetAsync("Alpha", 0, ct: Ct);
        await store.SetPausedAsync("Alpha", true, ct: Ct);

        var team = Assert.Single(await store.TeamsAsync(ct: Ct));

        Assert.Equal("Alpha team", team.Name);
        Assert.Equal(["claude-headless"], team.MemberAgents);
        Assert.Equal("Say hello first.", team.AdditionalInstructions);
        Assert.Equal(["https://example.invalid/a.git"], team.Repos);
        Assert.Equal("V", team.Env!["K"]);
        Assert.True(team.Paused);
        Assert.Equal(0, team.BudgetTokens);
    }

    [Fact]
    public async Task A_backlog_item_is_stored_under_its_citation_and_an_id_is_never_reused()
    {
        await new SchemaMigrator(Database).ApplyAsync(SchemaModules.All, ct: Ct);
        var store = new SqliteBacklogStore(Database);

        var first = await store.CreateAsync(null, "one", "", "a@example.invalid", ct: Ct);
        var second = await store.CreateAsync("Alpha", "two", "", "a@example.invalid", ct: Ct);
        await store.AddDispatchAsync(second.Id, "Alpha", "Alpha", 7, "a@example.invalid", ct: Ct);

        Assert.Equal([1L, 2L], new[] { first.Id, second.Id });
        Assert.Equal([PlatformBacklogId.Format(1), PlatformBacklogId.Format(2)], await ColumnAsync("SELECT id FROM backlog_items ORDER BY id"));
        Assert.Equal([PlatformBacklogId.Format(2)], await ColumnAsync("SELECT item FROM backlog_dispatches"));

        Assert.Equal("two", (await store.GetAsync(2, ct: Ct))!.Title);
        Assert.Equal(2, Assert.Single(await store.DispatchesAsync(2, ct: Ct)).Item);

        await store.DeleteAsync(2, ct: Ct);
        var third = await store.CreateAsync(null, "three", "", "a@example.invalid", ct: Ct);

        Assert.Equal(3, third.Id);
        Assert.Empty(await ColumnAsync("SELECT item FROM backlog_dispatches"));
        Assert.Equal([PlatformBacklogId.Format(1), PlatformBacklogId.Format(3)], await ColumnAsync("SELECT id FROM backlog_items ORDER BY id"));
    }

    private async Task<List<string>> ColumnAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={Database};Pooling=False");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct)) values.Add(reader.GetString(0));

        return values;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={Database};Pooling=False");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }
}

public sealed class TriggerValidationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("cron", "0 0 9 * * 1-5", "Europe/Oslo", null)]
    [InlineData("CRON", "0 0 9 * * 1-5", "UTC", null)]
    [InlineData("event", null, null, null)]
    [InlineData("every", null, null, 10)]
    public void A_runnable_shape_is_accepted(string kind, string? expression, string? timezone, int? interval) =>
        Assert.Null(Triggers.Validate(kind, expression, timezone, interval, null, Now));

    [Theory]
    [InlineData("1")]
    [InlineData("weekly")]
    public void An_unknown_kind_is_refused(string kind) =>
        Assert.Equal(
            "kind must be one of: cron, every, once, event, folderChange.",
            Triggers.Validate(kind, null, null, null, null, Now));

    [Fact]
    public void A_cron_the_scheduler_cannot_arm_is_refused_at_the_route()
    {
        Assert.NotNull(Triggers.Validate("cron", "not cron", "UTC", null, null, Now));
        Assert.NotNull(Triggers.Validate("cron", "0 0 9 * * 1-5", "Not/AZone", null, null, Now));
        Assert.NotNull(Triggers.Validate("every", null, null, Triggers.MinimumIntervalSeconds - 1, null, Now));
    }

    [Fact]
    public void A_past_once_is_refused_on_create_and_kept_on_update()
    {
        var past = Now.AddMinutes(-1);

        Assert.NotNull(Triggers.Validate("once", null, null, null, past, onceMustFollow: Now));
        Assert.Null(Triggers.Validate("once", null, null, null, past, onceMustFollow: null));
        Assert.NotNull(Triggers.Validate("once", null, null, null, null, onceMustFollow: null));
    }
}
