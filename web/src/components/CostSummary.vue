<script setup lang="ts">
import { computed } from 'vue';
import type { CostSummary } from '../api/outcomes';
import { budgetsTile, spendTile, summaryPriced, whereBars } from '../lib/costSummary';
import { EstimateTitle } from '../lib/teamCost';
import CostWeeksChart from './CostWeeksChart.vue';

/**
 * THE INSTANCE AT A GLANCE, above the teams list: what it is estimated to cost (Estimated Execution Cost), whether it is inside
 * budget (Budgets), where the money goes (Where it goes) and whether anything needs a person
 * (Right now). One row of four, two on a narrower screen, one on a phone.
 *
 * Every word is `lib/costSummary.ts`'s; every outcome colour is `outcomeColours`'s. Colour is never
 * alone: names, amounts and ⚠ carry the meaning, and status colours mark only over budget and
 * waiting on you.
 */
const props = defineProps<{
  summary: CostSummary;
  currency: string;
}>();

const emit = defineEmits<{
  /** The Outcomes dialog, at one outcome or (null) on its list. */
  'open-outcome': [id: string | null];
  /** The Needs-you list. */
  'open-needs-you': [];
}>();

const priced = computed(() => summaryPriced(props.summary));
const spend = computed(() => spendTile(props.summary, props.currency));
const budgets = computed(() => budgetsTile(props.summary, props.currency));
const bars = computed(() => whereBars(props.summary, props.currency));
const now = computed(() => props.summary.rightNow);
</script>

<template>
  <div class="cost-summary" data-cost-summary>
    <!-- ESTIMATED EXECUTION COST - each run priced at the rate it started with, never money paid: the period's total, its change on the eight weeks before, and the weeks as columns. -->
    <button type="button" class="cost-summary-tile" data-summary-tile="spend" @click="emit('open-outcome', null)">
      <span class="cost-summary-head"><span class="cost-summary-title">{{ EstimateTitle }}</span><span class="cost-summary-aside">8 wk</span></span>
      <span class="cost-summary-value mono" data-summary-value>{{ spend.value }}</span>
      <span v-if="spend.change" class="cost-summary-line" data-summary-change :data-direction="spend.direction">{{ spend.change }}</span>
      <span class="cost-summary-line os-text-muted" data-summary-detail>{{ spend.detail }}</span>
      <CostWeeksChart
        class="cost-summary-chart"
        :weeks="spend.weeks"
        :series="spend.series"
        :in-money="priced"
        :currency="currency"
        size="tile"
      />
    </button>

    <!-- BUDGETS: spent against budget over every budgeted outcome, each outcome's spend its total
         across teams. -->
    <button
      type="button"
      class="cost-summary-tile"
      data-summary-tile="budgets"
      :data-state="budgets.state"
      @click="emit('open-outcome', null)"
    >
      <span class="cost-summary-head"><span class="cost-summary-title">Budgets</span><span class="cost-summary-aside" data-summary-count>{{ budgets.count }}</span></span>
      <span class="cost-summary-value mono" data-summary-value>{{ budgets.value }}</span>
      <span v-if="budgets.line" class="cost-summary-line" data-summary-budget-line>{{ budgets.line }}</span>
      <span v-if="budgets.bar !== null" class="cost-summary-bar" aria-hidden="true">
        <span class="cost-summary-bar-fill" :data-over="budgets.state === 'over' ? 'true' : null" :style="{ width: `${budgets.bar * 100}%` }"></span>
      </span>
      <span v-if="budgets.status || budgets.unbudgeted" class="cost-summary-line">
        <span v-if="budgets.status" class="cost-summary-status" :data-over="budgets.state === 'over' ? 'true' : null" data-summary-status>{{ budgets.status }}</span>
        <!-- The space before the separator is part of the expression: template whitespace is condensed away. -->
        <span v-if="budgets.unbudgeted" class="os-text-muted" data-summary-unbudgeted>{{ budgets.status ? ` ${budgets.unbudgeted}` : budgets.unbudgeted }}</span>
      </span>
      <span v-if="budgets.note" class="cost-summary-line os-text-muted" data-summary-note>{{ budgets.note }}</span>
    </button>

    <!-- WHERE IT GOES: the top three outcomes, then Other / no outcome, on one scale; a tick at a
         budget, ⚠ after an outcome over it. A bar opens its outcome. -->
    <div class="cost-summary-tile cost-summary-tile--static" data-summary-tile="where">
      <span class="cost-summary-head"><span class="cost-summary-title">Where it goes</span><span class="cost-summary-aside">by outcome</span></span>
      <div v-if="bars.length === 0" class="cost-summary-line os-text-muted" data-summary-where-none>No work in these weeks</div>
      <button
        v-for="bar in bars"
        :key="bar.key"
        type="button"
        class="cost-summary-where"
        :data-summary-where="bar.key"
        :aria-label="`${bar.name}: ${bar.amount}${bar.over ? ', over budget' : ''}`"
        @click="emit('open-outcome', bar.outcome)"
      >
        <span class="cost-summary-where-label">
          <span class="cost-summary-where-name">{{ bar.name }}<span v-if="bar.over" class="cost-summary-over" data-summary-where-over> ⚠</span></span>
          <span class="mono" data-summary-where-amount>{{ bar.amount }}</span>
        </span>
        <span class="cost-summary-track" aria-hidden="true">
          <span class="cost-summary-where-fill" :style="{ width: `${bar.fill * 100}%`, background: bar.colour }" data-summary-where-fill></span>
          <span v-if="bar.tick !== null" class="cost-summary-tick" :style="{ left: `${bar.tick * 100}%` }" data-summary-where-tick></span>
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
/* One row of four; two on a narrower screen; one on a phone. */
.cost-summary {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
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

.cost-summary-bar,
.cost-summary-track {
  position: relative;
  display: block;
  height: 6px;
  border-radius: 3px;
  background: var(--os-rule);
}

.cost-summary-bar-fill,
.cost-summary-where-fill {
  display: block;
  height: 100%;
  border-radius: 3px;
  background: var(--os-ink-muted);
}

/* The critical colour, for over budget only. */
.cost-summary-bar-fill[data-over='true'] {
  background: var(--os-error);
}

.cost-summary-status[data-over='true'],
.cost-summary-over {
  color: var(--os-error);
  font-weight: 600;
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

.cost-summary-tick {
  position: absolute;
  top: -2px;
  bottom: -2px;
  width: 2px;
  margin-left: -1px;
  background: var(--os-ink);
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
