using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Ipc;

namespace Yawble.Core.Scripts;

/// <summary>What a script tool needs from the person before it runs.</summary>
public enum ScriptToolApproval
{
    /// <summary>Never: reading, building, checking and dry runs.</summary>
    None,

    /// <summary>Always the person's yes (adding a script member).</summary>
    PersonsYes,

    /// <summary>The person's yes for the first live (dry_run false) run of a script on a team; dry runs and later live runs need none.</summary>
    FirstLiveRun,

    /// <summary>
    /// The person's yes whenever a script member's trigger ends up enabled and that is new: a new enabled trigger, an off one turned on, or an
    /// enabled one whose schedule changes. Turning a trigger off, or saving one off, needs none.
    /// </summary>
    EnabledTrigger,
}

/// <summary>One script tool: its name, whether it changes something, whether only the person at this computer may use it
/// (by name; a live run is refused over the Cloud Link by its arguments, see <see cref="ScriptTools.RemoteRefused"/>), and its approval class.</summary>
public sealed record ScriptToolInfo(string Name, bool Changes, bool LocalOnly, ScriptToolApproval Approval);

/// <summary>A call that waits for the person's yes: the exact text to show them, and the key the yes is bound to (the same
/// arguments give the same key; a change in what would be done gives another).</summary>
public sealed record ScriptToolYes(string Plan, string Key);

/// <summary>
/// The script and schema tools for a chat that builds scripts for the person: the Concierge, and the companion's tools.
/// For every tool its name, its MCP Tool definition, its call onto the scripts.* / schemas.* RPC (<paramref name="ui"/>, the
/// person's own buttons, so the same rules and refusals apply) and its approval class. Teams and members are given by name.
/// The approval itself (showing the plan, the person's yes in a later turn) is the caller's confirmation flow: ask
/// <see cref="YesNeededAsync"/> first, then <see cref="CallAsync"/> once the person said yes.
/// </summary>
public sealed class ScriptTools(BusEngine bus, Func<string, JsonObject?, CancellationToken, Task<JsonNode?>> ui)
{
    public const string SchemasList = "schemas_list", SchemaGet = "schema_get", SchemaSave = "schema_save",
        ScriptsList = "scripts_list", ScriptGet = "script_get", ScriptSave = "script_save", ScriptCheck = "script_check",
        ScriptTestRun = "script_test_run", ScriptRuns = "script_runs", ScriptRun = "script_run", ScriptMemberAdd = "script_member_add",
        ScriptTriggerSet = "script_trigger_set", IntegrationActionAllow = "integration_action_allow";

