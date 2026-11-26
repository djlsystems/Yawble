using System.Text.Json;
using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Ipc;
using Yawble.Core.Policy;
using Yawble.Core.Ui;
using Xunit;

namespace Yawble.Core.Tests.Bus;

/// <summary>
/// An agent card lists the deliverables its active run works on (RunDeliverables: the distinct CorrelationIds of the run's
/// items, oldest first), and is empty for an idle member and an interactive session. teams.agents always carries it
/// as runDeliverables, an array of numbers. Fake runners and sessions only, the database in the test temp root.
/// </summary>
public class AgentCardRunDeliverablesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Rig : IAsyncDisposable
    {
        public TempDb Db { get; } = new();
        public BusEngine Engine { get; private set; } = null!;
        public RpcDispatcher Rpc { get; private set; } = null!;
        public long TeamId { get; private set; }

        public static async Task<Rig> StartAsync(ScriptedRunner runner)
        {
            var rig = new Rig();
            rig.Engine = new BusEngine(rig.Db.OpenStore(), runner, new BusOptions { SafetyPoll = TimeSpan.FromMilliseconds(100), IdleOffer = false });
            rig.TeamId = await rig.Engine.CreateTeamAsync("Alpha", ct: Ct);
            await rig.Engine.AddMemberAsync("Alpha", "Ada", ct: Ct);
            await rig.Engine.AddMemberAsync("Alpha", "Bo", ct: Ct);
            await rig.Engine.StartAsync(Ct);
            var terminals = new TerminalHub(new AppEvents());
            var home = OperatingSystem.IsWindows() ? @"C:\Users\pat" : "/home/pat";
            var dataDir = OperatingSystem.IsWindows() ? @"C:\Users\pat\AppData\Local\Yawble\data" : "/home/pat/.local/share/yawble/data";
            var install = OperatingSystem.IsWindows() ? @"C:\Program Files\Yawble" : "/opt/yawble";
            var ui = new UiRpc(rig.Engine, new PolicyStore(rig.Db.ConnectionString), new MemberLayouts(dataDir, install, home), new FakeModelCredentials(),
                new FakeHostTerminals(terminals, TimeSpan.FromMilliseconds(5)), new FakeInteractiveSessions(terminals, rig.Engine), terminals, 16_384);
            rig.Rpc = new RpcDispatcher(new Yawble.Core.Sandboxing.SandboxPlatformInfo(), () => [], ui: ui);
            return rig;
        }

        public async Task<AgentCard> Card(string name) =>
            (await Engine.AgentCardsAsync(TeamId, Ct)).Single(c => c.Member.Name == name);

        /// <summary>teams.agents' node for one member, as the shell reads it.</summary>
        public async Task<JsonObject> Wire(string name) =>
            (await Rpc.DispatchAsync("teams.agents", new JsonObject { ["teamId"] = TeamId }, Ct))!.AsArray()
                .Single(a => a!["member"]!["name"]!.GetValue<string>() == name)!.AsObject();

        public async ValueTask DisposeAsync()
        {
            await Engine.DisposeAsync();
            Db.Dispose();
        }
    }

    private static long[] Numbers(JsonObject card)
    {
        Assert.True(card.ContainsKey("runDeliverables"), "teams.agents carries runDeliverables: " + card.ToJsonString());
        return [.. card["runDeliverables"]!.AsArray().Select(n => n!.GetValue<long>())];
    }

    [Fact]
    public async Task ARunningMember_ListsItsRunsDeliverable_OnTheCardAndInTeamsAgents()
    {
        var release = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        var runner = new ScriptedRunner().On("Alpha/Ada", async (run, ct) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(ct);
            return RunResult.Ok;
        });
        await using var rig = await Rig.StartAsync(runner);
        var wf = (await rig.Engine.PersonTellAsync("Alpha", "Ada", "build it", ct: Ct)).CorrelationId;
        await started.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);

        var card = await rig.Card("Ada");
        Assert.Equal("running", card.State);
        Assert.Equal([wf], card.RunDeliverables);
        Assert.Equal([wf], Numbers(await rig.Wire("Ada")));
        // Bo has no run: an empty array, never absent or null.
        Assert.Empty((await rig.Card("Bo")).RunDeliverables);
        Assert.Empty(Numbers(await rig.Wire("Bo")));

        release.SetResult();
        await Wait.UntilAsync(() => rig.Engine.IsQuiescentAsync(Ct), "the bus to go quiet");
        Assert.Empty((await rig.Card("Ada")).RunDeliverables); // its run ended
    }

    [Fact]
    public void ARunsItemsFromTwoDeliverables_ListBoth_Distinct_OldestFirst()
    {
        var at = DateTimeOffset.UtcNow;
        Message Item(long seq, long deliverable) => new(seq, MessageTypes.InstructionPrefix + "Alpha/Ada", "{}", "person", deliverable, null, 0, at);
        IReadOnlyList<Message> items = [Item(10, 7), Item(11, 7), Item(12, 3), Item(13, 7), Item(14, 3)];
        Assert.Equal([7L, 3L], AgentCard.DeliverablesOf(items));
        Assert.Equal([5L], AgentCard.DeliverablesOf([Item(20, 5)]));
        Assert.Empty(AgentCard.DeliverablesOf(null));
    }

    [Fact]
    public async Task AnIdleMember_HasNoRunDeliverables()
    {
        await using var rig = await Rig.StartAsync(new ScriptedRunner());
        var card = await rig.Card("Ada");
        Assert.Equal("idle", card.State);
        Assert.Empty(card.RunDeliverables);
        var wire = await rig.Wire("Ada");
        Assert.Empty(Numbers(wire));
        Assert.Equal("[]", wire["runDeliverables"]!.ToJsonString());
    }

    [Fact]
    public async Task AnInteractiveSession_HasNoRunDeliverables()
    {
        await using var rig = await Rig.StartAsync(new ScriptedRunner());
        var release = new TaskCompletionSource();
        var opened = new TaskCompletionSource();
        await rig.Engine.RequestSessionAsync("Alpha", "Ada", async ct =>
        {
            opened.TrySetResult();
            await release.Task.WaitAsync(ct);
        }, Ct);
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);

        var card = await rig.Card("Ada");
        Assert.True(card.Interactive);
        Assert.Equal("running", card.State);
        Assert.Null(card.RunId);
        Assert.Empty(card.RunDeliverables);
        Assert.Empty(Numbers(await rig.Wire("Ada")));

        release.SetResult();
        await Wait.UntilAsync(() => rig.Engine.IsQuiescentAsync(Ct), "the session to end");
    }

    [Fact]
    public void ACardBuiltWithoutIt_SerializesAnEmptyArray()
    {
        var card = new AgentCard(null!, "idle", null, 0, 0, null, null, null, null, null);
        Assert.Empty(card.RunDeliverables);
        var wire = JsonSerializer.SerializeToNode(card, RpcDispatcher.Json)!;
        Assert.Equal("[]", wire["runDeliverables"]!.ToJsonString());
        var withTwo = card with { RunDeliverables = [4, 9] };
        Assert.Equal("[4,9]", JsonSerializer.SerializeToNode(withTwo, RpcDispatcher.Json)!["runDeliverables"]!.ToJsonString());
    }
}
