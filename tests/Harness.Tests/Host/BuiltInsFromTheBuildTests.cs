using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
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
        // A package binds its plugin members' secrets by key name, never a value (B001T).
        Assert.Contains("Secrets are key names, never values", skill.Body, StringComparison.Ordinal);
        Assert.Contains("\"secrets\": { \"apiKey\": \"ACME_API_KEY\" }", skill.Body, StringComparison.Ordinal);

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
