// @vitest-environment happy-dom
//
// THE STATISTICS DIALOG, opened from the Statistics tile on the team board: which period it opens
// on, what each period asks `/activity` for, how its columns stack, what its tooltip says, and how
// it reads an empty period and a member since removed.
//
// The bucketing and the words are pinned without a DOM in `lib/__tests__/teamActivity.spec.ts`;
// this file pins that the dialog asks for the right period and draws what those functions decide.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { init } from 'echarts/core';

import TeamKpiStrip from '../TeamKpiStrip.vue';
import { useConsoleStore } from '../../stores/console';
import { asTeamId } from '../../api/types';
import type { ActivityMember, TeamActivity, TeamWorkflows } from '../../api/types';
import { resetBody } from '../../test/mountQuasar';
import { ActivityStates } from '../../lib/teamActivity';

const teamId = asTeamId('alpha');

/** The board's clock for every case here: Sun 4 Oct 2026, 14:30 UTC. */
const now = Date.parse('2026-10-04T14:30:00Z');
const utc = (time: string) => Date.parse(`2026-10-04T${time}Z`);
// A column's label is in the browser's own zone, so the expected label is too: the suite runs in
// UTC in a container and in the person's zone on their machine.
const hm = (at: number) => new Intl.DateTimeFormat('en-US', { hour: '2-digit', minute: '2-digit', hourCycle: 'h23' }).format(at);
const span = (from: string, to: string) => `${hm(utc(from))}–${hm(utc(to))}`;
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
 * In the 14:05 minute the Manager runs all of it and Ines runs half and is blocked half: 1.5
 * member-minutes running under 0.5 blocked. In the 14:06 minute only Ines, waiting 20 s.
 */
