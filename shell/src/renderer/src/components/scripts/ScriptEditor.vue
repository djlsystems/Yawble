<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import type { TeamSummary, YawbleApi } from '../../../../shared/api';
import { errorText } from '../../api';
import { readOnly } from '../../remoteAccess';
import { jsonProblem, pretty, scriptsApi, stepRange } from '../../scripts/api';
import type { Check, ScriptDocument, ScriptSaveResult, ScriptVersionRow } from '../../scripts/types';
import BusyLabel from '../BusyLabel.vue';
import JsonEditor from './JsonEditor.vue';
import ScriptOutline from './ScriptOutline.vue';
import ScriptTestRun from './ScriptTestRun.vue';

// The script editor: the JSON with highlighting, the check's problems by step (a click jumps to the step),
// the outline beside it, Versions, Save and Test run. The Flowchart tab is a placeholder (the `flowchart` slot);
// the JSON is the source of truth.
const props = defineProps<{
  api: YawbleApi; teams: readonly TeamSummary[];
  /** The script opened (null: a new one, from `initial`). */
  script: { id: number; key: string } | null;
  /** A new script's starting document. */
  initial?: ScriptDocument;
}>();
const emit = defineEmits<{ saved: [r: ScriptSaveResult]; close: [] }>();
const s = scriptsApi(props.api);

const text = ref(props.initial ? pretty(props.initial) : '');
const savedText = ref(text.value);
const version = ref<number | null>(null);
const versions = ref<ScriptVersionRow[]>([]);
const check = ref<Check | null>(null);
const current = ref(props.script);
const loading = ref(!!props.script);
const busy = ref<null | 'save' | 'check'>(null);
const error = ref<string | null>(null);
const savedNote = ref<string | null>(null);
const tab = ref<'json' | 'flowchart' | 'test'>('json');
const editor = ref<InstanceType<typeof JsonEditor> | null>(null);

const parseProblem = computed(() => jsonProblem(text.value));
const doc = computed<ScriptDocument | null>(() => (parseProblem.value ? null : (JSON.parse(text.value) as ScriptDocument)));
const dirty = computed(() => text.value !== savedText.value);
/** The saved version's document (the one Test run runs): its settings, requires and trigger shape the test form, never the unsaved buffer. */
const savedDoc = computed<ScriptDocument | null>(() => (jsonProblem(savedText.value) ? null : (JSON.parse(savedText.value) as ScriptDocument)));
const issueSteps = computed(() => (check.value?.issues ?? []).map((i) => i.stepId).filter((x): x is string => !!x));
/** The problems grouped by step, the document's own (stepId null) first. */
const grouped = computed(() => {
  const by = new Map<string | null, Check['issues']>();
  for (const i of check.value?.issues ?? []) by.set(i.stepId, [...(by.get(i.stepId) ?? []), i]);
  return [...by.entries()].sort(([a], [b]) => (a === null ? -1 : b === null ? 1 : 0));
});

async function load(v?: number) {
  if (!current.value) return;
  loading.value = true;
  error.value = null;
  try {
    const [got, list] = await Promise.all([s.get(current.value.key, v), s.versions(current.value.key)]);
    text.value = pretty(got.document);
    savedText.value = text.value;
    version.value = got.version;
    check.value = got.check;
    versions.value = list;
  } catch (e) {
    error.value = errorText(e);
  } finally {
    loading.value = false;
  }
}
onMounted(() => { void load(); });

async function save() {
  if (readOnly.value || parseProblem.value || busy.value) return;
  busy.value = 'save';
  error.value = null;
  savedNote.value = null;
  try {
    const r = await s.save(text.value);
    check.value = r.check;
    savedText.value = text.value;
    version.value = r.version;
    current.value = { id: r.id, key: r.key };
    savedNote.value = r.draft ? `Saved as version ${r.version}, a draft: it has problems, so no member runs it.` : `Saved as version ${r.version}.`;
    try { versions.value = await s.versions(r.key); } catch { /* the list stays as it was */ }
    emit('saved', r);
  } catch (e) {
    error.value = errorText(e);
  } finally {
    busy.value = null;
  }
}

async function runCheck() {
  if (parseProblem.value || busy.value) return;
  busy.value = 'check';
  error.value = null;
  try { check.value = await s.check(text.value); } catch (e) { error.value = errorText(e); } finally { busy.value = null; }
}

/** Shows a step in the JSON: selects its "id" and scrolls to it. */
function jumpTo(stepId: string) {
  tab.value = 'json';
  const r = stepRange(text.value, stepId);
  if (r) void editor.value?.jump(r.start, r.end);
}

function pickVersion(e: Event) {
  const v = Number((e.target as HTMLSelectElement).value);
  if (Number.isInteger(v)) void load(v);
}
</script>

