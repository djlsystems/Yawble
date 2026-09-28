<script setup lang="ts">
/**
 * ONE REPOSITORY, AND THE ONE THING TO DO ABOUT IT NEXT.
 *
 * ONE LADDER, NOT THREE PERMANENT BUTTONS, and the reason is not tidiness. Bring current, Merge to
 * main and Clean up worktrees on screen at all times, each greyed for its own reasons, would offer a
 * reader three controls and no order — and the order is the entire thing a person diverged from
 * main needs to be told. `nextStep` in `lib/repoLadder.ts` answers it,
 * and this component renders that answer and decides nothing.
 *
 * THE CARD FETCHES WHEN IT OPENS. `mainBehind` counts commits on the LOCAL `origin/main` ref, which
 * only `git fetch` moves. (The header re-read runs `ls-remote`, which measures whether origin
 * ANSWERS and moves no ref at all.) Without a fetch, a clone whose origin had advanced would say
 * `behind 0`, and gating `Bring current` on that number would disable the one control that
 * corrects it. Fetching on open means the ladder is never derived from a stale view of origin.
 *
 * WHAT THE LADDER DOES NOT DECIDE. `getActionDisabledReason` below is not a rung: git missing and
 * a member Running refuse EVERY action, and the ladder cannot see either. And the client derives
 * ORDER only — every one of these endpoints enforces its own safety, and none of the refusals here
 * is the only thing standing between a click and an unsafe git operation.
 */
import { computed, onMounted, ref } from 'vue'
import type { TeamId, RepoStatus, RepoActionResult } from '../api/types'
import {
  fetchRepoAsync,
  bringRepoCurrentAsync,
  rebaseRepoAsync,
  askTeamToBringCurrent,
  ActionRefused,
  pushRepoAsync,
  mergeRepoToMainAsync,
  deleteRepoRemoteBranchAsync,
  cleanupRepoWorktreesAsync,
  getRepoStatus,
  openPullRequestAsync,
  getPullRequestDraft,
} from '../api/client'
import {
  formatMainDelta,
  formatHeadCheckout,
  formatPushedState,
  formatMergedState,
  formatOriginCheckedAt,
  formatWorktreeDelta,
  repoHeadline,
  branchWord,
  originBranchWord,
  defaultBranchOf,
  pullRequestReadAt,
} from '../lib/repoStatus'
import {
  nextStep,
  unintegratedWorktrees,
  openWorktrees,
  formatBytes,
  rungState,
  didRefreshOrigin,
  refreshFailureReason,
  LADDER_RUNGS,
  RUNG_LABELS,
  actionLabel,
} from '../lib/repoLadder'
import type { LadderAction, RungState } from '../lib/repoLadder'

interface Props {
  team: TeamId
  repo: RepoStatus
  gitResolves: boolean
  anyMemberRunning: boolean
}

const props = defineProps<Props>()

const emit = defineEmits<{
  updated: [status: RepoStatus]
  'action-failed': []
  'action-succeeded': []
}>()

const isRefreshing = ref(false)
const isActing = ref(false)
const currentAction = ref<LadderAction | null>(null)
const errorMessage = ref<string | null>(null)

/**
 * THE FILES A REBASE WOULD CONFLICT IN, when the server named them. Non-empty means the dialog
 * offers to hand the resolution to the team's Manager instead of ending at the refusal.
 */
const conflicts = ref<string[]>([])
const askingTeam = ref(false)

async function askTeam() {
  askingTeam.value = true
  try {
    const sent = await askTeamToBringCurrent(props.team, props.repo.name)
    conflicts.value = []
    errorMessage.value = null
    successMessage.value =
      `Sent to the Manager as workflow #${sent.correlationId}. When the team hands back, `
      + `refresh here, then Push and Merge to ${branchWord(props.repo)}.`
  } catch (err) {
    errorMessage.value = err instanceof Error ? err.message : String(err)
  } finally {
    askingTeam.value = false
  }
}

/**
 * WHAT WENT RIGHT, SAID IN INK RATHER THAN IN RED.
 *
 * A SEPARATE STATE FROM `errorMessage`, and it has to be. Both were one ref, so a successful
 * `Fetch origin` painted "origin fetched" in `--q-negative` under the primary control every
 * reader clicks first — a green path reporting itself as a failure, on the one button the ladder
 * exists to put in front of people.
 */
const successMessage = ref<string | null>(null)

/**
 * WHETHER THIS OPEN HAS READ ORIGIN, and the one input to the ladder that is not a `RepoStatus`
 * field. False on mount, which is what puts every card on the Refreshed rung for the moment before
 * its own fetch lands — and keeps it there if that fetch could not reach origin.
 */
const refreshedThisOpen = ref(false)

/** Said beside the figures, never instead of them. Null when the last refresh worked. */
const refreshFailure = ref<string | null>(null)

/**
 * THE ONE ACTION THAT DESTROYS ANYTHING GETS ASKED TWICE.
 *
 * A `q-dialog` IN THIS TEMPLATE, NEVER the Quasar Dialog plugin's imperative helper. It puts its
 * `class` on the inner CARD,
 * so there is no way to lift the dialog root above whatever it opens over, and `Dialog` is
 * deliberately not in the plugin list for that reason. A destructive confirmation that renders
 * behind something is the worst version of this failure: the click appears to do nothing, so the
 * natural response is to click again.
 *
 * ONLY THIS ACTION AND MERGE TO MAIN. The other five are reversible or additive - a fetch, a
 * fast-forward, a push that refuses to discard anything - and a confirmation on all of them would
 * train everybody to click through the ones that matter.
 */
