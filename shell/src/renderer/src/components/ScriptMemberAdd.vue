<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue';
import type { YawbleApi } from '../../../shared/api';
import { errorText } from '../api';
import { vChange } from '../remoteAccess';
import BusyLabel from './BusyLabel.vue';
import {
  bindableIntegrations, enumOptions, label, scriptsApi, settingInput, settingText, settingValue,
  type ScriptRequire, type ScriptSettingDecl, type ScriptSummary, type ScriptVersionInfo,
} from '../scriptMembers';

// B134 (card 598): Add member as "Script member": the script (a runnable one), its version (latest, or a pinned clean
// version), a settings form made from the script's own settings (types and defaults from its document), and one binding
// per requires entry over the team's integrations. Add calls scripts.memberAdd; a refusal (bad_script, not_runnable)
// shows its reason and keeps what was filled in.
const props = defineProps<{ api: YawbleApi; teamId: number; name: string }>();
const emit = defineEmits<{ added: [memberId: number]; cancel: [] }>();
const rpc = scriptsApi(props.api);

const scripts = ref<ScriptSummary[] | null>(null);
const scriptId = ref<number | null>(null);
const versions = ref<ScriptVersionInfo[]>([]);
/** '' = latest; else a pinned version number. */
const version = ref<string>('');
const settings = ref<ScriptSettingDecl[]>([]);
const requires = ref<ScriptRequire[]>([]);
// eslint-disable-next-line @typescript-eslint/no-explicit-any -- a field per setting: text, a number input's number, or a checkbox's boolean
const values = ref<Record<string, any>>({});
const bindings = ref<Record<string, string>>({});
/** Per requires name: the team's integrations of its kind (null: the kind is not listed, the name is typed). */
const choices = ref<Record<string, string[] | null>>({});
const error = ref<string | null>(null);
const loading = ref(false);
const adding = ref(false);

const runnable = computed(() => (scripts.value ?? []).filter((s) => s.runnableVersion !== null));
const chosen = computed(() => runnable.value.find((s) => s.id === scriptId.value) ?? null);
const cleanVersions = computed(() => versions.value.filter((v) => v.ok));

onMounted(async () => {
  try { scripts.value = await rpc.list(); } catch (e) { scripts.value = []; error.value = errorText(e); }
});

watch(scriptId, async (id) => {
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

watch(version, () => { if (scriptId.value !== null) void loadDocument().catch((e) => { error.value = errorText(e); }); });

/** The settings and requires of the version the member will run: the pinned one, or the script's runnable version. */
async function loadDocument() {
  const s = chosen.value;
  if (!s) return;
  const v = version.value ? Number(version.value) : s.runnableVersion ?? undefined;
  const doc = (await rpc.get(s.id, v)).document;
  if (s.id !== scriptId.value) return;
  settings.value = (doc.settings ?? []).filter((d) => d && typeof d.name === 'string');
  requires.value = (doc.requires ?? []).filter((r) => r && typeof r.name === 'string');
  const before = values.value;
  values.value = Object.fromEntries(settings.value.map((d) => [d.name, before[d.name] ?? settingText(d, d.default)]));
  const bound = bindings.value;
  bindings.value = Object.fromEntries(requires.value.map((r) => [r.name, bound[r.name] ?? '']));
  const lists = await Promise.all(requires.value.map((r) => bindableIntegrations(props.api, props.teamId, r.kind).catch(() => null)));
  choices.value = Object.fromEntries(requires.value.map((r, i) => [r.name, lists[i]]));
  for (const r of requires.value) {
    const list = choices.value[r.name];
    if (list && list.length === 1 && !bindings.value[r.name]) bindings.value[r.name] = list[0];
  }
}

const canAdd = computed(() => !!props.name.trim() && chosen.value !== null && !loading.value && !adding.value
  && requires.value.every((r) => !!bindings.value[r.name]?.trim()));

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
      teamId: props.teamId, name: props.name.trim(), script: chosen.value.id,
      ...(version.value ? { version: Number(version.value) } : {}),
      settings: out,
      bindings: Object.fromEntries(Object.entries(bindings.value).map(([k, v]) => [k, v.trim()])),
    });
    emit('added', m.memberId);
  } catch (e) {
    error.value = errorText(e);
  } finally {
    adding.value = false;
  }
}

const settingId = (name: string) => `script-setting-${name}`;
</script>

<template>
  <div class="script-add" data-test="script-member-form">
    <label>Script
      <select v-model="scriptId" data-test="script-member-script" :disabled="scripts === null">
        <option :value="null" disabled>{{ scripts === null ? 'Loading…' : runnable.length ? 'Choose a script' : 'No script can run yet' }}</option>
        <option v-for="s in runnable" :key="s.id" :value="s.id">{{ s.name }} ({{ s.key }})</option>
      </select>
    </label>
    <template v-if="chosen">
      <p v-if="chosen.description" class="muted small" data-test="script-member-description">{{ chosen.description }}</p>
      <label>Version
        <select v-model="version" data-test="script-member-version">
          <option value="">Latest (runs v{{ chosen.runnableVersion }})</option>
          <option v-for="v in cleanVersions" :key="v.version" :value="String(v.version)">v{{ v.version }}</option>
        </select>
      </label>
      <fieldset v-if="settings.length" class="group" data-test="script-member-settings">
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
      <fieldset v-if="requires.length" class="group" data-test="script-member-bindings">
        <legend>Integrations</legend>
        <label v-for="r in requires" :key="r.name" :data-test="`script-binding-${r.name}`">{{ r.name }} <span class="muted">({{ r.kind }})</span>
          <select v-if="choices[r.name]" v-model="bindings[r.name]">
            <option value="" disabled>{{ choices[r.name]!.length ? 'Choose one of the team\'s integrations' : 'None offered to this team' }}</option>
            <option v-for="n in choices[r.name]!" :key="n" :value="n">{{ n }}</option>
          </select>
          <input v-else v-model="bindings[r.name]" placeholder="The integration's name" />
        </label>
      </fieldset>
    </template>
    <p v-if="error" class="error small" data-test="script-member-error">{{ error }}</p>
    <div class="add-buttons">
      <button type="button" class="primary" v-change data-test="script-member-add" :disabled="!canAdd" :aria-busy="adding" @click="add">
        <BusyLabel :busy="adding" text="Adding…">Add</BusyLabel></button>
      <button type="button" data-test="script-member-cancel" :disabled="adding" @click="emit('cancel')">Cancel</button>
    </div>
  </div>
</template>

<style scoped>
.script-add { display: flex; flex-direction: column; gap: 8px; }
.script-add label { display: flex; flex-direction: column; gap: 3px; font-size: 12px; color: var(--ink-soft); }
.script-add label.check { flex-direction: row; align-items: center; gap: 6px; }
.script-add input:not([type=checkbox]), .script-add select, .script-add textarea { font: inherit; font-size: 13px; }
.group { border: 1px solid var(--line-soft); border-radius: 6px; padding: 6px 8px; margin: 0; display: flex; flex-direction: column; gap: 6px; }
.group legend { font-size: 12px; color: var(--ink-soft); padding: 0 4px; }
.need { color: var(--bad); }
.add-buttons { display: flex; gap: 6px; }
.muted { color: var(--muted); }
.small { font-size: 12px; }
.error { color: var(--bad); margin: 0; }
</style>
