<script setup lang="ts">
import { computed, onBeforeUpdate, onMounted, onUnmounted, onUpdated, ref, watch, watchPostEffect } from 'vue';
import type { KanbanLane } from '../api/kanban';
import { useKanbanStore, type KanbanView } from '../stores/kanban';
import { runningAcrossTeams, useWipStore } from '../stores/wip';
import { useAgentUpdatesStore } from '../stores/agentUpdates';
import { boardCountText, cardShowsWaiting, laneOverLimit, NeedsYouLaneId } from '../lib/kanban';
import { isRunningLane } from '../lib/tenantSettings';
import KanbanCard from './KanbanCard.vue';
import KanbanFilterBar from './KanbanFilterBar.vue';
import KanbanCardPanel from './KanbanCardPanel.vue';
import KanbanChangeOutcome from './KanbanChangeOutcome.vue';
import KanbanProposedOutcomes from './KanbanProposedOutcomes.vue';
import OutcomesDialog from './OutcomesDialog.vue';
import { FlipDurationMs, flipShifts, flipTransform, type CardBox } from '../lib/flip';
import { clampLaneWidth, DefaultLaneWidth, LaneWidthStep, loadLaneWidth, saveLaneWidth } from '../lib/kanbanLaneWidth';

/**
 * The board: swim lanes from the active template, cards from the projection.
 *
 * THE LANES COME FROM THE PAYLOAD, never from a list in here. The template decides what the lanes
 * are and the server decides which lane a card is in - a component with its own column list would
 * be a second answer to both, and would disagree the moment a tenant picked a different template.
 *
 * THE ANIMATION IS FLIP, and the reason it is FLIP rather than a CSS transition on the list is
 * that a card moving lane is REMOVED from one column's DOM and INSERTED into another's. There is
 * no property to transition across that, so the card is measured before the re-render and again
 * after, then started at its old position with a transform that eases back to none. The arithmetic
 * lives in `lib/flip.ts` where it can be tested; what stays here is the measuring and the two style
 * writes, which need a DOM this test stack does not have.
 */
const kanban = useKanbanStore();
const wip = useWipStore();

/**
 * OUTCOMES ARE SHOWN ELSEWHERE, so the board passes both of its outcome events up: a card's tag
 * (`open-outcome`, with the outcome's id) and the filter bar's Manage outcomes button.
 */
defineEmits<{ 'open-outcome': [outcomeId: string]; 'manage-outcomes': [] }>();

/** The card whose "Change outcome…" is open, or ''. */
const changingOutcome = ref('');
const agentUpdates = useAgentUpdatesStore();

const viewOptions: { label: string; value: KanbanView; icon: string }[] = [
  { label: 'Board', value: 'board', icon: 'view_kanban' },
  { label: 'Swimlanes', value: 'swimlanes', icon: 'table_rows' },
];

/**
 * THE RUNNING LANE (In Progress) COUNTS ITS CARDS, like every lane, and shows the ledger's figure
 * on a line of its own that says what it counts: `3 / 4 agents running, all teams`. The ledger is
 * instance-wide - every team's runs, a Manager's with no card among them, whatever the board is
 * filtered to - so given as the lane's count it read `1 / 2 running` over an empty lane. Before the
 * ledger answers there is no line.
 */
function ledgerText(lane: KanbanLane): string {
  return isRunningLane(lane.id) && wip.view ? runningAcrossTeams(wip.runningCount, wip.max) : '';
}

/**
 * WHAT A LANE HOLDS: its cards, and in Needs You the proposed outcomes waiting on a person too -
 * one count for the header and its amber.
 */
function laneCount(lane: KanbanLane): number {
  const cards = kanban.laneCards(lane.id).length;

  return lane.id === NeedsYouLaneId ? cards + kanban.proposedShown.length : cards;
}

/**
 * A lane's count: `count / limit` with an advisory limit, amber once over it - never a block. A
 * running lane's limit is the ledger's, counted in agents rather than cards, so its count is its
 * cards alone and the limit is on the ledger line beside it.
 */
