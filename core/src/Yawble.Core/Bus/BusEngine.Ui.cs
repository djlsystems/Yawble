using Yawble.Core.Agents.Profiles;
using Yawble.Core.Policy;
using Yawble.Core.Scheduling;

namespace Yawble.Core.Bus;

/// <summary>One team in the team list: its running and queued counts.</summary>
public sealed record TeamSummary(long Id, string Name, string? RepoPath, int Members, int Running, int Queued, bool Paused = false, bool Archived = false);

/// <summary>The header: global running / limit, and how many wait for a slot across all teams.</summary>
public sealed record RunSlots(int Running, int Limit, int Queued);

/// <summary>One agent card: the member, its state, queue position and the last run's outcome. Interactive: the
/// member holds or waits for an interactive terminal session (not a headless run), so the pane's ✕ may end it.</summary>
public sealed record AgentCard(
    MemberRow Member, string State, int? QueuePosition, int QueueLength, int QueuedInstructions, string? StatusLine,
    long? RunId, string? RunStartedAt, string? LastRunState, string? LastRunReason,
    string? Lease = null, string? LeaseWhat = null, int? LeasePosition = null,
    bool Interactive = false);

/// <summary>The UI's operations on the bus. Every one goes through the same gate as the pump.</summary>
public sealed partial class BusEngine
{
    public async Task<long> CreateTeamAsync(string name, string managerName, string managerCli, string? repoPath, CancellationToken ct = default)
    {
        MemberRow? manager = null;
        var id = await WithGate(() => _store.InTransaction(() =>
        {
            if (string.IsNullOrWhiteSpace(name) || name.Contains('/')) throw new BusRefusal("bad_name", "a team name is non-empty and has no '/'");
            if (_store.Teams().Any(t => string.Equals(t.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)))
                throw new BusRefusal("exists", $"there is already a team named '{name.Trim()}'");
            var team = _store.CreateTeam(name.Trim());
            var m = AddMember(team, managerName, MemberRole.Manager, CheckCli(managerCli));
            _store.SetTeamRepo(team, string.IsNullOrWhiteSpace(repoPath) ? null : repoPath.Trim());
            manager = _store.MemberRows(memberId: m.Id).Single();
            return team;
        }), ct);
        LogFirstAgent(manager!, Agents.Profiles.AgentChangeLog.HowTeamCreate);
        return id;
    }

    /// <summary>Adds a member. <paramref name="how"/> is what its first agent_changes row says (team_create when it comes
    /// with a new team; otherwise settings, or the Concierge's tool when the Concierge asked).</summary>
    public async Task<MemberRow> AddMemberAsync(long teamId, string name, string cli, CancellationToken ct = default, string? how = null)
    {
        var row = await AddMemberCoreAsync(teamId, name, cli, ct);
        LogFirstAgent(row, how ?? HowNow());
        return row;
    }

    private static string HowNow() => Provenance.Actor.Current?.Via switch
    {
        Provenance.Actor.Concierge => Agents.Profiles.AgentChangeLog.HowConciergeTool,
        Provenance.Actor.External => Agents.Profiles.AgentChangeLog.HowCompanion, // the yawble companion's team_agent_change
        _ => Agents.Profiles.AgentChangeLog.HowSettings,
    };

    /// <summary>The change log: a new Manager's or member's first agent and model, one agent_changes row (from nothing).
    /// Written after the transaction, never inside it.</summary>
    private void LogFirstAgent(MemberRow m, string how, Provenance.Actor? actor = null)
    {
        if (AgentChanges is not { } changes) return;
        var manager = m.Role == "manager";
        changes.RecordSubjectChange(manager ? Agents.Profiles.AgentChangeLog.KindManagerAgent : Agents.Profiles.AgentChangeLog.KindMemberAgent,
            manager ? Agents.Profiles.AgentChangeLog.ManagerSubject(m.Team) : Agents.Profiles.AgentChangeLog.MemberSubject(m.Team, m.Name),
            new Agents.Profiles.AgentSetting("", ""), new Agents.Profiles.AgentSetting(m.Cli, m.Model), how, actor);
    }

