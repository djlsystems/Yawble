<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue';
import { listMemberRuns, NoLiveView, openLiveView, readRunTranscript } from '../api/client';
import type { MemberId, MemberRun, TeamId } from '../api/types';
import { liveLineTime, parseLiveLine, type LiveLine } from '../lib/liveLines';
import { runDuration, runItemsText, runItemText, runOutcomeLabel, runStartedText, runStartedTitle } from '../lib/memberRuns';
import { QuietMark, WorkflowDeclaredMark } from '../lib/runMarks';
import { workflowNumberTitle } from '../lib/workflowNumber';

/**
 * WATCHING ONE MEMBER. Two parts: at the top, the run in progress, streaming as it
 * happens, or "Not running" when the member is idle; below it, **Earlier runs**, this member's
 * finished runs newest first, any of which opens its whole transcript.
 *
 * The server sends display lines that are already readable, and each is appended as it arrives with
 * its words untouched. What changes is how it is SHOWN: one row per step, striped, with the tool's
 * name as a chip (`lib/liveLines.ts`), the agent's own words in the reading face and a tool's result
 * muted beneath its call, each with the time the server stamped on it, in the viewer's own zone. The
 * agent's attachments - token counts, tool lists - are hidden until asked for, and say how many
 * there are. An earlier run's transcript is shown in exactly the same form; it is read whole, not
 * streamed.
 *
 * THE LIVE VIEW FOLLOWS THE BOTTOM UNTIL THE PERSON SCROLLS UP, and then leaves them alone until
 * they are back at the bottom. The member card's own feed has no auto-scroll for exactly the reason
 * this one stops: yanking the view while somebody reads an earlier line is what makes a live log
 * unusable. Here the newest line is at the bottom, so following is the default rather than absent.
 *
 * NEVER A BLANK SPINNER. A member that is not running (404) and a preset with no live view
 * (`live:false`) both answer with a sentence, and the sentence is what is shown; an empty one is
 * replaced so the box is never blank. While `/live` is still pending - the Host can hold it up to
 * 30s while it finds a Codex transcript - the dialog says "Waiting for the agent to start its session", from the first
 * moment rather than after a delay: for an agent whose transcript is already known it is gone
 * before it can be read, and for one still starting it is the truth. When the stream
 * ends the dialog says the run ended and keeps every line until it is closed. A transcript that is
 * no longer on disk (410) shows the server's sentence in place of its lines.
 *
 * A PLUGIN MEMBER (`plugin`) has no live view and no transcript: the dialog is its earlier runs
 * alone, and a run opens onto what it reported (the route's `output`), not a transcript.
 */
const props = withDefaults(
  defineProps<{
    team: TeamId;
    member: MemberId;
    name: string;
    /** Whether the member has a run in flight. Idle opens onto "Not running" without asking `/live`. */
    running?: boolean;
    /** A plugin member: earlier runs only, each opening onto its output. Never asks `/live`. */
    plugin?: boolean;
  }>(),
  { running: true, plugin: false },
);

const open = defineModel<boolean>({ required: true });

type Phase = 'idle' | 'connecting' | 'streaming' | 'ended' | 'unavailable';

const lines = ref<LiveLine[]>([]);
const showNotes = ref(false);

/** What is shown: every line, or every line but the attachments. */
const visible = (all: LiveLine[]) => (showNotes.value ? all : all.filter((line) => line.kind !== 'aside'));
const shown = computed(() => visible(lines.value));
const phase = ref<Phase>('idle');
const reason = ref('');
const following = ref(true);
const scroller = ref<HTMLElement | null>(null);

let controller: AbortController | null = null;

/** A couple of pixels of slack: a fractional scrollTop on a scaled display is never exactly equal. */
const BottomSlack = 4;

function atBottom(el: HTMLElement) {
  return el.scrollHeight - el.scrollTop - el.clientHeight <= BottomSlack;
}

function onScroll() {
  if (scroller.value) following.value = atBottom(scroller.value);
}

