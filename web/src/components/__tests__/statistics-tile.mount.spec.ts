// @vitest-environment happy-dom
//
// THE ACTIVITY TILE ON THE TEAM BOARD: where it sits in the strip, which lanes it draws, what its
// caption and tooltip say, and when it reads `/activity` again.
//
// The words and the order are pinned without a DOM in `lib/__tests__/teamActivity.spec.ts`; this
// file pins that the strip renders what those functions decide, and the one thing they cannot see -
// that the tile refetches on a change the board pushes and never on a clock.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';

import TeamKpiStrip from '../TeamKpiStrip.vue';
import { useConsoleStore } from '../../stores/console';
import { asTeamId } from '../../api/types';
import type { ActivityMember, TeamActivity, TeamWorkflowTiming, TeamWorkflows } from '../../api/types';
import { resetBody } from '../../test/mountQuasar';
import { localStretch } from '../../lib/localTime';

const teamId = asTeamId('alpha');

/** A local wall-clock instant; the words for it are computed with the browser's own locale. */
const at = (hours: number, minutes: number, seconds = 0) => new Date(2026, 9, 4, hours, minutes, seconds).getTime();
const iso = (ms: number) => new Date(ms).toISOString();

function container(id: string, name: string, over: Record<string, unknown> = {}) {
  return {
    team: teamId,
    id,
    name,
    agent: 'claude-headless',
    state: 'Idle',
    queueDepth: 0,
    ceiling: 16,
    subscribes: [],
    currentCorrelation: null,
    sinceSeq: 0,
    ...over,
  };
}

/** The board lists the Tester before the Developer; the Manager is last on the board. */
const board = [
  container('Tester', 'Tester Okon'),
  container('DeveloperInes', 'Developer Ines'),
  container('Manager', 'Manager'),
];

function member(name: string, over: Partial<ActivityMember> = {}): ActivityMember {
  return {
    member: name,
    kind: 'agent',
    isManager: name === 'Manager',
    current: true,
    spans: [{ state: 'running', from: iso(at(14, 2)), to: null, workflow: 7 }],
    ...over,
  };
}

function activity(over: Partial<TeamActivity> = {}): TeamActivity {
  return {
    from: iso(at(14, 2)),
    to: iso(at(14, 30)),
    serverNow: iso(at(14, 30)),
    window: 'workflows',
    members: [
      member('Manager'),
      member('DeveloperInes'),
      member('Tester'),
      member('Gone', { current: false }),
    ],
    ...over,
  };
}

function workflows(over: Partial<TeamWorkflows> = {}): TeamWorkflows {
  return {
    available: true,
    openCount: 2,
    totalCount: 2,
    earliestStartedAt: iso(at(14, 2)),
    serverNow: iso(at(14, 30)),
    workflows: [],
    missing: null,
    ...over,
  } as TeamWorkflows;
}

/** A closed workflow in the shape the server really sends. */
function closedWorkflow(startedAt: string, endedAt: string): TeamWorkflowTiming {
  return {
    available: true,
    correlation: 5,
    state: 'Completed',
    startedAt,
    endedAt,
    serverNow: iso(at(14, 30)),
    executionSeconds: 0,
    partial: false,
    runsCounted: 0,
    runsUnfinished: 0,
    blockedBy: null,
    runsFailed: 0,
    failedMembers: [],
    missing: null,
    members: [],
    lastActivityAt: endedAt,
    awaitingFrom: null,
    subject: 'Ship the tile',
    pausedAt: null,
    pausedReason: null,
    pausedLimit: null,
  } as TeamWorkflowTiming;
}

let answer: TeamActivity;
let fetchMock: ReturnType<typeof vi.fn>;
let wrapper: VueWrapper | undefined;

/** Every `/activity` read the tile made. */
const activityReads = () =>
  fetchMock.mock.calls.filter(([url]) => String(url).endsWith(`/api/teams/${teamId}/activity`)).length;

beforeEach(() => {
  answer = activity();
  fetchMock = vi.fn(async (url: string) =>
    String(url).endsWith('/activity')
      ? new Response(JSON.stringify(answer), { status: 200, headers: { 'Content-Type': 'application/json' } })
      : new Response('{}', { status: 404 }));
  vi.stubGlobal('fetch', fetchMock);
});

afterEach(() => {
  wrapper?.unmount();
  wrapper = undefined;
  vi.useRealTimers();
  vi.unstubAllGlobals();
  resetBody();
});

