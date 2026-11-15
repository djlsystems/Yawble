<script setup lang="ts">
// The documents browser, shared by a team's Documents dialog and the Documents page.
import { computed, onMounted, ref } from 'vue';
import type { DocEntry, DocFile, DocList, YawbleApi, TeamSummary } from '../../../shared/api';
import { errorText, resolveApi } from '../api';
import { renderMarkdown } from '../markdown';
import LoadingNote from './LoadingNote.vue';
import BusyLabel from './BusyLabel.vue';
import { useBusy } from '../busy';

/**
 * A team's documents: what its agents write for the person (reports, specs, plans) and anything the person adds. Browse
 * folders, read Markdown formatted, view images and text, edit and create text files, upload, rename, delete, and open
 * the folder on this computer. The team's agents read and write the same folder ($YAWBLE_TEAM_DOCS).
 */
const props = defineProps<{ team: TeamSummary; api?: YawbleApi; above?: string; fill?: boolean }>();
const emit = defineEmits<{ up: [] }>();
const api = resolveApi(props.api);

const folder = ref('');
const list = ref<DocList | null>(null);
const open = ref<DocFile | null>(null);
const error = ref<string | null>(null);
const busy = ref(false);
const editing = ref(false);
const draft = ref('');
const naming = ref<{ kind: 'folder' | 'file' | 'rename'; entry?: DocEntry; text: string } | null>(null);
const confirmDelete = ref<string | null>(null);
const picker = ref<HTMLInputElement | null>(null);
// The person's actions (save, create, rename, delete, upload): one at a time, the clicked button shows the spinner.
const work = useBusy();
const doing = (what: string) => work.is('docs', what);
const acting = computed(() => work.any());
// A file being opened (docs.read): the viewer shows the loading state until it arrives.
const opening = ref<string | null>(null);

const crumbs = computed(() => {
  const parts = folder.value ? folder.value.split('/') : [];
  return [{ label: props.above ? props.team.name : 'Documents', path: '' }, ...parts.map((p, i) => ({ label: p, path: parts.slice(0, i + 1).join('/') }))];
});
const rendered = computed(() => (open.value?.kind === 'markdown' && open.value.text != null ? renderMarkdown(open.value.text) : ''));
const editable = computed(() => open.value?.kind === 'markdown' || open.value?.kind === 'text');
const canReveal = computed(() => typeof api.openDocuments === 'function');

async function call<T>(method: string, params: Record<string, unknown>): Promise<T | null> {
  busy.value = true;
  error.value = null;
  try { return await api.call<T>(method, { teamId: props.team.id, ...params }); }
  catch (e) { error.value = errorText(e); return null; }
  finally { busy.value = false; }
}

async function load(path = folder.value) {
  const r = await call<DocList>('docs.list', { path });
  if (r) { list.value = r; folder.value = r.path; }
}

async function view(e: DocEntry) {
  if (e.folder) { open.value = null; editing.value = false; await load(e.path); return; }
  opening.value = e.name;
  try {
    const r = await call<DocFile>('docs.read', { path: e.path });
    if (r) { open.value = r; editing.value = false; }
  } finally { if (opening.value === e.name) opening.value = null; }
}

function startEdit() { draft.value = open.value?.text ?? ''; editing.value = true; }

const save = () => work.run('docs', doSave, 'save');
async function doSave() {
  if (!open.value) return;
  const r = await call<DocFile>('docs.write', { path: open.value.path, text: draft.value });
  if (r) { open.value = r; editing.value = false; await load(); }
}

const confirmName = () => work.run('docs', doConfirmName, 'name');
async function doConfirmName() {
  const n = naming.value;
  if (!n || !n.text.trim()) return;
  if (n.kind === 'folder') {
    if (await call('docs.mkdir', { path: folder.value, name: n.text.trim() })) await load();
  } else if (n.kind === 'file') {
    const name = /\.[a-z0-9]+$/i.test(n.text.trim()) ? n.text.trim() : `${n.text.trim()}.md`;
    const path = folder.value ? `${folder.value}/${name}` : name;
    const r = await call<DocFile>('docs.write', { path, text: name.endsWith('.md') ? `# ${name.replace(/\.md$/, '')}\n\n` : '' });
    if (r) { await load(); open.value = r; startEdit(); }
  } else if (n.entry) {
    const r = await call<{ path: string }>('docs.rename', { path: n.entry.path, name: n.text.trim() });
    if (r) { if (open.value?.path === n.entry.path) open.value = null; await load(); }
  }
  naming.value = null;
}

const remove = (path: string) => work.run('docs', async () => {
  if (await call('docs.delete', { path })) {
    if (open.value && (open.value.path === path || open.value.path.startsWith(path + '/'))) open.value = null;
    await load();
  }
  confirmDelete.value = null;
}, 'delete');

