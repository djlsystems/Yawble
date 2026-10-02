using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Capacity;

namespace Harness.Tests;

/// <summary>
/// A REMOVAL'S AGENT PASS IS A WORKER'S. What the Host cannot remove is sent to a connected worker as
/// the confined top-level paths, which the worker checks again and removes as the agent; with no worker
/// what remains is recorded unfinished, with the reason, for a workspace, a team root and an emptied
/// folder. In control each worker that joins retries the unfinished removals once; in one process
/// nothing joins and a run home is removed by the worker's own rule. Runs everywhere: the Host's
/// refusals are a seam, the agent user a decided one, and the pass a scripted worker.
/// </summary>
public sealed class FolderRemovalOnWorkersTests : IDisposable
{
    private const string Key = "the-worker-key-Rm4";

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private readonly string _root = Directory.CreateTempSubdirectory("harness-removal-workers-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>An agent user this process would switch to, decided without a real one.</summary>
    private static readonly AgentLaunchUser Switching =
        AgentLaunchUser.Decide("agent", (1001, 1001), 1000, (1UL << 6) | (1UL << 7), "/usr/bin/setpriv", _ => true);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task The_agent_pass_is_asked_of_a_connected_worker_with_the_confined_top_level_paths()
    {
        var workspace = Workspace("ws", out var agentOwned);
        var worker = Removing("w1");
        var unfinished = new Rows();
        var removal = new FolderRemoval(null, unfinished, Refusing(agentOwned), new WorkerAgentPass(AsksOf(worker), always: true));

        var report = await removal.RemoveWorkspaceAsync(workspace, "Alpha", "Developer", Ct);

        var asked = Assert.IsType<RemoveAsAgent>(Assert.Single(worker.Sent));
        Assert.Equal(workspace, asked.Boundary);
        Assert.Equal([agentOwned], asked.Paths);
        Assert.True(report.Complete);
        Assert.False(Directory.Exists(workspace));
        Assert.Empty(unfinished.Recorded);
    }

    [Fact]
    public async Task With_no_worker_what_remains_is_recorded_unfinished_with_the_reason()
    {
        var asks = new WorkerAsks(() => throw new InvalidOperationException("No worker is connected."));
        var unfinished = new Rows();

        // A workspace.
        var workspace = Workspace("ws", out var inWorkspace);
        var removal = new FolderRemoval(null, unfinished, Refusing(inWorkspace), new WorkerAgentPass(asks, always: true));
        var report = await removal.RemoveWorkspaceAsync(workspace, "Alpha", "Developer", Ct);
        AssertLeftWithReason(report, inWorkspace);
        Assert.Equal(RemovalKinds.Workspace, unfinished.Recorded[workspace].Kind);

        // A team root, its marker kept.
        var root = Directory.CreateDirectory(Path.Combine(_root, "teams", "alpha")).FullName;
        File.WriteAllText(TeamPaths.MarkerIn(root), "Alpha");
        var inRoot = Directory.CreateDirectory(Path.Combine(root, "agent-owned", "deep")).Parent!.FullName;
        File.WriteAllText(Path.Combine(inRoot, "deep", "f"), "x");
        report = await new FolderRemoval(null, unfinished, Refusing(inRoot), new WorkerAgentPass(asks, always: true))
            .RemoveTeamRootAsync(root, "Alpha", Ct);
        AssertLeftWithReason(report, inRoot);
        Assert.Equal(RemovalKinds.TeamRoot, unfinished.Recorded[root].Kind);
        Assert.True(File.Exists(TeamPaths.MarkerIn(root)));

        // A folder a reset empties.
        var emptied = Directory.CreateDirectory(Path.Combine(_root, "emptied")).FullName;
        var inEmptied = Directory.CreateDirectory(Path.Combine(emptied, "agent-owned")).FullName;
        File.WriteAllText(Path.Combine(inEmptied, "f"), "x");
        report = await new FolderRemoval(null, unfinished, Refusing(inEmptied), new WorkerAgentPass(asks, always: true))
            .EmptyAsync(emptied, "Alpha", Ct);
        AssertLeftWithReason(report, inEmptied);
        Assert.Equal(RemovalKinds.Emptied, unfinished.Recorded[emptied].Kind);
    }

    [Fact]
    public async Task The_worker_refuses_a_path_that_is_not_confined()
    {
        var boundary = Directory.CreateDirectory(Path.Combine(_root, "boundary")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "keep"), "x");
        var escaping = Path.Combine(boundary, "..", "outside");

        var worker = new WorkerAgentCli(new WorkerId("w1"), Switching, new RunHomes(null, RunHomeRemoval.For(null)));
        var answer = Assert.IsType<RemovedAsAgent>(await worker.AnswerAsync(new RemoveAsAgent("r1", boundary, [outside, escaping, boundary]), Ct));

        Assert.Equal("r1", answer.Request);
        Assert.Equal(
            string.Join(" ", new[] { outside, escaping, boundary }.Select(p => WorkerAgentCli.NotConfined(p, boundary))),
            answer.Error);
        Assert.True(File.Exists(Path.Combine(outside, "keep")));
        Assert.True(Directory.Exists(boundary));
    }

