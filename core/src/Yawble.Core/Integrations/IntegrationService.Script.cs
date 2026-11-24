using System.Text.Json.Nodes;
using Yawble.Core.Connections;
using Yawble.Core.Ipc;

namespace Yawble.Core.Integrations;

/// <summary>
/// Who makes a script's API call: the team (by name), the script member (its name, or "team/name"), the script run's id (written to the call
/// log) and whether the run is a dry run (then only a GET is sent).
/// </summary>
public sealed record ScriptCaller(string Team, string Member, string ScriptRunId, bool DryRun);

/// <summary>
/// What a script's API call returned: the HTTP status, the response's media type (without parameters, "" when none) and its body, scrubbed of
/// secrets and cut at 64 KB. A non-2xx is returned the same way (the script adapter turns it into a step failure).
/// <see cref="Sent"/> is false for a dry run's call that was not sent: Status is then 0 and Body says "would METHOD /path".
/// Status 0 with <see cref="Sent"/> true: nothing answered (a timeout, no connection); Body says why.
/// </summary>
public sealed record ScriptApiResult(int Status, string ContentType, string Body)
{
    public bool Sent { get; init; } = true;
}

/// <summary>
/// A script's API call that was not made, with the reason as the step's failure text: the API is unknown or not turned on for the team, the
/// action is not allowed ("deny"), needs the person's approval ("ask": "needs approval: allow &lt;action&gt; for &lt;team&gt;"), or the
/// parameters or the sign-in do not fit. Nothing was sent.
/// </summary>
public sealed class ScriptApiRefusedException(string message) : Exception(message);

/// <summary>One parameter a script passes to an operation by name: in = path, query, header or body (a form field, or the JSON body as "body"); its JSON type and description when the API gives them.</summary>
public sealed record ScriptOperationParameter(string Name, string In, bool Required, string? Type, string? Description = null);

/// <summary>
/// One operation a script may call (the script checker's catalog): its action name, HTTP method, route template, summary, the team's decision
/// ("allow", or "ask": the call then fails with needs approval), its parameters (without the path parameters the Connection fills) and the
/// body's media type (null when it takes none).
/// </summary>
public sealed record ScriptOperation(string Action, string Method, string Route, string Summary, string Decision,
    IReadOnlyList<ScriptOperationParameter> Parameters, string? BodyMediaType);

/// <summary>
/// A script member's calls of an API integration (SC-2): the same rules as an agent's (the API turned on for the team, the action decided by
/// <see cref="ApiDecide"/>), the same sign-in (<see cref="ResolveSignInAsync"/>, <see cref="SendApiAsync"/>) and the same integration_calls
/// log, with the script member as the caller (via "script", the script run's id in the args). No agent RunContext: an "ask" is refused, never
/// raised as an approval (no decision card is opened for a script yet).
/// </summary>
public sealed partial class IntegrationService
{
    /// <summary>The call log's "via" of a script's call.</summary>
    public const string ScriptVia = "script";

    private const string FormMediaType = "application/x-www-form-urlencoded";

