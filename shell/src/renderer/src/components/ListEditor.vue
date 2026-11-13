<script setup lang="ts">
import { ref, watch } from 'vue';
import { addEntries } from '../listEntries';

// A list of strings edited one entry at a time (Settings > Guardrails: hosts and folders): each entry a row with ×,
// and an input that adds what is typed on Enter (or the Add button, or leaving the field). Entries are trimmed; a
// paste of several (commas or new lines between them) adds each; one already in the list
// is ignored. The typed text is also a v-model (`draft`), so the page can count it as a change and add it on Save.
const props = defineProps<{ modelValue: string[]; draft?: string; placeholder?: string; mono?: boolean; addLabel?: string }>();
const emit = defineEmits<{ 'update:modelValue': [value: string[]]; 'update:draft': [value: string] }>();

const text = ref(props.draft ?? '');
watch(() => props.draft, (d) => { if (d !== undefined && d !== text.value) text.value = d; });
watch(text, (t) => { if (t !== props.draft) emit('update:draft', t); });

function add() {
  const next = addEntries(props.modelValue, text.value);
  text.value = '';
  if (next !== props.modelValue) emit('update:modelValue', next);
}

function remove(i: number) {
  emit('update:modelValue', props.modelValue.filter((_, k) => k !== i));
}
</script>

<template>
  <div class="list-editor" :class="{ mono }">
    <ul v-if="modelValue.length" class="entries">
      <li v-for="(v, i) in modelValue" :key="v" class="entry" data-test="list-entry">
        <span class="value" data-test="list-value">{{ v }}</span>
        <button type="button" class="remove" :title="`Remove ${v}`" :aria-label="`Remove ${v}`" data-test="list-remove" @click="remove(i)">×</button>
      </li>
    </ul>
    <p v-else class="none" data-test="list-none">None</p>
    <div class="add">
      <input v-model="text" :placeholder="placeholder" data-test="list-input" @keydown.enter.prevent="add" @blur="add" />
      <button type="button" data-test="list-add" :disabled="!text.trim()" @mousedown.prevent @click="add">{{ addLabel ?? 'Add' }}</button>
    </div>
  </div>
</template>

<style scoped>
.list-editor { display: flex; flex-direction: column; gap: 6px; min-width: 0; }
.entries { list-style: none; margin: 0; padding: 0; display: flex; flex-wrap: wrap; gap: 4px; }
.entry { display: inline-flex; align-items: center; gap: 2px; max-width: 100%; padding: 1px 2px 1px 8px; border: 1px solid var(--line); border-radius: 12px; background: var(--chip); font-size: 12.5px; }
.mono .value, .mono input { font-family: ui-monospace, monospace; font-size: 12px; }
.mono .entries { flex-direction: column; align-items: flex-start; }
.value { overflow-wrap: anywhere; }
.remove { border: 0; background: transparent; padding: 0 6px; line-height: 1.2; color: var(--muted); border-radius: 10px; }
.remove:hover:not(:disabled) { color: var(--bad); background: var(--hover); }
.none { margin: 0; color: var(--muted); font-size: 12.5px; }
.add { display: flex; gap: 6px; }
.add input { flex: 1; min-width: 0; }
</style>
