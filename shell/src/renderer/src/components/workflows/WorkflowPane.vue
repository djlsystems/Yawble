<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { pretty, settingFromText, settingToText } from '../../workflows/api';
import { deriveRequires, NATIVE_TYPES, stepsUsing, TRIGGER_KINDS, type DesignContext, type MemberIntegration } from '../../workflows/catalog';
import type { WorkflowDocument, WorkflowSetting, WorkflowVariable } from '../../workflows/types';

// The Workflow pane, shown when no step is selected: the whole workflow by hand on the Design tab (name, id, description,
// the triggers it accepts, settings, the integrations it uses, variables, outputs) and as JSON on the JSON tab. Both
// write the one document. JSON that does not parse (or is not an object) is refused and the form stays as it was.
const props = defineProps<{
  ctx: DesignContext;
  readonly: boolean;
  integrations?: MemberIntegration[] | null;
  /** The Start node is selected: only how the workflow starts (its triggers), with a way to the whole workflow. */
  start?: boolean;
}>();
const emit = defineEmits<{ change: [doc: WorkflowDocument]; step: [stepId: string]; workflow: [] }>();

interface Output { name: string; type: string; description?: string }

const tab = ref<'design' | 'json'>('design');
const doc = computed(() => props.ctx.doc);
const copy = <T>(v: T): T => JSON.parse(JSON.stringify(v)) as T;
function change(edit: (d: WorkflowDocument) => void) {
  if (props.readonly) return;
  const d = copy(doc.value);
  edit(d);
  emit('change', d);
}
const types = computed(() => [...NATIVE_TYPES, ...Object.keys(props.ctx.schemas ?? {})]);

// ---- trigger
const accepts = computed(() => doc.value.trigger?.accepts ?? []);
function toggleAccept(kind: string, on: boolean) {
  change((d) => {
    const list = (d.trigger?.accepts ?? []).filter((k) => k !== kind);
    if (on) list.push(kind);
    d.trigger = { ...(d.trigger ?? {}), accepts: TRIGGER_KINDS.filter((k) => list.includes(k)).concat(list.filter((k) => !TRIGGER_KINDS.includes(k))) };
  });
}

// ---- lists: settings, variables, outputs
type ListKey = 'settings' | 'variables' | 'outputs';
const settings = computed<WorkflowSetting[]>(() => (Array.isArray(doc.value.settings) ? doc.value.settings : []));
const variables = computed<WorkflowVariable[]>(() => (Array.isArray(doc.value.variables) ? doc.value.variables : []));
const outputs = computed<Output[]>(() => (Array.isArray(doc.value.outputs) ? (doc.value.outputs as Output[]) : []));

function listOf(d: WorkflowDocument, key: ListKey): Record<string, unknown>[] {
  if (!Array.isArray(d[key])) d[key] = [];
  return d[key] as Record<string, unknown>[];
}
function setField(key: ListKey, i: number, field: string, value: unknown) {
  change((d) => {
    const row = listOf(d, key)[i];
    if (!row) return;
    if (value === undefined || value === '') { if (field === 'name' || field === 'type') row[field] = ''; else delete row[field]; } else row[field] = value;
  });
}
function freeName(list: { name?: unknown }[], base: string) {
  let n = list.length + 1;
  while (list.some((x) => x.name === `${base}${n}`)) n++;
  return `${base}${n}`;
}
function add(key: ListKey) {
  change((d) => {
    const list = listOf(d, key);
    if (key === 'settings') list.push({ name: freeName(list, 'setting'), type: 'string' });
    else if (key === 'variables') list.push({ name: freeName(list, 'var'), type: 'string', scope: 'run' });
    else list.push({ name: freeName(list, 'output'), type: 'string' });
  });
}
const remove = (key: ListKey, i: number) => change((d) => { listOf(d, key).splice(i, 1); });
function move(key: ListKey, i: number, by: -1 | 1) {
  change((d) => {
    const list = listOf(d, key);
    const j = i + by;
    if (j < 0 || j >= list.length) return;
    [list[i], list[j]] = [list[j]!, list[i]!];
  });
}
const settingDefault = (s: WorkflowSetting) => ('default' in s ? settingToText(s.type ?? 'string', s.default) : '');
function setDefault(i: number, text: string) {
  const s = settings.value[i];
  setField('settings', i, 'default', text === '' ? undefined : settingFromText(s?.type ?? 'string', text));
}
const initialText = (v: WorkflowVariable) => (v.initial !== undefined ? JSON.stringify(v.initial) : '');
/** An initial value is JSON (0, "text", [], {}); text that is not JSON is taken as a string. */
function setInitial(i: number, text: string) {
  if (text.trim() === '') { setField('variables', i, 'initial', undefined); return; }
  let v: unknown;
  try { v = JSON.parse(text); } catch { v = text; }
  setField('variables', i, 'initial', v);
}