    /// <summary>
    /// Calls <paramref name="action"/> of the API <paramref name="integration"/> ("name" or "api:name") for a script, with
    /// <paramref name="args"/> by name: path, query and header parameters, a form body's fields (or the body as "body"). Path parameters the
    /// API's Connection fills are left out. Throws <see cref="ScriptApiRefusedException"/> when the call is not made.
    /// </summary>
    public async Task<ScriptApiResult> CallForScriptAsync(ScriptCaller caller, string integration, string action, JsonObject args, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var (teamId, teamName) = ScriptTeam(caller.Team);
        var api = ScriptApi(teamId, teamName, integration);
        var ops = ApiOps(api.Id);
        var id = action?.Trim() ?? "";
        var op = ops.FirstOrDefault(o => o.Id == id && !o.Gone)
            ?? throw new ScriptApiRefusedException($"refused: {api.Name} has no action '{Clip(SecretScrubber.Scrub(id), 100)}'");
        var team = ApiRulesOf(api.Id, teamId);
        var global = ApiRulesOf(api.Id, null);
        var (decision, rule) = ApiDecide(op, team, global);
        var ruleText = rule is null ? null : $"{rule.Layer}: {rule.Pattern}";
        var member = caller.Member.Contains('/', StringComparison.Ordinal) ? caller.Member : $"{teamName}/{caller.Member}";
        string[] callArgs = [op.Id, SecretScrubber.Scrub((args ?? []).ToJsonString()), $"script run {Clip(caller.ScriptRunId, 100)}"];
        var who = new Provenance.Actor(ScriptVia, null);
        void Record(string d, string? r, CliOutput? output) => Insert("api:" + api.Name, teamName, member, null, callArgs, d, r, output, who);

        if (decision == Decisions.Deny)
        {
            Record(Decisions.Deny, ruleText, null);
            throw new ScriptApiRefusedException($"refused: {api.Name}'s {op.Id} is not allowed for {teamName}{(rule?.Layer == "floor" ? " (it reveals or changes a sign-in)" : "")}");
        }
        ApiRequest request;
        try { request = PlanApiRequest(api, op, args ?? [], PathFillsOf(api)); }
        catch (ArgumentException e) { throw new ScriptApiRefusedException("refused: " + ApiScrub(e.Message, null)); }
        if (decision == Decisions.Ask)
        {
            Record(Decisions.Ask, ruleText, null);
            throw new ScriptApiRefusedException($"needs approval: allow {op.Id} for {teamName}");
        }
        // a dry run sends only what reads: anything else is described, not sent (and not logged: nothing happened)
        if (caller.DryRun && request.Method != HttpMethod.Get)
            return new ScriptApiResult(0, "text/plain", $"would {request.Method.Method} {request.Url.PathAndQuery}") { Sent = false };

        var (sign, view, refusal, refusedFor) = await ResolveSignInAsync(api, op, ct);
        if (refusal is not null)
        {
            Record(Decisions.Deny, refusedFor, null);
            throw new ScriptApiRefusedException(refusal);
        }
        var result = await SendApiAsync(api, request, sign, view, teamId, ct);
        Record(Decisions.Allow, ruleText, result.Output);
        return result.Status is { } status
            ? new ScriptApiResult(status, result.Media ?? "", result.Body ?? "")
            : new ScriptApiResult(0, "", result.Text);
    }

    /// <summary>
    /// The operations of the API <paramref name="integration"/> a script of <paramref name="team"/> may call (allowed, or ask), with their
    /// parameters as a script passes them. Throws <see cref="ScriptApiRefusedException"/> when the team or the API is unknown or the API is
    /// not turned on for the team.
    /// </summary>
    public IReadOnlyList<ScriptOperation> ListScriptOperations(string team, string integration)
    {
        var (teamId, teamName) = ScriptTeam(team);
        return ScriptOperationsOf(ScriptApi(teamId, teamName, integration), teamId);
    }

    /// <summary>
    /// The operations of the API <paramref name="integration"/> a script may call: for the team <paramref name="teamId"/> (its rules and the
    /// global ones; the API need not be turned on for it), or with no team under the global rules alone. Throws
    /// <see cref="ScriptApiRefusedException"/> when there is no web API of that name.
    /// </summary>
    public IReadOnlyList<ScriptOperation> ListScriptOperationsFor(string integration, long? teamId)
    {
        var name = (integration ?? "").Trim();
        if (name.StartsWith("api:", StringComparison.OrdinalIgnoreCase)) name = name[4..].Trim();
        var api = (name.Length == 0 ? null : ApiRows(name).FirstOrDefault(a => a.Kind == "http"))
            ?? throw new ScriptApiRefusedException($"refused: no API named '{Clip(SecretScrubber.Scrub(name), 60)}'");
        return ScriptOperationsOf(api, teamId);
    }

