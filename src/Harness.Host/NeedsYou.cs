using System.Text.Json.Serialization;
using Harness.Containers;
using Harness.Host.Auth;
using Harness.Contracts;
using Harness.Host.Solutions;
using Harness.Kanban;

namespace Harness.Host;

/// <summary>The kinds of fact <c>GET /api/needs-you</c> lists, as the wire spells them.</summary>
public static class NeedsYouKinds
{
    public const string Decision = "decision";
    public const string Blocked = "blocked";
    public const string Failed = "failed";
    public const string WaitingInput = "waiting-input";
    public const string MergeReady = "merge-ready";
    public const string Stranded = "stranded";
    public const string Removal = "removal";
    public const string Connection = "connection";
}

/// <summary>
/// The connection a <c>connection</c> row is about - its id and name - and whom it holds up: each
/// team, by id and name, with the members of it using the connection.
/// </summary>
public sealed record NeedsYouConnection(string Id, string Name, IReadOnlyList<NeedsYouHeld> Holds);

/// <summary>One team a connection holds up, and its members held.</summary>
public sealed record NeedsYouHeld(NeedsYouTeam Team, IReadOnlyList<string> Members);

/// <summary>The team a row is about: its id and the name a person reads.</summary>
public sealed record NeedsYouTeam(string Id, string Name);

/// <summary>
/// What opens a row: <c>workflow</c>, <c>card</c>, <c>backlog</c>, <c>merge</c>, <c>solution</c>,
/// <c>removals</c> or <c>connections</c> (Admin → Connections), on <see cref="Team"/>, with the workflow,
/// card and item it names when it names one.
/// </summary>
public sealed record NeedsYouWhere(
    string View,
    string Team,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Workflow = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Card = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Item = null);

/// <summary>
/// One fact waiting on a person. <see cref="Words"/> are the platform's own words for it, never new
/// prose; <see cref="Since"/> is when the fact began, read off the row or record that holds it.
/// </summary>
public sealed record NeedsYouItem(
    string Kind,
    NeedsYouTeam Team,
    string? Member,
    string? Card,
    long? Workflow,
    WorkflowItem? Item,
    DateTimeOffset Since,
    string Words,
    NeedsYouWhere Where,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? NeedsDecisionSeq = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FailureClass = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] NeedsYouConnection? Connection = null);

