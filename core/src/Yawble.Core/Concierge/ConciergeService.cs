using Yawble.Core.Agents.Profiles;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Ipc;
using Yawble.Core.Launch;
using Yawble.Core.Sandboxing;
using Yawble.Core.Policy;

namespace Yawble.Core.Concierge;

/// <summary>
/// The Concierge: the person's chats with an agent that knows every team here and acts on them for the person.
/// A turn is ONE headless run of the
/// conversation's agent CLI in a sandbox, given the whole context (<see cref="ConciergePrompt"/>), with the
/// Concierge's tools at its gateway (<see cref="ConciergeTools"/>). Conversations and their messages are kept
/// (<see cref="ConciergeStore"/>); while a turn runs, the reply streams to the window as concierge.turn pushes.
/// Each conversation has its own sandbox folder (team "_concierge", member "c-&lt;id&gt;"), and one turn at a time.
/// Between turns the Concierge keeps its follow-ups (<see cref="ConciergeWatcher"/>): the core posts team events the
/// conversation watches as updates (concierge.update pushes), and a watch with follow-up wakes the Concierge for a
/// review turn once the updates settle, never while a turn of that conversation runs.
/// </summary>
public sealed class ConciergeService
{
    public const string Team = "_concierge";
    public const string TurnEvent = "concierge.turn";
    public const string UpdateEvent = "concierge.update";
    public static readonly TimeSpan WatchEvery = TimeSpan.FromSeconds(2);
    public static readonly string[] Prefixes = ["concierge."];
    public static readonly TimeSpan PushEvery = TimeSpan.FromMilliseconds(250);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The action tools, set by the app once the Board and repositories exist.</summary>
    public ConciergeActions? Actions { get; set; }

    /// <summary>Resolves a conversation's agent: a built-in's id or a custom agent's name (set by the app; null = built-ins only).</summary>
    public Agents.Profiles.IAgentResolver? AgentResolver { get; set; }

    private readonly ConciergeStore _store;
    private readonly BusEngine _bus;
    private readonly Func<LaunchRequest, CancellationToken, Task<LaunchOutcome>> _launch;
    private readonly RunRegistry _registry;
    private readonly Ui.AppEvents _events;
    private readonly Func<string, JsonObject?, CancellationToken, Task<JsonNode?>> _ui;
    private readonly Func<CancellationToken, Task<string>> _defaultCli;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, Turn> _turns = new();
    private readonly ConciergeWatcher _watcher;
    private readonly TimeSpan _followUpAfter;
    /// <summary>Conversations whose updates asked for a review turn, and when it may start (updates settle first).</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _followUps = new();
    /// <summary>Whether each conversation's latest message from the person came through the Cloud Link: its review turns keep that scope.</summary>
    private readonly ConcurrentDictionary<string, bool> _remote = new();

    private sealed class Turn(string conversationId, string cli, ConciergeOutput output)
    {
        public string ConversationId { get; } = conversationId;
        public string Cli { get; } = cli;
        public ConciergeOutput Output { get; } = output;
        public volatile bool StopRequested;
        public long LastPush;
    }

    /// <summary>
    /// What the Concierge's tools call: board.* and backlog.* go to the Board (made after the Concierge, so it is
    /// looked up per call), everything else to the screens' RPCs.
    /// </summary>
    public static Func<string, JsonObject?, CancellationToken, Task<JsonNode?>> Route(
        Func<string, JsonObject?, CancellationToken, Task<JsonNode?>> ui,
        Func<Func<string, JsonObject?, CancellationToken, Task<JsonNode?>>?> board) =>
        (method, p, ct) => Companion.CompanionRpc.Handles(method)
            // The yawble companion's pairings and approvals are the person's at the window, never a Concierge tool's.
            ? throw new RpcException("forbidden", Companion.CompanionRpc.WindowOnly)
            : Board.BoardService.Handles(method)
            ? (board() ?? throw new RpcException("not_ready", "the Board is still starting")).Invoke(method, p, ct)
            : ui(method, p, ct);

