// @vitest-environment happy-dom
//
// THE HIGHLIGHT DEEP LINK LANDS. An address naming a member (`member=` beside `team=`) or a Kanban
// card (`card=`) opens the view, scrolls that card into view and flashes it once - no flash under
// reduced motion. A Kanban card the filter hides is made visible first: the team filter becomes
// the card's team and the member, status and outcome filters and the search box are cleared. A
// card no board holds shows the board with a one-line notice and no flash.
//
// Through the real stores, the real board and member cards, with the Host's answers faked at `fetch`.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  listContainerTriggers: vi.fn(async () => []),
}));

import KanbanBoard from '../KanbanBoard.vue';
import BoardGrid from '../BoardGrid.vue';
import { useKanbanStore } from '../../stores/kanban';
import { FlashMs, useHighlightStore } from '../../stores/highlight';
import { useConsoleStore } from '../../stores/console';
import type { KanbanCard } from '../../api/kanban';
import { asMemberId, asTeamId, type ContainerSnapshot, type Team } from '../../api/types';
import { resetBody } from '../../test/mountQuasar';

function card(over: Partial<KanbanCard> & { id: string }): KanbanCard {
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
  } as KanbanCard;
}

const lanes = [
  { id: 'todo', title: 'To do' },
  { id: 'doing', title: 'Doing' },
];

/** Every card the tenant has; the fake board route narrows them by the query as the Host does. */
const tenant = [
  card({ id: 'a1', team: 'alpha' }),
  card({ id: 'b7', team: 'beta', member: 'Mailer', status: 'running', laneId: 'doing' }),
];

/** The query of every board read, in order. */
let boardReads: URLSearchParams[] = [];

function json(body: unknown) {
  return Promise.resolve(new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } }));
}

let reduced = false;
const scrolled: Element[] = [];

beforeEach(() => {
  boardReads = [];
  reduced = false;
  scrolled.length = 0;
  vi.stubGlobal('matchMedia', (query: string) => ({
    matches: query.includes('reduce') && reduced,
    media: query,
    addEventListener() {},
    removeEventListener() {},
  }));
  Element.prototype.scrollIntoView = function (this: Element) {
    scrolled.push(this);
  };
  vi.stubGlobal(
    'fetch',
    vi.fn((input: RequestInfo | URL) => {
      const url = new URL(String(input), 'http://host');
      if (url.pathname === '/api/kanban/board') {
        const query = url.searchParams;
        boardReads.push(query);
        const cards = tenant.filter((entry) =>
          (!query.get('team') || entry.team === query.get('team'))
          && (!query.get('member') || entry.member === query.get('member'))
          && (!query.get('status') || entry.status === query.get('status')));
        return json({ lanes, cards });
      }
      // Everything else the board reads (the ledger, proposed outcomes) never answers here.
      return new Promise(() => {});
    }),
  );
});

let wrapper: VueWrapper | undefined;

afterEach(() => {
  wrapper?.unmount();
  wrapper = undefined;
  vi.unstubAllGlobals();
  vi.useRealTimers();
  resetBody();
});

async function settle() {
  for (let i = 0; i < 6; i++) await flushPromises();
}

async function mountKanban(filters: Record<string, string>, askFor: string, text = '') {
  setActivePinia(createPinia());
  const kanban = useKanbanStore();
  kanban.filters = filters;
  kanban.text = text;
  useHighlightStore().askCard(askFor);
  wrapper = mount(KanbanBoard, { attachTo: document.body });
  await settle();
  return kanban;
}

const flashed = (board: VueWrapper) => board.findAll('.k-card.os-flash').map((node) => node.attributes('data-card-id'));

