using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Yawble.Core.Bus;
using Yawble.Core.Gateway;
using Yawble.Core.Ipc;
using Yawble.Core.Policy;

namespace Yawble.Core.Integrations;

/// <summary>A host an agent was refused (or asked for), waiting for the person or decided (network_asks, 0039).</summary>
/// <param name="Member">"Team/Name".</param>
/// <param name="Scope">How it was allowed: agent, team or everywhere; null while pending or when denied.</param>
public sealed record NetworkAskRow(long Id, string At, long TeamId, string Team, string Member, string Host, string? Reason, string? Method,
    string? PathSample, int Count, string LastSeen, long? Workflow, long? CausationSeq, string State, string? Scope, string? DecidedBy,
    string? DecidedVia, string? DecidedAt, string? Note)
{
    public string MemberName => Member[(Member.IndexOf('/') + 1)..];
}

/// <summary>The person's four answers to a network ask (network.answer's choice).</summary>
public static class NetworkAskChoices
{
    public const string Agent = "agent", Team = "team", Everywhere = "everywhere", Deny = "deny";

    public static readonly IReadOnlyList<string> All = [Agent, Team, Everywhere, Deny];
}

/// <summary>Which refused hosts the person is asked about: host names only, never an address or a host no policy may list.</summary>
public static class NetworkAskFilter
{
    /// <summary>The app's own registrable domains: never asked about (the Cloud Link's site and anything under it).</summary>
    public static readonly IReadOnlyList<string> AppDomains = ["yawble.ai", FloorRules.ReservedSuffix];

    /// <summary>Names that only mean something on a private network (mDNS, home routers, intranets).</summary>
    private static readonly string[] PrivateSuffixes = ["local", "lan", "home.arpa", "internal", "intranet", "corp", "home", "localdomain"];

    public static string Normalize(string host) => host.Trim().TrimEnd('.').ToLowerInvariant();

