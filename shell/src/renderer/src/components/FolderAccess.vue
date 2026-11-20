<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import type { MemberPolicyView, PolicyPatchDoc, YawbleApi } from '../../../shared/api';
import { errorText } from '../api';
import { FOLDER_LISTS, folderRows, isAbsolutePath, SOURCE_LABEL, type FolderList, type FolderRow } from '../policyFields';
import BusyLabel from './BusyLabel.vue';
import type { Busy } from '../busy';

// B110: Member settings > Policy, at the top: which folders this member may use, and the one obvious step to give it one.
// Each row is an effective folder with where it comes from. Adding a folder beyond the global policy is a loosening, so it
// goes through the core's policy.previewLoosen and a confirm of exactly what it gives, then policy.saveLoosen (the core
// still refuses anything else; nothing about who may loosen changes here). Removing the member's own entry confirms too.
const props = defineProps<{
  view: MemberPolicyView; api: YawbleApi; memberId: number; memberName: string; work: Busy;
  /** The folders a save just added, per list: highlighted. */
  fresh?: Partial<Record<FolderList, string[]>>;
}>();
const emit = defineEmits<{ applied: [view: MemberPolicyView]; error: [text: string | null]; dirty: [dirty: boolean]; nothing: [text: string] }>();

const addPath = ref('');
const addMode = ref<'readwrite' | 'readonly'>('readwrite');
/** The step waiting for the person's Confirm: what it does in words, and the exact call it makes. */
interface Pending {
  kind: 'add' | 'remove';
  lines: string[];
  title: string;
  warnings: string[];
  save: () => Promise<MemberPolicyView>;
}
const pending = ref<Pending | null>(null);
const busy = computed(() => props.work.any());
// The native picker in the desktop window only; a remote view types the path (as TriggersDialog).
const canPick = typeof props.api.pickFolder === 'function' && !props.api.remote;

const lists = computed(() => FOLDER_LISTS.map((l) => ({ ...l, rows: folderRows(props.view, l.list) })));
const dirty = computed(() => addPath.value.trim().length > 0 || pending.value !== null);
watch(dirty, (d) => emit('dirty', d), { immediate: true });
watch(addPath, () => { if (pending.value?.kind === 'add') pending.value = null; });

const modeWords = (m: FolderList) => (m === 'readwrite' ? 'read-write' : m === 'readonly' ? 'read-only' : 'denied');
const isFresh = (list: FolderList, p: string) => props.fresh?.[list]?.includes(p) ?? false;

async function browse() {
  const picked = await props.api.pickFolder();
  if (picked) addPath.value = picked;
}

/** Plain warnings for a path: relative, or (when the window can look) not a folder on this computer. */
async function warningsFor(p: string): Promise<string[]> {
  if (!isAbsolutePath(p)) return [`"${p}" is not a full path (such as C:\\work\\shared or /home/you/shared). The sandbox may refuse it or read it from somewhere else.`];
  if (typeof props.api.folderExists !== 'function') return [];
  const there = await props.api.folderExists(p).catch(() => true);
  return there ? [] : [`${p} does not exist on this computer. You can still save it; it applies from the next run, once the folder is there.`];
}

function loosenWith(edit: (fs: { readonly: string[]; readwrite: string[] }) => void): PolicyPatchDoc {
  const doc: any = JSON.parse(JSON.stringify(props.view.loosen ?? {}));
  doc.filesystem ??= {};
  const fs = { readonly: [...(doc.filesystem.readonly ?? [])], readwrite: [...(doc.filesystem.readwrite ?? [])] };
  edit(fs);
  for (const k of ['readonly', 'readwrite'] as const) {
    if (fs[k].length) doc.filesystem[k] = fs[k]; else delete doc.filesystem[k];
  }
  if (Object.keys(doc.filesystem).length === 0) delete doc.filesystem;
  return doc;
}

async function previewOf(doc: PolicyPatchDoc): Promise<string[]> {
  return (await props.api.call<{ loosened: string[] }>('policy.previewLoosen', { memberId: props.memberId, loosen: doc })).loosened;
}
const saveLoosen = (doc: PolicyPatchDoc, confirmed: string[]) => () =>
  props.api.call<MemberPolicyView>('policy.saveLoosen', { memberId: props.memberId, loosen: doc, confirmed });