function activity(over: Partial<TeamActivity> = {}): TeamActivity {
  return {
    from: iso(utc('14:02:00')),
    to: iso(now),
    serverNow: iso(now),
    window: 'open',
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

/** The period button a person clicks, by its words. */
async function choose(label: string) {
  const button = [...dialog()!.querySelectorAll<HTMLButtonElement>('.stats-period button')]
    .find((b) => b.textContent?.trim() === label);

  expect(button, `a period button reading "${label}"`).toBeTruthy();
  button!.click();
  await settle();
}

const pressed = () => dialog()!.querySelector('.stats-period button[aria-pressed="true"]')?.textContent?.trim();

interface ChartOption {
  legend: { data: string[]; inactiveColor?: string; inactiveBorderColor?: string };
  yAxis: { name?: string; axisLabel?: { formatter: (value: number) => string } }[];
  series: { name: string; data: number[][] }[];
}

/** The reference lanes' labels, read at each lane's middle. */
function laneNames(option: ChartOption, count: number): string[] {
  return Array.from({ length: count }, (_, lane) => option.yAxis[0]!.axisLabel!.formatter(lane + 0.5));
}

/** The option the dialog's chart was given: the dialog's chart is the second on the page. */
function dialogOption(): ChartOption {
  const charts = wrapper!.findAllComponents({ name: 'Echarts' });

  return charts[charts.length - 1]!.props('option') as ChartOption;
}

/**
 * THE TOOLTIP ECHARTS SHOWS for a pointer moved over the columns to each instant in turn: the
 * dialog's option on a real chart of a fixed size, with the pointer moved the way a mouse moves it,
 * read after each move has had time to settle - ECharts throttles its hover line, and whatever that
 * hands the tooltip arrives late. Empty when no tooltip shows. What ECharts hands the formatter is
 * ECharts' own choice, so this is read from the tooltip it draws.
 */
async function hoverText(option: ChartOption, ...at: number[]): Promise<string> {
  return (await hoverMarkup(option, ...at)).textContent ?? '';
}

/** The same tooltip as markup, out of the chart it was drawn in. */
async function hoverMarkup(option: ChartOption, ...at: number[]): Promise<HTMLElement> {
  const host = document.createElement('div');
  document.body.appendChild(host);

  const chart = init(host, null, { renderer: 'svg', width: 1000, height: 600 });

  try {
    chart.setOption(option as never);

    for (const instant of at) {
      const [x, y] = chart.convertToPixel({ xAxisIndex: 1, yAxisIndex: 1 }, [instant, 0.1]) as number[];
      chart.getZr().handler.dispatch('mousemove', { zrX: x, zrY: y, offsetX: x, offsetY: y });
      await new Promise((resolve) => setTimeout(resolve, 150));
    }

    const shown = host.querySelector<HTMLElement>('.stats-tooltip');
    const tip = document.createElement('div');

    if (shown && shown.style.display !== 'none' && shown.style.visibility !== 'hidden') tip.innerHTML = shown.innerHTML;

    return tip;
  } finally {
    chart.dispose();
    host.remove();
  }
}

/**
 * THE COLUMNS ECHARTS LIGHTS UP while the pointer moves over the columns to each instant in turn,
 * on the same real chart: every highlight ECharts asks for along the way, as the start of the
 * column each highlighted state belongs to. What lights a column up is ECharts' own choice, so this
 * is read from the highlights it raises, not from the option.
 */
async function highlightedColumns(option: ChartOption, ...at: number[]): Promise<number[]> {
  const host = document.createElement('div');
  document.body.appendChild(host);

  const chart = init(host, null, { renderer: 'svg', width: 1000, height: 600 });
  const lit: number[] = [];

  try {
    chart.setOption(option as never);
    chart.on('highlight', (event: unknown) => {
      const raised = event as { batch?: { seriesIndex?: number; dataIndex?: number | number[] }[] };

      for (const item of raised.batch ?? []) {
        const series = option.series[item.seriesIndex ?? -1];
        if (!series || !(ActivityStates as readonly string[]).includes(series.name)) continue;

        for (const index of [item.dataIndex ?? []].flat()) {
          const from = series.data[index]?.[0];
          if (from !== undefined) lit.push(from);
        }
      }
    });

    for (const instant of at) {
      const [x, y] = chart.convertToPixel({ xAxisIndex: 1, yAxisIndex: 1 }, [instant, 0.1]) as number[];
      chart.getZr().handler.dispatch('mousemove', { zrX: x, zrY: y, offsetX: x, offsetY: y });
      await new Promise((resolve) => setTimeout(resolve, 150));
    }

    return lit;
  } finally {
    chart.dispose();
    host.remove();
  }
}

describe('opening the Statistics dialog', () => {
  it('opens from a click on the tile, on Open workflows by minute, reading the team\'s own window', async () => {
    await mountStrip();
    const before = activityUrls().length;

    expect(dialog()).toBeNull();

    await openFromTile();

    expect(dialog()).not.toBeNull();
    expect(pressed()).toBe('Open workflows');
    expect(activityUrls().slice(before)).toEqual([`/api/teams/${teamId}/activity`]);
    expect(dialogOption().yAxis[1]!.name).toBe('member-minutes');
  });

  it('opens from a click on the tile\'s label', async () => {
    await mountStrip();

    await wrapper!.find('.stats-label').trigger('click');
    await settle();

    expect(dialog()).not.toBeNull();
    expect(pressed()).toBe('Open workflows');
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

describe('the period picker', () => {
  const periodUrl = (from: string, to: number) =>
    `/api/teams/${teamId}/activity?from=${encodeURIComponent(from)}&to=${encodeURIComponent(iso(to))}`;

  it('sends each period\'s from and to, and reads its unit', async () => {
    await mountStrip();
    await openFromTile();

    const cases: [string, string, string][] = [
      ['Last hour', '2026-10-04T13:30:00.000Z', 'member-minutes'],
      ['Last day', '2026-10-03T14:30:00.000Z', 'member-minutes'],
      ['Last month', '2026-09-04T14:30:00.000Z', 'member-hours'],
      ['Last year', '2025-10-04T14:30:00.000Z', 'member-hours'],
    ];

    for (const [label, from, unit] of cases) {
      const before = activityUrls().length;

      await choose(label);

      expect(activityUrls().slice(before), label).toEqual([periodUrl(from, now)]);
      expect(pressed()).toBe(label);
      expect(dialogOption().yAxis[1]!.name, label).toBe(unit);
    }
  });

  it('refetches on a period change and on nothing else the board does', async () => {
    await mountStrip();
    await openFromTile();
    await choose('Last hour');

    const dialogReads = () => activityUrls().filter((url) => url.includes('from=')).length;

    expect(dialogReads()).toBe(1);

    vi.useRealTimers();
    await wrapper!.setProps({
      containers: [board[0], { ...board[1], state: 'Running', currentCorrelation: 7 }] as never,
      workflows: { ...workflows(), openCount: 2 },
    });
    await new Promise((resolve) => setTimeout(resolve, 1_100));
    await settle();

    expect(dialogReads()).toBe(1);
  });

  it('opens on Open workflows again after another period was chosen', async () => {
    await mountStrip();
    await openFromTile();
    await choose('Last day');

    await wrapper!.find('.stats-label').trigger('click');
    await settle();
    expect(pressed()).toBe('Last day');

    (dialog()!.querySelector('.stats-done') as HTMLButtonElement).click();
    await settle();
    await wrapper!.find('.stats-label').trigger('click');
    await settle();

    expect(pressed()).toBe('Open workflows');
  });
});

describe('the columns', () => {
  it('stacks each column in state order, in member-minutes', async () => {
    await mountStrip();
    await openFromTile();

    const option = dialogOption();
    const states = ['running', 'waiting', 'blocked', 'failed', 'idle'];

    expect(option.legend.data).toEqual(states);

    const series = Object.fromEntries(
      option.series.filter((s) => states.includes(s.name)).map((s) => [s.name, s.data]));

    expect(Object.keys(series)).toEqual(states);

    // [from, to, base, top]: running sits on the axis, blocked on running; the 14:06 column is
    // waiting only, and a state with no time in a column draws nothing there.
    expect(series.running).toEqual([[utc('14:05:00'), utc('14:06:00'), 0, 1.5]]);
    expect(series.blocked).toEqual([[utc('14:05:00'), utc('14:06:00'), 1.5, 2]]);
    expect(series.waiting).toEqual([[utc('14:06:00'), utc('14:07:00'), 0, 1 / 3]]);
    expect(series.failed).toEqual([]);
    expect(series.idle).toEqual([]);
  });

  it('draws a state the legend has toggled off in the theme\'s faint ink, not the chart\'s own grey', async () => {
    const theme = document.createElement('style');
    theme.textContent = '.stats-dialog-card { --os-ink-faint: rgb(1, 2, 3); }';
    document.head.appendChild(theme);

    try {
      await mountStrip();
      await openFromTile();

      expect(dialogOption().legend.inactiveColor).toBe('rgb(1, 2, 3)');
      expect(dialogOption().legend.inactiveBorderColor).toBe('rgb(1, 2, 3)');
    } finally {
      theme.remove();
    }
  });

  it('draws a reference lane per member above the columns, on the same time axis', async () => {
    await mountStrip();
    await openFromTile();

    expect(laneNames(dialogOption(), 3)).toEqual(['Manager', 'Ines Lopez', '']);
  });

  it('lists each member with initials in the tooltip, escaped', async () => {
    answer = activity({
      members: [
        ...activity().members,
        member('Bold', [{ state: 'failed', from: iso(utc('14:05:10')), to: iso(utc('14:05:40')) }]),
      ],
    });

    await mountStrip();
    await wrapper!.setProps({ containers: [...board, container('Bold', '<b>Bold</b>')] as never });
    await openFromTile();

    const text = await hoverText(dialogOption(), utc('14:05:30'));

    expect((await hoverMarkup(dialogOption(), utc('14:05:30'))).querySelector('b')).toBeNull();
    expect(text).toContain('running 1 min 30 s');
    expect(text).toContain('M Manager — running 1 min');
    expect(text).toContain('IL Ines Lopez — running 30 s, blocked 30 s');
    expect(text).toContain('< <b>Bold</b> — failed 30 s');
  });

  it('names the column under the pointer across its whole width, not the nearest column start', async () => {
    await mountStrip();
    await openFromTile();

    const option = dialogOption();

    // The 14:05 column, early and late in it; then 14:06, late in it, with no column after it.
    expect(await hoverText(option, utc('14:05:06'))).toContain(span('14:05:00', '14:06:00'));
    expect(await hoverText(option, utc('14:05:54'))).toContain(span('14:05:00', '14:06:00'));
    expect(await hoverText(option, utc('14:05:54'))).toContain('running 1 min 30 s');
    expect(await hoverText(option, utc('14:06:50'))).toContain(span('14:06:00', '14:07:00'));
    expect(await hoverText(option, utc('14:06:50'))).toContain('waiting 20 s');
    // Into the 14:05 column's right half from its left half, and on into the gap after 14:06.
    expect(await hoverText(option, utc('14:05:10'), utc('14:05:50'))).toContain(span('14:05:00', '14:06:00'));
    expect(await hoverText(option, utc('14:06:30'), utc('14:07:30'))).toBe('');
  });

  it('never lights up a column the pointer is not over, so the highlight agrees with the tooltip', async () => {
    await mountStrip();
    await openFromTile();

    const option = dialogOption();
    const column = (at: number) => [utc('14:05:00'), utc('14:06:00')].filter((from) => from <= at).pop();

    // Early and late in the 14:05 column, late in 14:06; over the gap after it, none.
    for (const at of [utc('14:05:06'), utc('14:05:54'), utc('14:06:50')]) {
      for (const from of await highlightedColumns(option, at)) expect(iso(from)).toBe(iso(column(at)!));
    }

    expect(await highlightedColumns(option, utc('14:07:30'))).toEqual([]);
    for (const from of await highlightedColumns(option, utc('14:05:10'), utc('14:05:50'))) {
      expect(iso(from)).toBe(iso(utc('14:05:00')));
    }
  });

  it('reads No runs in this period when nothing was recorded', async () => {
    answer = activity({ members: [member('Manager', []), member('DeveloperInes', [])] });

    await mountStrip();
    await openFromTile();

    expect(dialog()!.textContent).toContain('No runs in this period');
    expect(dialog()!.querySelector('.stats-dialog-chart')).toBeNull();
  });

  it('counts a member since removed under its name, marked removed, with a lane in the dialog only', async () => {
    answer = activity({
      members: [
        ...activity().members,
        member('Gone Member', [{ state: 'running', from: iso(utc('14:05:00')), to: iso(utc('14:05:30')) }], { current: false }),
      ],
    });

    await mountStrip();

    expect(wrapper!.find('.team-kpi--statistics').text()).not.toContain('Gone');

    await openFromTile();

    const option = dialogOption();

    expect(laneNames(option, 3)).toEqual(['Manager', 'Ines Lopez', 'Gone Member (removed)']);
    expect(option.series.find((s) => s.name === 'running')!.data).toEqual([[utc('14:05:00'), utc('14:06:00'), 0, 2]]);

    expect(await hoverText(option, utc('14:05:30'))).toContain('GM Gone Member (removed) — running 30 s');
  });
});
