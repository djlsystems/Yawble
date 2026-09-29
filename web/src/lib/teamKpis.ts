import type {
  ContainerSnapshot,
  Message,
  TeamTokenTotals,
  TeamWorkflows,
  TeamWorkflowTiming,
} from '../api/types';

/**
 * IT STATES WHAT THE PLATFORM SAW, NOT WHAT IT CONCLUDED.
 *
 * A word like `STALLED` would conflate two different worlds: work that FINISHED and was never
 * declared, and work that STOPPED PART-WAY with something left. THE PLATFORM CANNOT TELL THEM APART,
 * because only the manager knows what "done" means for its workflow - the same blind spot the
 * "nothing to do" close exists for one level down. Such a badge would convict on evidence that
 * cannot support it, which this codebase forbids everywhere ("a probe that cannot see must not
 * convict").
 *
 * `UNDECLARED` says only what is known: the workflow is open, nothing is running, everything that
 * ran succeeded, and nobody has declared it finished. The detail line carries the age, and the
 * person judges.
 */
export type TeamStatus =
  | 'misconfigured'
  | 'failed'
  | 'blocked'
  | 'paused'
  | 'running'
  | 'waiting'
  | 'queued'
  | 'undeclared'
  | 'idle';

/**
 * The members whose claim on a WIP slot is waiting, by label, in roster order.
 *
 * A team whose members are all idle but which has a delivered wake held behind the limit is NOT
 * undeclared and NOT idle: work is about to happen and nothing is wrong. This is the fact that
 * says so, and every surface that names the held member reads it from here.
 */
export function heldMembers(containers: readonly Pick<ContainerSnapshot, 'name' | 'held' | 'state'>[]): string[] {
  return containers
    .filter((container) => container.held === true && container.state !== 'Running')
    .map((container) => container.name);
}

/** The words a waiting team reads: `waiting for a slot: Manager`. */
export function waitingForSlotText(held: readonly string[]): string {
  return held.length === 0 ? '' : `waiting for a slot: ${held.join(', ')}`;
}

/**
 * A member's last terminal event on the message log.
 *
 * The four terminal facts: `completed`
 * says the step is done, `failed` says the PLATFORM did not complete a run, `blocked` says the
 * AGENT gave up, and `needs-decision` says it stopped ON PURPOSE and is waiting on a person.
 */
export type TerminalEvent = 'completed' | 'failed' | 'blocked' | 'needs-decision';

/**
 * What the LOG says about one team, which four of `UNDECLARED`'s five clauses need and no snapshot
 * carries.
 *
 * A second argument rather than a derivation, and the signature change is the honest form of
 * "this question needs the log": a `ContainerSnapshot` describes a container's run loop right now,
 * and both *is this workflow finished* and *what did this member last say* are history.
 */
export interface MemberTerminal {
  event: TerminalEvent;

  /**
   * When that row was published, exactly as `Message.occurredAt` carries it — an ISO-8601 instant.
   *
   * Without the timestamp no grace period is possible client-side. Carrying it here is the cost of
   * making the badge agree with the sweep — and it costs no server round-trip, because `Message`
   * carries `occurredAt`.
   */
  occurredAt: string;
}

export interface TeamLog {
  /**
   * The team's newest workflow has no TERMINAL ROW - neither a manager's `workflow.completed`
   * nor a person's `workflow.closed`. Both producers read both types; a reader that knew only
   * the declaration would go on calling a closed workflow open, which is how one surface starts
   * disagreeing with the next about the same team.
   *
   * UNDECLARED requires an OPEN workflow rather than merely an idle roster, and that clause is what keeps
   * the mark off a team that is legitimately between jobs.
   */
  workflowOpen: boolean;

  /**
   * Each member's last terminal event AND WHEN IT HAPPENED, keyed by the member's `id` — which is
   * what `ContainerSnapshot.id` carries. A member absent from this map has published no terminal
   * fact in the window, which is not the same as having delivered.
   *
   * One record per member rather than two parallel maps: the event and its instant are one fact,
   * and a shape that let a caller supply either without the other would be a shape where the age
   * gate below could silently not apply.
   */
  lastTerminal: Record<string, MemberTerminal>;
}

/**
 * THE BADGE'S GRACE PERIOD. Two minutes, and this is the only place the number is written.
 *
 * Without a grace period a brand-new team shows **UNDECLARED** within seconds of finishing
 * its own repository setup, before it has been given any work at all. Every clause of the
 * condition holds — the verdict is correct and it is the wrong thing to tell a person. This is how
 * long the board waits before it is willing to say so.
 *
 * **IT IS DELIBERATELY NOT `QuietSweepWindow`, AND MUST NOT BE REPLACED BY IT.** The two constants
 * having the same shape is exactly the trap:
 *
 * - The sweep's 30 minutes is about how long a condition must persist before it is worth
 *   **writing down forever** — a finding is a row in a table, re-reported every interval.
 * - This is about how long before it is worth **showing**. Nothing here is persisted, so it costs
 *   nothing to be wrong briefly, and a person watching the board wants to know sooner than half an
 *   hour.
 *
 * Same shape, different reasons. A shared constant would hide that, and the next person to tune
 * one of them would silently tune the other.
 *
 * WHICH READER IS THE REFERENCE, because one condition with two implementations and two different
 * opinions about time will disagree. The direction differs per question:
 *
 * - The **CONDITION** — that a quiet team must be gated on the AGE of its newest terminal row at
 *   all — is the server sweep's (`QuietTeamSweep.ReapIdleAsync`). That is the reference,
 *   and the badge follows it. If the sweep's gate changes shape, this one follows.
 * - The **VALUE** is this badge's own, for the reasons directly above. That is the one thing the
 *   two readers are not required to agree on, and the cross-reader test in
 *   `__tests__/teamKpis.spec.ts` states its scenario at 10 seconds and 2 hours precisely so it
 *   sits clear of both numbers.
 *
 * (The other direction: `teamKpis.ts` is the reference for the `workflowOpen` derivation, and the
 * server carries a comment saying so.)
 */
export const StalledBadgeGrace = 2 * 60 * 1000;

export type TokenUsage =
  | {
      available: false;
      /** What the log does not carry, in words a person can act on. */
      missing: string;
    }
  | {
      available: true;
      /** Uncached input only. Cache reads are {@link cachedIn}. */
      input: number;
      cachedIn: number;
      cacheCreation: number;
      output: number;
      /** The server's weighted figure (cache read 1/10, cache write 5/4). Never recomputed here. */
      billable: number;
      partial: boolean;
      /** Honest about the claim: this is a log total, never a feed slice. */
      claim: 'team total';
      byBrand: {
        brand: string;
        input: number | null;
        cachedIn: number | null;
        cacheCreation: number | null;
        output: number | null;
        billable: number | null;

        /** WHY there is no number, or null when there is one. The brand half of the member note
         *  below: the flag is a property of the AGENT, so the two must not disagree. */
        note: string | null;
      }[];
      byMember: {
        member: string;
        brand: string;
        input: number | null;
        cachedIn: number | null;
        cacheCreation: number | null;
        output: number | null;
        billable: number | null;

        /** Completed and failed rows the server counted for this member, or undefined from a
         *  server that does not send it. 0 is a claim; undefined is not. */
        runs: number | undefined;

        /** One combined figure from a brand that reports no split, or null. */
        tokensTotal: number | null;

        /** WHY there is no number, or null when there is one (or when the reason is genuinely
         *  unknown). The component renders this in place of the figures - the library decides what
         *  the state MEANS, which is the only testable place for it. */
        note: string | null;
      }[];
    };

export interface TeamKpis {
  /** Members whose snapshot state is Running. */
  working: number;
  /** Roster size of the team being shown. */
  members: number;
  /** Sum of queueDepth — work accepted and not yet running. */
  queued: number;
  /** Members whose snapshot carries a blocked reason. Idle-and-gave-up, not failed. */
  stopped: number;
  /** Members whose queue is at its own ceiling, so new work is being refused. */
  atCeiling: number;
}

/**
 * One word for a whole team, for the tab chip.
 *
 * `log` may be null and null is an ordinary answer — the board has it only once messages have
 * been pulled for that team. Without it this never answers `FAILED` or `UNDECLARED`: both need
 * history, and a state guessed from a snapshot alone would be wrong in the
 * direction that matters.
 *
 * `now` IS A PARAMETER AND NEVER A `Date.now()` INSIDE, which is the same design the server sweep
 * uses (`ReapIdleAsync` takes `now`). It is what makes {@link StalledBadgeGrace} testable at an
 * exact instant, and it is what lets the cross-reader test state its scenario as 10 seconds and 2
 * hours rather than as a sleep. Milliseconds since the epoch, as `Date.now()` gives them.
 *
 * RANKING, and each step of it is an argument rather than an ordering:
 *
 * - `misconfigured` first — a member that cannot run at all outranks anything it might have done.
 * - `failed` next. A platform failure outranks an agent's own decision to stop, because one is
 *   recoverable by the team and the other is not. The member's own card orders these two the same
 *   way, and the two surfaces must agree or a chip and the card beneath it disagree about the same
 *   member.
 * - `blocked`, then `paused`. A pause is a deliberate team-wide hold and must stay visible even
 *   while the last run is winding down or work is already queued behind it.
 * - `running`, then `queued`, once the team is not paused.
 * - `undeclared` directly above `idle`. A failure is a louder fact than a silence, and a silence is
 *   still louder than a team that has finished. **IT IS NOT AN ERROR STATE** — the condition
 *   requires an open workflow precisely so that a team between jobs never reaches it.
 */
export function teamStatus(
  containers: ContainerSnapshot[],
  log: TeamLog | null,
  now: number,
  paused = false,
): TeamStatus {
  let misconfigured = false;
  let failed = false;
  let blocked = false;
  let running = false;
  let queued = false;
  let waiting = false;

  for (const container of containers) {
    if (
      container.missingAgent
      || container.unreachableRoot
    ) {
      misconfigured = true;
    }

    // A FAILURE IS STATUS ONLY WHILE NOTHING HAS MOVED PAST IT. A member running again, or with
    // work queued, has been picked back up - by its Manager after an interruption, or by the next
    // instruction - so its last failed run is history, not the team's state. Counting it would paint
    // a team FAILED while one of its members was visibly working, e.g. after a host restart.
    const movedOn = container.state === 'Running' || container.queueDepth > 0;
    if (!movedOn && log?.lastTerminal[container.id]?.event === 'failed') failed = true;

    if (container.blocked) blocked = true;

    if (container.state === 'Running') running = true;
    if (container.queueDepth > 0) queued = true;
    if (container.held === true && container.state !== 'Running') waiting = true;
  }

  if (misconfigured) return 'misconfigured';
  if (failed) return 'failed';
  if (blocked) return 'blocked';
  if (paused) return 'paused';
  if (running) return 'running';
  // BELOW RUNNING, ABOVE EVERYTHING QUIET. A held wake is work about to start, which is why it can
  // never read `undeclared` - a Manager waiting behind another team's runs is not stalled.
  if (waiting) return 'waiting';
  if (queued) return 'queued';
  if (isUndeclared(containers, log, now)) return 'undeclared';
  return 'idle';
}

