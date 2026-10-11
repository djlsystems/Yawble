using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// A WORKER IS HEALTHY EXACTLY WHILE IT IS CONNECTED. Its host keeps <c>connected</c> in its state
/// folder - written at control's welcome, touched at every keep-alive, deleted when the connection
/// drops or the worker stops - and the worker image's healthcheck asks for that file, fresh.
/// </summary>
[Collection("worker processes")]
public sealed class WorkerHealthFileTests
{
    [Fact]
    public async Task The_health_file_exists_exactly_while_connected()
    {
        await using var bed = new ProcessBed();
        await bed.StartControlAsync();
        var stopping = Directory.CreateDirectory(Path.Combine(bed.Work, "state-stopping")).FullName;
        var dropping = Directory.CreateDirectory(Path.Combine(bed.Work, "state-dropping")).FullName;
        var connected = (string folder) => Path.Combine(folder, WorkerStateFiles.ConnectedName);

        // A stale file from before a restart is not read as connected.
        await File.WriteAllTextAsync(connected(stopping), "left by a previous start\n", TestContext.Current.CancellationToken);
        var w1 = bed.StartWorker("w1", more: new Dictionary<string, string> { ["HARNESS_WORKER_STATE_DIR"] = stopping });
        await bed.UntilAsync("w1 is connected", async () => (await bed.WorkersAsync()).Any(w => w.GetProperty("id").GetString() == "w1"));
        await bed.UntilAsync("w1's health file is written", () => Task.FromResult(File.Exists(connected(stopping))));
        Assert.StartsWith("20", await File.ReadAllTextAsync(connected(stopping), TestContext.Current.CancellationToken));

        // Kept fresh by the keep-alive (control pings every second here).
        var first = File.GetLastWriteTimeUtc(connected(stopping));
        await bed.UntilAsync("the keep-alive renews it", () => Task.FromResult(File.GetLastWriteTimeUtc(connected(stopping)) > first));

        // Stopped: gone.
        ProcessBed.Signal(w1, "TERM");
        await w1.WaitAsync(ProcessBed.Bound);
        Assert.True(w1.Exited, w1.Text());
        Assert.False(File.Exists(connected(stopping)), w1.Text());

        // Dropped: gone, while the worker goes on trying.
        var w2 = bed.StartWorker("w2", more: new Dictionary<string, string> { ["HARNESS_WORKER_STATE_DIR"] = dropping });
        await bed.UntilAsync("w2's health file is written", () => Task.FromResult(File.Exists(connected(dropping))));
        bed.Control.Stop();
        await bed.UntilAsync("w2's health file is deleted on the drop", () => Task.FromResult(!File.Exists(connected(dropping))));
        Assert.False(w2.Exited, w2.Text());
    }
}
