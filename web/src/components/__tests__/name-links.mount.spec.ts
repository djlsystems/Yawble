// @vitest-environment happy-dom
//
// EVERY NAME ON SCREEN IS A WAY IN, and each goes where it says: a Kanban card's member to that
// member's card on its board (the highlight deep link), its team to the board, its id to the
// clipboard; the Teams table's team name to the board, with a legend on the Activity heading and
// tooltips on clone, archive and delete; the Monitor's Open member, Open workflows and ticker team
// line; the Backlog's in-flight mark to the board and its dispatch line's workflow to that team's
// Workflows dialog. Every link is a hash link over `/console` or a store call the tabs make.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import LiveViewDialog from '../LiveViewDialog.vue';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import type { Message, TeamId } from '../../api/types';

const {
  follow, copyText, getWip, listRemovals, getMessages, getFileChanges, listEvents,
  backlogItems, backlogItem, listCatalog, fileSystemRoots, picks,
} = vi.hoisted(() => ({
  follow: vi.fn(),
  copyText: vi.fn(),
  getWip: vi.fn(),
  listRemovals: vi.fn(),
  getMessages: vi.fn(),
  getFileChanges: vi.fn(),
  listEvents: vi.fn(),
  backlogItems: vi.fn(),
  backlogItem: vi.fn(),
  listCatalog: vi.fn(),
  fileSystemRoots: vi.fn(),
  picks: [] as ((picked: unknown) => void)[],
}));

vi.mock('quasar', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  useQuasar: () => ({ notify: vi.fn(), dark: { isActive: false } }),
}));

vi.mock('../../lib/consoleLinks', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  follow,
}));

vi.mock('../../lib/clipboard', () => ({ copyText }));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  getWip, listRemovals, getMessages, getFileChanges, listEvents,
  backlogItems, backlogItem, listCatalog, fileSystemRoots,
}));

vi.mock('../../lib/monitor/font', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  loadMonitorSymbols: () => Promise.resolve(null),
}));

// The Monitor's scene, as it is, with the pick it is handed kept so a spec can pick a node.
vi.mock('../../lib/monitor/scene', async (importOriginal) => {
  const real = await importOriginal<typeof import('../../lib/monitor/scene')>();
  class PickableScene extends real.MonitorScene {
    constructor(...args: ConstructorParameters<typeof real.MonitorScene>) {
      super(...args);
      picks.push(args[1].onPick as (picked: unknown) => void);
    }
  }
  return { ...real, MonitorScene: PickableScene };
});

import KanbanCard from '../KanbanCard.vue';
import TeamsView from '../TeamsView.vue';
import MonitorView from '../MonitorView.vue';
import BacklogDialog from '../BacklogDialog.vue';
import { useConsoleStore } from '../../stores/console';
import { useWorkflowNavStore } from '../../stores/workflowNav';
import { useSessionStore } from '../../stores/session';
import { ActivityStates, stateWords } from '../../lib/teamActivity';
import type { KanbanCard as Card } from '../../api/kanban';
import { mountDialog, resetBody } from '../../test/mountQuasar';

let wrapper: VueWrapper | undefined;

beforeEach(() => {
  setActivePinia(createPinia());
  follow.mockReset();
  copyText.mockReset();
  copyText.mockResolvedValue(undefined);
  getWip.mockResolvedValue({ running: [], waiting: [], limit: null });
  listRemovals.mockResolvedValue([]);
  listEvents.mockResolvedValue([]);
  getFileChanges.mockResolvedValue({ changes: [], next: null });
  listCatalog.mockResolvedValue({ agents: [] });
  fileSystemRoots.mockResolvedValue({ roots: [] });
  picks.length = 0;
  vi.stubGlobal('fetch', vi.fn(() => new Promise(() => {})));
});

afterEach(() => {
  wrapper?.unmount();
  wrapper = undefined;
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  resetBody();
});

const card = (over: Partial<Card> = {}): Card => ({
  id: '2310_Mailer',
  team: 'beta',
  member: 'Mailer',
  item: null,
  workflowSeq: 2310,
  title: 'Send the digest',
  body: '',
  status: 'running',
  laneId: 'doing',
  progress: [],
  createdAt: '2026-09-18T00:00:00Z',
  updatedAt: '2026-09-18T00:00:00Z',
  awaitingManager: false,
  paused: false,
  ...over,
}) as Card;

