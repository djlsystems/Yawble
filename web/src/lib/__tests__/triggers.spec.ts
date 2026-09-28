import { describe, expect, it } from 'vitest'
import {
  builtInWakeSources,
  categoryLabel,
  categoryOfEventType,
  createTriggerRequestFromDraft,
  draftForCreate,
  draftFromTrigger,
  eventTypeSelectOptions,
  everyFromSeconds,
  folderPollSummary,
  formatPollDuration,
  watchRootOptions,
  fromDateTimeLocalValue,
  instructionTypeFor,
  instructionWakeSource,
  previewCronOccurrences,
  sortEventTypesForPicker,
  toDateTimeLocalValue,
  updateTriggerPatchFromDraft,
  MinimumIntervalSeconds,
  outcomeBadge,
  triggerDraftProblem,
  triggerFieldProblems,
  type TriggerDraft,
  renderNextDue,
  triggerSentence,
  secondsForEvery,
  validateSchedule,
  measuredCostLine,
  needsShortScheduleConfirmation,
  shortestScheduleGapSeconds,
  spentTodayLine,
  wakeManagerOf,
} from '../triggers'
import { asTeamId, type TeamTrigger } from '../../api/types'

/**
 * The folder-change fields every draft carries, at their defaults - written out as
 * literals rather than read from the constants, so `draftForCreate`'s defaults are pinned here and
 * not merely restated.
 */
const folderDefaults = {
  watchRoot: 'documents',
  watchPath: '',
  watchGlob: '',
  pollSeconds: 60,
  quietSeconds: 30,
  minIntervalSeconds: 60,
}

/** The cost-control fields every NEW draft carries: wake the Manager only on a hand-back or a
 *  failure, and no daily cap. */
const costDefaults = {
  wakeManager: 'onHandbackOrFailure' as const,
  dailyTokenCap: null,
}

describe('triggerSentence', () => {
  it('describes simple interval schedules in human words', () => {
    expect(triggerSentence({ kind: 'Every', intervalSeconds: 300 })).toBe('every 5 minutes')
    expect(triggerSentence({ kind: 'Every', intervalSeconds: 90 })).toBe('every 90 seconds')
  })

  it('describes a weekday cron with clock time and zone', () => {
    expect(triggerSentence({
      kind: 'Cron',
      expression: '0 0 9 * * 1-5',
      timezone: 'Europe/London',
    })).toBe('weekdays at 09:00 Europe/London')
  })

  it('describes a one-off schedule with a fixed UTC instant', () => {
    expect(triggerSentence({
      kind: 'Once',
      fireAt: '2026-08-26T14:30:00Z',
    })).toBe('once, at 2026-08-26 14:30 UTC')
  })

  it('still renders every schedule shape', () => {
    expect(triggerSentence({ kind: 'every', intervalSeconds: 300 })).toBe('every 5 minutes')
  })

  /**
   * THE WORDS COME FROM THE CATALOG'S OWN `summary`, PASSED IN - never a map of event type to
   * phrase kept in this file. A hand-written copy of what an event means would be a second store of
   * a fact `GET /api/events` already owns, and the two would drift the moment a summary changed.
   * `triggerSentence` never fetches the catalog itself; the caller (eventually `TriggerEditDialog`)
   * is the one holding it, from a `listEvents()` call this library does not make.
   */
  it('renders an unfiltered event trigger from the catalog definition handed to it', () => {
    expect(triggerSentence(
      { kind: 'event', eventType: 'agentContainer.failed' },
      { summary: 'a member fails' },
    )).toBe('when a member fails')
  })

  it('renders a filtered event trigger, naming the field and value', () => {
    expect(triggerSentence(
      {
        kind: 'event',
        eventType: 'agentContainer.failed',
        filter: 'container eq alpha/dev1',
      },
      { summary: 'a member fails' },
    )).toBe('when a member fails, where container is alpha/dev1')
  })

  it('renders a contains filter with its own wording', () => {
    expect(triggerSentence(
      { kind: 'event', eventType: 'agentContainer.completed', filter: 'output contains error' },
      { summary: 'a member finishes a run' },
    )).toBe('when a member finishes a run, where output contains error')
  })

  it('falls back to the raw event type when no definition is supplied', () => {
    // A caller that has not loaded the catalog yet (or named a type the catalog does not
    // declare) gets the plain type name rather than an invented phrase - the same "no fallback
    // text stands in for a definition nobody supplied" rule `EventCatalog.For` and `AgentCatalog.For`
    // apply on the server.
    expect(triggerSentence({ kind: 'event', eventType: 'agentContainer.failed' }))
      .toBe('when agentContainer.failed')
  })

  it('ignores a filter it cannot read rather than mangling the sentence', () => {
    expect(triggerSentence(
      { kind: 'event', eventType: 'agentContainer.failed', filter: 'nonsense' },
      { summary: 'a member fails' },
    )).toBe('when a member fails')
  })
})

describe('categoryOfEventType', () => {
  it('reads the first dot-segment as the category', () => {
    expect(categoryOfEventType('agentContainer.started')).toBe('agentContainer')
    expect(categoryOfEventType('workflow.completed')).toBe('workflow')
    expect(categoryOfEventType('kanban.card.moved')).toBe('kanban')
  })

  it('falls back to the whole string for a type with no dot', () => {
    expect(categoryOfEventType('undotted')).toBe('undotted')
  })
})

