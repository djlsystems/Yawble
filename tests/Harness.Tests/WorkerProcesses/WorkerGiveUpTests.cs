using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// A WORKER THAT HEARS NOTHING FROM CONTROL GIVES UP, when it is given a time (the worker image sets
/// 120 s): it exits 4 and says why, so the engine's restart policy starts it again - on Docker, into
/// control's current network namespace. The time counts from the start, or from the drop of the last
/// connection control welcomed. With no time set it keeps trying, as before.
/// </summary>
[Collection("worker processes")]
public sealed class WorkerGiveUpTests
{
    private const string Sentence = "this worker exits so the engine starts it again.";

    [Fact]
    public async Task A_worker_with_no_answer_from_control_exits_4_with_the_sentence_after_the_give_up_time()
    {
        await using var bed = new ProcessBed();
        var nobody = $"http://127.0.0.1:{FreePort()}";
        var started = Stopwatch.StartNew();
        var worker = bed.StartWorker("w1", more: new Dictionary<string, string>
        {
            ["HARNESS_CONTROL_URL"] = nobody, ["HARNESS_WORKER_GIVE_UP_SECONDS"] = "2",
        });

        await worker.WaitAsync(ProcessBed.Bound);
        Assert.True(worker.Exited, worker.Text());
        Assert.Equal(ControlConnection.GaveUpExitCode, worker.ExitCode);
        Assert.True(started.Elapsed >= TimeSpan.FromSeconds(2), $"Gave up after {started.Elapsed}.");
        Assert.Contains($"No answer from control at {nobody}/ for 2 s; {Sentence}", worker.Text());
    }

    [Fact]
    public async Task With_no_give_up_time_a_worker_keeps_trying()
    {
        await using var bed = new ProcessBed();
        var worker = bed.StartWorker("w1", more: new Dictionary<string, string> { ["HARNESS_CONTROL_URL"] = $"http://127.0.0.1:{FreePort()}" });

        await worker.WaitAsync(TimeSpan.FromSeconds(6));
        Assert.False(worker.Exited, worker.Text());
        Assert.Contains("Could not reach control", worker.Text());
        Assert.DoesNotContain(Sentence, worker.Text());
    }

    [Fact]
    public async Task A_worker_that_connected_once_counts_the_give_up_time_from_its_drop()
    {
        await using var bed = new ProcessBed();
        await bed.StartControlAsync();
        var worker = bed.StartWorker("w1", more: new Dictionary<string, string> { ["HARNESS_WORKER_GIVE_UP_SECONDS"] = "3" });
        await bed.UntilAsync("w1 is connected", async () => (await bed.WorkersAsync()).Any(w => w.GetProperty("id").GetString() == "w1"));

        // Connected for longer than its give-up time: counted from the start, it would go at the first miss.
        await Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(worker.Exited, worker.Text());

        bed.Control.Stop();
        var dropped = Stopwatch.StartNew();
        await worker.WaitAsync(ProcessBed.Bound);

        Assert.True(worker.Exited, worker.Text());
        Assert.Equal(ControlConnection.GaveUpExitCode, worker.ExitCode);
        Assert.True(dropped.Elapsed >= TimeSpan.FromSeconds(2.5), $"Gave up {dropped.Elapsed} after the drop.");
        Assert.Contains(Sentence, worker.Text());
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
