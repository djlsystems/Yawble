using System.Text.Json.Nodes;
using Yawble.Core.Ipc;
using Yawble.Core.Launch;
using Yawble.Core.Sandboxing;
using Yawble.Core.Policy;
using Yawble.Core.Provenance;

namespace Yawble.Core.Agents;

/// <summary>
/// What the shell calls about agent CLIs, their models and their runs (core side). Every method takes a <c>cli</c> (claude, grok, codex,
/// copilot, antigravity) unless noted; nothing returns a secret, and the environment is only ever NAMES.
/// <list type="bullet">
/// <item><c>agents.commandLine</c> {cli, model?} or {team, member} -> {cli, model: {value, source}, headless: {argv[], env[]}, interactive: {argv[], env[]}}.
/// The argv is the launched one with &lt;prompt&gt;, &lt;session id&gt;, &lt;workspace&gt;/.yawble/mcp.json placeholders. Each kind also has
/// launch[] (the executable / launcher tokens) and groups[] ({tokens[], locked, tail, removed, reason}: the built-in arguments,
/// from the same AgentPresets.BuiltIn the launch uses). A draft (an agent being edited) is {cli, model?, argsBefore?, argsAfter?,
/// removedArgs?}: with any of the three lists, the base CLI is described with those changes (validated first: bad_params).</item>
/// <item><c>agents.models</c> {cli} -> {models[], source: "app"}: the list the app carries. No CLI is ever run for it.</item>
/// <item><c>agents.defaultModels</c> -> {claude: "", grok: "gpt-...", ...} ("" = the CLI's default).</item>
/// <item><c>agents.setDefaultModel</c> {cli, model} -> {cli, previous, model}. The person or the Concierge only.</item>
/// <item><c>agents.resolveModel</c> {team, member} or {cli, model?} -> {model, source: member|agent|cli, label} (label e.g. "Agent default (opus)").</item>
/// <item><c>agents.versionStatus</c> {cli} -> {cli, detected, lastRun, changed}.</item>
/// <item><c>agents.checkForUpdates</c> {cli} -> {opened, command[]}: opens a visible terminal running the CLI's own update command. The person's window only.</item>
/// <item><c>agents.runRecords</c> {team?, member?, limit?} -> runs, newest first: agent, model, modelSource, cliVersion, argv[], startedAt, endedAt (UTC).</item>
/// </list>
/// </summary>
public sealed class AgentInfoRpc(
    SandboxLauncher launcher,
    AgentDefaultsStore defaults,
    ModelCatalog catalog,
    ICliDetector clis,
    RunRecordStore records,
    IMemberDirectory members,
    IUpdateTerminal terminal,
    Profiles.IAgentResolver? agents = null,
    Profiles.AgentChangeLog? changes = null)
{
    public static bool Handles(string method) => method is "agents.commandLine" or "agents.models" or "agents.defaultModels"
        or "agents.setDefaultModel" or "agents.resolveModel" or "agents.versionStatus" or "agents.checkForUpdates" or "agents.install" or "agents.runRecords";

    public async Task<JsonNode?> DispatchAsync(string method, JsonObject? p, CancellationToken ct)
    {
        switch (method)
        {
            case "agents.commandLine":
            {
                AgentCli cli;
                ModelResolution resolved;
                ArgvCustomization? custom;
                string agent;
                if (Draft(p) is { } draft)
                {
                    // An agent being edited: its base CLI with the draft's changes (validated first) and the draft's model.
                    cli = Cli(p);
                    var problems = AgentPresets.ValidateCustomization(cli, draft);
                    if (problems.Count > 0) throw new RpcException("bad_params", string.Join("; ", problems));
                    resolved = AgentModels.Resolve(null, OptModel(p));
                    custom = draft;
                    agent = cli.Id();
                }
                else
                {
                    var (c, member, memberModel) = Subject(p);
                    cli = c;
                    resolved = Resolve(cli, member, memberModel);
                    custom = member?.Custom;
                    agent = member?.Agent ?? cli.Id();
                }
                var lines = await launcher.DescribeAsync(cli, resolved.Model, custom, ct);
                return new JsonObject
                {
                    ["cli"] = cli.Id(),
                    ["agent"] = agent,
                    ["model"] = Model(resolved),
                    ["headless"] = Lines(lines.Headless, lines.HeadlessGroups),
                    ["interactive"] = Lines(lines.Interactive, lines.InteractiveGroups),
                };
            }
            case "agents.models":
            {
                var listing = catalog.List(Cli(p)); // the app's own list: no CLI is run
                return new JsonObject { ["models"] = new JsonArray(listing.Models.Select(m => (JsonNode?)m).ToArray()), ["source"] = listing.Source };
            }
            case "agents.defaultModels":
            {
                var o = new JsonObject();
                foreach (var (cli, model) in defaults.All()) o[cli.Id()] = model;
                return o;
            }
            case "agents.setDefaultModel":
            {
                if (Actor.Current is not { Via: Actor.App or Actor.Concierge })
                    throw new RpcException("forbidden", "an agent's default model is changed by the person (the app's window) or the Concierge");
                var cli = BuiltInCli(p);
                var model = OptModel(p) ?? "";
                try
                {
                    var previous = defaults.Set(cli, model);
                    // The change log: the same row agentProfiles.setDefaultModel writes (one per real change).
                    changes?.RecordDefaultModelChange(Profiles.AgentProfileStore.BuiltInName(cli), previous, model.Trim(),
                        Actor.Current?.Via == Actor.Concierge ? Profiles.AgentChangeLog.HowConciergeTool : Profiles.AgentChangeLog.HowSettings);
                    return new JsonObject { ["cli"] = cli.Id(), ["previous"] = previous, ["model"] = model.Trim() };
                }
                catch (ArgumentException e) { throw new RpcException("bad_params", e.Message); }
            }
            case "agents.resolveModel":
            {
                var (cli, member, memberModel) = Subject(p);
                return Model(Resolve(cli, member, memberModel));
            }
            case "agents.versionStatus":
            {
                var cli = Cli(p);
                var detected = (await clis.DetectAllAsync(ct)).FirstOrDefault(d => d.CliId == cli.Id())?.Version;
                var last = records.LastVersion(cli.Id());
                return new JsonObject
                {
                    ["cli"] = cli.Id(), ["detected"] = detected, ["lastRun"] = last,
                    ["changed"] = detected is not null && last is not null && !string.Equals(detected, last, StringComparison.Ordinal),
                };
            }
            case "agents.checkForUpdates":
            {
                if (Actor.Current is not { Via: Actor.App })
                    throw new RpcException("forbidden", "an update is started by the person, in the app's window on this computer");
                var cli = Cli(p);
                var found = (await clis.DetectAllAsync(ct)).FirstOrDefault(d => d.CliId == cli.Id());
                if (found is not { Found: true, ExecutablePath: { } exe })
                    throw new RpcException("unavailable", $"{cli.Id()} is not installed on this computer");
                var command = UpdateCommands.For(cli, exe) ?? throw new RpcException("unavailable", $"the app does not know an update command for {cli.Id()}");
                try { terminal.Open($"Update {cli.Id()}", command); }
                catch (Exception e) when (e is PlatformNotSupportedException or System.ComponentModel.Win32Exception)
                {
                    throw new RpcException("unavailable", e.Message);
                }
                return new JsonObject { ["opened"] = true, ["command"] = new JsonArray(command.Select(c => (JsonNode?)c).ToArray()) };
            }
            case "agents.install":
            {
                if (Actor.Current is not { Via: Actor.App })
                    throw new RpcException("forbidden", "an agent CLI is installed by the person, in the app's window on this computer");
                var cli = Cli(p);
                var found = (await clis.DetectAllAsync(ct)).FirstOrDefault(d => d.CliId == cli.Id());
                if (found is { Found: true })
                    throw new RpcException("unavailable", $"{cli.Id()} is already installed ({found.ExecutablePath}); use Update");
                var command = InstallCommands.For(cli)
                    ?? throw new RpcException("unavailable", $"{cli.Id()} has no install command; install it from its maker's site");
                try { terminal.Open($"Install {cli.Id()}", command); }
                catch (Exception e) when (e is PlatformNotSupportedException or System.ComponentModel.Win32Exception)
                {
                    throw new RpcException("unavailable", e.Message);
                }
                return new JsonObject { ["opened"] = true, ["command"] = new JsonArray(command.Select(c => (JsonNode?)c).ToArray()) };
            }
            case "agents.runRecords":
            {
                // A cloud viewer is not shown the window's own agent Terminal / Test sessions (team _agent-test), asked for or not.
                var hideAgentTests = Actor.Current?.Via == Actor.Cloud;
                var rows = records.Recent(p?["team"]?.GetValue<string>(), p?["member"]?.GetValue<string>(), p?["limit"]?.GetValue<int>() ?? 100,
                    hideAgentTests ? AgentTestSessions.Team : null);
                return new JsonArray(rows.Select(r => (JsonNode?)new JsonObject
                {
                    ["runKey"] = r.RunKey, ["team"] = r.Team, ["member"] = r.Member, ["cli"] = r.Cli, ["kind"] = r.Kind,
                    ["agent"] = r.Agent, ["model"] = r.Model, ["modelSource"] = r.ModelSource, ["cliVersion"] = r.CliVersion,
                    ["argv"] = r.Argv is null ? null : JsonNode.Parse(r.Argv),
                    ["startedAt"] = r.StartedAt, ["endedAt"] = r.EndedAt, ["endClass"] = r.EndClass,
                }).ToArray());
            }
            default:
                throw new RpcException("unknown_method", $"no method named '{method}'");
        }
    }

    /// <summary>The CLI asked about: a built-in's id, or a custom agent's name (its base CLI).</summary>
    private AgentCli Cli(JsonObject? p)
    {
        try { return Profiles.AgentResolvers.Resolve(agents, p?["cli"]?.GetValue<string>() ?? "").Cli; }
        catch (ArgumentException e) { throw new RpcException("bad_params", e.Message); }
    }

    /// <summary>A built-in's id only: an agent's default model is set per built-in (a custom agent's is saved with the agent).</summary>
    private static AgentCli BuiltInCli(JsonObject? p)
    {
        try { return AgentClis.Parse(p?["cli"]?.GetValue<string>() ?? ""); }
        catch (ArgumentException e) { throw new RpcException("bad_params", e.Message); }
    }

    /// <summary>The agent asked about: a member's (team + member) or a bare CLI with an optional model.</summary>
    private (AgentCli Cli, LaunchMember? Member, string? Model) Subject(JsonObject? p)
    {
        var team = p?["team"]?.GetValue<string>();
        var name = p?["member"]?.GetValue<string>();
        if (team is not null && name is not null)
        {
            var m = members.Find(team, name) ?? throw new RpcException("not_found", $"no member {team}/{name}");
            return (m.Cli, m, m.Model);
        }
        // A bare cli may be a custom agent's name: it brings its argv changes and default model like a member of it.
        var asked = p?["cli"]?.GetValue<string>() ?? "";
        try
        {
            var agent = Profiles.AgentResolvers.Resolve(agents, asked);
            var model = OptModel(p);
            return (agent.Cli, agent.Custom ? new LaunchMember(0, "", "", agent.Cli, Model: model, Agent: agent.Name, Custom: agent.Argv, AgentModel: agent.DefaultModel) : null, model);
        }
        catch (ArgumentException e) { throw new RpcException("bad_params", e.Message); }
    }

    private ModelResolution Resolve(AgentCli cli, LaunchMember? member, string? memberModel) =>
        AgentModels.Resolve(memberModel, member?.Agent is not null ? member.AgentModel : defaults.Get(cli));

    private static JsonObject Model(ModelResolution r) => new()
    {
        ["model"] = r.Model, ["source"] = r.Source,
        ["label"] = r.Source switch { ModelSources.Agent => $"Agent default ({r.Model})", ModelSources.Cli => "The CLI's default", _ => r.Model },
    };

    /// <summary>The optional "model" parameter: a string or absent; anything else is bad_params.</summary>
    private static string? OptModel(JsonObject? p) => p?["model"] switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => throw new RpcException("bad_params", "'model' is a string"),
    };

    /// <summary>
    /// A draft's argv changes (argsBefore / argsAfter / removedArgs), or null when none of the three lists is given.
    /// </summary>
    private static ArgvCustomization? Draft(JsonObject? p)
    {
        if (p is null || !(p.ContainsKey("argsBefore") || p.ContainsKey("argsAfter") || p.ContainsKey("removedArgs"))) return null;
        return new ArgvCustomization(Strings(p, "argsBefore"), Strings(p, "argsAfter"), Strings(p, "removedArgs"));
    }

    private static List<string> Strings(JsonObject p, string key) => p[key] switch
    {
        null => [],
        JsonArray a => [.. a.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : throw new RpcException("bad_params", $"'{key}' is a list of strings"))],
        _ => throw new RpcException("bad_params", $"'{key}' is a list of strings"),
    };

    private static JsonObject Lines(CommandLineDescription d, CommandLineGroups g) => new()
    {
        ["argv"] = new JsonArray(d.Argv.Select(a => (JsonNode?)a).ToArray()),
        ["env"] = new JsonArray(d.EnvNames.Select(a => (JsonNode?)a).ToArray()),
        ["launch"] = new JsonArray(g.Launch.Select(a => (JsonNode?)a).ToArray()),
        ["groups"] = new JsonArray(g.Groups.Select(x => (JsonNode?)new JsonObject
        {
            ["tokens"] = new JsonArray(x.Tokens.Select(t => (JsonNode?)t).ToArray()),
            ["locked"] = x.Locked, ["tail"] = x.Tail, ["removed"] = x.Removed, ["reason"] = x.Reason,
        }).ToArray()),
    };
}
