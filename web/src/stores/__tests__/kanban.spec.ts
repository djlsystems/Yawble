import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import type { KanbanBoard, KanbanCard, KanbanFilters } from '../../api/kanban';
import { asTeamId, type Team, type TeamId } from '../../api/types';

/**
 * A team with sensible defaults, matching the console store's own fixture (`console.spec.ts`) so a
 * team built here looks like one the server would actually send.
 */
const team = (id: TeamId, overrides: Partial<Team> = {}): Team => ({
  id,
  name: id,
  containers: [],
  concierge: 'claude',
  memberAgents: null,
  additionalInstructions: null,
  root: null,
  ...overrides,
});

const card = (overrides: Partial<KanbanCard> & Pick<KanbanCard, 'id'>): KanbanCard => ({
  workflowSeq: 1842,
  team: 'researchkanban',
  member: 'DeveloperDorian',
  title: 'A card',
  body: '',
  status: 'running',
  laneId: 'in-progress',
  progress: [],
  createdAt: '2026-09-06T10:00:00Z',
  updatedAt: '2026-09-06T11:00:00Z',
  awaitingManager: false,
  paused: false,
  ...overrides,
});

const board: KanbanBoard = {
  
  lanes: [
    { id: 'todo', title: 'To Do' },
    { id: 'in-progress', title: 'In Progress' },
    { id: 'done', title: 'Done' },
  ],
  cards: [
    card({ id: 'a' }),
    card({ id: 'b', laneId: 'todo', member: 'ResearcherRosa' }),

    // A CARD OF ANOTHER TEAM, because the board is tenant-wide and every per-card call has to
    // be addressed under the card's OWN team. With one team's cards on the fixture, "the card's
    // team" and "the active team" are the same string and a test cannot tell them apart.
    card({ id: 'c', team: 'otherteam', member: 'OpsOona' }),
  ],
};

/** Resolves only when released, so two fetches can be held in flight at once. */
function deferred<T>() {
  let release!: (value: T) => void;
  const promise = new Promise<T>((resolve) => (release = resolve));
  return { promise, release };
}

const getKanbanBoard = vi.fn(async (_filters?: KanbanFilters) => board);
const getKanbanCard = vi.fn(async (_team: unknown, id: string) => ({ ...card({ id }), trail: [] }));
const moveKanbanCard = vi.fn(async (_team: unknown, _id: string, _lane: string, _note?: string) => undefined);
const editKanbanCard = vi.fn(async (_team: unknown, _id: string, _changes: unknown) => undefined);
const commentKanbanCard = vi.fn(async (_team: unknown, _id: string, _text: string) => undefined);

vi.mock('../../api/kanban', async () => {
  // The types and the status list are real: only the seven functions that touch the network are
  // replaced, so a rename of a type or a change to `KanbanStatuses` still fails compilation here.
  const actual = await vi.importActual<typeof import('../../api/kanban')>('../../api/kanban');

  return {
    ...actual,
    getKanbanBoard: (...args: unknown[]) => getKanbanBoard(...(args as [KanbanFilters])),
    getKanbanCard: (...args: unknown[]) => getKanbanCard(...(args as [unknown, string])),
    moveKanbanCard: (...args: unknown[]) => moveKanbanCard(...(args as [unknown, string, string])),
    editKanbanCard: (...args: unknown[]) => editKanbanCard(...(args as [unknown, string, unknown])),
    commentKanbanCard: (...args: unknown[]) => commentKanbanCard(...(args as [unknown, string, string])),
  };
});

const { useKanbanStore } = await import('../kanban');
const { useConsoleStore } = await import('../console');

/**
 * THE BOARD FETCH DOES NOT READ THE ACTIVE TEAM AT ALL.
 *
 * `getKanbanBoard` takes the filters and nothing else, and the route it calls resolves the
 * caller's reachable teams itself - so choosing a team in the filter bar is what reaches the
 * server, not whichever team the console has active.
 *
 * These specs still put an active team on the console store: the point of several of them is that
 * the board ignores it, and card writes still address themselves under the CARD's own team - see
 * `Team` below, used only by the write specs.
 */
const Team = asTeamId('researchkanban');

