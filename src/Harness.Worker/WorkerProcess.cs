using System.Runtime.InteropServices;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host.Capacity;
using Microsoft.Extensions.Logging;

namespace Harness.Host;

/// <summary>
/// THE HOST RUN AS A WORKER (<c>--Role worker</c>): one connection out to control, and the run
/// worker behind it. It opens no store, takes no data-root lock, sets up no key ring and needs no
/// data root at all: every path a run uses - its working directory, its temp root, its home - and
/// its credential arrive with its start.
/// </summary>
/// <remarks>
/// Configured by environment or command line: <c>HARNESS_CONTROL_URL</c> (<c>--Worker:ControlUrl</c>),
/// <c>HARNESS_WORKER_KEY</c> (<c>--Worker:Key</c>) or the file the entrypoint moved it to,
/// <c>HARNESS_WORKER_KEY_FILE</c> (<c>--Worker:KeyFile</c>), <c>HARNESS_WORKER_ID</c> (<c>--Worker:Id</c>, the
/// machine name by default), <c>HARNESS_WORKER_STATE_DIR</c> (<c>--Worker:StateDir</c>: its run groups,
/// health and drain files), <c>HARNESS_WORKER_GIVE_UP_SECONDS</c> (<c>--Worker:GiveUpSeconds</c>, 0 for
/// never), <c>HARNESS_AGENT_USER</c> (<c>--Agents:RunAs</c>), and
/// <c>--Capacity:CgroupRoot</c> / <c>--Capacity:ProcRoot</c> for a fixture.
/// </remarks>
public static class WorkerProcess
{
    /// <summary>Exit codes: a configuration that cannot work.</summary>
    public const int Misconfigured = 2;

    /// <summary>The variables a worker is configured by, removed from its environment once read so no child inherits them.</summary>
    public static IReadOnlyList<string> OwnVariables { get; } =
    [
        "HARNESS_CONTROL_URL", "HARNESS_WORKER_KEY", WorkerKeyFile.FileVariable, "HARNESS_WORKER_ID", HostRoles.Variable,
        "HARNESS_WORKER_GIVE_UP_SECONDS", "Worker__ControlUrl", "Worker__Key", "Worker__KeyFile", "Worker__Id", "Worker__GiveUpSeconds",
    ];

    /// <summary>Runs the worker until it is stopped (SIGTERM, SIGINT) or refused. Returns the process's exit code.</summary>
    public static async Task<int> RunAsync(IReadOnlyList<string> args, string version)
    {
        using var log = new ConsoleLog("Harness.Worker");

        string? Setting(string name, string variable) =>
            HostRoles.Argument(args, name) ?? Environment.GetEnvironmentVariable(variable)
            ?? Environment.GetEnvironmentVariable(name.Replace(":", "__", StringComparison.Ordinal));

        var url = Setting("Worker:ControlUrl", "HARNESS_CONTROL_URL");
        var (key, keyRefusal) = WorkerKeyFile.Read(Setting("Worker:Key", "HARNESS_WORKER_KEY"), Setting("Worker:KeyFile", WorkerKeyFile.FileVariable));
        var giveUp = int.TryParse(Setting("Worker:GiveUpSeconds", "HARNESS_WORKER_GIVE_UP_SECONDS"), out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : (TimeSpan?)null;
        var id = new WorkerId(Setting("Worker:Id", "HARNESS_WORKER_ID") is { Length: > 0 } named ? named : Environment.MachineName);

        // THE WORKER'S OWN SETTINGS NEVER REACH A RUN. An agent child inherits this process's
        // environment, and the worker key in it would let a member connect as a worker.
        foreach (var own in OwnVariables) Environment.SetEnvironmentVariable(own, null);

        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var control)
            || control.Scheme is not ("http" or "https" or "ws" or "wss"))
        {
            log.LogError("No control URL is set: set HARNESS_CONTROL_URL to control's address (http://host:port) and start again.");
            return Misconfigured;
        }