describe('a Kanban card', () => {
  it('opens its member\'s card on the board from the member\'s name, and not the card panel', async () => {
    wrapper = mount(KanbanCard, { props: { card: card(), teamName: 'Beta team' } });

    await wrapper.find('[data-card-member]').trigger('click');

    expect(follow).toHaveBeenCalledWith('#/console?team=beta&member=Mailer');
    expect(wrapper.find('[data-card-member]').attributes('data-href')).toBe('#/console?team=beta&member=Mailer');
    expect(wrapper.emitted('open')).toBeUndefined();
  });

  it('opens the board from the team chip, named by the team\'s name', async () => {
    wrapper = mount(KanbanCard, { props: { card: card(), teamName: 'Beta team' } });

    const team = wrapper.find('[data-card-team]');
    expect(team.text()).toBe('Beta team');
    await team.trigger('click');

    expect(follow).toHaveBeenCalledWith('#/console?team=beta');
    expect(wrapper.emitted('open')).toBeUndefined();
  });

  it('copies its own id on a click, which is what tell --card takes, and says so', async () => {
    wrapper = mount(KanbanCard, { props: { card: card() } });

    await wrapper.find('[data-card-copy]').trigger('click');
    await flushPromises();

    expect(copyText).toHaveBeenCalledWith('2310_Mailer');
    expect(wrapper.find('[data-card-copy]').text()).toBe('copied');
    expect(wrapper.emitted('open')).toBeUndefined();
  });

  it('flashes when the board says it landed, and offers no member link for an unassigned card', async () => {
    wrapper = mount(KanbanCard, { props: { card: card({ member: null }), flashing: true } });

    expect(wrapper.find('.k-card').classes()).toContain('os-flash');
    expect(wrapper.find('[data-card-member]').exists()).toBe(false);
    expect(wrapper.find('.k-card-unassigned').text()).toBe('Unassigned');
  });
});

const container = (team: string, id: string) => ({
  team, id, name: id, agent: 'claude-headless', state: 'Idle', queueDepth: 0, ceiling: 16,
  subscribes: [], currentCorrelation: null, sinceSeq: 0,
});

async function showTooltip(host: VueWrapper | ReturnType<VueWrapper['find']>): Promise<string> {
  const tooltip = (host as VueWrapper).findComponent({ name: 'QTooltip' });
  expect(tooltip.exists(), 'no tooltip').toBe(true);
  (tooltip.vm as unknown as { show: () => void }).show();
  await flushPromises();
  const text = [...document.body.querySelectorAll('.q-tooltip')].map((node) => node.textContent ?? '').join(' | ');
  (tooltip.vm as unknown as { hide: () => void }).hide();
  await flushPromises();
  return text.replace(/\s+/g, ' ').trim();
}

describe('the Teams table', () => {
  async function mountTeams() {
    const board = useConsoleStore();
    board.$patch({
      teams: [{ id: 'alpha' as TeamId, name: 'Alpha', paused: false, containers: [container('alpha', 'Manager')] }],
    } as never);
    vi.spyOn(board, 'refresh').mockResolvedValue();
    vi.spyOn(board, 'refreshRollupIfShowing').mockImplementation(() => {});
    const openTeam = vi.spyOn(board, 'openTeam').mockImplementation(() => {});
    wrapper = mount(TeamsView, {
      attachTo: document.body,
      global: { stubs: { TeamStatisticsTile: { template: '<div class="tile-stub" />', props: ['teamId', 'containers', 'workflows', 'compact', 'range'], emits: ['own-window'] } } },
    });
    await flushPromises();
    return { view: wrapper, openTeam };
  }

  it('makes the team name a link to the board, opening it in place', async () => {
    const { view, openTeam } = await mountTeams();

    const name = view.find('[data-team-name]');
    expect(name.element.tagName).toBe('A');
    expect(name.attributes('href')).toBe('#/console?team=alpha');
    expect(name.text()).toBe('Alpha');

    await name.trigger('click');
    expect(openTeam).toHaveBeenCalledTimes(1);
    expect(openTeam).toHaveBeenCalledWith('alpha');
  });

  it('gives the Activity heading a legend on hover: lanes, initials and every state\'s words', async () => {
    const { view } = await mountTeams();

    const legend = await showTooltip(view.find('[data-activity-legend-anchor]') as never);

    expect(legend).toContain('One lane per member, named by its initials; hover the initials for the full name.');
    for (const state of ActivityStates) expect(legend).toContain(stateWords(state));
    expect(legend).toContain("A click on a team's lanes opens its Statistics.");
  });

  it('keeps the aria-labels on clone, archive and delete, and gives each a tooltip sentence like Pause\'s', async () => {
    const { view } = await mountTeams();
    const button = (label: string) => view.find(`[aria-label="${label}"]`);

    expect(await showTooltip(button('Clone this team') as never)).toBe('Clone this team: a new team with the same roster and settings');
    expect(await showTooltip(button('Archive this team') as never)).toBe('Archive this team: everything is kept, and it does no work until unarchived');
    expect(await showTooltip(button('Delete this team') as never)).toBe('Delete this team permanently');
  });
});

let seq = 0;
function row(type: string, source: string, payload: Record<string, unknown>, at: string, correlationId = 1): Message {
  seq++;
  return { seq, type, source, payload: JSON.stringify(payload), correlationId, causationSeq: null, depth: 0, occurredAt: at };
}

