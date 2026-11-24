<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue';
import type { BoardCard, BoardList, ConnectionAttention, YawbleApi } from '../../../shared/api';
import { errorText, resolveApi } from '../api';
import { displayName } from '../faces/faces';
import BoardCardItem from './BoardCardItem.vue';
import CardPanel from './CardPanel.vue';
import { inLane } from './cards';
import LoadingNote from '../components/LoadingNote.vue';
import BusyLabel from '../components/BusyLabel.vue';
import NetworkAsks from '../components/NetworkAsks.vue';
import ToolAsks from '../components/ToolAsks.vue';
import FolderAsks from '../components/FolderAsks.vue';
import WorkflowExpiryAsks from '../components/WorkflowExpiryAsks.vue';
import RunLimitDialog from '../components/RunLimitDialog.vue';
import { useBusy } from '../busy';
import { useNarrow, useSwipe } from '../narrowScreen';
import { openWorkflowsPage } from '../workflows/page';

/**
 * THE BOARD: every team's cards in four lanes - To Do, In Progress, Needs You, Done - newest change first in each.
 * A card is one member's piece of a workflow (or a piece the Manager planned and has not given out yet). Filters by
 * team and member, a search box over what is shown, the backlog, and a card's panel on the right.
 * Done's Clear hides exactly the Done cards shown from the board; nothing is deleted, "Show cleared"
 * lists them dimmed with Restore, and a cleared card moved out of Done shows again by itself.
 */
const props = defineProps<{ api?: YawbleApi; pollMs?: number; teamId?: number | null }>();
const emit = defineEmits<{ openTeam: [teamId: number]; backlog: []; connections: [] }>();
const api = resolveApi(props.api);

const board = ref<BoardList | null>(null);
const error = ref<string | null>(null);
const team = ref<string>('');
const member = ref<string>('');
const search = ref('');
const openCard = ref<number | null>(null);
const panel = ref<InstanceType<typeof CardPanel> | null>(null);
let timer: ReturnType<typeof setInterval> | null = null;

const members = computed(() => {
  const set = new Set<string>();
  for (const c of board.value?.cards ?? []) if (c.member && (!team.value || c.team === team.value)) set.add(c.member);
  return [...set].sort();
});
function matches(c: BoardCard): boolean {
  const q = search.value.trim().toLowerCase();
  return (!team.value || c.team === team.value) && (!member.value || c.member === member.value)
    && (!q || `${c.title} ${c.body} ${c.item ?? ''} ${c.member ?? ''} ${c.team}`.toLowerCase().includes(q));
}
const shown = computed(() => (board.value?.cards ?? []).filter(matches));

/**
 * Cleared Done cards (hidden from the board, not deleted). They come in the same board.list call as the rest
 * (includeCleared), so the count, the list and the board always agree and the core decorates the cards once. The
 * count is of the cleared cards the filters let through: exactly what Show cleared lists.
 */
const showCleared = ref(false);
const cleared = ref<BoardCard[]>([]);
const shownCleared = computed(() => inLane(cleared.value.filter(matches), 'done'));
const shownDone = computed(() => inLane(shown.value, 'done'));
const clearTitle = computed(() => `Hide the ${shownDone.value.length} card${shownDone.value.length === 1 ? '' : 's'} shown in Done from the board; nothing is deleted`);
function toggleCleared() { showCleared.value = !showCleared.value; }

// Clear (Done) and Restore await the core: held with a spinner until it answers.
const work = useBusy();
/** After an action: the core's board.changed reloads the view; without events (an older bridge) reload here. */
function afterAction() { if (!api.onEvent) void load(); }
const clearDone = () => work.run('clear', doClearDone);
async function doClearDone() {
  // Exactly the Done cards on screen: never ones the filters hide, nor ones that arrived since the last load.
  const ids = shownDone.value.map((c) => c.id);
  if (!ids.length) return;
  try {
    await api.call<{ cleared: number }>('board.clearDone', { ids });
    afterAction();
  } catch (e) { error.value = errorText(e); }
}
const restore = (id: number) => work.run(`restore-${id}`, () => doRestore(id));
async function doRestore(id: number) {
  try {
    await api.call<{ restored: boolean }>('board.restore', { id });
    afterAction();
  } catch (e) { error.value = errorText(e); }
}
const filtered = computed(() => !!(team.value || member.value || search.value.trim()));

