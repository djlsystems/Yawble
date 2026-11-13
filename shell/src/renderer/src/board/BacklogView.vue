<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import type { BacklogDispatch, BacklogItem, BacklogItemDetail, YawbleApi, TeamSummary } from '../../../shared/api';
import { errorText, resolveApi } from '../api';
import { renderMarkdown } from '../markdown';
import { ago } from './cards';
import LoadingNote from '../components/LoadingNote.vue';
import BusyLabel from '../components/BusyLabel.vue';

/**
 * The Backlog: the person's list of work not yet given to a team, in priority order. An item
 * is a SPEC. It is pending until the person has read it and marks it ready; a ready item is dispatched to a team's
 * Manager (or to a new team made for it), which cuts it into cards. When that work is declared complete the item reads
 * "declared"; only the person marks it implemented. Every dispatch is kept with its workflow, cards and usage.
 */
// teamsLoading: the app's first overview (the teams to dispatch to) has not arrived yet.
const props = defineProps<{ teams: TeamSummary[]; teamsLoading?: boolean; api?: YawbleApi; pollMs?: number }>();
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

// ---- reorder
async function step(item: BacklogItem, direction: 'up' | 'down') {
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
const newTeam = ref({ name: '', cli: 'claude', repo: '' });
const confirmAgain = ref(false);
const confirmDelete = ref(false);
const isAddress = (s: string) => /^(https?|ssh|file):|^[\w.-]+@[\w.-]+:/i.test(s.trim());
const dispatchBlocked = computed(() => {
  const i = open.value;
  if (!i) return 'Pick an item.';
  if (i.archivedAt) return 'Restore it first.';
  if (i.state !== 'ready') return i.state === 'pending' ? 'Read it, then mark it ready: only a ready item is dispatched.' : 'It is done: move it back to pending to work on it again.';
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
        id: i.id, name: newTeam.value.name.trim(), managerCli: newTeam.value.cli,
        ...(newTeam.value.repo.trim() ? { [isAddress(newTeam.value.repo) ? 'repoUrl' : 'repoPath']: newTeam.value.repo.trim() } : {}),
      }, 'dispatch')
    : await act<BacklogItem>('backlog.dispatch', { id: i.id, teamId: target.value, again: i.inFlight }, 'dispatch');
  confirmAgain.value = false;
  if (r) { newTeam.value = { name: '', cli: 'claude', repo: '' }; await load(); await loadDetail(); }
}
watch(() => props.teams, (ts) => { if (target.value !== 'new' && !ts.some((t) => t.id === target.value)) target.value = ts[0]?.id ?? 'new'; });

const tokens = (n: number) => (n >= 1_000_000 ? `${(n / 1_000_000).toFixed(1)}M` : n >= 1000 ? `${Math.round(n / 1000)}k` : `${n}`);
function elapsed(d: BacklogDispatch): string | null {
  if (!d.firstStart) return null;
  const end = d.state === 'open' || !d.lastEnd ? Date.now() : Date.parse(d.lastEnd);
  const min = Math.max(0, Math.round((end - Date.parse(d.firstStart)) / 60000));
  return min < 60 ? `${min} min` : `${Math.floor(min / 60)} h ${min % 60} min`;
}
const stateText = (d: BacklogDispatch) => d.teamGone ? 'team deleted' : d.state === 'open' ? (d.busy?.length ? `working: ${d.busy.join(', ')}` : 'open, idle') : d.state ?? 'unknown';
</script>

