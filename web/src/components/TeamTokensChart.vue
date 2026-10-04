<script setup lang="ts">
import { computed, nextTick, onMounted, ref, watch } from 'vue';
import { useQuasar } from 'quasar';
import { use } from 'echarts/core';
import { CustomChart } from 'echarts/charts';
import {
  AxisPointerComponent,
  DataZoomComponent,
  GridComponent,
  LegendComponent,
  ToolboxComponent,
  TooltipComponent,
} from 'echarts/components';
import { SVGRenderer } from 'echarts/renderers';
import type { CustomSeriesRenderItemAPI, CustomSeriesRenderItemParams, CustomSeriesRenderItemReturn } from 'echarts';
import VChart from 'vue-echarts';
import type { ContainerSnapshot, TeamTokenRuns } from '../api/types';
import { asTeamId } from '../api/types';
import { getTeamTokenRuns } from '../api/client';
import {
  SeriesSlots,
  TokenMetrics,
  type InstantFigure,
  type TokenMetric,
  axisTimeLabel,
  bucketFigures,
  figureTooltipHtml,
  runFigure,
  tooltipBeside,
  windowBucket,
} from '../lib/teamActivity';
import { crossesDays } from '../lib/localTime';

/**
 * THE TOKENS DIALOG'S CHART: when the team spent, and who, as stacked columns per bucket - one
 * series per member - over the team's runs from the usage ledger, each at the instant it ended,
 * drawn over the Statistics tile's own window: the team's earliest workflow root to the latest
 * activity of any workflow, never the clock.
 * Tree-shaken and drawn as in the Statistics dialog: custom series, the legend that toggles each
 * member, the zoom and the tooltip, SVG.
 *
 * NOTHING IS DRAWN AS ZERO. A run with no figure for the chosen metric adds nothing to a column; a
 * bucket holding such runs carries a marker and its tooltip counts them.
 */
use([
  CustomChart,
  GridComponent,
  LegendComponent,
  DataZoomComponent,
  ToolboxComponent,
  TooltipComponent,
  AxisPointerComponent,
  SVGRenderer,
]);

const props = defineProps<{
  teamId: string;
  /** The board's members, in board order: the series order and each member's label. */
  containers: ContainerSnapshot[];
  /** The team's billable total from the message log, as the totals below show it; null when unknown. */
  logBillable: number | null;
}>();

/** The marker series' name, in the legend and nowhere a member could be. */
const NotMeasured = 'not measured';

const ColumnsHeight = 240;

/** Room between the pointer and the hover box, so the hover line stays in sight beside it. */
const TooltipGap = 16;

/** Bucket edges fall on this browser's own timezone. */
const timeZone = Intl.DateTimeFormat().resolvedOptions().timeZone;

const $q = useQuasar();
const dark = computed(() => $q.dark?.isActive === true);

const rootEl = ref<HTMLElement | null>(null);

/** The theme's `--os-series-*` tokens as the browser resolves them, read again when the theme flips. */
const colours = computed(() => {
  void dark.value;
  void answer.value;

  const style = rootEl.value ? getComputedStyle(rootEl.value) : null;
  const token = (name: string) => style?.getPropertyValue(name).trim() ?? '';

  return {
    series: Array.from({ length: SeriesSlots }, (_, i) => token(`--os-series-${i + 1}`)),
    ink: token('--os-ink'),
    off: token('--os-ink-faint'),
  };
});

const metric = ref<TokenMetric>('billable');
const metricOptions = TokenMetrics.map((m) => ({ value: m.value, label: m.label }));

const answer = ref<TeamTokenRuns | null>(null);
const loading = ref(false);
const failed = ref(false);

/** Every member the legend shows, by stored name; a click on the legend hides one. */
const hidden = ref<Set<string>>(new Set());

/** Whether the legend has turned the not-measured markers off: then the hover leaves them out too. */
const gapsHidden = ref(false);

/** READ ON OPENING ONLY: the metric, the legend and zooming re-read nothing. */
let readSeq = 0;

async function read() {
  const seq = ++readSeq;

  loading.value = true;
  failed.value = false;
  answer.value = null;

  try {
    const read = await getTeamTokenRuns(asTeamId(props.teamId));

    if (seq === readSeq) answer.value = read;
  } catch {
    if (seq === readSeq) failed.value = true;
  } finally {
    if (seq === readSeq) loading.value = false;
  }
}

onMounted(() => void read());

