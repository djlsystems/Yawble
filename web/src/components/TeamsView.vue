<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue';
import { useQuasar } from 'quasar';
import {
  archiveCheck,
  archiveTeam,
  cloneTeam,
  deleteTeam,
  listLocalRepos,
  retryRemoval,
  pauseTeam,
  resumeTeam,
  TeamDeletionConfirmationRequired,
  unarchiveTeam,
} from '../api/client';
import { refusalStatus } from '../lib/forms';
import { useConsoleStore } from '../stores/console';
import { useWipStore, wipLine } from '../stores/wip';
import { StalledBadgeGrace } from '../lib/teamKpis';
import {
  DefaultTeamSort,
  compareTeams,
  nextSort,
  proposeCloneName,
  readTeamSort,
  readTeamsShown,
  teamRowFrom,
  teamShown,
  teamStatusText,
  writeTeamSort,
  type TeamRow,
  type TeamSort,
  type TeamsShown,
} from '../lib/teamsTable';
import { ariaSort } from '../lib/tableSort';
import { localDay } from '../lib/localTime';
import type { ArchiveCheck, LocalRepo, Team, TeamDeleted, TeamId } from '../api/types';
import { isLocalRepoReference } from '../lib/rules';
import UnfinishedRemovals from './UnfinishedRemovals.vue';
import TeamStatisticsTile from './TeamStatisticsTile.vue';
import { type ActivityRange, sharedWindow } from '../lib/teamActivity';
import TeamStatusIcon from './TeamStatusIcon.vue';
import { vResizableColumns } from '../lib/resizableColumns';

const board = useConsoleStore();
const $q = useQuasar();

const SortKey = 'harness.teamSort';

/** Wrapped: storage throws in a private window with cookies blocked, and a sort is not worth a crash. */
function storedSort(): TeamSort {
  try {
    return readTeamSort(localStorage.getItem(SortKey));
  } catch {
    return DefaultTeamSort;
  }
}

const sort = ref<TeamSort>(storedSort());

const RelativeKey = 'harness.teamsActivityRelative';

/**
 * THE ACTIVITY COLUMN'S RELATIVE VIEW: off, each chart runs over its own team's window; on, every
 * chart runs over one axis, the earliest start to the latest end of every team with a chart, so a
 * team waiting for a slot sits under the team holding it. Kept per browser, like the sort.
 */
function storedRelative(): boolean {
  try {
    return localStorage.getItem(RelativeKey) === '1';
  } catch {
    return false;
  }
}

const relative = ref(storedRelative());

function setRelative(on: boolean) {
  relative.value = on;
  try {
    localStorage.setItem(RelativeKey, on ? '1' : '0');
  } catch {
    // Not kept: the switch still works for this visit.
  }
}

/** Each row's own window, as its chart reports it; `null` for a team with no chart. */
const activityWindows = ref<Record<string, ActivityRange | null>>({});

function noteWindow(teamId: string, range: ActivityRange | null) {
  activityWindows.value = { ...activityWindows.value, [teamId]: range };
}

/** The one axis every chart shares while relative is on: over the rows listed, nothing else. */
const sharedRange = computed(() =>
  relative.value ? sharedWindow(rows.value.map((row) => activityWindows.value[row.id] ?? null)) : null);

const ShownKey = 'harness.teamsShown';

/** Show active and Show archived, remembered per browser. Wrapped for the same reason the sort is. */
function storedShown(): TeamsShown {
  try {
    return readTeamsShown(localStorage.getItem(ShownKey));
  } catch {
    return readTeamsShown(null);
  }
}

const shown = ref<TeamsShown>(storedShown());

function setShown(which: keyof TeamsShown, value: boolean) {
  shown.value = { ...shown.value, [which]: value };

  try {
    localStorage.setItem(ShownKey, JSON.stringify(shown.value));
  } catch {
    // Still applies for this session.
  }
}

/**
 * THE SAME CLOCK THE TAB STRIP KEEPS, and for the same reason: `UNDECLARED` has a grace period, so a
 * team's status is a function of TIME as well as of state - and a team that has stopped publishes
 * nothing by definition, so no push and no fetch will ever arrive to re-evaluate it. Without a tick
 * the Status column would simply never reach the word.
 *
 * It fetches nothing and wakes nobody: it re-reads refs this page already holds.
 */
const BadgeClockTick = StalledBadgeGrace / 8;
const now = ref(Date.now());
let badgeClock: ReturnType<typeof setInterval> | null = null;

/** The ledger, for each row's `2 running, 1 waiting` - the same copy the header and board read. */
const wip = useWipStore();

function slotLine(team: TeamId): string {
  return wipLine(wip.runningFor(team).length, wip.waitingFor(team).length);
}

onMounted(() => {
  badgeClock = setInterval(() => (now.value = Date.now()), BadgeClockTick);
  wip.watch();
});

onUnmounted(() => {
  wip.unwatch();
  if (badgeClock !== null) clearInterval(badgeClock);
  badgeClock = null;
});

