using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// OUTCOMES INSIDE RUNS, on the real Host with a fake agent: the gate (<c>outcomes.requireForCompletion</c>,
/// off by default) refuses an agent's declaration of a workflow with no outcome and never a person's
/// close or the platform's own declaration; a Manager's run context names its workflow's outcome or
/// nudges it to choose one; and a Manager's <c>set</c> with no causation links the run it is in.
/// </summary>
public sealed class OutcomeRunTests : IAsyncLifetime
{
    private const string Email = "person@example.test";
    private const string Password = "correct horse battery";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-outcome-runs-{Guid.NewGuid():N}");
    private readonly FakeAgent _agents = new();
    private readonly ConcurrentQueue<(long Correlation, HttpStatusCode Status, string Body)> _declarations = new();
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _person = null!;
    private string _team = "";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IServiceProvider Services => _factory.Services;

    private IMessageLog Log => Services.GetRequiredService<IMessageLog>();

    private IOutcomeStore Outcomes => Services.GetRequiredService<IOutcomeStore>();

    private ContainerId Dev => new(_team, "Dev");

    private ContainerId Echo => new(_team, "Echo");

    private ContainerId Manager => new(_team, TeamRegistry.DefaultManagerName);

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);
        PluginMemberEndToEndTests.InstallSampleEcho(_dataRoot);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services => services.AddSingleton<IAgentRunner>(_agents)));

        _team = (await Services.GetRequiredService<TeamRegistry>()
            .CreateAsync("Gated", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;

        await Services.GetRequiredService<IUserStore>().CreateAsync(Email, Password);
        _person = _factory.CreateClient();
        (await _person.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password }, Ct)).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.OK, (await _person.PostAsJsonAsync(
            $"/api/teams/{_team}/containers", new { name = "Dev", agent = "claude-headless" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _person.PostAsJsonAsync($"/api/teams/{_team}/containers", new
        {
            name = "Echo", agent = "plugin:sample-echo", config = new { mode = "upper" },
        }, Ct)).StatusCode);

        // Every agent run declares the workflow it is in; the Manager only reads.
        _agents.Behaviour = async invocation =>
        {
            if (invocation.Container == Dev) await DeclareAsync(invocation);
            return new AgentResult(0, "done");
        };
    }

    public async ValueTask DisposeAsync()
    {
        _person.Dispose();
        await _factory.DisposeAsync();
        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public async Task With_the_gate_off_an_agents_declaration_of_a_workflow_with_no_outcome_proceeds()
    {
        Assert.False(Services.GetRequiredService<TenantSettings>().OutcomesRequireForCompletion);

        var workflow = await TellAsync(Dev, "do it");
        var (_, status, body) = await DeclarationAsync(workflow);

        Assert.Equal(HttpStatusCode.NoContent, status);
        Assert.Equal("", body);
        Assert.Empty(await Log.OpenWorkflowsAmongAsync([workflow], Ct));
    }

    [Fact]
    public async Task With_the_gate_on_an_agents_declaration_is_refused_naming_the_outcome_tool_and_a_persons_close_and_the_platforms_declaration_proceed()
    {
        var on = await _person.PutAsJsonAsync("/api/tenant/settings", new Dictionary<string, object>
        {
            [TenantSettings.OutcomesRequireForCompletionName] = "on",
        }, Ct);
        Assert.True(on.IsSuccessStatusCode, await on.Content.ReadAsStringAsync(Ct));
        Assert.True(Services.GetRequiredService<TenantSettings>().OutcomesRequireForCompletion);

        // AN AGENT'S DECLARATION, NO OUTCOME: 409 with the sentence, and the workflow stays open.
        var refused = await TellAsync(Dev, "do it");
        var (_, status, body) = await DeclarationAsync(refused);
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal(OutcomeGate.Refusal, JsonDocument.Parse(body).RootElement.GetProperty("error").GetString());
        Assert.Contains("`outcome`", OutcomeGate.Refusal, StringComparison.Ordinal);
        await SettleAsync();
        Assert.Contains(refused, await Log.OpenWorkflowsAmongAsync([refused], Ct));

        // A PERSON'S CLOSE OF THAT SAME WORKFLOW PROCEEDS.
        var closed = await _person.PostAsJsonAsync($"/api/teams/{_team}/workflows/{refused}/close", new { reason = "not needed" }, Ct);
        Assert.True(closed.IsSuccessStatusCode, await closed.Content.ReadAsStringAsync(Ct));
        Assert.Empty(await Log.OpenWorkflowsAmongAsync([refused], Ct));

        // THE PLATFORM'S OWN DECLARATION PROCEEDS: a person tells the plugin directly.
        var plugin = await TellAsync(Echo, "abc");
        var declared = await WorkflowCompletedAsync(plugin);
        Assert.True(JsonDocument.Parse(declared.Payload).RootElement.GetProperty(UndeclarableWorkflows.DeclaredByPlatformField).GetBoolean());

        // AN AGENT'S DECLARATION WITH AN OUTCOME PROCEEDS.
        var outcome = (await Outcomes.CreateAsync("Gate passes", new OutcomeEdit(), new OutcomeActor(Email, OutcomeActorKind.Person),
            new TriggerAudit(null, Email, TenantActions.OutcomeCreated, null, null, null), Ct)).Outcome!;
        var linked = await TellAsync(Dev, "do it again", outcome.Id);
        Assert.Equal(HttpStatusCode.NoContent, (await DeclarationAsync(linked)).Status);
    }

    [Fact]
    public async Task A_managers_run_context_nudges_it_when_the_workflow_has_no_outcome_and_names_the_outcome_when_it_has_one()
    {
        var unlinked = await TellAsync(Manager, "look");
        var nudged = await InvocationAsync(Manager, unlinked);
        Assert.Contains(OutcomeNudge.NoOutcome, nudged.Context, StringComparison.Ordinal);
        Assert.Equal("This workflow has no outcome. Choose one with `outcome` list/set, or propose one.", OutcomeNudge.NoOutcome);

        var outcome = (await Outcomes.CreateAsync("Maintain a current pipeline of qualified job openings", new OutcomeEdit(),
            new OutcomeActor(Email, OutcomeActorKind.Person),
            new TriggerAudit(null, Email, TenantActions.OutcomeCreated, null, null, null), Ct)).Outcome!;
        var linked = await TellAsync(Manager, "look again", outcome.Name);
        var named = await InvocationAsync(Manager, linked);
        Assert.Contains(OutcomeNudge.Serves(outcome), named.Context, StringComparison.Ordinal);
        Assert.DoesNotContain(OutcomeNudge.NoOutcome, named.Context, StringComparison.Ordinal);

        // A MEMBER'S CONTEXT CARRIES NO SUCH LINE.
        var member = await InvocationAsync(Dev, await TellAsync(Dev, "work"));
        Assert.DoesNotContain("outcome", member.Context, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_managers_set_with_no_causation_links_the_workflow_its_run_is_in()
    {
        var outcome = (await Outcomes.CreateAsync("Links the run", new OutcomeEdit(), new OutcomeActor(Email, OutcomeActorKind.Person),
            new TriggerAudit(null, Email, TenantActions.OutcomeCreated, null, null, null), Ct)).Outcome!;
        var answers = new ConcurrentQueue<string>();

        _agents.Behaviour = async invocation =>
        {
            if (invocation.Container == Manager)
            {
                answers.Enqueue(await ToolsFor(invocation).Outcome("set", outcome: outcome.Name, cancellationToken: Ct));
            }

            return new AgentResult(0, "done");
        };

        var workflow = await TellAsync(Manager, "choose an outcome");
        await InvocationAsync(Manager, workflow);
        await SettleAsync();

        // Every run it made in that workflow linked it (a later wake finds its own link, an agent's, and may move it).
        Assert.NotEmpty(answers);
        Assert.All(answers, answer => Assert.StartsWith("HTTP 200", answer, StringComparison.Ordinal));
        var link = (await Outcomes.CurrentLinkAsync(workflow, Ct))!;
        Assert.Equal((outcome.Id, OutcomeLinkHow.Manager, Manager.ToString()), (link.OutcomeId, link.How, link.SetBy));
    }

    // ---- helpers ----

    private PlatformMcpTools ToolsFor(AgentInvocation invocation)
    {
        var context = new DefaultHttpContext { RequestServices = Services };
        context.Request.Headers[ApiKeyAuthenticationHandler.Header] = invocation.Environment["HARNESS_KEY"];
        context.User = PrincipalClaims.ToClaimsPrincipal(
            new Principal(invocation.Container.ToString(), PrincipalKind.Container, TeamRegistry.ManagerPermits), "test");

        return new PlatformMcpTools(
            new HttpContextAccessor { HttpContext = context },
            Services.GetRequiredService<IPrincipalStore>(),
            Services.GetRequiredService<AgentCatalog>(),
            new ServerClients(_factory.Server.CreateHandler()));
    }

    private async Task<long> TellAsync(ContainerId who, string instruction, string? outcome = null)
    {
        var told = await _person.PostAsJsonAsync($"/api/teams/{_team}/containers/{who.Name}/tell", new { instruction, outcome }, Ct);
        Assert.True(told.IsSuccessStatusCode, await told.Content.ReadAsStringAsync(Ct));
        return JsonDocument.Parse(await told.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("correlationId").GetInt64();
    }

    private async Task DeclareAsync(AgentInvocation invocation)
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.Header, invocation.Environment["HARNESS_KEY"]);
        var correlation = Services.GetRequiredService<ContainerHost>().Find(invocation.Container)!.Snapshot().CurrentCorrelation ?? 0;

        var response = await client.PostAsJsonAsync(
            $"/api/teams/{_team}/containers/{invocation.Container.Name}/workflow-complete", new { delivered = "done" }, Ct);
        _declarations.Enqueue((correlation, response.StatusCode, await response.Content.ReadAsStringAsync(Ct)));
    }

    private async Task<(long Correlation, HttpStatusCode Status, string Body)> DeclarationAsync(long correlation)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (DateTime.UtcNow < deadline)
        {
            if (_declarations.FirstOrDefault(d => d.Correlation == correlation) is { Correlation: > 0 } found) return found;
            await Task.Delay(50, Ct);
        }

        throw new TimeoutException($"Workflow {correlation} was never declared.");
    }

    private async Task<AgentInvocation> InvocationAsync(ContainerId who, long correlation)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (DateTime.UtcNow < deadline)
        {
            foreach (var invocation in _agents.Invocations.Where(i => i.Container == who))
            {
                if (invocation.Environment.TryGetValue("HARNESS_CAUSATION", out var seq)
                    && await Log.FindAsync(long.Parse(seq, System.Globalization.CultureInfo.InvariantCulture), Ct) is { } row
                    && row.CorrelationId == correlation)
                {
                    return invocation;
                }
            }

            await Task.Delay(50, Ct);
        }

        throw new TimeoutException($"{who} never ran in workflow {correlation}.");
    }

    private async Task<Message> WorkflowCompletedAsync(long correlation)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (DateTime.UtcNow < deadline)
        {
            if ((await Log.ReadCorrelationAsync(correlation, Ct)).FirstOrDefault(m => m.Type == MessageTypes.WorkflowCompleted) is { } row)
            {
                return row;
            }

            await Task.Delay(50, Ct);
        }

        throw new TimeoutException($"Workflow {correlation} was never declared.");
    }

    private async Task SettleAsync()
    {
        var host = Services.GetRequiredService<ContainerHost>();
        var pending = Services.GetRequiredService<IPendingDeliveries>();
        var deadline = DateTime.UtcNow.AddSeconds(15);

        for (var quiet = 0; quiet < 10 && DateTime.UtcNow < deadline;)
        {
            var idle = (await pending.ForTeamAsync(_team, Ct)).Count == 0
                && new[] { Manager, Dev, Echo }.All(id => host.Find(id)!.State == ContainerState.Idle);
            quiet = idle ? quiet + 1 : 0;
            await Task.Delay(100, Ct);
        }
    }

    private sealed class ServerClients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("http://localhost") };
    }
}
