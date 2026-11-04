<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import type { ContainerSnapshot, TeamTokenTotals } from '../api/types';
import { asTeamId } from '../api/types';
import { getTeamTokens } from '../api/client';
import { getTeamCostPeriod, type TeamCost, type TeamCostPeriod } from '../api/outcomes';
import { moneyText } from '../lib/outcomes';
import {
  EstimateTitle,
  agentHours,
  defaultRateText,
  hoursFigure,
  perMillionText,
} from '../lib/teamCost';
import { useCostPeriodChoice } from '../lib/costPeriodChoice';
import { bucketLabel, rangeOfChoice } from '../lib/teamsPeriod';
import {
  memberRows, outcomeRows, periodName, teamPeriodPriced, teamPeriodSeries, teamPeriodTile, workflowRows,
} from '../lib/teamPeriod';
import CostPeriodControl from './CostPeriodControl.vue';
import { useWorkflowNavStore } from '../stores/workflowNav';
import CostWeeksChart from './CostWeeksChart.vue';
import DialogTabs from './DialogTabs.vue';
import TeamTokensPanel from './TeamTokensPanel.vue';

/**
 * THE ESTIMATED EXECUTION COST DIALOG: a team's Estimated Execution Cost over the period the Teams list and the team
 * page share, with Agent Hours and the per-1M billable tokens figure, its trend on the team's own scale, and breakdowns
 * by outcome, by member (agent and plugin members apart) and by workflow - and, on its second tab, the tokens behind
 * it. Opened from the team page's Cost tile and from the teams list's column.
 *
 * EVERY FIGURE IS `GET /api/teams/{team}/cost/period`'s (`lib/teamPeriod.ts` words them). Each run's part inside the
 * period is priced once there, so each breakdown's rows add up to the headline exactly. Nothing here is an outcome
 * budget: an outcome's stored value is kept and readable on the outcome routes, and no figure is measured against it.
 *
 * It replaces the Tokens dialog: the Tokens tab is that dialog's content, unchanged
 * (`TeamTokensPanel.vue`).
 */
const open = defineModel<boolean>({ required: true });

