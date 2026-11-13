<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue';
import { renderMarkdown } from '../markdown';
import { parseTranscript } from '../transcriptFormat';

// A run's output for a person to read (the terminal's Formatted view): what the agent said, each tool call and what
// came back, how the run ended. Follows the end while the person is at the bottom.
const props = defineProps<{ text: string }>();
const entries = computed(() => parseTranscript(props.text));
const box = ref<HTMLElement | null>(null);
let atBottom = true;

function onScroll() {
  const el = box.value;
  if (el) atBottom = el.scrollHeight - el.scrollTop - el.clientHeight < 40;
}
watch(entries, () => { if (atBottom) void nextTick(() => { if (box.value) box.value.scrollTop = box.value.scrollHeight; }); });
</script>

<template>
  <div ref="box" class="transcript" data-test="transcript" @scroll="onScroll">
    <p v-if="!entries.length" class="empty">Nothing yet. A run's output appears here as it happens.</p>
    <template v-for="(e, i) in entries" :key="i">
      <div v-if="e.kind === 'text'" class="said md" v-html="renderMarkdown(e.body)"></div>
      <details v-else-if="e.kind === 'thinking'" class="thinking"><summary>Thinking</summary><pre>{{ e.body }}</pre></details>
      <div v-else-if="e.kind === 'tool'" class="tool" data-test="transcript-tool"><span class="tname">{{ e.title }}</span><code>{{ e.body }}</code></div>
      <pre v-else-if="e.kind === 'result'" class="result" :class="{ error: e.error }">{{ e.body }}</pre>
      <div v-else-if="e.kind === 'start' || e.kind === 'end'" class="mark" :class="{ error: e.error }"><b>{{ e.title }}</b> <span>{{ e.body }}</span></div>
      <div v-else-if="e.kind === 'note'" class="note"><span class="tname">{{ e.title }}</span> {{ e.body }}</div>
      <pre v-else class="plain">{{ e.body }}</pre>
    </template>
  </div>
</template>

<style scoped>
.transcript { height: 100%; overflow: auto; padding: 10px 12px; background: #101418; color: #d8dee9; font-size: 13px; line-height: 1.45; box-sizing: border-box; }
.empty { color: #7d8898; }
.said { color: #eef2f7; margin: 8px 0; }
.said :deep(p) { margin: 4px 0; }
.said :deep(code) { background: #222b36; padding: 0 4px; border-radius: 3px; }
.said :deep(pre) { background: #1b222c; padding: 6px 8px; border-radius: 4px; overflow: auto; }
.said :deep(a) { color: #8ab4ff; }
.said :deep(table) { border-collapse: collapse; }
.said :deep(td), .said :deep(th) { border: 1px solid #36404e; padding: 2px 6px; }
.thinking { margin: 4px 0; color: #8a95a7; }
.thinking summary { cursor: pointer; font-size: 12px; }
.thinking pre { white-space: pre-wrap; font-size: 12px; margin: 4px 0 4px 12px; }
.tool { display: flex; gap: 8px; align-items: baseline; margin: 6px 0 2px; font-size: 12.5px; }
.tname { color: #f2b84b; font-weight: 600; white-space: nowrap; }
.tool code { color: #c9d4e3; white-space: pre-wrap; word-break: break-word; font-family: ui-monospace, Consolas, monospace; }
.result { margin: 0 0 6px 14px; padding: 4px 8px; border-left: 2px solid #2c3542; color: #9aa6b8; white-space: pre-wrap; word-break: break-word; font-size: 12px; max-height: 260px; overflow: auto; }
.result.error { border-left-color: #c9473d; color: #ffb4b4; }
.mark { margin: 8px 0; padding: 4px 8px; background: #1b222c; border-radius: 4px; font-size: 12px; color: #9aa6b8; }
.mark b { color: #d8dee9; }
.mark.error b { color: #ff8a8a; }
.note { margin: 4px 0; font-size: 12.5px; color: #c9d4e3; }
.plain { white-space: pre-wrap; word-break: break-word; color: #c9d4e3; margin: 4px 0; font-size: 12.5px; }
</style>
