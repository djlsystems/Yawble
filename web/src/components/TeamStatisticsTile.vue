<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue';
import { useQuasar } from 'quasar';
import { use } from 'echarts/core';
import { CustomChart } from 'echarts/charts';
import { AxisPointerComponent, GridComponent, TooltipComponent } from 'echarts/components';
import { SVGRenderer } from 'echarts/renderers';
import type { CustomSeriesRenderItemAPI, CustomSeriesRenderItemParams, CustomSeriesRenderItemReturn } from 'echarts';
import VChart from 'vue-echarts';
import type { ContainerSnapshot, TeamActivity, TeamWorkflows } from '../api/types';
import { asTeamId } from '../api/types';
import { getTeamActivity } from '../api/client';
import {
  ActivityStates,
  type ActivityRange,
  activityCaption,
  activityLanes,
  activitySummary,
  stateShapes,
  tooltipBeside,
  tooltipHtml,
  windowEnd,
} from '../lib/teamActivity';
import { crossesDays } from '../lib/localTime';
import TeamStatisticsDialog from './TeamStatisticsDialog.vue';

/**
 * TREE-SHAKEN: the custom series the lanes are drawn with, the grid and its time axis, the tooltip
 * and the one vertical hover line. SVG, so the lanes stay sharp at any zoom and nothing is painted
 * into a canvas a screen reader cannot see past.
 */
use([CustomChart, GridComponent, TooltipComponent, AxisPointerComponent, SVGRenderer]);

const props = defineProps<{
  teamId: string;
  /** The board's members, in board order: the lanes follow it, and a change of state refetches. */
  containers: ContainerSnapshot[];
  /** The team's workflow list: the open count for the caption, and a change in it refetches. */
  workflows: TeamWorkflows | null;
  /**
   * THE TEAMS LIST'S CELL: the lanes alone, with no label and no caption under them, and any click
   * or tap opens the Activity dialog (there is no label to tap on a phone).
   */
  compact?: boolean;
  /**
   * THE AXIS TO DRAW ON, when the Teams list shares one across its rows (its relative view): the
   * lanes are still this team's own, only the stretch of time under them is the list's. Absent, the
   * axis is the team's own window.
   */
  range?: ActivityRange | null;
}>();

/** The team's own window, or `null` with no chart, so the Teams list can share one axis across rows. */
const emit = defineEmits<{ 'own-window': [range: ActivityRange | null] }>();

/** A lane is never shorter than this, so its initials stay legible. */
const LaneHeight = 14;

/** The tile reads `/activity` at most this often, however fast the board changes. */
const MinReadGap = 1000;

/** Room between the pointer and the hover box, so the hover line stays in sight beside it. */
const TooltipGap = 12;

const $q = useQuasar();
const dark = computed(() => $q.dark?.isActive === true);

const tileEl = ref<HTMLElement | null>(null);

/**
 * THE COLOURS ARE THE THEME'S TOKENS (`--os-stat-*` in `css/app.scss`), read from the tile as the
 * browser resolves them, so the chart follows the dark theme with the rest of the board. Read again
 * whenever the theme flips.
 */
const colours = computed(() => {
  void dark.value;

  const style = tileEl.value ? getComputedStyle(tileEl.value) : null;
  const token = (name: string) => style?.getPropertyValue(name).trim() ?? '';

  return {
    running: token('--os-stat-running'),
    waiting: token('--os-stat-waiting'),
    held: token('--os-stat-held'),
    blocked: token('--os-stat-blocked'),
    failed: token('--os-stat-failed'),
    idle: token('--os-stat-idle'),
    chrome: token('--os-chrome'),
    ink: token('--os-ink'),
  };
});

const activity = ref<TeamActivity | null>(null);

/**
 * READ ON A CHANGE, NEVER ON A CLOCK. The board's own pushes say when a member started or ended a
 * run, blocked or failed, and when the open-workflow list moved; those are the only times the
 * answer can differ. Several in one second become one read at the end of it.
 */
let lastRead = 0;
let readSeq = 0;
let pending: ReturnType<typeof setTimeout> | null = null;

async function read() {
  lastRead = Date.now();
  const seq = ++readSeq;

  try {
    const answer = await getTeamActivity(asTeamId(props.teamId));

    // A slow answer for a team the board has since left, or older than one already applied, loses.
    if (seq === readSeq) activity.value = answer;
  } catch {
    // The previous answer stays: a lane that blanks on one failed read says nothing true.
  }
}

function scheduleRead() {
  if (pending !== null) return;

  pending = setTimeout(() => {
    pending = null;
    void read();
  }, Math.max(0, lastRead + MinReadGap - Date.now()));
}

function cancelRead() {
  if (pending !== null) clearTimeout(pending);
  pending = null;
}

watch(() => props.teamId, () => {
  cancelRead();
  activity.value = null;
  void read();
}, { immediate: true });

/** What a member's card would show changing: running or not, under which workflow, blocked, failed. */
const memberKey = computed(() => props.containers
  .map((c) => [c.id, c.state, c.currentCorrelation ?? '', c.blocked ?? '', c.failed ?? '', c.needsDecision ?? ''].join('\u0001'))
  .join('\u0002'));

