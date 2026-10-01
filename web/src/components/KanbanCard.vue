<script setup lang="ts">
import { computed } from 'vue';
import type { KanbanCard } from '../api/kanban';
import { itemLabel } from '../lib/backlog';
import { KanbanStatusLabel, colourFor } from '../lib/kanban';

/**
 * One card on the board.
 *
 * PRESENTATIONAL. It fetches nothing, holds nothing and decides nothing: the colour comes from
 * `lib/kanban`'s rule, and a click is an event the board turns into a side panel. That is what
 * makes it safe for the parent to move its DOM node about during a FLIP - there is no state in
 * here for a re-parent to lose.
 *
 * `data-card-id` is how the board finds this node to measure it. It is the card's stable id, which
 * survives a replay, so a card keeps its identity across a rebuild of the whole projection and the
 * animation follows the same card rather than whatever now sits in that position.
 */
const props = defineProps<{
  card: KanbanCard;
  /** This card's member is waiting for a WIP slot. The board decides; the card only draws it. */
  waiting?: boolean;
  /** The CLI whose update holds this card's launch, or null. The board decides; the card only draws it. */
  updateWait?: string | null;
}>();

/**
 * `open-outcome` is the tag clicked, with the outcome's id: the board passes it up to whatever
 * shows outcomes. `change-outcome` is the card menu's "Change outcome…", with the card's id.
 */
const emit = defineEmits<{ open: [id: string]; 'open-outcome': [outcomeId: string]; 'change-outcome': [id: string] }>();

function openOutcome() {
  if (props.card.outcome) emit('open-outcome', props.card.outcome.id);
}

const colour = computed(() => colourFor(props.card));

/* THE WORD BECOMES A COLOUR IN `app.scss`, not here. `.os-kanban-<word>` sets `--os-kanban-accent`
   and `--os-kanban-tint` for the current theme, and the rules below read those two, so a card, its
   panel and a dark ground all agree without a hex in any component. */
const colourClass = computed(() => `os-kanban-${colour.value}`);
const progressCount = computed(() => props.card.progress?.length ?? 0);
</script>

