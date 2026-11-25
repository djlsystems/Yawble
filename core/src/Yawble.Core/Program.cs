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

// The data folder is user-only on Linux and macOS (0700 folders, 0600 files): made so when missing, tightened below when not.
Yawble.Core.Companion.UserOnlyFile.CreatePrivateDirectory(options.DataDir!);
// One core per data folder (Lifetime/DataFolderLock): <data>/core.lock is held until this process ends, and a second core
// on the same folder stops here with exit code 5 and a "fatal:" line the shell shows in a dialog. The shell's per-launch
// IPC folder (POSIX: yawble-XXXXXX holding the socket) is ours to remove when we leave, refused or not.
var launchFolder = OperatingSystem.IsWindows() ? null : Yawble.Core.Lifetime.RuntimeFolders.LaunchFolderOf(options.IpcAddress!);
var lockAttempt = Yawble.Core.Lifetime.DataFolderLock.TryAcquire(options.DataDir!, Environment.ProcessId);
if (lockAttempt.Lock is not { } heldLock)
{
    Console.Error.WriteLine("fatal: " + Yawble.Core.Lifetime.DataFolderLock.InUseMessage(lockAttempt.HeldByPid));
    if (launchFolder is not null) Yawble.Core.Lifetime.RuntimeFolders.RemoveOwn(launchFolder);
    return Yawble.Core.Lifetime.DataFolderLock.InUseExitCode;
}
using var dataLock = heldLock;
// The shell opens its window on this line (before it, a refused core may still exit with the dialog's message).
Console.WriteLine("YAWBLE-STARTING");
// Everything the core reports on standard error is also kept in <data>/logs/core-<day>.log (the Diagnostics log).
var diagnostics = new Yawble.Core.Logs.DiagnosticLog(options.DataDir!, "core");
var rawStderr = Console.Error;
// Structured lines (Logs.Diag) go to the log once, with their level and fields, and echo to the stderr under the tee.
Yawble.Core.Logs.Diag.Attach(diagnostics, rawStderr);
Console.SetError(diagnostics.Tee(rawStderr));
Console.Error.WriteLine(CoreInfo.StartupLine());
Yawble.Core.Storage.DataFolderAccess.Secure(options.DataDir!, Yawble.Core.Bus.BusHost.AppRootFor(options.DataDir!), line => Console.Error.WriteLine(line));
// The IPC folders crashed launches left behind (no socket in them answers); never ours, never one that is still in use.
if (launchFolder is not null && Path.GetDirectoryName(launchFolder) is { } runtimeBase)
    new Yawble.Core.Lifetime.RuntimeFolders(Yawble.Core.Lifetime.RuntimeFolders.UnixSocketAnswers, log: line => Console.Error.WriteLine(line))
        .SweepStale(runtimeBase, launchFolder);
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
// A new database is made 0600 (POSIX) just before SQLite first opens it, which would make it 0644: SQLite gives the -wal
// and -shm files the database file's own mode.
Yawble.Core.Companion.UserOnlyFile.CreateEmptyIfMissing(dbPath);
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
Yawble.Core.Scripts.Running.ScriptRunner? scriptRunner = null;
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
    availableAgents: ct => Yawble.Core.Agents.AgentReadiness.ChoicesAsync(clis, ct),
    // Script members' runs go to the script engine, agents' to the launcher as before.
    wrapRunner: r => new Yawble.Core.Launch.ScriptDispatchRunner(r, () => scriptRunner));
