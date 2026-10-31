// @vitest-environment happy-dom
//
// THE ACTIVITY PANE, MOUNTED through the real console store. The Clear button is gone: the pane
// folds to a strip instead (a chevron at the right of the ACTIVITY heading), remembered in this
// browser (`harness.monitor.activityOpen`). A mark the former Clear left (`harness.monitor.clearedAfter`)
// is still read, so a Monitor mounted later reads the window from the row after it, and Show
// everything again in the settings dialog forgets it. Nothing goes to the server.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import type { Message } from '../../api/types'

const { getMessages, getFileChanges, listEvents } = vi.hoisted(() => ({ getMessages: vi.fn(), getFileChanges: vi.fn(), listEvents: vi.fn() }))

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

import MonitorView from '../MonitorView.vue'
import { useConsoleStore } from '../../stores/console'
import { resetBody } from '../../test/mountQuasar'
import { ClearedKey } from '../../lib/monitor/cleared'

const ActivityOpenKey = 'harness.monitor.activityOpen'

let seq = 0
function row(type: string, source: string, payload: Record<string, unknown>, at: string, correlationId = 1): Message {
  seq++
  return { seq, type, source, payload: JSON.stringify(payload), correlationId, causationSeq: null, depth: 0, occurredAt: at }
}

const snapshot = (id: string) => ({
  team: 'Alpha', id, name: id, agent: 'claude', state: 'Idle', queueDepth: 0, ceiling: 1, subscribes: [],
  currentCorrelation: null, sinceSeq: 0,
})

const tell = (subject: string, at: string, correlation = 1) =>
  row('agentContainer.instruction.Alpha/Developer', 'Alpha/Manager', { instruction: subject, subject }, at, correlation)

/** Three rows: an instruction, its start, its finished run with tokens. */
function three(): Message[] {
  return [
    tell('Write the skeleton', '2026-10-21T10:00:00Z'),
    row('agentContainer.started', 'Alpha/Developer', {}, '2026-10-21T10:00:01Z'),
    row('agentContainer.completed', 'Alpha/Developer', { tokensIn: 12000, tokensOut: 300 }, '2026-10-21T10:05:00Z'),
  ]
}

let wrapper: VueWrapper | null = null

async function mountMonitor(rows: Message[]) {
  getMessages.mockResolvedValue(rows)
  const board = useConsoleStore()
  board.$patch({
    teams: [{ id: 'Alpha', name: 'Alpha', containers: [snapshot('Manager'), snapshot('Developer')] }] as never,
    managerName: 'Manager' as never,
  })
  board.view = 'monitor'
  wrapper = mount(MonitorView, { attachTo: document.body })
  await flushPromises()
  return { wrapper, board }
}

/** Past the view's next publish, which reads the model into what the page shows. */
async function published() {
  await new Promise((resolve) => setTimeout(resolve, 250))
  await flushPromises()
}

const listed = (w: VueWrapper) => w.findAll('.monitor-row').map((found) => found.find('.monitor-row-verb').text())
const bars = (w: VueWrapper) => w.findAll('.monitor-bar').filter((bar) => !(bar.attributes('style') ?? '').includes('height: 0%'))
const spanHint = (w: VueWrapper) => w.find('.monitor-span-hint').text()

async function openPanel(w: VueWrapper) {
  if (!w.find('[data-monitor-settings-panel]').exists()) await w.find('[data-monitor-settings]').trigger('click')
  return w.find('[data-monitor-settings-panel]')
}

beforeEach(() => {
  setActivePinia(createPinia())
  seq = 0
  listEvents.mockResolvedValue([])
  getFileChanges.mockResolvedValue({ changes: [], next: null })
  localStorage.removeItem(ClearedKey)
  localStorage.removeItem(ActivityOpenKey)
})

afterEach(() => {
  wrapper?.unmount()
  wrapper = null
  resetBody()
  localStorage.removeItem(ClearedKey)
  localStorage.removeItem(ActivityOpenKey)
  vi.clearAllMocks()
})

