<script setup lang="ts">
import { computed } from 'vue';
import type { CostSummary, TeamsCostPeriod } from '../api/outcomes';
import { bucketAside, periodTile, spendTile, summaryPriced, whereBars } from '../lib/costSummary';
import { bucketLabel } from '../lib/teamsPeriod';
import { EstimateTitle } from '../lib/teamCost';
import CostWeeksChart from './CostWeeksChart.vue';

/**
 * THE INSTANCE AT A GLANCE, above the teams list: what it is estimated to cost (Estimated Execution Cost), where the
 * money goes (Where it goes) and whether anything needs a person (Right now). One row of three, two on a narrower
 * screen, one on a phone. No tile measures an estimate against an outcome's stored value: there are no outcome budgets.
 *
 * Every word is `lib/costSummary.ts`'s; every outcome colour is `outcomeColours`'s. Colour is never
 * alone: names, amounts and ⚠ carry the meaning, and the status colour marks only waiting on you.
 */
const props = withDefaults(defineProps<{
  summary: CostSummary;
  currency: string;
  /**
   * THE TEAMS LIST'S PERIOD (`GET /api/teams/cost/period`): with it, Estimated Execution Cost and Where it goes are
   * the period's; Right now stays the summary's. Without it, all three are the summary's eight weeks.
   */
  period?: TeamsCostPeriod | null;
}>(), { period: null });

const emit = defineEmits<{
  /** The Outcomes dialog, at one outcome or (null) on its list. */
  'open-outcome': [id: string | null];
  /** The Needs-you list. */
  'open-needs-you': [];
}>();

const priced = computed(() => (props.period ? props.period.total.cost !== null : summaryPriced(props.summary)));
const spend = computed(() => (props.period ? periodTile(props.period, props.currency) : spendTile(props.summary, props.currency)));
const spendAside = computed(() => (props.period ? bucketAside(props.period.period.bucket) : '8 wk'));
const columnTitle = computed(() => {
  const bucket = props.period?.period.bucket;
  return bucket ? (start: string) => bucketLabel(start, bucket) : null;
});
const bars = computed(() => whereBars(props.period ?? props.summary, props.currency));
const whereNone = computed(() => (props.period ? 'No work in this period' : 'No work in these weeks'));
const now = computed(() => props.summary.rightNow);
</script>

<template>
  <div class="cost-summary" data-cost-summary>
    <!-- ESTIMATED EXECUTION COST - each run priced at the rate it started with, never money paid: the period's total and
         Agent Hours, its change on the same length of time before, and the period's days, months or weeks as columns. -->
    <button type="button" class="cost-summary-tile" data-summary-tile="spend" @click="emit('open-outcome', null)">
      <span class="cost-summary-head"><span class="cost-summary-title">{{ EstimateTitle }}</span><span class="cost-summary-aside" data-summary-aside>{{ spendAside }}</span></span>
      <span class="cost-summary-value mono" data-summary-value>{{ spend.value }}</span>
      <span v-if="spend.change" class="cost-summary-line" data-summary-change :data-direction="spend.direction">{{ spend.change }}</span>
      <span class="cost-summary-line os-text-muted" data-summary-detail>{{ spend.detail }}</span>
      <span v-if="spend.footnote" class="cost-summary-line os-text-muted" data-summary-footnote>{{ spend.footnote }}</span>
      <CostWeeksChart
        class="cost-summary-chart"
        :weeks="spend.weeks"
        :series="spend.series"
        :in-money="priced"
        :currency="currency"
        :column-title="columnTitle"
        size="tile"
      />
    </button>

    <!-- WHERE IT GOES: the top three outcomes, then Other / no outcome, on one scale. A bar opens its outcome. -->
    <div class="cost-summary-tile cost-summary-tile--static" data-summary-tile="where">
      <span class="cost-summary-head"><span class="cost-summary-title">Where it goes</span><span class="cost-summary-aside">by outcome</span></span>
      <div v-if="bars.length === 0" class="cost-summary-line os-text-muted" data-summary-where-none>{{ whereNone }}</div>
      <button
        v-for="bar in bars"
        :key="bar.key"
        type="button"
        class="cost-summary-where"
        :data-summary-where="bar.key"
        :aria-label="`${bar.name}: ${bar.amount}`"
        @click="emit('open-outcome', bar.outcome)"
      >
        <span class="cost-summary-where-label">
          <span class="cost-summary-where-name">{{ bar.name }}</span>
          <span class="mono" data-summary-where-amount>{{ bar.amount }}</span>
        </span>
        <span class="cost-summary-track" aria-hidden="true">
          <span class="cost-summary-where-fill" :style="{ width: `${bar.fill * 100}%`, background: bar.colour }" data-summary-where-fill></span>
        </span>
      </button>
    </div>

    <!-- RIGHT NOW: open workflows, members running, and what waits on a person, shown even at 0. -->
    <div class="cost-summary-tile cost-summary-tile--static" data-summary-tile="now">
      <span class="cost-summary-head"><span class="cost-summary-title">Right now</span></span>
      <span class="cost-summary-now" data-summary-now="workflows"><span class="mono cost-summary-now-figure">{{ now.openWorkflows }}</span> open workflow{{ now.openWorkflows === 1 ? '' : 's' }}</span>
      <span class="cost-summary-now" data-summary-now="running"><span class="mono cost-summary-now-figure">{{ now.membersRunning }}</span> member{{ now.membersRunning === 1 ? '' : 's' }} running</span>
      <button
        type="button"
        class="cost-summary-now cost-summary-waiting"
        data-summary-now="waiting"
        :data-waiting="now.waitingOnYou > 0 ? 'true' : null"
        @click="emit('open-needs-you')"
      ><span class="mono cost-summary-now-figure">{{ now.waitingOnYou }}</span> ⚠ waiting on you</button>
    </div>
  </div>
