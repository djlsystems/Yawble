<script setup lang="ts">
import { EndingFact } from '../lib/states';
import { computed, defineAsyncComponent, nextTick, onMounted, onUnmounted, ref, watch } from 'vue';
import { storeToRefs } from 'pinia';
import { useQuasar } from 'quasar';
import { useRouter } from 'vue-router';
import type { HubConnection } from '@microsoft/signalr';
import { useConsoleStore } from '../stores/console';
import { useSessionStore } from '../stores/session';
import { connectHub } from '../lib/hub';
import { fileWatch } from '../lib/monitor/workspace';
import { MaximumTeamNameLength, pauseTeam, renameTeam, resumeTeam, teamNameRefusal } from '../api/client';
import BoardGrid from '../components/BoardGrid.vue';
import BoardDisplayDialog from '../components/BoardDisplayDialog.vue';
import TeamKpiStrip from '../components/TeamKpiStrip.vue';
import KanbanBoard from '../components/KanbanBoard.vue';
const MonitorView = defineAsyncComponent(() => import('../components/MonitorView.vue'));
import TeamsView from '../components/TeamsView.vue';
import SolutionBlockedBanner from '../components/SolutionBlockedBanner.vue';
import TellManagerBox from '../components/TellManagerBox.vue';
import TeamSiteLinks from '../components/TeamSiteLinks.vue';
import { panelPath } from '../lib/solutionPanel';
import { stripScrollToShow } from '../lib/consoleStrip';
import { useKanbanStore } from '../stores/kanban';
import { useWorkflowNavStore } from '../stores/workflowNav';
import { TeamBar } from '../lib/ribbon';
import { useCapacityStore } from '../stores/capacity';
import { useNeedsYouStore } from '../stores/needsYou';
import { useAgentUpdatesStore } from '../stores/agentUpdates';
import { useConsoleHistory } from '../composables/useConsoleHistory';
import { useConsoleTitle } from '../composables/useConsoleTitle';
import type { Team, TeamId } from '../api/types';
import {
  StalledBadgeGrace,
  teamLogFacts,
  teamStatus,
  teamTabChip,
  type TeamStatus,
} from '../lib/teamKpis';

const $q = useQuasar();
const board = useConsoleStore();

/** The Board display dialog, opened from the board's own header as well as from the account menu. */
const boardDisplayOpen = ref(false);

/**
 * THE KANBAN TAB IS NOT A TEAM.
 *
 * It sits in the same strip as the team tabs and is selected the same way, but it is one board per
 * TENANT rather than a view of one team - so it lives in its own store, holds its own filters, and
 * replaces the container cards below rather than appearing beside them. Selecting it is a view
 * change and never a team switch: the team you were working in stays active, which is what makes
 * "back to the team" a single click and what the default team filter is taken from.
 */
const kanban = useKanbanStore();
const workflowNav = useWorkflowNavStore();

// Back and Forward move between the Teams table, a team's board and the Kanban.
useConsoleHistory();
// The tab names the view: Teams, Kanban or the team.
useConsoleTitle();
const capacity = useCapacityStore();
const needsYou = useNeedsYouStore();
const { hasTeams, error, signedOut, activeTeam, choosableTeams, openTeams, connected, connectionEstablished } =
  storeToRefs(board);

/** The team last opened, kept while the Teams list, the Kanban or the Monitor shows. */
const lastTeam = computed(() => openTeams.value[0] ?? null);

const session = useSessionStore();
const router = useRouter();

/**
 * A refusal from the API is not a board error, it is the end of the session - so the person goes
 * back to the front door, which knows how to offer them a way in. Watched rather than checked once
 * because the cookie can expire while the tab sits open, and a red "401 Unauthorized" banner above
 * an empty board would tell the person nothing.
 */
watch(signedOut, async (ended) => {
  if (!ended) return;

  session.user = null;
  await router.replace('/');
});

let connection: HubConnection | null = null;
const joinedTeams = new Set<string>();
let syncingHubTeams = false;
let syncHubTeamsQueued = false;

/**
 * The board feed follows the active tab. Changing teams updates the visible cards and their SignalR
 * subscriptions together, so the board never narrates one team while rendering another.
 */
const liveTeamIdsKey = computed(() => board.liveTeamIds.join('\u0000'));

function teamPaused(team: Team | null | undefined): boolean {
  return team?.paused === true;
}

function teamRunning(team: Team): boolean {
  return team.containers.some((container) => container.state === 'Running');
}

