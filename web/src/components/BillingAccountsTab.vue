<script setup lang="ts">
import { computed, ref } from 'vue';
import {
  type BillingAccount, type BillingAccountFields, type BillingPeriod, type BillingType, type LinkedMember,
  createBillingAccount, editBillingAccount, setBillingAccountActive,
} from '../api/spend';
import { BillingTypeWords, amountText, dayText } from '../lib/spend';

/**
 * THE BILLING ACCOUNTS TAB, over S1's routes: list, add, edit, deactivate and reactivate. There is no
 * delete. Each account shows the members linked to it, read-only - a member's account is set in its own
 * settings. Only the editable fields are ever sent; the store sets the id, scope, instance and who
 * created it.
 */
const props = defineProps<{ accounts: BillingAccount[]; linked: LinkedMember[] }>();
const emit = defineEmits<{ changed: [] }>();

const membersOf = computed(() => {
  const by = new Map<string, LinkedMember[]>();
  for (const m of props.linked) by.set(m.accountId, [...(by.get(m.accountId) ?? []), m]);
  return by;
});

interface Draft {
  name: string;
  vendor: string;
  billingType: BillingType;
  currency: string;
  billingPeriod: BillingPeriod;
  recurringAmount: string;
  effectiveFrom: string;
  effectiveTo: string;
  description: string;
}

const editing = ref<BillingAccount | null>(null);
const formOpen = ref(false);
const draft = ref<Draft>(blank());
const error = ref<string | null>(null);
const saving = ref(false);

const typeOptions = (['subscription', 'metered', 'service'] as const).map((value) => ({ value, label: BillingTypeWords[value] }));
const periodOptions = [{ value: 'monthly', label: 'Monthly' }, { value: 'annual', label: 'Annual' }];

function blank(): Draft {
  return {
    name: '', vendor: '', billingType: 'subscription', currency: 'USD', billingPeriod: 'monthly',
    recurringAmount: '', effectiveFrom: new Date().toISOString().slice(0, 10), effectiveTo: '', description: '',
  };
}

function of(account: BillingAccount): Draft {
  return {
    name: account.name, vendor: account.vendor, billingType: account.billingType, currency: account.currency,
    billingPeriod: account.billingPeriod, recurringAmount: account.recurringAmount ?? '',
    effectiveFrom: account.effectiveFrom.slice(0, 10), effectiveTo: account.effectiveTo?.slice(0, 10) ?? '',
    description: account.description ?? '',
  };
}

function add() {
  editing.value = null;
  draft.value = blank();
  error.value = null;
  formOpen.value = true;
}

function edit(account: BillingAccount) {
  editing.value = account;
  draft.value = of(account);
  error.value = null;
  formOpen.value = true;
}

/** The draft's fields; on an edit only those that moved. */
function fields(): BillingAccountFields {
  const d = draft.value;
  const all: BillingAccountFields = {
    name: d.name, vendor: d.vendor, billingType: d.billingType, currency: d.currency, billingPeriod: d.billingPeriod,
    recurringAmount: d.recurringAmount.trim() === '' ? null : d.recurringAmount.trim(),
    effectiveFrom: d.effectiveFrom, effectiveTo: d.effectiveTo === '' ? null : d.effectiveTo,
    description: d.description.trim() === '' ? null : d.description,
  };
  if (!editing.value) return all;
  const before = of(editing.value);
  const changed: BillingAccountFields = {};
  for (const key of Object.keys(all) as (keyof Draft)[]) {
    if (d[key] !== before[key]) (changed as Record<string, unknown>)[key] = all[key];
  }
  return changed;
}

async function save() {
  saving.value = true;
  error.value = null;
  try {
    if (editing.value) await editBillingAccount(editing.value.id, fields());
    else await createBillingAccount(fields());
    formOpen.value = false;
    emit('changed');
  } catch (e) {
    error.value = (e as Error).message;
  } finally {
    saving.value = false;
  }
}

const actionError = ref<string | null>(null);

async function toggle(account: BillingAccount) {
  actionError.value = null;
  try {
    await setBillingAccountActive(account.id, !account.active);
    emit('changed');
  } catch (e) {
    actionError.value = (e as Error).message;
  }
}
</script>

