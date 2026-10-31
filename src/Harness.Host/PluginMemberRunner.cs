using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// A PLUGIN MEMBER: adapts an installed plugin executable to <see cref="IMemberRunner"/>, speaking
/// protocol <see cref="PluginManifest.ProtocolV1"/>.
///
/// <list type="number">
/// <item>Resolve <c>plugin:&lt;id&gt;</c> through <see cref="PluginCatalog"/> ON EVERY RUN, never
/// cached per member, so a rescan or an upgrade takes effect on the next wake.</item>
/// <item>Launch it through <see cref="ChildProcess"/> - the same `setsid`, the same agent user and
/// the same process-group kill as an agent - in the member's workspace, with a MINIMAL environment:
/// no provider key, no platform credential, no agent-vendor variable.</item>
/// <item>Write ONE JSON request document to its stdin and close it: the work batch as data, the
/// member's configuration, and the secrets it binds, resolved at this moment.</item>
/// <item>Read its stdout as JSON LINES. <c>progress</c>, <c>blocked</c>, <c>needsDecision</c> and
/// <c>handback</c> go through <see cref="IMemberReports"/> - the very code the MCP routes run - so a
/// plugin's report is indistinguishable from an agent's. <c>publish</c> appends one of the events its
/// manifest declares (see <see cref="PublishAsync"/>). <c>result</c> is the outcome. A line that
/// is not a record is kept as output text, never an error.</item>
/// </list>
///
/// It holds no platform credential and needs none: everything it reports, the runtime writes.
/// </summary>
public sealed class PluginMemberRunner : IMemberRunner, IRunWorkerClient
{
    private readonly PluginCatalog catalog;
    private readonly IMemberReports reports;
    private readonly IRunWorker worker;
    private readonly RunDirectory directory;
    private readonly AgentLaunchUser? runAs;
    private readonly IPluginMemberSettings? settings;
    private readonly ISecretStore? secrets;
    private readonly IPluginMemberSecrets? memberSecrets;
    private readonly Connections? connections;
    private readonly SiteService? sites;
    private readonly long siteReadBudget;

    /// <summary>
    /// The most a run is handed of the site collections its manifest <c>reads</c>, counted in FRAMED
    /// bytes: each document as it appears inside the <see cref="StartRun"/> frame's stdin string,
    /// escaped by the request's encoder and again by the frame's. Less when the rest of the request
    /// leaves less room in the frame (see <see cref="EnvelopeMarginBytes"/>).
    /// </summary>
    public const long SiteReadBudgetBytes = 8 * 1024 * 1024;

    /// <summary>
    /// What is kept free in the frame for the read's envelopes - one per declared read, each with its
    /// site, collection, counts and sentences - which are always delivered and never counted against
    /// the budget. 32 envelopes at their largest frame at under 20 KiB: names are slugs, which neither
    /// encoder escapes, and <see cref="PluginManifest.MaxReads"/> bounds their number.
    /// </summary>
    public const int EnvelopeMarginBytes = 64 * 1024;

    /// <summary>For tests: told what each run was handed of its declared reads and what it cost.</summary>
    public Action<PluginSiteDelivery>? SiteDataDelivered { get; set; }

    /// <summary>
    /// The Host's: the plugin runs on <paramref name="worker"/>, through the run protocol, and
    /// <paramref name="directory"/> hands each line it prints back here. <paramref name="runAs"/> is
    /// who the worker runs members as, asked only whether it refuses.
    /// </summary>
    public PluginMemberRunner(
        PluginCatalog catalog,
        IMemberReports reports,
        IRunWorker worker,
        RunDirectory directory,
        AgentLaunchUser? runAs = null,
        IPluginMemberSettings? settings = null,
        ISecretStore? secrets = null,
        Connections? connections = null,
        SiteService? sites = null,
        long siteReadBudget = SiteReadBudgetBytes,
        IPluginMemberSecrets? memberSecrets = null)
    {
        this.catalog = catalog;
        this.reports = reports;
        this.worker = worker;
        this.directory = directory;
        this.runAs = runAs;
        this.settings = settings;
        this.secrets = secrets;
        this.memberSecrets = memberSecrets;
        this.connections = connections;
        this.sites = sites;
        this.siteReadBudget = siteReadBudget;
    }

    /// <summary>
    /// Over a worker of its own, in this process, made from <paramref name="heartbeat"/> and
    /// <paramref name="runAs"/>: the plugin crosses the protocol all the same.
    /// </summary>
    public PluginMemberRunner(
        PluginCatalog catalog,
        IMemberReports reports,
        RunHeartbeat heartbeat,
        AgentLaunchUser? runAs = null,
        IPluginMemberSettings? settings = null,
        ISecretStore? secrets = null,
        Connections? connections = null,
        SiteService? sites = null,
        long siteReadBudget = SiteReadBudgetBytes,
        IPluginMemberSecrets? memberSecrets = null)
    {
        var directory = new RunDirectory(reports);
        this.catalog = catalog;
        this.reports = reports;
        this.worker = InProcessWorker.Connect(
            WorkerId.Local,
            events => new WorkerHost(WorkerId.Local, events, new RunLauncher(heartbeat, runAs: runAs), heartbeat),
            directory.HandleAsync).Worker;
        this.directory = directory;
        this.runAs = runAs;
        this.settings = settings;
        this.secrets = secrets;
        this.memberSecrets = memberSecrets;
        this.connections = connections;
        this.sites = sites;
        this.siteReadBudget = siteReadBudget;
    }

    /// <summary>The worker plugin runs go to.</summary>
    public IRunWorker Worker => worker;

    /// <summary>
    /// Who runs a connection's test (<see cref="Connections.TestAsync"/>) for Team Connections'
    /// <c>connection.test</c> record; null answers that no test can run here.
    /// </summary>
    public WorkerAsks? ConnectionTests { get; init; }

    /// <summary>
    /// Where Team Connections' answer is sent to the member that asked (<see cref="AnswerAskersAsync"/>);
    /// null sends none, and the answer is only its run's <c>completed</c> row.
    /// </summary>
    public IMessageLog? Answers { get; init; }

    /// <summary>The only variables a plugin child inherits from the Host. Everything else - provider
    /// keys, HARNESS_*, CLAUDE_*, GROK_* - is absent because it was never copied.</summary>
    public static readonly IReadOnlyList<string> InheritedVariables =
    [
        "PATH", "HOME", "LANG", "LC_ALL", "TZ", "TMPDIR", "DOTNET_ROOT", "DOTNET_CLI_HOME",
    ];

    public const string CausationVariable = "HARNESS_CAUSATION";

    /// <summary>
    /// The shortest secret VALUE a plugin is handed. Every text a plugin writes has each bound value
    /// replaced before it is stored, and a value shorter than this cannot be replaced without
    /// destroying the text around it - so it is REFUSED, at hire when it is already set and on every
    /// run, rather than passed through unredacted.
    /// </summary>
    public const int MinimumSecretLength = ValueRedactor.MinimumLength;

    /// <summary>
    /// A PLUGIN RUNS NO MODEL, so every result it comes to - a success, a failure, a run that did not
    /// begin - reports <see cref="InvocationUsage.NoModel"/>: its token cost is known, and it is zero.
    /// The runtime writes that on the terminal row as <c>tokensSource: "none"</c>, the reason no
    /// figures follow, and the spend queries count it as a measured run of 0.
    /// </summary>
    public async Task<MemberResult> RunAsync(MemberInvocation invocation, CancellationToken ct = default) =>
        await RunPluginAsync(invocation, ct) with { Usage = InvocationUsage.NoModel };

