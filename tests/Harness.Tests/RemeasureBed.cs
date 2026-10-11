using System.Collections.Concurrent;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Capacity;

namespace Harness.Tests;

/// <summary>
/// CONTROL'S MEASUREMENTS OVER SCRIPTED WORKERS, composed as the control role composes them: a real
/// <see cref="WorkerPool"/>, <see cref="WorkerAsks"/>, update gate, install record, sign-in probe, launch
/// check (through the member runner's own <see cref="ProcessAgentRunner.CheckLaunchAsync"/>), tool
/// pre-flight and the re-measure pass on a manual clock. Every worker is a <see cref="BedWorker"/> that
/// answers with canned events: no CLI is run anywhere, and nothing runs an update - the gate is held by
/// the test with a task it completes. <c>claude</c> is the command because only a CLI with a listing is
/// listed; in this process it is the test assembly's stub (<see cref="AgentCliIsolation"/>), and nothing
/// here looks it up on a PATH.
/// </summary>
internal sealed class RemeasureBed : IDisposable
{
    public const string Cli = "claude";
    public const string Other = "codex";
    public const string Preset = "claude-headless";
    public const string OtherPreset = "codex-headless";

    /// <summary>The preset's update-off, which every version run carries, and its update, which none may.</summary>
    public static readonly AgentUpdates Updates = new(
        new Dictionary<string, string> { ["DISABLE_AUTOUPDATER"] = "1" }, ["--no-self-update"], ["npm", "install", "-g", "the-cli@latest"]);

    public RemeasureBed()
    {
        Pool = new WorkerPool(_ => new HeadroomGate(() => 80, () => 0));
        Asks = new WorkerAsks(() => Pool.Worker(null));
        Installs = WorkerInstalls.Over(Pool);
        Catalog = new AgentCatalog(
        [
            new AgentDefinition(Preset, AgentMode.Headless, new AgentLaunch(Cli, ["-p"]),
                Isolation: new AgentIsolation(["--strict-mcp-config"]), LaunchCheck: ["--version"], Updates: Updates),
            new AgentDefinition(OtherPreset, AgentMode.Headless, new AgentLaunch(Other, ["exec"]), LaunchCheck: ["--version"]),
        ]);
        Probe = new AgentInstallProbe(workers: Installs, gate: Gate);
        Auth = new AgentAuthProbe(Catalog, asks: Asks, dataRoot: Root, installs: Probe, measured: Installs, gate: Gate);
        Runner = new ProcessAgentRunner(Catalog, new RunWorkers(Pool, _ => null), Directory, null, () => null, null, gateHere: Gate);
        Launch = new AgentLaunchChecks(Catalog, Runner, Root, gate: Gate);
        Tools = new AgentToolPreflight(Catalog, new WorkerListingRunner(Asks, Root, pathIsTheWorkers: false), Root, gate: Gate);
        Pass = new RemeasureWhenWorkersChange(
            [
                ("the sign-in probe", ct => Auth.ReportsAsync(ct)),
                ("the launch check", ct => Launch.ReportsAsync(ct, fresh: true)),
                ("the tool pre-flight", ct => Tools.PassAsync(ct)),
                ("the CLI versions", ct => CliVersionsWhenWorkersChange.MeasureAsync(Catalog, Asks, Root, Gate, ct: ct)),
            ],
            () =>
            {
                Auth.Forget();
                Launch.Forget();
            },
            clock: Clock);
    }

    public string Root { get; } = System.IO.Directory.CreateTempSubdirectory("harness-remeasure-").FullName;

    public ManualTime Clock { get; } = new(DateTimeOffset.UnixEpoch.AddDays(1));

    public WorkerPool Pool { get; }

    public WorkerAsks Asks { get; }

    public RunDirectory Directory { get; } = new();

    public AgentUpdateGate Gate { get; } = new();

    public WorkerInstalls Installs { get; }

    public AgentCatalog Catalog { get; }

    public AgentInstallProbe Probe { get; }

    public AgentAuthProbe Auth { get; }

    public ProcessAgentRunner Runner { get; }

    public AgentLaunchChecks Launch { get; }

    public AgentToolPreflight Tools { get; }

    public RemeasureWhenWorkersChange Pass { get; }

    public AgentAuthRecord? AuthRecord => AgentAuthRecord.Read(Root);

    public AgentLaunchChecksRecord? LaunchRecord => AgentLaunchChecksRecord.Read(Root);

    public AgentToolsRecord? ToolsRecord => AgentToolsRecord.Read(Root);

    public CliVersionHistory Versions => CliVersionHistory.In(Root);