        if (keyRefusal is not null)
        {
            log.LogError("{Refusal}", keyRefusal);
            return Misconfigured;
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            log.LogError("No worker key is set: set HARNESS_WORKER_KEY to control's worker key and start again.");
            return Misconfigured;
        }

        if (Plaintext(control) is { } refusal)
        {
            log.LogError("{Refusal}", refusal);
            return Misconfigured;
        }

        var cgroupRoot = Setting("Capacity:CgroupRoot", "HARNESS_CAPACITY_CGROUP_ROOT") ?? WorkerPaths.CgroupRoot;
        var proc = Setting("Capacity:ProcRoot", "HARNESS_CAPACITY_PROC_ROOT") ?? WorkerPaths.Proc;

        // The runs a previous worker of this name left running when it was killed are stopped first.
        var stateDir = Setting("Worker:StateDir", "HARNESS_WORKER_STATE_DIR") ?? Path.GetTempPath();
        var orphans = WorkerOrphans.FileFor(stateDir, id);
        var state = new WorkerStateFiles(stateDir, log);
        state.Alive(false);
        if (WorkerOrphans.StopLeftovers(orphans) is { Count: > 0 } stopped)
        {
            log.LogWarning("Stopped {Count} run process group(s) a previous worker {Worker} left running: {Groups}.", stopped.Count, id, string.Join(", ", stopped));
        }

        var runAs = AgentLaunchUser.Resolve(Setting("Agents:RunAs", "HARNESS_AGENT_USER"));
        log.LogInformation("Worker {Worker}, version {Version}, for control at {Control}. Agent launch: {Mode} - {Reason}",
            id, version, control, runAs.Mode, runAs.Reason);

        var heartbeat = new RunHeartbeat();
        var updates = new AgentUpdateGate();
        var cgroup = new CgroupReader(cgroupRoot);
        var settings = RunWorkerSettings.None;

        // What control's welcome says one run is held to; the worker's own container decides how.
        RunMemoryLimit Normal() =>
            settings.RunLimit is { } figure ? new RunMemoryLimit(figure.Mb, figure.Source, figure.Set) : new RunMemoryLimit(null, "control has not said yet");
        long? ContainerMb() => cgroup.Read().MemoryLimitBytes is > 0 and var bytes ? bytes / (1024 * 1024) : null;

        // The mechanism is this process's own cgroup's, as the Host's own worker decides it; a configured
        // root is where capacity is read, which a test points at a fixture.
        var memory = RunMemoryLimits.Resolve(
            Normal, WorkerPaths.CgroupRoot, WorkerPaths.ProcSelfCgroup, () => RunMemoryRules.Ceiling(Normal(), ContainerMb(), settings.ReserveMb));
        log.LogInformation("{RunMemoryLimits}", memory.LogLine);

        WorkerHost? host = null;
        var allowances = new RunAllowances(
            memory,
            (othersMb, others) => RunMemoryRules.Heavy(Normal(), ContainerMb(), settings.ReserveMb, othersMb, others),
            () => host?.HeavyHolders ?? [],
            new ProcessGroupReader(proc),
            RunProcessGroups.Shared,
            procRoot: proc,
            runAs: runAs);

        var homes = new RunHomes(runAs, RunHomeRemoval.For(runAs));
        var launcher = new RunLauncher(
            heartbeat, log, runAs, reports: true, updates: updates, memory: memory, allowances: allowances, homes: homes);

        var connection = new ControlConnection(
            id, version, key.Trim(), ControlConnection.Connector(Http(control), key.Trim()),
            capacity: () =>
            {
                var figures = cgroup.Read();
                return new WorkerHelloCapacity(figures.CpuUnlimited ? null : figures.CpuLimit, figures.MemoryLimitBytes);
            },
            log: log,
            giveUp: giveUp,
            control: control.ToString());

