<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { usePoll } from '../usePoll';
import { DELIVERABLES_GET, DELIVERABLES_LIST, GOALS_LIST, type Goal, type GoalsListAnswer, type TeamSummary, type DeliverableDetail, type DeliverableRow, type DeliverablesListAnswer, type YawbleApi } from '../../../shared/api';
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
import DeliverablePane from './DeliverablePane.vue';
import DeliverableStatusIcon from './DeliverableStatusIcon.vue';
import GoalPicker from '../goals/GoalPicker.vue';
import AssignGoalDialog from '../goals/AssignGoalDialog.vue';
import {
  Chips, DefaultFilters, formatHash, highlight, inChip, isDeliverablesHash, loadFilters, matchesText, nextSort, parseHash, saveFilters, sortRows, statusLook, statusTip,
  summaryLine, todayByHour, type Chip, type Filters, type GoalFilter, type SortKey, type DeliverablesRequest,
} from './page';

/**
 * The Deliverables page: every team's deliverables in one table. Read with deliverables.list; the core filters by
 * team, text and state and counts the chips, and the page applies the same filters to what it has while an answer is
 * on its way. The filters are remembered and kept in the hash (#deliverables?t=3&q=..&s=open&sel=12).
 * Each row has a checkbox; with any ticked, a bar over the table offers what can be done to all of them at once.
 */
const props = defineProps<{ teams: TeamSummary[]; api?: YawbleApi; pollMs?: number; request?: DeliverablesRequest | null }>();
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
/** The goal the page is filtered to (opened from the Goals page): not remembered, cleared with its chip. */
const goalFilter = ref<GoalFilter | null>(null);
/** The deliverable the page was opened at when the list does not hold it (an old one past the newest): read by itself. */
const single = ref<DeliverableRow | null>(null);
/** "Deliverable #N was not found": the page was opened at a deliverable the core does not know. */
const notFound = ref<string | null>(null);
function applyRequest(r: DeliverablesRequest | null | undefined) {
  if (!r) return;
  const f = { ...filters.value };
  if (r.teamId !== undefined) f.teamId = r.teamId;
  // Opened at a goal: every one of its deliverables, whatever its state or team.
  if (r.goal !== undefined) { goalFilter.value = r.goal ?? null; if (r.goal) { f.chip = 'all'; f.q = ''; f.teamId = r.teamId ?? null; } }
  // Opened at one deliverable: whatever its state, it is shown (All, no text).
  // ...and in no goal's filter unless the request names one, so its row is in the table.
  if (r.deliverable != null && r.goal === undefined) goalFilter.value = null;
  if (r.deliverable != null) { f.chip = 'all'; f.q = ''; selected.value = r.deliverable; cursor.value = r.deliverable; notFound.value = null; single.value = null; }
  filters.value = f;
}
const cursor = ref<number | null>(selected.value);
applyRequest(props.request);
watch(() => props.request?.seq, () => applyRequest(props.request));