    public ConciergeService(ConciergeStore store, BusEngine bus, Func<LaunchRequest, CancellationToken, Task<LaunchOutcome>> launch,
        RunRegistry registry, Ui.AppEvents events, Func<string, JsonObject?, CancellationToken, Task<JsonNode?>> ui,
        Func<CancellationToken, Task<string>> defaultCli, TimeProvider? clock = null, TimeSpan? followUpAfter = null)
    {
        _store = store;
        _bus = bus;
        _launch = launch;
        _registry = registry;
        _events = events;
        _ui = ui;
        _defaultCli = defaultCli;
        _clock = clock ?? TimeProvider.System;
        _watcher = new ConciergeWatcher(store, bus, _clock);
        _followUpAfter = followUpAfter ?? TimeSpan.FromSeconds(5);
        _registry.Added += Attach;
    }

    public static bool Handles(string method) => Prefixes.Any(p => method.StartsWith(p, StringComparison.Ordinal));

    /// <summary>The conversation's sandbox member: its own folder under the "_concierge" team.</summary>
    public static string MemberOf(string conversationId) => "c-" + conversationId[..Math.Min(12, conversationId.Length)];

    public async Task<JsonNode?> DispatchAsync(string method, JsonObject? p, CancellationToken ct) => method switch
    {
        "concierge.list" => new JsonArray(await Task.WhenAll(_store.List().Select(c => SummaryAsync(c, ct)))),
        "concierge.get" => await GetAsync(Id(p), ct),
        "concierge.send" => await SendAsync(p, ct),
        "concierge.stop" => new JsonObject { ["stopped"] = Stop(Id(p)) },
        "concierge.rename" => await RenameAsync(Id(p), Str(p, "title"), ct),
        "concierge.delete" => new JsonObject { ["deleted"] = Delete(Id(p)) },
        "concierge.setTeam" => await SetTeamAsync(Id(p), TeamId(p), ct),
        "concierge.read" => Read(Id(p)),
        "concierge.unwatch" => new JsonObject { ["stopped"] = EndWatch(Id(p), WatchId(p)) },
        "concierge.setWatch" => SetWatch(Id(p), WatchId(p), p?["followUp"] is JsonValue f && f.TryGetValue<bool>(out var b) && b),
        _ => throw new RpcException("unknown_method", $"no method named '{method}'"),
    };

    // ---------------------------------------------------------------- reading

    private async Task<JsonNode> SummaryAsync(ConversationRow c, CancellationToken ct) => new JsonObject
    {
        ["id"] = c.Id,
        ["title"] = c.Title,
        ["cli"] = c.Cli,
        ["teamId"] = c.TeamId,
        ["team"] = await TeamNameAsync(c.TeamId, ct),
        ["createdAt"] = c.CreatedAt,
        ["updatedAt"] = c.UpdatedAt,
        ["running"] = _turns.ContainsKey(c.Id),
        ["unread"] = c.Unread,
        ["watching"] = _store.ActiveWatches(c.Id).Count,
    };

    private async Task<JsonNode?> GetAsync(string id, CancellationToken ct)
    {
        var c = _store.Get(id) ?? throw new RpcException("no_such_conversation", "that conversation is gone");
        var node = (JsonObject)await SummaryAsync(c, ct);
        node["messages"] = new JsonArray(_store.Messages(id).Select(m => (JsonNode?)MessageJson(m)).ToArray());
        node["turn"] = _turns.TryGetValue(id, out var turn) ? TurnJson(turn, "running", null) : null;
        node["watches"] = new JsonArray(_store.ActiveWatches(id).Select(w => (JsonNode?)WatchJson(w)).ToArray());
        return node;
    }

    private static JsonObject WatchJson(WatchRow w) => new()
    {
        ["id"] = w.Id,
        ["team"] = w.Team,
        ["workflow"] = w.Workflow,
        ["what"] = w.What,
        ["followUp"] = w.FollowUp,
        ["createdAt"] = w.CreatedAt,
        ["expiresAt"] = w.ExpiresAt,
    };

    private static JsonObject MessageJson(ConciergeMessageRow m) => new()
    {
        ["id"] = m.Id,
        ["role"] = m.Role,
        ["text"] = m.Text,
        ["at"] = m.At,
        ["state"] = m.State,
        ["error"] = m.Error,
        ["tools"] = JsonSerializer.SerializeToNode(m.Tools, Json),
        ["cli"] = m.Cli,
        ["team"] = m.Team,
        ["kind"] = m.Kind,
    };

