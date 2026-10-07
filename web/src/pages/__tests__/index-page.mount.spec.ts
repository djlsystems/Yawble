// @vitest-environment happy-dom
//
// THE BOARD PAGE, MOUNTED: the tab strip, which view replaces the cards, the marks on a team, and
// what each hub push re-reads. The rules behind them live in `lib/teamKpis.ts`, `lib/teamsTable.ts`,
// `lib/kanban.ts` and `stores/kanban.ts`, where they are tested; this file asserts the page wires
// them. It replaces three specs that read `IndexPage.vue` as text and sliced it between markers.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import type { HubHandlers } from '../../lib/hub';

const { connectHub, hub } = vi.hoisted(() => {
  const hub: { handlers: HubHandlers | null } = { handlers: null };
  return {
    hub,
    connectHub: vi.fn((handlers: HubHandlers) => {
      hub.handlers = handlers;
      return Promise.resolve(null);
    }),
  };
});

vi.mock('quasar', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  useQuasar: () => ({ notify: vi.fn() }),
}));

vi.mock('../../lib/hub', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  connectHub,
}));

import IndexPage from '../IndexPage.vue';
import { useConsoleStore } from '../../stores/console';
import { useKanbanStore } from '../../stores/kanban';
import { useSessionStore } from '../../stores/session';
import { StalledBadgeGrace } from '../../lib/teamKpis';
import { resetBody } from '../../test/mountQuasar';

function team(id: string, over: Record<string, unknown> = {}) {
  return {
    id,
    name: id[0]!.toUpperCase() + id.slice(1),
    paused: false,
    containers: [{ id: 'Manager', team: id, state: 'Idle', label: 'Manager', progress: null, blocked: null }],
    memberAgents: [],
    repos: [],
    env: {},
    ...over,
  };
}

/** Children are stubbed: this page is about WHICH of them is on screen and in what order. */
const stubs = { TeamsView: true, KanbanBoard: true, TeamKpiStrip: true, ContainerCard: true, TellManagerBox: true };

let wrapper: VueWrapper | undefined;
let board: ReturnType<typeof useConsoleStore>;
let kanban: ReturnType<typeof useKanbanStore>;

beforeEach(() => {
  localStorage.clear();
  hub.handlers = null;
  connectHub.mockClear();
  vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('[]', {
    status: 200,
    headers: { 'content-type': 'application/json' },
  })));
});

afterEach(() => {
  wrapper?.unmount();
  wrapper = undefined;
  vi.useRealTimers();
  vi.unstubAllGlobals();
  resetBody();
});

async function mountPage(
  teams: ReturnType<typeof team>[],
  { active = teams[0]?.id ?? '', view = 'board' as 'board' | 'teams' | 'kanban' } = {},
) {
  setActivePinia(createPinia());

  const session = useSessionStore();
  session.user = { email: 'person@example.com' } as never;
  session.checked = true;

  board = useConsoleStore();
  board.$patch({ teams: teams as never, activeTeamId: active as never, openTeamTabs: teams.map((t) => t.id) as never });
  board.view = view;

  for (const name of ['refresh', 'refreshForTeamCreated', 'refreshForTeamDeleted', 'pullMessages'] as const) {
    vi.spyOn(board, name).mockResolvedValue(undefined as never);
  }
  vi.spyOn(board, 'refreshRollupIfShowing').mockImplementation(() => {});

  kanban = useKanbanStore();
  vi.spyOn(kanban, 'showKanban').mockImplementation(() => {
    board.view = 'kanban';
  });
  vi.spyOn(kanban, 'loadIfShowing').mockImplementation(() => {});
  vi.spyOn(kanban, 'refreshIfActive').mockImplementation(() => {});

  wrapper = mount(IndexPage, { global: { stubs } });
  await flushPromises();
  return wrapper;
}

/** The tab strip, one entry per tab, left to right. */
function tabs(page: VueWrapper) {
  return page.findAll('.console-tabs > .console-tab');
}

