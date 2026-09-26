<script setup lang="ts">
import { computed, onBeforeUpdate, onMounted, onUnmounted, onUpdated, ref, watchPostEffect } from 'vue';
import type { KanbanLane } from '../api/kanban';
import { useKanbanStore, type KanbanView } from '../stores/kanban';
import { inProgressHeader, useWipStore } from '../stores/wip';
import { cardShowsWaiting, laneOverLimit } from '../lib/kanban';
import { isRunningLane } from '../lib/tenantSettings';
import KanbanCard from './KanbanCard.vue';
import KanbanFilterBar from './KanbanFilterBar.vue';
import KanbanCardPanel from './KanbanCardPanel.vue';
import { FlipDurationMs, flipShifts, flipTransform, type CardBox } from '../lib/flip';

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

const viewOptions: { label: string; value: KanbanView; icon: string }[] = [
  { label: 'Board', value: 'board', icon: 'view_kanban' },
  { label: 'Swimlanes', value: 'swimlanes', icon: 'table_rows' },
];

/**
 * THE RUNNING LANE'S HEADER (In Progress) COUNTS AGENTS RUNNING, AGAINST THE LEDGER'S LIMIT - `3 / 4 running`. The
 * limit is `wip.maxRunning`, instance-wide, so the figure is the ledger's and not this board's
 * card count. Before the ledger answers it falls back to the lane's own `wipLimit` and its running
 * cards, and with no limit either, to the plain card count.
 */
function inProgressText(lane: KanbanLane): string {
  const figure = runningFigure(lane);

  // With neither the ledger nor a limit there is nothing to read the lane against: its card count.
  return figure ? inProgressHeader(figure.running, figure.max) : laneCountText(lane);
}

/**
 * The running figure and the limit the running lane's header reads, or null when it has neither
 * and falls back to its card count. ONE ANSWER for the header text and the amber, so the lane
 * turns amber over exactly the figure it shows.
 */
function runningFigure(lane: KanbanLane): { running: number; max: number | null } | null {
  if (wip.view) return { running: wip.runningCount, max: wip.max };

  if (typeof lane.wipLimit !== 'number') return null;

  const running = kanban.laneCards(lane.id).filter((card) => card.status === 'running').length;

  return { running, max: lane.wipLimit };
}

/** Every other lane with a limit: `count / limit`, amber once over it. Advisory, never a block. */
function laneCountText(lane: KanbanLane): string {
  const count = kanban.laneCards(lane.id).length;

  return typeof lane.wipLimit === 'number' && lane.wipLimit > 0 ? `${count} / ${lane.wipLimit}` : `${count}`;
}

/**
 * Amber once over the limit, IN PROGRESS INCLUDED: `4 / 2 running` is
 * over its limit like any other lane. A running lane is read against the figure its header shows.
 */
function laneOver(lane: KanbanLane): boolean {
  if (isRunningLane(lane.id)) {
    const figure = runningFigure(lane);
    if (figure) return laneOverLimit(figure.running, figure.max);
  }

  return laneOverLimit(kanban.laneCards(lane.id).length, lane.wipLimit);
}

/** The swimlane grid: a team column, then one track per lane at the `.k-lane` width. */
const gridColumns = computed(() => `10rem repeat(${kanban.lanes.length}, 17rem)`);

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

onMounted(() => {
  previous = measure();
  wip.watch();
  revealRow();
});

onUnmounted(() => wip.unwatch());

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

const empty = computed(() => kanban.hasBoard && kanban.cardCount === 0);
</script>

<template>
  <div class="k-board">
    <div class="row items-center q-gutter-sm q-mb-sm">
      <div class="text-h6">Kanban</div>
      <div class="text-caption os-text-muted">
        {{ kanban.cardCount }} card{{ kanban.cardCount === 1 ? '' : 's' }}
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

    <KanbanFilterBar class="q-mb-md" />

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
        <span class="k-lane-count">{{ isRunningLane(lane.id) ? inProgressText(lane) : laneCountText(lane) }}</span>
      </header>

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
            @open="kanban.select"
          />
        </TransitionGroup>
      </template>
    </div>

    <div v-else ref="lanesEl" class="k-lanes">
      <section v-for="lane in kanban.lanes" :key="lane.id" class="k-lane" :data-lane-id="lane.id">
        <header class="k-lane-head" :class="{ 'k-lane-head--over': laneOver(lane) }">
          <span class="k-lane-title">{{ lane.title }}</span>
          <span class="k-lane-count">{{ isRunningLane(lane.id) ? inProgressText(lane) : laneCountText(lane) }}</span>
        </header>

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
            @open="kanban.select"
          />
        </TransitionGroup>
      </section>
    </div>

    <KanbanCardPanel />
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
  gap: 12px;
  align-items: flex-start;
  overflow-x: auto;
  padding-bottom: 8px;
}

.k-lane {
  flex: 0 0 17rem;
  background: var(--os-chrome);
  border: 1px solid var(--os-rule);
  border-radius: 8px;
  padding: 8px;
  min-height: 8rem;
}

.k-lane-head {
  display: flex;
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

/* OVER AN ADVISORY LIMIT. Amber, and the count beside it says by how much - colour is never the
   only signal. `--os-warn-ink`, not `--q-warning`: the header is 12px on white or chrome, where
   `--q-warning` fails AA contrast in light. */
.k-lane-head--over {
  color: var(--os-warn-ink);
  font-weight: 600;
}

/* The swimlanes grid. Columns are set inline: a team column, then one 17rem track per lane - the
   same width `.k-lane` has on the board, so switching views does not reflow a card. */
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
  width: calc(17rem - 16px);
}

@media (prefers-reduced-motion: reduce) {
  .k-card-move-enter-active,
  .k-card-move-leave-active {
    transition: none;
  }
}
</style>
