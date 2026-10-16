import { describe, expect, it } from 'vitest'
import type { Message, Team } from '../../api/types'
import { MonitorModel, ReplayClock, addressOf, compact, originOf, timeline, toneOf } from '../monitor/model'
import { initials } from '../monitor/scene'

let seq = 100

function row(type: string, source: string, payload: Record<string, unknown> = {}, at = '2026-10-15T10:00:00Z', correlationId = 1): Message {
  seq++
  return { seq, type, source, payload: JSON.stringify(payload), correlationId, causationSeq: null, depth: 0, occurredAt: at }
}

const tell = (to: string, from: string, subject = 'Build it', at?: string, correlation = 1) =>
  row(`agentContainer.instruction.${to}`, from, { instruction: subject, subject }, at, correlation)

describe('where a row belongs', () => {
  it('files an instruction on its addressee and a member row on its publisher', () => {
    expect(addressOf(tell('Alpha/Developer', 'Alpha/Manager'))).toEqual({ team: 'Alpha', member: 'Developer' })
    expect(addressOf(row('agentContainer.started', 'Alpha/Developer'))).toEqual({ team: 'Alpha', member: 'Developer' })
  })

  it('files a card or dispatch row on the team its payload names, and a skipped fire on its member', () => {
    expect(addressOf(row('backlog.item.dispatched', 'person@example.com', { team: 'Alpha', title: 'x' }))).toEqual({ team: 'Alpha', member: null })
    expect(addressOf(row('agentContainer.scheduleSkipped', 'schedule:abc', { member: 'Alpha/Poller' }))).toEqual({ team: 'Alpha', member: 'Poller' })
  })

  it('names where outside work came from', () => {
    expect(originOf('schedule:abc')).toBe('trigger')
    expect(originOf('trigger:abc')).toBe('trigger')
    expect(originOf('site:Alpha/triage')).toBe('site')
    expect(originOf('0fa36a52bdcb45a1')).toBe('person')
  })
})

describe('what each row does', () => {
  it('queues an instruction as a comet from the person, then runs it when it starts', () => {
    const model = new MonitorModel()
    const effects = model.apply(tell('Alpha/Manager', '0fa36a52bdcb45a1'))

    expect(effects).toContainEqual(expect.objectContaining({ kind: 'comet', from: { kind: 'origin', team: 'Alpha', origin: 'person' }, to: { kind: 'member', key: 'Alpha/Manager' }, tone: 'tell' }))
    expect(model.members.get('Alpha/Manager')).toMatchObject({ queued: 1, state: 'waiting', manager: true })

    model.apply(row('agentContainer.started', 'Alpha/Manager'))
    expect(model.members.get('Alpha/Manager')).toMatchObject({ queued: 0, state: 'running' })
  })

  it('sends a member-to-member instruction from the sender and ties both into the workflow', () => {
    const model = new MonitorModel()
    const effects = model.apply(tell('Alpha/Developer', 'Alpha/Manager', 'Write the skeleton', undefined, 7))

    expect(effects[0]).toMatchObject({ kind: 'comet', from: { kind: 'member', key: 'Alpha/Manager' }, label: 'Write the skeleton' })
    expect(model.workflows.get(7)?.title).toBe('Write the skeleton')
    expect([...model.workflows.get(7)!.members]).toEqual(['Alpha/Developer'])
  })

  it('counts a finished run\'s measured tokens and never estimates an unmeasured one', () => {
    const model = new MonitorModel()
    model.apply(row('agentContainer.started', 'Alpha/Developer'))
    model.apply(row('agentContainer.completed', 'Alpha/Developer', { tokensIn: 1200, tokensOut: 300 }))
    model.apply(row('agentContainer.completed', 'Alpha/Tester', {}))

    expect(model.tokens).toBe(1500)
    expect(model.members.get('Alpha/Developer')).toMatchObject({ tokens: 1500, runs: 1, state: 'idle' })
    expect(model.members.get('Alpha/Tester')).toMatchObject({ tokens: 0, runs: 1 })
  })

  it('takes a combined total as reported', () => {
    const model = new MonitorModel()
    model.apply(row('agentContainer.completed', 'Alpha/Developer', { tokensTotal: 9000, tokensIn: 1, tokensOut: 1 }))
    expect(model.tokens).toBe(9000)
  })

  it('sends a hand-back to the team\'s Manager in gold', () => {
    const model = new MonitorModel()
    const effects = model.apply(row('agentContainer.handback', 'Alpha/Developer', { delivered: 'done' }))

    expect(effects).toContainEqual(expect.objectContaining({ kind: 'comet', to: { kind: 'member', key: 'Alpha/Manager' }, tone: 'handback' }))
  })

  it('marks a blocked member and a failed one, and shakes the failure', () => {
    const model = new MonitorModel()
    model.apply(row('agentContainer.blocked', 'Alpha/Developer', { reason: 'no key' }))
    const failed = model.apply(row('agentContainer.failed', 'Alpha/Tester', { failureClass: 'crashed' }))

    expect(model.members.get('Alpha/Developer')).toMatchObject({ state: 'blocked', status: 'no key' })
    expect(model.members.get('Alpha/Tester')?.state).toBe('failed')
    expect(failed).toContainEqual({ kind: 'shake', at: { kind: 'member', key: 'Alpha/Tester' } })
    expect(model.counts().blocked).toBe(2)
  })

  it('ends a workflow with a big burst once, and a workflow row it never drew changes nothing', () => {
    const model = new MonitorModel()
    model.apply(tell('Alpha/Manager', 'person', 'Go', undefined, 5))
    const done = model.apply(row('workflow.completed', 'Alpha/Manager', {}, undefined, 5))
    const again = model.apply(row('workflow.completed', 'Alpha/Manager', {}, undefined, 5))

    expect(done).toContainEqual({ kind: 'burst', at: { kind: 'workflow', id: 5 }, tone: 'workflow', big: true })
    expect(again.some((effect) => effect.kind === 'burst')).toBe(false)
    expect(model.workflows.get(5)?.ending).toBe('completed')
    expect(model.counts().open).toBe(0)

    model.apply(row('workflow.completed', 'Alpha/Manager', {}, undefined, 99))
    expect(model.workflows.has(99)).toBe(false)
  })

  it('floats a progress line but keeps it out of the activity list', () => {
    const model = new MonitorModel()
    const effects = model.apply(row('agentContainer.progress', 'Alpha/Developer', { status: 'Running the tests' }))

    expect(effects).toContainEqual(expect.objectContaining({ kind: 'float', text: 'Running the tests' }))
    expect(model.ticker).toEqual([])
    expect(model.members.get('Alpha/Developer')?.status).toBe('Running the tests')
  })

  it('lists every other row newest first, with its verb', () => {
    const model = new MonitorModel()
    model.apply(tell('Alpha/Manager', 'person', 'Go'))
    model.apply(row('agentContainer.started', 'Alpha/Manager'))

    expect(model.ticker.map((entry) => entry.verb)).toEqual(['started', 'told'])
    expect(model.ticker[1]).toMatchObject({ team: 'Alpha', member: 'Manager', tone: 'tell' })
  })

  it('passes over a team it does not show', () => {
    const model = new MonitorModel('Manager', 80, new Set(['Alpha']))
    expect(model.apply(tell('Beta/Manager', 'person'))).toEqual([])
    expect(model.teams.has('Beta')).toBe(false)
    expect(model.ticker).toEqual([])
  })
})

