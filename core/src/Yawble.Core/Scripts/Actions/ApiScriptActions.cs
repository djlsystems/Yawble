using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Yawble.Core.Connections;
using Yawble.Core.Integrations;
using Yawble.Core.Scripts.Engine;
using Yawble.Core.Scripts.Language;
using Yawble.Core.Scripts.Running;
using IntegrationCaller = Yawble.Core.Integrations.ScriptCaller;
using RunCaller = Yawble.Core.Scripts.Running.ScriptCaller;

namespace Yawble.Core.Scripts.Actions;

/// <summary>
/// The "api" kind of a script's <c>requires</c> (SC-2): an action is an operation of a web API (its operationId, as the API's checklist
/// shows it), one the team allows or asks for. The catalog is every team's operations (scripts are global); a run's own catalog is its team's.
/// </summary>
public sealed class ApiScriptActionsProvider(IScriptApi api) : IScriptActionsProvider
{
    public const string Kind = "api";

    /// <summary>A snapshot of every team's operations, read now.</summary>
    public IScriptActionCatalog Catalog => new ApiScriptActionCatalog(api.Teams().SelectMany(api.Operations).ToList());

    public IScriptActions For(RunCaller caller) => new ApiScriptActions(api, caller);
}

/// <summary>The "api" actions a set of operations offers: HasAction by operationId; changing unless every operation by that id is a GET.</summary>
public sealed class ApiScriptActionCatalog(IReadOnlyList<ScriptOperation> operations) : IScriptActionCatalog
{
    public IReadOnlyList<ScriptOperation> Operations { get; } = operations;

    public bool HasAction(string kind, string action) =>
        kind == ApiScriptActionsProvider.Kind && Operations.Any(o => o.Action == action);

    public bool IsChanging(string kind, string action)
    {
        if (kind != ApiScriptActionsProvider.Kind) return true;
        var matching = Operations.Where(o => o.Action == action).ToList();
        // an unknown action is treated as changing: a dry run must not perform what it cannot tell is harmless
        return matching.Count == 0 || matching.Any(o => !string.Equals(o.Method, "GET", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// One run's API calls: the step's <c>use</c> is the member's binding name, bound to one of the team's web APIs. The result is an
/// ApiResponse <c>{ status, body }</c> (body parsed as JSON when the response is JSON, else the text). A refused call or a non-2xx answer
/// fails the step; a dry run's call that was not sent gives the "would ..." text as its body with status 0.
/// Also the run's catalog: the operations of the caller's team.
/// </summary>
public sealed class ApiScriptActions(IScriptApi api, RunCaller caller) : IScriptActions, IScriptActionCatalog
{
    /// <summary>The call log's member for a Test run (it has no member).</summary>
    public const string TestRunMember = "script-test";

    private const int BodyClip = 500;
    private ApiScriptActionCatalog? _catalog;

    private ApiScriptActionCatalog TeamCatalog => _catalog ??= new ApiScriptActionCatalog(api.Operations(caller.Team));

    public bool HasAction(string kind, string action) => TeamCatalog.HasAction(kind, action);

    public bool IsChanging(string kind, string action) => TeamCatalog.IsChanging(kind, action);

    public async Task<JsonNode?> InvokeAsync(ScriptActionCall call, CancellationToken ct)
    {
        if (call.Kind != ApiScriptActionsProvider.Kind) throw new ScriptActionException($"{call.Use}: '{call.Kind}' is not an API");
        if (!caller.Bindings.TryGetValue(call.Use, out var integration) || string.IsNullOrWhiteSpace(integration))
            throw new ScriptActionException($"'{call.Use}' is not bound to one of {caller.Team}'s APIs");
        var who = new IntegrationCaller(caller.Team, caller.Member ?? TestRunMember,
            caller.RunId.ToString(CultureInfo.InvariantCulture), caller.DryRun || call.DryRun);

        ScriptApiResult result;
        try { result = await api.CallAsync(who, integration, call.Action, call.Args, ct); }
        catch (ScriptApiRefusedException e) { throw new ScriptActionException(e.Message); }

        if (!result.Sent) return Response(0, JsonValue.Create(result.Body));
        if (result.Status == 0) throw new ScriptActionException("no answer: " + Clip(SecretScrubber.Scrub(result.Body)));
        if (result.Status is < 200 or > 299) throw new ScriptActionException($"{result.Status}: {Clip(SecretScrubber.Scrub(result.Body))}");
        return Response(result.Status, Body(result));
    }

    private static JsonObject Response(int status, JsonNode? body) => new() { ["status"] = status, ["body"] = body };

    /// <summary>The body as JSON when the response says it is JSON and it parses; else the text.</summary>
    public static JsonNode? Body(ScriptApiResult result)
    {
        if (IsJson(result.ContentType) && !string.IsNullOrWhiteSpace(result.Body))
        {
            try { return JsonNode.Parse(result.Body); }
            catch (JsonException) { } // cut at 64 KB, or not what it said: the text
        }
        return JsonValue.Create(result.Body);
    }

    private static bool IsJson(string? mediaType)
    {
        var m = (mediaType ?? "").Split(';')[0].Trim();
        return m.Equals("application/json", StringComparison.OrdinalIgnoreCase) || m.EndsWith("+json", StringComparison.OrdinalIgnoreCase)
            || m.Equals("text/json", StringComparison.OrdinalIgnoreCase);
    }

    private static string Clip(string text) => text.Length <= BodyClip ? text : text[..BodyClip] + "...";
}