/** The member's loosened items now, as the core lists them (team hosts aside: the loosen block never holds those). */
const currentLoosened = () => (props.view.effective.loosened ?? []).filter((l) => !l.endsWith(' (team)'));

const add = () => props.work.run('folder-add', doAdd);
async function doAdd() {
  emit('error', null);
  const p = addPath.value.trim();
  if (!p) return;
  const mode = addMode.value;
  try {
    const warnings = await warningsFor(p);
    const doc = loosenWith((fs) => { if (!fs[mode].some((x) => x === p)) fs[mode].push(p); });
    const preview = await previewOf(doc);
    const before = new Set(currentLoosened());
    const gives = preview.filter((l) => !before.has(l));
    if (gives.length === 0) {
      emit('nothing', `Nothing changed: ${props.memberName}'s current access already covers ${modeWords(mode)} on ${p}.`);
      addPath.value = '';
      return;
    }
    pending.value = {
      kind: 'add', warnings,
      title: `This gives ${props.memberName} ${modeWords(mode)} on:`,
      lines: gives.map((l) => l.replace(/^filesystem\.(readwrite|readonly) \+ /, '')),
      save: saveLoosen(doc, preview),
    };
  } catch (e) {
    emit('error', errorText(e));
  }
}

const remove = (list: FolderList, row: FolderRow) => props.work.run('folder-remove', () => doRemove(list, row));
async function doRemove(list: FolderList, row: FolderRow) {
  emit('error', null);
  const own = row.own!;
  try {
    if (row.source === 'loosen') {
      const doc = loosenWith((fs) => { if (list !== 'denied') fs[list] = fs[list].filter((x) => x !== own); });
      const preview = await previewOf(doc);
      pending.value = {
        kind: 'remove', warnings: [],
        title: `This takes away ${props.memberName}'s ${modeWords(list)} access to:`,
        lines: [row.path],
        save: saveLoosen(doc, preview),
      };
    } else {
      const doc: any = JSON.parse(JSON.stringify(props.view.override ?? {}));
      doc.filesystem[list] = (doc.filesystem[list] as string[]).filter((x) => x !== own);
      pending.value = {
        kind: 'remove', warnings: [],
        title: list === 'denied'
          ? `This takes out of ${props.memberName}'s override (back to the global policy) the denied folder:`
          : `This takes out of ${props.memberName}'s override the ${modeWords(list)} folder:`,
        lines: [row.path],
        save: () => props.api.call<MemberPolicyView>('policy.saveOverride', { memberId: props.memberId, override: doc }),
      };
    }
  } catch (e) {
    emit('error', errorText(e));
  }
}

const confirm = () => props.work.run('folder-confirm', doConfirm);
async function doConfirm() {
  const p = pending.value;
  if (!p) return;
  emit('error', null);
  try {
    const v = await p.save();
    pending.value = null;
    if (p.kind === 'add') addPath.value = '';
    emit('applied', v);
  } catch (e) {
    emit('error', errorText(e));
  }
}
const cancel = () => { pending.value = null; };
</script>