describe('categoryLabel', () => {
  /**
   * THE NAMING RULE: "container" alone reads as a Kubernetes container, so the category display
   * name must say "Agent container" - never the bare word. This is the client-side half of the
   * server's `No_summary_names_a_bare_container` test.
   */
  it('never presents the bare word "container"', () => {
    expect(categoryLabel('agentContainer')).toBe('Agent container')
  })

  it('labels the other known categories in plain words', () => {
    expect(categoryLabel('workflow')).toBe('Workflow')
    expect(categoryLabel('kanban')).toBe('Kanban')
  })

  it('capitalises an unrecognised category rather than hiding it', () => {
    expect(categoryLabel('scheduling')).toBe('Scheduling')
  })
})

describe('sortEventTypesForPicker', () => {
  const events = [
    { type: 'workflow.completed', summary: 'A manager finishes a workflow.' },
    { type: 'agentContainer.started', summary: 'A member began a run.' },
    { type: 'kanban.card.moved', summary: 'A person moved a card.' },
    { type: 'agentContainer.failed', summary: 'A run did not complete.' },
  ]

  it('groups by category label, then orders by code within a category', () => {
    expect(sortEventTypesForPicker(events).map((e) => e.type)).toEqual([
      // "Agent container" < "Kanban" < "Workflow" alphabetically
      'agentContainer.failed',
      'agentContainer.started',
      'kanban.card.moved',
      'workflow.completed',
    ])
  })

  it('carries the resolved category and its display label on every row', () => {
    const [first] = sortEventTypesForPicker(events)
    expect(first).toMatchObject({
      type: 'agentContainer.failed',
      category: 'agentContainer',
      categoryLabel: 'Agent container',
      summary: 'A run did not complete.',
    })
  })
})

describe('eventTypeSelectOptions', () => {
  const events = [
    { type: 'workflow.completed', summary: 'A manager finishes a workflow.' },
    { type: 'agentContainer.started', summary: 'A member began a run.' },
    { type: 'agentContainer.failed', summary: 'A run did not complete.' },
  ]

  it('interleaves one disabled header per category with its selectable rows', () => {
    const options = eventTypeSelectOptions(events)

    expect(options).toEqual([
      { value: '__category:agentContainer', label: 'Agent container', isHeader: true, disable: true },
      {
        value: 'agentContainer.failed', label: 'agentContainer.failed',
        caption: 'A run did not complete.', isHeader: false, disable: false,
      },
      {
        value: 'agentContainer.started', label: 'agentContainer.started',
        caption: 'A member began a run.', isHeader: false, disable: false,
      },
      { value: '__category:workflow', label: 'Workflow', isHeader: true, disable: true },
      {
        value: 'workflow.completed', label: 'workflow.completed',
        caption: 'A manager finishes a workflow.', isHeader: false, disable: false,
      },
    ])
  })

  it('never emits a header value that could collide with a real event type', () => {
    const options = eventTypeSelectOptions(events)
    const headerValues = options.filter((o) => o.isHeader).map((o) => o.value)
    const eventTypeValues = new Set(events.map((e) => e.type))

    for (const value of headerValues) {
      expect(eventTypeValues.has(value)).toBe(false)
    }
  })

  it('returns nothing for an empty catalog', () => {
    expect(eventTypeSelectOptions([])).toEqual([])
  })
})

describe('renderNextDue', () => {
  const now = new Date('2026-08-25T20:00:00Z')

  it('renders missing and invalid due times honestly', () => {
    expect(renderNextDue(null, now)).toBe('not scheduled')
    expect(renderNextDue('not-a-date', now)).toBe('invalid date')
  })

  it('renders upcoming and elapsed due times relatively', () => {
    expect(renderNextDue('2026-08-25T20:05:00Z', now)).toBe('in 5 minutes')
    expect(renderNextDue('2026-08-25T19:55:00Z', now)).toBe('5 minutes ago')
  })

  it('treats near-now due times as due now', () => {
    expect(renderNextDue('2026-08-25T20:00:10Z', now)).toBe('due now')
  })
})

describe('outcomeBadge', () => {
  it('maps known outcomes to stable badge labels and tones', () => {
    expect(outcomeBadge('fired')).toEqual({ label: 'fired', color: 'positive' })
    expect(outcomeBadge('skipped')).toEqual({ label: 'skipped (busy)', color: 'warning' })
    expect(outcomeBadge('missed')).toEqual({ label: 'missed', color: 'negative' })
    expect(outcomeBadge('member-missing')).toEqual({ label: 'member missing', color: 'negative' })
  })

  it('handles unknown and empty outcomes without failing open', () => {
    expect(outcomeBadge(null)).toEqual({ label: 'never fired', color: 'grey-6' })
    expect(outcomeBadge('custom-outcome')).toEqual({ label: 'custom outcome', color: 'info' })
  })
})

