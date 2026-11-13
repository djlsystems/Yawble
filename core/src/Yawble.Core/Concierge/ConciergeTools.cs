using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Gateway;
using Yawble.Core.Ipc;

namespace Yawble.Core.Concierge;

/// <summary>
/// The Concierge's MCP tools, served at bus.yawble.internal/mcp by its turn's gateway (as a team run gets the bus tools).
/// It acts for the PERSON: it may read every team and tell any team's Manager, answer a decision, or create a team,
/// through the same operations as the person's own buttons (UiRpc), so the same rules apply. Its notes are its memory
/// across conversations.
/// </summary>
public sealed class ConciergeTools(
    BusEngine bus,
    ConciergeStore store,
    Func<string, JsonObject?, CancellationToken, Task<JsonNode?>> ui,
    Func<string?> teamInFocus,
    string? conversationId = null,
    TimeProvider? clock = null,
    ConciergeActions? actions = null,
    bool? remote = null,
    ConciergeTurn? turn = null,
    Provenance.Actor? actingFor = null) : IBusEndpoint
{
    /// <summary>Whether this turn's message came from anywhere but the app's window on this computer (given by the service;
    /// else the caller at construction, and an unknown caller counts as remote). The read tools then keep to what a Cloud
    /// Link viewer may see; <paramref name="actions"/> carry the same in their turn.</summary>
    private static bool Remote(bool? given) => given ?? Provenance.Actor.Current?.Via != Provenance.Actor.App;

    /// <summary>The person this turn acts for (the one who wrote, from the window or the cloud), captured when the turn
    /// began: the Concierge's tool calls arrive later through the gateway, where no caller is known. A caller that acts for
    /// someone else through these tools (the companion: a paired external client) gives its own actor instead.</summary>
    private readonly Provenance.Actor _actingFor = actingFor ?? new(Provenance.Actor.Concierge, Provenance.Actor.Current?.User ?? Provenance.Actor.LocalUser);

    /// <summary>
    /// The turn these tools serve (given by the service: who started it, from where). Made here only when a caller builds
    /// the tools directly (tests): then it counts as the person's own turn, local only when the caller is the app window.
    /// </summary>
    private readonly ConciergeTurn _turn = turn ?? new ConciergeTurn(conversationId ?? "", byPerson: true, remote: Remote(remote));

    /// <summary>
    /// The tools here that change something. A turn the person did not start (a watch's review of updates, which agents
    /// can steer through what they post) runs none of them: it may read, keep its notes and watches, and recommend.
    /// </summary>
    public static readonly IReadOnlyList<string> Acting =
        ["tell", "answer", "team_create", "team_pause", "team_resume", "workflow_nudge", "workflow_close", "backlog_add", "team_repo_add", "team_repo_remove", "backup_now"];

    /// <summary>
    /// The tools here only for the person at this computer (as <see cref="ConciergeActions.LocalOnly"/>): a new team or
    /// repository makes the host clone a folder or address for a sandboxed agent, and closing a workflow ends work.
    /// </summary>
    public static readonly IReadOnlyList<string> LocalOnly = ["team_create", "team_repo_add", "team_repo_remove", "workflow_close"];

    public static readonly IReadOnlyList<string> ToolNames =
        ["team_list", "team_current", "team_status", "team_activity", "tell", "answer", "team_create", "notes_read", "notes_write",
         "watch", "watch_list", "unwatch", "team_workflows", "workflow_nudge", "workflow_close", "team_pause", "team_resume",
         "documents_list", "documents_read", "backlog_add", "team_repos", "team_repo_add", "team_repo_remove",
         .. ConciergeReadTools.Names];

    private readonly ConciergeReadTools _read = new(bus, ui, store.ConnectionString,
        openPrs: actions is null ? null : actions.OpenPullRequestsAsync, remote: Remote(remote));

    public const int NotesLimit = 16_000;

    public async Task<BusReply> HandleAsync(byte[] body, CancellationToken ct)
    {
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return new BusReply(400, Error(null, -32700, "parse error"));
        }
        if (parsed is JsonArray batch)
        {
            var replies = new JsonArray();
            foreach (var item in batch)
                if (item is JsonObject o && await DispatchAsync(o, ct) is { } reply) replies.Add(reply);
            return replies.Count == 0 ? new BusReply(202, null) : new BusReply(200, replies.ToJsonString());
        }
        if (parsed is not JsonObject message) return new BusReply(400, Error(null, -32600, "invalid request"));
        var single = await DispatchAsync(message, ct);
        return single is null ? new BusReply(202, null) : new BusReply(200, single.ToJsonString());
    }

    private async Task<JsonNode?> DispatchAsync(JsonObject message, CancellationToken ct)
    {
        var id = message["id"]?.DeepClone();
        if (id is null) return null;
        var method = message["method"]?.GetValue<string>();
        JsonNode result;
        switch (method)
        {
            case "initialize":
                var requested = (message["params"] as JsonObject)?["protocolVersion"]?.GetValue<string>();
                result = new JsonObject
                {
                    ["protocolVersion"] = requested is not null && McpBusServer.SupportedProtocolVersions.Contains(requested) ? requested : McpBusServer.SupportedProtocolVersions[0],
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                    ["serverInfo"] = new JsonObject { ["name"] = "yawble-concierge", ["version"] = CoreInfo.Version },
                    ["instructions"] = "The Concierge's tools: every team on this computer, acting for the person.",
                };
                break;
            case "ping":
                result = new JsonObject();
                break;
            case "tools/list":
                result = new JsonObject { ["tools"] = Definitions(actions is not null) };
                break;
            case "tools/call":
                var p = message["params"] as JsonObject;
                var (text, isError) = await CallToolAsync(p?["name"]?.GetValue<string>() ?? "", p?["arguments"] as JsonObject ?? [], ct);
                result = Result(text, isError);
                break;
            default:
                return JsonNode.Parse(Error(id, -32601, $"method not found: {method}"));
        }
        return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
    }

    /// <summary>
    /// Every tool offered here (tools/list), with the action tools when <paramref name="withActions"/>: a fresh copy for each
    /// caller (the companion offers a subset of them by name).
    /// </summary>
    public static JsonArray Definitions(bool withActions) => withActions
        ? [.. ConciergeReadTools.Append(Tools()).Select(t => t!.DeepClone()), .. ConciergeActions.Tools().Select(t => t!.DeepClone())]
        : ConciergeReadTools.Append(Tools());

    /// <summary>
    /// One tool call, as tools/call runs it but without the JSON-RPC framing: the text the tool answers, and whether it is an
    /// error (a refusal, an unknown tool, a missing argument). Every guard of the MCP path applies; it acts as this turn's actor.
    /// </summary>
    public async Task<(string Text, bool IsError)> CallToolAsync(string name, JsonObject args, CancellationToken ct)
    {
        using var acting = Provenance.Actor.Use(_actingFor);
        try
        {
            if (!_turn.ByPerson && Acting.Contains(name)) throw new ArgumentException(ConciergeActions.ReviewRefusal(name));
            if (_turn.Remote && LocalOnly.Contains(name)) throw new ArgumentException(ConciergeActions.RemoteRefusal(name));
            var text = name switch
            {
                "team_list" => await TeamListAsync(ct),
                "team_current" => teamInFocus() is { } t ? $"The person has team '{t}' in focus in this conversation." : "No team is in focus; the conversation is about all teams.",
                "team_status" => await TeamStatusAsync(await TeamAsync(Required(args, "team"), ct), ct),
                "team_activity" => await ActivityAsync(await TeamAsync(Required(args, "team"), ct), Int(args, "limit") ?? 25, ct),
                "tell" => await TellAsync(await TeamAsync(Required(args, "team"), ct), Required(args, "instruction"), Bool(args, "follow_up"), ct, Optional(args, "images")),
                "answer" => await AnswerAsync(await TeamAsync(Required(args, "team"), ct), Int(args, "question") ?? throw new ArgumentException("'question' is required"), Required(args, "answer"), ct, Optional(args, "images")),
                "team_create" => await CreateAsync(args, ct),
                "notes_read" => store.Notes() is { Length: > 0 } notes ? notes : "(no notes yet)",
                "notes_write" => WriteNotes(Required(args, "text")),
                "watch" => await WatchAsync(await TeamAsync(Required(args, "team"), ct), Required(args, "what"), Long(args, "workflow"),
                    Bool(args, "follow_up"), Int(args, "hours"), ct),
                "watch_list" => WatchList(),
                "unwatch" => Unwatch(Long(args, "id") ?? throw new ArgumentException("'id' is required")),
                "team_workflows" => await WorkflowsAsync(await TeamAsync(Required(args, "team"), ct), ct),
                "workflow_nudge" => await UiText("bus.nudgeWorkflow", await TeamAsync(Required(args, "team"), ct),
                    new JsonObject { ["workflow"] = Long(args, "workflow") ?? throw new ArgumentException("'workflow' is required"), ["note"] = Optional(args, "note") },
                    "nudged: its Manager is told to pick it up", ct),
                "workflow_close" => await UiText("bus.closeWorkflow", await TeamAsync(Required(args, "team"), ct),
                    new JsonObject { ["workflow"] = Long(args, "workflow") ?? throw new ArgumentException("'workflow' is required"), ["reason"] = Optional(args, "reason") },
                    "closed", ct),
                "team_pause" => await UiText("teams.pause", await TeamAsync(Required(args, "team"), ct), [], "paused: its work waits until it is resumed", ct),
                "team_resume" => await UiText("teams.resume", await TeamAsync(Required(args, "team"), ct), [], "resumed", ct),
                "documents_list" => await DocumentsListAsync(await TeamAsync(Required(args, "team"), ct), Optional(args, "path"), ct),
                "documents_read" => await DocumentsReadAsync(await TeamAsync(Required(args, "team"), ct), Required(args, "path"), ct),
                "backlog_add" => await BacklogAddAsync(args, ct),
                "team_repos" => await ReposAsync(await TeamAsync(Required(args, "team"), ct), ct),
                "team_repo_add" => await RepoAddAsync(await TeamAsync(Required(args, "team"), ct), args, ct),
                "team_repo_remove" => await UiText("teams.removeRepo", await TeamAsync(Required(args, "team"), ct),
                    new JsonObject { ["name"] = Required(args, "name") }, "removed: members no longer get it (its folder on this computer is kept)", ct),
                _ when ConciergeReadTools.Handles(name) => await _read.CallAsync(name, args, ct),
                _ => (actions is null ? null : await actions.TryCallAsync(name, args, ct)) ?? throw new ArgumentException($"unknown tool: {name}"),
            };
            return (text, false);
        }
        catch (Exception e) when (e is ArgumentException or RpcException or BusRefusal)
        {
            return (e is RpcException r ? $"refused ({r.Code}): {r.Message}" : e is BusRefusal b ? $"refused ({b.Code}): {b.Message}" : e.Message, true);
        }
    }

    private async Task<string> UiText(string method, TeamSummary team, JsonObject p, string done, CancellationToken ct)
    {
        p["teamId"] = team.Id;
        await ui(method, p, ct);
        return $"ok: {team.Name}: {done}.";
    }

    private async Task<string> WorkflowsAsync(TeamSummary team, CancellationToken ct)
    {
        var list = await bus.WorkflowsAsync(team.Name, 20, ct);
        if (list.Count == 0) return $"{team.Name} has no workflows yet.";
        return string.Join(Environment.NewLine, list.Select(w =>
            $"#{w.Id} [{(w.Undeclared ? "open, nobody working it" : w.State)}] {w.Title} (owner {w.Owner}; last {w.LastAt:yyyy-MM-dd HH:mm} UTC)" +
            (w.Busy.Count > 0 ? $" - {string.Join("; ", w.Busy)}" : "") + (w.Ending is { } e ? $" - {(w.State == "closed" ? "closed" : "delivered")}: {e}" : "")));
    }

    private async Task<string> DocumentsListAsync(TeamSummary team, string? path, CancellationToken ct)
    {
        var r = await ui("docs.list", new JsonObject { ["teamId"] = team.Id, ["path"] = path }, ct) as JsonObject;
        var entries = (r?["entries"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        if (entries.Count == 0) return $"{team.Name}'s documents{(string.IsNullOrEmpty(path) ? "" : $" /{path}")}: nothing there.";
        return string.Join(Environment.NewLine, entries.Select(e =>
            $"{(e["folder"]?.GetValue<bool>() == true ? "[folder] " : "")}{e["path"]?.GetValue<string>()}" +
            (e["size"] is JsonValue sz && sz.TryGetValue<long>(out var n) ? $" ({n} bytes)" : "")));
    }

    private async Task<string> DocumentsReadAsync(TeamSummary team, string path, CancellationToken ct)
    {
        var r = await ui("docs.read", new JsonObject { ["teamId"] = team.Id, ["path"] = path }, ct) as JsonObject;
        var kind = r?["kind"]?.GetValue<string>();
        if (kind is "markdown" or "text") return r!["text"]?.GetValue<string>() ?? "";
        return $"{path} is a {kind} file; it is not shown as text. The person can open it in the team's Documents.";
    }

    private async Task<string> BacklogAddAsync(JsonObject args, CancellationToken ct)
    {
        var team = Optional(args, "team") is { } t ? (await TeamAsync(t, ct)).Name : null;
        var r = await ui("backlog.add", new JsonObject { ["title"] = Required(args, "title"), ["body"] = Optional(args, "body") ?? "", ["team"] = team }, ct) as JsonObject;
        return $"ok: added {r?["label"]?.GetValue<string>() ?? "the item"} to the backlog (pending: the person marks it ready, then dispatches it).";
    }

    private async Task<TeamSummary> TeamAsync(string name, CancellationToken ct)
    {
        var teams = await bus.TeamsAsync(ct);
        return teams.FirstOrDefault(t => string.Equals(t.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
               ?? throw new ArgumentException($"there is no team '{name}'. Teams: {string.Join(", ", teams.Select(t => t.Name))}");
    }

    public async Task<string> TeamListAsync(CancellationToken ct)
    {
        var teams = await bus.TeamsAsync(ct);
        if (teams.Count == 0) return "There are no teams yet. Offer to create one (team_create).";
        var sb = new StringBuilder();
        foreach (var t in teams)
        {
            var decisions = (await bus.OpenDecisionsAsync(t.Name, ct)).Count;
            sb.AppendLine($"- {t.Name}: {t.Members} agents, {t.Running} running, {t.Queued} queued, {decisions} open decision(s); repository {t.RepoPath ?? "(none)"}");
        }
        return sb.ToString().TrimEnd();
    }

    private async Task<string> TeamStatusAsync(TeamSummary team, CancellationToken ct)
    {
        var sb = new StringBuilder($"Team {team.Name} (repository {team.RepoPath ?? "none"})\n");
        foreach (var c in await bus.AgentCardsAsync(team.Id, ct))
        {
            sb.Append($"- {c.Member.Name} ({c.Member.Role}, {c.Member.Cli}): {c.State}");
            if (c.QueuePosition is { } q) sb.Append($", queued {q} of {c.QueueLength}");
            if (c.QueuedInstructions > 0) sb.Append($", {c.QueuedInstructions} instruction(s) waiting");
            if (c.StatusLine is { Length: > 0 } s) sb.Append($"; says: {s}");
            else if (c.LastRunState is { } last) sb.Append($"; last run {last}{(c.LastRunReason is { } r ? $" ({r})" : "")}");
            sb.AppendLine();
        }
        if (bus.Repos is { } repos && await repos.BehindLineAsync(team.Id, team.Name, ct) is { } behind) sb.AppendLine("Branch: " + behind + ".");
        var decisions = await bus.OpenDecisionsAsync(team.Name, ct);
        if (decisions.Count > 0)
        {
            sb.AppendLine("Open decisions (answer with the answer tool):");
            foreach (var d in decisions) sb.AppendLine($"- question {d.Seq} from {From(d)}: {Field(d, "question")}");
        }
        return sb.ToString().TrimEnd();
    }

    private async Task<string> ActivityAsync(TeamSummary team, int limit, CancellationToken ct)
    {
        var messages = await bus.RecentTeamMessagesAsync(team.Name, Math.Clamp(limit, 1, 100), ct);
        if (messages.Count == 0) return $"Team {team.Name} has no activity yet.";
        var sb = new StringBuilder();
        foreach (var m in messages)
        {
            var what = m.Type.StartsWith(MessageTypes.InstructionPrefix, StringComparison.Ordinal)
                ? $"told {m.Type[MessageTypes.InstructionPrefix.Length..]}: {Field(m, "text", "instruction")}"
                : $"{m.Type}: {Field(m, "delivered", "status", "reason", "question", "text")}";
            sb.AppendLine($"[{m.OccurredAt:yyyy-MM-dd HH:mm}] #{m.Seq} (workflow {m.CorrelationId}) {From(m)} {Clip(what, 400)}");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// The images the person attached in this chat that the Concierge forwards (comma- or line-separated paths), as the
    /// request's "images". Only files in this conversation's own attachments folder: the Concierge cannot hand a team
    /// anything else from the computer.
    /// </summary>
    private JsonArray? ForwardedImages(string? paths)
    {
        if (string.IsNullOrWhiteSpace(paths)) return null;
        if (conversationId is null || bus.Repos is not { } repos) throw new ArgumentException("images can only be forwarded from a chat");
        var folder = Path.GetFullPath(Path.Combine(repos.MemberRoot(ConciergeService.Team, ConciergeService.MemberOf(conversationId)), "home", Ui.Attachments.Folder))
                     + Path.DirectorySeparatorChar;
        var list = new JsonArray();
        foreach (var raw in paths.Split([',', '\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var full = Path.GetFullPath(raw.Trim('"', '\'', '`'));
            if (!full.StartsWith(folder, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
                throw new ArgumentException($"{raw} is not an image the person attached in this chat (they are in {folder})");
            list.Add(new JsonObject { ["name"] = Path.GetFileName(full), ["base64"] = Convert.ToBase64String(File.ReadAllBytes(full)) });
        }
        return list.Count == 0 ? null : list;
    }

    private async Task<string> TellAsync(TeamSummary team, string instruction, bool followUp, CancellationToken ct, string? images = null)
    {
        var request = new JsonObject { ["teamId"] = team.Id, ["text"] = instruction };
        if (ForwardedImages(images) is { } forwarded) request["images"] = forwarded;
        var r = await ui("bus.tellManager", request, ct);
        var said = $"ok: told the Manager of {team.Name} (message {r?["seq"]}, new workflow {r?["workflow"]}). It runs when a slot is free.";
        // Every dispatch is followed up: the app watches the new workflow and posts here when it finishes, fails, is
        // blocked or needs a decision, so the person hears back without asking.
        if (conversationId is null || r?["workflow"] is not JsonValue wv || !wv.TryGetValue<long>(out var workflow))
            return said + " Check on it with team_status.";
        var since = r["seq"] is JsonValue sv && sv.TryGetValue<long>(out var seq) ? seq : workflow;
        var w = store.AddWatch(conversationId, team.Name, workflow, Clip(FirstLine(instruction), 120), followUp, since, Now() + ConciergeWatcher.DefaultLife);
        return said + $" The app now watches this workflow (watch {w.Id}) and posts updates in this chat when it finishes, fails, " +
               "is blocked or needs a decision" + (followUp ? "; you will be woken to review the outcome." : ". Tell the person so.");
    }

    private async Task<string> WatchAsync(TeamSummary team, string what, long? workflow, bool followUp, int? hours, CancellationToken ct)
    {
        if (conversationId is null) throw new ArgumentException("watches belong to a conversation");
        var life = TimeSpan.FromHours(Math.Clamp(hours ?? (int)ConciergeWatcher.DefaultLife.TotalHours, 1, (int)ConciergeWatcher.MaxLife.TotalHours));
        var w = store.AddWatch(conversationId, team.Name, workflow, Clip(FirstLine(what), 120), followUp, await bus.MaxSeqAsync(ct), Now() + life);
        var target = workflow is { } wf ? $"workflow {wf} of {team.Name}" : $"team {team.Name}";
        return $"ok: watch {w.Id} on {target}: the app posts updates in this chat when {(workflow is null ? "any of the team's work" : "it")} " +
               $"finishes, fails, is blocked or needs a decision, for {life.TotalHours:0} hours.";
    }

    private string WatchList()
    {
        if (conversationId is null) return "Nothing is being watched.";
        var watches = store.ActiveWatches(conversationId);
        if (watches.Count == 0) return "Nothing is being watched in this conversation.";
        return string.Join("\n", watches.Select(w =>
            $"- watch {w.Id}: {(w.Workflow is { } wf ? $"workflow {wf} of {w.Team}" : $"team {w.Team}")}: {w.What}" +
            $"{(w.FollowUp ? " (you review each update)" : "")}; until {w.ExpiresAt}"));
    }

    private string Unwatch(long id)
    {
        var w = store.Watch(id);
        if (w is null || w.ConversationId != conversationId) throw new ArgumentException($"there is no watch {id} in this conversation");
        return store.EndWatch(id, "cancelled") ? $"ok: stopped watch {id}." : $"watch {id} had already ended ({w.State}).";
    }

    private DateTimeOffset Now() => (clock ?? TimeProvider.System).GetUtcNow();

    private static string FirstLine(string s) => s.Trim().Split('\n', 2)[0].Trim();

    private async Task<string> AnswerAsync(TeamSummary team, int question, string answer, CancellationToken ct, string? images = null)
    {
        var request = new JsonObject { ["teamId"] = team.Id, ["seq"] = (long)question, ["text"] = answer };
        if (ForwardedImages(images) is { } forwarded) request["images"] = forwarded;
        var r = await ui("bus.answer", request, ct);
        return $"ok: answered question {question} in {team.Name} (message {r?["seq"]}).";
    }

    private async Task<string> CreateAsync(JsonObject args, CancellationToken ct)
    {
        var p = new JsonObject
        {
            ["name"] = Required(args, "name"),
            ["repoPath"] = Optional(args, "repo_path"),
            ["repoUrl"] = Optional(args, "repo_url"),
            ["managerCli"] = Optional(args, "manager_cli") ?? "claude",
            ["members"] = new JsonArray(),
        };
        var r = await ui("teams.create", p, ct);
        return $"ok: created team {p["name"]} (id {r?["teamId"]}) with a Manager. Tell its Manager what to do with tell; the Manager hires members.";
    }

    private async Task<string> ReposAsync(TeamSummary team, CancellationToken ct)
    {
        var r = await ui("teams.repos", new JsonObject { ["teamId"] = team.Id }, ct);
        var lines = new List<string>();
        lines.Add(r?["main"] is JsonObject m ? $"main: {m["path"]} (base branch {m["baseBranch"]}; every member's working directory)" : "main: not made yet (made at the first run)");
        foreach (var e in r?["extras"] as JsonArray ?? [])
            lines.Add($"{e?["name"]}: {e?["path"]} ({(e?["writable"]?.GetValue<bool>() == true ? "writable: each member has a clone, branches pushed like the main one's" : "read-only reference")}; base branch {e?["baseBranch"]})");
        return string.Join('\n', lines);
    }

    private async Task<string> RepoAddAsync(TeamSummary team, JsonObject args, CancellationToken ct)
    {
        var p = new JsonObject
        {
            ["teamId"] = team.Id,
            ["name"] = Optional(args, "name"),
            ["path"] = Optional(args, "repo_path"),
            ["url"] = Optional(args, "repo_url"),
            ["writable"] = !Bool(args, "read_only"),
        };
        var r = await ui("teams.addRepo", p, ct);
        return $"ok: team {team.Name} now has repository {r?["name"]} at {r?["path"]} ({(r?["writable"]?.GetValue<bool>() == true ? "writable" : "read-only reference")}). " +
               "Its members get it from their next run; tell the Manager it is there.";
    }

    private string WriteNotes(string text)
    {
        if (text.Length > NotesLimit) throw new ArgumentException($"the notes are limited to {NotesLimit} characters; keep them short");
        store.SaveNotes(text);
        return "ok: notes saved (they replace the previous notes).";
    }

    private static string From(Message m) => m.Source == MessageTypes.PersonSource ? "the person" : m.Source;

    private static string Field(Message m, params string[] names)
    {
        try
        {
            if (JsonNode.Parse(m.Payload) is JsonObject o)
                foreach (var n in names)
                    if (o[n] is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        }
        catch (JsonException) { }
        return m.Payload;
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static string Required(JsonObject args, string name) =>
        Optional(args, name) ?? throw new ArgumentException($"'{name}' is required");

    private static string? Optional(JsonObject args, string name) =>
        args[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

    private static bool Bool(JsonObject args, string name) =>
        args[name] is JsonValue v && (v.TryGetValue<bool>(out var b) ? b : v.TryGetValue<string>(out var s) && bool.TryParse(s, out var c) && c);

    private static long? Long(JsonObject args, string name) =>
        args[name] is JsonValue v ? (v.TryGetValue<long>(out var i) ? i : v.TryGetValue<string>(out var s) && long.TryParse(s, out var j) ? j : null) : null;

    private static int? Int(JsonObject args, string name) =>
        args[name] is JsonValue v ? (v.TryGetValue<int>(out var i) ? i : v.TryGetValue<string>(out var s) && int.TryParse(s, out var j) ? j : null) : null;

    private static JsonObject Result(string text, bool isError) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = isError,
    };

    private static string Error(JsonNode? id, int code, string message) => new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    }.ToJsonString();

    private static JsonArray Tools() =>
    [
        Tool("team_list", "Every team on this computer: agents, running, queued, open decisions, repository."),
        Tool("team_current", "The team the person has in focus in this conversation, if any (a hint, not a limit)."),
        Tool("team_status", "One team: each agent's state and what it says, its queue, and the open decisions waiting for the person.",
            ("team", "string", "The team's name.", true)),
        Tool("team_activity", "One team's latest messages: who was told what, progress, hand-backs, completions, failures.",
            ("team", "string", "The team's name.", true), ("limit", "integer", "How many messages (default 25, at most 100).", false)),
        Tool("tell", "Give a team's Manager new work, for the person: starts a new workflow. Write the full instruction; the Manager plans it and tells its members. " +
                     "The app then watches the workflow and posts updates in this chat (finished, failed, blocked, decision needed).",
            ("team", "string", "The team's name.", true), ("instruction", "string", "The complete instruction, as the person would give it.", true),
            ("follow_up", "boolean", "true to be woken to review the outcome when an update arrives (an extra agent turn); default false: the person just sees the update.", false),
            ("images", "string", ImagesParameter, false)),
        Tool("answer", "Answer an open decision of a team (question number from team_status) for the person, once they have told you the answer.",
            ("team", "string", "The team's name.", true), ("question", "integer", "The question's number.", true),
            ("answer", "string", "The answer, as the person gave it.", true),
            ("images", "string", ImagesParameter, false)),
        Tool("team_create", "Create a team with a Manager, when the person asks for one. If it should work on more than one repository, add the others " +
                            "with team_repo_add (read_only for reference-only ones) before telling its Manager the work. Then give its Manager work with tell.",
            ("name", "string", "A short name for the team.", true),
            ("repo_path", "string", "The repository folder on this computer the team works on (optional).", false),
            ("repo_url", "string", "Or a repository's address to clone (https://github.com/owner/repo.git); the team's branches are pushed back there.", false),
            ("manager_cli", "string", "The Manager's agent CLI: claude, grok, codex, copilot or antigravity (default claude).", false)),
        Tool("team_repos", "A team's repositories: its main one and any others, writable or read-only references.",
            ("team", "string", "The team's name.", true)),
        Tool("team_repo_add", "Give a team another repository, when the person asks (a team works on one main repository and may have more). " +
                              "Writable: every member gets its own clone and its branches are pushed back like the main one's. Read-only: a reference members can read, kept current. " +
                              "Give repo_url (cloned with the person's own git) or repo_path (a folder on this computer).",
            ("team", "string", "The team's name.", true),
            ("repo_url", "string", "The repository's address, e.g. https://github.com/owner/repo.git.", false),
            ("repo_path", "string", "Or the repository's folder on this computer.", false),
            ("name", "string", "A short name members see it by (default: the repository's own name).", false),
            ("read_only", "boolean", "true for a read-only reference; default false (writable).", false)),
        Tool("team_repo_remove", "Take a repository (not the main one) away from a team, when the person asks. Its folder on this computer is kept.",
            ("team", "string", "The team's name.", true), ("name", "string", "The repository's name (team_repos).", true)),
        Tool("watch", "Keep tabs on a team (or one of its workflows) for the person: the app posts an update in this chat when its work finishes, fails, " +
                      "is blocked or needs a decision. tell already watches the workflow it starts.",
            ("team", "string", "The team's name.", true), ("what", "string", "What the person wants to hear about, in a few words.", true),
            ("workflow", "integer", "Only this workflow (from team_activity); omit for the whole team.", false),
            ("follow_up", "boolean", "true to be woken to review each update (an extra agent turn); default false.", false),
            ("hours", "integer", "How long to watch (default 48, at most 168).", false)),
        Tool("watch_list", "What this conversation is watching."),
        Tool("unwatch", "Stop a watch, when the person no longer wants updates.", ("id", "integer", "The watch's number (from watch_list).", true)),
        Tool("team_workflows", "One team's workflows, newest first: open (and who works them, or that nobody does), completed, closed.",
            ("team", "string", "The team's name.", true)),
        Tool("workflow_nudge", "Nudge an open workflow nobody is working: its Manager is told to pick it up. When the person asks, or after asking them.",
            ("team", "string", "The team's name.", true), ("workflow", "integer", "The workflow's number (team_workflows).", true),
            ("note", "string", "A note for the Manager (optional).", false)),
        Tool("workflow_close", "Close a workflow without declaring it complete. Only when the person asks for it.",
            ("team", "string", "The team's name.", true), ("workflow", "integer", "The workflow's number.", true),
            ("reason", "string", "Why, in the person's words (optional).", false)),
        Tool("team_pause", "Pause a team: it starts no new runs; what it is told waits. Only when the person asks.",
            ("team", "string", "The team's name.", true)),
        Tool("team_resume", "Resume a paused team: its waiting work runs, oldest first. Only when the person asks.",
            ("team", "string", "The team's name.", true)),
        Tool("documents_list", "A team's documents folder: the reports, specs and plans its agents wrote for the person.",
            ("team", "string", "The team's name.", true), ("path", "string", "A folder inside it (optional).", false)),
        Tool("documents_read", "Read one of a team's documents (Markdown and text files).",
            ("team", "string", "The team's name.", true), ("path", "string", "The file's path, from documents_list.", true)),
        Tool("backlog_add", "Put work in the person's backlog for later (it starts pending; the person marks it ready and dispatches it).",
            ("title", "string", "A short title.", true), ("body", "string", "What and why: the spec a Manager will cut into cards.", false),
            ("team", "string", "The team it is meant for (optional).", false)),
        Tool("notes_read", "Your notes: what you keep across conversations with the person (preferences, what was dispatched, decisions)."),
        Tool("notes_write", "Replace your notes. Keep them short and current; write after dispatching work, a decision, or learning a preference.",
            ("text", "string", "The whole new notes text (markdown).", true)),
    ];

    private const string ImagesParameter =
        "Images the person attached in this chat that the team needs (a screenshot of a bug, a mock-up): their paths exactly as " +
        "listed in the person's message, separated by commas. The app copies them into the team's documents and names them in the " +
        "message. Only forward images that matter to the work.";

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