async function stickToBottom() {
  if (!following.value) return;
  await nextTick();
  const el = scroller.value;
  if (el) el.scrollTop = el.scrollHeight;
}

async function watchRun() {
  controller?.abort();
  const mine = new AbortController();
  controller = mine;

  lines.value = [];
  reason.value = '';
  following.value = true;
  phase.value = 'connecting';

  try {
    const view = await openLiveView(props.team, props.member, mine.signal);

    if (!view.live) {
      reason.value = view.reason;
      phase.value = 'unavailable';
      return;
    }

    phase.value = 'streaming';

    for await (const line of view.lines) {
      if (mine.signal.aborted) return;
      lines.value.push(parseLiveLine(line));
      void stickToBottom();
    }

    if (!mine.signal.aborted) phase.value = 'ended';
  } catch (cause) {
    if (mine.signal.aborted) return;

    // Before the first line this is the 404's own sentence; after it, the connection dropped, and
    // the lines already shown stay.
    reason.value = (cause instanceof Error ? cause.message : String(cause)) || (lines.value.length > 0 ? '' : NoLiveView);
    phase.value = lines.value.length > 0 ? 'ended' : 'unavailable';
  }
}

function stop() {
  controller?.abort();
  controller = null;
}

/**
 * EARLIER RUNS: pages of 20, newest first, the next older page on request. Each load carries a
 * generation so a page that arrives after the dialog closed, or after a reload, is dropped rather
 * than appended to a list it no longer belongs to.
 */
const runs = ref<MemberRun[]>([]);
const nextBefore = ref<number | null>(null);
const runsLoading = ref(false);
const runsError = ref('');
let runsGeneration = 0;

async function loadRuns(older: boolean) {
  const generation = older ? runsGeneration : ++runsGeneration;
  if (!older) {
    runs.value = [];
    nextBefore.value = null;
  }
  runsError.value = '';
  runsLoading.value = true;

  try {
    const page = await listMemberRuns(props.team, props.member, older ? nextBefore.value : null);
    if (generation !== runsGeneration) return;
    runs.value = older ? [...runs.value, ...page.runs] : page.runs;
    nextBefore.value = page.nextBefore;
  } catch (cause) {
    if (generation !== runsGeneration) return;
    runsError.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    if (generation === runsGeneration) runsLoading.value = false;
  }
}

/** The earlier run being read, its lines, and the sentence when its transcript is gone. */
const selected = ref<MemberRun | null>(null);
const transcript = ref<LiveLine[]>([]);
const transcriptLoading = ref(false);
const transcriptReason = ref('');
const shownTranscript = computed(() => visible(transcript.value));

async function openRun(run: MemberRun) {
  selected.value = run;
  transcript.value = [];
  transcriptReason.value = '';

  // A plugin's run is its output, already in the list: nothing to fetch.
  if (props.plugin) return;

  transcriptLoading.value = true;

  try {
    const raw = await readRunTranscript(props.team, props.member, run.seq);
    if (selected.value !== run) return;
    transcript.value = raw.map(parseLiveLine);
  } catch (cause) {
    if (selected.value !== run) return;
    transcriptReason.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    if (selected.value === run) transcriptLoading.value = false;
  }
}

function closeRun() {
  selected.value = null;
  transcript.value = [];
  transcriptReason.value = '';
}

/** The notes toggle covers both views: the live lines and, when one is open, an earlier run's. */
const hasLines = computed(
  () => phase.value === 'streaming' || phase.value === 'ended' || (selected.value !== null && transcript.value.length > 0),
);
const hiddenNotes = computed(
  () =>
    lines.value.length - shown.value.length + (transcript.value.length - shownTranscript.value.length),
);

function begin() {
  if (props.running && !props.plugin) void watchRun();
  else {
    stop();
    phase.value = 'idle';
  }
  closeRun();
  void loadRuns(false);
}

function end() {
  stop();
  runsGeneration++;
  closeRun();
}

watch(open, (showing) => {
  if (showing) begin();
  else end();
});

