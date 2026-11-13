using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Yawble.Core.Board;
using Yawble.Core.Bus;
using Yawble.Core.Integrations;
using Yawble.Core.Teams;

namespace Yawble.Core.Concierge;

/// <summary>One repository the Concierge may look at: where it is, its base branch, and the member clones of it.</summary>
public sealed record GitRepoTarget(string Team, string Repo, string Path, string BaseBranch, IReadOnlyList<GitCloneTarget> Clones);

/// <summary>One member's clone of a repository (it may not exist yet).</summary>
public sealed record GitCloneTarget(string Member, string Path);

/// <summary>
/// The Concierge's read tools: backlog, board, system status, git state per team, connections, usage.
/// They only read, so they never need approval; every answer is scrubbed of anything that looks like a secret before it
/// leaves (a Connection's value is never read in the first place). A check that could not run is said, not skipped.
/// Served by <see cref="ConciergeTools"/>, which hands them every call whose name is in <see cref="Names"/>.
/// </summary>
public sealed class ConciergeReadTools(
    BusEngine bus,
    Func<string, JsonObject?, CancellationToken, Task<JsonNode?>> ui,
    string? connectionString = null,
    Func<string?, CancellationToken, Task<IReadOnlyList<GitRepoTarget>>>? gitTargets = null,
    HostGit? git = null,
    Func<GitRepoTarget, CancellationToken, Task<IReadOnlyDictionary<string, string>?>>? openPrs = null,
    TimeProvider? clock = null,
    bool remote = true)
{
    // remote: whether this turn's message came from anywhere but the app's window on this computer. Not said means remote:
    // the reads keep to what RemoteAccess shows a Cloud Link viewer, and backup_now is refused.

    /// <summary>The read tools a turn from the Cloud Link may not use (they change something, or say what stays on the computer).</summary>
    public static readonly IReadOnlyList<string> LocalOnly = ["backup_now"];

    public static readonly IReadOnlyList<string> Names =
        ["backlog_list", "backlog_item", "board_list", "board_card", "system_status", "team_git", "integrations_status", "usage", "doctor", "backup_now"];

    public static bool Handles(string name) => Names.Contains(name);

    private readonly HostGit? _git = git ?? bus.Repos?.Git;
    private readonly Func<string?, CancellationToken, Task<IReadOnlyList<GitRepoTarget>>> _targets = gitTargets ?? ((team, ct) => TargetsFromAsync(bus, team, ct));

    public async Task<string> CallAsync(string name, JsonObject args, CancellationToken ct)
    {
        if (remote && LocalOnly.Contains(name)) throw new ArgumentException(ConciergeActions.RemoteRefusal(name));
        var text = name switch
        {
            "backlog_list" => await BacklogListAsync(Flag(args, "archived"), ct),
            "backlog_item" => await BacklogItemAsync(Required(args, "item"), ct),
            "board_list" => await BoardListAsync(Optional(args, "team"), ct),
            "board_card" => await BoardCardAsync(Required(args, "card"), ct),
            "system_status" => await SystemStatusAsync(ct),
            "team_git" => await TeamGitAsync(Optional(args, "team"), ct),
            "integrations_status" => await IntegrationsAsync(ct),
            "usage" => await UsageAsync(Optional(args, "team"), Int(args, "days") ?? 7, ct),
            "doctor" => await DoctorAsync(ct),
            "backup_now" => await BackupNowAsync(ct),
            _ => throw new ArgumentException($"unknown tool: {name}"),
        };
        return Scrub(text);
    }

    // ------------------------------------------------------------------------------------------------ doctor and backups

    private async Task<string> DoctorAsync(CancellationToken ct)
    {
        JsonObject? r;
        try { r = await ui("ops.doctor", null, ct) as JsonObject; }
        catch (Ipc.RpcException e) { throw new ArgumentException($"the health check is not available from here ({e.Message}); check not run."); }
        var checks = (r?["checks"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        // Over the Cloud Link, the Connections and Cloud Link checks (names, accounts) stay on the computer, as in RemoteAccess.
        var held = remote ? checks.RemoveAll(c => S(c, "id") is "connections" or "cloudLink") : 0;
        if (checks.Count == 0) return "The health check returned nothing (check not run).";
        var sb = new StringBuilder("App health (the same list as Settings > Health):\n");
        foreach (var c in checks)
        {
            sb.Append($"- {S(c, "title")}: {S(c, "status").ToUpperInvariant()}: {S(c, "detail")}");
            var fixes = (c["fixes"] as JsonArray ?? []).OfType<JsonObject>().Where(f => S(f, "label").Length > 0).Select(f => $"{S(f, "label")} [fix id {S(f, "id")}]").ToList();
            if (fixes.Count > 0) sb.Append($" (fixes: {string.Join("; ", fixes)}; run one with doctor_fix when the person asks, or they use Settings > Health)");
            sb.AppendLine();
        }
        if (held > 0) sb.AppendLine("- Connections and Cloud Link checks: only on the computer itself (not shown over the Cloud Link).");
        var bad = checks.Count(c => S(c, "status") == "fail");
        var warn = checks.Count(c => S(c, "status") == "warn");
        sb.AppendLine(bad + warn == 0 ? "Everything is OK." : $"{bad} failing, {warn} warning. Offer the one fix that matters most (doctor_fix runs it when the person asks).");
        return sb.ToString().TrimEnd();
    }

    private async Task<string> BackupNowAsync(CancellationToken ct)
    {
        try
        {
            var b = await ui("ops.backupNow", null, ct) as JsonObject;
            return b is null ? "The backup did not report anything (check not run)." : $"Backed up: {S(b, "name")} ({S(b, "size")} bytes, {S(b, "time")}). Restoring is only for the person, in Settings > General.";
        }
        catch (Ipc.RpcException e) { throw new ArgumentException($"the backup was not made: {e.Message}"); }
    }

    // ------------------------------------------------------------------------------------------------ backlog

    // board.* and backlog.* take the one routed path (ConciergeService.Route), like the action tools.
    private async Task<JsonNode?> BoardCall(string method, JsonObject p, CancellationToken ct)
    {
        try { return await ui(method, p, ct); }
        catch (Ipc.RpcException e) when (e.Code is "not_ready" or "unknown_method")
        { throw new ArgumentException("the board is not running in this app, so there is no backlog to read"); }
    }

    private async Task<string> BacklogListAsync(bool archived, CancellationToken ct)
    {
        var items = (await BoardCall("backlog.list", new JsonObject { ["archived"] = archived }, ct) as JsonArray ?? []).OfType<JsonObject>().ToList();
        if (items.Count == 0) return archived ? "The archive is empty." : "The backlog is empty.";
        var sb = new StringBuilder(archived ? "Archived backlog items:\n" : "Backlog (live items):\n");
        foreach (var i in items)
        {
            sb.Append($"- {S(i, "label")} [{S(i, "state")}] {S(i, "title")}");
            if (S(i, "team") is { Length: > 0 } meant) sb.Append($" (meant for {meant})");
            if (S(i, "dispatchedTeam") is { Length: > 0 } sent)
                sb.Append($"; dispatched to {sent} (workflow {S(i, "dispatchedWorkflow")}){(i["inFlight"]?.GetValue<bool>() == true ? ", in flight" : "")}, {S(i, "dispatchCount")} time(s)");
            if (archived && S(i, "archivedAt") is { Length: > 0 } at) sb.Append($"; archived {at}");
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    private async Task<string> BacklogItemAsync(string item, CancellationToken ct)
    {
        var id = BoardStore.ItemId(item.Trim()) ?? (long.TryParse(item.Trim(), out var n) ? n : throw new ArgumentException($"'{item}' is not a backlog item; use its label, e.g. B006"));
        JsonObject o;
        try
        {
            o = await BoardCall("backlog.item", new JsonObject { ["id"] = id }, ct) as JsonObject ?? throw new ArgumentException($"there is no item {item}");
        }
        catch (Ipc.RpcException e)
        {
            throw new ArgumentException(e.Message);
        }
        var sb = new StringBuilder();
        sb.AppendLine($"{S(o, "label")} [{S(o, "state")}] {S(o, "title")}");
        if (S(o, "team") is { Length: > 0 } meant) sb.AppendLine($"Meant for: {meant}");
        if (S(o, "archivedAt") is { Length: > 0 } arch) sb.AppendLine($"Archived: {arch}");
        sb.AppendLine($"Created {S(o, "createdAt")}, updated {S(o, "updatedAt")}");
        sb.AppendLine().AppendLine(S(o, "body"));
        var dispatches = (o["dispatches"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        sb.AppendLine().AppendLine(dispatches.Count == 0 ? "Never dispatched." : "Dispatch history (oldest first):");
        foreach (var d in dispatches)
        {
            sb.Append($"- {S(d, "dispatchedAt")} to {S(d, "team")}{(d["teamGone"]?.GetValue<bool>() == true ? " (team removed)" : "")}, workflow {S(d, "workflow")}");
            if (S(d, "state") is { Length: > 0 } st) sb.Append($" [{st}]");
            sb.Append($"; cards {S(d, "cardsDone")}/{S(d, "cards")} done; {S(d, "runs")} run(s), {S(d, "inputTokens")} input / {S(d, "outputTokens")} output tokens");
            if (S(d, "ending") is { Length: > 0 } end) sb.Append($"; ended: {end}");
            if (S(d, "dispatchedBy") is { Length: > 0 } by) sb.Append($"; by {by}");
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    // ------------------------------------------------------------------------------------------------ board

    private async Task<string> BoardListAsync(string? team, CancellationToken ct)
    {
        if (team is not null) await TeamNamedAsync(team, ct); // a wrong name is refused with the list of teams
        var r = await BoardCall("board.list", new JsonObject { ["team"] = team, ["includeCleared"] = true }, ct) as JsonObject;
        var cards = (r?["cards"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var lanes = (r?["lanes"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        if (cards.Count == 0) return team is null ? "No team has any cards on the board." : $"{team} has no cards on the board.";
        var sb = new StringBuilder();
        foreach (var lane in lanes)
        {
            var inLane = cards.Where(c => S(c, "lane") == S(lane, "id")).ToList();
            sb.AppendLine($"{S(lane, "label")} ({inLane.Count}):");
            foreach (var c in inLane.OrderBy(c => S(c, "team")).ThenBy(c => c["id"]!.GetValue<long>()))
            {
                sb.Append($"- card {S(c, "id")} [{S(c, "team")}{(S(c, "member") is { Length: > 0 } m ? "/" + m : "")}] {S(c, "title")} (status {S(c, "status")}");
                if (S(c, "item") is { Length: > 0 } item) sb.Append($", from {item}");
                if (c["memberState"] is JsonObject ms) sb.Append($", member {S(ms, "state")}{(S(ms, "attention") is { Length: > 0 } a ? ", " + a : "")}");
                sb.AppendLine(")");
            }
        }
        return sb.ToString().TrimEnd();
    }

    private async Task<string> BoardCardAsync(string card, CancellationToken ct)
    {
        if (!long.TryParse(card.Trim().TrimStart('#'), out var id)) throw new ArgumentException($"'{card}' is not a card number");
        JsonObject o;
        try
        {
            o = await BoardCall("board.card", new JsonObject { ["id"] = id }, ct) as JsonObject ?? throw new ArgumentException($"there is no card {id}");
        }
        catch (Ipc.RpcException e)
        {
            throw new ArgumentException(e.Message);
        }
        var sb = new StringBuilder();
        sb.AppendLine($"Card {id}: {S(o, "title")}");
        sb.AppendLine($"Team {S(o, "team")}{(S(o, "member") is { Length: > 0 } m ? $", member {m}" : "")}; lane {S(o, "lane")}, status {S(o, "status")}" +
                      $"{(S(o, "item") is { Length: > 0 } item ? $"; from backlog {item}" : "")}{(S(o, "workflow") is { Length: > 0 } w ? $"; workflow {w}" : "")}");
        sb.AppendLine($"Created {S(o, "createdAt")}, updated {S(o, "updatedAt")}");
        sb.AppendLine().AppendLine(S(o, "body"));
        sb.AppendLine().AppendLine("Trail:");
        foreach (var t in (o["trail"] as JsonArray ?? []).OfType<JsonObject>())
            sb.AppendLine($"  {S(t, "at")} {S(t, "source")} {S(t, "kind")}: {S(t, "text")}");
        return sb.ToString().TrimEnd();
    }

    // ------------------------------------------------------------------------------------------------ system

    private async Task<string> SystemStatusAsync(CancellationToken ct)
    {
        var sb = new StringBuilder();
        var slots = await bus.RunSlotsAsync(ct);
        sb.AppendLine($"Run slots: {slots.Running} in use of {slots.Limit}; {slots.Queued} run(s) queued for a slot.");
        sb.AppendLine($"Heavy-work lease: {await bus.LeaseStatusTextAsync(ct)}");
        var teams = await bus.TeamsAsync(ct);
        var needsYou = 0;
        var decisionLines = new List<string>();
        sb.AppendLine(teams.Count == 0 ? "Teams: none." : "Teams:");
        foreach (var t in teams)
        {
            var cards = await bus.AgentCardsAsync(t.Id, ct);
            var running = cards.Where(c => c.State == "running").Select(c => c.Member.Name).ToList();
            var queued = cards.Where(c => c.QueuePosition is not null).Select(c => $"{c.Member.Name} ({c.QueuePosition} of {c.QueueLength})").ToList();
            sb.AppendLine($"- {t.Name}{(t.Paused ? " [paused]" : "")}{(t.Archived ? " [archived]" : "")}: {t.Members} agent(s); " +
                          $"running: {(running.Count == 0 ? "none" : string.Join(", ", running))}; queued: {(queued.Count == 0 ? "none" : string.Join(", ", queued))}");
            if (bus.Repos is { } repos && await repos.BehindLineAsync(t.Id, t.Name, ct) is { } behind) sb.AppendLine($"  {behind}.");
            foreach (var d in await bus.OpenDecisionsAsync(t.Name, ct))
            {
                needsYou++;
                decisionLines.Add($"- {t.Name}: question {d.Seq} from {(d.Source == MessageTypes.PersonSource ? "the person" : d.Source)}: {Clip(DecisionText(d), 300)}");
            }
        }
        var asks = bus.Integrations?.PendingCount() ?? 0;
        sb.AppendLine(decisionLines.Count == 0 ? "Open decisions: none." : "Open decisions across teams:");
        foreach (var l in decisionLines) sb.AppendLine(l);
        sb.AppendLine($"Tool calls waiting for the person's approval: {asks}.");
        sb.AppendLine($"Needs the person: {needsYou + asks} item(s) ({needsYou} decision(s), {asks} approval(s)).");
        if (remote)
        {
            sb.AppendLine("Cloud Link: only on the computer itself (its state is not shown over the Cloud Link).");
            return sb.ToString().TrimEnd();
        }
        try
        {
            var link = await ui("link.status", null, ct) as JsonObject;
            sb.AppendLine(link is null ? "Cloud Link: state not available from here (check not run)."
                : "Cloud Link: " + string.Join(", ", link.Where(kv => kv.Value is JsonValue && !SecretKey(kv.Key)).Select(kv => $"{kv.Key} {kv.Value}")));
        }
        catch (Exception e) when (e is Ipc.RpcException or ArgumentException)
        {
            sb.AppendLine($"Cloud Link: state not available from here ({e.Message}); check not run.");
        }
        return sb.ToString().TrimEnd();
    }

    // ------------------------------------------------------------------------------------------------ integrations

    private async Task<string> IntegrationsAsync(CancellationToken ct)
    {
        var teams = (await bus.TeamsAsync(ct)).ToDictionary(t => t.Id, t => t.Name);
        string Teams(JsonObject o) =>
            o["all"]?.GetValue<bool>() == true ? "all teams"
            : (o["teams"] as JsonArray ?? []).Select(t => teams.TryGetValue(t!.GetValue<long>(), out var n) ? n : "team " + t).ToList() is { Count: > 0 } l ? string.Join(", ", l) : "no team";
        string Status(JsonObject o) => S(o, "status") is { Length: > 0 } s ? s : "not checked yet";
        // A Connection, an API's or a website's: its name and status only (the list RPCs never read a secret; a masked
        // value is in 'fields[].shown' of integrations.connections, which is never read here).
        string Linked(JsonNode? c) => c is JsonObject o ? $"connection {S(o, "name")} ({Status(o)})" : "no connection";
        var sb = new StringBuilder();

        sb.AppendLine("Model sign-ins (the agents' own accounts; never a value):");
        try
        {
            var c = await ui("credentials.status", null, ct) as JsonObject;
            if (c is null) sb.AppendLine("  not available from here (check not run).");
            else
            {
                sb.AppendLine($"  Claude: {Connected(c["claude"]?["subscription"], "subscription")}; {Connected(c["claude"]?["apiKey"], "API key")}; in use: {S(c["claude"] as JsonObject, "mode")}.");
                sb.AppendLine($"  Grok: {(c["grok"]?["login"]?["signedIn"]?.GetValue<bool>() == true ? "signed in" : "not signed in")}; {Connected(c["grok"]?["apiKey"], "xAI key")}; in use: {S(c["grok"] as JsonObject, "mode")}.");
                sb.AppendLine($"  Stored in: {S(c, "storage")}.");
            }
        }
        catch (Exception e) when (e is Ipc.RpcException or ArgumentException)
        {
            sb.AppendLine($"  not available from here ({e.Message}); check not run.");
        }

        if (bus.Integrations is not { } integrations)
        {
            sb.AppendLine("Connections, APIs, websites, the person's tools and MCP servers: not available from here (check not run).");
            return sb.ToString().TrimEnd();
        }

        async Task<JsonNode?> Read(string method, string what)
        {
            try { return await integrations.DispatchAsync(method, null, ct); }
            catch (Exception e) when (e is Ipc.RpcException or ArgumentException)
            {
                sb.AppendLine($"{what}: not available from here ({e.Message}); check not run.");
                return null;
            }
        }

        // Connections: the shared sign-ins the APIs, websites and MCP servers use. RemoteAccess keeps integrations.connection*
        // from Cloud Link viewers, so over the Cloud Link this section is left out (the API, website and MCP lines below
        // name each one's connection and its status, as integrations.apiList / mcpList show them remotely).
        if (remote)
            sb.AppendLine("Connections: only on the computer itself (not shown over the Cloud Link); the lines below name the connection each uses and its status.");
        else if (await Read("integrations.connections", "Connections") is JsonArray connections)
        {
            sb.AppendLine("Connections (shared sign-ins; never a value):");
            if (connections.Count == 0) sb.AppendLine("  none.");
            foreach (var c in connections.OfType<JsonObject>())
            {
                sb.Append($"  - {S(c, "name")} ({S(c, "kind")}): {Status(c)}");
                if (S(c, "statusReason") is { Length: > 0 } why) sb.Append($" ({why})");
                var used = (c["usedBy"] as JsonArray ?? []).Select(u => u?.ToString() ?? "").Where(u => u.Length > 0).ToList();
                sb.AppendLine(used.Count == 0 ? "; used by nothing" : $"; used by: {string.Join(", ", used)}");
            }
        }

        // APIs and websites share one list (kind http or website). Their addresses are not printed: one may carry a token.
        if (await Read("integrations.apiList", "APIs and websites") is JsonArray apis)
        {
            var http = apis.OfType<JsonObject>().Where(a => S(a, "kind") != "website").ToList();
            var sites = apis.OfType<JsonObject>().Where(a => S(a, "kind") == "website").ToList();
            sb.AppendLine("APIs:");
            if (http.Count == 0) sb.AppendLine("  none.");
            foreach (var a in http)
                sb.AppendLine($"  - {S(a, "name")}{(S(a, "title") is { Length: > 0 } title ? $" (\"{Clip(title, 80)}\")" : "")}: {Status(a)}; {Linked(a["connection"])}; turned on for {Teams(a)}");
            sb.AppendLine("Websites:");
            if (sites.Count == 0) sb.AppendLine("  none.");
            foreach (var w in sites)
                sb.AppendLine($"  - {S(w, "name")}: {Status(w)}; {(w["connection"] is JsonObject ? Linked(w["connection"]).Replace("connection ", "login ", StringComparison.Ordinal) : "public (no login)")}; turned on for {Teams(w)}");
        }

        if (await Read("integrations.list", "The person's tools (gh, az, git, ...)") is { } cli)
        {
            sb.AppendLine("The person's tools (gh, az, git, ...):");
            var list = (cli as JsonObject)?["integrations"] as JsonArray ?? [];
            var shown = 0;
            foreach (var i in list.OfType<JsonObject>().Where(i => i["enabled"]?.GetValue<bool>() == true || S(i, "status") is { Length: > 0 }))
            {
                shown++;
                sb.AppendLine($"  - {S(i, "name")} ({S(i, "label")}): {Status(i)}; turned on for {(i["enabled"]?.GetValue<bool>() == true ? Teams(i) : "no team")}");
            }
            if (shown == 0) sb.AppendLine("  none.");
        }

        if (await Read("integrations.mcpList", "MCP servers") is { } mcp)
        {
            sb.AppendLine("MCP servers:");
            var servers = (mcp as JsonArray ?? (mcp as JsonObject)?["servers"] as JsonArray ?? []).OfType<JsonObject>().ToList();
            if (servers.Count == 0) sb.AppendLine("  none.");
            // The command, its arguments and its address may hold a token, so only the name, status, connection and teams are said.
            foreach (var m in servers)
                sb.AppendLine($"  - {S(m, "name")}: {Status(m)}, {(m["tools"] as JsonArray)?.Count ?? 0} tool(s); " +
                              $"{(m["connection"] is JsonObject ? Linked(m["connection"]) + "; " : "")}turned on for {Teams(m)}");
        }
        sb.AppendLine($"Tool calls waiting for approval: {integrations.PendingCount()}.");
        return sb.ToString().TrimEnd();
    }

    private static string Connected(JsonNode? s, string what) =>
        s?["connected"]?.GetValue<bool>() == true
            ? $"{what} connected{(s["problem"]?.GetValue<string>() is { Length: > 0 } p ? $" (problem: {p})" : "")}{(s["renewSoon"]?.GetValue<bool>() == true ? " (renew soon)" : "")}"
            : $"{what} not connected";

    // ------------------------------------------------------------------------------------------------ usage

    private async Task<string> UsageAsync(string? team, int days, CancellationToken ct)
    {
        if (connectionString is null) throw new ArgumentException("the usage log is not available in this app");
        days = Math.Clamp(days, 1, 365);
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var since = now.AddDays(-days).ToString("O", CultureInfo.InvariantCulture);
        var rows = new List<(string Team, string Member, string? Run, string Model, long In, long Out, long CacheRead, bool Unread)>();
        using (var c = new SqliteConnection(connectionString))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT member, run_key, model, input_tokens, output_tokens, cache_read_tokens FROM usage_events WHERE at >= $since;";
            cmd.Parameters.AddWithValue("$since", since);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var member = r.IsDBNull(0) ? "(unknown)" : r.GetString(0);
                var slash = member.IndexOf('/');
                rows.Add((slash < 0 ? member : member[..slash], slash < 0 ? member : member[(slash + 1)..], r.IsDBNull(1) ? null : r.GetString(1),
                    r.IsDBNull(2) ? "(unknown model)" : r.GetString(2), r.IsDBNull(3) ? 0 : r.GetInt64(3), r.IsDBNull(4) ? 0 : r.GetInt64(4),
                    r.IsDBNull(5) ? 0 : r.GetInt64(5), r.IsDBNull(3) && r.IsDBNull(4)));
            }
        }
        if (team is not null)
        {
            await TeamNamedAsync(team, ct);
            rows = rows.Where(r => string.Equals(r.Team, team, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        var scope = team is null ? "every team" : team;
        if (rows.Count == 0) return $"No model usage recorded for {scope} in the last {days} day(s).";
        var sb = new StringBuilder($"Model usage for {scope}, last {days} day(s) (as the providers reported it):\n");
        foreach (var g in rows.GroupBy(r => r.Team, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine($"- {g.Key}: {g.Select(r => r.Run).Where(k => k is not null).Distinct().Count()} run(s), {g.Count()} model request(s), " +
                          $"{g.Sum(r => r.In):N0} input, {g.Sum(r => r.Out):N0} output, {g.Sum(r => r.CacheRead):N0} cache-read tokens" +
                          (g.Any(r => r.Unread) ? $"; {g.Count(r => r.Unread)} request(s) had no readable figures" : ""));
            foreach (var m in g.GroupBy(r => r.Member).OrderBy(m => m.Key, StringComparer.OrdinalIgnoreCase))
                sb.AppendLine($"    {m.Key}: {m.Sum(r => r.In):N0} in / {m.Sum(r => r.Out):N0} out ({string.Join(", ", m.Select(r => r.Model).Distinct())})");
        }
        return sb.ToString().TrimEnd();
    }

    // ------------------------------------------------------------------------------------------------ git

    /// <summary>
    /// Put before every git command these tools run: no filesystem monitor, no hooks, no untracked cache, no optional
    /// lock. A repository's own config must not make a read run a program. (HostGit already drops the system and global
    /// config: GIT_CONFIG_NOSYSTEM, GIT_CONFIG_GLOBAL = an empty file.) Member clones are never the repository git runs
    /// in at all: see <see cref="CloneAsync"/>.
    /// </summary>
    public static readonly IReadOnlyList<string> Hardening =
        ["-c", "core.fsmonitor=false", "-c", "core.hooksPath=" + (OperatingSystem.IsWindows() ? "NUL" : "/dev/null"), "-c", "core.untrackedCache=false", "--no-optional-locks"];

    private Task<GitResult> Git(string? path, IEnumerable<string> args, CancellationToken ct) => _git!.TryRunAsync(path, [.. Hardening, .. args], ct);

    private async Task<string> TeamGitAsync(string? team, CancellationToken ct)
    {
        if (team is not null) await TeamNamedAsync(team, ct);
        if (_git is null) throw new ArgumentException("git is not available to the app here, so no repository can be read");
        var targets = await _targets(team, ct);
        if (targets.Count == 0) return team is null ? "No team has a repository yet." : $"{team} has no repository yet (it is made at the team's first run).";
        var sb = new StringBuilder("All counts and remote branches are as of the last fetch: the Concierge never fetches or contacts a remote when it reads.\n");
        var failed = 0;
        foreach (var t in targets)
        {
            sb.AppendLine().AppendLine($"== {t.Team}: {t.Repo} ({t.Path}); base branch {t.BaseBranch}");
            failed += await RepoAsync(sb, t, ct);
        }
        var clones = targets.Any(t => t.Clones.Any(c => Directory.Exists(c.Path)));
        sb.AppendLine().AppendLine(failed > 0 ? $"{failed} check(s) could not run; they are named above."
            : clones ? "Every check ran, apart from uncommitted changes in member clones (never checked: the app does not run git inside an agent's clone)."
            : "Every check ran.");
        return sb.ToString().TrimEnd();
    }

    private async Task<int> RepoAsync(StringBuilder sb, GitRepoTarget t, CancellationToken ct)
    {
        var failed = 0;
        async Task<string?> Out(string path, params string[] args)
        {
            var r = await Git(path, args, ct);
            return r.ExitCode == 0 ? r.Stdout.Trim() : null;
        }
        if (await Out(t.Path, "rev-parse", "--git-dir") is null)
        {
            sb.AppendLine("  check not run: this folder is not a readable git repository.");
            return 1;
        }
        var baseSha = await Out(t.Path, "rev-parse", "--verify", "-q", "refs/heads/" + t.BaseBranch);
        var origin = await Out(t.Path, "config", "--get", "remote.origin.url");
        var originBase = origin is null ? null : await Out(t.Path, "rev-parse", "--verify", "-q", "refs/remotes/origin/" + t.BaseBranch);
        sb.AppendLine($"  {t.BaseBranch}: {(baseSha is null ? "no local branch of that name" : Short(baseSha))}; origin: {(origin is null ? "none (nothing to push to)" : OriginForDisplay(origin))}");
        if (origin is not null)
        {
            if (originBase is null) sb.AppendLine($"  origin has no {t.BaseBranch} branch as of the last fetch.");
            else if (baseSha is not null)
            {
                var ab = await Counts(t.Path, baseSha, originBase);
                sb.AppendLine(ab is null ? "  check not run: could not compare the base branch with origin's."
                    : ab == (0, 0) ? $"  Remote {t.BaseBranch} matches the team's {t.BaseBranch}."
                    : $"  Remote {t.BaseBranch} does NOT match the team's: the team's is {ab.Value.Ahead} ahead and {ab.Value.Behind} behind origin's.");
                if (ab is null) failed++;
            }
        }
        var compare = originBase ?? baseSha;
        var compareName = originBase is not null ? $"origin/{t.BaseBranch}" : t.BaseBranch;

        // Pull requests are a hosting service's, not git's: read for GitHub (through the person's gh) only.
        var host = Yawble.Core.Teams.RepoHost.HostOf(origin);
        var onGitHub = Yawble.Core.Teams.RepoHost.IsGitHub(origin);
        var prs = openPrs is null || !onGitHub ? null : await SafePrs(t, ct);
        sb.AppendLine(origin is null ? "  Pull requests: none (no origin)."
            : host is null ? "  Pull requests: none (origin is a folder on this computer)."
            : !onGitHub ? $"  Pull requests: not read for {host} (only GitHub's are read from here, through gh)."
            : openPrs is null ? "  Open pull requests: check not run (gh is not available here)."
            : prs is null ? "  Open pull requests: check not run (gh is not signed in or did not answer)." : $"  Open pull requests: {prs.Count}.");
        if (openPrs is not null && onGitHub && prs is null) failed++;

        var refs = await Out(t.Path, "for-each-ref", "--format=%(refname)%09%(objectname)%09%(committerdate:short)%09%(subject)", "refs/heads", "refs/remotes/origin");
        if (refs is null) { sb.AppendLine("  check not run: could not list branches."); failed++; }
        else
        {
            var lines = refs.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim().Split('\t', 4)).Where(p => p.Length == 4).ToList();
            var local = lines.Where(p => p[0].StartsWith("refs/heads/", StringComparison.Ordinal)).ToList();
            var remote = lines.Where(p => p[0].StartsWith("refs/remotes/origin/", StringComparison.Ordinal) && p[0] != "refs/remotes/origin/HEAD").ToList();
            var remoteNames = remote.Select(p => p[0]["refs/remotes/origin/".Length..]).ToHashSet();
            sb.AppendLine($"  Local branches ({local.Count}):");
            foreach (var p in local)
            {
                var b = p[0]["refs/heads/".Length..];
                sb.AppendLine("    " + await BranchLine(t, b, p, compare, compareName, prs, remoteNames.Contains(b) ? null : origin is null ? "no origin" : "local only (not on origin)", ct, () => failed++));
            }
            sb.AppendLine($"  Remote branches on origin ({remote.Count}):");
            foreach (var p in remote)
            {
                var b = p[0]["refs/remotes/origin/".Length..];
                sb.AppendLine("    " + await BranchLine(t, "origin/" + b, p, compare, compareName, prs, null, ct, () => failed++, prKey: b));
            }
        }

        sb.AppendLine("  Member clones (read from their branches only; uncommitted changes are never checked, because that would run git inside the agent's clone):");
        if (t.Clones.Count == 0) sb.AppendLine("    none (no members).");
        foreach (var c in t.Clones) failed += await CloneAsync(sb, t, c, baseSha, ct);
        return failed;

        async Task<(int Ahead, int Behind)?> Counts(string path, string a, string b)
        {
            var r = await Out(path, "rev-list", "--left-right", "--count", $"{a}...{b}");
            var parts = r?.Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries);
            return parts is { Length: 2 } && int.TryParse(parts[0], out var x) && int.TryParse(parts[1], out var y) ? (x, y) : null;
        }
    }

    private async Task<string> BranchLine(GitRepoTarget t, string branch, string[] p, string? compare, string compareName, IReadOnlyDictionary<string, string>? prs,
        string? localNote, CancellationToken ct, Action fail, string? prKey = null)
    {
        var sb = new StringBuilder($"{branch}: {Short(p[1])} {p[2]} \"{Clip(p[3], 80)}\"");
        if (compare is null) sb.Append($"; check not run: no {compareName} to compare with");
        else if (p[1] == await Rev(t.Path, compare, ct) && branch != t.BaseBranch && branch != "origin/" + t.BaseBranch) sb.Append($"; same as {compareName}, merged");
        else
        {
            var lr = await Git(t.Path, ["rev-list", "--left-right", "--count", $"{compare}...{p[1]}"], ct);
            var parts = lr.Stdout.Split(['\t', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries);
            if (lr.ExitCode != 0 || parts.Length != 2) { sb.Append("; check not run: could not compare with " + compareName); fail(); }
            else
            {
                var merged = (await Git(t.Path, ["merge-base", "--is-ancestor", p[1], compare], ct)).ExitCode == 0;
                sb.Append($"; {parts[1]} ahead / {parts[0]} behind {compareName}; {(merged ? "merged" : "NOT merged")}");
            }
        }
        if (localNote is not null) sb.Append("; " + localNote);
        var key = prKey ?? branch;
        if (prs is not null && prs.TryGetValue(key, out var pr)) sb.Append($"; open PR: {pr}");
        return sb.ToString();
    }

    private async Task<string?> Rev(string path, string reference, CancellationToken ct)
    {
        var r = await Git(path, ["rev-parse", "--verify", "-q", reference + "^{commit}"], ct);
        return r.ExitCode == 0 ? r.Stdout.Trim() : null;
    }

    private async Task<IReadOnlyDictionary<string, string>?> SafePrs(GitRepoTarget t, CancellationToken ct)
    {
        try { return await openPrs!(t, ct); }
        catch (Exception e) when (e is not OperationCanceledException) { return null; }
    }

    /// <summary>
    /// One member's clone. The clone (its .git/config, hooks, attributes, info/) is the AGENT's to write, so the host never
    /// runs git with it as the repository: a status, diff or log there could start a program the agent named
    /// (core.fsmonitor, a filter or diff driver, a hook) on the host, outside the sandbox. Instead its HEAD file is read,
    /// and its branches are FETCHED into a scratch bare repository of the host's own (the fetch reads the clone's objects;
    /// it runs none of its config), then counted against the team repository's objects. Uncommitted changes need the
    /// clone's working tree and index read by git, so they are not checked, and the answer says so.
    /// </summary>
    private async Task<int> CloneAsync(StringBuilder sb, GitRepoTarget t, GitCloneTarget c, string? baseSha, CancellationToken ct)
    {
        if (!Directory.Exists(c.Path)) { sb.AppendLine($"    {c.Member}: no clone yet."); return 0; }
        if (TeamRepos.CloneGitDirProblem(c.Path) is { } problem) { sb.AppendLine($"    {c.Member}: check not run: {problem}."); return 1; }
        var gitDir = System.IO.Path.Combine(c.Path, ".git");
        var head = CloneHead.Read(gitDir);
        sb.AppendLine($"    {c.Member}: on {(head is null ? "(unreadable)" : head.Branch ?? $"(detached at {Short(head.Detached!)})")}; uncommitted changes: not checked");
        var scratch = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "yawble-concierge-read-" + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            async Task<string?> Out(string path, params string[] args)
            {
                var r = await Git(path, args, ct);
                return r.ExitCode == 0 ? r.Stdout.Trim('\r', '\n') : null;
            }
            if (await Out(System.IO.Path.GetTempPath(), "init", "-q", "--bare", scratch) is null)
            {
                sb.AppendLine("      check not run: could not make a scratch repository to read the clone's branches into.");
                return 1;
            }
            // The team repository's objects, read in place, so its branches can be counted against.
            if (await Out(t.Path, "rev-parse", "--path-format=absolute", "--git-common-dir") is { Length: > 0 } common)
                await File.WriteAllTextAsync(System.IO.Path.Combine(scratch, "objects", "info", "alternates"), System.IO.Path.Combine(common, "objects") + "\n", ct);
            if (await Out(scratch, "fetch", "-q", "--no-tags", "--no-write-fetch-head", "--no-recurse-submodules", TeamRepos.StrictUploadPack, "--", gitDir, "+refs/heads/*:refs/clone/*") is null)
            {
                sb.AppendLine("      check not run: could not read the clone's branches.");
                return 1;
            }
            var heads = await Out(scratch, "for-each-ref", "--format=%(refname)%09%(objectname)", "refs/clone/");
            if (heads is null) { sb.AppendLine("      check not run: could not list the clone's branches."); return 1; }
            var failed = 0;
            var all = heads.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim().Split('\t', 2)).Where(p => p.Length == 2).ToList();
            // The team repository's branches, read once; counts are cached (many branches often share a commit).
            var teamHeads = (await Out(t.Path, "for-each-ref", "--format=%(refname)%09%(objectname)", "refs/heads/") ?? "")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim().Split('\t', 2)).Where(p => p.Length == 2)
                .ToDictionary(p => p[0]["refs/heads/".Length..], p => p[1], StringComparer.Ordinal);
            var counts = new Dictionary<(string, string), string?>();
            foreach (var parts in all.Take(MaxCloneBranches))
            {
                var b = parts[0]["refs/clone/".Length..];
                var teamSha = teamHeads.GetValueOrDefault(b);
                var against = teamSha ?? baseSha;
                var name = $"the team's {(teamSha is not null ? b : t.BaseBranch)}";
                string? n = null;
                if (against is not null && !counts.TryGetValue((parts[1], against), out n))
                    counts[(parts[1], against)] = n = await Out(scratch, "rev-list", "--count", parts[1], "^" + against);
                sb.AppendLine($"      {b}: " + (n is null ? $"unpushed commits: check not run{(against is null ? $" (the team has no {t.BaseBranch})" : "")}"
                    : $"{n} commit(s) not in {name}") + (teamSha is not null ? "" : "; local only (not on the team repository)"));
                if (n is null) failed++;
            }
            if (all.Count > MaxCloneBranches)
                sb.AppendLine($"      only the first {MaxCloneBranches} of {all.Count} branches are listed (the list was cut).");
            return failed;
        }
        finally
        {
            DeleteScratch(scratch);
        }
    }

    /// <summary>At most this many branches of one member clone are listed in team_git (an agent can make any number).</summary>
    public const int MaxCloneBranches = 200;

    private static void DeleteScratch(string dir)
    {
        if (!Directory.Exists(dir)) return;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal); // git's objects are read-only
            Directory.Delete(dir, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* a temp folder; the OS cleans it */ }
    }

    /// <summary>The repositories of the team (or every team) from the app's own records: the main repository and each writable extra.</summary>
    public static async Task<IReadOnlyList<GitRepoTarget>> TargetsFromAsync(BusEngine bus, string? team, CancellationToken ct)
    {
        var repos = bus.Repos;
        if (repos is null) return [];
        var list = new List<GitRepoTarget>();
        foreach (var t in (await bus.TeamsAsync(ct)).Where(t => team is null || string.Equals(t.Name, team, StringComparison.OrdinalIgnoreCase)))
        {
            var members = (await bus.AgentCardsAsync(t.Id, ct)).Select(c => c.Member.Name).ToList();
            if (repos.RepoOf(t.Id) is { } main)
                list.Add(new GitRepoTarget(t.Name, "main", main.Path, main.BaseBranch, [.. members.Select(m => new GitCloneTarget(m, System.IO.Path.Combine(repos.MemberRoot(t.Name, m), "repo")))]));
            foreach (var e in repos.ExtrasOf(t.Id).Where(e => e.Writable))
                list.Add(new GitRepoTarget(t.Name, e.Name, e.Path, e.BaseBranch, [.. members.Select(m => new GitCloneTarget(m, repos.ExtraClonePath(t.Name, m, e.Name)))]));
        }
        return list;
    }

    // ------------------------------------------------------------------------------------------------ plumbing

    private async Task<TeamSummary> TeamNamedAsync(string name, CancellationToken ct)
    {
        var teams = await bus.TeamsAsync(ct);
        return teams.FirstOrDefault(t => string.Equals(t.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
               ?? throw new ArgumentException($"there is no team '{name}'. Teams: {string.Join(", ", teams.Select(t => t.Name))}");
    }

    private static string DecisionText(Message m)
    {
        try
        {
            if (JsonNode.Parse(m.Payload) is JsonObject o && o["question"] is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        }
        catch (JsonException) { }
        return m.Payload;
    }

    private static bool SecretKey(string key) =>
        key.Contains("token", StringComparison.OrdinalIgnoreCase) || key.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("key", StringComparison.OrdinalIgnoreCase) || key.Contains("password", StringComparison.OrdinalIgnoreCase) || key.Contains("url", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// An origin address as it may be shown: scheme://host[:port]/path only (no user, password, query or fragment), with any
    /// path segment that looks like a credential (it holds ':' or '@', as in /x-access-token:TOKEN@/ or /oauth2:TOKEN/)
    /// replaced by ***. An scp-style address (user@host:path) loses its user. A local path is shown as it is.
    /// </summary>
    public static string OriginForDisplay(string url)
    {
        url = url.Trim();
        static string Segments(string path) => string.Join('/', path.Split('/').Select(seg =>
            Uri.UnescapeDataString(seg) is var d && (d.Contains(':') || d.Contains('@')) ? "***" : seg));
        if (url.Contains("://", StringComparison.Ordinal) && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Host.Length > 0)
            return $"{u.Scheme}://{u.Host}{(u.IsDefaultPort || u.Port < 0 ? "" : ":" + u.Port)}{Segments(u.AbsolutePath)}";
        if (url.Contains("://", StringComparison.Ordinal))
            return PersonGitPush.Redact(url.Split('?', '#')[0]); // file:///path and the like
        var scp = System.Text.RegularExpressions.Regex.Match(url, @"^(?:[^@/\s]+@)?([A-Za-z0-9.-]{2,}):(?!//)(.*)$");
        if (scp.Success) return $"{scp.Groups[1].Value}:{Segments(scp.Groups[2].Value.Split('?', '#')[0])}";
        return url;
    }

    /// <summary>Whatever looks like a secret (a token, a bearer header, credentials inside a URL) is replaced before the text leaves.</summary>
    public static string Scrub(string text) => PersonGitPush.Redact(CliRunner.Redact(text));

    private static string Short(string sha) => sha[..Math.Min(12, sha.Length)];

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static string S(JsonObject? o, string name) =>
        o?[name] is JsonValue v ? (v.TryGetValue<string>(out var s) ? s : v.ToString()) : "";

    private static string Required(JsonObject args, string name) =>
        Optional(args, name) ?? throw new ArgumentException($"'{name}' is required");

    private static string? Optional(JsonObject args, string name) =>
        args[name] is JsonValue v && (v.TryGetValue<string>(out var s) ? s : v.ToString()) is { } text && !string.IsNullOrWhiteSpace(text) ? text : null;

    private static bool Flag(JsonObject args, string name) =>
        args[name] is JsonValue v && (v.TryGetValue<bool>(out var b) ? b : v.TryGetValue<string>(out var s) && bool.TryParse(s, out var c) && c);

    private static int? Int(JsonObject args, string name) =>
        args[name] is JsonValue v ? (v.TryGetValue<int>(out var i) ? i : v.TryGetValue<string>(out var s) && int.TryParse(s, out var j) ? j : null) : null;

    // ------------------------------------------------------------------------------------------------ definitions

    /// <summary>The tools list with these tools appended.</summary>
    public static JsonArray Append(JsonArray tools)
    {
        foreach (var d in Definitions()) tools.Add(d);
        return tools;
    }

    public static IEnumerable<JsonObject> Definitions()
    {
        yield return Tool("backlog_list", "The person's backlog: every item with its state, the team it is meant for and where it was dispatched. Reads only.",
            ("archived", "boolean", "true for the archive instead of the live items.", false));
        yield return Tool("backlog_item", "One backlog item in full: its spec and its dispatch history (which team, workflow, cards done, runs and tokens).",
            ("item", "string", "The item's label, e.g. B006.", true));
        yield return Tool("board_list", "Every team's board cards by lane (or one team's), with each card's status and its member's state.",
            ("team", "string", "Only this team (optional).", false));
        yield return Tool("board_card", "One board card in full: its description and trail.", ("card", "integer", "The card's number.", true));
        yield return Tool("system_status", "The whole system at a glance: run slots (in use, queued, limit), the heavy-work lease (holders, queue), each team's running and queued members, " +
                                           "open decisions across teams, approvals waiting, and the Cloud Link state.");
        yield return Tool("team_git", "Git state per team and writable repository: local and origin branches with last commit, ahead/behind base, merged or not, " +
                                      "open PRs (through gh, when it is signed in on this computer); each member clone's commits not yet in the team repository and its local-only branches; " +
                                      "whether origin's base matches the team's. Uncommitted changes in member clones are NOT checked (that would run git inside an agent's clone); " +
                                      "say so when asked. Complete, and says every check it could not run. Use it to answer 'is everything pushed?'.",
            ("team", "string", "Only this team (optional).", false));
        yield return Tool("integrations_status", "What is connected: Connections (name, kind, status, what uses each; never a secret or a masked value), APIs, websites, " +
                                                "MCP servers, the person's tools (gh, az, ...), which teams have each turned on, and the agents' model sign-ins. " +
                                                "Over the Cloud Link, Connections show only name, kind and status.");
        yield return Tool("usage", "Model usage per team and member for a period: runs, requests and tokens, from the usage log.",
            ("team", "string", "Only this team (optional).", false), ("days", "integer", "How many days back (default 7, at most 365).", false));
        yield return Tool("doctor", "The app's health check (the list in Settings > Health): database, disk, sandbox, agent CLIs, Cloud Link, Connections, teams, logs; each OK, warning or failing, with its detail and the fixes it offers. Reads only; doctor_fix runs a fix when the person asks.");
        yield return Tool("backup_now", "Makes a backup of the app's database now (not over the Cloud Link). The person restores in Settings > General; you cannot restore.");
    }

    private static JsonObject Tool(string name, string description, params (string Name, string Type, string Description, bool Required)[] parameters)
    {
        var properties = new JsonObject();
        foreach (var p in parameters) properties[p.Name] = new JsonObject { ["type"] = p.Type, ["description"] = p.Description };
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

