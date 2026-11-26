<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue';
import type { TeamSummary, YawbleApi } from '../../../../shared/api';
import { errorText } from '../../api';
import { confirmDialog } from '../../confirm';
import { readOnly } from '../../remoteAccess';
import {
  fakeTriggerFired, jsonProblem, liveConfirm, workflowsApi, settingFromText, settingToText, teamBindingChoices, type BindingChoice,
} from '../../workflows/api';
import type { WorkflowDocument, TestRunResult } from '../../workflows/types';
import BusyLabel from '../BusyLabel.vue';
import WorkflowRunView from './WorkflowRunView.vue';

// Test run: a team, the workflow's settings as a form, one binding per requires entry (over the team's
// integrations), and an input (a fake TriggerFired now, or a sample). Dry run is on by default; the first live run of a
// workflow asks first, and the answer is remembered per workflow key. Calls workflows.testRun and shows the run view. doc is
// the document of ersion (the one that runs), so the form, the bindings and the fake input match what the RPC runs.
const props = defineProps<{ api: YawbleApi; workflow: { id: number; key: string }; version?: number; doc: WorkflowDocument | null; teams: readonly TeamSummary[] }>();
const emit = defineEmits<{ ran: [r: TestRunResult] }>();
const s = workflowsApi(props.api);

const teamId = ref<number | null>(props.teams[0]?.id ?? null);
const settings = computed(() => (Array.isArray(props.doc?.settings) ? props.doc!.settings! : []));
const requires = computed(() => (Array.isArray(props.doc?.requires) ? props.doc!.requires! : []));
const values = reactive<Record<string, string>>({});
watch(settings, (list) => {
  for (const st of list) if (!(st.name in values)) values[st.name] = settingToText(st.type, st.default);
}, { immediate: true });
const bindings = reactive<Record<string, string>>({});
const choices = ref<BindingChoice[]>([]);
function fillBindings() {
  for (const r of requires.value) {
    const of = choicesFor(r.kind);
    if (!of.some((c) => c.name === bindings[r.name])) bindings[r.name] = of.length === 1 ? of[0]!.name : '';
  }
}
watch(teamId, async (id) => {
  choices.value = id === null ? [] : await teamBindingChoices(props.api, id);
  fillBindings();
}, { immediate: true });
// A save (or another version picked) changes the document that runs: its new requires entries get a binding too.
watch(requires, fillBindings);
/** The team's integrations of the entry's kind; all of them when none is of that kind (the core checks the binding). */
function choicesFor(kind: string): BindingChoice[] {
  const same = choices.value.filter((c) => c.kind === kind);
  return same.length ? same : choices.value;
}

// A test run never sends a tell: a tell that waits takes the made-up reply typed here (JSON when it has a reply schema,
// else text); one left empty gets no reply. TellResult.outcome is dryRun either way.
const waitingTells = computed(() => (Array.isArray(props.doc?.steps) ? props.doc!.steps! : [])
  .filter((st) => st && st.type === 'tell' && st.wait === true)
  .map((st) => ({ id: st.id, reply: typeof st.reply === 'string' && st.reply ? st.reply : null })));
const replies = reactive<Record<string, string>>({});
/** The replies to send: JSON where the text is JSON (an object or a list), else the text. */
function tellReplies(): Record<string, unknown> | undefined {
  const out: Record<string, unknown> = {};
  for (const t of waitingTells.value) {
    const text = replies[t.id] ?? '';
    if (!text.trim()) continue;
    const trimmed = text.trim();
    if (trimmed.startsWith('{') || trimmed.startsWith('[')) {
      try { out[t.id] = JSON.parse(trimmed); continue; } catch { /* sent as text: the core says what is wrong with it */ }
    }
    out[t.id] = text;
  }
  return Object.keys(out).length ? out : undefined;
}

const inputMode = ref<'fake' | 'sample'>('fake');
const sample = ref('{\n  \n}');
const dryRun = ref(true);
const busy = ref(false);
const error = ref<string | null>(null);
const result = ref<TestRunResult | null>(null);

const sampleProblem = computed(() => (inputMode.value === 'sample' ? jsonProblem(sample.value) : null));
const canRun = computed(() => !readOnly.value && teamId.value !== null && !sampleProblem.value && !busy.value);

