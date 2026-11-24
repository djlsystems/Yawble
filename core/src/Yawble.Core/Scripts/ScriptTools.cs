using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Yawble.Core.Bus;

namespace Yawble.Core.Scripts;

/// <summary>What a script tool needs from the person before it runs (B135, scripts-design.md section 10).</summary>
public enum ScriptToolApproval
{
    /// <summary>Never: reading, building, checking and dry runs.</summary>
    None,

    /// <summary>Always the person's yes (adding a script member).</summary>
    PersonsYes,

    /// <summary>The person's yes for the first live (dry_run false) run of a script on a team; dry runs and later live runs need none.</summary>
    FirstLiveRun,
}

/// <summary>One script tool: its name, whether it changes something, whether only the person at this computer may use it
/// (by name; a live run is refused over the Cloud Link by its arguments, see <see cref="ScriptTools.RemoteRefused"/>), and its approval class.</summary>
public sealed record ScriptToolInfo(string Name, bool Changes, bool LocalOnly, ScriptToolApproval Approval);

/// <summary>A call that waits for the person's yes: the exact text to show them, and the key the yes is bound to (the same
/// arguments give the same key; a change in what would be done gives another).</summary>
public sealed record ScriptToolYes(string Plan, string Key);

/// <summary>
/// The script and schema tools (B135) for a chat that builds scripts for the person: the Concierge, and the companion's tools.
/// For every tool its name, its MCP Tool definition, its call onto the scripts.* / schemas.* RPC (<paramref name="ui"/>, the
/// person's own buttons, so the same rules and refusals apply) and its approval class. Teams and members are given by name.
/// The approval itself (showing the plan, the person's yes in a later turn) is the caller's confirmation flow: ask
/// <see cref="YesNeededAsync"/> first, then <see cref="CallAsync"/> once the person said yes.
/// </summary>
public sealed class ScriptTools(BusEngine bus, Func<string, JsonObject?, CancellationToken, Task<JsonNode?>> ui)
{
    public const string SchemasList = "schemas_list", SchemaGet = "schema_get", SchemaSave = "schema_save",
        ScriptsList = "scripts_list", ScriptGet = "script_get", ScriptSave = "script_save", ScriptCheck = "script_check",
        ScriptTestRun = "script_test_run", ScriptRuns = "script_runs", ScriptRun = "script_run", ScriptMemberAdd = "script_member_add";

    /// <summary>Every script tool with its class.</summary>
    public static readonly IReadOnlyList<ScriptToolInfo> All =
    [
        new(SchemasList, false, false, ScriptToolApproval.None),
        new(SchemaGet, false, false, ScriptToolApproval.None),
        new(SchemaSave, true, false, ScriptToolApproval.None),
        new(ScriptsList, false, false, ScriptToolApproval.None),
        new(ScriptGet, false, false, ScriptToolApproval.None),
        new(ScriptSave, true, false, ScriptToolApproval.None),
        new(ScriptCheck, false, false, ScriptToolApproval.None),
        new(ScriptTestRun, true, false, ScriptToolApproval.FirstLiveRun),
        new(ScriptRuns, false, false, ScriptToolApproval.None),
        new(ScriptRun, false, false, ScriptToolApproval.None),
        new(ScriptMemberAdd, true, true, ScriptToolApproval.PersonsYes),
    ];

    /// <summary>The tools' names.</summary>
    public static readonly IReadOnlyList<string> Names = [.. All.Select(t => t.Name)];

    /// <summary>The tools that change something (a save, a run, a new member): not for a turn the person did not start.</summary>
    public static readonly IReadOnlyList<string> Acting = [.. All.Where(t => t.Changes).Select(t => t.Name)];

    /// <summary>The tools only for the person at this computer, by name (not over the Cloud Link).</summary>
    public static readonly IReadOnlyList<string> LocalOnly = [.. All.Where(t => t.LocalOnly).Select(t => t.Name)];

    /// <summary>Whether <paramref name="name"/> is one of these tools.</summary>
    public static bool Handles(string name) => Names.Contains(name);

    /// <summary>The approval class of a tool (<see cref="ScriptToolApproval.None"/> for a name that is not one).</summary>
    public static ScriptToolApproval ApprovalOf(string name) => All.FirstOrDefault(t => t.Name == name)?.Approval ?? ScriptToolApproval.None;