    /// <summary>Why the person is not asked about this host (it stays a plain refusal), or null when they are.</summary>
    public static string? Problem(string host)
    {
        var h = Normalize(host);
        if (h.Length == 0) return "no host was given";
        // The gateway compares the name as sent, so a trailing dot would be allowed under one name and refused under another.
        if (host.Trim().EndsWith('.')) return $"'{host.Trim()}' ends in a dot: give the name without it ({h})";
        if (IPAddress.TryParse(h.Trim('[', ']'), out _)) return $"'{h}' is an IP address: the policy is host-name based, and addresses are never asked about";
        if (h.Contains(':') || h.Contains('/') || h.Contains('@')) return $"'{h}' is not a host name: give the name alone (api.nuget.org), without a scheme, port or path";
        // Only plain ASCII names (letters, digits, hyphens, dots) are asked about: a name in another script, or its xn--
        // form, can look like a well-known host (pypí.org) while it resolves somewhere else.
        if (h.Any(ch => !(ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.')))
            return $"'{h}' has characters other than letters, digits, hyphens and dots; such names are never asked about";
        if (h.Split('.').Any(label => label.StartsWith("xn--", StringComparison.Ordinal)))
            return $"'{h}' is an internationalized (xn--) name; such names are never asked about";
        if (h == "localhost" || h.EndsWith(".localhost", StringComparison.Ordinal)) return $"'{h}' is this computer (loopback) and is never reachable";
        if (!h.Contains('.') || PrivateSuffixes.Any(s => h.EndsWith("." + s, StringComparison.Ordinal)))
            return $"'{h}' is a private-network name and is never reachable";
        if (AppDomains.Any(d => h == d || h.EndsWith("." + d, StringComparison.Ordinal))) return $"'{h}' is one of the app's own hosts and is never reachable by an agent";
        if (Vhosts.IsNeverConnect(h)) return $"'{h}' is a model vendor's API or sign-in host; no one can allow it (a floor rule)";
        return FloorRules.HostProblem(h);
    }

    /// <summary>What a well-known package registry is for, shown with the ask; null for any other host.</summary>
    public static string? Hint(string host) => Normalize(host) switch
    {
        "api.nuget.org" => "NuGet: .NET packages (dotnet restore, dotnet test)",
        "registry.npmjs.org" => "npm: Node.js packages (npm install, npm ci)",
        "pypi.org" or "files.pythonhosted.org" => "pip: Python packages (pip install)",
        _ => null,
    };
}

/// <summary>The policy changes a network ask makes, through the policy store (its checks; it never writes a wildcard).</summary>
public interface INetworkPolicy
{
    /// <summary>Whether the host is on the member's effective allowlist for its next run.</summary>
    bool AllowedFor(long memberId, string host);

    /// <summary>Adds the host to the member's loosen block.</summary>
    Task AllowForMemberAsync(long memberId, string host, CancellationToken ct);

    /// <summary>Adds the host to the global network.allowHosts.</summary>
    Task AllowEverywhereAsync(string host, CancellationToken ct);
}

/// <summary>Tells a member something as the person (the bus's PersonTellAsync): joins the workflow of <paramref name="causation"/> when given.</summary>
public delegate Task NetworkAskTell(string team, string member, string text, long? causation, CancellationToken ct);

/// <summary>
/// Network asks: a run's gateway refusing a host that is not on the agent's allowlist, or the agent's request_host tool,
/// raises one pending ask per (team, member, host); repeats only count up. The person answers in Needs you: allow it for
/// this agent (its loosen block), for the team (every member's), everywhere (the global allowlist), or deny it (not asked
/// again for that member for 24 hours). The policy store makes every change; nothing is allowed without the person's
/// answer. After an allow the team's Manager is told to retry; after a deny the agent is told. Each ask and its answer is
/// in the Approvals log (Logs/LogSources.cs).
/// </summary>
public sealed class NetworkAskService(string connectionString, INetworkPolicy policy, NetworkAskTell tell, TimeProvider? clock = null) : IHostRefusals
{
    /// <summary>How long a denied host is not asked again for the same member.</summary>
    public static readonly TimeSpan DenyQuiet = TimeSpan.FromHours(24);

    /// <summary>Pushed when an ask is raised or answered: Needs you refreshes.</summary>
    public const string ChangedEvent = "network.asksChanged";

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    /// <summary>How many different hosts may wait for the person per member at once.</summary>
    public const int MaxWaitingPerMember = 5;

    private readonly Lock _lock = new();
    // The asks an answer is being applied to: claimed before any policy write, so two answers at once (the window and a
    // cloud viewer) cannot both change the policy; the second is told the request is being answered.
    private readonly HashSet<long> _answering = [];

    public Action? Changed { get; set; }

    public static bool Handles(string method) => method.StartsWith("network.", StringComparison.Ordinal);

    private sealed record Who(long MemberId, long TeamId, string Team, string Name)
    {
        public string Qualified => $"{Team}/{Name}";
    }

    /// <summary>What raising an ask came to. <paramref name="Problem"/>: why it was not asked (filtered).</summary>
    public sealed record Raised(HostAskOutcome Outcome, long? Id, string Host, string? Problem = null, DateTimeOffset? QuietUntil = null);

    // ------------------------------------------------------------------ raising

    /// <summary>The gateway refused a host not on the allowlist (CONNECT, or plain http).</summary>
    public HostAskOutcome Refused(string member, string host, string method, string? path)
    {
        var slash = member.IndexOf('/');
        if (slash <= 0 || Find(member[..slash], member[(slash + 1)..]) is not { } who) return HostAskOutcome.None;
        return Raise(who, host, null, method, Sample(path), null, null).Outcome;
    }

    /// <summary>The request_host tool: the same ask, with the agent's reason and its workflow.</summary>
    public string RequestFromAgent(Bus.MemberRef member, string host, string reason, long workflow, long causation)
    {
        // A URL is read as its host (https://api.nuget.org/v3/index.json → api.nuget.org); anything else is checked as given.
        var h = NetworkAskFilter.Normalize(host.Contains("://", StringComparison.Ordinal) && Uri.TryCreate(host.Trim(), UriKind.Absolute, out var uri) && uri.Host.Length > 0
            ? uri.Host : host);
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("give a reason: one line on what you need the host for");
        var who = new Who(member.Id, member.TeamId, member.Team, member.Name);
        var r = Raise(who, h, Clip(reason.Trim(), 500), null, null, workflow, causation);
        return r.Outcome switch
        {
            HostAskOutcome.Asked or HostAskOutcome.Pending =>
                $"asked: the person is asked to allow {r.Host} (network request {r.Id}){(r.Outcome == HostAskOutcome.Pending ? "; it was already waiting, and your reason is added" : "")}. " +
                $"A host the person allows is reachable from your next run, not this one. Hand back now as blocked on {r.Host} (say what it is for); " +
                "the Manager is told when it is allowed. Do not retry in a loop or try another site.",
            HostAskOutcome.Denied =>
                $"refused: the person denied {r.Host} for you; it is not asked again before {r.QuietUntil:yyyy-MM-dd HH:mm} UTC. " +
                "Do not try another site: carry on without it, or hand back blocked on it.",
            HostAskOutcome.AllowedNextRun =>
                $"{r.Host} is already allowed for you from your next run. If this run was refused it, hand back blocked on {r.Host} so the work is retried in a new run.",
            _ => $"refused: {r.Problem}. The person is not asked about it; do not try to reach it another way.",
        };
    }

    private Raised Raise(Who who, string host, string? reason, string? method, string? sample, long? workflow, long? causation)
    {
        var h = NetworkAskFilter.Normalize(host);
        if (NetworkAskFilter.Problem(host) is { } problem) return new Raised(HostAskOutcome.None, null, h, problem);
        long id, count;
        lock (_lock)
        {
            if (policy.AllowedFor(who.MemberId, h)) return new Raised(HostAskOutcome.AllowedNextRun, null, h);
            var now = _clock.GetUtcNow();
            using var c = Open();
            // At most MaxWaitingPerMember different hosts wait for the person per member: past that a new host stays a plain
            // refusal (a repeat of one already waiting still counts up), so one agent cannot fill Needs you.
            if (Scalar(c, "SELECT count(*) FROM network_asks WHERE team_id = $t AND member = $m AND state = 'pending' AND host <> $h;",
                    ("$t", who.TeamId), ("$m", who.Qualified), ("$h", h)) is long waiting && waiting >= MaxWaitingPerMember
                && Scalar(c, "SELECT count(*) FROM network_asks WHERE team_id = $t AND member = $m AND state = 'pending' AND host = $h;",
                    ("$t", who.TeamId), ("$m", who.Qualified), ("$h", h)) is 0L)
                return new Raised(HostAskOutcome.None, null, h,
                    $"{MaxWaitingPerMember} of your requests already wait for the person; wait for their answers before asking for another host");
            if (Scalar(c, "SELECT decided_at FROM network_asks WHERE team_id = $t AND member = $m AND host = $h AND state = 'denied' ORDER BY decided_at DESC LIMIT 1;",
                    ("$t", who.TeamId), ("$m", who.Qualified), ("$h", h)) is string denied
                && DateTimeOffset.TryParse(denied, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var deniedAt)
                && now - deniedAt < DenyQuiet)
                return new Raised(HostAskOutcome.Denied, null, h, null, deniedAt + DenyQuiet);
            using var cmd = Command(c,
                """
                INSERT INTO network_asks (at, team_id, member, host, reason, method, path_sample, count, last_seen, workflow, causation_seq)
                VALUES ($now, $t, $m, $h, $r, $method, $p, 1, $now, $w, $c)
                ON CONFLICT (team_id, member, host) WHERE state = 'pending' DO UPDATE SET
                    count = count + 1, last_seen = excluded.last_seen,
                    reason = COALESCE(excluded.reason, reason),
                    method = COALESCE(method, excluded.method), path_sample = COALESCE(path_sample, excluded.path_sample),
                    workflow = COALESCE(excluded.workflow, workflow), causation_seq = COALESCE(excluded.causation_seq, causation_seq)
                RETURNING id, count;
                """,
                ("$now", Stamp(now)), ("$t", who.TeamId), ("$m", who.Qualified), ("$h", h), ("$r", reason), ("$method", method), ("$p", sample),
                ("$w", workflow), ("$c", causation));
            using var r = cmd.ExecuteReader();
            r.Read();
            id = r.GetInt64(0);
            count = r.GetInt64(1);
        }
        // a repeat of the gateway's refusal changes only the count: no push for it (a restore loop would flood the window)
        if (count == 1 || reason is not null) Changed?.Invoke();
        return new Raised(count == 1 ? HostAskOutcome.Asked : HostAskOutcome.Pending, id, h);
    }

    /// <summary>A plain-http path as shown with the ask: no query (it may carry a token), at most 80 characters.</summary>
    private static string? Sample(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var p = path.Split('?', '#')[0];
        return Clip(p, 80);
    }

    // ------------------------------------------------------------------ reading

    public int PendingCount() => (int)(long)(Scalar(null, "SELECT count(*) FROM network_asks WHERE state = 'pending';") ?? 0L);

    public IReadOnlyList<NetworkAskRow> Asks(bool pendingOnly = true, long? teamId = null)
    {
        using var c = Open();
        using var cmd = Command(c,
            "SELECT a.id, a.at, a.team_id, COALESCE(t.name, ''), a.member, a.host, a.reason, a.method, a.path_sample, a.count, a.last_seen, a.workflow, a.causation_seq, " +
            "a.state, a.scope, a.decided_by, a.decided_via, a.decided_at, a.note FROM network_asks a LEFT JOIN teams t ON t.id = a.team_id WHERE 1 = 1" +
            (pendingOnly ? " AND a.state = 'pending'" : "") + (teamId is null ? "" : " AND a.team_id = $t") + " ORDER BY a.id;",
            ("$t", teamId));
        using var r = cmd.ExecuteReader();
        var list = new List<NetworkAskRow>();
        while (r.Read())
            list.Add(new NetworkAskRow(r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetString(3), r.GetString(4), r.GetString(5), S(r, 6), S(r, 7), S(r, 8),
                (int)r.GetInt64(9), r.GetString(10), r.IsDBNull(11) ? null : r.GetInt64(11), r.IsDBNull(12) ? null : r.GetInt64(12), r.GetString(13), S(r, 14),
                S(r, 15), S(r, 16), S(r, 17), S(r, 18)));
        return list;
    }

    /// <summary>One ask as the Needs you item reads it (network.asks).</summary>
    public static JsonObject ToJson(NetworkAskRow a) => new()
    {
        ["id"] = a.Id, ["at"] = a.At, ["teamId"] = a.TeamId, ["team"] = a.Team, ["member"] = a.Member, ["memberName"] = a.MemberName,
        ["host"] = a.Host, ["reason"] = a.Reason, ["method"] = a.Method, ["pathSample"] = a.PathSample, ["count"] = a.Count, ["lastSeen"] = a.LastSeen,
        ["hint"] = NetworkAskFilter.Hint(a.Host), ["workflow"] = a.Workflow, ["state"] = a.State, ["scope"] = a.Scope,
        ["decidedBy"] = a.DecidedBy, ["decidedAt"] = a.DecidedAt,
        ["choices"] = new JsonArray(NetworkAskChoices.All.Select(ch => (JsonNode?)ch).ToArray()),
    };

    /// <summary>The open asks as plain lines (the Concierge's and the companion's status reads).</summary>
    public IReadOnlyList<string> PendingLines() => Asks().Select(a =>
        $"- {a.Member} asks for {a.Host}{(NetworkAskFilter.Hint(a.Host) is { } hint ? $" ({hint})" : "")}: " +
        (a.Reason is { Length: > 0 } why ? $"\"{Clip(why, 200)}\"" : $"refused {a.Method ?? "a request"}{(a.PathSample is null ? "" : " " + a.PathSample)}") +
        $"; {a.Count} time(s) since {a.At} (network request {a.Id})").ToList();

    // ------------------------------------------------------------------ the person's answer

    public async Task<JsonNode?> DispatchAsync(string method, JsonObject? p, CancellationToken ct)
    {
        try
        {
            return method switch
            {
                // [{ id, at, teamId, team, member, memberName, host, reason, method, pathSample, count, lastSeen, hint, workflow, state, scope, decidedBy, decidedAt, choices }]
                "network.asks" => new JsonArray(Asks(p?["all"]?.GetValue<bool>() != true, OptLong(p, "teamId")).Select(a => (JsonNode?)ToJson(a)).ToArray()),
                "network.answer" => await AnswerAsync(OptLong(p, "id") ?? throw new ArgumentException("id is required"),
                    OptStr(p, "choice") ?? throw new ArgumentException("choice is required (agent, team, everywhere or deny)"), OptStr(p, "note"), ct),
                _ => throw new RpcException("unknown_method", $"no method named '{method}'"),
            };
        }
        catch (ArgumentException e)
        {
            throw new RpcException("bad_params", e.Message);
        }
        catch (PolicyException e)
        {
            throw new RpcException("policy_refused", string.Join("; ", e.Errors));
        }
    }

    /// <summary>
    /// The person's answer to one ask: the policy change first (through the policy store; a refusal leaves the ask
    /// waiting), then the ask closes (with any other waiting ask it covers), then the Manager or the agent is told.
    /// </summary>
    public async Task<JsonObject> AnswerAsync(long id, string choice, string? note, CancellationToken ct)
    {
        var who = Provenance.Actor.Current ?? Provenance.Actor.Window;
        // Never without the person's click: an agent, the Concierge, a trigger or a paired program cannot answer.
        if (who.Via is not (Provenance.Actor.App or Provenance.Actor.Cloud))
            throw new ArgumentException("a network request is answered by the person, in Needs you");
        if (!NetworkAskChoices.All.Contains(choice)) throw new ArgumentException($"'{choice}' is not a choice (agent, team, everywhere or deny)");
        lock (_lock)
            if (!_answering.Add(id)) throw new ArgumentException($"network request {id} is being answered already");
        try
        {
            return await AnswerClaimedAsync(id, choice, note, who, ct);
        }
        finally
        {
            lock (_lock) _answering.Remove(id);
        }
    }

    private async Task<JsonObject> AnswerClaimedAsync(long id, string choice, string? note, Provenance.Actor who, CancellationToken ct)
    {
        var ask = Asks().FirstOrDefault(a => a.Id == id) ?? throw new ArgumentException($"network request {id} is not waiting");
        if (NetworkAskFilter.Problem(ask.Host) is { } problem) throw new ArgumentException(problem);
        var allow = choice != NetworkAskChoices.Deny;
        // The other waiting asks this answer covers, listed once before the policy writes: only these close with it,
        // so an ask raised while the writes run (its member never checked) stays waiting.
        List<NetworkAskRow> others = allow && choice != NetworkAskChoices.Agent
            ? Asks().Where(a => a.Id != id && a.Host == ask.Host && (choice == NetworkAskChoices.Everywhere || a.TeamId == ask.TeamId)).ToList()
            : [];
        if (allow)
        {
            var asker = Find(ask.Team, ask.MemberName) ?? throw new ArgumentException($"{ask.Member} is no longer on the team");
            switch (choice)
            {
                case NetworkAskChoices.Agent:
                    await policy.AllowForMemberAsync(asker.MemberId, ask.Host, ct);
                    break;
                case NetworkAskChoices.Team:
                    foreach (var m in TeamMembers(ask.TeamId)) await policy.AllowForMemberAsync(m, ask.Host, ct);
                    break;
                default:
                    await policy.AllowEverywhereAsync(ask.Host, ct);
                    break;
            }

            // The members this answer tells the Manager about: the asker, and for team or everywhere each member whose
            // waiting ask for the host closes with it. A member's own override can narrow the global list (it is an
            // intersection), so the global list alone does not reach it: that member also gets the host in its loosen block.
            var reached = new List<Who> { asker };
            foreach (var a in others)
                if (Find(a.Team, a.MemberName) is { } m && reached.All(r => r.MemberId != m.MemberId)) reached.Add(m);
            foreach (var m in reached)
                if (!policy.AllowedFor(m.MemberId, ask.Host)) await policy.AllowForMemberAsync(m.MemberId, ask.Host, ct);
            // Never tell the Manager a host is allowed unless the member's effective hosts now include it.
            if (reached.Where(m => !policy.AllowedFor(m.MemberId, ask.Host)).Select(m => m.Qualified).ToList() is { Count: > 0 } missed)
                throw new PolicyException([$"{ask.Host} is still not allowed for {string.Join(", ", missed)} after the change; the request stays waiting"]);
        }

        var cleanNote = string.IsNullOrWhiteSpace(note) ? null : Clip(note.Trim(), 500);
        List<NetworkAskRow> closed;
        lock (_lock)
        {
            using var c = Open();
            using var tx = c.BeginTransaction();
            var now = Stamp(_clock.GetUtcNow());
            const string Close = "UPDATE network_asks SET state = $s, scope = $scope, decided_by = $by, decided_via = $via, decided_at = $now, note = $note WHERE state = 'pending'";
            (string, object?)[] Args(string? n, long target) => [("$s", allow ? "allowed" : "denied"), ("$scope", allow ? choice : null), ("$by", who.User), ("$via", who.Via), ("$now", now), ("$note", n), ("$id", target)];
            // exactly one answer wins: a second one (the window and a cloud viewer at once) finds nothing waiting
            if (Exec(c, tx, Close + " AND id = $id;", Args(cleanNote, id)) == 0) throw new ArgumentException($"network request {id} is not waiting");
            // Only the asks listed (and whose members were reached) before the writes; one answered meanwhile is not claimed.
            var also = others.Where(o => Exec(c, tx, Close + " AND id = $id;", Args($"covered by network request {id}", o.Id)) > 0).ToList();
            tx.Commit();
            closed = [ask, .. also];
        }
        Changed?.Invoke();

        var told = new JsonArray();
        if (allow)
        {
            // Each affected team's Manager: "<host> is now allowed for <member>; retry what was blocked".
            foreach (var team in closed.GroupBy(a => a.TeamId))
            {
                var first = team.First();
                if (ManagerOf(team.Key) is not { } manager) continue;
                var names = string.Join(" and ", team.Select(a => a.MemberName).Distinct(StringComparer.OrdinalIgnoreCase));
                var scope = choice switch
                {
                    NetworkAskChoices.Team => $" The person allowed it for every member of {first.Team}.",
                    NetworkAskChoices.Everywhere => " The person allowed it for every agent on this computer.",
                    _ => "",
                };
                var text = $"{ask.Host} is now allowed for {names}; retry what was blocked.{scope} It applies to runs that start from now on.";
                var causation = team.Select(a => a.CausationSeq).FirstOrDefault(s => s is not null);
                if (await TellAsync(first.Team, manager, text, causation, ct)) told.Add($"{first.Team}/{manager}");
            }
        }
        else
        {
            var text = $"The person denied network access to {ask.Host} (network request {id}){(cleanNote is null ? "" : $": {cleanNote}")}. " +
                       $"It is not asked again for you for 24 hours. Do not try another site or a way around it: carry on without it, or hand back blocked on {ask.Host}.";
            if (await TellAsync(ask.Team, ask.MemberName, text, ask.CausationSeq, ct)) told.Add(ask.Member);
        }
        return new JsonObject
        {
            ["ok"] = true, ["id"] = id, ["host"] = ask.Host, ["choice"] = choice,
            ["closed"] = new JsonArray(closed.Select(a => (JsonNode?)a.Id).ToArray()), ["told"] = told,
        };
    }

    /// <summary>Tells, in the ask's workflow when it has one (else a new one); false when the member or team is gone.</summary>
    private async Task<bool> TellAsync(string team, string member, string text, long? causation, CancellationToken ct)
    {
        try
        {
            await tell(team, member, text, causation, ct);
            return true;
        }
        catch (BusRefusal) when (causation is not null)
        {
            try
            {
                await tell(team, member, text, null, ct);
                return true;
            }
            catch (BusRefusal) { return false; }
        }
        catch (BusRefusal) { return false; }
    }

    // ------------------------------------------------------------------ the store

    private Who? Find(string team, string member)
    {
        using var c = Open();
        using var cmd = Command(c, "SELECT m.id, t.id, t.name, m.name FROM members m JOIN teams t ON t.id = m.team_id WHERE t.name = $t AND m.name = $m;", ("$t", team), ("$m", member));
        using var r = cmd.ExecuteReader();
        return r.Read() ? new Who(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3)) : null;
    }

    private List<long> TeamMembers(long teamId)
    {
        using var c = Open();
        using var cmd = Command(c, "SELECT id FROM members WHERE team_id = $t ORDER BY id;", ("$t", teamId));
        using var r = cmd.ExecuteReader();
        var list = new List<long>();
        while (r.Read()) list.Add(r.GetInt64(0));
        return list;
    }

    private string? ManagerOf(long teamId) =>
        Scalar(null, "SELECT name FROM members WHERE team_id = $t AND role = 'manager' LIMIT 1;", ("$t", teamId)) as string;

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(connectionString);
        c.Open();
        return c;
    }

    private static SqliteCommand Command(SqliteConnection c, string sql, params (string Name, object? Value)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    private object? Scalar(SqliteConnection? within, string sql, params (string Name, object? Value)[] args)
    {
        var c = within ?? Open();
        try
        {
            using var cmd = Command(c, sql, args);
            var v = cmd.ExecuteScalar();
            return v is DBNull ? null : v;
        }
        finally
        {
            if (within is null) c.Dispose();
        }
    }

    private static int Exec(SqliteConnection c, SqliteTransaction tx, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = Command(c, sql, args);
        cmd.Transaction = tx;
        return cmd.ExecuteNonQuery();
    }

    private static string? S(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static string Stamp(DateTimeOffset t) => t.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private static long? OptLong(JsonObject? p, string name) =>
        p?[name] is JsonValue v && (v.TryGetValue<long>(out var l) || (v.TryGetValue<string>(out var s) && long.TryParse(s, CultureInfo.InvariantCulture, out l))) ? l : null;

    private static string? OptStr(JsonObject? p, string name) =>
        p?[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
}

/// <summary>
/// The production <see cref="INetworkPolicy"/>: the policy store, with each member's run layout for its checks. A host for
/// an agent goes in its loosen block, confirmed as exactly that one host (the click is the confirmation); a host for
/// everywhere goes in the global network.allowHosts, checked against a sample member's run.
/// </summary>
public sealed class StoreNetworkPolicy(PolicyStore store, Func<long, CancellationToken, Task<RunLayout>> layoutFor, Func<CancellationToken, Task<RunLayout?>> sample)
    : INetworkPolicy
{
    public bool AllowedFor(long memberId, string host) => EffectiveHosts(store.GetGlobal(), store.GetMember(memberId)).Contains(host, StringComparer.OrdinalIgnoreCase);

    /// <summary>network.allowHosts as <see cref="PolicyMerger.Merge"/> computes it: the global list, narrowed by an override, plus the loosen block.</summary>
    public static IReadOnlyList<string> EffectiveHosts(GlobalPolicy global, MemberPolicy member)
    {
        var hosts = global.Network.AllowHosts.ToList();
        if (member.Override.Network?.AllowHosts is { } narrowed) hosts = hosts.Where(h => narrowed.Contains(h, StringComparer.Ordinal)).ToList();
        hosts.AddRange(member.Loosen.Network?.AllowHosts ?? []);
        return hosts.Where(h => FloorRules.HostProblem(h) is null).Distinct(StringComparer.Ordinal).ToList();
    }

    public async Task AllowForMemberAsync(long memberId, string host, CancellationToken ct)
    {
        var current = store.GetMember(memberId);
        var hosts = (current.Loosen.Network?.AllowHosts ?? []).ToList();
        if (hosts.Contains(host, StringComparer.Ordinal)) return;
        hosts.Add(host);
        var loosen = current.Loosen with { Network = new NetworkPatch(hosts) };
        var layout = await layoutFor(memberId, ct);
        var before = store.PreviewLoosen(memberId, current.Loosen, layout);
        var after = store.PreviewLoosen(memberId, loosen, layout);
        // The person confirmed this host only; what the block loosened before was confirmed when it was saved.
        var added = after.Except(before, StringComparer.Ordinal).ToList();
        if (added.Any(a => a != $"network.allowHosts + {host}"))
            throw new PolicyException([$"adding {host} would loosen more than that host: {string.Join(", ", added)}"]);
        store.SaveLoosen(memberId, loosen, layout, after);
    }

    public async Task AllowEverywhereAsync(string host, CancellationToken ct)
    {
        var global = store.GetGlobal();
        if (global.Network.AllowHosts.Contains(host, StringComparer.Ordinal)) return;
        store.SaveGlobal(global with { Network = new NetworkPolicy([.. global.Network.AllowHosts, host]) }, await sample(ct));
    }
}
