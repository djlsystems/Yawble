using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using Yawble.Core.Agents;
using Yawble.Core.Ipc;
using Yawble.Core.Link;
using Yawble.Core.Sandboxing;
using Yawble.Core.Policy;
using Yawble.Core.Provenance;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Launch;
using Yawble.Core.Ui;
using Xunit;

namespace Yawble.Core.Tests.Agents;

/// <summary>
/// An agent's Terminal / Test session is released when the person is done:
/// agents.testSessions lists the live ones, agents.testChanged is pushed on start and end (window only), and a session
/// with no input for the idle limit ends on its own. Real launcher, scripted backend: no real CLI runs.
/// </summary>
public class AgentTestSessionReleaseTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// A clock the test moves by hand, timers included: <see cref="Advance"/> fires every timer that has come due (the
    /// idle tick is Task.Delay on this clock). <see cref="Armed"/>: a timer is waiting, i.e. the idle loop finished its
    /// last check and is waiting for the next tick.
    /// </summary>
    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private readonly Lock _lock = new();
        private readonly List<Timer> _timers = [];
        private DateTimeOffset _now = now;

        public DateTimeOffset Now { get { lock (_lock) return _now; } }
        public override DateTimeOffset GetUtcNow() => Now;

        public bool Armed { get { lock (_lock) return _timers.Any(t => t.Due is not null); } }

        public void Advance(TimeSpan by)
        {
            List<Timer> due;
            lock (_lock)
            {
                _now += by;
                due = _timers.Where(t => t.Due is { } d && d <= _now).ToList();
                foreach (var t in due) t.Due = null; // one-shot (Task.Delay's); Change re-arms
            }
            foreach (var t in due) t.Fire();
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var t = new Timer(this, callback, state);
            lock (_lock) _timers.Add(t);
            t.Change(dueTime, period);
            return t;
        }

        private sealed class Timer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            public DateTimeOffset? Due;
            public void Fire() => callback(state);

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (clock._lock) Due = dueTime == Timeout.InfiniteTimeSpan ? null : clock._now + dueTime;
                return true;
            }

            public void Dispose() { lock (clock._lock) { Due = null; clock._timers.Remove(this); } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    /// <summary>Holds the launcher's detection (so the sandbox has not begun) until the test opens the gate.</summary>
    private sealed class GatedDetector(ICliDetector inner) : ICliDetector
    {
        public readonly TaskCompletionSource Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;

        public async Task<IReadOnlyList<DetectedCli>> DetectAllAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            await Gate.Task.WaitAsync(ct);
            return await inner.DetectAllAsync(ct);
        }
    }

    private sealed class NoClis : ICliDetector
    {
        public Task<IReadOnlyList<DetectedCli>> DetectAllAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<DetectedCli>>([]);
    }

    private static string Replay(TerminalHub hub, string channel) =>
        Encoding.UTF8.GetString(Convert.FromBase64String(hub.Attach(channel)["replay"]!.GetValue<string>()));

    /// <summary>Every agents.testChanged the window's connection would get (it subscribes to the same pushes).</summary>
    private static (ConcurrentQueue<JsonObject> Changes, IDisposable Subscription) Watch(AppEvents events)
    {
        var changes = new ConcurrentQueue<JsonObject>();
        var subscription = events.Subscribe(m =>
        {
            if (m["event"]?.GetValue<string>() == AgentTestSessions.ChangedEvent) changes.Enqueue(m);
            return Task.CompletedTask;
        });
        return (changes, subscription);
    }

    private static bool Has(ConcurrentQueue<JsonObject> changes, string cli, bool live) =>
        changes.Any(m => m["data"]!["cli"]!.GetValue<string>() == cli && m["data"]!["live"]!.GetValue<bool>() == live);

    [Fact]
    public void TheIdleLimitIsThirtyMinutes() => Assert.Equal(TimeSpan.FromMinutes(30), AgentTestSessions.IdleLimit);

    [Fact]
    public async Task TestSessionsListsALiveSessionWithItsAgentAndKind_AndTheChangeIsPushedOnStartAndEnd()
    {
        await using var kit = await LaunchKit.StartAsync(wallSec: 3600);
        var events = new AppEvents();
        var (changes, subscription) = Watch(events);
        using var watching = subscription;
        var hub = new TerminalHub(events);
        using var bridge = new RunTerminalBridge(kit.Registry, hub, (_, _) => null, AgentTestSessions.ChannelFor);
        kit.Backend.Script = (_, _) => Task.CompletedTask; // runs until stopped
        var tests = new AgentTestSessions(kit.Launcher, null, hub, kit.Clis);
        var dispatcher = new RpcDispatcher(new SandboxPlatformInfo(), () => [], agentTests: tests);
        var channel = AgentTestSessions.Channel("claude");

        Assert.Empty((await dispatcher.DispatchAsAsync(Actor.Window, "agents.testSessions", null, Ct))!.AsArray());
        await tests.StartAsync("claude", diagnostic: true, Ct);
        await Wait.UntilAsync(() => hub.SessionOf(channel) is { Ended: false }, "test session live");

        var listed = (await dispatcher.DispatchAsAsync(Actor.Window, "agents.testSessions", null, Ct))!.AsArray();
        var one = Assert.Single(listed)!.AsObject();
        Assert.Equal("claude", one["cli"]!.GetValue<string>());
        Assert.Equal("claude", one["agent"]!.GetValue<string>());
        Assert.Equal("test", one["kind"]!.GetValue<string>());
        Assert.True(DateTimeOffset.TryParse(one["startedAt"]!.GetValue<string>(), out _));
        await Wait.UntilAsync(() => Has(changes, "claude", live: true), "started pushed");
        var started = changes.First(m => m["data"]!["live"]!.GetValue<bool>())["data"]!;
        Assert.Equal("test", started["kind"]!.GetValue<string>());

        Assert.True(await tests.StopAsync("claude"));
        await Wait.UntilAsync(() => Has(changes, "claude", live: false), "ended pushed");
        Assert.Empty(tests.Sessions());
    }

    [Fact]
    public async Task ATerminalSessionThatEndsByItselfIsDroppedFromTheList()
    {
        await using var kit = await LaunchKit.StartAsync(wallSec: 3600);
        var events = new AppEvents();
        var (changes, subscription) = Watch(events);
        using var watching = subscription;
        var hub = new TerminalHub(events);
        using var bridge = new RunTerminalBridge(kit.Registry, hub, (_, _) => null, AgentTestSessions.ChannelFor);
        var exit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        kit.Backend.Script = async (_, p) => { await exit.Task; p.Exit(0); }; // the CLI exits when the test says so
        var tests = new AgentTestSessions(kit.Launcher, null, hub, kit.Clis);

        await tests.StartAsync("grok", diagnostic: false, Ct);
        await Wait.UntilAsync(() => hub.SessionOf(AgentTestSessions.Channel("grok")) is { Ended: false }, "terminal session live");
        Assert.Equal("terminal", Assert.Single(tests.Sessions())!["kind"]!.GetValue<string>());

        exit.SetResult();
        await Wait.UntilAsync(() => tests.Sessions().Count == 0, "dropped after it ended");
        await Wait.UntilAsync(() => Has(changes, "grok", live: false), "ended pushed");
    }

    [Fact]
    public async Task OverTheCloudLinkTestSessionsIsRefusedAndTheChangeIsNotForwarded()
    {
        var hub = new TerminalHub(new AppEvents());
        var tests = new AgentTestSessions(null, "no launcher", hub, new NoClis());
        var dispatcher = new RpcDispatcher(new SandboxPlatformInfo(), () => [], agentTests: tests);

        Assert.NotNull(RemoteAccess.Refusal("agents.testSessions", null, allowHostTerminals: true));
        foreach (var actor in new[] { Actor.CloudViewer("ines"), new Actor(Actor.Concierge, null), Actor.ExternalClient("claude-code") })
        {
            var refused = await Assert.ThrowsAsync<RpcException>(() => dispatcher.DispatchAsAsync(actor, "agents.testSessions", null, Ct));
            Assert.Equal("forbidden", refused.Code);
        }
        Assert.False(CloudLink.Forwards(AgentTestSessions.ChangedEvent));
        Assert.False(CloudLink.Forwards(new JsonObject
        {
            ["event"] = AgentTestSessions.ChangedEvent,
            ["data"] = new JsonObject { ["cli"] = "claude", ["agent"] = "claude", ["kind"] = "terminal", ["live"] = true },
        }));
        Assert.True(CloudLink.Forwards("agents.changed")); // other agents.* pushes still go up
    }

    [Fact]
    public async Task ASessionWithNoActivityForTheIdleLimitEndsOnItsOwn_ButNotWhileThePersonTypesOrTheAgentWrites()
    {
        await using var kit = await LaunchKit.StartAsync(wallSec: 3600);
        var events = new AppEvents();
        var (changes, subscription) = Watch(events);
        using var watching = subscription;
        var hub = new TerminalHub(events);
        using var bridge = new RunTerminalBridge(kit.Registry, hub, (_, _) => null, AgentTestSessions.ChannelFor);
        kit.Backend.Script = (_, _) => Task.CompletedTask;
        // The idle tick itself runs on the fake clock (Task.Delay on it): nothing ticks unless the test advances it.
        var clock = new ManualClock(DateTimeOffset.Parse("2026-11-12T09:00:00Z"));
        var tests = new AgentTestSessions(kit.Launcher, null, hub, kit.Clis, clock: clock);
        var channel = AgentTestSessions.Channel("claude");

        // Moves the clock; the tick that came due runs its check and waits for the next one (or ended the session).
        async Task Tick(TimeSpan by)
        {
            clock.Advance(by);
            await Wait.UntilAsync(() => clock.Armed || hub.SessionOf(channel) is { Ended: true }, "the idle check ran");
        }

        await tests.StartAsync("claude", diagnostic: false, Ct);
        await Wait.UntilAsync(() => hub.SessionOf(channel) is { Ended: false, InputEnabled: true }, "terminal session live");
        await Wait.UntilAsync(() => clock.Armed, "idle tick armed");

        // Real time alone never ends it: the tick is the fake clock's.
        await Task.Delay(100, Ct);
        Assert.True(hub.IsLive(channel));

        // 29 minutes in the person types; 29 minutes after that it is still live (the idle counts from the input).
        await Tick(TimeSpan.FromMinutes(29));
        Assert.True(hub.Input(channel, "hi\r"u8));
        await Tick(TimeSpan.FromMinutes(29));
        Assert.True(hub.IsLive(channel));
        Assert.Single(tests.Sessions());

        // The agent still working (output on its channel, as the PTY pump writes it) resets the timer too.
        hub.Write(channel, "still thinking…\r\n");
        hub.Write(AgentTestSessions.Channel("grok"), "another channel's output\r\n"); // not this session's activity
        await Tick(TimeSpan.FromMinutes(29));
        Assert.True(hub.IsLive(channel));
        Assert.DoesNotContain("no activity", Replay(hub, channel));

        // 30 minutes after the last activity it ends on its own, with a dimmed line saying why.
        await Tick(TimeSpan.FromMinutes(1));
        await Wait.UntilAsync(() => hub.SessionOf(channel) is { Ended: true }, "ended for idling");
        await Wait.UntilAsync(() => Replay(hub, channel).Contains("\x1b[2m■ ended after 30 minutes with no activity\x1b[0m"), "the line");
        await Wait.UntilAsync(() => tests.Sessions().Count == 0, "dropped");
        await Wait.UntilAsync(() => Has(changes, "claude", live: false), "ended pushed");
    }

    [Fact]
    public async Task TheIdleEndWritesItsLineOnlyWhenTheStopHappened()
    {
        await using var kit = await LaunchKit.StartAsync(wallSec: 3600);
        var hub = new TerminalHub(new AppEvents());
        using var bridge = new RunTerminalBridge(kit.Registry, hub, (_, _) => null, AgentTestSessions.ChannelFor);
        kit.Backend.Script = (_, _) => Task.CompletedTask;
        var clock = new ManualClock(DateTimeOffset.Parse("2026-11-12T09:00:00Z"));
        var tests = new AgentTestSessions(kit.Launcher, null, hub, kit.Clis, clock: clock);
        var channel = AgentTestSessions.Channel("claude");

        await tests.StartAsync("claude", diagnostic: false, Ct);
        await Wait.UntilAsync(() => hub.SessionOf(channel) is { Ended: false }, "terminal session live");
        // Still shown live, but with nothing the hub can stop (no input behind it): the stop does not happen, so the
        // "ended" line is not written (it would be untrue, and would count as activity).
        hub.Begin(channel, new TerminalSession("interactive", null, "2026-11-12T09:00:00Z", false, null), null);
        clock.Advance(TimeSpan.FromMinutes(31));
        Assert.True(hub.IsLive(channel));
        Assert.False(await tests.EndIfIdleAsync("claude"));
        Assert.DoesNotContain("no activity", Replay(hub, channel));
        kit.Backend.Spawned.Single().Exit(0); // the sandbox ends by itself
        await Wait.UntilAsync(() => tests.Sessions().Count == 0, "released");
    }

    [Fact]
    public async Task StopTestOverTheRpcWaitsForTheRunToFinish_SoAStartRightAfterItIsNotBusy()
    {
        await using var kit = await LaunchKit.StartAsync(wallSec: 3600);
        var hub = new TerminalHub(new AppEvents());
        using var bridge = new RunTerminalBridge(kit.Registry, hub, (_, _) => null, AgentTestSessions.ChannelFor);
        // The sandbox ignores the kill and exits a while after it: the run finishes well after the stop was asked.
        kit.Backend.IgnoreKill = true;
        kit.Backend.Script = async (_, p) =>
        {
            while (p.KillCalls == 0) await Task.Delay(10);
            await Task.Delay(400);
            p.Exit(0);
        };
        var tests = new AgentTestSessions(kit.Launcher, null, hub, kit.Clis);
        var dispatcher = new RpcDispatcher(new SandboxPlatformInfo(), () => [], agentTests: tests);
        var channel = AgentTestSessions.Channel("claude");

        var started = (await dispatcher.DispatchAsAsync(Actor.Window, "agents.test", new JsonObject { ["cli"] = "claude", ["diagnostic"] = false }, Ct))!;
        var runId = started["runId"]!.GetValue<string>();
        await Wait.UntilAsync(() => hub.SessionOf(channel) is { Ended: false }, "terminal session live");

        // As the Agents screen does (stopTest with the run, then Test): no wait in between, and it is not busy.
        var stopped = await dispatcher.DispatchAsAsync(Actor.Window, "agents.stopTest", new JsonObject { ["cli"] = "claude", ["runId"] = runId }, Ct);
        Assert.True(stopped!["stopped"]!.GetValue<bool>());
        Assert.Empty(tests.Sessions()); // the run had finished before the stop answered
        var again = (await dispatcher.DispatchAsAsync(Actor.Window, "agents.test", new JsonObject { ["cli"] = "claude", ["diagnostic"] = true }, Ct))!;
        Assert.NotEqual(runId, again["runId"]!.GetValue<string>());
        await Wait.UntilAsync(() => kit.Backend.Requests.Count == 2, "second launch");
        await Wait.UntilAsync(() => hub.SessionOf(channel) is { Ended: false }, "second session live");
        kit.Backend.IgnoreKill = false;
        foreach (var p in kit.Backend.Spawned) p.IgnoreKill = false;
        Assert.True(await tests.StopAsync("claude"));
    }

    [Fact]
    public async Task AStopForAnOlderRunOfTheSameCliStopsNothing()
    {
        await using var kit = await LaunchKit.StartAsync(wallSec: 3600);
        var hub = new TerminalHub(new AppEvents());
        using var bridge = new RunTerminalBridge(kit.Registry, hub, (_, _) => null, AgentTestSessions.ChannelFor);
        kit.Backend.Script = (_, _) => Task.CompletedTask;
        var tests = new AgentTestSessions(kit.Launcher, null, hub, kit.Clis);
        var channel = AgentTestSessions.Channel("claude");

        var first = (await tests.StartAsync("claude", diagnostic: false, Ct))["runId"]!.GetValue<string>();
        await Wait.UntilAsync(() => hub.SessionOf(channel) is { Ended: false }, "first live");
        Assert.True(await tests.StopAsync("claude", first));
        var second = (await tests.StartAsync("claude", diagnostic: true, Ct))["runId"]!.GetValue<string>();
        await Wait.UntilAsync(() => hub.SessionOf(channel) is { Ended: false }, "second live");
        Assert.Equal(second, tests.Sessions().Single()!["runId"]!.GetValue<string>());

        // An old pane's stop (the first run's id) leaves the newer session running.
        Assert.False(await tests.StopAsync("claude", first));
        await Task.Delay(100, Ct);
        Assert.True(hub.IsLive(channel));
        Assert.True(await tests.StopAsync("claude", second));
        await Wait.UntilAsync(() => hub.SessionOf(channel) is { Ended: true }, "second ended");
    }

    [Fact]
    public async Task AStopAskedWhileTheSandboxIsStartingStopsItAsSoonAsItBegins()
    {
        using var db = new TempDb();
        var root = HostPathCanonicalizer.Canonicalize(Path.Combine(db.Dir, "machine"));
        var standIns = TestClis.StandIns(root, Path.Combine(root, "home"));
        var launcherClis = new GatedDetector(standIns); // the launcher's own detection waits: the sandbox has not begun
        await using var kit = await LaunchKit.StartAsync(wallSec: 3600, db: db, clis: launcherClis);
        var hub = new TerminalHub(new AppEvents());
        using var bridge = new RunTerminalBridge(kit.Registry, hub, (_, _) => null, AgentTestSessions.ChannelFor);
        kit.Backend.Script = (_, _) => Task.CompletedTask; // runs until stopped
        var tests = new AgentTestSessions(kit.Launcher, null, hub, standIns);
        var channel = AgentTestSessions.Channel("claude");

        var runId = (await tests.StartAsync("claude", diagnostic: false, Ct))["runId"]!.GetValue<string>();
        await Wait.UntilAsync(() => Volatile.Read(ref launcherClis.Calls) >= 1, "the launcher is starting the sandbox");
        Assert.False(hub.IsLive(channel)); // nothing on the channel to stop yet

        var stopping = tests.StopAsync("claude", runId); // ✕ during start-up
        launcherClis.Gate.SetResult();
        Assert.True(await stopping);

        await Wait.UntilAsync(() => kit.Backend.Requests.Count == 1, "the sandbox was started");
        await Wait.UntilAsync(() => hub.SessionOf(channel) is { Ended: true }, "stopped as soon as it began");
        await Wait.UntilAsync(() => tests.Sessions().Count == 0, "released");
        // ...and released normally: the CLI is free for a new session.
        await Wait.UntilAsync(async () =>
        {
            try { await tests.StartAsync("claude", diagnostic: false, Ct); return true; }
            catch (RpcException e) when (e.Code == "busy") { return false; }
        }, "a new session after the stop");
        await Wait.UntilAsync(() => hub.SessionOf(channel) is { Ended: false }, "new session live");
        Assert.True(await tests.StopAsync("claude"));
    }

    [Fact]
    public async Task AShortenedIdleLimitEndsTheSessionWhenNothingHappens()
    {
        await using var kit = await LaunchKit.StartAsync(wallSec: 3600);
        var hub = new TerminalHub(new AppEvents());
        using var bridge = new RunTerminalBridge(kit.Registry, hub, (_, _) => null, AgentTestSessions.ChannelFor);
        kit.Backend.Script = (_, _) => Task.CompletedTask;
        var tests = new AgentTestSessions(kit.Launcher, null, hub, kit.Clis,
            idleLimit: TimeSpan.FromMilliseconds(300), idleCheck: TimeSpan.FromMilliseconds(20));
        var channel = AgentTestSessions.Channel("grok");

        await tests.StartAsync("grok", diagnostic: false, Ct);
        await Wait.UntilAsync(() => hub.SessionOf(channel) is { Ended: true }, "ended for idling");
        Assert.Contains("ended after 0.3 seconds with no activity", Replay(hub, channel));
    }
}
