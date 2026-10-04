// @vitest-environment happy-dom
//
// THE STATISTICS TILE ON THE TEAM BOARD: where it sits in the strip, which lanes it draws, what its
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

const teamId = asTeamId('alpha');

/** A local wall-clock instant, so the HH:MM the caption shows is the one written here. */
const at = (hours: number, minutes: number) => new Date(2026, 9, 4, hours, minutes).getTime();
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
    window: 'open',
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

describe('the Statistics tile', () => {
  it('sits between the Workflow tile and the Tokens tile', async () => {
    await mountStrip();

    const tiles = wrapper!.findAll('.team-kpi-strip > .team-kpi').map((t) => t.find('.team-kpi-label').text());

    expect(tiles.map((label) => label.split(/\s/)[0])).toEqual(['Workflow', 'Statistics', 'Tokens']);
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

  it('captions an open window with its start and the open workflows', async () => {
    await mountStrip();

    expect(tile().find('.stats-caption').text()).toBe('since 14:02 · 2 open workflows');
    expect(tile().find('.stats-chart').exists()).toBe(true);
  });

  it('captions the fallback with when the latest workflow closed', async () => {
    answer = activity({ window: 'latest', from: iso(at(9, 0)) });

    await mountStrip({
      workflows: workflows({
        openCount: 0,
        earliestStartedAt: null,
        workflows: [closedWorkflow(iso(at(9, 0)), iso(at(9, 15)))],
      }),
    });

    expect(tile().find('.stats-caption').text()).toBe('latest workflow, closed 09:15');
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

    expect(tile().attributes('aria-label')).toMatch(/^Statistics: 3 members — 3 running since 14:02$/);
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
});

describe('the Statistics tile keeps itself current', () => {
  it('refetches when a member changes state, and never on a timer', async () => {
    vi.useFakeTimers({ now: at(14, 30), toFake: ['setTimeout', 'clearTimeout', 'setInterval', 'clearInterval', 'Date'] });

    await mountStrip();
    expect(activityReads()).toBe(1);

    // A board left open for ten minutes with nothing changing reads nothing more.
    await vi.advanceTimersByTimeAsync(10 * 60_000);
    expect(activityReads()).toBe(1);

    await wrapper!.setProps({
      containers: [board[0], container('DeveloperInes', 'Developer Ines', { state: 'Running', currentCorrelation: 7 }), board[2]] as never,
    });
    await vi.advanceTimersByTimeAsync(1_000);
    expect(activityReads()).toBe(2);

    await vi.advanceTimersByTimeAsync(10 * 60_000);
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