busForRunner = bus;
// The global script and schema libraries and the script runner (members' data folders under <data>/scripts). Runs the
// old core left going resume from their last completed step. SC-2: a script's integration steps are the team's web API operations
// (kind "api"), through IntegrationService, which is made further down: its calls wait for it (integrationsReady).
var integrationsReady = new TaskCompletionSource<Yawble.Core.Integrations.IntegrationService>(TaskCreationOptions.RunContinuationsAsynchronously);
var scriptActions = new Yawble.Core.Scripts.Actions.ScriptActionKinds(new Dictionary<string, Yawble.Core.Scripts.Running.IScriptActionsProvider>
{
    [Yawble.Core.Scripts.Actions.ApiScriptActionsProvider.Kind] = new Yawble.Core.Scripts.Actions.ApiScriptActionsProvider(
        new Yawble.Core.Scripts.Actions.IntegrationScriptApi($"Data Source={dbPath}", integrationsReady.Task)),
    // SC-3: the built-in Email integration's read actions (kind "email")
    [Yawble.Core.Scripts.Actions.EmailActions.Kind] = new Yawble.Core.Scripts.Actions.EmailScriptActionsProvider(
        new Yawble.Core.Scripts.Actions.IntegrationScriptEmail(integrationsReady.Task)),
});
// A script's steps name the person's integrations (Email integrations, web APIs) by their real names: the directory reads them.
var scriptLibrary = new Yawble.Core.Scripts.Library.ScriptLibrary($"Data Source={dbPath}", () => scriptActions.Catalog,
    integrations: new Yawble.Core.Scripts.Library.IntegrationScriptDirectory($"Data Source={dbPath}", integrationsReady.Task));
var scriptRuns = new Yawble.Core.Scripts.Running.ScriptRunStore($"Data Source={dbPath}");
scriptRunner = new Yawble.Core.Scripts.Running.ScriptRunner(scriptRuns, scriptLibrary, scriptActions,
    Yawble.Core.Scripts.Running.SeamScriptEngine.Instance, Path.Combine(options.DataDir!, "scripts"),
    grantedFolders: memberId => policies.GetMember(memberId).Loosen.Filesystem?.Readwrite ?? [],
    log: line => Console.Error.WriteLine($"scripts: {line}"))
{
    Decisions = new Yawble.Core.Scripts.Running.BusScriptDecisions(bus),
};
bus.ScriptMembers = scriptRunner;
scriptRunner.ResumeInterrupted();

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
    new Yawble.Core.Monitor.StepFeed($"Data Source={dbPath}"),
    // The Monitor's code events: commits, merges and pushes the host recorded in the team repositories.
    new Yawble.Core.Monitor.CodeEvents($"Data Source={dbPath}"));
ui.Scripts = new Yawble.Core.Ui.ScriptsRpc(bus, scriptLibrary, scriptRuns, scriptRunner);
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
// (The saved default agent, Settings > Concierge, comes first when it still resolves: ConciergeService.DefaultAgentAsync.)
// Settings > Teams' Generate: one headless run of the Concierge's default agent with no Yawble tools.
bus.NameGenerator = concierge.GenerateNamesAsync;
// A workflow that waits on the person's answer and will expire tomorrow is posted once as a Concierge Update.
bus.ExpiryWarned = w => concierge.PostAppUpdate(w.TeamId, w.Team, w.Text, "expiring");
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
integrationsReady.TrySetResult(integrations); // script runs waiting on an integration step go on
// Network asks: a host a run's gateway refused (or an agent's request_host) waits for the person in Needs you; their answer
// changes the policy through the policy store, with each member's layout for its checks.
var networkAsks = new Yawble.Core.Integrations.NetworkAskService($"Data Source={dbPath}",
    new Yawble.Core.Integrations.StoreNetworkPolicy(policies,
        async (memberId, ct) =>
        {
            var everyone = await bus.AllMemberRowsAsync(ct);
            return layouts.For(everyone.FirstOrDefault(m => m.Id == memberId) ?? throw new ArgumentException($"no member {memberId}"), everyone);
        },
        async ct => layouts.Sample(await bus.AllMemberRowsAsync(ct))),
    (team, member, text, causation, ct) => bus.PersonTellAsync(team, member, text, causation, ct));
networkAsks.Changed = () => events.Publish(Yawble.Core.Integrations.NetworkAskService.ChangedEvent, null);
networkAsks.Resolve = (host, ct) => System.Net.Dns.GetHostAddressesAsync(host, ct);
bus.NetworkAsks = networkAsks;
launch.HostRefusals = networkAsks;
// Host tool asks: a tool from the app's catalog an agent asked for (request_tool) waits for the person in Needs you; Install
// runs the catalog's winget command in this process, as git.install does, and the launcher's detection checks it afterwards.
var toolAsks = new Yawble.Core.Integrations.ToolAskService($"Data Source={dbPath}",
    new Yawble.Core.Integrations.SystemToolHost(new Yawble.Core.Teams.SystemGitHost(), layouts.AppDataRoot),
    (team, member, text, causation, ct) => bus.PersonTellAsync(team, member, text, causation, ct));
