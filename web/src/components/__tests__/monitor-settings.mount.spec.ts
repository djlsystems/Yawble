// @vitest-environment happy-dom
//
// THE MONITOR'S SETTINGS: the gear on its toolbar opens a small panel - show changes plugins make,
// how long each shows, and one switch per plugin that declares a hint - remembered in this
// browser. The canvas is not drawn by happy-dom, so the scene is replaced by one that records what
// it is asked to draw; what each row resolves to is `monitorResource.spec.ts`. The same panel holds
// Show file changes and Draw down to (`harness.monitor.fileChanges`); what a push draws at each
// depth is played into a real scene here and pinned in `monitorWorkspace.spec.ts`.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import type { EventDefinition, Message } from '../../api/types'
import { MonitorModel, type MonitorEffect } from '../../lib/monitor/model'
import type { FileSettings, WorkspacePush } from '../../lib/monitor/workspace'
import { LayoutDefaults, LayoutKeyName, type LayoutSettings } from '../../lib/monitor/layout'

const { getMessages, getFileChanges, listEvents, played, fileSettings, layouts } = vi.hoisted(() => ({
  getMessages: vi.fn(),
  getFileChanges: vi.fn(),
  listEvents: vi.fn(),
  played: [] as MonitorEffect[],
  fileSettings: [] as FileSettings[],
  layouts: [] as LayoutSettings[],
}))

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  getMessages,
  getFileChanges,
  listEvents,
}))

vi.mock('../../lib/monitor/font', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  loadMonitorSymbols: () => Promise.resolve(null),
}))

vi.mock('../../lib/monitor/scene', async (importOriginal) => {
  class RecordingScene {
    setAutoFit() {}
    setOptions() {}
    setSymbols() {}
    resize() {}
    start() {}
    sync() {}
    fit() {}
    destroy() {}
    play(effects: readonly MonitorEffect[]) {
      played.push(...effects)
    }
    setFileChanges(settings: FileSettings) {
      fileSettings.push({ ...settings })
    }
    clearFileChanges() {}
    setLayout(layout: LayoutSettings) {
      layouts.push({ ...layout })
    }
  }
  return { ...(await importOriginal<Record<string, unknown>>()), MonitorScene: RecordingScene, readPalette: () => {} }
})

import MonitorView from '../MonitorView.vue'
import { useConsoleStore } from '../../stores/console'
import { resetBody } from '../../test/mountQuasar'
import { SettingsKey } from '../../lib/monitor/hints'
import { FileSettingsKey, fileWatch } from '../../lib/monitor/workspace'

let seq = 0
function row(type: string, source: string, payload: Record<string, unknown>): Message {
  seq++
  return { seq, type, source, payload: JSON.stringify(payload), correlationId: 0, causationSeq: null, depth: 0, occurredAt: '2026-10-15T10:00:00Z' }
}

function event(type: string, monitor: EventDefinition['monitor'] = null): EventDefinition {
  return { type, publisher: 'Agent', highVolume: false, inLedger: false, fields: [], summary: '', monitor }
}

const catalog = [
  event('plugin.azure.sqlCreated', { icon: 'database', action: 'created', label: '{name}' }),
  event('plugin.namecheap.recordSet', { icon: 'dns', actionField: 'change', label: '{host}' }),
  event('plugin.stripe.charged'),
]

const snapshot = (id: string) => ({
  team: 'Infra', id, name: id, agent: `plugin:${id.toLowerCase()}`, kind: 'plugin', state: 'Idle', queueDepth: 0, ceiling: 1,
  subscribes: [], currentCorrelation: null, sinceSeq: 0,
})

let wrapper: VueWrapper | null = null

async function mountMonitor(rows: Message[] = []) {
  getMessages.mockResolvedValue(rows)
  listEvents.mockResolvedValue(catalog)
  const board = useConsoleStore()
  board.$patch({
    teams: [{ id: 'Infra', name: 'Infra', containers: [snapshot('Manager'), snapshot('Azure'), snapshot('Namecheap')] }] as never,
    managerName: 'Manager' as never,
  })
  board.view = 'monitor'
  wrapper = mount(MonitorView, { attachTo: document.body })
  await flushPromises()
  return { wrapper, board }
}