        host = new WorkerHost(
            id, connection, launcher, heartbeat, allowances, cgroup, new ProcessGroupReader(proc), RunProcessGroups.Shared,
            cli: new WorkerAgentCli(id, runAs, homes, log), log: log,
            streaming: new WorkerStreaming(connection, new Harness.Pty.PortaPtyEngine(), runAs));
        connection.Apply = host.ApplyAsync;
        connection.Input = host.Input;
        connection.OpenRuns = host.OpenRuns;
        connection.Ready = host.ReadyAsync;
        connection.Settings = welcome =>
        {
            settings = welcome;
            log.LogInformation("Control's figures for runs: {Limit}, {Reserve} MB kept for this worker.",
                welcome.RunLimit is { } figure ? $"{figure.Mb} MB a run ({figure.Source})" : "no run memory limit", welcome.ReserveMb);
        };
        connection.Alive = state.Alive;
        // A drain asked before the first connection is said in the first hello.
        if (state.Draining) _ = connection.SetDrainingAsync(true);

        // Every run's process group, kept where the next worker of this name finds it.
        using var recording = new Timer(_ =>
        {
            try
            {
                WorkerOrphans.Record(orphans, RunProcessGroups.Shared, proc);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                log.LogWarning("Could not record this worker's run process groups in {File}: {Message}", orphans, exception.Message);
            }

            // The operator's drain file, read as often: the worker says so to control as it changes.
            _ = connection.SetDrainingAsync(state.Draining);
        }, null, TimeSpan.Zero, TimeSpan.FromSeconds(2));

        using var stopping = new CancellationTokenSource();
        using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            stopping.Cancel();
        });
        using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, context =>
        {
            context.Cancel = true;
            stopping.Cancel();
        });

        var code = await connection.RunAsync(stopping.Token);

        // Stopping: every terminal this worker has ends, and every run is stopped and given a moment to say so.
        await host.StopStreamsAsync();
        foreach (var run in host.OpenRuns()) await host.ApplyAsync(new CancelRun(run));
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (host.OpenRuns().Count > 0 && DateTime.UtcNow < deadline) await Task.Delay(100);

        // Stopped in order: no run is left for a next worker to stop, and nothing reads it as connected.
        recording.Dispose();
        state.Alive(false);
        try
        {
            File.Delete(orphans);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        log.LogInformation("Worker {Worker} stopped.", id);
        return code;
    }

    /// <summary>
    /// The worker key and a run's sealed secrets cross the connection: in the clear only to a loopback
    /// or private address. Anywhere else the URL must be https or wss.
    /// </summary>
    public static string? Plaintext(Uri control)
    {
        if (control.Scheme is "https" or "wss") return null;
        if (control.IsLoopback) return null;

        if (System.Net.IPAddress.TryParse(control.Host, out var address) && Private(address)) return null;
        if (!control.Host.Contains('.', StringComparison.Ordinal)) return null;

        return $"Control's address {control} is neither loopback nor private, so it must be https:// (or wss://): the worker key would otherwise cross the network in the clear.";
    }

    private static bool Private(System.Net.IPAddress address)
    {
        if (address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal || System.Net.IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;

        var b = address.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
    }

    private static Uri Http(Uri control) =>
        control.Scheme switch
        {
            "http" => new UriBuilder(control) { Scheme = "ws" }.Uri,
            "https" => new UriBuilder(control) { Scheme = "wss" }.Uri,
            _ => control,
        };

    /// <summary>One JSON object per line on the console, as the Host logs: a worker has no logging framework of its own.</summary>
    private sealed class ConsoleLog(string category) : ILogger, IDisposable
    {
        private static readonly Lock Gate = new();

        // A sentence is read by a person on the worker's stderr: written as it is, quotes and all.
        private static readonly JsonSerializerOptions Readable = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var line = JsonSerializer.Serialize(new
            {
                Timestamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                LogLevel = logLevel.ToString(),
                Category = category,
                Message = formatter(state, exception),
                Exception = exception?.ToString(),
            }, Readable);

            lock (Gate)
            {
                if (logLevel >= LogLevel.Warning) Console.Error.WriteLine(line);
                else Console.Out.WriteLine(line);
            }
        }

        public void Dispose()
        {
        }
    }
}
