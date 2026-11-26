<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue';
import type { YawbleApi } from '../../../../shared/api';
import { errorCode, errorText } from '../../api';
import { readOnly } from '../../remoteAccess';
import { jsonProblem, pretty, workflowsApi } from '../../workflows/api';
import {
  designFields, designProblems, moveField, newField, schemaFromFields, schemaTree, typeSuggestions, type DesignField, type JsonSchema,
} from '../../workflows/schemaModel';
import type { SchemaBreak, SchemaSummary, SchemaUse } from '../../workflows/types';
import BusyLabel from '../BusyLabel.vue';
import LoadingNote from '../LoadingNote.vue';
import JsonEditor from './JsonEditor.vue';
import SchemaTree from './SchemaTree.vue';

// The Schemas tab: schemas.list (built-ins first). A schema opens as a tree of its fields (type, required, description,
// enum, the schemas it refers to); a built-in one, and every schema for a Visitor, is read-only (Tree and JSON). Your own
// schema opens on Design (add, remove and reorder fields; each one's name, type, required, description and enum values)
// with JSON beside it: both edit one schema, and JSON that does not parse is refused without losing the form. Save goes
// through schemas.save, which checks the schema; a save that would break a saved workflow is refused (schema_breaks) with its
// list of workflows and steps. "Used in" lists the workflows (schemas.usage) with the variables and steps that use it.
const props = defineProps<{ api: YawbleApi }>();
const s = workflowsApi(props.api);

const list = ref<SchemaSummary[] | null>(null);
const error = ref<string | null>(null);
const open = ref<{ name: string; builtIn: boolean; version: number | null; isNew: boolean } | null>(null);
const name = ref('');
const description = ref('');
const text = ref('');
const busy = ref(false);
const saveError = ref<string | null>(null);
const breaks = ref<SchemaBreak[] | null>(null);
const note = ref<string | null>(null);
const usedIn = ref<SchemaUse[] | null>(null);

type View = 'tree' | 'design' | 'json';
const view = ref<View>('tree');
/** The schema the form edits: its top-level keys, and its fields as the designer shows them. */
const base = ref<JsonSchema>({ type: 'object', properties: {} });
const fields = ref<DesignField[]>([]);
/** The text the form last wrote, so a JSON edit is told from the form's own. */
let written = '';

const locked = computed(() => readOnly.value || !!open.value?.builtIn);
const problem = computed(() => (open.value ? jsonProblem(text.value) : null));
const schemaNames = computed(() => (list.value ?? []).map((x) => x.name));
const suggestions = computed(() => typeSuggestions(schemaNames.value.filter((n) => n !== name.value.trim())));
const formProblems = computed(() => (open.value && !locked.value ? designProblems(fields.value, schemaNames.value) : []));
const parsed = computed<JsonSchema | null>(() => {
  if (problem.value) return null;
  try { return JSON.parse(text.value) as JsonSchema; } catch { return null; }
});
const tree = computed(() => (parsed.value ? schemaTree(parsed.value) : []));

async function refresh() {
  try { list.value = await s.schemas(); error.value = null; } catch (e) { error.value = errorText(e); }
}
onMounted(refresh);

function reset() { saveError.value = null; breaks.value = null; note.value = null; usedIn.value = null; }

/** Loads a schema's JSON into the form and the text. */
function load(schema: JsonSchema) {
  base.value = schema;
  fields.value = designFields(schema);
  text.value = written = pretty(schema);
}

async function usage(n: string) {
  try { usedIn.value = (await s.schemaUsage(n)).workflows ?? []; } catch { usedIn.value = null; /* an older core: no Used in */ }
}

async function show(x: Pick<SchemaSummary, 'name'>) {
  reset();
  try {
    const v = await s.schema(x.name);
    open.value = { name: v.name, builtIn: v.builtIn, version: v.version, isNew: false };
    name.value = v.name;
    description.value = v.description ?? '';
    load(v.schema as JsonSchema);
    view.value = v.builtIn || readOnly.value ? 'tree' : 'design';
    void usage(v.name);
  } catch (e) { error.value = errorText(e); }
}
function newSchema() {
  reset();
  open.value = { name: '', builtIn: false, version: null, isNew: true };
  name.value = '';
  description.value = '';
  load({ type: 'object', properties: {}, required: [] });
  view.value = 'design';
}

// The form writes the text; a JSON edit that parses rewrites the form (one that does not leaves the form as it was).
/** The fields were just read from the text: the text stays as the person typed it. */
let fromText = false;
watch(fields, () => {
  if (fromText) { fromText = false; return; }
  if (!open.value || locked.value) return;
  text.value = written = pretty(schemaFromFields(base.value, fields.value));
}, { deep: true });
watch(text, (t) => {
  if (t === written || !parsed.value) return;
  written = t;
  base.value = parsed.value;
  fromText = true;
  fields.value = designFields(parsed.value);
});

function addField() { fields.value = [...fields.value, newField(fields.value)]; }
function removeField(i: number) { fields.value = fields.value.filter((_, j) => j !== i); }
function move(i: number, delta: number) { fields.value = moveField(fields.value, i, delta); }

