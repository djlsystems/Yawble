<script setup lang="ts">
import { ref, watch } from 'vue';

// The default-model picker of Settings > Agents: the app's own list for the CLI, "The CLI's default" (empty), and
// Custom… for any other model name. v-model is the model ("" = the CLI's default).
const props = defineProps<{ modelValue: string; models: string[]; testId?: string }>();
const emit = defineEmits<{ 'update:modelValue': [string]; incomplete: [boolean] }>();

const CUSTOM = '__custom__';
const pick = ref('');
const text = ref('');

function sync() {
  const m = props.modelValue;
  // While the person is in Custom…, their own typing never switches the picker (typing "sonnet-4-5" passes "sonnet").
  if (pick.value === CUSTOM && m === text.value.trim()) return;
  if (!m) { if (pick.value !== CUSTOM) pick.value = ''; return; }
  if (props.models.includes(m)) { pick.value = m; return; }
  pick.value = CUSTOM;
  text.value = m;
}
watch(() => [props.modelValue, props.models], sync, { immediate: true, deep: true });
// Custom… with no name yet is not a choice: the dialog's Save waits for one.
watch([pick, text], () => emit('incomplete', pick.value === CUSTOM && !text.value.trim()), { immediate: true });

function onPick() {
  if (pick.value === CUSTOM) { emit('update:modelValue', text.value.trim()); return; }
  emit('update:modelValue', pick.value);
}
function onText() { emit('update:modelValue', text.value.trim()); }
</script>

<template>
  <div class="picker">
    <select v-model="pick" :data-test="testId ?? 'model-pick'" @change="onPick">
      <option value="">The CLI's default</option>
      <option v-for="m in models" :key="m" :value="m">{{ m }}</option>
      <option :value="CUSTOM">Custom…</option>
    </select>
    <input v-if="pick === CUSTOM" v-model="text" placeholder="model name" :data-test="`${testId ?? 'model-pick'}-text`" @input="onText" />
    <span class="muted small">the app's list (Custom… takes any other model name)</span>
  </div>
</template>

<style scoped>
.picker { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; }
.small { font-size: 12px; }
</style>
