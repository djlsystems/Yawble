using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Ipc;

namespace Yawble.Core.Board;

/// <summary>
/// THE BOARD (a Kanban and the backlog): every team's cards in four lanes, and the person's backlog.
/// <list type="bullet">
/// <item>Cards follow the bus (<see cref="BoardProjector"/>, read every second from where it left off).</item>
/// <item>The person moves, edits or comments on a card: each is written on the card's trail and told to the team's
///   Manager, which is woken by it; the card is marked "awaiting manager" until the Manager next says anything.</item>
/// <item>The backlog: items are pending until the person marks them ready; a ready item is dispatched to a team's
///   Manager as a spec, which it cuts into cards (<c>board plan</c>) and gives to members (<c>tell</c> with <c>card</c>).
///   When that workflow is declared complete the item is "declared"; only the person marks it implemented.</item>
/// </list>
/// </summary>
public sealed class BoardService(BoardStore store, BusEngine bus, Action<string>? log = null)
{
    /// <summary>A deleted team's cards go with it.</summary>
    public void ForgetTeam(string team) => store.DeleteTeam(team);

    public const string ChangedEvent = "board.changed";

    /// <summary>
    /// Raised when what the board shows changes outside the bus: the person's clear done or restore, or a cleared card
    /// un-clearing itself by leaving Done (whoever moved it). Open boards refresh.
    /// </summary>
    public Action? Changed
    {
        get => _changed;
        set { _changed = value; store.Uncleared = value; }
    }
    private Action? _changed;
    public static readonly TimeSpan ProjectEvery = TimeSpan.FromSeconds(1);
    private readonly BoardProjector _projector = new(store);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public static bool Handles(string method) => method.StartsWith("board.", StringComparison.Ordinal) || method.StartsWith("backlog.", StringComparison.Ordinal);

