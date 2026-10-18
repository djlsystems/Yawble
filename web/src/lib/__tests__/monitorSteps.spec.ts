// @vitest-environment happy-dom
//
// AGENT STEPS ON THE MONITOR: each step a running member takes (a tool call, a result, its own text,
// a user line, a note) is a small icon that rises off the member's node and fades, the icon by kind
// and tool; a burst keeps at most a few on a node, the newest; nothing goes in the activity list; a
// replay reads the recorded steps page by page and plays each at its time. The settings panel and
// the replay bar's sentence are pinned in `monitor-settings.mount.spec.ts`.
import { readFileSync } from 'node:fs'
import { join } from 'node:path'
import { describe, expect, it, vi } from 'vitest'
import type { Message } from '../../api/types'
import { MonitorModel, ReplayClock } from '../monitor/model'
import { MonitorScene } from '../monitor/scene'
import {
  MaxRecordedSteps,
  MaxStepIconsEach,
  StepIcons,
  StepMs,
  StepSettingsKey,
  readRecordedSteps,
  readStep,
  readStepSettings,
  readStepsPush,
  stepIcon,
  writeStepSettings,
  type AgentStep,
} from '../monitor/steps'
import { FileWatch } from '../monitor/workspace'

const at = (seconds: number) => new Date(Date.UTC(2026, 9, 15, 10, 0, seconds)).toISOString()

let id = 0
function step(over: Partial<AgentStep> = {}): AgentStep {
  id++
  return { id, team: 'Core', member: 'Builder', run: 7, workflow: 3, at: at(1), kind: 'tool', label: 'Bash', mcpTool: null, count: 1, ...over }
}

describe('a step as the wire sends it', () => {
  it('reads each field of a step record', () => {
    expect(readStep({ id: 4, team: 'Core', member: 'Builder', run: 9, workflow: null, at: at(2), kind: 'tool', label: 'MCP', mcpTool: 'kanban', count: 3 })).toEqual({
      id: 4, team: 'Core', member: 'Builder', run: 9, workflow: null, at: at(2), kind: 'tool', label: 'MCP', mcpTool: 'kanban', count: 3,
    })
  })

  it('passes over anything that is not a step, and keeps a label and an MCP tool only where they belong', () => {
    expect(readStep(null)).toBeNull()
    expect(readStep({ team: 'Core', member: 'Builder', kind: 'thought' })).toBeNull()
    expect(readStep({ team: '', member: 'Builder', kind: 'text' })).toBeNull()
    expect(readStep({ team: 'Core', member: 'Builder', kind: 'text', label: 'Bash', mcpTool: 'tell' })).toMatchObject({ label: '', mcpTool: null, count: 1 })
    expect(readStep({ team: 'Core', member: 'Builder', kind: 'tool', label: 'Bash', mcpTool: 'tell', count: 0 })).toMatchObject({ mcpTool: null, count: 1 })
  })

  it('reads a push of one member, its steps taking its team and member, and drops a step of another', () => {
    const push = readStepsPush({
      team: 'Core',
      member: 'Builder',
      steps: [{ kind: 'tool', label: 'Edit', at: at(1) }, { kind: 'text', at: at(2) }, { team: 'Other', member: 'Builder', kind: 'text' }, 'junk'],
    })

    expect(push?.steps.map((s) => [s.team, s.member, s.kind, s.label])).toEqual([
      ['Core', 'Builder', 'tool', 'Edit'],
      ['Core', 'Builder', 'text', ''],
    ])
    expect(readStepsPush({ team: 'Core', member: 'Builder' })).toBeNull()
  })
})

