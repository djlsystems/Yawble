using Yawble.Core.Tasks;
using Yawble.Core;
using Yawble.Core.Guardrail;
using Yawble.Core.Ipc;
using Yawble.Core.Sandboxing;
using Yawble.Core.Storage;

// The guardrail check from the command line (also the test harness):
//   yawble-core guardrail-check --data-dir <app root>/data [--install-dir <install>] [--team T --member M] [--json]
if (args.Length > 0 && args[0] == "guardrail-check")
    return await GuardrailCli.RunAsync(args[1..], Environment.GetEnvironmentVariable, Console.Out, Console.Error,
        Yawble.Core.Agents.CliDetectorRegistry.ForThisHost());

// What the Agents dialog sees, as JSON (troubleshooting on a person's machine; the same detection as agents.detect):
//   yawble-core detect-agents
if (args.Length > 0 && args[0] == "detect-agents")
{
    var detected = await Yawble.Core.Agents.CliDetectorRegistry.ForThisHost().DetectAllAsync(CancellationToken.None);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(detected, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    return detected.Any(d => d.Found) ? 0 : 1;
}

// The core sidecar. Electron main starts it with:
//   yawble-core --ipc <socket path | pipe name> --data-dir <dir> [--install-dir <dir>]
// and the IPC token in YAWBLE_IPC_TOKEN. The core exits when its stdin closes, so it never outlives the shell.
var options = CoreOptions.Parse(args, Environment.GetEnvironmentVariable);
if (options.PrintVersion)
{
    Console.WriteLine(CoreInfo.Version);
    return 0;
}
if (options.Error is not null)
{
    Console.Error.WriteLine(options.Error);
    Console.Error.WriteLine(CoreOptions.Usage);
    return 2;
}

Console.Error.WriteLine(CoreInfo.StartupLine());
Directory.CreateDirectory(options.DataDir!);
var dbPath = Path.Combine(options.DataDir!, "yawble.db");
// Backups: one before the first pending migration (the update is refused if it fails), a daily one while running.
Yawble.Core.Connections.ConnectionService? connectionsForBackup = null; // set once the Connection service exists
var backupService = new BackupService(options.DataDir!,
    connections: () => connectionsForBackup is { } cs
        ? BackupConnections.FromService(cs)
        : BackupConnections.FromDatabase($"Data Source={dbPath};Pooling=False"),
    log: line => Console.Error.WriteLine(line));
// A restore staged by the last run is swapped in here, before the first connection to the database is opened.
// A restore that left no database in place (and could not put the old one back) stops the start here: the migrator
// below would otherwise make a new, empty database. The shell shows "fatal:" lines to the person.
try
{
    backupService.ApplyPendingRestore();
}
catch (RestoreFatalException e)
{
    Console.Error.WriteLine("fatal: " + e.Message);
    return 4;
}
IReadOnlyList<string> applied;
try
{
    applied = SchemaMigrator.Migrate($"Data Source={dbPath}", backupService.BeforeMigration);
}
catch (MigrationBackupException e)
{
    Console.Error.WriteLine("fatal: " + e.Message);
    return 3;
}
// The agent CLIs are the person's own, found where they are installed, at every launch (no pinned version).
Yawble.Core.Agents.ICliDetector clis = Yawble.Core.Agents.CliDetectorRegistry.ForThisHost();
// The startup probe: say clearly, on stderr and in core.getInfo, what this host lacks (Linux: the AppArmor profile).
var hostChecks = HostChecks.ForThisHost().Run();
foreach (var failed in hostChecks.Where(c => !c.Ok))
    Console.Error.WriteLine($"host check failed: {failed.Id}: {failed.Message} Fix: {failed.Fix}");
// The policy store, the push channel to the shell and the terminal channels.
var policies = new Yawble.Core.Policy.PolicyStore(
    $"Data Source={dbPath}", Yawble.Core.Policy.PolicyStore.ThroughSandbox, Yawble.Core.Policy.HostPathCanonicalizer.Canonicalize);
var events = new Yawble.Core.Ui.AppEvents();
// Pushes for the app's window on this computer only (website requests): never given to the Cloud Link.
var hostEvents = new Yawble.Core.Integrations.HostEvents();
var websiteHost = new Yawble.Core.Integrations.ShellWebsiteHost(hostEvents, Yawble.Core.Integrations.ShellWebsiteHost.WebsiteTimeout);
var terminals = new Yawble.Core.Ui.TerminalHub(events);
// The launcher: sandboxes left open by a previous core died with it; record them interrupted. The registry
// backs runs.list / runs.stop. The launcher runs on the gateway, credentials and transcripts (LaunchWiring).
new Yawble.Core.Launch.RunRecordStore($"Data Source={dbPath}").MarkOpenInterrupted();
var runs = new Yawble.Core.Launch.RunRegistry();
// git, found where it is installed even when this process's PATH predates the install (its folder goes first on PATH).
var gitService = new Yawble.Core.Teams.GitService(new Yawble.Core.Teams.SystemGitHost());
Yawble.Core.Teams.GitProgram.EnsureOnPath();
await using var launch = Yawble.Core.Launch.LaunchWiring.Create(
    options.DataDir!, $"Data Source={dbPath}", clis, options.InstallDir, runs, Environment.GetEnvironmentVariable,
    line => Console.Error.WriteLine($"launch: {line}"));
// Every sandbox the launcher registers streams into its member's terminal channel (live output and replay).
var memberDirectory = new Yawble.Core.Launch.SqliteMemberDirectory($"Data Source={dbPath}");
using var runTerminals = new Yawble.Core.Ui.RunTerminalBridge(runs, terminals, (team, member) => memberDirectory.Find(team, member)?.Id,
    Yawble.Core.Agents.AgentTestSessions.ChannelFor);
// The skills agents load over the bus (built-in, plus the person's own in the database).
var skillCatalog = new Yawble.Core.Skills.SkillCatalog($"Data Source={dbPath}");
var fakePty = Environment.GetEnvironmentVariable(Yawble.Core.Bus.BusHost.RunnerVariable) == "fake-pty";
Yawble.Core.Bus.BusEngine? busForRunner = null;
Yawble.Core.Bus.IAgentRunner? uiRunner = fakePty
    ? new Yawble.Core.Ui.FakePtyRunner(terminals,
        id => busForRunner?.MemberAsync(id).GetAwaiter().GetResult()?.Cli ?? "claude",
        int.TryParse(Environment.GetEnvironmentVariable("YAWBLE_FAKE_RUN_TICKS"), out var fakeTicks) ? fakeTicks : 8)
    : null;
// The bus: recovers interrupted runs and the run queue, then pumps. The runner is the sandbox
// launcher (YAWBLE_RUNNER=fake-pty only in the UI tests). The global run limit is app.maxConcurrentAgents of the
// global policy, read at every admission.
await using var bus = await Yawble.Core.Bus.BusHost.StartAsync(
    $"Data Source={dbPath}", Environment.GetEnvironmentVariable, line => Console.Error.WriteLine($"bus: {line}"),
    launch.Runner, uiRunner, Yawble.Core.Scheduling.PolicyRunLimit.From($"Data Source={dbPath}", line => Console.Error.WriteLine($"bus: {line}")),
    // Each run works in the member's own clone of the team repository (<app root>/teams/...).
    appDataRoot: Yawble.Core.Bus.BusHost.AppRootFor(options.DataDir!),
    skills: skillCatalog,
    availableAgents: ct => Yawble.Core.Agents.AgentReadiness.ChoicesAsync(clis, ct));
busForRunner = bus;

// A member's cli is a built-in's id or a custom agent's name: every site resolves it through this (base CLI, argv changes, default model).
var agentResolver = Yawble.Core.Agents.Profiles.AgentProfileStore.ForReading($"Data Source={dbPath}");
bus.AgentResolver = agentResolver;
var layouts = new Yawble.Core.Ui.MemberLayouts(options.DataDir!,
    Yawble.Core.Launch.LaunchWiring.ResolveInstallDir(options.InstallDir, Environment.GetEnvironmentVariable) ?? AppContext.BaseDirectory,
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), agents: agentResolver);
// The credential service (OS store, or the 0600 file in data/credentials/), the app-private host Grok
// home and its keeper: the launcher's own when it is on, so there is one keeper per Grok home. The host panes and the
// keeper run the person's own CLI by the absolute path detection found, never through PATH.
string? HostCliPath(string name) => Yawble.Core.Agents.HostCli.Path(clis, name);
var credentialService = launch.Credentials ?? Yawble.Core.Credentials.CredentialService.ForThisMachine(
    Path.Combine(options.DataDir!, "credentials"), $"Data Source={dbPath}", line => Console.Error.WriteLine(line));
