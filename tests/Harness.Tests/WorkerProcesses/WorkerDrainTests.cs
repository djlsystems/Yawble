using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// DRAINING A WORKER, as the operator CLI does before it stops one: a <c>drain</c> file in the
/// worker's state folder. The worker says so to control, control places nothing new on it, and the
/// runs it has go on. The drain is said again in every hello, so it holds across a reconnect.
/// </summary>
[Collection("worker processes")]
public sealed class WorkerDrainTests
{
    [Fact]
    public async Task A_draining_worker_is_passed_over_and_its_runs_continue()
    {
        await using var bed = new ProcessBed();
        await bed.StartControlAsync(graceSeconds: 30);
        var first = Directory.CreateDirectory(Path.Combine(bed.Work, "state-w1")).FullName;
        var w1 = bed.StartWorker("w1", more: new Dictionary<string, string> { ["HARNESS_WORKER_STATE_DIR"] = first });
        await bed.UntilAsync("w1 is connected", async () => (await bed.WorkersAsync()).Any(w => w.GetProperty("id").GetString() == "w1"));

        var team = await bed.TeamAsync("Drains", "BlockDev", "Later");
        (await bed.TellAsync(team, "BlockDev", "Do the work.")).EnsureSuccessStatusCode();
        await bed.UntilAsync("BlockDev runs on w1", () => Task.FromResult(bed.FakeRuns().Any(r => r.Member == "BlockDev")));
        Assert.Contains(w1.Pid, bed.FakeRuns().Single(r => r.Member == "BlockDev").Chain);

        await File.WriteAllTextAsync(Path.Combine(first, WorkerStateFiles.DrainName), "", TestContext.Current.CancellationToken);
        await bed.UntilAsync("control records w1 draining", () => Task.FromResult(
            WorkersRecord.Read(bed.Root)?.Items.SingleOrDefault(w => w.Id == "w1") is { Draining: true }));

        // Nothing new goes to w1: Later waits until another worker comes.
        (await bed.TellAsync(team, "Later", "Do the later work.")).EnsureSuccessStatusCode();
        await Task.Delay(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken);
        Assert.DoesNotContain(bed.FakeRuns(), r => r.Member == "Later");

        var w2 = bed.StartWorker("w2", more: new Dictionary<string, string> { ["HARNESS_WORKER_STATE_DIR"] = Directory.CreateDirectory(Path.Combine(bed.Work, "state-w2")).FullName });
        await bed.UntilAsync("Later runs on w2", () => Task.FromResult(bed.FakeRuns().Any(r => r.Member == "Later")));
        Assert.Contains(w2.Pid, bed.FakeRuns().Single(r => r.Member == "Later").Chain);

        // And the run w1 had goes on to its end there.
        bed.Go("BlockDev");
        await bed.UntilAsync("BlockDev ends on w1", () => Task.FromResult(bed.FakeRuns().Single(r => r.Member == "BlockDev").Ended));
        Assert.False(w1.Exited, w1.Text());
    }

    [Fact]
    public async Task Draining_survives_a_reconnect()
    {
        await using var bed = new ProcessBed();
        await bed.StartControlAsync(graceSeconds: 30);
        var state = Directory.CreateDirectory(Path.Combine(bed.Work, "state-w1")).FullName;

        // Asked before it connects: its hello says so, as every hello after a reconnect does.
        await File.WriteAllTextAsync(Path.Combine(state, WorkerStateFiles.DrainName), "", TestContext.Current.CancellationToken);
        var w1 = bed.StartWorker("w1", more: new Dictionary<string, string> { ["HARNESS_WORKER_STATE_DIR"] = state });
        await bed.UntilAsync("control records w1 draining from its hello", () => Task.FromResult(
            WorkersRecord.Read(bed.Root)?.Items.SingleOrDefault(w => w.Id == "w1") is { Draining: true }));

        // Taken off it: placeable again.
        File.Delete(Path.Combine(state, WorkerStateFiles.DrainName));
        await bed.UntilAsync("control records w1 no longer draining", () => Task.FromResult(
            WorkersRecord.Read(bed.Root)?.Items.SingleOrDefault(w => w.Id == "w1") is { Draining: false }));
        var team = await bed.TeamAsync("Undrained", "Dev");
        (await bed.TellAsync(team, "Dev", "Do the work.")).EnsureSuccessStatusCode();
        await bed.UntilAsync("Dev runs on w1", () => Task.FromResult(bed.FakeRuns().Any(r => r.Member == "Dev")));
        Assert.Contains(w1.Pid, bed.FakeRuns().Single(r => r.Member == "Dev").Chain);
    }
}