/** A row the board pulls, and the effects the Monitor asked the scene to draw for it. */
async function drawn(board: ReturnType<typeof useConsoleStore>, message: Message): Promise<MonitorEffect[]> {
  played.length = 0
  board.record([message])
  await flushPromises()
  return [...played]
}

const azure = () => row('plugin.azure.sqlCreated', 'Infra/Azure', { name: 'sql-db-maketags' })
const namecheap = () => row('plugin.namecheap.recordSet', 'Infra/Namecheap', { host: 'www', change: 'removed' })

async function openPanel(w: VueWrapper) {
  if (!w.find('[data-monitor-settings-panel]').exists()) await w.find('[data-monitor-settings]').trigger('click')
  return w.find('[data-monitor-settings-panel]')
}

function secondsInput(w: VueWrapper) {
  const found = w.find('[data-monitor-setting="seconds"]')
  return found.element.tagName === 'INPUT' ? found : found.find('input')
}

async function setSeconds(w: VueWrapper, value: string) {
  const input = secondsInput(w)
  await input.setValue(value)
  await input.trigger('change')
  // Leaving the field shows the value kept, as a person sees it.
  await input.trigger('blur')
  await new Promise((resolve) => setTimeout(resolve, 0))
  await flushPromises()
}

beforeEach(() => {
  setActivePinia(createPinia())
  seq = 0
  played.length = 0
  fileSettings.length = 0
  layouts.length = 0
  localStorage.removeItem(LayoutKeyName)
  localStorage.removeItem(SettingsKey)
  localStorage.removeItem(FileSettingsKey)
})

afterEach(() => {
  wrapper?.unmount()
  wrapper = null
  resetBody()
  vi.restoreAllMocks()
  vi.clearAllMocks()
})