describe('the kanban tab', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    useConsoleStore().activeTeamId = Team;
    vi.clearAllMocks();
    getKanbanBoard.mockImplementation(async () => board);
  });

  /** The console starts on the container cards. The board is a tab you go to. */
  it('starts on the team view', () => {
    expect(useKanbanStore().active).toBe(false);
  });

  it('shows the board when the tab is selected', async () => {
    const kanban = useKanbanStore();
    kanban.showKanban('researchkanban');

    expect(kanban.active).toBe(true);
    await vi.waitFor(() => expect(getKanbanBoard).toHaveBeenCalled());
  });

  /** Default filter = the team you were looking at. */
  it('opens filtered to the active team', () => {
    const kanban = useKanbanStore();
    kanban.showKanban('researchkanban');

    expect(kanban.filters).toEqual({ team: 'researchkanban' });
  });

  /**
   * THE TAB BUTTON CLEARS EVERYTHING AND SETS THE TEAM.
   *
   * Applying the default only once would make the same click mean different things depending on
   * what the board was last narrowed to: a person clicking Kanban on a team's tab strip is saying
   * "show me this team's work", and landing on last week's status filter over a different team
   * reads as the click having done nothing.
   */
  it('clears every filter and sets the team, however the board was last narrowed', () => {
    const kanban = useKanbanStore();
    kanban.filters = { team: 'other', status: 'blocked', member: 'Delia' };
    kanban.showKanban('researchkanban');

    expect(kanban.filters).toEqual({ team: 'researchkanban' });
  });

  it('opens with no team filter when no team is active', () => {
    const kanban = useKanbanStore();
    kanban.filters = { team: 'other', status: 'blocked' };
    kanban.showKanban('');

    expect(kanban.filters).toEqual({});
  });

  /**
   * THE RIBBON'S KANBAN BUTTON AND THE TAB BUTTON ARE THE SAME ACT. Both are a person naming a
   * team and asking to see it, so both clear what was there. Merging the team into the existing
   * filters would make the same icon mean two different things depending on which of the two you
   * reached it from.
   */
  it("clears the rest when opened from a team's ribbon, exactly as the tab does", () => {
    const kanban = useKanbanStore();
    kanban.filters = { team: 'other', status: 'blocked' };
    kanban.openForTeam('researchkanban');

    expect(kanban.active).toBe(true);
    expect(kanban.filters).toEqual({ team: 'researchkanban' });
  });

  /**
   * ACTIVATING A TEAM IS NOT A SIGNAL TO THE BOARD, and this is the assertion that keeps it that
   * way. If switching the console's team decided what the board fetched, a board deliberately
   * narrowed to one team would silently follow the tab strip somewhere else. The filters are a
   * person's choice and only a person clears them.
   */
  it('leaves the board alone when another team is activated', async () => {
    const consoleStore = useConsoleStore();
    const kanban = useKanbanStore();
    consoleStore.teams = [team(asTeamId('Alpha')), team(asTeamId('Beta'))];

    kanban.openForTeam('Beta');
    await vi.waitFor(() => expect(getKanbanBoard).toHaveBeenCalledWith({ team: 'Beta' }));
    getKanbanBoard.mockClear();

    consoleStore.setActiveTeam(asTeamId('Alpha'));

    expect(kanban.filters).toEqual({ team: 'Beta' });
    expect(getKanbanBoard).not.toHaveBeenCalled();
  });

  it('the Kanban tab keeps the active team, where the Teams tab clears it', () => {
    const board = useConsoleStore();
    const kanban = useKanbanStore();
    board.teams = [team(asTeamId('Alpha'))];
    board.setActiveTeam(asTeamId('Alpha'));

    kanban.showKanban('Alpha');

    expect(kanban.active).toBe(true);
    expect(board.view).toBe('kanban');
    expect(board.activeTeamId).toBe('Alpha');
  });
});

