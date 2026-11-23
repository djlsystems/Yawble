using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Ipc;
using Yawble.Core.Link;
using Yawble.Core.Policy;
using Yawble.Core.Ui;
using Xunit;

namespace Yawble.Core.Tests.Bus;

/// <summary>
/// Card 582 (B130 verify): TeamSummary.Waiting counts the team's members with an open needs_decision, over a real bus,
/// and goes down as the person answers each; teams.list carries it as camelCase "waiting"; org.summary carries it as
/// teams[].agents.waitingOnYou (0 when nobody asks). Other teams stay 0.
/// </summary>
public class B130VerifyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Rig : IAsyncDisposable
    {
        public TempDb Db { get; } = new();
        public BusEngine Engine { get; private set; } = null!;
        public RpcDispatcher Rpc { get; private set; } = null!;

        public static async Task<Rig> StartAsync(ScriptedRunner runner)
        {
            var rig = new Rig();
            rig.Engine = new BusEngine(rig.Db.OpenStore(), runner, new BusOptions { SafetyPoll = TimeSpan.FromMilliseconds(100), IdleOffer = false });
            await rig.Engine.CreateTeamAsync("Alpha", ct: Ct);
            await rig.Engine.CreateTeamAsync("Beta", ct: Ct);
            await rig.Engine.AddMemberAsync("Alpha", "Ann", ct: Ct);
            await rig.Engine.AddMemberAsync("Alpha", "Bob", ct: Ct);
            await rig.Engine.AddMemberAsync("Beta", "Cy", ct: Ct);
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

        public async Task<int> Waiting(string team) => (await Engine.TeamsAsync(Ct)).Single(t => t.Name == team).Waiting;

        /// <summary>teams.list as the shell reads it (JSON over the RPC).</summary>
        public async Task<JsonObject> ListedTeam(string team) =>
            (await Rpc.DispatchAsync("teams.list", null, Ct))!.AsArray().Single(t => t!["name"]!.GetValue<string>() == team)!.AsObject();

        /// <summary>org.summary's team, built from the real app source and round-tripped as JSON text.</summary>
        public async Task<JsonObject> SummaryTeam(string team)
        {
            var facts = await new AppOrgSummarySource(Engine, null, null, null, null, null, () => null, DateTimeOffset.UtcNow).ReadAsync(Ct);
            var s = JsonNode.Parse(OrgSummary.Build(facts, DateTimeOffset.UtcNow).ToJsonString())!.AsObject();
            return s["teams"]!.AsArray().Single(t => t!["name"]!.GetValue<string>() == team)!.AsObject();
        }

        public async Task<long> DecisionOf(string team, string member)
        {
            var teamId = (await Engine.TeamsAsync(Ct)).Single(t => t.Name == team).Id;
            var id = (await Engine.AgentCardsAsync(teamId, Ct)).Single(c => c.Member.Name == member).DecisionId;
            Assert.NotNull(id);
            return id!.Value;
        }

        public Task Quiet(string what) => Wait.UntilAsync(() => Engine.IsQuiescentAsync(Ct), what);

        public async ValueTask DisposeAsync()
        {
            await Engine.DisposeAsync();
            Db.Dispose();
        }
    }

    /// <summary>Each member's first run asks the person; later runs (the answer) just end.</summary>
    private static ScriptedRunner AskOnFirstRun(params string[] members)
    {
        var runner = new ScriptedRunner();
        foreach (var m in members)
        {
            var runs = 0;
            runner.On(m, async (run, ct) =>
            {
                if (Interlocked.Increment(ref runs) == 1) await run.NeedsDecisionAsync($"{m}: which way?", ct);
                return RunResult.Ok;
            });
        }
        return runner;
    }

    private static int AgentsWaitingOnYou(JsonObject team)
    {
        var agents = team["agents"]!.AsObject();
        return agents.TryGetPropertyValue("waitingOnYou", out var n) && n is not null ? n.GetValue<int>() : 0;
    }

    [Fact]
    public async Task Waiting_CountsAskingMembers_AndFallsAsThePersonAnswersEach()
    {
        await using var rig = await Rig.StartAsync(AskOnFirstRun("Alpha/Ann", "Alpha/Bob", "Beta/Cy"));

        // Nobody asks: 0 everywhere, in TeamsAsync, teams.list and org.summary.
        Assert.Equal(0, await rig.Waiting("Alpha"));
        Assert.Equal(0, await rig.Waiting("Beta"));
        var listed = await rig.ListedTeam("Alpha");
        Assert.True(listed.ContainsKey("waiting"), "teams.list carries camelCase 'waiting': " + listed.ToJsonString());
        Assert.Equal(0, listed["waiting"]!.GetValue<int>());
        Assert.Equal(0, AgentsWaitingOnYou(await rig.SummaryTeam("Alpha")));

        // Ann asks: Alpha 1, Beta 0.
        await rig.Engine.PersonTellAsync("Alpha", "Ann", "pick the database", ct: Ct);
        await rig.Quiet("Ann asked");
        Assert.Equal(1, await rig.Waiting("Alpha"));
        Assert.Equal(0, await rig.Waiting("Beta"));
        Assert.Equal(1, (await rig.ListedTeam("Alpha"))["waiting"]!.GetValue<int>());
        Assert.Equal(0, (await rig.ListedTeam("Beta"))["waiting"]!.GetValue<int>());
        Assert.Equal(1, AgentsWaitingOnYou(await rig.SummaryTeam("Alpha")));
        Assert.Equal(0, AgentsWaitingOnYou(await rig.SummaryTeam("Beta")));

        // Bob asks too: 2.
        await rig.Engine.PersonTellAsync("Alpha", "Bob", "pick the queue", ct: Ct);
        await rig.Quiet("Bob asked");
        Assert.Equal(2, await rig.Waiting("Alpha"));
        Assert.Equal(0, await rig.Waiting("Beta"));
        Assert.Equal(2, (await rig.ListedTeam("Alpha"))["waiting"]!.GetValue<int>());
        Assert.Equal(2, AgentsWaitingOnYou(await rig.SummaryTeam("Alpha")));

        // The person answers Ann: 1.
        await rig.Engine.PersonAnswerAsync("Alpha", "Ann", "Postgres", await rig.DecisionOf("Alpha", "Ann"), ct: Ct);
        await rig.Quiet("Ann answered");
        Assert.Equal(1, await rig.Waiting("Alpha"));
        Assert.Equal(1, AgentsWaitingOnYou(await rig.SummaryTeam("Alpha")));

        // Then Bob: 0.
        await rig.Engine.PersonAnswerAsync("Alpha", "Bob", "Rabbit", await rig.DecisionOf("Alpha", "Bob"), ct: Ct);
        await rig.Quiet("Bob answered");
        Assert.Equal(0, await rig.Waiting("Alpha"));
        Assert.Equal(0, (await rig.ListedTeam("Alpha"))["waiting"]!.GetValue<int>());
        Assert.Equal(0, AgentsWaitingOnYou(await rig.SummaryTeam("Alpha")));
        Assert.Equal(0, await rig.Waiting("Beta"));
    }

    [Fact]
    public async Task Waiting_IsPerTeam_AnotherTeamsDecisionDoesNotCount()
    {
        await using var rig = await Rig.StartAsync(AskOnFirstRun("Beta/Cy"));
        await rig.Engine.PersonTellAsync("Beta", "Cy", "pick a port", ct: Ct);
        await rig.Quiet("Cy asked");
        Assert.Equal(0, await rig.Waiting("Alpha"));
        Assert.Equal(1, await rig.Waiting("Beta"));
        Assert.Equal(1, AgentsWaitingOnYou(await rig.SummaryTeam("Beta")));
        Assert.Equal(0, AgentsWaitingOnYou(await rig.SummaryTeam("Alpha")));
    }
}
