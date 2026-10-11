using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// Independent probes from verification: a trigger link and a dispatch link the Manager may not move,
/// a merge chain, a person's own tell link the Manager may not move, and the tenant row a person's
/// tell that names an outcome appends.
/// </summary>
public sealed class OutcomeVerificationTests(HostFixture host) : IClassFixture<HostFixture>
{
    private const string Email = "person@example.test";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IOutcomeStore Outcomes => host.Services.GetRequiredService<IOutcomeStore>();

    private static string Unique(string stem) => $"{stem} {Guid.NewGuid():N}";

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();

    private async Task<Outcome> CreateAsync(HttpClient person, string name)
    {
        var created = await person.PostAsJsonAsync("/api/outcomes", new { name }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await Outcomes.FindAsync((await JsonAsync(created)).GetProperty("id").GetString()!, Ct))!;
    }

    private async Task<long> WorkflowAsync(HttpClient person, string team, string? outcome = null)
    {
        var told = await person.PostAsJsonAsync(
            $"/api/teams/{team}/containers/{TeamRegistry.DefaultManagerName}/tell",
            new { instruction = "look at the openings", outcome }, Ct);
        Assert.True(told.IsSuccessStatusCode, await told.Content.ReadAsStringAsync(Ct));
        return (await JsonAsync(told)).GetProperty("correlationId").GetInt64();
    }

    private PlatformMcpTools ManagerTools(string team)
    {
        var id = new ContainerId(team, TeamRegistry.DefaultManagerName).ToString();
        var key = host.Services.GetRequiredService<IPrincipalStore>()
            .MintAsync(id, PrincipalKind.Container, team, TeamRegistry.ManagerPermits, ct: Ct).GetAwaiter().GetResult();
        var context = new DefaultHttpContext();
        context.Request.Headers[ApiKeyAuthenticationHandler.Header] = key;
        context.User = PrincipalClaims.ToClaimsPrincipal(new Principal(id, PrincipalKind.Container, TeamRegistry.ManagerPermits, null), "test");

        return new PlatformMcpTools(
            new HttpContextAccessor { HttpContext = context },
            host.Services.GetRequiredService<IPrincipalStore>(),
            host.Services.GetRequiredService<AgentCatalog>(),
            new ServerClients(host.ServerHandler()));
    }

    [Fact]
    public async Task A_managers_set_is_refused_on_a_trigger_linked_workflow()
    {
        var person = await host.PersonAsync();
        var outcome = await CreateAsync(person, Unique("Trigger outcome"));
        var other = await CreateAsync(person, Unique("Other"));

        var trigger = await JsonAsync(await person.PostAsJsonAsync($"/api/teams/{host.Alpha}/triggers", new
        {
            name = Unique("crawl"), kind = "every", intervalSeconds = 3600, instruction = "crawl", idleOnly = false,
            outcomeId = outcome.Id,
        }, Ct));
        var ran = await JsonAsync(await person.PostAsync($"/api/teams/{host.Alpha}/triggers/{trigger.GetProperty("id").GetString()}/run", null, Ct));
        var fired = (await host.Services.GetRequiredService<IMessageLog>().FindAsync(ran.GetProperty("seq").GetInt64(), Ct))!;
        Assert.Equal(OutcomeLinkHow.Trigger, (await Outcomes.CurrentLinkAsync(fired.CorrelationId, Ct))!.How);

        var refused = await ManagerTools(host.Alpha).Outcome("set", outcome: other.Id, causation: fired.CorrelationId.ToString(), cancellationToken: Ct);
        Assert.StartsWith("Refused: the outcome tool's set: ", refused, StringComparison.Ordinal);
        Assert.Equal(outcome.Id, (await Outcomes.CurrentLinkAsync(fired.CorrelationId, Ct))!.OutcomeId);
    }

    [Fact]
    public async Task A_propose_on_a_dispatch_linked_workflow_is_refused_and_creates_nothing()
    {
        var person = await host.PersonAsync();
        var outcome = await CreateAsync(person, Unique("Dispatch outcome"));
        var item = (await JsonAsync(await person.PostAsJsonAsync("/api/backlog", new { title = "t", body = "b" }, Ct))).GetProperty("id").GetInt64();
        await person.PatchAsJsonAsync($"/api/backlog/{item}", new { state = "ready", outcomeId = outcome.Id }, Ct);
        var dispatched = (await JsonAsync(await person.PostAsync($"/api/teams/{host.Alpha}/backlog/{item}/dispatch", null, Ct)))
            .GetProperty("correlation").GetInt64();

        var name = Unique("Never created");
        var refused = await ManagerTools(host.Alpha).Outcome("propose", name: name, causation: dispatched.ToString(), cancellationToken: Ct);
        Assert.StartsWith("Refused: the outcome tool's propose: ", refused, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/", refused, StringComparison.Ordinal);
        Assert.Null(await Outcomes.FindLiveByNameAsync(name, Ct));
        Assert.Equal(outcome.Id, (await Outcomes.CurrentLinkAsync(dispatched, Ct))!.OutcomeId);
    }

    [Fact]
    public async Task A_merge_chain_moves_the_figures_to_the_last_outcome()
    {
        var person = await host.PersonAsync();
        var a = await CreateAsync(person, Unique("A"));
        var b = await CreateAsync(person, Unique("B"));
        var c = await CreateAsync(person, Unique("C"));
        var workflow = await WorkflowAsync(person, host.Alpha, a.Id);

        Assert.Equal(HttpStatusCode.OK, (await person.PostAsJsonAsync($"/api/outcomes/{a.Id}/merge", new { into = b.Id }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await person.PostAsJsonAsync($"/api/outcomes/{b.Id}/merge", new { into = c.Id }, Ct)).StatusCode);

        var detail = await JsonAsync(await person.GetAsync($"/api/outcomes/{a.Id}", Ct));
        Assert.Equal(c.Id, detail.GetProperty("resolvedTo").GetString());
        var list = await JsonAsync(await person.GetAsync("/api/outcomes", Ct));
        var target = list.GetProperty("outcomes").EnumerateArray().Single(o => o.GetProperty("id").GetString() == c.Id);
        Assert.Equal(1, target.GetProperty("figures").GetProperty("workflows").GetProperty("total").GetInt32());
        Assert.Equal(a.Id, (await Outcomes.CurrentLinkAsync(workflow, Ct))!.OutcomeId);

        // Merging back into a merged outcome is refused: no cycle.
        Assert.Equal(HttpStatusCode.Conflict, (await person.PostAsJsonAsync($"/api/outcomes/{c.Id}/merge", new { into = a.Id }, Ct)).StatusCode);
    }

    /// <summary>The tell route writes the link and its tenant row, naming the person, in one transaction.</summary>
    [Fact]
    public async Task A_persons_tell_that_names_an_outcome_appends_its_workflow_outcome_changed_row()
    {
        var person = await host.PersonAsync();
        var outcome = await CreateAsync(person, Unique("Told"));
        await WorkflowAsync(person, host.Alpha, outcome.Id);

        var row = await host.Services.GetRequiredService<ITenantLog>().FindLatestAsync(TenantActions.WorkflowOutcomeChanged, outcome.Id, Ct);
        Assert.NotNull(row);
        Assert.Equal(Email, row.ActorEmail);
        Assert.Equal(outcome.Name, row.SubjectName);
    }

    private sealed class ServerClients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("http://localhost") };
    }
}
