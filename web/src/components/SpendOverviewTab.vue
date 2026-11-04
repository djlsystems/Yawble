<script setup lang="ts">
import { computed } from 'vue';
import type { SpendOverview } from '../api/spend';
import { legacyRateNote, markedMoney } from '../lib/teamCost';
import {
  ActualSpendTitle, BillingTypeWords, EdgeRule, EstimateTileTitle, FixedVariableTitle, NoActualSpend, NoEstimate,
  NoProjected, ProjectedTitle, SpendSentence, agentHoursText, amountText, estimateText, perCurrency, servicePeriodText,
} from '../lib/spend';
import { EstimateTitle } from '../lib/teamCost';
import SpendMonthsChart from './SpendMonthsChart.vue';

/**
 * THE SPEND PAGE'S OVERVIEW: four tiles - Actual Spend, Projected, fixed vs variable, and Estimated
 * Execution Cost · Agent Hours - with the sentence under them, a breakdown by account and by vendor, and
 * the monthly trend as separate charts. Each currency is its own figure: nothing is converted or added
 * across currencies, a projection is never in Actual Spend, and an empty Actual Spend says so rather than
 * show an estimate.
 */
const props = defineProps<{ overview: SpendOverview }>();

const actual = computed(() => perCurrency(props.overview.actual));
const projected = computed(() => perCurrency(props.overview.projected));

const groups = computed(() => (['fixed', 'variable'] as const).map((group) => ({
  group,
  label: group === 'fixed' ? 'Fixed subscriptions' : 'Variable',
  lines: props.overview.billingTypes.filter((t) => t.group === group),
})));

const estimate = computed(() => props.overview.estimate);
const estimateMoney = computed(() =>
  estimate.value.cost === null ? null : markedMoney(estimateText(estimate.value), estimate.value.legacyRates));
const footnote = computed(() => legacyRateNote(estimate.value.legacyRates));

const months = computed(() => props.overview.months.map((m) => m.monthStart));

/** One Actual Spend chart per currency seen in the trend; each its own scale. */
const actualCharts = computed(() => {
  const currencies = [...new Set(props.overview.months.flatMap((m) => m.actual.map((a) => a.currency)))].sort();
  return currencies.map((currency) => ({
    currency,
    title: `${ActualSpendTitle} per month (${currency})`,
    values: props.overview.months.map((m) => m.actual.find((a) => a.currency === currency)?.amount ?? null),
  }));
});

const estimateChart = computed(() => ({
  title: `${EstimateTitle} per month (${estimate.value.currency})`,
  values: props.overview.months.map((m) => m.estimate.cost),
}));

function money(value: number | null, currency: string): string {
  return value === null ? '—' : amountText(value, currency);
}
</script>

