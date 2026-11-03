<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import type { ContainerSnapshot, TeamTokenTotals } from '../api/types';
import { asTeamId } from '../api/types';
import { getTeamTokens } from '../api/client';
import type { TeamCost, TeamCostOutcome } from '../api/outcomes';
import { moneyText, rateText } from '../lib/outcomes';
import {
  EstimateTitle,
  CriticalColour,
  agentHours,
  budgetUse,
  costSeries,
  hoursFigure,
  overBudget,
  percentText,
  perMillionText,
  priced,
} from '../lib/teamCost';
import { useWorkflowNavStore } from '../stores/workflowNav';
import CostWeeksChart from './CostWeeksChart.vue';
import DialogTabs from './DialogTabs.vue';
import TeamTokensPanel from './TeamTokensPanel.vue';

/**
 * THE COST DIALOG: what a team cost over the last eight weeks, in money by outcome against
 * the budgets set for them, and - on its second tab - the tokens behind it. Opened from the team
 * page's Cost tile and from the teams list's Cost column.
 *
 * It replaces the Tokens dialog: the Tokens tab is that dialog's content, unchanged
 * (`TeamTokensPanel.vue`).
 *
 * A BUDGET IS SHARED: an outcome's budget is measured against its total across EVERY team serving
 * it (`outcomeTotalCost`), so the Budget column reads that total, not this team's part of it.
 */
const open = defineModel<boolean>({ required: true });

const props = defineProps<{
  teamId: string;
  teamName: string;
  containers: ContainerSnapshot[];
  /** The cost route's answer, or null while it is read (or when it could not be). */
  cost: TeamCost | null;
  /** True when the cost route could not be read. */
  costFailed?: boolean;
  /**
   * The team's token totals from the log, as the board holds them; `undefined` when the opener has
   * none (the teams list), and the dialog then reads them itself when the Tokens tab opens.
   */
  usage?: TeamTokenTotals | null;
}>();

type Tab = 'cost' | 'tokens';
const tab = ref<Tab>('cost');

watch(open, (showing) => {
  if (showing) tab.value = 'cost';
});

// --- The Tokens tab's figures, read here only when the opener has none ---------------------------

const readUsage = ref<TeamTokenTotals | null>(null);
let usageFor = '';

watch([open, tab, () => props.teamId], async ([showing, which, team]) => {
  if (!showing || which !== 'tokens' || props.usage !== undefined || usageFor === team) return;
  usageFor = team;
  readUsage.value = null;
  try {
    const usage = await getTeamTokens(asTeamId(team));
    if (usageFor === team) readUsage.value = usage;
  } catch {
    if (usageFor === team) usageFor = '';
  }
});

const usage = computed(() => (props.usage !== undefined ? props.usage : readUsage.value));

// --- The Cost tab ---------------------------------------------------------------------------------

const isPriced = computed(() => (props.cost ? priced(props.cost) : false));
const currency = computed(() => props.cost?.money.currency ?? 'USD');
const series = computed(() => costSeries(props.cost?.outcomes ?? []));
const colourOf = computed(() => new Map(series.value.map((s) => [s.key, s.colour])));
const use = computed(() => (props.cost ? budgetUse(props.cost) : null));

/** The table's rows: each outcome in the chart's order, then No outcome. The Team total follows. */
const rows = computed(() => {
  const outcomes = props.cost?.outcomes ?? [];
  return [...outcomes.filter((o) => o.id !== null), ...outcomes.filter((o) => o.id === null)];
});

const rowKey = (o: TeamCostOutcome) => o.id ?? 'none';

function budgetCell(o: TeamCostOutcome): { text: string; fill: number | null; over: boolean } | null {
  if (o.id === null) return null;
  if (o.value === null) return { text: 'None set', fill: null, over: false };
  const total = o.outcomeTotalCost;
  const over = overBudget(o);
  return {
    text: `${total === null ? '—' : moneyText(total, currency.value)} / ${moneyText(o.value, currency.value)}`,
    fill: total === null ? null : o.value > 0 ? Math.min(1, total / o.value) : 1,
    over,
  };
}

const costCell = (cost: number | null) => (cost === null ? '—' : moneyText(cost, currency.value));

const workflowNav = useWorkflowNavStore();

