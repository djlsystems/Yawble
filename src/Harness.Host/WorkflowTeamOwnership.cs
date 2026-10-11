using Harness.Containers;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// WHETHER A WORKFLOW BELONGS TO A TEAM - the authority question `TeamGate` cannot ask, because it
/// only knows the route's <c>{team}</c> value and never which team a correlation actually happened
/// under. A route that identifies its resource by a bare correlation id - the close route, and Task
/// 5's nudge and stop - is structurally not gated for that resource at all: a caller holding team A
/// could otherwise act on team B's workflow by naming B's correlation through A's route. This is the
/// one place that question is answered, so close/nudge/stop share one definition of "this team's
/// workflow" rather than three that could drift apart.
///
/// DERIVED FROM THE ROOT, NEVER FROM ANY OTHER ROW ON THE CORRELATION. A REAL correlation id IS its
/// root's seq - but the number this method is HANDED is a caller-supplied `long` from a route
/// parameter, not something the platform already knows to be a correlation id. That a message's own
/// `CorrelationId` is always its root's seq says nothing about whether an ARBITRARY seq the caller
/// named happens to BE a root. Being a root is therefore a thing this method must CHECK, not a thing
/// it may assume of its input - see the `root.CorrelationId != correlation` guard below, which is
/// exactly that check and the fix for the second bypass this class's history records.
///
/// THE FIRST VERSION OF THIS CHECK ASKED "does ANY row on this correlation carry a Team/ source" - a
/// CRITICAL, because `Tell.Causation` is CALLER-SUPPLIED and `tell`'s handler validates only that the
/// seq exists and the depth limit holds, never that the causing message belongs to the caller's own
/// team. So team A could `tell` its OWN manager with `causation` set to team B's correlation; the
/// pump delivers; `A/Manager` publishes `container.started` genuinely sourced `A/...` but ON B's
/// correlation; and the old predicate found that row and returned true. THE PREDICATE ITSELF WAS
/// WRITABLE BY THE CALLER IT WAS MEANT TO CHECK. Deriving from the root closed that: grafting a row
/// onto another team's correlation cannot change what that correlation's ROOT is.
///
/// THE SECOND VERSION STILL HAD A GAP, because "derive from the root" and "check that what was
/// FOUND is a root" are different claims and only the first one was implemented. `FindAsync(correlation)`
/// returns WHATEVER ROW HAS THAT SEQ - root or not - and nothing stopped a caller naming a NON-root
/// row that happens to resolve to their own team by TYPE. The exploit needs no graft: Alice (bound
/// to Alpha only) `tell`s her OWN manager with `causation` set to a seq inside Beta's workflow -
/// `tell` validates only existence and depth, never team, and hands Beta's correlation id back in
/// its own response, which is itself an oracle. The new instruction row's TYPE is
/// `container.instruction.Alpha/Manager` (correctly - it addresses Alpha's own manager), so
/// `MessageTeam.Of` answers "Alpha" for THAT ROW. But the row's own `CorrelationId` is inherited from
/// its CAUSE - Beta's root - because a message's correlation is its cause's correlation, not its own
/// seq. Naming that row's OWN SEQ (not the correlation id `tell` returned) to the close route made
/// `WorkflowTeamOwnership` authorise on the row's team while `AppendAsync` published the close under
/// BETA'S correlation, closing Beta's workflow with a payload claiming `team: "Alpha"` - so Beta's
/// own feed and `/wait` never even show the row that closed it. `root.CorrelationId != correlation`
/// (equivalently `root.CausationSeq is not null`) is the fix: a root correlates to ITSELF, so this
/// costs nothing legitimate - every REAL correlation id passes it by construction - and refuses
/// every row that is merely CAUSED BY one.
///
/// (`tell` accepting a foreign causation at all is a separate defect with its own blast radius -
/// recorded for the platform's own hot path, not fixed here.)
///
/// THE SAME FUNCTION `WorkflowOwner.OfAsync` ALREADY TRUSTS for `workflow-complete`'s authority -
/// `MessageTeam.Of` derives from the root's TYPE for an addressed instruction (built by the server
/// from the container it resolved) or from its PAYLOAD for a kanban card event (also server-written,
/// from the `{team}` route value `TeamGate` already checked) - never from a caller-suppliable
/// field. Using it here makes the two authority checks in this codebase agree rather than inventing
/// a third rule.
///
/// NULL IS A REFUSAL, NOT A PASS. A workflow rooted in something `MessageTeam.Of` cannot attribute a
/// team to - a card event with a missing or malformed `team` field, a schedule firing whose payload
/// names no container, or any root shape neither arm covers - has no team a caller can be authorised
/// against, and "nobody derivably owns it" must never be read as "anyone may act on it". This method
/// answers false for that root exactly as it does for a correlation that does not exist at all, so
/// every caller (close, and Task 5's nudge/stop) refuses identically either way.
///
/// REFUSE WITH "NOT FOUND", NEVER "FORBIDDEN". A 403 here would CONFIRM that some other team's
/// correlation is real - exactly the tell a caller with no business seeing it must never get. This
/// is also why the method answers a bare `bool`: there is nothing safe to say beyond yes or no.
///
/// THE FLOOR, FOR THE PREDECESSOR CASE: a team deleted and recreated under the same name shares its
/// name with whatever came before it, and `MessageTeam.Of` reads only the name - so a correlation
/// whose root predates every live container this team currently holds belongs to that predecessor
/// life, not to this one. Computed the identical way `WorkflowsForTeamAsync`'s own caller
/// already does: the minimum `SinceSeq` across the team's live containers. A correlation id equal to
/// or below that floor is refused even when the root's team name matches.
///
/// THE COST, NOTED RATHER THAN HIDDEN: a workflow that genuinely spans two teams can now only be
/// acted on from the team whose root instruction or card event actually named it - ordinarily the
/// team that started it. Judged correct (act on it where it belongs), but it is a real narrowing,
/// not a free side effect.
/// </summary>
internal static class WorkflowTeamOwnership
{
    /// <param name="team">The STORED spelling - the value `teams.ExistingName` returned, not
    /// whatever case the caller typed in the route.</param>
    public static async Task<bool> OwnsAsync(
        TeamRegistry teams, ContainerHost host, IMessageLog log, string team, long correlation,
        CancellationToken ct)
    {
        // Mirrors WorkflowOwner.OfAsync's own guard: no message on the log carries a non-positive
        // seq, so a non-positive correlation can never be legitimately owned by anything.
        if (correlation <= 0) return false;

        if (await log.FindAsync(correlation, ct) is not { } root) return false;

        // THE NAMED SEQ MUST ACTUALLY BE A ROOT - see the class doc comment's "second version" for
        // the exploit this closes. A root correlates to ITSELF, so this is equivalently
        // `root.CausationSeq is not null`; costs nothing legitimate, since every real correlation id
        // already satisfies it.
        if (root.CorrelationId != correlation) return false;

        if (MessageTeam.Of(root) is not { } rootTeam
            || !string.Equals(rootTeam, team, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var floor = teams.ContainerIdsOf(team)
            .Select(host.Find)
            .Where(container => container is not null)
            .Select(container => container!.Snapshot().SinceSeq)
            .DefaultIfEmpty(0)
            .Min();

        return correlation > floor;
    }
}