/**
 * The six SORTABLE columns, in order. Wording and order only - every RULE about them (what they
 * sort on, what the last one says) is a tested function in `lib/teamsTable.ts`. The actions
 * column beside them (pause, clone, delete) is not one of these: it carries no heading and cannot be sorted on.
 *
 * `Workflows` sits BETWEEN `Status` and `Last workflow` on purpose: `status` is the roster answer,
 * `workflows` is the plural projection beside it, and `Last workflow` is a third question again
 * (when did the newest one last move) - three different questions
 * in a row, not one growing into three.
 */
const Headings: { key: TeamSort['column']; label: string }[] = [
  { key: 'name', label: 'Team' },
  { key: 'members', label: 'Members' },
  { key: 'status', label: 'Status' },
];

/** What the click MEANS is `nextSort`'s; persisting the answer is this component's. */
function sortBy(column: TeamSort['column']) {
  sort.value = nextSort(sort.value, column);

  try {
    localStorage.setItem(SortKey, writeTeamSort(sort.value));
  } catch {
    // The sort still applies for this session.
  }
}

const rows = computed<TeamRow[]>(() =>
  board.teams
    .filter((team) => teamShown(team, shown.value))
    .map((team) => teamRowFrom(team, board.workflowFor(team.id), board.workflowsFor(team.id), now.value))
    .sort((a, b) => compareTeams(a, b, sort.value)),
);

/**
 * Activating a row is a team switch AND a view change, in that order: the board must be the view
 * before a team is made active, or `setActiveTeam` writes the current team to the server while the
 * table is still what is on screen.
 */
function open(id: TeamId) {
  // An archived team has no board to open: it has no tab until it is unarchived.
  if (board.teams.find((team) => team.id === id)?.archived === true) return;

  board.openTeam(id);
}

/**
 * Deletion lives here, on the one screen listing teams - two screens listing teams is how two
 * screens start disagreeing. Offered to every signed-in person, matching the route: every person
 * is an administrator, so there is nobody to hide it from.
 */

/** The team being deleted. Null closes the confirmation. */
const doomed = ref<Team | null>(null);
const typed = ref('');
const deleteLosses = ref<string[]>([]);
const deletionConfirmation = ref<string | null>(null);
/** The server's sentence when it refused the deletion because the team is not quiet. */
const deleteRefusal = ref('');
const busy = ref(false);

/**
 * A DELETION WHOSE FOLDER COULD NOT BE REMOVED WHOLE: the team is gone, its root keeps its marker
 * and is recorded as a removal unfinished, and this names every remaining path and offers the retry
 * right here. The Host also retries it at its next start. Null when there is nothing to report.
 */
const unfinished = ref<{ team: string; root: string; remaining: string[]; note: string | null } | null>(null);
const retrying = ref(false);

/** The list of EVERY unfinished removal below the table, read again when this page adds or retries one. */
const removalsList = ref<InstanceType<typeof UnfinishedRemovals> | null>(null);

async function retryUnfinished() {
  if (!unfinished.value) return;

  const shown = unfinished.value;
  retrying.value = true;

  try {
    const { retried } = await retryRemoval(shown.root);
    const result = retried[0];

    if (!result || result.finished) {
      $q.notify({ type: 'positive', timeout: 5000, message: `What was left of ${shown.team} has been removed.` });
      unfinished.value = null;
    } else {
      unfinished.value = { ...shown, remaining: result.remaining, note: result.note };
    }
    void removalsList.value?.load();
  } catch (cause) {
    $q.notify({ type: 'negative', message: cause instanceof Error ? cause.message : String(cause) });
  } finally {
    retrying.value = false;
  }
}

/**
 * NO TYPING FOR THE ORDINARY CASE; A TYPED CONFIRMATION ONLY WHEN WORK WOULD BE LOST.
 *
 * This screen is where a finished team is closed out, teams are deleted in batches once their work
 * is merged, and retyping a name like `b13-team-created-anywhere-else` eight times teaches the
 * reader to stop reading the list of what is destroyed - which is the protection that actually
 * matters.
 *
 * The server REFUSES a deletion that would discard commits on no remote, handing back what would be
 * lost and an exact confirmation string. That is friction with something behind it, unlike a prompt
 * that fires every time and says the same thing whether a team holds unpushed work or nothing.
 *
 * So: no typing when there is nothing to lose, and a typed confirmation of the SERVER'S string -
 * not the team's name - when there is. `confirmed` is vacuously true in the ordinary case, which is
 * what keeps the button live for a batch delete.
 */
/**
 * The doomed team's local repositories, which deletion KEEPS: they live outside the team's folder
 * and show as unused in Admin -> Repositories, where a person may delete them. Said in the dialog,
 * because "permanently removes" above would otherwise read as including them.
 */
const doomedLocalRepos = computed(() => (doomed.value?.repos ?? []).filter(isLocalRepoReference));

/**
 * "ALSO DELETE ITS LOCAL REPOSITORY", off by default, one per `local:<name>` the team uses,
 * with what is lost read from `GET /api/local-repos`. Offered only for one NO OTHER team uses: one
 * another team uses is named with that team and gets no box. A URL never gets one - it is not the
 * platform's to delete - and neither does one the list could not describe. Ticked, the Host deletes
 * it after the team, through Admin -> Repositories' own delete.
 */
const localRepoList = ref<LocalRepo[] | null>(null);
const deleteLocalRepos = ref<string[]>([]);

