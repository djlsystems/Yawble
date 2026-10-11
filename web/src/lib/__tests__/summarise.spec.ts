import { describe, expect, it } from 'vitest'
import type { Message } from '../../api/types'
import { dateTimeOf, detail, label, ownerOf, summarise, timeOf } from '../summarise'

const message = (payload: unknown): Message => ({
  seq: 1,
  type: 'agentContainer.completed',
  payload: JSON.stringify(payload),

  // QUALIFIED. Message.Source carries `Team/Name` now; a fixture still saying
  // 'Manager' tests a wire format the server stopped emitting.
  source: 'Alpha/Manager',
  correlationId: 1,
  causationSeq: null,
  depth: 0,
  occurredAt: '',
})

describe('summarise', () => {
  it('prefers the instruction, then the output', () => {
    expect(summarise(message({ instruction: 'do the thing' }))).toBe('do the thing')
    expect(summarise(message({ output: 'it is Tuesday' }))).toBe('it is Tuesday')
  })

  it('falls back through reason and launchError', () => {
    expect(summarise(message({ reason: 'queue is at its ceiling' }))).toBe(
      'queue is at its ceiling',
    )
    expect(summarise(message({ launchError: 'not on PATH' }))).toBe('not on PATH')
  })

  /** A feed line is one line. An agent's answer is usually not. */
  it('collapses whitespace so a multi-line answer stays one line', () => {
    expect(summarise(message({ output: 'line one\n\n  line two' }))).toBe('line one line two')
  })

  /**
   * A container may publish a type this UI has never heard of. An unreadable line
   * beats a board that throws.
   */
  it('returns empty rather than throwing on a payload that is not JSON', () => {
    expect(summarise({ ...message({}), payload: 'not json' })).toBe('')
  })

  it('returns empty when no known field is present', () => {
    expect(summarise(message({ container: 'Alpha/Manager' }))).toBe('')
  })
})

describe('label', () => {
  it('strips the container prefix', () => {
    expect(label('agentContainer.completed')).toBe('completed')
  })

  /**
   * The BARE name after the arrow, from a QUALIFIED type. The qualified form is an
   * identity, not a label - it belongs on the wire and in the id, and a badge
   * reading `→ Alpha/Manager` is a user-visible change on a card that already sits
   * under its team's heading.
   */
  it('renders an addressed instruction as an arrow onto the bare name', () => {
    expect(label('agentContainer.instruction.Alpha/Manager')).toBe('→ Manager')
  })

  /** A team whose name contains a space is still just a team name. */
  it('drops the team half however it is spelled', () => {
    expect(label('agentContainer.instruction.TestTeam/dev1')).toBe('→ dev1')
  })
})

describe('summarise for started', () => {
  /** Not a blank dash: a row admitting something happened but not what. */
  it('says what woke the container', () => {
    const started: Message = {
      ...message({}),
      type: 'agentContainer.started',
      payload: JSON.stringify({
        container: 'Alpha/Manager',
        trigger: 'agentContainer.instruction.Alpha/Manager',
      }),
    }

    expect(summarise(started)).toBe('woke on → Manager')
  })

  it('degrades to a bare woke when no trigger was recorded', () => {
    const started: Message = {
      ...message({}),
      type: 'agentContainer.started',
      payload: JSON.stringify({ container: 'Alpha/Manager' }),
    }

    expect(summarise(started)).toBe('woke')
  })
})

