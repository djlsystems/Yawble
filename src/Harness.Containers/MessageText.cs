using System.Text.Json;
using Harness.Contracts;

namespace Harness.Containers;

/// <summary>
/// Turns a message into the sentence an agent should actually read.
///
/// An agent must never be handed the envelope. Passing raw JSON makes it parse a transport format
/// to find its instruction, and every agent would then need to know the shape of a message - which
/// is precisely the platform awareness the container exists to absorb.
///
/// It also has to READ like something worth reacting to. A manager woken by a worker finishing
/// should see "dev1 completed: ..." rather than a payload, because what it does next depends on
/// understanding what happened, and a model reading JSON keys is doing avoidable work.
/// </summary>
public static class MessageText
{
    public static string Of(IReadOnlyList<Message> messages)
    {
        if (messages.Count == 0) return string.Empty;
        if (messages.Count == 1) return Of(messages[0]);

        var lines = new List<string>(messages.Count + 1)
        {
            $"You have {messages.Count} new messages. They arrived in this order (oldest first, newest last):",
        };

        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            var newest = i == messages.Count - 1 ? " (newest)" : "";
            lines.Add(
                $"{i + 1}. [seq {message.Seq}] from {message.Source}{newest}: {Of(message)}");
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// What the class and any pending resume say, with a trailing space, or EMPTY when the row
    /// carries neither.
    ///
    /// EMPTY IS THE IMPORTANT ANSWER. An `agentContainer.failed` row may carry no class, the log is
    /// append-only, and such a row must read as a plain failure - which it does, because this
    /// contributes nothing to the sentence around it.
    ///
    /// The WORDS come from `FailureClasses.Sentence`, which is the one store for them on the C#
    /// side; this file decides only where they go and how a retry time is said.
    /// </summary>
    private static string FailureText(JsonElement? payload)
    {
        var failureClass = Field(payload, PayloadFields.FailureClass);

        if (FailureClasses.Sentence(failureClass) is not { } sentence) return string.Empty;

        // SAID ONLY FOR A CLASS THAT ACTUALLY RESUMES. `retryAfter` is what the PROVIDER said, and
        // a transport failure that happened to carry one would otherwise read as a promise the
        // platform has no intention of keeping - transport failures never resume automatically.
        if (FailureClasses.ResumesAutomatically(failureClass)
            && Field(payload, PayloadFields.RetryAfter) is { Length: > 0 } stamp)
        {
            return $"[{failureClass}] {sentence} The provider said to come back at {stamp}, and the "
                + "platform will resume this workflow then unless it has moved on. ";
        }

        return $"[{failureClass}] {sentence} ";
    }

    public static string Of(Message message)
    {
        var payload = Read(message.Payload);

        if (message.Type.StartsWith(MessageTypes.InstructionPrefix, StringComparison.Ordinal))
        {
            // Scheduled wakes stamp Source as schedule:<id>. Instructions already render their text
            // directly, so that source never needs to be read here.
            //
            // `instruction` AND NOTHING ELSE, and the payload's `subject` field must not be
            // preferred here however tidy that would read. A subject is a DISPLAY convenience for
            // a card's title - at most 80 characters, and for a long instruction it is the first
            // line of one. An agent handed that would be told LESS THAN THE PERSON WROTE, would
            // start work on a fragment, and NOTHING WOULD FAIL: the run reports success against an
            // instruction nobody gave. The board may summarise; the worker may not.
            return Field(payload, PayloadFields.Instruction) ?? message.Payload;
        }

        // The BARE name. `Message.Source` carries the qualified `Team/Name`, and that form is an
        // IDENTITY rather than a label - it exists so two teams' managers are two containers, not so
        // it appears in prose. A manager reading "Alpha/dev1 completed its work." is being handed a
        // platform detail about a team it is already the manager of, which is exactly the awareness
        // the container exists to absorb.
        //
        // READ FROM THE ENVELOPE, NEVER THE PAYLOAD. The envelope's source is the one place this
        // value lives; no payload field duplicates it.
        var who = Bare(message.Source);

        return message.Type switch
        {
            MessageTypes.Completed => $"{who} completed its work. It reported:\n{Field(payload, PayloadFields.Output) ?? "(no output)"}",

            // THE CLASS IN WORDS, AHEAD OF THE REASON, AND ONLY WHEN THERE IS ONE.
            //
            // A manager reading "FAILED" and a sentence retries the same thing; a manager told this
            // was a QUOTA stop with a resume already pending knows to leave it alone, which is the
            // whole point of there being a class at all. It goes FIRST for the reason the SPA's
            // `whileIdle` mark does: a failure's own text can be long, and a qualifier at the end of
            // it is a qualifier a reader has already stopped reading.
            //
            // OLD ROWS RENDER EXACTLY AS THEY DID. `FailureText` answers empty for a row with no
            // class and for a class this build has never heard of, so the append-only log keeps its
            // meaning and a row from a newer build degrades to today's sentence rather than to a
            // guess about a word.
            MessageTypes.Failed =>
                $"{who} FAILED. "
                + FailureText(payload)
                + (FailurePayloadText.FirstNonEmpty(payload, PayloadFields.LaunchError, PayloadFields.Output) ?? "(no detail)"),

            MessageTypes.NeedsDecision =>
                $"{who} needs a decision before continuing. "
                + (FailurePayloadText.FirstNonEmpty(payload, PayloadFields.Question, PayloadFields.Reason, PayloadFields.Output)
                    ?? "(no question)"),

            // AN AGENT GIVING UP. Without this arm the row would fall to the fallback and a manager
            // would read the ENVELOPE - the failure the kanban arms below exist to prevent, on a
            // type that matters more: `container.blocked` is deliberately IN the ledger
            // (`container.progress` is deliberately out) precisely so a manager reads back that a
            // member gave up rather than retrying what it abandoned, not `{"reason":"..."}`.
            //
            // WORDED AS A STOP, NOT A FAILURE. `container.blocked` is the AGENT'S decision and
            // `container.failed` is the PLATFORM's report of a run that did not complete - one
            // sentence for both would collapse a distinction the whole product is built on, and
            // this is the text a manager acts on.
            MessageTypes.Blocked =>
                $"{who} GAVE UP and is waiting on a person. "
                + (FailurePayloadText.FirstNonEmpty(payload, PayloadFields.Reason, PayloadFields.Output) ?? "(no reason)"),

            // A WORKER HANDING ITS PART BACK. It is read by the MANAGER, which is woken by this
            // row - the only agent-published type that wakes anybody - so the words have to say two
            // things at once: the work is DONE, and it is now the manager's move. Worded as a
            // success and never as a stop, so a manager never reads `GAVE UP` about finished work.
            //
            // It deliberately does NOT say the workflow is complete. Only a Manager declares that,
            // and a sentence here that implied otherwise would put the claim in the manager's
            // context anyway - the same failure mode this file's `blocked` arm guards against,
            // wearing the opposite costume.
            MessageTypes.Handback =>
                $"{who} HANDED BACK its finished work. It delivered: "
                + (FailurePayloadText.FirstNonEmpty(payload, PayloadFields.Delivered, PayloadFields.Output)
                    ?? "(no detail)")
                + " Nothing is owed on it; it has NOT declared the workflow complete.",

            MessageTypes.Rejected =>
                $"{who} REFUSED work because {Field(payload, PayloadFields.Reason) ?? "its queue was full"}. "
                + "Nothing was queued, so it has to be sent again or given elsewhere.",

            MessageTypes.Started => $"{who} started working.",
            MessageTypes.ScheduleSkipped => $"{who} skipped a scheduled wake. {Field(payload, PayloadFields.Reason) ?? ""}".Trim(),
            MessageTypes.WorkflowCompleted =>
                $"{who} declared the workflow completed. It delivered:\n{Field(payload, PayloadFields.Delivered) ?? "(no detail)"}",

            // A PERSON CLOSED THIS WORKFLOW, WHICH IS NOT A DECLARATION OF DELIVERY - see
            // Program.cs's own comment on the close route for why this is never `workflow.completed`.
            // `who` here is a bare user id rather than a container's name (Bare returns it whole,
            // exactly as it does for a card event's actor), and the reason is OPTIONAL - a fact this
            // sentence has to read correctly either way rather than manufacturing a placeholder for
            // "no reason given", which would look like data nobody actually wrote.
            //
            // `IsNullOrWhiteSpace`, matching `Changed`/`Wrote` below, not merely `is { }`: the route
            // trims and nulls a blank reason before it ever writes a row, but the log is append-only
            // and replayed, so a whitespace-only value from an older or different writer must read
            // exactly as "no reason given" rather than as a reason of nothing but spaces.
            MessageTypes.WorkflowClosed => Field(payload, PayloadFields.Reason) is { } reason
                && !string.IsNullOrWhiteSpace(reason)
                ? $"{who} closed this workflow, saying: {reason}"
                : $"{who} closed this workflow.",

            // A PAUSE IS THE ONE ROW THAT EXPLAINS WHY NOTHING ELSE IS HAPPENING, so the
            // sentence has to carry the figure and the way back - a Manager handed only "paused"
            // knows it is stopped and not what to tell the person reading its output.
            //
            // The reason is composed by the pump and already names which of the two bounds fired
            // and what was spent, so this arm reads it rather than recomposing a second sentence
            // that could disagree with the one on the row.
            MessageTypes.WorkflowPaused => Field(payload, PayloadFields.Reason) is { } pauseReason
                && !string.IsNullOrWhiteSpace(pauseReason)
                ? $"This workflow is PAUSED: {pauseReason} A person must resume it."
                : "This workflow is PAUSED and a person must resume it.",

            // `who` is a bare user id here, exactly as it is on the close arm above - a person
            // published this row, so Bare returns it whole.
            MessageTypes.WorkflowResumed => Field(payload, PayloadFields.Reason) is { } resumeReason
                && !string.IsNullOrWhiteSpace(resumeReason)
                ? $"{who} resumed this workflow, saying: {resumeReason}"
                : $"{who} resumed this workflow.",

            // A PERSON TOUCHED A CARD, and without these arms the fallback below hands the
            // Manager the ENVELOPE - it wakes to `{"cardId":"...","laneId":"in-progress",...}` and
            // has to parse a transport format to work out what it was woken for. That is the one
            // thing this file exists to prevent, and these three types are the only rows on the log
            // a person publishes directly, so they are the likeliest wake to be read by an agent
            // that was told nothing else.
            //
            // IT ALSO HAS TO WORK WITHOUT THE SKILL. The built-in kanban skill tells a Manager how to
            // read a card wake, but only a Manager that loads it reads it. A sentence here is the
            // half that reaches it either way.
            //
            // `change` is composed by the route (`Moved to in-progress`, `title from 'A' to 'B'`)
            // and carries BOTH values, because a Manager woken by an edit has no earlier version of
            // the card to compare against. `note` and `text` are what the PERSON typed, and they
            // are the whole reason the wake is worth anything. `who` is the actor: a card row's
            // source is a bare user id with no `/`, so `Bare` returns it whole.
            MessageTypes.KanbanCardMoved or MessageTypes.KanbanCardEdited =>
                $"{who} changed kanban card {Field(payload, PayloadFields.CardId) ?? "(unknown)"}"
                + Changed(Field(payload, PayloadFields.Change))
                + Wrote(Field(payload, PayloadFields.Note)),

            MessageTypes.KanbanCardCommented =>
                $"{who} commented on kanban card {Field(payload, PayloadFields.CardId) ?? "(unknown)"}: "
                + (Field(payload, PayloadFields.Text) ?? "(no comment)"),

            // ASSIGNMENT HAS TO BE VISIBLE IN THE TRAIL: a card
            // that changed hands and says so only by its header having a different name is a board
            // that cannot answer "who did what". These three are the sentences for it.
            //
            // The planned row names the ITEM rather than the card, because the card's id IS this
            // row's seq and repeating it would say the same number twice.
            MessageTypes.KanbanCardPlanned =>
                $"{who} planned this task"
                + (Number(payload, PayloadFields.BacklogItem) is { } item ? $" for {PlatformBacklogId.Format(item)}" : "")
                + (Field(payload, PayloadFields.Title) is { Length: > 0 } planned ? $": {planned}" : ""),

            // A REMOVAL'S SENTENCE NAMES THE MEMBER AND WHAT IT MEANS FOR THE WORK. `who` is the
            // member itself - its Source is the qualified `Team/Name`, which `Bare` reduces to the
            // name - because a container event is published BY the container it is about.
            MessageTypes.ContainerRemoved =>
                $"{who} was removed from the team; any task it held is unassigned.",

            // THE PLATFORM COULD NOT PUT THE WORK ON ORIGIN, and this arm is why the type is in the
            // ledger at all: a team that reads this back knows its branch is still only on this
            // machine, which it has no other way to find out - a member is not permitted to fetch and cannot answer "is my work pushed?" for itself.
            //
            // IT SAYS WHOSE FAULT IT IS NOT. `container.blocked` above had to be worded as a stop
            // rather than a failure for the same kind of reason: an unreachable origin is transport,
            // and a sentence that reads as an accusation sends a member to re-do work that is
            // perfectly fine. It names the retry instead, because the retry is the whole remedy -
            // the Git dialog's push button is the manual form of exactly this operation.
            //
            // `who` is the member whose acceptance triggered the push: the row's Source is that
            // member's qualified `Team/Name`, exactly as `container.failed`'s is, and the PLATFORM
            // publishes both. `reason` is git's own words, already redacted at the write.
            // `retryable: false` is a branch origin REFUSED: it was amended, rebased or reset after
            // it was pushed. Saying "safe to try again" there would send every wake to a retry that
            // fails forever, so this arm names the cause and the remedy instead.
            MessageTypes.RepoPushFailed when IsFalse(payload, PayloadFields.Retryable) =>
                $"Origin refused {who}'s branch because it was rewritten after it was pushed (amend, "
                + "rebase or reset), so origin holds a commit the branch no longer has. Retrying will "
                + "not help and the team's other branches were still pushed. Do not rewrite a pushed "
                + "branch again; a person has to reconcile this one. "
                + $"{Field(payload, PayloadFields.Repo) ?? "(unknown repo)"}: "
                + $"{Field(payload, PayloadFields.Branches) ?? "(no branches named)"}. "
                + (FailurePayloadText.FirstNonEmpty(payload, PayloadFields.Reason) ?? "(no detail)"),

            MessageTypes.RepoPushFailed =>
                $"The platform could not push {who}'s work to origin, so it exists only on this "
                + $"machine. Nothing moved on origin, so this is safe to try again. {Field(payload, PayloadFields.Repo) ?? "(unknown repo)"}: "
                + $"{Field(payload, PayloadFields.Branches) ?? "(no branches named)"}. "
                + (FailurePayloadText.FirstNonEmpty(payload, PayloadFields.Reason) ?? "(no detail)"),

            // NEVER FORCED, SO SAID. The tree is still on disk with whatever it holds; naming it is
            // what lets a person decide, since the platform will not.
            MessageTypes.RepoWorktreeLeft =>
                $"The platform left {who}'s worktree in place rather than force it: "
                + $"{Field(payload, PayloadFields.Worktree) ?? "(no path)"} "
                + $"({Field(payload, PayloadFields.Repo) ?? "(unknown repo)"}). "
                + (FailurePayloadText.FirstNonEmpty(payload, PayloadFields.Reason) ?? "(no detail)"),

            // A person opened a pull request upstream from the Git dialog.
            MessageTypes.RepoPullRequestOpened =>
                $"A pull request was opened upstream for {Field(payload, PayloadFields.Branch) ?? "(unknown branch)"} "
                + $"of {Field(payload, PayloadFields.Repo) ?? "(unknown repo)"}: {Field(payload, PayloadFields.Url) ?? "(no URL)"}.",

            // The Manager moved the clone's default branch. Reported, never reset.
            MessageTypes.RepoDefaultBranchMoved =>
                $"{who} {Field(payload, PayloadFields.Reason) ?? "moved the clone's default branch"} "
                + $"({Field(payload, PayloadFields.Repo) ?? "(unknown repo)"}). The platform reset nothing; "
                + "the work belongs on the team branch, and the clone's default branch is never committed to, merged into or moved.",

            // A person's Bring current brought the fork level with upstream; never forced.
            MessageTypes.RepoForkSynced =>
                $"The fork of {Field(payload, PayloadFields.Repo) ?? "(unknown repo)"} was fast-forwarded "
                + $"to upstream's {Field(payload, PayloadFields.Branch) ?? "(unknown branch)"}.",

            // Paths and a count - never contents, which the row does not carry either.
            MessageTypes.FileChanged => FileChanged(payload),

            // A person's click on a site. The payload is the page's, so it is quoted as JSON text.
            MessageTypes.SiteAction => SiteAction(payload),

            // An unrecognised type is passed through rather than dropped: a container may subscribe
            // to something this file has never heard of, and inventing a summary for it would be
            // worse than handing over what actually arrived.
            _ => message.Payload,
        };
    }

    /// <summary>
    /// The clause naming what moved, or nothing at all.
    ///
    /// AN EDIT CARRYING ONLY A NOTE CHANGES NO FIELD, so there is correctly nothing to name and the
    /// sentence has to END rather than trail off as `…kanban card 3_dev1: .`. That is the flow the
    /// panel advertises as how you hand work back, so it is the commonest wake of the three.
    ///
    /// A placeholder such as `(no detail given)` would be worse: it would assert that detail was
    /// MISSING from a row that is right to carry none, and the note immediately after it is the
    /// whole content of the wake.
    /// </summary>
    private static string Changed(string? change) =>
        string.IsNullOrWhiteSpace(change) ? "." : $": {change}.";

    /// <summary>
    /// What the person typed alongside a card change, or nothing at all.
    ///
    /// A NOTE IS OPTIONAL ON BOTH ROUTES, so the empty case must read as a sentence that simply
    /// ends rather than as `They wrote: `. Appended rather than substituted for `change`: the
    /// change is what the platform did and the note is why, and a Manager needs both.
    /// </summary>
    private static string Wrote(string? note) =>
        string.IsNullOrWhiteSpace(note) ? "" : $" They wrote: {note}";

    /// <summary>
    /// The name half of a qualified id. Neither half may contain a `/` - see
    /// <see cref="ContainerId.IsLegalName"/> - so the last one is the separator, and a source with
    /// none (`console`, `host`) is returned whole.
    /// </summary>
    private static string Bare(string who) => who[(who.LastIndexOf('/') + 1)..];

    private static JsonElement? Read(string payload)
    {
        try
        {
            return JsonDocument.Parse(payload).RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// One NUMBER out of a payload. <see cref="Field"/> beside it reads strings only and answers
    /// null for everything else, which is right for its callers and silently wrong for a numeric
    /// field - a backlog item id would simply never render. Guarded on <c>ValueKind</c> for the
    /// reason every wire read here is: this file does not get to assume the sender's shape.
    /// </summary>
    private static long? Number(JsonElement? payload, string name) =>
        payload is { ValueKind: JsonValueKind.Object } element
        && element.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.Number
        && value.TryGetInt64(out var number)
            ? number
            : null;

    private static bool IsFalse(JsonElement? payload, string name) =>
        payload is { ValueKind: JsonValueKind.Object } element
        && element.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.False;

    private static string FileChanged(JsonElement? payload)
    {
        var count = payload is { ValueKind: JsonValueKind.Object } element
            && element.TryGetProperty(PayloadFields.Count, out var value)
            && value.ValueKind is JsonValueKind.Number
                ? value.GetRawText()
                : "Some";
        var root = Field(payload, PayloadFields.Root) ?? "(unknown root)";
        var path = Field(payload, PayloadFields.Path) is { Length: > 0 } folder ? $"/{folder}" : "";

        return $"{count} file(s) changed in {root}{path}.";
    }

    private static string SiteAction(JsonElement? payload)
    {
        var sent = payload is { ValueKind: JsonValueKind.Object } element
            && element.TryGetProperty(PayloadFields.Payload, out var value)
                ? value.GetRawText()
                : "null";

        return $"{Field(payload, PayloadFields.By) ?? "A person"} posted the action "
            + $"`{Field(payload, PayloadFields.Action) ?? "(unknown action)"}` on the site "
            + $"`{Field(payload, PayloadFields.Site) ?? "(unknown site)"}` with the payload {sent}.";
    }

    private static string? Field(JsonElement? payload, string name) =>
        payload is { ValueKind: JsonValueKind.Object } element
        && element.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.String
            ? value.GetString()
            : null;
}
