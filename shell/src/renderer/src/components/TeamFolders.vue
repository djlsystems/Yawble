<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue';
import type { TeamFolder, YawbleApi } from '../../../shared/api';
import { addTeamFolder, errorText, removeTeamFolder, resolveApi, teamFolders, when } from '../api';
import LoadingNote from './LoadingNote.vue';
import BusyLabel from './BusyLabel.vue';
import { useBusy } from '../busy';
import { usePhone } from '../phone';
import { isAbsolutePath } from '../policyFields';
import { readOnly, vChange } from '../remoteAccess';

/**
 * Team settings > Folders: the team's folder list (policy.teamFolders). Each folder is one outside the members' own
 * folders that every member of the team may read, or read and write, from its next run, members hired later too. The
 * person adds one (policy.addTeamFolder) after a confirm step that states exactly what it grants, and removes one
 * (policy.removeTeamFolder) after a confirm. The core checks every folder for every member and refuses the ones no
 * agent may have; its refusal is shown as it is. A read-only view lists the folders and has no controls. Paths are
 * plain text ({{ }}), never HTML.
 */
const props = defineProps<{ teamId: number; teamName: string; api?: YawbleApi }>();
const emit = defineEmits<{ changed: [] }>();
const api = resolveApi(props.api);
const phone = usePhone();
// The native picker in the desktop window only; a remote view types the path (as FolderAccess and TriggersDialog).
const canPick = typeof api.pickFolder === 'function' && !api.remote;

const folders = ref<TeamFolder[]>([]);
const ready = ref(false);
const error = ref<string | null>(null);
const note = ref<string | null>(null);
const work = useBusy();

const path = ref('');
const access = ref<TeamFolder['access']>('read');
/** The step waiting for the person's Confirm: adding the typed folder, or removing a listed one. */
const pending = ref<{ kind: 'add'; path: string; access: TeamFolder['access']; warnings: string[] } | { kind: 'remove'; folder: TeamFolder } | null>(null);
watch([path, access], () => { if (pending.value?.kind === 'add') pending.value = null; });

const words = (a: TeamFolder['access']) => (a === 'readwrite' ? 'read-write' : 'read-only');
const can = (a: TeamFolder['access']) => (a === 'readwrite' ? 'read and change' : 'read, and not change,');
const how = (f: TeamFolder) => [f.addedBy, f.addedVia ? `via ${f.addedVia}` : null, when(f.addedAt)].filter(Boolean).join(' · ');
const same = (a: string, b: string) => a.replace(/[\\/]+$/, '').toLowerCase() === b.replace(/[\\/]+$/, '').toLowerCase();

/** What Confirm does, in one plain sentence. */
const confirmText = computed(() => {
  const p = pending.value;
  if (!p) return '';
  if (p.kind === 'remove')
    return `This takes ${p.folder.path} away from every member of ${props.teamName}, from their next runs. A member that has the folder on its own Policy tab keeps it.`;
  const on = folders.value.find((f) => same(f.path, p.path));
  return on
    ? `This changes ${on.path} from ${words(on.access)} to ${words(p.access)} for every member of ${props.teamName}: they can ${can(p.access)} the files in it, from their next runs.`
    : `This gives every member of ${props.teamName} ${words(p.access)} access to ${p.path}: they can ${can(p.access)} the files in it, from their next runs. Members hired later get it too.`;
});

async function load() {
  try { folders.value = (await teamFolders(api, props.teamId)).folders; }
  catch (e) { error.value = errorText(e); }
  finally { ready.value = true; }
}

async function browse() {
  const picked = await api.pickFolder();
  if (picked) path.value = picked;
}

const review = () => work.run('review', async () => {
  error.value = null;
  note.value = null;
  const p = path.value.trim();
  if (!p) return;
  if (!isAbsolutePath(p)) { error.value = `"${p}" is not a full path. Give the folder's full path, such as C:\\work\\shared or /home/you/shared.`; return; }
  if (folders.value.some((f) => same(f.path, p) && f.access === access.value)) { note.value = `${p} is on the list already, ${words(access.value)}.`; return; }
  const warnings: string[] = [];
  if (typeof api.folderExists === 'function' && !api.remote && !(await api.folderExists(p).catch(() => true)))
    warnings.push(`${p} does not exist on this computer. You can still add it; the members have it once the folder is there.`);
  pending.value = { kind: 'add', path: p, access: access.value, warnings };
});

const confirm = () => work.run('confirm', async () => {
  const p = pending.value;
  if (!p) return;
  error.value = null;
  note.value = null;
  try {
    if (p.kind === 'add') {
      folders.value = (await addTeamFolder(api, props.teamId, p.path, p.access)).folders;
      note.value = `Added ${p.path}, ${words(p.access)}: the team's next runs have it.`;
      path.value = '';
    } else {
      folders.value = (await removeTeamFolder(api, props.teamId, p.folder.path)).folders;
      note.value = `Removed ${p.folder.path}: the team's next runs do not have it.`;
    }
    pending.value = null;
    emit('changed');
  } catch (e) {
    // The core's refusal, as it says it; the list is read again (another view may have changed it).
    error.value = errorText(e);
    pending.value = null;
    await load();
  }
});

onMounted(load);
defineExpose({ load });
</script>

