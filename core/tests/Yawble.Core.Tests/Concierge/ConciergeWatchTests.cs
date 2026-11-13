using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Concierge;
using Yawble.Core.Launch;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Ui;
using Xunit;

namespace Yawble.Core.Tests.Concierge;

/// <summary>
/// The Concierge keeps tabs between turns: what it dispatched (and teams the person asked it to watch) is matched against
/// the bus by the core, with no agent running, and posted into the conversation as updates; a follow-up watch wakes it
/// for a review turn.
/// </summary>
public class ConciergeWatchTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Rig : IAsyncDisposable
    {
        public TempDb Db { get; } = new();
        public BusEngine Bus { get; private set; } = null!;
        public BusStore Writer { get; private set; } = null!;
        public ConciergeStore Store { get; private set; } = null!;
        public ConciergeService Service { get; private set; } = null!;
        public List<JsonObject> Pushes { get; } = [];
        public List<LaunchRequest> Launched { get; } = [];
        public ManualClock Clock { get; } = new(DateTimeOffset.UtcNow);
        private IDisposable? _subscription;

        public static async Task<Rig> StartAsync()
        {
            var rig = new Rig();
            rig.Bus = new BusEngine(rig.Db.OpenStore(), new UnavailableRunner());
            await rig.Bus.StartAsync(Ct);
            await rig.Bus.CreateTeamAsync("alpha", "Manager", "claude", null, Ct);
            await rig.Bus.CreateTeamAsync("beta", "Manager", "claude", null, Ct);
            rig.Writer = rig.Db.OpenStore();
            rig.Store = new ConciergeStore(rig.Db.ConnectionString, rig.Clock);
            var events = new AppEvents();
            rig._subscription = events.Subscribe(m => { lock (rig.Pushes) rig.Pushes.Add(m); return Task.CompletedTask; });
            rig.Service = new ConciergeService(rig.Store, rig.Bus,
                (request, _) =>
                {
                    lock (rig.Launched) rig.Launched.Add(request);
                    return Task.FromResult(new LaunchOutcome("r-x", RunEnd.StartFailed, "no agents in this test", null, []));
                },
                new RunRegistry(), events, rig.UiAsync, _ => Task.FromResult("claude"), rig.Clock, followUpAfter: TimeSpan.Zero);
            return rig;
        }

        /// <summary>The person's own operations, as the Concierge's tools call them: tell runs the real bus tell.</summary>
        public async Task<JsonNode?> UiAsync(string method, JsonObject? p, CancellationToken ct)
        {
            if (method != "bus.tellManager") throw new InvalidOperationException(method);
            var team = (await Bus.TeamsAsync(ct)).Single(t => t.Id == p!["teamId"]!.GetValue<long>());
            var m = await Bus.PersonTellAsync(team.Name, "Manager", p!["text"]!.GetValue<string>(), null, ct);
            return new JsonObject { ["seq"] = m.Seq, ["workflow"] = m.CorrelationId };
        }

        public ConciergeTools Tools(string conversationId) => new(Bus, Store, UiAsync, () => null, conversationId, Clock);

        public Message Post(string type, object payload, string source, long? causation = null) =>
            Writer.Append(new NewMessage(type, JsonSerializer.Serialize(payload), source, causation));

        public JsonObject[] Updates()
        {
            lock (Pushes) return Pushes.Where(p => p["event"]!.GetValue<string>() == ConciergeService.UpdateEvent).ToArray();
        }

        public async ValueTask DisposeAsync()
        {
            _subscription?.Dispose();
            Writer.Dispose();
            await Bus.DisposeAsync();
            Db.Dispose();
        }
    }

    [Fact]
    public async Task WhatTheConciergeDispatchedIsFollowedUp_EachEventPostedOnce_AndTheWatchEndsWithTheWorkflow()
    {
        await using var rig = await Rig.StartAsync();
        rig.Store.Create("c1", "README for alpha", "claude", null);
        var said = await CallAsync(rig.Tools("c1"), "tell", new JsonObject { ["team"] = "alpha", ["instruction"] = "Add a README\nwith setup steps" });
        Assert.Contains("watches this workflow", said);
        var watch = Assert.Single(rig.Store.ActiveWatches("c1"));
        Assert.Equal(("alpha", "Add a README", false), (watch.Team, watch.What, watch.FollowUp));
        var workflow = watch.Workflow!.Value;

        rig.Post(MessageTypes.Progress, new { text = "writing" }, "alpha/dev", workflow);            // not something to report
        rig.Post(MessageTypes.NeedsDecision, new { question = "MIT or Apache?" }, "alpha/dev", workflow);
        rig.Post(MessageTypes.Failed, new { reason = "unrelated" }, "beta/dev");                     // another team, not watched
        await rig.Service.TickAsync(Ct);
        rig.Post(MessageTypes.WorkflowCompleted, new { delivered = "README.md added" }, "alpha/Manager", workflow);
        await rig.Service.TickAsync(Ct);
        await rig.Service.TickAsync(Ct);                                                              // nothing new: nothing posted

        // This bus has no agent runner, so the Manager's own run fails to start: that is reported too (from alpha/Manager).
        var all = rig.Store.Messages("c1").Where(m => m.Role == "update").ToList();
        Assert.All(all.Where(u => u.Kind == "failed"), u => Assert.StartsWith("**alpha** · Manager", u.Text));
        var updates = all.Where(u => u.Kind != "failed").ToList();
        Assert.Equal(["decision", "done"], updates.Select(u => u.Kind));
        Assert.Contains("**alpha** · dev needs your decision", updates[0].Text);
        Assert.Contains("MIT or Apache?", updates[0].Text);
        Assert.Equal("**alpha** finished: README.md added", updates[1].Text);
        Assert.Empty(rig.Store.ActiveWatches("c1"));                                                  // the workflow is done
        Assert.Equal(all.Count, rig.Store.Get("c1")!.Unread);
        var pushes = rig.Updates();
        Assert.Equal(all.Count, pushes.Length);
        Assert.All(pushes, p => Assert.Equal("c1", p["data"]!["conversationId"]!.GetValue<string>()));
        Assert.Equal("update", pushes[^1]["data"]!["message"]!["role"]!.GetValue<string>());
        Assert.Equal("done", pushes[^1]["data"]!["message"]!["kind"]!.GetValue<string>());
        Assert.Equal(all.Count, pushes[^1]["data"]!["unread"]!.GetValue<int>());
        Assert.Empty(rig.Launched);                                                                   // no agent ran for any of it

        await rig.Service.DispatchAsync("concierge.read", new JsonObject { ["id"] = "c1" }, Ct);
        Assert.Equal(0, rig.Store.Get("c1")!.Unread);
    }

    [Fact]
    public async Task ATeamWatchWithFollowUpWakesTheConciergeToReviewWhatArrived()
    {
        await using var rig = await Rig.StartAsync();
        rig.Store.Create("c2", "Keep an eye on beta", "claude", null);
        rig.Post(MessageTypes.Failed, new { reason = "before the watch" }, "beta/dev");               // older: not reported
        var said = await CallAsync(rig.Tools("c2"), "watch", new JsonObject { ["team"] = "beta", ["what"] = "anything that breaks", ["follow_up"] = true, ["hours"] = 4 });
        Assert.Contains("for 4 hours", said);
        Assert.Contains("team beta: anything that breaks (you review each update)", await CallAsync(rig.Tools("c2"), "watch_list", []));

        rig.Post(MessageTypes.Failed, new { reason = "tests failed", failureClass = "timeout" }, "beta/dev");
        await rig.Service.TickAsync(Ct);   // posts the update and, with no settle time in this test, starts the review turn

        var update = Assert.Single(rig.Store.Messages("c2"), m => m.Role == "update");
        Assert.Equal("**beta** · dev's run ended (timeout): tests failed", update.Text);
        await Wait.UntilAsync(() => { lock (rig.Launched) return rig.Launched.Count == 1; }, "the review turn");
        var prompt = rig.Launched[0].Prompt!;
        Assert.Contains("**Update** (", prompt);
        Assert.Contains("tests failed", prompt);
        Assert.Contains("No message from the person this time", prompt);
        Assert.DoesNotContain("## The person now says", prompt);
        await Wait.UntilAsync(() => rig.Store.Messages("c2").Any(m => m.Role == "assistant"), "the review kept");
        Assert.Single(rig.Store.ActiveWatches("c2"));                                                  // a team watch goes on

        // The person can stop it; the Concierge cannot stop another conversation's watch.
        var id = rig.Store.ActiveWatches("c2")[0].Id;
        rig.Store.Create("c3", "Other", "claude", null);
        Assert.Contains("no watch", await CallAsync(rig.Tools("c3"), "unwatch", new JsonObject { ["id"] = id }));
        Assert.True((await rig.Service.DispatchAsync("concierge.unwatch", new JsonObject { ["id"] = "c2", ["watchId"] = id }, Ct))!["stopped"]!.GetValue<bool>());
        Assert.Empty(rig.Store.ActiveWatches("c2"));
    }

    [Fact]
    public async Task AWatchThatHearsNothingExpires_AndSaysSo()
    {
        await using var rig = await Rig.StartAsync();
        rig.Store.Create("c4", "Quiet", "claude", null);
        await CallAsync(rig.Tools("c4"), "watch", new JsonObject { ["team"] = "alpha", ["what"] = "the release", ["hours"] = 1 });
        await rig.Service.TickAsync(Ct);
        Assert.Single(rig.Store.ActiveWatches("c4"));
        rig.Clock.Now += TimeSpan.FromHours(2);
        await rig.Service.TickAsync(Ct);
        Assert.Empty(rig.Store.ActiveWatches("c4"));
        var update = Assert.Single(rig.Store.Messages("c4"), m => m.Role == "update");
        Assert.Equal("expired", update.Kind);
        Assert.Contains("the release", update.Text);
    }

    [Fact]
    public async Task TheConversationShowsItsWatches_AndThePersonCanTurnTheReviewOnOrOff()
    {
        await using var rig = await Rig.StartAsync();
        rig.Store.Create("c5", "Watches", "claude", null);
        await CallAsync(rig.Tools("c5"), "tell", new JsonObject { ["team"] = "alpha", ["instruction"] = "Ship it" });
        var c = (await rig.Service.DispatchAsync("concierge.get", new JsonObject { ["id"] = "c5" }, Ct))!;
        var w = Assert.Single(c["watches"]!.AsArray())!;
        Assert.Equal("Ship it", w["what"]!.GetValue<string>());
        Assert.False(w["followUp"]!.GetValue<bool>());
        var changed = await rig.Service.DispatchAsync("concierge.setWatch", new JsonObject { ["id"] = "c5", ["watchId"] = w["id"]!.GetValue<long>(), ["followUp"] = true }, Ct);
        Assert.True(changed!["followUp"]!.GetValue<bool>());
        var summary = Assert.Single((await rig.Service.DispatchAsync("concierge.list", null, Ct))!.AsArray())!;
        Assert.Equal(1, summary["watching"]!.GetValue<int>());
    }

    private static async Task<string> CallAsync(ConciergeTools tools, string name, JsonObject args)
    {
        var reply = await tools.HandleAsync(Encoding.UTF8.GetBytes(new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call", ["params"] = new JsonObject { ["name"] = name, ["arguments"] = args },
        }.ToJsonString()), Ct);
        return JsonNode.Parse(reply.Json!)!["result"]!["content"]![0]!["text"]!.GetValue<string>();
    }
}