/**
 * The UNDECLARED condition, and EVERY CLAUSE IS LOAD-BEARING.
 *
 * > the workflow is open — no `workflow.completed` and no `workflow.closed` — and nothing is
 * > Running and every queue is empty and every member's last terminal event is `completed`.
 *
 * Nobody is going to do anything, and nothing anywhere says so. Three of the clauses are read
 * from the roster above and are the reason this is reached only after `running` and `queued` have
 * already answered; the two below need the log.
 *
 * THE `completed` CHECK IS WHERE THE DECISION CLAUSE LIVES. A member whose last terminal event is
 * `needs-decision` is excluded by it, so `UNDECLARED` and *awaiting a decision* are mutually
 * exclusive BY CONSTRUCTION rather than by ranking — nothing has to choose between them. Without
 * that, a Manager that stopped on purpose to ask its owner a question renders as a team that
 * stopped for no reason, and those two boards want opposite things from the person reading them.
 *
 * An empty roster is never undeclared: there is nobody for the work to be waiting on, and a team
 * with no members is a different problem with a different fix.
 *
 * AND THE SIXTH CLAUSE: the team must have been silent for at least
 * {@link StalledBadgeGrace}. A completed errand and an abandoned job are structurally
 * identical in the log, and until a Manager declares what it finishes the only thing that
 * separates the alarming reading from the ordinary one is HOW LONG the silence has lasted. The
 * known limit is the trade: a real stall is delayed by exactly this long, which is small against
 * the sweep's thirty minutes and against a badge that is on permanently and therefore read by
 * nobody.
 */
function isUndeclared(containers: ContainerSnapshot[], log: TeamLog | null, now: number): boolean {
  if (!log || !log.workflowOpen || containers.length === 0) return false;

  let newest = Number.NEGATIVE_INFINITY;
  let ran = 0;

  for (const container of containers) {
    if (container.state === 'Running' || container.queueDepth !== 0 || container.held === true) return false;

    // ONLY THE MEMBERS THAT RAN, which is the question the SERVER projection asks.
    // Requiring a terminal from EVERY member on the roster would let one that had never been given
    // anything to do -- no terminal at all -- silence the mark for the whole team. A team of six
    // with two working is the normal shape, and the tile and the chip must agree on it: a routine
    // disagreement between two surfaces is what makes a REAL one invisible.
    const terminal = log.lastTerminal[container.id];
    if (!terminal) continue;

    if (terminal.event !== 'completed') return false;

    ran += 1;

    const occurredAt = Date.parse(terminal.occurredAt);

    // AN UNREADABLE INSTANT SILENCES THE BADGE RATHER THAN BYPASSING THE GATE. The gate cannot be
    // applied without a clock reading, and of the two ways to be wrong the over-eager one is the
    // one the grace period exists to prevent — so the missing fact answers "not yet", not "shout".
    if (Number.isNaN(occurredAt)) return false;

    if (occurredAt > newest) newest = occurredAt;
  }

  // AND A ROSTER THAT HAS RUN NOTHING IS NOT UNDECLARED. Skipping members without a terminal must not
  // collapse into "no evidence means undeclared": a team that has never done anything is idle, which is
  // the same reason an empty roster is excluded above.
  if (ran === 0) return false;

  // THE NEWEST ROW ON THE TEAM, not each member's own age. The question is how long the TEAM has
  // been silent, and a Manager that finished ten seconds ago has not been silent however long ago
  // its colleagues last spoke. This is the same evidence the server sweep gates on.
  return now - newest >= StalledBadgeGrace;
}

/** The terminal facts, by message type. `progress` is deliberately not one of them. */
const TerminalTypes: Record<string, TerminalEvent> = {
  'agentContainer.completed': 'completed',
  'agentContainer.failed': 'failed',
  'agentContainer.blocked': 'blocked',
  'agentContainer.needsDecision': 'needs-decision',
};

/**
 * What one team's message rows say, reduced to the two facts {@link teamStatus} asks the log for.
 *
 * Pure, and separate from `teamStatus` on purpose: the status is a decision about a stated
 * condition, and this is the reading of the log that produces it. Testing them apart is what lets
 * each of the condition's clauses be flipped one at a time.
 *
 * ORDERED BY `seq`, NEVER BY ARRAY ORDER. The board stores each card's activity newest-first, so
 * an implementation that trusted the order it was handed would read a member's OLDEST terminal
 * row as its latest fact — and would call a team that has since failed `undeclared`.
 */
export function teamLogFacts(messages: Message[]): TeamLog {
  let newestWorkflow: number | null = null;
  let completedWorkflow: number | null = null;
  const seen: Record<
    string,
    { seq: number; event: TerminalEvent; occurredAt: string; correlationId: number }
  > = {};

  for (const message of messages) {
    if (message.correlationId > 0) {
      if (newestWorkflow === null || message.correlationId > newestWorkflow) {
        newestWorkflow = message.correlationId;
      }

      // BOTH TERMINAL TYPES, exactly as `WorkflowOpenSql` and `TimingForAsync` read them on the
      // server. This reader produces the TAB CHIP's answer and the projection produces the table's,
      // and they are documented as one rule with two producers - so a `workflow.closed` recognised
      // on one side and not the other has the table saying `ended, Closed` while the chip beside it
      // calls the same workflow open.
      //
      // WHICH terminal type it was does not matter here, only that the workflow ended: this reader
      // answers `workflowOpen` and nothing finer. The WORD comes from the server's projection.
      if (message.type === 'workflow.completed' || message.type === 'workflow.closed') {
        if (completedWorkflow === null || message.correlationId > completedWorkflow) {
          completedWorkflow = message.correlationId;
        }
      }
    }

    const event = TerminalTypes[message.type];
    if (!event) continue;

    // A container's own events carry the qualified `Team/Name` as their source, and a snapshot
    // carries the bare half. Neither half may contain a `/`, so the last one is the separator.
    const member = message.source.slice(message.source.lastIndexOf('/') + 1);
    const previous = seen[member];
    if (!previous || message.seq > previous.seq) {
      seen[member] = {
        seq: message.seq,
        event,
        occurredAt: message.occurredAt,
        correlationId: message.correlationId,
      };
    }
  }

  // ORDERED BY `seq`, TIMED BY `occurredAt`, and they are not the same thing: `seq` is the log's
  // order and is what decides WHICH row is a member's latest, while `occurredAt` is the instant
  // {@link StalledBadgeGrace} is measured against. Picking the newest row by timestamp instead
  // would put the choice of latest fact at the mercy of clock skew on the publisher.
  //
  // SCOPED TO THE NEWEST WORKFLOW, which is the SHARED rule and not this reader's own preference.
  // The server's projection picks by most recent ACTIVITY, not by MAX(correlation_id),
  // so a fact taken from workflow 7 while workflow 8 is running (even if it finished) uses workflow 7
  // if it was more recently active. This reader must do the same: newestWorkflow is built by reading
  // messages in order, so the last message's correlationId is its most recent activity. The tab
  // chip and the table row must agree on which workflow they are describing, or one surface reads
  // FAILED over a table saying IDLE, both on screen at once.
  //
  // NOTHING IS DROPPED WHEN THERE IS NO WORKFLOW TO SCOPE TO. `newestWorkflow` is null only when
  // no row in the window carries a correlation at all, and a filter that silently emptied
  // `lastTerminal` there would be this file's own defect - reading an ABSENCE of evidence as
  // evidence.
  const lastTerminal: Record<string, MemberTerminal> = {};
  for (const [member, row] of Object.entries(seen)) {
    if (newestWorkflow !== null && row.correlationId !== newestWorkflow) continue;

    lastTerminal[member] = { event: row.event, occurredAt: row.occurredAt };
  }

  // THE NEWEST WORKFLOW, not "is there a completion anywhere". A team that finished job 7 and
  // started job 8 has a `workflow.completed` in the window and an open workflow, and reading the
  // presence of the row rather than which workflow it closed would call it finished for as long
  // as job 7 stayed in the feed.
  return {
    workflowOpen: newestWorkflow !== null && completedWorkflow !== newestWorkflow,
    lastTerminal,
  };
}

/**
 * The same facts `teamLogFacts` reads from messages, taken from the SERVER'S projection instead.
 *
 * THE TABLE CANNOT USE `teamLogFacts`. The store fills its activity buckets for OPEN TABS ONLY, so
 * for every team the person is not currently looking at that reader answers nothing - and a status
 * column that silently degraded for most rows would be worse than no column. The server's
 * projection reads the whole log rather than a bounded window, so this is the better evidence; it
 * is simply not the evidence the chip happens to hold.
 *
 * ONE RULE, TWO PRODUCERS. `teamStatus` and `isUndeclared` are untouched, which is what keeps the
 * ranking and every UNDECLARED clause in a single place. Two derivations of one word let a tile
 * read UNDECLARED while the chip reads IDLE indefinitely, and a routine disagreement is what makes
 * a real one invisible.
 *
 * Two clauses are deliberately conservative, and both err towards silence:
 *
 * - `runsUnfinished > 0` claims NO completions. A run with no terminal partner - a host restart
 *   mid-flight - means nobody can say WHICH member finished, so `isUndeclared` finds `ran === 0` and
 *   returns false rather than guessing.
 * - Every instant is the TEAM'S newest row, not the member's, because the projection carries no
 *   per-member instant. It is therefore never older than the true newest terminal, so the grace
 *   period can only fire LATER than it should, never sooner.
 *
 *   `lastActivityAt ?? startedAt ?? ''` rather than `lastActivityAt` alone. A payload can carry a start with no activity stamp - a workflow whose only
 *   row so far is the instruction that began it - and stamping those terminals with `''` there
 *   would hand every reader an instant `Date.parse` cannot read. The last fallback is still `''`,
 *   because a payload carrying neither has no instant to offer and inventing `now` would make a
 *   terminal look like it happened at the moment of the render.
 */