const upload = (files: FileList | null) => work.run('docs', () => doUpload(files), 'upload');
async function doUpload(files: FileList | null) {
  for (const f of Array.from(files ?? [])) {
    if (f.size > 25 * 1024 * 1024) { error.value = `${f.name} is larger than 25 MB`; continue; }
    const bytes = new Uint8Array(await f.arrayBuffer());
    let binary = '';
    for (let i = 0; i < bytes.length; i += 0x8000) binary += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
    await call('docs.upload', { path: folder.value, name: f.name, base64: btoa(binary) });
  }
  if (picker.value) picker.value.value = '';
  await load();
}

const size = (n: number | null) => n == null ? '' : n < 1024 ? `${n} B` : n < 1048576 ? `${(n / 1024).toFixed(0)} KB` : `${(n / 1048576).toFixed(1)} MB`;

onMounted(() => void load(''));
</script>

<template>
  <div class="browser" :class="{ fill }" data-test="docs-browser">
    <div class="bar">
      <nav class="crumbs" data-test="docs-crumbs">
        <template v-if="above"><a href="#" data-test="docs-up" @click.prevent="emit('up')">{{ above }}</a><span class="sep">/</span></template>
        <template v-for="(c, i) in crumbs" :key="c.path">
          <span v-if="i" class="sep">/</span>
          <a href="#" @click.prevent="open = null; load(c.path)">{{ c.label }}</a>
        </template>
      </nav>
      <span class="grow" />
      <button data-test="docs-new-file" :disabled="busy || acting" @click="naming = { kind: 'file', text: '' }">New file</button>
      <button data-test="docs-new-folder" :disabled="busy || acting" @click="naming = { kind: 'folder', text: '' }">New folder</button>
      <button data-test="docs-upload" :disabled="busy || acting" :aria-busy="doing('upload')" @click="picker?.click()"><BusyLabel :busy="doing('upload')" text="Uploading…">Upload</BusyLabel></button>
      <input ref="picker" type="file" multiple hidden data-test="docs-upload-input" @change="upload(($event.target as HTMLInputElement).files)" />
      <button v-if="canReveal" data-test="docs-reveal" title="Open the folder on this computer" @click="api.openDocuments!(team.id)">Open folder</button>
    </div>
    <div v-if="naming" class="naming">
      <input v-model="naming.text" class="grow" autofocus data-test="docs-name"
        :placeholder="naming.kind === 'folder' ? 'Folder name' : naming.kind === 'file' ? 'File name (notes.md)' : 'New name'" @keydown.enter="confirmName" />
      <button class="primary" data-test="docs-name-ok" :disabled="acting" @click="confirmName"><BusyLabel :busy="doing('name')" :text="naming.kind === 'rename' ? 'Renaming…' : 'Creating…'">{{ naming.kind === 'rename' ? 'Rename' : 'Create' }}</BusyLabel></button>
      <button :disabled="acting" @click="naming = null">Cancel</button>
    </div>
    <p v-if="error" class="error" data-test="docs-error">{{ error }}</p>
    <div class="split" data-test="docs-split">
      <ul class="files" data-test="docs-list">
        <li v-if="!list && !error" class="loading"><LoadingNote text="Loading documents…" /></li>
        <li v-for="e in list?.entries ?? []" :key="e.path" :class="{ on: open?.path === e.path }">
          <button class="name" :data-test="`doc-${e.path}`" @click="view(e)"><span class="ico">{{ e.folder ? '📁' : '📄' }}</span>{{ e.name }}</button>
          <span class="muted small">{{ size(e.size) }}</span>
          <span class="acts">
            <button class="mini" title="Rename" @click="naming = { kind: 'rename', entry: e, text: e.name }">✎</button>
            <template v-if="confirmDelete === e.path">
              <button class="mini danger" data-test="doc-delete-yes" :disabled="acting" @click="remove(e.path)"><BusyLabel :busy="doing('delete')" text="Deleting…">Delete</BusyLabel></button>
              <button class="mini" :disabled="acting" @click="confirmDelete = null">Keep</button>
            </template>
            <button v-else class="mini" title="Delete" :data-test="`doc-delete-${e.path}`" @click="confirmDelete = e.path">🗑</button>
          </span>
        </li>
        <li v-if="list && !list.entries.length" class="muted empty" data-test="docs-empty">
          Nothing here yet. The team writes reports, specs and plans here; you can add files too.
        </li>
      </ul>
      <section class="viewer" data-test="docs-viewer">
        <LoadingNote v-if="opening" :text="`Opening ${opening}…`" />
        <template v-else-if="open">
          <header class="vh">
            <b class="vname">{{ open.name }}</b>
            <span class="muted small">{{ size(open.size) }}</span>
            <span class="grow" />
            <template v-if="editable">
              <button v-if="!editing" data-test="doc-edit" @click="startEdit">Edit</button>
              <template v-else>
                <button class="primary" data-test="doc-save" :disabled="busy || acting" :aria-busy="doing('save')" @click="save"><BusyLabel :busy="doing('save')" text="Saving…">Save</BusyLabel></button>
                <button :disabled="acting" @click="editing = false">Cancel</button>
              </template>
            </template>
          </header>
          <textarea v-if="editing" v-model="draft" class="editor vbody" data-test="doc-editor" spellcheck="false"></textarea>
          <div v-else-if="open.kind === 'markdown'" class="md vbody" v-html="rendered"></div>
          <pre v-else-if="open.kind === 'text'" class="text vbody">{{ open.text }}</pre>
          <img v-else-if="open.kind === 'image'" :src="open.dataUrl ?? ''" :alt="open.name" class="img vbody" />
          <p v-else-if="open.kind === 'large'" class="muted">Too large to show here (over 2 MB). Open the folder to see it.</p>
          <p v-else class="muted">This kind of file is not shown here. Open the folder to see it.</p>
        </template>
        <p v-else class="muted pick">Pick a file to read it.</p>
      </section>
    </div>
  </div>
