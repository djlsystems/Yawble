using System.ComponentModel;
using Harness.Containers;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// What a reset was asked to do.
///
/// Every option except <see cref="ForgetHistory"/> defaults to FALSE, so the reset nobody thought
/// carefully about is the one that only moves floors - the reversible half.
/// </summary>
public sealed record TeamResetOptions(
    /// <summary>Which members to reset, by identifier. Empty resets nothing, which is not an error:
    /// a dialog with every box cleared should answer "nothing moved" rather than refuse.</summary>
    [property: Description(
        "Which members to reset, by identifier. A member this team does not hold is skipped rather "
        + "than refused. Empty resets nothing, which is not an error.")]
    IReadOnlyCollection<string> Members,

    /// <summary>Advance the targeted members' floors - what a reset MEANS, and the one option here
    /// whose default is TRUE, because a caller naming members and saying nothing else has asked for
    /// a reset. False is the narrow case: clear a working folder and leave the log alone.</summary>
    [property: Description(
        "Advance the named members' floors - what a reset MEANS. **Defaults to true**, and is the "
        + "only field here that does: naming members and saying nothing else is a reset. False is "
        + "the narrow case - clear folders and leave the log alone.")]
    bool ForgetHistory = true,

    /// <summary>Permanently delete the targeted members' messages. IRREVERSIBLE, and the only option
    /// here that destroys anything an audit could have used.</summary>
    [property: Description(
        "Permanently delete the named members' messages. **Irreversible**, and the only option here "
        + "that destroys something an audit could have used. A row still cited by a message that is "
        + "NOT being deleted is retained and counted - nulling its causation would destroy the one "
        + "field that records what caused what. That citing message is often another member of this "
        + "same team, a member since removed, or the team's Concierge; the purge does not "
        + "determine which and does not report it. A row still queued for another member's run is "
        + "retained the same way, because that run's own rows will cite it.")]
    bool Purge = false,

    [property: Description(
        "Empty the named members' working folders, `teams/{team}/workspaces/{member}`. The folders "
        + "themselves stay - an agent whose workspace is missing runs somewhere else entirely.")]
    bool ClearWorkspaces = false,

    [property: Description(
        "Empty the named members' transcripts, `teams/{team}/transcripts/{member}` - the raw captured "
        + "output of every run, which the message log does not hold.")]
    bool ClearTranscripts = false,

    [property: Description(
        "Empty the team's shared documents, `teams/{team}/docs`. Team-wide rather than per member, "
        + "because the folder is.")]
    bool ClearSharedDocuments = false);

// THERE IS NO `ResetConcierge` FLAG, and it was DELETED rather than left deprecated. A Concierge is
// keyed on the PERSON - one per signed-in user, serving every team they reach - so no team-scoped
// route owns a session to end, and nothing here ever read it. The same judgement this record
// already made about the session count on the way out: a field that is structurally inert is worse
// than no field, because every reader has to discover for themselves that it does nothing.
//
// Safe to remove from the wire: System.Text.Json ignores an unknown property, so a caller still
// sending it gets exactly what it got before - nothing.

/// <summary>What a reset actually did, so the caller can say it rather than guess - the same shape
/// and the same reason as <see cref="TeamDeleted"/>.</summary>
public sealed record TeamWasReset(
    string Team,
    long Floor,
    IReadOnlyList<string> Floored,
    int Purged,
    int Retained,

    // THERE IS NO SESSION COUNT, and its absence is the honest answer rather than an omission.
    // A Concierge session is keyed on the PERSON, so no team-scoped operation can address one -
    // this reset ends none, and cannot. A count here would always be zero, asserting "nobody was
    // connected" while somebody was typing into the session. The dialog's rule is the same: no
    // count, because a number that might be wrong is worse than none.
    IReadOnlyList<string> Cleared,
    IReadOnlyList<string> Failures,

    /// <summary>Every path a clear could not remove. Recorded by path and retried; a retry
    /// removes only these paths, never what the member has written since.</summary>
    IReadOnlyList<string> Remaining);

/// <summary>
/// A targeted member is running, holds queued work, or has accepted a delivery it has not finished.
///
/// THE WHOLE RESET IS REFUSED, not the busy half. A team where some members remember the last task
/// and others do not is a worse state than either, and it is one nobody asked for.
/// </summary>
public sealed class TeamBusyException(string team, IReadOnlyList<string> busy)
    : InvalidOperationException(
        $"'{team}' cannot be reset while {string.Join(", ", busy)} "
        + (busy.Count == 1 ? "is" : "are") + " still working.")
{
    public string Team { get; } = team;

    public IReadOnlyList<string> Busy { get; } = busy;
}

