// @vitest-environment happy-dom
//
// THE ACTIVITY DIALOG, opened from the Activity tile on the team board: that it shows the tile's
// own window with no period picker, one lane per member and nothing under them, and that its hover
// behaves as the tile's - the exact instant under the pointer and what each member
// was doing then - and how it reads an empty window and a member since removed.
//
// The lanes and the words are pinned without a DOM in `lib/__tests__/teamActivity.spec.ts`;
// this file pins that the dialog reads the tile's window and draws what those functions decide.
// Run it in more than one zone: every time here is expected in the browser's own locale and zone.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { getInstanceByDom } from 'echarts/core';

import TeamKpiStrip from '../TeamKpiStrip.vue';
import { useConsoleStore } from '../../stores/console';
import { asTeamId } from '../../api/types';
import type { ActivityMember, TeamActivity, TeamWorkflows } from '../../api/types';
import { resetBody } from '../../test/mountQuasar';

const teamId = asTeamId('alpha');

/** The board's clock for every case here: Sun 4 Oct 2026, 14:30 UTC. */
const now = Date.parse('2026-10-04T14:30:00Z');
const utc = (time: string) => Date.parse(`2026-10-04T${time}Z`);
// Times read in the browser's own locale and zone, so the expected text is too: the suite runs in
// UTC in a container and in the person's zone on their machine.
const time = (at: number) => new Date(at).toLocaleTimeString();
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

const board = [container('Manager', 'Manager'), container('DeveloperInes', 'Ines Lopez')];

function member(name: string, spans: ActivityMember['spans'], over: Partial<ActivityMember> = {}): ActivityMember {
  return { member: name, kind: 'agent', isManager: name === 'Manager', current: true, spans, ...over };
}

/**
 * A window from 14:02 to 14:07. The Manager runs 14:05-14:06; Ines runs 14:05-14:05:30, is blocked
 * to 14:06, then waits 20 s.
 */
function activity(over: Partial<TeamActivity> = {}): TeamActivity {
  return {
    from: iso(utc('14:02:00')),
    to: iso(utc('14:07:00')),
    serverNow: iso(now),
    window: 'workflows',
    members: [
      member('Manager', [{ state: 'running', from: iso(utc('14:05:00')), to: iso(utc('14:06:00')) }]),
      member('DeveloperInes', [
        { state: 'running', from: iso(utc('14:05:00')), to: iso(utc('14:05:30')) },
        { state: 'blocked', from: iso(utc('14:05:30')), to: iso(utc('14:06:00')), reason: 'needs a key' },
        { state: 'waiting', from: iso(utc('14:06:00')), to: iso(utc('14:06:20')) },
      ]),
    ],
    ...over,
  };
}

function workflows(): TeamWorkflows {
  return {
    available: true,
    openCount: 1,
    totalCount: 1,
    earliestStartedAt: iso(utc('14:02:00')),
    serverNow: iso(now),
    workflows: [],
    missing: null,
  } as TeamWorkflows;
}

let answer: TeamActivity;
let fetchMock: ReturnType<typeof vi.fn>;
let wrapper: VueWrapper | undefined;

/** Every `/activity` URL read, in order, tile and dialog alike. */
const activityUrls = () =>
  fetchMock.mock.calls.map(([url]) => String(url)).filter((url) => url.includes(`/api/teams/${teamId}/activity`));

beforeEach(() => {
  vi.useFakeTimers({ now, toFake: ['Date'] });
  answer = activity();
  fetchMock = vi.fn(async (url: string) =>
    String(url).includes('/activity')
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

async function settle() {
  await flushPromises();
  await flushPromises();
}

async function mountStrip() {
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
    },
    attachTo: document.body,
  });

  await settle();

  return wrapper;
}

const dialog = () => document.querySelector<HTMLElement>('.stats-dialog-card');

async function openFromTile() {
  await wrapper!.find('.team-kpi--statistics').trigger('click');
  await settle();
}

/** The dialog's subtitle: the window. */
const subtitle = () => dialog()!.querySelector('.stats-dialog-subtitle')?.textContent?.trim();

