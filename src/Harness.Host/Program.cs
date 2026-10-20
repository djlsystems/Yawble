using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.SignalR;
using ModelContextProtocol.AspNetCore;
using Harness.Containers;
using Harness.Backlog;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Harness.Host.Capacity;
using Harness.Host.Solutions;
using Harness.Identity;
using Harness.Kanban;
using Harness.Messaging;
using Harness.Skills;

// THE FIRST STATEMENT IN THE HOST, and it has to be: everything printed before it reaches a
// terminal and nowhere else. The host's own stdout is captured to a FILE, because a
// diagnostics TABLE cannot record the failure that stops the process writing to a table. Without
// it the only host output on the machine is a window: close it and the evidence is gone, leave it
// open and the scrollback is bounded.
//
// It COPIES the console rather than replacing it - every write reaches the terminal first and the
// file second - and it cannot open the file yet, because the data root is not known for another
// hundred lines. So it holds these lines in memory and `OpenIn` replays them. See HostLog.
//
// BEFORE CreateBuilder as well, so the console logging provider - constructed with whatever
// `Console.Out` is at the time - is built on the copy and an unhandled exception logged to the
// console lands in the file too.
var hostLog = HostLog.Tee();

var builder = WebApplication.CreateBuilder(args);

// ONE JSON OBJECT PER LINE on the console, with scopes, so a request's principal id (see
// PrincipalLogScope) rides on every line that request logs. It is still the console, so the tee
// above still copies it into the size-bounded host log: nothing about that file changes but its
// line shape.
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.UseUtcTimestamp = true;
    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
});

// One data root, one database, one process. Everything downstream - exactly-once delivery into a
// container's queue, a plain polling pump instead of a broker - rests on that being true.
//
// TWO WAYS IN, and the explicit one wins. `DataRoot` names a folder outright, which is what the
// operator commands, the ops scripts and every test fixture pass. `Instance` names an instance and
// the LAYOUT decides where that lives - one Harness folder in the platform's app-data location,
// one subfolder per instance - so a second instance is independent by construction rather than by
// somebody remembering to set a path. Nothing set at all is `core`.
// REMOVED FROM THE HOST'S OWN PROCESS, BEFORE ANYTHING CAN SPAWN A CHILD.
//
// Both spawn paths MERGE into an inherited environment, so a variable the Host holds reaches every
// agent child whether or not this codebase names it. `AgentEnvironment.Cleared` cannot help: it is
// a dictionary of strings and no value means absent, and for these names EMPTY IS THE DEFECT - node
// reads a SET `FORCE_COLOR` as "force colour on" whatever its value, which puts ANSI inside the
// payload the ledger is built from, where escape codes are tokens re-sent on every invocation.
//
// THIS IS THE ONLY THING THAT REACHES THE PTY PATH. Porta.Pty merges with the host environment and
// its `Environment` option is an override dictionary - omitting a name inherits it, and no value
// deletes it - so a Concierge child cannot be fixed at its spawn site at all. `ProcessAgentRunner`
// removes them again for headless children, as defence in depth rather than as the mechanism.
//
// Safe for the Host itself: `FORCE_COLOR` is a NODE convention and nothing in this solution reads
// it, so no .NET behaviour changes, its own console included.
foreach (var inherited in AgentEnvironment.MustBeAbsent)
{
    if (Environment.GetEnvironmentVariable(inherited) is null) continue;

    // SAID OUT LOUD, because a silent removal is one nobody can find when they go looking for why
    // their terminal lost its colour - and because this is the operator's own shell being
    // overridden, which they are entitled to know about.
    Environment.SetEnvironmentVariable(inherited, null);
    Console.WriteLine(
        $"Removed {inherited} from this Host's environment so agent children do not inherit it.");
}

// WHAT THIS PROCESS IS: all (control with its own run worker, the default), or control alone with
// its runs on workers that connect. A worker never gets here: it runs from WorkerRole, before this
// method is compiled. An unknown role is refused there for a real start, and here for a test Host.
var (role, roleRefusal) = HostRoles.Resolve([], builder.Configuration["Role"] ?? builder.Configuration[HostRoles.Variable]);
if (roleRefusal is not null || role == HostRole.Worker)
{
    Console.Error.WriteLine(roleRefusal ?? "--Role worker starts before the Host composes anything; this Host was composed, so it stops.");
    Environment.ExitCode = 2;
    return;
}

var control = role == HostRole.Control;

var dataRoot = builder.Configuration["DataRoot"]
    ?? Environment.GetEnvironmentVariable("HARNESS_DATA_ROOT");

if (dataRoot is null)
{
    // Non-zero, for the reason every other refusal in this file is: a supervisor told "served and
    // shut down cleanly" restarts straight back into it.
    Console.Error.WriteLine("No data root is set: set HARNESS_DATA_ROOT or --DataRoot and start again.");
    Environment.ExitCode = 1;
    return;
}

Directory.CreateDirectory(dataRoot);

// THE EARLIEST POINT A FILE CAN BE OPENED, and deliberately the earliest one: the root now exists,
// and everything below here that can fail - an
// operator command, a migration, a schema from the future, Kestrel refusing a port - fails INTO the
// file rather than into a window. What was printed before this line is replayed, so the lines that
// answer "what was this instance even configured as" are not the ones the ordering costs.
//
// The budget is DERIVED OR DECLARED, never a constant: `HostLog:Budget` if an operator names one,
// otherwise a hundredth of what is free where it lands, which is how it degrades on a small host
// instead of refusing. It is not in appsettings.json on purpose - a size shipped in the repository
// is a number tuned to one machine. See HostLogBudget.
hostLog.OpenIn(dataRoot, builder.Configuration["HostLog:Budget"]);

// What tells this instance from every other one on the machine. Derived from the data root, which
// is the instance boundary the line above declares - see InstanceIdentity. It composes with the
// layout rather than competing with it: the root is a function of the instance name, so the
// readable half of the cookie name comes out as that name with nothing coordinating the two.
var instance = InstanceIdentity.For(dataRoot);

var database = Path.Combine(dataRoot, "messages.db");

// WHERE EVERY COPY OF THE DATABASE GOES, and how many of each kind survive: read once, here, ahead
// of the operator commands, so `migrate` from a recovery switch, the startup migration and the
// daily backup all use one folder and one rule. See BackupSettings.
var backupSettings = BackupSettings.From(builder.Configuration, dataRoot);

// An operator command NEVER serves. Returning here is what makes recovery something a person at the
// machine does rather than a capability of the running system - see OperatorCommands.
//
// INSIDE the same handling the migration below gets, because every one of its failures is one of
// those three: --backup with a blocked destination throws BackupFailedException, --restore's own
// pre-restore copy the same, and --reset-password migrates before it can read an account. Left
// outside, an operator running the recovery switch would meet a thirty-line stack trace, one seam
// EARLIER than the catch that prevents it.
try
{
    var outcome = await OperatorCommands.TryRunAsync(args, dataRoot, Console.Out, backups: backupSettings);

    if (outcome != OperatorOutcome.NotAnOperatorCommand)
    {
        // A REFUSAL exits non-zero, for the same reason the catch below does: the scripts wrapping
        // these commands read $LASTEXITCODE, and a supervisor told "served and shut down cleanly"
        // restarts straight back into the same refusal.
        if (outcome == OperatorOutcome.Refused) Environment.ExitCode = 1;

        return;
    }
}
catch (Exception exception)
    when (exception is SchemaFromTheFutureException
              or BackupFailedException
              or MigrationFailedException)
{
    Console.Error.WriteLine(exception.Message);

    // Non-zero for the same reason as below: a refusal that exits 0 is a refusal no script wrapping
    // it - scripts/backup.ps1 and scripts/restore.ps1 both check $LASTEXITCODE - can see.
    Environment.ExitCode = 1;

    return;
}

// AFTER the operator commands and BEFORE the migration. Both halves of that placement matter.
//
// After, because an operator command is not a host: --backup uses SQLite's own VACUUM INTO and is
// deliberately safe to run against a LIVE database, so taking the root would refuse the one command
// whose whole point is that it does not need to.
//
// Before, because the migration takes a backup and rewrites the schema, and doing that underneath a
// host already serving the same file is the corruption this lock exists to prevent - not something
// to discover one step later.
//
DataRootLock taken;

try
{
    taken = DataRootLock.Acquire(dataRoot);
}
catch (DataRootInUseException exception)
{
    Console.Error.WriteLine(exception.Message);

    // Set rather than returned, and non-zero for the reason every other refusal here is: a
    // supervisor told "served and shut down cleanly" restarts straight back into it.
    Environment.ExitCode = 1;

    return;
}

// `using var` at this scope, so the root is released on every path out of the file below: a schema
// refusal, an exception, or an ordinary shutdown after app.Run returns.
using var held = taken;

// BEFORE any store is constructed. No store constructor creates schema: an order set by the
// declaration sequence below would work only while every statement was CREATE TABLE IF NOT
// EXISTS, and a step that must run once could not live there.
//
// Same ordering rule as LoadLabelsAsync further down, and for the same reason: a component that
// reaches a half-built schema fails in a way that reads as a data problem rather than a startup one.
//
// Message steps before auth steps. This expression is the only place that order is decided.
//
// All three refusals are CAUGHT and printed. An uncaught exception here scrolls thirty lines of
// stack trace and reads as "the host crashed" - which is exactly the failure port-free.ps1 exists to
// prevent, one layer down. The operator needs the sentence, not the trace. MigrationFailedException
// is the one that matters most in practice: a step whose SQL fails is the ORDINARY migration
// failure.
// HOISTED OUT OF THE `try` so the two facts it produces outlive the block. The migrator
// RETURNS rather than prints, and both of its answers are things this instance could not say about
// itself afterwards: the backup's path, which a terminal would lose at exactly the moment a
// migration went wrong, and what `PRAGMA journal_mode` actually replied. Both are recorded to the diagnostics log further down, once the store that
// holds them exists - which is only true AFTER this migration has created its table.
//
// The copies taken before a schema step go to the backups folder and are pruned after each one is
// taken and by every daily backup: the last `Backups:KeepBeforeStepDays` days (default 30, 0 for no
// age rule) plus the newest `Backups:KeepBeforeStep` (default 10).
var migrator = backupSettings.MigratorFor(database);
string? schemaBackup = null;

try
{
    schemaBackup = await migrator.ApplyAsync(SchemaModules.All);

    // One line, and only when the schema actually moved - ApplyAsync answers null when nothing was
    // pending, which is every ordinary restart. A backup taken silently is one nobody can find at
    // the moment they need it, and this copy exists precisely for the moment a migration went wrong.
    if (schemaBackup is not null)
    {
        Console.WriteLine($"Schema updated. Backup written to {schemaBackup}");
    }

    if (migrator.LastPrune is { } pruned) Console.WriteLine(pruned.Describe());
}
catch (Exception exception)
    when (exception is SchemaFromTheFutureException
              or BackupFailedException
              or MigrationFailedException)
{
    Console.Error.WriteLine(exception.Message);

    // RECORDED AS WELL AS PRINTED, and this is the one diagnostics write that has to be made by
    // hand: the host returns three lines below, so nothing downstream of here will ever run, and
    // the store registered with the container does not exist yet.
    //
    // IT DEGRADES RATHER THAN REFUSING, and the shape of that is worth stating. On an instance that
    // has booted before, `diagnostic_events` is already there and this row lands - which is the
    // case an operator meets, because a migration that fails is a migration that had something to
    // apply. On a database so fresh that even this table's own step has not run, the write finds no
    // table and is swallowed, like every other diagnostics write that cannot land. A store that
    // could not record the failure of the migration that creates it is the honest limit of a table
    // observing its own schema, and it is why capturing the host's stdout to a file ships beside
    // this rather than being replaced by it.
    await new SqliteDiagnosticsLog(database, DiagnosticRetention.ForDataRoot(dataRoot))
        .WriteAsync(
            DiagnosticSeverity.Error,
            DiagnosticKinds.DatabaseMigrationFailed,
            DiagnosticSources.Database,
            exceptionType: exception.GetType().FullName,
            message: exception.Message,
            detail: exception is MigrationFailedException failed
                ? $$"""{"step":"{{failed.StepId}}"}"""
                : null);

    // NON-ZERO, because a refusal that exits 0 is a refusal the machine cannot see: the host has
    // just said it will not serve, and 0 tells a supervisor that it served and shut
    // down cleanly. A supervisor reading 0 restarts straight back into the same refusal, forever.
    // Set rather than returned: these are top-level statements, and a `return 1;` here would make
    // every other exit path in the file have to carry a value too.
    Environment.ExitCode = 1;

    return;
}

// THE LEDGER'S START AND THE INSTANCE'S ID, before anything appends: the one-time backfill of
// `usage_ledger` and `workflow_ledger` from the log, and `instance.id`. See LedgerStart. A failure
// is printed and the host starts anyway - the backfill's own row is what marks it done, so the next
// start tries again, and rows recovered then are keyed so none is counted twice.
LedgerStartResult? ledgerStart = null;
try
{
    var ledger = ledgerStart = await LedgerStart.RunAsync(database);

    if (ledger.BackfilledRuns is { } recovered)
    {
        Console.WriteLine(
            $"Usage ledger started: {recovered} run(s) and {ledger.BackfilledWorkflows} workflow close(s) recovered from the log.");
    }
}
// ANY FAILURE, not only SQLite's: a backfill that met a stamp it could not parse must not stop the
// Host from starting any more than a locked database does. Rows are keyed, so the retry counts none twice.
catch (Exception exception) when (exception is not OperationCanceledException)
{
    Console.Error.WriteLine($"The usage ledger's start did not finish and is tried again at the next start: {exception.Message}");
}

// BACKLOG ITEMS TAKE THEIR DISPATCH WORKFLOW'S OUTCOME, once, for the items dispatched before the
// link writer did it for them. One transaction; a failure leaves it to the next start.
try
{
    if (await BacklogOutcomeStart.RunAsync(database) is { } inherited)
    {
        Console.WriteLine($"Backlog outcomes: {inherited} item(s) took the outcome their dispatch workflow serves.");
    }
}
catch (Exception exception) when (exception is not OperationCanceledException)
{
    Console.Error.WriteLine($"The backlog outcome back-fill did not finish and is tried again at the next start: {exception.Message}");
}

var workflowWaits = new MessageWaitRegistry();
var store = new SqliteMessageStore(database, workflowWaits);

builder.Services.AddSingleton<IMessageLog>(store);
builder.Services.AddSingleton<ICursors>(store);
builder.Services.AddSingleton<ISubscriptions>(store);
builder.Services.AddSingleton(workflowWaits);
builder.Services.AddSingleton<IUsageLedger>(new SqliteUsageLedger(database));

// EVERY ADMISSION HOLD, written down by admission as it happens (`admission_holds`), so the team
// activity read can show a wait for a slot with its reason after the fact. The writer queues; a
// write that fails is logged and admission carries on.
var admissionHolds = new SqliteAdmissionHolds(database);
var admissionHoldWriter = new AdmissionHoldWriter(admissionHolds);

// A HOST START CLOSES WHAT IT DID NOT SEE END: a hold still open belonged to a Host that went down
// while the run waited. It is closed at this start, its reason unchanged, marked unfinished. A
// failure is printed and the Host starts anyway; the rows stay open for the next start.
try
{
    if (await admissionHolds.CloseUnfinishedAsync(DateTimeOffset.UtcNow) is > 0 and var closed)
    {
        Console.WriteLine($"Closed {closed} admission hold(s) left open by the last run of this Host, marked unfinished.");
    }
}
catch (Exception exception) when (exception is not OperationCanceledException)
{
    Console.Error.WriteLine($"The admission holds left open could not be closed and are tried again at the next start: {exception.Message}");
}
builder.Services.AddSingleton<IAdmissionHoldLedger>(admissionHolds);
builder.Services.AddSingleton(admissionHoldWriter);

// THE OUTCOMES AND THEIR LINKS, in the same file: every person's write lands with its tenant_events
// row in one transaction, written with the tenant log's own columns.
var outcomeStore = new SqliteOutcomeStore(database, TenantAuditRow.AppendAsync);
builder.Services.AddSingleton(outcomeStore);
builder.Services.AddSingleton<IOutcomeStore>(outcomeStore);

// THE GATE, read through a delegate so a person's change applies to the next declaration.
builder.Services.AddSingleton(sp => new OutcomeGate(
    () => sp.GetRequiredService<TenantSettings>().OutcomesRequireForCompletion, outcomeStore));

// THE CONCIERGE'S MERGE, read through a delegate so turning it off refuses the next merge of a
// Concierge session already running.
builder.Services.AddSingleton(sp => new ConciergeMergeGate(
    () => sp.GetRequiredService<TenantSettings>().ConciergeMayMerge));

// THE CONCIERGE'S ARCHIVE, read through a delegate for the same reason.
builder.Services.AddSingleton(sp => new ConciergeArchiveGate(
    () => sp.GetRequiredService<TenantSettings>().ConciergeMayArchive));
builder.Services.AddSingleton(new LedgerIdentity(ledgerStart?.InstanceId, ledgerStart?.LedgerStartedAt));
// THE ONE BOARD PROJECTION, scoped by each team's floor through TeamRegistry (late-bound: the registry
// is composed after the stores), and told of the log's purges by retention and Reset.
builder.Services.AddSingleton(sp => new KanbanStore(store, team => sp.GetRequiredService<TeamRegistry>().FloorFor(team)));

// THE ROLLUP'S TIMINGS PER TEAM, kept until that team's rows move, and told of purges the same way.
builder.Services.AddSingleton(new RollupCache(store));

// What each container has accepted and not finished. Without it nothing is durable: a container's
// queue is an in-memory Channel and its cursor advances when a message is OFFERED, so a host
// stopping between the two would drop the work with no redelivery to fall back on and nothing to
// say it had happened.
builder.Services.AddSingleton<IPendingDeliveries>(new SqlitePendingDeliveries(database));
builder.Services.AddSingleton<PendingDeliveriesAtStart>();

// THE WAKES MEMBERS ASKED FOR (`wake_me`), stored so a restart keeps them. See `WakeMe`.
builder.Services.AddSingleton<IWakeStore>(new SqliteWakeStore(database));

// WHAT RUNNING MEMBERS' WORKTREES DID, recorded for the Monitor's replay. See WorkspaceFileChangeStore.
builder.Services.AddSingleton(new WorkspaceFileChangeStore(database));

// WHAT RUNNING AGENTS DID, step by step, recorded for the Monitor. See AgentStepStore.
builder.Services.AddSingleton(new AgentStepStore(database));

// Order carries no schema obligation - SchemaMigrator above has already applied every step
// and set journal_mode on the file. These are readers and writers over a database that exists.
var users = new SqliteUserStore(database);
var principals = new SqlitePrincipalStore(database);

builder.Services.AddSingleton<IUserStore>(users);
builder.Services.AddSingleton<IPrincipalStore>(principals);

// THE WORKER KEY: what a worker process connects to control with (Workers:Key, or HARNESS_WORKER_KEY;
// in an image, the file the entrypoint moved it to, Workers:KeyFile or HARNESS_WORKER_KEY_FILE).
// Stored hashed as a principal with no permit, or removed when none is configured, so a key a
// previous start stored never outlives its configuration. Taken on the worker connection alone.
var (workerKey, workerKeyRefusal) = WorkerKeyFile.Read(
    builder.Configuration["Workers:Key"] ?? builder.Configuration["HARNESS_WORKER_KEY"],
    builder.Configuration["Workers:KeyFile"] ?? builder.Configuration[WorkerKeyFile.FileVariable]);