// ---- loading ----
const answer = ref<DeliverablesListAnswer | null>(null);
const error = ref<string | null>(null);
const Limit = 500;
let asked = 0;
async function load() {
  const n = ++asked;
  const f = filters.value;
  try {
    const a = await api.call<DeliverablesListAnswer>(DELIVERABLES_LIST, {
      ...(f.teamId !== null ? { teamId: f.teamId } : {}), ...(f.q.trim() ? { q: f.q.trim() } : {}), state: f.chip, limit: Limit,
      ...(goalFilter.value ? { goal: goalFilter.value.id } : {}),
    });
    if (n !== asked) return; // a newer filter asked meanwhile
    noteFresh(a.deliverables);
    answer.value = a;
    error.value = null;
  } catch (e) {
    if (n !== asked) return;
    // A team that is gone (no_such_team): back to all teams rather than an empty page.
    if ((e as { code?: string })?.code === 'no_such_team' && filters.value.teamId !== null) { filters.value = { ...filters.value, teamId: null }; return; }
    error.value = errorText(e);
    return false;
  }
}
// ---- the goals every row's picker offers: one goals.list with each load (an older core without goals: none) ----
const goals = ref<Goal[] | null>(null);
async function loadGoals() {
  try { goals.value = (await api.call<GoalsListAnswer>(GOALS_LIST, {}))?.goals ?? []; } catch { goals.value = goals.value ?? []; return; }
  // The goal the page is filtered to was deleted (another window, the Concierge): the chip goes, the page shows every goal's.
  const g = goalFilter.value;
  if (g && g.id !== 'none' && !goals.value.some((x) => x.id === g.id)) goalFilter.value = null;
}
onMounted(loadGoals);
async function goalChanged() { await Promise.all([load(), loadGoals()]); }
let typing: ReturnType<typeof setTimeout> | null = null;
watch([() => filters.value.teamId, () => filters.value.chip, () => goalFilter.value?.id], () => { void load(); });
watch(() => filters.value.q, () => { if (typing) clearTimeout(typing); typing = setTimeout(() => { typing = null; void load(); }, 200); });
watch(filters, (f) => saveFilters(f), { deep: true });
onMounted(() => { void load(); });
// Each poll reads the goals too (one small call), so a goal made elsewhere reaches the pickers.
usePoll(async () => (await Promise.all([load(), loadGoals()]))[0], props.pollMs ?? 3000, { immediate: false });

// ---- the hash: kept while the page is shown, cleared when it goes ----
function writeHash() {
  try {
    const h = formatHash(filters.value, selected.value);
    if (location.hash !== h) history.replaceState(history.state, '', `${location.pathname}${location.search}${h}`);
  } catch { /* no history (a test page): nothing kept */ }
}
watch([filters, selected], writeHash, { deep: true, immediate: true });
onBeforeUnmount(() => {
  if (typing) clearTimeout(typing);
  try { if (isDeliverablesHash(location.hash)) history.replaceState(history.state, '', `${location.pathname}${location.search}`); } catch { /* likewise */ }
});

// ---- what is shown ----
/** The team filter's choices: the person's teams, and any team a row names that is not among them. */
const teamOptions = computed(() => {
  const m = new Map<number, string>(props.teams.map((t) => [t.id, t.name]));
  for (const w of answer.value?.deliverables ?? []) if (!m.has(w.teamId)) m.set(w.teamId, w.team);
  return [...m].map(([id, name]) => ({ id, name })).sort((a, b) => a.name.localeCompare(b.name));
});
/** The list's rows, and the deliverable read by itself when the list does not hold it. */
const rowsAll = computed<DeliverableRow[]>(() => {
  const list = answer.value?.deliverables ?? [];
  const s = single.value;
  return s && !list.some((w) => w.id === s.id) ? [...list, s] : list;
});
const shown = computed(() => {
  const f = filters.value;
  const g = goalFilter.value;
  const inGoal = (w: DeliverableRow) => !g || w.goal === undefined || (g.id === 'none' ? !w.goal : w.goal?.id === g.id);
  const rows = rowsAll.value.filter((w) => (f.teamId === null || w.teamId === f.teamId) && inChip(w, f.chip) && matchesText(w, f.q) && inGoal(w));
  return sortRows(rows, f.sort);
});
const counts = computed(() => answer.value?.counts ?? null);
const backlogReadable = computed(() => answer.value?.backlog ?? true);
const open = computed(() => (selected.value === null ? null : rowsAll.value.find((w) => w.id === selected.value) ?? null));
// Opened at a deliverable the list does not hold (older than the newest the core lists, or the list not yet in): read that
// one with deliverables.get and open its pane; one the core does not know is named in a note and no pane opens.
let reading: number | null = null;
async function readSingle(id: number) {
  if (reading === id) return;
  reading = id;
  try {
    const d = await api.call<DeliverableDetail>(DELIVERABLES_GET, { deliverable: id });
    if (selected.value !== id) return;
    if (d?.deliverable?.id !== id) throw Object.assign(new Error('no such deliverable'), { code: 'no_such_deliverable' });
    single.value = d.deliverable;
  } catch (e) {
    if (selected.value !== id) return;
    notFound.value = (e as { code?: string })?.code === 'no_such_deliverable' ? `Deliverable #${id} was not found.` : `Deliverable #${id} could not be loaded: ${errorText(e)}`;
    selected.value = null;
  } finally { if (reading === id) reading = null; }
}
watch([selected, answer], ([id, a]) => {
  if (id === null || !a || single.value?.id === id || (a.deliverables ?? []).some((w) => w.id === id)) return;
  void readSingle(id);
}, { immediate: true });
const filtered = computed(() => filters.value.teamId !== null || !!filters.value.q.trim() || filters.value.chip !== 'all' || !!goalFilter.value);
// The header's Clear shows only when the filters differ from the defaults (the page opens on the Open chip) and puts them back.
const atDefaults = computed(() => filters.value.teamId === DefaultFilters.teamId && !filters.value.q.trim() && filters.value.chip === DefaultFilters.chip && !goalFilter.value);
function resetFilters() { goalFilter.value = null; filters.value = { ...filters.value, teamId: DefaultFilters.teamId, q: '', chip: DefaultFilters.chip }; }

