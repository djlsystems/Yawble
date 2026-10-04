<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue';
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
  ActivityPeriods,
  ActivityStates,
  type ActivityPeriod,
  activityLanes,
  bucketActivity,
  bucketUnit,
  columnTooltipHtml,
  periodRange,
  stateShapes,
} from '../lib/teamActivity';

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
  /** Milliseconds from this browser's clock to the server's: a period ends at the server's now. */
  clockOffset: number;
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
  };
});

const period = ref<ActivityPeriod>('open');
const periodOptions = ActivityPeriods.map((p) => ({ value: p.value, label: p.label }));
const bucket = computed(() => ActivityPeriods.find((p) => p.value === period.value)!.bucket);
const unit = computed(() => bucketUnit(bucket.value));

const activity = ref<TeamActivity | null>(null);
const loading = ref(false);
const failed = ref(false);

/** Every state the legend shows, toggled off by a click on it. */
const shown = ref<Record<ActivityState, boolean>>({ running: true, waiting: true, blocked: true, failed: true, idle: true });

/** EVERY OPENING STARTS ON OPEN WORKFLOWS, the tile's own window. */
watch(() => props.modelValue, (isOpen) => {
  if (isOpen) period.value = 'open';
}, { flush: 'sync' });

/**
 * READ ON OPENING AND ON A PERIOD CHANGE, AND NOTHING ELSE: zooming, toggling a state or the board
 * moving underneath never re-reads or re-buckets.
 */
let readSeq = 0;

async function read() {
  const seq = ++readSeq;
  const range = periodRange(period.value, Date.now() + props.clockOffset);

  loading.value = true;
  failed.value = false;
  activity.value = null;

  try {
    const answer = await getTeamActivity(asTeamId(props.teamId), range ?? undefined);

    if (seq === readSeq) activity.value = answer;
  } catch {
    if (seq === readSeq) failed.value = true;
  } finally {
    if (seq === readSeq) loading.value = false;
  }
}

watch([() => props.modelValue, period], ([isOpen]) => {
  if (isOpen) void read();
}, { immediate: true });

/** The answer's own now: a snapshot, so open spans count up to the moment the period was read. */
const serverNow = computed(() => (activity.value ? Date.parse(activity.value.serverNow) : 0));

/** Every member with a lane - a member since removed too, marked so, in this dialog only. */
const lanes = computed(() =>
  activity.value ? activityLanes(activity.value, props.containers, serverNow.value, { removed: true }) : []);

const columns = computed(() => bucketActivity(lanes.value, bucket.value, timeZone));

const people = computed(() => new Map(lanes.value.map((lane) => [lane.member, { name: lane.name, initials: lane.initials }])));

const periodFrom = computed(() => (activity.value?.from ? Date.parse(activity.value.from) : null));
const periodTo = computed(() => (activity.value?.to ? Date.parse(activity.value.to) : serverNow.value));

const hasChart = computed(() => columns.value.length > 0 && periodFrom.value !== null);

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

/** A lane's label sits at its middle, `n + 0.5`; the edges between lanes read nothing. */
function laneLabel(value: number): string {
  return value % 1 === 0.5 ? lanes.value[Math.floor(value)]?.name ?? '' : '';
}

const lanesHeight = computed(() => lanes.value.length * LaneHeight);

const option = computed(() => {
  const start = periodFrom.value ?? serverNow.value;
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
    axisLabel: { show: labels, hideOverlap: true },
    axisTick: { show: labels },
    splitLine: { show: false },
    axisPointer: { snap: false, label: { show: false }, lineStyle: { color: palette.ink, width: 1 } },
  });

  return {
    backgroundColor: 'transparent',
    animation: false,
    legend: {
      data: [...ActivityStates],
      selected: { ...shown.value },
      top: 0,
      left: 0,
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
    tooltip: {
      trigger: 'axis',
      triggerOn: 'mousemove|click',
      axisPointer: { type: 'line' },
      confine: true,
      transitionDuration: 0,
      className: 'stats-tooltip',
      formatter: (params: unknown) => {
        const first = (Array.isArray(params) ? params[0] : params) as { axisValue?: number | string } | undefined;
        const at = Number(first?.axisValue ?? NaN);
        const column = columns.value.find((c) => c.from <= at && at < c.to);

        return column ? columnTooltipHtml(column, bucket.value, timeZone, people.value) : '';
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
    ],
  };
});

const chartHeight = computed(() => `${36 + lanesHeight.value + ColumnsGap + ColumnsHeight + 64}px`);

const chart = ref<InstanceType<typeof VChart> | null>(null);

/** FIT: the whole period again, after any zoom. */
function fit() {
  chart.value?.dispatchAction({ type: 'dataZoom', start: 0, end: 100 });
}

watch(period, () => {
  shown.value = { running: true, waiting: true, blocked: true, failed: true, idle: true };
  void nextTick(fit);
});
</script>

<template>
  <q-dialog v-model="open">
    <q-card ref="cardEl" class="stats-dialog-card os-dialog-xl">
      <q-card-section class="q-pb-none">
        <div class="os-dialog-title">Statistics</div>
        <div class="text-caption os-text-muted q-mt-xs">
          Member-time by state, in {{ unit.name }} per {{ bucket }}.
        </div>
      </q-card-section>

      <q-card-section class="stats-dialog-controls">
        <q-btn-toggle
          v-model="period"
          :options="periodOptions"
          class="stats-period"
          dense no-caps unelevated
          toggle-color="primary"
        />
        <q-btn
          flat dense no-caps
          class="stats-fit"
          label="Fit"
          :disable="!hasChart"
          @click="fit"
        />
      </q-card-section>

      <q-card-section>
        <div v-if="loading" class="stats-dialog-note os-text-muted">Reading…</div>
        <div v-else-if="failed" class="stats-dialog-note os-text-muted">The activity could not be read.</div>
        <div v-else-if="!hasChart" class="stats-dialog-note os-text-muted">No runs in this period</div>
        <v-chart
          v-else
          ref="chart"
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
.stats-dialog-controls {
  display: flex;
  align-items: center;
  gap: 12px;
  flex-wrap: wrap;
}

.stats-dialog-chart {
  width: 100%;
}

.stats-dialog-note {
  padding: 24px 0;
  text-align: center;
}

/* THE TOOLTIP is markup ECharts places inside this card, so it is reached with `:deep`. Classes
   only: the CSP refuses a `style` attribute in that markup. */
.stats-dialog-card :deep(.stats-tip-time) {
  font-weight: 600;
  margin-bottom: 2px;
}

.stats-dialog-card :deep(.stats-tip-members) {
  margin-top: 4px;
}

.stats-dialog-card :deep(.stats-tip-row) {
  white-space: nowrap;
}

.stats-dialog-card :deep(.stats-tip-initials) {
  font-weight: 600;
}

.stats-dialog-card :deep(.stats-chip) {
  display: inline-block;
  width: 9px;
  height: 9px;
  margin-right: 0.4em;
  border-radius: 2px;
}

.stats-dialog-card :deep(.stats-chip--running) { background: var(--os-stat-running); }
.stats-dialog-card :deep(.stats-chip--waiting) { background: var(--os-stat-waiting); }
.stats-dialog-card :deep(.stats-chip--blocked) { background: var(--os-stat-blocked); }
.stats-dialog-card :deep(.stats-chip--failed) { background: var(--os-stat-failed); }
.stats-dialog-card :deep(.stats-chip--idle) { background: var(--os-stat-idle); }
</style>