var grokHome = launch.GrokHome ?? Yawble.Core.Credentials.GrokHome.InDataDir(layouts.AppDataRoot).EnsureCreated();
await using var ownKeeper = launch.Keeper is null
    ? new Yawble.Core.Credentials.GrokKeeper(grokHome, new Yawble.Core.Credentials.GrokKeeperOptions
    {
        GrokBinary = HostCliPath("grok") ?? "grok-not-installed",
        ResolveBinary = () => HostCliPath("grok"),
        Log = line => Console.Error.WriteLine(line),
    })
    : null;
var grokKeeper = launch.Keeper ?? ownKeeper!;
// The keeper of the app-private Grok home is not started; runs use the host's own Grok sign-in.
// Open interactive CLI: a session (a global run slot, then the CLI on a sandbox PTY). The echo fake only
// with the fake-pty runner, or when the launcher is off on this machine.
Yawble.Core.Ui.IInteractiveSessions interactive = !fakePty && launch.Launcher is { } launcher
    ? new Yawble.Core.Ui.LauncherInteractiveSessions(new Yawble.Core.Launch.InteractiveSessions(bus, launcher), terminals)
    : new Yawble.Core.Ui.FakeInteractiveSessions(terminals, bus);
var ui = new Yawble.Core.Ui.UiRpc(bus, policies, layouts,
    new Yawble.Core.Ui.ModelCredentials(credentialService, grokHome, grokKeeper),
    new Yawble.Core.Ui.HostPaneTerminals(terminals, HostCliPath, grokKeeper),
    interactive,
    terminals,
    GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024),
    runs.Stop,
    skillCatalog,
    // The Monitor's step feed: what the members do, read from their runs' transcripts.
    new Yawble.Core.Monitor.StepFeed($"Data Source={dbPath}"));
