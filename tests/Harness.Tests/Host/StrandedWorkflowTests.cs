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
/// A STRANDED WORKFLOW IS SHOWN, AND ONLY A PERSON CLOSES IT. A backlog item was dispatched on
/// workflow W; W blocked; the same card was then sent again and finished in a later workflow on the
/// same team. The item's row says "the work continued in workflow N" and offers a person the
/// existing close route for W. Reading the row changes nothing: W stays open, and the dispatch stays
/// in flight, until a person closes it.
/// </summary>
public sealed class StrandedWorkflowTests(HostFixture host) : IClassFixture<HostFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Developer = "Developer";

    [Fact]
    public async Task A_blocked_dispatch_whose_card_was_done_in_a_later_workflow_shows_the_notice_and_offers_a_person_the_close()
    {
        var (team, original, item) = await DispatchAsync("Stranded Shown");
        var card = await BlockAsync(team, original);
        var later = await FinishInALaterWorkflowAsync(team, card);

        using var person = await host.PersonAsync();

        // THE LIST AND THE DETAIL SAY THE SAME THING.
        var row = (await person.GetFromJsonAsync<JsonElement>("/api/backlog", Ct))
            .EnumerateArray().Single(r => r.GetProperty("id").GetInt64() == item);
        var detail = (await person.GetFromJsonAsync<JsonElement>($"/api/backlog/{item}", Ct)).GetProperty("item");

        foreach (var shown in new[] { row, detail })
        {
            var stranded = shown.GetProperty("stranded");
            Assert.Equal(team, stranded.GetProperty("teamId").GetString());
            Assert.Equal(original, stranded.GetProperty("workflow").GetInt64());
            Assert.Equal("Blocked", stranded.GetProperty("state").GetString());
            Assert.Equal(later, stranded.GetProperty("continuedIn").GetInt64());
            Assert.Equal(card, Assert.Single(stranded.GetProperty("cards").EnumerateArray()).GetString());
            Assert.Equal($"The work continued in workflow {later}.", stranded.GetProperty("notice").GetString());

            // THE CLOSE IS THE EXISTING PERSON-ONLY ROUTE, offered and not taken.
            var close = stranded.GetProperty("close");
            Assert.Equal($"/api/teams/{team}/workflows/{original}/close", close.GetProperty("route").GetString());
            Assert.Equal("person", close.GetProperty("by").GetString());
            Assert.Contains($"workflow {later}", close.GetProperty("reason").GetString());

            // The dispatch still names its open workflow.
            Assert.Equal(original, shown.GetProperty("inFlight").GetProperty("correlation").GetInt64());
        }

        // NOTHING CHANGED BY READING IT: no close, no declaration, on the original workflow.
        var log = host.Services.GetRequiredService<IMessageLog>();
        Assert.DoesNotContain(
            await log.ReadCorrelationAsync(original, Ct),
            m => m.Type is MessageTypes.WorkflowClosed or MessageTypes.WorkflowCompleted);
        Assert.Contains(original, await log.OpenWorkflowsAmongAsync([original], Ct));

        // A MEMBER IS NOT OFFERED IT: the route stays a person's.
        using var member = host.Container(await KeyAsync(team, TeamRegistry.DefaultManagerName));
        var refused = await member.PostAsJsonAsync($"/api/teams/{team}/workflows/{original}/close", new { reason = "no" }, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains(original, await log.OpenWorkflowsAmongAsync([original], Ct));

        // THE PERSON ACTS: the offered route, with the offered reason. Then the notice is gone.
        var closed = await person.PostAsJsonAsync(
            $"/api/teams/{team}/workflows/{original}/close",
            new { reason = detail.GetProperty("stranded").GetProperty("close").GetProperty("reason").GetString() },
            Ct);
        Assert.Equal(HttpStatusCode.NoContent, closed.StatusCode);

        var after = (await person.GetFromJsonAsync<JsonElement>($"/api/backlog/{item}", Ct)).GetProperty("item");
        Assert.Equal(JsonValueKind.Null, after.GetProperty("stranded").ValueKind);
        Assert.Equal(JsonValueKind.Null, after.GetProperty("inFlight").ValueKind);
    }

    [Fact]
    public async Task A_blocked_dispatch_whose_card_was_not_done_elsewhere_is_not_stranded()
    {
        var (_, _, item) = await DispatchAsync("Stranded Not Yet");
        var (team, original) = (await TeamOfAsync(item), await WorkflowOfAsync(item));
        await BlockAsync(team, original);

        using var person = await host.PersonAsync();
        var detail = (await person.GetFromJsonAsync<JsonElement>($"/api/backlog/{item}", Ct)).GetProperty("item");

        Assert.Equal(JsonValueKind.Null, detail.GetProperty("stranded").ValueKind);
        Assert.Equal(original, detail.GetProperty("inFlight").GetProperty("correlation").GetInt64());
    }

    [Fact]
    public async Task The_team_reading_its_item_is_told_the_open_workflow_and_the_notice()
    {
        var (team, original, item) = await DispatchAsync("Stranded Team Read");

        using var manager = host.Container(await KeyAsync(team, TeamRegistry.DefaultManagerName));

        var open = await manager.GetFromJsonAsync<JsonElement>($"/api/teams/{team}/backlog/{item}", Ct);
        Assert.Equal(original, open.GetProperty("workflow").GetInt64());
        Assert.Equal(JsonValueKind.Null, open.GetProperty("stranded").ValueKind);

        var card = await BlockAsync(team, original);
        var later = await FinishInALaterWorkflowAsync(team, card);

        var stranded = await manager.GetFromJsonAsync<JsonElement>($"/api/teams/{team}/backlog/{item}", Ct);
        Assert.Equal(original, stranded.GetProperty("workflow").GetInt64());
        Assert.Equal(later, stranded.GetProperty("stranded").GetProperty("continuedIn").GetInt64());
    }

    [Fact]
    public async Task The_backlog_tool_lists_the_open_workflow_and_where_the_work_continued()
    {
        var (team, original, item) = await DispatchAsync("Stranded Tool");
        var tools = await ConciergeToolsAsync();

        var open = Row(await tools.Backlog("list", state: "all", cancellationToken: Ct), item);
        Assert.Equal(original, open.GetProperty("workflow").GetInt64());
        Assert.Equal(JsonValueKind.Null, open.GetProperty("continuedIn").ValueKind);

        var later = await FinishInALaterWorkflowAsync(team, await BlockAsync(team, original));

        var stranded = Row(await tools.Backlog("list", state: "all", cancellationToken: Ct), item);
        Assert.Equal(original, stranded.GetProperty("workflow").GetInt64());
        Assert.Equal(later, stranded.GetProperty("continuedIn").GetInt64());
        Assert.Equal($"The work continued in workflow {later}.", stranded.GetProperty("notice").GetString());
    }

    private static JsonElement Row(string answer, long item)
    {
        Assert.StartsWith("HTTP 200", answer);
        return JsonDocument.Parse(answer.Split(Environment.NewLine, 2)[1]).RootElement
            .EnumerateArray().Single(r => r.GetProperty("id").GetInt64() == item);
    }

    /// <summary>The backlog tool as the tenant Concierge calls it - the list is a person's, and the
    /// Concierge acts for one.</summary>
    private async Task<PlatformMcpTools> ConciergeToolsAsync()
    {
        var person = await host.Services.GetRequiredService<IUserStore>().FindAsync("person@example.test", Ct);
        var id = ConciergeLaunchFactory.PrincipalId(person!.Id);
        var key = await host.Services.GetRequiredService<IPrincipalStore>().MintAsync(
            id, PrincipalKind.TenantConcierge, null, ConciergeLaunchFactory.ConciergePermits,
            ownerUserId: person.Id, ct: Ct);

        var context = new DefaultHttpContext();
        context.Request.Headers[ApiKeyAuthenticationHandler.Header] = key;
        context.User = PrincipalClaims.ToClaimsPrincipal(
            new Principal(id, PrincipalKind.TenantConcierge, ConciergeLaunchFactory.ConciergePermits), "test");

        return new PlatformMcpTools(
            new FixedAccessor(context),
            host.Services.GetRequiredService<IPrincipalStore>(),
            host.Services.GetRequiredService<AgentCatalog>(),
            new ServerClients(host.ServerHandler()));
    }

    private sealed class FixedAccessor(HttpContext context) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get => context; set { } }
    }

    private sealed class ServerClients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("http://localhost") };
    }

    /// <summary>A team of its own, a backlog item dispatched to it, and the dispatch's workflow: an
    /// instruction to its Manager.</summary>
    private async Task<(string Team, long Workflow, long Item)> DispatchAsync(string name)
    {
        var registry = host.Services.GetRequiredService<TeamRegistry>();
        var agent = host.Services.GetRequiredService<AgentCatalog>().Definitions.First(d => d.Mode == AgentMode.Headless).Name;
        var team = (await registry.CreateAsync(name, agent, memberAgent: agent)).Id;

        // THE TEAM IS PAUSED, SO NOTHING HERE RUNS. The rows below are written straight to the log
        // while the real pump is delivering, and a row it would deliver - the developer's
        // `completed` wakes the Manager - starts a real run. This Host's agent CLI is the test
        // assembly's stub, so that run fails, and its `failed` row reads the workflow as Failed
        // rather than Blocked, sooner or later depending on load. A paused team takes no slot and
        // runs nothing; its workflows keep the state their rows give them.
        await registry.SetPausedAsync(team, true, Ct);

        // ROOTED AS A REAL DISPATCH IS, by its `backlog.item.dispatched` row, which no member acts on.
        var root = await Log.AppendAsync(new NewMessage(
            MessageTypes.BacklogItemDispatched,
            $$"""{"item":1,"team":"{{team}}","title":"Job tracker"}""", "person@example.test"), Ct);

        var backlog = host.Services.GetRequiredService<IBacklogStore>();
        var item = await backlog.CreateAsync(null, "Job tracker", "Build it.", "person@example.test", Ct);
        await backlog.AddDispatchAsync(item.Id, team, name, root.CorrelationId, "person@example.test", Ct);

        return (team, root.CorrelationId, item.Id);
    }

    /// <summary>The Manager sends the Developer a card in <paramref name="workflow"/>, and the Developer and the Manager
    /// both block on a person. Returns the card's id.</summary>
    private async Task<string> BlockAsync(string team, long workflow)
    {
        var manager = new ContainerId(team, TeamRegistry.DefaultManagerName).ToString();
        var developer = new ContainerId(team, Developer);

        await Log.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(developer), """{"instruction":"Re-check the build."}""", manager,
            workflow), Ct);
        await Log.AppendAsync(new NewMessage(MessageTypes.Started, "{}", developer.ToString(), workflow), Ct);
        await Log.AppendAsync(new NewMessage(MessageTypes.Blocked, """{"reason":"The tests cannot start."}""", developer.ToString(), workflow), Ct);
        await Log.AppendAsync(new NewMessage(MessageTypes.Completed, "{}", developer.ToString(), workflow), Ct);

        await Log.AppendAsync(new NewMessage(MessageTypes.Started, "{}", manager, workflow), Ct);
        await Log.AppendAsync(new NewMessage(MessageTypes.Blocked, """{"reason":"The re-check cannot start."}""", manager, workflow), Ct);
        await Log.AppendAsync(new NewMessage(MessageTypes.Completed, "{}", manager, workflow), Ct);

        return $"{workflow}_{Developer}";
    }

    /// <summary>The same card sent again as a NEW workflow, and done there. Returns that workflow.</summary>
    private async Task<long> FinishInALaterWorkflowAsync(string team, string card)
    {
        var developer = new ContainerId(team, Developer);

        var resent = await Log.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(developer),
            JsonSerializer.Serialize(new { instruction = "Re-check the build.", card }),
            new ContainerId(team, TeamRegistry.DefaultManagerName).ToString()), Ct);
        var later = resent.CorrelationId;

        await Log.AppendAsync(new NewMessage(MessageTypes.Started, "{}", developer.ToString(), later), Ct);
        await Log.AppendAsync(new NewMessage(MessageTypes.Handback, """{"delivered":"Re-checked."}""", developer.ToString(), later), Ct);
        await Log.AppendAsync(new NewMessage(MessageTypes.Completed, "{}", developer.ToString(), later), Ct);

        return later;
    }

    private async Task<string> TeamOfAsync(long item) =>
        (await host.Services.GetRequiredService<IBacklogStore>().DispatchesAsync(item, Ct))[^1].TeamId;

    private async Task<long> WorkflowOfAsync(long item) =>
        (await host.Services.GetRequiredService<IBacklogStore>().DispatchesAsync(item, Ct))[^1].Correlation;

    private Task<string> KeyAsync(string team, string member) =>
        host.Services.GetRequiredService<IPrincipalStore>().MintAsync(
            new ContainerId(team, member).ToString(), PrincipalKind.Container, team, Permits.All);

    private IMessageLog Log => host.Services.GetRequiredService<IMessageLog>();
}
