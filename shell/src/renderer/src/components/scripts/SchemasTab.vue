<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import type { YawbleApi } from '../../../../shared/api';
import { errorCode, errorText } from '../../api';
import { readOnly } from '../../remoteAccess';
import { jsonProblem, pretty, scriptsApi } from '../../scripts/api';
import type { SchemaBreak, SchemaSummary } from '../../scripts/types';
import BusyLabel from '../BusyLabel.vue';
import LoadingNote from '../LoadingNote.vue';
import JsonEditor from './JsonEditor.vue';

// The Schemas tab (B134): schemas.list (built-ins first), one schema's JSON in the editor, Save through schemas.save. A
// save that would break a saved script is refused (schema_breaks) and its list of scripts and steps is shown. Built-in
// schemas, and every schema for a Visitor, are read-only.
const props = defineProps<{ api: YawbleApi }>();
const s = scriptsApi(props.api);

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

const locked = computed(() => readOnly.value || !!open.value?.builtIn);
const problem = computed(() => (open.value ? jsonProblem(text.value) : null));

async function refresh() {
  try { list.value = await s.schemas(); error.value = null; } catch (e) { error.value = errorText(e); }
}
onMounted(refresh);

function reset() { saveError.value = null; breaks.value = null; note.value = null; }
async function show(x: SchemaSummary) {
  reset();
  try {
    const v = await s.schema(x.name);
    open.value = { name: v.name, builtIn: v.builtIn, version: v.version, isNew: false };
    name.value = v.name;
    description.value = v.description ?? '';
    text.value = pretty(v.schema);
  } catch (e) { error.value = errorText(e); }
}
function newSchema() {
  reset();
  open.value = { name: '', builtIn: false, version: null, isNew: true };
  name.value = '';
  description.value = '';
  text.value = pretty({ type: 'object', properties: {}, required: [] });
}

/** The breaks a schema_breaks refusal carries (in its data, when the transport keeps it), else null: the message says them. */
function breaksOf(e: unknown): SchemaBreak[] | null {
  const data = (e as { data?: unknown } | null)?.data as { breaks?: unknown } | unknown[] | undefined;
  const arr = Array.isArray(data) ? data : Array.isArray((data as { breaks?: unknown })?.breaks) ? (data as { breaks: unknown[] }).breaks : null;
  return arr ? (arr as SchemaBreak[]) : null;
}

async function save() {
  if (locked.value || problem.value || !name.value.trim() || busy.value) return;
  reset();
  busy.value = true;
  try {
    const r = await s.schemaSave(name.value.trim(), text.value, description.value);
    open.value = { name: r.name, builtIn: false, version: r.version, isNew: false };
    note.value = `Saved ${r.name} as version ${r.version}.`;
    await refresh();
  } catch (e) {
    if (errorCode(e) === 'schema_breaks') {
      breaks.value = breaksOf(e) ?? [];
      saveError.value = `Not saved: this change would break saved scripts. ${errorText(e)}`;
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
        <button v-if="!locked" type="button" class="primary" :disabled="!!problem || !name.trim() || busy" data-test="schema-save" @click="save">
          <BusyLabel :busy="busy" text="Saving…">Save</BusyLabel>
        </button>
      </div>
      <input v-model="description" class="desc" placeholder="Description (what an agent step reads)" :readonly="locked" data-test="schema-description" />
      <JsonEditor v-model="text" :readonly="locked" label="Schema JSON" />
      <p v-if="problem" class="error small">{{ problem }}</p>
      <p v-if="note" class="muted" data-test="schema-saved">{{ note }}</p>
      <div v-if="saveError" class="error-box" data-test="schema-error">
        <p class="error">{{ saveError }}</p>
        <ul v-if="breaks?.length" data-test="schema-breaks">
          <li v-for="(b, i) in breaks" :key="i" data-test="schema-break"><code>{{ b.script }}</code> v{{ b.version }}, step <code>{{ b.stepId }}</code>: <code>{{ b.fieldPath }}</code></li>
        </ul>
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
.error-box ul { margin: 0; padding-left: 18px; font-size: 12.5px; }
.mini { font-size: 12px; padding: 2px 8px; }
.small { font-size: 11.5px; margin: 0; }
@media (max-width: 720px) { .schemas { grid-template-columns: 1fr; } .side { border-right: 0; padding-right: 0; } }
</style>
