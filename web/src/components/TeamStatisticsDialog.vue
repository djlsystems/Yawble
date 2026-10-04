<script setup lang="ts">
import { computed, ref, watch } from 'vue';
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
import type { ActivityState, ContainerSnapshot, TeamActivity } from '../api/types';
import { asTeamId } from '../api/types';
import { getTeamActivity } from '../api/client';
import {
  ActivityStates,
  activityLanes,
  axisTimeLabel,
  bucketActivity,
  bucketUnit,
  bucketWords,
  columnTooltipHtml,
  stateShapes,
  tooltipBeside,
  windowBucket,
  windowEnd,
} from '../lib/teamActivity';
import { crossesDays, localStretch } from '../lib/localTime';

/**
 * TREE-SHAKEN: the custom series the lanes and columns are drawn with, two grids on one time axis,
 * the legend that toggles each state, the zoom - dragged on the axis, wheeled, or brushed with the
 * toolbox - and the tooltip with its hover line. SVG, as on the tile.
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
  modelValue: boolean;
  teamId: string;
  /** The board's members: a lane's label is the board's name for the member. */
  containers: ContainerSnapshot[];
}>();

const emit = defineEmits<{ 'update:modelValue': [value: boolean] }>();

const open = computed({
  get: () => props.modelValue,
  set: (value: boolean) => emit('update:modelValue', value),
});

/** A reference lane's height. */
const LaneHeight = 16;

/** The columns' own height, under the lanes. */
const ColumnsHeight = 240;

/** THE SAME LEFT EDGE FOR BOTH GRIDS, so a lane and the columns under it share one time axis. */
const GridLeft = 128;

/** Room between the lanes and the columns for the y axis's unit. */
const ColumnsGap = 32;

/** Room between the pointer and the hover box, so the hover line stays in sight beside it. */
const TooltipGap = 16;

/** Bucket edges fall on this browser's own timezone. */
const timeZone = Intl.DateTimeFormat().resolvedOptions().timeZone;

const $q = useQuasar();
const dark = computed(() => $q.dark?.isActive === true);

const cardEl = ref<{ $el: HTMLElement } | null>(null);

/** The theme's `--os-stat-*` tokens as the browser resolves them, read again when the theme flips. */
const colours = computed(() => {
  void dark.value;
  void activity.value;

  const el = cardEl.value?.$el;
  const style = el ? getComputedStyle(el) : null;
  const token = (name: string) => style?.getPropertyValue(name).trim() ?? '';

  return {
    running: token('--os-stat-running'),
    waiting: token('--os-stat-waiting'),
    blocked: token('--os-stat-blocked'),
    failed: token('--os-stat-failed'),
    idle: token('--os-stat-idle'),
    chrome: token('--os-surface') || token('--os-chrome'),
    ink: token('--os-ink'),
    off: token('--os-ink-faint'),
  };
});

const activity = ref<TeamActivity | null>(null);
const loading = ref(false);
const failed = ref(false);

/** Every state the legend shows, toggled off by a click on it. */
const shown = ref<Record<ActivityState, boolean>>({ running: true, waiting: true, blocked: true, failed: true, idle: true });

/**
 * THE TILE'S OWN WINDOW, READ ON OPENING AND NOTHING ELSE: from the team's earliest workflow root to
 * the latest activity of any workflow. Zooming, toggling a state or the board moving underneath
 * never re-reads or re-buckets.
 */
let readSeq = 0;

async function read() {
  const seq = ++readSeq;

  loading.value = true;
  failed.value = false;
  activity.value = null;
  shown.value = { running: true, waiting: true, blocked: true, failed: true, idle: true };

  try {
    const answer = await getTeamActivity(asTeamId(props.teamId));

    if (seq === readSeq) activity.value = answer;
  } catch {
    if (seq === readSeq) failed.value = true;
  } finally {
    if (seq === readSeq) loading.value = false;
  }
}

watch(() => props.modelValue, (isOpen) => {
  if (isOpen) void read();
}, { immediate: true });