/** "Change in Outcomes": the Outcomes dialog's rate, opened over the page this dialog leaves. */
function changeRate() {
  open.value = false;
  workflowNav.openOutcomeRate();
}
</script>

<template>
  <q-dialog v-model="open">
    <q-card class="token-dialog-card cost-dialog-card os-dialog-lg" data-cost-dialog>
      <q-card-section class="q-pb-none cost-dialog-head">
        <div class="os-dialog-title" data-cost-title>{{ EstimateTitle }} · {{ teamName }}</div>
        <div v-if="cost" class="text-caption os-text-muted cost-rate" data-cost-rate>
          {{ rateText(cost.money) }} ·
          <a href="#" class="os-name-link" data-cost-rate-change @click.prevent="changeRate">Change in Outcomes</a>
        </div>
      </q-card-section>

      <q-card-section class="q-py-none">
        <DialogTabs v-model="tab" data-cost-tabs>
          <q-tab name="cost" :label="EstimateTitle" data-cost-tab="cost" />
          <q-tab name="tokens" label="Tokens" data-cost-tab="tokens" />
        </DialogTabs>
      </q-card-section>

      <template v-if="tab === 'cost'">
        <q-card-section v-if="!cost" class="os-text-muted">
          {{ costFailed ? 'The cost could not be read.' : 'Reading…' }}
        </q-card-section>

        <template v-else>
          <!-- THE HEADLINE FIGURES. The Estimated Execution Cost is money only when there is a rate: never $0 for unpriced time. -->
          <q-card-section class="cost-figures" data-cost-figures>
            <div class="cost-figure" data-cost-figure="spent">
              <div class="cost-figure-value mono">{{ isPriced ? costCell(cost.totals.cost) : '—' }}</div>
              <div class="text-caption os-text-muted">{{ EstimateTitle }}{{ isPriced ? '' : ' · no rate set' }}</div>
            </div>
            <div class="cost-figure" data-cost-figure="agent-time">
              <div class="cost-figure-value mono">{{ agentHours(cost.totals.agentSeconds) }}</div>
              <div class="text-caption os-text-muted">Agent time</div>
            </div>
            <div v-if="use" class="cost-figure" data-cost-figure="budget-used">
              <div class="cost-figure-value mono" :style="use.over > 0 ? { color: CriticalColour } : undefined">
                {{ percentText(use.share) }}<template v-if="use.over > 0"> ⚠</template>
              </div>
              <div class="text-caption os-text-muted">
                Budget used · {{ moneyText(use.spent, currency) }} of {{ moneyText(use.budget, currency) }}
              </div>
            </div>
            <div class="cost-figure" data-cost-figure="per-million">
              <div class="cost-figure-value mono">{{ perMillionText(cost.totals, currency) }}</div>
              <div class="text-caption os-text-muted">Per 1M billable tokens</div>
            </div>
          </q-card-section>

          <q-card-section class="q-pt-none">
            <CostWeeksChart :weeks="cost.weeks" :series="series" :in-money="isPriced" :currency="currency" size="large" />
            <div v-if="!isPriced" class="text-caption os-text-muted q-mt-xs" data-cost-no-rate>
              Set an agent hourly rate in Outcomes to see dollars
            </div>
          </q-card-section>

          <!-- THE TABLE IS THE ACCESSIBLE VIEW: every figure the chart draws, in words. -->
          <q-card-section class="q-pt-none">
            <table class="token-table cost-table" data-cost-table>
              <thead>
                <tr>
                  <th class="token-name-col">Outcome</th>
                  <th class="token-num">Agent Hours</th>
                  <th class="token-num">{{ EstimateTitle }}</th>
                  <th>Budget</th>
                  <th class="token-num">Billable tokens</th>
                  <th class="token-num">$ / 1M tokens</th>
                </tr>
              </thead>
              <tbody>
                <tr v-for="row in rows" :key="rowKey(row)" :data-cost-row="rowKey(row)">
                  <td class="token-name">
                    <span class="cost-swatch" :style="{ background: colourOf.get(rowKey(row)) }" aria-hidden="true"></span>
                    {{ row.name }}
                  </td>
                  <td class="token-num mono" data-cell="hours">{{ hoursFigure(row.totals.agentSeconds) }}</td>
                  <td class="token-num mono" data-cell="cost">{{ costCell(row.totals.cost) }}</td>
                  <td class="cost-budget" data-cell="budget">
                    <template v-if="budgetCell(row)">
                      <div v-if="budgetCell(row)!.fill !== null" class="cost-mini-bar" aria-hidden="true">
                        <div
                          class="cost-mini-bar__fill"
                          :class="{ 'cost-mini-bar__fill--over': budgetCell(row)!.over }"
                          :style="{ width: `${budgetCell(row)!.fill! * 100}%` }"
                        />
                      </div>
                      <span class="mono" :class="{ 'os-text-muted': budgetCell(row)!.fill === null }">{{ budgetCell(row)!.text }}</span>
                      <span v-if="budgetCell(row)!.over" class="cost-over" data-cost-over> ⚠ over</span>
                    </template>
                    <span v-else class="os-text-muted">—</span>
                  </td>
                  <td class="token-num mono" data-cell="billable">{{ row.totals.billable.toLocaleString() }}</td>
                  <td class="token-num mono" data-cell="per-million">{{ perMillionText(row.totals, currency) }}</td>
                </tr>
                <tr class="cost-total-row" data-cost-row="total">
                  <td class="token-name">Team total</td>
                  <td class="token-num mono" data-cell="hours">{{ hoursFigure(cost.totals.agentSeconds) }}</td>
                  <td class="token-num mono" data-cell="cost">{{ costCell(cost.totals.cost) }}</td>
                  <td data-cell="budget"></td>
                  <td class="token-num mono" data-cell="billable">{{ cost.totals.billable.toLocaleString() }}</td>
                  <td class="token-num mono" data-cell="per-million">{{ perMillionText(cost.totals, currency) }}</td>
                </tr>
              </tbody>
            </table>
            <div class="text-caption os-text-muted q-mt-sm" data-cost-note>
              The Estimated Execution Cost is agent time at the current rate, not money paid. The $ / 1M
              tokens figure explains the dollars against the tokens behind them; tokens are not billed here.
            </div>
          </q-card-section>
        </template>
      </template>

      <TeamTokensPanel v-else :team-id="teamId" :containers="containers" :usage="usage" />

      <q-card-actions align="right">
        <q-btn flat no-caps label="Close" @click="open = false" />
      </q-card-actions>
    </q-card>
  </q-dialog>