const runs = computed(() => answer.value?.runs ?? []);

/**
 * THE WINDOW'S EDGES: the answer's own - the team's earliest workflow root to the latest workflow
 * activity - or, for a team with no workflow, its first and last run's ends. Never the clock.
 */
const periodFrom = computed(() => {
  if (answer.value?.from) return Date.parse(answer.value.from);
  const first = runs.value[0];

  return first ? Date.parse(first.endedAt) : null;
});
const periodTo = computed(() => {
  if (answer.value?.to) return Date.parse(answer.value.to);
  const last = runs.value[runs.value.length - 1];

  return last ? Date.parse(last.endedAt) : periodFrom.value ?? 0;
});

/** THE COLUMN SIZE THAT FITS THE WINDOW, by the Statistics dialog's own function. */
const bucket = computed(() => windowBucket(periodFrom.value ?? periodTo.value, periodTo.value));

/** A time of day alone is ambiguous once the window crosses midnight: then every time has its date. */
const withDate = computed(() => periodFrom.value !== null && crossesDays(periodFrom.value, periodTo.value));

/**
 * ONE SERIES PER MEMBER: the current members as the board lists them, then any current member the
 * board does not list yet, then each member since removed, labelled "<name> (removed)".
 */
const people = computed(() => {
  const listed = new Map<string, { name: string }>();
  const lower = (name: string) => name.toLowerCase();

  for (const container of props.containers) {
    if (runs.value.some((run) => run.current && lower(run.member) === lower(container.id))) {
      listed.set(runs.value.find((run) => lower(run.member) === lower(container.id))!.member, { name: container.name });
    }
  }

  for (const run of runs.value.filter((r) => r.current)) {
    if (!listed.has(run.member)) listed.set(run.member, { name: run.member });
  }

  for (const run of runs.value.filter((r) => !r.current)) {
    if (!listed.has(run.member)) listed.set(run.member, { name: `${run.member} (removed)` });
  }

  return listed;
});

const members = computed(() => [...people.value.keys()]);

const figures = computed<InstantFigure[]>(() => runs.value.map((run) => {
  const figure = runFigure(run, metric.value);
  const at = Date.parse(run.endedAt);

  return 'value' in figure
    ? { member: run.member, at, value: figure.value }
    : { member: run.member, at, value: null, gap: figure.gap };
}));

const columns = computed(() => bucketFigures(figures.value, members.value, bucket.value, timeZone));

const hasChart = computed(() => columns.value.length > 0);

/**
 * EACH MEMBER'S SERIES: `[from, to, base, top]` per column it has a figure in, stacked in board order
 * over the members the legend shows - a hidden member takes no room. A member whose runs in a column
 * all lack a figure has no entry there: no figure is never a column of 0.
 */
const stacks = computed(() => {
  const series = new Map(members.value.map((member) => [member, [] as number[][]]));
  const tops: number[] = [];

  for (const column of columns.value) {
    let base = 0;

    for (const share of column.members) {
      if (share.measured === 0) continue;

      series.get(share.member)!.push([column.from, column.to, base, base + share.value]);
      if (!hidden.value.has(share.member)) base += share.value;
    }

    tops.push(base);
  }

  return { series, tops };
});

/** THE MARKERS: one per bucket holding runs with no figure, `[from, to, top, runs]`, on its column. */
const markers = computed(() => columns.value
  .map((column, index) => [column.from, column.to, stacks.value.tops[index]!, column.unmeasured + column.unsplit])
  .filter((marker) => marker[3]! > 0));

/** The ledger's billable over the team's whole history: every run since its creation is listed. */
const ledgerBillable = computed(() => {
  if (!answer.value) return null;

  return runs.value.reduce((sum, run) => sum + (run.measured ? run.billable ?? 0 : 0), 0);
});

/**
 * THE NOTE UNDER THE SOURCE LINE, when the chart's whole history and the message-log total below
 * disagree: a Reset that deleted memory took runs off the log, and the ledger keeps every one.
 */
const differs = computed(() =>
  ledgerBillable.value !== null && props.logBillable !== null && ledgerBillable.value !== props.logBillable);

function onLegend(event: unknown) {
  const selected = (event as { selected?: Record<string, boolean> }).selected ?? {};
  const byLabel = new Map([...people.value].map(([member, person]) => [person.name, member]));

  hidden.value = new Set(Object.entries(selected)
    .filter(([label, on]) => !on && byLabel.has(label))
    .map(([label]) => byLabel.get(label)!));
  gapsHidden.value = selected[NotMeasured] === false;
}

