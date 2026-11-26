<script setup lang="ts">
import { computed } from 'vue';
import type { ItemTime } from '../../../shared/api';
import { formatShort } from '../timeline/time';
import { timeBar } from './page';

/** A thin stacked bar of where a deliverable's time went, in the Timeline dialog's category colours; hover names them. */
const props = defineProps<{ time: ItemTime | null | undefined }>();
const segs = computed(() => timeBar(props.time));
const says = computed(() => segs.value.map((s) => `${s.label} ${formatShort(s.ms)} (${s.pct}%)`).join(' · '));
</script>

<template>
  <span v-if="segs.length" class="tbar" data-test="deliverable-time-bar" :title="says" role="img" :aria-label="`Where the time went: ${says}`">
    <span v-for="s in segs" :key="s.category" class="seg" :class="s.category" data-test="deliverable-time-seg" :data-category="s.category"
      :style="{ flexGrow: s.ms, background: s.color }" :title="`${s.label}: ${formatShort(s.ms)} (${s.pct}%)`" />
  </span>
</template>

<style scoped>
.tbar { display: flex; height: 4px; width: 100%; min-width: 48px; max-width: 120px; border-radius: 2px; overflow: hidden; gap: 1px; background: var(--line-soft); }
.seg { flex-basis: 0; min-width: 2px; }
/* Stalled shares Agent working's colour (as the Timeline): striped. */
.seg.stalled { background-image: repeating-linear-gradient(135deg, transparent 0 2px, color-mix(in srgb, var(--surface) 55%, transparent) 2px 4px) !important; }
</style>
