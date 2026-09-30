<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue';
import { useQuasar } from 'quasar';
import type {
  ContainerSnapshot,
  TeamRepoStatus,
  TeamTokenTotals,
  TeamWorkflowTiming,
  TeamWorkflows,
  TeamId,
} from '../api/types';
import {
  WaitingForSlot,
  heldMembers,
  teamChip,
  tokenUsageFromTotals,
  waitingForSlotText,
  workflowsExecution,
  workflowsTile,
  workflowTiming,
  closeReasonPrefill,
  workflowRowActions,
  type WorkflowRow,
} from '../lib/teamKpis';
import { budgetBar, budgetInForce, budgetLine, spendAgainstBudget } from '../lib/teamBudget';
import { installStatus } from '../lib/agentInstall';
import { unpushedTeamBranchesLine } from '../lib/repoStatus';
import { asTeamId } from '../api/types';
import { closeWorkflow, nudgeWorkflow, resumeWorkflow, stopWorkflow } from '../api/client';
import { useConsoleStore } from '../stores/console';
import { vResizableColumns } from '../lib/resizableColumns';

const props = defineProps<{
  containers: ContainerSnapshot[];
  /** Log projection for this team, or null until the first fetch lands. */
  usage: TeamTokenTotals | null;

  /** The workflow projection for this team, or null until its first fetch lands. */
  timing: TeamWorkflowTiming | null;

  /**
   * EVERY workflow this team has run since its floor — OPEN AND CLOSED ALIKE, newest first — or
   * null until its first fetch lands. Null is ALSO what an older Host that predates this route
   * answers as — the tile then falls back to `timing` above exactly as it rendered before this prop
   * existed, rather than showing an em dash for data that was never missing.
   *
   * NOT OPEN-ONLY, though it was: anything here that wants the open work filters on
   * `endedAt === null`, which is what `soleOpenSpend` below does and why.
   */
  workflows: TeamWorkflows | null;

  /**
   * Milliseconds to add to this browser's clock to get the server's, taken once per fetch by the
   * store. The browser's clock may be wrong, and without this a machine ten minutes fast renders a
   * workflow that started in the future.
   */
  clockOffset: number;

  /** The team ID, needed for repo actions. */
  teamId: string;

  /**
   * The live finding kinds this team holds, or absent / null / empty if none.
   *
   * **NOTHING IN THIS COMPONENT READS IT, AND THAT IS DELIBERATE.** Using it to widen the Nudge
   * button's `v-if` would let a COMPLETED row with a live finding be nudged — which re-opens a delivered workflow, because `WorkflowOpenSql`'s `woke` subquery treats
   * a Nudge's causation as re-opening the correlation it names. Re-engaging finished work is the
   * Concierge's job and it has its own surface for it.
   *
   * KEPT DECLARED rather than removed, so `IndexPage.vue`'s binding stays a PROP and does not fall
   * through onto the root element as a stray `findings` attribute.
   */
  findings?: string[] | null;

  /**
   * This team's repository status, or null until it is read. Only read for the unpushed-branch
   * line: a finished workflow whose team branch never reached origin shows here, without opening
   * the Git dialog.
   */
  repoStatus?: TeamRepoStatus | null;
}>();

// Convert string teamId to TeamId type
const teamIdTyped = computed(() => asTeamId(props.teamId));

/**
 * THE CLOCK THE TILE COUNTS WITH. One fetch, and this ticks every second with no further traffic —
 * a server-computed elapsed integer would need polling to move, and polling a number a subtraction
 * can produce is the wrong trade.
 *
 * Cleared on unmount, or a board left open through a hundred team switches accumulates a hundred
 * timers all writing to a component nobody is looking at.
 */
const clock = ref(Date.now());
let ticker: ReturnType<typeof setInterval> | null = null;

onMounted(() => {
  ticker = setInterval(() => {
    clock.value = Date.now();
  }, 1000);
});

onBeforeUnmount(() => {
  if (ticker !== null) clearInterval(ticker);
  ticker = null;
});

/**
 * What the tile MEANS is decided in `lib/teamKpis.ts`; this component renders it. A rule expressed
 * in a template is a rule with worse tests.
 */
const workflow = computed(() =>
  workflowTiming(props.timing, props.containers, clock.value + props.clockOffset));

/**
 * EVERY workflow this team holds open, at once — the plural sibling of `workflow` above, and the
 * whole reason this component exists in its current form. Same reasoning as `workflow`: the logic
 * lives in `lib/teamKpis.ts` and this component only renders what it decides.
 */
const tile = computed(() =>
  workflowsTile(props.workflows, props.containers, clock.value + props.clockOffset));

/** The loudest open workflow, with the count beside it — see {@link teamChip}'s own comment for
 *  why this can differ from `workflow.state`: that word is derived from the WHOLE roster and can be
 *  masked to RUNNING by a member working a different workflow than the one that is actually BLOCKED. */
const chip = computed(() => teamChip(tile.value, props.containers));

/** `team/<id> is not pushed` while a local team branch is ahead of origin's, else null. */
const unpushedLine = computed(() => unpushedTeamBranchesLine(props.repoStatus ?? null));

/** `waiting for a slot: Manager` while a wake is held behind the WIP limit, else ''. */
const waitingLine = computed(() => waitingForSlotText(heldMembers(props.containers)));

/** The single-workflow word, with a held wake read as waiting rather than as a silence. */
const singleState = computed(() =>
  waitingLine.value && (workflow.value.state === null || workflow.value.state === 'UNDECLARED')
    ? WaitingForSlot
    : workflow.value.state);

/**
 * WHETHER THE TILE SHOWS THE PLURAL FIGURE OR FALLS BACK TO THE SINGLE-WORKFLOW RENDERING.
 *
 * Gated on `openCount > 0` and not merely on `props.workflows` being non-null: `tile.span` is the
 * span of OPEN workflows and is `—` once none are open, even though the payload is available and
 * `tile.rows` still carries the last closed one. Falling back here to the SAME `workflow` computed
 * used for "not fetched yet" is what keeps "a team with nothing open shows its last workflow"
 * true without a second implementation of what a closed workflow's figure says — `workflow` (built
 * from the singular `timing` prop, fetched independently and unaffected by this) already answers
 * that question correctly.
 */
const usePlural = computed(() => tile.value.available && tile.value.openCount > 0);

/**
 * WHERE THE RUN TIME WENT, ACROSS EVERY WORKFLOW THE TABLE LISTS — not across whichever workflow
 * `props.timing` happened to be. See {@link workflowsExecution}'s own comment for why the singular
 * reading could describe a workflow absent from the list above it.
 */
const execution = computed(() => workflowsExecution(props.workflows));

/** HOW MANY WORKFLOWS THE BREAKDOWN SPANS, in words, for the two captions beneath it. The claim
 *  that per-member run time exceeds elapsed is MORE true across several than across one, so the
 *  caption has to say across how many rather than leave it to be guessed. */
const executionScope = computed(() => {
  const count = tile.value.rows.length;

  return count === 1 ? 'the 1 workflow listed above' : `the ${count} workflows listed above`;
});

/** ONE tone, from whichever of the two the tile is currently rendering — `chip.tone` (which also
 *  covers member-level MISCONFIGURED) while plural, `workflow.tone` in the fallback. */
const displayTone = computed(() => (usePlural.value ? chip.value.tone : workflow.value.tone));

const workflowOpen = ref(false);

/**
 * NO CAPTION NAMES A SINGLE WORKFLOW for "where the run time went". `execution` aggregates across
 * every workflow in the table, so there is no single workflow to name; a caption reading the
 * singular `props.timing` (the team's newest correlation) could name one that is not among the
 * rows at all.
 */