<template>
  <section class="folders" data-test="folder-access">
    <h3>Folder access</h3>
    <p class="small muted">What {{ memberName }} may use from its next run. Add a folder here; it is checked and you confirm exactly what it gives.</p>
    <div class="lists">
      <div v-for="l in lists" :key="l.list" class="list" :data-test="`folders-${l.list}`">
        <h4>{{ l.label }} <span class="muted small">({{ l.rows.length }})</span></h4>
        <p v-if="l.rows.length === 0" class="small muted">(none)</p>
        <ul>
          <li v-for="r in l.rows" :key="`${r.source}:${r.path}`" class="folder" :class="{ fresh: isFresh(l.list, r.path) }"
            data-test="folder-row" :data-path="r.path" :data-source="r.source" :data-changed="isFresh(l.list, r.path) ? 'true' : undefined">
            <code class="mono path" data-test="folder-path">{{ r.path }}</code>
            <span class="source small" data-test="folder-source">{{ SOURCE_LABEL[r.source] }}<template v-if="r.covered"> · already covered, not added</template></span>
            <button v-if="r.own !== null" class="small" data-test="folder-remove" :disabled="busy" :aria-label="`Remove ${r.path}`" @click="remove(l.list, r)">Remove</button>
          </li>
        </ul>
      </div>
    </div>

    <div class="add" data-test="folder-add-row">
      <input v-model="addPath" class="grow mono" placeholder="Folder path, e.g. C:\work\shared" aria-label="Folder to add" data-test="folder-add-path" @keydown.enter="add" />
      <button v-if="canPick" type="button" data-test="folder-browse" :disabled="busy" @click="browse">Browse…</button>
      <select v-model="addMode" aria-label="Access" data-test="folder-add-mode">
        <option value="readwrite">Read/write</option>
        <option value="readonly">Read-only</option>
      </select>
      <button class="primary" data-test="folder-add" :disabled="busy || !addPath.trim()" :aria-busy="work.is('folder-add')" @click="add"><BusyLabel :busy="work.is('folder-add')" text="Checking…">Add folder</BusyLabel></button>
      <span v-if="addPath.trim() && !pending" class="unsaved small" data-test="folder-unsaved">Unsaved changes</span>
    </div>

    <div v-if="pending" class="confirm" role="group" aria-label="Confirm" data-test="folder-confirm-box">
      <p v-for="w in pending.warnings" :key="w" class="warn small" data-test="folder-warning">⚠ {{ w }}</p>
      <p><strong data-test="folder-confirm-title">{{ pending.title }}</strong></p>
      <ul><li v-for="x in pending.lines" :key="x" class="mono" data-test="folder-confirm-item">{{ x }}</li></ul>
      <p class="small muted">It applies from the next run; a running sandbox keeps its policy.</p>
      <div class="row"><span class="grow"></span>
        <span class="unsaved small" data-test="folder-unsaved">Unsaved changes</span>
        <button data-test="folder-cancel" :disabled="busy" @click="cancel">Cancel</button>
        <button class="primary" data-test="folder-confirm" :disabled="busy" :aria-busy="work.is('folder-confirm')" @click="confirm"><BusyLabel :busy="work.is('folder-confirm')" text="Saving…">{{ pending.warnings.length ? 'Confirm anyway' : 'Confirm' }}</BusyLabel></button>
      </div>
    </div>
  </section>
</template>

<style scoped>
.folders { border: 1px solid var(--line); border-radius: 8px; padding: 8px 12px; margin-bottom: 12px; }
h3 { margin: 2px 0 2px; font-size: 14px; }
h4 { margin: 6px 0 2px; font-size: 13px; }
.small { font-size: 12px; }
.lists { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 4px 14px; }
@media (max-width: 720px) { .lists { grid-template-columns: 1fr; } }
ul { list-style: none; margin: 0; padding: 0; }
.folder { display: flex; flex-wrap: wrap; align-items: center; gap: 2px 8px; padding: 3px 4px; border-radius: 4px; border-bottom: 1px solid var(--line-soft); }
.folder .path { word-break: break-all; flex: 1 1 auto; min-width: 0; }
.folder .source { color: var(--muted); }
.folder.fresh { background: var(--warn-bg); animation: flash 2.4s ease-out 1; }
@keyframes flash { 0% { box-shadow: 0 0 0 2px var(--accent); } 100% { box-shadow: 0 0 0 0 transparent; } }
.add { display: flex; flex-wrap: wrap; gap: 6px; align-items: center; margin-top: 8px; }
.add input { min-width: 12em; }
.unsaved { color: var(--warn); font-weight: 600; }
.warn { color: var(--warn); }
.confirm { border: 2px solid var(--warn-line-strong); background: var(--warn-bg); border-radius: 6px; padding: 6px 10px; margin-top: 8px; }
.confirm p { margin: 4px 0; }
.confirm ul { margin: 2px 0 6px; padding-left: 18px; list-style: disc; word-break: break-all; }
.confirm .row { flex-wrap: wrap; gap: 6px; }
</style>