describe('ownerOf', () => {
  /**
   * The defect this exists for: POST /tell stamps an instruction's source as
   * `console`, so filing by source hid every instruction from the card it was
   * addressed to.
   */
  it('files an addressed instruction under its addressee, not its publisher', () => {
    const instruction: Message = {
      ...message({ instruction: 'say hi' }),
      type: 'agentContainer.instruction.Alpha/Manager',
      source: 'console',
    }

    // The QUALIFIED id, which is what the board buckets on - a container's identity
    // is the pair, and two teams can each hold a 'Manager'.
    expect(ownerOf(instruction)).toBe('Alpha/Manager')
  })

  it('files everything else under its publisher', () => {
    expect(ownerOf({ ...message({}), source: 'Alpha/Manager' })).toBe('Alpha/Manager')
  })

  it('files schedule-skipped under the targeted member rather than schedule:<id>', () => {
    const skipped: Message = {
      ...message({}),
      type: 'agentContainer.scheduleSkipped',
      payload: JSON.stringify({
        member: 'Alpha/Manager',
        reason: 'busy',
      }),
      source: 'schedule:s-1',
    }

    expect(ownerOf(skipped)).toBe('Alpha/Manager')
  })

  /**
   * THE PERMANENT ALIAS. A row may carry the owner under `container` rather than `member` - see the
   * server's `PayloadFields.Member` doc comment. The log is append-only, so such rows are read as
   * they are, forever.
   */
  it('still files a schedule-skipped row that carries a container field', () => {
    const skipped: Message = {
      ...message({}),
      type: 'agentContainer.scheduleSkipped',
      payload: JSON.stringify({
        container: 'Alpha/Manager',
        reason: 'busy',
      }),
      source: 'schedule:s-1',
    }

    expect(ownerOf(skipped)).toBe('Alpha/Manager')
  })

  /**
   * A ROW WHOSE PAYLOAD NAMES A TEAM AND WHOSE SOURCE IS NOT `Team/Name` FILES UNDER THE TEAM.
   * The client-side twin of `MessageTeam.Of`, and the same closed set of types: a person
   * publishes these, so their source is a bare user id with no `/` and the team travels in the
   * payload.
   *
   * WITHOUT THIS the row lands in a bucket keyed by the USER ID, which `teamActivity`'s
   * `Team/` prefix never matches - so `teamLogFacts` could not see a close at all and the tab
   * chip went on calling a closed workflow open.
   */
  it('files a close published by a person under its team', () => {
    const closed: Message = {
      ...message({ team: 'Alpha', reason: 'nothing left to do' }),
      type: 'workflow.closed',
      source: 'u-17',
    }

    // THE TEAM BUCKET, whose name half is EMPTY on purpose: `ContainerId.IsLegalName` requires
    // at least one character starting alphanumeric, so no container can ever own this key. It
    // is the absence of an identifier rather than an invented one, and a person's act is not a
    // container's act.
    expect(ownerOf(closed)).toBe('Alpha/')
  })

  it('files a card event published by a person under its team, by the same rule', () => {
    const edited: Message = {
      ...message({ team: 'Alpha', title: 'ship it' }),
      type: 'kanban.card.edited',
      source: 'u-17',
    }

    expect(ownerOf(edited)).toBe('Alpha/')
  })

  /**
   * A CONTAINER CAN PUBLISH A CARD EVENT TOO - the three `KanbanEndpoints` routes are reached by
   * whoever holds `Progress`, which includes a team's own members, and a Manager answering a
   * card wake comments on it exactly as its seeded skill instructs. That row IS a container's
   * act and belongs on that container's card, so the source test is what keeps the team arm off
   * it.
   */
  it("leaves a card event published by a container on that container's card", () => {
    const commented: Message = {
      ...message({ team: 'Alpha', comment: 'on it' }),
      type: 'kanban.card.commented',
      source: 'Alpha/Manager',
    }

    expect(ownerOf(commented)).toBe('Alpha/Manager')
  })

  /** FAILS BACK TO THE SOURCE, never to a bare `/`: an unusable payload is no answer, and a key
   *  with an empty team half would be matched by `teamActivity` for a team called ''. */
  it('falls back to the source when the payload names no usable team', () => {
    const blank: Message = {
      ...message({ team: '   ' }),
      type: 'workflow.closed',
      source: 'u-17',
    }
    const broken: Message = {
      ...message({}),
      type: 'workflow.closed',
      payload: 'not json',
      source: 'u-17',
    }

    expect(ownerOf(blank)).toBe('u-17')
    expect(ownerOf(broken)).toBe('u-17')
  })
})

