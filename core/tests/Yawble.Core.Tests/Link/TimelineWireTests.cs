using System.Text.Json.Nodes;
using Yawble.Core.Board;
using Yawble.Core.Bus;
using Yawble.Core.Ipc;
using Yawble.Core.Link;
using Yawble.Core.Policy;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Ui;
using Xunit;

namespace Yawble.Core.Tests.Link;

/// <summary>
/// The timeline's wire for the shell and the cloud: timeline. is a screen prefix, timeline.get still
/// reaches the Board with the screens wired, a Visitor may call it over the Cloud Link, and org.summary's workflow rows
/// carry the same <c>time</c> as the bus.workflows item (its ticking ms alone are not a change).
/// </summary>
public class TimelineWireTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    // ---------------------------------------------------------------- 1. the screen prefix

    [Fact]
    public void TimelineIsAScreenPrefix_AndOnTheCloudLinksAllowList()
    {
        Assert.Contains("timeline.", UiRpc.Prefixes);
        Assert.True(UiRpc.Handles("timeline.get"));
        Assert.True(RemoteAccess.OnAllowList("timeline.get"));
    }

    [Fact]
    public async Task TimelineGet_ReachesTheBoard_ThoughTheScreensClaimThePrefix()
    {
        using var db = new TempDb();
        await using var bus = new BusEngine(db.OpenStore(), new UnavailableRunner());
        await bus.StartAsync(Ct);
        var terminals = new TerminalHub(new AppEvents());
        var home = OperatingSystem.IsWindows() ? @"C:\Users\pat" : "/home/pat";
        var dataDir = OperatingSystem.IsWindows() ? @"C:\Users\pat\AppData\Local\Yawble\data" : "/home/pat/.local/share/yawble/data";
        var install = OperatingSystem.IsWindows() ? @"C:\Program Files\Yawble" : "/opt/yawble";
        var ui = new UiRpc(bus, new PolicyStore(db.ConnectionString), new MemberLayouts(dataDir, install, home), new FakeModelCredentials(),
            new FakeHostTerminals(terminals, TimeSpan.FromMilliseconds(5)), new FakeInteractiveSessions(terminals, bus), terminals, 16_384);
        var board = new BoardService(new BoardStore(db.ConnectionString), bus);
        var rpc = new RpcDispatcher(new Yawble.Core.Sandboxing.SandboxPlatformInfo(), () => [], ui: ui, board: board);

        // The Board's own answers, not the screens' unknown_method.
        var noArgs = await Assert.ThrowsAsync<RpcException>(() => rpc.DispatchAsync("timeline.get", new JsonObject(), Ct));
        Assert.Equal("bad_params", noArgs.Code);
        var noFlow = await Assert.ThrowsAsync<RpcException>(() => rpc.DispatchAsync("timeline.get", new JsonObject { ["workflow"] = 999 }, Ct));
        Assert.Equal("no_such_workflow", noFlow.Code);
        // A name under timeline. that nobody has is still unknown.
        var other = await Assert.ThrowsAsync<RpcException>(() => rpc.DispatchAsync("timeline.zzzNew", null, Ct));
        Assert.Equal("unknown_method", other.Code);
    }

    // ---------------------------------------------------------------- 2. a Visitor read

    [Theory]
    [InlineData("{\"card\":7}")]
    [InlineData("{\"workflow\":3573}")]
    public void TimelineGet_IsAVisitorRead(string args)
    {
        var p = JsonNode.Parse(args)!.AsObject();
        Assert.Equal(RemoteAccess.Kind.Reads, RemoteAccess.Kinds["timeline.get"]);
        Assert.True(RemoteAccess.Reads("timeline.get", p));
        Assert.Null(RemoteAccess.Refusal("timeline.get", p, allowHostTerminals: false, RemoteAccess.Read));
        Assert.Null(RemoteAccess.Refusal("timeline.get", p, allowHostTerminals: false, RemoteAccess.Full));
        // Only that name: the prefix and a new name under it are not reads.
        Assert.False(RemoteAccess.Reads("timeline.", null));
        Assert.False(RemoteAccess.Reads("timeline.zzzNew", null));
    }

    // ---------------------------------------------------------------- 4. org.summary's workflow rows

    private static TimeSummary Time(bool open, long working, long waiting) => new(
        "2026-11-18T10:00:00Z", "2026-11-18T10:01:00Z", open ? null : "2026-11-18T11:00:00Z", open ? null : "completed", open,
        working + waiting, Rebuilt: false,
        [new TimeShare(TimeCategories.AgentWorking, working), new TimeShare(TimeCategories.WaitingOnYou, waiting)]);

    private static OrgSummaryFacts Facts(TimeSummary? openTime, TimeSummary? endedTime = null) => new(
        new OrgComputer("DESK-1", null, "2026.11.18.1", "Windows 11", Now.AddHours(-3), 0, 4, 0),
        [new OrgTeam("web", Paused: false, Archived: false, [])],
        [
            new OrgWorkflow(3573, "web", "Timelines", null, null, OrgSummary.Running, 1, 3, Now.AddHours(-2), Now.AddMinutes(-1), "Manager", Time: openTime),
            new OrgWorkflow(3574, "web", "Done one", null, null, OrgSummary.Completed, 2, 2, Now.AddHours(-2), Now.AddMinutes(-30), "Manager", Time: endedTime),
            new OrgWorkflow(3575, "web", "No board", null, null, OrgSummary.Running, 0, 0, Now.AddHours(-1), Now.AddMinutes(-2), "Manager"),
        ],
        [], []);

    [Fact]
    public void OrgSummaryWorkflowRows_CarryTheWireTime()
    {
        var open = Time(open: true, working: 60_000, waiting: 30_000);
        var s = OrgSummary.Build(Facts(open, Time(open: false, 3_000_000, 600_000)), Now);
        var rows = s["workflows"]!.AsArray().ToDictionary(r => r!["id"]!.GetValue<long>(), r => r!.AsObject());

        // Exactly the bus.workflows item's time (TimeSummary.ToJson, the wire contract on the card).
        Assert.Equal(open.ToJson().ToJsonString(), rows[3573]["time"]!.ToJsonString());
        var t = rows[3573]["time"]!;
        Assert.Equal(["createdAt", "startedAt", "endedAt", "endedHow", "open", "overallMs", "rebuilt", "breakdown"], t.AsObject().Select(p => p.Key));
        Assert.True(t["open"]!.GetValue<bool>());
        Assert.Equal(90_000, t["overallMs"]!.GetValue<long>());
        Assert.Equal(t["overallMs"]!.GetValue<long>(), t["breakdown"]!.AsArray().Sum(b => b!["ms"]!.GetValue<long>()));
        Assert.Equal(TimeCategories.AgentWorking, t["breakdown"]![0]!["category"]!.GetValue<string>());

        Assert.Equal("completed", rows[3574]["time"]!["endedHow"]!.GetValue<string>());
        Assert.False(rows[3574]["time"]!["open"]!.GetValue<bool>());
        // Without a board the key is there, null (like the other optional fields).
        Assert.True(rows[3575].ContainsKey("time"));
        Assert.Null(rows[3575]["time"]);
    }

    [Fact]
    public void AnOpenWorkflowsTickingTime_IsNotAChange_ButANewCategoryOrAnEndedOnesTimeIs()
    {
        var ended = Time(open: false, 3_000_000, 600_000);
        var last = OrgSummary.Build(Facts(Time(open: true, 60_000, 30_000), ended), Now);

        // Two seconds later: only the open one's ms grew.
        var tick = OrgSummary.Build(Facts(Time(open: true, 62_000, 30_000), ended), Now);
        Assert.DoesNotContain("workflows", OrgSummary.Changed(last, tick));

        // A category it was not in before is a change.
        var moreOpen = Time(open: true, 62_000, 30_000) with
        {
            Breakdown = [new TimeShare(TimeCategories.AgentWorking, 62_000), new TimeShare(TimeCategories.WaitingOnYou, 30_000), new TimeShare(TimeCategories.Stalled, 1_000)],
        };
        Assert.Contains("workflows", OrgSummary.Changed(last, OrgSummary.Build(Facts(moreOpen, ended), Now)));

        // An ended workflow's time is compared in full (it does not tick).
        Assert.Contains("workflows", OrgSummary.Changed(last, OrgSummary.Build(Facts(Time(open: true, 60_000, 30_000), Time(open: false, 3_000_001, 600_000)), Now)));

        // The snapshot itself is untouched by the comparison.
        Assert.Equal(60_000 + 30_000, last["workflows"]![0]!["time"]!["overallMs"]!.GetValue<long>());
    }
}
