<script setup lang="ts">
import { computed } from 'vue';
import FieldHelp from './FieldHelp.vue';
import type { BillingAccount } from '../api/spend';
import type { MemberDetail } from '../api/types';
import {
  AccountLabel,
  ModelNotIdentifiable,
  NextRunSentence,
  RateLabel,
  accountOptions,
  accountText,
  effectiveRateText,
  inheritLabel,
  rateSourceText,
  type CostDraft,
  type TenantDefault,
} from '../lib/memberCost';

/**
 * A MEMBER'S ESTIMATED HOURLY RATE AND BILLING ACCOUNT, in Member settings, for agent and plugin members
 * alike. What is edited here rides the member's own Save as `hourlyRate` and `billingAccountId`, each
 * only when the person changed it (`costPatch`). The EFFECTIVE CONFIGURATION below is read-only and is
 * the member read's own answer (`effectiveRate`, `billingAccountId`) - never worked out here - so it
 * shows what the last save stored, and what a run starting now is snapshotted at.
 */
const draft = defineModel<CostDraft>({ required: true });

const props = defineProps<{
  kind: 'agent' | 'plugin';
  /** The CLI preset, or the plugin, as the Agent field names it. */
  preset: string;
  /** The member read, or null until it answers (the fields stay disabled until then). */
  stored: MemberDetail | null;
  /** Why the member read failed, or null. */
  problem: string | null;
  /** The tenant's billing accounts, or null until (or unless) the read answers. */
  accounts: BillingAccount[] | null;
  accountsProblem: string | null;
  tenant: TenantDefault | null;
}>();

const heldRate = computed(() => props.stored?.hourlyRate ?? null);
const heldAccount = computed(() => props.stored?.billingAccountId ?? null);
const effective = computed(() => props.stored?.effectiveRate ?? null);

const modeOptions = computed(() => [
  {
    label: inheritLabel(props.kind, props.stored ? { hourlyRate: heldRate.value, effectiveRate: effective.value ?? { amount: null, currency: null, source: 'none' } } : null, props.tenant),
    value: 'inherit',
  },
  { label: 'Override', value: 'override' },
]);

const pickerOptions = computed(() => accountOptions(props.accounts ?? [], heldAccount.value));

/** The override is in the reporting currency of estimates, as the server stores it. */
const rateSuffix = computed(() => `${props.tenant?.currency ?? effective.value?.currency ?? ''}/h`.replace(/^\/h$/, 'per hour'));

const disabled = computed(() => props.stored === null);
</script>

<template>
  <div class="q-gutter-sm" data-member-cost>
    <div class="text-subtitle2">Estimated hourly rate and billing account</div>

    <div v-if="problem" class="text-negative text-caption" data-member-cost-problem>{{ problem }}</div>

    <div data-member-rate>
      <div class="os-body">{{ RateLabel }}</div>
      <q-option-group
        v-model="draft.mode"
        :options="modeOptions"
        type="radio"
        dense
        :disable="disabled"
        data-member-rate-mode
      />
      <q-input
        v-if="draft.mode === 'override'"
        v-model="draft.rate"
        outlined
        dense
        inputmode="decimal"
        :label="RateLabel"
        :suffix="rateSuffix"
        :disable="disabled"
        data-member-rate-input
      >
        <template #append><FieldHelp text="A plain number of zero or more, such as 60 or 62.5. 0 means explicitly not priced. Leave it blank, or choose Inherit, to go back to the default." /></template>
      </q-input>
    </div>

    <q-select
      v-model="draft.account"
      :options="pickerOptions"
      outlined
      dense
      emit-value
      map-options
      :label="AccountLabel"
      :disable="disabled || accounts === null"
      data-member-account
    >
      <template #option="scope">
        <q-item v-bind="scope.itemProps" :data-account-option="scope.opt.value">
          <q-item-section>
            <q-item-label>{{ scope.opt.label }}</q-item-label>
            <q-item-label v-if="scope.opt.caption" caption>{{ scope.opt.caption }}</q-item-label>
          </q-item-section>
        </q-item>
      </template>
      <template #append><FieldHelp text="The tenant's billing account this member's runs are recorded against. Only active accounts can be chosen; a member already on an inactive account keeps it until you choose another." /></template>
    </q-select>
    <div v-if="accountsProblem" class="text-negative text-caption" data-member-accounts-problem>{{ accountsProblem }}</div>

    <div class="member-cost-effective" data-member-effective>
      <div class="os-body text-weight-medium">Effective configuration</div>
      <dl class="q-my-xs">
        <div><dt class="os-text-muted">{{ kind === 'plugin' ? 'Plugin' : 'CLI preset' }}</dt><dd data-effective-preset>{{ preset }}</dd></div>
        <div><dt class="os-text-muted">Model</dt><dd data-effective-model>{{ ModelNotIdentifiable }}</dd></div>
        <div>
          <dt class="os-text-muted">{{ RateLabel }}</dt>
          <dd data-effective-rate>
            <template v-if="effective">{{ effectiveRateText(effective) }} · source: <span data-effective-source>{{ rateSourceText(effective.source) }}</span></template>
            <template v-else>Reading…</template>
          </dd>
        </div>
        <div><dt class="os-text-muted">{{ AccountLabel }}</dt><dd data-effective-account>{{ stored ? accountText(accounts, heldAccount) : 'Reading…' }}</dd></div>
      </dl>
    </div>

    <div class="text-caption os-text-muted" data-next-run>{{ NextRunSentence }}</div>
  </div>
</template>

<style scoped>
.member-cost-effective dl > div {
  display: flex;
  gap: 0.75rem;
}
.member-cost-effective dt {
  min-width: 11rem;
}
.member-cost-effective dd {
  margin: 0;
}
</style>
