// @vitest-environment happy-dom
//
// THE TEAMS LIST'S ACTIVITY COLUMN: each team's activity lanes in place of the Workflows and Last
// workflow columns, drawn by the team page's own Activity tile in its compact form - no label, no
// caption - and a click on it opens that team's Activity dialog rather than the team.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';

const { getWip, listRemovals } = vi.hoisted(() => ({ getWip: vi.fn(), listRemovals: vi.fn() }));

vi.mock('quasar', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  useQuasar: () => ({ notify: vi.fn(), dark: { isActive: false } }),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  getWip,
  listRemovals,
}));

import TeamsView from '../TeamsView.vue';
import TeamStatisticsTile from '../TeamStatisticsTile.vue';
import TeamStatisticsDialog from '../TeamStatisticsDialog.vue';
import { useConsoleStore } from '../../stores/console';
import { asTeamId, type TeamActivity, type TeamId } from '../../api/types';
import { resetBody } from '../../test/mountQuasar';

const teamId = asTeamId('alpha');
const iso = (minutes: number) => new Date(Date.UTC(2026, 9, 7, 14, minutes)).toISOString();

const container = (id: string) => ({
  team: teamId, id, name: id, agent: 'claude-headless', state: 'Idle', queueDepth: 0, ceiling: 16,
  subscribes: [], currentCorrelation: null, sinceSeq: 0,
});

const members = [container('Manager'), container('Developer')];

const activity: TeamActivity = {
  from: iso(0),
  to: iso(30),
  serverNow: iso(30),
  window: 'workflows',
  members: [
    { member: 'Manager', kind: 'agent', isManager: true, current: true, spans: [{ state: 'running', from: iso(0), to: iso(10), workflow: 7 }] },
    { member: 'Developer', kind: 'agent', isManager: false, current: true, spans: [{ state: 'running', from: iso(5), to: iso(30), workflow: 7 }] },
  ],
} as TeamActivity;

let wrapper: VueWrapper | undefined;

beforeEach(() => {
  getWip.mockResolvedValue({ running: [], waiting: [], limit: null });
  listRemovals.mockResolvedValue([]);
  vi.stubGlobal('fetch', vi.fn(async (url: string) =>
    String(url).endsWith('/activity')
      ? new Response(JSON.stringify(activity), { status: 200, headers: { 'Content-Type': 'application/json' } })
      : new Response('{}', { status: 404 })));
});

afterEach(() => {
  wrapper?.unmount();
  wrapper = undefined;
  vi.unstubAllGlobals();
  resetBody();
});

describe('the compact Activity tile', () => {
  async function mountTile(answer: TeamActivity = activity) {
    vi.stubGlobal('fetch', vi.fn(async () =>
      new Response(JSON.stringify(answer), { status: 200, headers: { 'Content-Type': 'application/json' } })));
    setActivePinia(createPinia());
    wrapper = mount(TeamStatisticsTile, {
      props: { teamId, containers: members as never, workflows: null, compact: true },
      attachTo: document.body,
    });
    await flushPromises();
    await flushPromises();
    return wrapper;
  }

  it('draws the lanes with no label and no caption under them', async () => {
    const tile = await mountTile();

    expect(tile.find('.stats-label').exists()).toBe(false);
    expect(tile.find('.stats-caption').exists()).toBe(false);
    expect(tile.findAll('.stats-lane-initials')).toHaveLength(2);
  });

  it('opens the Activity dialog on a click anywhere, a touch tap included', async () => {
    const tile = await mountTile();

    await tile.trigger('pointerdown', { pointerType: 'touch' });
    await tile.trigger('click');

    expect(tile.findComponent(TeamStatisticsDialog).props('modelValue')).toBe(true);
  });

  it('shows a dash, not an empty cell, for a team that never ran', async () => {
    const tile = await mountTile({ ...activity, window: 'none', from: null, to: null, members: [] } as TeamActivity);

    expect(tile.find('[data-activity-none]').text()).toBe('—');
  });
});

describe('the Teams list', () => {
  async function mountView() {
    setActivePinia(createPinia());
    const board = useConsoleStore();
    board.$patch({ teams: [{ id: 'alpha' as TeamId, name: 'Alpha', paused: false, containers: members }] } as never);
    vi.spyOn(board, 'refresh').mockResolvedValue();
    vi.spyOn(board, 'refreshRollupIfShowing').mockImplementation(() => {});
    const setActive = vi.spyOn(board, 'setActiveTeam').mockResolvedValue(undefined as never);

    wrapper = mount(TeamsView, {
      attachTo: document.body,
      global: { stubs: { TeamStatisticsTile: { template: '<div class="tile-stub" />', props: ['teamId', 'containers', 'workflows', 'compact'] } } },
    });
    await flushPromises();
    return { view: wrapper, setActive };
  }

  it('shows Activity in place of the Workflows and Last workflow columns', async () => {
    const { view } = await mountView();

    // The sorted heading carries its arrow's icon name as text; the words are what is checked.
    const headings = view.findAll('thead th').map((th) => th.text().replace(/arrow_(up|down)ward/, '').replace(/\s+/g, ' ').trim());
    expect(headings).toEqual(['Team', 'Members', 'Status', 'Activity', '']);
    expect(view.find('thead th[data-col="activity"]').exists()).toBe(true);
  });

  it('draws each team\'s compact Activity tile in its row', async () => {
    const { view } = await mountView();

    const tile = view.findComponent(TeamStatisticsTile);
    expect(tile.exists()).toBe(true);
    expect(tile.props('compact')).not.toBe(false);
    expect(tile.props('teamId')).toBe('alpha');
    expect(tile.props('containers')).toHaveLength(2);
  });

  it('does not open the team when its Activity cell is clicked', async () => {
    const { view, setActive } = await mountView();

    await view.find('td.teams-activity').trigger('click');

    expect(setActive).not.toHaveBeenCalled();
  });
});