describe('the icon a step draws', () => {
  it('is the one for its kind and tool, and the gear for a tool it does not know', () => {
    const tool = (label: string) => stepIcon({ kind: 'tool', label })

    expect(tool('Bash')).toBe('terminal')
    for (const label of ['Edit', 'Write', 'MultiEdit']) expect(tool(label)).toBe('edit')
    for (const label of ['Read', 'Grep', 'Glob', 'LS']) expect(tool(label)).toBe('search')
    expect(tool('MCP')).toBe('power')
    expect(tool('Skill')).toBe('menu_book')
    expect(tool('TodoWrite')).toBe('checklist')
    for (const label of ['WebFetch', 'WebSearch']) expect(tool(label)).toBe('language')
    for (const label of ['Task', 'Agent']) expect(tool(label)).toBe('account_tree')
    expect(tool('NotebookEdit')).toBe('settings')
    expect(tool('constructor')).toBe('settings')
    expect(stepIcon({ kind: 'text', label: '' })).toBe('chat_bubble')
    expect(stepIcon({ kind: 'note', label: '' })).toBe('sticky_note_2')
    expect(stepIcon({ kind: 'result', label: '' })).toBe('check_small')
    expect(stepIcon({ kind: 'user', label: '' })).toBe('person')
  })

  it('is always a Material Symbols name the bundled font has', () => {
    const names = new Set(readFileSync(join(__dirname, '../../../../cli/internal/plugin/material-symbols.txt'), 'utf8').split('\n'))
    for (const icon of StepIcons) expect(names.has(icon), icon).toBe(true)
  })
})

/** A scene with two members of team Core, driven by hand. */
function scene(reducedMotion = false) {
  const canvas = document.createElement('canvas')
  document.body.appendChild(canvas)
  const made = new MonitorScene(canvas, { fontPx: 12, fontFamily: 'sans-serif', reducedMotion })
  made.resize(800, 600, 1)
  const model = new MonitorModel()
  model.member('Core', 'Builder')
  model.member('Core', 'Tester')
  made.sync(model)
  let now = 0
  made.step(now)
  const until = (ms: number) => {
    while (now < ms) made.step((now = Math.min(ms, now + 50)))
  }
  return { made, model, until, canvas }
}

/** How far over the middle of its member's body a step's icon is now. */
function heightOver(made: MonitorScene, model: MonitorModel, key: string, y: number): number {
  const body = bodyOf(made, model, key)
  return (body.top + body.bottom) / 2 - y
}

/** The member's body as the scene places it: the first box `memberBoxes` gives for it. */
function bodyOf(made: MonitorScene, model: MonitorModel, key: string) {
  const boxes = made.memberBoxes()
  // The team's box comes first, then each member's body and its name, in the model's order.
  const index = [...model.members.keys()].indexOf(key)
  return boxes[1 + index * 2]!
}