const pauseBusy = ref(false);

/**
 * RENAMING IN PLACE: the team's name on its board is a button; a click turns it into a box holding
 * the name. Enter or leaving the box saves it through `renameTeam`, the route Team settings' name
 * uses; Escape puts it back. A refusal stays under the box, the name still being edited. A label
 * only: the team's folder, workspaces and branches keep the name it was created with.
 */
const renaming = ref(false);
const nameDraft = ref('');
const renameError = ref('');
const renameBusy = ref(false);

function startRename() {
  if (!activeTeam.value) return;
  nameDraft.value = activeTeam.value.name;
  renameError.value = '';
  renaming.value = true;
}

function cancelRename() {
  renaming.value = false;
  renameError.value = '';
}

async function saveRename() {
  const team = activeTeam.value;
  if (!renaming.value || renameBusy.value || !team) return;

  if (nameDraft.value.trim() === team.name.trim()) {
    cancelRename();
    return;
  }

  const refused = teamNameRefusal(nameDraft.value);
  if (refused) {
    renameError.value = refused;
    return;
  }

  renameBusy.value = true;
  renameError.value = '';

  try {
    await renameTeam(team.id, nameDraft.value);
    await board.refresh();
    board.refreshRollupIfShowing();
    renaming.value = false;
  } catch (cause) {
    renameError.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    renameBusy.value = false;
  }
}

// Another team's board shown: whatever was being typed for this one is dropped.
watch(() => activeTeam.value?.id, () => cancelRename());

/**
 * WHAT PAUSE PROMISES, said where the control is.
 *
 * "No new work", never "quiet now". A member four minutes into a test suite keeps running; relief
 * arrives when that run ends. That is deliberate and it is the whole reason the feature is cheap:
 * nothing is killed, so nothing is lost. The emergency brake is the workflow Stop on the board,
 * which is a different button on purpose - a person choosing between them should be able to say
 * which they want in one sentence.
 */
function teamPauseHint(team: Team): string {
  return teamPaused(team)
    ? 'Start offering this team work again. Anything queued while it was paused delivers in order.'
    : 'Stop offering this team new work. A run already under way finishes; the next batch waits.';
}

/**
 * ONE CONTROL, AND `paused` IS THE ONLY THING THAT DECIDES ITS LABEL.
 *
 * NOT GATED ON THE TEAM'S STATUS, and that is a decision rather than an omission. Pause is a
 * statement about FUTURE work - the pump stops offering this team anything, and its containers stop
 * taking their next batch - so RUNNING, IDLE, BLOCKED and COMPLETED all have a future and all of
 * them can meaningfully be paused. Pausing an IDLE team is the most useful case of the lot: nothing
 * has started, so the pause takes effect on the very next thing that would have woken it - a
 * trigger, a schedule, a Manager being told.
 *
 * BLOCKED IS NOT A TEAM-WIDE STOP either. It is a mark on one container that gave up; other members
 * may still be working and new work still delivers. Disabling the brake there would take it away at
 * exactly the moment somebody is clearing up.
 */
async function togglePaused(team: Team) {
  pauseBusy.value = true;

  try {
    if (teamPaused(team)) await resumeTeam(team.id);
    else await pauseTeam(team.id);

    // THE SAME TWO REFRESHES `TeamsView` MAKES, and both are needed: `refresh()` reloads the
    // overview this heading reads its `paused` from, and the rollup behind the Teams tab is a
    // separate fetch that would otherwise sit on a stale Status column.
    await board.refresh();
    board.refreshRollupIfShowing();
  } catch (cause) {
    $q.notify({
      type: 'negative',
      message: cause instanceof Error ? cause.message : String(cause),
    });
  } finally {
    pauseBusy.value = false;
  }
}

/**
 * THE BOARD'S CLOCK, and it is a clock rather than a poll.
 *
 * `UNDECLARED` has a grace period, which makes the chip a function of TIME as well as of
 * state — and a team that has stopped publishes nothing by definition, so no SignalR push and no
 * fetch will ever arrive to re-evaluate it. Without a tick the badge would simply never appear.
 *
 * IT FETCHES NOTHING AND WAKES NOBODY. That distinction is worth being explicit about, because
 * the store above says in so many words that there is no timer in it and there must not be: what
 * is refused is an automatic wake, which is an automatic SPEND — a manager re-deriving its whole
 * context from the ledger with no person in the loop. This re-reads two refs the page already
 * holds and re-renders a word. It is not a poll: a fifteen-second local read with no network in
 * it at all.
 *
 * The interval is a fraction of {@link StalledBadgeGrace} so the mark is never more than that
 * fraction late, and it is cleared on unmount beside the hub connection.
 */