function laneCountText(lane: KanbanLane): string {
  const count = laneCount(lane);

  return !isRunningLane(lane.id) && typeof lane.wipLimit === 'number' && lane.wipLimit > 0
    ? `${count} / ${lane.wipLimit}`
    : `${count}`;
}

/**
 * Amber once over the limit, IN PROGRESS INCLUDED: over the ledger line it shows
 * (`4 / 2 agents running, all teams`), or, before the ledger answers, over its running cards
 * against the lane's limit.
 */
function laneOver(lane: KanbanLane): boolean {
  if (isRunningLane(lane.id)) {
    if (wip.view) return laneOverLimit(wip.runningCount, wip.max);

    const running = kanban.laneCards(lane.id).filter((card) => card.status === 'running').length;
    return laneOverLimit(running, lane.wipLimit);
  }

  return laneOverLimit(laneCount(lane), lane.wipLimit);
}

// ONE WIDTH FOR EVERY LANE, set by dragging any divider between two lanes and remembered by this
// browser. It is a CSS variable on the board, so the lanes, the swimlanes grid and a card leaving a
// lane all read the same figure.
const laneWidth = ref(loadLaneWidth());
const boardStyle = computed(() => ({ '--k-lane-width': `${laneWidth.value}px` }));

function setLaneWidth(width: number) {
  laneWidth.value = clampLaneWidth(width);
}

/** A drag on a divider moves every lane's width by how far the pointer moved, saved when it is let go. */
function startLaneResize(event: PointerEvent) {
  const handle = event.currentTarget as HTMLElement;
  const startX = event.clientX;
  const startWidth = laneWidth.value;
  handle.setPointerCapture?.(event.pointerId);

  const move = (e: PointerEvent) => setLaneWidth(startWidth + (e.clientX - startX));
  const end = () => {
    handle.removeEventListener('pointermove', move);
    handle.removeEventListener('pointerup', end);
    handle.removeEventListener('pointercancel', end);
    saveLaneWidth(laneWidth.value);
  };

  handle.addEventListener('pointermove', move);
  handle.addEventListener('pointerup', end);
  handle.addEventListener('pointercancel', end);
}

/** The keyboard on a divider: arrows narrow or widen every lane a step, Home resets. */
function laneResizeKey(event: KeyboardEvent) {
  const next =
    event.key === 'ArrowLeft' ? laneWidth.value - LaneWidthStep
      : event.key === 'ArrowRight' ? laneWidth.value + LaneWidthStep
        : event.key === 'Home' ? DefaultLaneWidth
          : null;
  if (next === null) return;
  event.preventDefault();
  setLaneWidth(next);
  saveLaneWidth(laneWidth.value);
}

function resetLaneWidth() {
  setLaneWidth(DefaultLaneWidth);
  saveLaneWidth(laneWidth.value);
}

/** The swimlane grid: a team column, then one track per lane at the shared lane width. */
const gridColumns = computed(() => `10rem repeat(${kanban.lanes.length}, var(--k-lane-width))`);

const lanesEl = ref<HTMLElement | null>(null);

/** The last measured layout - the F in FLIP. Rewritten on every update, moved or not. */
let previous = new Map<string, CardBox>();

function measure(): Map<string, CardBox> {
  const boxes = new Map<string, CardBox>();
  const root = lanesEl.value;

  if (!root) return boxes;

  for (const node of root.querySelectorAll<HTMLElement>('[data-card-id]')) {
    const id = node.dataset.cardId;
    if (!id) continue;

    const rect = node.getBoundingClientRect();
    boxes.set(id, { left: rect.left, top: rect.top });
  }

  return boxes;
}

/**
 * NOT ANIMATED FOR SOMEBODY WHO ASKED FOR LESS MOTION. The board's job is to show that a card
 * moved; the movement is how it says so to everyone else, and the new position says the same thing
 * with nothing flying across the screen.
 */
