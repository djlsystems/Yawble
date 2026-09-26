import type { Message } from '../api/types'
import { failureClassWords, resumesAutomatically, resumeTimeText } from './failureClass'

/**
 * One line describing what a message was about, for a card's activity feed.
 *
 * No escaping here, deliberately. Piped into innerHTML, agent output could write
 * into the page; Vue escapes interpolation, so that hazard is closed by the stack rather than by a rule someone has to remember at every call site.
 */
export function summarise(message: Message): string {
  // Whitespace COLLAPSED, which is right for a two-line clamp and wrong for anything else: a row
  // is one glance, so a paragraph break inside it would spend half the row on nothing. `detail`
  // below is the same text without this, for the hover card that has room for the real shape.
  return textOf(message).replace(/\s+/g, ' ').trim()
}

/**
 * The same text `summarise` renders, with the agent's own line breaks INTACT.
 *
 * Reading one of these is the whole point of the hover card, and agent output is not a sentence:
 * a manager's report has paragraphs, a refusal has a blank line before the thing it wants you to
 * notice, an instruction has a list. Collapsed to single spaces it is a wall, and the reader is
 * back to opening the database - which is the trip the card exists to remove.
 *
 * Still a STRING that gets interpolated, never markup. Agent output reaches this; `**bold**` and backticks stay as typed rather than being
 * rendered, because a renderer here is a way for a member's output to write into the page.
 */
export function detail(message: Message): string {
  // An instruction is TWO things - a subject short enough to head a card and a body holding
  // the rest - and this is the one renderer with room for both. The row above shows the subject
  // alone, which is what a row is for; showing only the subject HERE would put the instruction's
  // own words out of reach of the card that exists to hold them.
  //
  // ITS OWN ARM RATHER THAN A SECOND CHAIN. `textOf` still decides which field carries the words
  // for every other message type, so the two renderers cannot come to disagree about that; this
  // adds the one case where the answer is a pair rather than a field.
  const payload = payloadOf(message)

  if (payload !== null) {
    // A subject is one line and its own whitespace means nothing, so it is trimmed.
    const subject = stringOr(payload.subject).trim()

    // THE BODY IS NOT. `InstructionText.TrimLeadingBlankLines` walks whole blank lines rather than
    // characters precisely so the first body line KEEPS its indentation - an instruction's list or
    // command example is shaped on purpose. Trimming the field here undid that, and did it
    // selectively: the first line lost its indent while every line under it kept one, which reads
    // as a broken list rather than as a renderer flattening text. Trim the JOIN if anything, never
    // the value.
    const body = stringOr(payload.body)

    // BOTH must be there. A subject whose instruction fit inside it has an empty body, and joining
    // them would leave a heading over a blank paragraph. Asked of the TRIMMED body, because a
    // body of nothing but spaces is a body with nothing in it.
    if (subject !== '' && body.trim() !== '') {
      // `trimEnd` and not `trim`: the leading end is the subject, already trimmed, and a `trim`
      // here would be a second place someone could reintroduce the flattening above.
      return `${subject}\n\n${body}`.replace(/\r\n?/g, '\n').trimEnd()
    }
  }

  // Carriage returns normalised so a Windows-authored payload does not render with a blank line
  // between every line in a `pre-wrap` block.
  return textOf(message).replace(/\r\n?/g, '\n').trim()
}

/**
 * A payload as an object, or null when it is neither.
 *
 * The one parse both renderers use. A payload may be anything a container chose to publish - a
 * bare string, a number, or not JSON at all - and none of that is a crash: an unreadable row beats
 * a blank board.
 */
function payloadOf(message: Message): Record<string, unknown> | null {
  try {
    const parsed: unknown = JSON.parse(message.payload)

    return typeof parsed === 'object' && parsed !== null
      ? (parsed as Record<string, unknown>)
      : null
  } catch {
    return null
  }
}

/**
 * A field's text when it is a string, and '' for every other shape.
 *
 * IT DOES NOT TRIM, and that absence is load-bearing - see `detail`. A helper that quietly
 * normalised its value would flatten a body's indentation at every call site that ever used it,
 * which is how this went wrong the first time. A caller that wants a trimmed value asks for one.
 */
function stringOr(value: unknown): string {
  return typeof value === 'string' ? value : ''
}

/**
 * Which field of a payload carries the human-readable part, raw.
 *
 * Shared by both renderers so the hover card cannot show a DIFFERENT field from the row it was
 * opened over - two copies of this chain is two answers waiting to disagree, and the disagreement
 * would look like the card lying about the row.
 */