export function teamLogFromTiming(timing: TeamWorkflowTiming | null): TeamLog | null {
  if (timing === null) return null;

  const occurredAt = timing.lastActivityAt ?? timing.startedAt ?? '';
  const lastTerminal: Record<string, MemberTerminal> = {};

  // A FAILURE INSIDE A WORKFLOW ITS OWNER DECLARED COMPLETE, OR A PERSON CLOSED, IS HISTORY. The
  // declaration is the decision that the work is done - typically after the failed run's work was
  // redone elsewhere, as a re-check sent in a second workflow is - so the member reads as completed
  // like every other member that ran, rather than painting the team FAILED under a board whose own
  // workflow panel says COMPLETED.
  const settled = timing.state === 'Completed' || timing.state === 'Closed';

  // Marked members first, so a member that appears in `members` as well is not overwritten by the
  // inferred `completed` below.
  if (!settled) {
    for (const member of timing.failedMembers) lastTerminal[member] = { event: 'failed', occurredAt };
  }
  if (timing.blockedBy) lastTerminal[timing.blockedBy] = { event: 'blocked', occurredAt };
  if (timing.awaitingFrom) {
    lastTerminal[timing.awaitingFrom] = { event: 'needs-decision', occurredAt };
  }

  if (timing.runsUnfinished === 0) {
    for (const member of timing.members) {
      if (member.runs === 0) continue;
      if (lastTerminal[member.member]) continue;

      lastTerminal[member.member] = { event: 'completed', occurredAt };
    }
  }

  // `== null`, LOOSE, and it must match `lastWorkflowText`'s test of the same field exactly. An
  // absent field on the wire arrives as `undefined`, which is not `null` - so a strict test here
  // beside a loose one there makes one column of a row say the workflow is open while the column
  // next to it says a manager declared it finished, from a server that simply does not send the
  // field. Two readings of one value is how they disagree.
  return { workflowOpen: timing.available && timing.endedAt == null, lastTerminal };
}

/**
 * Glance figures for one team's snapshots.
 *
 * Three true numbers a person watching a running team actually uses. Derived only from
 * ContainerSnapshot, which is what rides every SignalR push — so the strip updates when a
 * member does, not on reload.
 *
 * Token totals are NOT derived here. A card holds a slice of messages; summing that slice
 * would count down as the team got busier. Tokens come from {@link tokenUsageFromTotals}.
 */
export function teamKpis(containers: ContainerSnapshot[]): TeamKpis {
  let working = 0;
  let queued = 0;
  let stopped = 0;
  let atCeiling = 0;

  for (const container of containers) {
    if (container.state === 'Running') working += 1;
    queued += container.queueDepth;
    // `blocked` ALONE, AND `handedBack` IS DELIBERATELY NOT COUNTED HERE. `stopped` is the
    // strip's count of members that gave up; a member that FINISHED and handed its card back is the
    // opposite of that, and counting it would reproduce - on the one tile a person glances at - the
    // exact lie the hand-back verb exists to remove. It is also not cleared at the next wake the way
    // `blocked` is, so a member that handed back once would be counted as stopped forever.
    if (container.blocked) stopped += 1;
    if (container.queueDepth >= container.ceiling) atCeiling += 1;
  }

  return {
    working,
    members: containers.length,
    queued,
    stopped,
    atCeiling,
  };
}

/** A sum that goes null once any part of it is unknown, so an unmeasured run is never a zero. */
const addKnown = (total: number | null, part: number | null) =>
  total === null || part === null ? null : total + part;

/**
 * Token usage across the team, from the log projection.
 *
 * Null means the fetch has not landed (or failed): unavailable, not zero.
 * `available: false` from the server means no completed/failed run carries captured usage.
 */
export function tokenUsageFromTotals(usage: TeamTokenTotals | null): TokenUsage {
  if (!usage) {
    return {
      available: false,
      missing:
        'Team token totals have not been loaded from the message log yet',
    };
  }

  if (!usage.available) {
    return {
      available: false,
      missing:
        usage.missing
        ?? 'Completed and failed payloads on the message log do not carry tokensIn/tokensOut',
    };
  }

  const byMember = usage.members.map((row) => ({
    member: row.member,
    brand: row.brand || 'unknown',
    input: row.tokensIn,
    // `?? null`: a server may omit these, and absent is unknown, not zero.
    cachedIn: row.tokensCachedIn ?? null,
    cacheCreation: row.tokensCacheCreation ?? null,
    output: row.tokensOut,
    billable: row.tokensBillable ?? null,
    runs: row.runs,
    tokensTotal: row.tokensTotal ?? null,

    // THREE REASONS A CELL CAN BE EMPTY AND ONLY ONE OF THEM IS A GAP. Rendered identically as
    // `(unknown) in - (unknown) out`, a member on an Agent that reports nothing would look exactly
    // like a number that had gone missing and might come back.
    //
    // A NOTE EXPLAINS AN ABSENT FIGURE AND MUST NEVER APPEAR BESIDE ONE, which is why the first
    // arm reads the figures rather than the counts. A server may send neither `runs` nor
    // `brandReportsUsage`, and a rule that inferred "no completed run yet" from their absence
    // would caption real spend with a denial of it.
    //
    // `=== 0` and `=== false` for the same reason: ABSENT IS NOT ZERO AND NOT FALSE. Undefined
    // means the server did not say, which is the ordinary `(unknown)`.
    note:
      row.tokensIn !== null || row.tokensOut !== null
        ? null
        : // A COMBINED TOTAL OUTRANKS EVERY OTHER REASON, because it is not a reason - it is the
          // figure. codex reports one number for a run and no split, so this is what that run cost
          // and the row says so rather than explaining an absence that is not there.
          typeof row.tokensTotal === 'number'
          ? `${row.tokensTotal.toLocaleString()} total`
          : row.runs === 0
            ? 'no completed run yet'
            : row.brandReportsUsage === false
              ? 'this Agent reports no usage'
              : null,
  }));

  type BrandRow = Extract<TokenUsage, { available: true }>['byBrand'][number];
  const brandMap = new Map<string, BrandRow>();

  // A MEMBER THAT HAS RUN NOTHING CONTRIBUTES NOTHING, and skipping it is not a tidy-up. A brand
  // total goes null the moment one of its members carries a null figure - that is how an unmeasured
  // run refuses to be summed as a zero - so once every member is listed rather than only those with
  // rows, hiring onto a measured brand would blank that brand's real spend. It is also simply the
  // right arithmetic: there is nothing to add.
  //
  // Narrow on purpose: only a row the server SAID has no runs and carries no figure is skipped.
  // A payload that says neither keeps every row.
  const contributes = (member: (typeof byMember)[number]) =>
    member.runs !== 0 || member.input !== null || member.output !== null;

  for (const row of byMember.filter(contributes)) {
    const current = brandMap.get(row.brand)
      // THE BRAND ANSWER IS THE MEMBER ANSWER, taken rather than re-derived. Every member on a
      // brand row runs that same Agent, so `brandReportsUsage` is the same fact for all of them -
      // and two renderings one line apart must not read `(unknown)` and `this Agent reports no
      // usage`. Two derivations of one fact is how they would drift.
      ?? {
        brand: row.brand,
        input: 0,
        cachedIn: 0,
        cacheCreation: 0,
        output: 0,
        billable: 0,
        note: row.note,
      };
    current.input = addKnown(current.input, row.input);
    current.cachedIn = addKnown(current.cachedIn, row.cachedIn);
    current.cacheCreation = addKnown(current.cacheCreation, row.cacheCreation);
    current.output = addKnown(current.output, row.output);
    current.billable = addKnown(current.billable, row.billable);
    brandMap.set(row.brand, current);
  }

  const byBrand = [...brandMap.values()].sort((a, b) => a.brand.localeCompare(b.brand));

  return {
    available: true,
    input: usage.tokensIn,
    cachedIn: usage.tokensCachedIn,
    cacheCreation: usage.tokensCacheCreation,
    output: usage.tokensOut,
    billable: usage.tokensBillable,
    partial: usage.partial,
    claim: 'team total',
    byBrand,
    byMember,
  };
}

/**
 * The word the Workflow tile shows beside its label. TEN STATES, and each is reachable today.
 *
 * THE VALUE IS THE DISPLAY TEXT — `TeamKpiStrip` renders it raw — which is why `NO RESULT` carries
 * its space.
 *
 * Null is the ninth and is not a word at all, and it means ONE thing rather than "whatever is
 * left": a workflow whose log clauses read UNDECLARED while the team has been silent for less than
 * {@link StalledBadgeGrace}. `available: false` — a team that has
 * published nothing since its members were created — is different again and renders an em dash.
 */
export type WorkflowState =
  | 'RUNNING'
  | 'COMPLETED'
  | 'CLOSED'
  | 'BLOCKED'
  | 'FAILED'
  | 'UNDECLARED'
  | 'AWAITING'
  | 'NO RESULT'

  /**
   * THE PLATFORM STOPPED THIS ONE. It spent its per-workflow budget, so nothing more runs under it
   * until a person resumes it - a recoverable stop, which is the whole reason a settable budget is
   * reasonable at all (a budget that killed work would make a wrong figure DESTROY work; a stop
   * that costs a click destroys nothing).
   *
   * IT IS NOT `UNDECLARED`. An over-budget workflow is not *nobody is working this* - the platform
   * is actively refusing to let anybody work it. The two are opposite facts and lead a reader to
   * opposite actions.
   */
  | 'PAUSED';

/** Per-member run time, for the DIALOG. Never on the tile beside elapsed. */
export interface WorkflowMemberExecution {
  member: string;
  runs: number;

  /** `(unknown)` when every one of this member's runs was cut short. Never `0s`. */
  execution: string;
  unfinished: number;
}

/**
 * EXECUTION, WHICH LIVES ONLY IN THE DIALOG.
 *
 * Elapsed is on the tile and this is not, because side by side one of them reads as a bug:
 * execution legitimately EXCEEDS elapsed whenever two members overlap: one member running 52
 * minutes alongside another's 5-to-8 minute slices sums to the larger number.
 *
 * {@link WorkflowExecution.total} ALWAYS CARRIES ITS UNIT OF MEASURE IN WORDS — "1h 51m across 4
 * members" — so the label says why it is larger. Nothing here sums it with elapsed, averages them,
 * or derives a percentage: a ratio of the two is a concurrency figure nobody asked for and would be
 * read as progress. There is no progress bar and no percentage in this feature at all, because
 * nothing knows how much work remains.
 */
export interface WorkflowExecution {
  seconds: number;

  /** "1h 51m across 4 members", or "no measured run time" — never a bare number. */
  total: string;
  partial: boolean;
  unfinished: number;
  members: WorkflowMemberExecution[];
}

export interface WorkflowTile {
  /**
   * False for a team that has NEVER RUN. {@link WorkflowTile.elapsed} is then an em dash and never
   * `0m`: zero is a measured duration and this is an absent one, which is the same rule the Tokens
   * tile follows when it answers `unavailable` rather than `0`.
   */
  available: boolean;

  state: WorkflowState | null;

  /** Wall clock, in milliseconds. Null when there is nothing to measure — never zero for absent. */
  elapsedMs: number | null;

  /** `1h 04m`, `38m 02s`, or `—`. */
  elapsed: string;