// The guardrail check (runs a battery of escape attempts in a sandbox under a member's effective policy).
// It rides on the launcher: the same layout, policy, per-run gateway and backend as the members' runs.
// A member's check takes a global run slot through the bus, like a terminal session.
var guardrail = GuardrailService.Create(launch.Launcher, launch.Problem, $"Data Source={dbPath}", line => Console.Error.WriteLine($"guardrail: {line}"),
    GuardrailRunSlots.From(bus));
if (guardrail.Problem is { } guardrailProblem) Console.Error.WriteLine($"guardrail: the check is off: {guardrailProblem}");
// scheduler.getState (running / limit, the queue with "Queued · position N of M", per-team counts), and
// runs.stop withdrawing a member's queued terminal session when it has no live sandbox.
// The Agents dialog: the CLIs the person installed themselves, detected live on every call, and their Test (the
// CLI's TUI in a sandbox under the global policy). The same detector feeds every launch.
var agentTests = new Yawble.Core.Agents.AgentTestSessions(launch.Launcher, launch.Problem, terminals, clis,
    line => Console.Error.WriteLine(line), agentResolver);
// The Concierge: the person's chats with an agent that knows every team; a turn is one headless run of the chosen CLI.
// Default agent: the first CLI that is installed and signed in here (Claude, Codex, Grok, Copilot, Antigravity).
// The Board is made below; the Concierge's backlog tools reach it through this.
Func<string, System.Text.Json.Nodes.JsonObject?, CancellationToken, Task<System.Text.Json.Nodes.JsonNode?>>? boardForConcierge = null;
Yawble.Core.Ops.DoctorRpc? doctorForConcierge = null;
BackupRpc? backupsForConcierge = null;
var conciergeRoute = Yawble.Core.Concierge.ConciergeService.Route(
    (method, p, ct) => method is "ops.doctor" or "ops.doctorFix" or "ops.backupNow"
        ? (method == "ops.backupNow"
            ? (backupsForConcierge ?? throw new Yawble.Core.Ipc.RpcException("not_ready", "backups are still starting")).DispatchAsync(method, p, ct)
            : (doctorForConcierge ?? throw new Yawble.Core.Ipc.RpcException("not_ready", "the health check is still starting")).DispatchAsync(method, p, ct))
        : ui.DispatchAsync(method, p, ct),
    () => boardForConcierge);
