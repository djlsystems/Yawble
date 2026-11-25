<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { WORKFLOWS_GET, WORKFLOWS_LIST, type TeamSummary, type WorkflowDetail, type WorkflowRow, type WorkflowsListAnswer, type YawbleApi } from '../../../shared/api';
import { errorText, resolveApi } from '../api';
import { ago } from '../board/cards';
import CardPanel from '../board/CardPanel.vue';
import LoadingNote from '../components/LoadingNote.vue';
import Icon from '../components/Icon.vue';
import MemberAvatar from '../faces/MemberAvatar.vue';
import { displayName } from '../faces/faces';
import { workerSays } from '../waiting';
import { useNarrow } from '../narrowScreen';
import { pushEscape } from '../escapeClose';
import { readOnly } from '../remoteAccess';
import OverallTime from '../timeline/OverallTime.vue';
import { hasTime, openTimeline } from '../timeline/time';
import ItemLink from './ItemLink.vue';
import TimeBar from './TimeBar.vue';
import WorkflowPane from './WorkflowPane.vue';
import WorkflowStatusIcon from './WorkflowStatusIcon.vue';
import {
  Chips, DefaultFilters, formatHash, highlight, inChip, isWorkflowsHash, loadFilters, matchesText, nextSort, parseHash, saveFilters, sortRows, statusLook, statusTip,
  summaryLine, todayByHour, type Chip, type Filters, type SortKey, type WorkflowsRequest,
} from './page';

/**
 * THE WORKFLOWS PAGE - every team's workflows in one table, like the Backlog page: what runs, what waits on the
 * person, how long things took, and the way to the spec, the board and the Timeline. Read with workflows.list (the
 * core's workflows contract); the core filters by team, text and state and counts the chips, and the
 * page applies the same filters to what it has while an answer is on its way, so typing filters at once. The filters
 * are remembered (localStorage) and kept in the hash (#workflows?t=3&q=..&s=open&sel=12), so a link opens it filtered.
 * No tokens or cost anywhere (cost metrics are on hold).
 */
const props = defineProps<{ teams: TeamSummary[]; api?: YawbleApi; pollMs?: number; request?: WorkflowsRequest | null }>();
const emit = defineEmits<{ openTeam: [teamId: number]; board: [teamId: number | null]; backlog: [] }>();
const api = resolveApi(props.api);
const phone = useNarrow();

// ---- the filters: remembered, then the hash, then what the screen that opened the page asked for ----
const start = loadFilters();
const selected = ref<number | null>(null);
const fromHash = typeof location !== 'undefined' ? parseHash(location.hash) : null;
if (fromHash) {
  if (fromHash.teamId !== undefined) start.teamId = fromHash.teamId;
  if (fromHash.q !== undefined) start.q = fromHash.q;
  if (fromHash.chip) start.chip = fromHash.chip;
  if (fromHash.sort) start.sort = fromHash.sort;
  if (fromHash.selected !== undefined) selected.value = fromHash.selected;
}
const filters = ref<Filters>(start);
/** The workflow the page was opened at when the list does not hold it (an old one past the newest): read by itself. */
const single = ref<WorkflowRow | null>(null);
/** "Workflow #N was not found": the page was opened at a workflow the core does not know. */
const notFound = ref<string | null>(null);
function applyRequest(r: WorkflowsRequest | null | undefined) {
  if (!r) return;
  const f = { ...filters.value };
  if (r.teamId !== undefined) f.teamId = r.teamId;
  // Opened at one workflow: whatever its state, it is shown (All, no text).
  if (r.workflow != null) { f.chip = 'all'; f.q = ''; selected.value = r.workflow; cursor.value = r.workflow; notFound.value = null; single.value = null; }
  filters.value = f;
}
const cursor = ref<number | null>(selected.value);
applyRequest(props.request);
watch(() => props.request?.seq, () => applyRequest(props.request));

