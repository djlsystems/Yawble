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
}

/// <summary>The team a row is about: its id and the name a person reads.</summary>
public sealed record NeedsYouTeam(string Id, string Name);

/// <summary>
/// What opens a row: <c>workflow</c>, <c>card</c>, <c>backlog</c>, <c>merge</c>, <c>solution</c> or
/// <c>removals</c>, on <see cref="Team"/>, with the workflow, card and item it names when it names one.
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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FailureClass = null);

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
            ITeamSolutionStore solutions, SolutionInstaller installer, SolutionWait waiting,
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
                items.AddRange(await LiveAsync(names, host, backlog, solutions, installer, waiting, ct));
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
                + "`needsDecisionSeq` on a decision and `failureClass` on a failure. `kind` is `decision` "
                + "(a member's needs-decision mark), `blocked` (a member's last run published blocked), "
                + "`failed` (a member's last run failed; a member both blocked and failed is listed "
                + "failed), `waiting-input` (a solution team's `missing`), `merge-ready` (a backlog item "
                + "whose current dispatch workflow ended with `workflow.completed` and whose landed reads "
                + "`pushed`), `stranded` (an item carrying `stranded`) or `removal` (an unfinished "
                + "removal). `words` are the platform's own: the blocked reason, the failure, the "
                + "question, the missing input's sentence, the landed sentence, the stranded notice, "
                + "what the removal left. `where.view` is `workflow`, `card`, `backlog`, `merge`, "
                + "`solution` or `removals`.\n\n"
                + "`part=live` answers only decision, blocked, failed and waiting-input, from memory; "
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
    /// for is named from the backlog's latest dispatches and current titles - one read each, for the
    /// whole list, and none when nothing is marked.
    /// </summary>
    public static async Task<IReadOnlyList<NeedsYouItem>> LiveAsync(
        IReadOnlyDictionary<string, string> names,
        ContainerHost host,
        IBacklogStore backlog,
        ITeamSolutionStore solutions,
        SolutionInstaller installer,
        SolutionWait waiting,
        CancellationToken ct = default)
    {
        var marked = new List<(ContainerSnapshot Snapshot, string Kind, MarkRow Row)>();

        foreach (var snapshot in host.Snapshots())
        {
            if (!names.ContainsKey(snapshot.Team)) continue;
            if (host.Find(new ContainerId(snapshot.Team, snapshot.Id)) is not { } member) continue;

            var rows = member.Marks();

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

        var result = new List<NeedsYouItem>();

        if (marked.Count > 0)
        {
            var dispatched = (await backlog.LatestDispatchesAsync(ct))
                .GroupBy(d => d.Correlation)
                .ToDictionary(g => g.Key, g => g.First().Item);
            var workflows = marked
                .Select(m => m.Kind == NeedsYouKinds.Decision ? m.Snapshot.NeedsDecisionCorrelation ?? m.Row.Correlation : m.Row.Correlation)
                .ToList();
            var ids = workflows.Where(w => w is { } c && dispatched.ContainsKey(c)).Select(w => dispatched[w!.Value]).Distinct().ToList();
            var titles = ids.Count == 0 ? new Dictionary<long, string>() : await backlog.TitlesAsync(ids, ct);

            for (var i = 0; i < marked.Count; i++)
            {
                var (snapshot, kind, row) = marked[i];
                var workflow = workflows[i];
                var item = workflow is { } w && dispatched.TryGetValue(w, out var id) && titles.TryGetValue(id, out var title)
                    ? new WorkflowItem(id, title)
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
        foreach (var solution in await solutions.ListAsync(ct))
        {
            if (!names.TryGetValue(solution.Team, out var name)) continue;

            var missing = await installer.MissingAsync(solution.Team, solution, ct);
            if (missing.Count == 0) continue;

            result.Add(new NeedsYouItem(
                NeedsYouKinds.WaitingInput, new NeedsYouTeam(solution.Team, name), null, null, null, null,
                waiting.WaitingSince(solution.Team), SolutionWait.ReasonOf(missing),
                new NeedsYouWhere("solution", solution.Team)));
        }

        return result;
    }

    /// <summary>
    /// The stored part. The live backlog and every item's current dispatch, one store read each;
    /// which of those dispatches' workflows ended completed, one log query; landed from the
    /// dispatch's stored proof or the landed cache, never git; stranded by the Backlog's own rule;
    /// the unfinished removals, one read.
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

        var live = (await backlog.ListAsync(null, includeUnlinked: true, archived: false, ct))
            .ToDictionary(item => item.Id);
        var latest = (await backlog.LatestDispatchesAsync(ct))
            .Where(d => live.ContainsKey(d.Item))
            .ToList();

        NeedsYouTeam TeamOf(BacklogDispatch d) =>
            new(teams.ExistingName(d.TeamId) ?? d.TeamId, names.GetValueOrDefault(teams.ExistingName(d.TeamId) ?? d.TeamId) ?? d.TeamName);

        WorkflowItem ItemOf(BacklogDispatch d) => new(d.Item, live[d.Item].Title);

        // MERGE-READY: the dispatch workflow ENDED COMPLETED (closed by a person is not finished
        // work), and its landed reads `pushed` - on origin, not on the default branch. `local`,
        // `unknown`, `in-review` and an uncached team are not listed.
        var current = latest
            .Where(d => !string.Equals(live[d.Item].State, BacklogStates.Implemented, StringComparison.Ordinal))
            .Where(d => teams.ExistingName(d.TeamId) is { } stored && d.Correlation > teams.FloorFor(stored))
            .ToList();
        var completed = await log.CompletedWorkflowsAmongAsync([.. current.Select(d => d.Correlation)], ct);

        foreach (var dispatch in current)
        {
            if (!completed.TryGetValue(dispatch.Correlation, out var at)) continue;
            if (dispatch.LandedAt is not null) continue;
            if (landedCache.Get(teams.ExistingName(dispatch.TeamId)!) is not { State: BacklogLandedStates.Pushed } landed) continue;

            var team = TeamOf(dispatch);
            result.Add(new NeedsYouItem(
                NeedsYouKinds.MergeReady, team, null, null, dispatch.Correlation, ItemOf(dispatch), at, landed.Detail,
                new NeedsYouWhere("merge", team.Id, dispatch.Correlation, Item: dispatch.Item)));
        }

        // STRANDED, by the Backlog's own rule over the items in flight. Its notice is the words; it
        // began when the latest of its cards reached Done in the later workflow.
        var inFlight = await BacklogInFlightState.ForAsync(latest, teams, host, log, ct);
        var stuck = new List<(BacklogDispatch Dispatch, string State)>();

        foreach (var dispatch in latest)
        {
            if (!inFlight.TryGetValue(dispatch.Item, out var flight)) continue;

            var timing = await log.WorkflowForTeamAsync(flight.TeamId, flight.Correlation, teams.FloorFor(flight.TeamId), ct);
            if (timing.State is { } state && BacklogStrandedState.Stuck.Contains(state)) stuck.Add((dispatch, state));
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
