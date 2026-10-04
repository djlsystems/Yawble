// @vitest-environment happy-dom
//
// THE STATISTICS TILE'S DRAWING: open spans drawn to the window's end and never grown by the clock, the notch that tells
// blocked and failed apart without hue, lanes tall enough to read, the hover line, and an SVG chart
// that follows the dark theme.
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { Dark } from 'quasar';

import TeamKpiStrip from '../TeamKpiStrip.vue';
import { useConsoleStore } from '../../stores/console';
import { asTeamId } from '../../api/types';
import type { ActivityMember, ActivitySpan, TeamActivity, TeamWorkflows } from '../../api/types';
import { resetBody } from '../../test/mountQuasar';

const teamId = asTeamId('alpha');

const at = (hours: number, minutes: number, seconds = 0) => new Date(2026, 9, 4, hours, minutes, seconds).getTime();
const iso = (ms: number) => new Date(ms).toISOString();

function container(id: string, name: string) {
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
  };
}

function member(name: string, spans: ActivitySpan[]): ActivityMember {
  return { member: name, kind: 'agent', isManager: name === 'Manager', current: true, spans };
}

const workflows = {
  available: true,
  openCount: 1,
  totalCount: 1,
  earliestStartedAt: iso(at(14, 2)),
  serverNow: iso(at(14, 30)),
  workflows: [],
  missing: null,
} as unknown as TeamWorkflows;

let answer: TeamActivity;
let fetchMock: ReturnType<typeof vi.fn>;
let wrapper: VueWrapper | undefined;

const activityReads = () =>
  fetchMock.mock.calls.filter(([url]) => String(url).endsWith(`/api/teams/${teamId}/activity`)).length;

beforeEach(() => {
  answer = {
    from: iso(at(14, 2)),
    to: iso(at(14, 20)),
    serverNow: iso(at(14, 30)),
    window: 'workflows',
    members: [
      member('Manager', [{ state: 'running', from: iso(at(14, 2)), to: null }]),
      member('Ines', [
        { state: 'running', from: iso(at(14, 2)), to: iso(at(14, 10)) },
        { state: 'blocked', from: iso(at(14, 10)), to: null, reason: 'needs the API key' },
      ]),
    ],
  };
  fetchMock = vi.fn(async (url: string) =>
    String(url).endsWith('/activity')
      ? new Response(JSON.stringify(answer), { status: 200, headers: { 'Content-Type': 'application/json' } })
      : new Response('{}', { status: 404 }));
  vi.stubGlobal('fetch', fetchMock);
});

afterEach(() => {
  wrapper?.unmount();
  wrapper = undefined;
  Dark.set(false);
  vi.useRealTimers();
  vi.unstubAllGlobals();
  resetBody();
});

async function mountStrip(props: Record<string, unknown> = {}, containers = [container('Manager', 'Manager'), container('Ines', 'Ines')]) {
  setActivePinia(createPinia());
  useConsoleStore().$patch({
    teams: [{ id: teamId, name: 'Alpha', memberAgents: [], repos: [] }],
    activeTeamId: teamId,
    overviewLanded: true,
    workflowSpendLimit: null,
  } as never);

  wrapper = mount(TeamKpiStrip, {
    props: { teamId, containers: containers as never, usage: null, timing: null, workflows, clockOffset: 0, ...props },
    attachTo: document.body,
  });

  await flushPromises();
  await flushPromises();
}

type Datum = [number, number, number, number];

interface Option {
  tooltip: { trigger: string; triggerOn: string; axisPointer: { type: string } };
  xAxis: { min: number; max: number; axisPointer: { snap: boolean } };
  series: { data: Datum[]; renderItem: (params: unknown, api: unknown) => { children: { type: string }[] } }[];
}

const chart = () => wrapper!.findComponent({ name: 'Echarts' });
const option = () => chart().props('option') as Option;

describe('open spans between reads', () => {
  // The workflows' latest activity was 14:20; the server answered at 14:30.
  const blockedEnd = () => option().series[0]!.data.find(([lane, from]) => lane === 1 && from === at(14, 10))![2];

  it('end at the window\'s end the Host gives, not at now', async () => {
    vi.useFakeTimers({ now: at(14, 30), toFake: ['setTimeout', 'clearTimeout', 'setInterval', 'clearInterval', 'Date'] });

    await mountStrip();

    expect(blockedEnd()).toBe(at(14, 20));
    expect(option().xAxis.min).toBe(at(14, 2));
    expect(option().xAxis.max).toBe(at(14, 20));
  });

  it('never grow with the board clock, and make no read', async () => {
    vi.useFakeTimers({ now: at(14, 30), toFake: ['setTimeout', 'clearTimeout', 'setInterval', 'clearInterval', 'Date'] });

    await mountStrip();
    await vi.advanceTimersByTimeAsync(60_000);

    expect(blockedEnd()).toBe(at(14, 20));
    expect(option().xAxis.max).toBe(at(14, 20));
    expect(activityReads()).toBe(1);
  });

  it('take nothing from the browser clock\'s offset either', async () => {
    vi.useFakeTimers({ now: at(14, 20), toFake: ['setTimeout', 'clearTimeout', 'setInterval', 'clearInterval', 'Date'] });

    await mountStrip({ clockOffset: 10 * 60_000 });
    await vi.advanceTimersByTimeAsync(45_000);

    expect(blockedEnd()).toBe(at(14, 20));
  });

  it('leave a closed span where it ended', async () => {
    vi.useFakeTimers({ now: at(14, 30), toFake: ['setTimeout', 'clearTimeout', 'setInterval', 'clearInterval', 'Date'] });

    await mountStrip();
    await vi.advanceTimersByTimeAsync(60_000);

    const closed = option().series[0]!.data.find(([lane, from]) => lane === 1 && from === at(14, 2))!;

    expect(closed[2]).toBe(at(14, 10));
  });
});

