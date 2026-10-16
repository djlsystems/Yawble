// @vitest-environment happy-dom
//
// THE KANBAN CARD OF A WORKFLOW WITH A PENDING WAKE SAYS SO, as the member's card does: "wakes at
// 9:41 PM: <note>" on the reader's clock, with a person's Cancel. The card is a button, so Cancel is
// a span with a button's role whose click opens no panel; the card emits `cancel-wake` and the board
// cancels it under the card's own team, then refetches.
//
// The browser is in New York, on summer time (UTC-4) on these dates.
import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';

const { cancelWake } = vi.hoisted(() => ({ cancelWake: vi.fn() }));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  cancelWake,
}));

import KanbanBoard from '../KanbanBoard.vue';
import KanbanCardComponent from '../KanbanCard.vue';
import { useKanbanStore } from '../../stores/kanban';
import type { KanbanCard } from '../../api/kanban';
import type { PendingWake } from '../../api/types';
import { localMinute } from '../../lib/localTime';
import { resetBody } from '../../test/mountQuasar';

const Due = '2026-10-16T01:41:00Z';

const wake = (over: Partial<PendingWake> = {}): PendingWake => ({
  id: 7,
  member: 'DeveloperIda',
  workflow: 4100,
  dueAt: Due,
  askedAt: '2026-10-16T01:31:00Z',
  note: 'check whether the certificate was issued',
  ...over,
});

const card = (over: Partial<KanbanCard> = {}): KanbanCard => ({
  id: '4100_DeveloperIda',
  workflowSeq: 4100,
  team: 'alpha',
  member: 'DeveloperIda',
  item: null,
  title: 'Bind the domain',
  body: '',
  status: 'running',
  laneId: 'todo',
  progress: [],
  createdAt: '2026-10-16T01:00:00Z',
  updatedAt: '2026-10-16T01:31:00Z',
  awaitingManager: false,
  paused: false,
  openWorkflow: { workflow: 4100, latestSeq: 4180 },
  ...over,
});

let zone: string | undefined;
let wrapper: VueWrapper | undefined;

beforeAll(() => {
  zone = process.env.TZ;
  process.env.TZ = 'America/New_York';
});

afterAll(() => {
  if (zone === undefined) delete process.env.TZ;
  else process.env.TZ = zone;
});

beforeEach(() => {
  vi.useFakeTimers({ toFake: ['Date'] });
  vi.setSystemTime(new Date('2026-10-16T01:30:00Z'));
  cancelWake.mockReset();
  vi.stubGlobal('fetch', vi.fn(() => new Promise(() => {})));
});

afterEach(() => {
  wrapper?.unmount();
  wrapper = undefined;
  vi.useRealTimers();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  resetBody();
});

describe('a Kanban card with a pending wake', () => {
  it('says when it wakes on the reader\'s clock, with the note and Cancel', () => {
    wrapper = mount(KanbanCardComponent, { props: { card: card({ wakes: [wake()] }) } });
    const line = wrapper.get('.k-card-wakes').text();

    expect(line).toContain(`wakes at ${localMinute(Date.parse(Due))}: check whether the certificate was issued`);
    expect(line).toContain('9:41');
    expect(wrapper.get('[data-action="cancel-wake"]').text()).toContain('Cancel');
  });

  it('emits cancel-wake with the wake from Cancel, and does not open the card', async () => {
    wrapper = mount(KanbanCardComponent, { props: { card: card({ wakes: [wake()] }) } });

    await wrapper.get('[data-action="cancel-wake"]').trigger('click');

    expect(wrapper.emitted('cancel-wake')).toEqual([[wake()]]);
    expect(wrapper.emitted('open')).toBeUndefined();
  });

  it('shows nothing for a card with no wake, as an older server sends it', () => {
    wrapper = mount(KanbanCardComponent, { props: { card: card() } });

    expect(wrapper.find('.k-card-wakes').exists()).toBe(false);
    expect(wrapper.text()).not.toContain('wakes at');
  });

  it('is cancelled by the board under the card\'s own team, and the board refetched', async () => {
    setActivePinia(createPinia());
    const kanban = useKanbanStore();
    kanban.board = { lanes: [{ id: 'todo', title: 'To do' }], cards: [card({ wakes: [wake()] })] };
    vi.spyOn(kanban, 'setFilters').mockImplementation(() => {});
    const load = vi.spyOn(kanban, 'load').mockResolvedValue();
    cancelWake.mockResolvedValue({ wake: wake(), cancelled: true });

    wrapper = mount(KanbanBoard, { attachTo: document.body });
    await flushPromises();
    load.mockClear();

    await wrapper.get('[data-action="cancel-wake"]').trigger('click');
    await flushPromises();

    expect(cancelWake).toHaveBeenCalledWith('alpha', 7);
    expect(load).toHaveBeenCalledTimes(1);
  });

  it('puts the Host\'s refusal on the board\'s error line', async () => {
    setActivePinia(createPinia());
    const kanban = useKanbanStore();
    kanban.board = { lanes: [{ id: 'todo', title: 'To do' }], cards: [card({ wakes: [wake()] })] };
    vi.spyOn(kanban, 'setFilters').mockImplementation(() => {});
    vi.spyOn(kanban, 'load').mockResolvedValue();
    cancelWake.mockRejectedValue(new Error('No pending wake 7 on this team. It has fired or been cancelled.'));

    wrapper = mount(KanbanBoard, { attachTo: document.body });
    await flushPromises();

    await wrapper.get('[data-action="cancel-wake"]').trigger('click');
    await flushPromises();

    expect(kanban.error).toContain('It has fired or been cancelled.');
  });
});
