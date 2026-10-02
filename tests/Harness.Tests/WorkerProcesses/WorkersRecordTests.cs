using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Capacity;

namespace Harness.Tests;

/// <summary>
/// CONTROL RECORDS ITS WORKERS FOR --doctor, which is another process with no connection to read:
/// each worker, its version, connected or dropped, draining or not, its runs and its figures, in
/// <c>workers.json</c>, and the doctor reports it as <c>workers</c>.
/// </summary>
[Collection("worker processes")]
public sealed class WorkersRecordTests
{
    [Fact]
    public async Task Control_records_each_worker_and_the_doctor_reports_it()
    {
        await using var bed = new ProcessBed();
        await bed.StartControlAsync(graceSeconds: 30);
        bed.StartWorker("w1");
        var team = await bed.TeamAsync("Recorded", "BlockDev");
        (await bed.TellAsync(team, "BlockDev", "Do the work.")).EnsureSuccessStatusCode();

        await bed.UntilAsync("w1 is recorded with its run", () => Task.FromResult(
            WorkersRecord.Read(bed.Root)?.Items.SingleOrDefault(w => w.Id == "w1") is { State: WorkersView.Connected, Runs.Count: 1 }));

        var report = await HostDoctor.ReportAsync(bed.Root, TestContext.Current.CancellationToken);
        var w1 = Assert.Single(report.Workers!.Items);
        Assert.Equal("w1", w1.Id);
        Assert.False(w1.Draining);
        Assert.False(string.IsNullOrEmpty(w1.Version));
        var run = Assert.Single(w1.Runs);
        Assert.Equal((team, "BlockDev"), (run.Team, run.Member));
        Assert.Contains("\"workers\":{\"recordedAt\":", HostDoctor.ToJson(report));

        bed.Go("BlockDev");
    }

    // The doctor's running limit is control's record of it. Written at start only, it read "no worker is
    // connected" for as long as control ran, while GET /api/wip had the sum of the workers' bounds.
    [Fact]
    public async Task The_doctors_running_limit_follows_a_worker_joining_and_going()
    {
        await using var bed = new ProcessBed();
        await bed.StartControlAsync(graceSeconds: 1);

        Assert.Equal(0, WipRecord.Read(bed.Root)?.Limit.Limit);

        var worker = bed.StartWorker("w1");
        await bed.UntilAsync("the record counts w1's bound", () => Task.FromResult(
            WipRecord.Read(bed.Root)?.Limit is { Bound: "workers", Limit: > 0 } limit && limit.Reason.Contains("w1", StringComparison.Ordinal)));

        worker.Stop();
        await bed.UntilAsync("the record drops w1 once it has gone", () => Task.FromResult(
            WipRecord.Read(bed.Root)?.Limit is { Limit: 0 }));
    }

    [Fact]
    public async Task A_host_that_runs_its_runs_itself_records_no_workers()
    {
        var root = Directory.CreateTempSubdirectory("harness-no-workers-").FullName;
        try
        {
            var report = await HostDoctor.ReportAsync(root, TestContext.Current.CancellationToken);
            Assert.Null(report.Workers);
            Assert.Contains("\"workers\":null", HostDoctor.ToJson(report));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_run_placed_on_a_worker_but_not_yet_sent_is_in_that_worker_s_runs_until_its_slot_is_released()
    {
        var (pool, wip) = TwoWorkers();
        using var first = wip.TryEnter(new ContainerId("alpha", "Developer"));
        var held = wip.TryEnter(new ContainerId("alpha", "Tester"));
        Assert.NotNull(held);
        Assert.Equal(new WorkerId("w2"), wip.PlacedOn(new ContainerId("alpha", "Tester")));

        // Nothing is open anywhere yet: the run is still starting, or held at the update gate.
        var record = WorkersRecord.Of(pool, wip, _ => [], "v");
        var run = Assert.Single(Worker(record, "w2").Runs);
        Assert.Equal((string.Empty, "alpha", "Tester"), (run.Run, run.Team, run.Member));

        held!.Dispose();
        Assert.Empty(Worker(WorkersRecord.Of(pool, wip, _ => [], "v"), "w2").Runs);
    }

    [Fact]
    public void A_run_open_on_a_worker_is_named_once_by_its_run_and_not_again_by_its_slot()
    {
        var (pool, wip) = TwoWorkers();
        var developer = new ContainerId("alpha", "Developer");
        using var hold = wip.TryEnter(developer);
        Assert.Equal(new WorkerId("w1"), wip.PlacedOn(developer));

        var record = WorkersRecord.Of(pool, wip, worker => worker == new WorkerId("w1") ? [new RunId(developer, "n1")] : [], "v");

        var run = Assert.Single(Worker(record, "w1").Runs);
        Assert.Equal(("n1", "alpha", "Developer"), (run.Run, run.Team, run.Member));
        Assert.Empty(Worker(record, "w2").Runs);
    }

    [Fact]
    public async Task A_record_that_shows_a_worker_draining_holds_every_run_ever_placed_on_it()
    {
        for (var round = 0; round < 200; round++)
        {
            var (pool, wip) = TwoWorkers();
            var w2 = new WorkerId("w2");
            using var placing = new CancellationTokenSource();

            // Members keep taking slots while w2 starts draining; none is released, so every
            // placement on w2 is still there to be seen once the record is read.
            var placer = Task.Run(() =>
            {
                for (var n = 0; !placing.IsCancellationRequested && n < 400; n++) wip.TryEnter(new ContainerId("alpha", "m" + n));
            }, TestContext.Current.CancellationToken);
            await Task.Yield();
            pool.Drain(w2, draining: true);
            var record = WorkersRecord.Of(pool, wip, _ => [], "v");
            placing.Cancel();
            await placer;

            var shown = Worker(record, "w2");
            Assert.True(shown.Draining);
            var recorded = shown.Runs.Select(run => run.Member).ToHashSet();
            var placedOnW2 = wip.HoldsOn(w2).Select(hold => hold.Member).ToArray();
            Assert.All(placedOnW2, member => Assert.Contains(member, recorded));
        }
    }

    private static (WorkerPool Pool, WipLedger Wip) TwoWorkers()
    {
        var pool = new WorkerPool(_ => new HeadroomGate(() => 80, () => 0));
        var wip = new WipLedger(0, pool);
        pool.Placed = wip.PlacedCount;
        foreach (var id in new[] { "w1", "w2" }) pool.Join(new Idle(id), new WorkerInfo(new WorkerId(id), "v", 4, (long)8e9, DateTimeOffset.UtcNow));
        return (pool, wip);
    }

    private static WorkerRecordItem Worker(WorkersRecord record, string id) => record.Items.Single(item => item.Id == id);

    /// <summary>A connected worker that is sent nothing: these records are built from the ledger alone.</summary>
    private sealed class Idle(string id) : IRunWorker
    {
        public WorkerId Id { get; } = new(id);

        public Task Closed { get; } = new TaskCompletionSource().Task;

        public Task SendAsync(ControlMessage message, CancellationToken ct = default) => Task.CompletedTask;
    }
}
