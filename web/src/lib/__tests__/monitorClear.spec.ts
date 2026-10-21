// @vitest-environment happy-dom
//
// THE MONITOR'S CLEAR: the model forgets, in this browser, what the rows it applied made - the
// activity list, the workflows, the token stretch and the counts - and keeps the teams and members
// the overview's snapshots gave. The next row shows as it would in a fresh model. The scene drops
// what the forgotten rows drew at once, so nothing fades under reduced motion. The mark the view
// keeps (`harness.monitor.clearedAfter`) is the seq it cleared after; the window reads from there.
import { afterEach, describe, expect, it } from 'vitest'
import type { Message, Team } from '../../api/types'
import { MonitorModel } from '../monitor/model'
import { MonitorScene } from '../monitor/scene'
import { ClearedKey, readClearedAfter, windowAfter, writeClearedAfter } from '../monitor/cleared'

let seq = 20700

function row(type: string, source: string, payload: Record<string, unknown>, correlationId: number, at: string): Message {
  seq++
  return { seq, type, source, payload: JSON.stringify(payload), correlationId, causationSeq: null, depth: 0, occurredAt: at }
}

const tell = (correlation: number, at: string, to = 'Alpha/Developer') =>
  row(`agentContainer.instruction.${to}`, 'person@example.com', { instruction: 'Build it', subject: 'Build it' }, correlation, at)

const teams = [{
  id: 'Alpha', name: 'Alpha team', containers: [
    { id: 'Manager', name: 'Manager', state: 'Idle', queueDepth: 0, currentCorrelation: null },
    { id: 'Developer', name: 'Developer Ada', state: 'Idle', queueDepth: 0, currentCorrelation: null },
  ],
}] as unknown as Team[]

/** A live model as the view makes one: the snapshots, then the rows held. */
function busy(): MonitorModel {
  const model = new MonitorModel('Manager', 80, new Set(['Alpha']))
  model.reconcile(teams)
  model.apply(tell(41, '2026-10-21T09:00:00Z'))
  model.apply(row('agentContainer.started', 'Alpha/Developer', {}, 41, '2026-10-21T09:00:05Z'))
  model.apply(row('agentContainer.completed', 'Alpha/Developer', { tokensIn: 1200, tokensOut: 300 }, 41, '2026-10-21T09:01:00Z'))
  model.apply(row('agentContainer.completed', 'Alpha/Developer', {}, 41, '2026-10-21T09:02:00Z'))
  model.apply(tell(42, '2026-10-21T09:03:00Z', 'Alpha/Manager'))
  model.applyWorkspace({ team: 'Alpha', member: 'Developer', repo: 'Yawble', at: '2026-10-21T09:03:30Z', files: [{ path: 'web/a.ts', project: 'web', action: 'changed' }], commits: [], more: false })
  return model
}

describe('forgetting what the Monitor holds', () => {
  it('empties the ticker and the workflows and zeroes the oldest-row time, the tokens and the counts', () => {
    const model = busy()
    expect(model.ticker.length).toBeGreaterThan(0)
    expect(model.workflows.size).toBe(2)

    model.forget(teams)

    expect(model.ticker).toEqual([])
    expect(model.workflows.size).toBe(0)
    expect(model.since).toBe(0)
    expect(model.now).toBe(0)
    expect(model.tokens).toBe(0)
    expect(model.unmeasured).toBe(0)
    expect(model.rate()).toBe(0)
    expect(model.counts().open).toBe(0)
    for (const member of model.members.values()) expect(member).toMatchObject({ tokens: 0, runs: 0, lastAt: 0 })
  })

  it('keeps the teams and members the snapshots gave', () => {
    const model = busy()
    model.forget(teams)

    expect(model.teams.get('Alpha')?.name).toBe('Alpha team')
    expect([...model.members.keys()].sort()).toEqual(['Alpha/Developer', 'Alpha/Manager'])
    expect(model.members.get('Alpha/Developer')?.name).toBe('Developer Ada')
    expect(model.counts()).toMatchObject({ teams: 1, members: 2 })
    // The forgotten instruction had queued the Manager; the snapshot says idle with nothing queued.
    expect(model.members.get('Alpha/Manager')).toMatchObject({ state: 'idle', queued: 0, workflow: null })
  })

  it('shows the next row as a fresh model shows it', () => {
    const forgot = busy()
    forgot.forget(teams)
    const fresh = new MonitorModel('Manager', 80, new Set(['Alpha']))
    fresh.reconcile(teams)

    const next = tell(43, '2026-10-21T09:10:00Z')
    const effects = forgot.apply(next)

    expect(effects).toEqual(fresh.apply(next))
    expect(forgot.ticker).toEqual(fresh.ticker)
    expect([...forgot.workflows.values()]).toEqual([...fresh.workflows.values()])
    expect(forgot.since).toBe(fresh.since)
    expect(forgot.now).toBe(fresh.now)
    expect(forgot.counts()).toEqual(fresh.counts())

    const done = row('agentContainer.completed', 'Alpha/Developer', { tokensIn: 50 }, 43, '2026-10-21T09:11:00Z')
    forgot.apply(done)
    fresh.apply(done)
    expect(forgot.tokens).toBe(50)
    expect(forgot.tokens).toBe(fresh.tokens)
    expect(forgot.members.get('Alpha/Developer')).toMatchObject({ tokens: 50, runs: 1 })
  })

  it('keeps the ticker\'s depth', () => {
    const model = new MonitorModel('Manager', 3, null)
    model.forget(teams)
    for (let i = 0; i < 5; i++) model.apply(tell(50 + i, '2026-10-21T09:20:00Z'))
    expect(model.ticker).toHaveLength(3)
  })
})