/** One board.list at a time: a call asked for while one runs (the poll, an event) becomes one more call after it. */
let loading: Promise<void> | null = null;
let again = false;
function load(): Promise<void> {
  if (loading) { again = true; return loading; }
  loading = (async () => {
    do {
      again = false;
      try {
        const list = await api.call<BoardList>('board.list', { includeCleared: true });
        board.value = { ...list, cards: list.cards.filter((c) => !c.clearedAt) };
        cleared.value = list.cards.filter((c) => !!c.clearedAt);
        error.value = null;
        if (props.teamId != null && team.value === '' && !loadedOnce) team.value = board.value.teams.find((t) => t.id === props.teamId)?.name ?? '';
        loadedOnce = true;
      } catch (e) { error.value = errorText(e); }
    } while (again);
  })().finally(() => { loading = null; });
  return loading;
}
let loadedOnce = false;

/** Connections a service refused: they need the person, so they sit in Needs You (on the computer only; a remote view has none). */
const reconnect = ref<ConnectionAttention[]>([]);
async function loadReconnect() {
  if (api.remote) return;
  try { reconnect.value = await api.call<ConnectionAttention[]>('integrations.connectionAttention', {}); }
  catch { reconnect.value = []; } // an older core: nothing to show
}
let off: (() => void) | null = null;
/** Network asks shown in Needs You (NetworkAsks loads and answers them; the lane's count adds them). */
const networkAsks = ref(0);
/** Tool asks shown in Needs You (ToolAsks loads and answers them; the lane's count adds them). */
const toolAsks = ref(0);
/** Folder asks shown in Needs You (FolderAsks loads and answers them; the lane's count adds them). */
const folderAsks = ref(0);
/** Workflows that wait on the person and will expire within a day, shown in Needs You with Extend. */
const expiryAsks = ref(0);

function clear() { team.value = ''; member.value = ''; search.value = ''; }
/** The Workflows page, filtered to the team the board shows (all teams when it shows every team). */
function openWorkflows() { openWorkflowsPage({ teamId: board.value?.teams.find((t) => t.name === team.value)?.id ?? null }); }

/** The global run limit, shown with In Progress's count ("3 of 8"); its number opens a small dialog to change it. */
const runLimit = ref<number | null>(null);
const editLimit = ref(false);
async function loadLimit() {
  try { runLimit.value = (await api.call<{ slots?: { limit?: number } }>('app.overview'))?.slots?.limit ?? runLimit.value; }
  catch { /* keeps what it showed */ }
}

/** A lane's count: its cards shown, and in Needs You what else needs the person. */
const laneCount = (id: string) => inLane(shown.value, id).length + (id === 'blocked' ? reconnect.value.length + networkAsks.value + toolAsks.value + folderAsks.value + expiryAsks.value : 0);

// A phone: one lane at a time, chosen with the lane tabs (each with its count) or by swiping left or right.
// The other lanes stay mounted (hidden), so Needs You's asks keep counting.
const phone = useNarrow();
const lane = ref<string>('todo');
const laneIds = computed(() => board.value?.lanes.map((l) => l.id as string) ?? []);
const activeLane = computed(() => (laneIds.value.includes(lane.value) ? lane.value : laneIds.value[0] ?? 'todo'));
function stepLane(direction: -1 | 1) {
  const ids = laneIds.value;
  const at = ids.indexOf(activeLane.value);
  const to = at - direction; // a swipe to the left (-1) shows the next lane
  if (at >= 0 && to >= 0 && to < ids.length) lane.value = ids[to]!;
}
const swipe = useSwipe(stepLane);

