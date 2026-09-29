<script setup lang="ts">
import { onBeforeUnmount, ref } from 'vue';
import { getMessagesBefore } from '../api/client';
import type { Message } from '../api/types';
import { detail, label, ownerOf, summarise, timeOf } from '../lib/summarise';
import { solutionNoticeOf } from '../lib/solutionNotice';
import { useCursorList } from '../lib/useCursorList';
import { useConsoleStore } from '../stores/console';
import CursorSentinel from './CursorSentinel.vue';

/**
 * `team`, `name` and `sinceSeq` name whose feed this is, and turn on "load older". Without
 * them the feed is the live tail only.
 */
const props = defineProps<{ feed: Message[]; team?: string; name?: string; sinceSeq?: number }>();

/**
 * Which row's hover card is open - one at a time, keyed by seq rather than held per row.
 *
 * A boolean on every row would be a second store of what is already one fact ("the pointer is
 * over this row"), and two of them open at once is a state nothing would ever close.
 */
const openSeq = ref<number | null>(null);

let closing: ReturnType<typeof setTimeout> | null = null;

function cancelClose() {
  if (closing === null) return;

  clearTimeout(closing);
  closing = null;
}

/**
 * Opens the card only when the row is ACTUALLY cut off.
 *
 * Measured rather than guessed from a character count: the clamp is two lines at whatever width
 * the card happens to be, and a card is resizable per viewer, so any threshold in characters is
 * right at one width and wrong at every other. `scrollHeight > clientHeight` is exactly the
 * question the clamp answers, asked of the element that answered it.
 *
 * The point is that hovering a short row does nothing. A card that appears over every line,
 * including the ones already fully readable, is a card people learn to move the pointer around.
 */
function onEnter(event: MouseEvent, message: Message) {
  cancelClose();

  // NOT named `label`: that is the imported type-formatter this component's template calls, and
  // shadowing it here would compile fine and read as the same thing to the next person.
  const line = (event.currentTarget as HTMLElement | null)
    ?.querySelector<HTMLElement>('.feed-line');

  // The +1 absorbs sub-pixel rounding, which otherwise reports a one-line row as clipped on some
  // zoom levels and opens a card that repeats what is already on screen.
  const clipped = !!line
    && (line.scrollHeight > line.clientHeight + 1 || /\n/.test(detail(message)));

  openSeq.value = clipped ? message.seq : null;
}

/**
 * Closes on a DELAY, and that delay is what makes the card reachable.
 *
 * Without it the card vanishes the instant the pointer leaves the row - including when it leaves
 * heading for the card itself, to scroll a long report or select a line out of it. The card
 * cancels this on its own mouseenter, which is the other half.
 */
function onLeave() {
  cancelClose();
  closing = setTimeout(() => { openSeq.value = null; }, 180);
}

// A pending close outliving the component would fire against a disposed ref.
onBeforeUnmount(cancelClose);

/**
 * The WORKFLOW number, made visible.
 *
 * Both sides of a hop share one correlation id, and it rides every message the SPA holds. Rendering
 * it is what lets a person "find one correlation id on both sides of the hop, in the UI, without
 * reading the database".
 *
 * Followed through the STORE rather than through local state, because a dispatch and the completion
 * answering it are on two DIFFERENT cards by definition. A per-component highlight would light up
 * one end of exactly the thing this exists to show both ends of.
 */
const board = useConsoleStore();

/** Messages at or below this seq belong to whatever the container replaced - see `sinceSeq`. */
function floorSeq(): number {
  return props.sinceSeq ?? 0;
}

/**
 * LOAD OLDER: pages of `?before=` read into the STORE's list for this card, not into a copy here.
 *
 * The server reads this member's rows only, newest first; the owner filter stays as a guard, and the
 * cursor moves past the whole page (its lowest seq), not just the rows kept. It stops at `sinceSeq` - the
 * container's first message is the last one there is to reach - or when the log has nothing older.
 * The live window's depth never trims what this reads: see `history` in the console store.
 */
const older = useCursorList<Message>(
  async (before, take) => {
    const team = props.team;
    const name = props.name;
    if (!team || !name) return { rows: [], next: null };

    const held = props.feed;
    const from = before ?? (held.length ? held[held.length - 1]!.seq : board.lastSeq + 1);
    if (from <= floorSeq() + 1) return { rows: [], next: null };

    const page = await getMessagesBefore(team, name, from, take);
    if (page.length === 0) return { rows: [], next: null };

    board.recordHistory(team, name, page);

    const lowest = Math.min(...page.map((message) => message.seq));
    const owner = `${team}/${name}`;

    return {
      rows: page.filter((message) => ownerOf(message) === owner && message.seq > floorSeq()),
      next: lowest <= floorSeq() + 1 ? null : lowest,
    };
  },
  (message) => message.seq,
  { take: 200 },
);

