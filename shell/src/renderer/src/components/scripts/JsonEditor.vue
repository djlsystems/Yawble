<script setup lang="ts">
import { computed, nextTick, ref } from 'vue';
import { jsonTokens, lineOf } from '../../scripts/api';

// A light JSON editor (B134): a plain textarea over a highlighted copy of the same text, so typing, undo and the
// clipboard are the browser's own. No editor library: the shell has none. Colours come from the theme variables.
const props = defineProps<{ modelValue: string; readonly?: boolean; label?: string }>();
const emit = defineEmits<{ 'update:modelValue': [v: string] }>();

const area = ref<HTMLTextAreaElement | null>(null);
const shown = ref<HTMLPreElement | null>(null);
// A trailing newline would otherwise not get its line in the <pre>, and the two would drift apart at the end.
const tokens = computed(() => jsonTokens(props.modelValue.endsWith('\n') ? `${props.modelValue} ` : props.modelValue));
/** The line the last jump went to, marked until the next edit. */
const marked = ref<number | null>(null);

function sync() {
  if (!area.value || !shown.value) return;
  shown.value.scrollTop = area.value.scrollTop;
  shown.value.scrollLeft = area.value.scrollLeft;
}
function input(e: Event) {
  marked.value = null;
  emit('update:modelValue', (e.target as HTMLTextAreaElement).value);
}

/** Selects [start, end) and scrolls it into view (a check problem's step). */
async function jump(start: number, end: number) {
  await nextTick();
  const a = area.value;
  if (!a) return;
  a.focus();
  a.setSelectionRange(start, end);
  const line = lineOf(props.modelValue, start);
  marked.value = line;
  const lh = parseFloat(getComputedStyle(a).lineHeight) || 18;
  a.scrollTop = Math.max(0, line * lh - a.clientHeight / 3);
  sync();
}
defineExpose({ jump, marked });
</script>

<template>
  <div class="json-editor" :class="{ readonly }" data-test="json-editor">
    <pre ref="shown" class="shown" aria-hidden="true"><span v-for="(t, i) in tokens" :key="i" :class="`t-${t.kind}`">{{ t.text }}</span></pre>
    <textarea ref="area" class="edit" spellcheck="false" autocomplete="off" wrap="off" :aria-label="label ?? 'JSON'" :readonly="readonly"
      :value="modelValue" data-test="json-text" :data-marked-line="marked ?? undefined" @input="input" @scroll="sync" />
  </div>
</template>

<style scoped>
.json-editor { position: relative; flex: 1 1 0; min-height: 240px; border: 1px solid var(--line); border-radius: 6px; background: var(--surface-2); overflow: hidden; }
.shown, .edit { position: absolute; inset: 0; margin: 0; padding: 8px 10px; font-family: ui-monospace, monospace; font-size: 12.5px; line-height: 18px;
  white-space: pre; tab-size: 2; overflow: auto; border: 0; box-sizing: border-box; }
.shown { color: var(--ink); pointer-events: none; overflow: hidden; }
.edit { background: transparent; color: transparent; caret-color: var(--ink); resize: none; border-radius: 6px; }
.edit::selection { background: var(--selection); color: transparent; }
.readonly { background: var(--surface-sunken); }
.t-key { color: var(--accent); }
.t-string { color: var(--ok); }
.t-number { color: var(--warn); }
.t-literal { color: var(--pill-violet-ink); }
.t-punct { color: var(--muted); }
.t-other { color: var(--bad); }
</style>