onMounted(() => {
  void load();
  void loadReconnect();
  void loadLimit();
  off = api.onEvent?.((e) => {
    if (e.event === 'connections.changed') void loadReconnect();
    else if (e.event === 'board.changed') void load(); // cleared or restored (here or in another view)
  }) ?? null;
  timer = setInterval(() => { void load(); void loadReconnect(); void loadLimit(); }, props.pollMs ?? 2000);
});
onBeforeUnmount(() => { if (timer) clearInterval(timer); off?.(); });
</script>

<template>
  <div class="board-place" :class="{ phone, 'card-open': openCard !== null }" data-test="board">
    <div class="board-main">
      <h2 class="page-title">Kanban board</h2>
      <div class="filters">
        <select v-model="team" data-test="board-team" @change="member = ''"><option value="">All teams</option><option v-for="t in board?.teams ?? []" :key="t.id" :value="t.name">{{ t.name }}</option></select>
        <select v-model="member" data-test="board-member"><option value="">Everyone</option><option v-for="m in members" :key="m" :value="m">{{ displayName(m) }}</option></select>
        <input v-model="search" type="search" placeholder="Search cards" data-test="board-search" />
        <button v-if="filtered" class="link" data-test="board-clear" @click="clear">Clear</button>
        <span class="grow" />
        <span class="muted small">{{ shown.length }} card{{ shown.length === 1 ? '' : 's' }}</span>
        <button data-test="board-backlog" @click="emit('backlog')">Backlog</button>
        <button data-test="board-workflows" :title="team ? `${team}'s workflows` : 'Every team\'s workflows'" @click="openWorkflows">Workflows</button>
      </div>
      <p v-if="error" class="error" data-test="board-error">{{ error }}</p>
      <LoadingNote v-if="!board && !error" text="Loading the board…" />
      <nav v-if="board && phone" class="lane-tabs" role="tablist" aria-label="Lanes" data-test="lane-tabs">
        <button v-for="l in board.lanes" :key="l.id" type="button" role="tab" class="lane-tab" :class="[`lane-${l.id}`, { on: activeLane === l.id }]"
          :aria-selected="activeLane === l.id" :data-test="`lane-tab-${l.id}`" @click="lane = l.id">
          <span class="lname">{{ l.label }}</span><span class="count" data-test="lane-tab-count">{{ laneCount(l.id) }}</span>
        </button>
      </nav>
      <div v-if="board" class="lanes" data-test="board-lanes" @pointerdown="phone && swipe.down($event)" @pointerup="phone && swipe.up($event)" @pointercancel="swipe.cancel()">
        <section v-for="l in board.lanes" v-show="!phone || activeLane === l.id" :key="l.id" class="lane" :class="`lane-${l.id}`" :data-test="`lane-${l.id}`">
          <header><span class="lname">{{ l.label }}</span>
            <span v-if="l.id === 'in-progress' && runLimit !== null" class="count" data-test="lane-in-progress-count"
              :title="`${laneCount(l.id)} in progress; the global run limit is ${runLimit}`">{{ laneCount(l.id) }} of <button type="button" class="link limit-link"
                data-test="run-limit-link" title="Change the global run limit" @click="editLimit = true">{{ runLimit }}</button></span>
            <span v-else class="count">{{ laneCount(l.id) }}</span>
            <button v-if="l.id === 'done' && shownDone.length" class="link small clear-done" data-test="board-clear-done"
              :title="clearTitle" :disabled="work.is('clear')" :aria-busy="work.is('clear')" @click="clearDone"><BusyLabel :busy="work.is('clear')" text="Clearing…">Clear</BusyLabel></button></header>
          <div v-if="l.id === 'blocked' && reconnect.length" class="cards reconnect">
            <button v-for="c in reconnect" :key="c.id" class="reconnect-card" :data-test="`needs-reconnect-${c.id}`" @click="emit('connections')">
              <b>The connection {{ c.name }} needs reconnecting</b>
              <span v-if="c.reason" class="small muted">{{ c.reason }}</span>
              <span class="small">{{ c.where }}</span>
            </button>
          </div>
          <NetworkAsks v-if="l.id === 'blocked'" class="cards reconnect" :api="api" :poll-ms="pollMs" :team="team" :member="member" @count="networkAsks = $event" />
          <ToolAsks v-if="l.id === 'blocked'" class="cards reconnect" :api="api" :poll-ms="pollMs" :team="team" :member="member" @count="toolAsks = $event" />
          <FolderAsks v-if="l.id === 'blocked'" class="cards reconnect" :api="api" :poll-ms="pollMs" :team="team" :member="member" @count="folderAsks = $event" />
          <WorkflowExpiryAsks v-if="l.id === 'blocked'" class="cards reconnect" :api="api" :cards="board.cards" :team="team" @count="expiryAsks = $event" />
          <TransitionGroup tag="div" name="card" class="cards">
            <BoardCardItem v-for="c in inLane(shown, l.id)" :key="c.id" :card="c" :selected="openCard === c.id" @open="openCard = c.id" />
          </TransitionGroup>
          <p v-if="!inLane(shown, l.id).length && !(l.id === 'blocked' && (reconnect.length || networkAsks || toolAsks || folderAsks || expiryAsks))" class="none">{{ l.id === 'blocked' ? 'Nothing needs you.' : 'No cards.' }}</p>
          <template v-if="l.id === 'done' && shownCleared.length > 0">
            <button class="link small show-cleared" data-test="board-show-cleared" @click="toggleCleared">
              {{ showCleared ? `Hide cleared (${shownCleared.length})` : `Show cleared (${shownCleared.length})` }}</button>
            <div v-if="showCleared" class="cards cleared" data-test="board-cleared">
              <div v-for="c in shownCleared" :key="c.id" class="cleared-card" :data-test="`cleared-card-${c.id}`">
                <BoardCardItem :card="c" :selected="openCard === c.id" @open="openCard = c.id" />
                <button class="link small" :data-test="`board-restore-${c.id}`" title="Show this card on the board again"
                  :disabled="work.is(`restore-${c.id}`)" :aria-busy="work.is(`restore-${c.id}`)" @click="restore(c.id)"><BusyLabel :busy="work.is(`restore-${c.id}`)" text="Restoring…">Restore</BusyLabel></button>
              </div>
            </div>
          </template>
        </section>
      </div>
      <p v-if="board && !board.cards.length" class="hint muted" data-test="board-empty">
        Cards appear here as your teams work: one per member per piece of work. Put work in the Backlog and dispatch it to a
        team, or tell a team's Manager what you want, and its Manager plans the cards.
      </p>
    </div>
    <RunLimitDialog v-if="editLimit" :api="api" @close="editLimit = false" @changed="runLimit = $event" />
    <CardPanel v-if="openCard !== null && board" ref="panel" :id="openCard" :lanes="board.lanes" :api="api"
      @close="openCard = null" @changed="load" @open-team="emit('openTeam', $event)" />
  </div>