<template>
  <div data-spend-overview>
    <div class="row q-col-gutter-md">
      <div class="col-12 col-md-3">
        <q-card flat bordered class="full-height" data-spend-tile="actual">
          <q-card-section>
            <div class="text-overline">{{ ActualSpendTitle }}</div>
            <template v-if="actual.length">
              <div v-for="line in actual" :key="line" class="text-h6" data-spend-actual>{{ line }}</div>
              <div class="text-caption os-text-muted">Confirmed expenses</div>
            </template>
            <div v-else class="text-body2 os-text-muted" data-spend-actual-none>{{ NoActualSpend }}</div>
          </q-card-section>
        </q-card>
      </div>

      <div class="col-12 col-md-3">
        <q-card flat bordered class="full-height" data-spend-tile="projected">
          <q-card-section>
            <div class="text-overline">{{ ProjectedTitle }}</div>
            <template v-if="projected.length">
              <div v-for="line in projected" :key="line" class="text-h6" data-spend-projected>{{ line }} <span class="text-caption">projected</span></div>
              <div class="text-caption os-text-muted">Recurring charges not yet invoiced; not in {{ ActualSpendTitle }}</div>
            </template>
            <div v-else class="text-body2 os-text-muted">{{ NoProjected }}</div>
          </q-card-section>
        </q-card>
      </div>

      <div class="col-12 col-md-3">
        <q-card flat bordered class="full-height" data-spend-tile="types">
          <q-card-section>
            <div class="text-overline">{{ FixedVariableTitle }}</div>
            <div v-for="g in groups" :key="g.group" class="q-mb-xs" :data-spend-group="g.group">
              <div class="text-caption text-weight-medium">{{ g.label }}</div>
              <div v-if="!g.lines.length" class="text-caption os-text-muted">None</div>
              <div v-for="line in g.lines" :key="`${line.billingType}-${line.currency}`" class="text-body2">
                {{ BillingTypeWords[line.billingType] ?? line.billingType }}:
                {{ line.actual === null ? 'no confirmed expenses' : amountText(line.actual, line.currency) }}<template v-if="line.projected !== null">
                  · {{ amountText(line.projected, line.currency) }} projected</template>
              </div>
            </div>
          </q-card-section>
        </q-card>
      </div>

      <div class="col-12 col-md-3">
        <q-card flat bordered class="full-height" data-spend-tile="estimate">
          <q-card-section>
            <div class="text-overline">{{ EstimateTileTitle }}</div>
            <div v-if="estimateMoney" class="text-h6" data-spend-estimate>{{ estimateMoney }}</div>
            <div v-else class="text-body2 os-text-muted" data-spend-estimate-none>{{ NoEstimate }}</div>
            <div class="text-body2" data-spend-hours>{{ agentHoursText(estimate.agentSeconds) }}</div>
            <div v-if="footnote" class="text-caption os-text-muted">* {{ footnote }}</div>
          </q-card-section>
        </q-card>
      </div>
    </div>

    <p class="text-body2 q-mt-md q-mb-xs" data-spend-sentence>{{ SpendSentence }}</p>
    <p class="text-caption os-text-muted q-mb-md" data-spend-edge-rule>{{ EdgeRule }} Currencies are shown apart and never converted.</p>

    <div class="row q-col-gutter-md q-mb-md" data-spend-trend>
      <div v-for="chart in actualCharts" :key="chart.currency" class="col-12 col-md">
        <SpendMonthsChart :title="chart.title" :months="months" :values="chart.values" :currency="chart.currency" :empty="'no confirmed expenses'" :series="1" />
      </div>
      <div v-if="!actualCharts.length" class="col-12 col-md">
        <div class="text-subtitle2 q-mb-xs">{{ ActualSpendTitle }} per month</div>
        <div class="text-body2 os-text-muted" data-spend-trend-none>{{ NoActualSpend }} in these months.</div>
      </div>
      <div class="col-12 col-md">
        <SpendMonthsChart :title="estimateChart.title" :months="months" :values="estimateChart.values" :currency="estimate.currency" :empty="'nothing priced'" :series="2" />
      </div>
    </div>

    <div class="row q-col-gutter-md">
      <div class="col-12 col-md-7">
        <div class="text-subtitle2 q-mb-xs">By account</div>
        <q-markup-table flat bordered dense data-spend-by-account>
          <thead>
            <tr><th class="text-left">Account</th><th class="text-left">Vendor</th><th class="text-left">Type</th><th class="text-right">{{ ActualSpendTitle }}</th><th class="text-right">{{ ProjectedTitle }}</th></tr>
          </thead>
          <tbody>
            <tr v-if="!overview.accounts.length"><td colspan="5" class="os-text-muted">No billing accounts.</td></tr>
            <tr v-for="line in overview.accounts" :key="line.accountId" :data-spend-account="line.accountId">
              <td>{{ line.name }}<q-badge v-if="!line.active" outline class="q-ml-xs" label="Inactive" /></td>
              <td>{{ line.vendor }}</td>
              <td>{{ BillingTypeWords[line.billingType] ?? line.billingType }}</td>
              <td class="text-right">{{ money(line.actual, line.currency) }}</td>
              <td class="text-right">{{ line.projected === null ? '—' : `${amountText(line.projected, line.currency)} projected` }}</td>
            </tr>
          </tbody>
        </q-markup-table>
      </div>
      <div class="col-12 col-md-5">
        <div class="text-subtitle2 q-mb-xs">By vendor</div>
        <q-markup-table flat bordered dense data-spend-by-vendor>
          <thead>
            <tr><th class="text-left">Vendor</th><th class="text-left">Currency</th><th class="text-right">{{ ActualSpendTitle }}</th><th class="text-right">{{ ProjectedTitle }}</th></tr>
          </thead>
          <tbody>
            <tr v-if="!overview.vendors.length"><td colspan="4" class="os-text-muted">No vendors.</td></tr>
            <tr v-for="line in overview.vendors" :key="`${line.vendor}-${line.currency}`">
              <td>{{ line.vendor }}</td>
              <td>{{ line.currency }}</td>
              <td class="text-right">{{ money(line.actual, line.currency) }}</td>
              <td class="text-right">{{ line.projected === null ? '—' : `${amountText(line.projected, line.currency)} projected` }}</td>
            </tr>
          </tbody>
        </q-markup-table>

        <template v-if="overview.projections.length">
          <div class="text-subtitle2 q-mt-md q-mb-xs">Projected charges</div>
          <q-markup-table flat bordered dense data-spend-projections>
            <tbody>
              <tr v-for="p in overview.projections" :key="`${p.accountId}-${p.periodStart}`">
                <td>{{ p.accountName }}</td>
                <td>{{ servicePeriodText(p.periodStart, p.periodEnd) }}</td>
                <td class="text-right">{{ amountText(p.amount, p.currency) }} projected</td>
              </tr>
            </tbody>
          </q-markup-table>
        </template>
      </div>
    </div>
  </div>
</template>
