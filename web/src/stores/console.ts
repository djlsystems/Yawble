import { defineStore, acceptHMRUpdate } from 'pinia';
import type { HubConnection } from '@microsoft/signalr';
import {
  getMessages,
  getOverview,
  getTeamTokens,
  getTeamWorkflow,
  getTeamWorkflows,
  getTeamsRollup,
  getRepoStatus,
  setCurrentTeam,
  publishSteering,
  Unauthorized,
} from '../api/client';
import { asMemberId, asTeamId } from '../api/types';
import { joinTeam, leaveTeam } from '../lib/hub';
import { ownerOf } from '../lib/summarise';
import { DefaultBoardSize } from '../lib/boardSize';
import { useDisplayStore } from './display';
import type {
  ActiveFinding,
  ContainerSnapshot,
  Message,
  Team,
  TeamId,
  TeamTokenTotals,
  TeamWorkflowTiming,
  TeamWorkflows,
  TeamRepoStatus,
} from '../api/types';

/**
 * How many activity lines a card keeps, when this viewer has expressed no preference.
 *
 * One hundred, because a member calling `harness progress` steadily burns twenty lines in a couple
 * of minutes, and a smaller window closes faster than a person can read it. It is a viewer
 * preference - see `lib/boardSize` - and this constant is only the value a fresh browser starts at.
 *
 * A feed is still a glance rather than an audit. The log keeps everything; `harness status`
 * answers up to 100 rows for one member; the card shows the tail.
 */
const FeedDepth = DefaultBoardSize.feedDepth;

/** Survives a reload: ordered tabs plus which one is active. Wrapped because storage throws in a
 *  private window with cookies blocked, and a toolbar preference is not worth a crash. */
const ActiveTeamKey = 'harness.activeTeam';

/**
 * WHICH OF THE THREE THINGS the console page is showing.
 *
 * IT LIVES HERE rather than in the kanban store, where it began, because this store already owns
 * what this window is looking at - `activeTeamId`, `openTeamTabs` - and it already persists that.
 * The kanban store imports this one; this one imports nothing of kanban's, and `'kanban'` here is a
 * string rather than a dependency.
 *
 * `'board'` WAS CALLED `'teams'`, and the rename is not tidiness: with a Teams view in the same
 * union, a value named `teams` that means "one team's board" says the opposite of its contents -
 * which is the `activeTeamId` / `activeTeamName` trap this codebase has a branded type to prevent.
 */
export type ConsoleView = 'board' | 'kanban' | 'teams';

/**
 * This viewer's chosen feed depth, or the default when the display store is not up yet.
 *
 * A function rather than a captured value, because `record()` must see a change made in the Board
 * display dialog without a reload. Guarded because a Pinia store that has not been created yet
 * throws, and the ordering between two stores waking is not a thing this one should depend on.
 */
function windowNow(): number | undefined {
  try {
    return useDisplayStore().feedWindow;
  } catch {
    // The display store is not up yet. Undefined omits the parameter entirely and the server
    // applies its own default, which is the same number - so a frame where the two stores race
    // fetches exactly what it always did.
    return undefined;
  }
}

function depthNow(): number {
  try {
    return useDisplayStore().feedDepth;
  } catch {
    return FeedDepth;
  }
}

interface RememberedTabs {
  open: TeamId[];
  active: TeamId | '';

  /**
   * PERSISTED, and it has to be. `reconcileActiveTeam` forces an active team whenever none is set,
   * so a reload from the Teams view would land on a team AND PUT it to the server, moving the
   * Concierge with it - silently undoing the one state this view exists to make reachable.
   */
  view: ConsoleView;
}

/** Reads one stored string and normalises both stored shapes. */
function readRememberedTabs(): RememberedTabs {
  try {
    const stored = localStorage.getItem(ActiveTeamKey);
    if (!stored) return { open: [], active: '', view: 'board' };

    try {
      const parsed = JSON.parse(stored) as { open?: unknown; active?: unknown; view?: unknown };
      const open = Array.isArray(parsed.open)
        ? parsed.open.filter((id): id is string => typeof id === 'string').map(asTeamId)
        : [];
      const active = typeof parsed.active === 'string' ? asTeamId(parsed.active) : '';
      const view: ConsoleView =
        parsed.view === 'kanban' || parsed.view === 'teams' || parsed.view === 'board'
          ? parsed.view
          // An older blob, or a value from a build that spelled these differently. The board is the
          // safe landing: it is what every reader saw before this field existed.
          : 'board';

      return { open, active, view };
    } catch {
      const active = asTeamId(stored);
      return { open: [active], active, view: 'board' };
    }
  } catch {
    return { open: [], active: '', view: 'board' };
  }
}

function writeRememberedTabs(tabs: RememberedTabs) {
  try {
    localStorage.setItem(ActiveTeamKey, JSON.stringify(tabs));
  } catch {
    // Nothing to do and nothing worth saying: the selection still works for this session.
  }
}

/**
 * The board's state.
 *
 * Fed by exactly two things: one `/api/overview` fetch when the page loads, and
 * `containerChanged` pushes over SignalR thereafter. There is no timer anywhere
 * in this file, and there must not be — the board is push-driven, not polled.
 */