/// <summary>
/// Hands a team a clean slate without deleting it: KEEP the Agent Containers, reset the substrate
/// underneath them.
///
/// ITS OWN CLASS, not a method on <see cref="TeamRegistry"/>, for the reason
/// <see cref="TeamDeletion"/> gives: it needs the cursor, pending-delivery, message and session
/// stores and the data root, none of which the registry has or should acquire.
///
/// NOT A NEW MECHANISM. <c>team_members.floor_seq</c> already exists and already does both jobs a
/// reset needs - it is where the cursor starts, so nothing old is DELIVERED, and where the ledger
/// starts, so nothing old is REMEMBERED. This advances it on demand rather than only at creation.
///
/// THE ORDER IS THE DESIGN, and each step is placed against a way of getting it wrong:
///
/// 0. <b>Refuse, rather than offer an option.</b> A Running member owns a live child process, and
///    accepted work is work somebody was promised. Checked for EVERY targeted member before
///    anything moves, so a refusal leaves the team exactly as it was.
/// 1. <b>Read the head ONCE</b>, and give every member the same floor. Two members floored at
///    different heads means a message visible to one and not the other, produced by a single
///    operation that claimed to treat them alike.
/// 2. <b>End the Concierge sessions</b>, before anything touches directories - one with a
///    shell sitting in <c>teams/&lt;team&gt;/docs</c> holds a handle that would make that clear fail.
/// 3. <b>Per member: the ROW, then the cursor, then the live object.</b> The durable fact first. A
///    crash after it and before anything else leaves a database that says the reset happened, and
///    <c>RestoreAsync</c> brings the member back at the new floor; a crash before it leaves a
///    database that says nothing happened. There is no ordering here that produces a half-reset.
/// 4. <b>The purge, if asked</b> - AFTER flooring, never before. Flooring is what makes the team
///    correct; the purge is disk and confidentiality. A purge that fails leaves a reset that
///    succeeded.
/// 5. <b>Directories last</b>, for TeamDeletion's stated reason: the only step that is not a
///    database write and the only one that can fail halfway. Emptied through
///    <see cref="FolderRemoval"/>, as a team root is removed; what remains is NAMED path by path,
///    not swallowed, and recorded to be retried.
/// </summary>
public sealed class TeamReset(
    TeamRegistry teams,
    ContainerHost host,
    ITeamStore store,
    ICursors cursors,
    IPendingDeliveries pending,
    IMessageLog log,
    TeamPaths paths,
    FolderRemoval? removal = null)
{
    private readonly FolderRemoval _removal = removal ?? new FolderRemoval();

    /// <summary>How many rows one range read takes while enumerating a purge. Big enough that an
    /// ordinary instance is one or two round trips (a few thousand messages is about a megabyte)
    /// - and small enough that a million-row log does not arrive in one list.</summary>
    private const int PurgePage = 2_000;

    /// <summary>
    /// Resets <paramref name="team"/>. Null when there is no such team, which is a 404 rather than
    /// an error - the same rule <see cref="TeamDeletion.DeleteAsync"/> follows, because asking twice
    /// is not a fault.
    /// </summary>
    /// <exception cref="TeamBusyException">A targeted member is running or holds accepted work.</exception>
    public async Task<TeamWasReset?> ResetAsync(
        string team, TeamResetOptions options, CancellationToken ct = default)
    {
        // The STORED spelling, resolved once. Every path and row key below is built from it, so
        // taking the caller's capitalisation clears a directory that is not there and leaves the one
        // that is.
        if (teams.ExistingName(team) is not { } stored) return null;

        // The targeted members, resolved against what the team actually HOLDS. A name the team does
        // not have is dropped rather than refused: a dialog sends what it was showing, and a member
        // deleted between opening it and pressing Reset is not a fault worth failing everyone else's
        // reset over.
        var targeted = teams.ContainerIdsOf(stored)
            .Where(id => options.Members.Any(
                name => string.Equals(name, id.Name, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        // 0. Refuse the whole thing, before anything moves.
        var busy = new List<string>();

        foreach (var id in targeted)
        {
            if (host.Find(id) is not { } container) continue;

            var accepted = await pending.ForAsync(id, ct);

            if (ContainerBusy.IsBusy(container, accepted))
            {
                busy.Add(id.Name);
            }
        }

        if (busy.Count > 0) throw new TeamBusyException(stored, busy);

        var failures = new List<string>();

        // 1. ONE head, for everybody.
        var head = await log.HighestSeqAsync(ct);

        // 2. NO SESSION STEP, and nothing here reports one either. A Concierge is keyed on the
        //    person, so this reset cannot reach one - and a count it could only ever answer 0 to
        //    said the opposite of the truth in the one case that matters.

        // 3. Per member: row, cursor, live object.
        //
        // A separate list rather than a `continue` inside the loop, so "nothing was floored" and
        // "these were floored" come out of one place - a skipped body would leave `floored` silently
        // empty while the loop still ran.
        IReadOnlyList<ContainerId> flooring = options.ForgetHistory ? targeted : [];
        var floored = new List<string>();

        foreach (var id in flooring)
        {
            try
            {
                await store.SetFloorAsync(id.Team, id.Name, head, ct);

                // Safe unconditionally: AdvanceAsync is MAX(position, excluded.position) and cannot
                // move a cursor backwards.
                await cursors.AdvanceAsync(id, head, ct);

                // The card clears in every open browser as a CONSEQUENCE of the snapshot this
                // pushes, not through a second code path that could disagree with it.
                host.Find(id)?.Refloor(head);

                floored.Add(id.Name);
            }
            catch (Exception ex)
            {
                failures.Add($"{id}: {ex.Message}");
            }
        }

        // 4. The purge.
        var report = options.Purge
            ? await PurgeAsync(targeted, head, ct)
            : new PurgeReport(0, 0);

        // 5. The directories.
        var cleared = new List<string>();

        var remaining = new List<string>();

        foreach (var directory in ToClear(stored, targeted, options))
        {
            if (!Directory.Exists(directory)) continue;

            // EMPTIED, NEVER REMOVED. ProcessAgentRunner silently falls back to the Host's own
            // current directory for a container whose workspace is missing - which for anyone
            // running from a clone is this repository - so a removed workspace puts the next
            // run's AGENTS.md somewhere nobody expects. A team's documents folder is created
            // with the team and takes the same rule.
            var emptied = await _removal.EmptyAsync(directory, stored, ct);

            if (emptied.Complete)
            {
                cleared.Add(directory);
            }
            else
            {
                // Named, not swallowed, and recorded to be retried.
                remaining.AddRange(emptied.Remaining);
                failures.Add(
                    $"{directory}: {emptied.Remaining.Count} path(s) could not be removed; they are "
                    + "retried at the next start or on request: " + string.Join(", ", emptied.Remaining));
            }
        }

        return new TeamWasReset(
            stored, head, floored, report.Purged, report.Retained, cleared, failures, remaining);
    }

    /// <summary>
    /// The targeted members' messages, gone.
    ///
    /// WHICH CONTAINER A MESSAGE BELONGS TO IS DECIDED IN C#, by the two rules
    /// <see cref="MessageTeam.Of"/> owns: an addressed instruction carries the container in its TYPE,
    /// and a container event publishes its own qualified id as SOURCE. That disjunction lives in one
    /// language and must not be copied into SQL, so this pages the log and filters here.
    ///
    /// Bounded ABOVE by the floor just written: everything past it was published after the reset
    /// began, and a purge that took those would delete the reset's own first rows.
    /// </summary>
    private async Task<PurgeReport> PurgeAsync(
        IReadOnlyList<ContainerId> targeted, long head, CancellationToken ct)
    {
        var wanted = targeted
            .Select(id => id.ToString())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var seqs = new List<long>();
        var after = 0L;

        while (after < head)
        {
            var page = await log.ReadRangeAsync(after, PurgePage, ct);

            if (page.Count == 0) break;

            foreach (var message in page)
            {
                if (message.Seq > head) break;

                var qualified =
                    message.Type.StartsWith(MessageTypes.InstructionPrefix, StringComparison.Ordinal)
                        ? message.Type[MessageTypes.InstructionPrefix.Length..]
                        : message.Source;

                if (wanted.Contains(qualified)) seqs.Add(message.Seq);
            }

            after = page[^1].Seq;
        }

        return await log.DeleteAsync(seqs, ct);
    }

    /// <summary>Which directories this reset was asked to empty. Every one is keyed by the team's
    /// IDENTIFIER, which is the half of a team's two names that cannot change.</summary>
    private IEnumerable<string> ToClear(
        string team, IReadOnlyList<ContainerId> targeted, TeamResetOptions options)
    {
        if (options.ClearWorkspaces)
        {
            foreach (var id in targeted)
            {
                yield return paths.WorkspaceFor(new ContainerId(team, id.Name));
            }
        }

        if (options.ClearTranscripts)
        {
            foreach (var id in targeted)
            {
                yield return paths.TranscriptsFor(new ContainerId(team, id.Name));
            }
        }

        // Team-wide, and deliberately NOT `interactive-agents/<team>` - that is left alone, exactly
        // as Reload leaves it.
        if (options.ClearSharedDocuments) yield return paths.DocsFor(team);
    }
}
