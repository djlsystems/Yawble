// @vitest-environment happy-dom
//
// THE MONITOR, MOUNTED through the real console store. The graph is a canvas, which happy-dom does
// not draw, so this asserts what the view says in words beside it: the figures, the activity list
// and the live and replay controls. What each row does to the model is `monitorModel.spec.ts`.
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

// The full icon font is `monitorFont.spec.ts`; here it never arrives, and the dots stand.
vi.mock('../../lib/monitor/font', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  loadMonitorSymbols: () => Promise.resolve(null),
}))

// The Monitor's scene, as it is, with the pick it is handed kept so a spec can pick a node.
const picks: ((picked: unknown) => void)[] = []
vi.mock('../../lib/monitor/scene', async (importOriginal) => {
  const real = await importOriginal<typeof import('../../lib/monitor/scene')>()
  class PickableScene extends real.MonitorScene {
    constructor(...args: ConstructorParameters<typeof real.MonitorScene>) {
      super(...args)
      picks.push(args[1].onPick as (picked: unknown) => void)
    }
  }
  return { ...real, MonitorScene: PickableScene }
})

import MonitorView from '../MonitorView.vue'
import { useConsoleStore } from '../../stores/console'
import { MonitorModel } from '../../lib/monitor/model'
import { resetBody } from '../../test/mountQuasar'
import { avatarIndex, setAvatarSheet } from '../../lib/avatars'

let seq = 0
function row(type: string, source: string, payload: Record<string, unknown>, at: string): Message {
  seq++
  return { seq, type, source, payload: JSON.stringify(payload), correlationId: 1, causationSeq: null, depth: 0, occurredAt: at }
}

const snapshot = (id: string, state: 'Idle' | 'Running' = 'Idle', name = id) => ({
  team: 'Alpha', id, name, agent: 'claude', state, queueDepth: 0, ceiling: 1, subscribes: [],
  currentCorrelation: null, sinceSeq: 0,
})

let wrapper: VueWrapper | null = null

