using System.Globalization;
using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;

namespace Harness.Host;

public enum QuietTeamSweepFindingKind
{
    RunningWithoutProgress,
    PendingAcceptedWithoutTerminal,
    QuietTeam,

    /// <summary>A team declared its work done while its clone still holds unpushed commits.</summary>
    WrapUpNotPushed,
}

public sealed record QuietTeamSweepFinding(
    QuietTeamSweepFindingKind Kind,
    string Team,
    string Subject,
    string? Container,
    long EvidenceSeq,
    DateTimeOffset ObservedAt,
    TimeSpan Age);

/// <summary>
/// Detection-only sweep for conditions with no incoming request to hang a check on.
///
/// It writes findings to tenant_events and NEVER to the message log.
/// </summary>
public sealed class QuietTeamSweep(
    TeamRegistry teams,
    ITeamStore teamStore,
    IPendingDeliveries pending,
    IMessageLog messages,
    ITenantLog tenantLog,
    GitRunner git,
    TeamPaths paths)
{
    private static readonly HashSet<string> TeamTerminals = new(StringComparer.Ordinal)
    {
        MessageTypes.Completed,
        MessageTypes.Blocked,
        MessageTypes.Failed,
        MessageTypes.NeedsDecision,
    };

    public async Task<IReadOnlyList<QuietTeamSweepFinding>> ReapIdleAsync(
        TimeSpan window,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        var findings = new List<QuietTeamSweepFinding>();
        var clearingCandidates = new Dictionary<(string Action, string Subject), string?>();
        var teamSummaries = teams.All();
        var memberFloors = await MemberFloorsAsync(ct);
        var pendingByTeam = await PendingByTeamAsync(teamSummaries.Select(summary => summary.Id), ct);
        var workflowState = await WorkflowStateAsync(ct);
        var latestTerminals = await LatestTerminalByContainerAsync(teamSummaries, memberFloors, ct);

        static void AddCandidate(
            Dictionary<(string Action, string Subject), string?> candidates,
            string action,
            string subject,
            string? subjectName)
        {
            candidates[(action, subject)] = subjectName;
        }

        foreach (var summary in teamSummaries)
        {
            AddCandidate(
                clearingCandidates,
                TenantActions.SweepQuietTeam,
                summary.Id,
                summary.Id);
            foreach (var container in summary.Containers)
            {
                var qualified = $"{container.Team}/{container.Id}";
                AddCandidate(
                    clearingCandidates,
                    TenantActions.SweepRunningWithoutProgress,
                    qualified,
                    container.Id);
                AddCandidate(
                    clearingCandidates,
                    TenantActions.SweepPendingNeverTerminal,
                    qualified,
                    container.Id);
            }

            foreach (var container in summary.Containers.Where(snapshot => snapshot.State == ContainerState.Running))
            {
                var qualified = $"{container.Team}/{container.Id}";
                var key = new ContainerId(container.Team, container.Id);
                if (!memberFloors.TryGetValue(key, out var floorSeq)) continue;
                var evidence = await LatestRunningEvidenceAsync(qualified, floorSeq, ct);
                if (evidence is null) continue;
                var age = now - evidence.OccurredAt;
                if (age < window) continue;

                findings.Add(new QuietTeamSweepFinding(
                    QuietTeamSweepFindingKind.RunningWithoutProgress,
                    summary.Id,
                    qualified,
                    container.Id,
                    evidence.Seq,
                    evidence.OccurredAt,
                    age));
            }

            foreach (var accepted in pendingByTeam.GetValueOrDefault(summary.Id) ?? [])
            {
                var age = now - accepted.OccurredAt;
                if (age < window) continue;

                findings.Add(new QuietTeamSweepFinding(
                    QuietTeamSweepFindingKind.PendingAcceptedWithoutTerminal,
                    summary.Id,
                    accepted.Subscriber.ToString(),
                    accepted.Subscriber.Name,
                    accepted.Seq,
                    accepted.OccurredAt,
                    age));
            }

            if (!IsQuietTeam(summary, pendingByTeam, workflowState, latestTerminals))
            {
                continue;
            }

            var latestTerminal = latestTerminals
                .Where(kvp => string.Equals(kvp.Key.Team, summary.Id, StringComparison.OrdinalIgnoreCase))
                .Select(kvp => kvp.Value)
                .OrderByDescending(row => row.Seq)
                .FirstOrDefault();
            var observed = latestTerminal?.OccurredAt ?? now;
            var quietAge = now - observed;

            // v.2's per-correlation comparison fixes teams that ALREADY declared as done; this
            // gate bounds the separate false positive where a team has not declared YET.
            if (quietAge < window)
            {
                continue;
            }

            findings.Add(new QuietTeamSweepFinding(
                QuietTeamSweepFindingKind.QuietTeam,
                summary.Id,
                summary.Id,
                null,
                latestTerminal?.Seq ?? 0,
                observed,
                quietAge));
        }

        // A TEAM THAT DECLARED ITS WORK DONE WHILE ITS CLONE STILL HOLDS UNPUSHED COMMITS.
        //
        // A manager can report a merge and a push it has not performed, and without this nothing in
        // the platform would disagree: the outcome is AGENT NARRATION, and the only reader a person
        // who happens to look. This is the same question asked of the disk.
        //
        // THE LOCAL HALF ONLY, DELIBERATELY. `origin/<default>` is a TRACKING REF and it is updated BY a
        // successful push, so a clone ahead of its own tracking ref has not pushed - conclusive
        // without asking anybody. The network half is deliberately not asked: `git ls-remote` has
        // its own failure modes, and a detector that reports a finding because GitHub was briefly
        // unreachable is a detector nobody trusts.
        //
        // THIS IS THE FIRST THING IN THIS SWEEP THAT TOUCHES THE FILESYSTEM, which is an accepted
        // cost. It is bounded: only teams that have declared a workflow complete,
        // only repos whose clone actually exists.
        //
        // `Declared*` AND NEVER `Ended*`: this is a check on a CLAIM, and a person's
        // `workflow.closed` is not one. See `WorkflowState`'s own comment for why the asymmetry with
        // the quiet finding above is deliberate.
        foreach (var summary in teamSummaries)
        {
            var declared = workflowState.TryGetValue(summary.Id, out var state)
                && state.DeclaredWorkflowCorrelations.Count > 0;

            if (!declared)
            {
                continue;
            }

            foreach (var url in teams.ReposFor(summary.Id))
            {
                var repo = RepoUrls.DeriveName(url);
                var clonePath = Path.Combine(paths.ReposFor(summary.Id), repo, "main");
                var subject = $"{summary.Id}/{repo}";

                // A team that never cloned is not a defect, and neither is one whose clone is gone.
                if (!Directory.Exists(clonePath))
                {
                    continue;
                }

                AddCandidate(
                    clearingCandidates,
                    TenantActions.SweepWrapUpNotPushed,
                    subject,
                    summary.Id);

                // Measured against the stored default branch; not known leaves MainAhead
                // null, which is no finding.
                var status = await git.StatusAsync(
                    clonePath, teams.DefaultBranchFor(summary.Id, repo).Branch, ct: ct);

                // NULL IS UNKNOWN AND UNKNOWN IS NOT A FINDING. There is no tracking ref on a clone
                // that has never fetched, and reporting that as unpushed work would be the invented
                // number this codebase refuses everywhere else.
                //
                // MainAhead is the local default branch vs its origin tracking ref, not HEAD. A clone sitting on team/<id>
                // with that branch ahead is not unpushed main; unpushed main is still reported
                // when HEAD is the team branch. Change this call site if that meaning changes.
                if (status.MainAhead is not > 0)
                {
                    continue;
                }

                findings.Add(new QuietTeamSweepFinding(
                    QuietTeamSweepFindingKind.WrapUpNotPushed,
                    summary.Id,
                    subject,
                    null,
                    0,
                    now,
                    TimeSpan.Zero));
            }
        }

        foreach (var finding in findings.OrderBy(f => f.Kind).ThenBy(f => f.Team, StringComparer.Ordinal))
        {
            if (!await ShouldWriteFindingAsync(finding, ct))
            {
                continue;
            }

            await tenantLog.WriteAsync(
                actorId: null,
                actorEmail: null,
                action: ActionOf(finding.Kind),
                subject: finding.Subject,
                subjectName: finding.Container ?? finding.Team,
                detail: JsonSerializer.Serialize(new
                {
                    kind = finding.Kind.ToString(),
                    team = finding.Team,
                    container = finding.Container,
                    evidenceSeq = finding.EvidenceSeq,
                    observedAt = finding.ObservedAt.ToString("O", CultureInfo.InvariantCulture),
                    ageSeconds = Math.Max(0, Math.Floor(finding.Age.TotalSeconds)),
                }),
                ct);
        }

        await WriteClearedRowsAsync(findings, clearingCandidates, ct);

        return findings;
    }

    private async Task<bool> ShouldWriteFindingAsync(
        QuietTeamSweepFinding finding,
        CancellationToken ct)
    {
        var action = ActionOf(finding.Kind);
        var subject = finding.Subject;
        var latestReport = await tenantLog.FindLatestAsync(action, subject, ct);
        if (latestReport is null)
        {
            return true;
        }

        var latestCleared = await tenantLog.FindLatestAsync(ClearedActionOf(finding.Kind), subject, ct);
        return latestCleared is { Seq: var clearedSeq } && clearedSeq > latestReport.Seq;
    }

    // Clearing is represented by dedicated sweep.*-cleared actions so the log itself can show
    // that a standing finding ended, and a later recurrence can be emitted again.
    private async Task WriteClearedRowsAsync(
        IReadOnlyCollection<QuietTeamSweepFinding> findings,
        IReadOnlyDictionary<(string Action, string Subject), string?> candidates,
        CancellationToken ct)
    {
        var currentlyDetected = findings
            .Select(finding => (Action: ActionOf(finding.Kind), finding.Subject))
            .ToHashSet();

        foreach (var candidate in candidates)
        {
            var action = candidate.Key.Action;
            var subject = candidate.Key.Subject;
            var latestReport = await tenantLog.FindLatestAsync(action, subject, ct);
            if (latestReport is null)
            {
                continue;
            }

            var latestCleared = await tenantLog.FindLatestAsync(ClearedActionOf(action), subject, ct);
            var standingAndOpen = latestCleared is null || latestReport.Seq > latestCleared.Seq;
            if (!standingAndOpen || currentlyDetected.Contains((action, subject)))
            {
                continue;
            }

            await tenantLog.WriteAsync(
                actorId: null,
                actorEmail: null,
                action: ClearedActionOf(action),
                subject: subject,
                subjectName: candidate.Value,
                detail: JsonSerializer.Serialize(new
                {
                    clearedAction = action,
                }),
                ct);
        }
    }

    private static string ClearedActionOf(string action) =>
        action switch
        {
            TenantActions.SweepRunningWithoutProgress => TenantActions.SweepRunningWithoutProgressCleared,
            TenantActions.SweepPendingNeverTerminal => TenantActions.SweepPendingNeverTerminalCleared,
            TenantActions.SweepQuietTeam => TenantActions.SweepQuietTeamCleared,
            TenantActions.SweepWrapUpNotPushed => TenantActions.SweepWrapUpNotPushedCleared,
            _ => throw new InvalidOperationException($"Unknown sweep action '{action}'."),
        };

    // ONE PAIRING, NOT TWO. A kind's cleared action is the cleared partner of the action that kind
    // writes - deriving it is what keeps a new kind from being added to `ActionOf` and forgotten
    // here, which is exactly how `WrapUpNotPushed` came to kill every sweep after the first.
    private static string ClearedActionOf(QuietTeamSweepFindingKind kind) =>
        ClearedActionOf(ActionOf(kind));

    private async Task<Message?> LatestRunningEvidenceAsync(
        string qualified,
        long floorSeq,
        CancellationToken ct)
    {
        var rows = await messages.ReadForContainerAsync(qualified, floorSeq, 200, ct);
        var evidence = rows.FirstOrDefault(message =>
            message.Type == MessageTypes.Progress || message.Type == MessageTypes.Started);
        return evidence;
    }

    private async Task<Dictionary<ContainerId, long>> MemberFloorsAsync(CancellationToken ct)
    {
        var map = new Dictionary<ContainerId, long>();

        foreach (var member in await teamStore.MembersAsync(ct))
        {
            map[new ContainerId(member.Team, member.Name)] = member.FloorSeq;
        }

        return map;
    }

    private sealed record PendingEvidence(ContainerId Subscriber, long Seq, DateTimeOffset OccurredAt);

    private async Task<Dictionary<string, List<PendingEvidence>>> PendingByTeamAsync(
        IEnumerable<string> teamsToRead,
        CancellationToken ct)
    {
        var byTeam = new Dictionary<string, List<PendingEvidence>>(StringComparer.OrdinalIgnoreCase);

        foreach (var team in teamsToRead)
        {
            var rows = await pending.ForTeamAsync(team, ct);
            foreach (var row in rows)
            {
                var split = row.Subscriber.Split('/');
                if (split.Length != 2) continue;
                var subscriber = new ContainerId(split[0], split[1]);
                if (await messages.FindAsync(row.Seq, ct) is not { } accepted) continue;
                if (!byTeam.TryGetValue(team, out var list))
                {
                    list = [];
                    byTeam[team] = list;
                }

                list.Add(new PendingEvidence(subscriber, row.Seq, accepted.OccurredAt));
            }
        }

        return byTeam;
    }

    /// <summary>
    /// TWO SETS, BECAUSE THIS SWEEP ASKS TWO DIFFERENT QUESTIONS OF ONE PAIR OF MESSAGE TYPES, and
    /// collapsing them into one is how a `workflow.closed` reaches the reader it must not.
    ///
    /// <see cref="EndedWorkflowCorrelations"/> answers **IS THIS WORKFLOW OVER** - either terminal
    /// type - and is what the quiet-team finding reads. Asking `workflow.completed` alone would make that
    /// finding permanent for any workflow a PERSON had closed: every other quiet clause still holds
    /// after a close, so the team would be reported to people as one nobody had declared finished,
    /// about work somebody had explicitly ended.
    ///
    /// <see cref="DeclaredWorkflowCorrelations"/> answers **DID SOMEBODY CLAIM A DELIVERY** -
    /// `workflow.completed` ONLY - and is what the unpushed-clone finding reads. That finding exists
    /// because a manager can report a merge and a push it has not performed, so it is a check on a
    /// CLAIM. A person closing a workflow makes no claim; they are saying stop counting this. Adding
    /// closes here would point a filesystem-touching detector at abandoned work and report it as an
    /// unkept promise nobody made, so the asymmetry is deliberate rather than an oversight.
    /// </summary>
    private sealed record WorkflowState(
        HashSet<long> EndedWorkflowCorrelations,
        HashSet<long> DeclaredWorkflowCorrelations);

    private async Task<Dictionary<string, WorkflowState>> WorkflowStateAsync(CancellationToken ct)
    {
        var state = new Dictionary<string, WorkflowState>(StringComparer.OrdinalIgnoreCase);
        var after = 0L;

        while (true)
        {
            // BOTH TERMINAL TYPES, as `WorkflowOpenSql` reads them. WHICH one ended it does not
            // matter to this sweep - it asks only whether anything still needs a person - and the
            // distinction between a delivery and an administrative close belongs to the surfaces
            // that show a WORD.
            var page = await messages.ReadAfterAsync(
                after, [MessageTypes.WorkflowCompleted, MessageTypes.WorkflowClosed], 500, ct);
            if (page.Count == 0) break;

            foreach (var row in page)
            {
                // A `workflow.closed` ROW'S TEAM COMES FROM ITS PAYLOAD, not its Source - it is
                // published by a PERSON, whose id carries no `/`. `MessageTeam.Of` already knows
                // that, which is why this loop needs no second arm; without that arm the row would
                // have no team here and the check above would silently do nothing.
                var team = MessageTeam.Of(row);
                if (team is null) continue;
                if (!state.TryGetValue(team, out var workflowState))
                {
                    workflowState = new WorkflowState([], []);
                    state[team] = workflowState;
                }

                workflowState.EndedWorkflowCorrelations.Add(row.CorrelationId);

                if (string.Equals(row.Type, MessageTypes.WorkflowCompleted, StringComparison.Ordinal))
                {
                    workflowState.DeclaredWorkflowCorrelations.Add(row.CorrelationId);
                }
            }

            after = page[^1].Seq;
        }

        return state;
    }

    private async Task<Dictionary<ContainerId, Message>> LatestTerminalByContainerAsync(
        IReadOnlyCollection<TeamSummary> teamSummaries,
        IReadOnlyDictionary<ContainerId, long> memberFloors,
        CancellationToken ct)
    {
        var latest = new Dictionary<ContainerId, Message>();

        foreach (var summary in teamSummaries)
        {
            foreach (var container in summary.Containers)
            {
                var key = new ContainerId(container.Team, container.Id);
                if (!memberFloors.TryGetValue(key, out var floorSeq)) continue;
                var rows = await messages.ReadForContainerAsync($"{container.Team}/{container.Id}", floorSeq, 200, ct);
                var terminal = rows.FirstOrDefault(message => TeamTerminals.Contains(message.Type));
                if (terminal is null) continue;
                latest[key] = terminal;
            }
        }

        return latest;
    }

    private static bool IsQuietTeam(
        TeamSummary summary,
        IReadOnlyDictionary<string, List<PendingEvidence>> pendingByTeam,
        IReadOnlyDictionary<string, WorkflowState> workflowState,
        IReadOnlyDictionary<ContainerId, Message> latestTerminals)
    {
        if (summary.Containers.Count == 0) return false;
        if (summary.Containers.Any(container => container.State == ContainerState.Running)) return false;
        if (summary.Containers.Sum(container => container.QueueDepth) > 0) return false;
        if ((pendingByTeam.GetValueOrDefault(summary.Id) ?? []).Count > 0) return false;

        var terminalRows = new List<Message>();
        foreach (var container in summary.Containers)
        {
            var key = new ContainerId(container.Team, container.Id);
            if (!latestTerminals.TryGetValue(key, out var terminal)) return false;
            if (terminal.Type == MessageTypes.NeedsDecision) return false;
            if (terminal.Type != MessageTypes.Completed) return false;
            terminalRows.Add(terminal);
        }

        var latestTerminal = terminalRows
            .OrderByDescending(terminal => terminal.Seq)
            .First();
        // Reference implementation: web/src/lib/teamKpis.ts (teamLogFacts.workflowOpen).
        // We cannot share one derivation because the server reads SQL while the client reads a
        // bounded message window, so this path mirrors the client's correlation comparison.
        //
        // THAT REFERENCE READS BOTH TERMINAL TYPES, AND SO MUST THIS - `workflow.closed` included,
        // since `/api/messages` carries the row. A citation that named a reference this did not
        // match would be worse than no citation, because the next reader would trust it. Both sides
        // ask the same question - has this
        // workflow ENDED - and neither asks who ended it.
        var latestTerminalsWorkflowHasEnded = workflowState
            .GetValueOrDefault(summary.Id)?
            .EndedWorkflowCorrelations
            .Contains(latestTerminal.CorrelationId)
            ?? false;

        return !latestTerminalsWorkflowHasEnded;
    }

    private static string ActionOf(QuietTeamSweepFindingKind kind) =>
        kind switch
        {
            QuietTeamSweepFindingKind.RunningWithoutProgress => TenantActions.SweepRunningWithoutProgress,
            QuietTeamSweepFindingKind.PendingAcceptedWithoutTerminal => TenantActions.SweepPendingNeverTerminal,
            QuietTeamSweepFindingKind.QuietTeam => TenantActions.SweepQuietTeam,
            QuietTeamSweepFindingKind.WrapUpNotPushed => TenantActions.SweepWrapUpNotPushed,
            _ => throw new InvalidOperationException($"Unknown sweep finding kind '{kind}'."),
        };
}
