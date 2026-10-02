using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests;

[CollectionDefinition("worker processes", DisableParallelization = true)]
public sealed class WorkerProcessesCollection;

/// <summary>
/// CONTROL AND WORKERS AS TWO (OR THREE) REAL PROCESSES on one data root, with a fake agent CLI: a run
/// end to end; placement by measured headroom; a run that waits for a worker; a worker killed with its
/// run; a worker that comes back to find its run already ended; a worker refused for its key; and a
/// worker that never loads the database or the key ring. Every wait polls what can be observed, with a
/// bound; nothing sleeps for correctness. See <see cref="ProcessBed"/> for how processes are started and
/// stopped - only by the PID recorded when each was started.
/// </summary>
[Collection("worker processes")]
public sealed class WorkerProcessTests
{
    [Fact]
    public async Task Control_and_a_worker_as_two_processes_run_a_member_end_to_end()
    {
        await using var bed = new ProcessBed();
        await bed.StartControlAsync();
        var w1 = bed.StartWorker("w1");
        await bed.UntilAsync("w1 is connected", async () => (await bed.WorkersAsync()).Any(w => w.GetProperty("id").GetString() == "w1"));

        var team = await bed.TeamAsync("Ends", "BlockDev");
        (await bed.TellAsync(team, "BlockDev", "Do the work.")).EnsureSuccessStatusCode();

        // Held: the run is on the worker, its child is the worker's, and its progress call went through.
        await bed.UntilAsync("the run is held on w1", () => Task.FromResult(bed.FakeRuns().Any(r => r.Member == "BlockDev")));
        var run = bed.FakeRuns().Single(r => r.Member == "BlockDev");
        Assert.Contains(w1.Pid, run.Chain);
        Assert.DoesNotContain(bed.Control.Pid, run.Chain);
        Assert.Matches("^2[0-9][0-9]$", run.Progress ?? "");
        Assert.DoesNotContain("HARNESS_WORKER_KEY", run.Environment);
        Assert.DoesNotContain(bed.Key, run.Environment);
        await bed.UntilAsync("/api/workers lists the run on w1", async () =>
            (await bed.WorkersAsync()).Single(w => w.GetProperty("id").GetString() == "w1")
                .GetProperty("runs").EnumerateArray().Any(r => r.GetProperty("member").GetString() == "BlockDev"));

        bed.Go("BlockDev");
        await bed.UntilAsync("the run completed", async () => (await bed.RowsOfAsync(team, "BlockDev", MessageTypes.Completed)).Count >= 1);

        // The run's own rows, in order. (A member that answered without handing back is woken again by
        // the platform; that later run is not this one.)
        var rows = await bed.RowsOfAsync(team, "BlockDev", MessageTypes.Started, MessageTypes.Progress, MessageTypes.Completed, MessageTypes.Failed);
        Assert.Equal([MessageTypes.Started, MessageTypes.Progress, MessageTypes.Completed], rows.Take(3).Select(r => r.Type));
        var completed = rows[2].Payload;
        Assert.Equal(7, completed.GetProperty(PayloadFields.TokensIn).GetInt32());
        Assert.Equal(11, completed.GetProperty(PayloadFields.TokensOut).GetInt32());
    }

