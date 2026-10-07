<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { useQuasar } from 'quasar';
import { useKanbanStore } from '../stores/kanban';
import { KanbanStatusLabel, bodyToRender, colourFor, trailRows } from '../lib/kanban';
import { workflowNumberTitle } from '../lib/workflowNumber';
import { KanbanStatuses, type KanbanCardAttributes, type KanbanStatus } from '../api/kanban';

/**
 * A card, opened.
 *
 * A SIDE PANEL AND NOT A DIALOG, and the difference is the point: this shows a workflow's whole
 * trail beside the board it came off, so a person can read what happened without losing the shape
 * they were reading it in. A modal card in the middle of the screen would make the board a thing
 * you leave to inspect a card.
 *
 * Every edit here is an APPEND. Title, status, note, move and comment all become `kanban.card.*`
 * messages on the same log the board is projected from, and the board is refetched afterwards - so
 * what appears is the projection's answer and never this component's guess at it. That is also what
 * sets `awaitingManager`, which is why nothing here writes that flag.
 *
 * THERE IS NO "SEND INSTRUCTION" BOX, AND ITS ABSENCE IS THE FEATURE. The server's edit record has
 * no `instruction` property, so such a box would append an edit carrying nothing - a green
 * notification over a no-op. Instead every append below WAKES the team's Manager: saving a note, moving
 * the card or commenting on it are all ways of handing it back, and the Manager decides what to do.
 */
const kanban = useKanbanStore();
const $q = useQuasar();

const open = computed({
  get: () => kanban.selectedId !== '',
  set: (value: boolean) => {
    if (!value) kanban.closeCard();
  },
});

/** The board's copy renders immediately; the fetched detail fills in the trail and attributes. */
const card = computed(() => kanban.detail ?? kanban.selectedCard);
const colour = computed(() => (card.value ? colourFor(card.value) : 'slate'));
/* The word becomes a colour in `app.scss`, the same way it does for the card. */
const colourClass = computed(() => `os-kanban-${colour.value}`);

/**
 * The rest of the instruction, under the title. THE RULE IS IN `lib/kanban.ts` - whether there is
 * anything to draw is a decision, and a decision written as a `v-if` in here is a decision with
 * no test, which is how this half of the card came to be fetched and rendered nowhere at all.
 */
const body = computed(() => bodyToRender(card.value));

const title = ref('');
const status = ref<KanbanStatus | null>(null);
const note = ref('');
const laneId = ref<string | null>(null);
const comment = ref('');

/**
 * The form follows the card, and is RESET on every open rather than merged.
 *
 * A half-typed note left over from the previous card would be posted against this one on the next
 * Save - a human edit attributed to the wrong workflow, which is the worst thing a board whose
 * edits are log messages can do.
 */
watch(
  () => kanban.selectedId,
  () => {
    title.value = card.value?.title ?? '';
    status.value = card.value?.status ?? null;
    laneId.value = card.value?.laneId ?? null;
    note.value = '';
    comment.value = '';
  },
  { immediate: true },
);

// The detail lands after the panel opens, so the fields take their values from it when it does -
// but only while the person has not started typing, which `dirty` below is the whole test for.
watch(
  () => kanban.detail,
  (detail) => {
    if (!detail || dirty.value) return;

    title.value = detail.title;
    status.value = detail.status;
    laneId.value = detail.laneId;
  },
);

const dirty = computed(
  () =>
    title.value !== (card.value?.title ?? '') ||
    status.value !== (card.value?.status ?? null) ||
    note.value !== '',
);

const laneOptions = computed(() =>
  kanban.lanes.map((lane) => ({ label: lane.title, value: lane.id })),
);

const statusOptions = computed(() =>
  KanbanStatuses.map((value) => ({ label: KanbanStatusLabel[value], value })),
);

/**
 * The Trail section's rows: what the server sent, or the card's own progress lines when it sent
 * nothing. The RULE is in `lib/kanban.ts` and tested there - a decision written as a `v-if` here
 * is a decision easier to miss, and a fallback taken on every fetch would go unnoticed.
 */
const trail = computed(() => trailRows(kanban.detail, card.value));

/** The attributes, as rows. Unknown keys are rendered too - see the note in `api/kanban.ts`. */
const attributeRows = computed(() => {
  const attributes: KanbanCardAttributes = kanban.detail?.attributes ?? {};

  return Object.entries(attributes)
    .filter(([, value]) => value !== null && value !== undefined && value !== '')
    .map(([key, value]) => ({
      key,
      label: key.replace(/([a-z])([A-Z])/g, '$1 $2').replace(/^./, (first) => first.toUpperCase()),
      value: Array.isArray(value) ? value.join(', ') : String(value),
    }))
    .filter((row) => row.value !== '');
});