describe('dateTimeOf', () => {
  it('puts the local date in front of the time, so a message from days ago reads as such', () => {
    const at = new Date(2026, 9, 5, 14, 27, 37)

    expect(dateTimeOf(at.toISOString())).toBe(`${at.toLocaleDateString()} ${timeOf(at.toISOString())}`)
    expect(dateTimeOf(at.toISOString())).toMatch(/14[:.]27[:.]37/)
  })

  it('returns empty for an unparseable stamp', () => {
    expect(dateTimeOf('not a date')).toBe('')
  })
})

describe('timeOf', () => {
  it('renders local wall-clock to the second', () => {
    const at = new Date(2026, 7, 14, 13, 5, 9)

    expect(timeOf(at.toISOString())).toMatch(/13[:.]05[:.]09/)
  })

  it('returns empty rather than Invalid Date for an unparseable stamp', () => {
    expect(timeOf('not a date')).toBe('')
  })

  it('renders a progress report, which carries {status} and nothing else', () => {
    // Without a `status` branch this fell through every other field and returned '' - a feed row
    // that says something happened while refusing to say what, exactly the defect the `started`
    // branch already exists to fix.
    expect(
      summarise({
        seq: 9,
        type: 'agentContainer.progress',
        payload: JSON.stringify({ status: 'gathering sources - 3 of 8' }),
        source: 'Alpha/Scout',
        correlationId: 3,
        causationSeq: 2,
        occurredAt: '2026-08-22T21:00:00Z',
      } as never),
    ).toBe('gathering sources - 3 of 8')
  })

  it('renders a blocked report, which carries {reason}', () => {
    // `reason` was already in the fallback chain - `container.rejected` uses it - so this needs no
    // new branch. Asserted anyway rather than assumed: the field name is what makes the feed row
    // legible, and a rename on the server would blank the row with nothing failing.
    expect(
      summarise({
        seq: 11,
        type: 'agentContainer.blocked',
        payload: JSON.stringify({ reason: 'contract freeze failed twice' }),
        source: 'Alpha/Scout',
        correlationId: 3,
        causationSeq: 2,
        occurredAt: '2026-08-23T13:27:52Z',
      } as never),
    ).toBe('contract freeze failed twice')
  })

  it('labels the blocked type without its prefix', () => {
    expect(label('agentContainer.blocked')).toBe('blocked')
  })

  describe('detail', () => {
    const report = {
      seq: 12,
      type: 'agentContainer.completed',
      payload: JSON.stringify({
        output: [
          'Owen Hale reported the freeze as done.',
          'Problem: the three files are missing.',
          'I will not send the same write back a third time.',
        ].join('\n\n'),
      }),
      source: 'alpha/Manager',
      correlationId: 102,
      causationSeq: 101,
      occurredAt: '2026-08-23T13:27:56Z',
    } as never

    it('keeps the paragraphs the agent wrote', () => {
      // The whole reason the hover card is readable at all. `summarise` collapses these into one
      // run-on line, which is right for a two-line clamp and is a wall of text at full size.
      expect(detail(report).split('\n\n')).toHaveLength(3)
      expect(detail(report)).toContain('Problem: the three files are missing.')
    })

    it('is the same field summarise renders, so the card cannot contradict the row', () => {
      // Both read one shared chain. Two copies of it would be two answers waiting to disagree, and
      // the disagreement would look like the card lying about the row it was opened over.
      expect(summarise(report)).toBe(detail(report).replace(/\s+/g, ' ').trim())
    })

    it('normalises carriage returns so a Windows payload is not double-spaced', () => {
      const windows = {
        ...(report as object),
        payload: JSON.stringify({ output: 'one\r\ntwo' }),
      } as never

      expect(detail(windows)).toBe('one\ntwo')
    })

    it('returns empty for a payload that is not JSON, exactly as summarise does', () => {
      const broken = { ...(report as object), payload: 'not json' } as never

      expect(detail(broken)).toBe('')
      expect(summarise(broken)).toBe('')
    })
  })
})