describe('each span as drawn', () => {
  const api = (datum: Datum) => ({
    value: (i: number) => datum[i],
    // One pixel a second along x, fourteen a lane down y.
    coord: ([x, y]: [number, number]) => [(x - at(14, 0)) / 1000, y * 14],
  });
  const params = { coordSys: { x: 0, width: 100_000 } };

  const draw = async (state: number) => {
    await mountStrip();
    return option().series[0]!.renderItem(params, api([0, at(14, 2), at(14, 10), state])).children.map((c) => c.type);
  };

  it('notches blocked and failed so neither depends on hue, and leaves running plain', async () => {
    // ActivityStates order: running, waiting, blocked, failed, idle.
    expect(await draw(0)).toEqual(['rect']);
    wrapper!.unmount();
    expect(await draw(1)).toEqual(['rect']);
    wrapper!.unmount();
    expect(await draw(2)).toEqual(['rect', 'polygon']);
    wrapper!.unmount();
    expect(await draw(3)).toEqual(['rect', 'polygon', 'polygon']);
    wrapper!.unmount();
    expect(await draw(4)).toEqual(['rect']);
  });
});

describe('lane height', () => {
  it('gives each lane at least 10 px, and the chart grows with the team', async () => {
    await mountStrip();

    const heights = wrapper!.findAll('.stats-lane-initials').map((lane) => parseFloat((lane.element as HTMLElement).style.height));
    const chartHeight = () => parseFloat((chart().element as HTMLElement).style.height);

    expect(heights).toHaveLength(2);
    for (const h of heights) expect(h).toBeGreaterThanOrEqual(10);
    expect(chartHeight()).toBe(heights.reduce((a, b) => a + b, 0));

    wrapper!.unmount();
    answer.members.push(member('Okon', []), member('Rhea', []));
    await mountStrip({}, [container('Manager', 'Manager'), container('Ines', 'Ines'), container('Okon', 'Okon'), container('Rhea', 'Rhea')]);

    expect(chartHeight()).toBeGreaterThanOrEqual(40);
    expect(chartHeight()).toBe(heights[0]! * 4);
  });

  it('never scrolls', () => {
    const source = readFileSync(join(import.meta.dirname, '..', 'TeamStatisticsTile.vue'), 'utf8');

    expect(source).not.toMatch(/overflow(-[xy])?\s*:\s*(auto|scroll)/);
  });
});

describe('hover and chart setup', () => {
  it('draws one vertical line across every lane on hover, shown by a tap as well as a move', async () => {
    await mountStrip();

    expect(option().tooltip.trigger).toBe('axis');
    expect(option().tooltip.axisPointer.type).toBe('line');
    expect(option().tooltip.triggerOn.split('|')).toContain('click');
  });

  it('puts the hover box beside the pointer, never over it, at the tile\'s left edge, middle and right edge', async () => {
    const screen = Object.getOwnPropertyDescriptor(window, 'innerWidth')
    Object.defineProperty(window, 'innerWidth', { configurable: true, value: 700 })

    try {
      await mountStrip();

      const tooltip = option().tooltip as unknown as {
        confine: boolean
        appendTo: string
        className: string
        position: (point: number[], params: unknown, dom: unknown, rect: unknown, size: { contentSize: number[]; viewSize: number[] }) => number[]
      };

      // Drawn outside the tile, so it may reach past its edges.
      expect(tooltip.confine).toBe(false);
      expect(tooltip.appendTo).toBe('body');
      expect(tooltip.className).toContain('stats-tooltip');

      const width = 600;
      const box = [180, 90];

      for (const x of [0, width / 2, width]) {
        const [left] = tooltip.position([x, 7], [], null, null, { contentSize: box, viewSize: [width, 28] });

        expect(x < left! || x > left! + box[0]!, `pointer at ${x}`).toBe(true);
      }
    } finally {
      if (screen) Object.defineProperty(window, 'innerWidth', screen);
      else delete (window as { innerWidth?: number }).innerWidth;
    }
  });

  it('renders SVG with the light theme, and the dark theme while dark mode is on', async () => {
    await mountStrip();

    expect((chart().props('initOptions') as { renderer: string }).renderer).toBe('svg');
    expect(chart().props('theme')).not.toBe('dark');

    wrapper!.unmount();
    Dark.set(true);
    await mountStrip();

    expect(chart().props('theme')).toBe('dark');
  });

  it('imports ECharts piece by piece, never the whole library', () => {
    const source = readFileSync(join(import.meta.dirname, '..', 'TeamStatisticsTile.vue'), 'utf8');

    expect(source).not.toMatch(/^import (?!type)[^;]*from 'echarts';/m);
    expect(source).toMatch(/from 'echarts\/core'/);
    expect(source).toMatch(/SVGRenderer/);
  });
});