/**
 * One place a failed append is reported.
 *
 * RAISED, NEVER RETRIED, matching the Resume button on the console board: these are messages on an
 * append-only log, and a silent retry would append twice. The person reads what happened and
 * decides whether to press it again.
 */
async function attempt(what: string, action: () => Promise<void>) {
  try {
    await action();
    $q.notify({ type: 'positive', timeout: 3000, message: what });
  } catch (cause) {
    $q.notify({ type: 'negative', message: cause instanceof Error ? cause.message : String(cause) });
  }
}

async function save() {
  const subject = card.value;
  if (!subject) return;

  await attempt('Card updated.', () =>
    kanban.edit(subject.id, {
      ...(title.value && title.value !== subject.title ? { title: title.value } : {}),
      ...(status.value && status.value !== subject.status ? { status: status.value } : {}),
      ...(note.value ? { note: note.value } : {}),
    }),
  );

  note.value = '';
}

async function moveTo() {
  const subject = card.value;
  if (!subject || !laneId.value || laneId.value === subject.laneId) return;

  await attempt('Card moved.', () => kanban.move(subject.id, laneId.value!, note.value || undefined));
  note.value = '';
}

async function postComment() {
  const subject = card.value;
  if (!subject || !comment.value.trim()) return;

  await attempt('Comment added.', () => kanban.comment(subject.id, comment.value.trim()));
  comment.value = '';
}

function when(value: string | null | undefined): string {
  if (!value) return '';

  const parsed = new Date(value);

  return Number.isNaN(parsed.getTime()) ? value : parsed.toLocaleString();
}
</script>

<template>
  <q-dialog v-model="open" position="right" full-height>
    <q-card v-if="card" class="k-panel os-dialog-md" :class="colourClass" :data-colour="colour">
      <q-card-section class="k-panel-head">
        <div class="row items-start no-wrap">
          <div class="col">
            <div class="text-subtitle1">{{ card.title }}</div>
            <div class="text-caption os-text-muted">
              {{ card.team }} / {{ card.member }} · <span :title="workflowNumberTitle(card.workflowSeq)" data-workflow-number>workflow #{{ card.workflowSeq }}</span>
            </div>
          </div>
          <q-btn v-close-popup flat dense round icon="close" aria-label="Close" />
        </div>

        <div class="row items-center q-gutter-xs q-mt-sm">
          <q-chip dense square :label="KanbanStatusLabel[card.status]" class="k-panel-status" />
          <q-chip dense square outline :label="card.laneId" />
          <q-chip
            v-if="card.awaitingManager"
            dense
            square
            label="awaiting manager"
            class="k-panel-awaiting"
          />
        </div>
      </q-card-section>

      <q-separator />

      <q-card-section class="k-panel-body">
        <q-banner v-if="kanban.detailError" dense class="bg-negative text-white q-mb-md">
          {{ kanban.detailError }}
        </q-banner>

        <!-- THE INSTRUCTION, UNDER THE TITLE THAT WAS CUT FROM IT, and unlabelled on purpose: it
             is not a section of the card, it is what the card SAYS, so a heading over it would
             read as one more attribute rather than as the thing itself.

             It sits inside the scrolling section rather than up in the head with the title, which
             is `flex: 0 0 auto` - a long instruction there would push the trail and every control
             off the bottom of the panel with no way to scroll back to them.

             Nothing at all when there is no body, which `bodyToRender` decides. -->
        <template v-if="body">
          <div class="k-panel-description">{{ body }}</div>
          <q-separator class="q-my-md" />
        </template>

        <div class="text-overline">Attributes</div>
        <div v-if="attributeRows.length" class="k-attributes">
          <template v-for="row in attributeRows" :key="row.key">
            <div class="k-attribute-key">{{ row.label }}</div>
            <div class="k-attribute-value">{{ row.value }}</div>
          </template>
        </div>
        <div v-else class="text-caption os-text-muted">
          <span v-if="kanban.detailLoading">Loading…</span>
          <span v-else>
            None reported. Created {{ when(card.createdAt) }}, last moved {{ when(card.updatedAt) }}.
          </span>
        </div>

        <!-- OLDEST FIRST. It is a story: the tell that started the workflow, then what the member
             said as it went, then every move, edit and comment a person made and the note each
             carried. Newest-first is right for a feed you glance at and wrong for a trail you
             read. The server decides the order; `trailRows` decides which rows there are. -->
        <div class="text-overline q-mt-md">Trail</div>
        <div v-if="trail.length" class="k-trail">
          <!-- THE TEXT GETS THE WHOLE WIDTH, on its own line under the stamp and the type.
               Three grid columns gave it whatever was left of a panel - about a third - and a
               trail row is usually a whole instruction, so it came out as a ribbon of two or three
               words a line that nobody reads. The stamp and the type are short and fixed; they
               pair on one line and cost nothing. -->
          <div v-for="row in trail" :key="row.key" class="k-trail-row">
            <div class="k-trail-head">
              <span class="k-trail-when">{{ when(row.at) }}</span>
              <span class="k-trail-type">{{ row.type }}</span>
            </div>
            <div class="k-trail-text">{{ row.text }}</div>
          </div>
        </div>
        <div v-else class="text-caption os-text-muted">
          <span v-if="kanban.detailLoading">Loading…</span>
          <span v-else>Nothing on the log for this card yet.</span>
        </div>

        <q-separator class="q-my-md" />

        <div class="text-overline">Edit</div>
        <q-input v-model="title" dense outlined label="Title" class="q-mb-sm" />

        <div class="row q-col-gutter-sm q-mb-sm">
          <div class="col-6">
            <q-select
              v-model="status"
              :options="statusOptions"
              dense
              outlined
              emit-value
              map-options
              label="Status"
            />
          </div>
          <div class="col-6">
            <q-select
              v-model="laneId"
              :options="laneOptions"
              dense
              outlined
              emit-value
              map-options
              label="Lane"
            />
          </div>
        </div>

        <q-input
          v-model="note"
          dense
          outlined
          autogrow
          label="Note (goes with the edit or the move)"
          class="q-mb-sm"
        />

        <div class="row q-gutter-sm q-mb-md">
          <q-btn
            no-caps
            unelevated
            color="primary"
            label="Save"
            :disable="!dirty || kanban.busy"
            :loading="kanban.busy"
            @click="save"
          />
          <q-btn
            no-caps
            outline
            label="Move"
            :disable="laneId === card.laneId || kanban.busy"
            @click="moveTo"
          />
        </div>

        <q-input v-model="comment" dense outlined autogrow label="Comment" class="q-mb-sm" />
        <q-btn
          no-caps
          outline
          label="Add comment"
          :disable="!comment.trim() || kanban.busy"
          @click="postComment"
        />

        <!-- Said once, under the controls it is about, because it changes what pressing them
             MEANS. Every append above reaches the Manager; a person reading this panel had no way
             to know that, and the button this replaces claimed it while doing nothing.

             QUALIFIED, because the claim is not unconditional. A team with no Manager is a real
             shape - `AGENTS.md` keeps it alive for a converted tenant-agent row - and there the
             append lands on the log and wakes nobody. Stated flatly, a person on such a team
             presses Save, is told the Manager was woken, and watches nothing happen; the record
             half is true either way and is what the sentence leads with. -->
        <div class="text-caption os-text-muted q-mt-md">
          Saving, moving or commenting is recorded on the log, and wakes this team's Manager, which
          decides what to do next. A team with no Manager records it and wakes nobody.
        </div>
      </q-card-section>
    </q-card>
  </q-dialog>