interface DoomedLocalRepo {
  reference: string
  repo: LocalRepo | null
  /** The other teams using it, by name: non-empty means kept, with no box. */
  others: string[]
}

const doomedLocalRepoRows = computed<DoomedLocalRepo[]>(() => doomedLocalRepos.value.map((reference) => {
  const repo = localRepoList.value?.find((r) => r.reference === reference.trim()) ?? null;
  const others = (repo?.teams ?? [])
    .filter((id) => id !== doomed.value?.id)
    .map((id) => board.teams.find((team) => team.id === id)?.name ?? id);
  return { reference, repo, others };
}));

const offeredLocalRepos = computed(() => doomedLocalRepoRows.value.filter((row) => row.repo !== null && row.others.length === 0));
const sharedLocalRepos = computed(() => doomedLocalRepoRows.value.filter((row) => row.others.length > 0));

/** The team's local repositories this deletion keeps and a person may delete later: every one not
 *  ticked, except one another team uses, which is named with that team instead. */
const keptLocalRepos = computed(() => doomedLocalRepos.value.filter((reference) =>
  !deleteLocalRepos.value.includes(reference) && !sharedLocalRepos.value.some((row) => row.reference === reference)));

/** What deleting one loses, in one line: its commits, its branches and its last commit's date. */
function losesLine(repo: LocalRepo): string {
  const commits = repo.commitCount == null
    ? 'commits not counted'
    : `${repo.commitCount} commit${repo.commitCount === 1 ? '' : 's'}`;
  const branches = (repo.branches ?? []).length > 0
    ? `on ${repo.branches!.length === 1 ? 'branch' : 'branches'} ${repo.branches!.join(', ')}`
    : 'on no branch';
  const last = repo.lastCommit?.committedAt ? `, last commit ${localDay(repo.lastCommit.committedAt)}` : '';
  return `${commits} ${branches}${last}`;
}

async function loadLocalRepos() {
  localRepoList.value = null;
  if (doomedLocalRepos.value.length === 0) return;

  try {
    localRepoList.value = await listLocalRepos();
  } catch {
    // Nothing is offered: a box whose loss cannot be said is not one to tick.
    localRepoList.value = null;
  }
}

const confirmed = computed(() =>
  doomed.value !== null
  && (deletionConfirmation.value === null || typed.value.trim() === deletionConfirmation.value));

function ask(team: Team | null) {
  doomed.value = team;
  typed.value = '';
  deleteLosses.value = [];
  deletionConfirmation.value = null;
  deleteRefusal.value = '';
  deleteLocalRepos.value = [];
  if (team) void loadLocalRepos();
}

/** A row carries only what the table renders (`TeamRow`); deletion needs the full `Team` that
 *  `ask` and `remove` both read, so the click looks it back up from the same list the row came from. */
function teamFor(row: TeamRow): Team | null {
  return board.teams.find((team) => team.id === row.id) ?? null;
}

async function remove() {
  if (!doomed.value) return;

  const team = doomed.value;

  busy.value = true;

  try {
    // Only boxes still offered: a repository another team took up since is not sent.
    const ticked = deleteLocalRepos.value.filter((reference) => offeredLocalRepos.value.some((row) => row.reference === reference));
    const removed: TeamDeleted = ticked.length > 0
      ? await deleteTeam(team.id, deletionConfirmation.value ?? undefined, ticked)
      : await deleteTeam(team.id, deletionConfirmation.value ?? undefined);
    const repoFailures = removed.localRepositoryFailures ?? [];

    // The team is gone whether or not every directory went with it, so this is positive either way
    // — but a failure has to be SAID, because the alternative is files left on disk that nobody is
    // ever told about. Usually a child process that has not finished exiting.
    if ((removed.remaining?.length ?? 0) > 0 && removed.removalUnfinished) {
      unfinished.value = {
        team: team.name,
        root: removed.removalUnfinished,
        remaining: removed.remaining ?? [],
        note: null,
      };
      void removalsList.value?.load();
    } else if (removed.failures.length > 0 || repoFailures.length > 0) {
      $q.notify({
        type: 'warning',
        timeout: 12000,
        multiLine: true,
        message:
          `${team.name} was deleted, but some of it could not be removed: `
          + [
            ...removed.failures,
            // The repository and why, with the Repositories dialog as the way to finish it.
            ...repoFailures.map((failure) => `${failure.reference} was not deleted: ${failure.reason}`),
          ].join('; '),
      });
    } else {
      $q.notify({
        type: 'positive',
        timeout: 5000,
        message: `${team.name} and its ${removed.containers} Agent Container(s) were deleted.`
          + ((removed.localRepositoriesDeleted?.length ?? 0) > 0
            ? ` Deleted: ${removed.localRepositoriesDeleted!.join(', ')}.`
            : '')
          + ((removed.localRepositoriesKept?.length ?? 0) > 0
            ? ` Kept: ${removed.localRepositoriesKept!.join(', ')} - delete it from Admin → Repositories.`
            : ''),
      });
    }

    ask(null);

    // The board holds this team, and may have it ACTIVE. Refreshing reconciles both — the store
    // clears a selection that no longer names a team it holds.
    await board.refreshForTeamDeleted(team.id);
  } catch (cause) {
    if (cause instanceof TeamDeletionConfirmationRequired) {
      deleteLosses.value = cause.losses;
      deletionConfirmation.value = cause.confirmation;
      typed.value = '';
      return;
    }

    // Not quiet: said in the dialog, where the person asked, naming what is still going.
    if (refusalStatus(cause) === 409 && cause instanceof Error) {
      deleteRefusal.value = cause.message;
      return;
    }

    $q.notify({
      type: 'negative',
      message: cause instanceof Error ? cause.message : String(cause),
    });
  } finally {
    busy.value = false;
  }
}