    private async Task<string?> TeamNameAsync(long? teamId, CancellationToken ct) =>
        teamId is null ? null : (await _bus.TeamsAsync(ct)).FirstOrDefault(t => t.Id == teamId)?.Name;

    // ---------------------------------------------------------------- a turn

    private async Task<JsonNode?> SendAsync(JsonObject? p, CancellationToken ct)
    {
        var images = Ui.Attachments.Read(p);
        var text = (OptStr(p, "text") ?? "").Trim();
        if (text.Length == 0 && images.Count > 0) text = images.Count == 1 ? "Look at this image." : "Look at these images.";
        if (text.Length == 0) throw new RpcException("empty", "write a message");
        var cli = OptStr(p, "cli");
        if (cli is not null)
        {
            cli = AgentResolver.Canonical(cli) ?? throw new RpcException("bad_params", $"no agent named '{cli}'");
        }
        var id = OptStr(p, "id");
        ConversationRow conversation;
        if (id is null)
        {
            id = Guid.NewGuid().ToString("N");
            conversation = _store.Create(id, ConciergePrompt.Title(text), cli ?? await _defaultCli(ct), p?.ContainsKey("teamId") == true ? TeamId(p) : null);
            // Its first agent, as a new member's is (from nothing).
            _bus.AgentChanges?.RecordSubjectChange(Agents.Profiles.AgentChangeLog.KindConciergeAgent, Agents.Profiles.AgentChangeLog.ConciergeSubject(id),
                new Agents.Profiles.AgentSetting("", ""), new Agents.Profiles.AgentSetting(conversation.Cli, ""), Agents.Profiles.AgentChangeLog.HowSettings);
        }
        else
        {
            conversation = _store.Get(id) ?? throw new RpcException("no_such_conversation", "that conversation is gone");
            if (_turns.ContainsKey(id)) throw new RpcException("busy", "the Concierge is still answering in this chat: wait, or stop it");
            if (cli is not null && cli != conversation.Cli)
            {
                _store.SetCli(id, cli);
                // The Concierge's agent changed (its model is the agent's default: it has no model of its own).
                _bus.AgentChanges?.RecordSubjectChange(Agents.Profiles.AgentChangeLog.KindConciergeAgent, Agents.Profiles.AgentChangeLog.ConciergeSubject(id),
                    new Agents.Profiles.AgentSetting(conversation.Cli, ""), new Agents.Profiles.AgentSetting(cli, ""),
                    Provenance.Actor.Current?.Via == Provenance.Actor.Concierge ? Agents.Profiles.AgentChangeLog.HowConciergeTool : Agents.Profiles.AgentChangeLog.HowSettings);
            }
            if (p?.ContainsKey("teamId") == true) _store.SetTeam(id, TeamId(p));
            conversation = _store.Get(id)!;
        }
        if (images.Count > 0)
        {
            // The conversation's own home folder (its sandbox's HOME, read/write to its runs and to no other agent).
            var repos = _bus.Repos ?? throw new RpcException("unavailable", "images need the app's data folder");
            var home = Path.Combine(repos.MemberRoot(Team, MemberOf(id)), "home");
            text = Ui.Attachments.WithImages(text, Ui.Attachments.Save(home, images, _clock));
        }
        await BeginTurnAsync(conversation, text, ct);
        return new JsonObject { ["id"] = id };
    }