</template>

<style scoped>
.k-panel {
  display: flex;
  flex-direction: column;
  border-left: 4px solid var(--os-ink-faint);
}

.k-panel[data-colour] {
  border-left-color: var(--os-kanban-accent, var(--os-ink-faint));
}

.k-panel-head {
  flex: 0 0 auto;
}

.k-panel-body {
  overflow-y: auto;
}

.k-panel-status {
  background: var(--os-chrome);
}

.k-panel-awaiting {
  background: var(--q-negative);
  color: white;
}

/* `pre-wrap` AND NOT `pre`: the newlines and the indentation an instruction was written with are
   part of what it says - a list, a command example - and the server preserves them all the way
   here on purpose. `pre` would keep them and refuse to wrap, putting a horizontal scrollbar under
   every ordinary paragraph in a panel a third of a screen wide.

   `anywhere` for the wrapping, because a body is as likely to hold a URL or a stack frame as
   prose, and one long token under `pre-wrap` widens the whole panel. */
.k-panel-description {
  white-space: pre-wrap;
  overflow-wrap: anywhere;
  font-size: 13px;
  line-height: 1.45;
  color: var(--os-ink);
}

.k-attributes {
  display: grid;
  grid-template-columns: 10rem 1fr;
  gap: 2px 12px;
  font-size: 12px;
}

.k-attribute-key {
  color: var(--os-ink-muted);
}

.k-attribute-value {
  word-break: break-word;
}

.k-trail-row {
  font-size: 12px;
  padding: 5px 0;
  border-bottom: 1px solid var(--os-rule);
}

/* The stamp and the type, paired. `wrap` rather than `nowrap`: a long message type on a narrow
   panel takes a second line instead of pushing the row into a horizontal scroll. */
.k-trail-head {
  display: flex;
  flex-wrap: wrap;
  gap: 4px 8px;
  align-items: baseline;
}

.k-trail-when,
.k-trail-type {
  color: var(--os-ink-muted);
  font-family: 'IBM Plex Mono', monospace;
  font-size: 11px;
}

.k-trail-text {
  word-break: break-word;
  margin-top: 2px;
}
</style>