toolAsks.Changed = () => events.Publish(Yawble.Core.Integrations.ToolAskService.ChangedEvent, null);
toolAsks.Progress = e => events.Publish(Yawble.Core.Integrations.ToolAskService.ProgressEvent, e);
bus.ToolAsks = toolAsks;
// Folder asks: a folder an agent asked to read or write (request_folder) waits for the person in Needs you; an
// approval writes the member's loosen block through the policy store, as policy.saveLoosen does.
var folderAsks = new Yawble.Core.Integrations.FolderAskService($"Data Source={dbPath}", policies,
    async (memberId, ct) =>
    {
        var everyone = await bus.AllMemberRowsAsync(ct);
        return layouts.For(everyone.FirstOrDefault(m => m.Id == memberId) ?? throw new ArgumentException($"no member {memberId}"), everyone);
    },
    async (teamId, ct) =>
    {
        // Every other team's repositories (the person's checkouts, the extras) and documents folders.
        var folders = new List<string>();
        if (bus.Repos is not { } repos) return folders;
        foreach (var t in await bus.TeamsAsync(ct))
        {
            if (t.Id == teamId) continue;
            if (repos.RepoOf(t.Id) is { } main) folders.Add(main.Path);
            folders.AddRange(repos.ExtrasOf(t.Id).Select(e => e.Path));
            folders.Add(Path.Combine(repos.TeamRoot(t.Name), Yawble.Core.Teams.TeamDocuments.Folder));
        }
        return folders;
    },
    (team, member, text, causation, ct) => bus.PersonTellAsync(team, member, text, causation, ct),
    canonicalize: Yawble.Core.Policy.HostPathCanonicalizer.Canonicalize);
folderAsks.Changed = () => events.Publish(Yawble.Core.Integrations.FolderAskService.ChangedEvent, null);
bus.FolderAsks = folderAsks;
link.IsIntegration = integrations.HasIntegration; // a remote viewer may detect or discover a CLI that is already added, not a new unknown one
link.IsRemoteMcp = integrations.IsRemoteMcp;
// Host tasks: the person's named programs, run on this computer on the base branch of their own checkout, under a hash-pinned approval.
// The Concierge and the companion ask for them as an agent does (host_task_run), through conciergeHostTasks.
Yawble.Core.Concierge.ConciergeHostTasks? conciergeHostTasks = null;
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
            // A run the Concierge asked for ends as an Update in its chat (a companion client's: nowhere, it checks the status).
            if (concierge.TryPostTaskUpdate(wake, hostTasks!.Store.TeamName(wake.TeamId) ?? $"team {wake.TeamId}")) return;
            var slash = wake.Member.IndexOf('/');
            await bus.PersonTellAsync(wake.Member[..slash], wake.Member[(slash + 1)..], wake.Text, wake.CausationSeq);
        },
        log: line => Console.Error.WriteLine($"tasks: {line}"), connections: connections);
    var interrupted = hostTasks.Recover();
    if (interrupted > 0) Console.Error.WriteLine($"tasks: {interrupted} run(s) were cut off when the app last closed; marked interrupted");
    integrations.HostTasks = new HostTaskRpc(hostTasks,
        teamId => new[] { "main" }.Concat(taskRepos.ExtrasOf(teamId).Select(e => e.Name)).ToList());
    conciergeHostTasks = new Yawble.Core.Concierge.ConciergeHostTasks(integrations.HostTasks);
}
var board = new Yawble.Core.Board.BoardService(new Yawble.Core.Board.BoardStore($"Data Source={dbPath}"), bus,
    line => Console.Error.WriteLine($"board: {line}"));