    /// <summary>Whether this call is a live run: script_test_run with dry_run false (absent means a dry run).</summary>
    public static bool IsLiveRun(string name, JsonObject args) => name == ScriptTestRun && !DryRun(args);

    /// <summary>Whether a turn over the Cloud Link is refused this call: a <see cref="LocalOnly"/> tool, or a live run (a dry run stays allowed).</summary>
    public static bool RemoteRefused(string name, JsonObject args) => LocalOnly.Contains(name) || IsLiveRun(name, args);

    /// <summary>The tools' MCP definitions (tools/list entries), a fresh copy each call.</summary>
    public static JsonArray Definitions() =>
    [
        Tool(SchemasList, "The schemas scripts use for their data (built-ins first): name, latest version, description."),
        Tool(SchemaGet, "One schema in full (its JSON Schema), the latest version or the one given.",
            P("name", "string", "The schema's name.", true), P("version", "integer", "A version (default the latest).", false)),
        Tool(SchemaSave, "Save a schema (a new one, or a new version of one). It is checked against every saved script first: a change that would break " +
                         "a step's field references is not saved, and the breaks (script, version, step, field) are returned so you can fix them.",
            P("name", "string", "The schema's name.", true), P("description", "string", "What it describes (optional).", false),
            P("schema", "object", "The schema: a JSON Schema subset (an object, or its JSON text).", true)),
        Tool(ScriptsList, "The script library: each script's key, name, latest and runnable version, whether the latest is a draft, and the members that use it."),
        Tool(ScriptGet, "One script version in full: its document (to edit and save again) and its check result.",
            P("script", "string", "The script's key or id.", true), P("version", "integer", "A version (default the latest).", false)),
        Tool(ScriptSave, "Save a script document as a new version and check it. One that fails its check is kept as a draft no member runs; " +
                         "fix each issue (step, code, message) and save again.",
            P("document", "object", "The whole script document (an object, or its JSON text).", true)),
        Tool(ScriptCheck, "Check a script document without saving it: every issue with its step, code and message.",
            P("document", "object", "The whole script document (an object, or its JSON text).", true)),
        Tool(ScriptTestRun, "Test-run a script on a team (a draft too) and get its steps and log. A dry run (the default) sends and writes nothing; " +
                            "always dry-run first. A live run (dry_run false) really sends and writes: the first live run of a script on a team needs the " +
                            "person's yes (the first call shows what will run and returns a token), and live runs are only for the person at this computer.",
            P("script", "string", "The script's key or id.", true), P("team", "string", "The team's name (whose integrations the bindings name).", true),
            P("version", "integer", "A version (default the latest).", false), PAny("input", "The run's input (the trigger's data), optional."),
            P("settings", "object", "Setting values by name (missing ones take their default).", false),
            P("bindings", "object", "Each requires name of the script mapped to one of the team's integrations by name.", false),
            P("dry_run", "boolean", "Default true. false for a live run.", false), Confirm()),
        Tool(ScriptRuns, "A team's script runs, newest first (test runs too): status, version, trigger, failed step and reason.",
            P("team", "string", "The team's name.", true), P("member", "string", "Only this script member's runs (its name).", false),
            P("limit", "integer", "How many (default 20, at most 500).", false)),
        Tool(ScriptRun, "One script run in full: its steps (input, output, status) and log.",
            P("id", "integer", "The run's id (script_runs or script_test_run).", true)),
        Tool(ScriptMemberAdd, "Add a script member to a team: it runs the script when its triggers fire or it is told. Needs the person's yes " +
                              "(the first call shows what it adds and returns a token); only for the person at this computer. Dry-run the script first.",
            P("team", "string", "The team's name.", true), P("name", "string", "The new member's name.", true),
            P("script", "string", "The script's key or id.", true),
            P("version", "integer", "Pin this clean version (default: the latest clean one, following new versions).", false),
            P("settings", "object", "Setting values by name.", false),
            P("bindings", "object", "Each requires name of the script mapped to one of the team's integrations by name.", false), Confirm()),
    ];