async function mountMonitor(rows: Message[], developerName = 'Developer') {
  getMessages.mockResolvedValue(rows)
  const board = useConsoleStore()
  board.$patch({
    teams: [{ id: 'Alpha', name: 'Alpha', containers: [snapshot('Manager', 'Running'), snapshot('Developer', 'Idle', developerName)] }] as never,
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
  picks.length = 0
  listEvents.mockResolvedValue([])
  getFileChanges.mockResolvedValue({ changes: [], next: null })
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

  it('shows the avatar of a row\'s member to the left of its words, in the ring of its state on the wall', async () => {
    setAvatarSheet(null, 1024, 1024)
    try {
      const { wrapper } = await mountMonitor([
        row('agentContainer.started', 'Alpha/Manager', {}, '2026-10-15T10:00:00Z'),
        row('agentContainer.started', 'Alpha/Developer', {}, '2026-10-15T10:00:01Z'),
      ])

      const rows = wrapper.findAll('.monitor-row')
      expect(rows).toHaveLength(2)
      const [developer, manager] = rows.map((found) => found.get('[data-test="member-avatar"]'))
      expect(developer!.attributes('data-avatar')).toBe(String(avatarIndex('Alpha', 'Developer', 100)))
      expect(manager!.attributes('data-avatar')).toBe(String(avatarIndex('Alpha', 'Manager', 100)))
      expect(manager!.attributes('data-state')).toBe('running')
      expect(manager!.attributes('style')).toContain('var(--os-monitor-state-running)')
      // Left of the words: after the tone's mark, before the body.
      expect(rows[0]!.element.children[1]!.getAttribute('data-test')).toBe('member-avatar')
      expect(rows[0]!.element.children[2]!.classList.contains('monitor-row-body')).toBe(true)
    } finally {
      setAvatarSheet(null, 0, 0)
    }
  })

  it('counts the members the snapshots say are running', async () => {
    const { wrapper } = await mountMonitor([])

    expect(wrapper.find('[data-stat="running"] .monitor-stat-value').text()).toBe('1')
  })

  it('reconciles on a snapshot that changed a member and not on one that changed nothing', async () => {
    const reconcile = vi.spyOn(MonitorModel.prototype, 'reconcile')
    const { board } = await mountMonitor([])
    reconcile.mockClear()

    board.$patch({ teams: [{ id: 'Alpha', name: 'Alpha', containers: [snapshot('Manager', 'Running'), snapshot('Developer')] }] as never })
    await flushPromises()
    expect(reconcile).not.toHaveBeenCalled()

    board.$patch({ teams: [{ id: 'Alpha', name: 'Alpha', containers: [snapshot('Manager', 'Running'), snapshot('Developer', 'Running')] }] as never })
    await flushPromises()
    expect(reconcile).toHaveBeenCalledTimes(1)
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

  it('draws plugin rows as before when the event catalog cannot be read', async () => {
    listEvents.mockRejectedValueOnce(new Error('503 Service Unavailable'))
    const { wrapper } = await mountMonitor([
      row('plugin.azure.sqlCreated', 'Alpha/Developer', { name: 'sql-db' }, '2026-10-15T10:00:01Z'),
    ])

    expect(wrapper.find('.monitor-row-verb').text()).toBe('sqlCreated')
    expect(wrapper.find('[data-monitor-resource]').exists()).toBe(false)
    expect(wrapper.find('.monitor-overlay--error').exists()).toBe(false)
  })
})

describe('the Monitor and a picked member', () => {
  it('lists that member\'s rows when its name is not its id, the rows read before its snapshot named it', async () => {
    const { wrapper } = await mountMonitor([
      row('agentContainer.instruction.Alpha/Developer', 'Alpha/Manager', { instruction: 'Write the skeleton', subject: 'Write the skeleton' }, '2026-10-15T10:00:00Z'),
      row('agentContainer.started', 'Alpha/Developer', {}, '2026-10-15T10:00:01Z'),
      row('agentContainer.started', 'Alpha/Manager', {}, '2026-10-15T10:00:02Z'),
    ], 'Developer Quinn')

    picks.at(-1)!({ kind: 'member', key: 'Alpha/Developer' })
    await flushPromises()

    const rows = wrapper.findAll('.monitor-row')
    expect(rows.map((found) => found.find('.monitor-row-verb').text())).toEqual(['started', 'told'])
  })
})

describe('the Monitor and a wake', () => {
  it('names each ask, fire and cancel in its activity list, with the note', async () => {
    const { wrapper } = await mountMonitor([
      row('wake.asked', 'Alpha/Developer', { wake: 1, dueAt: '2026-10-15T10:10:00Z', note: 'check the certificate' }, '2026-10-15T10:00:00Z'),
      row('wake.fired', 'Alpha/Developer', { wake: 1, dueAt: '2026-10-15T10:10:00Z', askedAt: '2026-10-15T10:00:00Z', instruction: 3, note: 'check the certificate' }, '2026-10-15T10:10:00Z'),
      row('wake.asked', 'Alpha/Developer', { wake: 2, dueAt: '2026-10-15T10:40:00Z', note: 'look at the deploy' }, '2026-10-15T10:20:00Z'),
      row('wake.cancelled', 'Alpha/Developer', { wake: 2, by: 'ada@example.com', reason: 'cancelled by a person', note: 'look at the deploy' }, '2026-10-15T10:21:00Z'),
    ])

    const rows = wrapper.findAll('.monitor-row')
    expect(rows.map((found) => found.find('.monitor-row-verb').text())).toEqual([
      'wake cancelled',
      'asked to be woken',
      'wake fired',
      'asked to be woken',
    ])
    expect(rows[0]!.text()).toContain('look at the deploy')
    expect(rows[2]!.text()).toContain('check the certificate')
    expect(rows[3]!.text()).toContain('check the certificate')
  })
})
