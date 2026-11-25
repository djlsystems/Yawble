<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue';
import type { WorkflowRow } from '../../../shared/api';
import { statusLook } from './page';

/**
 * A workflow's status mark. While a run is on it (`working`) the mark breathes gently; when it completes the old
 * mark shrinks away and the check grows in (once). Under prefers-reduced-motion (read once, as the Monitor does) both
 * are off: the mark is still and simply changes.
 */
const props = defineProps<{ workflow: Pick<WorkflowRow, 'status' | 'state' | 'working'>; size?: number }>();

const reduced = typeof matchMedia === 'function' && (() => { try { return matchMedia('(prefers-reduced-motion: reduce)').matches; } catch { return false; } })();
const look = computed(() => statusLook(props.workflow));
const animated = computed(() => !reduced && props.workflow.working && look.value.open);
/** The mark it had before it ended, shown shrinking away for a moment while the new one grows in. */
const leaving = ref<string | null>(null);
let timer: ReturnType<typeof setTimeout> | null = null;
watch(() => look.value, (now, before) => {
  if (reduced || !before || !before.open || now.open) return;
  leaving.value = before.icon;
  if (timer) clearTimeout(timer);
  timer = setTimeout(() => { leaving.value = null; }, 700);
});
onBeforeUnmount(() => { if (timer) clearTimeout(timer); });
</script>

<template>
  <span class="wsi" :class="[look.status, { animated, morph: !!leaving }]" :style="{ '--tone': look.tone, '--size': `${size ?? 16}px` }"
    data-test="workflow-status-icon" :data-status="look.status" :data-animated="animated ? 'true' : 'false'" aria-hidden="true">
    <svg viewBox="0 0 16 16">
      <circle class="halo" cx="8" cy="8" r="7.2" />
      <path v-if="leaving" class="old" :d="leaving" />
      <path class="mark" :d="look.icon" />
    </svg>
  </span>
</template>

<style scoped>
.wsi { display: inline-grid; place-items: center; width: var(--size); height: var(--size); flex: none; color: var(--tone); }
svg { width: 100%; height: 100%; overflow: visible; }
path { fill: none; stroke: currentColor; stroke-width: 1.5; stroke-linecap: round; stroke-linejoin: round; transform-origin: 8px 8px; }
.running path.mark { fill: currentColor; }
.halo { fill: color-mix(in srgb, var(--tone) 14%, transparent); stroke: none; transform-origin: 8px 8px; }
.completed .halo { fill: color-mix(in srgb, var(--tone) 18%, transparent); }
.closed .halo, .expired .halo { fill: transparent; }
/* Someone is on it: the halo breathes, slowly. */
.animated .halo { animation: wsi-breathe 2.4s ease-in-out infinite; }
@keyframes wsi-breathe { 0%, 100% { transform: scale(.82); opacity: .55; } 50% { transform: scale(1.08); opacity: 1; } }
/* It just ended: the old mark shrinks away, the new one grows in. */
.morph .old { animation: wsi-out .45s ease-in forwards; }
.morph .mark { animation: wsi-in .55s cubic-bezier(.3, 1.4, .6, 1) both .15s; }
@keyframes wsi-out { to { transform: scale(.2) rotate(-40deg); opacity: 0; } }
@keyframes wsi-in { from { transform: scale(.2); opacity: 0; } to { transform: scale(1); opacity: 1; } }
@media (prefers-reduced-motion: reduce) { .animated .halo, .morph .old, .morph .mark { animation: none; } .morph .old { display: none; } }
</style>
