<script setup lang="ts">
import { onMounted, ref } from 'vue';
import type { TeamSummary, YawbleApi } from '../../../../shared/api';
import { errorCode, errorText } from '../../api';
import { ASK_CONCIERGE_PROMPT, discussWithConcierge } from '../../conciergeDiscuss';
import { confirmDialog } from '../../confirm';
import { readOnly } from '../../remoteAccess';
import { scriptsApi, TEMPLATES } from '../../scripts/api';
import type { ScriptDocument, ScriptSummary } from '../../scripts/types';
import LoadingNote from '../LoadingNote.vue';
import ScriptEditor from './ScriptEditor.vue';

// The Scripts tab: the library (scripts.list) with each script's version, draft state and the teams that use it;
// New (blank or a template), Import and Export of the .json, Delete for a script nobody uses (scripts.delete), and the
// editor for one script. A Visitor reads only: New, Import and Delete are not shown.
const props = defineProps<{ api: YawbleApi; teams: readonly TeamSummary[] }>();
const s = scriptsApi(props.api);

const list = ref<ScriptSummary[] | null>(null);
const error = ref<string | null>(null);
const note = ref<string | null>(null);
const editing = ref<{ script: { id: number; key: string } | null; initial?: ScriptDocument; n: number } | null>(null);
const newOpen = ref(false);
const fileInput = ref<HTMLInputElement | null>(null);
let n = 0;

async function refresh() {
  try { list.value = await s.list(); error.value = null; } catch (e) { error.value = errorText(e); }
}
onMounted(refresh);

function open(x: ScriptSummary) { editing.value = { script: { id: x.id, key: x.key }, n: ++n }; }
function newScript(doc: ScriptDocument) {
  newOpen.value = false;
  editing.value = { script: null, initial: JSON.parse(JSON.stringify(doc)) as ScriptDocument, n: ++n };
}
/** New > Ask the Concierge: a new Concierge chat with a first message ready to finish. */
function askConcierge() {
  newOpen.value = false;
  discussWithConcierge(null, ASK_CONCIERGE_PROMPT);
}
function closeEditor() { editing.value = null; void refresh(); }

async function importFile(e: Event) {
  const input = e.target as HTMLInputElement;
  const file = input.files?.[0];
  input.value = '';
  if (!file) return;
  note.value = null;
  try {
    const r = await s.import(await file.text());
    note.value = r.draft ? `Imported ${r.key} as version ${r.version}, a draft (it has problems).` : `Imported ${r.key} as version ${r.version}.`;
    error.value = null;
    await refresh();
  } catch (err) { error.value = errorText(err); }
}

async function exportScript(x: ScriptSummary) {
  try {
    const r = await s.export(x.key);
    const url = URL.createObjectURL(new Blob([r.json], { type: 'application/json' }));
    const a = document.createElement('a');
    a.href = url;
    a.download = r.fileName;
    a.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  } catch (err) { error.value = errorText(err); }
}

async function remove(x: ScriptSummary) {
  if (!(await confirmDialog({ message: `Delete the script ${x.name} (${x.key}) and all its versions?`, okLabel: 'Delete', danger: true }))) return;
  let failed: string | null = null;
  try {
    await s.delete(x.key);
  } catch (err) {
    failed = errorCode(err) === 'in_use' ? `${x.key} is in use by a member, so it was not deleted: ${errorText(err)}` : errorText(err);
  }
  // The list again either way (in_use: its Used by is out of date); the outcome is said after, so the refresh keeps it.
  await refresh();
  note.value = failed ? null : `Deleted ${x.key}.`;
  if (failed) error.value = failed;
}

const usedBy = (x: ScriptSummary) => x.usedBy.map((u) => `${u.team} / ${u.member}${u.version === null ? '' : ` (v${u.version})`}`).join(', ');
</script>

