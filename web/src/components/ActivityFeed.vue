<script setup lang="ts">
import { computed, ref } from 'vue';
import { getMessagesBefore } from '../api/client';
import type { Message } from '../api/types';
import { detail, label, ownerOf, summarise, timeOf } from '../lib/summarise';
import { solutionNoticeOf } from '../lib/solutionNotice';
import { runMarkOf } from '../lib/runMarks';
import { useCursorList } from '../lib/useCursorList';
import { useConsoleStore } from '../stores/console';
import CursorSentinel from './CursorSentinel.vue';

/**
 * `team`, `name` and `sinceSeq` name whose feed this is, and turn on "load older". Without
 * them the feed is the live tail only.
 */
const props = defineProps<{ feed: Message[]; team?: string; name?: string; sinceSeq?: number }>();

/**
 * THE MESSAGE THE DIALOG SHOWS, by seq - a click (or a tap) on a row opens it, and Newer / Older
 * step through this card's rows in the order the card lists them, newest first.
 *
 * Held by seq rather than by index because the live feed grows at the top while the dialog is
 * open: an index would slide the dialog onto whatever just arrived, and a person reading a report
 * would find it replaced under them.
 */
const shownSeq = ref<number | null>(null);

/**
 * The message as it was when shown. The card keeps a rolling window, so a row can fall out of
 * `feed` while it is open; the dialog then keeps showing it, with no neighbours to step to, rather
 * than vanishing mid-read.
 */
const held = ref<Message | null>(null);

const shownIndex = computed(() =>
  shownSeq.value === null ? -1 : props.feed.findIndex((message) => message.seq === shownSeq.value));

const shown = computed<Message | null>(() =>
  shownIndex.value >= 0 ? props.feed[shownIndex.value]! : shownSeq.value === null ? null : held.value);

const newer = computed(() => (shownIndex.value > 0 ? props.feed[shownIndex.value - 1]! : null));
const older = computed(() =>
  shownIndex.value >= 0 && shownIndex.value < props.feed.length - 1 ? props.feed[shownIndex.value + 1]! : null);

function show(message: Message | null) {
  if (message === null) return;

  shownSeq.value = message.seq;
  held.value = message;
}

function close() {
  shownSeq.value = null;
  held.value = null;
}

