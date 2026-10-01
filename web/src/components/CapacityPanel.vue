<script setup lang="ts">
import { computed } from 'vue';
import type { CapacitySample, RunFigures } from '../api/types';
import {
  NotMeasured,
  ageWords,
  bytesWords,
  cpuWords,
  memoryWords,
  partyWords,
  pidsWords,
  pressureWords,
  runsWords,
  sparkline,
} from '../lib/capacity';

/**
 * THE ACTIVITY MONITOR'S DROPDOWN: the instance's figures in words, each with what it is measured
 * against. Every figure the Host could not read says "not measured"; none reads 0. Stale figures
 * say how old they are above everything else, because every line below is then that old too.
 */
const props = defineProps<{
  sample: CapacitySample | null;
  history: CapacitySample[];
  ageSeconds: number | null;
  stale: boolean;
}>();

const emit = defineEmits<{ team: [team: string] }>();

const SparkWidth = 160;
const SparkHeight = 28;

const memorySpark = computed(() =>
  sparkline(props.history.map((s) => s.memory.percentOfLimit), SparkWidth, SparkHeight),
);
const cpuSpark = computed(() => sparkline(props.history.map((s) => s.cpu.percentOfLimit), SparkWidth, SparkHeight));

/** The sparkline's hover: its range over the window, so a reader need not trace the line. */
function sparkTitle(values: (number | null)[], what: string): string {
  const measured = values.filter((value): value is number => value !== null);
  if (measured.length === 0) return `${what}: ${NotMeasured} over the last 10 minutes`;

  return `${what} over the last 10 minutes: low ${Math.round(Math.min(...measured))}%, high ${Math.round(Math.max(...measured))}%`;
}

const memoryTitle = computed(() => sparkTitle(props.history.map((s) => s.memory.percentOfLimit), 'Memory'));
const cpuTitle = computed(() => sparkTitle(props.history.map((s) => s.cpu.percentOfLimit), 'CPU'));

const memoryParts = computed(() => {
  const memory = props.sample?.memory;
  if (!memory) return '';

  return `processes ${bytesWords(memory.anonBytes)}, shared memory ${bytesWords(memory.shmemBytes)}, file cache ${bytesWords(memory.fileBytes)}`;
});

function cpuOfRun(run: RunFigures): string {
  return run.cpuPercent === null ? NotMeasured : `${Math.round(run.cpuPercent)}% of a CPU`;
}
</script>