    /// <summary>
    /// Starts a turn: for the person's <paramref name="text"/>, or with none, a review of the updates that arrived (a
    /// watch's follow-up). The person's message also covers any pending review: their turn sees the same updates.
    /// </summary>
    private async Task BeginTurnAsync(ConversationRow conversation, string? text, CancellationToken ct)
    {
        var id = conversation.Id;
        var turn = new Turn(id, conversation.Cli, new ConciergeOutput(AgentResolver.Resolve(conversation.Cli).Cli));
        if (!_turns.TryAdd(id, turn)) throw new RpcException("busy", "the Concierge is still answering in this chat");
        try
        {
            _followUps.TryRemove(id, out _);
            if (text is not null)
            {
                _store.AddMessage(id, "user", text);
                _store.MarkRead(id);
            }
            var focus = await TeamNameAsync(conversation.TeamId, ct);
            // Who started this turn decides what its tools may do: only a message from the app's window on this computer is
            // local; anything else (the Cloud Link, or a caller not known) keeps to what a remote viewer may do, and so do
            // the review turns after it (one whose origin is unknown, after a restart, too). A review turn (no message
            // from the person) runs no action at all.
            var byPerson = text is not null;
            var remote = byPerson ? Provenance.Actor.Current?.Via != Provenance.Actor.App : _remote.GetValueOrDefault(id, true);
            if (byPerson) _remote[id] = remote;
            var scope = new ConciergeTurn(id, byPerson, remote);
            var tools = new ConciergeTools(_bus, _store, _ui, () => focus, id, _clock, Actions?.ForTurn(scope), remote, scope);
            var prompt = ConciergePrompt.Build(await tools.TeamListAsync(ct), focus, _store.Notes(), _store.Messages(id), _clock.GetLocalNow(),
                review: text is null);
            Publish(TurnJson(turn, "running", null));
            _ = Task.Run(() => RunTurnAsync(turn, prompt, tools));
        }
        catch
        {
            _turns.TryRemove(id, out _);
            throw;
        }
    }

    // ---------------------------------------------------------------- keeping tabs (between turns)