async function run() {
  if (!canRun.value || teamId.value === null) return;
  error.value = null;
  if (!dryRun.value && !liveConfirm.done(props.workflow.key)) {
    const ok = await confirmDialog({
      message: `Run ${props.workflow.key} live? A live run really sends: its integration calls, emails and texts happen, and its files are written. Later live runs of this workflow will not ask again.`,
      okLabel: 'Run live', danger: true,
    });
    if (!ok) return;
    liveConfirm.remember(props.workflow.key);
  }
  const set: Record<string, unknown> = {};
  for (const st of settings.value) {
    const text = values[st.name] ?? '';
    if (text === '' && 'default' in st) continue;
    set[st.name] = settingFromText(st.type, text);
  }
  const bind: Record<string, string> = {};
  for (const r of requires.value) if (bindings[r.name]) bind[r.name] = bindings[r.name]!;
  const made = tellReplies();
  busy.value = true;
  try {
    const r = await s.testRun({
      workflow: props.workflow.id, ...(props.version !== undefined ? { version: props.version } : {}),
      input: inputMode.value === 'fake' ? fakeTriggerFired(props.doc) : JSON.parse(sample.value),
      settings: set, bindings: bind, teamId: teamId.value, dryRun: dryRun.value,
      ...(made ? { tellReplies: made } : {}),
    });
    result.value = r;
    emit('ran', r);
  } catch (e) {
    error.value = errorText(e);
  } finally {
    busy.value = false;
  }
}
</script>

<template>
  <form class="test-run" data-test="workflow-test-run" @submit.prevent="run">
    <label class="field">Team
      <select v-model="teamId" data-test="test-team">
        <option v-for="t in teams" :key="t.id" :value="t.id">{{ t.name }}</option>
      </select>
    </label>
    <p v-if="!teams.length" class="muted">A test run needs a team: make one first.</p>

    <fieldset v-if="settings.length" data-test="test-settings">
      <legend>Settings</legend>
      <label v-for="st in settings" :key="st.name" class="field" :data-test="`test-setting-${st.name}`">
        <span>{{ st.label || st.name }} <code class="muted">{{ st.type }}</code></span>
        <textarea v-if="st.type.startsWith('list<')" v-model="values[st.name]" rows="3" placeholder="one per line" />
        <select v-else-if="st.type === 'bool'" v-model="values[st.name]"><option value="true">true</option><option value="false">false</option></select>
        <input v-else v-model="values[st.name]" :type="st.type === 'int' || st.type === 'float' ? 'number' : 'text'" :placeholder="'default' in st ? String(st.default) : ''" />
      </label>
    </fieldset>

    <fieldset v-if="requires.length" data-test="test-bindings">
      <legend>Integrations</legend>
      <label v-for="r in requires" :key="r.name" class="field" :data-test="`test-binding-${r.name}`">
        <span><code>{{ r.name }}</code> <span class="muted">({{ r.kind }})</span></span>
        <select v-model="bindings[r.name]">
          <option value="">(not bound)</option>
          <option v-for="c in choicesFor(r.kind)" :key="`${c.kind}:${c.name}`" :value="c.name">{{ c.name }}</option>
        </select>
      </label>
    </fieldset>

    <fieldset v-if="waitingTells.length" data-test="test-tell-replies">
      <legend>Tells that wait</legend>
      <p class="muted small">A test run does not send a tell. Type the reply each one gets, as if the agent had answered (left empty: no reply).</p>
      <label v-for="t in waitingTells" :key="t.id" class="field" :data-test="`test-tell-reply-${t.id}`">
        <span>Made-up reply for <code>{{ t.id }}</code> <span class="muted">{{ t.reply ? `(JSON of ${t.reply})` : '(text)' }}</span></span>
        <textarea v-model="replies[t.id]" class="mono" rows="3" :placeholder="t.reply ? '{ }' : 'The closing summary'" data-test="test-tell-reply" />
      </label>
    </fieldset>

    <fieldset data-test="test-input">
      <legend>Input</legend>
      <label class="radio"><input v-model="inputMode" type="radio" value="fake" data-test="test-input-fake" /> Use a fake TriggerFired now</label>
      <label class="radio"><input v-model="inputMode" type="radio" value="sample" data-test="test-input-sample" /> A sample input (JSON)</label>
      <textarea v-if="inputMode === 'sample'" v-model="sample" class="mono" rows="5" data-test="test-sample" />
      <p v-if="sampleProblem" class="error small">{{ sampleProblem }}</p>
    </fieldset>

    <div class="row">
      <label class="radio"><input v-model="dryRun" type="checkbox" data-test="test-dry-run" /> Dry run (nothing is sent; reads are real)</label>
      <span class="grow" />
      <button v-if="!readOnly" type="submit" class="primary" :disabled="!canRun" :aria-busy="busy" data-test="test-run-go">
        <BusyLabel :busy="busy" text="Running…">{{ dryRun ? 'Test run' : 'Run live' }}</BusyLabel>
      </button>
    </div>
    <p v-if="error" class="error" data-test="test-error">{{ error }}</p>
    <WorkflowRunView v-if="result" :key="result.runId" :run="result" :doc="doc" />
  </form>
</template>

<style scoped>
.test-run { display: flex; flex-direction: column; gap: 8px; font-size: 13px; }
fieldset { border: 1px solid var(--line); border-radius: 6px; padding: 6px 10px 8px; display: flex; flex-direction: column; gap: 6px; margin: 0; }
legend { font-size: 12px; color: var(--muted); padding: 0 4px; }
.field { display: flex; flex-direction: column; gap: 2px; }
.radio { display: flex; align-items: center; gap: 6px; }
.small { font-size: 12px; margin: 0; }
</style>