interface ChartOption {
  legend: { data: string[]; selectedMode?: boolean; formatter: (name: string) => string };
  grid: unknown;
  xAxis: { min: number; max: number; axisLabel: { formatter?: (value: number) => string } };
  yAxis: { name?: string; axisLabel: { formatter: (value: number) => string } };
  tooltip: {
    trigger: string
    confine: boolean
    appendTo: string
    position: (point: number[], params: unknown, dom: unknown, rect: unknown, size: { contentSize: number[]; viewSize: number[] }) => number[]
    formatter: (params: unknown) => string
  };
  series: { name: string; data: unknown[]; itemStyle?: { color?: string; decal?: unknown } }[];
}

/** The dialog's chart: the second on the page, after the tile's. */
function dialogChart() {
  const charts = wrapper!.findAllComponents({ name: 'Echarts' });

  return charts[charts.length - 1]!;
}

const dialogOption = () => dialogChart().props('option') as ChartOption;

/** The lanes' labels, read at each lane's middle. */
function laneNames(option: ChartOption, count: number): string[] {
  return Array.from({ length: count }, (_, lane) => option.yAxis.axisLabel.formatter(lane + 0.5));
}

/** The dialog's lanes start this far in, and end this far from the right: the plot is between. */
const GridLeft = 128;
const GridRight = 16;

/** The window every case here draws: 14:02 to 14:07. */
const windowFrom = utc('14:02:00');
const windowTo = utc('14:07:00');

/** The ECharts instance that drew the dialog's chart. */
function drawnChart() {
  const el = dialog()!.querySelector<HTMLElement>('.stats-dialog-chart')!;
  const chart = getInstanceByDom(el) ?? getInstanceByDom(el.firstElementChild as HTMLElement);

  if (!chart) throw new Error('the dialog drew no chart');

  return chart;
}

/** Gives the chart element a width of its own: `plot` px between the lanes' margins. */
function sizeElement(plot: number) {
  const el = dialog()!.querySelector('.stats-dialog-chart') as HTMLElement;
  const width = GridLeft + plot + GridRight;
  el.getBoundingClientRect = () => ({ x: 0, y: 0, left: 0, top: 0, right: width, bottom: 200, width, height: 200, toJSON: () => ({}) });
}

/**
 * THE CHART AS ECHARTS LAID IT OUT, 1000 px of plot wide, answering a pixel as a time - while the
 * element is another width, as it is while the dialog's zoom-in runs. The box must name the time
 * the chart drew the line at, not one measured from the element.
 */
function layOutChart() {
  sizeElement(1300);
  vi.spyOn(drawnChart(), 'convertFromPixel')
    .mockImplementation(((_finder: unknown, x: number) => windowFrom + ((x - GridLeft) / 1000) * (windowTo - windowFrom)) as never);
}

/** The hover box's text for a pointer at `x` px into the plot, with the axis naming `axisValue`. */
async function hoverAt(x: number, axisValue: number): Promise<HTMLElement> {
  dialogChart().vm.$emit('zr:mousemove', { offsetX: GridLeft + x });
  await flushPromises();

  const box = document.createElement('div');
  box.innerHTML = dialogOption().tooltip.formatter([{ axisValue }]);

  return box;
}

describe('opening the Activity dialog', () => {
  it('opens from a click on the tile, reading the tile\'s own window', async () => {
    await mountStrip();
    const before = activityUrls().length;

    expect(dialog()).toBeNull();

    await openFromTile();

    expect(dialog()).not.toBeNull();
    expect(activityUrls().slice(before)).toEqual([`/api/teams/${teamId}/activity`]);
  });

  it('opens from a click on the tile\'s label', async () => {
    await mountStrip();

    await wrapper!.find('.stats-label').trigger('click');
    await settle();

    expect(dialog()).not.toBeNull();
  });

  it('stays shut on a touch tap on the lanes, which shows the tooltip instead', async () => {
    await mountStrip();

    const tile = wrapper!.find('.team-kpi--statistics');

    tile.element.dispatchEvent(Object.assign(new Event('pointerdown', { bubbles: true }), { pointerType: 'touch' }));
    await tile.trigger('click');
    await settle();

    expect(dialog()).toBeNull();
  });
});

