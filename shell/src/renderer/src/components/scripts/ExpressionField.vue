<script setup lang="ts">
import { computed, nextTick, ref } from 'vue';
import { applySuggestion, suggestAt, valueFromText, valueText, type Suggestion, type SuggestionsAt } from '../../scripts/catalog';

// A value field of a step: the text of the value ("{vars.res.messages}", "New email: {m.subject}", 200), with
// suggestions for what is typed at the cursor: settings.*, vars.*, the loop items and their fields, and the functions.
// Outside braces a pick is put in "{…}". Arrow keys move through the list, Enter or Tab picks, Escape closes it.
const props = defineProps<{
  modelValue: unknown;
  label: string;
  /** The type the value should have, shown beside the label. */
  type?: string;
  required?: boolean;
  suggestions: readonly Suggestion[];
  readonly?: boolean;
  /** For data-test. */
  name?: string;
}>();
const emit = defineEmits<{ 'update:modelValue': [v: unknown] }>();

const input = ref<HTMLInputElement | null>(null);
const at = ref<SuggestionsAt | null>(null);
const active = ref(0);
const shown = computed(() => (at.value && at.value.items.length ? at.value.items : []));

function refresh() {
  const el = input.value;
  if (!el || props.readonly) { at.value = null; return; }
  at.value = suggestAt(el.value, el.selectionStart ?? el.value.length, props.suggestions);
  active.value = 0;
}

function onInput(e: Event) {
  const t = (e.target as HTMLInputElement).value;
  emit('update:modelValue', valueFromText(t, props.modelValue));
  refresh();
}

async function pick(s: Suggestion) {
  const el = input.value;
  if (!el || !at.value) return;
  const r = applySuggestion(el.value, at.value, s);
  emit('update:modelValue', valueFromText(r.value, props.modelValue));
  el.value = r.value;
  at.value = null;
  await nextTick();
  el.focus();
  el.setSelectionRange(r.cursor, r.cursor);
}

function onKey(e: KeyboardEvent) {
  if (!shown.value.length) return;
  if (e.key === 'ArrowDown') { e.preventDefault(); active.value = (active.value + 1) % shown.value.length; } else if (e.key === 'ArrowUp') { e.preventDefault(); active.value = (active.value - 1 + shown.value.length) % shown.value.length; } else if (e.key === 'Enter' || e.key === 'Tab') { e.preventDefault(); void pick(shown.value[active.value]!); } else if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); at.value = null; }
}

function close() { setTimeout(() => { at.value = null; }, 120); }
</script>

<template>
  <label class="expr" data-test="expr-field" :data-name="name ?? label">
    <span class="lab">{{ label }}<span v-if="required" class="req" title="Required" data-test="expr-required">*</span>
      <span v-if="type" class="type" data-test="expr-type">{{ type }}</span></span>
    <span class="wrap">
      <input ref="input" :value="valueText(modelValue)" :readonly="readonly" spellcheck="false" autocomplete="off" data-test="expr-input"
        @input="onInput" @focus="refresh" @click="refresh" @keydown="onKey" @blur="close" />
      <ul v-if="shown.length" class="list" role="listbox" data-test="expr-suggestions">
        <li v-for="(s, i) in shown" :key="s.text" role="option" :aria-selected="i === active" :class="{ on: i === active }" :data-kind="s.kind"
          data-test="expr-suggestion" :data-text="s.text" @mousedown.prevent="pick(s)">
          <code>{{ s.text }}</code><span class="detail">{{ s.detail }}</span>
        </li>
      </ul>
    </span>
  </label>
</template>

<style scoped>
.expr { display: flex; flex-direction: column; gap: 2px; font-size: 12px; }
.lab { display: inline-flex; gap: 4px; align-items: baseline; color: var(--ink-soft); }
.req { color: var(--bad); }
.type { font-family: ui-monospace, monospace; font-size: 11px; color: var(--muted); }
.wrap { position: relative; display: block; }
input { width: 100%; box-sizing: border-box; font-family: ui-monospace, monospace; font-size: 12px; padding: 3px 6px; border: 1px solid var(--line); border-radius: 4px; background: var(--surface); color: var(--ink); }
input[readonly] { background: var(--surface-sunken); }
.list { position: absolute; z-index: 20; left: 0; right: 0; top: 100%; margin: 2px 0 0; padding: 2px 0; list-style: none; max-height: 200px; overflow: auto;
  background: var(--surface); border: 1px solid var(--line); border-radius: 4px; box-shadow: 0 4px 14px var(--shadow-menu); }
.list li { display: flex; justify-content: space-between; gap: 8px; padding: 2px 8px; cursor: pointer; }
.list li.on, .list li:hover { background: var(--selected); }
.detail { color: var(--muted); font-size: 11px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
</style>