describe('the Monitor settings', () => {
  it('opens from a gear after Keep my view, with the defaults: on, 30 seconds, every hinted plugin on', async () => {
    const { wrapper } = await mountMonitor()
    const tools = wrapper.findAll('.monitor-controls [data-monitor-keep-view], .monitor-controls [data-monitor-settings]')
    expect(tools.map((tool) => (tool.attributes('data-monitor-keep-view') !== undefined ? 'keep' : 'gear'))).toEqual(['keep', 'gear'])

    const panel = await openPanel(wrapper)

    expect(panel.find('[data-monitor-setting="show"]').attributes('aria-checked')).toBe('true')
    expect((secondsInput(wrapper).element as HTMLInputElement).value).toBe('30')
    const plugins = panel.findAll('[data-monitor-plugin]')
    expect(plugins.map((found) => found.attributes('data-monitor-plugin'))).toEqual(['azure', 'namecheap'])
    expect(plugins.map((found) => found.attributes('aria-checked'))).toEqual(['true', 'true'])
  })

  it('draws a hinted row as the thing it acted on, for 30 seconds, and says it in words', async () => {
    const { wrapper, board } = await mountMonitor()

    const effects = await drawn(board, azure())

    expect(effects).toEqual([
      {
        kind: 'resource',
        at: { kind: 'member', key: 'Infra/Azure' },
        resource: { plugin: 'azure', icon: 'database', action: 'created', label: 'sql-db-maketags' },
        ttlMs: 30_000,
      },
    ])
    await new Promise((resolve) => setTimeout(resolve, 250))
    await flushPromises()
    const words = wrapper.find('[data-monitor-resource]')
    expect(words.text()).toBe('Azure (Infra) created sql-db-maketags')
    expect(words.attributes('data-action')).toBe('created')
    expect(wrapper.find('.monitor-row-verb').text()).toBe('created')
  })

  it('draws hinted rows as the plain burst with Show changes off, and the list still says them', async () => {
    const { wrapper, board } = await mountMonitor()
    const panel = await openPanel(wrapper)

    await panel.find('[data-monitor-setting="show"]').trigger('click')

    expect(panel.find('[data-monitor-setting="show"]').attributes('aria-checked')).toBe('false')
    expect((await drawn(board, azure())).map((effect) => effect.kind)).toEqual(['burst'])
    await new Promise((resolve) => setTimeout(resolve, 250))
    await flushPromises()
    expect(wrapper.find('[data-monitor-resource]').text()).toBe('Azure (Infra) created sql-db-maketags')
  })

  it('shows each for the seconds asked, kept between 5 and 300', async () => {
    const { wrapper, board } = await mountMonitor()
    await openPanel(wrapper)
    const ttl = async () => {
      const [effect] = await drawn(board, azure())
      return effect?.kind === 'resource' ? effect.ttlMs : null
    }

    await setSeconds(wrapper, '120')
    expect(await ttl()).toBe(120_000)

    await setSeconds(wrapper, '1')
    expect(await ttl()).toBe(5_000)
    expect((secondsInput(wrapper).element as HTMLInputElement).value).toBe('5')

    await setSeconds(wrapper, '9999')
    expect(await ttl()).toBe(300_000)
  })

  it("draws one plugin's rows as the plain burst when its switch is off, and the others as before", async () => {
    const { wrapper, board } = await mountMonitor()
    const panel = await openPanel(wrapper)

    await panel.find('[data-monitor-plugin="azure"]').trigger('click')

    expect(panel.find('[data-monitor-plugin="azure"]').attributes('aria-checked')).toBe('false')
    expect((await drawn(board, azure())).map((effect) => effect.kind)).toEqual(['burst'])
    const [other] = await drawn(board, namecheap())
    expect(other).toMatchObject({ kind: 'resource', resource: { plugin: 'namecheap', icon: 'dns', action: 'deleted', label: 'www' } })
  })

  it('remembers them in this browser across a remount', async () => {
    const first = await mountMonitor()
    let panel = await openPanel(first.wrapper)
    await panel.find('[data-monitor-setting="show"]').trigger('click')
    await setSeconds(first.wrapper, '45')
    await panel.find('[data-monitor-plugin="namecheap"]').trigger('click')
    first.wrapper.unmount()
    wrapper = null

    const again = await mountMonitor()
    panel = await openPanel(again.wrapper)

    expect(panel.find('[data-monitor-setting="show"]').attributes('aria-checked')).toBe('false')
    expect((secondsInput(again.wrapper).element as HTMLInputElement).value).toBe('45')
    expect(panel.find('[data-monitor-plugin="azure"]').attributes('aria-checked')).toBe('true')
    expect(panel.find('[data-monitor-plugin="namecheap"]').attributes('aria-checked')).toBe('false')

    await panel.find('[data-monitor-setting="show"]').trigger('click')
    const [effect] = await drawn(again.board, azure())
    expect(effect).toMatchObject({ kind: 'resource', ttlMs: 45_000 })
    expect((await drawn(again.board, namecheap())).map((made) => made.kind)).toEqual(['burst'])
  })

  it('renders and works when storage throws', async () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('SecurityError')
    })
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('QuotaExceededError')
    })
    const { wrapper, board } = await mountMonitor()
    const panel = await openPanel(wrapper)

    expect(panel.find('[data-monitor-setting="show"]').attributes('aria-checked')).toBe('true')
    expect((secondsInput(wrapper).element as HTMLInputElement).value).toBe('30')

    await setSeconds(wrapper, '60')
    expect(await drawn(board, azure())).toMatchObject([{ kind: 'resource', ttlMs: 60_000 }])

    await panel.find('[data-monitor-plugin="azure"]').trigger('click')
    expect((await drawn(board, azure())).map((effect) => effect.kind)).toEqual(['burst'])

    await panel.find('[data-monitor-setting="show"]').trigger('click')
    expect((await drawn(board, namecheap())).map((effect) => effect.kind)).toEqual(['burst'])
  })
})

// --- file changes ---------------------------------------------------------------------------------

/** A push as the Host sends it on `workspaceChanged`, for a member of the mounted team. */
const workspace = (): WorkspacePush => ({
  team: 'Infra',
  member: 'Azure',
  repo: 'web',
  at: '2026-10-15T10:01:00Z',
  files: [
    { path: 'src/lib/parser.ts', project: 'harness-web', action: 'changed' },
    { path: 'src/lib/lexer.ts', project: 'harness-web', action: 'created' },
    { path: 'cli/main.go', project: 'cli', action: 'deleted' },
  ],
  commits: [],
  more: false,
})

