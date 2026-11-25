<script setup lang="ts">
/**
 * A running card's outline: a dotted line in the running colour along the card's edge, its dots moving clockwise.
 * The parent is positioned and hides its own border while running. The line sits just inside the parent's padding
 * edge (`inset` moves it), so a parent that clips its overflow keeps all four sides; `radius` is the line's corner
 * radius (the parent's inner radius less the 1.5px the line is set in). With reduced motion the dots
 * stand still.
 */
withDefaults(defineProps<{ inset?: string; radius?: number }>(), { inset: '0', radius: 6 });
</script>

<template>
  <span class="running-outline" :style="{ inset }" aria-hidden="true" data-test="running-outline">
    <svg><rect class="dots" :rx="radius" :ry="radius" /></svg>
  </span>
</template>

<style scoped>
.running-outline { position: absolute; pointer-events: none; z-index: 0; }
.running-outline svg { display: block; width: 100%; height: 100%; overflow: visible; }
/* A rect's path starts at its top-left corner and runs right along the top, so a falling dash offset moves the dots
   clockwise. One step of the offset is one dot-plus-gap period, so the loop has no jump. */
.dots { x: 1.5px; y: 1.5px; width: calc(100% - 3px); height: calc(100% - 3px); fill: none; stroke: var(--state-running);
  stroke-width: 2.25; stroke-linecap: round; stroke-dasharray: 0 7; animation: running-outline-march .7s linear infinite; }
@keyframes running-outline-march { to { stroke-dashoffset: -7; } }
@media (prefers-reduced-motion: reduce) { .dots { animation: none; } }
</style>
