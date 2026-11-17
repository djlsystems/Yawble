using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Link;
using Yawble.Core.Provenance;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Ui;
using Xunit;

namespace Yawble.Core.Tests.Link;

/// <summary>
/// B048 (card 252): org.summary's snapshot built from fake teams, workflows, asks and log problems; the no
/// token/cost/usage/price rule; the five dashboard methods as reads a Visitor may call; org.summaryChanged pushed only
/// while watched and at most once per 2 s (a manual clock); a viewer leaving the link is unwatched; the hello's caps.
/// Fakes only: no socket, no database, no system program.
/// </summary>
public class OrgSummaryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    private const string Secret = "ghp_abcdefghijklmnopqrstuvwxyz0123456789";

    private static OrgSummaryFacts Facts(int queued = 1, string? extraDecision = null) => new(
        new OrgComputer("DESK-1", "Ada's desk", "2026.11.15.1", "Windows 11", Now.AddHours(-3), 2, 4, queued),
        [
            new OrgTeam("web", Paused: false, Archived: false,
            [
                new OrgAgent("Manager", "Manager", "running", false, "card 7: plan the login page", Now.AddMinutes(-5)),
                new OrgAgent("Ines", "Member", "queued", false),
                new OrgAgent("Tomas", "Member", "idle", false),
                new OrgAgent("Priya", "Member", "idle", true),
            ]),
            new OrgTeam("api", Paused: true, Archived: false, []),
        ],
        [
            new OrgWorkflow(10, "web", "Build the login page", "B048", "Live org summary", OrgSummary.Running, 2, 5, Now.AddHours(-2), Now.AddMinutes(-1), "Manager"),
            new OrgWorkflow(11, "web", "Old but still open", null, null, OrgSummary.NobodyWorking, 0, 0, Now.AddDays(-5), Now.AddDays(-3), "Manager"),
            new OrgWorkflow(12, "web", "Done yesterday morning", null, null, OrgSummary.Completed, 3, 3, Now.AddDays(-2), Now.AddHours(-30), "Manager"),
            new OrgWorkflow(13, "api", "Failed an hour ago", null, null, OrgSummary.Failed, 0, 1, Now.AddHours(-4), Now.AddHours(-1), "Manager"),
        ],
        [
            new OrgAsk("decision", "web", "Which login provider? " + Secret),
            new OrgAsk("toolCall", "web", "Ines: gh pr create"),
            new OrgAsk("network", "api", "Tomas: registry.npmjs.org"),
            new OrgAsk("tool", "api", "Tomas: Node.js"),
            .. extraDecision is null ? Array.Empty<OrgAsk>() : [new OrgAsk("decision", "web", extraDecision)],
        ],
        [
            .. Enumerable.Range(1, 25).Select(i => new OrgProblem("Runs", "web", "Ines", Now.AddMinutes(-i), "error", $"run failed #{i}")),
            new OrgProblem("Network", "web", "Tomas", Now.AddMinutes(-90), "warning", "too old"),
            new OrgProblem("Diagnostics", null, null, Now.AddSeconds(-10), "info", "just chatter"),
            new OrgProblem("Diagnostics", null, null, Now.AddSeconds(-5), "warning", "token " + Secret + "\nsecond line"),
        ]);

    [Fact]
    public void TheSnapshot_IsBuiltFromTheFacts()
    {
        var s = OrgSummary.Build(Facts(), Now);
        Assert.Equal(1, s["summaryVersion"]!.GetValue<int>());
        Assert.Equal(["summaryVersion", "at", "computer", "teams", "workflows", "needsYou", "problems"], s.Select(p => p.Key));

        var c = s["computer"]!;
        Assert.Equal("DESK-1", c["name"]!.GetValue<string>());
        Assert.Equal("Ada's desk", c["label"]!.GetValue<string>());
        Assert.Equal("2026.11.15.1", c["yawbleVersion"]!.GetValue<string>());
        Assert.Equal("Windows 11", c["platform"]!.GetValue<string>());
        Assert.Equal(3 * 3600, c["uptimeSeconds"]!.GetValue<long>());
        Assert.Equal(2, c["runSlots"]!["used"]!.GetValue<int>());
        Assert.Equal(4, c["runSlots"]!["total"]!.GetValue<int>());
        Assert.Equal(1, c["queuedRuns"]!.GetValue<int>());

        var teams = s["teams"]!.AsArray();
        Assert.Equal(["api", "web"], teams.Select(t => t!["name"]!.GetValue<string>()));
        Assert.True(teams[0]!["paused"]!.GetValue<bool>());
        var web = teams[1]!;
        Assert.Equal(4, web["agents"]!["total"]!.GetValue<int>());
        Assert.Equal(1, web["agents"]!["running"]!.GetValue<int>());
        Assert.Equal(1, web["agents"]!["queued"]!.GetValue<int>());
        Assert.Equal(1, web["agents"]!["idle"]!.GetValue<int>());
        Assert.Equal(1, web["agents"]!["errored"]!.GetValue<int>());
        var running = Assert.Single(web["running"]!.AsArray())!;
        Assert.Equal("Manager", running["member"]!.GetValue<string>());
        Assert.Equal("Manager", running["role"]!.GetValue<string>());
        Assert.Equal("card 7: plan the login page", running["doing"]!.GetValue<string>());
        Assert.Equal("2026-11-16T11:55:00Z", running["since"]!.GetValue<string>());

        // Open workflows however old; ended ones of the last 24 h only; newest activity first.
        var workflows = s["workflows"]!.AsArray();
        Assert.Equal([10L, 13L, 11L], workflows.Select(w => w!["id"]!.GetValue<long>()));
        var first = workflows[0]!;
        Assert.Equal("web", first["team"]!.GetValue<string>());
        Assert.Equal("Build the login page", first["title"]!.GetValue<string>());
        Assert.Equal("B048", first["item"]!["id"]!.GetValue<string>());
        Assert.Equal("Live org summary", first["item"]!["title"]!.GetValue<string>());
        Assert.Equal("running", first["state"]!.GetValue<string>());
        Assert.Equal(2, first["cards"]!["done"]!.GetValue<int>());
        Assert.Equal(5, first["cards"]!["total"]!.GetValue<int>());
        Assert.Equal("2026-11-16T10:00:00Z", first["startedAt"]!.GetValue<string>());
        Assert.Equal("2026-11-16T11:59:00Z", first["lastActivityAt"]!.GetValue<string>());
        Assert.Equal("Manager", first["owner"]!.GetValue<string>());
        Assert.Null(workflows[2]!["item"]);
        Assert.Equal("failed", workflows[1]!["state"]!.GetValue<string>());

        var needs = s["needsYou"]!;
        Assert.Equal(4, needs["total"]!.GetValue<int>());
        Assert.Equal(1, needs["decisions"]!.GetValue<int>());
        Assert.Equal(1, needs["toolCalls"]!.GetValue<int>());
        Assert.Equal(1, needs["networkRequests"]!.GetValue<int>());
        Assert.Equal(1, needs["toolRequests"]!.GetValue<int>());
        var perTeam = needs["teams"]!.AsArray();
        Assert.Equal(["api", "web"], perTeam.Select(t => t!["team"]!.GetValue<string>()));
        Assert.Equal(2, perTeam[1]!["total"]!.GetValue<int>());
        Assert.Equal(["Ines: gh pr create"], perTeam[1]!["toolCalls"]!["titles"]!.AsArray().Select(t => t!.GetValue<string>()));
        Assert.Equal(["Tomas: registry.npmjs.org"], perTeam[0]!["networkRequests"]!["titles"]!.AsArray().Select(t => t!.GetValue<string>()));
        Assert.Equal(1, perTeam[0]!["toolRequests"]!["count"]!.GetValue<int>());

        // The last 20 warnings and errors of the last hour, newest first; info and older entries are left out.
        var problems = s["problems"]!.AsArray();
        Assert.Equal(OrgSummary.MaxProblems, problems.Count);
        Assert.Equal("Diagnostics", problems[0]!["source"]!.GetValue<string>());
        Assert.Equal("warning", problems[0]!["level"]!.GetValue<string>());
        Assert.Equal("run failed #1", problems[1]!["message"]!.GetValue<string>());
        Assert.Equal("web", problems[1]!["team"]!.GetValue<string>());
        Assert.Equal("Ines", problems[1]!["member"]!.GetValue<string>());
        Assert.Equal("2026-11-16T11:59:00Z", problems[1]!["at"]!.GetValue<string>());
        Assert.DoesNotContain(problems, p => p!["message"]!.GetValue<string>() is "too old" or "just chatter");
    }

    [Fact]
    public void AFailedWorkflow_IsOpen_AndStaysListedAfter24hIdle_ButACompletedOrClosedOneDoesNot()
    {
        // B051 F2 (card 258): docs/cloud-link.md calls failed open; only completed and closed are ended.
        Assert.False(OrgSummary.Ended(OrgSummary.Failed));
        Assert.True(OrgSummary.Ended(OrgSummary.Completed));
        Assert.True(OrgSummary.Ended(OrgSummary.Closed));
        Assert.All(new[] { OrgSummary.Running, OrgSummary.WaitingOnPerson, OrgSummary.Blocked, OrgSummary.NobodyWorking },
            st => Assert.False(OrgSummary.Ended(st)));

        var facts = Facts() with
        {
            Workflows =
            [
                new OrgWorkflow(20, "web", "Failed 25 h ago", null, null, OrgSummary.Failed, 0, 1, Now.AddHours(-30), Now.AddHours(-25), "Manager"),
                new OrgWorkflow(21, "web", "Completed 25 h ago", null, null, OrgSummary.Completed, 1, 1, Now.AddHours(-30), Now.AddHours(-25), "Manager"),
                new OrgWorkflow(22, "web", "Closed 25 h ago", null, null, OrgSummary.Closed, 0, 1, Now.AddHours(-30), Now.AddHours(-25), "Manager"),
            ],
        };
        var listed = Assert.Single(OrgSummary.Build(facts, Now)["workflows"]!.AsArray())!;
        Assert.Equal(20L, listed["id"]!.GetValue<long>());
        Assert.Equal("failed", listed["state"]!.GetValue<string>());
    }

    [Fact]
    public void SecretsInTitlesAndLogLines_AreRedacted_AndALogLineIsOneLine()
    {
        var s = OrgSummary.Build(Facts(), Now);
        Assert.DoesNotContain(Secret, s.ToJsonString());
        var decision = s["needsYou"]!["teams"]!.AsArray()[1]!["decisions"]!["titles"]!.AsArray().Single()!.GetValue<string>();
        Assert.Equal("Which login provider? [redacted]", decision);
        Assert.Equal("token [redacted]", s["problems"]!.AsArray()[0]!["message"]!.GetValue<string>());
    }

    [Fact]
    public void NoFieldName_SaysTokenCostUsageOrPrice_InTheSnapshotOrThePush()
    {
        var snapshot = OrgSummary.Build(Facts(), Now);
        var push = OrgSummary.ChangeOf(snapshot, OrgSummary.Sections);
        var names = new List<string>();
        Walk(snapshot, names);
        Walk(push, names);
        Assert.True(names.Count > 50, $"only {names.Count} field names walked");
        string[] banned = ["token", "cost", "usage", "price"];
        var bad = names.Where(n => banned.Any(b => n.Contains(b, StringComparison.OrdinalIgnoreCase))).Distinct().ToList();
        Assert.True(bad.Count == 0, "banned field names: " + string.Join(", ", bad));
    }

    private static void Walk(JsonNode? node, List<string> names)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (key, value) in o)
                {
                    names.Add(key);
                    Walk(value, names);
                }
                break;
            case JsonArray a:
                foreach (var item in a) Walk(item, names);
                break;
        }
    }

    /// <summary>The facts' records carry no such figure either, so none can be added to the JSON by accident.</summary>
    [Fact]
    public void NoFactRecord_HasATokenCostUsageOrPriceProperty()
    {
        Type[] records = [typeof(OrgComputer), typeof(OrgRunningAgent), typeof(OrgAgent), typeof(OrgTeam), typeof(OrgWorkflow), typeof(OrgAsk), typeof(OrgProblem), typeof(OrgSummaryFacts)];
        string[] banned = ["token", "cost", "usage", "price"];
        var bad = records.SelectMany(r => r.GetProperties().Select(p => $"{r.Name}.{p.Name}"))
            .Where(n => banned.Any(b => n.Contains(b, StringComparison.OrdinalIgnoreCase))).ToList();
        Assert.Empty(bad);
    }

    public static TheoryData<string> DashboardMethods() =>
        ["org.summary", "org.summary.watch", "org.summary.unwatch", "logs.tail", "logs.untail"];

    [Theory]
    [MemberData(nameof(DashboardMethods))]
    public void RemoteAccess_ListsTheDashboardMethodsAsReads_AVisitorMayCall(string method)
    {
        Assert.Equal(RemoteAccess.Kind.Reads, RemoteAccess.Kinds[method]);
        Assert.True(RemoteAccess.OnAllowList(method));
        Assert.True(RemoteAccess.Reads(method, null));
        Assert.Null(RemoteAccess.Refusal(method, null, allowHostTerminals: false, RemoteAccess.Read));
        Assert.Null(RemoteAccess.Refusal(method, null, allowHostTerminals: false, RemoteAccess.Full));
    }

    [Fact]
    public void TheHello_AnnouncesSummaryAndLogsTail()
    {
        var caps = CloudLink.Hello()["caps"]!.AsArray().Select(c => c!.GetValue<string>()).ToList();
        Assert.Equal(["access", "summary", "logsTail"], caps);
        Assert.Equal("summary", CloudLink.SummaryCap);
        Assert.Equal("logsTail", CloudLink.LogsTailCap);
    }

    [Fact]
    public async Task OrgSummary_AnswersTheSnapshot_WithoutWatching()
    {
        var fx = new Fixture();
        var s = (JsonObject)(await fx.CallAsCloud("alice", "org.summary"))!;
        Assert.Equal(1, s["summaryVersion"]!.GetValue<int>());
        Assert.Equal(0, fx.Service.Watching);
        Assert.False(await fx.Service.TickAsync(Ct));
    }

    [Fact]
    public async Task NothingIsBuiltOrPushed_WhileNobodyWatches()
    {
        var fx = new Fixture();
        fx.Source.Facts = Facts(queued: 9);
        Assert.False(await fx.Service.TickAsync(Ct));
        fx.Clock.Advance(TimeSpan.FromSeconds(10));
        Assert.False(await fx.Service.TickAsync(Ct));
        Assert.Equal(0, fx.Source.Reads);
        Assert.Empty(await fx.PushesAsync(0));
    }

    [Fact]
    public async Task AChange_IsPushedWhileWatched_AtMostOncePer2Seconds_WithOnlyTheChangedSections()
    {
        var fx = new Fixture();
        var watched = (JsonObject)(await fx.CallAsCloud("alice", "org.summary.watch"))!;
        Assert.Equal(1, watched["summaryVersion"]!.GetValue<int>());
        Assert.Equal(1, fx.Service.Watching);

        // Nothing changed (the uptime alone ticks): no push.
        fx.Clock.Advance(TimeSpan.FromSeconds(5));
        Assert.False(await fx.Service.TickAsync(Ct));

        fx.Source.Facts = Facts(queued: 3);
        Assert.True(await fx.Service.TickAsync(Ct));
        // More changes within 2 s wait for the next beat.
        fx.Source.Facts = Facts(queued: 3, extraDecision: "Ship it?");
        fx.Clock.Advance(TimeSpan.FromSeconds(1.5));
        Assert.False(await fx.Service.TickAsync(Ct));
        fx.Clock.Advance(TimeSpan.FromSeconds(0.5));
        Assert.True(await fx.Service.TickAsync(Ct));

        var pushes = await fx.PushesAsync(2);
        Assert.Equal(2, pushes.Count);
        Assert.All(pushes, p => Assert.Equal(OrgSummaryService.ChangedEvent, p["event"]!.GetValue<string>()));
        var one = pushes[0]["data"]!;
        Assert.Equal(1, one["summaryVersion"]!.GetValue<int>());
        Assert.False(one["full"]!.GetValue<bool>());
        Assert.Equal(["computer"], one["changed"]!.AsArray().Select(c => c!.GetValue<string>()));
        Assert.Equal(3, one["sections"]!["computer"]!["queuedRuns"]!.GetValue<int>());
        Assert.Null(one["sections"]!["teams"]);
        var two = pushes[1]["data"]!;
        Assert.Equal(["needsYou"], two["changed"]!.AsArray().Select(c => c!.GetValue<string>()));
        Assert.Equal(2, two["sections"]!["needsYou"]!["decisions"]!.GetValue<int>());
    }

    [Fact]
    public async Task Unwatch_StopsThePushes_AndIsPerViewer()
    {
        var fx = new Fixture();
        await fx.CallAsCloud("alice", "org.summary.watch");
        await fx.CallAsCloud("bob", "org.summary.watch");
        Assert.Equal(2, fx.Service.Watching);
        await fx.CallAsCloud("alice", "org.summary.unwatch");
        Assert.Equal(1, fx.Service.Watching);
        fx.Source.Facts = Facts(queued: 4);
        fx.Clock.Advance(TimeSpan.FromSeconds(3));
        Assert.True(await fx.Service.TickAsync(Ct));

        var unwatched = await fx.CallAsCloud("bob", "org.summary.unwatch");
        Assert.False(unwatched!["watching"]!.GetValue<bool>());
        Assert.Equal(0, fx.Service.Watching);
        var reads = fx.Source.Reads;
        fx.Source.Facts = Facts(queued: 5);
        fx.Clock.Advance(TimeSpan.FromSeconds(3));
        Assert.False(await fx.Service.TickAsync(Ct));
        Assert.Equal(reads, fx.Source.Reads);
    }

    [Fact]
    public async Task AViewerLeavingTheLink_OrTheLinkDropping_IsAnUnwatch()
    {
        var fx = new Fixture();
        using var dir = new TempDb();
        await using var link = new CloudLink(dir.Dir, (m, p, ct) => fx.Service.DispatchAsync(m, p, ct), new AppEvents());
        link.ViewersChanged += fx.Service.ViewersPresent;

        // Through the link as a Visitor (read access): watching is a read, so it runs.
        await Call(link, "s1", "alice", "org.summary.watch");
        await Call(link, "s2", "bob", "org.summary.watch");
        await fx.CallAs(Actor.Window, "org.summary.watch");
        Assert.Equal(3, fx.Service.Watching);

        link.ViewersNow(["bob"]); // alice closed her tab
        Assert.Equal(2, fx.Service.Watching);
        link.ViewersNow([]); // the connection ended: every cloud watch ends, the window's stays
        Assert.Equal(1, fx.Service.Watching);
        await fx.CallAs(Actor.Window, "org.summary.unwatch");
        Assert.Equal(0, fx.Service.Watching);
    }

    private static async Task Call(CloudLink link, string session, string user, string method)
    {
        JsonObject? reply = null;
        await link.AnswerAsync(new JsonObject
        {
            ["type"] = "call", ["session"] = session, ["id"] = 1, ["method"] = method, ["user"] = user, ["access"] = "read",
        }, m => { reply = m; return Task.CompletedTask; }, Ct);
        Assert.NotNull(reply);
        Assert.Null(reply!["error"]);
        Assert.Equal(1, reply["result"]!["summaryVersion"]!.GetValue<int>());
    }

    [Fact]
    public void AWorkflowsState_ComesFromTheBus_TheBoardAndTheAsks()
    {
        WorkflowSummary W(string state, bool undeclared = false, params string[] busy) =>
            new(1, "t", "Manager", state, undeclared, busy, null, Now, Now, 1);
        Assert.Equal("completed", AppOrgSummarySource.StateOf(W("completed"), true, true, true));
        Assert.Equal("closed", AppOrgSummarySource.StateOf(W("closed"), false, false, false));
        Assert.Equal("waitingOnPerson", AppOrgSummarySource.StateOf(W("open", busy: "Ada is running"), true, false, false));
        Assert.Equal("running", AppOrgSummarySource.StateOf(W("open", busy: "Ada is running"), false, true, false));
        Assert.Equal("blocked", AppOrgSummarySource.StateOf(W("open", undeclared: true), false, true, false));
        Assert.Equal("failed", AppOrgSummarySource.StateOf(W("open", undeclared: true), false, false, true));
        Assert.Equal("nobodyWorking", AppOrgSummarySource.StateOf(W("open", undeclared: true), false, false, false));
        Assert.All(OrgSummary.WorkflowStates, s => Assert.Matches("^[a-z][A-Za-z]+$", s));
    }

    [Theory]
    [InlineData("Runs", "run failed", "error")]
    [InlineData("Runs", "run start_failed", "error")]
    [InlineData("Runs", "run timeout", "warning")]
    [InlineData("Runs", "run completed", null)]
    [InlineData("Network", "host_denied", "warning")]
    [InlineData("Network", "allowed", null)]
    [InlineData("Model usage", "claude-opus failed", null)]
    [InlineData("Messages", "agent.failed", null)]
    public void AnActivityRowsLevel_NeverReadsModelUsage(string source, string ev, string? level) =>
        Assert.Equal(level, AppOrgSummarySource.ProblemLevel(source, ev));

    [Theory]
    [InlineData("bus: run 4 failed: exit 1", "error")]
    [InlineData("Cloud Link: connection ended: Unhandled exception", "error")]
    [InlineData("board: could not project: busy", "warning")]
    [InlineData("core started", null)]
    public void ADiagnosticsLinesLevel(string text, string? level) => Assert.Equal(level, AppOrgSummarySource.DiagnosticLevel(text));

    private sealed class FakeSource : IOrgSummarySource
    {
        public OrgSummaryFacts Facts { get; set; } = OrgSummaryTests.Facts();
        public int Reads;

        public Task<OrgSummaryFacts> ReadAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Reads);
            return Task.FromResult(Facts);
        }
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class Fixture
    {
        public FakeSource Source { get; } = new();
        public ManualClock Clock { get; } = new(Now);
        public AppEvents Events { get; } = new();
        public OrgSummaryService Service { get; }
        private readonly List<JsonObject> _pushes = [];

        public Fixture()
        {
            Service = new OrgSummaryService(Source, Events, Clock);
            Events.Subscribe(m =>
            {
                lock (_pushes) _pushes.Add(m);
                return Task.CompletedTask;
            });
        }

        public Task<JsonNode?> CallAsCloud(string user, string method) => CallAs(Actor.CloudViewer(user), method);

        public async Task<JsonNode?> CallAs(Actor actor, string method)
        {
            using var acting = Actor.Use(actor);
            return await Service.DispatchAsync(method, null, Ct);
        }

        /// <summary>The pushes so far, once <paramref name="expected"/> arrived (delivery is asynchronous), or after a short wait.</summary>
        public async Task<List<JsonObject>> PushesAsync(int expected)
        {
            for (var i = 0; i < 100; i++)
            {
                lock (_pushes) if (_pushes.Count >= expected && (expected > 0 || i >= 10)) return [.. _pushes];
                await Task.Delay(20, Ct);
            }
            lock (_pushes) return [.. _pushes];
        }
    }
}
