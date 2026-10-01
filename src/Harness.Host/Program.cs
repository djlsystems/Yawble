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
    var outcome = await OperatorCommands.TryRunAsync(args, dataRoot, Console.Out);

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
var migrator = new SchemaMigrator(database);
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

var workflowWaits = new MessageWaitRegistry();
var store = new SqliteMessageStore(database, workflowWaits);

builder.Services.AddSingleton<IMessageLog>(store);
builder.Services.AddSingleton<ICursors>(store);
builder.Services.AddSingleton<ISubscriptions>(store);
builder.Services.AddSingleton(workflowWaits);
builder.Services.AddSingleton<IUsageLedger>(new SqliteUsageLedger(database));

// THE OUTCOMES AND THEIR LINKS, in the same file: every person's write lands with its tenant_events
// row in one transaction, written with the tenant log's own columns.
var outcomeStore = new SqliteOutcomeStore(database, TenantAuditRow.AppendAsync);
builder.Services.AddSingleton(outcomeStore);
builder.Services.AddSingleton<IOutcomeStore>(outcomeStore);

// THE GATE, read through a delegate so a person's change applies to the next declaration.
builder.Services.AddSingleton(sp => new OutcomeGate(
    () => sp.GetRequiredService<TenantSettings>().OutcomesRequireForCompletion, outcomeStore));
builder.Services.AddSingleton(new LedgerIdentity(ledgerStart?.InstanceId, ledgerStart?.LedgerStartedAt));
builder.Services.AddSingleton(new KanbanStore(store));

// What each container has accepted and not finished. Without it nothing is durable: a container's
// queue is an in-memory Channel and its cursor advances when a message is OFFERED, so a host
// stopping between the two would drop the work with no redelivery to fall back on and nothing to
// say it had happened.
builder.Services.AddSingleton<IPendingDeliveries>(new SqlitePendingDeliveries(database));
builder.Services.AddSingleton<PendingDeliveriesAtStart>();

// Order carries no schema obligation - SchemaMigrator above has already applied every step
// and set journal_mode on the file. These are readers and writers over a database that exists.
var users = new SqliteUserStore(database);
var principals = new SqlitePrincipalStore(database);

builder.Services.AddSingleton<IUserStore>(users);
builder.Services.AddSingleton<IPrincipalStore>(principals);

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
// is what makes it impossible for the ledger to disagree with the log about what happened.
builder.Services.AddSingleton<ILedger>(new SqliteLedger(database));
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
var pluginCatalog = new PluginCatalog(Path.Combine(dataRoot, "plugins"));
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
// resolved by logical key from the Host's own environment at each run - where `secret set` values
// already arrive. Swapping the resolver for an encrypted store later changes nothing else.
builder.Services.AddSingleton<ISecretStore>(new EnvironmentSecretStore());
builder.Services.AddSingleton<IPluginMemberSettingsStore>(new SqlitePluginMemberSettings(database));

// CONNECTIONS: OAuth accounts the Host holds for plugins (see Connections). The client secret and
// every token are Data Protection ciphertext under `<dataRoot>/keys`; the provider is reached only
// through IOAuthEndpoints, which the tests replace.
builder.Services.AddSingleton(sp => new ConnectionStore(database, sp.GetRequiredService<IDataProtectionProvider>()));
builder.Services.AddHttpClient(nameof(HttpOAuthEndpoints), client => client.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddSingleton<IOAuthEndpoints>(sp => new HttpOAuthEndpoints(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(HttpOAuthEndpoints))));
builder.Services.AddSingleton(sp => new Connections(
    sp.GetRequiredService<ConnectionStore>(), sp.GetRequiredService<IOAuthEndpoints>(), sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<IUserStore>()));
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

// The operator's tags for a built-in are read through the setting on every use.
builder.Services.AddSingleton(new AgentCatalog(loadedCatalog, () => tenantSettings.AgentTags));

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

        // THE AGENT a package's agent members run when it names none: the first headless model
        // preset installed on this machine, else the first in the catalog. The wizard may name one.
        () =>
        {
            var headless = agents.Definitions.Where(d => d.Mode == AgentMode.Headless).ToList();
            return (headless.FirstOrDefault(d => d.Launch.LanguageModel && probe.Probe(d).State is null)
                ?? headless.FirstOrDefault(d => d.Launch.LanguageModel)
                ?? headless.FirstOrDefault())?.Name;
        },
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
// Constructed rather than resolved by type: its three parameters are the injection seams the specs
// drive (a `fileExists`, a clock, the cache window) and none of them is a service, so leaving the
// container to choose a constructor would either fail to find one or quietly pick the wrong shape
// the day another is added.
builder.Services.AddSingleton(new AgentInstallProbe());
// The one path from the progress ROUTE to whatever is currently running that member. Singleton
// because both ends have to be looking at the same object for a heartbeat to mean anything.
builder.Services.AddSingleton<RunHeartbeat>();
// WRAPPED, AND THE WRAPPING IS LOAD-BEARING. `ProcessAgentRunner` spawns the process and reads what
// it wrote; `CredentialUseRunner` answers what the SERVER saw while it ran, which is how a run that
// did nothing is told from one that decided there was nothing to do. Unwrap this and every result
// reports "not measured" - which AgentResult.Succeeded treats as success, silently, which is the
// defect it exists to catch. `AgentRunnerCompositionTests` asserts what comes out of here.
// Who agent children run as, decided once here and logged at start. `agent` when the
// Host can switch to it, the Host's own user otherwise - see AgentLaunchUser.
builder.Services.AddSingleton(_ => AgentLaunchUser.Resolve(
    builder.Configuration["Agents:RunAs"] ?? Environment.GetEnvironmentVariable("HARNESS_AGENT_USER")));
// The runs in flight, for the live route. The runner records; nothing is stored.
builder.Services.AddSingleton<LiveRuns>();
// WHO MAY USE AN AGENT CLI'S SHARED INSTALL: member runs share it, a platform update has it alone,
// and a launch that arrives during an update waits for it. One per Host, shared by the runner, the
// Concierge's launch and the updater, or the hold holds nothing.
builder.Services.AddSingleton<AgentUpdateGate>();
builder.Services.AddSingleton(sp => new AgentCliUpdater(
    sp.GetRequiredService<AgentCatalog>(),
    sp.GetRequiredService<AgentUpdateGate>(),
    sp.GetRequiredService<AgentLaunchUser>(),
    dataRoot));
builder.Services.AddSingleton<ProcessAgentRunner>();
builder.Services.AddSingleton<IAgentRunner>(sp => new CredentialUseRunner(
    sp.GetRequiredService<ProcessAgentRunner>(),
    sp.GetRequiredService<IPrincipalStore>(),
    sp.GetRequiredService<AgentCatalog>()));

// WHAT A MEMBER'S REPORT DOES - row, mark, card push, idle clock - written once, for the MCP routes
// and for a plugin member's stdout alike.
builder.Services.AddSingleton<MemberReports>();
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
    sp.GetRequiredService<RunHeartbeat>(),
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

// Instance-wide, shared across users. 0 is unlimited. Absent means 4, which is a desktop-sized
// default rather than a guess at a server. The container's cgroup is a blast radius; this is
// the scheduler. Held work is listed by GET /api/wip.
//
// `wip.maxRunning`: settable at runtime. One place reads it - here, at start, and on every
// change through `WipLedger.SetMax`, which applies it under the ledger's lock and evicts nothing.
var wip = new WipLedger(tenantSettings.WipMaxRunning);
tenantSettings.Changed += name =>
{
    if (name == TenantSettings.WipMaxRunningName) wip.SetMax(tenantSettings.WipMaxRunning);
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

    // AN EVENT OR FOLDER TRIGGER'S DAILY TOKEN CAP, asked by the pump before it fires one. Late-
    // bound for the reason above: TriggerCost's tenant log is registered below this line.
    triggerCapped: (trigger, member, cause, ct) => sp.GetRequiredService<TriggerCost>()
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

    onRunEnding: async (member, causation, succeeded, ct) =>
    {
        // THE PUBLISH IGNORES `succeeded` AND THAT IS THE WHOLE POINT: a run killed between
        // doing the work and finishing its turn is the case this exists for, so its branch
        // matters MORE than a clean run's, not less.
        await TerminalPublish.OnRunEndingAsync(
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
            activity: () => sp.GetRequiredService<GitRunner>().Activity);

        // A MANAGER THAT MOVED ITS CLONE'S DEFAULT BRANCH IS REPORTED, BEFORE THE TERMINAL ROW, so
        // the report lands on its card and in the feed. Reported only; nothing is reset. See
        // `DefaultBranchMove`.
        await DefaultBranchMove.OnRunEndingAsync(
            sp.GetRequiredService<TeamRegistry>(),
            sp.GetRequiredService<TeamPaths>(),
            sp.GetRequiredService<GitRunner>(),
            sp.GetRequiredService<IMessageLog>(),
            member,
            causation,
            ct);

        await sp.GetRequiredService<IdleWorkflowOffer>()
            .OnRunEndingAsync(member, causation, succeeded, ct);

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
        return await sp.GetRequiredService<UndeclarableWorkflows>()
                .OnRunEndingAsync(member, causation, succeeded, ct)
            || (!member.Equals(new ContainerId(member.Team, TeamRegistry.DefaultManagerName))
                && await WorkflowDeclaration.DeclaredAsync(sp.GetRequiredService<IMessageLog>(), causation, ct));
    }));
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

// The instance's git identity (GIT_AUTHOR_NAME / GIT_AUTHOR_EMAIL, set with the operator CLI's `secret set`)
// and the one place a clone is brought in line with its contributor settings.
builder.Services.AddSingleton(new InstanceGitIdentity(builder.Configuration));
builder.Services.AddSingleton(sp => new ContributorClone(
    sp.GetRequiredService<GitRunner>(), sp.GetRequiredService<InstanceGitIdentity>()));

// THE PLATFORM MAKES A TEAM'S MAIN CLONE. Wired here rather than defaulted inside TeamRegistry,
// because a seam that can be omitted is one something quietly omits - and the omission here looks
// like a team with a repository and nothing at its path.
//
builder.Services.AddSingleton<IRepoClone>(sp => new RepoClone(
    sp.GetRequiredService<GitRunner>(), localRepos: sp.GetRequiredService<LocalRepos>()));

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
        sp.GetRequiredService<BacklogTipRecorder>().RecordTipAsync(team, repo, sha, causation, ct)));

builder.Services.AddSingleton(sp => new BacklogTipRecorder(
    sp.GetRequiredService<IBacklogStore>(),
    sp.GetRequiredService<IMessageLog>(),
    sp.GetRequiredService<TeamRegistry>(),
    sp.GetRequiredService<TeamPaths>(),
    sp.GetRequiredService<GitRunner>()));

builder.Services.AddSingleton(sp => new TeamAccess(
    sp.GetRequiredService<IUserStore>(),
    sp.GetRequiredService<IPrincipalStore>(),
    sp.GetRequiredService<TeamRegistry>()));

builder.Services.AddSingleton<TeamListPush>();

// THE ONE REMOVAL a team root, a member's workspace and a reset's folders go through: the marker
// last, agent content removed as the agent through its launch prefix, and whatever remains recorded
// to be retried. See FolderRemoval.
builder.Services.AddSingleton(sp => new FolderRemoval(
    sp.GetRequiredService<AgentLaunchUser>(),
    sp.GetRequiredService<IUnfinishedRemovals>()));

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
    sp.GetRequiredService<ITeamSolutionStore>()));

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
    sp.GetRequiredService<RepositoryReset>()));

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

builder.Services.AddSingleton<IPtyEngine, PortaPtyEngine>();
builder.Services.AddSingleton(sp => new ConciergeLaunchFactory(
    sp.GetRequiredService<TeamPaths>(),
    memberBaseAddress,
    sp.GetRequiredService<IPrincipalStore>(),
    sp.GetRequiredService<AgentCatalog>(),
    sp.GetRequiredService<SkillDirectory>(),
    sp.GetRequiredService<AgentLaunchUser>(),
    sp.GetRequiredService<AgentUpdateGate>()));
builder.Services.AddSingleton(sp =>
{
    // Resolved ONCE, here, and captured - never re-resolved inside the delegates. The revoke runs
    // from ConciergeSessionStore.DisposeAsync during host shutdown, by which time the service
    // provider is already disposed, so a `sp.GetRequiredService` in there throws
    // ObjectDisposedException out of disposal and takes every live session's cleanup with it.
    var launcher = sp.GetRequiredService<ConciergeLaunchFactory>();
    var teams = sp.GetRequiredService<TeamRegistry>();

    // The LOGIN, for the workspace's directory name. Resolved here rather than inside the
    // factory for teamLabel's stated reason - the factory has no store of its own - and captured
    // once for this whole block's stated reason: a GetRequiredService inside the delegate throws
    // out of shutdown.
    var accounts = sp.GetRequiredService<IUserStore>();

    // For the agent the Concierge runs when nobody has chosen one - see ConciergeAgentDefault.
    var catalog = sp.GetRequiredService<AgentCatalog>();
    var probe = sp.GetRequiredService<AgentAuthProbe>();

    return new ConciergeSessionStore(
        sp.GetRequiredService<IPtyEngine>(),
        // Console config belongs to the TEAM, not a global setting: an embedded console for one
        // app needs its own CLI and key, and another app's needs something else. The PERSON comes
        // from the key rather than from config, because it is whoever opened this one.
        // The LABEL, resolved here rather than inside the factory - it has no TeamRegistry of
        // its own, and this delegate already holds one. LabelFor falls back to the identifier for
        // a team nobody has relabelled, so a never-relabelled team's console reads its identifier.
        async (key, team, publicUrl, ct) =>
        {
            // THE CONCIERGE, ASKED WITHOUT NAMING A TEAM. ConciergeFor(team) throws when the
            // registry holds nothing, which is the empty instance GET /api/concierge/ws exists
            // to serve. Concierge() is already the tenant-wide reader.
            var settings = teams.Concierge();
            var stored = string.IsNullOrWhiteSpace(team) ? null : teams.ExistingName(team);

            // BY ID, because that is what the session key carries and an email can change under a
            // live session - a rename is a case to survive rather than an edge.
            //
            // NOT FALLING BACK TO THE ID when the account cannot be read, which it never should
            // be: the route has already refused anything but an authenticated User, and the mint
            // below would fail its owner foreign key anyway. An empty login lands on the
            // `user-<suffix>` fallback instead, and that choice is deliberate - a name is claimed
            // by its marker for good, so falling back to the id would let one unreadable lookup
            // pin this person to the id-named folder the migration exists to retire.
            var account = await accounts.FindByIdAsync(key.User, ct);

            return await launcher.ForAsync(
                stored ?? "",
                stored is null ? "this instance" : teams.LabelFor(stored),
                key.User,
                account?.Email ?? string.Empty,
                await ConciergeAgentDefault.ResolveAsync(settings.Agent, catalog, probe, ct),
                teams.EnvFor(stored ?? ""),
                SteeringFile.Read(dataRoot, key.User),
                publicUrl,
                ct);
        },
        (key, ct) => launcher.RevokeAsync(key.User, ct));
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


// How long a console may sit unattended before it is ended, and how often that is checked.
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

builder.Services.AddSingleton<IHostedService>(sp => new ConciergeReaper(
    sp.GetRequiredService<ConciergeSessionStore>(),
    () => tenantSettings.ConciergeIdleTimeout,
    consoleSweep,
    sp.GetRequiredService<ILogger<ConciergeReaper>>()));
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
// retention never touches. `Backups:Keep` is how many daily copies survive (default 7).
var backupsEnabled = !bool.TryParse(builder.Configuration["Backups:Enabled"], out var configuredBackups)
    || configuredBackups;
if (backupsEnabled)
{
    var dailyBackup = new DailyBackup(
        database,
        builder.Configuration["Backups:Directory"] is { Length: > 0 } backupDirectory
            ? backupDirectory
            : Path.Combine(dataRoot, "backups"),
        int.TryParse(builder.Configuration["Backups:Keep"], out var backupKeep)
            ? backupKeep
            : DailyBackup.DefaultKeep);
    builder.Services.AddSingleton(dailyBackup);
    builder.Services.AddSingleton<IHostedService>(sp => new DailyBackupRunner(
        dailyBackup,
        TimeSpan.FromHours(1),
        sp.GetRequiredService<ILogger<DailyBackupRunner>>()));
}
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
    sp.GetRequiredService<ILogger<IdleWorkflowOffer>>()));
builder.Services.AddSingleton<UndeclarableWorkflows>();
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

builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<AgentAuthProbe>();

// THE PRE-FLIGHT: what each preset's CLI would load, listed by the CLI itself as the agent user,
// once the Host is serving and again after every catalog save. Never on the start path.
builder.Services.AddSingleton<IListingRunner>(sp => new CliListingRunner(sp.GetRequiredService<AgentLaunchUser>()));
builder.Services.AddSingleton(sp => new AgentToolPreflight(
    sp.GetRequiredService<AgentCatalog>(), sp.GetRequiredService<IListingRunner>(), dataRoot,
    sp.GetRequiredService<ILogger<AgentToolPreflight>>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<AgentToolPreflight>());
builder.Services.AddMcpServer()
    .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
    .WithTools<PlatformMcpTools>();

var app = builder.Build();
app.Lifetime.ApplicationStopped.Register(pluginEvents.Dispose);

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
if (resumed.Reoffered > 0 || resumed.Interrupted > 0)
{
    Console.WriteLine(
        $"Resumed {resumed.Reoffered} queued item(s) and reported {resumed.Interrupted} "
        + "interrupted run(s) from the last stop.");
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

// AFTER UseAuthorization, which is what populates context.User for a key-authenticated request.
PrincipalLogScope.Use(app);

// NoPermitRequired, and not an exemption from anything: every tool relays to an /api route with
// the caller's own X-Api-Key (PlatformMcpTools.SendAsync), so TeamGate and PermitGate judge each
// call on that route's own marker. RouteMarkerTests requires a marker here like everywhere else.
app.MapMcp("/mcp").NoPermitRequired();
SurfaceEndpoints.Map(app, dataRoot);
SiteEndpoints.Map(app);
SiteApiEndpoints.Map(app);
TenantSettingsEndpoints.Map(app);
LedgerEndpoints.Map(app);
OutcomeEndpoints.Map(app);
HealthEndpoints.Map(app, database, dataRoot);
VersionEndpoints.Map(app);
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
containers.Changed += snapshot =>
    _ = hub.Clients.Group(snapshot.Team).SendAsync("containerChanged", snapshot);
registryForPush.TeamChanged += team =>
    _ = hub.Clients.Group(team.Id).SendAsync("teamChanged", team);

// RequireAuthorization, not a null check in the handler: the policy is what populates context.User
// for a key-authenticated request while the cookie is the default scheme. A handler that only
// checked context.User would answer 401 to a valid key.
// Permits travel here for DISPLAY, and governing access is not one of them: this set is a claim
// minted at login into a fourteen-day cookie and can be that stale. A person holds every permit;
// the SPA reads this rather than knowing that. Do not read it as authority anywhere, here or in
// the SPA.
app.MapGet("/api/auth/me", (HttpContext context) =>
    PrincipalClaims.From(context.User) is { } principal
        ? Results.Ok(new
        {
            principal.Id,
            kind = principal.Kind.ToString(),
            email = context.User.FindFirstValue(ClaimTypes.Email),
            permits = principal.Permits,
        })
        : Results.Unauthorized())
    .RequireAuthorization()
    .WithTags("Account")
    .NoPermitRequired()
    .WithSummary("Who the caller is")
    .WithDescription(
        "Identifies whoever is holding this session or key: their id, whether the principal is a "
        + "person or a machine, and their permits.\n\n"
        + "**`permits` is for DISPLAY only.** It is a claim minted into a fourteen-day cookie at "
        + "sign-in and never reissued, so it can be that stale. What actually governs is read from "
        + "the database on every request. The browser uses this to grey out a control; treating it "
        + "as authority anywhere is a bug.");

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
KanbanEndpoints.Map(app);
BacklogEndpoints.Map(app);
BacklogEndpoints.MapTeamScoped(app);
DiagnosticsEndpoints.Map(app);
LiveViewEndpoints.Map(app);

static bool TryFindingTeam(string subject, out string team)
{
    team = "";
    if (string.IsNullOrWhiteSpace(subject)) return false;

    var slash = subject.IndexOf('/');
    team = slash < 0 ? subject : subject[..slash];
    return team.Length > 0;
}

static async Task<IReadOnlyList<object>> ReadActiveFindingsAsync(
    ITenantLog tenantLog,
    IReadOnlySet<string> visibleTeams,
    CancellationToken ct)
{
    var reportKinds = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [TenantActions.SweepRunningWithoutProgress] = nameof(QuietTeamSweepFindingKind.RunningWithoutProgress),
        [TenantActions.SweepPendingNeverTerminal] = nameof(QuietTeamSweepFindingKind.PendingAcceptedWithoutTerminal),
        [TenantActions.SweepQuietTeam] = nameof(QuietTeamSweepFindingKind.QuietTeam),
        [TenantActions.SweepWrapUpNotPushed] = nameof(QuietTeamSweepFindingKind.WrapUpNotPushed),
    };
    var clearedToReport = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [TenantActions.SweepRunningWithoutProgressCleared] = TenantActions.SweepRunningWithoutProgress,
        [TenantActions.SweepPendingNeverTerminalCleared] = TenantActions.SweepPendingNeverTerminal,
        [TenantActions.SweepQuietTeamCleared] = TenantActions.SweepQuietTeam,
        [TenantActions.SweepWrapUpNotPushedCleared] = TenantActions.SweepWrapUpNotPushed,
    };
    var newestBySubject = new Dictionary<(string Action, string Subject), (long Seq, bool IsReport, string Team, string Kind, string Subject)>();
    var rows = await tenantLog.FindLatestBySubjectAsync(
        [.. reportKinds.Keys, .. clearedToReport.Keys],
        ct);

    foreach (var row in rows)
    {
        if (row.Subject is not { } subject || !TryFindingTeam(subject, out var team))
        {
            continue;
        }

        if (!visibleTeams.Contains(team))
        {
            continue;
        }

        string reportAction;
        bool isReport;

        if (reportKinds.ContainsKey(row.Action))
        {
            reportAction = row.Action;
            isReport = true;
        }
        else if (clearedToReport.TryGetValue(row.Action, out var mapped))
        {
            reportAction = mapped;
            isReport = false;
        }
        else
        {
            continue;
        }

        var key = (reportAction, subject);
        if (!newestBySubject.TryGetValue(key, out var current) || row.Seq > current.Seq)
        {
            newestBySubject[key] = (row.Seq, isReport, team, reportKinds[reportAction], subject);
        }
    }

    return newestBySubject.Values
        .Where(row => row.IsReport)
        .OrderBy(row => row.Team, StringComparer.Ordinal)
        .ThenBy(row => row.Kind, StringComparer.Ordinal)
        .ThenBy(row => row.Subject, StringComparer.Ordinal)
        .Select(row => (object)new
        {
            team = row.Team,
            kind = row.Kind,
        })
        .ToArray();
}

static string PauseNoticeFor(string teamLabel) =>
    $"Team '{teamLabel}' is paused. This instruction was queued and will run when the team resumes.";

// Carries no {team} route value, so TeamGate's rule 1 passes it through unchecked - filtered here
// instead of refused there. TeamRegistry.Overview() returns `object` and All() is unfiltered, so
// the filter has to happen at the route rather than inside the registry.
app.MapGet("/api/overview", async (
    HttpContext context, TeamRegistry teams, TeamAccess access, ITenantLog tenantLog, CancellationToken ct) =>
{
    if (PrincipalClaims.From(context.User) is not { } principal) return Results.Unauthorized();

    var effective = await access.EffectiveTeamsAsync(principal, ct);
    var visibleTeams = effective.ToHashSet(StringComparer.OrdinalIgnoreCase);
    var activeFindings = await ReadActiveFindingsAsync(tenantLog, visibleTeams, ct);

    // WHERE A TEAM'S FILES ARE is shown to a person and withheld from a machine principal, and
    // this is the one route that answers it - the Console reads its teams from here, and Team
    // Settings shows the root read-only off the team object it already holds. TeamRegistry.All
    // defaults the flag OFF so every other route that projects a team discloses nothing without
    // deciding to.
    var withRoot = principal.Kind == PrincipalKind.User;

    return Results.Ok(new
    {
        teams = teams.All(withRoot).Where(t => visibleTeams.Contains(t.Id)),
        managerName = TeamRegistry.DefaultManagerName,
        activeFindings,

        // **THE CEILING A TEAM CANNOT RAISE, SENT SO IT IS NOT INVISIBLE.**
        //
        // It is instance-wide, so it rides the one payload the Console already fetches once rather
        // than being repeated per team. It is NOT settable from here and must not become so - a
        // bound a team can raise is not a backstop: a `{team}`-gated ceiling is one anyone on the
        // team could raise.
        //
        // BUT UNREACHABLE AND INVISIBLE ARE DIFFERENT THINGS. A team stopped by a number that
        // appears on no screen, in no dialog and in no log until the refusal, while its tile shows
        // a fraction of a bound that is not operating, cannot tell why it stopped.
        //
        // 0 or absent means no instance ceiling, matching how the team figure reads its own zero.
        //
        // SETTABLE, so read now rather than at start: `workflow.spendLimit`.
        workflowSpendLimit = tenantSettings.WorkflowSpendLimit,
    });
})
    .WithTags("Teams")
    .RequirePermit(Permits.Read)
    .WithSummary("The teams this caller can see")
    .WithDescription(
        "The Console's starting point: every team the caller reaches, each with its Agent Containers and "
        + "their current state, plus the default manager name.\n\n"
        + "A team carries TWO names and they are not interchangeable. `name` is what a person "
        + "chose and what you should render; `id` is the identifier that keys the documents "
        + "folder and half of every container's identity, and it is what every other "
        + "route in this API expects in its path. Getting them the wrong way round points a request "
        + "at a team that does not exist, and nothing fails loudly.\n\n"
        + "Filtered to the caller's effective teams. Teams and their members are restored from the "
        + "database at startup, so a restart does not empty this list - an empty one means "
        + "the caller reaches no team, not that the host has just come up.");

// WHICH TEAM THIS PERSON IS WORKING ON. One fact, one owner, read per command.
//
// NEITHER OF THESE TWO ROUTES DECLARES {team}, SO NEITHER IS INSIDE TeamGate - the gate keys on the
// route value, which couples it to structure rather than spelling. That is why the PUT below asks
// the gate's own questions itself, in the gate's own order. It is also why they cannot be built the
// other way round: a route that let the SERVER default a team would have to be team-scoped without
// a {team} segment, and an ungated team-scoped route is the shape this codebase refuses.
//
// The value lives on the PERSON, never on the credential, because a browser and the Concierge it
// drives are two principals for one person - keyed on either, the browser would write something the
// agent never reads.
app.MapGet("/api/me/current-team", async (
    HttpContext context, TeamRegistry teams, IUserStore users, TeamDocuments documents,
    CancellationToken ct) =>
{
    if (PrincipalClaims.From(context.User) is not { } principal) return Results.Unauthorized();
    if (CurrentTeam.PersonBehind(principal) is not { } person) return CurrentTeam.NotAPersonsDoor;

    var stored = await users.CurrentTeamForAsync(person, ct);

    if (stored is null)
    {
        return Results.Ok(
            new { team = (string?)null, name = (string?)null, kind = principal.Kind.ToString(), shared = (string?)null });
    }

    // The team may have been deleted or relabelled since it was chosen. Answered rather than cleared:
    // a caller that knows the name it is pointed at can say what went, where a silently-emptied
    // value looks like the person never chose one.
    var canonical = teams.ExistingName(stored);

    return Results.Ok(new
    {
        team = canonical ?? stored,
        name = canonical is null ? null : teams.LabelFor(canonical),

        // THE CALLER'S PRINCIPAL KIND, so a caller knows whether it may use an implicit team.
        // A Concierge is deliberately given no current team of its own (HARNESS_TEAM absent),
        // and must name the team on team-scoped tools. Only the server knows the principal kind;
        // this field is the single authority for that decision.
        kind = principal.Kind.ToString(),

        // WHERE THIS TEAM KEEPS ITS SHARED WORK, and this is a Concierge's only route to it: an
        // environment variable written once at spawn would be frozen, and this is DERIVED from the
        // current team.
        //
        // FOR A CONCIERGE ONLY, and that bound is a disclosure decision rather than tidiness. This
        // is an absolute path on the Host's filesystem, which `/api/overview` shows to a person
        // only. A Concierge is exempt because it is the one principal that works across every
        // team it opens and needs this value for each - so answering it here is no new disclosure,
        // where answering a browser or a pasted API key would be. Through TeamDocuments rather than
        // composed here, so one place still decides where a team's documents live.
        shared = canonical is not null && principal.Kind == PrincipalKind.TenantConcierge
            ? documents.RootFor(canonical)
            : null,
    });
})
    .WithTags("Teams")
    .RequirePermit(Permits.Read)
    .WithSummary("Which team this caller is currently working on")
    .WithDescription(
        "`team` is NULL when no team is active, which is a real state rather than a "
        + "misconfiguration - it is where somebody on the Teams tab genuinely is.\n\n"
        + "`name` is the label to render and `team` is the identifier to address with. `name` is "
        + "NULL when the stored team no longer exists, and `team` still names it so a caller can "
        + "say which team went.\n\n"
        + "Refused for a container: its team is half its identity and arrives in HARNESS_TEAM.");

app.MapPut("/api/me/current-team", async (
    SetCurrentTeam request, HttpContext context, TeamRegistry teams, TeamAccess access,
    IUserStore users, IHubContext<ContainerHub> hub, CancellationToken ct) =>
{
    if (PrincipalClaims.From(context.User) is not { } principal) return Results.Unauthorized();
    if (CurrentTeam.PersonBehind(principal) is not { } person) return CurrentTeam.NotAPersonsDoor;

    // Clearing needs no authority beyond being a person: "no team is active" names nobody.
    if (string.IsNullOrWhiteSpace(request.Team))
    {
        await users.SetCurrentTeamAsync(person, null, ct);
        await CurrentTeam.AnnounceAsync(hub, person, null, null, ct);

        return Results.NoContent();
    }

    // TeamGate's rule, for its reasons: a person reaches every team and gets the handler's own 404
    // for a name that does not exist; a machine principal is bound to its team and is refused a
    // team that is not its own with the same body a missing team would get.
    if (principal.Kind != PrincipalKind.User
        && !await access.MayActOnAsync(principal, request.Team, ct))
    {
        return Results.Content(
            TeamGate.NoSuchTeam, "application/json", statusCode: StatusCodes.Status403Forbidden);
    }

    // Those callers reach the handler and get its ordinary 404, exactly as they do on a
    // {team}-scoped route.
    if (teams.ExistingName(request.Team) is not { } canonical)
    {
        return Results.NotFound(new { error = $"No team '{request.Team}'." });
    }

    // The CANONICAL spelling, never the caller's. Team identity is case-insensitive and every
    // reader compares ordinally, so storing what was typed makes one team two.
    await users.SetCurrentTeamAsync(person, canonical, ct);
    await CurrentTeam.AnnounceAsync(hub, person, canonical, teams.LabelFor(canonical), ct);

    return Results.NoContent();
})
    .WithTags("Teams")
    .RequirePermit(Permits.Read)
    .WithSummary("Point this caller at a team, or at none")
    .WithDescription(
        "A null or empty `team` clears it, which is what the Teams tab does: no team is active.\n\n"
        + "403 with the gate's own words for a team the caller does not reach - byte-identical to a "
        + "team that does not exist, so probing cannot tell the two apart. A person naming a "
        + "team that does not exist gets 404 instead, because for them 'not yours' is not a state.\n\n"
        + "Refused for a container: a container's team is half its identity and must not move.");

app.MapGet("/api/teams", async (
    HttpContext context, TeamRegistry teams, TeamAccess access, CancellationToken ct) =>
{
    if (PrincipalClaims.From(context.User) is not { } principal) return Results.Unauthorized();

    var effective = await access.EffectiveTeamsAsync(principal, ct);
    var withRoot = principal.Kind == PrincipalKind.User;

    return Results.Ok(teams.All(withRoot).Where(t => effective.Contains(t.Id)));
})
    .WithTags("Teams")
    .RequirePermit(Permits.Read)
    .WithSummary("List teams this caller can see")
    .WithDescription(
        "Returns this caller's visible teams, already scoped by TeamAccess. A person sees every "
        + "team that exists; a machine principal sees the teams its identity reaches.\n\n"
        + "A team carries two names: `name` is what a person reads, `id` is what routes use. This "
        + "route returns both.");

app.MapGet("/api/events", () => Results.Ok(EventCatalog.WithPlugins()))
    .WithSummary("Every event type this platform can publish.")
    .WithDescription(
        "The catalog a trigger picker is built from. It describes the platform rather than any "
        + "team's data, which is why it needs no permit and declares no {team}. After the "
        + "platform's own types come those the installed plugins declare, `plugin.<id>.<suffix>`, "
        + "with `publisher` `Plugin`.")
    .NoPermitRequired();

// `name` here is what a PERSON typed - free text, spaces and accents included. The identifier
// everything else keys on is derived from it inside TeamRegistry and is never asked for, never
// returned as a separate field and never shown. See TeamRegistry.CreateAsync.
// The tenant log. PEOPLE ONLY: it names every account on the instance and what was done to them,
// which is a roster an agent enumerating it would be reading for no reason of its own.
app.MapGet("/api/tenant-log", async (
    [Description(
        "Only rows with a seq below this one - the last row's seq from the previous page. Omit it "
        + "for the newest page.")] long? before,
    [Description("How many to return. Default 50, clamped to 1..200.")] int? take,
    ITenantLog log, CancellationToken ct) =>
    Results.Ok(await log.ReadAsync(before, Math.Clamp(take ?? 50, 1, ITenantLog.MaxTake), ct)))
    .WithTags("Tenant")
    .HumansOnly()
    .WithSummary("Read the tenant log")
    .WithDescription(
        "Administrative acts, newest first: who signed in, who created or deleted a team, who "
        + "changed an account's access. **Not the message log** - that is the causal stream Agent "
        + "Containers publish to and read from, holds only `container.*` types, and is what a "
        + "member's own activity feed shows.\n\n"
        + "This log OUTLIVES its subjects deliberately. The row saying a team was deleted survives "
        + "the team, and the row saying an account was removed survives the account, which is why "
        + "the actor's email and the subject's name are stored on the row rather than joined.\n\n"
        + "Answers `{ events, total }`, newest first. Paged by a seq cursor: pass the last row's "
        + "`seq` as `before` for the next, older page; a page shorter than `take` is the end. "
        + "A row appended while somebody scrolls lands above the first page and never shifts an "
        + "older one. `total` counts the whole log regardless of the cursor - a caption.\n\n"
        + "It never contains a credential, a password hash or an Agent definition's `env`.\n\n"
        + "**A person's action.**");

app.MapPost("/api/teams", async (
    CreateTeam request, TeamRegistry teams, TenantLogging audit, HttpContext context,
    AgentInstallProbe probe, AgentCatalog catalog, TeamListPush listPush, TeamRepoSetup repoSetup,
    CancellationToken ct) =>
{
    var name = (request.Name ?? "").Trim();

    if (name.Length == 0) return Results.BadRequest(new { error = "A team needs a name." });

    if (name.Length > TeamRegistry.MaximumLabelLength)
    {
        return Results.BadRequest(new
        {
            error = $"A team name cannot be longer than {TeamRegistry.MaximumLabelLength} characters.",
        });
    }

    // REFUSED ON THIS ROUTE TOO, not only on `PUT /api/teams/{team}/budget`: a caller that can
    // reach one can reach the other, so refusing only on the setter would close one instance
    // rather than the class. Checked BEFORE `CreateAsync`, beside the name
    // checks above - a refused create must leave nothing behind, where applying the value only
    // AFTER creation would mean a team already exists by the time a negative figure is found.
    if (request.BudgetTokens is < 0)
    {
        return Results.BadRequest(new
        {
            error = "budgetTokens cannot be negative. Use 0 for unlimited, or omit it to use the "
                + "instance figure.",
        });
    }

    // EVERY URL IS READ BEFORE ANYTHING IS CREATED (B001F): one `git ls-remote` cannot read is
    // refused with the choices this caller may take, and no team, row, folder or clone is left.
    // What can be refused without the network is refused first: a URL or upstream that is not one,
    // with the 400 it always had. The same path as backlog dispatch-to-new.
    NewTeamRepos newRepos;
    try
    {
        newRepos = await repoSetup.PlanNewTeamAsync(
            request.Repos, request.Upstreams, request.RepoChoices,
            person: PrincipalClaims.From(context.User) is { Kind: PrincipalKind.User }, request.LocalRepository, ct);
    }
    catch (RepoSetupRefusedException refused)
    {
        return Results.Json(refused.Body, statusCode: refused.Status);
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }

    var teamCreated = false;

    try
    {
        var created = await teams.CreateAsync(
            // VERBATIM, no `??`. Both defaults named a catalog entry a person may rename,
            // and `CreateAsync` refuses a blank one by name rather than substituting anything.
            name, request.Agent ?? "",
            request.AdditionalInstructions,
            request.MemberAgent, request.MemberAgents, request.Root, newRepos.Repos,

            // CARRIED IN THIS SAME REQUEST rather than by a client-side follow-up call to the PUT
            // route. A create that answered 201 and then failed its second call would leave a team
            // running under a figure nobody chose, for the window between them - and the window is
            // exactly when the Manager's first wake happens.
            budgetTokens: request.BudgetTokens,
            ct: ct,
            upstreams: request.Upstreams,

            // ONE UNIT WITH THE TEAM: made once every check on the team has passed and before
            // anything of it is written, so a repository that cannot be made refuses the create.
            addRepo: newRepos.AddRepoAsync);
        teamCreated = true;

        await newRepos.LogAsync(audit, context, created.Id, ct);
        var localRepository = newRepos.LocalRepository;
        var createdOnGitHub = newRepos.CreatedOnGitHub;

        var unresolvedList = new List<object>();
        
        // Probe manager (in Containers)
        foreach (var container in created.Containers)
        {
            var definition = catalog.Definition(container.Agent);
            if (definition is not null)
            {
                var installation = probe.Probe(definition);
                if (installation.State is not null)
                {
                    unresolvedList.Add(new { agent = installation.Agent, command = installation.Command, message = installation.Message });
                }
            }
        }
        
        // Probe the Concierge -- WHICH THIS TEAM DID NOT CHOOSE. It is the tenant's, one for the
        // whole instance, and the summary carries it because that is what this team will open. Worth
        // probing all the same: the answer is about the machine, and the moment a person is looking
        // at a team they just made is a reasonable moment to learn the door is not installed.
        if (!string.IsNullOrEmpty(created.Concierge))
        {
            var definition = catalog.Definition(created.Concierge);
            if (definition is not null)
            {
                var installation = probe.Probe(definition);
                if (installation.State is not null)
                {
                    unresolvedList.Add(new { agent = installation.Agent, command = installation.Command, message = installation.Message });
                }
            }
        }
        
        // Probe member-agents allowlist
        if (created.MemberAgents is not null)
        {
            foreach (var agent in created.MemberAgents)
            {
                var definition = catalog.Definition(agent);
                if (definition is not null)
                {
                    var installation = probe.Probe(definition);
                    if (installation.State is not null)
                    {
                        unresolvedList.Add(new { agent = installation.Agent, command = installation.Command, message = installation.Message });
                    }
                }
            }
        }

        // THE DISPLACED DOCUMENTS FOLDER, WHEN THERE WAS ONE, and this row is what makes
        // "reported, not discovered" durable. Team ids are reusable and documents outlive their
        // team, so a team created with a dead team's identifier meets its predecessor's folder;
        // the create moves that folder aside rather than handing it over, and the only way anybody
        // finds out later is a row that says so.
        var retiredDocuments = teams.RetiredDocumentsFor(created.Id);

        await audit.WriteAsync(
            context, TenantActions.TeamCreated, created.Id, created.Name,
            new
            {
                manager = created.Containers.FirstOrDefault()?.Agent,
                Concierge = created.Concierge,
                retiredDocuments,
                localRepository = localRepository is null
                    ? null
                    : new { name = localRepository.Name, reference = localRepository.Reference, created = localRepository.Created },
                createdOnGitHub = createdOnGitHub.Count == 0 ? null : createdOnGitHub,
            },
            ct);

        await listPush.AnnounceCreatedAsync(created.Id, ct);

        // Serialize created to JsonNode and add unresolvedAgents
        var node = JsonSerializer.SerializeToNode(created, new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } });
        if (node is JsonObject obj)
        {
            obj["unresolvedAgents"] = JsonSerializer.SerializeToNode(unresolvedList, new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } });

            // Present only when something WAS displaced, so a client can say it once rather than
            // render an empty row on every create. See the tenant log line above for why it is
            // reported at all.
            if (retiredDocuments is not null) obj["retiredDocuments"] = retiredDocuments;

            if (localRepository is not null)
            {
                obj["localRepository"] = new JsonObject
                {
                    ["name"] = localRepository.Name,
                    ["reference"] = localRepository.Reference,
                    ["created"] = localRepository.Created,
                };
            }

            if (createdOnGitHub.Count > 0) obj["createdOnGitHub"] = new JsonArray([.. createdOnGitHub.Select(u => JsonValue.Create(u))]);
        }
        return Results.Json(node, statusCode: 200);
    }
    catch (RepoSetupRefusedException refused)
    {
        return Results.Json(refused.Body, statusCode: refused.Status);
    }
    catch (TeamNameTakenException taken)
    {
        // 409 naming the existing team by its LABEL. Two different names can derive to one
        // identifier ("Platform Engineering" and "Platform-Engineering" both give
        // `PlatformEngineering`), and that ambiguity is refused rather than resolved with a
        // suffix - two teams a person cannot tell apart is worse than being asked for another name.
        return Results.Conflict(new { error = taken.Message });
    }
    catch (NoSuchAgentException exception)
    {
        // CreateAsync builds the team's Manager THROUGH AddContainerAsync, so an unknown agent here
        // is the same refusal a member create meets - without this catch it would surface as an
        // unmapped 500 rather than a 400 naming the agent.
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    finally
    {
        await newRepos.ForgetUnlessCreatedAsync(teamCreated);
    }
})
    // Takes its team from the BODY, so TeamGate cannot see it - this route is covered only by this
    // filter. The standing rule for any NEW team-scoped route is to take its team from the path
    // instead, so TeamGate covers it by existing rather than needing its own filter.
    //
    // Team creation authority is inherited, never held: a machine principal may create only while
    // acting as an owner who still exists. The permit names the verb; this filter binds whose
    // authority is allowed to exercise it.
    .RequireTeamCreationAuthority()
    .WithTags("Teams")
    .RequirePermit(Permits.CreateTeam)
    .WithSummary("Create a team")
    .WithDescription(
        "Creates a team and, with it, a Manager container - so a team never exists without a door "
        + "into it - and a documents folder on disk.\n\n"
        + "**Nobody types an identifier.** Send the name a person would say; the identifier every "
        + "other route addresses is derived from it here and is never asked for. Derivation is "
        + "lossy on purpose: \"R&D / Tooling\" becomes `RDTooling`, and a label with no usable "
        + "ASCII at all gets a generated identifier rather than a refusal, because refusing a name "
        + "for its script would be the machinery leaking out.\n\n"
        + "Hyphens and underscores survive derivation, so \"Platform-Engineering\" and \"Platform "
        + "Engineering\" are different teams while \"Platform / Engineering\" collides with the "
        + "latter. A collision is refused with 409 naming the existing team, never resolved with a "
        + "suffix - two teams a person cannot tell apart is worse than being asked for another "
        + "name. 400 for an empty or over-long name.\n\n"
        + "Requires team-creation authority: a person directly, or a machine principal acting "
        + "for an owner who still exists.\n\n"
        + "**Repositories (B001F).** Every URL in `repos` is read with `git ls-remote` first (a github.com "
        + "URL with `GH_TOKEN`, as Fetch). One that cannot be read is refused with 422 `{ error, code: "
        + "\"repo-check-failed\", repos: [{ url, failure, reason, choices }] }` and nothing is created; send "
        + "it again with `repoChoices` naming a choice per URL: `use-local`, and for a person only "
        + "`create-on-github` and `attach-anyway` (network failure only). An agent sending a person's "
        + "choice is refused with 403. A team left with no repository gets a local one named after it, "
        + "`local:<team id>` (an unused one of that name is reused; `-2`, `-3` when a team uses it), "
        + "unless `localRepository` is false; when it cannot be made, no team is created.\n\n"
        + "so a refused value leaves nothing behind.");

// A NEW TEAM CARRYING AN EXISTING ONE'S CONFIGURATION.
//
// SERVER-SIDE, AND THAT IS THE DESIGN RATHER THAN A CONVENIENCE. Two of the things a clone must
// carry are not available to a browser at all: the stored `teams.root` PARENT is never sent -
// `TeamSummary.Root` is the RESOLVED team folder and is filled only for a person - and
// `DeriveName` lives in C#. A dialog doing this by calling `POST /api/teams` could therefore only
// ever drop a placed team's location silently, and would need a second copy of the derivation rule
// to pick a free identifier: the id-versus-name defect this codebase has a branded `TeamId` type to
// prevent, re-entered from the other side.
//
// Same authority as creation, because that is what it does. `{team}` is in the template so TeamGate
// covers the SOURCE structurally, and the creation-authority filter is what says the caller may
// make a team at all.
app.MapPost("/api/teams/{team}/clone", async (
    [Description(Describe.Team)] string team, CloneTeam request, TeamRegistry teams,
    TenantLogging audit, HttpContext context, CancellationToken ct) =>
{
    if (teams.ExistingName(team) is not { } stored)
    {
        return Results.NotFound(new { error = $"No team '{team}'." });
    }

    var name = (request.Name ?? "").Trim();

    if (name.Length == 0) return Results.BadRequest(new { error = "A team needs a name." });

    if (name.Length > TeamRegistry.MaximumLabelLength)
    {
        return Results.BadRequest(new
        {
            error = $"A team name cannot be longer than {TeamRegistry.MaximumLabelLength} characters.",
        });
    }

    try
    {
        var done = await teams.CloneAsync(stored, name, ct);
        var sourceSummary = teams.All().Single(t => string.Equals(t.Id, stored, StringComparison.OrdinalIgnoreCase));
        var sourceManager = sourceSummary.Containers.Single(
            c => string.Equals(c.Id, TeamRegistry.DefaultManagerName, StringComparison.OrdinalIgnoreCase));
        var carriedEnvKeys = teams.EnvFor(stored)
            .Keys
            .Where(key => key != TeamEnv.AdminEmail && key != TeamEnv.AdminPassword)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();

        // THE FAILURE COUNT BELONGS HERE, or a log asked "did this clone come out whole" cannot
        // answer - a row recording only what was carried reads identically for a clone that carried
        // everything and one that lost half its roster.
        //
        // SAFE VALUES ONLY. This table is kept forever and read by every person, so the
        // carried env is named by KEY only - never by value - while the other travelled settings
        // can be recorded verbatim. Failures stay a COUNT, never the raw exception text, for the
        // same reason: this row must never promise that arbitrary step output contains no secret.
        await audit.WriteAsync(
            context, TenantActions.TeamCloned, done.Team.Id, done.Team.Name,
            new
            {
                source = stored,
                repos = done.Repos,
                envKeys = done.EnvKeys,
                members = done.Members,
                failures = done.Failures.Count,
                carried = new
                {
                    agent = sourceManager.Agent,
                    additionalInstructions = done.Team.AdditionalInstructions is not null,
                    memberAgentAllowlist = done.Team.MemberAgents,
                    repos = done.Team.Repos,

                    // The source's CHOICE and not the figure in force for it - see `CloneAsync`.
                    // A clone of a team that chose nothing goes on tracking the instance figure
                    // rather than being frozen at today's value of it.
                    budgetTokens = done.Team.BudgetTokens,
                    env = done.EnvKeys > 0 ? carriedEnvKeys : Array.Empty<string>(),
                    roster = done.Team.Containers
                        .Select(container => new
                        {
                            id = container.Id,
                            name = container.Name,
                            agent = container.Agent,
                            hired_for = container.HiredFor,
                        })
                        .ToArray(),
                },
                notCarried = new
                {
                    paused = sourceSummary.Paused,
                    adminCredentials = "minted fresh",
                },
            },
            ct);

        return Results.Created($"/api/teams/{done.Team.Id}", new
        {
            team = done.Team,
            repos = done.Repos,
            envKeys = done.EnvKeys,
            members = done.Members,
            failures = done.Failures,
        });
    }
    catch (TeamNameTakenException taken)
    {
        // 409 NAMING THE EXISTING TEAM BY ITS LABEL, exactly as creation refuses: a collision is on
        // the DERIVED IDENTIFIER, which is why a name that looks free can still be refused, and the
        // person needs to be told which team they already have rather than that they typed
        // something invalid.
        //
        // The three arms below are unrelated sealed types with no `catch (InvalidOperationException)`
        // beneath them, so their ORDER carries nothing and none of them shadows another. Adding a
        // broad arm is what would make that untrue.
        return Results.Conflict(new { error = taken.Message, existing = taken.ExistingLabel });
    }
    catch (NoSuchAgentException agent)
    {
        return Results.BadRequest(new { error = agent.Message, available = agent.Available });
    }
    catch (ArgumentException exception)
    {
        // A SOURCE THAT HAS CHOSEN NOTHING CANNOT BE CLONED INTO A VALID TEAM, and this is where
        // that becomes a sentence rather than a 500. `CreateAsync` refuses a null manager Prompt,
        // member Prompt or member Agent BY NAME - null means nobody has chosen, an error condition
        // and not a default - and `CloneAsync` deliberately passes the source's nulls straight
        // through rather than manufacturing a reference nothing wrote down. The exception's own
        // message names the setting; a 500 would read as a platform fault over a team that is
        // simply not finished.
        //
        // IT ALSO CARRIES THE REFUSAL FOR A SOURCE WITH NO MANAGER AT ALL - a reachable state, and
        // one whose sentence has to name the MANAGER. Falling through to `CreateAsync`'s
        // empty-agent guard would answer "A team needs an Agent for its manager", which reads as a
        // field the caller left blank on a request that has no such field.
        //
        // It also catches a root the allowlist does not permit any more, which is the one moment a placed
        // root is re-examined at all.
        return Results.BadRequest(new { error = exception.Message });
    }
})
    // BOTH MARKERS, exactly as `POST /api/teams` carries both, and they are different kinds. The
    // permit names the VERB; the filter binds whose authority may exercise it.
    .RequireTeamCreationAuthority()
    .WithTags("Teams")
    .RequirePermit(Permits.CreateTeam)
    .WithSummary("Clone a team")
    .WithDescription(
        "Creates a new team carrying this one's configuration: its manager Agent, prompts, member "
        + "allowlist, repository URLs, `env` except the seeded admin credentials, its "
        + "roster - each member's Agent, Prompt, added instructions and the tag it was hired for - "
        + "and its roster.\n\n"
        + "**Files are not copied.** The clone gets an empty skeleton and its own Manager clones "
        + "the repositories into it. **History is not copied** either - a clone's members floor at "
        + "the head of the message log like any new container, so it starts work rather than "
        + "remembering somebody else's.\n\n"
        + "The seeded `TEST_ADMIN_EMAIL` and `TEST_ADMIN_PASSWORD` are the CLONE'S OWN and are not "
        + "carried: every team mints a per-team address and a fresh random password at creation, "
        + "and copying them would hand two teams one credential under an address naming the wrong "
        + "team. `paused` is not carried either: the clone starts unpaused regardless of the "
        + "source's queue state.\n\n"
        + "**The name is checked as an IDENTIFIER.** Derivation is lossy, so \"Alpha 2\" and "
        + "\"Alpha  2\" are one team; a collision is 409 naming the existing team. 400 for an "
        + "empty or over-long name, and 400 naming the setting when the SOURCE has chosen no "
        + "Prompt or no Agent for its members - a team that could not be created by hand cannot be "
        + "cloned into one either.\n\n"
        + "Answers 201 with what it carried and a `failures` list naming any member it could not "
        + "hire. It does NOT roll back: a reported half-clone is recoverable, where an automatic "
        + "rollback would run the most destructive operation in the product unattended, on a team "
        + "a person can already see.\n\n"
        + "Requires team-creation authority.");

// Renames the LABEL and nothing else. The identifier stays fixed for the reasons TeamSummary
// states: it keys the documents folder on disk and half of every container's identity, and
// moving both atomically is a different job. So this route deliberately has no
// way to express "change the id" - there is one field and it is the display name.
//
// People only: a console child agent has no business renaming the team it was handed. It carries
// {team} in its template, so TeamGate covers it as well.
// Deleting a team, and everything that names it. PEOPLE ONLY: this ends running processes and
// removes files, and no agent credential is consent to destroy a team.
//
// The ORDER the work happens in is TeamDeletion's, and its doc comment is where the reasoning lives.
// This route's job is authority, the 404, and reporting what actually went.
app.MapDelete("/api/teams/{team}", async (
    [Description(Describe.Team)] string team,
    [Description(
        "Exact confirmation string required only when deletion would discard commits that are on "
        + "no remote.")]
    string? confirm,
    [Description(
        "A `local:<name>` repository of this team's to delete after the team, repeated for each one "
        + "the person ticked. Only a local repository the team uses; a URL is refused with 400.")]
    string[]? deleteLocalRepository,
    TeamDeletion deletion, LocalRepoDeletion localRepoDeletion, TenantLogging audit, ITenantLog tenantLog,
    TeamRegistry teams, TeamListPush listPush,
    HttpContext context,
    CancellationToken ct) =>
{
    // Null means no such team, which is a 404 rather than a failure: asking twice is not a fault,
    // and a caller retrying after a timeout should not be told something broke.
    // The LABEL, read before the deletion - afterwards there is nothing left to read it from, and
    // an audit row naming an identifier a person never saw is a row they cannot match to what they
    // did.
    var label = teams.ExistingName(team) is { } present ? teams.LabelFor(present) : team;
    var viewers = teams.ExistingName(team) is null
        ? Array.Empty<string>()
        : (await listPush.EntitledViewerIdsAsync(team, ct)).ToArray();

    // KEPT, NEVER DELETED WITH THE TEAM (B001F): its local repositories live under
    // `<dataRoot>/repos`, outside the team's root, and show as unused in Admin -> Repositories,
    // where a person may delete them. Read before the deletion, which forgets the list.
    var localRepositories = teams.ExistingName(team) is { } named
        ? teams.ReposFor(named).Where(LocalRepos.IsLocal).Select(r => r.Trim()).ToArray()
        : [];

    // DELETED WITH THE TEAM ONLY WHEN TICKED: each a `local:<name>` this team uses. Anything
    // else - a URL, another team's repository - is refused before a single thing is deleted.
    var ticked = new List<string>();
    foreach (var asked in deleteLocalRepository ?? [])
    {
        if (localRepositories.FirstOrDefault(r => string.Equals(r, asked?.Trim(), StringComparison.Ordinal)) is not { } own)
        {
            return Results.BadRequest(new
            {
                error = $"'{asked}' is not a local repository this team uses, so nothing was deleted. Only a "
                    + "team's own local:<name> repositories can be deleted with it.",
            });
        }

        if (!ticked.Contains(own, StringComparer.Ordinal)) ticked.Add(own);
    }

    TeamDeleted? removed;
    try
    {
        // TEAM.DELETING COMES FIRST, and is not swallowed: it is written after the confirmation
        // refusal and before anything is removed, so a row that cannot be written leaves the whole
        // team in place. The team.deleted row after names what went and what remains.
        removed = await deletion.DeleteAsync(team, confirm, ct, (plan, token) => tenantLog.WriteAsync(
            context.User.FindFirstValue(ClaimTypes.NameIdentifier),
            context.User.FindFirstValue(ClaimTypes.Email),
            TenantActions.TeamDeleting, plan.Team, label,
            JsonSerializer.Serialize(new
            {
                containers = plan.Containers,
                root = plan.Root,
                deleteLocalRepositories = ticked,
                confirmed = !string.IsNullOrWhiteSpace(confirm),
            }, JsonSerializerOptions.Web),
            token));
    }
    catch (TeamDeletionNotRecordedException unrecorded)
    {
        return Results.Json(new { error = unrecorded.Message }, statusCode: StatusCodes.Status500InternalServerError);
    }
    catch (TeamDeletionConfirmationRequiredException refusal)
    {
        return Results.Conflict(new
        {
            error = refusal.Message,
            confirmation = refusal.Confirmation,
            losses = refusal.Losses,
        });
    }

    if (removed is null) return Results.NotFound(new { error = $"No team '{team}'." });

    // AFTER THE TEAM, and through the one delete Admin -> Repositories uses: its own
    // `local-repo.deleted` row first, the same refusal while another team uses it. One that fails
    // leaves the team deleted - never the reverse - and is named with its reason and kept.
    var localRepositoriesDeleted = new List<string>();
    var localRepositoriesKept = new List<string>();
    var localRepositoryFailures = new List<LocalRepositoryNotDeleted>();
    foreach (var reference in localRepositories)
    {
        if (!ticked.Contains(reference, StringComparer.Ordinal))
        {
            localRepositoriesKept.Add(reference);
            continue;
        }

        var result = await localRepoDeletion.DeleteAsync(LocalRepos.NameOf(reference), context.User, ct);
        if (result.Deleted)
        {
            localRepositoriesDeleted.Add(reference);
            continue;
        }

        // Incomplete: its name is gone, so it is not kept, and what is left is retried, not
        // deleted again from Admin -> Repositories.
        if (result.Outcome == LocalRepoDeleteOutcome.Incomplete)
        {
            localRepositoryFailures.Add(new LocalRepositoryNotDeleted(reference, result.Error!));
            continue;
        }

        if (result.Outcome != LocalRepoDeleteOutcome.NotFound) localRepositoriesKept.Add(reference);
        localRepositoryFailures.Add(new LocalRepositoryNotDeleted(
            reference, $"{result.Error} Delete it from Admin → Repositories."));
    }

    // Exactly what went, because this is the one act in the system with nothing to inspect
    // afterwards. The AFTER-row, carrying the result; the team.deleting row before the act is the
    // one that must land, and this one stays on the swallowing path so a team already gone is
    // never reported as a failure.
    await audit.WriteAsync(
        context, TenantActions.TeamDeleted, removed.Team, label,
        // Named explicitly rather than by shorthand: a shorthand property keeps its C# casing and
        // nothing camel-cases this detail on the way out, so the whole grid reads one way.
        new
        {
            containers = removed.Containers,
            pendingDeliveries = removed.PendingDeliveries,
            schedules = removed.Schedules,
            directories = removed.Directories,
            failures = removed.Failures,

            // Every path still on disk, one by one: the root keeps its marker and is retried.
            remaining = removed.Remaining,
            localRepositoriesDeleted,
            localRepositoriesKept,
            localRepositoryFailures = localRepositoryFailures
                .Select(f => new { reference = f.Reference, reason = f.Reason }).ToArray(),

            // The agent CLIs' session folders for its workspaces, and any still there.
            sessionFolders = removed.SessionFolders,
            sessionFoldersRemaining = removed.SessionFoldersRemaining,
        },
        ct);

    await listPush.AnnounceDeletedAsync(removed.Team, viewers, ct);

    // 200 with a body rather than 204. A deletion that could not remove a directory is still a
    // deletion - the team is gone from every list - and a caller that is told only "no content"
    // cannot say which files are still on disk.
    return Results.Ok(removed with
    {
        LocalRepositoriesKept = localRepositoriesKept,
        LocalRepositoriesDeleted = localRepositoriesDeleted,
        LocalRepositoryFailures = localRepositoryFailures,
    });
})
    .WithTags("Teams")
    .HumansOnly()
    .WithSummary("Delete a team and everything that names it")
    .WithDescription(
        "Ends every Concierge session and every Agent Container on the team, removes their "
        + "cursors, subscriptions, outstanding deliveries and credentials, deletes the team row - "
        + "which cascades to its members - and removes its ROOT: one "
        + "directory holding its skills, repos, member workspaces, transcripts and Concierge "
        + "folders.\n\n"
        + "**ITS DOCUMENTS ARE KEPT**, and `documentsKept` says where they are; it is null when the "
        + "team wrote none, and its empty documents folder is removed with it. They live under the "
        + "tenant documents root rather than inside the team, so this cannot reach them: a team is "
        + "deleted as soon as its work is merged, which is exactly when its reports become the only "
        + "record of how that work was checked. They stay readable - by any person, once the "
        + "team that shared them is gone - through `GET /api/documents` and the documents routes "
        + "under this team's name.\n\n"
        + "**ITS LOCAL REPOSITORIES ARE KEPT** unless named in `deleteLocalRepository` (`localRepositoriesKept`, "
        + "the `local:<name>` references it had and still has on disk): they are listed as unused in "
        + "`GET /api/local-repos`, where a person may delete them. One named in `deleteLocalRepository` is "
        + "deleted AFTER the team through the same delete `DELETE /api/local-repos/{name}` does - its "
        + "`local-repo.deleted` row first, refused while another team uses it - and listed in "
        + "`localRepositoriesDeleted`; one that could not be deleted is kept and named with its reason in "
        + "`localRepositoryFailures`, and the team is deleted either way.\n\n"
        + "**The agent CLIs' session folders** keyed to the team's own workspaces are removed from the shared "
        + "agent home (Claude's `~/.claude/projects/<workspace, dashed>` and `~/.cache/claude-cli-nodejs/...`, "
        + "each built-in preset's `sessionFolders`), counted in `sessionFolders`; any path left is in "
        + "`sessionFoldersRemaining` and `failures`. Nothing else in the home, and nothing of a live team's.\n\n"
        + "**The message log is not touched.** It is append-only, and a team's messages are the "
        + "history of what happened rather than a property of the team; deleting them would take "
        + "other teams' causally-linked messages with them.\n\n"
        + "**A `team.deleting` row is written to the tenant log BEFORE anything is removed** (after the "
        + "confirmation refusal), naming the containers and root. When it cannot be written the delete "
        + "answers 500 with a sentence saying so and removes nothing. The `team.deleted` row after the "
        + "act carries the result.\n\n"
        + "Answers 200 with what was removed, including any directory that could NOT be deleted - "
        + "usually a file still held by a child process that has not finished exiting. Those are "
        + "named rather than swallowed, and the team is gone either way.\n\n"
        + "**A root that does not carry the platform's `.harness-team` marker is LEFT ALONE and "
        + "named in `failures`.** A team's root can be a path a person typed, so the marker - "
        + "written when the platform created the folder - is the only evidence it made it. The "
        + "database rows go either way; the files are a person's to remove.\n\n"
        + "If the team's clone or any linked git worktree still holds commits that are on no "
        + "remote, deletion REFUSES with 409 before it stops a process or removes a row. Repeat "
        + "the delete only by sending back the exact confirmation string the refusal names, so the "
        + "loss being accepted is the one the server actually found.\n\n"
        + "404 for an unknown team; asking twice is not an error.\n\n"
        + "**A person's action.**");

// Handing a team a clean slate WITHOUT deleting it: keep the Agent Containers, reset the substrate
// underneath them.
//
// It declares {team}, so TeamGate covers it, and it is HumansOnly (see below): a member able to
// reset its own team could erase its own evidence, and Reset repositories removes code.
//
// The ORDER the work happens in is TeamReset's, and its doc comment is where the reasoning lives.
// This route's job is authority, the two refusals, and reporting what actually happened.
app.MapPost("/api/teams/{team}/reset", async (
    [Description(Describe.Team)] string team,
    TeamResetOptions options,
    TeamReset reset, TenantLogging audit, ITenantLog tenantLog, TeamRegistry teams, HttpContext context,
    CancellationToken ct) =>
{
    // The LABEL, read before the reset - an audit row naming an identifier a person has never seen
    // is a row they cannot match to what they did.
    var label = teams.ExistingName(team) is { } present ? teams.LabelFor(present) : team;

    TeamWasReset? done;

    try
    {
        // RESET REPOSITORIES' ROW COMES FIRST, and is not swallowed: a tree or branch removed with
        // no record of who asked is the one outcome refused, so a row that cannot be written stops
        // the whole reset before anything moves. It names what may go; the team.reset row after
        // names what went and what was kept.
        done = await reset.ResetAsync(team, options, ct, (plan, token) => tenantLog.WriteAsync(
            context.User.FindFirstValue(ClaimTypes.NameIdentifier),
            context.User.FindFirstValue(ClaimTypes.Email),
            TenantActions.TeamResetRepositories, teams.ExistingName(team) ?? team, label,
            JsonSerializer.Serialize(new
            {
                members = options.Members,
                worktrees = plan.Worktrees,
                branches = plan.Branches,
                teamBranch = plan.TeamBranch,
            }, JsonSerializerOptions.Web),
            token));
    }
    catch (TeamBusyException busy)
    {
        // 409, not 400. The request is well formed and the team is right there; it is the team's
        // STATE that refuses, and waiting is the fix. The WHOLE reset is refused rather than the
        // idle half - a team where some members remember the last task and others do not is a worse
        // state than either.
        return Results.Conflict(new { error = busy.Message, busy = busy.Busy });
    }
    catch (ResetDefaultBranchNotKnownException unknown)
    {
        // The team branch is reset to the STORED default branch or not at all, and the existing
        // sentence says which. Nothing else changes: the refusal comes before anything moves.
        await audit.WriteAsync(
            context, TenantActions.TeamResetRepositories, teams.ExistingName(team) ?? team, label,
            new { refused = true, reason = unknown.Message }, ct);
        return Results.Conflict(new { error = unknown.Message, repo = unknown.Repo });
    }
    catch (ResetNotRecordedException unrecorded)
    {
        return Results.Json(new { error = unrecorded.Message }, statusCode: StatusCodes.Status500InternalServerError);
    }

    if (done is null) return Results.NotFound(new { error = $"No team '{team}'." });

    // NAMES AND COUNTS, NEVER CONTENT. Every person can read this table and it is kept
    // forever; `payload.output` carries a bounded excerpt of real agent output, so a purge records
    // THAT it happened and how much went, and nothing of what was in it.
    await audit.WriteAsync(
        context, TenantActions.TeamReset, done.Team, label,
        // Named explicitly rather than by shorthand, matching the deletion row beside it: a
        // shorthand property keeps its C# casing and nothing camel-cases this detail on the way
        // out, so the whole grid reads one way.
        new
        {
            floor = done.Floor,
            floored = done.Floored,
            purged = done.Purged,
            retained = done.Retained,
            cleared = done.Cleared,
            failures = done.Failures,
            remaining = done.Remaining,

            // Each tree and branch removed, and each kept with its reason. Absent when not asked.
            repositories = done.Repositories is null
                ? (JsonElement?)null
                : JsonSerializer.SerializeToElement(done.Repositories, JsonSerializerOptions.Web),
        },
        ct);

    // 200 with a body rather than 204, for TeamDeletion's reason: a directory that could not be
    // emptied is still a completed reset, and a caller told only "no content" cannot say which.
    return Results.Ok(done);
})
    .WithTags("Teams")
    // HUMANS ONLY, and this one is load-bearing rather than bookkeeping. RequirePermit would make
    // this reachable by a machine principal - and a member able to reset its own team is an agent
    // that can erase its team's memory, including its own evidence of having done so.
    .HumansOnly()
    .WithSummary("Reset a team's substrate, keeping its Agent Containers")
    .WithDescription(
        "Advances every named member's FLOOR to the head of the message log - the value a member is "
        + "already created with, which is both where its cursor starts (so nothing older is "
        + "delivered) and where its ledger starts (so nothing older is remembered). The members "
        + "themselves are untouched: identity, label, Agent, Prompt, override, permits and "
        + "subscriptions all survive, as do the team's name and its documents.\n\n"
        + "`forgetHistory` defaults to **true** - naming members and saying nothing else is a "
        + "reset. Send it false to clear folders and leave the log alone; it is the only field here "
        + "whose default is true.\n\n"
        + "Optionally clears the named members' working folders and transcripts, the team's shared "
        + "documents, and resets the team's Concierge - ending **every session on the "
        + "team, including other people's**, "
        + "since sessions are keyed per person and a reset is a team-level act.\n\n"
        + "`purge` PERMANENTLY DELETES the named members' messages. Irreversible. A row still cited "
        + "by a message that is NOT being deleted is RETAINED rather than failing the purge, and "
        + "counted - nulling its causation would destroy the one field that answers what woke "
        + "what. Which message that is, is NOT reported and must not be guessed: it is frequently "
        + "another member of the SAME team, one since removed, or the team's Concierge, "
        + "which is not a member and is never targeted.\n\n"
        + "409 when any named member is running or holds accepted work: the whole reset is refused, "
        + "never the idle half.\n\n"
        + "`resetRepositories` removes each named member's worktrees (never forced) and its merged or "
        + "empty branches, and with EVERY member named resets `team/<id>` to the stored default "
        + "branch; `repositories` in the answer names each tree and branch removed and each kept, "
        + "with the reason. Its `team.reset-repositories` tenant row is written before anything "
        + "moves: 500 and nothing reset when it cannot be. 409 with the existing sentence, and "
        + "nothing reset, when every member is named and a repository's default branch is not known.\n\n"
        + "404 for an unknown team. A member the team does not hold is skipped rather than "
        + "refused.\n\n"
        + "**A person's action.**");

// WHAT RESET REPOSITORIES WOULD REMOVE, read before the person confirms: the dialog lists the
// ticked members' trees and branches, and the team branch when every member is ticked. HumansOnly
// for the reason the reset itself is.
app.MapGet("/api/teams/{team}/reset/repositories", async (
    [Description(Describe.Team)] string team,
    TeamReset reset, CancellationToken ct) =>
    await reset.PreviewRepositoriesAsync(team, ct) is { } preview
        ? Results.Ok(preview)
        : Results.NotFound(new { error = $"No team '{team}'." }))
    .WithTags("Teams")
    .HumansOnly()
    .WithSummary("List what Reset repositories would remove")
    .WithDescription(
        "Every member's worktrees and local branches in every team repository, each with its "
        + "member, and the team branch `team/<id>` that is reset when every member is ticked. "
        + "`defaultBranchNotKnown` names each repository whose default branch is not known, for "
        + "which that reset is refused. Reads only; changes nothing. 404 for an unknown team.");

app.MapPost("/api/teams/{team}/pause", async (
    [Description(Describe.Team)] string team,
    TeamRegistry teams, HttpContext context, CancellationToken ct) =>
{
    if (teams.ExistingName(team) is not { } stored)
    {
        return Results.NotFound(new { error = $"No team '{team}'." });
    }

    // The flag and its tenant_events row are one transaction: a change with no record of who
    // made it does not land.
    await teams.SetPausedAsync(
        stored, paused: true, ct,
        id => TenantLogging.Row(context, TenantActions.TeamPaused, id, teams.LabelFor(id), new { paused = true }));

    return Results.NoContent();
})
    .WithTags("Teams")
    .HumansOnly()
    .WithSummary("Pause this team")
    .WithDescription(
        "Stops this team from being offered new work, and stops its containers from taking their "
        + "next queued batch. A run already in flight is left alone and finishes normally. "
        + "Idempotent: pausing an already-paused team still answers 204.");

app.MapPost("/api/teams/{team}/resume", async (
    [Description(Describe.Team)] string team,
    TeamRegistry teams, HttpContext context, CancellationToken ct) =>
{
    if (teams.ExistingName(team) is not { } stored)
    {
        return Results.NotFound(new { error = $"No team '{team}'." });
    }

    // The flag and its tenant_events row are one transaction: a change with no record of who
    // made it does not land.
    await teams.SetPausedAsync(
        stored, paused: false, ct,
        id => TenantLogging.Row(context, TenantActions.TeamResumed, id, teams.LabelFor(id), new { paused = false }));

    return Results.NoContent();
})
    .WithTags("Teams")
    .HumansOnly()
    .WithSummary("Resume this team")
    .WithDescription(
        "Lets this team start receiving new work again. Queued work remains queued while paused and "
        + "delivers in order after resume. Idempotent: resuming an already-running team still "
        + "answers 204.");

app.MapGet("/api/teams/{team}/triggers", async (
    [Description(Describe.Team)] string team,
    TeamRegistry teams,
    ITriggerStore schedules,
    TriggerCost cost,
    CancellationToken ct) =>
{
    if (teams.ExistingName(team) is not { } stored)
    {
        return Results.NotFound(new { error = $"No team '{team}'." });
    }

    return Results.Ok(await cost.ViewsAsync(await schedules.ListForTeamAsync(stored, ct), DateTimeOffset.UtcNow, ct));
})
    .WithTags("Teams")
    .HumansOnly()
    .WithSummary("List this team's triggers")
    .WithDescription(
        "Every trigger row currently stored for this team, oldest first, each with its read-only "
        + TriggerCost.SpendDescription + "\n\n"
        + "404 for an unknown team.");

// THE PER-CONTAINER ROUTE IS WHAT THE CARD DIALOG READS. It declares {team}, so TeamGate covers
// it structurally - the same declaration that lets its handler read a team at all. Shares
// RequirePermit(Permits.Read) with the sibling per-container routes under
// /api/teams/{team}/containers/{name}/..., since this is one more thing the `status` tool for a
// member could read, not a human-only surface.
app.MapGet("/api/teams/{team}/containers/{name}/triggers", async (
    [Description(Describe.Team)] string team,
    [Description("The member's current identifier, as addressed in its route.")] string name,
    TeamRegistry teams,
    ITriggerStore schedules,
    TriggerCost cost,
    CancellationToken ct) =>
{
    try
    {
        var member = await teams.MemberAsync(team, name, ct);

        return Results.Ok(await cost.ViewsAsync(
            await schedules.ListForContainerAsync(member.Team, member.Name, ct), DateTimeOffset.UtcNow, ct));
    }
    catch (InvalidOperationException exception)
    {
        return Results.NotFound(new { error = exception.Message });
    }
})
    .WithTags("Teams")
    .RequirePermit(Permits.Read)
    .WithSummary("List one member's triggers")
    .WithDescription(
        "Every trigger row currently stored for this one member, oldest first - what the member "
        + "card's own dialog reads, separately from its teammates', each with its read-only "
        + TriggerCost.SpendDescription + "\n\n"
        + "404 for an unknown team or member.");

// WHAT A MEMBER'S RUNS ACTUALLY COST, for the trigger dialog before a person saves. Measured only:
// nothing here projects a day, and a member nobody has measured says so with a null median.
app.MapGet("/api/teams/{team}/containers/{name}/cost", async (
    [Description(Describe.Team)] string team,
    [Description("The member's current identifier, as addressed in its route.")] string name,
    TeamRegistry teams,
    TriggerCost cost,
    CancellationToken ct) =>
{
    try
    {
        var member = await teams.MemberAsync(team, name, ct);

        return Results.Ok(await cost.RecentAsync(
            new ContainerId(member.Team, member.Name), MemberRef.KindOf(member.Agent), ct));
    }
    catch (InvalidOperationException exception)
    {
        return Results.NotFound(new { error = exception.Message });
    }
})
    .WithTags("Teams")
    .RequirePermit(Permits.Read)
    .WithSummary("A member's measured recent cost")
    .WithDescription(
        "What this member's last runs (up to 10) actually cost: `lastRuns`, how many of them were "
        + "measured (`measuredRuns`) and not (`unmeasuredRuns`), the median `BillableTokens` of the "
        + "measured ones (`medianBillableTokens`, null when none was), and `kind` (`agent` or "
        + "`plugin`). Nothing is estimated. A run is one terminal row carrying its run's figures, so "
        + "a batched run counts once.\n\n"
        + "404 for an unknown team or member.");

app.MapPost("/api/teams/{team}/triggers", async (
    [Description(Describe.Team)] string team,
    CreateSchedule request,
    TeamRegistry teams,
    TriggerCreation triggers,
    TriggerCost cost,
    HttpContext context,
    CancellationToken ct) =>
{
    if (teams.ExistingName(team) is not { } stored)
    {
        return Results.NotFound(new { error = $"No team '{team}'." });
    }

    // THE ONE PATH a trigger is made by, a person's here and a solution install's alike: every check,
    // the row with its tenant row in one transaction, the subscriptions recomputed, the runner woken.
    var created = await triggers.CreateAsync(
        stored,
        new NewTrigger(
            request.Name, request.Container, request.Instruction, request.Kind, request.Expression,
            request.Timezone, request.IntervalSeconds, request.FireAt, request.IdleOnly, request.Enabled,
            request.NextDueAt, request.EventType, request.Filter, request.WatchRoot, request.WatchPath,
            request.WatchGlob, request.PollSeconds, request.QuietSeconds, request.MinIntervalSeconds,
            request.WakeManager, request.DailyTokenCap, request.OutcomeId),
        context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown",
        context.User.FindFirstValue(ClaimTypes.Email),
        row => TenantLogging.Row(
            context, TenantActions.ScheduleCreated, row.Id, row.Name, new { team = row.Team, member = row.Container }),
        ct);

    if (created.Row is not { } row) return Results.BadRequest(new { error = created.Refusal });

    return Results.Created($"/api/teams/{row.Team}/triggers/{row.Id}", await cost.ViewAsync(row, DateTimeOffset.UtcNow, ct));
})
    .WithTags("Teams")
    .HumansOnly()
    .WithSummary("Create a trigger on this team")
    .WithDescription(
        "Creates one durable trigger row. `container` defaults to the team's Manager when omitted. "
        + "Validation refuses a shape that is not valid for its kind, a member name this team does "
        + "not hold, for `kind: event`, an `eventType` the catalog does not declare, and a `filter` "
        + "naming a field that event type does not carry.\n\n"
        + "`kind: folderChange` watches `watchPath` under `watchRoot` (`documents` or "
        + "`root:<name>` for a file-browser root with allowWatch) and publishes `file.changed`; its "
        + "`eventType` is always `file.changed`. A folder outside those roots, a symbolic link, one "
        + "holding more than 10,000 entries, or one past the instance's watch cap is refused with "
        + "a sentence.\n\n"
        + "`wakeManager` defaults to `onHandbackOrFailure`; `dailyTokenCap` defaults to no cap. The "
        + "row returned carries the read-only " + TriggerCost.SpendDescription + "\n\n"
        + "400 for invalid input; 404 for an unknown team.");

app.MapPatch("/api/teams/{team}/triggers/{id}", async (
    [Description(Describe.Team)] string team,
    [Description("The schedule row's identifier.")] string id,
    JsonElement body,
    TeamRegistry teams,
    ITriggerStore schedules,
    TriggerCost cost,
    FolderWatch folders,
    AgentCatalog catalog,
    EffectiveSubscriptions effective,
    TriggerWakeSignal wake,
    IOutcomeStore outcomes,
    HttpContext context,
    CancellationToken ct) =>
{
    if (teams.ExistingName(team) is not { } stored)
    {
        return Results.NotFound(new { error = $"No team '{team}'." });
    }

    if (await schedules.FindAsync(id, ct) is not { } existing
        || !string.Equals(existing.Team, stored, StringComparison.OrdinalIgnoreCase))
    {
        return Results.NotFound(new { error = $"No schedule '{id}' on team '{stored}'." });
    }

    if (!TryReadSchedulePatch(body, out var patch, out var patchError))
    {
        return Results.BadRequest(new { error = patchError });
    }

    if (ApplySchedulePatch(existing, patch, out var candidate, out var applyError) is false)
    {
        return Results.BadRequest(new { error = applyError });
    }

    if (!TeamHasMember(teams, stored, candidate.Container))
    {
        return Results.BadRequest(new { error = $"No member '{candidate.Container}' on team '{stored}'." });
    }

    if (Triggers.Validate(
        candidate.Kind,
        candidate.Expression,
        candidate.Timezone,
        candidate.IntervalSeconds,
        candidate.FireAt,
        onceMustFollow: null) is { } invalid)
    {
        return Results.BadRequest(new { error = invalid });
    }

    // See the POST route. The event type is the server's; the folder is checked here; the
    // instance cap counts only a row BECOMING a folder trigger, or editing one at the cap would be
    // refused for being one of the watches it is counted among.
    if (FolderWatch.IsFolderKind(candidate.Kind))
    {
        candidate = candidate with
        {
            EventType = FolderWatch.FileChangedEventType,
            WatchGlob = FolderWatch.Glob(candidate.WatchGlob),
            PollSeconds = candidate.PollSeconds ?? FolderWatch.DefaultPollSeconds,
            QuietSeconds = candidate.QuietSeconds ?? FolderWatch.DefaultQuietSeconds,
            MinIntervalSeconds = candidate.MinIntervalSeconds ?? FolderWatch.DefaultMinIntervalSeconds,
        };

        if (await folders.RefusalForAsync(
                stored, candidate.WatchRoot, candidate.WatchPath, candidate.WatchGlob,
                candidate.PollSeconds, candidate.QuietSeconds, candidate.MinIntervalSeconds,
                counting: !FolderWatch.IsFolderKind(existing.Kind), ct) is { } folderRefusal)
        {
            return Results.BadRequest(new { error = folderRefusal });
        }

        var target = folders.Resolve(stored, candidate.WatchRoot, candidate.WatchPath, out _)!;
        candidate = candidate with { WatchRoot = target.Root, WatchPath = target.Folder };

        // A MOVED WATCH POLLS AT ONCE, to take its new baseline (the store forgets the old one),
        // and so does one switched back on.
        var moved = candidate.WatchRoot != existing.WatchRoot
            || candidate.WatchPath != existing.WatchPath
            || candidate.WatchGlob != existing.WatchGlob;

        if (moved || (candidate.Enabled && !existing.Enabled) || candidate.NextDueAt is null)
        {
            candidate = candidate with { NextDueAt = DateTimeOffset.UtcNow };
        }
    }
    else if (FolderWatch.IsFolderKind(existing.Kind))
    {
        candidate = candidate with
        {
            EventType = string.Equals(candidate.EventType, FolderWatch.FileChangedEventType, StringComparison.Ordinal)
                && !string.Equals(candidate.Kind, "event", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : candidate.EventType,
            WatchRoot = null,
            WatchPath = null,
            WatchGlob = null,
            PollSeconds = null,
            QuietSeconds = null,
            MinIntervalSeconds = null,
        };
    }

    // AN EVENT TRIGGER NAMES A TYPE THE CATALOG DECLARES, or it can never fire. Refused rather than
    // stored: a trigger that silently never matches is indistinguishable from one whose event has
    // not happened yet, and a person would wait on it indefinitely.
    if (string.Equals(candidate.Kind, "event", StringComparison.OrdinalIgnoreCase))
    {
        if (string.IsNullOrWhiteSpace(candidate.EventType))
        {
            return Results.BadRequest(new { error = "An event trigger must name an event type." });
        }

        if (EventCatalog.For(candidate.EventType) is null)
        {
            return Results.BadRequest(new
            {
                error = $"\"{candidate.EventType}\" is not an event this platform publishes. "
                    + "GET /api/events lists every type a trigger can name.",
            });
        }

        // THE SAME RULE, REACHED BY A DIFFERENT DOOR - see the POST route's identical check.
        // `TeamHasMember` above already confirmed `candidate.Container` exists, so this cannot 404.
        var subscriber = await teams.MemberAsync(stored, candidate.Container, ct);

        if (catalog.For(subscriber.Agent) is { LanguageModel: true }
            && EventCatalog.IsHighVolume(candidate.EventType))
        {
            return Results.BadRequest(
                new { error = TeamRegistry.FirehoseRefusal(candidate.EventType, subscriber.Agent) });
        }
    }

    if (TriggerFilter.RefusalFor(candidate.Filter, candidate.EventType ?? "") is { } filterRefusal)
    {
        return Results.BadRequest(new { error = filterRefusal });
    }

    // THE OUTCOME ITS FIRES SERVE: a live one, named by id or exact name, stored by id.
    if (patch.HasOutcomeId && candidate.OutcomeId is { } outcomeAsked)
    {
        if (await outcomes.ResolveLiveAsync(outcomeAsked, ct) is not { } outcome)
        {
            return Results.BadRequest(new { error = TriggerCreation.OutcomeRefusal(outcomeAsked) });
        }

        candidate = candidate with { OutcomeId = outcome.Id };
    }

    // A PERSON'S CHANGE WAKES A SCHEDULE ASLEEP ON ITS CAP. Raised above today's spend, or cleared,
    // the cap no longer stops it, so it fires at its next occurrence from now rather than tomorrow.
    // Lowering it wakes nothing. An edit to the schedule itself has already re-armed it from now
    // (see ApplySchedulePatch), and an explicit `nextDueAt` still wins.
    var now = DateTimeOffset.UtcNow;
    if (patch.HasDailyTokenCap
        && !patch.HasNextDueAt
        && existing.LastOutcome == "capped"
        && Enum.TryParse<TriggerKind>(candidate.Kind, ignoreCase: true, out var clockKind)
        && clockKind is TriggerKind.Cron or TriggerKind.Every or TriggerKind.Once)
    {
        var spent = await cost.SpentTodayAsync(existing, now, ct);
        if (TriggerCost.CapReached(existing, spent) && !TriggerCost.CapReached(candidate, spent))
        {
            candidate = candidate with
            {
                NextDueAt = FirstOccurrence(
                    candidate.Kind, candidate.Expression, candidate.Timezone, candidate.IntervalSeconds, candidate.FireAt, now),
            };
        }
    }

    // THE PERSON WHO CHANGED IT IS NOW ITS CONFIGURER: their email is what its fires' outcome links
    // name. A caller with no email keeps the one stored.
    candidate = candidate with { ConfiguredByEmail = context.User.FindFirstValue(ClaimTypes.Email) ?? existing.ConfiguredByEmail };

    // The row and its tenant_events row are one transaction: a change with no record of who made
    // it does not land.
    await schedules.SaveAsync(
        candidate,
        new TriggerAudit(
            context.User.FindFirstValue(ClaimTypes.NameIdentifier),
            context.User.FindFirstValue(ClaimTypes.Email),
            TenantActions.ScheduleChanged,
            candidate.Id,
            candidate.Name,
            JsonSerializer.Serialize(new { team = candidate.Team, member = candidate.Container })),
        ct);

    // The edit can change EITHER the trigger's event type/enabled state OR which container holds
    // it. Both sides are recomputed: the target container (which may have gained or lost this
    // trigger's contribution) and, when a PATCH moved the trigger to a different container, the
    // ORIGINAL one too - it just lost a subscription this same write moved somewhere else, and
    // nothing else would ever recompute it.
    await effective.RecomputeAsync(new ContainerId(stored, candidate.Container), ct);

    if (!string.Equals(existing.Container, candidate.Container, StringComparison.OrdinalIgnoreCase))
    {
        await effective.RecomputeAsync(new ContainerId(stored, existing.Container), ct);
    }

    // The runner is asleep until whatever WAS due next. Without this it would not learn
    // about this write until its heartbeat expires, so a schedule due in ten seconds could
    // sit unnoticed for a minute - which reads as the feature being broken rather than
    // slow. Signalled AFTER the write, so waking early cannot read a row that is not there.
    wake.Signal();

    return Results.Ok(await cost.ViewAsync(candidate, DateTimeOffset.UtcNow, ct));
})
    .Accepts<UpdateSchedule>("application/json")
    .WithTags("Teams")
    .HumansOnly()
    .WithSummary("Change part of one trigger")
    .WithDescription(
        "PATCH semantics: absent means leave alone; present with a value stores that value; present "
        + "as null or blank clears nullable fields (`expression`, `timezone`, `intervalSeconds`, "
        + "`fireAt`, `nextDueAt`, `eventType`, `filter`, `dailyTokenCap`). `wakeManager` may not be "
        + "null. The row returned carries the read-only " + TriggerCost.SpendDescription + "\n\n"
        + "400 for invalid input, a member this team does not hold, (for `kind: event`) an "
        + "`eventType` the catalog does not declare, or a `filter` naming a field that event type "
        + "does not carry; 404 for an unknown team or trigger.");

app.MapDelete("/api/teams/{team}/triggers/{id}", async (
    [Description(Describe.Team)] string team,
    [Description("The schedule row's identifier.")] string id,
    TeamRegistry teams,
    ITriggerStore schedules,
    EffectiveSubscriptions effective,
    TriggerWakeSignal wake,
    HttpContext context,
    CancellationToken ct) =>
{
    if (teams.ExistingName(team) is not { } stored)
    {
        return Results.NotFound(new { error = $"No team '{team}'." });
    }

    if (await schedules.FindAsync(id, ct) is not { } row
        || !string.Equals(row.Team, stored, StringComparison.OrdinalIgnoreCase))
    {
        return Results.NotFound(new { error = $"No schedule '{id}' on team '{stored}'." });
    }

    // One transaction with its tenant_events row, as the create and the change are.
    await schedules.DeleteAsync(
        row.Id,
        TenantLogging.Row(
            context, TenantActions.ScheduleDeleted, row.Id, row.Name, new { team = row.Team, member = row.Container }),
        ct);

    // A DELETED event trigger can only ever remove a subscription - the base set is untouched, so
    // this can never eat a Manager's agentContainer.completed - but "can only add or remove nothing" is
    // exactly the reasoning that turns into drift the moment someone reaches for it as a shortcut.
    // Recomputed whole, same as every other write.
    await effective.RecomputeAsync(new ContainerId(stored, row.Container), ct);

    // The runner is asleep until whatever WAS due next. Without this it would not learn
    // about this write until its heartbeat expires, so a schedule due in ten seconds could
    // sit unnoticed for a minute - which reads as the feature being broken rather than
    // slow. Signalled AFTER the write, so waking early cannot read a row that is not there.
    wake.Signal();

    return Results.NoContent();
})
    .WithTags("Teams")
    .HumansOnly()
    .WithSummary("Delete one trigger")
    .WithDescription(
        "Deletes one trigger row.\n\n"
        + "404 for an unknown team or trigger.");

// RUN NOW: the fire the schedule makes, at a person's hand. TriggerSweep.RunNowAsync is the one
// single-fire entry point (a solution's run-at-install calls it too); this route only resolves the
// row and says who asked. Always 200 for a clock trigger: a skip is the answer, not a failure.
app.MapPost("/api/teams/{team}/triggers/{id}/run", async (
    [Description(Describe.Team)] string team,
    [Description("The schedule row's identifier.")] string id,
    TeamRegistry teams,
    ITriggerStore schedules,
    TriggerSweep sweep,
    TriggerCost cost,
    HttpContext context,
    CancellationToken ct) =>
{
    if (teams.ExistingName(team) is not { } stored)
    {
        return Results.NotFound(new { error = $"No team '{team}'." });
    }

    if (await schedules.FindAsync(id, ct) is not { } row
        || !string.Equals(row.Team, stored, StringComparison.OrdinalIgnoreCase))
    {
        return Results.NotFound(new { error = $"No schedule '{id}' on team '{stored}'." });
    }

    var now = DateTimeOffset.UtcNow;
    var run = await sweep.RunNowAsync(
        row.Id,
        now,
        context.User.FindFirstValue(ClaimTypes.NameIdentifier),
        context.User.FindFirstValue(ClaimTypes.Email),
        TenantActions.ScheduleRunNow,
        ct: ct);

    if (run is null)
    {
        return Results.BadRequest(new { error = "Run now fires a schedule (cron, every or once); this trigger fires on an event." });
    }

    return Results.Ok(new { outcome = run.Outcome, reason = run.Reason, seq = run.Seq, trigger = await cost.ViewAsync(run.Trigger, now, ct) });
})
    .WithTags("Teams")
    .HumansOnly()
    .WithSummary("Run one schedule now")
    .WithDescription(
        "Fires a `cron`, `every` or `once` trigger now, the same fire its schedule makes: source "
        + "`schedule:<id>`, its instruction with its `wakeManager`, skipped for a paused team or a busy "
        + "idle-only member, and skipped with its `schedule.skipped` row when its daily token cap is "
        + "reached. Its stored next due time is not moved (a capped one sleeps, as the cap does). "
        + "Answers `outcome` (`fired`, `skipped`, `capped`, `member-missing`), `reason`, `seq` and "
        + "`trigger`, the row as the list routes return it. Audited as `schedule.run-now`.\n\n"
        + "400 for an event or folder trigger; 404 for an unknown team or trigger.");

// "TEST THIS FOLDER". What a folder trigger there would see and how long the listing took,
// so a person can tell a slow share from a wrong path before saving anything. Always 200 for a team
// that exists: a refusal is the answer to the question asked, not a failed request.
app.MapPost("/api/teams/{team}/triggers/test-folder", (
    [Description(Describe.Team)] string team,
    TestFolder request,
    TeamRegistry teams,
    FolderWatch folders) =>
    teams.ExistingName(team) is not { } stored
        ? Results.NotFound(new { error = $"No team '{team}'." })
        : Results.Ok(folders.Test(stored, request.WatchRoot, request.WatchPath, request.WatchGlob)))
    .WithTags("Teams")
    .HumansOnly()
    .WithSummary("Test a folder for a folder trigger")
    .WithDescription(
        "Lists what a `folderChange` trigger on this folder would see: `ok`, `refusal` (a sentence, "
        + "or null), `folder`, `count`, `truncated`, `elapsedMs` and the first 200 `entries` "
        + "(`path`, `size`, `modifiedAt`). Nothing is stored.\n\n"
        + "404 for an unknown team.");

// The cron builder's preview, and the start field's: the next firings of a schedule as the sweep
// would arm it (Triggers.Preview uses NextOccurrence itself), in the schedule's own timezone.
// Always 200 for a team that exists: a shape that cannot run answers with its sentence.
app.MapPost("/api/teams/{team}/triggers/preview", (
    [Description(Describe.Team)] string team,
    PreviewSchedule request,
    TeamRegistry teams) =>
{
    if (teams.ExistingName(team) is null) return Results.NotFound(new { error = $"No team '{team}'." });

    var kind = Enum.GetNames<TriggerKind>().FirstOrDefault(
        name => string.Equals(name, request.Kind?.Trim(), StringComparison.OrdinalIgnoreCase));
    if (kind is null) return Results.Ok(new { occurrences = Array.Empty<DateTimeOffset>(), error = "kind must be one of: cron, every, once." });

    var preview = Triggers.Preview(
        Enum.Parse<TriggerKind>(kind), request.Expression, request.Timezone, request.IntervalSeconds,
        request.FireAt, DateTimeOffset.UtcNow, request.Count ?? 5);
    return Results.Ok(new { occurrences = preview.Occurrences, error = preview.Error });
})
    .WithTags("Teams")
    .HumansOnly()
    .WithSummary("Preview a schedule's next firings")
    .WithDescription(
        "For a `cron`, `every` or `once` shape (`kind`, `expression`, `timezone`, `intervalSeconds`, "
        + "`fireAt` - for cron and every, the start before which it never fires - and `count`, 1 to "
        + "20, default 5), answers `occurrences`, the next firings as the scheduler would arm them, "
        + "and `error`, a sentence when the shape cannot run. Nothing is stored.\n\n"
        + "404 for an unknown team.");

app.MapGet("/api/teams/{team}/triggers/watch-roots", (
    [Description(Describe.Team)] string team,
    TeamRegistry teams,
    FolderWatch folders) =>
    teams.ExistingName(team) is null
        ? Results.NotFound(new { error = $"No team '{team}'." })
        : Results.Ok(folders.RootOptions()))
    .WithTags("Teams")
    .HumansOnly()
    .WithSummary("List the areas a folder trigger may watch")
    .WithDescription(
        "`documents` (the team's documents) first, then every file-browser root configured with "
        + "allowWatch, as `{ value, label }`. The instance's own data folder is never offered.\n\n"
        + "404 for an unknown team.");

// A team's own named values - its shared test credentials, and anything else every member must
// agree on. Its own route for the repos route's reason below.
//
// .HumansOnly() is the WHOLE of the restriction here, in both directions. A member that could WRITE
// this would give itself variables or overwrite the team's credentials; a member that could READ
// it would have a way to enumerate values it was never given. Every member and every
// Concierge runs with permissions bypassed, so there is nothing else standing in the way.
app.MapGet("/api/teams/{team}/env", (
    [Description(Describe.Team)] string team, TeamRegistry teams) =>
    teams.ExistingName(team) is { } stored
        ? Results.Ok(teams.EnvFor(stored))
        : Results.NotFound(new { error = $"No team '{team}'." }))
    .WithTags("Teams")
    .HumansOnly()
    .WithSummary("Read a team's environment")
    .WithDescription(
        "The named values every member of this team is launched with. "
        + "**Values are returned in full and are NOT redacted.** Redaction here would be theatre: "
        + "anyone who can reach this route can already read any of these by putting `{env:NAME}` "
        + "into a member's prompt override and reading the transcript. A control that looks like a "
        + "boundary and is not is worse than an honest absence of one. "
        + "**This is coordination, not confinement.** These sit in the database in plaintext and "
        + "every agent runs with permissions bypassed. Only put values every person in the "
        + "tenant is already entitled to - never a production key.");

// ITS OWN ROUTE, like env and repos beside it. A budget is one number and a PATCH field carrying
// it would have to distinguish absent from cleared, which is exactly the three-state trap
// `PATCH .../containers/{name}` already documents - here a body whose only field IS the budget
// says which of the three it means without a convention.
//
// THE THREE STATES ARE THE POINT. Null is "I have chosen nothing", which returns the team to the
// instance `WorkflowSpendLimit`; 0 is an explicit "unlimited"; a positive value is the figure, and
// it APPLIES EVEN WHEN IT IS ABOVE the instance figure - the team's figure wins, rather than the
// lower of the two.
//
// A BOUND A TEAM CAN RAISE IS NOT A BACKSTOP, and this route is `{team}`-gated so anyone who
// reaches the team can raise their own. That is deliberate: a limit nobody can see is one nobody
// can reason about, and a field that silently refuses what a person typed is a worse lie than a
// number they can change. `WorkflowSpendLimit` stays the thing an unset team is bounded by.
app.MapPut("/api/teams/{team}/budget", async (
    [Description(Describe.Team)] string team,
    TeamBudget? body, TeamRegistry teams, HttpContext context,
    CancellationToken ct) =>
{
    // REFUSED HERE, ON THE ROUTE, not left to the input control: "a boundary a route call can
    // bypass is not a boundary" - the same rule `max-concurrent` states beside it. Left unrefused
    // a negative value would be STORED, because the setter's job is to store what it
    // is given, and it would then read as a bound of less than zero that nothing can ever be
    // under. 0 is NOT refused: it is a meaningful choice here.
    if (body?.BudgetTokens is < 0)
    {
        return Results.BadRequest(new
        {
            error = "budgetTokens cannot be negative. Use 0 for unlimited, or null to use the "
                + "instance figure.",
        });
    }

    // The figure and its tenant_events row are one transaction: a change with no record of who
    // made it does not land.
    try
    {
        await teams.SetBudgetAsync(
            team, body?.BudgetTokens, ct,
            stored => TenantLogging.Row(
                context, TenantActions.TeamBudgetChanged, stored, teams.LabelFor(stored),
                new { budgetTokens = body?.BudgetTokens }));
    }
    catch (InvalidOperationException exception)
    {
        return Results.NotFound(new { error = exception.Message });
    }

    var stored = teams.ExistingName(team)!;

    // THE WHOLE SUMMARY, not the single field back. The caller needs `effectiveWorkflowBudget`
    // alongside the choice it just made - it is what the KPI bar measures against, and a browser
    // that re-derived it would be the second place that knows 0 means unlimited.
    return Results.Ok(teams.All().First(t => string.Equals(t.Id, stored, StringComparison.Ordinal)));
})
    .WithTags("Teams")
    .HumansOnly()
    .WithSummary("Set what this team may spend on ONE workflow")
    .WithDescription(
        "Tokens, input and output together, and the figure bounds **one workflow** rather than "
        + "the team's total: a team with three workflows open has three budgets of this size, one "
        + "each, and nothing is shared between them.\n\n"
        + "**Three states.** `null` (or an absent body) means the team has chosen nothing and the "
        + "instance `WorkflowSpendLimit` applies. `0` means UNLIMITED. A positive value is stored "
        + "exactly as typed and applies **even when it is above the instance figure** - this is "
        + "not a ceiling a team may only go under. 400 for a negative value.\n\n"
        + "A workflow that reaches its figure PAUSES rather than going quiet, and a person - and "
        + "only a person - resumes that one workflow through "
        + "`POST /api/teams/{team}/workflows/{correlation}/resume`.\n\n"
        + "It takes effect on the next WAKE, which is where spending happens, not only on the "
        + "next dispatch.\n\n"
        + "Answers the whole team, so the caller gets `effectiveWorkflowBudget` - the figure "
        + "actually in force, with null meaning unlimited - beside the choice it just made.");

app.MapPut("/api/teams/{team}/env", async (
    [Description(Describe.Team)] string team,
    IReadOnlyDictionary<string, string>? env, TeamRegistry teams, CancellationToken ct) =>
{
    try
    {
        await teams.SetEnvAsync(team, env, ct);
        return Results.Ok(teams.EnvFor(teams.ExistingName(team)!));
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (InvalidOperationException exception)
    {
        return Results.NotFound(new { error = exception.Message });
    }
})
    .WithTags("Teams")
    .HumansOnly()
    .WithSummary("Replace a team's environment")
    .WithDescription(
        "Replaces the whole map; an empty object clears it. Names must look like environment "
        + "variables and may not begin `HARNESS_`, which is the platform's own prefix and "
        + "carries a container's credential. "
        + "Containers already running are updated in place, so a rotated value reaches a live "
        + "agent on its next invocation rather than after a restart.");

// Its OWN route rather than folded into a wider team-settings write: the list is the whole body, so
// clearing it is an explicit empty array rather than a special third state.
app.MapPut("/api/teams/{team}/repos", async (
    [Description(Describe.Team)] string team,
    JsonElement body, TeamRegistry teams, TeamRepoSetup repoSetup, LocalRepos localRepos, CancellationToken ct) =>
{
    // THE BARE ARRAY AS BEFORE, or `{ repos, repoChoices }` to answer a refused check (B001F).
    SetTeamRepos request;
    try
    {
        request = body.ValueKind switch
        {
            JsonValueKind.Array => new SetTeamRepos(body.Deserialize<List<string>>(JsonSerializerOptions.Web), null),
            JsonValueKind.Object => body.Deserialize<SetTeamRepos>(JsonSerializerOptions.Web)!,
            JsonValueKind.Null => new SetTeamRepos(null, null),
            _ => throw new JsonException(),
        };
    }
    catch (JsonException)
    {
        return Results.BadRequest(new { error = "Send the repository list as an array, or as { \"repos\": [...], \"repoChoices\": {...} }." });
    }

    if (teams.ExistingName(team) is not { } stored) return Results.NotFound(new { error = $"No team '{team}'." });

    TeamLocalRepository? localRepository = null;
    var attached = false;
    try
    {
        RepoUrls.Validate(request.Repos, localRepos.Exists);

        // Only a URL the team does not have yet is read: one already attached was checked then.
        var plan = await repoSetup.PlanAsync(
            request.Repos ?? [], request.RepoChoices, person: true, alreadyAttached: teams.ReposFor(stored), ct);
        await repoSetup.CreateOnGitHubAsync(plan, ct);

        var repos = plan.Kept.ToList();
        if (plan.UseLocal && !repos.Any(LocalRepos.IsLocal))
        {
            localRepository = await repoSetup.EnsureLocalAsync(stored, ct);
            repos.Add(localRepository.Reference);
        }

        await teams.SetReposAsync(stored, repos, ct);
        attached = true;
        return Results.Ok(teams.All().Single(t => string.Equals(t.Id, stored, StringComparison.OrdinalIgnoreCase)));
    }
    catch (RepoSetupRefusedException refused)
    {
        return Results.Json(refused.Body, statusCode: refused.Status);
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (InvalidOperationException exception)
    {
        return Results.NotFound(new { error = exception.Message });
    }
    finally
    {
        if (!attached) await repoSetup.ForgetAsync(localRepository, CancellationToken.None);
    }
})
    .WithTags("Teams")
    .HumansOnly()
    .WithSummary("Replace a team's Git repository list")
    .WithDescription(
        "Takes the complete ordered list of absolute http or https repository URLs, or `local:<name>` "
        + "for one of the instance's local repositories (`GET /api/local-repos`). An empty array "
        + "clears it. A `local:` name that is not legal or names no local repository is refused naming it. Every URL is validated before anything is written; repository names are "
        + "derived from the final path segment and unsafe or colliding names are refused. 404 for "
        + "an unknown team, 400 for an invalid URL or derived folder name.\n\n"
        + "**Every URL the team does not have yet is read with `git ls-remote` first (B001F).** One that "
        + "cannot be read is refused with 422 `{ error, code: \"repo-check-failed\", repos: [{ url, failure, "
        + "reason, choices }] }` and the list is unchanged. To answer it, send `{ \"repos\": [...], "
        + "\"repoChoices\": { \"<url>\": \"create-on-github\" | \"use-local\" | \"attach-anyway\" } }` "
        + "instead of the bare array; `use-local` puts the team's local repository in place of the URL.");

// "Create a local repository" in Team settings: the team's own, by the same naming as a new team's.
app.MapPost("/api/teams/{team}/local-repo", async (
    [Description(Describe.Team)] string team,
    TeamRegistry teams, TeamRepoSetup repoSetup, TenantLogging audit, HttpContext context, CancellationToken ct) =>
{
    if (teams.ExistingName(team) is not { } stored) return Results.NotFound(new { error = $"No team '{team}'." });

    var current = teams.ReposFor(stored);
    if (current.FirstOrDefault(LocalRepos.IsLocal) is { } existing)
    {
        return Results.Conflict(new { error = $"{stored} already has a local repository, {existing}." });
    }

    TeamLocalRepository? localRepository = null;
    var attached = false;
    try
    {
        localRepository = await repoSetup.EnsureLocalAsync(stored, ct);
        await teams.SetReposAsync(stored, [.. current, localRepository.Reference], ct);
        attached = true;
    }
    catch (ArgumentException exception)
    {
        return Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status500InternalServerError);
    }
    finally
    {
        if (!attached) await repoSetup.ForgetAsync(localRepository, CancellationToken.None);
    }

    await audit.WriteAsync(
        context, TenantActions.LocalRepoCreated, localRepository.Name, localRepository.Name,
        new
        {
            reference = localRepository.Reference,
            defaultBranch = LocalRepos.InitialBranch,
            team = stored,
            created = localRepository.Created,
        },
        ct);

    return Results.Created($"/api/local-repos/{localRepository.Name}", new
    {
        team = teams.All().Single(t => string.Equals(t.Id, stored, StringComparison.OrdinalIgnoreCase)),
        localRepository = new { name = localRepository.Name, reference = localRepository.Reference, created = localRepository.Created },
    });
})
    .WithTags("Teams")
    .HumansOnly()
    .WithSummary("Create the team's local repository")
    .WithDescription(
        "No body. Creates the team's local repository and adds it to the team's list, which clones it: "
        + "named after the team, `local:<team id>`; an existing one of that name that no team uses is "
        + "reused, and `-2`, `-3` are tried when a team uses it. Answers 201 `{ team, localRepository: "
        + "{ name, reference, created } }`. 409 when the team already has a local repository, 404 for an "
        + "unknown team, 500 naming the reason when the repository cannot be made (nothing is attached). "
        + "Appends `local-repo.created` to the tenant log.\n\n**A person's action.**");

// A person's choice of one repository's default branch. `branch` null or blank clears it,
// and the host goes back to what origin's HEAD named on the last clone or successful Fetch (or not
// known). The choice is stored apart from the remote's value, so no Fetch ever replaces it.
app.MapPut("/api/teams/{team}/repos/{repo}/default-branch", async (
    [Description(Describe.Team)] string team,
    [Description("The repository name (derived from the URL in the team's repos list)")] string repo,
    SetRepoDefaultBranch request, TeamRegistry teams, HttpContext context,
    CancellationToken ct) =>
{
    try
    {
        // One transaction with its tenant_events row.
        await teams.SetPersonDefaultBranchAsync(
            team, repo, request.Branch, ct,
            next => TenantLogging.Row(
                context, TenantActions.RepoDefaultBranchSet, $"{next.Team}/{next.Repo}", null,
                new { setByPerson = next.SetByPerson, fromRemote = next.FromRemote }));
        var stored = teams.ExistingName(team)!;

        return Results.Ok(teams.All().Single(t => string.Equals(t.Id, stored, StringComparison.OrdinalIgnoreCase)));
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (InvalidOperationException exception)
    {
        return Results.NotFound(new { error = exception.Message });
    }
})
    .WithTags("Teams")
    .HumansOnly()
    .WithSummary("Set or clear a repository's default branch")
    .WithDescription(
        "Body `{ \"branch\": \"trunk\" }` sets the branch every Git action on this repository treats "
        + "as main; `null` or blank clears it. A value set here is kept across every later Fetch and "
        + "used until it is cleared; once cleared, the branch origin's HEAD named on the last clone "
        + "or successful Fetch is used, or it is not known. Answers the team. 404 for an unknown "
        + "team or repository, 400 for a name git would not accept as a branch.");

// A repository's contributor settings: its upstream (contributor mode), the fork's owner,
// DCO sign-off and the CLA note. Stored first, then the clone is brought in line - the upstream
// remote added, re-pointed or removed, and nothing else; the sign-off hook written or removed.
// Turning DCO on is refused with the fix when the instance has no git identity, and when a
// commit-msg hook the platform did not write is in the way; neither refusal stores anything.
app.MapPut("/api/teams/{team}/repos/{repo}/contributor", async (
    [Description(Describe.Team)] string team,
    [Description("The repository name (derived from the URL in the team's repos list)")] string repo,
    SetRepoContributor request, TeamRegistry teams, TeamPaths paths, InstanceGitIdentity identity,
    ContributorClone contributorClone, TenantLogging audit, HttpContext context, CancellationToken ct) =>
{
    if (teams.ExistingName(team) is not { } stored)
    {
        return Results.NotFound(new { error = $"No team '{team}'." });
    }

    var name = teams.ReposFor(stored).Select(RepoUrls.DeriveName)
        .FirstOrDefault(n => string.Equals(n, repo, StringComparison.OrdinalIgnoreCase));
    if (name is null)
    {
        return Results.NotFound(new { error = $"No repo '{repo}' on team '{team}'." });
    }

    var clonePath = Path.Combine(paths.ReposFor(stored), name, "main");
    var turningOn = request.DcoSignOff && !teams.ContributorFor(stored, name).DcoSignOff;
    if (turningOn && identity.Current is null)
    {
        return Results.Conflict(new { error = InstanceGitIdentity.Missing });
    }

    if (request.DcoSignOff && File.Exists(DcoHook.PathFor(clonePath)) && !DcoHook.IsOurs(clonePath))
    {
        return Results.Conflict(new
        {
            error = $"A commit-msg hook the platform did not write is already at {DcoHook.PathFor(clonePath)}, "
                + "so sign-off was not turned on. Move it away, then turn the setting on again.",
        });
    }

    RepoContributor saved;
    try
    {
        saved = await teams.SetContributorAsync(
            stored, name, request.UpstreamUrl, request.ForkOwner, request.DcoSignOff, request.ClaSignedNote, ct);
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }

    var applied = await contributorClone.ApplyAsync(clonePath, saved, ct);

    await audit.WriteAsync(
        context, TenantActions.RepoContributorSet,
        subject: $"{stored}/{name}",
        subjectName: null,
        new
        {
            upstream = GitOutputRedaction.RedactUserInfo(saved.UpstreamUrl),
            forkOwner = saved.ForkOwner,
            dcoSignOff = saved.DcoSignOff,
            claSignedNote = saved.ClaSignedNote,
            clone = applied,
        },
        ct);

    if (applied is not null)
    {
        return Results.Json(
            new { error = $"The settings were saved, but the clone was not brought in line: {applied}" },
            statusCode: StatusCodes.Status502BadGateway);
    }

    return Results.Ok(teams.All().Single(t => string.Equals(t.Id, stored, StringComparison.OrdinalIgnoreCase)));
})
    .WithTags("Teams")
    .HumansOnly()
    .WithSummary("Set a repository's contributor settings: upstream, fork owner, DCO, CLA note")
    .WithDescription(
        "Body `{ \"upstreamUrl\": \"https://github.com/project/Widget.git\", \"forkOwner\": null, "
        + "\"dcoSignOff\": true, \"claSignedNote\": \"signed 2026-09-20\" }`. An upstream URL puts the "
        + "repository in contributor mode: its URL is the fork (origin) and the clone gets an `upstream` "
        + "remote beside it; Fetch fetches both, and Bring current and Rebase use upstream/<default>. "
        + "`null` or blank makes it owned again and removes that remote and nothing else. A blank fork "
        + "owner is read from the repository URL. `dcoSignOff` installs a commit-msg hook in the clone "
        + "(its worktrees share it) that adds `Signed-off-by:` with the instance's git identity, once; "
        + "turning it on answers 409 when GIT_AUTHOR_NAME / GIT_AUTHOR_EMAIL are not configured. The CLA "
        + "note is a record, never a signature. Answers the team. 404 for an unknown team or repository, "
        + "400 for an invalid URL or account name.");

// Its OWN route because this is tenant-wide Concierge launch configuration, not team
// metadata at all.
app.MapGet("/api/concierge", async (
    TeamRegistry teams, AgentCatalog catalog, AgentAuthProbe probe, CancellationToken ct) =>
    Results.Ok(await ConciergeView.ReadAsync(teams.Concierge(), catalog, probe, ct)))
    .WithTags("Teams")
    .HumansOnly()
    .WithSummary("What the tenant Concierge launches")
    .WithDescription(
        "One setting for the whole instance. NO TEAM IS NAMED, and that is the point: reading it through a team read as though the setting belonged to that team.\n\n"
        + "There is no prompt setting: the Concierge always runs the built-in Concierge prompt.\n\n"
        + "`effective` is what the launcher will actually use after defaults, from the same code the launcher runs: `agent` with `agentSource` (`chosen` or `default`), and `auth`, the auth probe's verdict for that agent. `agent` is NULL when no agent can start. `auth.signedIn` is true only when the probe measured a sign-in, and `auth.detail` says what it found.\n\n"
        + "**A person's action.**");

app.MapPut("/api/concierge", async (
    SetConcierge request, TeamRegistry teams, HttpContext context,
    CancellationToken ct) =>
{
    try
    {
        // One transaction with its tenant_events row.
        await teams.SetInteractiveAsync(
            request.Agent, ct,
            TenantLogging.Row(context, TenantActions.ConciergeChanged, null, null, new { agent = request.Agent }));

        return Results.NoContent();
    }
    catch (NoSuchAgentException exception)
    {
        // 400, not 404: the TEAM is there, it is the Agent that is missing or cannot be typed at.
        return Results.BadRequest(new { error = exception.Message });
    }
})
    .WithTags("Teams")
    .HumansOnly()
    .WithSummary("Choose which Agent the tenant Concierge launches")
    .WithDescription(
        "The preset must be INTERACTIVE - a headless one has no command that can be typed at, and "
        + "is refused with a message that says which of the two problems it was.\n\n"
        + "Takes effect on the next Concierge opened by any user. A session already "
        + "attached keeps the process it started with.\n\n"
        + "400 when the Agent is unknown or is headless.\n\n"
        + "**A person's action.**");

app.MapPut("/api/teams/{team}/member-agent", async (
    [Description(Describe.Team)] string team,
    SetMemberAgent request, TeamRegistry teams, AgentInstallProbe probe,
    AgentCatalog catalog, HttpContext context, CancellationToken ct) =>
{
    try
    {
        // One transaction with its tenant_events row.
        await teams.SetMemberAgentsAsync(
            team, request.Agents ?? [request.Agent ?? ""], ct,
            agents => TenantLogging.Row(
                context, TenantActions.TeamMemberAgentChanged,
                teams.ExistingName(team), teams.LabelFor(teams.ExistingName(team) ?? team),
                new { agents }));

        var unresolvedList = new List<object>();
        var memberAgents = teams.MemberAgentsFor(team) ?? [];
        foreach (var agent in memberAgents)
        {
            var definition = catalog.Definition(agent);
            if (definition is not null)
            {
                var installation = probe.Probe(definition);
                if (installation.State is not null)
                {
                    unresolvedList.Add(new { agent = installation.Agent, command = installation.Command, message = installation.Message });
                }
            }
        }

        var teamData = teams.All().Single(t => string.Equals(
            t.Id, teams.ExistingName(team), StringComparison.OrdinalIgnoreCase));
        
        // Serialize teamData to JsonNode and add unresolvedAgents
        var node = JsonSerializer.SerializeToNode(teamData, new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } });
        if (node is JsonObject obj)
        {
            obj["unresolvedAgents"] = JsonSerializer.SerializeToNode(unresolvedList, new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } });
        }
        return Results.Json(node, statusCode: 200);
    }
    catch (NoSuchAgentException exception)
    {
        // 400, not 404: the TEAM is there, it is the Agent that is missing or is interactive.
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (InvalidOperationException exception)
    {
        return Results.NotFound(new { error = exception.Message });
    }
})
    // Gated by TEAM access, and covered by TeamGate through declaring `{team}` rather than
    // through anyone remembering: choosing what your own team's new members run is the team's
    // business, exactly as adding one of them is.
    //
    // Its OWN route rather than a field on `PATCH /api/teams/{team}`, mirroring concierge
    // above: that PATCH refuses an empty name, so folding this in would force a caller to resend a
    // name it does not care about - and a slip there renames the team.
    .WithTags("Teams")
    .HumansOnly()
    .WithSummary("Choose which Agent this team's new members run")
    .WithDescription(
        "The ordered allowlist this team's member create uses when it names no `agent` of its own, "
        + "and what a manager hiring through the `member` tool gets.\n\n"
        + "When a hire names no `for` tag, selection starts at the first allowed Agent.\n\n"
        + "The preset must be HEADLESS - a member is woken by a message and never typed at.\n\n"
        + "It is a setting rather than a literal in the manager's own PROMPT, which would not be a "
        + "default at all: a manager copies what it is shown, so a team put deliberately on one CLI "
        + "would hire workers on another.\n\n"
        + "400 when the Agent is unknown or is interactive; 404 for an unknown team.");

app.MapPut("/api/teams/{team}/additional-instructions", async (
    [Description(Describe.Team)] string team,
    SetAdditionalInstructions request, TeamRegistry teams, HttpContext context,
    CancellationToken ct) =>
{
    if (teams.ExistingName(team) is not { } stored)
    {
        return Results.NotFound(new { error = $"No team '{team}'." });
    }

    // One transaction with its tenant_events row. WHETHER they are set, never the words: the log is
    // read by every person and kept forever.
    await teams.SetAdditionalInstructionsAsync(
        stored, request.AdditionalInstructions, ct,
        TenantLogging.Row(
            context, TenantActions.TeamInstructionsChanged, stored, teams.LabelFor(stored),
            new { set = !string.IsNullOrWhiteSpace(request.AdditionalInstructions) }));

    return Results.Ok(teams.All().Single(t => string.Equals(
        t.Id, stored, StringComparison.OrdinalIgnoreCase)));
})
    // Gated by TEAM access, and covered by TeamGate through declaring `{team}`.
    .WithTags("Teams")
    .HumansOnly()
    .WithSummary("Set this team's additional instructions")
    .WithDescription(
        "Free text appended after the built-in role prompt for this team's Manager and members, "
        + "under the heading \"Instructions from the person for this team\". It never replaces "
        + "the role prompt, which is chosen by role and cannot be set. Null or blank clears it. "
        + "Every container on the team is re-prompted, so the next wake of each reads it. Answers "
        + "the team, carrying `additionalInstructions`; 404 for an unknown team.");

app.MapGet("/api/teams/{team}/hiring", async (
    [Description(Describe.Team)] string team,
    TeamRegistry teams,
    ITeamStore teamStore,
    AgentCatalog catalog,
    PluginCatalog plugins,
    ConnectionStore connectionStore,
    CancellationToken ct) =>
{
    if (!teams.Exists(team))
    {
        return Results.NotFound(new { error = $"No team '{team}'." });
    }

    // THE CONNECTIONS A PERSON HAS BOUND ON THIS TEAM: the only ones a Manager's hire may name. By id,
    // name and provider - never an account's token, and not the account itself.
    var uses = await connectionStore.AllUsesAsync(ct);
    var boundHere = (await connectionStore.ListAsync(ct))
        .SelectMany(c => uses[c.Id]
            .Where(u => string.Equals(u.Team, team, StringComparison.OrdinalIgnoreCase))
            .Select(u => new { id = c.Id, name = c.Name, provider = c.Provider, slot = u.Slot }))
        .Distinct()
        .ToArray();

    var allowlist = teams.MemberAgentsFor(team) ?? [];
    var members = (await teamStore.MembersAsync(ct))
        .Where(member => string.Equals(member.Team, team, StringComparison.OrdinalIgnoreCase))
        .ToArray();

    var rows = allowlist.Select(agent =>
    {
        var tags = catalog.Definition(agent)?.Tags?
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray()
            ?? [];

        var counts = tags.Select(tag => new
        {
            tag,
            count = members.Count(member =>
                string.Equals(member.Agent, agent, StringComparison.OrdinalIgnoreCase)
                && string.Equals(member.HiredFor, tag, StringComparison.OrdinalIgnoreCase)),
        });

        return new { agent, tags, counts };
    }).ToArray();

    return Results.Ok(new
    {
        team = teams.ExistingName(team) ?? team,
        allowlist = rows,
        // The roles a hire would ask for that NO allowed agent carries: hiring for one of these
        // falls back to the first allowed agent, and the Manager should know before it asks.
        uncoveredTags = HireTags.Uncovered(rows.Select(row => row.tags)),

        // THE INSTALLED PLUGINS, which a Manager may hire onto its own team with the `member`
        // tool's `plugin`: each one's id, one line, and the skill that says how to use it
        // (`skills_get`), whether or not it is hired anywhere.
        plugins = plugins.Plugins.Select(p => new
        {
            id = p.Manifest.Id,
            reference = MemberRef.ForPlugin(p.Manifest.Id),
            description = p.Manifest.Description,
            skill = PluginSkills.SkillOf(p),
            connections = p.Manifest.Connections.ToDictionary(c => c.Key, c => c.Value.Summary),
        }).ToArray(),

        connections = boundHere,
    });
})
    .WithTags("Teams")
    .RequirePermit(Permits.Read)
    .WithSummary("Show this team's dynamic-member hiring mix")
    .WithDescription(
        "Returns only this team's member-agent allowlist in order, each entry's tags, and the "
        + "current per-tag counts on this team, plus `uncoveredTags`: the hire roles (developer, "
        + "tester, researcher) that no allowed agent carries, and `plugins`: every plugin installed "
        + "on this Host (id, reference, one line, its skill's name, and what each connection slot "
        + "needs), and `connections`: each connection a person has bound on this team (`id`, `name`, "
        + "`provider`, `slot`) - the only ones a Manager's hire may name.\n\n"
        + "This route never returns command lines, `env`, a token, or Agents outside this team's allowlist.");

app.MapPost("/api/teams/{team}/containers", async (
    [Description(Describe.Team)] string team,
    CreateContainer request, TeamRegistry teams, ITeamStore teamStore, AgentCatalog catalog,
    TenantLogging audit, AgentInstallProbe probe, HttpContext context, ISecretStore secretStore,
    IPluginMemberSettingsStore pluginSettings, PluginCatalog plugins, IUserStore users,
    Connections connections, CancellationToken ct) =>
{
    // `name` is free text here too, exactly as it is for a team: the identifier is derived inside
    // TeamRegistry and never asked for. A member called "Data Ingest" is `DataIngest` on disk.
    var name = (request.Name ?? "").Trim();

    if (name.Length == 0) return Results.BadRequest(new { error = "A member needs a name." });
    if (!teams.Exists(team)) return Results.NotFound(new { error = $"No team '{team}'." });

    if (name.Length > TeamRegistry.MaximumLabelLength)
    {
        return Results.BadRequest(new
        {
            error = $"A member name cannot be longer than {TeamRegistry.MaximumLabelLength} characters.",
        });
    }

    try
    {
        // A PERMIT GRANTS THE VERB, NOT THE FIELDS - and that gap is what this refusal closes.
        //
        // `Permits.CreateContainer` says a manager may HIRE. It does not say it may choose what the
        // hire RUNS. Without this refusal `request.Agent` would win unconditionally below, so the
        // team's agent allowlist would be binding on the hire-without-saying path and merely
        // advisory the moment a caller named something - and a team deliberately created on one
        // preset could have its members hired on another by its own manager, a spend decision an
        // agent made on its owner's behalf.
        //
        // THE MANAGER DOES NOT INVENT THE FIELD. The `member` tool advertises `agent` and explains
        // what it does, so an agent picking the one it judges best is behaving exactly as invited.
        // That makes this the platform's job, and a reworded prompt or help page is not the fix.
        //
        // REFUSED RATHER THAN SILENTLY SUBSTITUTED. A manager told "this team hires on X" adapts;
        // one whose choice was quietly replaced believes it got what it asked for, and the mismatch
        // is then visible only to somebody reading the database.
        //
        // AND ONLY ON A MISMATCH. Refusing every `agent` from a machine principal would stop a
        // manager that passes the correct value from hiring at all, which turns a cost guard into a
        // team that cannot work.
        //
        // ApiKey and User are NOT bounded here: both act as a PERSON who chose deliberately.
        // Container and Concierge
        // are the two an LLM holds.
        var allowlist = teams.MemberAgentsFor(team);

        if (allowlist is null || allowlist.Count == 0)
        {
            throw new TeamHasNoMemberAgentException(team);
        }

        var hirer = PrincipalClaims.From(context.User);

        // A MANAGER MAY HIRE ANY INSTALLED PLUGIN ONTO ITS OWN TEAM (a person's decision,
        // 2026-09-27) - the allowlist bounds which AGENTS a hire may run, a spend decision, and a
        // plugin runs no model. It is validated against its manifest exactly as a person's hire is,
        // in `HireMemberAsync`, and an uninstalled one is refused there. Its own team only: the
        // route's `{team}` is TeamGate's, and a Manager's credential reaches no other team.
        //
        // SECRETS ARE LOGICAL KEYS A PERSON HAS ALREADY BOUND ON THIS TEAM (round 2, M1). A Manager
        // never handles a value, and it may bind only a key some PERSON's hire already bound on its
        // own team - optional or not. "Set on this Host" is not the test: the secret store is the
        // Host's whole environment, so that would hand a plugin PATH, HOSTNAME or any operator
        // variable, and the Host cannot tell which names `secret set` wrote (that list lives in the
        // operator's env file, outside the container). A person's binding is a deliberate choice
        // for this team, which is the decision the person made. The set only grows by a person's
        // hire, because a Manager can bind nothing outside it; firing the last member that bound a
        // key takes it out again. The refusal names the key the Manager sent, never a value.
        var managerHiresPlugin = hirer is { Kind: PrincipalKind.Container }
            && request.Agent is { } pluginReference
            && MemberRef.IsPlugin(pluginReference.Trim(), out _);

        if (managerHiresPlugin && request.Secrets is { Count: > 0 } bindings)
        {
            var bound = await pluginSettings.KeysBoundOnAsync(team, ct);

            foreach (var (secret, key) in bindings)
            {
                if (EnvironmentSecretStore.Refusal(key) is { } keyRefusal)
                {
                    return Results.BadRequest(new { error = keyRefusal });
                }

                if (!bound.Contains(key) || secretStore.TryGet(key) is null)
                {
                    return Results.BadRequest(new
                    {
                        error = $"The key `{key}` bound for `{secret}` is not one a person has bound on this team. "
                            + "A Manager binds only logical keys a person has already set (`secret set <KEY>`) and "
                            + "bound on a member of this team, never a value. Ask a person to hire the first member "
                            + "that uses it.",
                    });
                }
            }
        }

        // SETTINGS ONLY A PERSON CHOOSES. A manifest marks a field `"setBy": "person"` when it
        // widens what the plugin does outward - a real send mode, an allowlist. An agent's hire (a
        // Manager's credential, or a Concierge's) may leave such a field at its default and nothing
        // else: incoming content reaches an agent's context by design, so an email saying "hire a
        // sender with this allowlist" must not be able to do it. A person's own hire is not bounded.
        if (hirer is { Kind: PrincipalKind.Container or PrincipalKind.Concierge or PrincipalKind.TenantConcierge }
            && request.Agent is { } agentHired
            && MemberRef.IsPlugin(agentHired.Trim(), out var hiredPluginId)
            && plugins.For(hiredPluginId) is { } hiredPlugin
            && request.Config is { Count: > 0 } agentConfig)
        {
            foreach (var (field, value) in agentConfig)
            {
                if (hiredPlugin.Manifest.Config.TryGetValue(field, out var declared)
                    && declared.AgentRefusal(field, value) is { } personOnly)
                {
                    return Results.BadRequest(new { error = personOnly });
                }
            }
        }

        // CONNECTIONS ARE A PERSON'S CHOICE. A binding points a plugin at somebody's mailbox, so an
        // agent's hire (a Manager's credential, or a Concierge's) may name only a connection a PERSON
        // has already bound on a member of this same team - otherwise incoming content could steer an
        // agent into pointing a plugin at an account nobody chose for this team. Every hire's binding
        // must exist, suit the slot's providers and hold the slot's scopes; the refusal offers
        // Reconnect when only scopes are missing.
        if (request.Connections is { Count: > 0 } connectionBindings)
        {
            if (request.Agent is not { } boundAgent || !MemberRef.IsPlugin(boundAgent.Trim(), out var boundPluginId))
            {
                return Results.BadRequest(new { error = "`connections` belong to a plugin member; an Agent gets no connection and no token." });
            }

            if (plugins.For(boundPluginId) is { } boundPlugin)
            {
                var agentHire = hirer is { Kind: PrincipalKind.Container or PrincipalKind.Concierge or PrincipalKind.TenantConcierge };
                var boundOnTeam = agentHire ? await pluginSettings.ConnectionsBoundOnAsync(team, ct) : null;

                if (await connections.BindingRefusalAsync(boundPlugin.Manifest, connectionBindings, boundOnTeam, ct) is { } bindingRefusal)
                {
                    return Results.BadRequest(bindingRefusal.Body());
                }
            }
        }

        if (request.Agent is { } named
            && !managerHiresPlugin
            && hirer
                is
                {
                    Kind: PrincipalKind.Container
                        or PrincipalKind.Concierge
                        or PrincipalKind.TenantConcierge,
                })
        {
            if (!allowlist.Contains(named, StringComparer.OrdinalIgnoreCase))
            {
                return Results.Json(
                    new
                    {
                        error = $"Members of this team may run: {string.Join(", ", allowlist)}. "
                            + $"Hire without naming an Agent, or name one of those - '{named}' is "
                            + "not yours to choose. A person changes it in Team Settings, under "
                            + "Dynamic members.",
                    },
                    statusCode: StatusCodes.Status403Forbidden);
            }
        }

        var requestedTag = string.IsNullOrWhiteSpace(request.For) ? null : request.For.Trim();
        var chosenByTag = string.IsNullOrWhiteSpace(request.Agent)
            ? await ResolveMemberAgentForHireAsync(team, allowlist, requestedTag, teamStore, catalog, ct)
            : (request.Agent!.Trim(), false);

        // No permits, deliberately and with no way to ask for any. A member added through this
        // route is a WORKER: it gets no credential and no HARNESS_* environment, so its agent
        // cannot call the platform at all. A manager is created with the team and is the only container
        // that gets any, which is what stops "add a member" being a way to mint authority.
        var snapshot = await teams.HireMemberAsync(
            // THE TEAM, never a name in code - the Agent half of the same rule its Prompt follows,
            // and refused rather than defaulted when the team has chosen none.
            team, name,
            chosenByTag.Item1,
            request.SystemPrompt ?? "",
            request.Subscribes ?? [], hiredFor: requestedTag, ct: ct,
            settings: request.Config is null && request.Secrets is null && request.Connections is null
                ? null
                : new PluginMemberSettings(
                    request.Config ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal),
                    request.Secrets ?? new Dictionary<string, string>(StringComparer.Ordinal))
                {
                    Connections = request.Connections ?? new Dictionary<string, string>(StringComparer.Ordinal),
                },

            // THE BINDING'S ROW lands in the same transaction as the member's settings.
            connectionsAudit: request.Connections is { Count: > 0 } hiredBindings
                && request.Agent is { } hiredAgent && MemberRef.IsPlugin(hiredAgent.Trim(), out var hiredId)
                ? member => TenantLogging.Row(
                    context, TenantActions.MemberConnectionsChanged, $"{member.Team}/{member.Name}", name,
                    PluginEndpoints.ConnectionsChangedDetail(
                        member, hiredId, hiredBindings.Keys.Order(StringComparer.Ordinal), PluginMemberSettings.None,
                        PluginMemberSettings.None with { Connections = hiredBindings }))
                : null,

            // THE HIRER wrote the member's own instructions: a Manager through the `member` tool,
            // or a person.
            promptSetBy: await SystemPromptSetters.ForAsync(context, users, ct));

        // Said in the BODY as well as the header: the body is what the `member` tool hands the
        // Manager, and a substitution said only in a header is never seen by anyone who hired.
        string? hiringNotice = null;
        if (chosenByTag.Item2 && requestedTag is { } unresolved)
        {
            context.Response.Headers["X-Harness-Hiring-Notice"] =
                $"no Agent this team may hire on carries the tag '{unresolved}'";
            hiringNotice = HireTags.Substituted(unresolved, snapshot.Agent);
        }

        var unresolvedList = new List<object>();
        var definition = catalog.Definition(snapshot.Agent);
        if (definition is not null)
        {
            var installation = probe.Probe(definition);
            if (installation.State is not null)
            {
                unresolvedList.Add(new { agent = installation.Agent, command = installation.Command, message = installation.Message });
            }
        }

        await audit.WriteAsync(
            context, TenantActions.MemberAdded, $"{snapshot.Team}/{snapshot.Id}", snapshot.Name,
            new { requestedTag, resolvedAgent = snapshot.Agent, team = snapshot.Team }, ct);

        // Serialize snapshot to JsonNode and add unresolvedAgents
        var node = JsonSerializer.SerializeToNode(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } });
        if (node is JsonObject obj)
        {
            obj["unresolvedAgents"] = JsonSerializer.SerializeToNode(unresolvedList, new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } });
            if (hiringNotice is not null) obj["hiringNotice"] = hiringNotice;
        }
        return Results.Json(node, statusCode: 200);
    }
    catch (TeamNameTakenException taken)
    {
        // A DUPLICATE, not a server fault. Every team is created with a Manager, so this is a
        // collision a person reaches by hand, not an unhandled 500. Named by its
        // LABEL, like every other refusal a person reads.
        return Results.Conflict(new { error = $"A member called '{taken.ExistingLabel}' already exists in this team." });
    }
    catch (NoSuchAgentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (PluginSettingsException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (FirehoseSubscriptionException exception)
    {
        // 400, beside its Agent and Prompt siblings: the team and the member both exist and the
        // Agent resolves - it is the SUBSCRIPTION that is refused. A bad target here is never 404,
        // the same reasoning a repoint follows: "not found" reads as the member having vanished.
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (TeamHasNoMemberAgentException exception)
    {
        // The Agent half of the refusal below, and a fourth type rather than words bolted onto it.
        // Same reasoning: a manager reads these, and "no Agent chosen" and "no Prompt chosen" are
        // fixed in two different fields of the same dialog.
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (TeamRootUnreachableException exception)
    {
        // 503, and NOT a 4xx: nothing about this request is wrong. The team is there, the member's
        // name is free and the Agent resolves - what is missing is the team's own folder, which is
        // the Host's side of the conversation. Retrying later is exactly the right response, which
        // is what 503 says and no 4xx does.
        //
        // Unmapped this would be a bare 500 out of Directory.CreateDirectory, for a condition every
        // existing member of the same team explains in one sentence on its own card.
        return Results.Json(
            new { error = exception.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
})
    .WithTags("Members")
    .RequirePermit(Permits.CreateContainer)
    .WithSummary("Add a member to a team")
    .WithDescription(
        "Creates an Agent Container - a team member. Every member is headless: it owns one agent, "
        + "holds a queue with a ceiling, and is woken by the message types it subscribes to. There "
        + "is no interactive mode and no second way in.\n\n"
        + "A member's identity is the pair (team, name), because names are global in the message "
        + "log and a bare name is therefore not an identity. As with a team, send the name a person "
        + "would say and the identifier is derived here.\n\n"
        + "400 for an empty or over-long name; 404 for an unknown team; 409 when the team already "
        + "has a member by that name - every team is created with a Manager, so that is a "
        + "collision reachable by hand; 503 when the team's own folder cannot be reached, which is "
        + "the Host's side of the conversation rather than anything wrong with the request.");

// The STORED row behind a member - its label and its system-prompt OVERRIDE, never the composed
// text its agent actually receives. That composed text is NOT exposed anywhere: `ContainerSnapshot`
// deliberately carries no such field, for two reasons that both matter - it is derived data that
// would answer a question `SystemPrompt` here already answers, disagreeing with it the moment a
// preset's own default changes; and a snapshot rides every SignalR `containerChanged` push to every
// connected client on the team, where a multi-kilobyte prompt has no business travelling on a
// state change nobody asked to see it for.
//
// Carries {team} in its template, so TeamGate covers it the same way its PATCH sibling is covered -
// no bespoke authority check here, and none of the Agent catalog's redaction either: this answers
// the team's OWN stored member, not another principal's preset or credential.
app.MapGet("/api/teams/{team}/containers/{name}", async (
    [Description(Describe.Team)] string team,
    [Description("The member's current identifier, as addressed in its route.")] string name,
    TeamRegistry teams, CancellationToken ct) =>
{
    try
    {
        var member = await teams.MemberAsync(team, name, ct);

        return Results.Ok(new
        {
            team = member.Team,
            id = member.Name,
            name = member.Label ?? member.Name,
            systemPrompt = member.SystemPrompt,
            systemPromptSetBy = member.SystemPromptSetBy?.By,
            systemPromptSetByKind = member.SystemPromptSetBy?.Kind,
            systemPromptSetAt = SystemPromptSetAt(member.SystemPromptSetBy),
        });
    }
    catch (InvalidOperationException exception)
    {
        return Results.NotFound(new { error = exception.Message });
    }
})
    .WithTags("Members")
    .RequirePermit(Permits.Read)
    .WithSummary("Read a member's stored settings")
    .WithDescription(
        "The STORED row behind a member - its label and its own role line, never the composed text "
        + "its agent actually receives (not exposed anywhere). `systemPrompt` is the role line the "
        + "member was hired with, appended after the built-in role prompt, or null when it has none. "
        + "There is no prompt choice: the prompt is chosen by role.\n\n"
        + "`systemPromptSetBy` names who last set `systemPrompt` - a Manager's member id, or a "
        + "person's email - and `systemPromptSetByKind` which (`manager` or `person`); "
        + "`systemPromptSetAt` is when, ISO-8601 UTC. Set at hire and by every PATCH that changes the "
        + "text, a clear included. All three are null for a member nobody is known to have set: one "
        + "from before they were recorded, until it is next edited, or a plugin.\n\n"
        + "404 for an unknown team or member.");

// One member's own recent activity - the message log filtered to one (team, member), newest first.
// This is also the member-toolbar log, so there is one route rather than two that can disagree.
//
// Resolved the same way the GET route above it is: through TeamRegistry.MemberAsync, which resolves
// the team's STORED spelling and confirms the container actually exists before handing back the
// row - so the qualified id built below is the FOUND container's, never the caller's spelling. That
// matters here specifically because ReadForContainerAsync matches on Source, and a type built from
// what was typed is how a call answers 200 over an empty tail forever.
app.MapGet("/api/teams/{team}/containers/{name}/messages", async (
    [Description(Describe.Team)] string team,
    [Description("The member's current identifier, as addressed in its route.")] string name,
    [Description("How many rows, newest first. Default 10, clamped to 100.")] int? take,
    TeamRegistry teams, IMessageLog log, CancellationToken ct) =>
{
    try
    {
        var member = await teams.MemberAsync(team, name, ct);
        var id = new ContainerId(member.Team, member.Name);

        // Clamped rather than trusted: this is a tail, and an agent asking for everything is
        // exactly the context blow-up the transcript was excluded from this feature to avoid.
        var max = Math.Clamp(take ?? 10, 1, 100);

        // The member's own FLOOR, off the row this route has already read. Everything at or below
        // it belongs to whatever same-(team, name) container came before - a deleted team recreated
        // under the same names, or a member deleted and re-hired. Without it this route hands an
        // AGENT its predecessor's task, since the `status` tool reads exactly here, and nothing
        // fails.
        //
        // The STORED floor rather than the live container's: this route already resolves through
        // MemberAsync, and the two are one fact written in one operation - TeamReset writes the row
        // and re-floors the live object together, in that order.
        return Results.Ok(await log.ReadForContainerAsync(id.ToString(), member.FloorSeq, max, ct));
    }
    catch (InvalidOperationException exception)
    {
        return Results.NotFound(new { error = exception.Message });
    }
})
    .WithTags("Activity")
    .RequirePermit(Permits.Read)
    .WithSummary("What one agent container recently published")
    .WithDescription(
        "That container's own rows - started, completed, failed, progress, blocked, needs-decision - newest "
        + "first. It is a TAIL rather than a history: at most 100 rows, and there is no paging.\n\n"
        + "Instructions addressed TO the container are not here. Those are somebody else's "
        + "publication and appear in the team's activity feed.\n\n"
        + "Not the transcript. A transcript is unbounded raw agent output; this is the "
        + "structured record of what the container reported.\n\n"
        + "404 for an unknown team or member.");

// Token totals across every completed/failed run this team published.
//
// Summed on the server with json_extract, not in the browser over the twenty-message feed. A card
// that summed the retained slice would count DOWN as the team got busier, which is the blocked-mark
// failure wearing a spend number. Historical rows without the keys are missing, not zeros.
app.MapGet("/api/teams/{team}/tokens", async (
    [Description(Describe.Team)] string team,
    TeamRegistry teams, IMessageLog log, ContainerHost host, AgentCatalog catalog,
    CancellationToken ct) =>
{
    if (teams.ExistingName(team) is not { } stored)
    {
        return Results.NotFound(new { error = $"No team '{team}'." });
    }

    // THROUGH `TeamRegistry.FloorFor`, WHICH IS THE ONE STORE OF THIS RULE. The kanban board needs
    // the identical figure - without it a recreated team's board shows the cards of the team it
    // replaced - and a second copy is how the next reader ends up being one that does not ask.
    var floor = teams.FloorFor(stored);

    var rows = await log.SumUsageForTeamAsync(stored, floor, ct);

    var tokensIn = rows.Sum(r => r.TokensIn);
    var tokensOut = rows.Sum(r => r.TokensOut);
    var with = rows.Sum(r => r.RunsWithUsage);
    var without = rows.Sum(r => r.RunsWithoutUsage);
    var available = with > 0 || without == 0;
    var partial = available && without > 0;

    // WHETHER A BRAND CAN ANSWER AT ALL, resolved per request and never cached against a
    // container - the drift this codebase already paid for three times. Null means the Agent could
    // not be resolved, which is "we could not tell" and not "it reports nothing": the same
    // not-measured-is-not-a-failure rule the run probe follows.
    bool? ReportsUsage(string brand) =>
        string.IsNullOrEmpty(brand)
            ? null
            : catalog.Definition(brand) is { } definition
                ? definition.Launch.UsageFormat is not null
                : null;

    var members = new List<MemberTokenTotals>();
    var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    foreach (var row in rows.OrderBy(r => r.Source, StringComparer.OrdinalIgnoreCase))
    {
        string member;
        string brand;

        try
        {
            var id = ContainerId.Parse(row.Source);
            member = id.Name;
            brand = host.Find(id)?.Snapshot().Agent ?? "";
        }
        catch (FormatException)
        {
            member = row.Source;
            brand = "";
        }

        listed.Add(member);

        members.Add(MemberTokenTotals.FromUsage(member, brand, row, ReportsUsage(brand)));
    }

    // EVERY MEMBER THE TEAM HOLDS, NOT ONLY THE ONES WITH ROWS. A member mid-first-run has
    // published no completed or failed row, so without this it would be absent from a dialog that
    // claims to say who consumed what - and absence reads as "nothing to report about this member" when the truth is
    // "nothing yet". `runs: 0` is what says which.
    //
    // ADDED AFTER the loop above and guarded on `listed`, so a member that DOES have rows keeps
    // its numbers rather than being overwritten by an empty line. Nothing here touches the team's
    // own totals: those are summed from `rows`, which is the log.
    foreach (var id in teams.ContainerIdsOf(stored)
        .OrderBy(container => container.Name, StringComparer.OrdinalIgnoreCase))
    {
        if (!listed.Add(id.Name)) continue;

        var brand = host.Find(id)?.Snapshot().Agent ?? "";

        members.Add(new MemberTokenTotals(id.Name, brand, null, null, ReportsUsage(brand), 0));
    }

    return Results.Ok(new TeamTokenTotals(
        available,
        tokensIn,
        tokensOut,
        partial,
        with,
        without,
        available
            ? null
            : "Completed and failed payloads on the message log do not carry tokensIn/tokensOut",
        members,
        rows.Sum(r => r.TokensCachedIn),
        rows.Sum(r => r.TokensCacheCreation),
        rows.Sum(r => r.TokensBillable)));
})
    .WithTags("Activity")
    .RequirePermit(Permits.Read)
    .WithSummary("Token totals across a team's completed and failed runs")
    .WithDescription(
        "A TEAM TOTAL from the message log: json_extract of tokensIn/tokensOut on every "
        + "agentContainer.completed and agentContainer.failed this team published, grouped by member.\n\n"
        + "Not a slice of the board's twenty-message feed, and not a guess. Rows that predate "
        + "capture (no keys) are counted as missing rather than as zero; if every run on the "
        + "team is one of those, the answer is unavailable rather than 0.\n\n"
        + "partial is true when some runs on this team carry no usage - either their Agent reports "
        + "none, or they predate capture. Those runs are counted, never summed as zeros. "
        + "partial is true when some runs on the team have usage and earlier ones do not.\n\n"
        + "tokensIn is uncached input. tokensCachedIn (cache reads) and tokensCacheCreation (cache "
        + "writes) are summed beside it, never into it. tokensBillable weights a cache read at 1/10 "
        + "and a cache write at 5/4 and counts a combined total (codex) as reported - the weights "
        + "a workflow budget uses.\n\n"
        + "Brand is the member's current Agent, not a historical one. 404 for an unknown team.");

// How long this team's newest workflow has been going, and how much run time went into it.
//
// Projected on the SERVER over `messages`, beside the tokens route and for the same reason: a
// figure summed in the browser over the board's twenty-message feed would shrink as the team got
// busier. It returns INSTANTS and never a computed elapsed integer - the client subtracts and ticks
// it, so a tile counts up every second with no further traffic.
//
// IT IS FETCHED AND NEVER PUSHED. ContainerSnapshot rides every SignalR frame to every browser
// holding the team and already carries `currentCorrelation`, which is all the push side needs.
app.MapGet("/api/teams/{team}/workflow", async (
    [Description(Describe.Team)] string team,
    TeamRegistry teams, IMessageLog log, ContainerHost host, CancellationToken ct) =>
{
    if (teams.ExistingName(team) is not { } stored)
    {
        return Results.NotFound(new { error = $"No team '{team}'." });
    }

    // THE SAME FLOOR THE TOKENS ROUTE COMPUTES, deliberately the same block rather than a second
    // one: the lowest floor any of this team's live containers holds. Per-source floors would mean
    // the messages table joining team_members, which IMessageLog cannot do - see
    // SumUsageForTeamAsync's own remarks for exactly what that trade costs.
    //
    // Without it a team deleted and recreated under the same names reports its PREDECESSOR'S
    // elapsed time and its predecessor's workflow, because members share their qualified ids with
    // whatever came before. `floor_seq` is this repository's worked example of one store of a fact
    // and several readers only some of which consult it; this is the fourth reader and it consults.
    var floor = teams.ContainerIdsOf(stored)
        .Select(host.Find)
        .Where(container => container is not null)
        .Select(container => container!.Snapshot().SinceSeq)
        .DefaultIfEmpty(0)
        .Min();

    return Results.Ok(await log.ElapsedForTeamAsync(stored, floor, ct));
})
    .WithTags("Activity")
    .RequirePermit(Permits.Read)
    .WithSummary("How long this team's current or last workflow has taken")
    .WithDescription(
        "A LOG PROJECTION over the team's newest workflow: when it started, whether it is still "
        + "open, and how much per-member run time went into it.\n\n"
        + "Two durations, named differently and never added. ELAPSED is wall clock and is not on "
        + "this payload at all - `startedAt` and `serverNow` are, and the client subtracts them "
        + "and ticks the result with no further traffic. `executionSeconds` is per-member run time "
        + "SUMMED, and it legitimately EXCEEDS elapsed whenever two members overlap; that "
        + "inequality is the pairing working rather than a fault.\n\n"
        + "`serverNow` is here because the browser's clock may be wrong. Without it a machine ten "
        + "minutes fast renders a workflow that started in the future.\n\n"
        + "A run that started and has no terminal partner - a host restart mid-flight - is counted "
        + "in `runsUnfinished` and excluded from the total. Never zero, and never still running.\n\n"
        + "`available` is false for a team that has never run. Render an em dash: zero is a "
        + "measured duration and this is an absent one. 404 for an unknown team.");

// EVERY WORKFLOW THIS TEAM HAS RUN SINCE ITS FLOOR, open and closed alike - not only the newest
// and not only the open ones.
//
// A member can hold several workflows at once and keep them apart, so "the newest workflow" no
// longer describes what a team is doing. Same projection as the singular route above, called once
// per correlation instead of once for the newest - see TeamWorkflows.
app.MapGet("/api/teams/{team}/workflows", async (
    [Description(Describe.Team)] string team,
    TeamRegistry teams, IMessageLog log, ContainerHost host, IOutcomeStore outcomes, CancellationToken ct) =>
{
    if (teams.ExistingName(team) is not { } stored)
    {
        return Results.NotFound(new { error = $"No team '{team}'." });
    }

    // THE SAME FLOOR THE SINGULAR ROUTE COMPUTES, deliberately the same block rather than a second
    // one: the lowest floor any of this team's live containers holds. Without it a team deleted and
    // recreated under the same names reports its PREDECESSOR'S open work, because members share
    // their qualified ids with whatever came before.
    var floor = teams.ContainerIdsOf(stored)
        .Select(host.Find)
        .Where(container => container is not null)
        .Select(container => container!.Snapshot().SinceSeq)
        .DefaultIfEmpty(0)
        .Min();

    var listed = await log.WorkflowsForTeamAsync(stored, floor, ct);

    // EACH ROW'S OUTCOME, in one query for the whole list: the workflows view's Outcome column.
    var served = await outcomes.CurrentOutcomesAsync(
        listed.Workflows.Where(w => w.Correlation is not null).Select(w => w.Correlation!.Value).ToList(), ct);

    return Results.Ok(listed with
    {
        Workflows = listed.Workflows
            .Select(w => w.Correlation is { } c && served.TryGetValue(c, out var outcome) ? w with { Outcome = outcome } : w)
            .ToList(),
    });
})
    .WithTags("Activity")
    .RequirePermit(Permits.Read)
    .WithSummary("Every workflow this team has run since its floor")
    .WithDescription(
        "A LOG PROJECTION over EVERY workflow this team has run since its floor - open AND closed, "
        + "not only the newest: bounds, completion, per-member outcomes, declarations, failures and "
        + "run pairing, per workflow. Each row reports its own state, so a finished workflow says "
        + "so rather than being listed as work still in flight.\n\n"
        + "`earliestStartedAt` is the start of the SPAN across every OPEN workflow and is never a "
        + "sum of their durations - three workflows covering the same ten minutes summed would read "
        + "as thirty, a number larger than the time that has actually passed. There is no total "
        + "duration field on this payload and there must not be one. It is null when nothing is "
        + "open, even though `workflows` still lists the finished ones.\n\n"
        + "An open workflow is one with no terminal row (`workflow.completed` or `workflow.closed`) "
        + "that nothing has woken since - see `WorkflowOpenSql` for the one place that predicate "
        + "lives.\n\n"
        + "TWO COUNTS, AND THEY ANSWER DIFFERENT QUESTIONS. `openCount` is how many workflows this "
        + "team HOLDS OPEN, and it is ALLOWED TO GROW: a wake that ends without a declaration or a "
        + "closure leaves a workflow open forever, and this number is what makes that visible. "
        + "`totalCount` is every workflow the team has run since its floor, uncapped - compare "
        + "`workflows.length` against THAT to tell truncation, never against `openCount`.\n\n"
        + "`workflows` lists them newest first, capped at fifty, so a team that just finished "
        + "everything still has something to render rather than an em dash where a completion "
        + "belongs.\n\n"
        + "`available` is false for a team that has never run. Render an em dash: zero is a "
        + "measured count and this is an absent one. 404 for an unknown team.");

// WHICH WORKFLOWS ARE OPEN UNDER ONE MEMBER - the read-side twin of `workflow-complete`.
//
// `/api/teams/{team}/workflows` lists the TEAM'S open workflows and nothing
// else - not which of them are a Manager's own to declare, and not which of them nobody is
// working. A Manager can end its last wake holding an open workflow it said it would close, and
// every fact needed to notice that lives in three places; this route puts them together.
//
// IT DECLARES NOTHING AND CHANGES NOTHING, and that is deliberate rather than incidental: a verb
// CANNOT be the fix, because a skill instructs and cannot enforce. This
// makes the state legible. It is not a safeguard and must not be built on as one.
//
// IT COMPOSES EXISTING PIECES rather than deriving anything of its own:
//
//   * `WorkflowsForTeamAsync` - every workflow since the floor, open and closed alike, newest
//     first, capped at fifty, with the uncapped `openCount` beside it.
//   * `WorkflowOwner.OfAsync` - who may declare a workflow, with the SAME Manager fallback
//     `workflow-complete` applies, because two answers to "whose is it" is how a member is told it
//     may close something the declaring route then refuses.
//   * `WorkflowBusyState.DescribeAsync` - what is still in flight under a correlation, with the
//     SAME caller exclusion, for the same reason: "empty here" and "not refused there" have to mean
//     one thing about one instant, or this route sends a member to a refusal it predicted away.
//
// `{name}` IS A FILTER, NOT AN AUTHORITY CLAIM, and the permit is plain Read like every other
// read route on a member (`GET .../containers/{name}`, `.../messages`). It exposes nothing a team
// reader could not already assemble from the routes above; what it adds is the assembly. The
// member it names is the one whose own Running state is excluded from every `working` list, which
// is a question about WHOSE VIEW this is and not about who is asking - and `{team}` in the route
// means TeamGate covers the access question structurally, as it does everywhere else.
//
// CLOSED WORKFLOWS ARE FILTERED HERE, on `EndedAt is null` against a typed record, not by a client
// on the far side of the wire: a payload that never carries a closed workflow cannot be rendered
// wrongly by a client that filters it wrongly.
app.MapGet("/api/teams/{team}/containers/{name}/workflows", async (
    [Description(Describe.Team)] string team,
    [Description(
        "The member to answer for. A FILTER and not an authority claim: it selects whose view this "
        + "is - whose own Running state is excluded from each `working` list, and which workflows "
        + "come back marked `yours`.")]
    string name,
    TeamRegistry teams, IMessageLog log, ContainerHost host, IPendingDeliveries pending,
    CancellationToken ct) =>
{
    if (teams.ExistingName(team) is not { } stored)
    {
        return Results.NotFound(new { error = $"No team '{team}'." });
    }

    if (host.Find(new ContainerId(stored, name)) is not { } container)
    {
        return Results.NotFound(new { error = $"No member '{name}'." });
    }

    // THE SAME FLOOR THE TWO ROUTES ABOVE COMPUTE, deliberately the same block rather than a
    // fourth spelling: without it a team deleted and recreated under the same names reports its
    // PREDECESSOR'S open work, because members share their qualified ids with whatever came before.
    var floor = teams.ContainerIdsOf(stored)
        .Select(host.Find)
        .Where(found => found is not null)
        .Select(found => found!.Snapshot().SinceSeq)
        .DefaultIfEmpty(0)
        .Min();

    var projection = await log.WorkflowsForTeamAsync(stored, floor, ct);
    var snapshot = container.Snapshot();

    if (!projection.Available)
    {
        // UNAVAILABLE IS NOT "NOTHING IS OPEN", and the two must not collapse into one empty list:
        // a team that has never run cannot answer the question, where a team holding nothing open
        // has answered it. `missing` carries the words, exactly as the sibling routes do.
        return Results.Ok(new MemberOpenWorkflows(
            false, snapshot.Id, 0, projection.ServerNow, [], projection.Missing));
    }

    var rows = new List<OpenWorkflowRow>();

    foreach (var workflow in projection.Workflows)
    {
        // `EndedAt` IS THE OPEN TEST, not `state` - the predicate the `status` tool, the team
        // tile and `teamKpis.ts` all already settled on. `state` is a seven-value string needing an
        // exclusion list a new state could silently join, and a FAILED workflow is emphatically
        // still open.
        if (workflow.EndedAt is not null) continue;

        // A ROW WITH NO CORRELATION CANNOT BE ASKED ABOUT. The projection types it nullable and a
        // null would make both lookups below match nothing, turning "unattributable" into
        // "nobody is working it" - the one answer this route must never invent.
        if (workflow.Correlation is not { } correlation) continue;

        var owner = await WorkflowOwner.OfAsync(log, correlation, ct)
            ?? new ContainerId(stored, TeamRegistry.DefaultManagerName);

        var working = await WorkflowBusyState.DescribeAsync(
            stored, correlation, host, pending, log, container.Id, ct);

        rows.Add(new OpenWorkflowRow(
            correlation,
            workflow.Subject,
            workflow.StartedAt,
            workflow.LastActivityAt,
            owner.Name,
            owner.Equals(container.Id),
            snapshot.CurrentCorrelation == correlation,
            [.. workflow.Members.Select(member => member.Member)],
            working));
    }

    return Results.Ok(new MemberOpenWorkflows(
        true, snapshot.Id, projection.OpenCount, projection.ServerNow, rows));
})
    .WithTags("Activity")
    .RequirePermit(Permits.Read)
    .WithSummary("Which workflows are open under this member")
    .WithDescription(
        "EVERY OPEN WORKFLOW ON THIS TEAM, seen from one member: what it is called, who may declare "
        + "it complete, and whether anything is still working it.\n\n"
        + "This is the read-side twin of `workflow-complete`. That route answers 'may I declare "
        + "this' for ONE workflow with a 204 or a refusal naming what is busy; this asks the same "
        + "questions of every open workflow at once and answers them in a list. It declares "
        + "nothing, closes nothing and changes nothing.\n\n"
        + "`owner` is which member may declare each workflow - the container the root instruction "
        + "addressed, falling back to this team's Manager when the root addressed nobody (a "
        + "schedule firing, a kanban card event). `yours` is whether that owner is the member named "
        + "in the route; `current` is whether that member is running under the workflow at this "
        + "instant, which is false between invocations.\n\n"
        + "`working` IS THE FIELD THIS ROUTE EXISTS FOR. It carries the same sentences "
        + "`workflow-complete` refuses with, from the same predicate: a member Running under the "
        + "workflow, or a delivery accepted under it. AN EMPTY LIST MEANS NOTHING IS COMING - "
        + "nobody is working that workflow and nobody has accepted work under it, so no event it "
        + "could produce will ever wake anybody about it again. The member named in the route is "
        + "excluded from every list, on the same terms `workflow-complete` excludes its caller, "
        + "because a member reads this from inside its own run.\n\n"
        + "Closed workflows are filtered out server-side on `endedAt`, so nothing here announces "
        + "delivered work as live. `openCount` counts what the TEAM holds open, uncapped - compare "
        + "`workflows.length` against it to tell truncation; the list inherits the fifty-entry cap "
        + "on `GET /api/teams/{team}/workflows` and never adds a second one.\n\n"
        + "`correlation` is for finding the thread (`GET /api/workflows/{correlationId}`). It is "
        + "NOT an argument to `workflow-complete`, which takes its correlation from the container's "
        + "own causation and never from a caller.\n\n"
        + "`available` is false for a team that has never run - which is not the same as nothing "
        + "being open, and `missing` says so in words. 404 for an unknown team or member.");

// EVERY REACHABLE TEAM'S PROJECTION, in one read, for the Teams table.
//
// It declares no `{team}`, so TeamGate does not cover it and it resolves access itself - exactly
// the shape `/api/overview` above uses, and for the same reason. There is NO second authority
// question to ask here: `EffectiveTeamsAsync` already answers "everything that exists" for a
// person, so the ordering trap that `/api/me/current-team` has to be careful about (asking
// the effective set alone answers a person 403 for a typo, and everybody 403 for the moments
// after a restart) cannot arise for a route that names no team.
//
// NOT folded into `/api/overview`: that is what the `status` tool reads, and every status call
// would then pay a log projection per team for data it never shows.
app.MapGet("/api/teams/rollup", async (
    HttpContext context, TeamRegistry teams, TeamAccess access, IMessageLog log, ContainerHost host,
    WipLedger wip, CancellationToken ct) =>
{
    if (PrincipalClaims.From(context.User) is not { } principal) return Results.Unauthorized();

    var effective = await access.EffectiveTeamsAsync(principal, ct);
    var rows = new List<TeamRollupRow>();

    // ONE READ OF THE LEDGER FOR EVERY ROW, so two teams' counts are from the same instant.
    var slots = wip.View();

    foreach (var team in teams.All().Where(t => effective.Contains(t.Id)))
    {
        // THE SAME FLOOR THE PER-TEAM ROUTES COMPUTE, and it must be the same: without it a team
        // deleted and recreated under the same names would otherwise report its PREDECESSOR'S
        // workflow, because members share their qualified ids with whatever came before.
        var floor = teams.ContainerIdsOf(team.Id)
            .Select(host.Find)
            .Where(container => container is not null)
            .Select(container => container!.Snapshot().SinceSeq)
            .DefaultIfEmpty(0)
            .Min();

        // THE PLURAL PROJECTION, BESIDE THE SINGULAR ONE, ON THE SAME FLOOR - the Teams table needs
        // every team's workflows, not only the team a person happens to have made active, and this
        // is the one place that already loops every reachable team. `WorkflowsForTeamAsync` runs
        // four queries plus one `TimingForAsync` per workflow in the capped list; this roughly
        // DOUBLES the per-team cost of a route the SPA already polls, and the list is every workflow
        // a team has run since its floor, so the per-workflow projections run up to the
        // fifty-entry cap rather than up to the open count. Accepted: the alternative is a Workflows column that only ever answers for one
        // team per page load, which is not a working feature. Named here so a later profiler finds
        // the reasoning rather than a surprise.
        var elapsed = await log.ElapsedForTeamAsync(team.Id, floor, ct);
        var open = await log.WorkflowsForTeamAsync(team.Id, floor, ct);

        var held = slots.Waiting
            .Where(hold => string.Equals(hold.Team, team.Id, StringComparison.OrdinalIgnoreCase))
            .Select(hold => hold.Member)
            .ToList();
        var running = slots.Running
            .Count(hold => string.Equals(hold.Team, team.Id, StringComparison.OrdinalIgnoreCase));

        rows.Add(new TeamRollupRow(
            team.Id, elapsed, open, running, held.Count, held,
            held.Count > 0 ? TeamRollupRow.WaitingForASlot : null));
    }

    return Results.Ok(new TeamRollup(rows));
})
    .WithTags("Activity")
    .RequirePermit(Permits.Read)
    .WithSummary("Every reachable team's current or last workflow")
    .WithDescription(
        "One row per team the caller reaches, each carrying BOTH projections: `workflow`, the same "
        + "one `GET /api/teams/{team}/workflow` answers for that team, and `openWorkflows`, the "
        + "same one `GET /api/teams/{team}/workflows` answers - every workflow that team has run "
        + "since its floor, open and closed alike, not only the newest. Both are computed from the "
        + "same floor, in the same loop, so a team's two figures here can never name a different "
        + "predecessor than each other.\n\n"
        + "`running` and `waiting` count this team's holders of, and waiters for, the "
        + "instance-wide run slots (`GET /api/wip`); `held` names the waiting members in queue "
        + "order, and `slotStatus` reads \"waiting for a slot\" while any are waiting - a team "
        + "whose Manager has a delivered wake held by the limit is waiting, not idle.\n\n"
        + "It carries NOTHING ELSE - no name, no containers. Those are on "
        + "`/api/overview`, which a console already holds, and a second source for a team's name is "
        + "a second answer waiting to disagree with the first.\n\n"
        + "An empty list means the caller reaches no team. It is not a refusal.");

// Changes what a member is CALLED, what it is TOLD, and what it RUNS.
//
// `agent` is repointable here because the runner resolves the preset by name on every invocation -
// there is no per-container map of resolved commands - so a repoint is one write and takes effect
// on the member's next wake.
//
// Carries {team} in its template, so TeamGate covers it the same way the member-create route
// beside it is covered - no bespoke authority check here.
app.MapPatch("/api/teams/{team}/containers/{name}", async (
    [Description(Describe.Team)] string team,
    [Description("The member's current identifier, as addressed in its route.")] string name,
    UpdateContainer request, TeamRegistry teams, AgentInstallProbe probe,
    AgentCatalog catalog, HttpContext context, IUserStore users, CancellationToken ct) =>
{
    try
    {
        // The row and its tenant_events rows are one transaction: an edit with no record of who
        // made it does not land.
        //
        // WHICH fields moved, not what they moved to. A prompt is a person's words and can be long;
        // recording that one changed keeps the log readable and still answers "who touched this".
        var update = await teams.UpdateMemberAsync(
            team, name, request.Name, request.SystemPrompt, request.Agent,
            await SystemPromptSetters.ForAsync(context, users, ct), ct,
            change => MemberAuditRows(context, change, request.Agent));
        var updated = update.Snapshot;

        var unresolvedList = new List<object>();
        
        // Only probe if agent was in the body
        if (!string.IsNullOrEmpty(request.Agent))
        {
            var definition = catalog.Definition(updated.Agent);
            if (definition is not null)
            {
                var installation = probe.Probe(definition);
                if (installation.State is not null)
                {
                    unresolvedList.Add(new { agent = installation.Agent, command = installation.Command, message = installation.Message });
                }
            }
        }

        // Serialize updated to JsonNode and add unresolvedAgents
        var node = JsonSerializer.SerializeToNode(updated, new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } });
        if (node is JsonObject obj)
        {
            obj["unresolvedAgents"] = JsonSerializer.SerializeToNode(unresolvedList, new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } });

            // The same three the GET answers, so a client that just saved need not read back.
            obj["systemPromptSetBy"] = update.Row.SystemPromptSetBy?.By;
            obj["systemPromptSetByKind"] = update.Row.SystemPromptSetBy?.Kind;
            obj["systemPromptSetAt"] = SystemPromptSetAt(update.Row.SystemPromptSetBy);
        }
        return Results.Json(node, statusCode: 200);
    }
    catch (NoSuchAgentException exception)
    {
        // 400, not the 404 the generic catch below would give: the TEAM and the MEMBER both exist -
        // it is the requested Agent that does not, or is interactive. Answering "not found" for a
        // route whose subject is right there reads as the member having vanished. Caught ahead of
        // InvalidOperationException, which it derives from; the compiler enforces the order.
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (FirehoseSubscriptionException exception)
    {
        // 400, not the 404 the generic catch below would give, for the same reason as its two
        // siblings above: the team and the member both exist, and a repoint that would turn a
        // legal pair illegal is refused before the row is written. Caught ahead of
        // InvalidOperationException, which it derives from.
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (InvalidOperationException exception)
    {
        return Results.NotFound(new { error = exception.Message });
    }
})
    .WithTags("Members")
    .HumansOnly()
    .WithSummary("Change a member's name, system prompt or Agent")
    .WithDescription(
        "Changes what a member is CALLED, what it is TOLD, and what it RUNS. `agent` must name a "
        + "HEADLESS preset - a member is woken by a message and never typed at. The change takes "
        + "effect on the member's next wake, with no restart, because the catalog entry a runner "
        + "resolves is written in the same operation as the stored row.\n\n"
        + "A system prompt equal to its preset's current default - Ordinal comparison - is stored "
        + "as no override at all, so a member left tracking its preset keeps tracking it as the "
        + "preset improves, and that comparison is against the preset the member ends up on. The "
        + "live container is re-prompted immediately; nothing here waits for a restart.\n\n"
        + "A MANAGER IS EDITABLE LIKE ANY OTHER MEMBER - its label, Agent, Prompt and added "
        + "instructions alike. Its team's CURRENT name and roster still reach it on every run: "
        + "an override is a TEMPLATE resolved on every composition, so "
        + "`{team}` and `{members}` stay exactly as live inside one as inside a preset default, "
        + "and the words are ADDED to the composed System Prompt rather than substituted for it. "
        + "Relabelling a manager and repointing its Prompt is how a team takes on a ROLE - an "
        + "`Auditor Manager`, a `Security Manager` - with no second mechanism behind it.\n\n"
        + "A change to `systemPrompt` - a clear included, a resend of the same words not - records "
        + "the person as who last set it (`systemPromptSetBy`, `systemPromptSetByKind`, "
        + "`systemPromptSetAt`, answered here and on GET) and appends `member.instructions-changed` "
        + "to the tenant log. People only: a Manager sets a member's instructions once, at hire.\n\n"
        + "400 when `agent` names a preset this tenant does not have, or an interactive one.\n\n"
        + "404 for an unknown team or member.");

// The ORDER the work happens in is MemberDeletion's, and its doc comment is where the reasoning
// lives. Gated by {team} alone, like the POST and PATCH above it and deliberately NOT people-only
// the way deleting a TEAM is: whoever can add a member can remove one, and a member is re-creatable
// in a way a team full of documents and transcripts is not.
app.MapDelete("/api/teams/{team}/containers/{name}", async (
    [Description(Describe.Team)] string team,
    [Description("The member's identifier, as addressed in its route.")] string name,
    MemberDeletion deletion, TenantLogging audit, HttpContext context,
    CancellationToken ct) =>
{
    try
    {
        // Null means no such team OR no such member, and both are a 404 rather than a failure:
        // asking twice is not a fault, and a caller retrying after a timeout should not be told
        // something broke.
        if (await deletion.DeleteAsync(team, name, ct) is not { } removed)
        {
            return Results.NotFound(new { error = $"No member '{name}' on team '{team}'." });
        }

        // Exactly what went. Written AFTER the delete: recording an intention that then failed
        // would be worse than recording nothing. The LABEL travels beside the identifier because
        // afterwards there is nothing left to read it from, and a row naming an identifier a person
        // never saw is a row they cannot match to what they did.
        await audit.WriteAsync(
            context, TenantActions.MemberDeleted, $"{removed.Team}/{removed.Member}", removed.Label,
            // Named explicitly rather than by shorthand, and the reason is visible in the log
            // itself: a shorthand property keeps its C# casing, and nothing camel-cases this
            // detail on the way out - so `removed.Failures` beside `team = ...` writes one object
            // spelt two ways, in a column a person reads.
            new
            {
                team = removed.Team,
                pendingDeliveries = removed.PendingDeliveries,
                schedules = removed.Schedules,
                directories = removed.Directories,
                failures = removed.Failures,
                remaining = removed.Remaining,
            },
            ct);

        // 200 with a body rather than 204, for the reason deleting a team answers one: a workspace
        // that could not be removed is still a deletion, and a caller told only "no content" cannot
        // say which files are still on disk.
        return Results.Ok(removed);
    }
    catch (ManagerCannotBeDeletedException exception)
    {
        // 409 rather than 403: nothing about the caller would make this allowed.
        return Results.Conflict(new { error = exception.Message });
    }
})
    .WithTags("Members")
    .HumansOnly()
    .WithSummary("Delete a member and everything that names it")
    .WithDescription(
        "Stops the member's Agent Container, removes its cursor, subscriptions, outstanding "
        + "deliveries and credential, deletes its row, and removes its workspace directory.\n\n"
        + "**The team's manager is re-prompted**, so it stops being told it can dispatch to a "
        + "member that is no longer there.\n\n"
        + "**Transcripts are kept**, unlike a team deletion, because messages already in the log "
        + "carry paths into them and the log is append-only and untouched here.\n\n"
        + "Answers 200 with what was removed, including a workspace that could NOT be deleted - "
        + "usually a file still held by a child process that has not finished exiting. Those are "
        + "named rather than swallowed, and the member is gone either way.\n\n"
        + "409 for a team's manager: there is no team without a door into it, so delete the team "
        + "itself instead.\n\n"
        + "404 for an unknown team or member; asking twice is not an error.");

// EVERY DOCUMENTS FOLDER ON DISK, WHETHER OR NOT ITS TEAM IS STILL THERE.
//
// The first half of what the Documents dialog needs, since it hangs off Projects rather than the
// active team: it has to offer a team to look at before it can show one, and the set it offers is
// the set of FOLDERS, which is strictly larger than the set of teams. That difference is the whole
// item - a document outlives the team that wrote it.
//
// NOT UNDER /api/teams/{team}/, deliberately: it is not about one team, and a `{team}` route value
// is what TeamGate keys on. This one carries none, so it does its own filtering, below and in the
// open.
//
// HumansOnly. This is a picker for a person: it names folders across every team the caller can
// reach, and no container has a reason to enumerate other teams' documents. The per-folder routes
// keep RequirePermit(Read) so an agent goes on reading its OWN team's, exactly as before.
app.MapGet("/api/documents", (TeamDocuments docs, TeamRegistry teams, HttpContext context) =>
{
    if (PrincipalClaims.From(context.User) is null) return Results.Unauthorized();

    // EVERY FOLDER, retired ones and dead teams' included. HumansOnly, and a person reaches every
    // team and every documents folder - TeamGate's rule 3 is what opens a folder whose team no
    // longer exists, so a list filtered any narrower would hide exactly the work this list exists
    // to keep findable.
    //
    // A NAMED RECORD RATHER THAN AN ANONYMOUS OBJECT, so the wire shape is one the parity gate can
    // see and hold against `web/src/api/types.ts`. `exists` and `label` are the registry's answers
    // and are added HERE rather than on the disk record, which cannot check either - see
    // DocumentsFolder's own summary for why `exists` is not simply "the team is in the list".
    var folders = docs.Folders()
        .Select(folder => new DocumentsFolder(
            folder.Folder,
            folder.Team,
            teams.LabelFor(folder.Team),
            !folder.Retired && teams.ExistingName(folder.Team) is not null,
            folder.Retired,
            folder.Entries,
            folder.ModifiedAt));

    return Results.Ok(new { folders });
})
    .WithTags("Documents")
    .HumansOnly()
    .WithSummary("List the team documents folders that exist")
    .WithDescription(
        "One entry per folder under the tenant documents root, each saying whether that team still "
        + "exists. Documents are NOT removed when a team is deleted, so this list is larger than "
        + "the list of teams and the difference is the work that would otherwise have been lost "
        + "with them.\n\n"
        + "`folder` is the name to put in `/api/teams/{team}/documents` to browse it. For a live "
        + "team that IS the team identifier. For a RETIRED folder it is not and never can be: a "
        + "team created with a dead team's identifier does not inherit its documents - the "
        + "predecessor's folder is relabelled with a suffix no team name may contain, and `team` "
        + "still says whose it was.\n\n"
        + "Every person sees every folder, including those of teams that are gone.");

// A team's documents. Every path arrives from outside and is resolved against the team's own
// folder before anything touches disk - see TeamDocuments. A team name is matched
// case-insensitively and then used in its STORED spelling, so the folder a request reaches is the
// one the board shows.
// WithTags only. A group-level WithDescription would be REPLACED on every route that sets its own,
// which is most of them - see Describe.Documents, which is appended per route instead.
var documents = app.MapGroup("/api/teams/{team}/documents").WithTags("Documents");

documents.MapGet("", (
    [Description(Describe.Team)] string team,
    [Description(
        "The folder to list, relative to the team's documents root. Omit it for the root itself.")]
    string? path,
    [Description("List the whole tree beneath `path` rather than just its immediate children.")]
    bool? recursive,
    TeamRegistry teams, TeamDocuments docs) =>
    DocumentsFolderOf(teams, docs, team) is not { } folder
        ? Results.NotFound(new { error = $"No team '{team}'." })
        : Documents(() => Results.Ok(docs.List(folder, path, recursive ?? false))))
    .RequirePermit(Permits.Read)
    .WithSummary("List a team's documents")
    .WithDescription(
        "The files and folders a team shares with its members - what a person uploads for an agent "
        + "to read, and what an agent leaves behind." + Describe.Documents);

documents.MapGet("/content", (
    [Description(Describe.Team)] string team,
    [Description("The file to download, relative to the team's documents root.")] string path,
    TeamRegistry teams, TeamDocuments docs) =>
{
    if (DocumentsFolderOf(teams, docs, team) is not { } stored)
    {
        return Results.NotFound(new { error = $"No team '{team}'." });
    }

    return Documents(() =>
    {
        var file = docs.Resolve(stored, path);

        if (!File.Exists(file)) return Results.NotFound(new { error = "No such document." });

        // A download rather than an inline render: this serves whatever a person or an agent put
        // there, and handing the browser an HTML document to execute on this origin would make an
        // uploaded file into a script running inside the session.
        return Results.File(file, "application/octet-stream", Path.GetFileName(file));
    });
})
    .RequirePermit(Permits.Read)
    .WithSummary("Download a document")
    .WithDescription(
        "Returns the file's bytes as a download, always as `application/octet-stream` and never "
        + "inline. This serves whatever a person or an agent put there, and handing the browser an "
        + "uploaded HTML document to render would make it a script running on this origin, inside "
        + "the session." + Describe.Documents);

// The same file as /content, shown rather than downloaded: by extension only,
// told to the browser exactly (nosniff), and inside a sandboxed CSP that replaces the app's - see
// DocumentView for why an uploaded HTML page can render on this origin without running.
documents.MapGet("/view", async (
    [Description(Describe.Team)] string team,
    [Description("The file to show, relative to the team's documents root.")] string path,
    TeamRegistry teams, TeamDocuments docs, HttpContext context, CancellationToken ct) =>
{
    if (DocumentsFolderOf(teams, docs, team) is not { } stored)
    {
        return Results.NotFound(new { error = $"No team '{team}'." });
    }

    return await DocumentsAsync(async () =>
    {
        var file = docs.Resolve(stored, path);

        if (!File.Exists(file)) return Results.NotFound(new { error = "No such document." });

        var name = Path.GetFileName(file);

        if (DocumentView.For(name) is not { } type)
        {
            return Results.Json(
                new { error = "This file cannot be shown in the browser. Download it instead." },
                statusCode: StatusCodes.Status415UnsupportedMediaType);
        }

        context.Items[SecurityHeaders.PolicyOverride] = DocumentView.PolicyFor(type.Kind);
        context.Response.Headers.ContentDisposition =
            new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("inline") { FileNameStar = name }.ToString();

        if (type.Kind == DocumentView.Kind.Markdown)
        {
            var markdown = await File.ReadAllTextAsync(file, ct);
            return Results.Text(DocumentView.RenderMarkdown(name, markdown), type.ContentType);
        }

        return Results.File(file, type.ContentType);
    });
})
    .RequirePermit(Permits.Read)
    .WithSummary("Show a document in the browser")
    .WithDescription(
        "Returns the file inline, typed by its extension: Markdown (`.md`, `.markdown`) rendered to "
        + "an HTML page with raw HTML disabled; HTML as `text/html`; text and source files as "
        + "`text/plain; charset=utf-8`; images as their type; PDF as `application/pdf`. Anything "
        + "else answers 415 - download it with `/content`.\n\n"
        + "Every response carries `X-Content-Type-Options: nosniff`, and every one REPLACES the "
        + "app's Content-Security-Policy with a sandboxed one: `" + DocumentView.SandboxPolicy + "`, "
        + "or `" + DocumentView.PdfPolicy + "` for PDF. No script runs and the page has an opaque "
        + "origin, so it cannot read the session or call this API.\n\n"
        + "Like the other read routes it serves a live team's folder, a gone team's, or a retired "
        + "one, named by the folder on disk." + Describe.Documents);

documents.MapPost("/folders", (
    [Description(Describe.Team)] string team,
    NewFolder request, TeamRegistry teams, TeamDocuments docs) =>
    teams.ExistingName(team) is not { } stored
        ? Results.NotFound(new { error = $"No team '{team}'." })
        : Documents(() => Results.Ok(docs.CreateFolder(stored, request.Path ?? ""))))
    .HumansOnly()
    .WithSummary("Create a folder")
    .WithDescription(
        "Creates a folder, and any missing folders above it." + Describe.Documents);

documents.MapPost("/upload", async (
    [Description(Describe.Team)] string team,
    HttpRequest request, TeamRegistry teams, TeamDocuments docs, FolderWatch folders,
    TenantLogging audit, HttpContext context, CancellationToken ct) =>
{
    if (teams.ExistingName(team) is not { } stored)
    {
        return Results.NotFound(new { error = $"No team '{team}'." });
    }

    if (!request.HasFormContentType) return Results.BadRequest(new { error = "Send a file." });

    var form = await request.ReadFormAsync(ct);
    var file = form.Files.GetFile("file");

    if (file is null || file.Length == 0) return Results.BadRequest(new { error = "Send a file." });

    if (file.Length > TeamDocuments.MaximumUploadBytes)
    {
        return Results.BadRequest(new
        {
            error = $"That file is larger than {TeamDocuments.MaximumUploadBytes / (1024 * 1024)} MB.",
        });
    }

    await using var content = file.OpenReadStream();

    return await DocumentsAsync(async () =>
    {
        var saved = await docs.SaveAsync(stored, form["path"], file.FileName, content, ct);

        // THE PLATFORM ANNOUNCES ITS OWN WRITE, at once and with no poll: it performed it.
        await folders.AnnounceAsync(
            stored,
            Path.GetDirectoryName(saved.Path)?.Replace('\\', '/') ?? "",
            [saved.Path],
            context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown",
            ct);

        // RECORDED LIKE EVERY OTHER PERSON'S WRITE: who put which file where, and how big it was.
        // Never the contents - this table is readable by every person and kept forever.
        await audit.WriteAsync(
            context, TenantActions.DocumentUploaded, stored, saved.Path,
            new { team = stored, path = saved.Path, size = saved.Size },
            ct);

        return Results.Ok(saved);
    });
})
    .HumansOnly()
    .WithSummary("Upload a document")
    .WithDescription(
        "A multipart form with the file in `file` and an optional destination folder in `path`.\n\n"
        + "Only the LEAF of the uploaded filename is kept, so a name carrying directory separators "
        + "cannot place the file anywhere but where `path` says. 400 for a missing file, an empty "
        + $"one, or one larger than {TeamDocuments.MaximumUploadBytes / (1024 * 1024)} MB. Audited as "
        + "`document.uploaded`, with the path and size and never the contents."
        + Describe.Documents);

documents.MapDelete("", async (
    [Description(Describe.Team)] string team,
    [Description(
        "The file or folder to delete, relative to the team's documents root. Omit it, for a team "
        + "that no longer exists or a retired folder, to delete the whole documents folder.")]
    string? path,
    [Description(
        "Delete a folder and everything in it. Without it a folder that still has something in it "
        + "is refused with 409.")]
    bool? recursive,
    TeamRegistry teams, TeamDocuments docs, FolderWatch folders, FolderRemoval removal, ITenantLog tenantLog,
    HttpContext context, CancellationToken ct) =>
{
    // RESOLVED LIKE THE READ ROUTES: a gone team's folder and a retired one can be cleared
    // out. A person may delete a record; upload and new folder still refuse to add to one.
    if (DocumentsFolderOf(teams, docs, team) is not { } stored)
    {
        return Results.NotFound(new { error = $"No team '{team}'." });
    }

    var live = !TeamPaths.IsRetiredDocumentsFolder(stored) && teams.ExistingName(stored) is not null;

    return await DocumentsAsync(async () =>
    {
        // PLANNED FIRST, so every refusal - outside the folder, the marker, not empty, a live
        // team's root, a whole folder the platform did not make - is answered before anything is
        // recorded or removed.
        var plan = docs.Plan(stored, path, recursive ?? false, wholeFolderAllowed: !live);

        // RECORDED BEFORE REMOVED, and not swallowed the way TenantLogging swallows: the delete
        // does not happen when its row cannot be written. A delete nobody can account for is the
        // one thing a record of deletes exists to rule out.
        try
        {
            await tenantLog.WriteAsync(
                context.User.FindFirstValue(ClaimTypes.NameIdentifier),
                context.User.FindFirstValue(ClaimTypes.Email),
                TenantActions.DocumentsDeleted,
                stored,
                plan.Path.Length == 0 ? stored : plan.Path,
                JsonSerializer.Serialize(new
                {
                    folder = stored,
                    path = plan.Path,
                    isFolder = plan.IsFolder,
                    files = plan.Files.Count,
                }),
                ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Results.Problem(
                "The delete could not be recorded, so nothing was deleted.",
                statusCode: StatusCodes.Status500InternalServerError);
        }

        // THROUGH FolderRemoval, never a recursive delete: an agent's owner-only folder is removed
        // as the agent where the Host switches users, links are never followed, and what cannot go
        // is named with why instead of escaping as an unhandled exception after the row above.
        var root = docs.RootFor(stored);
        var report = await removal.RemoveDocumentsAsync(root, plan.Absolute, TeamPaths.TeamOfDocumentsFolder(stored), ct);
        var removed = plan.Files.Where(file => !TeamDocuments.StillThere(Path.Combine(root, file))).ToList();

        // Announced after the delete, ONE ANNOUNCEMENT PER FOLDER a removed file was in, exactly
        // as a single-file delete is announced - and only the files that went. A gone team has no
        // triggers.
        if (live)
        {
            var actor = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown";

            foreach (var group in removed.GroupBy(
                file => Path.GetDirectoryName(file)?.Replace('\\', '/') ?? "", StringComparer.Ordinal))
            {
                await folders.AnnounceAsync(stored, group.Key, [.. group], actor, ct);
            }
        }

        if (report.Complete) return Results.NoContent();

        // WHAT WAS LEFT, AND WHY, as a row after the `documents.deleted` above - so the log never
        // claims a deletion that did not happen - and as the answer's sentence.
        var left = report.Refused is { } refused
            ? [new DocumentLeft(plan.Path.Length == 0 ? "" : plan.Path, refused)]
            : report.Remaining
                .Select(path => new DocumentLeft(
                    docs.Relative(root, path) is var relative && relative == "." ? "" : relative,
                    report.Reasons?.GetValueOrDefault(path) ?? "still there after the delete"))
                .ToList();
        var nothing = removed.Count == 0 && TeamDocuments.StillThere(plan.Absolute);
        var sentence =
            (nothing
                ? "Nothing was removed. "
                : $"The delete did not finish: {removed.Count} of {plan.Files.Count} file(s) were removed. ")
            + "Still there: "
            + string.Join("; ", left.Select(l => $"{(l.Path.Length == 0 ? $"the folder {stored}" : l.Path)} ({l.Reason})"))
            + ". Delete it again once that is fixed.";

        try
        {
            await tenantLog.WriteAsync(
                context.User.FindFirstValue(ClaimTypes.NameIdentifier),
                context.User.FindFirstValue(ClaimTypes.Email),
                TenantActions.DocumentsDeleteIncomplete,
                stored,
                plan.Path.Length == 0 ? stored : plan.Path,
                JsonSerializer.Serialize(new
                {
                    folder = stored,
                    path = plan.Path,
                    files = plan.Files.Count,
                    removed = removed.Count,
                    remaining = left.Select(l => new { path = l.Path, reason = l.Reason }),
                }),
                ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            sentence += " The record of what was left could not be written.";
        }

        return Results.Json(
            new { error = sentence, removed = removed.Count, remaining = left },
            statusCode: StatusCodes.Status409Conflict);
    });
})
    .HumansOnly()
    .WithSummary("Delete a document or folder")
    .WithDescription(
        "204 on success. A folder that still has something in it is refused with 409 unless "
        + "`recursive=true` - the one refusal here a person is expected to meet and act on.\n\n"
        + "A delete the Host cannot complete answers 409 with a sentence in `error` naming each path "
        + "left and why (permission denied, in use), `remaining` listing them and `removed` counting "
        + "the files that went; one that removed nothing says so. It is followed by a "
        + "`documents.delete-incomplete` tenant event naming the paths left, and deleting again once "
        + "they are removable finishes it.\n\n"
        + "Works for a team that no longer exists and for a retired folder, as the read routes do. "
        + "For those, and only those, omitting `path` deletes the whole documents folder, and only "
        + "when its marker says the platform created it. A live team's documents folder itself "
        + "cannot be deleted.\n\n"
        + "Every delete appends a `documents.deleted` tenant event naming the folder, the path, "
        + "whether it was a folder and the number of files removed; nothing is deleted when that "
        + "row cannot be written." + Describe.Documents);

/// Telling a container something is publishing an addressed instruction. There is no other way in,
/// which is what keeps a container event-driven rather than something with two doors.
///
/// TEAM-SCOPED, because a container is identified by (team, name) - an unqualified route could not
/// say which team's Manager it meant. It also puts the route under /api/teams/{team}/ with
/// everything else a team owns, so a per-team gate covers it BY PATH rather than needing a special
/// case inside this handler.
app.MapPost("/api/teams/{team}/containers/{name}/tell", async (
    [Description(Describe.Team)] string team,
    [Description(
        "The member's name within that team, matched case-insensitively. A team's manager is "
        + "called `Manager` unless it has been relabelled.")]
    string name,
    Tell request, TeamRegistry teams, ContainerHost host,
    IMessageLog log, IPendingDeliveries pending, IOutcomeStore outcomes, HttpContext context, CancellationToken ct) =>
{
    // The STORED spelling, so the id built here is the one the container was registered under
    // however the caller capitalised the team.
    if (teams.ExistingName(team) is not { } stored) return Results.NotFound(new { error = $"No team '{team}'." });

    // Checked BEFORE the id is constructed: ContainerId's constructor THROWS on an illegal name, and
    // an unhandled ArgumentException from a route segment is a 500 where a 404 is correct.
    if (!ContainerId.IsLegalName(name)) return Results.NotFound(new { error = $"No member '{name}'." });

    var id = new ContainerId(stored, name);

    // The FOUND container, captured rather than discarded, because the type below must be built from
    // ITS id and not from the caller's spelling. ContainerId equality is case-insensitive, so
    // `tell DEV1` finds a container named `dev1` - but SqliteMessageStore.ReadAfterAsync matches
    // `type IN (...)` under SQLite's default BINARY collation, which is not. Building the type from
    // `id` would append `agentContainer.instruction.Alpha/DEV1` against a subscription row reading
    // `agentContainer.instruction.Alpha/dev1`, so the call would return 200, write a row, and wake
    // nothing. A success reported for work that silently did not happen is the worst outcome available:
    // the person has no reason to look.
    if (host.Find(id) is not { } container) return Results.NotFound(new { error = $"No member '{name}'." });
    if (string.IsNullOrWhiteSpace(request.Instruction)) return Results.BadRequest(new { error = "Say something." });

    // WORK GOES THROUGH THE COORDINATOR, AND THE PLATFORM SAYS SO RATHER THAN A SKILL.
    // A skill can ask for this but cannot enforce it, so without this one ambiguous instruction
    // pasted into a Concierge would be enough to root a whole workflow at a member while the
    // Manager - idle, subscribed and able - knew nothing about it.
    //
    // AUTHORITY IS IDENTITY, NOT THE PERMIT, the shape `workflow.completed` already uses. The
    // permit still says a Concierge MAY tell; this bounds WHOM, exactly as the hiring allowlist
    // bounds what a manager's hire may RUN.
    //
    // A USER FALLS THROUGH, AND SO DOES A TICKET WITH NO READABLE KIND. A person addressing a
    // member directly to debug or unstick it is legitimate and is the one caller who can see the
    // whole board. An unreadable kind cannot reach here anyway - permits ride the same claims, so
    // `PermitGate` refused it already - and if that ever changes, this must be revisited rather
    // than left to the pattern below.
    //
    // ASKED THROUGH `host.Find`, THE SAME LOOKUP THAT FOUND THE TARGET FOUR LINES UP. Whether a
    // container is here has one answer; reading the registry's summaries for the Manager while
    // reading the host for the target is two, and they can disagree. It is also the safe direction:
    // a Manager the host does not hold cannot be told either, so refusing on its behalf would
    // refuse a dispatch that has no working alternative.
    if (PrincipalClaims.From(context.User)
        is { Kind: PrincipalKind.Concierge or PrincipalKind.Container } caller)
    {
        var manager = new ContainerId(stored, TeamRegistry.DefaultManagerName);

        // A Manager telling its own members is the whole point of the platform, so it is excluded
        // by identity rather than by kind. Short-circuited: only a Container has a parseable
        // ContainerId, and a Concierge's id is not one.
        var callerIsManager = caller.Kind == PrincipalKind.Container
            && ContainerId.Parse(caller.Id).Equals(manager);

        // A team with NO Manager is excluded first: the concierge skill documents that shape and
        // tells a Concierge to sequence the job itself, and only a hand-built team produces one.
        if (host.Find(manager) is not null && !container.Id.Equals(manager) && !callerIsManager)
        {
            return Results.Json(
                new { error = TellRefusals.SkipsManager },
                statusCode: StatusCodes.Status403Forbidden);
        }
    }

    // The AUTHENTICATED principal, never `request.From`. That field is caller-supplied and
    // unvalidated, so it could name any container on any team - including one the caller holds no
    // authority over - which is exactly why MessageTeam.Of reads an instruction's TYPE for its team and
    // never its Source.
    //
    // That rule STAYS even though this line makes it less load-bearing: a future caller-supplied
    // path would reopen the hole in silence, and the rule is the only thing written down about it.
    var from = PrincipalClaims.From(context.User)?.Id ?? "console";

    // THE HOP'S OWN THREAD. Correlation is inherited from the causing message at append time, so
    // this one value is what makes a manager's dispatch part of the workflow it is answering rather
    // than the head of a new one. Without it every dispatch would start a fresh correlation and
    // "one correlation id on both sides of the hop" would not be something any amount of care by the
    // agent could achieve - each tool call is a fresh request from a child process.
    //
    // Blank is absent, not zero: an unset HARNESS_CAUSATION reaches the tool as null and a cleared
    // one as "", and the two mean the same thing here.
    long? causation = null;

    // The workflow this instruction will join, known only when it answers something: an
    // instruction with no causation heads a new workflow, where nothing can already be queued.
    long? joins = null;

    // A team container that names no causation dispatches inside the run it is in. See
    // `TellCausation`; the depth and budget checks below then apply to it like any other.
    var callerPrincipal = PrincipalClaims.From(context.User);
    var requestedCausation = TellCausation.Resolve(
        request.Causation,
        callerPrincipal?.Kind,
        callerPrincipal is { Kind: PrincipalKind.Container } machine
            && host.Find(ContainerId.Parse(machine.Id)) is { } callerContainer
                ? callerContainer.CurrentCausation
                : null);

    if (!string.IsNullOrWhiteSpace(requestedCausation))
    {
        // Refused rather than ignored. Silently dropping an unparseable value would put the
        // instruction at the head of its own workflow - and it would look identical to it
        // working.
        if (!long.TryParse(
                requestedCausation, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seq))
        {
            return Results.BadRequest(new { error = $"'{requestedCausation}' is not a message seq." });
        }

        // The message must EXIST, and this is a real check rather than belt and braces:
        // `messages.causation_seq` is a foreign key, so an unknown seq would otherwise surface as a
        // constraint violation out of the append - a 500 for what is a caller error.
        if (await log.FindAsync(seq, ct) is not { } found)
        {
            return Results.BadRequest(new { error = $"No message #{seq} to answer." });
        }

        // THE LOOP BOUND. The causing message's depth plus one is what this instruction would be
        // stored at, so the check is on the message about to be written rather than on its parent.
        if (tenantSettings.CausationTooDeep(found.Depth + 1))
        {
            // 409 rather than 400: nothing about the request is malformed, and the caller is not
            // going to fix it by sending different JSON. The body is written to be READ BY AN
            // AGENT, because it is - the tool hands the refusal back verbatim - so it says
            // what to do instead rather than only what went wrong.
            return Results.Conflict(new
            {
                error =
                    $"This job is already {found.Depth + 1} steps deep, which is the limit. "
                    + "Something is going round in circles. Stop dispatching and report what you "
                    + "have to whoever asked you.",
            });
        }

        // THE SPEND BOUND. Check if this workflow's cumulative spend exceeds the limit.
        // Unmeasured spend never convicts: if all runs in this workflow have no usage recorded,
        // the check cannot refuse, even if the limit is 0. Partial measurement bounds only the
        // measured part.
        // SINCE THE LAST NUDGE, matching what the wake asks. Two bounds reading two different
        // figures would refuse a dispatch the pump would have allowed, or the reverse.
        //
        // AND THROUGH THE SAME RESOLVER THE WAKE USES. Reading the raw instance figure here would
        // ignore the team's own per-workflow budget; a team whose figure is LOWER than the
        // instance one would then have a dispatch accepted here and refused at the wake -
        // precisely the disagreement the paragraph above warns about, only from the other side.
        // `null` is UNLIMITED and is the only spelling of it that reaches here.
        var effectiveBudget = teams.EffectiveWorkflowBudgetFor(stored);
        var spend = await log.GetSpendSinceNudgeAsync(found.CorrelationId, ct);

        if (effectiveBudget is { } bound
            && spend.RunsWithMeasuredUsage > 0 && spend.TokensSpent > bound)
        {
            return Results.Conflict(new
            {
                error =
                    $"This workflow has spent {spend.TokensSpent:N0} tokens, which exceeds the limit of {bound:N0}. "
                    + "Something is going too far. Stop dispatching and report what you "
                    + "have to whoever asked you.",
            });
        }

        causation = seq;
        joins = found.CorrelationId;
    }

    // A DUPLICATE IS NAMED, NOT HELD. A Manager that re-sends an instruction already queued to a
    // busy member when its predecessor is accepted pays a member run and a Manager wake to hear it
    // repeated. The reply names the queued seq so the sender learns that - but the row is
    // still appended below, because `tell` is never held: text that matches is not proof the sender
    // meant the same thing, and a refusal the sender misreads loses work silently.
    var duplicate = joins is { } correlation
        ? await QueuedInstructions.DuplicateOfAsync(pending, log, container.Id, correlation, request.Instruction, ct)
        : null;

    // THE OUTCOME OF A NEW WORKFLOW, named at its root: an id, or the exact name of a live outcome.
    // An instruction that joins a workflow has none to name - that workflow's outcome is its own.
    Outcome? rootOutcome = null;
    if (!string.IsNullOrWhiteSpace(request.Outcome))
    {
        if (causation is not null)
        {
            return Results.BadRequest(new
            {
                error = $"`outcome` names the outcome of a NEW workflow, and this instruction joins workflow {joins}. "
                    + "Set that workflow's outcome with the `outcome` tool instead.",
            });
        }

        rootOutcome = await outcomes.ResolveLiveAsync(request.Outcome, ct);
        if (rootOutcome is null)
        {
            return Results.BadRequest(new { error = TriggerCreation.OutcomeRefusal(request.Outcome.Trim()) });
        }
    }

    // Three fields, and `instruction` is the one that must never leave.
    //
    // It is what every reader reads, and the log is APPEND-ONLY and REPLAYED: older rows may carry
    // it alone, so a projection meets a mixed history and needs both shapes anyway. Writing the split as well only means the board and the browser do
    // not have to re-derive it row by row - it does not make `instruction` redundant, and dropping
    // it would silently rewrite a history nothing can rewrite.
    //
    // VERBATIM and COMPLETE, not the trimmed or split text: the agent is handed this field, and an
    // agent told less than the person wrote fails in the way nothing can see.
    var parts = InstructionText.Split(request.Instruction, request.Subject);

    var instructionRow = new NewMessage(
            MessageTypes.InstructionFor(container.Id),
            JsonSerializer.Serialize(new
            {
                instruction = request.Instruction,
                subject = parts.Subject,
                body = parts.Body,

                // ABSENT, NOT NULL, when no card is claimed. The projector asks whether the field
                // is a non-empty string, so a null would behave the same - but every row on this
                // append-only log is replayed forever, and writing `"card":null` onto every
                // instruction ever sent would put a field nobody reads on the whole history.
                card = string.IsNullOrWhiteSpace(request.Card) ? null : request.Card.Trim(),
            }),
            from,
            causation);

    // THE ROOT, ITS LINK AND ITS TENANT ROW IN ONE TRANSACTION (`how: tell`): a person's own tell
    // is theirs, and the Concierge's is an agent's, which a Manager may later move. The
    // `workflow.outcome-changed` row names the person, or the member for an agent's tell; when it
    // cannot be written, neither the instruction nor the link lands.
    var tellerIsPerson = callerPrincipal?.Kind == PrincipalKind.User;
    var message = rootOutcome is null
        ? await log.AppendAsync(instructionRow, ct)
        : (await log.AppendWithinAsync(
            (_, _, _) => Task.FromResult<NewMessage?>(instructionRow),
            async (connection, transaction, stored, token) =>
            {
                var sqlite = (Microsoft.Data.Sqlite.SqliteConnection)connection;
                var within = (Microsoft.Data.Sqlite.SqliteTransaction)transaction;

                await OutcomeLinks.WriteAsync(
                    sqlite, within, stored.CorrelationId, rootOutcome.Id, container.Id.Team,
                    tellerIsPerson ? context.User.FindFirstValue(ClaimTypes.Email) ?? from : from,
                    tellerIsPerson ? OutcomeActorKind.Person : OutcomeActorKind.Member,
                    OutcomeLinkHow.Tell,
                    token);

                var detail = new { team = container.Id.Team, workflow = stored.CorrelationId, how = OutcomeLinkHow.Tell };
                await TenantAuditRow.AppendAsync(sqlite, within,
                    tellerIsPerson
                        ? TenantLogging.Row(context, TenantActions.WorkflowOutcomeChanged, rootOutcome.Id, rootOutcome.Name, detail)
                        : new TriggerAudit(from, null, TenantActions.WorkflowOutcomeChanged, rootOutcome.Id, rootOutcome.Name,
                            JsonSerializer.Serialize(detail)),
                    token);
            },
            ct))!;
    var paused = teams.IsPaused(stored);
    return Results.Ok(new
    {
        message.Seq,
        message.CorrelationId,
        paused,
        pauseNotice = paused ? PauseNoticeFor(teams.LabelFor(stored)) : null,
        duplicateOf = duplicate?.Seq,
        duplicateNotice = duplicate is null ? null : QueuedInstructions.DuplicateNotice(container.Id.Name, duplicate.Seq),
    });
})
    .WithTags("Members")
    .RequirePermit(Permits.Tell)
    .WithSummary("Tell a member to do something")
    .WithDescription(
        "The only way into a member, which is what keeps it event-driven rather than something with "
        + "two doors. It appends an addressed instruction to the message log; the member's "
        + "container is woken by the delivery pump and enqueues an invocation of its agent.\n\n"
        + "When the team is paused the row is still appended and the 200 says so, because queued "
        + "work that does not run yet must not read as though it already had.\n\n"
        + "**Nothing blocks and there is no `wait`.** The 200 means the instruction was recorded, "
        + "never that the work is done - by design, since a caller that could wait on a worker is a "
        + "caller that will. Follow the returned `correlationId` through `GET "
        + "/api/workflows/{correlationId}` to see what happened; every message the instruction goes "
        + "on to cause carries it.\n\n"
        + "When the text matches an instruction already queued to this member in the same workflow, "
        + "`duplicateOf` names that queued seq and `duplicateNotice` says so in a sentence. The "
        + "instruction is appended all the same: a match is reported, never held.\n\n"
        + "400 for an empty instruction; 404 for an unknown team or member.");

// WHAT IS WAITING FOR EACH MEMBER: accepted, not started. `status` shows it so a Manager does not
// re-send what a member already has - see QueuedInstructions.
app.MapGet("/api/teams/{team}/queued", async (
    [Description(Describe.Team)] string team,
    [Description("Omit for every member. The member's identifier, matched case-insensitively.")] string? member,
    TeamRegistry teams, IMessageLog log, IPendingDeliveries pending, CancellationToken ct) =>
{
    if (teams.ExistingName(team) is not { } stored) return Results.NotFound(new { error = $"No team '{team}'." });

    var queued = await QueuedInstructions.ForTeamAsync(
        pending, log, stored, string.IsNullOrWhiteSpace(member) ? null : member.Trim(), ct);

    return Results.Ok(new { queued });
})
    .WithTags("Members")
    .RequirePermit(Permits.Read)
    .WithSummary("Each member's queued and deferred instructions")
    .WithDescription(
        "Every delivery a member of this team has accepted and not yet started, oldest first within "
        + "each member: its `seq`, the first `line` of the instruction, its `source` and the workflow "
        + "(`correlation`) it belongs to. `state` is `queued`, or `deferred` for an item a batched run "
        + "put back on the queue, with `deferredFromRun` naming that run. What a member is running "
        + "now is not listed: every item of a running batch has started.\n\n"
        + "404 for an unknown team.");

// What a member says about ITSELF while it works. The one route a permit-less-by-default member
// can reach, and the reason every member gets a credential at all.
//
// It carries {team} deliberately, even though the server derives everything it trusts from the
// authenticated principal: that declaration is what puts this inside TeamGate structurally rather
// than by anyone remembering. TeamGate's own comment is explicit that a route naming its team any
// other way is ungated, silently.
app.MapPost("/api/teams/{team}/containers/{name}/progress", async (
    [Description(Describe.Team)] string team,
    [Description("The member reporting. Must be the caller itself.")] string name,
    ReportProgress request,
    HttpContext context, TeamRegistry teams, ContainerHost host, IMemberReports reports,
    CancellationToken ct) =>
{
    if (teams.ExistingName(team) is not { } stored)
    {
        return Results.NotFound(new { error = $"No team '{team}'." });
    }

    var id = new ContainerId(stored, name);

    // The FOUND container's id, never the caller's spelling - a message type is matched under
    // SQLite's BINARY collation while ContainerId equality folds case, so building a type from what
    // was typed is how a call answers 200 and nothing ever happens.
    if (host.Find(id) is not { } container)
    {
        return Results.NotFound(new { error = $"No member '{name}'." });
    }

    // THE SPOOF CHECK, and the permit does not cover it: every member holds Progress, and a
    // container narrating another container's work would appear on that container's card with
    // nothing to say where it came from. Compared under ContainerId equality, which folds case on
    // both halves, so a member is not refused for capitalising its own name differently.
    if (PrincipalClaims.From(context.User) is not { Kind: PrincipalKind.Container } caller
        || !ContainerId.Parse(caller.Id).Equals(container.Id))
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    if (string.IsNullOrWhiteSpace(request.Status))
    {
        // Refused rather than recorded. A blank line on a card is indistinguishable from "it has
        // not said", which is the exact ambiguity progress reporting exists to remove.
        return Results.BadRequest(new { error = "Say what you are doing." });
    }

    // The seq of the message being run. Correlation is inherited from causation at append time,
    // so this one value puts the report inside the workflow that produced it; passing the
    // correlation instead would head a new one.
    // THE EFFECTS LIVE IN MemberReports, which a plugin member's stdout reaches too: the row
    // (flagged `whileIdle` from a process that outlived its run), the card push, the idle clock.
    await reports.ProgressAsync(container.Id, request.Status, ct);

    return Results.NoContent();
})
    .WithTags("Members")
    .RequirePermit(Permits.Progress)
    .WithSummary("Say what you are doing")
    .WithDescription(
        "A member narrating its own run, so the board can say more than `running`. Each line joins "
        + "the card's activity feed and stays there; nothing replaces anything. "
        + "**Only about yourself.** The caller must BE the member it names; anything else is 403. "
        + "Every member holds this permit, so the identity check rather than the permit is what "
        + "stops one member narrating another's work. "
        + "400 for an empty status; 404 for an unknown team or member.");

// The other half of what a member may say about itself: that it has STOPPED without finishing.
//
// Everything structural here is the progress route's, on purpose. It carries {team} so TeamGate
// covers it by declaration rather than by memory; it builds the type from the FOUND container's id,
// because a type is matched under BINARY collation while ContainerId equality folds case; and it
// asserts the caller IS the container it names, because the permit cannot - every member holds
// Progress.
//
// It reuses that permit rather than minting a Blocked one. The permit bounds "may this container
// narrate its own state on its own card", and both verbs are exactly that. A new permit would need
// every existing member's `permits` column migrated, and a member that could not say it had given
// up would fail SILENTLY - this feature failing in the precise way it exists to prevent.
// STOP THIS RUN. The only way to end a run that has gone wrong without deleting the
// member, which would also destroy a workspace and a row.
//
// HumansOnly, deliberately. A manager holding this could stop its colleagues' work, and the blast
// radius of a mistake there is somebody else's half-finished job; a person clicking a button on a
// card they are looking at is the case this exists for. It is inside TeamGate by declaring {team},
// so the person must hold the team as well.
//
// It is NOT idempotent-by-pretending: an idle container answers 200 saying nothing was running,
// because by the time a click arrives the run may have finished on its own and that is not an error.
app.MapPost("/api/teams/{team}/containers/{name}/stop", async (
    [Description(Describe.Team)] string team,
    [Description("The member whose current run should be ended.")] string name,
    TeamRegistry teams, ContainerHost host, CancellationToken ct) =>
{
    if (teams.ExistingName(team) is not { } stored)
    {
        return Results.NotFound(new { error = $"No team '{team}'." });
    }

    // The FOUND container, never the caller's spelling - the same rule every other route on this
    // path follows, and for the same reason.
    if (host.Find(new ContainerId(stored, name)) is not { } container)
    {
        return Results.NotFound(new { error = $"No member '{name}'." });
    }

    var stopped = container.Stop();

    await Task.CompletedTask;

    return Results.Ok(new
    {
        stopped,
        message = stopped
            ? $"{container.Id.Name} was stopped."
            : $"{container.Id.Name} was not running.",
    });
})
    .WithTags("Members")
    .HumansOnly()
    .WithSummary("Stop the run in flight")
    .WithDescription(
        "Ends the invocation this member is currently running and leaves the member able to take "
        + "its next instruction. The run is reported as `agentContainer.failed`, saying a person stopped "
        + "it - which is deliberately different wording from a timeout or a Host shutdown, because "
        + "the three are fixed in different places.\n\n"
        + "**Not a delete.** Removing the member also ends a run, and destroys a workspace and a row "
        + "with it.\n\n"
        + "`stopped` is false when nothing was running. That is a 200, not an error: by the time a "
        + "click reaches the server the run it was aimed at may have finished on its own.")
    .Produces<object>();

app.MapPost("/api/teams/{team}/containers/{name}/blocked", async (
    [Description(Describe.Team)] string team,
    [Description("The member reporting. Must be the caller itself.")] string name,
    ReportBlocked request,
    HttpContext context, TeamRegistry teams, ContainerHost host, IMemberReports reports,
    CancellationToken ct) =>
{
    if (teams.ExistingName(team) is not { } stored)
    {
        return Results.NotFound(new { error = $"No team '{team}'." });
    }

    var id = new ContainerId(stored, name);

    if (host.Find(id) is not { } container)
    {
        return Results.NotFound(new { error = $"No member '{name}'." });
    }

    if (PrincipalClaims.From(context.User) is not { Kind: PrincipalKind.Container } caller
        || !ContainerId.Parse(caller.Id).Equals(container.Id))
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    if (string.IsNullOrWhiteSpace(request.Reason))
    {
        // "Stopped, and here is nothing" is the least useful thing a card can say: a reader has to
        // go to the log anyway, which is the trip this whole route removes.
        return Results.BadRequest(new { error = "Say why you stopped." });
    }

    // A DEFERRAL NAMES ITS ITEM: deferring "the run" means nothing.
    if (request.Defer == true && request.Item is null)
    {
        return Results.BadRequest(new { error = "Name the item to defer: its number in this run's prompt." });
    }

    // THE EFFECTS LIVE IN MemberReports: one batch item closed by its own row, one deferred to its
    // own next run, or the whole run marked. An `item` that is not in this run is refused there, for
    // every caller, and so is deferring the only item of a run.
    var outcome = request.Defer == true
        ? await reports.DeferAsync(container.Id, request.Item!.Value, request.Reason, ct)
        : await reports.BlockedAsync(container.Id, request.Reason, request.Item, ct);

    if (!outcome.Accepted) return Results.BadRequest(new { error = outcome.Refusal });

    return Results.NoContent();
})
    .WithTags("Members")
    .RequirePermit(Permits.Progress)
    .WithSummary("Say you have stopped without finishing")
    .WithDescription(
        "A member reporting that it is giving up on the work it was given, and why. Distinct from "
        + "`agentContainer.failed`, which is the PLATFORM reporting a run that did not complete: this is "
        + "a run that finished normally and produced no result. Without it, a member that abandons "
        + "a job and one that delivers are both idle with exit code 0. `item` is optional and, when "
        + "present, names one batched prompt item (1-based) to abandon instead of the whole run. "
        + "`defer` true with an `item` DEFERS that item instead: it is not closed when this run ends "
        + "but delivered again as its own next run, in the same workflow, and the run's terminal rows "
        + "list it as `deferred` with the reason. Deferring the only item of a run is refused.\n\n"
        + "The reason stays on the card until something wakes this member again. "
        + "**Only about yourself.** The caller must BE the member it names; anything else is 403. "
        + "Every member holds this permit, so the identity check rather than the permit is what "
        + "stops one member marking another. "
        + "400 for an empty reason, an invalid `item`, or a deferral that is refused; 404 for an "
        + "unknown team or member.");

// THE HAND-BACK. A worker saying its own part is done and nothing is owed.
//
// MODELLED ON `blocked` ABOVE AND AUTHORISED THE SAME WAY - the `Progress` permit every member
// already holds, with the identity check rather than the permit stopping one member speaking for
// another. That precedent is deliberate: a permit nobody's column carries is a
// verb nobody can reach, and this one has to be reachable by every worker on its worst day.
//
// IT IS NOT `workflow-complete` AND DOES NOT BECOME IT. That route is a MANAGER'S declaration,
// asserted on identity, refused while the team is still working, and nothing here touches it - a
// member that hands back is still refused a declaration by exactly the same words. This hands work
// over; only a Manager ends a workflow.
//
// WHAT IS NEW IS THAT SOMETHING IS SUBSCRIBED TO THE ROW. `blocked` and `progress` wake nobody;
// `agentContainer.handback` is in `TeamRegistry.ManagerSubscriptions`, so the append below wakes
// this team's Manager through the ordinary pump - on the SAME correlation, because the row is
// caused by `container.CurrentCausation` like every other thing this run publishes. Both of the
// pump's guards still do their work unchanged: a Manager that hands back does not wake itself (a
// container never reacts to its own publications) and no other team hears this at all (a container
// is woken only by its own team).
app.MapPost("/api/teams/{team}/containers/{name}/handback", async (
    [Description(Describe.Team)] string team,
    [Description("The member handing back. Must be the caller itself.")] string name,
    ReportHandback request,
    HttpContext context, TeamRegistry teams, ContainerHost host, IMemberReports reports,
    CancellationToken ct) =>
{
    if (teams.ExistingName(team) is not { } stored)
    {
        return Results.NotFound(new { error = $"No team '{team}'." });
    }

    var id = new ContainerId(stored, name);

    if (host.Find(id) is not { } container)
    {
        return Results.NotFound(new { error = $"No member '{name}'." });
    }

    // Identity, not permit - `blocked`'s rule exactly. Every member holds `Progress`, so what stops
    // one member handing back on another's behalf is that the caller must BE the member it names.
    if (PrincipalClaims.From(context.User) is not { Kind: PrincipalKind.Container } caller
        || !ContainerId.Parse(caller.Id).Equals(container.Id))
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    if (string.IsNullOrWhiteSpace(request.Delivered))
    {
        // "Done, and here is nothing" is the hand-back's version of the empty blocked reason, and
        // it is worse: this row WAKES a manager, which then has to go to the log to find out what
        // it was woken for. Refused for the reason `workflow-complete` refuses a blank `delivered`.
        return Results.BadRequest(new { error = "Say what you finished." });
    }

    // THE EFFECTS LIVE IN MemberReports: the row that wakes the Manager, and the last-handback
    // words that are never cleared.
    await reports.HandbackAsync(container.Id, request.Delivered, ct);

    return Results.NoContent();
})
    .WithTags("Members")
    .RequirePermit(Permits.Progress)
    .WithSummary("Hand your finished part back")
    .WithDescription(
        "A member reporting that the work it was given is DONE and nothing is owed. A success, and "
        + "the counterpart of `blocked`: that one says the agent gave up, and a member that uses it "
        + "on finished work teaches its manager the opposite of the truth and marks its card as a "
        + "team in trouble.\n\n"
        + "**It does not end the workflow.** `workflow-complete` is a Manager's declaration and "
        + "stays refused to a worker, unchanged. This hands the work over: it wakes this team's "
        + "Manager on the same workflow, which then decides what to declare.\n\n"
        + "The words are kept as this member's last hand-back and, unlike a blocked reason, are NOT "
        + "cleared when it is next woken - a delivery does not go stale.\n\n"
        + "**Only about yourself.** The caller must BE the member it names; anything else is 403. "
        + "Every member holds this permit, so the identity check rather than the permit is what "
        + "stops one member handing back for another. "
        + "400 for empty words; 404 for an unknown team or member.");

app.MapPost("/api/teams/{team}/containers/{name}/needs-decision", async (
    [Description(Describe.Team)] string team,
    [Description("The member reporting. Must be the caller itself.")] string name,
    ReportNeedsDecision request,
    HttpContext context, TeamRegistry teams, ContainerHost host, IMemberReports reports,
    CancellationToken ct) =>
{
    if (teams.ExistingName(team) is not { } stored)
    {
        return Results.NotFound(new { error = $"No team '{team}'." });
    }

    var id = new ContainerId(stored, name);

    if (host.Find(id) is not { } container)
    {
        return Results.NotFound(new { error = $"No member '{name}'." });
    }

    if (PrincipalClaims.From(context.User) is not { Kind: PrincipalKind.Container } caller
        || !ContainerId.Parse(caller.Id).Equals(container.Id))
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    if (string.IsNullOrWhiteSpace(request.Question))
    {
        return Results.BadRequest(new { error = "Say what decision you need." });
    }

    // THE EFFECTS LIVE IN MemberReports: the row and the needs-decision mark.
    await reports.NeedsDecisionAsync(container.Id, request.Question, ct);

    return Results.NoContent();
})
    .WithTags("Members")
    .RequirePermit(Permits.Progress)
    .WithSummary("Say you are waiting for a decision")
    .WithDescription(
        "A member reporting that it has paused on purpose to ask a question before continuing. "
        + "This is not `agentContainer.completed`: the step is not done. It is also not "
        + "`agentContainer.blocked`: the agent has not given up. The question stays on the card until "
        + "something wakes this member again.\n\n"
        + "**Only about yourself.** The caller must BE the member it names; anything else is 403. "
        + "Every member holds this permit, so the identity check rather than the permit is what "
        + "stops one member posting as another. 400 for an empty question; 404 for an unknown team "
        + "or member.");

// A declaration that this workflow is done, by the container the workflow's root instruction
// addressed - the Manager when the root was not an addressed instruction (a schedule firing, a
// kanban card event).
//
// Same structural shape as progress/blocked: {team} in the route so TeamGate covers it by
// declaration, a type named by the server, and correlation stamped from the running container's
// current cause rather than from an id a caller can supply.
app.MapPost("/api/teams/{team}/containers/{name}/workflow-complete", async (
    [Description(Describe.Team)] string team,
    [Description(
        "The member declaring completion. Must be the container the workflow's root instruction "
        + "addressed, or this team's Manager when nothing addressed it or when one of its members "
        + "was addressed.")]
    string name,
    ReportWorkflowCompleted request,
    HttpContext context, TeamRegistry teams, ContainerHost host, IMessageLog log,
    IPendingDeliveries pending, IBacklogStore backlog, ITeamPublisher publisher,
    KanbanStore kanban, WorktreeRemoval worktrees, TeamPaths paths, ILoggerFactory loggers,
    SolutionNotice solutionNotice, OutcomeGate outcomeGate,
    CancellationToken ct) =>
{
    if (teams.ExistingName(team) is not { } stored)
    {
        return Results.NotFound(new { error = $"No team '{team}'." });
    }

    var id = new ContainerId(stored, name);

    if (host.Find(id) is not { } container)
    {
        return Results.NotFound(new { error = $"No member '{name}'." });
    }

    // Identity, not permit. A member claiming to be somebody else is refused by this check rather
    // than believed.
    if (PrincipalClaims.From(context.User) is not { Kind: PrincipalKind.Container } caller
        || !ContainerId.Parse(caller.Id).Equals(container.Id))
    {
        return Results.Json(
            new { error = "A member may only declare its own workflows completed." },
            statusCode: StatusCodes.Status403Forbidden);
    }

    // The ROOT's seq, not the seq that woke THIS container: a follow-up inside an existing
    // workflow is caused by a different row entirely, so reading CurrentCausation here would look
    // up the wrong message and derive the wrong owner, or none.
    var correlation = container.Snapshot().CurrentCorrelation ?? 0;

    // A CONTAINER THAT IS NOT RUNNING A WORKFLOW HAS NO WORKFLOW TO DECLARE.
    //
    // `CurrentCorrelation` is null between invocations, and no message on the log carries correlation
    // 0 - a root settles to its own seq - so a zero here makes every arm of the busy check match
    // nothing and turns the refusal into an unconditional 204. It would also append a row stamped from
    // a null causation, which becomes its OWN root: a `workflow.completed` belonging to a workflow of
    // one message, closing nothing, on the log forever.
    //
    // Refused rather than defaulted, which is this codebase's standing direction for an absent value:
    // null means nobody has chosen, and a guess is refused.
    if (correlation == 0)
    {
        return Results.Conflict(new
        {
            error = "This member is not currently running a workflow, so there is nothing to "
                + "declare complete. Call this from inside the run that was given the work, once "
                + "that invocation has woken with the instruction to close out.",
        });
    }

    // WHOSE WORKFLOW IS THIS. A member holds several at once now, and a workflow addressed to a
    // member is that member's to close - the Manager keeps the right for the workflows it was asked
    // to do, so nothing about today's shapes changes.
    //
    // Null means the root was not an addressed instruction - a schedule, a card event - and the
    // Manager owns it. Falling back rather than refusing is deliberate: a workflow nobody may
    // declare complete is a team that reads UNDECLARED forever.
    var manager = new ContainerId(stored, TeamRegistry.DefaultManagerName);
    var owner = await WorkflowOwner.OfAsync(log, correlation, ct) ?? manager;

    // THE MANAGER MAY DECLARE A WORKFLOW ONE OF ITS OWN MEMBERS OWNS, on the owner's behalf, and on
    // the same rule the owner declares by: the owner and every other member idle in it, nothing
    // queued in it but the Manager's own delivery (the busy check below, with the Manager as the
    // caller). Without it a Manager woken by a member-owned workflow could only tell the owner to
    // declare, and each refusal woke the other again. Any other member is refused, as before.
    var onBehalf = !container.Id.Equals(owner)
        && container.Id.Equals(manager)
        && string.Equals(owner.Team, stored, StringComparison.OrdinalIgnoreCase)
        && host.Find(owner) is not null;

    if (!container.Id.Equals(owner) && !onBehalf)
    {
        return Results.Json(
            new
            {
                error = $"This workflow was addressed to '{owner.Name}', "
                    + "so only that member, or this team's Manager on its behalf, may declare it completed.",
            },
            statusCode: StatusCodes.Status403Forbidden);
    }

    if (string.IsNullOrWhiteSpace(request.Delivered))
    {
        return Results.BadRequest(new { error = "Say what was delivered." });
    }

    // A WORKFLOW ALREADY DECLARED OR CLOSED IS NOT DECLARED AGAIN. With two members able to declare
    // one workflow, the second would otherwise append a second `workflow.completed` to it.
    if (!(await log.OpenWorkflowsAmongAsync([correlation], ct)).Contains(correlation))
    {
        return Results.Conflict(new
        {
            error = "This workflow is already declared complete or closed, so there is nothing "
                + "to declare. End your turn; do not retry.",
        });
    }

    // THE OUTCOME GATE, off by default: when a person turns it on, an agent does not declare a
    // workflow that serves no outcome. Read through its delegate on every declaration. A person's
    // close and the platform's own declarations never come through here, so it never gates them.
    if (await outcomeGate.RefusalAsync(correlation, ct) is { } noOutcome)
    {
        return Results.Conflict(new { error = noOutcome });
    }

    // THE OWNER OF A MEMBER-OWNED WORKFLOW IS NOT REFUSED FOR A MANAGER THAT IS ONLY WATCHING -
    // woken by a row the owner or another member wrote here. See `WorkflowBusyState`'s `watcher`.
    // A Manager told to do work here still counts, and every member's run still counts.
    var busy = await WorkflowBusyState.DescribeAsync(
        stored, correlation, host, pending, log, container.Id, ct,
        watcher: container.Id.Equals(manager) ? null : manager);

    if (busy.Count != 0)
    {
        return Results.Conflict(new
        {
            error = "Workflow completion refused because work is still in flight under this "
                + "workflow: " + string.Join("; ", busy) + ".",
        });
    }

    // LOOSE ENDS. Declaring the workflow moves every one of its cards to Done, so a card still in
    // To Do, interrupted, failed or blocked would read as delivered. Refused until each is finished
    // or the declaration says, in `dropped`, why it is being left - which goes on the record.
    var looseEnds = await WorkflowLooseEnds.DescribeAsync(kanban, stored, correlation, container.Id.Name);
    var dropped = request.Dropped?.Trim();

    if (looseEnds.Count != 0 && string.IsNullOrEmpty(dropped))
    {
        return Results.Conflict(new
        {
            error = "Workflow completion refused because these cards are not finished: "
                + string.Join("; ", looseEnds) + ". Finish or re-send each one, or declare again "
                + "with `dropped` saying why it is being left undone.",
        });
    }

    // THE WORK IS PUT ON ORIGIN BEFORE THE ROW THAT ACCEPTS IT, AND THE ORDER IS THE GUARANTEE.
    //
    // The append below is what drives a card to `done` - see `KanbanProjector.HandleWorkflowCompleted` -
    // so THAT ROW IS THE ACCEPTANCE. Published after it, this would be an optimisation nobody could
    // rely on; published before it, a card cannot read `done` without the branch having reached
    // origin first. Left to agents, a backlog item can read `implemented` while its work exists
    // only under a team root.
    //
    // IT CANNOT REFUSE AND IS NOT CHECKED. A team with no repository completes normally - the report
    // for one is simply empty - and a push that FAILED is its own row, `repo.pushFailed`, written by
    // the publisher a moment ago and deliberately not folded into this declaration's success or
    // failure: an unreachable origin is transport, not this member's fault, and blaming a run for it
    // is how a durability rule gets switched off. That is also why nothing here reads
    // `TeamPublishReport`: a branch on this result would be a refusal wearing a different name, and
    // refusing the acceptance is deliberately not done - a member out of budget cannot satisfy a
    // refusal.
    //
    // THE SAME CAUSATION AS THE DECLARATION, so the push rows land INSIDE the workflow being
    // accepted rather than rooting one of their own.
    await publisher.PublishAsync(
        stored, teams.ReposFor(stored), container.Id, container.CurrentCausation, ct);

    var declared = new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["delivered"] = request.Delivered.Trim(),
        ["dropped"] = looseEnds.Count == 0 ? null : dropped,
        ["looseEnds"] = looseEnds.Count == 0 ? null : looseEnds,
    };

    // WHO DECLARED IT, when that is not the owner: the Manager, on the owner's behalf.
    if (onBehalf)
    {
        declared[WorkflowDeclaration.DeclaredByField] = container.Id.ToString();
        declared[WorkflowDeclaration.OnBehalfOfField] = owner.ToString();
    }

    // THE ROW, ITS BACKLOG ITEM AND ITS SETTLED TREES: one sequence, shared with the platform's
    // declaration for a member that cannot declare (`UndeclarableWorkflows`).
    await WorkflowDeclaration.AppendAsync(
        stored, correlation, container.Id, container.CurrentCausation,
        JsonSerializer.Serialize(declared),
        log, backlog, host, kanban, worktrees, paths, teams.ReposFor(stored),
        loggers.CreateLogger("WorktreeRemoval"), ct, solutionNotice);

    return Results.NoContent();
})
    .WithTags("Members")
    .RequirePermit(Permits.Progress)
    .WithSummary("Declare a workflow completed")
    .WithDescription(
        "A declaration that a workflow has finished and what it delivered. The caller must be the "
        + "container the workflow's root instruction addressed - this team's Manager when the root "
        + "was not an addressed instruction (a schedule firing, a kanban card event). Correlation is "
        + "stamped from the waking message by the container; the caller never supplies one. The "
        + "declaration is refused while a member is Running or has pending deliveries UNDER THIS "
        + "WORKFLOW - other open workflows on the same team do not block it. When a member owns the "
        + "workflow, a Manager woken only by that member's or another member's row in it is not "
        + "counted against the owner; a Manager told to do work in it is. The Manager may declare "
        + "a workflow one of its members owns on that member's behalf, on the same rule, and the "
        + "row records `declaredBy` and `onBehalfOf`. A workflow already declared or closed is "
        + "refused (409).");

// A PERSON ENDING A WORKFLOW, WHICH IS NOT A MANAGER DECLARING A DELIVERY.
//
// `.HumansOnly()` for the reason team reset is: a member able to close its own workflow could erase
// the evidence of having failed at it.
//
// THE REASON IS OPTIONAL, deliberately, although `workflow-complete` refuses a blank `delivered`
// because a completion with no result says work ended and not what was delivered. The mitigation is that the
// CLIENT prefills a suggestion from what the platform knows, so one click still produces a row that
// accounts for itself - and the row carries WHO and WHEN regardless.
//
// {team} in the route so TeamGate covers it structurally, exactly like workflow-complete above; the
// correlation is the CAUSE of the published row rather than a fresh root, so it lands INSIDE the
// workflow it closes rather than rooting a new one of its own.
app.MapPost("/api/teams/{team}/workflows/{correlation:long}/close", async (
    [Description(Describe.Team)] string team,
    [Description(
        "The correlation id shared by every message in the workflow to close, as returned by a "
        + "`tell` or carried on any message in the thread.")]
    long correlation,
    CloseWorkflow request,
    TeamRegistry teams, ContainerHost host, IMessageLog log, HttpContext context,
    CancellationToken ct) =>
{
    if (teams.ExistingName(team) is not { } stored)
    {
        return Results.NotFound(new { error = $"No team '{team}'." });
    }

    // ONE CHECK ANSWERS BOTH "no rows at all" AND "not this team's" - `TeamGate` gates `{team}`
    // structurally, but it cannot ask whether the CORRELATION belongs to that team, so a route
    // identified by a bare id is not gated for its resource at all. See `WorkflowTeamOwnership`'s
    // own doc comment for why the refusal is 404 rather than 403: a 403 would CONFIRM that some
    // other team's correlation is real, which is exactly the tell a caller with no business seeing
    // it must never get. Both cases read identically to an agent or a person: retype the
    // correlation, or check you are on the right team - never "you lack permission", which would
    // point at a permission that was never the problem.
    if (!await WorkflowTeamOwnership.OwnsAsync(teams, host, log, stored, correlation, ct))
    {
        return Results.NotFound(new { error = $"No workflow with correlation {correlation}." });
    }

    // The AUTHENTICATED principal, never a caller-supplied field - see `tell`'s identical comment.
    // `.HumansOnly()` guarantees this is a person, so the id is what the row's Source becomes: a
    // bare id with no `/`, exactly the shape a kanban card event's actor carries.
    var from = PrincipalClaims.From(context.User)?.Id ?? "console";

    var reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();

    await log.AppendAsync(
        new NewMessage(
            MessageTypes.WorkflowClosed,
            // `team` travels in the payload for the same reason a kanban card event's does - see
            // MessageTeam.Of. This row is published by a PERSON, whose Source carries no `/`, so
            // falling through to Source would recover no team and the row would be readable by a
            // person only - invisible to agents on to the very team that closed it.
            JsonSerializer.Serialize(new { team = stored, reason }),
            from,
            correlation),
        ct);

    return Results.NoContent();
})
    .WithTags("Activity")
    .HumansOnly()
    .WithSummary("Close one workflow")
    .WithDescription(
        "A person's declaration that a workflow should stop counting as open - NOT the same as "
        + "`workflow-complete`, which is a manager's declaration of what was delivered. Publishing "
        + "`workflow.completed` on a person's behalf would put a claim on the append-only log that "
        + "nobody made, so this writes `workflow.closed` instead.\n\n"
        + "Additive: nothing is deleted, no floor moves, and the workflow's rows stay on the log and "
        + "in the feed - a closed workflow is still fully readable. New activity on the same "
        + "correlation re-opens it exactly as a manager's own declaration would.\n\n"
        + "`reason` is optional, against the recommendation to require it - `workflow-complete` "
        + "refuses a blank `delivered` because a completion with no result says nothing about what "
        + "was delivered, and the same argument applies here. The row still carries who closed it "
        + "and when regardless of whether a reason was given.\n\n"
        + "`.HumansOnly()`: a member able to close its own workflow could erase the evidence of "
        + "having failed at it, for the reason team reset already follows.\n\n"
        + "404 for an unknown team, and a different 404 - naming the correlation - for one with no "
        + "rows on THIS team, whether the correlation has no rows anywhere or belongs to another "
        + "team entirely. Never 403 for the second case: that would confirm another team's "
        + "correlation exists, which a caller with no business seeing it must never be told. Closing "
        + "an already-closed workflow is 204 and harmless: a second terminal row does not reopen "
        + "anything nothing has woken since.");

// A PERSON WAKING A TEAM'S MANAGER UNDER A SPECIFIC WORKFLOW. A workflow can otherwise only be
// closed from inside a run already woken under it, so a workflow nobody is inside is a workflow nobody can close; nudge is what puts
// somebody back inside it.
//
// THIS IS `tell` IN EVERY RESPECT EXCEPT WHO IS CALLING IT AND WHAT CAUSATION IT IS GIVEN - see
// `tell`'s own comment for why an addressed instruction's TYPE (not its Source) is what
// MessageTeam.Of reads. THE CAUSATION IS THE CORRELATION ITSELF, and that value is never
// caller-suppliable here: it is exactly the correlation `WorkflowTeamOwnership` has just verified
// belongs to this team. A nudge that rooted its own workflow (a null causation, the way `tell`
// does by default) would be a NEW job wearing a rescue's name - the Manager would wake with no
// history of the thread it is meant to be rescuing, and the projection would never connect the
// two. (`tell` accepting an arbitrary CALLER-supplied causation is the platform's own separate
// defect - see WorkflowTeamOwnership's doc comment - and does not apply here: nothing about this
// value came from the request.)
//
// {team} in the route so TeamGate covers it structurally, exactly like close above.
app.MapPost("/api/teams/{team}/workflows/{correlation:long}/nudge", async (
    [Description(Describe.Team)] string team,
    [Description(
        "The correlation id of the workflow to nudge, as returned by a `tell` or carried on any "
        + "message in the thread.")]
    long correlation,
    TeamRegistry teams, ContainerHost host, IMessageLog log, HttpContext context,
    CancellationToken ct) =>
{
    if (teams.ExistingName(team) is not { } stored)
    {
        return Results.NotFound(new { error = $"No team '{team}'." });
    }

    // The IDENTICAL guard close uses, called rather than re-derived - see WorkflowTeamOwnership's
    // own doc comment for why a bespoke check here would silently reopen the hole it took three
    // rounds to close. Same 404-not-403 reasoning: a 403 would confirm another team's correlation
    // is real.
    if (!await WorkflowTeamOwnership.OwnsAsync(teams, host, log, stored, correlation, ct))
    {
        return Results.NotFound(new { error = $"No workflow with correlation {correlation}." });
    }

    // A TEAM CAN LEGITIMATELY HAVE NO MANAGER - a hire converted with no Agent chosen (see the
    // tenant-agent conversion) gets a team and no Manager, on purpose.
    // Naming the problem, rather than the bare "No workflow..." refusal above, is what tells a
    // reader this is not a typo in the correlation.
    var managerId = new ContainerId(stored, TeamRegistry.DefaultManagerName);

    if (host.Find(managerId) is not { } manager)
    {
        return Results.NotFound(new
        {
            error = $"'{teams.LabelFor(stored)}' has no {TeamRegistry.DefaultManagerName} to nudge.",
        });
    }

    // The AUTHENTICATED principal, never a caller-supplied field - the same rule `tell` and close
    // follow.
    var from = PrincipalClaims.From(context.User)?.Id ?? "console";

    const string nudgeText =
        "A person nudged this workflow back to life. Look at everything under this thread and "
        + "either continue the work, declare it complete with workflow-complete, or say why it "
        + "should stop.";

    var parts = InstructionText.Split(nudgeText);

    await log.AppendAsync(
        new NewMessage(
            MessageTypes.InstructionFor(manager.Id),
            JsonSerializer.Serialize(new
            {
                instruction = nudgeText,
                subject = parts.Subject,
                body = parts.Body,
            }),
            from,
            correlation),
        ct);

    if (!teams.IsPaused(stored))
    {
        return Results.NoContent();
    }

    return Results.Ok(new
    {
        paused = true,
        pauseNotice = PauseNoticeFor(teams.LabelFor(stored)),
    });
})
    .WithTags("Activity")
    .HumansOnly()
    .WithSummary("Wake this team's Manager under one workflow")
    .WithDescription(
        "Publishes an addressed instruction to the team's Manager whose CAUSATION IS THE "
        + "CORRELATION, so the Manager wakes with that thread's own history rather than the head "
        + "of a new one - the one affordance missing before this route: a workflow nobody is "
        + "currently inside could otherwise only be closed, never picked back up.\n\n"
        + "If the team is paused the instruction is still logged and the response says it will run "
        + "after resume, rather than reading like immediate execution.\n\n"
        + "LIKE `tell`, THIS QUEUES RATHER THAN INTERRUPTS: a 204 means the instruction was "
        + "recorded, not that the Manager has already woken to read it. It joins whatever that "
        + "member's own queue already holds, in order; and if the Manager is at its queue ceiling "
        + "the platform publishes `agentContainer.rejected` on this same workflow instead of accepting "
        + "the nudge - both are the platform's existing shape for every addressed instruction, "
        + "inherited rather than new here.\n\n"
        + "404 for an unknown team; the identical 404 close uses for a correlation with no rows on "
        + "THIS team, never 403 (see close's own description); and a third 404 naming "
        + $"'{TeamRegistry.DefaultManagerName}' when the team has none to wake - a reachable state, "
        + "not a bug.\n\n"
        + "`.HumansOnly()`: a member able to nudge its own team's workflow could splice a fresh "
        + "instruction into a thread on its own authority.");

// A PERSON RELEASING ONE PAUSED WORKFLOW.
//
// THE UNIT IS ONE WORKFLOW AND NEVER THE TEAM. `POST /api/teams/{team}/resume` beside it releases
// a TEAM; this releases one correlation, so a team with three open workflows that paused one gets
// that one back and leaves the other two exactly as they were.
//
// IT IS A SEPARATE VERB FROM NUDGE ON PURPOSE. Resuming must not require knowing that `nudge`
// is the verb - a person looking at something that says PAUSED
// should press Resume. Mechanically the two rows it writes are a nudge plus a state change, but
// that is an implementation the caller is not asked to know.
//
// AND IT CLEARS NO COUNTER, WHICH IS THE WHOLE POINT. There is no counter to clear: the spend
// window is computed by `GetSpendSinceNudgeAsync` as "since the last addressed instruction whose
// causation is the correlation", so writing such an instruction MOVES the window as a consequence
// of what it is. Nothing here zeroes anything, there is no field to reset, and therefore no
// mechanism a platform actor could reach for. `.HumansOnly()` is the other half: `PermitGate` refuses every machine principal
// before this handler runs.
app.MapPost("/api/teams/{team}/workflows/{correlation:long}/resume", async (
    [Description(Describe.Team)] string team,
    [Description(
        "The correlation id of the paused workflow to resume, as carried on every row in the "
        + "thread and returned as `correlation` by the team's `/workflows` route.")]
    long correlation,
    TeamRegistry teams, ContainerHost host, IMessageLog log, HttpContext context,
    CancellationToken ct) =>
{
    if (teams.ExistingName(team) is not { } stored)
    {
        return Results.NotFound(new { error = $"No team '{team}'." });
    }

    // The IDENTICAL guard close, nudge and stop use, called rather than re-derived. Same
    // 404-not-403 reasoning: a 403 would confirm another team's correlation is real.
    if (!await WorkflowTeamOwnership.OwnsAsync(teams, host, log, stored, correlation, ct))
    {
        return Results.NotFound(new { error = $"No workflow with correlation {correlation}." });
    }

    var managerId = new ContainerId(stored, TeamRegistry.DefaultManagerName);

    if (host.Find(managerId) is not { } manager)
    {
        return Results.NotFound(new
        {
            error = $"'{teams.LabelFor(stored)}' has no {TeamRegistry.DefaultManagerName} to wake.",
        });
    }

    // NOT PAUSED IS A 204 THAT WRITES NOTHING. Idempotent, and the case is reachable rather than
    // theoretical: the Console renders the button off `pausedAt`, which it holds from a fetch that
    // has already happened, so two people looking at the same row can both press it. A resume that
    // spent a manager invocation on a workflow nobody had paused would be the surprise.
    var thread = await log.ReadCorrelationAsync(correlation, ct);

    if (!WorkflowPause.IsPaused(thread))
    {
        return Results.NoContent();
    }

    // The AUTHENTICATED principal, never a caller-supplied field - the same rule `tell`, close and
    // nudge all follow.
    var from = PrincipalClaims.From(context.User)?.Id ?? "console";

    // THE STATE ROW FIRST, THE WAKE SECOND, AND THE ORDER IS LOAD-BEARING. If the instruction
    // landed first the pump could reach it while the correlation still read paused, and
    // `OverBudgetAsync` would refuse the very wake this route exists to deliver - a resume that
    // silently did nothing and burned a `Rejected` row saying so.
    await log.AppendAsync(
        new NewMessage(
            MessageTypes.WorkflowResumed,

            // `team` IS WRITTEN BY THE SERVER, from the value TeamGate has already checked and
            // ExistingName resolved - never from anything the caller typed. `MessageTeam.Of` reads
            // this field for this type, because the Source here is a person's bare id with no team
            // in it; a row without it is invisible to the team that resumed the workflow.
            JsonSerializer.Serialize(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [PayloadFields.Team] = stored,
            }),
            from,
            correlation),
        ct);

    const string resumeText =
        "A person resumed this workflow after it paused for reaching its spend limit. Its spend "
        + "window starts again from here. Look at everything under this thread and either "
        + "continue the work, declare it complete with workflow-complete, or say why it should "
        + "stop.";

    var resumeParts = InstructionText.Split(resumeText);

    await log.AppendAsync(
        new NewMessage(
            MessageTypes.InstructionFor(manager.Id),
            JsonSerializer.Serialize(new
            {
                instruction = resumeText,
                subject = resumeParts.Subject,
                body = resumeParts.Body,
            }),
            from,

            // THE CAUSATION IS THE CORRELATION, and this line is the entire rescue. It is what
            // `GetSpendSinceNudgeAsync` windows on, so the figure the guard reads starts again
            // from this row - which is what stops a resumed workflow from re-hitting the limit on
            // its very next wake. The value is not caller-suppliable: it is the correlation
            // `WorkflowTeamOwnership` has just verified belongs to this team.
            correlation),
        ct);

    // AND EVERY OTHER BROWSER HOLDING THIS TEAM IS TOLD TO COME AND RE-READ.
    //
    // NOT FOR THE PERSON WHO PRESSED RESUME - they refetch locally the moment this returns. It is
    // for ANYBODY ELSE watching the same team: the Console has no timer and refetches a team's
    // workflows only from inside its `containerChanged` handler, so a console that was open when
    // the workflow paused goes on rendering PAUSED, and a live Resume button, over a workflow
    // somebody else has already released.
    //
    // The pump writes the pause and republishes there for the same reason - see
    // `ContainerHost.PauseWorkflowAsync`. `RepublishTeam` re-emits this team's snapshots UNCHANGED through the
    // event the hub already forwards: no new field on `ContainerSnapshot`, no new hub event.
    //
    // AFTER BOTH APPENDS, so a console that refetches on this frame reads a workflow that is
    // already released AND already woken, rather than one caught between its two rows. And below
    // the not-paused early return above, which writes nothing: a second press on a workflow
    // nobody had paused must not make every browser on the team refetch an unchanged projection.
    host.RepublishTeam(stored);

    // THE TEAM MAY ALSO BE PAUSED, which is a different thing and this route does not touch it.
    // Mirrors nudge: the rows are still written and the answer says so, rather than a 204 that
    // reads like the Manager is already awake.
    if (!teams.IsPaused(stored))
    {
        return Results.NoContent();
    }

    return Results.Ok(new
    {
        paused = true,
        pauseNotice = PauseNoticeFor(teams.LabelFor(stored)),
    });
})
    .WithTags("Activity")
    .HumansOnly()
    .WithSummary("Resume one paused workflow")
    .WithDescription(
        "Releases a workflow that PAUSED for reaching the per-workflow spend figure in force for "
        + "its team, and wakes the Manager under it.\n\n"
        + "**ONE WORKFLOW, NEVER THE TEAM.** `POST /api/teams/{team}/resume` releases a team; this "
        + "releases one correlation and leaves every sibling workflow exactly as it was.\n\n"
        + "**THE SPEND WINDOW STARTS AGAIN**, because the instruction this writes is caused by the "
        + "correlation - which is what the per-nudge counter measures from. A rescue that "
        + "immediately re-hit the limit would be no rescue. Nothing is reset and there is no "
        + "counter to reset: no platform-generated wake can reach this, because the route is "
        + "`.HumansOnly()` and because causing an instruction by the correlation is the only "
        + "mechanism there is.\n\n"
        + "204 when it resumed, and also 204 when the workflow was not paused - in which case "
        + "NOTHING is written, so pressing it twice costs one invocation rather than two.\n\n"
        + "If the TEAM is paused the rows are still written and the response says they will run "
        + "after the team resumes.\n\n"
        + "404 for an unknown team; the identical 404 close and nudge use for a correlation with "
        + "no rows on THIS team, never 403; and a third 404 naming "
        + $"'{TeamRegistry.DefaultManagerName}' when the team has none to wake.");

// THE CORRELATION-SCOPED SIBLING OF THE PER-CONTAINER STOP ABOVE. Ends the run of every member
// currently Running UNDER THIS WORKFLOW and leaves every other member alone - including one
// Running under a DIFFERENT correlation on the same team, which is not this call's business, for
// the identical reason `WorkflowBusyState` narrows on `CurrentCorrelation` rather than the team
// alone.
app.MapPost("/api/teams/{team}/workflows/{correlation:long}/stop", async (
    [Description(Describe.Team)] string team,
    [Description(
        "The correlation id of the workflow to stop, as returned by a `tell` or carried on any "
        + "message in the thread.")]
    long correlation,
    TeamRegistry teams, ContainerHost host, IMessageLog log, CancellationToken ct) =>
{
    if (teams.ExistingName(team) is not { } stored)
    {
        return Results.NotFound(new { error = $"No team '{team}'." });
    }

    if (!await WorkflowTeamOwnership.OwnsAsync(teams, host, log, stored, correlation, ct))
    {
        return Results.NotFound(new { error = $"No workflow with correlation {correlation}." });
    }

    // A snapshot per RUNNING member of this team whose CURRENT workflow is this one - never the
    // team alone, or stopping one workflow would end every member's run on the whole team
    // regardless of what it was doing.
    //
    // A NARROW TOCTOU, ACCEPTED RATHER THAN CLOSED: `Snapshots()` and `Stop()` are two separate
    // reads of a container that keeps running between them, so a member that finishes THIS
    // workflow and picks up a NEW one in that instant has the new run cancelled instead - the
    // description below says so rather than promising more than this code does. Sub-millisecond
    // and triggered only by a person clicking Stop, so closing it needs a
    // stop-if-still-on-this-correlation check taken under the container's own lock - a new
    // synchronisation primitive on `MemberRuntime` for a race this narrow and this recoverable
    // (a second click ends whatever it actually caught). Documented rather than closed, as this
    // codebase does for its other narrow, human-triggered races.
    foreach (var snapshot in host.Snapshots())
    {
        if (!string.Equals(snapshot.Team, stored, StringComparison.OrdinalIgnoreCase)) continue;
        if (snapshot.State != ContainerState.Running) continue;
        if (snapshot.CurrentCorrelation != correlation) continue;

        host.Find(new ContainerId(snapshot.Team, snapshot.Id))?.Stop();
    }

    return Results.NoContent();
})
    .WithTags("Activity")
    .HumansOnly()
    .WithSummary("Stop everything running under one workflow")
    .WithDescription(
        "Ends the run of every member currently Running under this correlation - the "
        + "correlation-scoped sibling of `POST .../containers/{name}/stop`. A member Running under "
        + "a DIFFERENT workflow, even on the same team, is left untouched, MODULO a narrow race: a "
        + "member observed here that finishes this workflow and starts a new one in the instant "
        + "before the stop reaches it has that NEW run cancelled instead - sub-millisecond, and "
        + "only ever triggered by a person's own click, so a second Stop resolves it.\n\n"
        + "204 whether or not anything was actually running: idempotent by nature, since by the "
        + "time a click reaches the server the workflow it targeted may already have gone quiet.\n\n"
        + "404 for an unknown team, and the identical 404 close and nudge use for a correlation "
        + "with no rows on THIS team, never 403.\n\n"
        + "`.HumansOnly()`, for the identical reason as the per-container stop and workflow close: "
        + "a member able to stop its own workflow's run could erase the evidence of having failed "
        + "at it.");

// Carries no {team} route value either, and leaks the most if missed: every message of every
// team, instruction text and agent output included, is what the board's activity feed renders.
app.MapGet("/api/messages", async (
    [Description(
        "Return only messages after this sequence number. Pass the highest `seq` from the previous "
        + "call to poll forward; omit it to start from the beginning.")]
    long? after,

    [Description(
        "How many of the most recent messages to consider, before the team filter below. Default "
        + "200, clamped to 2000.\n\nA VIEWER-FACING KNOB rather than tuning: the cap is applied "
        + "BEFORE filtering to the teams a caller may see, so a quiet member on a busy team drops "
        + "out of the window entirely and its card renders empty - which is indistinguishable from "
        + "having never done anything. Raising this widens how far back the board can reach.")]
    int? take,

    [Description(
        "One member's history OLDER than this sequence number, newest first - the backward cursor "
        + "under the live window. Pass the last row's `seq` from the previous page. Needs `team` "
        + "and `member`, and cannot be combined with `after`. With it, `take` defaults to 50 and is "
        + "clamped to 1..200.")]
    long? before,

    [Description("With `before`: the member's team.")] string? team,
    [Description("With `before`: the member's current identifier.")] string? member,

    HttpContext context, IMessageLog log, TeamAccess access, TeamRegistry teams,
    CancellationToken ct) =>
{
    if (PrincipalClaims.From(context.User) is not { } principal) return Results.Unauthorized();

    if (before is not null || team is not null || member is not null)
    {
        return await MemberHistory.ReadAsync(
            principal, after, before, team, member, take, log, access, teams, ct);
    }

    // THE CATALOG IS THE SET, not a union over live subscriptions.
    //
    // A union over subscriptions would make the feed's coverage a property of the current ROSTER:
    // card events would reach the feed only because a Manager happened to subscribe to them, so a
    // team with no Manager - a shape this platform supports - could never see one.
    //
    // `EventCatalogCoverageTests` fails the build on a type the catalog does not declare, which is
    // what keeps a type nobody subscribes to from being invisible here.
    var types = FeedTypes.Explicit;

    var effective = await access.EffectiveTeamsAsync(principal, ct);
    var person = principal.Kind == PrincipalKind.User;

    // The NEWEST 200, not the oldest, and that word matters.
    //
    // Filtering by team happens below, after the cap. A window that started at `after` and ran
    // FORWARD would, on a cold load with after=0, be the first two hundred rows the log ever took -
    // so once a log holds 200 rows belonging to teams the caller cannot see (every deleted team
    // qualifies, and they are never coming back) the window would contain nothing else and this
    // route would answer `[]` permanently: a board showing "Nothing yet." over a running team.
    // A cap applied BEFORE a filter degrades to nothing rather than to less.
    //
    // Filtering by team in the query would solve it at the other end, and is refused: MessageTeam.Of
    // reads the TYPE for an instruction and the SOURCE for a container event, and pushing that rule
    // into SQL is a second copy of it in a second language, free to drift. Reading the newest rows gets the behaviour right without it.
    // Clamped rather than trusted. This is a per-viewer preference reaching a query cost, so the
    // ceiling is the server's to set - and the floor stops a zero or negative asking for nothing
    // and rendering an empty board that looks like a fault.
    var window = Math.Clamp(take ?? 200, 50, 2000);

    var messages = await log.ReadLatestAsync(after ?? 0, types, window, ct);

    return Results.Ok(messages.Where(m => MessageTeam.Of(m) switch
    {
        // Unattributable: a caller-supplied `from`, or a row predating qualified identity. Shown
        // to a person, hidden from every machine principal - the conservative reading, and consistent
        // with refusals being byte-identical to nonexistent below.
        //
        // A `kanban.card.*` ROW IS NOT ON THE `null` ARM. Those rows are published by a PERSON, so
        // their source is a bare user id with no `/`; `MessageTeam.Of` has a card arm that
        // resolves the team off the payload, so they are visible to anyone reaching that team,
        // exactly like the instruction and completion rows either side of them on the same feed -
        // rather than PEOPLE-ONLY.
        //
        // THE DIRECTION IS RIGHT AND THE WIDENING IS BOUNDED: it is `effective.Contains(team)`,
        // the same team set every other row on this feed is filtered by, and the team on a card
        // row is the server's own - written from a card resolved inside the `{team}` route value
        // `TeamGate` already checked. What would be wrong is the opposite state: a person edits a card on their own team and cannot see the row they just wrote.
        // Pinned by `FilteredReadTests.A_card_event_reaches_the_teams_members_and_nobody_else`.
        null => person,
        var team => effective.Contains(team),
    }));
})
    .WithTags("Activity")
    .RequirePermit(Permits.Read)
    .WithSummary("Recent activity across every team you can see")
    .WithDescription(
        "The append-only message log: instructions going in, and what members published coming "
        + "back. This is what the board's activity feed renders.\n\n"
        + "At most 200 rows per call, and the cap applies BEFORE the caller's teams are filtered "
        + "out - so you may receive fewer than 200 and should page on `seq` rather than assume a "
        + "short page means the end.\n\n"
        + "Messages that cannot be attributed to a team are shown only to people.\n\n"
        + "**Backward, one member** (`before`, `team`, `member`): that member's history below "
        + "`before`, NEWEST FIRST - instructions addressed to it and the feed rows it published - "
        + "at most `take` (default 50, clamped to 1..200). The next cursor is the last row's `seq`; "
        + "an empty page is the end, which is the first message the member ever received. A row "
        + "appended meanwhile is above every cursor and never enters or shifts an older page. 400 "
        + "when `before`, `team` and `member` are not all given or `after` is given with them; 404 "
        + "for an unknown team or member, or one the caller cannot see.");

app.MapGet("/api/workflows/{correlationId:long}", async (
    [Description(
        "The correlation id shared by every message in one workflow, as returned by a `tell` or "
        + "carried on any message in the thread.")]
    long correlationId,
    HttpContext context, IMessageLog log, TeamAccess access, CancellationToken ct) =>
{
    if (PrincipalClaims.From(context.User) is not { } principal) return Results.Unauthorized();

    var effective = await access.EffectiveTeamsAsync(principal, ct);
    var person = principal.Kind == PrincipalKind.User;

    var messages = await log.ReadCorrelationAsync(correlationId, ct);

    // A workflow belonging to another team and one that never existed both come back `200 []` -
    // there is no second code path distinguishing "not yours" from "not there".
    // `kanban.card.*` rows are on this thread too, and they are team-visible rather than
    // people-only - see the same switch on `/api/activity` for what moved and why.
    var visible = messages.Where(m => MessageTeam.Of(m) switch
    {
        null => person,
        var team => effective.Contains(team),
    }).ToList();

    return Results.Ok(visible);
})
    .WithTags("Activity")
    .RequirePermit(Permits.Read)
    .WithSummary("Every message in one workflow")
    .WithDescription(
        "One thread of work, in order - the manager's dispatch, the worker's completion, the "
        + "manager's reaction to it, and so on. Correlation is stamped by the containers "
        + "themselves, never by a prompt, so the thread cannot be broken by an agent forgetting to "
        + "carry it.\n\n"
        + "A workflow belonging to another team and a workflow that never existed both answer "
        + "`200 []`. There is deliberately no second code path telling \"not yours\" apart from "
        + "\"not there\".");

app.MapGet("/api/workflows/{correlationId:long}/wait", async (
    [Description(
        "The correlation id shared by one workflow, as returned by `tell`.")]
    long correlationId,

    [Description(
        "How long to wait in seconds before answering `timeout`. Defaults to 120 and is capped at 900.")]
    int? timeoutSeconds,
    HttpContext context, IMessageLog log, TeamAccess access, MessageWaitRegistry waits,
    ContainerHost host, IPendingDeliveries pending,
    CancellationToken ct) =>
{
    const int defaultTimeoutSeconds = 120;
    const int maxTimeoutSeconds = 900;

    if (PrincipalClaims.From(context.User) is not { } principal) return Results.Unauthorized();

    if (timeoutSeconds is <= 0)
    {
        return Results.BadRequest(new { error = "timeoutSeconds must be greater than zero." });
    }

    var effective = await access.EffectiveTeamsAsync(principal, ct);
    var person = principal.Kind == PrincipalKind.User;
    var timeout = Math.Min(timeoutSeconds ?? defaultTimeoutSeconds, maxTimeoutSeconds);
    var terminalTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        // Section 5.1 ("The team that went quiet - v.2"): agentContainer.completed is deliberately
        // excluded. It is published after each leg and would end a wait while the workflow is
        // still in progress, returning confidently wrong.
        MessageTypes.WorkflowCompleted,

        // `WorkflowOpenSql` already treats this as terminal for the open-count projection - without
        // it here, "is this workflow over" has two readers that disagree, and a person closing a
        // STUCK workflow (precisely because nobody will ever finish it) leaves a waiter hanging up
        // to the full timeout instead of ending immediately.
        MessageTypes.WorkflowClosed,
        MessageTypes.Blocked,
        MessageTypes.NeedsDecision,
        MessageTypes.Failed,
    };

    bool VisibleToCaller(Message message) => MessageTeam.Of(message) switch
    {
        null => person,
        var team => effective.Contains(team),
    };

    async Task<Message?> CurrentTerminalAsync()
    {
        var messages = await log.ReadCorrelationAsync(correlationId, ct);

        return messages.LastOrDefault(message =>
            terminalTypes.Contains(message.Type) && VisibleToCaller(message));
    }

    if (await CurrentTerminalAsync() is { } terminal)
    {
        return Results.Ok(WorkflowWaitReply.From(correlationId, terminal, timeout));
    }

    using var registration = waits.Register(message =>
        message.CorrelationId == correlationId
        && terminalTypes.Contains(message.Type)
        && VisibleToCaller(message));

    if (await CurrentTerminalAsync() is { } settled)
    {
        return Results.Ok(WorkflowWaitReply.From(correlationId, settled, timeout));
    }

    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeout));

    try
    {
        var arrived = await registration.WaitAsync(timeoutCts.Token);
        return Results.Ok(WorkflowWaitReply.From(correlationId, arrived, timeout));
    }
    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
    {
        var all = await log.ReadCorrelationAsync(correlationId, ct);
        var visible = all.Where(VisibleToCaller).ToArray();
        var team = visible
            .Select(MessageTeam.Of)
            .FirstOrDefault(name => name is not null);

        if (team is null)
        {
            return Results.Ok(WorkflowWaitReply.Timeout(correlationId, timeout, TimeoutVerdict.Working));
        }

        if (visible.Any(message =>
                string.Equals(message.Type, MessageTypes.WorkflowCompleted, StringComparison.Ordinal)
                || string.Equals(message.Type, MessageTypes.WorkflowClosed, StringComparison.Ordinal)))
        {
            return Results.Ok(WorkflowWaitReply.Timeout(correlationId, timeout, TimeoutVerdict.Working));
        }

        var busy = await TeamBusyState.DescribeAsync(team, host, pending, excluded: null, ct);
        if (busy.Count != 0)
        {
            return Results.Ok(WorkflowWaitReply.Timeout(correlationId, timeout, TimeoutVerdict.Working));
        }

        var snapshots = host.Snapshots()
            .Where(snapshot => string.Equals(snapshot.Team, team, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (snapshots.Length == 0)
        {
            return Results.Ok(WorkflowWaitReply.Timeout(correlationId, timeout, TimeoutVerdict.Working));
        }

        var teamTerminals = new HashSet<string>(StringComparer.Ordinal)
        {
            MessageTypes.Completed,
            MessageTypes.Blocked,
            MessageTypes.Failed,
            MessageTypes.NeedsDecision,
        };
        var latestTerminals = new List<Message>(snapshots.Length);
        foreach (var snapshot in snapshots)
        {
            var rows = await log.ReadForContainerAsync($"{snapshot.Team}/{snapshot.Id}", 0, 200, ct);
            var latestTerminal = rows.FirstOrDefault(message => teamTerminals.Contains(message.Type));
            if (latestTerminal is null || latestTerminal.Type != MessageTypes.Completed)
            {
                return Results.Ok(WorkflowWaitReply.Timeout(correlationId, timeout, TimeoutVerdict.Working));
            }

            latestTerminals.Add(latestTerminal);
        }

        var idleSince = latestTerminals.OrderByDescending(row => row.Seq).First().OccurredAt;
        return Results.Ok(WorkflowWaitReply.Timeout(
            correlationId,
            timeout,
            TimeoutVerdict.Quiet,
            idleSince));
    }
})
    .WithTags("Activity")
    .RequirePermit(Permits.Read)
    .WithSummary("Wait for one workflow to end")
    .WithDescription(
        "Holds one request until this workflow ends, or the timeout returns `timeout`.\n\n"
        + "Workflow-ending rows are `workflow.completed`, `workflow.closed`, `agentContainer.blocked`, "
        + "`agentContainer.needsDecision` and "
        + "`agentContainer.failed` under this correlation.\n\n"
        + "Timeout is a normal answer and returns 200.");

// The console is on a RAW WebSocket, not the SignalR hub. SignalR is message-oriented; a PTY is a
// bidirectional byte stream with binary frames, and forcing one through the other would mean framing
// bytes that are already framed.
app.UseWebSockets();

// cols/rows are on the CONNECT request, not left to the first resize frame. The child prints its
// banner the instant it spawns, so a size that arrives afterwards means those bytes were laid out for
// a guessed default width - and a terminal transcript has its width baked in, so replaying them into
// the real terminal is scrambled rather than merely narrow.
//
// One Concierge per PERSON, not per team, so the route names no team. Outside TeamGate because the
// template has no {team}.
//
// MUST NOT call SetCurrentTeamAsync. Opening the Concierge must leave the current team as it is.
// Setting it here would be picking a team on the person's behalf.
app.MapGet("/api/concierge/ws", async (
    int? cols, int? rows, HttpContext context,
    ConciergeSessionStore consoles, CancellationToken ct) =>
{
    if (PrincipalClaims.From(context.User) is not { } caller) return Results.Unauthorized();

    // A console is a HUMAN's door. A machine principal has no person to belong to: its id is not a
    // user id, so minting would fail the owner foreign key and surface as a 500 on a WebSocket
    // upgrade - a confusing answer to a request that should simply be refused.
    if (caller.Kind != PrincipalKind.User)
    {
        return Results.Content(PermitGate.HumansOnlyBody, "application/json", statusCode: 403);
    }

    // MUST NOT call SetCurrentTeamAsync.

    var key = new ConciergeSessionKey(caller.Id);

    var width = PtyDimensionLimits.ClampOrDefault(cols, PtySpecDefaults.Cols);
    var height = PtyDimensionLimits.ClampOrDefault(rows, PtySpecDefaults.Rows);

    if (!context.WebSockets.IsWebSocketRequest)
    {
        return Results.BadRequest(new { error = "This endpoint speaks WebSocket." });
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();

    ConciergeSession console;

    try
    {
        // Attached AFTER the upgrade so that a launch failure can be reported in the terminal. A
        // refused upgrade shows an empty panel and puts the reason in devtools.
        // THE ADDRESS THIS BROWSER REACHED US ON becomes the Concierge's HARNESS_PUBLIC_URL. The
        // scheme is the tunnel's when one forwarded it (UseForwardedHeaders); the host is what the
        // browser asked for.
        console = await consoles.AttachAsync(
            key, "", width, height, ct,
            $"{context.Request.Scheme}://{context.Request.Host.Value}{context.Request.PathBase.Value}");
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        var message = Encoding.UTF8.GetBytes(
            $"\u001b[31mThe console could not start: {ex.Message}\u001b[0m\r\n");

        await socket.SendAsync(message, WebSocketMessageType.Binary, true, ct);
        await socket.CloseOutputAsync(WebSocketCloseStatus.InternalServerError, "launch failed", ct);
        return Results.Empty;
    }

    await PtyWebSocket.PumpAsync(
        socket, console.Record, console.Session, console.Attachment, width, height, ct);

    // Deliberately nothing here: the pump returning means this CLIENT went away, and the session
    // keeps running for whoever attaches next. ConciergeReaper bounds a session nobody returns to.
    return Results.Empty;
})
    // A WebSocket upgrade wearing a GET, so it is described out of the API reference rather than
    // rendered there with a Send button that can only ever fail.
    .ExcludeFromDescription()
    .HumansOnly();

// Ending a session is explicit, and separate from closing the panel. There is no close-that-kills on
// the panel chrome at all - this is what a confirmed action in settings calls.
// The session is keyed on the person, so ending it needs no team in the path. MUST NOT call
// SetCurrentTeamAsync.
app.MapDelete("/api/concierge", async (
    [Description(
        "Whose console to end. Omit it to end your own; any person may end anyone's.")]
    string? user,
    HttpContext context, ConciergeSessionStore consoles) =>
{
    if (PrincipalClaims.From(context.User) is not { } caller) return Results.Unauthorized();

    await consoles.EndAsync(new ConciergeSessionKey(user ?? caller.Id));

    return Results.NoContent();
})
    .WithTags("Concierge")
    .HumansOnly()
    .WithSummary("End this person's Concierge session")
    .WithDescription(
        "Kills the Concierge's process. This is separate from closing the panel on purpose: "
        + "detaching from a session and destroying it are different intentions and only one of "
        + "them is recoverable.\n\n"
        + "The session is keyed on the person, so this names no team and does not point the "
        + "person at one. A session nobody returns to is ended on a timer as well.\n\n"
        + "Always 204 when it is allowed at all, whether or not a session was running.");

// Read, because that is honestly what a hub connection is: the board, pushed. Nothing
// machine-shaped subscribes to SignalR today - a container reaches the system through MCP tools -
// but declaring HumansOnly would be a bound justified by "nothing does this yet", which is the
// weakest reason there is and the one that stops being true without anybody noticing.
app.MapHub<ContainerHub>("/hub/containers").RequirePermit(Permits.Read);

app.Run();

// An orderly close, and nothing rests on it: every write is already flushed, which is what makes
// the file survive a KILL rather than only a shutdown. This is the tidy half of the same promise.
hostLog.Dispose();

/// <summary>
/// The one place a documents failure becomes a status code.
///
/// Written once rather than per endpoint because the mapping is the contract: a path outside the
/// team is a 400 and not a 404 (a 404 would answer "does that file exist?" about somewhere the
/// caller has no business), a missing file is a 404, and a folder that still has something in it
/// is a 409 - the one refusal a person is expected to hit and act on.
/// </summary>
/// <summary>
/// The documents folder a READ may name: a live team in its stored spelling, or a folder on disk
/// whose team is gone. Null when there is neither, which the caller turns into the same 404 an
/// unknown team has always answered.
///
/// **THIS IS WHAT LETS A DELETED TEAM'S DOCUMENTS BE READ AT ALL**, and it is why they need no
/// second route group: `/api/teams/{team}/documents` names its team EXPLICITLY rather than
/// implying it from whichever team is active, so the only thing that could stop it serving a
/// dead team's folder is a lookup insisting the team be live.
///
/// **THE GATE IS UNCHANGED AND IS NOT HERE.** `TeamGate` keys on the route's `{team}` value, so
/// these routes are gated by structure: a live team is reachable by everyone it binds, and a team
/// that no longer exists is absent from every effective set - which leaves rule 3 as the only way
/// through and makes a dead team's folder PEOPLE ONLY. That is the
/// safe default, and nothing here re-decides it: a helper that widened who may read would
/// be a second answer sitting underneath the first.
///
/// ADDING WRITES DO NOT USE THIS. Upload and create-folder keep asking `ExistingName` and keep
/// answering 404 for a team that is gone: a dead team's documents are a record of what happened,
/// and a record is not added to. DELETE DOES: a person may clear a record out.
///
/// THE FOLDER MUST BE THERE ON DISK for the dead-team arm. Composing the path alone would answer
/// 200-with-nothing for every misspelling, which reads as "that team had no documents" - a
/// silent-empty state.
/// </summary>
static string? DocumentsFolderOf(TeamRegistry teams, TeamDocuments docs, string team)
{
    if (teams.ExistingName(team) is { } stored)
    {
        // REPAIRED FOR A LIVE TEAM, and only for a live team. Creating the folder on read is what
        // brings a team whose directory was removed by hand back working; a folder is also
        // reachable by the name of a team that no longer exists, and repairing there would
        // manufacture an empty folder for every name anybody mistyped.
        docs.EnsureFor(stored);

        return stored;
    }

    // Checked before composing, not after: TeamPaths refuses a segment it did not compose, and a
    // route value is the one place a name arrives that ContainerId has never vetted.
    if (!TeamPaths.IsDocumentsFolder(team)) return null;

    return Directory.Exists(docs.RootFor(team)) ? team : null;
}

static IResult Documents(Func<IResult> action)
{
    try
    {
        return action();
    }
    catch (DocumentPathException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (FileNotFoundException)
    {
        return Results.NotFound(new { error = "No such document." });
    }
    catch (DirectoryNotFoundException)
    {
        return Results.NotFound(new { error = "No such folder." });
    }
    catch (InvalidOperationException ex)
    {
        return Results.Conflict(new { error = ex.Message });
    }
    catch (IOException ex)
    {
        return Results.Conflict(new { error = ex.Message });
    }
}

static async Task<IResult> DocumentsAsync(Func<Task<IResult>> action)
{
    try
    {
        return await action();
    }
    catch (DocumentPathException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (FileNotFoundException)
    {
        return Results.NotFound(new { error = "No such document." });
    }
    catch (DirectoryNotFoundException)
    {
        return Results.NotFound(new { error = "No such folder." });
    }
    catch (InvalidOperationException ex)
    {
        return Results.Conflict(new { error = ex.Message });
    }
    catch (IOException ex)
    {
        return Results.Conflict(new { error = ex.Message });
    }
}

// ISO-8601 UTC, `Z`-suffixed, as the member routes answer it.
static string? SystemPromptSetAt(SystemPromptSetter? setter) =>
    setter?.At.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

// A member edit's tenant rows: `member.changed` always, and ITS OWN ROW when the member's own
// instructions changed, so "who told this member to be what it is" is found by name. Who and
// whether cleared - never the words.
static IReadOnlyList<TriggerAudit> MemberAuditRows(HttpContext context, MemberChange change, string? agent)
{
    var subject = $"{change.Id.Team}/{change.Id.Name}";
    var rows = new List<TriggerAudit>
    {
        TenantLogging.Row(
            context, TenantActions.MemberChanged, subject, change.Name,
            new { team = change.Id.Team, renamed = change.Renamed, promptChanged = change.PromptChanged, agent }),
    };

    if (change.PromptChanged)
    {
        rows.Add(TenantLogging.Row(
            context, TenantActions.MemberInstructionsChanged, subject, change.Name,
            new
            {
                team = change.Id.Team,
                setBy = change.Row.SystemPromptSetBy?.By,
                setByKind = change.Row.SystemPromptSetBy?.Kind,
                cleared = change.Row.SystemPrompt is null,
            }));
    }

    return rows;
}

static bool TeamHasMember(TeamRegistry teams, string team, string member) =>
    teams.ContainerIdsOf(team).Any(id => string.Equals(id.Name, member, StringComparison.OrdinalIgnoreCase));

static async Task<(string Agent, bool Substituted)> ResolveMemberAgentForHireAsync(
    string team,
    IReadOnlyList<string> allowlist,
    string? requestedTag,
    ITeamStore teamStore,
    AgentCatalog catalog,
    CancellationToken ct)
{
    if (string.IsNullOrWhiteSpace(requestedTag))
    {
        return (allowlist[0], false);
    }

    var tagged = allowlist
        .Where(agent => AgentCarriesTag(catalog, agent, requestedTag))
        .ToArray();

    if (tagged.Length == 0)
    {
        return (allowlist[0], true);
    }

    var members = (await teamStore.MembersAsync(ct))
        .Where(member => string.Equals(member.Team, team, StringComparison.OrdinalIgnoreCase))
        .ToArray();

    var selected = tagged
        .Select((agent, index) => new
        {
            Agent = agent,
            Index = index,
            Count = members.Count(member =>
                string.Equals(member.Agent, agent, StringComparison.OrdinalIgnoreCase)
                && string.Equals(member.HiredFor, requestedTag, StringComparison.OrdinalIgnoreCase)),
        })
        .OrderBy(entry => entry.Count)
        .ThenBy(entry => entry.Index)
        .First().Agent;

    return (selected, false);
}

static bool AgentCarriesTag(AgentCatalog catalog, string agent, string tag)
{
    var definition = catalog.Definition(agent);

    if (definition?.Tags is null || definition.Tags.Count == 0)
    {
        return false;
    }

    return definition.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// When a schedule with this shape should first fire, or null when the shape cannot say.
///
/// Takes the kind as the STRING the wire carries, because that is what both routes hold - the
/// validator reads the same string. Parsing here rather than at each call site keeps the
/// case-insensitivity in one place.
///
/// Null for an unparseable kind is safe: `Triggers.Validate` has already refused anything that is
/// not a known kind by the time this is reached.
/// </summary>
static DateTimeOffset? FirstOccurrence(
    string kind,
    string? expression,
    string? timezone,
    int? intervalSeconds,
    DateTimeOffset? fireAt,
    DateTimeOffset after) =>
    Enum.TryParse<TriggerKind>(kind, ignoreCase: true, out var parsed)
        ? Triggers.NextOccurrence(parsed, expression, timezone, intervalSeconds, fireAt, after)
        : null;

static bool TryReadSchedulePatch(JsonElement body, out SchedulePatch patch, out string error)
{
    patch = default;
    error = "";

    if (body.ValueKind is not JsonValueKind.Object)
    {
        error = "PATCH body must be a JSON object.";
        return false;
    }

    UpdateSchedule? typed;

    try
    {
        typed = JsonSerializer.Deserialize<UpdateSchedule>(
            body.GetRawText(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }
    catch (JsonException exception)
    {
        error = $"Invalid schedule patch payload: {exception.Message}";
        return false;
    }

    typed ??= new UpdateSchedule();

    patch = new SchedulePatch(
        body.TryGetProperty("name", out _), typed.Name,
        body.TryGetProperty("container", out _), typed.Container,
        body.TryGetProperty("instruction", out _), typed.Instruction,
        body.TryGetProperty("kind", out _), typed.Kind,
        body.TryGetProperty("expression", out _), typed.Expression,
        body.TryGetProperty("timezone", out _), typed.Timezone,
        body.TryGetProperty("intervalSeconds", out _), typed.IntervalSeconds,
        body.TryGetProperty("fireAt", out _), typed.FireAt,
        body.TryGetProperty("idleOnly", out _), typed.IdleOnly,
        body.TryGetProperty("enabled", out _), typed.Enabled,
        body.TryGetProperty("nextDueAt", out _), typed.NextDueAt,
        body.TryGetProperty("eventType", out _), typed.EventType,
        body.TryGetProperty("filter", out _), typed.Filter,
        body.TryGetProperty("watchRoot", out _), typed.WatchRoot,
        body.TryGetProperty("watchPath", out _), typed.WatchPath,
        body.TryGetProperty("watchGlob", out _), typed.WatchGlob,
        body.TryGetProperty("pollSeconds", out _), typed.PollSeconds,
        body.TryGetProperty("quietSeconds", out _), typed.QuietSeconds,
        body.TryGetProperty("minIntervalSeconds", out _), typed.MinIntervalSeconds,
        body.TryGetProperty("wakeManager", out _), typed.WakeManager,
        body.TryGetProperty("dailyTokenCap", out _), typed.DailyTokenCap,
        body.TryGetProperty("outcomeId", out _), string.IsNullOrWhiteSpace(typed.OutcomeId) ? null : typed.OutcomeId.Trim());

    return true;
}

static bool ApplySchedulePatch(
    TriggerRow existing,
    SchedulePatch patch,
    out TriggerRow updated,
    out string error)
{
    error = "";

    var name = existing.Name;
    var container = existing.Container;
    var instruction = existing.Instruction;
    var kind = existing.Kind;
    var expression = existing.Expression;
    var timezone = existing.Timezone;
    var intervalSeconds = existing.IntervalSeconds;
    var fireAt = existing.FireAt;
    var idleOnly = existing.IdleOnly;
    var enabled = existing.Enabled;
    var nextDueAt = existing.NextDueAt;
    var eventType = existing.EventType;
    var filter = existing.Filter;

    if (patch.HasName)
    {
        name = (patch.Name ?? "").Trim();
        if (name.Length == 0)
        {
            updated = existing;
            error = "A schedule needs a name.";
            return false;
        }
    }

    if (patch.HasContainer)
    {
        container = (patch.Container ?? "").Trim();
        if (container.Length == 0)
        {
            updated = existing;
            error = "A schedule needs a member name.";
            return false;
        }
    }

    if (patch.HasInstruction)
    {
        instruction = (patch.Instruction ?? "").Trim();
        if (instruction.Length == 0)
        {
            updated = existing;
            error = "A schedule needs an instruction.";
            return false;
        }
    }

    if (patch.HasKind)
    {
        kind = (patch.Kind ?? "").Trim();
        if (kind.Length == 0)
        {
            updated = existing;
            error = "A schedule needs a kind.";
            return false;
        }
    }

    if (patch.HasExpression) expression = string.IsNullOrWhiteSpace(patch.Expression) ? null : patch.Expression.Trim();
    if (patch.HasTimezone) timezone = string.IsNullOrWhiteSpace(patch.Timezone) ? null : patch.Timezone.Trim();
    if (patch.HasIntervalSeconds) intervalSeconds = patch.IntervalSeconds;
    if (patch.HasFireAt) fireAt = patch.FireAt;
    if (patch.HasEventType) eventType = string.IsNullOrWhiteSpace(patch.EventType) ? null : patch.EventType.Trim();
    if (patch.HasFilter) filter = string.IsNullOrWhiteSpace(patch.Filter) ? null : patch.Filter.Trim();

    if (patch.HasIdleOnly)
    {
        if (patch.IdleOnly is null)
        {
            updated = existing;
            error = "idleOnly cannot be null.";
            return false;
        }

        idleOnly = patch.IdleOnly.Value;
    }

    if (patch.HasEnabled)
    {
        if (patch.Enabled is null)
        {
            updated = existing;
            error = "enabled cannot be null.";
            return false;
        }

        enabled = patch.Enabled.Value;
    }

    if (patch.HasNextDueAt) nextDueAt = patch.NextDueAt;

    var wakeManager = existing.WakeManager;
    if (patch.HasWakeManager)
    {
        if (WakeManagerPolicy.Parse(patch.WakeManager) is not { } chosen)
        {
            updated = existing;
            error = TriggerCost.WakeManagerRefusal;
            return false;
        }

        wakeManager = chosen;
    }

    var dailyTokenCap = existing.DailyTokenCap;
    if (patch.HasDailyTokenCap)
    {
        if (patch.DailyTokenCap is < 1)
        {
            updated = existing;
            error = TriggerCost.DailyTokenCapRefusal;
            return false;
        }

        dailyTokenCap = patch.DailyTokenCap;
    }

    // RE-ARMED WHEN THE SHAPE MOVED, and armed when it was never armed at all.
    //
    // Two cases, and both would otherwise be dead ends. Changing an interval from a day to ten
    // minutes would leave the row pointing at tomorrow, so the edit would appear to work and change
    // nothing until the next fire. And re-enabling a row whose due time had been cleared - which is
    // what `container.once` leaves behind - would put it back in a state that can never fire, so
    // the toggle would do nothing at all.
    //
    // An explicit `nextDueAt` in the patch still wins, on the same contract creation uses: it means
    // "fire next at this time". Only an absent one is recomputed, so nothing here overrides a
    // caller that said when it wanted the next run.
    if (!patch.HasNextDueAt)
    {
        var shapeMoved = patch.HasKind
            || patch.HasExpression
            || patch.HasTimezone
            || patch.HasIntervalSeconds
            || patch.HasFireAt;

        if (shapeMoved || (enabled && nextDueAt is null))
        {
            nextDueAt = FirstOccurrence(
                kind, expression, timezone, intervalSeconds, fireAt, DateTimeOffset.UtcNow);
        }
    }

    updated = existing with
    {
        Name = name,
        Container = container,
        Instruction = instruction,
        Kind = kind,
        Expression = expression,
        Timezone = timezone,
        IntervalSeconds = intervalSeconds,
        FireAt = fireAt,
        IdleOnly = idleOnly,
        Enabled = enabled,
        NextDueAt = nextDueAt,
        EventType = eventType,
        Filter = filter,
        WatchRoot = patch.HasWatchRoot ? patch.WatchRoot?.Trim() : existing.WatchRoot,
        WatchPath = patch.HasWatchPath ? patch.WatchPath ?? "" : existing.WatchPath,
        WatchGlob = patch.HasWatchGlob ? FolderWatch.Glob(patch.WatchGlob) : existing.WatchGlob,

        // Null on a PATCH puts a folder trigger's interval back to its default, applied at the route.
        PollSeconds = patch.HasPollSeconds ? patch.PollSeconds : existing.PollSeconds,
        QuietSeconds = patch.HasQuietSeconds ? patch.QuietSeconds : existing.QuietSeconds,
        MinIntervalSeconds = patch.HasMinIntervalSeconds ? patch.MinIntervalSeconds : existing.MinIntervalSeconds,
        WakeManager = wakeManager,
        DailyTokenCap = dailyTokenCap,
        OutcomeId = patch.HasOutcomeId ? patch.OutcomeId : existing.OutcomeId,
    };

    return true;
}

internal readonly record struct SchedulePatch(
    bool HasName, string? Name,
    bool HasContainer, string? Container,
    bool HasInstruction, string? Instruction,
    bool HasKind, string? Kind,
    bool HasExpression, string? Expression,
    bool HasTimezone, string? Timezone,
    bool HasIntervalSeconds, int? IntervalSeconds,
    bool HasFireAt, DateTimeOffset? FireAt,
    bool HasIdleOnly, bool? IdleOnly,
    bool HasEnabled, bool? Enabled,
    bool HasNextDueAt, DateTimeOffset? NextDueAt,
    bool HasEventType, string? EventType,
    bool HasFilter, string? Filter,
    bool HasWatchRoot = false, string? WatchRoot = null,
    bool HasWatchPath = false, string? WatchPath = null,
    bool HasWatchGlob = false, string? WatchGlob = null,
    bool HasPollSeconds = false, int? PollSeconds = null,
    bool HasQuietSeconds = false, int? QuietSeconds = null,
    bool HasMinIntervalSeconds = false, int? MinIntervalSeconds = null,
    bool HasWakeManager = false, string? WakeManager = null,
    bool HasDailyTokenCap = false, long? DailyTokenCap = null,
    bool HasOutcomeId = false, string? OutcomeId = null);

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

/// <summary>
/// One store of the Concierge or non-Manager Container refusal, so duplicating the sentence is
/// impossible.
/// </summary>
internal static class TellRefusals
{
    /// <summary>
    /// Refused when a Concierge or Container tells a non-Manager member on a team that has a Manager.
    /// Names the Manager so the caller knows what to do instead.
    ///
    /// COMPOSED FROM <see cref="TeamRegistry.DefaultManagerName"/> RATHER THAN SPELLING IT. The
    /// check three lines away reads that constant; a literal here is a second store of the same
    /// fact, and the two are adjacent enough that a rename would move one and leave the message
    /// naming a container the reader cannot find. `static readonly` rather than `const` for the
    /// same reason - a const cannot be composed, and that is not a good enough argument for
    /// hard-coding a name.
    /// </summary>
    public static readonly string SkipsManager =
        $"This team is coordinated by '{TeamRegistry.DefaultManagerName}'. "
        + "Send the work there and let it dispatch its own members.";
}

/// <summary>
/// Parameter prose that repeats across routes, written once.
///
/// A constant rather than a copy per route because the identifier-versus-label distinction is the
/// single easiest thing to get backwards in this API, and a reader who meets two slightly different
/// explanations of `{team}` reasonably concludes the two routes want different things.
/// </summary>
internal static class Describe
{
    public const string Team =
        "The team's IDENTIFIER - the `name` field from `GET /api/overview`, never the `label` a "
        + "person reads. Matched case-insensitively. Addressing a team by its label reaches a team "
        + "that does not exist, and the failure is quiet.";

    /// <summary>
    /// Appended to every documents route rather than set once on the group, and that is not a
    /// stylistic choice: a route's own <c>WithDescription</c> REPLACES the group's, so the three
    /// routes here that have something of their own to say would have silently dropped this - the
    /// half of the explanation covering where a path may point and what happens when it does not.
    /// </summary>
    public const string Documents =
        "\n\nPaths are relative to the team's documents folder at `<dataRoot>/documents/<team>`, "
        + "claimed with the team and NOT removed with it. Every path arriving from outside is "
        + "RESOLVED and then compared against that folder rather than searched for `..`, so one "
        + "that climbs out is refused with 400 whatever spelling it arrives in.\n\n"
        + "**A team's documents outlive the team.** Deleting a team keeps them - its result says "
        + "where - so the READ routes here (`GET` and `GET /content`) also serve a folder whose "
        + "team no longer exists, named by the folder on disk. `GET /api/documents` lists those "
        + "folders. The WRITE routes answer 404 for a team that is gone: a dead team's documents "
        + "are a record, and a record is not edited.\n\n"
        + "Who may reach a folder is decided by the team in the path exactly as it is everywhere "
        + "else, so a document a team wrote stays readable to that team - and a folder whose team "
        + "no longer exists is absent from everybody's set, which leaves it reachable by a tenant "
        + "admin alone. 404 for a missing file.";
}

/// <summary>
/// What a team may spend on ONE workflow, in tokens, IN and OUT together.
///
/// THREE STATES: null and 0 are different answers. Null means the team has CHOSEN NOTHING and inherits the instance `WorkflowSpendLimit` - which must still
/// apply to such a team - leaving 0 as the only spelling of an explicit "unlimited". A negative
/// value is refused with 400 at the route.
/// </summary>
internal sealed record TeamBudget(
    [property: Description(
        "Tokens this team may spend on ONE workflow, input and output together. **Null means the "
        + "team has chosen nothing** and the instance figure applies; **0 means unlimited**; a "
        + "positive value is stored exactly as typed, including above the instance figure.")]
    long? BudgetTokens);

/// <summary>The object form of <c>PUT /api/teams/{team}/repos</c>: the list and the choices for URLs a
/// check refused.</summary>
internal sealed record SetTeamRepos(
    IReadOnlyList<string>? Repos,
    IReadOnlyDictionary<string, string>? RepoChoices);

internal sealed record CreateTeam(
    [property: Description(
        "The team's name as a PERSON would write it - free text, spaces and accents included. The "
        + "identifier every other route addresses is derived from this and is never asked for.")]
    string? Name,
    [property: Description(
        "The agent this team's Manager runs. REQUIRED - there is no default: nothing in code "
        + "names an Agent, because a name in code is a reference no rename can move, and a "
        + "create that does not name all three Agents is refused.")]
    string? Agent,
    [property: Description(
        "Optional instructions from the person for this team. Appended after the built-in role "
        + "prompt for the team's Manager and members, under the heading \"Instructions from the "
        + "person for this team\"; they never replace it. Omitted or blank means nothing is "
        + "appended. The prompt itself is chosen by role and cannot be set.")]
    string? AdditionalInstructions = null,
    [property: Description(
        "Single-entry shape of `memberAgents`, kept for callers still sending one string. "
        + "When `memberAgents` is present, this is ignored.")]
    string? MemberAgent = null,
    [property: Description(
        "Which Agents this team's new members may run, in tie-break order. Required for new teams: "
        + "at least one HEADLESS Agent. Empty is refused.")]
    IReadOnlyList<string>? MemberAgents = null,
    [property: Description(
        "The folder that will CONTAIN this team's `teams/<id>/` directory. Omitted, the instance "
        + "root is used and nothing is stored, so the team follows the data root if it ever moves. "
        + "Checked in this order: it must be a location this host allows - one of "
        + "`FileBrowser:Roots` in `appsettings.json`, or inside the instance's own folder, which is "
        + "always permitted - then it must already exist, then it must be writable, and it must not "
        + "already contain a folder for this team. The allowlist is checked FIRST and deliberately: "
        + "it is the only one of the four whose recovery a caller cannot guess, and checking it "
        + "later would mean probing a path the host was about to refuse.")]
    string? Root = null,
    [property: Description(
        "The ordered Git repository URLs for this team. Each must be an absolute http or https "
        + "URL whose final path segment is safe and unique as a folder name, or `local:<name>` for one "
        + "of the instance's local repositories. Omitted means none.")]
    IReadOnlyList<string>? Repos = null,

    [property: Description(
        "Tokens this team may spend on ONE workflow, input and output together - a budget for one "
        + "workflow, not a total the team may spend across everything it runs. Null means the team "
        + "has chosen nothing and the instance figure applies; 0 means unlimited; a positive value "
        + "is stored exactly as typed, including above the instance figure. A negative value is "
        + "refused (400) BEFORE anything is created. Changeable later on the team's settings "
        + "screen.")]
    long? BudgetTokens = null,

    [property: Description(
        "Whether a team left with no repository gets a local repository named after it, attached as "
        + "`local:<team id>`. Omitted means true. Ignored when `repos` names any repository.")]
    bool? LocalRepository = null,

    [property: Description(
        "What to do with a URL in `repos` that `git ls-remote` could not read, keyed by that URL: "
        + "`use-local`, `create-on-github` (a person only; github.com only) or `attach-anyway` (a person "
        + "only; a network failure only). A URL with no choice that cannot be read refuses the create.")]
    IReadOnlyDictionary<string, string>? RepoChoices = null,

    [property: Description(
        "Each contributed repository's upstream, keyed by its URL in `repos`: that repository "
        + "is in contributor mode, its URL is the fork (origin), and its clone is made with an "
        + "`upstream` remote. A blank value, or a repository not named, is owned. Checked BEFORE "
        + "anything is created (400). Changeable later on the team's settings screen.")]
    IReadOnlyDictionary<string, string>? Upstreams = null);

/// <summary>
/// What a person typed as the clone's name, and NOTHING ELSE. The identifier is derived from it
/// here, on the server, where <c>DeriveName</c> lives.
///
/// There is deliberately no field for anything else a team carries. Every one of them comes from
/// the SOURCE - a body that could override one would make this two operations wearing one name, and
/// the first field somebody added would be `root`, which is the one value a browser is never sent.
/// </summary>
internal sealed record CloneTeam(
    [property: Description(
        "The new team's name as a PERSON would write it. The identifier is derived from it, and a "
        + "name deriving to one that is taken is refused with 409 naming the existing team.")]
    string? Name);

/// <summary>
/// One field, and it is the display name. No Name: this API has no way to express changing a team's
/// identifier, which is the property that keeps the documents folder and every
/// container id from needing to move together. See TeamSummary.
/// </summary>
/// <summary>A team's additional instructions.</summary>
internal sealed record SetAdditionalInstructions(
    [property: Description(
        "The instructions, as free text. Null or blank clears them.")]
    string? AdditionalInstructions);

internal sealed record SetMemberAgent(
    [property: Description(
        "The ordered allowlist of Agents this team's new members may run. Empty is refused.")]
    IReadOnlyList<string>? Agents,
    [property: Description(
        "Single-entry shape of `agents` for callers still sending one Agent.")]
    string? Agent);

internal sealed record CreateSchedule(
    [property: Description("A human-readable name for this schedule. Required.")]
    string? Name,
    [property: Description(
        "Which member receives the scheduled instruction. Omit for the team's Manager.")]
    string? Container,
    [property: Description("The instruction text to append when this schedule fires. Required.")]
    string? Instruction,
    [property: Description("The kind: `cron`, `every`, `once`, `event` or `folderChange`. Required.")]
    string? Kind,
    [property: Description("Cron expression when `kind` is `cron`.")]
    string? Expression = null,
    [property: Description("IANA timezone when `kind` is `cron`.")]
    string? Timezone = null,
    [property: Description("Interval in seconds when `kind` is `every`. Minimum 1.")]
    int? IntervalSeconds = null,
    [property: Description("The one-shot instant when `kind` is `once`.")]
    DateTimeOffset? FireAt = null,
    [property: Description(
        "When true, this schedule is skipped while the target member is busy. Defaults to true.")]
    bool? IdleOnly = null,
    [property: Description("Whether this schedule is currently enabled. Defaults to true.")]
    bool? Enabled = null,
    [property: Description(
        "The next due instant this row should be swept at. Null means not currently due.")]
    DateTimeOffset? NextDueAt = null,
    [property: Description(
        "Which event type fires this trigger, required when `kind` is `event` and checked against "
        + "GET /api/events - naming one this platform does not publish is refused.")]
    string? EventType = null,
    [property: Description(
        "A filter evaluated before the wake is enqueued. Null means every message of that type. "
        + "`field op value`, where op is `eq` or `contains`. Naming a field the event type does not "
        + "declare is refused.")]
    string? Filter = null,
    [property: Description(WatchDescriptions.Root)]
    string? WatchRoot = null,
    [property: Description(WatchDescriptions.Path)]
    string? WatchPath = null,
    [property: Description(WatchDescriptions.Glob)]
    string? WatchGlob = null,
    [property: Description(WatchDescriptions.Poll)]
    int? PollSeconds = null,
    [property: Description(WatchDescriptions.Quiet)]
    int? QuietSeconds = null,
    [property: Description(WatchDescriptions.MinInterval)]
    int? MinIntervalSeconds = null,
    [property: Description(WatchDescriptions.WakeManager + " Defaults to `onHandbackOrFailure`.")]
    string? WakeManager = null,
    [property: Description(WatchDescriptions.DailyTokenCap + " Omit or null for no cap.")]
    long? DailyTokenCap = null,
    [property: Description(WatchDescriptions.Outcome + " Omit or null for none.")]
    string? OutcomeId = null);

/// <summary>The folder-trigger field descriptions, shared by the create, update and test-folder bodies.</summary>
internal static class WatchDescriptions
{
    public const string Outcome =
        "The outcome this trigger's fires serve: an active or proposed outcome's id or exact name, "
        + "stored as its id. A fire that roots a workflow links it to this outcome, attributed to the "
        + "person who configured the trigger.";

    public const string Root =
        "`kind: folderChange` only. `documents` for the team's documents, or `root:<name>` for a "
        + "file-browser root configured with allowWatch.";

    public const string Path =
        "`kind: folderChange` only. The folder to watch, relative to `watchRoot`. Empty is the root.";

    public const string Glob =
        "`kind: folderChange` only. Optional wildcard: `*.pdf` matches file names, a pattern with `/` "
        + "matches the path inside the folder, `**` crosses folders.";

    public const string Poll = "`kind: folderChange` only. Seconds between listings. Default 60, minimum 15.";

    public const string Quiet =
        "`kind: folderChange` only. Seconds a change must hold still before it fires. Default 30.";

    public const string MinInterval =
        "`kind: folderChange` only. The shortest gap in seconds between two fires. Default 60.";

    public const string WakeManager =
        "What a run this trigger started does to the Manager when it ends: `always` (every finished "
        + "run wakes it), `onHandbackOrFailure` (only a hand-back or a failure), or `never` (not even a "
        + "failure; the run is still recorded and shown). Work the Manager sent, and a person's tell, "
        + "are not affected.";

    public const string DailyTokenCap =
        "Billable tokens this trigger's runs may spend per day in its timezone (UTC when it has "
        + "none), the Manager runs they woke included; measured usage only. A fire once it is reached "
        + "is skipped: the first skip of the day writes `schedule.skipped` (reason `daily token cap "
        + "reached`, and for a schedule `; resumes at <time>`) and a tenant row, and later ones are "
        + "only counted. A schedule sleeps until its first occurrence of the next day; raising the "
        + "cap above today's spend or clearing it wakes it. A whole number of at least 1.";
}

/// <summary>"Test this folder".</summary>
internal sealed record TestFolder(
    [property: Description(WatchDescriptions.Root)] string? WatchRoot,
    [property: Description(WatchDescriptions.Path)] string? WatchPath,
    [property: Description(WatchDescriptions.Glob)] string? WatchGlob = null);

/// <summary>The cron builder's and the start field's preview: a schedule shape, nothing stored.</summary>
internal sealed record PreviewSchedule(
    [property: Description("`cron`, `every` or `once`.")] string? Kind,
    [property: Description("A cron expression in the seconds format, for `cron`.")] string? Expression = null,
    [property: Description("An IANA timezone for `cron`; UTC when absent.")] string? Timezone = null,
    [property: Description("Seconds between firings, for `every`.")] int? IntervalSeconds = null,
    [property: Description("For `cron` and `every`, the start before which it never fires; for `once`, when it fires.")] DateTimeOffset? FireAt = null,
    [property: Description("How many firings to list, 1 to 20; 5 when absent.")] int? Count = null);

internal sealed record UpdateSchedule(
    [property: Description("A new human-readable name for this schedule.")]
    string? Name = null,
    [property: Description("A new target member. Use null or blank to leave unchanged.")]
    string? Container = null,
    [property: Description("New instruction text.")]
    string? Instruction = null,
    [property: Description("A new kind: `cron`, `every`, `once`, `event` or `folderChange`.")]
    string? Kind = null,
    [property: Description("New cron expression. Null or blank clears it.")]
    string? Expression = null,
    [property: Description("New timezone. Null or blank clears it.")]
    string? Timezone = null,
    [property: Description("New interval seconds. Null clears it.")]
    int? IntervalSeconds = null,
    [property: Description("New one-shot instant. Null clears it.")]
    DateTimeOffset? FireAt = null,
    [property: Description("When supplied, sets idle-only behavior.")]
    bool? IdleOnly = null,
    [property: Description("When supplied, sets enabled/disabled.")]
    bool? Enabled = null,
    [property: Description("When supplied, sets next-due. Null clears it.")]
    DateTimeOffset? NextDueAt = null,
    [property: Description(
        "New event type. Null or blank clears it. Checked against GET /api/events when the "
        + "resulting kind is `event`.")]
    string? EventType = null,
    [property: Description("New filter. Null or blank clears it.")]
    string? Filter = null,
    [property: Description(WatchDescriptions.Root)]
    string? WatchRoot = null,
    [property: Description(WatchDescriptions.Path)]
    string? WatchPath = null,
    [property: Description(WatchDescriptions.Glob + " Null or blank clears it.")]
    string? WatchGlob = null,
    [property: Description(WatchDescriptions.Poll)]
    int? PollSeconds = null,
    [property: Description(WatchDescriptions.Quiet)]
    int? QuietSeconds = null,
    [property: Description(WatchDescriptions.MinInterval)]
    int? MinIntervalSeconds = null,
    [property: Description(WatchDescriptions.WakeManager + " May not be null.")]
    string? WakeManager = null,
    [property: Description(WatchDescriptions.DailyTokenCap + " Null clears the cap.")]
    long? DailyTokenCap = null,
    [property: Description(WatchDescriptions.Outcome + " Null clears it.")]
    string? OutcomeId = null);

internal sealed record CreateContainer(
    [property: Description(
        "The member's name as a PERSON would write it. Its identifier is derived from this and "
        + "never asked for, the same as a team's.")]
    string? Name,
    [property: Description(
        "The Agent this member runs. Omit to have the team resolve one from its allowlist.")]
    string? Agent,
    [property: Description(
        "The work tag to hire this member for. When omitted, the team's first allowed Agent is used.")]
    string? For,
    [property: Description(
        "The member's own role line - who it is, not what it is doing now. Appended after the "
        + "built-in Member prompt, never in place of it.")]
    string? SystemPrompt,
    [property: Description(
        "The message types that WAKE this member. The container holds the subscription, never the "
        + "agent - a headless agent runs and exits and cannot hold one. Beware global types: two "
        + "managers subscribed to `agentContainer.completed` wake each other without end.")]
    string[]? Subscribes,
    [property: Description(
        "A PLUGIN member's configuration: field name to value, checked against the plugin's "
        + "manifest (`GET /api/plugins`). Refused for an Agent.")]
    Dictionary<string, JsonElement>? Config = null,
    [property: Description(
        "A PLUGIN member's secret bindings: each secret its manifest names, to a LOGICAL KEY set on "
        + "the Host with `secret set` - never a value. A required secret whose key is not set is "
        + "refused. Refused for an Agent.")]
    Dictionary<string, string>? Secrets = null,
    [property: Description(
        "A PLUGIN member's connection bindings: each connection slot its manifest declares, to a "
        + "CONNECTION ID (Admin > Connections) - never a token. The connection must suit the slot's "
        + "providers and hold its scopes. A Manager or a Concierge may name only a connection a person "
        + "has already bound on a member of this team. Refused for an Agent.")]
    Dictionary<string, string>? Connections = null);

/// <summary>A repository's contributor settings. See the route.</summary>
internal sealed record SetRepoContributor(
    [property: Description("The original project's URL. Set puts the repository in contributor mode; null or blank makes it owned.")]
    string? UpstreamUrl,
    [property: Description("The account the fork belongs to. Null or blank reads it from the repository URL.")]
    string? ForkOwner,
    [property: Description("Sign off commits (DCO): install the commit-msg hook that adds Signed-off-by with the instance's git identity.")]
    bool DcoSignOff,
    [property: Description("A person's note that the upstream project's CLA is signed. A record only; the platform signs nothing.")]
    string? ClaSignedNote);

/// <summary>A person's choice of a repository's default branch.</summary>
internal sealed record SetRepoDefaultBranch(
    [property: Description(
        "The branch this repository's Git actions treat as main. Null or blank clears the person's "
        + "choice, so the branch origin's HEAD named on the last clone or Fetch is used, or not known.")]
    string? Branch);

/// <summary>
/// `Agent` repoints the member. Headless presets only, and it takes effect on the next wake: the
/// runner resolves a preset by name at launch rather than from anything captured at create.
/// </summary>
internal sealed record SetConcierge(
    [property: Description(
        "The Agent preset this team's Concierge launches. Must be an INTERACTIVE preset. Blank "
        + "clears the choice, so the Concierge runs its default agent; OMIT it to leave it as it is.")]
    string? Agent);

/// <summary>
/// A person's current team, or nothing. NULL and empty mean the SAME thing here - "no team is
/// active" - which is the one place in this codebase where collapsing them is right: there is no
/// third state to tell apart, unlike a stored system prompt where absent leaves and empty clears.
/// </summary>
internal sealed record SetCurrentTeam(
    [property: Description(
        "The team identifier to work on, or null/empty for none. Null is a real state rather than a "
        + "misconfiguration: it is what the Teams tab sets, and a tool's refusal for it names how "
        + "to choose rather than reporting a fault.")]
    string? Team);

/// <summary>
/// Who, if anyone, is the PERSON behind a principal - the one question both `/api/me/current-team`
/// routes have to answer before they can do anything.
/// </summary>
internal static class CurrentTeam
{
    /// <summary>
    /// The user id whose current team this principal shares, or null when the principal is not a
    /// person's door at all.
    ///
    /// A CONTAINER FALLS OUT AS NULL rather than being refused by name, and that is deliberate: its
    /// team is half of its `(team, name)` identity and arrives in HARNESS_TEAM, so it has no
    /// person to share a current team with. A future PrincipalKind that gains an owner joins the
    /// second arm automatically, which is the safe direction here - the value is per-person state,
    /// not authority, and every route the caller then uses is still gated on its own terms.
    /// </summary>
    public static string? PersonBehind(Principal principal) =>
        principal.Kind == PrincipalKind.User ? principal.Id : principal.OwnerUserId;

    /// <summary>
    /// Tells every connection this PERSON holds, on every device, that their current team moved.
    ///
    /// Without it the browser follows a tab click and not a change made anywhere else, which leaves
    /// two stores of one fact again - the shape this whole design exists to remove.
    ///
    /// A PERSON GROUP, never a team group. The message is "your current team is now X", and the
    /// teams it moves between are exactly the groups a connection may not be in.
    ///
    /// ContainerSnapshot is NOT widened to carry this. That record rides the hub to every browser
    /// holding a team, and a per-person fact on it would be pushed to people it is not about.
    /// </summary>
    public static Task AnnounceAsync(
        IHubContext<ContainerHub> hub,
        string person,
        string? team,
        string? name,
        CancellationToken ct) =>
        hub.Clients.Group(ContainerHub.PersonGroup(person))
            .SendAsync("currentTeamChanged", new { team, name }, ct);

    /// <summary>
    /// 403 rather than 400: the request is well formed and it is the CALLER that is wrong for it.
    /// </summary>
    public static IResult NotAPersonsDoor { get; } = Results.Content(
        """{"error":"This principal has no current team. A member's team is fixed at its identity."}""",
        "application/json",
        statusCode: StatusCodes.Status403Forbidden);
}

internal sealed record UpdateContainer(
    [property: Description(
        "The member's new display name, as a person would write it. Omit or send blank to leave "
        + "it unchanged.")]
    string? Name,
    [property: Description(
        "The member's own role line, appended after the built-in role prompt. Omit to leave it "
        + "unchanged; blank clears it.")]
    string? SystemPrompt,
    [property: Description(
        "The Agent preset this member runs. Must be a HEADLESS preset - a member is never "
        + "interactive. Omit or send blank to leave it unchanged. Changing it takes effect on the "
        + "member's next wake, with no restart: the catalog entry the runner resolves is updated in "
        + "the same operation.")]
    string? Agent = null);

/// <remarks>
/// There is no `From`. A caller-supplied sender is never validated, so it could name any container
/// on any team and would be evidence of nothing; the source is derived from the authenticated
/// principal. Do not add one - a body field that names a sender is indisputably
/// forgeable, and the activity feed renders it.
/// </remarks>
/// <summary>What a member says about itself, mid-run.</summary>
internal sealed record ReportProgress(
    [property: Description(
        "One short line saying what you are doing right now - \"gathering sources - 3 of 8\". It "
        + "REPLACES whatever you last said rather than adding to it, so write the current state "
        + "rather than a running commentary. Blank is refused: an empty line on a card cannot be "
        + "told apart from having said nothing.")]
    string? Status);

/// <summary>Why a member stopped without finishing.</summary>
internal sealed record ReportBlocked(
    [property: Description(
        "Why you stopped, in one or two sentences - \"the contract files were never written, so "
        + "there is nothing to build against\". It stays on your card until something wakes you "
        + "again, so write what the person reading the board needs in order to unblock you. Blank "
        + "is refused: a card saying only that you stopped sends its reader to the log, which is "
        + "the trip this exists to remove.")]
    string? Reason,
    [property: Description(
        "Optional 1-based item number from this run's batched prompt. When present, marks only "
        + "that item as abandoned; omit it to mark the whole run.")]
    int? Item = null,
    [property: Description(
        "With `item`: defer that item instead of abandoning it. It is delivered again as its own "
        + "next run; `reason` says why it waits. Refused for the only item of a run.")]
    bool? Defer = null);

/// <summary>What question this member needs answered before it can continue.</summary>
internal sealed record ReportNeedsDecision(
    [property: Description(
        "The question that needs an answer before you can continue this step. It stays on your "
        + "card until something wakes you again, so write the actual decision and not a status "
        + "line. Blank is refused.")]
    string? Question);

/// <summary>
/// What a worker finished and is handing back.
///
/// `Delivered`, the same member name `ReportWorkflowCompleted` uses below, because it answers the
/// same question and writes the same payload field - see the catalog entry for
/// `agentContainer.handback`. The two records stay separate because the ROUTES are: one is a
/// worker's hand-back and the other a Manager's declaration, and sharing a body type between them
/// would put one OpenAPI description on two different claims.
/// </summary>
internal sealed record ReportHandback(
    [property: Description(
        "What you finished, in one short sentence. Blank is refused: this wakes your Manager, and "
        + "a wake that does not say what it is about sends the reader to the log.")]
    string? Delivered);

/// <summary>What this workflow delivered.</summary>
internal sealed record ReportWorkflowCompleted(
    [property: Description(
        "What the finished workflow delivered, in one short sentence. Blank is refused: a "
        + "completion with no result says that work ended and not what was delivered.")]
    string? Delivered,
    [property: Description(
        "Required only when cards of this workflow are unfinished (still to do, running, "
        + "interrupted, failed or blocked): why they are being left undone. Recorded on the "
        + "declaration.")]
    string? Dropped = null);

/// <summary>A person's reason for closing a workflow. Optional - see the route's own comment for
/// why a blank one is accepted rather than refused.</summary>
internal sealed record CloseWorkflow(
    [property: Description(
        "Why this workflow is being closed, in one short sentence. Optional: the row still records "
        + "who closed it and when regardless.")]
    string? Reason);

internal sealed record WorkflowWaitReply(
    long Workflow,
    string Outcome,
    string? MessageType,
    long? Seq,
    int TimeoutSeconds,
    string? TimeoutVerdict,
    DateTimeOffset? IdleSince)
{
    public static WorkflowWaitReply From(long workflow, Message terminal, int timeoutSeconds) =>
        terminal.Type switch
        {
            MessageTypes.WorkflowCompleted => new(
                workflow, WaitOutcome.Completed, terminal.Type, terminal.Seq, timeoutSeconds, null, null),
            MessageTypes.WorkflowClosed => new(
                workflow, WaitOutcome.Closed, terminal.Type, terminal.Seq, timeoutSeconds, null, null),
            MessageTypes.Blocked => new(
                workflow, WaitOutcome.Blocked, terminal.Type, terminal.Seq, timeoutSeconds, null, null),
            MessageTypes.NeedsDecision => new(
                workflow, WaitOutcome.NeedsDecision, terminal.Type, terminal.Seq, timeoutSeconds, null, null),
            MessageTypes.Failed => new(
                workflow, WaitOutcome.Failed, terminal.Type, terminal.Seq, timeoutSeconds, null, null),
            _ => throw new InvalidOperationException(
                $"Message type '{terminal.Type}' is not a wait terminal."),
        };

    public static WorkflowWaitReply Timeout(
        long workflow,
        int timeoutSeconds,
        string timeoutVerdict,
        DateTimeOffset? idleSince = null) =>
        new(workflow, WaitOutcome.Timeout, null, null, timeoutSeconds, timeoutVerdict, idleSince);
}

internal static class WaitOutcome
{
    public const string Completed = "completed";

    /// <summary>A person ended the workflow rather than a manager declaring it delivered - see
    /// the close route's own comment for why this is a distinct outcome from `Completed`.</summary>
    public const string Closed = "closed";
    public const string Blocked = "blocked";
    public const string NeedsDecision = "needs-decision";
    public const string Failed = "failed";
    public const string Timeout = "timeout";
}

internal static class TimeoutVerdict
{
    public const string Working = "working";
    public const string Quiet = "quiet";
}

internal static class TeamBusyState
{
    public static async Task<IReadOnlyList<string>> DescribeAsync(
        string team,
        ContainerHost host,
        IPendingDeliveries pending,
        ContainerId? excluded,
        CancellationToken ct)
    {
        var busy = new List<string>();

        foreach (var snapshot in host.Snapshots().Where(s =>
                     string.Equals(s.Team, team, StringComparison.OrdinalIgnoreCase)))
        {
            var member = new ContainerId(snapshot.Team, snapshot.Id);

            // The caller's own RUNNING state is excluded, and only that field. Its queue still
            // counts: accepted work not yet started means the team is not finished.
            if ((excluded is null || !member.Equals(excluded)) && snapshot.State == ContainerState.Running)
            {
                busy.Add($"{snapshot.Name} is Running");
            }

            if (snapshot.QueueDepth > 0)
            {
                busy.Add($"{snapshot.Name} has {snapshot.QueueDepth} queued");
            }
        }

        var excludedSubscriber = excluded?.ToString();
        var pendingRows = (await pending.ForTeamAsync(team, ct))
            // The excluded STARTED row is a durable in-flight marker and is ignored only for the
            // workflow-complete caller. Unstarted rows still count because accepted work remains.
            .Where(row => !(excludedSubscriber is not null
                            && row.Started
                            && string.Equals(
                                row.Subscriber,
                                excludedSubscriber,
                                StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        foreach (var group in pendingRows.GroupBy(row => row.Subscriber, StringComparer.OrdinalIgnoreCase))
        {
            var subscriber = group.Key;
            var slash = subscriber.LastIndexOf('/');
            var who = slash >= 0 && slash < subscriber.Length - 1 ? subscriber[(slash + 1)..] : subscriber;
            var count = group.Count();
            busy.Add($"{who} has {count} pending deliver{(count == 1 ? "y" : "ies")}");
        }

        return busy;
    }
}

internal sealed record Tell(
    [property: Description("What to tell the member. Empty or whitespace is refused.")]
    string? Instruction,

    [property: Description(
        "A short line to head the card this instruction becomes, at most 80 characters. Omitted, "
        + "the subject is taken from the instruction's own first line - so this is for when that "
        + "line is not a title. It is a DISPLAY convenience: the member is handed the whole "
        + "instruction either way.")]
    string? Subject = null,

    [property: Description(
        "The seq of the message this instruction is ANSWERING, so it joins that workflow instead "
        + "of starting a new one. The `tell` tool sends it from `HARNESS_CAUSATION`, which the platform "
        + "sets per invocation; a caller with no waking message - a person, a console - omits it. "
        + "A string rather than a number so a malformed value is refused with a message naming it, "
        + "rather than rejected by the JSON binder before any of this code runs.")]
    string? Causation = null,

    [property: Description(
        "The planned card this instruction CLAIMS. The card gains this member and moves out of "
        + "Todo; no second card is minted. Absent - which is every ordinary instruction - behaves "
        + "exactly as it always has, which is what keeps a team that never touches the backlog "
        + "unchanged and stops conversational instructions each becoming a card of their own.")]
    string? Card = null,

    [property: Description(
        "The outcome the NEW workflow this instruction roots serves: an active or proposed "
        + "outcome's id, or its exact name. Only on an instruction with no causation; one that "
        + "joins a workflow is refused, and that workflow's outcome is set with the `outcome` tool.")]
    string? Outcome = null);

internal sealed record NewFolder(
    [property: Description(
        "The folder to create, relative to the team's documents root. Resolved against that root "
        + "before anything touches disk, so a path climbing out of it is refused.")]
    string? Path);

/// <summary>
/// Carries nothing inbound except which groups a connection belongs to. Instructions still go
/// through the API, so there is one way IN; this is only about which of the API's own outputs a
/// given socket is allowed to overhear.
/// </summary>
internal sealed class ContainerHub(
    TeamAccess access,
    DiagnosticsRecorder? diagnostics = null,
    TransportWatch? watch = null) : Hub
{
    /// <summary>
    /// A CONNECTION ARRIVING, recorded only when it is a RECONNECT.
    ///
    /// Every socket this product opens goes through here, so recording each one would put a row on
    /// the instance's busiest store for every tab anybody opens. A reconnect is the interesting
    /// half: it means a connection dropped and the client came back, which is the shape of a
    /// reconnect storm that a console can neither recover from nor describe.
    ///
    /// The DROP itself is always recorded, below. This pairs with it.
    /// </summary>
    public override async Task OnConnectedAsync()
    {
        await base.OnConnectedAsync();

        if (diagnostics is null || watch is null) return;
        if (PrincipalClaims.From(Context.User!) is not { } principal) return;
        if (!watch.WasRecentlyDropped(principal.Id)) return;

        await diagnostics.WriteAsync(
            DiagnosticSeverity.Warning,
            DiagnosticKinds.TransportReconnecting,
            DiagnosticSources.Transport,
            message: principal.Id,
            detail: new { principal = principal.Id, kind = principal.Kind.ToString() },
            ct: Context.ConnectionAborted);
    }

    /// <summary>
    /// A CONNECTION DROPPING. Transport drops are otherwise invisible to the platform, and this is
    /// the only place the server learns of one.
    ///
    /// <para>
    /// AN EXCEPTION MAKES IT AN ERROR AND ITS ABSENCE DOES NOT. A null exception is a client that
    /// said goodbye - a closed tab, a navigation - and is ordinary; a non-null one is a socket that
    /// broke, which is the case worth finding in a filter. Both are recorded, because the RATE of
    /// ordinary disconnects is itself the signal when a page is reloading in a loop.
    /// </para>
    ///
    /// <para>
    /// THE ROW NAMES THE PRINCIPAL, NEVER THE CONNECTION ID. A connection id is meaningless an hour
    /// later and a principal id is the thing a reader can join to a person or a container.
    /// </para>
    /// </summary>
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await base.OnDisconnectedAsync(exception);

        if (diagnostics is null) return;
        if (Context.User is null || PrincipalClaims.From(Context.User) is not { } principal) return;

        watch?.NoteDropped(principal.Id);

        await diagnostics.WriteAsync(
            exception is null ? DiagnosticSeverity.Info : DiagnosticSeverity.Error,
            DiagnosticKinds.TransportDisconnected,
            DiagnosticSources.Transport,
            exceptionType: exception is null ? null : DiagnosticsRecorder.TypeNameOf(exception),
            message: exception?.Message ?? principal.Id,
            detail: new { principal = principal.Id, kind = principal.Kind.ToString() },

            // CancellationToken.None: the connection has already gone, so
            // `Context.ConnectionAborted` is cancelled by definition and passing it would cancel
            // every write on this path - the one row this method exists to produce.
            ct: CancellationToken.None);
    }

    /// <summary>
    /// Joining is an AUTHORITY decision, not plumbing. The hub has no `{team}` route value for
    /// TeamGate to see, so without a check here a client could join any team's group by naming it,
    /// a side channel around every route the gate protects. It looks and feels like transport
    /// wiring, which is exactly why it gets missed.
    ///
    /// It asks the SAME questions as TeamGate, in the same order, and that ordering is the point
    /// rather than a stylistic echo - see the person check below. Asking `MayActOnAsync` alone would
    /// be asymmetric: effective teams is intersected with what TeamRegistry currently holds, so a
    /// team that does not exist is absent from it for EVERYONE, and teams live in memory. After a
    /// restart the routes correctly answer a person 404 while this would refuse them, and the SPA
    /// turns that refusal into "live updates are unavailable for
    /// this team - reload to try again" - a transport-shaped complaint about a team that is merely
    /// gone. `hub.ts` makes it reachable rather than theoretical: an auto-reconnect rejoins with
    /// the team name the board was already holding, BEFORE `onReconnected` refreshes it, so the
    /// stale name is the first thing the reconnected socket asks for.
    /// </summary>
    public async Task JoinTeam(string team)
    {
        var principal = PrincipalClaims.From(Context.User!)
            ?? throw new HubException(TeamGate.NoSuchTeamMessage);

        // TeamGate's rule, and it must stay in step with the gate. A person reaches every team
        // and joins the group for a name that does not exist and simply hears nothing on it - a
        // group is not a capability, and nothing is published to a team that is not there.
        if (principal.Kind == PrincipalKind.User)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, team, Context.ConnectionAborted);
            return;
        }

        if (!await access.MayActOnAsync(principal, team, Context.ConnectionAborted))
        {
            // Byte-identical to a team that does not exist, matching the gate and the filtered read
            // routes: a refusal must not tell an unprivileged caller whether the name they tried is
            // real. Shares TeamGate.NoSuchTeamMessage rather than a hand-typed copy - see that
            // constant's own doc comment for why the two staying identical is structural, not a
            // coincidence to maintain by discipline.
            throw new HubException(TeamGate.NoSuchTeamMessage);
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, team, Context.ConnectionAborted);
    }

    /// <summary>No authority check needed to leave a group - it can only narrow what a connection
    /// receives, never widen it.</summary>
    public async Task LeaveTeam(string team) =>
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, team, Context.ConnectionAborted);

    /// <summary>
    /// The group a person's own pushes go to - every connection they hold, on every device.
    ///
    /// A PERSON, NOT A TEAM, and it cannot be a team group: the message is "your current team is now
    /// X", and the teams it moves between are exactly the groups the connection may not be in yet.
    ///
    /// NO AUTHORITY CHECK IS NEEDED because there is no authority to check: the name comes from the
    /// connection's OWN principal rather than from an argument, so a caller cannot name somebody
    /// else's. That is the whole reason it takes no parameter.
    /// </summary>
    public static string PersonGroup(string userId) => $"user:{userId}";

    public async Task JoinMe()
    {
        if (PrincipalClaims.From(Context.User!) is not { } principal) return;

        await Groups.AddToGroupAsync(
            Context.ConnectionId, PersonGroup(principal.Id), Context.ConnectionAborted);
    }
}

/// <summary>
/// Drives delivery. A plain loop over the subscribers, idling when there was nothing - with one
/// process and one database that is a complete queue, and no broker is being done without.
/// </summary>
internal sealed class PumpService(
    ContainerHost host, PumpHeartbeat heartbeat, ILogger<PumpService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // Every pass, failed or not: /healthz asks whether the loop is turning, and a pass that
            // threw is logged below and followed by the next one.
            heartbeat.Beat();
            var delivered = 0;

            try
            {
                delivered = await host.PumpOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // The pump is the only thing delivering anything. It has to outlive any single
                // failure, or one bad message stops every container permanently.
                logger.LogWarning(ex, "A delivery pass failed.");
            }

            if (delivered == 0)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
}

/// <summary>A path a documents delete left, relative to the documents folder, and why.</summary>
internal sealed record DocumentLeft(string Path, string Reason);
