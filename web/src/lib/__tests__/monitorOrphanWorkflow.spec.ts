// @vitest-environment happy-dom
//
// A WORKFLOW NODE ONLY FOR A WORKFLOW SOMEBODY WORKS. A row addressed to a team alone - a folder's
// `file.changed` published when a person uploads a document, a trigger's poll - begins no
// workflow: it keeps its ticker line and its ripple on the team, and adds to a workflow a member
// row already began. The scene draws no workflow with no member, so a model built from older rows
// draws no orphan either; the node appears with the first member row.
import { describe, expect, it } from 'vitest'
import type { Message } from '../../api/types'
import { MonitorModel, ReplayClock, addressOf } from '../monitor/model'
import { MonitorScene } from '../monitor/scene'

let seq = 18600

function row(type: string, source: string, payload: Record<string, unknown>, correlationId: number, at = '2026-10-19T10:00:00Z'): Message {
  seq++
  return { seq, type, source, payload: JSON.stringify(payload), correlationId, causationSeq: null, depth: 0, occurredAt: at }
}

/** A person uploads a document to the team's folder: the team in the payload, no member. */
const upload = (correlation: number, at?: string) =>
  row('file.changed', 'person@example.com', { team: 'Alpha', path: 'documents', changed: ['notes.md'] }, correlation, at)
/** An instruction to a member, from a person or a trigger. */
const tell = (correlation: number, source = 'person@example.com', at?: string) =>
  row('agentContainer.instruction.Alpha/Developer', source, { instruction: 'Read the notes', subject: 'Read the notes' }, correlation, at)

function scene(model: MonitorModel) {
  const canvas = document.createElement('canvas')
  document.body.appendChild(canvas)
  const made = new MonitorScene(canvas, { fontPx: 12, fontFamily: 'sans-serif', reducedMotion: true })
  made.sync(model)
  made.step(0)
  return made
}

describe('a row addressed to a team alone', () => {
  it('names no member', () => {
    expect(addressOf(upload(18627))).toEqual({ team: 'Alpha', member: null })
  })

  it('begins no workflow in its own correlation, and still gets its ticker line and its ripple', () => {
    const model = new MonitorModel()
    const effects = model.apply(upload(18627))

    expect([...model.workflows.keys()]).toEqual([])
    expect(model.counts().open).toBe(0)
    expect(effects).toEqual([expect.objectContaining({ kind: 'ripple', at: { kind: 'team', id: 'Alpha' } })])
    expect(model.ticker).toHaveLength(1)
    expect(model.ticker[0]).toMatchObject({ team: 'Alpha', member: null, workflow: 18627 })
  })

  it('adds to the workflow a member instruction already opened', () => {
    const model = new MonitorModel()
    model.apply(tell(18638))
    model.apply(upload(18638))

    expect([...model.workflows.keys()]).toEqual([18638])
    expect(model.workflows.get(18638)).toMatchObject({ events: 2, ending: 'open' })
    expect([...model.workflows.get(18638)!.members]).toEqual(['Alpha/Developer'])
  })

  it('leaves a trigger fire after it to begin the workflow, as an instruction to a member', () => {
    const model = new MonitorModel()
    model.apply(upload(18688))
    expect(model.workflows.has(18688)).toBe(false)

    const effects = model.apply(tell(18688, 'trigger:notes'))

    expect(effects).toContainEqual(expect.objectContaining({ kind: 'comet', from: { kind: 'origin', team: 'Alpha', origin: 'trigger' } }))
    expect([...model.workflows.get(18688)!.members]).toEqual(['Alpha/Developer'])
    expect(model.counts().open).toBe(1)
  })
})

describe('the scene', () => {
  it('draws no node for a workflow with no members, and draws it when its first member row arrives', () => {
    const model = new MonitorModel()
    // A model built from older rows: a workflow nobody works.
    model.workflows.set(18627, { id: 18627, team: 'Alpha', title: '', item: null, members: new Set(), openedAt: 0, endedAt: null, ending: 'open', events: 1 })
    const made = scene(model)
    expect(made.drawnWorkflows).toEqual([])

    model.apply(tell(18627))
    made.sync(model)
    expect(made.drawnWorkflows).toEqual([18627])
  })
})

describe('a replay', () => {
  it('draws no orphan over an upload row and an instruction row', () => {
    const rows = [upload(18627, '2026-10-19T10:00:00Z'), tell(18700, 'person@example.com', '2026-10-19T10:00:05Z')]
    const clock = new ReplayClock(rows, 10)
    const model = new MonitorModel()
    model.replay = true
    const made = scene(model)

    for (let wall = 0; !clock.done; wall += 100) {
      for (const due of clock.advance(wall === 0 ? 0 : 100)) model.apply(due)
      made.sync(model)
      // Never a workflow node without a member, at any point of the replay.
      for (const id of made.drawnWorkflows) expect(model.workflows.get(id)!.members.size).toBeGreaterThan(0)
    }

    expect(model.ticker.map((line) => line.workflow).sort()).toEqual([18627, 18700])
    expect(made.drawnWorkflows).toEqual([18700])
    expect(model.counts().open).toBe(1)
  })
})