if (workerKeyRefusal is not null)
{
    Console.Error.WriteLine(workerKeyRefusal);
    Environment.ExitCode = 2;
    return;
}
WorkerKeyGate.SetAsync(principals, workerKey).GetAwaiter().GetResult();
var workerKeys = new WorkerKeyGate(workerKey);
builder.Services.AddSingleton(workerKeys);

// Teams and their members, in the same file for the same reason accounts are - see AuthSchema's
// remarks on why the non-message tables live together and why the Host does not own one directly.
builder.Services.AddSingleton<ITeamStore>(new SqliteTeamStore(database));
builder.Services.AddSingleton<ITriggerStore>(new SqliteTriggerStore(database));
builder.Services.AddSingleton<ITenantLog>(new SqliteTenantLog(database));

// A team's sites: their versions and their data (auth-015), and the service every caller - the
// browser routes, a plugin's records, the team deletion - goes through. See SiteService.
builder.Services.AddSingleton<ISiteStore>(new SqliteSiteStore(database));
// The team lookup is LATE: TeamRegistry needs the member runtime, whose plugin runner needs this.
builder.Services.AddSingleton(sp => new SiteService(
    sp.GetRequiredService<ISiteStore>(),
    team => sp.GetRequiredService<TeamRegistry>().ExistingName(team),
    sp.GetRequiredService<TeamPaths>(),
    sp.GetRequiredService<IMessageLog>(),
    removal: sp.GetRequiredService<FolderRemoval>()));
builder.Services.AddSingleton<SiteCapability>();

// The folders a deletion or reset could not finish removing (auth-012), retried at start and on
// request. See FolderRemoval.
builder.Services.AddSingleton<IUnfinishedRemovals>(new SqliteUnfinishedRemovals(database));

// INSTANCE-WIDE SETTINGS, read once here - rows over appsettings.json over built-in defaults -
// and then held in memory. Every consumer below is handed a delegate onto this, never a number, so
// a person's change applies at its next use with no restart. See `TenantSettings`.
var tenantSettings = new TenantSettings(new SqliteTenantSettingsStore(database), builder.Configuration);
await tenantSettings.LoadAsync();
builder.Services.AddSingleton(tenantSettings);

// "System packages" is installed by the entrypoint before the host starts, so the host keeps a
// plain copy of the list on the volume for it: now, and on every change. See `SystemPackages`.
SystemPackages.WriteFile(dataRoot, tenantSettings.SystemPackages);
tenantSettings.Changed += name =>
{
    if (name == TenantSettings.SystemPackagesName) SystemPackages.WriteFile(dataRoot, tenantSettings.SystemPackages);
};
builder.Services.AddSingleton<ISkillStore>(new SqliteSkillStore(database));
// A person's edit lands with its tenant_events row in one transaction, as the outcomes' writes do.
builder.Services.AddSingleton<IBacklogStore>(
    new SqliteBacklogStore(database, audit: TenantAuditRow.AppendAsync));
builder.Services.AddSingleton<TenantLogging>();

// THE THIRD STORE. Not the message log and not the tenant log: see IDiagnosticsLog for the
// boundary, which is the whole of this feature's design work.
//
// THE BOUND IS RESOLVED HERE because this is the one place that knows the data root, and it is
// DECLARED-OR-DERIVED rather than a constant: absent settings derive a row count from the size of
// the volume this instance sits on, so a small host keeps a shorter log instead of filling its
// disk, and nothing refuses. See DiagnosticRetention.
//
// Constructed rather than resolved by type, like every other store above it: its parameters are a
// path and a bound, neither of which is a service.
var diagnosticsRetention = DiagnosticRetention.ForDataRoot(
    dataRoot,
    builder.Configuration.GetValue<int?>("Diagnostics:RetentionDays"),
    builder.Configuration.GetValue<long?>("Diagnostics:MaxRows"));

var diagnostics = new SqliteDiagnosticsLog(database, diagnosticsRetention);

builder.Services.AddSingleton<IDiagnosticsLog>(diagnostics);
builder.Services.AddSingleton<DiagnosticsRecorder>();

// A SINGLETON because it is the only thing that can tell a reconnect from a first connection, and
// a hub instance lives for one invocation. See TransportWatch for why this is in memory and lossy.
builder.Services.AddSingleton<TransportWatch>();

// The key ring that encrypts every session ticket, IN the data root and filed under this instance's
// name. Without this the Host configures neither, and the isolation between two instances is
// ACCIDENTAL: ASP.NET Core derives its discriminator from the CONTENT ROOT PATH, so two worktrees
// happen to differ while two hosts sharing a content root - a copied tree, a published build, one
// image run twice - cross-decrypt each other's tickets. The second host then answers 200 OK,
// authenticated as a user id its database has never heard of, which presents as a permission bug
// rather than as the wrong session.
//
// Pinning it here makes the key ring obey the rule everything else here obeys - one data root, one
// database, one process - so isolation is a property of the code rather than of where the tree
// happens to sit on disk. It is independent of the cookie name below and worth having alone.
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataRoot, "keys")))
    .SetApplicationName(instance);

// AT REST the key ring is plaintext under the data root. The container boundary and the file mode are the isolation, and docs/architecture/secrets.md is the
// plan for narrowing who inside the container can read it.

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        // NAMED FOR THE INSTANCE, because a cookie is keyed by (host, path, name) and port is not
        // part of that key - RFC 6265 §8.5, by design, in every browser. A constant name would let
        // two instances on different ports share one slot in one jar, so signing in to either
        // would sign you out of the other.
        options.Cookie.Name = InstanceIdentity.CookieNameFor(dataRoot);
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;

        // Sent over the reverse-proxy tunnel as well as loopback, so let the request decide rather
        // than pinning Always (which would drop the cookie on plain-http localhost). SameAsRequest
        // reads Request.IsHttps, which is only the truth about the tunnel because UseForwardedHeaders
        // is wired below - see the comment there. The two are one mechanism; neither works alone.
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromDays(14);
        options.SlidingExpiration = true;

        // An API must answer 401, not 302 to a login page that does not exist. Without these two
        // the SPA's fetch() follows the redirect and parses HTML as JSON.
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };

        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    })
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
        ApiKeyAuthenticationHandler.Scheme, _ => { });

// The policy names its schemes explicitly, and every scheme it names must already be registered or
// the policy throws when it is evaluated. The default authentication scheme is the cookie, so
// UseAuthentication alone only populates context.User from the cookie - a key-authenticated request
// gets its principal because this policy names that scheme too and the authorization middleware
// authenticates it.
//
// The DEFAULT policy applies where authorization was ASKED for; the FALLBACK applies where nothing
// was asked at all - which is every endpoint here. That is what covers the console WebSocket and the
// SignalR hub by their being routes, rather than by anyone remembering them.
var policy = new AuthorizationPolicyBuilder(
        CookieAuthenticationDefaults.AuthenticationScheme,
        ApiKeyAuthenticationHandler.Scheme)
    .RequireAuthenticatedUser()
    .Build();

builder.Services.AddAuthorizationBuilder()
    .SetDefaultPolicy(policy)
    .SetFallbackPolicy(policy);

// Registered ABOVE every consumer. One place decides where a team's files are; composing any of
// these paths a second time from dataRoot is two stores of one fact.
var teamPaths = new TeamPaths(dataRoot);
builder.Services.AddSingleton(teamPaths);

// The picker's reach. Effective() adds the instance root to whatever is configured - it is never
// written into appsettings.json, because the data root is a runtime value.
var configuredFileBrowser = builder.Configuration.GetSection(FileBrowserOptions.Section).Get<FileBrowserOptions>();

// `Effective` labels the instance root from the resolved data root itself.
var effectiveFileBrowserRoots = FileBrowserOptions.Effective(configuredFileBrowser, dataRoot);

var fileBrowser = new FileBrowserPolicy(effectiveFileBrowserRoots);

// BOTH stages, including the implied instance root - see ReportDrops's own doc comment for why a
// diff against only the raw configured list would miss an instance root the POLICY drops
// (`--DataRoot=\\?\C:\foo`, a device path), leaving a picker with zero roots and nothing saying why.
FileBrowserOptions.ReportDrops(configuredFileBrowser, effectiveFileBrowserRoots, fileBrowser, Console.Error);

builder.Services.AddSingleton(fileBrowser);

// The ledger reads the same file the store writes. A second READER, never a second writer - which
// is what makes it impossible for the ledger to disagree with the log about what happened. The
// installed plugins' high-volume events stay out of it, as `agentContainer.progress` does.
builder.Services.AddSingleton<ILedger>(sp => new SqliteLedger(database, sp.GetRequiredService<PluginCatalog>()));
builder.Services.AddSingleton<ITranscriptStore>(new FileTranscriptStore(teamPaths));
builder.Services.AddSingleton<IContextBuilder>(sp =>
    new OutcomeNudge(
        new LedgerContextBuilder(sp.GetRequiredService<ILedger>()),
        sp.GetRequiredService<IOutcomeStore>()));

// BUILT-IN PRESETS FROM THE BUILD, custom ones from agents.json. Nothing built-in is
// written to the volume, so a fix to a built-in preset reaches this instance on its next start.
var loadedCatalog = AgentCatalogFile.BuiltIns()
    .Concat(AgentCatalogFile.LoadCustom(dataRoot, Console.Out))
    .ToList();
var unmeasuredHeadlessPresets = Program.UnmeasuredHeadlessPresets(loadedCatalog);

// THE INSTALLED PLUGINS, read from `<dataRoot>/plugins` - data, not code: nothing is compiled in and
// nothing is registered by DI per plugin, so `POST /api/plugins/rescan` registers a new one with no
// restart. A refused manifest is named here, once, with the field that is wrong.
// In control a plugin's runtime is the workers' to have, never looked up on control's own PATH
// (PluginCatalog.OnWorkers).
var pluginCatalog = new PluginCatalog(Path.Combine(dataRoot, "plugins"), control ? PluginCatalog.OnWorkers : null);
PluginEndpoints.Report(pluginCatalog.Rescan(), Console.Out);
builder.Services.AddSingleton(pluginCatalog);

// INSTALLING FROM A FOLDER INSIDE THE INSTANCE, for a person's route and the operator CLI's request
// file alike. Files are given the agent's group, which runs every plugin, when the Host switches to it.
builder.Services.AddSingleton(sp =>
{
    var runAs = sp.GetRequiredService<AgentLaunchUser>();
    return new PluginInstaller(pluginCatalog, dataRoot, runAs.Switches ? runAs.Gid : -1);
});

// REMOVING A PLUGIN OR ONE VERSION, from the web app: `plugin remove`'s rules, the installer's
// gate, and its tenant row in the same transaction as the move.
builder.Services.AddSingleton(sp => new PluginRemover(
    pluginCatalog, sp.GetRequiredService<PluginInstaller>(), sp.GetRequiredService<TeamRegistry>(), database));

// THE EVENTS THE INSTALLED PLUGINS DECLARE join the platform's in every lookup - triggers, filters,
// `{event.*}` tokens, the high-volume rule, `GET /api/events` - read off the catalog on each call,
// so a rescan is seen at once. Released when this Host stops.
var pluginEvents = EventCatalog.Register(pluginCatalog);

// A PLUGIN MEMBER'S SETTINGS: configuration and secret BINDINGS in `team_member_config`, and the values
// resolved by logical key at each run: a key a person set from the screen (`plugin_secrets`, Data
// Protection ciphertext) first, then the Host's own environment, where `secret set` values arrive.
builder.Services.AddSingleton(sp => new PluginSecretStore(
    database, sp.GetRequiredService<IDataProtectionProvider>(), new EnvironmentSecretStore()));
builder.Services.AddSingleton<ISecretStore>(sp => sp.GetRequiredService<PluginSecretStore>());
builder.Services.AddSingleton<IPluginMemberSettingsStore>(new SqlitePluginMemberSettings(database));

// CONNECTIONS: OAuth accounts the Host holds for plugins (see Connections). The client secret and
// every token are Data Protection ciphertext under `<dataRoot>/keys`; the provider is reached only
// through IOAuthEndpoints, which the tests replace.
builder.Services.AddSingleton(sp => new ConnectionStore(database, sp.GetRequiredService<IDataProtectionProvider>()));
builder.Services.AddHttpClient(nameof(HttpOAuthEndpoints), client => client.Timeout = TimeSpan.FromSeconds(30));

// THE RELEASE CHECK: whether a newer release is out, for the version chip. It reads the release
// list of the repository the operator CLI names (Updates:Repository, or HARNESS_RELEASE_REPOSITORY);
// with none it reads nothing and says so. It never updates anything (see ReleaseCheck).
builder.Services.AddHttpClient(nameof(GitHubReleaseFeed), client => client.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddSingleton<IReleaseFeed>(sp => new GitHubReleaseFeed(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(GitHubReleaseFeed)),
    builder.Configuration[ReleaseCheck.RepositorySetting] ?? builder.Configuration[ReleaseCheck.RepositoryVariable]));
builder.Services.AddSingleton(sp => new ReleaseCheck(
    sp.GetRequiredService<IReleaseFeed>(),
    () => sp.GetRequiredService<TenantSettings>().UpdatesCheck,
    () => DateTimeOffset.UtcNow,
    sp.GetRequiredService<ILogger<ReleaseCheck>>()));
builder.Services.AddHostedService(sp => new ReleaseCheckLoop(sp.GetRequiredService<ReleaseCheck>()));

// THE PACKAGE CATALOG: what may be added from Marketplace > Browse. It reads the catalog address
// the operator CLI names (Marketplace:Catalog, or HARNESS_MARKETPLACE_CATALOG); with none it reads
// nothing and says so. A fetch only brings a package into the documents (see Marketplace).
builder.Services.AddHttpClient(nameof(HttpMarketplaceFeed), client => client.Timeout = TimeSpan.FromMinutes(10));
builder.Services.AddSingleton<IMarketplaceFeed>(sp => new HttpMarketplaceFeed(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(HttpMarketplaceFeed)),
    builder.Configuration[Marketplace.CatalogSetting] ?? builder.Configuration[Marketplace.CatalogVariable]));
builder.Services.AddSingleton(sp => new Marketplace(
    sp.GetRequiredService<IMarketplaceFeed>(),
    () => sp.GetRequiredService<TenantSettings>().MarketplaceCheck,
    () => DateTimeOffset.UtcNow,
    () => sp.GetRequiredService<TeamRegistry>().All()
        .Where(team => team.Solution is not null)
        .Select(team => (team.Id, team.Solution!)),
    id => sp.GetRequiredService<PluginCatalog>().For(id)?.Manifest.Version,
    sp.GetRequiredService<ILogger<Marketplace>>(),
    // A custom-<id> provider in the catalog's needs is said by the name a person gave it here.
    async ct => (await sp.GetRequiredService<ConnectionStore>().ProvidersAsync(ct))
        .Where(p => !string.IsNullOrWhiteSpace(p.Name))
        .ToDictionary(p => p.Id, p => p.Name!, StringComparer.Ordinal)));
builder.Services.AddHostedService<MarketplaceLoop>();
builder.Services.AddSingleton<IOAuthEndpoints>(sp => new HttpOAuthEndpoints(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(HttpOAuthEndpoints))));
// A MAILBOX's login test: IMAP and SMTP over a socket in the Host, no process. Tests replace it with
// one pointed at a fake server in the test process.
builder.Services.AddSingleton<IMailLogin>(new SocketMailLogin());
builder.Services.AddSingleton(sp => new Connections(
    sp.GetRequiredService<ConnectionStore>(), sp.GetRequiredService<IOAuthEndpoints>(), sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<IUserStore>(), mail: sp.GetRequiredService<IMailLogin>(),
    // THE INSTANCE'S MICROSOFT CLIENT: the public client the operator CLI names (Connections:MicrosoftClientId,
    // or HARNESS_MICROSOFT_CLIENT_ID), used while no Microsoft client is saved; with none, Microsoft
    // waits for a person's own app.
    microsoftClientId: builder.Configuration[Connections.MicrosoftClientSetting] ?? builder.Configuration[Connections.MicrosoftClientVariable]));
