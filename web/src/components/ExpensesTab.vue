<script setup lang="ts">
import { computed, ref } from 'vue';
import {
  type BillingAccount, type Expense, type ExpenseFields, type ExpenseKind, recordExpense, replaceExpense, voidExpense,
} from '../api/spend';
import { KindWords, amountText, correctable, dayText, expenseChains, servicePeriodText } from '../lib/spend';

/**
 * THE EXPENSES TAB, over S2's routes: record an invoice, an adjustment or a confirmed subscription charge,
 * and correct one by voiding it or replacing it. There is no edit and no delete: a correction is a new
 * row that supersedes the old, and each expense shows that history. The currency is the account's own.
 */
const props = defineProps<{ accounts: BillingAccount[]; expenses: Expense[] }>();
const emit = defineEmits<{ changed: [] }>();

const accountsById = computed(() => new Map(props.accounts.map((a) => [a.id, a])));
const chains = computed(() => expenseChains(props.expenses));
const expanded = ref<Record<string, boolean>>({});

const kindOptions = (['invoice', 'subscription-charge', 'adjustment'] as const).map((value) => ({ value, label: KindWords[value] }));
const accountOptions = computed(() => props.accounts.map((a) => ({
  value: a.id, label: `${a.name} · ${a.vendor} (${a.currency})${a.active ? '' : ' · inactive'}`,
})));

interface Draft {
  accountId: string;
  kind: ExpenseKind;
  amount: string;
  first: string;
  last: string;
  reference: string;
  note: string;
}

const formOpen = ref(false);
const replacing = ref<Expense | null>(null);
const draft = ref<Draft>(blank());
const error = ref<string | null>(null);
const saving = ref(false);

function iso(date: Date): string {
  return date.toISOString().slice(0, 10);
}

function blank(): Draft {
  const now = new Date();
  const first = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), 1));
  const last = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth() + 1, 0));
  return { accountId: props.accounts.find((a) => a.active)?.id ?? '', kind: 'invoice', amount: '', first: iso(first), last: iso(last), reference: '', note: '' };
}

function lastDay(exclusiveEnd: string): string {
  const end = new Date(exclusiveEnd);
  end.setUTCDate(end.getUTCDate() - 1);
  return iso(end);
}

/** The person picks the LAST day covered; the route's end is exclusive, the day after. */
function endOf(last: string): string {
  const end = new Date(`${last}T00:00:00Z`);
  end.setUTCDate(end.getUTCDate() + 1);
  return iso(end);
}

const currency = computed(() => accountsById.value.get(draft.value.accountId)?.currency ?? '');

function record() {
  replacing.value = null;
  draft.value = blank();
  error.value = null;
  formOpen.value = true;
}

function replace(row: Expense) {
  replacing.value = row;
  draft.value = {
    accountId: row.accountId, kind: row.kind, amount: row.amount, first: row.servicePeriodStart.slice(0, 10),
    last: lastDay(row.servicePeriodEnd), reference: row.reference ?? '', note: row.note ?? '',
  };
  error.value = null;
  formOpen.value = true;
}

async function save() {
  saving.value = true;
  error.value = null;
  const d = draft.value;
  const fields: ExpenseFields = {
    amount: d.amount.trim(), currency: currency.value, servicePeriodStart: d.first, servicePeriodEnd: endOf(d.last), kind: d.kind,
    reference: d.reference.trim() === '' ? null : d.reference.trim(), note: d.note.trim() === '' ? null : d.note.trim(),
  };
  try {
    if (replacing.value) await replaceExpense(replacing.value.id, fields);
    else await recordExpense({ ...fields, accountId: d.accountId });
    formOpen.value = false;
    emit('changed');
  } catch (e) {
    error.value = (e as Error).message;
  } finally {
    saving.value = false;
  }
}

const voiding = ref<Expense | null>(null);
const voidNote = ref('');
const voidError = ref<string | null>(null);

function askVoid(row: Expense) {
  voiding.value = row;
  voidNote.value = '';
  voidError.value = null;
}

async function confirmVoid() {
  if (!voiding.value) return;
  try {
    await voidExpense(voiding.value.id, voidNote.value.trim() === '' ? null : voidNote.value.trim());
    voiding.value = null;
    emit('changed');
  } catch (e) {
    voidError.value = (e as Error).message;
  }
}

function accountName(id: string): string {
  return accountsById.value.get(id)?.name ?? id;
}

function statusOf(row: Expense): string {
  if (row.status === 'void') return 'Void';
  return row.current ? 'Confirmed' : 'Replaced';
}
</script>

