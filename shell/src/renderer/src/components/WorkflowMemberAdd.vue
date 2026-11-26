<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue';
import type { YawbleApi } from '../../../shared/api';
import { errorText } from '../api';
import { vChange } from '../remoteAccess';
import BusyLabel from './BusyLabel.vue';
import {
  bindableIntegrations, bindingsFrom, enumOptions, isRealName, knownIntegrations, label, overrideChoices, workflowsApi, settingInput, settingText,
  settingValue, type KnownIntegration, type WorkflowRequire, type WorkflowSettingDecl, type WorkflowSummary, type WorkflowVersionInfo,
} from '../workflowMembers';

// Add member as "Workflow member": the workflow (a runnable one), its version (latest, or a pinned clean
// version), a settings form made from the workflow's own settings (types and defaults from its document), and its
// integrations: one the workflow names by its real name is used as it is ("Use <name>") or replaced by another of the
// team's of the same kind; an alias the workflow names needs one of the team's integrations. Add calls workflows.memberAdd; a
// refusal (bad_workflow, not_runnable) shows its reason and keeps what was filled in.
const props = defineProps<{ api: YawbleApi; teamId: number; name: string }>();
const emit = defineEmits<{ added: [memberId: number]; cancel: [] }>();
const rpc = workflowsApi(props.api);

const workflows = ref<WorkflowSummary[] | null>(null);
const workflowId = ref<number | null>(null);
const versions = ref<WorkflowVersionInfo[]>([]);
/** '' = latest; else a pinned version number. */
const version = ref<string>('');
const settings = ref<WorkflowSettingDecl[]>([]);
const requires = ref<WorkflowRequire[]>([]);
// A field per setting: text, a number input's number, or a checkbox's boolean.
const values = ref<Record<string, any>>({});
const bindings = ref<Record<string, string>>({});
/** Per requires name: the team's integrations of its kind (null: the kind is not listed, the name is typed). */
const choices = ref<Record<string, string[] | null>>({});
/** Every integration by name: a requires name among them is a real name (no binding needed). */
const known = ref<KnownIntegration[]>([]);
const real = (name: string) => isRealName(known.value, name);
const error = ref<string | null>(null);
const loading = ref(false);
const adding = ref(false);

const runnable = computed(() => (workflows.value ?? []).filter((s) => s.runnableVersion !== null));
const chosen = computed(() => runnable.value.find((s) => s.id === workflowId.value) ?? null);
const cleanVersions = computed(() => versions.value.filter((v) => v.ok));

onMounted(async () => {
  try { workflows.value = await rpc.list(); } catch (e) { workflows.value = []; error.value = errorText(e); }
});

watch(workflowId, async (id) => {
  versions.value = []; version.value = ''; settings.value = []; requires.value = []; values.value = {}; bindings.value = {}; choices.value = {};
  error.value = null;
  if (id === null) return;
  loading.value = true;
  try {
    versions.value = await rpc.versions(id);
    await loadDocument();
  } catch (e) {
    error.value = errorText(e);
  } finally {
    loading.value = false;
  }
});

watch(version, () => { if (workflowId.value !== null) void loadDocument().catch((e) => { error.value = errorText(e); }); });

/** The settings and requires of the version the member will run: the pinned one, or the workflow's runnable version. */
async function loadDocument() {
  const s = chosen.value;
  if (!s) return;
  const v = version.value ? Number(version.value) : s.runnableVersion ?? undefined;
  const doc = (await rpc.get(s.id, v)).document;
  if (s.id !== workflowId.value) return;
  settings.value = (doc.settings ?? []).filter((d) => d && typeof d.name === 'string');
  requires.value = (doc.requires ?? []).filter((r) => r && typeof r.name === 'string');
  const before = values.value;
  values.value = Object.fromEntries(settings.value.map((d) => [d.name, before[d.name] ?? settingText(d, d.default)]));
  const bound = bindings.value;
  bindings.value = Object.fromEntries(requires.value.map((r) => [r.name, bound[r.name] ?? '']));
  const [lists, all] = await Promise.all([
    Promise.all(requires.value.map((r) => bindableIntegrations(props.api, props.teamId, r.kind).catch(() => null))),
    knownIntegrations(props.api).catch(() => [] as KnownIntegration[]),
  ]);
  known.value = all;
  choices.value = Object.fromEntries(requires.value.map((r, i) => [r.name,
    real(r.name) ? overrideChoices((lists[i] ?? []).map((n) => ({ name: n, kind: r.kind })), { name: r.name, kind: r.kind as 'email' | 'api' }) : lists[i]]));
  for (const r of requires.value) {
    const list = choices.value[r.name];
    if (!real(r.name) && list && list.length === 1 && !bindings.value[r.name]) bindings.value[r.name] = list[0];
  }
}

const canAdd = computed(() => !!props.name.trim() && chosen.value !== null && !loading.value && !adding.value
  && requires.value.every((r) => real(r.name) || !!bindings.value[r.name]?.trim()));