    /// <summary>The schedule fields script_trigger_set takes: triggers.save's own names (UiRpc.TriggerDraftOf).</summary>
    public static readonly IReadOnlyList<string> ScheduleFields =
        ["kind", "intervalSec", "cron", "timezone", "fireAt", "eventName", "watchRoot", "watchPath", "watchGlob", "quietSec", "fireOn"];

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
        // triggers.save is a change a full Cloud Link viewer may make (Link/RemoteAccess.cs), so this is not local only; its yes is still asked.
        new(ScriptTriggerSet, true, false, ScriptToolApproval.EnabledTrigger),
        new(IntegrationActionAllow, true, true, ScriptToolApproval.PersonsYes),
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
        Tool(ScriptTriggerSet, "Give a script member a trigger, or change one of its triggers (with id): each fire runs the member's script with the fire's " +
                               "details as input. Only for script members (not agents). Turning a trigger on (a new enabled trigger, an off one turned on, or " +
                               "an enabled one whose schedule changes) needs the person's yes (the first call shows when it fires and which script runs, and " +
                               "returns a token). Turning one off, or saving it with enabled false, needs none.",
            P("team", "string", "The team's name.", true), P("member", "string", "The script member's name.", true),
            P("name", "string", "The trigger's name.", true), ScheduleParam(),
            P("enabled", "boolean", "Default true: the trigger fires. false saves it off.", false),
            P("id", "integer", "Change this trigger (one of this member's); without it a new trigger is made.", false),
            P("only_when_idle", "boolean", "Skip a fire while the member is busy (default: the trigger's own, true for a new one).", false), Confirm()),
        Tool(IntegrationActionAllow, "Allow one action of an API integration for a team: one team rule for exactly that operation id, as the person's rule " +
                                     "editor writes it. A script's call of it then no longer stops with \"needs approval\" (the team's agents may call it " +
                                     "without asking too). Always needs the person's yes (the first call shows the API, the action's method, route and " +
                                     "summary, and returns a token); only for the person at this computer. Not for Email integrations (their actions are not gated by rules).",
            P("team", "string", "The team's name.", true), P("integration", "string", "The API integration's name (\"name\" or \"api:name\").", true),
            P("action", "string", "The action's operation id (scripts.catalog / script_get list them).", true), Confirm()),
    ];

    /// <summary>script_trigger_set's schedule: triggers.save's fields, by kind.</summary>
    private static Param ScheduleParam()
    {
        static JsonObject F(string type, string description) => new() { ["type"] = type, ["description"] = description };
        var properties = new JsonObject
        {
            ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("every", "cron", "once", "event", "folder"), ["description"] = "When it fires." },
            ["intervalSec"] = F("integer", "every: seconds between fires (at least 10). folder: how often the folder is looked at (default 30)."),
            ["cron"] = F("string", "cron: 5 fields, minute hour day month weekday (e.g. \"0 9 * * 1-5\")."),
            ["timezone"] = F("string", "cron: an IANA or Windows timezone id (e.g. \"Europe/London\")."),
            ["fireAt"] = F("string", "once: when (ISO 8601, in the future). every / cron: an optional start."),
            ["eventName"] = F("string", "event: the event's name (letters, digits, . _ - and * for any), e.g. web.pr."),
            ["watchRoot"] = F("string", "folder: \"documents\" (the team's documents) or \"host\" (a full path on this computer)."),
            ["watchPath"] = F("string", "folder: the folder inside the team's documents, or its full path for host."),
            ["watchGlob"] = F("string", "folder: only files matching this pattern (e.g. *.csv)."),
            ["quietSec"] = F("integer", "folder: how long a change must stay still before it fires (0 to 3600, default 10)."),
            ["fireOn"] = F("string", "folder: added (default for a new trigger), added-or-changed or any."),
        };
        return new Param("schedule", new JsonObject
        {
            ["type"] = "object",
            ["description"] = "When it fires, in triggers.save's fields: kind and the fields that kind takes.",
            ["properties"] = properties,
            ["required"] = new JsonArray("kind"),
            ["additionalProperties"] = false,
        }, true);
    }

    /// <summary>
    /// What the person must say yes to before this call runs, or null when it runs without (everything but a member add, a
    /// script's first live run on a team, a script member's trigger turned on and an API action not yet allowed).
    /// Reads only; a call that would be refused (an agent member, an unknown API) throws its refusal here, before any yes is asked.
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
            case ScriptToolApproval.EnabledTrigger when name == ScriptTriggerSet:
            {
                var t = await TriggerPlanAsync(args, ct);
                return t.NeedsYes ? new ScriptToolYes(t.Plan, t.Key) : null;
            }
            case ScriptToolApproval.PersonsYes when name == IntegrationActionAllow:
            {
                var op = await ActionToAllowAsync(args, ct);
                if (op.Decision == Integrations.Decisions.Allow) return null; // already allowed: answered without a yes
                var plan = $"Allow the action {op.Action} of the API {op.Api} for team {op.Team}: {op.Method.ToUpperInvariant()} {op.Route}" +
                           (op.Summary.Trim().Length > 0 ? $" ({op.Summary.Trim()})" : "") + ".\n" +
                           $"Now it is \"{op.Decision}\" for {op.Team}. One team rule is added (as the rule editor in Settings > Integrations writes it): " +
                           $"{op.Team}'s scripts, and its agents, may then call {op.Action} without asking each time. Other teams keep their own rules.";
                return new ScriptToolYes(plan, string.Join('\n', name, op.TeamId, op.Api, op.Action, op.Method, op.Route, op.Decision));
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
        ScriptTriggerSet => await TriggerSetAsync(args, ct),
        IntegrationActionAllow => await ActionAllowAsync(args, ct),
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

    // ------------------------------------------------------------------ a script member's trigger

    /// <summary>
    /// What a script_trigger_set call would save: the triggers.save parameters, the draft they read as, the trigger it changes (with id),
    /// whether it needs the person's yes, the plan and the key the yes is bound to. Refuses (nothing saved) an unknown member, an agent member
    /// and an id that is not one of this member's triggers.
    /// </summary>
    private sealed record TriggerPlan(TeamSummary Team, string Member, JsonObject Save, TriggerDraft Draft, TriggerRow? Existing, bool NeedsYes, string When, string Plan, string Key);

    private async Task<TriggerPlan> TriggerPlanAsync(JsonObject args, CancellationToken ct)
    {
        var team = await TeamAsync(Required(args, "team"), ct);
        var memberName = Required(args, "member").Trim();
        var card = (await bus.AgentCardsAsync(team.Id, ct)).FirstOrDefault(c => string.Equals(c.Member.Name, memberName, StringComparison.OrdinalIgnoreCase))
                   ?? throw new ArgumentException($"refused: {team.Name} has no member '{memberName}'. Nothing was saved.");
        var member = card.Member.Name;
        JsonObject script;
        try
        {
            script = await ui("scripts.memberGet", new JsonObject { ["memberId"] = card.Member.Id }, ct) as JsonObject
                     ?? throw new ArgumentException($"refused: {team.Name}/{member} is not a script member. Nothing was saved.");
        }
        catch (RpcException e) when (e.Code == "not_found")
        {
            throw new ArgumentException($"refused: {team.Name}/{member} is an agent member, not a script member: script_trigger_set is for script members only " +
                                        "(an agent's triggers are set in the app's Triggers dialog). Nothing was saved.");
        }

        var id = Long(args, "id");
        TriggerRow? existing = null;
        if (id is { } tid)
        {
            existing = (await bus.TriggersAsync(team.Id, ct)).FirstOrDefault(t => t.Id == tid);
            if (existing is null || existing.MemberId != card.Member.Id)
                throw new ArgumentException($"refused: trigger {tid} is not one of {team.Name}/{member}'s triggers. Nothing was saved.");
        }

        var schedule = Obj(args, "schedule") ?? throw new ArgumentException("'schedule' is required: an object with kind and the fields that kind takes");
        var save = new JsonObject();
        foreach (var (field, value) in schedule)
        {
            if (!ScheduleFields.Contains(field))
                throw new ArgumentException($"'schedule' has no field '{field}'. Its fields: {string.Join(", ", ScheduleFields)}");
            if (value is null) continue;
            save[field] = field is "intervalSec" or "quietSec"
                ? Long(schedule, field) ?? throw new ArgumentException($"schedule.{field} must be a whole number")
                : value is JsonValue v && v.TryGetValue<string>(out var s) ? s : throw new ArgumentException($"schedule.{field} must be text");
        }
        if (id is { } saveId) save["id"] = saveId;
        save["memberId"] = card.Member.Id;
        save["name"] = Required(args, "name").Trim();
        save["instruction"] = ""; // a script member is told nothing: its fire's details are its run's input
        save["enabled"] = Flag(args, "enabled") ?? true;
        save["onlyWhenIdle"] = Flag(args, "only_when_idle") ?? existing?.OnlyWhenIdle ?? true;
        if (existing is not null) save["wakeManager"] = existing.WakeManager;

        TriggerDraft draft;
        try { draft = Ui.UiRpc.TriggerDraftOf(save); }
        catch (FormatException) { throw new ArgumentException("schedule.fireAt is not a time: give it in ISO 8601, e.g. 2026-12-01T09:00:00Z"); }

        var scheduleKey = ScheduleKey(draft.Schedule, draft.EventName, draft.Watch, existing?.Watch);
        var needsYes = draft.Enabled && (existing is null || !existing.Enabled
                                         || scheduleKey != ScheduleKey(existing.Schedule, existing.EventName, existing.Watch, existing.Watch));
        var key = Str(script, "script") ?? "?";
        var runs = script["version"] is JsonValue pinned
            ? $"the script {key} version {pinned} (pinned)"
            : $"the script {key}, the latest clean version (now {script["runsVersion"]?.ToString() ?? "none"}; it follows new ones)";
        var when = When(draft);
        var what = existing is null ? "a new trigger" : !existing.Enabled ? $"trigger {existing.Id}, now off" : $"trigger {existing.Id}, its schedule changes";
        var plan = $"Turn on the trigger \"{draft.Name.Trim()}\" of script member {member} of team {team.Name} ({what}): it fires {when}.\n" +
                   $"Each fire runs {runs}, with the fire's details as its input: its steps really send and write through the team's integrations.";
        var bound = string.Join('\n', ScriptTriggerSet, team.Id, card.Member.Id, draft.Name.Trim().ToLowerInvariant(), scheduleKey, draft.Enabled, id,
            key, script["version"]?.ToJsonString(), script["runsVersion"]?.ToJsonString());
        return new TriggerPlan(team, member, save, draft, existing, needsYes, when, plan, bound);
    }

    private async Task<string> TriggerSetAsync(JsonObject args, CancellationToken ct)
    {
        var t = await TriggerPlanAsync(args, ct);
        var saved = await ui("triggers.save", t.Save, ct) as JsonObject;
        var id = saved?["id"]?.ToString() ?? t.Existing?.Id.ToString(CultureInfo.InvariantCulture) ?? "?";
        return t.Draft.Enabled
            ? $"ok: saved trigger {id} \"{t.Draft.Name.Trim()}\" of script member {t.Team.Name}/{t.Member}: on, it fires {t.When}."
            : $"ok: saved trigger {id} \"{t.Draft.Name.Trim()}\" of script member {t.Team.Name}/{t.Member}: off, it does not fire until it is turned on.";
    }

    /// <summary>A schedule as compared for "its schedule changes": only the fields its kind reads, trimmed as the bus stores them.</summary>
    private static string ScheduleKey(Triggers.TriggerSchedule s, string? eventName, Triggers.FolderWatch? watch, Triggers.FolderWatch? before)
    {
        var at = s.FireAt?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        return s.Kind switch
        {
            Triggers.TriggerKinds.Every => $"every|{s.IntervalSec}|{at}",
            Triggers.TriggerKinds.Cron => $"cron|{s.Cron?.Trim()}|{s.Timezone?.Trim()}|{at}",
            Triggers.TriggerKinds.Once => $"once|{at}",
            Triggers.TriggerKinds.Event => $"event|{eventName?.Trim()}",
            Triggers.TriggerKinds.Folder => $"folder|{s.IntervalSec}|{watch?.Root}|{FolderPath(watch)}|{watch?.Glob}|{watch?.QuietSec}|" +
                                            (watch?.FireOn is { Length: > 0 } on ? on : before?.FireOn ?? Triggers.FolderWatch.Added),
            _ => s.Kind,
        };

        static string? FolderPath(Triggers.FolderWatch? w) =>
            w is null ? null : w.Root == Triggers.FolderWatch.Documents ? (w.Path ?? "").Trim().Replace('\\', '/').Trim('/') : (w.Path ?? "").Trim();
    }

    /// <summary>When a trigger fires, in plain words: "every 15 minutes", the cron text, the time of a once, the event or the folder.</summary>
    private static string When(TriggerDraft d)
    {
        var s = d.Schedule;
        var start = s.FireAt is { } from ? $", starting {from.UtcDateTime:yyyy-MM-dd HH:mm} UTC" : "";
        return s.Kind switch
        {
            Triggers.TriggerKinds.Every => $"every {Interval(s.IntervalSec ?? 0)}{start}",
            Triggers.TriggerKinds.Cron => $"on the cron schedule \"{s.Cron?.Trim()}\" ({s.Timezone?.Trim()}){start}",
            Triggers.TriggerKinds.Once => s.FireAt is { } at ? $"once, at {at.UtcDateTime:yyyy-MM-dd HH:mm} UTC" : "once",
            Triggers.TriggerKinds.Event => $"whenever the event {d.EventName?.Trim()} is published",
            Triggers.TriggerKinds.Folder => d.Watch is { } w
                ? $"when files are {(w.FireOn is Triggers.FolderWatch.AddedOrChanged ? "added or changed" : w.FireOn is Triggers.FolderWatch.AnyChange ? "added, changed or removed" : "added")} in " +
                  (w.Root == Triggers.FolderWatch.Documents ? $"the team's documents{(w.Path.Trim().Length > 0 ? "/" + w.Path.Trim().Replace('\\', '/').Trim('/') : "")}" : $"the folder {w.Path.Trim()} on this computer") +
                  (w.Glob is { Length: > 0 } g ? $" matching {g}" : "") + $" (looked at every {Interval(s.IntervalSec ?? Triggers.FolderWatch.DefaultPollSec)})"
                : "when its folder changes",
            _ => s.Kind,
        };
    }

    private static string Interval(int seconds) => seconds switch
    {
        > 0 when seconds % 86400 == 0 => Plural(seconds / 86400, "day"),
        > 0 when seconds % 3600 == 0 => Plural(seconds / 3600, "hour"),
        > 0 when seconds % 60 == 0 => Plural(seconds / 60, "minute"),
        _ => Plural(seconds, "second"),
    };

    private static string Plural(int n, string unit) => n == 1 ? unit : $"{n} {unit}s";

    // ------------------------------------------------------------------ allowing an API action

    /// <summary>The API action a call names, checked (unknown API, not on for the team, unknown or gone action, a sign-in action: refused, nothing written).</summary>
    private async Task<Integrations.ScriptActionToAllow> ActionToAllowAsync(JsonObject args, CancellationToken ct)
    {
        var team = await TeamAsync(Required(args, "team"), ct);
        var svc = bus.Integrations ?? throw new ArgumentException("refused: the integrations are not available here. Nothing was changed.");
        try { return svc.ActionToAllowForScripts(team.Name, Required(args, "integration"), Required(args, "action")); }
        catch (Integrations.ScriptApiRefusedException e) { throw new ArgumentException(e.Message + ". Nothing was changed."); }
    }

    private async Task<string> ActionAllowAsync(JsonObject args, CancellationToken ct)
    {
        var op = await ActionToAllowAsync(args, ct);
        if (op.Decision == Integrations.Decisions.Allow)
            return $"already allowed: {op.Api}'s {op.Action} is already allowed for {op.Team}; nothing was changed.";
        // The rule editor's own save (Settings > Integrations, the API's checklist for this team): one team choice and the rule it owns.
        await ui("integrations.saveChecklist", new JsonObject
        {
            ["name"] = op.Api, ["kind"] = "api", ["teamId"] = op.TeamId,
            ["choices"] = new JsonArray(new JsonObject { ["path"] = op.Action, ["state"] = Integrations.RowStates.Allow }),
        }, ct);
        var now = (await ActionToAllowAsync(args, ct)).Decision;
        if (now != Integrations.Decisions.Allow)
            throw new ArgumentException($"refused: the rule was saved but {op.Api}'s {op.Action} is still \"{now}\" for {op.Team}");
        return $"ok: allowed {op.Api}'s {op.Action} ({op.Method.ToUpperInvariant()} {op.Route}) for {op.Team}: one team rule. " +
               "A script's call of it no longer needs approval.";
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

    /// <summary>A flag given as a JSON boolean or as text ("true" / "false"); null when absent.</summary>
    private static bool? Flag(JsonObject args, string name) => args[name] switch
    {
        null => null,
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        JsonValue v when v.TryGetValue<string>(out var s) && bool.TryParse(s, out var c) => c,
        _ => throw new ArgumentException($"'{name}' must be true or false"),
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