/** A package's check reads as its outcome: ready is calm, problems are loud. */
function toneOf(message: Message): string {
  const notice = solutionNoticeOf(message);
  if (notice !== null) return notice.ok ? 'positive' : 'negative';
  return tone(message.type);
}

/** Completed reads calm, failed and rejected read loud. Busy is normal; refused is not. */
function tone(type: string): string {
  if (type.endsWith('failed')) return 'negative';
  if (type.endsWith('rejected')) return 'warning';
  if (type.endsWith('completed')) return 'positive';
  if (type.includes('.instruction.')) return 'primary';

  // Progress is an agent TALKING rather than a lifecycle event, and reads muted so a card of
  // status updates does not look like a card of state changes. Deliberately not the running
  // badge's green either: that one means "this is happening now", and every one of these was
  // true at the time and is history by the time it is read.
  if (type.endsWith('progress')) return 'grey-5';
  return 'grey-6';
}
</script>

<template>
  <q-list dense separator>
    <q-item
      v-for="message in feed"
      :key="message.seq"
      class="q-px-none"
      :class="{
        'feed-followed': board.highlightedWorkflow === message.correlationId,
        'feed-dimmed':
          board.highlightedWorkflow !== null
          && board.highlightedWorkflow !== message.correlationId,
      }"
      @mouseenter="onEnter($event, message)"
      @mouseleave="onLeave"
    >
      <!-- THE FULL TEXT, for a row the two-line clamp cut off. A QMenu rather than a QTooltip:
           a tooltip is sized and styled for a few words of chrome help, and what lands here is a
           manager's report or a whole dispatched instruction.

           Driven by model-value rather than by QMenu's own hover handling, because the open
           condition is not "the pointer is here" but "the pointer is here AND this row is
           actually cut off" - see onEnter. -->
      <q-menu
        :model-value="openSeq === message.seq"
        no-parent-event
        no-focus
        no-refocus
        anchor="top right"
        self="top left"
        :offset="[10, 0]"
        max-width="none"
        transition-show="fade"
        transition-hide="fade"
        @update:model-value="(open: boolean) => { if (!open) openSeq = null; }"
      >
        <!-- Every rule below is on THIS element and its children, never on the q-menu root.
             Quasar creates that root itself, so it carries no scope attribute and no scoped rule
             can reach it - the same wall the Concierge dialog met from the other side.
             Anything this card needs, it sizes itself. -->
        <!-- The tone classes are written out LITERALLY rather than composed as
             `feed-card-${tone(...)}`, which is what this was first. styles-match-templates greps
             the template for each styled class, and a composed name is invisible to it - so the
             convenient version silently opted every one of these rules out of the only check that
             catches a dangling selector. The muted default lives on .feed-card itself. -->
        <div
          class="feed-card"
          :class="{
            'feed-card-negative': toneOf(message) === 'negative',
            'feed-card-warning': toneOf(message) === 'warning',
            'feed-card-positive': toneOf(message) === 'positive',
            'feed-card-primary': toneOf(message) === 'primary',
          }"
          @mouseenter="cancelClose"
          @mouseleave="onLeave"
        >
          <div class="feed-card-head">
            <q-badge :color="toneOf(message)" :label="label(message.type)" />
            <span class="feed-card-source mono">{{ message.source }}</span>
            <span class="feed-card-spacer"></span>
            <span class="feed-card-meta mono">#{{ message.correlationId }}</span>
            <span class="feed-card-meta mono">{{ timeOf(message.occurredAt) }}</span>
          </div>

          <!-- Interpolated, exactly like the row it came from. Agent output reaches this, and a
               markdown renderer here would be a way for a member to write into the page. -->
          <div class="feed-card-body">{{ detail(message) || '—' }}</div>
        </div>
      </q-menu>
      <q-item-section side top>
        <q-badge :color="toneOf(message)" :label="label(message.type)" />
      </q-item-section>

      <q-item-section>
        <!-- Interpolated, never innerHTML: agent output reaches this line and Vue
             escapes it. -->
        <q-item-label caption lines="2" class="feed-line">{{ summarise(message) || '—' }}</q-item-label>
        <!-- A package ready to install: the wizard's link, composed from the folder and never read
             from the row's text. It opens the review; nothing installs from here. -->
        <q-item-label v-if="solutionNoticeOf(message)?.href" caption>
          <a class="feed-install text-weight-bold" :href="solutionNoticeOf(message)!.href!">Review and install</a>
        </q-item-label>
      </q-item-section>

      <!-- The workflow this message belongs to. A BUTTON rather than a label, because its job is to
           be clicked: following one lights up every message of that job on every card, which is the
           only way a person sees a hop as one thing rather than as two unrelated rows. -->
      <q-item-section side top>
        <q-btn
          flat
          dense
          no-caps
          size="sm"
          class="feed-workflow mono"
          :class="{ 'feed-workflow-on': board.highlightedWorkflow === message.correlationId }"
          :label="`#${message.correlationId}`"
          :aria-label="`Follow workflow ${message.correlationId}`"
          @click="board.toggleWorkflow(message.correlationId)"
        >
          <q-tooltip>
            {{ board.highlightedWorkflow === message.correlationId
              ? 'Stop following this workflow'
              : 'Follow this workflow across every card' }}
          </q-tooltip>
        </q-btn>
      </q-item-section>

      <!-- Absolute wall-clock, so a row can be lined up against what the console
           printed. Tabular numerals stop the column jittering as digits change. -->
      <q-item-section side top class="feed-time">
        <q-item-label caption class="mono">{{ timeOf(message.occurredAt) }}</q-item-label>
      </q-item-section>
    </q-item>

    <q-item v-if="feed.length === 0">
      <q-item-section>
        <q-item-label caption class="text-italic">Nothing yet.</q-item-label>
      </q-item-section>
    </q-item>

    <!-- LOAD OLDER, at the bottom, which is where older is. Reads history into this card's list;
         the rows above it never move. -->
    <CursorSentinel
      v-if="team && name"
      :loading="older.loading.value"
      :exhausted="older.exhausted.value"
      :error="older.error.value"
      :done-label="feed.length ? 'The first message.' : ''"
      :manual="older.cursor.value === undefined"
      @more="older.loadMore"
    />
  </q-list>