  /**
   * Whether the figure moves. True while the workflow is OPEN, which is most of the time —
   * `workflow.completed` is a manager's declaration and most workflows never get one.
   */
  ticking: boolean;

  /** The line under the figure: who gave up, who is waiting, how many runs failed. */
  detail: string | null;

  /**
   * How the tile is coloured. `quiet` for UNDECLARED is deliberate and must not become `error`: an
   * undeclared team's runs SUCCEEDED and the work stopped anyway, which is a silence rather than a
   * fault. `info` for AWAITING is the same argument in the other direction — it is the one state
   * that describes the platform working exactly as intended.
   */
  tone: 'live' | 'error' | 'warn' | 'info' | 'quiet' | 'none';

  /** `2 working · 0 queued · 0 stopped` — the click target, and the only one on this tile. */
  members: string;

  execution: WorkflowExecution;
}

/** `1h 04m`, `38m 02s`, `12s`. Two-digit padding on the smaller unit. */
function duration(ms: number): string {
  const total = Math.max(0, Math.floor(ms / 1000));
  const hours = Math.floor(total / 3600);
  const minutes = Math.floor((total % 3600) / 60);
  const seconds = total % 60;

  if (hours > 0) return `${hours}h ${String(minutes).padStart(2, '0')}m`;
  if (minutes > 0) return `${minutes}m ${String(seconds).padStart(2, '0')}s`;
  return `${seconds}s`;
}

/** How long ago, coarsely — "12m", "2h 05m". For "finished N ago" and nothing else. */
function ago(ms: number): string {
  const total = Math.max(0, Math.floor(ms / 1000));
  if (total < 60) return `${total}s`;
  if (total < 3600) return `${Math.floor(total / 60)}m`;
  return duration(ms);
}

/** An instant the payload may not carry, and may carry unreadably. Null rather than `NaN`. */
function instant(raw: string | null): number | null {
  if (!raw) return null;
  const parsed = Date.parse(raw);
  return Number.isNaN(parsed) ? null : parsed;
}

/**
 * THE SAME INSTANT, FOR A PERSON TO READ — `lib/teamsTable.ts`'s `clock` with the date kept, since
 * a workflow table spans days and a bare `09:00` on row forty says nothing about which day.
 *
 * `—` FOR ABSENT AND FOR UNREADABLE ALIKE, which is this codebase's rule everywhere a measurement
 * is missing: zero is a measured value and an em dash is not, and the same applies to an instant.
 * An open workflow has no end, and that is the ordinary case rather than a fault.
 */
function stamp(raw: string | null): string {
  const parsed = instant(raw);

  return parsed === null ? '—' : new Date(parsed).toLocaleString();
}

const Unmeasured: WorkflowExecution = {
  seconds: 0,
  total: 'no measured run time',
  partial: false,
  unfinished: 0,
  members: [],
};

/**
 * WHAT THE WORKFLOW TILE MEANS. The component renders it and decides nothing.
 *
 * A PURE FUNCTION IN `lib/`, AND THAT IS NOT A PREFERENCE. The component is not where this state
 * should mean itself - a spec that greps a
 * component's source proves only that somebody typed a string. The library holds what the state
 * MEANS; the component renders it.
 *
 * TWO SOURCES, AND THE SPLIT IS THE DESIGN:
 *
 * - `timing` is the LOG, projected on the server. It is the only thing that can see
 *   `agentContainer.failed`, and that is the whole argument for the projection existing: a team
 *   whose every member failed within seconds would otherwise read `WORKING 0 of 4`, `QUEUED 0`,
 *   `STOPPED 0`, `IDLE` - every number correct, and together describing a healthy team with
 *   nothing to do.
 * - `containers` are the live snapshots, and they answer the one question the log cannot: is
 *   anybody working RIGHT NOW. That is why RUNNING ranks first here and is decided here.
 *
 * THE RANKING, and each step is an argument rather than an ordering:
 *
 * 1. RUNNING while any member is Running or any queue has depth — a team that is working is not
 *    described by whatever its last terminal facts said.
 * 2. FAILED. A platform failure is a louder fact than anything below it.
 * 3. BLOCKED — an agent giving up, which reads differently from the platform failing a run.
 * 4. AWAITING — someone stopped ON PURPOSE and is waiting on a person.
 * 5. UNDECLARED — the runs succeeded and the work stopped anyway. Ranked below FAILED and above idle:
 *    a failure is louder than a silence, and a silence is louder than a team that has finished.
 * 6. COMPLETED, then the last known figure with no word at all.
 *
 * UNDECLARED AND AWAITING ARE MUTUALLY EXCLUSIVE BY CONSTRUCTION, not by this ranking: UNDECLARED
 * requires EVERY last terminal event to be `completed`, and a member waiting on a decision is not.
 * Nothing here has to choose between them, which is the same guarantee {@link teamStatus} has.
 *
 * @param now MILLISECONDS ON THE SERVER'S CLOCK, which the caller obtains by adding the offset it
 *   took between `timing.serverNow` and its own clock at fetch time. THE BROWSER'S CLOCK MAY BE
 *   WRONG: without that offset a machine ten minutes fast renders a workflow that started ten
 *   minutes in the future — a negative duration, which is the kind of visible nonsense that gets
 *   diagnosed as a server fault. It is a PARAMETER and never a `Date.now()` inside, for
 *   {@link teamStatus}'s reason: it is what makes {@link StalledBadgeGrace} testable at an exact
 *   instant.
 */