    [Fact]
    public async Task In_all_the_pass_goes_to_the_local_worker_only_where_agents_run_as_another_user()
    {
        var local = Removing("local");
        var pass = new WorkerAgentPass(AsksOf(local), always: false);

        // Agents run as this process's user: no agent pass, as before.
        var same = Workspace("same", out var sameOwned);
        var report = await new FolderRemoval(null, new Rows(), Refusing(sameOwned), pass).RemoveWorkspaceAsync(same, "Alpha", "Developer", Ct);
        Assert.Equal([sameOwned], report.Remaining);
        Assert.Null(report.Reasons);
        Assert.Empty(local.Sent);

        // Agents run as another user: the local worker's pass.
        var other = Workspace("other", out var otherOwned);
        report = await new FolderRemoval(Switching, new Rows(), Refusing(otherOwned), pass).RemoveWorkspaceAsync(other, "Alpha", "Developer", Ct);
        Assert.True(report.Complete);
        Assert.Equal([otherOwned], Assert.IsType<RemoveAsAgent>(Assert.Single(local.Sent)).Paths);
    }

    [Fact]
    public async Task A_worker_joining_retries_unfinished_removals_once()
    {
        var bed = new JoinBed(control: true);
        var (connection, links, running) = bed.Worker("w1");
        await Until(() => bed.Retries == 1 && connection.Connected);

        // Dropped and back on the same session within its grace: not a join.
        links.Last().Drop();
        await Until(() => !connection.Connected);
        await Until(() => { bed.Clock.Advance(TimeSpan.FromSeconds(1)); return connection.Connected; });
        await Task.Delay(200, Ct);
        Assert.Equal(1, bed.Retries);

        // Another worker joining retries again, once.
        var (second, _, alsoRunning) = bed.Worker("w2");
        await Until(() => second.Connected && bed.Retries == 2);

        bed.Stop();
        await running.WaitAsync(Bound, Ct);
        await alsoRunning.WaitAsync(Bound, Ct);
    }

    [Fact]
    public async Task In_all_a_worker_joining_retries_nothing()
    {
        var bed = new JoinBed(control: false);
        var (connection, _, running) = bed.Worker("w1");
        await Until(() => connection.Connected);
        await Task.Delay(200, Ct);

        Assert.False(bed.Wired);
        Assert.Equal(0, bed.Retries);

        bed.Stop();
        await running.WaitAsync(Bound, Ct);
    }

    [Fact]
    public async Task In_all_a_run_home_is_removed_by_the_workers_run_home_removal()
    {
        var homes = RunHome.Homes(null);

        // Composed with the worker's own rule, never control's FolderRemoval.
        var remove = typeof(RunHomes).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Select(f => f.GetValue(homes)).OfType<Func<string, string, Task>>().Single();
        Assert.Equal(typeof(RunHomeRemoval), remove.Method.DeclaringType!.DeclaringType ?? remove.Method.DeclaringType);

        // And it removes the home, with everything written in it and nothing beside it.
        var parent = Directory.CreateDirectory(Path.Combine(_root, "member-temp")).FullName;
        var home = (await homes.CreateAsync(parent, null, Ct))!;
        Directory.CreateDirectory(Path.Combine(home, ".claude", "projects"));
        File.WriteAllText(Path.Combine(home, ".claude", "projects", "s.jsonl"), "{}");
        await homes.RemoveAsync(home);

        Assert.False(Directory.Exists(home));
        Assert.True(Directory.Exists(RunHome.CacheBeside(home)));
    }

    private static void AssertLeftWithReason(FolderRemovalReport report, string left)
    {
        Assert.Contains(left, report.Remaining);
        Assert.NotNull(report.Reasons);
        Assert.All(report.Remaining, p => Assert.Equal(WorkerAgentPass.NoWorkerText, report.Reasons![p]));
    }

    /// <summary>A workspace holding a folder of the agent's, with a file in it, that the Host cannot remove.</summary>
    private string Workspace(string name, out string agentOwned)
    {
        var workspace = Directory.CreateDirectory(Path.Combine(_root, "workspaces", name)).FullName;
        File.WriteAllText(Path.Combine(workspace, "hosts-own"), "x");
        agentOwned = Directory.CreateDirectory(Path.Combine(workspace, "agent-owned")).FullName;
        Directory.CreateDirectory(Path.Combine(agentOwned, "nested"));
        File.WriteAllText(Path.Combine(agentOwned, "nested", "f"), "x");
        return workspace;
    }

