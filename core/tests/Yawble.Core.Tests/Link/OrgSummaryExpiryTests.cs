using System.Globalization;
using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Ipc;
using Yawble.Core.Link;
using Yawble.Core.Policy;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Ui;
using Xunit;

namespace Yawble.Core.Tests.Link;

/// <summary>
/// org.summary, as the core sends it over the Cloud Link, carries teams[].deliverableExpiryDays
/// (the team's setting, default 3) and deliverables[].expiresAt (the same instant as the bus.deliverables item's expiresAt; null
/// while the deliverable is worked and once it ended). Built from the real app source over a real bus with a fake clock, and
/// read as JSON, the way yawble-cloud's OrgWire reads it.
/// </summary>
public class OrgSummaryExpiryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Start = new(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);

    private sealed class Rig : IAsyncDisposable
    {
        public TempDb Db { get; } = new();
        public DeliverableExpiryTests.ManualClock Clock { get; } = new(Start);
        public BusEngine Engine { get; private set; } = null!;
        public RpcDispatcher Rpc { get; private set; } = null!;
        public long TeamId { get; private set; }

        public static async Task<Rig> StartAsync(ScriptedRunner runner)
        {
            var rig = new Rig();
            rig.Engine = new BusEngine(new BusStore(rig.Db.ConnectionString, rig.Clock), runner,
                new BusOptions { SafetyPoll = TimeSpan.FromMilliseconds(100), IdleOffer = false });
            await rig.Engine.CreateTeamAsync("Alpha", ct: Ct);
            await rig.Engine.AddMemberAsync("Alpha", "Ada", ct: Ct);
            await rig.Engine.StartAsync(Ct);
            var terminals = new TerminalHub(new AppEvents());
            var home = OperatingSystem.IsWindows() ? @"C:\Users\pat" : "/home/pat";
            var dataDir = OperatingSystem.IsWindows() ? @"C:\Users\pat\AppData\Local\Yawble\data" : "/home/pat/.local/share/yawble/data";
            var install = OperatingSystem.IsWindows() ? @"C:\Program Files\Yawble" : "/opt/yawble";
            var ui = new UiRpc(rig.Engine, new PolicyStore(rig.Db.ConnectionString), new MemberLayouts(dataDir, install, home), new FakeModelCredentials(),
                new FakeHostTerminals(terminals, TimeSpan.FromMilliseconds(5)), new FakeInteractiveSessions(terminals, rig.Engine), terminals, 16_384);
            rig.Rpc = new RpcDispatcher(new Yawble.Core.Sandboxing.SandboxPlatformInfo(), () => [], ui: ui);
            rig.TeamId = (await rig.Engine.TeamsAsync(Ct)).Single().Id;
            return rig;
        }

        public async Task<JsonNode?> Call(string method, object? args = null) =>
            await Rpc.DispatchAsync(method, args is null ? null : System.Text.Json.JsonSerializer.SerializeToNode(args)!.AsObject(), Ct);

        /// <summary>org.summary's snapshot: the app source and the builder OrgSummaryService sends, round-tripped as JSON text.</summary>
        public async Task<JsonObject> Summary()
        {
            var facts = await new AppOrgSummarySource(Engine, null, null, null, null, null, () => null, Start, Clock).ReadAsync(Ct);
            return JsonNode.Parse(OrgSummary.Build(facts, Clock.GetUtcNow()).ToJsonString())!.AsObject();
        }

        public async Task<JsonObject> SummaryDeliverable(long id) =>
            (await Summary())["deliverables"]!.AsArray().Single(w => w!["id"]!.GetValue<long>() == id)!.AsObject();

        public async Task<JsonObject> BusItem(long id) =>
            (await Call("bus.deliverables", new { teamId = TeamId }))!.AsArray().Single(w => w!["id"]!.GetValue<long>() == id)!.AsObject();

        public DateTimeOffset LastActivity(long id)
        {
            using var s = Db.OpenStore();
            return s.Deliverable(id).Where(m => m.Type is not (MessageTypes.DeliverableExpired or BusEngine.DeliverableExpiring)).Max(m => m.OccurredAt);
        }

        public async ValueTask DisposeAsync()
        {
            await Engine.DisposeAsync();
            Db.Dispose();
        }
    }

    private static DateTimeOffset? At(JsonNode? n) => n is null ? null : DateTimeOffset.Parse(n.GetValue<string>(), CultureInfo.InvariantCulture);

    /// <summary>org.summary's times are to the second (yyyy-MM-ddTHH:mm:ssZ).</summary>
    private static DateTimeOffset? ToSecond(DateTimeOffset? t) => t is { } v ? new DateTimeOffset(v.UtcTicks - v.UtcTicks % TimeSpan.TicksPerSecond, TimeSpan.Zero) : null;

    [Fact]
    public async Task Teams_CarryDeliverableExpiryDays_Default3_AndFollowTheSetting()
    {
        await using var rig = await Rig.StartAsync(new ScriptedRunner());
        var team = Assert.Single((await rig.Summary())["teams"]!.AsArray())!.AsObject();
        Assert.True(team.ContainsKey("deliverableExpiryDays"));
        Assert.Equal(3, team["deliverableExpiryDays"]!.GetValue<int>());

        await rig.Call("teams.deliverableExpiry", new { teamId = rig.TeamId, days = 5 });
        Assert.Equal(5, Assert.Single((await rig.Summary())["teams"]!.AsArray())!["deliverableExpiryDays"]!.GetValue<int>());
        Assert.Equal(5, (await rig.Call("teams.list"))!.AsArray().Single()!["deliverableExpiryDays"]!.GetValue<int>());
    }

    [Fact]
    public async Task Deliverables_CarryExpiresAt_SameAsTheBusItem_NullWhileWorked_AndOnceExpired()
    {
        var release = new TaskCompletionSource<RunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = true;
        var runner = new ScriptedRunner().On("Alpha/Ada", (_, _) =>
        {
            if (!first) return Task.FromResult(RunResult.Ok);
            first = false;
            return release.Task;
        });
        await using var rig = await Rig.StartAsync(runner);
        var root = await rig.Engine.PersonTellAsync("Alpha", "Ada", "look into the flaky test", ct: Ct);

        // Worked: Ada's run is going, so nothing will expire it.
        await Wait.UntilAsync(async () => (await rig.BusItem(root.Seq))["busy"]!.AsArray().Count > 0, "Ada is running");
        var worked = await rig.SummaryDeliverable(root.Seq);
        Assert.True(worked.ContainsKey("expiresAt"));
        Assert.Null(worked["expiresAt"]);
        Assert.Null((await rig.BusItem(root.Seq))["expiresAt"]);

        // Idle: expiresAt is the bus item's (last activity + 3 days), to the second.
        release.SetResult(RunResult.Ok);
        await Wait.UntilAsync(() => rig.Engine.IsQuiescentAsync(Ct), "quiet");
        var last = rig.LastActivity(root.Seq);
        var item = await rig.BusItem(root.Seq);
        Assert.Equal(last.AddDays(3), At(item["expiresAt"]));
        var idle = await rig.SummaryDeliverable(root.Seq);
        Assert.Equal(ToSecond(At(item["expiresAt"])), At(idle["expiresAt"]));
        Assert.Equal(ToSecond(last.AddDays(3)), At(idle["expiresAt"]));

        // Expired: ended, so no expiresAt; endedAt and expiredAt are set.
        rig.Clock.Now = last.AddDays(3).AddMinutes(1);
        Assert.Equal(1, await rig.Engine.SweepExpiryAsync(rig.Clock.Now, Ct));
        var expired = await rig.SummaryDeliverable(root.Seq);
        Assert.Equal(OrgSummary.Expired, expired["state"]!.GetValue<string>());
        Assert.True(expired.ContainsKey("expiresAt"));
        Assert.Null(expired["expiresAt"]);
        Assert.Null((await rig.BusItem(root.Seq))["expiresAt"]);
        Assert.Equal(ToSecond(last), At(expired["endedAt"]));
        Assert.Equal(ToSecond(rig.Clock.Now), At(expired["expiredAt"]));
    }

    [Fact]
    public void Build_WritesBothFields_FromTheFacts()
    {
        var facts = new OrgSummaryFacts(new OrgComputer("pc", null, "1.0", "os", Start, 0, 4, 0),
            [new OrgTeam("web", false, false, [], DeliverableExpiryDays: 7), new OrgTeam("api", false, false, [])],
            [
                new OrgDeliverable(1, "web", "idle", null, null, OrgSummary.NobodyWorking, 0, 0, Start, Start, "Manager", ExpiresAt: Start.AddDays(7)),
                new OrgDeliverable(2, "web", "worked", null, null, OrgSummary.Running, 0, 0, Start, Start, "Manager"),
            ], [], []);
        var s = OrgSummary.Build(facts, Start);
        var teams = s["teams"]!.AsArray();
        Assert.Equal(3, teams.Single(t => t!["name"]!.GetValue<string>() == "api")!["deliverableExpiryDays"]!.GetValue<int>());
        Assert.Equal(7, teams.Single(t => t!["name"]!.GetValue<string>() == "web")!["deliverableExpiryDays"]!.GetValue<int>());
        var deliverables = s["deliverables"]!.AsArray();
        Assert.Equal("2026-11-24T09:00:00Z", deliverables.Single(w => w!["id"]!.GetValue<long>() == 1)!["expiresAt"]!.GetValue<string>());
        var worked = deliverables.Single(w => w!["id"]!.GetValue<long>() == 2)!.AsObject();
        Assert.True(worked.ContainsKey("expiresAt"));
        Assert.Null(worked["expiresAt"]);
    }
}