// ---- loading ----
const answer = ref<WorkflowsListAnswer | null>(null);
const error = ref<string | null>(null);
const Limit = 500;
let asked = 0;
async function load() {
  const n = ++asked;
  const f = filters.value;
  try {
    const a = await api.call<WorkflowsListAnswer>(WORKFLOWS_LIST, {
      ...(f.teamId !== null ? { teamId: f.teamId } : {}), ...(f.q.trim() ? { q: f.q.trim() } : {}), state: f.chip, limit: Limit,
    });
    if (n !== asked) return; // a newer filter asked meanwhile
    noteFresh(a.workflows);
    answer.value = a;
    error.value = null;
  } catch (e) {
    if (n !== asked) return;
    // A team that is gone (no_such_team): back to all teams rather than an empty page.
    if ((e as { code?: string })?.code === 'no_such_team' && filters.value.teamId !== null) { filters.value = { ...filters.value, teamId: null }; return; }
    error.value = errorText(e);
  }
}
let typing: ReturnType<typeof setTimeout> | null = null;
watch([() => filters.value.teamId, () => filters.value.chip], () => { void load(); });
watch(() => filters.value.q, () => { if (typing) clearTimeout(typing); typing = setTimeout(() => { typing = null; void load(); }, 200); });
watch(filters, (f) => saveFilters(f), { deep: true });
let timer: ReturnType<typeof setInterval> | null = null;
onMounted(() => { void load(); timer = setInterval(load, props.pollMs ?? 3000); });

// ---- the hash: kept while the page is shown, cleared when it goes ----
function writeHash() {
  try {
    const h = formatHash(filters.value, selected.value);
    if (location.hash !== h) history.replaceState(history.state, '', `${location.pathname}${location.search}${h}`);
  } catch { /* no history (a test page): nothing kept */ }
}
watch([filters, selected], writeHash, { deep: true, immediate: true });
onBeforeUnmount(() => {
  if (timer) clearInterval(timer);
  if (typing) clearTimeout(typing);
  try { if (isWorkflowsHash(location.hash)) history.replaceState(history.state, '', `${location.pathname}${location.search}`); } catch { /* likewise */ }
});

// ---- what is shown ----
/** The team filter's choices: the person's teams, and any team a row names that is not among them. */
const teamOptions = computed(() => {
  const m = new Map<number, string>(props.teams.map((t) => [t.id, t.name]));
  for (const w of answer.value?.workflows ?? []) if (!m.has(w.teamId)) m.set(w.teamId, w.team);
  return [...m].map(([id, name]) => ({ id, name })).sort((a, b) => a.name.localeCompare(b.name));
});
/** The list's rows, and the workflow read by itself when the list does not hold it. */
const rowsAll = computed<WorkflowRow[]>(() => {
  const list = answer.value?.workflows ?? [];
  const s = single.value;
  return s && !list.some((w) => w.id === s.id) ? [...list, s] : list;
});
const shown = computed(() => {
  const f = filters.value;
  const rows = rowsAll.value.filter((w) => (f.teamId === null || w.teamId === f.teamId) && inChip(w, f.chip) && matchesText(w, f.q));
  return sortRows(rows, f.sort);
});
const counts = computed(() => answer.value?.counts ?? null);
const backlogReadable = computed(() => answer.value?.backlog ?? true);
const open = computed(() => (selected.value === null ? null : rowsAll.value.find((w) => w.id === selected.value) ?? null));
// Opened at a workflow the list does not hold (older than the newest the core lists, or the list not yet in): read that
// one with workflows.get and open its pane; one the core does not know is named in a note and no pane opens.
let reading: number | null = null;
async function readSingle(id: number) {
  if (reading === id) return;
  reading = id;
  try {
    const d = await api.call<WorkflowDetail>(WORKFLOWS_GET, { workflow: id });
    if (selected.value !== id) return;
    if (d?.workflow?.id !== id) throw Object.assign(new Error('no such workflow'), { code: 'no_such_workflow' });
    single.value = d.workflow;
  } catch (e) {
    if (selected.value !== id) return;
    notFound.value = (e as { code?: string })?.code === 'no_such_workflow' ? `Workflow #${id} was not found.` : `Workflow #${id} could not be loaded: ${errorText(e)}`;
    selected.value = null;
  } finally { if (reading === id) reading = null; }
}
watch([selected, answer], ([id, a]) => {
  if (id === null || !a || single.value?.id === id || (a.workflows ?? []).some((w) => w.id === id)) return;
  void readSingle(id);
}, { immediate: true });
const filtered = computed(() => filters.value.teamId !== null || !!filters.value.q.trim() || filters.value.chip !== 'all');
// The header's Clear shows only when the filters differ from the defaults (the page opens on the Open chip) and puts them back.
const atDefaults = computed(() => filters.value.teamId === DefaultFilters.teamId && !filters.value.q.trim() && filters.value.chip === DefaultFilters.chip);
function resetFilters() { filters.value = { ...filters.value, teamId: DefaultFilters.teamId, q: '', chip: DefaultFilters.chip }; }

