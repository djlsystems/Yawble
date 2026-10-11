using Harness.Containers;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// IS ANYTHING STILL IN FLIGHT UNDER THIS WORKFLOW - the question `workflow-complete` has to ask
/// once a team can hold several at a time.
///
/// BESIDE <c>TeamBusyState</c> AND NOT A REPLACEMENT FOR IT. Deletion is a team-level question and
/// still wants the team answer; narrowing that one would silently widen what deletion is willing to
/// destroy. Two questions, two methods, and each caller says which it is asking.
///
/// Why it cannot stay team-scoped: with two open workflows, each declaration is refused by the
/// other's Running or queued state, so NEITHER is ever declared and both read UNDECLARED forever. The
/// team-scoped check coincided with reality only while the Manager was the sole originator.
///
/// A PENDING ROW'S WORKFLOW IS LOOKED UP ON THE LOG. `pending_deliveries` holds a seq and nothing
/// else, deliberately - it is a durable marker, not a projection - so its correlation is read from
/// the message it names. That is one small lookup per outstanding row, and outstanding rows on a
/// team about to declare a workflow finished are few by construction.
///
/// THIS CHECK READS PENDING ROWS ONLY, unlike `TeamBusyState` which also reads `QueueDepth` directly
/// - so a container constructed without the optional `IPendingDeliveries` seam is invisible to the
/// queued-work half of this check entirely. `Program.cs` always wires it for a real host; this is
/// documentation of a known trap for a future caller, not a live bug.
/// </summary>
internal static class WorkflowBusyState
{
    /// <param name="watcher">
    /// The Manager, when the OWNER of a member-owned workflow is declaring it. A Manager woken by a
    /// row the owner or another member wrote in this workflow is reading what happened, not working
    /// in it - and counting that run refused the owner for the very wake its own completion caused,
    /// while the Manager was refused as not the owner: the two went round until a person closed it.
    /// Such a run, and the watcher's queued rows of the same kind, are left out; a watcher TOLD to
    /// do work here still counts. Null - every other caller - counts everything, as before.
    /// </param>
    public static async Task<IReadOnlyList<string>> DescribeAsync(
        string team,
        long correlation,
        ContainerHost host,
        IPendingDeliveries pending,
        IMessageLog log,
        ContainerId? excluded,
        CancellationToken ct,
        ContainerId? watcher = null)
    {
        var busy = new List<string>();

        foreach (var snapshot in host.Snapshots().Where(s =>
                     string.Equals(s.Team, team, StringComparison.OrdinalIgnoreCase)))
        {
            var member = new ContainerId(snapshot.Team, snapshot.Id);

            // The caller's own RUNNING state is excluded, and only that field - it is running BY
            // DEFINITION, being the thing making this call. Its QUEUE still counts: accepted work
            // not yet started means this workflow is not finished.
            //
            // `CurrentCorrelation` is what makes this narrow. A member running under ANOTHER
            // workflow is not this workflow's business, and asking otherwise is the deadlock above.
            if ((excluded is null || !member.Equals(excluded))
                && snapshot.State == ContainerState.Running
                && snapshot.CurrentCorrelation == correlation)
            {
                // THE WATCHER READING WHAT HAPPENED IS NOT WORK IN THE WORKFLOW. See `watcher`.
                // Its cause is read NOW, and a run that ended in between reads as busy: only a
                // cause that is positively a watching row is left out.
                if (watcher is not null && member.Equals(watcher)
                    && host.Find(member)?.CurrentCausation is { } cause
                    && await log.FindAsync(cause, ct) is { } woke
                    && Watching(woke, team, correlation, watcher))
                {
                    continue;
                }

                busy.Add($"{snapshot.Name} is Running");
            }
        }

        var excludedSubscriber = excluded?.ToString();
        var matchedSubscribers = new List<string>();

        foreach (var row in await pending.ForTeamAsync(team, ct))
        {
            // The excluded STARTED row is this caller's own durable in-flight marker for the run it
            // is calling from. Unstarted rows still count, whoever holds them.
            if (excludedSubscriber is not null
                && row.Started
                && string.Equals(row.Subscriber, excludedSubscriber, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // A row whose message is GONE cannot be attributed to a workflow, and the safe reading
            // is that it is not this one: refusing on it would make an unattributable row block
            // every declaration on the team forever, with nothing naming what to do about it.
            if (await log.FindAsync(row.Seq, ct) is not { } message) continue;
            if (message.CorrelationId != correlation) continue;

            // The watcher's deliveries OF THE SAME KIND - another member's row it is only to read -
            // are left out with its run. Anything else queued for it, an instruction above all,
            // still counts.
            if (watcher is not null
                && string.Equals(row.Subscriber, watcher.ToString(), StringComparison.OrdinalIgnoreCase)
                && Watching(message, team, correlation, watcher))
            {
                continue;
            }

            matchedSubscribers.Add(row.Subscriber);
        }

        // GROUPED, LIKE `TeamBusyState`, AND FOR THE SAME REASON: ungrouped, three pending rows for
        // one member would repeat "X has work pending in this workflow" three times in a refusal
        // an AGENT reads and has to act on - noise that makes the one thing it actually needs (WHO
        // is still busy) harder to find, not easier.
        foreach (var group in matchedSubscribers.GroupBy(
                     subscriber => subscriber, StringComparer.OrdinalIgnoreCase))
        {
            var subscriber = group.Key;
            var slash = subscriber.LastIndexOf('/');
            var who = slash >= 0 && slash < subscriber.Length - 1
                ? subscriber[(slash + 1)..]
                : subscriber;
            var count = group.Count();

            busy.Add($"{who} has {count} pending deliver{(count == 1 ? "y" : "ies")} in this workflow");
        }

        return busy;
    }

    /// <summary>
    /// A row the <paramref name="watcher"/> is handed only to READ: not an instruction, under this
    /// workflow, written by a member of this team other than the watcher - a completion, a hand-back,
    /// a failure. A person's row, the platform's (`host`), and any instruction - told work - are not.
    /// </summary>
    internal static bool Watching(Message row, string team, long correlation, ContainerId watcher) =>
        row.CorrelationId == correlation
        && !row.Type.StartsWith(MessageTypes.InstructionPrefix, StringComparison.Ordinal)
        && ContainerId.TryParse(row.Source, out var source)
        && string.Equals(source.Team, team, StringComparison.OrdinalIgnoreCase)
        && !source.Equals(watcher);
}
