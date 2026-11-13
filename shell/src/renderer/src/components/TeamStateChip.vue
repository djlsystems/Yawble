<script setup lang="ts">
import { computed } from 'vue';
import type { TeamSummary } from '../../../shared/api';
import { teamState, teamStateLabel } from '../teamState';

// A team's state in words and colour (Running, Queued, Paused, Idle): the same chip on its card in the list and in its
// own page title. A running team's chip carries a small pulsing dot.
const props = defineProps<{ team: Pick<TeamSummary, 'running' | 'queued' | 'paused'> }>();
const state = computed(() => teamState(props.team));
</script>

<template>
  <span class="state-chip" :class="`is-${state}`" :data-state="state"><i v-if="state === 'running'" class="pulse" />{{ teamStateLabel[state] }}</span>
</template>

<style scoped>
.state-chip { --tone: var(--state-idle); display: inline-flex; align-items: center; gap: 5px; font-size: 11px; font-weight: 600; line-height: 1;
  padding: 3px 7px; border-radius: 999px; color: var(--tone); background: color-mix(in srgb, var(--tone) 12%, transparent); white-space: nowrap; }
.is-running { --tone: var(--state-running); }
.is-queued { --tone: var(--state-waiting); }
.is-paused { --tone: var(--state-blocked); }
.pulse { width: 6px; height: 6px; border-radius: 50%; background: var(--tone); animation: chip-pulse 1.6s ease-in-out infinite; }
@keyframes chip-pulse { 50% { opacity: .35; } }
@media (prefers-reduced-motion: reduce) { .pulse { animation: none; } }
</style>
