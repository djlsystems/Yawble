using System.Text.Json.Nodes;
using Yawble.Core.Logs;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Ui;
using Xunit;

namespace Yawble.Core.Tests.Ui;

/// <summary>
/// What the terminal channels and the event queues hold in memory: a replay takes what was printed, a live terminal
/// keeps its replay, the replays of ended terminals stay within a budget (the least recently used go first), and a
/// subscriber that does not read has a bounded queue.
/// </summary>
public class TerminalMemoryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TerminalSession Session(string started = "2030-01-01T00:00:00Z") => new("headless", 1, started, false, "fake");

    private static byte[] Bytes(int n, int seed)
    {
        var data = new byte[n];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static byte[] Replay(TerminalHub hub, string channel) => Convert.FromBase64String(hub.Attach(channel)["replay"]!.GetValue<string>());

    private static long End(TerminalHub hub, string channel) => hub.Attach(channel)["end"]!.GetValue<long>();

    [Fact]
    public void AReplayBuffer_KeepsTheNewestBytes_AsAPlainListWould()
    {
        const int Capacity = 10_000;
        var buffer = new ReplayBuffer(Capacity);
        var model = new List<byte>();
        var random = new Random(7);
        Assert.Equal(0, buffer.Held);
        Assert.Empty(buffer.ToArray());

        for (var i = 0; i < 400; i++)
        {
            var chunk = Bytes(random.Next(0, i % 50 == 49 ? 25_000 : 700), i);
            buffer.Append(chunk);
            model.AddRange(chunk);
            if (model.Count > Capacity) model.RemoveRange(0, model.Count - Capacity);
            Assert.Equal(model.Count, buffer.Count);
            Assert.True(buffer.Held <= Capacity);
            Assert.True(model.SequenceEqual(buffer.ToArray()), $"after append {i}");
            if (i == 200)
            {
                buffer.Clear();
                model.Clear();
            }
        }

        buffer.Free();
        Assert.Equal((0, 0), (buffer.Held, buffer.Count));
        buffer.Append([1, 2, 3]);
        Assert.Equal([1, 2, 3], buffer.ToArray());
    }

    [Fact]
    public void AReplay_TakesWhatWasPrinted_AndAChannelNobodyWroteTo_TakesNothing()
    {
        var hub = new TerminalHub(new AppEvents());

        hub.Attach("member:1"); // a pane shown for a member that never ran
        hub.Begin("member:2", Session(), null);
        Assert.Equal(0, hub.ReplayBytesHeld);

        hub.Write("member:2", Bytes(10_000, 1));
        Assert.InRange(hub.ReplayBytesHeld, 10_000, 20_000);

        hub.Write("member:2", Bytes(3 * TerminalHub.ReplayBytes, 2));
        Assert.Equal(TerminalHub.ReplayBytes, hub.ReplayBytesHeld); // never more than the limit
        Assert.Equal(TerminalHub.ReplayBytes, Replay(hub, "member:2").Length);
    }

    [Fact]
    public void EndedTerminals_KeepTheirReplaysWithinTheBudget_TheLeastRecentlyUsedGoFirst_AndALiveOneIsKept()
    {
        var hub = new TerminalHub(new AppEvents()) { EndedReplayBudget = 250_000 };
        var printed = new Dictionary<string, byte[]>();
        void Run(string channel, int seed)
        {
            hub.Begin(channel, Session(), null);
            hub.Write(channel, printed[channel] = Bytes(100_000, seed));
        }

        Run("member:1", 1);
        Run("member:2", 2);
        Run("member:3", 3);
        Run("member:4", 4);
        Assert.Equal(400_000, hub.ReplayBytesHeld); // all live: over the budget, and all kept

        hub.End("member:1", 0, "completed");
        hub.End("member:2", 0, "completed");
        Assert.Equal(printed["member:1"], Replay(hub, "member:1")); // within the budget; and looked at, so used after member:2
        hub.End("member:3", 0, "completed");

        // Three ended replays are 300,000 bytes: the one used longest ago (member:2) is freed.
        Assert.Empty(Replay(hub, "member:2"));
        Assert.Equal(100_000, End(hub, "member:2")); // its byte count goes on: an open window drops nothing it has not seen
        Assert.Equal(printed["member:1"], Replay(hub, "member:1"));
        Assert.Equal(printed["member:3"], Replay(hub, "member:3"));
        Assert.Equal(printed["member:4"], Replay(hub, "member:4")); // still live
        Assert.Equal(300_000, hub.ReplayBytesHeld);

        // A new run on the freed channel starts a new replay and counts on.
        hub.Begin("member:2", Session("2030-01-02T00:00:00Z"), null);
        hub.Write("member:2", "again");
        Assert.Equal("again"u8.ToArray(), Replay(hub, "member:2"));
        Assert.Equal(100_005, End(hub, "member:2"));
    }

    [Fact]
    public async Task ASubscriberThatDoesNotRead_HasABoundedQueue_AndGetsTheNewestEvents()
    {
        var entries = new List<DiagEntry>();
        using var capture = Diag.Capture(e => { lock (entries) entries.Add(e); });
        var events = new AppEvents();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var got = new List<int>();
        using var subscription = events.Subscribe(async message =>
        {
            first.TrySetResult();
            await release.Task;
            lock (got) got.Add(message["data"]!["n"]!.GetValue<int>());
        });

        events.Publish("test", new JsonObject { ["n"] = 0 });
        await first.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct); // event 0 is being sent; the rest wait in the queue
        const int Sent = AppEvents.QueueLimit + 500;
        for (var n = 1; n <= Sent; n++) events.Publish("test", new JsonObject { ["n"] = n });
        release.SetResult();

        await Wait.UntilAsync(() => { lock (got) return got.Count > 0 && got[^1] == Sent; }, "the newest event arrives");
        int[] received;
        lock (got) received = [.. got];
        Assert.Equal(AppEvents.QueueLimit + 1, received.Length); // the one being sent, and a full queue
        Assert.Equal(Enumerable.Range(Sent - AppEvents.QueueLimit + 1, AppEvents.QueueLimit), received.Skip(1)); // the newest, in order
        DiagEntry warning;
        lock (entries) warning = Assert.Single(entries, e => e.Area == "events");
        Assert.Equal(LogLevels.Warning, warning.Level);
    }

    [Fact]
    public async Task ASubscriberWhoseSendFails_IsTakenOff_AndNothingMoreIsQueuedForIt()
    {
        var events = new AppEvents();
        var sends = 0;
        using var subscription = events.Subscribe(_ =>
        {
            Interlocked.Increment(ref sends);
            throw new IOException("the window went away");
        });
        Assert.Equal(1, events.Subscribers);

        events.Publish("test", new JsonObject { ["n"] = 1 });
        await Wait.UntilAsync(() => events.Subscribers == 0, "the failed subscriber is taken off");
        for (var n = 0; n < 100; n++) events.Publish("test", new JsonObject { ["n"] = n });

        Assert.Equal(1, Volatile.Read(ref sends));
        subscription.Dispose(); // already off: nothing to do
        Assert.Equal(0, events.Subscribers);
    }
}
