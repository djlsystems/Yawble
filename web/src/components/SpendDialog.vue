<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import {
  type BillingAccount, type Expense, type LinkedMember, type SpendOverview,
  getLinkedMembers, getSpendOverview, listBillingAccounts, listExpenses,
} from '../api/spend';
import { currentMonth, periodLabel, rangeOf, shiftMonth } from '../lib/spend';
import DialogTabs from './DialogTabs.vue';
import SpendOverviewTab from './SpendOverviewTab.vue';
import BillingAccountsTab from './BillingAccountsTab.vue';
import ExpensesTab from './ExpensesTab.vue';

/**
 * ADMIN → SPEND, the tenant's Spend page at `#/spend`: what was paid to vendors (Actual Spend), what
 * recurring charges are projected, and the Estimated Execution Cost of agent and plugin time beside it,
 * over a period of this page's own - the current UTC month by default, or a custom range. A person's
 * only: every route it reads is refused to a machine principal.
 */
const open = defineModel<boolean>({ required: true });

const tab = ref<'overview' | 'accounts' | 'expenses'>('overview');
const period = ref(currentMonth());
const custom = ref(false);
const first = ref(period.value.from);
const last = ref('');
const rangeError = ref<string | null>(null);

const overview = ref<SpendOverview | null>(null);
const accounts = ref<BillingAccount[]>([]);
const expenses = ref<Expense[]>([]);
const linked = ref<LinkedMember[]>([]);
const loading = ref(false);
const error = ref<string | null>(null);

const isMonth = computed(() => {
  const month = currentMonth(new Date(`${period.value.from}T00:00:00Z`));
  return month.from === period.value.from && month.to === period.value.to;
});

async function load() {
  loading.value = true;
  error.value = null;
  try {
    const [o, a, e, l] = await Promise.all([getSpendOverview(period.value), listBillingAccounts(), listExpenses(), getLinkedMembers()]);
    overview.value = o;
    accounts.value = a;
    expenses.value = e;
    linked.value = l;
  } catch (e) {
    error.value = (e as Error).message;
  } finally {
    loading.value = false;
  }
}

function step(by: number) {
  period.value = shiftMonth(isMonth.value ? period.value.from : currentMonth().from, by);
  custom.value = false;
}

function thisMonth() {
  period.value = currentMonth();
  custom.value = false;
}

function openCustom() {
  const end = new Date(`${period.value.to}T00:00:00Z`);
  end.setUTCDate(end.getUTCDate() - 1);
  first.value = period.value.from;
  last.value = end.toISOString().slice(0, 10);
  rangeError.value = null;
  custom.value = true;
}

function applyRange() {
  const range = rangeOf(first.value, last.value);
  if (!range) {
    rangeError.value = 'Pick a first and a last day, the last on or after the first.';
    return;
  }
  rangeError.value = null;
  period.value = range;
}

watch(open, (isOpen) => { if (isOpen) void load(); }, { immediate: true });
watch(period, () => { if (open.value) void load(); });
</script>

<template>
  <q-dialog v-model="open">
    <q-card class="os-dialog-xl" data-spend-dialog>
      <q-card-section class="row items-center q-pb-none">
        <div class="os-dialog-title">Spend</div>
        <q-space />
        <div class="row items-center q-gutter-xs" data-spend-period>
          <q-btn flat dense round icon="chevron_left" aria-label="Previous month" data-spend-prev @click="step(-1)" />
          <div class="text-subtitle1 q-px-sm" data-spend-period-label>{{ periodLabel(period) }}</div>
          <q-btn flat dense round icon="chevron_right" aria-label="Next month" data-spend-next @click="step(1)" />
          <q-btn flat dense no-caps label="This month" data-spend-this-month @click="thisMonth" />
          <q-btn flat dense no-caps icon="event" label="Custom range" data-spend-custom @click="openCustom" />
          <span class="text-caption os-text-muted q-ml-sm">UTC</span>
        </div>
        <q-btn flat dense no-caps icon="refresh" label="Refresh" class="q-ml-md" :loading="loading" @click="load" />
        <q-btn v-close-popup flat dense round icon="close" aria-label="Close" />
      </q-card-section>

      <q-card-section v-if="custom" class="row items-center q-gutter-sm q-pb-none" data-spend-range>
        <q-input v-model="first" dense outlined type="date" stack-label label="First day" data-field="rangeFrom" />
        <q-input v-model="last" dense outlined type="date" stack-label label="Last day" data-field="rangeTo" />
        <q-btn unelevated no-caps color="primary" label="Apply" data-spend-range-apply @click="applyRange" />
        <span v-if="rangeError" class="text-negative text-caption">{{ rangeError }}</span>
      </q-card-section>

      <DialogTabs v-model="tab" class="q-px-md">
        <q-tab name="overview" label="Overview" data-spend-tab="overview" />
        <q-tab name="accounts" :label="`Billing accounts (${accounts.length})`" data-spend-tab="accounts" />
        <q-tab name="expenses" label="Expenses" data-spend-tab="expenses" />
      </DialogTabs>
      <q-separator />

      <q-card-section>
        <q-banner v-if="error" dense class="q-mb-md bg-negative text-white" data-spend-error>{{ error }}</q-banner>
        <SpendOverviewTab v-if="tab === 'overview' && overview" :overview="overview" />
        <BillingAccountsTab v-else-if="tab === 'accounts'" :accounts="accounts" :linked="linked" @changed="load" />
        <ExpensesTab v-else-if="tab === 'expenses'" :accounts="accounts" :expenses="expenses" @changed="load" />
      </q-card-section>
    </q-card>
  </q-dialog>
</template>