const BadgeClockTick = StalledBadgeGrace / 8;
const now = ref(Date.now());
let badgeClock: ReturnType<typeof setInterval> | null = null;

/**
 * The chip on a team's tab.
 *
 * The roster, the team summary and the log all answer part of this. `paused` comes from the team
 * summary, `failed` and `undeclared` need the log, and the rest still come from the live snapshots.
 * `UNDECLARED` is the reason this reads history at all: every clause of it but "nothing is
 * Running" and "every queue is empty" is history, and a `ContainerSnapshot` holds none of it.
 *
 * `now` is READ HERE rather than inside the library, which is what makes the whole derivation a
 * pure function of stated inputs and testable at an exact instant. It is also what makes this
 * computed re-run as the clock moves.
 */
function tabStatus(team: Team): TeamStatus {
  return teamStatus(
    team.containers,
    teamLogFacts(board.teamActivity(team.id)),
    now.value,
    teamPaused(team),
  );
}

/** The chip on a team's tab: `teamTabChip`, which the command palette's team rows read too. */
function tabChip(team: Team): { text: string; token: string } {
  return teamTabChip(tabStatus(team), board.workflowsFor(team.id), team.containers, now.value);
}


async function syncHubTeams() {
  if (!connection) return;
  if (syncingHubTeams) {
    syncHubTeamsQueued = true;
    return;
  }

  syncingHubTeams = true;

  try {
    do {
      syncHubTeamsQueued = false;
      const wanted = new Set(board.liveTeamIds);

      for (const joined of [...joinedTeams]) {
        if (wanted.has(joined)) continue;
        await board.leaveHubTeam(connection, joined);
        joinedTeams.delete(joined);
      }

      for (const team of wanted) {
        if (joinedTeams.has(team)) continue;
        if (await board.switchHubTeam(connection, team)) {
          joinedTeams.add(team);
        }
      }
    } while (syncHubTeamsQueued);
  } finally {
    syncingHubTeams = false;
  }
}

/**
 * A team tab: switch the team AND leave the board.
 *
 * Both, in one place, because they are one act - clicking a team means "show me this team", and a
 * switch that left the Kanban board on screen would look like the click did nothing while quietly
 * repointing everything underneath it.
 *
 * IT DOES NOT TOUCH THE KANBAN STORE AT ALL. `showBoard()` moves the VIEW off the board, which is
 * this store's own state; the board's filters and its cards are none of a team switch's business.
 * A call from "a team was activated" into the kanban store is the shape that grows a refetch,
 * or a re-filter, the next time somebody wants the board to follow the tab strip. It must not: a
 * board narrowed to Beta stays narrowed to Beta when you go and look at Alpha.
 */
/**
 * THE ACTIVE TAB STAYS IN VIEW on a strip that scrolls sideways (a phone): whichever tab or view
 * became active, from the strip, a link, the palette or a reload, the strip scrolls just far enough
 * to show it whole. A strip that fits does not scroll, so this moves nothing on a wide screen.
 */
const strip = ref<HTMLElement | null>(null);

watch(
  () => [board.view, kanban.active, activeTeam.value?.id ?? ''].join('|'),
  async () => {
    await nextTick();
    const el = strip.value;
    const tab = el?.querySelector<HTMLElement>('.console-tab.active');
    if (!el || !tab) return;

    const left = tab.getBoundingClientRect().left - el.getBoundingClientRect().left + el.scrollLeft;
    el.scrollLeft = stripScrollToShow(el.scrollLeft, el.clientWidth, left, tab.getBoundingClientRect().width);
  },
  { immediate: true },
);

function selectTeamTab(teamId: TeamId) {
  board.showBoard();
  board.setActiveTeam(teamId);
}


