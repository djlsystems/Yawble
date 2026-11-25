<script setup lang="ts">
import { computed, ref } from 'vue';
import type { YawbleApi } from '../../../../shared/api';
import { errorText } from '../../api';
import { pretty, runScript, scriptsApi, showValue, took, writtenFiles } from '../../scripts/api';
import type { RunDetail, ScriptDocument, TestRunResult } from '../../scripts/types';
import RunWaitLink from './RunWaitLink.vue';
import ScriptFlowchart from './ScriptFlowchart.vue';

// The run view: each step with its input, output, time and status, the run's log, and the files it wrote. Shown
// for a row of the Runs tab (scripts.run) and right after a Test run (scripts.testRun's answer). The Flowchart toggle
// shows the run's script version, read-only, with the steps it took highlighted and the step it failed at marked: doc
// is that version's document when the caller has it, else it is loaded (scripts.get with the run's version) over api.
const props = defineProps<{ run: RunDetail | TestRunResult; doc?: ScriptDocument | null; api?: YawbleApi }>();

const detail = computed(() => ('id' in props.run ? (props.run as RunDetail) : null));
const status = computed(() => props.run.status);
const steps = computed(() => props.run.steps ?? []);
const log = computed(() => (props.run as { log?: unknown[] }).log?.map((l) =>
  typeof l === 'string' ? { at: null as string | null, line: l } : { at: (l as { at?: string }).at ?? null, line: String((l as { line?: unknown }).line ?? '') }) ?? []);
const files = computed(() => writtenFiles(steps.value, props.run.outputs));
const open = ref<Set<number>>(new Set());
function toggle(seq: number) {
  const s = new Set(open.value);
  if (s.has(seq)) s.delete(seq); else s.add(seq);
  open.value = s;
}
const copied = ref<string | null>(null);
async function copy(path: string) {
  try { await navigator.clipboard.writeText(path); copied.value = path; } catch { copied.value = null; }
}
const time = (iso: string | null) => (iso ? new Date(iso).toLocaleTimeString() : '');

// ---- the flowchart of the run
const canChart = computed(() => !!props.doc || (!!props.api && !!detail.value));
const chartOpen = ref(false);
const chartText = ref<string | null>(props.doc ? pretty(props.doc) : null);
const chartError = ref<string | null>(null);
const chartRun = computed(() => ({ taken: [...new Set(steps.value.map((s) => s.stepId))], failed: props.run.failedStepId }));
async function toggleChart() {
  chartOpen.value = !chartOpen.value;
  if (!chartOpen.value || chartText.value !== null || !props.api || !detail.value) return;
  chartError.value = null;
  try {
    const v = await scriptsApi(props.api).get(detail.value.scriptId, detail.value.version);
    chartText.value = pretty(v.document);
  } catch (e) {
    chartError.value = errorText(e);
  }
}
</script>