    private Task<MemberRow> AddMemberCoreAsync(long teamId, string name, string cli, CancellationToken ct) =>
        WithGate(() => _store.InTransaction(() =>
        {
            if (_store.Teams().All(t => t.Id != teamId)) throw new BusRefusal("no_such_team", $"there is no team {teamId}");
            string id;
            try { id = Teams.MemberNames.IdFor(name); }
            catch (ArgumentException e) { throw new BusRefusal("bad_name", e.Message); }
            if (_store.MemberRows(teamId).Any(m => string.Equals(m.Name, id, StringComparison.OrdinalIgnoreCase)))
                throw new BusRefusal("exists", $"the team already has a member '{id}'");
            var m = AddMember(teamId, id, MemberRole.Member, CheckCli(cli));
            return _store.MemberRows(memberId: m.Id).Single();
        }), ct);

    /// <summary>Removes an agent. Refused for the Manager and while it runs; its queued work is dropped with it.</summary>
    public async Task RemoveMemberAsync(long memberId, CancellationToken ct = default)
    {
        var closedAsks = await WithGate(() => _store.InTransaction(() =>
        {
            var member = _store.Member(memberId) ?? throw new BusRefusal("no_such_member", $"there is no member {memberId}");
            if (member.Role == MemberRole.Manager) throw new BusRefusal("not_allowed", "a team's Manager cannot be removed");
            if (_active.Values.Any(a => a.Context.Member.Id == memberId) || HasSession(memberId))
                throw new BusRefusal("running", $"{member.Name} is running; stop it first");
            var closed = _store.DeleteMember(member);
            _statusLines.Remove(memberId);
            return closed;
        }), ct);
        // Its waiting network asks closed with it: Needs you refreshes.
        if (closedAsks > 0) NetworkAsks?.Changed?.Invoke();
    }

    public async Task<MemberRow> UpdateMemberAsync(long memberId, string cli, string instructions, string model, string credential, CancellationToken ct = default)
    {
        var before = _store.MemberRows(memberId: memberId).SingleOrDefault();
        var after = await UpdateMemberCoreAsync(memberId, cli, instructions, model, credential, ct);
        // The change log: one agent_changes row when the agent or model changed (written after the transaction, never inside it).
        if (before is not null && AgentChanges is { } changes)
        {
            var kind = before.Role == "manager" ? Agents.Profiles.AgentChangeLog.KindManagerAgent : Agents.Profiles.AgentChangeLog.KindMemberAgent;
            var subject = before.Role == "manager" ? Agents.Profiles.AgentChangeLog.ManagerSubject(before.Team) : Agents.Profiles.AgentChangeLog.MemberSubject(before.Team, before.Name);
            var how = HowNow();
            changes.RecordSubjectChange(kind, subject, new Agents.Profiles.AgentSetting(before.Cli, before.Model), new Agents.Profiles.AgentSetting(after.Cli, after.Model), how);
        }
        return after;
    }

    /// <summary>Where member and Manager agent or model changes are logged (set by the app; null when nothing looks).</summary>
    public Agents.Profiles.AgentChangeLog? AgentChanges { get; set; }

    private Task<MemberRow> UpdateMemberCoreAsync(long memberId, string cli, string instructions, string model, string credential, CancellationToken ct) =>
        WithGate(() => _store.InTransaction(() =>
        {
            var row = _store.MemberRows(memberId: memberId).SingleOrDefault()
                ?? throw new BusRefusal("no_such_member", $"there is no member {memberId}");
            cli = CheckCli(cli);
            credential = string.IsNullOrWhiteSpace(credential) ? "default" : credential;
            var allowed = AgentResolver.Resolve(cli).Cli.Id() switch
            {
                "claude" => new[] { "default", "subscription", "apiKey" },
                "grok" => ["default", "login", "apiKey"],
                _ => ["default"], // the CLI's own sign-in on this computer, nothing else
            };
            if (!allowed.Contains(credential))
                throw new BusRefusal("bad_credential", $"'{credential}' is not a credential mode for {cli} (one of: {string.Join(", ", allowed)})");
            _ = row;
            _store.UpdateMember(memberId, cli, instructions ?? "", (model ?? "").Trim(), credential);
            return _store.MemberRows(memberId: memberId).Single();
        }), ct);

    public Task<IReadOnlyList<TeamSummary>> TeamsAsync(CancellationToken ct = default) =>
        WithGate<IReadOnlyList<TeamSummary>>(() =>
        {
            var queue = _store.Queue();
            var members = _store.MemberRows();
            return _store.Teams().Select(t =>
            {
                var ids = members.Where(m => m.TeamId == t.Id).Select(m => m.Id).ToHashSet();
                return new TeamSummary(t.Id, t.Name, t.RepoPath, ids.Count,
                    // An interactive session holds a run slot, so it counts as running.
                    _active.Values.Count(a => ids.Contains(a.Context.Member.Id)) + _sessions.Keys.Count(ids.Contains),
                    queue.Count(q => ids.Contains(q.MemberId)), t.Paused, t.Archived);
            }).ToArray();
        }, ct);

