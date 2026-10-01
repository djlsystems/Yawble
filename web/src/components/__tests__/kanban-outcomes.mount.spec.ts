// @vitest-environment happy-dom
//
// THE BOARD'S OUTCOMES, MOUNTED: the Outcome filter on the bar (server-side, like Member and
// Status, and counted by Clear), the tag under a card's title (dashed and "proposed" for a proposed
// outcome, absent for none, and clicking it emits `open-outcome` with the outcome's id) and the
// card menu's "Change outcome…", which sends the link route as the person.
//
// The network is a stubbed `fetch` that records every request, so what is asserted is the URL and
// body the server would receive - not a store method that was called.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { QSelect } from 'quasar';

vi.mock('quasar', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  useQuasar: () => ({ notify: vi.fn() }),
}));

import KanbanBoard from '../KanbanBoard.vue';
import KanbanCard from '../KanbanCard.vue';
import KanbanFilterBar from '../KanbanFilterBar.vue';
import KanbanChangeOutcome from '../KanbanChangeOutcome.vue';
import { useKanbanStore } from '../../stores/kanban';
import type { KanbanCard as Card } from '../../api/kanban';
import { resetBody } from '../../test/mountQuasar';

const shipping = { id: '11111111-1111-1111-1111-111111111111', name: 'Ship <b>the release</b>', status: 'active' as const };
const faster = { id: '22222222-2222-2222-2222-222222222222', name: 'Faster onboarding', status: 'proposed' as const };

function card(over: Partial<Card> & { id: string }): Card {
  return {
    team: 'alpha',
    member: 'DeveloperInes',
    item: null,
    workflowSeq: 2226,
    title: `Card ${over.id}`,
    body: '',
    status: 'queued',
    laneId: 'todo',
    progress: [],
    createdAt: '2026-09-18T00:00:00Z',
    updatedAt: '2026-09-18T00:00:00Z',
    awaitingManager: false,
    paused: false,
    ...over,
  } as Card;
}

const lanes = [{ id: 'todo', title: 'To do' }];

let requests: { url: string; method: string; body: unknown }[] = [];
let boardCards: Card[] = [];
let wrapper: VueWrapper | undefined;

beforeEach(() => {
  requests = [];
  boardCards = [];
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    requests.push({ url, method: init?.method ?? 'GET', body: init?.body ? JSON.parse(String(init.body)) : null });

    // Every other read (the WIP view, agent updates) never answers, as in `kanban-board.mount.spec`.
    const answer = url.startsWith('/api/outcomes')
      ? { outcomes: [shipping, faster] }
      : url.startsWith('/api/kanban/board')
        ? { lanes, cards: boardCards, filters: {} }
        : null;

    if (init?.method === 'PUT') return new Response(null, { status: 204 });
    if (answer === null) return new Promise<Response>(() => {});

    return new Response(JSON.stringify(answer), { status: 200, headers: { 'content-type': 'application/json' } });
  }));
});

afterEach(() => {
  wrapper?.unmount();
  wrapper = undefined;
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  resetBody();
});

async function mountBoard(cards: Card[]) {
  setActivePinia(createPinia());
  boardCards = cards;
  const kanban = useKanbanStore();
  kanban.board = { lanes, cards };

  wrapper = mount(KanbanBoard, { attachTo: document.body });
  await flushPromises();

  return { board: wrapper, kanban };
}

const outcomeFilter = (board: VueWrapper) =>
  board.findComponent(KanbanFilterBar).findAllComponents(QSelect).find((select) => select.props('label') === 'Outcome')!;

const clearLabel = () =>
  [...document.body.querySelectorAll('button .block')].map((b) => b.textContent?.trim() ?? '').find((t) => t.startsWith('Clear'));

