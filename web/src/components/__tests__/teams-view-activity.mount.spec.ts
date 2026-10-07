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

  it('reports its own window to the list, and nothing for a team that never ran', async () => {
    const tile = await mountTile();
    expect(tile.emitted('own-window')!.at(-1)).toEqual([{ from: Date.parse(iso(0)), to: Date.parse(iso(30)) }]);

    wrapper!.unmount();
    const none = await mountTile({ ...activity, window: 'none', from: null, to: null, members: [] } as TeamActivity);
    expect(none.emitted('own-window')!.at(-1)).toEqual([null]);
  });

  it('draws its axis over the range the list shares, and over its own window without one', async () => {
    const tile = await mountTile();
    const axis = () => (tile.findComponent({ name: 'echarts' }).props('option') as { xAxis: { min: number; max: number } }).xAxis;

    expect(axis()).toMatchObject({ min: Date.parse(iso(0)), max: Date.parse(iso(30)) });

    await tile.setProps({ range: { from: Date.parse(iso(-60)), to: Date.parse(iso(90)) } });
    expect(axis()).toMatchObject({ min: Date.parse(iso(-60)), max: Date.parse(iso(90)) });
  });

  it('shows a dash, not an empty cell, for a team that never ran', async () => {
    const tile = await mountTile({ ...activity, window: 'none', from: null, to: null, members: [] } as TeamActivity);

    expect(tile.find('[data-activity-none]').text()).toBe('—');
  });
});

describe('the Teams list', () => {
  async function mountView(latest: string | null = null, roster = members, extraTeams: { id: string; name: string }[] = []) {
    setActivePinia(createPinia());
    const board = useConsoleStore();
    board.$patch({
      teams: [
        { id: 'alpha' as TeamId, name: 'Alpha', paused: false, containers: roster },
        ...extraTeams.map((t) => ({ id: t.id as TeamId, name: t.name, paused: false, containers: roster })),
      ],
    } as never);
    if (latest !== null) {
      board.$patch({
        workflows: {
          alpha: {
            available: true, openCount: 0, totalCount: 1, earliestStartedAt: null, serverNow: iso(30),
            workflows: [{
              available: true, correlation: 1, state: latest, startedAt: iso(0), endedAt: iso(30), serverNow: iso(30),
              executionSeconds: 0, partial: false, runsCounted: 0, runsUnfinished: 0, blockedBy: null, runsFailed: 0,
              failedMembers: [], missing: null, members: [], lastActivityAt: iso(30), awaitingFrom: null, subject: null,
              pausedAt: null, pausedReason: null, pausedLimit: null,
            }],
            missing: null,
          },
        },
      } as never);
    }
    vi.spyOn(board, 'refresh').mockResolvedValue();
    vi.spyOn(board, 'refreshRollupIfShowing').mockImplementation(() => {});
    const setActive = vi.spyOn(board, 'setActiveTeam').mockResolvedValue(undefined as never);

    wrapper = mount(TeamsView, {
      attachTo: document.body,
      global: { stubs: { TeamStatisticsTile: { template: '<div class="tile-stub" />', props: ['teamId', 'containers', 'workflows', 'compact', 'range'], emits: ['own-window'] } } },
    });
    await flushPromises();
    return { view: wrapper, setActive };
  }

  it('shows Activity in place of the Workflows and Last workflow columns', async () => {
    const { view } = await mountView();

    // The sorted heading carries its arrow's icon name as text; the words are what is checked.
    const headings = view.findAll('thead th').map((th) => th.text().replace(/arrow_(up|down)ward/, '').replace(/\s+/g, ' ').trim());
    expect(headings).toEqual(['Team', 'Members', 'Status', 'ActivityRelative', '']);
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
  it('shows a green check to the right of the chart for a team whose latest workflow was completed', async () => {
    const { view } = await mountView('Completed');

    const cell = view.find('td.teams-activity');
    const icon = cell.find('[data-team-status]');
    expect(icon.exists()).toBe(true);
    expect(icon.classes()).toContain('team-status-icon--positive');
    expect(icon.attributes('aria-label')).toBe('completed');
    expect(icon.text()).toContain('check_circle');

    // To the RIGHT of the chart: the chart first in the cell, the icon after it.
    const children = cell.find('.teams-activity-cell').element.children;
    expect(children[0]!.classList.contains('tile-stub')).toBe(true);
    expect(children[1]!.hasAttribute('data-team-status')).toBe(true);
  });

  it('shows a running team as running, with no check', async () => {
    const { view } = await mountView('Completed', [{ ...container('Manager'), state: 'Running' }] as never);

    const icon = view.find('td.teams-activity [data-team-status]');
    expect(icon.attributes('data-team-status')).toBe('running');
    expect(icon.text()).toContain('sync');
  });

  it('shows no icon for a team that has run no workflow', async () => {
    const { view } = await mountView();

    expect(view.find('td.teams-activity [data-team-status]').exists()).toBe(false);
  });

  describe('the Relative switch in the Activity heading', () => {
    beforeEach(() => localStorage.removeItem('harness.teamsActivityRelative'));

    const tileFor = (view: VueWrapper, id: string) =>
      view.findAllComponents(TeamStatisticsTile).find((tile) => tile.props('teamId') === id)!;

    it('is off by default, and each chart keeps its own window', async () => {
      const { view } = await mountView(null, members, [{ id: 'beta', name: 'Beta' }]);

      expect(view.find('[data-activity-relative]').attributes('aria-checked')).toBe('false');
      tileFor(view, 'alpha').vm.$emit('own-window', { from: 100, to: 400 });
      tileFor(view, 'beta').vm.$emit('own-window', { from: 200, to: 900 });
      await flushPromises();

      expect(tileFor(view, 'alpha').props('range')).toBeNull();
      expect(tileFor(view, 'beta').props('range')).toBeNull();
    });

    it('on, gives every chart the earliest start to the latest end of the teams with a chart', async () => {
      const { view } = await mountView(null, members, [{ id: 'beta', name: 'Beta' }, { id: 'gamma', name: 'Gamma' }]);

      tileFor(view, 'alpha').vm.$emit('own-window', { from: 100, to: 400 });
      tileFor(view, 'beta').vm.$emit('own-window', { from: 200, to: 900 });
      tileFor(view, 'gamma').vm.$emit('own-window', null);
      await view.find('[data-activity-relative]').trigger('click');
      await flushPromises();

      for (const id of ['alpha', 'beta', 'gamma']) expect(tileFor(view, id).props('range')).toEqual({ from: 100, to: 900 });
    });

    it('is kept in this browser and read back on the next visit', async () => {
      const first = await mountView();
      await first.view.find('[data-activity-relative]').trigger('click');
      expect(localStorage.getItem('harness.teamsActivityRelative')).toBe('1');
      first.view.unmount();

      const { view } = await mountView();
      expect(view.find('[data-activity-relative]').attributes('aria-checked')).toBe('true');

      await view.find('[data-activity-relative]').trigger('click');
      expect(localStorage.getItem('harness.teamsActivityRelative')).toBe('0');
    });
  });
});