const stillness = () =>
  typeof window !== 'undefined' &&
  typeof window.matchMedia === 'function' &&
  window.matchMedia('(prefers-reduced-motion: reduce)').matches;

/**
 * A TEAM'S KANBAN BUTTON WHILE SWIMLANES IS ON: every row stays, and that team's row is
 * scrolled into view. Run after each render, because the row may only exist once the board the
 * button fetched has landed; `revealTeam` is cleared once it has been shown, so a later refetch
 * does not yank the page back to it.
 */
function revealRow() {
  const team = kanban.revealTeam;
  const root = lanesEl.value;

  if (!team || kanban.view !== 'swimlanes' || !root) return;

  const row = [...root.querySelectorAll<HTMLElement>('[data-team-row]')].find(
    (node) => node.dataset.teamRow === team,
  );
  if (!row) return;

  row.scrollIntoView({ block: 'nearest', inline: 'nearest', behavior: stillness() ? 'auto' : 'smooth' });
  kanban.revealTeam = '';
}

watchPostEffect(revealRow);

/**
 * NEEDS YOU'S PROPOSED OUTCOMES are reread on arrival and with every board the store lands - a hub
 * push or a person's move - and after a write in Manage outcomes, so a confirmed, merged, retired or
 * rejected outcome leaves the lane.
 */
watch(() => kanban.board, () => void kanban.loadProposed());

onMounted(() => {
  void kanban.loadProposed();
  previous = measure();
  wip.watch();
  agentUpdates.watch();
  revealRow();
});

onUnmounted(() => {
  wip.unwatch();
  agentUpdates.unwatch();
});

onBeforeUpdate(() => {
  previous = measure();
});

onUpdated(() => {
  revealRow();

  const after = measure();
  const shifts = flipShifts(previous, after);
  previous = after;

  if (shifts.size === 0 || stillness()) return;

  const root = lanesEl.value;
  if (!root) return;

  const moved: HTMLElement[] = [];

  for (const node of root.querySelectorAll<HTMLElement>('[data-card-id]')) {
    const shift = shifts.get(node.dataset.cardId ?? '');
    if (!shift) continue;

    // INVERT: no transition, sit where you were.
    node.style.transition = 'none';
    node.style.transform = flipTransform(shift);
    moved.push(node);
  }

  if (moved.length === 0) return;

  // Reading a layout property forces the browser to accept the inverted position as a real frame.
  // Without it the two writes below collapse into one style recalculation and nothing animates at
  // all - the cards simply appear in their new lane, which is what this whole block exists to
  // avoid.
  void root.offsetHeight;

  requestAnimationFrame(() => {
    for (const node of moved) {
      // PLAY: back to where the DOM already says you are.
      node.style.transition = `transform ${FlipDurationMs}ms cubic-bezier(0.2, 0, 0, 1)`;
      node.style.transform = '';
    }
  });
});

// A proposed outcome keeps the lanes up: Needs You has something to show with no card on the board.
const empty = computed(() => kanban.hasBoard && kanban.cardCount === 0 && kanban.proposedShown.length === 0);

/**
 * MANAGE OUTCOMES, opened from the filter bar's button at the list, and from a card's outcome tag
 * (`open-outcome`, the outcome's id) at that outcome.
 */
const outcomesOpen = ref(false);
const outcomeAt = ref<string | null>(null);

function openOutcomes(id: string | null = null) {
  outcomeAt.value = id;
  outcomesOpen.value = true;
}
</script>