var concierge = new Yawble.Core.Concierge.ConciergeService(
    new Yawble.Core.Concierge.ConciergeStore($"Data Source={dbPath}"), bus,
    (request, ct) => launch.Launcher is { } l
        ? l.RunAsync(request, ct)
        : Task.FromResult(new Yawble.Core.Launch.LaunchOutcome("", Yawble.Core.Launch.RunEnd.StartFailed,
            launch.Problem ?? "agents cannot run on this computer", null, [])),
    runs, events,
    // The Concierge's tools reach the window's operations; of ops.* only the doctor's list and fixes and a backup now (never a
    // restore). board.* and backlog.* go to the Board (ConciergeService.Route), everything else to the window's UiRpc.
    conciergeRoute,
    async ct =>
    {
        var found = await clis.DetectAllAsync(ct);
        foreach (var id in new[] { "claude", "codex", "grok", "copilot", "antigravity" })
            if (found.FirstOrDefault(c => c.CliId == id) is { Found: true, SignIn.Status: Yawble.Core.Agents.SignInStatus.SignedIn }) return id;
        return "claude";
    });
// The Cloud Link (optional): a remote viewer's calls go through this same dispatcher, after RemoteAccess.
RpcDispatcher? dispatcherForLink = null;
await using var link = new Yawble.Core.Link.CloudLink(options.DataDir!,
    (method, p, ct) => dispatcherForLink!.DispatchAsAsync(Yawble.Core.Provenance.Actor.Current ?? Yawble.Core.Provenance.Actor.Window, method, p, ct), events, line => Console.Error.WriteLine(line));
// The Board: every team's cards in four lanes (kept in step with the bus) and the person's backlog.
var issuedKeys = new Yawble.Core.Credentials.FileIssuedKeyLedger(options.DataDir!);
var connections = new Yawble.Core.Connections.ConnectionService(credentialService.Store, $"Data Source={dbPath}", ledger: issuedKeys);
connectionsForBackup = connections;
// a saved, removed or re-marked Connection: open Settings screens and Needs You refresh (no names or values in the event)
connections.Changed = () => events.Publish(Yawble.Core.Connections.ConnectionService.ChangedEvent, null);
await using var integrations = new Yawble.Core.Integrations.IntegrationService($"Data Source={dbPath}", bus,
    connections: connections, browser: new Yawble.Core.Integrations.ShellBrowser(), redirect: () => new Yawble.Core.Integrations.LoopbackRedirectReceiver(),
    websites: websiteHost);