</template>

<style scoped>
/* One row of three; two on a narrower screen; one on a phone. */
.cost-summary {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 12px;
  margin-bottom: 12px;
}

@media (max-width: 1099px) {
  .cost-summary {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
}

@media (max-width: 599px) {
  .cost-summary {
    grid-template-columns: minmax(0, 1fr);
  }
}

.cost-summary-tile {
  display: flex;
  flex-direction: column;
  align-items: stretch;
  gap: 4px;
  min-width: 0;
  padding: 10px 12px;
  border: 1px solid var(--os-rule);
  border-radius: 6px;
  background: var(--os-surface);
  color: var(--os-ink);
  font: inherit;
  text-align: left;
}

button.cost-summary-tile {
  cursor: pointer;
}

button.cost-summary-tile:hover {
  border-color: var(--os-rule-strong);
}

.cost-summary-tile:focus-visible,
.cost-summary-where:focus-visible,
.cost-summary-waiting:focus-visible {
  outline: 2px solid var(--os-ink);
  outline-offset: 1px;
}

.cost-summary-head {
  display: flex;
  justify-content: space-between;
  gap: 8px;
}

.cost-summary-title {
  font-weight: 600;
  color: var(--os-ink-muted);
}

.cost-summary-aside {
  font-size: 0.75rem;
  color: var(--os-ink-muted);
}

.cost-summary-value {
  font-size: 1.375rem;
  font-weight: 600;
  line-height: 1.2;
}

.cost-summary-line {
  font-size: 0.75rem;
}

.cost-summary-chart {
  margin-top: 4px;
}

.cost-summary-track {
  position: relative;
  display: block;
  height: 6px;
  border-radius: 3px;
  background: var(--os-rule);
}

.cost-summary-where-fill {
  display: block;
  height: 100%;
  border-radius: 3px;
  background: var(--os-ink-muted);
}

.cost-summary-where {
  display: flex;
  flex-direction: column;
  gap: 2px;
  padding: 2px 0;
  border: 0;
  background: none;
  color: inherit;
  font: inherit;
  text-align: left;
  cursor: pointer;
}

.cost-summary-where-label {
  display: flex;
  justify-content: space-between;
  gap: 8px;
  font-size: 0.75rem;
}

.cost-summary-where-name {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.cost-summary-now {
  font-size: 0.875rem;
}

.cost-summary-now-figure {
  display: inline-block;
  min-width: 1.5rem;
  font-weight: 600;
}

.cost-summary-waiting {
  padding: 0;
  border: 0;
  background: none;
  color: inherit;
  font: inherit;
  font-size: 0.875rem;
  text-align: left;
  cursor: pointer;
}

.cost-summary-waiting[data-waiting='true'] {
  color: var(--os-warn-ink);
  font-weight: 600;
}
</style>