/** The top-level views and blocks in document order, as the tag or class a reader would name. */
function blocks(page: VueWrapper): string[] {
  return page.findAll('teams-view-stub, kanban-board-stub, team-kpi-strip-stub, container-card-stub')
    .map((found) => found.element.tagName.toLowerCase());
}

describe('the tab strip', () => {
  it('puts the Teams tab first, then the open teams, then the Kanban tab', async () => {
    const page = await mountPage([team('alpha'), team('beta')]);

    expect(tabs(page).map((tab) => tab.find('.console-tab-label').text())).toEqual(['Teams', 'Alpha', 'Beta', 'Kanban']);
  });

  /** Only the teams a person has open get a tab. */
  it('shows tabs only for open teams', async () => {
    const page = await mountPage([team('alpha'), team('beta')]);
    board.openTeamTabs = ['alpha' as never];
    await flushPromises();

    expect(tabs(page).map((tab) => tab.find('.console-tab-label').text())).toEqual(['Teams', 'Alpha', 'Kanban']);
  });

  /** Neither the Teams tab nor the Kanban tab is a team: no status chip, no close icon. */
  it.each(['console-tab--teams', 'console-tab--kanban'])('%s carries no status and cannot be closed', async (cls) => {
    const page = await mountPage([team('alpha')]);
    const tab = page.find(`.${cls}`);

    expect(tab.find('.console-tab-status').exists()).toBe(false);
    expect(tab.find('.console-tab-close').exists()).toBe(false);
  });

  it('selects the Teams view through the store', async () => {
    const page = await mountPage([team('alpha')]);
    const showTeamsView = vi.spyOn(board, 'showTeamsView').mockImplementation(() => {});

    await page.find('.console-tab--teams').trigger('click');

    expect(showTeamsView).toHaveBeenCalled();
  });

  it('selects the board through the kanban store, keeping the active team', async () => {
    const page = await mountPage([team('alpha')]);

    await page.find('.console-tab--kanban').trigger('click');

    expect(kanban.showKanban).toHaveBeenCalledWith('alpha');
  });

  /**
   * Clicking a team leaves the board, and tells the kanban store nothing: a board narrowed to one
   * team stays narrowed while you go and look at another.
   */
  it('leaves the board when a team tab is clicked, and tells the kanban store nothing', async () => {
    const page = await mountPage([team('alpha'), team('beta')], { view: 'kanban' });
    const showBoard = vi.spyOn(board, 'showBoard');
    const setActiveTeam = vi.spyOn(board, 'setActiveTeam').mockImplementation(() => {});
    vi.mocked(kanban.showKanban).mockClear();

    await tabs(page)[2]!.trigger('click');

    expect(showBoard).toHaveBeenCalled();
    expect(setActiveTeam).toHaveBeenCalledWith('beta');
    expect(kanban.showKanban).not.toHaveBeenCalled();
    expect(kanban.refreshIfActive).not.toHaveBeenCalled();
  });

  it('does not mark a team tab active while the board is showing', async () => {
    const page = await mountPage([team('alpha')], { view: 'kanban' });

    expect(tabs(page)[1]!.classes()).not.toContain('active');
    expect(page.find('.console-tab--kanban').classes()).toContain('active');
  });

  it('keeps the strip on screen while the board is showing', async () => {
    const page = await mountPage([team('alpha')], { view: 'kanban' });

    expect(page.find('.console-tabs').exists()).toBe(true);
  });
});

describe('which view replaces the cards', () => {
  it('shows the KPI strip above the member cards on a team', async () => {
    const page = await mountPage([team('alpha')]);

    expect(blocks(page)).toEqual(['team-kpi-strip-stub', 'container-card-stub']);
  });

  it('shows the Teams table INSTEAD of the cards', async () => {
    const page = await mountPage([team('alpha')], { view: 'teams' });

    expect(blocks(page)).toEqual(['teams-view-stub']);
  });

  /** Two boards on one page is two things claiming to be what the team is doing. */
  it('shows the Kanban board INSTEAD of the cards', async () => {
    const page = await mountPage([team('alpha')], { view: 'kanban' });

    expect(blocks(page)).toEqual(['kanban-board-stub']);
  });
});