async function mountStrip(props: Record<string, unknown> = {}) {
  setActivePinia(createPinia());
  useConsoleStore().$patch({
    teams: [{ id: teamId, name: 'Alpha', memberAgents: [], repos: [] }],
    activeTeamId: teamId,
    overviewLanded: true,
    workflowSpendLimit: null,
  } as never);

  wrapper = mount(TeamKpiStrip, {
    props: {
      teamId,
      containers: board as never,
      usage: null,
      timing: null,
      workflows: workflows(),
      clockOffset: 0,
      ...props,
    },
    attachTo: document.body,
  });

  await flushPromises();
  await flushPromises();

  return wrapper;
}

const tile = () => wrapper!.find('.team-kpi--statistics');

describe('the Activity tile', () => {
  it('sits between the Workflow tile and the Tokens tile', async () => {
    await mountStrip();

    const tiles = wrapper!.findAll('.team-kpi-strip > .team-kpi').map((t) => t.find('.team-kpi-label').text());

    expect(tiles.map((label) => label.split(/\s/)[0])).toEqual(['Workflow', 'Activity', 'Tokens']);
  });

  it('draws one lane per current member with its initials, the Manager first, then board order', async () => {
    await mountStrip();

    expect(tile().findAll('.stats-lane-initials').map((lane) => lane.text())).toEqual(['M', 'TO', 'DI']);
  });

  it('gives a removed member no lane', async () => {
    await mountStrip();

    expect(tile().findAll('.stats-lane-initials')).toHaveLength(3);
    expect(tile().text()).not.toContain('Gone');
  });

  it('captions the window with its stretch, in the browser\'s locale, and the open workflows', async () => {
    await mountStrip();

    expect(tile().find('.stats-caption').text()).toBe(`${localStretch(at(14, 2), at(14, 30))} · 2 open workflows`);
    expect(tile().find('.stats-chart').exists()).toBe(true);
  });

  it('captions a window of closed workflows by its stretch alone', async () => {
    answer = activity({ from: iso(at(9, 0, 5)), to: iso(at(9, 15, 40)) });

    await mountStrip({
      workflows: workflows({
        openCount: 0,
        earliestStartedAt: null,
        workflows: [closedWorkflow(iso(at(9, 0, 5)), iso(at(9, 15, 40)))],
      }),
    });

    expect(tile().find('.stats-caption').text()).toBe(localStretch(at(9, 0, 5), at(9, 15, 40)));
  });

  it('reads No workflows yet and draws no chart for a team that never ran', async () => {
    answer = { from: null, to: null, serverNow: iso(at(14, 30)), window: 'none', members: [] };

    await mountStrip({ workflows: workflows({ available: false, openCount: 0, totalCount: 0, earliestStartedAt: null }) });

    expect(tile().find('.stats-caption').text()).toBe('No workflows yet');
    expect(tile().find('.stats-chart').exists()).toBe(false);
    expect(tile().findAll('.stats-lane-initials')).toHaveLength(0);
  });

  it('summarises itself in its aria-label', async () => {
    await mountStrip();

    expect(tile().attributes('aria-label')).toBe(`Activity ${localStretch(at(14, 2), at(14, 30))}: 3 members - 3 running`);
  });

  it('counts a member waiting for a slot in its aria-label, and gives the reason in the tooltip', async () => {
    answer = activity({ members: [
      member('Manager'),
      member('DeveloperInes', { spans: [{
        state: 'held', from: iso(at(14, 26)), to: null,
        reason: 'waiting for memory: 11.2 of 12.9 GB in use', reasonKind: 'memory',
      }] }),
      member('Tester'),
    ] });

    await mountStrip({ containers: [container('Manager', 'Manager'), container('DeveloperInes', 'Ines Lopez'), container('Tester', 'Tester Okon')] });

    expect(tile().attributes('aria-label'))
      .toBe(`Activity ${localStretch(at(14, 2), at(14, 30))}: 3 members - 2 running, 1 waiting for a slot`);

    const chart = wrapper!.findComponent({ name: 'Echarts' });
    const option = chart.props('option') as { tooltip: { formatter: (params: unknown) => string } };
    const box = document.createElement('div');
    box.innerHTML = option.tooltip.formatter([{ axisValue: at(14, 30) }]);

    expect(box.textContent).toContain('IL Ines Lopez — waiting for a slot 4 min (memory: 11.2 of 12.9 GB in use)');
  });

  it('escapes a name in the tooltip, so markup shows as characters', async () => {
    answer = activity({ members: [member('Manager'), member('Bold', { spans: [
      { state: 'blocked', from: iso(at(14, 5)), to: null, reason: '<i>key</i>' },
    ] })] });

    await mountStrip({ containers: [container('Manager', 'Manager'), container('Bold', '<b>Bold</b>')] });

    const chart = wrapper!.findComponent({ name: 'Echarts' });
    const option = chart.props('option') as { tooltip: { formatter: (params: unknown) => string } };
    const html = option.tooltip.formatter([{ axisValue: at(14, 10) }]);

    const box = document.createElement('div');
    box.innerHTML = html;

    expect(box.querySelector('b')).toBeNull();
    expect(box.querySelector('i')).toBeNull();
    expect(box.textContent).toContain('<b>Bold</b> — blocked');
    expect(box.textContent).toContain('(<i>key</i>)');
  });

  it('names the time under the pointer, not the nearest span edge the axis snaps to', async () => {
    await mountStrip();

    const chart = wrapper!.findComponent({ name: 'Echarts' });
    const plot = tile().find('.stats-chart').element as HTMLElement;
    plot.getBoundingClientRect = () => ({ x: 0, y: 0, left: 0, top: 0, right: 280, bottom: 42, width: 280, height: 42, toJSON: () => ({}) });

    // Halfway across a window from 14:02 to 14:30; ECharts hands the formatter the nearest span start.
    chart.vm.$emit('zr:mousemove', { offsetX: 140 });
    await flushPromises();

    const option = chart.props('option') as { tooltip: { formatter: (params: unknown) => string } };
    const box = document.createElement('div');
    box.innerHTML = option.tooltip.formatter([{ axisValue: at(14, 2) }]);

    expect(box.querySelector('.stats-tip-time')!.textContent).toBe(new Date(at(14, 16)).toLocaleTimeString());
  });

  it('reads the time under the pointer as the browser\'s locale reads it', async () => {
    await mountStrip();

    const chart = wrapper!.findComponent({ name: 'Echarts' });
    const option = chart.props('option') as { tooltip: { formatter: (params: unknown) => string } };
    const box = document.createElement('div');
    box.innerHTML = option.tooltip.formatter([{ axisValue: at(14, 10, 7) }]);

    expect(box.querySelector('.stats-tip-time')!.textContent).toBe(new Date(at(14, 10, 7)).toLocaleTimeString());
  });
});