describe('the window', () => {
  it('has no period picker and no Fit: it shows the tile\'s window', async () => {
    await mountStrip();
    await openFromTile();

    expect(dialog()!.querySelector('.stats-period')).toBeNull();
    expect(dialog()!.querySelector('.stats-fit')).toBeNull();

    for (const words of ['Open workflows', 'Last hour', 'Last day', 'Last month', 'Last year', 'Fit']) {
      expect([...dialog()!.querySelectorAll('button')].map((b) => b.textContent?.trim())).not.toContain(words);
    }
  });

  it('draws the window the Host gives, from its start to its end, never to the clock', async () => {
    await mountStrip();
    await openFromTile();

    expect(dialogOption().xAxis.min).toBe(utc('14:02:00'));
    expect(dialogOption().xAxis.max).toBe(utc('14:07:00'));

    vi.setSystemTime(now + 3_600_000);
    await settle();

    expect(dialogOption().xAxis.max).toBe(utc('14:07:00'));
  });

  it('names the stretch in its subtitle and no column size', async () => {
    await mountStrip();
    await openFromTile();

    expect(subtitle()).toBe(`${time(utc('14:02:00'))} – ${time(utc('14:07:00'))}`);
  });

  it('labels the time axis as the browser\'s locale reads a time', async () => {
    await mountStrip();
    await openFromTile();

    expect(dialogOption().xAxis.axisLabel.formatter!(utc('14:05:30'))).toBe(time(utc('14:05:30')));
  });

  it('reads on opening and on nothing else the board does', async () => {
    await mountStrip();
    await openFromTile();

    const reads = activityUrls().length;

    vi.useRealTimers();
    await wrapper!.setProps({
      containers: [board[0], { ...board[1], state: 'Running', currentCorrelation: 7 }] as never,
      workflows: { ...workflows(), openCount: 2 },
    });
    await new Promise((resolve) => setTimeout(resolve, 1_100));
    await settle();

    // The tile may read again on the change; the dialog does not, and keeps what it drew.
    expect(activityUrls().length - reads).toBeLessThanOrEqual(1);
    expect(dialogOption().xAxis.max).toBe(utc('14:07:00'));
  });

  it('reads No runs in this window when nothing was recorded', async () => {
    answer = activity({ members: [member('Manager', []), member('DeveloperInes', [])] });

    await mountStrip();
    await openFromTile();

    expect(dialog()!.textContent).toContain('No runs in this window');
    expect(dialog()!.querySelector('.stats-dialog-chart')).toBeNull();
  });
});

describe('the lanes', () => {
  it('draws one lane per member and no member-time columns under them', async () => {
    await mountStrip();
    await openFromTile();

    const option = dialogOption();

    expect(laneNames(option, 3)).toEqual(['Manager', 'Ines Lopez', '']);
    // One grid, one y axis naming members: no second chart of member-minutes.
    expect(Array.isArray(option.grid)).toBe(false);
    expect(Array.isArray(option.yAxis)).toBe(false);
    expect(option.yAxis.name).toBeUndefined();
    // Only the lanes carry spans; the per-state series are empty and only key the legend.
    expect(option.series.filter((s) => s.data.length > 0 && s.name !== 'period').map((s) => s.name)).toEqual(['lanes']);
  });

  it('keys each state in the legend, held as waiting for a slot and striped, and filters nothing', async () => {
    await mountStrip();
    await openFromTile();

    const option = dialogOption();

    expect(option.legend.data).toEqual(['running', 'waiting', 'held', 'blocked', 'failed', 'idle']);
    expect(option.legend.selectedMode).toBe(false);
    expect(option.legend.formatter('held')).toBe('waiting for a slot');
    expect(option.series.find((s) => s.name === 'held')!.itemStyle?.decal).toBeTruthy();
    expect(option.series.find((s) => s.name === 'blocked')!.itemStyle?.decal).toBeUndefined();
  });

  it('gives a member since removed its own lane, marked removed, in the dialog only', async () => {
    answer = activity({
      members: [
        ...activity().members,
        member('Gone Member', [{ state: 'running', from: iso(utc('14:05:00')), to: iso(utc('14:05:30')) }], { current: false }),
      ],
    });

    await mountStrip();

    expect(wrapper!.find('.team-kpi--statistics').text()).not.toContain('Gone');

    await openFromTile();

    expect(laneNames(dialogOption(), 3)).toEqual(['Manager', 'Ines Lopez', 'Gone Member (removed)']);
  });
});

