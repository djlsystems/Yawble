<script setup lang="ts">
import { computed, ref } from 'vue';
import { type CostSeries, agentHours, axisTicks, valueText, weekLabel, weekValue } from '../lib/teamCost';
import { moneyText } from '../lib/outcomes';

/**
 * A TEAM'S EIGHT WEEKS AS STACKED COLUMNS, one series per outcome, drawn as plain SVG at three sizes:
 * the Cost tile's (`tile`), a teams-list row's (`row`) and the Cost dialog's (`large`, with its axis,
 * grid, legend and per-week hover box).
 *
 * THE CHART RULES: series in the fixed order `costSeries` gives - No outcome first, at the
 * bottom of each stack in neutral gray, then each outcome on the categorical palette slot of its
 * GLOBAL rank (`--os-series-*`, which the theme steps for dark mode), so one outcome is one colour on
 * every team; a 2px surface gap between stacked segments;
 * the data end (the top of a stack) rounded, the baseline square; one axis, in money or, without a
 * rate, in hours; a recessive grid; a legend for two or more series. Colour is never alone: the
 * dialog's table carries every figure, and the hover box names each outcome.
 */
const props = withDefaults(defineProps<{
  weeks: string[];
  series: CostSeries[];
  /** Money when the instance has a rate, otherwise agent hours. */
  inMoney: boolean;
  currency: string;
  size?: 'tile' | 'row' | 'large';
}>(), { size: 'large' });

const Gap = 2;

const box = computed(() => {
  if (props.size === 'tile') return { width: 120, height: 32, left: 0, right: 0, top: 2, bottom: 0, radius: 2, bar: 0.7 };
  if (props.size === 'row') return { width: 112, height: 28, left: 0, right: 0, top: 2, bottom: 0, radius: 2, bar: 0.7 };
  return { width: 640, height: 240, left: 64, right: 8, top: 10, bottom: 26, radius: 4, bar: 0.62 };
});

const plot = computed(() => ({
  x: box.value.left,
  y: box.value.top,
  width: box.value.width - box.value.left - box.value.right,
  height: box.value.height - box.value.top - box.value.bottom,
}));

/** Each week's total, the sum of its stacked segments. */
const totals = computed(() => props.weeks.map((_, week) =>
  props.series.reduce((sum, s) => sum + weekValue(s.weekly[week] ?? { cost: null, agentSeconds: 0 }, props.inMoney), 0)));

const ticks = computed(() => axisTicks(Math.max(0, ...totals.value)));
const top = computed(() => ticks.value[ticks.value.length - 1] ?? 1);

const slot = computed(() => plot.value.width / Math.max(props.weeks.length, 1));
const barWidth = computed(() => Math.max(2, slot.value * box.value.bar));

function yOf(value: number): number {
  return plot.value.y + plot.value.height - (value / top.value) * plot.value.height;
}

/** A rectangle with only its top corners rounded: the data end, never the baseline. */
function roundedTop(x: number, y: number, w: number, h: number, r: number): string {
  const radius = Math.max(0, Math.min(r, w / 2, h));
  return `M${x},${y + h}V${y + radius}Q${x},${y} ${x + radius},${y}H${x + w - radius}Q${x + w},${y} ${x + w},${y + radius}V${y + h}Z`;
}

interface Segment { key: string; week: number; colour: string; path: string }

const segments = computed<Segment[]>(() => {
  const drawn: Segment[] = [];

  props.weeks.forEach((_, week) => {
    const x = plot.value.x + week * slot.value + (slot.value - barWidth.value) / 2;
    const parts = props.series
      .map((s) => ({ s, value: weekValue(s.weekly[week] ?? { cost: null, agentSeconds: 0 }, props.inMoney) }))
      .filter((p) => p.value > 0);

    let base = 0;
    parts.forEach((part, index) => {
      const low = yOf(base);
      const high = yOf(base + part.value);
      base += part.value;

      // THE GAP sits between segments: every segment above the first gives up its bottom 2px.
      const bottom = index === 0 ? low : low - Gap;
      const height = Math.max(bottom - high, 1);
      const last = index === parts.length - 1;
      const path = last
        ? roundedTop(x, bottom - height, barWidth.value, height, box.value.radius)
        : `M${x},${bottom - height}h${barWidth.value}v${height}h${-barWidth.value}Z`;

      drawn.push({ key: part.s.key, week, colour: part.s.colour, path });
    });
  });

  return drawn;
});

const large = computed(() => props.size === 'large');

/** The legend, for two or more series. */
const legend = computed(() => large.value && props.series.length >= 2);

const hovered = ref<number | null>(null);

/** The hover box's lines for one week: each outcome's cost and hours, then the week's total. */
const tooltip = computed(() => {
  const week = hovered.value;
  if (week === null) return null;

  const rows = [...props.series].reverse().map((s) => {
    const figures = s.weekly[week];
    return {
      key: s.key,
      name: s.name,
      colour: s.colour,
      cost: figures?.cost ?? null,
      hours: agentHours(figures?.agentSeconds ?? 0),
    };
  });
  const seconds = props.series.reduce((sum, s) => sum + (s.weekly[week]?.agentSeconds ?? 0), 0);
  const cost = props.inMoney ? props.series.reduce((sum, s) => sum + (s.weekly[week]?.cost ?? 0), 0) : null;

  return {
    title: `Week of ${weekLabel(props.weeks[week]!)}`,
    rows,
    total: cost === null ? agentHours(seconds) : `${moneyText(cost, props.currency)} · ${agentHours(seconds)}`,
    left: ((plot.value.x + (week + 0.5) * slot.value) / box.value.width) * 100,
  };
});