/**
 * The words a failed row carries, in the order the shared chain reads them.
 *
 * NOT A FOURTH PRIVATE CHAIN — it is the SAME order `textOf` uses two fields on, narrowed to the
 * two a failure can carry. `launchError` FIRST because a failed run carries `output: ''` and the
 * reason lives in `launchError`; getting that one backwards renders every runner-level failure
 * as a blank row.
 */
function failureText(payload: Record<string, unknown>): string {
  const text = [payload.launchError, payload.output].find(
    (value) => value !== null && value !== undefined && String(value).trim() !== '',
  )

  return text === undefined ? '' : String(text)
}

function textOf(message: Message): string {
  const payload = payloadOf(message)

  // A payload that is not JSON is not a crash. A container may publish a type this UI has never
  // heard of, and an unreadable line is better than a blank board. CHECKED BEFORE the type arms
  // below, or a `started` row with an unreadable payload would still render "woke" - a claim
  // made about a message nothing could read.
  if (payload === null) {
    return ''
  }

  // `started` carries {container, trigger} and none of the fields below, so
  // without this arm it would render as a blank dash - a row that told you
  // something happened while refusing to say what. The trigger is the
  // interesting part: what woke it.
  if (message.type === 'agentContainer.started') {
    const trigger = payload.trigger
    return trigger ? `woke on ${label(String(trigger))}` : 'woke'
  }

  // A PERSON CLOSED THIS WORKFLOW, WHICH IS NOT A DECLARATION OF DELIVERY.
  //
  // DELIBERATELY UNREACHED TODAY, and kept rather than deleted. `ownerOf` files a person's
  // team-level row under the team's own bucket, and every feed in this SPA is per CARD -
  // `ActivityFeed` is mounted only inside `ContainerCard` - so nothing renders this arm. It becomes
  // reachable the moment a team-level feed exists, and a renderer written then would have to
  // re-derive both arguments below. The sibling rule applies: `MessageText.cs` renders the same row
  // for agents and does reach it, so the two must go on agreeing whatever this file can display.
  //
  // `payload.reason` is ALREADY in the shared chain below, which needs-decision and rejected rows
  // also read - so a closure WITH a reason would already render, but as the bare reason text with
  // nothing marking it as a closure, indistinguishable from those other rows. And a closure with NO
  // reason (accepted on purpose - see the close route's own comment) matches nothing in that chain
  // at all and renders as an em dash, the exact "a row that told you something happened while
  // refusing to say what" shape `agentContainer.started` above exists to prevent. Its own arm, ahead of
  // the shared chain, fixes both.
  if (message.type === 'workflow.closed') {
    const reason = payload.reason
    return typeof reason === 'string' && reason.trim() !== '' ? `closed: ${reason}` : 'closed'
  }

  // A REPORT THAT BELONGED TO NO RUN, marked at the progress route because the container had no
  // current causation when it arrived - so a process outlived the invocation that started it, and
  // whatever it eventually produces will be reported by nobody.
  //
  // ITS OWN ARM, AND IT GOES FIRST, exactly as StatusCommand.ActivityText does it: a feed row is
  // clamped to two lines, and a mark at the end of a truncated line is a mark that is not there.
  // Without it this renders as an ordinary status line and a reader scanning the board concludes
  // the job is simply still going - which is the one wrong conclusion available here.
  //
  // `=== true` rather than a truthiness test, because THE FLAG IS WRITTEN ONLY WHEN TRUE: the
  // route serialises two different payload shapes rather than one shape carrying a boolean, so
  // anything else in this field is a payload this platform does not emit. A mark that appears on
  // rows that did not earn it is worse than no mark at all.
  if (payload.whileIdle === true && typeof payload.status === 'string') {
    return `[after its run ended] ${payload.status}`
  }

  // WHAT KIND OF FAILURE, AND WHETHER ANYTHING IS GOING TO HAPPEN ABOUT IT.
  //
  // ITS OWN ARM AND IT GOES FIRST, exactly as `whileIdle` above does and for the same reason: a
  // feed row is clamped to two lines, so a qualifier at the end of a failure's own text is a
  // qualifier nobody reads. "FAILED" and a provider's sentence sends a person to investigate a run
  // that was working perfectly and hit a spend limit — which is the wrong conclusion.
  //
  // ROWS WITHOUT A CLASS ARE UNTOUCHED, and that is checked twice over: the arm only fires for
  // `agentContainer.failed`, and only when `failureClass` is a spelling this bundle knows. A row
  // with no such field falls straight through to the shared chain below. The log is append-only; a renderer that changed
  // its mind about a historical row would be rewriting it.
  if (message.type === 'agentContainer.failed') {
    const failureClass = typeof payload.failureClass === 'string' ? payload.failureClass : null
    const words = failureClassWords(failureClass)

    if (words !== null) {
      // THE RESUME IS ONLY SAID FOR A CLASS THAT ACTUALLY GETS ONE. `retryAfter` is what the
      // PROVIDER said; a transport row carrying one would otherwise read as a promise this platform
      // has no intention of keeping, transport having been offered automatic resume and refused.
      const resume =
        resumesAutomatically(failureClass) && typeof payload.retryAfter === 'string'
          ? resumeTimeText(payload.retryAfter)
          : null

      const rest = failureText(payload)
      const resumeSentence = resume === null ? '' : ` Resuming at ${resume}.`

      return `[${failureClass}] ${words}.${resumeSentence}${rest === '' ? '' : ` ${rest}`}`
    }
  }

  // Ordered by what a human most wants to see: what was asked, then what came
  // back, then why it did not.
  //
  // NOT `??`. Null-coalescing only falls through on null or undefined, and a failed run carries
  // `output: ""` - the reason lives in `launchError`. With `??`, EVERY runner-level failure would
  // render as a BLANK ROW: a stop, a timeout, a host shutdown, a missing Agent, a Prompt nobody
  // chose. A line that says something happened while refusing to say what is the same failure the
  // `started` and `progress` branches above guard against.
  //
  // The agent-facing renderer, MessageText, also reads `launchError` FIRST for a failure, so a
  // manager and the person watching the board are told the same reason.
  const text = [
    // AHEAD OF `instruction`, and both stay. A row is one glance, so it shows the subject a
    // person wrote or the one the tell derived from the first line - while `instruction` is
    // the only thing a row without a subject has. Neither is dead: the log is append-only and both shapes reach this feed.
    payload.subject,
    payload.instruction,
    payload.output,
    // `workflow.completed` carries {delivered} AND NOTHING ELSE IN THIS CHAIN, so without it the
    // one row that announces a workflow is finished is the one row with no words in it - an em
    // dash where the Manager's own summary of what it delivered should be.
    //
    // THE AGENT-FACING RENDERER HAS IT TOO - `MessageText.cs` writes "declared the workflow
    // completed. It delivered: ..." - so the Manager and the person watching the board read the
    // same declaration.
    payload.delivered,
    payload.reason,
    // `needs-decision` carries {container, question}: a member that stopped ON PURPOSE and is
    // waiting on a person. IN THE SHARED CHAIN rather than an arm of its own, because
    // every renderer that writes its own chain is another place to get the
    // ordering wrong. It sits beside `reason` because both are the member's own account of why it stopped.
    payload.question,
    payload.launchError,
    // `progress` carries {status} and none of the others.
    payload.status,
  ].find((value) => value !== null && value !== undefined && String(value).trim() !== '')

  return text === undefined ? '' : String(text)
}