/**
 * Cloning, beside deletion for the same reason: two screens listing teams is how two screens start
 * disagreeing. Offered to every signed-in person, matching `POST /api/teams/{team}/clone`.
 */

/** The team being cloned, and the name typed for the copy. Null closes the dialog. */
const cloning = ref<Team | null>(null);
const cloneName = ref('');
const cloneBusy = ref(false);
const pauseBusy = ref<TeamId | ''>('');

/** What the server refused, if anything - shown against the NAME field rather than as a toast,
 *  because every refusal this route makes (a collision on the derived identifier, an empty or
 *  over-long name, a source team with no chosen Prompt) is fixed by typing something different. */
const cloneError = ref('');

/** Matching `TeamRegistry.MaximumLabelLength`, which the server checks too - same rule as
 *  `CreateTeamDialog`'s own name field. */
const CloneNameMaximumLength = 60;

const cloneNameRules = [
  (value: string) => value.trim().length > 0 || 'A team needs a name.',
  (value: string) =>
    value.trim().length <= CloneNameMaximumLength || `At most ${CloneNameMaximumLength} characters.`,
];

const cloneNameIsLegal = () =>
  cloneName.value.trim().length > 0 && cloneName.value.trim().length <= CloneNameMaximumLength;

function askClone(team: Team | null) {
  cloning.value = team;
  cloneError.value = '';

  // A CONVENIENCE ONLY: `proposeCloneName` never talks to the server, so this can collide with a
  // team created a moment ago by somebody else. Availability is decided on the DERIVED identifier,
  // which only the server can compute (`DeriveName` is lossy and lives in C#) - so a collision here
  // comes back as an ordinary 409 against this same field rather than being caught client-side.
  cloneName.value = team === null
    ? ''
    : proposeCloneName(team.name, board.teams.map((entry) => entry.name));
}

async function clone() {
  if (!cloning.value || !cloneNameIsLegal()) return;

  const source = cloning.value;

  cloneBusy.value = true;
  cloneError.value = '';

  try {
    const cloned = await cloneTeam(source.id, cloneName.value.trim());

    // The team exists either way, so this is positive regardless - but a failure has to be SAID,
    // the same reasoning as `remove` above: a member the clone could not hire is a fact the person
    // needs, not a detail the platform quietly dropped.
    if (cloned.failures.length > 0) {
      $q.notify({
        type: 'warning',
        timeout: 12000,
        multiLine: true,
        message:
          `${cloned.team.name} was cloned from ${source.name}, but some of it could not be `
          + `carried over: ${cloned.failures.join('; ')}`,
      });
    } else {
      $q.notify({
        type: 'positive',
        timeout: 5000,
        message: `${cloned.team.name} was cloned from ${source.name}.`,
      });
    }

    askClone(null);

    // Refresh BEFORE opening the new team, or `open`'s `setActiveTeam` races the board still not
    // holding the team it is about to select.
    await board.refresh();
    open(cloned.team.id);
  } catch (cause) {
    cloneError.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    cloneBusy.value = false;
  }
}

/**
 * ARCHIVING: kept whole, doing no work, out of the way, and still a template to clone. The dialog
 * reads `archive-check` for the open workflows it lists; a refusal is the server's sentence.
 */
const archiving = ref<Team | null>(null);
const archiveFacts = ref<ArchiveCheck | null>(null);
const archiveRefusal = ref('');
const archiveBusy = ref(false);
const unarchiveBusy = ref<TeamId | ''>('');

async function askArchive(team: Team | null) {
  archiving.value = team;
  archiveFacts.value = null;
  archiveRefusal.value = '';
  if (!team) return;

  try {
    const facts = await archiveCheck(team.id);
    if (archiving.value?.id !== team.id) return;
    archiveFacts.value = facts;
    if (!facts.quiet && facts.reason) archiveRefusal.value = facts.reason;
  } catch (cause) {
    if (archiving.value?.id === team.id) archiveRefusal.value = cause instanceof Error ? cause.message : String(cause);
  }
}

async function archive() {
  if (!archiving.value || archiveBusy.value) return;

  const team = archiving.value;
  archiveBusy.value = true;
  archiveRefusal.value = '';

  try {
    await archiveTeam(team.id);
    void askArchive(null);
    await board.refresh();
    board.refreshRollupIfShowing();
    $q.notify({
      type: 'positive',
      timeout: 5000,
      message: `${team.name} is archived. Tick Show archived to find it.`,
    });
  } catch (cause) {
    archiveRefusal.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    archiveBusy.value = false;
  }
}

