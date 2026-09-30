using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Harness.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// The Manager sees what is queued for each member, and a `tell` that repeats a queued
/// instruction says so - through the MCP tools a Manager actually calls.
///
/// Each test has its own PAUSED team, so the pump offers nothing and a delivery stays exactly where
/// the test put it. Acceptance is recorded the way the pump records it, through
/// <see cref="IPendingDeliveries.AddAsync"/>, which is the durable queue the listing reads.
/// </summary>
public sealed class QueuedInstructionsToolTests(HostFixture host) : IClassFixture<HostFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Status_lists_each_members_queued_instructions_with_seq_first_line_and_source()
    {
        var (team, manager) = await PausedTeamAsync();
        var member = await HireAsync(team, "Developer Ada");

        var sent = await TellAsync(manager, team, member, "Build the queue view\nwith every detail.");
        await host.Services.GetRequiredService<IPendingDeliveries>().AddAsync(new ContainerId(team, member), sent, Ct);

        var status = await manager.Status(team: team, cancellationToken: Ct);

        var queued = Queued(status);
        var entry = Assert.Single(queued);
        Assert.Equal(member, entry.GetProperty("member").GetString());
        Assert.Equal(sent, entry.GetProperty("seq").GetInt64());
        Assert.Equal("Build the queue view", entry.GetProperty("line").GetString());
        Assert.Equal($"{team}/Manager", entry.GetProperty("source").GetString());
        Assert.Equal("queued", entry.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Status_for_one_member_lists_only_that_members_queue()
    {
        var (team, manager) = await PausedTeamAsync();
        var ada = await HireAsync(team, "Developer Ada");
        var bea = await HireAsync(team, "Tester Bea");
        var pending = host.Services.GetRequiredService<IPendingDeliveries>();

        await pending.AddAsync(new ContainerId(team, ada), await TellAsync(manager, team, ada, "For Ada"), Ct);
        await pending.AddAsync(new ContainerId(team, bea), await TellAsync(manager, team, bea, "For Bea"), Ct);

        var status = await manager.Status(member: bea, team: team, cancellationToken: Ct);

        var entry = Assert.Single(Queued(status));
        Assert.Equal("For Bea", entry.GetProperty("line").GetString());
    }

    [Fact]
    public async Task A_started_delivery_is_running_not_queued()
    {
        var (team, manager) = await PausedTeamAsync();
        var member = await HireAsync(team, "Developer Ada");
        var pending = host.Services.GetRequiredService<IPendingDeliveries>();
        var id = new ContainerId(team, member);

        var running = await TellAsync(manager, team, member, "Running now");
        var waiting = await TellAsync(manager, team, member, "Waiting");
        await pending.AddAsync(id, running, Ct);
        await pending.StartAsync(id, running, Ct);
        await pending.AddAsync(id, waiting, Ct);

        var entry = Assert.Single(Queued(await manager.Status(team: team, cancellationToken: Ct)));
        Assert.Equal(waiting, entry.GetProperty("seq").GetInt64());
    }

    [Fact]
    public async Task Tell_names_a_queued_duplicate_in_the_same_workflow_and_still_sends_it()
    {
        var (team, manager) = await PausedTeamAsync();
        var member = await HireAsync(team, "Developer Ada");
        var id = new ContainerId(team, member);

        var first = await TellAsync(manager, team, member, "Build the notice: the folder link.");
        await host.Services.GetRequiredService<IPendingDeliveries>().AddAsync(id, first, Ct);

        // Same words, spaced and cased differently, in the same workflow.
        var reply = await manager.Tell(
            member, "build the notice:  the folder link.", team: team, causation: first.ToString(), cancellationToken: Ct);

        Assert.StartsWith("HTTP 200", reply);
        var body = Body(reply);
        Assert.Equal(first, body.GetProperty("duplicateOf").GetInt64());
        Assert.Contains($"#{first}", body.GetProperty("duplicateNotice").GetString(), StringComparison.Ordinal);

        // SENT ANYWAY: tell is never held.
        var second = body.GetProperty("seq").GetInt64();
        Assert.NotEqual(first, second);
        Assert.NotNull(await host.Services.GetRequiredService<IMessageLog>().FindAsync(second, Ct));
    }

    [Fact]
    public async Task Tell_says_nothing_about_different_text_or_another_workflow()
    {
        var (team, manager) = await PausedTeamAsync();
        var member = await HireAsync(team, "Developer Ada");
        var id = new ContainerId(team, member);

        var first = await TellAsync(manager, team, member, "Build the notice.");
        await host.Services.GetRequiredService<IPendingDeliveries>().AddAsync(id, first, Ct);

        var different = Body(await manager.Tell(
            member, "Build the settings page after the notice.", team: team, causation: first.ToString(), cancellationToken: Ct));
        Assert.Equal(JsonValueKind.Null, different.GetProperty("duplicateOf").ValueKind);
        Assert.Equal(JsonValueKind.Null, different.GetProperty("duplicateNotice").ValueKind);

        // No causation: a new workflow, where nothing of this one is queued.
        var elsewhere = Body(await manager.Tell(member, "Build the notice.", team: team, cancellationToken: Ct));
        Assert.Equal(JsonValueKind.Null, elsewhere.GetProperty("duplicateOf").ValueKind);
    }

    [Fact]
    public async Task Tell_does_not_call_a_started_instruction_queued()
    {
        var (team, manager) = await PausedTeamAsync();
        var member = await HireAsync(team, "Developer Ada");
        var id = new ContainerId(team, member);
        var pending = host.Services.GetRequiredService<IPendingDeliveries>();

        var first = await TellAsync(manager, team, member, "Build the notice.");
        await pending.AddAsync(id, first, Ct);
        await pending.StartAsync(id, first, Ct);

        var reply = Body(await manager.Tell(
            member, "Build the notice.", team: team, causation: first.ToString(), cancellationToken: Ct));
        Assert.Equal(JsonValueKind.Null, reply.GetProperty("duplicateOf").ValueKind);
    }

    /// <summary>A delivery the defer put back - through the real store's
    /// <see cref="IPendingDeliveries.DeferAsync"/> - is listed by `status` as deferred, naming the
    /// run. The runtime's own deferral is followed into this listing by
    /// <c>DeferredItemQueueListingTests</c>.</summary>
    [Fact]
    public async Task Status_lists_a_deferred_delivery_as_deferred_from_its_run()
    {
        var (team, manager) = await PausedTeamAsync();
        var member = await HireAsync(team, "Developer Ada");
        var id = new ContainerId(team, member);
        var sent = await TellAsync(manager, team, member, "Do Y after X");
        var pending = host.Services.GetRequiredService<IPendingDeliveries>();

        await pending.AddAsync(id, sent, Ct);
        await pending.StartAsync(id, sent, Ct);
        await pending.DeferAsync(id, sent, fromRun: 4242, Ct);

        var entry = Assert.Single(Queued(await manager.Status(team: team, cancellationToken: Ct)));
        Assert.Equal(sent, entry.GetProperty("seq").GetInt64());
        Assert.Equal("deferred", entry.GetProperty("state").GetString());
        Assert.Equal(4242, entry.GetProperty("deferredFromRun").GetInt64());
        Assert.Equal("Do Y after X", entry.GetProperty("line").GetString());
    }

    private static JsonElement[] Queued(string status)
    {
        // The roster, then the queue: the queue is the section that follows the roster.
        var start = status.IndexOf("{\"queued\"", StringComparison.Ordinal);
        Assert.True(start >= 0, status);

        var end = status.IndexOf('\n', start);
        using var queue = JsonDocument.Parse(status[start..(end < 0 ? status.Length : end)]);
        return [.. queue.RootElement.GetProperty("queued").EnumerateArray().Select(e => e.Clone())];
    }

    private static JsonElement Body(string reply) =>
        JsonDocument.Parse(reply[reply.IndexOf('{')..]).RootElement.Clone();

    private static async Task<long> TellAsync(PlatformMcpTools manager, string team, string member, string instruction)
    {
        var reply = await manager.Tell(member, instruction, team: team, cancellationToken: Ct);
        Assert.StartsWith("HTTP 200", reply);
        return Body(reply).GetProperty("seq").GetInt64();
    }

    private async Task<(string Team, PlatformMcpTools Manager)> PausedTeamAsync()
    {
        var registry = host.Services.GetRequiredService<TeamRegistry>();
        var agent = host.Services.GetRequiredService<AgentCatalog>().Definitions
            .First(d => d.Mode == AgentMode.Headless).Name;

        var team = (await registry.CreateAsync($"Queue {Guid.NewGuid():N}"[..14], agent, memberAgent: agent, ct: Ct)).Id;
        await registry.SetPausedAsync(team, true, Ct);

        var managerId = new ContainerId(team, TeamRegistry.DefaultManagerName);
        var key = await host.Services.GetRequiredService<IPrincipalStore>().MintAsync(
            managerId.ToString(), PrincipalKind.Container, team, Permits.All);

        return (team, Tools(managerId, key));
    }

    private async Task<string> HireAsync(string team, string label)
    {
        var agent = host.Services.GetRequiredService<AgentCatalog>().Definitions
            .First(d => d.Mode == AgentMode.Headless).Name;

        return (await host.Services.GetRequiredService<TeamRegistry>()
            .HireMemberAsync(team, label, agent, "", [], ct: Ct)).Id;
    }

    private PlatformMcpTools Tools(ContainerId caller, string key)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[ApiKeyAuthenticationHandler.Header] = key;
        context.User = PrincipalClaims.ToClaimsPrincipal(
            new Principal(caller.ToString(), PrincipalKind.Container, new HashSet<string>()), "test");

        return new PlatformMcpTools(
            new FixedAccessor(context),
            host.Services.GetRequiredService<IPrincipalStore>(),
            host.Services.GetRequiredService<AgentCatalog>(),
            new ServerClients(host.ServerHandler()));
    }

    /// <summary>Held in a field: <see cref="HttpContextAccessor"/> is async-local, and these tools
    /// are built inside an async helper whose locals do not flow back to the test.</summary>
    private sealed class FixedAccessor(HttpContext context) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get => context; set { } }
    }

    private sealed class ServerClients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("http://localhost") };
    }
}