</template>

<style scoped>
/* Followed rows are LIFTED and the rest are dimmed, rather than only the followed ones being
   coloured. Against a feed of twenty rows a single highlight is easy to miss; taking the contrast
   OUT of everything else is what makes one job stand out at a glance, on a phone, without reading. */
.feed-followed {
  background: color-mix(in srgb, var(--os-primary) 10%, transparent);
}

.feed-dimmed {
  opacity: 0.38;
}

.feed-workflow {
  font-variant-numeric: tabular-nums;
  opacity: 0.55;
}

.feed-workflow-on {
  opacity: 1;
  font-weight: 600;
}

.feed-time {
  font-variant-numeric: tabular-nums;
  white-space: nowrap;
  padding-left: 8px;
}

/* THE MEASURE is the whole design here. Agent output is prose written to be read once, and a
   popup that grows to the width of its longest line is a popup nobody finishes: past about 75
   characters the eye loses the start of the next line. 68ch against the body size lands a little
   under that, and min-width stops a three-word progress line rendering as a sliver. */
.feed-card {
  min-width: 260px;
  max-width: 68ch;
  padding: 12px 14px 13px;

  /* The MUTED default, overridden by the four tones below. A default of `transparent` with a rule
     for every tone would need a class per Quasar colour name, and the two muted ones are exactly
     the colours that have no --q- variable to name. */
  border-left: 3px solid var(--os-rule-strong);
}

/* Header and body are separated by a rule rather than by space alone: the header is metadata
   about the message and the body is the message, and at this density a gap alone reads as one
   block with an odd first line. */
.feed-card-head {
  display: flex;
  align-items: center;
  gap: 8px;
  padding-bottom: 8px;
  margin-bottom: 9px;
  border-bottom: 1px solid var(--os-rule);
}

.feed-card-spacer {
  flex: 1 1 auto;
}

/* WHO said it, which the card cannot get from context: on a dispatch row the source is the
   SENDER rather than the member whose card this is, so a reader following a hop needs it named. */
.feed-card-source {
  font-size: 11px;
  opacity: 0.75;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.feed-card-meta {
  font-size: 11px;
  opacity: 0.6;
  font-variant-numeric: tabular-nums;
}

/* pre-wrap is the single line that makes this readable. The agent wrote paragraphs; `summarise`
   flattens them for the two-line row and this is where they come back. `break-word` because a
   payload can carry a path or a URL with no space in it, and one of those would otherwise push
   the measure out to whatever it happens to be. */
.feed-card-body {
  white-space: pre-wrap;
  overflow-wrap: break-word;
  font-size: 13px;
  line-height: 1.55;

  /* Bounded and scrollable rather than unbounded: a dispatched instruction runs to a couple of
     thousand characters, and a popup taller than the window has a top nobody can reach. */
  max-height: min(46vh, 420px);
  overflow-y: auto;
}

/* The left rule carries the row's own tone through to the card, so a wall of text opened from a
   red row is identifiably the failure before a word of it is read. Colour is the ONLY thing that
   varies - the shape is identical for every type, because the type is already named in the badge
   just above and saying it twice in two languages is not emphasis. */
.feed-card-negative {
  border-left-color: var(--q-negative);
}

.feed-card-warning {
  border-left-color: var(--q-warning);
}

.feed-card-positive {
  border-left-color: var(--q-positive);
}

.feed-card-primary {
  border-left-color: var(--q-primary);
}

</style>
