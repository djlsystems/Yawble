// @vitest-environment happy-dom
//
// ONE EVENT VOCABULARY ON EVERY VIEW. A card move, a push, a run's completion and a type nobody knows
// read the same words on the member card's feed, the Monitor's activity list and the Kanban card
// panel's story - the words of `lib/eventWords` - and the raw type stays in the feed row's tooltip
// and the opened message's header, for anyone quoting it.
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
  publishSteering: vi.fn(),
}))

vi.mock('../../lib/monitor/font', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  loadMonitorSymbols: () => Promise.resolve(null),
}))

import ActivityFeed from '../ActivityFeed.vue'
import MonitorView from '../MonitorView.vue'
import KanbanCardPanel from '../KanbanCardPanel.vue'
import { useConsoleStore } from '../../stores/console'
import { useKanbanStore } from '../../stores/kanban'
import type { KanbanCardDetail } from '../../api/kanban'
import { eventWords } from '../../lib/eventWords'
import { bodyFind, resetBody } from '../../test/mountQuasar'
import { settle } from '../../test/formProbe'

/** The four rows the item names, oldest first: a card move, a push, a completion, an unknown type. */
const Types = ['kanban.card.moved', 'repo.pushed', 'agentContainer.completed', 'agentContainer.somethingNew'] as const

function row(seq: number, type: string): Message {
  return {
    seq,
    type,
    source: 'Alpha/Developer',
    payload: JSON.stringify({ change: 'to Review', branches: 'team/alpha', team: 'Alpha' }),
    correlationId: 1,
    causationSeq: null,
    depth: 0,
    occurredAt: `2026-10-15T10:00:0${seq}Z`,
  }
}

const rows = Types.map((type, i) => row(i + 1, type))
/** What each row reads, from the one table and nowhere else. */
const words = Types.map((type) => eventWords(type).words)

let wrapper: VueWrapper | null = null

beforeEach(() => {
  setActivePinia(createPinia())
  listEvents.mockResolvedValue([])
  getFileChanges.mockResolvedValue({ changes: [], next: null })
})

afterEach(() => {
  wrapper?.unmount()
  wrapper = null
  resetBody()
  vi.clearAllMocks()
})

describe('the one event vocabulary', () => {
  it('says the four rows in words a person reads, and an unknown type as it always read', () => {
    expect(words).toEqual(['moved a card', 'pushed', 'finished', 'somethingNew'])
  })

  it("reads the table's words on each feed row, with the raw type in the row's tooltip", async () => {
    wrapper = mount(ActivityFeed, { props: { feed: [...rows].reverse() }, attachTo: document.body })
    const chips = wrapper.findAll('[data-feed-chip]')

    expect(chips.map((chip) => chip.find('[data-feed-words]').text())).toEqual([...words].reverse())

    for (const [index, chip] of chips.entries()) {
      const tooltip = chip.findComponent({ name: 'QTooltip' })
      ;(tooltip.vm as unknown as { show: () => void }).show()
      await flushPromises()
      expect(document.body.querySelector('[data-feed-chip-type]')?.textContent).toBe([...Types].reverse()[index])
      ;(tooltip.vm as unknown as { hide: () => void }).hide()
      await flushPromises()
      resetBody()
    }
  })

  it("keeps the raw type in the opened message's header, beside the table's words", async () => {
    wrapper = mount(ActivityFeed, { props: { feed: [rows[1]!] }, attachTo: document.body })
    await wrapper.find('.q-item').trigger('click')
    await settle()

    expect(bodyFind('[data-feed-dialog] [data-feed-words]')?.textContent).toBe('pushed')
    expect(bodyFind('[data-feed-card-type]')?.textContent).toBe('repo.pushed')
  })

  it("says the same words in the Monitor's activity list", async () => {
    getMessages.mockResolvedValue(rows)
    const board = useConsoleStore()
    board.$patch({
      teams: [{ id: 'Alpha', name: 'Alpha', containers: [{
        team: 'Alpha', id: 'Developer', name: 'Developer', agent: 'claude', state: 'Idle', queueDepth: 0, ceiling: 1,
        subscribes: [], currentCorrelation: null, sinceSeq: 0,
      }] }] as never,
      managerName: 'Manager' as never,
    })
    board.view = 'monitor'
    wrapper = mount(MonitorView, { attachTo: document.body })
    await flushPromises()

    expect(wrapper.findAll('.monitor-row-verb').map((verb) => verb.text())).toEqual([...words].reverse())
  })

  it("says the same words in the Kanban card panel's story, with the raw type on hover", async () => {
    const kanban = useKanbanStore()
    wrapper = mount(KanbanCardPanel, { attachTo: document.body })
    const detail: KanbanCardDetail = {
      id: 'c-1',
      workflowSeq: 1,
      team: 'Alpha',
      member: 'Developer',
      title: 'Write the skeleton',
      body: '',
      status: 'running',
      laneId: 'in-progress',
      progress: [],
      createdAt: '2026-10-15T10:00:00Z',
      updatedAt: '2026-10-15T10:00:04Z',
      awaitingManager: false,
      paused: false,
      trail: rows.map((message) => ({ seq: message.seq, type: message.type, occurredAt: message.occurredAt, text: 'said' })),
    }
    kanban.selectedId = detail.id
    kanban.detail = detail
    await flushPromises()

    const said = [...document.body.querySelectorAll('[data-trail-words]')].map((element) => element.textContent?.trim())
    expect(said).toEqual(words)

    const tooltip = wrapper.findAllComponents({ name: 'QTooltip' })
      .find((found) => (found.element as Node).parentElement?.hasAttribute('data-trail-words'))
    expect(tooltip, 'no tooltip on the first story row').toBeTruthy()
    ;(tooltip!.vm as unknown as { show: () => void }).show()
    await flushPromises()
    expect(document.body.querySelector('[data-trail-type]')?.textContent).toBe(Types[0])
  })
})