describe('the snapshots are the truth', () => {
  const snapshot = (id: string, over: Record<string, unknown> = {}) => ({
    team: 'Alpha', id, name: id, agent: 'claude', state: 'Idle', queueDepth: 0, ceiling: 1, subscribes: [],
    currentCorrelation: null, sinceSeq: 0, ...over,
  })

  it('takes each member\'s state from its snapshot and forgets a member the team no longer has', () => {
    const model = new MonitorModel('Manager', 80, new Set(['Alpha']))
    model.apply(row('agentContainer.started', 'Alpha/Gone'))
    model.apply(row('agentContainer.started', 'Alpha/Developer'))

    const teams = [{
      id: 'Alpha', name: 'Alpha team', containers: [
        snapshot('Manager', { state: 'Running' }),
        snapshot('Developer', { queueDepth: 2 }),
        snapshot('Tester', { needsDecision: 'Which repository?' }),
        snapshot('Mailer', { kind: 'plugin', state: 'Running', held: true }),
      ],
    }] as unknown as Team[]
    model.reconcile(teams)

    expect(model.teams.get('Alpha')?.name).toBe('Alpha team')
    expect(model.members.has('Alpha/Gone')).toBe(false)
    expect(model.members.get('Alpha/Manager')?.state).toBe('running')
    expect(model.members.get('Alpha/Developer')).toMatchObject({ state: 'waiting', queued: 2 })
    expect(model.members.get('Alpha/Tester')).toMatchObject({ state: 'decision', status: 'Which repository?' })
    expect(model.members.get('Alpha/Mailer')).toMatchObject({ state: 'waiting', kind: 'plugin' })
  })
})

