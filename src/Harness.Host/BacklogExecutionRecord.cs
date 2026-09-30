using System.Text.Json;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// What one dispatch of a backlog item actually did: who ran, what it cost, and how it ended.
///
/// <para>
/// MOST OF THIS NEEDS NO COPY, BECAUSE THE MESSAGE LOG IS NEVER TOUCHED BY TEAM DELETION. Members,
/// workflow seq, token totals and outcomes all stay derivable from the dispatch's correlation after
/// the team is gone - whether the team still exists is irrelevant to the query. A copy would be a second store of a fact, free to disagree with the log,
/// and the log's answer is the one that cannot be wrong.
/// </para>
///
/// <para>
/// WHAT GENUINELY DIES WITH THE TEAM IS ITS NAME, which is not on the log at all - the log carries
/// the team ID inside a container's source. That one field is denormalised onto
/// <c>backlog_dispatches</c>, for the reason <c>tenant_events.actor_email</c> is: "deleted by
/// (unknown)" is not an audit trail.
/// </para>
/// </summary>
/// <param name="Members">Who ran under this dispatch, in the order they first appear.</param>
/// <param name="Outcome">
/// `completed`, `failed`, or `unknown`. UNKNOWN IS A REAL STATE AND IS NOT A GUESS: an item
/// archived before its dispatch finished freezes what was known and says so, rather than inventing a
/// completion that never happened.
/// </param>
/// <param name="RunsWithoutUsage">
/// THE NUMBER THAT STOPS THE TOTAL LYING BY OMISSION. Runs whose Agent reported no usage, plus rows
/// predating capture and `UsageSource.ExcludedEstimate` rows, which count as runs WITHOUT usage rather
/// than as spend - those numbers cannot be re-derived from an append-only log, and a dashboard must
/// not paint invented spend. A total alone lies by omission; freezing it would make the lie
/// permanent, which is why this travels beside it into <c>frozen_stats</c>.
/// </param>
public sealed record BacklogExecutionStats(
    long Correlation,
    IReadOnlyList<string> Members,
    int Instructions,
    string Outcome,
    long Tokens,
    int RunsWithUsage,
    int RunsWithoutUsage,

    /// <summary>
    /// How long this dispatch's workflow took, or null when it has not finished.
    ///
    /// NULL IS "NOT MEASURED" AND IS NOT ZERO, the same rule the outcome follows: a dispatch still
    /// running has no elapsed time, and reporting 0 would be inventing one.
    ///
    /// MEASURED TO THE LAST ROW ON THE CORRELATION, NOT TO THE ROW THAT SET THE OUTCOME. A
    /// `Failed` row can arrive AFTER `workflow.completed` - an ordinary shape, see the comment
    /// where `outcome` is set - and when it does, this span runs to that later row rather than
    /// stopping at the declaration. So a member that finishes (or fails) after the Manager has
    /// already declared the workflow complete EXTENDS this number: it answers "when did everything
    /// actually settle", not "how long until the Manager said done". That is the honest reading for
    /// a concurrent workflow - where a slower member can be running work a faster sibling has nothing to do with - but a reader comparing this
    /// figure against a Manager's own sense of how long the workflow took should expect it to run
    /// a little long when a straggler trails the declaration.
    /// </summary>
    double? ElapsedSeconds = null,

    /// <summary>
    /// The platform's check of each solution package this dispatch's workflow wrote, read off its
    /// <c>solution.checked</c> rows: ready with its install link, or the problems. Null in a stats
    /// blob frozen before the notice existed; empty when the workflow wrote no package.
    /// </summary>
    IReadOnlyList<BacklogSolutionNotice>? Notices = null);

/// <summary>One <c>solution.checked</c> row, as the backlog item shows it.</summary>
/// <param name="Link">The install wizard's link, on a pass only.</param>
/// <param name="Problems">Each problem by file and field, on a fail only.</param>
public sealed record BacklogSolutionNotice(
    bool Ok, string Text, string Folder, string? Name, string? Version, string? Link, IReadOnlyList<string> Problems);

