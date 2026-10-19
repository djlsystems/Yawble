// @vitest-environment happy-dom
//
// THE MONITOR NAMES A WORKFLOW BY ITS WORK. A workflow a `backlog.item.dispatched` row opened is
// titled from that row - the item's citation, a separator, its title - whatever the instruction
// says; the dispatch names no member, so the workflow is begun by the first member row (UX 8) and
// takes the item's name then. The workflow node's label and the member tooltip's workflow line read the name, then
// the number. Citations are built with `itemLabel` over a number, never written out.
import { describe, expect, it } from 'vitest'
import type { Message } from '../../api/types'
import { itemLabel } from '../backlog'
import { ItemSeparator, MonitorModel, workflowNameLine } from '../monitor/model'
import { MonitorScene } from '../monitor/scene'

let seq = 18000

function row(type: string, source: string, payload: Record<string, unknown>, correlationId: number): Message {
  seq++
  return { seq, type, source, payload: JSON.stringify(payload), correlationId, causationSeq: null, depth: 0, occurredAt: '2026-10-19T10:00:00Z' }
}

const ITEM = 197
const WORKFLOW = 18195

const dispatched = () => row('backlog.item.dispatched', 'person@example.com', { item: ITEM, team: 'Alpha', title: 'Name the work' }, WORKFLOW)
const tell = (to: string, subject: string) => row(`agentContainer.instruction.${to}`, 'Alpha/Manager', { instruction: subject, subject }, WORKFLOW)
const started = (member: string) => row('agentContainer.started', `Alpha/${member}`, {}, WORKFLOW)

const named = `${itemLabel(ITEM)}${ItemSeparator}Name the work`

describe('a workflow begun by a dispatch row', () => {
  it('is titled by its item when the first member row begins it, the dispatch alone begins nothing', () => {
    const model = new MonitorModel()
    model.apply(dispatched())
    expect(model.workflows.has(WORKFLOW)).toBe(false)

    model.apply(tell('Alpha/Manager', 'Work the item'))
    const workflow = model.workflows.get(WORKFLOW)!
    expect(workflow.title).toBe(named)
    expect(workflow.item).toEqual({ id: ITEM, title: 'Name the work' })
  })

  it('keeps the item title when an instruction arrives after it', () => {
    const model = new MonitorModel()
    model.apply(dispatched())
    model.apply(tell('Alpha/Developer', 'Build the component'))

    expect(model.workflows.get(WORKFLOW)!.title).toBe(named)
  })

  it('is titled by the subject when a tell began it, with no item', () => {
    const model = new MonitorModel()
    model.apply(tell('Alpha/Developer', 'Build the component'))

    const workflow = model.workflows.get(WORKFLOW)!
    expect(workflow.title).toBe('Build the component')
    expect(workflow.item).toBeNull()
  })

  it('reads the name and then the number, and the number alone with no name', () => {
    expect(workflowNameLine({ id: WORKFLOW, title: named })).toBe(`${named} #${WORKFLOW}`)
    expect(workflowNameLine({ id: WORKFLOW, title: '' })).toBe(`#${WORKFLOW}`)
    // The name gives way to a cut; the number never does.
    expect(workflowNameLine({ id: WORKFLOW, title: named }, 8).endsWith(`… #${WORKFLOW}`)).toBe(true)
  })
})

describe('the tooltips', () => {
  function scene(model: MonitorModel) {
    const canvas = document.createElement('canvas')
    document.body.appendChild(canvas)
    const made = new MonitorScene(canvas, { fontPx: 12, fontFamily: 'sans-serif', reducedMotion: true })
    made.sync(model)
    made.step(0)
    return made
  }

  it('says the name and the number on the member tooltip workflow line', () => {
    const model = new MonitorModel()
    model.member('Alpha', 'Manager', 'Manager')
    model.member('Alpha', 'Developer', 'Developer')
    model.apply(dispatched())
    model.apply(tell('Alpha/Developer', 'Build the component'))
    model.apply(started('Developer'))

    const info = scene(model).describeNode('m:Alpha/Developer')!
    expect(info.lines).toContain(`workflow ${named} #${WORKFLOW}`)
  })

  it('says the number alone on the member line when the workflow has no name', () => {
    const model = new MonitorModel()
    model.member('Alpha', 'Developer', 'Developer')
    model.apply(started('Developer'))

    const info = scene(model).describeNode('m:Alpha/Developer')!
    expect(info.lines).toContain(`workflow #${WORKFLOW}`)
  })

  it('titles the workflow node by its item, with its number under it', () => {
    const model = new MonitorModel()
    model.member('Alpha', 'Manager', 'Manager')
    model.apply(dispatched())
    model.apply(tell('Alpha/Manager', 'Work the item'))

    const info = scene(model).describeNode(`w:${WORKFLOW}`)!
    expect(info.title).toBe(named)
    expect(info.subtitle).toContain(`workflow ${WORKFLOW}`)
  })
})
