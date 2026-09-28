<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue';
import type { ContainerSnapshot, Message } from '../api/types';
import { listContainerTriggers, stopRun } from '../api/client';
import { containerMark } from '../lib/teamKpis';
import { useWipStore } from '../stores/wip';
import { failureClassLabel, failureClassWords, resumeTimeText } from '../lib/failureClass';
import ActivityFeed from './ActivityFeed.vue';
import LiveViewDialog from './LiveViewDialog.vue';
import MemberSettingsDialog from './MemberSettingsDialog.vue';
import TriggersDialog from './TriggersDialog.vue';

const props = defineProps<{
  snapshot: ContainerSnapshot;
  feed: Message[];
}>();

/**
 * The way into `MemberSettingsDialog`, nested here rather than hoisted to the layout - it needs
 * nothing this card does not already hold.
 */
const settingsOpen = ref(false);

/**
 * The way into `TriggersDialog`, nested exactly like the settings dialog above for the same
 * reason - this card already holds everything the dialog's props ask for: `snapshot.team` and
 * `snapshot.id` (already branded `TeamId`; there is no raw string here to run through `asTeamId`,
 * which is for edges only), and `snapshot.subscribes` too, so the dialog can show the member's
 * BUILT-IN wake sources alongside its trigger rows rather than only the latter.
 *
 * `triggerCount` is a SEPARATE read from the dialog's own rows: the badge has to answer "are there
 * any?" without a click, so it is fetched once on mount and again whenever the dialog CLOSES - that
 * is the only moment an add, edit or delete inside it could have changed the answer. A failed count
 * fetch is swallowed rather than surfaced here; opening the dialog shows the real error if there is
 * one, and a card-wide banner for a badge nobody asked to see explicitly would be the wrong scale of
 * complaint.
 */
const triggersOpen = ref(false);
const triggerCount = ref(0);

async function loadTriggerCount() {
  try {
    const rows = await listContainerTriggers(props.snapshot.team, props.snapshot.id);
    triggerCount.value = rows.length;
  } catch {
    // Silent - see the comment above.
  }
}

onMounted(loadTriggerCount);

watch(triggersOpen, (showing) => {
  if (showing) return;
  void loadTriggerCount();
});

const running = computed(() => props.snapshot.state === 'Running');

/**
 * A PLUGIN MEMBER runs no model: it has no tokens, no spend and no transcript to watch. Its card
 * shows no eye and says its failures without provider or spend words; its earlier runs, each with
 * what it reported, open from a history button instead. Absent `kind` is an agent, from an older Host.
 */
const isPlugin = computed(() => props.snapshot.kind === 'plugin');

/** What this member runs, as the card's second line names it: the plugin id, or the Agent. */
const runsWhat = computed(() =>
  isPlugin.value
    ? { word: 'plugin', name: props.snapshot.agent.replace(/^plugin:/, '') }
    : { word: 'agent', name: props.snapshot.agent },
);

/**
 * The way into `LiveViewDialog`, offered whenever the member is `watchable`, running
 * or idle: the dialog shows the run in flight, or "Not running", above the member's earlier runs.
 * It is left open when the run ends, because it says so itself and keeps the lines; closing it is
 * the person's decision.
 */
const watchOpen = ref(false);

/**
 * HELD BEHIND THE WIP LIMIT: this member has work to start and every slot is taken. The snapshot's
 * `held`, or the ledger naming it waiting - the ledger's poll often lands before the next push.
 */
const wip = useWipStore();
const waiting = computed(
  () => !running.value && (props.snapshot.held === true || wip.isWaiting(props.snapshot.team, props.snapshot.id)),
);

/**
 * THE ONE TERMINAL MARK THIS CARD SHOWS, chosen in `lib/teamKpis.ts` rather than by the order of
 * three `v-if`s here.
 *
 * A run can leave two - an agent gives up and publishes `blocked`, then its process dies and the
 * platform publishes `failed` - and the ranking that resolves it has to be the SAME ranking
 * `teamStatus` gives the tab chip, or a chip and the card beneath it disagree about this member.
 * A precedence written as template order is a precedence a test only sees after the fact, so it
 * lives in `containerMark`.
 */
const mark = computed(() => containerMark(props.snapshot));