describe('validateSchedule', () => {
  const now = new Date('2026-08-25T20:00:00Z')

  it('accepts valid every, cron and one-off schedules', () => {
    expect(validateSchedule({ kind: 'Every', intervalSeconds: 120 }, now)).toBeNull()
    expect(validateSchedule({
      kind: 'Cron',
      expression: '0 */5 * * * *',
      timezone: 'UTC',
    }, now)).toBeNull()
    expect(validateSchedule({
      kind: 'Once',
      fireAt: '2026-08-25T20:05:00Z',
    }, now)).toBeNull()
  })

  it('refuses invalid intervals and past one-off timestamps', () => {
    expect(validateSchedule({ kind: 'Every', intervalSeconds: 0 }, now))
      .toContain(`${MinimumIntervalSeconds} seconds`)
    expect(validateSchedule({ kind: 'Every', intervalSeconds: 1.5 }, now))
      .toContain('whole number')
    expect(validateSchedule({ kind: 'Once', fireAt: '2026-08-25T19:59:00Z' }, now))
      .toContain('must be in the future')
  })

  it('refuses invalid cron expressions and zones', () => {
    expect(validateSchedule({
      kind: 'Cron',
      expression: '*/5 * *',
      timezone: 'UTC',
    }, now)).toContain('5 or 6 fields')

    expect(validateSchedule({
      kind: 'Cron',
      expression: '0 */5 * * * *',
      timezone: 'Not/AZone',
    }, now)).toContain('valid IANA zone')
  })
})

