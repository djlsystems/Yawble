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
import type { ContainerSnapshot, TeamActivity } from '../api/types';
import { asTeamId } from '../api/types';
import { getTeamActivity } from '../api/client';
import {
  ActivityStates,
  activityLanes,
  axisTimeLabel,
  stateShapes,
  stateWords,
  tooltipBeside,
  tooltipHtml,
  windowBucket,
  windowEnd,
} from '../lib/teamActivity';
import { crossesDays, localStretch } from '../lib/localTime';

/**
 * TREE-SHAKEN: the custom series the lanes are drawn with, the grid and its time axis, the legend
 * that names each state's look, the zoom - dragged on the axis, wheeled, or brushed with the
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

/** A lane's height: taller than the tile's, the dialog has the room. */
const LaneHeight = 22;

/** The lanes' left edge: room for each member's full name. */
const GridLeft = 128;

/** The lanes' right edge. */
const GridRight = 16;

/** Room above the lanes for the legend and the toolbox. */
const LanesTop = 36;

/** Room between the pointer and the hover box, so the hover line stays in sight beside it. */
const TooltipGap = 16;

/** Axis labels fall on this browser's own timezone. */
const timeZone = Intl.DateTimeFormat().resolvedOptions().timeZone;

const $q = useQuasar();
const dark = computed(() => $q.dark?.isActive === true);

const cardEl = ref<{ $el: HTMLElement } | null>(null);

const activity = ref<TeamActivity | null>(null);
const loading = ref(false);
const failed = ref(false);

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
    held: token('--os-stat-held'),
    blocked: token('--os-stat-blocked'),
    failed: token('--os-stat-failed'),
    idle: token('--os-stat-idle'),
    chrome: token('--os-surface') || token('--os-chrome'),
    ink: token('--os-ink'),
  };
});

/** What the zoom shows now, as times; null shows the whole window. */
const zoom = ref<{ start: number; end: number } | null>(null);

/**
 * THE INSTANT UNDER THE POINTER, from where it is across the lanes and what the zoom shows, as on
 * the tile. An axis tooltip hands its formatter the NEAREST span edge instead, which would name a
 * time the hover line is not on.
 */
const pointerAt = ref<number | null>(null);

/**
 * THE TILE'S OWN WINDOW, READ ON OPENING AND NOTHING ELSE: from the team's earliest workflow root to
 * the latest activity of any workflow. Zooming or the board moving underneath never re-reads.
 */
let readSeq = 0;

