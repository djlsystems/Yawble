// @vitest-environment happy-dom
//
// THE MONITOR'S SETTINGS: the gear on its toolbar opens a small panel - show changes plugins make,
// how long each shows, and one switch per plugin that declares a hint - remembered in this
// browser. The canvas is not drawn by happy-dom, so the scene is replaced by one that records what
// it is asked to draw; what each row resolves to is `monitorResource.spec.ts`.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import type { EventDefinition, Message } from '../../api/types'
import type { MonitorEffect } from '../../lib/monitor/model'

const { getMessages, listEvents, played } = vi.hoisted(() => ({
  getMessages: vi.fn(),
  listEvents: vi.fn(),
  played: [] as MonitorEffect[],
}))

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  getMessages,
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
  }
  return { ...(await importOriginal<Record<string, unknown>>()), MonitorScene: RecordingScene, readPalette: () => {} }
})

import MonitorView from '../MonitorView.vue'
import { useConsoleStore } from '../../stores/console'
import { resetBody } from '../../test/mountQuasar'
import { SettingsKey } from '../../lib/monitor/hints'

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

async function mountMonitor() {
  getMessages.mockResolvedValue([])
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
  localStorage.removeItem(SettingsKey)
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