export const useConsoleStore = defineStore('console', {
  state: () => {
    const remembered = readRememberedTabs();

    return {
      teams: [] as Team[],

      /**
       * `/api/overview` HAS ANSWERED AT LEAST ONCE.
       *
       * WHAT IT SEPARATES IS "NO TEAMS" FROM "NOT YET". `teams` starts empty and is empty again
       * for every moment before the first fetch lands, so an empty list is TWO different facts
       * wearing one shape - and `reconcileActiveTeam`'s empty arm CLEARS THE TAB STRIP AND
       * PERSISTS IT. Without this flag the stored blob reads `open: []` while the store still
       * holds the tabs, and the next reload opens with no team tabs at all.
       *
       * Set where the answer arrives and never reset. A reconnect refetches, a failed fetch leaves
       * it as it was, and neither is a reason to un-know that the server has teams: the flag says
       * the question has been ANSWERED, not that the answer is fresh.
       */
      overviewLanded: false,

      // BRANDED EVEN THOUGH IT IS EMPTY. The store's field is what every caller reads, so if
      // it were a plain `string` the brand would stop at the API boundary and the one place
      // the identifier is actually USED would be unguarded again. Empty is the sentinel for
      // "no manager known yet" - `tell` requires a manager identifier to address.
      managerName: asMemberId(''),

      /** The instance ceiling on one workflow, from `/api/overview`. Null until the first fetch,
       *  and when the Host does not send the field - both read as "no ceiling stated". */
      workflowSpendLimit: null as number | null,

    /**
     * Activity keyed by whose card it belongs on - see `ownerOf`. Usually the
     * publisher, except an addressed instruction, which belongs to its addressee.
     */
    activity: {} as Record<string, Message[]>,

    /**
     * OLDER ROWS A PERSON ASKED FOR, per owner, newest first - "load older" on a card.
     *
     * KEPT APART FROM `activity` so the viewer's feed depth bounds only the LIVE TAIL that pushes
     * arrive into. History is what somebody scrolled down to read; trimming it as the next progress
     * line arrived would take the row out from under them. Once an owner has history, rows the live
     * window drops fall into it rather than off the end, so the two stay one unbroken list.
     */
    history: {} as Record<string, Message[]>,

    /** The cursor into the message log. Every fetch is bounded by it. */
    lastSeq: 0,

    /** A pull is in flight. Internal - see `pullMessages`. */
    pulling: false,

    /** Something changed while a pull was in flight, so pull once more when it lands. */
    pullQueued: false,

    /**
     * Team token totals from GET /api/teams/{team}/tokens — a log projection, not a sum of
     * `activity`. Null until the first fetch for the active team lands.
     */
    tokenUsage: null as TeamTokenTotals | null,

    /** Which team `tokenUsage` was fetched for. A stale response after a switch is dropped. */
    tokenUsageTeam: '' as string,

    usagePulling: false,
    usagePullQueued: false,

    /**
     * The workflow projection from GET /api/teams/{team}/workflow — how long the team's current or
     * last workflow has taken. Null until the first fetch for the active team lands, and null is
     * UNAVAILABLE rather than zero.
     */
    workflow: null as TeamWorkflowTiming | null,

    /** Which team `workflow` was fetched for. A stale response after a switch is dropped. */
    workflowTeam: '' as string,

    /**
     * MILLISECONDS TO ADD TO `Date.now()` TO GET THE SERVER'S CLOCK, taken once per fetch.
     *
     * THE BROWSER'S CLOCK MAY BE WRONG, and that is the only reason `serverNow` is on the payload.
     * Without this a machine ten minutes fast renders a workflow that started ten minutes in the
     * future — a negative duration, which is the kind of visible nonsense that gets diagnosed as a
     * server fault. Zero on a machine that agrees with the Host, which is almost all of them.
     */
    workflowClockOffset: 0,

    workflowPulling: false,
    workflowPullQueued: false,

    /**
     * EVERY workflow the active team has run since its floor — OPEN AND CLOSED ALIKE, newest first
     * and capped at fifty — from GET
     * /api/teams/{team}/workflows — the plural sibling of `workflow` above. Keyed by team id like
     * `rollup`, but populated one team at a time as `pullWorkflows` fetches for the active team,
     * rather than all at once. A team absent from this record has not been fetched for yet, which
     * `workflowsTile` reads as `null` — the same "not yet" the singular `workflow` field carries.
     */
    workflows: {} as Record<string, TeamWorkflows>,

    /** The fetch this store is waiting for; a slow answer landing last must not overwrite a newer
     *  one. Same guard `pullRollup` uses. */
    workflowsSeq: 0,

    /**
     * The repository status from GET /api/teams/{team}/repo-status — repos and their worktrees,
     * prerequisites, and unresolvedAgents from writes. Null until the first fetch for the active
     * team lands.
     */
    repoStatus: null as TeamRepoStatus | null,

    /** Which team `repoStatus` was fetched for. A stale response after a switch is dropped. */
    repoStatusTeam: '' as string,

    repoPulling: false,
    repoPullQueued: false,

    /**
     * Per-team workflow timing for the Teams table, keyed by team id. Empty until the first fetch.
     *
     * SEPARATE FROM `workflow`, which is the ACTIVE team's and drives the KPI tile. This one exists
     * for a page where there is deliberately no active team.
     */
    rollup: {} as Record<string, TeamWorkflowTiming>,

    /** The fetch this store is waiting for; a slow answer landing last must not overwrite a newer
     *  one. The board fires `pullRollup` on every hub push while the table is showing. */
    rollupSeq: 0,

    /**
     * The workflow a person is currently following, or null.
     *
     * Both sides of a hop share ONE correlation id, and without this that would be true and
     * invisible: the number is on every message the SPA holds and would be rendered nowhere.
     * Holding it in the STORE rather than in the feed component is what
     * makes it cross-card - a dispatch and the completion answering it are on two different cards
     * by definition, so a per-component highlight could never show both ends of the thing it exists
     * to show.
     */
    highlightedWorkflow: null as number | null,

    connected: false,
    /**
     * `connected` false at first paint only means the hub is still starting. Once this has ever
     * been true, a later false means the cards on screen may now be stale.
     */
    connectionEstablished: false,
    error: '' as string,

    /**
     * The sweep findings that are live right now, across every team, as `/api/overview` answers
     * them.
     *
     * This is the board's only sight of the DETECTOR'S VERDICT, and it is what the
     * TeamKpiStrip uses via `findingKinds`. Empty until the first refresh lands, and empty is the honest
     * answer then — nothing has said anything is wrong yet.
     *
     * It rides `/api/overview` rather than SignalR, so it moves on refresh and reconnect and not
     * on every push. A finding older than the sweep's window is not a fact that needs to arrive
     * within the second.
     */
    activeFindings: [] as ActiveFinding[],

    /**
     * The session ended underneath us - an expired cookie, or a host restarted while the tab sat
     * open. Distinct from `error` on purpose: being signed out is an ordinary state with an
     * obvious next step, and rendering "401 Unauthorized" in a red banner above an empty board
     * offers the person nothing they can act on. The page watches this and goes to the front door.
     */
    signedOut: false,

    /**
     * The team this window is working in. One team is shown at a time, chosen in the ribbon.
     *
     * A NAME rather than an object, because the team list is replaced wholesale on every refresh -
     * a held reference would go stale the first time anything changed, and comparing by identity
     * would silently select nothing.
     */
      activeTeamId: remembered.active,

      /**
       * Ordered team tabs.
       *
       * Stored by team id, because names change and ids are what every route and hub group uses.
       */
      openTeamTabs: remembered.open,

      /** See {@link ConsoleView}. */
      view: remembered.view,

    /**
     * A current-team announcement that arrived before the team list loaded.
     *
     * The guard at `applyCurrentTeam` cannot distinguish "not loaded" from "not present", so when
     * teams is empty we cannot know if the announced team exists: dropping it would be wrong if
     * we have just not heard the list yet. Instead, hold it here and apply it when the list
     * arrives.
     *
     * A second announcement replaces a held one rather than queuing - the current team is a fact,
     * not a stream, and the newest one is the only true one.
     *
     * Null when there is no pending announcement.
     */
    pendingCurrentTeam: null as string | null,

    /**
     * Tracks whether we are currently refreshing the teams list.
     *
     * Set to true when `refresh()` starts fetching new teams, and false when the new teams
     * arrive. Used to distinguish the reconnect window from the loaded-but-lacking case:
     * announcements arriving while refreshing are held instead of dropped against the stale list.
     */
    isRefreshing: false,
    };
  },

  getters: {
    hasTeams: (state) => state.teams.length > 0,
    /**
     * The ACTIVE teams: what the tabs, the ribbon's chooser, the Kanban and backlog dispatch offer.
     * An archived team stays in `teams`, so the Teams list, Delete and Clone still find it.
     */
    choosableTeams: (state): Team[] => state.teams.filter((team) => team.archived !== true),
    openTeams(): Team[] {
      return this.openTeamTabs
        .map((id) => this.choosableTeams.find((team) => team.id === id) ?? null)
        .filter((team): team is Team => team !== null);
    },
    /**
     * The teams whose SignalR groups this browser joins, which is the teams whose pushes it hears.
     *
     * EVERY TEAM WHILE THE TEAMS TABLE IS SHOWING, because the table shows every team: its Members
     * and Status cells are the pushed snapshots, and its Workflows and Last workflow cells are
     * re-read on each push (`refreshRollupIfShowing`). Open tabs alone left a team without a tab
     * frozen at the last overview while the polled WIP ledger beside it moved on - IDLE next to
     * `2 running`. On a board, the open tabs: a closed tab stops pushing.
     */
    liveTeamIds(state): string[] {
      if (state.view === 'teams') return state.teams.map((team) => team.id);

      return this.openTeams.map((team) => team.id);
    },
    /**
     * A card's activity, keyed on the PAIR.
     *
     * `ownerOf` yields the qualified `Team/Name` - Message.Source and the instruction-type suffix
     * both carry it - so a card asking for a bare `Manager` got `[]` permanently, and every feed on
     * the board was blank with nothing failing.
     *
     * Takes the two halves rather than a joined string so the key FORMAT stays inside this store,
     * beside the `ownerOf` that produces it. A caller passing `` `${team}/${name}` `` would be a
     * second place that has to agree about the separator, which is the kind of duplication that
     * drifts - and it is the same reason `applySnapshot` below matches team and name as separate
     * fields rather than reassembling an id to compare.
     */
    activityFor: (state) => (team: string, name: string): Message[] => {
      const live = state.activity[`${team}/${name}`] ?? [];
      const older = state.history[`${team}/${name}`];
      if (!older?.length) return live;

      // Strictly below the live tail's oldest row, so a row can never be listed twice.
      const floor = live.length ? live[live.length - 1]!.seq : Number.POSITIVE_INFINITY;
      return [...live, ...older.filter((message) => message.seq < floor)];
    },

    /**
     * Every message held for one team, across its members' buckets.
     *
     * The buckets are per CARD, WITH ONE EXCEPTION; this is the same rows read as one team's
     * history, which is what `teamLogFacts` needs to answer "is this workflow still open" and "what
     * did each member last say". A `workflow.completed` is published by the Manager and so lives on
     * the Manager's card — a per-card read could never see it from anywhere else.
     *
     * THE EXCEPTION IS THE TEAM'S OWN BUCKET, `<team>/`, which `ownerOf` uses for a row a PERSON
     * published about the team rather than about a container — `workflow.closed` and `kanban.card.*`.
     * A container can never own that key (`ContainerId.IsLegalName` requires at least one character,
     * starting alphanumeric), and `activityFor` COMPOSES its key rather than accepting one, so no
     * card can ask for it. It is here so this getter — and therefore `teamLogFacts` — can see a
     * close; nothing renders it, because every feed is per card.
     *
     * Neither half of a container id may contain a `/`, so the prefix cannot match another team.
     *
     * IT IS A WINDOW AND NOT THE LOG, and that limit is real: the board holds a bounded slice per
     * card. A team whose whole recent history has scrolled out reads as having published nothing,
     * which makes `teamStatus` fall back to what a snapshot alone can say rather than guess.
     */
    teamActivity: (state) => (team: string): Message[] => {
      const prefix = `${team}/`;

      return Object.entries(state.activity)
        .filter(([owner]) => owner.startsWith(prefix))
        .flatMap(([, messages]) => messages);
    },

    /**
     * The `kind`s of live sweep findings for one team.
     *
     * Matched on the team ID exactly as the server writes it into a finding. Absent and empty both
     * mean no finding.
     */
    findingKinds: (state) => (team: string): string[] =>
      state.activeFindings.filter((finding) => finding.team === team).map((finding) => finding.kind),

    /** The active team, or null. Null is an ordinary state: a fresh instance has no teams. */
    activeTeam: (state): Team | null =>
      state.teams.find((team) => team.id === state.activeTeamId) ?? null,

    /** One team's timing, or null. Null is ordinary: the first fetch has not landed. */
    workflowFor: (state) => (team: string): TeamWorkflowTiming | null =>
      state.rollup[team] ?? null,

    /** One team's PLURAL workflow projection, or null. Null is ordinary: `pullWorkflows` has not
     *  landed for this team yet, and the tile falls back to the singular rendering when it reads
     *  null - see `TeamKpiStrip`'s `workflows` prop. */
    workflowsFor: (state) => (team: string): TeamWorkflows | null =>
      state.workflows[team] ?? null,
    activeWorkTeam(): Team | null {
      return this.activeTeam;
    },
    activeWorkTeamId(): TeamId | '' {
      return this.activeWorkTeam?.id ?? '';
    },

    /**
     * What to CALL the active team on screen.
     *
     * Falls back to `activeTeamId` rather than to an empty string for a selection the team list
     * does not hold - which is the ordinary state for the frame between a reload and the first
     * `/api/overview`, and the whole time after a restart has emptied the server's teams. A heading
     * that blanked in those windows would read as "no team" when a team is exactly what is
     * selected. Empty only when nothing is selected at all, which the ribbon renders as "No team".
     */
    activeTeamLabel(state): string {
      return this.activeTeam?.name ?? state.activeTeamId;
    },
  },

  actions: {
    /**
     * Follow a workflow, or stop following it. Clicking the one already followed clears it, so the
     * control is its own off switch and there is no second affordance to find.
     *
     * A FLIP, NOT A SET — and that is only safe for a caller that shows the person the current
     * on/off state before they click, the way the feed's own `#<correlation>` chip does (it is
     * rendered lit when it is the one currently followed). A caller with no such state to show —
     * "Show thread" in the workflows dialog — must use {@link showWorkflow} below instead: a
     * button whose whole label is "show me this" must not flip an ALREADY-highlighted thread OFF,
     * which is the opposite of what it says - clicking it on an already-followed workflow would
     * close the dialog with the feed left highlighting nothing.
     */
    toggleWorkflow(correlationId: number) {
      this.highlightedWorkflow =
        this.highlightedWorkflow === correlationId ? null : correlationId;
      publishSteering(this.highlightedWorkflow);
    },

    /**
     * Follow a workflow OUTRIGHT, regardless of what is currently followed. The sibling of
     * {@link toggleWorkflow} for a caller that has no on/off state of its own to show — see that
     * action's own comment for why the two are not interchangeable.
     */
    showWorkflow(correlationId: number) {
      this.highlightedWorkflow = correlationId;
      publishSteering(correlationId);
    },

    /**
     * The full picture. Runs once on load, and again after a reconnect — SignalR
     * delivers *changes*, never current state, so a client that only listened
     * would sit blank until something happened to move.
     */
    setConnected(value: boolean) {
      this.connected = value;
      if (value) this.connectionEstablished = true;
    },

    async refresh() {
      try {
        // MARK THAT WE'RE REFRESHING SO ANNOUNCEMENTS THAT ARRIVE WHILE THIS IS IN FLIGHT
        // ARE HELD INSTEAD OF JUDGED AGAINST THE STALE TEAM LIST.
        this.isRefreshing = true;

        const [overview, messages] = await Promise.all([
          getOverview(),
          getMessages(this.lastSeq, windowNow()),
        ]);

        this.teams = overview.teams;
        // NEW TEAMS HAVE ARRIVED. Clear the refreshing flag so new announcements will be
        // judged against the fresh list.
        this.isRefreshing = false;

        // BEFORE `reconcileActiveTeam` BELOW, and that order is the whole of the legitimate empty
        // case: a restart that really did empty the server must still clear the strip.
        this.overviewLanded = true;

        // Apply any announcement that arrived while the team list was loading.
        this.applyPendingCurrentTeam();

        this.managerName = overview.managerName;
        this.workflowSpendLimit = overview.workflowSpendLimit ?? null;

        // `?? []` rather than a required field: a board talking to a Host that does not send
        // `activeFindings` must still work, and absent means "no finding".
        this.activeFindings = overview.activeFindings ?? [];
        this.reconcileActiveTeam();
        this.record(messages);
        this.error = '';
        await this.pullTokenUsage();
        await this.pullWorkflow();
        await this.pullWorkflows();
        await this.pullRepoStatus();
      } catch (cause) {
        this.isRefreshing = false; // Clear flag on error too
        if (cause instanceof Unauthorized) {
          this.signedOut = true;
          this.error = '';
          return;
        }

        this.error = cause instanceof Error ? cause.message : String(cause);
      }
    },

    setActiveTeam(id: TeamId | '') {
      const changed = this.activeTeamId !== id;
      this.activeTeamId = id;
      const picked = this.choosableTeams.find((team) => team.id === id);
      if (id && picked && !this.openTeamTabs.includes(id)) {
        this.openTeamTabs.push(id);
      }
      this.rememberTabs();
      if (changed) {
        // THE SERVER IS TOLD, ON EVERY CHANGE, and this is the one writer that tells it.
        //
        // Without this the browser's active team and the Concierge's current team are two facts
        // that only look like one: the agent would follow `harness team switch` and not a tab
        // click, which is the confusion this whole design removes, relocated rather than fixed.
        //
        // NOT DEBOUNCED. A delay buys one saved request per flicked tab and costs a window in which
        // the panel header and the agent disagree - measure the volume before adding one.
        //
        // '' is sent as null: no team is active, which is what the Teams overview means and a state
        // the server stores rather than an absence it ignores.
        void this.pushCurrentTeam(id || null);

        void this.pullTokenUsage();

        // A TEAM SWITCH IS A REFETCH TRIGGER. The tile shows THIS team's workflow, and the
        // previous one's figure left on screen for a second reads as this one's.
        void this.pullWorkflow();
        void this.pullWorkflows();

        // Fetch repo status the same way tokens and workflow are fetched.
        void this.pullRepoStatus();
      }
    },

    /**
     * Show the Teams table, and with it NO ACTIVE TEAM.
     *
     * The clearing is the point and it is the one place this differs from the Kanban tab, which
     * deliberately keeps the active team because it is a view change and never a team switch. Here
     * "no team is active" is a real state a person is genuinely in, and `setActiveTeam('')` is the
     * single writer that tells the server so - which is what moves the Concierge with them.
     */
    showTeamsView() {
      this.view = 'teams';
      this.setActiveTeam('');

      // THE TEAM LIST ITSELF, not only the timings. `pullRollup` answers what each team is DOING;
      // the teams a person can see come from `/api/overview`, and nothing else re-reads it while
      // the console is open. Without this, a team created anywhere else - another browser, the
      // CLI, a clone - would be invisible until a hard refresh.
      //
      // It is not the whole answer: a team appearing WHILE the table is on screen still waits for the
      // next open, because a new team's snapshot is pushed to that team's SignalR group and this
      // browser has never joined it. That needs an event on a group the browser is already in -
      // the shape `currentTeamChanged` uses - and is tracked separately.
      void this.refresh();
      void this.pullRollup();
    },

    /** Back to a team's board. Takes no team: the caller decides what is active. */
    showBoard() {
      this.view = 'board';
      this.rememberTabs();
    },

    /**
     * A team picked from a list - the Teams list's row, the ribbon's Choose Team - opens on its
     * board: the board is shown, then the team made active. Making it active alone opened its tab
     * but left the Teams list or the Kanban on screen when no team's board was showing.
     */
    openTeam(id: TeamId) {
      this.showBoard();
      this.setActiveTeam(id);
    },

    /**
     * A current team that moved somewhere else - another device, or `harness team switch` inside
     * the Concierge.
     *
     * IGNORES THE ECHO OF THIS BROWSER'S OWN WRITE. Every tab click PUTs and every PUT is announced
     * back - so without that check a click would write, hear its own echo, and write again, and two
     * devices could ping-pong a team between them. THIS METHOD DOES NOT RE-CHECK IT: one fact, one
     * place, and a second copy of the same condition here is the shape that goes stale. It is
     * pinned from this side too, so moving it out of `setActiveTeam` fails a test that names the
     * echo rather than only tests about tab switching.
     *
     * A team this browser cannot see IS checked here, because nothing else asks: `setActiveTeam`
     * would put an id in the tab strip with no row behind it, which renders as a tab that cannot be
     * opened. The person's other device is welcome to be somewhere this one is not.
     *
     * HOLDS ANNOUNCEMENTS WHEN THE LIST HAS NOT LOADED YET. Before the list arrives, the
     * guard cannot distinguish "not loaded" from "not present", so announcements are HELD rather
     * than DROPPED. When the list arrives, `applyPendingCurrentTeam` is called to apply held ones.
     * A second announcement replaces a held one rather than queuing.
     */
    applyCurrentTeam(team: string | null) {
      const id = team ? asTeamId(team) : '';

      // HOLD announcements in two cases:
      // 1. List has not loaded yet (!overviewLanded)
      // 2. We're refreshing the list (isRefreshing) - reconnect window
      //
      // In both cases, we cannot distinguish "announced team not loaded" from 
      // "announced team not present", so we hold and apply when new teams arrive.
      if (id && (!this.overviewLanded || this.isRefreshing)) {
        this.pendingCurrentTeam = team;
        return;
      }

      // If we have a team id and it's not in the loaded list, drop it (not on this browser).
      if (id && !this.teams.some((candidate) => candidate.id === id)) return;

      this.setActiveTeam(id);
      // Clear any pending announcement once we apply one.
      this.pendingCurrentTeam = null;
    },

    /**
     * Apply a pending current-team announcement that was held while the list was loading.
     *
     * Called after `/api/overview` lands (when overviewLanded becomes true) to apply any
     * announcement that arrived while teams was still loading. If the announced team is now in the
     * loaded list, apply it; otherwise drop it (the team doesn't exist on this browser).
     */
    applyPendingCurrentTeam() {
      if (!this.pendingCurrentTeam) return;

      const pending = this.pendingCurrentTeam;
      this.pendingCurrentTeam = null;

      const id = asTeamId(pending);
      // If the team exists in the loaded list, apply it through the normal path.
      if (this.teams.some((candidate) => candidate.id === id)) {
        this.setActiveTeam(id);
      }
      // Otherwise drop it - the team doesn't exist on this browser.
    },

    /**
     * Re-reads the team list when a visible team was CREATED elsewhere.
     *
     * The no-op is the self-echo rule: the browser that just refreshed itself after creating the
     * team, or a browser whose reconnect refresh is already in flight, must not fetch the same list
     * again because the hub repeated the fact it already holds.
     */
    async refreshForTeamCreated(team: string) {
      if (this.isRefreshing || this.teams.some((candidate) => candidate.id === team)) return;

      await this.refresh();
      this.refreshRollupIfShowing();
    },

    /**
     * Re-reads the team list when a visible team was DELETED elsewhere.
     *
     * The mirror of `refreshForTeamCreated`: if the team is already gone locally, or the list is
     * already being re-read, the announcement changes nothing and fetches nothing.
     */
    async refreshForTeamDeleted(team: string) {
      if (this.isRefreshing || !this.teams.some((candidate) => candidate.id === team)) return;

      await this.refresh();
      this.refreshRollupIfShowing();
    },

    /**
     * Writes the current team to the server, and says nothing when it fails.
     *
     * DELIBERATELY SILENT. This is a background convenience: the tab still switched, the board
     * still renders, and a toast about a failed sync would be noise over a click that visibly
     * worked. What it costs is that the Concierge stays on the previous team until the next
     * successful switch - and asking the platform for the current team still tells the truth about that, which is
     * the whole reason the agent is told to ask rather than remember.
     */
    async pushCurrentTeam(id: TeamId | null) {
      try {
        await setCurrentTeam(id);
      } catch {
        // Nothing. See above.
      }
    },

    closeTeamTab(id: TeamId) {
      const index = this.openTeamTabs.indexOf(id);
      if (index < 0) return;

      this.openTeamTabs.splice(index, 1);

      if (this.activeTeamId !== id) {
        this.rememberTabs();
        return;
      }

      const next = this.openTeamTabs[index] ?? this.openTeamTabs[index - 1] ?? '';

      // THE STRIP CANNOT EMPTY - the Teams tab is permanent - so closing the last team tab is not a
      // no-op. Closing the last one lands on the table with no
      // active team, which is exactly the state that view is for.
      //
      // SET BEFORE THE WRITE BELOW, because `setActiveTeam` calls `rememberTabs()` and the blob it
      // persists carries the view - so the other order stores the view being left. Same ordering as
      // `showTeamsView`.
      if (next === '') this.view = 'teams';

      // THROUGH `setActiveTeam`, WHICH IS THE ONE WRITER THAT TELLS THE SERVER. Assigning
      // `activeTeamId` here instead is the browser's active team and the Concierge's current team
      // becoming two facts that only look like one - the exact defect the current-team work removed,
      // relocated into this method. It matters most on the path this feature added: landing on the
      // Teams table means NO active team, and the Concierge is supposed to follow.
      //
      // It pushes on an ORDINARY close too, and that is correct rather than a side effect: if the
      // browser's active team changed, the person's current team changed. Closing a NON-ACTIVE tab
      // still pushes nothing - that path returned above, and `setActiveTeam` no-ops an unchanged id.
      this.setActiveTeam(next);

      this.reconcileActiveTeam();
    },

    /**
     * Joins one team's SignalR group.
     *
     * Takes the connection as a PARAMETER rather than holding it in state: a `HubConnection` is a
     * class instance with private fields, and Pinia wraps state in a reactive Proxy that breaks
     * private-field access on a wrapped instance. The connection stays owned by whoever created
     * it; only the DECISION of what a failed join means to the person looking at the board belongs
     * here - which is also what makes that decision testable without a real socket.
     *
     * The matching leave is done only when a tab closes. A tab switch alone does not leave the
     * previous team, so background tabs stay live.
     */
    async switchHubTeam(connection: HubConnection, team: string): Promise<boolean> {
      try {
        await joinTeam(connection, team);
        return true;
      } catch {
        this.reportJoinFailure(team);
        return false;
      }
    },

    async leaveHubTeam(connection: HubConnection, team: string) {
      if (!team) return;
      void leaveTeam(connection, team);
    },

    /**
     * A JoinTeam invoke failed. Unlike a missed `containerChanged` push, which the next change
     * repairs on its own, nothing retries a failed join: the connection stays open and reports
     * "live" while the group for this team was never joined, so the board silently stops
     * updating - indistinguishable from a team where nothing is happening, which is the exact
     * symptom per-team groups exist to remove. Reuses `error`/the existing banner rather than a
     * new piece of state, because the person needs to know the same thing either way: what they
     * are looking at is not reliable. Worded as a transport problem rather than echoing the raw
     * exception - `HubException("No such team.")` reads as "you don't have this team", which is
     * misleading for a connection that already passed that same check once to get here.
     */
    reportJoinFailure(teamId: string) {
      const name = this.teams.find((team) => team.id === teamId)?.name ?? teamId;
      this.error = `Live updates are unavailable for ${name} - reload to try again.`;
    },

    /**
     * Keeps the selection pointing at a team that exists.
     *
     * Teams live in memory on the server, so a restart empties the list and a remembered name
     * dangles - and a board filtered to a team that is gone shows nothing at all, which reads as
     * a broken page rather than as a stale selection. Falling back to the first surviving tab means
     * the board always has something to show whenever there is anything to show.
     */
    reconcileActiveTeam() {
      // AN EMPTY LIST IS ONLY AN ANSWER ONCE THE SERVER HAS GIVEN ONE. Before the first
      // `/api/overview` lands, `teams` is empty because nothing has been asked yet - and the arm
      // below does not merely decline to pick a team, it CLEARS THE TAB STRIP AND WRITES THAT TO
      // localStorage. So a reconcile reaching this method early (a tab closed on a slow load, a
      // reconnect racing the first fetch) persisted `open: []` over a strip the person had built,
      // and the next reload opened with no team tabs.
      //
      // RETURNING TOUCHES NOTHING, which is the recoverable direction: the strip and the active
      // team stay exactly as the last reload remembered them, and the refresh that is already in
      // flight reconciles them properly a moment later. The legitimate case - a restart that
      // really did empty the server - is unchanged, because `refresh` sets the flag BEFORE it
      // calls this.
      if (this.teams.length === 0 && !this.overviewLanded) return;

      if (this.teams.length === 0) {
        // THE TABS FIRST, THEN THE TEAM, because `setActiveTeam` calls `rememberTabs()` and the
        // blob it persists carries the strip - the other order stores the tabs being cleared.
        this.openTeamTabs = [];

        // THROUGH THE ONE WRITER. See below: this arm moves the active team exactly as the other
        // one does, and an assignment here is the same defect in the quieter place.
        this.setActiveTeam('');
        return;
      }

      const choosable = new Set(this.choosableTeams.map((team) => team.id));

      this.openTeamTabs = this.openTeamTabs.filter((id, index, all) =>
        choosable.has(id) && all.indexOf(id) === index,
      );

      // An archived team never gets a tab back, even while it is the active one.
      const active = this.choosableTeams.find((team) => team.id === this.activeTeamId) ?? null;
      if (active !== null && !this.openTeamTabs.includes(active.id)) {
        this.openTeamTabs.push(active.id);
      }

      // NOT WHILE THE TEAMS VIEW IS SHOWING. Everything below exists to keep a BOARD pointing at a
      // team that exists; on the Teams table there is deliberately no active team, and forcing one
      // here would write it to the server on every reload.
      if (this.view !== 'teams') {
        if (this.openTeamTabs.length === 0 && this.choosableTeams.length > 0) {
          this.openTeamTabs = [this.choosableTeams[0]!.id];
        }

        const activeExists = this.choosableTeams.some((team) => team.id === this.activeTeamId);
        const activeIsOpenTeam = active !== null && this.openTeamTabs.includes(active.id);

        if (!activeExists || !activeIsOpenTeam) {
          // THROUGH `setActiveTeam`, WHICH IS THE ONE WRITER THAT TELLS THE SERVER - the same rule
          // `closeTeamTab` follows, and this is where it was missing.
          //
          // Assigning `activeTeamId` here made the browser's active team and the Concierge's
          // current team two facts that only look like one, on a path nothing repairs: click
          // Teams (active '', server told null), click Kanban (the view moves, the active team
          // does not), then reload or let any push fire a refresh - this arm picks
          // `openTeamTabs[0]`, the strip shows that team with its `team-` ribbon actions enabled,
          // and `harness team current` still answers "no team is active" while `tell` refuses.
          // Clicking the tab cannot repair it either: `setActiveTeam` sees no change and no-ops.
          //
          // NO RECURSION: `setActiveTeam` does not call back into this method, and it no-ops when
          // the id did not move - which is also why this method does no refetches of its own.
          // They are `setActiveTeam`'s, and firing them here as well would be one extra round trip
          // per reconcile for tokens and workflow both.
          this.setActiveTeam(this.openTeamTabs[0] ?? '');
        }
      }

      this.rememberTabs();
    },

    rememberTabs() {
      writeRememberedTabs({ open: this.openTeamTabs, active: this.activeTeamId, view: this.view });
    },

    /**
     * New messages only, bounded by the cursor. Called when something changed —
     * not on a timer. A snapshot push says a container moved but carries no
     * messages, so without this the feed would freeze while the cards updated.
     *
     * COALESCED, because a container publishes a snapshot at least three times per
     * instruction — on enqueue, on Running, on Idle. Overlapping pulls each read
     * the cursor before any of them advanced it, fetched the same batch, and
     * recorded it more than once: every row appeared twice with the same text and
     * the same timestamp.
     *
     * A pull already in flight will pick up anything that arrives while it runs,
     * because the fetch is always "everything after the cursor" — so a second
     * caller only has to ask for one more lap, never its own request.
     */
    async pullMessages() {
      if (this.pulling) {
        this.pullQueued = true;
        return;
      }

      this.pulling = true;

      try {
        do {
          this.pullQueued = false;
          this.record(await getMessages(this.lastSeq, windowNow()));
        } while (this.pullQueued);
      } catch {
        // A failed delta is not worth surfacing: the next change pulls again, and
        // an error banner that clears itself is noise rather than information.
      } finally {
        this.pulling = false;
      }
    },

    /**
     * One container changed. Replaces it in place rather than refetching the
     * world — that difference is the whole reason the board is push-driven.
     */
    applySnapshot(snapshot: ContainerSnapshot) {
      // Matched on the PAIR. Matching on name alone would return the first hit across all teams, so
      // with two teams each holding a Manager a snapshot for one would update the other's card.
      const team = this.teams.find((t) => t.id === snapshot.team);

      if (!team) {
        // A team this client does not hold. With per-team SignalR groups the hub only
        // ever puts a connection in the group for a team it is entitled to, so this should not be
        // reachable in the running system - but `refresh()` here would fetch /api/overview AND
        // /api/messages, and a stray snapshot arriving on some stale membership would then be an
        // unbounded reload loop rather than one dropped push. Silently discarding it is strictly
        // safer than either refreshing on it or rendering it.
        return;
      }

      const index = team.containers.findIndex((c) => c.id === snapshot.id);

      if (index >= 0) {
        const previous = team.containers[index];
        team.containers[index] = snapshot;
        void this.pullTokenUsage();

        // NARROWER THAN THE TOKENS TRIGGER, ON PURPOSE. A container publishes a snapshot at least
        // three times per instruction and again for every progress line, and only two of those
        // movements can BEGIN or END a workflow: a member entering or leaving `Running`, and
        // `currentCorrelation` moving. Refetching on the rest would put a request behind every
        // status line a chatty member writes, for a payload that had not changed.
        if (
          previous === undefined
          || previous.state !== snapshot.state
          || previous.currentCorrelation !== snapshot.currentCorrelation
        ) {
          void this.pullWorkflow();
          void this.pullWorkflows();
        }

        return;
      }

      // A container this client has never seen, on a team it already holds. Ask
      // for the shape of the world rather than guessing where to put it.
      void this.refresh();
    },

    /**
     * Idempotent by seq, and that is the load-bearing part.
     *
     * Coalescing above makes overlapping pulls unlikely; this makes a duplicate
     * impossible. `refresh` and `pullMessages` both fetch after the cursor and can
     * still interleave, and the cost of being wrong is a feed that quietly says
     * a thing happened twice — which is worse than a feed that lags.
     */
    record(messages: Message[]) {
      for (const message of messages) {
        if (message.seq <= this.lastSeq) continue;

        this.lastSeq = message.seq;

        const owner = ownerOf(message);
        const existing = this.activity[owner] ?? [];

        // THE VIEWER'S depth, read per append rather than captured once, so raising it in the
        // Board display dialog takes effect on the next message rather than on the next reload.
        // Falls back to the constant when the display store has not hydrated - which is the frame
        // between this store waking and Pinia creating that one, and is not worth a crash.
        const next = [message, ...existing];
        const depth = depthNow();
        this.activity[owner] = next.slice(0, depth);

        // The live window bounds only the pushed tail. What it drops from an owner somebody has
        // read back through becomes history rather than a gap between the two.
        const older = this.history[owner];
        if (older && next.length > depth) this.history[owner] = [...next.slice(depth), ...older];
      }
    },

    /**
     * Rows read back through `?before=` for one card, appended BELOW everything it holds.
     *
     * Only rows that are this owner's and older than its oldest held row are kept, so a page that
     * overlaps the live tail - or a push racing the read - cannot put a row on the card twice.
     */
    recordHistory(team: string, name: string, messages: Message[]) {
      const owner = `${team}/${name}`;
      const held = this.activityFor(team, name);
      const floor = held.length ? held[held.length - 1]!.seq : Number.POSITIVE_INFINITY;

      const older = messages
        .filter((message) => ownerOf(message) === owner && message.seq < floor)
        .sort((a, b) => b.seq - a.seq);

      this.history[owner] = [...(this.history[owner] ?? []), ...older];
    },

    /**
     * Team token totals from the log. Coalesced the same way as pullMessages: a snapshot
     * fires on enqueue, Running and Idle, and overlapping SUMs of the same log are wasted
     * work rather than a race on the number.
     *
     * THE NUMBER IS NOT SUMMED FROM `activity`. That array is a twenty-row slice; summing
     * it would count down as the team got busier.
     */
    async pullTokenUsage() {
      const team = this.activeTeamId;
      if (!team) {
        this.tokenUsage = null;
        this.tokenUsageTeam = '';
        return;
      }

      if (this.usagePulling) {
        this.usagePullQueued = true;
        return;
      }

      this.usagePulling = true;

      try {
        do {
          this.usagePullQueued = false;
          const fetchedFor = this.activeTeamId;
          if (!fetchedFor) {
            this.tokenUsage = null;
            this.tokenUsageTeam = '';
            break;
          }

          const usage = await getTeamTokens(fetchedFor);
          if (this.activeTeamId !== fetchedFor) continue;

          this.tokenUsage = usage;
          this.tokenUsageTeam = fetchedFor;
        } while (this.usagePullQueued);
      } catch {
        // A failed total is not a zero. Keep what we have if it is still for this team;
        // otherwise leave null so the card stays unavailable rather than inventing spend.
        if (this.tokenUsageTeam !== this.activeTeamId) {
          this.tokenUsage = null;
        }
      } finally {
        this.usagePulling = false;
      }
    },

    /**
     * The workflow projection for the active team.
     *
     * THE SIBLING OF `pullTokenUsage`, AND DELIBERATELY THE SAME SHAPE rather than a second one
     * invented beside it: single-flight with a `queued` re-run, keyed by team, a response for a
     * team we have since switched away from dropped, and a FAILURE KEEPING THE PREVIOUS VALUE
     * rather than replacing it with a zero. That method carries the reasoning; this reads like it
     * on purpose, because two coalescing schemes that differ in one clause is how one of them ends
     * up wrong with nothing failing.
     *
     * ONE FETCH IS ENOUGH FOR THE TILE TO COUNT UP. The payload carries instants, not a computed
     * elapsed number, so the component ticks with no traffic between ticks. This runs on a team
     * switch, on a snapshot change that could BEGIN or END a workflow, and after each of the three
     * workflow actions — close, nudge and stop — because a close ENDS a workflow without any
     * snapshot moving, which is how the tile once went on reading UNDECLARED while the dialog beside
     * it read CLOSED. NEVER ON A TIMER, and that clause is the load-bearing one.
     */
    async pullWorkflow() {
      const team = this.activeTeamId;
      if (!team) {
        this.workflow = null;
        this.workflowTeam = '';
        return;
      }

      if (this.workflowPulling) {
        this.workflowPullQueued = true;
        return;
      }

      this.workflowPulling = true;

      try {
        do {
          this.workflowPullQueued = false;
          const fetchedFor = this.activeTeamId;
          if (!fetchedFor) {
            this.workflow = null;
            this.workflowTeam = '';
            break;
          }

          const timing = await getTeamWorkflow(fetchedFor);
          if (this.activeTeamId !== fetchedFor) continue;

          // TAKEN ONCE PER FETCH, against the clock reading closest to the response. Recomputing it
          // on every tick would freeze the tile: the offset would grow by exactly the second that
          // had passed, and the difference the component renders would never move.
          const serverNow = Date.parse(timing.serverNow);
          this.workflowClockOffset = Number.isNaN(serverNow) ? 0 : serverNow - Date.now();

          this.workflow = timing;
          this.workflowTeam = fetchedFor;
        } while (this.workflowPullQueued);
      } catch {
        // A failed fetch is not an elapsed time of zero. Keep what we have if it is still for this
        // team; otherwise leave null so the tile reads as an em dash rather than inventing a
        // duration.
        if (this.workflowTeam !== this.activeTeamId) {
          this.workflow = null;
        }
      } finally {
        this.workflowPulling = false;
      }
    },

    /**
     * EVERY workflow the active team has run since its floor, open and closed alike — the plural
     * sibling of `pullWorkflow` above.
     *
     * GUARDED THE SAME WAY `pullRollup` IS, not `pullWorkflow`'s single-flight/queued shape: a
     * monotonic sequence bumped on every call, and a response applied only if nothing newer has
     * started since. Several of these are routinely in flight - a busy team pushes constantly, and
     * every push that could begin or end a workflow fires one - so a slow answer landing last must
     * not overwrite a newer one, across a team switch as much as within one team.
     *
     * `pullRollup` ALSO WRITES THIS SAME MAP, for every team it names rather than the active one
     * alone - see its own comment for why the two are not in conflict and which one wins when both
     * have run.
     */
    async pullWorkflows() {
      const team = this.activeTeamId;
      if (!team) return;

      const seq = ++this.workflowsSeq;

      try {
        const answer = await getTeamWorkflows(team);

        // A SLOW ANSWER LANDING LAST MUST NOT WIN. See `pullRollup`'s identical guard.
        if (seq !== this.workflowsSeq) return;

        this.workflows[team] = answer;
      } catch (cause) {
        if (cause instanceof Unauthorized) {
          this.signedOut = true;
          return;
        }

        // SILENT OTHERWISE, AND `workflows` IS LEFT EXACTLY AS IT WAS. The tile falls back to the
        // singular `workflow` rendering for a team this has never landed for, and a team that
        // already had a plural payload keeps showing it rather than blanking on one failed fetch.
      }
    },

    /**
     * The repo status for the active team.
     *
     * THE SIBLING OF `pullTokenUsage` AND `pullWorkflow`, and deliberately the same shape rather
     * than a third one invented beside them: single-flight with a `queued` re-run, keyed by team,
     * a response for a team we have since switched away from dropped, and a FAILURE KEEPING THE
     * PREVIOUS VALUE rather than replacing it with null. Fetched when the active team is SET, NOT
     * on every SignalR frame — repos are static metadata, not live state.
     */
    async pullRepoStatus() {
      const team = this.activeTeamId;
      if (!team) {
        this.repoStatus = null;
        this.repoStatusTeam = '';
        return;
      }

      if (this.repoPulling) {
        this.repoPullQueued = true;
        return;
      }

      this.repoPulling = true;

      try {
        do {
          this.repoPullQueued = false;
          const fetchedFor = this.activeTeamId;
          if (!fetchedFor) {
            this.repoStatus = null;
            this.repoStatusTeam = '';
            break;
          }

          const status = await getRepoStatus(fetchedFor);
          if (this.activeTeamId !== fetchedFor) continue;

          this.repoStatus = status;
          this.repoStatusTeam = fetchedFor;
        } while (this.repoPullQueued);
      } catch {
        // A failed fetch is not missing repos. Keep what we have if it is still for this team;
        // otherwise leave null.
        if (this.repoStatusTeam !== this.activeTeamId) {
          this.repoStatus = null;
        }
      } finally {
        this.repoPulling = false;
      }
    },

    /**
     * Fetches every reachable team's workflow projection.
     *
     * NO TIMER, and there must not be one - see this store's own remarks. Called when the Teams
     * table is opened and again on the `containerChanged` pushes the page already receives, which
     * is the same shape `kanban.refreshIfActive()` uses.
     */
    async pullRollup() {
      const seq = ++this.rollupSeq;

      try {
        const answer = await getTeamsRollup();

        // A SLOW ANSWER LANDING LAST MUST NOT WIN. Several of these are routinely in flight: a busy
        // team pushes constantly, and every push fires one.
        if (seq !== this.rollupSeq) return;

        this.rollup = Object.fromEntries(answer.teams.map((row) => [row.team, row.workflow]));

        // THE PLURAL PROJECTION, FOR EVERY TEAM THIS ROLLUP NAMES - not only the active one.
        // `pullWorkflows` above writes the SAME map, keyed the same way, for the active team alone;
        // that read is fresher and cheaper for the one team a person is actually looking at, so it
        // stays. The two are not in conflict: each is guarded against ITS OWN stale responses by
        // its own sequence counter (`workflowsSeq` there, `rollupSeq` here), so whichever of the two
        // calls LANDS LAST wins for a team both happen to name - the same last-write-wins rule this
        // store already applies to `rollup` itself, one line up. Replacing the WHOLE map (not
        // merging into it) is deliberate too: it drops a team no longer in `answer.teams` - one
        // whose access was revoked, or that was deleted - exactly as the `rollup` assignment above
        // already does.
        this.workflows = Object.fromEntries(answer.teams.map((row) => [row.team, row.openWorkflows]));
      } catch (cause) {
        if (cause instanceof Unauthorized) {
          this.signedOut = true;
          return;
        }

        // SILENT OTHERWISE, AND `rollup` IS LEFT EXACTLY AS IT WAS. The table still renders every
        // team it knows about from /api/overview, and a row that already had timing keeps showing
        // it rather than blanking on one failed fetch.
        //
        // WHAT A ROW WITH NO TIMING SAYS IS `not loaded yet`, NEVER `never run`. That distinction
        // is `lastWorkflowText`'s and it is what makes staying silent honest here: this arm can
        // leave every row without timing for the rest of a session, and "never run" is a claim the
        // server never made about teams that may run constantly. The Status column falls back to
        // what the snapshots say, which is real data this fetch was never the source of.
      }
    },

    /** Refetch only while the table is showing - traffic for a page nobody is looking at has no
     *  reader. Opening the tab fetches. */
    refreshRollupIfShowing() {
      if (this.view !== 'teams') return;

      void this.pullRollup();
    },
  },
});

if (import.meta.hot) {
  import.meta.hot.accept(acceptHMRUpdate(useConsoleStore, import.meta.hot));
}