<template>
  <div class="script-editor" data-test="script-editor">
    <div class="bar row">
      <button type="button" class="ghost" data-test="editor-back" @click="emit('close')">← Scripts</button>
      <strong class="name">{{ doc?.name ?? current?.key ?? 'New script' }}</strong>
      <span v-if="current" class="muted"><code>{{ current.key }}</code></span>
      <label v-if="versions.length" class="muted">Version
        <select :value="version ?? ''" data-test="editor-version" @change="pickVersion">
          <option v-for="v in versions" :key="v.version" :value="v.version">{{ v.version }}{{ v.ok ? '' : ' (draft)' }}</option>
        </select>
      </label>
      <span v-if="dirty" class="chip" data-test="editor-dirty">not saved</span>
      <span class="grow" />
      <div class="tabs" role="tablist">
        <button type="button" role="tab" :aria-selected="tab === 'json'" :class="{ on: tab === 'json' }" data-test="editor-tab-json" @click="tab = 'json'">JSON</button>
        <button type="button" role="tab" :aria-selected="tab === 'flowchart'" :class="{ on: tab === 'flowchart' }" data-test="editor-tab-flowchart" @click="tab = 'flowchart'">Flowchart</button>
        <button type="button" role="tab" :aria-selected="tab === 'test'" :class="{ on: tab === 'test' }" data-test="editor-tab-test" :disabled="!current"
          :title="current ? 'Try the script on a sample input' : 'Save the script first'" @click="tab = 'test'">Test run</button>
      </div>
      <button type="button" :disabled="!!parseProblem || !!busy" data-test="editor-check" @click="runCheck"><BusyLabel :busy="busy === 'check'" text="Checking…">Check</BusyLabel></button>
      <button v-if="!readOnly" type="button" class="primary" :disabled="!!parseProblem || !!busy" data-test="editor-save" @click="save"><BusyLabel :busy="busy === 'save'" text="Saving…">Save</BusyLabel></button>
    </div>
    <p v-if="error" class="error" data-test="editor-error">{{ error }}</p>
    <p v-if="savedNote" class="muted" data-test="editor-saved">{{ savedNote }}</p>
    <p v-if="loading" class="muted">Loading…</p>

    <div v-show="tab === 'json'" class="body">
      <div class="left">
        <JsonEditor ref="editor" v-model="text" :readonly="readOnly" label="Script JSON" />
        <p v-if="parseProblem" class="error small" data-test="editor-parse">{{ parseProblem }}</p>
        <div class="problems" data-test="check-problems">
          <template v-if="check">
            <p v-if="check.ok && !check.issues.length" class="ok small" data-test="check-ok">The check found no problems.</p>
            <div v-for="[stepId, list] in grouped" :key="stepId ?? '(document)'" class="group">
              <button v-if="stepId" type="button" class="link" data-test="check-step" :data-step="stepId" @click="jumpTo(stepId)">Step <code>{{ stepId }}</code></button>
              <span v-else class="muted">The document</span>
              <ul>
                <li v-for="(i, n) in list" :key="n" data-test="check-issue" :data-step="stepId ?? ''">
                  <button v-if="stepId" type="button" class="link plain" @click="jumpTo(stepId)">{{ i.message }}</button>
                  <span v-else>{{ i.message }}</span>
                  <code class="muted code">{{ i.code }}</code>
                </li>
              </ul>
            </div>
          </template>
          <p v-else class="muted small">Check or Save to see the script's problems.</p>
        </div>
      </div>
      <ScriptOutline class="right" :doc="doc" :issue-steps="issueSteps" @step="jumpTo" />
    </div>
    <div v-if="tab === 'flowchart'" class="flowchart" data-test="editor-flowchart">
      <slot name="flowchart" :doc="doc">
        <p class="muted">Edit the script as JSON; the outline shows its steps.</p>
      </slot>
    </div>
    <div v-if="tab === 'test' && current" class="test">
      <p v-if="dirty" class="warn small">The test runs version {{ version }} as saved, not the changes above.</p>
      <ScriptTestRun :api="api" :script="current" :version="version ?? undefined" :doc="savedDoc" :teams="teams" />
    </div>
  </div>
</template>

<style scoped>
.script-editor { display: flex; flex-direction: column; gap: 6px; min-height: 0; flex: 1 1 0; }
.bar { flex-wrap: wrap; }
.name { font-size: 14px; }
.ghost { border: 0; background: transparent; color: var(--accent); padding: 4px 6px; }
.tabs { display: inline-flex; border: 1px solid var(--line); border-radius: 6px; overflow: hidden; }
.tabs button { border: 0; border-radius: 0; padding: 4px 10px; background: var(--surface); }
.tabs button.on { background: var(--selected); }
.body { display: grid; grid-template-columns: minmax(0, 1fr) minmax(220px, 320px); gap: 10px; min-height: 420px; flex: 1 1 0; }
.left { display: flex; flex-direction: column; gap: 6px; min-height: 0; }
.right { border-left: 1px solid var(--line-soft); padding-left: 10px; }
.problems { max-height: 180px; overflow: auto; font-size: 12.5px; }
.problems ul { margin: 2px 0 6px; padding-left: 18px; }
.group { margin-bottom: 4px; }
.link { border: 0; background: transparent; padding: 0; color: var(--accent); text-align: left; }
.link.plain { color: var(--ink); }
.link:hover { text-decoration: underline; }
.code { margin-left: 6px; }
.small { font-size: 12px; margin: 0; }
.flowchart, .test { padding: 6px 0; }
@media (max-width: 720px) { .body { grid-template-columns: 1fr; } .right { border-left: 0; padding-left: 0; } }
</style>