    /// <summary>
    /// What the person must say yes to before this call runs, or null when it runs without (everything but a member add and a
    /// script's first live run on a team). Reads only.
    /// </summary>
    public async Task<ScriptToolYes?> YesNeededAsync(string name, JsonObject args, CancellationToken ct)
    {
        switch (ApprovalOf(name))
        {
            case ScriptToolApproval.PersonsYes when name == ScriptMemberAdd:
            {
                var team = await TeamAsync(Required(args, "team"), ct);
                var member = Required(args, "name").Trim();
                var (key, _, runnable) = await ScriptAsync(Required(args, "script"), null, ct);
                var pinned = Int(args, "version");
                var settings = Obj(args, "settings") ?? [];
                var bindings = Obj(args, "bindings") ?? [];
                var runs = pinned is { } v ? $"version {v} (pinned)" : $"the latest clean version (now {(runnable is { } r ? r.ToString(CultureInfo.InvariantCulture) : "none")}; it follows new ones)";
                var plan = $"Add script member {member} to team {team.Name}: it runs the script {key}, {runs}, whenever its triggers fire or it is told.\n" +
                           $"Settings: {settings.ToJsonString()}\nBindings (the team's integrations it uses): {bindings.ToJsonString()}";
                return new ScriptToolYes(plan, string.Join('\n', name, team.Id, member.ToLowerInvariant(), key, pinned, settings.ToJsonString(), bindings.ToJsonString(), runnable));
            }
            case ScriptToolApproval.FirstLiveRun when IsLiveRun(name, args):
            {
                var team = await TeamAsync(Required(args, "team"), ct);
                var (key, version, _) = await ScriptAsync(Required(args, "script"), Int(args, "version"), ct);
                var runs = await ui("scripts.runs", new JsonObject { ["teamId"] = team.Id, ["limit"] = 500 }, ct) as JsonArray ?? [];
                if (runs.OfType<JsonObject>().Any(r => Str(r, "script") == key && r["dryRun"] is JsonValue d && d.TryGetValue<bool>(out var dry) && !dry)) return null;
                var input = args["input"]?.ToJsonString() ?? "null";
                var settings = Obj(args, "settings") ?? [];
                var bindings = Obj(args, "bindings") ?? [];
                var plan = $"Run the script {key} version {version} LIVE on team {team.Name}: the first live run of it there. Its steps really send and write " +
                           $"through the bound integrations (not a dry run).\nInput: {input}\nSettings: {settings.ToJsonString()}\nBindings: {bindings.ToJsonString()}";
                return new ScriptToolYes(plan, string.Join('\n', name, team.Id, key, version, input, settings.ToJsonString(), bindings.ToJsonString()));
            }
            default:
                return null;
        }
    }

    /// <summary>
    /// Runs one script tool and answers its text. It does not ask for the person's yes: the caller checks
    /// <see cref="YesNeededAsync"/> (and its own refusals) first. A refused RPC surfaces as its <c>RpcException</c>; a bad argument as an <see cref="ArgumentException"/>.
    /// </summary>
    public async Task<string> CallAsync(string name, JsonObject args, CancellationToken ct) => name switch
    {
        SchemasList => SchemasText(await ui("schemas.list", [], ct) as JsonArray ?? []),
        SchemaGet => Json(await ui("schemas.get", new JsonObject { ["name"] = Required(args, "name"), ["version"] = Int(args, "version") }, ct)),
        SchemaSave => await SchemaSaveAsync(args, ct),
        ScriptsList => ScriptsText(await ui("scripts.list", [], ct) as JsonArray ?? []),
        ScriptGet => ScriptGetText(await ui("scripts.get", new JsonObject { ["script"] = Required(args, "script"), ["version"] = Int(args, "version") }, ct) as JsonObject),
        ScriptSave => SaveText(await ui("scripts.save", new JsonObject { ["document"] = Document(args, "document") }, ct) as JsonObject),
        ScriptCheck => "check: " + CheckText(await ui("scripts.check", new JsonObject { ["document"] = Document(args, "document") }, ct) as JsonObject),
        ScriptTestRun => await TestRunAsync(args, ct),
        ScriptRuns => await RunsAsync(args, ct),
        ScriptRun => Json(await ui("scripts.run", new JsonObject { ["id"] = Long(args, "id") ?? throw new ArgumentException("'id' is required") }, ct)),
        ScriptMemberAdd => await MemberAddAsync(args, ct),
        _ => throw new ArgumentException($"unknown tool: {name}"),
    };

