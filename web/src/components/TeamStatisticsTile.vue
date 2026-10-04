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
  activityCaption,
  activityLanes,
  activitySummary,
  liveNow,
  tooltipHtml,
} from '../lib/teamActivity';

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
  /** The board's ticking clock, so open spans grow between reads with no traffic. */
  clock: number;
  /** Milliseconds from this browser's clock to the server's. */
  clockOffset: number;
}>();

/** A lane is never shorter than this, so its initials stay legible. */
const LaneHeight = 14;

/** The tile reads `/activity` at most this often, however fast the board changes. */
const MinReadGap = 1000;

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

const now = computed(() =>
  activity.value ? liveNow(activity.value.serverNow, props.clock, props.clockOffset) : props.clock + props.clockOffset);

const lanes = computed(() => (activity.value ? activityLanes(activity.value, props.containers, now.value) : []));

const from = computed(() => (activity.value?.from ? Date.parse(activity.value.from) : null));

const hasChart = computed(() =>
  activity.value !== null && activity.value.window !== 'none' && from.value !== null && lanes.value.length > 0);

const latestClosedAt = computed(() => {
  const ended = props.workflows?.workflows.find((w) => w.endedAt !== null)?.endedAt;

  return ended ? Date.parse(ended) : null;
});

const caption = computed(() =>
  activity.value
    ? activityCaption(activity.value.window, from.value, props.workflows?.openCount ?? null, latestClosedAt.value)
    : '—');

const summary = computed(() =>
  activity.value ? activitySummary(activity.value.window, lanes.value, from.value, now.value) : 'Statistics');

/**
 * ONE SPAN: a bar inside its lane, idle a pale track. Blocked and failed carry a notch cut from the
 * tile's own colour - one corner for blocked, both left corners for failed - so neither is told by
 * hue alone.
 */
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
  const palette = colours.value;

  const children: CustomSeriesRenderItemReturn[] = [
    { type: 'rect', shape: { x, y, width, height }, style: { fill: palette[state] } },
  ];

  const notch = Math.min(6, height, width);

  if (state === 'blocked' || state === 'failed') {
    children.push({ type: 'polygon', shape: { points: [[x, y], [x + notch, y], [x, y + notch]] }, style: { fill: palette.chrome } });
  }

  if (state === 'failed') {
    children.push({
      type: 'polygon',
      shape: { points: [[x, y + height], [x + notch, y + height], [x, y + height - notch]] },
      style: { fill: palette.chrome },
    });
  }

  return { type: 'group', children } as CustomSeriesRenderItemReturn;
}

const option = computed(() => {
  const start = from.value ?? now.value;
  const end = Math.max(now.value, start + 1);
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
      max: end,
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
      confine: true,
      transitionDuration: 0,
      className: 'stats-tooltip',
      // THE TIME UNDER THE CURSOR comes from the axis; every line under it is escaped text.
      formatter: (params: unknown) => {
        const first = (Array.isArray(params) ? params[0] : params) as { axisValue?: number | string } | undefined;
        const hovered = Number(first?.axisValue ?? now.value);

        return tooltipHtml(lanes.value, hovered);
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
        data: [[0, start, end]],
      },
    ],
  };
});

const chartHeight = computed(() => `${lanes.value.length * LaneHeight}px`);

/**
 * A CLICK ANYWHERE OPENS THE STATISTICS DIALOG, which has not landed yet: until it does this does
 * nothing. On touch, a tap on the lanes shows the tooltip instead, and the label opens the dialog.
 */
let lastTapWasTouch = false;

function notePointer(event: PointerEvent) {
  lastTapWasTouch = event.pointerType === 'touch';
}

function openStatistics() {
  // The dialog is wired here when it exists.
}

function onTileClick() {
  if (lastTapWasTouch) return;

  openStatistics();
}
</script>

<template>
  <div
    class="team-kpi team-kpi--statistics"
    role="group"
    ref="tileEl"
    :aria-label="summary"
    @pointerdown="notePointer"
    @click="onTileClick"
  >
    <button type="button" class="team-kpi-label stats-label" @click.stop="openStatistics">Statistics</button>

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
      />
    </div>

    <div class="stats-caption">{{ caption }}</div>
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

/* THE TOOLTIP is markup ECharts places inside this tile, so it is reached with `:deep`. Classes
   only: the CSP refuses a `style` attribute in that markup. */
.team-kpi--statistics :deep(.stats-tip-time) {
  font-weight: 600;
  margin-bottom: 2px;
}

.team-kpi--statistics :deep(.stats-tip-row) {
  white-space: nowrap;
}

.team-kpi--statistics :deep(.stats-tip-initials) {
  font-weight: 600;
}

.team-kpi--statistics :deep(.stats-chip) {
  display: inline-block;
  width: 9px;
  height: 9px;
  margin-right: 0.4em;
  border-radius: 2px;
}

.team-kpi--statistics :deep(.stats-chip--running) { background: var(--os-stat-running); }
.team-kpi--statistics :deep(.stats-chip--waiting) { background: var(--os-stat-waiting); }
.team-kpi--statistics :deep(.stats-chip--blocked) { background: var(--os-stat-blocked); }
.team-kpi--statistics :deep(.stats-chip--failed) { background: var(--os-stat-failed); }
.team-kpi--statistics :deep(.stats-chip--idle) { background: var(--os-stat-idle); }
.team-kpi--statistics :deep(.stats-chip--none) { border: 1px dashed currentColor; }
</style>