describe('a replay knows what the Host calls things', () => {
  it('draws a plugin member as a plugin and names teams and members as the Host does, adding none', () => {
    const model = new MonitorModel('Manager', 80, null)
    model.replay = true
    model.know([{
      id: 'Alpha', name: 'Alpha team', containers: [
        { id: 'Mailer', name: 'Mail sender', kind: 'plugin' },
        { id: 'Developer', name: 'Developer Ada', kind: 'agent' },
      ],
    }] as unknown as Team[])
    expect(model.members.size).toBe(0)

    model.apply(row('agentContainer.started', 'Alpha/Mailer'))
    model.apply(row('agentContainer.started', 'Alpha/Developer'))
    model.apply(row('agentContainer.started', 'Gone/Someone'))

    expect(model.teams.get('Alpha')?.name).toBe('Alpha team')
    expect(model.members.get('Alpha/Mailer')).toMatchObject({ name: 'Mail sender', kind: 'plugin' })
    expect(model.members.get('Alpha/Developer')).toMatchObject({ name: 'Developer Ada', kind: 'agent' })
    expect(model.members.get('Gone/Someone')).toMatchObject({ name: 'Someone', kind: 'agent' })
  })
})

describe('the replay clock', () => {
  const at = (seconds: number) => new Date(Date.UTC(2026, 9, 15, 10, 0, seconds)).toISOString()

  it('plays rows at its speed', () => {
    const rows = [row('a', 'A/M', {}, at(0)), row('b', 'A/M', {}, at(10)), row('c', 'A/M', {}, at(20))]
    const clock = new ReplayClock(rows, 10)

    expect(clock.advance(0).map((r) => r.type)).toEqual(['a'])
    expect(clock.advance(500).map((r) => r.type)).toEqual([])
    expect(clock.advance(600).map((r) => r.type)).toEqual(['b'])
    expect(clock.advance(1000).map((r) => r.type)).toEqual(['c'])
    expect(clock.done).toBe(true)
  })

  it('skips a quiet stretch down to its longest gap', () => {
    const rows = [row('a', 'A/M', {}, at(0)), row('b', 'A/M', {}, '2026-10-15T14:00:00Z')]
    const clock = new ReplayClock(rows, 10, 20_000)
    clock.advance(0)

    // Four hours later at 10x would be 24 minutes of wall time; the gap is cut to 20 s of log time.
    expect(clock.advance(2100).map((r) => r.type)).toEqual(['b'])
  })

  it('starts where it is asked to', () => {
    const rows = [row('a', 'A/M', {}, at(0)), row('b', 'A/M', {}, at(30))]
    const clock = new ReplayClock(rows, 1, 20_000, Date.parse(at(25)))
    expect(clock.advance(5000).map((r) => r.type)).toEqual(['b'])
  })

  it('plays recorded file changes at their times beside the rows, and is done only when both are', () => {
    const change = (seconds: number) => ({ team: 'A', member: 'M', repo: 'r', at: at(seconds), files: [], commits: [], more: false })
    const rows = [row('a', 'A/M', {}, at(0)), row('b', 'A/M', {}, at(10))]
    const clock = new ReplayClock(rows, 10, 20_000, Date.parse(at(0)), [change(15), change(5)])

    expect(clock.step(0)).toMatchObject({ rows: [{ type: 'a' }], changes: [] })
    expect(clock.step(400).changes).toEqual([])
    expect(clock.step(100)).toMatchObject({ rows: [], changes: [{ at: at(5) }] })
    expect(clock.step(500)).toMatchObject({ rows: [{ type: 'b' }], changes: [] })
    expect(clock.done).toBe(false)
    expect(clock.step(500)).toMatchObject({ rows: [], changes: [{ at: at(15) }] })
    expect(clock.done).toBe(true)
  })

  it('skips the quiet down to the next row or change, whichever is sooner', () => {
    const change = { team: 'A', member: 'M', repo: 'r', at: '2026-10-15T12:00:00Z', files: [], commits: [], more: false }
    const rows = [row('a', 'A/M', {}, at(0)), row('b', 'A/M', {}, '2026-10-15T14:00:00Z')]
    const clock = new ReplayClock(rows, 10, 20_000, Date.parse(at(0)), [change])
    clock.step(0)

    expect(clock.step(2100)).toMatchObject({ rows: [], changes: [{ at: change.at }] })
    expect(clock.step(2100).rows.map((r) => r.type)).toEqual(['b'])
  })
})

describe('the timeline and the words', () => {
  it('cuts the rows into equal slices coloured by what most of them were, progress aside', () => {
    const rows = [
      row('agentContainer.progress', 'A/M', {}, '2026-10-15T10:00:00Z'),
      row('agentContainer.progress', 'A/M', {}, '2026-10-15T10:00:01Z'),
      row('agentContainer.completed', 'A/M', {}, '2026-10-15T10:00:02Z'),
      row('agentContainer.failed', 'A/M', {}, '2026-10-15T10:00:09Z'),
    ]
    const buckets = timeline(rows, 2)

    expect(buckets.map((bucket) => bucket.count)).toEqual([3, 1])
    expect(buckets.map((bucket) => bucket.tone)).toEqual(['done', 'fail'])
    expect(toneOf('plugin.azure.applied')).toBe('plugin')
  })

  it('says token figures and member initials briefly', () => {
    expect(compact(950)).toBe('950')
    expect(compact(12_345)).toBe('12k')
    expect(compact(1_500)).toBe('1.5k')
    expect(compact(2_500_000)).toBe('2.5M')
    expect(initials('DeveloperHelene')).toBe('DH')
    expect(initials('Namecheap DNS')).toBe('ND')
    expect(initials('Manager')).toBe('M')
  })
})