    // ------------------------------------------------------------------ calls

    private async Task<string> SchemaSaveAsync(JsonObject args, CancellationToken ct)
    {
        var name = Required(args, "name");
        var schema = Document(args, "schema");
        var check = await ui("schemas.check", new JsonObject { ["name"] = name, ["schema"] = schema.DeepClone() }, ct) as JsonObject;
        var breaks = check?["breaks"] as JsonArray ?? [];
        if (breaks.Count > 0)
            throw new ArgumentException($"not saved: schema {name} would break {breaks.Count} reference(s) in saved scripts. Fix the schema or the scripts, then save again:\n" + BreaksText(breaks));
        var saved = await ui("schemas.save", new JsonObject { ["name"] = name, ["description"] = Optional(args, "description"), ["schema"] = schema }, ct) as JsonObject;
        return $"ok: saved schema {Str(saved, "name") ?? name} version {saved?["version"]}; breaks: none.";
    }

    private async Task<string> TestRunAsync(JsonObject args, CancellationToken ct)
    {
        var team = await TeamAsync(Required(args, "team"), ct);
        var dry = DryRun(args);
        var p = new JsonObject
        {
            ["script"] = Required(args, "script"), ["version"] = Int(args, "version"), ["input"] = args["input"]?.DeepClone(),
            ["settings"] = Obj(args, "settings"), ["bindings"] = Obj(args, "bindings"), ["teamId"] = team.Id, ["dryRun"] = dry,
        };
        var r = await ui("scripts.testRun", p, ct) as JsonObject ?? [];
        var sb = new StringBuilder($"run {r["runId"]} on {team.Name} ({(dry ? "dry run: nothing was sent or written" : "LIVE")}): {Str(r, "status")}");
        if (Str(r, "failedStepId") is { } failed) sb.Append($"; failed at step {failed}");
        if (Str(r, "reason") is { } reason) sb.Append($": {reason}");
        sb.AppendLine();
        sb.AppendLine("outputs: " + (r["outputs"]?.ToJsonString() ?? "null"));
        sb.AppendLine("steps: " + (r["steps"]?.ToJsonString() ?? "[]"));
        sb.Append("log:");
        foreach (var line in r["log"] as JsonArray ?? []) sb.Append("\n  " + line?.ToString());
        return sb.ToString();
    }