const confirmDeleteOpen = ref(false)



/**
 * THE SENTENCE THIS CARD LEADS WITH. Derived, never stored - see `repoHeadline` for the precedence
 * and why "not measured" and "diverged" outrank everything below them.
 */
const headline = computed(() => repoHeadline(props.repo));

/** Every ladder action, wired to the endpoint that performs it. */
const ACTIONS: Record<LadderAction, (team: TeamId, repo: string) => Promise<RepoActionResult>> = {
  'fetch': fetchRepoAsync,
  'bring-current': bringRepoCurrentAsync,
  'rebase': rebaseRepoAsync,
  'push': pushRepoAsync,
  'merge-to-main': mergeRepoToMainAsync,
  'delete-remote-branch': deleteRepoRemoteBranchAsync,
  'cleanup-worktrees': cleanupRepoWorktreesAsync,
  // Sends what the person edited in the Open pull request dialog; see `sendPullRequest`.
  'open-pull-request': (team, repo) => openPullRequestAsync(team, repo, prTitle.value, prBody.value),
}

/** Material SYMBOLS names (outlined): an Icons-only name such as `get_app` renders as its own
    letters at the wrong width with nothing logged. */
const ACTION_ICONS: Record<LadderAction, string> = {
  'fetch': 'refresh',
  'bring-current': 'download',
  'rebase': 'sync',
  'push': 'publish',
  'merge-to-main': 'merge_type',
  'delete-remote-branch': 'delete',
  'cleanup-worktrees': 'delete_sweep',
  'open-pull-request': 'send',
}

const step = computed(() => nextStep(props.repo, refreshedThisOpen.value))

/** Merge to main asks once before it runs; see `takeStep`. */
const confirmMergeOpen = ref(false)

/**
 * OPEN PULL REQUEST IS EDITED BEFORE IT IS SENT. The draft - the backlog item's title, the
 * Manager's delivery text and the item's citation - is a starting point the server composes; the
 * person changes either before anything reaches GitHub.
 */
const pullRequestOpen = ref(false)
const prTitle = ref('')
const prBody = ref('')
const prDraftError = ref<string | null>(null)

async function openPullRequestDialog() {
  prDraftError.value = null
  pullRequestOpen.value = true
  try {
    const draft = await getPullRequestDraft(props.team, props.repo.name)
    prTitle.value = draft.title
    prBody.value = draft.body
  } catch (err) {
    prDraftError.value = err instanceof Error ? err.message : String(err)
  }
}

function sendPullRequest() {
  if (prTitle.value.trim() === '') return
  pullRequestOpen.value = false
  void runStep('open-pull-request')
}

/**
 * WHICH WORKTREES THE TIDY WOULD REFUSE OVER — the mark on the row, read off the same predicate.
 *
 * The row's mark and the step's refusal must agree. `formatWorktreeDelta` prints
 * `commitsNotOnMain`, so a mark bound from `aheadMain` would render `aheadMain 0 / commitsNotOnMain 3`
 * as "3 not on main" unmarked, directly above a tidy refusing because of those same three commits.
 * `unintegratedWorktrees` in `lib/repoLadder.ts` is the predicate the step uses; binding from it
 * keeps the two from drifting.
 *
 * BY PATH, because that is the row's identity here (`:key="wt.path"`) and it does not depend on the
 * filtered array handing back the same object references the `v-for` walks.
 */
/**
 * THE OPEN TREES AND WHAT THEY COST ON DISK. One checkout per open card, and no cap - so
 * the dialog says how many there are and how much they take, which is how a cap would ever be
 * shown to matter.
 */
const openTrees = computed(() => openWorktrees(props.repo))
const openTreesBytes = computed(() =>
  openTrees.value.reduce((sum, wt) => sum + (wt.sizeBytes ?? 0), 0))

const heldWorktreePaths = computed(
  () => new Set(unintegratedWorktrees(props.repo).map(wt => wt.path)),
)

function getActionDisabledReason(): string | null {
  if (!props.gitResolves) {
    return 'git was not found on this machine\'s PATH'
  }
  if (props.anyMemberRunning) {
    return 'a member is Running'
  }
  return null
}

const actionDisabledReason = computed(() => getActionDisabledReason())

/**
 * The caption under the one button: why nothing may run, or else why this step is the next one.
 *
 * THE GATE ONLY WINS WHEN THERE IS SOMETHING FOR IT TO REFUSE. With no action — a finished
 * repository showing five ticks, or a dirty clone — "a member is Running" implies something is
 * waiting to run and reads as the reason the button is missing. `step.reason` is the true answer in
 * both of those states.
 */
const stepCaption = computed(() =>
  step.value.action ? (actionDisabledReason.value ?? step.value.reason) : step.value.reason,
)

/**
 * `unknown` IS A REAL STATE AND MUST NOT LOOK LIKE A TICK.
 *
 * `Pushed` and `Merged` are answered from the team ref; with no team branch there is no evidence
 * either way, so rendering that as satisfied would be a lie. `help` is deliberately not
 * `check_circle` in another colour - a reader scanning five icons reads shape before hue.
 *
 * Material SYMBOLS names: `error_outline` and `help_outline` are Icons-only. The outlined Symbols
 * `error` and `help` are the same outlined shapes.
 */
