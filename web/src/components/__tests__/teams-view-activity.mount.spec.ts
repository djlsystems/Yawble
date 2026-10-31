// @vitest-environment happy-dom
//
// THE ACTIVITY TILE'S COMPACT FORM: lanes with no label and no caption, a click opening the Activity
// dialog. The teams list drew it in its Activity column until that column became Cost · 8 weeks
// (`teams-view-cost.mount.spec.ts`); the form itself is still the tile's.
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

import TeamStatisticsTile from '../TeamStatisticsTile.vue';
import TeamStatisticsDialog from '../TeamStatisticsDialog.vue';
import { asTeamId, type TeamActivity } from '../../api/types';
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

  it('hides its hover box and hover line as soon as the pointer leaves the lanes', async () => {
    const tile = await mountTile();
    const chart = tile.findComponent({ name: 'echarts' });
    const dispatch = vi.spyOn((chart.vm as unknown as { $: { exposed: { dispatchAction: (action: unknown) => void } } }).$.exposed, 'dispatchAction');

    await tile.find('.stats-body').trigger('pointerleave');

    expect(dispatch).toHaveBeenCalledWith({ type: 'hideTip' });
    expect(dispatch).toHaveBeenCalledWith({ type: 'updateAxisPointer', currTrigger: 'leave' });
  });

  it('shows a dash, not an empty cell, for a team that never ran', async () => {
    const tile = await mountTile({ ...activity, window: 'none', from: null, to: null, members: [] } as TeamActivity);

    expect(tile.find('[data-activity-none]').text()).toBe('—');
  });
});
