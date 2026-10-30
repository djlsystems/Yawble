using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// WHICH TEAMS MAY USE A CONNECTION (team_connections, auth-032): a person assigns a connection to a
/// team in Team settings or at creation, its plugin members bind only what the team was given, an
/// agent's hire binds an assigned one and is refused another, no machine principal assigns, a removal
/// asks first and unbinds, a required slot with nothing bound reads Needs you, and a clone runs with
/// the original's connections.
/// </summary>
public sealed partial class ConnectionsTests
{
    [Fact]
    public async Task A_connection_assigned_in_team_settings_is_this_teams_only_with_its_row_once()
    {
        var id = await ConnectAsync();
        var other = (await Services.GetRequiredService<TeamRegistry>().CreateAsync("Other", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;

        // A team starts with none.
        Assert.Empty((await GetAsync($"/api/teams/{_team}/connections")).GetProperty("connections").EnumerateArray());

        var (status, body) = await SendAsync(HttpMethod.Post, $"/api/teams/{_team}/connections", new { connection = id });
        Assert.True(status == HttpStatusCode.Created, body);

        var listed = Assert.Single((await GetAsync($"/api/teams/{_team}/connections")).GetProperty("connections").EnumerateArray());
        Assert.Equal(id, listed.GetProperty("connection").GetProperty("id").GetString());
        Assert.Empty((await GetAsync($"/api/teams/{other}/connections")).GetProperty("connections").EnumerateArray());

        // What a Manager's hire may bind is what was assigned, here and nowhere else.
        Assert.Equal(id, Assert.Single((await GetAsync($"/api/teams/{_team}/hiring")).GetProperty("connections").EnumerateArray()).GetProperty("id").GetString());
        Assert.Empty((await GetAsync($"/api/teams/{other}/hiring")).GetProperty("connections").EnumerateArray());

        // Again: 200, nothing written.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, $"/api/teams/{_team}/connections", new { connection = id })).Status);
        var row = JsonDocument.Parse(Assert.Single(await TenantDetailsAsync(TenantActions.TeamConnectionAssigned))).RootElement;
        Assert.Equal((_team, id, "team settings"), (row.GetProperty("team").GetString(), row.GetProperty("connection").GetString(), row.GetProperty("via").GetString()));
        Assert.All(_provider.Issued, t => Assert.DoesNotContain(t.Access, row.GetRawText()));

        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Post, $"/api/teams/{_team}/connections", new { connection = "conn-nope" })).Status);
    }

    [Fact]
    public async Task A_manager_hire_binds_a_connection_assigned_to_its_team_and_is_refused_one_that_is_not()
    {
        var id = await ConnectAsync();
        var tools = ManagerTools(await ManagerKeyAsync());

        var refused = await tools.Hire("Early", plugin: "mailer", connections: new() { ["mail"] = id }, cancellationToken: Ct);
        Assert.StartsWith("HTTP 400", refused);
        Assert.Contains("is not assigned to this team", refused);

        // A person assigns it - no member of the team has bound it - and the Manager's hire binds it.
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(HttpMethod.Post, $"/api/teams/{_team}/connections", new { connection = id })).Status);
        Assert.StartsWith("HTTP 200", await tools.Hire("Inbox", plugin: "mailer", connections: new() { ["mail"] = id }, cancellationToken: Ct));
        Assert.Equal(id, (await GetAsync($"/api/teams/{_team}/members/Inbox/plugin-settings")).GetProperty("connections").GetProperty("mail").GetString());
        Assert.Equal(MessageTypes.Completed, (await TellAndAwaitAsync("Inbox")).Type);
    }

    [Fact]
    public async Task No_machine_principal_assigns_or_removes_a_connection()
    {
        var id = await ConnectAsync();
        using var manager = _factory.CreateClient();
        manager.DefaultRequestHeaders.Add(Harness.Host.Auth.ApiKeyAuthenticationHandler.Header, await ManagerKeyAsync());

        using (var assign = await manager.PostAsJsonAsync($"/api/teams/{_team}/connections", new { connection = id }, Ct))
        {
            Assert.Equal(HttpStatusCode.Forbidden, assign.StatusCode);
        }

        using (var list = await manager.GetAsync($"/api/teams/{_team}/connections", Ct))
        {
            Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        }

        Assert.Equal(HttpStatusCode.Created, (await SendAsync(HttpMethod.Post, $"/api/teams/{_team}/connections", new { connection = id })).Status);
        using (var remove = await manager.DeleteAsync($"/api/teams/{_team}/connections/{id}?unbind=true", Ct))
        {
            Assert.Equal(HttpStatusCode.Forbidden, remove.StatusCode);
        }

        // A create naming connections is a person's too.
        using (var create = await manager.PostAsJsonAsync("/api/teams", new { name = "Agent made", agent = "claude-headless", memberAgents = new[] { "claude-headless" }, connections = new[] { id } }, Ct))
        {
            Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        }

        Assert.Single(await TenantDetailsAsync(TenantActions.TeamConnectionAssigned));
    }

    [Fact]
    public async Task A_new_team_starts_with_the_connections_its_creator_chose()
    {
        var id = await ConnectAsync();

        var (status, body) = await SendAsync(HttpMethod.Post, "/api/teams",
            new { name = "Fresh", agent = "claude-headless", memberAgents = new[] { "claude-headless" }, connections = new[] { id } });
        Assert.True(status is HttpStatusCode.OK or HttpStatusCode.Created, body);
        var fresh = JsonDocument.Parse(body).RootElement.GetProperty("id").GetString()!;

        Assert.Equal(id, Assert.Single((await GetAsync($"/api/teams/{fresh}/connections")).GetProperty("connections").EnumerateArray())
            .GetProperty("connection").GetProperty("id").GetString());
        Assert.Equal("new team", JsonDocument.Parse(Assert.Single(await TenantDetailsAsync(TenantActions.TeamConnectionAssigned))).RootElement.GetProperty("via").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Post, "/api/teams",
            new { name = "Nope", agent = "claude-headless", memberAgents = new[] { "claude-headless" }, connections = new[] { "conn-nope" } })).Status);
        Assert.False(Services.GetRequiredService<TeamRegistry>().Exists("Nope"));
    }

    [Fact]
    public async Task A_new_team_whose_connections_record_cannot_be_written_is_not_created_and_says_so()
    {
        var id = await ConnectAsync();
        var store = Services.GetRequiredService<IConnectionStore>();

        await ExecuteAsync("CREATE TRIGGER refuse_tenant BEFORE INSERT ON tenant_events BEGIN SELECT RAISE(ABORT, 'refused by the test'); END");
        var (status, body) = await SendAsync(HttpMethod.Post, "/api/teams",
            new { name = "Halfway", agent = "claude-headless", memberAgents = new[] { "claude-headless" }, connections = new[] { id } });
        await ExecuteAsync("DROP TRIGGER refuse_tenant");

        Assert.Equal(HttpStatusCode.InternalServerError, status);
        Assert.StartsWith("The team was not created: its connections' record could not be written", JsonDocument.Parse(body).RootElement.GetProperty("error").GetString());
        Assert.False(Services.GetRequiredService<TeamRegistry>().Exists("Halfway"));
        Assert.DoesNotContain(id, (await store.AllAssignmentsAsync(Ct)).Select(g => g.Key));
        Assert.DoesNotContain(await Services.GetRequiredService<ITeamStore>().TeamsAsync(Ct), t => t.Id == "Halfway");

        // Nothing was left in the way: the same create, with the row writable, makes the team and its assignment.
        (status, body) = await SendAsync(HttpMethod.Post, "/api/teams",
            new { name = "Halfway", agent = "claude-headless", memberAgents = new[] { "claude-headless" }, connections = new[] { id } });
        Assert.True(status is HttpStatusCode.OK or HttpStatusCode.Created, body);
        Assert.Single((await GetAsync("/api/teams/Halfway/connections")).GetProperty("connections").EnumerateArray());
    }

    [Fact]
    public async Task Removing_a_connection_from_a_team_asks_first_then_unbinds_its_members_who_read_needs_you()
    {
        var id = await ConnectAsync();
        await HireAsync("Inbox", id);

        // A person's binding assigned it, in the same transaction.
        Assert.Single((await GetAsync($"/api/teams/{_team}/connections")).GetProperty("connections").EnumerateArray());

        var (status, body) = await SendAsync(HttpMethod.Delete, $"/api/teams/{_team}/connections/{id}", null);
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains("Inbox", body);
        Assert.Equal("Inbox", Assert.Single(JsonDocument.Parse(body).RootElement.GetProperty("usedBy").EnumerateArray()).GetProperty("member").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, $"/api/teams/{_team}/connections/{id}?unbind=true", null)).Status);

        Assert.Empty((await GetAsync($"/api/teams/{_team}/connections")).GetProperty("connections").EnumerateArray());
        Assert.Empty((await GetAsync($"/api/teams/{_team}/members/Inbox/plugin-settings")).GetProperty("connections").EnumerateObject());
        Assert.Single((await GetAsync("/api/connections")).EnumerateArray());

        var snapshot = Services.GetRequiredService<Harness.Containers.ContainerHost>().Find(new ContainerId(_team, "Inbox"))!.Snapshot();
        Assert.Contains("`mail`", snapshot.NeedsPerson);
        Assert.Contains("removed from this team", snapshot.Blocked);

        var removed = JsonDocument.Parse(Assert.Single(await TenantDetailsAsync(TenantActions.TeamConnectionRemoved))).RootElement;
        Assert.Equal("Inbox", Assert.Single(removed.GetProperty("unbound").EnumerateArray()).GetString());
        Assert.Contains(await TenantDetailsAsync(TenantActions.MemberConnectionsChanged), d => d.Contains("connection removed from the team"));

        // Its run waits for a person - needs-person, never an error.
        var failed = await TellAndAwaitAsync("Inbox");
        Assert.Equal(MessageTypes.Failed, failed.Type);
        Assert.Equal(FailureClasses.NeedsPerson, JsonDocument.Parse(failed.Payload).RootElement.GetProperty(PayloadFields.FailureClass).GetString());

        // Binding another connection the team was given clears every mark the removal left: the card
        // is not stopped or blocked, and the removal's own workflow no longer reads Blocked.
        var log = Services.GetRequiredService<IMessageLog>();
        var blockedRow = (await log.ReadAfterAsync(0, [MessageTypes.Blocked], int.MaxValue, Ct))
            .Single(m => m.Source == new ContainerId(_team, "Inbox").ToString());
        var other = await ConnectAsync();
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(HttpMethod.Post, $"/api/teams/{_team}/connections", new { connection = other })).Status);
        var (bound, boundBody) = await SendAsync(HttpMethod.Put, $"/api/teams/{_team}/members/Inbox/plugin-settings",
            new { config = new { }, connections = new Dictionary<string, string> { ["mail"] = other } });
        Assert.True(bound == HttpStatusCode.OK, boundBody);

        snapshot = Services.GetRequiredService<Harness.Containers.ContainerHost>().Find(new ContainerId(_team, "Inbox"))!.Snapshot();
        Assert.Null(snapshot.NeedsPerson);
        Assert.Null(snapshot.Blocked);
        Assert.Null(snapshot.Failed);
        Assert.Null(snapshot.FailureClass);
        Assert.Empty(await log.OpenWorkflowsAmongAsync([blockedRow.CorrelationId], Ct));
    }

    [Fact]
    public async Task After_a_removal_binding_another_assigned_connection_clears_the_blocked_mark_and_ends_its_wait()
    {
        var id = await ConnectAsync();
        var other = await ConnectAsync();
        await HireAsync("Inbox", id);
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(HttpMethod.Post, $"/api/teams/{_team}/connections", new { connection = other })).Status);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, $"/api/teams/{_team}/connections/{id}?unbind=true", null)).Status);

        var member = new ContainerId(_team, "Inbox");
        var runtime = Services.GetRequiredService<Harness.Containers.ContainerHost>().Find(member)!;
        Assert.Contains("removed from this team", runtime.Snapshot().Blocked);

        var log = Services.GetRequiredService<IMessageLog>();
        var wait = (await log.ReadAfterAsync(0, [MessageTypes.Blocked], int.MaxValue, Ct)).Single(m => m.Source == member.ToString());
        Assert.Equal(FailureClasses.NeedsPerson, new ContainerMarks(wait, null, null).BlockedClass);
        Assert.Contains(wait.CorrelationId, await log.OpenWorkflowsAmongAsync([wait.CorrelationId], Ct));

        var (status, body) = await SendAsync(HttpMethod.Put, $"/api/teams/{_team}/members/Inbox/plugin-settings",
            new { config = new { }, connections = new Dictionary<string, string> { ["mail"] = other } });
        Assert.True(status == HttpStatusCode.OK, body);

        // Neither Needs you nor stopped without finishing, and the wait the removal opened is closed.
        var snapshot = runtime.Snapshot();
        Assert.Null(snapshot.NeedsPerson);
        Assert.Null(snapshot.Blocked);
        Assert.Null(runtime.Marks().Blocked);
        Assert.Empty(await log.OpenWorkflowsAmongAsync([wait.CorrelationId], Ct));

        Assert.Equal(MessageTypes.Completed, (await TellAndAwaitAsync("Inbox")).Type);
    }

    [Fact]
    public async Task A_required_slot_unbound_reads_needs_you_and_new_connection_from_the_slot_assigns_and_binds_in_one_go_telling_the_manager()
    {
        var hired = await _person.PostAsJsonAsync($"/api/teams/{_team}/containers", new { name = "Inbox", agent = "plugin:mailer" }, Ct);
        Assert.True(hired.IsSuccessStatusCode, await hired.Content.ReadAsStringAsync(Ct));

        var host = Services.GetRequiredService<Harness.Containers.ContainerHost>();
        Assert.Contains("bind a connection for the slot `mail`", host.Find(new ContainerId(_team, "Inbox"))!.Snapshot().NeedsPerson);

        var failed = await TellAndAwaitAsync("Inbox");
        Assert.Equal(MessageTypes.Failed, failed.Type);

        // New connection... from the slot: made, then saved on the member - one request assigns and binds.
        var id = await ConnectAsync();
        var (status, body) = await SendAsync(HttpMethod.Put, $"/api/teams/{_team}/members/Inbox/plugin-settings",
            new { config = new { }, connections = new Dictionary<string, string> { ["mail"] = id } });
        Assert.True(status == HttpStatusCode.OK, body);

        Assert.Equal(id, Assert.Single((await GetAsync($"/api/teams/{_team}/connections")).GetProperty("connections").EnumerateArray())
            .GetProperty("connection").GetProperty("id").GetString());
        Assert.Equal("member binding", JsonDocument.Parse(Assert.Single(await TenantDetailsAsync(TenantActions.TeamConnectionAssigned))).RootElement.GetProperty("via").GetString());
        // Cleared at once, the run's needs-person failed mark with it: no next run is needed.
        var ready = host.Find(new ContainerId(_team, "Inbox"))!.Snapshot();
        Assert.Null(ready.NeedsPerson);
        Assert.Null(ready.Failed);
        Assert.Null(ready.FailureClass);

        var manager = MessageTypes.InstructionFor(new ContainerId(_team, TeamRegistry.DefaultManagerName));
        var notices = (await Services.GetRequiredService<IMessageLog>().ReadAfterAsync(0, [manager], int.MaxValue, Ct))
            .Where(m => m.Payload.Contains(PluginReadyNotice.ReadyField, StringComparison.Ordinal));
        Assert.Equal(failed.CorrelationId, Assert.Single(notices).CorrelationId);

        Assert.Equal(MessageTypes.Completed, (await TellAndAwaitAsync("Inbox")).Type);
    }

    [Fact]
    public async Task A_clone_runs_its_plugin_members_with_the_originals_connections_and_a_deleted_team_takes_its_assignments()
    {
        var id = await ConnectAsync();
        await HireAsync("Inbox", id);

        var (status, body) = await SendAsync(HttpMethod.Post, $"/api/teams/{_team}/clone", new { name = "Mail copy" });
        Assert.True(status == HttpStatusCode.Created, body);
        var clone = JsonDocument.Parse(body).RootElement.GetProperty("team").GetProperty("id").GetString()!;

        Assert.Equal(id, Assert.Single((await GetAsync($"/api/teams/{clone}/connections")).GetProperty("connections").EnumerateArray())
            .GetProperty("connection").GetProperty("id").GetString());
        Assert.Contains(await TenantDetailsAsync(TenantActions.TeamConnectionAssigned), d => d.Contains("\"clone\""));
        Assert.Equal(MessageTypes.Completed, (await TellAndAwaitAsync("Inbox", clone)).Type);

        // Deleted: its assignments go with it; the connection and the source's stay.
        var deleted = await _person.DeleteAsync($"/api/teams/{clone}", Ct);
        Assert.True(deleted.IsSuccessStatusCode, await deleted.Content.ReadAsStringAsync(Ct));
        Assert.DoesNotContain(clone, (await Services.GetRequiredService<IConnectionStore>().AllAssignmentsAsync(Ct))[id], StringComparer.OrdinalIgnoreCase);
        Assert.Single((await GetAsync($"/api/teams/{_team}/connections")).GetProperty("connections").EnumerateArray());
        Assert.Single((await GetAsync("/api/connections")).EnumerateArray());
    }
}