/**
 * A container's feed starts when the container does.
 *
 * Names are global and the log outlives any team, so without this a fresh team's Manager card shows
 * a previous run's conversation. The same floor the ledger uses,
 * applied to what a human sees.
 */
const own = computed(() => props.feed.filter((m) => m.seq > props.snapshot.sinceSeq));

/**
 * At-ceiling gets its own treatment, distinct from busy. Busy is normal;
 * at-ceiling means work is being REFUSED and a human may want to intervene. If it
 * looks like busy, the rejection is the only thing that ever notices.
 */
const atCeiling = computed(() => props.snapshot.queueDepth >= props.snapshot.ceiling);

/**
 * Stop is DESTRUCTIVE and confirms INLINE, in two steps, rather than through `$q.dialog()`.
 *
 * The Dialog plugin is deliberately absent from `quasar.config.ts`, and the reason is recorded
 * there: its `class` option lands on the inner card rather than the dialog root, so a confirmation
 * opened from inside a lifted panel cannot be raised above it and paints behind the terminal. A
 * destructive action whose confirmation is invisible is the worst version of that bug - the click
 * appears to do nothing, so the natural response is to click again.
 *
 * Two steps rather than one because ending somebody's run half-finished is not undoable.
 */
const confirming = ref(false);
const stopping = ref(false);
const stopError = ref('');

async function stop() {
  stopError.value = '';
  stopping.value = true;

  try {
    // `stopped: false` is not an error. The run may have finished between the click and the call,
    // and saying so is more honest than a silent no-op or a red banner over nothing wrong.
    //
    // ADDRESSED WITH `id`, THE WAY `listContainerTriggers` ABOVE ALREADY DOES IT. This passed
    // `name`, which is the LABEL - so a member called `Researcher Rhea` produced
    // `/containers/Researcher%20Rhea/stop`, `ContainerId`'s allowlist refused the space, and the
    // route answered 500 rather than 404. A member whose label equals its id, like `Manager`,
    // stopped perfectly, which is what kept this hidden: the button worked for the member you
    // tried it on first.
    const result = await stopRun(props.snapshot.team, props.snapshot.id);

    if (!result.stopped) stopError.value = result.message;

    confirming.value = false;
  } catch (cause) {
    stopError.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    stopping.value = false;
  }
}
</script>