// ---- the summary line and today's sparkline (started per hour, completed per hour) ----
const summary = computed(() => summaryLine(counts.value));
const spark = computed(() => {
  const { started, completed } = todayByHour(answer.value?.workflows ?? []);
  const max = Math.max(1, ...started, ...completed);
  const line = (v: number[]) => v.map((n, i) => `${i === 0 ? 'M' : 'L'}${(i * 60) / 23},${14 - (n / max) * 12}`).join(' ');
  const total = (v: number[]) => v.reduce((a, b) => a + b, 0);
  return { started: line(started), completed: line(completed), any: total(started) + total(completed) > 0, says: `Today: ${total(started)} started, ${total(completed)} completed` };
});

// ---- rows that appear after the first answer ease in ----
const seen = new Set<number>();
const fresh = ref(new Set<number>());
let first = true;
function noteFresh(rows: WorkflowRow[]) {
  const now = new Set<number>();
  for (const w of rows) { if (!first && !seen.has(w.id)) now.add(w.id); seen.add(w.id); }
  first = false;
  if (now.size) { fresh.value = now; setTimeout(() => { if (fresh.value === now) fresh.value = new Set(); }, 1200); }
}

// ---- sorting ----
function sortBy(key: SortKey) { filters.value = { ...filters.value, sort: nextSort(filters.value.sort, key) }; }
const arrow = (key: SortKey) => (filters.value.sort.key === key ? (filters.value.sort.desc ? ' ▾' : ' ▴') : '');
const ariaSort = (key: SortKey) => (filters.value.sort.key === key ? (filters.value.sort.desc ? 'descending' : 'ascending') : undefined);
function setChip(c: Chip) { filters.value = { ...filters.value, chip: c }; }
function setTeam(v: string) { filters.value = { ...filters.value, teamId: v ? Number(v) : null }; }
function clearFilters() { filters.value = { ...filters.value, teamId: null, q: '', chip: 'all' }; }

// ---- selection and the keyboard: up/down move the selection, Enter opens the pane, Esc closes it ----
function select(w: WorkflowRow) { cursor.value = w.id; selected.value = w.id; notFound.value = null; }
function closePane() { selected.value = null; }
const listEl = ref<HTMLElement | null>(null);
function move(step: 1 | -1) {
  const rows = shown.value;
  if (!rows.length) return;
  const at = rows.findIndex((w) => w.id === cursor.value);
  const to = at < 0 ? (step > 0 ? 0 : rows.length - 1) : Math.min(rows.length - 1, Math.max(0, at + step));
  cursor.value = rows[to].id;
  if (selected.value !== null) selected.value = rows[to].id; // the pane follows while it is open
  void nextTick(() => listEl.value?.querySelector(`[data-wf="${cursor.value}"]`)?.scrollIntoView?.({ block: 'nearest' }));
}
function onKey(e: KeyboardEvent) {
  if (e.defaultPrevented || e.altKey || e.ctrlKey || e.metaKey) return;
  const t = e.target instanceof HTMLElement ? e.target : null;
  if (t && (t.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(t.tagName))) return;
  if (document.querySelector('[aria-modal="true"]') || openCard.value !== null) return; // a dialog or the card panel has the keys
  if (e.key === 'ArrowDown') { e.preventDefault(); move(1); }
  else if (e.key === 'ArrowUp') { e.preventDefault(); move(-1); }
  else if (e.key === 'Enter' && cursor.value !== null && t?.tagName !== 'BUTTON' && t?.getAttribute('role') !== 'button') { e.preventDefault(); selected.value = cursor.value; }
}
onMounted(() => window.addEventListener('keydown', onKey));
onBeforeUnmount(() => window.removeEventListener('keydown', onKey));
let releaseEsc: (() => void) | null = null;
watch(() => selected.value !== null, (on) => { releaseEsc?.(); releaseEsc = on ? pushEscape(closePane) : null; }, { immediate: true });
onBeforeUnmount(() => releaseEsc?.());

