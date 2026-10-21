// @vitest-environment happy-dom
//
// THE ACTIVITY PANE'S CLEAR, MOUNTED through the real console store. A Clear icon button at the
// right of the ACTIVITY heading empties the activity list and the timeline chart, starts the token
// figure and its stretch over, and remembers in this browser the seq it cleared after
// (`harness.monitor.clearedAfter`), so a Monitor mounted later reads the window from the row after
// it. Show everything again in the settings dialog forgets the mark. Nothing goes to the server.
// What the model forgets is `monitorClear.spec.ts`, with the scene under reduced motion.
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
})

afterEach(() => {
  wrapper?.unmount()
  wrapper = null
  resetBody()
  localStorage.removeItem(ClearedKey)
  vi.clearAllMocks()
})

describe('the Clear button on the activity pane', () => {
  it('sits at the right of the ACTIVITY heading as an icon button whose tooltip and label say Clear', async () => {
    const { wrapper } = await mountMonitor(three())
    const head = wrapper.get('.monitor-ticker-head')
    const clear = head.get('[data-monitor-clear]')

    expect(head.element.firstElementChild?.textContent?.trim()).toBe('Activity')
    expect(head.element.lastElementChild).toBe(clear.element)
    expect(clear.attributes('aria-label')).toBe('Clear')
    expect(clear.text()).not.toContain('Clear')
    expect(clear.find('.q-icon').exists()).toBe(true)

    const tooltip = clear.findComponent({ name: 'QTooltip' })
    ;(tooltip.vm as unknown as { show: () => void }).show()
    await flushPromises()
    expect(document.body.querySelector('.q-tooltip')?.textContent?.trim()).toBe('Clear')
  })

  it('empties the list, the chart and the token figure at once, writes the mark, and shows the next row as before', async () => {
    const rows = three()
    const { wrapper, board } = await mountMonitor(rows)
    await published()
    expect(listed(wrapper)).toEqual(['finished', 'started', 'told'])
    expect(bars(wrapper).length).toBeGreaterThan(0)
    expect(spanHint(wrapper)).toContain('3 rows')
    expect(wrapper.get('[data-stat="tokens"] .monitor-stat-value').text()).toBe('12k')

    await wrapper.get('[data-monitor-clear]').trigger('click')
    await flushPromises()

    expect(listed(wrapper)).toEqual([])
    expect(bars(wrapper)).toEqual([])
    expect(spanHint(wrapper)).toContain('0 rows')
    // At once: no roll down to zero, nothing that moves.
    expect(wrapper.get('[data-stat="tokens"] .monitor-stat-value').text()).toBe('0')
    expect(wrapper.get('[data-stat="tokens"] .monitor-stat-words').text()).toBe('billable tokens')
    expect(wrapper.get('[data-stat="open"] .monitor-stat-value').text()).toBe('0')
    expect(localStorage.getItem(ClearedKey)).toBe(String(rows[2]!.seq))
    // Nothing is asked of the server.
    expect(getMessages).toHaveBeenCalledTimes(1)

    board.record([tell('Write the tests', '2026-10-21T10:10:00Z', 2)])
    await published()

    expect(listed(wrapper)).toEqual(['told'])
    expect(wrapper.findAll('.monitor-row')[0]!.text()).toContain('Write the tests')
    expect(spanHint(wrapper)).toContain('1 rows')
    expect(wrapper.get('[data-stat="open"] .monitor-stat-value').text()).toBe('1')
  })

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