    public Task<RunSlots> RunSlotsAsync(CancellationToken ct = default) =>
        WithGate(() => new RunSlots(SlotsInUse, _options.MaxConcurrentAgents(), _store.Queue().Count), ct);

    public Task<IReadOnlyList<AgentCard>> AgentCardsAsync(long teamId, CancellationToken ct = default) =>
        WithGate<IReadOnlyList<AgentCard>>(() =>
        {
            var queue = _store.Queue();
            var unstarted = _store.Pending(started: false);
            var runs = _store.Runs();
            TidyLease(_store.Clock.GetUtcNow());
            return _store.MemberRows(teamId).Select(m =>
            {
                var lease = LeaseOf(m.Id);
                var active = _active.Values.FirstOrDefault(a => a.Context.Member.Id == m.Id);
                var position = RunScheduler.PositionOf(queue, q => q.MemberId == m.Id);
                var qualified = $"{m.Team}/{m.Name}";
                var last = runs.Where(r => r.MemberId == m.Id).MaxBy(r => r.Id);
                return new AgentCard(m,
                    // A live interactive session holds a slot: the card shows it running (and Stop ends it).
                    active is not null || _sessions.ContainsKey(m.Id) ? "running" : position is not null ? "queued" : "idle",
                    position, queue.Count,
                    unstarted.Count(p => string.Equals(p.Subscriber, qualified, StringComparison.OrdinalIgnoreCase)),
                    _statusLines.GetValueOrDefault(m.Id),
                    active?.Context.RunId,
                    active is null ? null : last?.StartedAt,
                    last?.State, last?.Reason,
                    lease?.State, lease?.What, lease?.Position,
                    HasSession(m.Id));
            }).ToArray();
        }, ct);

    public Task<MemberRow?> MemberAsync(long memberId, CancellationToken ct = default) =>
        WithGate(() => _store.MemberRows(memberId: memberId).SingleOrDefault(), ct);

    public Task<IReadOnlyList<MemberRow>> AllMemberRowsAsync(CancellationToken ct = default) =>
        WithGate(() => _store.MemberRows(), ct);

    public Task<IReadOnlyList<Message>> MessagesFromAsync(long after, int limit, CancellationToken ct = default) =>
        WithGate(() => _store.MessagesFrom(after, limit), ct);

    public Task<(long End, IReadOnlyList<Message> Messages)> MessagesPageAsync(long after, int limit, CancellationToken ct = default) =>
        WithGate(() => (_store.MaxSeq(), _store.MessagesPage(after, limit)), ct);

    public Task<IReadOnlyDictionary<string, Message>> LatestReportsAsync(string team, CancellationToken ct = default) =>
        WithGate(() => _store.LatestReports(team), ct);

    public Task<IReadOnlyList<Message>> OpenDecisionsAsync(string team, CancellationToken ct = default) =>
        WithGate(() => _store.OpenDecisions(team), ct);

    /// <summary>The newest message's seq (0 when the log is empty): where a new Concierge watch starts.</summary>
    public Task<long> MaxSeqAsync(CancellationToken ct = default) => WithGate(() => _store.MaxSeq(), ct);

    /// <summary>The messages of these types after <paramref name="after"/>, and the end of the log they were read up to.</summary>
    public Task<(long End, IReadOnlyList<Message> Messages)> MessagesSinceAsync(long after, IReadOnlyCollection<string> types, CancellationToken ct = default) =>
        WithGate(() =>
        {
            var end = _store.MaxSeq();
            return (end, end <= after ? (IReadOnlyList<Message>)[] : _store.MessagesAfter(after, end, types));
        }, ct);

    /// <summary>One message by seq (a workflow's root instruction names the team it was given to).</summary>
    public Task<Message?> FindMessageAsync(long seq, CancellationToken ct = default) => WithGate(() => _store.Find(seq), ct);

    /// <summary>A team's latest messages, oldest first (the Concierge's team_activity).</summary>
    public Task<IReadOnlyList<Message>> RecentTeamMessagesAsync(string team, int limit, CancellationToken ct = default) =>
        WithGate(() => _store.RecentTeamMessages(team, limit), ct);