describe('trigger draft helpers', () => {
  it('maps interval seconds to the most readable unit and back', () => {
    expect(everyFromSeconds(300)).toEqual({ everyCount: 5, everyUnit: 'minutes' })
    expect(secondsForEvery(2, 'hours')).toBe(7200)
  })

  it('normalises a persisted cron row into an editable draft', () => {
    const draft = draftFromTrigger({
      id: 's1',
      team: asTeamId('Alpha'),
      container: 'Manager',
      name: 'Weekday standup',
      instruction: 'Post update.',
      kind: 'cron',
      expression: '0 0 9 * * 1-5',
      timezone: 'Europe/London',
      intervalSeconds: null,
      fireAt: null,
      idleOnly: true,
      enabled: true,
      nextDueAt: null,
      lastFiredAt: null,
      lastOutcome: null,
      lastSeq: null,
      missedCount: 0,
      createdAt: '2026-08-25T20:00:00Z',
      createdBy: 'u1',
      eventType: null,
      filter: null,
    })

    expect(draft.mode).toBe('cron')
    expect(draft.cronExpression).toBe('0 0 9 * * 1-5')
    expect(draft.cronTimezone).toBe('Europe/London')
    expect(draft.eventType).toBe('')
    expect(draft.filter).toBe('')
  })

  it('normalises a persisted event row into an editable draft', () => {
    const draft = draftFromTrigger({
      id: 's2',
      team: asTeamId('Alpha'),
      container: 'Manager',
      name: 'On failure',
      instruction: 'Look at it.',
      kind: 'event',
      expression: null,
      timezone: null,
      intervalSeconds: null,
      fireAt: null,
      idleOnly: true,
      enabled: true,
      nextDueAt: null,
      lastFiredAt: null,
      lastOutcome: null,
      lastSeq: null,
      missedCount: 0,
      createdAt: '2026-08-25T20:00:00Z',
      createdBy: 'u1',
      eventType: 'agentContainer.failed',
      filter: 'container eq alpha/dev1',
    })

    expect(draft.mode).toBe('event')
    expect(draft.eventType).toBe('agentContainer.failed')
    expect(draft.filter).toBe('container eq alpha/dev1')
  })

  it('builds create payloads from draft state', () => {
    const body = createTriggerRequestFromDraft({
      name: 'Every five',
      container: 'Manager',
      instruction: 'Do the thing.',
      mode: 'every',
      everyCount: 5,
      everyUnit: 'minutes',
      cronExpression: '',
      cronTimezone: '',
      fireAt: '',
      idleOnly: true,
      enabled: false,
      eventType: '',
      filter: '',
      ...folderDefaults,
      ...costDefaults,
    })

    expect(body).toEqual({
      name: 'Every five',
      container: 'Manager',
      instruction: 'Do the thing.',
      kind: 'every',
      expression: null,
      timezone: null,
      intervalSeconds: 300,
      fireAt: null,
      idleOnly: true,
      enabled: false,
      eventType: null,
      filter: null,
      wakeManager: 'onHandbackOrFailure',
      dailyTokenCap: null,
    })
  })

  it('builds an event create payload, carrying the event type and filter', () => {
    const body = createTriggerRequestFromDraft({
      name: 'On failure',
      container: 'Manager',
      instruction: 'Look at it.',
      mode: 'event',
      everyCount: 5,
      everyUnit: 'minutes',
      cronExpression: '',
      cronTimezone: '',
      fireAt: '',
      idleOnly: true,
      enabled: true,
      eventType: 'agentContainer.failed',
      filter: 'container eq alpha/dev1',
      ...folderDefaults,
      ...costDefaults,
    })

    expect(body).toEqual({
      name: 'On failure',
      container: 'Manager',
      instruction: 'Look at it.',
      kind: 'event',
      expression: null,
      timezone: null,
      intervalSeconds: null,
      fireAt: null,
      idleOnly: true,
      enabled: true,
      eventType: 'agentContainer.failed',
      filter: 'container eq alpha/dev1',
      wakeManager: 'onHandbackOrFailure',
      dailyTokenCap: null,
    })
  })

  it('builds PATCH with only changed fields and explicit clears', () => {
    const existing = {
      id: 's1',
      team: asTeamId('Alpha'),
      container: 'Manager',
      name: 'Morning cron',
      instruction: 'Ping.',
      kind: 'cron' as const,
      expression: '0 0 9 * * 1-5',
      timezone: 'Europe/London',
      intervalSeconds: null,
      fireAt: null,
      idleOnly: true,
      enabled: true,
      nextDueAt: null,
      lastFiredAt: null,
      lastOutcome: null,
      lastSeq: null,
      missedCount: 0,
      createdAt: '2026-08-25T20:00:00Z',
      createdBy: 'u1',
      eventType: null,
      filter: null,
    }

    const patch = updateTriggerPatchFromDraft(existing, {
      name: 'Morning every',
      container: 'Manager',
      instruction: 'Ping.',
      mode: 'every',
      everyCount: 10,
      everyUnit: 'minutes',
      cronExpression: '',
      cronTimezone: '',
      fireAt: '',
      idleOnly: false,
      enabled: true,
      eventType: '',
      filter: '',
      ...folderDefaults,
      // An older row has no wake choice and reads as `always`; kept so, this patch is not about it.
      wakeManager: 'always',
      dailyTokenCap: null,
    })

    expect(patch).toEqual({
      name: 'Morning every',
      kind: 'every',
      expression: null,
      timezone: null,
      intervalSeconds: 600,
      idleOnly: false,
    })
  })

  it('PATCHes eventType and filter when an existing trigger is re-pointed at a new event', () => {
    const existing = {
      id: 's2',
      team: asTeamId('Alpha'),
      container: 'Manager',
      name: 'On failure',
      instruction: 'Look at it.',
      kind: 'event' as const,
      expression: null,
      timezone: null,
      intervalSeconds: null,
      fireAt: null,
      idleOnly: true,
      enabled: true,
      nextDueAt: null,
      lastFiredAt: null,
      lastOutcome: null,
      lastSeq: null,
      missedCount: 0,
      createdAt: '2026-08-25T20:00:00Z',
      createdBy: 'u1',
      eventType: 'agentContainer.failed',
      filter: null,
    }

    const patch = updateTriggerPatchFromDraft(existing, {
      ...draftFromTrigger(existing),
      filter: 'container eq alpha/dev1',
    })

    expect(patch).toEqual({ filter: 'container eq alpha/dev1' })
  })

  it('renders the next five cron occurrences for valid cron+timezone', () => {
    const preview = previewCronOccurrences(
      '0 */15 * * * *',
      'UTC',
      5,
      new Date('2026-08-25T20:02:00Z'),
    )

    expect(preview).toHaveLength(5)
    expect(preview[0]).toContain('UTC')
  })

  it('maps one-off wire dates to datetime-local input values', () => {
    expect(toDateTimeLocalValue('2026-08-25T20:05:00Z')).toMatch(/^2026-08-25T/)
    expect(toDateTimeLocalValue(null)).toBe('')
  })

  it('maps datetime-local values back to wire ISO and round-trips the same instant', () => {
    const wire = '2026-08-25T20:05:00Z'
    const local = toDateTimeLocalValue(wire)
    const roundTrip = fromDateTimeLocalValue(local)
    expect(roundTrip).not.toBeNull()
    expect(new Date(roundTrip ?? '').getTime()).toBe(new Date(wire).getTime())
  })

  it('creates draft defaults with Manager target by default', () => {
    expect(draftForCreate()).toEqual({
      name: '',
      container: 'Manager',
      instruction: '',
      mode: 'every',
      everyCount: 5,
      everyUnit: 'minutes',
      cronExpression: '0 */5 * * * *',
      cronTimezone: 'UTC',
      fireAt: '',
      startsAt: '',
      idleOnly: true,
      enabled: true,
      eventType: '',
      filter: '',
      ...folderDefaults,
      ...costDefaults,
    })
  })

  it('omits fireAt in PATCH when only the wire string format changed', () => {
    const existing = {
      id: 's1',
      team: asTeamId('Alpha'),
      container: 'Manager',
      name: 'One-off',
      instruction: 'Run once.',
      kind: 'once' as const,
      expression: null,
      timezone: null,
      intervalSeconds: null,
      fireAt: '2026-08-25T20:05:00+00:00',
      idleOnly: true,
      enabled: true,
      nextDueAt: null,
      lastFiredAt: null,
      lastOutcome: null,
      lastSeq: null,
      missedCount: 0,
      createdAt: '2026-08-25T20:00:00Z',
      createdBy: 'u1',
      eventType: null,
      filter: null,
    }

    const patch = updateTriggerPatchFromDraft(existing, draftFromTrigger(existing))
    expect('fireAt' in patch).toBe(false)
  })

  it('sends changed fireAt and explicit null when fireAt is cleared', () => {
    const existing = {
      id: 's1',
      team: asTeamId('Alpha'),
      container: 'Manager',
      name: 'One-off',
      instruction: 'Run once.',
      kind: 'once' as const,
      expression: null,
      timezone: null,
      intervalSeconds: null,
      fireAt: '2026-08-25T20:05:00Z',
      idleOnly: true,
      enabled: true,
      nextDueAt: null,
      lastFiredAt: null,
      lastOutcome: null,
      lastSeq: null,
      missedCount: 0,
      createdAt: '2026-08-25T20:00:00Z',
      createdBy: 'u1',
      eventType: null,
      filter: null,
    }

    const changedFireAt = updateTriggerPatchFromDraft(existing, {
      ...draftFromTrigger(existing),
      fireAt: toDateTimeLocalValue('2026-08-25T20:10:00Z'),
    })
    expect('fireAt' in changedFireAt).toBe(true)
    expect(changedFireAt.fireAt).toBe('2026-08-25T20:10:00.000Z')

    const clearedFireAt = updateTriggerPatchFromDraft(existing, {
      ...draftFromTrigger(existing),
      mode: 'every',
      everyCount: 5,
      everyUnit: 'minutes',
      fireAt: '',
    })
    expect('fireAt' in clearedFireAt).toBe(true)
    expect(clearedFireAt.fireAt).toBeNull()
  })
})