    private async Task<MemberResult> RunPluginAsync(MemberInvocation invocation, CancellationToken ct)
    {
        if (invocation.Context.UnreachableRoot is { Length: > 0 } unreachable)
        {
            return MemberResult.NotRun(
                $"This team's folder '{unreachable}' could not be reached when the Host started, so "
                + "this run did not begin. Reconnect the drive or share and restart the Host.");
        }

        if (runAs is { Refuses: true }) return MemberResult.NotRun(runAs.Refusal("This plugin member"));

        if (!MemberRef.IsPlugin(invocation.Implementation, out var id))
        {
            return MemberResult.NotRun($"'{invocation.Implementation}' is not a plugin reference.");
        }

        // Resolved HERE, on every run. See PluginCatalog.
        if (catalog.For(id) is not { } plugin)
        {
            return MemberResult.NotRun(
                (catalog.RefusalFor(id) ?? $"'{invocation.Implementation}' is not installed.")
                + " This run did not start; it takes effect the next time this member is woken.");
        }

        var manifest = plugin.Manifest;
        var bound = settings is null ? PluginMemberSettings.None : await settings.ForAsync(invocation.Member, ct);

        // WHAT ONLY A PERSON CAN FIX - a setting, a key, a connection - fails the run NEEDS-PERSON:
        // nothing ran, nothing is wrong with the plugin, and a person's save tells the Manager when
        // it is ready (PluginReadyNotice). Never agent-fault, which would say re-running is wasted.
        var (config, configRefusal) = EffectiveConfig(manifest, bound);
        if (configRefusal is not null) return MemberResult.NotRun(configRefusal, FailureClasses.NeedsPerson);

        var own = memberSecrets is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : await memberSecrets.ValuesAsync(invocation.Member, ct);
        var (resolvedSecrets, secretKeys, secretRefusal) = ResolveSecrets(manifest, bound, own);
        if (secretRefusal is not null) return MemberResult.NotRun(secretRefusal, FailureClasses.NeedsPerson);

        // THE MEMBER'S NAMED SECRETS, only for a plugin that declares the block; a value that is a name
        // the run is told would ride on stdin unredacted, so it is refused here, as a key's is.
        IReadOnlyDictionary<string, string>? named = manifest.NamedSecrets is null
            ? null
            : memberSecrets is null ? new Dictionary<string, string>(StringComparer.Ordinal) : await memberSecrets.NamedValuesAsync(invocation.Member, ct);
        if (named is not null && NamedValueIsAName(named, secretKeys, resolvedSecrets, manifest) is { } namedRefusal)
        {
            return MemberResult.NotRun(namedRefusal, FailureClasses.NeedsPerson);
        }

        var (grants, connectionRefusal, heldBy) = await GrantConnectionsAsync(manifest, bound, ct);
        if (connectionRefusal is not null)
        {
            // A CONNECTION THAT STILL NEEDS A PERSON holds this run up too: the row names it, so the
            // person's fix tells the Manager here as well.
            return MemberResult.NotRun(connectionRefusal, FailureClasses.NeedsPerson) with { HeldByConnection = heldBy };
        }

        // THE REDACTION SET: every bound secret AND every access token and app password this run is handed.
        // A field the manifest declares `"masked": false` is not secret, and is not redacted.
        // EACH TABLE'S SECRET CELLS, by row and column, for the rows the member has; a masked one is redacted.
        // A short part of a saved sign-in session is redacted only as a whole token (SignInSessionValue.SecretParts).
        var (cells, maskedCells, maskedTokens) = TableSecrets(manifest, bound, own);

        var redacted = ValueRedactor.For(
            [
                .. resolvedSecrets.Where(s => manifest.Secrets.GetValueOrDefault(s.Key)?.Masked != false).Select(s => s.Value),
                .. grants.Values.SelectMany(g => g.Secrets),
                .. named?.Values ?? [],
                .. maskedCells,
            ],
            maskedTokens);

        var siteFiles = await SiteFilesAsync(invocation.Member, ct);

        // TEAM CONNECTIONS, THE BUILT-IN: every connection assigned to its team by name, kind and health,
        // never a value; and its `connection.test` record runs that connection's own test. No other plugin
        // is handed either, so every other request is byte for byte what it was.
        var gathered = new Gathered();
        JsonArray? teamConnections = null;
        if (plugin.BuiltIn && manifest.Id == TeamConnectionsMember.PluginId && connections is not null)
        {
            gathered.TeamConnections = true;
            teamConnections = new JsonArray([.. (await AssignedConnectionsAsync(invocation.Member.Team, ct)).Select(c => (JsonNode)new JsonObject
            {
                ["name"] = c.Name,
                ["kind"] = c.Kind,
                ["health"] = c.Status,
                ["reason"] = c.StatusReason,
                ["testable"] = Connections.Testable(c),
            })]);
        }

        var request = Request(invocation, manifest, config, resolvedSecrets, grants, new JsonArray(), siteFiles, secretKeys, named, cells, teamConnections);

        StartRun Start(string stdin) => new(
            RunId.For(invocation.Member),
            invocation.Implementation,
            "",
            "",
            "",
            invocation.WorkingDirectory,
            new Dictionary<string, string>(),
            null,
            null,
            null,
            null,
            null,
            new RunProcess(
                plugin.Executable,
                manifest.Arguments,
                InheritedVariables,
                new Dictionary<string, string>
                {
                    [CausationVariable] = invocation.Causation.ToString(System.Globalization.CultureInfo.InvariantCulture),
                },
                stdin,
                manifest.TimeoutSeconds,
                plugin.Directory));

        // THE DECLARED READS, read now as the member and measured against what the rest of the
        // request leaves in the frame. A cut or a missing site is said on stdin and in one row each.
        if (manifest.Reads.Count > 0)
        {
            var (read, delivery) = await ReadSitesAsync(invocation.Member, manifest, FramedBytes(Start(request)), ct);
            request = Request(invocation, manifest, config, resolvedSecrets, grants, read, siteFiles, secretKeys, named, cells, teamConnections);

            foreach (var row in (string?[])[delivery.CutRow, delivery.MissingRow])
            {
                if (row is not null) await reports.ProgressAsync(invocation.Member, Redact(row, redacted), ct);
            }

            SiteDataDelivered?.Invoke(delivery);
        }

        // The run's record, gathered as the lines arrive (made above).
        // ON THE WORKER, through the run protocol: the same launcher every member shares, over
        // MINIMAL AND ALLOW-LISTED variables, the opposite of an agent's inherited-then-scrubbed
        // environment - a plugin is given only what it is named here. Each line it prints comes back
        // here, in order, before the next is read.
        var (ended, lost) = await directory.RunProcessAsync(
            worker,
            Start(request),
            line => OnLineAsync(invocation.Member, manifest, invocation.Context.Limits, line, gathered, redacted, ct),
            ct);

        if (lost) return MemberResult.NotRun(ended.LaunchError ?? RunDirectory.LostRunText, ended.FailureClass ?? FailureClasses.Interrupted);

        var outcome = ended.Process!;

        // EACH TEST TEAM CONNECTIONS ASKED FOR, now that its process has let go of the worker.
        if (outcome is { Killed: false, StartError: null, Refused: null })
        {
            foreach (var name in gathered.TestsAsked) gathered.Answers.Add(await TestByNameAsync(invocation.Member, name, ct));
        }

        if (outcome.Refused is not null)
        {
            return MemberResult.NotRun(
                $"setsid is not in a root-owned system directory ({string.Join(", ", SystemCommand.Directories)}), so this plugin member could not be started.");
        }

        if (outcome.StartError is { } startError)
        {
            return MemberResult.NotRun($"The plugin '{manifest.Id}' could not be started: {startError}");
        }

        if (outcome.Killed)
        {
            return outcome.Expired
                ? MemberResult.NotRun(
                    $"This run went {manifest.TimeoutSeconds}s without reporting progress and was stopped. "
                    + $"That limit is the plugin's own `timeoutSeconds` in its {PluginManifest.FileName}. It "
                    + "measures SILENCE, so a plugin that writes progress records can work for as long as it needs to.",
                    FailureClasses.Timeout) with { ProcessId = ended.ProcessId }
                : MemberResult.NotRun(
                    "This run was stopped before it finished, because the Host was shutting down. "
                    + "Whatever it had done is not recorded.",
                    FailureClasses.Interrupted) with { ProcessId = ended.ProcessId };
        }

        var output = new StringBuilder(gathered.Result?.Output ?? "");

        // EACH TEST TEAM CONNECTIONS ASKED FOR, in the test's own sentence: its answer to the member that asked.
        // Redacted below with the rest of the output.
        foreach (var answer in gathered.Answers)
        {
            if (output.Length > 0) output.AppendLine();
            output.Append(answer);
        }

        if (gathered.Text.Length > 0)
        {
            if (output.Length > 0) output.AppendLine();
            output.Append(gathered.Text);
        }

        foreach (var note in gathered.Notes)
        {
            if (output.Length > 0) output.AppendLine();
            output.Append("[harness] ").Append(note);
        }

        if (outcome.HeldOpen)
        {
            if (output.Length > 0) output.AppendLine();
            output.Append($"[harness] Something this plugin started is still holding its output after "
                + $"{ChildProcess.DrainGrace.TotalSeconds:0}s. The run is complete; anything written after this line was not captured.");
        }

        // Kept, not discarded, as an agent's is: it is where a plugin says what went wrong.
        if (!string.IsNullOrWhiteSpace(outcome.Stderr))
        {
            if (output.Length > 0) output.AppendLine();
            output.Append(outcome.Stderr);
        }

        var text = Redact(output.ToString().Trim(), redacted);

        string? reason = gathered.Result switch
        {
            null => $"The plugin exited {ended.ExitCode} without a result record.",
            { Ok: false } failed => Redact(
                string.IsNullOrWhiteSpace(failed.Error) ? "The plugin reported that it failed." : failed.Error!,
                redacted),
            _ when ended.ExitCode != 0 => $"The plugin exited {ended.ExitCode}.",
            _ => null,
        };

        // THE REASON LEADS THE OUTPUT when the output does not already say it. The terminal row
        // carries the output and the launch error, never this sentence, and the board, the trail
        // and every agent read a failure from that row: a plugin that failed with only a result
        // record (`ok:false` and its `error`) otherwise left a red card saying nothing about why.
        if (reason is not null && !text.Contains(reason, StringComparison.Ordinal))
        {
            text = text.Length == 0 ? reason : reason + "\n" + text;
        }

        // A CONNECTION THE RUN WAS HANDED, REPORTED REFUSED OR ASKING FOR A PERSON'S SIGN-IN, is marked
        // needs-reconnect: every member using it reads Needs you and waits. The run itself is still the
        // failure it was - not relabelled a wait - and names the connection that held it up. Only the
        // plugin's named kind decides; a kind not named, unreachable, or a slot this run was not handed
        // marks nothing. See RunConnectionFailures.
        string? heldByConnection = null;
        if (gathered.Result is { Ok: false, Slot: { } slot, Kind: { } kind } failedOn
            && RunConnectionFailures.Holds(kind)
            && grants.TryGetValue(slot, out var handed)
            && connections is not null)
        {
            var sentence = Redact(RunConnectionFailures.Sentence(kind, failedOn.Error), redacted);

            try
            {
                await connections.MarkFromRunAsync(handed.ConnectionId, sentence, kind, invocation.Member, slot, ct);
                heldByConnection = handed.ConnectionId;
            }
            catch (System.Data.Common.DbException)
            {
                // Not marked: the run fails as it would have, and the next run reports it again.
            }
        }

        // TEAM CONNECTIONS ANSWERS THE MEMBER THAT ASKED, in its own workflow, as an instruction to it.
        if (gathered.TeamConnections) await AnswerAskersAsync(invocation, text, ct);

        return new MemberResult(
            reason is null,
            ended.ExitCode,
            text,
            FailureReason: reason,
            ProcessId: ended.ProcessId,
            // A FAILURE IS NEVER QUIET, whatever the record said: ok:false, a non-zero exit, a
            // timeout and a Stop all wake as they always did.
            Quiet: reason is null && gathered.Result is { Quiet: true },
            HeldByConnection: reason is null ? null : heldByConnection);
    }

