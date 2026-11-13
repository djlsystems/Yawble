using System.Text.Json;
using Yawble.Core.Bus;
using Yawble.Core.Sandboxing;
using AgentCli = Yawble.Core.Policy.AgentCli;
using Yawble.Core.Scheduling;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Launch;
using Xunit;

namespace Yawble.Core.Tests.Scheduling;

/// <summary>
/// Teams under one run limit in REAL sandboxes (Linux, bubblewrap): two teams of three agents (a Manager and two
/// members each, Claude and Grok mixed) under one global limit of 3. Every run is the real launcher in a real
/// sandbox, through the real per-run gateway and yawble-bridge; only the agent CLI is the stand-in and every secret is
/// a fake. Live sandboxes are counted from the OS process list (/proc), never from the scheduler's own books.
/// </summary>
[Collection(RealSandboxCollection.Name)]
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public class RealSandboxTeamsUnderOneLimitTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object Member(string say, double holdSeconds) => new
    {
        say,
        bus = new object[]
        {
            new { tool = "progress", args = new { status = say + ": working" } },
            new { tool = "sleep", args = new { seconds = holdSeconds } },
            new { tool = "handback", args = new { delivered = say + ": done" } },
        },
    };

    /// <summary>First run: split the task between the two members. Every later run: try to declare (refused until both handed back).</summary>
    private static object Manager(string first, string second) => new
    {
        runs = new object[]
        {
            new
            {
                say = "split",
                bus = new object[]
                {
                    new { tool = "tell", args = new { member = first, instruction = "write the first half" } },
                    new { tool = "tell", args = new { member = second, instruction = "write the second half" } },
                },
            },
            new
            {
                say = "declare",
                bus = new object[] { new { tool = "workflow_complete", args = new { delivered = "both halves done" } } },
            },
        },
    };

    [Fact]
    public async Task TwoTeamsOfThreeUnderLimitThreeNeverExceedThreeLiveSandboxesAndBothWorkflowsComplete()
    {
        RealSandbox.SkipUnlessAvailable();
        const int Limit = 3;
        var scenario = new Dictionary<string, object>
        {
            ["MgrA"] = Manager("ada", "ali"),
            ["MgrB"] = Manager("bea", "ben"),
            ["ada"] = Member("ada", 4),
            ["ali"] = Member("ali", 4),
            ["bea"] = Member("bea", 4),
            ["ben"] = Member("ben", 4),
        };
        await using var kit = await RealSandboxLauncherTests.Kit.StartAsync(scenario, limit: Limit);
        var bus = kit.Launch.Bus;
        await bus.CreateTeamAsync("teama", "MgrA", Ct);
        await bus.AddMemberAsync("teama", "ada", "claude", Ct);
        await bus.AddMemberAsync("teama", "ali", "grok", Ct);
        await bus.CreateTeamAsync("teamb", "MgrB", Ct);
        await bus.AddMemberAsync("teamb", "bea", "grok", Ct);
        await bus.AddMemberAsync("teamb", "ben", "claude", Ct);

        await using var sampler = new SandboxSampler(kit.App, bus);
        var tellA = await bus.PersonTellAsync("teama", "MgrA", "build feature A with your team", ct: Ct);
        var tellB = await bus.PersonTellAsync("teamb", "MgrB", "build feature B with your team", ct: Ct);

        await Wait.UntilAsync(() => kit.Launch.Messages().Count(m => m.Type == MessageTypes.WorkflowCompleted) == 2,
            "both workflows to be declared complete", TimeSpan.FromMinutes(4));
        await Wait.UntilAsync(() => bus.IsQuiescentAsync(Ct).GetAwaiter().GetResult(), "the bus to settle", TimeSpan.FromMinutes(1));
        await Wait.UntilAsync(() => SandboxSampler.Live(kit.App).Sandboxes == 0, "every sandbox to exit", TimeSpan.FromSeconds(30));
        await sampler.StopAsync();

        var messages = kit.Launch.Messages();
        using var store = kit.Launch.Db.OpenStore();
        var members = store.AllMembers().ToDictionary(m => m.Id);
        var runs = store.Runs().Where(r => members[r.MemberId].Team is "teama" or "teamb").OrderBy(r => r.Id).ToList();
        string Name(BusStore.RunRow r) => members[r.MemberId].Name;

        RealSandbox.Report($"teams under one limit, real sandboxes: limit {Limit}; {sampler.Samples} process-list samples; max live sandboxes {sampler.MaxSandboxes}, " +
            $"max live agent CLIs {sampler.MaxAgents}, max scheduler running {sampler.MaxRunning}, max queued {sampler.MaxQueued}; " +
            $"runs in admission order: {string.Join(", ", runs.Select(r => $"{Name(r)}#{r.Id}({r.State})"))}; " +
            $"queue labels seen: {string.Join(" | ", sampler.LabelsAtMaxQueue)}");

        // Never more than 3 sandboxes alive at once on the OS, and the limit was actually reached.
        Assert.True(sampler.Samples > 50, $"only {sampler.Samples} samples");
        Assert.True(sampler.MaxSandboxes <= Limit, $"{sampler.MaxSandboxes} sandboxes alive at once (limit {Limit})");
        Assert.True(sampler.MaxAgents <= Limit, $"{sampler.MaxAgents} agent CLIs alive at once (limit {Limit})");
        Assert.Equal(Limit, sampler.MaxSandboxes);
        Assert.True(sampler.MaxRunning <= Limit, $"the scheduler reported {sampler.MaxRunning} running");

        // Waiting agents showed "Queued · position N of M", numbered 1..M in one queue across both teams.
        Assert.True(sampler.MaxQueued >= 1, "nothing ever waited: the test did not exercise the queue");
        Assert.Equal(Enumerable.Range(1, sampler.MaxQueued).Select(i => $"Queued · position {i} of {sampler.MaxQueued}"), sampler.LabelsAtMaxQueue);

        // Oldest first across teams: admission order (run id) follows queue order (enqueue time), for every run.
        Assert.Equal(runs.Select(r => r.Id), runs.OrderBy(r => r.QueueId).Select(r => r.Id));
        Assert.Equal(runs.OrderBy(r => r.QueueId).Select(r => r.QueuedAt), runs.Select(r => r.QueuedAt).Order(StringComparer.Ordinal));
        // Both Managers were told first, so they hold the first two slots, in the person's order.
        Assert.Equal(["MgrA", "MgrB"], runs.Take(2).Select(Name));

        // Every run completed in its sandbox; both members of each team handed back on its own workflow; both declared.
        Assert.All(runs, r => Assert.True(r.State == "completed", $"{Name(r)} run {r.Id}: {r.State} {r.Reason}"));
        foreach (var (team, tell, names) in new[] { ("teama", tellA, new[] { "ada", "ali" }), ("teamb", tellB, new[] { "bea", "ben" }) })
        {
            foreach (var name in names)
                Assert.Contains(messages, m => m.Type == MessageTypes.Handback && m.Source == $"{team}/{name}" && m.CorrelationId == tell.CorrelationId);
            Assert.Single(messages, m => m.Type == MessageTypes.WorkflowCompleted && m.CorrelationId == tell.CorrelationId);
        }
        // Mixed CLIs: each member's sandbox ran its own CLI (the stand-in installed as claude and as grok).
        foreach (var (team, name, cli) in new[] { ("teama", "ada", AgentCli.Claude), ("teama", "ali", AgentCli.Grok), ("teamb", "bea", AgentCli.Grok), ("teamb", "ben", AgentCli.Claude) })
        {
            var h = kit.Launch.Host;
            var layout = Yawble.Core.Policy.RunLayout.Standard(h.Os, h.Home, h.AppDataRoot, h.InstallDir,
                new Yawble.Core.Policy.MemberRef(team, name), cli, await kit.Launch.Launcher.DetectCliAsync(cli, false, Ct), []);
            var o = JsonDocument.Parse(File.ReadAllText(Path.Combine(layout.Workspace, "standin-out.json"))).RootElement;
            Assert.Equal(cli == AgentCli.Claude ? "claude" : "grok", o.GetProperty("cli").GetString());
            Assert.True(o.TryGetProperty("done", out _), $"{team}/{name}: {o}");
        }
    }

    /// <summary>
    /// Samples the OS process list every few ms: sandboxes (outermost bwrap processes whose command line names
    /// this test's app folder) and agent CLIs (the stand-in, by its install path), plus the scheduler's own view.
    /// </summary>
    private sealed class SandboxSampler : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _task;

        public SandboxSampler(string appRoot, BusEngine bus)
        {
            _task = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    var (sandboxes, agents) = Live(appRoot);
                    MaxSandboxes = Math.Max(MaxSandboxes, sandboxes);
                    MaxAgents = Math.Max(MaxAgents, agents);
                    var snap = await bus.SchedulerSnapshotAsync();
                    MaxRunning = Math.Max(MaxRunning, snap.Running);
                    if (snap.Queued > MaxQueued)
                    {
                        MaxQueued = snap.Queued;
                        LabelsAtMaxQueue = [.. snap.Queue.Select(q => q.Label)];
                    }
                    Samples++;
                    try { await Task.Delay(10, _stop.Token); } catch (OperationCanceledException) { }
                }
            });
        }

        public int Samples { get; private set; }
        public int MaxSandboxes { get; private set; }
        public int MaxAgents { get; private set; }
        public int MaxRunning { get; private set; }
        public int MaxQueued { get; private set; }
        public IReadOnlyList<string> LabelsAtMaxQueue { get; private set; } = [];

        public static (int Sandboxes, int Agents) Live(string appRoot)
        {
            var procs = new Dictionary<int, (int PPid, string Comm, string Cmd)>();
            foreach (var dir in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(dir), out var pid)) continue;
                try
                {
                    var cmd = File.ReadAllText(Path.Combine(dir, "cmdline")).Replace('\0', ' ');
                    var stat = File.ReadAllText(Path.Combine(dir, "stat"));
                    var close = stat.LastIndexOf(')');
                    var comm = stat[(stat.IndexOf('(') + 1)..close];
                    var ppid = int.Parse(stat[(close + 2)..].Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture);
                    procs[pid] = (ppid, comm, cmd);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (FormatException) { }
            }
            bool IsSandbox(int pid) => procs.TryGetValue(pid, out var p) && p.Comm == "bwrap" && p.Cmd.Contains(appRoot, StringComparison.Ordinal);
            var sandboxes = procs.Keys.Count(pid => IsSandbox(pid) && !IsSandbox(procs[pid].PPid));
            var install = Path.GetDirectoryName(appRoot)!;
            var agents = procs.Values.Count(p => p.Comm != "bwrap" && p.Cmd.Contains("python3 -I", StringComparison.Ordinal)
                && p.Cmd.Contains(install, StringComparison.Ordinal) && (p.Cmd.Contains("/claude", StringComparison.Ordinal) || p.Cmd.Contains("/grok", StringComparison.Ordinal)));
            return (sandboxes, agents);
        }

        public async Task StopAsync()
        {
            await _stop.CancelAsync();
            await _task;
        }

        public async ValueTask DisposeAsync()
        {
            if (!_stop.IsCancellationRequested) await StopAsync();
            _stop.Dispose();
        }
    }
}
