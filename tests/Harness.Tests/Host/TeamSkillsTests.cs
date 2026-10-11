using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// TEAM SKILLS, through the real Host across two teams: a team's skill is offered only to that
/// team's members of its roles - listed by <c>skills_search</c> and in the prompt's skill list only
/// for them, and refused by <c>skills_get</c> to anyone else with a sentence. A person manages them
/// through Team settings' routes, the Skills list names the team, and deleting the team deletes them.
/// </summary>
public sealed class TeamSkillsTests(HostFixture host) : IClassFixture<HostFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<HttpClient> ManagerOfAsync(string team)
    {
        var key = await host.Services.GetRequiredService<IPrincipalStore>().MintAsync(
            new ContainerId(team, TeamRegistry.DefaultManagerName).ToString(),
            PrincipalKind.Container, team, Permits.All, ct: Ct);
        return host.Container(key);
    }

    private async Task<HttpClient> ConciergeAsync()
    {
        var person = await host.Services.GetRequiredService<IUserStore>().FindAsync("person@example.test", Ct);
        var key = await host.Services.GetRequiredService<IPrincipalStore>().MintAsync(
            ConciergeLaunchFactory.PrincipalId(person!.Id), PrincipalKind.TenantConcierge, null,
            ConciergeLaunchFactory.ConciergePermits, ownerUserId: person.Id, ct: Ct);
        return host.Container(key);
    }

    private static async Task<List<string?>> NamesAsync(HttpClient caller, string query = "") =>
        (await caller.GetFromJsonAsync<JsonElement[]>($"/api/me/skills{query}", Ct))!
            .Select(r => r.GetProperty("name").GetString()).ToList();

    private string ManagerPrompt(string team) =>
        host.Services.GetRequiredService<ContainerHost>()
            .Find(new ContainerId(team, TeamRegistry.DefaultManagerName))!.SystemPrompt;

    private static object Skill(string name, string role, string body) =>
        new { name, description = $"Use when running {name}.", roles = new[] { role }, body };

    [Fact]
    public async Task A_team_skill_is_offered_to_its_teams_members_of_its_roles_and_refused_to_another_team()
    {
        using var person = await host.PersonAsync();
        var created = await person.PostAsJsonAsync(
            $"/api/teams/{host.Alpha}/skills", Skill("alpha-playbook", "manager", "Quetzal steps."), Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(host.Alpha, (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("team").GetString());

        // Alpha's Manager: listed, found by search, loaded.
        using var alphaManager = await ManagerOfAsync(host.Alpha);
        Assert.Contains("alpha-playbook", await NamesAsync(alphaManager));
        Assert.Equal(["alpha-playbook"], await NamesAsync(alphaManager, "?q=quetzal"));
        var loaded = await alphaManager.GetAsync("/api/me/skills/alpha-playbook", Ct);
        Assert.Equal(HttpStatusCode.OK, loaded.StatusCode);
        Assert.Contains("Quetzal steps.", await loaded.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        // Beta's Manager, same role, another team: never listed, refused with a sentence that does
        // not name the other team.
        using var betaManager = await ManagerOfAsync(host.Beta);
        Assert.DoesNotContain("alpha-playbook", await NamesAsync(betaManager));
        Assert.Empty(await NamesAsync(betaManager, "?q=quetzal"));
        var refused = await betaManager.GetAsync("/api/me/skills/alpha-playbook", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        var sentence = await refused.Content.ReadAsStringAsync(Ct);
        Assert.Contains("belongs to another team", sentence, StringComparison.Ordinal);
        Assert.DoesNotContain(host.Alpha, sentence, StringComparison.Ordinal);

        // Alpha's member: its team, not its role.
        using var alphaMember = host.Container(host.AlphaContainerKey);
        Assert.DoesNotContain("alpha-playbook", await NamesAsync(alphaMember));
        var wrongRole = await alphaMember.GetAsync("/api/me/skills/alpha-playbook", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, wrongRole.StatusCode);
        Assert.Contains("not offered to the member role", await wrongRole.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        // The Concierge is on no team: never offered, told whose it is.
        using var concierge = await ConciergeAsync();
        Assert.DoesNotContain("alpha-playbook", await NamesAsync(concierge));
        var toConcierge = await concierge.GetAsync("/api/me/skills/alpha-playbook", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, toConcierge.StatusCode);
        Assert.Contains($"belongs to team {host.Alpha}", await toConcierge.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        await person.DeleteAsync($"/api/teams/{host.Alpha}/skills/alpha-playbook", Ct);
    }

    [Fact]
    public async Task A_team_skill_is_listed_in_its_teams_prompts_only_and_follows_an_edit_and_a_delete()
    {
        using var person = await host.PersonAsync();
        (await person.PostAsJsonAsync(
            $"/api/teams/{host.Alpha}/skills", Skill("alpha-runbook", "manager", "Steps."), Ct)).EnsureSuccessStatusCode();

        Assert.Contains("- `alpha-runbook` - Use when running alpha-runbook.", ManagerPrompt(host.Alpha), StringComparison.Ordinal);
        Assert.DoesNotContain("alpha-runbook", ManagerPrompt(host.Beta), StringComparison.Ordinal);

        var edited = await person.PutAsJsonAsync(
            $"/api/teams/{host.Alpha}/skills/alpha-runbook", new { description = "Use for the weekly run." }, Ct);
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        Assert.Contains("- `alpha-runbook` - Use for the weekly run.", ManagerPrompt(host.Alpha), StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.NoContent, (await person.DeleteAsync($"/api/teams/{host.Alpha}/skills/alpha-runbook", Ct)).StatusCode);
        Assert.DoesNotContain("alpha-runbook", ManagerPrompt(host.Alpha), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await person.DeleteAsync($"/api/teams/{host.Alpha}/skills/alpha-runbook", Ct)).StatusCode);
    }

    [Fact]
    public async Task Two_teams_may_hold_one_name_and_neither_may_shadow_an_instance_wide_skill()
    {
        using var person = await host.PersonAsync();
        (await person.PostAsJsonAsync($"/api/teams/{host.Alpha}/skills", Skill("shared-name", "manager", "Alpha's body."), Ct)).EnsureSuccessStatusCode();
        (await person.PostAsJsonAsync($"/api/teams/{host.Beta}/skills", Skill("shared-name", "manager", "Beta's body."), Ct)).EnsureSuccessStatusCode();

        using var alphaManager = await ManagerOfAsync(host.Alpha);
        using var betaManager = await ManagerOfAsync(host.Beta);
        Assert.Contains("Alpha's body.", await alphaManager.GetStringAsync("/api/me/skills/shared-name", Ct), StringComparison.Ordinal);
        Assert.Contains("Beta's body.", await betaManager.GetStringAsync("/api/me/skills/shared-name", Ct), StringComparison.Ordinal);

        var again = await person.PostAsJsonAsync($"/api/teams/{host.Alpha}/skills", Skill("shared-name", "manager", "x"), Ct);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        var builtIn = await person.PostAsJsonAsync($"/api/teams/{host.Alpha}/skills", Skill("manager", "manager", "x"), Ct);
        Assert.Equal(HttpStatusCode.Conflict, builtIn.StatusCode);
        Assert.Contains("built-in skill's name", await builtIn.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        var instance = await person.PostAsJsonAsync("/api/skills/shared-name", Skill("shared-name", "manager", "x"), Ct);
        Assert.Equal(HttpStatusCode.Conflict, instance.StatusCode);
        Assert.Contains("'s skill name", await instance.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        var missing = await person.PostAsJsonAsync("/api/teams/no-such-team/skills", Skill("x-skill", "manager", "x"), Ct);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        // The Skills list names the team beside each team skill.
        var listed = await person.GetFromJsonAsync<JsonElement[]>("/api/skills?q=body", Ct);
        Assert.Equal(
            [host.Alpha, host.Beta],
            listed!.Where(r => r.GetProperty("name").GetString() == "shared-name")
                .Select(r => r.GetProperty("team").GetString()!).Order(StringComparer.Ordinal));

        await person.DeleteAsync($"/api/teams/{host.Alpha}/skills/shared-name", Ct);
        await person.DeleteAsync($"/api/teams/{host.Beta}/skills/shared-name", Ct);
    }

    [Fact]
    public async Task Registering_a_team_skill_twice_rewrites_it_and_a_skill_file_registers_by_its_front_matter()
    {
        var teamSkills = host.Services.GetRequiredService<TeamSkills>();

        await teamSkills.RegisterAsync(host.Beta, new SkillDraft("beta-install", "Use it.", ["member"], "First."), null, ct: Ct);
        var again = await teamSkills.RegisterAsync(host.Beta, new SkillDraft("beta-install", "Use it.", ["member"], "Second."), null, ct: Ct);
        Assert.Equal("Second.", again.Body);
        Assert.Single(await teamSkills.ListAsync(host.Beta, Ct) ?? [], s => s.Name == "beta-install");

        var fromFile = await teamSkills.RegisterFileAsync(
            host.Beta, "---\nname: beta-file\ndescription: From a file.\nroles: manager, member\n---\n\nThe body.\n", null, ct: Ct);
        Assert.Equal(["manager", "member"], fromFile.Roles);
        Assert.Equal(host.Beta, fromFile.Team);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            teamSkills.RegisterAsync("no-such-team", new SkillDraft("x-skill", "Use it.", ["member"], "x"), null, ct: Ct));

        await teamSkills.DeleteAsync(host.Beta, "beta-install", ct: Ct);
        await teamSkills.DeleteAsync(host.Beta, "beta-file", ct: Ct);
    }

    [Fact]
    public async Task Deleting_a_team_deletes_its_skills_with_a_tenant_row_each_and_a_successor_inherits_none()
    {
        var registry = host.Services.GetRequiredService<TeamRegistry>();
        var agent = host.Services.GetRequiredService<AgentCatalog>().Definitions.First(d => d.Mode == AgentMode.Headless).Name;
        var gamma = (await registry.CreateAsync("Gamma Skills", agent, memberAgent: agent, ct: Ct)).Id;

        var teamSkills = host.Services.GetRequiredService<TeamSkills>();
        await teamSkills.RegisterAsync(gamma, new SkillDraft("gamma-one", "Use it.", ["manager"], "One."), null, ct: Ct);
        await teamSkills.RegisterAsync(gamma, new SkillDraft("gamma-two", "Use it.", ["member"], "Two."), null, ct: Ct);

        Assert.NotNull(await host.Services.GetRequiredService<TeamDeletion>().DeleteAsync(gamma, ct: Ct));

        var store = host.Services.GetRequiredService<ISkillStore>();
        Assert.Empty(await store.ListTeamAsync(gamma, Ct));
        Assert.Empty(await store.FindAllAsync("gamma-one", Ct));

        await using var connection = new SqliteConnection($"Data Source={Path.Combine(host.DataRoot, "messages.db")};Pooling=false");
        await connection.OpenAsync(Ct);
        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM tenant_events WHERE action = 'skill.deleted' AND subject = $team AND detail LIKE '%team deleted%'";
        count.Parameters.AddWithValue("$team", gamma);
        Assert.Equal(2L, (long)(await count.ExecuteScalarAsync(Ct))!);

        var successor = (await registry.CreateAsync("Gamma Skills", agent, memberAgent: agent, ct: Ct)).Id;
        Assert.Empty(await store.ListTeamAsync(successor, Ct));
        Assert.DoesNotContain("gamma-one", ManagerPrompt(successor), StringComparison.Ordinal);
    }
}