    /// <summary>The request document: protocol v1's whole input, one JSON object on stdin.</summary>
    internal static string Request(
        MemberInvocation invocation, PluginManifest manifest,
        IReadOnlyDictionary<string, JsonNode?> config, IReadOnlyDictionary<string, string> resolvedSecrets,
        IReadOnlyDictionary<string, ConnectionGrant>? grants = null, JsonArray? sites = null,
        JsonArray? siteFiles = null, IReadOnlyDictionary<string, string>? secretKeys = null,
        IReadOnlyDictionary<string, string>? namedSecrets = null, JsonObject? tableSecrets = null,
        JsonArray? teamConnections = null)
    {
        var work = new JsonArray();

        foreach (var message in invocation.Work)
        {
            JsonNode? payload;

            try
            {
                payload = JsonNode.Parse(message.Payload);
            }
            catch (JsonException)
            {
                payload = JsonValue.Create(message.Payload);
            }

            var item = new JsonObject
            {
                ["seq"] = message.Seq,
                ["type"] = message.Type,
                ["source"] = message.Source,
                ["correlation"] = message.CorrelationId,
                ["payload"] = payload,
            };

            // The words of an instruction, lifted out so a plugin need not know the payload shape.
            if (payload is JsonObject body && body[PayloadFields.Instruction] is JsonValue words
                && words.TryGetValue<string>(out var instruction))
            {
                item["instruction"] = instruction;
            }

            work.Add(item);
        }

        var worktrees = new JsonArray();
        foreach (var tree in invocation.Context.Worktrees)
        {
            worktrees.Add(new JsonObject { ["repo"] = tree.Repo, ["path"] = tree.Path, ["clonePath"] = tree.ClonePath });
        }

        var document = new JsonObject
        {
            ["protocol"] = PluginManifest.ProtocolV1,
            ["plugin"] = new JsonObject { ["id"] = manifest.Id, ["version"] = manifest.Version },
            ["member"] = new JsonObject
            {
                ["id"] = invocation.Member.ToString(),
                ["team"] = invocation.Member.Team,
                ["name"] = invocation.Member.Name,
            },
            ["causation"] = invocation.Causation,
            ["correlation"] = invocation.Work[0].CorrelationId,
            ["work"] = work,
            ["config"] = new JsonObject(config.Select(c => KeyValuePair.Create(c.Key, c.Value?.DeepClone()))),
            // A TABLE'S SECRET CELLS sit beside the fields, as `secrets.<table>.<row>.<column>` - never in
            // `config`. Only a plugin declaring a table with a secret column has any.
            ["secrets"] = new JsonObject(resolvedSecrets.Select(s => KeyValuePair.Create(s.Key, (JsonNode?)JsonValue.Create(s.Value)))
                .Concat((tableSecrets ?? []).Select(t => KeyValuePair.Create(t.Key, t.Value?.DeepClone())))),

            // EACH SECRET FIELD'S NAME FOR IT - the bound logical key, set or not, or the field's own
            // name for a field set only by the member's own value - a name, never a value, and not
            // redacted, so a plugin can say which value is missing. Additive: `secrets` is unchanged.
            ["secretKeys"] = new JsonObject((secretKeys ?? new Dictionary<string, string>()).Select(k => KeyValuePair.Create(k.Key, (JsonNode?)JsonValue.Create(k.Value)))),
            ["workingDirectory"] = invocation.WorkingDirectory,
            ["worktrees"] = worktrees,

            // EACH BOUND SLOT'S FRESH ACCESS TOKEN - never the refresh token, never the client secret -
            // or a mailbox's servers and app password.
            ["connections"] = new JsonObject((grants ?? new Dictionary<string, ConnectionGrant>()).Select(g => KeyValuePair.Create(g.Key, (JsonNode?)(
                g.Value.Mailbox is { } mailbox
                    ? new JsonObject
                    {
                        ["kind"] = MailboxSettings.Kind,
                        ["account"] = g.Value.Account,
                        ["username"] = mailbox.Username,
                        ["password"] = mailbox.Password,
                        ["imap"] = new JsonObject { ["host"] = mailbox.Imap.Host, ["port"] = mailbox.Imap.Port, ["security"] = mailbox.Imap.Security },
                        ["smtp"] = new JsonObject { ["host"] = mailbox.Smtp.Host, ["port"] = mailbox.Smtp.Port, ["security"] = mailbox.Smtp.Security },
                    }
                    : g.Value.Credential is { } credential
                        // A WEBSITE, AN API KEY, A USERNAME AND PASSWORD: each field by name, its value as stored.
                        ? new JsonObject
                        {
                            ["kind"] = credential.Kind,
                            ["account"] = g.Value.Account,
                            ["preset"] = credential.Preset,
                            ["fields"] = new JsonObject(credential.Values.Select(v => KeyValuePair.Create(v.Key, (JsonNode?)JsonValue.Create(v.Value)))),
                        }
                        : Grant(g.Value))))),

            // EACH OF THE MEMBER'S OWN TEAM'S SITES' FILES FOLDER (see SiteFilesAsync); [] for none.
            // Cloned: the request is built twice when reads are measured, and a node has one parent.
            ["siteFiles"] = siteFiles?.DeepClone() ?? new JsonArray(),

        };

        // THE MEMBER'S NAMED SECRETS, name to value, and the names alone, sorted - only for a plugin whose
        // manifest declares a `namedSecrets` block: every other plugin's request is byte for byte what it was.
        if (manifest.NamedSecrets is not null)
        {
            var names = (namedSecrets ?? new Dictionary<string, string>()).Keys.Order(StringComparer.Ordinal).ToList();
            document["namedSecrets"] = new JsonObject(names.Select(n => KeyValuePair.Create(n, (JsonNode?)JsonValue.Create(namedSecrets![n]))));
            document["namedSecretNames"] = new JsonArray([.. names.Select(n => (JsonNode?)JsonValue.Create(n))]);
        }

        // TEAM CONNECTIONS' OWN: its team's connections, never a value. Only the built-in is handed it.
        if (teamConnections is not null) document["teamConnections"] = teamConnections.DeepClone();

        // THE DECLARED READS of the member's own team's sites (see ReadSitesAsync); [] for none.
        // Last, so what is measured before it is the whole rest of the request.
        document["sites"] = sites ?? new JsonArray();

        return document.ToJsonString() + "\n";
    }

    /// <summary>One OAuth slot as stdin carries it. A Microsoft slot also carries <c>tokens</c>, last:
    /// API to that API's own fresh token, the first of them being <c>accessToken</c>.</summary>
    private static JsonObject Grant(ConnectionGrant grant)
    {
        var slot = new JsonObject
        {
            ["provider"] = grant.Provider,
            ["account"] = grant.Account,
            ["accessToken"] = grant.AccessToken,
            ["expiresAt"] = Expiry(grant.ExpiresAt),
            ["scopes"] = Scopes(grant.Scopes),
        };

        if (grant.Tokens is { } tokens)
        {
            slot["tokens"] = new JsonObject(tokens.Select(t => KeyValuePair.Create(t.Api, (JsonNode?)new JsonObject
            {
                ["accessToken"] = t.AccessToken,
                ["expiresAt"] = Expiry(t.ExpiresAt),
                ["scopes"] = Scopes(t.Scopes),
            })));
        }

        return slot;

        static string? Expiry(DateTimeOffset? at) => at?.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

        static JsonArray Scopes(IReadOnlyList<string> scopes) => new([.. scopes.Select(scope => (JsonNode?)JsonValue.Create(scope))]);
    }

    /// <summary>
    /// The run's <c>siteFiles</c> block: every site of the member's OWN team, by name, with the
    /// absolute path of its files folder in the team's documents, repaired here. Every site, not only
    /// the declared reads: writing a file needs no read. Paths only, never a secret. <c>[]</c> when the
    /// team has no site or the Host serves none. Part of the request measured before site documents
    /// are chosen, so the worker frame's bound holds unchanged.
    /// </summary>
    public async Task<JsonArray> SiteFilesAsync(ContainerId member, CancellationToken ct = default)
    {
        var listed = new JsonArray();
        if (sites is null) return listed;

        foreach (var site in (await sites.ListAsync(member.Team, ct)).OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            listed.Add(new JsonObject { ["site"] = site.Name, ["folder"] = sites.Files.Ensure(site.Team, site.Name) });
        }

        return listed;
    }

