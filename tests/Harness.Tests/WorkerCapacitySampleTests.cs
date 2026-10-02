using System.Collections.Concurrent;
using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests;

/// <summary>A worker's capacity sample names every run it has open, however many one member has.</summary>
public sealed class WorkerCapacitySampleTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("harness-worker-sample-").FullName;

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        File.WriteAllText(Path.Combine(_root, "go"), "");
        MemberTempCleanup.Remove(_root);
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A child still leaving; the temp directory is the system's to clear.
        }
    }

    [Fact]
    public async Task A_sample_lists_every_open_run_of_a_member()
    {
        var member = new ContainerId("alpha", "sampled-twice");
        var events = new Events();
        var heartbeat = new RunHeartbeat();
        var host = new WorkerHost(
            new WorkerId("w1"), events, new RunLauncher(heartbeat, reports: false, lookup: LaunchLookup.Once), heartbeat);

        var go = Path.Combine(_root, "go");
        var script = Path.Combine(_root, "wait.sh");
        await File.WriteAllTextAsync(script, $"i=0; while [ ! -e '{go}' ] && [ $i -lt 400 ]; do sleep 0.05; i=$((i+1)); done\n", Ct);

        var first = Start(member, script);
        var second = Start(member, script);
        await host.ApplyAsync(first, Ct);
        await host.ApplyAsync(second, Ct);
        await host.ApplyAsync(new SampleCapacity(), Ct);

        var sampled = Assert.Single(events.Published.Select(e => e.Event).OfType<WorkerCapacitySampled>());
        Assert.Equal(
            new[] { first.Run, second.Run }.OrderBy(r => r.Nonce),
            sampled.OpenRuns.OrderBy(r => r.Nonce));

        await File.WriteAllTextAsync(go, "", Ct);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (host.OpenRuns().Count > 0 && DateTime.UtcNow < deadline) await Task.Delay(20, Ct);
        Assert.Empty(host.OpenRuns());
    }

    private StartRun Start(ContainerId member, string script) =>
        new(RunId.For(member), "probe", "You are a probe.", "hello", "", Directory.CreateDirectory(Path.Combine(_root, "workspace")).FullName,
            new Dictionary<string, string>(), null,
            new RunLaunch("sh", [script], null, null, null, false, null, null, null, []), null, null, null);

    private sealed class Events : IRunEvents
    {
        public ConcurrentQueue<WorkerEnvelope> Published { get; } = new();

        public Task PublishAsync(WorkerEnvelope envelope, CancellationToken ct = default)
        {
            Published.Enqueue(envelope);
            return Task.CompletedTask;
        }
    }
}