// ---- integrations used
const required = computed(() => deriveRequires(doc.value, props.ctx.catalog));
const overrideOf = (name: string) => props.integrations?.find((i) => i.name === name && i.overridden) ?? null;

// ---- JSON
const local = ref(pretty(doc.value));
const problem = ref<string | null>(null);
watch(() => pretty(doc.value), (t) => {
  try { if (JSON.stringify(JSON.parse(local.value)) === JSON.stringify(JSON.parse(t))) return; } catch { /* shown below */ }
  local.value = t;
  problem.value = null;
});
function editJson(e: Event) {
  local.value = (e.target as HTMLTextAreaElement).value;
  if (props.readonly) return;
  let v: unknown;
  try { v = JSON.parse(local.value); } catch (err) { problem.value = `Not valid JSON, so it is not used: ${(err as Error).message}`; return; }
  if (!v || typeof v !== 'object' || Array.isArray(v)) { problem.value = 'A workflow is a JSON object, so this is not used.'; return; }
  problem.value = null;
  emit('change', v as WorkflowDocument);
}
</script>

<template>
  <div class="pane" data-test="workflow-pane">
    <template v-if="start">
      <div class="head" data-test="start-pane">
        <strong>Start</strong>
        <span class="muted small">The run starts here, when one of these triggers fires.</span>
      </div>
    </template>
    <div v-else class="tabs" role="tablist">
      <button type="button" role="tab" :aria-selected="tab === 'design'" :class="{ on: tab === 'design' }" data-test="workflow-pane-tab-design" @click="tab = 'design'">Design</button>
      <button type="button" role="tab" :aria-selected="tab === 'json'" :class="{ on: tab === 'json' }" data-test="workflow-pane-tab-json" @click="tab = 'json'">JSON</button>
    </div>

    <div v-if="start" class="design">
      <fieldset class="group" data-test="workflow-accepts">
        <legend>Triggers it accepts</legend>
        <label v-for="k in TRIGGER_KINDS" :key="k" class="check">
          <input type="checkbox" :checked="accepts.includes(k)" :disabled="readonly" :data-test="`workflow-accept-${k}`" @change="toggleAccept(k, ($event.target as HTMLInputElement).checked)" />{{ k }}
        </label>
      </fieldset>
      <p class="muted small">When it fires is set on the member that runs the workflow (its Triggers pill).</p>
      <button type="button" class="link" data-test="start-pane-workflow" @click="emit('workflow')">The whole workflow: settings, variables, outputs</button>
    </div>
    <div v-else-if="tab === 'design'" class="design" data-test="workflow-pane-design">
      <label class="field">Name <input :value="doc.name ?? ''" :readonly="readonly" data-test="workflow-name" @input="change((d) => { d.name = ($event.target as HTMLInputElement).value; })" /></label>
      <label class="field">Id <input :value="doc.id ?? ''" :readonly="readonly" spellcheck="false" data-test="workflow-id" @input="change((d) => { d.id = ($event.target as HTMLInputElement).value; })" /></label>
      <label class="field">Description <textarea :value="doc.description ?? ''" :readonly="readonly" rows="2" data-test="workflow-description" @input="change((d) => { d.description = ($event.target as HTMLTextAreaElement).value; })" /></label>

      <fieldset class="group" data-test="workflow-accepts">
        <legend>Triggers it accepts</legend>
        <label v-for="k in TRIGGER_KINDS" :key="k" class="check">
          <input type="checkbox" :checked="accepts.includes(k)" :disabled="readonly" :data-test="`workflow-accept-${k}`" @change="toggleAccept(k, ($event.target as HTMLInputElement).checked)" />{{ k }}
        </label>
      </fieldset>

      <fieldset class="group" data-test="workflow-settings">
        <legend>Settings</legend>
        <div v-for="(s, i) in settings" :key="i" class="row-item" data-test="workflow-setting" :data-name="s.name">
          <div class="line">
            <input :value="s.name" :readonly="readonly" placeholder="name" spellcheck="false" aria-label="Setting name" data-test="setting-name" @change="setField('settings', i, 'name', ($event.target as HTMLInputElement).value.trim())" />
            <input :value="s.type" :readonly="readonly" list="workflow-pane-types" placeholder="type" spellcheck="false" aria-label="Setting type" data-test="setting-type" @change="setField('settings', i, 'type', ($event.target as HTMLInputElement).value.trim())" />
            <template v-if="!readonly">
              <button type="button" class="mini" :disabled="i === 0" title="Move up" aria-label="Move up" data-test="setting-up" @click="move('settings', i, -1)">↑</button>
              <button type="button" class="mini" :disabled="i === settings.length - 1" title="Move down" aria-label="Move down" data-test="setting-down" @click="move('settings', i, 1)">↓</button>
              <button type="button" class="mini" data-test="setting-remove" @click="remove('settings', i)">Remove</button>
            </template>
          </div>
          <div class="line">
            <input :value="s.label ?? ''" :readonly="readonly" placeholder="label" aria-label="Setting label" data-test="setting-label" @change="setField('settings', i, 'label', ($event.target as HTMLInputElement).value)" />
            <textarea v-if="(s.type ?? '').trim().startsWith('list<')" :value="settingDefault(s)" :readonly="readonly" rows="2" placeholder="default, one per line"
              aria-label="Setting default" data-test="setting-default" @change="setDefault(i, ($event.target as HTMLTextAreaElement).value)" />
            <input v-else :value="settingDefault(s)" :readonly="readonly" placeholder="default" aria-label="Setting default" data-test="setting-default" @change="setDefault(i, ($event.target as HTMLInputElement).value)" />
          </div>
        </div>
        <button v-if="!readonly" type="button" class="mini" data-test="setting-add" @click="add('settings')">Add a setting</button>
      </fieldset>

      <fieldset class="group" data-test="workflow-requires">
        <legend>Integrations used</legend>
        <p v-if="!required.length" class="muted small">No step calls an integration.</p>
        <div v-for="r in required" :key="r.name" class="require" data-test="workflow-require" :data-name="r.name">
          <div><strong>{{ r.name }}</strong> <span class="muted">{{ r.kind === 'email' ? 'Email' : 'API' }}</span>
            <span v-if="overrideOf(r.name)" class="override" data-test="workflow-require-override">{{ r.name }} → {{ overrideOf(r.name)!.uses }}</span></div>
          <div class="muted small">{{ r.actions?.join(', ') }}</div>
          <div class="steps">
            <button v-for="id in stepsUsing(doc, r.name)" :key="id" type="button" class="link" data-test="workflow-require-step" :data-step="id" @click="emit('step', id)"><code>{{ id }}</code></button>
          </div>
        </div>
      </fieldset>

      <fieldset class="group" data-test="workflow-variables">
        <legend>Variables</legend>
        <div v-for="(v, i) in variables" :key="i" class="row-item" data-test="workflow-variable" :data-name="v.name">
          <div class="line">
            <input :value="v.name" :readonly="readonly" placeholder="name" spellcheck="false" aria-label="Variable name" data-test="variable-name" @change="setField('variables', i, 'name', ($event.target as HTMLInputElement).value.trim())" />
            <input :value="v.type" :readonly="readonly" list="workflow-pane-types" placeholder="type" spellcheck="false" aria-label="Variable type" data-test="variable-type" @change="setField('variables', i, 'type', ($event.target as HTMLInputElement).value.trim())" />
            <select :value="v.scope ?? 'run'" :disabled="readonly" aria-label="Scope" data-test="variable-scope" @change="setField('variables', i, 'scope', ($event.target as HTMLSelectElement).value)">
              <option value="run">run</option><option value="saved">saved</option>
            </select>
            <button v-if="!readonly" type="button" class="mini" data-test="variable-remove" @click="remove('variables', i)">Remove</button>
          </div>
          <div class="line">
            <input :value="initialText(v)" :readonly="readonly" placeholder="initial value (JSON)" spellcheck="false" aria-label="Initial value" data-test="variable-initial" @change="setInitial(i, ($event.target as HTMLInputElement).value)" />
          </div>
        </div>
        <button v-if="!readonly" type="button" class="mini" data-test="variable-add" @click="add('variables')">Add a variable</button>
      </fieldset>

      <fieldset class="group" data-test="workflow-outputs">
        <legend>Outputs</legend>
        <div v-for="(o, i) in outputs" :key="i" class="row-item" data-test="workflow-output" :data-name="o.name">
          <div class="line">
            <input :value="o.name" :readonly="readonly" placeholder="name" spellcheck="false" aria-label="Output name" data-test="output-name" @change="setField('outputs', i, 'name', ($event.target as HTMLInputElement).value.trim())" />
            <input :value="o.type" :readonly="readonly" list="workflow-pane-types" placeholder="type" spellcheck="false" aria-label="Output type" data-test="output-type" @change="setField('outputs', i, 'type', ($event.target as HTMLInputElement).value.trim())" />
            <button v-if="!readonly" type="button" class="mini" data-test="output-remove" @click="remove('outputs', i)">Remove</button>
          </div>
          <div class="line">
            <input :value="o.description ?? ''" :readonly="readonly" placeholder="description" aria-label="Output description" data-test="output-description" @change="setField('outputs', i, 'description', ($event.target as HTMLInputElement).value)" />
          </div>
        </div>
        <button v-if="!readonly" type="button" class="mini" data-test="output-add" @click="add('outputs')">Add an output</button>
      </fieldset>
      <datalist id="workflow-pane-types"><option v-for="t in types" :key="t" :value="t" /></datalist>
    </div>

    <div v-else class="jsonpane">
      <textarea class="json" :value="local" :readonly="readonly" spellcheck="false" aria-label="Workflow JSON" data-test="workflow-pane-json" @input="editJson" />
      <p v-if="problem" class="error small" data-test="workflow-pane-json-error">{{ problem }}</p>
    </div>
  </div>