bus.Integrations = integrations;
link.IsIntegration = integrations.HasIntegration; // a remote viewer may detect or discover a CLI that is already added, not a new unknown one
link.IsRemoteMcp = integrations.IsRemoteMcp;
// Host tasks: the person's named programs, run on this computer on the base branch of their own checkout, under a hash-pinned approval.
if (bus.Repos is { } taskRepos)
{
    HostTaskService? hostTasks = null;
    hostTasks = new HostTaskService($"Data Source={dbPath}", taskRepos.Git,
        (teamId, name) => name == "main"
            ? taskRepos.RepoOf(teamId) is { } main ? new TaskRepoInfo(main.Path, main.BaseBranch) : null
            : taskRepos.ExtrasOf(teamId).FirstOrDefault(e => e.Name == name) is { } extra ? new TaskRepoInfo(extra.Path, extra.BaseBranch) : null,
        teamId => new Yawble.Core.Teams.TeamDocuments(taskRepos).Root(hostTasks!.Store.TeamName(teamId) ?? throw new InvalidOperationException("no such team")),
        async wake =>
        {
            var slash = wake.Member.IndexOf('/');
            await bus.PersonTellAsync(wake.Member[..slash], wake.Member[(slash + 1)..], wake.Text, wake.CausationSeq);
        },
        log: line => Console.Error.WriteLine($"tasks: {line}"));
    var interrupted = hostTasks.Recover();
    if (interrupted > 0) Console.Error.WriteLine($"tasks: {interrupted} run(s) were cut off when the app last closed; marked interrupted");
    integrations.HostTasks = new HostTaskRpc(hostTasks,
        teamId => new[] { "main" }.Concat(taskRepos.ExtrasOf(teamId).Select(e => e.Name)).ToList());
}
var board = new Yawble.Core.Board.BoardService(new Yawble.Core.Board.BoardStore($"Data Source={dbPath}"), bus,
    line => Console.Error.WriteLine($"board: {line}"));
bus.Board = board;
board.Changed = () => events.Publish(Yawble.Core.Board.BoardService.ChangedEvent, null); // clear done / restore: open boards refresh
bus.LeaseChanged = () => events.Publish(Yawble.Core.Bus.BusEngine.LeaseChangedEvent, null); // the Monitor's heavy-work line, the member cards
// Custom agents and the agent/model change log (rows are written by the stores that change things; local only).
var agentDefaults = launch.AgentDefaults ?? new Yawble.Core.Agents.AgentDefaultsStore($"Data Source={dbPath}");
concierge.AgentResolver = agentResolver;
Yawble.Core.Agents.Profiles.AgentProfileStore? agentProfileStore = null;
var agentChangeLog = new Yawble.Core.Agents.Profiles.AgentChangeLog($"Data Source={dbPath}", null, (agent, model) => agentProfileStore!.ResolveModel(agent, model));
agentProfileStore = new Yawble.Core.Agents.Profiles.AgentProfileStore($"Data Source={dbPath}", agentChangeLog, new Yawble.Core.Agents.Profiles.SqliteAgentUsage($"Data Source={dbPath}"), defaults: agentDefaults);
bus.AgentChanges = agentChangeLog;
var agentProfileRpc = new Yawble.Core.Agents.Profiles.AgentProfileRpc(agentProfileStore, agentChangeLog);
boardForConcierge = board.DispatchAsync;
if (bus.Repos is { } actionRepos)
    concierge.Actions = Yawble.Core.Concierge.ConciergeActions.ForApp(bus, actionRepos, conciergeRoute, conciergeRoute,
        new Yawble.Core.Provenance.AuditLog($"Data Source={dbPath}"));