/** A push heard on the hub while the Monitor is open, and the effects it asked the scene to draw. */
async function pushed(): Promise<MonitorEffect[]> {
  played.length = 0
  fileWatch.deliver(workspace())
  await flushPromises()
  return [...played]
}

/** What those effects draw in a real scene told the settings the Monitor last gave its own. */
async function drawnKinds(effects: MonitorEffect[]): Promise<string[]> {
  const { MonitorScene } = await vi.importActual<typeof import('../../lib/monitor/scene')>('../../lib/monitor/scene')
  const scene = new MonitorScene(document.createElement('canvas'), { fontPx: 12, fontFamily: 'sans-serif', reducedMotion: false })
  const model = new MonitorModel()
  model.member('Infra', 'Azure', 'Azure', 'plugin')
  scene.setFileChanges(fileSettings.at(-1)!)
  scene.sync(model)
  scene.step(0)
  scene.play(effects)
  scene.step(50)
  return scene.shownWorkspace().nodes.map((node) => node.kind)
}

function depthButtons(panel: ReturnType<VueWrapper['find']>) {
  return panel.find('[data-monitor-setting="depth"]').findAll('button')
}

async function chooseDepth(panel: ReturnType<VueWrapper['find']>, words: string) {
  const button = depthButtons(panel).find((found) => found.text() === words)!
  await button.trigger('click')
  await flushPromises()
}

const pressed = (panel: ReturnType<VueWrapper['find']>) =>
  depthButtons(panel)
    .filter((found) => found.attributes('aria-pressed') === 'true')
    .map((found) => found.text())

async function listLines(w: VueWrapper): Promise<string[]> {
  await new Promise((resolve) => setTimeout(resolve, 250))
  await flushPromises()
  return w.findAll('[data-monitor-push] li').map((found) => found.text().replace(/\s+/g, ' ').trim())
}

describe('the Monitor settings for file changes', () => {
  it('defaults to Show file changes on, drawn down to the file', async () => {
    const { wrapper } = await mountMonitor()
    const panel = await openPanel(wrapper)

    expect(panel.find('[data-monitor-setting="files"]').attributes('aria-checked')).toBe('true')
    expect(depthButtons(panel).map((found) => found.text())).toEqual(['Repository', 'Project', 'File'])
    expect(pressed(panel)).toEqual(['File'])
    expect(fileSettings.at(-1)).toEqual({ show: true, depth: 'file' })

    const effects = await pushed()
    expect(effects).toMatchObject([{ kind: 'workspace', at: { kind: 'member', key: 'Infra/Azure' }, ttlMs: 30_000 }])
    expect(await drawnKinds(effects)).toEqual(['repository', 'project', 'file', 'file', 'project', 'file'])
    expect(await listLines(wrapper)).toEqual(['~ src/lib/parser.ts', '+ src/lib/lexer.ts', '− cli/main.go'])
  })

  it('draws the repository alone, then its projects, then every file, as Draw down to says', async () => {
    const { wrapper } = await mountMonitor()
    const panel = await openPanel(wrapper)

    await chooseDepth(panel, 'Repository')
    expect(pressed(panel)).toEqual(['Repository'])
    expect(await drawnKinds(await pushed())).toEqual(['repository'])

    await chooseDepth(panel, 'Project')
    expect(pressed(panel)).toEqual(['Project'])
    expect(await drawnKinds(await pushed())).toEqual(['repository', 'project', 'project'])

    await chooseDepth(panel, 'File')
    expect(await drawnKinds(await pushed())).toEqual(['repository', 'project', 'file', 'file', 'project', 'file'])
  })

  it('draws none of it and lists none of it with Show file changes off, with the plugin changes left alone', async () => {
    const { wrapper, board } = await mountMonitor()
    const panel = await openPanel(wrapper)

    await panel.find('[data-monitor-setting="files"]').trigger('click')

    expect(panel.find('[data-monitor-setting="files"]').attributes('aria-checked')).toBe('false')
    expect(fileSettings.at(-1)).toEqual({ show: false, depth: 'file' })
    expect(await pushed()).toEqual([])
    expect(await listLines(wrapper)).toEqual([])
    expect((await drawn(board, azure())).map((effect) => effect.kind)).toEqual(['resource'])
  })

  it('keeps the plugin changes time to live, with no second duration control', async () => {
    const { wrapper } = await mountMonitor()
    const panel = await openPanel(wrapper)

    await setSeconds(wrapper, '45')

    expect(panel.findAll('input[type="number"]')).toHaveLength(1)
    expect(await pushed()).toMatchObject([{ kind: 'workspace', ttlMs: 45_000 }])
  })

  it('remembers them in this browser across a remount', async () => {
    const first = await mountMonitor()
    let panel = await openPanel(first.wrapper)
    await chooseDepth(panel, 'Project')
    await panel.find('[data-monitor-setting="files"]').trigger('click')
    first.wrapper.unmount()
    wrapper = null

    expect(JSON.parse(localStorage.getItem(FileSettingsKey)!)).toEqual({ show: false, depth: 'project' })

    const again = await mountMonitor()
    panel = await openPanel(again.wrapper)

    expect(panel.find('[data-monitor-setting="files"]').attributes('aria-checked')).toBe('false')
    expect(pressed(panel)).toEqual(['Project'])
    expect(await pushed()).toEqual([])

    await panel.find('[data-monitor-setting="files"]').trigger('click')
    expect(await drawnKinds(await pushed())).toEqual(['repository', 'project', 'project'])
  })

  it('renders and works when storage throws', async () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('SecurityError')
    })
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('QuotaExceededError')
    })
    const { wrapper } = await mountMonitor()
    const panel = await openPanel(wrapper)

    expect(panel.find('[data-monitor-setting="files"]').attributes('aria-checked')).toBe('true')
    expect(pressed(panel)).toEqual(['File'])

    await chooseDepth(panel, 'Repository')
    expect(await drawnKinds(await pushed())).toEqual(['repository'])

    await panel.find('[data-monitor-setting="files"]').trigger('click')
    expect(await pushed()).toEqual([])
  })

  it('leaves the file watch when the Monitor closes', async () => {
    const mounted = await mountMonitor()
    mounted.wrapper.unmount()
    wrapper = null
    played.length = 0

    fileWatch.deliver(workspace())
    await flushPromises()
    expect(played).toEqual([])
  })
})