/** The breaks a schema_breaks refusal carries (in its data, when the transport keeps it), else null: the message says them. */
function breaksOf(e: unknown): SchemaBreak[] | null {
  const data = (e as { data?: unknown } | null)?.data as { breaks?: unknown } | unknown[] | undefined;
  const arr = Array.isArray(data) ? data : Array.isArray((data as { breaks?: unknown })?.breaks) ? (data as { breaks: unknown[] }).breaks : null;
  return arr ? (arr as SchemaBreak[]) : null;
}

const canSave = computed(() => !locked.value && !problem.value && !!name.value.trim() && !busy.value && formProblems.value.length === 0);

async function save() {
  if (!canSave.value) return;
  reset();
  busy.value = true;
  try {
    const r = await s.schemaSave(name.value.trim(), text.value, description.value);
    open.value = { name: r.name, builtIn: false, version: r.version, isNew: false };
    note.value = `Saved ${r.name} as version ${r.version}.`;
    await refresh();
    void usage(r.name);
  } catch (e) {
    if (errorCode(e) === 'schema_breaks') {
      breaks.value = breaksOf(e) ?? [];
      saveError.value = `Not saved: this change would break saved workflows. ${errorText(e)}`;
    } else saveError.value = errorText(e);
  } finally {
    busy.value = false;
  }
}
</script>

<template>
  <div class="schemas" data-test="schemas-tab">
    <div class="side">
      <div class="row">
        <strong class="grow">Schemas</strong>
        <button v-if="!readOnly" type="button" class="mini" data-test="schema-new" @click="newSchema">New</button>
      </div>
      <p v-if="error" class="error">{{ error }}</p>
      <LoadingNote v-if="list === null && !error" text="Loading schemas…" />
      <ul v-else-if="list" class="list">
        <li v-for="x in list" :key="x.name">
          <button type="button" class="item" :class="{ on: open?.name === x.name }" data-test="schema-row" :data-name="x.name" @click="show(x)">
            <span class="grow">{{ x.name }}</span>
            <span v-if="x.builtIn" class="chip" data-test="schema-builtin">built-in</span>
            <span class="muted small">v{{ x.latestVersion }}</span>
          </button>
        </li>
      </ul>
    </div>
    <div v-if="open" class="main" data-test="schema-editor">
      <div class="row">
        <input v-if="open.isNew" v-model="name" placeholder="Schema name, e.g. Triage" data-test="schema-name" />
        <strong v-else>{{ open.name }}</strong>
        <span v-if="open.version" class="muted">version {{ open.version }}</span>
        <span v-if="open.builtIn" class="muted" data-test="schema-readonly">Built-in: read-only.</span>
        <span class="grow" />
        <button v-if="!locked" type="button" class="primary" :disabled="!canSave" data-test="schema-save" @click="save">
          <BusyLabel :busy="busy" text="Saving…">Save</BusyLabel>
        </button>
      </div>
      <input v-model="description" class="desc" placeholder="Description (what an agent step reads)" :readonly="locked" data-test="schema-description" />
      <div class="tabs" role="tablist">
        <button v-if="locked" type="button" role="tab" :aria-selected="view === 'tree'" :class="{ on: view === 'tree' }" data-test="schema-view-tree" @click="view = 'tree'">Tree</button>
        <button v-else type="button" role="tab" :aria-selected="view === 'design'" :class="{ on: view === 'design' }" data-test="schema-view-design" @click="view = 'design'">Design</button>
        <button type="button" role="tab" :aria-selected="view === 'json'" :class="{ on: view === 'json' }" data-test="schema-view-json" @click="view = 'json'">JSON</button>
      </div>

      <div v-if="view === 'tree'" class="pane" data-test="schema-tree-view">
        <SchemaTree v-if="tree.length" :fields="tree" @open="(n) => show({ name: n })" />
        <p v-else class="muted small">No fields.</p>
      </div>

      <div v-else-if="view === 'design'" class="pane" data-test="schema-design">
        <p v-if="problem" class="error small" data-test="schema-design-stale">The JSON does not parse, so the form shows the last schema that did. {{ problem }}</p>
        <datalist id="schema-type-suggestions"><option v-for="t in suggestions" :key="t" :value="t" /></datalist>
        <table v-if="fields.length" class="fields">
          <thead><tr><th>Name</th><th>Type</th><th>Required</th><th>Description</th><th>Enum values</th><th /></tr></thead>
          <tbody>
            <tr v-for="(f, i) in fields" :key="i" data-test="schema-field" :data-index="i">
              <td><input v-model="f.name" class="mono" data-test="schema-field-name" aria-label="Field name" /></td>
              <td><input v-model="f.type" class="mono" list="schema-type-suggestions" data-test="schema-field-type" aria-label="Field type" /></td>
              <td class="center"><input v-model="f.required" type="checkbox" data-test="schema-field-required" aria-label="Required" /></td>
              <td><input v-model="f.description" data-test="schema-field-description" aria-label="Description" /></td>
              <td><input v-model="f.enumValues" :disabled="f.type.trim() !== 'string'" placeholder="a, b, c" data-test="schema-field-enum" aria-label="Enum values" /></td>
              <td class="actions">
                <button type="button" class="mini" :disabled="i === 0" data-test="schema-field-up" title="Move up" @click="move(i, -1)">↑</button>
                <button type="button" class="mini" :disabled="i === fields.length - 1" data-test="schema-field-down" title="Move down" @click="move(i, 1)">↓</button>
                <button type="button" class="mini danger" data-test="schema-field-remove" title="Remove" @click="removeField(i)">✕</button>
              </td>
            </tr>
          </tbody>
        </table>
        <p v-else class="muted small">No fields yet.</p>
        <div><button type="button" class="mini" data-test="schema-field-add" @click="addField">Add field</button></div>
        <ul v-if="formProblems.length" class="problems" data-test="schema-design-problems">
          <li v-for="p in formProblems" :key="p" class="error small">{{ p }}</li>
        </ul>
      </div>

      <template v-else>
        <JsonEditor v-model="text" :readonly="locked" label="Schema JSON" />
        <p v-if="problem" class="error small" data-test="schema-json-problem">{{ problem }}</p>
      </template>

      <p v-if="note" class="muted" data-test="schema-saved">{{ note }}</p>
      <div v-if="saveError" class="error-box" data-test="schema-error">
        <p class="error">{{ saveError }}</p>
        <ul v-if="breaks?.length" data-test="schema-breaks">
          <li v-for="(b, i) in breaks" :key="i" data-test="schema-break"><code>{{ b.workflow }}</code> v{{ b.version }}, step <code>{{ b.stepId }}</code>: <code>{{ b.fieldPath }}</code></li>
        </ul>
      </div>

      <div v-if="usedIn" class="used" data-test="schema-used-in">
        <h4>Used in</h4>
        <ul v-if="usedIn.length">
          <li v-for="u in usedIn" :key="u.id" data-test="schema-use">
            <strong>{{ u.name }}</strong> <span class="muted">v{{ u.version }}</span>
            <span v-if="u.variables.length" data-test="schema-use-variables"> · variables <code v-for="v in u.variables" :key="v" class="item-code">{{ v }}</code></span>
            <span v-if="u.steps.length" data-test="schema-use-steps"> · steps <code v-for="st in u.steps" :key="st" class="item-code">{{ st }}</code></span>
          </li>
        </ul>
        <p v-else class="muted small" data-test="schema-unused">No workflow uses it.</p>
      </div>
    </div>
    <p v-else class="main muted">Pick a schema to read it{{ readOnly ? '' : ', or New for one of your own' }}.</p>
  </div>
