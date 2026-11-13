using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Yawble.Core.Ipc;

namespace Yawble.Core.Integrations;

/// <summary>A watch (0021): an integration's read-only command looked at every interval, a new item an event.</summary>
public sealed record WatchRow(long Id, long TeamId, string Name, string Integration, IReadOnlyList<string> Args, string KeyField, string EventName,
    int IntervalSec, bool Enabled, string? Seen, DateTimeOffset? NextDueAt, string? LastOutcome, string? LastReason);

/// <summary>
/// Watches (an integration polls, an agent reacts; the polling needs no model run): an integration's
/// command the team's rules allow (read-only, e.g. gh pr list --json number,title,url) runs on this computer every
/// interval; its JSON output is read as a list of items, and each item whose key it has not seen becomes an event for
/// the team. The first look only remembers. At most 20 events per look.
/// </summary>
public sealed partial class IntegrationService
{
    public const int MinWatchIntervalSec = 60, MaxEventsPerLook = 20, MaxSeenKeys = 2000;

    private static bool IsWatchMethod(string method) => method is "integrations.watches" or "integrations.saveWatch" or "integrations.deleteWatch" or "integrations.runWatch";

    private async Task<JsonNode?> DispatchWatchAsync(string method, JsonObject? p, CancellationToken ct) => method switch
    {
        "integrations.watches" => WatchesJson(Watches(OptLong(p, "teamId"))),
        "integrations.saveWatch" => SaveWatch(OptLong(p, "teamId") ?? throw new RpcException("bad_params", "which team?"), Str(p, "name"), Str(p, "integration"),
            (p?["args"] as JsonArray ?? []).Select(a => a?.ToString() ?? "").Where(a => a.Length > 0).ToList(), Str(p, "keyField"), Str(p, "eventName"),
            (int)(OptLong(p, "intervalSec") ?? 300), OptLong(p, "id")),
        "integrations.deleteWatch" => DeleteWatch(OptLong(p, "id") ?? throw new RpcException("bad_params", "which watch?")),
        "integrations.runWatch" => new JsonObject { ["events"] = await LookAsync(Watches(null).FirstOrDefault(w => w.Id == OptLong(p, "id"))
            ?? throw new ArgumentException("no such watch"), ct) },
        _ => throw new RpcException("unknown_method", $"no method named '{method}'"),
    };

    private JsonNode SaveWatch(long teamId, string name, string integration, IReadOnlyList<string> args, string keyField, string eventName, int intervalSec, long? id)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("a watch needs a name");
        if (!System.Text.RegularExpressions.Regex.IsMatch(eventName.Trim(), @"^[A-Za-z0-9._-]{1,100}$"))
            throw new ArgumentException("the event's name is letters, digits, . _ - (e.g. web.pr)");
        if (string.IsNullOrWhiteSpace(keyField)) throw new ArgumentException("give the field that names an item (e.g. number, id, url)");
        if (intervalSec < MinWatchIntervalSec) throw new ArgumentException($"a watch looks every {MinWatchIntervalSec} seconds at most often");
        var row = EnabledFor(teamId).FirstOrDefault(r => string.Equals(r.Name, integration, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"{integration} is not enabled for this team");
        var (decision, _) = CliRules.Decide(args, CliCatalog.Of(row.Name) ?? CliCatalog.Generic(row.Name), Rules(row.Id, teamId), Rules(row.Id, null));
        if (decision != Decisions.Allow)
            throw new ArgumentException($"`{row.Name} {CliRules.Join(args)}` is not allowed for this team without asking, and a watch runs with nobody there: allow it with a rule first");
        var now = Now();
        if (id is { } existing)
            Exec("""
                UPDATE watches SET name = $n, integration = $i, args = $a, key_field = $k, event_name = $e, interval_sec = $s, seen = NULL,
                       next_due_at = NULL, updated_at = $now WHERE id = $id AND team_id = $t;
                """, ("$n", name.Trim()), ("$i", row.Name), ("$a", JsonSerializer.Serialize(args)), ("$k", keyField.Trim()), ("$e", eventName.Trim()),
                ("$s", intervalSec), ("$now", now), ("$id", existing), ("$t", teamId));
        else
            Exec("""
                INSERT INTO watches (team_id, name, integration, args, key_field, event_name, interval_sec, created_at, updated_at)
                VALUES ($t, $n, $i, $a, $k, $e, $s, $now, $now);
                """, ("$t", teamId), ("$n", name.Trim()), ("$i", row.Name), ("$a", JsonSerializer.Serialize(args)), ("$k", keyField.Trim()),
                ("$e", eventName.Trim()), ("$s", intervalSec), ("$now", now));
        return new JsonObject { ["ok"] = true };
    }

    private JsonNode DeleteWatch(long id)
    {
        Exec("DELETE FROM watches WHERE id = $id;", ("$id", id));
        return new JsonObject { ["ok"] = true };
    }