    [Fact]
    public async Task Two_workers_runs_go_to_the_one_with_room_when_the_other_is_over_its_threshold()
    {
        await using var bed = new ProcessBed();
        await bed.StartControlAsync();

        // A is over admission.memoryPercent (80 by default), 85 of 100 GB in use, yet has the most
        // bytes free: 15 GB against B's 5 GB. Ordering by headroom alone would choose A, so only the
        // threshold passes it over. B has room for two runs: 4 CPUs (bound 3) and 6 GB (bound 2).
        var a = Cgroup(bed, "a", inUse: 85_000_000_000, limit: 100_000_000_000);
        var b = Cgroup(bed, "b", inUse: 1_000_000_000, limit: 6_000_000_000);

        // A first, so a placement by connection order would choose it.
        var workerA = bed.StartWorker("A", a);
        await bed.UntilAsync("A is measured", async () => Measured(await bed.WorkersAsync(), "A"));
        var workerB = bed.StartWorker("B", b);
        await bed.UntilAsync("A and B are both measured", async () =>
        {
            var workers = await bed.WorkersAsync();
            return Measured(workers, "A") && Measured(workers, "B");
        });

        var team = await bed.TeamAsync("Placed", "BlockOne", "BlockTwo");
        (await bed.TellAsync(team, "BlockOne", "One.")).EnsureSuccessStatusCode();
        (await bed.TellAsync(team, "BlockTwo", "Two.")).EnsureSuccessStatusCode();

        await bed.UntilAsync("both runs are held", () => Task.FromResult(bed.FakeRuns().Count(r => r.Member.StartsWith("Block")) == 2));
        var workers = await bed.WorkersAsync();
        Assert.Equal(
            ["BlockOne", "BlockTwo"],
            workers.Single(w => w.GetProperty("id").GetString() == "B").GetProperty("runs").EnumerateArray()
                .Select(r => r.GetProperty("member").GetString()).Order());
        Assert.Empty(workers.Single(w => w.GetProperty("id").GetString() == "A").GetProperty("runs").EnumerateArray());

        bed.Go("BlockOne");
        bed.Go("BlockTwo");
        // Each member has completed. (Not a total: a member that answered without handing back is woken
        // again and completes at once, so the count of completed rows keeps growing.)
        await bed.UntilAsync("both completed", async () =>
            (await bed.RowsOfAsync(team, "BlockOne", MessageTypes.Completed)).Count >= 1
            && (await bed.RowsOfAsync(team, "BlockTwo", MessageTypes.Completed)).Count >= 1);

        // None was ever on A: no run's process descends from it.
        Assert.All(bed.FakeRuns(), run => Assert.DoesNotContain(workerA.Pid, run.Chain));
        Assert.All(bed.FakeRuns().Where(r => r.Member.StartsWith("Block")), run => Assert.Contains(workerB.Pid, run.Chain));
    }

    [Fact]
    public async Task A_run_waits_for_a_worker_and_starts_when_one_connects()
    {
        await using var bed = new ProcessBed();
        await bed.StartControlAsync();
        var team = await bed.TeamAsync("Waits", "Dev");

        var told = await bed.TellAsync(team, "Dev", "Do it when you can.");
        Assert.True(told.IsSuccessStatusCode, $"{(int)told.StatusCode}");

        await bed.UntilAsync("Dev waits for a worker", async () =>
            (await bed.GetAsync("/api/wip")).GetProperty("waiting").EnumerateArray()
                .Any(h => h.GetProperty("member").GetString() == "Dev" && h.GetProperty("reason").GetString() == "waiting for a worker"));
        Assert.Empty(await bed.RowsOfAsync(team, "Dev", MessageTypes.Failed, MessageTypes.Started));

        bed.StartWorker("w1");
        await bed.UntilAsync("Dev completed", async () => (await bed.RowsOfAsync(team, "Dev", MessageTypes.Completed)).Count >= 1);

        var connected = (await bed.WorkersAsync()).Single().GetProperty("connectedSince").GetDateTimeOffset();
        var started = (await bed.RowsOfAsync(team, "Dev", MessageTypes.Started))[0];
        Assert.True(started.At >= connected, $"started {started.At:O}, worker connected {connected:O}");
        Assert.Empty(await bed.RowsOfAsync(team, "Dev", MessageTypes.Failed));
    }