<template>
  <MemberSettingsDialog v-model="settingsOpen" :snapshot="snapshot" />
  <TriggersDialog
    v-model="triggersOpen"
    :team="snapshot.team"
    :container="snapshot.id"
    :subscribes="snapshot.subscribes"
    :member-kind="snapshot.kind ?? 'agent'"
    :member-agent="snapshot.agent ?? ''"
  />
  <LiveViewDialog
    v-model="watchOpen"
    :team="snapshot.team"
    :member="snapshot.id"
    :name="snapshot.name"
    :running="running"
    :plugin="isPlugin"
  />

  <q-card flat bordered class="container-window" :class="{ 'container-window-plugin': isPlugin }">
    <q-card-section class="q-pb-xs">
      <div class="row items-center no-wrap q-gutter-sm">
        <!-- THE LABEL, AND ONLY THE LABEL. `snapshot.name` is NOT the identifier - `Containers.cs`
             and `types.ts` agree: "Render this; address with `id`". A route addressed by the label
             fails for every member whose label carries a space. -->
        <div class="col text-subtitle1 text-weight-medium ellipsis">{{ snapshot.name }}</div>

        <!-- Running is its OWN green rather than the brand orange, and a different one from the
             `completed` badges in the feed below (Quasar `positive`). Two states that both mean
             "went well" should not be the same colour when one is still happening: a person
             scanning a column of green needs to see which row is alive.

             The pulsing dot is what actually carries that - colour alone is a weak signal at badge
             size, and it is the one cue that reads at a glance from across a board. -->
        <q-badge v-if="running" class="container-running">
          <span class="container-running-dot" aria-hidden="true"></span>
          running
        </q-badge>
        <q-badge v-else-if="waiting" color="warning" text-color="dark" label="waiting for a slot">
          <q-tooltip>Every running slot is taken. This member starts when one is released; nothing was refused.</q-tooltip>
        </q-badge>
        <q-badge v-else color="grey-6" label="idle" />

        <!-- Only while something is RUNNING. A Stop on an idle card is a control with nothing to
             act on, and the server says so politely rather than failing - but offering it at all
             invites the click. -->
        <!-- THE EYE FOLLOWS `watchable`, NOT `running`: an idle member's earlier runs are
             still there to read. A member whose agent has no live view shows none, rather than an
             eye that opens onto "no live view". -->
        <q-btn
          v-if="snapshot.watchable && !isPlugin"
          flat
          dense
          round
          size="sm"
          icon="visibility"
          aria-label="Watch this member"
          @click="watchOpen = true"
        >
          <q-tooltip>{{ running ? 'Watch this run and earlier ones' : 'Earlier runs' }}</q-tooltip>
        </q-btn>
        <!-- A PLUGIN HAS NO LIVE VIEW, but it has earlier runs, each with what it reported. -->
        <q-btn
          v-if="isPlugin"
          flat
          dense
          round
          size="sm"
          icon="history"
          aria-label="Earlier runs"
          @click="watchOpen = true"
        >
          <q-tooltip>Earlier runs</q-tooltip>
        </q-btn>

        <template v-if="running">
          <q-btn
            v-if="!confirming"
            flat
            dense
            round
            size="sm"
            icon="stop_circle"
            color="negative"
            aria-label="Stop this run"
            @click="confirming = true"
          >
            <q-tooltip>Stop this run</q-tooltip>
          </q-btn>
          <q-btn
            v-else
            dense
            no-caps
            size="sm"
            color="negative"
            label="Stop?"
            :loading="stopping"
            aria-label="Confirm stopping this run"
            @click="stop"
          />
        </template>

        <q-btn
          flat
          dense
          round
          size="sm"
          icon="tune"
          aria-label="Member settings"
          @click="settingsOpen = true"
        >
          <q-tooltip>Member settings</q-tooltip>
        </q-btn>

        <!-- What this member runs without anybody asking. A trigger belongs to exactly one Agent
             Container, so the way in belongs here rather than on a team-level screen that would
             have to name the member itself. The badge
             answers "are there any?" without opening the dialog. -->
        <q-btn
          flat
          dense
          round
          size="sm"
          icon="bolt"
          aria-label="Triggers"
          @click="triggersOpen = true"
        >
          <q-badge v-if="triggerCount > 0" floating color="primary">{{ triggerCount }}</q-badge>
          <q-tooltip>Triggers</q-tooltip>
        </q-btn>
      </div>

      <div v-if="stopError" class="os-body text-warning q-mt-xs">{{ stopError }}</div>

      <div class="text-caption os-text-muted q-mt-xs">
        {{ runsWhat.word }} <span class="mono text-weight-medium">{{ runsWhat.name }}</span>
        · queue
        <span :class="atCeiling ? 'text-warning text-weight-bold' : 'text-weight-medium'">
          <span class="mono">{{ snapshot.queueDepth }}/{{ snapshot.ceiling }}</span>
        </span>
      </div>

      <!-- Reachable because the Agent catalog is a file, not restored with the database: this
           member can name an Agent that no longer exists in it. Shown beside the state rather than
           replacing it - the container is still Idle or Running, it simply cannot do anything the
           next time it wakes. -->
      <div v-if="snapshot.missingAgent" class="q-mt-xs">
        <q-badge color="warning" text-color="black" :label="`agent '${snapshot.missingAgent}' missing`" />
        <!-- The recovery is stated as it actually is. `ProcessAgentRunner` resolves the preset by
             name on every invocation, so adding the Agent back takes effect on the next wake, and
             repointing is the Agent field on the member settings dialog. Telling somebody to
             restart a Host they do not need to restart is the same defect as a refusal that denies
             the way out. -->
        <div class="text-caption os-text-muted q-mt-xs">
          This member cannot run until its Agent is added back to the catalog, or the member is
          pointed at another Agent from its settings. Neither needs a restart.
        </div>
      </div>

      <!-- THE TEAM'S FILES ARE NOT THERE. Not a catalog fault like the mark above it: this
           member's Agent may be perfect, and the folder its team was placed in was
           unreachable when the Host started - an unplugged volume, a share that did not mount.
           Shown ALONGSIDE the catalog marks rather than instead of them, and worded without a
           settings screen in it: a team's root has no setter, so the only recovery is the folder
           and a restart. The missing-Agent mark is above it. -->
      <div v-if="snapshot.unreachableRoot" class="q-mt-xs">
        <q-badge color="warning" text-color="black" label="team folder unreachable" />
        <div class="text-caption os-text-muted q-mt-xs">
          This team's folder <span class="mono">{{ snapshot.unreachableRoot }}</span> could not be
          reached when the Host started, so this member will not run. Reconnect it and restart the
          Host.
        </div>
      </div>

      <!-- STOPPED WITHOUT FINISHING. Not a configuration fault like the marks above, and
           not a state like the badge: this container is idle and perfectly able to take more work,
           it has simply given up on what it was last given. It stays until something wakes it.

           The case this exists for: a manager does exactly what its prompt tells it to - stops
           after the same work comes back twice - and exits 0, which is also what a manager that
           FINISHED does. Without this mark the board shows green idle cards and the message log
           is the only place the difference is written down. -->
      <!-- THE PLATFORM DID NOT COMPLETE THIS RUN, which is a different sentence from the one below
           and is ordered ABOVE it deliberately. `blocked` is the AGENT saying its run finished and
           it is giving up on the work; this is the platform saying the run did not finish at all -
           a launch error, a non-zero exit, a host restart mid-flight.

           The case this exists for: every member of a team fails within seconds (say the agent CLI
           hits a spend limit and exits 1). Working 0, Queued 0, Stopped 0 and an `IDLE` chip are
           each correct, and together describe a healthy team with nothing to do, because a failed
           run and a successful one both end `Idle`. This mark is the failure field that tells
           them apart.

           WHEN BOTH THIS AND `blocked` ARE SET THE CARD SAYS FAILED. A run can leave both - an
           agent gives up and publishes `blocked`, then its process dies and the platform publishes
           `failed` - and both are cleared at the same next wake, so the card has to choose. It
           shows the failure, matching the ranking the tab chip gives `teamStatus`: a platform
           failure outranks an agent's own decision to stop, because one is recoverable by the team
           and the other is not. The two surfaces must order these identically, or a chip and the
           card beneath it disagree about the same member. -->
      <!-- WHAT KIND, AND WHETHER ANYTHING IS GOING TO HAPPEN ABOUT IT.
           The badge beside `run failed` rather than instead of it: a card that said only `quota`
           would stop saying the one thing every reader of this card knows to look for. A class NOBODY COULD DETERMINE is `unknown`, which is rendered as honestly as any
           other - it is a real class and it means the platform will not resume this by itself.

           BOTH LINES ARE ABSENT FOR A FAILURE WITH NO CLASS. The log is append-only and so is the
           board's reading of it. -->
      <div v-if="mark?.kind === 'failed'" class="q-mt-xs">
        <q-badge color="negative" label="run failed" />
        <q-badge
          v-if="failureClassLabel(mark.failureClass, snapshot.kind) !== null"
          color="grey-8"
          class="q-ml-xs"
          :label="failureClassLabel(mark.failureClass, snapshot.kind) ?? ''"
        />
        <div class="text-caption os-text-muted q-mt-xs">{{ mark.reason }}</div>
        <div
          v-if="failureClassWords(mark.failureClass, snapshot.kind) !== null"
          class="text-caption os-text-muted q-mt-xs"
        >
          {{ failureClassWords(mark.failureClass, snapshot.kind) }}
        </div>

        <!-- THE PLATFORM IS GOING TO SPEND MONEY, AND SAYS SO BEFORE IT DOES. Visible is a
             requirement rather than polish: a silent automatic retry is how a
             quota failure becomes a spend failure. The server withdraws `resumeAt` the moment the
             bound is spent or the member moves on, so this line never outlives the intention. -->
        <div v-if="resumeTimeText(mark.resumeAt) !== null" class="text-caption text-primary q-mt-xs">
          resuming automatically at {{ resumeTimeText(mark.resumeAt) }}
        </div>
      </div>

      <div v-else-if="mark?.kind === 'blocked'" class="q-mt-xs">
        <q-badge color="warning" label="stopped without finishing" />
        <div class="text-caption os-text-muted q-mt-xs">{{ mark.reason }}</div>
      </div>

      <!-- STOPPED ON PURPOSE, AND ASKING. The same mechanism as the mark above and deliberately
           not the same reading: `blocked` says this member gave up, and this says its run ended
           WELL and the work cannot continue without a person. Not a card to investigate - a card
           with a question on it, addressed to whoever is looking at the board.

           The case this exists for: a Manager reviews a plan, finds it sound, and stops to ask its
           owner questions before anyone writes code - which is a team working correctly. Without
           its own terminal fact that reads as `container.completed` with exit code 0, green and
           idle, with the questions only in the output prose.

           Coloured as information rather than as a fault, for the same reason: an orange card
           sends somebody to find out what went wrong, and nothing did. -->
      <!-- THIRD IN THE PRECEDENCE `failed › blocked › needsDecision`, and `v-else-if` rather than
           `v-if` for that reason. `blocked` and this one cannot both be set by one run - a run
           publishes one terminal fact - so that pair is an ordering for the record rather than a
           case that arises; `failed` beside either is the real one. A run that died after asking
           did not get its question answered either, and the failure is what has to be cleared
           first. -->
      <div v-else-if="mark?.kind === 'needs-decision'" class="q-mt-xs">
        <q-badge color="primary" label="waiting on a decision" />
        <div class="text-caption os-text-muted q-mt-xs">{{ mark.reason }}</div>
      </div>

      <!-- The thread a human follows. M3 makes this the point of the screen, so it
           is surfaced from the start rather than added when it matters. -->
      <div v-if="snapshot.currentCorrelation !== null" class="text-caption text-primary q-mt-xs">
        workflow #{{ snapshot.currentCorrelation }}
      </div>
    </q-card-section>

    <q-separator />

    <q-card-section class="q-pa-sm container-window-feed">
      <ActivityFeed
        :feed="own"
        :team="snapshot.team"
        :name="snapshot.id"
        :since-seq="snapshot.sinceSeq"
      />
    </q-card-section>
  </q-card>