<template>
  <div class="k-board" :style="boardStyle">
    <div class="row items-center q-gutter-sm q-mb-sm">
      <div class="text-h6">Kanban</div>
      <div class="text-caption os-text-muted">
        {{ boardCountText(kanban.cardCount, kanban.proposedShown.length) }}
      </div>

      <q-space />

      <q-btn-toggle
        :model-value="kanban.view"
        :options="viewOptions"
        dense
        no-caps
        unelevated
        toggle-color="primary"
        class="k-view-toggle"
        aria-label="Board layout"
        @update:model-value="(value: KanbanView) => kanban.setView(value)"
      />

      <q-spinner v-if="kanban.loading" size="18px" color="primary" />
    </div>

    <q-banner v-if="kanban.error" dense rounded class="bg-negative text-white q-mb-sm">
      <template #avatar><q-icon name="error" /></template>
      {{ kanban.error }}
    </q-banner>

    <KanbanFilterBar class="q-mb-md" @manage-outcomes="openOutcomes(); $emit('manage-outcomes')" />

    <div v-if="!kanban.hasBoard && !kanban.loading" class="column items-center q-pa-xl text-center">
      <div class="text-subtitle1 q-mb-sm">No board yet</div>
      <div class="text-body2 os-text-muted" style="max-width: 32rem">
        The board is projected from the message log by <code>/api/kanban/board</code>. If that
        endpoint is not in this build, there is nothing to show and the banner above says why.
      </div>
    </div>

    <!-- THE SEARCH BOX IS NAMED HERE TOO. It narrows this view without narrowing the fetch, so a
         board emptied by three characters nobody remembers typing would otherwise be explained by
         a sentence about filters that are all unset. Clear resets both. -->
    <div v-else-if="empty" class="column items-center q-pa-xl text-center">
      <div class="text-subtitle1 q-mb-sm">Nothing matches this filter</div>
      <div class="text-body2 os-text-muted" style="max-width: 32rem">
        Cards appear when a manager tells a member to do something. Widen the filter, empty the
        search box, or clear both, to see the whole tenant.
      </div>
    </div>

    <!-- ONE ROW PER TEAM. The lane headers are a sticky top row and the team names a sticky left
         column, so a person scrolled into the middle still knows which lane and which team a card
         is in. `lanesEl` is on this grid too: the FLIP above measures `[data-card-id]` wherever
         the cards are, so a card moving lane inside its team's row animates the same way. -->
    <div
      v-else-if="kanban.view === 'swimlanes'"
      ref="lanesEl"
      class="k-swimlanes"
      :style="{ gridTemplateColumns: gridColumns }"
    >
      <div class="k-swim-corner k-swim-sticky-top k-swim-sticky-left">Team</div>
      <header
        v-for="lane in kanban.lanes"
        :key="`head-${lane.id}`"
        class="k-lane-head k-swim-head k-swim-sticky-top"
        :class="{ 'k-lane-head--over': laneOver(lane) }"
        :data-lane-id="lane.id"
      >
        <span class="k-lane-title">{{ lane.title }}</span>
        <span class="k-lane-count">{{ laneCountText(lane) }}</span>
        <span v-if="ledgerText(lane)" class="k-lane-wip" data-lane-wip>{{ ledgerText(lane) }}</span>
      </header>

      <!-- PROPOSED OUTCOMES BELONG TO NO TEAM: an outcome is instance-wide, so they have a row of
           their own, with entries in Needs You's column only. -->
      <template v-if="kanban.proposedShown.length">
        <div class="k-swim-team k-swim-sticky-left">Outcomes</div>
        <div v-for="lane in kanban.lanes" :key="`outcomes-${lane.id}`" class="k-lane-cards k-swim-cell">
          <KanbanProposedOutcomes
            v-if="lane.id === NeedsYouLaneId"
            :outcomes="kanban.proposedShown"
            @open="(id: string) => { openOutcomes(id); $emit('open-outcome', id); }"
          />
        </div>
      </template>

      <template v-for="team in kanban.swimlanes" :key="team.id">
        <div class="k-swim-team k-swim-sticky-left" :data-team-row="team.id">{{ team.name }}</div>
        <TransitionGroup
          v-for="lane in kanban.lanes"
          :key="`${team.id}-${lane.id}`"
          tag="div"
          class="k-lane-cards k-swim-cell"
          enter-active-class="k-card-move-enter-active"
          leave-active-class="k-card-move-leave-active"
          enter-from-class="k-card-move-enter-from"
          leave-to-class="k-card-move-leave-to"
        >
          <KanbanCard
            v-for="card in kanban.cellCards(team.id, lane.id)"
            :key="card.id"
            :card="card"
            :waiting="cardShowsWaiting(card, kanban.memberHeld(card))"
            :update-wait="card.member ? agentUpdates.heldBy(card.team, card.member) : null"
            @open="kanban.select"
            @open-outcome="(id: string) => { openOutcomes(id); $emit('open-outcome', id); }"
            @change-outcome="(id: string) => (changingOutcome = id)"
          />
        </TransitionGroup>
      </template>
    </div>

    <div v-else ref="lanesEl" class="k-lanes">
      <template v-for="(lane, index) in kanban.lanes" :key="lane.id">
      <section class="k-lane" :data-lane-id="lane.id">
        <header class="k-lane-head" :class="{ 'k-lane-head--over': laneOver(lane) }">
          <span class="k-lane-title">{{ lane.title }}</span>
          <span class="k-lane-count">{{ laneCountText(lane) }}</span>
          <span v-if="ledgerText(lane)" class="k-lane-wip" data-lane-wip>{{ ledgerText(lane) }}</span>
        </header>

        <KanbanProposedOutcomes
          v-if="lane.id === NeedsYouLaneId"
          :outcomes="kanban.proposedShown"
          @open="(id: string) => { openOutcomes(id); $emit('open-outcome', id); }"
        />

        <!-- TransitionGroup handles the card ENTERING and LEAVING a lane; the FLIP above handles
             the journey between the two, which no transition can express on its own. -->
        <!-- The four classes are NAMED here rather than derived from a `name` prop. Vue would
             generate exactly these from `name="k-card-move"`, and the rules would work - but a
             class name that exists only by convention is a class name `styles-match-templates`
             cannot see, and that spec is the only thing standing between this board and a
             dangling selector that silently does nothing. -->
        <TransitionGroup
          tag="div"
          class="k-lane-cards"
          enter-active-class="k-card-move-enter-active"
          leave-active-class="k-card-move-leave-active"
          enter-from-class="k-card-move-enter-from"
          leave-to-class="k-card-move-leave-to"
        >
          <KanbanCard
            v-for="card in kanban.laneCards(lane.id)"
            :key="card.id"
            :card="card"
            :waiting="cardShowsWaiting(card, kanban.memberHeld(card))"
            :update-wait="card.member ? agentUpdates.heldBy(card.team, card.member) : null"
            @open="kanban.select"
            @open-outcome="(id: string) => { openOutcomes(id); $emit('open-outcome', id); }"
            @change-outcome="(id: string) => (changingOutcome = id)"
          />
        </TransitionGroup>
      </section>

        <!-- THE DIVIDER after every lane but the last: dragging any one sets every lane's width.
             A separator a person can focus, so the arrows do what the drag does. -->
        <div
          v-if="index < kanban.lanes.length - 1"
          class="k-lane-divider"
          role="separator"
          aria-orientation="vertical"
          aria-label="Lane width"
          :aria-valuenow="laneWidth"
          tabindex="0"
          title="Drag to make every lane wider or narrower; double-click to reset"
          data-test="lane-divider"
          @pointerdown.prevent="startLaneResize"
          @keydown="laneResizeKey"
          @dblclick="resetLaneWidth"
        />
      </template>
    </div>

    <KanbanCardPanel />
    <KanbanChangeOutcome :card-id="changingOutcome" @close="changingOutcome = ''" />
    <OutcomesDialog v-model="outcomesOpen" :outcome="outcomeAt" @changed="kanban.loadProposed()" />
  </div>