onMounted(async () => {
  // THE RELOAD CASE FOR THE BOARD, and the board's half of the one the Teams table has below.
  // The view is persisted, so a reload lands straight back on the Kanban tab - and every fetch in
  // that store is a reaction to a filter changing, so nothing asked the server for a board and the
  // empty state read as "there is no work". `connectHub` delivers CHANGES and never current state,
  // so listening alone left it blank until something happened to move. A no-op for the other two
  // views, and clicking the tab fetches through `showKanban` instead.
  //
  // AFTER THE SAMPLE FLAG, because the flag decides whether this goes to the network at all, and
  // BEFORE `board.refresh()`, because the board fetch needs nothing from the overview - waiting on
  // it would only delay the one view the person is already looking at.
  kanban.loadIfShowing();

  // The initial fetch STAYS, and must. SignalR delivers changes, never current
  // state, so a client that only listened would sit blank until something
  // happened to move.
  await board.refresh();

  // THE STEERING, ONCE PER START: what this page highlights, `null` when nothing, so a workflow
  // the Host still holds from another day is cleared by the first page a person opens.
  board.announceSteering();

  // THE RELOAD CASE. The view is persisted, so a reload can land straight on the Teams table -
  // and `refresh()` fetches the overview, not the rollup. Without this the table comes back with
  // every team's Last workflow cell reading "not loaded yet" until some unrelated container
  // happens to push. A no-op for the other two views, and clicking the tab fetches through
  // `showTeamsView` instead.
  board.refreshRollupIfShowing();

  connection = await connectHub(
    {
      onContainerChanged: (snapshot) => {
        board.applySnapshot(snapshot);

        // A snapshot says a container moved but carries no messages, so the feed
        // would freeze while the cards updated. This is a bounded fetch after the
        // cursor, triggered by a change - not a poll.
        void board.pullMessages();

        // AND THE BOARD, because container activity IS kanban activity: a member starting,
        // reporting progress or giving up is a card changing colour and lane. This is what makes
        // the board live before the Host raises `kanbanChanged` at all - the minimum the design
        // brief asks for. It is a no-op while the Kanban tab is not the one being shown.
        kanban.refreshIfActive();

        // AND THE TEAMS TABLE, for the identical reason and by the identical rule: its Status and
        // Last workflow columns are a projection of the log, which a snapshot does not carry. This
        // is the table's ONLY refresh after it is opened - without it the rollup is whatever was
        // fetched when the tab was clicked, and the table quietly ages while the board beside it
        // stays live. A no-op while the table is not the view being shown.
        board.refreshRollupIfShowing();

        // AND NEEDS YOU'S LIVE PART: a mark set or cleared is a row joining or leaving it.
        void needsYou.loadLive();

        // A HELD LAUNCH may be held behind an Agent CLI update: the list that says so is read now,
        // while the board shows it, rather than on a clock.
        if (snapshot.held) useAgentUpdatesStore().onHeld();
      },

      // The projection's own push, once the Host raises it: a human edit, or anything else that
      // moved a card without moving a container. Refetch rather than patch - the board is a
      // derived read model and the server owns the derivation.
      onKanbanChanged: () => {
        kanban.refreshIfActive();
        void needsYou.loadLive();
      },

      // The activity monitor's figures: each sample appended to the one copy the app bar reads.
      onCapacityChanged: (sample) => capacity.apply(sample),

      // A member's file changes, heard while the Monitor has joined the file watch.
      onWorkspaceChanged: (push) => fileWatch.deliver(push),

      // A member's agent steps, heard on the same watch.
      onAgentSteps: (push) => fileWatch.deliverSteps(push),

      onTeamChanged: () => {
        void board.refresh();
        board.refreshRollupIfShowing();
      },

      onTeamCreated: (change) => void board.refreshForTeamCreated(change.team),

      onTeamDeleted: (change) => void board.refreshForTeamDeleted(change.team),

      onReconnected: async () => {
        joinedTeams.clear();
        await board.refresh();

        // The Host may have restarted or dropped the workflow while the connection was down.
        board.announceSteering();

        // Samples pushed while the connection was down are missed; the route's history fills them.
        void capacity.load();

        // THE SAME REASON THE RELOAD CASE ABOVE HAS IT. `refresh()` fetches the overview, and the
        // rollup is a separate read - so a drop and reconnect while the table is showing left the
        // Last workflow column frozen at whatever was fetched before the connection went, until
        // some unrelated container happened to push. A reconnect is exactly the moment that column
        // is most likely to be stale, and the pushes that would have repaired it are the ones that
        // were missed.
        board.refreshRollupIfShowing();

        await syncHubTeams();

        // The file watch lived on the old connection id too.
        fileWatch.rejoin();
      },
      onConnectedChanged: (value) => board.setConnected(value),
      onJoinFailed: () => board.reportJoinFailure(board.activeTeamId || 'this team'),
    },
    () => board.activeTeamId,
  );

  // The Monitor may already be open and waiting to hear file changes.
  fileWatch.attach(connection);
  await syncHubTeams();
});