/**
 * THE MEMBER STARTS OR STOPS WHILE THE DIALOG IS OPEN. Starting is followed: the top switches from
 * "Not running" to the new run. Stopping leaves the live lines where they are - the stream says the
 * run ended by itself - and reloads the newest page, where the run just finished now belongs.
 */
watch(
  () => props.running,
  (now, before) => {
    if (!open.value) return;
    if (now && !before && !props.plugin && phase.value !== 'connecting' && phase.value !== 'streaming') void watchRun();
    if (!now && before) void loadRuns(false);
  },
);

onBeforeUnmount(end);
</script>

<template>
  <q-dialog v-model="open">
    <q-card class="os-dialog-lg live-view-card">
      <q-card-section class="row items-center q-pb-none">
        <div class="os-dialog-title ellipsis">{{ plugin ? `Runs of ${name}` : `Watching ${name}` }}</div>
        <q-space />
        <q-toggle
          v-if="hasLines"
          v-model="showNotes"
          dense
          size="sm"
          class="q-mr-sm live-view-notes"
          :label="showNotes || hiddenNotes === 0 ? 'Notes' : `Notes (${hiddenNotes} hidden)`"
        />
        <q-btn v-close-popup flat round dense icon="close" aria-label="Close" />
      </q-card-section>

      <!-- A plugin has no live section: nothing above its earlier runs. -->
      <template v-if="plugin"></template>

      <q-card-section v-else-if="phase === 'idle'" class="os-body os-text-muted live-view-idle">
        Not running
      </q-card-section>

      <q-card-section v-else-if="phase === 'connecting'" class="row items-center q-gutter-sm os-body os-text-muted">
        <q-spinner size="1.2em" />
        <span class="live-view-waiting">Waiting for the agent to start its session.</span>
      </q-card-section>

      <q-card-section v-else-if="phase === 'unavailable'" class="os-body live-view-reason">
        {{ reason }}
      </q-card-section>

      <q-card-section v-else class="q-pt-sm">
        <div ref="scroller" class="live-view-lines" @scroll="onScroll">
          <div v-for="(line, index) in shown" :key="index" class="live-view-line" :data-kind="line.kind">
            <span v-if="line.at" class="live-view-time" :title="line.at.toLocaleString()">{{ liveLineTime(line.at) }}</span>
            <span v-if="line.label" class="live-view-label">{{ line.label }}</span>
            <span class="live-view-text">{{ line.text }}</span>
          </div>
        </div>

        <div v-if="phase === 'ended'" class="os-body os-text-muted q-mt-sm live-view-ended">
          The run ended.<template v-if="reason"> {{ reason }}</template>
        </div>
        <div v-else-if="!following" class="text-caption os-text-muted q-mt-xs">
          Paused at your position. Scroll to the bottom to follow again.
        </div>
      </q-card-section>

      <q-separator v-if="!plugin" />

      <q-card-section class="earlier-runs">
        <div class="row items-center no-wrap q-mb-xs">
          <q-btn
            v-if="selected"
            flat
            dense
            round
            size="sm"
            icon="arrow_back"
            aria-label="Back to earlier runs"
            class="q-mr-xs earlier-runs-back"
            @click="closeRun"
          />
          <div class="text-subtitle2">Earlier runs</div>
          <div v-if="selected" class="q-ml-sm text-caption os-text-muted ellipsis earlier-run-heading">
            {{ runStartedText(selected.startedAt) }}<template v-if="selected.workflow !== null">
              · <span :title="workflowNumberTitle(selected.workflow)" data-workflow-number>workflow #{{ selected.workflow }}</span></template> · {{ runDuration(selected.durationMs) }} ·
            {{ runOutcomeLabel(selected.outcome) }}
          </div>
        </div>

        <!-- A RUN OF SEVERAL ITEMS says what became of each, a deferral included: never one
             outcome for all of them. A deferred item's own run says where it came from. -->
        <div v-if="selected?.items" class="os-body q-mb-xs run-items">
          <div
            v-for="entry in selected.items"
            :key="entry.seq"
            class="run-item"
            :data-outcome="entry.outcome"
          >{{ runItemText(entry) }}</div>
        </div>
        <div v-if="selected && selected.deferredFromRun !== null" class="os-body os-text-muted q-mb-xs run-deferred-from">
          Deferred from run {{ selected.deferredFromRun }}.
        </div>

        <template v-if="selected && plugin">
          <!-- A run that blocked every item wrote no output row: what it said is the block's reason. -->
          <div v-if="selected.reason" class="os-body q-mb-xs run-reason">Blocked: {{ selected.reason }}</div>
          <div v-if="selected.output" class="live-view-lines run-output">{{ selected.output }}</div>
          <div v-else-if="!selected.reason" class="os-body os-text-muted run-output-none">This run reported no output.</div>
        </template>

        <template v-else-if="selected">
          <div v-if="transcriptLoading" class="row items-center q-gutter-sm os-body os-text-muted">
            <q-spinner size="1.2em" />
            <span>Reading the transcript…</span>
          </div>
          <div v-else-if="transcriptReason" class="os-body run-transcript-reason">{{ transcriptReason }}</div>
          <div v-else-if="transcript.length === 0" class="os-body os-text-muted">This run's transcript is empty.</div>
          <div v-else class="live-view-lines run-transcript-lines">
            <div
              v-for="(line, index) in shownTranscript"
              :key="index"
              class="live-view-line run-transcript-line"
              :data-kind="line.kind"
            >
              <span v-if="line.at" class="live-view-time" :title="line.at.toLocaleString()">{{ liveLineTime(line.at) }}</span>
              <span v-if="line.label" class="live-view-label">{{ line.label }}</span>
              <span class="live-view-text">{{ line.text }}</span>
            </div>
          </div>
        </template>

        <template v-else>
          <q-list v-if="runs.length > 0" dense separator class="earlier-runs-list">
            <q-item
              v-for="run in runs"
              :key="run.seq"
              v-ripple
              clickable
              class="earlier-run"
              :data-seq="run.seq"
              @click="openRun(run)"
            >
              <q-item-section>
                <q-item-label class="earlier-run-when" :title="runStartedTitle(run.startedAt)">
                  {{ runStartedText(run.startedAt) }}
                </q-item-label>
                <q-item-label v-if="run.items" caption class="earlier-run-items">
                  items {{ runItemsText(run.items) }}
                </q-item-label>
                <q-item-label v-if="run.deferredFromRun !== null" caption class="earlier-run-deferred-from">
                  deferred from run {{ run.deferredFromRun }}
                </q-item-label>
              </q-item-section>
              <q-item-section side class="earlier-run-workflow" :title="run.workflow !== null ? workflowNumberTitle(run.workflow) : undefined">
                {{ run.workflow !== null ? `workflow #${run.workflow}` : 'no workflow' }}
              </q-item-section>
              <q-item-section side class="earlier-run-duration">{{ runDuration(run.durationMs) }}</q-item-section>
              <q-item-section side class="row no-wrap items-center">
                <!-- A QUIET RUN woke nobody when it finished: said here, beside how it ended, so a
                     list of runs nobody was told about is not mistaken for missed work. -->
                <span v-if="run.quiet" class="earlier-run-quiet text-caption os-text-muted q-mr-xs">
                  {{ QuietMark.label }}
                  <q-tooltip>{{ QuietMark.tooltip }}</q-tooltip>
                </span>
                <!-- A WORKFLOW THE PLATFORM DECLARED as this run ended: marked as a quiet run is, so a
                     person sees why the Manager did not run. -->
                <span v-else-if="run.workflowDeclared" class="earlier-run-declared text-caption os-text-muted q-mr-xs">
                  {{ WorkflowDeclaredMark.label }}
                  <q-tooltip>{{ WorkflowDeclaredMark.tooltip }}</q-tooltip>
                </span>
                <q-badge class="earlier-run-outcome" :data-outcome="run.outcome" :label="runOutcomeLabel(run.outcome)" />
              </q-item-section>
            </q-item>
          </q-list>

          <div v-if="runsError" class="os-body text-negative q-mt-xs">{{ runsError }}</div>
          <div v-else-if="runsLoading" class="row items-center q-gutter-sm os-body os-text-muted q-mt-xs">
            <q-spinner size="1.2em" />
            <span>Loading runs…</span>
          </div>
          <div v-else-if="runs.length === 0" class="os-body os-text-muted earlier-runs-none">
            No earlier runs to show.
          </div>

          <q-btn
            v-if="nextBefore !== null && !runsLoading"
            flat
            dense
            no-caps
            size="sm"
            class="q-mt-xs earlier-runs-older"
            label="Load older runs"
            @click="loadRuns(true)"
          />
        </template>
      </q-card-section>
    </q-card>
  </q-dialog>