const props = defineProps<{
  teamId: string;
  teamName: string;
  containers: ContainerSnapshot[];
  /** The eight-week cost route's answer - read here only for its default-rate line - or null while it is read. */
  cost: TeamCost | null;
  /** True when the cost route could not be read. */
  costFailed?: boolean;
  /**
   * The period read's answer, as the team page holds it; `undefined` when the opener has none (the teams list), and
   * the dialog then reads it itself while it is open.
   */
  period?: TeamCostPeriod | null;
  /** True when the opener's period read failed. */
  periodFailed?: boolean;
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

// --- The period, read here only when the opener has none ------------------------------------------

const { choice } = useCostPeriodChoice();
const readPeriod = ref<TeamCostPeriod | null>(null);
const readFailed = ref(false);
let periodSeq = 0;

watch([open, () => props.teamId, choice], async ([showing, team]) => {
  if (!showing || props.period !== undefined) return;
  const seq = ++periodSeq;
  const asked = choice.value;
  readFailed.value = false;
  try {
    const answer = await getTeamCostPeriod(team, rangeOfChoice(asked), asked.bucket);
    if (seq === periodSeq) readPeriod.value = answer;
  } catch {
    if (seq === periodSeq) {
      readPeriod.value = null;
      readFailed.value = true;
    }
  }
}, { immediate: true });

const period = computed(() => (props.period !== undefined ? props.period : readPeriod.value));
const periodFailed = computed(() => (props.period !== undefined ? props.periodFailed ?? false : readFailed.value));

// --- The Estimated Execution Cost tab ---------------------------------------------------------------

const isPriced = computed(() => (period.value ? teamPeriodPriced(period.value) : false));
const currency = computed(() => period.value?.money.currency ?? props.cost?.money.currency ?? 'USD');
const headline = computed(() => (period.value ? teamPeriodTile(period.value) : null));
const series = computed(() => (period.value ? teamPeriodSeries(period.value) : []));
const colourOf = computed(() => new Map(series.value.map((s) => [s.key, s.colour])));
const columnTitle = computed(() => {
  const bucket = period.value?.period.bucket ?? choice.value.bucket;
  return (start: string) => bucketLabel(start, bucket);
});
const periodWords = computed(() => periodName(choice.value));

/** Which breakdown shows under the chart. */
type Breakdown = 'outcome' | 'member' | 'workflow';
const breakdown = ref<Breakdown>('outcome');
watch(open, (showing) => {
  if (showing) breakdown.value = 'outcome';
});

/** By outcome: each outcome in the chart's order, then Unattributed. The Team total follows. */
const byOutcome = computed(() => {
  const rows = period.value ? outcomeRows(period.value) : [];
  return [...rows.filter((r) => r.outcome.id !== null), ...rows.filter((r) => r.outcome.id === null)];
});
const byMember = computed(() => (period.value ? memberRows(period.value) : { agents: [], plugins: [] }));
const byWorkflow = computed(() => (period.value ? workflowRows(period.value) : []));

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
    <q-card class="token-dialog-card cost-dialog-card os-dialog-xl" data-cost-dialog>
      <q-card-section class="q-pb-none cost-dialog-head">
        <div class="os-dialog-title" data-cost-title>{{ EstimateTitle }} · {{ teamName }}</div>
        <div v-if="cost" class="text-caption os-text-muted cost-rate" data-cost-rate>
          {{ defaultRateText(cost.money) }} ·
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
        <q-card-section class="q-pb-none" data-cost-period-row>
          <CostPeriodControl />
        </q-card-section>

        <q-card-section v-if="!period || !headline" class="os-text-muted">
          {{ periodFailed ? 'The Estimated Execution Cost could not be read.' : 'Reading…' }}
        </q-card-section>

        <template v-else>
          <!-- THE HEADLINE FIGURES over the period. Money only when a run was priced: never $0 for unpriced time. -->
          <q-card-section class="cost-figures" data-cost-figures>
            <div class="cost-figure" data-cost-figure="estimate">
              <div class="cost-figure-value mono" data-cost-headline>{{ headline.value }}</div>
              <div class="text-caption os-text-muted">{{ EstimateTitle }} · {{ periodWords }}</div>
              <div v-if="headline.footnote" class="text-caption os-text-muted" data-cost-footnote>{{ headline.footnote }}</div>
            </div>
            <div class="cost-figure" data-cost-figure="agent-hours">
              <div class="cost-figure-value mono">{{ agentHours(period.totals.agentSeconds) }}</div>
              <div class="text-caption os-text-muted">{{ headline.detail }}</div>
            </div>
            <div class="cost-figure" data-cost-figure="per-million">
              <div class="cost-figure-value mono">{{ perMillionText(period.totals, currency) }}</div>
              <div class="text-caption os-text-muted">Per 1M billable tokens</div>
            </div>
          </q-card-section>

          <q-card-section class="q-pt-none">
            <CostWeeksChart
              :weeks="period.buckets"
              :series="series"
              :in-money="isPriced"
              :currency="currency"
              :column-title="columnTitle"
              size="large"
            />
            <div v-if="!isPriced" class="text-caption os-text-muted q-mt-xs" data-cost-no-rate>
              Set an agent hourly rate in Outcomes to see dollars
            </div>
          </q-card-section>

          <!-- THE BREAKDOWNS ARE THE ACCESSIBLE VIEW: every figure the chart draws, in words, three ways. -->
          <q-card-section class="q-pt-none">
            <q-btn-toggle
              v-model="breakdown"
              dense
              no-caps
              unelevated
              toggle-color="primary"
              class="q-mb-sm"
              :options="[
                { label: 'By outcome', value: 'outcome' },
                { label: 'By member', value: 'member' },
                { label: 'By workflow', value: 'workflow' },
              ]"
              data-cost-breakdowns
            />

            <div v-if="breakdown === 'outcome'" class="cost-table-scroll">
            <table class="token-table cost-table" data-cost-table data-cost-breakdown="outcome">
              <thead>
                <tr>
                  <th class="token-name-col">Outcome</th>
                  <th class="token-num">Executions</th>
                  <th class="token-num">Agent Hours</th>
                  <th class="token-num">{{ EstimateTitle }}</th>
                  <th class="token-num">Billable tokens</th>
                  <th class="token-num">$ / 1M tokens</th>
                </tr>
              </thead>
              <tbody>
                <tr v-for="row in byOutcome" :key="row.key" :data-cost-row="row.key">
                  <td class="token-name">
                    <span class="cost-swatch" :style="{ background: colourOf.get(row.key) }" aria-hidden="true"></span>
                    {{ row.name }}
                  </td>
                  <td class="token-num mono" data-cell="executions">{{ row.executions.toLocaleString() }}</td>
                  <td class="token-num mono" data-cell="hours">{{ row.hours }}</td>
                  <td class="token-num mono" data-cell="cost" :title="row.footnote ?? undefined">{{ row.estimate }}</td>
                  <td class="token-num mono" data-cell="billable">{{ row.outcome.figures.billable.toLocaleString() }}</td>
                  <td class="token-num mono" data-cell="per-million">{{ perMillionText(row.outcome.figures, currency) }}</td>
                </tr>
                <tr class="cost-total-row" data-cost-row="total">
                  <td class="token-name">Team total</td>
                  <td class="token-num mono" data-cell="executions">{{ period.totals.executions.toLocaleString() }}</td>
                  <td class="token-num mono" data-cell="hours">{{ hoursFigure(period.totals.agentSeconds) }}</td>
                  <td class="token-num mono" data-cell="cost">{{ headline.value }}</td>
                  <td class="token-num mono" data-cell="billable">{{ period.totals.billable.toLocaleString() }}</td>
                  <td class="token-num mono" data-cell="per-million">{{ perMillionText(period.totals, currency) }}</td>
                </tr>
              </tbody>
            </table>
            </div>

            <template v-else-if="breakdown === 'member'">
              <table class="token-table cost-table" data-cost-breakdown="agent-members">
                <thead>
                  <tr>
                    <th class="token-name-col">Agent member</th>
                    <th class="token-num">Executions</th>
                    <th class="token-num">Agent Hours</th>
                    <th class="token-num">Estimated Agent Cost</th>
                  </tr>
                </thead>
                <tbody>
                  <tr v-for="row in byMember.agents" :key="row.key" :data-cost-member="row.name">
                    <td class="token-name">{{ row.name }}</td>
                    <td class="token-num mono" data-cell="executions">{{ row.executions.toLocaleString() }}</td>
                    <td class="token-num mono" data-cell="hours">{{ row.hours }}</td>
                    <td class="token-num mono" data-cell="cost" :title="row.footnote ?? undefined">{{ row.estimate }}</td>
                  </tr>
                  <tr v-if="byMember.agents.length === 0"><td colspan="4" class="os-text-muted">No agent member ran in this period.</td></tr>
                </tbody>
              </table>
              <table class="token-table cost-table q-mt-md" data-cost-breakdown="plugin-members">
                <thead>
                  <tr>
                    <th class="token-name-col">Plugin member</th>
                    <th class="token-num">Executions</th>
                    <th class="token-num">Estimated Plugin Cost</th>
                  </tr>
                </thead>
                <tbody>
                  <tr v-for="row in byMember.plugins" :key="row.key" :data-cost-member="row.name">
                    <td class="token-name">{{ row.name }}</td>
                    <td class="token-num mono" data-cell="executions">{{ row.executions.toLocaleString() }}</td>
                    <td class="token-num mono" data-cell="cost" :title="row.footnote ?? undefined">{{ row.estimate }}</td>
                  </tr>
                  <tr v-if="byMember.plugins.length === 0"><td colspan="3" class="os-text-muted">No plugin member ran in this period.</td></tr>
                </tbody>
              </table>
              <div class="text-caption os-text-muted q-mt-sm">
                A plugin member's time is never Agent Hours. A plugin member with no rate of its own is not priced.
              </div>
            </template>

            <table v-else class="token-table cost-table" data-cost-breakdown="workflow">
              <thead>
                <tr>
                  <th class="token-name-col">Workflow</th>
                  <th class="token-num">Executions</th>
                  <th class="token-num">Agent Hours</th>
                  <th class="token-num">{{ EstimateTitle }}</th>
                </tr>
              </thead>
              <tbody>
                <tr v-for="row in byWorkflow" :key="row.key" :data-cost-workflow="row.workflow">
                  <td class="token-name">{{ row.name }}</td>
                  <td class="token-num mono" data-cell="executions">{{ row.executions.toLocaleString() }}</td>
                  <td class="token-num mono" data-cell="hours">{{ row.hours }}</td>
                  <td class="token-num mono" data-cell="cost" :title="row.footnote ?? undefined">{{ row.estimate }}</td>
                </tr>
                <tr v-if="byWorkflow.length === 0"><td colspan="4" class="os-text-muted">No workflow ran in this period.</td></tr>
              </tbody>
            </table>

            <div class="text-caption os-text-muted q-mt-sm" data-cost-note>
              The Estimated Execution Cost prices each run at the rate it started with, not money paid; a run crossing the
              period's edge counts only its part inside it. Each breakdown's rows add up to the Team total. The $ / 1M tokens
              figure explains the dollars against the tokens behind them; tokens are not billed here.
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

/* THE OUTCOME BREAKDOWN'S SEVEN COLUMNS scroll sideways in a narrow dialog rather than squeeze its names letter by letter. */
.cost-table-scroll {
  overflow-x: auto;
}

.cost-table .token-name {
  min-width: 14ch;
  overflow-wrap: normal;
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
</style>
