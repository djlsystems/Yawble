using System.Collections.Concurrent;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// A run's text is redacted of what its own child's environment carries, read where that environment
/// is built - on the worker - and control keeps the set the worker applied. In one process the set
/// control computes and the set the worker reads are the same.
/// </summary>
public sealed class WorkerRedactionTests : IDisposable
{
    private const string WorkerOnly = "worker-only-secret-7Hq2";

    private static int _members;

    private readonly ContainerId _member = new("alpha", $"redacted-{Interlocked.Increment(ref _members)}");

    private readonly string _root = Directory.CreateTempSubdirectory("harness-worker-redaction-").FullName;

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        MemberTempCleanup.Remove(_root);
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task The_worker_redacts_what_its_own_child_environment_carries()
    {
        var (directory, secrets, worker) = Bed();
        var script = await Script("printf '%s' \"$WORKER_ONLY_KEY\"\n");

        // Control sent no value at all: only the name of the variable that holds one.
        var result = await directory.RunAsync(worker, Start(script, ["WORKER_ONLY_KEY"]), Ct);

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain(WorkerOnly, result.Output, StringComparison.Ordinal);
        Assert.Contains(DiagnosticRedaction.Placeholder, result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Control_remembers_the_set_the_worker_applied()
    {
        var (directory, secrets, worker) = Bed();
        var script = await Script("true\n");

        await directory.RunAsync(worker, Start(script, ["WORKER_ONLY_KEY"]), Ct);

        Assert.Equal($"key {DiagnosticRedaction.Placeholder}", secrets.For(_member).Apply($"key {WorkerOnly}"));
    }

    [Fact]
    public async Task Without_the_names_the_worker_adds_nothing_of_its_own()
    {
        var (directory, secrets, worker) = Bed();
        var script = await Script("printf '%s' \"$WORKER_ONLY_KEY\"\n");

        var result = await directory.RunAsync(worker, Start(script, null), Ct);

        Assert.Equal(WorkerOnly, result.Output);
        Assert.Same(ValueRedactor.Empty, secrets.For(_member));
    }

    [Fact]
    public async Task In_one_process_the_two_sets_are_equal()
    {
        var variable = AgentEnvironment.ProviderVariables.Order(StringComparer.Ordinal).First();
        var script = await Script("true\n");
        var catalog = new AgentCatalog(
        [
            new AgentDefinition("probe", AgentMode.Headless, new AgentLaunch("sh", [script], LanguageModel: false)),
        ]);

        var heartbeat = new RunHeartbeat();
        var launcher = new RunLauncher(heartbeat, reports: false, lookup: LaunchLookup.Once);
        var directory = new RunDirectory();
        var applied = new ConcurrentQueue<RunCredentialApplied>();
        var inProcess = InProcessWorker.Connect(
            WorkerId.Local,
            events => new WorkerHost(WorkerId.Local, events, launcher, heartbeat),
            async (envelope, ct) =>
            {
                if (envelope.Event is RunCredentialApplied credential) applied.Enqueue(credential);
                await directory.HandleAsync(envelope, ct);
            });
        var recording = new Recording(inProcess.Worker);
        var runner = new ProcessAgentRunner(catalog, recording, directory, launcher, () => null, null);

        await runner.RunAsync(
            new AgentInvocation(
                _member, "You are a probe.", "hello", Workspace(),
                new Dictionary<string, string> { [variable] = "handed-in-value-Lm4x" }, Agent: "probe"),
            Ct);

        var start = Assert.Single(recording.Sent.OfType<StartRun>());
        var fromControl = start.Redaction!;
        var fromWorker = Assert.Single(applied).Redaction!;

        Assert.Contains("handed-in-value-Lm4x", fromControl.Forms);
        Assert.NotNull(start.CredentialNames);
        Assert.Contains(variable, start.CredentialNames!);
        Assert.Equal(fromControl.Forms, fromWorker.Forms);
        inProcess.Close();
    }

    private (RunDirectory Directory, RunSecrets Secrets, IRunWorker Worker) Bed()
    {
        var secrets = new RunSecrets(new AgentCatalog([]));
        var directory = new RunDirectory(secrets: secrets);
        var heartbeat = new RunHeartbeat();
        var launcher = new RunLauncher(heartbeat, reports: false, lookup: LaunchLookup.Once);
        var worker = InProcessWorker.Connect(
            WorkerId.Local, events => new WorkerHost(WorkerId.Local, events, launcher, heartbeat), directory.HandleAsync);
        return (directory, secrets, worker.Worker);
    }

    private StartRun Start(string script, IReadOnlyList<string>? names) =>
        new(RunId.For(_member), "probe", "You are a probe.", "hello", "", Workspace(),
            new Dictionary<string, string> { ["WORKER_ONLY_KEY"] = WorkerOnly }, null,
            new RunLaunch("sh", [script], null, null, null, false, null, null, null, []), null, null, null,
            Redaction: ValueRedactor.Empty, CredentialNames: names);

    private string Workspace() => Directory.CreateDirectory(Path.Combine(_root, "workspace")).FullName;

    private async Task<string> Script(string body)
    {
        var path = Path.Combine(_root, $"run-{Guid.NewGuid():N}.sh");
        await File.WriteAllTextAsync(path, body, Ct);
        return path;
    }

    private sealed class Recording(IRunWorker inner) : IRunWorker
    {
        public ConcurrentQueue<ControlMessage> Sent { get; } = new();

        public WorkerId Id => inner.Id;

        public Task Closed => inner.Closed;

        public Task SendAsync(ControlMessage message, CancellationToken ct = default)
        {
            Sent.Enqueue(message);
            return inner.SendAsync(message, ct);
        }
    }
}
