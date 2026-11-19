<script setup lang="ts">
import { vChange } from '../remoteAccess';
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import type { BacklogItem, BacklogItemDetail, YawbleApi, TeamSummary } from '../../../shared/api';
import { errorText, resolveApi } from '../api';
import { renderMarkdown } from '../markdown';
import { ago } from './cards';
import LoadingNote from '../components/LoadingNote.vue';
import BusyLabel from '../components/BusyLabel.vue';
import Icon from '../components/Icon.vue';
import { useNarrow } from '../narrowScreen';
import { openWorkflowsPage, type BacklogRequest } from '../workflows/page';

/**
 * The Backlog: the person's list of work not yet given to a team, in priority order. An item
 * is a SPEC. It is pending until the person has read it and marks it ready; a ready item is dispatched to a team's
 * Manager (or to a new team made for it), which cuts it into cards. When that work is declared complete the item reads
 * "declared"; only the person marks it implemented. Every dispatch is kept with its workflow, cards and usage.
 */
// teamsLoading: the app's first overview (the teams to dispatch to) has not arrived yet.
// focus (B104): open with this item selected (an Item link on the Workflows page; an archived one in the archive).
const props = defineProps<{ teams: TeamSummary[]; teamsLoading?: boolean; api?: YawbleApi; pollMs?: number; focus?: BacklogRequest | null }>();
const emit = defineEmits<{ openTeam: [id: number]; board: [teamId: number | null] }>();
const api = resolveApi(props.api);

const tab = ref<'live' | 'archived'>('live');
const items = ref<BacklogItem[]>([]);
const error = ref<string | null>(null);
const busy = ref(false);
// Which action is running (a method, or a finer name like "ready"): that button shows the spinner; all are held.
const doing = ref<string | null>(null);
// The list's first answer for this tab (polls after it keep what is shown).
const loaded = ref(false);
const openId = ref<number | null>(null);
const detail = ref<BacklogItemDetail | null>(null);
const open = computed(() => items.value.find((i) => i.id === openId.value) ?? null);

const StateLabel: Record<BacklogItem['state'], string> = { pending: 'Pending', ready: 'Ready', declared: 'Declared done', implemented: 'Implemented' };
// What the person sees: a dispatched item that is not yet declared done is In flight (its cards are being worked) or
// Dispatched (sent, nothing being worked right now), not Ready; the stored state stays as it is.
type Shown = BacklogItem['state'] | 'dispatched' | 'flight';
const ShownLabel: Record<Shown, string> = { ...StateLabel, dispatched: 'Dispatched', flight: 'In flight' };
const ShownOrder: Shown[] = ['pending', 'ready', 'dispatched', 'flight', 'declared', 'implemented'];
function shownState(i: BacklogItem): Shown {
  if ((i.state === 'ready' || i.state === 'pending') && i.dispatchedAt) return i.inFlight ? 'flight' : 'dispatched';
  return i.state;
}
const TitleMax = 200;

// ---- loading
async function load() {
  const t = tab.value;
  try {
    const got = await api.call<BacklogItem[]>('backlog.list', { archived: t === 'archived' });
    if (t !== tab.value) return; // the person switched tabs meanwhile
    items.value = got;
    loaded.value = true;
    error.value = null;
    if (openId.value !== null && !open.value) openId.value = null;
    focusItem();
  } catch (e) { if (t === tab.value) error.value = errorText(e); }
}
async function loadDetail() {
  if (openId.value === null) { detail.value = null; return; }
  try { detail.value = await api.call<BacklogItemDetail>('backlog.item', { id: openId.value }); } catch (e) { error.value = errorText(e); }
}
async function act<T>(method: string, params: Record<string, unknown>, label = method): Promise<T | null> {
  if (busy.value) return null;
  busy.value = true;
  doing.value = label;
  error.value = null;
  try { return await api.call<T>(method, params); } catch (e) { error.value = errorText(e); return null; } finally { busy.value = false; doing.value = null; }
}
function switchTab(t: 'live' | 'archived') {
  if (t === tab.value) return;
  tab.value = t; openId.value = null; loaded.value = false; items.value = []; error.value = null;
  void load();
}

// ---- opened at an item (B104: an Item link): its tab, then the item selected once the list has it ----
const pendingFocus = ref<{ label: string; archived: boolean; other: boolean } | null>(null);
function focusItem() {
  const f = pendingFocus.value;
  if (!f || !loaded.value) return;
  const hit = items.value.find((i) => i.label === f.label);
  if (hit) { pendingFocus.value = null; select(hit); return; }
  // Not in this tab (restored or archived since the link was drawn): the other tab, once.
  if (!f.other) { pendingFocus.value = { ...f, other: true }; switchTab(tab.value === 'live' ? 'archived' : 'live'); return; }
  pendingFocus.value = null;
}
watch(() => props.focus?.seq, () => {
  const f = props.focus;
  if (!f) return;
  pendingFocus.value = { label: f.label, archived: f.archived, other: false };
  const want = f.archived ? 'archived' : 'live';
  if (tab.value !== want) switchTab(want);
  else focusItem();
}, { immediate: true });

let timer: ReturnType<typeof setInterval> | null = null;
onMounted(() => { void load(); timer = setInterval(() => { void load(); void loadDetail(); }, props.pollMs ?? 5000); });
onBeforeUnmount(() => { if (timer) clearInterval(timer); });

