using System.Data.Common;
using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests.Contracts;

/// <summary>
/// WHAT EVERY <see cref="ISkillStore"/> DOES, on SQLite and on SQL Server alike. Taken from
/// <c>TeamSkillStoreTests</c>, <c>PluginSkillsTests</c> and <c>SkillMigrationTests</c> (same names, same
/// values: a team skill's write with its tenant row or not at all, a built-in winning its name, plugin
/// skills replaced per source and locked, the first-start migration, the role a listing honours) and from
/// the interface's and the SQLite store's remarks: the two name scopes and every refusal between them,
/// the free name a displaced skill moves to, paging, search. Plus the providers' differences made into
/// rules: a name's case and its trailing space, search's case and its special characters, and two
/// writers at once.
/// </summary>
public abstract class SkillStoreContract : IdentityContract
{
    protected async Task<ISkillStore> CreateAsync() => (await DatabaseAsync()).Skills();

    private static TriggerAudit Row(string action, string name) => new(null, null, action, "alpha", name, null);

    private static SkillDraft Draft(string name, string body = "Body.") => new(name, "Use it.", ["manager"], body);

    private static SkillDraft Member(string name, string body = "How to use it.") =>
        new(name, "Use it for things.", ["manager", "member"], body);

    private async Task<IReadOnlyList<string?>> ActionsAsync() =>
        await (await DatabaseAsync()).ColumnAsync("SELECT action FROM tenant_events ORDER BY seq");

    [Fact]
    public async Task A_team_skill_write_whose_tenant_row_cannot_be_written_does_not_happen()
    {
        var store = await CreateAsync();
        await store.PutTeamSkillAsync("alpha", Draft("kept"), replace: false, null, Row(TenantActions.SkillCreated, "kept"), Ct);

        await (await DatabaseAsync()).ExecuteAsync("DROP TABLE tenant_events");

        await Assert.ThrowsAnyAsync<DbException>(() =>
            store.PutTeamSkillAsync("alpha", Draft("lost"), replace: false, null, Row(TenantActions.SkillCreated, "lost"), Ct));
        await Assert.ThrowsAnyAsync<DbException>(() =>
            store.UpdateTeamSkillAsync("alpha", "kept", Draft("kept", "Changed."), null, Row(TenantActions.SkillChanged, "kept"), Ct));
        await Assert.ThrowsAnyAsync<DbException>(() =>
            store.DeleteTeamSkillAsync("alpha", "kept", Row(TenantActions.SkillDeleted, "kept"), Ct));
        await Assert.ThrowsAnyAsync<DbException>(() =>
            store.DeleteTeamSkillsAsync("alpha", name => Row(TenantActions.SkillDeleted, name), Ct));

        var left = Assert.Single(await store.ListTeamAsync("alpha", Ct));
        Assert.Equal("kept", left.Name);
        Assert.Equal("Body.", left.Body);
    }

    [Fact]
    public async Task A_built_in_shipped_later_wins_its_name_from_a_team_skill_which_is_kept_under_another()
    {
        var store = await CreateAsync();
        await store.PutTeamSkillAsync("alpha", Draft("later-builtin", "Mine."), replace: false, null, Row(TenantActions.SkillCreated, "later-builtin"), Ct);

        var moved = await store.ReplaceBuiltInsAsync([Draft("later-builtin", "The build's.")], DateTimeOffset.UtcNow, Ct);

        Assert.Contains(moved, m => m.Contains("later-builtin -> later-builtin-custom", StringComparison.Ordinal));
        Assert.Equal("The build's.", (await store.GetAsync("later-builtin", Ct))!.Body);
        Assert.Equal("Mine.", Assert.Single(await store.ListTeamAsync("alpha", Ct)).Body);
    }

    [Fact]
    public async Task What_a_role_is_offered_instance_wide_leaves_out_every_team_skill()
    {
        var store = await CreateAsync();
        await store.PutTeamSkillAsync("alpha", Draft("alpha-only", "zanzibar"), replace: false, null, Row(TenantActions.SkillCreated, "alpha-only"), Ct);

        Assert.Empty(await store.ListAsync(SkillKindFilter.All, "zanzibar", SkillRoles.Manager, null, 50, Ct));
        Assert.Single(await store.ListAsync(SkillKindFilter.All, "zanzibar", null, null, 50, Ct));
        Assert.Single(await store.ListOfferedAsync(SkillRoles.Manager, "ALPHA", "zanzibar", 50, Ct));
        Assert.Empty(await store.ListOfferedAsync(SkillRoles.Manager, "beta", "zanzibar", 50, Ct));
        Assert.Empty(await store.ListOfferedAsync(SkillRoles.Manager, null, "zanzibar", 50, Ct));
        Assert.Null(await store.GetAsync("alpha-only", Ct));
    }