/** The window's end as the server gave it: open spans are drawn to here, never to the clock. */
const periodTo = computed(() => (activity.value ? windowEnd(activity.value) : 0));
const periodFrom = computed(() => (activity.value?.from ? Date.parse(activity.value.from) : null));

/** THE COLUMN SIZE THAT FITS THE WINDOW: the minute, hour, day or month (`windowBucket`). */
const bucket = computed(() => windowBucket(periodFrom.value ?? periodTo.value, periodTo.value));
const unit = computed(() => bucketUnit(bucket.value));

/** A time of day alone is ambiguous once the window crosses midnight: then every time has its date. */
const withDate = computed(() => periodFrom.value !== null && crossesDays(periodFrom.value, periodTo.value));

/** The subtitle: the stretch and the column size, "11:38:02 AM – 3:41:17 PM, member-minutes per minute". */
const subtitle = computed(() => {
  const what = `${unit.value.name} ${bucketWords(bucket.value)}`;

  return periodFrom.value === null ? `Member-time by state, in ${what}.` : `${localStretch(periodFrom.value, periodTo.value)}, ${what}`;
});

/** Every member with a lane - a member since removed too, marked so, in this dialog only. */
const lanes = computed(() =>
  activity.value ? activityLanes(activity.value, props.containers, periodTo.value, { removed: true }) : []);

const columns = computed(() => bucketActivity(lanes.value, bucket.value, timeZone));

const people = computed(() => new Map(lanes.value.map((lane) => [lane.member, { name: lane.name, initials: lane.initials }])));

const hasChart = computed(() => columns.value.length > 0 && periodFrom.value !== null);

/** The states the legend has turned off: left out of the hover, as they are out of the drawing. */
const hiddenStates = computed(() => new Set(ActivityStates.filter((state) => !shown.value[state])));

/** THE HOVER BOX BESIDE THE POINTER, never over it, placed against the screen: it is on the body. */
function besidePointer(point: number[], _params: unknown, _dom: unknown, _rect: unknown, size: { contentSize: number[] }) {
  const chart = cardEl.value?.$el.querySelector('.stats-dialog-chart')?.getBoundingClientRect() ?? { left: 0, top: 0 };

  return tooltipBeside(point, size.contentSize, chart, { width: window.innerWidth, height: window.innerHeight }, TooltipGap);
}

/**
 * EACH STATE'S SERIES: one entry per column it has time in, `[from, to, base, top]` in the y unit,
 * stacked in state order over the states the legend shows - a hidden state takes no room, so the
 * ones above it settle down onto the ones below.
 */
const stacks = computed(() => {
  const series = Object.fromEntries(ActivityStates.map((state) => [state, [] as number[][]])) as Record<ActivityState, number[][]>;

  for (const column of columns.value) {
    let base = 0;

    for (const state of ActivityStates) {
      const height = column.totals[state] / unit.value.ms;

      if (height <= 0) continue;

      series[state].push([column.from, column.to, base, base + height]);
      if (shown.value[state]) base += height;
    }
  }

  return series;
});

function onLegend(event: unknown) {
  const selected = (event as { selected?: Record<string, boolean> }).selected ?? {};

  shown.value = { ...shown.value, ...selected } as Record<ActivityState, boolean>;
}

/** ONE STATE OF ONE COLUMN: its bucket's full width, from its base to its top, notched as on the tile. */
function renderColumn(state: ActivityState) {
  return (params: CustomSeriesRenderItemParams, api: CustomSeriesRenderItemAPI): CustomSeriesRenderItemReturn => {
    const low = api.coord([api.value(0), api.value(2)]);
    const high = api.coord([api.value(1), api.value(3)]);
    const area = params.coordSys as unknown as { x: number; width: number };

    const left = Math.max(low[0]!, area.x);
    const right = Math.min(high[0]!, area.x + area.width);

    if (right <= area.x || left >= area.x + area.width) return { type: 'group', children: [] } as CustomSeriesRenderItemReturn;

    const width = Math.max(right - left - 1, 1);
    const height = Math.max(low[1]! - high[1]!, 1);

    return { type: 'group', children: stateShapes(left, high[1]!, width, height, state, colours.value) } as unknown as CustomSeriesRenderItemReturn;
  };
}