/// <summary>
/// Derives <see cref="BacklogExecutionStats"/> from the log, and freezes them when an item is
/// archived.
///
/// <para>
/// THERE IS A SECOND ERASER BESIDES TEAM DELETION, AND IT IS WHAT MAKES FREEZING LOAD-BEARING.
/// <c>IMessageLog.DeleteAsync</c> purges rows nothing currently cites and retention moves with the
/// slowest subscriber - so a purge takes the history the log was holding, quietly, long after
/// anybody was watching. Archiving is already the moment a person says *keep this for the record*,
/// so it is the right moment to stop deriving and start storing.
/// </para>
/// </summary>
public static class BacklogExecutionRecord
{
    public const string Completed = "completed";
    public const string Failed = "failed";

    /// <summary>Not a guess. See <see cref="BacklogExecutionStats.Outcome"/>.</summary>
    public const string Unknown = "unknown";

    /// <summary>
    /// The stats for one dispatch: FROZEN if it has been, otherwise derived from the log now.
    ///
    /// <para>
    /// The frozen copy wins unconditionally when it is there. That is the whole point - once a
    /// person has said keep this, a later purge must not change the answer, and re-deriving would
    /// let it.
    /// </para>
    /// </summary>
    public static async Task<BacklogExecutionStats> ForAsync(
        BacklogDispatch dispatch, IMessageLog log, CancellationToken ct = default, IUsageLedger? ledger = null)
    {
        if (dispatch.FrozenStats is { Length: > 0 } frozen)
        {
            try
            {
                if (JsonSerializer.Deserialize<BacklogExecutionStats>(frozen) is { } stored)
                {
                    return stored;
                }
            }
            catch (JsonException)
            {
                // A frozen blob we cannot read is not a reason to fail the screen, and it is not a
                // reason to pretend it says something either. Fall through and derive - which may
                // answer less than it once did, and says so honestly through `unknown`.
            }
        }

        return await DeriveAsync(dispatch.Correlation, log, ledger, ct);
    }

