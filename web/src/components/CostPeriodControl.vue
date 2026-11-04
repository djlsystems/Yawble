<script setup lang="ts">
import { computed, onBeforeUnmount, ref } from 'vue';
import type { CostBucket } from '../api/outcomes';
import { useCostPeriodChoice } from '../lib/costPeriodChoice';
import { rangeOf } from '../lib/spend';
import { periodHeading, rangeOfChoice } from '../lib/teamsPeriod';

/**
 * THE SHARED PERIOD CONTROL: the period's words, Daily | Monthly, Last two months and Custom range - the same control
 * above the Teams list and on a team page, over the one remembered choice (`lib/costPeriodChoice.ts`). A custom range
 * is a first and a last day, both included, read as UTC days; the read sends `to` as the day after the last.
 */
const { choice, choose } = useCostPeriodChoice();

/** The clock the heading's "last two months" moves with, ticking once a minute. */
const now = ref(Date.now());
const ticker = setInterval(() => (now.value = Date.now()), 60_000);
onBeforeUnmount(() => clearInterval(ticker));

const heading = computed(() => periodHeading(choice.value, new Date(now.value)));

/** The custom range's fields, open while a person picks one. */
const customOpen = ref(false);
const firstDay = ref('');
const lastDay = ref('');
const rangeError = ref<string | null>(null);

function openCustom() {
  const range = rangeOfChoice(choice.value);
  const last = new Date(`${range.to}T00:00:00Z`);
  last.setUTCDate(last.getUTCDate() - 1);
  firstDay.value = range.from;
  lastDay.value = last.toISOString().slice(0, 10);
  rangeError.value = null;
  customOpen.value = true;
}

function applyRange() {
  const range = rangeOf(firstDay.value, lastDay.value);
  if (!range) {
    rangeError.value = 'Pick a first and a last day, the last on or after the first.';
    return;
  }
  rangeError.value = null;
  customOpen.value = false;
  choose({ ...choice.value, range });
}

function lastTwo() {
  customOpen.value = false;
  choose({ ...choice.value, range: null });
}

function setBucket(bucket: CostBucket) {
  choose({ ...choice.value, bucket });
}
</script>

<template>
  <div class="cost-period" data-cost-period>
    <div class="row items-center cost-period-control" data-teams-period-control>
      <span class="os-text-muted cost-period-words" data-teams-period>{{ heading }} <span class="text-caption">UTC</span></span>
      <q-btn-toggle
        dense
        no-caps
        unelevated
        toggle-color="primary"
        :model-value="choice.bucket"
        :options="[{ label: 'Daily', value: 'day' }, { label: 'Monthly', value: 'month' }]"
        data-teams-bucket
        @update:model-value="setBucket"
      />
      <q-btn
        flat
        dense
        no-caps
        label="Last two months"
        :disable="choice.range === null"
        data-teams-period-default
        @click="lastTwo"
      />
      <q-btn flat dense no-caps icon="event" label="Custom range" data-teams-period-custom @click="openCustom" />
    </div>
    <div v-if="customOpen" class="row items-center q-gutter-sm q-mt-sm" data-teams-range>
      <q-input v-model="firstDay" dense outlined type="date" stack-label label="First day" data-field="rangeFrom" />
      <q-input v-model="lastDay" dense outlined type="date" stack-label label="Last day" data-field="rangeTo" />
      <q-btn unelevated no-caps color="primary" label="Apply" data-teams-range-apply @click="applyRange" />
      <q-btn flat no-caps label="Cancel" data-teams-range-cancel @click="customOpen = false" />
      <span v-if="rangeError" class="text-negative text-caption" data-teams-range-error>{{ rangeError }}</span>
    </div>
  </div>
</template>

<style scoped>
.cost-period {
  display: flex;
  flex-direction: column;
  align-items: flex-end;
}

/* The period's words, Daily/Monthly, then the range buttons. */
.cost-period-control {
  gap: 8px;
  flex-wrap: wrap;
  justify-content: flex-end;
}

.cost-period-words {
  font-size: 0.875rem;
}
</style>