<template>
  <ScriptEditor v-if="editing" :key="editing.n" :api="api" :teams="teams" :script="editing.script" :initial="editing.initial"
    @close="closeEditor" @saved="refresh" />
  <div v-else class="library" data-test="scripts-library">
    <div class="row bar">
      <span class="muted">Scripts can be shared by any team; a script team member runs one with its own settings.</span>
      <span class="grow" />
      <template v-if="!readOnly">
        <div class="new-wrap">
          <button type="button" class="primary" data-test="scripts-new" :aria-expanded="newOpen" @click="newOpen = !newOpen">New</button>
          <div v-if="newOpen" class="menu" role="menu" data-test="scripts-new-menu">
            <button v-for="t in TEMPLATES" :key="t.id" type="button" role="menuitem" :data-test="`scripts-new-${t.id}`" @click="newScript(t.doc)">{{ t.label }}</button>
            <button type="button" role="menuitem" data-test="scripts-new-concierge" @click="askConcierge">Ask the Concierge</button>
          </div>
        </div>
        <button type="button" data-test="scripts-import" @click="fileInput?.click()">Import…</button>
        <input ref="fileInput" type="file" accept=".json,application/json" hidden data-test="scripts-import-file" @change="importFile" />
      </template>
    </div>
    <p v-if="error" class="error" data-test="scripts-error">{{ error }}</p>
    <p v-if="note" class="muted" data-test="scripts-note">{{ note }}</p>
    <LoadingNote v-if="list === null && !error" text="Loading scripts…" />
    <p v-else-if="list && !list.length" class="muted" data-test="scripts-empty">No scripts yet. New starts one from a template.</p>
    <table v-else-if="list" class="table">
      <thead><tr><th>Name</th><th>Description</th><th>Version</th><th>Used by</th><th /></tr></thead>
      <tbody>
        <tr v-for="x in list" :key="x.id" data-test="script-row" :data-key="x.key">
          <td><button type="button" class="link" data-test="script-open" @click="open(x)">{{ x.name }}</button><div class="muted small"><code>{{ x.key }}</code></div></td>
          <td>{{ x.description }}</td>
          <td data-test="script-version">v{{ x.latestVersion }}<span v-if="x.draft" class="chip draft" data-test="script-draft" :title="x.runnableVersion ? `Members on latest run v${x.runnableVersion}` : 'No version checked clean yet'">draft</span></td>
          <td data-test="script-used-by">{{ x.usedBy.length ? usedBy(x) : '' }}<span v-if="!x.usedBy.length" class="muted">nobody</span></td>
          <td class="actions">
            <button type="button" class="mini" data-test="script-export" @click="exportScript(x)">Export</button>
            <button v-if="!readOnly && !x.usedBy.length" type="button" class="mini danger" data-test="script-delete" @click="remove(x)">Delete</button>
          </td>
        </tr>
      </tbody>
    </table>
  </div>
</template>

<style scoped>
.library { display: flex; flex-direction: column; gap: 8px; }
.bar { flex-wrap: wrap; }
.new-wrap { position: relative; }
.menu { position: absolute; right: 0; top: calc(100% + 4px); z-index: 5; background: var(--surface); border: 1px solid var(--line); border-radius: 6px;
  box-shadow: 0 8px 24px var(--shadow-menu); display: flex; flex-direction: column; min-width: 170px; padding: 4px; }
.menu button { border: 0; text-align: left; padding: 6px 10px; }
.menu button:hover { background: var(--hover); }
.table { border-collapse: collapse; width: 100%; font-size: 13px; }
.table th { text-align: left; font-weight: 500; color: var(--muted); font-size: 12px; border-bottom: 1px solid var(--line); padding: 4px 6px; }
.table td { padding: 5px 6px; border-bottom: 1px solid var(--line-soft); vertical-align: top; }
.actions { white-space: nowrap; text-align: right; }
.link { border: 0; background: transparent; padding: 0; color: var(--accent); font-weight: 500; text-align: left; }
.link:hover { text-decoration: underline; }
.draft { background: var(--pill-yellow-bg); color: var(--pill-yellow-ink); }
.mini { font-size: 12px; padding: 2px 8px; }
.small { font-size: 11.5px; }
</style>
