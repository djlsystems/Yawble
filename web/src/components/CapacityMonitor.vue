<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue';
import { storeToRefs } from 'pinia';
import { useRouter } from 'vue-router';
import { useCapacityStore } from '../stores/capacity';
import { useConsoleStore } from '../stores/console';
import { asTeamId } from '../api/types';
import { gauge, gaugeFill, staleness } from '../lib/capacity';
import CapacityPanel from './CapacityPanel.vue';

/**
 * THE ACTIVITY MONITOR on the app bar: a small gauge whose colour is the worst of memory in use,
 * memory pressure and CPU pressure, and whose tooltip names the figure that set it. Clicking it
 * opens `CapacityPanel`.
 *
 * It reads ONE route (`GET /api/capacity`, people only) once, and the live connection's
 * `capacityChanged` after that. The clock below only ages the figures already held - it fetches
 * nothing - so a Host that stops answering shows as stale figures, never as fresh ones.
 */
const capacity = useCapacityStore();
const { latest, history, intervalSeconds } = storeToRefs(capacity);
const board = useConsoleStore();
const router = useRouter();

const now = ref(Date.now());
let ticker: ReturnType<typeof setInterval> | 0 = 0;

onMounted(() => {
  if (!capacity.loaded) void capacity.load();
  ticker = setInterval(() => (now.value = Date.now()), 1000);
});

onUnmounted(() => {
  if (ticker) clearInterval(ticker);
});

const age = computed(() => staleness(latest.value, intervalSeconds.value, now.value));
const state = computed(() => gauge(latest.value, age.value));

/** The arc: a 270-degree track, filled to memory in use against its limit. */
const Radius = 8;
const Track = 2 * Math.PI * Radius * 0.75;
const fill = computed(() => (gaugeFill(latest.value) / 100) * Track);

function openTeam(team: string) {
  board.setActiveTeam(asTeamId(team));
  if (router && router.currentRoute.value.path !== '/console') void router.push('/console');
}
</script>

<template>
  <q-btn
    flat
    dense
    round
    class="capacity-gauge q-ml-xs"
    :class="{
      'capacity-gauge-green': state.level === 'green',
      'capacity-gauge-amber': state.level === 'amber',
      'capacity-gauge-red': state.level === 'red',
      'capacity-gauge-unmeasured': state.level === 'unmeasured',
      'capacity-gauge-stale': state.level === 'stale',
    }"
    :data-level="state.level"
    data-test="capacity-gauge"
    :aria-label="`Activity monitor: ${state.reason}`"
  >
    <svg width="22" height="22" viewBox="0 0 22 22" aria-hidden="true">
      <circle
        class="capacity-gauge-track"
        cx="11"
        cy="11"
        :r="Radius"
        :stroke-dasharray="`${Track} 100`"
        transform="rotate(135 11 11)"
      />
      <circle
        class="capacity-gauge-fill"
        cx="11"
        cy="11"
        :r="Radius"
        :stroke-dasharray="`${fill} 100`"
        transform="rotate(135 11 11)"
      />
      <circle class="capacity-gauge-dot" cx="11" cy="11" r="2.5" />
    </svg>
    <q-tooltip>{{ state.reason }}</q-tooltip>

    <q-menu anchor="bottom right" self="top right" class="above-concierge">
      <CapacityPanel
        :sample="latest"
        :history="history"
        :age-seconds="age.ageSeconds"
        :stale="age.stale"
        @team="openTeam"
      />
    </q-menu>
  </q-btn>
</template>

<style scoped>
.capacity-gauge circle {
  fill: none;
  stroke-width: 3;
  stroke-linecap: round;
}

.capacity-gauge-track {
  stroke: currentColor;
  opacity: 0.3;
}

.capacity-gauge .capacity-gauge-dot {
  fill: currentColor;
  stroke: none;
}

.capacity-gauge-green {
  --gauge: var(--q-positive);
}

.capacity-gauge-amber {
  --gauge: var(--q-warning);
}

.capacity-gauge-red {
  --gauge: var(--q-negative);
}

.capacity-gauge-unmeasured,
.capacity-gauge-stale {
  --gauge: currentColor;
}

.capacity-gauge-fill {
  stroke: var(--gauge);
}

.capacity-gauge .capacity-gauge-dot {
  fill: var(--gauge);
}

.capacity-gauge-stale svg {
  opacity: 0.5;
}
</style>
