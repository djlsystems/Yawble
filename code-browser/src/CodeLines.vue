<script setup lang="ts">
import { computed, ref } from 'vue';
import type { RemovedMarker } from './types';
import type { LineDecoration } from './markers';

// The lines of a file: line numbers, the highlighted text (highlight.js's escaped HTML only), the recent-change
// highlight fading with age (--hl, 0..1), the faint gutter mark of a branch change, and thin red markers where lines
// were removed (hover: the removed text). Removal markers take no height, so every line stays LINE_HEIGHT tall.
const props = defineProps<{ lines: string[]; decorations: LineDecoration[]; removed: Map<number, RemovedMarker[]>; target: number | null }>();
const emit = defineEmits<{ scroll: [] }>();
const el = ref<HTMLElement | null>(null);
defineExpose({ el });

const removedTitle = (list: RemovedMarker[]) => `Removed:\n${list.map((r) => r.text).join('\n')}`;
const width = computed(() => `${String(props.lines.length).length + 1}ch`);
</script>

<template>
  <div ref="el" class="cb-lines" data-test="code-lines" :style="{ '--num-w': width }" @scroll="emit('scroll')">
    <div v-for="(html, i) in lines" :key="i" class="cb-line"
        :class="{ recent: (decorations[i + 1]?.strength ?? 0) > 0, branch: decorations[i + 1]?.branch, target: target === i + 1 }"
        :style="(decorations[i + 1]?.strength ?? 0) > 0 ? { '--hl': String(decorations[i + 1]!.strength.toFixed(2)) } : undefined"
        :data-line="i + 1" :data-test="`code-line-${i + 1}`">
      <span v-if="i === 0 && removed.get(0)" class="cb-removed top" data-test="code-removed" :title="removedTitle(removed.get(0)!)" />
      <span class="cb-num">{{ i + 1 }}</span><span class="cb-text" v-html="html" />
      <span v-if="removed.get(i + 1)" class="cb-removed" data-test="code-removed" :data-after="i + 1" :title="removedTitle(removed.get(i + 1)!)" />
    </div>
  </div>
</template>

<style scoped>
.cb-lines { overflow: auto; font: 12.5px/20px ui-monospace, 'Cascadia Code', Consolas, monospace; background: var(--surface); min-height: 0; flex: 1 1 0; }
.cb-line { position: relative; height: 20px; white-space: pre; display: flex; min-width: max-content; }
.cb-num { flex: none; width: var(--num-w); padding: 0 10px 0 6px; text-align: right; color: var(--muted); user-select: none; border-right: 2px solid transparent;
  position: sticky; left: 0; background: var(--surface); z-index: 1; }
.cb-text { padding-left: 10px; position: relative; z-index: 1; }
.cb-line.recent::before { content: ''; position: absolute; inset: 0; background: var(--state-running); opacity: calc(var(--hl) * .22); pointer-events: none; }
.cb-line.recent .cb-num { background: color-mix(in srgb, var(--state-running) 14%, var(--surface)); }
.cb-line.branch .cb-num { border-right-color: color-mix(in srgb, var(--ok) 45%, transparent); }
.cb-line.target .cb-num { color: var(--ink); font-weight: 600; }
.cb-removed { position: absolute; left: 0; right: 0; bottom: -1px; height: 2px; background: var(--bad); z-index: 2; cursor: help; }
.cb-removed.top { top: -1px; bottom: auto; }
.cb-removed::after { content: ''; position: absolute; left: 0; right: 0; top: -3px; bottom: -3px; } /* an easier hover target */
</style>
