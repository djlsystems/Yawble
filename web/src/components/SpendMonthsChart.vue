<script setup lang="ts">
import { computed } from 'vue';
import { axisTicks } from '../lib/teamCost';
import { amountText, monthLabel } from '../lib/spend';

/**
 * ONE SERIES PER MONTH AS COLUMNS, on its own scale: the Spend page draws one of these for Actual Spend
 * in each currency and another for Estimated Execution Cost, side by side and never stacked, never one
 * summed series and never a second axis on one chart. The title names the series, so there is no
 * legend. A month with nothing (no expense, or nothing priced) draws no column; its hover still says so.
 */
const props = defineProps<{
  title: string;
  months: string[];
  /** One per month; null where there is nothing (never drawn as 0). */
  values: (number | null)[];
  currency: string;
  /** The words a month with nothing says. */
  empty: string;
  series?: number;
}>();

const box = { width: 420, height: 180, left: 56, right: 8, top: 10, bottom: 24, radius: 4, bar: 0.6 };
const plot = { x: box.left, y: box.top, width: box.width - box.left - box.right, height: box.height - box.top - box.bottom };

const ticks = computed(() => axisTicks(Math.max(0, ...props.values.map((v) => v ?? 0))));
const top = computed(() => ticks.value[ticks.value.length - 1] || 1);
const slot = computed(() => plot.width / Math.max(props.months.length, 1));
const barWidth = computed(() => Math.max(2, slot.value * box.bar));

function yOf(value: number): number {
  return plot.y + plot.height - (Math.max(0, value) / top.value) * plot.height;
}

function roundedTop(x: number, y: number, w: number, h: number, r: number): string {
  const radius = Math.min(r, w / 2, h);
  return `M${x},${y + h}V${y + radius}Q${x},${y} ${x + radius},${y}H${x + w - radius}Q${x + w},${y} ${x + w},${y + radius}V${y + h}Z`;
}

const bars = computed(() => props.months.map((month, index) => {
  const value = props.values[index] ?? null;
  const x = plot.x + slot.value * index + (slot.value - barWidth.value) / 2;
  const y = value === null ? plot.y + plot.height : yOf(value);
  const h = plot.y + plot.height - y;
  return {
    month,
    label: monthLabel(month),
    path: value !== null && h > 0 ? roundedTop(x, y, barWidth.value, h, box.radius) : null,
    hit: { x: plot.x + slot.value * index, width: slot.value },
    tip: `${monthLabel(month)}: ${value === null ? props.empty : amountText(value, props.currency)}`,
  };
}));
</script>

<template>
  <figure class="spend-chart q-ma-none" :data-spend-chart="title">
    <figcaption class="text-subtitle2 q-mb-xs">{{ title }}</figcaption>
    <svg :viewBox="`0 0 ${box.width} ${box.height}`" role="img" :aria-label="title" class="spend-chart__svg">
      <g class="spend-chart__grid">
        <template v-for="tick in ticks" :key="tick">
          <line :x1="plot.x" :x2="plot.x + plot.width" :y1="yOf(tick)" :y2="yOf(tick)" />
          <text :x="plot.x - 6" :y="yOf(tick) + 4" text-anchor="end">{{ amountText(tick, currency).replace(/\.00$/, '') }}</text>
        </template>
      </g>
      <g v-for="bar in bars" :key="bar.month" :data-spend-month="bar.month">
        <title>{{ bar.tip }}</title>
        <rect class="spend-chart__hit" :x="bar.hit.x" :y="plot.y" :width="bar.hit.width" :height="plot.height" />
        <path v-if="bar.path" :d="bar.path" class="spend-chart__bar" :style="{ fill: `var(--os-series-${series ?? 1})` }" />
        <text class="spend-chart__month" :x="bar.hit.x + bar.hit.width / 2" :y="box.height - 6" text-anchor="middle">{{ bar.label }}</text>
      </g>
    </svg>
  </figure>
</template>

<style scoped>
.spend-chart__svg { width: 100%; height: auto; display: block; }
.spend-chart__grid line { stroke: var(--os-rule); stroke-width: 1; }
.spend-chart__grid text, .spend-chart__month { fill: var(--os-ink-muted); font-size: 0.6875rem; }
.spend-chart__hit { fill: transparent; }
g:hover > .spend-chart__hit { fill: rgba(127, 127, 127, 0.08); }
</style>