describe('a failure whose reason is not in output', () => {
  /**
   * A runner-level failure carries `output: ""` and puts the reason in `launchError`. A chain using
   * `??`, which only falls through on null or undefined, would let the empty string win and render
   * a BLANK row - for a stop, a timeout, a host shutdown, a missing Agent, a Prompt nobody chose.
   *
   * The agent-facing renderer, MessageText, also reads launchError FIRST for a failure, so a
   * manager and the person watching the board are told the same reason.
   */
  it('reads launchError when output is an empty string', () => {
    expect(summarise(message({
      output: '',
      launchError: 'Alpha/Worker was stopped from the board.',
    }))).toContain('stopped from the board')
  })

  it('still prefers real output when there is some', () => {
    expect(summarise(message({
      output: 'what it managed to say',
      launchError: 'and why it died',
    }))).toContain('what it managed to say')
  })

  it('a whitespace-only output does not swallow the reason either', () => {
    expect(summarise(message({
      output: '   \n  ',
      launchError: 'the real reason',
    }))).toContain('the real reason')
  })
})

/**
 * The SPA's half of tier 1 of the quiet-team check.
 *
 * The progress route marks a report that arrived when `container.CurrentCausation` was null - a
 * process that outlived the invocation that started it, narrating into a run nobody is going to
 * read the result of. `harness status` has rendered that since tier 1 landed; the browser
 * rendered an ordinary progress row, which is the reading the mark exists to prevent.
 *
 * Tested HERE and never against ContainerCard's source: a spec that reads a component's text is
 * not a test of it, and `summarise` is where the row's words are actually decided.
 */
describe('a progress report that belonged to no run', () => {
  const progress = (payload: unknown): Message => ({
    seq: 61,
    type: 'agentContainer.progress',
    payload: JSON.stringify(payload),
    source: 'Alpha/Digger',
    correlationId: 7,
    causationSeq: null,
    depth: 0,
    occurredAt: '2026-09-01T09:00:00Z',
  })

  /**
   * FIRST, and that position is the assertion. The row is clamped to two lines, so a mark at the
   * end of a truncated line is a mark that is not there - the same reason StatusCommand puts it
   * first. A reader scanning the feed otherwise takes an ordinary status line at face value and
   * concludes the job is simply still going.
   */
  it('marks it, and the mark comes before the status', () => {
    const row = summarise(progress({ status: 'Gate still running', whileIdle: true }))

    expect(row).toBe('[after its run ended] Gate still running')
    expect(row.indexOf('after its run ended')).toBeLessThan(row.indexOf('Gate still running'))
  })

  /**
   * The one that matters. A mark on every line is a mark nobody reads, and the ordinary case is
   * every progress row this platform has ever published.
   */
  it('leaves an ordinary progress row alone', () => {
    const row = summarise(progress({ status: 'reading the brief' }))

    expect(row).toBe('reading the brief')
    expect(row).not.toContain('after its run ended')
  })

  /**
   * THE FLAG IS WRITTEN ONLY WHEN TRUE - the route serialises two different shapes rather than one
   * shape with a boolean. So anything other than `true` here is a payload this platform does not
   * emit, and guessing at it is how a mark starts appearing on rows that earned it.
   */
  it('marks on the flag being TRUE, never on it merely being present', () => {
    expect(summarise(progress({ status: 'still going', whileIdle: false }))).toBe('still going')
    expect(summarise(progress({ status: 'still going', whileIdle: 'true' }))).toBe('still going')
  })

  /** Both renderers read one chain, so the hover card cannot show a row the feed did not. */
  it('marks the hover card the same way', () => {
    expect(detail(progress({ status: 'Gate still running', whileIdle: true }))).toBe(
      '[after its run ended] Gate still running',
    )
  })
})

