using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Yawble.Core.Bus;
using Yawble.Core.Concierge;
using Yawble.Core.Ipc;
using Yawble.Core.Provenance;

namespace Yawble.Core.Companion;

public sealed record CompanionResult(string Text, bool IsError, string? RequestId = null);
public sealed record PairingTicket(string Id, string Code, DateTimeOffset Expires);
public sealed record PairingOutcome(bool Approved, string? Key, CompanionLevel? Level, string Reason);
/// <summary>Running: approved and executing now (a second Approve runs nothing).</summary>
public enum ActionState { Pending, Done, Failed, Denied, Expired, Changed, Running }
public sealed record ActionStatus(string Id, ActionState State, string Plan, string? Result, string? NextId);

/// <summary>
/// What a paired external client (the yawble companion) may do, and how. Every call goes through the Concierge's own tools
/// (<see cref="ConciergeTools"/> → ConciergeReadTools / ConciergeActions) as <see cref="Actor.ExternalClient"/>: the same
/// refusals, scrubbing and confirmation binding apply. Reads and acts run in a turn that is the person's (they paired the
/// client and granted its level) but remote (Cloud Link parity). A destructive call never runs on the client's call: it
/// becomes an approval request with the exact plan, which only the person at the app's window approves or denies. On
/// approval this service runs exactly the stored tool and arguments once, in a fresh local turn of the request's own
/// conversation, redeeming the token from its OWN <see cref="ConfirmationBook"/> (never shown to the client, never the
/// Concierge chat's). Pairings and requests are in memory only.
/// </summary>
public sealed partial class CompanionService(
    BusEngine bus,
    ConciergeStore store,
    Func<string, JsonObject?, CancellationToken, Task<JsonNode?>> route,
    Func<ConciergeTurn, ConfirmationBook, ConciergeActions?> actionsFor,
    CompanionClientStore clients,
    AuditLog audit,
    Ui.AppEvents events,
    TimeProvider? clock = null) : IAsyncDisposable
{
    public const string ChangedEvent = "companion.changed";
    public static readonly TimeSpan PairingLife = TimeSpan.FromMinutes(5);
    /// <summary>
    /// A little shorter than its token's life (<see cref="ConfirmationBook.Life"/>): approved at its last moment, the run still
    /// redeems its token, so a late approval says Expired, never "the list changed".
    /// </summary>
    public static readonly TimeSpan ActionLife = ConfirmationBook.Life - TimeSpan.FromSeconds(5);
    public const int MaxPendingPairings = 3;
    public const int MaxPendingPerClient = 10;

    /// <summary>The longest argument text a destructive request takes: its plan shows every argument that runs, in full.</summary>
    public const int MaxArgumentChars = 8 * 1024;

    /// <summary>How long a finished request stays readable by its client (request_status).</summary>
    private static readonly TimeSpan KeepFinished = TimeSpan.FromHours(1);

    /// <summary>How long after a pairing's life an approved key nobody collected is kept before the pairing is undone.</summary>
    private static readonly TimeSpan CollectGrace = TimeSpan.FromMinutes(1);

    /// <summary>The destructive tools bound to the commit their plan showed: a branch or team branch that moved since is a new request.</summary>
    private static readonly string[] ShaBound = ["git_merge_team", "git_push_branch", "git_sync_branch"];

    private const string CancelledWhileRunning = "failed: cancelled while running (it may have partly run; check before asking again)";

    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ConfirmationBook _book = new(clock);
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Pairing> _pairings = [];
    private readonly Dictionary<string, Request> _requests = [];
    /// <summary>Approved runs under way (by request id), each on its own task with the service's lifetime token.</summary>
    private readonly Dictionary<string, Task<ActionStatus>> _runs = [];
    private readonly CancellationTokenSource _life = new();
    private int _disposed;

    /// <summary>Raised with a client's name when it is revoked (its open sessions are closed by the endpoint).</summary>
    public event Action<string>? Revoked;

    private sealed class Pairing(string id, string name, CompanionLevel level, string code, DateTimeOffset expires)
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public CompanionLevel Level { get; } = level;
        public string Code { get; } = code;
        public DateTimeOffset Expires { get; } = expires;
        public PairingOutcome? Outcome { get; set; }
        public bool Handed { get; set; }
        /// <summary>The client row an approval made (an uncollected one is undone by this id, never by name).</summary>
        public long? ClientId { get; set; }
        public TaskCompletionSource Decided { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Request(string id, long clientId, string client, string tool, JsonObject args, string plan, string? token, string? bound, string conversation, DateTimeOffset expires)
    {
        public string Id { get; } = id;
        /// <summary>The client row that made it: a later client of the same name is another client (never sees or decides it).</summary>
        public long ClientId { get; } = clientId;
        public string Client { get; } = client;
        public string Tool { get; } = tool;
        public JsonObject Args { get; } = args;
        public string Plan { get; } = plan;
        public string? Token { get; } = token;
        /// <summary>For <see cref="ShaBound"/> tools: the commit the plan showed.</summary>
        public string? Bound { get; } = bound;
        public string Conversation { get; } = conversation;
        public DateTimeOffset Expires { get; } = expires;
        public ActionState State { get; set; } = ActionState.Pending;
        public string? Result { get; set; }
        public string? NextId { get; set; }
        public DateTimeOffset? FinishedAt { get; set; }
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>A destructive call made ready: either an answer at once (nothing to do), or the plan to approve.</summary>
    private sealed record Prepared(string? Immediate, string Plan, string? Token, string? Bound);

    private DateTimeOffset Now => _clock.GetUtcNow();

    private void Changed() => events.Publish(ChangedEvent, null);

    private static string NewId(string prefix) => prefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));

    [GeneratedRegex("confirm\\s*=\\s*\"?[0-9a-fA-F]{16}\"?")]
    private static partial Regex ConfirmArgument();

    [GeneratedRegex("\\b[0-9a-f]{16}\\b")]
    private static partial Regex TokenLike();

    /// <summary>
    /// Every text a client or the window sees: scrubbed of secrets, and never a confirmation token or the Concierge's
    /// "call again with confirm" instruction (the companion's yes is the person's click, never a token).
    /// </summary>
    private static string ClientSafe(string text) =>
        ConfirmArgument().Replace(ConciergeReadTools.Scrub(text), "(the person approves this in the Yawble window)");

    /// <summary>
    /// Text on the request path (plans, request results, the window's list): as <see cref="ClientSafe"/>, and any standalone
    /// 16-hex string, the shape of a confirmation token, is withheld too. Read and act results are left as they are.
    /// </summary>
    private static string RequestSafe(string text) => TokenLike().Replace(ClientSafe(text), "[withheld]");

    private bool IsActive(long clientId) => clients.List().Any(c => c.Id == clientId && c.RevokedAt is null);

    [GeneratedRegex(@"^[a-z_]{1,64}\z")]
    private static partial Regex ToolName();

    /// <summary>The audit method of a client's call: the tool name only when it is well formed (it comes from the client).</summary>
    private static string AuditMethod(string tool) => tool is not null && ToolName().IsMatch(tool) ? "companion." + tool : "companion.(unknown)";

    // ------------------------------------------------------------------ the control endpoint's checks

    /// <summary>The active client whose key this is (the control endpoint's auth); null for a wrong key or a revoked or unknown name.</summary>
    public CompanionClient? Authenticate(string name, string presentedKey) => clients.Authenticate(name, presentedKey);

    /// <summary>Whether this exact client row is still active (not revoked, not replaced by a later pairing of the name).</summary>
    public bool IsActive(CompanionClient client) => clients.List().Any(c => c.Id == client.Id && c.Name == client.Name && c.RevokedAt is null);

    // ------------------------------------------------------------------ calls

    /// <summary>One tool call of an authenticated client.</summary>
    public async Task<CompanionResult> CallAsync(CompanionClient client, string tool, JsonObject args, CancellationToken ct)
    {
        var name = client.Name;
        var actor = Actor.ExternalClient(name);
        var method = AuditMethod(tool);
        args = args.DeepClone().AsObject();
        CompanionResult Refused(string text)
        {
            audit.Record(actor, method, SafeParams(args), text);
            return new CompanionResult(text, true);
        }
        // A revoked client is refused even on a connection that is still open.
        if (!clients.List().Any(c => c.Id == client.Id && c.Name == name && c.RevokedAt is null))
            return Refused("refused: this client was revoked in the app; pair it again to use it");

        var needs = CompanionTools.LevelOf(tool, args);
        // A malformed name is never echoed into the audit (it comes from the client).
        if (needs is null) return Refused(method == "companion.(unknown)" ? "refused: that is not a tool the yawble companion offers" : $"refused: {tool} is not offered to the yawble companion");
        if (needs > client.Level)
            return Refused($"refused: {tool} needs the {CompanionLevels.Name(needs.Value)} level; this client was paired for {CompanionLevels.Name(client.Level)}");

        using var acting = Actor.Use(actor);
        try
        {
            var result = needs == CompanionLevel.Destructive ? await RequestAsync(client, tool, args, ct) : await ReadOrActAsync(client, tool, args, ct);
            return result with { Text = needs == CompanionLevel.Destructive ? RequestSafe(result.Text) : ClientSafe(result.Text) };
        }
        catch (Exception e) when (e is ArgumentException or RpcException or BusRefusal)
        {
            var raw = (e is RpcException r ? $"refused ({r.Code}): {r.Message}" : e is BusRefusal b ? $"refused ({b.Code}): {b.Message}" : e.Message);
            var text = needs == CompanionLevel.Destructive ? RequestSafe(raw) : ClientSafe(raw);
            if (needs != CompanionLevel.Read) audit.Record(actor, method, SafeParams(args), text.StartsWith("refused", StringComparison.Ordinal) ? text : "refused: " + text);
            return new CompanionResult(text, true);
        }
    }

    private async Task<CompanionResult> ReadOrActAsync(CompanionClient client, string tool, JsonObject args, CancellationToken ct)
    {
        var name = client.Name;
        switch (tool)
        {
            case "request_status":
            {
                var id = CompanionTools.Text(args, "id") ?? throw new ArgumentException("'id' is required");
                var s = StatusOf(client.Id, id) ?? throw new ArgumentException($"there is no request '{id}' of this client");
                return new CompanionResult(Describe(s), false);
            }
            case "backups_list":
            {
                var list = (await route("ops.backups", null, ct) as JsonArray ?? []).OfType<JsonObject>().ToList();
                if (list.Count == 0) return new CompanionResult("There are no backups yet.", false);
                return new CompanionResult(string.Join('\n', list.Select(b => $"- {b["name"]} ({b["reason"]}, {b["time"]}, {b["size"]} bytes)")), false);
            }
            case "tell" or "answer":
                foreach (var p in CompanionTools.ChatOnlyParameters) args.Remove(p); // never offered: no chat to attach from or wake
                break;
        }
        // byPerson: the person paired this client and granted its level; remote: reads keep to Cloud Link parity, and every
        // local-only tool would be refused here even if the level gate were bypassed. No conversation id: an external client
        // has no chat, so no watch is made for it and no chat attachment can be forwarded.
        var turn = new ConciergeTurn("external:" + name, byPerson: true, remote: true);
        var tools = new ConciergeTools(bus, store, route, () => null, null, _clock, actionsFor(turn, _book), remote: true, turn, Actor.ExternalClient(name));
        var (text, isError) = await tools.CallToolAsync(tool, args, ct);
        // Every act call is audited. The Concierge's action tools audit themselves (concierge.<tool>); the rest are its older
        // tools, which go to the app's operations without passing the dispatcher's audit, so they are recorded here.
        if (CompanionTools.Act.Contains(tool) && !ConciergeActions.ToolNames.Contains(tool))
            audit.Record(Actor.ExternalClient(name), "companion." + tool, SafeParams(args), isError ? "refused: " + ClientSafe(text) : "ok");
        return new CompanionResult(text, isError);
    }

    /// <summary>A destructive call: never run now. The exact plan becomes a request the window approves or denies.</summary>
    private async Task<CompanionResult> RequestAsync(CompanionClient client, string tool, JsonObject args, CancellationToken ct)
    {
        var name = client.Name;
        args.Remove("confirm"); // a client never supplies a yes
        foreach (var (key, value) in args)
            if (key != "value" && value is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > MaxArgumentChars)
                throw new ArgumentException($"refused: '{key}' is longer than 8 KB ({s.Length} characters); the person must be able to read in full what they approve, so shorten it");
        // Checked before the window is asked: a plan with a blank where a required value goes would only fail once approved.
        if (CompanionTools.MissingRequired(tool, args) is { } missing) throw new ArgumentException($"'{missing}' is required");
        lock (_gate)
        {
            Sweep();
            RefuseOverCap(client.Id);
        }
        var id = NewId("r");
        var conversation = $"external:{name}:{id}";
        var expires = Now + ActionLife;
        var prepared = await PrepareAsync(name, tool, args, conversation, ct);
        if (prepared.Immediate is { } immediate) return new CompanionResult(immediate, false);
        var request = new Request(id, client.Id, name, tool, args, RequestSafe(prepared.Plan), prepared.Token, prepared.Bound, conversation, expires);
        lock (_gate)
        {
            // Revoked while the plan was being made: nothing is stored.
            if (!IsActive(client.Id)) throw new ArgumentException("refused: this client was revoked in the app; pair it again to use it");
            // Checked again: other calls of this client may have stored theirs while this plan was made.
            Sweep();
            RefuseOverCap(client.Id);
            _requests[id] = request;
        }
        audit.Record(Actor.ExternalClient(name), "companion." + tool, SafeParams(args), $"awaiting the person's approval (request {id})");
        Changed();
        return new CompanionResult($"NOT DONE: waiting for the person's approval in the Yawble window (request {id}): {request.Plan}", false, id);
    }

    /// <summary>Under the gate: a client may have at most <see cref="MaxPendingPerClient"/> requests waiting.</summary>
    private void RefuseOverCap(long clientId)
    {
        if (_requests.Values.Count(r => r.ClientId == clientId && r.State == ActionState.Pending) >= MaxPendingPerClient)
            throw new ArgumentException($"refused: this client already has {MaxPendingPerClient} requests waiting for the person; wait until they are decided");
    }

    /// <summary>The plan (and, for a self-confirming tool, its private token; for a SHA-bound one, the commit shown).</summary>
    private async Task<Prepared> PrepareAsync(string name, string tool, JsonObject args, string conversation, CancellationToken ct)
    {
        if (CompanionTools.SelfConfirming.Contains(tool))
        {
            // The Concierge's own confirm flow makes the exact list and its token, in the companion's book. Local: the list is
            // only looked at here; the yes is the person's click.
            var planTurn = new ConciergeTurn(conversation, byPerson: true, remote: false);
            var tools = new ConciergeTools(bus, store, route, () => null, null, _clock, actionsFor(planTurn, _book), remote: false, planTurn, Actor.ExternalClient(name));
            var (text, isError) = await tools.CallToolAsync(tool, args, ct);
            if (isError) throw new ArgumentException(text);
            if (text.StartsWith("nothing to do", StringComparison.Ordinal)) return new Prepared(text, "", null, null);
            var issued = _book.IssuedIn(planTurn);
            if (issued.Count != 1) throw new ArgumentException($"refused: {tool} did not make a list to approve");
            return new Prepared(null, issued[0].Plan, issued[0].Token, null);
        }
        var plan = CompanionTools.Describe(tool, args);
        string? bound = null;
        switch (tool)
        {
            case "git_merge_team":
            {
                var (more, nothing, sha) = await MergePlanAsync(args, ct);
                if (nothing) return new Prepared("nothing to do: " + more, "", null, null);
                plan += " " + more;
                bound = sha ?? "unknown";
                break;
            }
            case "git_push_branch" or "git_sync_branch":
            {
                var sha = await BranchTipAsync(args, ct);
                plan += sha is not null ? $" Its tip now: {sha}."
                    : $" Its tip is not pinned: whatever it holds when you approve is {(tool == "git_push_branch" ? "pushed" : "synced")}.";
                bound = sha ?? "unknown";
                break;
            }
            case "backup_restore":
            {
                var backup = CompanionTools.Text(args, "backup") ?? throw new ArgumentException("'backup' is required");
                var list = await route("ops.backups", null, ct) as JsonArray ?? [];
                if (!list.OfType<JsonObject>().Any(b => b["name"]?.GetValue<string>() == backup))
                    throw new ArgumentException($"there is no backup '{backup}' (backups_list shows them)");
                break;
            }
        }
        return new Prepared(null, plan, null, bound);
    }

    private async Task<TeamSummary> TeamAsync(JsonObject args, CancellationToken ct)
    {
        var name = CompanionTools.Text(args, "team") ?? throw new ArgumentException("'team' is required");
        var teams = await bus.TeamsAsync(ct);
        return teams.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
               ?? throw new ArgumentException($"there is no team '{name}'. Teams: {string.Join(", ", teams.Select(t => t.Name))}");
    }

    private async Task<JsonObject> GitStatusAsync(JsonObject args, CancellationToken ct)
    {
        var team = await TeamAsync(args, ct);
        return await route("git.status", new JsonObject { ["teamId"] = team.Id, ["repo"] = CompanionTools.Text(args, "repo") }, ct) as JsonObject ?? [];
    }

    /// <summary>What merging the team branch would do now (git.status), whether there is nothing to merge, and the team branch's commit.</summary>
    private async Task<(string Text, bool Nothing, string? Sha)> MergePlanAsync(JsonObject args, CancellationToken ct)
    {
        var team = await TeamAsync(args, ct);
        var s = await GitStatusAsync(args, ct);
        string S(string k) => s[k]?.ToString() ?? "?";
        var branch = S("teamBranch");
        var into = S("baseBranch");
        var sha = s["teamSha"]?.ToString();
        return S("merge") switch
        {
            "none" => ($"{team.Name} has no team branch yet.", true, sha),
            "up-to-date" => ($"{into} already has everything on {branch}.", true, sha),
            "conflict" => ($"{branch} does not merge cleanly into {into} (conflicts in {string.Join(", ", (s["conflictFiles"] as JsonArray ?? []).Select(f => f?.ToString()))}); nothing would change. Ask the team to bring it current first.", true, sha),
            var how => ($"Now: {branch} at {sha ?? "?"} is {S("teamAheadOfBase")} commit(s) ahead of {into} and {S("teamBehindBase")} behind; it lands as {(how == "fast-forward" ? "a fast-forward" : "a merge commit")}" +
                        (s["origin"] is JsonValue ? " and is pushed to origin." : "."), false, sha),
        };
    }

    /// <summary>The tip of a team-made branch (a member's or the team branch) as git.status reports it; null when it does not list it.</summary>
    private async Task<string?> BranchTipAsync(JsonObject args, CancellationToken ct)
    {
        var branch = CompanionTools.Text(args, "branch");
        if (branch is null) return null;
        JsonObject s;
        try { s = await GitStatusAsync(args, ct); }
        catch (RpcException) { return null; } // no repository to read: the run itself says why
        if (s["teamBranch"]?.ToString() == branch) return s["teamSha"]?.ToString();
        return (s["members"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(m => m["branch"]?.ToString() == branch)?["sha"]?.ToString();
    }

    /// <summary>The commit a SHA-bound tool would act on now.</summary>
    private async Task<string> BoundNowAsync(Request r, CancellationToken ct) => r.Tool == "git_merge_team"
        ? (await MergePlanAsync(r.Args, ct)).Sha ?? "unknown"
        : await BranchTipAsync(r.Args, ct) ?? "unknown";

    // ------------------------------------------------------------------ the window's decisions

    private static string RefuseUnlessWindow() =>
        Actor.Current?.Via == Actor.App ? Actor.Current.User ?? Actor.LocalUser
            : throw new RpcException("forbidden", "only the person at the app's window on this computer can decide this");

    /// <summary>
    /// The person's Approve or Deny of a request. Runs it at most once, exactly as stored. An approval answers Running at once
    /// (the window's connection serves one call at a time, so it must not wait for the run): the run goes on by itself, on its
    /// own task with this service's lifetime token (never the window call's), and <see cref="ChangedEvent"/> announces its end.
    /// </summary>
    /// <param name="ct">The window call's token: it only stops a decision not yet made, never a run.</param>
    public Task<ActionStatus> DecideActionAsync(string id, bool approve, CancellationToken ct)
    {
        var approver = RefuseUnlessWindow();
        ct.ThrowIfCancellationRequested();
        ActionStatus answer;
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0) throw new RpcException("unavailable", "the app is closing; nothing was run");
            Sweep();
            if (!_requests.TryGetValue(id, out var r)) throw new RpcException("not_found", $"there is no request '{id}'");
            if (r.State != ActionState.Pending) return Task.FromResult(StatusOf(r)); // decided already, or running now
            if (!IsActive(r.ClientId))
            {
                Finish(r, ActionState.Denied, "voided: the client was revoked");
                audit.Record(Actor.ExternalClient(r.Client), "companion." + r.Tool, SafeParams(r.Args), $"voided (request {id}): the client was revoked");
            }
            else if (!approve)
            {
                Finish(r, ActionState.Denied, "denied by the person");
                audit.Record(Actor.ExternalClient(r.Client), "companion." + r.Tool, SafeParams(r.Args), $"denied by {approver} (request {id})");
            }
            else
            {
                r.State = ActionState.Running;
                var life = _life.Token;
                _runs[id] = Task.Run(() => RunApprovedAsync(r, approver, life), CancellationToken.None);
            }
            answer = StatusOf(r);
        }
        Changed();
        return Task.FromResult(answer);
    }

    /// <summary>The window's read of one request (companion.actionStatus): decides and runs nothing; null for an unknown id.</summary>
    public ActionStatus? StatusForWindow(string id)
    {
        RefuseUnlessWindow();
        lock (_gate)
        {
            Sweep();
            return _requests.TryGetValue(id, out var r) ? StatusOf(r) : null;
        }
    }

    /// <summary>For tests: the approved run of this request (kept while the request is), which ends with its final status.</summary>
    internal Task<ActionStatus>? RunOf(string id)
    {
        lock (_gate) return _runs.GetValueOrDefault(id);
    }

    /// <summary>
    /// An approved request's run. Never throws: any exception (a cancellation included) finishes the request Failed, and every
    /// end is audited and announced.
    /// </summary>
    private async Task<ActionStatus> RunApprovedAsync(Request r, string approver, CancellationToken ct)
    {
        var id = r.Id;
        var actor = Actor.ExternalClient(r.Client) with { ApprovedBy = approver };
        var turn = new ConciergeTurn(r.Conversation, byPerson: true, remote: false);
        var state = ActionState.Failed;
        var result = "failed: the run stopped before it finished";
        Request? next = null;
        var reallyChanged = false;
        try
        {
            using (Actor.Use(actor))
            {
                if (ShaBound.Contains(r.Tool) && await BoundNowAsync(r, ct) != r.Bound)
                {
                    // What was shown moved since: approving the old plan must not act on the new commit. Shown again instead.
                    var prepared = await PrepareAsync(r.Client, r.Tool, r.Args, r.Conversation, ct);
                    if (prepared.Immediate is { } immediate)
                    {
                        state = ActionState.Done;
                        result = RequestSafe(immediate);
                    }
                    else
                    {
                        next = new Request(NewId("r"), r.ClientId, r.Client, r.Tool, r.Args.DeepClone().AsObject(), RequestSafe(prepared.Plan), prepared.Token, prepared.Bound, r.Conversation, Now + ActionLife);
                        state = ActionState.Changed;
                        reallyChanged = true;
                    }
                }
                else
                {
                    var (text, isError) = await ExecuteAsync(r, turn, actor, ct);
                    result = RequestSafe(text);
                    state = isError ? ActionState.Failed : ActionState.Done;
                    // The run issued a new list (it changed since it was shown, or a second step needs its own yes): a new request.
                    if (_book.IssuedIn(turn) is [var issued, ..])
                    {
                        next = new Request(NewId("r"), r.ClientId, r.Client, r.Tool, r.Args.DeepClone().AsObject(), RequestSafe(issued.Plan), issued.Token, null, r.Conversation, Now + ActionLife);
                        state = ActionState.Changed;
                        // Redeeming a shown list gave a new one: it changed. Without a token, this was the first look of a
                        // tool whose second step needs its own yes (team_env_set replace, doctor_fix): nothing changed.
                        reallyChanged = r.Token is not null;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            next = null;
            state = ActionState.Failed;
            // Stopped by the app closing, mid-run: some of it may have happened.
            result = ct.IsCancellationRequested ? CancelledWhileRunning : "failed: cancelled before it finished";
        }
        catch (Exception e)
        {
            next = null;
            state = ActionState.Failed;
            result = RequestSafe("failed: " + (e is RpcException x ? $"refused ({x.Code}): {x.Message}" : e.Message));
        }

        ActionStatus final;
        lock (_gate)
        {
            try
            {
                if (next is not null)
                {
                    // Attached only while the very client row that asked is active (a re-paired name is another client).
                    bool active;
                    try { active = IsActive(r.ClientId); }
                    catch (Exception) { active = false; } // cannot tell (a database error): treated as voided
                    if (active)
                    {
                        _requests[next.Id] = next;
                        r.NextId = next.Id;
                        result = reallyChanged ? $"the list changed before approval; request {next.Id} shows the new list"
                            : $"needs another approval; request {next.Id} shows exactly what it will do";
                    }
                    else
                    {
                        next = null;
                        state = ActionState.Denied;
                        result = "voided: the client was revoked, or whether it still is could not be checked";
                    }
                }
            }
            finally
            {
                // Always: a request is never left Running.
                Finish(r, state, result);
                final = StatusOf(r);
            }
        }
        try
        {
            audit.Record(actor, "companion." + r.Tool, SafeParams(r.Args), $"approved by {approver} (request {id}): " + state switch
            {
                ActionState.Changed => reallyChanged ? $"the list changed; new request {r.NextId}" : $"needs another approval; new request {r.NextId}",
                ActionState.Done => "done",
                ActionState.Denied => "voided: the client was revoked",
                _ => result.StartsWith("failed", StringComparison.Ordinal) ? result : "failed: " + result,
            });
        }
        catch (Exception e)
        {
            // The request is finished either way; a run on its own task must never fault unobserved.
            Console.Error.WriteLine($"companion: the audit of request {id} could not be written ({e.GetType().Name})");
        }
        Changed();
        return final;
    }

    /// <summary>Cancels approved runs still under way (each ends Failed, audited) and waits for them; later approvals are refused.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        Task[] runs;
        lock (_gate) runs = [.. _runs.Values];
        await _life.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(runs).ConfigureAwait(false); // never faults: each run catches everything
    }

    private async Task<(string Text, bool IsError)> ExecuteAsync(Request r, ConciergeTurn turn, Actor actor, CancellationToken ct)
    {
        switch (r.Tool)
        {
            case "git_merge_team":
            {
                var team = await TeamAsync(r.Args, ct);
                var m = await route("git.merge", new JsonObject { ["teamId"] = team.Id, ["repo"] = CompanionTools.Text(r.Args, "repo") }, ct) as JsonObject ?? [];
                var outcome = m["outcome"]?.ToString() ?? "";
                var message = m["message"]?.ToString() ?? "";
                return outcome is "fast-forward" or "merged" or "up-to-date" ? ($"ok: {message}", false) : ($"not done ({outcome}): {message}", true);
            }
            case "backup_restore":
            {
                var backup = CompanionTools.Text(r.Args, "backup") ?? throw new ArgumentException("'backup' is required");
                await route("ops.restore", new JsonObject { ["backup"] = backup, ["confirm"] = true }, ct);
                return ($"ok: backup {backup} is staged; it replaces the app's data when the app restarts.", false);
            }
            default:
            {
                var args = r.Args.DeepClone().AsObject();
                if (r.Token is { } token) args["confirm"] = token;
                var tools = new ConciergeTools(bus, store, route, () => null, null, _clock, actionsFor(turn, _book), remote: false, turn, actor);
                return await tools.CallToolAsync(r.Tool, args, ct);
            }
        }
    }

    private void Finish(Request r, ActionState state, string? result)
    {
        r.State = state;
        r.Result = result;
        r.FinishedAt = Now;
        r.Finished.TrySetResult();
    }

    /// <summary>Marks what has expired; forgets finished requests after a while. Under the gate.</summary>
    private void Sweep()
    {
        var now = Now;
        foreach (var r in _requests.Values.Where(r => r.State == ActionState.Pending && r.Expires <= now).ToList())
            Finish(r, ActionState.Expired, "expired: nobody approved it in time");
        foreach (var r in _requests.Values.Where(r => r.FinishedAt is { } at && at + KeepFinished <= now).ToList())
        {
            _requests.Remove(r.Id);
            _runs.Remove(r.Id);
        }
        foreach (var p in _pairings.Values.Where(p => p.Outcome is null && p.Expires <= now).ToList())
        {
            p.Outcome = new PairingOutcome(false, null, null, "expired: the pairing was not approved within five minutes");
            p.Decided.TrySetResult();
        }
        // Approved, but the waiting client never collected its key: undone, so the key (dropped here) can never be used and the name is free.
        foreach (var p in _pairings.Values.Where(p => p.Outcome is { Approved: true } && !p.Handed && p.Expires + CollectGrace <= now).ToList())
        {
            p.Outcome = new PairingOutcome(false, null, null, "expired: the approved pairing was not collected");
            // By the row this pairing made, never by name: a later client of the same name is not touched.
            if (p.ClientId is { } clientId && clients.RevokeId(clientId))
            {
                audit.Record(new Actor(Actor.Host, Actor.LocalUser), "companion.revoke", new JsonObject { ["name"] = p.Name }, "undone: the approved pairing's key was not collected");
                var name = p.Name;
                // Raised off the lock (a handler may call back in).
                ThreadPool.QueueUserWorkItem(_ => Revoked?.Invoke(name));
            }
        }
        foreach (var p in _pairings.Values.Where(p => p.Outcome is not null && p.Expires + KeepFinished <= now).ToList()) _pairings.Remove(p.Id);
    }

    private static ActionStatus StatusOf(Request r) => new(r.Id, r.State, r.Plan, r.Result, r.NextId);

    /// <summary>By the client row's id, never its name: a later client of the same name is another client.</summary>
    private ActionStatus? StatusOf(long clientId, string id)
    {
        lock (_gate)
        {
            Sweep();
            return _requests.TryGetValue(id, out var r) && r.ClientId == clientId ? StatusOf(r) : null;
        }
    }

    /// <summary>Where a request of this client stands; null for another client's (a re-paired name's included) or an unknown id.</summary>
    public ActionStatus? Status(CompanionClient client, string id) => StatusOf(client.Id, id);

    /// <summary>Waits up to <paramref name="max"/> for a request of this client to be decided and, if approved, to finish running.</summary>
    public async Task<ActionStatus?> WaitAsync(CompanionClient client, string id, TimeSpan max, CancellationToken ct)
    {
        Request? r;
        lock (_gate) r = _requests.GetValueOrDefault(id) is { } found && found.ClientId == client.Id ? found : null;
        if (r is null) return null;
        var until = DateTime.UtcNow + max;
        while (StatusOf(client.Id, id) is { State: ActionState.Pending or ActionState.Running } && DateTime.UtcNow < until)
        {
            var left = until - DateTime.UtcNow;
            await Task.WhenAny(r.Finished.Task, Task.Delay(left < TimeSpan.FromMilliseconds(250) ? left : TimeSpan.FromMilliseconds(250), ct));
            ct.ThrowIfCancellationRequested();
        }
        return StatusOf(client.Id, id);
    }

    /// <summary>For tests only: whether a pairing still holds a plain key.</summary>
    internal bool HoldsKey(string id)
    {
        lock (_gate)
        {
            Sweep();
            return _pairings.GetValueOrDefault(id)?.Outcome?.Key is not null;
        }
    }

    /// <summary>For tests only: the token a request will redeem (never shown to any client or the window).</summary>
    internal string? TokenOf(string id)
    {
        lock (_gate) return _requests.GetValueOrDefault(id)?.Token;
    }

    private static string Describe(ActionStatus s) =>
        $"request {s.Id}: {s.State.ToString().ToLowerInvariant()}" +
        (s.State == ActionState.Pending ? " (waiting for the person's approval in the Yawble window)" : s.State == ActionState.Running ? " (approved; running now)" : "") +
        $"\nplan: {s.Plan}" + (s.Result is { } res ? $"\nresult: {res}" : "") + (s.NextId is { } n ? $"\nthe list changed: the new request is {n}" : "");

    // ------------------------------------------------------------------ pairing

    /// <summary>A client asks to pair: the window shows the name, level and code; the client shows the same code.</summary>
    public PairingTicket BeginPairing(string name, CompanionLevel level)
    {
        if (!CompanionClientStore.ValidName(name)) throw new RpcException("bad_params", "a client name is lowercase letters, digits and '-', 1 to 32 characters");
        if (!Enum.IsDefined(level)) throw new RpcException("bad_params", "the level is read, act or destructive");
        Pairing p;
        lock (_gate)
        {
            Sweep();
            var pending = _pairings.Values.Where(x => x.Outcome is null).ToList();
            if (pending.Any(x => x.Name == name) || clients.List().Any(c => c.Name == name && c.RevokedAt is null))
                throw new RpcException("exists", $"a client named '{name}' is already paired or waiting; revoke it in the app's Settings first");
            if (pending.Count >= MaxPendingPairings) throw new RpcException("busy", "three pairings are already waiting for the person; try again in a few minutes");
            var code = new string(RandomNumberGenerator.GetItems<char>(CodeAlphabet, 6));
            p = new Pairing(NewId("p"), name, level, code, Now + PairingLife);
            _pairings[p.Id] = p;
        }
        Changed();
        return new PairingTicket(p.Id, p.Code, p.Expires);
    }

    /// <summary>Waits for the person's decision (or the pairing's expiry). The key is handed to the first waiter only, then forgotten.</summary>
    public async Task<PairingOutcome> WaitPairingAsync(string id, CancellationToken ct)
    {
        Pairing? p;
        lock (_gate) p = _pairings.GetValueOrDefault(id);
        if (p is null) return new PairingOutcome(false, null, null, "unknown pairing");
        while (true)
        {
            lock (_gate)
            {
                Sweep();
                if (p.Outcome is { } o)
                {
                    if (p.Handed) return new PairingOutcome(false, null, null, "already answered");
                    p.Handed = true;
                    p.Outcome = o with { Key = null }; // the plain key now exists only with the client
                    return o;
                }
            }
            // Re-checked at least every quarter second, so an expiry on the clock is noticed.
            await Task.WhenAny(p.Decided.Task, Task.Delay(TimeSpan.FromMilliseconds(250), ct));
            ct.ThrowIfCancellationRequested();
        }
    }

    /// <summary>
    /// The control endpoint took this approved pairing's key but could not write it to its client: nobody holds the key, so
    /// the row the approval made is revoked (by its id, never by name), audited and announced, and the name may pair again.
    /// </summary>
    public bool PairingUndelivered(string id)
    {
        string name;
        lock (_gate)
        {
            if (!_pairings.TryGetValue(id, out var p) || p.Outcome is not { Approved: true } || !p.Handed || p.ClientId is not { } clientId) return false;
            p.ClientId = null;
            p.Outcome = new PairingOutcome(false, null, null, "undone: the key could not be delivered to the client");
            if (!clients.RevokeId(clientId)) return false;
            audit.Record(new Actor(Actor.Host, Actor.LocalUser), "companion.revoke", new JsonObject { ["name"] = p.Name }, "undone: the approved pairing's key could not be delivered");
            name = p.Name;
        }
        Revoked?.Invoke(name);
        Changed();
        return true;
    }

    /// <summary>The person's decision on a pairing, from the window. The level may only be lowered.</summary>
    public void DecidePairing(string id, bool approve, CompanionLevel? level)
    {
        var by = RefuseUnlessWindow();
        var decider = Actor.Current ?? Actor.Window;
        lock (_gate)
        {
            Sweep();
            if (!_pairings.TryGetValue(id, out var p) || p.Outcome is not null) throw new RpcException("not_found", $"there is no pairing '{id}' waiting");
            if (!approve) p.Outcome = new PairingOutcome(false, null, null, "denied by the person");
            else
            {
                var granted = level ?? p.Level;
                if (!Enum.IsDefined(granted) || granted > p.Level) throw new RpcException("bad_params", "a pairing's level can only be lowered, never raised");
                try
                {
                    var c = clients.Add(p.Name, granted, by, out var key);
                    Supersede(p.Name);
                    p.ClientId = c.Id;
                    p.Outcome = new PairingOutcome(true, key, c.Level, "approved");
                }
                catch (InvalidOperationException e) { throw new RpcException("exists", e.Message); }
            }
            audit.Record(decider, "companion.pair", new JsonObject { ["name"] = p.Name },
                approve ? $"approved ({CompanionLevels.Name(p.Outcome.Level!.Value)})" : "denied");
            p.Decided.TrySetResult();
        }
        Changed();
    }

    /// <summary>
    /// An approved pairing of this name nobody collected is settled now (its key dropped), when the name is revoked or paired
    /// again: its client row may be replaced (even under the same id), so its later sweep must not revoke anything. Under the gate.
    /// </summary>
    private void Supersede(string name)
    {
        foreach (var old in _pairings.Values.Where(x => x.Name == name && x.Outcome is { Approved: true } && !x.Handed))
        {
            old.Outcome = new PairingOutcome(false, null, null, "superseded: the client was revoked or paired again");
            old.ClientId = null;
        }
    }

    // ------------------------------------------------------------------ the window's view

    /// <summary>{clients, pairings (pending), actions (pending or running)}: never a key, a hash or a token.</summary>
    public JsonObject ListForWindow()
    {
        var list = new JsonArray();
        foreach (var c in clients.List())
            list.Add(new JsonObject
            {
                ["name"] = c.Name, ["level"] = CompanionLevels.Name(c.Level), ["pairedAt"] = Iso(c.PairedAt), ["pairedBy"] = c.PairedBy,
                ["lastUsedAt"] = c.LastUsedAt is { } u ? Iso(u) : null, ["revokedAt"] = c.RevokedAt is { } r ? Iso(r) : null,
            });
        var pairings = new JsonArray();
        var actions = new JsonArray();
        lock (_gate)
        {
            Sweep();
            foreach (var p in _pairings.Values.Where(p => p.Outcome is null).OrderBy(p => p.Expires))
                pairings.Add(new JsonObject { ["id"] = p.Id, ["name"] = p.Name, ["level"] = CompanionLevels.Name(p.Level), ["code"] = p.Code, ["expires"] = Iso(p.Expires) });
            foreach (var r in _requests.Values.Where(r => r.State is ActionState.Pending or ActionState.Running).OrderBy(r => r.Expires))
                actions.Add(new JsonObject
                {
                    ["id"] = r.Id, ["client"] = r.Client, ["tool"] = r.Tool, ["plan"] = r.Plan, ["expires"] = Iso(r.Expires),
                    ["state"] = r.State == ActionState.Running ? "running" : "pending",
                });
        }
        return new JsonObject { ["clients"] = list, ["pairings"] = pairings, ["actions"] = actions };
    }

    /// <summary>Pending pairings plus pending action requests (the window's Needs You); a running one waits for nobody.</summary>
    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                Sweep();
                return _pairings.Values.Count(p => p.Outcome is null) + _requests.Values.Count(r => r.State == ActionState.Pending);
            }
        }
    }

    /// <summary>Revokes a client and voids its pending requests (by client row id: every request of a row no longer active).</summary>
    public bool Revoke(string name)
    {
        var revoked = clients.Revoke(name);
        var active = clients.List().Where(c => c.RevokedAt is null).Select(c => c.Id).ToHashSet();
        lock (_gate)
        {
            Supersede(name);
            foreach (var r in _requests.Values.Where(r => r.State == ActionState.Pending && !active.Contains(r.ClientId)).ToList())
                Finish(r, ActionState.Denied, "voided: the client was revoked");
        }
        if (revoked)
        {
            audit.Record(Actor.Current ?? Actor.Window, "companion.revoke", new JsonObject { ["name"] = name }, "ok");
            Revoked?.Invoke(name);
        }
        Changed();
        return revoked;
    }

    private static string Iso(DateTimeOffset t) => t.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    /// <summary>The identifying parameters only (never an env value, a title, a body or an instruction).</summary>
    private static JsonObject SafeParams(JsonObject args)
    {
        var o = new JsonObject();
        foreach (var k in new[] { "team", "item", "card", "lane", "repo", "branch", "name", "member", "pr", "fix", "workflow", "backup", "id" })
            if (args[k] is JsonValue v) o[k] = v.ToString();
        return o;
    }
}