// The doctor and log retention. Backups come from the BackupService lister.
// Connections: read through ConnectionService.Index (no secret). Orphan secrets are offered only for keys this data folder's
// issued-keys ledger recorded (the OS store is shared by every data folder), and only from a store that is a credential index.
var logFolders = new[] { Path.Combine(options.DataDir!, "logs"), Path.Combine(options.DataDir!, "transcripts") };
var doctorService = new Yawble.Core.Ops.DoctorService(
    new Yawble.Core.Ops.DoctorHostSystem(options.DataDir!, () => applied, launch.Problem, guardrail, link.Status, logFolders, launch.GatewayProbe),
    backups: new BackupListerAdapter(backupService), clis, new Yawble.Core.Ops.ConnectionSourceAdapter(connections),
    credentialService.Store as Yawble.Core.Ops.ICredentialIndex,
    new Yawble.Core.Ops.DoctorHostTeams(bus, $"Data Source={dbPath}"),
    ledger: issuedKeys, git: gitService);
var doctor = new Yawble.Core.Ops.DoctorRpc(doctorService);
var backupRpc = new BackupRpc(backupService);
doctorForConcierge = doctor;
backupsForConcierge = backupRpc;
// The yawble companion (Companion/): a paired external client (Claude Code) calls the Concierge's own tools through this
// route, which is the Concierge's plus the backups' list and a restore the person approved in the window (BackupRpc refuses
// any other). Its actions are the Concierge's own (so its tools audit themselves), with the companion's own confirmation book.
var companionRoute = Yawble.Core.Companion.CompanionHost.Route(conciergeRoute, backupRpc.DispatchAsync);
var companion = new Yawble.Core.Companion.CompanionService(bus, new Yawble.Core.Concierge.ConciergeStore($"Data Source={dbPath}"), companionRoute,
    (turn, book) => concierge.Actions?.ForTurn(turn, book),
    new Yawble.Core.Companion.CompanionClientStore($"Data Source={dbPath}"), new Yawble.Core.Provenance.AuditLog($"Data Source={dbPath}"), events);
// Disposed on the way out (after the endpoints): an approved run still under way is cancelled and ends Failed, audited.
await using var companionScope = companion;
var companionBinary = Yawble.Core.Companion.CompanionHost.BinaryPathFor(
    Yawble.Core.Launch.LaunchWiring.ResolveInstallDir(options.InstallDir, Environment.GetEnvironmentVariable));
Yawble.Core.Companion.CompanionHost? companionHost = null; // opened after the window's endpoint, below
// The home (YAWBLE_HOME or the default) is resolved inside CompanionHost: a malformed one is said in companion.status, never thrown.
var companionStarting = Yawble.Core.Companion.CompanionHost.Off(Environment.GetEnvironmentVariable, companionBinary, "the yawble companion is still starting");
var companionRpc = new Yawble.Core.Companion.CompanionRpc(companion, () => (companionHost ?? companionStarting).Status());
// "Needs You" counts the failing checks (the list is run once at start and then every 15 minutes, and on each Run again),
// and the pairings and companion requests waiting for the person.
ui.ExtraNeedsYou = Yawble.Core.Companion.CompanionHost.NeedsYou(() => doctorService.LastFailCount, companion);
_ = Task.Run(async () =>
{
    while (true)
    {
        try { await doctorService.RunAsync(CancellationToken.None); }
        catch (Exception e) { Console.Error.WriteLine($"doctor: the periodic check failed: {e.GetType().Name}"); }
        await Task.Delay(TimeSpan.FromMinutes(15));
    }
});
// Command line, default model, run records, versions and updates for Settings > Agents and Member settings.
var agentInfoRpc = launch.Launcher is null ? null : new Yawble.Core.Agents.AgentInfoRpc(launch.Launcher, agentDefaults,
    new Yawble.Core.Agents.ModelCatalog(), clis,
    new Yawble.Core.Launch.RunRecordStore($"Data Source={dbPath}"), memberDirectory, new Yawble.Core.Agents.WindowsUpdateTerminal(), agentResolver, agentChangeLog);