</template>

<style scoped>
.cost-dialog-head {
  display: flex;
  flex-wrap: wrap;
  align-items: baseline;
  justify-content: space-between;
  gap: 4px 16px;
}

.cost-figures {
  display: flex;
  flex-wrap: wrap;
  gap: 12px 32px;
}

.cost-figure-value {
  font-size: 1.5rem;
  font-weight: 600;
  line-height: 1.1;
}

.token-table {
  width: 100%;
  border-collapse: collapse;
  font-variant-numeric: tabular-nums;
}

.token-table th {
  padding: 4px 8px;
  font-size: 0.6875rem;
  font-weight: 600;
  text-align: left;
  opacity: 0.7;
  border-bottom: 1px solid var(--os-rule-strong);
  white-space: nowrap;
}

.token-table td {
  padding: 6px 8px;
  border-bottom: 1px solid var(--os-rule);
  vertical-align: top;
}

.token-table .token-num {
  text-align: right;
  white-space: nowrap;
}

.token-name-col {
  width: 100%;
}

.token-name {
  overflow-wrap: anywhere;
  min-width: 10ch;
}

.cost-total-row td {
  border-top: 1px solid var(--os-rule-strong);
  border-bottom: none;
  font-weight: 600;
}

.cost-swatch {
  display: inline-block;
  width: 10px;
  height: 10px;
  margin-right: 6px;
  border-radius: 2px;
  vertical-align: baseline;
}

.cost-budget {
  white-space: nowrap;
}

.cost-mini-bar {
  width: 4rem;
  height: 4px;
  margin-bottom: 3px;
  border-radius: 2px;
  overflow: hidden;
  background: var(--os-rule);
}

.cost-mini-bar__fill {
  height: 100%;
  background: var(--os-ink-muted);
}

.cost-mini-bar__fill--over {
  background: var(--os-error);
}

.cost-over {
  color: var(--os-error);
  font-weight: 600;
}
</style>