    [Fact]
    public async Task Killing_the_worker_fails_its_run_worker_lost_releases_its_lease_and_wakes_the_manager_once()
    {
        await using var bed = new ProcessBed();
        await bed.StartControlAsync(graceSeconds: 2);
        var w1 = bed.StartWorker("w1");
        await bed.UntilAsync("w1 is connected", async () => (await bed.WorkersAsync()).Count == 1);

        // 1. The member's run is held on w1, holding the heavy lease.
        var team = await bed.TeamAsync("Lost", "BlockHeavy", "BlockHeavyToo");
        (await bed.TellAsync(team, "BlockHeavy", "Hold the heavy lease.")).EnsureSuccessStatusCode();
        await bed.UntilAsync("the run holds the lease on w1", () =>
            Task.FromResult(bed.FakeRuns().Any(r => r.Member == "BlockHeavy" && r.Lease is not null)));
        Assert.Contains("\"outcome\":\"granted\"", bed.FakeRuns().Single(r => r.Member == "BlockHeavy").Lease);
        Assert.Contains(w1.Pid, bed.FakeRuns().Single(r => r.Member == "BlockHeavy").Chain);
        await bed.UntilAsync("the sample shows the member holding heavy", async () => Holders(await bed.GetAsync("/api/capacity")).Contains("BlockHeavy"));

        // 2. A second worker, there to take what comes next.
        var w2 = bed.StartWorker("w2");
        await bed.UntilAsync("w2 is connected", async () => (await bed.WorkersAsync()).Count == 2);

        // 3. w1 is killed.
        w1.KillAlone();

        await bed.UntilAsync("the run failed worker-lost", async () =>
            (await bed.RowsOfAsync(team, "BlockHeavy", MessageTypes.Failed)).Count == 1);
        var failed = Assert.Single(await bed.RowsOfAsync(team, "BlockHeavy", MessageTypes.Failed));
        Assert.Equal(FailureClasses.WorkerLost, failed.FailureClass);
        Assert.NotEqual(FailureClasses.AgentFault, failed.FailureClass);
        Assert.Empty(await bed.RowsOfAsync(team, "BlockHeavy", MessageTypes.Completed));

        // The lease is free: the sample says so, and another member's run takes it.
        await bed.UntilAsync("the heavy lease is free", async () => Holders(await bed.GetAsync("/api/capacity")).Count == 0);
        (await bed.TellAsync(team, "BlockHeavyToo", "Take the heavy lease now.")).EnsureSuccessStatusCode();
        await bed.UntilAsync("the second member holds the lease", () =>
            Task.FromResult(bed.FakeRuns().Any(r => r.Member == "BlockHeavyToo" && r.Lease is not null)));
        Assert.Contains("\"outcome\":\"granted\"", bed.FakeRuns().Single(r => r.Member == "BlockHeavyToo").Lease);

        // The Manager is woken by the failure, once, on w2, and its run completes.
        await bed.UntilAsync("the Manager's run caused by the failure completed", async () =>
        {
            var rows = await bed.RowsOfAsync(team, WipLedger.ManagerName);
            var started = rows.Where(r => r.Type == MessageTypes.Started && r.CausationSeq == failed.Seq).ToList();
            return started.Count == 1 && rows.Any(r => r.Type == MessageTypes.Completed && r.Seq > started[0].Seq);
        });
        Assert.Contains(bed.FakeRuns(), r => r.Member == WipLedger.ManagerName && r.Chain.Contains(w2.Pid));

        // Settled; and still once after a further window of three graces.
        var window = DateTime.UtcNow.AddSeconds(6);
        while (DateTime.UtcNow < window) await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Single(await bed.RowsOfAsync(team, WipLedger.ManagerName, MessageTypes.Started), r => r.CausationSeq == failed.Seq);
        Assert.Single(await bed.RowsOfAsync(team, "BlockHeavy", MessageTypes.Failed));
        Assert.DoesNotContain(await bed.RowsOfAsync(team, "BlockHeavy", MessageTypes.Failed), r => r.FailureClass == FailureClasses.Interrupted);

        bed.Go("BlockHeavyToo");
    }

    [Fact]
    public async Task A_reconnecting_worker_s_stale_run_is_stopped()
    {
        await using var bed = new ProcessBed();
        await bed.StartControlAsync(graceSeconds: 2, keepAliveSeconds: 1);
        var w1 = bed.StartWorker("w1");
        await bed.UntilAsync("w1 is connected", async () => (await bed.WorkersAsync()).Count == 1);

        var team = await bed.TeamAsync("Stale", "BlockStale");
        (await bed.TellAsync(team, "BlockStale", "Wait for go.")).EnsureSuccessStatusCode();
        await bed.UntilAsync("the run is held", () => Task.FromResult(bed.FakeRuns().Any(r => r.Member == "BlockStale")));
        var run = bed.FakeRuns().Single(r => r.Member == "BlockStale");

        // Stopped, not killed: it answers no ping, its grace passes, and control ends its run.
        ProcessBed.Signal(w1, "STOP");
        try
        {
            await bed.UntilAsync("the run failed worker-lost", async () =>
                (await bed.RowsOfAsync(team, "BlockStale", MessageTypes.Failed)).Any(r => r.FailureClass == FailureClasses.WorkerLost));
        }
        finally
        {
            ProcessBed.Signal(w1, "CONT");
        }

        // Back on the same session, still holding the run: control tells it to stop it.
        await bed.UntilAsync("the stale run is stopped", () => Task.FromResult(!ProcessBed.Alive(run.Pid)));
        Assert.DoesNotContain(bed.FakeRuns(), r => r.Pid == run.Pid && r.Ended);

        var diagnostics = await bed.GetAsync("/api/diagnostics?kind=" + DiagnosticKinds.WorkerStaleRunStopped);
        Assert.Contains(
            $"Stopped run {team}/BlockStale that worker w1 still held after control had ended it.",
            diagnostics.ToString());

        // One terminal row, and no run exists twice.
        await bed.UntilAsync("w1 is back", async () => (await bed.WorkersAsync()).Any(w => w.GetProperty("state").GetString() == "connected"));
        var ends = await bed.RowsOfAsync(team, "BlockStale", MessageTypes.Completed, MessageTypes.Failed);
        Assert.Single(ends);
        Assert.Equal(MessageTypes.Failed, ends[0].Type);
    }