export function workflowTiming(
  timing: TeamWorkflowTiming | null,
  containers: ContainerSnapshot[],
  now: number,
): WorkflowTile {
  const kpis = teamKpis(containers);
  const members = `${kpis.working} working · ${kpis.queued} queued · ${kpis.stopped} stopped`;

  // NOT LOADED YET, OR NEVER RUN. Both render an em dash and neither renders a zero — the rule that
  // an unknown delta must not look like a zero delta is the same rule the Tokens tile follows.
  if (!timing || !timing.available) {
    return {
      available: false,
      state: null,
      elapsedMs: null,
      elapsed: '—',
      ticking: false,
      detail: null,
      tone: 'none',
      members,
      execution: Unmeasured,
    };
  }

  const startedAt = instant(timing.startedAt);
  const endedAt = instant(timing.endedAt);
  const lastActivityAt = instant(timing.lastActivityAt);

  // OPEN IS THE NORMAL CASE, NOT AN ERROR. `workflow.completed` is a manager's DECLARATION and most
  // workflows never get one. A design that treated "no end marker" as an anomaly would render the anomaly
  // almost always.
  const open = endedAt === null;

  const working = containers.some((container) => container.state === 'Running');
  const queued = containers.some((container) => container.queueDepth > 0);

  // TICKING MEANS SOMEBODY IS WORKING, AND NOTHING ELSE. Advancing for as long as the workflow is
  // OPEN, which is almost always, would show a team idle for fifteen hours a climbing clock - and a
  // climbing clock reads as work in progress.
  const live = working || queued;

  // WHERE THE FIGURE IS MEASURED TO, and the third arm matters most. A closed workflow freezes at
  // its own end, a live one follows the clock, and an open one with nobody working freezes at THE
  // NEWEST ROW rather than at `now`.
  //
  // The newest row rather than the instant of the last render, because the render instant is
  // arbitrary: the number would be whatever the tab happened to be showing when everyone stopped,
  // and would JUMP on the next reload. `lastActivityAt` is a fact the server and every reader agree
  // on, so the figure is stable across refreshes and means one thing — how long this workflow was
  // ACTIVE for.
  //
  // Falls back to `now` when there is no activity stamp at all: a payload that cannot answer the
  // question keeps a figure rather than collapsing it to zero.
  const measuredTo = !open ? endedAt : live ? now : (lastActivityAt ?? now);

  const elapsedMs = startedAt === null ? null : Math.max(0, measuredTo - startedAt);

  // THE FIGURE DOES NOT ANSWER "HOW LONG HAS THIS BEEN OPEN", so this says it instead. Only where the figure
  // is frozen and the workflow is still open: COMPLETED already says `finished 12m ago`, and a
  // running team's clock is the answer.
  const sinceActivity =
    open && !live && lastActivityAt !== null
      ? ` · last active ${ago(now - lastActivityAt)} ago`
      : '';

  // THE CLAUSES THE LOG COULD NOT ANSWER. The server said "open, and every member that ran ended
  // completed"; this side owns only the team silent for at least the grace period. Nobody working
  // and nothing queued are covered by RUNNING ranking above it.
  //
  // DELIBERATELY NOT GATED ON `containers.length > 0`, unlike `isUndeclared`'s "an empty roster is
  // never undeclared". `workflowsTile` passes a roster FILTERED to one correlation (see its comment
  // above the filter): a workflow the server calls `"Undeclared"` is, by definition, one nobody is
  // currently running under that correlation, so the filtered list is ALWAYS empty and such a guard
  // would make UNDECLARED unreachable for every row. Omitting it costs nothing for the unfiltered,
  // single-tile caller either: the server only emits `"Undeclared"` once
  // `terminals > 0` (`SqliteMessageStore.cs`) — i.e. once some container has already completed a
  // run under this correlation — so a non-null `lastActivityAt` already carries the "somebody was
  // here" fact the roster check existed to provide.
  const undeclared = lastActivityAt !== null && now - lastActivityAt >= StalledBadgeGrace;

  let state: WorkflowState | null = null;

  if (working || queued) state = 'RUNNING';

  // PAUSED, AND ITS TWO NEIGHBOURS IN THIS CHAIN ARE BOTH ARGUMENTS.
  //
  // BELOW THE LIVE CHECK, because a workflow can be paused while one member is still finishing the
  // very run that took it over budget - and at that instant somebody IS working it. Telling a
  // reader it is stopped while a process is burning tokens is a reassuring-but-wrong reading.
  //
  // ABOVE `Failed`, because a paused workflow whose last member run also failed will not run again
  // whatever that failure says. FAILED would send a reader to read a run when the reason nothing
  // moves is the budget. `Completed` and `Closed` still outrank it on the SERVER's ladder - a
  // workflow paused and then closed is closed - and they are not reachable here at the same time.
  //
  // READ, NEVER RE-DERIVED. `timing.state` is resolved from `pausedAt` on the server, in one
  // place, exactly as `Blocked` is from `blockedBy`. Asking `timing.pausedAt !== null` here would
  // be a second resolver, and the pair would eventually disagree.
  else if (timing.state === 'Paused') state = 'PAUSED';
  else if (timing.state === 'Failed') state = 'FAILED';
  else if (timing.state === 'Blocked') state = 'BLOCKED';
  else if (timing.state === 'Awaiting') state = 'AWAITING';
  else if (timing.state === 'Undeclared' && undeclared) state = 'UNDECLARED';
  else if (timing.state === 'Completed') state = 'COMPLETED';

  // A PERSON ENDED THIS ONE, AND IT IS NOT `COMPLETED`. The server keeps the two apart deliberately
  // — `workflow.closed` says "stop counting this", `workflow.completed` says "this was
  // delivered" — so mapping both to one word here would put back on the screen exactly the fiction
  // the server refuses to write on the log. Directly after COMPLETED, mirroring the server's own
  // ladder, where a declaration outranks a close when a workflow carries both.
  else if (timing.state === 'Closed') state = 'CLOSED';

  // THE SERVER'S `Running`, which nothing above maps, since RUNNING is decided above from live
  // containers instead. Without this arm the tile would render an elapsed time with nothing beside it.
  //
  // IT MEANS NO TERMINAL FACT AT ALL, NOT A MIXED ONE. The projection row that would clear
  // `everyTerminalCompleted` is the same row that fills `failedMembers`, `blockedBy` or
  // `awaitingFrom`, and all three outrank `Undeclared` on the server — so mixed facts resolve to the
  // louder word and never arrive here. Only an EMPTY set does: a `started` with no partner, or a
  // rejection at the queue ceiling. Work began and nothing came back.
  //
  // Last in the chain because it is the fall-through. The order is not load-bearing — `timing.state`
  // holds one value.
  else if (timing.state === 'Running') state = 'NO RESULT';

  let detail: string | null = null;
  let tone: WorkflowTile['tone'] = 'none';

  switch (state) {
    case 'RUNNING':
      tone = 'live';
      break;

    case 'FAILED':
      // BOTH NUMBERS. Four runs failed across two members is not the same as two failures: a count
      // of members alone understates it, while a count of runs alone does not say who to go and look at.
      detail =
        `${timing.runsFailed} ${timing.runsFailed === 1 ? 'run' : 'runs'} failed`
        + (timing.failedMembers.length > 0 ? ` · ${timing.failedMembers.join(', ')}` : '');
      tone = 'error';
      break;

    case 'PAUSED':
      // THE FIGURE THAT WAS IN FORCE, off the payload and formatted, never re-resolved. The server
      // decided which of the team's number and the instance's applied at the moment it wrote the
      // row; `pausedLimit` is that answer and the browser has no business asking again.
      //
      // FALLS BACK TO THE SERVER'S OWN SENTENCE when no limit came with the row - a Host that does
      // not send it, or a pause for some other reason. Never to an invented number and never to
      // silence: this state exists so a team never goes quiet with no reason on screen.
      detail =
        timing.pausedLimit !== null
          ? `paused - over its ${timing.pausedLimit.toLocaleString()} budget`
          : timing.pausedReason;

      // WARN, NOT ERROR. Nothing failed - the platform stopped the work on purpose and there is a
      // known way back - but somebody has to go and press it, which is what `warn` says. The same
      // tone BLOCKED and NO RESULT carry, for the same reason.
      tone = 'warn';
      break;

    case 'BLOCKED':
      detail = timing.blockedBy ? `${timing.blockedBy} gave up` : 'a member gave up';
      tone = 'warn';
      break;

    case 'AWAITING':
      // A NAME AND AN IMPERATIVE, not an observation. This is the only state on the table that
      // describes the platform working exactly as intended and still needing the board to say so.
      detail = timing.awaitingFrom
        ? `${timing.awaitingFrom} is waiting on you`
        : 'a member is waiting on you';
      tone = 'info';
      break;

    case 'UNDECLARED':
      detail = `no one is working${sinceActivity}`;

      // NOT COLOURED AS AN ERROR. The runs SUCCEEDED and the work stopped anyway.
      tone = 'quiet';
      break;

    case 'COMPLETED':
      detail = endedAt === null ? null : `finished ${ago(now - endedAt)} ago`;
      tone = 'none';
      break;

    case 'CLOSED':
      // `closed`, NEVER `finished`. COMPLETED's word is earned by a manager's declaration of what
      // was delivered; this row is a person saying stop counting it, and saying "finished" about
      // one is the same fiction on a smaller surface.
      detail = endedAt === null ? null : `closed ${ago(now - endedAt)} ago`;

      // COMPLETED'S TONE, DELIBERATELY, AND NOT A NEW COLOUR. A person closing a workflow is a
      // resolution rather than an alarm — nothing here needs looking at, which is what `none` says.
      tone = 'none';
      break;

    case 'NO RESULT':
      // ONE ARM, NOT A BRANCH ON `runsUnfinished > 0` — an `agentContainer.started` with no
      // partner. That count returns to zero on the next boot: the Host's startup sweep publishes
      // `agentContainer.failed` for work it cannot account for, so an unpaired `started` exists only WHILE a run is in flight,
      // and then a live container outranks the log and the tile says RUNNING. A branch that cannot
      // fire is a claim about the system that is not true.
      detail = 'nothing has reported back' + sinceActivity;

      // NOT `quiet`, WHICH IS UNDECLARED'S: that tone is earned by UNDECLARED's runs having SUCCEEDED,
      // and nothing here succeeded. Not `error` either — nothing reported a failure. Somebody has
      // to go and look, which is what `warn` says.
      tone = 'warn';
      break;

    // THE ONE REMAINING NULL, AND IT IS DELIBERATE — do not fill it too. The log's clauses read
    // UNDECLARED but the team has been silent for less than `StalledBadgeGrace`, so a brand-new team
    // that has just finished its own setup is not called undeclared within two minutes of doing it.
    // The tab chip waits the same grace out. `maps every state the projection can answer to a word`
    // and `waits out the grace period before it will say UNDECLARED` pin the two halves together.
    default:
      break;
  }

  const memberExecution: WorkflowMemberExecution[] = timing.members.map((row) => ({
    member: row.member,
    runs: row.runs,

    // `(unknown)` AND NEVER `0s`. A member whose only run was cut short by a host restart spent
    // real time, and nothing knows how much — the same idiom the Tokens dialog uses for a run with
    // no usage recorded.
    execution: row.executionSeconds === null ? '(unknown)' : duration(row.executionSeconds * 1000),
    unfinished: row.unfinished,
  }));

  const counted = timing.members.filter((row) => row.executionSeconds !== null).length;

  return {
    available: true,
    state,
    elapsedMs,
    elapsed: elapsedMs === null ? '—' : duration(elapsedMs),
    ticking: open && live,
    detail,
    tone,
    members,
    execution: {
      seconds: timing.executionSeconds,

      // THE UNIT OF MEASURE, IN WORDS. It is what says why this figure may be LARGER than the one
      // on the tile: members run concurrently.
      total:
        timing.runsCounted > 0
          ? `${duration(timing.executionSeconds * 1000)} across ${counted} `
            + `${counted === 1 ? 'member' : 'members'}`
          : 'no measured run time',
      partial: timing.partial,
      unfinished: timing.runsUnfinished,
      members: memberExecution,
    },
  };
}

/** One row of the workflows dialog — one open workflow, or the last closed one. */
export interface WorkflowRow {
  correlation: number;

  /**
   * `TeamWorkflowTiming.subject` — the instruction that began this workflow's own subject, read
   * off the server rather than looked up separately here. `Workflow #<correlation>` is the
   * LAST-RESORT FALLBACK, only when the server has nothing: a root that is not an addressed
   * instruction, or one that carries no subject. `correlation` is the one thing every reader can
   * quote back to a manager regardless, exactly as `KanbanCard.workflowSeq` already is for a card,
   * so the fallback is built from it rather than left blank.
   */
  subject: string;

  state: WorkflowState | null;

  /**
   * `now - startedAt` while this workflow is open, `endedAt - startedAt` once it is closed — NEVER
   * `workflowTiming`'s own frozen-at-idle figure. That freeze exists because a SINGLE open workflow
   * with nobody live reads as abandoned; with several open at once `containers` is the WHOLE
   * ROSTER and cannot say which one a working member is actually on, so freezing every row on one
   * team-wide liveness bit would be wrong as often as right. Each row keeps its own clock.
   */
  elapsed: string;
  elapsedMs: number | null;

  /** True while THIS workflow is open (`endedAt === null`) — independent of team-wide liveness, for
   *  {@link elapsedMs}'s reason. */
  ticking: boolean;

  /**
   * WHEN THIS WORKFLOW BEGAN AND WHEN IT ENDED, AS INSTANTS — the two facts {@link elapsed} is the
   * subtraction of, so a reader can check the arithmetic rather than take the duration on trust.
   *
   * THE SERVER IS NEVER ASKED FOR A NUMBER HERE. `elapsed` stays a client-side subtraction that
   * ticks every second with no further traffic; these are the endpoints of it, formatted once.
   *
   * `—` FOR ABSENT, NEVER `now` AND NEVER AN INVENTED STAMP. An open workflow has no end, which is
   * the ordinary case rather than an anomaly - `workflow.completed` is a manager's declaration and
   * most workflows never get one.
   */
  started: string;
  ended: string;

  detail: string | null;
  tone: WorkflowTile['tone'];

  /**
   * WHO THIS WORKFLOW IS WITH — the members `TeamWorkflowTiming.members` names for THIS workflow,
   * never the roster-wide split `WorkflowTile.members` carries. Two workflows on one team involve
   * different members, and a figure that read the same on every row would say nothing.
   */
  withWhom: string;

  /**
   * THIS WORKFLOW IS PAUSED - `TeamWorkflowTiming.pausedAt !== null`, read off the payload.
   *
   * SEPARATE FROM {@link state} EVEN THOUGH THE TWO AGREE TODAY. `state` is a single word chosen
   * by a ladder, and a paused workflow with a member still finishing its last run reads RUNNING at
   * that instant - correctly, because somebody IS working it. It is still paused, and the row must
   * still offer the way back. Deriving the button from the word would take Resume away from
   * exactly the row that is about to need it.
   */
  paused: boolean;
}

/**
 * WHAT THE TILE SAYS ABOUT EVERY WORKFLOW A TEAM HAS RUN, open and closed alike, and the dialog
 * behind it. The SPAN and the COUNT still describe the open ones only — see their own fields.
 *
 * `available`/`openCount`/`totalCount`/`count` mirror `TeamWorkflows` verbatim — see that record's
 * own documentation. `rows` is one entry per `TeamWorkflows.workflows`, each built by calling
 * {@link workflowTiming} — never a second implementation of the eight-word vocabulary, the tone
 * table or the detail line.
 */
export interface WorkflowsTile {
  available: boolean;

  /** OPEN workflows, uncapped — what the tile's count line says, and what the span is measured
   *  over. Never the length of {@link rows}, which carries closed workflows too. */
  openCount: number;

  /**
   * EVERY workflow this team has run since its floor, open and closed alike, uncapped.
   *
   * THE FIGURE THE TRUNCATION LINE COUNTS AGAINST - `rows.length < totalCount`. It cannot be
   * `rows.length < openCount`: a team with nothing open reads `openCount: 0` while `rows` still
   * carries workflows, so that comparison fires backwards. With closed rows in the list there is no
   * reading of `openCount` that can answer the question at all.
   */
  totalCount: number;