describe('triggerDraftProblem', () => {
  const ok = (over: Partial<TriggerDraft> = {}): TriggerDraft => ({
    ...draftForCreate('Manager'),
    name: 'TEST',
    instruction: 'Say hello!',
    mode: 'every',
    everyCount: 10,
    everyUnit: 'seconds',
    ...over,
  })

  it('RETURNS NULL for a complete every-N draft', () => {
    // A complete form: name, Manager, Every 10 seconds, an instruction. `validateSchedule` answers
    // NULL here, not '' - a dialog comparing against '' would keep Save disabled forever while the
    // warning banner, `v-if` on the same falsy null, showed nothing.
    // A dead button with no reason given.
    expect(triggerDraftProblem(ok())).toBeNull()
  })

  it('names the missing field rather than answering a bare false', () => {
    expect(triggerDraftProblem(ok({ name: '   ' }))).toMatch(/name/i)
    expect(triggerDraftProblem(ok({ instruction: '' }))).toMatch(/told/i)
    expect(triggerDraftProblem(ok({ container: '' }))).toMatch(/member/i)
  })

  it('carries the shape validation through for every mode', () => {
    expect(triggerDraftProblem(ok({ everyCount: 0 }))).toMatch(/1 or more/i)

    // An EMPTY number box yields NaN, and `secondsForEvery` clamps it to 1 - so unless it is
    // refused, clearing the box gives you a schedule firing every second, with Save enabled.
    expect(triggerDraftProblem(ok({ everyCount: Number.NaN }))).toMatch(/1 or more/i)
    expect(triggerDraftProblem(ok({ mode: 'once', fireAt: '' }))).toMatch(/one-off/i)
    expect(triggerDraftProblem(ok({ mode: 'cron', cronExpression: 'nonsense' }))).not.toBeNull()
  })

  it('accepts a valid cron draft', () => {
    expect(
      triggerDraftProblem(
        ok({ mode: 'cron', cronExpression: '*/5 * * * *', cronTimezone: 'UTC' }),
      ),
    ).toBeNull()
  })

  /**
   * THE WHOLE RULE IN ONE FUNCTION. Splitting it across a pure function and a component is still a
   * rule no test should have to reconstruct from rendering, even with mounted specs, and that split
   * can produce a permanently disabled Save button with no explanation, because `null === ''` is
   * false and the warning banner is falsy too.
   */
  it('asks for an event type', () => {
    const draft = { ...draftForCreate('Manager'), mode: 'event' as const, eventType: '' }
    expect(triggerDraftProblem(draft)).toBe('Choose which event fires this trigger.')
  })

  it('accepts a complete event draft', () => {
    const draft = {
      ...draftForCreate('Manager'),
      mode: 'event' as const,
      eventType: 'agentContainer.failed',
      instruction: 'look at it',
      name: 'on failure',
    }
    expect(triggerDraftProblem(draft)).toBeNull()
  })

  it('refuses a filter that does not read `field op value`', () => {
    const draft = {
      ...draftForCreate('Manager'),
      mode: 'event' as const,
      eventType: 'agentContainer.failed',
      instruction: 'look at it',
      name: 'on failure',
      filter: 'nonsense',
    }
    expect(triggerDraftProblem(draft)).toMatch(/field op value/i)
  })

  it('accepts a well-formed filter', () => {
    const draft = {
      ...draftForCreate('Manager'),
      mode: 'event' as const,
      eventType: 'agentContainer.failed',
      instruction: 'look at it',
      name: 'on failure',
      filter: 'container eq alpha/dev1',
    }
    expect(triggerDraftProblem(draft)).toBeNull()
  })

  it('answers the SAME contract validateSchedule does - a message or null, never an empty string', () => {
    // Two contracts meeting is the risk. A caller may compare against null OR use
    // truthiness and get the same answer; comparing against '' must never be a thing that works
    // by accident.
    const answers = [
      triggerDraftProblem(ok()),
      triggerDraftProblem(ok({ name: '' })),
    ]

    for (const answer of answers) {
      expect(answer === null || (typeof answer === 'string' && answer.length > 0)).toBe(true)
    }
  })
})

describe('instructionTypeFor', () => {
  it('matches the server\'s ContainerId.ToString() shape exactly - Team/Name, not team-name', () => {
    expect(instructionTypeFor('Alpha', 'Manager')).toBe('agentContainer.instruction.Alpha/Manager')
  })
})

describe('instructionWakeSource', () => {
  it('is always the instruction type, marked as an instruction row', () => {
    const source = instructionWakeSource('Alpha', 'Manager')
    expect(source.type).toBe('agentContainer.instruction.Alpha/Manager')
    expect(source.isInstruction).toBe(true)
    expect(source.label).toMatch(/tell/i)
  })
})