/** One glyph per tone, so the sentence is legible before it is read. */
function headlineIcon(tone: string): string {
  if (tone === 'done') return 'check_circle';
  if (tone === 'attention') return 'error';
  if (tone === 'unknown') return 'help';
  return 'sync';
}

/**
 * NOT RADIO GLYPHS.
 *
 * `radio_button_checked` / `radio_button_unchecked` render a SEQUENCE as a form control: five circles
 * with one filled read as "pick one" rather than "you are here".
 * A filled dot on a connected line reads as a step; a radio never will, whatever the markup says.
 *
 * Both are `circle`: in outlined Symbols it is a ring, and `.repo-rung-current` turns the fill axis
 * on for the current rung. `panorama_fish_eye` is an Icons-only name and does not exist in Symbols.
 */
function rungIcon(state: RungState): string {
  if (state === 'satisfied') return 'check_circle'
  if (state === 'unknown') return 'help'
  return 'circle'
}

/**
 * THE ONE PLACE `refreshedThisOpen` MOVES, and it moves only on `didRefreshOrigin`.
 *
 * Both the fetch on open and a clicked Fetch land here, so the success/failure rule is stored once.
 * It was written out twice, which is two answers to one question waiting to disagree — and the one
 * that matters most on this card, since the wrong branch ticks the Refreshed rung off on a fetch
 * that read nothing.
 */
function applyRefresh(result: RepoActionResult) {
  if (didRefreshOrigin(result)) {
    refreshedThisOpen.value = true
    refreshFailure.value = null
  } else {
    refreshFailure.value = refreshFailureReason(result)
  }
}

/**
 * THE FETCH ON OPEN. The card paints from the cached status wearing its existing age suffix the
 * instant it mounts, and repaints when this lands — so a slow or unreachable origin costs the
 * reader nothing but freshness.
 *
 * IT IS SKIPPED WHEN NOTHING MAY RUN. The fetch endpoint answers 409 while a member is Running, so
 * an unconditional call would paint a red refusal across every card of every busy team at open.
 * The ladder says the same thing more usefully, on the button.
 */
async function refreshOnOpen() {
  if (getActionDisabledReason()) return

  isRefreshing.value = true
  try {
    const result = await fetchRepoAsync(props.team, props.repo.name)
    emit('updated', result.status)
    applyRefresh(result)
  } catch (err) {
    // The values and their age stay exactly where they are; only the claim of freshness is lost.
    refreshFailure.value = err instanceof Error ? err.message : String(err)
  } finally {
    isRefreshing.value = false
  }
}

onMounted(() => {
  void refreshOnOpen()
})

/**
 * The header re-read. `?refresh=true` runs `ls-remote` — it asks whether origin ANSWERS and moves
 * no ref — so it deliberately does NOT satisfy the Refreshed rung. Only a fetch does.
 */
async function refresh() {
  isRefreshing.value = true
  try {
    const result = await getRepoStatus(props.team, true)
    if (result.repos.length > 0) {
      const updated = result.repos.find((r) => r.name === props.repo.name)
      if (updated) {
        emit('updated', updated)
      }
    }
    // A re-read clears BOTH notices: whatever the last action said, it is about a state this
    // response has just replaced.
    errorMessage.value = null
    successMessage.value = null
  } catch (err) {
    errorMessage.value = err instanceof Error ? err.message : String(err)
  } finally {
    isRefreshing.value = false
  }
}

/**
 * RUN THE ONE STEP THE LADDER OFFERS.
 *
 * Every action answers the status it produced, so the card repaints from the response rather than
 * asking a second time what the first call just changed. `action-succeeded` / `action-failed` stay
 * beside that: they make the PANEL re-read, which is how the other repositories on the team, and
 * the git prerequisite itself, stay in step after an action.
 */
/**
 * WHAT THE ONE BUTTON DOES: run the step, unless it is the destructive one, which asks first.
 * The confirmation's own button calls `runStep` directly - it has already been asked.
 */
function takeStep(action: LadderAction) {
  if (action === 'delete-remote-branch') {
    confirmDeleteOpen.value = true
    return
  }

  if (action === 'open-pull-request') {
    void openPullRequestDialog()
    return
  }

  // Putting work on main is what every future team clones from, so it asks once, plainly.
  if (action === 'merge-to-main') {
    confirmMergeOpen.value = true
    return
  }

  void runStep(action)
}

function confirmMerge() {
  confirmMergeOpen.value = false
  void runStep('merge-to-main')
}

function confirmDelete() {
  confirmDeleteOpen.value = false
  void runStep('delete-remote-branch')
}

async function runStep(action: LadderAction) {
  if (getActionDisabledReason()) return

  isActing.value = true
  currentAction.value = action
  errorMessage.value = null
  successMessage.value = null
  conflicts.value = []

  try {
    const result = await ACTIONS[action](props.team, props.repo.name)
    emit('updated', result.status)

    // AN ACTION THAT REPORTS A FAILURE WITHOUT THROWING STILL FAILED. Only `fetch` has such an arm
    // today - every other endpoint refuses with a status code, which `json<T>()` throws on - but
    // reading `success` for all of them means the next endpoint to gain one is not miscoloured green.
    if (result.success) {
      successMessage.value = result.message || null
    } else {
      errorMessage.value = result.message || null
    }

    if (action === 'fetch') {
      applyRefresh(result)
    }

    emit('action-succeeded')
  } catch (err) {
    errorMessage.value = err instanceof Error ? err.message : String(err)
    const named = err instanceof ActionRefused ? err.body.conflicts : undefined
    if (action === 'rebase' && Array.isArray(named)) {
      conflicts.value = named.filter((path): path is string => typeof path === 'string')
    }
    emit('action-failed')
  } finally {
    isActing.value = false
    currentAction.value = null
  }
}

