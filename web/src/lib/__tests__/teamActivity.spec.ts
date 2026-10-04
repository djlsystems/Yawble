import { describe, expect, it } from 'vitest'
import type { ActivityMember, ActivitySpan, ActivityState, TeamActivity, TeamTokenRun } from '../../api/types'
import {
  ActivityStates,
  type Column,
  type FigureColumn,
  type InstantFigure,
  activityCaption,
  axisTimeLabel,
  bucketFigures,
  figureTooltipHtml,
  runFigure,
  tooltipBeside,
  windowBucket,
  activityLanes,
  activitySummary,
  bucketActivity,
  bucketLabel,
  columnTooltipHtml,
  escapeHtml,
  growSpans,
  laneInitials,
  memberInitials,
  memberTime,
  tooltipHtml,
} from '../teamActivity'
import { crossesDays, localStretch, localTime } from '../localTime'

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
    window: 'workflows',
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

describe('spans reach the window\'s end and never the clock', () => {
  it('draws an open span to the window\'s end and keeps a closed span where it ended', () => {
    const end = at(14, 31)
    const spans = growSpans([
      { state: 'running', from: iso(at(14, 2)), to: iso(at(14, 10)), workflow: 7 },
      { state: 'blocked', from: iso(at(14, 20)), to: null, workflow: 7, reason: 'needs the API key' },
    ], end)

    expect(spans).toEqual([
      { state: 'running', from: at(14, 2), to: at(14, 10), open: false, workflow: 7 },
      { state: 'blocked', from: at(14, 20), to: end, open: true, workflow: 7, reason: 'needs the API key' },
    ])
  })

  it('reads a span the server ended at the window\'s end as still in its state there', () => {
    const end = at(14, 31)
    const [span] = growSpans([{ state: 'idle', from: iso(at(14, 20)), to: iso(end) }], end)

    expect(span).toEqual({ state: 'idle', from: at(14, 20), to: end, open: true })
  })

  it('draws the lanes to the answer\'s own end, however long ago that was', () => {
    // The answer's window ended at 14:30; the board has ticked on for an hour since.
    const answer = activity([member('Manager', [{ state: 'idle', from: iso(at(14, 10)), to: iso(at(14, 30)) }])])
    const lanes = activityLanes(answer, [], Date.parse(answer.to!))

    expect(lanes[0]!.spans.map((span) => span.to)).toEqual([at(14, 30)])
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
  const end = at(14, 30)

  it('names the window, then counts each member\'s state at its end', () => {
    const lanes = activityLanes(activity([
      member('Manager', [{ state: 'running', from: iso(at(14, 2)), to: iso(end) }]),
      member('Ines', [{ state: 'running', from: iso(at(14, 5)), to: null }]),
      member('Okon', [
        { state: 'running', from: iso(at(14, 5)), to: iso(at(14, 20)) },
        { state: 'blocked', from: iso(at(14, 20)), to: iso(end), reason: 'needs the API key' },
      ]),
    ], { from: iso(at(14, 20)) }), [], end)

    expect(activitySummary('workflows', lanes, at(14, 20), end))
      .toBe(`Statistics ${localStretch(at(14, 20), end)}: 3 members - 2 running, 1 blocked`)
  })

  it('says how many have no span at the end', () => {
    const lanes = activityLanes(activity([
      member('Manager', [{ state: 'idle', from: iso(at(14, 2)), to: iso(end) }]),
      member('Ines', [{ state: 'running', from: iso(at(14, 5)), to: iso(at(14, 6)) }]),
    ]), [], end)

    expect(activitySummary('workflows', lanes, at(14, 2), end))
      .toBe(`Statistics ${localStretch(at(14, 2), end)}: 2 members - 1 idle, 1 with no runs at the end`)
  })

  it('says one member in the singular', () => {
    const lanes = activityLanes(activity([
      member('Manager', [{ state: 'failed', from: iso(at(14, 2)), to: null }]),
    ]), [], end)

    expect(activitySummary('workflows', lanes, at(14, 2), end))
      .toBe(`Statistics ${localStretch(at(14, 2), end)}: 1 member - 1 failed`)
  })

  it('names no window when the answer has none', () => {
    const lanes = activityLanes(activity([
      member('Manager', [{ state: 'running', from: iso(at(14, 2)), to: null }]),
    ]), [], end)

    expect(activitySummary('workflows', lanes, null, end)).toBe('Statistics: 1 member - 1 running')
  })

  it('says there are no workflows yet when the team never ran', () => {
    expect(activitySummary('none', [], null, end)).toBe('Statistics: No workflows yet')
  })
})

describe('the caption under the lanes', () => {
  it('names the window\'s stretch and the open workflows', () => {
    expect(activityCaption('workflows', at(11, 38, 2), at(15, 41, 17), 2))
      .toBe(`${localTime(at(11, 38, 2))} – ${localTime(at(15, 41, 17))} · 2 open workflows`)
    expect(activityCaption('workflows', at(11, 38, 2), at(15, 41, 17), 1))
      .toBe(`${localTime(at(11, 38, 2))} – ${localTime(at(15, 41, 17))} · 1 open workflow`)
  })

  it('names only the stretch while the workflow list has not landed, or when nothing is open', () => {
    expect(activityCaption('workflows', at(11, 38, 2), at(15, 41, 17), null))
      .toBe(`${localTime(at(11, 38, 2))} – ${localTime(at(15, 41, 17))}`)
    expect(activityCaption('workflows', at(11, 38, 2), at(15, 41, 17), 0))
      .toBe(`${localTime(at(11, 38, 2))} – ${localTime(at(15, 41, 17))}`)
  })

  it('reads No workflows yet when there is none', () => {
    expect(activityCaption('none', null, null, 0)).toBe('No workflows yet')
  })
})

describe('the tooltip', () => {
  const now = at(14, 32)

  const lanes = () => activityLanes(activity([
    member('Manager', [{ state: 'running', from: iso(at(14, 2)), to: iso(at(14, 10)) }]),
    member('Ines Lopez', [{ state: 'blocked', from: iso(at(14, 20)), to: null, reason: 'needs the API key' }]),
  ]), [], now)

  it('gives the time under the cursor, then one line per member with state and how long it has lasted', () => {
    const html = tooltipHtml(lanes(), at(14, 25, 7), false)
    const text = html.replace(/<[^>]+>/g, '')

    expect(text.startsWith(new Date(at(14, 25, 7)).toLocaleTimeString())).toBe(true)
    expect(text).toContain('M Manager — no runs in this window')
    expect(text).toContain('IL Ines Lopez — blocked 12 min (needs the API key)')
    expect(html).toContain('stats-chip--blocked')
  })

  it('gives the date too when the window crosses days', () => {
    const text = tooltipHtml(lanes(), at(14, 25, 7), true).replace(/<[^>]+>/g, '')

    expect(text.startsWith(new Date(at(14, 25, 7)).toLocaleString())).toBe(true)
  })

  it('escapes every name and reason, so markup reads as characters', () => {
    const html = tooltipHtml(activityLanes(activity([
      member('<b>Bold</b>', [{ state: 'failed', from: iso(at(14, 2)), to: null, reason: '<img src=x onerror=alert(1)>' }]),
    ]), [], now), at(14, 3), false)

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

  const time = (ms: number) => new Date(ms).toLocaleTimeString(undefined, { timeZone: zone })
  const stamp = (ms: number) => new Date(ms).toLocaleString(undefined, { timeZone: zone })

  it('labels each bucket size in the browser\'s locale, in the given zone', () => {
    const hour = [Date.parse('2026-10-06T12:00:00Z'), Date.parse('2026-10-06T13:00:00Z')] as const
    const day = Date.parse('2026-10-05T22:00:00Z')
    const month = Date.parse('2026-09-30T22:00:00Z')

    expect(bucketLabel(start, start + 60_000, 'minute', zone, false)).toBe(`${time(start)} – ${time(start + 60_000)}`)
    expect(bucketLabel(hour[0], hour[1], 'hour', zone, false)).toBe(`${time(hour[0])} – ${time(hour[1])}`)
    expect(bucketLabel(day, Date.parse('2026-10-06T22:00:00Z'), 'day', zone, false))
      .toBe(new Date(day).toLocaleDateString(undefined, { timeZone: zone }))
    expect(bucketLabel(month, Date.parse('2026-10-31T23:00:00Z'), 'month', zone, false))
      .toBe(new Date(month).toLocaleDateString(undefined, { timeZone: zone, year: 'numeric', month: 'long' }))
  })

  it('gives a minute or an hour its date too when the window crosses days', () => {
    expect(bucketLabel(start, start + 60_000, 'minute', zone, true)).toBe(`${stamp(start)} – ${stamp(start + 60_000)}`)
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

    expect(text).toContain(`${time(start)} – ${time(start + 60_000)}`)
    expect(text).toContain('running 40 s')
    expect(text).toContain('blocked 50 s')
    expect(text).not.toContain('waiting')
    expect(text).toContain('IL Ines Lopez — running 40 s, blocked 20 s')
    expect(html).not.toContain('<b>')
    expect(text).toContain('&lt;b&gt;Bold&lt;/b&gt; (removed) — blocked 30 s')
    expect(html).not.toContain('style=')
  })

  it('leaves a state hidden in the legend out of the totals and every member\'s split', () => {
    const column: Column = {
      from: start,
      to: start + 60_000,
      totals: { running: 40_000, waiting: 0, blocked: 50_000, failed: 0, idle: 10_000 },
      members: [
        { member: 'Ines', ms: { running: 40_000, waiting: 0, blocked: 20_000, failed: 0, idle: 0 } },
        { member: 'Rhea', ms: { running: 0, waiting: 0, blocked: 30_000, failed: 0, idle: 10_000 } },
        { member: 'Okon', ms: { running: 0, waiting: 0, blocked: 0, failed: 0, idle: 0 } },
      ],
    }
    const people = new Map([
      ['Ines', { name: 'Ines Lopez', initials: 'IL' }],
      ['Rhea', { name: 'Rhea', initials: 'R' }],
    ])

    const text = columnTooltipHtml(column, 'minute', zone, people, { hidden: new Set(['blocked']) }).replace(/<[^>]+>/g, '')

    expect(text).not.toContain('blocked')
    expect(text).toContain('running 40 s')
    expect(text).toContain('IL Ines Lopez — running 40 s')
    expect(text).toContain('R Rhea — idle 10 s')

    // A member with nothing left to show has no line at all.
    const runningOnly = columnTooltipHtml(column, 'minute', zone, people, { hidden: new Set(['blocked', 'idle']) })
      .replace(/<[^>]+>/g, '')

    expect(runningOnly).not.toContain('Rhea')
  })

  it('reads time under a second as under a second, never as nothing', () => {
    expect(memberTime(1)).toBe('<1 s')
    expect(memberTime(499)).toBe('<1 s')
    expect(memberTime(999)).toBe('<1 s')
    expect(memberTime(1_000)).toBe('1 s')
    expect(memberTime(1_400)).toBe('1 s')
    expect(memberTime(40_000)).toBe('40 s')

    const column: Column = {
      from: start,
      to: start + 60_000,
      totals: { running: 300, waiting: 0, blocked: 0, failed: 15_000, idle: 0 },
      members: [{ member: 'Ines', ms: { running: 300, waiting: 0, blocked: 0, failed: 15_000, idle: 0 } }],
    }
    const html = columnTooltipHtml(column, 'minute', zone, new Map([['Ines', { name: 'Ines Lopez', initials: 'IL' }]]))

    expect(html).toContain('running &lt;1 s')
    expect(html).toContain('IL</span> <span class="stats-tip-name">Ines Lopez</span> — running &lt;1 s, failed 15 s')
  })
})

describe('bucketing a figure at an instant', () => {
  const utc = (text: string) => Date.parse(text)
  const point = (member: string, at: string, value: number | null, gap?: InstantFigure['gap']): InstantFigure =>
    gap ? { member, at: utc(at), value, gap } : { member, at: utc(at), value }
  const edges = (columns: FigureColumn[]) => columns.map((c) => [new Date(c.from).toISOString(), new Date(c.to).toISOString()])

  it('puts each figure in the local minute it fell in, an edge belonging to the minute it starts', () => {
    const columns = bucketFigures([
      point('Ines', '2026-10-04T12:00:59.999Z', 10),
      point('Ines', '2026-10-04T12:01:00Z', 20),
      point('Ines', '2026-10-04T12:01:30Z', 5),
    ], ['Ines'], 'minute', 'Europe/Berlin')

    expect(edges(columns)).toEqual([
      ['2026-10-04T12:00:00.000Z', '2026-10-04T12:01:00.000Z'],
      ['2026-10-04T12:01:00.000Z', '2026-10-04T12:02:00.000Z'],
    ])
    expect(columns.map((c) => c.total)).toEqual([10, 25])
  })

  it('puts hour edges on the local hour of a half-hour zone, not the UTC hour', () => {
    // 10:29 and 10:30 UTC are 15:59 and 16:00 in Kolkata: two hours, not one.
    const columns = bucketFigures([
      point('Ines', '2026-10-04T10:29:59Z', 7),
      point('Ines', '2026-10-04T10:30:00Z', 8),
    ], ['Ines'], 'hour', 'Asia/Kolkata')

    expect(edges(columns)).toEqual([
      ['2026-10-04T09:30:00.000Z', '2026-10-04T10:30:00.000Z'],
      ['2026-10-04T10:30:00.000Z', '2026-10-04T11:30:00.000Z'],
    ])
    expect(columns.map((c) => c.total)).toEqual([7, 8])
  })

  it('puts day edges on local midnight', () => {
    // 21:59 and 22:00 UTC are 23:59 on the 4th and midnight on the 5th in Berlin summer time.
    const columns = bucketFigures([
      point('Ines', '2026-10-04T21:59:59Z', 1),
      point('Ines', '2026-10-04T22:00:00Z', 2),
    ], ['Ines'], 'day', 'Europe/Berlin')

    expect(edges(columns)).toEqual([
      ['2026-10-03T22:00:00.000Z', '2026-10-04T22:00:00.000Z'],
      ['2026-10-04T22:00:00.000Z', '2026-10-05T22:00:00.000Z'],
    ])
    expect(columns.map((c) => c.total)).toEqual([1, 2])
  })

  it('puts month edges on local midnight of the first', () => {
    const columns = bucketFigures([
      point('Ines', '2026-09-30T21:59:59Z', 3),
      point('Ines', '2026-09-30T22:00:00Z', 4),
    ], ['Ines'], 'month', 'Europe/Berlin')

    expect(edges(columns)).toEqual([
      ['2026-08-31T22:00:00.000Z', '2026-09-30T22:00:00.000Z'],
      ['2026-09-30T22:00:00.000Z', '2026-10-31T23:00:00.000Z'],
    ])
    expect(columns.map((c) => c.total)).toEqual([3, 4])
  })

  it('sums per member in the order given, and the members add up to the column', () => {
    const [column] = bucketFigures([
      point('Rhea', '2026-10-04T12:00:10Z', 30),
      point('Ines', '2026-10-04T12:00:20Z', 10),
      point('Rhea', '2026-10-04T12:00:30Z', 5),
    ], ['Ines', 'Rhea'], 'minute', 'Europe/Berlin')

    expect(column!.members.map((m) => [m.member, m.value])).toEqual([['Ines', 10], ['Rhea', 35]])
    expect(column!.total).toBe(45)
  })

  it('counts unmeasured runs and unsplit runs per bucket, never as zero, and keeps a bucket of only those', () => {
    const columns = bucketFigures([
      point('Ines', '2026-10-04T12:00:10Z', 10),
      point('Ines', '2026-10-04T12:00:20Z', null, 'unmeasured'),
      point('Rhea', '2026-10-04T12:00:30Z', null, 'split'),
      point('Rhea', '2026-10-04T12:05:00Z', null, 'unmeasured'),
      point('Rhea', '2026-10-04T12:05:40Z', null, 'unmeasured'),
    ], ['Ines', 'Rhea'], 'minute', 'Europe/Berlin')

    expect(columns.map((c) => [c.total, c.measured, c.unmeasured, c.unsplit])).toEqual([[10, 1, 1, 1], [0, 0, 2, 0]])
    expect(columns[0]!.members.map((m) => [m.member, m.measured, m.unmeasured, m.unsplit])).toEqual([['Ines', 1, 1, 0], ['Rhea', 0, 0, 1]])
    expect(edges(columns)[1]).toEqual(['2026-10-04T12:05:00.000Z', '2026-10-04T12:06:00.000Z'])
  })

  it('makes no bucket where no run ended', () => {
    const columns = bucketFigures([
      point('Ines', '2026-10-04T12:00:10Z', 1),
      point('Ines', '2026-10-04T12:09:10Z', 1),
    ], ['Ines'], 'minute', 'Europe/Berlin')

    expect(columns).toHaveLength(2)
  })
})

describe('a run\'s figure for the chosen metric', () => {
  const run = (over: Partial<TeamTokenRun>): TeamTokenRun =>
    ({ member: 'Ines', current: true, endedAt: '2026-10-04T12:00:00Z', measured: true, ...over })

  it('reads each metric from its own field', () => {
    const split = run({ billable: 380, tokensIn: 100, tokensCachedIn: 2000, tokensCacheCreation: 40, tokensOut: 30 })

    expect(runFigure(split, 'billable')).toEqual({ value: 380 })
    expect(runFigure(split, 'in')).toEqual({ value: 100 })
    expect(runFigure(split, 'cacheRead')).toEqual({ value: 2000 })
    expect(runFigure(split, 'cacheWrite')).toEqual({ value: 40 })
    expect(runFigure(split, 'out')).toEqual({ value: 30 })
  })

  it('gives an unmeasured run no figure for any metric', () => {
    for (const metric of ['billable', 'in', 'cacheRead', 'cacheWrite', 'out'] as const) {
      expect(runFigure(run({ measured: false }), metric)).toEqual({ gap: 'unmeasured' })
    }
  })

  it('counts a combined total in billable and as split not reported for the rest', () => {
    const combined = run({ billable: 900, combined: 900 })

    expect(runFigure(combined, 'billable')).toEqual({ value: 900 })

    for (const metric of ['in', 'cacheRead', 'cacheWrite', 'out'] as const) {
      expect(runFigure(combined, metric)).toEqual({ gap: 'split' })
    }
  })
})

describe('the column size that fits the window', () => {
  const from = Date.parse('2026-01-31T10:00:00Z')
  const hour = 3_600_000

  it('is the minute up to two hours', () => {
    expect(windowBucket(from, from + 1)).toBe('minute')
    expect(windowBucket(from, from + 2 * hour)).toBe('minute')
    expect(windowBucket(from, from + 2 * hour + 1)).toBe('hour')
  })

  it('is the hour up to three days', () => {
    expect(windowBucket(from, from + 72 * hour)).toBe('hour')
    expect(windowBucket(from, from + 72 * hour + 1)).toBe('day')
  })

  it('is the day up to three months, by the calendar', () => {
    const threeMonths = Date.parse('2026-04-30T10:00:00Z')

    expect(windowBucket(from, threeMonths)).toBe('day')
    expect(windowBucket(from, threeMonths + 1)).toBe('month')
  })

  it('is the month beyond', () => {
    expect(windowBucket(from, Date.parse('2027-01-31T10:00:00Z'))).toBe('month')
  })
})

describe('times read as the browser\'s locale reads them', () => {
  const morning = at(11, 38, 2)
  const afternoon = at(15, 41, 17)
  const nextDay = new Date(2026, 9, 5, 9, 0, 0).getTime()

  it('reads a time of day with seconds, exactly as toLocaleTimeString does', () => {
    expect(localTime(morning)).toBe(new Date(morning).toLocaleTimeString())
    expect(localTime(afternoon)).toBe(new Date(afternoon).toLocaleTimeString())
  })

  it('adds the date, exactly as the Workflows dialog\'s stamp does', () => {
    expect(localTime(afternoon, { date: true })).toBe(new Date(afternoon).toLocaleString())
  })

  it('names a stretch by its two ends, with dates only when it crosses days', () => {
    expect(crossesDays(morning, afternoon)).toBe(false)
    expect(crossesDays(morning, nextDay)).toBe(true)
    expect(localStretch(morning, afternoon))
      .toBe(`${new Date(morning).toLocaleTimeString()} – ${new Date(afternoon).toLocaleTimeString()}`)
    expect(localStretch(morning, nextDay))
      .toBe(`${new Date(morning).toLocaleString()} – ${new Date(nextDay).toLocaleString()}`)
  })

  it('labels the time axis as the locale does: a time of day, a date at midnight, a date or a month', () => {
    const zone = Intl.DateTimeFormat().resolvedOptions().timeZone
    const midnight = new Date(2026, 9, 5).getTime()

    expect(axisTimeLabel(afternoon, 'minute', zone)).toBe(new Date(afternoon).toLocaleTimeString())
    expect(axisTimeLabel(afternoon, 'hour', zone)).toBe(new Date(afternoon).toLocaleTimeString())
    expect(axisTimeLabel(midnight, 'hour', zone)).toBe(new Date(midnight).toLocaleDateString())
    expect(axisTimeLabel(afternoon, 'day', zone)).toBe(new Date(afternoon).toLocaleDateString())
    expect(axisTimeLabel(afternoon, 'month', zone))
      .toBe(new Date(afternoon).toLocaleDateString(undefined, { year: 'numeric', month: 'long' }))
  })
})

describe('the hover box beside the pointer', () => {
  const box = [180, 90] as const
  const gap = 12
  const viewport = { width: 1200, height: 800 }

  /** Where the box's left and right edges fall, in the chart's own pixels. */
  function edges(pointer: number, chartLeft: number, chartWidth: number) {
    const [x] = tooltipBeside([pointer, 20], [...box], { left: chartLeft, top: 300 }, viewport, gap)

    return { left: x, right: x + box[0], width: chartWidth }
  }

  it('sits to the pointer\'s right with a gap, never over it, at the left edge and the middle', () => {
    for (const pointer of [0, 2, 300]) {
      const { left } = edges(pointer, 100, 600)

      expect(left).toBe(pointer + gap)
    }
  })

  it('flips to the pointer\'s left near the right edge, with the same gap', () => {
    for (const pointer of [598, 600]) {
      const { right } = edges(pointer, 600, 600)

      expect(right).toBe(pointer - gap)
    }
  })

  it('never covers the pointer\'s x anywhere across the chart', () => {
    for (const chartLeft of [0, 100, 600, 1000]) {
      for (let pointer = 0; pointer <= 200; pointer += 10) {
        const { left, right } = edges(pointer, chartLeft, 200)

        expect(pointer < left || pointer > right).toBe(true)
      }
    }
  })

  it('may reach past the chart\'s own edges but stays on the screen vertically', () => {
    const [, top] = tooltipBeside([10, 0], [...box], { left: 100, top: 0 }, viewport, gap)
    const [, low] = tooltipBeside([10, 40], [...box], { left: 100, top: 760 }, viewport, gap)

    expect(top).toBe(0)
    expect(760 + low + box[1]).toBeLessThanOrEqual(viewport.height)
  })
})

describe('a token bucket in words', () => {
  const zone = 'Europe/Berlin'
  const start = Date.parse('2026-10-06T12:00:00Z')

  it('gives the bucket, each member\'s figure, the total and the runs not measured, escaped', () => {
    const column: FigureColumn = {
      from: start,
      to: start + 3_600_000,
      total: 1500,
      measured: 3,
      unmeasured: 2,
      unsplit: 1,
      members: [
        { member: 'Ines', value: 1200, measured: 2, unmeasured: 0, unsplit: 0 },
        { member: 'Bold', value: 300, measured: 1, unmeasured: 2, unsplit: 0 },
        { member: 'Rhea', value: 0, measured: 0, unmeasured: 0, unsplit: 1 },
      ],
    }
    const people = new Map([
      ['Ines', { name: 'Ines Lopez' }],
      ['Bold', { name: '<b>Bold</b> (removed)' }],
      ['Rhea', { name: 'Rhea' }],
    ])
    const html = figureTooltipHtml(column, 'hour', zone, people)
    const text = html.replace(/<[^>]+>/g, '')
    const time = (ms: number) => new Date(ms).toLocaleTimeString(undefined, { timeZone: zone })

    expect(text).toContain(`${time(start)} – ${time(start + 3_600_000)}`)
    expect(text).toContain(`Ines Lopez — ${(1200).toLocaleString()}`)
    expect(text).toContain(`&lt;b&gt;Bold&lt;/b&gt; (removed) — ${(300).toLocaleString()}, 2 runs not measured`)
    expect(text).toContain('Rhea — 1 run: split not reported')
    expect(text).toContain(`Total ${(1500).toLocaleString()}`)
    expect(text).toContain('2 runs not measured')
    expect(text).toContain('1 run: split not reported')
    expect(html).not.toContain('<b>')
    expect(html).not.toContain('style=')
  })

  it('leaves a member hidden in the legend out of the lines and the total', () => {
    const column: FigureColumn = {
      from: start,
      to: start + 3_600_000,
      total: 1500,
      measured: 3,
      unmeasured: 2,
      unsplit: 1,
      members: [
        { member: 'Ines', value: 1200, measured: 2, unmeasured: 0, unsplit: 0 },
        { member: 'Bold', value: 300, measured: 1, unmeasured: 2, unsplit: 0 },
        { member: 'Rhea', value: 0, measured: 0, unmeasured: 0, unsplit: 1 },
      ],
    }
    const people = new Map([['Ines', { name: 'Ines Lopez' }], ['Bold', { name: 'Bold' }], ['Rhea', { name: 'Rhea' }]])

    const text = figureTooltipHtml(column, 'hour', zone, people, { hidden: new Set(['Ines']) }).replace(/<[^>]+>/g, '')

    expect(text).not.toContain('Ines')
    expect(text).toContain(`Total ${(300).toLocaleString()}`)
    expect(text).toContain('2 runs not measured')
    expect(text).toContain('1 run: split not reported')

    const onlyRhea = figureTooltipHtml(column, 'hour', zone, people, { hidden: new Set(['Ines', 'Bold']) })
      .replace(/<[^>]+>/g, '')

    expect(onlyRhea).toContain('Total not measured')
    expect(onlyRhea).not.toContain('not measured,')
    expect(onlyRhea).not.toContain('2 runs')
  })

  it('never reads a bucket of only unmeasured runs as a total of zero', () => {
    const column: FigureColumn = {
      from: start, to: start + 3_600_000, total: 0, measured: 0, unmeasured: 3, unsplit: 0,
      members: [{ member: 'Ines', value: 0, measured: 0, unmeasured: 3, unsplit: 0 }],
    }
    const text = figureTooltipHtml(column, 'hour', zone, new Map()).replace(/<[^>]+>/g, '')

    expect(text).toContain('Ines — 3 runs not measured')
    expect(text).not.toMatch(/\b0\b/)
    expect(text).toContain('Total not measured')
  })
})
