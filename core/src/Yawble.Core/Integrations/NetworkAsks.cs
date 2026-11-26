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
/// <param name="Scope">How it was allowed: agent, team or everywhere; null while pending, when denied or closed.</param>
/// <param name="State">pending, allowed, denied, or closed (the member was removed from the team while it waited).</param>
/// <param name="MemberId">The asking member's id (0041); null for an ask from before it whose member is gone.</param>
public sealed record NetworkAskRow(long Id, string At, long TeamId, string Team, string Member, string Host, string? Reason, string? Method,
    string? PathSample, int Count, string LastSeen, long? Deliverable, long? CausationSeq, string State, string? Scope, string? DecidedBy,
    string? DecidedVia, string? DecidedAt, string? Note, long? MemberId = null)
{
    public string MemberName => Member[(Member.IndexOf('/') + 1)..];

    /// <summary>The refusal was a plain-http request (not CONNECT, not request_host): only https is forwarded, even once the host is allowed.</summary>
    public bool PlainHttp => Method is { Length: > 0 } m && !string.Equals(m, "CONNECT", StringComparison.OrdinalIgnoreCase);
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

/// <summary>
/// The policy changes a network ask makes, through the policy store (its checks): an exact host, or the domain allow
/// the person chose ("*.digicert.com", only ever <see cref="NetworkAskService.DomainPattern"/>).
/// </summary>
public interface INetworkPolicy
{
    /// <summary>Whether the host is on the member's effective allowlist for its next run.</summary>
    bool AllowedFor(long memberId, string host);

    /// <summary>Adds the host to the member's loosen block.</summary>
    Task AllowForMemberAsync(long memberId, string host, CancellationToken ct);

    /// <summary>Adds the host to the team's network allow list (team_network_allows), in one write: every member's run has it, later hires too.</summary>
    Task AllowForTeamAsync(long teamId, string host, CancellationToken ct);

    /// <summary>Adds the host to the global network.allowHosts.</summary>
    Task AllowEverywhereAsync(string host, CancellationToken ct);
}

/// <summary>Tells a member something as the person (the bus's PersonTellAsync): joins the deliverable of <paramref name="causation"/> when given.</summary>
public delegate Task NetworkAskTell(string team, string member, string text, long? causation, CancellationToken ct);

/// <summary>
/// Network asks: a run's gateway refusing a host that is not on the agent's allowlist, or the agent's request_host tool,
/// raises one pending ask per (team, member, host); repeats only count up. The person answers in Needs you: allow it for
/// this agent (its loosen block), for the team (the team's network allow list, which later hires get too), everywhere (the
/// global allowlist), or deny it (not asked again for that member, by its id, for 24 hours). An allow is for the host, or
/// (when <see cref="DomainPattern"/> offers one) for "*.&lt;registrable domain&gt;", which closes every waiting ask it covers
/// at that scope. One member's waiting asks with one registrable domain are one item in Needs you, answered together
/// (<see cref="AnswerGroupAsync"/>); the per-member cap still counts hosts. The policy store makes every
/// change; nothing is allowed without the person's answer. After an allow the team's Manager is told to retry; after a
/// deny the agent is told. A member removed from the team has its waiting asks closed (BusStore.DeleteMember). Each ask
/// and its answer is in the Approvals log (Logs/LogSources.cs).
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

    /// <summary>
    /// Looks a name up (production: the system resolver). Called only when the person answers with an allow, once per
    /// answer: an allowed name that resolves only to loopback, private or link-local addresses would never be reachable
    /// (the gateway refuses those addresses), so that answer is refused and the ask stays for Deny. Never called for an
    /// agent's request, so an agent cannot make the app look up names it chooses. Null: no check.
    /// </summary>
    public Func<string, CancellationToken, Task<System.Net.IPAddress[]>>? Resolve { get; set; }

    /// <summary>How long the answer's lookup may take before the allow goes ahead without it.</summary>
    public static readonly TimeSpan ResolveLimit = TimeSpan.FromSeconds(5);

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

    /// <summary>The request_host tool: the same ask, with the agent's reason and its deliverable.</summary>
    public string RequestFromAgent(Bus.MemberRef member, string host, string reason, long deliverable, long causation)
    {
        // A URL is read as its host (https://api.nuget.org/v3/index.json → api.nuget.org); anything else is checked as given.
        var h = NetworkAskFilter.Normalize(host.Contains("://", StringComparison.Ordinal) && Uri.TryCreate(host.Trim(), UriKind.Absolute, out var uri) && uri.Host.Length > 0
            ? uri.Host : host);
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("give a reason: one line on what you need the host for");
        var who = new Who(member.Id, member.TeamId, member.Team, member.Name);
        var r = Raise(who, h, Clip(reason.Trim(), 500), null, null, deliverable, causation);
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

    private Raised Raise(Who who, string host, string? reason, string? method, string? sample, long? deliverable, long? causation)
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
            // The 24-hour deny is the member's, by its id: a later hire with the same name is asked again.
            if (Scalar(c, "SELECT decided_at FROM network_asks WHERE member_id = $id AND host = $h AND state = 'denied' ORDER BY decided_at DESC LIMIT 1;",
                    ("$id", who.MemberId), ("$h", h)) is string denied
                && DateTimeOffset.TryParse(denied, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var deniedAt)
                && now - deniedAt < DenyQuiet)
                return new Raised(HostAskOutcome.Denied, null, h, null, deniedAt + DenyQuiet);
            // A plain-http refusal's method wins over CONNECT's or none, so the card can say only https is forwarded.
            using var cmd = Command(c,
                """
                INSERT INTO network_asks (at, team_id, member, member_id, host, reason, method, path_sample, count, last_seen, deliverable, causation_seq)
                VALUES ($now, $t, $m, $mid, $h, $r, $method, $p, 1, $now, $w, $c)
                ON CONFLICT (team_id, member, host) WHERE state = 'pending' DO UPDATE SET
                    count = count + 1, last_seen = excluded.last_seen,
                    member_id = COALESCE(member_id, excluded.member_id),
                    reason = COALESCE(excluded.reason, reason),
                    method = CASE WHEN excluded.method IS NOT NULL AND excluded.method <> 'CONNECT' AND (method IS NULL OR method = 'CONNECT')
                                  THEN excluded.method ELSE COALESCE(method, excluded.method) END,
                    path_sample = COALESCE(path_sample, excluded.path_sample),
                    deliverable = COALESCE(excluded.deliverable, deliverable), causation_seq = COALESCE(excluded.causation_seq, causation_seq)
                RETURNING id, count;
                """,
                ("$now", Stamp(now)), ("$t", who.TeamId), ("$m", who.Qualified), ("$mid", who.MemberId), ("$h", h), ("$r", reason), ("$method", method), ("$p", sample),
                ("$w", deliverable), ("$c", causation));
            using var r = cmd.ExecuteReader();
            r.Read();
            id = r.GetInt64(0);
            count = r.GetInt64(1);
        }
        // a repeat of the gateway's refusal changes only the count: no push for it (a restore loop would flood the window)
        if (count == 1 || reason is not null) Changed?.Invoke();
        return new Raised(count == 1 ? HostAskOutcome.Asked : HostAskOutcome.Pending, id, h);
    }

    private static async Task<bool> ResolvesOnlyPrivateAsync(Func<string, CancellationToken, Task<System.Net.IPAddress[]>> resolve, string host, CancellationToken ct)
    {
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(ResolveLimit);
            var addresses = await resolve(host, limit.Token).ConfigureAwait(false);
            return addresses.Length > 0 && addresses.All(Gateway.RunGateway.IsPrivate);
        }
        catch (Exception e) when (e is System.Net.Sockets.SocketException or OperationCanceledException or ArgumentException && !ct.IsCancellationRequested)
        {
            return false; // not resolvable (now): nothing says it is private
        }
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
            "SELECT a.id, a.at, a.team_id, COALESCE(t.name, ''), a.member, a.host, a.reason, a.method, a.path_sample, a.count, a.last_seen, a.deliverable, a.causation_seq, " +
            "a.state, a.scope, a.decided_by, a.decided_via, a.decided_at, a.note, a.member_id FROM network_asks a LEFT JOIN teams t ON t.id = a.team_id WHERE 1 = 1" +
            (pendingOnly ? " AND a.state = 'pending'" : "") + (teamId is null ? "" : " AND a.team_id = $t") + " ORDER BY a.id;",
            ("$t", teamId));
        using var r = cmd.ExecuteReader();
        var list = new List<NetworkAskRow>();
        while (r.Read())
            list.Add(new NetworkAskRow(r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetString(3), r.GetString(4), r.GetString(5), S(r, 6), S(r, 7), S(r, 8),
                (int)r.GetInt64(9), r.GetString(10), r.IsDBNull(11) ? null : r.GetInt64(11), r.IsDBNull(12) ? null : r.GetInt64(12), r.GetString(13), S(r, 14),
                S(r, 15), S(r, 16), S(r, 17), S(r, 18), r.IsDBNull(19) ? null : r.GetInt64(19)));
        return list;
    }

    /// <summary>One ask as the Needs you item reads it (network.asks).</summary>
    public static JsonObject ToJson(NetworkAskRow a) => new()
    {
        ["id"] = a.Id, ["at"] = a.At, ["teamId"] = a.TeamId, ["team"] = a.Team, ["member"] = a.Member, ["memberName"] = a.MemberName,
        ["host"] = a.Host, ["reason"] = a.Reason, ["method"] = a.Method, ["pathSample"] = a.PathSample, ["count"] = a.Count, ["lastSeen"] = a.LastSeen,
        ["hint"] = NetworkAskFilter.Hint(a.Host), ["deliverable"] = a.Deliverable, ["state"] = a.State, ["scope"] = a.Scope,
        ["decidedBy"] = a.DecidedBy, ["decidedAt"] = a.DecidedAt,
        // A plain-http refusal: the card says only https is forwarded, even once the host is allowed.
        ["plainHttp"] = a.PlainHttp,
        ["choices"] = new JsonArray(NetworkAskChoices.All.Select(ch => (JsonNode?)ch).ToArray()),
        // Needs you shows one member's waiting asks with the same registrable domain as one item (null: the host alone).
        ["domain"] = PublicSuffix.RegistrableDomain(a.Host),
        // The domain allow offered with the ask ("All of *.digicert.com"); null when only the host may be allowed.
        ["domainPattern"] = DomainPattern(a.Host),
    };

    /// <summary>
    /// The domain allow that may be offered for an ask's host (<see cref="PublicSuffix.DomainPatternFor"/>), or null. Null
    /// too for the registrable domain itself (digicert.com): "*.digicert.com" never covers the apex.
    /// </summary>
    public static string? DomainPattern(string host) =>
        PublicSuffix.DomainPatternFor(host) is { } pattern && Covers(pattern, host) ? pattern : null;

    /// <summary>Whether an allowHosts entry (an exact host, or "*.example.com") covers the host, as the gateway matches it.</summary>
    internal static bool Covers(string entry, string host) => Vhosts.HostAllowed(host, [entry]);

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
                // [{ id, at, teamId, team, member, memberName, host, reason, method, pathSample, count, lastSeen, hint, deliverable, state, scope, decidedBy, decidedAt, plainHttp, choices, domain, domainPattern }]
                "network.asks" => new JsonArray(Asks(p?["all"]?.GetValue<bool>() != true, OptLong(p, "teamId")).Select(a => (JsonNode?)ToJson(a)).ToArray()),
                // { id, choice, note?, domain?: bool (allow the ask's "*.<domain>"), ids?: [..] (a grouped item: every listed ask) }
                "network.answer" => await AnswerGroupAsync(
                    OptLong(p, "id") ?? throw new ArgumentException("id is required"), OptLongs(p, "ids"),
                    OptStr(p, "choice") ?? throw new ArgumentException("choice is required (agent, team, everywhere or deny)"), OptStr(p, "note"),
                    p?["domain"] is JsonValue dv && dv.TryGetValue<bool>(out var d) && d, ct),
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
    /// <summary>
    /// The person's answer to a grouped item in Needs you: <paramref name="id"/> and the other asks in
    /// <paramref name="ids"/>, all waiting for one member with one registrable domain. Each is answered in turn with the
    /// same choice (an ask an earlier answer already covered is skipped), so Allow answers every listed host; the notices
    /// merge in the Manager's notice window. Deny denies every listed host and tells the member once, listing them all.
    /// With no other ids it is <see cref="AnswerAsync"/>.
    /// </summary>
    public async Task<JsonObject> AnswerGroupAsync(long id, IReadOnlyList<long> ids, string choice, string? note, bool domain, CancellationToken ct)
    {
        var rest = ids.Where(i => i != id).Distinct().ToList();
        if (rest.Count == 0) return await AnswerAsync(id, choice, note, ct, domain);
        var waiting = Asks();
        var first = waiting.FirstOrDefault(a => a.Id == id) ?? throw new ArgumentException($"network request {id} is not waiting");
        var key = PublicSuffix.RegistrableDomain(first.Host);
        foreach (var other in rest)
        {
            if (waiting.FirstOrDefault(a => a.Id == other) is not { } o) continue; // answered meanwhile: nothing to do
            if (!SameMember(o, first) || key is null || PublicSuffix.RegistrableDomain(o.Host) != key)
                throw new ArgumentException($"network request {other} is not one of {first.MemberName}'s requests for {key ?? first.Host}; answer it on its own");
        }

        // A Deny tells each member once, after every ask is answered, listing all of its denied hosts; an Allow's
        // notices already merge in the Manager's notice window.
        var deny = choice == NetworkAskChoices.Deny;
        var denied = new List<NetworkAskRow>();
        var result = await AnswerAsync(id, choice, note, ct, domain, tellDeny: !deny);
        if (deny) denied.Add(first);
        var closed = result["closed"]!.AsArray().Select(n => n!.GetValue<long>()).ToHashSet();
        var told = result["told"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        var allowed = new List<string> { result["allowed"]?.GetValue<string>() ?? first.Host };
        foreach (var other in rest)
        {
            if (closed.Contains(other) || Asks().FirstOrDefault(a => a.Id == other) is not { } row) continue;
            var r = await AnswerAsync(other, choice, note, ct, domain, tellDeny: !deny);
            if (deny) denied.Add(row);
            foreach (var c in r["closed"]!.AsArray()) closed.Add(c!.GetValue<long>());
            told.AddRange(r["told"]!.AsArray().Select(n => n!.GetValue<string>()));
            if (r["allowed"]?.GetValue<string>() is { } a) allowed.Add(a);
        }
        if (deny)
        {
            var cleanNote = CleanNote(note);
            foreach (var member in denied.GroupBy(a => (a.TeamId, Key: a.MemberId?.ToString() ?? a.Member)))
            {
                var asks = member.ToList();
                var one = asks[0];
                var causation = asks.Select(a => a.CausationSeq).FirstOrDefault(s => s is not null);
                if (await TellAsync(one.Team, one.MemberName, DeniedText(asks, cleanNote), causation, ct)) told.Add(one.Member);
            }
        }
        result["closed"] = new JsonArray(closed.Order().Select(c => (JsonNode?)c).ToArray());
        result["told"] = new JsonArray(told.Distinct().Select(t => (JsonNode?)t).ToArray());
        if (choice != NetworkAskChoices.Deny) result["allowed"] = string.Join(", ", allowed.Distinct());
        return result;
    }

    /// <summary>Two asks from the same member: by its id (0041), by name for an older ask without one.</summary>
    private static bool SameMember(NetworkAskRow a, NetworkAskRow b) =>
        a.TeamId == b.TeamId && (a.MemberId is { } x && b.MemberId is { } y ? x == y : string.Equals(a.Member, b.Member, StringComparison.Ordinal));

    /// <param name="domain">
    /// Allow the ask's domain ("*.digicert.com", <see cref="DomainPattern"/>) instead of the host alone; it closes every
    /// waiting ask it covers at the chosen scope. Refused when no domain allow may be offered for the host. Ignored for Deny.
    /// </param>
    public Task<JsonObject> AnswerAsync(long id, string choice, string? note, CancellationToken ct, bool domain = false) =>
        AnswerAsync(id, choice, note, ct, domain, tellDeny: true);

    /// <param name="tellDeny">False for a grouped Deny: <see cref="AnswerGroupAsync"/> tells the member once for the group.</param>
    private async Task<JsonObject> AnswerAsync(long id, string choice, string? note, CancellationToken ct, bool domain, bool tellDeny)
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
            return await AnswerClaimedAsync(id, choice, note, who, domain, tellDeny, ct);
        }
        finally
        {
            lock (_lock) _answering.Remove(id);
        }
    }

    private async Task<JsonObject> AnswerClaimedAsync(long id, string choice, string? note, Provenance.Actor who, bool domain, bool tellDeny, CancellationToken ct)
    {
        var ask = Asks().FirstOrDefault(a => a.Id == id) ?? throw new ArgumentException($"network request {id} is not waiting");
        if (NetworkAskFilter.Problem(ask.Host) is { } problem) throw new ArgumentException(problem);
        var allow = choice != NetworkAskChoices.Deny;
        // What is written: the host, or for a domain allow "*.<registrable domain>". The registrable domain itself (the
        // apex, which "*." never covers) is allowed as its host, so a grouped item's Allow still answers it.
        var target = ask.Host;
        if (allow && domain)
        {
            var pattern = PublicSuffix.DomainPatternFor(ask.Host)
                ?? throw new ArgumentException($"{ask.Host} cannot be allowed as a whole domain (a shared or vendor domain, or not a registrable one); allow the host itself");
            if (Covers(pattern, ask.Host)) target = pattern;
        }
        if (allow && Resolve is { } resolve && await ResolvesOnlyPrivateAsync(resolve, ask.Host, ct))
            throw new ArgumentException($"{ask.Host} resolves only to private or loopback addresses, which agents can never reach, so allowing it would change nothing; choose Deny");
        // The other waiting asks this answer covers, listed once before the policy writes: only these close with it,
        // so an ask raised while the writes run (its member never checked) stays waiting. The same rules for a host and a
        // domain allow: this member's asks for an agent answer, the team's for team, every team's for everywhere.
        List<NetworkAskRow> others = allow
            ? Asks().Where(a => a.Id != id && Covers(target, a.Host) && choice switch
            {
                NetworkAskChoices.Agent => SameMember(a, ask),
                NetworkAskChoices.Team => a.TeamId == ask.TeamId,
                _ => true,
            }).ToList()
            : [];
        if (allow)
        {
            var asker = Asker(ask) ?? throw new ArgumentException($"{ask.Member} is no longer on the team");
            switch (choice)
            {
                case NetworkAskChoices.Agent:
                    await policy.AllowForMemberAsync(asker.MemberId, target, ct);
                    break;
                case NetworkAskChoices.Team:
                    // One write: the team's list, which every member's run (and every later hire's) gets at launch.
                    await policy.AllowForTeamAsync(ask.TeamId, target, ct);
                    break;
                default:
                    await policy.AllowEverywhereAsync(target, ct);
                    break;
            }

            // The members this answer tells the Manager about: the asker, and each member whose waiting ask the answer
            // covers, each with the hosts it waited for. A member's own override can narrow the global list (it is an
            // intersection), so the global list alone does not reach it: that member also gets the entry in its loosen block.
            // The team's list is added after the override's narrowing, so a team answer never writes a loosen block.
            var reached = new List<(Who Member, List<string> Hosts)> { (asker, [ask.Host]) };
            foreach (var a in others)
            {
                if (Asker(a) is not { } m) continue;
                var at = reached.FindIndex(r => r.Member.MemberId == m.MemberId);
                if (at < 0) reached.Add((m, [a.Host]));
                else reached[at].Hosts.Add(a.Host);
            }
            if (choice != NetworkAskChoices.Team)
                foreach (var (m, hosts) in reached)
                    if (hosts.Any(h => !policy.AllowedFor(m.MemberId, h))) await policy.AllowForMemberAsync(m.MemberId, target, ct);
            // Never tell the Manager a host is allowed unless the member's effective hosts now include it.
            if (reached.Where(r => r.Hosts.Any(h => !policy.AllowedFor(r.Member.MemberId, h))).Select(r => r.Member.Qualified).ToList() is { Count: > 0 } missed)
                throw new PolicyException([$"{target} is still not allowed for {string.Join(", ", missed)} after the change; the request stays waiting"]);
        }

        var cleanNote = CleanNote(note);
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
            // Each affected team's Manager: "<host> is now allowed for <member>; retry what was blocked". Notices to one
            // Manager within NoticeWindow are gathered into one wake listing every host (AllowedText).
            foreach (var team in closed.GroupBy(a => a.TeamId))
            {
                var first = team.First();
                if (ManagerOf(team.Key) is not { } manager) continue;
                var names = string.Join(" and ", team.Select(a => a.MemberName).Distinct(StringComparer.OrdinalIgnoreCase));
                var causation = team.Select(a => a.CausationSeq).FirstOrDefault(s => s is not null);
                if (await NoticeAsync(team.Key, first.Team, manager, new AllowNotice(target, names, choice, first.Team, causation), ct))
                    told.Add($"{first.Team}/{manager}");
            }
        }
        else if (tellDeny)
        {
            if (await TellAsync(ask.Team, ask.MemberName, DeniedText([ask], cleanNote), ask.CausationSeq, ct)) told.Add(ask.Member);
        }
        var answer = new JsonObject
        {
            ["ok"] = true, ["id"] = id, ["host"] = ask.Host, ["choice"] = choice,
            ["closed"] = new JsonArray(closed.Select(a => (JsonNode?)a.Id).ToArray()), ["told"] = told,
        };
        // What the policy now lists: the host, or the domain allow ("*.digicert.com").
        if (allow) answer["allowed"] = target;
        return answer;
    }

    private static string? CleanNote(string? note) => string.IsNullOrWhiteSpace(note) ? null : Clip(note.Trim(), 500);

    /// <summary>
    /// What a member is told about a Deny: one ask ("The person denied network access to api.nuget.org (network request
    /// 7). It is not asked again ...") or a grouped item's asks, every host in one message.
    /// </summary>
    internal static string DeniedText(IReadOnlyList<NetworkAskRow> asks, string? note)
    {
        var hosts = JoinAnd(asks.Select(a => a.Host).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        var reason = note is null ? "" : $": {note}";
        if (asks.Count == 1)
            return $"The person denied network access to {hosts} (network request {asks[0].Id}){reason}. " +
                   $"It is not asked again for you for 24 hours. Do not try another site or a way around it: carry on without it, or hand back blocked on {hosts}.";
        return $"The person denied network access to {hosts} (network requests {JoinAnd(asks.Select(a => a.Id.ToString()).ToList())}){reason}. " +
               $"They are not asked again for you for 24 hours. Do not try another site or a way around them: carry on without them, or hand back blocked on {hosts}.";
    }

    // ------------------------------------------------------------------ "now allowed" notices

    /// <summary>
    /// How long "now allowed" notices to one team's Manager are gathered before it is told once, listing every host
    /// (one dotnet restore needed four hosts, and each allow woke the Manager). The window opens with the first
    /// notice; every notice to the same Manager before it closes joins it. Zero or less: each notice is told at once.
    /// </summary>
    public TimeSpan NoticeWindow { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Waits out the notice window; null: a timer on the service's clock. Tests give a fake one.</summary>
    public Func<TimeSpan, Task>? NoticeDelay { get; set; }

    /// <summary>One allow to tell a Manager about: the host, the members it was allowed for, and the answer's scope.</summary>
    internal sealed record AllowNotice(string Host, string Names, string Choice, string Team, long? Causation);

    private sealed record NoticeBatch(string Team, string Manager, List<AllowNotice> Items);

    // Open windows, by (team id, Manager); guarded by _lock.
    private readonly Dictionary<(long TeamId, string Manager), NoticeBatch> _notices = [];

    /// <summary>
    /// Tells the Manager about an allow, or adds it to the Manager's open window (opening one when none is). True when
    /// the Manager is told or will be at the window's end; false when it was told at once and is gone.
    /// </summary>
    private async Task<bool> NoticeAsync(long teamId, string team, string manager, AllowNotice notice, CancellationToken ct)
    {
        if (NoticeWindow <= TimeSpan.Zero) return await TellAsync(team, manager, AllowedText([notice]), notice.Causation, ct);
        var key = (teamId, manager.ToLowerInvariant());
        NoticeBatch batch;
        lock (_lock)
        {
            if (_notices.TryGetValue(key, out var open))
            {
                open.Items.Add(notice);
                return true;
            }
            _notices[key] = batch = new NoticeBatch(team, manager, [notice]);
        }
        _ = FlushLaterAsync(key, batch);
        return true;
    }

    private async Task FlushLaterAsync((long, string) key, NoticeBatch batch)
    {
        try { await (NoticeDelay ?? (w => Task.Delay(w, _clock)))(NoticeWindow).ConfigureAwait(false); }
        catch (Exception) { /* told now rather than never */ }
        List<AllowNotice> items;
        lock (_lock)
        {
            _notices.Remove(key);
            items = [.. batch.Items];
        }
        try
        {
            // The window outlives the answer, so the answer's token does not apply; it joins the first ask's deliverable that has one.
            await TellAsync(batch.Team, batch.Manager, AllowedText(items), items.Select(i => i.Causation).FirstOrDefault(c => c is not null), CancellationToken.None);
        }
        catch (Exception e) { Console.Error.WriteLine($"network asks: telling {batch.Team}/{batch.Manager} a host is allowed failed: {e.Message}"); }
    }

    /// <summary>
    /// What the Manager is told about one or more allows: "api.nuget.org is now allowed for Priya; retry what was
    /// blocked. It applies to runs that start from now on." Allows for the same members at the same scope share one
    /// sentence ("crl3.digicert.com and ocsp.digicert.com are now allowed for Priya; ..."), in the order they came.
    /// </summary>
    internal static string AllowedText(IReadOnlyList<AllowNotice> items)
    {
        var groups = items.GroupBy(i => (Names: i.Names.ToLowerInvariant(), i.Choice, i.Team)).Select(g =>
        {
            var hosts = g.Select(i => i.Host).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var it = hosts.Count == 1 ? "it" : "them";
            var scope = g.Key.Choice switch
            {
                NetworkAskChoices.Team => $" The person allowed {it} for every member of {g.Key.Team}.",
                NetworkAskChoices.Everywhere => $" The person allowed {it} for every agent on this computer.",
                _ => "",
            };
            return $"{JoinAnd(hosts)} {(hosts.Count == 1 ? "is" : "are")} now allowed for {g.First().Names}; retry what was blocked.{scope}";
        });
        var many = items.Select(i => i.Host).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;
        return $"{string.Join(" ", groups)} {(many ? "They apply" : "It applies")} to runs that start from now on.";
    }

    private static string JoinAnd(IReadOnlyList<string> parts) =>
        parts.Count == 1 ? parts[0] : $"{string.Join(", ", parts.Take(parts.Count - 1))} and {parts[^1]}";

    /// <summary>Tells, in the ask's deliverable when it has one (else a new one); false when the member or team is gone.</summary>
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

    /// <summary>The member who asked, by its id (0041); by name only for an older ask without one. Null when it has left the team.</summary>
    private Who? Asker(NetworkAskRow ask)
    {
        if (ask.MemberId is not { } id) return Find(ask.Team, ask.MemberName);
        using var c = Open();
        using var cmd = Command(c, "SELECT m.id, t.id, t.name, m.name FROM members m JOIN teams t ON t.id = m.team_id WHERE m.id = $id AND t.id = $t;", ("$id", id), ("$t", ask.TeamId));
        using var r = cmd.ExecuteReader();
        return r.Read() ? new Who(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3)) : null;
    }

    /// <summary>
    /// Closes the member's waiting asks when it is removed from the team (within the removal's transaction): state closed,
    /// with a note, so a later hire with the same name starts with none. Returns how many closed.
    /// </summary>
    public static int CloseForRemovedMember(SqliteConnection c, SqliteTransaction? tx, long memberId, long teamId, string qualified, string now)
    {
        using var cmd = Command(c,
            "UPDATE network_asks SET state = 'closed', decided_by = $by, decided_via = $via, decided_at = $now, note = $note " +
            "WHERE state = 'pending' AND (member_id = $id OR (member_id IS NULL AND team_id = $t AND member = $m));",
            ("$by", Provenance.Actor.Current?.User ?? Provenance.Actor.LocalUser), ("$via", Provenance.Actor.Current?.Via ?? Provenance.Actor.Host), ("$now", now),
            ("$note", $"closed: {qualified[(qualified.IndexOf('/') + 1)..]} was removed from the team"), ("$id", memberId), ("$t", teamId), ("$m", qualified));
        cmd.Transaction = tx;
        return cmd.ExecuteNonQuery();
    }

    private string? ManagerOf(long teamId) =>
        Scalar(null, "SELECT name FROM members WHERE team_id = $t AND role = 'manager' LIMIT 1;", ("$t", teamId)) as string;

    private SqliteConnection Open()
    {
        var c = Storage.Db.Open(connectionString);
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

    private static List<long> OptLongs(JsonObject? p, string name) =>
        p?[name] is JsonArray a
            ? a.Select(n => n is JsonValue v && v.TryGetValue<long>(out var l) ? l : throw new ArgumentException($"{name} is a list of request ids")).ToList()
            : [];

    private static string? OptStr(JsonObject? p, string name) =>
        p?[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
}

/// <summary>
/// The production <see cref="INetworkPolicy"/>: the policy store, with each member's run layout for its checks. A host for
/// an agent goes in its loosen block, confirmed as exactly that one host (the click is the confirmation); a host for the
/// team goes in the team's network allow list, checked against each current member's run, in one write; a host for
/// everywhere goes in the global network.allowHosts, checked against a sample member's run.
/// </summary>
public sealed class StoreNetworkPolicy(PolicyStore store, Func<long, CancellationToken, Task<RunLayout>> layoutFor, Func<CancellationToken, Task<RunLayout?>> sample)
    : INetworkPolicy
{
    /// <summary>Whether the member's run reaches the host, matched as the gateway matches it (a "*.example.com" entry covers its subdomains).</summary>
    public bool AllowedFor(long memberId, string host) =>
        Vhosts.HostAllowed(host, EffectiveHosts(store.GetGlobal(), store.GetMember(memberId), store.TeamHostsOfMember(memberId)));

    /// <summary>
    /// network.allowHosts as <see cref="PolicyMerger.Merge"/> computes it: the global list, narrowed by an override, plus
    /// the loosen block and the member's team's list (<paramref name="teamHosts"/>).
    /// </summary>
    public static IReadOnlyList<string> EffectiveHosts(GlobalPolicy global, MemberPolicy member, IReadOnlyList<string>? teamHosts = null)
    {
        var hosts = global.Network.AllowHosts.ToList();
        if (member.Override.Network?.AllowHosts is { } narrowed) hosts = hosts.Where(h => narrowed.Contains(h, StringComparer.Ordinal)).ToList();
        hosts.AddRange(member.Loosen.Network?.AllowHosts ?? []);
        hosts.AddRange(teamHosts ?? []);
        return hosts.Where(h => FloorRules.HostProblem(h) is null).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>How many times <see cref="AllowForTeamAsync"/> builds the member list again after the team's members changed under it before it refuses.</summary>
    public const int TeamMembersAttempts = 3;

    /// <summary>
    /// Adds the host to the team's list, checked against every current member's run. The list of members (with each one's
    /// run layout) is built here; when a member is hired or removed before the write, the store writes nothing and says
    /// so, and the list is built again - so a member hired mid-add is checked too. Refused after
    /// <see cref="TeamMembersAttempts"/> lists.
    /// </summary>
    public async Task AllowForTeamAsync(long teamId, string host, CancellationToken ct)
    {
        var who = Provenance.Actor.Current ?? Provenance.Actor.Window;
        for (var attempt = 1; ; attempt++)
        {
            var members = new List<(long, RunLayout)>();
            foreach (var id in store.TeamMemberIds(teamId)) members.Add((id, await layoutFor(id, ct)));
            try
            {
                store.AddTeamHost(teamId, host, members, who.User ?? Provenance.Actor.LocalUser, who.Via);
                return;
            }
            catch (TeamMembersChangedException) when (attempt < TeamMembersAttempts)
            {
                // a hire or removal landed after the list was built: build it again and check every member
            }
            catch (TeamMembersChangedException e)
            {
                throw new PolicyException([$"the team's members kept changing while {host} was checked; nothing was written, try again", .. e.Errors]);
            }
        }
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