    /// <summary>The watch loop: looks at every due watch, outside the bus's gate (a command can take seconds).</summary>
    public async Task RunWatchesAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var now = _clock.GetUtcNow();
                foreach (var w in Watches(null).Where(w => w.Enabled && (w.NextDueAt is null || w.NextDueAt <= now)))
                {
                    try { await LookAsync(w, stop); }
                    catch (Exception e) when (e is not OperationCanceledException) { RecordWatch(w.Id, "failed", e.Message, null); }
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // A bad pass never ends the loop.
                _ = e;
            }
            try { await Task.Delay(TimeSpan.FromSeconds(15), stop); } catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>One look: run the command, find the items not seen before, publish one event each. The events published.</summary>
    public async Task<int> LookAsync(WatchRow w, CancellationToken ct)
    {
        var row = EnabledFor(w.TeamId).FirstOrDefault(r => string.Equals(r.Name, w.Integration, StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            RecordWatch(w.Id, "skipped", $"{w.Integration} is no longer enabled for the team", w.Seen);
            return 0;
        }
        var (decision, _) = CliRules.Decide(w.Args, CliCatalog.Of(row.Name) ?? CliCatalog.Generic(row.Name), Rules(row.Id, w.TeamId), Rules(row.Id, null));
        if (decision != Decisions.Allow)
        {
            RecordWatch(w.Id, "skipped", "the rules no longer allow its command without asking", w.Seen);
            return 0;
        }
        var output = await RunAsync(row, w.Args, w.TeamId, ct);
        Insert(row.Name, null, $"watch:{w.Name}", null, w.Args, "watch", null, output, new Provenance.Actor(Provenance.Actor.Host, null));
        if (output.ExitCode != 0)
        {
            RecordWatch(w.Id, "failed", Clip((output.Stderr + output.Stdout).Trim(), 300), w.Seen);
            return 0;
        }
        JsonArray items;
        try
        {
            items = JsonNode.Parse(output.Stdout) switch
            {
                JsonArray a => a,
                JsonObject o when o.Select(kv => kv.Value).OfType<JsonArray>().FirstOrDefault() is { } inner => inner,
                _ => throw new JsonException("not a list"),
            };
        }
        catch (JsonException)
        {
            RecordWatch(w.Id, "failed", "the command's output is not a JSON list (ask the CLI for JSON, e.g. --json number,title,url)", w.Seen);
            return 0;
        }
        var keyed = items.OfType<JsonObject>().Select(i => (Key: i[w.KeyField]?.ToString(), Item: i)).Where(x => !string.IsNullOrEmpty(x.Key)).ToList();
        var seen = w.Seen is null ? null : JsonSerializer.Deserialize<List<string>>(w.Seen) ?? [];
        var fresh = seen is null ? [] : keyed.Where(x => !seen.Contains(x.Key!)).Take(MaxEventsPerLook).ToList();
        foreach (var (_, item) in fresh) await bus.PublishEventAsync(w.TeamId, w.EventName, item, $"watch:{w.Id}", ct);
        var all = (seen ?? []).Concat(keyed.Select(x => x.Key!)).Distinct().TakeLast(MaxSeenKeys).ToList();
        RecordWatch(w.Id, seen is null ? "remembered" : fresh.Count > 0 ? "events" : "nothing new",
            seen is null ? $"{keyed.Count} item(s) remembered; new ones become events" : $"{fresh.Count} event(s)", JsonSerializer.Serialize(all));
        return fresh.Count;
    }

    private void RecordWatch(long id, string outcome, string reason, string? seen)
    {
        var w = Watches(null).FirstOrDefault(x => x.Id == id);
        var next = _clock.GetUtcNow().AddSeconds(Math.Max(MinWatchIntervalSec, w?.IntervalSec ?? 300));
        Exec("UPDATE watches SET last_outcome = $o, last_reason = $r, seen = $s, next_due_at = $n WHERE id = $id;",
            ("$o", outcome), ("$r", Clip(reason, 500)), ("$s", seen), ("$n", next.ToString("O", CultureInfo.InvariantCulture)), ("$id", id));
    }

    public IReadOnlyList<WatchRow> Watches(long? teamId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, team_id, name, integration, args, key_field, event_name, interval_sec, enabled, seen, next_due_at, last_outcome, last_reason " +
            $"FROM watches {(teamId is null ? "" : "WHERE team_id = $t")} ORDER BY id;";
        if (teamId is not null) cmd.Parameters.AddWithValue("$t", teamId);
        using var r = cmd.ExecuteReader();
        var list = new List<WatchRow>();
        while (r.Read())
            list.Add(new WatchRow(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), JsonSerializer.Deserialize<List<string>>(r.GetString(4)) ?? [],
                r.GetString(5), r.GetString(6), r.GetInt32(7), r.GetInt64(8) == 1, S(r, 9),
                S(r, 10) is { } n ? DateTimeOffset.Parse(n, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) : null, S(r, 11), S(r, 12)));
        return list;
    }

    private static JsonNode WatchesJson(IReadOnlyList<WatchRow> watches) => new JsonArray(watches.Select(w => (JsonNode?)new JsonObject
    {
        ["id"] = w.Id, ["teamId"] = w.TeamId, ["name"] = w.Name, ["integration"] = w.Integration, ["args"] = new JsonArray(w.Args.Select(a => (JsonNode?)a).ToArray()),
        ["keyField"] = w.KeyField, ["eventName"] = w.EventName, ["intervalSec"] = w.IntervalSec, ["enabled"] = w.Enabled,
        ["nextDueAt"] = w.NextDueAt?.ToString("O"), ["lastOutcome"] = w.LastOutcome, ["lastReason"] = w.LastReason,
    }).ToArray());
}