bus.Board = board;
// workflow_digest reads the runs' transcripts.
var digestTranscripts = new Yawble.Core.Transcripts.TranscriptStore(options.DataDir!, $"Data Source={dbPath}");
bus.TranscriptOf = digestTranscripts.Read;
board.Changed = () => events.Publish(Yawble.Core.Board.BoardService.ChangedEvent, null); // clear done / restore: open boards refresh
bus.LeaseChanged = () => events.Publish(Yawble.Core.Bus.BusEngine.LeaseChangedEvent, null); // the Monitor's heavy-work line, the member cards
// Custom agents and the agent/model change log (rows are written by the stores that change things; local only).
var agentDefaults = launch.AgentDefaults ?? new Yawble.Core.Agents.AgentDefaultsStore($"Data Source={dbPath}");
concierge.AgentResolver = agentResolver;
Yawble.Core.Agents.Profiles.AgentProfileStore? agentProfileStore = null;
// The person's own CLI settings (read only): the model a member with none and no agent default runs with, and one picker source.
var cliModelSettings = launch.ModelSettings ?? Yawble.Core.Agents.CliModelSettings.ForThisComputer();
var agentChangeLog = new Yawble.Core.Agents.Profiles.AgentChangeLog($"Data Source={dbPath}", null, (agent, model) => agentProfileStore!.ResolveModel(agent, model),
    agent => agentProfileStore!.CliOf(agent) is { } changedCli && cliModelSettings.DefaultModel(changedCli) is { } fromSettings
        ? (fromSettings, $"model from your {Yawble.Core.Policy.AgentClis.DisplayName(changedCli)} settings") : null);
agentProfileStore = new Yawble.Core.Agents.Profiles.AgentProfileStore($"Data Source={dbPath}", agentChangeLog, new Yawble.Core.Agents.Profiles.SqliteAgentUsage($"Data Source={dbPath}"), defaults: agentDefaults);
bus.AgentChanges = agentChangeLog;
// The member panes' agent pill: the model each member runs (set, the agent's default, or the one its last run used).
ui.Models = new Yawble.Core.Agents.MemberModels($"Data Source={dbPath}", agentResolver, agentDefaults, cliModelSettings);
var agentProfileRpc = new Yawble.Core.Agents.Profiles.AgentProfileRpc(agentProfileStore, agentChangeLog);
boardForConcierge = board.DispatchAsync;
if (bus.Repos is { } actionRepos)
    concierge.Actions = Yawble.Core.Concierge.ConciergeActions.ForApp(bus, actionRepos, conciergeRoute, conciergeRoute,
        new Yawble.Core.Provenance.AuditLog($"Data Source={dbPath}"));
if (concierge.Actions is { } conciergeActions) conciergeActions.HostTasks = conciergeHostTasks;
// The doctor and log retention. Backups come from the BackupService lister.
// Connections: read through ConnectionService.Index (no secret). Orphan secrets are offered only for keys this data folder's
// issued-keys ledger recorded (the OS store is shared by every data folder), and only from a store that is a credential index.
// Updates to the app itself, from GitHub Releases; installed from the window on Windows, and on Linux over an install.sh
// install the person may change (LinuxSelfUpdate).
var linuxSelfUpdate = OperatingSystem.IsLinux() && Yawble.Core.Updates.LinuxSelfUpdate.InstallFolder(AppContext.BaseDirectory) is not null;
var updateHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
updateHttp.DefaultRequestHeaders.UserAgent.ParseAdd("Yawble-Updater/1");
var updateService = new Yawble.Core.Updates.UpdateService(options.DataDir!, $"Data Source={dbPath}", Yawble.Core.CoreInfo.Version,
    new Yawble.Core.Updates.GitHubReleaseFeed(updateHttp),
    OperatingSystem.IsLinux() ? new Yawble.Core.Updates.LinuxTarballCheck() : new Yawble.Core.Updates.AuthenticodeCheck(),
    canInstall: OperatingSystem.IsWindows() || linuxSelfUpdate,
    arch: System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64",
    log: line => Console.Error.WriteLine(line), managed: Yawble.Core.Updates.UpdatePolicy.Managed, os: OperatingSystem.IsLinux() ? "linux" : "win");
