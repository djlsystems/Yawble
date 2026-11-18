<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { highlightLines, languageFor } from './highlight';
import { hunkTitle, normalizeHunks, sideBySide, type DiffHunk, type DiffLine } from './diff';
import { useNarrow } from './narrow';
import './theme.css';

// One file's diff, read-only (B072's Commit view): inline (one column with the old and new line numbers) or side by side
// (old left, new right). Text is highlighted by highlight.js line by line (its escaped HTML only, as CodeLines). A
// binary or too-large file shows the core's summary line instead of any content. A narrow screen starts inline.
export interface DiffInput {
  path: string; oldPath?: string | null; binary: boolean; tooLarge: boolean; summary?: string | null;
  hunks?: DiffHunk[] | null; lines?: DiffLine[] | null;
}
const props = withDefaults(defineProps<{ diff: DiffInput; mode?: 'inline' | 'split' | null }>(), { mode: null });
const emit = defineEmits<{ 'update:mode': [mode: 'inline' | 'split'] }>();

const narrow = useNarrow();
const own = ref<'inline' | 'split'>(narrow.value ? 'inline' : 'split');
const shown = computed(() => props.mode ?? own.value);
function setMode(m: 'inline' | 'split') { own.value = m; emit('update:mode', m); }
watch(() => props.mode, (m) => { if (m) own.value = m; });

const language = computed(() => languageFor(props.diff.path));
const hunks = computed(() => normalizeHunks(props.diff));
const html = (text: string) => highlightLines(text, language.value)[0] ?? '';
const noContent = computed(() => props.diff.binary || props.diff.tooLarge);
const summary = computed(() => props.diff.summary
  || (props.diff.binary ? 'Binary file: its content is not shown.' : props.diff.tooLarge ? 'This diff is too large to show.' : ''));
const split = computed(() => hunks.value.map((h) => ({ title: hunkTitle(h), rows: sideBySide(h) })));
const width = computed(() => {
  let max = 1;
  for (const h of hunks.value) for (const l of h.lines) max = Math.max(max, l.oldLine ?? 0, l.newLine ?? 0);
  return `${String(max).length + 1}ch`;
});
const mark = (k: ' ' | '+' | '-') => (k === ' ' ? ' ' : k === '+' ? '+' : '−');
</script>

<template>
  <div class="cb-diff cb" data-test="diff-view" :data-mode="shown" :style="{ '--num-w': width }">
    <div class="cb-diff-bar">
      <span class="cb-diff-path" data-test="diff-path">
        <template v-if="diff.oldPath && diff.oldPath !== diff.path">{{ diff.oldPath }} → </template>{{ diff.path }}
      </span>
      <span v-if="!noContent" class="cb-diff-modes" role="group" aria-label="How the diff is shown">
        <button type="button" data-test="diff-mode-split" :aria-pressed="shown === 'split'" @click="setMode('split')">Side by side</button>
        <button type="button" data-test="diff-mode-inline" :aria-pressed="shown === 'inline'" @click="setMode('inline')">Inline</button>
      </span>
    </div>
    <p v-if="noContent" class="cb-diff-summary" data-test="diff-summary">{{ summary }}</p>
    <p v-else-if="!hunks.length" class="cb-diff-summary" data-test="diff-empty">{{ diff.summary || 'No line changed (only the file’s mode or name).' }}</p>
    <div v-else-if="shown === 'inline'" class="cb-diff-body" data-test="diff-inline">
      <template v-for="(h, hi) in hunks" :key="hi">
        <div class="cb-diff-hunk" data-test="diff-hunk">{{ hunkTitle(h) }}</div>
        <div v-for="(l, li) in h.lines" :key="`${hi}-${li}`" class="cb-diff-line" :class="{ add: l.kind === '+', del: l.kind === '-' }" data-test="diff-line" :data-kind="l.kind">
          <span class="cb-dnum">{{ l.oldLine ?? '' }}</span><span class="cb-dnum">{{ l.newLine ?? '' }}</span>
          <span class="cb-dmark" aria-hidden="true">{{ mark(l.kind) }}</span><span class="cb-dtext" v-html="html(l.text)" />
        </div>
      </template>
    </div>
    <div v-else class="cb-diff-body split" data-test="diff-split">
      <template v-for="(h, hi) in split" :key="hi">
        <div class="cb-diff-hunk" data-test="diff-hunk">{{ h.title }}</div>
        <div v-for="(r, ri) in h.rows" :key="`${hi}-${ri}`" class="cb-diff-row" data-test="diff-row">
          <div class="cb-diff-line half" :class="{ del: r.left?.kind === '-', empty: !r.left }" data-test="diff-left">
            <template v-if="r.left"><span class="cb-dnum">{{ r.left.num }}</span><span class="cb-dmark" aria-hidden="true">{{ mark(r.left.kind) }}</span><span class="cb-dtext" v-html="html(r.left.text)" /></template>
          </div>
          <div class="cb-diff-line half" :class="{ add: r.right?.kind === '+', empty: !r.right }" data-test="diff-right">
            <template v-if="r.right"><span class="cb-dnum">{{ r.right.num }}</span><span class="cb-dmark" aria-hidden="true">{{ mark(r.right.kind) }}</span><span class="cb-dtext" v-html="html(r.right.text)" /></template>
          </div>
        </div>
      </template>
    </div>
  </div>