    /// <summary>The Host's deletes, refusing everything inside <paramref name="agentOwned"/> and the folder itself, as an owner-only folder of another user refuses.</summary>
    private static FolderRemoval.HostDeletes Refusing(string agentOwned)
    {
        bool Theirs(string path) => path == agentOwned || path.StartsWith(agentOwned + Path.DirectorySeparatorChar, StringComparison.Ordinal);

        return new(
            path =>
            {
                if (Theirs(path)) throw new UnauthorizedAccessException($"Access to the path '{path}' is denied.");
                File.Delete(path);
            },
            path =>
            {
                if (Theirs(path)) throw new UnauthorizedAccessException($"Access to the path '{path}' is denied.");
                Directory.Delete(path, recursive: false);
            },
            path => Theirs(path) ? throw new UnauthorizedAccessException($"Access to the path '{path}' is denied.") : Directory.EnumerateFileSystemEntries(path));
    }

    /// <summary>A worker whose agent removes what it is handed, as rm as the agent would.</summary>
    private static ScriptedCliWorker Removing(string id) =>
        new(id, message =>
        {
            if (message is not RemoveAsAgent remove) return null;
            foreach (var path in remove.Paths) Directory.Delete(path, recursive: true);
            return new RemovedAsAgent(remove.Request, null);
        });

    private static WorkerAsks AsksOf(ScriptedCliWorker worker)
    {
        var asks = new WorkerAsks(() => worker);
        worker.Asks = asks;
        return asks;
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Bound;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Not true within {Bound.TotalSeconds:0} s.");
            await Task.Delay(10, Ct);
        }
    }

    /// <summary>The unfinished removals recorded, by path.</summary>
    private sealed class Rows : IUnfinishedRemovals
    {
        public ConcurrentDictionary<string, UnfinishedRemoval> Recorded { get; } = new(StringComparer.Ordinal);

        public Task RecordAsync(UnfinishedRemoval removal, CancellationToken ct = default)
        {
            Recorded[removal.Path] = removal;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<UnfinishedRemoval>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<UnfinishedRemoval>>([.. Recorded.Values]);

        public Task<UnfinishedRemoval?> FindAsync(string path, CancellationToken ct = default) =>
            Task.FromResult(Recorded.GetValueOrDefault(path));

        public Task ForgetAsync(string path, CancellationToken ct = default)
        {
            Recorded.TryRemove(path, out _);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Control's worker connections over real loopback sockets on a manual clock, with the join retry
    /// wired as the Host wires it for <c>control</c> or not, counting the retries it starts.
    /// </summary>
    private sealed class JoinBed
    {
        private readonly CancellationTokenSource _stop = new();
        private int _retries;

        public JoinBed(bool control)
        {
            var pool = new WorkerPool(_ => new HeadroomGate(() => 80, () => 0, Clock), clock: Clock);
            var directory = new RunDirectory(clock: Clock);
            Connections = new WorkerConnections(
                pool, new WipLedger(5, pool), directory.HandleAsync, directory.OpenOn, Key, takesWorkers: true,
                BuildVersion.Current.Version, timings: new WorkerTimings(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10)),
                clock: Clock);
            Wired = RetryWhenAWorkerJoins.Wire(control, Connections, _ =>
            {
                Interlocked.Increment(ref _retries);
                return Task.CompletedTask;
            });
        }

        public ManualTime Clock { get; } = new(DateTimeOffset.UnixEpoch.AddDays(1));

        public WorkerConnections Connections { get; }

        public bool Wired { get; }

        public int Retries => Volatile.Read(ref _retries);

        public void Stop() => _stop.Cancel();

        public (ControlConnection Connection, ConcurrentQueue<Link> Links, Task<int> Running) Worker(string id)
        {
            var links = new ConcurrentQueue<Link>();
            var connection = new ControlConnection(
                new WorkerId(id), BuildVersion.Current.Version, Key,
                async ct =>
                {
                    var (control, worker) = await Pair();
                    links.Enqueue(worker);
                    _ = Connections.AcceptAsync(control.Socket, _stop.Token);
                    return worker.Socket;
                },
                clock: Clock)
            {
                OpenRuns = () => [],
                Apply = (_, _) => Task.CompletedTask,
            };

            return (connection, links, connection.RunAsync(_stop.Token));
        }
    }

    private static async Task<(Link Control, Link Worker)> Pair()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        var connecting = client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        var server = await listener.AcceptTcpClientAsync();
        await connecting;

        return (
            new Link(server, WebSocket.CreateFromStream(server.GetStream(), isServer: true, null, Timeout.InfiniteTimeSpan)),
            new Link(client, WebSocket.CreateFromStream(client.GetStream(), isServer: false, null, Timeout.InfiniteTimeSpan)));
    }

    private sealed record Link(TcpClient Tcp, WebSocket Socket)
    {
        public void Drop() => Tcp.Dispose();
    }
}