/** THE HOVER BOX BESIDE THE POINTER, never over it, placed against the screen: it is on the body. */
function besidePointer(point: number[], _params: unknown, _dom: unknown, _rect: unknown, size: { contentSize: number[] }) {
  const chart = rootEl.value?.querySelector('.tokens-chart-canvas')?.getBoundingClientRect() ?? { left: 0, top: 0 };

  return tooltipBeside(point, size.contentSize, chart, { width: window.innerWidth, height: window.innerHeight }, TooltipGap);
}

/** ONE MEMBER'S PART OF A COLUMN: its bucket's full width, from its base to its top. */
function renderColumn(colour: () => string) {
  return (params: CustomSeriesRenderItemParams, api: CustomSeriesRenderItemAPI): CustomSeriesRenderItemReturn => {
    const low = api.coord([api.value(0), api.value(2)]);
    const high = api.coord([api.value(1), api.value(3)]);
    const area = params.coordSys as unknown as { x: number; width: number };

    const left = Math.max(low[0]!, area.x);
    const right = Math.min(high[0]!, area.x + area.width);

    if (right <= area.x || left >= area.x + area.width) return { type: 'group', children: [] } as CustomSeriesRenderItemReturn;

    return {
      type: 'rect',
      shape: { x: left, y: high[1]!, width: Math.max(right - left - 1, 1), height: Math.max(low[1]! - high[1]!, 1) },
      style: { fill: colour() },
    } as unknown as CustomSeriesRenderItemReturn;
  };
}

/** A BUCKET'S MARKER: a hollow triangle over its column's top, or over the axis where it has none. */
function renderMarker(params: CustomSeriesRenderItemParams, api: CustomSeriesRenderItemAPI): CustomSeriesRenderItemReturn {
  const middle = (Number(api.value(0)) + Number(api.value(1))) / 2;
  const [x, y] = api.coord([middle, api.value(2)]) as [number, number];
  const area = params.coordSys as unknown as { x: number; width: number };

  if (x < area.x || x > area.x + area.width) return { type: 'group', children: [] } as CustomSeriesRenderItemReturn;

  return {
    type: 'polygon',
    shape: { points: [[x, y - 4], [x - 5, y - 13], [x + 5, y - 13]] },
    style: { fill: 'transparent', stroke: colours.value.ink, lineWidth: 1.5 },
  } as unknown as CustomSeriesRenderItemReturn;
}

/** ONE COLUMN'S HOVER AREA, drawn as nothing over its bucket's whole width and the grid's height. */
function renderHover(params: CustomSeriesRenderItemParams, api: CustomSeriesRenderItemAPI): CustomSeriesRenderItemReturn {
  const area = params.coordSys as unknown as { x: number; y: number; width: number; height: number };
  const left = Math.max(api.coord([api.value(0), 0])[0]!, area.x);
  const right = Math.min(api.coord([api.value(1), 0])[0]!, area.x + area.width);

  if (right <= left) return { type: 'group', children: [] } as CustomSeriesRenderItemReturn;

  return {
    type: 'rect',
    shape: { x: left, y: area.y, width: right - left, height: area.height },
    style: { fill: 'transparent' },
  } as unknown as CustomSeriesRenderItemReturn;
}