builder.Services.AddSingleton(sp => new ConnectRequests(
    sp.GetRequiredService<Connections>(), dataRoot, sp.GetRequiredService<ILogger<ConnectRequests>>(),
    sp.GetRequiredService<TeamRegistry>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<ConnectRequests>());

if (unmeasuredHeadlessPresets.Count > 0)
{
    Console.WriteLine(
        $"No usage format for Headless presets {string.Join(", ", unmeasuredHeadlessPresets)} - "
        + "runs on them stay (unknown) until usageFormat is set in the Agents screen.");
}

// Check for rooted file names and warn about them - still serve them.
foreach (var agent in loadedCatalog)
{
    var fileName = agent.Launch.FileName;
    if (fileName is not null && fileName.IndexOfAny(['\\', '/', ':']) >= 0)
    {
        Console.WriteLine(
            $"WARNING: Agent '{agent.Name}' has a rooted fileName '{fileName}' - put the executable on PATH and name it. Serving anyway.");
    }
}

// The operator's tags for a built-in, and whether a member's CLI may sign its commits, are read
// through their settings on every use.
var agentCatalog = new AgentCatalog(
    loadedCatalog, () => tenantSettings.AgentTags, () => tenantSettings.AgentsCommitAttribution);
builder.Services.AddSingleton(agentCatalog);

// SOLUTION PACKAGES are checked against this Host's own catalogs - its Agent presets, its events
// (installed plugins' included) and its runtimes - through one service the route, the install and the
// board's notice share. See docs/solutions.md.
builder.Services.AddSingleton(sp => new SolutionService(
    new SolutionChecker(SolutionPlatform.For(sp.GetRequiredService<AgentCatalog>(), pluginCatalog)), dataRoot,

    // A LINK opens only the instance's documents and the teams' folders.
    () =>
    {
        var registry = sp.GetRequiredService<TeamRegistry>();
        var paths = sp.GetRequiredService<TeamPaths>();
        return registry.All().Select(t => paths.RootFor(t.Id)).Prepend(paths.TenantDocuments);
    }));

// WHICH SOLUTION PACKAGE EACH TEAM CAME FROM (`team_solutions`, auth-016), held in memory as well so
// a team's summary reads it with no round trip. Written by the install and the update, swept by a
// team's deletion.
var teamSolutions = new TeamSolutionIndex(new SqliteTeamSolutionStore(database));
await teamSolutions.LoadAsync();
builder.Services.AddSingleton(teamSolutions);
builder.Services.AddSingleton<ITeamSolutionStore>(teamSolutions);
builder.Services.AddSingleton<ITeamSolutions>(teamSolutions);

// THE INSTALL AND UPDATE OF A SOLUTION PACKAGE: the check, then the stores a person's own clicks
// use, step by step, undone in reverse when a step fails. See SolutionInstaller.
builder.Services.AddSingleton(sp =>
{
    var agents = sp.GetRequiredService<AgentCatalog>();
    var probe = sp.GetRequiredService<AgentInstallProbe>();
    var runAs = sp.GetRequiredService<AgentLaunchUser>();
    var listPush = sp.GetRequiredService<TeamListPush>();

    return new SolutionInstaller(
        sp.GetRequiredService<SolutionService>(),
        sp.GetRequiredService<PluginInstaller>(),
        pluginCatalog,
        sp.GetRequiredService<TeamRegistry>(),
        sp.GetRequiredService<TeamRepoSetup>(),
        sp.GetRequiredService<TeamDeletion>(),
        sp.GetRequiredService<MemberDeletion>(),
        sp.GetRequiredService<TeamSkills>(),
        sp.GetRequiredService<TeamPaths>(),
        sp.GetRequiredService<SiteService>(),
        sp.GetRequiredService<TriggerCreation>(),
        sp.GetRequiredService<ITeamSolutionStore>(),
        sp.GetRequiredService<TeamDocuments>(),
        sp.GetRequiredService<FolderWatch>(),
        sp.GetRequiredService<TenantLogging>(),

        // THE AGENT a package's agent members run when it names none (SolutionDefaultAgent).
        () => SolutionDefaultAgent.Of(agents, probe),
        sp.GetRequiredService<Connections>(),
        sp.GetRequiredService<ConnectionStore>(),
        sp.GetRequiredService<IPluginMemberSettingsStore>(),
        new TeamAnnouncements(
            team => listPush.AnnounceCreatedAsync(team),
            async team =>
            {
                var viewers = (await listPush.EntitledViewerIdsAsync(team)).ToArray();
                return () => listPush.AnnounceDeletedAsync(team, viewers);
            }),
        runAs.Switches ? runAs.Gid : -1,
        sp.GetRequiredService<ISecretStore>(),
        sp.GetRequiredService<TriggerSweep>(),
        sp.GetRequiredService<SqliteOutcomeStore>(),
        sp.GetRequiredService<FolderRemoval>());
});

// WHAT A SOLUTION TEAM WAITS FOR, asked before every trigger's fire. The installer is late-bound:
// it fires run-at-install through TriggerSweep, which asks this.
builder.Services.AddSingleton(sp => new SolutionWait(
    sp.GetRequiredService<ITeamSolutionStore>(),
    () => sp.GetRequiredService<SolutionInstaller>(),
    sp.GetRequiredService<ContainerHost>(),
    sp.GetRequiredService<IMessageLog>(),
    sp.GetRequiredService<ITriggerStore>(),
    sp.GetRequiredService<TenantLogging>()));

// THE SOLUTIONS LAUNCHER AND A SOLUTION'S CONTROL PANEL: reads only; every control is an existing route.
builder.Services.AddSingleton(sp => new SolutionPanels(
    sp.GetRequiredService<ITeamSolutionStore>(),
    sp.GetRequiredService<SolutionInstaller>(),
    sp.GetRequiredService<TeamRegistry>(),
    sp.GetRequiredService<ContainerHost>(),
    sp.GetRequiredService<ITriggerStore>(),
    sp.GetRequiredService<TriggerCost>(),
    sp.GetRequiredService<IMessageLog>(),
    sp.GetRequiredService<SiteService>(),
    sp.GetRequiredService<TeamDocuments>(),
    pluginCatalog));

// The board's notice for a package a workflow wrote, checked through that one door when the
// workflow is declared complete.
builder.Services.AddSingleton(sp => new SolutionNotice(
    sp.GetRequiredService<SolutionService>(), sp.GetRequiredService<TeamDocuments>(), sp.GetRequiredService<IMessageLog>()));

// What each role is offered, for the "Available skills" list in every system prompt. Filled with
// the custom skills at start, before any team is restored, and refreshed after every skill write.
builder.Services.AddSingleton(new SkillDirectory());

// Team skills: a team's own custom skills, offered only to its members. What an installer calls.
builder.Services.AddSingleton(sp => new TeamSkills(
    sp.GetRequiredService<ISkillStore>(),
    sp.GetRequiredService<SkillDirectory>(),
    sp.GetRequiredService<TeamRegistry>()));

// Whether the CLI a preset names is on this machine at all. A SINGLETON because the few-second
// cache is the whole reason it is a class rather than a static method: a per-request instance would
// walk PATH once per preset on every render, and one that lived forever would go on reporting a
// problem the person had just fixed. It runs no candidate binary - it resolves a name and stops -
// so it takes no dependency on anything that could.
// Constructed rather than resolved by type: its parameters are the injection seams the specs
// drive (a clock, the cache window, the workers' answers) and none of them is resolved by type, so leaving the
// container to choose a constructor would either fail to find one or quietly pick the wrong shape
// the day another is added.
// In CONTROL its answer is the workers' and never this machine's PATH, which has no agent CLI on it:
// what each placeable worker measured (WorkerInstalls), from the sign-in probe and from the
// measurement each worker gets as it joins. In `all` the runs are this machine's, and so is the PATH.
builder.Services.AddSingleton(sp => WorkerInstalls.Over(sp.GetRequiredService<WorkerPool>()));
// A command the update gate holds reads updating, in both roles, and never not installed.
builder.Services.AddSingleton(sp => control
    ? new AgentInstallProbe(workers: sp.GetRequiredService<WorkerInstalls>(), gate: sp.GetRequiredService<AgentUpdateGate>())
    : new AgentInstallProbe(gate: sp.GetRequiredService<AgentUpdateGate>()));
// The one path from the progress ROUTE to whatever is currently running that member. Singleton
// because both ends have to be looking at the same object for a heartbeat to mean anything.
builder.Services.AddSingleton<RunHeartbeat>();
// THE INSTANCE-WIDE LEASES (`heavy`), across every team. The holder count is read through a
// delegate on every call, so a settings write applies at the next acquire or release. ILeaseState
// is the read-only view the activity monitor reads. See InstanceLeases and LeaseActions.
builder.Services.AddSingleton(sp =>
    new InstanceLeases(() => sp.GetRequiredService<TenantSettings>().LeasesHeavyHolders));
builder.Services.AddSingleton<ILeaseState>(sp => sp.GetRequiredService<InstanceLeases>());
// BY FACTORY over the shared worker: a lease's moves reach the runs as protocol messages.
builder.Services.AddSingleton(sp => new LeaseActions(
    sp.GetRequiredService<InstanceLeases>(), sp.GetRequiredService<IRunWorker>(), sp.GetRequiredService<IMemberReports>()));
// WRAPPED, AND THE WRAPPING IS LOAD-BEARING. `ProcessAgentRunner` spawns the process and reads what
// it wrote; `CredentialUseRunner` answers what the SERVER saw while it ran, which is how a run that
// did nothing is told from one that decided there was nothing to do. Unwrap this and every result
// reports "not measured" - which AgentResult.Succeeded treats as success, silently, which is the
// defect it exists to catch. `AgentRunnerCompositionTests` asserts what comes out of here.
// Who agent children run as, decided once here and logged at start. `agent` when the
// Host can switch to it, the Host's own user otherwise - see AgentLaunchUser.
builder.Services.AddSingleton(_ => InProcessWorker.LaunchUser(
    builder.Configuration["Agents:RunAs"] ?? Environment.GetEnvironmentVariable("HARNESS_AGENT_USER")));
// The runs in flight, for the live route. The runner records; nothing is stored.
builder.Services.AddSingleton<LiveRuns>();
// WHAT EACH MEMBER RUN'S CREDENTIALS ARE, held in memory so its reports and its transcript are
// redacted of it as the run's output is. Nothing is stored. See RunSecrets.
builder.Services.AddSingleton<RunSecrets>();
// WHO MAY USE AN AGENT CLI'S SHARED INSTALL: member runs share it, a platform update has it alone,
// and a launch that arrives during an update waits for it. One per Host, shared by the runner, the
// Concierge's launch and the updater, or the hold holds nothing.
builder.Services.AddSingleton<AgentUpdateGate>();
// WHAT CONTROL ASKS A WORKER ABOUT ITS AGENT CLIS - a sign-in probe, a listing, a version, an update,
// a removal as the agent - and the answers, by request. Asked of the connected worker with the most
// measured headroom; in a Host that runs its runs itself that is its own worker, over the transport.
builder.Services.AddSingleton(sp => new WorkerAsks(() => sp.GetRequiredService<WorkerPool>().Worker(null)));
// A person's update runs once, on one connected worker, when the gate has drained (AgentCliUpdater).
builder.Services.AddSingleton(sp => new AgentCliUpdater(
    sp.GetRequiredService<AgentCatalog>(),
    sp.GetRequiredService<AgentUpdateGate>(),
    sp.GetRequiredService<AgentLaunchUser>(),
    dataRoot,
    sp.GetRequiredService<WorkerAsks>()));
// THE RUN WORKER, IN THIS PROCESS. Control talks to runs only through the run protocol: a run is
// started, reported and ended by records over the in-process transport, and this worker is the one
// place that launches, measures and limits them. ONE worker, shared by the runner, the leases, the
// reports and the capacity sample (RunWorkerCompositionTests). It is composed here from what control
// owns - the idle-clock registry, who agent children run as, the shared update gate, the memory
// settings and the heavy allowance's rule - and decides the rest itself: HOW MUCH MEMORY ONE RUN MAY
// USE, AND HOW (a cgroup per run, an rlimit per process, or nothing, decided once from the
// container's own cgroup and logged at start; see RunMemoryLimits), and THE HEAVY ALLOWANCE, which
// raises the run holding the `heavy` lease to the container's limit less the Host reserve less what
// the other runs are measured to use, and lowers it when it lets go (RunAllowances). The cgroup and
// proc roots are configuration so a test Host reads a fixture.
// WHAT WORKERS STREAM TO CONTROL - a terminal's output, a followed transcript, a file's text - in
// both roles: in one process the Host's own worker hands its chunks over by direct call.
builder.Services.AddSingleton<WorkerStreams>();
// THE AGENT'S FILES, read on a worker as the agent: a finished run's transcript and a run's tool check.
builder.Services.AddSingleton(sp => new WorkerReads(
    WorkersOf(sp), sp.GetRequiredService<WorkerStreams>()));
builder.Services.AddSingleton(sp => new RunDirectory(
    () => sp.GetRequiredService<IMemberReports>(),
    () => sp.GetRequiredService<IDiagnosticsLog>(),
    sp.GetRequiredService<LiveRuns>(),
    secrets: sp.GetRequiredService<RunSecrets>()));
if (!control)
{
builder.Services.AddSingleton(sp => InProcessWorker.Create(
    WorkerId.Local,
    sp.GetRequiredService<RunHeartbeat>(),
    sp.GetRequiredService<AgentLaunchUser>(),
    sp.GetRequiredService<AgentUpdateGate>(),
    tenantSettings.RunMemoryLimit,
    tenantSettings.RunMemoryCeiling,
    tenantSettings.HeavyRunMemoryLimit,
    async (envelope, ct) =>
    {
        await sp.GetRequiredService<WorkerAsks>().HandleAsync(envelope, ct);
        await sp.GetRequiredService<RunDirectory>().HandleAsync(envelope, ct);
        if (envelope.Event is RunMeasured or TerminalMeasured or WorkerCapacitySampled)
        {
            await sp.GetRequiredService<CapacitySampler>().HandleAsync(envelope, ct);
        }

        await sp.GetRequiredService<WorkerStreams>().HandleAsync(envelope, ct);
    },
    sp.GetRequiredService<ILogger<ProcessAgentRunner>>(),
    sp.GetRequiredService<ILogger<RunAllowances>>(),
    builder.Configuration["Capacity:CgroupRoot"],
    builder.Configuration["Capacity:ProcRoot"],
    sp.GetRequiredService<WorkerStreams>().Deliver));
builder.Services.AddSingleton<IRunWorker>(sp =>
{
    var worker = sp.GetRequiredService<InProcessWorker>().Worker;
    sp.GetRequiredService<WorkerPool>().Connect(worker);
    return worker;
});
builder.Services.AddSingleton(sp => sp.GetRequiredService<InProcessWorker>().Memory!);
builder.Services.AddSingleton(sp => sp.GetRequiredService<InProcessWorker>().Allowances!);
}
else
{
    // CONTROL ALONE: no run worker in this process, so no launcher and no run's memory mechanism here.
    // Every run goes to the worker its member was placed on, each of which decides its own mechanism.
    builder.Services.AddSingleton<IRunWorker>(sp => new RunWorkers(
        sp.GetRequiredService<WorkerPool>(), member => sp.GetRequiredService<WipLedger>().PlacedOn(member)));
    builder.Services.AddSingleton(_ => RunMemoryLimits.NotAvailable(
        "this Host runs no member itself (--Role control): each worker limits its own runs and logs how when it starts"));
}
// Member TMPDIRs on the data volume, never a /tmp the engine may hold in memory (MemberTemp).
builder.Services.AddSingleton(_ => MemberTemp.RootUnder(dataRoot));
// BY FACTORY: the runner has a second constructor, which builds a worker of its own from parts, and
// production's is this one, over the shared worker.
builder.Services.AddSingleton(sp => new ProcessAgentRunner(
    sp.GetRequiredService<AgentCatalog>(),
    sp.GetRequiredService<IRunWorker>(),
    sp.GetRequiredService<RunDirectory>(),
    control ? null : sp.GetRequiredService<InProcessWorker>().Host.Launcher,
    () => ProcessAgentRunner.Allowance((tenantSettings.RunMemoryLimit(), tenantSettings.RunMemoryCeiling())),
    sp.GetRequiredService<MemberTempRoot>().Path,
    member => sp.GetRequiredService<WorkerPool>().Worker(sp.GetRequiredService<WipLedger>().PlacedOn(member)),
    control
        ? member => sp.GetRequiredService<WorkerPool>().For(sp.GetRequiredService<WipLedger>().PlacedOn(member)) is RemoteWorker placed
            ? ProcessAgentRunner.Allowance((
                tenantSettings.RunMemoryLimit(WorkerBounds.Of(placed.Info)), tenantSettings.RunMemoryCeiling(WorkerBounds.Of(placed.Info))))
            : null
        : null,
    // In control no worker in this process takes a run's share of its CLI's install, so the runner
    // takes it from this control's one gate, around every run on every worker. In all the Host's own
    // worker takes it from the same gate, as before, and the runner does not, so a run counts once.
    control ? sp.GetRequiredService<AgentUpdateGate>() : null,
    () => sp.GetRequiredService<IMemberReports>()));
builder.Services.AddSingleton<IAgentRunner>(sp => new CredentialUseRunner(
    sp.GetRequiredService<ProcessAgentRunner>(),
    sp.GetRequiredService<IPrincipalStore>(),
    sp.GetRequiredService<AgentCatalog>()));

// WHAT A MEMBER'S REPORT DOES - row, mark, card push, idle clock - written once, for the MCP routes
// and for a plugin member's stdout alike.
builder.Services.AddSingleton(sp => new MemberReports(
    sp.GetRequiredService<ContainerHost>(),
    sp.GetRequiredService<IMessageLog>(),
    sp.GetRequiredService<IRunWorker>(),
    sp.GetRequiredService<ILoggerFactory>(),
    sp.GetRequiredService<RunSecrets>()));
builder.Services.AddSingleton<IMemberReports>(sp => sp.GetRequiredService<MemberReports>());

// WHAT EVERY MEMBER RUNS THROUGH. The member runtime hands its work, as data, to this; the agent
// adapter turns it into the prompt and history an agent CLI has always been given, over the
// IAgentRunner stack above. Tests that substitute IAgentRunner keep working because this reads it.
//
// ROUTED PER INVOCATION: `plugin:<id>` to the plugin runner, anything else to the agent adapter. The
// choice is made here and nowhere in the pump.
builder.Services.AddSingleton(sp => new AgentMemberRunner(
    sp.GetRequiredService<IAgentRunner>(),
    sp.GetRequiredService<IContextBuilder>()));
builder.Services.AddSingleton(sp => new PluginMemberRunner(
    sp.GetRequiredService<PluginCatalog>(),
    sp.GetRequiredService<IMemberReports>(),
    sp.GetRequiredService<IRunWorker>(),
    sp.GetRequiredService<RunDirectory>(),
    sp.GetRequiredService<AgentLaunchUser>(),
    sp.GetRequiredService<IPluginMemberSettingsStore>(),
    sp.GetRequiredService<ISecretStore>(),
    sp.GetRequiredService<Connections>(),
    sp.GetRequiredService<SiteService>()));
builder.Services.AddSingleton<IMemberRunner>(sp => new MemberRunnerRouter(
    sp.GetRequiredService<AgentMemberRunner>(),
    sp.GetRequiredService<PluginMemberRunner>()));

// Constructed explicitly rather than by convention: the two artifact seams and the pending store are
// all OPTIONAL parameters, and a container silently built without them is a container that silently
// does not remember - and, without the third, one that silently loses accepted work on a restart.
// `onRegistered` is left at its default (null) DELIBERATELY, not merely omitted. `TeamRegistry`
// already calls `EffectiveSubscriptions.RecomputeAsync` itself, once per container, AFTER
// `SaveMemberAsync` has written the member's row - `RecomputeAsync` reads that row back, and a call
// any earlier finds none and computes an empty base set. Wiring this host's own seam to it as well
// would fire from INSIDE `AddAsync`, which `TeamRegistry.AddContainerAsync` always calls BEFORE that
// row exists - so every registration would persist an empty set first and the correct one a moment
// later: not a second production WRITER (`EffectiveSubscriptions` stays the only caller of
// `ISubscriptions.SetAsync`), just the same one called twice, the first time with the wrong answer.
// See `ContainerHost`'s constructor for the parameter this decision is about, and
// `EffectiveSubscriptions`'s own doc comment for why it also cannot be handed to this factory
// directly - `EffectiveSubscriptions` itself depends on `ContainerHost`.
// Per-workflow spend limit. A backstop against a runaway loop, not a budget anyone should feel.
// An agent that omits its causation starts a fresh chain at spend 0, exactly as it does at depth
// 0, so this bounds the ACCIDENT - the runaway that actually costs money.
//
// ASKED AT THE WAKE, not only at `tell`: a Manager woken by its members' completions never
// goes through `tell`, so a limit read there alone would let it spend unbounded. `ContainerHost`
// asks at the WAKE, which is where the cost is incurred, so it is declared before the
// registration that takes it.
//
// 0 MEANS UNLIMITED, and `ContainerHost` reads it that way - the same answer as absent, because a
// person who clears a setting and one who types 0 mean the same thing.
//
// THE DEFAULT (100M) IS A BACKSTOP, NOT A BUDGET. Real jobs run a few million tokens a round, and a
// lower figure would bound honest work from a place nobody can see. It sits above the offered team
// default (50M), so the number a person sets is the one that normally decides and this only
// catches something genuinely away.
//
// **SETTABLE.** `workflow.spendLimit` in the Tenant Settings dialog; this
// startup figure is only what a fixture or an unwired reader falls back to. The decision and its
// reason are recorded in the setting's own description in `TenantSettings`.
var workflowSpendLimit = tenantSettings.WorkflowSpendLimit;

// Instance-wide, shared across users. 0 is unlimited. Absent means the smaller of max(1, CPUs - 1)
// and, under a container memory limit, limit / wip.memoryPerRunMb (`TenantSettings.RunLimit`). The container's cgroup is a blast radius; this is
// the scheduler. Held work is listed by GET /api/wip.
//
// `wip.maxRunning`: settable at runtime. One place reads it - here, at start, and on every
// change through `WipLedger.SetMax`, which applies it under the ledger's lock and evicts nothing.
//
// AND MEASURED HEADROOM: a run the limit has room for still waits while memory in use or memory
// pressure is over its `admission.*` threshold, read through delegates from the capacity sampler's
// last measurement (`HeadroomGate`). Not measured falls back to the limit alone. The cgroup and proc
// roots are configuration so a test Host reads a fixture, never the machine it runs on.
//
// AND A WORKER: admission places each run on a worker with room, and the headroom it reads is that
// worker's, fed by its own capacity samples. In `all`, one: the Host's own (WorkerPool).
var headroom = new HeadroomGate(
    () => tenantSettings.AdmissionMemoryPercent, () => tenantSettings.AdmissionMemoryPressurePercent);
// In control the pool starts empty and fills as workers connect, each with a gate of its own and,
// under the default run limit, its own bound; the default limit is then the sum of their bounds.
// Workers are ranked by headroom less wip.memoryPerRunMb for each run placed since their last
// sample, so a burst spreads.
var workers = control
    ? new WorkerPool(
        _ => new HeadroomGate(() => tenantSettings.AdmissionMemoryPercent, () => tenantSettings.AdmissionMemoryPressurePercent),
        info => tenantSettings.WorkerBound(WorkerBounds.Of(info)),
        memoryPerRunMb: () => tenantSettings.WipMemoryPerRunMb)
    : new WorkerPool([(WorkerId.Local, headroom)]);
if (control)
{
    tenantSettings.Workers = () => [.. workers.Entries().Where(e => e.DroppedAt is null).Select(e => WorkerBounds.Of(e.Info))];
}

var wip = new WipLedger(tenantSettings.WipMaxRunning, workers, admissionHoldWriter);
workers.Placed = wip.PlacedCount;
builder.Services.AddSingleton(headroom);
builder.Services.AddSingleton(workers);

// The `heavy` lease's holders and queue, read from the instance's leases on every sample; the
// capacity sample and its push carry them.
builder.Services.AddSingleton<IHeavyLeaseView>(sp => new HeavyLeaseFromLeases(sp.GetRequiredService<ILeaseState>()));
// The worker measures - its cgroup and each run's process group, on SampleCapacity - and this
// sampler composes the sample from what it says.
builder.Services.AddSingleton(sp => new CapacitySampler(
    control
        ? () => [.. workers.Entries().Where(e => e.Worker is not null && e.DroppedAt is null).Select(e => (e.Worker!, e.Gate))]
        : () => [(sp.GetRequiredService<IRunWorker>(), workers.Gate(WorkerId.Local))],
    wip,
    sp.GetRequiredService<IHeavyLeaseView>(),
    () => tenantSettings.AdmissionMemoryPercent,
    () => tenantSettings.AdmissionMemoryPressurePercent,
    push: sample => sp.GetRequiredService<IHubContext<ContainerHub>>()
        .Clients.Group(ContainerHub.PeopleGroup).SendAsync(CapacityEndpoints.PushName, sample),
    logger: sp.GetRequiredService<ILogger<CapacitySampler>>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<CapacitySampler>());
tenantSettings.Changed += name =>
{
    // The per-run allowance moves the default, so a change to it re-reads the limit too.
    if (name is TenantSettings.WipMaxRunningName or TenantSettings.WipMemoryPerRunMbName)
    {
        wip.SetMax(tenantSettings.WipMaxRunning);
    }
};
builder.Services.AddSingleton(wip);

builder.Services.AddSingleton(sp => new ContainerHost(
    sp.GetRequiredService<IMessageLog>(),
    sp.GetRequiredService<ICursors>(),
    sp.GetRequiredService<ISubscriptions>(),
    sp.GetRequiredService<ITranscriptStore>(),
    sp.GetRequiredService<IPendingDeliveries>(),
    sp.GetRequiredService<ITriggerStore>(),

    // WIRED HERE OR THE BOUND DOES NOTHING. The seam is an optional parameter, so an unwired host
    // is a host that never refuses a wake - and nothing would fail. `WorkflowBudgetAtTheWakeTests`
    // is the only thing that would notice, which is the same argument that keeps
    // `CredentialUseRunner`'s decorator wired.
    workflowSpendLimit: workflowSpendLimit,
    workflowSpendLimitNow: () => tenantSettings.WorkflowSpendLimit,

    // THE FIGURE ACTUALLY IN FORCE FOR A TEAM, resolved per wake. Late-bound through the
    // provider rather than captured: `TeamRegistry` is registered BELOW this line and depends on
    // things registered below it again, so a captured instance would be a circular construction.
    // `sp` is the root provider and the registry is a singleton, so this resolves the same object
    // the routes write through - which is what makes a figure changed on a screen take effect on
    // the very next wake.
    //
    // WIRED HERE OR THE TEAM'S NUMBER DOES NOTHING. The seam is optional, so an unwired host
    // quietly falls back to `workflowSpendLimit` above and every team setting is ignored - and
    // nothing fails. The same argument that keeps the line above it wired.
    effectiveWorkflowBudget: (team, _) =>
        ValueTask.FromResult(sp.GetRequiredService<TeamRegistry>().EffectiveWorkflowBudgetFor(team)),

    wip: wip,

    // A MEMBER'S TREE FOR THE CARD IT WAS WOKEN FOR, per invocation, as `HARNESS_WORKTREE`
    // and in the instruction text. Late-bound through the provider for the reason above, and read
    // live so a repository added to a team after its members were created is named on the next wake.
    worktrees: (member, key) => sp.GetRequiredService<TeamRegistry>().WorktreesFor(member, key),

    // AN EVENT OR FOLDER TRIGGER HELD, asked by the pump before it fires one: its team is archived
    // (TeamArchive), its solution team waits for a missing required input (SolutionWait), or its
    // daily token cap is reached. Late-bound for the reason above: TriggerCost's tenant log is
    // registered below this line.
    triggerHeld: async (trigger, member, cause, ct) =>
        await sp.GetRequiredService<TeamArchive>().SkipIfArchivedAsync(trigger, member, DateTimeOffset.UtcNow, cause.Seq, ct)
        || await sp.GetRequiredService<SolutionWait>().SkipIfWaitingAsync(trigger, member, DateTimeOffset.UtcNow, cause.Seq, ct)
        || await sp.GetRequiredService<TriggerCost>()
            .SkipIfCappedAsync(trigger, member, DateTimeOffset.UtcNow, nextDueAt: null, cause.Seq, ct),

    // Whether a member's card shows the eye: its preset has a live view this Host can read.
    // Asked per snapshot, so a catalog edit or a repoint shows on the next one.
    watchable: agent => LiveView.Watchable(
        sp.GetRequiredService<AgentCatalog>().Definition(agent)?.LiveView,
        sp.GetRequiredService<AgentLaunchUser>()),

    // THE PLATFORM PUBLISHES A TEAM'S BRANCHES WHEN A RUN ENDS, NOT ONLY WHEN A CARD IS ACCEPTED.
    //
    // The acceptance hook below (`workflow-complete`) is the first place a team's work is
    // published and it is necessary; it is not sufficient, because a run KILLED between doing the work and finishing
    // its turn never declares anything and never reaches that route. This is the second call site
    // of the SAME publisher, on the event the platform observes whatever the agent managed to do -
    // see `TerminalPublish`, which also states the bound a dying container waits under.
    //
    // Late-bound through the provider, because `TeamRegistry` is
    // registered below this line, so a captured instance would be a circular construction.
    //
    // THE IDLE-WORKFLOW OFFER ALSO HANGS HERE, AND SECOND. `TerminalPublish` puts the team's branches on origin; the
    // offer may WAKE the Manager under this same workflow, and a Manager woken to declare a
    // workflow whose branches have not been pushed yet would be racing the push it is about to
    // accept - the same ordering argument `workflow-complete` makes for publishing before it
    // appends. See `IdleWorkflowOffer` for what it decides and every reason it decides not to.
    // THE PER-RUN FOREIGN TOOLS CHECK, on the row that carries the run, inside the run: the
    // transcript the agent wrote is read for tools the platform did not give it. Late-bound for
    // the reason above. See ForeignToolsCheck.
    onTerminal: async (terminal, ct) =>
        await sp.GetRequiredService<ForeignToolsCheck>().CheckAsync(terminal, ct),

    // AN ARCHIVED TEAM, asked by the pump for a paused team: its trigger-only events are skipped
    // (TeamArchive, through triggerHeld above) rather than left to fire at Resume. Late-bound for the
    // reason above.
    archived: team => sp.GetRequiredService<TeamRegistry>().IsArchived(team),

    onRunEnding: (member, causation, succeeded, ct) => RunEnding.RunAsync(
        sp.GetRequiredService<ILogger<ContainerHost>>(), member, causation, succeeded, ct,

        // NO LEASE OUTLIVES ITS RUN. First, because a run waiting on a push should not keep another
        // team's heavy work waiting too. Every way a run ends reaches this hook - completed, failed,
        // stopped, a runner that threw - and a Host that goes down holds no lease when it comes
        // back: they live in memory. Each step is guarded by RunEnding, so a release that throws
        // (a worker dropped as its run ended) never skips the publish below.
        releaseLeases: () => sp.GetRequiredService<LeaseActions>().EndedAsync(LeaseOwner.For(member), ct),

        // THE PUBLISH IGNORES `succeeded` AND THAT IS THE WHOLE POINT: a run killed between
        // doing the work and finishing its turn is the case this exists for, so its branch
        // matters MORE than a clean run's, not less.
        publish: () => TerminalPublish.OnRunEndingAsync(
            sp.GetRequiredService<TeamRegistry>(),
            sp.GetRequiredService<ITeamPublisher>(),
            member,
            causation,
            ct,

            // THE ONLY ARGUMENT PRODUCTION PASSES BEYOND THE REQUIRED ONES, and it is what
            // stops the bound below giving the ordering away on a busy machine: `TerminalPublish`
            // waits while git is still starting and finishing operations, and gives up when it has
            // gone quiet. Without this the bound is elapsed time, and elapsed time cannot tell a
            // slow publish from a hung origin - a HEALTHY push of a dozen branches to a LOCAL
            // origin can take well over ten seconds on a loaded host. See `TerminalPublish.Budget`.
            activity: () => sp.GetRequiredService<GitRunner>().Activity),

        // A MANAGER THAT MOVED ITS CLONE'S DEFAULT BRANCH IS REPORTED, BEFORE THE TERMINAL ROW, so
        // the report lands on its card and in the feed. Reported only; nothing is reset. See
        // `DefaultBranchMove`.
        defaultBranch: () => DefaultBranchMove.OnRunEndingAsync(
            sp.GetRequiredService<TeamRegistry>(),
            sp.GetRequiredService<TeamPaths>(),
            sp.GetRequiredService<GitRunner>(),
            sp.GetRequiredService<IMessageLog>(),
            member,
            causation,
            ct),

        idleOffer: reads => sp.GetRequiredService<IdleWorkflowOffer>()
            .OnRunEndingAsync(member, causation, succeeded, reads, ct),

        // A WORKFLOW ITS OWNER CANNOT DECLARE - a person told a plugin member directly - is
        // declared by the platform when nothing is left working it. The offer above goes only to
        // an owner that CAN declare, so at most one of the two acts. See `UndeclarableWorkflows`.
        //
        // ITS ANSWER IS THE HOOK'S: the run's terminal row says the workflow was declared, and the
        // pump passes over the Manager on it (PayloadFields.WorkflowDeclared).
        //
        // AND A WORKFLOW ALREADY DECLARED WHEN THE RUN ENDS SAYS SO THE SAME WAY - the owner of a
        // member-owned workflow declaring it in this run, typically on the idle offer. The offer went
        // to the owner only; waking the Manager on the run that answered it would hand it the same
        // decision, already made. Never on the Manager's own run, which wakes no Manager anyway; the
        // runtime keeps the key off a row closing a member's own `tell`.
        declare: async reads => await sp.GetRequiredService<UndeclarableWorkflows>()
                .OnRunEndingAsync(member, causation, succeeded, reads, ct)
            || (!member.Equals(new ContainerId(member.Team, TeamRegistry.DefaultManagerName))
                && await WorkflowDeclaration.DeclaredAsync(sp.GetRequiredService<IMessageLog>(), causation, ct)))));
// A container's credential and its environment, in one place - see AgentEnvironment for why those
// two belong together rather than either side of the registry.
// Registered ABOVE AgentEnvironment, which depends on it: TeamDocuments is the one place that
// decides where a team's documents live, and a container is told that path as HARNESS_SHARED.
// Composing the path a second time from dataRoot would be two stores of one fact.
builder.Services.AddSingleton(new TeamDocuments(teamPaths));

// WHERE A MEMBER CALLS BACK. Derived from this host's own binding rather than a literal port -
// see MemberBaseAddress for why a literal port breaks every other instance's members, and why the
// answer is the dev server's: derive it, and print what was chosen.
var memberBaseAddress = MemberBaseAddress.Resolve(
    builder.Configuration["MemberBaseAddress"],
    builder.Configuration["urls"] ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS"));

// The MCP tools call the platform's own routes back on the address members are told, never on
// the request's Host header: the caller writes that header.
builder.Services.AddHttpClient(
    PlatformMcpTools.ClientName, client => client.BaseAddress = new Uri(memberBaseAddress));

builder.Services.AddSingleton(sp => new AgentEnvironment(
    sp.GetRequiredService<IPrincipalStore>(),
    sp.GetRequiredService<AgentCatalog>(),
    memberBaseAddress,
    sp.GetRequiredService<TeamDocuments>()));

// THE ONLY WRITER OF `subscriptions` - see EffectiveSubscriptions' own doc comment. Registered
// before TeamRegistry, which is its first caller, and before MemberDeletion/TeamDeletion, its other
// two.
builder.Services.AddSingleton(sp => new EffectiveSubscriptions(
    sp.GetRequiredService<ITeamStore>(),
    sp.GetRequiredService<ITriggerStore>(),
    sp.GetRequiredService<ISubscriptions>(),
    sp.GetRequiredService<ContainerHost>()));

builder.Services.AddSingleton(sp => new TeamRegistry(
    sp.GetRequiredService<ContainerHost>(),
    sp.GetRequiredService<AgentCatalog>(),
    sp.GetRequiredService<IMemberRunner>(),
    sp.GetRequiredService<TeamPaths>(),
    sp.GetRequiredService<ITeamStore>(),
    sp.GetRequiredService<AgentEnvironment>(),
    sp.GetRequiredService<FileBrowserPolicy>(),
    sp.GetRequiredService<IMessageLog>(),
    sp.GetRequiredService<EffectiveSubscriptions>(),
    sp.GetRequiredService<IRepoClone>(),

    // THE INSTANCE FIGURE, because `EffectiveWorkflowBudgetFor` is the one function allowed to
    // know that a team which has chosen nothing falls back to it. The same variable the
    // ContainerHost registration above and `IdleWorkflowOffer` below are handed - one source read
    // three times, never three sources.
    workflowSpendLimit,
    workflowSpendLimitNow: () => tenantSettings.WorkflowSpendLimit,
    skillDirectory: sp.GetRequiredService<SkillDirectory>(),
    // Resolved per call: the git runner reads the registry, so it cannot be built first.
    prepareClone: (contributor, clonePath, ct) =>
        sp.GetRequiredService<ContributorClone>().ApplyAsync(clonePath, contributor, ct),
    plugins: sp.GetRequiredService<PluginCatalog>(),
    pluginSettings: sp.GetRequiredService<IPluginMemberSettingsStore>(),
    secrets: sp.GetRequiredService<ISecretStore>(),
    removal: sp.GetRequiredService<FolderRemoval>(),
    localRepos: sp.GetRequiredService<LocalRepos>(),
    solutions: sp.GetRequiredService<ITeamSolutions>()));

// THE INSTANCE'S LOCAL REPOSITORIES: bare, under <dataRoot>/repos, the Host's and read-only to the
// agent. A team names one as `local:<name>`; see LocalRepos.
builder.Services.AddSingleton(sp => new LocalRepos(
    dataRoot, sp.GetRequiredService<GitRunner>(), sp.GetRequiredService<FolderRemoval>()));

// The one delete of a local repository: Admin -> Repositories' and a team deletion's.
builder.Services.AddSingleton(sp => new LocalRepoDeletion(
    sp.GetRequiredService<LocalRepos>(), sp.GetRequiredService<TeamRegistry>(), sp.GetRequiredService<ITenantLog>()));

// FORGIVING TEAM REPOSITORIES (B001F): a team's default local repository, and the `ls-remote` check
// with its choices before a URL is used. The registry is resolved per call: it is built after this.
builder.Services.AddSingleton<IRemoteRepoCheck>(sp => new RemoteRepoCheck(sp.GetRequiredService<GitRunner>()));
builder.Services.AddSingleton(sp => new TeamRepoSetup(
    sp.GetRequiredService<LocalRepos>(),
    () => sp.GetRequiredService<TeamRegistry>(),
    sp.GetRequiredService<IRemoteRepoCheck>(),
    sp.GetRequiredService<IGitHubContributor>()));
builder.Services.AddSingleton<LocalRepoOnNeed>();

// The instance's git identity (GIT_AUTHOR_NAME / GIT_AUTHOR_EMAIL, set with the operator CLI's `secret set`)
// and the one place a clone is brought in line with its contributor settings.
builder.Services.AddSingleton(new InstanceGitIdentity(builder.Configuration));
builder.Services.AddSingleton(sp => new ContributorClone(
    sp.GetRequiredService<GitRunner>(), sp.GetRequiredService<InstanceGitIdentity>()));

// THE PLATFORM MAKES A TEAM'S MAIN CLONE. Wired here rather than defaulted inside TeamRegistry,
// because a seam that can be omitted is one something quietly omits - and the omission here looks
// like a team with a repository and nothing at its path.
//
// A TEAM CREATE FINISHES WHEN ITS CALLER GOES AWAY: the create, its clone and a dispatch into it run
// on the Host's lifetime rather than the request's. See DetachedWork.
builder.Services.AddSingleton<DetachedWork>();

builder.Services.AddSingleton<IRepoClone>(sp => new RepoClone(
    sp.GetRequiredService<GitRunner>(), localRepos: sp.GetRequiredService<LocalRepos>(),
    removal: sp.GetRequiredService<FolderRemoval>()));

// THE PLATFORM PUBLISHES A TEAM'S WORK TO ORIGIN, on the acceptance of a card, BEFORE the row that
// accepts it. Wired here for the reason `IRepoClone` above it is - a seam that can be omitted is one
// something quietly omits, and left to agents alone most teams' work would exist only under a team
// root. See `ITeamPublisher`.
//
// NO `enabled` FLAG BESIDE IT, deliberately: with cloning off there is no clone to push from, so the
// cost this would switch off is a `Directory.Exists` that answers false.
builder.Services.AddSingleton<ITeamPublisher>(sp => new TeamPublisher(
    sp.GetRequiredService<TeamPaths>(),
    sp.GetRequiredService<GitRunner>(),
    sp.GetRequiredService<IMessageLog>(),
    // Resolved per call, not at construction, so the publisher does not need the registry
    // to exist before it does.
    (team, repo) => sp.GetRequiredService<TeamRegistry>().DefaultBranchFor(team, repo).Branch,
    // THE TIP A DISPATCH RECORDS, so `landed` outlives the branch and the team.
    (team, repo, sha, causation, ct) =>
        sp.GetRequiredService<BacklogTipRecorder>().RecordTipAsync(team, repo, sha, causation, ct),
    // A DISPATCH'S START NOT RECORDED AT DISPATCH, tried again before the push and its tip.
    (team, causation, ct) => sp.GetRequiredService<BacklogTipRecorder>().RetryForPublishAsync(team, causation, ct)));

builder.Services.AddSingleton(sp => new BacklogTipRecorder(
    sp.GetRequiredService<IBacklogStore>(),
    sp.GetRequiredService<IMessageLog>(),
    sp.GetRequiredService<TeamRegistry>(),
    sp.GetRequiredService<TeamPaths>(),
    sp.GetRequiredService<GitRunner>(),
    // READ ON EVERY RECORDING, so a changed appsettings value applies to the next dispatch.
    () => builder.Configuration.GetValue<TimeSpan?>(BacklogTipRecorder.FetchBudgetKey) is { } budget && budget > TimeSpan.Zero
        ? budget
        : BacklogTipRecorder.DefaultFetchBudget));

builder.Services.AddSingleton(sp => new TeamAccess(
    sp.GetRequiredService<IUserStore>(),
    sp.GetRequiredService<IPrincipalStore>(),
    sp.GetRequiredService<TeamRegistry>()));

builder.Services.AddSingleton<TeamListPush>();

// THE ONE REMOVAL a team root, a member's workspace and a reset's folders go through: the marker
// last, agent content removed as the agent through its launch prefix, and whatever remains recorded
// to be retried. See FolderRemoval.
// The agent's pass goes to a connected worker; in control always, in all only where agents run as
// another user, as before.
builder.Services.AddSingleton(sp => new FolderRemoval(
    sp.GetRequiredService<AgentLaunchUser>(),
    sp.GetRequiredService<IUnfinishedRemovals>(),
    agent: new WorkerAgentPass(sp.GetRequiredService<WorkerAsks>(), always: control)));

// IMAGES A PERSON GIVES THEIR CONCIERGE. The cap and the retention are configuration rather than
// tenant settings: they bound one route and one sweep, and nothing reads them while running.
builder.Services.AddSingleton(sp => new ConciergeAttachments(
    sp.GetRequiredService<TeamPaths>(),
    sp.GetRequiredService<AgentLaunchUser>(),
    sp.GetRequiredService<FolderRemoval>(),
    long.TryParse(builder.Configuration["ConciergeAttachmentMaxBytes"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var attachmentMax)
        && attachmentMax > 0
        ? attachmentMax
        : ConciergeAttachments.DefaultMaxBytes,
    TimeSpan.TryParse(builder.Configuration["ConciergeAttachmentRetention"], CultureInfo.InvariantCulture, out var attachmentRetention)
        && attachmentRetention > TimeSpan.Zero
        ? attachmentRetention
        : ConciergeAttachments.DefaultRetention));

builder.Services.AddSingleton(sp => new UnfinishedRemovalRetry(
    sp.GetRequiredService<FolderRemoval>(),
    sp.GetRequiredService<IUnfinishedRemovals>(),
    sp.GetRequiredService<TeamRegistry>(),
    sp.GetRequiredService<ContainerHost>(),
    sp.GetRequiredService<TeamPaths>(),
    sp.GetRequiredService<LocalRepos>()));

// Constructed BY HAND, like ContainerHost, and for the same reason: every dependency here is one a
// deletion would silently skip if it were optional. A TeamDeletion missing its pending-delivery
// store deletes a team and leaves rows the next team of that name inherits.
builder.Services.AddSingleton(sp => new TeamDeletion(
    sp.GetRequiredService<TeamRegistry>(),
    sp.GetRequiredService<ContainerHost>(),
    sp.GetRequiredService<ITeamStore>(),
    sp.GetRequiredService<ICursors>(),
    sp.GetRequiredService<ISubscriptions>(),
    sp.GetRequiredService<EffectiveSubscriptions>(),
    sp.GetRequiredService<IPendingDeliveries>(),
    sp.GetRequiredService<ITriggerStore>(),
    sp.GetRequiredService<IPrincipalStore>(),
    sp.GetRequiredService<TeamPaths>(),
    sp.GetRequiredService<GitRunner>(),
    sp.GetRequiredService<FolderRemoval>(),
    sp.GetRequiredService<SiteService>(),
    sp.GetRequiredService<TeamSkills>(),
    sp.GetRequiredService<ITeamSolutionStore>(),
    wakes: sp.GetRequiredService<IWakeStore>(),
    fileChanges: sp.GetRequiredService<WorkspaceFileChangeStore>(),
    runSteps: sp.GetRequiredService<AgentStepStore>()));

// By hand for the reason TeamDeletion is: every dependency here is one a reset would silently skip
// if it were optional. A TeamReset missing its pending-delivery store cannot tell a member that has
// accepted work from an idle one, and would floor a member out from under work it was promised.
builder.Services.AddSingleton(sp => new TeamReset(
    sp.GetRequiredService<TeamRegistry>(),
    sp.GetRequiredService<ContainerHost>(),
    sp.GetRequiredService<ITeamStore>(),
    sp.GetRequiredService<ICursors>(),
    sp.GetRequiredService<IPendingDeliveries>(),
    sp.GetRequiredService<IMessageLog>(),
    sp.GetRequiredService<TeamPaths>(),
    sp.GetRequiredService<FolderRemoval>(),
    sp.GetRequiredService<RepositoryReset>(),
    sp.GetRequiredService<WorkspaceFileChangeStore>(),
    sp.GetRequiredService<AgentStepStore>(),
    purged: () =>
    {
        sp.GetRequiredService<KanbanStore>().Invalidate();
        sp.GetRequiredService<RollupCache>().Invalidate();
        sp.GetRequiredService<WorkflowEndings>().Invalidate();
    }));

// Reset repositories: trees through the one WorktreeRemoval, git through the one GitRunner (as the
// agent, and into a local origin as the Host), kept branches named on the log.
builder.Services.AddSingleton(sp => new RepositoryReset(
    sp.GetRequiredService<GitRunner>(),
    sp.GetRequiredService<WorktreeRemoval>(),
    sp.GetRequiredService<TeamPaths>(),
    sp.GetRequiredService<IMessageLog>()));

// By hand for the reason TeamDeletion is: every dependency here is one a deletion would silently
// skip if it were optional, and a MemberDeletion missing its pending-delivery store leaves rows the
// NEXT member of that name inherits - on a team that is still running, so there is no "the team is
// gone" to explain the failure away with.
builder.Services.AddSingleton(sp => new MemberDeletion(
    sp.GetRequiredService<TeamRegistry>(),
    sp.GetRequiredService<ContainerHost>(),
    sp.GetRequiredService<ICursors>(),
    sp.GetRequiredService<EffectiveSubscriptions>(),
    sp.GetRequiredService<IPendingDeliveries>(),
    sp.GetRequiredService<ITriggerStore>(),
    sp.GetRequiredService<IPrincipalStore>(),
    sp.GetRequiredService<TeamPaths>(),
    sp.GetRequiredService<IMessageLog>(),
    builder.Configuration["GitExecutable"] ?? "git",
    sp.GetRequiredService<AgentLaunchUser>(),
    sp.GetRequiredService<FolderRemoval>()));

// Git runner for repository operations: as the agent, and GH_TOKEN only for a team the
// registry says has a GitHub remote - see GitRunner's class comment.
builder.Services.AddSingleton(sp => new GitRunner(
    builder.Configuration["GitExecutable"] ?? "git",
    runAs: sp.GetRequiredService<AgentLaunchUser>(),
    remotesFor: clonePath =>
    {
        var registry = sp.GetRequiredService<TeamRegistry>();
        var paths = sp.GetRequiredService<TeamPaths>();
        var clone = Path.GetFullPath(clonePath);
        foreach (var team in registry.All())
        {
            try
            {
                var repos = Path.GetFullPath(paths.ReposFor(team.Id)) + Path.DirectorySeparatorChar;
                if (clone.StartsWith(repos, StringComparison.Ordinal)) return registry.RemotesFor(team.Id);
            }
            catch (KeyNotFoundException)
            {
            }
        }

        return [];
    },
    // A clone whose origin is, by the registry, a local repository: its pushes go into the bare
    // repository as the Host, and GH_TOKEN is never handed to it.
    localOriginFor: clonePath =>
    {
        var registry = sp.GetRequiredService<TeamRegistry>();
        var paths = sp.GetRequiredService<TeamPaths>();
        var clone = Path.GetFullPath(clonePath);
        foreach (var team in registry.All())
        {
            try
            {
                var repos = Path.GetFullPath(paths.ReposFor(team.Id)) + Path.DirectorySeparatorChar;
                if (!clone.StartsWith(repos, StringComparison.Ordinal)) continue;

                var folder = clone[repos.Length..].Split(Path.DirectorySeparatorChar)[0];
                var url = registry.ReposFor(team.Id)
                    .FirstOrDefault(u => string.Equals(RepoUrls.DeriveName(u), folder, StringComparison.OrdinalIgnoreCase));
                return url is not null && LocalRepos.IsLocal(url)
                    ? sp.GetRequiredService<LocalRepos>().PathForReference(url)
                    : null;
            }
            catch (KeyNotFoundException)
            {
            }
        }

        return null;
    }));

// The one place a settled card's worktree is removed - never forced - shared by the
// workflow-completed handler, member deletion and the Git dialog's clean-up button.
builder.Services.AddSingleton(sp => new WorktreeRemoval(
    sp.GetRequiredService<GitRunner>(), sp.GetRequiredService<IMessageLog>()));

// The Backlog's `landed` derivation shells out to git, and the list is re-read on every
// visit to the screen - so one measurement per team is shared for a short lifetime rather than
// re-run per request. A SINGLETON because that is the only scope a cache can usefully have here.
builder.Services.AddSingleton(new BacklogLandedCache());

// What contributor mode asks GitHub - fork, find, open and read a pull request - through gh
// with the instance's GH_TOKEN, as the agent user, and only from a person's button. Tests replace
// it with a fake: no automated test calls GitHub. The reader asks at most once a minute per pull request.
builder.Services.AddSingleton<IGitHubContributor>(sp => new GhContributor(sp.GetRequiredService<AgentLaunchUser>()));
builder.Services.AddSingleton(sp => new PullRequestStateReader(
    sp.GetRequiredService<IGitHubContributor>(), sp.GetRequiredService<TeamRegistry>()));

// A PERSON'S TERMINAL RUNS ON A WORKER, in both roles: control relays it (WorkerPtyEngine), and in one
// process the Host's own worker spawns it over the in-process transport.
builder.Services.AddSingleton(sp => new WorkerPtyEngine(
    WorkersOf(sp), sp.GetRequiredService<WorkerStreams>(),
    log: sp.GetRequiredService<ILogger<WorkerPtyEngine>>()));
builder.Services.AddSingleton<IPtyEngine>(sp => sp.GetRequiredService<WorkerPtyEngine>());
builder.Services.AddSingleton<SteeringWorkflows>();
builder.Services.AddSingleton<ConciergeLaunches>();
builder.Services.AddSingleton(sp => new ConciergeLaunchFactory(
    sp.GetRequiredService<TeamPaths>(),
    memberBaseAddress,
    sp.GetRequiredService<IPrincipalStore>(),
    sp.GetRequiredService<AgentCatalog>(),
    sp.GetRequiredService<SkillDirectory>(),
    // Who the agent runs as is the worker's to decide and refuse; control alone has no agent child.
    control ? null : sp.GetRequiredService<AgentLaunchUser>(),
    sp.GetRequiredService<AgentUpdateGate>()));
builder.Services.AddSingleton(sp =>
{
    // Resolved ONCE, here, and captured - never re-resolved inside the delegates. The revoke runs
    // from ConciergeSessionStore.DisposeAsync during host shutdown, by which time the service
    // provider is already disposed, so a `sp.GetRequiredService` in there throws
    // ObjectDisposedException out of disposal and takes every live session's cleanup with it.
    var launcher = sp.GetRequiredService<ConciergeLaunchFactory>();
    var leaseActions = sp.GetRequiredService<LeaseActions>();
    var launches = sp.GetRequiredService<ConciergeLaunches>();

    return new ConciergeSessionStore(
        sp.GetRequiredService<IPtyEngine>(),
        launches.LaunchAsync,
        // A Concierge's run is its session: a lease it took ends with it, through the same path
        // as a run's end, so a member the queue hands it to is un-paused and its card told.
        // And the images the person attached go with it, however it ended.
        sp.GetRequiredService<ConciergeAttachments>().RemovingOnEnd(
            leaseActions.Releasing((key, ct) => launcher.RevokeAsync(key.User, ct))),
        sp.GetRequiredService<TimeProvider>(),
        launches.FloorOf,
        sp.GetRequiredService<ILogger<ConciergeSessionStore>>());
});

// HOW DEEP ONE CHAIN OF CAUSATION MAY GO before the platform stops it.
//
// `depth` is computed and stored on every message, and this is what reads it. Team scoping stops
// two teams' managers waking each other, and does nothing whatever about a manager and one worker
// ping-ponging INSIDE one team. `Ceiling` does not help - it bounds a queue's depth, which is how
// much work is waiting, not how far from a human's instruction that work has drifted.
//
// The bound is on CAUSATION rather than on message count on purpose: a manager fanning out to six
// workers at once is fine and is depth 1, while a manager and a worker answering each other twenty
// times is the runaway, and only depth tells them apart.
//
// 25 is generous rather than tuned. A real job - console instructs manager, manager dispatches to a
// developer, developer completes, manager dispatches verification, tester completes, manager reports
// - is six or seven, so this is roughly four times the deepest thing anyone has run. It is a
// backstop against a loop, not a budget anyone should feel.
//
// IT IS EVADABLE, and that is worth stating rather than discovering. An agent that omits its
// causation starts a fresh chain at depth 0, and nothing here can tell that from a person opening a
// new job. The `tell` tool always sends it, so this bounds the ACCIDENT - the manager that keeps answering
// itself - which is the failure that actually costs money. It is not a defence against an agent
// deliberately covering its tracks, and a tighter one would need a per-invocation credential.
//
// `causation.depthLimit`: read on every tell from `tenantSettings`, 0 meaning no limit.

// How long a console may sit unattended AND inactive before it is ended, and how often that is checked.
//
// Configurable for deployment AND for a demonstration: the default is far too long to sit through,
// so setting ConciergeIdleTimeout to something like 00:02:00 is how a person watches this work.
// `concierge.idleTimeout`, read at every sweep.
var consoleIdle = tenantSettings.ConciergeIdleTimeout;

// The sweep follows the window rather than being fixed. A one-minute sweep against a two-minute
// window would make the window mean anything up to three minutes, which is exactly the kind of
// vagueness that makes someone conclude the feature does not work.
var consoleSweep = consoleIdle < TimeSpan.FromMinutes(4)
    ? TimeSpan.FromSeconds(15)
    : TimeSpan.FromMinutes(1);

var scheduleSweepInterval = TimeSpan.TryParse(builder.Configuration["ScheduleSweepInterval"], out var configuredSweep)
    ? configuredSweep
    : TimeSpan.FromSeconds(15);
var scheduleRunnerEnabled = !bool.TryParse(builder.Configuration["ScheduleRunnerEnabled"], out var configuredRunner)
    || configuredRunner;
var quietSweepInterval = TimeSpan.TryParse(builder.Configuration["QuietSweepInterval"], out var configuredQuietSweep)
    ? configuredQuietSweep
    : TimeSpan.FromMinutes(1);
var quietSweepEnabled = !bool.TryParse(builder.Configuration["QuietSweepEnabled"], out var configuredQuietSweepEnabled)
    || configuredQuietSweepEnabled;

// THE CLOCK IN FRONT OF THE NUDGE. See `ResumeSweep` for the whole argument; these three
// settings are the parts an operator is allowed to move.
//
// THE INTERVAL IS THE ACCURACY and a minute is chosen for the same reason the quiet sweep's is: a
// resume is late by up to one tick and never early, and a provider's reset is a wall-clock minute
// rather than a second. `ResumeMaxAutomatic` is the BOUND, and it is configurable DOWNWARDS as much
// as up - setting it to 0 turns automatic resumes off entirely without removing the class, the
// card, or a person's own Nudge.
var resumeSweepInterval = TimeSpan.TryParse(builder.Configuration["ResumeSweepInterval"], out var configuredResumeSweep)
    ? configuredResumeSweep
    : TimeSpan.FromMinutes(1);
var resumeSweepEnabled = !bool.TryParse(builder.Configuration["ResumeSweepEnabled"], out var configuredResumeEnabled)
    || configuredResumeEnabled;
// The resume bound is the tenant setting `resume.maxAutomatic`, read at every sweep.

// Registered even when the runner is gated off. Every schedule write signals it, and a route that
// had to know whether a background service exists would be a second thing to keep in step.
builder.Services.AddSingleton<TriggerWakeSignal>();

// The doctor's record of the running Concierge sessions, written once the app is built: after each
// sweep, and when a session starts or ends.
Action? recordConciergeSessions = null;
builder.Services.AddSingleton<IHostedService>(sp =>
{
    var accounts = sp.GetRequiredService<IUserStore>();
    return new ConciergeReaper(
        sp.GetRequiredService<ConciergeSessionStore>(),
        () => tenantSettings.ConciergeIdleTimeout,
        consoleSweep,
        sp.GetRequiredService<ILogger<ConciergeReaper>>(),
        sp.GetRequiredService<ITenantLog>(),
        async (user, ct) => (await accounts.FindByIdAsync(user, ct))?.Email,
        () => recordConciergeSessions?.Invoke());
});
builder.Services.AddSingleton<WorkflowEndings>();
builder.Services.AddSingleton<QuietTeamSweep>();
if (quietSweepEnabled)
{
    builder.Services.AddSingleton<IHostedService>(sp => new QuietTeamReaper(
        sp.GetRequiredService<QuietTeamSweep>(),
        () => tenantSettings.QuietWindow,
        quietSweepInterval,
        sp.GetRequiredService<ILogger<QuietTeamReaper>>(),
        ct => SqliteMaintenance.CheckpointAsync(database, ct)));
}

// Written by scripts/ensure-agent-clis.sh at each container start, read by Diagnostics.
builder.Services.AddSingleton(CliVersionHistory.In(dataRoot));

// A DAILY COPY OF THE DATABASE. `Backups:Directory` defaults to `<dataRoot>/backups`,
// which in the container is /data/backups, beside the pre-migration and --backup copies that
// its own retention never touches. `Backups:Keep` is how many daily copies survive (default 7). Each
// run also prunes the before-step copies in the same folder the migrator wrote them to, by
// `Backups:KeepBeforeStep` and `Backups:KeepBeforeStepDays` - all read once, above.
var backupsEnabled = !bool.TryParse(builder.Configuration["Backups:Enabled"], out var configuredBackups)
    || configuredBackups;
if (backupsEnabled)
{
    var dailyBackup = backupSettings.DailyBackupFor(database);
    builder.Services.AddSingleton(dailyBackup);
    builder.Services.AddSingleton<IHostedService>(sp => new DailyBackupRunner(
        dailyBackup,
        TimeSpan.FromHours(1),
        sp.GetRequiredService<ILogger<DailyBackupRunner>>()));
}
// THE MESSAGE LOG KEEPS `log.retentionDays` OF FINISHED WORK: a pass shortly after start and then
// daily removes the rest through the log's one delete, never on a request's path. See LogRetention.
builder.Services.AddSingleton(sp => new LogRetention(
    new SqliteLogRetention(database),
    sp.GetRequiredService<IMessageLog>(),
    sp.GetRequiredService<ITenantLog>(),
    () => tenantSettings.LogRetentionDays,
    sp.GetRequiredService<ILogger<LogRetention>>(),
    purged: () =>
    {
        sp.GetRequiredService<KanbanStore>().Invalidate();
        sp.GetRequiredService<RollupCache>().Invalidate();
        sp.GetRequiredService<WorkflowEndings>().Invalidate();
    }));
builder.Services.AddHostedService(sp => new LogRetentionLoop(
    sp.GetRequiredService<LogRetention>(),
    sp.GetRequiredService<ILogger<LogRetentionLoop>>()));
builder.Services.AddSingleton(sp => new ResumeSweep(
    sp.GetRequiredService<TeamRegistry>(),
    sp.GetRequiredService<ContainerHost>(),
    sp.GetRequiredService<IMessageLog>(),
    ResumeSweep.DefaultMaxAutomaticResumes,
    sp.GetRequiredService<ILogger<ResumeSweep>>(),
    () => tenantSettings.ResumeMaxAutomatic));

// THE THING THAT NOTICES A WORKFLOW NOBODY IS WORKING. See `IdleWorkflowOffer` for the
// whole argument.
//
// A SERVICE RATHER THAN A STATIC, ONLY BECAUSE OF THE TWO BOUNDS. `TerminalPublish` beside it is a
// static method taking its collaborators as arguments, and this would be too if it did not need
// `causationDepthLimit` and `workflowSpendLimit` - both of which are read from configuration, and
// one of which is declared BELOW the `ContainerHost` registration that ends up calling this. A
// singleton closes over them here, where both are in scope, and the run-ending hook resolves it
// through the provider exactly as it already resolves `TeamRegistry`.
builder.Services.AddSingleton(sp => new IdleWorkflowOffer(
    sp.GetRequiredService<TeamRegistry>(),
    sp.GetRequiredService<ContainerHost>(),
    sp.GetRequiredService<IMessageLog>(),
    sp.GetRequiredService<IPendingDeliveries>(),
    sp.GetRequiredService<ICursors>(),
    sp.GetRequiredService<ISubscriptions>(),
    () => tenantSettings.CausationDepthLimit,

    // NO `workflowSpendLimit` HERE. A raw configuration value would disagree with `ContainerHost`
    // about a configured 0 and could not see a team's own per-workflow figure, so both ask
    // `TeamRegistry.EffectiveWorkflowBudgetFor`, which is the one place that resolves it.
    sp.GetRequiredService<ILogger<IdleWorkflowOffer>>(),

    // A PENDING WAKE IS WORK IN THE WORKFLOW: nothing is offered while one waits to fire.
    sp.GetRequiredService<IWakeStore>()));
builder.Services.AddSingleton<UndeclarableWorkflows>();

// `wake_me`: A MEMBER WOKEN ONCE, LATER, IN THE WORKFLOW IT IS WORKING. Its own clock, so a test
// moves the wakes' time and nothing else's; the limits read through delegates at every ask.
builder.Services.AddSingleton(sp => new WakeClock(sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<WakeSignal>();
builder.Services.AddSingleton(sp => new WakeMe(
    sp.GetRequiredService<IWakeStore>(),
    sp.GetRequiredService<IMessageLog>(),
    sp.GetRequiredService<TeamRegistry>(),
    sp.GetRequiredService<ContainerHost>(),
    sp.GetRequiredService<WakeClock>(),
    sp.GetRequiredService<WakeSignal>(),
    () => tenantSettings.WakeMaxDelay,
    () => tenantSettings.CausationDepthLimit,
    sp.GetRequiredService<ILogger<WakeMe>>()));
builder.Services.AddHostedService<WakeRunner>();
if (resumeSweepEnabled)
{
    builder.Services.AddSingleton<IHostedService>(sp => new ResumeRunner(
        sp.GetRequiredService<ResumeSweep>(),
        resumeSweepInterval,
        sp.GetRequiredService<ILogger<ResumeRunner>>()));
}
// THE FOLDER-CHANGE TRIGGER. Polled by the TriggerSweep below and announced to by the
// Documents routes. Its areas are the team's documents and the file-browser roots marked allowWatch.
builder.Services.AddSingleton(sp => new FolderWatch(
    sp.GetRequiredService<ITriggerStore>(),
    sp.GetRequiredService<IMessageLog>(),
    sp.GetRequiredService<ContainerHost>(),
    sp.GetRequiredService<IPendingDeliveries>(),
    team => sp.GetRequiredService<TeamDocuments>().RootFor(team),
    fileBrowser.Roots,
    dataRoot,
    int.TryParse(builder.Configuration["FolderWatch:MaxWatches"], out var maxWatches) && maxWatches > 0
        ? maxWatches
        : FolderWatch.DefaultMaximumWatches,
    sp.GetRequiredService<ILogger<FolderWatch>>()));
builder.Services.AddSingleton<TriggerCost>();

// WHAT AN ARCHIVED TEAM IS REFUSED AND SKIPS, and whether a team is quiet enough to archive or delete.
builder.Services.AddSingleton<TeamArchive>();
builder.Services.AddSingleton<TriggerSweep>();

// THE ONE PATH A TRIGGER IS MADE BY: the Triggers dialog's route and a solution install.
builder.Services.AddSingleton<TriggerCreation>();
if (scheduleRunnerEnabled)
{
    builder.Services.AddSingleton<IHostedService>(sp => new TriggerRunner(
        sp.GetRequiredService<TriggerSweep>(),
        sp.GetRequiredService<ITriggerStore>(),
        sp.GetRequiredService<TriggerWakeSignal>(),

        // A HEARTBEAT rather than a tick. The runner sleeps until the next schedule is actually
        // due; this only bounds how long it will go without re-reading, for state that changed
        // without a signal reaching it. `ScheduleSweepInterval` does not decide accuracy.
        scheduleSweepInterval,
        sp.GetRequiredService<ILogger<TriggerRunner>>()));
}

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<PumpHeartbeat>();
builder.Services.AddSingleton<LoginThrottle>();
builder.Services.AddHostedService<PumpService>();
builder.Services.AddHostedService<KanbanChangePush>();

// THE MONITOR'S VIEW OF A RUNNING MEMBER'S FILES: read during every run, recorded for the replay,
// pushed only to the people watching. See WorkspaceFileWatch.
builder.Services.AddSingleton<WorkspaceFileWatchTiming>();
builder.Services.AddSingleton<IWorkspaceRuns>(sp => new HostWorkspaceRuns(sp.GetRequiredService<ContainerHost>()));
builder.Services.AddSingleton(sp => new WorkspaceFileWatch(
    sp.GetRequiredService<GitRunner>(),
    sp.GetRequiredService<TeamPaths>(),
    (change, ct) => sp.GetRequiredService<IHubContext<ContainerHub>>()
        .Clients.Group(WorkspaceFileWatch.Group).SendAsync(WorkspaceFileWatch.Message, change, ct),
    record: sp.GetRequiredService<WorkspaceFileChangeStore>(),
    logger: sp.GetRequiredService<ILogger<WorkspaceFileWatch>>()));
builder.Services.AddHostedService<WorkspaceFileWatchService>();

// WHAT EVERY AGENT RUN DID, step by step: its transcript followed on its worker from the run's start
// to its end whether or not anybody watches, each step recorded (kind, tool name, time) and pushed
// only to the people watching, as workspaceChanged is. Off every run's path. See AgentStepFollow.
builder.Services.AddSingleton(sp => new AgentStepFollow(
    sp.GetRequiredService<LiveRuns>(),
    member => sp.GetRequiredService<ContainerHost>().Find(member) is { CurrentCausation: { } delivery } runtime
        ? new AgentStepRun(delivery, runtime.Snapshot().CurrentCorrelation)
        : null,
    member => LiveViewEndpoints.Locate(sp.GetRequiredService<RunDirectory>(), WorkersOf(sp), member, out var placed) is { } why
        ? (null, why)
        : (placed, null),
    sp.GetRequiredService<WorkerStreams>(),
    sp.GetRequiredService<AgentStepStore>(),
    (push, ct) => sp.GetRequiredService<IHubContext<ContainerHub>>()
        .Clients.Group(WorkspaceFileWatch.Group).SendAsync(AgentStepFollow.Message, push, ct),
    logger: sp.GetRequiredService<ILogger<AgentStepFollow>>()));

// THE PER-RUN FOREIGN TOOLS CHECK, called by every member at the end of its run (`onTerminal`
// above). What a preset allows is the catalog's isolation model (AgentCatalog.Allowance); a preset
// that declares no allowed tools is not verified, and its runs are never reported clean. See
// ForeignToolsCheck.
builder.Services.AddSingleton(sp => new PresetAllowedTools(sp.GetRequiredService<AgentCatalog>().Allowance));
builder.Services.AddSingleton<ForeignToolsCheck>();
builder.Services.AddHostedService<DefaultBranchAtStart>();
// The operator CLI's `plugin install` asks for a rescan by writing a file the Host polls: no restart, no API key.
builder.Services.AddHostedService<PluginRescanRequests>();

// THE OPERATOR CLI'S `solution install`: a request file only the Host and root can write, answered
// by the installer the routes run.
builder.Services.AddHostedService<SolutionRequests>();

// SignalR has its OWN serialiser and does not read ConfigureHttpJsonOptions below. Configuring only
// that one would let /api/overview answer `"state": "Idle"` while every containerChanged push
// carried `"state": 1`, and the SPA's ContainerCard compares `snapshot.state === 'Running'`. A
// number never equals that string, so a worker's badge would read `idle` through a whole run while
// its own activity feed - fetched over HTTP - updated beside it. Nothing would fail anywhere; the
// comparison would simply always be false.
//
// The two serialisers are the thing to remember here. Every future converter, naming policy or
// option belongs on BOTH, and a browser reading one contract from two transports cannot tell you
// which of them you forgot.
static void ConfigureHostJson(JsonSerializerOptions options)
{
    options.TypeInfoResolverChain.Insert(0, HostJsonContext.Default);
    options.TypeInfoResolverChain.Add(new DefaultJsonTypeInfoResolver());
    options.Converters.Add(new JsonStringEnumConverter());
}

builder.Services.AddSignalR()
    .AddJsonProtocol(options => ConfigureHostJson(options.PayloadSerializerOptions));

// Registered UNCONDITIONALLY, not behind IsDevelopment() as the vendor recipe has it. The container
// runs Production, so the gated version would compile, pass, and serve nothing, naming no reason.

// Enums as NAMES, not numbers. A browser receiving state 0 has to know the member order to mean
// anything by it, and that coupling breaks silently the first time a value is inserted rather than
// appended -- every client would then be confidently wrong rather than obviously broken.
//
// This is HALF the story. The hub is the other half:
// see AddSignalR above, which needs the same converter added separately because SignalR never reads
// these options.
builder.Services.ConfigureHttpJsonOptions(options => ConfigureHostJson(options.SerializerOptions));

// A BODY THAT DOES NOT BIND THROWS, in every environment rather than Development alone, so
// `JsonBodyErrors` can answer it with a sentence naming the field instead of an empty 400.
builder.Services.Configure<Microsoft.AspNetCore.Routing.RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);

builder.Services.AddHttpContextAccessor();
// THE SIGN-IN PROBE: asked of a connected worker, recorded in agent-auth.json for the doctor.
builder.Services.AddSingleton(sp => new AgentAuthProbe(
    sp.GetRequiredService<AgentCatalog>(), sp.GetRequiredService<AgentLaunchUser>(),
    sp.GetRequiredService<WorkerAsks>(), dataRoot, sp.GetRequiredService<ILogger<AgentAuthProbe>>(),
    installs: sp.GetRequiredService<AgentInstallProbe>(), measured: control ? sp.GetRequiredService<WorkerInstalls>() : null,
    gate: sp.GetRequiredService<AgentUpdateGate>()));
// THE LAUNCH CHECK: each preset's free invocation through the member runner's own launch (AgentLaunchChecks).
builder.Services.AddSingleton(sp => new AgentLaunchChecks(
    sp.GetRequiredService<AgentCatalog>(), sp.GetRequiredService<ProcessAgentRunner>(), dataRoot,
    sp.GetRequiredService<ILogger<AgentLaunchChecks>>(), sp.GetRequiredService<AgentUpdateGate>(), onThisMachine: !control));
builder.Services.AddHostedService(sp => sp.GetRequiredService<AgentLaunchChecks>());

// THE PRE-FLIGHT: what each preset's CLI would load, listed by the CLI itself as the agent user,
// once the Host is serving and again after every catalog save. Never on the start path.
// Each listing runs on the connected worker with the most headroom (WorkerListingRunner).
builder.Services.AddSingleton<IListingRunner>(sp => new WorkerListingRunner(
    sp.GetRequiredService<WorkerAsks>(), pathIsTheWorkers: !control));
builder.Services.AddSingleton(sp => new AgentToolPreflight(
    sp.GetRequiredService<AgentCatalog>(), sp.GetRequiredService<IListingRunner>(), dataRoot,
    sp.GetRequiredService<ILogger<AgentToolPreflight>>(),
    sp.GetRequiredService<AgentUpdateGate>(), onThisMachine: !control));
builder.Services.AddHostedService(sp => sp.GetRequiredService<AgentToolPreflight>());
builder.Services.AddMcpServer()
    .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
    .WithTools<PlatformMcpTools>();

var app = builder.Build();
app.Lifetime.ApplicationStopped.Register(pluginEvents.Dispose);
admissionHoldWriter.Logger = app.Services.GetRequiredService<ILogger<AdmissionHoldWriter>>();
// Made now, not on first use: it follows every run from the first one on.
app.Services.GetRequiredService<AgentStepFollow>();

{
    var runAs = app.Services.GetRequiredService<AgentLaunchUser>();
    if (runAs.Refuses)
    {
        // Loud, because every member, Concierge and host git will refuse.
        app.Logger.LogError(
            "Agent launch: {Mode} - no agent child, Concierge or host git will run: {Reason}", runAs.Mode, runAs.Reason);
    }
    else
    {
        app.Logger.LogInformation("Agent launch: {Mode} - {Reason}", runAs.Mode, runAs.Reason);
    }

    AgentLaunchRecord.Write(dataRoot, runAs, app.Logger);

    var memoryLimits = app.Services.GetRequiredService<RunMemoryLimits>();
    var runLimit = memoryLimits.Limit();
    var heavyNow = tenantSettings.HeavyRunMemoryLimit(0, 0);
    if (memoryLimits.NotEnforced)
    {
        // Under rlimit with nothing set, runs get no limit and no heavy raise: the line says so and nothing more.
        app.Logger.LogInformation("{RunMemoryLimits}", memoryLimits.LogLine);
    }
    else
    {
        app.Logger.LogInformation("{RunMemoryLimits} (each run now: {Limit}; a heavy run with no other running: {Heavy})", memoryLimits.LogLine,
            runLimit.Mb is { } mb ? $"{mb} MB, as {runLimit.Source}" : $"no limit, as {runLimit.Source}",
            heavyNow.Mb is { } heavyMb ? $"{heavyMb} MB, as {heavyNow.Source}" : $"no limit, as {heavyNow.Source}");
    }

    // For --doctor, which reads the Host's own figures rather than working them out as itself.
    WipRecord.Keep(dataRoot, tenantSettings, memoryLimits, app.Logger);

    var memberTemp = app.Services.GetRequiredService<MemberTempRoot>();
    app.Logger.LogInformation("Member temporary folders: {Path} - {Reason}", memberTemp.Path, memberTemp.Reason);
}

// THE ADDRESS EVERY MEMBER IS TOLD TO CALL, printed for the reason the dev server prints its proxy
// target: when this is wrong it is wrong SILENTLY - a worktree's members write their progress to
// another instance and nothing else says so. One line, at the one
// moment somebody is looking at this console.
Console.WriteLine($"Members call back on {memberBaseAddress}.");

// AWAITED, before anything can serve a request and before PumpService takes a pass. A registry that
// has not been restored is not wrong, only empty: it answers "no such team" for every team its own
// database still holds, which presents as "my teams are gone" rather than as a startup problem.
//
// CAUGHT the same way the migration above is. RestoreAsync converts what would otherwise be
// PER-REQUEST failures - an unwritable Directory.CreateDirectory, an AgentEnvironment.ForContainerAsync that
// cannot mint a credential - into a startup failure, and an uncaught one would be a thirty-line
// trace instead of a sentence. TeamRestorationFailedException is thrown
// from RestoreAsync itself, where the team being restored is known.
// BUILT-IN SKILLS FROM THE BUILD, on every start, BEFORE any team is restored. The index
// is rebuilt from what this build ships, so changing a built-in in code reaches every agent on the
// next start with no manual step. A volume that still holds a skill-files folder has those files
// moved to the backup and the custom ones imported; without such a folder this does nothing.
// All of it precedes the restore because every restored prompt lists its role's skills.
var skills = app.Services.GetRequiredService<ISkillStore>();
var paths = app.Services.GetRequiredService<TeamPaths>();
var skillDirectory = app.Services.GetRequiredService<SkillDirectory>();

foreach (var moved in await skills.ReplaceBuiltInsAsync(BuiltInSkills.Drafts(), DateTimeOffset.UtcNow))
{
    Console.WriteLine(
        $"WARNING: a custom skill held a built-in skill's name and was relabelled: {moved}.");
}

// THE INSTALLED PLUGINS' SKILLS, now and after every rescan however it is started: locked rows of
// kind `plugin`, named `plugin-<id>[-<name>]`, never in a prompt's list. See `PluginSkills`.
await pluginCatalog.Attach(async scan =>
{
    foreach (var line in await PluginSkills.SyncAsync(skills, scan))
    {
        Console.WriteLine($"WARNING: {line}");
    }
});

var tenantSkillMigration = await SkillMigration.RunAsync(
    [("skills", paths.TenantSkills), ("skill-drafts", paths.TenantSkillDrafts)],
    paths.SkillBackups,
    skills);

Program.ReportSkillMigration(tenantSkillMigration, paths.SkillBackups);

await skillDirectory.RefreshAsync(skills);

RestoreReport restoreReport;
TeamRegistry registry;

try
{
    registry = app.Services.GetRequiredService<TeamRegistry>();
    restoreReport = await registry.RestoreAsync();
}
catch (TeamRestorationFailedException exception)
{
    Console.Error.WriteLine(exception.Message);
    Environment.ExitCode = 1;

    return;
}

// A MANAGER'S ROSTER NAMES ITS PLUGIN MEMBERS' DESCRIPTIONS AND SKILLS, so a rescan that changes
// them re-prompts every member, the same as a skill write does. Attached after the restore, which
// composed every prompt from the plugins already loaded.
await pluginCatalog.Attach(_ => registry.RepromptAllAsync());

// A PLACED TEAM WHOSE VOLUME IS NOT THERE. Said out loud for the reason the blocked moves above
// are, and it is the louder of the two - but NOT an exit: exiting 1 would take every OTHER team
// down with it - default-rooted ones included - into a restart loop with no in-product way out.
//
// The team is up and visible; its members are marked and refuse at their wake, which is what stops
// one of them running in the Host's own directory.
foreach (var root in restoreReport.UnreachableRoots)
{
    Console.WriteLine(
        $"WARNING: team '{root.TeamId}' is placed at {root.Root}, which could not be reached "
        + $"({root.Reason}). The team is up but its members will refuse to run until the folder is "
        + "reachable and the Host is restarted.");
}

// A STORED TEAM THAT COULD NOT BE REBUILT AT ALL, which is the louder of the two and is here for
// the identical reason. A `teams` row whose id is not a legal container name must not throw out of
// RestoreAsync, past the catch above - an `ArgumentException`, not `TeamRestorationFailedException` -
// or it escapes `Main`, the Host never builds an IHost at all, and its supervisor restarts it into
// the same refusal forever with every other team down too.
//
// The row is left exactly where it is, and this is the only thing that says so. Nothing repairs it
// and nothing deletes it: a team's data is the operator's, and the recovery here is a person
// reading this line.
foreach (var team in restoreReport.Unrestorable)
{
    Console.WriteLine(
        $"WARNING: team '{team.TeamId}' could not be restored and was skipped - {team.Reason} "
        + "Every other team started normally. Its row is still in the database; remove or rename "
        + "it by hand if you no longer want it.");
}

// THE REMOVALS A DELETION OR RESET COULD NOT FINISH, retried now that the teams are restored and a
// live team or member can be told from a dead one. Before any member runs, so nothing is writing in
// a folder being emptied. Never fatal: a folder that still cannot be removed stays recorded, is said
// here, and is retried at the next start or on request.
try
{
    var retried = await app.Services.GetRequiredService<UnfinishedRemovalRetry>().RetryAsync();

    foreach (var removal in retried)
    {
        Console.WriteLine(removal.Finished
            ? $"Removal finished at start: {removal.Path}."
            : $"WARNING: removal still unfinished at start: {removal.Path} - "
              + (removal.Note ?? $"{removal.Remaining.Count} path(s) remain: {string.Join(", ", removal.Remaining)}"));
    }

    if (retried.Count > 0)
    {
        await app.Services.GetRequiredService<TenantLogging>().WriteAsAsync(
            null, null, TenantActions.RemovalRetried, null, null,
            new { atStart = true, retried = retried.Select(RemovalEndpoints.Detail).ToArray() });
    }
}
catch (Exception exception) when (exception is not OperationCanceledException)
{
    Console.WriteLine($"WARNING: unfinished removals could not be retried at start: {exception.Message}");
}

// CONCIERGE ATTACHMENTS PAST THEIR RETENTION, which a Host stopped rather than its sessions ended
// would otherwise keep for good. Before any Concierge starts. Never fatal.
try
{
    foreach (var left in await app.Services.GetRequiredService<ConciergeAttachments>().RemoveExpiredAsync(DateTimeOffset.UtcNow))
    {
        Console.WriteLine($"WARNING: an expired Concierge attachment could not be removed at start: {left}");
    }
}
catch (Exception exception) when (exception is not OperationCanceledException)
{
    Console.WriteLine($"WARNING: expired Concierge attachments could not be removed at start: {exception.Message}");
}

// Any per-team skill folders still on disk, now that the teams are registered and their
// roots resolve. No skill is team-scoped: each folder is migrated like the tenant's and
// moved to the backup, and a skill imported from one re-prompts everybody so it is listed.
var teamSkillMigration = await SkillMigration.RunAsync(
    [.. registry.All(withRoot: true).SelectMany(team => new[]
    {
        ($"teams/{team.Id}/skills", paths.SkillsFor(team.Id)),
        ($"teams/{team.Id}/skill-drafts", paths.SkillDraftsFor(team.Id)),
    })],
    paths.SkillBackups,
    skills);

Program.ReportSkillMigration(teamSkillMigration, paths.SkillBackups);

if (teamSkillMigration.Imported.Count > 0)
{
    await skillDirectory.RefreshAsync(skills);
    await registry.RepromptAllAsync();
}

// Git prerequisite check
//
// COLLECTED AS WELL AS PRINTED. A `PathSearch` miss is a launch failure waiting to happen, and a
// line in a terminal that scrolls is not evidence anybody can find later. The list is
// recorded with the rest of the startup facts below, in one pass, rather than here: the point of
// those rows is that they are read together as "what was this instance configured as".
var missingPrerequisites = new List<string>();

var gitExecutable = builder.Configuration["GitExecutable"] ?? "git";
if (SystemCommand.Find(gitExecutable) is null)
{
    missingPrerequisites.Add(gitExecutable);

    Console.WriteLine(
        "WARNING: git was not found in a root-owned system directory - the Repos panel cannot run until " +
        "git is installed and the Host is restarted.");
}

// GitHub CLI prerequisite check
if (PathSearch.Find("gh") is null)
{
    missingPrerequisites.Add("gh");

    Console.WriteLine(
        "WARNING: gh was not found on this machine's PATH - agents use it for pull requests; " +
        "the platform does not invoke it.");
}

// ANNOUNCED, never inferred. A missing/empty member-agent allowlist refuses hiring rather than
// running on a guessed catalog name, and is fixed in one click by somebody who knows to make it.
// There is no prompt to announce: every member is told its job by its role.
var restoredTeams = registry.All();

foreach (var pausedTeam in restoredTeams.Where(team => team.Paused))
{
    Console.WriteLine(
        $"WARNING: team '{pausedTeam.Name}' is paused. No new work will be delivered until someone "
        + "resumes it.");
}

// Every team is one somebody can hire into, so there is nothing to exclude and the condition each
// line reports is one a person can act on.
var unchosenAllowlists = restoredTeams
    .Where(t => t.MemberAgents is not { Count: > 0 })
    .Select(t => t.Name)
    .ToList();

if (unchosenAllowlists.Count > 0)
{
    Console.WriteLine(
        $"No Agent allowlist for new members on {string.Join(", ", unchosenAllowlists)} - "
        + "hiring is refused until one is chosen under Dynamic members in Team Settings.");
}

// EXISTING ROWS WARN AND SERVE, never refuse.
//
// A guard fails in whichever direction is RECOVERABLE, and there is nothing wrong with the data
// here - only with what someone may do next. Startup already names every NULL prompt and every
// headless preset with no usage format, and RestoreAsync skips and reports an illegal teams row
// rather than dying on it. `AddContainerAsync`, `UpdateContainerAsync` and `PUT /api/agents` all
// refuse the illegal pair; this is the fourth site, for a pair already sitting in the database.
var firehoseHolders = Program.FirehoseHolders(loadedCatalog, restoredTeams);

if (firehoseHolders.Count > 0)
{
    // Named a real recovery ONLY: there is no `subscribes` editor anywhere (member settings
    // can repoint the Agent and the Prompt, never the subscription - see `UpdateContainer` in
    // this file), and "point it at another Agent" has no seeded target, since nothing seeds a
    // `languageModel: false` preset. The two that actually work are on the Agents screen - open
    // this preset and clear its Language model checkbox, which `PUT /api/agents` always allows
    // in that direction - or delete the member and hire a fresh one: `addMember` on the SPA
    // sends no `subscribes` at all, so a member hired through the UI never carries a firehose
    // subscription to begin with.
    Console.WriteLine(
        $"WARNING: {string.Join(", ", firehoseHolders)} subscribe(s) to a high-volume type on a "
        + "preset that is now a language model - it will be woken by every status line every "
        + "worker writes. On the Agents screen, open that preset and clear its Language model "
        + "checkbox, or delete the member and hire a fresh one - a hire through the UI never "
        + "carries a high-volume subscription.");
}

// AFTER restoration, because it walks the containers restoration has just brought back - there is
// nothing to hand work to before that. Says so when it did anything: work resumed silently is work
// nobody knows was ever at risk, and an interrupted run is reported as a failure somebody has to
// read to understand why their instruction came back unfinished.
// FIRST, what deleted teams left queued: removed and logged, a live team's never touched.
await app.Services.GetRequiredService<PendingDeliveriesAtStart>().SweepAsync();

var resumed = await app.Services.GetRequiredService<ContainerHost>().ResumePendingAsync();

// BOTH numbers, and a line whenever either is non-zero. An interrupted run is reported to the log
// and is exactly the thing whoever dispatched it needs told about, so a restart that cut runs short
// must say so even when none of them could be re-offered.
if (resumed.Reoffered > 0 || resumed.Interrupted > 0 || resumed.ManagersWoken > 0)
{
    Console.WriteLine(
        $"Resumed {resumed.Reoffered} queued item(s) and reported {resumed.Interrupted} "
        + $"interrupted run(s) from the last stop; told {resumed.ManagersWoken} Manager(s) cut off "
        + "by the restart to carry on.");
}

// THE STARTUP AND LIFECYCLE FACTS, IN ONE PASS.
//
// Every line below this point is also PRINTED, and printed alone it is lost: the host's stdout is
// a window with a bounded scrollback. These are the lines that answer "WHAT WAS THIS INSTANCE EVEN CONFIGURED AS" on a cold start, and they
// are worth a row each precisely because nobody thinks to copy them out until it is too late.
//
// BOUNDED BY BOOTS, NOT BY TRAFFIC. That is what makes this family affordable where per-request
// timings are not: an instance restarted twenty times a day writes a few dozen rows, not a few
// hundred thousand.
//
// HERE rather than at each printing site, because these rows are read TOGETHER. A reader opening
// the screen after a bad night wants one contiguous block describing the instance, not seven facts
// scattered between whatever else was happening.
//
// AWAITED, before anything can serve. A startup fact recorded after the first request is a fact
// that is missing from exactly the window somebody is investigating.
{
    var startup = app.Services.GetRequiredService<DiagnosticsRecorder>();

    // FIRST, so the trim cannot remove the rows this boot is about to write, and so a host that
    // stays up for months is not the reason its own log is unbounded. The store trims from the
    // write path as well, throttled - this is the pass that happens even on a quiet instance.
    await startup.TrimAsync();

    await startup.WriteAsync(
        DiagnosticSeverity.Info,
        DiagnosticKinds.StartupInstance,
        DiagnosticSources.Startup,
        message: $"{instance} serving from {dataRoot}",
        detail: new
        {
            instance,
            dataRoot,
            database,

            // THE PORT, by way of the address members are told to call - the same fact, and the one
            // spelling of it that has ever been wrong in a way anybody noticed.
            membersCallBack = memberBaseAddress,
            teams = restoredTeams.Count,

            // SO A READER CAN TELL A QUIET INSTANCE FROM A TRIMMED ONE. A bounded log that does not
            // say what its bound is looks identical to one that recorded nothing.
            retentionDays = diagnosticsRetention.MaxAge.TotalDays,
            retentionRows = diagnosticsRetention.MaxRows,
        });

    // WHAT THE PRAGMA ACTUALLY ANSWERED, rather than firing it blind and throwing it away. Every
    // other guarantee over this file assumes WAL - a reader never blocking a writer, `--backup`
    // being safe against a live database, two stores writing one file - so a file that did not
    // enter it is an error rather than a curiosity, and saying so is the whole value of the row.
    //
    // NULL IS "NOT MEASURED" AND IS NOT A FAILURE, the distinction CredentialUseRunner makes about
    // a probe that could not see: a pragma that could not be read must not convict.
    await startup.WriteAsync(
        migrator.JournalMode is null ? DiagnosticSeverity.Info
            : migrator.JournalMode.Equals("wal", StringComparison.OrdinalIgnoreCase)
                ? DiagnosticSeverity.Info
                : DiagnosticSeverity.Error,
        DiagnosticKinds.DatabaseJournalMode,
        DiagnosticSources.Database,
        message: migrator.JournalMode ?? "(unknown)",
        detail: new { journalMode = migrator.JournalMode });

    // ONLY WHEN ONE WAS TAKEN. ApplyAsync answers null when nothing was pending, which is every
    // ordinary restart, and a row per boot saying "no backup" would bury the boots where there was
    // one.
    if (schemaBackup is not null)
    {
        await startup.WriteAsync(
            DiagnosticSeverity.Info,
            DiagnosticKinds.StartupBackupWritten,
            DiagnosticSources.Startup,
            message: schemaBackup);
    }

    foreach (var pausedTeam in restoredTeams.Where(team => team.Paused))
    {
        await startup.WriteAsync(
            DiagnosticSeverity.Warning,
            DiagnosticKinds.StartupTeamPaused,
            DiagnosticSources.Startup,
            message: pausedTeam.Name,
            detail: new { team = pausedTeam.Name });
    }

    foreach (var team in unchosenAllowlists)
    {
        await startup.WriteAsync(
            DiagnosticSeverity.Warning,
            DiagnosticKinds.StartupNoAgentAllowlist,
            DiagnosticSources.Startup,
            message: team,
            detail: new { team });
    }

    foreach (var preset in unmeasuredHeadlessPresets)
    {
        await startup.WriteAsync(
            DiagnosticSeverity.Warning,
            DiagnosticKinds.StartupPresetWithoutUsageFormat,
            DiagnosticSources.Startup,
            message: preset,
            detail: new { preset });
    }

    // A `PathSearch` MISS IS A LAUNCH FAILURE THAT HAS NOT HAPPENED YET, which is why it
    // is here rather than only under `process.*`: by the time a run fails for it, the reason is a
    // child process's own "cannot find the file", and nothing connects that to a host that said so
    // at boot.
    foreach (var executable in missingPrerequisites)
    {
        await startup.WriteAsync(
            DiagnosticSeverity.Warning,
            DiagnosticKinds.StartupPrerequisiteMissing,
            DiagnosticSources.Startup,
            message: executable,
            detail: new { executable });
    }
}

// FIRST IN THE PIPELINE, so it sees what every other middleware does.
//
// Without this a 500 reaches the console the Host was started from and nowhere else, and is
// unanalysable afterwards. An exception thrown in authentication,
// in TeamGate or in a static-file handler is still a 500 nobody can explain, so a seam placed after
// them would be blind to exactly the failures that are hardest to reason about.
//
// IT RECORDS AND RE-THROWS. It does not answer the request, does not set a status and does not
// swallow: everything downstream of an unhandled exception behaves exactly as it would without it,
// and the only addition is a row. See DiagnosticsMiddleware for what it will and will not
// record, and why that set is closed.
DiagnosticsMiddleware.Use(app);

// SECOND, so every response carries them - the bundle, an API answer, a gate's refusal, a 404.
SecurityHeaders.Use(app);

// THIRD, before the console's pages and files are served: the worker key is refused everywhere but
// the worker connection, with a sentence, before anything else reads it.
workerKeys.Use(app);

app.UseDefaultFiles();

// no-cache, not no-store: ETag/Last-Modified stay in play, so an unchanged file still answers a
// cheap 304 instead of a full re-download on every navigation. Without this header the default
// static-file middleware stamps only those two and leaves freshness to the browser's heuristics,
// which lets a browser serve a stale index.html with no round-trip at all - a new build would go on
// looking like the previous one until a hard refresh. Applies
// to everything under wwwroot EXCEPT /assets: nothing else there carries an explicit cache policy
// today, and the same staleness risk exists for any other hand-edited runtime file this instance
// might carry.
//
// /assets is carved out because it is Vite's content-addressed output - the filename itself
// changes when the bytes do, so the file at a given URL never changes and no-cache's conditional
// GET can only ever come back 304. That round trip is free on loopback but not on the ngrok tunnel
// this is served over, where every asset on every reload pays 50-200ms for a revalidation that was
// always going to say "unchanged". immutable is honest here in a way it would not be for
// index.html, which DOES change without a new filename.
// THE SPA FALLBACK, AND IT IS A REWRITE RATHER THAN AN ENDPOINT. The alternatives are instructive
// enough to keep.
//
// WHY IT IS NEEDED: `UseStaticFiles` serves files that EXIST and `UseDefaultFiles` rewrites
// only `/`, so without this a browser asking the server for `/console`, `/login` or `/board` would
// match no endpoint at all - and a request matching no endpoint is exactly what the authorization
// FALLBACK policy refuses, with **401 and an empty body**. It bites only on a deep link, a refresh
// or a pasted URL, because ordinary use loads `/` once and the client router never asks the server
// again. `routes.ts` redirects `/board` to `/console` so a bookmark lands, which needs this too.
//
// WHY NOT `MapFallbackToFile`: a catch-all endpoint matches every path AND EVERY VERB, and both
// halves break something the suite guards.
//   - Every path: `/api/kanban/templates` must answer 404, and
//     `KanbanTeamScopeTests.The_ungated_routes_are_gone` would see 200 with HTML.
//   - Every verb: with an endpoint under `/api`, a POST to a GET-only path would stop being
//     **405 Method Not Allowed** and become 404, because routing reports a method mismatch only
//     when no endpoint claims the request. `TeamAccessViewTests` guards that.
// A `regex(^(?!api/|hub/).*$)` route constraint serves nothing at all. So the rule belongs BEFORE
// routing, where it costs the machine surface nothing.
//
// THE RULE: a GET, for an extensionless path, outside `/api`, `/hub` and `/sites` (a site is served by its own routes), is a client-side route.
// That is exact here because nothing on this server renders HTML - every page is the one bundle.
//   - GET only, so a mistyped POST still gets a refusal rather than a page to parse.
//   - Extensionless, so a missing `/assets/...` chunk still 404s as itself instead of silently
//     answering HTML, which is the failure mode that makes a bad deploy look like a router bug.
//   - BEFORE `UseStaticFiles`, so the rewritten request is served by it and short-circuits there -
//     the authorization fallback policy is never reached, which is what the 401 would be.
//
// The bundle is not the secret, the API is - the same argument `UseAuthentication`'s placement
// comment below already makes. The client router still guards `/console` and sends a signed-out
// visitor to the landing page.
app.Use(async (context, next) =>
{
    var path = context.Request.Path;

    if (HttpMethods.IsGet(context.Request.Method)
        && path.HasValue
        && !Path.HasExtension(path.Value)
        && !path.StartsWithSegments("/api")
        && !path.StartsWithSegments("/hub")
        && !path.StartsWithSegments(SiteEndpoints.Prefix)
        && !path.StartsWithSegments(HealthEndpoints.Route))
    {
        context.Request.Path = "/index.html";
    }

    await next();
});

app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
        context.Context.Response.Headers.CacheControl =
            context.Context.Request.Path.StartsWithSegments("/assets")
                ? "public,max-age=31536000,immutable"
                : "no-cache",
});

// BEFORE UseAuthentication, because the cookie's SecurePolicy is SameAsRequest and that reads
// Request.IsHttps. A tunnel terminates TLS at the proxy and forwards plain HTTP to this host, so
// without this the request looks insecure, the login cookie ships with NO Secure attribute, and the
// one deployment the SecurePolicy comment reasons about is the one it fails on. Pinning Always
// instead would fix the tunnel and drop the cookie on plain-http loopback, which is the ordinary
// local case - so both pieces are needed, and removing either re-opens one of the two.
//
// PROTO ONLY, and only from THIS MACHINE. X-Forwarded-For and -Host are deliberately not consumed:
// nothing here authorises on client IP or host, so accepting them would widen what a forged header
// controls for no gain. The known-proxy list is left at its default of ::1 PLUS an explicit
// 127.0.0.1 - the default covers only the IPv6 loopback, and a tunnel agent (ngrok, cloudflared)
// runs on this machine and dials Kestrel's IPv4 loopback, so without the addition the headers are
// silently ignored and this middleware does nothing at all. Clearing the lists - the usual
// make-it-work advice - would let anyone who can reach the port claim any scheme, so it is not done.
//
// AND FROM THIS MACHINE'S OWN ADDRESSES, not loopback alone. In the container, pasta delivers the
// tunnel's connection from the container's own eth0 address, so loopback-only would log every tunnel
// request as http://. See `LocalProxies`.
var forwarded = LocalProxies.Options(LocalProxies.OwnAddresses());

app.UseForwardedHeaders(forwarded);

// AFTER the static-file middleware, not before: ASP.NET's authorization fallback policy applies to
// requests that match NO endpoint at all, which is exactly what a static file request is. Putting
// this first would mean the SPA bundle itself needs a key under the fallback policy - and there
// would be no page left on which to log in. The bundle is not the secret; the API is.
app.UseAuthentication();
app.UseAuthorization();

// EVERY ROUTE THAT BINDS A JSON BODY, in one place: a wrong type or malformed JSON is answered 400
// with a sentence naming the field and the type expected, never an empty body.
JsonBodyErrors.Use(app);

// AFTER UseAuthorization, which is what populates context.User for a key-authenticated request.
PrincipalLogScope.Use(app);

// A call under a Concierge session's own credential is that session's activity while it runs,
// /mcp included. AFTER UseAuthorization for the same reason.
ConciergeCallClock.Use(app);

// NoPermitRequired, and not an exemption from anything: every tool relays to an /api route with
// the caller's own X-Api-Key (PlatformMcpTools.SendAsync), so TeamGate and PermitGate judge each
// call on that route's own marker. RouteMarkerTests requires a marker here like everywhere else.
app.MapMcp("/mcp").NoPermitRequired();
SurfaceEndpoints.Map(app, dataRoot);
Appearance.Map(app);
CapacityEndpoints.Map(app);
SiteEndpoints.Map(app);
SiteApiEndpoints.Map(app);
TenantSettingsEndpoints.Map(app);
LedgerEndpoints.Map(app);
TeamActivity.Map(app);
TeamTokenRuns.Map(app);
TeamActivityFrom.Map(app);
WorkspaceFileChangeEndpoints.Map(app);
AgentStepEndpoints.Map(app);
OutcomeEndpoints.Map(app);
HealthEndpoints.Map(app, database, dataRoot);
VersionEndpoints.Map(app);
ReleaseCheckEndpoints.Map(app);
MarketplaceEndpoints.Map(app);
// A worker's one connection. In control a worker joins the pool here; a Host that runs its runs
// itself (all) welcomes none, and a worker that connects to it is told so, in a sentence, and stops.
var workerConnections = new WorkerConnections(
    app.Services.GetRequiredService<WorkerPool>(),
    app.Services.GetRequiredService<WipLedger>(),
    async (envelope, ct) =>
    {
        await app.Services.GetRequiredService<WorkerAsks>().HandleAsync(envelope, ct);
        await app.Services.GetRequiredService<RunDirectory>().HandleAsync(envelope, ct);
        if (envelope.Event is RunMeasured or TerminalMeasured or WorkerCapacitySampled)
        {
            await app.Services.GetRequiredService<CapacitySampler>().HandleAsync(envelope, ct);
        }

        await app.Services.GetRequiredService<WorkerStreams>().HandleAsync(envelope, ct);
    },
    worker => app.Services.GetRequiredService<RunDirectory>().OpenOn(worker),
    workerKey,
    takesWorkers: control,
    BuildVersion.Current.Version,
    settings: info => new RunWorkerSettings(
        ProcessAgentRunner.Allowance((tenantSettings.RunMemoryLimit(WorkerBounds.Of(info)), tenantSettings.RunMemoryCeiling(WorkerBounds.Of(info)))).Limit,
        TenantSettings.HostReserveMb),
    timings: WorkerBounds.Timings(builder.Configuration),
    diagnostics: app.Services.GetRequiredService<IDiagnosticsLog>(),
    log: app.Services.GetRequiredService<ILogger<WorkerConnections>>(),
    streams: app.Services.GetRequiredService<WorkerStreams>().Deliver);
workerConnections.Changed += () => wip.SetMax(tenantSettings.WipMaxRunning);
// The run limit is the sum of the connected workers' bounds, so --doctor's record of it is written
// again whenever a worker joins or goes, as it is after a setting change; written at start only, it
// said "no worker is connected" for as long as control ran.
workerConnections.Changed += () => WipRecord.Of(tenantSettings, app.Services.GetRequiredService<RunMemoryLimits>()).Write(dataRoot, app.Logger);
// A setting the figures a worker runs by are built from may have changed: every connected worker is
// told now, not at its next welcome. Any change is said, since the figures are cheap to send and a
// list of the settings they read would drift from the code that reads them.
if (control)
{
    tenantSettings.Changed += setting => Observed.Forget(workerConnections.SettingsChangedAsync(), app.Logger, "settings push to the workers");
}
// In control the start's retry of unfinished removals ran before any worker could connect: each worker
// that joins retries them once, so what only the agent's pass can remove does not wait for a person.
RetryWhenAWorkerJoins.Wire(
    control, workerConnections,
    ct => app.Services.GetRequiredService<UnfinishedRemovalRetry>().RetryAsync(ct: ct),
    app.Services.GetRequiredService<ILogger<WorkerConnections>>());
// Each worker that joins is asked which agent CLIs it has, so whether one is installed is every
// worker's answer, and two that disagree are seen.
MeasureInstallsWhenAWorkerJoins.Wire(
    control, joined => workerConnections.Joined += joined,
    worker => app.Services.GetRequiredService<WorkerPool>().For(worker),
    () => MeasureInstallsWhenAWorkerJoins.Commands(app.Services.GetRequiredService<AgentCatalog>()),
    app.Services.GetRequiredService<WorkerAsks>(), app.Services.GetRequiredService<WorkerInstalls>(),
    app.Services.GetRequiredService<ILogger<WorkerConnections>>(), app.Services.GetRequiredService<AgentUpdateGate>());
AgentUpdatesAtStart.Wire(
    control, builder.Configuration["HARNESS_IMAGE"], builder.Configuration["HARNESS_UPDATE_AGENTS"], joined => workerConnections.Joined += joined,
    () => AgentUpdatesAtStart.OnePerCommand(app.Services.GetRequiredService<AgentCatalog>()),
    agent => app.Services.GetRequiredService<AgentCliUpdater>().Request(agent, person: null),
    app.Services.GetRequiredService<ILogger<WorkerConnections>>());
// EVERYTHING CONTROL MEASURES THROUGH A WORKER IS MEASURED AGAIN WHEN THE WORKERS CHANGE - the sign-in
// probe, the launch check, the tool pre-flight and the CLI version record - as one pass once the
// changes settle, and when an update ends, which also asks every placeable worker about that command.
// Measured at start only, none of them had a worker to ask.
RemeasureWhenWorkersChange.Wire(
    control,
    changed => workerConnections.Changed += changed,
    attached => workerConnections.Attached += attached,
    ended => app.Services.GetRequiredService<AgentUpdateGate>().UpdateEnded += ended,
    () =>
    {
        var auth = app.Services.GetRequiredService<AgentAuthProbe>();
        var launch = app.Services.GetRequiredService<AgentLaunchChecks>();
        var tools = app.Services.GetRequiredService<AgentToolPreflight>();
        return new RemeasureWhenWorkersChange(
            [
                ("the sign-in probe", ct => auth.ReportsAsync(ct)),
                ("the launch check", ct => launch.ReportsAsync(ct, fresh: true)),
                ("the tool pre-flight", ct => tools.PassAsync(ct)),
                ("the CLI versions", ct => CliVersionsWhenWorkersChange.MeasureAsync(
                    app.Services.GetRequiredService<AgentCatalog>(), app.Services.GetRequiredService<WorkerAsks>(), dataRoot,
                    app.Services.GetRequiredService<AgentUpdateGate>(), app.Logger, ct)),
            ],
            () =>
            {
                auth.Forget();
                launch.Forget();
            },
            log: app.Services.GetRequiredService<ILogger<RemeasureWhenWorkersChange>>(),
            stopping: app.Lifetime.ApplicationStopping);
    },
    command =>
    {
        var pool = app.Services.GetRequiredService<WorkerPool>();
        return Task.WhenAll(pool.Workers.Select(worker => pool.For(worker)).OfType<IRunWorker>().Select(worker =>
            MeasureInstallsWhenAWorkerJoins.MeasureAsync(
                worker, [command], app.Services.GetRequiredService<WorkerAsks>(), app.Services.GetRequiredService<WorkerInstalls>(),
                app.Logger, app.Lifetime.ApplicationStopping, app.Services.GetRequiredService<AgentUpdateGate>())));
    },
    app.Services.GetRequiredService<ILogger<WorkerConnections>>());
// In all no worker joins: when an update ends, this machine's answers are taken again.
if (!control)
{
    app.Services.GetRequiredService<AgentUpdateGate>().UpdateEnded += command =>
    {
        app.Services.GetRequiredService<AgentInstallProbe>().Forget(command);
        app.Services.GetRequiredService<AgentAuthProbe>().Forget();
        app.Services.GetRequiredService<AgentLaunchChecks>().Refresh();
        app.Services.GetRequiredService<AgentToolPreflight>().Refresh();
    };
}
// Control's workers, recorded for --doctor (another process) on every change and every few seconds.
System.Threading.Timer? workersRecording = null;
if (control)
{
    void RecordWorkers() => WorkersRecord.RecordSafely(
        () => WorkersRecord.Of(
            app.Services.GetRequiredService<WorkerPool>(), app.Services.GetRequiredService<WipLedger>(),
            worker => app.Services.GetRequiredService<RunDirectory>().OpenOn(worker), BuildVersion.Current.Version),
        dataRoot, app.Logger);
    workerConnections.Changed += RecordWorkers;
    workersRecording = new System.Threading.Timer(_ => RecordWorkers(), null, TimeSpan.Zero, WorkersRecord.Every);
}
recordConciergeSessions = () => _ = Task.Run(async () =>
{
    // A session ended by shutdown has nothing left to record, and nothing left to record it with.
    if (app.Lifetime.ApplicationStopping.IsCancellationRequested) return;

    try
    {
        var consoles = app.Services.GetRequiredService<ConciergeSessionStore>();
        var accounts = app.Services.GetRequiredService<IUserStore>();
        var sessions = await ConciergeSessionsView.ReadAsync(
            consoles, tenantSettings.ConciergeIdleTimeout, app.Services.GetRequiredService<CapacitySampler>().Terminal,
            async (user, ct) => (await accounts.FindByIdAsync(user, ct))?.Email, CancellationToken.None);
        ConciergeSessionsRecord.Of(consoles.Clock.GetUtcNow(), tenantSettings.ConciergeIdleTimeout, sessions).Write(dataRoot, app.Logger);
    }
    catch (ObjectDisposedException)
    {
        // The Host stopped between the check and the read.
    }
    catch (Exception exception) when (exception is not OutOfMemoryException)
    {
        app.Logger.LogWarning("Concierge: could not record the sessions for the doctor: {Error}", exception.Message);
    }
});
app.Services.GetRequiredService<ConciergeSessionStore>().Changed += () => recordConciergeSessions?.Invoke();
recordConciergeSessions();
// ONE CALLBACK, IN THIS ORDER. The Host going down ends no admission hold: a waiter it withdraws, or
// admits to a slot it frees, leaves its row open for the next start to close, marked unfinished - so
// the ledger is told first, before dropping the workers frees a slot. One callback rather than three
// because a token runs its callbacks newest first, and an order that depends on registration order
// is an order the next registration silently breaks.
app.Lifetime.ApplicationStopping.Register(() =>
{
    wip.HostStopping();
    workerConnections.Stop();
    workersRecording?.Dispose();
});
Func<IReadOnlyList<WorkerSample>> workersNow = () => WorkersView.Of(
    app.Services.GetRequiredService<WorkerPool>(), app.Services.GetRequiredService<WipLedger>(), BuildVersion.Current.Version,
    worker => ConciergeSessionsView.TerminalsOn(
        app.Services.GetRequiredService<ConciergeSessionStore>(), worker, app.Services.GetRequiredService<CapacitySampler>().Terminal));
WorkerEndpoints.MapList(app, workersNow);
ServiceStatusEndpoints.Map(app, database, dataRoot, workersNow);
app.Services.GetRequiredService<CapacitySampler>().Describe = workersNow;
if (control)
{
    app.Logger.LogInformation(
        workerKeys.Configured
            ? "Control role: runs go to workers that connect at {Route} with the worker key; none runs in this process. CLI updates run on one connected worker, through this control's one update gate."
            : "Control role: no worker key is configured (HARNESS_WORKER_KEY), so no worker can connect and every run waits for a worker ({Route}).",
        WorkerKeyGate.ConnectRoute);
}
WorkerEndpoints.Map(app, async context =>
{
    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    await workerConnections.AcceptAsync(socket, context.RequestAborted);
});
RemovalEndpoints.Map(app);
LocalRepoEndpoints.Map(app);

// AFTER UseAuthorization, so context.User is populated, and BEFORE the endpoints run. One gate
// keyed on the route's team value - see TeamGate's own doc comment for why it is preferred over a
// per-route filter.
TeamGate.Use(app);

// AFTER TeamGate, deliberately. "No such team." must still win, so a caller who cannot see a team
// learns nothing new about it from a permit refusal that never runs - the byte-identity property
// that refusal goes to trouble for holds.
PermitGate.Use(app);

// Snapshots pushed rather than polled. One multiplexed connection carries every container, which is
// the whole reason for a hub here: a browser allows about six concurrent connections per origin, and
// one stream per container would exhaust that as soon as a team is big enough to be interesting.
var hub = app.Services.GetRequiredService<IHubContext<ContainerHub>>();
var containers = app.Services.GetRequiredService<ContainerHost>();
var registryForPush = app.Services.GetRequiredService<TeamRegistry>();

// Group-routed, not Clients.All. Clients.All would be every connection receiving every team's
// container state, which is a side channel around every gate the routes above enforce - a browser
// would learn about another team's queue depth and current instruction the moment anything
// happened there, regardless of what /api/overview and /api/messages are willing to show.
// COALESCED PER MEMBER: a changed snapshot goes at once, a progress line's unchanged one is held
// for a quarter of a second and the newest sent once. See SnapshotPushCoalescer.
var containerPushes = new SnapshotPushCoalescer(
    snapshot => hub.Clients.Group(snapshot.Team).SendAsync("containerChanged", snapshot),
    SnapshotPushCoalescer.DefaultWindow,
    app.Services.GetRequiredService<TimeProvider>(),
    app.Logger);
containers.Changed += containerPushes.Offer;
registryForPush.TeamChanged += team =>
    Observed.Forget(hub.Clients.Group(team.Id).SendAsync("teamChanged", team), app.Logger, "teamChanged push");

OverviewEndpoints.Map(app, tenantSettings);
AuthEndpoints.Map(app);
UserEndpoints.Map(app);
AgentEndpoints.Map(app, dataRoot);
PluginEndpoints.Map(app);
SolutionEndpoints.Map(app);
ConnectionEndpoints.Map(app);
RepoEndpoints.Map(app);
KeyEndpoints.Map(app);
FileSystemEndpoints.Map(app);
SkillsEndpoints.Map(app);
LeaseEndpoints.Map(app);
KanbanEndpoints.Map(app);
BacklogEndpoints.Map(app);
NeedsYou.Map(app);
BacklogEndpoints.MapTeamScoped(app);
DiagnosticsEndpoints.Map(app);
LiveViewEndpoints.Map(app);
TeamEndpoints.Map(app);
TriggerEndpoints.Map(app);
MemberEndpoints.Map(app);
ConciergeEndpoints.Map(app);
WorkflowEndpoints.Map(app);
DocumentsEndpoints.Map(app);
MemberReportEndpoints.Map(app, tenantSettings);

// The console is on a RAW WebSocket, not the SignalR hub. SignalR is message-oriented; a PTY is a
// bidirectional byte stream with binary frames, and forcing one through the other would mean framing
// bytes that are already framed.
app.UseWebSockets();

app.Run();

// An orderly close, and nothing rests on it: every write is already flushed, which is what makes
// the file survive a KILL rather than only a shutdown. This is the tidy half of the same promise.
hostLog.Dispose();

// The workers a terminal or a file read goes to. The run worker is resolved first: in one process
// that is what connects the Host's own worker to the pool.
static WorkerPool WorkersOf(IServiceProvider services)
{
    _ = services.GetRequiredService<IRunWorker>();
    return services.GetRequiredService<WorkerPool>();
}

/// <summary>
/// Public so WebApplicationFactory&lt;Program&gt; can find it. Note the asymmetry with
/// Harness.Cli, whose entry point is a differently-named class for exactly this reason: a project
/// referencing both must be able to resolve which Program it meant.
/// </summary>
public partial class Program
{
    /// <summary>What the skill migration did, said once at the start that did it.</summary>
    public static void ReportSkillMigration(SkillMigration.Report report, string backups)
    {
        if (report.ReplacedByBuiltIn.Count > 0)
        {
            Console.WriteLine(
                $"NOTE: {report.ReplacedByBuiltIn.Count} skill file(s) on disk are now built-in skills "
                + $"from this build: {string.Join(", ", report.ReplacedByBuiltIn)}. The old files are in {backups}.");
        }

        if (report.Imported.Count > 0)
        {
            Console.WriteLine(
                $"NOTE: {report.Imported.Count} skill(s) on disk became custom skills: "
                + $"{string.Join(", ", report.Imported)}. The old files are in {backups}.");
        }

        if (report.NotMigrated.Count > 0)
        {
            Console.WriteLine(
                $"NOTE: {string.Join(", ", report.NotMigrated)} was retired and not migrated. The old "
                + $"files are in {backups}.");
        }

        foreach (var warning in report.Warnings)
        {
            Console.WriteLine($"WARNING: skill migration {warning}");
        }
    }

    /// <summary>
    /// Headless presets whose runs will report `(unknown)` usage, for the startup line.
    ///
    /// NON-MODEL PRESETS ARE EXEMPT. A program has no tokens by construction, so naming it here is a
    /// non-problem stated on every start forever - and a report that always fires is one people stop
    /// reading, which costs the real entries their only reader.
    /// </summary>
    public static IReadOnlyList<string> UnmeasuredHeadlessPresets(IReadOnlyList<AgentDefinition> agents) =>
        agents
            .Where(a => a.Mode == AgentMode.Headless
                        && a.Launch.LanguageModel
                        && a.Launch.UsageFormat is null)
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Select(a => a.Name)
            .ToList();

    /// <summary>
    /// Existing members holding a high-volume subscription on a preset that is now a language
    /// model, for the startup line - reachable only from data written before the rule existed, or
    /// hand-edited in. WARNED ABOUT AND SERVED, never refused: a guard fails in whichever direction
    /// is recoverable, and there is nothing wrong with this data, only with what someone may do
    /// next - the same reasoning `RestoreAsync` follows for an illegal `teams` row.
    /// </summary>
    public static IReadOnlyList<string> FirehoseHolders(
        IReadOnlyList<AgentDefinition> agents, IReadOnlyCollection<TeamSummary> teams)
    {
        // DistinctBy first: two presets whose names differ only by case must not throw out of
        // ToDictionary here. This is a hand-edited `agents.json`'s one door `PUT /api/agents`
        // cannot close, and the method's own contract is WARN AND SERVE, never refuse - an
        // unhandled ArgumentException at startup would stop the Host before the Agents screen
        // that could fix the duplicate is ever served.
        var byName = agents
            .DistinctBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);

        return teams
            .SelectMany(t => t.Containers.Select(c => (Team: t.Name, Container: c)))
            .Where(entry => byName.GetValueOrDefault(entry.Container.Agent) is { Launch.LanguageModel: true })
            .SelectMany(entry => entry.Container.Subscribes
                .Where(EventCatalog.IsHighVolume)
                .Select(type => (entry.Team, entry.Container, Type: type)))
            .OrderBy(entry => entry.Team, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Container.Name, StringComparer.OrdinalIgnoreCase)
            .Select(entry => $"{entry.Team}/{entry.Container.Name} ({entry.Type})")
            .ToList();
    }
}