/** Two paths naming the same place, allowing for git++ backslashes. */
function samePath(a: string, b: string): boolean {
  const norm = (s: string) => s.replace(/\\/g, '/').replace(/\/+$/, '').toLowerCase()
  return norm(a) === norm(b)
}

/**
 * WHO IS IN THIS WORKTREE.
 *
 * `git worktree list` INCLUDES THE MAIN WORKING TREE, so the clone itself appears in this list
 * beside the members. Naming it after its folder renders "main  main  9d66c6e", which reads as a
 * member called main rather than as the clone everything else on this card describes.
 *
 * `member` is inferred from the path and is null for the clone and for any path that does not
 * follow the convention. `clonePath` is withheld from a machine principal and may be null, so the
 * folder name stays as the last resort rather than the only test.
 */
function worktreeWho(wt: { member: string | null; path: string }): string {
  if (wt.member) return wt.member
  // SEPARATORS DIFFER ON WINDOWS AND A RAW COMPARISON NEVER MATCHES. `clonePath` is built by
  // `Path.Combine` and carries backslashes; a worktree path comes from `git worktree list --porcelain`,
  // which emits forward slashes on Windows too. Case is folded for the same reason the platform
  // folds it elsewhere: these are Windows paths.
  if (props.repo.clonePath && samePath(wt.path, props.repo.clonePath)) return "this clone"
  const leaf = wt.path.replace(/\\/g, '/').replace(/\/+$/, '').split('/').pop()
  return leaf || 'worktree'
}
</script>