describe('a step in the scene', () => {
  it('draws its icon over its own member, rising and fading, and gone within a couple of seconds', () => {
    const { made, model, until } = scene()
    made.play(model.applySteps([step({ member: 'Tester', label: 'Edit' })]))

    until(100)
    const [first] = made.shownSteps()
    const body = bodyOf(made, model, 'Core/Tester')
    expect(first).toMatchObject({ at: { kind: 'member', key: 'Core/Tester' }, icon: 'edit', count: null, caption: null })
    expect(first!.x).toBeGreaterThanOrEqual(body.left)
    expect(first!.x).toBeLessThanOrEqual(body.right)
    expect(first!.y).toBeLessThan(body.top + 6)

    const startHeight = heightOver(made, model, 'Core/Tester', first!.y)
    until(1000)
    const later = made.shownSteps()[0]!
    expect(heightOver(made, model, 'Core/Tester', later.y)).toBeGreaterThan(startHeight + 10)
    expect(later.alpha).toBeLessThan(1)
    expect(later.alpha).toBeGreaterThan(0)

    until(StepMs + 50)
    expect(made.shownSteps()).toEqual([])
    expect(StepMs).toBeLessThanOrEqual(2000)
  })

  it('neither rises nor fades under reduced motion', () => {
    const { made, model, until } = scene(true)
    made.play(model.applySteps([step()]))

    until(100)
    const first = heightOver(made, model, 'Core/Builder', made.shownSteps()[0]!.y)
    until(1200)
    const later = made.shownSteps()[0]!
    expect(heightOver(made, model, 'Core/Builder', later.y)).toBeCloseTo(first)
    expect(later.alpha).toBe(1)
  })

  it('draws a dot until the font is here, then the icon', () => {
    const { made, model, until } = scene()
    made.play(model.applySteps([step({ label: 'Bash' })]))
    until(50)

    expect(made.shownSteps()[0]!.glyph).toBeNull()
    made.setSymbols({ terminal: 0xeb8e })
    expect(made.shownSteps()[0]!.glyph).toBe(String.fromCodePoint(0xeb8e))
  })

  it(`keeps at most ${MaxStepIconsEach} on one node in a burst, the newest, each in its own place`, () => {
    const { made, model, until } = scene()
    const labels = ['Bash', 'Read', 'Edit', 'Skill', 'TodoWrite', 'WebFetch', 'Task', 'MCP']
    made.play(model.applySteps(labels.map((label) => step({ label }))))
    made.play(model.applySteps([step({ member: 'Tester', kind: 'text', label: '' })]))
    until(50)

    const builder = made.shownSteps().filter((shown) => shown.at.kind === 'member' && shown.at.key === 'Core/Builder')
    expect(MaxStepIconsEach).toBe(3)
    expect(builder.map((shown) => shown.icon)).toEqual(['language', 'account_tree', 'power'])
    expect(new Set(builder.map((shown) => shown.x)).size).toBe(builder.length)
    // Another member's step is its own, not crowded out.
    expect(made.shownSteps().filter((shown) => shown.at.kind === 'member' && shown.at.key === 'Core/Tester')).toHaveLength(1)
  })

  it('shows a count above 1 as a small number on its icon', () => {
    const { made, model, until } = scene()
    made.play(model.applySteps([step({ count: 12 }), step({ count: 1, kind: 'result', label: '' })]))
    until(50)

    expect(made.shownSteps().map((shown) => shown.count)).toEqual([12, null])
  })

  it("captions an MCP step with its tool only when the node is drawn large enough", () => {
    const { made, model, until, canvas } = scene()
    const zoom = (deltaY: number) => canvas.dispatchEvent(new WheelEvent('wheel', { deltaY, clientX: 400, clientY: 300 }))

    zoom(800)
    made.play(model.applySteps([step({ label: 'MCP', mcpTool: 'kanban' })]))
    until(50)
    expect(made.shownSteps()[0]!.caption).toBeNull()

    zoom(-1600)
    expect(made.shownSteps()[0]!.caption).toBe('kanban')
    // A tool that is not MCP has no caption, however large.
    made.play(model.applySteps([step({ label: 'Bash' })]))
    expect(made.shownSteps()[1]!.caption).toBeNull()
  })

  it('draws none with Show agent steps off, and clears those showing', () => {
    const { made, model, until } = scene()
    made.play(model.applySteps([step()]))
    until(50)
    made.setAgentSteps(false)

    expect(made.shownSteps()).toEqual([])
    made.play([{ kind: 'step', at: { kind: 'member', key: 'Core/Builder' }, step: step() }])
    expect(made.shownSteps()).toEqual([])
  })
})

describe('the model and a step', () => {
  it('makes one step effect on the member for each, and adds nothing to the activity list', () => {
    const model = new MonitorModel()
    model.member('Core', 'Builder')
    const before = [...model.ticker]

    const effects = model.applySteps([step(), step({ kind: 'text', label: '' })])

    expect(effects.map((effect) => effect.kind === 'step' && [effect.at, effect.step.kind])).toEqual([
      [{ kind: 'member', key: 'Core/Builder' }, 'tool'],
      [{ kind: 'member', key: 'Core/Builder' }, 'text'],
    ])
    expect(model.ticker).toEqual(before)
  })

  it('makes none with the setting off, for a team it does not show, or for a member it has no node for', () => {
    const model = new MonitorModel('Manager', 80, new Set(['Core']))
    model.member('Core', 'Builder')

    expect(model.applySteps([step({ team: 'Other' })])).toEqual([])
    expect(model.applySteps([step({ member: 'Nobody' })])).toEqual([])
    model.stepSettings = { show: false }
    expect(model.applySteps([step()])).toEqual([])
  })
})