describe('the scene on a clear', () => {
  it('drops the forgotten workflows and what was in flight at once, with nothing fading under reduced motion', () => {
    const model = busy()
    const canvas = document.createElement('canvas')
    document.body.appendChild(canvas)
    const scene = new MonitorScene(canvas, { fontPx: 12, fontFamily: 'sans-serif', reducedMotion: true })
    scene.resize(800, 600)
    scene.sync(model)
    scene.step(0)
    scene.play([{ kind: 'step', at: { kind: 'member', key: 'Alpha/Developer' }, step: { id: 1, team: 'Alpha', member: 'Developer', run: 9, workflow: 41, at: '2026-10-21T09:03:00Z', kind: 'tool', label: 'Bash', mcpTool: null, count: 1 } }])
    scene.play(model.applyWorkspace({ team: 'Alpha', member: 'Developer', repo: 'Yawble', at: '2026-10-21T09:03:40Z', files: [{ path: 'web/b.ts', project: 'web', action: 'created' }], commits: [], more: false }))
    scene.step(16)
    expect(scene.describeNode('w:41')).not.toBeNull()
    expect(scene.shownSteps().length).toBe(1)
    expect(scene.shownWorkspace().nodes.length).toBeGreaterThan(0)

    model.forget(teams)
    scene.forget(model)
    scene.step(32)

    expect(scene.describeNode('w:41')).toBeNull()
    expect(scene.describeNode('w:42')).toBeNull()
    expect(scene.drawnWorkflows).toEqual([])
    expect(scene.shownSteps()).toEqual([])
    expect(scene.shownWorkspace().nodes).toEqual([])
    expect(scene.describeNode('m:Alpha/Developer')).not.toBeNull()
    expect(scene.describeNode('t:Alpha')).not.toBeNull()
  })
})

describe('the clear mark', () => {
  afterEach(() => localStorage.removeItem(ClearedKey))

  it('is the seq cleared after, beside the Monitor\'s other settings, and none when not set or unreadable', () => {
    expect(ClearedKey).toBe('harness.monitor.clearedAfter')
    expect(readClearedAfter()).toBeNull()
    writeClearedAfter(20705)
    expect(localStorage.getItem(ClearedKey)).toBe('20705')
    expect(readClearedAfter()).toBe(20705)
    localStorage.setItem(ClearedKey, 'nonsense')
    expect(readClearedAfter()).toBeNull()
    writeClearedAfter(null)
    expect(localStorage.getItem(ClearedKey)).toBeNull()
  })

  it('reads the window from the row after it, and all of it with none', () => {
    const rows = [tell(60, '2026-10-21T09:30:00Z'), tell(61, '2026-10-21T09:31:00Z'), tell(62, '2026-10-21T09:32:00Z')]
    expect(windowAfter(rows, null)).toBe(rows)
    expect(windowAfter(rows, rows[0]!.seq)).toEqual(rows.slice(1))
    expect(windowAfter(rows, rows[2]!.seq)).toEqual([])
  })
})