describe('a team\'s marks', () => {
  it('marks a paused team PAUSED on its tab and in the heading', async () => {
    const page = await mountPage([team('alpha', { paused: true })]);

    expect(tabs(page)[1]!.find('.console-tab-status--paused').text()).toBe('PAUSED');
    expect(page.find('.team-heading-pause').text()).toBe('PAUSED');
  });

  it('links a team installed from a solution package to its control panel', async () => {
    const page = await mountPage([
      team('job-tracker', { solution: { id: 'job-tracker', name: 'Job Tracker', version: '1.1.0', plugins: [] } }),
    ]);

    const link = page.find('[data-manage-solution]');
    expect(link.exists()).toBe(true);
    expect(link.text()).toContain('Manage solution');
    expect(link.attributes('href')).toBe('#/solutions/job-tracker');
  });

  it('has no Manage solution link for a team made by hand', async () => {
    const page = await mountPage([team('alpha')]);

    expect(page.find('[data-manage-solution]').exists()).toBe(false);
  });

  it('gives the team work on the team page itself, through a Tell the Manager box', async () => {
    const page = await mountPage([team('alpha')]);

    const box = page.findComponent({ name: 'TellManagerBox' });
    expect(box.exists()).toBe(true);
    expect(box.props('team')).toBe('alpha');
  });

  it('no longer says the page cannot take work, and still names the Concierge as another way', async () => {
    const page = await mountPage([team('alpha')]);

    expect(page.find('[data-team-work-hint]').text()).toBe(
      'You can also ask the Concierge (bottom right).');
  });

  it('does not mark a running team paused', async () => {
    const page = await mountPage([team('alpha')]);

    expect(page.find('.console-tab-status--paused').exists()).toBe(false);
  });
});

describe('socket disconnect state', () => {
  it('warns that the cards may be stale once a connection has dropped', async () => {
    const page = await mountPage([team('alpha')]);
    const banner = 'Live updates are disconnected. Reconnecting automatically; the cards below may be stale.';

    expect(page.text()).not.toContain(banner);

    board.setConnected(true);
    board.setConnected(false);
    await flushPromises();

    expect(page.text()).toContain(banner);
  });
});