/** ONE SPAN OF A REFERENCE LANE, as on the tile: lane `n` runs from `n` to `n + 1` on its axis. */
function renderLane(params: CustomSeriesRenderItemParams, api: CustomSeriesRenderItemAPI): CustomSeriesRenderItemReturn {
  const lane = Number(api.value(0));
  const start = api.coord([api.value(1), lane]);
  const end = api.coord([api.value(2), lane + 1]);
  const state = ActivityStates[Number(api.value(3))] ?? 'idle';
  const area = params.coordSys as unknown as { x: number; width: number };

  const left = Math.max(start[0]!, area.x);
  const right = Math.min(end[0]!, area.x + area.width);

  if (right <= area.x || left >= area.x + area.width) return { type: 'group', children: [] } as CustomSeriesRenderItemReturn;

  return {
    type: 'group',
    children: stateShapes(left, start[1]! + 1, Math.max(right - left, 1), Math.max(end[1]! - start[1]! - 2, 1), state, colours.value),
  } as unknown as CustomSeriesRenderItemReturn;
}

/**
 * ONE COLUMN'S HOVER AREA, drawn as nothing over its bucket's whole width and its grid's whole
 * height: the pointer anywhere over a column, above its top too, is over this column and no other.
 */
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

/** A lane's label sits at its middle, `n + 0.5`; the edges between lanes read nothing. */
function laneLabel(value: number): string {
  return value % 1 === 0.5 ? lanes.value[Math.floor(value)]?.name ?? '' : '';
}

const lanesHeight = computed(() => lanes.value.length * LaneHeight);