<template>
  <div class="run-view" data-test="script-run-view">
    <div class="head">
      <span class="status" :data-status="status" data-test="run-status">{{ status }}</span>
      <RunWaitLink v-if="status === 'waiting'" :workflow="detail?.waitWorkflowId ?? null" />
      <template v-if="detail">
        <span><code>{{ runScript(detail) }}</code></span>
        <span class="muted">{{ detail.team }}<template v-if="detail.member"> / {{ detail.member }}</template></span>
        <span class="muted">{{ detail.trigger }}</span>
        <span v-if="detail.dryRun" class="chip">dry run</span>
        <span v-if="detail.test" class="chip">test</span>
        <span class="muted">{{ new Date(detail.startedAt).toLocaleString() }}<template v-if="detail.endedAt"> · {{ took(detail.startedAt, detail.endedAt) }}</template></span>
      </template>
      <span v-else class="muted" data-test="run-id">run {{ (run as TestRunResult).runId }}</span>
      <button v-if="canChart" type="button" class="mini" :aria-pressed="chartOpen" data-test="run-flowchart-toggle" @click="toggleChart">{{ chartOpen ? 'Hide flowchart' : 'Flowchart' }}</button>
    </div>
    <div v-if="chartOpen" class="chart" data-test="run-flowchart">
      <p v-if="chartError" class="error" data-test="run-flowchart-error">{{ chartError }}</p>
      <p v-else-if="chartText === null" class="muted">Loading the script…</p>
      <ScriptFlowchart v-else :model-value="chartText" readonly :run="chartRun" />
    </div>
    <p v-if="run.reason || run.failedStepId" class="error" data-test="run-reason">
      <template v-if="run.failedStepId">Step <code>{{ run.failedStepId }}</code>: </template>{{ run.reason }}
    </p>

    <h4>Steps</h4>
    <p v-if="!steps.length" class="muted">No steps ran.</p>
    <table v-else class="steps" data-test="run-steps">
      <thead><tr><th>#</th><th>Step</th><th>Type</th><th>Status</th><th>Time</th><th /></tr></thead>
      <tbody>
        <template v-for="s in steps" :key="s.seq">
          <tr data-test="run-step" :data-step="s.stepId" :class="{ failed: s.status === 'failed' }">
            <td>{{ s.seq }}</td><td><code>{{ s.stepId }}</code></td><td>{{ s.type }}</td>
            <td><span class="status" :data-status="s.status">{{ s.status }}</span></td>
            <td class="muted">{{ took(s.startedAt, s.endedAt) }}</td>
            <td><button type="button" class="mini" data-test="run-step-toggle" :aria-expanded="open.has(s.seq)" @click="toggle(s.seq)">{{ open.has(s.seq) ? 'Hide' : 'Input and output' }}</button></td>
          </tr>
          <tr v-if="open.has(s.seq)" class="io" data-test="run-step-io">
            <td colspan="6">
              <div class="io-grid">
                <div><h5>Input</h5><pre data-test="run-step-input">{{ showValue(s.input) }}</pre></div>
                <div><h5>Output</h5><pre data-test="run-step-output">{{ showValue(s.output) }}</pre></div>
              </div>
            </td>
          </tr>
        </template>
      </tbody>
    </table>

    <template v-if="run.outputs && Object.keys(run.outputs).length">
      <h4>Outputs</h4>
      <pre data-test="run-outputs">{{ showValue(run.outputs) }}</pre>
    </template>

    <h4>Files it wrote</h4>
    <p v-if="!files.length" class="muted" data-test="run-no-files">None.</p>
    <ul v-else class="files" data-test="run-files">
      <li v-for="f in files" :key="f" data-test="run-file">
        <a :href="`file:///${f.replace(/\\/g, '/').replace(/^\/+/, '')}`" :title="`${f} (click to copy the path)`" target="_blank" rel="noopener" @click.prevent="copy(f)"><code>{{ f }}</code></a>
        <span v-if="copied === f" class="muted small">path copied</span>
      </li>
    </ul>

    <h4>Log</h4>
    <p v-if="!log.length" class="muted">Nothing logged.</p>
    <pre v-else class="log" data-test="run-log"><template v-for="(l, i) in log" :key="i"><span v-if="l.at" class="muted">{{ time(l.at) }} </span>{{ l.line }}
</template></pre>
  </div>
</template>

<style scoped>
.run-view { display: flex; flex-direction: column; gap: 4px; font-size: 13px; min-width: 0; }
.head { display: flex; flex-wrap: wrap; gap: 10px; align-items: center; }
h4 { margin: 10px 0 2px; font-size: 12.5px; }
h5 { margin: 0 0 2px; font-size: 11.5px; color: var(--muted); font-weight: 600; }
.steps { border-collapse: collapse; width: 100%; }
.steps th { text-align: left; font-weight: 500; color: var(--muted); font-size: 12px; border-bottom: 1px solid var(--line); padding: 3px 6px; }
.steps td { padding: 3px 6px; border-bottom: 1px solid var(--line-soft); vertical-align: top; }
.steps tr.failed td { background: var(--bad-bg-soft); }
.io-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 8px; }
pre { margin: 0; padding: 6px 8px; background: var(--surface-sunken); border-radius: 5px; font-size: 12px; white-space: pre-wrap; overflow-wrap: anywhere; max-height: 260px; overflow: auto; }
.log { max-height: 220px; }
.status { font-size: 11.5px; padding: 1px 7px; border-radius: 9px; background: var(--pill-neutral-bg); color: var(--pill-neutral-ink); }
.status[data-status=succeeded], .status[data-status=done] { background: var(--pill-green-bg); color: var(--pill-green-ink); }
.status[data-status=failed] { background: var(--pill-red-bg); color: var(--pill-red-ink); }
.status[data-status=running] { background: var(--pill-blue-bg); color: var(--pill-blue-ink); }
.status[data-status=waiting] { background: var(--pill-violet-bg); color: var(--pill-violet-ink); }
.status[data-status=skipped], .status[data-status=interrupted] { background: var(--pill-yellow-bg); color: var(--pill-yellow-ink); }
.files { margin: 0; padding-left: 18px; }
.files a { color: var(--accent); }
.mini { font-size: 11.5px; padding: 1px 6px; }
.small { font-size: 11.5px; }
.chart { display: flex; flex-direction: column; min-height: 480px; }
</style>