// --- the replay of recorded file changes -------------------------------------------------------------

const at = (seconds: number) => new Date(Date.UTC(2026, 9, 15, 10, 0, seconds)).toISOString()

/** The period the Monitor holds: Azure's run, ten seconds long. */
const period = (): Message[] => [
  { ...row('agentContainer.started', 'Infra/Azure', {}), occurredAt: at(0) },
  { ...row('agentContainer.completed', 'Infra/Azure', {}), occurredAt: at(10) },
]

/** What the Host recorded in it, two pages of the route: a change, then a commit. */
function recordedPages() {
  const first = { id: 11, correlation: 3, ...workspace(), at: at(2) }
  const second = {
    id: 12,
    correlation: 3,
    ...workspace(),
    at: at(5),
    files: [],
    commits: [{ sha: '9f8e7d6c5b4a', subject: 'Add the lexer', files: [{ path: 'src/lib/lexer.ts', project: 'harness-web', action: 'created' }] }],
  }
  getFileChanges.mockImplementation((_period: unknown, after: number) =>
    Promise.resolve(after === 0 ? { changes: [first], next: 11 } : { changes: [second], next: null }),
  )
}

/** Switches to Replay and lets it play the period through: ten log seconds at 60x is under a fifth of a second. */
async function replayed(w: VueWrapper): Promise<MonitorEffect[]> {
  played.length = 0
  await w.findAll('.monitor-mode .q-btn')[1]!.trigger('click')
  await flushPromises()
  await new Promise((resolve) => setTimeout(resolve, 400))
  await flushPromises()
  return played.filter((effect) => effect.kind === 'workspace')
}

