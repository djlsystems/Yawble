import { describe, expect, it } from 'vitest'
import type { ActivityMember, ActivitySpan, TeamActivity } from '../../api/types'
import {
  activityCaption,
  activityLanes,
  activitySummary,
  clockTime,
  escapeHtml,
  growSpans,
  laneInitials,
  liveNow,
  memberInitials,
  tooltipHtml,
} from '../teamActivity'

/** A local wall-clock instant, so the HH:MM a person reads is the one written here. */
const at = (hours: number, minutes: number, seconds = 0) =>
  new Date(2026, 9, 4, hours, minutes, seconds).getTime()

const iso = (ms: number) => new Date(ms).toISOString()

function member(name: string, spans: ActivitySpan[] = [], over: Partial<ActivityMember> = {}): ActivityMember {
  return { member: name, kind: 'agent', isManager: name === 'Manager', current: true, spans, ...over }
}

function activity(members: ActivityMember[], over: Partial<TeamActivity> = {}): TeamActivity {
  return {
    from: iso(at(14, 2)),
    to: iso(at(14, 30)),
    serverNow: iso(at(14, 30)),
    window: 'open',
    members,
    ...over,
  }
}

describe('initials before each lane', () => {
  it('takes the first and last initial of a two-word name', () => {
    expect(memberInitials('Ines Lopez')).toBe('IL')
    expect(memberInitials('Developer Ines')).toBe('DI')
  })

  it('takes the first and LAST initial of a longer name, not the second', () => {
    expect(memberInitials('Ana Maria Lopez')).toBe('AL')
  })

  it('takes one uppercase letter of a one-word name', () => {
    expect(memberInitials('Manager')).toBe('M')
    expect(memberInitials('rhea')).toBe('R')
  })

  it('splits on hyphens, underscores and dots as on spaces', () => {
    expect(memberInitials('test-runner')).toBe('TR')
    expect(memberInitials('build_bot')).toBe('BB')
    expect(memberInitials('j.doe')).toBe('JD')
    expect(memberInitials('  spaced -- out__name ')).toBe('SN')
  })

  it('numbers colliding initials in board order and leaves the rest alone', () => {
    expect(laneInitials(['Manager', 'Developer', 'Designer', 'Ines Lopez', 'Dora']))
      .toEqual(['M', 'D1', 'D2', 'IL', 'D3'])
  })

  it('numbers a collision between a one-word and a two-word name only when the letters agree', () => {
    expect(laneInitials(['Ines Lopez', 'ines_lopez', 'Ivan'])).toEqual(['IL1', 'IL2', 'I'])
  })
})

describe('open spans grow to now', () => {
  it('is the server clock advanced by the board tick and corrected by the offset', () => {
    const server = at(14, 30)
    // The browser is ten minutes slow: offset +10 min. Its clock has ticked 45 s since the answer.
    const offset = 10 * 60_000
    const browser = server - offset + 45_000

    expect(liveNow(iso(server), browser, offset)).toBe(server + 45_000)
  })

  it('never moves now before the answer\'s own serverNow', () => {
    const server = at(14, 30)

    expect(liveNow(iso(server), server - 60_000, 0)).toBe(server)
  })

  it('draws an open span to now and keeps a closed span where it ended', () => {
    const now = at(14, 31)
    const spans = growSpans([
      { state: 'running', from: iso(at(14, 2)), to: iso(at(14, 10)), workflow: 7 },
      { state: 'blocked', from: iso(at(14, 20)), to: null, workflow: 7, reason: 'needs the API key' },
    ], now)

    expect(spans).toEqual([
      { state: 'running', from: at(14, 2), to: at(14, 10), open: false, workflow: 7 },
      { state: 'blocked', from: at(14, 20), to: now, open: true, workflow: 7, reason: 'needs the API key' },
    ])
  })
})

describe('one lane per current member, Manager first, then board order', () => {
  const board = [
    { id: 'Tester', name: 'Tester Okon' },
    { id: 'DeveloperInes', name: 'Developer Ines' },
    { id: 'Manager', name: 'Manager' },
  ]

  it('puts the Manager first and the rest as the board lists them, by label', () => {
    const lanes = activityLanes(
      activity([member('Manager'), member('DeveloperInes'), member('Tester')]),
      board,
      at(14, 30),
    )

    expect(lanes.map((lane) => [lane.member, lane.name, lane.initials])).toEqual([
      ['Manager', 'Manager', 'M'],
      ['Tester', 'Tester Okon', 'TO'],
      ['DeveloperInes', 'Developer Ines', 'DI'],
    ])
  })

  it('gives a removed member no lane', () => {
    const lanes = activityLanes(
      activity([
        member('Manager'),
        member('Tester'),
        member('Gone Member', [{ state: 'running', from: iso(at(14, 3)), to: iso(at(14, 4)) }], { current: false }),
      ]),
      board,
      at(14, 30),
    )

    expect(lanes.map((lane) => lane.member)).toEqual(['Manager', 'Tester'])
  })
})