<template>
  <section class="repo-card">
    <header class="repo-head">
      <h3 class="repo-name">{{ repo.name }}</h3>

      <!-- DIRTY IS A PROPERTY OF THE CLONE, so it sits with the clone's name rather than as a row
           reading "dirty: yes". Shown only when true: a mark that is usually absent gets read when
           it appears, and one that says "clean" every time gets read by nobody. -->
      <span v-if="repo.dirty" class="repo-flag">uncommitted changes</span>

      <!-- THE FETCH ON OPEN IS THE SLOW PART, and a disabled refresh icon was all it showed. -->
      <span v-if="isRefreshing" class="repo-checking os-text-muted text-caption">Checking origin…</span>

      <button
        type="button"
        class="repo-refresh"
        :disabled="isRefreshing"
        @click="refresh"
        :aria-label="'Refresh ' + repo.name + ' status'"
      >
        <q-icon name="refresh" size="18px" />
        <q-tooltip>Re-read this clone and ask whether origin answers. Only Fetch moves origin's refs.</q-tooltip>
      </button>
    </header>

    <q-linear-progress v-if="isRefreshing" indeterminate color="primary" size="2px" class="repo-progress" />

    <!-- THE ANSWER, BEFORE THE EVIDENCE. Correct refs alone do not tell a person whether a team is
         safe to delete: `behind 5` and `pushed · … · MERGED TO MAIN` are facts, and neither is an
         answer. This says what they MEAN, and the refs are the
         supporting detail. -->
    <!-- LITERAL CLASS NAMES, NOT COMPOSED ONES. `'repo-headline-' + tone` is invisible to
         `styles-match-templates.spec.ts`, which reads the template as text - and that spec exists
         because a class relabelled in a template with its style left behind is INVISIBLE rather than
         ugly. A composed name defeats it silently, so it is spelled out. -->
    <p
      class="repo-headline"
      :class="{
        'repo-headline-done': headline.tone === 'done',
        'repo-headline-attention': headline.tone === 'attention',
        'repo-headline-unknown': headline.tone === 'unknown',
        'repo-headline-working': headline.tone === 'working',
      }"
    >
      <q-icon :name="headlineIcon(headline.tone)" size="18px" />
      <span>{{ headline.text }}</span>
    </p>

    <!-- THE MANAGER MOVED THE CLONE'S DEFAULT BRANCH. The server's own sentence, the one the
         Manager's card and the feed carry; the platform resets nothing, so it names where the work is. -->
    <p v-if="repo.defaultBranchMoved" class="repo-moved" role="alert">
      <q-icon name="warning" size="18px" />
      <span class="repo-moved-text">{{ repo.defaultBranchMoved }}</span>
    </p>

    <!-- A GRID, NOT A ROW OF GAPS. A hard first column is what lines the refs up under each other;
         a flex row lets every value begin wherever the previous string happened to end, and the
         card reads as text scattered across whitespace. -->
    <dl class="repo-facts">
      <!-- The repository's stored default branch, never an assumed `main`. -->
      <dt class="mono">{{ repo.defaultBranch ?? 'default branch' }}</dt>
      <dd :class="{ 'repo-attention': (repo.mainBehind ?? 0) > 0 }">
        {{ formatMainDelta(repo) }}
        <q-tooltip>{{ formatOriginCheckedAt(repo) }}</q-tooltip>
      </dd>

      <template v-if="repo.headCheckout && repo.headCheckout !== repo.defaultBranch">
        <dt class="mono">HEAD</dt>
        <dd>{{ formatHeadCheckout(repo) }}</dd>
      </template>

      <!-- A repository in contributor mode has two remotes, and both are named. -->
      <template v-if="repo.upstreamUrl">
        <dt class="mono">origin</dt>
        <dd class="mono repo-remote">{{ repo.originUrl ?? 'not known' }} <span class="os-text-muted">(the fork)</span></dd>
        <dt class="mono">upstream</dt>
        <dd class="mono repo-remote">{{ repo.upstreamUrl }}</dd>
      </template>

      <dt class="mono">{{ repo.teamBranch }}</dt>
      <dd>
        {{ formatPushedState(repo) }}
        <template v-if="repo.teamPushed"> &middot; {{ formatMergedState(repo) }}</template>
        <q-tooltip>{{ formatOriginCheckedAt(repo) }}</q-tooltip>
      </dd>

      <!-- The recorded pull request, linked, with GitHub's last answer and when it was read. -->
      <template v-if="repo.upstreamUrl && repo.pullRequest">
        <dt class="mono">pull request</dt>
        <dd class="repo-pull-request">
          <a :href="repo.pullRequest.url" target="_blank" rel="noopener">#{{ repo.pullRequest.number }}</a>
          &middot; {{ repo.pullRequest.state }}
          <span class="repo-pull-request-read">&middot; read {{ pullRequestReadAt(repo) ?? 'never' }}</span>
          <span v-if="repo.pullRequest.unknownReason" class="repo-pull-request-read">
            &middot; GitHub could not say what it is now: {{ repo.pullRequest.unknownReason }}
          </span>
        </dd>
      </template>
    </dl>

    <!-- A FAILED REFRESH MUST NOT EMPTY THE CARD. The figures above keep their values and their age
         suffix; this says, beside them, that the attempt to make them current did not land. -->
    <p v-if="refreshFailure" class="repo-stale">
      Could not refresh from origin — the figures above are as old as their age says.
      <span class="repo-stale-reason">{{ refreshFailure }}</span>
    </p>

    <div v-if="repo.worktrees.length > 0" class="repo-worktrees">
      <h4 class="repo-subhead">Worktrees</h4>
      <p v-if="openTrees.length > 0" class="repo-wt-open-summary">
        {{ openTrees.length }} open {{ openTrees.length === 1 ? 'tree' : 'trees' }},
        {{ formatBytes(openTreesBytes) }} on disk
      </p>
      <ul class="repo-wt-list">
        <li v-for="wt in repo.worktrees" :key="wt.path" class="repo-wt">
          <span class="repo-wt-who">{{ worktreeWho(wt) }}</span>
          <span class="repo-wt-branch mono">{{ wt.branch || 'detached' }}</span>
          <span class="repo-wt-sha mono">{{ wt.sha?.slice(0, 7) }}</span>
          <!-- Which card the tree is for, whether that card is still open (the tidy leaves
               it), and what it costs on disk. One cell, so the columns stay put without it. -->
          <span class="repo-wt-meta">
            <template v-if="wt.card">card {{ wt.card }}</template>
            <strong v-if="wt.open === true"> &middot; open</strong>
            <template v-if="wt.sizeBytes != null">{{ wt.card ? ' · ' : '' }}{{ formatBytes(wt.sizeBytes) }}</template>
          </span>
          <!-- ONLY WHEN THERE IS SOMETHING TO SAY. Printing `ahead 0, behind 1` at somebody makes
               an integrated worktree look like a problem; the row speaks only when a count
               is outstanding, or to say the worktree could not be measured at all.

               THE MARK MEANS WHAT THE TIDY REFUSAL MEANS, and nothing else. It is NOT
               `formatWorktreeDelta(wt)` being non-empty: a detached worktree still says "no branch
               to compare against main" while `unintegratedWorktrees` excludes it and the tidy is
               OFFERED, so that binding would paint not-measured as at-risk. Not measured is not at
               risk. -->
          <span
            v-if="formatWorktreeDelta(wt, defaultBranchOf(repo))"
            class="repo-wt-behind"
            :class="{ 'repo-attention': heldWorktreePaths.has(wt.path) }"
          >
            {{ formatWorktreeDelta(wt, defaultBranchOf(repo)) }}
          </span>
        </li>
      </ul>
    </div>

    <p v-if="errorMessage" class="repo-error">{{ errorMessage }}</p>
    <div v-if="conflicts.length" class="repo-conflicts">
      <div class="text-caption">It conflicts in:</div>
      <ul class="repo-conflict-files">
        <li v-for="path in conflicts" :key="path" class="mono">{{ path }}</li>
      </ul>
      <q-btn
        outline
        no-caps
        dense
        icon="send"
        label="Ask the team to resolve it"
        :loading="askingTeam"
        @click="askTeam"
      >
        <q-tooltip>Sends the Manager these files and an instruction to merge {{ originBranchWord(repo) }} into {{ branchWord(repo) }} and resolve them</q-tooltip>
      </q-btn>
    </div>
    <p v-if="successMessage" class="repo-success">{{ successMessage }}</p>

    <!-- THE FIVE RUNGS, IN THE ORDER THEY ARE CLIMBED. Satisfied, standing here, not yet reached -
         three states rather than a tick and a blank, because the rung a reader is STANDING on is
         the one thing on this card they need to find. -->
    <ol class="repo-ladder">
      <li
        v-for="rung in LADDER_RUNGS"
        :key="rung"
        class="repo-rung"
        :class="{
          'repo-rung-satisfied': rungState(rung, step, repo) === 'satisfied',
          'repo-rung-current': rungState(rung, step, repo) === 'current',
          'repo-rung-pending': rungState(rung, step, repo) === 'pending',
          'repo-rung-unknown': rungState(rung, step, repo) === 'unknown',
        }"
        :title="rungState(rung, step, repo) === 'unknown'
          ? 'This team has no branch on origin, so there is nothing to compare - work may be on a member branch.'
          : undefined"
      >
        <q-icon :name="rungIcon(rungState(rung, step, repo))" size="16px" />
        <span class="repo-rung-label">{{ RUNG_LABELS[rung] }}</span>
      </li>
    </ol>

    <!-- EXACTLY ONE ENABLED ACTION, and when there is none the reason stands alone. A control that
         is grey with no explanation is the one people click twice and then report as broken. -->
    <footer class="repo-actions">
      <button
        v-if="step.action"
        type="button"
        class="repo-action"
        :disabled="isActing || isRefreshing || !!actionDisabledReason"
        @click="takeStep(step.action)"
      >
        <!-- The open fetch counts too: while it is in flight this button is disabled, and a grey
             button with its ordinary icon reads as refused rather than as busy. -->
        <q-icon
          :name="currentAction || isRefreshing ? 'hourglass_empty' : ACTION_ICONS[step.action]"
          size="18px"
        />
        {{ actionLabel(step.action, repo) }}
        <q-tooltip>{{ stepCaption }}</q-tooltip>
      </button>

      <!-- The stored CLA note, beside the button it is about. A record; nothing is signed. -->
      <p v-if="step.action === 'open-pull-request' && repo.claSignedNote" class="repo-cla-note">
        CLA: {{ repo.claSignedNote }}
      </p>

      <p class="repo-next-reason">{{ stepCaption }}</p>
    </footer>

    <!-- The two actions that change what other people see confirm first, and each names what it
         touches, because "are you sure?" over an unnamed object is a question nobody can answer.
         `class="repo-confirm"` is the z-index lift; see the rule in `css/app.scss` for why a
         teleported dialog needs one. -->
    <q-dialog v-model="confirmMergeOpen" class="repo-confirm">
      <q-card class="repo-confirm-card os-dialog-sm">
        <q-card-section class="os-dialog-title">Merge to {{ branchWord(repo) }}?</q-card-section>

        <q-card-section class="q-pt-none">
          <p class="repo-confirm-line">
            This merges <span class="repo-confirm-ref mono">{{ repo.teamBranch }}</span> into
            <span class="mono">{{ branchWord(repo) }}</span> and pushes it to <span class="mono">{{ originBranchWord(repo) }}</span>,
            which every future team clones from.
          </p>
        </q-card-section>

        <q-card-actions align="right">
          <q-btn v-close-popup flat label="Cancel" />
          <q-btn flat color="primary" label="Merge" @click="confirmMerge" />
        </q-card-actions>
      </q-card>
    </q-dialog>

    <q-dialog v-model="pullRequestOpen" class="repo-confirm">
      <q-card class="repo-confirm-card os-dialog-sm">
        <q-card-section class="os-dialog-title">Open a pull request upstream</q-card-section>

        <q-card-section class="q-pt-none">
          <p class="repo-confirm-line">
            From <span class="repo-confirm-ref mono">{{ repo.forkOwner ?? 'the fork' }}:{{ repo.teamBranch }}</span>
            into <span class="mono">{{ repo.upstreamUrl }}</span>
            <span class="mono">{{ repo.defaultBranch }}</span>.
          </p>
          <p v-if="prDraftError" class="repo-error">{{ prDraftError }}</p>
          <q-input v-model="prTitle" label="Title" dense outlined :maxlength="256" class="repo-pr-field" />
          <q-input v-model="prBody" label="Body" type="textarea" dense outlined autogrow class="repo-pr-field" />
          <p v-if="repo.dcoSignOff" class="repo-confirm-line">
            Sign off commits (DCO) is on: every commit on {{ repo.teamBranch }} must carry Signed-off-by, or nothing is sent.
          </p>
          <p v-if="repo.claSignedNote" class="repo-cla-note">CLA: {{ repo.claSignedNote }}</p>
        </q-card-section>

        <q-card-actions align="right">
          <q-btn v-close-popup flat label="Cancel" />
          <q-btn flat color="primary" label="Open pull request" :disable="prTitle.trim() === ''" @click="sendPullRequest" />
        </q-card-actions>
      </q-card>
    </q-dialog>

    <q-dialog v-model="confirmDeleteOpen" class="repo-confirm">
      <q-card class="repo-confirm-card os-dialog-sm">
        <q-card-section class="os-dialog-title">Delete {{ repo.teamBranch }} on origin?</q-card-section>

        <q-card-section class="q-pt-none">
          <p class="repo-confirm-line">
            This removes the branch
            <span class="repo-confirm-ref mono">origin/{{ repo.teamBranch }}</span>
            from origin. Nothing local is touched.
          </p>
          <p class="repo-confirm-line">
            It is safe because the work on that branch is already on
            <span class="mono">{{ branchWord(repo) }}</span>, which is the only state this action is offered in — and
            the server checks it again before deleting.
          </p>
        </q-card-section>

        <q-card-actions align="right">
          <q-btn v-close-popup flat label="Cancel" />
          <q-btn flat color="negative" label="Delete the branch" @click="confirmDelete" />
        </q-card-actions>
      </q-card>
    </q-dialog>
  </section>