describe('the Activity tile keeps itself current', () => {
  it('refetches when a member changes state, and never on a timer', async () => {
    vi.useFakeTimers({ now: at(14, 30), toFake: ['setTimeout', 'clearTimeout', 'setInterval', 'clearInterval', 'Date'] });

    await mountStrip();
    expect(activityReads()).toBe(1);

    // A board left open with nothing changing reads nothing more. Two minutes, not ten: every
    // simulated second re-renders the strip on its clock tick, and ten minutes twice ran past the
    // test timeout on a busy machine while proving nothing more than two do.
    await vi.advanceTimersByTimeAsync(2 * 60_000);
    expect(activityReads()).toBe(1);

    await wrapper!.setProps({
      containers: [board[0], container('DeveloperInes', 'Developer Ines', { state: 'Running', currentCorrelation: 7 }), board[2]] as never,
    });
    await vi.advanceTimersByTimeAsync(1_000);
    expect(activityReads()).toBe(2);

    await vi.advanceTimersByTimeAsync(2 * 60_000);
    expect(activityReads()).toBe(2);
  });

  it('reads at most once a second however fast the changes come', async () => {
    vi.useFakeTimers({ now: at(14, 30), toFake: ['setTimeout', 'clearTimeout', 'setInterval', 'clearInterval', 'Date'] });

    await mountStrip();

    for (const blocked of ['a', 'b', 'c']) {
      await wrapper!.setProps({
        containers: [board[0], container('DeveloperInes', 'Developer Ines', { blocked }), board[2]] as never,
      });
      await vi.advanceTimersByTimeAsync(100);
    }

    await vi.advanceTimersByTimeAsync(1_000);
    expect(activityReads()).toBe(2);
  });

  it('refetches when the open-workflow list changes', async () => {
    vi.useFakeTimers({ now: at(14, 30), toFake: ['setTimeout', 'clearTimeout', 'setInterval', 'clearInterval', 'Date'] });

    await mountStrip();

    await wrapper!.setProps({ workflows: workflows({ openCount: 3 }) });
    await vi.advanceTimersByTimeAsync(1_000);

    expect(activityReads()).toBe(2);
  });
});