// The read-only code browser of members' clones (watched while a member runs or a Code dialog is open on it).
await using var codeBrowser = new Yawble.CodeBrowser.CodeBrowserService(new Yawble.Core.Code.CodeHost(bus, runs, events, policies));
codeBrowser.Start();
var dispatcher = new RpcDispatcher(new SandboxPlatformInfo(), () => applied, runs, guardrail, ui, hostChecks,
    scheduler: bus.SchedulerSnapshotAsync, stopQueued: bus.StopMemberAsync, agents: clis, agentTests: agentTests, link: link, concierge: concierge, board: board,
    audit: new Yawble.Core.Provenance.AuditLog($"Data Source={dbPath}"), integrations: integrations,
    doctor: doctor, backups: backupRpc, agentProfiles: agentProfileRpc, agentInfo: agentInfoRpc, companion: companionRpc,
    code: new Yawble.Core.Code.CodeBrowserRpc(codeBrowser), git: new Yawble.Core.Teams.GitRpc(gitService));
dispatcherForLink = dispatcher;
link.Start();

// Not disposed: ProcessExit can still fire after Main returns.
var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.Cancel();
// After a restore the database file is replaced: answer the call, then leave so the shell starts the core again.
backupService.RestartRequested = () => _ = Task.Run(async () => { await Task.Delay(750); stop.Cancel(); });
_ = backupService.RunDailyAsync(stop.Token);
if (options.ExitOnStdinClose)
{
    _ = Task.Run(async () =>
    {
        // Parent death detection: Electron holds our stdin; EOF means the shell is gone.
        var buffer = new byte[256];
        using var stdin = Console.OpenStandardInput();
        while (await stdin.ReadAsync(buffer).ConfigureAwait(false) > 0) { }
        stop.Cancel();
    });
}

await using var server = IpcServer.Start(IpcEndpoint.Create(options.IpcAddress!), options.Token!, dispatcher, events, hostEvents, websiteHost,
    websiteSignedIn: id => _ = integrations.WebsiteSignedIn(id)); // starts the check and returns: it never throws
// The yawble companion's control endpoint (its own pipe or socket, never the window's, never handed to the Cloud Link) and
// the user-only control.json naming it. Never fatal: a home that cannot be secured leaves the core running without it, and
// companion.status says why. On POSIX the socket sits beside the window's, in a folder only this user can open.
await using var companionHostScope = companionHost = options.Control
    ? Yawble.Core.Companion.CompanionHost.Start(Environment.GetEnvironmentVariable,
        OperatingSystem.IsWindows() ? options.DataDir! : Path.GetDirectoryName(Path.GetFullPath(options.IpcAddress!))!,
        companion, companionBinary, line => Console.Error.WriteLine(line))
    : Yawble.Core.Companion.CompanionHost.Off(Environment.GetEnvironmentVariable, companionBinary, "the yawble companion is off for this launch (--no-control)");
// The shell waits for this exact line before it connects.
Console.WriteLine($"YAWBLE-READY {server.Address}");
// The Concierge keeps tabs on what it promised to follow up (its watches), with no agent running.
_ = concierge.WatchAsync(stop.Token);
_ = board.RunAsync(stop.Token);
_ = new Yawble.Core.Ops.LogRetention(logFolders, log: line => Console.Error.WriteLine(line)).RunAsync(stop.Token); // logs older than 14 days: on start, then daily
_ = integrations.RunWatchesAsync(stop.Token); // integrations' watches: their new items become events
try
{
    await Task.Delay(Timeout.Infinite, stop.Token);
}
catch (OperationCanceledException)
{
}
return 0;

/// <summary>Adapts the backup service's list to the doctor's lister seam.</summary>
file sealed class BackupListerAdapter(BackupService s) : Yawble.Core.Ops.IBackupLister
{
    public IReadOnlyList<Yawble.Core.Ops.BackupInfo> List() => s.List().Select(b => new Yawble.Core.Ops.BackupInfo(b.Name, b.Size, b.Reason, b.Time)).ToList();
}