    /// <summary>
    /// The collections <paramref name="manifest"/> <c>reads</c>, as the run's <c>sites</c> block:
    /// one envelope per declared read, in declaration order, each with its documents newest first.
    ///
    /// <list type="bullet">
    /// <item>OWN TEAM ONLY: each is listed in the member's team with the member as a bound actor, as
    /// <c>site.put</c> writes, so another team's site reads exactly as a missing one does. A read
    /// appends nothing to the tenant log.</item>
    /// <item>THE BUDGET counts documents only, in framed bytes: the lesser of the runner's budget and
    /// what <paramref name="rest"/> - the <see cref="StartRun"/> frame with <c>sites: []</c> - leaves
    /// in the frame after <see cref="EnvelopeMarginBytes"/>, never below zero. Envelopes are always
    /// delivered, paid from the margin.</item>
    /// <item>A STRICT NEWEST-FIRST PREFIX: collections fill in declaration order, and the first
    /// document that does not fit ends delivery for the whole run. Whole documents only.</item>
    /// <item>NEVER SILENT: each collection cut says so in its <c>cut</c>, and the run writes one row
    /// for the cut and one for missing sites, naming names and counts, never content.</item>
    /// </list>
    /// </summary>
    public async Task<(JsonArray Sites, PluginSiteDelivery Delivery)> ReadSitesAsync(
        ContainerId member, PluginManifest manifest, long rest, CancellationToken ct = default)
    {
        var room = WorkerFrameCodec.MaxBytes - rest - EnvelopeMarginBytes;
        var frameLimited = room < siteReadBudget;
        var budget = Math.Max(0, Math.Min(siteReadBudget, room));

        var limit = siteReadBudget % (1024 * 1024) == 0
            ? $"{siteReadBudget / (1024 * 1024)} MiB"
            : $"{siteReadBudget.ToString(CultureInfo.InvariantCulture)} bytes";
        var clause = frameLimited
            ? $"{limit}, less what the rest of the request takes; {budget.ToString(CultureInfo.InvariantCulture)} bytes were left"
            : limit;

        var reads = new List<(PluginSiteRead Read, IReadOnlyList<SiteDocument>? Documents, string? Missing)>();

        foreach (var read in manifest.Reads)
        {
            if (sites is null)
            {
                reads.Add((read, null, "This Host serves no sites."));
                continue;
            }

            var listed = await sites.ListDocumentsAsync(member.Team, read.Site, read.Collection, SiteActor.Member(member), ct);

            reads.Add(listed.Value is { } documents
                ? (read, [.. documents.OrderByDescending(d => d.UpdatedAt).ThenBy(d => d.Id, StringComparer.Ordinal)], null)
                : (read, null, listed.Refusal ?? SiteService.NoSuchSite));
        }

        // WHAT FITS: the newest documents first, until the first that does not.
        long spent = 0;
        var full = false;
        var delivered = new List<List<JsonObject>>();

        foreach (var (_, documents, _) in reads)
        {
            var entries = new List<JsonObject>();
            delivered.Add(entries);

            foreach (var document in documents ?? [])
            {
                if (full) break;

                var entry = new JsonObject
                {
                    ["id"] = document.Id,
                    ["doc"] = JsonNode.Parse(document.Json),
                    ["updatedAt"] = document.UpdatedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                    ["updatedBy"] = document.UpdatedBy,
                };

                // Its comma too, after the first of its list.
                var cost = FramedBytes(entry) + (entries.Count > 0 ? 1 : 0);

                if (spent + cost > budget)
                {
                    full = true;
                    break;
                }

                spent += cost;
                entries.Add(entry);
            }
        }

        var block = new JsonArray();
        var cut = new List<string>();
        var missing = new List<string>();

        for (var i = 0; i < reads.Count; i++)
        {
            var (read, documents, gone) = reads[i];
            var total = documents?.Count ?? 0;
            var count = delivered[i].Count;
            string? sentence = null;

            if (documents is not null && count < total)
            {
                sentence = $"Only {count} of {total} documents of {read.Site}/{read.Collection} were delivered (newest first): "
                    + $"this run's site data is limited to {clause}. The rest were cut.";
                cut.Add($"{read.Site}/{read.Collection} {count} of {total}");
            }

            if (gone is not null) missing.Add($"{read.Site}/{read.Collection} ({gone})");

            block.Add(new JsonObject
            {
                ["site"] = read.Site,
                ["collection"] = read.Collection,
                ["documents"] = new JsonArray(),
                ["total"] = total,
                ["cut"] = sentence,
                ["missing"] = gone,
            });
        }

        // THE ENVELOPES' OWN COST, measured empty; the documents then add exactly what was spent.
        var envelopeBytes = FramedBytes(block) - FramedBytes(new JsonArray());

        for (var i = 0; i < reads.Count; i++)
        {
            var list = (JsonArray)block[i]!["documents"]!;
            foreach (var entry in delivered[i]) list.Add(entry);
        }

        return (block, new PluginSiteDelivery(
            rest, budget, frameLimited, spent, envelopeBytes,
            cut.Count == 0 ? null : $"Site data for this run was cut: {string.Join(", ", cut)}. This run's site data is limited to {clause}.",
            missing.Count == 0 ? null : $"A declared read found no site: {string.Join(", ", missing)}."));
    }