    [Fact]
    public async Task Plugin_skills_are_replaced_per_source_and_locked()
    {
        var store = await CreateAsync();
        var at = DateTimeOffset.UtcNow;

        await store.ReplacePluginSkillsAsync("alpha", [Member("plugin-alpha"), Member("plugin-alpha-extra")], at, Ct);
        await store.ReplacePluginSkillsAsync("beta", [Member("plugin-beta")], at, Ct);

        var alpha = (await store.GetAsync("plugin-alpha", Ct))!;
        Assert.Equal(SkillKind.Plugin, alpha.Kind);
        Assert.Equal("alpha", alpha.Source);
        Assert.True(alpha.IsLocked);

        // Replaced, per source: alpha's second skill goes, beta's stays.
        await store.ReplacePluginSkillsAsync("alpha", [Member("plugin-alpha", "new body")], at, Ct);
        Assert.Null(await store.GetAsync("plugin-alpha-extra", Ct));
        Assert.Equal("new body", (await store.GetAsync("plugin-alpha", Ct))!.Body);
        Assert.NotNull(await store.GetAsync("plugin-beta", Ct));
        Assert.Equal(["alpha", "beta"], await store.PluginSourcesAsync(Ct));

        // A plugin cannot ship a name outside its own namespace.
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReplacePluginSkillsAsync("alpha", [Member("manager")], at, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReplacePluginSkillsAsync("alpha", [Member("plugin-beta")], at, Ct));

        // Locked: not edited, renamed or deleted, and its namespace not taken by a custom skill.
        await Assert.ThrowsAsync<SkillRefusedException>(() => store.UpdateCustomAsync("plugin-alpha", Member("plugin-alpha"), "p", Ct));
        await Assert.ThrowsAsync<SkillRefusedException>(() => store.DeleteCustomAsync("plugin-alpha", Ct));
        await Assert.ThrowsAsync<SkillRefusedException>(() => store.CreateCustomAsync(Member("plugin-alpha"), "p", Ct));
        await Assert.ThrowsAsync<SkillRefusedException>(() => store.CreateCustomAsync(Member("plugin-gamma"), "p", Ct));

        var mine = await store.CreateCustomAsync(Member("mine"), "p", Ct);
        await Assert.ThrowsAsync<SkillRefusedException>(() => store.UpdateCustomAsync(mine.Name, Member("plugin-alpha-x"), "p", Ct));

        // Empty removes a source.
        await store.ReplacePluginSkillsAsync("beta", [], at, Ct);
        Assert.Null(await store.GetAsync("plugin-beta", Ct));
    }

    [Fact]
    public async Task A_plugin_name_another_skill_holds_is_not_indexed_and_a_displaced_skill_takes_the_next_free_name()
    {
        var store = await CreateAsync();
        var at = DateTimeOffset.UtcNow;

        await store.ReplacePluginSkillsAsync("a", [Member("plugin-a-b")], at, Ct);
        var said = await store.ReplacePluginSkillsAsync("a-b", [Member("plugin-a-b", "Theirs.")], at, Ct);

        Assert.Equal(["plugin 'a-b''s skill plugin-a-b was not indexed: another skill already holds that name."], said);
        Assert.Equal("a", (await store.GetAsync("plugin-a-b", Ct))!.Source);

        await store.CreateCustomAsync(Member("shipped"), "p", Ct);
        await store.CreateCustomAsync(Member("shipped-custom"), "p", Ct);
        var moved = await store.ReplaceBuiltInsAsync([Member("shipped", "Ours.")], at, Ct);

        Assert.Equal(["shipped -> shipped-custom-2"], moved);
        Assert.Equal(SkillKind.Custom, (await store.GetAsync("shipped-custom-2", Ct))!.Kind);
    }