describe('a Kanban card an address names', () => {
  it('lands on the card the board holds: scrolled into view and flashed once', async () => {
    const kanban = await mountKanban({}, 'b7');
    const highlight = useHighlightStore();

    expect(flashed(wrapper!)).toEqual(['b7']);
    expect(scrolled.map((node) => (node as HTMLElement).dataset.cardId)).toEqual(['b7']);
    expect(highlight.card).toBe('');
    expect(highlight.notice).toBe('');
    expect(kanban.filters).toEqual({});
  });

  it('flashes once and then stops', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    await mountKanban({}, 'b7');
    expect(flashed(wrapper!)).toEqual(['b7']);

    vi.advanceTimersByTime(FlashMs);
    await flushPromises();
    expect(flashed(wrapper!)).toEqual([]);
  });

  it('scrolls but does not flash under reduced motion', async () => {
    reduced = true;
    await mountKanban({}, 'b7');

    expect(flashed(wrapper!)).toEqual([]);
    expect(scrolled.map((node) => (node as HTMLElement).dataset.cardId)).toEqual(['b7']);
    expect(useHighlightStore().card).toBe('');
  });

  it('widens a filter that hides the card to its team, clears member, status, outcome and the search, then lands', async () => {
    const kanban = await mountKanban({ team: 'alpha', member: 'DeveloperInes', status: 'queued', outcome: 'none' }, 'b7', 'Card');

    expect(kanban.filters).toEqual({ team: 'beta' });
    expect(kanban.text).toBe('');
    expect(boardReads.at(-1)?.toString()).toBe('team=beta');
    expect(flashed(wrapper!)).toEqual(['b7']);
    expect(useHighlightStore().notice).toBe('');
  });

  it('shows the board with a one-line notice and no flash for a card that is gone', async () => {
    const kanban = await mountKanban({ team: 'alpha' }, 'gone1');

    const notice = wrapper!.find('[data-highlight-notice]');
    expect(notice.exists()).toBe(true);
    expect(notice.text()).toContain('Card gone1 is not on the board.');
    expect(flashed(wrapper!)).toEqual([]);
    expect(scrolled).toEqual([]);
    expect(kanban.filters).toEqual({ team: 'alpha' });
    expect(wrapper!.findAll('.k-card').map((node) => node.attributes('data-card-id'))).toEqual(['a1']);
    expect(useHighlightStore().card).toBe('');
  });
});

const snapshot = (over: Omit<Partial<ContainerSnapshot>, 'id'> & { id: string }): ContainerSnapshot => ({
  team: asTeamId('beta'),
  name: over.id,
  agent: 'echo',
  state: 'Idle',
  queueDepth: 0,
  ceiling: 16,
  subscribes: [],
  currentCorrelation: null,
  sinceSeq: 0,
  ...over,
  id: asMemberId(over.id),
});

const roster = [snapshot({ id: 'Manager' }), snapshot({ id: 'Mailer', name: 'Mailer Mo' })];

async function mountGrid(team: string, member: string) {
  setActivePinia(createPinia());
  const board = useConsoleStore();
  board.teams = [{ id: asTeamId('beta'), name: 'beta', containers: roster } as unknown as Team];
  useHighlightStore().askMember(team, member);
  wrapper = mount(BoardGrid, { props: { containers: roster }, attachTo: document.body });
  await settle();
}

const flashedMembers = () =>
  [...document.querySelectorAll<HTMLElement>('[data-member-card].os-flash')].map((node) => node.dataset.memberCard);

describe('a member an address names', () => {
  it('lands on the named member card: scrolled into view and flashed once', async () => {
    await mountGrid('beta', 'Mailer');

    expect(flashedMembers()).toEqual(['member:beta/Mailer']);
    expect(scrolled.map((node) => (node as HTMLElement).dataset.memberCard)).toEqual(['member:beta/Mailer']);
    expect(useHighlightStore().member).toBeNull();
  });

  it('does not flash under reduced motion', async () => {
    reduced = true;
    await mountGrid('beta', 'Mailer');

    expect(flashedMembers()).toEqual([]);
    expect(scrolled.map((node) => (node as HTMLElement).dataset.memberCard)).toEqual(['member:beta/Mailer']);
  });

  it('waits while the board shows another team', async () => {
    await mountGrid('alpha', 'Mailer');

    expect(flashedMembers()).toEqual([]);
    expect(useHighlightStore().member).toEqual({ team: 'alpha', member: 'Mailer' });
  });

  it('says in one line that a member the board has not got is not there, with no flash', async () => {
    await mountGrid('beta', 'Nobody');

    expect(document.querySelector('[data-highlight-notice]')?.textContent).toContain('Nobody is not on this board.');
    expect(flashedMembers()).toEqual([]);
    expect(scrolled).toEqual([]);
  });
});