  /** `3 open` / `1 open` / `0 open`. Never a count when unavailable — `—` instead, matching
   *  {@link available}. THE OPEN COUNT, NOT {@link totalCount}: a count that silently grew to mean
   *  "ever" would read as a team with three hundred live workflows. */
  count: string;

  /** THE SPAN: earliest open start until now. Never a sum. */
  span: string;
  spanMs: number | null;

  /** Whether the span still grows. True exactly while something is open — see the comment on
   *  `spanMs` below for why this needs no `live` check the way {@link WorkflowTile.ticking} does. */
  ticking: boolean;

  /** The loudest row's tone — see {@link teamChip} for the identical ranking, used so a chip and
   *  the tile beneath it never disagree about which workflow is worst. */
  tone: WorkflowTile['tone'];

  rows: WorkflowRow[];
}

/**
 * THE LOUDNESS RANKING, OVER EVERY WORD THE VOCABULARY HAS. `FAILED` > `BLOCKED` > `RUNNING` >
 * `UNDECLARED` is a direct borrow of
 * {@link teamStatus}'s own order (`misconfigured` > `failed` > `blocked` > `running` > `queued` >
 * `undeclared` > `idle`) — `queued` folds into `RUNNING` already (`workflowTiming` sets it for
 * `working || queued`), and `idle` has no analogue for an OPEN workflow, so `COMPLETED` takes its
 * place at the foot.
 *
 * `AWAITING` AND `NO RESULT` HAVE NO ANALOGUE IN `teamStatus` AT ALL — that ranking has neither
 * word, and `workflowTiming`'s own numbered list that mentions them is a FACT-RESOLUTION chain
 * (which raw signal wins when deciding one workflow's state), not a cross-entity loudness order;
 * reusing its position for AWAITING would in fact rank it ABOVE `FAILED`, which is wrong for a
 * chip. Their placement here is THIS FUNCTION'S OWN DECISION, pinned by
 * `multiWorkflow.spec.ts`'s `RowLoudness` ranking tests rather than left implicit:
 *
 * - `NO RESULT` sits beside `BLOCKED`: both are "somebody has to go and look" facts and share
 *   `BLOCKED`'s `warn` tone, but `BLOCKED` at least NAMES a member and `NO RESULT` does not, so it
 *   is ranked one step quieter.
 * - `AWAITING` outranks `RUNNING` because a workflow waiting on a person's decision does not move
 *   until that person acts, which is louder than a workflow merely going.
 *
 * A `Record` rather than an array, for `StatusOrder`'s reason in `teamsTable.ts`: an array's
 * `indexOf` answers `-1` for a state it has never heard of, which would rank a new `WorkflowState`
 * FIRST — above `MISCONFIGURED` — silently. As a `Record` keyed on the union, a new state is a
 * compile error here instead.
 *
 * EXPORTED so the Teams table's Workflows column can sort by this same ranking rather than keeping
 * a second copy: two rankings of one loudness is exactly how a chip and the column beneath it would
 * eventually disagree about which team is worst.
 */
export const RowLoudness: Record<WorkflowState, number> = {
  FAILED: 0,
  BLOCKED: 1,
  'NO RESULT': 2,

  // ABOVE `AWAITING`, AND THIS RANKING IS NOT THE SERVER'S. The two answer different questions -
  // the server's ladder resolves which fact describes ONE workflow, and this ranks which ROW is
  // worst across several. PAUSED outranks AWAITING because the PLATFORM stopped the work, where
  // AWAITING is an agent asking a person a question: the first is a thing that was done to the
  // team, the second a thing the team is doing.
  //
  // Below `NO RESULT` because that row has nothing to tell a reader at all, where a paused one
  // carries a reason and a button.
  PAUSED: 3,

  AWAITING: 4,
  RUNNING: 5,
  UNDECLARED: 6,
  COMPLETED: 7,

  // QUIETEST OF ALL, AND BESIDE `COMPLETED` RATHER THAN BESIDE `UNDECLARED`. A person closing a
  // workflow is a RESOLUTION - they looked at it and ended it - so it is terminal news, not an
  // alarm; ranking it with `UNDECLARED` would push a row somebody has already dealt with above one
  // nobody has looked at, which is backwards.
  //
  // One step BELOW `COMPLETED` rather than tied with it, mirroring the server's own ladder: a
  // declared delivery is the stronger statement of the two. A tie would resolve on row order
  // instead, which is not a decision anybody made.
  CLOSED: 8,
};

/** The loudest row, or null for an empty list. `null` state ranks quietest — an anomaly, not a
 *  louder fact than any word the vocabulary actually has. */
function loudestRow(rows: WorkflowRow[]): WorkflowRow | null {
  let loudest: WorkflowRow | null = null;
  let loudestRank = Number.POSITIVE_INFINITY;

  for (const row of rows) {
    const rank = row.state === null ? Number.POSITIVE_INFINITY : RowLoudness[row.state];
    if (rank < loudestRank) {
      loudest = row;
      loudestRank = rank;
    }
  }

  return loudest;
}

/**
 * THE SUBJECT A READER SEES FOR ONE WORKFLOW — the server's subject, read, never re-derived; a
 * `Workflow #<correlation>` fallback only for a root that is not an addressed instruction or
 * carries no subject. `workflowsTile` uses it for each row's own `subject`; `TeamKpiStrip.vue`'s
 * "where the run time went" dialog uses it too, to say WHICH workflow its per-member totals
 * describe — that section reads `props.timing`, the team's single newest correlation, which need
 * not be any of the rows in the LIST above it, so a bare "this workflow" caption would name none
 * of them. One function so the two callers cannot
 * describe the same workflow two different ways.
 */
export function workflowSubject(entry: Pick<TeamWorkflowTiming, 'subject' | 'correlation'>): string {
  return entry.subject && entry.subject.length > 0
    ? entry.subject
    : `Workflow #${entry.correlation ?? 0}`;
}

/**
 * WHAT THE CLOSE DIALOG'S REASON BOX STARTS WITH.
 *
 * `POST .../workflows/{correlation}/close` accepts a blank reason on purpose - a completion with no
 * result says nothing, but requiring one costs more than it earns - and the route's own comment
 * names the mitigation that makes that acceptable: "the CLIENT prefills a suggestion from what the
 * platform knows". This is that suggestion, built rather than hand-typed on every close, so a
 * one-click submission still produces a row that accounts for itself.
 *
 * BUILT FROM THE ROW'S OWN `detail`, NEVER A SECOND READING OF THE LOG. `detail` already carries
 * both halves - a verdict ("no one is working") and a qualifying clause (" · last active 22h ago")
 * - and this keeps only the second, because the first is written in the third person for a status
 * line and reads oddly as a reason someone gives ("no one is working" is not a thing a PERSON says
 * when closing a workflow). `closed as <state>` replaces it; the qualifying clause survives verbatim
 * so the reason still carries the fact that made the row worth showing in the first place.
 *
 * A row with no state and no qualifying clause - the null state a still-inside-its-grace-period
 * UNDECLARED row carries, or a row with no `·` in its detail at all - falls back to `closed as
 * workflow`, which is honest rather than invented: there is no word yet to name what state it was in.
 */
/**
 * WHICH OF THE DIALOG'S FOUR ACTIONS A ROW MAY OFFER — THE ONLY COPY OF THAT TABLE.
 *
 * IT LIVES HERE RATHER THAN AS FOUR `v-if`s, for this file's usual reason: a rule expressed in a
 * template is a rule no test should have to reconstruct from markup - and none of these four is
 * cosmetic. `TeamKpiStrip.vue` calls this and decides nothing; the decision must not migrate into
 * a `v-if` condition, or a rule ends up half in a function and half in markup.
 *
 * NUDGE IS THE ONE WITH TEETH. `tile.rows` lists CLOSED workflows alongside open ones, and Nudge
 * publishes an instruction whose causation is that correlation, which `WorkflowOpenSql`'s `woke`
 * subquery then treats as RE-OPENING it. So the button would wake a Manager on already-delivered
 * work and make the row open again by being pressed.
 *
 * `findings` IS NOT PART OF THIS SIGNATURE. Nudge is not offered on a terminal row even when
 * the team holds a live finding: a finding does not make finished work unfinished, it makes it
 * worth RE-ENGAGING, and re-engaging finished work is the Concierge's job on its own surface.
 * Leaving the parameter out is deliberate: an exception that cannot be reached by passing an
 * argument cannot be introduced by someone passing one.
 *
 * CLOSE IS NOT ON THE RUNNING ROW, and this one is a server fact rather than a taste.
 * `POST .../workflows/{correlation}/close` has NO BUSY CHECK, so a person could close a workflow
 * whose members were still spending and the route would accept it. Stop first, then Close. Close
 * stays on every other open row - UNDECLARED above all, which is the row it exists for - and is
 * withheld from a terminal one because a second `workflow.closed` on the same correlation is
 * accepted, additive and meaningless.
 *
 * SHOW THREAD STAYS ON EVERY ROW. Locating a finished workflow in the feed is exactly what it is
 * for, and it writes nothing.
 *
 * STOP KEEPS ITS OWN RULE - only while the row itself is RUNNING - which is a stricter test than
 * "not terminal" and is why it is not folded into `terminal` below.
 *
 * HIDDEN RATHER THAN DISABLED, as `Stop` is: a control that is
 * present and refuses invites the second click that a control which is absent never gets.
 */
export interface WorkflowRowActions {
  close: boolean;
  nudge: boolean;
  thread: boolean;
  stop: boolean;

  /**
   * RESUME, ON A PAUSED ROW AND NOWHERE ELSE.
   *
   * IT DOES NOT REPLACE NUDGE. Nudge is the generic "wake this" on every idle row and is
   * untouched; Resume is the one control a reader who has just read PAUSED can find without
   * already knowing that `nudge` is the verb that clears a spend window.
   */
  resume: boolean;
}

export function workflowRowActions(
  // `paused` is OPTIONAL and absent is NOT paused. A payload may carry no pause fact at all,
  // and a Resume button over a workflow this browser cannot know the state of is a control that
  // can only mislead.
  row: Pick<WorkflowRow, 'state'> & Partial<Pick<WorkflowRow, 'paused'>>,
): WorkflowRowActions {
  // A NULL STATE IS NOT TERMINAL. It is an UNDECLARED row still inside its grace period, or a row
  // the vocabulary has no word for yet - unknown, never finished. Reading it as terminal would take
  // Close and Nudge away from a row that is genuinely open, which is the opposite defect.
  const terminal = row.state === 'COMPLETED' || row.state === 'CLOSED';

  // STRICTER THAN "NOT TERMINAL", AND SEPARATE FROM IT. `live` is this row's own RUNNING, which is
  // what Close is withheld against; `terminal` is what Nudge is withheld against. The two happen
  // to exclude each other and are still not the same question.
  const live = row.state === 'RUNNING';

  return {
    close: !terminal && !live,
    nudge: !terminal && !live,
    thread: true,
    stop: live,

    // THE PAUSE ITSELF, NOT THE WORD ON THE ROW. See `WorkflowRow.paused`: a paused workflow whose
    // last member is still finishing reads RUNNING, and that row must still offer the way back.
    resume: row.paused === true,
  };
}

