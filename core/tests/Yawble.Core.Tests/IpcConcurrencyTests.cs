using System.Text;
using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Ipc;
using Yawble.Core.Logs;
using Yawble.Core.Policy;
using Yawble.Core.Sandboxing;
using Yawble.Core.Scheduling;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Ui;
using Xunit;

namespace Yawble.Core.Tests;

/// <summary>
/// A window's connection runs its requests side by side: a slow call does not hold the calls sent after it, the calls
/// of one terminal channel still take effect in the order sent, and no more than <see cref="IpcServer.MaxInFlight"/>
/// run at once. The slow call is scheduler.getState over a source the test holds; the terminal is a fake input.
/// </summary>
public class IpcConcurrencyTests
{
    private const string Token = "0123456789abcdef0123456789abcdef-test";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly TimeSpan Soon = TimeSpan.FromSeconds(20);

    private sealed class Rig : IAsyncDisposable
    {
        public TempDb Db { get; } = new();
        public BusEngine Engine { get; private set; } = null!;
        public TerminalHub Terminals { get; private set; } = null!;
        public IpcServer Server { get; private set; } = null!;
        public TestIpcClient Client { get; private set; } = null!;

        /// <summary>Lets the held scheduler.getState calls go.</summary>
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Entered;
        public Func<Task<SchedulerSnapshot>>? Scheduler;

        public static async Task<Rig> StartAsync()
        {
            var rig = new Rig();
            rig.Engine = new BusEngine(rig.Db.OpenStore(), new ScriptedRunner(), new BusOptions { IdleOffer = false });
            rig.Terminals = new TerminalHub(new AppEvents());
            var home = OperatingSystem.IsWindows() ? @"C:\Users\pat" : "/home/pat";
            var dataDir = OperatingSystem.IsWindows() ? @"C:\Users\pat\AppData\Local\Yawble\data" : "/home/pat/.local/share/yawble/data";
            var install = OperatingSystem.IsWindows() ? @"C:\Program Files\Yawble" : "/opt/yawble";
            var ui = new UiRpc(rig.Engine, new PolicyStore(rig.Db.ConnectionString), new MemberLayouts(dataDir, install, home), new FakeModelCredentials(),
                new FakeHostTerminals(rig.Terminals, TimeSpan.FromMilliseconds(5)), new FakeInteractiveSessions(rig.Terminals, rig.Engine), rig.Terminals, 16_384);
            var dispatcher = new RpcDispatcher(new SandboxPlatformInfo(), () => ["0001_baseline"], scheduler: _ => rig.Scheduler!(), ui: ui);
            rig.Scheduler = async () =>
            {
                Interlocked.Increment(ref rig.Entered);
                await rig.Release.Task.WaitAsync(TimeSpan.FromSeconds(60));
                return new SchedulerSnapshot(0, 1, 0, [], [], []);
            };
            rig.Server = IpcServer.Start(IpcEndpoint.Create(IpcEndpoint.NewAddress(Path.GetTempPath())), Token, dispatcher, rig.Terminals.Events);
            rig.Client = await TestIpcClient.ConnectAsync(rig.Server.Address, Ct);
            Assert.True((await rig.Client.AuthAsync(Token, Ct))!["result"]!["authenticated"]!.GetValue<bool>());
            return rig;
        }

        public Task<int> Type(string channel, string data) =>
            Client.SendAsync("terminal.input", new JsonObject { ["channel"] = channel, ["data"] = data }, Ct);

        public async ValueTask DisposeAsync()
        {
            Release.TrySetResult();
            await Client.DisposeAsync();
            await Server.DisposeAsync();
            await Engine.DisposeAsync();
            Db.Dispose();
        }
    }

    /// <summary>A terminal's input that records what it is given; its first write waits for the test.</summary>
    private sealed class HeldInput : ITerminalInput
    {
        private readonly List<string> _written = [];
        public readonly TaskCompletionSource FirstStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ManualResetEventSlim Release = new();
        private int _writes;

        public string[] Written
        {
            get { lock (_written) return [.. _written]; }
        }

        public void Write(ReadOnlySpan<byte> data)
        {
            var text = Encoding.UTF8.GetString(data);
            if (Interlocked.Increment(ref _writes) == 1)
            {
                FirstStarted.TrySetResult();
                Assert.True(Release.Wait(TimeSpan.FromSeconds(60)), "the test lets the first write go");
            }
            lock (_written) _written.Add(text);
        }

        public void Resize(int cols, int rows)
        {
            lock (_written) _written.Add($"resize {cols}x{rows}");
        }

        public Task StopAsync() => Task.CompletedTask;
    }

    private static int IdOf(JsonObject reply) => reply["id"]!.GetValue<int>();

    [Fact]
    public async Task ASlowCall_DoesNotDelayAFastOneSentAfterIt()
    {
        await using var rig = await Rig.StartAsync();

        var slow = await rig.Client.SendAsync("scheduler.getState", null, Ct);
        await Wait.UntilAsync(() => Volatile.Read(ref rig.Entered) == 1, "the slow call is running");
        var fast = await rig.Client.SendAsync("core.getInfo", null, Ct);

        var first = (await rig.Client.ReadNextReplyAsync(Ct))!;
        Assert.Equal(fast, IdOf(first));
        Assert.Equal(CoreInfo.Version, first["result"]!["version"]!.GetValue<string>());

        rig.Release.SetResult();
        var second = (await rig.Client.ReadNextReplyAsync(Ct))!;
        Assert.Equal(slow, IdOf(second));
        Assert.Equal(1, second["result"]!["limit"]!.GetValue<int>());
    }

