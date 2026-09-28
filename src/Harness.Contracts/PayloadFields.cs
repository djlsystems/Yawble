namespace Harness.Contracts;

/// <summary>
/// The field names inside a message payload, as constants rather than literals at call sites.
///
/// THE SAME MOVE <see cref="MessageTypes"/> MAKES, one level in: the `completed`/`failed` payload
/// field is named `output`, and renaming it blanks the agent-facing renderer and the browser feed
/// at once, because both read it by spelling. Naming it here gives the C# side ONE store.
///
/// THE SPA SPELLS ITS OWN COPY, in `web/src/lib/summarise.ts`. That is not an oversight: no
/// test can compare them - there is no server in the web test run - and making a renderer fetch
/// `/api/events` to learn a field name buys a runtime dependency for a naming problem. Two stores,
/// and that remaining pair is a known gap.
/// </summary>
public static class PayloadFields
{
    /// <summary>What a run reported. Read by `MessageText` and by the SPA's `summarise.ts`.</summary>
    public const string Output = "output";

    public const string LaunchError = "launchError";
    public const string StoppedByPerson = "stoppedByPerson";
    public const string Reason = "reason";
    public const string Question = "question";
    public const string Delivered = "delivered";

    /// <summary>
    /// On a `completed` row: true when the run that just ended had already published a `handback`.
    /// A subscriber holding BOTH types was woken by the hand-back and is not woken again by this
    /// row - one wake per finished run, decided in `ContainerHost.PumpOnceAsync`. A subscriber
    /// holding only `completed` still wakes.
    /// </summary>
    public const string HandedBack = "handedBack";

    /// <summary>
    /// On a `completed` row: true when the run ended with a plugin result record marked
    /// <c>quiet</c> - nothing happened that anyone needs to be woken for. The row is written, shown
    /// and counted as any other; the pump wakes no subscriber on it (`ContainerHost.PumpOnceAsync`).
    /// Absent on every other row, and never on a `failed` row: a failure always wakes. What the run
    /// published, and a hand-back in it, wake on their own rows as usual.
    /// </summary>
    public const string Quiet = "quiet";

    /// <summary>
    /// On a trigger's instruction, and on the `completed` or `failed` row that closes it: the
    /// trigger's <see cref="WakeManagerPolicy"/> choice, written only when it is not `always`. The
    /// pump passes over the Manager on a `completed` row carrying `onHandbackOrFailure` or `never`,
    /// and on a `failed` row carrying `never`. Absent on every other row.
    /// </summary>
    public const string WakeManager = "wakeManager";

    /// <summary>
    /// On a `completed` or `failed` row that closes a delivery its run shared with others: the
    /// causation seq of the row in the same batch that carries the run's token figures. This row
    /// carries none, and the spend queries skip it, so a run is billed once however many deliveries
    /// it answered. Absent on a row that carries its run's usage, or unknown usage, itself.
    /// </summary>
    public const string UsageCountedOn = "usageCountedOn";

    /// <summary>
    /// On the Host's repository-setup instruction to a Manager: the clone paths that could not be
    /// made. It roots a workflow the Manager reports the block in, and when a person later makes one
    /// of these clones (Fetch or Bring current in the Git dialog) the Host finds that workflow by
    /// this field and tells the Manager inside it. See <c>RepoReadyNotice</c>.
    /// </summary>
    public const string RepoNotReady = "repoNotReady";

    /// <summary>The text of an addressed instruction. Never `subject`, which is a display
    /// convenience truncated for a card title - an agent handed that is told less than the person
    /// wrote, starts work on a fragment, and nothing fails.</summary>
    public const string Instruction = "instruction";

    /// <summary>
    /// The declared name of the envelope field every event type carries - see
    /// <see cref="EventCatalog"/>'s shared `Source` field. NOT a literal payload key: the value is
    /// resolved from `Message.Source`, never read out of the JSON, which is the whole point - it
    /// replaces `Container`, a field that copied `Message.Source` into the payload as well.
    /// </summary>
    public const string Source = "source";

    public const string Trigger = "trigger";

    public const string CardId = "cardId";

    /// <summary>
    /// The backlog item a planned card belongs to, on a `kanban.card.planned` row.
    ///
    /// THE WIRE NAME IS `item` AND THE CONSTANT IS NOT, deliberately. <see cref="Item"/> above
    /// already spells `item` for a different type - which batched prompt item `container.blocked`
    /// abandoned - and two constants cannot share a name. The types never overlap, so the wire is
    /// unambiguous; only C# needed telling apart. Do not "fix" this by renaming the wire field:
    /// that is a contract the SPA and the projector both read.
    /// </summary>
    public const string BacklogItem = "item";