const option = computed(() => {
  const start = periodFrom.value ?? periodTo.value;
  const end = Math.max(periodTo.value, start + 1);
  const palette = colours.value;
  const laneTop = 36;
  const columnsTop = laneTop + lanesHeight.value + ColumnsGap;

  const timeAxis = (gridIndex: number, labels: boolean) => ({
    type: 'time',
    gridIndex,
    min: start,
    max: end,
    axisLine: { show: labels },
    axisLabel: { show: labels, hideOverlap: true, formatter: (value: number) => axisTimeLabel(value, bucket.value, timeZone) },
    axisTick: { show: labels },
    splitLine: { show: false },
    // The hover line lights up nothing: it would light the item NEAREST the pointer, which in a
    // column's right half is the next column, and disagree with the tooltip.
    axisPointer: { show: true, snap: false, triggerEmphasis: false, label: { show: false }, lineStyle: { color: palette.ink, width: 1 } },
  });

  return {
    backgroundColor: 'transparent',
    animation: false,
    legend: {
      data: [...ActivityStates],
      selected: { ...shown.value },
      top: 0,
      left: 0,
      // OFF READS DIMMER THAN ON in either theme, not ECharts' own light grey.
      inactiveColor: palette.off,
      inactiveBorderColor: palette.off,
    },
    toolbox: {
      right: 0,
      top: 0,
      feature: { dataZoom: { xAxisIndex: [0, 1], yAxisIndex: false } },
    },
    grid: [
      { left: GridLeft, right: 16, top: laneTop, height: lanesHeight.value },
      { left: GridLeft, right: 16, top: columnsTop, height: ColumnsHeight },
    ],
    xAxis: [timeAxis(0, false), timeAxis(1, true)],
    yAxis: [
      {
        type: 'value',
        gridIndex: 0,
        inverse: true,
        min: 0,
        max: Math.max(lanes.value.length, 1),
        interval: 0.5,
        splitLine: { show: false },
        axisLine: { show: false },
        axisTick: { show: false },
        axisLabel: { fontSize: 10, formatter: laneLabel, width: GridLeft - 12, overflow: 'truncate' },
      },
      {
        type: 'value',
        gridIndex: 1,
        name: unit.value.name,
        nameTextStyle: { align: 'right' },
        min: 0,
      },
    ],
    // ZOOM ONLY: the columns stay the buckets they were read as, and the lanes follow the range.
    dataZoom: [
      { type: 'inside', xAxisIndex: [0, 1], filterMode: 'none' },
      { type: 'slider', xAxisIndex: [0, 1], filterMode: 'none', height: 18, bottom: 4 },
    ],
    axisPointer: { link: [{ xAxisIndex: 'all' }] },
    // THE COLUMN UNDER THE POINTER, from the hover area it is over. An axis trigger would hand the
    // formatter the nearest column's start, which in a column's right half is the next column.
    tooltip: {
      trigger: 'item',
      triggerOn: 'mousemove|click',
      // OUTSIDE THE CARD, beside the pointer: never over the hover line.
      confine: false,
      appendTo: 'body',
      position: besidePointer,
      transitionDuration: 0,
      className: 'stats-tooltip',
      formatter: (params: unknown) => {
        const from = Number((params as { value?: number[] } | undefined)?.value?.[0] ?? NaN);
        const column = columns.value.find((c) => c.from === from);

        return column
          ? columnTooltipHtml(column, bucket.value, timeZone, people.value, { hidden: hiddenStates.value, withDate: withDate.value })
          : '';
      },
    },
    series: [
      {
        type: 'custom',
        name: 'lanes',
        xAxisIndex: 0,
        yAxisIndex: 0,
        renderItem: renderLane,
        encode: { x: [1, 2], y: 0 },
        data: lanes.value.flatMap((lane, index) =>
          lane.spans.map((span) => [index, span.from, span.to, ActivityStates.indexOf(span.state)])),
      },
      ...ActivityStates.map((state) => ({
        type: 'custom',
        name: state,
        xAxisIndex: 1,
        yAxisIndex: 1,
        itemStyle: { color: palette[state] },
        renderItem: renderColumn(state),
        encode: { x: [0, 1], y: [2, 3] },
        data: stacks.value[state],
      })),
      {
        // THE WHOLE PERIOD, drawn as nothing, so the hover line has something to stand on.
        type: 'custom',
        name: 'period',
        silent: true,
        xAxisIndex: 1,
        yAxisIndex: 1,
        renderItem: () => ({ type: 'group', children: [] }),
        encode: { x: [0, 1], y: [2, 3] },
        data: [[start, end, 0, 0]],
      },
      // Over the lanes and over the columns alike, on top of what they cover.
      ...[0, 1].map((grid) => ({
        type: 'custom',
        name: 'hover',
        z: 10,
        xAxisIndex: grid,
        yAxisIndex: grid,
        emphasis: { disabled: true },
        renderItem: renderHover,
        encode: { x: [0, 1] },
        data: columns.value.map((column) => [column.from, column.to]),
      })),
    ],
  };
});

const chartHeight = computed(() => `${36 + lanesHeight.value + ColumnsGap + ColumnsHeight + 64}px`);
</script>

<template>
  <q-dialog v-model="open">
    <q-card ref="cardEl" class="stats-dialog-card os-dialog-xl">
      <q-card-section class="q-pb-none">
        <div class="os-dialog-title">Statistics</div>
        <div class="text-caption os-text-muted q-mt-xs stats-dialog-subtitle">{{ subtitle }}</div>
      </q-card-section>

      <q-card-section>
        <div v-if="loading" class="stats-dialog-note os-text-muted">Reading…</div>
        <div v-else-if="failed" class="stats-dialog-note os-text-muted">The activity could not be read.</div>
        <div v-else-if="!hasChart" class="stats-dialog-note os-text-muted">No runs in this window</div>
        <v-chart
          v-else
          class="stats-dialog-chart"
          :style="{ height: chartHeight }"
          :option="option"
          :theme="dark ? 'dark' : ''"
          :init-options="{ renderer: 'svg' }"
          autoresize
          @legendselectchanged="onLegend"
        />
      </q-card-section>

      <q-card-actions align="right">
        <q-btn flat no-caps label="Done" class="stats-done" @click="open = false" />
      </q-card-actions>
    </q-card>
  </q-dialog>
</template>

<style scoped>
.stats-dialog-chart {
  width: 100%;
}

.stats-dialog-note {
  padding: 24px 0;
  text-align: center;
}
</style>