</template>

<style scoped>
/* THE CARD SIZES ITSELF, rather than taking a class from the board.

   The obvious shape - `<ContainerCard class="board-card">` with the rule in IndexPage - silently
   does nothing: this component has TWO root nodes, the settings dialog and the card, and Vue does
   not auto-inherit a fallthrough attribute onto a fragment. The class never lands and nothing
   errors. Found by reading computed style off the live board, where the grid had its variables and
   `querySelectorAll('.board-card')` returned nothing.

   The variables still come from the board container through the cascade, so one place decides the
   size; only the rule consuming them lives here. */
.container-window {
  /* A MAXIMUM, never a floor. `min(..., 100%)` is what makes "fits on a phone" true by
     construction rather than by picking a small enough default: a 900px setting on a 360px
     viewport still fits, because the viewport wins. */
  width: min(var(--os-card-w, 380px), 100%);
  height: var(--os-card-h, 520px);
  flex: 0 0 auto;

  display: flex;
  flex-direction: column;
  overflow: hidden;
}

/* A PLUGIN MEMBER LOOKS LIKE ONE: its own ground and a violet edge, from the theme's plugin tokens
   so light and dark each get a pair that stands apart from an agent's tile. */
.container-window.container-window-plugin {
  background: var(--os-plugin-surface);
  border-left: 4px solid var(--os-plugin-accent);
}

.container-window-feed {
  flex: 1 1 auto;
  overflow-y: auto;

  /* LOAD-BEARING, and the single most likely way for this to look broken while every test passes.
     A flex child defaults to `min-height: auto`, which refuses to shrink below its content - so
     without this the section grows to its full feed height, the card grows with it, and nothing
     ever scrolls. The overflow rule above is not what makes scrolling work; this is what lets it.

     Newest is already at the top of the feed, so a container that starts at the top shows the
     latest with no scroll handling. There is deliberately NO auto-scroll: yanking the view while
     somebody reads older rows is what makes a live log unusable. */
  min-height: 0;
}

/* Every class here is USED in the template above. styles-match-templates.spec.ts fails on a class a
   component styles and never uses - a dangling selector can put a whole panel one viewport below
   the fold, invisible rather than ugly. */

/* THE LIVE COLOUR, `--os-ok`, the same token the KPI strip's live tile and pulse use, so a running
   member and a running workflow read as one fact. */
.container-running {
  background: var(--os-ok);
}

.container-running-dot {
  background: var(--os-surface);
}
</style>