    [Fact]
    public async Task TheCallsOfOneTerminalChannel_TakeEffectInTheOrderSent_WhileOtherCallsGoOn()
    {
        await using var rig = await Rig.StartAsync();
        var one = new HeldInput();
        var two = new HeldInput();
        two.Release.Set();
        rig.Terminals.Begin("member:1", new TerminalSession("interactive", null, "2030-01-01T00:00:00Z", true, "fake"), one);
        rig.Terminals.Begin("member:2", new TerminalSession("interactive", null, "2030-01-01T00:00:00Z", true, "fake"), two);

        var typed = new List<int>();
        foreach (var key in new[] { "a", "b", "c" }) typed.Add(await rig.Type("member:1", key));
        typed.Add(await rig.Client.SendAsync("terminal.resize", new JsonObject { ["channel"] = "member:1", ["cols"] = 100, ["rows"] = 30 }, Ct));
        foreach (var key in new[] { "d", "e" }) typed.Add(await rig.Type("member:1", key));
        await one.FirstStarted.Task.WaitAsync(Soon, Ct);

        // The first key is still being written: another terminal's key and another call are answered meanwhile,
        // and nothing sent after the first key has reached its terminal.
        var other = await rig.Type("member:2", "x");
        var info = await rig.Client.SendAsync("core.getInfo", null, Ct);
        var answered = new[] { IdOf((await rig.Client.ReadNextReplyAsync(Ct))!), IdOf((await rig.Client.ReadNextReplyAsync(Ct))!) };
        Assert.Equal([other, info], answered.Order());
        Assert.Equal(["x"], two.Written);
        Assert.Empty(one.Written);

        one.Release.Set();
        var replies = new List<JsonObject>();
        for (var n = 0; n < typed.Count; n++) replies.Add((await rig.Client.ReadNextReplyAsync(Ct))!);
        Assert.Equal(typed, replies.Select(IdOf)); // one after the other, so answered in the order sent
        Assert.All(replies, r => Assert.Null(r["error"]));
        Assert.Equal(["a", "b", "c", "resize 100x30", "d", "e"], one.Written);
    }

    [Fact]
    public async Task NoMoreThanTheLimit_RunAtOnce_AndTheRestRunAsTheyAreAnswered()
    {
        await using var rig = await Rig.StartAsync();
        const int Extra = 8;

        var sent = new List<int>();
        for (var n = 0; n < IpcServer.MaxInFlight + Extra; n++) sent.Add(await rig.Client.SendAsync("scheduler.getState", null, Ct));
        await Wait.UntilAsync(() => Volatile.Read(ref rig.Entered) == IpcServer.MaxInFlight, "the limit is reached");
        await Task.Delay(300, Ct); // the requests over the limit are not read while it is full
        Assert.Equal(IpcServer.MaxInFlight, Volatile.Read(ref rig.Entered));

        rig.Release.SetResult();
        var answered = new List<int>();
        for (var n = 0; n < sent.Count; n++) answered.Add(IdOf((await rig.Client.ReadNextReplyAsync(Ct))!));
        Assert.Equal(sent, answered.Order());
        Assert.Equal(sent.Count, Volatile.Read(ref rig.Entered));
        Assert.NotNull((await rig.Client.CallAsync("core.getInfo", null, Ct))!["result"]);
    }

    [Fact]
    public async Task ACallThatFails_IsAnsweredAsAnError_WrittenToTheDiagnosticsLog_AndTheConnectionGoesOn()
    {
        var entries = new List<DiagEntry>();
        using var capture = Diag.Capture(e => { lock (entries) entries.Add(e); });
        await using var rig = await Rig.StartAsync();
        rig.Scheduler = () => throw new InvalidOperationException("the scheduler broke");

        var reply = (await rig.Client.CallAsync("scheduler.getState", null, Ct))!;

        Assert.Equal("internal", reply["error"]!["code"]!.GetValue<string>());
        Assert.Contains("the scheduler broke", reply["error"]!["message"]!.GetValue<string>());
        DiagEntry line;
        lock (entries) line = Assert.Single(entries, e => e.Area == "ipc");
        Assert.Equal(LogLevels.Error, line.Level);
        Assert.Equal("scheduler.getState", line.Field("method"));
        Assert.Contains("the scheduler broke", line.Text);
        Assert.NotNull((await rig.Client.CallAsync("core.getInfo", null, Ct))!["result"]);
    }

    [Fact]
    public void OnlyACallOnATerminalChannel_HasALane()
    {
        Assert.Equal("member:7", IpcServer.LaneOf("terminal.input", new JsonObject { ["channel"] = "member:7", ["data"] = "x" }));
        Assert.Equal("host:claude", IpcServer.LaneOf("terminal.resize", new JsonObject { ["channel"] = "host:claude" }));
        Assert.Null(IpcServer.LaneOf("terminal.stop", new JsonObject { ["memberId"] = 7 }));
        Assert.Null(IpcServer.LaneOf("teams.agents", new JsonObject { ["channel"] = "member:7" }));
        Assert.Null(IpcServer.LaneOf("terminal.input", null));
    }
}