/// <summary>
/// What the Manager and Member are TOLD about queued instructions and batched runs. The
/// option to defer an item is named as the MCP tool that blocks one - never a shell verb, which
/// <see cref="McpContractTests"/> holds for every skill and prompt.
/// </summary>
public sealed class BatchedRunGuidanceTests
{
    private const string Defer = "`blocked` with reason \"<why it waits>\", `item` <n> and `defer: true`";

    [Fact]
    public void The_manager_skill_says_a_queued_instruction_is_delivered_in_turn_and_not_re_sent()
    {
        var manager = BuiltInSkills.All.Single(s => s.Name == "manager").Body;

        Assert.Contains("## An instruction already queued is delivered in turn", manager, StringComparison.Ordinal);
        Assert.Contains("do not re-send the instruction that was\nqueued behind it", manager, StringComparison.Ordinal);
        Assert.Contains("`duplicateOf`", manager, StringComparison.Ordinal);
        Assert.Contains("`defer: true`", manager, StringComparison.Ordinal);
    }

    [Fact]
    public void The_member_skill_names_the_defer_through_the_blocked_tool()
    {
        var member = BuiltInSkills.All.Single(s => s.Name == "member").Body;

        Assert.Contains("## A run with several numbered items", member, StringComparison.Ordinal);
        Assert.Contains(Defer, member, StringComparison.Ordinal);
        Assert.Contains("Deferring the only item of a run is refused.", member, StringComparison.Ordinal);
    }

    [Fact]
    public void The_manager_and_member_prompts_name_the_defer_through_the_blocked_tool()
    {
        foreach (var prompt in new[] { BuiltInPrompts.ManagerPromptText, BuiltInPrompts.MemberPromptText })
        {
            Assert.Contains("## A run with several numbered items", prompt, StringComparison.Ordinal);
            Assert.Contains("`blocked` naming its `item` and `defer: true`", prompt, StringComparison.Ordinal);
            Assert.Contains("The only item of a run cannot be deferred.", prompt, StringComparison.Ordinal);
        }
    }
}
