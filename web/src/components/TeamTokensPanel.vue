<script setup lang="ts">
import { computed } from 'vue';
import type { ContainerSnapshot, TeamTokenTotals } from '../api/types';
import { tokenUsageFromTotals } from '../lib/teamKpis';
import { vResizableColumns } from '../lib/resizableColumns';
import TeamTokensChart from './TeamTokensChart.vue';

/**
 * THE COST DIALOG'S TOKENS TAB: what the Tokens dialog showed before the tile became Cost,
 * MOVED HERE UNCHANGED - the ledger's runs chart with its metric switch, then the team total from
 * the message log with its in / cache read / cache write / out breakout, by brand and by member.
 *
 * Token totals come from the log, not from the member snapshots and not from the feed slice.
 */
const props = defineProps<{
  teamId: string;
  containers: ContainerSnapshot[];
  /** Log projection for this team, or null until it lands. */
  usage: TeamTokenTotals | null;
}>();

const tokens = computed(() => tokenUsageFromTotals(props.usage));

/** A team whose every member is a plugin runs no model: it uses no tokens. An empty roster is not plugin-only. */
const pluginOnly = computed(
  () => props.containers.length > 0 && props.containers.every((member) => member.kind === 'plugin'),
);

const formatToken = (value: number | null) => (value === null ? '(unknown)' : value.toLocaleString());
</script>