// ---- the card panel (the board's), opened from the pane's cards ----
const openCard = ref<number | null>(null);
const Lanes = [{ id: 'todo' as const, label: 'To Do' }, { id: 'in-progress' as const, label: 'In Progress' }, { id: 'blocked' as const, label: 'Needs You' }, { id: 'done' as const, label: 'Done' }];

// ---- the Who column: who works it now, then the owner ----
type Face = { name: string; state: 'running' | 'waiting' | 'idle'; says: string };
function faces(w: WorkflowRow): Face[] {
  const out: Face[] = w.members.map((m) => ({ name: m.name, state: m.state === 'running' ? 'running' : 'waiting',
    says: `${displayName(m.name)}: ${workerSays(m)}` }));
  if (w.owner && !w.members.some((m) => m.name === w.owner)) out.push({ name: w.owner, state: 'idle', says: `${displayName(w.owner)}: owner` });
  return out;
}
const timeline = (w: WorkflowRow) => openTimeline({ kind: 'workflow', id: w.id, title: w.title, team: w.team });
</script>

<template>
  <div class="wf-place" :class="{ phone, 'card-open': openCard !== null }" data-test="workflows-view">
    <section class="wf-main">
      <div class="wrap">
        <header v-show="!(phone && open)" class="head">
          <h2 v-if="!phone">Workflows</h2>
          <select :value="filters.teamId ?? ''" data-test="workflows-team-filter" title="Team" aria-label="Team" @change="setTeam(($event.target as HTMLSelectElement).value)">
            <option value="">All teams</option>
            <option v-for="t in teamOptions" :key="t.id" :value="t.id">{{ t.name }}</option>
          </select>
          <input v-model="filters.q" class="search" type="search" placeholder="Filter: id, title, item, team, member" aria-label="Filter the workflows" data-test="workflows-search" />
          <span class="grow" />
          <button v-if="!atDefaults" class="link" data-test="workflows-clear" @click="resetFilters">{{ phone ? 'Clear' : 'Clear filters' }}</button>
        </header>
        <div v-show="!(phone && open)" class="chips" role="tablist" aria-label="Status" data-test="workflows-chips">
          <button v-for="c in Chips" :key="c.id" type="button" role="tab" class="chip" :class="[c.id, { on: filters.chip === c.id }]" :aria-selected="filters.chip === c.id"
            :data-test="`workflows-chip-${c.id}`" @click="setChip(c.id)">
            {{ c.label }}<span class="n" data-test="workflows-chip-count">{{ counts ? counts[c.count] : '·' }}</span>
          </button>
          <span class="grow" />
          <span v-if="summary && !phone" class="summary muted" data-test="workflows-summary">
            {{ summary }}
            <svg v-if="spark.any" class="spark" viewBox="-1 0 62 16" role="img" :aria-label="spark.says" data-test="workflows-sparkline"><title>{{ spark.says }}</title>
              <path class="s-started" :d="spark.started" /><path class="s-completed" :d="spark.completed" /></svg>
          </span>
        </div>
        <p v-if="summary && phone && !open" class="summary muted phone-sum" data-test="workflows-summary">{{ summary }}</p>
        <p v-if="error" class="error" data-test="workflows-error">{{ error }}</p>
        <p v-if="notFound" class="note" role="status" data-test="workflows-not-found">{{ notFound }}
          <button class="link" data-test="workflows-not-found-dismiss" @click="notFound = null">Dismiss</button></p>
        <p v-if="answer?.truncated" class="muted small" data-test="workflows-truncated">Showing every open workflow and the newest {{ Limit }} others; filter to find an older one.</p>

        <div class="split" :class="{ withItem: !!open }">
          <div v-show="!(phone && open)" ref="listEl" class="listwrap" data-test="workflows-listwrap">
            <table class="list" :class="{ narrow: !!open }" data-test="workflows-table">
              <thead>
                <tr>
                  <th class="sortable" :aria-sort="ariaSort('status')" data-test="workflows-sort-status" @click="sortBy('status')">Status{{ arrow('status') }}</th>
                  <th class="sortable" :aria-sort="ariaSort('title')" data-test="workflows-sort-title" @click="sortBy('title')">Workflow{{ arrow('title') }}</th>
                  <th class="sortable" :aria-sort="ariaSort('item')" data-test="workflows-sort-item" @click="sortBy('item')">Item{{ arrow('item') }}</th>
                  <th class="sortable" :aria-sort="ariaSort('team')" data-test="workflows-sort-team" @click="sortBy('team')">Team{{ arrow('team') }}</th>
                  <th>Who</th>
                  <th class="sortable" :aria-sort="ariaSort('elapsed')" data-test="workflows-sort-elapsed" @click="sortBy('elapsed')">Elapsed{{ arrow('elapsed') }}</th>
                  <th class="sortable" :aria-sort="ariaSort('last')" data-test="workflows-sort-last" @click="sortBy('last')">Last activity{{ arrow('last') }}</th>
                </tr>
              </thead>
              <tbody>
                <tr v-for="w in shown" :key="w.id" :class="[statusLook(w).status, { sel: selected === w.id, cur: cursor === w.id, fresh: fresh.has(w.id), working: w.working }]"
                  :aria-selected="selected === w.id" :data-wf="w.id" :data-status="w.status" data-test="workflows-row" @click="select(w)">
                  <td class="st" :title="statusTip(w)">
                    <span class="st-in"><WorkflowStatusIcon :workflow="w" /><span class="st-word" data-test="workflows-status">{{ statusLook(w).label }}</span></span>
                  </td>
                  <td class="ttl">
                    <span class="wid mono">#<template v-for="(p, i) in highlight(String(w.id), filters.q)" :key="i"><mark v-if="p.hit">{{ p.text }}</mark><template v-else>{{ p.text }}</template></template></span>
                    <span class="wtitle" data-test="workflows-title"><template v-for="(p, i) in highlight(w.title, filters.q)" :key="i"><mark v-if="p.hit" data-test="workflows-hit">{{ p.text }}</mark><template v-else>{{ p.text }}</template></template></span>
                  </td>
                  <td class="itm"><ItemLink :item="w.item" :backlog="backlogReadable" :q="filters.q" /></td>
                  <td class="tm"><template v-for="(p, i) in highlight(w.team, filters.q)" :key="i"><mark v-if="p.hit">{{ p.text }}</mark><template v-else>{{ p.text }}</template></template></td>
                  <td class="who">
                    <span class="faces" data-test="workflows-who">
                      <span v-for="f in faces(w).slice(0, 4)" :key="f.name" class="face" :title="f.says"><MemberAvatar :team="w.team" :member="f.name" :state="f.state" :size="20" /></span>
                      <span v-if="faces(w).length > 4" class="more muted small">+{{ faces(w).length - 4 }}</span>
                    </span>
                  </td>
                  <td class="el">
                    <span v-if="hasTime(w.time)" class="el-in">
                      <span class="el-top">
                        <OverallTime :time="w.time" :target="{ kind: 'workflow', id: w.id, title: w.title, team: w.team }" />
                        <button type="button" class="tl-btn" title="Open its timeline" aria-label="Open its timeline" data-test="workflows-timeline" @click.stop="timeline(w)">
                          <svg viewBox="0 0 16 16" aria-hidden="true"><path d="M2 4h7 M5 8h9 M3 12h6" /></svg>
                        </button>
                      </span>
                      <TimeBar :time="w.time" />
                    </span>
                    <span v-else class="muted">—</span>
                  </td>
                  <td class="when muted small" :title="w.lastAt">{{ ago(w.lastAt) }}</td>
                </tr>
                <tr v-if="!answer && !error"><td colspan="7"><LoadingNote text="Loading the workflows…" /></td></tr>
                <tr v-else-if="answer && !shown.length">
                  <td colspan="7" class="empty" data-test="workflows-empty">
                    <template v-if="filtered && (counts?.all ?? 0) > 0">
                      <p>Nothing matches these filters.</p>
                      <button data-test="workflows-empty-clear" @click="clearFilters">Show all workflows</button>
                    </template>
                    <template v-else>
                      <svg class="empty-art" viewBox="0 0 64 40" aria-hidden="true"><rect x="4" y="6" width="18" height="12" rx="3" /><rect x="42" y="22" width="18" height="12" rx="3" /><path d="M22 12h10v16h10" /></svg>
                      <p>{{ filters.chip === 'open' || filters.chip === 'needs-you' ? 'All quiet: nothing open right now.' : 'No workflows yet.' }}</p>
                      <p class="muted small">A workflow starts when you dispatch a backlog item to a team, or tell a Manager something.</p>
                      <button class="primary" data-test="workflows-empty-backlog" @click="emit('backlog')">Go to the Backlog</button>
                    </template>
                  </td>
                </tr>
              </tbody>
            </table>
          </div>

          <WorkflowPane v-if="open" :key="open.id" :row="open" :api="api" :poll-ms="pollMs" :read-only="readOnly" :phone="phone"
            @close="closePane" @card="openCard = $event" @open-team="emit('openTeam', $event)" @board="emit('board', $event)" @changed="load" />
        </div>
      </div>
    </section>
    <CardPanel v-if="openCard !== null" :id="openCard" :lanes="Lanes" :api="api" @close="openCard = null" @changed="load" @open-team="emit('openTeam', $event)" />
  </div>
