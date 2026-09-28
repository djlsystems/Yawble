using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// A SETTINGS WRITE AND ITS TENANT ROW ARE ONE TRANSACTION, through the real Host. With
/// `tenant_events` taken away, a trigger create or delete and each team or instance setting does
/// not happen: nothing is stored and the Host goes on serving what was there. A Host of its own per
/// test, because the only honest way to make the row unwritable is to take the table away.
/// </summary>
public sealed class SettingsAuditTransactionTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _dataRoot =
        Path.Combine(Path.GetTempPath(), $"harness-settings-audit-{Guid.NewGuid():N}");

    private WebApplicationFactory<Program> _factory = null!;

    private string Database => Path.Combine(_dataRoot, "messages.db");

    private TeamRegistry Teams => _factory.Services.GetRequiredService<TeamRegistry>();

    private AgentCatalog Catalog => _factory.Services.GetRequiredService<AgentCatalog>();

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning"));

        var agent = Catalog.Definitions.First(d => d.Mode == AgentMode.Headless).Name;
        await Teams.CreateAsync("Alpha", agent, memberAgent: agent);
        await _factory.Services.GetRequiredService<IUserStore>().CreateAsync("person@example.test", HostFixture.Password);
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        SqliteConnection.ClearAllPools();

        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private async Task<HttpClient> PersonAsync()
    {
        var client = _factory.CreateClient();
        (await client.PostAsJsonAsync(
            "/api/auth/login", new { email = "person@example.test", password = HostFixture.Password }, Ct))
            .EnsureSuccessStatusCode();
        return client;
    }

    private async Task DropTenantEventsAsync()
    {
        await using var connection = new SqliteConnection($"Data Source={Database};Pooling=false");
        await connection.OpenAsync(Ct);
        await using var drop = connection.CreateCommand();
        drop.CommandText = "DROP TABLE tenant_events";
        await drop.ExecuteNonQueryAsync(Ct);
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={Database};Pooling=false");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(Ct);
    }

    [Fact]
    public async Task Each_settings_write_lands_with_its_tenant_row_naming_the_person()
    {
        var client = await PersonAsync();
        await Teams.SetReposAsync("Alpha", ["https://example.test/owner/Widget.git"], Ct);
        var headless = Catalog.Definitions.Where(d => d.Mode == AgentMode.Headless).Select(d => d.Name).ToList();
        var interactive = Catalog.Definitions.First(d => d.Mode == AgentMode.Interactive).Name;

        var created = await client.PostAsJsonAsync("/api/teams/Alpha/triggers", new
        {
            name = "Poll", kind = "every", intervalSeconds = 300, instruction = "look",
        }, Ct);
        created.EnsureSuccessStatusCode();
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetString()!;
        (await client.DeleteAsync($"/api/teams/Alpha/triggers/{id}", Ct)).EnsureSuccessStatusCode();
        (await client.PutAsJsonAsync("/api/teams/Alpha/budget", new { budgetTokens = 5000 }, Ct)).EnsureSuccessStatusCode();
        (await client.PutAsJsonAsync(
            "/api/teams/Alpha/additional-instructions", new { additionalInstructions = "Be brief." }, Ct)).EnsureSuccessStatusCode();
        (await client.PutAsJsonAsync(
            "/api/teams/Alpha/repos/Widget/default-branch", new { branch = "trunk" }, Ct)).EnsureSuccessStatusCode();
        (await client.PutAsJsonAsync("/api/concierge", new { agent = interactive }, Ct)).EnsureSuccessStatusCode();
        (await client.PutAsJsonAsync("/api/teams/Alpha/member-agent", new { agents = headless }, Ct)).EnsureSuccessStatusCode();

        var rows = (await _factory.Services.GetRequiredService<ITenantLog>().ReadAsync(take: 100, ct: Ct)).Events;
        TenantEvent Row(string action, string? subject) =>
            Assert.Single(rows, r => r.Action == action && r.Subject == subject);

        Assert.Equal("person@example.test", Row(TenantActions.ScheduleCreated, id).ActorEmail);
        Assert.Equal("person@example.test", Row(TenantActions.ScheduleDeleted, id).ActorEmail);
        Assert.Contains("5000", Row(TenantActions.TeamBudgetChanged, "Alpha").Detail, StringComparison.Ordinal);
        Assert.Contains("true", Row(TenantActions.TeamInstructionsChanged, "Alpha").Detail, StringComparison.Ordinal);
        Assert.Contains("trunk", Row(TenantActions.RepoDefaultBranchSet, "Alpha/Widget").Detail, StringComparison.Ordinal);
        Assert.Contains(interactive, Row(TenantActions.ConciergeChanged, null).Detail, StringComparison.Ordinal);
        Assert.Contains(headless[^1], Row(TenantActions.TeamMemberAgentChanged, "Alpha").Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_trigger_is_not_created_when_its_tenant_row_cannot_be_written()
    {
        var client = await PersonAsync();
        await DropTenantEventsAsync();

        var created = await client.PostAsJsonAsync("/api/teams/Alpha/triggers", new
        {
            name = "Poll", kind = "every", intervalSeconds = 300, instruction = "look",
        }, Ct);

        Assert.False(created.IsSuccessStatusCode);
        Assert.Empty(await _factory.Services.GetRequiredService<ITriggerStore>().ListForTeamAsync("Alpha", Ct));
    }

    [Fact]
    public async Task A_trigger_is_not_deleted_when_its_tenant_row_cannot_be_written()
    {
        var client = await PersonAsync();
        var created = await client.PostAsJsonAsync("/api/teams/Alpha/triggers", new
        {
            name = "Poll", kind = "every", intervalSeconds = 300, instruction = "look",
        }, Ct);
        created.EnsureSuccessStatusCode();
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetString()!;
        await DropTenantEventsAsync();

        var deleted = await client.DeleteAsync($"/api/teams/Alpha/triggers/{id}", Ct);

        Assert.False(deleted.IsSuccessStatusCode);
        Assert.NotNull(await _factory.Services.GetRequiredService<ITriggerStore>().FindAsync(id, Ct));
    }

    [Fact]
    public async Task A_team_budget_is_not_changed_when_its_tenant_row_cannot_be_written()
    {
        var client = await PersonAsync();
        await DropTenantEventsAsync();

        var set = await client.PutAsJsonAsync("/api/teams/Alpha/budget", new { budgetTokens = 5000 }, Ct);

        Assert.False(set.IsSuccessStatusCode);
        Assert.Null(Teams.BudgetFor("Alpha"));
        Assert.IsType<DBNull>(await ScalarAsync("SELECT budget_tokens FROM teams WHERE id = 'Alpha'"));
    }

    [Fact]
    public async Task Team_instructions_are_not_changed_when_their_tenant_row_cannot_be_written()
    {
        var client = await PersonAsync();
        await DropTenantEventsAsync();

        var set = await client.PutAsJsonAsync(
            "/api/teams/Alpha/additional-instructions", new { additionalInstructions = "Be brief." }, Ct);

        Assert.False(set.IsSuccessStatusCode);
        Assert.Null(Teams.AdditionalInstructionsFor("Alpha"));
        Assert.IsType<DBNull>(await ScalarAsync("SELECT additional_instructions FROM teams WHERE id = 'Alpha'"));
    }

    [Fact]
    public async Task A_repositorys_default_branch_is_not_set_when_its_tenant_row_cannot_be_written()
    {
        var client = await PersonAsync();
        await Teams.SetReposAsync("Alpha", ["https://example.test/owner/Widget.git"], Ct);
        await DropTenantEventsAsync();

        var set = await client.PutAsJsonAsync(
            "/api/teams/Alpha/repos/Widget/default-branch", new { branch = "trunk" }, Ct);

        Assert.False(set.IsSuccessStatusCode);
        Assert.Null(Teams.DefaultBranchFor("Alpha", "Widget").SetByPerson);
        Assert.Equal(0L, await ScalarAsync(
            "SELECT COUNT(*) FROM team_repo_default_branches WHERE set_by_person IS NOT NULL"));
    }

    [Fact]
    public async Task The_concierge_agent_is_not_changed_when_its_tenant_row_cannot_be_written()
    {
        var client = await PersonAsync();
        var before = Teams.Concierge();
        var chosen = Catalog.Definitions.First(d => d.Mode == AgentMode.Interactive).Name;
        await DropTenantEventsAsync();

        var set = await client.PutAsJsonAsync("/api/concierge", new { agent = chosen }, Ct);

        Assert.False(set.IsSuccessStatusCode);
        Assert.Equal(before, Teams.Concierge());
        Assert.Equal(0L, await ScalarAsync(
            "SELECT COUNT(*) FROM tenant_interactive_agent_settings WHERE interactive_agent IS NOT NULL"));
    }

    [Fact]
    public async Task A_member_edit_is_not_saved_when_its_tenant_rows_cannot_be_written()
    {
        var client = await PersonAsync();
        var member = Teams.ContainerIdsOf("Alpha").First().Name;
        await DropTenantEventsAsync();

        var set = await client.PatchAsJsonAsync(
            $"/api/teams/Alpha/containers/{member}", new { name = "Renamed", systemPrompt = "Be careful." }, Ct);

        Assert.False(set.IsSuccessStatusCode);
        Assert.Equal(0L, await ScalarAsync(
            "SELECT COUNT(*) FROM team_members WHERE label = 'Renamed' OR system_prompt IS NOT NULL"));
    }

    [Fact]
    public async Task A_teams_member_agents_are_not_changed_when_their_tenant_row_cannot_be_written()
    {
        var client = await PersonAsync();
        var before = Teams.MemberAgentsFor("Alpha")!;
        var headless = Catalog.Definitions.Where(d => d.Mode == AgentMode.Headless).Select(d => d.Name).ToList();
        var chosen = headless.First(name => !before.Contains(name, StringComparer.OrdinalIgnoreCase));
        await DropTenantEventsAsync();

        var set = await client.PutAsJsonAsync("/api/teams/Alpha/member-agent", new { agents = new[] { chosen } }, Ct);

        Assert.False(set.IsSuccessStatusCode);
        Assert.Equal(before, Teams.MemberAgentsFor("Alpha"));
        Assert.Equal(before[0], (string?)await ScalarAsync("SELECT member_agent FROM teams WHERE id = 'Alpha'"));
    }
}
