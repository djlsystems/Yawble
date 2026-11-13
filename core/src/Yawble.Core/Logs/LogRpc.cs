using System.Text.Json.Nodes;
using Yawble.Core.Ipc;
using Yawble.Core.Provenance;

namespace Yawble.Core.Logs;

/// <summary>
/// The Logs dialog's calls. Reading (logs.sources, logs.query, logs.values) is for the window and cloud viewers alike;
/// what changes the logs (logs.prunePreview, logs.prune, logs.setRetention) is the person's, in the window on this
/// computer only.
/// </summary>
public sealed class LogRpc(LogService logs)
{
    public static bool Handles(string method) => method.StartsWith("logs.", StringComparison.Ordinal);

    public const string LocalOnly = "logs are pruned, and how long they are kept is set, by the person in the app's window on this computer";

    public static bool ChangesLogs(string method) => method is "logs.prunePreview" or "logs.prune" or "logs.setRetention";

    public Task<JsonNode?> DispatchAsync(string method, JsonObject? p, CancellationToken ct)
    {
        if (ChangesLogs(method) && Actor.Current is { Via: not Actor.App }) throw new RpcException("forbidden", LocalOnly);
        try
        {
            JsonNode? result = method switch
            {
                "logs.sources" => logs.Sources(),
                "logs.who" => logs.Who(),
                "logs.query" => logs.Query(QueryOf(p)),
                "logs.values" => logs.Values(Required(p, "source"), Required(p, "column"), Text(p, "team"), Text(p, "member")),
                "logs.prunePreview" => Preview(logs.PrunePreview(Required(p, "source"), Cutoff(p))),
                "logs.prune" => Prune(p),
                "logs.setRetention" => SetRetention(p),
                _ => throw new RpcException("unknown_method", $"no method named '{method}'"),
            };
            return Task.FromResult<JsonNode?>(result);
        }
        catch (ArgumentException e)
        {
            throw new RpcException("bad_params", e.Message);
        }
    }

    private JsonNode Prune(JsonObject? p)
    {
        var source = Required(p, "source");
        var cutoff = Cutoff(p);
        if (p?["confirm"]?.GetValue<bool>() != true)
            throw new RpcException("needs_confirm", "say yes first: logs.prunePreview says what this deletes, then logs.prune with confirm");
        var deleted = logs.Prune(source, cutoff);
        return new JsonObject { ["deleted"] = deleted, ["source"] = source, ["cutoff"] = cutoff };
    }

    private JsonNode SetRetention(JsonObject? p)
    {
        var source = Required(p, "source");
        var days = p?["days"]?.GetValue<int>() ?? throw new ArgumentException("days is required (0: keep forever)");
        logs.SetRetention(source, days);
        return new JsonObject { ["source"] = source, ["retentionDays"] = logs.RetentionDays(source) };
    }

    /// <summary>The cutoff: an ISO time (<c>before</c>), or <c>olderThanDays</c> from now, or <c>all</c>.</summary>
    private string Cutoff(JsonObject? p)
    {
        if (p?["all"]?.GetValue<bool>() == true) return "9999-12-31";
        if (Text(p, "before") is { } before) return before;
        if (p?["olderThanDays"] is JsonValue v && v.TryGetValue<int>(out var days) && days >= 0) return logs.CutoffFor(days);
        throw new ArgumentException("give before (an ISO time), olderThanDays, or all");
    }

    private static JsonObject Preview(LogPrunePreview v) => new()
    {
        ["source"] = v.Source, ["title"] = v.Title, ["cutoff"] = v.Cutoff, ["count"] = v.Count, ["oldest"] = v.Oldest,
        ["newest"] = v.Newest, ["loss"] = v.Loss, ["files"] = v.Files,
    };

    private static LogQuery QueryOf(JsonObject? p)
    {
        var filters = new Dictionary<string, string>();
        if (p?["filters"] is JsonObject f)
            foreach (var (k, v) in f)
                if (v is JsonValue jv && jv.ToString() is { Length: > 0 } s) filters[k] = s;
        return new LogQuery(
            Required(p, "source"), Text(p, "team"), Text(p, "member"), Text(p, "from"), Text(p, "to"), Text(p, "text"), filters,
            Text(p, "sort"), p?["descending"]?.GetValue<bool>() ?? true, p?["offset"]?.GetValue<int>() ?? 0,
            p?["limit"]?.GetValue<int>() ?? 100, Text(p, "asOf"));
    }

    private static string? Text(JsonObject? p, string key) =>
        p?[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Trim().Length > 0 ? s.Trim() : null;

    private static string Required(JsonObject? p, string key) => Text(p, key) ?? throw new ArgumentException($"{key} is required");
}