/**
 * Re-joins on every team switch, whatever triggered it - the ribbon's switcher, a team just
 * created, `reconcileActiveTeam` falling back after the active team disappeared. All of them go
 * through `setActiveTeam`, so watching the one field it writes covers every caller instead of
 * requiring each of them to remember a hub call too.
 *
 * Guarded on `connection`: this can fire before `onMounted` finishes connecting (the initial
 * `board.refresh()` above already picks a team), and the connect call above does its own first
 * join once the connection exists - this watcher only needs to handle CHANGES after that. The
 * leave/join themselves, and what a failed join means, live in `board.switchHubTeam` - see there.
 */
watch(liveTeamIdsKey, () => void syncHubTeams());

onMounted(() => {
  badgeClock = setInterval(() => (now.value = Date.now()), BadgeClockTick);
});

onUnmounted(() => {
  if (badgeClock !== null) clearInterval(badgeClock);
  badgeClock = null;

  fileWatch.detach();
  void connection?.stop();
  connection = null;
  joinedTeams.clear();
});
</script>

<template>
  <q-page padding>
    <q-banner
      v-if="connectionEstablished && !connected"
      dense
      rounded
      class="bg-warning text-dark q-mb-md"
    >
      Live updates are disconnected. Reconnecting automatically; the cards below may be stale.
    </q-banner>

    <q-banner v-if="error" dense rounded class="bg-negative text-white q-mb-md">
      {{ error }}
    </q-banner>

    <!-- UNCONDITIONAL. The Teams tab below is permanent, so the strip always holds at least one
         thing and there is always a way back to a team.

         THE STRIP SHOWS SCOPE. Left, the Teams button: the list, or a team's board from its menu.
         Right, at the strip's end, the views of every team at once - Kanban and Monitor - as one
         segmented control in one style. The active element in either group takes the primary
         underline the ribbon's active team title uses. On a phone the strip scrolls sideways and
         keeps the active tab in view. -->
    <div ref="strip" class="console-tabs q-mb-md">
      <div class="console-places" role="group" aria-label="Places" data-test="console-places">
        <!-- THE TEAMS BUTTON. First and permanent, and a split button: the word opens the Teams
             list, which leaves NO ACTIVE TEAM; the arrow lists every active team with its status
             chip, and picking one opens its board. There is no tab per team: one button stays one
             button however many teams there are. -->
        <q-btn-dropdown
          split
          unelevated
          no-caps
          class="console-tab console-tab--teams"
          :class="{ active: board.view === 'teams' }"
          content-class="above-concierge"
          icon="groups"
          :aria-pressed="board.view === 'teams' ? 'true' : 'false'"
          dropdown-icon="arrow_drop_down"
          :disable-dropdown="choosableTeams.length === 0"
          data-test="teams-button"
          @click="board.showTeamsView()"
        >
          <template #label>
            <span class="console-tab-label">Teams</span>
          </template>

          <q-list class="teams-menu" data-test="teams-menu">
            <q-item
              v-for="team in choosableTeams"
              :key="team.id"
              v-close-popup
              clickable
              :active="board.view === 'board' && team.id === activeTeam?.id"
              active-class="text-primary"
              data-test="teams-menu-team"
              @click="selectTeamTab(team.id)"
            >
              <q-item-section>{{ team.name }}</q-item-section>
              <q-item-section side class="row no-wrap items-center">
                <span v-if="teamPaused(team)" class="console-tab-status console-tab-status--paused team-pause-chip">
                  PAUSED
                </span>
                <span class="console-tab-status" :class="`console-tab-status--${tabChip(team).token}`">
                  <span
                    v-if="teamRunning(team)"
                    class="container-running-dot"
                    aria-hidden="true"
                  ></span>
                  {{ tabChip(team).text }}
                  <q-tooltip v-if="tabChip(team).text === 'COMPLETED'">{{ EndingFact.workflow }}</q-tooltip>
                </span>
              </q-item-section>
            </q-item>
          </q-list>
        </q-btn-dropdown>

        <!-- THE LAST TEAM: one button for the team last opened, so its board is one click away from
             the Teams list, the Kanban or the Monitor. It is the open team (`openTeams`), which
             outlasts a visit to the Teams list; picking another team in the menu replaces it. -->
        <q-btn
          v-if="lastTeam"
          unelevated
          no-caps
          class="console-tab console-tab--last-team"
          :class="{ active: board.view === 'board' && !kanban.active && lastTeam.id === activeTeam?.id }"
          data-test="last-team"
          @click="selectTeamTab(lastTeam.id)"
        >
          <span class="console-tab-label">{{ lastTeam.name }}</span>
          <span v-if="teamPaused(lastTeam)" class="console-tab-status console-tab-status--paused team-pause-chip">
            PAUSED
          </span>
          <span class="console-tab-status" :class="`console-tab-status--${tabChip(lastTeam).token}`">
            <span
              v-if="teamRunning(lastTeam)"
              class="container-running-dot"
              aria-hidden="true"
            ></span>
            {{ tabChip(lastTeam).text }}
            <q-tooltip v-if="tabChip(lastTeam).text === 'COMPLETED'">{{ EndingFact.workflow }}</q-tooltip>
          </span>
        </q-btn>
      </div>

      <div class="console-views" role="group" aria-label="Views of every team" data-test="console-views">
        <!-- THE KANBAN TAB. In the strip, and NOT a team: it is one board per tenant, it has no team
             status chip and it cannot be closed. It is the one door to the Kanban: it opens it
             filtered to the active team (the ribbon's Kanban button, which did the same, is gone). -->
        <q-btn
          unelevated
          no-caps
          class="console-tab console-view console-tab--kanban"
          :class="{ active: kanban.active }"
          icon="view_kanban"
          :aria-pressed="kanban.active ? 'true' : 'false'"
          @click="kanban.showKanban(board.activeWorkTeamId)"
        >
          <span class="console-tab-label">Kanban</span>
        </q-btn>

        <!-- THE MONITOR TAB, beside the Kanban and of the same kind: one view of every team, not a
             team. It shows the work moving - who is told what, who runs, what finishes - as a live
             graph, and can play the newest stretch of the log back at speed. -->
        <q-btn
          unelevated
          no-caps
          class="console-tab console-view console-tab--monitor"
          :class="{ active: board.view === 'monitor' }"
          icon="hub"
          :aria-pressed="board.view === 'monitor' ? 'true' : 'false'"
          data-monitor-tab
          @click="board.showMonitor()"
        >
          <span class="console-tab-label">Monitor</span>
        </q-btn>
      </div>
    </div>

    <!-- THE FIRST BRANCH, because the Teams table is the one view that is reachable with no active
         team at all: every branch below it reads `activeTeam`, and one of them renders the empty
         state that the table renders for itself. -->
    <TeamsView v-if="board.view === 'teams'" />

    <!-- The line is load-bearing rather than decorative: it explains why there is
         no separate "add a manager" step anywhere in the UI. The way to make one is
         New Team on the ribbon - there is no second door here that does the same job. -->
    <!-- The Kanban tab REPLACES the container cards rather than sitting above them. Two boards on
         one page is two things claiming to be what the team is doing. -->
    <KanbanBoard v-else-if="kanban.active" />

    <!-- The Monitor replaces the cards as the Kanban does: every team at once. -->
    <MonitorView v-else-if="board.view === 'monitor'" />

    <div v-else-if="!hasTeams" class="column items-center q-pa-xl text-center">
      <div class="text-h6 q-mb-sm">No teams yet</div>
      <div class="text-body2 os-text-muted" style="max-width: 32rem">
        A team is a Manager and the members it takes on to do your work; to start one, choose
        <strong>New Team</strong> on the ribbon above or ask the Concierge.
      </div>
    </div>

    <!-- ONE team at a time: the active team, chosen in the ribbon. Stacking every team would make
         the ribbon's team switcher a control with nothing to change. -->
    <div v-else-if="activeTeam" class="q-mb-lg">
      <div class="row items-center q-mb-sm q-gutter-sm">
        <!-- The LABEL. The shell-level Concierge is launched from this team's `.id`, so the heading binds
             to the same identifier rather than to a separately-derived name. -->
        <q-input
          v-if="renaming"
          v-model="nameDraft"
          dense
          outlined
          autofocus
          class="team-name-edit"
          aria-label="Team name"
          data-test="team-name-input"
          :maxlength="MaximumTeamNameLength"
          :loading="renameBusy"
          :error="!!renameError"
          :error-message="renameError"
          @keydown.enter.prevent="saveRename"
          @keydown.esc.prevent.stop="cancelRename"
          @blur="saveRename"
        />
        <button
          v-else
          type="button"
          class="text-h6 team-name-button"
          data-test="team-name"
          :aria-label="`${activeTeam.name}: rename this team`"
          @click="startRename"
        >
          {{ activeTeam.name }}
          <q-icon name="edit" size="16px" class="team-name-button-icon" aria-hidden="true" />
          <q-tooltip>Rename this team</q-tooltip>
        </button>
        <!-- The team's published sites, a globe each: its site cards leave the board once done. -->
        <TeamSiteLinks :team="activeTeam.id" />
        <span v-if="teamPaused(activeTeam)" class="console-tab-status console-tab-status--paused team-heading-pause">
          PAUSED
        </span>


        <q-space />

        <!-- A TEAM INSTALLED FROM A SOLUTION PACKAGE is managed from its control panel: an address,
             so it is the same screen the launcher's Manage opens. A plain hash link, as the install
             notice's is (`installHref`). -->
        <q-btn
          v-if="activeTeam.solution"
          flat
          dense
          no-caps
          size="sm"
          icon="tune"
          label="Manage solution"
          :href="`#${panelPath(activeTeam.id)}`"
          data-manage-solution
        >
          <q-tooltip>{{ activeTeam.solution.name }} {{ activeTeam.solution.version }}: status, controls, results and maintenance</q-tooltip>
        </q-btn>

        <!-- THE BRAKE, WHERE THE WORK IS. It lived only on the Teams rollup and in team settings,
             and the first thing anybody did when they wanted to stop a running team was look at the
             running team. This is that screen.

             ONE CONTROL, TWO LABELS, AND `paused` IS THE ONLY THING THAT DECIDES which. It is NOT
             gated on RUNNING/IDLE/BLOCKED/COMPLETED, deliberately: pause is a statement about
             FUTURE work - it stops the pump offering this team anything and stops its containers
             taking their next batch - and every one of those states has a future. Pausing an idle
             team is the most useful case of all, because it is the one where nothing has started
             yet. See `teamPauseLabel`. -->
        <q-btn
          v-if="activeTeam"
          outline
          dense
          no-caps
          size="sm"
          class="team-heading-pause-btn"
          :icon="teamPaused(activeTeam) ? 'play_arrow' : 'pause'"
          :label="teamPaused(activeTeam) ? 'Resume' : 'Pause'"
          :loading="pauseBusy"
          @click="togglePaused(activeTeam)"
        >
          <q-tooltip>{{ teamPauseHint(activeTeam) }}</q-tooltip>
        </q-btn>

        <!-- THE CARDS' SIZE, found from the board it sizes: the exact numbers and the feed settings.
             The cards' own edges drag the size too; the account menu keeps its entry. -->
        <q-btn
          flat
          dense
          round
          size="sm"
          icon="tune"
          aria-label="Board display"
          data-board-display
          @click="boardDisplayOpen = true"
        >
          <q-tooltip>Board display</q-tooltip>
        </q-btn>
      </div>


      <!-- THE TEAM BAR: the commands on this team, under its name. The ribbon holds what is the
           instance's; what is one team's is here, on its board (`TeamBar`). -->
      <div class="team-bar row items-center no-wrap q-mb-sm" role="toolbar" :aria-label="`${activeTeam.name} commands`" data-test="team-bar">
        <q-btn
          v-for="command in TeamBar"
          :key="command.action"
          flat
          dense
          no-caps
          size="sm"
          class="team-bar-btn"
          :icon="command.icon"
          :label="command.label"
          :data-team-command="command.action"
          @click="workflowNav.runTeamCommand(activeTeam.id, command.action)"
        />
      </div>

      <!-- HOW WORK REACHES A TEAM, where people look for it: a person who opened a team to give it
           work found no input. The box tells the Manager; its hint names the Concierge as another way in. -->
      <TellManagerBox :team="activeTeam.id" :workflows="board.workflowsFor(activeTeam.id)?.workflows ?? []" />

      <!-- A team installed from a solution package that still waits for its person. -->
      <SolutionBlockedBanner :team="activeTeam.id" />

      <!-- Glance figures for the team being watched. Fed from the same snapshots the cards
           already hold, so a SignalR push updates the strip without a reload. -->
      <TeamKpiStrip
        :containers="activeTeam.containers"
        :usage="board.tokenUsageTeam === activeTeam.id ? board.tokenUsage : null"
        :timing="board.workflowTeam === activeTeam.id ? board.workflow : null"
        :workflows="board.workflowsFor(activeTeam.id)"
        :clock-offset="board.workflowClockOffset"
        :team-id="activeTeam.id"
        :status="tabStatus(activeTeam)"
        :findings="board.findingKinds(activeTeam.id)"
        :repo-status="board.repoStatusTeam === activeTeam.id ? board.repoStatus : null"
      />

      <!-- The member cards: fixed-size windows that wrap, every one the size this viewer chose. -->
      <BoardGrid :containers="activeTeam.containers" />
    </div>

    <BoardDisplayDialog v-model="boardDisplayOpen" />
  </q-page>
</template>

<style scoped>
/* The team bar: one row of this team's commands under its name, sideways-scrolling on a phone. */
.team-bar {
  gap: 4px;
  overflow-x: auto;
  padding-bottom: 4px;
  border-bottom: 1px solid var(--os-rule);
}

.team-bar-btn {
  flex: none;
}

.console-tabs {
  display: flex;
  align-items: center;
  gap: 8px;
}

/* The places wrap onto a second line on a wide screen with many teams open; the views stay at the
   strip's end. */
.console-places {
  flex: 1 1 auto;
  min-width: 0;
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}

.console-views {
  flex: none;
  margin-left: auto;
  display: inline-flex;
  align-items: center;
}

.console-tab {
  border: 1px solid var(--os-rule);
  background: var(--os-surface);
  color: var(--os-ink-muted);
  padding: 0 10px;
  min-height: 34px;
}

.console-tab.pinned {
  font-weight: 600;
}

/* ONE SEGMENTED CONTROL: the two views share their borders and only the outer corners are round,
   so they read as one choice of how to look at every team, not as two more places. */
.console-view {
  border-radius: 0;
}

.console-view + .console-view {
  margin-left: -1px;
}

.console-view:first-child {
  border-radius: 4px 0 0 4px;
}

.console-view:last-child {
  border-radius: 0 4px 4px 0;
}

/* Set apart from the team tabs with a rule rather than a colour: it is a different KIND of tab,
   not a tab in a different state, and colouring it would compete with the status words beside it. */
.console-tab--teams {
  margin-right: 4px;
  border-right-width: 3px;
  border-right-color: var(--os-rule-strong);
}

/* THE ACTIVE ELEMENT, in either group: the primary underline the ribbon's active team title draws
   (2px, `--os-primary`), inside the tab so the strip does not move when it changes. */
.console-tab.active {
  color: var(--os-ink);
  box-shadow: inset 0 -2px 0 var(--os-primary, var(--q-primary));
}

/* A PHONE: one line that scrolls sideways, the active tab kept in view by the page's script. */
@media (max-width: 599.98px) {
  .console-tabs {
    overflow-x: auto;
    scrollbar-width: thin;
  }

  .console-places {
    flex: none;
    flex-wrap: nowrap;
  }
}

.console-tab-label {
  max-width: 14rem;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.console-tab-close {
  margin-left: 8px;
  color: var(--os-ink-faint);
}

.console-tab-close:hover {
  color: var(--os-ink);
}

/* THE CHIP ITSELF - its typography and its nine colours - IS GLOBAL, in `css/app.scss`, because
   the Teams table renders the same chip and a scoped copy would either leave that one uncoloured
   or become a second palette to keep in step. What stays here is the spacing, which belongs to
   the tab the chip sits in and to nothing else.

   NINE COLOURS ACROSS TWELVE SELECTORS - four of them share the faint grey (`idle`,
   `completed`, `closed`, `none`), which is why counting the selectors gives a different
   answer. The count said seven until the workflow vocabulary grew past the roster's own; both
   numbers are here so the next reader can tell which one has drifted. */
.console-tab-status {
  margin-left: 8px;
}

.team-pause-chip {
  margin-left: 8px;
}

/* A SMALL OUTLINE BUTTON, NOT A PRIMARY ONE. The heading's job is to say which team you are
   watching; the brake belongs there but must not compete with the name for attention. */
.team-name-button {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 0 4px;
  margin: 0 -4px;
  border: 1px solid transparent;
  border-radius: 4px;
  background: none;
  color: inherit;
  font: inherit;
  cursor: text;
}

.team-name-button:hover,
.team-name-button:focus-visible {
  border-color: var(--os-ink-faint);
}

.team-name-button-icon {
  opacity: 0;
  transition: opacity 0.15s;
}

.team-name-button:hover .team-name-button-icon,
.team-name-button:focus-visible .team-name-button-icon {
  opacity: 0.6;
}

.team-name-edit {
  min-width: 18rem;
  font-size: 1.25rem;
}

.team-heading-pause-btn {
  flex: 0 0 auto;
}

.team-heading-pause {
  margin-left: 4px;
}

</style>