</template>

<style scoped>
.k-board {
  min-height: 60vh;
}

/* Horizontal swim lanes: the template's lanes side by side, scrolling sideways rather than
   squeezing. A five-lane template on a laptop is narrower than a comfortable card, and a lane
   nobody can read is worse than a lane you have to scroll to. */
.k-lanes {
  display: flex;
  gap: 0;
  align-items: flex-start;
  overflow-x: auto;
  padding-bottom: 8px;
}

.k-lane {
  /* The shared width, and never wider: a long outcome or title wraps rather than stretching the lane. */
  flex: 0 0 var(--k-lane-width, 300px);
  width: var(--k-lane-width, 300px);
  min-width: 0;
  background: var(--os-chrome);
  border: 1px solid var(--os-rule);
  border-radius: 8px;
  padding: 8px;
  min-height: 8rem;
}

.k-lane-head {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 8px;
  font-size: 12px;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  color: var(--os-ink-muted);
}

.k-lane-count {
  font-family: 'IBM Plex Mono', monospace;
}

/* The ledger's line under the running lane's title and count: a whole row of its own, in the
   header's colour, so it turns amber with it. Not upper-cased: it is a sentence. */
.k-lane-wip {
  flex: 1 0 100%;
  text-transform: none;
  letter-spacing: 0;
}

