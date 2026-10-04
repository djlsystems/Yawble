// @vitest-environment happy-dom
//
// THE TOKENS DIALOG'S CHART, opened from the Tokens tile on the team board: which period it opens
// on, what each period asks `/tokens/runs` for, how the metric picker changes the figures, one
// series per member, a bucket of runs with no figure, the hover, the source line - and that the
// totals and tables under it are as they were.
//
// The bucketing and the words are pinned without a DOM in `lib/__tests__/teamActivity.spec.ts`;
// this file pins that the dialog asks for the right period and draws what those functions decide.
// Run it in more than one zone: every label here is expected in the browser's own.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { init } from 'echarts/core';

import TeamKpiStrip from '../TeamKpiStrip.vue';
import { useConsoleStore } from '../../stores/console';
import { asTeamId } from '../../api/types';
import type { TeamTokenRun, TeamTokenRuns, TeamTokenTotals } from '../../api/types';
import { resetBody } from '../../test/mountQuasar';

const teamId = asTeamId('alpha');

/** The board's clock for every case here: Sun 4 Oct 2026, 14:30 UTC. */
const now = Date.parse('2026-10-04T14:30:00Z');
const utc = (time: string) => Date.parse(`2026-10-04T${time}Z`);
const iso = (ms: number) => new Date(ms).toISOString();
// A bucket's label is in the browser's own zone, so the expected label is too.
const hm = (at: number) => new Intl.DateTimeFormat('en-US', { hour: '2-digit', minute: '2-digit', hourCycle: 'h23' }).format(at);
const minuteOf = (time: string) => {
  const from = Math.floor(utc(time) / 60_000) * 60_000;
  return `${hm(from)}–${hm(from + 60_000)}`;
};

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

/** Board order: the Manager, then Ines, then Rhea. */
const board = [container('Manager', 'Manager'), container('DeveloperInes', 'Ines Lopez'), container('Rhea', '<b>Rhea</b>')];

/** A run with no figures: as the read answers an unmeasured run, before `over` adds any. */
function bare(member: string, endedAt: string, over: Partial<TeamTokenRun> = {}): TeamTokenRun {
  return { member, current: true, endedAt: iso(utc(endedAt)), measured: false, ...over };
}

/** A measured run with an in/out split. */
function run(member: string, endedAt: string, over: Partial<TeamTokenRun> = {}): TeamTokenRun {
  return bare(member, endedAt, {
    measured: true, billable: 100, tokensIn: 10, tokensCachedIn: 200, tokensCacheCreation: 4, tokensOut: 3, ...over,
  });
}

/**
 * The team was created at 14:00, half an hour of history: All workflows buckets it by the minute.
 * In the 14:05 minute Rhea and Ines spend (Rhea's run listed first, but Ines is earlier on the
 * board); in 14:07 the Manager spends and Ines's run is a combined total; in 14:09 only runs that
 * reported nothing.
 */
function runs(over: Partial<TeamTokenRuns> = {}): TeamTokenRuns {
  return {
    from: iso(utc('14:00:00')),
    to: iso(now),
    serverNow: iso(now),
    window: 'all',
    runs: [
      run('Rhea', '14:05:10', { billable: 250, tokensIn: 25 }),
      run('DeveloperInes', '14:05:40', { billable: 100, tokensIn: 10 }),
      run('Manager', '14:07:00', { billable: 40, tokensIn: 4 }),
      bare('DeveloperInes', '14:07:30', { measured: true, billable: 900, combined: 900 }),
      bare('DeveloperInes', '14:09:10'),
      bare('Rhea', '14:09:20'),
    ],
    ...over,
  };
}

/** The message log's total: what the totals and tables under the chart show. */
function totals(over: Partial<TeamTokenTotals> = {}): TeamTokenTotals {
  return {
    available: true,
    tokensIn: 39,
    tokensOut: 9,
    tokensCachedIn: 600,
    tokensCacheCreation: 12,
    tokensBillable: 1290,
    partial: true,
    runsWithUsage: 4,
    runsWithoutUsage: 2,
    missing: null,
    members: [
      {
        member: 'DeveloperInes', brand: 'claude-headless', tokensIn: 10, tokensOut: 3, runs: 3,
        tokensCachedIn: 200, tokensCacheCreation: 4, tokensBillable: 1000,
      },
    ],
    ...over,
  } as TeamTokenTotals;
}

let answer: TeamTokenRuns;
let fetchMock: ReturnType<typeof vi.fn>;
let wrapper: VueWrapper | undefined;