</template>

<style scoped>
.wf-place { height: 100%; display: flex; min-height: 0; }
.wf-main { flex: 1; min-width: 0; overflow: auto; padding: 4px 24px 24px; display: flex; flex-direction: column; }
.wrap { max-width: 1440px; width: 100%; box-sizing: border-box; margin: 12px auto 0; flex: 1 1 0; min-height: 360px; display: flex; flex-direction: column; }
.wrap > * { flex: none; }
.wrap > .split { flex: 1 1 0; min-height: 0; grid-template-rows: minmax(0, 1fr); align-items: stretch; }
.head { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; }
.head h2 { margin: 0 12px 0 0; font-size: 22px; }
.head select, .search { font: inherit; font-size: 13px; padding: 5px 8px; border: 1px solid var(--line); border-radius: 6px; }
.search { width: 260px; }
.grow { flex: 1; }
.link { border: 0; background: transparent; color: var(--accent); padding: 0; font-size: 12.5px; cursor: pointer; }
.primary { background: var(--accent); color: var(--on-accent); border-color: var(--accent); }
.chips { display: flex; flex-wrap: wrap; gap: 6px; align-items: center; margin: 10px 0 10px; }
.chip { display: inline-flex; align-items: center; gap: 6px; border: 1px solid var(--line); background: transparent; border-radius: 14px; padding: 3px 10px; font-size: 12.5px; color: var(--ink-soft); cursor: pointer; }
.chip:hover { border-color: var(--accent); color: var(--ink); }
.chip.on { background: color-mix(in srgb, var(--accent) 12%, transparent); border-color: var(--accent); color: var(--accent); font-weight: 600; }
.chip .n { font-size: 11px; min-width: 16px; text-align: center; padding: 0 5px; border-radius: 8px; background: var(--chip); color: var(--ink-soft); font-variant-numeric: tabular-nums; }
.chip.needs-you .n { background: color-mix(in srgb, var(--state-decision) 18%, transparent); color: var(--state-decision); }
.summary { display: inline-flex; align-items: center; gap: 8px; font-size: 12.5px; }
.spark { width: 62px; height: 16px; overflow: visible; }
.spark path { fill: none; stroke-width: 1.3; stroke-linejoin: round; stroke-linecap: round; }
.s-started { stroke: var(--state-running); opacity: .55; }
.s-completed { stroke: var(--ok); }
.error { color: var(--bad); }
.note { margin: 0 0 8px; padding: 6px 10px; border: 1px solid var(--line); border-radius: 6px; font-size: 13px; color: var(--ink-soft); display: flex; gap: 10px; align-items: center; }
.muted { color: var(--muted); }
.small { font-size: 12px; }
.mono { font-family: ui-monospace, Consolas, monospace; font-size: 12px; }
.split { display: grid; grid-template-columns: 1fr; gap: 14px; align-items: start; }
.split.withItem { grid-template-columns: minmax(0, 1fr) minmax(340px, 420px); }
.listwrap { min-height: 0; min-width: 0; overflow: auto; outline: none; }
/* The table: the Backlog's styles (sticky muted headers, 13 px rows, the accent selection). */
.list { width: 100%; border-collapse: collapse; font-size: 13px; }
.list th { position: sticky; top: 0; z-index: 1; background: var(--surface); text-align: left; font-size: 11.5px; color: var(--muted); font-weight: 600; padding: 4px 6px; border-bottom: 1px solid var(--line); white-space: nowrap; }
.list th.sortable { cursor: pointer; user-select: none; }
.list th.sortable:hover { color: var(--ink); }
.list td { padding: 7px 6px; border-bottom: 1px solid var(--line-soft); vertical-align: middle; }
.list tr[data-test=workflows-row] { cursor: pointer; position: relative; }
.list tr[data-test=workflows-row]:hover td { background: color-mix(in srgb, var(--ink) 3%, var(--surface)); }
.list tr.sel td { background: color-mix(in srgb, var(--accent) 9%, var(--surface)); box-shadow: inset 0 1px 0 color-mix(in srgb, var(--accent) 45%, transparent), inset 0 -1px 0 color-mix(in srgb, var(--accent) 45%, transparent); }
.list tr.sel td:first-child { box-shadow: inset 4px 0 0 var(--accent), inset 0 1px 0 color-mix(in srgb, var(--accent) 45%, transparent), inset 0 -1px 0 color-mix(in srgb, var(--accent) 45%, transparent); }
.list tr.cur:not(.sel) td:first-child { box-shadow: inset 3px 0 0 color-mix(in srgb, var(--accent) 50%, transparent); }
/* A workflow that just appeared eases in. */
.list tr.fresh td { animation: row-in .6s ease-out; }
@keyframes row-in { from { opacity: 0; transform: translateY(-4px); background: color-mix(in srgb, var(--accent) 14%, var(--surface)); } }
@media (prefers-reduced-motion: reduce) { .list tr.fresh td { animation: none; } }
.st { white-space: nowrap; }
.st-in { display: inline-flex; align-items: center; gap: 6px; }
.st-word { font-size: 12.5px; font-weight: 500; }
tr.needs-you .st-word { color: var(--state-decision); font-weight: 600; }
tr.closed .st-word, tr.expired .st-word { color: var(--muted); }
.ttl { min-width: 220px; }
.wid { color: var(--muted); margin-right: 6px; }
.wtitle { font-weight: 500; }
tr.closed .wtitle, tr.expired .wtitle { color: var(--ink-soft); font-weight: 400; }
mark { background: color-mix(in srgb, var(--pill-yellow-bg) 85%, transparent); color: inherit; border-radius: 2px; padding: 0 1px; box-shadow: 0 0 0 1px color-mix(in srgb, var(--pill-yellow-ink) 25%, transparent); }
.itm, .tm { white-space: nowrap; }
.tm { max-width: 180px; overflow: hidden; text-overflow: ellipsis; }
/* The pane open beside the table: Team, Who and Last activity give way (the pane names them), so Elapsed stays in sight. */
.list.narrow th:nth-child(4), .list.narrow td.tm, .list.narrow th:nth-child(5), .list.narrow td.who, .list.narrow th:nth-child(7), .list.narrow td.when { display: none; }
.list.narrow .ttl { min-width: 0; }
.faces { display: inline-flex; align-items: center; gap: 2px; }
.face { display: inline-flex; }
.el { white-space: nowrap; }
.el-in { display: inline-flex; flex-direction: column; gap: 3px; min-width: 96px; }
.el-top { display: inline-flex; align-items: center; gap: 2px; }
.el-top :deep(.ot) { font-size: 12px; color: var(--ink-soft); }
.tl-btn { display: inline-grid; place-items: center; width: 20px; height: 18px; padding: 0; border: 0; border-radius: 4px; background: transparent; color: var(--muted); cursor: pointer; }
.tl-btn:hover { color: var(--accent); background: color-mix(in srgb, var(--accent) 10%, transparent); }
.tl-btn svg { width: 13px; height: 13px; fill: none; stroke: currentColor; stroke-width: 1.6; stroke-linecap: round; }
.when { white-space: nowrap; }
.empty { text-align: center; padding: 36px 12px; }
.empty p { margin: 6px 0; }
.empty-art { width: 80px; height: 50px; margin-bottom: 6px; }
.empty-art rect, .empty-art path { fill: none; stroke: var(--accent); stroke-width: 1.6; stroke-linejoin: round; opacity: .7; }
.empty-art path { stroke-dasharray: 3 3; }
/* The card panel beside the page, as on the board; on a phone it takes the page. */
.wf-place.phone.card-open > .wf-main { display: none; }
/* A phone: the rows wrap (no table columns), an opened workflow takes the page (Back returns to the list). */
@media (max-width: 759.98px) {
  .wf-main { padding: 4px 10px 16px; }
  .wrap { min-height: 0; margin-top: 4px; display: block; }
  .head { gap: 6px; }
  .search { width: auto; flex: 1 1 100%; min-width: 0; order: -1; }
  .head select { flex: 1 1 0; min-width: 0; }
  .head .grow { display: none; }
  .head select, .search, .chip, .link, .empty button { min-height: 44px; box-sizing: border-box; }
  .chips { gap: 4px; flex-wrap: nowrap; overflow-x: auto; margin: 8px -10px; padding: 0 10px; scrollbar-width: none; }
  .chip { flex: none; }
  .chip { padding: 3px 9px; }
  .chips .grow { display: none; }
  .phone-sum { margin: 0 0 6px; font-size: 12.5px; }
  .list.narrow td.tm, .list.narrow td.who, .list.narrow td.when { display: block; }
  .split.withItem { grid-template-columns: minmax(0, 1fr); }
  .listwrap { overflow: visible; }
  .list thead { display: none; }
  .list, .list tbody { display: block; }
  .list tr[data-test=workflows-row] { display: flex; flex-wrap: wrap; align-items: center; gap: 4px 10px; padding: 9px 6px; border-bottom: 1px solid var(--line-soft); }
  .list tr[data-test=workflows-row] td { display: block; padding: 0; border: 0; background: transparent !important; box-shadow: none !important; }
  .list tr.sel { background: color-mix(in srgb, var(--accent) 9%, var(--surface)); box-shadow: inset 4px 0 0 var(--accent); }
  .list td.st { flex: 1; }
  .list td.ttl { order: 5; flex: 1 1 100%; min-width: 0; overflow-wrap: anywhere; }
  .list td.itm, .list td.tm, .list td.when { order: 6; }
  .list td.who { order: 3; }
  .list td.el { order: 7; flex: 1 1 100%; }
  .el-in { flex-direction: row; align-items: center; gap: 10px; }
  .tl-btn { width: 44px; height: 44px; }
}
</style>