/**
 * `agentContainer.needsDecision`, the fourth terminal fact.
 *
 * A member that stopped ON PURPOSE to ask its owner something. Not `completed` (the step is not
 * done), not `blocked` (nobody gave up), not `failed` (nothing broke). Its payload carries
 * {container, question} and none of the fields the chain already reads, so without an arm it
 * renders as a blank row - which is this spec's whole subject arriving through a new door.
 */
describe('summarise for needs-decision', () => {
  const asked = (payload: unknown): Message => ({
    seq: 71,
    type: 'agentContainer.needsDecision',
    payload: JSON.stringify(payload),
    source: 'Alpha/Manager',
    correlationId: 9,
    causationSeq: 8,
    depth: 0,
    occurredAt: '2026-09-02T08:15:00Z',
  })

  it('renders the question', () => {
    expect(
      summarise(
        asked({
          container: 'Alpha/Manager',
          question: 'Should the sweep write tenant_events or the message log?',
        }),
      ),
    ).toBe('Should the sweep write tenant_events or the message log?')
  })

  /**
   * THROUGH THE SHARED CHAIN, not a fourth private one. The standing argument: the same
   * defect has now been found in three renderers of a payload, each of which had written its own
   * ordering. A branch of its own here would be the fourth place to get it wrong.
   */
  it('reads through the same ordering every other row does', () => {
    // `instruction` outranks it for the same reason it outranks `output` - and no real payload
    // carries both. Asserted so a future arm cannot be quietly bolted on ahead of the chain.
    expect(summarise(asked({ instruction: 'do the thing', question: 'or should I?' }))).toBe(
      'do the thing',
    )
  })

  it('keeps the paragraphs of a multi-part question in the hover card', () => {
    const three = asked({
      container: 'Alpha/Manager',
      question: ['1. Which suite gates this?', '2. Who merges?', '3. Ship without §5?'].join('\n\n'),
    })

    expect(detail(three).split('\n\n')).toHaveLength(3)
    expect(summarise(three)).toBe(detail(three).replace(/\s+/g, ' ').trim())
  })

  it('labels the type without its prefix', () => {
    expect(label('agentContainer.needsDecision')).toBe('needsDecision')
  })

  /** Published by the container itself, so it files on its own card - exactly like `blocked`. */
  it('files on the asking container’s card', () => {
    expect(ownerOf(asked({ container: 'Alpha/Manager', question: 'well?' }))).toBe('Alpha/Manager')
  })
})

/**
 * THE ROW THAT ANNOUNCES A WORKFLOW IS FINISHED HAS WORDS IN IT.
 *
 * `workflow.completed` carries {delivered} and nothing else this chain reads, so without it the
 * Manager's own summary of what the team delivered would render as an em dash.
 *
 * `MessageText.cs` writes "declared the workflow completed. It delivered: ..." - so the Manager
 * and the person watching the board read the same declaration. One chain, three renderers, and
 * they must agree about every field.
 */
describe('a workflow declaration', () => {
  it('renders what the manager said it delivered', () => {
    expect(summarise(message({ delivered: 'console app that prints Hello You built and tested' })))
      .toBe('console app that prints Hello You built and tested')
  })

  /** Ordered with `output`, not ahead of it: a run's own answer still wins where both exist. */
  it('does not displace a run output that is also present', () => {
    expect(summarise(message({ output: 'the run said this', delivered: 'the declaration' })))
      .toBe('the run said this')
  })
})

/**
 * A PERSON CLOSING A WORKFLOW - not the same claim as `workflow.completed` above, and its own arm
 * for the same reason: `payload.reason` is already in the shared chain (needs-decision reads it
 * too), so a closure WITH a reason would already render as the bare reason text with nothing
 * marking it a closure, and one with NO reason - accepted on purpose - would match nothing in that
 * chain and render as an em dash, the exact "something happened and nothing says what" shape this
 * file exists to prevent.
 */