<template>
  <div data-expenses>
    <div class="row items-center q-mb-sm">
      <div class="text-body2 os-text-muted">Confirmed charges, in each account's currency. A correction is a void or a replacement; nothing is edited or deleted.</div>
      <q-space />
      <q-btn unelevated no-caps color="primary" icon="add" label="Record expense" :disable="!accounts.length" data-expense-record @click="record" />
    </div>

    <q-markup-table flat bordered dense>
      <thead>
        <tr>
          <th class="text-left">Account</th><th class="text-left">Kind</th><th class="text-left">Service period</th>
          <th class="text-right">Amount</th><th class="text-left">Status</th><th class="text-left">Reference</th>
          <th class="text-left">Recorded</th><th class="text-right" />
        </tr>
      </thead>
      <tbody>
        <tr v-if="!chains.length"><td colspan="8" class="os-text-muted">No expenses recorded.</td></tr>
        <template v-for="chain in chains" :key="chain.current.id">
          <tr :data-expense="chain.current.id" :data-expense-status="chain.current.status">
            <td>{{ accountName(chain.current.accountId) }}</td>
            <td>{{ KindWords[chain.current.kind] ?? chain.current.kind }}</td>
            <td>{{ servicePeriodText(chain.current.servicePeriodStart, chain.current.servicePeriodEnd) }}</td>
            <td class="text-right" :class="{ 'text-strike os-text-muted': chain.current.status === 'void' }">{{ amountText(chain.current.amount, chain.current.currency) }}</td>
            <td><q-badge :outline="chain.current.status === 'void'" :color="chain.current.status === 'void' ? 'grey' : 'primary'" :label="statusOf(chain.current)" /></td>
            <td>{{ chain.current.reference ?? '' }}<div v-if="chain.current.note" class="text-caption os-text-muted">{{ chain.current.note }}</div></td>
            <td class="text-caption">{{ dayText(chain.current.recordedAt) }} · {{ chain.current.recordedBy }}</td>
            <td class="text-right no-wrap">
              <q-btn v-if="chain.history.length" flat dense no-caps :icon="expanded[chain.current.id] ? 'keyboard_arrow_up' : 'history'"
                :label="`History (${chain.history.length})`" data-expense-history-toggle
                @click="expanded[chain.current.id] = !expanded[chain.current.id]" />
              <template v-if="correctable(chain.current)">
                <q-btn flat dense no-caps icon="edit" label="Replace" data-expense-replace @click="replace(chain.current)" />
                <q-btn flat dense no-caps icon="block" label="Void" data-expense-void @click="askVoid(chain.current)" />
              </template>
            </td>
          </tr>
          <template v-if="expanded[chain.current.id]">
            <tr v-for="old in chain.history" :key="old.id" class="os-text-muted" :data-expense-history="old.id">
              <td class="q-pl-lg">superseded</td>
              <td>{{ KindWords[old.kind] ?? old.kind }}</td>
              <td>{{ servicePeriodText(old.servicePeriodStart, old.servicePeriodEnd) }}</td>
              <td class="text-right text-strike">{{ amountText(old.amount, old.currency) }}</td>
              <td>{{ statusOf(old) }}</td>
              <td>{{ old.reference ?? '' }}<div v-if="old.note" class="text-caption">{{ old.note }}</div></td>
              <td class="text-caption">{{ dayText(old.recordedAt) }} · {{ old.recordedBy }}</td>
              <td />
            </tr>
          </template>
        </template>
      </tbody>
    </q-markup-table>

    <q-dialog v-model="formOpen">
      <q-card class="os-dialog-md" data-expense-form>
        <q-card-section class="os-dialog-title">{{ replacing ? 'Replace an expense' : 'Record an expense' }}</q-card-section>
        <q-card-section class="q-gutter-sm">
          <q-select v-model="draft.accountId" dense outlined emit-value map-options :options="accountOptions" :disable="!!replacing" label="Billing account" data-field="accountId" />
          <q-select v-model="draft.kind" dense outlined emit-value map-options :options="kindOptions" label="Kind" data-field="kind" />
          <q-input v-model="draft.amount" dense outlined inputmode="decimal" :label="`Amount${currency ? ` (${currency})` : ''}`" data-field="amount" />
          <div class="row q-col-gutter-sm">
            <q-input v-model="draft.first" class="col-6" dense outlined type="date" stack-label label="Service period from" data-field="first" />
            <q-input v-model="draft.last" class="col-6" dense outlined type="date" stack-label label="Service period to (last day)" data-field="last" />
          </div>
          <q-input v-model="draft.reference" dense outlined label="Reference (optional)" data-field="reference" />
          <q-input v-model="draft.note" dense outlined label="Note (optional)" data-field="note" />
          <q-banner v-if="error" dense class="bg-negative text-white" data-expense-error>{{ error }}</q-banner>
        </q-card-section>
        <q-card-actions align="right">
          <q-btn v-close-popup flat no-caps label="Cancel" />
          <q-btn unelevated no-caps color="primary" :loading="saving" :label="replacing ? 'Replace' : 'Record'" data-expense-save @click="save" />
        </q-card-actions>
      </q-card>
    </q-dialog>

    <q-dialog :model-value="voiding !== null" @update:model-value="(v) => { if (!v) voiding = null }">
      <q-card v-if="voiding" class="os-dialog-md" data-expense-void-form>
        <q-card-section class="os-dialog-title">Void this expense?</q-card-section>
        <q-card-section>
          <div class="q-mb-sm">{{ accountName(voiding.accountId) }} · {{ amountText(voiding.amount, voiding.currency) }} · {{ servicePeriodText(voiding.servicePeriodStart, voiding.servicePeriodEnd) }}</div>
          <div class="text-caption os-text-muted q-mb-sm">The row is kept in its history and no longer counts.</div>
          <q-input v-model="voidNote" dense outlined label="Note (optional)" data-field="voidNote" />
          <q-banner v-if="voidError" dense class="q-mt-sm bg-negative text-white">{{ voidError }}</q-banner>
        </q-card-section>
        <q-card-actions align="right">
          <q-btn flat no-caps label="Cancel" @click="voiding = null" />
          <q-btn unelevated no-caps color="negative" label="Void" data-expense-void-confirm @click="confirmVoid" />
        </q-card-actions>
      </q-card>
    </q-dialog>
  </div>
</template>