describe('the Outcome filter', () => {
  it('offers All, No outcome and each active and proposed outcome, by name as text', async () => {
    const { board } = await mountBoard([]);

    const options = outcomeFilter(board).props('options') as { label: string; value: string }[];

    expect(options).toEqual([
      { label: 'All', value: '' },
      { label: 'No outcome', value: 'none' },
      { label: 'Ship <b>the release</b>', value: shipping.id },
      { label: 'Faster onboarding (proposed)', value: faster.id },
    ]);
    expect(requests.some((r) => decodeURIComponent(r.url) === '/api/outcomes?status=active,proposed')).toBe(true);
  });

  it('sends the chosen outcome as the board query, and Clear counts it', async () => {
    const { board } = await mountBoard([]);
    expect(clearLabel()).toBeUndefined();

    outcomeFilter(board).vm.$emit('update:modelValue', shipping.id);
    await flushPromises();

    expect(requests.at(-1)!.url).toBe(`/api/kanban/board?outcome=${shipping.id}`);
    expect(clearLabel()).toBe('Clear (1)');

    outcomeFilter(board).vm.$emit('update:modelValue', 'none');
    await flushPromises();

    expect(requests.at(-1)!.url).toBe('/api/kanban/board?outcome=none');
    expect(clearLabel()).toBe('Clear (1)');
  });

  it('sends nothing for All', async () => {
    const { board, kanban } = await mountBoard([]);
    kanban.filters = { outcome: faster.id };

    outcomeFilter(board).vm.$emit('update:modelValue', '');
    await flushPromises();

    expect(requests.at(-1)!.url).toBe('/api/kanban/board');
    expect(clearLabel()).toBeUndefined();
  });
});

describe('the card tag', () => {
  function render(over: Partial<Card>) {
    const mounted = mount(KanbanCard, { props: { card: card({ id: '4045', ...over }) } });
    wrapper = mounted;

    return mounted;
  }

  it('draws an active outcome under the title, solid, its name as text', () => {
    const mounted = render({ outcome: shipping });
    const tag = mounted.find('.k-card-outcome');

    expect(tag.exists()).toBe(true);
    expect(tag.classes()).not.toContain('k-card-outcome--proposed');
    expect(tag.text()).toBe('Ship <b>the release</b>');
    expect(tag.find('b').exists()).toBe(false);
    expect(mounted.find('.k-card-title + .k-card-outcome').exists()).toBe(true);
  });

  it('draws a proposed outcome dashed and says proposed', () => {
    const tag = render({ outcome: faster }).find('.k-card-outcome');

    expect(tag.classes()).toContain('k-card-outcome--proposed');
    expect(tag.find('.k-card-outcome-proposed').text()).toBe('proposed');
  });

  it('draws no tag for a card with no outcome', () => {
    expect(render({ outcome: null }).find('.k-card-outcome').exists()).toBe(false);
    expect(render({}).find('.k-card-outcome').exists()).toBe(false);
  });

  it('emits open-outcome with the outcome id when clicked, and does not open the card', async () => {
    const mounted = render({ outcome: faster });

    await mounted.find('.k-card-outcome').trigger('click');

    expect(mounted.emitted('open-outcome')).toEqual([[faster.id]]);
    expect(mounted.emitted('open')).toBeUndefined();
  });

  it('passes open-outcome up through the board', async () => {
    const { board } = await mountBoard([card({ id: '4045', outcome: shipping })]);

    await board.find('.k-card-outcome').trigger('click');

    expect(board.emitted('open-outcome')).toEqual([[shipping.id]]);
  });
});

describe('Change outcome…', () => {
  it('sends the link route for the card\'s open workflow, under the card\'s team', async () => {
    const { board } = await mountBoard([
      card({
        id: '4045', team: 'beta', workflowSeq: 3900, workflows: [3900, 4000],
        openWorkflow: { workflow: 4000, latestSeq: 4100 }, outcome: shipping,
      }),
    ]);

    board.findComponent(KanbanCard).vm.$emit('change-outcome', '4045');
    await flushPromises();

    const select = board.findComponent(KanbanChangeOutcome).findComponent(QSelect);
    select.vm.$emit('update:modelValue', faster.id);
    await flushPromises();

    ([...document.body.querySelectorAll('[data-action="save-outcome"]')][0] as HTMLButtonElement).click();
    await flushPromises();

    const put = requests.find((r) => r.method === 'PUT')!;
    expect(put.url).toBe('/api/teams/beta/workflows/4000/outcome');
    expect(put.body).toEqual({ outcome: faster.id });
  });

  it('is on the card menu', async () => {
    const mounted = mount(KanbanCard, { props: { card: card({ id: '4045' }) }, attachTo: document.body });
    wrapper = mounted;

    await mounted.find('.k-card-menu').trigger('click');
    await flushPromises();

    const item = document.body.querySelector('[data-action="change-outcome"]') as HTMLElement;
    expect(item.textContent).toContain('Change outcome…');

    item.click();
    expect(mounted.emitted('change-outcome')).toEqual([['4045']]);
    expect(mounted.emitted('open')).toBeUndefined();
  });
});
