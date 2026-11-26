<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue';
import { DELIVERABLES_LIST, type DeliverableRow, type DeliverablesListAnswer, type Goal, type GoalsListAnswer, type GoalTotals, type YawbleApi } from '../../../shared/api';
import { errorText, resolveApi } from '../api';
import LoadingNote from '../components/LoadingNote.vue';
import Icon from '../components/Icon.vue';
import { useNarrow } from '../narrowScreen';
import { vChange } from '../remoteAccess';
import OverallTime from '../timeline/OverallTime.vue';
import { formatDuration, hasTime, useNow } from '../timeline/time';
import DeliverableStatusIcon from '../deliverables/DeliverableStatusIcon.vue';
import { openDeliverablesPage, statusLook, type GoalFilter } from '../deliverables/page';
import GoalDialog from './GoalDialog.vue';
import { countsLine, goalsApi, liveElapsedMs } from './goals';

/**
 * THE GOALS PAGE - the person's goals, each with the deliverables linked to it, and the deliverables with no goal
 * (Unassigned) last. A goal's elapsed time is the core's: the calendar time its deliverables cover, overlaps counted
 * once; while one is open it counts on here. Each goal folds down (closed at first) to list its deliverables, read with
 * deliverables.list filtered to the goal; a deliverable opens on the Deliverables page. New goal and Edit open the goal
 * dialog (name, description; Delete asks first). No tokens or cost anywhere.
 */
const props = defineProps<{ api?: YawbleApi; pollMs?: number }>();
const api = resolveApi(props.api);
const calls = goalsApi(api);
const phone = useNarrow();
const now = useNow();

const answer = ref<GoalsListAnswer | null>(null);
const error = ref<string | null>(null);
async function load() {
  try { answer.value = await calls.list(); error.value = null; }
  catch (e) { error.value = errorText(e); }
}
let timer: ReturnType<typeof setInterval> | null = null;
onMounted(() => { void load(); timer = setInterval(() => { void load(); void reloadOpen(); }, Math.max(3000, props.pollMs ?? 5000)); });
onBeforeUnmount(() => { if (timer) clearInterval(timer); });

const goals = computed(() => answer.value?.goals ?? []);
const unassigned = computed<GoalTotals | null>(() => answer.value?.unassigned ?? null);
const elapsed = (t: GoalTotals) => formatDuration(liveElapsedMs(t.time, answer.value?.now, now.value));

// ---- the fold-downs: closed at first; an open one lists its deliverables ----
type Key = number | 'none';
const open = ref(new Set<Key>());
const rows = ref(new Map<Key, DeliverableRow[]>());
const rowsError = ref(new Map<Key, string>());
/** The fold-downs whose list was cut (the newest RowsLimit, plus every open one): they say so and link to the full list. */
const cut = ref(new Set<Key>());
const RowsLimit = 500;
async function loadRows(key: Key) {
  try {
    const a = await api.call<DeliverablesListAnswer>(DELIVERABLES_LIST, { goal: key, state: 'all', limit: RowsLimit });
    rows.value = new Map(rows.value).set(key, a?.deliverables ?? []);
    const c = new Set(cut.value); if (a?.truncated) c.add(key); else c.delete(key); cut.value = c;
    const e = new Map(rowsError.value); e.delete(key); rowsError.value = e;
  } catch (e) { rowsError.value = new Map(rowsError.value).set(key, errorText(e)); }
}
async function reloadOpen() { await Promise.all([...open.value].map(loadRows)); }
function toggle(key: Key) {
  const s = new Set(open.value);
  if (s.has(key)) s.delete(key);
  else { s.add(key); void loadRows(key); }
  open.value = s;
}
const keyOf = (k: Key) => (k === 'none' ? 'none' : String(k));

// ---- the dialog ----
const editing = ref<Goal | null>(null);
const creating = ref(false);
function closeDialog() { editing.value = null; creating.value = false; }
async function saved() { closeDialog(); await load(); await reloadOpen(); }
async function deleted(id: number) {
  closeDialog();
  const s = new Set(open.value); s.delete(id); open.value = s;
  await load();
  await reloadOpen();
}

// ---- to the Deliverables page ----
const openDeliverable = (w: DeliverableRow) => openDeliverablesPage({ deliverable: w.id });
const showAll = (goal: GoalFilter) => openDeliverablesPage({ goal });
</script>