    public void Dispose()
    {
        try { System.IO.Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }

    /// <summary>A worker joins the pool, answering as <paramref name="setup"/> says.</summary>
    public BedWorker Join(string id, Action<BedWorker>? setup = null)
    {
        var worker = new BedWorker(id, this);
        setup?.Invoke(worker);
        Pool.Join(worker.Scripted, new WorkerInfo(worker.Scripted.Id, "v", 4, (long)8e9, DateTimeOffset.UtcNow));
        return worker;
    }

    /// <summary>Advances the manual clock past the quiet window and waits, bounded, for pass <paramref name="n"/>.</summary>
    public async Task SettleAsync(int n)
    {
        Clock.Advance(RemeasureWhenWorkersChange.Quiet);
        await Pass.WaitForPassAsync(n).WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
    }

    /// <summary>Holds <paramref name="command"/>'s update in <paramref name="phase"/>; complete the returned source to end it.</summary>
    public async Task<Held> HoldAsync(string command, string phase, string? worker = null)
    {
        var ct = TestContext.Current.CancellationToken;
        IDisposable? share = phase == AgentUpdatePhases.Waiting ? await Gate.EnterRunAsync(command, null, ct) : null;
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var update = Gate.UpdateAsync(command, _ => release.Task, ct);
        Assert.Equal(phase, Gate.Holding(command)?.Phase);
        if (worker is not null) Gate.RunsOn(command, worker);
        return new Held(release, update, share);
    }

    public sealed record Held(TaskCompletionSource<bool> Release, Task<bool> Update, IDisposable? Share)
    {
        /// <summary>Ends the hold as the update would, done: its run out first when it waited for one.</summary>
        public async Task EndAsync()
        {
            Share?.Dispose();
            Release.TrySetResult(true);
            await Update.WaitAsync(TimeSpan.FromSeconds(60));
        }
    }
}

/// <summary>
/// One scripted worker in a <see cref="RemeasureBed"/>: a sign-in probe answered per command as
/// <see cref="Installed"/> says, a listing answered empty, a version run answered <see cref="Version"/>,
/// and a launch check answered ok - each naming the worker, and each counted by kind.
/// </summary>
internal sealed class BedWorker
{
    private readonly RemeasureBed _bed;

    public BedWorker(string id, RemeasureBed bed)
    {
        _bed = bed;
        Scripted = new ScriptedCliWorker(id, Answer) { Asks = bed.Asks };
        Scripted.Received += message =>
        {
            if (message is CheckLaunch check && Answers)
            {
                _ = Scripted.SayAsync(bed.Directory.HandleAsync, new LaunchChecked(check.Request, AgentLaunchReport.Ok, 0, null, $"started on {id}"));
            }
        };
    }

    public ScriptedCliWorker Scripted { get; }

    public string Id => Scripted.Id.Value;

    /// <summary>Whether each command is on this worker's PATH, by command; absent is installed.</summary>
    public ConcurrentDictionary<string, bool> Installed { get; } = new(StringComparer.Ordinal);

    public string Version { get; set; } = "2.0.1 (Claude Code)";

    /// <summary>False: nothing is answered, as a worker that does not answer.</summary>
    public bool Answers { get; set; } = true;

    /// <summary>Runs before each sign-in probe is answered: where a test starts a hold mid-ask.</summary>
    public Action<ProbeSignIn>? BeforeProbeAnswer { get; set; }

    public IReadOnlyList<ProbeSignIn> Probes => [.. Scripted.Sent.OfType<ProbeSignIn>()];

    public IReadOnlyList<CheckLaunch> Checks => [.. Scripted.Sent.OfType<CheckLaunch>()];

    /// <summary>The tool listings sent: every command run that is not a version.</summary>
    public IReadOnlyList<RunAgentCommands> Listings =>
        [.. Scripted.Sent.OfType<RunAgentCommands>().Where(r => !r.Commands.Any(c => c.Arguments.Contains("--version")))];

    public IReadOnlyList<RunAgentCommands> VersionRuns =>
        [.. Scripted.Sent.OfType<RunAgentCommands>().Where(r => r.Commands.All(c => c.Arguments.Contains("--version")))];

    private WorkerEvent? Answer(ControlMessage message)
    {
        if (!Answers) return null;

        switch (message)
        {
            case ProbeSignIn probe:
                BeforeProbeAnswer?.Invoke(probe);
                return new SignInProbed(probe.Request, [.. probe.Commands.Select(c =>
                {
                    var installed = Installed.GetValueOrDefault(c.Command, true);
                    return new SignInProbeResult(c.Command, installed, installed ? true : null, $"{c.Command} on {Id}");
                })]);

            case RunAgentCommands run when run.Commands.All(c => c.Arguments.Contains("--version")):
                return new AgentCommandsRan(run.Request, [.. run.Commands.Select(_ =>
                    new AgentCliRunResult(true, 0, false, Version + "\n", "", false, null))]);

            case RunAgentCommands run:
                return new AgentCommandsRan(run.Request, [.. run.Commands.Select(c =>
                    new AgentCliRunResult(true, 0, false, c.Arguments.Contains("--json") ? "[]" : "", "", false, null))]);

            default:
                return null;
        }
    }
}