describe('fetching the board', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    useConsoleStore().activeTeamId = Team;
    vi.clearAllMocks();
    getKanbanBoard.mockImplementation(async () => board);
  });

  it('fetches with the current filter', async () => {
    const kanban = useKanbanStore();
    kanban.filters = { team: 'researchkanban' };
    await kanban.load();

    expect(getKanbanBoard).toHaveBeenCalledWith({ team: 'researchkanban' });
    expect(kanban.cardCount).toBe(3);
  });

  /**
   * THE TEAM FILTER IS WHAT DECIDES THE FETCH, AND THE ACTIVE TEAM IS NOT CONSULTED. Stated as one
   * line: choose a different team and the board asks for that team.
   */
  it('asks for the team that was filtered to, not the one the console has active', async () => {
    const kanban = useKanbanStore();
    useConsoleStore().activeTeamId = asTeamId('Alpha');

    kanban.filters = { team: 'otherteam' };
    await kanban.load();

    expect(getKanbanBoard).toHaveBeenCalledWith({ team: 'otherteam' });
  });

  /**
   * NO TEAM FILTER IS THE WHOLE TENANT, and it is a REAL state rather than a refusal. Blanking the
   * board with "No team is active" whenever the console had none would make the per-tenant view
   * unreachable from the one place a person genuinely has no active team.
   */
  it('fetches the whole tenant with no team filter and no active team', async () => {
    const kanban = useKanbanStore();
    useConsoleStore().activeTeamId = '';

    await kanban.load();

    expect(getKanbanBoard).toHaveBeenCalledWith({});
    expect(kanban.error).toBe('');
    expect(kanban.cardCount).toBe(3);
  });

  /**
   * A board fetch is fired by clicks, by the filter bar and by every hub push, so several are
   * routinely in flight - and the slow answer to the OLD filter landing last would otherwise
   * repaint over the fast answer to the new one.
   */
  it('drops a response that arrived after a newer fetch was started', async () => {
    const kanban = useKanbanStore();
    const slow = deferred<KanbanBoard>();

    getKanbanBoard.mockImplementationOnce(() => slow.promise);
    const first = kanban.load();

    const second = { ...board, cards: [card({ id: 'only' })] };
    getKanbanBoard.mockImplementationOnce(async () => second);
    await kanban.load();

    slow.release({ ...board, cards: [] });
    await first;

    expect(kanban.cardCount).toBe(1);
  });

  /**
   * A refetch that failed is a STALE board, and a stale board beside a stated error is more use
   * than an empty one - especially on a board whose whole claim is that it is live.
   */
  it('keeps the board it had when a refetch fails, and says what happened', async () => {
    const kanban = useKanbanStore();
    await kanban.load();

    getKanbanBoard.mockRejectedValueOnce(new Error('500 Internal Server Error'));
    await kanban.load();

    expect(kanban.cardCount).toBe(3);
    expect(kanban.error).toBe('500 Internal Server Error');
  });

  it('clears the error once a fetch succeeds again', async () => {
    const kanban = useKanbanStore();
    getKanbanBoard.mockRejectedValueOnce(new Error('nope'));
    await kanban.load();
    await kanban.load();

    expect(kanban.error).toBe('');
  });

  /**
   * Container activity IS kanban activity - a member starting or reporting progress is a card
   * changing colour and lane. But a push for a tab nobody is looking at is traffic with no reader,
   * and opening the tab loads the board anyway, so there is nothing to catch up on.
   */
  it('ignores a hub push while the tab is not showing', () => {
    const kanban = useKanbanStore();
    kanban.refreshIfActive();

    expect(getKanbanBoard).not.toHaveBeenCalled();
  });

  it('refetches on a hub push while the tab is showing', async () => {
    const kanban = useKanbanStore();
    // `active` reads the console store, which is what "showing" means.
    useConsoleStore().view = 'kanban';
    kanban.refreshIfActive();

    await vi.waitFor(() => expect(getKanbanBoard).toHaveBeenCalled());
  });
});

/**
 * THE STATE A PERSON LANDS ON AFTER A RELOAD.
 *
 * Every other fetch in this store is a REACTION TO A FILTER CHANGING - `showKanban`, `openForTeam`,
 * `setFilters`, `clearFilters` and the two hub paths. A fresh store is `board: null` with no
 * filters, so without a fetch on mount a page restored onto the Kanban tab would ask the server for
 * nothing at all and the board would render its own empty state: "there is no work" where the
 * truth is "nothing has been fetched". The mount is its own path, not a default.
 */