    /// <summary>
    /// What <paramref name="node"/> costs inside a worker frame: its request text
    /// (<see cref="JsonNode.ToJsonString"/>), escaped again as a string by
    /// <see cref="WorkerFrameCodec.Json"/>, in UTF-8 bytes. Both escape character by character, so
    /// pieces' costs add up.
    /// </summary>
    public static long FramedBytes(JsonNode node) =>
        Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(node.ToJsonString(), WorkerFrameCodec.Json)) - 2;

    /// <summary>
    /// <paramref name="start"/> as <see cref="WorkerFrameCodec.Write"/> frames it, in UTF-8 bytes,
    /// without its refusal: a plugin run seals nothing, so this is the frame bar its id's digits.
    /// </summary>
    public static long FramedBytes(StartRun start) =>
        Encoding.UTF8.GetByteCount(JsonSerializer.Serialize<WorkerFrame>(new CommandFrame(0, start), WorkerFrameCodec.Json));

    /// <summary>The manifest's defaults under this member's own values, each checked against its
    /// field's type. A required field with neither is a refusal naming it. Bounds are a WRITE's
    /// check (<see cref="SettingsRefusal"/>), not a run's: a value stored before its bounds existed
    /// is never rewritten, and the member keeps running with it.</summary>
    public static (IReadOnlyDictionary<string, JsonNode?> Config, string? Refusal) EffectiveConfig(
        PluginManifest manifest, PluginMemberSettings bound)
    {
        var config = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);

        foreach (var (name, field) in manifest.Config)
        {
            if (bound.Config.TryGetValue(name, out var value))
            {
                if (field.Refusal(name, value, bounds: false) is { } refusal) return (config, $"This member's configuration is invalid: {refusal}");
                config[name] = field.Table is { } table ? table.ForRun(value) : JsonNode.Parse(value.GetRawText());
            }
            else if (field.Default is { } fallback)
            {
                config[name] = JsonNode.Parse(fallback.GetRawText());
            }
            else if (field.Required)
            {
                return (config, $"This member's configuration has no `{name}`, which the plugin requires.");
            }
        }

        return (config, null);
    }

    /// <summary>
    /// Each stored config value outside its field's bounds, by field, with the sentence a write of it
    /// would be refused with. Reported on the settings read; the value itself is left as it is.
    /// </summary>
    public static IReadOnlyDictionary<string, string> OutOfRange(PluginManifest manifest, PluginMemberSettings stored) =>
        stored.Config
            .Where(c => manifest.Config.ContainsKey(c.Key))
            .Select(c => (c.Key, Refusal: manifest.Config[c.Key].BoundsRefusal(c.Key, c.Value)))
            .Where(c => c.Refusal is not null)
            .ToDictionary(c => c.Key, c => c.Refusal!, StringComparer.Ordinal);

    /// <summary>
    /// Why <paramref name="settings"/> cannot be saved for a member of <paramref name="manifest"/>,
    /// or null - checked at hire so a person hears it then, and not from the first run. Every
    /// field must be one the manifest declares and of its type; every secret binding must name a
    /// declared secret and a usable logical key; a required secret must be bound and set NOW -
    /// except for a solution install (<paramref name="requireSet"/> false), where a key not set yet is
    /// not a refusal: the member fails its runs, naming the key, until it is set.
    /// </summary>
    public static string? SettingsRefusal(
        PluginManifest manifest, PluginMemberSettings settings, ISecretStore? secrets, bool requireSet = true, PluginMemberSettings? stored = null,
        bool requireBound = true, IReadOnlySet<string>? valued = null)
    {
        foreach (var (name, value) in settings.Config)
        {
            // A VALUE STORED FOR A FIELD A NEWER VERSION DROPPED, sent back unchanged, is kept - as a
            // stored number outside later bounds is: a run never sees it, and refusing it would stop
            // every other change to the member until a person found it.
            if (!manifest.Config.ContainsKey(name)
                && !(stored is not null && stored.Config.TryGetValue(name, out var kept) && JsonElement.DeepEquals(kept, value)))
            {
                return $"`{name}` is not a configuration field of plugin '{manifest.Id}'. It has: "
                    + (manifest.Config.Count == 0 ? "none" : string.Join(", ", manifest.Config.Keys)) + ".";
            }
        }

        if (EffectiveConfig(manifest, settings).Refusal is { } configRefusal) return configRefusal;

        // A NUMBER OUTSIDE ITS BOUNDS IS REFUSED, naming the field and the bound - by every writer,
        // which all come here. The one exception is the value already STORED for the field, sent
        // back unchanged: it is kept as it is (see OutOfRange), never rewritten, and it does not
        // stop the rest of a form being saved.
        foreach (var (name, value) in settings.Config)
        {
            if (stored is not null && stored.Config.TryGetValue(name, out var kept) && JsonElement.DeepEquals(kept, value)) continue;
            if (manifest.Config.TryGetValue(name, out var declared) && declared.BoundsRefusal(name, value) is { } outside) return outside;
        }

        foreach (var (name, key) in settings.Secrets)
        {
            if (!manifest.Secrets.ContainsKey(name))
            {
                return $"`{name}` is not a secret plugin '{manifest.Id}' names. It names: "
                    + (manifest.Secrets.Count == 0 ? "none" : string.Join(", ", manifest.Secrets.Keys)) + ".";
            }

            if (EnvironmentSecretStore.Refusal(key) is { } keyRefusal) return keyRefusal;

            if (secrets?.TryGet(key) is { Length: < MinimumSecretLength })
            {
                return ShortSecret(key, name);
            }
        }

        if (KeyNamedByAValue(settings.Secrets, secrets) is { } named) return named;

        foreach (var (name, secret) in manifest.Secrets.Where(s => s.Value.Required))
        {
            // THE MEMBER'S OWN VALUE satisfies the field, whatever is bound.
            if (valued?.Contains(name) == true) continue;

            // An agent's hire may leave a required secret unset (requireBound false): the member
            // is made and waits for a person to set it (PluginReadyNotice.Missing).
            if (!settings.Secrets.TryGetValue(name, out var key))
            {
                if (!requireBound) continue;
                return $"Plugin '{manifest.Id}' requires the secret `{name}`: type its value in the member's settings "
                    + $"(\"secretValues\": {{\"{name}\": \"...\"}}).";
            }

            if (requireSet && secrets?.TryGet(key) is null)
            {
                return $"The secret `{key}` bound for `{name}` is not set on this Host. {SetIt(key)}";
            }
        }

        return null;
    }

    /// <summary>
    /// Each declared secret field resolved NOW, never earlier: THE MEMBER'S OWN VALUE first
    /// (<see cref="IPluginMemberSecrets"/>), else the key the member's settings bind the field to,
    /// through <see cref="ISecretStore"/> (a key stored from the screen, then the Host's environment).
    /// Beside each, the NAME a plugin is told it by on stdin (<c>secretKeys</c>): the bound key when
    /// there is one, else the field's own name for a field set only by a member value - so a plugin can
    /// always say which value is missing.
    /// </summary>
    private (IReadOnlyDictionary<string, string> Secrets, IReadOnlyDictionary<string, string> Keys, string? Refusal) ResolveSecrets(
        PluginManifest manifest, PluginMemberSettings bound, IReadOnlyDictionary<string, string> own)
    {
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (name, secret) in manifest.Secrets)
        {
            var key = bound.Secrets.GetValueOrDefault(name);

            if (own.TryGetValue(name, out var mine))
            {
                keys[name] = key ?? name;
                resolved[name] = mine;
                continue;
            }

            var value = key is null || secrets is null ? null : secrets.TryGet(key);

            if (key is not null) keys[name] = key;

            if (value is { Length: < MinimumSecretLength } && secret.Masked)
            {
                return (resolved, keys, ShortSecret(key!, name));
            }

            if (value is not null)
            {
                resolved[name] = value;
            }
            else if (secret.Required)
            {
                return (resolved, keys, key is null
                    ? $"This member has no value for the plugin's required secret `{name}`. A person types it in the member's settings."
                    : $"The secret `{key}` that this member binds for `{name}` is not set on this Host. {SetIt(key)}");
            }
        }

        // A NAME THAT IS A HANDED VALUE is refused here as at binding: the names go on stdin unredacted.
        if (NameIsAValue(keys, resolved, own, manifest) is { } named) return (resolved, keys, named);

        return (resolved, keys, null);
    }

    /// <summary>
    /// Why a named secret's value is a name this run hands on stdin unredacted - one of the member's
    /// named secrets or a <c>secretKeys</c> name - or why a field's masked value is one of the member's
    /// secret names, or null. The sentence names the secret or field, never the value.
    /// </summary>
    private static string? NamedValueIsAName(
        IReadOnlyDictionary<string, string> named, IReadOnlyDictionary<string, string> keys,
        IReadOnlyDictionary<string, string> resolved, PluginManifest manifest)
    {
        var names = named.Keys.Concat(keys.Values).ToHashSet(StringComparer.Ordinal);

        foreach (var (name, value) in named.OrderBy(n => n.Key, StringComparer.Ordinal))
        {
            if (names.Contains(value))
            {
                return $"The value of this member's secret `{name}` is the name of a secret its plugin is told, and names "
                    + "are not secret. A person types the real value in the member's settings.";
            }
        }

        foreach (var (field, value) in resolved)
        {
            if (manifest.Secrets.GetValueOrDefault(field)?.Masked != false && named.ContainsKey(value))
            {
                return $"The value handed for `{field}` is the name of one of this member's named secrets, and names are not "
                    + "secret. Set a real value in the member's settings.";
            }
        }

        return null;
    }

    /// <summary>Why a name handed on stdin as <c>secretKeys</c> is itself a masked value this run is
    /// handed, or null. The sentence names the field, never the value.</summary>
    private static string? NameIsAValue(
        IReadOnlyDictionary<string, string> keys, IReadOnlyDictionary<string, string> resolved,
        IReadOnlyDictionary<string, string> own, PluginManifest manifest)
    {
        var names = keys.Values.ToHashSet(StringComparer.Ordinal);

        foreach (var (name, value) in resolved)
        {
            if (manifest.Secrets.GetValueOrDefault(name)?.Masked == false || !names.Contains(value)) continue;

            if (!own.ContainsKey(name) && keys.GetValueOrDefault(name) is { } key)
            {
                return $"The secret `{key}` bound for `{name}` has a value that is the name of a key this member binds, "
                    + $"and key names are not secret. Set a real value for `{key}`. {SetIt(key)}";
            }

            return $"The value handed for `{name}` is the name of a key or field this member's plugin is told, "
                + "and names are not secret. Set a real value in the member's settings.";
        }

        return null;
    }

    /// <summary>
    /// Why the bindings cannot stand because one key's NAME is the value of a key bound beside it
    /// (its own or another's), or null. Key names go on stdin unredacted as <c>secretKeys</c>, so a
    /// name that is a value would put that value beside a field it was not bound to. The sentence
    /// names the key whose value it is, never the value.
    /// </summary>
    private static string? KeyNamedByAValue(IReadOnlyDictionary<string, string> bindings, ISecretStore? secrets)
    {
        if (secrets is null) return null;

        var names = bindings.Values.ToHashSet(StringComparer.Ordinal);

        foreach (var (name, key) in bindings)
        {
            if (secrets.TryGet(key) is { } value && names.Contains(value))
            {
                return $"The secret `{key}` bound for `{name}` has a value that is the name of a key this member binds, "
                    + $"and key names are not secret. Set a real value for `{key}`. {SetIt(key)}";
            }
        }

        return null;
    }

    /// <summary>
    /// Each connection slot's fresh access token, fetched NOW (and refreshed when it is within 5
    /// minutes of expiry), or a mailbox's app password (logged in again when its last good login is
    /// older than <see cref="Connections.MailboxRecheck"/>), or the sentence the run is blocked with: a
    /// required slot unbound, a connection gone, needing reconnect, or lacking a scope the slot asks for.
    /// </summary>
    private async Task<(IReadOnlyDictionary<string, ConnectionGrant> Grants, string? Refusal, string? HeldBy)> GrantConnectionsAsync(
        PluginManifest manifest, PluginMemberSettings bound, CancellationToken ct)
    {
        var grants = new Dictionary<string, ConnectionGrant>(StringComparer.Ordinal);

        foreach (var (slot, declared) in manifest.Connections)
        {
            if (!bound.Connections.TryGetValue(slot, out var connectionId))
            {
                if (declared.Required)
                {
                    return (grants, $"This member has no connection bound for the plugin's required slot `{slot}`. "
                        + "A person binds one in the member's settings.", null);
                }

                continue;
            }

            if (connections is null)
            {
                return (grants, $"This Host holds no connections, so slot `{slot}` could not be given its account.", null);
            }

            var record = await connections.Store.GetAsync(connectionId, ct);
            if (record is not null && Connections.ScopeRefusal(slot, declared, record, retry: null) is { } scopeRefusal)
            {
                return (grants, scopeRefusal.Error, null);
            }

            ConnectionGrant? grant;
            string? refusal;

            try
            {
                (grant, refusal) = await connections.GrantAsync(connectionId, slot, ct, record?.Provider is { } provider && declared.ScopesFor(provider) is { Count: > 0 } asked ? asked : null);
            }
            catch (RefreshNotStoredException exception)
            {
                // A REFRESHED TOKEN THAT COULD NOT BE STORED IS NOT HANDED OUT: a rotated refresh token
                // lost here would strand the connection, so the run does not start.
                return (grants, $"The connection bound for slot `{slot}` was refreshed but could not be stored "
                    + $"({exception.Code}), so this run did not start. The next run tries again.", null);
            }
            catch (System.Data.Common.DbException exception)
            {
                return (grants, $"The connection bound for slot `{slot}` could not be read or updated "
                    + $"({DatabaseFault.Code(exception)}), so this run did not start. The next run tries again.", null);
            }

            if (refusal is not null)
            {
                // Held by the connection when it is, after the grant, marked as needing a person.
                var after = await connections.Store.GetAsync(connectionId, ct);
                return (grants, refusal, after?.Status == ConnectionRecord.NeedsReconnect ? connectionId : null);
            }

            grants[slot] = grant!;
        }

        return (grants, null, null);
    }

    private static string ShortSecret(string key, string name) =>
        $"The secret `{key}` bound for `{name}` is shorter than {MinimumSecretLength} characters, so it "
        + "could not be kept out of what this plugin writes. Set a longer value in the member's settings, "
        + $"or with the operator CLI's `secret set {key}` and its `up`.";

    /// <summary>How a person sets <paramref name="key"/>: the screen first, the operator CLI second.</summary>
    private static string SetIt(string key) =>
        $"Set it in the member's settings, or with the operator CLI's `secret set {key}` and its `up`.";

    private async Task OnLineAsync(
        ContainerId member, PluginManifest manifest, ArtifactLimits limits, string line, Gathered gathered,
        ValueRedactor secretValues, CancellationToken ct)
    {
        var trimmed = line.Trim();

        if (trimmed.Length == 0) return;

        JsonObject? record = null;

        if (trimmed.StartsWith('{'))
        {
            try
            {
                record = JsonNode.Parse(trimmed) as JsonObject;
            }
            catch (JsonException)
            {
            }
        }

        switch (record?["t"]?.GetValueKind() == JsonValueKind.String ? (string?)record["t"] : null)
        {
            case "progress" when Words(record!, "status") is { } status:
                await reports.ProgressAsync(member, Redact(status, secretValues), ct);
                break;

            case "blocked" when Words(record!, "reason") is { } reason:
                int? item = record!["item"] is JsonValue number && number.TryGetValue<int>(out var index) ? index : null;
                var outcome = await reports.BlockedAsync(member, Redact(reason, secretValues), item, ct);
                if (!outcome.Accepted) gathered.Notes.Add($"blocked record refused: {outcome.Refusal}");
                break;

            case "needsDecision" when Words(record!, "question") is { } question:
                await reports.NeedsDecisionAsync(member, Redact(question, secretValues), ct);
                break;

            case "handback" when Words(record!, "delivered") is { } delivered:
                await reports.HandbackAsync(member, Redact(delivered, secretValues), ct);
                break;

            case "result":
                gathered.Result = new ResultRecord(
                    record!["ok"]?.GetValueKind() == JsonValueKind.True,
                    record["output"]?.GetValueKind() == JsonValueKind.String ? (string?)record["output"] : null,
                    record["error"]?.GetValueKind() == JsonValueKind.String ? (string?)record["error"] : null,
                    record["quiet"]?.GetValueKind() == JsonValueKind.True,
                    record[RunConnectionFailures.SlotField]?.GetValueKind() == JsonValueKind.String ? (string?)record[RunConnectionFailures.SlotField] : null,
                    record[RunConnectionFailures.KindField]?.GetValueKind() == JsonValueKind.String ? (string?)record[RunConnectionFailures.KindField] : null);
                break;

            case "publish":
                await PublishAsync(member, manifest, limits, record!, gathered, secretValues, ct);
                break;

            case "site.put":
            case "site.delete":
                await SiteRecordAsync(member, record!, gathered, secretValues, ct);
                break;

            case "row.status":
                await RowStatusAsync(member, manifest, record!, gathered, secretValues, ct);
                break;

            // TEAM CONNECTIONS ONLY: any other plugin's line of this shape is text, as an unknown record is.
            // Asked here, run once the process has ended: a test runs on a worker, and this worker is waiting
            // for this line to be read.
            case "connection.test" when gathered.TeamConnections && Words(record!, "name") is { } name:
                gathered.TestsAsked.Add(name.Trim());
                break;

            default:
                // NOT A RECORD, OR NOT ONE THIS VERSION KNOWS: kept as text, never an error.
                if (gathered.Text.Length > 0) gathered.Text.Append('\n');
                gathered.Text.Append(line);
                break;
        }
    }

    /// <summary>
    /// EACH TABLE'S SECRET CELLS AS A RUN RECEIVES THEM: table to row (as the member's settings spell it)
    /// to column to value, for every table with a secret column - `{}` for one whose cells are all
    /// empty - and the masked values, which join the run's redaction set. A cell is the member's own
    /// value under <see cref="PluginMemberSecretStore.CellField"/>, so it is in <paramref name="own"/>.
    /// A saved sign-in session's short parts are in <c>MaskedTokens</c>, redacted only as whole tokens.
    /// </summary>
    internal static (JsonObject Cells, IReadOnlyList<string> Masked, IReadOnlyList<string> MaskedTokens) TableSecrets(
        PluginManifest manifest, PluginMemberSettings bound, IReadOnlyDictionary<string, string> own)
    {
        var cells = new JsonObject();
        var masked = new List<string>();
        var maskedTokens = new List<string>();

        foreach (var (name, field) in manifest.Config)
        {
            if (field.Table is not { } table || !table.SecretColumns.Any()) continue;

            var rows = new JsonObject();
            foreach (var row in table.RowNames(bound.Config.TryGetValue(name, out var stored) ? stored : null))
            {
                var values = new JsonObject();
                foreach (var column in table.SecretColumns)
                {
                    if (!own.TryGetValue(PluginMemberSecretStore.CellField(name, row, column.Name), out var value)) continue;
                    values[column.Name] = value;
                    if (!column.Masked) continue;
                    masked.Add(value);
                    // A saved sign-in session is redacted part by part too: one cookie printed alone.
                    var parts = SignInSessionValue.SecretParts(value);
                    masked.AddRange(parts.Anywhere);
                    maskedTokens.AddRange(parts.WholeTokens);
                }

                if (values.Count > 0) rows[row] = values;
            }

            cells[name] = rows;
        }

        return (cells, masked, maskedTokens);
    }

    /// <summary>
    /// A <c>row.status</c> record: <c>{"t":"row.status","field":"&lt;table&gt;","row":"&lt;key&gt;","values":{...}}</c>.
    ///
    /// <list type="bullet">
    /// <item>WRITES ONLY THE ROW'S READ-ONLY COLUMNS - the plugin's status cells - of a row the member
    /// already has (its key matched ignoring case). A row a person has not added is never made.</item>
    /// <item>An unknown table, row or column is dropped with a warning; a column a person writes, and a
    /// secret column, is ignored with a warning; a cell not of its column's type is dropped with a
    /// warning. One warning row per sentence per run.</item>
    /// <item>A string cell is redacted like everything else the plugin writes. The rest of the member's
    /// settings - every row, every cell a person wrote - is left exactly as it is.</item>
    /// </list>
    /// </summary>
    private async Task RowStatusAsync(
        ContainerId member, PluginManifest manifest, JsonObject record, Gathered gathered, ValueRedactor secretValues, CancellationToken ct)
    {
        var name = Words(record, "field");
        var rowName = Words(record, "row");

        if (name is null || !manifest.Config.TryGetValue(name, out var field) || field.Table is not { } table)
        {
            await WarnAsync($"A `row.status` was dropped: `{name ?? "(no field)"}` is not a table of plugin '{manifest.Id}'.");
            return;
        }

        if (settings is not IPluginMemberSettingsStore store)
        {
            await WarnAsync("A `row.status` was dropped: this Host keeps no member settings.");
            return;
        }

        if (record["values"] is not JsonObject values)
        {
            await WarnAsync($"A `row.status` of `{name}` was dropped: its `values` is not a JSON object.");
            return;
        }

        if (rowName is null || table.Row((await store.ForAsync(member, ct)).Config.TryGetValue(name, out var current) ? current : null, rowName) is null)
        {
            await WarnAsync($"A `row.status` of `{name}` was dropped: there is no row '{rowName ?? "(no row)"}'. Only a person adds a row.");
            return;
        }

        var accepted = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        foreach (var (column, node) in values)
        {
            var declared = table.Column(column);
            var why = declared switch
            {
                null => $"`{name}` has no column `{column}`; it was dropped.",
                { IsSecret: true } => $"`{name}.{column}` is a secret column; a status never writes one, so it was ignored.",
                { ReadOnly: false } => $"`{name}.{column}` is written only by a person; it was ignored.",
                _ => null,
            };

            if (why is null)
            {
                var cell = node?.GetValueKind() == JsonValueKind.String
                    ? JsonSerializer.SerializeToElement(Redact((string)node!, secretValues))
                    : JsonSerializer.SerializeToElement(node);
                why = declared!.CellRefusal($"{name}.{column}", cell) is { } refusal ? $"{refusal} It was dropped." : null;
                if (why is null) accepted[column] = cell;
            }

            if (why is not null) await WarnAsync($"A `row.status` cell was not written: {why}");
        }

        if (accepted.Count == 0) return;

        await using (await PluginTableWrites.LockAsync(member, ct))
        {
            var bound = await store.ForAsync(member, ct);
            var stored = bound.Config.TryGetValue(name, out var rows) ? rows : (JsonElement?)null;

            // A PERSON MAY HAVE DELETED THE ROW since: then there is nothing to write.
            if (table.Row(stored, rowName) is null) return;

            var updated = new JsonArray();
            foreach (var row in stored!.Value.EnumerateArray())
            {
                var node = JsonNode.Parse(row.GetRawText())!;
                if (string.Equals(table.RowName(row), rowName, StringComparison.OrdinalIgnoreCase) && node is JsonObject cells)
                {
                    foreach (var (column, value) in accepted) cells[column] = JsonNode.Parse(value.GetRawText());
                }

                updated.Add(node);
            }

            var config = new Dictionary<string, JsonElement>(bound.Config, StringComparer.Ordinal)
            {
                [name] = JsonSerializer.SerializeToElement(updated),
            };

            await store.SaveAsync(member, bound with { Config = config }, ct);
        }

        async Task WarnAsync(string sentence)
        {
            if (!gathered.Dropped.Add("row.status " + sentence)) return;
            await reports.ProgressAsync(member, Redact(sentence, secretValues), ct);
        }
    }

    /// <summary>
    /// A <c>publish</c> record: <c>{"t":"publish","type":"&lt;suffix&gt;","payload":{...}}</c>.
    ///
    /// <list type="bullet">
    /// <item>THE TYPE IS ALWAYS <c>plugin.&lt;id&gt;.&lt;suffix&gt;</c>, and only a suffix the manifest's
    /// <c>events.publishes</c> declares. The record may name the bare suffix or the full type; anything
    /// else - an undeclared suffix, another plugin's type, a platform type such as
    /// <c>agentContainer.completed</c> - is DROPPED, with one warning row per type per run.</item>
    /// <item>The SOURCE is the member and the CAUSATION the message the run is handling, stamped by
    /// <see cref="IMemberReports.PublishAsync"/>, so the event joins the workflow.</item>
    /// <item>The PAYLOAD is a JSON object, every string in it redacted like any other text the plugin
    /// writes, and no larger than the member's <see cref="ArtifactLimits.ExcerptChars"/>.</item>
    /// </list>
    /// </summary>
    private async Task PublishAsync(
        ContainerId member, PluginManifest manifest, ArtifactLimits limits, JsonObject record, Gathered gathered,
        ValueRedactor secretValues, CancellationToken ct)
    {
        var named = Words(record, "type") ?? "(no type)";
        var own = EventCatalog.PluginType(manifest.Id, "");
        var suffix = named.StartsWith(own, StringComparison.Ordinal) ? named[own.Length..] : named;

        if (!manifest.Publishes.Any(e => e.Type == suffix))
        {
            await DropAsync(named, $"plugin '{manifest.Id}' declares no such event. It may publish: "
                + (manifest.Publishes.Count == 0 ? "nothing" : string.Join(", ", manifest.Publishes.Select(e => e.Type))) + ".");
            return;
        }

        var type = EventCatalog.PluginType(manifest.Id, suffix);

        if (record["payload"] is not (null or JsonObject))
        {
            await DropAsync(type, "its `payload` is not a JSON object.");
            return;
        }

        var payload = (JsonObject?)record["payload"]?.DeepClone() ?? new JsonObject();
        if (RedactPayload(payload, secretValues) is { } refusal)
        {
            await DropAsync(type, refusal);
            return;
        }

        var text = payload.ToJsonString();

        if (limits.ExcerptChars > 0 && text.Length > limits.ExcerptChars)
        {
            await DropAsync(type, $"its payload is {text.Length} characters, over this member's limit of {limits.ExcerptChars}.");
            return;
        }

        var outcome = await reports.PublishAsync(member, type, text, ct);
        if (!outcome.Accepted) await DropAsync(type, outcome.Refusal ?? "it was refused.");

        // ONE WARNING ROW PER TYPE PER RUN: a plugin looping on a bad publish is one line, not a flood.
        async Task DropAsync(string what, string why)
        {
            if (!gathered.Dropped.Add(what)) return;
            await reports.ProgressAsync(member, Redact($"A `publish` of `{what}` was dropped: {why}", secretValues), ct);
        }
    }

    /// <summary>
    /// A <c>site.put</c> or <c>site.delete</c> record:
    /// <c>{"t":"site.put","site":"&lt;site&gt;","collection":"&lt;c&gt;","id":"&lt;id&gt;","doc":&lt;json&gt;}</c>.
    ///
    /// <list type="bullet">
    /// <item>A SITE OF THE PLUGIN'S OWN TEAM ONLY: the site is looked up in the member's team, with the
    /// member as a bound actor, so another team's site answers as a missing one does. A record naming
    /// a <c>team</c> other than the member's is dropped.</item>
    /// <item>The document is redacted like every other text the plugin writes, and a site, collection
    /// or id that redaction would change drops the record.</item>
    /// <item>A refusal - a limit, a name, no such site - is one warning row per kind per run, as a
    /// dropped <c>publish</c> is.</item>
    /// </list>
    /// </summary>
    private async Task SiteRecordAsync(
        ContainerId member, JsonObject record, Gathered gathered, ValueRedactor secretValues, CancellationToken ct)
    {
        var kind = (string)record["t"]!;

        if (sites is null)
        {
            await DropAsync("this Host serves no sites.");
            return;
        }

        if (record["team"] is { } named
            && !(named.GetValueKind() == JsonValueKind.String
                && string.Equals((string?)named, member.Team, StringComparison.OrdinalIgnoreCase)))
        {
            await DropAsync("a plugin writes only its own team's sites.");
            return;
        }

        var site = Words(record, "site");
        var collection = Words(record, "collection");
        var id = Words(record, "id");

        if (site is null || collection is null || id is null)
        {
            await DropAsync("it needs `site`, `collection` and `id`.");
            return;
        }

        if (Redact(site, secretValues) != site || Redact(collection, secretValues) != collection || Redact(id, secretValues) != id)
        {
            await DropAsync("its site, collection or id holds a secret.");
            return;
        }

        var actor = SiteActor.Member(member);
        var service = sites;

        if (kind == "site.delete")
        {
            var deleted = await service.DeleteDocumentAsync(member.Team, site, collection, id, actor, ct);
            if (deleted.Refusal is { } refusal) await DropAsync(refusal);
            return;
        }

        if (!record.ContainsKey("doc"))
        {
            await DropAsync("it needs `doc`, the document's JSON.");
            return;
        }

        var doc = record["doc"]?.DeepClone();

        if (doc is JsonValue scalar && scalar.TryGetValue<string>(out var text))
        {
            doc = JsonValue.Create(Redact(text, secretValues));
        }
        else if (RedactPayload(doc, secretValues) is { } secret)
        {
            await DropAsync(secret.Replace("its payload", "its document", StringComparison.Ordinal));
            return;
        }

        var written = await service.PutDocumentAsync(
            member.Team, site, collection, id, doc?.ToJsonString() ?? "null", actor, ct);
        if (written.Refusal is { } why) await DropAsync(why);

        async Task DropAsync(string why)
        {
            if (!gathered.Dropped.Add(kind)) return;
            await reports.ProgressAsync(member, Redact($"A `{kind}` record was dropped: {why}", secretValues), ct);
        }
    }

    /// <summary>
    /// Every string value in <paramref name="node"/> through <see cref="Redact"/>, in place, so the
    /// payload stays the JSON object it was - or why the publish is REFUSED instead:
    /// <list type="bullet">
    /// <item>a PROPERTY NAME that <see cref="Redact"/> would change. Renaming a key to the
    /// placeholder would change what triggers and <c>{event.*}</c> tokens read, and could collide
    /// with a sibling, so the publish is dropped rather than quietly reshaped;</item>
    /// <item>a NUMBER (or <c>true</c>/<c>false</c>/<c>null</c>) whose JSON text <see cref="Redact"/>
    /// would change. Replacing it with the placeholder would change its JSON type under a
    /// subscriber, so it is refused the same way.</item>
    /// </list>
    /// The reason names neither the key nor the value.
    /// </summary>
    private static string? RedactPayload(JsonNode? node, ValueRedactor secretValues)
    {
        switch (node)
        {
            case JsonObject body:
                foreach (var (key, child) in body.ToList())
                {
                    if (Redact(key, secretValues) != key) return "a property name in its payload holds a secret.";
                    if (child is JsonValue value && value.TryGetValue<string>(out var text)) body[key] = Redact(text, secretValues);
                    else if (RedactPayload(child, secretValues) is { } refusal) return refusal;
                }

                break;

            case JsonArray items:
                for (var i = 0; i < items.Count; i++)
                {
                    if (items[i] is JsonValue value && value.TryGetValue<string>(out var text)) items[i] = Redact(text, secretValues);
                    else if (RedactPayload(items[i], secretValues) is { } refusal) return refusal;
                }

                break;

            case JsonValue scalar:
                var json = scalar.ToJsonString();
                if (Redact(json, secretValues) != json || EqualsASecretNumber(scalar, secretValues))
                {
                    return "a number in its payload matches a secret.";
                }

                break;
        }

        return null;
    }

    /// <summary>
    /// R1-c: the text match above misses a secret NUMBER written another way - <c>2.0261234e7</c> for
    /// <c>20261234</c> - which a subscriber reads as the same value. So a number is also compared by
    /// value, as <see cref="decimal"/>, with each bound secret that parses as one (and its negation,
    /// which the text match would also catch as <c>-20261234</c>). A number outside decimal's range
    /// cannot equal a secret that is inside it.
    /// </summary>
    private static bool EqualsASecretNumber(JsonValue scalar, ValueRedactor secretValues)
    {
        if (scalar.GetValueKind() != JsonValueKind.Number || !scalar.TryGetValue<decimal>(out var value)) return false;

        foreach (var secret in secretValues.Forms.Concat(secretValues.TokenForms))
        {
            if (decimal.TryParse(secret, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                && decimal.Abs(value) == decimal.Abs(number))
            {
                return true;
            }
        }

        return false;
    }

    private static string? Words(JsonObject record, string field) =>
        record[field] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;

    /// <summary>
    /// EVERY TEXT A PLUGIN WRITES passes here before it is stored or reported: each bound secret value
    /// replaced by <see cref="ValueRedactor"/> (in any case, in its JSON-escaped forms, longest first;
    /// none is shorter than <see cref="MinimumSecretLength"/>; see there), then
    /// <see cref="DiagnosticRedaction"/>'s named-value and credential-shape rules, unbounded, since
    /// this is output and not a diagnostic.
    /// </summary>
    internal static string Redact(string text, IEnumerable<string> values) => Redact(text, ValueRedactor.For(values));

    /// <inheritdoc cref="Redact(string, IEnumerable{string})"/>
    internal static string Redact(string text, ValueRedactor values) =>
        DiagnosticRedaction.RedactWithoutLimit(values.Apply(text)) ?? text;

    /// <summary>Every spelling of the bound values that <see cref="Redact"/> replaces, longest first.</summary>
    internal static IReadOnlyList<string> RedactedForms(IEnumerable<string> values) => ValueRedactor.For(values).Forms;

    /// <summary>
    /// The connections assigned to <paramref name="team"/> (its stored id), oldest first: what Team
    /// Connections holds. Read from the team's assignments and nothing else, so a connection assigned
    /// only to another team is never among them.
    /// </summary>
    private async Task<IReadOnlyList<ConnectionRecord>> AssignedConnectionsAsync(string team, CancellationToken ct)
    {
        var list = new List<ConnectionRecord>();
        foreach (var assigned in await connections!.Store.AssignedAsync(team, ct))
        {
            if (await connections.Store.GetAsync(assigned.ConnectionId, ct) is { } connection) list.Add(connection);
        }

        return list;
    }

    /// <summary>
    /// TEAM CONNECTIONS' <c>test &lt;name&gt;</c>: the connection of that name assigned to the member's team,
    /// put through its own Test connection (<see cref="Connections.TestAsync"/>) - the same test, the same
    /// health and the same <c>connections.tested</c> row as a person's, with the member as who asked. One
    /// sentence, never a value: the test's sentence has every value it carried taken out.
    /// </summary>
    private async Task<string> TestByNameAsync(ContainerId member, string name, CancellationToken ct)
    {
        var assigned = await AssignedConnectionsAsync(member.Team, ct);
        var connection = assigned.FirstOrDefault(c => string.Equals(c.Name.Trim(), name, StringComparison.OrdinalIgnoreCase));

        if (connection is null)
        {
            return $"Test connection '{name}': no connection of that name is assigned to this team. "
                + (assigned.Count == 0
                    ? "This team has none; a person assigns them in Team settings → Connections."
                    : $"This team's connections: {string.Join(", ", assigned.Select(c => c.Name))}.");
        }

        if (ConnectionTests is null) return $"Test connection '{connection.Name}': no worker can run a test here.";

        ConnectionTestAnswer answer;
        try
        {
            answer = await connections!.TestAsync(ConnectionActor.ForMember(member), connection.Id, ConnectionTests, ct);
        }
        catch (Exception exception) when (exception is System.Data.Common.DbException or RefreshNotStoredException)
        {
            return $"Test connection '{connection.Name}': the test's result could not be recorded. Nothing was changed; try again.";
        }

        return answer.Health is { } health
            ? $"Test connection '{connection.Name}' ({connection.Kind}): {(health == ConnectionRecord.Ok ? "ok" : "needs reconnect")}. {answer.Sentence}"
            : $"Test connection '{connection.Name}' ({connection.Kind}): {answer.Error}";
    }

    /// <summary>
    /// TEAM CONNECTIONS' ANSWER REACHES THE MEMBER THAT ASKED: for each work item an agent member of the
    /// same team sent (never the Manager, which its <c>completed</c> row wakes already; never a person, the
    /// Concierge or a schedule), one instruction to that member, caused by its own tell so it lands in
    /// that member's workflow, from Team Connections, carrying the run's answer - names, kinds, health and
    /// test sentences, never a value. Best effort: the answer is still the run's <c>completed</c> row.
    /// </summary>
    private async Task AnswerAskersAsync(MemberInvocation invocation, string text, CancellationToken ct)
    {
        if (Answers is null) return;

        foreach (var item in invocation.Work)
        {
            if (!ContainerId.TryParse(item.Source, out var asker)
                || !string.Equals(asker.Team, invocation.Member.Team, StringComparison.OrdinalIgnoreCase)
                || asker.Equals(invocation.Member)
                || string.Equals(asker.Name, TeamRegistry.DefaultManagerName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var answer = $"{TeamConnectionsMember.Label} answers your request:\n{(text.Length == 0 ? "(no answer)" : text)}";
            try
            {
                await Answers.AppendAsync(
                    new NewMessage(
                        MessageTypes.InstructionFor(new ContainerId(invocation.Member.Team, asker.Name)),
                        JsonSerializer.Serialize(new Dictionary<string, object?>
                        {
                            [PayloadFields.Instruction] = answer,
                        }),
                        invocation.Member.ToString(),
                        item.Seq),
                    ct);
            }
            catch (System.Data.Common.DbException)
            {
                // Not sent: the answer is still the run's completed row, in the same workflow.
            }
        }
    }

    private sealed class Gathered
    {
        public ResultRecord? Result;

        /// <summary>Whether this run is Team Connections', whose <c>connection.test</c> records are honoured.</summary>
        public bool TeamConnections;

        /// <summary>Each connection a <c>connection.test</c> record named, in the order asked.</summary>
        public readonly List<string> TestsAsked = [];

        /// <summary>Each test's sentence, in the order asked: they follow the plugin's own output.</summary>
        public readonly List<string> Answers = [];
        public readonly StringBuilder Text = new();
        public readonly List<string> Notes = [];
        public readonly HashSet<string> Dropped = new(StringComparer.Ordinal);
    }

    /// <summary>A <c>result</c> record. <see cref="Slot"/> and <see cref="Kind"/> are its
    /// <see cref="RunConnectionFailures"/> fields, as written; only a held kind on a handed slot marks.</summary>
    private sealed record ResultRecord(bool Ok, string? Output, string? Error, bool Quiet, string? Slot = null, string? Kind = null);
}

/// <summary>A plugin member's own configuration values and secret bindings.</summary>
/// <param name="Config">Field name to value, checked against the manifest's <c>config</c>.</param>
/// <param name="Secrets">The manifest's secret name to a LOGICAL KEY, resolved through
/// <see cref="ISecretStore"/> at each run. Never a value.</param>
public sealed record PluginMemberSettings(
    IReadOnlyDictionary<string, JsonElement> Config,
    IReadOnlyDictionary<string, string> Secrets)
{
    /// <summary>The manifest's connection slot to a CONNECTION ID (see <see cref="ConnectionStore"/>).
    /// Never a token: the token is fetched, and refreshed, at each run. Only a person binds one.</summary>
    public IReadOnlyDictionary<string, string> Connections { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public static readonly PluginMemberSettings None = new(
        new Dictionary<string, JsonElement>(StringComparer.Ordinal),
        new Dictionary<string, string>(StringComparer.Ordinal));
}

/// <summary>Where a plugin member's settings are read from, per run.</summary>
public interface IPluginMemberSettings
{
    Task<PluginMemberSettings> ForAsync(ContainerId member, CancellationToken ct = default);
}

/// <summary>
/// SECRETS BY LOGICAL KEY. A plugin member's configuration names a key - <c>ACME_STORAGE_KEY</c> -
/// and this answers its value at the moment of a run, or null. Only the resolver knows where
/// values live, so changing that later changes nothing a plugin or a binding says.
/// </summary>
public interface ISecretStore
{
    string? TryGet(string logicalKey);
}

/// <summary>
/// What one run was handed of its declared reads. <paramref name="Rest"/> is the framed size of its
/// <see cref="StartRun"/> with <c>sites: []</c>; <paramref name="Budget"/> the documents' share,
/// <paramref name="FrameLimited"/> when the frame's room set it rather than the runner's budget;
/// <paramref name="DocumentBytes"/> and <paramref name="EnvelopeBytes"/> what each part added to the
/// frame. <paramref name="CutRow"/> and <paramref name="MissingRow"/> are the run's rows, or null.
/// </summary>
public sealed record PluginSiteDelivery(
    long Rest, long Budget, bool FrameLimited, long DocumentBytes, long EnvelopeBytes, string? CutRow, string? MissingRow);