    /// <summary>The card an instruction claims - see <see cref="BacklogItem"/>. Absent on every
    /// other instruction, which is what keeps a team that never touches the backlog unchanged.</summary>
    public const string Card = "card";
    public const string Change = "change";
    public const string Note = "note";
    public const string Text = "text";

    /// <summary>A status line, as reported by `container.progress`. Not the run-loop
    /// <c>ContainerState</c> - this is free text the agent chose to say about itself.</summary>
    public const string Status = "status";

    /// <summary>Written on a `container.progress` row ONLY when true - see
    /// <see cref="MessageTypes.Progress"/>'s own doc comment. A field that is always present and
    /// usually false is a question every reader asks once and nobody answers.</summary>
    public const string WhileIdle = "whileIdle";

    public const string ExitCode = "exitCode";
    public const string OutputLength = "outputLength";
    public const string Transcript = "transcript";

    /// <summary>
    /// On the `completed` or `failed` row that carries a run's usage: the absolute path of the
    /// session transcript the AGENT CLI wrote for that run, for the Host to read to a person. Absent
    /// when the preset has no live view or the file was not found. Not <see cref="Transcript"/>,
    /// which already names the platform's own copy of the run's output on every terminal row.
    /// Never rendered to an agent, like <see cref="UsageCountedOn"/>.
    /// </summary>
    public const string AgentTranscript = "agentTranscript";

    /// <summary>Beside <see cref="AgentTranscript"/>: the live view format it is read in, as the
    /// preset named it when the run started, so a member moved to another agent since still reads
    /// its earlier runs right.</summary>
    public const string AgentTranscriptFormat = "agentTranscriptFormat";

    /// <summary>NULL MEANS UNKNOWN on every one of the four token fields below, written as an
    /// absent key rather than a zero - see <see cref="InvocationUsage"/>. A run whose Agent
    /// reports no usage reports none; zero would be indistinguishable from a run that genuinely
    /// spent nothing.</summary>
    public const string TokensIn = "tokensIn";

    public const string TokensOut = "tokensOut";
    public const string TokensCachedIn = "tokensCachedIn";

    /// <summary>Tokens written to the prompt cache. Absent when the brand did not report them.
    /// Spend counts these at 5/4, not at full input weight.</summary>
    public const string TokensCacheCreation = "tokensCacheCreation";
    public const string TokensReasoning = "tokensReasoning";
    public const string TokensSource = "tokensSource";

    /// <summary>One combined figure from a brand that reports no in/out split - codex.</summary>
    public const string TokensTotal = "tokensTotal";

    /// <summary>
    /// WHAT KIND of failure a run was - one of <see cref="FailureClasses"/> - on an
    /// `agentContainer.failed` row. Absent on some rows (read as unclassified), and NULL on
    /// `agentContainer.completed`, which shares the same JSON object at the same call site.
    ///
    /// A FIELD AND NEVER A SECOND MESSAGE TYPE: see <see cref="FailureClasses"/> for the argument.
    /// </summary>
    public const string FailureClass = "failureClass";

    /// <summary>
    /// WHEN the provider said to come back, as an ISO-8601 instant - null whenever the provider did
    /// not say, which is most failures and every class but quota and rate.
    ///
    /// AN INSTANT RATHER THAN THE PROVIDER'S OWN WORDS. "your session limit resets 12am
    /// (America/New_York)" is a sentence, and a sentence is not a moment anything can wait until;
    /// the words are still in `output`, where they always were. Absent means NOBODY STATED ONE -
    /// the same rule the token fields follow - and for a quota failure that means no automatic
    /// resume, because the alternative is this platform inventing a horizon.
    /// </summary>
    public const string RetryAfter = "retryAfter";

    /// <summary>The queue depth `container.rejected` was refused at.</summary>
    public const string Ceiling = "ceiling";

    /// <summary>The message TYPE that was refused, on `container.rejected` - so the row names what
    /// could not be queued, not only who refused it.</summary>
    public const string Refused = "refused";

    public const string RefusedSeq = "refusedSeq";

    /// <summary>The per-workflow spend figure that was IN FORCE when a `workflow.paused` row was
    /// written - never the instance figure by assumption. With two possible sources for one bound,
    /// a row that recorded only "over budget" leaves a reader unable to tell which number to
    /// change.</summary>
    public const string Limit = "limit";