    private async Task<string> RunsAsync(JsonObject args, CancellationToken ct)
    {
        var team = await TeamAsync(Required(args, "team"), ct);
        var p = new JsonObject { ["teamId"] = team.Id, ["limit"] = Math.Clamp(Int(args, "limit") ?? 20, 1, 500) };
        string? memberName = null;
        if (Optional(args, "member") is { } m)
        {
            var card = (await bus.AgentCardsAsync(team.Id, ct)).FirstOrDefault(c => string.Equals(c.Member.Name, m.Trim(), StringComparison.OrdinalIgnoreCase))
                       ?? throw new ArgumentException($"{team.Name} has no member '{m}'");
            p["memberId"] = card.Member.Id;
            memberName = card.Member.Name;
        }
        var runs = (await ui("scripts.runs", p, ct) as JsonArray ?? []).OfType<JsonObject>().ToList();
        if (runs.Count == 0) return $"{team.Name}{(memberName is null ? "" : "/" + memberName)} has no script runs yet.";
        return string.Join('\n', runs.Select(r =>
            $"- run {r["id"]}: {Str(r, "script")} v{r["version"]} {Str(r, "status")}" +
            $"{(Bool(r, "test") ? " (test" + (Bool(r, "dryRun") ? ", dry run)" : ", live)") : Bool(r, "dryRun") ? " (dry run)" : "")}" +
            $" by {Str(r, "member") ?? "a test run"}, trigger {Str(r, "trigger")}, started {Str(r, "startedAt")}" +
            (Str(r, "failedStepId") is { } f ? $"; failed at step {f}" : "") + (Str(r, "reason") is { } why ? $": {why}" : "")));
    }

    private async Task<string> MemberAddAsync(JsonObject args, CancellationToken ct)
    {
        var team = await TeamAsync(Required(args, "team"), ct);
        var p = new JsonObject
        {
            ["teamId"] = team.Id, ["name"] = Required(args, "name").Trim(), ["script"] = Required(args, "script"), ["version"] = Int(args, "version"),
            ["settings"] = Obj(args, "settings") ?? [], ["bindings"] = Obj(args, "bindings") ?? [],
        };
        var m = await ui("scripts.memberAdd", p, ct) as JsonObject ?? [];
        var runs = m["version"] is JsonValue ? $"version {m["version"]} (pinned)" : $"the latest clean version (now {m["runsVersion"]?.ToString() ?? "none"})";
        return $"ok: added script member {team.Name}/{Str(m, "name") ?? p["name"]} (member {m["memberId"]}) running {Str(m, "script")}, {runs}. " +
               "Give it a trigger (or tell it) to run it; turning a trigger on is the person's choice.";
    }

    // ------------------------------------------------------------------ text

    private static string SchemasText(JsonArray list) => list.Count == 0 ? "There are no schemas." : string.Join('\n', list.OfType<JsonObject>().Select(s =>
        $"- {Str(s, "name")} v{s["latestVersion"]}{(Bool(s, "builtIn") ? " (built-in, cannot be saved over)" : "")}: {Str(s, "description")}"));

    private static string ScriptsText(JsonArray list) => list.Count == 0 ? "The script library is empty." : string.Join('\n', list.OfType<JsonObject>().Select(s =>
    {
        var used = (s["usedBy"] as JsonArray ?? []).OfType<JsonObject>().Select(u => $"{Str(u, "team")}/{Str(u, "member")}").ToList();
        return $"- {Str(s, "key")} ({Str(s, "name")}): latest v{s["latestVersion"]}{(Bool(s, "draft") ? " (a draft: fails its check)" : "")}, " +
               $"runnable {(s["runnableVersion"] is JsonValue rv ? "v" + rv : "none (no clean version)")}; " +
               $"used by {(used.Count == 0 ? "no member" : string.Join(", ", used))}. {Str(s, "description")}".TrimEnd();
    }));

    private static string ScriptGetText(JsonObject? v) =>
        $"{Str(v, "key")} version {v?["version"]} ({(Bool(v, "ok") ? "passes its check" : "a draft: fails its check")})\n" +
        $"check: {CheckText(v?["check"] as JsonObject)}\ndocument: {v?["document"]?.ToJsonString() ?? "null"}";

    private static string SaveText(JsonObject? s) =>
        $"saved {Str(s, "key")} version {s?["version"]}: " +
        (Bool(s, "ok") ? "it passes its check; members on latest now run it." : "a DRAFT: it fails its check, so no member runs it. Fix each issue and save again.") +
        $"\ncheck: {CheckText(s?["check"] as JsonObject)}";

    /// <summary>A check result as the model acts on it: ok, or each issue as step, code and message, then the check as JSON.</summary>
    private static string CheckText(JsonObject? check)
    {
        if (check is null) return "(none)";
        var issues = (check["issues"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var sb = new StringBuilder(Bool(check, "ok") && issues.Count == 0 ? "ok, no issues" : $"{issues.Count} issue(s)");
        foreach (var i in issues) sb.Append($"\n- step {Str(i, "stepId") ?? "(the document)"}: {Str(i, "code")}: {Str(i, "message")}");
        sb.Append("\nas JSON: ").Append(check.ToJsonString());
        return sb.ToString();
    }

    private static string BreaksText(JsonArray breaks) => string.Join('\n', breaks.OfType<JsonObject>().Select(b =>
        $"- script {Str(b, "script")} version {b["version"]}, step {Str(b, "stepId")}, field {Str(b, "fieldPath")}"));

    private static string Json(JsonNode? node) => node?.ToJsonString() ?? "null";

    // ------------------------------------------------------------------ lookups

    private async Task<TeamSummary> TeamAsync(string name, CancellationToken ct)
    {
        var teams = await bus.TeamsAsync(ct);
        return teams.FirstOrDefault(t => string.Equals(t.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
               ?? throw new ArgumentException($"there is no team '{name}'. Teams: {string.Join(", ", teams.Select(t => t.Name))}");
    }

    /// <summary>The script's key, the version a call means (the one given, else the latest) and its runnable version.</summary>
    private async Task<(string Key, int Version, int? Runnable)> ScriptAsync(string script, int? version, CancellationToken ct)
    {
        var v = await ui("scripts.get", new JsonObject { ["script"] = script, ["version"] = version }, ct) as JsonObject
                ?? throw new ArgumentException($"there is no script '{script}'");
        var key = Str(v, "key") ?? script;
        var runnable = (await ui("scripts.list", [], ct) as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(s => Str(s, "key") == key)?["runnableVersion"] is JsonValue r
                       && Ui.UiRpc.WholeNumber(r, out var n) ? (int)n : (int?)null;
        return (key, v["version"] is JsonValue vv && Ui.UiRpc.WholeNumber(vv, out var ver) ? (int)ver : version ?? 0, runnable);
    }

    // ------------------------------------------------------------------ arguments

    private static bool DryRun(JsonObject args) => args["dry_run"] switch
    {
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        JsonValue v when v.TryGetValue<string>(out var s) && bool.TryParse(s, out var c) => c,
        _ => true,
    };

    private static string Required(JsonObject args, string name) => Optional(args, name) ?? throw new ArgumentException($"'{name}' is required");

    private static string? Optional(JsonObject args, string name) => args[name] switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) => s,
        JsonValue v when Ui.UiRpc.WholeNumber(v, out var l) => l.ToString(CultureInfo.InvariantCulture),
        _ => null,
    };

    /// <summary>A whole number given as any JSON number type or as text.</summary>
    private static long? Long(JsonObject args, string name) =>
        args[name] is JsonValue v ? (Ui.UiRpc.WholeNumber(v, out var i) ? i : v.TryGetValue<string>(out var s) && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var j) ? j : null) : null;

    private static int? Int(JsonObject args, string name) => Long(args, name) is { } l ? checked((int)l) : null;

    /// <summary>An object argument given as an object or as JSON text (a model sends either).</summary>
    private static JsonObject? Obj(JsonObject args, string name) => args[name] switch
    {
        null => null,
        JsonObject o => (JsonObject)o.DeepClone(),
        JsonValue v when v.TryGetValue<string>(out var s) && string.IsNullOrWhiteSpace(s) => null,
        JsonValue v when v.TryGetValue<string>(out var s) => Parse(s, name) as JsonObject ?? throw new ArgumentException($"'{name}' must be an object"),
        _ => throw new ArgumentException($"'{name}' must be an object"),
    };

    /// <summary>A document argument: an object, or its JSON text (passed on as given; the RPC parses and checks it).</summary>
    private static JsonNode Document(JsonObject args, string name) => args[name] switch
    {
        JsonObject o => o.DeepClone(),
        JsonValue v when v.TryGetValue<string>(out var s) && s.Trim().Length > 0 => JsonValue.Create(s),
        _ => throw new ArgumentException($"'{name}' is required (an object or its JSON text)"),
    };

    private static JsonNode? Parse(string text, string name)
    {
        try { return JsonNode.Parse(text); }
        catch (JsonException e) { throw new ArgumentException($"'{name}' is not valid JSON: {e.Message}"); }
    }

    private static string? Str(JsonObject? o, string name) => o?[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static bool Bool(JsonObject? o, string name) => o?[name] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    // ------------------------------------------------------------------ definitions

    private sealed record Param(string Name, JsonObject Schema, bool Required);

    private static Param P(string name, string type, string description, bool required) =>
        new(name, new JsonObject { ["type"] = type, ["description"] = description }, required);

    private static Param PAny(string name, string description) => new(name, new JsonObject { ["description"] = description }, false);

    private static Param Confirm() => P("confirm", "string", "The token, after the person's yes.", false);

    private static JsonObject Tool(string name, string description, params Param[] parameters)
    {
        var properties = new JsonObject();
        foreach (var p in parameters) properties[p.Name] = p.Schema;
        return new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = new JsonArray(parameters.Where(p => p.Required).Select(p => (JsonNode)JsonValue.Create(p.Name)!).ToArray()),
                ["additionalProperties"] = false,
            },
        };
    }
}