describe('builtInWakeSources', () => {
  const events = [
    { type: 'agentContainer.failed', summary: 'A member\'s run failed.' },
    { type: 'kanban.card.moved', summary: 'A card moved between columns.' },
  ]

  it('lists every subscribed type with no event trigger behind it, labelled from the catalog', () => {
    const subscribes = [
      'agentContainer.completed',
      'agentContainer.failed',
      'kanban.card.moved',
      'agentContainer.instruction.Alpha/Manager',
    ]

    const sources = builtInWakeSources(subscribes, [], 'Alpha', 'Manager', events)

    // The instruction type is excluded - it gets its own row from instructionWakeSource.
    expect(sources.map((s) => s.type)).toEqual([
      'agentContainer.completed',
      'agentContainer.failed',
      'kanban.card.moved',
    ])
    expect(sources.every((s) => s.isInstruction === false)).toBe(true)
  })

  // The row reads like the New trigger picker: the event name is the label, and the catalog's
  // long description is the summary under it.
  it('labels a row with the event name and carries the catalog description as its summary', () => {
    const sources = builtInWakeSources(['agentContainer.failed'], [], 'Alpha', 'Manager', events)
    expect(sources).toEqual([{ type: 'agentContainer.failed', label: 'agentContainer.failed', summary: 'A member\'s run failed', isInstruction: false }])
  })

  it('has no summary when the catalog has no entry for the type', () => {
    const sources = builtInWakeSources(['agentContainer.completed'], [], 'Alpha', 'Manager', [])
    expect(sources).toEqual([{ type: 'agentContainer.completed', label: 'agentContainer.completed', summary: null, isInstruction: false }])
  })

  it('excludes a type covered by an ENABLED event trigger row - appears once, as the trigger row, never twice', () => {
    const rows = [{ kind: 'event' as const, eventType: 'kanban.card.moved', enabled: true }]
    const subscribes = ['agentContainer.completed', 'kanban.card.moved']

    const sources = builtInWakeSources(subscribes, rows, 'Alpha', 'Manager', events)

    expect(sources.map((s) => s.type)).toEqual(['agentContainer.completed'])
  })

  it('a type with a DISABLED event trigger still appears as a built-in row', () => {
    // The bug this pins: `container.completed` is one of the six ManagerSubscriptions() types, so
    // it is in `subscribes` STRUCTURALLY - not because of the trigger. Disabling the trigger must
    // not make the type vanish from both the (now-disabled) trigger row AND the built-in list; the
    // built-in row is what tells a reader the member still wakes on it regardless.
    const rows = [{ kind: 'event' as const, eventType: 'agentContainer.completed', enabled: false }]
    const subscribes = ['agentContainer.completed', 'agentContainer.failed']

    const sources = builtInWakeSources(subscribes, rows, 'Alpha', 'Manager', events)

    expect(sources.map((s) => s.type)).toEqual(['agentContainer.completed', 'agentContainer.failed'])
  })

  it('ignores a schedule-kind row even if it somehow named an eventType', () => {
    // A cron/every/once trigger adds nothing to `subscribes`, so its eventType (if any leftover
    // value were present) must never be read as covering a type - only `kind === 'event'` counts.
    const rows = [{ kind: 'cron' as const, eventType: 'agentContainer.completed', enabled: true }]
    const sources = builtInWakeSources(['agentContainer.completed'], rows, 'Alpha', 'Manager', events)
    expect(sources.map((s) => s.type)).toEqual(['agentContainer.completed'])
  })
})

describe('triggerFieldProblems', () => {
  const ok = (over: Partial<TriggerDraft> = {}): TriggerDraft => ({
    ...draftForCreate('Manager'),
    name: 'TEST',
    instruction: 'Say hello!',
    ...over,
  })

  it('is empty for a draft triggerDraftProblem accepts', () => {
    expect(triggerFieldProblems(ok())).toEqual({})
    expect(triggerDraftProblem(ok())).toBeNull()
  })

  it('puts each problem on its own field, and triggerDraftProblem reports the first', () => {
    const draft = ok({ name: '', instruction: '', mode: 'cron', cronExpression: 'nope', cronTimezone: '' })
    const problems = triggerFieldProblems(draft)

    expect(Object.keys(problems).sort()).toEqual(['cronExpression', 'cronTimezone', 'instruction', 'name'])
    expect(triggerDraftProblem(draft)).toBe(problems.name)
  })

  it('judges only the fields the mode uses', () => {
    expect(triggerFieldProblems(ok({ mode: 'every', cronExpression: '' }))).toEqual({})
    expect(triggerFieldProblems(ok({ mode: 'every', everyCount: 0 })).everyCount).toMatch(/1 or more/)
    expect(triggerFieldProblems(ok({ mode: 'every', everyCount: 5, everyUnit: 'seconds' })).everyCount)
      .toMatch(/shortest interval/)
    expect(triggerFieldProblems(ok({ mode: 'once', fireAt: '' })).fireAt).toMatch(/when/)
    expect(triggerFieldProblems(ok({ mode: 'event', eventType: '' })).eventType).toMatch(/event/)
    expect(triggerFieldProblems(ok({ mode: 'event', eventType: 'x', filter: 'kind' })).filter).toMatch(/field op value/)
  })
})