/* OVER AN ADVISORY LIMIT. Amber, and the count beside it says by how much - colour is never the
   only signal. `--os-warn-ink`, not `--q-warning`: the header is 12px on white or chrome, where
   `--q-warning` fails AA contrast in light. */
.k-lane-head--over {
  color: var(--os-warn-ink);
  font-weight: 600;
}

/* The swimlanes grid. Columns are set inline: a team column, then one track per lane at the shared
   `--k-lane-width` the board's lanes use, so switching views does not reflow a card. */
.k-swimlanes {
  display: grid;
  gap: 8px 12px;
  overflow: auto;
  max-height: 75vh;
  padding-bottom: 8px;
}

.k-swim-sticky-top {
  position: sticky;
  top: 0;
  z-index: 2;
  background: var(--os-surface);
}

.k-swim-sticky-left {
  position: sticky;
  left: 0;
  z-index: 1;
  background: var(--os-surface);
}

.k-swim-corner {
  z-index: 3;
  font-size: 12px;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  color: var(--os-ink-muted);
  padding: 8px 4px;
}

.k-swim-head {
  padding: 8px;
  margin-bottom: 0;
  border-bottom: 1px solid var(--os-rule);
}

.k-swim-team {
  font-weight: 600;
  padding: 8px 4px;
  border-top: 1px solid var(--os-rule);
  overflow-wrap: anywhere;
}

.k-swim-cell {
  background: var(--os-chrome);
  border: 1px solid var(--os-rule);
  border-radius: 8px;
  padding: 8px;
  min-height: 4rem;
}

.k-lane-cards {
  display: flex;
  flex-direction: column;
  gap: 8px;

  /* The containing block for a leaving card, which is taken out of the flow below. */
  position: relative;
}

.k-card-move-enter-active,
.k-card-move-leave-active {
  transition:
    opacity 200ms ease,
    transform 200ms ease;
}

.k-card-move-enter-from,
.k-card-move-leave-to {
  opacity: 0;
  transform: scale(0.96);
}

/* A leaving card is taken out of the flow so the cards under it close up while the FLIP carries
   the same card into its new lane. Left in the flow, every lane would jump a card's height. */
.k-card-move-leave-active {
  position: absolute;
  width: calc(var(--k-lane-width, 300px) - 16px);
}

/* The divider between two lanes: the 12px gap the lanes used to have, with a line that shows on
   hover and focus. */
.k-lane-divider {
  flex: 0 0 12px;
  align-self: stretch;
  min-height: 8rem;
  cursor: col-resize;
  touch-action: none;
  position: relative;
}

.k-lane-divider::after {
  content: '';
  position: absolute;
  top: 0;
  bottom: 0;
  left: 5px;
  width: 2px;
  border-radius: 1px;
  background: transparent;
}

.k-lane-divider:hover::after,
.k-lane-divider:focus-visible::after {
  background: var(--q-primary);
}

.k-lane-divider:focus-visible {
  outline: none;
}

@media (prefers-reduced-motion: reduce) {
  .k-card-move-enter-active,
  .k-card-move-leave-active {
    transition: none;
  }
}
</style>
