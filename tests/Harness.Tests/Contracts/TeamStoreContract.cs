using Harness.Contracts;

namespace Harness.Tests.Contracts;

/// <summary>
/// <see cref="ITeamStore"/>, as each provider must keep it. From the existing SQLite tests, same names
/// and values where they map one to one: <c>TeamMemberLookupTests</c>' lookup;
/// <c>FreshVolumeTests</c>' team round trip, member author round trip, Concierge clear, archive and
/// activity-from round trips. The rest are the store's own remarks (narrow writes, a re-save keeps
/// what it did not name, a team's repository rows) and the review's rules: the case map (a team id
/// and a member name fold case, a team's name does not), a write and its tenant row land together or
/// not at all, concurrent upserts, and long unicode text.
/// </summary>
public abstract class TeamStoreContract : IdentityContract
{
    protected async Task<ITeamStore> CreateAsync() => (await DatabaseAsync()).Teams();

    private static PersistedMember Member(string team, string name, string agent = "claude", long floor = 0) =>
        new(team, name, null, agent, null, [], [], floor, null);

    [Fact]
    public async Task A_member_is_read_by_team_and_name_in_any_case_and_a_missing_one_is_null()
    {
        var store = await CreateAsync();
        await store.SaveTeamAsync(new PersistedTeam("Alpha", "Alpha"), Ct);
        await store.SaveTeamAsync(new PersistedTeam("Beta", "Beta"), Ct);
        await store.SaveMemberAsync(new PersistedMember("Alpha", "Developer", null, "claude", "be careful", ["x"], ["Read"], 7, "developer"), Ct);
        await store.SaveMemberAsync(new PersistedMember("Alpha", "Tester", null, "claude", null, [], [], 0, null), Ct);
        await store.SaveMemberAsync(new PersistedMember("Beta", "Developer", null, "codex", "other", [], [], 0, null), Ct);

        var found = await store.MemberAsync("alpha", "DEVELOPER", Ct);

        Assert.NotNull(found);
        Assert.Equal("Alpha", found.Team);
        Assert.Equal("Developer", found.Name);
        Assert.Equal("be careful", found.SystemPrompt);
        Assert.Equal(7, found.FloorSeq);
        Assert.Equal(["x"], found.Subscribes);
        Assert.Null(await store.MemberAsync("Alpha", "Designer", Ct));
        Assert.Null(await store.MemberAsync("Gamma", "Developer", Ct));
    }

    [Fact]
    public async Task A_team_row_and_what_is_written_with_it_commit_together_or_not_at_all()
    {
        var store = await CreateAsync();

        // The caller's write runs inside the row's own transaction: it sees the row before any commit.
        long seen = -1;
        await store.SaveTeamAsync(new PersistedTeam("Alpha", null), async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT COUNT(*) FROM teams WHERE id = 'Alpha'";
            seen = Convert.ToInt64(await command.ExecuteScalarAsync(token));
        }, Ct);
        Assert.Equal(1, seen);
        Assert.Contains(await store.TeamsAsync(Ct), t => t.Id == "Alpha");