</template>

<style scoped>
/* A plugin run's output: its own words, verbatim, wrapped rather than cut. */
.run-output {
  padding: 8px;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
  font-family: var(--os-mono);
}

.live-view-lines {
  height: min(36vh, 340px);
  overflow-y: auto;
  border: 1px solid var(--os-rule);
  border-radius: 4px;
  font-size: 12px;
}

/* ONE ROW PER STEP, STRIPED, so a wrapped command reads as one step and not three. */
.live-view-line {
  display: flex;
  gap: 8px;
  align-items: baseline;
  padding: 4px 8px;
  border-left: 3px solid transparent;
}

.live-view-line:nth-child(even) {
  background: var(--os-chrome);
}

/* Verbatim: the server's spacing and long lines are kept, wrapped rather than cut. */
.live-view-text {
  flex: 1;
  min-width: 0;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
  font-family: var(--os-mono);
}

/* When the step happened, in the viewer's time: a quiet column the eye can run down. */
.live-view-time {
  flex: none;
  font-family: var(--os-mono);
  font-size: 11px;
  color: var(--os-ink-faint);
  font-variant-numeric: tabular-nums;
}

.live-view-label {
  flex: none;
  min-width: 44px;
  padding: 0 6px;
  border-radius: 3px;
  background: var(--os-tint-info);
  color: var(--os-ink);
  font-family: var(--os-mono);
  font-size: 11px;
  font-weight: 600;
  text-align: center;
}

