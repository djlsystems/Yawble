<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import { listLiveOutcomes, outcomeLabel, type OutcomeRef } from '../api/outcomes';

/**
 * AN OUTCOME PICKER, where work starts: the backlog item editor and each trigger. Offers None and
 * every active and proposed outcome; the value is the outcome's id, or '' for none - the word the
 * routes take for "clear it".
 *
 * The names are q-select labels, so they render as text, never HTML. A failed read leaves only
 * None on offer; the form around it still works.
 */
const props = defineProps<{ modelValue: string | null | undefined; label?: string; hint?: string; disable?: boolean }>();
const emit = defineEmits<{ 'update:modelValue': [value: string] }>();

const outcomes = ref<OutcomeRef[]>([]);

onMounted(async () => {
  try {
    outcomes.value = await listLiveOutcomes();
  } catch {
    outcomes.value = [];
  }
});

const options = computed(() => [
  { label: 'None', value: '' },
  ...outcomes.value.map((outcome) => ({ label: outcomeLabel(outcome), value: outcome.id })),
]);
</script>

<template>
  <q-select
    :model-value="props.modelValue ?? ''"
    :options="options"
    :label="props.label ?? 'Outcome'"
    :hint="props.hint"
    :disable="props.disable"
    dense
    outlined
    emit-value
    map-options
    data-picker="outcome"
    @update:model-value="(value) => emit('update:modelValue', value ?? '')"
  />
</template>