</template>

<style scoped>
/* The recorded pull request and the CLA note: quiet facts beside the ladder. */
.repo-pull-request a {
  color: var(--q-primary);
}

.repo-pull-request-read,
.repo-cla-note {
  color: var(--os-muted);
}

.repo-cla-note {
  margin: 0;
  font-size: 12px;
}

.repo-pr-field {
  margin-top: 8px;
}

/* Inside the Git dialog these stack, so the card is a band with a rule under it rather than a box
   with its own border: boxes inside a box is the SaaS-card default, and it reads as three unrelated
   panels rather than one list of repositories. */
.repo-card {
  display: flex;
  flex-direction: column;
  gap: 10px;
  padding: 14px 2px 18px;
  border-bottom: 1px solid var(--os-rule);
}

.repo-card:last-of-type {
  border-bottom: 0;
  padding-bottom: 4px;
}

.repo-head {
  display: flex;
  align-items: baseline;
  gap: 10px;
}

/* THE REPOSITORY NAME IS THE OBJECT ON THIS CARD and everything else describes it, so it is the one
   thing here allowed to be large. */
.repo-name {
  margin: 0;
  font-size: 1.05rem;
  font-weight: 600;
  letter-spacing: -0.01em;
  color: var(--os-ink);
}

.repo-flag {
  padding: 1px 7px;
  border: 1px solid var(--q-warning);
  border-radius: 999px;
  font-size: 0.7rem;
  color: var(--q-warning);
  white-space: nowrap;
}