// ---- filter and sort (Yawble: the # column is priority; drag only when sorted by # with no filter)
type SortKey = 'pos' | 'label' | 'title' | 'state' | 'team' | 'created' | 'updated' | 'archived';
const sort = ref<{ key: SortKey; desc: boolean }>({ key: 'pos', desc: false });
const search = ref('');
const teamFilter = ref('');
const teamOptions = computed(() => [...new Set(items.value.map((i) => i.dispatchedTeam).filter((t): t is string => !!t))].sort());
const position = computed(() => new Map(items.value.map((i, n) => [i.id, n + 1])));
const shown = computed(() => {
  const q = search.value.trim().toLowerCase();
  const list = items.value.filter((i) => (!q || i.title.toLowerCase().includes(q) || i.label.toLowerCase().includes(q) || i.body.toLowerCase().includes(q))
    && (!teamFilter.value || i.dispatchedTeam === teamFilter.value));
  const key = (i: BacklogItem): string | number => {
    switch (sort.value.key) {
      case 'pos': return position.value.get(i.id) ?? 0;
      case 'label': return i.id;
      case 'title': return i.title.toLowerCase();
      case 'state': return ShownOrder.indexOf(shownState(i));
      case 'team': return (i.dispatchedTeam ?? '~').toLowerCase();
      case 'created': return i.createdAt;
      case 'updated': return i.updatedAt;
      case 'archived': return i.archivedAt ?? '';
    }
  };
  const sorted = [...list].sort((a, b) => { const x = key(a); const y = key(b); return x < y ? -1 : x > y ? 1 : 0; });
  return sort.value.desc ? sorted.reverse() : sorted;
});
function sortBy(key: SortKey) { sort.value = { key, desc: sort.value.key === key ? !sort.value.desc : false }; }
const arrow = (key: SortKey) => (sort.value.key === key ? (sort.value.desc ? ' ▾' : ' ▴') : '');
const canDrag = computed(() => tab.value === 'live' && sort.value.key === 'pos' && !sort.value.desc && !search.value.trim() && !teamFilter.value);

// ---- a phone (B037): the rows wrap (no table columns), sorting is a select, a row's actions are in its ⋯ menu, and an
// opened item takes the page (Back returns to the list).
const phone = useNarrow();
const menuFor = ref<number | null>(null);
const sortKeys = computed<{ key: SortKey; label: string }[]>(() => [
  { key: 'pos', label: 'Priority' }, { key: 'label', label: 'Id' }, { key: 'title', label: 'Title' }, { key: 'state', label: 'Status' }, { key: 'team', label: 'Team' },
  tab.value === 'archived' ? { key: 'archived', label: 'Archived' } : { key: 'updated', label: 'Modified' },
]);
function pickSort(key: SortKey) { sort.value = { key, desc: false }; }
// The phone's fold (B054): closed at first; its button counts what is set in it (search, team, a sort other than priority).
const toolsOpen = ref(false);
const toolsSet = computed(() => (search.value.trim() ? 1 : 0) + (teamFilter.value ? 1 : 0) + (sort.value.key !== 'pos' || sort.value.desc ? 1 : 0));

// ---- reorder
async function step(item: BacklogItem, direction: 'up' | 'down') {
  menuFor.value = null;
  const list = await act<BacklogItem[]>('backlog.move', { id: item.id, direction });
  if (list) items.value = list;
}
const dragging = ref<number | null>(null);
const dropAt = ref<number | null>(null);
async function drop(target: BacklogItem) {
  const id = dragging.value;
  dragging.value = null;
  dropAt.value = null;
  if (id === null || id === target.id) return;
  const order = items.value.map((i) => i.id).filter((x) => x !== id);
  const at = order.indexOf(target.id);
  const from = items.value.findIndex((i) => i.id === id);
  const to = items.value.findIndex((i) => i.id === target.id);
  // Dropping on an item puts the dragged one where that item was: before it when moving up, after it when moving down.
  const index = from > to ? at : at + 1;
  const list = await act<BacklogItem[]>('backlog.position', { id, after: order[index - 1] ?? null, before: order[index] ?? null });
  if (list) items.value = list;
}