    /// <summary>Runs <see cref="TickAsync"/> every <see cref="WatchEvery"/> until <paramref name="ct"/> is cancelled.</summary>
    public async Task WatchAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(WatchEvery, _clock);
        try
        {
            do
            {
                try
                {
                    await TickAsync(ct);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    Console.Error.WriteLine($"concierge: watching failed this time: {e.Message}");
                }
            }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException)
        {
            // the core is stopping
        }
    }

    /// <summary>Posts what the watches were waiting for, then starts the review turns that are due.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        foreach (var hit in await _watcher.PollAsync(ct))
        {
            _events.Publish(UpdateEvent, new JsonObject
            {
                ["conversationId"] = hit.ConversationId,
                ["message"] = MessageJson(hit.Message),
                ["unread"] = _store.Get(hit.ConversationId)?.Unread ?? 0,
            });
            if (hit.FollowUp) _followUps[hit.ConversationId] = _clock.GetUtcNow() + _followUpAfter; // settle: later updates push it back
        }
        var now = _clock.GetUtcNow();
        foreach (var (id, due) in _followUps)
        {
            if (due > now || _turns.ContainsKey(id)) continue;
            if (_store.Get(id) is not { } conversation)
            {
                _followUps.TryRemove(id, out _);
                continue;
            }
            try
            {
                await BeginTurnAsync(conversation, null, ct);
            }
            catch (RpcException)
            {
                // a turn started in between: it sees the updates; nothing to review separately
            }
        }
    }

    private JsonNode Read(string id)
    {
        _store.MarkRead(id);
        return new JsonObject { ["unread"] = 0 };
    }

    private bool EndWatch(string conversationId, long watchId) =>
        _store.Watch(watchId) is { } w && w.ConversationId == conversationId && _store.EndWatch(watchId, "cancelled");

    private JsonNode SetWatch(string conversationId, long watchId, bool followUp)
    {
        if (_store.Watch(watchId) is not { State: "active" } w || w.ConversationId != conversationId)
            throw new RpcException("no_such_watch", "that watch has ended");
        _store.SetWatchFollowUp(watchId, followUp);
        return WatchJson(_store.Watch(watchId)!);
    }

    private static long WatchId(JsonObject? p) =>
        p?["watchId"] is JsonValue v && v.TryGetValue<long>(out var id) ? id : throw new RpcException("bad_params", "watchId is required");

    private async Task RunTurnAsync(Turn turn, string prompt, ConciergeTools tools)
    {
        var agent = AgentResolver.Resolve(turn.Cli);
        var member = new LaunchMember(0, Team, MemberOf(turn.ConversationId), agent.Cli, Agent: agent.RecordedAgent, Custom: agent.Argv, AgentModel: agent.Custom ? agent.DefaultModel : null);
        string state, text;
        string? error = null, runKey = null;
        try
        {
            var outcome = await _launch(new LaunchRequest(Team, member.Name, RunKind.Headless, prompt, null, Subject: member, Tools: tools), CancellationToken.None);
            runKey = outcome.RunKey;
            turn.Output.Complete();
            text = turn.Output.Text;
            (state, error) = outcome.End switch
            {
                RunEnd.Completed when turn.Output.Error is { } e => ("failed", e),
                RunEnd.Completed when text.Length == 0 => ("failed", "the agent gave no answer"),
                RunEnd.Completed => ("done", null),
                RunEnd.Stopped => ("stopped", null),
                _ => ("failed", outcome.Reason ?? RunEnds.Code(outcome.End)),
            };
            if (turn.StopRequested && state != "done") (state, error) = ("stopped", null);
        }
        catch (Exception e)
        {
            text = turn.Output.Text;
            state = "failed";
            error = e.Message;
        }
        try
        {
            _store.AddMessage(turn.ConversationId, "assistant", text, state, error, turn.Output.Tools, turn.Cli, runKey);
        }
        finally
        {
            _turns.TryRemove(turn.ConversationId, out _);
            Publish(TurnJson(turn, state, error, text));
        }
    }

    /// <summary>Streams a turn's output into its pushes: the launcher adds the run before its output starts.</summary>
    private void Attach(LiveRun run)
    {
        if (run.Team != Team) return;
        var turn = _turns.Values.FirstOrDefault(t => MemberOf(t.ConversationId) == run.Member);
        if (turn is null) return;
        run.Output.Data += data =>
        {
            turn.Output.Write(data);
            var now = _clock.GetTimestamp();
            if (_clock.GetElapsedTime(Interlocked.Read(ref turn.LastPush), now) < PushEvery) return;
            Interlocked.Exchange(ref turn.LastPush, now);
            Publish(TurnJson(turn, "running", null));
        };
    }

    private bool Stop(string id)
    {
        if (!_turns.TryGetValue(id, out var turn)) return false;
        turn.StopRequested = true;
        return _registry.Stop(Team, MemberOf(id));
    }

    private JsonObject TurnJson(Turn turn, string state, string? error, string? text = null) => new()
    {
        ["conversationId"] = turn.ConversationId,
        ["state"] = state,
        ["text"] = text ?? turn.Output.Text,
        ["tools"] = JsonSerializer.SerializeToNode(turn.Output.Tools, Json),
        ["error"] = error,
    };

    private void Publish(JsonObject e) => _events.Publish(TurnEvent, e);

    // ---------------------------------------------------------------- managing conversations

    private async Task<JsonNode?> RenameAsync(string id, string title, CancellationToken ct)
    {
        title = title.Trim();
        if (title.Length is 0 or > 120) throw new RpcException("bad_params", "a title needs 1 to 120 characters");
        _store.Rename(id, title);
        return await SummaryAsync(_store.Get(id) ?? throw new RpcException("no_such_conversation", "that conversation is gone"), ct);
    }

    private async Task<JsonNode?> SetTeamAsync(string id, long? teamId, CancellationToken ct)
    {
        _store.SetTeam(id, teamId);
        return await SummaryAsync(_store.Get(id) ?? throw new RpcException("no_such_conversation", "that conversation is gone"), ct);
    }

    private bool Delete(string id)
    {
        if (_turns.ContainsKey(id)) throw new RpcException("busy", "stop the Concierge's answer in this chat first");
        _followUps.TryRemove(id, out _);
        _remote.TryRemove(id, out _);
        return _store.Delete(id);
    }

    private static string Id(JsonObject? p) => Str(p, "id");

    private static long? TeamId(JsonObject? p) =>
        p?["teamId"] is JsonValue v && v.TryGetValue<long>(out var id) ? id : null;

    private static string Str(JsonObject? p, string name) =>
        OptStr(p, name) ?? throw new RpcException("bad_params", $"{name} is required");

    private static string? OptStr(JsonObject? p, string name) =>
        p?[name] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
}