<template>
  <div class="goals-place" :class="{ phone }" data-test="goals-view">
    <div class="wrap">
      <header class="head">
        <h2 v-if="!phone">Goals</h2>
        <p class="muted small lead">Link deliverables to a goal from the Deliverables page. Each deliverable has at most one goal.</p>
        <span class="grow" />
        <button v-change class="primary" data-test="goals-new" @click="creating = true"><Icon name="plus" :size="15" /> New goal</button>
      </header>
      <p v-if="error" class="error" data-test="goals-error">{{ error }}</p>
      <LoadingNote v-if="!answer && !error" text="Loading the goals…" />

      <template v-if="answer">
        <p v-if="!goals.length" class="empty" data-test="goals-empty">No goals yet. A goal gathers the deliverables that work towards it, and adds up the time they took.</p>
        <ul class="list" data-test="goals-list">
          <li v-for="g in goals" :key="g.id" class="goal" :data-id="g.id" data-test="goal-row">
            <div class="ghead">
              <button type="button" class="fold" :aria-expanded="open.has(g.id)" :aria-controls="`goal-${g.id}-list`" :title="open.has(g.id) ? 'Hide its deliverables' : 'Show its deliverables'"
                data-test="goal-fold" @click="toggle(g.id)">
                <span class="chev" :class="{ on: open.has(g.id) }" aria-hidden="true">▸</span>
                <span class="gname" data-test="goal-name">{{ g.name }}</span>
              </button>
              <span class="gcounts muted" data-test="goal-counts">{{ countsLine(g.total, g.open) }}</span>
              <span class="gtime" :title="`Calendar time its deliverables cover${g.time.endAt === null && g.time.firstAt ? ', still counting' : ''}`" data-test="goal-elapsed">
                <Icon name="clock" :size="13" /> {{ elapsed(g) }}
              </span>
              <button v-change class="tool" title="Edit the goal" :aria-label="`Edit ${g.name}`" data-test="goal-edit" @click="editing = g"><Icon name="edit" :size="15" /></button>
            </div>
            <p v-if="g.description" class="gdesc" data-test="goal-description">{{ g.description }}</p>
            <div v-if="open.has(g.id)" :id="`goal-${g.id}-list`" class="fold-body" data-test="goal-deliverables">
              <p v-if="rowsError.get(g.id)" class="error small">{{ rowsError.get(g.id) }}</p>
              <LoadingNote v-else-if="!rows.get(g.id)" text="Loading its deliverables…" />
              <p v-else-if="!rows.get(g.id)!.length" class="muted small" data-test="goal-deliverables-empty">No deliverables linked yet.</p>
              <button v-for="w in rows.get(g.id) ?? []" :key="w.id" type="button" class="drow" :data-id="w.id" data-test="goal-deliverable" @click="openDeliverable(w)">
                <DeliverableStatusIcon :deliverable="w" :size="14" />
                <span class="dstatus small" data-test="goal-deliverable-status">{{ statusLook(w).label }}</span>
                <span class="dtitle"><span class="mono muted">#{{ w.id }}</span> {{ w.title }}</span>
                <span class="dteam muted small">{{ w.team }}</span>
                <OverallTime v-if="hasTime(w.time)" :time="w.time" :target="{ kind: 'deliverable', id: w.id, title: w.title, team: w.team }" />
              </button>
              <p v-if="cut.has(g.id)" class="muted small" data-test="goal-deliverables-cut">Showing the newest {{ (rows.get(g.id) ?? []).length }}; open the Deliverables page for all.</p>
              <button v-if="(rows.get(g.id) ?? []).length" class="link small" data-test="goal-show-all" @click="showAll({ id: g.id, name: g.name })">Show on the Deliverables page</button>
            </div>
          </li>
          <li v-if="unassigned" class="goal unassigned" data-test="goal-unassigned">
            <div class="ghead">
              <button type="button" class="fold" :aria-expanded="open.has('none')" :aria-controls="`goal-${keyOf('none')}-list`" data-test="goal-fold" @click="toggle('none')">
                <span class="chev" :class="{ on: open.has('none') }" aria-hidden="true">▸</span>
                <span class="gname">Unassigned</span>
              </button>
              <span class="gcounts muted" data-test="goal-counts">{{ countsLine(unassigned.total, unassigned.open) }}</span>
              <span class="gtime" data-test="goal-elapsed"><Icon name="clock" :size="13" /> {{ elapsed(unassigned) }}</span>
              <span class="tool-gap" />
            </div>
            <p class="gdesc muted small">Deliverables with no goal.</p>
            <div v-if="open.has('none')" :id="`goal-${keyOf('none')}-list`" class="fold-body" data-test="goal-deliverables">
              <p v-if="rowsError.get('none')" class="error small">{{ rowsError.get('none') }}</p>
              <LoadingNote v-else-if="!rows.get('none')" text="Loading the deliverables…" />
              <p v-else-if="!rows.get('none')!.length" class="muted small" data-test="goal-deliverables-empty">Every deliverable has a goal.</p>
              <button v-for="w in rows.get('none') ?? []" :key="w.id" type="button" class="drow" :data-id="w.id" data-test="goal-deliverable" @click="openDeliverable(w)">
                <DeliverableStatusIcon :deliverable="w" :size="14" />
                <span class="dstatus small" data-test="goal-deliverable-status">{{ statusLook(w).label }}</span>
                <span class="dtitle"><span class="mono muted">#{{ w.id }}</span> {{ w.title }}</span>
                <span class="dteam muted small">{{ w.team }}</span>
                <OverallTime v-if="hasTime(w.time)" :time="w.time" :target="{ kind: 'deliverable', id: w.id, title: w.title, team: w.team }" />
              </button>
              <p v-if="cut.has('none')" class="muted small" data-test="goal-deliverables-cut">Showing the newest {{ (rows.get('none') ?? []).length }}; open the Deliverables page for all.</p>
              <button v-if="(rows.get('none') ?? []).length" class="link small" data-test="goal-show-all" @click="showAll({ id: 'none', name: 'Unassigned' })">Show on the Deliverables page</button>
            </div>
          </li>
        </ul>
      </template>
    </div>
    <GoalDialog v-if="creating || editing" :api="api" :goal="editing" :goals="goals" @saved="saved" @deleted="deleted" @close="closeDialog" />
  </div>