describe('arriving on the board with no click', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    useConsoleStore().activeTeamId = Team;
    vi.clearAllMocks();
    getKanbanBoard.mockImplementation(async () => board);
  });

  /**
   * THE BUG, STATED AS ONE LINE. No filters is the whole tenant, and the tenant-wide board is what
   * a person with nothing selected must see.
   */
  it('fetches the tenant-wide board when the page arrives on the tab with no filters', async () => {
    const kanban = useKanbanStore();
    useConsoleStore().view = 'kanban';

    kanban.loadIfShowing();

    await vi.waitFor(() => expect(getKanbanBoard).toHaveBeenCalledWith({}));
    expect(kanban.hasBoard).toBe(true);
    expect(kanban.cardCount).toBe(3);
    expect(kanban.error).toBe('');
  });

  /**
   * NOT A DEFAULT TEAM FILTER, and this is the assertion that refuses the obvious repair. The
   * console has a team active throughout this file; narrowing the arrival to it would make the
   * tenant-wide board unreachable from the one place a person actually lands, and `showTeamsView`
   * depends on no filter meaning the whole tenant. The SERVER bounds what the caller reaches.
   */
  it('does not narrow itself to the active team', async () => {
    const kanban = useKanbanStore();
    useConsoleStore().view = 'kanban';

    kanban.loadIfShowing();

    await vi.waitFor(() => expect(getKanbanBoard).toHaveBeenCalled());
    expect(getKanbanBoard).toHaveBeenCalledWith({});
    expect(kanban.filters).toEqual({});
  });

  /**
   * A RELOAD THAT ARRIVES FILTERED FETCHES UNDER THAT FILTER. The board on screen must never be
   * narrower than the bar above it says: showing a filtered board that was never asked for, or
   * asking for the whole tenant while the bar reads one team, are the same defect from two sides.
   */
  it('fetches under the filters it arrives holding, rather than clearing them', async () => {
    const kanban = useKanbanStore();
    useConsoleStore().view = 'kanban';
    kanban.filters = { team: 'otherteam', status: 'blocked' };

    kanban.loadIfShowing();

    await vi.waitFor(() =>
      expect(getKanbanBoard).toHaveBeenCalledWith({ team: 'otherteam', status: 'blocked' }),
    );
    expect(kanban.filters).toEqual({ team: 'otherteam', status: 'blocked' });
  });

  /** A page restored onto the container cards has no reader for the board. Clicking the tab loads it. */
  it('fetches nothing when the page arrives on another tab', () => {
    useKanbanStore().loadIfShowing();

    expect(getKanbanBoard).not.toHaveBeenCalled();
  });

  /**
   * IT TAKES A TICKET LIKE EVERY OTHER CALLER. The mount fetch races the first thing a person
   * touches, and it is the one most likely to lose: it starts before the page has finished
   * loading. A slow answer to the empty filter must not repaint over a fast answer to the filter
   * somebody just chose.
   */
  it('never repaints over a newer filter when its own answer is slow', async () => {
    const kanban = useKanbanStore();
    useConsoleStore().view = 'kanban';

    const slow = deferred<KanbanBoard>();
    getKanbanBoard.mockImplementationOnce(() => slow.promise);
    kanban.loadIfShowing();

    const narrowed = { ...board, cards: [card({ id: 'only', member: 'OpsOona' })] };
    getKanbanBoard.mockImplementationOnce(async () => narrowed);
    kanban.setFilters({ member: 'OpsOona' });
    await vi.waitFor(() => expect(kanban.cardCount).toBe(1));

    slow.release(board);
    await vi.waitFor(() => expect(getKanbanBoard).toHaveBeenCalledTimes(2));

    expect(kanban.cardCount).toBe(1);
  });
});

/**
 * THE FREE-TEXT BOX: VIEW STATE THAT NARROWS WHAT IS DRAWN AND NEVER LEAVES THE BROWSER.
 *
 * Client-side by decision: instant, no route change, and it composes with the three
 * filters that do reach the server. It is deliberately NOT a `KanbanFilters` field - a box that
 * never reaches the server, kept in the record that is serialised onto the query string, is one
 * refactor away from being sent - and it is deliberately not a rewrite of `board.cards` either.
 * The board is a derived read model and this store holds no copy of it.
 */