/** The open workflows: a new one or a closed one can move the window's `from`. */
const openKey = computed(() => {
  const list = props.workflows;

  if (!list) return '';

  const open = list.workflows.filter((w) => w.endedAt === null).map((w) => w.correlation);

  return `${list.openCount}|${list.earliestStartedAt ?? ''}|${open.join(',')}`;
});

watch([memberKey, openKey], scheduleRead);

onBeforeUnmount(cancelRead);

/**
 * THE WINDOW THE SERVER GAVE, AND NOTHING ELSE: from its earliest workflow's root to the latest
 * activity of any of them. No clock is read here, so nothing grows between reads; the window moves
 * when a read answers a later end.
 */
const end = computed(() => (activity.value ? windowEnd(activity.value) : 0));

const lanes = computed(() => (activity.value ? activityLanes(activity.value, props.containers, end.value) : []));

const from = computed(() => (activity.value?.from ? Date.parse(activity.value.from) : null));

/** A time of day alone is ambiguous once the window crosses midnight: then every time has its date. */
const withDate = computed(() => from.value !== null && crossesDays(axisFrom.value, axisTo.value));

const hasChart = computed(() =>
  activity.value !== null && activity.value.window !== 'none' && from.value !== null && lanes.value.length > 0);

/** WHERE THE AXIS RUNS: the shared range when the list hands one, else the team's own window. */
const axisFrom = computed(() => props.range?.from ?? from.value ?? end.value);
const axisTo = computed(() => Math.max(props.range?.to ?? end.value, axisFrom.value + 1));

watch(
  () => (hasChart.value && from.value !== null ? `${from.value}|${end.value}` : ''),
  (key) => emit('own-window', key === '' ? null : { from: from.value!, to: end.value }),
  { immediate: true },
);

const caption = computed(() =>
  activity.value
    ? activityCaption(activity.value.window, from.value, activity.value.to ? end.value : null, props.workflows?.openCount ?? null)
    : '—');

const summary = computed(() =>
  activity.value ? activitySummary(activity.value.window, lanes.value, from.value, end.value) : 'Activity');

/**
 * THE HOVER BOX BESIDE THE POINTER, never over it: ECharts asks with the pointer in the chart's
 * pixels; the box is appended to the body, so it is placed against the screen, not the tile.
 */
function besidePointer(point: number[], _params: unknown, _dom: unknown, _rect: unknown, size: { contentSize: number[] }) {
  const chart = tileEl.value?.querySelector('.stats-chart')?.getBoundingClientRect() ?? { left: 0, top: 0 };

  return tooltipBeside(point, size.contentSize, chart, { width: window.innerWidth, height: window.innerHeight }, TooltipGap);
}

/**
 * THE INSTANT UNDER THE POINTER, from where it is across the lanes. An axis tooltip hands its
 * formatter the NEAREST span edge instead, which would name a time the hover line is not on.
 */
const pointerAt = ref<number | null>(null);

function notePointerX(event: { offsetX?: number }) {
  const width = tileEl.value?.querySelector('.stats-chart')?.getBoundingClientRect().width ?? 0;
  const start = axisFrom.value;
  const stop = axisTo.value;

  pointerAt.value = width > 0 && event.offsetX !== undefined
    ? start + (Math.min(Math.max(event.offsetX, 0), width) / width) * (stop - start)
    : null;
}

/** ONE SPAN: a bar inside its lane, idle a pale track, blocked and failed notched (`stateShapes`). */
function renderSpan(params: CustomSeriesRenderItemParams, api: CustomSeriesRenderItemAPI): CustomSeriesRenderItemReturn {
  const lane = Number(api.value(0));
  const start = api.coord([api.value(1), lane]);
  const end = api.coord([api.value(2), lane + 1]);
  const state = ActivityStates[Number(api.value(3))] ?? 'idle';
  const area = params.coordSys as unknown as { x: number; width: number };

  const left = Math.max(start[0]!, area.x);
  const right = Math.min(end[0]!, area.x + area.width);
  const x = left;
  const y = start[1]! + 1;
  const width = Math.max(right - left, 1);
  const height = Math.max(end[1]! - start[1]! - 2, 1);
  const children = stateShapes(x, y, width, height, state, colours.value);

  return { type: 'group', children } as CustomSeriesRenderItemReturn;
}