</template>

<style scoped>
.goals-place { height: 100%; overflow: auto; padding: 4px 24px 24px; box-sizing: border-box; }
.wrap { max-width: 980px; margin: 12px auto 0; }
.head { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; margin-bottom: 12px; }
.head h2 { margin: 0; font-size: 22px; }
.lead { margin: 0; }
.grow { flex: 1; }
.primary { background: var(--accent); color: var(--on-accent); border-color: var(--accent); display: inline-flex; align-items: center; gap: 6px; }
.muted { color: var(--muted); }
.small { font-size: 12px; }
.mono { font-family: ui-monospace, Consolas, monospace; font-size: 12px; }
.error { color: var(--bad); }
.empty { color: var(--ink-soft); padding: 16px 0; }
.list { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 8px; }
.goal { border: 1px solid var(--line); border-radius: 8px; padding: 8px 10px; background: var(--surface); min-width: 0; }
.goal.unassigned { border-style: dashed; }
.ghead { display: flex; align-items: center; gap: 10px; min-width: 0; }
.fold { display: inline-flex; align-items: center; gap: 6px; border: 0; background: transparent; padding: 2px 0; font: inherit; color: var(--ink); cursor: pointer; min-width: 0; flex: 1 1 auto; text-align: left; }
.chev { display: inline-block; transition: transform .12s; color: var(--muted); }
.chev.on { transform: rotate(90deg); }
@media (prefers-reduced-motion: reduce) { .chev { transition: none; } }
.gname { font-weight: 600; font-size: 14px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.gcounts { font-size: 12.5px; white-space: nowrap; }
.gtime { display: inline-flex; align-items: center; gap: 4px; font-size: 12.5px; color: var(--ink-soft); font-variant-numeric: tabular-nums; white-space: nowrap; }
.tool { border: 0; background: transparent; color: var(--muted); padding: 4px; border-radius: 6px; display: inline-grid; place-items: center; cursor: pointer; }
.tool:hover { color: var(--ink); background: var(--hover-strong); }
.tool-gap { width: 23px; flex: none; }
.gdesc { margin: 4px 0 0 20px; font-size: 13px; color: var(--ink-soft); white-space: pre-wrap; overflow-wrap: anywhere; }
.fold-body { margin: 8px 0 2px 20px; display: flex; flex-direction: column; gap: 2px; }
.drow { display: flex; align-items: center; gap: 8px; border: 0; border-radius: 6px; background: transparent; padding: 5px 6px; font: inherit; font-size: 13px; color: var(--ink); text-align: left; cursor: pointer; min-width: 0; }
.drow:hover { background: color-mix(in srgb, var(--ink) 4%, var(--surface)); }
.dstatus { width: 74px; flex: none; color: var(--ink-soft); }
.dtitle { flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.dteam { white-space: nowrap; max-width: 160px; overflow: hidden; text-overflow: ellipsis; }
.link { border: 0; background: transparent; color: var(--accent); padding: 4px 0; cursor: pointer; align-self: flex-start; }
/* A phone: the row wraps (name on its own line), every control a 44 px target, nothing wider than the screen. */
@media (max-width: 759.98px) {
  .goals-place { padding: 4px 10px 16px; }
  .wrap { margin-top: 4px; }
  .head .primary { min-height: 44px; flex: 1 1 100%; justify-content: center; }
  .ghead { flex-wrap: wrap; gap: 4px 10px; }
  .fold { flex: 1 1 calc(100% - 54px); min-height: 44px; }
  .tool { width: 44px; height: 44px; }
  .tool-gap { display: none; }
  .gname { white-space: normal; overflow-wrap: anywhere; }
  .gdesc, .fold-body { margin-left: 0; }
  .drow { flex-wrap: wrap; min-height: 44px; }
  .dtitle { flex: 1 1 100%; white-space: normal; overflow-wrap: anywhere; order: 5; }
  .link { min-height: 44px; }
}
</style>