describe('the search box', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    useConsoleStore().activeTeamId = Team;
    vi.clearAllMocks();
    getKanbanBoard.mockImplementation(async () => board);
  });

  /**
   * THE BOX AS ONE LINE: type, and what is on screen is narrower while what was fetched is not.
   * `cardCount`, `lanes` and `laneCards` are what the board draws from, so all three narrow; the
   * fetched array keeps all three cards, ready for the moment the box is cleared again.
   */
  it('narrows the rendered cards without touching the board it fetched', async () => {
    const kanban = useKanbanStore();
    await kanban.load();

    kanban.setText('rosa');

    expect(kanban.cardCount).toBe(1);
    expect(kanban.laneCards('todo').map((entry) => entry.id)).toEqual(['b']);
    expect(kanban.board?.cards).toHaveLength(3);
  });

  /** Cleared, the board is whole again - from the same fetch, with nothing asked for twice. */
  it('draws every card again once the box is emptied', async () => {
    const kanban = useKanbanStore();
    await kanban.load();
    getKanbanBoard.mockClear();

    kanban.setText('rosa');
    kanban.setText('');

    expect(kanban.cardCount).toBe(3);
    expect(getKanbanBoard).not.toHaveBeenCalled();
  });

  /**
   * IT TRIGGERS NO FETCH AT ALL, which is the whole reason it can be instant. Every other control
   * on the bar refetches; this one cannot, because the server has no parameter to receive it.
   */
  it('asks the server for nothing when somebody types', async () => {
    const kanban = useKanbanStore();
    await kanban.load();
    getKanbanBoard.mockClear();

    kanban.setText('rosa');

    expect(getKanbanBoard).not.toHaveBeenCalled();
    expect(kanban.filters).toEqual({});
  });

  /** Case and stray spaces are what a person actually types, and neither is a reason to show nothing. */
  it('matches case-insensitively, on trimmed text', async () => {
    const kanban = useKanbanStore();
    await kanban.load();

    kanban.setText('  ROSA  ');

    expect(kanban.cardCount).toBe(1);
  });

  /**
   * IT COMPOSES WITH THE FILTERS THAT DO REACH THE SERVER: the fetch narrows the board, the box
   * narrows what is drawn from it, and a refetch does not quietly widen the screen back out.
   */
  it('keeps narrowing across a refetch fired by one of the server filters', async () => {
    const kanban = useKanbanStore();
    await kanban.load();
    kanban.setText('rosa');

    kanban.setFilters({ status: 'running' });
    await vi.waitFor(() => expect(getKanbanBoard).toHaveBeenCalledWith({ status: 'running' }));

    expect(kanban.text).toBe('rosa');
    expect(kanban.cardCount).toBe(1);
  });

  /**
   * CLEAR RESETS IT. A box outside `KanbanFilters` still has to be cleared by the one control that
   * says it clears things - otherwise Clear leaves the board narrowed with nothing on screen to
   * say why, which is the missing-cards reading this whole box has to avoid.
   */
  it('is reset by Clear, along with the filters that reach the server', async () => {
    const kanban = useKanbanStore();
    kanban.filters = { team: 'researchkanban' };
    await kanban.load();
    kanban.setText('rosa');

    kanban.clearFilters();

    expect(kanban.text).toBe('');
    expect(kanban.filters).toEqual({});
    await vi.waitFor(() => expect(kanban.cardCount).toBe(3));
  });

  /**
   * AND `openForTeam` CLEARS IT, because that action clears every filter deliberately and
   * unconditionally: a person clicking Kanban on a team's tab strip is saying "show me this team's
   * work", and landing on a board still narrowed by last week's typing reads as the click having
   * done nothing.
   */
  it("is cleared when the board is opened from a team's ribbon", async () => {
    const kanban = useKanbanStore();
    await kanban.load();
    kanban.setText('rosa');

    kanban.openForTeam('researchkanban');

    expect(kanban.text).toBe('');
    expect(kanban.filters).toEqual({ team: 'researchkanban' });
  });

  /** The same act under its other name - the two entry points must not disagree. */
  it('is cleared by the tab button too', async () => {
    const kanban = useKanbanStore();
    await kanban.load();
    kanban.setText('rosa');

    kanban.showKanban('');

    expect(kanban.text).toBe('');
  });
});

describe('filters', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    useConsoleStore().activeTeamId = Team;
    vi.clearAllMocks();
    getKanbanBoard.mockImplementation(async () => board);
  });

  it('refetches when a filter changes', async () => {
    const kanban = useKanbanStore();
    kanban.setFilters({ member: 'ResearcherRosa' });

    await vi.waitFor(() =>
      expect(getKanbanBoard).toHaveBeenCalledWith({ member: 'ResearcherRosa' }),
    );
  });

  it('refetches the whole tenant when the filters are cleared', async () => {
    const kanban = useKanbanStore();
    kanban.filters = { team: 'researchkanban' };
    kanban.clearFilters();

    expect(kanban.filters).toEqual({});
    await vi.waitFor(() => expect(getKanbanBoard).toHaveBeenCalledWith({}));
  });

  it('offers the members and teams that are actually on the board', async () => {
    const kanban = useKanbanStore();
    await kanban.load();

    expect(kanban.memberOptions).toEqual(['DeveloperDorian', 'OpsOona', 'ResearcherRosa']);

    // TWO TEAMS, because the board is tenant-wide: the options are what is on the board rather
    // than what the console has active.
    expect(kanban.teamOptions).toEqual(['otherteam', 'researchkanban']);
  });
});

