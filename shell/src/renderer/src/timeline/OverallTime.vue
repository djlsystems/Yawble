<script setup lang="ts">
import { computed, onBeforeUnmount, watch } from 'vue';
import type { ItemTime } from '../../../shared/api';
import { formatDuration, liveOverallMs, nowRef, openTimeline, overallSays, subscribeNow, type TimelineTarget } from './time';

// B086: an item's overall time, one quiet line: a clock mark and "1h 12m 05s". An open item ticks on the ONE shared
// 1 s timer (time.ts), subscribed only while it is open and shown. Clicking it opens the timeline dialog for the item;
// it sits inside a card or row that is itself a button, so it stops the click there and is a focusable role=button.
const props = defineProps<{ time: ItemTime; target: TimelineTarget }>();

let off: (() => void) | null = null;
watch(() => props.time.open, (open) => {
  if (open && !off) off = subscribeNow();
  else if (!open && off) { off(); off = null; }
}, { immediate: true });
onBeforeUnmount(() => { off?.(); off = null; });

const now = nowRef();
const ms = computed(() => liveOverallMs(props.time, props.time.open ? now.value : 0));
const says = computed(() => overallSays(props.time, props.time.open ? now.value : 0));
const open = () => openTimeline(props.target);
</script>

<template>
  <span class="ot" :class="{ open: time.open }" role="button" tabindex="0" :title="`${says}. Open its timeline.`" :aria-label="`${says}. Open its timeline.`"
    data-test="overall-time" :data-open="time.open ? 'true' : 'false'"
    @click.stop.prevent="open" @keydown.enter.stop.prevent="open" @keydown.space.stop.prevent="open">
    <svg class="ot-mark" viewBox="0 0 12 12" aria-hidden="true"><circle cx="6" cy="6" r="4.6" fill="none" stroke="currentColor" stroke-width="1.3" /><path d="M6 3.4V6l1.8 1.2" fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" /></svg>
    <span class="ot-v" data-test="overall-time-value">{{ formatDuration(ms) }}</span>
  </span>
</template>

<style scoped>
.ot { display: inline-flex; align-items: center; gap: 3px; white-space: nowrap; font-size: 11px; color: var(--muted); font-variant-numeric: tabular-nums;
  cursor: pointer; border-radius: 4px; padding: 0 2px; }
.ot:hover { color: var(--ink-soft); text-decoration: underline; }
.ot:focus-visible { outline: 2px solid var(--focus); outline-offset: 1px; }
.ot-mark { width: 11px; height: 11px; flex: none; }
</style>