</template>

<style scoped>
.pane { display: flex; flex-direction: column; gap: 8px; min-height: 0; font-size: 12.5px; }
.head { display: flex; flex-direction: column; gap: 2px; }
.tabs { display: inline-flex; align-self: flex-start; border: 1px solid var(--line); border-radius: 6px; overflow: hidden; }
.tabs button { border: 0; border-radius: 0; padding: 3px 10px; font-size: 12px; background: var(--surface); color: var(--ink); }
.tabs button.on { background: var(--selected); }
.design { display: flex; flex-direction: column; gap: 8px; }
.field { display: flex; flex-direction: column; gap: 2px; font-size: 12px; color: var(--ink-soft); }
input:not([type=checkbox]), textarea, select { font-size: 12px; padding: 3px 6px; border: 1px solid var(--line); border-radius: 4px; background: var(--surface); color: var(--ink); min-width: 0; }
input[readonly], textarea[readonly] { background: var(--surface-sunken); }
.group { border: 1px solid var(--line-soft); border-radius: 5px; padding: 6px 8px; display: flex; flex-direction: column; gap: 6px; margin: 0; }
.group legend { font-size: 11.5px; color: var(--muted); padding: 0 4px; }
.check { display: inline-flex; gap: 4px; align-items: center; margin-right: 10px; }
.row-item { display: flex; flex-direction: column; gap: 3px; padding-bottom: 5px; border-bottom: 1px dashed var(--line-soft); }
.line { display: flex; gap: 4px; align-items: center; }
.line input { flex: 1 1 0; }
.require { display: flex; flex-direction: column; gap: 2px; }
.override { margin-left: 6px; color: var(--accent); }
.steps { display: flex; flex-wrap: wrap; gap: 6px; }
.link { border: 0; background: transparent; padding: 0; color: var(--accent); }
.link:hover { text-decoration: underline; }
.mini { font-size: 11.5px; padding: 1px 7px; align-self: flex-start; }
.small { font-size: 12px; margin: 0; }
.jsonpane { display: flex; flex-direction: column; gap: 4px; }
.json { min-height: 320px; font-family: ui-monospace, monospace; resize: vertical; }
</style>