const InstructionPrefix = 'agentContainer.instruction.'

/**
 * THE TYPES A PERSON PUBLISHES, whose team lives in the PAYLOAD rather than in the source.
 *
 * The client-side twin of `MessageTeam.Of`, and deliberately the same CLOSED SET OF TYPES rather
 * than a payload sniff over every row: nothing outside the platform can write either type -
 * `kanban.card.*` comes from the three `KanbanEndpoints` routes and `workflow.closed` from the
 * close route alone - and a container cannot choose a message type at all. Sniffing every
 * payload for a `team` field would instead file any row that happened to carry one.
 */
const KanbanCardPrefix = 'kanban.card.'
const WorkflowClosed = 'workflow.closed'

function publishedByAPerson(type: string): boolean {
  return type === WorkflowClosed || type.startsWith(KanbanCardPrefix)
}

/** The `team` field of a payload, or null for anything unusable - malformed JSON, a missing
 *  field, a blank. Null is the recoverable direction: the caller keeps the source it had. */
function teamOf(payload: string): string | null {
  try {
    const parsed = JSON.parse(payload) as Record<string, unknown>
    const team = parsed.team

    if (team === null || team === undefined) return null

    const text = String(team).trim()

    return text === '' ? null : text
  } catch {
    return null
  }
}