/**
 * Snapshot figures recompute whenever a snapshot lands: the page feeds
 * `activeTeam.containers`, and `applySnapshot` replaces a member in that array in place.
 * Token totals come from the log, not from those snapshots and not from the feed slice.
 */
const tokensOpen = ref(false);

const tokens = computed(() => tokenUsageFromTotals(props.usage));

/**
 * A TEAM WHOSE EVERY MEMBER IS A PLUGIN runs no model, so there is no usage to capture and no spend
 * to bound. The Tokens tile then says `none` quietly instead of `unavailable` with advice about
 * usage capture, and the workflow tile draws no budget line or bar: both would describe a fault on
 * a team that has none. An empty roster is not plugin-only.
 */
const pluginOnly = computed(
  () => props.containers.length > 0 && props.containers.every((member) => member.kind === 'plugin'),
);

/**
 * The limit line under the WORKFLOW tile, or null when nothing bounds this workflow.
 *
 * ON THE WORKFLOW TILE AND NOT THE TOKENS TILE, which is a matter of meaning, not placement.
 * The limit bounds ONE workflow; the Tokens tile is a TEAM TOTAL across every workflow the team
 * has ever run, and it passes 50M honestly after enough work. Putting a per-workflow bound under
 * a cumulative figure would compare two different quantities - the mistake the Workflows dialog
 * already warns about for elapsed time.
 *
 * THE SINGLE-WORKFLOW CASE ONLY. With several open there is no one spend to compare, and a line
 * naming an arbitrary one of them would be worse than none.
 *
 * GATED ON `openCount`, NOT ON `usePlural` AND NOT ON THE LIST'S LENGTH.
 *
 * `usePlural` is `available && openCount > 0`: gating on it would suppress the line whenever a
 * workflow is OPEN and show it only over a finished one. The name is the trap — `usePlural` reads
 * as "more than one" and means "the plural-capable rendering is usable".
 *
 * And the list is not open-only: `TeamWorkflows.workflows` is **"the open ones, or the newest
 * closed one when none are open,"** so a `length === 1` test passes over a closed workflow.
 * `openCount` is the question being asked and the only field that answers it — it is also
 * UNCAPPED, where the list is capped at fifty.
 */
/**
 * THE SPEND ON THE SINGLE OPEN WORKFLOW, when there is exactly one — `null` when there is no
 * workflow to bound at all, and `{ spent: null }` when there is one whose spend was never measured.
 * The two are different answers: the first draws nothing, the second draws a LINE naming the limit
 * and no bar.
 *
 * **IT MUST NOT READ `workflows[0]`.** The list carries CLOSED workflows alongside open ones, newest
 * first, so `workflows[0]` is whichever workflow is newest and is very often a finished one. A team
 * with one workflow running and a newer one already closed would get the RUNNING workflow's budget
 * bar drawn from the CLOSED workflow's spend — a figure that stops moving while the pump keeps
 * spending, which is the same class of reassuring-but-wrong bar as one measured against the wrong bound.
 *
 * `endedAt === null` IS THE OPEN TEST, the same one `workflowTiming` uses. Undefined when the one
 * open workflow fell outside the fifty-row cap, which reads as unmeasured rather than as zero.
 *
 * **AND IT IS `spendSinceNudge`, NOT `spend`** — the window the server's own guard decides on,
 * where `spend` is the cumulative total that never comes down. Reading the total would keep a
 * nudged or resumed workflow's bar at `> limit` while the pump is letting it run. Which of the two
 * applies lives in `spendAgainstBudget`, next to the arithmetic that uses it, so the fallback for a
 * Host that never sent the window is written once and testable.
 */
const soleOpenSpend = computed<{ spent: number | null } | null>(() => {
  const payload = props.workflows;

  if (!payload?.available || payload.openCount !== 1) return null;

  const open = payload.workflows.find((entry) => entry.endedAt === null);

  return {
    spent: spendAgainstBudget(open?.spendSinceNudge?.tokensSpent, open?.spend?.tokensSpent),
  };
});

/**
 * THE FIGURE ACTUALLY IN FORCE FOR ONE WORKFLOW ON THIS TEAM, and the browser does NOT work it out.
 *
 * `Team.effectiveWorkflowBudget` is what the SERVER resolved from the team's stored choice and the
 * instance's `WorkflowSpendLimit`, with `null` meaning UNLIMITED and 0 never appearing. Nothing
 * here reads `Team.budgetTokens`: that is the stored CHOICE, where `0` means the team chose
 * unlimited and `null` means it chose nothing, and `isUnlimited` would read the first as no bound
 * and the second as no bound too — two wrong bars from one wrong field.
 *
 * AN ABSENT FIELD IS NOT A NULL ONE, and `budgetInForce` in `lib/teamBudget.ts` is where that one
 * distinction lives — see its own comment. It is the ONLY thing resolved on this side, and it is
 * not a resolution of the two bounds but of "did this Host answer at all".
 */
const effectiveWorkflowBudget = computed<number | null>(() =>
  budgetInForce(
    board.teams.find((entry) => entry.id === props.teamId)?.effectiveWorkflowBudget,
    board.workflowSpendLimit,
  ));

const budget = computed(() => {
  const spend = soleOpenSpend.value;

  if (spend === null || pluginOnly.value) return null;

  return budgetLine(spend.spent, effectiveWorkflowBudget.value);
});

/**
 * The same fact as {@link budget}, shaped for the bar along the tile's top edge.
 *
 * A SECOND COMPUTED RATHER THAN A FIELD ON THE FIRST, because the two differ in one case and it
 * matters: spend that was never measured gets a LINE naming the budget and NO BAR. A bar at zero is
 * a measurement — "barely started" — and `(unknown)` is the absence of one.
 *
 * Both read the same guard above, so the bar and the line can never disagree about whether there is
 * a workflow to bound.
 */
const budgetBarState = computed(() => {
  const spend = soleOpenSpend.value;

  if (spend === null || pluginOnly.value) return null;

  return budgetBar(spend.spent, effectiveWorkflowBudget.value);
});

const formatToken = (value: number | null) => (value === null ? '(unknown)' : value.toLocaleString());


const anyMemberRunning = computed(() => {
  return props.containers.some((c) => c.state === 'Running');
});

const board = useConsoleStore();

/**
 * THE FOUR THINGS A PERSON CAN DO TO ONE OPEN WORKFLOW, from inside the dialog that lists them.
 *
 * Close, Nudge and Stop each open THEIR OWN `q-dialog`, DECLARED IN THIS COMPONENT'S OWN TEMPLATE
 * - never `$q.dialog()`, whose `class` option lands on the inner card and cannot be lifted, the
 * same trap `ContainerCard.vue`'s own Stop control already documents for the identical reason. A
 * destructive action whose confirmation renders invisibly is worse than none: the click appears to
 * do nothing, and the natural response is to click again.
 *
 * Each dialog's OPEN STATE IS DERIVED from its target being non-null rather than a separate
 * boolean, so dismissing it (Cancel, Escape, a backdrop click) and a successful submission both
 * clear the same field and there is no way for the two to disagree about whether it is showing.
 *
 * Every action calls BOTH `board.pullWorkflows()` (the plural fetch the dialog's own `tile` is
 * built from) and `board.pullWorkflow()` (the singular figure the TILE renders) on success, so
 * every surface moves without a reload.
 *
 * THE SINGULAR HALF IS NOT REDUNDANT. `TimingForAsync` reports `Closed` with a non-null `endedAt`,
 * so a close changes the singular answer too; skipping that fetch would leave the tile reading
 * UNDECLARED beside a dialog and a tab chip both reading CLOSED - two surfaces disagreeing about one
 * workflow, which is the exact defect this whole feature exists to remove.
 */
