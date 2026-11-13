<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue';
import type { GuardrailReport, GuardrailResult, GuardrailState, YawbleApi } from '../../../shared/api';
import { errorText, resolveApi, when } from '../api';
import LoadingNote from './LoadingNote.vue';
import BusyLabel from './BusyLabel.vue';

// The in-app guardrail check: runs the escape battery in a sandbox under this agent's
// effective policy (or the global policy alone when no member is given) and shows each probe as BLOCKED or ALLOWED
// with its evidence. The core answers one request at a time, so the panel starts the check and polls.
// `runLabel`: the Run button's own words (the Guardrails page says "Run check"); otherwise the long ones.
const props = defineProps<{ team?: string | null; member?: string | null; api?: YawbleApi; pollMs?: number; runLabel?: string }>();
const api = resolveApi(props.api);

const state = ref<GuardrailState | null>(null);
const error = ref<string | null>(null);
const stopping = ref(false);
let timer: ReturnType<typeof setTimeout> | null = null;
let disposed = false;

const subject = computed(() => (props.team && props.member ? { team: props.team, member: props.member } : {}));
const running = computed(() => state.value?.state === 'running');
// The core says why the check cannot run on this install (no launcher, no probe): no point offering Run.
const unavailable = computed(() => state.value?.state === 'none' && !!state.value.error);
const report = computed<GuardrailReport | null>(() => (state.value?.state === 'done' ? state.value.report : null));

const progressLine = computed(() => {
  const s = state.value;
  if (s?.state !== 'running') return null;
  if (s.phase === 'running') return 'Running the probes in the sandbox…';
  return s.position != null
    ? `Queued · position ${s.position} in the global run queue · waiting for a run slot`
    : 'Queued · waiting to start';
});

const verdictLine = computed(() => {
  const r = report.value;
  if (!r) return null;
  if (r.run?.end === 'stopped') return 'STOPPED';
  return r.passed ? 'PASSED' : 'FAILED';
});

// Unexpected results first, then the rest in the core's order.
const results = computed<GuardrailResult[]>(() => {
  const all = report.value?.results ?? [];
  return [...all.filter((r) => !r.asExpected && r.expected !== null), ...all.filter((r) => r.asExpected || r.expected === null)];
});

async function load() {
  try {
    state.value = await api.call<GuardrailState>('guardrail.get', subject.value);
    error.value = null;
  } catch (e) {
    error.value = errorText(e);
  }
  schedule();
}

function schedule() {
  if (timer) clearTimeout(timer);
  timer = null;
  if (!disposed && running.value) timer = setTimeout(load, props.pollMs ?? 1000);
}

// Run was clicked and the core has not said the check is running yet: the button is held with a spinner.
const starting = ref(false);
async function start() {
  if (starting.value) return;
  error.value = null;
  starting.value = true;
  try {
    await api.call('guardrail.start', subject.value);
    await load();
  } catch (e) {
    error.value = errorText(e);
  } finally {
    starting.value = false;
  }
}

async function stop() {
  if (stopping.value) return;
  error.value = null;
  stopping.value = true;
  try {
    await api.call('guardrail.stop', subject.value);
    await load();
  } catch (e) {
    error.value = errorText(e);
  } finally {
    stopping.value = false;
  }
}

onMounted(load);
onBeforeUnmount(() => {
  disposed = true;
  if (timer) clearTimeout(timer);
});

defineExpose({ load });
</script>

