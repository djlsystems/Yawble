using Harness.Host;

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
}