    [Fact]
    public async Task A_worker_with_a_wrong_key_exits_with_controls_sentence()
    {
        await using var bed = new ProcessBed();
        await bed.StartControlAsync();

        var wrong = bed.StartWorker("wrong", key: "not-the-worker-key");
        await bed.UntilAsync("the worker exits", () => Task.FromResult(wrong.Exited), TimeSpan.FromSeconds(30));
        await wrong.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ControlConnection.RefusedExitCode, wrong.ExitCode);
        Assert.Contains(WorkerSentences.WrongKey, wrong.Text());
        Assert.Empty(await bed.WorkersAsync());
    }

    [Fact]
    public async Task A_worker_as_another_user_on_controls_data_root_runs_normally()
    {
        var nobody = AgentLaunchUser.Resolve("nobody");
        if (!nobody.Switches)
        {
            Assert.Skip($"Starting a worker as another user needs this test process to switch users, and it cannot: {nobody.Reason}");
        }

        await using var bed = new ProcessBed();
        await bed.StartControlAsync();
        var team = await bed.TeamAsync("Apart", "Dev");

        // The database and the key ring stay control's user's alone.
        foreach (var path in Directory.GetFiles(bed.Root, "messages.db*")) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.SetUnixFileMode(Path.Combine(bed.Root, "keys"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        // The other user is given exactly what a worker writes: the fake CLI's out folder, the workers'
        // state folder, the member temporary folders' root and the member's workspace. The folders on
        // the way to them it may only pass through; the go folder and the fake CLI it may only read.
        var tempRoot = MemberTemp.RootUnder(bed.Root).Path;
        var workspace = Directory.CreateDirectory(Path.Combine(bed.Root, "teams", team, "workspaces", "Dev")).FullName;
        string[] granted = [bed.Out, bed.State, tempRoot, workspace];
        foreach (var dir in granted.Where(d => d.StartsWith(bed.Work + "/", StringComparison.Ordinal)))
        {
            // (A temporary root outside the bed is the system's /tmp, open to every user already.)
            Grant(dir, UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
        }

        foreach (var dir in new[] { bed.Work, bed.Root, Path.Combine(bed.Root, "teams"), Path.Combine(bed.Root, "teams", team), Path.GetDirectoryName(workspace)! })
        {
            Grant(dir, UnixFileMode.OtherExecute);
        }

        Grant(bed.GoFolder, UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        Grant(bed.Fake, UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        // Preconditions, before the worker starts. The other user can start this build's Host at all;
        // if it cannot, that is this machine's layout, not the worker's behaviour.
        var dll = Path.Combine(AppContext.BaseDirectory, "Harness.Host.dll");
        var (runs, why) = await RunAsync([.. nobody.Prefix, "sh", "-c", $"dotnet --list-runtimes >/dev/null && test -r '{dll}'"]);
        if (runs != 0)
        {
            Assert.Skip($"The other user cannot run dotnet or read {dll} from this test's folder: {why}");
        }

        // It can write each granted folder...
        foreach (var dir in granted)
        {
            var (code, said) = await RunAsync([.. nobody.Prefix, "sh", "-c", $"touch '{dir}/.probe' && rm '{dir}/.probe'"]);
            Assert.True(code == 0, $"the other user cannot write {dir}: {said}");
        }

        // ...and can read neither the database nor the key ring.
        foreach (var probe in new[] { $"cat '{bed.Root}/messages.db' >/dev/null", $"ls '{bed.Root}/keys'" })
        {
            var (code, said) = await RunAsync([.. nobody.Prefix, "sh", "-c", probe]);
            Assert.NotEqual(0, code);
            Assert.Contains("Permission denied", said);
        }

        bed.StartWorker("other-user", prefix: nobody.Prefix);
        (await bed.TellAsync(team, "Dev", "Run as the other user.")).EnsureSuccessStatusCode();
        await bed.UntilAsync("Dev completed", async () => (await bed.RowsOfAsync(team, "Dev", MessageTypes.Completed)).Count >= 1);
        Assert.Matches("^2[0-9][0-9]$", bed.FakeRuns().First(r => r.Member == "Dev").Progress ?? "");
    }

    [Fact]
    public async Task A_worker_process_never_loads_the_database_or_the_key_ring()
    {
        await using var bed = new ProcessBed();

        // A view of a data root whose database and key ring are links to nowhere, as the worker's working
        // directory: opening either fails, here and for the worker.
        var view = Directory.CreateDirectory(Path.Combine(bed.Work, "view")).FullName;
        File.CreateSymbolicLink(Path.Combine(view, "messages.db"), Path.Combine(bed.Work, "nowhere.db"));
        Directory.CreateSymbolicLink(Path.Combine(view, "keys"), Path.Combine(bed.Work, "no-keys"));
        Assert.ThrowsAny<IOException>(() => File.OpenRead(Path.Combine(view, "messages.db")).Dispose());
        Assert.ThrowsAny<IOException>(() => Directory.GetFiles(Path.Combine(view, "keys")));

        await bed.StartControlAsync();
        var w1 = bed.StartWorker("w1", cwd: view);
        var team = await bed.TeamAsync("Clean", "Dev");
        (await bed.TellAsync(team, "Dev", "Run.")).EnsureSuccessStatusCode();
        await bed.UntilAsync("Dev completed", async () => (await bed.RowsOfAsync(team, "Dev", MessageTypes.Completed)).Count >= 1);

        // The worker is alive, has run a member end to end, and has never mapped a store's code.
        Assert.False(w1.Exited);
        var maps = await File.ReadAllTextAsync($"/proc/{w1.Pid}/maps", TestContext.Current.CancellationToken);
        foreach (var forbidden in new[]
        {
            "e_sqlite3", "Microsoft.Data.Sqlite.dll", "SQLitePCLRaw", "Microsoft.AspNetCore.DataProtection.dll", "Harness.Identity.dll",
        })
        {
            Assert.DoesNotContain(forbidden, maps, StringComparison.Ordinal);
        }

        var keys = Path.Combine(bed.Root, "keys");
        Assert.DoesNotContain(keys, maps, StringComparison.Ordinal);
        Assert.DoesNotContain("messages.db", maps, StringComparison.Ordinal);
        foreach (var fd in Directory.GetFileSystemEntries($"/proc/{w1.Pid}/fd"))
        {
            var target = new FileInfo(fd).LinkTarget ?? "";
            Assert.False(target.StartsWith(keys, StringComparison.Ordinal) || target.Contains("messages.db", StringComparison.Ordinal), target);
        }
    }

    private static bool Measured(IReadOnlyList<JsonElement> workers, string id) =>
        workers.FirstOrDefault(w => w.GetProperty("id").GetString() == id) is { ValueKind: JsonValueKind.Object } worker
        && worker.GetProperty("capacity").GetProperty("sampledAt").ValueKind == JsonValueKind.String;

    private static List<string> Holders(JsonElement capacity) =>
        capacity.GetProperty("latest") is { ValueKind: JsonValueKind.Object } latest
        && latest.GetProperty("heavyLease") is { ValueKind: JsonValueKind.Object } lease
            ? [.. lease.GetProperty("holding").EnumerateArray().Select(h => h.GetProperty("member").GetString()!)]
            : [];

    /// <summary>A cgroup v2 fixture: 4 CPUs, <paramref name="limit"/> bytes of memory, <paramref name="inUse"/> of it anonymous.</summary>
    private static string Cgroup(ProcessBed bed, string name, long inUse, long limit = 10_000_000_000)
    {
        var root = Directory.CreateDirectory(Path.Combine(bed.Work, "cgroup-" + name)).FullName;
        File.WriteAllText(Path.Combine(root, "cgroup.controllers"), "cpu io memory pids");
        File.WriteAllText(Path.Combine(root, "cpu.max"), "400000 100000");
        File.WriteAllText(Path.Combine(root, "cpu.stat"), "usage_usec 1000\nnr_throttled 0\nthrottled_usec 0\n");
        File.WriteAllText(Path.Combine(root, "memory.max"), $"{limit}");
        File.WriteAllText(Path.Combine(root, "memory.current"), $"{inUse}");
        File.WriteAllText(Path.Combine(root, "memory.stat"), $"anon {inUse}\nfile 0\nkernel 0\nshmem 0\n");
        File.WriteAllText(Path.Combine(root, "pids.current"), "10");
        File.WriteAllText(Path.Combine(root, "pids.max"), "max");
        return root;
    }

    private static void Grant(string path, UnixFileMode mode) => File.SetUnixFileMode(path, File.GetUnixFileMode(path) | mode);

    private static async Task<(int Code, string Said)> RunAsync(IReadOnlyList<string> command)
    {
        var start = new System.Diagnostics.ProcessStartInfo(command[0])
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in command.Skip(1)) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        return (process.ExitCode, await output + await error);
    }
}
