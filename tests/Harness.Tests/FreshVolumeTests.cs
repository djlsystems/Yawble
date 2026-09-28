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
    /// token cap.
    /// </summary>
    [Fact]
    public void The_steps_are_the_squash_and_the_steps_added_after_it()
    {
        Assert.Equal(
            ["messages-001", "auth-001", "auth-002", "auth-003", "auth-004", "auth-005", "auth-006", "auth-007", "auth-008", "auth-009", "auth-010", "skill-001", "skill-002", "skill-003", "backlog-001"],
            SchemaModules.All.Select(s => s.Id));
    }

    [Fact]
    public async Task An_empty_volume_migrates_to_only_the_live_tables()
    {
        var migrator = new SchemaMigrator(Database);

        await migrator.ApplyAsync(SchemaModules.All, ct: Ct);

        Assert.Equal(
            ["auth-001", "auth-002", "auth-003", "auth-004", "auth-005", "auth-006", "auth-007", "auth-008", "auth-009", "auth-010", "backlog-001", "messages-001", "skill-001", "skill-002", "skill-003"],
            await migrator.AppliedAsync(ct: Ct));

        // Nothing pending on the second start, so no backup and no change.
        Assert.Null(await migrator.ApplyAsync(SchemaModules.All, ct: Ct));

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

        await store.SetConciergeSettingsAsync("codex", Ct);
        Assert.Equal("codex", (await store.ConciergeSettingsAsync(ct: Ct)).Agent);

        await store.SetConciergeSettingsAsync(null, Ct);
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
        Assert.Equal(["B0001", "B0002"], await ColumnAsync("SELECT id FROM backlog_items ORDER BY id"));
        Assert.Equal(["B0002"], await ColumnAsync("SELECT item FROM backlog_dispatches"));

        Assert.Equal("two", (await store.GetAsync(2, ct: Ct))!.Title);
        Assert.Equal(2, Assert.Single(await store.DispatchesAsync(2, ct: Ct)).Item);

        await store.DeleteAsync(2, ct: Ct);
        var third = await store.CreateAsync(null, "three", "", "a@example.invalid", ct: Ct);

        Assert.Equal(3, third.Id);
        Assert.Empty(await ColumnAsync("SELECT item FROM backlog_dispatches"));
        Assert.Equal(["B0001", "B0003"], await ColumnAsync("SELECT id FROM backlog_items ORDER BY id"));
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
