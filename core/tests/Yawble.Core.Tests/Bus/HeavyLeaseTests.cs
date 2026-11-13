using System.Text;
using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Gateway;
using Yawble.Core.Launch;
using Yawble.Core.Policy;
using Yawble.Core.Skills;
using Yawble.Core.Tests.Gateway;
using Xunit;

namespace Yawble.Core.Tests.Bus;

/// <summary>
/// The heavy-work lease: a queue in order, N holders, hand-over on release / run end / the 30-minute
/// limit, the new holder told in its run, the idle clock held while queued, and what the UI and the skills say.
/// Fakes only: scripted runs that wait until the test ends them, a fake clock, a fake idle clock.
/// </summary>
public class HeavyLeaseTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Sentence = "Before running the repository's full test suite, a full build, an image build or anything else you know is heavy, " +
                                    "take the heavy-work lease with `lease` (action acquire, what = the step); release it as soon as that command finishes. " +
                                    "While iterating run only the tests you touched; run the full suite once when finished.";

    private sealed class FakeIdle : IRunIdleClock
    {
        public bool Held { get; private set; }
        public int Resumes { get; private set; }
        public void Hold() => Held = true;
        public void Resume() { Held = false; Resumes++; }
    }

    /// <summary>A team Alpha with members Ada, Ben, Cy, Dee whose runs wait until the test ends them.</summary>
    private sealed class Kit : IAsyncDisposable
    {
        private readonly TempDb _db = new();
        private readonly Dictionary<string, TaskCompletionSource<RunContext>> _started = [];
        private readonly Dictionary<string, TaskCompletionSource> _end = [];
        public FakeClock Clock { get; } = new(new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero));
        public BusEngine Engine { get; private set; } = null!;
        public Dictionary<string, FakeIdle> Idle { get; } = [];
        public int Changes;

        public static Task<Kit> StartAsync(int atOnce = 1) => StartAsync(() => atOnce);

        public static async Task<Kit> StartAsync(Func<int> atOnce)
        {
            var kit = new Kit();
            string[] names = ["Ada", "Ben", "Cy", "Dee"];
            var runner = new ScriptedRunner();
            foreach (var n in names)
            {
                kit._started[n] = new(TaskCreationOptions.RunContinuationsAsynchronously);
                kit._end[n] = new(TaskCreationOptions.RunContinuationsAsynchronously);
                kit.Idle[n] = new FakeIdle();
                runner.On("Alpha/" + n, async (run, ct) =>
                {
                    run.IdleClock = kit.Idle[n];
                    kit._started[n].TrySetResult(run);
                    await kit._end[n].Task.WaitAsync(ct);
                    return RunResult.Ok;
                });
            }
            kit.Engine = new BusEngine(new BusStore(kit._db.ConnectionString, kit.Clock), runner, new BusOptions
            {
                SafetyPoll = TimeSpan.FromMilliseconds(100),
                IdleOffer = false,
                HeavyWorkAtOnce = atOnce,
            });
            kit.Engine.LeaseChanged = () => Interlocked.Increment(ref kit.Changes);
            await kit.Engine.CreateTeamAsync("Alpha", ct: Ct);
            foreach (var n in names) await kit.Engine.AddMemberAsync("Alpha", n, ct: Ct);
            await kit.Engine.StartAsync(Ct);
            return kit;
        }

        public async Task<RunContext> RunAsync(string member)
        {
            await Engine.PersonTellAsync("Alpha", member, "work", ct: Ct);
            return await _started[member].Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        }

        public async Task EndAsync(string member, RunContext run)
        {
            _end[member].TrySetResult();
            await Wait.UntilAsync(async () => (await Engine.AgentCardsAsync(1, Ct)).Single(c => c.Member.Name == member).RunId != run.RunId,
                member + "'s run to end");
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var e in _end.Values) e.TrySetResult();
            await Engine.DisposeAsync();
            _db.Dispose();
        }
    }

    private static async Task<(string Text, bool Error)> Call(RunContext run, string tool, object args)
    {
        var body = new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = System.Text.Json.JsonSerializer.SerializeToNode(args) },
        };
        var reply = await new McpBusServer(run).HandleAsync(Encoding.UTF8.GetBytes(body.ToJsonString()), Ct);
        var result = JsonNode.Parse(reply.Json!)!["result"]!;
        return (result["content"]![0]!["text"]!.GetValue<string>(), result["isError"]!.GetValue<bool>());
    }

    private static async Task<string> Lease(RunContext run, string action, string? what = null) =>
        (await Call(run, "lease", what is null ? new { action } : new { action, what })).Text;

    private static async Task<string[]> Holders(BusEngine engine) => [.. (await engine.LeaseSnapshotAsync(Ct)).Holders.Select(h => h.Member)];

    private static async Task<string[]> Queue(BusEngine engine) => [.. (await engine.LeaseSnapshotAsync(Ct)).Queue.Select(h => h.Member)];

    [Fact]
    public async Task TheQueueKeepsOrder_AndAskingAgainKeepsYourPlace()
    {
        await using var kit = await Kit.StartAsync();
        var ada = await kit.RunAsync("Ada");
        var ben = await kit.RunAsync("Ben");
        var cy = await kit.RunAsync("Cy");
        Assert.StartsWith("ok: you hold the heavy-work lease for \"full test suite\"", await Lease(ada, "acquire", "full test suite"));
        Assert.StartsWith("queued, position 1; Alpha/Ada holds it for \"full test suite\" since 09:00 UTC (0 min). Keep working on something else, or check with lease status",
            await Lease(ben, "acquire", "build"));
        Assert.StartsWith("queued, position 2; ", await Lease(cy, "acquire", "image build"));
        Assert.StartsWith("queued, position 1; ", await Lease(ben, "acquire", "build again")); // keeps its place
        Assert.StartsWith("ok: you hold", await Lease(ada, "acquire", "x")); // a holder asking again still holds it
        Assert.Equal(["Ada"], await Holders(kit.Engine));
        Assert.Equal(["Ben", "Cy"], await Queue(kit.Engine));
        Assert.Equal("build", (await kit.Engine.LeaseSnapshotAsync(Ct)).Queue[0].What); // the first ask's step is kept
    }

    [Fact]
    public async Task AReleaseHandsItToTheNext_WhoIsToldInItsNextToolReply()
    {
        await using var kit = await Kit.StartAsync();
        var ada = await kit.RunAsync("Ada");
        var ben = await kit.RunAsync("Ben");
        await Lease(ada, "acquire", "full test suite");
        await Lease(ben, "acquire", "build");
        var before = kit.Changes;
        Assert.Equal("ok: released.", await Lease(ada, "release"));
        Assert.True(kit.Changes > before, "a change event");
        Assert.Equal(["Ben"], await Holders(kit.Engine));
        Assert.Empty(await Queue(kit.Engine));
        var (text, error) = await Call(ben, "progress", new { status = "still working" });
        Assert.False(error);
        Assert.StartsWith("[heavy-work lease] You now hold the heavy-work lease for \"build\"; run it, then release.", text);
        Assert.Contains("ok: message", text);
        Assert.DoesNotContain("heavy-work lease", (await Call(ben, "progress", new { status = "again" })).Text); // told once
        Assert.StartsWith("ok: you hold the heavy-work lease for \"build\"", await Lease(ben, "status"));
    }

    [Fact]
    public async Task AHolderWhoseRunEnds_HandsItOver()
    {
        await using var kit = await Kit.StartAsync();
        var ada = await kit.RunAsync("Ada");
        var ben = await kit.RunAsync("Ben");
        await Lease(ada, "acquire", "full test suite");
        await Lease(ben, "acquire", "build");
        await kit.EndAsync("Ada", ada);
        Assert.Equal(["Ben"], await Holders(kit.Engine));
        Assert.StartsWith("ok: you hold the heavy-work lease for \"build\"", await Lease(ben, "acquire", "build"));
    }

    [Fact]
    public async Task AQueuedRunThatEnds_DropsOut_AndAQueuedReleaseLeavesTheQueue()
    {
        await using var kit = await Kit.StartAsync();
        var ada = await kit.RunAsync("Ada");
        var ben = await kit.RunAsync("Ben");
        var cy = await kit.RunAsync("Cy");
        var dee = await kit.RunAsync("Dee");
        await Lease(ada, "acquire", "suite");
        await Lease(ben, "acquire", "build");
        await Lease(cy, "acquire", "image");
        await Lease(dee, "acquire", "lint all");
        await kit.EndAsync("Ben", ben);
        Assert.Equal(["Cy", "Dee"], await Queue(kit.Engine));
        Assert.Equal("ok: you left the queue.", await Lease(cy, "release"));
        Assert.Equal(["Dee"], await Queue(kit.Engine));
        await Lease(ada, "release");
        Assert.Equal(["Dee"], await Holders(kit.Engine));
    }

    [Fact]
    public async Task TheThirtyMinuteLimit_HandsItOver_AndTellsTheOldHolder()
    {
        await using var kit = await Kit.StartAsync();
        var ada = await kit.RunAsync("Ada");
        var ben = await kit.RunAsync("Ben");
        await Lease(ada, "acquire", "suite");
        await Lease(ben, "acquire", "build");
        kit.Clock.Advance(TimeSpan.FromMinutes(29));
        Assert.Equal(["Ada"], await Holders(kit.Engine));
        kit.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(["Ben"], await Holders(kit.Engine));
        Assert.Equal(new DateTimeOffset(2026, 11, 12, 9, 31, 0, TimeSpan.Zero).ToString("O"), (await kit.Engine.LeaseSnapshotAsync(Ct)).Holders[0].Since);
        var (text, _) = await Call(ada, "progress", new { status = "s" });
        Assert.StartsWith("[heavy-work lease] Your heavy-work lease for \"suite\" passed its 30-minute limit", text);
        Assert.StartsWith("queued, position 1; Alpha/Ben holds it", await Lease(ada, "acquire", "suite"));
    }

    [Fact]
    public async Task ALapsedLeasesNotice_IsSaidOnce_AndNotAfterTakingItAgain()
    {
        await using var kit = await Kit.StartAsync();
        var ada = await kit.RunAsync("Ada");
        await Lease(ada, "acquire", "suite");
        kit.Clock.Advance(TimeSpan.FromMinutes(31));
        Assert.Empty(await Holders(kit.Engine));
        // Nobody waited: it was released, not handed on. Status says so once.
        var told = await Lease(ada, "status");
        Assert.StartsWith("Your heavy-work lease for \"suite\" passed its 30-minute limit and was released. Take it again", told);
        Assert.EndsWith("Now: free (1 at once); nobody waiting.", told);
        Assert.Equal("free (1 at once); nobody waiting.", await Lease(ada, "status"));
        // Lapsed again, then taken again: no stale banner on the next tool call.
        await Lease(ada, "acquire", "suite");
        kit.Clock.Advance(TimeSpan.FromMinutes(31));
        Assert.Empty(await Holders(kit.Engine));
        Assert.StartsWith("ok: you hold", await Lease(ada, "acquire", "suite"));
        Assert.DoesNotContain("heavy-work lease]", (await Call(ada, "progress", new { status = "s" })).Text);
    }

    [Fact]
    public async Task TheStepIsOneLine_AtMost80Characters_AndQuoted()
    {
        await using var kit = await Kit.StartAsync();
        var ada = await kit.RunAsync("Ada");
        var ben = await kit.RunAsync("Ben");
        Assert.Contains("for \"full test suite\"", await Lease(ada, "acquire", "  full\n  test \t  suite \r\n"));
        await Lease(ben, "acquire", new string('x', 200));
        var what = (await kit.Engine.LeaseSnapshotAsync(Ct)).Queue.Single().What;
        Assert.Equal(80, what.Length);
        Assert.EndsWith("…", what);
        Assert.Contains($"for \"{what}\"", await Lease(ben, "status"));
        Assert.Equal("a heavy step", BusEngine.NormalizeWhat(" \n "));
    }

    [Fact]
    public async Task HeavyAtOnceIsReadOnce_AndAgainAfterAPolicySave()
    {
        var reads = 0;
        var atOnce = 1;
        await using var kit = await Kit.StartAsync(() => { Interlocked.Increment(ref reads); return atOnce; });
        var ada = await kit.RunAsync("Ada");
        var ben = await kit.RunAsync("Ben");
        await Lease(ada, "acquire", "suite");
        await Lease(ben, "acquire", "build");
        for (var i = 0; i < 5; i++) await Lease(ben, "status");
        await Task.Delay(300, Ct); // a few pump passes
        Assert.Equal(1, reads);
        atOnce = 2;
        kit.Engine.Reschedule(); // what saving the global policy calls
        await Wait.UntilAsync(async () => (await Holders(kit.Engine)).Length == 2, "the raised limit to hand Ben the lease");
        Assert.Equal(["Ada", "Ben"], await Holders(kit.Engine));
    }

    [Fact]
    public async Task TwoAtOnce_LetsTwoHoldIt_AndTheThirdWaits()
    {
        await using var kit = await Kit.StartAsync(atOnce: 2);
        var ada = await kit.RunAsync("Ada");
        var ben = await kit.RunAsync("Ben");
        var cy = await kit.RunAsync("Cy");
        Assert.StartsWith("ok: you hold", await Lease(ada, "acquire", "suite"));
        Assert.StartsWith("ok: you hold", await Lease(ben, "acquire", "build"));
        var queued = await Lease(cy, "acquire", "image");
        Assert.StartsWith("queued, position 1; Alpha/Ada holds it for \"suite\" since 09:00 UTC (0 min); Alpha/Ben holds it for \"build\"", queued);
        Assert.Equal(2, (await kit.Engine.LeaseSnapshotAsync(Ct)).Limit);
        await Lease(ben, "release");
        Assert.Equal(["Ada", "Cy"], await Holders(kit.Engine));
    }

    [Fact]
    public async Task StatusSaysWhoHoldsItAndWhoWaits_ForAgents_SystemStatus_AndTheCards()
    {
        await using var kit = await Kit.StartAsync();
        var ada = await kit.RunAsync("Ada");
        var ben = await kit.RunAsync("Ben");
        Assert.Equal("free (1 at once); nobody waiting.", await Lease(ada, "status"));
        await Lease(ada, "acquire", "full test suite");
        kit.Clock.Advance(TimeSpan.FromMinutes(5));
        await Lease(ben, "acquire", "build");
        Assert.Equal("Alpha/Ada holds it for \"full test suite\" since 09:00 UTC (5 min) (1 of 1 at once); waiting: 1. Alpha/Ben (you) for \"build\" since 09:05 UTC.",
            await Lease(ben, "status"));
        Assert.Equal("Alpha/Ada holds it for \"full test suite\" since 09:00 UTC (5 min) (1 of 1 at once); waiting: 1. Alpha/Ben for \"build\" since 09:05 UTC.",
            await kit.Engine.LeaseStatusTextAsync(Ct));
        var cards = await kit.Engine.AgentCardsAsync(1, Ct);
        var adaCard = cards.Single(c => c.Member.Name == "Ada");
        var benCard = cards.Single(c => c.Member.Name == "Ben");
        Assert.Equal(("holding", "full test suite", (int?)null), (adaCard.Lease, adaCard.LeaseWhat, adaCard.LeasePosition));
        Assert.Equal(("waiting", "build", (int?)1), (benCard.Lease, benCard.LeaseWhat, benCard.LeasePosition));
        Assert.Null(cards.Single(c => c.Member.Name == "Cy").Lease);
        var team = await kit.Engine.TeamStatusAsync("Alpha", Ct);
        Assert.Equal("waiting for the heavy-work lease (position 1)", team.Single(m => m.Name == "Ben").HeavyLease);
        var snap = await kit.Engine.LeaseSnapshotAsync(Ct);
        Assert.Equal(new LeaseEntry("Alpha", "Ada", "full test suite", "2026-11-12T09:00:00.0000000+00:00"), snap.Holders.Single());
        Assert.Equal(new LeaseEntry("Alpha", "Ben", "build", "2026-11-12T09:05:00.0000000+00:00", 1), snap.Queue.Single());
    }

    [Fact]
    public async Task AQueuedRunsIdleClockIsHeld_AndGetsAFullWindowWhenGrantedOrWhenItLeaves()
    {
        await using var kit = await Kit.StartAsync();
        var ada = await kit.RunAsync("Ada");
        var ben = await kit.RunAsync("Ben");
        var cy = await kit.RunAsync("Cy");
        await Lease(ada, "acquire", "suite");
        Assert.False(kit.Idle["Ada"].Held); // a holder works: its clock runs
        await Lease(ben, "acquire", "build");
        await Lease(cy, "acquire", "image");
        Assert.True(kit.Idle["Ben"].Held);
        Assert.True(kit.Idle["Cy"].Held);
        await Lease(ada, "release");
        Assert.False(kit.Idle["Ben"].Held);
        Assert.Equal(1, kit.Idle["Ben"].Resumes);
        Assert.True(kit.Idle["Cy"].Held);
        await Lease(cy, "release");
        Assert.False(kit.Idle["Cy"].Held);
    }

    [Fact]
    public void RunActivity_Held_ShowsNoIdleTime_AndResumeStartsAFullWindow()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero));
        var activity = new RunActivity(clock);
        activity.Hold();
        clock.Advance(TimeSpan.FromHours(3));
        Assert.Equal(TimeSpan.Zero, clock.GetUtcNow() - activity.Last); // the watchdog sees no idle time
        activity.Resume();
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(TimeSpan.FromMinutes(10), clock.GetUtcNow() - activity.Last);
        Assert.False(activity.Held);
    }

    [Fact]
    public void TheSkillsTellAgentsToUseIt_AndAnExistingDbsCustomSkillIsKept()
    {
        foreach (var name in new[] { "member", "manager", "dev-tools", "testing-a-web-app", "testing-console-apps" })
        {
            var body = SkillCatalog.BuiltIns.Single(s => s.Name == name).Body;
            Assert.Contains(Sentence, string.Join(' ', body.Split((char[])['\n', ' '], StringSplitOptions.RemoveEmptyEntries)));
        }
        // An existing database (migrated, with a skill the person customized): built-ins come from the core itself,
        // so the new text reaches it at once, and the person's own dev-tools still wins.
        using var db = new TempDb();
        var skills = new SkillCatalog(db.ConnectionString);
        skills.Save("dev-tools", "Our own tools.", ["any"], "Use our tools.", null);
        var offered = skills.Offered("member", 1);
        Assert.Equal("Use our tools.", offered.Single(s => s.Name == "dev-tools").Body);
        Assert.Contains("heavy-work lease", offered.Single(s => s.Name == "member").Body);
        Assert.Contains("heavy-work lease", offered.Single(s => s.Name == "testing-a-web-app").Body);
    }

    [Fact]
    public void HeavyWorkAtOnce_IsAGlobalPolicySetting_DefaultOne_OneToEight()
    {
        Assert.Equal(1, GlobalPolicy.Default.App.HeavyWorkAtOnce);
        var json = GlobalPolicy.Default.ToJson();
        Assert.Equal(1, json["app"]!["heavyWorkAtOnce"]!.GetValue<int>());
        json["app"]!["heavyWorkAtOnce"] = 3;
        Assert.Equal(3, GlobalPolicy.Parse(json.ToJsonString()).App.HeavyWorkAtOnce);
        json["app"]!.AsObject().Remove("heavyWorkAtOnce");
        Assert.Equal(1, GlobalPolicy.Parse(json.ToJsonString()).App.HeavyWorkAtOnce); // a saved policy from before
        Assert.Empty(PolicyMerger.CheckGlobal(GlobalPolicy.Default with { App = GlobalPolicy.Default.App with { HeavyWorkAtOnce = 8 } }));
        Assert.Contains(PolicyMerger.CheckGlobal(GlobalPolicy.Default with { App = GlobalPolicy.Default.App with { HeavyWorkAtOnce = 9 } }),
            e => e.Contains("heavyWorkAtOnce must be 1..8"));
        Assert.NotEmpty(PolicyMerger.CheckGlobal(GlobalPolicy.Default with { App = GlobalPolicy.Default.App with { HeavyWorkAtOnce = 0 } }));
    }
}