<template>
  <section class="backlog-place fill" data-test="backlog-view">
    <div class="wrap fill">
      <header class="head">
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
          <button data-test="backlog-upload" title="A Markdown or text file: its first # heading is the title" @click="picker?.click()">Upload document</button>
          <input ref="picker" type="file" accept=".md,.markdown,.txt,text/*" hidden data-test="backlog-upload-input" @change="upload(($event.target as HTMLInputElement).files)" />
          <button class="primary" data-test="backlog-add" @click="adding = !adding">+ Add item</button>
        </template>
      </header>
      <p class="muted intro">Work you want done, most important first. Mark an item ready once you have read it, then dispatch it to a team: its Manager cuts it into cards on the Board.</p>
      <p v-if="error" class="error" data-test="backlog-error">{{ error }}</p>

      <div v-if="adding" class="itemform" data-test="backlog-new">
        <input v-model="draft.title" placeholder="Title" :maxlength="TitleMax" data-test="backlog-new-title" />
        <textarea v-model="draft.body" rows="8" placeholder="The spec, in Markdown: what, why, and how you will know it is done" data-test="backlog-new-body"></textarea>
        <div class="buttons"><button @click="adding = false; draft = { title: '', body: '' }">Cancel</button><button class="primary" :disabled="busy || !draft.title.trim()" data-test="backlog-new-save" :aria-busy="doing === 'backlog.add'" @click="add"><BusyLabel :busy="doing === 'backlog.add'" text="Adding…">Add</BusyLabel></button></div>
      </div>

      <div class="split" :class="{ withItem: !!open }" data-test="backlog-split">
        <div class="listwrap" data-test="backlog-listwrap">
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
              :draggable="canDrag" @dragstart="dragging = i.id" @dragover.prevent="canDrag && (dropAt = i.id)" @dragleave="dropAt = null" @drop.prevent="drop(i)"
              @click="select(i)">
              <td class="muted">{{ position.get(i.id) }}</td>
              <td class="mono">{{ i.label }}</td>
              <td>{{ i.title }}</td>
              <td><span class="state" :class="shownState(i)" data-test="backlog-status" :title="shownState(i) === 'flight' ? 'Its cards are being worked on' : shownState(i) === 'dispatched' ? 'Sent to ' + (i.dispatchedTeam ?? 'a team') + '; nothing is being worked right now' : ''">{{ ShownLabel[shownState(i)] }}</span></td>
              <td class="muted">{{ i.dispatchedTeam ?? '—' }}<span v-if="i.dispatchCount > 1" class="muted small"> ×{{ i.dispatchCount }}</span></td>
              <td class="muted small">{{ ago(tab === 'archived' ? (i.archivedAt ?? i.updatedAt) : i.updatedAt) }}</td>
              <td class="acts" @click.stop>
                <template v-if="tab === 'live' && canDrag">
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
            <button class="mini" title="Close" @click="openId = null">✕</button>
          </div>
          <input v-model="editTitle" :maxlength="TitleMax" data-test="backlog-item-title" :disabled="!!open.archivedAt" />
          <div class="specbar">
            <button class="mini" :class="{ on: preview }" @click="preview = true">Read</button>
            <button class="mini" :class="{ on: !preview }" :disabled="!!open.archivedAt" data-test="backlog-item-edit" @click="preview = false">Edit</button>
          </div>
          <div v-if="preview" class="md ibody" data-test="backlog-item-read" v-html="rendered"></div>
          <textarea v-else v-model="editBody" rows="12" class="ibody" data-test="backlog-item-body"></textarea>
          <div v-if="dirty" class="buttons">
            <button @click="editTitle = open.title; editBody = open.body">Discard</button>
            <button class="primary" :disabled="busy" data-test="backlog-item-save" :aria-busy="doing === 'save'" @click="update({ title: editTitle, body: editBody })"><BusyLabel :busy="doing === 'save'" text="Saving…">Save</BusyLabel></button>
          </div>

          <div v-if="!open.archivedAt" class="flow">
            <button v-if="open.state === 'pending'" class="primary" data-test="backlog-ready" :disabled="busy" @click="update({ state: 'ready' }, 'ready')"><BusyLabel :busy="doing === 'ready'" text="Saving…">I've read it: mark ready</BusyLabel></button>
            <button v-if="open.state !== 'pending'" data-test="backlog-pending" :disabled="busy" @click="update({ state: 'pending' }, 'pending')"><BusyLabel :busy="doing === 'pending'" text="Saving…">{{ open.state === 'ready' ? 'Back to pending' : 'Reopen as pending' }}</BusyLabel></button>
            <button v-if="open.state === 'declared' || open.state === 'ready'" data-test="backlog-implemented" :disabled="busy" @click="update({ state: 'implemented' }, 'implemented')"><BusyLabel :busy="doing === 'implemented'" text="Saving…">Mark implemented</BusyLabel></button>
          </div>

          <div v-if="!open.archivedAt" class="dispatch">
            <label>Dispatch to
              <select v-model="target" data-test="backlog-team">
                <option v-if="teamsLoading && !teams.length" disabled value="">Loading teams…</option>
                <option v-for="t in teams" :key="t.id" :value="t.id">{{ t.name }}</option>
                <option value="new">A new team…</option>
              </select>
            </label>
            <template v-if="target === 'new'">
              <label>New team's name<input v-model="newTeam.name" data-test="backlog-new-team" placeholder="e.g. Billing" /></label>
              <label>Its repository (address or folder, optional)<input v-model="newTeam.repo" class="mono" placeholder="https://github.com/owner/repo.git" /></label>
              <label>Manager's agent
                <select v-model="newTeam.cli"><option value="claude">Claude</option><option value="codex">Codex</option><option value="grok">Grok</option><option value="copilot">Copilot</option><option value="antigravity">Antigravity</option></select>
              </label>
            </template>
            <p v-if="confirmAgain" class="warn small" data-test="backlog-again-confirm">
              {{ open.label }} is still in flight with {{ open.dispatchedTeam }}. Dispatch it again anyway?
            </p>
            <div class="buttons">
              <button v-if="confirmAgain" :disabled="busy" @click="confirmAgain = false">Cancel</button>
              <button class="primary" data-test="backlog-dispatch" :disabled="busy || !!dispatchBlocked" :title="dispatchBlocked ?? ''" :aria-busy="doing === 'dispatch'" @click="dispatch">
                <BusyLabel :busy="doing === 'dispatch'" text="Dispatching…">{{ confirmAgain ? 'Dispatch again' : open.inFlight ? 'Dispatch again…' : 'Dispatch' }}</BusyLabel>
              </button>
            </div>
            <p v-if="dispatchBlocked" class="muted small" data-test="backlog-dispatch-why">{{ dispatchBlocked }}</p>
          </div>

          <LoadingNote v-if="open.dispatchCount > 0 && detail?.id !== open.id" text="Loading its dispatches…" />
          <div v-if="detail?.id === open.id && detail.dispatches.length" class="history" data-test="backlog-history">
            <h4>Dispatches</h4>
            <div v-for="d in detail.dispatches" :key="d.workflow" class="disp">
              <div class="drow">
                <b>{{ d.team }}</b><span class="muted small">workflow #{{ d.workflow }} · {{ ago(d.dispatchedAt) }}</span><span class="grow" />
                <span class="dstate" :class="d.teamGone ? 'gone' : d.state">{{ stateText(d) }}</span>
              </div>
              <div class="muted small">
                {{ d.cardsDone }}/{{ d.cards }} cards done · {{ d.runs }} run{{ d.runs === 1 ? '' : 's' }}<template v-if="d.members.length"> by {{ d.members.join(', ') }}</template>
                <template v-if="elapsed(d)"> · {{ elapsed(d) }}</template>
                <template v-if="d.inputTokens || d.outputTokens"> · {{ tokens(d.inputTokens) }} in / {{ tokens(d.outputTokens) }} out tokens</template>
                <template v-if="d.ending"> · {{ d.ending }}</template>
              </div>
              <div v-if="d.teamId !== null" class="links">
                <button class="link" @click="emit('openTeam', d.teamId!)">Open team</button>
                <button class="link" @click="emit('board', d.teamId)">Its board</button>
              </div>
            </div>
          </div>

          <div class="buttons end">
            <button v-if="!open.archivedAt" data-test="backlog-archive" :disabled="busy" @click="simple('backlog.archive')"><BusyLabel :busy="doing === 'backlog.archive'" text="Archiving…">Archive</BusyLabel></button>
            <template v-else>
              <button data-test="backlog-restore" :disabled="busy" @click="simple('backlog.restore')"><BusyLabel :busy="doing === 'backlog.restore'" text="Restoring…">Restore</BusyLabel></button>
              <template v-if="confirmDelete">
                <span class="small">Delete {{ open.label }} for good?</span>
                <button class="danger" data-test="backlog-delete-yes" :disabled="busy" @click="simple('backlog.delete')"><BusyLabel :busy="doing === 'backlog.delete'" text="Deleting…">Delete</BusyLabel></button>
                <button :disabled="busy" @click="confirmDelete = false">Keep</button>
              </template>
              <button v-else class="danger-text" data-test="backlog-delete" :disabled="busy" @click="confirmDelete = true">Delete…</button>
            </template>
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
.primary { background: var(--accent); color: #fff; border-color: var(--accent); }
.danger { background: var(--bad); color: #fff; border-color: var(--bad); }
.danger-text { color: var(--bad); }
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
.list tr.sel td { background: color-mix(in srgb, var(--accent) 9%, #fff); box-shadow: inset 0 1px 0 color-mix(in srgb, var(--accent) 45%, transparent), inset 0 -1px 0 color-mix(in srgb, var(--accent) 45%, transparent); }
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
.state { font-size: 11px; padding: 1px 7px; border-radius: 10px; background: #f1f5f9; color: #475569; white-space: nowrap; }
.state.ready { background: #e0f2fe; color: #0369a1; }
.state.dispatched { background: #ede9fe; color: #5b21b6; }
.state.flight { background: #dbeafe; color: #1d4ed8; box-shadow: inset 0 0 0 1px #93c5fd; }
.state.declared { background: #fef9c3; color: #854d0e; }
.state.implemented { background: #dcfce7; color: #166534; }
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
.specbar { display: flex; gap: 4px; }
.md { border: 1px solid var(--line-soft); border-radius: 6px; padding: 6px 10px; font-size: 13px; overflow: auto; }
.buttons, .flow { display: flex; gap: 6px; justify-content: flex-end; flex-wrap: wrap; align-items: center; }
.buttons.end { border-top: 1px solid var(--line-soft); padding-top: 8px; }
.dispatch { display: flex; flex-direction: column; gap: 6px; border-top: 1px solid var(--line-soft); padding-top: 8px; }
.dispatch label { display: flex; flex-direction: column; gap: 3px; font-size: 12px; color: var(--ink-soft); }
.history h4 { margin: 4px 0; font-size: 13px; }
.disp { border: 1px solid var(--line-soft); border-radius: 8px; padding: 6px 8px; margin-bottom: 6px; display: flex; flex-direction: column; gap: 2px; }
.drow { display: flex; gap: 6px; align-items: baseline; }
.dstate { font-size: 11px; padding: 0 6px; border-radius: 8px; background: #f1f5f9; color: #475569; }
.dstate.open { background: #e0f2fe; color: #0369a1; }
.dstate.completed { background: #dcfce7; color: #166534; }
.dstate.gone { background: #fee2e2; color: #991b1b; }
.links { display: flex; gap: 10px; }
.link { border: 0; background: transparent; color: var(--accent); padding: 0; font-size: 12px; }
.warn { color: var(--warn); margin: 0; }
.error { color: var(--bad); }
</style>