</template>

<style scoped>
.loading { list-style: none; padding: 4px 8px; }
.bar { display: flex; gap: 6px; align-items: center; margin-bottom: 8px; flex-wrap: wrap; }
.crumbs { font-size: 13px; }
.crumbs a { color: var(--accent); text-decoration: none; }
.sep { color: var(--muted); margin: 0 4px; }
.grow { flex: 1; }
.naming { display: flex; gap: 6px; margin-bottom: 8px; }
.naming input { font: inherit; padding: 4px 8px; border: 1px solid var(--line); border-radius: 6px; }
.split { display: grid; grid-template-columns: minmax(220px, 300px) 1fr; gap: 12px; min-height: 380px; }
.files { list-style: none; margin: 0; padding: 0; border: 1px solid var(--line-soft); border-radius: 8px; overflow: auto; max-height: 60vh; }
.files li { display: flex; align-items: center; gap: 6px; padding: 3px 6px; border-bottom: 1px solid var(--line-soft); }
.files li.on { background: var(--hover); }
.files .name { flex: 1; text-align: left; border: 0; background: transparent; padding: 3px 2px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.ico { margin-right: 6px; }
.acts { display: flex; gap: 2px; }
.mini { border: 0; background: transparent; padding: 2px 4px; font-size: 12px; }
.mini.danger { color: var(--bad); font-weight: 600; }
.empty { padding: 10px; font-size: 13px; }
.viewer { border: 1px solid var(--line-soft); border-radius: 8px; padding: 10px 12px; overflow: auto; max-height: 60vh; min-width: 0; }
.vh { display: flex; gap: 8px; align-items: center; margin-bottom: 8px; }
.vname { overflow-wrap: anywhere; }
.editor { width: 100%; min-height: 340px; font: 13px/1.45 ui-monospace, Consolas, monospace; border: 1px solid var(--line); border-radius: 6px; padding: 8px; box-sizing: border-box; }
.text { white-space: pre-wrap; overflow-wrap: anywhere; font-size: 12.5px; margin: 0; }
.img { max-width: 100%; }
.pick { margin-top: 40px; text-align: center; }
/* fill (the Documents page): the browser is a column; the toolbar rows keep their height and the split takes the rest.
   The list scrolls on its own; in the preview the header row (name, size, Edit / Save / Cancel) stays on top and the
   content scrolls (the editor fills the pane). min-height: 0 all the way down; no containment. */
.browser.fill { height: 100%; min-height: 0; display: flex; flex-direction: column; }
.browser.fill > * { flex: none; }
.browser.fill > .split { flex: 1 1 0; min-height: 0; grid-template-rows: minmax(0, 1fr); }
.browser.fill .files { max-height: none; min-height: 0; }
.browser.fill .viewer { max-height: none; min-height: 0; display: flex; flex-direction: column; overflow: hidden; }
.browser.fill .viewer > * { flex: none; }
.browser.fill .viewer > .vbody { flex: 1 1 0; min-height: 0; overflow: auto; }
.browser.fill .viewer > .editor { min-height: 0; resize: none; }
.browser.fill .viewer > .img { object-fit: contain; align-self: flex-start; }
.primary { background: var(--accent); color: var(--on-accent); border-color: var(--accent); }
.muted { color: var(--muted); }
.small { font-size: 12px; }
.error { color: var(--bad); }
</style>