</template>

<style scoped>
.schemas { display: grid; grid-template-columns: minmax(180px, 240px) minmax(0, 1fr); gap: 12px; min-height: 420px; }
.side { display: flex; flex-direction: column; gap: 6px; border-right: 1px solid var(--line-soft); padding-right: 10px; }
.list { list-style: none; margin: 0; padding: 0; }
.item { display: flex; gap: 6px; align-items: center; width: 100%; border: 0; background: transparent; text-align: left; padding: 5px 8px; border-radius: 6px; }
.item:hover { background: var(--hover); }
.item.on { background: var(--selected); }
.main { display: flex; flex-direction: column; gap: 6px; min-height: 0; }
.desc { width: 100%; }
.tabs { display: flex; gap: 4px; border-bottom: 1px solid var(--line-soft); }
.tabs button { border: 0; background: transparent; padding: 4px 10px; border-bottom: 2px solid transparent; color: var(--ink-soft); }
.tabs button.on { border-bottom-color: var(--accent); color: var(--ink); }
.pane { display: flex; flex-direction: column; gap: 6px; overflow: auto; }
.fields { width: 100%; border-collapse: collapse; font-size: 12.5px; }
.fields th { text-align: left; font-weight: 600; color: var(--ink-soft); padding: 2px 4px; }
.fields td { padding: 2px 4px; vertical-align: middle; }
.fields input:not([type=checkbox]) { width: 100%; box-sizing: border-box; font-size: 12.5px; }
.center { text-align: center; }
.actions { white-space: nowrap; }
.mono { font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; }
.problems { margin: 0; padding-left: 18px; }
.used h4 { font-size: 12px; margin: 6px 0 2px; color: var(--ink-soft); }
.used ul { list-style: none; margin: 0; padding: 0; font-size: 12.5px; display: flex; flex-direction: column; gap: 2px; }
.item-code { margin-left: 4px; }
.error-box ul { margin: 0; padding-left: 18px; font-size: 12.5px; }
.mini { font-size: 12px; padding: 2px 8px; }
.danger { color: var(--bad); }
.small { font-size: 11.5px; margin: 0; }
@media (max-width: 720px) { .schemas { grid-template-columns: 1fr; } .side { border-right: 0; padding-right: 0; } }
</style>