/** A compact chart's native hover text per week: its date and total. */
function weekTitle(week: number): string {
  return `Week of ${weekLabel(props.weeks[week]!)}: ${valueText(totals.value[week] ?? 0, props.inMoney, props.currency)}`;
}
</script>

<template>
  <div class="cost-chart" data-cost-chart :data-size="size">
    <div v-if="legend" class="cost-legend" data-cost-legend>
      <span v-for="s in [...series].reverse()" :key="s.key" class="cost-legend-item" :data-cost-legend-item="s.key">
        <span class="cost-swatch" :style="{ background: s.colour }" aria-hidden="true"></span>{{ s.name }}
      </span>
    </div>

    <div class="cost-chart-frame">
      <svg
        :viewBox="`0 0 ${box.width} ${box.height}`"
        :width="large ? '100%' : box.width"
        :height="large ? undefined : box.height"
        class="cost-chart-svg"
        role="img"
        :aria-label="inMoney ? 'Cost by week and outcome' : 'Agent hours by week and outcome'"
      >
        <!-- THE GRID AND THE ONE AXIS, recessive: hairlines in the rule colour, labels muted. -->
        <g v-if="large" class="cost-grid">
          <g v-for="tick in ticks" :key="tick">
            <line :x1="plot.x" :x2="plot.x + plot.width" :y1="yOf(tick)" :y2="yOf(tick)" class="cost-grid-line" />
            <text :x="plot.x - 8" :y="yOf(tick)" dy="0.32em" text-anchor="end" class="cost-axis-text" data-cost-tick>
              {{ valueText(tick, inMoney, currency) }}
            </text>
          </g>
          <text
            v-for="(week, index) in weeks"
            :key="week"
            :x="plot.x + (index + 0.5) * slot"
            :y="box.height - 8"
            text-anchor="middle"
            class="cost-axis-text"
          >{{ weekLabel(week) }}</text>
        </g>
        <line
          v-else
          :x1="0" :x2="box.width" :y1="plot.y + plot.height - 0.5" :y2="plot.y + plot.height - 0.5"
          class="cost-grid-line"
        />

        <path
          v-for="(segment, index) in segments"
          :key="index"
          :d="segment.path"
          :style="{ fill: segment.colour }"
          :data-cost-segment="segment.key"
          :data-week="segment.week"
        />

        <!-- One hover area per week, the slot's full width and the plot's full height. -->
        <rect
          v-for="(week, index) in weeks"
          :key="`hover-${week}`"
          :x="plot.x + index * slot"
          :y="plot.y"
          :width="slot"
          :height="plot.height"
          class="cost-hover"
          :class="{ 'cost-hover--on': large && hovered === index }"
          :data-cost-week="index"
          @mouseenter="hovered = index"
          @mouseleave="hovered = null"
        ><title v-if="!large">{{ weekTitle(index) }}</title></rect>
      </svg>

      <div
        v-if="large && tooltip"
        class="cost-tooltip"
        :class="{ 'cost-tooltip--left': tooltip.left > 60 }"
        :style="{ left: `${tooltip.left}%` }"
        data-cost-tooltip
      >
        <div class="cost-tooltip-title">{{ tooltip.title }}</div>
        <div v-for="row in tooltip.rows" :key="row.key" class="cost-tooltip-row" :data-cost-tooltip-row="row.key">
          <span class="cost-swatch" :style="{ background: row.colour }" aria-hidden="true"></span>
          <span class="cost-tooltip-name">{{ row.name }}</span>
          <span v-if="row.cost !== null" class="mono">{{ moneyText(row.cost, currency) }}</span>
          <span class="mono os-text-muted">{{ row.hours }}</span>
        </div>
        <div class="cost-tooltip-total" data-cost-tooltip-total>Week total <span class="mono">{{ tooltip.total }}</span></div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.cost-chart[data-size='tile'],
.cost-chart[data-size='row'] {
  display: inline-block;
  line-height: 0;
}

.cost-chart-frame {
  position: relative;
}

.cost-chart-svg {
  display: block;
  overflow: visible;
}

.cost-grid-line {
  stroke: var(--os-rule);
  stroke-width: 1;
}

.cost-axis-text {
  fill: var(--os-ink-muted);
  font-size: 0.6875rem;
}

.cost-hover {
  fill: transparent;
}

.cost-hover--on {
  fill: var(--os-ink);
  fill-opacity: 0.05;
}

.cost-legend {
  display: flex;
  flex-wrap: wrap;
  gap: 4px 16px;
  margin-bottom: 8px;
  font-size: 0.75rem;
  color: var(--os-ink);
}

.cost-legend-item {
  display: inline-flex;
  align-items: center;
  gap: 6px;
}

.cost-swatch {
  display: inline-block;
  flex: none;
  width: 10px;
  height: 10px;
  border-radius: 2px;
}

.cost-tooltip {
  position: absolute;
  top: 0;
  z-index: 2;
  min-width: 14rem;
  margin-left: 16px;
  padding: 8px 10px;
  border: 1px solid var(--os-rule-strong);
  border-radius: 6px;
  background: var(--os-surface);
  color: var(--os-ink);
  font-size: 0.75rem;
  pointer-events: none;
  box-shadow: 0 2px 8px rgb(0 0 0 / 0.12);
}

.cost-tooltip--left {
  transform: translateX(calc(-100% - 32px));
}

.cost-tooltip-title {
  font-weight: 600;
  margin-bottom: 4px;
}

.cost-tooltip-row {
  display: flex;
  align-items: center;
  gap: 6px;
}

.cost-tooltip-name {
  flex: 1 1 auto;
}

.cost-tooltip-total {
  display: flex;
  justify-content: space-between;
  gap: 12px;
  margin-top: 4px;
  padding-top: 4px;
  border-top: 1px solid var(--os-rule);
  font-weight: 600;
}
</style>