// ---- the summary line and today's sparkline (started per hour, completed per hour) ----
const summary = computed(() => summaryLine(counts.value));
const spark = computed(() => {
  const { started, completed } = todayByHour(answer.value?.deliverables ?? []);
  const max = Math.max(1, ...started, ...completed);
  const line = (v: number[]) => v.map((n, i) => `${i === 0 ? 'M' : 'L'}${(i * 60) / 23},${14 - (n / max) * 12}`).join(' ');
  const total = (v: number[]) => v.reduce((a, b) => a + b, 0);
  return { started: line(started), completed: line(completed), any: total(started) + total(completed) > 0, says: `Today: ${total(started)} started, ${total(completed)} completed` };
});

// ---- rows that appear after the first answer ease in ----
const seen = new Set<number>();
const fresh = ref(new Set<number>());
let first = true;
function noteFresh(rows: DeliverableRow[]) {
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
function clearFilters() { goalFilter.value = null; filters.value = { ...filters.value, teamId: null, q: '', chip: 'all' }; }

// ---- selection and the keyboard: up/down move the selection, Enter opens the pane, Esc closes it ----
function select(w: DeliverableRow) { cursor.value = w.id; selected.value = w.id; notFound.value = null; }
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

// ---- ticked rows: the deliverables an action applies to at once ----
// A tick is kept only while its row is shown: a filter that hides a row unticks it, so the count is what the person sees.
const picked = ref(new Set<number>());
const pickedRows = computed(() => shown.value.filter((w) => picked.value.has(w.id)));
const allPicked = computed(() => shown.value.length > 0 && pickedRows.value.length === shown.value.length);
watch(shown, (rows) => {
  if (!picked.value.size) return;
  const ids = new Set(rows.map((w) => w.id));
  const kept = [...picked.value].filter((id) => ids.has(id));
  if (kept.length !== picked.value.size) picked.value = new Set(kept);
});
/** The row ticked or unticked last: Shift with the next click does the same to every row between the two. */
let pickedLast: number | null = null;
function pick(w: DeliverableRow, e: MouseEvent) {
  const next = new Set(picked.value);
  const on = !next.has(w.id);
  const rows = shown.value;
  const from = e.shiftKey && pickedLast !== null ? rows.findIndex((r) => r.id === pickedLast) : -1;
  const to = rows.findIndex((r) => r.id === w.id);
  const range = from >= 0 && to >= 0 ? rows.slice(Math.min(from, to), Math.max(from, to) + 1) : [w];
  for (const r of range) { if (on) next.add(r.id); else next.delete(r.id); }
  picked.value = next;
  pickedLast = w.id;
}
function pickAll() { picked.value = allPicked.value ? new Set() : new Set(shown.value.map((w) => w.id)); pickedLast = null; }
function clearPicked() { picked.value = new Set(); pickedLast = null; }
const assigning = ref(false);
function assigned() { assigning.value = false; clearPicked(); }
/** The table's columns: the checkbox column is not shown to a Visitor, who can change nothing. */
const columns = computed(() => (readOnly.value ? 8 : 9));

// ---- the card panel (the board's), opened from the pane's cards ----
const openCard = ref<number | null>(null);
const Lanes = [{ id: 'todo' as const, label: 'To Do' }, { id: 'in-progress' as const, label: 'In Progress' }, { id: 'blocked' as const, label: 'Needs You' }, { id: 'done' as const, label: 'Done' }];

// ---- the Who column: who works it now, then the owner ----
type Face = { name: string; state: 'running' | 'waiting' | 'idle'; says: string };
function faces(w: DeliverableRow): Face[] {
  const out: Face[] = w.members.map((m) => ({ name: m.name, state: m.state === 'running' ? 'running' : 'waiting',
    says: `${displayName(m.name)}: ${workerSays(m)}` }));
  if (w.owner && !w.members.some((m) => m.name === w.owner)) out.push({ name: w.owner, state: 'idle', says: `${displayName(w.owner)}: owner` });
  return out;
}
const timeline = (w: DeliverableRow) => openTimeline({ kind: 'deliverable', id: w.id, title: w.title, team: w.team });
</script>

<template>
  <div class="wf-place" :class="{ phone, 'card-open': openCard !== null }" data-test="deliverables-view">
    <section class="wf-main">
      <div class="wrap">
        <header v-show="!(phone && open)" class="head">
          <h2 v-if="!phone">Deliverables</h2>
          <select :value="filters.teamId ?? ''" data-test="deliverables-team-filter" title="Team" aria-label="Team" @change="setTeam(($event.target as HTMLSelectElement).value)">
            <option value="">All teams</option>
            <option v-for="t in teamOptions" :key="t.id" :value="t.id">{{ t.name }}</option>
          </select>
          <input v-model="filters.q" class="search" type="search" placeholder="Filter: id, title, item, team, member" aria-label="Filter the deliverables" data-test="deliverables-search" />
          <span class="grow" />
          <button v-if="!atDefaults" class="link" data-test="deliverables-clear" @click="resetFilters">{{ phone ? 'Clear' : 'Clear filters' }}</button>
        </header>
        <div v-show="!(phone && open)" class="chips" role="tablist" aria-label="Status" data-test="deliverables-chips">
          <button v-for="c in Chips" :key="c.id" type="button" role="tab" class="chip" :class="[c.id, { on: filters.chip === c.id }]" :aria-selected="filters.chip === c.id"
            :data-test="`deliverables-chip-${c.id}`" @click="setChip(c.id)">
            {{ c.label }}<span class="n" data-test="deliverables-chip-count">{{ counts ? counts[c.count] : '·' }}</span>
          </button>
          <span v-if="goalFilter" class="goal-chip" data-test="deliverables-goal-chip" :title="`Only the deliverables of ${goalFilter.id === 'none' ? 'no goal' : `the goal ${goalFilter.name}`}`">
            <Icon name="goals" :size="13" />{{ goalFilter.id === 'none' ? 'No goal' : `Goal: ${goalFilter.name}` }}
            <button type="button" class="chip-x" aria-label="Clear the goal filter" title="Clear the goal filter" data-test="deliverables-goal-chip-clear" @click="goalFilter = null">×</button>
          </span>
          <span class="grow" />
          <span v-if="summary && !phone" class="summary muted" data-test="deliverables-summary">
            {{ summary }}
            <svg v-if="spark.any" class="spark" viewBox="-1 0 62 16" role="img" :aria-label="spark.says" data-test="deliverables-sparkline"><title>{{ spark.says }}</title>
              <path class="s-started" :d="spark.started" /><path class="s-completed" :d="spark.completed" /></svg>
          </span>
        </div>
        <p v-if="summary && phone && !open" class="summary muted phone-sum" data-test="deliverables-summary">{{ summary }}</p>
        <p v-if="error" class="error" data-test="deliverables-error">{{ error }}</p>
        <p v-if="notFound" class="note" role="status" data-test="deliverables-not-found">{{ notFound }}
          <button class="link" data-test="deliverables-not-found-dismiss" @click="notFound = null">Dismiss</button></p>
        <p v-if="answer?.truncated" class="muted small" data-test="deliverables-truncated">Showing every open deliverable and the newest {{ Limit }} others; filter to find an older one.</p>

        <div v-if="pickedRows.length && !(phone && open)" class="actions" role="toolbar" aria-label="Selected deliverables" data-test="deliverables-actions">
          <strong data-test="deliverables-picked-count">{{ pickedRows.length }} selected</strong>
          <button type="button" data-test="deliverables-assign-goal" @click="assigning = true"><Icon name="goals" :size="13" />Assign goal…</button>
          <span class="grow" />
          <button type="button" class="link" data-test="deliverables-picked-clear" @click="clearPicked">Clear selection</button>
        </div>

        <div class="split" :class="{ withItem: !!open }">
          <div v-show="!(phone && open)" ref="listEl" class="listwrap" data-test="deliverables-listwrap">
            <table class="list" :class="{ narrow: !!open }" data-test="deliverables-table">
              <thead>
                <tr>
                  <th v-if="!readOnly" class="pk"><input type="checkbox" :checked="allPicked" :indeterminate="pickedRows.length > 0 && !allPicked" :disabled="!shown.length"
                    aria-label="Select every deliverable shown" title="Select every deliverable shown" data-test="deliverables-pick-all" @change="pickAll" /></th>
                  <th class="sortable" :aria-sort="ariaSort('status')" data-test="deliverables-sort-status" @click="sortBy('status')">Status{{ arrow('status') }}</th>
                  <th class="sortable" :aria-sort="ariaSort('title')" data-test="deliverables-sort-title" @click="sortBy('title')">Deliverable{{ arrow('title') }}</th>
                  <th class="sortable" :aria-sort="ariaSort('item')" data-test="deliverables-sort-item" @click="sortBy('item')">Item{{ arrow('item') }}</th>
                  <th class="sortable tm" :aria-sort="ariaSort('team')" data-test="deliverables-sort-team" @click="sortBy('team')">Team{{ arrow('team') }}</th>
                  <th class="who">Who</th>
                  <th class="sortable" :aria-sort="ariaSort('elapsed')" data-test="deliverables-sort-elapsed" @click="sortBy('elapsed')">Elapsed{{ arrow('elapsed') }}</th>
                  <th class="sortable when" :aria-sort="ariaSort('last')" data-test="deliverables-sort-last" @click="sortBy('last')">Last activity{{ arrow('last') }}</th>
                  <th class="gl">Goal</th>
                </tr>
              </thead>
              <tbody>
                <tr v-for="w in shown" :key="w.id" :class="[statusLook(w).status, { sel: selected === w.id, cur: cursor === w.id, fresh: fresh.has(w.id), working: w.working, picked: picked.has(w.id) }]"
                  :aria-selected="selected === w.id" :data-wf="w.id" :data-status="w.status" data-test="deliverables-row" @click="select(w)">
                  <td v-if="!readOnly" class="pk" @click.stop><input type="checkbox" :checked="picked.has(w.id)" :aria-label="`Select deliverable #${w.id}`"
                    data-test="deliverables-pick" @click="pick(w, $event)" /></td>
                  <td class="st" :title="statusTip(w)">
                    <span class="st-in"><DeliverableStatusIcon :deliverable="w" /><span class="st-word" data-test="deliverables-status">{{ statusLook(w).label }}</span></span>
                  </td>
                  <td class="ttl">
                    <span class="wid mono">#<template v-for="(p, i) in highlight(String(w.id), filters.q)" :key="i"><mark v-if="p.hit">{{ p.text }}</mark><template v-else>{{ p.text }}</template></template></span>
                    <span class="wtitle" data-test="deliverables-title"><template v-for="(p, i) in highlight(w.title, filters.q)" :key="i"><mark v-if="p.hit" data-test="deliverables-hit">{{ p.text }}</mark><template v-else>{{ p.text }}</template></template></span>
                  </td>
                  <td class="itm"><ItemLink :item="w.item" :backlog="backlogReadable" :q="filters.q" /></td>
                  <td class="tm"><template v-for="(p, i) in highlight(w.team, filters.q)" :key="i"><mark v-if="p.hit">{{ p.text }}</mark><template v-else>{{ p.text }}</template></template></td>
                  <td class="who">
                    <span class="faces" data-test="deliverables-who">
                      <span v-for="f in faces(w).slice(0, 4)" :key="f.name" class="face" :title="f.says"><MemberAvatar :team="w.team" :member="f.name" :state="f.state" :size="20" /></span>
                      <span v-if="faces(w).length > 4" class="more muted small">+{{ faces(w).length - 4 }}</span>
                    </span>
                  </td>
                  <td class="el">
                    <span v-if="hasTime(w.time)" class="el-in">
                      <span class="el-top">
                        <OverallTime :time="w.time" :target="{ kind: 'deliverable', id: w.id, title: w.title, team: w.team }" />
                        <button type="button" class="tl-btn" title="Open its timeline" aria-label="Open its timeline" data-test="deliverables-timeline" @click.stop="timeline(w)">
                          <svg viewBox="0 0 16 16" aria-hidden="true"><path d="M2 4h7 M5 8h9 M3 12h6" /></svg>
                        </button>
                      </span>
                      <TimeBar :time="w.time" />
                    </span>
                    <span v-else class="muted">—</span>
                  </td>
                  <td class="when muted small" :title="w.lastAt">{{ ago(w.lastAt) }}</td>
                  <td class="gl" data-test="deliverables-goal"><GoalPicker :api="api" :deliverable="w.id" :goal="w.goal ?? null" :goals="goals ?? []" compact @changed="load" @goals-changed="loadGoals" /></td>
                </tr>
                <tr v-if="!answer && !error"><td :colspan="columns"><LoadingNote text="Loading the deliverables…" /></td></tr>
                <tr v-else-if="answer && !shown.length">
                  <td :colspan="columns" class="empty" data-test="deliverables-empty">
                    <template v-if="filtered && ((counts?.all ?? 0) > 0 || !!goalFilter)">
                      <p>Nothing matches these filters.</p>
                      <button data-test="deliverables-empty-clear" @click="clearFilters">Show all deliverables</button>
                    </template>
                    <template v-else>
                      <svg class="empty-art" viewBox="0 0 64 40" aria-hidden="true"><rect x="4" y="6" width="18" height="12" rx="3" /><rect x="42" y="22" width="18" height="12" rx="3" /><path d="M22 12h10v16h10" /></svg>
                      <p>{{ filters.chip === 'open' || filters.chip === 'needs-you' ? 'All quiet: nothing open right now.' : 'No deliverables yet.' }}</p>
                      <p class="muted small">A deliverable starts when you dispatch a backlog item to a team, or tell a Manager something.</p>
                      <button class="primary" data-test="deliverables-empty-backlog" @click="emit('backlog')">Go to the Backlog</button>
                    </template>
                  </td>
                </tr>
              </tbody>
            </table>
          </div>

          <DeliverablePane v-if="open" :key="open.id" :row="open" :api="api" :poll-ms="pollMs" :read-only="readOnly" :phone="phone"
            :goals="goals ?? []" @close="closePane" @card="openCard = $event" @open-team="emit('openTeam', $event)" @board="emit('board', $event)" @changed="goalChanged" />
        </div>
      </div>
    </section>
    <AssignGoalDialog v-if="assigning" :api="api" :deliverables="pickedRows" :goals="goals ?? []" @changed="goalChanged" @done="assigned" @close="assigning = false" />
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
.list tr[data-test=deliverables-row] { cursor: pointer; position: relative; }
.list tr[data-test=deliverables-row]:hover td { background: color-mix(in srgb, var(--ink) 3%, var(--surface)); }
.list tr.sel td { background: color-mix(in srgb, var(--accent) 9%, var(--surface)); box-shadow: inset 0 1px 0 color-mix(in srgb, var(--accent) 45%, transparent), inset 0 -1px 0 color-mix(in srgb, var(--accent) 45%, transparent); }
.list tr.sel td:first-child { box-shadow: inset 4px 0 0 var(--accent), inset 0 1px 0 color-mix(in srgb, var(--accent) 45%, transparent), inset 0 -1px 0 color-mix(in srgb, var(--accent) 45%, transparent); }
.list tr.cur:not(.sel) td:first-child { box-shadow: inset 3px 0 0 color-mix(in srgb, var(--accent) 50%, transparent); }
/* A deliverable that just appeared eases in. */
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
.list.narrow th.tm, .list.narrow td.tm, .list.narrow th.who, .list.narrow td.who, .list.narrow th.when, .list.narrow td.when,
.list.narrow th.gl, .list.narrow td.gl { display: none; }
.gl { white-space: nowrap; }
/* The checkbox column, and the bar of what can be done to the ticked rows. */
.pk { width: 22px; padding-right: 0; cursor: default; }
.pk input { margin: 0; cursor: pointer; vertical-align: middle; }
.list tr.picked:not(.sel) td { background: color-mix(in srgb, var(--accent) 5%, var(--surface)); }
.actions { display: flex; align-items: center; gap: 10px; margin: 0 0 8px; padding: 6px 10px; border: 1px solid var(--accent); border-radius: 8px; font-size: 13px;
  background: color-mix(in srgb, var(--accent) 7%, var(--surface)); }
.actions button:not(.link) { display: inline-flex; align-items: center; gap: 5px; font-size: 13px; }
.goal-chip { display: inline-flex; align-items: center; gap: 5px; border: 1px solid var(--accent); border-radius: 14px; padding: 2px 4px 2px 9px; font-size: 12.5px;
  color: var(--accent); background: color-mix(in srgb, var(--accent) 8%, transparent); max-width: 100%; min-width: 0; }
.chip-x { border: 0; background: transparent; color: inherit; padding: 0 5px; font-size: 15px; line-height: 1; cursor: pointer; border-radius: 10px; }
.chip-x:hover { background: color-mix(in srgb, var(--accent) 16%, transparent); }
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
/* A phone: the rows wrap (no table columns), an opened deliverable takes the page (Back returns to the list). */
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
  .list tr[data-test=deliverables-row] { display: flex; flex-wrap: wrap; align-items: center; gap: 4px 10px; padding: 9px 6px; border-bottom: 1px solid var(--line-soft); }
  .list tr[data-test=deliverables-row] td { display: block; padding: 0; border: 0; background: transparent !important; box-shadow: none !important; }
  .list tr.sel { background: color-mix(in srgb, var(--accent) 9%, var(--surface)); box-shadow: inset 4px 0 0 var(--accent); }
  .list td.pk { width: 44px; height: 44px; display: grid !important; place-items: center; order: 0; }
  .list td.pk input { width: 20px; height: 20px; }
  .actions { flex-wrap: wrap; }
  .actions button { min-height: 44px; box-sizing: border-box; }
  .list td.st { flex: 1; }
  .list td.ttl { order: 5; flex: 1 1 100%; min-width: 0; overflow-wrap: anywhere; }
  .list td.itm, .list td.tm, .list td.when { order: 6; }
  .list td.who { order: 3; }
  .list td.el { order: 7; flex: 1 1 100%; }
  .list.narrow td.gl, .list td.gl { display: block; order: 8; flex: 1 1 100%; }
  .goal-chip { flex: none; min-height: 44px; box-sizing: border-box; }
  .chip-x { min-width: 36px; min-height: 36px; }
  .el-in { flex-direction: row; align-items: center; gap: 10px; }
  .tl-btn { width: 44px; height: 44px; }
}
</style>