</template>

<style scoped>
.board-place { display: flex; height: 100%; min-height: 0; }
.board-main { flex: 1; min-width: 0; display: flex; flex-direction: column; padding: 4px 20px 16px; overflow: hidden; }
.filters { display: flex; gap: 8px; align-items: center; padding: 6px 0 12px; flex-wrap: wrap; }
.filters select, .filters input { font: inherit; font-size: 13px; padding: 5px 8px; border: 1px solid var(--line); border-radius: 8px; background: var(--surface); }
.filters input { width: 220px; }
.link { border: 0; background: transparent; color: var(--accent); }
.grow { flex: 1; }
.lanes { flex: 1; min-height: 0; display: grid; grid-template-columns: repeat(4, minmax(170px, 1fr)); gap: 12px; overflow-x: auto; }
.lane { display: flex; flex-direction: column; min-height: 0; background: var(--side); border: 1px solid var(--line-soft); border-radius: 12px; }
.lane header { display: flex; align-items: center; gap: 8px; padding: 10px 12px 8px; }
.lname { font-weight: 700; font-size: 13px; }
.limit-link { font-size: inherit; padding: 0; min-height: 0; text-decoration: underline; }
.count { font-size: 12px; color: var(--muted); background: var(--surface); border: 1px solid var(--line); border-radius: 10px; padding: 0 7px; }
.lane-blocked .lname { color: var(--warn-edge); }
.cards { display: flex; flex-direction: column; gap: 8px; padding: 0 10px 10px; overflow-y: auto; }
.none { color: var(--muted); font-size: 12.5px; padding: 0 12px 12px; margin: 0; }
.clear-done { margin-left: auto; padding: 0 2px; color: var(--muted); }
.clear-done:hover { color: var(--accent); }
.show-cleared { align-self: flex-start; padding: 0 12px 10px; color: var(--muted); }
.show-cleared:hover { color: var(--accent); }
.cleared-card { display: flex; flex-direction: column; align-items: flex-end; gap: 2px; }
.cleared-card > :first-child { opacity: .55; align-self: stretch; }
.cleared-card > :first-child:hover { opacity: .85; }
.reconnect { overflow: visible; }
.reconnect-card { display: flex; flex-direction: column; gap: 3px; text-align: left; font: inherit; font-size: 12.5px; padding: 8px 10px; border: 1px solid var(--warn-edge-soft); border-left: 3px solid var(--warn-edge); border-radius: 10px; background: var(--surface); cursor: pointer; }
.hint { max-width: 640px; font-size: 13px; }
.muted { color: var(--muted); }
.small { font-size: 12px; }
.error { color: var(--bad); }
.card-move { transition: transform .35s ease; }
.card-enter-from, .card-leave-to { opacity: 0; transform: translateY(6px); }
.card-enter-active, .card-leave-active { transition: all .25s ease; }
.card-leave-active { position: absolute; }
@media (prefers-reduced-motion: reduce) { .card-move, .card-enter-active, .card-leave-active { transition: none; } }
.page-title { margin: 0 0 8px; font-size: 22px; }
.lane-tabs { display: none; }
/* A phone: one column, one lane at a time under the lane tabs (swiped left or right), the card's panel over
   the whole board, tap targets 44 px. */
