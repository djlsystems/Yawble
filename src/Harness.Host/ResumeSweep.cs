using System.Globalization;
using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// ONE AUTOMATIC RESUME THE PLATFORM INTENDS TO PERFORM: whose failure it belongs to, when it
/// fires, and which of the bounded attempts it would be.
/// </summary>
/// <param name="Attempt">
/// 1 for the first automatic resume on this workflow. Counted over the WORKFLOW rather than over
/// the failure, because the bound exists to stop a thread spending forever and a thread that failed
/// five different ways has still been resumed five times.
/// </param>
public sealed record PendingResume(
    string Team,
    ContainerId Container,
    long FailureSeq,
    long CorrelationId,
    string FailureClass,
    DateTimeOffset ResumeAt,
    int Attempt);

/// <summary>
/// THE CLOCK IN FRONT OF THE NUDGE.
///
/// <para>
/// THERE IS NO NEW MECHANISM HERE, and that is the design rather than a summary of it. A person's
/// Nudge resumes a failed workflow; this is the same call, addressed to the same Manager,
/// with the same causation, fired by a timer instead of a finger. Everything that makes a nudge
/// work - the causation being the correlation itself, `MessageTeam.Of` reading the addressed type,
/// the member's own queue and ceiling - is inherited rather than rebuilt.
/// </para>
///
/// <para>
/// SAME CORRELATION, WHICH IS THE POINT. The causation of the instruction is the correlation id, so
/// the Manager wakes with that thread's own history. A resume that rooted a new workflow would be a
/// fresh job wearing a rescue's name: everything before it orphaned, and the projection never
/// connecting the two.
/// </para>
///
/// <para>
/// IT WAKES NOBODY NEW. WHO is woken does not change - a Manager already subscribes to
/// `agentContainer.failed`, and a resume that woke somebody else would race a manager's own retry
/// logic. The recipient here is exactly the recipient of the human nudge: the team's Manager. The
/// race is closed by the OTHER half of <see cref="PendingAsync"/>: a member that has started again
/// since its failure has no pending resume at all, so a Manager that handled the failure itself
/// cancels the resume by doing so.
/// </para>
///
/// <para>
/// EVERY DECISION IS DERIVED FROM THE APPEND-ONLY LOG and nothing is remembered between ticks.
/// There is no pending-resume table to keep in step, a Host restart loses nothing, and the same
/// tick that would fire a resume is the tick that discovers it is no longer wanted. Idempotence
/// falls out for free: a fired resume leaves a row on the thread naming the failure seq it
/// answered, and that row is what stops it firing twice.
/// </para>
///
/// <para>
/// IT DOES NOT TOUCH `ResumePendingAsync`. That method's unstarted/STARTED split is about a run the
/// Host was restarted out from under and is a different thing entirely. Nothing here reads it, writes it, or shares a code path with it.
/// </para>
/// </summary>
public sealed class ResumeSweep(
    TeamRegistry teams,
    ContainerHost host,
    IMessageLog log,
    int maxAutomaticResumesAtStart = ResumeSweep.DefaultMaxAutomaticResumes,
    ILogger<ResumeSweep>? diagnostics = null,
    Func<int>? maxAutomaticResumesNow = null)
{
    /// <summary>`resume.maxAutomatic`, read at every use so a change applies at the next
    /// sweep. The constructor's figure is what a fixture with no settings gets.</summary>
    private int MaxAutomaticResumes => maxAutomaticResumesNow?.Invoke() ?? maxAutomaticResumesAtStart;

    /// <summary>
    /// HOW MANY TIMES THE PLATFORM WILL RESUME ONE WORKFLOW BY ITSELF before it stops and leaves it
    /// to a person.
    ///
    /// BOUNDED IS PART OF THE DESIGN, NOT POLISH: a silent automatic retry is how a quota
    /// failure becomes a spend failure, and an unbounded one can spend a night's worth of tokens
    /// without anybody meaning to. Three is chosen to cover the case this exists for - a provider resetting at midnight, a second wall an hour later - without
    /// letting a thread that keeps hitting the same wall spend all night walking into it.
    /// </summary>
    public const int DefaultMaxAutomaticResumes = 3;

    /// <summary>
    /// How many recent `agentContainer.failed` rows a tick looks at.
    ///
    /// A WINDOW RATHER THAN THE WHOLE LOG, for `ReadLatestAsync`'s own reason: this runs on a timer
    /// forever, and an unbounded read grows with the log. A failure that falls out of the window
    /// before its reset time arrives loses its automatic resume and keeps its card, which is the
    /// safe direction - it degrades to what the platform did before this existed, which is a person
    /// pressing Nudge.
    /// </summary>
    private const int Window = 400;

    /// <summary>
    /// The marker that makes a resume RECOGNISABLE AS ONE on the thread it joins.
    ///
    /// Written on the instruction payload rather than inferred from the source or the wording,
    /// because both of those are things a person could reproduce by hand and this field decides
    /// whether the platform may spend again. It is also what makes the bound countable and the
    /// firing idempotent, from the log alone.
    /// </summary>
    private const string AutomaticResumeField = "automaticResume";

    /// <summary>Which failure seq this resume answers - the whole of its idempotence.</summary>
    private const string ResumeOfField = "resumeOf";

    private const string AttemptField = "resumeAttempt";

    /// <summary>
    /// Every automatic resume this platform currently intends to perform, due or not.
    ///
    /// THE CARD READS THIS TOO, which is why it returns the not-yet-due ones. "Visible" in the
    /// owner's decision means a person can see a resume COMING, and a list that only ever contained
    /// resumes that were already firing would show nothing until it was too late to object.
    /// </summary>
    public async Task<IReadOnlyList<PendingResume>> PendingAsync(
        DateTimeOffset now, CancellationToken ct = default)
    {
        // ALSO RETURNS ADDRESSED INSTRUCTIONS whatever the type filter says - a documented widening
        // of ReadLatestAsync's contract - so the filter is applied again here rather than trusted.
        var rows = await log.ReadLatestAsync(0, [MessageTypes.Failed], Window, ct);

        // THE LAST FAILURE PER MEMBER, and only that one. An older failure on the same member has
        // been overtaken: whatever happened after it is a better account of where that member is,
        // and resuming on the strength of it would be resuming a fact that is no longer true.
        var latest = new Dictionary<string, Message>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            if (!string.Equals(row.Type, MessageTypes.Failed, StringComparison.Ordinal)) continue;

            if (!latest.TryGetValue(row.Source, out var seen) || row.Seq > seen.Seq)
            {
                latest[row.Source] = row;
            }
        }

        var pending = new List<PendingResume>();

        foreach (var failure in latest.Values)
        {
            if (await ResumeForAsync(failure, now, ct) is { } resume) pending.Add(resume);
        }

        return pending;
    }

    /// <summary>
    /// One tick: tell every card what is coming, then fire whatever is due.
    ///
    /// THE CARD IS UPDATED BEFORE ANYTHING IS SPENT, and on every tick rather than only when
    /// something changes, because withdrawing a promise matters as much as making one: a member
    /// whose bound has just been spent, or which has started again by itself, has its promised
    /// resume taken off its card by the same pass that would have fired it.
    /// </summary>
    public async Task<IReadOnlyList<PendingResume>> SweepAsync(
        DateTimeOffset now, CancellationToken ct = default)
    {
        var pending = await PendingAsync(now, ct);
        var promised = new HashSet<ContainerId>();

        foreach (var resume in pending)
        {
            host.Find(resume.Container)?.MarkResumePending(resume.ResumeAt);
            promised.Add(resume.Container);
        }

        // WITHDRAWN FROM EVERYONE ELSE. A snapshot carrying a `resumeAt` that this sweep no longer
        // intends to honour is the one failure mode a visible promise has.
        foreach (var snapshot in host.Snapshots())
        {
            if (snapshot.ResumeAt is null) continue;

            // `Id` AND NOT `Name`. `ContainerSnapshot.Name` is the LABEL - equal to the identifier
            // for every member nobody relabelled, which is exactly how this reads correctly in a test
            // and targets the wrong container the first time somebody gives a member a display
            // name. `Id` is the half of the pair every path and every event type is built from.
            var id = new ContainerId(snapshot.Team, snapshot.Id);

            if (!promised.Contains(id)) host.Find(id)?.MarkResumePending(null);
        }

        var fired = new List<PendingResume>();

        foreach (var resume in pending)
        {
            if (resume.ResumeAt > now) continue;

            if (await FireAsync(resume, ct)) fired.Add(resume);
        }

        return fired;
    }

    /// <summary>
    /// Whether this failure still deserves an automatic resume, and when.
    ///
    /// EVERY REFUSAL BELOW IS A `return null` WITH A REASON WRITTEN BESIDE IT, deliberately rather
    /// than one combined predicate: this method decides whether the platform spends money without
    /// being asked, and the next person to widen it should have to delete a sentence to do it.
    /// </summary>
    private async Task<PendingResume?> ResumeForAsync(
        Message failure, DateTimeOffset now, CancellationToken ct)
    {
        JsonElement payload;

        try
        {
            payload = JsonDocument.Parse(failure.Payload).RootElement;
        }
        catch (JsonException)
        {
            // THE LOG IS APPEND-ONLY. A row written by hand or by an older build that will not parse
            // is not a resume candidate, and it is certainly not a reason to stop sweeping.
            return null;
        }

        if (payload.ValueKind != JsonValueKind.Object) return null;

        var failureClass = Text(payload, PayloadFields.FailureClass);

        // THE OWNER'S DECISION, AND THE ONLY GATE THAT MATTERS. Quota and rate resume; transport,
        // timeout, agent-fault and unknown never do - including `unknown`, which IS the default, so
        // a row with no class at all (every row written before classes existed) lands here and
        // stops. The append-only log keeps working precisely because this answers false for it.
        if (!FailureClasses.ResumesAutomatically(failureClass)) return null;

        // A QUOTA WITH NO STATED RESET GETS NO RESUME. See AgentFailureEvidence.DefaultRateBackoff:
        // rate may fall back to a horizon of ours, quota may not, and by this point a rate failure
        // that was going to fall back already has. A missing value here is the provider having said
        // nothing, and waiting until a moment nobody named is inventing one.
        if (Text(payload, PayloadFields.RetryAfter) is not { } stamp
            || !DateTimeOffset.TryParse(
                stamp,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var resumeAt))
        {
            return null;
        }

        // `Team/Name`. A source that is not a container's qualified id is not a member's failure -
        // `agentContainer.failed` publishes the container's own id, but this is read off a row that
        // may have been written by anything, so it is checked rather than assumed.
        if (Qualified(failure.Source) is not { } container) return null;

        // A TEAM THIS HOST NO LONGER HAS is not resumed, and neither is one with no Manager to
        // nudge - a legitimate state (a hire converted with no Agent or no Prompt gets a team and
        // no Manager), which the nudge route already refuses in the same words.
        if (teams.ExistingName(container.Team) is not { } team) return null;
        if (host.Find(new ContainerId(team, TeamRegistry.DefaultManagerName)) is null) return null;

        var thread = await log.ReadCorrelationAsync(failure.CorrelationId, ct);
        var attempts = 0;

        foreach (var row in thread)
        {
            if (row.Seq <= failure.Seq)
            {
                // Still counted: the bound is the WORKFLOW's, and resumes earlier in the thread
                // were spent on it. Only the "has this member moved on" tests below care about
                // being after the failure.
                if (IsAutomaticResume(row, out _)) attempts++;
                continue;
            }

            // A WORKFLOW SOMEBODY ENDED IS NOT RESUMED. Closing says stop counting this, and a
            // platform that woke it again on a timer would be overruling the person who closed it.
            if (string.Equals(row.Type, MessageTypes.WorkflowClosed, StringComparison.Ordinal)
                || string.Equals(row.Type, MessageTypes.WorkflowCompleted, StringComparison.Ordinal))
            {
                return null;
            }

            if (IsAutomaticResume(row, out var answered))
            {
                attempts++;

                // ALREADY DONE FOR THIS FAILURE. The whole of the idempotence, and it is a fact on
                // the log rather than a flag in memory - so a Host that restarts between the append
                // and the next tick does not resume twice.
                if (answered == failure.Seq) return null;
            }

            // THE MEMBER HAS MOVED ON. A run started, finished or failed again since, so this
            // failure is no longer its last word - and if a Manager retried it by itself, this is
            // the line that keeps the two from racing.
            if (string.Equals(row.Source, failure.Source, StringComparison.OrdinalIgnoreCase)
                && (string.Equals(row.Type, MessageTypes.Started, StringComparison.Ordinal)
                    || string.Equals(row.Type, MessageTypes.Completed, StringComparison.Ordinal)
                    || string.Equals(row.Type, MessageTypes.Failed, StringComparison.Ordinal)))
            {
                return null;
            }
        }

        // THE BOUND. Spent means spent: the card keeps its failure and its class, loses its promise
        // of a resume, and waits for a person - which is exactly where this product was before any
        // of this existed, reached deliberately instead of by accident.
        if (attempts >= MaxAutomaticResumes) return null;

        return new PendingResume(
            team,
            container,
            failure.Seq,
            failure.CorrelationId,
            failureClass!,
            resumeAt,
            attempts + 1);
    }

    /// <summary>
    /// The nudge itself. Returns whether anything was appended.
    /// </summary>
    private async Task<bool> FireAsync(PendingResume resume, CancellationToken ct)
    {
        var manager = new ContainerId(resume.Team, TeamRegistry.DefaultManagerName);

        // RE-CHECKED AT THE MOMENT OF FIRING. The list was computed a few lines above, but a
        // Manager can be removed between the two and appending an instruction addressed to a
        // container that does not exist puts a message on the log nothing will ever read.
        if (host.Find(manager) is null) return false;

        var when = resume.ResumeAt.ToUniversalTime().ToString("u", CultureInfo.InvariantCulture);

        // IT SAYS WHAT HAPPENED AND WHAT IS LEFT. A Manager woken with "carry on" and no account of
        // why it stopped re-derives the whole thread or, worse, starts again; and a Manager that is
        // not told this was its second of three automatic resumes cannot judge whether to keep
        // trying. The bound is spent on its behalf, so it is told about it.
        var text =
            $"The platform resumed this workflow automatically. Its last run failed with a "
            + $"{resume.FailureClass} failure - the provider said to come back at {when} UTC, and "
            + $"that time has passed. This is automatic resume {resume.Attempt} of "
            + $"{MaxAutomaticResumes} for this workflow; after that nothing further happens until a "
            + "person nudges it. Look at everything under this thread and either continue the work, "
            + "declare it complete with workflow-complete, or say why it should stop.";

        var parts = InstructionText.Split(text);

        await log.AppendAsync(
            new NewMessage(
                MessageTypes.InstructionFor(manager),
                JsonSerializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [PayloadFields.Instruction] = text,
                    ["subject"] = parts.Subject,
                    [PayloadFields.Body] = parts.Body,
                    [AutomaticResumeField] = true,
                    [ResumeOfField] = resume.FailureSeq,
                    [AttemptField] = resume.Attempt,
                    [PayloadFields.FailureClass] = resume.FailureClass,
                }),

                // `host`, the source every platform-published instruction already uses. NOT a
                // person's id: nothing here was done by a person, and a thread that read as though
                // somebody had pressed Nudge would be the log telling a lie about who spent the
                // money.
                "host",

                // THE CAUSATION IS THE CORRELATION ITSELF, exactly as the human nudge does it, and
                // it is what keeps the Manager's history intact. A resume that started a new
                // correlation orphans everything before it.
                resume.CorrelationId),
            ct);

        diagnostics?.LogInformation(
            "Resumed workflow {Correlation} automatically after a {Class} failure at seq {Seq} "
            + "(attempt {Attempt} of {Max}).",
            resume.CorrelationId,
            resume.FailureClass,
            resume.FailureSeq,
            resume.Attempt,
            MaxAutomaticResumes);

        return true;
    }

    /// <summary>
    /// Whether this row is one of this sweep's own resumes, and which failure it answered.
    ///
    /// READ OFF THE PAYLOAD MARKER and never off the source or the wording - see
    /// <see cref="AutomaticResumeField"/>. A person may write anything into an instruction; this
    /// field is what the platform wrote about itself.
    /// </summary>
    private static bool IsAutomaticResume(Message message, out long answered)
    {
        answered = 0;

        if (!message.Type.StartsWith(MessageTypes.InstructionPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var payload = JsonDocument.Parse(message.Payload).RootElement;

            if (payload.ValueKind != JsonValueKind.Object
                || !payload.TryGetProperty(AutomaticResumeField, out var flag)
                || flag.ValueKind != JsonValueKind.True)
            {
                return false;
            }

            if (payload.TryGetProperty(ResumeOfField, out var of)
                && of.ValueKind == JsonValueKind.Number
                && of.TryGetInt64(out var seq))
            {
                answered = seq;
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// A `Team/Name` source as a container id, or null when it is not one.
    ///
    /// `ContainerId.Parse` THROWS, which is right for a caller that has an identifier and expects it
    /// to be valid. This one has a log row's Source field and no such expectation.
    /// </summary>
    private static ContainerId? Qualified(string source)
    {
        try
        {
            return ContainerId.Parse(source);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement payload, string field) =>
        payload.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() is { Length: > 0 } text ? text : null
            : null;
}
