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
    private readonly Connections? connections;
    private readonly SiteService? sites;

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
        SiteService? sites = null)
    {
        this.catalog = catalog;
        this.reports = reports;
        this.worker = worker;
        this.directory = directory;
        this.runAs = runAs;
        this.settings = settings;
        this.secrets = secrets;
        this.connections = connections;
        this.sites = sites;
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
        SiteService? sites = null)
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
        this.connections = connections;
        this.sites = sites;
    }

    /// <summary>The worker plugin runs go to.</summary>
    public IRunWorker Worker => worker;

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

        var (config, configRefusal) = EffectiveConfig(manifest, bound);
        if (configRefusal is not null) return MemberResult.NotRun(configRefusal);

        var (resolvedSecrets, secretRefusal) = ResolveSecrets(manifest, bound);
        if (secretRefusal is not null) return MemberResult.NotRun(secretRefusal);

        var (grants, connectionRefusal) = await GrantConnectionsAsync(manifest, bound, ct);
        if (connectionRefusal is not null) return MemberResult.NotRun(connectionRefusal);

        // THE REDACTION SET: every bound secret AND every access token this run is handed.
        IReadOnlyList<string> redacted = [.. resolvedSecrets.Values, .. grants.Values.Select(g => g.AccessToken)];

        var request = Request(invocation, manifest, config, resolvedSecrets, grants);

        // The run's record, gathered as the lines arrive.
        var gathered = new Gathered();

        // ON THE WORKER, through the run protocol: the same launcher every member shares, over
        // MINIMAL AND ALLOW-LISTED variables, the opposite of an agent's inherited-then-scrubbed
        // environment - a plugin is given only what it is named here. Each line it prints comes back
        // here, in order, before the next is read.
        var (ended, lost) = await directory.RunProcessAsync(
            worker,
            new StartRun(
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
                    request,
                    manifest.TimeoutSeconds,
                    plugin.Directory)),
            line => OnLineAsync(invocation.Member, manifest, invocation.Context.Limits, line, gathered, redacted, ct),
            ct);

        if (lost) return MemberResult.NotRun(RunDirectory.LostRunText, FailureClasses.Interrupted);

        var outcome = ended.Process!;

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

        return new MemberResult(
            reason is null,
            ended.ExitCode,
            text,
            FailureReason: reason,
            ProcessId: ended.ProcessId,
            // A FAILURE IS NEVER QUIET, whatever the record said: ok:false, a non-zero exit, a
            // timeout and a Stop all wake as they always did.
            Quiet: reason is null && gathered.Result is { Quiet: true });
    }

    /// <summary>The request document: protocol v1's whole input, one JSON object on stdin.</summary>
    internal static string Request(
        MemberInvocation invocation, PluginManifest manifest,
        IReadOnlyDictionary<string, JsonNode?> config, IReadOnlyDictionary<string, string> resolvedSecrets,
        IReadOnlyDictionary<string, ConnectionGrant>? grants = null)
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
            ["secrets"] = new JsonObject(resolvedSecrets.Select(s => KeyValuePair.Create(s.Key, (JsonNode?)JsonValue.Create(s.Value)))),
            ["workingDirectory"] = invocation.WorkingDirectory,
            ["worktrees"] = worktrees,

            // EACH BOUND SLOT'S FRESH ACCESS TOKEN - never the refresh token, never the client secret.
            ["connections"] = new JsonObject((grants ?? new Dictionary<string, ConnectionGrant>()).Select(g => KeyValuePair.Create(g.Key, (JsonNode?)new JsonObject
            {
                ["provider"] = g.Value.Provider,
                ["account"] = g.Value.Account,
                ["accessToken"] = g.Value.AccessToken,
                ["expiresAt"] = g.Value.ExpiresAt?.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                ["scopes"] = new JsonArray([.. g.Value.Scopes.Select(scope => (JsonNode?)JsonValue.Create(scope))]),
            }))),
        };

        return document.ToJsonString() + "\n";
    }

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
                config[name] = JsonNode.Parse(value.GetRawText());
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
        PluginManifest manifest, PluginMemberSettings settings, ISecretStore? secrets, bool requireSet = true, PluginMemberSettings? stored = null)
    {
        foreach (var name in settings.Config.Keys)
        {
            if (!manifest.Config.ContainsKey(name))
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
            if (manifest.Config[name].BoundsRefusal(name, value) is { } outside) return outside;
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

        foreach (var (name, secret) in manifest.Secrets.Where(s => s.Value.Required))
        {
            if (!settings.Secrets.TryGetValue(name, out var key))
            {
                return $"Plugin '{manifest.Id}' requires the secret `{name}`: bind it to a logical key, as in \"secrets\": {{\"{name}\": \"MY_KEY\"}}.";
            }

            if (requireSet && secrets?.TryGet(key) is null)
            {
                return $"The secret `{key}` bound for `{name}` is not set on this Host. Set it with `secret set {key}` and restart the Host.";
            }
        }

        return null;
    }

    /// <summary>Each bound secret resolved by its logical key NOW, never earlier and never stored.</summary>
    private (IReadOnlyDictionary<string, string> Secrets, string? Refusal) ResolveSecrets(
        PluginManifest manifest, PluginMemberSettings bound)
    {
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (name, secret) in manifest.Secrets)
        {
            var key = bound.Secrets.GetValueOrDefault(name);
            var value = key is null || secrets is null ? null : secrets.TryGet(key);

            if (value is { Length: < MinimumSecretLength })
            {
                return (resolved, ShortSecret(key!, name));
            }

            if (value is not null)
            {
                resolved[name] = value;
            }
            else if (secret.Required)
            {
                return (resolved, key is null
                    ? $"This member binds no key for the plugin's required secret `{name}`."
                    : $"The secret `{key}` that this member binds for `{name}` is not set on this Host. Set it with `secret set {key}` and restart the Host.");
            }
        }

        return (resolved, null);
    }

    /// <summary>
    /// Each connection slot's fresh access token, fetched NOW (and refreshed when it is within 5
    /// minutes of expiry), or the sentence the run is blocked with: a required slot unbound, a
    /// connection gone, needing reconnect, or lacking a scope the slot asks for.
    /// </summary>
    private async Task<(IReadOnlyDictionary<string, ConnectionGrant> Grants, string? Refusal)> GrantConnectionsAsync(
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
                        + "A person binds one in the member's settings.");
                }

                continue;
            }

            if (connections is null)
            {
                return (grants, $"This Host holds no connections, so slot `{slot}` could not be given its account.");
            }

            if (await connections.Store.GetAsync(connectionId, ct) is { } record
                && Connections.ScopeRefusal(slot, declared, record) is { } scopeRefusal)
            {
                return (grants, scopeRefusal.Error);
            }

            ConnectionGrant? grant;
            string? refusal;

            try
            {
                (grant, refusal) = await connections.GrantAsync(connectionId, slot, ct);
            }
            catch (RefreshNotStoredException exception)
            {
                // A REFRESHED TOKEN THAT COULD NOT BE STORED IS NOT HANDED OUT: a rotated refresh token
                // lost here would strand the connection, so the run does not start.
                return (grants, $"The connection bound for slot `{slot}` was refreshed but could not be stored "
                    + $"({exception.SqliteErrorCode}), so this run did not start. The next run tries again.");
            }
            catch (Microsoft.Data.Sqlite.SqliteException exception)
            {
                return (grants, $"The connection bound for slot `{slot}` could not be read or updated "
                    + $"({exception.SqliteErrorCode}), so this run did not start. The next run tries again.");
            }

            if (refusal is not null) return (grants, refusal);

            grants[slot] = grant!;
        }

        return (grants, null);
    }

    private static string ShortSecret(string key, string name) =>
        $"The secret `{key}` bound for `{name}` is shorter than {MinimumSecretLength} characters, so it "
        + "could not be kept out of what this plugin writes. Set a longer value and restart the Host.";

    private async Task OnLineAsync(
        ContainerId member, PluginManifest manifest, ArtifactLimits limits, string line, Gathered gathered,
        IEnumerable<string> secretValues, CancellationToken ct)
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
                    record["quiet"]?.GetValueKind() == JsonValueKind.True);
                break;

            case "publish":
                await PublishAsync(member, manifest, limits, record!, gathered, secretValues, ct);
                break;

            case "site.put":
            case "site.delete":
                await SiteRecordAsync(member, record!, gathered, secretValues, ct);
                break;

            default:
                // NOT A RECORD, OR NOT ONE THIS VERSION KNOWS: kept as text, never an error.
                if (gathered.Text.Length > 0) gathered.Text.Append('\n');
                gathered.Text.Append(line);
                break;
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
        IEnumerable<string> secretValues, CancellationToken ct)
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
        ContainerId member, JsonObject record, Gathered gathered, IEnumerable<string> secretValues, CancellationToken ct)
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
    private static string? RedactPayload(JsonNode? node, IEnumerable<string> secretValues)
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
    private static bool EqualsASecretNumber(JsonValue scalar, IEnumerable<string> secretValues)
    {
        if (scalar.GetValueKind() != JsonValueKind.Number || !scalar.TryGetValue<decimal>(out var value)) return false;

        foreach (var secret in secretValues)
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
    internal static string Redact(string text, IEnumerable<string> values) =>
        DiagnosticRedaction.RedactWithoutLimit(ValueRedactor.For(values).Apply(text)) ?? text;

    /// <summary>Every spelling of the bound values that <see cref="Redact"/> replaces, longest first.</summary>
    internal static IReadOnlyList<string> RedactedForms(IEnumerable<string> values) => ValueRedactor.For(values).Forms;

    private sealed class Gathered
    {
        public ResultRecord? Result;
        public readonly StringBuilder Text = new();
        public readonly List<string> Notes = [];
        public readonly HashSet<string> Dropped = new(StringComparer.Ordinal);
    }

    private sealed record ResultRecord(bool Ok, string? Output, string? Error, bool Quiet);
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