describe('the activity pane folds to a strip', () => {
  it('has no Clear button: its heading ends in a Hide activity chevron', async () => {
    const { wrapper } = await mountMonitor(three())
    const head = wrapper.get('.monitor-ticker-head')
    const toggle = head.get('[data-monitor-activity-toggle]')

    expect(wrapper.find('[data-monitor-clear]').exists()).toBe(false)
    expect(head.element.firstElementChild?.textContent?.trim()).toBe('Activity')
    expect(head.element.lastElementChild).toBe(toggle.element)
    expect(toggle.attributes('aria-label')).toBe('Hide activity')
    expect(toggle.attributes('aria-expanded')).toBe('true')
  })

  it('folds to a strip and opens again, keeping its rows, and this browser remembers it', async () => {
    const { wrapper } = await mountMonitor(three())
    await published()
    expect(listed(wrapper)).toEqual(['finished', 'started', 'told'])

    await wrapper.get('[data-monitor-activity-toggle]').trigger('click')
    await flushPromises()

    expect(wrapper.find('[data-monitor-activity-collapsed]').exists()).toBe(true)
    expect(wrapper.findAll('.monitor-row')).toEqual([])
    const show = wrapper.get('[data-monitor-activity-toggle]')
    expect(show.attributes('aria-label')).toBe('Show activity')
    expect(show.attributes('aria-expanded')).toBe('false')
    expect(localStorage.getItem(ActivityOpenKey)).toBe('0')

    await show.trigger('click')
    await published()

    expect(wrapper.find('[data-monitor-activity-collapsed]').exists()).toBe(false)
    expect(listed(wrapper)).toEqual(['finished', 'started', 'told'])
    expect(localStorage.getItem(ActivityOpenKey)).toBeNull()
  })

  it('starts folded when this browser last folded it', async () => {
    localStorage.setItem(ActivityOpenKey, '0')
    const { wrapper } = await mountMonitor(three())

    expect(wrapper.find('[data-monitor-activity-collapsed]').exists()).toBe(true)
  })
})

describe('a mark left by the former Clear', () => {
  it('reads the window from the row after the mark when the Monitor is mounted with one', async () => {
    const rows = three()
    localStorage.setItem(ClearedKey, String(rows[1]!.seq))
    const { wrapper } = await mountMonitor(rows)
    await published()

    expect(listed(wrapper)).toEqual(['finished'])
    expect(spanHint(wrapper)).toContain('1 rows')
  })

  it('renders the whole window when the mark cannot be read', async () => {
    localStorage.setItem(ClearedKey, 'not a seq')
    const { wrapper } = await mountMonitor(three())
    await published()

    expect(listed(wrapper)).toEqual(['finished', 'started', 'told'])
  })
})

describe('Show everything again', () => {
  it('is offered in the settings dialog while a mark is set, removes it and reads the window as before', async () => {
    const rows = three()
    localStorage.setItem(ClearedKey, String(rows[2]!.seq))
    const { wrapper } = await mountMonitor(rows)
    await published()
    expect(listed(wrapper)).toEqual([])

    const panel = await openPanel(wrapper)
    const again = panel.get('[data-monitor-show-everything]')
    expect(again.text()).toBe('Show everything again')

    await again.trigger('click')
    await published()

    expect(localStorage.getItem(ClearedKey)).toBeNull()
    expect(listed(wrapper)).toEqual(['finished', 'started', 'told'])
    expect(spanHint(wrapper)).toContain('3 rows')
    expect(getMessages).toHaveBeenCalledTimes(1)
  })

  it('is not offered with nothing cleared', async () => {
    const { wrapper } = await mountMonitor(three())
    const panel = await openPanel(wrapper)

    expect(panel.find('[data-monitor-show-everything]').exists()).toBe(false)
  })
})