</template>

<style scoped>
.cb-diff { display: flex; flex-direction: column; min-width: 0; min-height: 0; border: 1px solid var(--line); border-radius: 8px; overflow: hidden; background: var(--surface); }
.cb-diff-bar { display: flex; flex-wrap: wrap; align-items: center; gap: 6px 10px; padding: 6px 8px; border-bottom: 1px solid var(--line); }
.cb-diff-path { flex: 1 1 200px; min-width: 0; overflow-wrap: anywhere; font: 12.5px ui-monospace, 'Cascadia Code', Consolas, monospace; }
.cb-diff-modes { display: inline-flex; gap: 4px; }
.cb-diff-modes button[aria-pressed="true"] { background: color-mix(in srgb, var(--accent) 14%, var(--surface)); border-color: var(--accent); }
.cb-diff-summary { margin: 0; padding: 10px; color: var(--muted); }
.cb-diff-body { overflow: auto; font: 12.5px/20px ui-monospace, 'Cascadia Code', Consolas, monospace; max-height: 60vh; }
.cb-diff-hunk { padding: 0 8px; color: var(--muted); background: color-mix(in srgb, var(--accent) 7%, var(--surface)); white-space: pre; min-width: max-content; }
.cb-diff-line { display: flex; white-space: pre; min-width: max-content; min-height: 20px; }
.cb-diff-line.add { background: color-mix(in srgb, var(--ok) 16%, var(--surface)); }
.cb-diff-line.del { background: color-mix(in srgb, var(--bad) 14%, var(--surface)); }
.cb-diff-line.empty { background: color-mix(in srgb, var(--line) 35%, var(--surface)); }
.cb-dnum { flex: none; width: var(--num-w); padding: 0 6px; text-align: right; color: var(--muted); user-select: none; }
.cb-dmark { flex: none; width: 2ch; text-align: center; color: var(--muted); user-select: none; }
.cb-dtext { padding-right: 10px; }
/* Side by side: two equal halves of the box; a long line wraps inside its half (a row as wide as its longest line put
   the new side off screen). */
.split .cb-diff-row { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 1fr); }
.split .cb-diff-hunk { min-width: 0; }
.split .cb-diff-line.half { min-width: 0; white-space: pre-wrap; border-right: 1px solid var(--line); }
.split .cb-diff-line.half .cb-dtext { flex: 1 1 0; min-width: 0; overflow-wrap: anywhere; }
@media (max-width: 759.98px) {
  .cb-diff-modes button { min-height: 44px; }
}
.cb :deep(.hljs-comment), .cb :deep(.hljs-quote) { color: var(--cb-hl-comment); font-style: italic; }
.cb :deep(.hljs-keyword), .cb :deep(.hljs-selector-tag), .cb :deep(.hljs-literal), .cb :deep(.hljs-doctag) { color: var(--cb-hl-keyword); }
.cb :deep(.hljs-string), .cb :deep(.hljs-regexp), .cb :deep(.hljs-addition), .cb :deep(.hljs-meta .hljs-string) { color: var(--cb-hl-string); }
.cb :deep(.hljs-number), .cb :deep(.hljs-symbol), .cb :deep(.hljs-bullet), .cb :deep(.hljs-variable), .cb :deep(.hljs-template-variable) { color: var(--cb-hl-number); }
.cb :deep(.hljs-title), .cb :deep(.hljs-section), .cb :deep(.hljs-name), .cb :deep(.hljs-selector-id), .cb :deep(.hljs-selector-class) { color: var(--cb-hl-title); }
.cb :deep(.hljs-attr), .cb :deep(.hljs-attribute), .cb :deep(.hljs-property), .cb :deep(.hljs-type), .cb :deep(.hljs-built_in) { color: var(--cb-hl-attr); }
.cb :deep(.hljs-meta), .cb :deep(.hljs-tag) { color: var(--cb-hl-meta); }
.cb :deep(.hljs-deletion) { color: var(--cb-hl-deletion); }
</style>