<template>
  <div data-billing-accounts>
    <div class="row items-center q-mb-sm">
      <div class="text-body2 os-text-muted">Named vendor accounts the instance is billed through, each in its own currency. They hold no credential.</div>
      <q-space />
      <q-btn unelevated no-caps color="primary" icon="add" label="Add account" data-billing-account-add @click="add" />
    </div>
    <q-banner v-if="actionError" dense class="q-mb-sm bg-negative text-white">{{ actionError }}</q-banner>

    <q-markup-table flat bordered dense>
      <thead>
        <tr>
          <th class="text-left">Account</th><th class="text-left">Vendor</th><th class="text-left">Type</th>
          <th class="text-left">Currency</th><th class="text-left">Recurring</th><th class="text-left">Effective</th>
          <th class="text-left">Linked members</th><th class="text-right" />
        </tr>
      </thead>
      <tbody>
        <tr v-if="!accounts.length"><td colspan="8" class="os-text-muted">No billing accounts yet.</td></tr>
        <tr v-for="account in accounts" :key="account.id" :data-billing-account="account.id">
          <td>
            <div>{{ account.name }}<q-badge v-if="!account.active" outline class="q-ml-xs" label="Inactive" data-billing-account-inactive /></div>
            <div v-if="account.description" class="text-caption os-text-muted">{{ account.description }}</div>
          </td>
          <td>{{ account.vendor }}</td>
          <td>{{ BillingTypeWords[account.billingType] ?? account.billingType }}</td>
          <td>{{ account.currency }}</td>
          <td>{{ account.recurringAmount === null ? '—' : `${amountText(account.recurringAmount, account.currency)} ${account.billingPeriod}` }}</td>
          <td>{{ dayText(account.effectiveFrom) }}<template v-if="account.effectiveTo"> – {{ dayText(account.effectiveTo) }}</template></td>
          <td data-billing-account-members>
            <span v-if="!membersOf.get(account.id)?.length" class="os-text-muted">None</span>
            <div v-for="m in membersOf.get(account.id) ?? []" :key="`${m.team}/${m.member}`">{{ m.label }} <span class="text-caption os-text-muted">({{ m.teamName }})</span></div>
          </td>
          <td class="text-right no-wrap">
            <q-btn flat dense no-caps icon="edit" label="Edit" data-billing-account-edit @click="edit(account)" />
            <q-btn flat dense no-caps :icon="account.active ? 'block' : 'replay'" :label="account.active ? 'Deactivate' : 'Reactivate'"
              data-billing-account-toggle @click="toggle(account)" />
          </td>
        </tr>
      </tbody>
    </q-markup-table>

    <q-dialog v-model="formOpen">
      <q-card class="os-dialog-md" data-billing-account-form>
        <q-card-section class="os-dialog-title">{{ editing ? `Edit ${editing.name}` : 'Add a billing account' }}</q-card-section>
        <q-card-section class="q-gutter-sm">
          <q-input v-model="draft.name" dense outlined label="Name" data-field="name" />
          <q-input v-model="draft.vendor" dense outlined label="Vendor" data-field="vendor" />
          <q-select v-model="draft.billingType" dense outlined emit-value map-options :options="typeOptions" label="Billing type" data-field="billingType" />
          <div class="row q-col-gutter-sm">
            <q-input v-model="draft.currency" class="col-4" dense outlined label="Currency" maxlength="3" data-field="currency" />
            <q-select v-model="draft.billingPeriod" class="col-4" dense outlined emit-value map-options :options="periodOptions" label="Billing period" data-field="billingPeriod" />
            <q-input v-model="draft.recurringAmount" class="col-4" dense outlined label="Recurring amount" inputmode="decimal" data-field="recurringAmount" />
          </div>
          <div class="row q-col-gutter-sm">
            <q-input v-model="draft.effectiveFrom" class="col-6" dense outlined type="date" label="Effective from" stack-label data-field="effectiveFrom" />
            <q-input v-model="draft.effectiveTo" class="col-6" dense outlined type="date" label="Effective to (optional)" stack-label data-field="effectiveTo" />
          </div>
          <q-input v-model="draft.description" dense outlined label="Description (optional)" data-field="description" />
          <q-banner v-if="error" dense class="bg-negative text-white" data-billing-account-error>{{ error }}</q-banner>
        </q-card-section>
        <q-card-actions align="right">
          <q-btn v-close-popup flat no-caps label="Cancel" />
          <q-btn unelevated no-caps color="primary" :loading="saving" :label="editing ? 'Save' : 'Add account'" data-billing-account-save @click="save" />
        </q-card-actions>
      </q-card>
    </q-dialog>
  </div>
</template>