<template>
  <button
    type="button"
    class="k-card"
    :class="colourClass"
    :data-colour="colour"
    :data-awaiting="card.awaitingManager ? 'yes' : null"
    :data-waiting="waiting ? 'yes' : null"
    :data-update-wait="updateWait || null"
    :data-card-id="card.id"
    :aria-label="`${card.title} — ${KanbanStatusLabel[card.status]}`"
    @click="$emit('open', card.id)"
  >
    <!-- THE ITEM THIS CARD IS A PIECE OF, when it is one. `B000H` rather than a bare number, which
         is the same prefix the backlog screen renders and the one an agent quotes back. -->
    <div v-if="card.item != null" class="k-card-item">{{ itemLabel(card.item) }}</div>

    <div class="k-card-title">{{ card.title }}</div>

    <!-- THE OUTCOME THIS CARD'S WORK SERVES, under the title: the open workflow's, else the latest
         one's. No outcome, no tag. A proposed one is dashed and says so, because only a person
         confirms it. A span with a button's role rather than a button: this whole card is a
         button, and one may not hold another. The name is text, never HTML. -->
    <span
      v-if="card.outcome"
      role="button"
      tabindex="0"
      class="k-card-outcome"
      :class="{ 'k-card-outcome--proposed': card.outcome.status === 'proposed' }"
      :data-outcome-id="card.outcome.id"
      :title="`Outcome: ${card.outcome.name}`"
      @click.stop="openOutcome"
      @keydown.enter.stop.prevent="openOutcome"
      @keydown.space.stop.prevent="openOutcome"
    >
      <span class="k-card-outcome-name">{{ card.outcome.name }}</span>
      <span v-if="card.outcome.status === 'proposed'" class="k-card-outcome-proposed">proposed</span>
    </span>

    <div class="k-card-meta">
      <!-- NULL IS ITS OWN WORD, not an empty span. A card nobody has been assigned yet is the whole
           reason the Todo lane can be filled before anybody is told, and a blank where a name goes
           reads as a rendering fault rather than as a state. -->
      <span class="k-card-member" :class="card.member === null ? 'k-card-unassigned' : ''">
        {{ card.member ?? 'Unassigned' }}
      </span>
      <!-- THE CARD'S OWN ID, AS WELL AS THE WORKFLOW SEQ - both, never one instead of the other.
           The id is what `tell --card <id>` takes and what a member quotes in every progress line
           ("card 2237: read spec"), so it is the one handle a person watching the board and an
           agent reporting to it share. Three cards planned under one workflow all read `#2226`;
           the id is what tells them apart. It is LABELLED, because an instruction card's id is
           `2310_Manager` and a bare `2310_Manager` beside the member's name reads as a fault. -->
      <span class="k-card-id" :title="`Card id ${card.id} - what tell --card takes`">card {{ card.id }}</span>
      <!-- The workflow seq, because it is the number that GROUPS cards - the one a person quotes
           back at a manager and the one `--workflow` filters on. A card without it could
           not be found among its siblings. -->
      <span class="k-card-workflow" :title="`Workflow #${card.workflowSeq}`">#{{ card.workflowSeq }}</span>

      <!-- THE CARD MENU. A span for the reason the tag is one; the click stops here so it does not
           open the panel as well. -->
      <span
        role="button"
        tabindex="0"
        class="k-card-menu"
        aria-label="Card menu"
        @click.stop
        @keydown.enter.stop
      >
        <q-icon name="more_horiz" size="16px" />
        <q-menu>
          <q-list dense>
            <q-item v-close-popup clickable data-action="change-outcome" @click="emit('change-outcome', card.id)">
              <q-item-section>Change outcome…</q-item-section>
            </q-item>
          </q-list>
        </q-menu>
      </span>
    </div>

    <div class="k-card-foot">
      <span class="k-card-status">{{ KanbanStatusLabel[card.status] }}</span>

      <span v-if="progressCount" class="k-card-progress" :title="`${progressCount} progress lines`">
        <q-icon name="checklist" size="13px" />
        {{ progressCount }}
      </span>

      <!-- The magenta is an OVERLAY and never a status: a card awaiting a manager is still queued,
           running or blocked, and colouring it magenta would lose the state it is actually in. -->
      <q-badge v-if="card.awaitingManager" class="k-card-awaiting" label="awaiting manager" />

      <!-- THE WORKFLOW THIS CARD BELONGS TO SPENT ITS BUDGET, so nothing will move this card until
           somebody resumes that workflow. A BADGE AND NOT A STATUS, exactly like the one above: a
           paused card is still queued, running or blocked, and folding the pause into `status`
           would erase the state it is actually in - and erase a member's own `blocked` mark, which
           the next `container.started` resets and a pause does not. -->
      <q-badge v-if="card.paused" class="k-card-paused os-kanban-teal" label="paused" />

      <!-- A RUN ON THIS CARD MET A TOOL THE PLATFORM DID NOT GIVE IT. A call is marked above an
           offer: a filled badge in the negative colour for a call, the warning colour for an offer,
           and a quiet outline for a run that could not be checked - never nothing, since unknown is
           not clean. A badge beside the status, like the two above. -->
      <q-badge
        v-if="card.foreignTools === 'called'"
        class="k-card-foreign k-card-foreign-called"
        label="foreign tool called"
      >
        <q-tooltip>A run on this card called a tool the platform did not give it. See the card's trail.</q-tooltip>
      </q-badge>
      <q-badge
        v-else-if="card.foreignTools === 'offered'"
        class="k-card-foreign k-card-foreign-offered"
        label="foreign tool offered"
      >
        <q-tooltip>A run on this card was offered a tool the platform did not give it, and called none.</q-tooltip>
      </q-badge>
      <q-badge
        v-else-if="card.foreignTools === 'notMeasured'"
        class="k-card-foreign k-card-foreign-unmeasured"
        outline
        label="tools not measured"
      >
        <q-tooltip>A run on this card could not be checked for foreign tools, so it is not reported clean.</q-tooltip>
      </q-badge>
      <q-badge
        v-else-if="card.foreignTools === 'notVerified'"
        class="k-card-foreign k-card-foreign-unmeasured"
        outline
        label="preset not verified"
      >
        <q-tooltip>This member's preset declares no allowed tools, so only its MCP servers were checked. It is not reported clean.</q-tooltip>
      </q-badge>

      <!-- HELD BY THE WIP LIMIT: the work has not failed and nothing refused it; it starts when a
           slot is released. A mark beside the status, never instead of it. -->
      <q-badge v-if="waiting" class="k-card-waiting" label="waiting for a slot">
        <q-tooltip>Every running slot is taken. This member starts when one is released.</q-tooltip>
      </q-badge>

      <!-- HELD BEHIND AN AGENT CLI UPDATE: held, never failed; it starts when the update is done or
           cancelled. -->
      <q-badge v-if="updateWait" class="k-card-waiting k-card-update-wait" :label="`waiting for the ${updateWait} update`">
        <q-tooltip>The platform is updating {{ updateWait }}. This member's run starts when the update is done.</q-tooltip>
      </q-badge>
    </div>
  </button>
</template>

<style scoped>
/* The colour transition. A status change repaints the card in place over the same third of a
   second the FLIP move takes, so a card that changes colour AND lane does one movement rather
   than a jump and a flash. */
.k-card {
  display: block;
  width: 100%;
  text-align: left;
  border: 1px solid var(--os-rule);
  border-left: 4px solid var(--os-ink-faint);
  border-radius: 6px;
  background: var(--os-surface);
  padding: 8px 10px;
  cursor: pointer;
  font: inherit;
  color: var(--os-ink);
  transition:
    border-color 320ms ease,
    background-color 320ms ease,
    box-shadow 320ms ease;
}