async function read() {
  const seq = ++readSeq;

  loading.value = true;
  failed.value = false;
  activity.value = null;
  zoom.value = null;
  pointerAt.value = null;

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

/** The axis's labels are as fine as the window needs: a time of day, or a date. */
const bucket = computed(() => windowBucket(periodFrom.value ?? periodTo.value, periodTo.value));

/** A time of day alone is ambiguous once the window crosses midnight: then every time has its date. */
const withDate = computed(() => periodFrom.value !== null && crossesDays(periodFrom.value, periodTo.value));

/** The subtitle: the stretch the lanes cover, "11:38:02 AM – 3:41:17 PM". */
const subtitle = computed(() =>
  periodFrom.value === null ? 'Member time by state.' : localStretch(periodFrom.value, periodTo.value));

/** Every member with a lane - a member since removed too, marked so, in this dialog only. */
const lanes = computed(() =>
  activity.value ? activityLanes(activity.value, props.containers, periodTo.value, { removed: true }) : []);

/** Something to draw: a member was other than idle at some point in the window. */
const hasChart = computed(() =>
  periodFrom.value !== null && lanes.value.some((lane) => lane.spans.some((span) => span.state !== 'idle')));

/** The whole window, as the axis draws it before any zoom. */
const axisRange = computed(() => {
  const start = periodFrom.value ?? periodTo.value;

  return { start, end: Math.max(periodTo.value, start + 1) };
});

/**
 * KEEPS THE ZOOM'S RANGE AS TIMES, whichever control moved it: the slider and the wheel say percents
 * of the window, the toolbox's brush says times.
 */
function onZoom(event: unknown) {
  type Range = { start?: number; end?: number; startValue?: number; endValue?: number };
  const raised = event as Range & { batch?: Range[] };
  const item = raised.batch?.[0] ?? raised;
  const { start, end } = axisRange.value;

  if (item.startValue !== undefined && item.endValue !== undefined) {
    zoom.value = { start: Number(item.startValue), end: Number(item.endValue) };
  } else if (item.start !== undefined && item.end !== undefined) {
    zoom.value = { start: start + (item.start / 100) * (end - start), end: start + (item.end / 100) * (end - start) };
  }
}

function notePointerX(event: { offsetX?: number }) {
  const width = cardEl.value?.$el.querySelector('.stats-dialog-chart')?.getBoundingClientRect().width ?? 0;
  const plot = width - GridLeft - GridRight;
  const { start, end } = zoom.value ?? axisRange.value;

  pointerAt.value = plot > 0 && event.offsetX !== undefined
    ? start + (Math.min(Math.max(event.offsetX - GridLeft, 0), plot) / plot) * (end - start)
    : null;
}

/** THE HOVER BOX BESIDE THE POINTER, never over it, placed against the screen: it is on the body. */
function besidePointer(point: number[], _params: unknown, _dom: unknown, _rect: unknown, size: { contentSize: number[] }) {
  const chart = cardEl.value?.$el.querySelector('.stats-dialog-chart')?.getBoundingClientRect() ?? { left: 0, top: 0 };

  return tooltipBeside(point, size.contentSize, chart, { width: window.innerWidth, height: window.innerHeight }, TooltipGap);
}

/** ONE SPAN OF A LANE, as on the tile: lane `n` runs from `n` to `n + 1` on its axis. */
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
  const { start, end } = axisRange.value;
  const palette = colours.value;

  return {
    backgroundColor: 'transparent',
    animation: false,
    // A KEY TO THE STATES, not a filter: each lane is its member's whole time.
    legend: {
      data: [...ActivityStates],
      formatter: stateWords,
      selectedMode: false,
      top: 0,
      left: 0,
    },
    toolbox: {
      right: 0,
      top: 0,
      feature: { dataZoom: { xAxisIndex: [0], yAxisIndex: false } },
    },
    grid: { left: GridLeft, right: GridRight, top: LanesTop, height: lanesHeight.value },
    xAxis: {
      type: 'time',
      min: start,
      max: end,
      axisLabel: { hideOverlap: true, formatter: (value: number) => axisTimeLabel(value, bucket.value, timeZone) },
      splitLine: { show: false },
      axisPointer: { snap: false, label: { show: false }, lineStyle: { color: palette.ink, width: 1 } },
    },
    yAxis: {
      type: 'value',
      inverse: true,
      min: 0,
      max: Math.max(lanes.value.length, 1),
      interval: 0.5,
      splitLine: { show: false },
      axisLine: { show: false },
      axisTick: { show: false },
      axisLabel: { fontSize: 11, formatter: laneLabel, width: GridLeft - 12, overflow: 'truncate' },
    },
    dataZoom: [
      { type: 'inside', xAxisIndex: [0], filterMode: 'none' },
      { type: 'slider', xAxisIndex: [0], filterMode: 'none', height: 18, bottom: 4 },
    ],
    // THE TILE'S HOVER: one line across every lane, and the box names the instant under it and
    // what each member was doing then.
    tooltip: {
      trigger: 'axis',
      triggerOn: 'mousemove|click',
      axisPointer: { type: 'line' },
      // OUTSIDE THE CARD, beside the pointer: never over the hover line.
      confine: false,
      appendTo: 'body',
      position: besidePointer,
      transitionDuration: 0,
      className: 'stats-tooltip',
      formatter: (params: unknown) => {
        const first = (Array.isArray(params) ? params[0] : params) as { axisValue?: number | string } | undefined;
        const hovered = pointerAt.value ?? Number(first?.axisValue ?? end);

        return tooltipHtml(lanes.value, hovered, withDate.value);
      },
    },
    series: [
      {
        type: 'custom',
        name: 'lanes',
        renderItem: renderLane,
        encode: { x: [1, 2], y: 0 },
        data: lanes.value.flatMap((lane, index) =>
          lane.spans.map((span) => [index, span.from, span.to, ActivityStates.indexOf(span.state)])),
      },
      // ONE EMPTY SERIES PER STATE, only so the legend has each state's look to show.
      ...ActivityStates.map((state) => ({
        type: 'custom',
        name: state,
        silent: true,
        // HELD IS STRIPED in its legend mark too, as its spans are, so it never depends on hue.
        itemStyle: state === 'held'
          ? { color: palette[state], decal: { symbol: 'rect', dashArrayX: [2, 4], dashArrayY: [1, 0], color: palette.chrome } }
          : { color: palette[state] },
        renderItem: () => ({ type: 'group', children: [] }),
        data: [],
      })),
      {
        // THE WHOLE WINDOW, drawn as nothing, so the hover line has something to stand on.
        type: 'custom',
        name: 'period',
        silent: true,
        renderItem: () => ({ type: 'group', children: [] }),
        encode: { x: [1, 2], y: 0 },
        data: [[0, start, end]],
      },
    ],
  };
});

/** The legend, the lanes, the axis labels under them and the zoom slider. */
const chartHeight = computed(() => `${LanesTop + lanesHeight.value + 64}px`);
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
          @datazoom="onZoom"
          @zr:mousemove="notePointerX"
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
