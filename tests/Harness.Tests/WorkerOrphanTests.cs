using System.Diagnostics;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Capacity;

namespace Harness.Tests;

/// <summary>
/// A worker that was killed leaves its runs' children running; the next worker of the same name stops
/// them at its start - only a group whose leader is still the process that was recorded.
/// </summary>
public sealed class WorkerOrphanTests : IDisposable
{
    private readonly string _state = Directory.CreateTempSubdirectory("harness-worker-orphans-").FullName;
    private readonly List<Process> _started = [];

    public void Dispose()
    {
        // Only the processes this test started, by their own handles.
        foreach (var process in _started)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            process.WaitForExit(10_000);
            process.Dispose();
        }

        Directory.Delete(_state, recursive: true);
    }

    [Fact]
    public void A_run_a_killed_worker_left_running_is_stopped_by_the_next_worker_of_its_name()
    {
        var leftover = StartGroup();
        var groups = new RunProcessGroups();
        using var registered = groups.Register(leftover.Id, new ContainerId("alpha", "Developer"));
        var file = WorkerOrphans.FileFor(_state, new WorkerId("w1"));

        WorkerOrphans.Record(file, groups);
        Assert.True(File.Exists(file));

        // The worker is gone; the next one of its name starts.
        var stopped = WorkerOrphans.StopLeftovers(file);

        Assert.Equal([leftover.Id], stopped);
        Assert.True(leftover.WaitForExit(10_000));
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void A_group_whose_number_now_names_another_process_is_left_alone()
    {
        var other = StartGroup();
        var file = WorkerOrphans.FileFor(_state, new WorkerId("w1"));

        // Recorded with another start time: the number was the system's to give again.
        File.WriteAllText(file, $"{other.Id} 1\n");

        Assert.Empty(WorkerOrphans.StopLeftovers(file));
        Assert.False(other.WaitForExit(500));
    }

    /// <summary>A process leading a process group of its own, as a run's child is.</summary>
    private Process StartGroup()
    {
        var start = new ProcessStartInfo("setsid") { UseShellExecute = false, RedirectStandardInput = true };
        start.ArgumentList.Add("sleep");
        start.ArgumentList.Add("600");
        var process = Process.Start(start)!;
        process.StandardInput.Close();
        _started.Add(process);

        // Its own group once setsid has made it one.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && !File.ReadAllText($"/proc/{process.Id}/stat").Contains($" {process.Id} {process.Id} ")) Thread.Sleep(20);
        return process;
    }
}