    /// <summary>What the workflow had spent SINCE ITS LAST NUDGE when it paused - the same figure
    /// the bound was evaluated against, never the whole-workflow total. See
    /// `IMessageLog.GetSpendSinceNudgeAsync`.</summary>
    public const string Spent = "spent";

    /// <summary>Which figure fired: <see cref="LimitSources.Team"/> or
    /// <see cref="LimitSources.Instance"/>. "You are over budget" and "the platform stopped you"
    /// are different sentences to the person reading them.</summary>
    public const string LimitSource = "limitSource";

    /// <summary>Which batched prompt item (1-based) `container.blocked` abandons, when the agent
    /// named one instead of the whole run. Optional.</summary>
    public const string Item = "item";

    /// <summary>A team id carried IN THE PAYLOAD rather than derived from `Message.Source` - see
    /// `MessageTeam.Of`. Needed wherever the publisher is a person, whose Source carries no team at
    /// all: `workflow.closed` and every `kanban.card.*` row.</summary>
    public const string Team = "team";

    /// <summary>The person who acted, on a `kanban.card.*` row - a bare user id.</summary>
    public const string Actor = "actor";

    /// <summary>Which member a row is about - the member a kanban card belongs to, or (on
    /// `agentContainer.scheduleSkipped`) the member whose scheduled wake was skipped. Two different
    /// <c>EventField</c> descriptions share this one constant, the same move `Reason` already makes
    /// across several types.</summary>
    public const string Member = "member";

    public const string LaneId = "laneId";
    public const string Title = "title";

    /// <summary>The rest of a planned card's instruction - what the title had no room for. Empty
    /// rather than absent when there is none, matching <c>KanbanCard.Body</c>'s own rule.</summary>
    public const string Body = "body";

    /// <summary>NULL where the field did not move, on `kanban.card.edited` - the row must never
    /// assert what a field "was" for a field nobody changed.</summary>
    public const string PreviousTitle = "previousTitle";

    public const string PreviousStatus = "previousStatus";

    /// <summary>Which repository a `repo.*` row is about, by the folder name derived from its URL -
    /// never the URL itself, which is where a credential lives.</summary>
    public const string Repo = "repo";

    /// <summary>The branches a `repo.*` row is about, comma-separated. On `repo.pushed` they are the
    /// ones that reached origin; on `repo.pushFailed` they are the ones that did NOT. Two different
    /// <c>EventField</c> descriptions share this one constant, the same move `Reason` already makes
    /// across several types.</summary>
    public const string Branches = "branches";

    /// <summary>The one branch a `repo.*` row is about - on `repo.forkSynced`, the default branch
    /// the fork was brought level with upstream on.</summary>
    public const string Branch = "branch";

    /// <summary>A pull request's URL on `repo.pullRequestOpened`.</summary>
    public const string Url = "url";

    /// <summary>A pull request's number on `repo.pullRequestOpened`.</summary>
    public const string Number = "number";

    /// <summary>The absolute path of the worktree a `repo.worktreeLeft` row is about.</summary>
    public const string Worktree = "worktree";

    /// <summary>`file.changed`: which area the path is in - `documents` or `root:&lt;name&gt;`.</summary>
    public const string Root = "root";

    /// <summary>`file.changed`: the folder, relative to <see cref="Root"/>.</summary>
    public const string Path = "path";

    /// <summary>`file.changed`: the changed files' paths relative to <see cref="Root"/>, a JSON
    /// array cut at <c>FolderWatchScope.MaximumChangedListed</c>.</summary>
    public const string Changed = "changed";

    /// <summary>`file.changed`: how many files changed, counted before the list was cut.</summary>
    public const string Count = "count";

    /// <summary>
    /// Whether the condition a row reports is TRANSPORT rather than a fault of the thing that ran -
    /// so repeating the operation is the correct response and changes nothing if it fails again.
    ///
    /// <para>
    /// ON THE ROW, NOT ONLY IN THE CATALOG. The catalog's summary and <c>MessageText</c>'s sentence
    /// both already say a failed push is safe to repeat, and NEITHER IS PERSISTED: the summary is
    /// documentation and the sentence is composed at delivery. Anything querying the log - the
    /// ledger, the board, a person reading `messages` - sees the payload and nothing else, and
    /// "an unreachable origin must never read as an agent fault" is a claim about what the ROW says.
    /// </para>
    /// </summary>
    public const string Retryable = "retryable";
}
