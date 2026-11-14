using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Ipc;
using Yawble.Core.Provenance;
using Yawble.Core.Teams;

namespace Yawble.Core.Concierge;

/// <summary>
/// One Concierge turn, as its tools see it: the conversation it belongs to, whether a message from the person started it
/// (a watch's follow-up review has none), and whether that message came through the Cloud Link. Captured when the turn
/// begins; the tool calls arrive later through the gateway, where no caller is known.
/// </summary>
public sealed class ConciergeTurn(string conversationId, bool byPerson, bool remote)
{
    private static long _last;

    public string ConversationId { get; } = conversationId;
    public bool ByPerson { get; } = byPerson;
    public bool Remote { get; } = remote;
    /// <summary>Increases with every turn made: a later turn has a larger one.</summary>
    public long Seq { get; } = Interlocked.Increment(ref _last);

    /// <summary>A stand-in that can never say yes (the app's template actions, before any turn).</summary>
    public static ConciergeTurn None => new("", byPerson: false, remote: true);
}

/// <summary>
/// What the person said yes to. A destructive tool first returns the exact list and a one-use token; it runs only when
/// called again with that token, for the very same list, in a LATER turn of the SAME conversation that a message from the
/// person started (the person's yes is a new chat message, and a turn is one message's answer; a watch's review turn has
/// no message from them, so it cannot say yes for them), and not over the Cloud Link. If the list changed meanwhile, or
/// the token is tried for another tool or in another conversation, it is void and the new list is shown instead.
/// A token lives <see cref="Life"/>. Shared by every turn of the app (a yes arrives in another turn than the list).
/// </summary>
public sealed class ConfirmationBook(TimeProvider? clock = null)
{
    public static readonly TimeSpan Life = TimeSpan.FromMinutes(10);
    public enum Outcome { Ok, Unknown, SameTurn, Changed, OtherConversation, NotThePerson, Remote }

    private sealed record Pending(string Tool, string Key, ConciergeTurn Turn, DateTimeOffset Expires, string Plan = "");
    private readonly ConcurrentDictionary<string, Pending> _pending = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>A token this book issued, with the tool and the exact list it was shown for.</summary>
    public sealed record IssuedConfirmation(string Token, string Tool, string Plan);

    /// <param name="plan">The exact list shown to the person for this token (kept so a caller can show it again).</param>
    public string Issue(ConciergeTurn turn, string tool, string key, string? plan = null)
    {
        var now = _clock.GetUtcNow();
        foreach (var old in _pending.Where(p => p.Value.Expires <= now).Select(p => p.Key).ToList()) _pending.TryRemove(old, out _);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        _pending[token] = new Pending(tool, key, turn, now + Life, plan ?? "");
        return token;
    }

    /// <summary>The tokens issued in <paramref name="turn"/> (that very turn) that are still pending and not expired.</summary>
    public IReadOnlyList<IssuedConfirmation> IssuedIn(ConciergeTurn turn)
    {
        var now = _clock.GetUtcNow();
        return _pending.Where(p => ReferenceEquals(p.Value.Turn, turn) && p.Value.Expires > now)
            .Select(p => new IssuedConfirmation(p.Key, p.Value.Tool, p.Value.Plan)).ToList();
    }

    public Outcome Redeem(ConciergeTurn turn, string tool, string key, string token)
    {
        if (!_pending.TryGetValue(token, out var p)) return Outcome.Unknown;
        if (p.Expires <= _clock.GetUtcNow()) { _pending.TryRemove(token, out _); return Outcome.Unknown; }
        if (p.Turn.ConversationId != turn.ConversationId) { _pending.TryRemove(token, out _); return Outcome.OtherConversation; }
        if (p.Tool != tool || p.Key != key) { _pending.TryRemove(token, out _); return Outcome.Changed; }
        if (ReferenceEquals(p.Turn, turn) || turn.Seq <= p.Turn.Seq) return Outcome.SameTurn;
        // Kept in these two cases: the person can still say yes in their own next message at the computer.
        if (!turn.ByPerson) return Outcome.NotThePerson;
        if (turn.Remote) return Outcome.Remote;
        return _pending.TryRemove(token, out _) ? Outcome.Ok : Outcome.Unknown;
    }
}

/// <summary>Runs the person's gh on the host (signed in as they are). Tests pass a fake instead.</summary>
public static class HostGh
{
    public static async Task<GitResult> RunAsync(string workingDirectory, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("gh") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, WorkingDirectory = workingDirectory };
        psi.Environment["GH_PROMPT_DISABLED"] = "1";
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi) ?? throw new GitException("gh could not be started");
            p.StandardInput.Close();
            var o = p.StandardOutput.ReadToEndAsync(ct);
            var e = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            return new GitResult(p.ExitCode, (await o).Trim(), (await e).Trim());
        }
        catch (System.ComponentModel.Win32Exception) { return new GitResult(-1, "", "gh is not installed on this computer"); }
    }
}