    /// <summary>The active run of a member, if any (the terminal window attaches to it).</summary>
    public Task<long?> ActiveRunOfAsync(long memberId, CancellationToken ct = default) =>
        WithGate(() => _active.Values.FirstOrDefault(a => a.Context.Member.Id == memberId)?.Context.RunId, ct);

    /// <summary>
    /// Stop: <see cref="StopMemberAsync(long, CancellationToken)"/>. Cancels the member's active
    /// run (recorded failed, class "stopped") or its interactive session, or withdraws a session still waiting for a
    /// slot, and gives the slot to the next queued entry. The launcher owns ending the sandbox tree.
    /// </summary>
    public Task<bool> StopMemberRunAsync(long memberId, CancellationToken ct = default) => StopMemberAsync(memberId, ct);

    /// <summary>Resolves custom agent names (a member's cli may be one); set by the app, null = built-in CLI ids only.</summary>
    public Agents.Profiles.IAgentResolver? AgentResolver { get; set; }

    /// <summary>The stored form of an agent name (a built-in's id or a custom agent's name), or a refusal naming what is accepted.</summary>
    private string CheckCli(string cli) => AgentResolver.Canonical(cli ?? "")
        ?? throw new BusRefusal("bad_cli", $"'{cli}' is not an agent ({string.Join(", ", AgentResolver?.Names() ?? AgentClis.Ids)})");

    /// <summary>
    /// The Manager hires a member into its own team: a new agent with its name, CLI and standing
    /// instructions (its job on the team). The name must be free in the team and usable as a folder name; the CLI is
    /// the Manager's own when none is given. Only a Manager may hire, and only into its own team.
    /// </summary>
    internal async Task<MemberRow> HireAsync(RunContext run, string name, string? cli, string? instructions, CancellationToken ct)
    {
        var row = await HireCoreAsync(run, name, cli, instructions, ct);
        LogFirstAgent(row, Agents.Profiles.AgentChangeLog.HowManagerHire, new Provenance.Actor(Provenance.Actor.Agent, run.Member.Qualified));
        return row;
    }

    private Task<MemberRow> HireCoreAsync(RunContext run, string name, string? cli, string? instructions, CancellationToken ct) =>
        WithGate(() => _store.InTransaction(() =>
        {
            if (run.Member.Role != MemberRole.Manager) throw new BusRefusal("not_allowed", "only the Manager hires members");
            string trimmed;
            try
            {
                trimmed = Teams.MemberNames.IdFor(name);
            }
            catch (ArgumentException e)
            {
                throw new BusRefusal("bad_name", e.Message + "; name them for the job, a space, then a first name, e.g. 'Developer Priya'");
            }
            if (_store.MemberRows(run.Member.TeamId).Any(m => string.Equals(m.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
                throw new BusRefusal("exists", $"the team already has a member named '{trimmed}'; pick another name, or tell that member");
            var own = _store.MemberRows(memberId: run.Member.Id).Single().Cli;
            var chosen = CheckCli(string.IsNullOrWhiteSpace(cli) ? own : cli.Trim());
            var m = AddMember(run.Member.TeamId, trimmed, MemberRole.Member, chosen);
            if (!string.IsNullOrWhiteSpace(instructions)) _store.UpdateMember(m.Id, chosen, instructions.Trim(), "", "default");
            Log($"{run.Member.Qualified} hired {m.Qualified} ({chosen})");
            return _store.MemberRows(memberId: m.Id).Single();
        }), ct);

    /// <summary>What the Manager may hire: every preset, with whether it can run on this computer now.</summary>
    internal async Task<IReadOnlyList<AgentChoice>> HiringAsync(CancellationToken ct)
    {
        var builtIns = _options.AvailableAgents is { } detect
            ? await detect(ct)
            : AgentClis.Ids.Select(id => new AgentChoice(id, true, "not checked")).ToList();
        return WithCustomAgents(builtIns);
    }

    /// <summary>The custom agents follow their base CLI's readiness (detection and sign-in are the base CLI's).</summary>
    private IReadOnlyList<AgentChoice> WithCustomAgents(IReadOnlyList<AgentChoice> builtIns)
    {
        if (AgentResolver is null) return builtIns;
        var all = builtIns.ToList();
        foreach (var name in AgentResolver.Names())
            if (!all.Any(c => string.Equals(c.Cli, name, StringComparison.OrdinalIgnoreCase))
                && builtIns.FirstOrDefault(c => c.Cli == AgentResolver.Resolve(name).Cli.Id()) is { } b)
                all.Add(b with { Cli = name });
        return all;
    }
}
