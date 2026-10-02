using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// Built-in prompts, skills and presets come from the build. Read-only through every route,
/// offered to agents by role, and a team's additional instructions appended after the role prompt.
/// </summary>
public sealed class BuiltInsFromTheBuildTests(HostFixture host) : IClassFixture<HostFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void A_fresh_volume_holds_no_skill_files_and_no_catalog_file()
    {
        Assert.False(Directory.Exists(Path.Combine(host.DataRoot, "skills")));

        // Absent until a person saves a custom preset - another test here does - and never holding
        // a built-in one.
        var catalog = Path.Combine(host.DataRoot, "agents.json");
        if (File.Exists(catalog))
        {
            var text = File.ReadAllText(catalog);
            Assert.All(AgentCatalogFile.BuiltIns(), preset =>
                Assert.DoesNotContain($"\"{preset.Name}\"", text, StringComparison.Ordinal));
        }
        Assert.False(Directory.Exists(Path.Combine(host.DataRoot, "teams", host.Alpha, "skills")));
    }

    [Fact]
    public async Task The_skills_list_is_custom_by_default_and_shows_built_ins_when_asked()
    {
        using var person = await host.PersonAsync();

        var custom = await person.GetFromJsonAsync<JsonElement[]>("/api/skills", Ct);
        Assert.All(custom!, row => Assert.Equal("custom", row.GetProperty("kind").GetString()));

        var builtIn = await person.GetFromJsonAsync<JsonElement[]>("/api/skills?kind=builtin", Ct);
        Assert.Equal(
            BuiltInSkills.All.Select(s => s.Name).Order(StringComparer.Ordinal),
            builtIn!.Select(r => r.GetProperty("name").GetString()!).Order(StringComparer.Ordinal));

        var manager = builtIn!.Single(r => r.GetProperty("name").GetString() == "manager");
        Assert.Equal(["manager"], manager.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
        Assert.Equal(JsonValueKind.Null, manager.GetProperty("updatedBy").ValueKind);
    }

    [Fact]
    public async Task No_route_edits_deletes_or_shadows_a_built_in_skill()
    {
        using var person = await host.PersonAsync();

        var edit = await person.PutAsJsonAsync(
            "/api/skills/manager", new { description = "x", roles = new[] { "member" }, body = "x" }, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, edit.StatusCode);
        Assert.Contains("built-in skill", await edit.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        var delete = await person.DeleteAsync("/api/skills/worktrees", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);

        var shadow = await person.PostAsJsonAsync(
            "/api/skills/wrap-up", new { description = "x", roles = new[] { "member" }, body = "x" }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, shadow.StatusCode);
        Assert.Contains("built-in skill's name", await shadow.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_custom_skill_needs_its_roles_and_can_be_edited_relabelled_and_deleted()
    {
        using var person = await host.PersonAsync();

        var roleless = await person.PostAsJsonAsync(
            "/api/skills/roleless", new { description = "No roles.", body = "Body." }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, roleless.StatusCode);

        var created = await person.PostAsJsonAsync(
            "/api/skills/lint-rules",
            new { description = "Use when linting.", roles = new[] { "member" }, body = "Run the linter." },
            Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var dto = await created.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("custom", dto.GetProperty("kind").GetString());
        Assert.Equal("person@example.test", dto.GetProperty("updatedBy").GetString());

        var relabelled = await person.PutAsJsonAsync(
            "/api/skills/lint-rules", new { name = "lint-house-rules", roles = new[] { "member", "manager" } }, Ct);
        Assert.Equal(HttpStatusCode.OK, relabelled.StatusCode);
        var after = await relabelled.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("lint-house-rules", after.GetProperty("name").GetString());
        Assert.Equal("Run the linter.", after.GetProperty("body").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await person.DeleteAsync("/api/skills/lint-house-rules", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await person.GetAsync("/api/skills/lint-house-rules", Ct)).StatusCode);
    }

    [Fact]
    public async Task A_custom_skill_can_be_created_with_its_name_in_the_body()
    {
        using var person = await host.PersonAsync();

        var created = await person.PostAsJsonAsync(
            "/api/skills",
            new { name = "house-style", description = "Use when writing prose.", roles = new[] { "member" }, body = "Write plainly." },
            Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var dto = await created.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("house-style", dto.GetProperty("name").GetString());
        Assert.True(dto.GetProperty("id").GetInt64() > 0);

        var shadow = await person.PostAsJsonAsync(
            "/api/skills", new { name = "manager", description = "x", roles = new[] { "member" }, body = "x" }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, shadow.StatusCode);
        Assert.Contains("\"error\"", await shadow.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        var nameless = await person.PostAsJsonAsync(
            "/api/skills", new { description = "x", roles = new[] { "member" }, body = "x" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, nameless.StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await person.DeleteAsync("/api/skills/house-style", Ct)).StatusCode);
    }

    [Fact]
    public async Task A_member_is_refused_a_manager_skill_with_a_sentence_naming_its_role()
    {
        using var member = host.Container(host.AlphaContainerKey);

        var own = await member.GetAsync("/api/me/skills/member", Ct);
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);

        var manager = await member.GetAsync("/api/me/skills/manager", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, manager.StatusCode);
        Assert.Contains("not offered to the member role", await manager.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        var listed = await member.GetFromJsonAsync<JsonElement[]>("/api/me/skills", Ct);
        var names = listed!.Select(r => r.GetProperty("name").GetString()).ToList();
        Assert.Contains("worktrees", names);
        Assert.DoesNotContain("manager", names);
        Assert.DoesNotContain("wrap-up", names);
    }

    /// <summary>
    /// The Member and Manager skills an agent loads say to take the `heavy` lease with `lease`
    /// before heavy work and release it after, word for word.
    /// </summary>
    [Fact]
    public async Task The_member_and_manager_skills_served_to_agents_name_the_heavy_lease()
    {
        using var member = host.Container(host.AlphaContainerKey);
        var memberSkill = await member.GetStringAsync("/api/me/skills/member", Ct);
        Assert.Contains(McpContractTests.HeavyLeaseRule, McpContractTests.Flat(memberSkill), StringComparison.Ordinal);

        var key = await host.Services.GetRequiredService<IPrincipalStore>().MintAsync(
            new ContainerId(host.Alpha, TeamRegistry.DefaultManagerName).ToString(),
            PrincipalKind.Container, host.Alpha, Permits.All, ct: Ct);
        using var manager = host.Container(key);
        var managerSkill = await manager.GetStringAsync("/api/me/skills/manager", Ct);
        Assert.Contains(McpContractTests.HeavyLeaseRule, McpContractTests.Flat(managerSkill), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_managers_list_has_no_worktrees_and_it_is_refused_the_member_skill()
    {
        var key = await host.Services.GetRequiredService<IPrincipalStore>().MintAsync(
            new ContainerId(host.Alpha, TeamRegistry.DefaultManagerName).ToString(),
            PrincipalKind.Container, host.Alpha, Permits.All, ct: Ct);
        using var manager = host.Container(key);

        var listed = await manager.GetFromJsonAsync<JsonElement[]>("/api/me/skills", Ct);
        var names = listed!.Select(r => r.GetProperty("name").GetString()).ToList();
        Assert.Contains("manager", names);
        Assert.Contains("wrap-up", names);
        Assert.DoesNotContain("worktrees", names);

        var refused = await manager.GetAsync("/api/me/skills/worktrees", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("manager role", await refused.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    /// <summary>
    /// The authoring-plugins skill is for designing a plugin and writing its spec, which the Concierge
    /// and the Manager do; a Member building one reads the repository's docs instead.
    /// </summary>
    [Fact]
    public async Task The_plugin_authoring_skill_is_offered_to_the_concierge_and_manager_and_refused_to_a_member()
    {
        const string Name = "authoring-plugins";
        var skill = BuiltInSkills.Find(Name)!;
        Assert.Equal([SkillRoles.Concierge, SkillRoles.Manager], skill.Roles);

        // The skill states the polling principle and the two trigger settings that bound cost.
        Assert.Contains("Poll with plugins, spend models only when something happened", skill.Body, StringComparison.Ordinal);
        Assert.Contains("Wake the Manager when a run ends", skill.Body, StringComparison.Ordinal);
        Assert.Contains("Daily token cap", skill.Body, StringComparison.Ordinal);

        // It says which language to build in, that the spec records why, and that a plugin brings
        // everything it needs in its own folder.
        Assert.Contains("Default to **Go** for connectors", skill.Body, StringComparison.Ordinal);
        Assert.Contains("Choose **.NET** when the best or only SDK", skill.Body, StringComparison.Ordinal);
        Assert.Contains("Choose **Python** when the library", skill.Body, StringComparison.Ordinal);
        Assert.Contains("Record the choice and the reason in the spec", skill.Body, StringComparison.Ordinal);
        Assert.Contains("Plugins are self-contained.", skill.Body, StringComparison.Ordinal);
        Assert.Contains("Nothing a plugin needs is ever added", skill.Body, StringComparison.Ordinal);

        // OAuth is a connection: the platform holds the tokens, and nobody asks a person for one.
        Assert.Contains("OAuth accounts are connections, never secrets", skill.Body, StringComparison.Ordinal);
        Assert.Contains("Never ask the person for a token", skill.Body, StringComparison.Ordinal);
        Assert.Contains("Never write an \"authorize\" or \"login\" command", skill.Body, StringComparison.Ordinal);

        // `plugin-` is the namespace plugin skills are forced into; a built-in there could collide
        // with an installed plugin's skill (a plugin with id `authoring` would ship `plugin-authoring`).
        Assert.DoesNotContain(BuiltInSkills.All, s => s.Name.StartsWith("plugin-", StringComparison.Ordinal));

        var principals = host.Services.GetRequiredService<IPrincipalStore>();
        var person = await host.Services.GetRequiredService<IUserStore>().FindAsync("person@example.test", Ct);
        var conciergeKey = await principals.MintAsync(
            ConciergeLaunchFactory.PrincipalId(person!.Id), PrincipalKind.TenantConcierge, null,
            ConciergeLaunchFactory.ConciergePermits, ownerUserId: person.Id, ct: Ct);
        var managerKey = await principals.MintAsync(
            new ContainerId(host.Alpha, TeamRegistry.DefaultManagerName).ToString(),
            PrincipalKind.Container, host.Alpha, Permits.All, ct: Ct);

        foreach (var key in new[] { conciergeKey, managerKey })
        {
            using var caller = host.Container(key);

            var got = await caller.GetAsync($"/api/me/skills/{Name}", Ct);
            Assert.Equal(HttpStatusCode.OK, got.StatusCode);

            var found = await caller.GetFromJsonAsync<JsonElement[]>("/api/me/skills?q=plugin", Ct);
            Assert.Contains(Name, found!.Select(r => r.GetProperty("name").GetString()));
        }

        using var member = host.Container(host.AlphaContainerKey);
        var refused = await member.GetAsync($"/api/me/skills/{Name}", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("not offered to the member role", await refused.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        var searched = await member.GetFromJsonAsync<JsonElement[]>("/api/me/skills?q=plugin", Ct);
        Assert.DoesNotContain(Name, searched!.Select(r => r.GetProperty("name").GetString()));

        // Listed in the Manager's prompt by name and description, never by body.
        var manager = Container(host.Alpha, TeamRegistry.DefaultManagerName).SystemPrompt;
        Assert.Contains($"- `{Name}` - {skill.Description}", manager, StringComparison.Ordinal);
        Assert.DoesNotContain("## 8. The house rule for plugins that act outward", manager, StringComparison.Ordinal);
        Assert.DoesNotContain($"- `{Name}` - ", await NewMemberPromptAsync("Plumber", "Fixes pipes."), StringComparison.Ordinal);
    }

    /// <summary>
    /// A plugin reads the site collections its manifest declares: the plugin skill names `reads`,
    /// the `sites` block, the budget and the Delete/Archive advice, and the site skill names the read
    /// in the same sentence as `site.put`.
    /// </summary>
    [Fact]
    public void The_plugin_and_site_skills_name_the_declared_read()
    {
        static string Flat(string name) => System.Text.RegularExpressions.Regex.Replace(BuiltInSkills.Find(name)!.Body, @"\s+", " ");

        var plugins = Flat("authoring-plugins");
        Assert.Contains("\"reads\": [{\"site\": \"board\", \"collection\": \"items\"}]", plugins, StringComparison.Ordinal);
        Assert.Contains("Each run's request then carries `sites`", plugins, StringComparison.Ordinal);
        Assert.Contains("up to 8 MiB a run, less when the work batch, config or secrets are large", plugins, StringComparison.Ordinal);
        Assert.Contains("A page's Delete or Archive is best kept in site data the plugin reads - a `status` on the document - rather than in a second store", plugins, StringComparison.Ordinal);

        Assert.Contains(
            "(`site.put` records, and the collections their manifest `reads`, handed to each run) all read and write the same data.",
            Flat("building-sites"), StringComparison.Ordinal);
    }

    /// <summary>
    /// A site's files folder is taught where an agent writes a site's files and where a plugin learns
    /// it: the folder, the relative path in the data, the helper's link, and never a secret there.
    /// </summary>
    [Fact]
    public void The_site_skills_name_the_files_folder_and_its_link()
    {
        static string Flat(string name) => System.Text.RegularExpressions.Regex.Replace(BuiltInSkills.Find(name)!.Body, @"\s+", " ");

        var sites = Flat("building-sites");
        Assert.Contains("`<team documents>/sites/<site>/files/`", sites, StringComparison.Ordinal);
        Assert.Contains("`filesFolder`", sites, StringComparison.Ordinal);
        Assert.Contains("| `site.files.url(path)` |", sites, StringComparison.Ordinal);
        Assert.Contains("Store the path RELATIVE to the folder in the site's data", sites, StringComparison.Ordinal);
        Assert.Contains("Never write a secret, a credential or a token into a site's files or data", sites, StringComparison.Ordinal);

        // The download bullet points at the files folder, in neutral words.
        Assert.Contains("**Downloads work for the site's files folder.** To hand the person something the team made (a document, a spreadsheet, an image)", sites, StringComparison.Ordinal);
        Assert.DoesNotContain("download>", sites, StringComparison.Ordinal);

        var packaging = Flat("packaging-solutions");
        Assert.Contains("`<team documents>/sites/<site>/files/`", packaging, StringComparison.Ordinal);
        Assert.Contains("`site.files.url(path)`", packaging, StringComparison.Ordinal);
        Assert.Contains("`sites/<site>/files`, may be one of them", packaging, StringComparison.Ordinal);

        var plugins = Flat("authoring-plugins");
        Assert.Contains("Each run's request also carries `siteFiles`", plugins, StringComparison.Ordinal);
        Assert.Contains("Never write a secret or a token into a site's files or data", plugins, StringComparison.Ordinal);
    }

    /// <summary>
    /// The packaging-solutions skill is for writing a spec whose delivery is a solution package and
    /// handing the person its link, which the Concierge and the Manager do. It carries the four
    /// points, and the skills that lead to it point there.
    /// </summary>
    [Fact]
    public async Task The_packaging_skill_is_offered_to_the_concierge_and_manager_carries_its_four_points_and_is_pointed_to()
    {
        const string Name = "packaging-solutions";
        var skill = BuiltInSkills.Find(Name)!;
        Assert.Equal([SkillRoles.Concierge, SkillRoles.Manager], skill.Roles);

        // Where the package goes, what the Done-when names, two teams, and the hand-off.
        Assert.Contains("<team documents>/<id>-<version>/", skill.Body, StringComparison.Ordinal);
        Assert.Contains("\"`/api/solutions/check` passes\"", skill.Body, StringComparison.Ordinal);
        Assert.Contains("The team that builds it is not the team that runs it", skill.Body, StringComparison.Ordinal);
        Assert.Contains("$HARNESS_PUBLIC_URL/#/solutions/install?folder=", skill.Body, StringComparison.Ordinal);
        Assert.Contains("one line on what they will be asked", skill.Body, StringComparison.Ordinal);
        // A package binds its plugin members' secrets by key name, never a value.
        Assert.Contains("Secrets are key names, never values", skill.Body, StringComparison.Ordinal);
        Assert.Contains("\"secrets\": { \"apiKey\": \"ACME_API_KEY\" }", skill.Body, StringComparison.Ordinal);
        // The Concierge points at the launcher and the panel on the web's own routes.
        var concierge = BuiltInSkills.Find("concierge")!.Body;
        Assert.Contains("$HARNESS_PUBLIC_URL/#/solutions`", concierge, StringComparison.Ordinal);
        Assert.Contains("$HARNESS_PUBLIC_URL/#/solutions/<team>`", concierge, StringComparison.Ordinal);
        Assert.DoesNotContain("solutions/manage", concierge, StringComparison.Ordinal);

        foreach (var pointer in new[] { "concierge", "new-team", "authoring-plugins", "building-sites" })
        {
            Assert.Contains($"`{Name}`", BuiltInSkills.Find(pointer)!.Body, StringComparison.Ordinal);
        }

        var principals = host.Services.GetRequiredService<IPrincipalStore>();
        var person = await host.Services.GetRequiredService<IUserStore>().FindAsync("person@example.test", Ct);
        var conciergeKey = await principals.MintAsync(
            ConciergeLaunchFactory.PrincipalId(person!.Id), PrincipalKind.TenantConcierge, null,
            ConciergeLaunchFactory.ConciergePermits, ownerUserId: person.Id, ct: Ct);
        var managerKey = await principals.MintAsync(
            new ContainerId(host.Alpha, TeamRegistry.DefaultManagerName).ToString(),
            PrincipalKind.Container, host.Alpha, Permits.All, ct: Ct);

        foreach (var key in new[] { conciergeKey, managerKey })
        {
            using var caller = host.Container(key);
            Assert.Equal(HttpStatusCode.OK, (await caller.GetAsync($"/api/me/skills/{Name}", Ct)).StatusCode);

            var found = await caller.GetFromJsonAsync<JsonElement[]>("/api/me/skills?q=package", Ct);
            Assert.Contains(Name, found!.Select(r => r.GetProperty("name").GetString()));
        }

        using var member = host.Container(host.AlphaContainerKey);
        var refused = await member.GetAsync($"/api/me/skills/{Name}", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("not offered to the member role", await refused.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        var manager = Container(host.Alpha, TeamRegistry.DefaultManagerName).SystemPrompt;
        Assert.Contains($"- `{Name}` - {skill.Description}", manager, StringComparison.Ordinal);
    }

    /// <summary>
    /// The running-the-backlog skill is how the Concierge runs a whole backlog: plan, dispatch,
    /// watch, assess before the next step, act, reorder, tidy up and keep a run log. It is the
    /// Concierge's alone, and the `concierge` skill points to it.
    /// </summary>
    [Fact]
    public async Task The_backlog_running_skill_is_offered_to_the_concierge_only_carries_every_step_and_is_pointed_to()
    {
        const string Name = "running-the-backlog";
        var skill = BuiltInSkills.Find(Name)!;
        Assert.Equal([SkillRoles.Concierge], skill.Roles);

        foreach (var line in new[]
        {
            // Plan, and its ordering rules.
            "List every pending and ready item (`backlog  action: list",
            "read each one (`backlog  action: show",
            "An item that says it needs another merged first waits for it.",
            "Items that touch the same area (the same files, schema module, skill or dialog) go to the same team one after another, or wait.",
            "Independent items run in parallel, each on its own new team.",
            "Tell the person the plan in one message",
            "The person's request to run the backlog is the instruction to mark the planned items ready",
            // Dispatch.
            "create a team for it with `team_create`, named after the item: its citation and a short slug",
            "`backlog  action: dispatch  id: <id>  team: <team>`",
            "Record which team holds which item",
            // Watch.
            "Follow each team with `status` and `kanban`",
            "blocked on a person is raised to the person at once",
            "A launch-missing failure is re-sent once, then raised to the person.",
            // Assess before the next step, against the verification document.
            "Assess before the next step, every time",
            "Read its verification document in the team's documents folder.",
            "Check every Done-when line of the item against the verification document.",
            "Check the repository state with `repo`",
            "Check the item's landed and stranded state",
            "as met, not met or unverified",
            "in the team's own figures, marked as the team's",
            // Act on it.
            "tell the person the item is ready to merge",
            "name the Git dialog as the place to merge",
            "When the item reads landed, mark it implemented",
            "tell the same team to fix it, continuing the dispatch workflow",
            "causation: <the dispatch workflow's latest row>",
            "write a new backlog item for it with `backlog  action: add`, in the house format (Summary, The change, Constraints, Done when, Delivery)",
            // Reorder and continue.
            "After each assessment, re-plan",
            "Keep going until every item in the plan is implemented or the person stops it.",
            // Tidy up.
            "Archive the item: `backlog  action: archive",
            "Ask the person to delete the finished team; team deletion is the person's action.",
            "its documents, and a local repository unless the person ticks it",
            "Say which documents the team left, and ask whether to keep them.",
            // The run log, and resuming from it.
            "`backlog-run-<date>.md`",
            "your own working folder",
            "It lists the plan, each dispatch (item, team, workflow), each assessment and its outcome, each follow-up item, and what is waiting on the person.",
            "If you find an unfinished run log, read it before doing anything else, and ask the person whether to continue it.",
            // Every never.
            "Never merge or push a default branch.",
            "Never delete a team.",
            "Never close a workflow.",
            "Never mark an item implemented before it has landed, unless the person says they merged it outside the platform and `landed` reads `unknown`.",
            "Never estimate a figure you were not given.",
            "Ask the person for each of these, and keep going with everything else meanwhile.",
        })
        {
            Assert.Contains(Flat(line), Flat(skill.Body), StringComparison.Ordinal);
        }

        // The concierge skill points to it, and its ready rule names the run request.
        var concierge = BuiltInSkills.Find("concierge")!.Body;
        Assert.Contains($"`{Name}`", concierge, StringComparison.Ordinal);
        Assert.Contains(Flat("or asks you to run the backlog: then the items in the plan you stated to them"), Flat(concierge), StringComparison.Ordinal);

        var principals = host.Services.GetRequiredService<IPrincipalStore>();
        var person = await host.Services.GetRequiredService<IUserStore>().FindAsync("person@example.test", Ct);
        var conciergeKey = await principals.MintAsync(
            ConciergeLaunchFactory.PrincipalId(person!.Id), PrincipalKind.TenantConcierge, null,
            ConciergeLaunchFactory.ConciergePermits, ownerUserId: person.Id, ct: Ct);
        var managerKey = await principals.MintAsync(
            new ContainerId(host.Alpha, TeamRegistry.DefaultManagerName).ToString(),
            PrincipalKind.Container, host.Alpha, Permits.All, ct: Ct);

        using (var caller = host.Container(conciergeKey))
        {
            Assert.Equal(HttpStatusCode.OK, (await caller.GetAsync($"/api/me/skills/{Name}", Ct)).StatusCode);
            var found = await caller.GetFromJsonAsync<JsonElement[]>("/api/me/skills?q=backlog", Ct);
            Assert.Contains(Name, found!.Select(r => r.GetProperty("name").GetString()));
        }

        foreach (var (key, role) in new[] { (managerKey, "manager"), (host.AlphaContainerKey, "member") })
        {
            using var caller = host.Container(key);
            var refused = await caller.GetAsync($"/api/me/skills/{Name}", Ct);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Contains($"not offered to the {role} role", await refused.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

            var searched = await caller.GetFromJsonAsync<JsonElement[]>("/api/me/skills?q=backlog", Ct);
            Assert.DoesNotContain(Name, searched!.Select(r => r.GetProperty("name").GetString()));
        }

        Assert.DoesNotContain($"- `{Name}` - ", Container(host.Alpha, TeamRegistry.DefaultManagerName).SystemPrompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// The `backlog` tool says a request to run the backlog marks the items of the stated plan
    /// ready, and still only on the person's word.
    /// </summary>
    [Fact]
    public void The_backlog_tool_says_a_run_request_marks_the_planned_items_ready()
    {
        var description = typeof(PlatformMcpTools).GetMethod(nameof(PlatformMcpTools.Backlog))!
            .GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()!.Description;

        Assert.Contains("Mark an item ready only when the person tells you to", description, StringComparison.Ordinal);
        Assert.Contains("or asks you to run the backlog: then the items in the plan you stated to them.", description, StringComparison.Ordinal);
    }

    /// <summary>
    /// The Concierge runs a backlog, and what it never does in a run stays a person's: merge, team
    /// deletion and workflow close are `HumansOnly`, so the Concierge's key is refused them however
    /// the skill is read.
    /// </summary>
    [Fact]
    public void The_concierge_runs_a_backlog_and_leaves_merge_team_deletion_and_workflow_close_to_the_person()
    {
        var endpoints = host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();

        foreach (var (method, route) in new[]
        {
            ("POST", "/api/teams/{team}/repos/{repo}/merge-to-main"),
            ("POST", "/api/teams/{team}/repos/{repo}/bring-current-and-merge"),
            ("DELETE", "/api/teams/{team}"),
            ("POST", "/api/teams/{team}/workflows/{correlation:long}/close"),
        })
        {
            var endpoint = endpoints.Single(e =>
                string.Equals(e.RoutePattern.RawText, route, StringComparison.Ordinal)
                && e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains(method) == true);
            Assert.True(endpoint.Metadata.GetMetadata<HumansOnlyMarker>() is not null, $"{method} {route} is not HumansOnly");
        }

        var body = BuiltInSkills.Find("running-the-backlog")!.Body;
        Assert.Contains("Merging to the default branch is the person's action; you never merge, push or ask an agent to.", Flat(body), StringComparison.Ordinal);
        Assert.Contains("Never delete a team.", body, StringComparison.Ordinal);
        Assert.Contains("Never close a workflow.", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// A finished team's branch is what `landed` is read from until it is proven, so the skill reads
    /// `landed` before asking the person to delete the team. And when the person merged the work
    /// outside the platform and `landed` reads `unknown`, their word marks the item implemented, and
    /// the item's history records who confirmed it - the platform's `implementedBy`.
    /// </summary>
    [Fact]
    public void The_backlog_running_skill_reads_landed_before_team_deletion_and_takes_the_persons_word_when_landed_is_unknown()
    {
        var body = Flat(BuiltInSkills.Find("running-the-backlog")!.Body);

        var read = body.IndexOf(Flat("Read `landed` (`backlog  action: show`) before asking the person to delete the finished team"), StringComparison.Ordinal);
        var ask = body.IndexOf("Ask the person to delete the finished team;", StringComparison.Ordinal);
        Assert.True(read >= 0, "the skill does not say to read landed before asking to delete the team");
        Assert.True(read < ask, "the skill asks to delete the team before it reads landed");
        Assert.Contains("When it does not read landed, say so and what it reads, and let the person decide.", body, StringComparison.Ordinal);

        foreach (var line in new[]
        {
            "when the person tells you they merged the work outside the platform (on GitHub, or by hand) and `landed` still reads `unknown`, the person's word marks the item implemented",
            "The item's history records who confirmed it.",
            "Only `unknown` gives way to their word",
        })
        {
            Assert.Contains(Flat(line), body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_backlog_running_skill_asks_the_person_to_confirm_the_merge_before_team_deletion_when_the_start_was_not_recorded()
    {
        var body = Flat(BuiltInSkills.Find("running-the-backlog")!.Body);

        var start = body.IndexOf("Read `startRecorded` on the same `show`.", StringComparison.Ordinal);
        var ask = body.IndexOf("Ask the person to delete the finished team;", StringComparison.Ordinal);
        Assert.True(start >= 0, "the skill does not read startRecorded before team deletion");
        Assert.True(start < ask, "the skill asks to delete the team before it reads startRecorded");

        foreach (var line in new[]
        {
            "its landed is read live and is not kept after the team is gone: give the person the item's `startDetail` sentence, and ask them to confirm the work is merged before they delete the team.",
            "they can press Record where it started now on the item in the Backlog dialog; it is a person's button, not yours.",
        })
        {
            Assert.Contains(Flat(line), body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_backlog_running_skill_says_a_run_continues_unwatched_and_resumes_from_its_run_log()
    {
        var body = Flat(BuiltInSkills.Find("running-the-backlog")!.Body);

        foreach (var line in new[]
        {
            "A run continues while you work, whether or not the person is watching: closing the tab does not end you while you are producing output or calling the platform.",
            "A Concierge that has done nothing for the idle window with nobody watching is ended.",
            "A restarted Concierge resumes from the run log, so keep it current before every wait.",
            "Keep one document, `backlog-run-<date>.md`, in your own working folder.",
        })
        {
            Assert.Contains(Flat(line), body, StringComparison.Ordinal);
        }
    }

    /// <summary>Whitespace-insensitive text, so a pinned sentence survives re-wrapping.</summary>
    private static string Flat(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");

    [Fact]
    public async Task A_custom_member_skill_is_found_by_search_and_listed_in_a_new_members_prompt()
    {
        using var person = await host.PersonAsync();
        var created = await person.PostAsJsonAsync(
            "/api/skills/house-style",
            new { description = "Use when writing prose for this house.", roles = new[] { "member" }, body = "Short sentences. Zanzibar." },
            Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var member = host.Container(host.AlphaContainerKey);
        var found = await member.GetFromJsonAsync<JsonElement[]>("/api/me/skills?q=zanzibar", Ct);
        Assert.Equal(["house-style"], found!.Select(r => r.GetProperty("name").GetString()));

        var prompt = await NewMemberPromptAsync("House Writer", "Writes the prose.");
        Assert.Contains("- `house-style` - Use when writing prose for this house.", prompt, StringComparison.Ordinal);

        await person.DeleteAsync("/api/skills/house-style", Ct);
    }

    /// <summary>
    /// The skills say deliverables go in "the team's shared folder, named in your prompt". Unless the
    /// prompt names it by path, a Manager invents a plausible folder beside the team's others, and
    /// every document written there is missing from the Documents dialog.
    /// </summary>
    [Fact]
    public async Task The_manager_and_member_prompts_name_the_teams_documents_folder_by_its_path()
    {
        var shared = host.Services.GetRequiredService<TeamDocuments>().RootFor(host.Alpha);

        var member = await NewMemberPromptAsync("Archivist", "Files things.");
        var manager = Container(host.Alpha, TeamRegistry.DefaultManagerName).SystemPrompt;

        foreach (var prompt in new[] { member, manager })
        {
            Assert.Contains($"The team's shared folder is `{shared}`", prompt, StringComparison.Ordinal);
            Assert.DoesNotContain("{shared}", prompt, StringComparison.Ordinal);
        }

        // A member is told where a site's files go, under that same folder.
        Assert.Contains($"Files you make for one of the team's sites go in `{shared}/sites/<site>/files/`", Flat(member), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_members_prompt_is_the_member_prompt_then_its_role_then_the_teams_instructions_then_its_skills()
    {
        using var person = await host.PersonAsync();
        var set = await person.PutAsJsonAsync(
            $"/api/teams/{host.Beta}/additional-instructions",
            new { additionalInstructions = "Every commit message is in French." }, Ct);
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.Equal(
            "Every commit message is in French.",
            (await set.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("additionalInstructions").GetString());

        var prompt = await NewMemberPromptAsync("Translator", "Translates.", host.Beta);

        var role = prompt.IndexOf("Your role skill is `member`", StringComparison.Ordinal);
        var own = prompt.IndexOf("Translates.", StringComparison.Ordinal);
        var heading = prompt.IndexOf(BuiltInPrompts.AdditionalInstructionsHeading, StringComparison.Ordinal);
        var words = prompt.IndexOf("Every commit message is in French.", StringComparison.Ordinal);
        var skills = prompt.IndexOf(BuiltInPrompts.AvailableSkillsHeading, StringComparison.Ordinal);

        Assert.True(role >= 0 && role < own && own < heading && heading < words && words < skills, prompt);
        Assert.DoesNotContain("- `manager` - ", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("- `wrap-up` - ", prompt, StringComparison.Ordinal);
        Assert.Contains("- `worktrees` - ", prompt, StringComparison.Ordinal);

        // The team's Manager is re-prompted with them too, under the Manager prompt.
        var manager = Container(host.Beta, TeamRegistry.DefaultManagerName).SystemPrompt;
        Assert.Contains("Your role skill is `manager`", manager, StringComparison.Ordinal);
        Assert.Contains("Every commit message is in French.", manager, StringComparison.Ordinal);
        Assert.DoesNotContain("- `worktrees` - ", manager, StringComparison.Ordinal);

        // Cleared, nothing is appended.
        await person.PutAsJsonAsync(
            $"/api/teams/{host.Beta}/additional-instructions", new { additionalInstructions = "" }, Ct);
        Assert.DoesNotContain(
            BuiltInPrompts.AdditionalInstructionsHeading,
            Container(host.Beta, TeamRegistry.DefaultManagerName).SystemPrompt,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_built_in_preset_is_listed_read_only_and_refused_when_sent_back_changed()
    {
        using var person = await host.PersonAsync();

        var catalog = await person.GetFromJsonAsync<JsonElement>("/api/agents", Ct);
        var agents = catalog.GetProperty("agents").EnumerateArray().ToList();
        Assert.False(catalog.TryGetProperty("prompts", out _));
        Assert.All(agents, a => Assert.True(a.GetProperty("builtIn").GetBoolean()));

        var claude = AgentCatalogFile.BuiltIns().First(a => a.Name == "claude-headless");
        var changed = await person.PutAsJsonAsync(
            "/api/agents", new { agents = new[] { claude with { TimeoutSeconds = 5 } } }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, changed.StatusCode);
        Assert.Contains("built-in preset", await changed.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        // Sent back unchanged it is ignored, and nothing built-in is written to the file.
        var unchanged = await person.PutAsJsonAsync("/api/agents", new { agents = new[] { claude } }, Ct);
        Assert.Equal(HttpStatusCode.NoContent, unchanged.StatusCode);
        Assert.DoesNotContain(
            "claude-headless",
            await File.ReadAllTextAsync(AgentCatalogFile.PathIn(host.DataRoot), Ct),
            StringComparison.Ordinal);
    }

    private async Task<string> NewMemberPromptAsync(string label, string role, string? team = null)
    {
        team ??= host.Alpha;
        var registry = host.Services.GetRequiredService<TeamRegistry>();
        var agent = host.Services.GetRequiredService<AgentCatalog>().Definitions
            .First(d => d.Mode == AgentMode.Headless).Name;

        var snapshot = await registry.AddContainerAsync(team, label, agent, role, [], ct: Ct);
        return Container(team, snapshot.Id).SystemPrompt;
    }

    private MemberRuntime Container(string team, string name) =>
        host.Services.GetRequiredService<ContainerHost>().Find(new ContainerId(team, name))
            ?? throw new InvalidOperationException($"No container {team}/{name}.");
}
