<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { modelOptionText, type ModelOption } from '../modelOptions';

// A model picker: the models agents.models lists for the CLI, each with its small "from" hint, the default (empty;
// "The CLI's default" unless defaultLabel says otherwise), and Custom… for any other model name. v-model is the model
// ("" = the default). Settings > Agents, the agent editor and New team use it.
const props = withDefaults(defineProps<{ modelValue: string; models: ModelOption[]; testId?: string; defaultLabel?: string; note?: boolean }>(), {
  testId: undefined, defaultLabel: "The CLI's default", note: true,
});
const emit = defineEmits<{ 'update:modelValue': [string]; incomplete: [boolean] }>();

const CUSTOM = '__custom__';
const pick = ref('');
const text = ref('');
const names = computed(() => props.models.map((o) => o.model));

function sync() {
  const m = props.modelValue;
  // While the person is in Custom…, their own typing never switches the picker (typing "sonnet-4-5" passes "sonnet").
  if (pick.value === CUSTOM && m === text.value.trim()) return;
  if (!m) { if (pick.value !== CUSTOM) pick.value = ''; return; }
  if (names.value.includes(m)) { pick.value = m; return; }
  pick.value = CUSTOM;
  text.value = m;
}
watch(() => [props.modelValue, names.value], sync, { immediate: true, deep: true });
// Custom… with no name yet is not a choice: the dialog's Save waits for one.
watch([pick, text], () => emit('incomplete', pick.value === CUSTOM && !text.value.trim()), { immediate: true });

function onPick() {
  if (pick.value === CUSTOM) { emit('update:modelValue', text.value.trim()); return; }
  emit('update:modelValue', pick.value);
}
function onText() { emit('update:modelValue', text.value.trim()); }
const id = computed(() => props.testId ?? 'model-pick');
</script>

<template>
  <div class="picker">
    <select v-model="pick" :data-test="id" title="The model this agent runs" @change="onPick">
      <option value="">{{ defaultLabel }}</option>
      <option v-for="o in models" :key="o.model" :value="o.model" :data-from="o.from || undefined">{{ modelOptionText(o) }}</option>
      <option :value="CUSTOM">Custom…</option>
    </select>
    <input v-if="pick === CUSTOM" v-model="text" placeholder="model name" :data-test="`${id}-text`" @input="onText" />
    <span v-if="note" class="muted small">Custom… takes any other model name</span>
  </div>
</template>

<style scoped>
.picker { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; }
.small { font-size: 12px; }
</style>