describe('what the page re-reads', () => {
  /** The view is persisted, so a reload can land straight on the table or the board. */
  it('loads the board and the rollup on mount, before the hub connects', async () => {
    const order: string[] = [];
    connectHub.mockImplementationOnce((handlers: HubHandlers) => {
      order.push('connectHub');
      hub.handlers = handlers;
      return Promise.resolve(null);
    });

    setActivePinia(createPinia());
    const kanbanStore = useKanbanStore();
    const boardStore = useConsoleStore();
    vi.spyOn(kanbanStore, 'loadIfShowing').mockImplementation(() => void order.push('loadIfShowing'));
    vi.spyOn(boardStore, 'refresh').mockResolvedValue();
    vi.spyOn(boardStore, 'refreshRollupIfShowing').mockImplementation(() => void order.push('refreshRollupIfShowing'));
    useSessionStore().$patch({ user: { email: 'person@example.com' } as never, checked: true });

    wrapper = mount(IndexPage, { global: { stubs } });
    await flushPromises();

    expect(order).toEqual(['loadIfShowing', 'refreshRollupIfShowing', 'connectHub']);
  });

  /**
   * The Teams table lists every team, so it hears every team's pushes - a team with no tab
   * included. Joining only the open tabs left such a row reading the last overview (IDLE, one
   * member) beside a WIP ledger line saying two of its members were running.
   */
  it('joins every team\'s live updates on the Teams table, tab or no tab', async () => {
    const joined: string[] = [];
    connectHub.mockImplementationOnce((handlers: HubHandlers) => {
      hub.handlers = handlers;
      return Promise.resolve({ stop: vi.fn() } as never);
    });

    setActivePinia(createPinia());
    const boardStore = useConsoleStore();
    boardStore.$patch({ teams: [team('alpha'), team('beta')] as never, activeTeamId: '' as never, openTeamTabs: [] });
    boardStore.view = 'teams';
    vi.spyOn(boardStore, 'refresh').mockResolvedValue();
    vi.spyOn(boardStore, 'refreshRollupIfShowing').mockImplementation(() => {});
    vi.spyOn(boardStore, 'switchHubTeam').mockImplementation(async (_connection, id) => {
      joined.push(id);
      return true;
    });
    vi.spyOn(useKanbanStore(), 'loadIfShowing').mockImplementation(() => {});
    useSessionStore().$patch({ user: { email: 'person@example.com' } as never, checked: true });

    wrapper = mount(IndexPage, { global: { stubs } });
    await flushPromises();

    expect(joined.sort()).toEqual(['alpha', 'beta']);
  });

  /** Container activity IS kanban activity, and the Teams table's only refresh once it is open. */
  it('refreshes the board and the rollup on container activity', async () => {
    await mountPage([team('alpha')]);
    vi.mocked(board.refreshRollupIfShowing).mockClear();

    hub.handlers!.onContainerChanged(team('alpha').containers[0] as never);

    expect(kanban.refreshIfActive).toHaveBeenCalled();
    expect(board.refreshRollupIfShowing).toHaveBeenCalled();
  });

  it('refreshes the board on its own push', async () => {
    await mountPage([team('alpha')]);

    hub.handlers!.onKanbanChanged!({} as never);

    expect(kanban.refreshIfActive).toHaveBeenCalled();
  });

  /** A teamChanged push is how a pause or resume made elsewhere lands live. */
  it('refreshes the teams and the rollup on a teamChanged push', async () => {
    await mountPage([team('alpha')]);
    vi.mocked(board.refresh).mockClear();
    vi.mocked(board.refreshRollupIfShowing).mockClear();

    hub.handlers!.onTeamChanged!({} as never);

    expect(board.refresh).toHaveBeenCalled();
    expect(board.refreshRollupIfShowing).toHaveBeenCalled();
  });

  it('routes team create and delete pushes through the store helpers that no-op on self-echo', async () => {
    await mountPage([team('alpha')]);

    hub.handlers!.onTeamCreated!({ team: 'beta' } as never);
    hub.handlers!.onTeamDeleted!({ team: 'alpha' } as never);

    expect(board.refreshForTeamCreated).toHaveBeenCalledWith('beta');
    expect(board.refreshForTeamDeleted).toHaveBeenCalledWith('alpha');
  });

  /**
   * The status chip is a function of TIME, so a clock re-reads it - locally. The 700ms fetch loop
   * this board deleted is not coming back, and the clock is cleared when the page goes.
   */
  it('ticks a local clock that fetches nothing, and clears it on unmount', async () => {
    vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval'] });
    const page = await mountPage([team('alpha')]);
    const fetches = vi.mocked(fetch).mock.calls.length;
    vi.mocked(board.refresh).mockClear();
    vi.mocked(kanban.refreshIfActive).mockClear();

    vi.advanceTimersByTime(StalledBadgeGrace * 2);

    expect(vi.mocked(fetch).mock.calls.length).toBe(fetches);
    expect(board.refresh).not.toHaveBeenCalled();
    expect(kanban.refreshIfActive).not.toHaveBeenCalled();
    expect(vi.getTimerCount()).toBeGreaterThan(0);

    page.unmount();
    wrapper = undefined;
    expect(vi.getTimerCount()).toBe(0);
  });
});

/**
 * THE EMPTY CONSOLE SAYS WHAT A TEAM IS AND WHAT TO DO NEXT, in words a first-time person reads
 * once: New Team, or the Concierge. Not a line about how the platform is built.
 */
describe('the console with no team yet', () => {
  it('says what a team is and names New Team and the Concierge as the next step', async () => {
    const page = await mountPage([]);
    const text = page.text().replace(/\s+/g, ' ');

    expect(text).toContain('No teams yet');
    expect(text).toContain(
      'A team is a Manager and the members it takes on to do your work; to start one, choose New Team on the ribbon above or ask the Concierge.',
    );
    expect(text).not.toContain('door into it');
  });
});