<template>
  <div class="team-folders" :class="{ phone }" data-test="team-folders">
    <h4>The team's folders</h4>
    <p class="muted small" data-test="team-folders-about">
      Folders on this computer, outside the team's own folders, that every member of {{ teamName }} may use: agents and workflows alike, from their next run, members hired later too.
      Read-only lets them read the files; read-write lets them change them as well.
    </p>
    <p v-if="error" class="error small" role="alert" data-test="team-folders-error">{{ error }}</p>
    <p v-if="note" class="ok small" role="status" data-test="team-folders-note">{{ note }}</p>
    <LoadingNote v-if="!ready" text="Loading the team's folders…" />
    <p v-else-if="!folders.length" class="muted small" data-test="team-folders-empty">No folders are shared with the whole team.</p>
    <ul v-else class="list">
      <li v-for="f in folders" :key="f.path" class="row" data-test="team-folder" :data-path="f.path" :data-access="f.access">
        <code class="mono path" data-test="team-folder-path">{{ f.path }}</code>
        <span class="tag" data-test="team-folder-access">{{ words(f.access) }}</span>
        <span class="grow muted small" data-test="team-folder-added">{{ how(f) }}</span>
        <button v-if="!readOnly" v-change class="link" data-test="team-folder-remove" :aria-label="`Remove ${f.path}`" :disabled="work.any()"
          @click="pending = { kind: 'remove', folder: f }">Remove</button>
      </li>
    </ul>

    <div v-if="!readOnly" class="add" data-test="team-folder-add-row">
      <h4>Add a folder</h4>
      <div class="fields">
        <input v-model="path" class="grow mono" placeholder="Folder path, e.g. C:\work\shared" aria-label="Folder to add" data-test="team-folder-path-input" @keydown.enter="review" />
        <button v-if="canPick" type="button" data-test="team-folder-browse" :disabled="work.any()" @click="browse">Browse…</button>
      </div>
      <div class="fields">
        <label class="choice"><input v-model="access" type="radio" value="read" data-test="team-folder-access-read" /> Read-only</label>
        <label class="choice"><input v-model="access" type="radio" value="readwrite" data-test="team-folder-access-readwrite" /> Read-write</label>
        <span class="grow" />
        <button v-change class="primary" data-test="team-folder-add" :disabled="work.any() || !path.trim()" @click="review">
          <BusyLabel :busy="work.is('review')" text="Checking…">Add a folder</BusyLabel>
        </button>
      </div>
    </div>

    <div v-if="pending && !readOnly" class="confirm" role="group" aria-label="Confirm" data-test="team-folder-confirm-box">
      <template v-if="pending.kind === 'add'">
        <p v-for="w in pending.warnings" :key="w" class="warn small" data-test="team-folder-warning">{{ w }}</p>
      </template>
      <p data-test="team-folder-confirm-text">{{ confirmText }}</p>
      <p v-if="pending.kind === 'add'" class="muted small">Yawble checks the folder for every member first. Folders that hold secrets, Yawble's own data, another team's work or system files are refused.</p>
      <div class="fields">
        <span class="grow" />
        <button data-test="team-folder-cancel" :disabled="work.any()" @click="pending = null">Cancel</button>
        <button v-change :class="pending.kind === 'remove' ? 'danger' : 'primary'" data-test="team-folder-confirm" :disabled="work.any()" :aria-busy="work.is('confirm')" @click="confirm">
          <BusyLabel :busy="work.is('confirm')" :text="pending.kind === 'remove' ? 'Removing…' : 'Adding…'">{{ pending.kind === 'remove' ? 'Remove for the team' : 'Give the team this folder' }}</BusyLabel>
        </button>
      </div>
    </div>
  </div>
</template>

<style scoped>
.team-folders { display: flex; flex-direction: column; gap: 8px; }
.team-folders h4 { margin: 0; font-size: 14px; }
.team-folders p { margin: 0; overflow-wrap: anywhere; }
.list { list-style: none; margin: 0; padding: 0; }
.row { display: flex; flex-wrap: wrap; gap: 8px; align-items: baseline; padding: 4px 0; border-top: 1px solid var(--line-soft); }
.path { overflow-wrap: anywhere; user-select: all; }
.mono { font-family: ui-monospace, Consolas, monospace; }
.tag { font-size: 11px; font-weight: 600; color: var(--ink-soft); border: 1px solid var(--line); border-radius: 10px; padding: 0 6px; white-space: nowrap; }
.add { display: flex; flex-direction: column; gap: 6px; border-top: 1px solid var(--line-soft); padding-top: 8px; }
.fields { display: flex; flex-wrap: wrap; gap: 8px; align-items: center; }
.fields input.grow { min-width: 0; font: inherit; font-family: ui-monospace, Consolas, monospace; font-size: 13px; padding: 5px 8px; border: 1px solid var(--line); border-radius: 6px; }
.choice { display: inline-flex; flex-direction: row; align-items: center; gap: 6px; }
.confirm { border: 1px solid var(--warn-line); background: var(--warn-bg); border-radius: 8px; padding: 8px 10px; display: flex; flex-direction: column; gap: 6px; }
.grow { flex: 1; }
.link { border: 0; background: transparent; color: var(--accent); }
.primary { background: var(--accent); color: var(--on-accent); border-color: var(--accent); }
.danger { background: var(--bad); color: var(--on-accent); border-color: var(--bad); }
.muted { color: var(--muted); }
.small { font-size: 12px; }
.warn { color: var(--warn); }
.ok { color: var(--ok); }
.error { color: var(--bad); white-space: pre-line; }
/* A phone: one column, and every control a 44 px tap target. */
.phone .row { flex-direction: column; align-items: stretch; gap: 2px; padding: 8px 0; }
.phone .link { min-height: 44px; text-align: left; padding: 0; }
.phone .fields { align-items: stretch; }
.phone .fields input.grow { flex: 1 1 100%; min-height: 44px; box-sizing: border-box; }
.phone .fields button { min-height: 44px; flex: 1 1 auto; }
.phone .choice { min-height: 44px; flex: 1 1 40%; }
</style>