/** Left and Right step, like the buttons; Up and Down stay with the body, which scrolls. */
function onKey(event: KeyboardEvent) {
  if (event.key === 'ArrowLeft') show(newer.value);
  else if (event.key === 'ArrowRight') show(older.value);
  else return;

  event.preventDefault();
}

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
const history = useCursorList<Message>(
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
      clickable
      :aria-label="`Open ${label(message.type)} at ${timeOf(message.occurredAt)}`"
      @click="show(message)"
    >
      <q-item-section side top>
        <q-badge :color="toneOf(message)" :label="label(message.type)" />
      </q-item-section>

      <q-item-section>
        <!-- Interpolated, never innerHTML: agent output reaches this line and Vue
             escapes it. -->
        <q-item-label caption lines="2" class="feed-line">{{ summarise(message) || '—' }}</q-item-label>
        <!-- WHY A FINISHED RUN WOKE NOBODY: a quiet run, or one whose workflow the platform declared
             as it ended. Said on the row, so a Manager that did not run is not mistaken for a miss. -->
        <q-item-label v-if="runMarkOf(message)" caption class="feed-run-mark os-text-muted">
          {{ runMarkOf(message)!.label }}
          <q-tooltip>{{ runMarkOf(message)!.tooltip }}</q-tooltip>
        </q-item-label>
        <!-- A package ready to install: the wizard's link, composed from the folder and never read
             from the row's text. It opens the review; nothing installs from here. -->
        <q-item-label v-if="solutionNoticeOf(message)?.href" caption>
          <a class="feed-install text-weight-bold" :href="solutionNoticeOf(message)!.href!" @click.stop>Review and install</a>
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
          @click.stop="board.toggleWorkflow(message.correlationId)"
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
      :loading="history.loading.value"
      :exhausted="history.exhausted.value"
      :error="history.error.value"
      :done-label="feed.length ? 'The first message.' : ''"
      :manual="history.cursor.value === undefined"
      @more="history.loadMore"
    />
  </q-list>

  <!-- ONE MESSAGE, READ IN FULL. Opened by a click or a tap on a row - never by hover, which a phone
       does not have and which put a card over every line the pointer crossed. -->
  <q-dialog :model-value="shown !== null" @update:model-value="(open: boolean) => { if (!open) close(); }">
    <!-- The tone classes are written out LITERALLY rather than composed as `feed-card-${tone}`:
         styles-match-templates greps the template for each styled class, and a composed name is
         invisible to it. The muted default lives on .feed-card itself. -->
    <q-card
      v-if="shown"
      class="os-dialog-md feed-card"
      :class="{
        'feed-card-negative': toneOf(shown) === 'negative',
        'feed-card-warning': toneOf(shown) === 'warning',
        'feed-card-positive': toneOf(shown) === 'positive',
        'feed-card-primary': toneOf(shown) === 'primary',
      }"
      data-feed-dialog
      @keydown="onKey"
    >
      <q-card-section class="feed-card-nav">
        <q-btn
          flat
          dense
          no-caps
          icon="chevron_left"
          label="Newer"
          :disable="newer === null"
          aria-label="Newer: the message above"
          data-feed-previous
          @click="show(newer)"
        />
        <span class="feed-card-position os-text-muted" data-feed-position>
          <template v-if="shownIndex >= 0">{{ shownIndex + 1 }} of {{ feed.length }}</template>
        </span>
        <q-btn
          flat
          dense
          no-caps
          icon-right="chevron_right"
          label="Older"
          :disable="older === null"
          aria-label="Older: the message below"
          data-feed-next
          @click="show(older)"
        />
        <span class="feed-card-spacer"></span>
        <q-btn v-close-popup flat dense round icon="close" aria-label="Close" />
      </q-card-section>

      <q-card-section class="feed-card-head">
        <q-badge :color="toneOf(shown)" :label="label(shown.type)" />
        <span class="feed-card-source mono">{{ shown.source }}</span>
        <span class="feed-card-spacer"></span>
        <span class="feed-card-meta mono">#{{ shown.correlationId }}</span>
        <span class="feed-card-meta mono">{{ timeOf(shown.occurredAt) }}</span>
      </q-card-section>

      <!-- Interpolated, exactly like the row it came from. Agent output reaches this, and a
           markdown renderer here would be a way for a member to write into the page. -->
      <q-card-section class="feed-card-body" data-feed-body>{{ detail(shown) || '—' }}</q-card-section>

      <q-card-section v-if="solutionNoticeOf(shown)?.href" class="q-pt-none">
        <a class="feed-install text-weight-bold" :href="solutionNoticeOf(shown)!.href!">Review and install</a>
      </q-card-section>
    </q-card>
  </q-dialog>
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

/* THE DIALOG'S WIDTH is the dialog scale's (`os-dialog-md`), never set here. What this sets is the
   tone: a left rule, MUTED by default and overridden by the four tones below. A default of
   `transparent` with a rule for every tone would need a class per Quasar colour name, and the two
   muted ones are exactly the colours that have no --q- variable to name. */
.feed-card {
  border-left: 3px solid var(--os-rule-strong);

  /* ONE HEIGHT FOR EVERY MESSAGE, so Newer and Older stay under the pointer. The dialog is
     centred, and a card that took each message's own height moved its header - and the buttons in
     it - up and down the screen at every step. The body takes what is left and scrolls. */
  height: min(78vh, 680px);
  display: flex;
  flex-direction: column;
}

/* Newer and Older sit together at the top, where a thumb or a pointer finds them without moving
   as the body below changes length from one message to the next. */
.feed-card-nav {
  display: flex;
  align-items: center;
  gap: 4px;
  padding-bottom: 0;
}

.feed-card-position {
  font-size: 12px;
  font-variant-numeric: tabular-nums;
  min-width: 5.5em;
  text-align: center;
}

/* Header and body are separated by a rule rather than by space alone: the header is metadata
   about the message and the body is the message, and at this density a gap alone reads as one
   block with an odd first line. */
.feed-card-head {
  display: flex;
  align-items: center;
  gap: 8px;
  padding-bottom: 8px;
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

  /* The rest of the card's fixed height, scrolling: a dispatched instruction runs to a couple of
     thousand characters. `min-height: 0` is what lets a flex child shrink below its content and
     so scroll at all. */
  flex: 1 1 auto;
  min-height: 0;
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