/**
 * `agentContainer.completed` reads as `completed`; an addressed instruction as `→ name`.
 *
 * The BARE name after the arrow, from a type whose suffix is now the qualified
 * `Team/Name`. The qualified form is an IDENTITY, not a label: it belongs on the
 * wire, in the message type and in the id, and nowhere on screen. A badge reading
 * `→ Alpha/Manager` on a card that already sits under its team's heading repeats
 * what the person can see and is a user-visible change to a slice that promised
 * none.
 */
export function label(type: string): string {
  if (!type.startsWith(InstructionPrefix)) return type.replace('agentContainer.', '')

  const id = type.slice(InstructionPrefix.length)

  // Neither half may contain a `/` - see ContainerId.IsLegalName - so the last one
  // is the separator, and a suffix carrying no separator at all is returned whole.
  return `→ ${id.slice(id.lastIndexOf('/') + 1)}`
}

/**
 * Whose card a message belongs on.
 *
 * Usually the publisher — a container's own events are its own story. The
 * exception is an ADDRESSED INSTRUCTION, whose source is the SENDER rather than
 * the addressee, so filing by source put "tell the manager to say hi" in a
 * bucket the board never renders and the instruction simply never appeared.
 * The addressee is in the type, which is the whole point of addressing riding on
 * the type rather than the payload.
 *
 * The source is the authenticated principal — `console-<team>` for a console's
 * agent, a user id for a person — and is not a stable vocabulary, which is
 * exactly why this reads the type: filing by source would break the board the
 * day a principal's shape changed, and nothing here would fail.
 */
export function ownerOf(message: Message): string {
  if (message.type === 'agentContainer.scheduleSkipped') {
    try {
      const payload = JSON.parse(message.payload) as Record<string, unknown>

      // `member` is the current field - this row's own `source` is `schedule:<id>`, never the
      // member, which is why the payload carries its identity at all (see the server's
      // MessageTeam.cs and EventCatalog's ScheduleSkipped entry). `container` is read as a
      // PERMANENT ALIAS: the log is append-only, and a row may carry the identity under that key.
      const owner = payload.member ?? payload.container
      if (owner !== null && owner !== undefined && String(owner).trim() !== '') {
        return String(owner)
      }
    } catch {
      // fall through to source
    }
  }

  if (message.type.startsWith(InstructionPrefix)) {
    return message.type.slice(InstructionPrefix.length)
  }

  // THE RULE, ONCE: a row whose payload names a team and whose source is NOT `Team/Name` files
  // under the TEAM. A person publishes these, so their source is a bare user id - and a bucket
  // keyed by a user id is matched by nothing: `teamActivity` collects `Team/`-prefixed keys, so
  // `teamLogFacts` would not see a close at all and the tab chip would go on calling a closed
  // workflow open.
  //
  // THE NAME HALF IS EMPTY ON PURPOSE. `ContainerId.IsLegalName` requires at least one
  // character, starting alphanumeric, so no container can ever own this key - it is the ABSENCE
  // of an identifier rather than an invented one, and it is collision-proof by a rule already
  // enforced on the server. It also means no CARD reads these rows, which is right: a person's
  // act is not a container's act.
  //
  // NOT `${team}/Manager`, WHICH WAS THE OTHER CANDIDATE. A team with no Manager is still
  // reachable, so that key would file the row nowhere again - on exactly the teams most likely
  // to have a person closing workflows by hand.
  //
  // THE SOURCE TEST IS LOAD-BEARING, not a formality. A CONTAINER can publish a card event too:
  // the three card routes are reached by whoever holds `Progress`, which includes a team's own
  // members, and a Manager answering a card wake comments on it exactly as its seeded skill
  // instructs. That row IS a container's act and belongs on that container's card.
  if (!message.source.includes('/') && publishedByAPerson(message.type)) {
    const team = teamOf(message.payload)

    if (team !== null) return `${team}/`
  }

  return message.source
}

/**
 * Local wall-clock, to the second.
 *
 * Absolute rather than relative: the point of a timestamp here is lining a board
 * row up against what the console printed, and "2m ago" cannot be compared to
 * anything.
 */
export function timeOf(occurredAt: string): string {
  const at = new Date(occurredAt)

  return Number.isNaN(at.getTime())
    ? ''
    : at.toLocaleTimeString(undefined, { hour12: false })
}