    /// <summary>
    /// What the log says about this correlation right now.
    ///
    /// <para>
    /// THE OUTCOME IS READ FROM THE DECLARATION, not inferred from whether anything is still
    /// running: `workflow.completed` is a Manager's claim about the workflow and is the only thing
    /// entitled to say it finished. Absent means UNKNOWN, which is a real answer here.
    /// </para>
    /// </summary>
    private static async Task<BacklogExecutionStats> DeriveAsync(
        long correlation, IMessageLog log, IUsageLedger? ledger, CancellationToken ct)
    {
        var rows = await log.ReadCorrelationAsync(correlation, ct);
        var spend = await log.GetWorkflowSpendAsync(correlation, ct);

        var members = new List<string>();
        var instructions = 0;
        var outcome = Unknown;
        var notices = new List<BacklogSolutionNotice>();

        foreach (var row in rows)
        {
            if (string.Equals(row.Type, MessageTypes.SolutionChecked, StringComparison.Ordinal)
                && Notice(row) is { } notice)
            {
                notices.Add(notice);
            }

            if (row.Type.StartsWith(MessageTypes.InstructionPrefix, StringComparison.Ordinal))
            {
                instructions++;

                // The member is on the TYPE for an addressed instruction and nowhere else - the
                // same place `MessageTeam.Of` reads a team from, and for the same reason.
                var addressed = row.Type[MessageTypes.InstructionPrefix.Length..];

                // The qualified form is `Team/Name`; the NAME is what a person reads. A row whose
                // type carries no `/` is an unqualified spelling rather than a fault, so it is skipped
                // rather than counted under a name that would be the team's.
                var slash = addressed.LastIndexOf('/');

                if (slash >= 0 && slash + 1 < addressed.Length)
                {
                    var member = addressed[(slash + 1)..];

                    if (!members.Contains(member, StringComparer.OrdinalIgnoreCase))
                    {
                        members.Add(member);
                    }
                }
            }

            if (string.Equals(row.Type, MessageTypes.WorkflowCompleted, StringComparison.Ordinal))
            {
                outcome = Completed;
            }
            else if (string.Equals(row.Type, MessageTypes.Failed, StringComparison.Ordinal)
                && outcome == Unknown)
            {
                // A failure does NOT overwrite a completion: a member can fail and the Manager can
                // still deliver, which is an ordinary shape. Only the absence of a declaration
                // leaves a failure as the last word.
                outcome = Failed;
            }
        }

        double? elapsed = null;

        if (outcome != Unknown && rows.Count > 0)
        {
            elapsed = (rows[^1].OccurredAt - rows[0].OccurredAt).TotalSeconds;
        }

        // A DECLARATION THE LOG NO LONGER HOLDS - a Reset's "Delete memory" takes the Manager's
        // `workflow.completed` with the rest of its rows - IS STILL IN THE WORKFLOW LEDGER, which
        // nothing deletes: the newest completion there says the workflow completed and how long it
        // took. Only when the log cannot say so itself, so a workflow the log still describes reads
        // exactly as it always did.
        if (outcome == Unknown
            && ledger is not null
            && await ledger.ReadWorkflowAsync(correlation, ct) is { HowClosed: WorkflowLedgerRow.Completed } declared)
        {
            outcome = Completed;
            elapsed = declared.ElapsedSeconds;
        }

        return new BacklogExecutionStats(
            correlation,
            members,
            instructions,
            outcome,
            spend.TokensSpent,
            spend.RunsWithMeasuredUsage,
            spend.RunsWithoutUsage,
            elapsed,
            notices);
    }