.repo-refresh {
  margin-left: auto;
  display: inline-flex;
  align-items: center;
  padding: 4px;
  border: 0;
  border-radius: 4px;
  background: transparent;
  color: var(--os-ink-faint);
  cursor: pointer;
}

.repo-refresh:hover:not(:disabled) {
  background: var(--os-chrome);
  color: var(--os-ink);
}

.repo-refresh:disabled {
  opacity: 0.45;
  cursor: not-allowed;
}

/* THE ALIGNMENT FIX. `max-content` sizes the first column to the LONGEST ref rather than to a
   guessed width, so `main` and `team/os-agent-verif` share an edge whatever the team is called and
   nothing has to be truncated to fit a number somebody picked. */
.repo-facts {
  display: grid;
  grid-template-columns: max-content 1fr;
  gap: 4px 16px;
  margin: 0;
  font-size: 0.85rem;
}

.repo-facts dt {
  color: var(--os-ink-muted);
}

.repo-facts dd {
  margin: 0;
  color: var(--os-ink);
}

/* A remote URL is long and unbroken; it wraps rather than widening the card. */
.repo-remote {
  overflow-wrap: anywhere;
}

/* MONO IS FOR THINGS YOU MIGHT COPY - a ref, a sha - and not for labels. The state beside them is
   prose and stays in the interface face, which is also what stops the two columns reading as one
   undifferentiated block of code. */
.mono {
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  font-size: 0.82rem;
}

/* THE ONLY COLOUR IN THE CARD, and it means one thing: this number is why an action exists. Applied
   to being behind, never to being ahead - ahead is ordinary and colouring it would make the card
   shout on every healthy repository. */
.repo-attention {
  color: var(--q-warning);
}

/* The clone's default branch was moved: a person has to act, and nothing will act for them. */
.repo-moved {
  display: flex;
  align-items: flex-start;
  gap: 6px;
  margin: 0;
  font-size: 0.82rem;
  color: var(--q-warning);
  overflow-wrap: anywhere;
}

.repo-moved-text {
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
}

/* A REFRESH THAT DID NOT LAND. Muted rather than red: nothing is broken and nothing was lost, the
   figures are simply as old as they already said they were. */
.repo-stale {
  margin: 0;
  font-size: 0.78rem;
  color: var(--os-ink-muted);
}

.repo-stale-reason {
  display: block;
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  font-size: 0.72rem;
  color: var(--os-ink-faint);
}

.repo-worktrees {
  margin-top: 2px;
}

.repo-subhead {
  margin: 0 0 6px;
  font-size: 0.72rem;
  font-weight: 600;
  color: var(--os-ink-muted);
}

.repo-wt-list {
  margin: 0;
  padding: 0;
  list-style: none;
  display: flex;
  flex-direction: column;
  gap: 3px;
}

/* Four columns rather than a flex row for the same reason as the facts grid: with several members
   the shas and the behind-counts have to line up or the eye cannot scan down them. */
.repo-wt {
  display: grid;
  grid-template-columns: minmax(6rem, max-content) minmax(5rem, 1fr) max-content max-content max-content;
  align-items: baseline;
  gap: 12px;
  font-size: 0.8rem;
  color: var(--os-ink-muted);
}

.repo-wt-who {
  color: var(--os-ink);
}

.repo-wt-sha {
  color: var(--os-ink-faint);
}

.repo-wt-meta,
.repo-wt-open-summary {
  color: var(--os-ink-faint);
}