async function unarchive(team: Team | null) {
  if (!team || unarchiveBusy.value) return;

  unarchiveBusy.value = team.id;

  try {
    await unarchiveTeam(team.id);
    await board.refresh();
    board.refreshRollupIfShowing();
    $q.notify({
      type: 'positive',
      timeout: 5000,
      message: `${team.name} is back, paused. Nothing runs until someone resumes it.`,
    });
  } catch (cause) {
    $q.notify({ type: 'negative', message: cause instanceof Error ? cause.message : String(cause) });
  } finally {
    unarchiveBusy.value = '';
  }
}

async function setPaused(team: Team | null, paused: boolean) {
  if (!team || pauseBusy.value) return;

  pauseBusy.value = team.id;

  try {
    if (paused) await pauseTeam(team.id);
    else await resumeTeam(team.id);

    await board.refresh();
    board.refreshRollupIfShowing();

    $q.notify({
      type: 'positive',
      timeout: 5000,
      message: paused
        ? `${team.name} is paused. New work will queue until someone resumes it.`
        : `${team.name} resumed. Queued work may now start.`,
    });
  } catch (cause) {
    $q.notify({
      type: 'negative',
      message: cause instanceof Error ? cause.message : String(cause),
    });
  } finally {
    pauseBusy.value = '';
  }
}
</script>