const runsUrls = () =>
  fetchMock.mock.calls.map(([url]) => String(url)).filter((url) => url.includes(`/api/teams/${teamId}/tokens/runs`));

beforeEach(() => {
  vi.useFakeTimers({ now, toFake: ['Date'] });
  answer = runs();
  fetchMock = vi.fn(async (url: string) =>
    String(url).includes('/tokens/runs')
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

async function mountStrip(usage: TeamTokenTotals = totals()) {
  setActivePinia(createPinia());
  useConsoleStore().$patch({
    teams: [{ id: teamId, name: 'Alpha', memberAgents: [], repos: [] }],
    activeTeamId: teamId,
    overviewLanded: true,
    workflowSpendLimit: null,
  } as never);

  wrapper = mount(TeamKpiStrip, {
    props: { teamId, containers: board as never, usage, timing: null, workflows: null, clockOffset: 0 },
    attachTo: document.body,
  });

  await settle();

  return wrapper;
}

const dialog = () => document.querySelector<HTMLElement>('.token-dialog-card');

async function openDialog() {
  await wrapper!.find('.team-kpi--tokens').trigger('click');
  await settle();
}

async function choose(group: 'period' | 'metric', label: string) {
  const button = [...dialog()!.querySelectorAll<HTMLButtonElement>(`.tokens-${group} button`)]
    .find((b) => b.textContent?.trim() === label);

  expect(button, `a ${group} button reading "${label}"`).toBeTruthy();
  button!.click();
  await settle();
}

const pressed = (group: 'period' | 'metric') =>
  dialog()!.querySelector(`.tokens-${group} button[aria-pressed="true"]`)?.textContent?.trim();

interface ChartOption {
  legend: { data: string[]; top?: number; padding?: number; itemHeight?: number };
  toolbox: { top?: number; padding?: number; itemSize?: number };
  grid: { top: number; height: number };
  yAxis: { name?: string; nameLocation?: string; nameGap?: number; nameTextStyle?: { fontSize?: number } };
  series: { name: string; data: number[][] }[];
}

/** The option the dialog's chart was given: the last chart on the page. */
function chartOption(): ChartOption {
  const charts = wrapper!.findAllComponents({ name: 'Echarts' });

  return charts[charts.length - 1]!.props('option') as ChartOption;
}

const seriesNamed = (option: ChartOption, name: string) => option.series.find((s) => s.name === name)!;

/** The tooltip ECharts draws for a pointer moved over the columns to an instant, as text. */
async function hoverMarkup(option: ChartOption, at: number): Promise<HTMLElement> {
  const host = document.createElement('div');
  document.body.appendChild(host);

  const chart = init(host, null, { renderer: 'svg', width: 1000, height: 400 });

  try {
    chart.setOption(option as never);

    const [x, y] = chart.convertToPixel({ xAxisIndex: 0, yAxisIndex: 0 }, [at, 1]) as number[];
    chart.getZr().handler.dispatch('mousemove', { zrX: x, zrY: y, offsetX: x, offsetY: y });
    await new Promise((resolve) => setTimeout(resolve, 150));

    const shown = host.querySelector<HTMLElement>('.tokens-tooltip');
    const tip = document.createElement('div');

    if (shown && shown.style.display !== 'none' && shown.style.visibility !== 'hidden') tip.innerHTML = shown.innerHTML;

    return tip;
  } finally {
    chart.dispose();
    host.remove();
  }
}

const hoverText = async (option: ChartOption, at: number) => (await hoverMarkup(option, at)).textContent ?? '';

describe('opening the Tokens dialog', () => {
  it('opens on All workflows and Billable, reading the team\'s whole history by the minute', async () => {
    await mountStrip();
    await openDialog();

    expect(pressed('period')).toBe('All workflows');
    expect(pressed('metric')).toBe('Billable');
    expect(runsUrls()).toEqual([`/api/teams/${teamId}/tokens/runs`]);

    // Half an hour of history: one-minute columns.
    const ines = seriesNamed(chartOption(), 'Ines Lopez');
    expect(ines.data[0]![1]! - ines.data[0]![0]!).toBe(60_000);
  });

  it('puts the chart above the totals and the tables, which are as they were', async () => {
    await mountStrip();
    await openDialog();

    const card = dialog()!;
    const chart = card.querySelector('[data-tokens-chart]')!;
    const summary = card.querySelector('[data-token-summary]')!;

    expect(chart).not.toBeNull();
    expect(chart.compareDocumentPosition(summary) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();

    const text = card.textContent!.replace(/\s+/g, ' ');
    expect(text).toContain('Team total from the message log.');
    expect(card.querySelector('.token-summary-value')!.textContent).toBe((1290).toLocaleString());
    expect(card.querySelector('[data-token-part="in"]')!.textContent).toContain('39');
    expect(card.querySelector('[data-token-table="brand"]')).not.toBeNull();
    expect(card.querySelector('[data-token-row="member:DeveloperInes"] [data-cell="billable"]')!.textContent).toBe((1000).toLocaleString());
  });
});

describe('the period picker', () => {
  const periodUrl = (from: string) =>
    `/api/teams/${teamId}/tokens/runs?from=${encodeURIComponent(from)}&to=${encodeURIComponent(iso(now))}`;

  it('lists the Statistics periods after All workflows, and sends each one\'s from and to', async () => {
    await mountStrip();
    await openDialog();

    expect([...dialog()!.querySelectorAll('.tokens-period button')].map((b) => b.textContent?.trim()))
      .toEqual(['All workflows', 'Last hour', 'Last day', 'Last month', 'Last year']);

    const cases: [string, string][] = [
      ['Last hour', '2026-10-04T13:30:00.000Z'],
      ['Last day', '2026-10-03T14:30:00.000Z'],
      ['Last month', '2026-09-04T14:30:00.000Z'],
      ['Last year', '2025-10-04T14:30:00.000Z'],
    ];

    for (const [label, from] of cases) {
      const before = runsUrls().length;

      await choose('period', label);

      expect(runsUrls().slice(before), label).toEqual([periodUrl(from)]);
      expect(pressed('period')).toBe(label);
    }

    const before = runsUrls().length;
    await choose('period', 'All workflows');
    expect(runsUrls().slice(before)).toEqual([`/api/teams/${teamId}/tokens/runs`]);
  });

  it('opens on All workflows again after another period was chosen', async () => {
    await mountStrip();
    await openDialog();
    await choose('period', 'Last day');

    (dialog()!.querySelector('.q-card__actions .q-btn') as HTMLButtonElement).click();
    await settle();
    await openDialog();

    expect(pressed('period')).toBe('All workflows');
  });
});

describe('the columns', () => {
  it('is one series per member in board order, a removed member marked removed', async () => {
    answer = runs({ runs: [...runs().runs, run('Gone', '14:05:50', { current: false, billable: 7 })] });

    await mountStrip();
    await openDialog();

    const option = chartOption();
    const names = ['Manager', 'Ines Lopez', '<b>Rhea</b>', 'Gone (removed)'];

    expect(option.legend.data).toEqual([...names, 'not measured']);
    expect(option.series.slice(0, 4).map((s) => s.name)).toEqual(names);

    // [from, to, base, top]: in 14:05 Ines sits on the axis, Rhea on Ines, Gone on Rhea.
    expect(seriesNamed(option, 'Ines Lopez').data[0]).toEqual([utc('14:05:00'), utc('14:06:00'), 0, 100]);
    expect(seriesNamed(option, '<b>Rhea</b>').data[0]).toEqual([utc('14:05:00'), utc('14:06:00'), 100, 350]);
    expect(seriesNamed(option, 'Gone (removed)').data[0]).toEqual([utc('14:05:00'), utc('14:06:00'), 350, 357]);
  });

  it('changes the figures with the metric picker, and names the metric on the axis', async () => {
    await mountStrip();
    await openDialog();

    expect([...dialog()!.querySelectorAll('.tokens-metric button')].map((b) => b.textContent?.trim()))
      .toEqual(['Billable', 'In', 'Cache read', 'Cache write', 'Out']);
    expect(chartOption().yAxis.name).toBe('billable tokens');

    const before = runsUrls().length;
    await choose('metric', 'In');

    expect(runsUrls()).toHaveLength(before);
    expect(chartOption().yAxis.name).toBe('in tokens');
    expect(seriesNamed(chartOption(), '<b>Rhea</b>').data[0]).toEqual([utc('14:05:00'), utc('14:06:00'), 10, 35]);

    await choose('metric', 'Out');
    expect(seriesNamed(chartOption(), '<b>Rhea</b>').data[0]).toEqual([utc('14:05:00'), utc('14:06:00'), 3, 6]);
  });

  it('marks a bucket of runs that reported nothing and never draws it as zero', async () => {
    await mountStrip();
    await openDialog();

    const option = chartOption();
    const at = utc('14:09:00');
    const members = option.series.slice(0, 3);

    for (const series of members) expect(series.data.some((d) => d[0] === at), series.name).toBe(false);
    expect(seriesNamed(option, 'not measured').data).toEqual([[at, utc('14:10:00'), 0, 2]]);

    const text = await hoverText(option, utc('14:09:30'));
    expect(text).toContain('2 runs not measured');
    expect(text).toContain('Total not measured');
    expect(text).not.toContain('Total 0');
  });

  it('counts a combined total as split not reported for In, Out and the cache', async () => {
    await mountStrip();
    await openDialog();

    expect(seriesNamed(chartOption(), 'Ines Lopez').data.find((d) => d[0] === utc('14:07:00'))).toEqual([utc('14:07:00'), utc('14:08:00'), 40, 940]);
    expect(seriesNamed(chartOption(), 'not measured').data.some((d) => d[0] === utc('14:07:00'))).toBe(false);

    await choose('metric', 'Cache read');

    const option = chartOption();
    expect(seriesNamed(option, 'Ines Lopez').data.some((d) => d[0] === utc('14:07:00'))).toBe(false);
    expect(seriesNamed(option, 'not measured').data).toContainEqual([utc('14:07:00'), utc('14:08:00'), 200, 1]);
    expect(await hoverText(option, utc('14:07:30'))).toContain('Ines Lopez — 1 run: split not reported');
  });

  it('names the bucket, each member\'s figure and the total on hover, names as text', async () => {
    await mountStrip();
    await openDialog();

    const option = chartOption();
    const markup = await hoverMarkup(option, utc('14:05:30'));
    const text = markup.textContent ?? '';

    expect(text).toContain(minuteOf('14:05:00'));
    expect(text).toContain(`Ines Lopez — ${(100).toLocaleString()}`);
    expect(text).toContain(`<b>Rhea</b> — ${(250).toLocaleString()}`);
    expect(text).toContain(`Total ${(350).toLocaleString()}`);
    expect(markup.querySelector('b')).toBeNull();
  });

  it('reads No runs in this period when the ledger has none', async () => {
    answer = runs({ runs: [] });

    await mountStrip();
    await openDialog();

    expect(dialog()!.textContent).toContain('No runs in this period');
  });
});

describe('the layout', () => {
  // ECharts' own defaults where the option leaves them out.
  const Padding = 5;
  const LegendItemHeight = 14;
  const ToolboxItemSize = 15;
  const AxisNameGap = 15;
  const AxisNameFontSize = 12;
  /** How far a bucket's marker rises above its column's top. */
  const MarkerRise = 13;

  /** The bottom of the legend's row, from the chart's top. */
  const legendBottom = (option: ChartOption) =>
    (option.legend.top ?? 0) + 2 * (option.legend.padding ?? Padding) + (option.legend.itemHeight ?? LegendItemHeight);

  /** The bottom of the toolbox: its icons, and under an icon the title shown on hover. */
  const toolboxBottom = (option: ChartOption) =>
    (option.toolbox.top ?? 0) + 2 * (option.toolbox.padding ?? Padding) + 2 * (option.toolbox.itemSize ?? ToolboxItemSize);

  it('draws the axis name clear of the legend row', async () => {
    await mountStrip();
    await openDialog();

    const option = chartOption();
    const beside = option.yAxis.nameLocation === 'middle' || option.yAxis.nameLocation === 'center';
    const nameTop = beside
      ? option.grid.top
      : option.grid.top - (option.yAxis.nameGap ?? AxisNameGap) - (option.yAxis.nameTextStyle?.fontSize ?? AxisNameFontSize);

    expect(nameTop).toBeGreaterThanOrEqual(legendBottom(option));
  });

  it('leaves the tallest column\'s marker room below the toolbox', async () => {
    await mountStrip();
    await openDialog();

    const option = chartOption();

    expect(option.grid.top - MarkerRise).toBeGreaterThanOrEqual(toolboxBottom(option));
  });
});

describe('the source line', () => {
  it('says where the runs come from, and says so when the log total below differs', async () => {
    await mountStrip();
    await openDialog();

    const source = dialog()!.querySelector('[data-tokens-source]')!.textContent!.trim();
    expect(source).toBe('Runs from the usage ledger, at the time each run ended.');

    // The ledger's measured billable is 1,290 as well: no note.
    expect(dialog()!.querySelector('[data-tokens-differs]')).toBeNull();
  });

  it('adds a note when the message-log total differs from the ledger\'s', async () => {
    await mountStrip(totals({ tokensBillable: 390 }));
    await openDialog();

    const note = dialog()!.querySelector('[data-tokens-differs]')!.textContent!.replace(/\s+/g, ' ').trim();
    expect(note).toContain('message-log total below differs');
    expect(note).toContain('Reset that deleted memory');
  });
});