describe('the recorded steps, for a replay', () => {
  it('reads every page, oldest first, until the route says there are no more', async () => {
    const pages = vi.fn((after: number, _take: number) =>
      Promise.resolve(after === 0 ? { steps: [step({ at: at(1) }), 'junk'], next: 30 } : { steps: [step({ at: at(2) })], next: null }),
    )

    const read = await readRecordedSteps(pages)

    expect(pages.mock.calls.map(([after, take]) => [after, take])).toEqual([[0, 500], [30, 500]])
    expect(read.steps.map((s) => s.at)).toEqual([at(1), at(2)])
    expect(read.cut).toBe(false)
  })

  it(`stops at ${MaxRecordedSteps.toLocaleString('en-US')} and says it was cut short, and at a cursor that does not move on`, async () => {
    const full = vi.fn((after: number) => Promise.resolve({ steps: Array.from({ length: 500 }, () => step()), next: after + 500 }))
    const capped = await readRecordedSteps(full)
    expect(full).toHaveBeenCalledTimes(MaxRecordedSteps / 500)
    expect(capped).toMatchObject({ cut: true })
    expect(capped.steps).toHaveLength(MaxRecordedSteps)

    const stuck = await readRecordedSteps(() => Promise.resolve({ steps: [step()], next: 0 }))
    expect(stuck.cut).toBe(true)
  })

  it('plays each step at its own time beside the rows and the file changes', () => {
    const row = (seq: number, seconds: number): Message => ({
      seq, type: 'agentContainer.started', source: 'Core/Builder', payload: '{}', correlationId: 0, causationSeq: null, depth: 0, occurredAt: at(seconds),
    })
    const steps = [step({ at: at(6) }), step({ at: at(2) }), step({ at: at(14) })]
    const clock = new ReplayClock([row(1, 0), row(2, 10)], 1, 20_000, Date.parse(at(0)), [], steps)

    const dueAt = (ms: number) => clock.step(ms)
    expect(dueAt(0).rows.map((r) => r.seq)).toEqual([1])
    expect(dueAt(2000).steps.map((s) => s.at)).toEqual([at(2)])
    expect(dueAt(3000).steps).toEqual([])
    expect(dueAt(1000).steps.map((s) => s.at)).toEqual([at(6)])
    const atTen = dueAt(4000)
    expect([atTen.rows.map((r) => r.seq), atTen.steps]).toEqual([[2], []])
    expect(clock.done).toBe(false)
    expect(dueAt(4000).steps.map((s) => s.at)).toEqual([at(14)])
    expect(clock.done).toBe(true)
  })

  it('starts from a moment with the steps before it passed over', () => {
    const clock = new ReplayClock([], 1, 20_000, Date.parse(at(5)), [], [step({ at: at(2) }), step({ at: at(6) })])

    expect(clock.step(1000).steps.map((s) => s.at)).toEqual([at(6)])
  })
})

describe('Show agent steps', () => {
  it('is on by default and remembered in this browser, and the default stands without storage', () => {
    localStorage.removeItem(StepSettingsKey)
    expect(readStepSettings()).toEqual({ show: true })
    writeStepSettings({ show: false })
    expect(readStepSettings()).toEqual({ show: false })
    localStorage.setItem(StepSettingsKey, '{"show":"no"}')
    expect(readStepSettings()).toEqual({ show: true })
    localStorage.setItem(StepSettingsKey, 'not json')
    expect(readStepSettings()).toEqual({ show: true })
    localStorage.removeItem(StepSettingsKey)
  })
})

describe('the live steps', () => {
  it('join the file watch while anyone hears them, reach each listener, and leave when the last goes', async () => {
    const watch = new FileWatch()
    const invoke = vi.fn((_method: string) => Promise.resolve())
    watch.attach({ invoke })
    const heard: string[] = []

    const stop = watch.listenSteps((push) => heard.push(...push.steps.map((s) => s.label)))
    await Promise.resolve()
    watch.deliverSteps({ team: 'Core', member: 'Builder', steps: [{ kind: 'tool', label: 'Grep', at: at(1) }] })
    watch.deliverSteps({ team: 'Core', member: 'Builder', steps: [] })
    watch.deliverSteps('junk')
    stop()
    await Promise.resolve()
    watch.deliverSteps({ team: 'Core', member: 'Builder', steps: [{ kind: 'tool', label: 'Glob', at: at(2) }] })

    expect(heard).toEqual(['Grep'])
    expect(invoke.mock.calls.map(([method]) => method)).toEqual(['JoinFileWatch', 'LeaveFileWatch'])
  })
})
