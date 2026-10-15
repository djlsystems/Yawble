// @vitest-environment happy-dom
//
// THE MONITOR, MOUNTED through the real console store. The graph is a canvas, which happy-dom does
// not draw, so this asserts what the view says in words beside it: the figures, the activity list
// and the live and replay controls. What each row does to the model is `monitorModel.spec.ts`.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import type { Message } from '../../api/types'

const { getMessages } = vi.hoisted(() => ({ getMessages: vi.fn() }))

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  getMessages,
}))

import MonitorView from '../MonitorView.vue'
import { useConsoleStore } from '../../stores/console'
import { resetBody } from '../../test/mountQuasar'

let seq = 0
function row(type: string, source: string, payload: Record<string, unknown>, at: string): Message {
  seq++
  return { seq, type, source, payload: JSON.stringify(payload), correlationId: 1, causationSeq: null, depth: 0, occurredAt: at }
}

const snapshot = (id: string, state: 'Idle' | 'Running' = 'Idle') => ({
  team: 'Alpha', id, name: id, agent: 'claude', state, queueDepth: 0, ceiling: 1, subscribes: [],
  currentCorrelation: null, sinceSeq: 0,
})

let wrapper: VueWrapper | null = null

async function mountMonitor(rows: Message[]) {
  getMessages.mockResolvedValue(rows)
  const board = useConsoleStore()
  board.$patch({
    teams: [{ id: 'Alpha', name: 'Alpha', containers: [snapshot('Manager', 'Running'), snapshot('Developer')] }] as never,
    managerName: 'Manager' as never,
  })
  board.view = 'monitor'
  wrapper = mount(MonitorView, { attachTo: document.body })
  await flushPromises()
  return { wrapper, board }
}

beforeEach(() => {
  setActivePinia(createPinia())
  seq = 0
})

afterEach(() => {
  wrapper?.unmount()
  wrapper = null
  resetBody()
  vi.clearAllMocks()
})

describe('the Monitor', () => {
  it('reads the log once, newest window, and says what happened in words, newest first', async () => {
    const { wrapper } = await mountMonitor([
      row('agentContainer.instruction.Alpha/Developer', 'Alpha/Manager', { instruction: 'Write the skeleton', subject: 'Write the skeleton' }, '2026-10-15T10:00:00Z'),
      row('agentContainer.started', 'Alpha/Developer', {}, '2026-10-15T10:00:01Z'),
      row('agentContainer.progress', 'Alpha/Developer', { status: 'Reading the spec' }, '2026-10-15T10:00:02Z'),
    ])

    expect(getMessages).toHaveBeenCalledWith(0, 2000)
    const rows = wrapper.findAll('.monitor-row')
    expect(rows.map((found) => found.find('.monitor-row-verb').text())).toEqual(['started', 'told'])
    expect(rows[1]!.text()).toContain('Write the skeleton')
    expect(wrapper.find('[data-monitor-mode="live"]').exists()).toBe(true)
  })

  it('counts the members the snapshots say are running', async () => {
    const { wrapper } = await mountMonitor([])

    expect(wrapper.find('[data-stat="running"] .monitor-stat-value').text()).toBe('1')
  })

  it('shows rows the board pulls while it is live', async () => {
    const { wrapper, board } = await mountMonitor([])

    board.record([row('agentContainer.handback', 'Alpha/Developer', { delivered: 'Skeleton pushed' }, '2026-10-15T10:05:00Z')])
    await new Promise((resolve) => setTimeout(resolve, 250))
    await flushPromises()

    expect(wrapper.findAll('.monitor-row').map((found) => found.find('.monitor-row-verb').text())).toEqual(['handed back'])
  })

  it('passes over a team that does not exist while live', async () => {
    const { wrapper } = await mountMonitor([
      row('agentContainer.started', 'Gone/Developer', {}, '2026-10-15T10:00:01Z'),
    ])

    expect(wrapper.findAll('.monitor-row')).toHaveLength(0)
    expect(wrapper.find('.monitor-empty').text()).toBe('No activity in view.')
  })

  it('switches to a replay of the rows held, and back to live', async () => {
    const { wrapper } = await mountMonitor([
      row('agentContainer.started', 'Alpha/Developer', {}, '2026-10-15T10:00:01Z'),
    ])

    await wrapper.findAll('.monitor-mode .q-btn')[1]!.trigger('click')
    await flushPromises()
    expect(wrapper.find('[data-monitor-mode="replay"]').text()).toBe('REPLAY 60×')

    await wrapper.findAll('.monitor-mode .q-btn')[0]!.trigger('click')
    await flushPromises()
    expect(wrapper.find('[data-monitor-mode="live"]').exists()).toBe(true)
  })

  it('offers Keep my view, off by default, and remembers it in this browser', async () => {
    localStorage.removeItem('harness.monitor.keepView')
    const { wrapper } = await mountMonitor([])
    const button = () => wrapper.find('[data-monitor-keep-view]')

    expect(button().attributes('aria-pressed')).toBe('false')
    await button().trigger('click')
    expect(button().attributes('aria-pressed')).toBe('true')

    wrapper.unmount()
    const again = await mountMonitor([])
    expect(again.wrapper.find('[data-monitor-keep-view]').attributes('aria-pressed')).toBe('true')

    await again.wrapper.find('[data-monitor-keep-view]').trigger('click')
    expect(localStorage.getItem('harness.monitor.keepView')).toBeNull()
  })

  it('says so when the log cannot be read', async () => {
    getMessages.mockRejectedValueOnce(new Error('503 Service Unavailable'))
    const board = useConsoleStore()
    board.$patch({ teams: [] as never })
    wrapper = mount(MonitorView, { attachTo: document.body })
    await flushPromises()

    expect(wrapper.find('.monitor-overlay--error').text()).toBe('The log could not be read: 503 Service Unavailable')
  })
})