<template>
  <div>
    <!-- The board's own words, deliberately: a person who has no teams meets one explanation of
         how to get one, not two that differ slightly. -->
    <div v-if="board.teams.length === 0" class="column items-center q-pa-xl text-center">
      <div class="text-h6 q-mb-sm">No teams yet</div>
      <div class="text-body2 os-text-muted" style="max-width: 32rem">
        A team is a Manager and the members it takes on to do your work; to start one, choose
        <strong>New Team</strong> on the ribbon above or ask the Concierge.
      </div>
    </div>

    <template v-else>
    <!-- WHICH TEAMS ARE LISTED, remembered per browser. Archived ones are out of the way until asked for. -->
    <div class="row q-gutter-md q-mb-sm teams-shown">
      <q-checkbox
        dense
        label="Show active"
        :model-value="shown.active"
        data-teams-shown="active"
        @update:model-value="(value: boolean) => setShown('active', value)"
      />
      <q-checkbox
        dense
        label="Show archived"
        :model-value="shown.archived"
        data-teams-shown="archived"
        @update:model-value="(value: boolean) => setShown('archived', value)"
      />
    </div>

    <div v-if="!shown.active && !shown.archived" class="q-pa-lg text-center os-text-muted" data-teams-shown-none>
      Tick Show active or Show archived
    </div>

    <q-markup-table v-else v-resizable-columns="'teams'" flat bordered class="teams-table">
      <thead>
        <tr>
          <!-- `aria-sort` ON THE HEADER CELL, which is where the ARIA table pattern puts it -
               never on the button inside it. A screen reader announces the column's sort state as
               part of the heading; without it the arrow glyph is the only thing saying which way
               the table is ordered, and a glyph is exactly what a screen reader cannot read.
               `none` on the four columns that are not the sort, because the attribute is only
               meaningful when every sortable header carries it. -->
          <th
            v-for="heading in Headings"
            :key="heading.key"
            :data-col="heading.key"
            class="text-left"
            :aria-sort="ariaSort(sort, heading.key)"
          >
            <q-btn
              flat
              dense
              no-caps
              class="teams-sort"
              :aria-label="`Sort by ${heading.label}`"
              @click="sortBy(heading.key)"
            >
              {{ heading.label }}
              <!-- Material SYMBOLS names. The app loads Symbols Outlined only, and an Icons-only
                   name renders as its own letters at the wrong width with nothing logged. -->
              <q-icon
                v-if="sort.column === heading.key"
                :name="sort.descending ? 'arrow_downward' : 'arrow_upward'"
                size="14px"
                class="teams-sort-arrow"
              />
            </q-btn>
          </th>

          <!-- NOT SORTABLE: lanes over time have no single value to order by. -->
          <th data-col="activity" class="text-left">
            <div class="teams-activity-heading">
              <span>Activity</span>
              <q-toggle
                dense
                size="xs"
                data-activity-relative
                label="Relative"
                :model-value="relative"
                @update:model-value="setRelative"
              >
                <q-tooltip>Every chart on the same time span, so teams can be compared side by side</q-tooltip>
              </q-toggle>
            </div>
          </th>

          <!-- No label: the column carries live controls rather than sortable data. -->
          <th class="text-right" />
        </tr>
      </thead>

      <tbody>
        <!-- THE WHOLE ROW ACTIVATES THE TEAM. Keyboard too: a row that only answers a mouse is a
             navigation nobody without one can reach. -->
        <tr
          v-for="row in rows"
          :key="row.id"
          :data-team="row.id"
          class="teams-row"
          tabindex="0"
          @click="open(row.id)"
          @keydown.enter="open(row.id)"
          @keydown.space.prevent="open(row.id)"
        >
          <td>{{ row.name }}</td>

          <td>
            {{ row.members }}
            <div v-if="row.memberBreakdown" class="text-caption os-text-muted">{{ row.memberBreakdown }}</div>
          </td>

          <td v-if="teamFor(row)?.archived" data-col-status>
            <span class="console-tab-status console-tab-status--archived">ARCHIVED</span>
          </td>
          <td v-else data-col-status>
            <span class="console-tab-status" :class="`console-tab-status--${row.status}`">
              <span
                v-if="row.status === 'running'"
                class="container-running-dot"
                aria-hidden="true"
              ></span>
              {{ teamStatusText(row.status, row.held) }}
            </span>
            <div v-if="slotLine(row.id)" class="text-caption os-text-muted">{{ slotLine(row.id) }}</div>
          </td>

          <!-- THE TEAM'S ACTIVITY TILE, lanes only: the same chart its page shows, without the
               label and the caption. A click on it opens the Activity dialog for this team, not the
               team itself, so the row's own click is stopped here. -->
          <td class="teams-activity" @click.stop @keydown.enter.stop @keydown.space.stop>
            <div class="teams-activity-cell">
              <TeamStatisticsTile
                compact
                :team-id="row.id"
                :containers="teamFor(row)?.containers ?? []"
                :workflows="board.workflowsFor(row.id)"
                :range="sharedRange"
                @own-window="(range: ActivityRange | null) => noteWindow(row.id, range)"
              />
              <TeamStatusIcon :status="row.status" :held="row.held" :workflows="board.workflowsFor(row.id)" />
            </div>
          </td>

          <!-- `.stop` because these controls sit inside the whole-row click handler that switches
               teams - without it, acting on a team would also activate it on the way through. -->
          <td class="text-right" data-col-actions>
            <!-- AN ARCHIVED TEAM is brought back with Unarchive, and comes back paused: no Pause or Resume here. -->
            <q-btn
              v-if="teamFor(row)?.archived"
              flat
              dense
              no-caps
              icon="unarchive"
              label="Unarchive"
              :loading="unarchiveBusy === row.id"
              :disable="busy || cloneBusy"
              @click.stop="unarchive(teamFor(row))"
            >
              <q-tooltip>Bring this team back, paused</q-tooltip>
            </q-btn>
            <q-btn
              v-else
              flat
              dense
              no-caps
              :icon="teamFor(row)?.paused ? 'play_arrow' : 'pause'"
              :label="teamFor(row)?.paused ? 'Resume' : 'Pause'"
              :loading="pauseBusy === row.id"
              :disable="busy || cloneBusy"
              @click.stop="setPaused(teamFor(row), !(teamFor(row)?.paused === true))"
            >
              <q-tooltip>
                {{
                  teamFor(row)?.paused
                    ? 'Let this team start receiving new work again'
                    : 'Stop new work being delivered to this team'
                }}
              </q-tooltip>
            </q-btn>
            <q-btn
              flat
              dense
              round
              icon="content_copy"
              aria-label="Clone this team"
              :disable="busy || cloneBusy"
              @click.stop="askClone(teamFor(row))"
            >
              <q-tooltip>Clone team</q-tooltip>
            </q-btn>
            <q-btn
              v-if="!teamFor(row)?.archived"
              flat
              dense
              round
              icon="archive"
              aria-label="Archive this team"
              :disable="busy || cloneBusy || archiveBusy"
              @click.stop="askArchive(teamFor(row))"
            >
              <q-tooltip>Archive team</q-tooltip>
            </q-btn>
            <q-btn
              flat
              dense
              round
              icon="delete"
              color="negative"
              aria-label="Delete this team"
              :disable="busy"
              @click.stop="ask(teamFor(row))"
            >
              <q-tooltip>Delete team</q-tooltip>
            </q-btn>
          </td>
        </tr>
      </tbody>
    </q-markup-table>
    </template>

    <UnfinishedRemovals ref="removalsList" />

    <!-- `no-backdrop-dismiss`, NOT `persistent`: a stray click outside must not dismiss a question
         about destroying a team, but Escape must. `persistent` blocks both and only the click was
         ever meant; Escape cancels, which is the safe direction. This matters MORE now that there
         is nothing to type: the dialog is a read-and-press, so the only guard against an accidental
         press is that it cannot be dismissed by accident either. -->
    <q-dialog :model-value="doomed !== null" no-backdrop-dismiss @update:model-value="ask(null)">
      <q-card class="teams-confirm-card os-dialog-sm">
        <q-card-section class="os-dialog-title">Delete {{ doomed?.name }}?</q-card-section>

        <q-card-section class="q-pt-none">
          <p>This cannot be undone. It permanently removes:</p>
          <ul class="q-pl-md">
            <!-- THERE IS NO BULLET HERE ABOUT A CONCIERGE OR A LIVE TERMINAL, AND THAT ABSENCE
                 IS DELIBERATE: deletion removes neither. A Concierge is chosen for the INSTANCE, at Admin -> Concierge; a team row
                 only RECORDS the choice, and recording is not choosing. And a session is keyed on
                 `ConciergeSessionKey(user)` - the person alone - so no team-scoped operation can
                 reach one at all. The server agrees: `TeamDeleted` carries no session count,
                 since any count would be a structural zero written into a permanent audit row.

                 A destructive confirmation that warns about something it cannot destroy teaches
                 the reader a wrong model of what a Concierge is, on the one screen where they are
                 paying most attention.

                 `teams-view.mount.spec.ts` asserts the rendered card, so this comment is free
                 to name what it forbids. -->
            <li>every Agent Container on the team, and any running agent</li>
            <li>its documents, its members' working folders, and its transcripts</li>
            <li>every account's access to it</li>
          </ul>
          <!-- Off by default, and only for a local repository no other team uses. -->
          <div v-for="row in offeredLocalRepos" :key="row.reference" class="q-mb-sm" :data-delete-local-repo="row.reference">
            <q-checkbox
              v-model="deleteLocalRepos"
              :val="row.reference"
              dense
              :disable="busy"
              :label="`Also delete its local repository ${row.reference}`"
            />
            <div class="text-caption os-text-muted q-ml-lg" data-delete-local-repo-loses>
              Loses {{ losesLine(row.repo!) }}.
            </div>
          </div>
          <p v-for="row in sharedLocalRepos" :key="row.reference" class="os-body" :data-local-repo-shared="row.reference">
            <span class="mono">{{ row.reference }}</span> is kept: {{ row.others.join(', ') }}
            {{ row.others.length === 1 ? 'uses' : 'use' }} it.
          </p>
          <p v-if="keptLocalRepos.length > 0" class="os-body" data-local-repos-kept>
            {{ keptLocalRepos.length === 1 ? 'Its local repository' : 'Its local repositories' }}
            <span class="mono">{{ keptLocalRepos.join(', ') }}</span>
            {{ keptLocalRepos.length === 1 ? 'is' : 'are' }} kept, and can be deleted from
            Admin → Repositories.
          </p>
          <!-- A PACKAGE'S PLUGINS ARE THE INSTANCE'S, not the team's: deleting the team never
               removes one, and a person removes them in Admin -> Plugins. -->
          <p v-if="doomed?.solution?.plugins.length" class="os-body" data-solution-plugins-kept>
            <template v-if="doomed.solution.plugins.length === 1">
              {{ doomed.solution.name }} {{ doomed.solution.version }} installed the plugin
              <span class="mono">{{ doomed.solution.plugins[0] }}</span>. It stays installed after the
              team is deleted; remove it in Admin → Plugins.
            </template>
            <template v-else>
              {{ doomed.solution.name }} {{ doomed.solution.version }} installed the plugins
              <span class="mono">{{ doomed.solution.plugins.join(', ') }}</span>. They stay installed after
              the team is deleted; remove them in Admin → Plugins.
            </template>
          </p>
          <p class="os-body os-text-muted">
            The message log keeps its history — those messages are a record of what happened, and
            other teams' messages are linked to them.
          </p>
          <!-- THE INPUT LIVES INSIDE THE REFUSAL, not beside it. An unconditional box would ask the
               same question of a team with nothing to lose as of one holding unpushed commits, and
               teach people to type past it. This one only exists
               when the server has already refused and named what would go. -->
          <!-- NOT QUIET: the server's sentence naming what is still going, member by member. -->
          <p v-if="deleteRefusal" class="text-negative" data-delete-refusal>{{ deleteRefusal }}</p>
          <template v-if="deletionConfirmation">
            <p class="text-negative q-mb-sm">
              This team's repos still hold commits that are on no remote. Deleting it now would also
              discard:
            </p>
            <ul class="q-pl-md text-negative">
              <li v-for="loss in deleteLosses" :key="loss">{{ loss }}</li>
            </ul>

            <q-input
              v-model="typed"
              outlined
              dense
              autofocus
              :label="`Type ${deletionConfirmation} to confirm losing those commits`"
              :disable="busy"
              @keyup.enter="remove"
            />
          </template>
        </q-card-section>

        <q-card-actions align="right">
          <q-btn flat label="Cancel" :disable="busy" @click="ask(null)" />
          <q-btn
            color="negative"
            unelevated
            label="Delete team"
            :loading="busy"
            :disable="!confirmed || busy"
            @click="remove"
          />
        </q-card-actions>
      </q-card>
    </q-dialog>

    <!-- THE REMOVAL THAT DID NOT FINISH, said where the deletion was asked for, with the retry
         beside it. Every remaining path is listed: a count alone would leave somebody guessing
         which files are still on disk. -->
    <q-dialog :model-value="unfinished !== null" no-backdrop-dismiss @update:model-value="unfinished = null">
      <q-card class="teams-unfinished-card os-dialog-sm">
        <q-card-section class="os-dialog-title">{{ unfinished?.team }} was deleted, but its folder was not removed whole</q-card-section>

        <q-card-section class="q-pt-none">
          <p>
            These paths under <code>{{ unfinished?.root }}</code> could not be removed. The folder
            keeps its marker and is retried when the Host next starts, or now:
          </p>
          <ul class="q-pl-md teams-unfinished-paths">
            <li v-for="path in unfinished?.remaining ?? []" :key="path"><code>{{ path }}</code></li>
          </ul>
          <p v-if="unfinished?.note" class="text-negative">{{ unfinished.note }}</p>
        </q-card-section>

        <q-card-actions align="right">
          <q-btn flat label="Close" :disable="retrying" @click="unfinished = null" />
          <q-btn
            color="primary"
            unelevated
            label="Retry removal"
            :loading="retrying"
            @click="retryUnfinished"
          />
        </q-card-actions>
      </q-card>
    </q-dialog>

    <!-- ARCHIVE: what it does, the open workflows it leaves open, and the refusal as the server's sentence. -->
    <q-dialog :model-value="archiving !== null" no-backdrop-dismiss @update:model-value="askArchive(null)">
      <q-card class="teams-archive-card os-dialog-sm">
        <q-card-section class="os-dialog-title">Archive {{ archiving?.name }}?</q-card-section>

        <q-card-section class="q-pt-none">
          <p>
            Archiving keeps everything the team has - its members, settings, documents, repositories,
            sites and history - and puts it away: it does no work, takes no instructions, and leaves
            the tabs and the Kanban. It can still be cloned and deleted.
          </p>
          <p>Unarchive brings the team back paused: nothing runs until someone resumes it.</p>
          <template v-if="archiveFacts && archiveFacts.openWorkflows.length > 0">
            <p class="q-mb-xs">These workflows are still open and stay open:</p>
            <ul class="q-pl-md" data-archive-open-workflows>
              <li v-for="open in archiveFacts.openWorkflows" :key="open.workflow">
                #{{ open.workflow }} {{ open.title }}
              </li>
            </ul>
          </template>
          <p v-if="archiveRefusal" class="text-negative" data-archive-refusal>{{ archiveRefusal }}</p>
        </q-card-section>

        <q-card-actions align="right">
          <q-btn flat label="Cancel" :disable="archiveBusy" @click="askArchive(null)" />
          <q-btn
            color="primary"
            unelevated
            label="Archive team"
            :loading="archiveBusy"
            :disable="archiveBusy"
            @click="archive"
          />
        </q-card-actions>
      </q-card>
    </q-dialog>

    <!-- `no-backdrop-dismiss`, same as the delete confirmation: a stray click must not lose what
         was typed here, but Escape still cancels. -->
    <q-dialog :model-value="cloning !== null" no-backdrop-dismiss @update:model-value="askClone(null)">
      <q-card class="teams-clone-card os-dialog-sm">
        <q-card-section class="os-dialog-title">Clone {{ cloning?.name }}?</q-card-section>

        <q-card-section class="q-pt-none">
          <p>A new team, carrying:</p>
          <ul class="q-pl-md">
            <li>its Manager Agent and member-agent allowlist</li>
            <li>its repositories</li>
            <li>its environment variables, except the admin credentials</li>
            <li>its roster — the same Manager and members, with the same hired_for tags</li>
            <li>the same root, so the copy lands beside the original</li>
          </ul>
          <p class="text-body2">
            <strong>Not carried:</strong> <code>paused</code>, and the admin credentials — every
            clone mints those fresh for itself.
          </p>
          <!-- STATED OUTRIGHT: "Clone" invites the opposite assumption, and this route does not
               touch a single file the original team has written. -->
          <p class="text-body2">
            <strong>Files and history are not copied.</strong> Repositories are cloned fresh from
            their own remotes; nothing else on disk travels with the new team.
          </p>

          <q-input
            v-model="cloneName"
            outlined
            dense
            autofocus
            label="New team name"
            :rules="cloneNameRules"
            :maxlength="CloneNameMaximumLength"
            :disable="cloneBusy"
            hint="Prefilled as a suggestion — availability is decided when you submit."
            @update:model-value="cloneError = ''"
            @keyup.enter="clone"
          />

          <!-- Against the FIELD, not a toast: every refusal this route makes is fixed by typing
               something different, and a toast would float away from the box the fix belongs in. -->
          <div v-if="cloneError" class="text-negative q-mt-sm">{{ cloneError }}</div>
        </q-card-section>

        <q-card-actions align="right">
          <q-btn flat label="Cancel" :disable="cloneBusy" @click="askClone(null)" />
          <q-btn
            color="primary"
            unelevated
            label="Clone team"
            :loading="cloneBusy"
            :disable="!cloneNameIsLegal() || cloneBusy"
            @click="clone"
          />
        </q-card-actions>
      </q-card>
    </q-dialog>
  </div>