/// <summary>
/// WHAT IS WAITING ON A PERSON, in one list: <c>GET /api/needs-you</c>. A read that decides nothing,
/// changes nothing and never runs git.
///
/// <para>
/// TWO PARTS, BY COST. The LIVE part - <c>decision</c>, <c>blocked</c>, <c>failed</c>,
/// <c>waiting-input</c> - is read from memory: each member's snapshot and the rows its marks were set
/// from (<see cref="MemberRuntime.Marks"/>), and each solution team's <c>missing</c>; the items those
/// workflows were dispatched for are named from one backlog read. The STORED part -
/// <c>merge-ready</c>, <c>stranded</c>, <c>removal</c> - is read from the stores and the landed CACHE
/// only (<see cref="BacklogLandedCache.Get"/>): a team whose verdict is not cached says nothing here
/// rather than being measured, so the git the Backlog list pays is never paid by this read.
/// </para>
/// </summary>
public static class NeedsYou
{
    public const string LivePart = "live";
    public const string StoredPart = "stored";

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/needs-you", async (
            HttpContext context,
            TeamRegistry teams, ContainerHost host, IBacklogStore backlog, IMessageLog log,
            KanbanStore kanban, BacklogLandedCache landedCache, UnfinishedRemovalRetry removals,
            ITeamSolutionStore solutions, SolutionInstaller installer, SolutionWait waiting, PluginSetup setup,
            string? part = null,
            CancellationToken ct = default) =>
        {
            if (part is not null and not LivePart and not StoredPart)
            {
                return Results.BadRequest(new { error = $"`part` is `{LivePart}`, `{StoredPart}` or omitted for both." });
            }

            var names = teams.All().ToDictionary(t => t.Id, t => t.Name, StringComparer.OrdinalIgnoreCase);
            var items = new List<NeedsYouItem>();

            if (part is null or LivePart)
            {
                items.AddRange(await LiveAsync(names, host, backlog, solutions, installer, waiting, ct, setup));
            }

            if (part is null or StoredPart)
            {
                items.AddRange(await StoredAsync(names, teams, host, backlog, log, kanban, landedCache, removals, ct));
            }

            return Results.Ok(new { items = Newest(items) });
        })
            .WithTags("Teams")
            .HumansOnly()
            .WithSummary("What is waiting on a person")
            .WithDescription(
                "One list of what is waiting on a person, newest first by `since`: `{ items: [...] }`. "
                + "Each item is `{ kind, team: { id, name }, member, card, workflow, item: { id, title } "
                + "or null, since, words, where: { view, team, workflow?, card?, item? } }`, with "
                + "`needsDecisionSeq` on a decision, `failureClass` on a failure and `connection: { id, name, "
                + "holds: [{ team: { id, name }, members }] }` on a connection. `kind` is `decision` "
                + "(a member's needs-decision mark), `blocked` (a member's last run published blocked), "
                + "`failed` (a member's last run failed; a member both blocked and failed is listed "
                + "failed), `waiting-input` (a solution team's `missing`), `merge-ready` (a backlog item "
                + "whose current dispatch workflow ended with `workflow.completed` and whose landed reads "
                + "`pushed`), `stranded` (an item carrying `stranded`) or `removal` (an unfinished "
                + "removal), or `connection` (a connection that needs a person - refused, or asking for a "
                + "sign-in - listed ONCE however many members it holds up, naming each team and member in "
                + "`connection.holds`; those members' own failures are not listed beside it; `team` is "
                + "the first team it holds up). `words` are the platform's own: the blocked reason, the "
                + "failure, the question, the missing input's sentence, the landed sentence, the stranded "
                + "notice, what the removal left, the connection's sentence. `where.view` is `workflow`, "
                + "`card`, `backlog`, `merge`, `solution`, `removals` or `connections`.\n\n"
                + "`part=live` answers only decision, blocked, failed, waiting-input and connection, from memory; "
                + "`part=stored` answers only merge-ready, stranded and removal, from the stores and the "
                + "landed cache - this route never fetches a remote and never runs git, so an item whose "
                + "team's landed is not cached is not listed until the Backlog or Git dialog has read "
                + "it. Omitted, both. Reading it decides and changes nothing. A quiet instance answers "
                + "an empty list.\n\n"
                + "**A person's read.**");
    }

    /// <summary>Newest first by <c>since</c>; ties in a fixed order, so two reads list alike.</summary>
    public static IReadOnlyList<NeedsYouItem> Newest(IEnumerable<NeedsYouItem> items) =>
        [.. items
            .OrderByDescending(item => item.Since)
            .ThenBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.Team.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Member ?? "", StringComparer.Ordinal)
            .ThenBy(item => item.Item?.Id ?? 0)];

    /// <summary>
    /// The live part. Each member's marks, from its snapshot and the rows behind them; each solution
    /// team's missing inputs, in the wait's own sentence. The item a mark's workflow was dispatched
    /// for is named from the backlog's current dispatches with their items' titles - one read for
    /// the whole list, and none when nothing is marked.
    /// </summary>
    public static async Task<IReadOnlyList<NeedsYouItem>> LiveAsync(
        IReadOnlyDictionary<string, string> names,
        ContainerHost host,
        IBacklogStore backlog,
        ITeamSolutionStore solutions,
        SolutionInstaller installer,
        SolutionWait waiting,
        CancellationToken ct = default,
        PluginSetup? setup = null)
    {
        var marked = new List<(ContainerSnapshot Snapshot, string Kind, MarkRow Row)>();
        var result = new List<NeedsYouItem>();

        // A CONNECTION THAT NEEDS A PERSON, ONCE, however many members on however many teams it holds
        // up - each named under it. Since is the earliest held run's failure, else when it was first seen.
        var holds = (setup?.Holds() ?? [])
            .Select(hold => (Hold: hold, Members: hold.Members.Where(m => names.ContainsKey(m.Team)).ToList()))
            .Where(h => h.Members.Count > 0)
            .ToList();
        var holding = holds.Select(h => h.Hold.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var (hold, members) in holds)
        {
            var teams = members
                .GroupBy(m => m.Team, StringComparer.OrdinalIgnoreCase)
                .Select(g => new NeedsYouHeld(new NeedsYouTeam(g.Key, names[g.Key]), [.. g.Select(m => m.Name)]))
                .ToList();
            var since = members
                .Select(m => host.Find(m) is { } runtime && runtime.HeldByConnection == hold.Id ? runtime.Marks().Failed?.At : null)
                .Where(at => at is not null)
                .DefaultIfEmpty(hold.Since)
                .Min()!.Value;

            result.Add(new NeedsYouItem(
                NeedsYouKinds.Connection, teams[0].Team, null, null, null, null, since, hold.Sentence,
                new NeedsYouWhere("connections", teams[0].Team.Id),
                Connection: new NeedsYouConnection(hold.Id, hold.Name, teams)));
        }

        foreach (var snapshot in host.Snapshots())
        {
            if (!names.ContainsKey(snapshot.Team)) continue;
            if (host.Find(new ContainerId(snapshot.Team, snapshot.Id)) is not { } member) continue;

            var rows = member.Marks();

            // HELD UP BY A CONNECTION LISTED ABOVE: its row says this member; its failure is not said again.
            if (member.HeldByConnection is { } heldBy && holding.Contains(heldBy)) rows = rows with { Failed = null, Blocked = null };

            // FAILED OUTRANKS BLOCKED, as on the card (`ContainerSnapshot.Failed`): a run that gave
            // up and then died is one stop, said once. A question is its own ask and is listed too.
            if (snapshot.Failed is not null && rows.Failed is { } failed)
            {
                marked.Add((snapshot, NeedsYouKinds.Failed, failed));
            }
            else if (snapshot.Blocked is not null && rows.Blocked is { } blocked)
            {
                marked.Add((snapshot, NeedsYouKinds.Blocked, blocked));
            }

            if (snapshot.NeedsDecision is not null && rows.NeedsDecision is { } decision)
            {
                marked.Add((snapshot, NeedsYouKinds.Decision, decision));
            }
        }

        if (marked.Count > 0)
        {
            var dispatched = (await backlog.CurrentDispatchesAsync(ct))
                .GroupBy(d => d.Dispatch.Correlation)
                .ToDictionary(g => g.Key, g => g.First());
            var workflows = marked
                .Select(m => m.Kind == NeedsYouKinds.Decision ? m.Snapshot.NeedsDecisionCorrelation ?? m.Row.Correlation : m.Row.Correlation)
                .ToList();

            for (var i = 0; i < marked.Count; i++)
            {
                var (snapshot, kind, row) = marked[i];
                var workflow = workflows[i];
                var item = workflow is { } w && dispatched.TryGetValue(w, out var current)
                    ? new WorkflowItem(current.Dispatch.Item, current.Title)
                    : null;
                var team = new NeedsYouTeam(snapshot.Team, names[snapshot.Team]);
                var where = row.Card is { } card
                    ? new NeedsYouWhere("card", team.Id, workflow, card)
                    : new NeedsYouWhere("workflow", team.Id, workflow);

                result.Add(kind switch
                {
                    NeedsYouKinds.Failed => new NeedsYouItem(
                        kind, team, snapshot.Id, row.Card, workflow, item, row.At, snapshot.Failed!, where,
                        FailureClass: snapshot.FailureClass),
                    NeedsYouKinds.Blocked => new NeedsYouItem(
                        kind, team, snapshot.Id, row.Card, workflow, item, row.At, snapshot.Blocked!, where),
                    _ => new NeedsYouItem(
                        kind, team, snapshot.Id, row.Card, workflow, item, row.At, snapshot.NeedsDecision!, where,
                        NeedsDecisionSeq: snapshot.NeedsDecisionSeq ?? row.Seq),
                });
            }
        }

        // A SOLUTION TEAM'S MISSING INPUT, in the wait's own sentence. Read through the installer, not
        // `SolutionWait.WaitingForAsync`, which clears marks when a wait ends: this read changes nothing.
        // Since is when the wait was first seen; a wait the Host has not seen yet (the sweep's heartbeat
        // will) is dated from when the package that asks for the input was installed or last updated.
        foreach (var solution in await solutions.ListAsync(ct))
        {
            if (!names.TryGetValue(solution.Team, out var name)) continue;

            var missing = await installer.MissingAsync(solution.Team, solution, ct);
            if (missing.Count == 0) continue;

            result.Add(new NeedsYouItem(
                NeedsYouKinds.WaitingInput, new NeedsYouTeam(solution.Team, name), null, null, null, null,
                waiting.WaitingSince(solution.Team) ?? solution.UpdatedAt ?? solution.InstalledAt, SolutionWait.ReasonOf(missing),
                new NeedsYouWhere("solution", solution.Team)));
        }

        return result;
    }

    /// <summary>
    /// The stored part. Every item's current dispatch with the item, one backlog read; where each of
    /// those workflows stands - open, ended completed, Blocked or Failed - one log query
    /// (<see cref="IMessageLog.WorkflowStandingsAmongAsync"/>); landed from the dispatch's stored proof
    /// or the landed cache, never git; stranded by the Backlog's own rule, with the board read once
    /// and only when an item in flight is Blocked or Failed; the unfinished removals, one read.
    /// </summary>
    public static async Task<IReadOnlyList<NeedsYouItem>> StoredAsync(
        IReadOnlyDictionary<string, string> names,
        TeamRegistry teams,
        ContainerHost host,
        IBacklogStore backlog,
        IMessageLog log,
        KanbanStore kanban,
        BacklogLandedCache landedCache,
        UnfinishedRemovalRetry removals,
        CancellationToken ct = default)
    {
        var result = new List<NeedsYouItem>();

        var live = (await backlog.CurrentDispatchesAsync(ct))
            .Where(d => !d.Archived)
            .ToDictionary(d => d.Dispatch.Item);
        var latest = live.Values.Select(d => d.Dispatch).ToList();

        NeedsYouTeam TeamOf(BacklogDispatch d) =>
            new(teams.ExistingName(d.TeamId) ?? d.TeamId, names.GetValueOrDefault(teams.ExistingName(d.TeamId) ?? d.TeamId) ?? d.TeamName);

        WorkflowItem ItemOf(BacklogDispatch d) => new(d.Item, live[d.Item].Title);

        // WHERE EVERY DISPATCH'S WORKFLOW STANDS, in one log query: the dispatches of teams that still
        // exist, above their floors - the candidates `BacklogInFlightState` itself would ask about.
        var asked = latest
            .Where(d => teams.ExistingName(d.TeamId) is { } stored && d.Correlation > teams.FloorFor(stored))
            .GroupBy(d => d.Correlation)
            .ToDictionary(g => g.Key, g => teams.ExistingName(g.First().TeamId)!);
        var standings = await log.WorkflowStandingsAmongAsync(asked, ct);

        // MERGE-READY: the dispatch workflow ENDED COMPLETED (closed by a person is not finished
        // work), and its landed reads `pushed` - on origin, not on the default branch. `local`,
        // `unknown`, `in-review` and an uncached team are not listed.
        foreach (var dispatch in latest)
        {
            if (string.Equals(live[dispatch.Item].State, BacklogStates.Implemented, StringComparison.Ordinal)) continue;
            if (standings.GetValueOrDefault(dispatch.Correlation) is not { CompletedAt: { } at }) continue;
            if (dispatch.LandedAt is not null) continue;
            if (landedCache.Get(asked[dispatch.Correlation]) is not { State: BacklogLandedStates.Pushed } landed) continue;

            var team = TeamOf(dispatch);
            result.Add(new NeedsYouItem(
                NeedsYouKinds.MergeReady, team, null, null, dispatch.Correlation, ItemOf(dispatch), at, landed.Detail,
                new NeedsYouWhere("merge", team.Id, dispatch.Correlation, Item: dispatch.Item)));
        }

        // STRANDED, by the Backlog's own rule over the items in flight. Its notice is the words; it
        // began when the latest of its cards reached Done in the later workflow.
        var open = standings.Values.Where(s => s.Open).Select(s => s.Correlation).ToHashSet();
        var inFlight = BacklogInFlightState.From(latest, teams, host, open);
        var stuck = new List<(BacklogDispatch Dispatch, string State)>();

        foreach (var dispatch in latest)
        {
            if (!inFlight.ContainsKey(dispatch.Item)) continue;
            if (standings.GetValueOrDefault(dispatch.Correlation)?.Stuck is { } state) stuck.Add((dispatch, state));
        }

        if (stuck.Count > 0)
        {
            var cards = (await kanban.GetBoardAsync()).Cards;

            foreach (var (dispatch, state) in stuck)
            {
                var team = TeamOf(dispatch);
                var onTeam = cards.Where(c => string.Equals(c.Team, team.Id, StringComparison.OrdinalIgnoreCase)).ToList();
                if (BacklogStrandedState.Of(team.Id, dispatch.Correlation, state, onTeam) is not { } stranded) continue;

                var since = onTeam.Where(c => stranded.Cards.Contains(c.Id)).Max(c => c.UpdatedAt);
                result.Add(new NeedsYouItem(
                    NeedsYouKinds.Stranded, team, null, null, dispatch.Correlation, ItemOf(dispatch),
                    new DateTimeOffset(DateTime.SpecifyKind(since, DateTimeKind.Utc)), stranded.Notice,
                    new NeedsYouWhere("backlog", team.Id, dispatch.Correlation, Item: dispatch.Item)));
            }
        }

        // UNFINISHED REMOVALS, in the words the Host's own retry at start uses for what is left.
        foreach (var removal in await removals.ListAsync(ct))
        {
            var id = teams.ExistingName(removal.Team) ?? removal.Team;
            result.Add(new NeedsYouItem(
                NeedsYouKinds.Removal, new NeedsYouTeam(id, names.GetValueOrDefault(id) ?? removal.Team),
                removal.Member, null, null, null, removal.RecordedAt,
                $"{removal.Remaining.Count} path(s) remain: {string.Join(", ", removal.Remaining)}",
                new NeedsYouWhere("removals", id)));
        }

        return result;
    }
}