const closeTarget = ref<WorkflowRow | null>(null);
const closeReasonText = ref('');
const closeBusy = ref(false);
const closeErrorText = ref('');

const closeDialogOpen = computed({
  get: () => closeTarget.value !== null,
  set: (value: boolean) => {
    if (!value) closeTarget.value = null;
  },
});

function openClose(row: WorkflowRow) {
  closeTarget.value = row;
  closeReasonText.value = closeReasonPrefill(row);
  closeErrorText.value = '';
}

async function confirmClose() {
  if (!closeTarget.value) return;

  closeBusy.value = true;
  closeErrorText.value = '';

  try {
    await closeWorkflow(teamIdTyped.value, closeTarget.value.correlation, closeReasonText.value);
    closeTarget.value = null;
    await board.pullWorkflows();
    await board.pullWorkflow();
  } catch (cause) {
    closeErrorText.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    closeBusy.value = false;
  }
}

// Notify rather than a banner in the dialog: the nudge dialog CLOSES on success, so anything
// rendered inside it goes with it. A queued-not-run notice that disappears with the dialog is the
// same silence it exists to break.
const $q = useQuasar();

const nudgeTarget = ref<WorkflowRow | null>(null);
const nudgeBusy = ref(false);
const nudgeErrorText = ref('');

const nudgeDialogOpen = computed({
  get: () => nudgeTarget.value !== null,
  set: (value: boolean) => {
    if (!value) nudgeTarget.value = null;
  },
});

function openNudge(row: WorkflowRow) {
  nudgeTarget.value = row;
  nudgeErrorText.value = '';
}

async function confirmNudge() {
  if (!nudgeTarget.value) return;

  nudgeBusy.value = true;
  nudgeErrorText.value = '';

  try {
    const pauseNotice = await nudgeWorkflow(
      teamIdTyped.value, nudgeTarget.value.correlation);
    nudgeTarget.value = null;

    // A PAUSED TEAM LOGS THE INSTRUCTION AND DOES NOT RUN IT, AND THE PERSON HAS TO BE TOLD.
    // Without this the dialog closes cleanly, the board does not move, and a nudge that is merely
    // QUEUED is indistinguishable from one that woke the Manager - which is the failure mode a
    // pause is most likely to produce on this screen.
    if (pauseNotice) {
      $q.notify({
        type: 'warning',
        message: pauseNotice,
        caption: 'Nudge queued, not run',
        timeout: 6000,
      });
    }

    await board.pullWorkflows();
    await board.pullWorkflow();
  } catch (cause) {
    nudgeErrorText.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    nudgeBusy.value = false;
  }
}

const stopTarget = ref<WorkflowRow | null>(null);
const stopBusy = ref(false);
const stopErrorText = ref('');

const stopDialogOpen = computed({
  get: () => stopTarget.value !== null,
  set: (value: boolean) => {
    if (!value) stopTarget.value = null;
  },
});

function openStop(row: WorkflowRow) {
  stopTarget.value = row;
  stopErrorText.value = '';
}

async function confirmStop() {
  if (!stopTarget.value) return;

  stopBusy.value = true;
  stopErrorText.value = '';

  try {
    await stopWorkflow(teamIdTyped.value, stopTarget.value.correlation);
    stopTarget.value = null;
    await board.pullWorkflows();
    await board.pullWorkflow();
  } catch (cause) {
    stopErrorText.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    stopBusy.value = false;
  }
}

/**
 * RESUME: THE WAY BACK FROM A PAUSE, FROM WHERE THE PAUSE IS SHOWN.
 *
 * **NO CONFIRMATION DIALOG, AND THAT IS THE ONE PLACE THIS CONTROL DEPARTS FROM ITS THREE
 * NEIGHBOURS.** Close, Nudge and Stop each open one, because each does something a person may not
 * have meant: Close ends a workflow, Stop kills live runs, Nudge wakes a Manager on work that may
 * be finished. Resume undoes a stop the PLATFORM made, on a workflow whose row already says PAUSED
 * and why - so a confirmation would be asking a person to agree to the thing they just read and
 * pressed. The card exists because the way back was too far from the pause; a second step is the
 * defect in miniature.
 *
 * ONE WORKFLOW, BY ITS OWN CORRELATION. Its siblings on this team were never stopped and are not
 * touched - a team with three open workflows must not lose all three because one spent its budget,
 * and must not restart all three because one was rescued.
 *
 * `$q.notify` FOR BOTH ANSWERS, because there is no dialog to render either into. A paused TEAM
 * logs the instruction and does not run it, and a person who is not told reads a working button as
 * broken - the same pair `confirmNudge` above carries, for the same reason.
 */
const resumeBusy = ref<number | null>(null);

async function resumeRow(row: WorkflowRow) {
  if (resumeBusy.value !== null) return;

  resumeBusy.value = row.correlation;

  try {
    const pauseNotice = await resumeWorkflow(teamIdTyped.value, row.correlation);

    if (pauseNotice) {
      $q.notify({
        type: 'warning',
        message: pauseNotice,
        caption: 'Resume queued, not run',
        timeout: 6000,
      });
    }

    await board.pullWorkflows();
    await board.pullWorkflow();
  } catch (cause) {
    $q.notify({
      type: 'negative',
      message: cause instanceof Error ? cause.message : String(cause),
      caption: 'Could not resume this workflow',
      timeout: 6000,
    });
  } finally {
    resumeBusy.value = null;
  }
}

/**
 * SHOW THREAD is not a server call at all - it lights the same feed highlight a message's own
 * `#<correlation>` chip already offers, and closes this dialog so the person lands on the feed it
 * just highlighted rather than reading a thread through a dialog stacked on top of a dialog.
 *
 * `board.showWorkflow`, NEVER `board.toggleWorkflow` - this button has no on/off state of its own
 * to show the person before they click, unlike the feed's own chip (rendered lit when it is the
 * one already followed). A row whose workflow is ALREADY highlighted is reachable - click its
 * chip in the feed, then open this dialog and click Show thread to relocate it - and a toggle
 * there would turn the highlight OFF and close the dialog, leaving the feed following nothing:
 * the opposite of what the button says. See `showWorkflow`'s own comment in the store.
 */
function showThread(row: WorkflowRow) {
  board.showWorkflow(row.correlation);
  workflowOpen.value = false;
}
</script>