        // One that throws leaves no row: it was never committed, so nothing had to be taken back out.
        var refused = new InvalidOperationException("refused by the test");
        Assert.Same(refused, await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SaveTeamAsync(new PersistedTeam("Beta", null), (_, _, _) => throw refused, Ct)));
        Assert.DoesNotContain(await store.TeamsAsync(Ct), t => t.Id == "Beta");
    }

    [Fact]
    public async Task A_team_round_trips_without_the_dropped_columns()
    {
        var store = await CreateAsync();

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

    /// <summary>
    /// The budget, the pause and the creation time are written by nothing but their own setters, so a
    /// re-save of the whole record leaves them; a team saved with nothing reads back as nothing chosen.
    /// </summary>
    [Fact]
    public async Task A_re_save_keeps_the_budget_the_pause_and_the_creation_time()
    {
        var store = await CreateAsync();
        await store.SaveTeamAsync(new PersistedTeam("Alpha", null), Ct);

        var fresh = Assert.Single(await store.TeamsAsync(Ct));
        Assert.Equal((null, false, null, (string?)null, (IReadOnlyList<string>?)null), (fresh.BudgetTokens, fresh.Paused, fresh.MemberAgent, fresh.AdditionalInstructions, fresh.MemberAgents));
        Assert.Empty(fresh.Repos!);
        Assert.Empty(fresh.Env!);
        var created = await store.CreatedAtAsync("Alpha", Ct);
        Assert.NotNull(created);
        Assert.Null(await store.CreatedAtAsync("Gamma", Ct));

        await store.SetBudgetAsync("Alpha", 5000, ct: Ct);
        await store.SetPausedAsync("Alpha", true, ct: Ct);
        await Task.Delay(20, Ct);
        await store.SaveTeamAsync(new PersistedTeam("Alpha", "Renamed", MemberAgents: ["codex", "claude"]), Ct);

        var saved = Assert.Single(await store.TeamsAsync(Ct));
        Assert.Equal(("Renamed", 5000L, true, "codex"), (saved.Name, saved.BudgetTokens, saved.Paused, saved.MemberAgent));
        Assert.Equal(created, await store.CreatedAtAsync("alpha", Ct));

        await store.SetBudgetAsync("Alpha", null, ct: Ct);
        Assert.Null(Assert.Single(await store.TeamsAsync(Ct)).BudgetTokens);
    }

    /// <summary>Each narrow setter writes its own columns and none other, whatever case the team is asked in.</summary>
    [Fact]
    public async Task The_narrow_setters_write_their_own_columns()
    {
        var store = await CreateAsync();
        await store.SaveTeamAsync(new PersistedTeam("Alpha", "Alpha", "claude", ["claude"], "Keep it short.", Repos: ["r1"], Env: new Dictionary<string, string> { ["A"] = "1" }), Ct);

        await store.SetNameAsync("ALPHA", "New name", Ct);
        await store.SetReposAsync("alpha", ["r2", "r3"], ct: Ct);
        await store.SetEnvAsync("alpha", new Dictionary<string, string> { ["B"] = "2" }, ct: Ct);
        await store.SetMemberAgentsAsync("alpha", ["codex"], Audit(TenantActions.TeamMemberAgentChanged, "Alpha"), Ct);
        await store.SetAdditionalInstructionsAsync("alpha", "  Be brief.  ", ct: Ct);

        var team = Assert.Single(await store.TeamsAsync(Ct));
        Assert.Equal(("Alpha", "New name", "codex", "Be brief."), (team.Id, team.Name, team.MemberAgent, team.AdditionalInstructions));
        Assert.Equal(["codex"], team.MemberAgents);
        Assert.Equal(["r2", "r3"], team.Repos);
        Assert.Equal(new Dictionary<string, string> { ["B"] = "2" }, team.Env);

        await store.SetAdditionalInstructionsAsync("Alpha", "   ", ct: Ct);
        await store.SetNameAsync("Alpha", null, Ct);
        await store.SetMemberAgentsAsync("Alpha", [], Audit(TenantActions.TeamMemberAgentChanged, "Alpha"), Ct);
        var cleared = Assert.Single(await store.TeamsAsync(Ct));
        Assert.Equal(((string?)null, (string?)null, (string?)null), (cleared.AdditionalInstructions, cleared.Name, cleared.MemberAgent));
        Assert.Empty(cleared.MemberAgents!);
    }

    /// <summary><c>FreshVolumeTests.The_instructions_author_step_applies_to_an_empty_volume_and_round_trips</c>, same values.</summary>
    [Fact]
    public async Task A_members_instructions_author_round_trips_and_nobody_reads_back_as_nobody()
    {
        var store = await CreateAsync();
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

    /// <summary>A member saved again under any case is the same member: the stored spelling stays, the rest is replaced.</summary>
    [Fact]
    public async Task A_member_re_saved_in_another_case_is_one_member_and_its_floor_moves_alone()
    {
        var store = await CreateAsync();
        await store.SaveTeamAsync(new PersistedTeam("Alpha", null), Ct);
        await store.SaveMemberAsync(new PersistedMember("Alpha", "Dev", "Developer", "claude", "first", ["a"], ["Read"], 3, "dev"), Ct);

        await store.SaveMemberAsync(new PersistedMember("ALPHA", "dev", null, "codex", "second", ["b"], ["Tell"], 4, null), Ct);
        await store.SetFloorAsync("alpha", "DEV", 99, Ct);

        var member = Assert.Single(await store.MembersAsync(Ct));
        Assert.Equal(("Alpha", "Dev", (string?)null, "codex", "second", 99L, (string?)null),
            (member.Team, member.Name, member.Label, member.Agent, member.SystemPrompt, member.FloorSeq, member.HiredFor));
        Assert.Equal(["b"], member.Subscribes);
        Assert.Equal(["Tell"], member.Permits);
    }

    /// <summary>
    /// THE CASE MAP OF A TEAM: its id folds case (SQLite's <c>COLLATE NOCASE</c> key), so a save under
    /// another spelling is the same team and keeps the stored one.
    /// </summary>
    [Fact]
    public async Task Team_ids_differing_only_in_case_are_one_team()
    {
        var store = await CreateAsync();
        await store.SaveTeamAsync(new PersistedTeam("Alpha", "First"), Ct);

        await store.SaveTeamAsync(new PersistedTeam("alpha", "Second"), Ct);

        var team = Assert.Single(await store.TeamsAsync(Ct));
        Assert.Equal(("Alpha", "Second"), (team.Id, team.Name));
    }

    /// <summary>
    /// THE CASE MAP OF A TEAM: its name is compared exactly (SQLite's BINARY column), so two teams named
    /// <c>Alpha</c> and <c>alpha</c> are two names, each read back as typed, and a lookup by name sees one.
    /// </summary>
    [Fact]
    public async Task Team_names_differing_only_in_case_are_two_names()
    {
        var database = await DatabaseAsync();
        var store = database.Teams();
        await store.SaveTeamAsync(new PersistedTeam("t1", "Alpha"), Ct);
        await store.SaveTeamAsync(new PersistedTeam("t2", "alpha"), Ct);

        Assert.Equal(["Alpha", "alpha"], (await store.TeamsAsync(Ct)).OrderBy(t => t.Id, StringComparer.Ordinal).Select(t => t.Name));
        Assert.Equal(1, await database.LongAsync("SELECT COUNT(*) FROM teams WHERE name = 'alpha'"));
        Assert.Equal("t2", await database.TextAsync("SELECT id FROM teams WHERE name = 'alpha'"));
    }

    /// <summary>A deletion takes the team's members and repository rows with it, whatever case it is asked in, and nothing of another team.</summary>
    [Fact]
    public async Task Deleting_a_team_takes_its_members_and_repository_rows_and_no_other_teams()
    {
        var database = await DatabaseAsync();
        var store = database.Teams();
        foreach (var id in new[] { "Alpha", "Beta" })
        {
            await store.SaveTeamAsync(new PersistedTeam(id, id), Ct);
            await store.SaveMemberAsync(Member(id, "Dev"), Ct);
            await store.SetRemoteDefaultBranchAsync(id, "app", "main", Ct);
            await store.SetRepoContributorAsync(new RepoContributor(id, "app", "https://example.invalid/up.git", "me", true, null), Ct);
        }

        await store.DeleteTeamAsync("alpha", Ct);

        Assert.Equal(["Beta"], (await store.TeamsAsync(Ct)).Select(t => t.Id));
        Assert.Equal(["Beta"], (await store.MembersAsync(Ct)).Select(m => m.Team));
        Assert.Equal(["Beta"], (await store.RepoDefaultBranchesAsync(Ct)).Select(b => b.Team));
        Assert.Equal(["Beta"], (await store.RepoContributorsAsync(Ct)).Select(c => c.Team));
    }

    [Fact]
    public async Task Deleting_a_member_removes_that_member_whatever_the_case_and_no_other()
    {
        var store = await CreateAsync();
        await store.SaveTeamAsync(new PersistedTeam("Alpha", null), Ct);
        await store.SaveMemberAsync(Member("Alpha", "Developer"), Ct);
        await store.SaveMemberAsync(Member("Alpha", "Tester"), Ct);

        await store.DeleteMemberAsync("alpha", "developer", Ct);

        Assert.Equal(["Tester"], (await store.MembersAsync(Ct)).Select(m => m.Name));
    }

    [Fact]
    public async Task A_fresh_volume_has_no_concierge_agent_and_one_can_be_chosen_cleared_and_chosen_again()
    {
        var store = await CreateAsync();
        Assert.Null((await store.ConciergeSettingsAsync(Ct)).Agent);

        await store.SetConciergeSettingsAsync("codex", ct: Ct);
        Assert.Equal("codex", (await store.ConciergeSettingsAsync(ct: Ct)).Agent);

        await store.SetConciergeSettingsAsync(null, ct: Ct);
        Assert.Null((await store.ConciergeSettingsAsync(ct: Ct)).Agent);

        await store.SetConciergeSettingsAsync("claude", Audit(TenantActions.ConciergeChanged, "concierge"), Ct);
        Assert.Equal("claude", (await store.ConciergeSettingsAsync(ct: Ct)).Agent);
    }

    /// <summary><c>FreshVolumeTests.ArchiveRoundTripAsync</c>, same values.</summary>
    [Fact]
    public async Task A_team_is_archived_and_unarchived_each_with_its_tenant_row()
    {
        var database = await DatabaseAsync();
        var store = database.Teams();
        const string team = "fresh-team";
        await store.SaveTeamAsync(new PersistedTeam(team, null), Ct);
        var at = new DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);

        await store.SetArchivedAsync(team, at, "person@example.test", new TriggerAudit(null, "person@example.test", TenantActions.TeamArchived, team, team, null), Ct);
        var archived = Assert.Single(await store.TeamsAsync(Ct), t => t.Id == team);
        Assert.Equal((at, "person@example.test", true), (archived.ArchivedAt, archived.ArchivedBy, archived.Paused));

        await store.SetArchivedAsync(team, null, null, new TriggerAudit(null, "person@example.test", TenantActions.TeamUnarchived, team, team, null), Ct);
        var unarchived = Assert.Single(await store.TeamsAsync(Ct), t => t.Id == team);
        Assert.Equal((null, null, true), (unarchived.ArchivedAt, unarchived.ArchivedBy, unarchived.Paused));
        Assert.Equal(["team.archived", "team.unarchived"], await database.ColumnAsync($"SELECT action FROM tenant_events WHERE subject = '{team}' ORDER BY seq"));
    }

    /// <summary><c>FreshVolumeTests.ActivityFromRoundTripAsync</c>, same values.</summary>
    [Fact]
    public async Task A_teams_activity_from_round_trips_a_re_save_keeps_it_and_a_clear_clears_both()
    {
        var database = await DatabaseAsync();
        var store = database.Teams();
        const string team = "fresh-team";
        await store.SaveTeamAsync(new PersistedTeam(team, null), Ct);
        Assert.Null(await store.ActivityFromAsync(team, Ct));
        var set = new ActivityFrom(new DateTimeOffset(2026, 10, 11, 9, 0, 0, TimeSpan.Zero), "person@example.test");

        await store.SetActivityFromAsync(team, set, new TriggerAudit(null, "person@example.test", TenantActions.TeamActivityFromSet, team, team, null), Ct);
        Assert.Equal(set, await store.ActivityFromAsync(team, Ct));
        Assert.Equal(set, await store.ActivityFromAsync("FRESH-TEAM", Ct));

        await store.SaveTeamAsync(new PersistedTeam(team, null), Ct);
        Assert.Equal(set, await store.ActivityFromAsync(team, Ct));

        await store.SetActivityFromAsync(team, null, new TriggerAudit(null, "person@example.test", TenantActions.TeamActivityFromCleared, team, team, null), Ct);
        Assert.Null(await store.ActivityFromAsync(team, Ct));
        Assert.Equal(["team.activity-from-set", "team.activity-from-cleared"], await database.ColumnAsync($"SELECT action FROM tenant_events WHERE subject = '{team}' ORDER BY seq"));
    }

    /// <summary>
    /// A PERSON'S BRANCH AND THE REMOTE'S ARE TWO COLUMNS, each written alone: a Fetch never moves a
    /// person's choice, and clearing it falls back to what the remote named. The repository folds case.
    /// </summary>
    [Fact]
    public async Task A_persons_branch_survives_a_fetch_and_a_clear_falls_back_to_the_remotes()
    {
        var database = await DatabaseAsync();
        var store = database.Teams();
        await store.SaveTeamAsync(new PersistedTeam("Alpha", null), Ct);
        Assert.Empty(await store.RepoDefaultBranchesAsync(Ct));

        await store.SetRemoteDefaultBranchAsync("Alpha", "App", "main", Ct);
        await store.SetPersonDefaultBranchAsync("alpha", "app", "release", Audit(TenantActions.RepoDefaultBranchSet, "Alpha"), Ct);
        await store.SetRemoteDefaultBranchAsync("Alpha", "APP", "trunk", Ct);

        var row = Assert.Single(await store.RepoDefaultBranchesAsync(Ct));
        Assert.Equal(new RepoDefaultBranch("Alpha", "App", "trunk", "release"), row);
        Assert.Equal("release", row.Branch);

        await store.SetPersonDefaultBranchAsync("Alpha", "App", null, ct: Ct);
        Assert.Equal("trunk", Assert.Single(await store.RepoDefaultBranchesAsync(Ct)).Branch);
        Assert.Equal(1, await database.LongAsync($"SELECT COUNT(*) FROM tenant_events WHERE action = '{TenantActions.RepoDefaultBranchSet}'"));
    }

    /// <summary>The settings and the recorded pull request are written apart, so neither clears the other.</summary>
    [Fact]
    public async Task A_contributor_setting_and_a_pull_request_are_written_apart()
    {
        var store = await CreateAsync();
        await store.SaveTeamAsync(new PersistedTeam("Alpha", null), Ct);
        var readAt = new DateTimeOffset(2026, 10, 9, 8, 7, 6, TimeSpan.Zero);

        await store.SetRepoPullRequestAsync("Alpha", "app", new RepoPullRequest("https://example.invalid/pr/7", 7, "open", readAt), Ct);
        Assert.Equal(
            new RepoContributor("Alpha", "app", null, null, false, null, new RepoPullRequest("https://example.invalid/pr/7", 7, "open", readAt)),
            Assert.Single(await store.RepoContributorsAsync(Ct)));

        await store.SetRepoContributorAsync(new RepoContributor("alpha", "APP", "https://example.invalid/up.git", "me", true, "signed in person"), Ct);
        var both = Assert.Single(await store.RepoContributorsAsync(Ct));
        Assert.Equal(("https://example.invalid/up.git", "me", true, "signed in person", 7), (both.UpstreamUrl, both.ForkOwner, both.DcoSignOff, both.ClaSignedNote, both.PullRequest!.Number));
        Assert.True(both.ContributorMode);

        await store.SetRepoPullRequestAsync("Alpha", "app", null, Ct);
        var cleared = Assert.Single(await store.RepoContributorsAsync(Ct));
        Assert.Null(cleared.PullRequest);
        Assert.Equal("https://example.invalid/up.git", cleared.UpstreamUrl);
    }

    /// <summary>
    /// A TEAM WRITE AND ITS TENANT ROW ARE ONE TRANSACTION: with the tenant log unwritable, each write
    /// that carries a tenant row throws and changes nothing.
    /// </summary>
    [Fact]
    public async Task A_write_is_not_saved_when_its_tenant_row_cannot_be()
    {
        var database = await DatabaseAsync();
        var store = database.Teams();
        await store.SaveTeamAsync(new PersistedTeam("Alpha", "Alpha"), Ct);
        await store.SaveMemberAsync(Member("Alpha", "Dev", floor: 1), Ct);
        await store.SetRemoteDefaultBranchAsync("Alpha", "app", "main", Ct);
        await database.ExecuteAsync("DROP TABLE tenant_events");

        await Assert.ThrowsAnyAsync<Exception>(() => store.SetPausedAsync("Alpha", true, Audit(TenantActions.TeamPaused, "Alpha"), Ct));
        await Assert.ThrowsAnyAsync<Exception>(() => store.SetBudgetAsync("Alpha", 10, Audit(TenantActions.TeamBudgetChanged, "Alpha"), Ct));
        await Assert.ThrowsAnyAsync<Exception>(() => store.SetArchivedAsync("Alpha", DateTimeOffset.UnixEpoch, "p", Audit(TenantActions.TeamArchived, "Alpha"), Ct));
        await Assert.ThrowsAnyAsync<Exception>(() => store.SetEnvAsync("Alpha", new Dictionary<string, string> { ["K"] = "V" }, Audit(TenantActions.TeamEnvChanged, "Alpha"), Ct));
        await Assert.ThrowsAnyAsync<Exception>(() => store.SaveMemberAsync(Member("Alpha", "Dev", "codex", 2), [Audit(TenantActions.MemberChanged, "Alpha/Dev")], Ct));
        await Assert.ThrowsAnyAsync<Exception>(() => store.SetPersonDefaultBranchAsync("Alpha", "app", "release", Audit(TenantActions.RepoDefaultBranchSet, "Alpha"), Ct));
        await Assert.ThrowsAnyAsync<Exception>(() => store.SetConciergeSettingsAsync("codex", Audit(TenantActions.ConciergeChanged, "concierge"), Ct));

        var team = Assert.Single(await store.TeamsAsync(Ct));
        Assert.Equal((false, (long?)null, (DateTimeOffset?)null), (team.Paused, team.BudgetTokens, team.ArchivedAt));
        Assert.Empty(team.Env!);
        Assert.Equal(("claude", 1L), ((await store.MemberAsync("Alpha", "Dev", Ct))!.Agent, (await store.MemberAsync("Alpha", "Dev", Ct))!.FloorSeq));
        Assert.Null(Assert.Single(await store.RepoDefaultBranchesAsync(Ct)).SetByPerson);
        Assert.Null((await store.ConciergeSettingsAsync(Ct)).Agent);
    }

    [Fact]
    public async Task A_member_saved_with_its_tenant_rows_lands_with_every_one()
    {
        var database = await DatabaseAsync();
        var store = database.Teams();
        await store.SaveTeamAsync(new PersistedTeam("Alpha", null), Ct);

        await store.SaveMemberAsync(Member("Alpha", "Dev"), [Audit(TenantActions.MemberAdded, "Alpha/Dev"), Audit(TenantActions.MemberInstructionsChanged, "Alpha/Dev")], Ct);

        Assert.NotNull(await store.MemberAsync("Alpha", "Dev", Ct));
        Assert.Equal(["member.added", "member.instructions-changed"], await database.ColumnAsync("SELECT action FROM tenant_events WHERE subject = 'Alpha/Dev' ORDER BY seq"));
    }

    /// <summary>REVIEW FOCUS 4: a team's name, instructions and a member's prompt come back byte for byte.</summary>
    [Fact]
    public async Task Long_and_unicode_text_round_trips()
    {
        var store = await CreateAsync();
        var name = LongUnicode("Zoë's team");
        var prompt = LongUnicode("Write it all down");

        await store.SaveTeamAsync(new PersistedTeam("Alpha", name, AdditionalInstructions: prompt), Ct);
        await store.SaveMemberAsync(new PersistedMember("Alpha", "Dev", "Développeur 🚀", "claude", prompt, [], [], 0), Ct);

        var team = Assert.Single(await store.TeamsAsync(Ct));
        Assert.Equal((name, prompt), (team.Name, team.AdditionalInstructions));
        var member = (await store.MemberAsync("Alpha", "Dev", Ct))!;
        Assert.Equal(("Développeur 🚀", prompt), (member.Label, member.SystemPrompt));
    }

    /// <summary>
    /// REVIEW FOCUS 2: eight saves of one new team at once, in two cases. Every save succeeds and one
    /// team stands, under one of the spellings it was saved with.
    /// </summary>
    [Fact]
    public async Task Concurrent_saves_of_one_new_team_in_two_cases_make_one_team()
    {
        var store = await CreateAsync();

        for (var round = 0; round < 12; round++)
        {
            var id = $"Raced{round}";
            await RaceAsync(8, async i =>
            {
                await store.SaveTeamAsync(new PersistedTeam(i % 2 == 0 ? id : id.ToLowerInvariant(), $"name {i}"), Ct);
                return i;
            });

            Assert.Single(await store.TeamsAsync(Ct), t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>REVIEW FOCUS 2: eight hires of one new member at once. Every save succeeds and one row stands.</summary>
    [Fact]
    public async Task Concurrent_saves_of_one_new_member_make_one_row()
    {
        var store = await CreateAsync();
        await store.SaveTeamAsync(new PersistedTeam("Alpha", null), Ct);

        for (var round = 0; round < 12; round++)
        {
            var name = $"Raced{round}";
            await RaceAsync(8, async i =>
            {
                await store.SaveMemberAsync(Member("Alpha", i % 2 == 0 ? name : name.ToUpperInvariant(), floor: i), [Audit(TenantActions.MemberAdded, $"Alpha/{name}")], Ct);
                return i;
            });

            Assert.Single(await store.MembersAsync(Ct), m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// REVIEW FOCUS 2: a Fetch records the remote's branch while a person sets their own, on a repository
    /// with no row yet. Both land in the one row; neither is lost to the other's insert.
    /// </summary>
    [Fact]
    public async Task Concurrent_writes_of_a_new_repositorys_two_branches_keep_both()
    {
        var store = await CreateAsync();
        await store.SaveTeamAsync(new PersistedTeam("Alpha", null), Ct);

        for (var round = 0; round < 10; round++)
        {
            var repo = $"repo{round}";
            await RaceAsync(8, async i =>
            {
                if (i % 2 == 0) await store.SetRemoteDefaultBranchAsync("Alpha", repo, "main", Ct);
                else await store.SetPersonDefaultBranchAsync("Alpha", repo, "release", ct: Ct);
                return i;
            });

            Assert.Equal(new RepoDefaultBranch("Alpha", repo, "main", "release"), Assert.Single(await store.RepoDefaultBranchesAsync(Ct), b => b.Repo == repo));
        }
    }

    /// <summary>
    /// REVIEW FOCUS 2: A TEAM IS DELETED WHILE ITS MEMBERS AND REPOSITORY ROWS ARE WRITTEN. Each write
    /// either lands before the deletion (and goes with the team) or is refused by the foreign key after
    /// it; none fails any other way, and no member or repository row outlives its team. The deletion
    /// locks the team and then its children; each child write locks the team first too, so the two
    /// cannot hold what the other waits for.
    /// </summary>
    [Fact]
    public async Task A_team_deleted_while_its_members_and_branches_are_written_leaves_no_child_and_no_deadlock()
    {
        var store = await CreateAsync();

        for (var round = 0; round < 12; round++)
        {
            var team = $"Doomed{round}";
            await store.SaveTeamAsync(new PersistedTeam(team, null), Ct);
            await store.SaveMemberAsync(Member(team, "Dev"), Ct);
            await store.SetRemoteDefaultBranchAsync(team, "app", "main", Ct);

            var outcomes = await RaceAsync(8, async i =>
            {
                try
                {
                    switch (i % 4)
                    {
                        case 0 when i == 0: await store.DeleteTeamAsync(team, Ct); break;
                        case 0: await store.SetRepoContributorAsync(new RepoContributor(team, $"r{i}", null, null, false, null), Ct); break;
                        case 1: await store.SaveMemberAsync(Member(team, $"M{i}", floor: i), [Audit(TenantActions.MemberAdded, $"{team}/M{i}")], Ct); break;
                        case 2: await store.SetRemoteDefaultBranchAsync(team, "app", $"b{i}", Ct); break;
                        default: await store.SetPersonDefaultBranchAsync(team, $"r{i}", "release", ct: Ct); break;
                    }

                    return null;
                }
                catch (System.Data.Common.DbException refused)
                {
                    return refused.Message;
                }
            });

            Assert.All(outcomes, refusal => Assert.True(refusal is null || refusal.Contains("FOREIGN KEY", StringComparison.Ordinal), refusal));
            Assert.DoesNotContain(await store.TeamsAsync(Ct), t => t.Id == team);
            Assert.DoesNotContain(await store.MembersAsync(Ct), m => m.Team == team);
            Assert.DoesNotContain(await store.RepoDefaultBranchesAsync(Ct), b => b.Team == team);
            Assert.DoesNotContain(await store.RepoContributorsAsync(Ct), c => c.Team == team);
        }
    }
}

public sealed class SqliteTeamStoreTests : TeamStoreContract
{
    protected override Task<IdentityDatabase> OpenAsync() => Task.FromResult<IdentityDatabase>(new SqliteIdentityDatabase());
}

public sealed class SqlServerTeamStoreTests : TeamStoreContract
{
    protected override async Task<IdentityDatabase> OpenAsync() => await SqlServerIdentityDatabase.CreateAsync();
}