</template>

<style scoped>
/* The Activity heading: its word, then the Relative switch beside it. */
.teams-activity-heading {
  display: flex;
  align-items: center;
  gap: 12px;
  font-weight: inherit;
}

/* The Activity cell: the chart, then the team's status icon on its right. */
.teams-activity-cell {
  display: flex;
  align-items: center;
  gap: 6px;
}

.teams-activity-cell > :first-child {
  flex: 1 1 auto;
  min-width: 0;
}

/* 350px UNTIL A PERSON RESIZES THE COLUMN. Under the browser's automatic layout the chart's canvas
   is its own width, so the column only ever grew and pushed the actions off the page. Once a
   column is resized, `lib/resizableColumns.ts` marks the table `os-cols-fixed` at run time and the
   cell fills the width it was given. */
.teams-activity-cell {
  width: 350px;
}

:deep(.os-cols-fixed) .teams-activity-cell {
  width: auto;
}

.teams-table [data-col-actions] {
  white-space: nowrap;
}

.teams-table {
  background: var(--os-surface);
}

.teams-row {
  cursor: pointer;
}

.teams-row:hover,
.teams-row:focus-visible {
  background: var(--os-chrome);
}

/* The header cell is a button, so it must not read as one: a table heading that looks like a
   control competes with the rows underneath it for what a person clicks first. */
.teams-sort {
  padding: 0;
  font-weight: 600;
  color: var(--os-ink-muted);
}

.teams-sort-arrow {
  margin-left: 4px;
}

</style>