<template>
  <div class="team-kpi-strip" role="group" aria-label="Team status" aria-live="polite">
    <!-- WORKFLOW. Working, Queued and Stopped are the member line here rather than tiles of their
         own: as tiles they almost always read `2 of 4`, `0`, `0` and none of them answers the
         question a person actually has, which is "how long has this been going?". The room is
         given to the elapsed figure above it. -->
    <div
      class="team-kpi team-kpi--workflow"
      :class="{
        'team-kpi--live': displayTone === 'live',
        'team-kpi--error': displayTone === 'error',
        'team-kpi--warn': displayTone === 'warn',
        'team-kpi--info': displayTone === 'info',
        'team-kpi--quiet': displayTone === 'quiet',
      }"
    >
      <!-- THE BUDGET, AS A THIN BAR ALONG THE TOP EDGE OF THE TILE.

           A GLANCE RATHER THAN AN ARITHMETIC PROBLEM: the line at the bottom of this tile is exact
           and needs two long numbers compared, which nobody does while watching a run. The bar is
           the same fact at a distance.

           IT IS DECORATION OVER A LABELLED FACT, and that is what keeps it inside the rule that
           colour is never the only signal. Everything the bar says, `budget.text` below says in
           words and numbers - so a reader who cannot tell orange from red loses nothing at all.

           ABSENT ON THE SAME TERMS AS THE LINE, from the same computed: one open workflow, a budget
           set, and spend actually measured. -->
      <div
        v-if="budgetBarState"
        class="team-kpi-budget-bar"
        :class="{ 'team-kpi-budget-bar--near': budgetBarState.near }"
        aria-hidden="true"
      >
        <div
          class="team-kpi-budget-bar__fill"
          :style="{ width: `${budgetBarState.fraction * 100}%` }"
        />
      </div>

      <div class="team-kpi-label">
        Workflow
        <!-- THE LOUDEST OPEN WORKFLOW, WITH THE COUNT BESIDE IT, while any are open — `chip.label`
             rather than `workflow.state`, because that word is derived from the WHOLE roster and a
             member Running under one workflow can mask a different workflow's own BLOCKED or FAILED.
             `usePlural`'s fallback keeps the single word for a team with nothing open.

             `chip.count`, NEVER `tile.count` — `tile.count` is the formatted `"3 open"` string built
             for the tile's own count line elsewhere; concatenating it here would double the word into
             "BLOCKED · 3 open" where the intended chip is `BLOCKED · 3`. `chip.count` is the typed
             number the interface carries for exactly this.

             `chip.label` is nullable and a null one renders NO WORD, the same way `workflow.state`
             does below via its own `v-else-if` — never a stand-in word, and never `COMPLETED`: a
             null label means unknown or still inside the grace period, not finished. -->
        <span v-if="usePlural" class="team-kpi-state">
          <template v-if="chip.label === WaitingForSlot">{{ chip.label }} · {{ chip.held.join(', ') }}</template>
          <template v-else><template v-if="chip.label">{{ chip.label }} · </template>{{ chip.count }}</template>
        </span>
        <span v-else-if="singleState" class="team-kpi-state">{{ singleState }}</span>
      </div>

      <!-- AN EM DASH FOR A TEAM THAT HAS NEVER RUN, never `0m`. Zero is a measured duration and
           this is an absent one - the same rule the Tokens tile follows when it says
           `unavailable`. THE SPAN, NEVER A SUM, while anything is open — see `tile`'s own
           documentation for why summing several workflows' durations overstates the time that has
           actually passed. Falls back to `workflow.elapsed`, which already answers "nothing open"
           correctly on its own. -->
      <div class="team-kpi-value mono">
        <span v-if="displayTone === 'live'" class="team-kpi-dot" aria-hidden="true"></span>
        {{ usePlural ? tile.span : workflow.elapsed }}
      </div>

      <div v-if="!usePlural && workflow.detail" class="team-kpi-detail">{{ workflow.detail }}</div>
      <div v-if="waitingLine" class="team-kpi-detail team-kpi-waiting">{{ waitingLine }}</div>
      <div v-if="unpushedLine" class="team-kpi-detail team-kpi-unpushed">
        {{ unpushedLine }}
        <q-tooltip>Origin does not have its latest commits. Push it from the Git dialog.</q-tooltip>
      </div>

      <!-- THE BUDGET, in smaller letters under the spend it bounds.

           IT STOPS COUNTING ONCE IT IS OVER. Past the budget the pump refuses every wake under
           this workflow, so the team is STOPPED rather than merely close to a limit - and a
           figure still ticking would suggest work being done. "> budget" is the state.

           ABSENT WHEN THERE IS NO BUDGET. An unbounded team is the ordinary case, and a line
           that always shows is a line nobody reads. -->
      <div
        v-if="budget"
        class="team-kpi-budget"
        :class="{ 'team-kpi-budget--over': budget.over }"
      >
        {{ budget.text }}
        <!-- IT NAMES A CONTROL THAT EXISTS: the per-workflow budget on Team settings → General.
             Stale guidance in a tooltip is the worst kind, because it reads as considered.
             `__tests__/team-kpi-tokens.mount.spec.ts` opens this tooltip and pins its wording.

             AND IT NAMES THE WAY BACK RATHER THAN THE VERB. "Nudge" is what resets the spend
             window, and a person who does not already know that cannot get there from a number.
             The row in the Workflows dialog carries a Resume button; this points at it. -->
        <q-tooltip v-if="budget.over">
          This workflow has spent more than the budget in force for it. The per-workflow budget is
          set on Team settings, under General. A workflow the platform has already stopped reads
          PAUSED in the Workflows dialog, with a Resume button on its own row.
        </q-tooltip>
      </div>

      <!-- THE MEMBER LINE IS THE CLICK TARGET, NOT THE WHOLE TILE, and the inconsistency with the
           Tokens tile beside it is a recorded decision rather than an oversight. A `<button>`
           cannot nest inside a `<button>`, and the elapsed figure has no drill-down of its own - a
           whole-tile target that only means something on one line is worse than a line that looks
           like what it is. It is underlined so it reads as interactive rather than as a surprise. -->
      <button type="button" class="team-kpi-members" @click="workflowOpen = true">
        {{ workflow.members }}
        <q-tooltip>Where the run time went — click for the breakdown</q-tooltip>
      </button>
    </div>

    <button type="button" class="team-kpi team-kpi--tokens" @click="tokensOpen = true">
      <div class="team-kpi-label">Tokens</div>
      <template v-if="tokens.available">
        <!-- BILLABLE LEADS. tokensIn is uncached input only, so on its own a team that read ~76M
             tokens from the cache read as a few thousand. -->
        <div class="team-kpi-value mono">
          {{ tokens.billable.toLocaleString() }}
          <span class="team-kpi-of">billable</span>
        </div>
        <div class="team-kpi-breakdown mono">
          {{ tokens.input.toLocaleString() }} in · {{ tokens.cachedIn.toLocaleString() }} cache read ·
          {{ tokens.cacheCreation.toLocaleString() }} cache write · {{ tokens.output.toLocaleString() }} out
        </div>
        <div v-if="tokens.partial" class="team-kpi-partial">
          <span v-if="tokens.partial">partial</span>
        </div>
        <div class="team-kpi-claim">{{ tokens.claim }}</div>
      </template>
      <div v-else-if="pluginOnly" class="team-kpi-value team-kpi-unavailable team-kpi-none">none</div>
      <div v-else class="team-kpi-value team-kpi-unavailable">unavailable</div>
      <q-tooltip>Tokens consumed — click for the breakdown</q-tooltip>
    </button>
  </div>

  <!-- EXECUTION LIVES HERE AND NOWHERE ELSE. Elapsed is on the tile; the two are never on screen
       together as two bare numbers, because side by side one of them reads as a bug - execution
       legitimately EXCEEDS elapsed whenever two members overlap. Every figure below carries its
       unit of measure in words for the same reason. Nothing sums them, averages them or derives a
       percentage: there is no progress bar and no percentage anywhere in this feature, because
       nothing knows how much work remains. -->
  <q-dialog v-model="workflowOpen">
    <q-card class="token-dialog-card os-dialog-md">
      <!-- THE WORKFLOW SURFACE: every workflow, and what a person can do to one, with the execution
           breakdown as its own section below. -->
      <q-card-section class="q-pb-none">
        <div class="os-dialog-title">Workflows</div>
        <!-- IT DOES NOT CLAIM TO BE ONLY WHAT THE TEAM HOLDS OPEN: the list is open and closed alike,
             so the caption says so - and the actions it advertises are the ones a row may actually
             carry, which does not include nudging a finished workflow back to life. Re-engaging
             delivered work is the Concierge's. -->
        <div class="text-caption q-mt-xs">
          Every workflow this team has run, open and closed alike, newest first. Close one that is
          idle, nudge its Manager back to life, follow its thread in the feed, or stop what is
          running under it.
        </div>
      </q-card-section>

      <!-- ONE ROW PER WORKFLOW, ABOVE THE PER-MEMBER EXECUTION TABLE BELOW. `tile.rows` is every
           workflow this team has run, newest first, open and closed alike - so this section renders
           whenever there is anything at all to show, independently of `usePlural` above. -->
      <q-card-section v-if="tile.rows.length > 0" class="q-pb-none">
        <div class="text-caption os-text-muted text-weight-medium q-mb-xs">
          Workflows
          <!-- THE TRUNCATION SIGNAL, COUNTED AGAINST THE TOTAL AND NOT AGAINST `openCount`.
               `tile.rows` is capped at fifty server-side while `totalCount` is not.

               NOT `rows.length < openCount`: a team with nothing open reads `openCount: 0` while the
               list still carries rows, so that comparison could never fire on a list that was
               genuinely truncated. With closed workflows in the list there is no reading of
               `openCount` that answers "is this list complete" at all. -->
          <span v-if="tile.rows.length < tile.totalCount" class="os-text-muted">
            · showing {{ tile.rows.length }} of {{ tile.totalCount }}
          </span>
        </div>

        <div class="workflow-rows-scroll">
          <div
            v-for="row in tile.rows"
            :key="row.correlation"
            class="workflow-row q-mb-sm"
            :class="{
              'workflow-row--live': row.tone === 'live',
              'workflow-row--error': row.tone === 'error',
              'workflow-row--warn': row.tone === 'warn',
              'workflow-row--info': row.tone === 'info',
              'workflow-row--quiet': row.tone === 'quiet',
            }"
          >
            <div class="row items-center no-wrap">
              <div class="col ellipsis">
                {{ row.subject }}
                <span v-if="row.state" class="os-text-muted"> · {{ row.state }}</span>
              </div>
              <div class="col-auto mono text-caption q-ml-sm">
                <span v-if="row.tone === 'live'" class="team-kpi-dot" aria-hidden="true"></span>
                {{ row.elapsed }}
              </div>
            </div>
            <!-- THE TWO INSTANTS `elapsed` IS THE SUBTRACTION OF. The duration above is still
                 computed on this side and still ticks with no further traffic; these say what it
                 was measured BETWEEN, so a row forty entries down is locatable in time rather than
                 being "2h 14m" of some unspecified day. `—` for a workflow that has not ended,
                 which is the ordinary case. -->
            <div class="workflow-row-when text-caption os-text-muted mono">
              started {{ row.started }} · ended {{ row.ended }}
            </div>

            <!-- WHO RAN IN IT, AND WHAT THE ROW IS WAITING FOR. NOT `ellipsis`, which is on the
                 subject line above by design: on an AWAITING row this line is the half that says
                 who is waiting and on what, and clipping it to one line hides exactly the sentence
                 a person opened this dialog to read. There is deliberately no extra button beside
                 it - Nudge on an AWAITING row wakes the Manager with no answer and can only produce
                 the same question again. -->
            <div class="workflow-row-detail text-caption os-text-muted">
              with {{ row.withWhom }}
              <span v-if="row.detail"> · {{ row.detail }}</span>
            </div>

            <!-- THE FOUR THINGS A PERSON CAN DO TO THIS WORKFLOW, and WHICH of them this row may
                 offer is decided by `workflowRowActions` AND NOWHERE ELSE. Every `v-if` below is a
                 bare field read off one call; none of them composes a condition of its own, and
                 none of them may start to.

                 THAT IS THE DEFECT THIS SHAPE EXISTS TO PREVENT: a condition composed here (say,
                 passing `props.findings` into the call) could render Nudge on a COMPLETED row - a
                 button that publishes an instruction whose causation `WorkflowOpenSql`'s `woke`
                 subquery treats as RE-OPENING the workflow. The rule lives in one function with a
                 spec, not half in markup with none; see that function's comment. -->
            <div class="row items-center q-gutter-xs q-mt-xs">
              <q-btn
                v-if="workflowRowActions(row).close"
                flat
                dense
                no-caps
                size="sm"
                label="Close"
                @click="openClose(row)"
              />
              <q-btn
                v-if="workflowRowActions(row).nudge"
                flat
                dense
                no-caps
                size="sm"
                label="Nudge"
                @click="openNudge(row)"
              />
              <!-- RESUME, BESIDE NUDGE AND NOT INSTEAD OF IT. A reader who has just read PAUSED on
                   this row can act on it here; before this card the only way back was Nudge, and
                   nothing on screen said that pressing it was what reset the spend window. It is
                   coloured `primary` because it is the ONE thing to do to a paused row - the other
                   three are the generic set every idle row carries. -->
              <q-btn
                v-if="workflowRowActions(row).resume"
                flat
                dense
                no-caps
                size="sm"
                color="primary"
                label="Resume"
                :loading="resumeBusy === row.correlation"
                @click="resumeRow(row)"
              />
              <q-btn flat dense no-caps size="sm" label="Show thread" @click="showThread(row)" />
              <q-btn
                v-if="workflowRowActions(row).stop"
                flat
                dense
                no-caps
                size="sm"
                color="negative"
                label="Stop"
                @click="openStop(row)"
              />
            </div>
          </div>
        </div>
      </q-card-section>

      <q-card-section>
        <!-- THE EXECUTION BREAKDOWN, one section of the workflow surface above. -->
        <div class="text-subtitle2 q-mb-xs">Where the run time went</div>
        <!-- ACROSS EVERY WORKFLOW IN THE TABLE, not across whichever one the dialog happens to
             pick (such as `props.timing`, the team's single NEWEST correlation). The breakdown
             aggregates, so there is no one workflow to name. -->
        <div class="os-body os-text-muted q-mb-md">
          Per-member run time from the message log, summed across {{ executionScope }}.
        </div>

        <div class="text-body2 q-mb-md">
          Total execution:
          <span class="mono text-weight-medium">{{ execution.total }}</span>
        </div>

        <!-- THE HONESTY, WORDED TO SAY ACROSS WHAT. The claim is MORE true across
             several workflows than across one, not less: members overlap within a workflow and the
             workflows themselves overlap too, so the sum can exceed any single tile's elapsed by
             more than concurrency alone would explain. Nothing here adds the two, divides them, or
             derives a percentage. -->
        <div class="os-body os-text-muted q-mb-md">
          Members run at the same time, and these totals span {{ executionScope }} — so this figure
          is often LARGER than the elapsed time on the tile, or on any one row above. That is the
          two figures being different quantities, not a fault.
        </div>

        <div v-if="execution.unfinished > 0" class="os-body os-text-muted q-mb-md">
          {{ execution.unfinished }} run(s) here started and never reported an end — a host
          restart mid-flight. They are counted as unknown rather than as zero, so they are not in
          the total above.
        </div>

        <div class="text-caption os-text-muted text-weight-medium q-mb-xs">By member</div>
        <div v-if="execution.members.length === 0" class="os-body os-text-muted">
          No runs recorded in {{ executionScope }}.
        </div>
        <div v-else>
          <div
            v-for="row in execution.members"
            :key="row.member"
            class="row items-center q-mb-xs"
          >
            <div class="col ellipsis">
              {{ row.member }}
              <span class="os-text-muted"> · {{ row.runs }} run(s)</span>
            </div>
            <div class="col-auto mono text-caption">
              {{ row.execution }}
              <span v-if="row.unfinished > 0" class="os-text-muted">
                · {{ row.unfinished }} unfinished
              </span>
            </div>
          </div>
        </div>
      </q-card-section>

      <!-- "Done", NOT "Close" - a row inside this same dialog now has its own Close button that
           closes a WORKFLOW, and the dialog's own dismiss button must not read as doing the same
           thing. -->
      <q-card-actions align="right">
        <q-btn flat no-caps label="Done" @click="workflowOpen = false" />
      </q-card-actions>
    </q-card>
  </q-dialog>

  <!-- CLOSE. Declared here, in this component's own template - never `$q.dialog()` - so the
       confirmation can never render behind anything; see `closeTarget`'s own comment above.
       `:persistent="closeBusy"` blocks Escape and a backdrop click ONLY while the request is in
       flight, so the explicit Cancel-button guard and the dialog's own implicit dismiss paths
       agree about when this may be dismissed - without it, either would clear `closeTarget` out
       from under a request already sent (harmless today, since the route is idempotent and a
       resulting error has nowhere left to render, but a guard that half-covers its own dialog is
       worth closing rather than leaving as a footnote). -->
  <q-dialog v-model="closeDialogOpen" :persistent="closeBusy">
    <q-card class="workflow-action-card os-dialog-sm">
      <q-card-section class="q-pb-none">
        <div class="os-dialog-title">Close this workflow?</div>
        <div v-if="closeTarget" class="text-caption os-text-muted q-mt-xs">
          {{ closeTarget.subject }}
        </div>
      </q-card-section>

      <q-card-section>
        <!-- PREFILLED FROM WHAT THE PLATFORM ALREADY KNOWS, EDITABLE, ONE CLICK TO SUBMIT - the
             mitigation that makes an optional reason acceptable; the route's own comment cites it
             by name. -->
        <q-input
          v-model="closeReasonText"
          label="Reason"
          hint="Optional — the row still records who closed it and when regardless."
          dense
          :disable="closeBusy"
        />
      </q-card-section>

      <q-card-section v-if="closeErrorText" class="q-pt-none">
        <q-banner dense class="bg-negative text-white">{{ closeErrorText }}</q-banner>
      </q-card-section>

      <q-card-actions align="right">
        <q-btn flat no-caps label="Cancel" :disable="closeBusy" @click="closeDialogOpen = false" />
        <!-- No explicit `:disable` here alongside `:loading` - Quasar's own loading state already
             suppresses further clicks, the same convention `ContainerCard.vue`'s Stop control
             relies on for its own confirm button. -->
        <q-btn
          unelevated
          no-caps
          color="primary"
          label="Close workflow"
          :loading="closeBusy"
          @click="confirmClose"
        />
      </q-card-actions>
    </q-card>
  </q-dialog>

  <!-- NUDGE. Same `:persistent`-while-busy reasoning as CLOSE above. -->
  <q-dialog v-model="nudgeDialogOpen" :persistent="nudgeBusy">
    <q-card class="workflow-action-card os-dialog-sm">
      <q-card-section class="q-pb-none">
        <div class="os-dialog-title">Nudge this workflow?</div>
        <div v-if="nudgeTarget" class="text-caption os-text-muted q-mt-xs">
          {{ nudgeTarget.subject }}
        </div>
      </q-card-section>

      <q-card-section class="text-body2">
        Wakes this team's Manager with this thread's own history, not the head of a new one. It
        queues rather than interrupts — the Manager picks it up on its next turn.
        <template v-if="nudgeTarget && (nudgeTarget.state === 'COMPLETED' || nudgeTarget.state === 'CLOSED')">
          <br />
          This wakes the Manager under that thread and re-opens it.
        </template>
      </q-card-section>

      <q-card-section v-if="nudgeErrorText" class="q-pt-none">
        <q-banner dense class="bg-negative text-white">{{ nudgeErrorText }}</q-banner>
      </q-card-section>

      <q-card-actions align="right">
        <q-btn flat no-caps label="Cancel" :disable="nudgeBusy" @click="nudgeDialogOpen = false" />
        <!-- See CLOSE's own comment on `:loading` without a matching `:disable`. -->
        <q-btn
          unelevated
          no-caps
          color="primary"
          label="Nudge"
          :loading="nudgeBusy"
          @click="confirmNudge"
        />
      </q-card-actions>
    </q-card>
  </q-dialog>

  <!-- STOP. Same `:persistent`-while-busy reasoning as CLOSE above. -->
  <q-dialog v-model="stopDialogOpen" :persistent="stopBusy">
    <q-card class="workflow-action-card os-dialog-sm">
      <q-card-section class="q-pb-none">
        <div class="os-dialog-title">Stop this workflow?</div>
        <div v-if="stopTarget" class="text-caption os-text-muted q-mt-xs">
          {{ stopTarget.subject }}
        </div>
      </q-card-section>

      <q-card-section class="text-body2">
        Ends the run of every member currently working under this workflow. Members stay; only
        their runs in flight go.
      </q-card-section>

      <q-card-section v-if="stopErrorText" class="q-pt-none">
        <q-banner dense class="bg-negative text-white">{{ stopErrorText }}</q-banner>
      </q-card-section>

      <q-card-actions align="right">
        <q-btn flat no-caps label="Cancel" :disable="stopBusy" @click="stopDialogOpen = false" />
        <!-- See CLOSE's own comment on `:loading` without a matching `:disable`. -->
        <q-btn
          unelevated
          no-caps
          color="negative"
          label="Stop"
          :loading="stopBusy"
          @click="confirmStop"
        />
      </q-card-actions>
    </q-card>
  </q-dialog>

  <q-dialog v-model="tokensOpen">
    <q-card class="token-dialog-card os-dialog-lg">
      <q-card-section class="q-pb-none">
        <div class="os-dialog-title">Tokens consumed</div>
        <div v-if="tokens.available" class="text-caption q-mt-xs">
          Team total from the message log.
        </div>
      </q-card-section>

      <q-card-section v-if="tokens.available">
        <!-- THE TEAM TOTAL: billable as the headline, the four figures it is weighted from as
             labelled values beside it, never one run-on line of numbers. -->
        <div class="token-summary" data-token-summary>
          <div class="token-summary-total">
            <div class="token-summary-value mono">{{ tokens.billable.toLocaleString() }}</div>
            <div class="text-caption os-text-muted">billable tokens</div>
          </div>
          <div class="token-summary-parts">
            <div class="token-summary-part" data-token-part="in">
              <div class="mono">{{ tokens.input.toLocaleString() }}</div>
              <div class="text-caption os-text-muted">in</div>
            </div>
            <div class="token-summary-part" data-token-part="cache-read">
              <div class="mono">{{ tokens.cachedIn.toLocaleString() }}</div>
              <div class="text-caption os-text-muted">cache read</div>
            </div>
            <div class="token-summary-part" data-token-part="cache-write">
              <div class="mono">{{ tokens.cacheCreation.toLocaleString() }}</div>
              <div class="text-caption os-text-muted">cache write</div>
            </div>
            <div class="token-summary-part" data-token-part="out">
              <div class="mono">{{ tokens.output.toLocaleString() }}</div>
              <div class="text-caption os-text-muted">out</div>
            </div>
          </div>
        </div>
        <div class="text-caption os-text-muted q-mt-sm">
          Billable weights a cache read at 1/10 and a cache write at 5/4, and counts a combined
          total as reported.
        </div>

        <!-- ONE COLUMN PER FIGURE, right-aligned so the digits line up, and the name in a column
             of its own that WRAPS rather than truncates: "De...416" named nobody. A row's note
             ("this Agent reports no usage") REPLACES its figures, spanning their columns - the
             decision is lib/teamKpis.ts's, as before. -->
        <div class="token-section-title">By agent brand</div>
        <div v-if="tokens.byBrand.length === 0" class="text-caption os-text-muted">
          No brand totals.
        </div>
        <table v-else v-resizable-columns="'tokens-by-brand'" class="token-table" data-token-table="brand">
          <thead>
            <tr>
              <th class="token-name-col">Brand</th>
              <th class="token-num">Billable</th>
              <th class="token-num">In</th>
              <th class="token-num">Cache read</th>
              <th class="token-num">Cache write</th>
              <th class="token-num">Out</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="row in tokens.byBrand" :key="row.brand" :data-token-row="`brand:${row.brand}`">
              <td class="token-name">{{ row.brand }}</td>
              <td v-if="row.note" colspan="5" class="token-note">{{ row.note }}</td>
              <template v-else>
                <td class="token-num mono token-billable" data-cell="billable">{{ formatToken(row.billable) }}</td>
                <td class="token-num mono" data-cell="in">{{ formatToken(row.input) }}</td>
                <td class="token-num mono" data-cell="cache-read">{{ formatToken(row.cachedIn) }}</td>
                <td class="token-num mono" data-cell="cache-write">{{ formatToken(row.cacheCreation) }}</td>
                <td class="token-num mono" data-cell="out">{{ formatToken(row.output) }}</td>
              </template>
            </tr>
          </tbody>
        </table>

        <div class="token-section-title">By member</div>
        <div v-if="tokens.byMember.length === 0" class="text-caption os-text-muted">
          No member totals.
        </div>
        <table v-else v-resizable-columns="'tokens-by-member'" class="token-table" data-token-table="member">
          <thead>
            <tr>
              <th class="token-name-col">Member</th>
              <th class="token-num">Billable</th>
              <th class="token-num">In</th>
              <th class="token-num">Cache read</th>
              <th class="token-num">Cache write</th>
              <th class="token-num">Out</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="row in tokens.byMember" :key="row.member" :data-token-row="`member:${row.member}`">
              <td class="token-name">
                <div>{{ row.member }}</div>
                <div class="text-caption os-text-muted">{{ row.brand }}</div>
              </td>
              <td v-if="row.note" colspan="5" class="token-note">{{ row.note }}</td>
              <template v-else>
                <td class="token-num mono token-billable" data-cell="billable">{{ formatToken(row.billable) }}</td>
                <td class="token-num mono" data-cell="in">{{ formatToken(row.input) }}</td>
                <td class="token-num mono" data-cell="cache-read">{{ formatToken(row.cachedIn) }}</td>
                <td class="token-num mono" data-cell="cache-write">{{ formatToken(row.cacheCreation) }}</td>
                <td class="token-num mono" data-cell="out">{{ formatToken(row.output) }}</td>
              </template>
            </tr>
          </tbody>
        </table>
      </q-card-section>

      <q-card-section v-else-if="pluginOnly" class="os-body os-text-muted team-kpi-plugin-only">
        Every member of this team is a plugin. Plugins run no model, so this team uses no tokens.
      </q-card-section>

      <q-card-section v-else>
        <q-banner dense class="os-bg-quiet os-text-muted">
          <template #avatar><q-icon name="block" /></template>
          Unavailable. {{ tokens.missing }}.
        </q-banner>
        <div class="text-body2 q-mt-md">
          This card only shows a team total summed from tokensIn and tokensOut on
          agentContainer.completed and agentContainer.failed rows in the message log. It does not sum
          the twenty messages a card still holds — that slice would count down as the team
          got busier. A guessed count would look like a real one.
        </div>
        <div class="os-body os-text-muted q-mt-md">
          Configure usage capture in the Agents screen.
        </div>
      </q-card-section>

      <q-card-actions align="right">
        <q-btn flat no-caps label="Close" @click="tokensOpen = false" />
      </q-card-actions>
    </q-card>
  </q-dialog>
</template>

<style scoped>
/* THE STRIP SIZES ITSELF. This component has two root nodes (the strip and the
   dialog), so a class from IndexPage would silently never land — same trap as
   ContainerCard. The rule lives here. */
.team-kpi-strip {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  margin: 0 0 16px;
}

.team-kpi {
  /* EQUAL THIRDS, which needs a ZERO basis rather than matching bases: with a content-sized basis
     each tile grows from a different starting width, so three tiles that grow at the same rate
     still end up three different widths. Growing from nought is what makes them equal.

     `min-width` is what wraps them - below ~592px three of these no longer seat, and the row breaks
     on its own with no media query. */
  flex: 1 1 0;
  min-width: 12rem;
  background: var(--os-chrome);
  border: 1px solid var(--os-rule);
  border-radius: 8px;
  padding: 10px 12px;
}

.team-kpi-label {
  font-size: 0.68rem;
  letter-spacing: 0.08em;
  text-transform: uppercase;
  color: var(--os-ink-faint);
}

.team-kpi-value {
  display: flex;
  align-items: baseline;
  flex-wrap: wrap;
  gap: 0.35rem;
  margin-top: 4px;
  font-size: 1.35rem;
  font-weight: 600;
  color: var(--os-ink);
  line-height: 1.2;
}

.team-kpi-of {
  font-size: 0.75rem;
  font-weight: 400;
  color: var(--os-ink-muted);
}

/* The state word sits ON the label line rather than under the figure, so the tile keeps the height
   the three it replaced had between them. */
.team-kpi-state {
  margin-left: 0.4rem;
  font-weight: 700;
  color: var(--os-ink-muted);

  /* `NO RESULT` is the first state that is two words, and the card's width is a per-viewer
     preference — so without this it can break at its own space and read as two labels. It is no
     wider than what already ships: `Workflow NO RESULT` is the same eighteen characters as
     `Workflow COMPLETED`. */
  white-space: nowrap;
}

/* THE BUDGET BAR, along the tile's top edge.

   THE TILE BECOMES THE POSITIONING CONTEXT, which is the only change this makes to anything that
   already worked - `.team-kpi` has padding and a radius, and a bar inside that padding would float
   below the edge with a gap on both sides rather than reading as part of the tile. */
.team-kpi--workflow {
  position: relative;
}

.team-kpi-budget-bar {
  position: absolute;
  top: 0;
  left: 0;
  right: 0;
  height: 3px;

  /* Follows the tile's own corners, so the bar ends where the border curves rather than crossing
     it. `overflow: hidden` is what clips the fill to that curve. */
  border-radius: 8px 8px 0 0;
  overflow: hidden;
  background: var(--os-rule);
}

.team-kpi-budget-bar__fill {
  height: 100%;
  background: var(--q-primary);

  /* Spend only ever grows, and a figure that jumps is harder to read than one that moves. Short
     enough not to lag a live number. */
  transition: width 0.3s ease;
}

/* WITHIN 10% OF THE BUDGET. The same red as the `> budget` text below it, deliberately: they are
   one state seen twice, and two shades of warning would read as two different problems. */
.team-kpi-budget-bar--near .team-kpi-budget-bar__fill {
  background: var(--q-negative);
}

/* Smaller than the detail line above it, because it is a bound rather than a fact about the run. */
.team-kpi-budget {
  font-size: 11px;
  color: var(--os-ink-faint);
}

/* OVER BUDGET IS A STOP, NOT A WARNING. The pump is refusing wakes, so the team has stopped - the
   same weight the card gives a failed run rather than the amber of something merely close. */
.team-kpi-budget--over {
  color: var(--q-negative);
  font-weight: 600;
}

.team-kpi-detail {
  margin-top: 2px;
  font-size: 0.7rem;
  color: var(--os-ink-muted);
}

/* A wake held behind the WIP limit: about to work, so warm rather than muted. */
.team-kpi-unpushed {
  color: var(--q-warning);
}

.team-kpi-waiting {
  color: var(--q-warning);
}

/* THE ONLY CLICK TARGET ON THIS TILE. Underlined so it reads as interactive - a line that does
   something and looks like prose is worse than no affordance at all. */
.team-kpi-members {
  display: block;
  margin-top: 4px;
  padding: 0;
  border: 0;
  background: none;
  font: inherit;
  font-size: 0.72rem;
  text-align: left;
  text-decoration: underline;
  color: var(--os-ink-muted);
  cursor: pointer;
}

.team-kpi-members:focus-visible {
  outline: 2px solid var(--os-ink);
  outline-offset: 2px;
}


.team-kpi--live {
  border-color: var(--os-ok);
}

.team-kpi--warn {
  border-color: var(--os-warn);
}

.team-kpi--error {
  border-color: var(--q-negative);
}

.team-kpi--info {
  border-color: var(--q-info);
}

/* UNDECLARED IS NOT AN ERROR COLOUR, deliberately: those runs SUCCEEDED and the work stopped anyway.
   A red tile sends somebody to find out what broke, and nothing did. */
.team-kpi--quiet {
  border-color: var(--os-ink-faint);
  border-style: dashed;
}

.team-kpi-dot {
  display: inline-block;
  width: 7px;
  height: 7px;
  border-radius: 50%;
  background: var(--os-ok);
  align-self: center;
  animation: team-kpi-pulse 1.2s ease-in-out infinite;
}

.team-kpi--tokens {
  /* A BUTTON CENTRES ITS CONTENT VERTICALLY, and its two neighbours are divs whose content starts at
     the top - so this tile's label hung about 29px lower than `WORKFLOW` and `REPOS` and the three
     titles did not line up. The tiles are equal-height flex siblings, so the taller the strip gets
     the further out it drifts. Declaring the column explicitly overrides the button's own centring;
     `text-align: left` below does not, because this is block-axis alignment, not inline. */
  display: flex;
  flex-direction: column;

  font: inherit;
  text-align: left;
  cursor: pointer;
  color: inherit;
  border-style: dashed;
}

.team-kpi--tokens:focus-visible {
  outline: 2px solid var(--os-ink);
  outline-offset: 2px;
}

.team-kpi-unavailable {
  font-size: 0.82rem;
  font-weight: 600;
  letter-spacing: 0.06em;
  text-transform: uppercase;
  color: var(--os-ink-faint);
}

.team-kpi-partial {
  margin-top: 2px;
  font-size: 0.7rem;
  letter-spacing: 0.06em;
  text-transform: uppercase;
  color: var(--os-warn);
}

.team-kpi-breakdown {
  margin-top: 2px;
  font-size: 0.7rem;
  color: var(--os-ink-faint);
}

.team-kpi-claim {
  margin-top: 2px;
  font-size: 0.7rem;
  letter-spacing: 0.06em;
  text-transform: uppercase;
  color: var(--os-ink-faint);
}

/* A CAP WITH ITS OWN SCROLL, the same shape `AgentsDialog`'s `.agents-body` uses: a team running
   several workflows at once makes this list longer than the per-member table it sits above, and
   without a bound the dialog grows past the viewport with no way to reach its Close button. The
   bound is `max-height` plus `overflow-y: auto` here, not a flex `min-height: 0` trick - this
   section is a plain block inside a `q-card-section` (`position: relative` only, not a flex
   container), so nothing forces it to stay at its content's height the way a flex child would.
   `min-height: 0` is kept anyway, defensively, in case a future layout change makes this a flex
   child without anyone remembering why the scroll stopped working. */
.workflow-rows-scroll {
  max-height: 14rem;
  min-height: 0;
  overflow-y: auto;
}

.workflow-row {
  padding: 4px 8px;
  border-left: 2px solid var(--os-rule);
  border-radius: 2px;
}

/* THE ROW'S TWO INSTANTS. Smaller than the line above it because it is provenance rather than the
   answer - the duration beside the subject is what a person reads first. */
.workflow-row-when {
  font-size: 0.7rem;
  opacity: 0.85;
}

/* NOT CLIPPED, AND THAT IS THE RULE RATHER THAN THE DEFAULT. On an AWAITING row this line carries
   who is waiting and on what, which is the sentence the dialog was opened to read; `ellipsis` (used
   on the subject line above, deliberately) would hide it on exactly the rows that need it. Stated
   here so a later tidy has to argue with it. */
.workflow-row-detail {
  white-space: normal;
  overflow-wrap: anywhere;
}

.workflow-row--live {
  border-left-color: var(--os-ok);
}

.workflow-row--warn {
  border-left-color: var(--os-warn);
}

.workflow-row--error {
  border-left-color: var(--q-negative);
}

.workflow-row--info {
  border-left-color: var(--q-info);
}

/* UNDECLARED IS NOT AN ERROR COLOUR HERE EITHER, for `.team-kpi--quiet`'s own reason. */
.workflow-row--quiet {
  border-left-style: dashed;
  border-left-color: var(--os-ink-faint);
}

@media (prefers-reduced-motion: reduce) {
  .team-kpi-dot {
    animation: none;
  }
}

@keyframes team-kpi-pulse {
  0%,
  100% {
    opacity: 1;
    transform: scale(1);
  }
  50% {
    opacity: 0.35;
    transform: scale(0.7);
  }
}

/* THE TOKENS DIALOG. The headline total with its four parts, then two tables whose figures line
   up in right-aligned columns and whose names wrap instead of being cut. */
.token-summary {
  display: flex;
  flex-wrap: wrap;
  align-items: flex-end;
  gap: 16px 32px;
}

.token-summary-value {
  font-size: 28px;
  font-weight: 600;
  line-height: 1.1;
}

.token-summary-parts {
  display: flex;
  flex-wrap: wrap;
  gap: 8px 24px;
  font-variant-numeric: tabular-nums;
}

.token-section-title {
  margin: 20px 0 6px;
  font-size: 12px;
  font-weight: 600;
  letter-spacing: 0.04em;
  text-transform: uppercase;
  opacity: 0.7;
}

.token-table {
  width: 100%;
  border-collapse: collapse;
  font-variant-numeric: tabular-nums;
}

.token-table th {
  padding: 4px 8px;
  font-size: 11px;
  font-weight: 600;
  text-align: left;
  opacity: 0.7;
  border-bottom: 1px solid var(--os-rule-strong);
  white-space: nowrap;
}

.token-table td {
  padding: 6px 8px;
  border-bottom: 1px solid var(--os-rule);
  vertical-align: top;
}

.token-table tbody tr:last-child td {
  border-bottom: none;
}

.token-table .token-num {
  text-align: right;
  white-space: nowrap;
}

.token-name-col {
  width: 100%;
}

.token-name {
  overflow-wrap: anywhere;
  min-width: 10ch;
}

.token-billable {
  font-weight: 600;
}

.token-note {
  font-size: 12px;
  opacity: 0.7;
}
</style>