    /// <summary>A <c>solution.checked</c> row's notice, or null for a payload that does not read as
    /// one - which only the platform writes, so that is a fault to skip rather than show.</summary>
    private static BacklogSolutionNotice? Notice(Message row)
    {
        try
        {
            using var json = JsonDocument.Parse(row.Payload);
            var payload = json.RootElement;

            if (payload.ValueKind != JsonValueKind.Object
                || Text(payload, PayloadFields.Text) is not { } text
                || Text(payload, PayloadFields.Path) is not { } folder)
            {
                return null;
            }

            var ok = payload.TryGetProperty(PayloadFields.Ok, out var passed) && passed.ValueKind == JsonValueKind.True;
            var problems = payload.TryGetProperty(PayloadFields.Problems, out var listed) && listed.ValueKind == JsonValueKind.Array
                ? listed.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.String).Select(p => p.GetString()!).ToList()
                : [];

            return new BacklogSolutionNotice(
                ok, text, folder, Text(payload, PayloadFields.Name), Text(payload, PayloadFields.Version),
                ok ? Text(payload, PayloadFields.Link) : null, problems);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement payload, string field) =>
        payload.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>
    /// A WORKFLOW WAS DECLARED COMPLETE: move its backlog item to `declared`, ONCE.
    ///
    /// <para>
    /// `declared` AND NOT `implemented`, AND THAT IS THE WHOLE POINT. This writes what a
    /// Manager SAID - a claim about a workflow - and nothing here can see whether the work reached
    /// main. `implemented` means the work is in the product and is a PERSON'S value; landing is
    /// what earns it. One word carrying both claims would be read as the second.
    /// </para>
    ///
    /// <para>
    /// ITS OWN METHOD RATHER THAN FOUR LINES INSIDE THE ROUTE, and that is not tidiness. Reaching
    /// this through <c>POST .../workflow-complete</c> means getting a whole team past
    /// <c>WorkflowBusyState</c> first - which asks about every member, every queue and every pending
    /// delivery, and which the fixture's own completion wakes keep answering "busy". The wiring
    /// under test is one lookup and one narrow write; a test that can only reach it by quieting a
    /// live team is testing the fixture.
    /// </para>
    ///
    /// <para>
    /// ONCE PER DECLARATION, AND THE GUARD IS ON THE LOG RATHER THAN ON THE ITEM'S STATE. The
    /// obvious version - write only while the item is still `pending` - is WRONG: a person moving the item back to pending re-arms it, so the next declaration under that
    /// workflow takes their decision away again. It fails in exactly the direction the rule exists
    /// to prevent, and it fails silently.
    /// </para>
    ///
    /// <para>
    /// So the question asked is *is this the FIRST completion of this workflow*, which the
    /// append-only log answers and nothing can re-arm. The caller appends its declaration BEFORE
    /// calling, so the first one sees exactly one row.
    /// </para>
    ///
    /// <para>
    /// This is NOT the same as deriving the state. A derived status is recomputed on every read and
    /// cannot be overridden, which would be wrong here: a Manager declaring a workflow complete is a
    /// claim about the WORKFLOW, and the person is the authority on whether the ITEM is done.
    /// </para>
    ///
    /// <para>
    /// AND IT IS NEVER COMPUTED FROM CARD COMPLETION. Complete every card and the item does not move
    /// itself. Deriving it would make the rollup and the state two spellings of one fact, free to
    /// disagree - and the one a person set is the one they will trust.
    /// </para>
    ///
    /// <para>
    /// A DECLARATION IS ALWAYS RECORDED, WHETHER OR NOT THE ITEM MOVES. The caller appends its
    /// `workflow.completed` row BEFORE calling this, and a `false` here never unwrites it: the
    /// workflow's own record is the history, and it is not the backlog's to withhold. Everything
    /// below decides one thing only - whether the ITEM's state follows.
    /// </para>
    ///
    /// <para>
    /// AND IT DOES NOT MOVE WHILE ANOTHER DISPATCH OF THE SAME ITEM IS STILL BEING WORKED. The guard is on the loop below rather than here because
    /// the reasoning about WHICH correlations are asked - and which deliberately is not - only
    /// makes sense next to the code that does it.
    /// </para>
    ///
    /// <para>
    /// NOTHING AUTOMATIC EVER MOVES AN ITEM OUT OF `declared` AGAIN. There is no sweeper, no
    /// timeout and no second write from here - <c>declarations != 1</c> sees to that. An item
    /// declared and never landed reads `declared` forever, until a person reopens it to `pending`
    /// or marks it `implemented` themselves. That permanence is the feature: it is how
    /// declared-and-never-landed stays visible instead of tidying itself away. A second dispatch
    /// does not reset it either, and cannot arrive by accident - <c>RefuseUnlessReady</c> still
    /// admits only `ready` at both dispatch doors, so a declared item is undispatchable until a
    /// person moves it back.
    /// </para>
    /// </summary>
    /// <param name="snapshots">
    /// THE LIVE CONTAINERS, <c>host.Snapshots()</c> at the route. Passed in rather than reached for
    /// so the false-positive guard below can be pinned against synthetic snapshots - the same
    /// reason <see cref="BacklogInFlightState.RunningUnder"/> takes them.
    /// </param>
    /// <returns>True when this call is what moved it, so a caller can say so.</returns>
    public static async Task<bool> OnWorkflowCompletedAsync(
        long correlation,
        IBacklogStore backlog,
        IMessageLog log,
        IReadOnlyCollection<ContainerSnapshot> snapshots,
        CancellationToken ct = default)
    {
        if (await backlog.DispatchForCorrelationAsync(correlation, ct) is not { } dispatch)
        {
            return false;
        }

        var declarations = (await log.ReadCorrelationAsync(correlation, ct))
            .Count(row => string.Equals(
                row.Type, MessageTypes.WorkflowCompleted, StringComparison.Ordinal));

        if (declarations != 1) return false;

        // NOBODY MAY STILL BE WORKING THIS ITEM. A Manager can truthfully declare a DUPLICATE
        // dispatch complete while other developers are still running on the item. The declaration
        // is honest about ITS workflow; this is what asks about the ITEM.
        //
        // THE DECLARED WORKFLOW'S OWN BUSY STATE IS DELIBERATELY NOT RE-ASKED HERE, and that is not
        // an oversight - it is the only way this can be right. `WorkflowBusyState` already asked it
        // at the door, and it knows to EXCLUDE the declaring container; this does not have that
        // container's id, and the declaring Manager is by definition Running under the correlation
        // it is declaring, from inside this very call. Re-asking would refuse every declaration
        // ever made.
        //
        // What the door cannot ask, and this can, is whether ANOTHER dispatch of the SAME ITEM is
        // still being worked. `ready` SURVIVES A DISPATCH, so an item can hold two live dispatches
        // at once and that is how the duplicate existed. Every dispatch is asked rather than only
        // the latest, because "is anybody working this item" is the question and which of two
        // duplicates happened to be created second is not an answer to it.
        //
        // `BacklogInFlightState.RunningUnder` IS THE PLATFORM'S OWN PREDICATE and is asked rather
        // than re-implemented, so this and the in-flight mark on the Backlog screen can never
        // disagree about what Running means. It is narrow on purpose - `CurrentCorrelation`, not
        // merely "a member of that team is busy" - so a member on a different job does not freeze
        // this item.
        var dispatches = await backlog.DispatchesAsync(dispatch.Item, ct);

        foreach (var other in dispatches)
        {
            if (other.Correlation == correlation) continue;

            if (BacklogInFlightState.RunningUnder(other.Correlation, snapshots))
            {
                // NOT A WRITE AND NOT AN UNDO. The declaration row is already on the log and stays
                // there - the workflow's own record is the history and is not the backlog's to
                // withhold. Only the ITEM's state is held back, and `declarations != 1` means this
                // workflow will not come back for it: the item waits for a PERSON.
                return false;
            }
        }

        if (await backlog.GetAsync(dispatch.Item, ct) is not { } item) return false;

        // Already there is not a failure and not a write. An item already `declared`, or one a
        // PERSON had already marked `implemented`, is at or past where this would have put it -
        // and a hand-set `implemented` is never taken back off them.
        if (string.Equals(item.State, BacklogStates.Declared, StringComparison.Ordinal)
            || string.Equals(item.State, BacklogStates.Implemented, StringComparison.Ordinal))
        {
            return false;
        }

        await backlog.UpdateAsync(dispatch.Item, state: BacklogStates.Declared, ct: ct);

        return true;
    }

    /// <summary>
    /// Freezes every dispatch of one item, or thaws them all.
    ///
    /// <para>
    /// EVERY DISPATCH, not only the latest: an item dispatched twice has two histories and archiving
    /// is a statement about the ITEM. Restoring clears them and it goes back to deriving;
    /// re-archiving freezes again from whatever the log still holds, which may by then be less.
    /// </para>
    /// </summary>
    public static async Task FreezeAsync(
        long item,
        bool freeze,
        IBacklogStore backlog,
        IMessageLog log,
        CancellationToken ct = default,
        IUsageLedger? ledger = null)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");

        foreach (var dispatch in await backlog.DispatchesAsync(item, ct))
        {
            if (!freeze)
            {
                await backlog.FreezeDispatchAsync(dispatch.Id, null, null, ct);
                continue;
            }

            // DERIVED, never read back from a frozen copy - re-freezing an already-frozen dispatch
            // must take what the LOG says now rather than copying a copy forward.
            var stats = await DeriveAsync(dispatch.Correlation, log, ledger, ct);

            await backlog.FreezeDispatchAsync(
                dispatch.Id, now, JsonSerializer.Serialize(stats), ct);
        }
    }
}