describe('folder-change triggers', () => {
  const folderRow: TeamTrigger = {
    id: 'f1',
    team: asTeamId('Alpha'),
    container: 'Manager',
    name: 'Inbox',
    instruction: 'Sort the inbox.',
    kind: 'folderChange',
    expression: null,
    timezone: null,
    intervalSeconds: null,
    fireAt: null,
    idleOnly: true,
    enabled: true,
    nextDueAt: null,
    lastFiredAt: null,
    lastOutcome: null,
    lastSeq: null,
    missedCount: 0,
    createdAt: '2026-09-23T10:00:00Z',
    createdBy: 'u1',
    eventType: null,
    filter: null,
    watchRoot: 'root:Scans',
    watchPath: 'incoming',
    watchGlob: '*.pdf',
    pollSeconds: 120,
    quietSeconds: 45,
    minIntervalSeconds: 300,
    lastPollAt: null,
    lastPollMs: null,
    lastPollEntries: null,
    lastPollError: null,
    lastChangeAt: null,
  }

  const folderDraft = (over: Partial<TriggerDraft> = {}): TriggerDraft => ({
    ...draftForCreate('Manager'),
    name: 'Inbox',
    instruction: 'Sort the inbox.',
    mode: 'folderChange',
    watchPath: 'inbox',
    ...over,
  })

  it('reads the folder, and the glob when there is one, in the sentence', () => {
    expect(triggerSentence({ kind: 'folderChange', watchRoot: 'documents', watchPath: 'inbox' }))
      .toBe('when files change in documents/inbox')
    expect(triggerSentence({ kind: 'FolderChange', watchRoot: 'root:Scans', watchPath: '/incoming/', watchGlob: '*.pdf' }))
      .toBe('when files change in Scans/incoming matching *.pdf')
    expect(triggerSentence({ kind: 'folderChange', watchRoot: 'documents', watchPath: '' }))
      .toBe('when files change in documents')
  })

  it('offers what the server offered, and documents even when it offered nothing', () => {
    const offered = [
      { value: 'documents', label: 'Team documents' },
      { value: 'root:Scans', label: 'Scans' },
    ]

    expect(watchRootOptions(offered)).toEqual(offered)
    expect(watchRootOptions([])).toEqual([{ label: 'Team documents', value: 'documents' }])
  })

  it('keeps a row\'s root that is no longer watchable, marked, rather than re-pointing it', () => {
    const options = watchRootOptions([{ value: 'documents', label: 'Team documents' }], 'root:Scans')

    expect(options.map((option) => option.value)).toEqual(['documents', 'root:Scans'])
    expect(options[1]!.label).toMatch(/not watchable/)
  })

  it('accepts a folder draft at the defaults', () => {
    expect(triggerFieldProblems(folderDraft())).toEqual({})
  })

  it('refuses a poll interval below 15 seconds, and accepts 15', () => {
    expect(triggerFieldProblems(folderDraft({ pollSeconds: 14 })).pollSeconds).toMatch(/15/)
    expect(triggerFieldProblems(folderDraft({ pollSeconds: Number.NaN })).pollSeconds).toBeDefined()
    expect(triggerFieldProblems(folderDraft({ pollSeconds: 15 })).pollSeconds).toBeUndefined()
  })

  it('refuses a negative quiet period or minimum interval', () => {
    expect(triggerFieldProblems(folderDraft({ quietSeconds: -1 })).quietSeconds).toBeDefined()
    expect(triggerFieldProblems(folderDraft({ minIntervalSeconds: 1.5 })).minIntervalSeconds).toBeDefined()
    expect(triggerFieldProblems(folderDraft({ quietSeconds: 0, minIntervalSeconds: 0 }))).toEqual({})
  })

  it('refuses an absolute path or a climb out of the root, before asking the server', () => {
    expect(triggerFieldProblems(folderDraft({ watchPath: '/etc' })).watchPath).toMatch(/relative/)
    expect(triggerFieldProblems(folderDraft({ watchPath: 'C:\\Users' })).watchPath).toMatch(/relative/)
    expect(triggerFieldProblems(folderDraft({ watchPath: 'inbox/../../x' })).watchPath).toMatch(/\.\./)
    expect(triggerFieldProblems(folderDraft({ watchPath: '' })).watchPath).toBeUndefined()
  })

  it('does not judge folder fields on another kind', () => {
    expect(triggerFieldProblems(folderDraft({ mode: 'every', pollSeconds: 1, watchPath: '/x' }))).toEqual({})
  })

  it('sends the folder fields for a folder trigger', () => {
    expect(createTriggerRequestFromDraft(folderDraft({ watchGlob: ' *.pdf ' }))).toEqual({
      name: 'Inbox',
      container: 'Manager',
      instruction: 'Sort the inbox.',
      kind: 'folderChange',
      expression: null,
      timezone: null,
      intervalSeconds: null,
      fireAt: null,
      idleOnly: true,
      enabled: true,
      eventType: null,
      filter: null,
      watchRoot: 'documents',
      watchPath: 'inbox',
      watchGlob: '*.pdf',
      pollSeconds: 60,
      quietSeconds: 30,
      minIntervalSeconds: 60,
      wakeManager: 'onHandbackOrFailure',
      dailyTokenCap: null,
    })
  })

  it('sends a blank glob as null', () => {
    expect(createTriggerRequestFromDraft(folderDraft()).watchGlob).toBeNull()
  })

  it('reads a folder row back into a draft', () => {
    const draft = draftFromTrigger(folderRow)

    expect(draft).toMatchObject({
      mode: 'folderChange',
      watchRoot: 'root:Scans',
      watchPath: 'incoming',
      watchGlob: '*.pdf',
      pollSeconds: 120,
      quietSeconds: 45,
      minIntervalSeconds: 300,
    })
  })

  it('saves a folder row opened and left unchanged as an empty patch, never clearing its event type', () => {
    const row = { ...folderRow, eventType: 'file.changed' }

    expect(updateTriggerPatchFromDraft(row, draftFromTrigger(row))).toEqual({})
  })

  it('patches only the folder fields that changed', () => {
    const patch = updateTriggerPatchFromDraft(folderRow, {
      ...draftFromTrigger(folderRow),
      pollSeconds: 30,
      watchGlob: '',
    })

    expect(patch).toEqual({ pollSeconds: 30, watchGlob: null })
  })

  it('says when it last polled, how long that took, and when it last fired', () => {
    const now = new Date('2026-09-23T12:00:00Z')

    expect(folderPollSummary({ lastPollAt: null, lastPollMs: null, lastPollEntries: null, lastChangeAt: null }, now))
      .toBe('not polled yet • never fired')
    expect(folderPollSummary({
      lastPollAt: '2026-09-23T11:59:00Z',
      lastPollMs: 2400,
      lastPollEntries: 3,
      lastChangeAt: '2026-09-23T10:00:00Z',
    }, now)).toBe('last poll 1 minute ago, took 2.4 s, 3 files • last fired 2 hours ago')
  })

  it('formats a listing duration in ms below a second and seconds above', () => {
    expect(formatPollDuration(340)).toBe('340 ms')
    expect(formatPollDuration(2400)).toBe('2.4 s')
    expect(formatPollDuration(12_600)).toBe('13 s')
    expect(formatPollDuration(null)).toBe('?')
  })
})