const option = computed(() => {
  const start = axisFrom.value;
  const stop = axisTo.value;
  const ink = colours.value.ink;

  const data = lanes.value.flatMap((lane, index) =>
    lane.spans.map((span) => [index, span.from, span.to, ActivityStates.indexOf(span.state)]));

  return {
    backgroundColor: 'transparent',
    animation: false,
    grid: { left: 0, right: 0, top: 0, bottom: 0 },
    xAxis: {
      type: 'time',
      min: start,
      max: stop,
      axisLine: { show: false },
      axisTick: { show: false },
      axisLabel: { show: false },
      splitLine: { show: false },
      axisPointer: { snap: false, label: { show: false }, lineStyle: { color: ink, width: 1 } },
    },
    yAxis: { type: 'value', min: 0, max: lanes.value.length, inverse: true, show: false },
    tooltip: {
      trigger: 'axis',
      triggerOn: 'mousemove|click',
      axisPointer: { type: 'line' },
      // OUTSIDE THE TILE, beside the pointer: the box may reach past the tile's edges, never over the line.
      confine: false,
      appendTo: 'body',
      position: besidePointer,
      transitionDuration: 0,
      className: 'stats-tooltip',
      // THE TIME UNDER THE CURSOR comes from the axis; every line under it is escaped text.
      formatter: (params: unknown) => {
        const first = (Array.isArray(params) ? params[0] : params) as { axisValue?: number | string } | undefined;
        const hovered = pointerAt.value ?? Number(first?.axisValue ?? end.value);

        return tooltipHtml(lanes.value, hovered, withDate.value);
      },
    },
    series: [
      {
        type: 'custom',
        renderItem: renderSpan,
        encode: { x: [1, 2], y: 0 },
        data,
      },
      {
        // THE WHOLE WINDOW, drawn as nothing: it gives the hover line something to stand on where
        // no member has a span, so "no runs in this window" can still be read there.
        type: 'custom',
        silent: true,
        renderItem: () => ({ type: 'group', children: [] }),
        encode: { x: [1, 2], y: 0 },
        data: [[0, start, stop]],
      },
    ],
  };
});

const chartHeight = computed(() => `${lanes.value.length * LaneHeight}px`);

/**
 * A CLICK ANYWHERE OPENS THE ACTIVITY DIALOG. On touch, a tap on the lanes shows the tooltip
 * instead, and the label opens the dialog.
 */
let lastTapWasTouch = false;

function notePointer(event: PointerEvent) {
  lastTapWasTouch = event.pointerType === 'touch';
}

const statisticsOpen = ref(false);

function openActivity() {
  statisticsOpen.value = true;
}

function onTileClick() {
  if (lastTapWasTouch && !props.compact) return;

  openActivity();
}
</script>

<template>
  <div
    class="team-kpi team-kpi--statistics"
    :class="{ 'team-kpi--compact': compact }"
    :data-activity-compact="compact ? '' : undefined"
    role="group"
    ref="tileEl"
    :aria-label="summary"
    @pointerdown="notePointer"
    @click="onTileClick"
  >
    <button v-if="!compact" type="button" class="team-kpi-label stats-label" @click.stop="openActivity">Activity</button>

    <div v-if="hasChart" class="stats-body">
      <div class="stats-initials" aria-hidden="true">
        <div
          v-for="lane in lanes"
          :key="lane.member"
          class="stats-lane-initials mono"
          :style="{ height: `${LaneHeight}px`, lineHeight: `${LaneHeight}px` }"
        >{{ lane.initials }}</div>
      </div>
      <v-chart
        class="stats-chart"
        :style="{ height: chartHeight }"
        :option="option"
        :theme="dark ? 'dark' : ''"
        :init-options="{ renderer: 'svg' }"
        autoresize
        @zr:mousemove="notePointerX"
      />
    </div>

    <div v-if="!compact" class="stats-caption">{{ caption }}</div>
    <div v-else-if="!hasChart" class="os-text-muted" data-activity-none>—</div>

    <TeamStatisticsDialog
      v-model="statisticsOpen"
      :team-id="teamId"
      :containers="containers"
    />
  </div>
</template>

<style scoped>
.team-kpi--statistics {
  display: flex;
  flex-direction: column;
  cursor: pointer;
}

/* The strip's own label look: `.team-kpi-label` there is scoped to the strip and does not reach
   inside this component. */
.stats-label {
  align-self: flex-start;
  padding: 0;
  border: 0;
  background: none;
  font-family: inherit;
  font-size: 0.68rem;
  letter-spacing: 0.08em;
  text-transform: uppercase;
  color: var(--os-ink-faint);
  cursor: pointer;
}

.stats-label:focus-visible {
  outline: 2px solid var(--os-ink);
  outline-offset: 2px;
}

/* THE LANES TAKE THE TILE'S WIDTH and grow its height with the team; nothing scrolls. */
.stats-body {
  display: flex;
  align-items: flex-start;
  gap: 6px;
  margin-top: 6px;
}

.stats-initials {
  flex: none;
}

.stats-lane-initials {
  font-size: 10px;
  font-weight: 600;
  color: var(--os-ink-muted);
  text-align: right;
  min-width: 1.6em;
}

.stats-chart {
  flex: 1 1 auto;
  min-width: 0;
}

.stats-caption {
  margin-top: 4px;
  font-size: 0.7rem;
  color: var(--os-ink-muted);
}


/* THE TEAMS LIST'S CELL: the lanes alone, flush with the row, wide enough to read across. */
.team-kpi--compact {
  min-width: 14rem;
}

.team-kpi--compact .stats-body {
  margin-top: 0;
}
</style>