    /// <summary>Reads the bus into the cards until stopped.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(ProjectEvery);
        try
        {
            do
            {
                try { await ProjectAsync(ct); }
                catch (Exception e) when (e is not OperationCanceledException) { log?.Invoke($"board: {e.Message}"); }
            }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Applies every message not read yet. Returns how many were read.</summary>
    public async Task<int> ProjectAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var read = 0;
            while (true)
            {
                var after = Math.Max(0, store.Cursor());
                var page = await bus.MessagesFromAsync(after, 500, ct);
                if (page.Count == 0) return read;
                foreach (var m in page) _projector.Apply(m);
                store.SetCursor(page[^1].Seq);
                read += page.Count;
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<JsonNode?> DispatchAsync(string method, JsonObject? p, CancellationToken ct)
    {
        await ProjectAsync(ct); // what the person sees includes everything said so far
        return method switch
        {
            "board.list" => await ListAsync(OptStr(p, "team"), Bool(p, "cleared"), Bool(p, "includeCleared"), ct),
            "board.clearDone" => ClearDone(Ids(p, "ids")),
            "board.restore" => Restore(Long(p, "id")),
            "board.card" => await CardAsync(Long(p, "id"), ct),
            "board.move" => await MoveAsync(Long(p, "id"), Str(p, "lane"), OptStr(p, "note"), ct),
            "board.edit" => await EditAsync(Long(p, "id"), OptStr(p, "title"), OptStr(p, "status"), OptStr(p, "note"), ct),
            "board.comment" => await CommentAsync(Long(p, "id"), Str(p, "text"), ct),

            "backlog.list" => Items(store.Items(p?["archived"] is JsonValue a && a.TryGetValue<bool>(out var ar) && ar)),
            "backlog.add" => ItemJson(store.AddItem(Required(Str(p, "title"), "a title"), OptStr(p, "body") ?? "", OptStr(p, "team"))),
            "backlog.edit" => EditItem(Long(p, "id"), OptStr(p, "title"), OptStr(p, "body"), OptStr(p, "state"), OptStr(p, "team")),
            "backlog.move" => MoveItem(Long(p, "id"), Str(p, "direction")),
            "backlog.archive" => ItemJson(Save(ItemOr(Long(p, "id")) with { ArchivedAt = store.Now() })),
            "backlog.restore" => ItemJson(Save(ItemOr(Long(p, "id")) with { ArchivedAt = null })),
            "backlog.delete" => new JsonObject { ["deleted"] = store.DeleteItem(Long(p, "id")) },
            "backlog.dispatch" => await DispatchItemAsync(Long(p, "id"), Long(p, "teamId"), p?["again"] is JsonValue g && g.TryGetValue<bool>(out var again) && again, ct),
            "backlog.item" => await ItemDetailAsync(Long(p, "id"), ct),
            "backlog.position" => PlaceItem(Long(p, "id"), OptLong(p, "after"), OptLong(p, "before")),
            "backlog.dispatchNew" => await DispatchNewAsync(Long(p, "id"), Required(OptStr(p, "name"), "the new team's name"), OptStr(p, "managerCli") ?? "claude",
                OptStr(p, "repoPath"), OptStr(p, "repoUrl"), ct),
            _ => throw new RpcException("method_not_found", $"unknown method '{method}'"),
        };
    }

    // ---------------------------------------------------------------- the board

    /// <summary>
    /// The board's cards. Cleared cards (the person's "Clear done", 0037) are left out unless <paramref name="cleared"/>
    /// (only those) or <paramref name="includeCleared"/> (everything, each with its clearedAt: the Concierge reads the
    /// board as it is, and the board view splits them itself so one call serves both). <c>clearedCount</c> is how many
    /// the team filter has cleared.
    /// </summary>
    private async Task<JsonNode> ListAsync(string? team, bool cleared, bool includeCleared, CancellationToken ct)
    {
        var teams = await bus.TeamsAsync(ct);
        var running = new Dictionary<string, (string State, string? Attention)>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in teams.Where(t => team is null || string.Equals(t.Name, team, StringComparison.OrdinalIgnoreCase)))
        {
            var cards = await bus.AgentCardsAsync(t.Id, ct);
            var asking = (await bus.OpenDecisionsAsync(t.Name, ct)).Select(m => m.Source[(m.Source.IndexOf('/') + 1)..]).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var reports = await bus.LatestReportsAsync(t.Name, ct);
            foreach (var c in cards) running[$"{t.Name}/{c.Member.Name}"] = (c.State, Ui.UiRpc.Attention(c, asking, reports));
        }
        var list = new JsonArray();
        var open = new HashSet<long>();
        foreach (var card in store.Cards(team).Where(c => includeCleared || (c.ClearedAt is not null) == cleared))
        {
            // A member running right now is working on its newest open card, though no message has said so yet.
            var shown = card;
            if (card.Member is { } m && running.TryGetValue($"{card.Team}/{m}", out var state) && state.State == "running"
                && card.Status == "queued" && open.Add(card.Id) && store.OpenCardOf(card.Team, m)?.Id == card.Id)
                shown = card with { Status = "running", Lane = card.Placed ? card.Lane : Lanes.InProgress };
            var node = CardJson(shown, teams);
            if (card.Member is { } who && running.TryGetValue($"{card.Team}/{who}", out var s))
                node["memberState"] = new JsonObject { ["state"] = s.State, ["attention"] = s.Attention };
            list.Add(node);
        }
        return new JsonObject
        {
            ["lanes"] = new JsonArray(Lanes.All.Select(l => (JsonNode)new JsonObject { ["id"] = l.Id, ["label"] = l.Label }).ToArray()),
            ["cards"] = list,
            ["teams"] = new JsonArray(teams.Select(t => (JsonNode)new JsonObject { ["id"] = t.Id, ["name"] = t.Name }).ToArray()),
            ["clearedCount"] = store.ClearedCount(team),
        };
    }

    /// <summary>Hides the Done cards the person saw (their ids) from the board; nothing is deleted.</summary>
    private JsonNode ClearDone(IReadOnlyList<long> ids)
    {
        var n = store.ClearDone(ids);
        if (n > 0) Changed?.Invoke();
        return new JsonObject { ["cleared"] = n };
    }

    /// <summary>Shows a cleared card on the board again.</summary>
    private JsonNode Restore(long id)
    {
        CardOr(id);
        var restored = store.Restore(id);
        if (restored) Changed?.Invoke();
        return new JsonObject { ["restored"] = restored };
    }

    private async Task<JsonNode> CardAsync(long id, CancellationToken ct)
    {
        var card = CardOr(id);
        var node = CardJson(card, await bus.TeamsAsync(ct));
        node["trail"] = new JsonArray(store.Trail(id).Select(t => (JsonNode)new JsonObject
        {
            ["id"] = t.Id, ["at"] = t.At, ["source"] = t.Source, ["kind"] = t.Kind, ["text"] = t.Text,
        }).ToArray());
        if (card.Item is { } label && BoardStore.ItemId(label) is { } itemId && store.Item(itemId) is { } item)
            node["itemTitle"] = item.Title;
        return node;
    }

    private async Task<JsonNode> MoveAsync(long id, string lane, string? note, CancellationToken ct)
    {
        if (!Lanes.IsLane(lane)) throw new RpcException("bad_params", $"no lane '{lane}'");
        var card = CardOr(id);
        if (card.Lane == lane && string.IsNullOrWhiteSpace(note)) throw new RpcException("bad_params", "the card is in that lane already");
        var label = Lanes.All.First(l => l.Id == lane).Label;
        store.Save(card with { Lane = lane, Placed = true, AwaitingManager = true });
        store.AddTrail(id, "you", "moved", $"Moved to {label}{Note(note)}");
        await WakeManagerAsync(card, $"The person moved card {id} \"{card.Title}\" to {label}{Note(note)}", ct);
        return await CardAsync(id, ct);
    }

    private async Task<JsonNode> EditAsync(long id, string? title, string? status, string? note, CancellationToken ct)
    {
        var card = CardOr(id);
        var changes = new List<string>();
        var next = card;
        if (title is { } t && t.Trim().Length > 0 && t.Trim() != card.Title) { next = next with { Title = t.Trim() }; changes.Add($"title \"{t.Trim()}\""); }
        if (status is { } s && s != card.Status)
        {
            if (!Lanes.Statuses.Contains(s)) throw new RpcException("bad_params", $"no status '{s}'");
            next = next with { Status = s, Lane = Lanes.Of(s), Placed = false };
            changes.Add($"status {s}");
        }
        // Nothing to change would still wake the Manager (a paid run): refused.
        if (changes.Count == 0) throw new RpcException("bad_params", "nothing to change");
        store.Save(next with { AwaitingManager = true });
        store.AddTrail(id, "you", "edited", $"Edited: {string.Join(", ", changes)}{Note(note)}");
        await WakeManagerAsync(card, $"The person edited card {id} \"{card.Title}\": {string.Join(", ", changes)}{Note(note)}", ct);
        return await CardAsync(id, ct);
    }

    private async Task<JsonNode> CommentAsync(long id, string text, CancellationToken ct)
    {
        var card = CardOr(id);
        var said = Required(text, "a comment");
        store.Save(card with { AwaitingManager = true });
        store.AddTrail(id, "you", "comment", said);
        await WakeManagerAsync(card, $"The person commented on card {id} \"{card.Title}\": {said}", ct);
        return await CardAsync(id, ct);
    }

    /// <summary>The edit IS the message: the team's Manager is told, in the card's workflow when it has one.</summary>
    private async Task WakeManagerAsync(CardRow card, string text, CancellationToken ct)
    {
        var manager = await ManagerOfAsync(card.Team, ct);
        var full = $"{text}\nAct on it: tell the member, acknowledge with board comment, or move the card back. Never drop it silently.";
        await bus.PersonTellAsync(card.Team, manager, full, null, ct, new JsonObject { ["about"] = card.Id });
    }

    private async Task<string> ManagerOfAsync(string team, CancellationToken ct)
    {
        var t = (await bus.TeamsAsync(ct)).FirstOrDefault(x => string.Equals(x.Name, team, StringComparison.OrdinalIgnoreCase))
                ?? throw new RpcException("no_such_team", $"there is no team '{team}'");
        return (await bus.AgentCardsAsync(t.Id, ct)).Select(c => c.Member).FirstOrDefault(m => m.Role == "manager")?.Name ?? "Manager";
    }

    // ---------------------------------------------------------------- the backlog

    private JsonNode EditItem(long id, string? title, string? body, string? state, string? team = null)
    {
        var item = ItemOr(id);
        if (state is not null && state is not ("pending" or "ready" or "implemented"))
            throw new RpcException("bad_params", "an item is pending, ready or implemented (declared is the Manager's)");
        if (state == "implemented" && item.State is "pending") throw new RpcException("bad_params", "mark it ready first");
        return ItemJson(Save(item with
        {
            Title = title?.Trim() is { Length: > 0 } t ? t : item.Title,
            Body = body ?? item.Body,
            State = state ?? item.State,
            Team = team ?? item.Team,
        }));
    }

    private JsonNode MoveItem(long id, string direction)
    {
        var items = store.Items().ToList();
        var i = items.FindIndex(x => x.Id == id);
        if (i < 0) throw new RpcException("no_such_item", $"there is no live item {BoardStore.Label(id)}");
        var j = direction == "up" ? i - 1 : direction == "down" ? i + 1 : throw new RpcException("bad_params", "direction is up or down");
        if (j < 0 || j >= items.Count) return Items(items);
        var (a, b) = (items[i], items[j]);
        store.SaveItem(a with { Position = b.Position });
        store.SaveItem(b with { Position = a.Position });
        return Items(store.Items());
    }

    private async Task<JsonNode> DispatchItemAsync(long id, long teamId, bool again, CancellationToken ct)
    {
        var item = ItemOr(id);
        if (item.State != "ready") throw new RpcException("not_ready", "only a ready item can be dispatched: read it and mark it ready first");
        if (item.DispatchedWorkflow is { } w && !again && store.CardsOfWorkflow(w).Any(c => c.Lane != Lanes.Done))
            throw new RpcException("in_flight", $"{item.Label} is in flight with {item.DispatchedTeam}; dispatch it again only on purpose (again: true)");
        var team = (await bus.TeamsAsync(ct)).FirstOrDefault(t => t.Id == teamId) ?? throw new RpcException("no_such_team", $"there is no team {teamId}");
        var manager = await ManagerOfAsync(team.Name, ct);
        var text = $"Backlog item {item.Label} \"{item.Title}\" is dispatched to your team. It is a SPEC, not a task: read it with " +
                   $"`backlog` (action show, item {item.Label}), cut it into a few cards with `board` (action plan, item {item.Label}) - " +
                   "three cards a person can read beat nine that restate the spec's headings - then `tell` each member with the card id. " +
                   "Declare the work complete with workflow_complete when every card is done.";
        var message = await bus.PersonTellAsync(team.Name, manager, text, null, ct, new JsonObject { ["item"] = item.Label });
        store.AddDispatch(item.Id, team.Name, message.CorrelationId, Provenance.Actor.Current?.User);
        return ItemJson(Save(item with { DispatchedTeam = team.Name, DispatchedWorkflow = message.CorrelationId, DispatchedAt = store.Now() }));
    }

    /// <summary>
    /// Makes a team for an item and dispatches it there (Yawble dispatch-to-new): a Manager on the CLI given, working on a
    /// folder on this computer or a repository cloned from its address, like the New team dialog.
    /// </summary>
    private async Task<JsonNode> DispatchNewAsync(long id, string name, string managerCli, string? repoPath, string? repoUrl, CancellationToken ct)
    {
        var item = ItemOr(id);
        if (item.State != "ready") throw new RpcException("not_ready", "only a ready item can be dispatched: read it and mark it ready first");
        if ((await bus.TeamsAsync(ct)).Any(t => string.Equals(t.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)))
            throw new RpcException("exists", $"a team named {name.Trim()} already exists");
        if (repoUrl is not null)
        {
            var repos = bus.Repos ?? throw new RpcException("unavailable", "cloning needs the app's data folder");
            try { repoPath = await repos.CloneForTeamAsync(name.Trim(), repoUrl, ct); }
            catch (ArgumentException e) { throw new RpcException("clone_failed", e.Message); }
        }
        var teamId = await bus.CreateTeamAsync(name.Trim(), "Manager", managerCli, repoPath, ct);
        return await DispatchItemAsync(id, teamId, again: true, ct);
    }

    private JsonNode PlaceItem(long id, long? after, long? before)
    {
        var item = ItemOr(id);
        if (item.ArchivedAt is not null) throw new RpcException("bad_params", "an archived item has no place in the list; restore it first");
        store.PlaceItem(id, after, before);
        return Items(store.Items());
    }

    /// <summary>An item with every dispatch: the team, the workflow's state and cards, who ran and what it used.</summary>
    private async Task<JsonNode> ItemDetailAsync(long id, CancellationToken ct)
    {
        var item = ItemOr(id);
        var node = ItemJson(item);
        var teams = await bus.TeamsAsync(ct);
        var workflows = new Dictionary<string, IReadOnlyList<WorkflowSummary>>(StringComparer.OrdinalIgnoreCase);
        var list = new JsonArray();
        foreach (var d in store.Dispatches(id))
        {
            var team = teams.FirstOrDefault(t => string.Equals(t.Name, d.Team, StringComparison.OrdinalIgnoreCase));
            WorkflowSummary? wf = null;
            if (team is not null)
            {
                if (!workflows.TryGetValue(team.Name, out var known))
                {
                    try { known = await bus.WorkflowsAsync(team.Name, 200, ct); }
                    catch (Exception e) when (e is BusRefusal or ArgumentException or InvalidOperationException) { known = []; }
                    workflows[team.Name] = known;
                }
                wf = known.FirstOrDefault(w => w.Id == d.Workflow);
            }
            var cards = store.CardsOfWorkflow(d.Workflow);
            var usage = store.UsageOf(d.Workflow);
            list.Add(new JsonObject
            {
                ["team"] = d.Team, ["teamId"] = team?.Id, ["teamGone"] = team is null, ["workflow"] = d.Workflow,
                ["dispatchedAt"] = d.DispatchedAt, ["dispatchedBy"] = d.DispatchedBy,
                ["state"] = wf?.State, ["ending"] = wf?.Ending, ["busy"] = wf is null ? null : new JsonArray([.. wf.Busy.Select(b => (JsonNode?)JsonValue.Create(b))]),
                ["lastAt"] = wf?.LastAt.ToString("O"),
                ["cards"] = cards.Count, ["cardsDone"] = cards.Count(c => c.Lane == Lanes.Done),
                ["runs"] = usage.Runs, ["members"] = new JsonArray([.. usage.Members.Select(m => (JsonNode?)JsonValue.Create(m))]),
                ["inputTokens"] = usage.InputTokens, ["outputTokens"] = usage.OutputTokens,
                ["firstStart"] = usage.FirstStart, ["lastEnd"] = usage.LastEnd,
            });
        }
        node["dispatches"] = list;
        return node;
    }

    // ---------------------------------------------------------------- the Manager's tools

    /// <summary>The Manager's <c>board</c> tool: show, plan, move, comment.</summary>
    public async Task<string> BoardToolAsync(RunContext run, JsonObject args, CancellationToken ct)
    {
        await ProjectAsync(ct);
        var team = run.Member.Team;
        var action = OptStr(args, "action") ?? "show";
        switch (action)
        {
            case "show" when args["card"] is not null:
            {
                // One card in full: what a member reads before working it ("as described on the card").
                var card = TeamCard(args, team);
                var sb = new StringBuilder();
                sb.AppendLine($"card {card.Id}: {card.Title}");
                sb.AppendLine($"lane {card.Lane}, {card.Status}, {(card.Member ?? "unassigned")}{(card.Item is null ? "" : $", backlog {card.Item}")}");
                sb.AppendLine();
                sb.AppendLine(string.IsNullOrWhiteSpace(card.Body) ? "(no description: the instruction you were given is the card)" : card.Body.Trim());
                var trail = store.Trail(card.Id);
                if (trail.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("Trail:");
                    foreach (var t in trail.TakeLast(20)) sb.AppendLine($"  {t.At[..Math.Min(16, t.At.Length)]} {t.Source} {t.Kind}: {t.Text}");
                }
                return sb.ToString().TrimEnd();
            }
            case "show":
            {
                var sb = new StringBuilder();
                foreach (var (laneId, label) in Lanes.All)
                {
                    var cards = store.Cards(team).Where(c => c.Lane == laneId).ToList();
                    sb.AppendLine($"{label} ({cards.Count})");
                    foreach (var c in cards)
                        sb.AppendLine($"  card {c.Id}: {c.Title} [{c.Status}] {(c.Member ?? "unassigned")}{(c.Item is null ? "" : $" {c.Item}")}{(c.AwaitingManager ? " (awaiting you)" : "")}");
                }
                return sb.ToString().TrimEnd();
            }
            case "plan":
            {
                if (run.Member.Role != MemberRole.Manager) throw new BusRefusal("not_allowed", "only the Manager plans cards");
                var title = Required(OptStr(args, "title"), "a title");
                var itemLabel = OptStr(args, "item") is { } l && BoardStore.ItemId(l) is { } iid ? BoardStore.Label(iid) : null;
                var card = store.AddCard(team, null, itemLabel, title, OptStr(args, "body") ?? "", "queued", Lanes.Todo, run.Workflow == 0 ? null : run.Workflow);
                store.AddTrail(card.Id, run.Member.Name, "planned", title);
                return $"ok: planned card {card.Id} \"{card.Title}\" in To Do, unassigned. Give it to a member with tell (member, instruction, card: {card.Id}).";
            }
            case "move":
            {
                var card = TeamCard(args, team);
                var lane = OptStr(args, "lane") ?? throw new BusRefusal("bad_params", "move needs a lane: todo, in-progress, blocked or done");
                if (!Lanes.IsLane(lane)) throw new BusRefusal("bad_params", "lane is todo, in-progress, blocked or done");
                var note = OptStr(args, "note");
                // B019: the Manager closing a tester's card the tester never ran on says why, on the card.
                var untested = run.Member.Role == MemberRole.Manager && lane == Lanes.Done && card.Lane != Lanes.Done
                    ? await UntestedTesterAsync(run.Member.TeamId, card, ct) : null;
                if (untested is not null && string.IsNullOrWhiteSpace(note))
                    throw new BusRefusal("needs_reason",
                        $"card {card.Id} is {untested}'s and {untested} has not run on it yet. Give it to {untested} with tell (card: {card.Id}), " +
                        "or move it to done with the reason in note (for example \"no tester on this team\" or \"the person asked to skip testing\").");
                store.Save(card with { Lane = lane, Placed = true });
                store.AddTrail(card.Id, run.Member.Name, "moved", $"Moved to {Lanes.All.First(x => x.Id == lane).Label}{Note(note)}");
                if (untested is not null) store.AddTrail(card.Id, run.Member.Name, "comment", $"Done without {untested}'s test: {note!.Trim()}");
                return $"ok: card {card.Id} is in {lane}.";
            }
            case "comment":
            {
                var card = TeamCard(args, team);
                store.AddTrail(card.Id, run.Member.Name, "comment", Required(OptStr(args, "text"), "text"));
                return $"ok: commented on card {card.Id}.";
            }
            case "edit":
            {
                if (run.Member.Role != MemberRole.Manager) throw new BusRefusal("not_allowed", "only the Manager edits cards");
                var card = TeamCard(args, team);
                var title = OptStr(args, "title");
                var body = OptStr(args, "body");
                if (title is null && body is null) throw new BusRefusal("bad_params", "edit needs a new title, a new body, or both");
                store.Save(card with { Title = title ?? card.Title, Body = body ?? card.Body });
                store.AddTrail(card.Id, run.Member.Name, "edited", title is not null ? $"Retitled: {title}" : "Rescoped the card");
                return $"ok: card {card.Id} edited.";
            }
            default:
                throw new BusRefusal("bad_params", "action is show, plan, edit, move or comment");
        }
    }

    /// <summary>
    /// The Manager's <c>backlog</c> tool: show an item dispatched to its team, list its team's items, or add a follow-up
    /// item for the person to review (it starts pending: the person marks it ready and dispatches it).
    /// </summary>
    public string BacklogTool(RunContext run, JsonObject args)
    {
        var action = OptStr(args, "action") ?? (OptStr(args, "item") is null ? "list" : "show");
        if (action == "list")
        {
            var mine = store.Items().Where(i => string.Equals(i.Team, run.Member.Team, StringComparison.OrdinalIgnoreCase)
                || string.Equals(i.DispatchedTeam, run.Member.Team, StringComparison.OrdinalIgnoreCase)).ToList();
            return mine.Count == 0 ? "No backlog items for your team."
                : string.Join(Environment.NewLine, mine.Select(i => $"{i.Label} [{i.State}] {i.Title}"));
        }
        if (action == "add")
        {
            if (run.Member.Role != MemberRole.Manager) throw new BusRefusal("not_allowed", "only the Manager adds backlog items");
            var title = Required(OptStr(args, "title"), "a title");
            var added = store.AddItem(title, OptStr(args, "body") ?? "", run.Member.Team);
            return $"ok: added {added.Label} \"{added.Title}\" for the person to review (pending). It is not work until the person decides.";
        }
        if (action != "show") throw new BusRefusal("bad_params", "action is list, show or add");
        var id = BoardStore.ItemId(OptStr(args, "item")) ?? throw new BusRefusal("bad_params", "backlog show needs item, e.g. B007");
        var item = store.Item(id);
        if (item is null || !string.Equals(item.DispatchedTeam, run.Member.Team, StringComparison.OrdinalIgnoreCase))
            throw new BusRefusal("not_found", $"{BoardStore.Label(id)} was not dispatched to your team");
        return $"{item.Label}: {item.Title}\n\n{item.Body}";
    }

    /// <summary>
    /// The tester the card is for, when that tester has not run on it yet; null otherwise. A tester is a member (not the
    /// Manager) whose name starts with "Tester" ("Tester Wren" is TesterWren) or whose job text starts with the word
    /// "Tester". The card is for one when it is the card's member now or was assigned to it ("Assigned to X:" on the
    /// trail). A tester has run on the card once anything on the trail is from it: its runs' progress, hand-back,
    /// question, block, failure or end, and its own board comments or moves, are all written under its name.
    /// </summary>
    private async Task<string?> UntestedTesterAsync(long teamId, CardRow card, CancellationToken ct)
    {
        var testers = (await bus.AllMemberRowsAsync(ct))
            .Where(m => m.TeamId == teamId && m.Role != "manager" && IsTester(m))
            .Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (testers.Count == 0) return null;
        var trail = store.Trail(card.Id);
        var forWhom = trail.Where(t => t.Kind == "assigned" && t.Text.StartsWith("Assigned to ", StringComparison.Ordinal))
            .Select(t => t.Text["Assigned to ".Length..].Split(':')[0].Trim())
            .Prepend(card.Member ?? "")
            .Where(testers.Contains).Distinct(StringComparer.OrdinalIgnoreCase);
        return forWhom.FirstOrDefault(tester => !trail.Any(t => string.Equals(t.Source, tester, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool IsTester(MemberRow m) => StartsWithWord(m.Name, "Tester", wholeWord: false) || StartsWithWord(m.Instructions, "Tester", wholeWord: true);

    private static bool StartsWithWord(string? s, string word, bool wholeWord)
    {
        var t = (s ?? "").TrimStart();
        return t.StartsWith(word, StringComparison.OrdinalIgnoreCase) && (!wholeWord || t.Length == word.Length || !char.IsLetter(t[word.Length]));
    }

    private CardRow TeamCard(JsonObject args, string team)
    {
        var id = args["card"] is JsonValue v && (Ui.UiRpc.WholeNumber(v, out var n) || (v.TryGetValue<string>(out var s) && long.TryParse(s, out n))) ? n
            : throw new BusRefusal("bad_params", "which card? (card: its id)");
        var card = store.Card(id);
        if (card is null || !string.Equals(card.Team, team, StringComparison.OrdinalIgnoreCase)) throw new BusRefusal("not_found", $"your team has no card {id}");
        return card;
    }

    // ---------------------------------------------------------------- JSON and plumbing

    private JsonObject CardJson(CardRow c, IReadOnlyList<TeamSummary> teams) => new()
    {
        ["id"] = c.Id, ["team"] = c.Team, ["teamId"] = teams.FirstOrDefault(t => string.Equals(t.Name, c.Team, StringComparison.OrdinalIgnoreCase))?.Id,
        ["member"] = c.Member, ["item"] = c.Item, ["title"] = c.Title, ["body"] = c.Body, ["status"] = c.Status, ["lane"] = c.Lane,
        ["placed"] = c.Placed, ["awaitingManager"] = c.AwaitingManager, ["workflow"] = c.Workflow,
        ["createdAt"] = c.CreatedAt, ["updatedAt"] = c.UpdatedAt, ["trailCount"] = store.TrailCount(c.Id), ["clearedAt"] = c.ClearedAt,
    };

    private JsonNode Items(IEnumerable<ItemRow> items) => new JsonArray(items.Select(i => (JsonNode)ItemJson(i)).ToArray());

    private JsonObject ItemJson(ItemRow i)
    {
        var inFlight = i.DispatchedWorkflow is { } w && store.CardsOfWorkflow(w).Any(c => c.Lane != Lanes.Done);
        return new JsonObject
        {
            ["id"] = i.Id, ["label"] = i.Label, ["team"] = i.Team, ["title"] = i.Title, ["body"] = i.Body, ["state"] = i.State,
            ["archivedAt"] = i.ArchivedAt, ["dispatchedTeam"] = i.DispatchedTeam, ["dispatchedWorkflow"] = i.DispatchedWorkflow,
            ["dispatchedAt"] = i.DispatchedAt, ["inFlight"] = inFlight, ["createdAt"] = i.CreatedAt, ["updatedAt"] = i.UpdatedAt,
            ["dispatchCount"] = store.DispatchCount(i.Id),
        };
    }

    private ItemRow Save(ItemRow i)
    {
        store.SaveItem(i);
        return store.Item(i.Id)!;
    }

    private CardRow CardOr(long id) => store.Card(id) ?? throw new RpcException("no_such_card", $"there is no card {id}");

    private ItemRow ItemOr(long id) => store.Item(id) ?? throw new RpcException("no_such_item", $"there is no item {BoardStore.Label(id)}");

    private static string Note(string? note) => string.IsNullOrWhiteSpace(note) ? "" : $". Note: {note.Trim()}";

    private static string Required(string? s, string what) =>
        string.IsNullOrWhiteSpace(s) ? throw new RpcException("bad_params", $"{what} is needed") : s.Trim();

    private static string Str(JsonObject? p, string name) =>
        p?[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : throw new RpcException("bad_params", $"missing '{name}'");

    private static string? OptStr(JsonObject? p, string name) =>
        p?[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

    private static IReadOnlyList<long> Ids(JsonObject? p, string name)
    {
        if (p?[name] is not JsonArray a) throw new RpcException("bad_params", $"missing '{name}' (the card ids to clear)");
        return a.Select(n => n is JsonValue v && Ui.UiRpc.WholeNumber(v, out var l) ? l : throw new RpcException("bad_params", $"'{name}' holds card ids")).ToList();
    }

    private static bool Bool(JsonObject? p, string name) => p?[name] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    private static long? OptLong(JsonObject? p, string name) =>
        p?[name] is JsonValue v && Ui.UiRpc.WholeNumber(v, out var l) ? l : null;

    private static long Long(JsonObject? p, string name) =>
        p?[name] is JsonValue v && Ui.UiRpc.WholeNumber(v, out var l) ? l : throw new RpcException("bad_params", $"missing '{name}'");
}