/* The agent speaking: the reading face, full ink, marked at the edge - the lines a person reads. */
.live-view-line[data-kind='text'] {
  border-left-color: var(--q-primary);
}

.live-view-line[data-kind='text'] .live-view-text {
  font-family: inherit;
  font-size: 13px;
  color: var(--os-ink);
}

/* A tool's result belongs to the call above it: indented under the chip, muted. */
.live-view-line[data-kind='result'] .live-view-text {
  padding-left: 52px;
  color: var(--os-ink-muted);
}

.live-view-line[data-kind='user'] .live-view-label {
  background: var(--os-tint-warn);
}

.live-view-line[data-kind='aside'] .live-view-label {
  background: transparent;
  border: 1px solid var(--os-rule-strong);
}

.live-view-line[data-kind='aside'] .live-view-text {
  color: var(--os-ink-faint);
}

/* EARLIER RUNS: one row per run, the columns lined up so a person can run their eye down times,
   durations and outcomes. */
.earlier-runs-list {
  max-height: min(30vh, 280px);
  overflow-y: auto;
  border: 1px solid var(--os-rule);
  border-radius: 4px;
  font-size: 12px;
}

.earlier-run-when,
.earlier-run-duration {
  font-variant-numeric: tabular-nums;
}

.earlier-run-duration {
  min-width: 64px;
  text-align: right;
}

.earlier-run-outcome {
  background: var(--os-tint-info);
  color: var(--os-ink);
}

.earlier-run-outcome[data-outcome='blocked'] {
  background: var(--os-tint-warn);
}

.earlier-run-outcome[data-outcome='failed'] {
  background: var(--os-tint-error);
}
</style>