describe('the Monitor', () => {
  async function mountMonitor() {
    seq = 0;
    getMessages.mockResolvedValue([
      row('agentContainer.instruction.Alpha/Developer', 'Alpha/Manager', { instruction: 'Write it', subject: 'Write it' }, '2026-10-15T10:00:00Z'),
      row('agentContainer.started', 'Alpha/Developer', {}, '2026-10-15T10:00:01Z'),
    ]);
    const board = useConsoleStore();
    board.$patch({
      teams: [{ id: 'Alpha', name: 'Alpha team', containers: [container('Alpha', 'Manager'), container('Alpha', 'Developer')] }] as never,
      managerName: 'Manager' as never,
    });
    board.view = 'monitor';
    const openTeam = vi.spyOn(board, 'openTeam').mockImplementation(() => {});
    wrapper = mount(MonitorView, { attachTo: document.body });
    await flushPromises();
    return { view: wrapper, openTeam };
  }

  async function pick(picked: unknown) {
    picks.at(-1)!(picked);
    await flushPromises();
  }

  it("offers Open member on a picked member, opening the watch dialog the board's eye opens over the Monitor", async () => {
    const { view } = await mountMonitor();
    await pick({ kind: 'member', key: 'Alpha/Developer' });

    const open = view.find('[data-monitor-open-member]');
    expect(open.exists()).toBe(true);
    expect(open.attributes('href'), 'it is not a link away from the Monitor').toBeUndefined();
    expect(view.findComponent(LiveViewDialog).exists()).toBe(false);

    await open.trigger('click');
    await flushPromises();

    const dialog = view.findComponent(LiveViewDialog);
    expect(dialog.exists()).toBe(true);
    expect(dialog.props('team')).toBe('Alpha');
    expect(dialog.props('member')).toBe('Developer');
    expect(useConsoleStore().view).toBe('monitor');
  });

  it('keeps Open member a link to the card when the board does not hold the member', async () => {
    const { view } = await mountMonitor();
    useConsoleStore().teams[0]!.containers = [] as never;
    await pick({ kind: 'member', key: 'Alpha/Developer' });

    expect(view.find('[data-monitor-open-member]').attributes('href')).toBe('#/console?team=Alpha&member=Developer');
  });

  it('offers Open workflows on a picked workflow with no item, opening that team\'s Workflows dialog', async () => {
    const { view } = await mountMonitor();
    await pick({ kind: 'workflow', id: 1 });
    const nav = useWorkflowNavStore();

    expect(view.find('[data-monitor-open-item]').exists()).toBe(false);
    await view.find('[data-monitor-open-workflows]').trigger('click');

    expect(nav.workflowsTeam).toBe('Alpha');
    expect(nav.workflowsAsks).toBe(1);
  });

  it('makes the ticker\'s team line a link to that team\'s board', async () => {
    const { view, openTeam } = await mountMonitor();

    const team = view.find('[data-monitor-row-team]');
    expect(team.attributes('href')).toBe('#/console?team=Alpha');
    expect(team.text()).toBe('Alpha team');

    await team.trigger('click');
    expect(openTeam).toHaveBeenCalledWith('Alpha');
  });
});

describe('the Backlog dialog', () => {
  const item = {
    id: 3, team: null, teamName: null, teamGone: false, title: 'Job tracker', body: 'Build it.',
    state: 'ready', archivedAt: null, createdAt: '2026-09-01T00:00:00Z', updatedAt: '2026-09-02T00:00:00Z',
    createdBy: 'someone@example.com',
    inFlight: { teamId: 'builders', teamName: 'Builders', correlation: 40, running: true },
  };

  async function mountBacklog() {
    const board = useConsoleStore();
    board.teams = [{ id: 'builders', name: 'Builders', containers: [] }] as never;
    useSessionStore().user = { email: 'admin@example.com' } as never;
    const openTeam = vi.spyOn(board, 'openTeam').mockImplementation(() => {});
    backlogItems.mockResolvedValue([item]);
    backlogItem.mockResolvedValue({
      item,
      dispatches: [{
        id: 1, teamId: 'builders', teamName: 'Builders', correlation: 40,
        dispatchedAt: '2026-09-29T09:00:00Z', dispatchedBy: 'admin@example.com', frozenAt: null, teamGone: false,
      }],
      stats: [],
    });
    await mountDialog(BacklogDialog, {}, { pinia: false });
    await flushPromises();
    return { openTeam };
  }

  it('opens the board of the team working an item from its in-flight mark, leaving the dialog and the item closed', async () => {
    const { openTeam } = await mountBacklog();
    const nav = useWorkflowNavStore();
    nav.backlogOpen = true;

    const mark = document.querySelector<HTMLAnchorElement>('[data-inflight-board]')!;
    expect(mark.getAttribute('href')).toBe('#/console?team=builders');
    mark.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
    await flushPromises();

    expect(openTeam).toHaveBeenCalledWith('builders');
    expect(nav.backlogOpen).toBe(false);
    expect(backlogItem).not.toHaveBeenCalled();
  });

  it('opens that team\'s Workflows dialog from the dispatch line\'s workflow', async () => {
    await mountBacklog();
    [...document.querySelectorAll('tr')]
      .find((found) => found.textContent?.includes('Job tracker'))!
      .dispatchEvent(new Event('click', { bubbles: true }));
    await flushPromises();
    const nav = useWorkflowNavStore();

    const number = document.querySelector<HTMLElement>('.backlog-dispatch [data-workflow-number]')!;
    expect(number.textContent).toContain('40');
    number.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    await flushPromises();

    expect(nav.workflowsTeam).toBe('builders');
  });
});