const option = computed(() => {
  const start = Math.min(periodFrom.value ?? periodTo.value, columns.value[0]?.from ?? Infinity);
  const end = Math.max(periodTo.value, columns.value[columns.value.length - 1]?.to ?? 0, start + 1);
  const palette = colours.value;
  const colour = (index: number) => palette.series[index % SeriesSlots] ?? palette.ink;
  const metricLabel = TokenMetrics.find((m) => m.value === metric.value)!.label.toLowerCase();

  return {
    backgroundColor: 'transparent',
    animation: false,
    legend: {
      data: [...members.value.map((member) => people.value.get(member)!.name), ...(markers.value.length > 0 ? [NotMeasured] : [])],
      selected: {
        ...Object.fromEntries(members.value.map((member) => [people.value.get(member)!.name, !hidden.value.has(member)])),
        [NotMeasured]: !gapsHidden.value,
      },
      top: 0,
      left: 0,
      right: 64,
      type: 'scroll',
      inactiveColor: palette.off,
      inactiveBorderColor: palette.off,
    },
    toolbox: { right: 0, top: 0, feature: { dataZoom: { yAxisIndex: false } } },
    // The columns start below the legend's row and far enough below the toolbox for a marker over the tallest one.
    grid: { left: 92, right: 16, top: 56, height: ColumnsHeight },
    xAxis: {
      type: 'time',
      min: start,
      max: end,
      axisLabel: { hideOverlap: true, formatter: (value: number) => axisTimeLabel(value, bucket.value, timeZone) },
      splitLine: { show: false },
      axisPointer: { show: true, snap: false, triggerEmphasis: false, label: { show: false }, lineStyle: { color: palette.ink, width: 1 } },
    },
    yAxis: { type: 'value', name: `${metricLabel} tokens`, nameLocation: 'middle', nameGap: 76, min: 0 },
    dataZoom: [
      { type: 'inside', filterMode: 'none' },
      { type: 'slider', filterMode: 'none', height: 18, bottom: 4 },
    ],
    // THE COLUMN UNDER THE POINTER, from the hover area it is over, never the nearest column start.
    tooltip: {
      trigger: 'item',
      triggerOn: 'mousemove|click',
      // OUTSIDE THE DIALOG'S CARD, beside the pointer: never over the hover line.
      confine: false,
      appendTo: 'body',
      position: besidePointer,
      transitionDuration: 0,
      className: 'stats-tooltip tokens-tooltip',
      formatter: (params: unknown) => {
        const from = Number((params as { value?: number[] } | undefined)?.value?.[0] ?? NaN);
        const column = columns.value.find((c) => c.from === from);

        return column
          ? figureTooltipHtml(column, bucket.value, timeZone, people.value, { hidden: hidden.value, gapsHidden: gapsHidden.value, withDate: withDate.value })
          : '';
      },
    },
    series: [
      ...members.value.map((member, index) => ({
        type: 'custom',
        name: people.value.get(member)!.name,
        itemStyle: { color: colour(index) },
        renderItem: renderColumn(() => colour(index)),
        encode: { x: [0, 1], y: [2, 3] },
        data: stacks.value.series.get(member)!,
      })),
      {
        type: 'custom',
        name: NotMeasured,
        z: 5,
        itemStyle: { color: palette.ink },
        renderItem: renderMarker,
        encode: { x: [0, 1], y: 2 },
        data: markers.value,
      },
      {
        type: 'custom',
        name: 'hover',
        z: 10,
        emphasis: { disabled: true },
        renderItem: renderHover,
        encode: { x: [0, 1] },
        data: columns.value.map((column) => [column.from, column.to]),
      },
    ],
  };
});

const chart = ref<InstanceType<typeof VChart> | null>(null);

/** A new metric shows the whole window again, after any zoom. */
watch(metric, () => void nextTick(() => chart.value?.dispatchAction({ type: 'dataZoom', start: 0, end: 100 })));
</script>

<template>
  <div ref="rootEl" class="tokens-chart" data-tokens-chart>
    <div class="tokens-chart-controls">
      <q-btn-toggle
        v-model="metric"
        :options="metricOptions"
        class="tokens-metric"
        dense no-caps unelevated
        toggle-color="primary"
      />
    </div>

    <div v-if="loading" class="tokens-chart-note os-text-muted">Reading…</div>
    <div v-else-if="failed" class="tokens-chart-note os-text-muted">The runs could not be read.</div>
    <div v-else-if="!hasChart" class="tokens-chart-note os-text-muted">No runs in this window</div>
    <v-chart
      v-else
      ref="chart"
      class="tokens-chart-canvas"
      :option="option"
      :theme="dark ? 'dark' : ''"
      :init-options="{ renderer: 'svg' }"
      autoresize
      @legendselectchanged="onLegend"
    />

    <div class="text-caption os-text-muted" data-tokens-source>
      Runs from the usage ledger, at the time each run ended.
    </div>
    <div v-if="differs" class="text-caption os-text-muted" data-tokens-differs>
      The message-log total below differs: a Reset that deleted memory took runs off the log, and the
      usage ledger keeps every run.
    </div>
  </div>
</template>

<style scoped>
.tokens-chart-controls {
  display: flex;
  align-items: center;
  gap: 12px;
  flex-wrap: wrap;
  margin-bottom: 8px;
}

.tokens-chart-canvas {
  width: 100%;
  height: 336px;
}

.tokens-chart-note {
  padding: 24px 0;
  text-align: center;
}
</style>