export function closeReasonPrefill(row: Pick<WorkflowRow, 'state' | 'detail'>): string {
  const word = row.state ? row.state.toLowerCase() : 'workflow';

  if (!row.detail) return `closed as ${word}`;

  const separator = row.detail.indexOf(' · ');
  const qualifier = separator === -1 ? '' : row.detail.slice(separator);

  return `closed as ${word}${qualifier}`;
}

/**
 * WHAT THE TILE AND THE DIALOG MEAN, for every workflow a team holds open at once. The component
 * renders it and decides nothing — the same split {@link workflowTiming} follows and for the same
 * reason: the logic has to live here for any of it to be testable directly at all.
 *
 * @param now MILLISECONDS ON THE SERVER'S CLOCK — see {@link workflowTiming}'s parameter of the
 *   same name for why this is a parameter and never a `Date.now()` inside.
 */
export function workflowsTile(
  workflows: TeamWorkflows | null,
  containers: ContainerSnapshot[],
  now: number,
): WorkflowsTile {
  if (!workflows || !workflows.available) {
    return {
      available: false,
      openCount: 0,
      totalCount: 0,
      count: '—',
      span: '—',
      spanMs: null,
      ticking: false,
      tone: 'none',
      rows: [],
    };
  }

  const rows: WorkflowRow[] = workflows.workflows.map((entry) => {
    // EVERY FIGURE ON THE ROW COMES FROM HERE, ELAPSED INCLUDED. `workflowTiming` freezes an open
    // workflow's clock at `lastActivityAt` whenever nobody is live, and the roster passed in is
    // this workflow's own (see the filter below), so "nobody live" means THIS workflow is idle,
    // exactly as it does for the single-tile caller.
    //
    // Measuring an open row to `now` would be wrong: a workflow is OPEN until a manager DECLARES it
    // over — which a failed one never does — so a row whose last run failed at 21:33 would read
    // `11h 32m` the following morning, counting the night. One rule, one place, both readers.
    //
    // THE ROSTER PASSED IN IS FILTERED TO THIS WORKFLOW, and that is the reason per-workflow status
    // exists at all: "A team can legitimately be Running one workflow and
    // Blocked on another." The unfiltered roster cannot express that — one member Running anywhere
    // forces `working || queued` true for EVERY row (RUNNING is checked before Failed/Blocked/
    // Awaiting/Undeclared inside `workflowTiming`), masking a genuinely Blocked or Failed workflow
    // underneath it and silently defeating loudest-wins on both the tile's tone and the chip's
    // label.
    //
    // `ContainerSnapshot.currentCorrelation` is what makes the filter possible — `ContainerCard.vue`
    // already reads it per-member — and it settles QUEUED for free rather than needing a second
    // rule: `MemberRuntime.ConsumeAsync` sets `_currentCorrelation` in the SAME statement as
    // `State = Running` and clears it in the same statement as `State = Idle`, so every container
    // that survives this filter already has `state === 'Running'` by construction. The `queued`
    // half of `workflowTiming`'s `live` check has nothing left to add on a filtered list.
    //
    // THAT IS DELIBERATE AND MUST NOT BE "FIXED" THE OTHER WAY BY FILTERING queueDepth TOO —
    // `queueDepth` is one number per member with no correlation of its own. A queued-but-idle
    // member has `currentCorrelation === null` and is excluded by this filter already; work
    // queued BEHIND a member's current run may belong to a different workflow entirely and cannot
    // be attributed to this one either. Queued stays the TEAM-LEVEL fact it already is, on
    // `WorkflowTile.members`, computed from the UNFILTERED roster at the tile level — never
    // invented per row from data that cannot support the claim.
    const containersOnThisWorkflow =
      entry.correlation === null
        ? []
        : containers.filter((container) => container.currentCorrelation === entry.correlation);
    const tile = workflowTiming(entry, containersOnThisWorkflow, now);

    const memberNames = entry.members.map((member) => member.member);

    return {
      // 0 ONLY FOR A ROW THE LOG COULD NOT IDENTIFY — real correlations are message seqs and are
      // never 0, so this reads as "unknown" rather than colliding with a real workflow.
      correlation: entry.correlation ?? 0,

      subject: workflowSubject(entry),
      state: tile.state,
      elapsed: tile.elapsed,
      elapsedMs: tile.elapsedMs,

      // `open && live` on the tile, never `open` alone. A failed row is still open and is not
      // running, and a figure that animates says work is happening under it.
      ticking: tile.ticking,

      // THE TWO ENDPOINTS `elapsed` IS THE SUBTRACTION OF, read off the payload and formatted here
      // so the component decides nothing. `—` for a stamp the payload does not carry, which for
      // `endedAt` is the ordinary case rather than a fault.
      started: stamp(entry.startedAt),
      ended: stamp(entry.endedAt),

      detail: tile.detail,
      tone: tile.tone,
      withWhom: memberNames.length > 0 ? memberNames.join(', ') : '—',

      // THE PAUSE FACT ITSELF, not the word the ladder chose - see `WorkflowRow.paused`. `?? null`
      // because a Host may send no such field, and absent is NOT paused.
      paused: (entry.pausedAt ?? null) !== null,
    };
  });

  // THE SPAN, AND NEVER THE SUM. Three workflows covering the same ten minutes summed reads as
  // thirty - a number larger than the time that has actually passed. This codebase already carries
  // the rule for exactly this trap: per-member execution time "legitimately EXCEEDS wall-clock
  // elapsed, because members run concurrently. Never added to elapsed and never divided by it."
  //
  // Nothing here adds two durations together. There is one subtraction, from the earliest open
  // start, and that is the whole of it.
  const earliest = instant(workflows.earliestStartedAt);

  // WHERE THE SPAN IS MEASURED TO, by the same rule `workflowTiming` follows for one workflow.
  //
  // A workflow is OPEN until a manager DECLARES it over, and a FAILED one never does - so measuring
  // an open workflow to `now` counts the night, and a row whose last run failed at 21:33 would read
  // `11h 32m` the next morning.
  //
  // IT IS THE HALF A PERSON ACTUALLY SEES. `TeamKpiStrip` renders `tile.span` whenever
  // `openCount > 0` and falls back to the frozen `workflow.elapsed` only when nothing is open at
  // all.
  //
  // LIVE IS READ OFF THE ROWS RATHER THAN RECOMPUTED. Each row's `ticking` is `open && live` as
  // `workflowTiming` decided it, against a roster filtered to that row's own correlation; asking
  // the question a second time here, over the unfiltered list, would be a second store of a fact.
  const anyLive = rows.some((row) => row.ticking);

  // The LATEST activity across the open workflows, for the reason the single-workflow arm freezes
  // at `lastActivityAt` rather than at the render instant: the render instant is arbitrary and the
  // figure would jump on the next reload, where a stamp every reader agrees on is stable and means
  // one thing - how long this team was ACTIVE for.
  const latestActivity = workflows.workflows.reduce<number | null>((latest, entry) => {
    const activity = instant(entry.lastActivityAt);

    return activity === null ? latest : latest === null ? activity : Math.max(latest, activity);
  }, null);

  // Falls back to `now` when nothing carries an activity stamp: a payload that cannot answer the
  // question keeps a figure rather than collapsing it to zero.
  const measuredTo = anyLive ? now : (latestActivity ?? now);

  const spanMs = earliest === null ? null : Math.max(0, measuredTo - earliest);

  return {
    available: true,
    openCount: workflows.openCount,

    // `?? rows.length` FOR A PAYLOAD WITHOUT THE FIELD, and never `openCount`. Falling back to the
    // rendered length says "nothing is truncated", which is the only claim that cannot be wrong -
    // where falling back to `openCount` would make the comparison fire backwards.
    totalCount: workflows.totalCount ?? rows.length,

    count: `${workflows.openCount} open`,
    span: spanMs === null ? '—' : duration(spanMs),
    spanMs,

    // TICKING IFF THE SPAN CAN STILL GROW, which takes BOTH halves: `earliestStartedAt` named
    // something — `TeamWorkflows.EarliestStartedAt` is null precisely when nothing is open — AND
    // somebody is actually working. The `live` check is essential: a figure that animates says
    // work is happening under it, and every open
    // workflow on a team whose last run failed is idle.
    ticking: spanMs !== null && anyLive,

    tone: loudestRow(rows)?.tone ?? 'none',
    rows,
  };
}

/**
 * WHERE THE RUN TIME WENT, ACROSS EVERY WORKFLOW IN THE TABLE — the plural sibling of the
 * `execution` field {@link workflowTiming} builds for ONE workflow.
 *
 * IT DESCRIBES THE WORKFLOWS THE TABLE LISTS, NOT WHICHEVER ONE THE DIALOG HAPPENS TO PICK. The
 * team's single NEWEST correlation need not be one of the rows, so a breakdown built from it alone
 * could describe a workflow ABSENT from the list entirely.
 *
 * ALL THREE HONESTIES SURVIVE, and none of them is weakened by the aggregation:
 *
 * - The per-member figure is run time SUMMED FROM THE MESSAGE LOG, which is where it already came
 *   from. One member appearing in four workflows is ONE line here, not four.
 * - It legitimately EXCEEDS the tile's elapsed, because members run at the same time - and MORE so
 *   across several workflows than across one, not less. `total` carries its unit of measure in
 *   words for that reason, exactly as the single-workflow figure does, and nothing here adds it to
 *   elapsed, divides by it, or derives a percentage.
 * - A RUN THAT STARTED AND NEVER REPORTED AN END IS UNKNOWN, NEVER ZERO, AND IS NOT IN THE TOTAL.
 *   That rule is carried verbatim: a member whose every contribution is `null` renders `(unknown)`,
 *   and `runsUnfinished` is summed and surfaced separately so the dialog can say how many runs the
 *   total cannot account for.
 *
 * NULL AND EMPTY BOTH ANSWER {@link Unmeasured}, matching the single-workflow arm: a team that has
 * published nothing has no breakdown, which is different from a breakdown of zero.
 */