describe('the aria summary', () => {
  const now = at(14, 30)

  it('counts each member\'s state now, since the window began', () => {
    const lanes = activityLanes(activity([
      member('Manager', [{ state: 'running', from: iso(at(14, 2)), to: null }]),
      member('Ines', [{ state: 'running', from: iso(at(14, 5)), to: null }]),
      member('Okon', [
        { state: 'running', from: iso(at(14, 5)), to: iso(at(14, 20)) },
        { state: 'blocked', from: iso(at(14, 20)), to: null, reason: 'needs the API key' },
      ]),
    ], { from: iso(at(14, 20)) }), [], now)

    expect(activitySummary('open', lanes, at(14, 20), now))
      .toBe('Statistics: 3 members — 2 running, 1 blocked since 14:20')
  })

  it('says how many have no span now', () => {
    const lanes = activityLanes(activity([
      member('Manager', [{ state: 'idle', from: iso(at(14, 2)), to: null }]),
      member('Ines', [{ state: 'running', from: iso(at(14, 5)), to: iso(at(14, 6)) }]),
    ]), [], now)

    expect(activitySummary('open', lanes, at(14, 2), now))
      .toBe('Statistics: 2 members — 1 idle, 1 with no runs now since 14:02')
  })

  it('says one member in the singular', () => {
    const lanes = activityLanes(activity([
      member('Manager', [{ state: 'failed', from: iso(at(14, 2)), to: null }]),
    ]), [], now)

    expect(activitySummary('open', lanes, at(14, 2), now))
      .toBe('Statistics: 1 member — 1 failed since 14:02')
  })

  it('says there are no workflows yet when the team never ran', () => {
    expect(activitySummary('none', [], null, now)).toBe('Statistics: No workflows yet')
  })
})

describe('the caption under the lanes', () => {
  it('names the window start and the open workflows', () => {
    expect(activityCaption('open', at(14, 2), 2, null)).toBe('since 14:02 · 2 open workflows')
    expect(activityCaption('open', at(14, 2), 1, null)).toBe('since 14:02 · 1 open workflow')
  })

  it('names only the start while the workflow list has not landed', () => {
    expect(activityCaption('open', at(14, 2), null, null)).toBe('since 14:02')
  })

  it('names when the latest workflow closed for the fallback', () => {
    expect(activityCaption('latest', at(9, 0), 0, at(9, 15))).toBe('latest workflow, closed 09:15')
  })

  it('reads No workflows yet when there is none', () => {
    expect(activityCaption('none', null, 0, null)).toBe('No workflows yet')
  })
})

describe('the tooltip', () => {
  const now = at(14, 32)

  const lanes = () => activityLanes(activity([
    member('Manager', [{ state: 'running', from: iso(at(14, 2)), to: iso(at(14, 10)) }]),
    member('Ines Lopez', [{ state: 'blocked', from: iso(at(14, 20)), to: null, reason: 'needs the API key' }]),
  ]), [], now)

  it('gives the time under the cursor, then one line per member with state and how long it has lasted', () => {
    const html = tooltipHtml(lanes(), at(14, 25))
    const text = html.replace(/<[^>]+>/g, '')

    expect(text).toContain(clockTime(at(14, 25)))
    expect(text).toContain('M Manager — no runs in this window')
    expect(text).toContain('IL Ines Lopez — blocked 12 min (needs the API key)')
    expect(html).toContain('stats-chip--blocked')
  })

  it('escapes every name and reason, so markup reads as characters', () => {
    const html = tooltipHtml(activityLanes(activity([
      member('<b>Bold</b>', [{ state: 'failed', from: iso(at(14, 2)), to: null, reason: '<img src=x onerror=alert(1)>' }]),
    ]), [], now), at(14, 3))

    expect(html).not.toContain('<b>')
    expect(html).not.toContain('<img')
    expect(html).toContain('&lt;b&gt;Bold&lt;/b&gt;')
    expect(html).toContain('&lt;img src=x onerror=alert(1)&gt;')
  })

  it('escapes the five characters markup reads', () => {
    expect(escapeHtml(`<a href="x">'&'</a>`)).toBe('&lt;a href=&quot;x&quot;&gt;&#39;&amp;&#39;&lt;/a&gt;')
  })
})