<template>
  <div class="tokens-panel" data-tokens-panel>
    <q-card-section v-if="tokens.available" class="q-pb-none">
      <div class="text-caption">Team total from the message log.</div>
    </q-card-section>

    <!-- WHEN THE TEAM SPENT, AND WHO: the ledger's runs at the time each ended, above the totals,
         which stay as they were. -->
    <q-card-section v-if="!pluginOnly">
      <TeamTokensChart
        :team-id="teamId"
        :containers="containers"
        :log-billable="tokens.available ? tokens.billable : null"
      />
    </q-card-section>

    <q-card-section v-if="tokens.available">
      <!-- THE TEAM TOTAL: billable as the headline, the four figures it is weighted from as
           labelled values beside it, never one run-on line of numbers. -->
      <div class="token-summary" data-token-summary>
        <div class="token-summary-total">
          <div class="token-summary-value mono">{{ tokens.billable.toLocaleString() }}</div>
          <div class="text-caption os-text-muted">billable tokens</div>
        </div>
        <div class="token-summary-parts">
          <div class="token-summary-part" data-token-part="in">
            <div class="mono">{{ tokens.input.toLocaleString() }}</div>
            <div class="text-caption os-text-muted">in</div>
          </div>
          <div class="token-summary-part" data-token-part="cache-read">
            <div class="mono">{{ tokens.cachedIn.toLocaleString() }}</div>
            <div class="text-caption os-text-muted">cache read</div>
          </div>
          <div class="token-summary-part" data-token-part="cache-write">
            <div class="mono">{{ tokens.cacheCreation.toLocaleString() }}</div>
            <div class="text-caption os-text-muted">cache write</div>
          </div>
          <div class="token-summary-part" data-token-part="out">
            <div class="mono">{{ tokens.output.toLocaleString() }}</div>
            <div class="text-caption os-text-muted">out</div>
          </div>
        </div>
      </div>
      <div class="text-caption os-text-muted q-mt-sm">
        Billable weights a cache read at 1/10 and a cache write at 5/4, and counts a combined
        total as reported.
      </div>

      <!-- ONE COLUMN PER FIGURE, right-aligned so the digits line up, and the name in a column
           of its own that WRAPS rather than truncates: "De...416" named nobody. A row's note
           ("this Agent reports no usage") REPLACES its figures, spanning their columns - the
           decision is lib/teamKpis.ts's, as before. -->
      <div class="token-section-title">By agent brand</div>
      <div v-if="tokens.byBrand.length === 0" class="text-caption os-text-muted">
        No brand totals.
      </div>
      <table v-else v-resizable-columns="'tokens-by-brand'" class="token-table" data-token-table="brand">
        <thead>
          <tr>
            <th class="token-name-col">Brand</th>
            <th class="token-num">Billable</th>
            <th class="token-num">In</th>
            <th class="token-num">Cache read</th>
            <th class="token-num">Cache write</th>
            <th class="token-num">Out</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="row in tokens.byBrand" :key="row.brand" :data-token-row="`brand:${row.brand}`">
            <td class="token-name">{{ row.brand }}</td>
            <td v-if="row.note" colspan="5" class="token-note">{{ row.note }}</td>
            <template v-else>
              <td class="token-num mono token-billable" data-cell="billable">{{ formatToken(row.billable) }}</td>
              <td class="token-num mono" data-cell="in">{{ formatToken(row.input) }}</td>
              <td class="token-num mono" data-cell="cache-read">{{ formatToken(row.cachedIn) }}</td>
              <td class="token-num mono" data-cell="cache-write">{{ formatToken(row.cacheCreation) }}</td>
              <td class="token-num mono" data-cell="out">{{ formatToken(row.output) }}</td>
            </template>
          </tr>
        </tbody>
      </table>

      <div class="token-section-title">By member</div>
      <div v-if="tokens.byMember.length === 0" class="text-caption os-text-muted">
        No member totals.
      </div>
      <table v-else v-resizable-columns="'tokens-by-member'" class="token-table" data-token-table="member">
        <thead>
          <tr>
            <th class="token-name-col">Member</th>
            <th class="token-num">Billable</th>
            <th class="token-num">In</th>
            <th class="token-num">Cache read</th>
            <th class="token-num">Cache write</th>
            <th class="token-num">Out</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="row in tokens.byMember" :key="row.member" :data-token-row="`member:${row.member}`">
            <td class="token-name">
              <div>{{ row.member }}</div>
              <div class="text-caption os-text-muted">{{ row.brand }}</div>
            </td>
            <td v-if="row.note" colspan="5" class="token-note">{{ row.note }}</td>
            <template v-else>
              <td class="token-num mono token-billable" data-cell="billable">{{ formatToken(row.billable) }}</td>
              <td class="token-num mono" data-cell="in">{{ formatToken(row.input) }}</td>
              <td class="token-num mono" data-cell="cache-read">{{ formatToken(row.cachedIn) }}</td>
              <td class="token-num mono" data-cell="cache-write">{{ formatToken(row.cacheCreation) }}</td>
              <td class="token-num mono" data-cell="out">{{ formatToken(row.output) }}</td>
            </template>
          </tr>
        </tbody>
      </table>
    </q-card-section>

    <q-card-section v-else-if="pluginOnly" class="os-body os-text-muted team-kpi-plugin-only">
      Every member of this team is a plugin. Plugins run no model, so this team uses no tokens.
    </q-card-section>

    <q-card-section v-else>
      <q-banner dense class="os-bg-quiet os-text-muted">
        <template #avatar><q-icon name="block" /></template>
        Unavailable. {{ tokens.missing }}.
      </q-banner>
      <div class="text-body2 q-mt-md">
        This card only shows a team total summed from tokensIn and tokensOut on
        agentContainer.completed and agentContainer.failed rows in the message log. It does not sum
        the twenty messages a card still holds — that slice would count down as the team
        got busier. A guessed count would look like a real one.
      </div>
      <div class="os-body os-text-muted q-mt-md">
        Configure usage capture in the Agents screen.
      </div>
    </q-card-section>
  </div>
</template>

<style scoped>
/* THE TOKENS DIALOG. The headline total with its four parts, then two tables whose figures line
   up in right-aligned columns and whose names wrap instead of being cut. */
.token-summary {
  display: flex;
  flex-wrap: wrap;
  align-items: flex-end;
  gap: 16px 32px;
}

.token-summary-value {
  font-size: 1.75rem;
  font-weight: 600;
  line-height: 1.1;
}

.token-summary-parts {
  display: flex;
  flex-wrap: wrap;
  gap: 8px 24px;
  font-variant-numeric: tabular-nums;
}

.token-section-title {
  margin: 20px 0 6px;
  font-size: 0.75rem;
  font-weight: 600;
  letter-spacing: 0.04em;
  text-transform: uppercase;
  opacity: 0.7;
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

.token-table tbody tr:last-child td {
  border-bottom: none;
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

.token-billable {
  font-weight: 600;
}

.token-note {
  font-size: 0.75rem;
  opacity: 0.7;
}
</style>