async function add() {
  if (!canAdd.value || !chosen.value) return;
  error.value = null;
  const out: Record<string, unknown> = {};
  try {
    for (const d of settings.value) {
      const v = settingValue(d, values.value[d.name] ?? '');
      if (v !== undefined) out[d.name] = v;
    }
  } catch (e) {
    error.value = errorText(e);
    return;
  }
  adding.value = true;
  try {
    const m = await rpc.memberAdd({
      teamId: props.teamId, name: props.name.trim(), workflow: chosen.value.id,
      ...(version.value ? { version: Number(version.value) } : {}),
      settings: out,
      bindings: bindingsFrom(bindings.value, real),
    });
    emit('added', m.memberId);
  } catch (e) {
    error.value = errorText(e);
  } finally {
    adding.value = false;
  }
}

const settingId = (name: string) => `workflow-setting-${name}`;
</script>

<template>
  <div class="workflow-add" data-test="workflow-member-form">
    <label>Workflow
      <select v-model="workflowId" data-test="workflow-member-workflow" :disabled="workflows === null">
        <option :value="null" disabled>{{ workflows === null ? 'Loading…' : runnable.length ? 'Choose a workflow' : 'No workflow can run yet' }}</option>
        <option v-for="s in runnable" :key="s.id" :value="s.id">{{ s.name }} ({{ s.key }})</option>
      </select>
    </label>
    <template v-if="chosen">
      <p v-if="chosen.description" class="muted small" data-test="workflow-member-description">{{ chosen.description }}</p>
      <label>Version
        <select v-model="version" data-test="workflow-member-version">
          <option value="">Latest (runs v{{ chosen.runnableVersion }})</option>
          <option v-for="v in cleanVersions" :key="v.version" :value="String(v.version)">v{{ v.version }}</option>
        </select>
      </label>
      <fieldset v-if="settings.length" class="group" data-test="workflow-member-settings">
        <legend>Settings</legend>
        <template v-for="d in settings" :key="d.name">
          <label v-if="settingInput(d.type) === 'bool'" class="check" :data-test="settingId(d.name)">
            <input v-model="values[d.name]" type="checkbox" />{{ label(d) }}
          </label>
          <label v-else :data-test="settingId(d.name)">{{ label(d) }}<span v-if="d.default === undefined" class="need" title="Needs a value"> *</span>
            <select v-if="settingInput(d.type) === 'enum'" v-model="values[d.name]">
              <option v-if="d.default === undefined" value="" disabled>Choose…</option>
              <option v-for="o in enumOptions(d.type)" :key="o" :value="o">{{ o }}</option>
            </select>
            <input v-else-if="settingInput(d.type) === 'number'" v-model="values[d.name]" type="number" :step="d.type === 'int' ? 1 : 'any'" />
            <textarea v-else-if="settingInput(d.type) === 'list' || settingInput(d.type) === 'json'" v-model="values[d.name]" rows="3"
              :placeholder="settingInput(d.type) === 'list' ? 'One per line' : 'JSON'"></textarea>
            <input v-else v-model="values[d.name]" :placeholder="d.type && d.type !== 'string' ? d.type : ''" />
          </label>
        </template>
      </fieldset>
      <fieldset v-if="requires.length" class="group" data-test="workflow-member-bindings">
        <legend>Integrations</legend>
        <label v-for="r in requires" :key="r.name" :data-test="`workflow-binding-${r.name}`">{{ r.name }} <span class="muted">({{ r.kind }})</span>
          <select v-if="real(r.name)" v-model="bindings[r.name]" data-test="workflow-binding-override">
            <option value="">Use {{ r.name }}</option>
            <option v-for="n in choices[r.name] ?? []" :key="n" :value="n">Use {{ n }} instead</option>
          </select>
          <select v-else-if="choices[r.name]" v-model="bindings[r.name]">
            <option value="" disabled>{{ choices[r.name]!.length ? 'Choose one of the team\'s integrations' : 'None offered to this team' }}</option>
            <option v-for="n in choices[r.name]!" :key="n" :value="n">{{ n }}</option>
          </select>
          <input v-else v-model="bindings[r.name]" placeholder="The integration's name" />
        </label>
      </fieldset>
    </template>
    <p v-if="error" class="error small" data-test="workflow-member-error">{{ error }}</p>
    <div class="add-buttons">
      <button type="button" class="primary" v-change data-test="workflow-member-add" :disabled="!canAdd" :aria-busy="adding" @click="add">
        <BusyLabel :busy="adding" text="Adding…">Add</BusyLabel></button>
      <button type="button" data-test="workflow-member-cancel" :disabled="adding" @click="emit('cancel')">Cancel</button>
    </div>
  </div>
</template>

<style scoped>
.workflow-add { display: flex; flex-direction: column; gap: 8px; }
.workflow-add label { display: flex; flex-direction: column; gap: 3px; font-size: 12px; color: var(--ink-soft); }
.workflow-add label.check { flex-direction: row; align-items: center; gap: 6px; }
.workflow-add input:not([type=checkbox]), .workflow-add select, .workflow-add textarea { font: inherit; font-size: 13px; }
.group { border: 1px solid var(--line-soft); border-radius: 6px; padding: 6px 8px; margin: 0; display: flex; flex-direction: column; gap: 6px; }
.group legend { font-size: 12px; color: var(--ink-soft); padding: 0 4px; }
.need { color: var(--bad); }
.add-buttons { display: flex; gap: 6px; }
.muted { color: var(--muted); }
.small { font-size: 12px; }
.error { color: var(--bad); margin: 0; }
</style>