describe('the hover, as on the tile', () => {
  it('draws one line across the lanes and names the instant, as the tile does', async () => {
    await mountStrip();
    await openFromTile();

    const tooltip = dialogOption().tooltip;

    expect(tooltip.trigger).toBe('axis');

    // Halfway across 14:02-14:07 is 14:04:30; ECharts hands the formatter the nearest span edge.
    layOutChart();
    const box = await hoverAt(500, utc('14:05:00'));

    expect(box.querySelector('.stats-tip-time')!.textContent).toBe(time(utc('14:04:30')));
  });

  it('measures from the element\'s own width only when the chart cannot say', async () => {
    await mountStrip();
    await openFromTile();

    sizeElement(1000);
    vi.spyOn(drawnChart(), 'convertFromPixel').mockReturnValue(Number.NaN as never);

    expect((await hoverAt(500, utc('14:05:00'))).querySelector('.stats-tip-time')!.textContent).toBe(time(utc('14:04:30')));
  });

  it('names what each member was doing at that instant, escaped', async () => {
    await mountStrip();
    await wrapper!.setProps({ containers: [board[0], container('DeveloperInes', '<b>Ines</b>')] as never });
    await openFromTile();
    layOutChart();

    // 14:05:45: the Manager running, Ines blocked.
    const box = await hoverAt(750, utc('14:05:30'));

    expect(box.querySelector('.stats-tip-time')!.textContent).toBe(time(utc('14:05:45')));
    expect(box.querySelector('b')).toBeNull();
    expect(box.textContent).toContain('Manager — running 1 min');
    expect(box.textContent).toContain('<b>Ines</b> — blocked under a minute (needs a key)');
  });

  it('has no zoom: no slider, no wheel zoom and no zoom buttons, so the whole window always shows', async () => {
    await mountStrip();
    await openFromTile();

    const option = dialogOption() as ChartOption & { dataZoom?: unknown; toolbox?: unknown };

    expect(option.dataZoom).toBeUndefined();
    expect(option.toolbox).toBeUndefined();
  });

  it('lightens no span under the pointer: nothing reads as a state it is not in', async () => {
    await mountStrip();
    await openFromTile();

    const option = dialogOption() as ChartOption & { xAxis: { axisPointer: { triggerEmphasis?: boolean } } };
    const lanes = option.series.find((s) => s.name === 'lanes') as { silent?: boolean; emphasis?: { disabled?: boolean } };

    // Seen in a browser: emphasis off alone still lightened the span under the pointer.
    expect(lanes.silent).toBe(true);
    expect(lanes.emphasis?.disabled).toBe(true);
    expect(option.xAxis.axisPointer.triggerEmphasis).toBe(false);
  });

  it('puts the hover box beside the pointer, never over it, at the left edge, middle and right edge', async () => {
    const screen = Object.getOwnPropertyDescriptor(window, 'innerWidth');
    Object.defineProperty(window, 'innerWidth', { configurable: true, value: 1000 });

    try {
      await mountStrip();
      await openFromTile();

      const tooltip = dialogOption().tooltip;

      expect(tooltip.confine).toBe(false);
      expect(tooltip.appendTo).toBe('body');

      for (const x of [0, 500, 1000]) {
        const [left] = tooltip.position([x, 100], [], null, null, { contentSize: [220, 140], viewSize: [1000, 600] });

        expect(x < left! || x > left! + 220, `pointer at ${x}`).toBe(true);
      }
    } finally {
      if (screen) Object.defineProperty(window, 'innerWidth', screen);
      else delete (window as { innerWidth?: number }).innerWidth;
    }
  });
});
