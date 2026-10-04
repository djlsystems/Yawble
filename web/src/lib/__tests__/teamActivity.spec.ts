import { describe, expect, it } from 'vitest'
import type { ActivityMember, ActivitySpan, ActivityState, TeamActivity } from '../../api/types'
import {
  ActivityStates,
  type Column,
  activityCaption,
  activityLanes,
  activitySummary,
  bucketActivity,
  bucketLabel,
  columnTooltipHtml,
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

  it('gives a removed member a lane marked removed when asked to, after the current ones', () => {
    const lanes = activityLanes(
      activity([
        member('Manager'),
        member('Gone Member', [{ state: 'running', from: iso(at(14, 3)), to: iso(at(14, 4)) }], { current: false }),
        member('Tester'),
      ]),
      board,
      at(14, 30),
      { removed: true },
    )

    expect(lanes.map((lane) => [lane.member, lane.name, lane.initials])).toEqual([
      ['Manager', 'Manager', 'M'],
      ['Tester', 'Tester Okon', 'TO'],
      ['Gone Member', 'Gone Member (removed)', 'GM'],
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
      .toBe('Statistics since 14:20: 3 members - 2 running, 1 blocked')
  })

  it('says how many have no span now', () => {
    const lanes = activityLanes(activity([
      member('Manager', [{ state: 'idle', from: iso(at(14, 2)), to: null }]),
      member('Ines', [{ state: 'running', from: iso(at(14, 5)), to: iso(at(14, 6)) }]),
    ]), [], now)

    expect(activitySummary('open', lanes, at(14, 2), now))
      .toBe('Statistics since 14:02: 2 members - 1 idle, 1 with no runs now')
  })

  it('says one member in the singular', () => {
    const lanes = activityLanes(activity([
      member('Manager', [{ state: 'failed', from: iso(at(14, 2)), to: null }]),
    ]), [], now)

    expect(activitySummary('open', lanes, at(14, 2), now))
      .toBe('Statistics since 14:02: 1 member - 1 failed')
  })

  it('names no start when the window has none', () => {
    const lanes = activityLanes(activity([
      member('Manager', [{ state: 'running', from: iso(at(14, 2)), to: null }]),
    ]), [], now)

    expect(activitySummary('open', lanes, null, now)).toBe('Statistics: 1 member - 1 running')
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

describe('bucketing member-time', () => {
  const utc = (text: string) => Date.parse(text)
  const minute = 60_000
  const hour = 60 * minute
  const none = { running: 0, waiting: 0, blocked: 0, failed: 0, idle: 0 }

  function lane(name: string, spans: [ActivityState, string, string][]) {
    return { member: name, spans: spans.map(([state, from, to]) => ({ state, from: utc(from), to: utc(to), open: false })) }
  }

  const edges = (columns: Column[]) => columns.map((c) => [new Date(c.from).toISOString(), new Date(c.to).toISOString()])

  it('splits a span at local minute edges, exactly', () => {
    const columns = bucketActivity(
      [lane('Ines', [['running', '2026-10-04T12:00:30Z', '2026-10-04T12:02:15Z']])], 'minute', 'Europe/Berlin')

    expect(edges(columns)).toEqual([
      ['2026-10-04T12:00:00.000Z', '2026-10-04T12:01:00.000Z'],
      ['2026-10-04T12:01:00.000Z', '2026-10-04T12:02:00.000Z'],
      ['2026-10-04T12:02:00.000Z', '2026-10-04T12:03:00.000Z'],
    ])
    expect(columns.map((c) => c.totals.running)).toEqual([30_000, 60_000, 15_000])
  })

  it('puts hour edges on the local hour of a half-hour zone, not the UTC hour', () => {
    // 10:00Z-11:00Z is 15:30-16:30 in Kolkata: half in the 15:00 hour, half in the 16:00 hour.
    const columns = bucketActivity(
      [lane('Ines', [['blocked', '2026-10-04T10:00:00Z', '2026-10-04T11:00:00Z']])], 'hour', 'Asia/Kolkata')

    expect(edges(columns)).toEqual([
      ['2026-10-04T09:30:00.000Z', '2026-10-04T10:30:00.000Z'],
      ['2026-10-04T10:30:00.000Z', '2026-10-04T11:30:00.000Z'],
    ])
    expect(columns.map((c) => c.totals.blocked)).toEqual([30 * minute, 30 * minute])
  })

  it('puts day edges on local midnight', () => {
    // 23:00-02:00 Berlin summer time: one hour on the 4th, two on the 5th.
    const columns = bucketActivity(
      [lane('Ines', [['waiting', '2026-10-04T21:00:00Z', '2026-10-05T00:00:00Z']])], 'day', 'Europe/Berlin')

    expect(edges(columns)).toEqual([
      ['2026-10-03T22:00:00.000Z', '2026-10-04T22:00:00.000Z'],
      ['2026-10-04T22:00:00.000Z', '2026-10-05T22:00:00.000Z'],
    ])
    expect(columns.map((c) => c.totals.waiting)).toEqual([hour, 2 * hour])
  })

  it('puts month edges on local midnight of the first', () => {
    const columns = bucketActivity(
      [lane('Ines', [['failed', '2026-09-30T21:00:00Z', '2026-09-30T23:00:00Z']])], 'month', 'Europe/Berlin')

    expect(edges(columns)).toEqual([
      ['2026-08-31T22:00:00.000Z', '2026-09-30T22:00:00.000Z'],
      ['2026-09-30T22:00:00.000Z', '2026-10-31T23:00:00.000Z'],
    ])
    expect(columns.map((c) => c.totals.failed)).toEqual([hour, hour])
  })

  it('gives the day clocks go back 25 hours and the day they go forward 23', () => {
    const autumn = bucketActivity(
      [lane('Ines', [['running', '2026-10-24T22:00:00Z', '2026-10-25T23:00:00Z']])], 'day', 'Europe/Berlin')
    const spring = bucketActivity(
      [lane('Ines', [['running', '2026-03-28T23:00:00Z', '2026-03-29T22:00:00Z']])], 'day', 'Europe/Berlin')

    expect(autumn).toHaveLength(1)
    expect(autumn[0]!.to - autumn[0]!.from).toBe(25 * hour)
    expect(autumn[0]!.totals.running).toBe(25 * hour)
    expect(spring).toHaveLength(1)
    expect(spring[0]!.to - spring[0]!.from).toBe(23 * hour)
    expect(spring[0]!.totals.running).toBe(23 * hour)
  })

  it('gives the hour that happens twice when clocks go back two columns of an hour each', () => {
    // 00:00Z-02:00Z on 25 Oct is 02:00 summer time, then 02:00 winter time, in Berlin.
    const columns = bucketActivity(
      [lane('Ines', [['idle', '2026-10-25T00:00:00Z', '2026-10-25T02:00:00Z']])], 'hour', 'Europe/Berlin')

    expect(edges(columns)).toEqual([
      ['2026-10-25T00:00:00.000Z', '2026-10-25T01:00:00.000Z'],
      ['2026-10-25T01:00:00.000Z', '2026-10-25T02:00:00.000Z'],
    ])
    expect(columns.map((c) => c.totals.idle)).toEqual([hour, hour])
  })

  it('counts an open span up to now and no further', () => {
    const now = utc('2026-10-04T12:01:40Z')
    const spans = growSpans([{ state: 'running', from: '2026-10-04T12:00:20Z', to: null }], now)

    const columns = bucketActivity([{ member: 'Ines', spans }], 'minute', 'Europe/Berlin')

    expect(columns.map((c) => c.totals.running)).toEqual([40_000, 40_000])
  })

  it('makes no column where nothing was recorded, and never counts the gap as idle', () => {
    const columns = bucketActivity([lane('Ines', [
      ['running', '2026-10-04T12:00:00Z', '2026-10-04T12:01:00Z'],
      ['running', '2026-10-04T12:05:00Z', '2026-10-04T12:06:00Z'],
    ])], 'minute', 'Europe/Berlin')

    expect(columns).toHaveLength(2)
    expect(columns.map((c) => c.totals)).toEqual([{ ...none, running: minute }, { ...none, running: minute }])
  })

  it('splits each column per member, and the members add up to the column', () => {
    const columns = bucketActivity([
      lane('Manager', [
        ['running', '2026-10-04T12:00:00Z', '2026-10-04T12:00:40Z'],
        ['idle', '2026-10-04T12:00:40Z', '2026-10-04T12:02:00Z'],
      ]),
      lane('Ines', [
        ['running', '2026-10-04T12:00:10Z', '2026-10-04T12:00:50Z'],
        ['blocked', '2026-10-04T12:00:50Z', '2026-10-04T12:01:30Z'],
      ]),
    ], 'minute', 'Europe/Berlin')

    expect(columns[0]!.members).toEqual([
      { member: 'Manager', ms: { ...none, running: 40_000, idle: 20_000 } },
      { member: 'Ines', ms: { ...none, running: 40_000, blocked: 10_000 } },
    ])
    expect(columns[1]!.members).toEqual([
      { member: 'Manager', ms: { ...none, idle: minute } },
      { member: 'Ines', ms: { ...none, blocked: 30_000 } },
    ])

    for (const column of columns) {
      for (const state of ActivityStates) {
        expect(column.members.reduce((sum, m) => sum + m.ms[state], 0)).toBe(column.totals[state])
      }
    }
  })
})

describe('a column in words', () => {
  const zone = 'Europe/Berlin'
  // Tue 6 Oct 2026, 14:20 in Berlin.
  const start = Date.parse('2026-10-06T12:20:00Z')

  it('labels each bucket size in the given zone', () => {
    expect(bucketLabel(start, start + 60_000, 'minute', zone)).toBe('Tue 14:20–14:21')
    expect(bucketLabel(Date.parse('2026-10-06T12:00:00Z'), Date.parse('2026-10-06T13:00:00Z'), 'hour', zone))
      .toBe('Tue 14:00–15:00')
    expect(bucketLabel(Date.parse('2026-10-05T22:00:00Z'), Date.parse('2026-10-06T22:00:00Z'), 'day', zone))
      .toBe('Tue 6 Oct')
    expect(bucketLabel(Date.parse('2026-09-30T22:00:00Z'), Date.parse('2026-10-31T23:00:00Z'), 'month', zone))
      .toBe('Oct 2026')
  })

  it('gives the bucket, each state\'s total, then each member with initials, escaped', () => {
    const column: Column = {
      from: start,
      to: start + 60_000,
      totals: { running: 40_000, waiting: 0, blocked: 50_000, failed: 0, idle: 0 },
      members: [
        { member: 'Ines', ms: { running: 40_000, waiting: 0, blocked: 20_000, failed: 0, idle: 0 } },
        { member: 'Bold', ms: { running: 0, waiting: 0, blocked: 30_000, failed: 0, idle: 0 } },
      ],
    }
    const people = new Map([
      ['Ines', { name: 'Ines Lopez', initials: 'IL' }],
      ['Bold', { name: '<b>Bold</b> (removed)', initials: 'BB' }],
    ])

    const html = columnTooltipHtml(column, 'minute', zone, people)
    const text = html.replace(/<[^>]+>/g, '')

    expect(text).toContain('Tue 14:20–14:21')
    expect(text).toContain('running 40 s')
    expect(text).toContain('blocked 50 s')
    expect(text).not.toContain('waiting')
    expect(text).toContain('IL Ines Lopez — running 40 s, blocked 20 s')
    expect(html).not.toContain('<b>')
    expect(text).toContain('&lt;b&gt;Bold&lt;/b&gt; (removed) — blocked 30 s')
    expect(html).not.toContain('style=')
  })
})