/// <summary>
/// The Concierge's ACTION tools, for the person, who asked or accepted a recommendation: backlog and board
/// actions, git actions on a team's repository through the host, and team settings. Kept in its own file; ConciergeTools only
/// offers and routes. Anything destructive or hard to undo shows its exact list first and needs the person's yes (<see cref="ConfirmationBook"/>).
/// Output never carries a secret: env values are never read back or echoed, remote URLs are redacted.
/// </summary>
public sealed class ConciergeActions(
    BusEngine bus,
    Func<string, JsonObject?, CancellationToken, Task<JsonNode?>> ui,
    Func<string, JsonObject?, CancellationToken, Task<JsonNode?>> board,
    ConciergeGit git,
    Func<TeamSummary, CancellationToken, Task<IReadOnlyList<ActionRepo>>> repos,
    ConfirmationBook book,
    Func<string, IReadOnlyList<string>, CancellationToken, Task<GitResult>>? gh = null,
    AuditLog? audit = null,
    ConciergeTurn? turn = null)
{
    /// <summary>The turn these tools serve: its conversation, whether the person started it, and whether over the Cloud Link.</summary>
    private readonly ConciergeTurn _turn = turn ?? ConciergeTurn.None;

    /// <summary>The book the lists and the person's yes are kept in.</summary>
    private readonly ConfirmationBook _book = book;

    /// <summary>The same actions for a new turn (the person's yes is a later turn than the list); with <paramref name="book"/>,
    /// bound to that book instead of these actions' own (the companion keeps its own, so neither accepts the other's tokens).</summary>
    public ConciergeActions ForTurn(ConciergeTurn turn, ConfirmationBook? book = null) => new(bus, ui, board, git, repos, book ?? _book, gh, audit, turn);

    /// <summary>
    /// What a turn started through the Cloud Link may not do (git deletes and anything hard to undo stay on
    /// the computer). Remote viewers keep the read tools and the backlog and board actions.
    /// </summary>
    public static readonly IReadOnlyList<string> LocalOnly =
    [
        "git_branches_delete", "git_prune", "git_push_branch", "git_sync_branch", "git_pr_open", "git_pr_merge",
        "team_archive", "team_delete", "team_env_set", "doctor_fix",
    ];

    /// <summary>The refusal a turn not started at this computer's window gets, naming the tool and why.</summary>
    public static string RemoteRefusal(string tool) =>
        $"refused: {tool} is only for the person at this computer, not over the Cloud Link (this conversation's message did not come from the app's window here: " +
        "it came through the Cloud Link, or from somewhere not known to be the person at the computer). " +
        "Nothing was changed and no list was made. Tell the person they can do it on the computer itself; from here you can still read everything and use the backlog and board actions.";

    /// <summary>The refusal a turn the person did not start (a watch's review of updates) gets for every action.</summary>
    public static string ReviewRefusal(string tool) =>
        $"refused: {tool} was not run: this turn was not started by a message from the person (it reviews updates that arrived), and actions run only when the person asks. " +
        "Nothing was changed. Tell them what you found and recommend the next step; they can ask for it in their next message.";

    public static readonly IReadOnlyList<string> ToolNames =
    [
        "backlog_edit", "backlog_ready", "backlog_dispatch", "backlog_archive", "backlog_restore", "board_move", "board_comment",
        "git_branches_delete", "git_prune", "git_push_branch", "git_sync_branch", "git_pr_open", "git_pr_merge",
        "team_archive", "team_unarchive", "team_delete", "team_env_set", "team_agent_change", "doctor_fix",
    ];

    /// <summary>Null when <paramref name="name"/> is not one of these tools.</summary>
    public async Task<string?> TryCallAsync(string name, JsonObject args, CancellationToken ct)
    {
        if (!ToolNames.Contains(name)) return null;
        string outcome = "ok";
        try
        {
            // Every action needs the person's own message; the local-only ones also need it to come from this computer's window.
            if (!_turn.ByPerson) throw new ArgumentException(ReviewRefusal(name));
            if (_turn.Remote && LocalOnly.Contains(name)) throw new ArgumentException(RemoteRefusal(name));
            var text = await CallAsync(name, args, ct);
            outcome = text.StartsWith("NOT DONE", StringComparison.Ordinal) ? "awaiting the person's yes" : "ok";
            return text;
        }
        catch (Exception e) when (e is ArgumentException or RpcException or BusRefusal)
        {
            outcome = "refused: " + e.Message;
            throw;
        }
        finally
        {
            audit?.Record(Actor.Current ?? new Actor(Actor.Concierge, Actor.LocalUser), "concierge." + name, SafeParams(args), outcome);
        }
    }

    /// <summary>The identifying parameters only (never an env value).</summary>
    private static JsonObject SafeParams(JsonObject args)
    {
        var o = new JsonObject();
        foreach (var k in new[] { "team", "item", "card", "lane", "repo", "branch", "name", "member", "pr", "fix" })
            if (args[k] is JsonValue v) o[k] = v.ToString();
        return o;
    }

    private async Task<string> CallAsync(string name, JsonObject args, CancellationToken ct)
    {
        switch (name)
        {
            case "backlog_edit":
            {
                var p = new JsonObject { ["id"] = ItemId(args), ["title"] = Optional(args, "title"), ["body"] = Optional(args, "body"),
                    ["team"] = Optional(args, "team") is { } t ? (await TeamAsync(t, ct)).Name : null };
                if (p["title"] is null && p["body"] is null && p["team"] is null) throw new ArgumentException("give a new title, body or team");
                return $"ok: edited {Label(await board("backlog.edit", p, ct))}.";
            }
            case "backlog_ready":
                return $"ok: {Label(await board("backlog.edit", new JsonObject { ["id"] = ItemId(args), ["state"] = "ready" }, ct))} is ready to dispatch.";
            case "backlog_archive":
                return $"ok: archived {Label(await board("backlog.archive", new JsonObject { ["id"] = ItemId(args) }, ct))} (backlog_restore brings it back).";
            case "backlog_restore":
                return $"ok: restored {Label(await board("backlog.restore", new JsonObject { ["id"] = ItemId(args) }, ct))}.";
            case "backlog_dispatch":
            {
                var id = ItemId(args);
                if (Optional(args, "new_team") is { } fresh)
                {
                    var n = await board("backlog.dispatchNew", new JsonObject { ["id"] = id, ["name"] = fresh, ["managerCli"] = Optional(args, "manager_cli") ?? "claude",
                        ["repoPath"] = Optional(args, "repo_path"), ["repoUrl"] = Optional(args, "repo_url") }, ct);
                    return $"ok: made team {fresh} and dispatched {Label(n)} to its Manager.";
                }
                var team = await TeamAsync(Required(args, "team"), ct);
                var r = await board("backlog.dispatch", new JsonObject { ["id"] = id, ["teamId"] = team.Id, ["again"] = Bool(args, "again") }, ct);
                return $"ok: dispatched {Label(r)} to the Manager of {team.Name}. It runs when a slot is free.";
            }
            case "board_move":
            {
                var lane = Required(args, "lane");
                await board("board.move", new JsonObject { ["id"] = Long(args, "card") ?? throw new ArgumentException("'card' is required"), ["lane"] = lane, ["note"] = Optional(args, "note") }, ct);
                return $"ok: card {args["card"]} moved to {lane}; the team's Manager is told.";
            }
            case "board_comment":
                await board("board.comment", new JsonObject { ["id"] = Long(args, "card") ?? throw new ArgumentException("'card' is required"), ["text"] = Required(args, "text") }, ct);
                return $"ok: commented on card {args["card"]}; the team's Manager is told.";

            case "git_branches_delete":
            {
                var (team, repo) = await RepoAsync(args, ct);
                var plan = await git.PlanDeleteAsync(repo, List(args, "branches"), List(args, "unmerged_ok"), Bool(args, "remote"), ct);
                return await ConfirmedAsync(name, args, $"Delete these branches in {team.Name}:\n{plan.Text}", plan.Key, plan.Empty, plan.Run, ct);
            }
            case "git_prune":
            {
                var (team, repo) = await RepoAsync(args, ct);
                var plan = await git.PlanPruneAsync(repo, ct);
                return await ConfirmedAsync(name, args, $"Prune in {team.Name}:\n{plan.Text}", plan.Key, plan.Empty, plan.Run, ct);
            }
            case "git_push_branch":
            {
                var (team, repo) = await RepoAsync(args, ct);
                return $"ok: {team.Name}/{repo.Name}: " + await git.PushBranchAsync(repo, Required(args, "branch"), ct);
            }
            case "git_sync_branch":
            {
                var (team, repo) = await RepoAsync(args, ct);
                return $"ok: {team.Name}/{repo.Name}: " + await git.SyncBranchAsync(repo, Required(args, "branch"), ct);
            }
            case "git_pr_open":
            {
                var (_, repo) = await RepoAsync(args, ct);
                await RequireGitHubAsync(repo, ct);
                var branch = Required(args, "branch");
                var r = await Gh(repo.Path, ["pr", "create", "--head", branch, "--base", repo.BaseBranch, "--title", Required(args, "title"), "--body", Optional(args, "body") ?? ""], ct);
                return $"ok: opened a pull request for {branch} into {repo.BaseBranch}: {r}";
            }
            case "git_pr_merge":
            {
                var (team, repo) = await RepoAsync(args, ct);
                await RequireGitHubAsync(repo, ct);
                var pr = (Long(args, "pr") ?? throw new ArgumentException("'pr' is required")).ToString();
                var view = await Gh(repo.Path, ["pr", "view", pr, "--json", "number,title,headRefName,baseRefName,state,isDraft"], ct);
                if (JsonNode.Parse(view) is not JsonObject v || v["state"]?.GetValue<string>() != "OPEN") throw new ArgumentException($"pull request {pr} is not open; nothing to merge");
                if (v["baseRefName"]?.GetValue<string>() != repo.BaseBranch) throw new ArgumentException($"pull request {pr} targets {v["baseRefName"]}, not {repo.BaseBranch}; refused");
                var text = $"Merge pull request #{pr} \"{v["title"]}\" ({v["headRefName"]} into {v["baseRefName"]}) in {team.Name}. A merged pull request cannot simply be undone.";
                return await ConfirmedAsync(name, args, text, view, false,
                    async token => "merged: " + await Gh(repo.Path, ["pr", "merge", pr, "--merge"], token), ct);
            }

            case "team_archive":
            {
                var team = await TeamAsync(Required(args, "team"), ct);
                var text = $"Archive team {team.Name}: {team.Running} running, {team.Queued} queued, {team.Members} agents. It stops taking work and leaves the lists; team_unarchive brings it back.";
                return await ConfirmedAsync(name, args, text, $"{team.Id}:{team.Name}:{team.Running}:{team.Queued}", false, async token =>
                {
                    await ui("teams.archive", new JsonObject { ["teamId"] = team.Id }, token);
                    return $"archived {team.Name}.";
                }, ct);
            }
            case "team_unarchive":
            {
                var team = await TeamAsync(Required(args, "team"), ct);
                await ui("teams.unarchive", new JsonObject { ["teamId"] = team.Id }, ct);
                return $"ok: {team.Name} is back.";
            }
            case "team_delete":
            {
                var team = await TeamAsync(Required(args, "team"), ct);
                var text = $"DELETE team {team.Name} ({team.Members} agents, its messages and cards). This cannot be undone; its repository folder is left on this computer.";
                return await ConfirmedAsync(name, args, text, $"{team.Id}:{team.Name}", false, async token =>
                {
                    await ui("teams.delete", new JsonObject { ["teamId"] = team.Id, ["confirm"] = team.Name }, token);
                    return $"deleted {team.Name}.";
                }, ct);
            }
            case "team_env_set":
                return await EnvSetAsync(args, ct);
            case "doctor_fix":
                return await DoctorFixAsync(args, ct);
            case "team_agent_change":
            {
                var team = await TeamAsync(Required(args, "team"), ct);
                var memberName = Required(args, "member");
                var member = (await bus.AgentCardsAsync(team.Id, ct)).Select(c => c.Member).FirstOrDefault(m => string.Equals(m.Name, memberName, StringComparison.OrdinalIgnoreCase))
                             ?? throw new ArgumentException($"{team.Name} has no member '{memberName}'");
                var cli = Optional(args, "cli") ?? member.Cli;
                var model = Optional(args, "model") ?? (cli == member.Cli ? member.Model : "");
                await ui("members.update", new JsonObject { ["memberId"] = member.Id, ["cli"] = cli, ["instructions"] = member.Instructions, ["model"] = model,
                    ["credential"] = cli == member.Cli ? member.Credential : "default" }, ct);
                return $"ok: {team.Name}/{member.Name} now uses {cli}{(model.Length > 0 ? $" ({model})" : "")}; it applies from its next run.";
            }
        }
        throw new ArgumentException($"unknown tool: {name}");
    }

    // ------------------------------------------------------------------ confirm flow

    private async Task<string> ConfirmedAsync(string tool, JsonObject args, string plan, string key, bool empty, Func<CancellationToken, Task<string>> run, CancellationToken ct)
    {
        if (empty) return "nothing to do:\n" + plan;
        key = Hash(key);
        if (Optional(args, "confirm") is { } token)
        {
            switch (_book.Redeem(_turn, tool, key, token))
            {
                case ConfirmationBook.Outcome.Ok:
                    return "ok: " + await run(ct);
                case ConfirmationBook.Outcome.SameTurn:
                    throw new ArgumentException("refused: the person has not said yes yet. Show them the list, end your turn, and call again with the token only after they answer yes in chat.");
                case ConfirmationBook.Outcome.NotThePerson:
                    throw new ArgumentException("refused: this turn was not started by a message from the person (it reviews updates that arrived), so it cannot take their yes. " +
                                                "Show them the list again if needed, and call with the token only after they answer yes in chat.");
                case ConfirmationBook.Outcome.Remote:
                    throw new ArgumentException(RemoteRefusal(tool));
                case ConfirmationBook.Outcome.Changed:
                    return Shown(tool, plan, _book.Issue(_turn, tool, key, plan), "THE LIST CHANGED since it was shown, so the earlier yes does not apply.");
                case ConfirmationBook.Outcome.OtherConversation:
                    return Shown(tool, plan, _book.Issue(_turn, tool, key, plan), "That confirmation was made in another conversation, so it does not apply here (and is void now).");
                default:
                    return Shown(tool, plan, _book.Issue(_turn, tool, key, plan), "That confirmation is unknown or expired.");
            }
        }
        return Shown(tool, plan, _book.Issue(_turn, tool, key, plan), null);
    }

    private static string Shown(string tool, string plan, string token, string? why) =>
        $"NOT DONE. {(why is null ? "" : why + " ")}Show the person exactly this and ask them to say yes in chat:\n{plan}\n" +
        $"Only after they answer yes in their NEXT message, call {tool} again with the same arguments plus confirm=\"{token}\". Do not run it before; do not ask for a yes on a different list.";

    /// <summary>
    /// Runs one doctor fix (Settings > Health) when the person asks. The first call never runs a fix that removes or archives
    /// (the doctor answers needsConfirm): it shows what it would do with a token, and only the person's yes in a later turn runs it.
    /// </summary>
    private async Task<string> DoctorFixAsync(JsonObject args, CancellationToken ct)
    {
        var fix = Required(args, "fix").Trim();
        var probe = await ui("ops.doctorFix", new JsonObject { ["fix"] = fix }, ct) as JsonObject
                    ?? throw new ArgumentException("the health fixes are not available from here; the person can use Settings > Health");
        var message = probe["message"]?.GetValue<string>() ?? "";
        if (probe["needsConfirm"]?.GetValue<bool>() != true)
            return probe["done"]?.GetValue<bool>() == true ? $"ok: {message}" : $"not done: {message}";
        // The yes is bound to exactly what was shown: the fix id plus its targets (the credential keys, the team), sorted, as
        // branch deletes are bound to SHAs. A list that grew or shrank since the yes gives another key, so the yes is void.
        var targets = Targets(probe);
        var plan = $"Run the health fix {fix}: {message}";
        try
        {
            return await ConfirmedAsync("doctor_fix", args, plan, DoctorFixKey(fix, targets, message), false, async token =>
            {
                var p = new JsonObject { ["fix"] = fix, ["confirm"] = true };
                if (targets is not null) p["expect"] = new JsonArray(targets.Select(t => (JsonNode?)t).ToArray());
                var r = await ui("ops.doctorFix", p, token) as JsonObject;
                // The list moved between this call's look and the run (the doctor removed nothing): ask again on the new list.
                if (r?["changed"]?.GetValue<bool>() == true) throw new DoctorListChanged(r);
                return r?["message"]?.GetValue<string>() ?? "the fix ran.";
            }, ct);
        }
        catch (DoctorListChanged changed)
        {
            var text = changed.Result["message"]?.GetValue<string>() ?? "";
            var key = Hash(DoctorFixKey(fix, Targets(changed.Result), text));
            var shown = $"Run the health fix {fix}: {text}";
            return Shown("doctor_fix", shown, _book.Issue(_turn, "doctor_fix", key, shown),
                "THE LIST CHANGED since it was shown, so the earlier yes does not apply.");
        }
    }

    private static List<string>? Targets(JsonObject r) =>
        r["targets"] is JsonArray a ? a.Select(n => n?.GetValue<string>() ?? "").Order(StringComparer.Ordinal).ToList() : null;

    /// <summary>The fix id plus its canonical targets; without targets (an older doctor), the shown message.</summary>
    private static string DoctorFixKey(string fix, List<string>? targets, string message) =>
        fix + "\n" + (targets is null ? "message:" + message : string.Join("\n", targets));

    private static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    private sealed class DoctorListChanged(JsonObject result) : Exception
    {
        public JsonObject Result { get; } = result;
    }

    private async Task<string> EnvSetAsync(JsonObject args, CancellationToken ct)
    {
        var team = await TeamAsync(Required(args, "team"), ct);
        var name = Required(args, "name").Trim();
        var remove = Bool(args, "remove");
        var value = args["value"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        if (!remove && value is null) throw new ArgumentException("'value' is required (or remove: true)");
        var current = await ui("teams.env", new JsonObject { ["teamId"] = team.Id }, ct) as JsonObject ?? [];
        var entries = new JsonArray();
        foreach (var kv in current)
            if (kv.Key != name) entries.Add(new JsonObject { ["name"] = kv.Key, ["value"] = kv.Value?.GetValue<string>() ?? "" });
        if (!remove) entries.Add(new JsonObject { ["name"] = name, ["value"] = value });
        Task<string> Apply(CancellationToken token) => SetEnv(team, entries, name, remove, token);
        // Changing or removing a variable that exists loses its old value (never shown): that asks first. A new one does not.
        if (current.ContainsKey(name))
        {
            var text = remove ? $"Remove environment variable {name} from team {team.Name} (its value is not shown here and is lost)."
                              : $"Replace the value of environment variable {name} of team {team.Name} (the old value is lost; neither is shown here).";
            return await ConfirmedAsync("team_env_set", args, text, $"{team.Id}:{name}:{remove}", false, Apply, ct);
        }
        if (remove) throw new ArgumentException($"{team.Name} has no variable {name}");
        return "ok: " + await Apply(ct);
    }

    private async Task<string> SetEnv(TeamSummary team, JsonArray entries, string name, bool remove, CancellationToken ct)
    {
        await ui("teams.setEnv", new JsonObject { ["teamId"] = team.Id, ["entries"] = entries }, ct);
        return $"{team.Name}: {name} {(remove ? "removed" : "set")} (the value is not shown). It applies from the next run.";
    }

    /// <summary>
    /// The open pull requests of a repository, by head branch ("#7 Title"), through the person's gh on the host, run in the
    /// team repository (which no agent writes). Null when gh is not available, fails or answers something unreadable;
    /// team_git then says the check did not run.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>?> OpenPullRequestsAsync(GitRepoTarget repo, CancellationToken ct)
    {
        if (gh is null) return null;
        var r = await gh(repo.Path, ["pr", "list", "--state", "open", "--limit", "200", "--json", "number,title,headRefName"], ct);
        if (r.ExitCode != 0) return null;
        try
        {
            if (JsonNode.Parse(r.Stdout) is not JsonArray list) return null;
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pr in list.OfType<JsonObject>())
                if (pr["headRefName"] is JsonValue h && h.TryGetValue<string>(out var head))
                    map.TryAdd(head, $"#{pr["number"]} {pr["title"]?.ToString() ?? ""}".TrimEnd());
            return map;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Pull requests are GitHub's (through gh): a repository with no origin, or hosted elsewhere, is refused with what to do instead.</summary>
    private async Task RequireGitHubAsync(ActionRepo repo, CancellationToken ct)
    {
        if (RepoHost.PullRequestsUnavailable(repo.Name, await git.OriginUrlAsync(repo.Path, ct)) is { } why) throw new ArgumentException(why);
    }

    private async Task<string> Gh(string path, IReadOnlyList<string> args, CancellationToken ct)
    {
        if (gh is null) throw new ArgumentException("gh is not available on this computer, so pull requests cannot be opened or merged from here; the closest thing: push the branch (git_push_branch) and the person opens it on GitHub");
        var r = await gh(path, args, ct);
        if (r.ExitCode != 0) throw new ArgumentException("gh refused: " + PersonGitPush.Redact(string.Join(' ', r.Stderr, r.Stdout).Trim()));
        return PersonGitPush.Redact(r.Stdout);
    }

    private async Task<(TeamSummary Team, ActionRepo Repo)> RepoAsync(JsonObject args, CancellationToken ct)
    {
        var team = await TeamAsync(Required(args, "team"), ct);
        var list = await repos(team, ct);
        var want = Optional(args, "repo") ?? "main";
        return (team, list.FirstOrDefault(r => string.Equals(r.Name, want, StringComparison.OrdinalIgnoreCase))
                      ?? throw new ArgumentException($"{team.Name} has no repository '{want}'. It has: {string.Join(", ", list.Select(r => r.Name))}"));
    }

    private async Task<TeamSummary> TeamAsync(string name, CancellationToken ct)
    {
        var teams = await bus.TeamsAsync(ct);
        return teams.FirstOrDefault(t => string.Equals(t.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
               ?? throw new ArgumentException($"there is no team '{name}'. Teams: {string.Join(", ", teams.Select(t => t.Name))}");
    }

    private static long ItemId(JsonObject args)
    {
        var raw = Required(args, "item").Trim().TrimStart('B', 'b');
        return long.TryParse(raw, out var id) ? id : throw new ArgumentException("'item' is a backlog label such as B006");
    }

    private static string Label(JsonNode? item) => item?["label"]?.GetValue<string>() ?? "the item";

    private static string Required(JsonObject args, string name) => Optional(args, name) ?? throw new ArgumentException($"'{name}' is required");

    private static string? Optional(JsonObject args, string name) =>
        args[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

    private static bool Bool(JsonObject args, string name) =>
        args[name] is JsonValue v && (v.TryGetValue<bool>(out var b) ? b : v.TryGetValue<string>(out var s) && bool.TryParse(s, out var c) && c);

    private static long? Long(JsonObject args, string name) =>
        args[name] is JsonValue v ? (v.TryGetValue<long>(out var i) ? i : v.TryGetValue<int>(out var n) ? n : v.TryGetValue<string>(out var s) && long.TryParse(s, out var j) ? j : null) : null;

    /// <summary>A list given as a JSON array or as comma/space separated text.</summary>
    private static IReadOnlyList<string> List(JsonObject args, string name) => args[name] switch
    {
        JsonArray a => a.Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToList(),
        JsonValue v when v.TryGetValue<string>(out var s) => s.Split([',', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        _ => [],
    };

    // ------------------------------------------------------------------ the tool list

    public static JsonArray Tools() =>
    [
        Tool("backlog_edit", "Edit a backlog item (title, body and/or the team it is meant for), when the person asks.",
            ("item", "string", "The item, e.g. B006.", true), ("title", "string", "New title.", false), ("body", "string", "New body (the whole text).", false), ("team", "string", "Team it is meant for.", false)),
        Tool("backlog_ready", "Mark a backlog item ready to dispatch, when the person says it is ready.", ("item", "string", "The item, e.g. B006.", true)),
        Tool("backlog_dispatch", "Dispatch a ready backlog item to a team's Manager (or to a brand-new team with new_team), when the person asks or accepts. It starts paid agent work.",
            ("item", "string", "The item.", true), ("team", "string", "The team to dispatch to.", false),
            ("again", "boolean", "true to dispatch again though it is in flight (only on purpose).", false),
            ("new_team", "string", "Instead: make a team with this name and dispatch there.", false), ("manager_cli", "string", "new_team: the Manager's CLI (default claude).", false),
            ("repo_path", "string", "new_team: repository folder.", false), ("repo_url", "string", "new_team: repository address to clone.", false)),
        Tool("backlog_archive", "Archive a backlog item (reversible with backlog_restore).", ("item", "string", "The item.", true)),
        Tool("backlog_restore", "Bring an archived backlog item back.", ("item", "string", "The item.", true)),
        Tool("board_move", "Move a board card to a lane (todo, in-progress, blocked, done); the team's Manager is told.",
            ("card", "integer", "The card's number.", true), ("lane", "string", "todo, in-progress, blocked or done.", true), ("note", "string", "Why (optional).", false)),
        Tool("board_comment", "Comment on a board card; the team's Manager is told.", ("card", "integer", "The card's number.", true), ("text", "string", "The comment.", true)),
        Tool("git_branches_delete", "Delete branches of a team's repository (local, and on origin with remote). DESTRUCTIVE: the first call only returns the exact list and a token; show the list to the person, and call again with the token only after they say yes in their next message. " +
                                    "Each deleted branch's SHA is recorded. Never deletes the base branch, a branch checked out anywhere, or an unmerged branch unless it is named in unmerged_ok.",
            ("team", "string", "The team.", true), ("repo", "string", "Repository name (default main).", false),
            ("branches", "string", "Branch names, comma separated; omit for every merged branch.", false), ("unmerged_ok", "string", "Unmerged branches the person explicitly named for deletion.", false),
            ("remote", "boolean", "Also delete the same branches on origin.", false), ("confirm", "string", "The token, after the person's yes.", false)),
        Tool("git_prune", "Forget remote-tracking branches origin no longer has. Lists them first and needs the person's yes like git_branches_delete.",
            ("team", "string", "The team.", true), ("repo", "string", "Repository name (default main).", false), ("confirm", "string", "The token, after the person's yes.", false)),
        Tool("git_push_branch", "Push one branch to origin (never forced), when the person asks.",
            ("team", "string", "The team.", true), ("branch", "string", "The branch.", true), ("repo", "string", "Repository name (default main).", false)),
        Tool("git_sync_branch", "Bring a member's or team branch current with the base branch (a merge commit; a conflict changes nothing; a checked-out branch is refused).",
            ("team", "string", "The team.", true), ("branch", "string", "The branch.", true), ("repo", "string", "Repository name (default main).", false)),
        Tool("git_pr_open", "Open a GitHub pull request for a branch into the base branch (only for a repository whose origin is on GitHub; needs gh signed in on this computer).",
            ("team", "string", "The team.", true), ("branch", "string", "The branch.", true), ("title", "string", "Title.", true), ("body", "string", "Description.", false), ("repo", "string", "Repository name (default main).", false)),
        Tool("git_pr_merge", "Merge an open GitHub pull request (into the base branch only; origin on GitHub). Needs the person's yes: the first call shows the pull request and returns a token.",
            ("team", "string", "The team.", true), ("pr", "integer", "The pull request's number.", true), ("repo", "string", "Repository name (default main).", false), ("confirm", "string", "The token, after the person's yes.", false)),
        Tool("team_archive", "Archive a team. Needs the person's yes (first call shows it, returns a token).", ("team", "string", "The team.", true), ("confirm", "string", "The token, after the person's yes.", false)),
        Tool("team_unarchive", "Bring an archived team back.", ("team", "string", "The team.", true)),
        Tool("team_delete", "DELETE a team for good. Needs the person's yes (first call shows it, returns a token).", ("team", "string", "The team.", true), ("confirm", "string", "The token, after the person's yes.", false)),
        Tool("team_env_set", "Set (or remove) one environment variable for a team's agents. Values are never shown back. Replacing or removing an existing one needs the person's yes.",
            ("team", "string", "The team.", true), ("name", "string", "The variable's name.", true), ("value", "string", "The value (as the person gave it).", false),
            ("remove", "boolean", "true to remove it.", false), ("confirm", "string", "The token, after the person's yes.", false)),
        Tool("doctor_fix", "Run one fix the health check offers (see the doctor tool; use the fix's id), only when the person asks. A fix that removes or archives something needs the person's yes: the first call returns the plan and a token; call again with the token only after they say yes in their next message. A fix that just opens a screen tells the person where to click.",
            ("fix", "string", "The fix's id, as the doctor lists it.", true), ("confirm", "string", "The token, after the person's yes.", false)),
        Tool("team_agent_change", "Change which agent CLI (and model) a team member uses; it applies from its next run.",
            ("team", "string", "The team.", true), ("member", "string", "The member's name.", true), ("cli", "string", "claude, grok, codex, copilot or antigravity.", false), ("model", "string", "Model (optional).", false)),
    ];

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

    // ------------------------------------------------------------------ the app's wiring

    /// <summary>The actions on a TeamRepos-backed app: a team's main repository and its writable extras, with each member's clone folders.</summary>
    public static ConciergeActions ForApp(BusEngine bus, TeamRepos teamRepos, Func<string, JsonObject?, CancellationToken, Task<JsonNode?>> ui,
        Func<string, JsonObject?, CancellationToken, Task<JsonNode?>> board, AuditLog? audit = null) =>
        new(bus, ui, board, new ConciergeGit(teamRepos.Git, teamRepos.Pusher), async (team, ct) =>
        {
            var members = (await bus.AgentCardsAsync(team.Id, ct)).Select(c => c.Member.Name).ToList();
            var list = new List<ActionRepo>();
            // Card 104: every team's clones of the repository count for what is checked out, not only this team's.
            List<string> With(string repoPath, IEnumerable<string> own) => own.Concat(teamRepos.RepoUsers(repoPath).Select(u => u.ClonePath)).Distinct().ToList();
            if (teamRepos.RepoOf(team.Id) is { } main)
                list.Add(new ActionRepo("main", main.Path, main.BaseBranch, With(main.Path, members.Select(m => Path.Combine(teamRepos.MemberRoot(team.Name, m), "repo")))));
            foreach (var e in teamRepos.ExtrasOf(team.Id))
                list.Add(new ActionRepo(e.Name, e.Path, e.BaseBranch, With(e.Path, members.Select(m => teamRepos.ExtraClonePath(team.Name, m, e.Name)))));
            return list;
        }, Shared, HostGh.RunAsync, audit, ConciergeTurn.None);

    /// <summary>One book for the whole app: the person's yes arrives in a later turn than the list.</summary>
    public static ConfirmationBook Shared { get; } = new();
}