// ---- add (typed, or from a document: the first # heading is the title, the whole file the spec)
const adding = ref(false);
const draft = ref({ title: '', body: '' });
const picker = ref<HTMLInputElement | null>(null);
async function add() {
  const item = await act<BacklogItem>('backlog.add', { title: draft.value.title.trim(), body: draft.value.body });
  if (!item) return;
  draft.value = { title: '', body: '' };
  adding.value = false;
  await load();
  select(item);
}
async function upload(files: FileList | null) {
  const f = files?.[0];
  if (picker.value) picker.value.value = '';
  if (!f) return;
  if (f.size > 1024 * 1024) { error.value = `${f.name} is larger than 1 MB`; return; }
  const text = await f.text();
  if (text.includes('\u0000')) { error.value = `${f.name} is not a text file`; return; }
  const heading = text.split(/\r?\n/).find((l) => /^#\s+\S/.test(l));
  const title = (heading ? heading.replace(/^#\s+/, '') : f.name.replace(/\.[^.]+$/, '')).trim().slice(0, TitleMax);
  draft.value = { title, body: text };
  adding.value = true;
}

// ---- the open item
const editTitle = ref('');
const editBody = ref('');
const preview = ref(true);
function select(item: BacklogItem) {
  openId.value = item.id;
  editTitle.value = item.title;
  editBody.value = item.body;
  preview.value = true;
  confirmAgain.value = false;
  confirmDelete.value = false;
  dispatchOpen.value = false;
  void loadDetail();
}
watch(open, (now, before) => { if (now && before?.id === now.id && !dirty.value) { editTitle.value = now.title; editBody.value = now.body; } });
const dirty = computed(() => !!open.value && (editTitle.value !== open.value.title || editBody.value !== open.value.body));
const rendered = computed(() => renderMarkdown(editBody.value || '_No spec yet._'));

async function update(params: Record<string, unknown>, label = 'save') {
  if (!open.value) return;
  if (await act<BacklogItem>('backlog.edit', { id: open.value.id, ...params }, label)) { await load(); await loadDetail(); }
}
async function simple(method: string) {
  if (!open.value) return;
  const id = open.value.id;
  if (await act(method, { id })) { openId.value = null; confirmDelete.value = false; await load(); }
}

// ---- dispatch
const target = ref<number | 'new'>(props.teams[0]?.id ?? 'new');
const newTeam = ref({ name: '', cli: '', repo: '' });
const confirmAgain = ref(false);
const confirmDelete = ref(false);
const isAddress = (s: string) => /^(https?|ssh|file):|^[\w.-]+@[\w.-]+:/i.test(s.trim());
// The toolbar's Dispatch opens the dispatch form (where to, then Dispatch); what the item itself allows decides
// whether it can open at all.
const dispatchOpen = ref(false);
const itemBlocked = computed(() => {
  const i = open.value;
  if (!i) return 'Pick an item.';
  if (i.archivedAt) return 'Restore it first.';
  if (i.state !== 'ready') return i.state === 'pending' ? 'Read it, then mark it ready: only a ready item is dispatched.' : 'It is done: move it back to pending to work on it again.';
  return null;
});
watch(itemBlocked, (b) => { if (b) dispatchOpen.value = false; });
const dispatchBlocked = computed(() => {
  if (itemBlocked.value) return itemBlocked.value;
  if (target.value === 'new' && !newTeam.value.name.trim()) return 'Name the new team.';
  if (target.value !== 'new' && !props.teams.some((t) => t.id === target.value)) return 'Pick a team.';
  return null;
});
async function dispatch() {
  const i = open.value;
  if (!i || dispatchBlocked.value) return;
  if (i.inFlight && !confirmAgain.value) { confirmAgain.value = true; return; }
  const r = target.value === 'new'
    ? await act<BacklogItem>('backlog.dispatchNew', {
        id: i.id, name: newTeam.value.name.trim(), ...(newTeam.value.cli ? { managerCli: newTeam.value.cli } : {}),
        ...(newTeam.value.repo.trim() ? { [isAddress(newTeam.value.repo) ? 'repoUrl' : 'repoPath']: newTeam.value.repo.trim() } : {}),
      }, 'dispatch')
    : await act<BacklogItem>('backlog.dispatch', { id: i.id, teamId: target.value, again: i.inFlight }, 'dispatch');
  confirmAgain.value = false;
  if (r) { dispatchOpen.value = false; newTeam.value = { name: '', cli: '', repo: '' }; await load(); await loadDetail(); }
}
watch(() => props.teams, (ts) => { if (target.value !== 'new' && !ts.some((t) => t.id === target.value)) target.value = ts[0]?.id ?? 'new'; });

// The teams this item went to, newest dispatch first, once each (a deleted team has no links).
const teamLinks = computed(() => {
  if (detail.value?.id !== open.value?.id) return [];
  const seen = new Set<number>();
  const out: { teamId: number; team: string; workflow: number }[] = [];
  for (const d of detail.value?.dispatches ?? []) {
    if (d.teamId === null || d.teamGone || seen.has(d.teamId)) continue;
    seen.add(d.teamId); out.push({ teamId: d.teamId, team: d.team, workflow: d.workflow });
  }
  return out;
});
</script>

<template>
  <section class="backlog-place fill" :class="{ phone }" data-test="backlog-view">
    <div class="wrap fill">
      <!-- A phone (B054): one compact row (the tabs, the search-and-sort fold and + Add item), scrolling away with the
           list; the search, team filter, sort and Upload document are in the fold, closed at first. -->
      <template v-if="phone">
        <header class="phead" data-test="backlog-phone-head">
          <nav class="tabs">
            <button :class="{ on: tab === 'live' }" data-test="backlog-tab-live" @click="switchTab('live')">Backlog</button>
            <button :class="{ on: tab === 'archived' }" data-test="backlog-tab-archived" @click="switchTab('archived')">Archived</button>
          </nav>
          <span class="grow" />
          <button type="button" class="hbtn" :class="{ on: toolsOpen || !!toolsSet }" title="Search, filter and sort" aria-label="Search, filter and sort"
            :aria-expanded="toolsOpen" data-test="backlog-tools-toggle" @click="toolsOpen = !toolsOpen">
            <Icon name="search" :size="18" /><span v-if="toolsSet" class="badge" data-test="backlog-tools-count">{{ toolsSet }}</span>
          </button>
          <template v-if="tab === 'live'">
            <button type="button" class="hbtn primary" v-change title="Add an item" aria-label="Add item" data-test="backlog-add" @click="adding = !adding"><Icon name="plus" :size="18" /></button>
            <input ref="picker" type="file" accept=".md,.markdown,.txt,text/*" hidden data-test="backlog-upload-input" @change="upload(($event.target as HTMLInputElement).files)" />
          </template>
        </header>
        <div v-if="toolsOpen" class="phone-tools" data-test="backlog-tools">
          <input v-model="search" class="search" placeholder="Search" data-test="backlog-search" />
          <select v-model="teamFilter" data-test="backlog-team-filter" title="Dispatched to">
            <option value="">All teams</option>
            <option v-for="t in teamOptions" :key="t" :value="t">{{ t }}</option>
          </select>
          <div class="phone-sort" data-test="backlog-sort">
            <label>Sort by
              <select :value="sort.key" data-test="backlog-sort-key" @change="pickSort(($event.target as HTMLSelectElement).value as SortKey)">
                <option v-for="s in sortKeys" :key="s.key" :value="s.key">{{ s.label }}</option>
              </select>
            </label>
            <button type="button" class="mini" :aria-label="sort.desc ? 'Descending' : 'Ascending'" data-test="backlog-sort-dir" @click="sort.desc = !sort.desc">{{ sort.desc ? '▾' : '▴' }}</button>
          </div>
          <button v-if="tab === 'live'" v-change data-test="backlog-upload" title="A Markdown or text file: its first # heading is the title" @click="picker?.click()">Upload document</button>
          <p class="muted small intro" data-test="backlog-intro">Most important first. Mark an item ready once you have read it, then dispatch it to a team.</p>
        </div>
      </template>
      <header v-else class="head">
        <h2>Backlog</h2>
        <nav class="tabs">
          <button :class="{ on: tab === 'live' }" data-test="backlog-tab-live" @click="switchTab('live')">Backlog</button>
          <button :class="{ on: tab === 'archived' }" data-test="backlog-tab-archived" @click="switchTab('archived')">Archived</button>
        </nav>
        <span class="grow" />
        <input v-model="search" class="search" placeholder="Search" data-test="backlog-search" />
        <select v-model="teamFilter" data-test="backlog-team-filter" title="Dispatched to">
          <option value="">All teams</option>
          <option v-for="t in teamOptions" :key="t" :value="t">{{ t }}</option>
        </select>
        <template v-if="tab === 'live'">
          <button v-change data-test="backlog-upload" title="A Markdown or text file: its first # heading is the title" @click="picker?.click()">Upload document</button>
          <input ref="picker" type="file" accept=".md,.markdown,.txt,text/*" hidden data-test="backlog-upload-input" @change="upload(($event.target as HTMLInputElement).files)" />
          <button class="primary" v-change data-test="backlog-add" @click="adding = !adding">+ Add item</button>
        </template>
      </header>
      <p v-if="!phone && tab === 'live'" class="muted intro">Mark an item ready once you have read it, then dispatch it to a team.</p>
      <p v-if="error" class="error" data-test="backlog-error">{{ error }}</p>

      <div v-if="adding" class="itemform" data-test="backlog-new">
        <input v-model="draft.title" placeholder="Title" :maxlength="TitleMax" data-test="backlog-new-title" />
        <textarea v-model="draft.body" rows="8" placeholder="The spec, in Markdown: what, why, and how you will know it is done" data-test="backlog-new-body"></textarea>
        <div class="buttons"><button @click="adding = false; draft = { title: '', body: '' }">Cancel</button><button class="primary" :disabled="busy || !draft.title.trim()" v-change data-test="backlog-new-save" :aria-busy="doing === 'backlog.add'" @click="add"><BusyLabel :busy="doing === 'backlog.add'" text="Adding…">Add</BusyLabel></button></div>
      </div>

      <div class="split" :class="{ withItem: !!open }" data-test="backlog-split">
        <div v-show="!(phone && open)" class="listwrap" data-test="backlog-listwrap">
        <table class="list">
          <thead>
            <tr>
              <th class="sortable" @click="sortBy('pos')">#{{ arrow('pos') }}</th>
              <th class="sortable" @click="sortBy('label')">Id{{ arrow('label') }}</th>
              <th class="sortable" @click="sortBy('title')">Title{{ arrow('title') }}</th>
              <th class="sortable" @click="sortBy('state')">Status{{ arrow('state') }}</th>
              <th class="sortable" @click="sortBy('team')">Team{{ arrow('team') }}</th>
              <th class="sortable" @click="sortBy(tab === 'archived' ? 'archived' : 'updated')">{{ tab === 'archived' ? 'Archived' : 'Modified' }}{{ arrow(tab === 'archived' ? 'archived' : 'updated') }}</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="i in shown" :key="i.id" :class="{ sel: openId === i.id, drop: dropAt === i.id }" :aria-selected="openId === i.id" data-test="backlog-row"
              :draggable="canDrag && !phone" @dragstart="dragging = i.id" @dragover.prevent="canDrag && (dropAt = i.id)" @dragleave="dropAt = null" @drop.prevent="drop(i)"
              @click="select(i)">
              <td class="muted pos">{{ position.get(i.id) }}</td>
              <td class="mono lbl">{{ i.label }}</td>
              <td class="ttl">{{ i.title }}</td>
              <td class="st"><span class="state" :class="shownState(i)" data-test="backlog-status" :title="shownState(i) === 'flight' ? 'Its cards are being worked on' : shownState(i) === 'dispatched' ? 'Sent to ' + (i.dispatchedTeam ?? 'a team') + '; nothing is being worked right now' : ''">{{ ShownLabel[shownState(i)] }}</span></td>
              <td class="muted tm">{{ i.dispatchedTeam ?? '—' }}<span v-if="i.dispatchCount > 1" class="muted small"> ×{{ i.dispatchCount }}</span></td>
              <td class="muted small when">{{ ago(tab === 'archived' ? (i.archivedAt ?? i.updatedAt) : i.updatedAt) }}</td>
              <td class="acts" @click.stop>
                <span v-if="phone && tab === 'live' && canDrag" class="row-menu">
                  <button class="mini" title="Actions" aria-label="Actions" aria-haspopup="menu" :aria-expanded="menuFor === i.id" data-test="backlog-row-menu"
                    @click="menuFor = menuFor === i.id ? null : i.id">⋯</button>
                  <span v-if="menuFor === i.id" class="menu" role="menu" data-test="backlog-row-menu-list">
                    <button role="menuitem" data-test="backlog-row-up" :disabled="busy || position.get(i.id) === 1" @click="step(i, 'up')">↑ Higher priority</button>
                    <button role="menuitem" data-test="backlog-row-down" :disabled="busy || position.get(i.id) === items.length" @click="step(i, 'down')">↓ Lower priority</button>
                  </span>
                </span>
                <template v-else-if="tab === 'live' && canDrag">
                  <button class="mini" title="Higher priority" :disabled="busy || position.get(i.id) === 1" @click="step(i, 'up')">↑</button>
                  <button class="mini" title="Lower priority" :disabled="busy || position.get(i.id) === items.length" @click="step(i, 'down')">↓</button>
                </template>
              </td>
            </tr>
            <tr v-if="!loaded && !error"><td colspan="7"><LoadingNote :text="tab === 'live' ? 'Loading the backlog…' : 'Loading archived items…'" /></td></tr>
            <tr v-else-if="!shown.length && loaded"><td colspan="7" class="muted empty">{{ items.length ? 'Nothing matches.' : tab === 'live' ? 'Nothing in the backlog. Add the work you want done, then mark it ready and dispatch it to a team.' : 'Nothing archived.' }}</td></tr>
          </tbody>
        </table>
        </div>

        <section v-if="open" class="item" data-test="backlog-item">
          <div class="ihead">
            <span class="mono">{{ open.label }}</span><span class="state" :class="shownState(open)">{{ ShownLabel[shownState(open)] }}</span>
            <span class="muted small">added {{ ago(open.createdAt) }}</span><span class="grow" />
            <button v-if="phone" class="tool back" title="Back to the backlog" data-test="backlog-item-back" @click="openId = null">← Backlog</button>
            <button v-else class="tool" title="Close" aria-label="Close" @click="openId = null"><Icon name="close" :size="16" /></button>
          </div>

          <!-- The item's toolbar: how the spec is shown (Read / Edit) on the left; what to do with the item on the right,
               in the order it happens (mark ready, dispatch, mark implemented), then Archive. -->
          <div class="toolbar" role="toolbar" aria-label="Backlog item" data-test="backlog-toolbar">
            <div class="seg" role="group" aria-label="Spec">
              <button class="tool" :class="{ on: preview }" title="Read the spec" aria-label="Read" :aria-pressed="preview" data-test="backlog-item-readmode" @click="preview = true"><Icon name="eye" :size="16" /><span>Read</span></button>
              <button class="tool" :class="{ on: !preview }" :title="open.archivedAt ? 'Restore it to edit it' : 'Edit the spec (Markdown)'" aria-label="Edit" :aria-pressed="!preview" :disabled="!!open.archivedAt" data-test="backlog-item-edit" @click="preview = false"><Icon name="edit" :size="16" /><span>Edit</span></button>
            </div>
            <span class="grow" />
            <template v-if="!open.archivedAt">
              <button v-if="open.state === 'pending'" class="tool go" title="I've read it: mark ready (only a ready item is dispatched)" aria-label="Mark ready" data-test="backlog-ready" :disabled="busy" @click="update({ state: 'ready' }, 'ready')">
                <BusyLabel :busy="doing === 'ready'"><Icon name="check" :size="16" /></BusyLabel>
              </button>
              <button v-else class="tool" :title="open.state === 'ready' ? 'Back to pending' : 'Reopen as pending'" :aria-label="open.state === 'ready' ? 'Back to pending' : 'Reopen as pending'" data-test="backlog-pending" :disabled="busy" @click="update({ state: 'pending' }, 'pending')">
                <BusyLabel :busy="doing === 'pending'"><Icon name="undo" :size="16" /></BusyLabel>
              </button>
              <button class="tool" :class="{ on: dispatchOpen, go: open.state === 'ready' && !open.dispatchedAt }" :title="itemBlocked ?? (open.inFlight ? 'Dispatch again…' : 'Dispatch to a team…')" aria-label="Dispatch" :aria-expanded="dispatchOpen"
                data-test="backlog-dispatch-open" :disabled="busy || !!itemBlocked" @click="dispatchOpen = !dispatchOpen; confirmAgain = false">
                <Icon name="dispatch" :size="16" />
              </button>
              <button v-if="open.state === 'declared' || open.state === 'ready'" class="tool" title="Mark implemented" aria-label="Mark implemented" data-test="backlog-implemented" :disabled="busy" @click="update({ state: 'implemented' }, 'implemented')">
                <BusyLabel :busy="doing === 'implemented'"><Icon name="checkAll" :size="16" /></BusyLabel>
              </button>
              <span class="sep" />
              <button class="tool" title="Archive" aria-label="Archive" data-test="backlog-archive" :disabled="busy" @click="simple('backlog.archive')">
                <BusyLabel :busy="doing === 'backlog.archive'"><Icon name="archive" :size="16" /></BusyLabel>
              </button>
            </template>
            <template v-else>
              <button class="tool" title="Restore to the backlog" aria-label="Restore" data-test="backlog-restore" :disabled="busy" @click="simple('backlog.restore')">
                <BusyLabel :busy="doing === 'backlog.restore'"><Icon name="unarchive" :size="16" /></BusyLabel>
              </button>
              <button class="tool bad" :class="{ on: confirmDelete }" title="Delete for good…" aria-label="Delete" data-test="backlog-delete" :disabled="busy" @click="confirmDelete = !confirmDelete"><Icon name="trash" :size="16" /></button>
            </template>
          </div>
          <p v-if="open.state === 'pending' && !open.archivedAt" class="muted small hint" data-test="backlog-dispatch-why">Read it, then mark it ready (✓): only a ready item is dispatched.</p>
          <div v-if="confirmDelete && open.archivedAt" class="buttons confirm">
            <span class="small">Delete {{ open.label }} for good?</span>
            <button :disabled="busy" @click="confirmDelete = false">Keep</button>
            <button class="danger" data-test="backlog-delete-yes" :disabled="busy" @click="simple('backlog.delete')"><BusyLabel :busy="doing === 'backlog.delete'" text="Deleting…">Delete</BusyLabel></button>
          </div>

          <div v-if="dispatchOpen && !open.archivedAt" class="dispatch" data-test="backlog-dispatch-panel">
            <label>Dispatch to
              <select v-model="target" data-test="backlog-team">
                <option v-if="teamsLoading && !teams.length" disabled value="">Loading teams…</option>
                <option v-for="t in teams" :key="t.id" :value="t.id">{{ t.name }}</option>
                <option value="new">A new team…</option>
              </select>
            </label>
            <template v-if="target === 'new'">
              <label>New team's name<input v-model="newTeam.name" data-test="backlog-new-team" placeholder="e.g. Billing" /></label>
              <label>Its repository (address or folder, optional)<input v-model="newTeam.repo" class="mono" placeholder="https://git.example.com/owner/repo.git" /></label>
              <label>Manager's agent
                <select v-model="newTeam.cli"><option value="">Default (Settings &gt; Teams)</option><option value="claude">Claude</option><option value="codex">Codex</option><option value="grok">Grok</option><option value="copilot">Copilot</option><option value="antigravity">Antigravity</option></select>
              </label>
            </template>
            <p v-if="confirmAgain" class="warn small" data-test="backlog-again-confirm">
              {{ open.label }} is still in flight with {{ open.dispatchedTeam }}. Dispatch it again anyway?
            </p>
            <div class="buttons">
              <button :disabled="busy" data-test="backlog-dispatch-cancel" @click="confirmAgain ? (confirmAgain = false) : (dispatchOpen = false)">Cancel</button>
              <button class="primary" data-test="backlog-dispatch" :disabled="busy || !!dispatchBlocked" :title="dispatchBlocked ?? ''" :aria-busy="doing === 'dispatch'" @click="dispatch">
                <BusyLabel :busy="doing === 'dispatch'" text="Dispatching…">{{ confirmAgain ? 'Dispatch again' : open.inFlight ? 'Dispatch again…' : 'Dispatch' }}</BusyLabel>
              </button>
            </div>
            <p v-if="dispatchBlocked" class="muted small">{{ dispatchBlocked }}</p>
          </div>

          <input v-model="editTitle" :maxlength="TitleMax" data-test="backlog-item-title" :disabled="!!open.archivedAt" />
          <div v-if="preview" class="md ibody" data-test="backlog-item-read" v-html="rendered"></div>
          <textarea v-else v-model="editBody" rows="12" class="ibody" data-test="backlog-item-body"></textarea>
          <div v-if="dirty" class="buttons">
            <button @click="editTitle = open.title; editBody = open.body">Discard</button>
            <button class="primary" :disabled="busy" data-test="backlog-item-save" :aria-busy="doing === 'save'" @click="update({ title: editTitle, body: editBody })"><BusyLabel :busy="doing === 'save'" text="Saving…">Save</BusyLabel></button>
          </div>

          <div v-for="t in teamLinks" :key="t.teamId" class="links" data-test="backlog-team-links">
            <span class="muted small">{{ t.team }}</span>
            <button class="link" @click="emit('openTeam', t.teamId)">Open team</button>
            <button class="link" @click="emit('board', t.teamId)">Its board</button>
            <button class="link" data-test="backlog-team-workflow" :title="`${t.team}'s latest workflow for ${open.label}, on the Workflows page`"
              @click="openWorkflowsPage({ teamId: t.teamId, workflow: t.workflow })">workflow #{{ t.workflow }}</button>
          </div>
        </section>
      </div>
    </div>
  </section>
</template>

<style scoped>
.backlog-place { height: 100%; overflow: auto; padding: 4px 24px 24px; }
.wrap { max-width: 1280px; margin: 12px auto; }
/* Filling the page (as Settings > Skills): the page is a column; the wrap takes the height below the toolbar rows; the
   split's one row is as tall as what is left, and the table and the detail pane scroll on their own (min-height: 0
   all the way down). The wrap keeps a floor so a very short window scrolls the page instead. */
.backlog-place.fill { display: flex; flex-direction: column; }
.wrap.fill { flex: 1 1 0; min-height: 360px; width: 100%; box-sizing: border-box; margin: 12px auto 0; display: flex; flex-direction: column; }
.wrap.fill > * { flex: none; }
.wrap.fill > .split { flex: 1 1 0; min-height: 0; grid-template-rows: minmax(0, 1fr); align-items: stretch; }
.listwrap { min-height: 0; min-width: 0; overflow: auto; }
.head { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; }
.head h2 { margin: 0 12px 0 0; font-size: 22px; }
.tabs { display: flex; gap: 2px; }
.tabs button { border: 0; border-bottom: 2px solid transparent; border-radius: 0; background: transparent; padding: 6px 10px; font-weight: 600; color: var(--ink-soft); }
.tabs button.on { color: var(--accent); border-bottom-color: var(--accent); }
.search { font: inherit; font-size: 13px; padding: 5px 8px; border: 1px solid var(--line); border-radius: 6px; width: 180px; }
.head select, .dispatch select, .dispatch input { font: inherit; font-size: 13px; padding: 5px 7px; border: 1px solid var(--line); border-radius: 6px; }
.intro { margin: 6px 0 12px; font-size: 13px; }
.grow { flex: 1; }
.primary { background: var(--accent); color: var(--on-accent); border-color: var(--accent); }
.danger { background: var(--bad); color: var(--on-accent); border-color: var(--bad); }
.split { display: grid; grid-template-columns: 1fr; gap: 14px; align-items: start; }
.split.withItem { grid-template-columns: minmax(0, 1fr) minmax(360px, 440px); }
.list { width: 100%; border-collapse: collapse; font-size: 13px; }
.list th { position: sticky; top: 0; z-index: 1; background: var(--surface); text-align: left; font-size: 11.5px; color: var(--muted); font-weight: 600; padding: 4px 6px; border-bottom: 1px solid var(--line); white-space: nowrap; }
.list th.sortable { cursor: pointer; user-select: none; }
.list th.sortable:hover { color: var(--ink); }
.list td { padding: 7px 6px; border-bottom: 1px solid var(--line-soft); vertical-align: top; }
.list tr[data-test=backlog-row] { cursor: pointer; }
.list tr[draggable=true] { cursor: grab; }
/* The chosen item: an accent bar on its left edge, a tinted row and an outline, so it stands out from the list. */
.list tr.sel td { background: color-mix(in srgb, var(--accent) 9%, var(--surface)); box-shadow: inset 0 1px 0 color-mix(in srgb, var(--accent) 45%, transparent), inset 0 -1px 0 color-mix(in srgb, var(--accent) 45%, transparent); }
.list tr.sel td:first-child { box-shadow: inset 4px 0 0 var(--accent), inset 0 1px 0 color-mix(in srgb, var(--accent) 45%, transparent), inset 0 -1px 0 color-mix(in srgb, var(--accent) 45%, transparent); }
.list tr.sel td:last-child { box-shadow: inset -1px 0 0 color-mix(in srgb, var(--accent) 45%, transparent), inset 0 1px 0 color-mix(in srgb, var(--accent) 45%, transparent), inset 0 -1px 0 color-mix(in srgb, var(--accent) 45%, transparent); }
.list tr.drop td { border-top: 2px solid var(--accent); }
.mono { font-family: ui-monospace, Consolas, monospace; font-size: 12px; }
.muted { color: var(--muted); }
.small { font-size: 12px; }
.empty { padding: 18px 6px; }
.acts { white-space: nowrap; }
.mini { padding: 0 6px; font-size: 12px; }
.mini.on { border-color: var(--accent); color: var(--accent); }
.state { font-size: 11px; padding: 1px 7px; border-radius: 10px; background: var(--pill-neutral-bg); color: var(--pill-neutral-ink); white-space: nowrap; }
.state.ready { background: var(--pill-info-bg); color: var(--pill-info-ink); }
.state.dispatched { background: var(--pill-violet-bg); color: var(--pill-violet-ink); }
.state.flight { background: var(--pill-blue-bg); color: var(--pill-blue-ink); box-shadow: inset 0 0 0 1px var(--pill-blue-line); }
.state.declared { background: var(--pill-yellow-bg); color: var(--pill-yellow-ink); }
.state.implemented { background: var(--pill-green-bg); color: var(--pill-green-ink); }
.item, .itemform { display: flex; flex-direction: column; gap: 8px; border: 1px solid var(--line); border-radius: 10px; padding: 10px 12px; background: var(--surface); }
.item { min-height: 0; overflow: auto; }
.item > * { flex: none; }
/* The spec (Read/Edit) takes all the free height; the rest keeps its natural height below it. A pane too short for
   the fixed parts plus 160px of spec scrolls as a whole. */
.item > .ibody { flex: 1 1 0; min-height: 160px; }
.item > textarea.ibody { resize: none; box-sizing: border-box; }
.itemform { margin-bottom: 12px; }
.ihead { display: flex; align-items: center; gap: 8px; }
.item input, .item textarea, .itemform input, .itemform textarea { font: inherit; font-size: 13px; padding: 5px 7px; border: 1px solid var(--line); border-radius: 6px; }
.item textarea, .itemform textarea { font-family: ui-monospace, Consolas, monospace; font-size: 12.5px; }
/* The item's toolbar: icon buttons as in a window's toolbar (a tooltip names each); Read / Edit is a segmented pair. */
.toolbar { display: flex; align-items: center; gap: 4px; padding: 4px 0; border-top: 1px solid var(--line-soft); border-bottom: 1px solid var(--line-soft); }
.seg { display: flex; border: 1px solid var(--line); border-radius: 7px; overflow: hidden; }
.seg .tool { border: 0; border-radius: 0; width: auto; padding: 0 9px; gap: 5px; font-size: 12.5px; }
.seg .tool + .tool { border-left: 1px solid var(--line); }
.tool { display: inline-flex; align-items: center; justify-content: center; width: 30px; height: 28px; padding: 0; border: 1px solid transparent; border-radius: 6px; background: transparent; color: var(--ink-soft); cursor: pointer; }
.tool:hover:not(:disabled) { background: color-mix(in srgb, var(--ink) 7%, transparent); color: var(--ink); }
.tool:disabled { opacity: 0.4; cursor: default; }
.tool.on { background: color-mix(in srgb, var(--accent) 12%, transparent); color: var(--accent); }
.tool.go:not(:disabled) { color: var(--accent); }
.tool.bad:not(:disabled) { color: var(--bad); }
.ihead .tool { width: 26px; height: 24px; }
.sep { width: 1px; align-self: stretch; margin: 2px 4px; background: var(--line); }
.hint { margin: 0; }
.confirm { justify-content: flex-start; }
.md { border: 1px solid var(--line-soft); border-radius: 6px; padding: 6px 10px; font-size: 13px; overflow: auto; }
.buttons { display: flex; gap: 6px; justify-content: flex-end; flex-wrap: wrap; align-items: center; }
.dispatch { display: flex; flex-direction: column; gap: 6px; border-top: 1px solid var(--line-soft); padding-top: 8px; }
.dispatch label { display: flex; flex-direction: column; gap: 3px; font-size: 12px; color: var(--ink-soft); }
.links { display: flex; gap: 10px; align-items: baseline; flex: none; }
.link { border: 0; background: transparent; color: var(--accent); padding: 0; font-size: 12px; }
.warn { color: var(--warn); margin: 0; }
.error { color: var(--bad); }
.row-menu { position: relative; display: inline-block; }
.menu { position: absolute; right: 0; top: 100%; z-index: 3; display: flex; flex-direction: column; min-width: 190px; padding: 4px;
  background: var(--surface); border: 1px solid var(--line); border-radius: 8px; box-shadow: 0 6px 18px var(--shadow); }
.menu button { text-align: left; border: 0; background: transparent; padding: 0 10px; min-height: 44px; font-size: 13px; }
.menu button:hover:not(:disabled) { background: var(--hover); }
.phone-sort { display: flex; align-items: center; gap: 6px; margin-bottom: 6px; font-size: 13px; }
.phone-sort label { display: flex; align-items: center; gap: 6px; flex: 1; }
.phone-sort select { flex: 1; font: inherit; font-size: 13px; padding: 5px 7px; border: 1px solid var(--line); border-radius: 6px; }
/* A phone (B037): one column; each row wraps (number, id, status and its ⋯ menu on top, the title under them, then
   the team and when); an opened item takes the page; tap targets 44 px. */
@media (max-width: 759.98px) {
  .backlog-place { padding: 4px 10px 16px; }
  /* One scroller (B054): the page; the header row, the fold, the list and an opened item scroll with it, nothing pinned. */
  .backlog-place.fill { display: block; }
  .wrap.fill { display: block; min-height: 0; margin: 4px auto 0; }
  .wrap.fill > .split { grid-template-rows: auto; }
  .listwrap, .item { overflow: visible; }
  .item > .ibody { flex: none; }
  .md { overflow-y: visible; }
  .phead { display: flex; align-items: center; gap: 6px; flex-wrap: nowrap; height: 44px; margin: 0 0 6px; }
  .phead .tabs { flex: 0 1 auto; min-width: 0; }
  .hbtn { position: relative; flex: none; display: inline-flex; align-items: center; justify-content: center; width: 44px; height: 44px; padding: 0; box-sizing: border-box; }
  .hbtn.on:not(.primary) { border-color: var(--accent); color: var(--accent); }
  .badge { position: absolute; top: 3px; right: 3px; min-width: 16px; height: 16px; padding: 0 4px; box-sizing: border-box; border-radius: 8px; background: var(--accent); color: var(--on-accent); font-size: 10.5px; line-height: 16px; }
  .phone-tools { display: flex; flex-wrap: wrap; align-items: center; gap: 6px; margin: 0 0 8px; }
  .phone-tools .search, .phone-tools .phone-sort { flex: 1 1 100%; margin: 0; }
  .phone-tools > select { flex: 1 1 0; min-width: 0; font: inherit; font-size: 13px; padding: 5px 7px; border: 1px solid var(--line); border-radius: 6px; }
  .phone-tools .intro { flex: 1 1 100%; margin: 0; }
  .search { width: auto; min-width: 0; }
  .phone-tools button, .phone-tools select, .phone-tools input, .phone-sort select, .phone-sort button, .tabs button { min-height: 44px; box-sizing: border-box; }
  .split.withItem { grid-template-columns: minmax(0, 1fr); }
  .list thead { display: none; }
  .list, .list tbody { display: block; }
  .list tr[data-test=backlog-row] { display: flex; flex-wrap: wrap; align-items: center; gap: 2px 8px; padding: 8px 6px; border-bottom: 1px solid var(--line-soft); }
  .list tr[data-test=backlog-row] td { display: block; padding: 0; border: 0; }
  .list tr.sel td, .list tr.sel td:first-child, .list tr.sel td:last-child { box-shadow: none; background: transparent; }
  .list tr.sel { background: color-mix(in srgb, var(--accent) 9%, var(--surface)); box-shadow: inset 4px 0 0 var(--accent); }
  .list td.st { flex: 1; }
  .list td.ttl { order: 5; flex: 1 1 100%; font-weight: 600; overflow-wrap: anywhere; }
  .list td.tm, .list td.when { order: 6; }
  .list td.acts { order: 4; }
  .mini { min-width: 44px; min-height: 44px; font-size: 16px; }
  .tool { width: 44px; height: 44px; }
  .ihead .tool { width: 44px; height: 44px; }
  .ihead .tool.back { width: auto; padding: 0 10px; font-size: 13px; color: var(--accent); }
  .seg .tool { height: 44px; }
  .toolbar { flex-wrap: wrap; }
  .item input, .dispatch select, .dispatch input, .buttons button { min-height: 44px; box-sizing: border-box; }
  .link { min-height: 44px; }
}
</style>