<template>
  <div class="capacity-panel q-pa-md" data-test="capacity-panel">
    <div v-if="!sample" class="text-body2" data-test="capacity-none">No figures yet.</div>

    <template v-else>
      <div v-if="stale && ageSeconds !== null" class="capacity-stale q-mb-sm" role="status" data-test="capacity-stale">
        Figures are stale: last measured {{ ageWords(ageSeconds) }} ago. The Host may not be answering.
      </div>

      <section class="q-mb-sm" data-test="capacity-memory">
        <div class="text-subtitle2">Memory</div>
        <div class="row items-center no-wrap q-gutter-x-sm">
          <div class="col text-body2">{{ memoryWords(sample) }}</div>
          <svg
            class="capacity-spark"
            :width="SparkWidth"
            :height="SparkHeight"
            :viewBox="`0 0 ${SparkWidth} ${SparkHeight}`"
            role="img"
            :aria-label="memoryTitle"
          >
            <title>{{ memoryTitle }}</title>
            <polyline v-for="(points, i) in memorySpark" :key="i" :points="points" />
          </svg>
        </div>
        <div class="text-caption os-text-muted">{{ memoryParts }}</div>
        <div class="text-caption" data-test="capacity-memory-pressure">
          Pressure: {{ pressureWords(sample.memory.pressure, 'Memory') }}
        </div>
      </section>

      <section class="q-mb-sm" data-test="capacity-cpu">
        <div class="text-subtitle2">CPU</div>
        <div class="row items-center no-wrap q-gutter-x-sm">
          <div class="col text-body2">{{ cpuWords(sample) }}</div>
          <svg
            class="capacity-spark"
            :width="SparkWidth"
            :height="SparkHeight"
            :viewBox="`0 0 ${SparkWidth} ${SparkHeight}`"
            role="img"
            :aria-label="cpuTitle"
          >
            <title>{{ cpuTitle }}</title>
            <polyline v-for="(points, i) in cpuSpark" :key="i" :points="points" />
          </svg>
        </div>
        <div class="text-caption" data-test="capacity-cpu-pressure">
          Pressure: {{ pressureWords(sample.cpu.pressure, 'CPU') }}
        </div>
      </section>

      <section class="q-mb-sm" data-test="capacity-pids">
        <div class="text-subtitle2">Processes</div>
        <div class="text-body2">{{ pidsWords(sample) }}</div>
      </section>

      <section class="q-mb-sm" data-test="capacity-runs">
        <div class="text-subtitle2">Runs</div>
        <div class="text-body2">{{ runsWords(sample.runs) }}</div>
        <div v-if="sample.admission.holding" class="text-caption" data-test="capacity-admission">
          A member asking now would be {{ sample.admission.holding }}
        </div>
        <ul v-if="sample.runs.waiting.length" class="capacity-list text-caption">
          <li v-for="hold in sample.runs.waiting" :key="`${hold.team}/${hold.member}`">
            {{ hold.member }} on {{ hold.team }}: {{ hold.reason ?? 'waiting for a slot' }}
          </li>
        </ul>
      </section>

      <section class="q-mb-sm" data-test="capacity-lease">
        <div class="text-subtitle2">Heavy-work lease</div>
        <div v-if="!sample.heavyLease" class="text-body2">not available</div>
        <template v-else>
          <div class="text-body2">
            {{
              sample.heavyLease.holding.length
                ? `Held by ${sample.heavyLease.holding.map(partyWords).join(', ')}`
                : 'Nobody holds it'
            }}
            ({{ sample.heavyLease.holders }} at once)
          </div>
          <div class="text-caption">
            {{
              sample.heavyLease.queued.length
                ? `Queued: ${sample.heavyLease.queued.map((party, i) => `${i + 1}. ${partyWords(party)}`).join(', ')}`
                : 'Nobody is queued'
            }}
          </div>
        </template>
      </section>

      <section class="q-mb-sm" data-test="capacity-top-memory">
        <div class="text-subtitle2">Top runs by memory</div>
        <div v-if="!sample.topByMemory.length" class="text-caption">No runs</div>
        <ul v-else class="capacity-list text-caption">
          <li v-for="run in sample.topByMemory" :key="`${run.team}/${run.member}`">
            <a href="#" class="capacity-team" @click.prevent="emit('team', run.team)">{{ run.team }}</a>
            {{ run.member }}: {{ bytesWords(run.residentBytes) }}, {{ run.processes }} processes
          </li>
        </ul>
      </section>

      <section data-test="capacity-top-cpu">
        <div class="text-subtitle2">Top runs by CPU</div>
        <div v-if="!sample.topByCpu.length" class="text-caption">No runs</div>
        <ul v-else class="capacity-list text-caption">
          <li v-for="run in sample.topByCpu" :key="`${run.team}/${run.member}`">
            <a href="#" class="capacity-team" @click.prevent="emit('team', run.team)">{{ run.team }}</a>
            {{ run.member }}: {{ cpuOfRun(run) }}
          </li>
        </ul>
      </section>

      <div class="text-caption os-text-muted q-mt-sm">
        Measured {{ ageSeconds === null ? '' : `${ageWords(ageSeconds)} ago` }} from the container's own cgroup{{
          sample.cgroup ? ` (${sample.cgroup})` : `: ${NotMeasured}`
        }}.
      </div>
    </template>
  </div>
</template>

<style scoped>
.capacity-panel {
  width: 360px;
  max-width: 90vw;
}

.capacity-spark {
  flex: none;
}

.capacity-spark polyline {
  fill: none;
  stroke: var(--q-primary);
  stroke-width: 2;
  stroke-linejoin: round;
  stroke-linecap: round;
}

.capacity-stale {
  padding: 4px 8px;
  border-left: 3px solid var(--q-warning);
}

.capacity-list {
  margin: 2px 0 0;
  padding-left: 16px;
}

.capacity-team {
  color: inherit;
  font-weight: 600;
}
</style>