// Every log, for the Logs dialog, its pruning and the daily retention sweep.
var logService = new Yawble.Core.Logs.LogService(options.DataDir!, $"Data Source={dbPath}", log: line => Console.Error.WriteLine(line));
// logs.tail: new entries pushed to who tails them (the Diagnostics log as written, the activity log while a tail is open).
var logTail = new Yawble.Core.Logs.LogTail(events, since => logService.TailSince(since));
diagnostics.Tail = logTail;
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
// yawble-companion (Companion/): a paired external client (Claude Code) calls the Concierge's own tools through this
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
var companionStarting = Yawble.Core.Companion.CompanionHost.Off(Environment.GetEnvironmentVariable, companionBinary, "yawble-companion is still starting");
var companionRpc = new Yawble.Core.Companion.CompanionRpc(companion, () => (companionHost ?? companionStarting).Status());
// "Needs You" counts the failing checks (the list is run once at start and then every 15 minutes, and on each Run again),
// and the pairings and companion requests waiting for the person.
ui.ExtraNeedsYou = Yawble.Core.Companion.CompanionHost.NeedsYou(() => doctorService.LastFailCount, companion);
_ = Task.Run(async () =>
{
    while (true)
    {
        try { await doctorService.RunAsync(CancellationToken.None); }
        catch (Exception e)
        {
            // which check was running, the exception and its message.
            Yawble.Core.Logs.Diag.Error("doctor", $"doctor: the periodic check failed: {e.GetType().Name}: {e.Message}",
                ("check", doctorService.RunningCheck), ("exception", e.GetType().Name));
        }
        await Task.Delay(TimeSpan.FromMinutes(15));
    }
});
// Command line, default model, run records, versions and updates for Settings > Agents and Member settings.
var agentInfoRpc = launch.Launcher is null ? null : new Yawble.Core.Agents.AgentInfoRpc(launch.Launcher, agentDefaults,
    // The pickers' lists: the CLI's own listing, the person's CLI settings, the models runs used, the built-in list.
    new Yawble.Core.Agents.ModelCatalog(
        new Yawble.Core.Agents.CliModelListing(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetEnvironmentVariable,
            new Yawble.Core.Agents.RealCliProcessRunner(), cli => HostCliPath(Yawble.Core.Policy.AgentClis.Id(cli)), launch.GrokModelsOutput),
        cliModelSettings, new Yawble.Core.Agents.SqliteModelsUsed($"Data Source={dbPath}")), clis,
    new Yawble.Core.Launch.RunRecordStore($"Data Source={dbPath}"), memberDirectory, new Yawble.Core.Agents.WindowsUpdateTerminal(), agentResolver, agentChangeLog);
// The read-only code browser of members' clones (watched while a member runs or a Code dialog is open on it).
await using var codeBrowser = new Yawble.CodeBrowser.CodeBrowserService(new Yawble.Core.Code.CodeHost(bus, runs, events, policies));
codeBrowser.Start();
// The cloud org dashboard: org.summary from what the app already tracks (never tokens, cost or usage), pushed as
// org.summaryChanged while a viewer watches; a Cloud Link viewer who leaves is unwatched.
await using var orgSummary = new Yawble.Core.Link.OrgSummaryService(
    new Yawble.Core.Link.AppOrgSummarySource(bus, new Yawble.Core.Board.BoardStore($"Data Source={dbPath}"), integrations, networkAsks, toolAsks, logService,
        () => link.InstanceName, new DateTimeOffset(System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime(), TimeSpan.Zero)),
    events, log: line => Console.Error.WriteLine(line));
link.ViewersChanged += orgSummary.ViewersPresent;
orgSummary.Start();
var dispatcher = new RpcDispatcher(new SandboxPlatformInfo(), () => applied, runs, guardrail, ui, hostChecks,
    scheduler: bus.SchedulerSnapshotAsync, stopQueued: bus.StopMemberAsync, agents: new Yawble.Core.Agents.GatewaySignInOverlay(clis, launch.GrokSignIn), agentTests: agentTests, link: link, concierge: concierge, board: board,
    audit: new Yawble.Core.Provenance.AuditLog($"Data Source={dbPath}"), integrations: integrations,
    doctor: doctor, backups: backupRpc, agentProfiles: agentProfileRpc, agentInfo: agentInfoRpc, companion: companionRpc,
    code: new Yawble.Core.Code.CodeBrowserRpc(codeBrowser), git: new Yawble.Core.Teams.GitRpc(gitService), logs: new Yawble.Core.Logs.LogRpc(logService, logTail),
    updates: new Yawble.Core.Updates.UpdateRpc(updateService), networkAsks: networkAsks, toolAsks: toolAsks, orgSummary: orgSummary,
    folderAsks: folderAsks);