export function workflowsExecution(workflows: TeamWorkflows | null): WorkflowExecution {
  if (!workflows || !workflows.available || workflows.workflows.length === 0) return Unmeasured;

  // KEYED ON THE MEMBER NAME, AND INSERTION-ORDERED. `Map` preserves first-seen order, and the
  // payload is newest workflow first - so the member who ran most recently leads, which is the same
  // order the single-workflow breakdown has.
  const byMember = new Map<string, { runs: number; seconds: number | null; unfinished: number }>();

  let seconds = 0;
  let unfinished = 0;
  let partial = false;
  let runsCounted = 0;

  for (const entry of workflows.workflows) {
    seconds += entry.executionSeconds;
    unfinished += entry.runsUnfinished;
    runsCounted += entry.runsCounted;
    partial = partial || entry.partial;

    for (const row of entry.members) {
      const held = byMember.get(row.member) ?? { runs: 0, seconds: null, unfinished: 0 };

      held.runs += row.runs;
      held.unfinished += row.unfinished;

      // NULL IS UNKNOWN AND STAYS OUT OF THE SUM, but it must not erase a measured contribution
      // from another workflow either: a member with ten measured minutes here and one unmeasured
      // run there has spent AT LEAST ten minutes, and `(unknown)` would be a worse answer than the
      // floor. Only a member whose every contribution is null stays null.
      if (row.executionSeconds !== null) held.seconds = (held.seconds ?? 0) + row.executionSeconds;

      byMember.set(row.member, held);
    }
  }

  const members: WorkflowMemberExecution[] = [...byMember].map(([member, held]) => ({
    member,
    runs: held.runs,

    // `(unknown)` AND NEVER `0s`, the same idiom the single-workflow arm uses: a member whose only
    // runs were cut short by a host restart spent real time, and nothing knows how much.
    execution: held.seconds === null ? '(unknown)' : duration(held.seconds * 1000),
    unfinished: held.unfinished,
  }));

  const counted = members.filter((row) => row.execution !== '(unknown)').length;

  return {
    seconds,

    // THE UNIT OF MEASURE, IN WORDS. It is what says why this figure may be LARGER than the span on
    // the tile: members run concurrently, and across several workflows they do so across several
    // clocks as well.
    total:
      runsCounted > 0
        ? `${duration(seconds * 1000)} across ${counted} ${counted === 1 ? 'member' : 'members'}`
        : 'no measured run time',
    partial,
    unfinished,
    members,
  };
}

export interface TeamChip {
  /**
   * NULL MEANS NO WORD, NEVER `'COMPLETED'`. A row's own state is null when the server said
   * `"Undeclared"` but this side is still inside {@link StalledBadgeGrace}, or when nothing ranked at
   * all — neither is "finished", and inventing a word for either is worse than showing none. See
   * this file's `teamChip`.
   *
   * THE UNION, NOT `string`, AND THAT IS WHAT MAKES {@link RowLoudness}'s PROMISE REAL. That table
   * is a `Record` keyed on {@link WorkflowState} so a new state is a compile error rather than a
   * silent `-1`; typed as `string`, its one external reader - `chipRank` in `teamsTable.ts` - would
   * have to cast to look a label up, and a cast defeats exactly the guarantee the `Record` exists to give.
   * A label outside the union would have yielded `undefined`, a `NaN` comparator, and arbitrary row
   * order, silently.
   *
   * `'MISCONFIGURED'` IS SPELT OUT RATHER THAN FOLDED INTO `WorkflowState`. It is a MEMBER-level
   * fact - a missing Agent, an unreachable root - and no workflow can carry it, so
   * it is a member of this chip's vocabulary and not of the workflow one. `teamChip` below is the
   * only producer of this record, so the type is the whole truth about what can appear here.
   */
  label: WorkflowState | 'MISCONFIGURED' | typeof WaitingForSlot | null;
  count: number;
  tone: WorkflowTile['tone'];

  /**
   * The members held behind the WIP limit, by label - named beside `WAITING FOR A SLOT` in place
   * of the count. Empty for every other label.
   */
  held: string[];
}

/**
 * THE CHIP'S WORD FOR A TEAM WHOSE WAKE IS HELD BEHIND THE WIP LIMIT. Like `MISCONFIGURED`, a
 * member-level fact rather than a workflow state, so it is spelt out here and not in
 * `WorkflowState`.
 */
export const WaitingForSlot = 'WAITING FOR A SLOT' as const;

/** The workflow words a held wake replaces: every one that says nobody is doing anything. */
const QuietLabels: readonly (WorkflowState | null)[] = [null, 'UNDECLARED', 'COMPLETED', 'CLOSED'];

/**
 * WHAT A TEAM'S TAB SAYS WHEN ITS WORKFLOWS DISAGREE: the loudest, with the count beside it.
 *
 * `BLOCKED · 3`. The loudness ranking already exists so that the worst news surfaces, which is
 * what a chip is for; the count preserves "there are several" without inventing a second concept,
 * and the per-workflow detail is one click away in the dialog.
 *
 * `misconfigured` IS THE EXCEPTION AND IT IS NOT A WORKFLOW FACT AT ALL - it is a property of a
 * MEMBER: a missing Agent or an unreachable root. So this is the UNION of
 * member-level facts and per-workflow facts, and the COUNT counts only the latter. A reader trying
 * to derive it from a workflow will not find it, which is why the asymmetry is stated here.
 */
export function teamChip(tile: WorkflowsTile, containers: ContainerSnapshot[]): TeamChip {
  // THE SAME FIELDS `teamStatus` READS, asked directly rather than through that function: this
  // question needs only the roster, and `teamStatus` also needs a `TeamLog` this caller does not
  // have. Duplicating the check is smaller than widening that function's signature for a
  // caller that cannot supply half of what it asks for.
  const misconfigured = containers.some(
    (container) =>
      container.missingAgent
      || container.unreachableRoot,
  );

  if (misconfigured) {
    return { label: 'MISCONFIGURED', count: tile.openCount, tone: 'error', held: [] };
  }

  const loudest = loudestRow(tile.rows);

  // A HELD WAKE OUTRANKS SILENCE AND NOTHING LOUDER. A team whose loudest open workflow reads
  // UNDECLARED (or nothing) while its Manager waits for a slot is about to work, so it must not
  // read as stopped; a FAILED or BLOCKED workflow beside the held one is still the worse news.
  const held = heldMembers(containers);
  const anyRunning = containers.some((container) => container.state === 'Running');

  if (held.length > 0 && !anyRunning && QuietLabels.includes(loudest?.state ?? null)) {
    return { label: WaitingForSlot, count: tile.openCount, tone: 'info', held };
  }

  // NEVER `?? 'COMPLETED'` HERE. A `null` state — every row's own `state` came back null, which
  // happens for an `Undeclared` row still inside its grace period, or for a tile with no rows at all —
  // means unknown or not-yet-decided, not finished. With `?? 'COMPLETED'` a team with three
  // workflows nobody had touched in hours would read `COMPLETED · 3 open`: `loudestRow` ranks a
  // null state at `+Infinity`, and when every row is null that is what it returns. The caller renders the count with no
  // word for a null label, the same way the single-workflow tile already does for
  // `workflow.state === null` (`v-if="workflow.state"` in `TeamKpiStrip.vue`).
  return {
    label: loudest?.state ?? null,
    count: tile.openCount,
    tone: loudest?.tone ?? 'none',
    held: [],
  };
}

/**
 * THE CSS-SAFE SPELLING of a {@link TeamChip.label}, for the one place its word is interpolated
 * into a class name (the Teams table's Workflows column, the tab chip). `WorkflowState` carries one
 * two-word member, `'NO RESULT'` — a space inside a `class` attribute value is not one token but
 * two, so a literal label there would split into `console-tab-status--no` plus a stray `result`,
 * silently: `vue-tsc` cannot see a string built at runtime. Lower-cased and hyphenated so the existing
 * `console-tab-status--*` palette in `app.scss` — already lower-case, one word each — is extended
 * rather than duplicated. `'none'` for a null label: an absent word is not the quietest fact on the
 * palette, it is no answer at all, and it earns its own entry the same way `idle` does for the
 * roster chip.
 */
export function chipStateClass(label: string | null): string {
  return label === null ? 'none' : label.toLowerCase().replace(/ /g, '-');
}

/**
 * THE ONE MARK A MEMBER'S CARD SHOWS, when its last run left more than one.
 *
 * `Failed` › `Blocked` › `NeedsDecision`, and each step is an argument rather than an ordering: a
 * platform failure outranks an agent's decision to stop, which outranks an agent's decision to ask
 * — because a run that died after asking did not get its question answered either, and the failure
 * is the thing that has to be cleared first.
 *
 * IT LIVES HERE, BESIDE {@link teamStatus}, ON PURPOSE. That function ranks the same three facts
 * for the TAB CHIP, and **the two surfaces must order them identically** or a chip and the card
 * beneath it disagree about the same member — which is worse than either mark being absent. Two
 * rankings in two files is exactly how they drift.
 *
 * It is also the only way the ordering is testable directly at all: a precedence expressed as `v-if` / `v-else-if` in a template is still a
 * precedence the component can render without explaining. The component renders what this decides.
 *
 * `blocked` and `needsDecision` CANNOT BOTH BE SET BY ONE RUN — a run publishes one terminal fact —
 * so that pair is an ordering for the record rather than a case that arises. `failed` beside either
 * is the real case: an agent gives up and publishes `blocked`, then its process dies and the
 * platform publishes `failed`. Both are cleared at the same next wake, so the card must choose.
 */
export type ContainerMark =
  | {
      kind: 'failed';
      reason: string;

      /**
       * WHAT KIND of failure, when the server said — see `ContainerSnapshot.failureClass`. Carried
       * on the MARK rather than read from the snapshot a second time in the template, so the class
       * and the reason it belongs to can never be rendered from two different reads.
       *
       * `null` for a failure that carries no class.
       */
      failureClass: string | null;

      /** When the platform will resume this workflow by itself, or `null` when it will not. */
      resumeAt: string | null;
    }
  | { kind: 'blocked'; reason: string }
  | { kind: 'needs-decision'; reason: string }
  | null;

export function containerMark(container: ContainerSnapshot): ContainerMark {
  if (container.failed) {
    return {
      kind: 'failed',
      reason: container.failed,
      failureClass: container.failureClass ?? null,
      resumeAt: container.resumeAt ?? null,
    };
  }

  // `handedBack` IS NOT A MARK AND IS NOT RANKED HERE. Everything this function returns is
  // a call to action - something a person has to clear - and a member that handed its finished work
  // back needs nothing done about it. The card already says so through its STATUS (`handback`,
  // which the board colours and labels as a success); a fourth `ContainerMark` kind would put a
  // delivery in the row reserved for trouble and, since `handedBack` never clears, would leave it
  // there permanently.
  if (container.blocked) return { kind: 'blocked', reason: container.blocked };
  if (container.needsDecision) {
    return { kind: 'needs-decision', reason: container.needsDecision };
  }

  return null;
}