.repo-wt-open-summary {
  margin: 0 0 4px;
  font-size: 0.8rem;
}

.repo-wt-behind {
  text-align: right;
  font-size: 0.75rem;
}

.repo-error {
  margin: 0;
  font-size: 0.8rem;
  color: var(--q-negative);
}

/* WHAT WENT RIGHT, IN INK. Deliberately NOT a modifier on `.repo-error` - a modifier leaves the
   negative colour applied unless it is overridden, which is one careless edit away from red again.
   Muted rather than a success green for the same reason `.repo-stale` is muted: nothing needs
   attention here, the action did what it said. */
.repo-success {
  margin: 0;
  font-size: 0.8rem;
  color: var(--os-ink-muted);
}

/* THE LADDER READS LEFT TO RIGHT and wraps rather than scrolling: five short words fit on one line
   on a laptop and on two on a phone, and a horizontal scroller would hide the rung the reader is
   standing on exactly when the card is narrowest. */
.repo-ladder {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 4px 14px;
  margin: 2px 0 0;
  padding: 0;
  list-style: none;
}

/* A LINE IS WHAT MAKES IT A SEQUENCE. Five dots with gaps between them is a radio group however
   the markup is written; a connector says these are steps on one path. Drawn BEFORE each rung
   except the first, so it never dangles off either end, and `flex-wrap` above means it simply
   disappears on the row a phone breaks - which is the right failure: a broken line is worse than
   none. */
.repo-rung {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  font-size: 0.78rem;
}

.repo-rung + .repo-rung::before {
  content: '';
  width: 14px;
  height: 2px;
  margin-right: 9px;
  border-radius: 1px;
  background: currentColor;
  opacity: 0.35;
}

/* THE RUNG BEING STOOD ON IS THE ONE THING TO FIND, so it is the only one carrying weight. */
.repo-rung-current .repo-rung-label {
  font-weight: 600;
}

/* THE ANSWER, STYLED AS AN ANSWER. Tone is carried by an icon AND a colour AND a word - never by
   colour alone, which says nothing to a reader who cannot see it. */
.repo-headline {
  display: flex;
  align-items: flex-start;
  gap: 8px;
  margin: 10px 0 2px;
  font-size: 0.92rem;
  line-height: 1.35;
}

.repo-headline-done {
  color: var(--os-ok);
}

.repo-headline-attention {
  color: var(--os-warn);
}

.repo-headline-unknown,
.repo-headline-working {
  color: var(--os-muted);
}

.repo-rung-label {
  white-space: nowrap;
}

/* THREE STATES, THREE WEIGHTS. Satisfied recedes, pending recedes further, and the rung being stood
   on is the only one that carries any emphasis - so the card answers "where am I" before it is
   read at all. */
.repo-rung-satisfied {
  color: var(--os-ink-muted);
}

.repo-rung-current {
  color: var(--os-ink);
  font-weight: 600;
}

.repo-rung-current :deep(.q-icon) {
  font-variation-settings: 'FILL' 1;
}

.repo-rung-pending {
  color: var(--os-ink-faint);
}

/* A rung whose evidence does not exist. It reads like the pending rungs rather than the
   satisfied ones, which is the honest direction: not reached is closer to the truth than done. */
.repo-rung-unknown {
  color: var(--os-ink-faint);
  font-style: italic;
}

.repo-actions {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px 12px;
  margin-top: 4px;
}

/* THE REASON IS BESIDE THE BUTTON, NOT ONLY INSIDE A TOOLTIP, because when `action` is null there
   is no control left to hover. */
.repo-next-reason {
  margin: 0;
  font-size: 0.78rem;
  color: var(--os-ink-muted);
}

/* A REAL BUTTON. This was icon-and-text with no border, so a disabled one was indistinguishable
   from a caption and an enabled one did not look pressable. Everything it can run is irreversible
   enough to deserve an edge. */
.repo-action {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 5px 11px;
  border: 1px solid var(--os-rule-strong);
  border-radius: 5px;
  background: var(--os-surface);
  font: inherit;
  font-size: 0.8rem;
  color: var(--os-ink);
  cursor: pointer;
}

.repo-action:hover:not(:disabled) {
  background: var(--os-chrome);
  border-color: var(--os-ink-faint);
}

.repo-action:focus-visible {
  outline: 2px solid var(--os-ink);
  outline-offset: 2px;
}

.repo-action:disabled {
  background: var(--os-chrome);
  border-color: var(--os-rule);
  color: var(--os-ink-faint);
  cursor: not-allowed;
}

.repo-confirm-line {
  margin: 0 0 8px;
  font-size: 0.85rem;
  color: var(--os-ink);
}

.repo-confirm-line:last-child {
  margin-bottom: 0;
}

/* THE REF IS THE OBJECT OF THE QUESTION, set in mono like every other machine-addressable value in
   this app - it is the thing that will stop existing, and it has to be readable as a name. */
.repo-confirm-ref {
  color: var(--q-negative);
}
.repo-conflicts {
  margin: 0.25rem 0 0.5rem;
}

.repo-conflict-files {
  margin: 0.25rem 0 0.5rem;
  padding-left: 1.25rem;
  font-size: 0.8rem;
}
.repo-checking {
  margin-left: 0.5rem;
}

.repo-progress {
  margin: 0.25rem 0 0.5rem;
}
</style>