dispatcherForLink = dispatcher;
// A decision option's action (Allow localhost) is applied as the setting's own call, audited as that call.
ui.Dispatch = (method, p, ct) => dispatcher.DispatchAsAsync(Yawble.Core.Provenance.Actor.Current ?? Yawble.Core.Provenance.Actor.Window, method, p, ct);
link.Start();

// Not disposed: ProcessExit can still fire after Main returns. The first way out writes "core stopping: <reason>" to the core log.
var stop = new Yawble.Core.Logs.CoreStop(line => Console.Error.WriteLine(line));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Request("Ctrl+C"); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.Request("the process was asked to end (SIGTERM or the system shutting down)");
// After a restore the database file is replaced: answer the call, then leave so the shell starts the core again.
backupService.RestartRequested = () => _ = Task.Run(async () => { await Task.Delay(750); stop.Request("restarting after a backup was restored"); });
_ = backupService.RunDailyAsync(stop.Token);
if (options.ExitOnStdinClose)
{
    _ = Task.Run(async () =>
    {
        // Parent death detection: Electron holds our stdin; EOF means the shell is gone.
        var buffer = new byte[256];
        using var stdin = Console.OpenStandardInput();
        while (await stdin.ReadAsync(buffer).ConfigureAwait(false) > 0) { }
        stop.Request("the window closed its connection (the app is quitting)");
    });
}

// Disposed after the endpoints below (declared before them): on a clean exit the emptied launch folder goes too.
using var launchFolderCleanup = new RunOnDispose(() => { if (launchFolder is not null) Yawble.Core.Lifetime.RuntimeFolders.RemoveOwn(launchFolder); });
await using var server = IpcServer.Start(IpcEndpoint.Create(options.IpcAddress!), options.Token!, dispatcher, events, hostEvents, websiteHost,
    websiteSignedIn: id => _ = integrations.WebsiteSignedIn(id)); // starts the check and returns: it never throws
// yawble-companion's control endpoint (its own pipe or socket, never the window's, never handed to the Cloud Link) and
// the user-only control.json naming it. Never fatal: a home that cannot be secured leaves the core running without it, and
// companion.status says why. On POSIX the socket sits beside the window's, in a folder only this user can open.
await using var companionHostScope = companionHost = options.Control
    ? Yawble.Core.Companion.CompanionHost.Start(Environment.GetEnvironmentVariable,
        OperatingSystem.IsWindows() ? options.DataDir! : Path.GetDirectoryName(Path.GetFullPath(options.IpcAddress!))!,
        companion, companionBinary, line => Console.Error.WriteLine(line))
    : Yawble.Core.Companion.CompanionHost.Off(Environment.GetEnvironmentVariable, companionBinary, "yawble-companion is off for this launch (--no-control)");
// The shell waits for this exact line before it connects.
Console.WriteLine($"YAWBLE-READY {server.Address}");
// The Concierge keeps tabs on what it promised to follow up (its watches), with no agent running.
_ = concierge.WatchAsync(stop.Token);
_ = board.RunAsync(stop.Token);
_ = logService.RunDailyAsync(stop.Token); // each log kept to its retention: on start, then daily
_ = updateService.RunAsync(stop.Token); // updates to the app: a minute after start, then every 6 hours (when on)
_ = integrations.RunWatchesAsync(stop.Token); // integrations' watches: their new items become events
// About once a minute: was our executable deleted, or the data folder removed or replaced? The shell then offers Quit.
_ = new Yawble.Core.Lifetime.RemovalWatch(new Yawble.Core.Lifetime.SystemInstallProbe(dataLock.StillOurs), events.Publish,
    line => Console.Error.WriteLine(line)).RunAsync(stop.Token);
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

/// <summary>Runs an action when disposed (the launch folder's removal, in the order of the using declarations).</summary>
file sealed class RunOnDispose(Action action) : IDisposable
{
    public void Dispose() => action();
}