.k-card:hover {
  border-color: var(--os-rule-strong);
}

.k-card:focus-visible {
  outline: 2px solid var(--q-primary);
  outline-offset: 1px;
}

/* THE STATUS COLOUR, READ FROM THE TOKENS `.os-kanban-<word>` sets on this element. The transition
   above still runs: it is `border-left-color` and `background-color` that change, and a `var()`
   is resolved before the transition sees the value.

   `data-colour` stays in the template as the word the tests and the panel read; the class carries
   the colour. */
.k-card[data-colour] {
  border-left-color: var(--os-kanban-accent, var(--os-ink-faint));
  background: var(--os-kanban-tint, var(--os-surface));
}

.k-card[data-awaiting] {
  box-shadow: 0 0 0 2px var(--q-negative);
}

/* THE ITEM LINE SITS ABOVE THE TITLE, small and muted: it is context for the piece of work rather
   than the piece of work itself, and a card that shouted its parent would bury its own subject. */
.k-card-item {
  font-size: 10px;
  font-weight: 600;
  letter-spacing: 0.04em;
  color: var(--os-ink-muted);
}

/* THE OUTCOME TAG: a small outlined pill under the title. Dashed for a proposed outcome. */
.k-card-outcome {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  max-width: 100%;
  margin-top: 4px;
  padding: 0 6px;
  border: 1px solid var(--os-rule-strong);
  border-radius: 10px;
  font-size: 10px;
  line-height: 16px;
  color: var(--os-ink-muted);
  cursor: pointer;
}

.k-card-outcome:hover,
.k-card-outcome:focus-visible {
  color: var(--os-ink);
  border-color: var(--q-primary);
}

.k-card-outcome--proposed {
  border-style: dashed;
}

.k-card-outcome-name {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.k-card-outcome-proposed {
  font-style: italic;
}

.k-card-menu {
  margin-left: auto;
  display: inline-flex;
  align-items: center;
  cursor: pointer;
  color: var(--os-ink-muted);
}

/* `Unassigned` is dimmer than a name, so a filled Todo lane reads at a glance as work waiting for
   somebody rather than as work belonging to somebody whose name failed to render. */
.k-card-unassigned {
  font-style: italic;
  opacity: 0.75;
}

.k-card-title {
  font-size: 13px;
  line-height: 1.35;
  font-weight: 600;
  overflow: hidden;
  display: -webkit-box;
  -webkit-line-clamp: 3;
  line-clamp: 3;
  -webkit-box-orient: vertical;
}

/* WRAPS. A member name, a card id and a workflow number do not always fit one line of a lane on
   a phone, and a meta line that clipped its own id would hide the one handle it exists to show. */
.k-card-meta {
  display: flex;
  flex-wrap: wrap;
  column-gap: 8px;
  row-gap: 2px;
  margin-top: 6px;
  font-size: 11px;
  color: var(--os-ink-muted);
}

/* BOTH IDENTIFIERS IN THE SAME MONOSPACE, so `card 2310_Manager` and `#2310` read as two handles
   for one thing rather than as a word and a number. The card id is the one an agent types, so it
   is the one a person copies. */
.k-card-id,
.k-card-workflow {
  font-family: 'IBM Plex Mono', monospace;
  white-space: nowrap;
}

.k-card-id {
  user-select: all;
}

.k-card-foot {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-top: 6px;
  font-size: 11px;
  color: var(--os-ink-muted);
}

.k-card-status {
  text-transform: uppercase;
  letter-spacing: 0.03em;
}

.k-card-progress {
  display: inline-flex;
  align-items: center;
  gap: 2px;
}

.k-card-waiting {
  background: var(--q-warning);
  color: var(--os-ink);
  font-size: 10px;
}

.k-card-awaiting {
  background: var(--q-negative);
  color: white;
  font-size: 10px;
}

/* THE TEAL THE REST OF THE PRODUCT PAUSES IN - the `teal` kanban word, taken through the same
   `.os-kanban-teal` token a completed card uses, as a fill rather than a new swatch.

   DELIBERATELY NOT THE MAGENTA ABOVE. That means "somebody outside is waiting on this team", which
   is a different fact and can be true at the same time - the two badges sit side by side. */
.k-card-paused {
  background: var(--os-kanban-accent);
  color: white;
  font-size: 10px;
}

/* A CALL IS LOUDER THAN AN OFFER: the negative fill, bold, for a call; the warning fill for an
   offer; an outline in the muted text colour for a run that could not be checked. */
.k-card-foreign {
  font-size: 10px;
}

.k-card-foreign-called {
  background: var(--q-negative);
  color: white;
  font-weight: 700;
}

.k-card-foreign-offered {
  background: var(--q-warning);
  color: black;
}

.k-card-foreign-unmeasured {
  color: var(--os-ink-muted);
}
</style>