    [Fact]
    public async Task Skill_files_on_a_volume_are_moved_to_the_backup_and_only_the_custom_ones_imported()
    {
        var store = await CreateAsync();
        await store.ReplaceBuiltInsAsync(BuiltInSkills.Drafts(), DateTimeOffset.UtcNow, Ct);
        var root = Directory.CreateTempSubdirectory("harness-skill-contract-").FullName;
        var skills = Path.Combine(root, "skills");
        var backups = Path.Combine(root, "backups", "skills-before-builtins");

        try
        {
            void Write(string name, string description, string body)
            {
                var directory = Path.Combine(skills, name);
                Directory.CreateDirectory(directory);
                File.WriteAllText(
                    Path.Combine(directory, "SKILL.md"),
                    $"---\nname: {name}\ndescription: {description}\nmetadata:\n  gated: false\n  version: \"1.0\"\n---\n\n{body}\n");
            }

            Write("manager", "Use when managing team work.", "An edited old copy.");
            Write("tenant-manager", "Use when managing the tenant.", "Stale.");
            Write("release-notes", "Use when writing release notes.", "Say what changed.");
            Write("concierge-greeting", "Use when the Concierge greets a person.", "Say hello.");

            var report = await SkillMigration.RunAsync([("skills", skills)], backups, store, Ct);

            Assert.Equal(["manager"], report.ReplacedByBuiltIn);
            Assert.Equal(["tenant-manager"], report.NotMigrated);
            Assert.Equal(["concierge-greeting", "release-notes"], report.Imported.Order(StringComparer.Ordinal));

            var manager = await store.GetAsync("manager", Ct);
            Assert.Equal(SkillKind.BuiltIn, manager!.Kind);
            Assert.DoesNotContain("An edited old copy.", manager.Body, StringComparison.Ordinal);
            Assert.Null(await store.GetAsync("tenant-manager", Ct));

            var notes = await store.GetAsync("release-notes", Ct);
            Assert.Equal(SkillKind.Custom, notes!.Kind);
            Assert.Equal([SkillRoles.Member], notes.Roles);
            Assert.Equal("Say what changed.", notes.Body.Trim());
            Assert.Equal([SkillRoles.Concierge], (await store.GetAsync("concierge-greeting", Ct))!.Roles);

            Assert.False((await SkillMigration.RunAsync([("skills", skills)], backups, store, Ct)).Moved);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public async Task Rebuilding_the_built_ins_takes_the_builds_text_and_keeps_every_custom_skill()
    {
        var store = await CreateAsync();
        await store.ReplaceBuiltInsAsync(BuiltInSkills.Drafts(), DateTimeOffset.UtcNow, Ct);
        await store.CreateCustomAsync(new SkillDraft("house-style", "Use when writing.", [SkillRoles.Member], "Short."), "person", Ct);

        var changed = BuiltInSkills.Drafts()
            .Select(d => d.Name == "member" ? d with { Body = "A newer member body from the build." } : d)
            .ToList();
        await store.ReplaceBuiltInsAsync(changed, DateTimeOffset.UtcNow, Ct);

        Assert.Equal("A newer member body from the build.", (await store.GetAsync("member", Ct))!.Body);
        Assert.NotNull(await store.GetAsync("house-style", Ct));
    }

    [Fact]
    public async Task A_custom_skill_holding_a_name_the_build_now_ships_is_moved_out_of_the_way()
    {
        var store = await CreateAsync();
        await store.ReplaceBuiltInsAsync(BuiltInSkills.Drafts(), DateTimeOffset.UtcNow, Ct);
        await store.CreateCustomAsync(new SkillDraft("shipped-later", "Use when.", [SkillRoles.Member], "Mine."), "person", Ct);

        var moved = await store.ReplaceBuiltInsAsync(
            [.. BuiltInSkills.Drafts(), new SkillDraft("shipped-later", "Built in now.", [SkillRoles.Any], "Ours.")],
            DateTimeOffset.UtcNow, Ct);

        Assert.Equal(["shipped-later -> shipped-later-custom"], moved);
        Assert.Equal(SkillKind.BuiltIn, (await store.GetAsync("shipped-later", Ct))!.Kind);
        Assert.Equal("Mine.", (await store.GetAsync("shipped-later-custom", Ct))!.Body);
    }

    [Fact]
    public async Task Search_and_listing_honour_the_role()
    {
        var store = await CreateAsync();
        await store.ReplaceBuiltInsAsync(BuiltInSkills.Drafts(), DateTimeOffset.UtcNow, Ct);

        var member = await store.ListAsync(SkillKindFilter.All, null, SkillRoles.Member, null, 200, Ct);
        Assert.Contains(member, s => s.Name == "worktrees");
        Assert.DoesNotContain(member, s => s.Name is "manager" or "wrap-up");

        // `any` is offered to every role.
        Assert.Contains(member, s => s.Name == "test-credentials");

        var found = await store.ListAsync(SkillKindFilter.All, "worktree", SkillRoles.Manager, null, 200, Ct);
        Assert.DoesNotContain(found, s => s.Name == "worktrees");
    }

    [Fact]
    public async Task A_custom_skill_is_refused_a_name_another_skill_holds()
    {
        var store = await CreateAsync();
        await store.ReplaceBuiltInsAsync([Draft("built")], DateTimeOffset.UtcNow, Ct);
        await store.CreateCustomAsync(Draft("mine"), "person", Ct);
        await store.PutTeamSkillAsync("Alpha", Draft("theirs"), replace: false, null, Row(TenantActions.SkillCreated, "theirs"), Ct);

        Assert.Equal(
            "'built' is a built-in skill's name. A custom skill needs a name of its own.",
            (await Assert.ThrowsAsync<SkillRefusedException>(() => store.CreateCustomAsync(Draft("built"), "p", Ct))).Message);
        Assert.Equal(
            "A custom skill named 'mine' already exists.",
            (await Assert.ThrowsAsync<SkillRefusedException>(() => store.CreateCustomAsync(Draft(" mine "), "p", Ct))).Message);
        Assert.Equal(
            "'theirs' is team Alpha's skill name. An instance-wide skill needs a name no team's skill holds.",
            (await Assert.ThrowsAsync<SkillRefusedException>(() => store.CreateCustomAsync(Draft("theirs"), "p", Ct))).Message);
        Assert.StartsWith(
            "'plugin-x' begins 'plugin-'",
            (await Assert.ThrowsAsync<SkillRefusedException>(() => store.CreateCustomAsync(Draft("plugin-x"), "p", Ct))).Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentException>(() => store.CreateCustomAsync(Draft("Not Legal"), "p", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.CreateCustomAsync(new SkillDraft("fine", "two\nlines", ["member"], "Body."), "p", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.CreateCustomAsync(new SkillDraft("fine", "One.", ["member"], " "), "p", Ct));

        Assert.Equal(["built", "mine"], (await store.ListAsync(SkillKindFilter.All, null, SkillRoles.Manager, null, 50, Ct)).Select(s => s.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_custom_skill_is_renamed_and_deleted_and_a_built_in_is_neither()
    {
        var store = await CreateAsync();
        await store.ReplaceBuiltInsAsync([Draft("built")], DateTimeOffset.UtcNow, Ct);
        var mine = await store.CreateCustomAsync(new SkillDraft("mine", "  Mine.  ", ["Member", "any"], "Body."), "person@example.test", Ct);

        Assert.Equal(("mine", "Mine.", SkillKind.Custom, "person@example.test"), (mine.Name, mine.Description, mine.Kind, mine.UpdatedBy));
        Assert.Equal([SkillRoles.Member, SkillRoles.Any], mine.Roles);

        var renamed = (await store.UpdateCustomAsync("mine", new SkillDraft("ours", "Ours.", ["manager"], "New."), "other@example.test", Ct))!;
        Assert.Equal((mine.Id, "ours", "New.", "other@example.test"), (renamed.Id, renamed.Name, renamed.Body, renamed.UpdatedBy));
        Assert.Null(await store.GetAsync("mine", Ct));
        Assert.Null(await store.UpdateCustomAsync("nobody", Draft("nobody"), "p", Ct));

        Assert.Equal(
            "'built' is a built-in skill. Built-in skills change only with the product, so it cannot be edited or relabelled here.",
            (await Assert.ThrowsAsync<SkillRefusedException>(() => store.UpdateCustomAsync("built", Draft("built"), "p", Ct))).Message);
        Assert.Equal(
            "'built' is a built-in skill's name. A custom skill needs a name of its own.",
            (await Assert.ThrowsAsync<SkillRefusedException>(() => store.UpdateCustomAsync("ours", Draft("built"), "p", Ct))).Message);
        Assert.Equal(
            "'built' is a built-in skill. Built-in skills change only with the product, so it cannot be deleted.",
            (await Assert.ThrowsAsync<SkillRefusedException>(() => store.DeleteCustomAsync("built", Ct))).Message);

        Assert.True(await store.DeleteCustomAsync("ours", Ct));
        Assert.False(await store.DeleteCustomAsync("ours", Ct));
        Assert.False(await store.DeleteCustomAsync("Not Legal", Ct));
        Assert.NotNull(await store.GetAsync("built", Ct));
    }

    [Fact]
    public async Task A_team_skill_is_refused_an_instance_name_and_a_second_create_and_replaced_by_a_registration()
    {
        var store = await CreateAsync();
        await store.ReplaceBuiltInsAsync([Draft("built")], DateTimeOffset.UtcNow, Ct);
        await store.CreateCustomAsync(Draft("mine"), "person", Ct);
        await store.ReplacePluginSkillsAsync("zeta", [Member("plugin-zeta")], DateTimeOffset.UtcNow, Ct);

        foreach (var (name, said) in new[]
        {
            ("built", "'built' is a built-in skill's name. A team skill needs a name of its own."),
            ("mine", "'mine' is an instance-wide custom skill's name. A team skill needs a name of its own."),
        })
        {
            Assert.Equal(said, (await Assert.ThrowsAsync<SkillRefusedException>(() =>
                store.PutTeamSkillAsync("alpha", Draft(name), replace: true, null, Row(TenantActions.SkillCreated, name), Ct))).Message);
        }

        await Assert.ThrowsAsync<SkillRefusedException>(() =>
            store.PutTeamSkillAsync("alpha", Draft("plugin-zeta"), replace: true, null, Row(TenantActions.SkillCreated, "plugin-zeta"), Ct));

        var first = await store.PutTeamSkillAsync("alpha", Draft("playbook", "First."), replace: false, "person", Row(TenantActions.SkillCreated, "playbook"), Ct);
        Assert.Equal(("alpha", SkillKind.Custom, "First."), (first.Team, first.Kind, first.Body));

        Assert.Equal(
            "Team alpha already has a skill named 'playbook'.",
            (await Assert.ThrowsAsync<SkillRefusedException>(() =>
                store.PutTeamSkillAsync("alpha", Draft("playbook"), replace: false, null, Row(TenantActions.SkillCreated, "playbook"), Ct))).Message);

        var again = await store.PutTeamSkillAsync("alpha", Draft("playbook", "Second."), replace: true, null, Row(TenantActions.SkillChanged, "playbook"), Ct);
        Assert.Equal((first.Id, "Second.", (string?)null), (again.Id, again.Body, again.UpdatedBy));
        Assert.Equal([TenantActions.SkillCreated, TenantActions.SkillChanged], await ActionsAsync());
    }

    [Fact]
    public async Task A_team_skill_is_renamed_within_its_team_and_two_teams_may_hold_one_name()
    {
        var store = await CreateAsync();
        await store.PutTeamSkillAsync("beta", Draft("playbook", "Beta's."), replace: false, null, Row(TenantActions.SkillCreated, "playbook"), Ct);
        await store.PutTeamSkillAsync("alpha", Draft("playbook", "Alpha's."), replace: false, null, Row(TenantActions.SkillCreated, "playbook"), Ct);
        await store.PutTeamSkillAsync("alpha", Draft("notes"), replace: false, null, Row(TenantActions.SkillCreated, "notes"), Ct);

        Assert.Equal(["alpha", "beta"], (await store.FindAllAsync("playbook", Ct)).Select(s => s.Team));
        Assert.Equal(["notes", "playbook"], (await store.ListTeamAsync("alpha", Ct)).Select(s => s.Name));

        Assert.Equal(
            "Team alpha already has a skill named 'notes'.",
            (await Assert.ThrowsAsync<SkillRefusedException>(() =>
                store.UpdateTeamSkillAsync("alpha", "playbook", Draft("notes"), null, Row(TenantActions.SkillChanged, "playbook"), Ct))).Message);

        var renamed = (await store.UpdateTeamSkillAsync("alpha", "playbook", Draft("guide", "Renamed."), "person", Row(TenantActions.SkillChanged, "playbook"), Ct))!;
        Assert.Equal(("guide", "alpha", "Renamed.", "person"), (renamed.Name, renamed.Team, renamed.Body, renamed.UpdatedBy));
        Assert.Null(await store.UpdateTeamSkillAsync("alpha", "playbook", Draft("x"), null, Row(TenantActions.SkillChanged, "playbook"), Ct));
        Assert.Equal("Beta's.", Assert.Single(await store.FindAllAsync("playbook", Ct)).Body);

        await store.CreateCustomAsync(Draft("shared"), "p", Ct);
        await Assert.ThrowsAsync<SkillRefusedException>(() =>
            store.UpdateTeamSkillAsync("alpha", "guide", Draft("shared"), null, Row(TenantActions.SkillChanged, "guide"), Ct));
        Assert.Equal(["shared"], (await store.FindAllAsync("shared", Ct)).Select(s => s.Name));
        Assert.Empty(await store.FindAllAsync("Not Legal", Ct));
    }

    [Fact]
    public async Task A_teams_skills_go_with_one_row_each_and_only_that_team()
    {
        var store = await CreateAsync();
        await store.PutTeamSkillAsync("alpha", Draft("two"), replace: false, null, Row(TenantActions.SkillCreated, "two"), Ct);
        await store.PutTeamSkillAsync("alpha", Draft("one"), replace: false, null, Row(TenantActions.SkillCreated, "one"), Ct);
        await store.PutTeamSkillAsync("beta", Draft("one"), replace: false, null, Row(TenantActions.SkillCreated, "one"), Ct);

        Assert.False(await store.DeleteTeamSkillAsync("alpha", "none", Row(TenantActions.SkillDeleted, "none"), Ct));
        Assert.False(await store.DeleteTeamSkillAsync("alpha", "Not Legal", Row(TenantActions.SkillDeleted, "x"), Ct));
        Assert.Equal(["one", "two"], await store.DeleteTeamSkillsAsync("ALPHA", name => Row(TenantActions.SkillDeleted, name), Ct));
        Assert.Empty(await store.ListTeamAsync("alpha", Ct));
        Assert.True(await store.DeleteTeamSkillAsync("beta", "one", Row(TenantActions.SkillDeleted, "one"), Ct));

        Assert.Equal(
            [TenantActions.SkillCreated, TenantActions.SkillCreated, TenantActions.SkillCreated, TenantActions.SkillDeleted, TenantActions.SkillDeleted, TenantActions.SkillDeleted],
            await ActionsAsync());
    }

    [Fact]
    public async Task A_team_differing_only_in_case_is_the_same_team()
    {
        var store = await CreateAsync();
        await store.PutTeamSkillAsync("Alpha", Draft("playbook"), replace: false, null, Row(TenantActions.SkillCreated, "playbook"), Ct);

        Assert.Equal("Alpha", Assert.Single(await store.ListTeamAsync("alpha", Ct)).Team);
        await Assert.ThrowsAsync<SkillRefusedException>(() =>
            store.PutTeamSkillAsync("ALPHA", Draft("playbook"), replace: false, null, Row(TenantActions.SkillCreated, "playbook"), Ct));
        Assert.NotNull(await store.UpdateTeamSkillAsync("alpha", "playbook", Draft("playbook", "New."), null, Row(TenantActions.SkillChanged, "playbook"), Ct));
        Assert.Single(await store.ListOfferedAsync(SkillRoles.Manager, "aLPHA", null, 50, Ct));
        Assert.True(await store.DeleteTeamSkillAsync("ALPHA", "playbook", Row(TenantActions.SkillDeleted, "playbook"), Ct));
    }

    [Fact]
    public async Task A_name_or_a_team_with_a_trailing_space_is_another()
    {
        var store = await CreateAsync();
        await store.CreateCustomAsync(Draft("mine", "Mine."), "p", Ct);
        await store.PutTeamSkillAsync("alpha", Draft("kept", "Kept."), replace: false, null, Row(TenantActions.SkillCreated, "kept"), Ct);

        Assert.Null(await store.UpdateCustomAsync("mine ", Draft("mine", "Changed."), "p", Ct));
        Assert.Null(await store.UpdateTeamSkillAsync("alpha", "kept ", Draft("kept", "Changed."), null, Row(TenantActions.SkillChanged, "kept"), Ct));
        Assert.Null(await store.UpdateTeamSkillAsync("alpha ", "kept", Draft("kept", "Changed."), null, Row(TenantActions.SkillChanged, "kept"), Ct));
        Assert.Empty(await store.ListTeamAsync("alpha ", Ct));
        Assert.False(await store.DeleteTeamSkillAsync("alpha ", "kept", Row(TenantActions.SkillDeleted, "kept"), Ct));
        Assert.Empty(await store.DeleteTeamSkillsAsync("alpha ", name => Row(TenantActions.SkillDeleted, name), Ct));
        Assert.Equal(["mine"], (await store.ListOfferedAsync(SkillRoles.Manager, "alpha ", null, 50, Ct)).Select(s => s.Name));

        Assert.Equal("Mine.", (await store.GetAsync("mine", Ct))!.Body);
        Assert.Equal("Kept.", Assert.Single(await store.ListTeamAsync("alpha", Ct)).Body);
    }

    [Fact]
    public async Task Listing_pages_newest_first_below_its_cursor_by_kind()
    {
        var store = await CreateAsync();
        await store.ReplaceBuiltInsAsync([Draft("b-built"), Draft("a-built")], DateTimeOffset.UtcNow, Ct);
        await store.CreateCustomAsync(Draft("first"), "p", Ct);
        await store.CreateCustomAsync(Draft("second"), "p", Ct);
        await store.ReplacePluginSkillsAsync("zeta", [Member("plugin-zeta")], DateTimeOffset.UtcNow, Ct);

        var all = await store.ListAsync(SkillKindFilter.All, null, null, null, 50, Ct);
        Assert.Equal(["plugin-zeta", "second", "first", "a-built", "b-built"], all.Select(s => s.Name));

        Assert.Equal(["second", "first"], (await store.ListAsync(SkillKindFilter.Custom, null, null, null, 50, Ct)).Select(s => s.Name));
        Assert.Equal(["a-built", "b-built"], (await store.ListAsync(SkillKindFilter.BuiltIn, null, null, null, 50, Ct)).Select(s => s.Name));
        Assert.Equal(["plugin-zeta"], (await store.ListAsync(SkillKindFilter.Plugin, null, null, null, 50, Ct)).Select(s => s.Name));

        Assert.Equal(["first", "a-built"], (await store.ListAsync(SkillKindFilter.All, null, null, all[1].Id, 2, Ct)).Select(s => s.Name));
        Assert.Single(await store.ListAsync(SkillKindFilter.All, null, null, null, 0, Ct));
        Assert.Single(await store.ListOfferedAsync(SkillRoles.Manager, null, null, -5, Ct));
    }

    [Fact]
    public async Task Search_finds_any_word_in_the_name_the_description_or_the_body()
    {
        var store = await CreateAsync();
        await store.CreateCustomAsync(new SkillDraft("harbour-pilot", "Steer ships.", ["manager"], "Body."), "p", Ct);
        await store.CreateCustomAsync(new SkillDraft("weather", "Read the sky.", ["manager"], "Watch the barometer."), "p", Ct);
        await store.CreateCustomAsync(new SkillDraft("ledger", "Count things.", ["manager"], "Tally."), "p", Ct);

        Assert.Equal(["harbour-pilot"], await NamesAsync(store, "harbour"));
        Assert.Equal(["weather"], await NamesAsync(store, "sky"));
        Assert.Equal(["weather"], await NamesAsync(store, "barometer"));
        Assert.Equal(["harbour-pilot", "weather"], await NamesAsync(store, "ships   barometer"));
        Assert.Empty(await NamesAsync(store, "zanzibar"));
        Assert.Equal(3, (await store.ListAsync(SkillKindFilter.All, "   ", null, null, 50, Ct)).Count);
    }

    [Fact]
    public async Task Search_ignores_ascii_case()
    {
        var store = await CreateAsync();
        await store.CreateCustomAsync(new SkillDraft("zanzibar-rules", "The Zanzibar Rules.", ["manager"], "Sail to ZANZIBAR."), "p", Ct);
        await store.CreateCustomAsync(new SkillDraft("other", "Other.", ["manager"], "Nothing here."), "p", Ct);

        foreach (var query in new[] { "zanzibar", "ZANZIBAR", "zAnZiBaR", "RULES" })
        {
            Assert.Equal(["zanzibar-rules"], await NamesAsync(store, query));
        }

        Assert.Single(await store.ListOfferedAsync(SkillRoles.Manager, null, "SAIL", 50, Ct));
    }

    [Fact]
    public async Task Search_takes_percent_underscore_and_bracket_literally()
    {
        var store = await CreateAsync();
        await store.CreateCustomAsync(new SkillDraft("lit-one", "Literal.", ["manager"], "Costs 100% of a_b in [x] form."), "p", Ct);
        await store.CreateCustomAsync(new SkillDraft("lit-two", "Literal.", ["manager"], "Has 1000 units axb in box."), "p", Ct);

        Assert.Equal(["lit-one"], await NamesAsync(store, "100%"));
        Assert.Equal(["lit-one"], await NamesAsync(store, "a_b"));
        Assert.Equal(["lit-one"], await NamesAsync(store, "[x]"));
    }

    /// <summary>
    /// THE CASES SQLITE'S FTS5 INDEX IS THE ORACLE FOR: a stem finds its other forms, a word is a whole
    /// token (not a substring), case and a Latin diacritic fold, punctuation splits a word into a phrase,
    /// a word of punctuation alone matches nothing, a token under 3 or over 64 bytes is not stemmed, and
    /// the three columns are each searched.
    /// </summary>
    private static async Task<ISkillStore> IndexedAsync(ISkillStore store)
    {
        foreach (var (name, description, body) in new[]
        {
            ("alpha-one", "Steady.", "Before you run the suite, read this."),
            ("beta-two", "Steady.", "Deploy skills to the worktree."),
            ("gamma-three", "Steady.", "Order a café au lait. 1000 units."),
            ("delta-four", "Steady.", "Use foo bar here."),
            ("theta-five", "Steady.", "Use foo_bar there."),
            ("epsilon-six", "Steady.", "Greek ΑΘΗΝΑ, German Straße and Bücher."),
            ("zeta-seven", "Steady.", "ab " + new string('a', 61) + "ing"),
            ("iota-eight", "Steady.", new string('a', 62) + "ing"),
            ("eta-nine", "Says nothing more.", "Plain."),
        })
        {
            await store.CreateCustomAsync(new SkillDraft(name, description, ["manager"], body), "p", Ct);
        }

        return store;
    }

    [Theory]
    [InlineData("running", "alpha-one")]
    [InlineData("RUN", "alpha-one")]
    [InlineData("skill", "beta-two")]
    [InlineData("deploying", "beta-two")]
    [InlineData("work", "")]
    [InlineData("100", "")]
    [InlineData("1000", "gamma-three")]
    [InlineData("cafe", "gamma-three")]
    [InlineData("CaFÉ", "gamma-three")]
    [InlineData("foo-bar", "delta-four theta-five")]
    [InlineData("bar-foo", "")]
    [InlineData("%%", "")]
    [InlineData("%% running", "alpha-one")]
    [InlineData("zanzibar suite", "alpha-one")]
    [InlineData("ab", "zeta-seven")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "zeta-seven")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "")]
    [InlineData("αθηνα", "epsilon-six")]
    [InlineData("STRASSE", "")]
    [InlineData("straße", "epsilon-six")]
    [InlineData("bucher", "epsilon-six")]
    [InlineData("eta", "eta-nine")]
    [InlineData("nothing", "eta-nine")]
    [InlineData("seven", "zeta-seven")]
    public async Task Search_matches_as_sqlites_full_text_index_does(string query, string expected)
    {
        var store = await IndexedAsync(await CreateAsync());

        Assert.Equal(expected.Split(' ', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal), await NamesAsync(store, query));
    }

    [Fact]
    public async Task A_search_pages_newest_first_by_its_matches_alone()
    {
        var store = await CreateAsync();
        for (var i = 0; i < 6; i++)
        {
            await store.CreateCustomAsync(new SkillDraft($"page-{i}", "Steady.", ["manager"], i % 2 == 0 ? "Deploying now." : "Nothing."), "p", Ct);
        }

        var page = await store.ListAsync(SkillKindFilter.All, "deploy", null, null, 2, Ct);
        Assert.Equal(["page-4", "page-2"], page.Select(s => s.Name));
        Assert.Equal(["page-0"], (await store.ListAsync(SkillKindFilter.All, "deploy", null, page[^1].Id, 2, Ct)).Select(s => s.Name));
        Assert.Equal(["page-4"], (await store.ListOfferedAsync(SkillRoles.Manager, null, "deployed", 1, Ct)).Select(s => s.Name));
    }

    private static async Task<IReadOnlyList<string>> NamesAsync(ISkillStore store, string query) =>
        [.. (await store.ListAsync(SkillKindFilter.All, query, null, null, 50, Ct)).Select(s => s.Name).Order(StringComparer.Ordinal)];

    [Fact]
    public async Task Long_and_unicode_text_round_trips()
    {
        var store = await CreateAsync();
        var body = LongUnicode("# Skill");
        var description = "Use it for O'Brien's café 🚀 日本語.";

        await store.CreateCustomAsync(new SkillDraft("unicode", description, ["member"], body), "o'brien@example.test", Ct);
        await store.PutTeamSkillAsync("Équipe", new SkillDraft("team-unicode", description, ["member"], body), replace: false, null, Row(TenantActions.SkillCreated, "team-unicode"), Ct);

        var read = (await store.GetAsync("unicode", Ct))!;
        Assert.Equal((description, body, "o'brien@example.test"), (read.Description, read.Body, read.UpdatedBy));
        var team = Assert.Single(await store.ListTeamAsync("Équipe", Ct));
        Assert.Equal((description, body, "Équipe"), (team.Description, team.Body, team.Team));
    }

    [Fact]
    public async Task Concurrent_creates_of_one_custom_name_make_one()
    {
        var store = await CreateAsync();

        for (var round = 0; round < 5; round++)
        {
            var name = $"raced-{round}";
            var outcomes = await RaceAsync(8, async i =>
            {
                try
                {
                    await store.CreateCustomAsync(Draft(name, $"Body {i}."), "p", Ct);
                    return "created";
                }
                catch (SkillRefusedException)
                {
                    return "refused";
                }
            });

            Assert.Single(outcomes, o => o == "created");
            Assert.Equal(7, outcomes.Count(o => o == "refused"));
        }
    }

    [Fact]
    public async Task A_custom_skill_and_a_team_skill_raced_to_one_name_leave_one()
    {
        var store = await CreateAsync();

        for (var round = 0; round < 8; round++)
        {
            var name = $"contested-{round}";
            var outcomes = await RaceAsync(4, async i =>
            {
                try
                {
                    if (i == 0) await store.CreateCustomAsync(Draft(name), "p", Ct);
                    else await store.PutTeamSkillAsync($"team{i}", Draft(name), replace: true, null, Row(TenantActions.SkillCreated, name), Ct);
                    return i == 0 ? "custom" : "team";
                }
                catch (SkillRefusedException)
                {
                    return "refused";
                }
            });

            var held = await store.FindAllAsync(name, Ct);
            Assert.True(
                held.All(s => s.Team is null) || held.All(s => s.Team is not null),
                $"'{name}' is held by an instance-wide skill and a team's at once: {string.Join(", ", held.Select(s => s.Team ?? "instance"))}.");
            Assert.Equal(outcomes.Count(o => o != "refused"), held.Count);
        }
    }

    [Fact]
    public async Task Concurrent_registrations_of_one_team_skill_make_one_row()
    {
        var store = await CreateAsync();

        for (var round = 0; round < 5; round++)
        {
            var name = $"registered-{round}";
            await RaceAsync(8, async i =>
            {
                await store.PutTeamSkillAsync(i % 2 == 0 ? "alpha" : "ALPHA", Draft(name, $"Body {i}."), replace: true, null, Row(TenantActions.SkillCreated, name), Ct);
                return i;
            });

            Assert.Single(await store.FindAllAsync(name, Ct));
        }

        Assert.Equal(40, (await ActionsAsync()).Count);
    }
}

public sealed class SqliteSkillStoreTests : SkillStoreContract
{
    protected override Task<IdentityDatabase> OpenAsync() => Task.FromResult<IdentityDatabase>(new SqliteIdentityDatabase());
}

public sealed class SqlServerSkillStoreTests : SkillStoreContract
{
    protected override async Task<IdentityDatabase> OpenAsync() => await SqlServerIdentityDatabase.CreateAsync();
}
