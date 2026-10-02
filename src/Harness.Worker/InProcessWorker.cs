using Harness.Contracts;
using Harness.Host.Capacity;
using Microsoft.Extensions.Logging;

namespace Harness.Host;

/// <summary>
/// A worker in the Host's own process: its <see cref="WorkerHost"/> joined to control's event
/// handler by an <see cref="InProcessTransport"/>. Control holds <see cref="Worker"/> and talks to
/// runs through it alone.
/// </summary>
public sealed class InProcessWorker
{
    private InProcessWorker(InProcessTransport transport, WorkerHost host)
    {
        Transport = transport;
        Host = host;
    }

    public InProcessTransport Transport { get; }

    public WorkerHost Host { get; }

    /// <summary>Control's handle on this worker.</summary>
    public IRunWorker Worker => Transport;

    /// <summary>
    /// A worker made by <paramref name="host"/>, whose events go to <paramref name="control"/>. It says
    /// it is ready before this returns.
    /// </summary>
    public static InProcessWorker Connect(
        WorkerId id, Func<IRunEvents, WorkerHost> host, Func<WorkerEnvelope, CancellationToken, Task> control,
        Action<WorkerId, StreamChunk>? streams = null)
    {
        var transport = new InProcessTransport(id);
        var worker = host(transport);
        transport.Connect(worker.ApplyAsync, control);
        transport.ConnectStreams(streams, worker.Input);
        worker.ReadyAsync().GetAwaiter().GetResult();
        return new InProcessWorker(transport, worker);
    }

    /// <summary>
    /// Closes the connection: control sees every run still open on it as lost. Never by the service
    /// container: the Host's worker stays connected while the Host stops, so a run still ending
    /// then ends in the Host's own words.
    /// </summary>
    public void Close() => Transport.Dispose();

    /// <summary>
    /// THE HOST'S OWN WORKER, composed from what control hands it: the idle-clock registry, who
    /// agent children run as, the shared update gate, the memory figures the settings give and the
    /// heavy allowance's rule, and where to read the cgroup and <c>/proc</c> (a test Host points
    /// these at fixtures). Every path is passed explicitly from <see cref="WorkerPaths"/> here, so no
    /// default path is compiled into a control caller. <paramref name="homes"/> makes and removes the
    /// home of a run that signs in with an issued credential. <paramref name="streams"/> takes what the
    /// worker streams to control; <paramref name="pty"/> spawns a person's terminal (Porta.Pty by default).
    /// </summary>
    public static InProcessWorker Create(
        WorkerId id,
        RunHeartbeat heartbeat,
        AgentLaunchUser runAs,
        AgentUpdateGate updates,
        Func<RunMemoryLimit> memoryLimit,
        Func<RunMemoryLimit> memoryCeiling,
        Func<long, int, RunMemoryLimit> heavyAllowance,
        Func<WorkerEnvelope, CancellationToken, Task> control,
        ILogger? launchLog = null,
        ILogger<RunAllowances>? allowancesLog = null,
        string? cgroupRoot = null,
        string? procRoot = null,
        RunHomes? homes = null,
        Action<WorkerId, StreamChunk>? streams = null,
        Harness.Pty.IPtyEngine? pty = null)
    {
        var proc = procRoot ?? WorkerPaths.Proc;
        var memory = RunMemoryLimits.Resolve(memoryLimit, WorkerPaths.CgroupRoot, WorkerPaths.ProcSelfCgroup, memoryCeiling);

        WorkerHost? host = null;
        var allowances = new RunAllowances(
            memory,
            heavyAllowance,
            () => host?.HeavyHolders ?? [],
            new ProcessGroupReader(proc),
            RunProcessGroups.Shared,
            procRoot: proc,
            runAs: runAs,
            log: allowancesLog);

        var launcher = new RunLauncher(
            heartbeat, launchLog, runAs, reports: true, updates: updates, memory: memory, allowances: allowances, homes: homes);

        var worker = Connect(
            id,
            events => host = new WorkerHost(
                id, events, launcher, heartbeat, allowances,
                new CgroupReader(cgroupRoot ?? WorkerPaths.CgroupRoot), new ProcessGroupReader(proc), RunProcessGroups.Shared,
                cli: new WorkerAgentCli(id, runAs, homes ?? new RunHomes(runAs, RunHomeRemoval.For(runAs)), launchLog),
                log: launchLog,
                streaming: new WorkerStreaming((IRunStreamSink)events, pty ?? new Harness.Pty.PortaPtyEngine(), runAs)),
            control,
            streams);

        worker.Memory = memory;
        worker.Allowances = allowances;
        return worker;
    }

    /// <summary>
    /// A worker in this process that answers only about its agent CLIs (a sign-in probe, commands, a
    /// removal as the agent) and takes no run: what a control part composed without the Host's own
    /// worker - a test's - asks, over the same transport and the same worker code.
    /// </summary>
    public static InProcessWorker ForAgentClis(
        WorkerId id, AgentLaunchUser? runAs, Func<WorkerEnvelope, CancellationToken, Task> control, RunHomes? homes = null)
    {
        var heartbeat = new RunHeartbeat();
        return Connect(
            id,
            events => new WorkerHost(
                id, events, new RunLauncher(heartbeat, runAs: runAs), heartbeat,
                cli: new WorkerAgentCli(id, runAs, homes ?? new RunHomes(runAs, RunHomeRemoval.For(runAs)))),
            control);
    }

    /// <summary>
    /// The run homes of the Host's own worker, removed by the worker's own <see cref="RunHomeRemoval"/>,
    /// the rule a worker process uses.
    /// </summary>
    public static RunHomes Homes(AgentLaunchUser? runAs) => new(runAs, RunHomeRemoval.For(runAs));

    /// <summary>Who agent children run as on this worker, decided once (<see cref="AgentLaunchUser.Resolve"/>).</summary>
    public static AgentLaunchUser LaunchUser(string? configured) => AgentLaunchUser.Resolve(configured);

    /// <summary>The run memory limits this worker applies; control reports them.</summary>
    public RunMemoryLimits? Memory { get; private set; }

    /// <summary>The heavy allowances this worker moves.</summary>
    public RunAllowances? Allowances { get; private set; }
}

/// <summary>Where a worker reads the machine. Passed explicitly wherever a worker type has a default.</summary>
public static class WorkerPaths
{
    public const string Proc = "/proc";

    public const string ProcSelfCgroup = "/proc/self/cgroup";

    public const string CgroupRoot = "/sys/fs/cgroup";
}