describe('trigger cost control', () => {
  const now = new Date('2026-09-28T08:00:00Z')

  it('reads a row with no wake choice as always, which is what it did before the setting', () => {
    expect(wakeManagerOf({})).toBe('always')
    expect(wakeManagerOf({ wakeManager: 'never' })).toBe('never')
    expect(draftForCreate().wakeManager).toBe('onHandbackOrFailure')
    expect(draftForCreate().dailyTokenCap).toBeNull()
  })

  it('refuses a cap that is not a whole number of 1 or more, and takes no cap', () => {
    const draft = { ...draftForCreate('Dev'), name: 'Sweep', instruction: 'Sweep.' }
    expect(triggerFieldProblems({ ...draft, dailyTokenCap: null }).dailyTokenCap).toBeUndefined()
    expect(triggerFieldProblems({ ...draft, dailyTokenCap: 1 }).dailyTokenCap).toBeUndefined()
    expect(triggerFieldProblems({ ...draft, dailyTokenCap: 0 }).dailyTokenCap).toBeDefined()
    expect(triggerFieldProblems({ ...draft, dailyTokenCap: 2.5 }).dailyTokenCap).toBeDefined()
    expect(triggerFieldProblems({ ...draft, dailyTokenCap: NaN }).dailyTokenCap).toBeDefined()
  })

  it('measures a cron schedule by its shortest gap, and a one-off or event not at all', () => {
    const base = draftForCreate('Dev')
    expect(shortestScheduleGapSeconds({ ...base, mode: 'cron', cronExpression: '0 * * * * *' }, now)).toBe(60)
    expect(shortestScheduleGapSeconds({ ...base, mode: 'cron', cronExpression: '0 */5 * * * *' }, now)).toBe(300)
    expect(shortestScheduleGapSeconds({ ...base, mode: 'every', everyCount: 90, everyUnit: 'seconds' }, now)).toBe(90)
    expect(shortestScheduleGapSeconds({ ...base, mode: 'event', eventType: 'x.y' }, now)).toBeNull()
  })

  it('asks for confirmation below 5 minutes on an agent, and never on a plugin', () => {
    const everyMinute = { ...draftForCreate('Dev'), everyCount: 1, everyUnit: 'minutes' as const }
    const everyFive = { ...draftForCreate('Dev'), everyCount: 5, everyUnit: 'minutes' as const }
    expect(needsShortScheduleConfirmation(everyMinute, 'agent', now)).toBe(true)
    expect(needsShortScheduleConfirmation(everyFive, 'agent', now)).toBe(false)
    expect(needsShortScheduleConfirmation(everyMinute, 'plugin', now)).toBe(false)
    expect(needsShortScheduleConfirmation(
      { ...everyMinute, mode: 'cron', cronExpression: '0 * * * * *' }, 'agent', now,
    )).toBe(true)
  })

  it('says a member whose runs reported nothing has no runs measured, not a zero', () => {
    expect(measuredCostLine({
      lastRuns: 3, measuredRuns: 0, unmeasuredRuns: 3, medianBillableTokens: null, kind: 'agent',
    })).toBe('No runs measured yet - its last 3 runs reported no usage.')
    expect(measuredCostLine({
      lastRuns: 1, measuredRuns: 1, unmeasuredRuns: 0, medianBillableTokens: 900, kind: 'agent',
    })).toBe('Median 900 billable tokens per run, over its last 1 run (1 measured).')
  })

  it('says spent today without a cap, and not known when the Host sends no spend', () => {
    expect(spentTodayLine({ spentToday: { billableTokens: 500, measuredRuns: 1, unmeasuredRuns: 0 }, dailyTokenCap: null }))
      .toBe('spent today 500 tokens (no cap)')
    expect(spentTodayLine({ dailyTokenCap: 800 })).toBe('spent today not known / cap 800 tokens')
    expect(spentTodayLine({})).toBeNull()
  })

  it('names a fire skipped at the cap', () => {
    expect(outcomeBadge('capped')).toEqual({ label: 'skipped (daily cap)', color: 'warning' })
  })
})