describe('a workflow closure', () => {
  const closed = (payload: unknown): Message => ({ ...message(payload), type: 'workflow.closed' })

  it('renders the reason when one was given', () => {
    expect(summarise(closed({ team: 'Alpha', reason: 'undeclared - last active 22h ago' })))
      .toBe('closed: undeclared - last active 22h ago')
  })

  it('still reads as something happened when no reason was given', () => {
    expect(summarise(closed({ team: 'Alpha' }))).toBe('closed')
  })

  it('treats a blank reason the same as none', () => {
    expect(summarise(closed({ team: 'Alpha', reason: '' }))).toBe('closed')
  })
})

/**
 * An instruction carries a SUBJECT and a BODY now, and every row written before it does not.
 *
 * The log is append-only and both shapes reach the same feed, so neither branch is dead code
 * waiting to be cleaned up - the fallback is permanent.
 */
describe('an instruction split into a subject and a body', () => {
  const instruction = (payload: unknown): Message => ({
    ...message(payload),
    type: 'agentContainer.instruction.Alpha/dev1',
  })

  const split = {
    instruction: 'Ship the release\n\nCheck the changelog first.\nThen tag it.',
    subject: 'Ship the release',
    body: 'Check the changelog first.\nThen tag it.',
  }

  it('shows the subject in a row, because a row is one glance', () => {
    expect(summarise(instruction(split))).toBe('Ship the release')
  })

  it('shows the subject AND the body in the hover card, which has room for both', () => {
    expect(detail(instruction(split))).toBe(
      'Ship the release\n\nCheck the changelog first.\nThen tag it.',
    )
  })

  /**
   * A subject somebody TYPED is not a slice of the instruction - the body is then the whole text.
   * So the hover card genuinely has two things to show, and a renderer still reading `instruction`
   * alone would drop the heading and look correct.
   */
  it('shows a typed subject above the instruction it heads', () => {
    const titled = instruction({
      instruction: 'do the thing',
      subject: 'Nightly build',
      body: 'do the thing',
    })

    expect(summarise(titled)).toBe('Nightly build')
    expect(detail(titled)).toBe('Nightly build\n\ndo the thing')
  })

  it('reads a row written before the split from its instruction alone', () => {
    const old = instruction({ instruction: 'do the thing\n\nand then the other thing' })

    expect(summarise(old)).toBe('do the thing and then the other thing')
    expect(detail(old)).toBe('do the thing\n\nand then the other thing')
  })

  /**
   * THE INDENTATION IS PART OF WHAT THE BODY SAYS, and this renderer must not flatten it.
   *
   * `InstructionText.TrimLeadingBlankLines` goes out of its way to KEEP the first body line's
   * indentation - it walks whole blank lines rather than characters - because an instruction's
   * list or command example is shaped on purpose. A per-field `.trim()` on the way to the hover
   * card undid exactly that, in the one renderer that displays the shape: the row above collapses
   * whitespace anyway, so nothing else could have shown it.
   */
  it('keeps a body indented the way the instruction was written', () => {
    const shaped = instruction({
      instruction: 'Set the repos up\n\n  - clone main\n  - run the tests',
      subject: 'Set the repos up',
      body: '  - clone main\n  - run the tests',
    })

    expect(detail(shaped)).toBe('Set the repos up\n\n  - clone main\n  - run the tests')
  })

  /**
   * An instruction short enough to be its own title has an EMPTY body, and the hover card must not
   * then render a trailing blank paragraph under a heading with nothing beneath it.
   */
  it('renders a subject with no body as just the subject', () => {
    const short = instruction({ instruction: 'Ship it', subject: 'Ship it', body: '' })

    expect(summarise(short)).toBe('Ship it')
    expect(detail(short)).toBe('Ship it')
  })

  /** A completion has neither field, so nothing about this may change what it renders. */
  it('leaves every other message type where it was', () => {
    expect(summarise(message({ output: 'it is Tuesday' }))).toBe('it is Tuesday')
    expect(detail(message({ output: 'one\r\ntwo' }))).toBe('one\ntwo')
  })
})