describe('a member\'s pending wake', () => {
  const asked = (id: number, dueAt: string, note: string) =>
    row('wake.asked', 'Alpha/Developer', { wake: id, dueAt, note }, '2026-10-15T10:00:00Z', 7)

  it('sets the clock on the member at the ask, earliest first, and takes it down at the fire, a cancel or a skip', () => {
    const model = new MonitorModel()
    model.apply(asked(2, '2026-10-15T10:30:00Z', 'look at the deploy'))
    model.apply(asked(1, '2026-10-15T10:10:00Z', 'check the certificate'))

    expect(model.members.get('Alpha/Developer')!.wakes).toEqual([
      { id: 1, dueAt: Date.parse('2026-10-15T10:10:00Z'), note: 'check the certificate' },
      { id: 2, dueAt: Date.parse('2026-10-15T10:30:00Z'), note: 'look at the deploy' },
    ])

    model.apply(row('wake.fired', 'Alpha/Developer', { wake: 1, dueAt: '2026-10-15T10:10:00Z', instruction: 140, note: 'check the certificate' }))
    expect(model.members.get('Alpha/Developer')!.wakes.map((wake) => wake.id)).toEqual([2])

    model.apply(row('wake.cancelled', 'Alpha/Developer', { wake: 2, by: 'ada@example.com', reason: 'cancelled by a person', note: 'look at the deploy' }))
    expect(model.members.get('Alpha/Developer')!.wakes).toEqual([])

    model.apply(asked(3, '2026-10-15T11:00:00Z', 'again'))
    model.apply(row('wake.skipped', 'Alpha/Developer', { wake: 3, reason: 'its team is archived', note: 'again' }))
    expect(model.members.get('Alpha/Developer')!.wakes).toEqual([])
  })

  it('names the ask, the fire and the cancel in the activity list, with the note', () => {
    const model = new MonitorModel()
    model.apply(asked(1, '2026-10-15T10:10:00Z', 'check the certificate'))
    model.apply(row('wake.fired', 'Alpha/Developer', { wake: 1, dueAt: '2026-10-15T10:10:00Z', instruction: 140, note: 'check the certificate' }))
    model.apply(asked(2, '2026-10-15T10:30:00Z', 'look at the deploy'))
    model.apply(row('wake.cancelled', 'Alpha/Developer', { wake: 2, by: 'ada@example.com', reason: 'cancelled by a person', note: 'look at the deploy' }))

    expect(model.ticker.map((entry) => entry.verb)).toEqual(['wake cancelled', 'asked to be woken', 'wake fired', 'asked to be woken'])
    expect(model.ticker[0]!.text).toBe('cancelled by a person (ada@example.com): look at the deploy')
    expect(model.ticker[2]!.text).toBe('woken as it asked: check the certificate')
    expect(model.ticker[3]!.text).toContain('check the certificate')
  })

  it('draws the instruction a wake fires as the member\'s own ripple, never a comet from a person', () => {
    const model = new MonitorModel()
    const effects = model.apply(row('agentContainer.instruction.Alpha/Developer', 'wake:1', { instruction: 'You asked to be woken.' }, undefined, 7))

    expect(effects).toEqual([{ kind: 'ripple', at: { kind: 'member', key: 'Alpha/Developer' }, tone: 'tell' }])
    expect(model.ticker[0]!.verb).toBe('woken')
    expect(model.members.get('Alpha/Developer')).toMatchObject({ queued: 1, state: 'waiting' })
  })

  it('reads the clock from the snapshot, and takes it down when the snapshot has none', () => {
    const model = new MonitorModel()
    const team = (wakes?: unknown[]) =>
      [{ id: 'Alpha', name: 'Alpha', containers: [{ id: 'Developer', name: 'Developer', state: 'Idle', queueDepth: 0, currentCorrelation: 7, ...(wakes ? { wakes } : {}) }] }] as unknown as Team[]

    model.reconcile(team([{ id: 4, member: 'Developer', workflow: 7, dueAt: '2026-10-15T10:10:00Z', askedAt: '2026-10-15T10:00:00Z', note: 'check' }]))
    expect(model.members.get('Alpha/Developer')!.wakes).toEqual([{ id: 4, dueAt: Date.parse('2026-10-15T10:10:00Z'), note: 'check' }])

    model.reconcile(team())
    expect(model.members.get('Alpha/Developer')!.wakes).toEqual([])
  })
})