describe('human edits', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    useConsoleStore().activeTeamId = Team;
    vi.clearAllMocks();
    getKanbanBoard.mockImplementation(async () => board);
  });

  /**
   * NO OPTIMISTIC LOCAL EDIT. The change becomes a message on the log and the projection decides
   * what it means - including `awaitingManager`, which the browser has no business inventing.
   * Refetching is what makes the board on screen the projection's answer rather than a guess.
   */
  it('appends and then refetches, rather than patching the card in place', async () => {
    const kanban = useKanbanStore();
    await kanban.load();
    getKanbanBoard.mockClear();

    await kanban.move('a', 'done', 'by hand');

    expect(moveKanbanCard).toHaveBeenCalledWith(Team, 'a', 'done', 'by hand');
    expect(getKanbanBoard).toHaveBeenCalledTimes(1);
  });

  it('refetches the open card too, so the trail shows what was just appended', async () => {
    const kanban = useKanbanStore();
    await kanban.load();
    await kanban.select('a');
    getKanbanCard.mockClear();

    await kanban.comment('a', 'looks stuck');

    expect(commentKanbanCard).toHaveBeenCalledWith(Team, 'a', 'looks stuck');
    expect(getKanbanCard).toHaveBeenCalledWith(Team, 'a');
  });

  /**
   * A WRITE IS ADDRESSED UNDER THE CARD'S OWN TEAM, NEVER THE ACTIVE ONE.
   *
   * The two are routinely different, because a board holds every team the caller reaches: `otherteam` is what card `c` belongs to, and the
   * console is sitting on `researchkanban` throughout. The active team would 404 a card that is
   * plainly on screen.
   */
  it('addresses each of the three writes with the team the card belongs to', async () => {
    const kanban = useKanbanStore();
    await kanban.load();

    await kanban.move('c', 'done');
    await kanban.edit('c', { title: 'New title' });
    await kanban.comment('c', 'a note');

    expect(moveKanbanCard).toHaveBeenCalledWith('otherteam', 'c', 'done', undefined);
    expect(editKanbanCard).toHaveBeenCalledWith('otherteam', 'c', { title: 'New title' });
    expect(commentKanbanCard).toHaveBeenCalledWith('otherteam', 'c', 'a note');
  });

  /** The caller is a form with a field to put an error beside. It must not be swallowed here. */
  it('raises a refused write to the caller and stops being busy', async () => {
    const kanban = useKanbanStore();
    await kanban.load();
    editKanbanCard.mockRejectedValueOnce(new Error('No such status.'));

    await expect(kanban.edit('a', { status: 'nonsense' })).rejects.toThrow('No such status.');
    expect(kanban.busy).toBe(false);
  });

  /**
   * A card the board does not hold has no team to be addressed under, so the write THROWS rather
   * than returning quietly - falling back to the active team would send the write to whichever
   * team the tab strip was on.
   */
  it('refuses a write against a card the board does not hold', async () => {
    const kanban = useKanbanStore();
    await kanban.load();

    await expect(kanban.move('nobody', 'done')).rejects.toThrow('not on the board');
    expect(moveKanbanCard).not.toHaveBeenCalled();
  });
});

describe('the card panel', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    useConsoleStore().activeTeamId = Team;
    vi.clearAllMocks();
    getKanbanBoard.mockImplementation(async () => board);
  });

  it('opens on the card the board already holds and fetches its trail', async () => {
    const kanban = useKanbanStore();
    await kanban.load();

    const opening = kanban.select('a');
    expect(kanban.selectedCard?.id).toBe('a');

    await opening;
    expect(getKanbanCard).toHaveBeenCalledWith(Team, 'a');
    expect(kanban.detail?.id).toBe('a');
  });

  /** The card panel is addressed under the card's own team too - see the write specs above. */
  it('fetches the trail under the team the card belongs to', async () => {
    const kanban = useKanbanStore();
    await kanban.load();

    await kanban.select('c');

    expect(getKanbanCard).toHaveBeenCalledWith('otherteam', 'c');
  });

  it('states a failed detail fetch rather than showing an empty panel', async () => {
    const kanban = useKanbanStore();
    await kanban.load();
    getKanbanCard.mockRejectedValueOnce(new Error('404 Not Found'));

    await kanban.select('a');

    expect(kanban.detailError).toBe('404 Not Found');
    expect(kanban.detailLoading).toBe(false);
  });

  it('forgets the card and its trail when closed', async () => {
    const kanban = useKanbanStore();
    await kanban.load();
    await kanban.select('a');
    kanban.closeCard();

    expect(kanban.selectedId).toBe('');
    expect(kanban.detail).toBeNull();
  });
});
