using System.Text.Json.Nodes;
using Yawble.Core.Ipc;
using Yawble.Core.Policy;
using Yawble.Core.Provenance;

namespace Yawble.Core.Agents.Profiles;

/// <summary>
/// The window's calls for custom agents and the change history. The prefix is "agentProfiles.", which is not in
/// RemoteAccess's list: none of this goes over the Cloud Link.
/// </summary>
public sealed class AgentProfileRpc(AgentProfileStore store, AgentChangeLog log)
{
    public static bool Handles(string method) => method.StartsWith("agentProfiles.", StringComparison.Ordinal);

    public Task<JsonNode?> DispatchAsync(string method, JsonObject? p, CancellationToken ct)
    {
        try { return Task.FromResult<JsonNode?>(Dispatch(method, p)); }
        catch (AgentProfileException e)
        {
            throw new RpcException("invalid_params", e.Message);
        }
    }

    private JsonNode Dispatch(string method, JsonObject? p) => method switch
    {
        // Every agent one can choose (built-ins with their default model, then the custom ones).
        "agentProfiles.list" => new JsonArray([.. store.Choices().Select(Json)]),
        "agentProfiles.duplicate" => Json(store.Duplicate(Cli(p), Str(p, "name"), OptStr(p, "defaultModel"))),
        "agentProfiles.update" => Json(store.Update(Id(p), Str(p, "name"), OptStr(p, "defaultModel") ?? "", Strings(p, "argsBefore"), Strings(p, "argsAfter"),
            Strings(p, "removedArgs"), AgentChangeLog.HowSettings, Actor.Current ?? Actor.Window)),
        "agentProfiles.setDefaultModel" => Json(store.SetDefaultModel(Cli(p), OptStr(p, "model") ?? "", AgentChangeLog.HowSettings, Actor.Current ?? Actor.Window)),
        "agentProfiles.delete" => Delete(p),
        // The arguments the validator refuses, without saving (the dialog shows them as the person types).
        "agentProfiles.check" => new JsonArray([.. AgentArgsValidator.Validate(Cli(p), Strings(p, "argsBefore"), Strings(p, "argsAfter"), Strings(p, "removedArgs"))
            .Select(x => (JsonNode)new JsonObject { ["argument"] = x.Argument, ["reason"] = x.Reason })]),
        "agentProfiles.history" => new JsonArray([.. History(p).Select(Json)]),
        _ => throw new RpcException("method_not_found", $"unknown method '{method}'"),
    };

    private JsonNode Delete(JsonObject? p)
    {
        store.Delete(Id(p), p?["moveToBase"] is JsonValue v && v.TryGetValue<bool>(out var b) && b, AgentChangeLog.HowSettings, Actor.Current ?? Actor.Window);
        return new JsonObject { ["deleted"] = true };
    }

    /// <summary>History per member (team + member), per agent (agent) or per subject (a Manager or Concierge conversation).</summary>
    private IReadOnlyList<AgentChange> History(JsonObject? p)
    {
        var limit = p?["limit"] is JsonValue l && l.TryGetValue<int>(out var n) ? n : 200;
        if (OptStr(p, "member") is { } member) return log.HistoryOfMember(Str(p, "team"), member, limit);
        if (OptStr(p, "agent") is { } agent) return log.HistoryOfAgent(agent, limit);
        if (OptStr(p, "subject") is { } subject) return log.HistoryOfSubject(subject, limit);
        return log.All(limit);
    }

    private static JsonNode Json(AgentProfile a) => new JsonObject
    {
        ["id"] = a.Id, ["name"] = a.Name, ["baseCli"] = a.BaseCli.ToString().ToLowerInvariant(), ["custom"] = a.Custom, ["defaultModel"] = a.DefaultModel,
        ["argsBefore"] = new JsonArray([.. a.ArgsBefore.Select(s => (JsonNode)s)]), ["argsAfter"] = new JsonArray([.. a.ArgsAfter.Select(s => (JsonNode)s)]),
        ["removedArgs"] = new JsonArray([.. a.RemovedArgs.Select(s => (JsonNode)s)]), ["createdAt"] = a.CreatedAt, ["updatedAt"] = a.UpdatedAt,
    };

    private static JsonNode Json(AgentChange c) => new JsonObject
    {
        ["id"] = c.Id, ["at"] = c.At, ["kind"] = c.Kind, ["subject"] = c.Subject, ["fromAgent"] = c.FromAgent, ["toAgent"] = c.ToAgent,
        ["fromModel"] = c.FromModel, ["toModel"] = c.ToModel, ["effectiveModel"] = c.EffectiveModel, ["actorVia"] = c.ActorVia, ["actorUser"] = c.ActorUser,
        ["how"] = c.How, ["detail"] = c.Detail,
    };

    private static string? OptStr(JsonObject? p, string key) => p?[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    private static string Str(JsonObject? p, string key) => OptStr(p, key) ?? throw new RpcException("invalid_params", $"'{key}' is required");

    private static long Id(JsonObject? p) => p?["id"] is JsonValue v && v.TryGetValue<long>(out var n) ? n : throw new RpcException("invalid_params", "'id' is required");

    private static AgentCli Cli(JsonObject? p) =>
        AgentProfileStore.BuiltIn(Str(p, "baseCli")) ?? throw new RpcException("invalid_params", $"'{Str(p, "baseCli")}' is not a built-in agent");

    private static List<string> Strings(JsonObject? p, string key) =>
        p?[key] is JsonArray a ? [.. a.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : throw new RpcException("invalid_params", $"'{key}' is a list of strings"))] : [];
}