<template>
  <section class="guardrail" data-test="guardrail-panel">
    <p class="muted small">
      Runs a probe in a sandbox under {{ member ? `${member}'s effective policy` : 'the global policy alone' }}, through the
      same launcher and per-run gateway as its runs, with no model credential. It tries to write outside the allowed folders,
      read the app's data, the Grok home and ~/.ssh, reach the network directly, CONNECT to hosts the gateway doesn't allow,
      and reach host processes. {{ member ? 'It takes a global run slot, like a run.' : '' }}
    </p>
    <div class="row">
      <button class="primary" data-test="guardrail-run" :disabled="running || unavailable || starting" :aria-busy="starting" @click="start">
        <BusyLabel :busy="starting" text="Starting…">{{ runLabel ?? (report || state?.state === 'failed' ? 'Run the check again' : 'Run the guardrail check') }}</BusyLabel></button>
      <button v-if="running" class="danger" data-test="guardrail-stop" :disabled="stopping" :aria-busy="stopping" @click="stop"><BusyLabel :busy="stopping" text="Stopping…">■ Stop</BusyLabel></button>
      <span v-if="progressLine" class="muted" data-test="guardrail-progress">{{ progressLine }}</span>
    </div>
    <LoadingNote v-if="!state && !error" text="Loading the last check…" />
    <p v-if="error" class="error" data-test="guardrail-error">{{ error }}</p>
    <p v-if="state?.state === 'failed'" class="error" data-test="guardrail-failed">The check did not run: {{ state.error }}</p>
    <p v-if="state?.state === 'none' && state.error" class="error" data-test="guardrail-unavailable">Not available: {{ state.error }}</p>
    <p v-if="state?.state === 'none' && !state.error" class="muted" data-test="guardrail-none">No check has run yet.</p>

    <template v-if="report">
      <p class="summary" :class="verdictLine === 'PASSED' ? 'ok' : 'error'" data-test="guardrail-summary">
        <strong data-test="guardrail-verdict">{{ verdictLine }}</strong>
        · {{ report.counts.blocked }} blocked · {{ report.counts.allowed }} allowed · {{ report.counts.unexpected }} unexpected
        <span v-if="report.counts.skipped"> · {{ report.counts.skipped }} skipped</span>
        <span class="muted"> · {{ when(report.startedAt) }} · {{ (report.durationMs / 1000).toFixed(1) }} s · {{ report.sandbox.backend }}
          <template v-if="report.run"> · run {{ report.run.end }} on {{ report.run.backend }}</template></span>
      </p>
      <p v-if="report.sandbox.refusal" class="error small" data-test="guardrail-refusal">The sandbox refused: {{ report.sandbox.refusal }}</p>
      <p v-if="report.loosened.length" class="warn small" data-test="guardrail-loosened">Loosened for this agent: {{ report.loosened.join('; ') }}</p>
      <ul v-if="report.notes.length" class="small notes" data-test="guardrail-notes">
        <li v-for="n in report.notes" :key="n">{{ n }}</li>
      </ul>
      <table v-if="results.length" class="small" data-test="guardrail-results">
        <thead><tr><th>Probe</th><th>Verdict</th><th>Evidence</th></tr></thead>
        <tbody>
          <tr v-for="r in results" :key="r.id" :class="{ bad: !r.asExpected && r.expected !== null }" :data-test="`guardrail-result-${r.id}`">
            <td class="mono">{{ r.id }}<div class="muted">{{ r.category }}</div></td>
            <td>
              <span class="verdict" :class="r.verdict.toLowerCase()" data-test="guardrail-result-verdict">{{ r.verdict }}</span>
              <div v-if="!r.asExpected && r.expected" class="error">expected {{ r.expected }}</div>
              <div v-if="r.sandboxOnly" class="muted" title="Created inside the sandbox's own empty mask only; it never reached your files">sandbox only</div>
              <div v-if="r.logged === true" class="muted">logged by the gateway</div>
              <div v-if="r.logged === false" class="error">not in the gateway log</div>
            </td>
            <td>{{ r.evidence }}</td>
          </tr>
        </tbody>
      </table>
    </template>
  </section>
</template>

<style scoped>
.guardrail > * { margin: 0 0 8px; }
.small { font-size: 12px; }
.summary { font-size: 14px; }
.notes { padding-left: 18px; }
table { width: 100%; border-collapse: collapse; }
th, td { text-align: left; padding: 3px 6px; border-bottom: 1px solid var(--line-soft); vertical-align: top; }
td.mono { white-space: nowrap; }
tr.bad { background: color-mix(in srgb, var(--bad) 7%, var(--surface)); }
.verdict { font-weight: 600; font-size: 11px; padding: 1px 6px; border-radius: 8px; background: var(--chip); }
.verdict.blocked { background: color-mix(in srgb, var(--ok) 12%, var(--surface)); color: var(--ok); }
</style>
