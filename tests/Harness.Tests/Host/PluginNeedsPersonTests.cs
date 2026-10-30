using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// A PLUGIN MEMBER A MANAGER COULD NOT FINISH SETTING UP WAITS FOR A PERSON. A Manager's hire of a
/// plugin whose required key no person has bound on the team is made, and its answer says what a
/// person must set; each run of the member fails <c>needs-person</c> naming it, without running;
/// and a person's save that leaves nothing missing tells the Manager inside that workflow, once,
/// to send the instruction again. A person's own hire is still refused an unbound required key,
/// and a Manager still binds no key a person has not bound on its team.
/// </summary>
public sealed class PluginNeedsPersonTests : IAsyncLifetime
{
    private const string Email = "person@example.test";
    private const string Password = "correct horse battery";

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-needs-person-{Guid.NewGuid():N}");
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _person = null!;
    private string _team = "";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IServiceProvider Services => _factory.Services;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);
        PluginInstall.Write(_dataRoot, "keyed",
            script: "cat > /dev/null; echo '{\"t\":\"result\",\"ok\":true,\"output\":\"read\"}'",
            manifest: PluginInstall.Manifest("keyed", edit: m => m["secrets"] = JsonNode.Parse("""{"token":{"description":"The service's token.","required":true}}""")));

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services => services.AddSingleton<IAgentRunner>(new FakeAgent())));

        _team = (await Services.GetRequiredService<TeamRegistry>()
            .CreateAsync("Waits", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;

        await Services.GetRequiredService<IUserStore>().CreateAsync(Email, Password);
        _person = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        (await _person.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password }, Ct)).EnsureSuccessStatusCode();
    }

    public async ValueTask DisposeAsync()
    {
        _person.Dispose();
        await _factory.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public async Task A_managers_hire_with_a_required_key_nobody_bound_is_made_and_says_what_a_person_must_set()
    {
        var hired = await ManagerTools(await ManagerKeyAsync()).Hire("Keyed", plugin: "keyed", cancellationToken: Ct);

        Assert.StartsWith("HTTP 200", hired);
        Assert.Contains("waitingForPerson", hired);
        Assert.Contains("set the secret `token` in the member's settings", hired);
        Assert.Contains("fails needs-person", hired);
        Assert.NotNull(Services.GetRequiredService<Harness.Containers.ContainerHost>().Find(new ContainerId(_team, "Keyed")));

        // A Manager still binds no key a person has not bound on its team.
        var named = await ManagerTools(await ManagerKeyAsync()).Hire(
            "Keyed2", plugin: "keyed", secrets: new() { ["token"] = "NOBODY_BOUND_THIS_KEY" }, cancellationToken: Ct);
        Assert.StartsWith("HTTP 400", named);
        Assert.Contains("not one a person has bound on this team", named);
    }

    [Fact]
    public async Task A_persons_own_hire_is_still_refused_an_unbound_required_key()
    {
        var refused = await _person.PostAsJsonAsync($"/api/teams/{_team}/containers", new { name = "Mine", agent = "plugin:keyed" }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("requires the secret `token`", await refused.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_run_waiting_for_a_person_fails_needs_person_and_a_persons_save_tells_the_manager_in_that_workflow_once()
    {
        Assert.StartsWith("HTTP 200", await ManagerTools(await ManagerKeyAsync()).Hire("Keyed", plugin: "keyed", cancellationToken: Ct));

        var failed = await TellAndAwaitAsync("Keyed");
        Assert.Equal(MessageTypes.Failed, failed.Type);
        using (var payload = JsonDocument.Parse(failed.Payload))
        {
            Assert.Equal(FailureClasses.NeedsPerson, payload.RootElement.GetProperty(PayloadFields.FailureClass).GetString());
        }
        Assert.Contains("token", failed.Payload);

        // THE MANAGER'S WOKEN RUN ENDS FIRST, as it does whenever a person takes longer than an agent:
        // the workflow is the plugin's, which cannot declare, and must stay open for the notice.
        await ManagerRanAsync(failed);
        Assert.Contains(failed.CorrelationId, await Services.GetRequiredService<IMessageLog>().OpenWorkflowsAmongAsync([failed.CorrelationId], Ct));

        // AND THE WAIT IS NOT AN ERROR on the workflow either: the screen's tab and tile read this word.
        await AssertWaitingNotFailedAsync(failed.CorrelationId);

        // The person sets a key and binds it on the member, from the screen.
        var key = $"NEEDS_PERSON_TEST_{Guid.NewGuid():N}".ToUpperInvariant();
        Assert.Equal(HttpStatusCode.OK, (await _person.PutAsJsonAsync($"/api/secrets/{key}", new { value = $"value-{Guid.NewGuid():N}" }, Ct)).StatusCode);
        var saved = await _person.PutAsJsonAsync(
            $"/api/teams/{_team}/members/Keyed/plugin-settings", new { config = new { }, secrets = new Dictionary<string, string> { ["token"] = key } }, Ct);
        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync(Ct));

        var notices = await NoticesAsync();
        var notice = Assert.Single(notices);
        Assert.Equal(failed.CorrelationId, notice.CorrelationId);
        Assert.Equal(failed.Seq, notice.CausationSeq);
        Assert.Contains("Keyed is ready now", notice.Payload);
        Assert.Contains("send it the same instruction again", notice.Payload);
        await AssertWaitingNotFailedAsync(failed.CorrelationId);

        // A second save says nothing more: the workflow was already told.
        var again = await _person.PutAsJsonAsync(
            $"/api/teams/{_team}/members/Keyed/plugin-settings", new { config = new { }, secrets = new Dictionary<string, string> { ["token"] = key } }, Ct);
        Assert.True(again.IsSuccessStatusCode);
        Assert.Single(await NoticesAsync());

        // And the member now runs.
        Assert.Equal(MessageTypes.Completed, (await TellAndAwaitAsync("Keyed")).Type);
    }

    [Fact]
    public async Task An_agents_hire_cannot_carry_a_value_and_a_persons_typed_value_tells_the_manager_once()
    {
        // A Manager sending a value with its hire is refused, and nothing is made.
        using var manager = _factory.CreateClient();
        manager.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.Header, await ManagerKeyAsync());
        var steered = await manager.PostAsJsonAsync($"/api/teams/{_team}/containers", new
        {
            name = "Keyed",
            agent = "plugin:keyed",
            secretValues = new Dictionary<string, string> { ["token"] = $"value-{Guid.NewGuid():N}" },
        }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, steered.StatusCode);
        Assert.Contains("An agent's hire cannot carry a secret value", await steered.Content.ReadAsStringAsync(Ct));
        Assert.Null(Services.GetRequiredService<Harness.Containers.ContainerHost>().Find(new ContainerId(_team, "Keyed")));

        // Without one the member is made and waits for a person.
        var hired = await ManagerTools(await ManagerKeyAsync()).Hire("Keyed", plugin: "keyed", cancellationToken: Ct);
        Assert.Contains("set the secret `token` in the member's settings", hired);
        var failed = await TellAndAwaitAsync("Keyed");
        Assert.Equal(MessageTypes.Failed, failed.Type);

        // A person types the member's own value: the Manager is told once, in that workflow.
        var typed = await _person.PutAsJsonAsync($"/api/teams/{_team}/members/Keyed/secrets/token", new { value = $"value-{Guid.NewGuid():N}" }, Ct);
        Assert.True(typed.IsSuccessStatusCode, await typed.Content.ReadAsStringAsync(Ct));
        var notice = Assert.Single(await NoticesAsync());
        Assert.Equal(failed.CorrelationId, notice.CorrelationId);

        // Replacing it says nothing more, and the member now runs.
        Assert.True((await _person.PutAsJsonAsync($"/api/teams/{_team}/members/Keyed/secrets/token", new { value = $"value-{Guid.NewGuid():N}" }, Ct)).IsSuccessStatusCode);
        Assert.Single(await NoticesAsync());
        Assert.Equal(MessageTypes.Completed, (await TellAndAwaitAsync("Keyed")).Type);
    }

    [Fact]
    public async Task A_persons_settings_save_with_a_typed_value_tells_the_manager_once_after_an_agents_hire_made_the_member_wait()
    {
        var hired = await ManagerTools(await ManagerKeyAsync()).Hire("Saved", plugin: "keyed", cancellationToken: Ct);
        Assert.Contains("set the secret `token` in the member's settings", hired);
        var failed = await TellAndAwaitAsync("Saved");
        Assert.Equal(MessageTypes.Failed, failed.Type);

        // The person's Save carries the member's own value with its settings: the Manager is told once, in that workflow.
        var saved = await _person.PutAsJsonAsync($"/api/teams/{_team}/members/Saved/plugin-settings", new
        {
            config = new { },
            secretValues = new Dictionary<string, string?> { ["token"] = $"value-{Guid.NewGuid():N}" },
        }, Ct);
        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync(Ct));
        var notice = Assert.Single(await NoticesAsync());
        Assert.Equal(failed.CorrelationId, notice.CorrelationId);

        // A second Save replacing it says nothing more, and the member now runs.
        Assert.True((await _person.PutAsJsonAsync($"/api/teams/{_team}/members/Saved/plugin-settings", new
        {
            config = new { },
            secretValues = new Dictionary<string, string?> { ["token"] = $"value-{Guid.NewGuid():N}" },
        }, Ct)).IsSuccessStatusCode);
        Assert.Single(await NoticesAsync());
        Assert.Equal(MessageTypes.Completed, (await TellAndAwaitAsync("Saved")).Type);
    }

    [Fact]
    public void Needs_person_is_a_class_of_its_own_never_resumed_and_the_manager_is_told_what_to_do_with_it()
    {
        Assert.Contains(FailureClasses.NeedsPerson, FailureClasses.All);
        Assert.False(FailureClasses.ResumesAutomatically(FailureClasses.NeedsPerson));
        Assert.NotNull(FailureClasses.Sentence(FailureClasses.NeedsPerson));

        var prompt = BuiltInPrompts.For(SkillRoles.Manager);
        Assert.Contains("`[needs-person]`", prompt);
        Assert.Contains("Report it to the person once with `blocked`", prompt);
        Assert.Contains("send the instruction again", prompt);
    }

    /// <summary>Waits until the Manager the failure woke has run and is idle again.</summary>
    private async Task ManagerRanAsync(Message failed)
    {
        var manager = new ContainerId(_team, TeamRegistry.DefaultManagerName);
        var log = Services.GetRequiredService<IMessageLog>();
        var host = Services.GetRequiredService<Harness.Containers.ContainerHost>();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var ended = (await log.ReadAfterAsync(failed.Seq, [MessageTypes.Completed, MessageTypes.Failed], int.MaxValue, Ct))
                .Any(m => m.Source == manager.ToString() && m.CorrelationId == failed.CorrelationId);
            if (ended && host.Find(manager)!.State == ContainerState.Idle) return;
            await Task.Delay(50, Ct);
        }

        Assert.Fail("The Manager was not woken by the needs-person failure, or its run did not end.");
    }

    /// <summary>
    /// The workflow as the screen reads it: open, Awaiting the plugin member, with no failed member and
    /// no failed run counted.
    /// </summary>
    private async Task AssertWaitingNotFailedAsync(long correlation)
    {
        var workflows = await _person.GetFromJsonAsync<JsonObject>($"/api/teams/{_team}/workflows", Ct);
        var workflow = workflows!["workflows"]!.AsArray().Single(w => (long)w!["correlation"]! == correlation)!;
        Assert.Equal("Awaiting", (string?)workflow["state"]);
        Assert.Empty(workflow["failedMembers"]!.AsArray());
        Assert.Equal(0, (int)workflow["runsFailed"]!);
        Assert.Null(workflow["endedAt"]);
    }

    private async Task<IReadOnlyList<Message>> NoticesAsync()
    {
        var manager = MessageTypes.InstructionFor(new ContainerId(_team, TeamRegistry.DefaultManagerName));
        var rows = await Services.GetRequiredService<IMessageLog>().ReadAfterAsync(0, [manager], int.MaxValue, Ct);
        return [.. rows.Where(m => m.Payload.Contains(PluginReadyNotice.ReadyField, StringComparison.Ordinal))];
    }

    private string ManagerId => new ContainerId(_team, TeamRegistry.DefaultManagerName).ToString();

    private Task<string> ManagerKeyAsync() =>
        Services.GetRequiredService<IPrincipalStore>().MintAsync(
            ManagerId, PrincipalKind.Container, _team, TeamRegistry.ManagerPermits, ct: Ct);

    private sealed class ServerClients(WebApplicationFactory<Program> factory) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(factory.Server.CreateHandler(), disposeHandler: true) { BaseAddress = factory.Server.BaseAddress };
    }

    /// <summary>The MCP tools as the team's Manager holds them. SYNCHRONOUS on purpose: the accessor's
    /// context is an AsyncLocal, and one set inside an async helper does not flow back to its caller.</summary>
    private PlatformMcpTools ManagerTools(string key)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[ApiKeyAuthenticationHandler.Header] = key;
        context.User = PrincipalClaims.ToClaimsPrincipal(
            new Principal(ManagerId, PrincipalKind.Container, TeamRegistry.ManagerPermits.ToHashSet()), "test");

        return new PlatformMcpTools(
            new HttpContextAccessor { HttpContext = context }, Services.GetRequiredService<IPrincipalStore>(),
            Services.GetRequiredService<AgentCatalog>(), new ServerClients(_factory));
    }

    private async Task<Message> TellAndAwaitAsync(string member)
    {
        var who = new ContainerId(_team, member);
        var log = Services.GetRequiredService<IMessageLog>();

        async Task<IReadOnlyList<Message>> Rows() =>
            [.. (await log.ReadAfterAsync(0, [MessageTypes.Completed, MessageTypes.Failed], int.MaxValue, Ct)).Where(m => m.Source == who.ToString())];

        var after = (await Rows()).Select(m => m.Seq).DefaultIfEmpty(0).Max();

        var told = await _person.PostAsJsonAsync($"/api/teams/{_team}/containers/{member}/tell", new { instruction = "read" }, Ct);
        Assert.True(told.IsSuccessStatusCode, await told.Content.ReadAsStringAsync(Ct));

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if ((await Rows()).FirstOrDefault(m => m.Seq > after) is { } row)
            {
                var host = Services.GetRequiredService<Harness.Containers.ContainerHost>();
                while (host.Find(who)!.State != ContainerState.Idle && DateTime.UtcNow < deadline) await Task.Delay(20, Ct);
                return row;
            }

            await Task.Delay(50, Ct);
        }

        throw new TimeoutException($"{who} wrote no terminal row.");
    }
}