@media (max-width: 759.98px) {
  .board-main { padding: 4px 10px 10px; }
  .filters { gap: 6px; }
  .filters select, .filters input { min-height: 44px; box-sizing: border-box; }
  .filters input { width: auto; flex: 1 1 100%; order: -1; }
  .filters button { min-height: 44px; }
  .lane-tabs { display: flex; gap: 4px; overflow-x: auto; padding: 0 0 8px; flex: none; }
  .lane-tab { flex: 1 0 auto; display: inline-flex; align-items: center; justify-content: center; gap: 6px; min-height: 44px; padding: 0 10px;
    border: 1px solid var(--line); border-radius: 10px; background: var(--surface); font-size: 13px; }
  .lane-tab.on { border-color: var(--accent); background: color-mix(in srgb, var(--accent) 10%, var(--surface)); }
  .lane-tab.lane-blocked .lname { color: var(--warn-edge); }
  .lanes { display: flex; flex-direction: column; overflow: hidden; touch-action: pan-y; }
  .lane { flex: 1 1 0; }
  .lane header { display: none; }
  .lane-done header { display: flex; justify-content: flex-end; padding: 6px 12px 0; }
  .lane-done header .lname, .lane-done header .count { display: none; }
  .cards { padding-top: 10px; }
  .clear-done, .show-cleared, .cleared-card .link { min-height: 44px; }
  .board-place.card-open > .board-main { display: none; }
}
</style>