    private List<ScriptOperation> ScriptOperationsOf(ApiRecord api, long? teamId)
    {
        var teamRules = teamId is { } tid ? ApiRulesOf(api.Id, tid) : [];
        var global = ApiRulesOf(api.Id, null);
        var fills = PathFillsOf(api);
        var list = new List<ScriptOperation>();
        foreach (var op in ApiOps(api.Id).Where(o => !o.Gone))
        {
            var decision = ApiDecide(op, teamRules, global).Decision;
            if (decision == Decisions.Deny) continue;
            var props = op.Params["properties"] as JsonObject ?? [];
            var required = Names(op.Params["required"]);
            var filled = ConnectionFilledKeys(op, fills);
            var parameters = new List<ScriptOperationParameter>();
            foreach (var (key, schema) in props)
            {
                if (key == "body" && ApiIn(schema) is null || filled.Contains(key)) continue;
                parameters.Add(new ScriptOperationParameter(key, ApiIn(schema) ?? "query", required.Contains(key), TypeOf(schema), DescriptionOf(schema)));
            }
            string? media = null;
            if (props["body"] is JsonObject body && ApiIn(body) is null)
            {
                var kind = OptStr(op.Params, "x-body");
                media = kind == "form" ? FormMediaType : "application/json";
                if (kind == "form" && body["properties"] is JsonObject fields && fields.Count > 0)
                {
                    var fieldsRequired = Names(body["required"]);
                    foreach (var (field, schema) in fields)
                        if (!props.ContainsKey(field)) parameters.Add(new ScriptOperationParameter(field, "body", fieldsRequired.Contains(field), TypeOf(schema), DescriptionOf(schema)));
                }
                else parameters.Add(new ScriptOperationParameter("body", "body", required.Contains("body"), TypeOf(body) ?? "object", DescriptionOf(body)));
            }
            list.Add(new ScriptOperation(op.Id, op.Method, op.Route, op.Summary, decision, parameters, media));
        }
        return list;

        static HashSet<string> Names(JsonNode? node) =>
            (node as JsonArray ?? []).Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).OfType<string>().ToHashSet(StringComparer.Ordinal);

        static string? DescriptionOf(JsonNode? schema) =>
            (schema as JsonObject)?["description"] is JsonValue d && d.TryGetValue<string>(out var text) && text.Trim().Length > 0 ? text.Trim() : null;

        static string? TypeOf(JsonNode? schema) => (schema as JsonObject)?["type"] switch
        {
            JsonValue v when v.TryGetValue<string>(out var t) => t,
            JsonArray a => a.Select(n => n is JsonValue v && v.TryGetValue<string>(out var t) ? t : null).FirstOrDefault(t => t is not null and not "null"),
            _ => null,
        };
    }

    /// <summary>The team by its name (any case): its id and name as stored.</summary>
    private (long Id, string Name) ScriptTeam(string team)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, name FROM teams WHERE name = $n COLLATE NOCASE;";
        cmd.Parameters.AddWithValue("$n", team?.Trim() ?? "");
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetInt64(0), r.GetString(1)) : throw new ScriptApiRefusedException($"refused: no team named '{Clip(team ?? "", 60)}'");
    }

    /// <summary>The web API by its name ("name" or "api:name"), turned on for the team.</summary>
    private ApiRecord ScriptApi(long teamId, string teamName, string integration)
    {
        var name = (integration ?? "").Trim();
        if (name.StartsWith("api:", StringComparison.OrdinalIgnoreCase)) name = name[4..].Trim();
        var api = name.Length == 0 ? null : ApiRows(name).FirstOrDefault();
        if (api is null) throw new ScriptApiRefusedException($"refused: no API named '{Clip(SecretScrubber.Scrub(name), 60)}'");
        if (api.Kind != "http") throw new ScriptApiRefusedException($"refused: {api.Name} is a website, not a web API");
        if (!ApisOfferedTo(teamId).Any(a => a.Id == api.Id)) throw new ScriptApiRefusedException($"refused: {api.Name} is not turned on for {teamName}");
        return api;
    }
}