describe('the Monitor replay of recorded file changes', () => {
  it('reads the period page by page and draws each change at its time, and the activity list says them', async () => {
    recordedPages()
    const { wrapper } = await mountMonitor(period())

    const effects = await replayed(wrapper)

    expect(getFileChanges.mock.calls.map(([asked, after]) => [asked, after])).toEqual([
      [{ from: at(0), to: at(10) }, 0],
      [{ from: at(0), to: at(10) }, 11],
    ])
    expect(effects.map((effect) => effect.kind === 'workspace' && [effect.push.at, effect.at])).toEqual([
      [at(2), { kind: 'member', key: 'Infra/Azure' }],
      [at(5), { kind: 'member', key: 'Infra/Azure' }],
    ])
    expect(await drawnKinds(effects)).toEqual(['repository', 'project', 'file', 'file', 'project', 'file'])
    expect(wrapper.find('[data-monitor-mode="replay"]').exists()).toBe(true)
    expect(await listLines(wrapper)).toEqual(['+ src/lib/lexer.ts', '~ src/lib/parser.ts', '+ src/lib/lexer.ts', '− cli/main.go'])
    expect(wrapper.text()).toContain('9f8e7d6 Add the lexer')
  })

  it('draws down only as far as Draw down to says', async () => {
    recordedPages()
    const { wrapper } = await mountMonitor(period())
    await chooseDepth(await openPanel(wrapper), 'Project')

    expect(await drawnKinds(await replayed(wrapper))).toEqual(['repository', 'project', 'project'])
  })

  it('draws and lists none of it with Show file changes off', async () => {
    recordedPages()
    const { wrapper } = await mountMonitor(period())
    await (await openPanel(wrapper)).find('[data-monitor-setting="files"]').trigger('click')

    expect(await replayed(wrapper)).toEqual([])
    expect(await listLines(wrapper)).toEqual([])
  })

  it('plays the rows without file changes when they cannot be read', async () => {
    getFileChanges.mockRejectedValue(new Error('503 Service Unavailable'))
    const { wrapper } = await mountMonitor(period())

    expect(await replayed(wrapper)).toEqual([])
    expect(played.length).toBeGreaterThan(0)
  })
})

describe('the Monitor layout settings', () => {
  const slider = (panel: ReturnType<VueWrapper['find']>, key: string) =>
    panel.find(`[data-monitor-layout="${key}"]`).findComponent({ name: 'QSlider' })

  it('lists link distance, link strength, repulsion, gravity and friction at the defaults, and hands them to the scene', async () => {
    const { wrapper } = await mountMonitor()
    const panel = await openPanel(wrapper)

    expect(panel.findAll('[data-monitor-layout]').map((found) => found.attributes('data-monitor-layout'))).toEqual([
      'distance', 'strength', 'repulsion', 'gravity', 'friction',
    ])
    expect(layouts.at(-1)).toEqual(LayoutDefaults)
    expect(panel.find('[data-monitor-layout-reset]').attributes('disabled')).toBeDefined()
  })

  it('moves the scene and remembers a change in this browser, and Reset puts the defaults back', async () => {
    const { wrapper } = await mountMonitor()
    let panel = await openPanel(wrapper)

    slider(panel, 'distance').vm.$emit('update:modelValue', 2)
    slider(panel, 'repulsion').vm.$emit('update:modelValue', 0.5)
    await flushPromises()
    expect(layouts.at(-1)).toEqual({ ...LayoutDefaults, distance: 2, repulsion: 0.5 })
    expect(JSON.parse(localStorage.getItem(LayoutKeyName)!)).toEqual({ ...LayoutDefaults, distance: 2, repulsion: 0.5 })
    expect(panel.find('[data-monitor-layout="distance"]').text()).toContain('×2.00')

    wrapper.unmount()
    const again = await mountMonitor()
    expect(layouts.at(-1)).toEqual({ ...LayoutDefaults, distance: 2, repulsion: 0.5 })

    panel = await openPanel(again.wrapper)
    await panel.find('[data-monitor-layout-reset]').trigger('click')
    expect(layouts.at(-1)).toEqual(LayoutDefaults)
    expect(JSON.parse(localStorage.getItem(LayoutKeyName)!)).toEqual(LayoutDefaults)
  })

  it('works with storage refusing', async () => {
    const getItem = vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('denied')
    })
    const setItem = vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('denied')
    })
    try {
      const { wrapper } = await mountMonitor()
      const panel = await openPanel(wrapper)
      slider(panel, 'gravity').vm.$emit('update:modelValue', 1.5)
      await flushPromises()
      expect(layouts.at(-1)).toEqual({ ...LayoutDefaults, gravity: 1.5 })
    } finally {
      getItem.mockRestore()
      setItem.mockRestore()
    }
  })
})
