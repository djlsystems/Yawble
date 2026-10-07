import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import type { HubConnection } from '@microsoft/signalr';
import type {
  ContainerSnapshot,
  Message,
  Overview,
  Team,
  TeamRollupRow,
  TeamTokenTotals,
  TeamWorkflows,
  TeamWorkflowTiming,
} from '../../api/types';
import { asMemberId, asTeamId, type TeamId } from '../../api/types';

/** A snapshot with sensible defaults, overridden per test. */
const container = (
  overrides: Partial<Omit<ContainerSnapshot, 'id'>> & { id?: string } = {},
): ContainerSnapshot => ({
  team: asTeamId('TestTeam'),
  // Defaults to the id, which is what the server sends for a member nobody gave a separate one -
  // the same rule the `team` fixture below follows for a team's name.
  name: 'Manager',
  agent: 'echo',
  state: 'Idle',
  queueDepth: 0,
  ceiling: 16,
  subscribes: [],
  currentCorrelation: null,
  sinceSeq: 0,
  ...overrides,

  // BRANDED HERE, AT THE FIXTURE'S EDGE, which is what lets every spec go on writing `id: 'Writer'`.
  // A literal in a test genuinely IS an identifier arriving from outside the app, so this is the
  // edge `asMemberId` is for. Wrapping all seventy-three call sites instead would be noise over the
  // one thing the brand exists to catch: a LABEL reaching a route in production code.
  id: asMemberId(overrides.id ?? 'Manager'),
});

/**
 * A team with sensible defaults. Exists so `label` is never written by hand in a fixture: it
 * defaults to the NAME, which is what the server sends for a team nobody has relabelled, and a test
 * that cares about a rename says so by passing one. Fixtures drifting from what the server actually
 * emits is how a blank activity feed once stayed invisible to this whole suite.
 */
const team = (id: TeamId, overrides: Partial<Team> = {}): Team => ({
  id,
  name: id,
  containers: [],
  // The server's own default for a team nobody has repointed - same reasoning as `name` above: a
  // fixture that omits what the server always sends is a fixture the suite cannot catch drift with.
  concierge: 'claude',
  memberAgents: null,
  additionalInstructions: null,
  root: null,
  ...overrides,
});

const overview: Overview = {
  teams: [team(asTeamId('TestTeam'), { containers: [container()] })],
  managerName: asMemberId('Manager'),
};

const message = (seq: number): Message => ({
  seq,
  type: 'agentContainer.completed',
  payload: JSON.stringify({ container: 'TestTeam/Manager', output: `answer ${seq}` }),

  // QUALIFIED, because that is what the server emits. Message.Source and the
  // instruction-type suffix both carry `Team/Name`, and a fixture saying just
  // 'Manager' would keep this suite green against a wire format that does not
  // exist - hiding a blank activity feed.
  source: 'TestTeam/Manager',
  correlationId: seq,
  causationSeq: null,
  depth: 0,
  occurredAt: '2026-08-14T13:57:52Z',
});

/** Resolves only when released, so two pulls can be held in flight at once. */
function deferred<T>() {
  let release!: (value: T) => void;
  const promise = new Promise<T>((resolve) => (release = resolve));
  return { promise, release };
}

const getMessages = vi.fn();
const getOverview = vi.fn(async () => overview);
const emptyTokens: TeamTokenTotals = {
  available: true,
  tokensIn: 0,
  tokensOut: 0,
  tokensCachedIn: 0,
  tokensCacheCreation: 0,
  tokensBillable: 0,
  partial: false,
  runsWithUsage: 0,
  runsWithoutUsage: 0,
  missing: null,
  members: [],
};
const getTeamTokens = vi.fn(async (_team: string) => emptyTokens);
const setCurrentTeam = vi.fn(async (_team: string | null) => new Response(null, { status: 204 }));
const getTeamsRollup = vi.fn(async () => ({ teams: [] as TeamRollupRow[] }));
const getRepoStatus = vi.fn(async (_team: string) => ({
  repos: [],
}));

vi.mock('../../api/client', () => ({
  getOverview: (...args: unknown[]) => getOverview(...(args as [])),
  getMessages: (...args: unknown[]) => getMessages(...(args as [number])),
  getTeamTokens: (...args: unknown[]) => getTeamTokens(...(args as [string])),
  setCurrentTeam: (...args: unknown[]) => setCurrentTeam(...(args as [string | null])),
  getTeamsRollup: (...args: unknown[]) => getTeamsRollup(...(args as [])),
  getRepoStatus: (...args: unknown[]) => getRepoStatus(...(args as [string])),
  publishSteering: (..._args: unknown[]) => undefined,

  // The store checks `cause instanceof Unauthorized` in its catch. Left out of this mock, that
  // line throws a TypeError instead of reporting a signed-out session - and only on the failure
  // path, where it would be least visible.
  Unauthorized: class Unauthorized extends Error {},
}));

// A HubConnection is a real class instance the store never constructs - switchHubTeam only ever
// receives one as a parameter, so the store's own behaviour is fully exercised by mocking the two
// functions it calls, without needing a fake connection object at all.
const joinTeam = vi.fn();
const leaveTeam = vi.fn();

vi.mock('../../lib/hub', () => ({
  joinTeam: (...args: unknown[]) => joinTeam(...(args as [])),
  leaveTeam: (...args: unknown[]) => leaveTeam(...(args as [])),
}));

const storage = new Map<string, string>();
vi.stubGlobal('localStorage', {
  getItem: (key: string) => storage.get(key) ?? null,
  setItem: (key: string, value: string) => {
    storage.set(key, value);
  },
  removeItem: (key: string) => {
    storage.delete(key);
  },
  clear: () => {
    storage.clear();
  },
});

const { useConsoleStore } = await import('../console');

describe('board store', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    localStorage.clear();
    getMessages.mockReset();
    getOverview.mockClear();
    getTeamTokens.mockReset();
    getTeamTokens.mockResolvedValue(emptyTokens);
    getTeamsRollup.mockReset();
    getTeamsRollup.mockResolvedValue({ teams: [] });
    getRepoStatus.mockReset();
    getRepoStatus.mockResolvedValue({ repos: [] });
    joinTeam.mockReset();
    leaveTeam.mockReset();
  });

  /**
   * The board's only sight of the detector's verdict, and what `Resume` is gated on.
   *
   * Absent is an ordinary answer and must read as NO finding — a Host that does not send the
   * field, or one that has simply found nothing, must not leave the button offered on every team
   * as an always-on control.
   */
  describe('live sweep findings', () => {
    it('holds none before the first refresh, so a board that has read nothing offers nothing', () => {
      const board = useConsoleStore();

      expect(board.activeFindings).toEqual([]);
      expect(board.findingKinds('TestTeam')).toEqual([]);
    });

    it('keeps the kinds for each team apart', async () => {
      getMessages.mockResolvedValue([]);
      getOverview.mockResolvedValueOnce({
        ...overview,
        activeFindings: [
          { team: 'TestTeam', kind: 'RunningWithoutProgress' },
          { team: 'TestTeam', kind: 'QuietTeam' },
          { team: 'OtherTeam', kind: 'WrapUpNotPushed' },
        ],
      });

      const board = useConsoleStore();
      await board.refresh();

      expect(board.findingKinds('TestTeam')).toEqual(['RunningWithoutProgress', 'QuietTeam']);
      expect(board.findingKinds('OtherTeam')).toEqual(['WrapUpNotPushed']);
      expect(board.findingKinds('NoSuchTeam')).toEqual([]);
    });

    it('reads a Host that does not send the field as no findings, never as unknown', async () => {
      // Without the field the button must still be gated on the other three clauses rather than
      // on nothing.
      getMessages.mockResolvedValue([]);

      const board = useConsoleStore();
      await board.refresh();

      expect(board.activeFindings).toEqual([]);
    });

    it('forgets findings that have cleared, so a resolved one stops offering the button', async () => {
      getMessages.mockResolvedValue([]);
      getOverview.mockResolvedValueOnce({
        ...overview,
        activeFindings: [{ team: 'TestTeam', kind: 'QuietTeam' }],
      });

      const board = useConsoleStore();
      await board.refresh();
      expect(board.findingKinds('TestTeam')).toEqual(['QuietTeam']);

      await board.refresh();
      expect(board.findingKinds('TestTeam')).toEqual([]);
    });
  });

  describe('the active team', () => {
    it('selects the first team when nothing is selected yet', async () => {
      getMessages.mockResolvedValue([]);

      const board = useConsoleStore();
      await board.refresh();

      expect(board.activeTeamId).toBe('TestTeam');
      expect(board.activeTeam?.containers).toHaveLength(1);
    });

    it('resolves activeWorkTeam and activeWorkTeamId even when wire data still carries reserved: true', () => {
      const board = useConsoleStore();
      const teamWithReservedOnWire = {
        ...team(asTeamId('Alpha')),
        reserved: true,
      } as Team & { reserved: true };

      board.teams = [teamWithReservedOnWire];
      board.setActiveTeam(asTeamId('Alpha'));

      expect(board.activeWorkTeam).not.toBeNull();
      expect(board.activeWorkTeam?.id).toBe('Alpha');
      expect(board.activeWorkTeamId).toBe('Alpha');
    });

    it('keeps a selection that still exists', async () => {
      getMessages.mockResolvedValue([]);
      getOverview.mockResolvedValueOnce({
        ...overview,
        teams: [team(asTeamId('Other')), ...overview.teams],
      });

      const board = useConsoleStore();
      board.setActiveTeam(asTeamId('TestTeam'));
      await board.refresh();

      // Not reset to the first team. A refresh happens on every reconnect, and a selection that
      // moved underneath the person each time would make the switcher useless.
      expect(board.activeTeamId).toBe('TestTeam');
    });

    it('falls back to the first team when the selected one is gone', async () => {
      getMessages.mockResolvedValue([]);

      const board = useConsoleStore();
      board.setActiveTeam(asTeamId('Deleted Team'));
      await board.refresh();

      // Teams live in memory on the server, so a restart empties the list and every remembered
      // name dangles. Left pointing at a team that no longer exists, the board renders nothing -
      // which reads as broken rather than as stale.
      expect(board.activeTeamId).toBe('TestTeam');
    });

    it('setting active team on an open tab switches instead of duplicating', () => {
      const board = useConsoleStore();
      board.teams = [team(asTeamId('Alpha')), team(asTeamId('Beta'))];
      board.reconcileActiveTeam();

      board.setActiveTeam(asTeamId('Alpha'));
      board.setActiveTeam(asTeamId('Beta'));
      board.setActiveTeam(asTeamId('Alpha'));

      expect(board.openTeamTabs).toEqual(['Alpha', 'Beta']);
      expect(board.activeTeamId).toBe('Alpha');
    });

    it('drops a remembered tab that no longer exists on refresh', async () => {
      localStorage.setItem(
        'harness.activeTeam',
        JSON.stringify({ open: ['Missing Team', 'TestTeam'], active: 'Missing Team' }),
      );
      getMessages.mockResolvedValue([]);

      const board = useConsoleStore();
      await board.refresh();

      expect(board.openTeamTabs).toEqual(['TestTeam']);
      expect(board.activeTeamId).toBe('TestTeam');
    });

    it('keeps a selection made before the refresh that will introduce it', async () => {
      getMessages.mockResolvedValue([]);
      getOverview.mockResolvedValueOnce({
        ...overview,
        teams: [...overview.teams, team(asTeamId('Brand New'))],
      });

      const board = useConsoleStore();

      // What creating a team does: select it, THEN refresh. Selecting first is what stops the
      // board painting the previous team for a frame - and reconcile must not undo it just
      // because the name was not in the list it held a moment ago.
      board.setActiveTeam(asTeamId('Brand New'));
      await board.refresh();

      expect(board.activeTeamId).toBe('Brand New');
      expect(board.activeTeam?.name).toBe('Brand New');
    });

    /**
     * A team whose NAME is not its ID, which is every team with a space in it.
     *
     * A fixture built with `team(id)` has a name that defaults to the id, so passing a name where
     * an id belongs works in tests and fails in the product the moment somebody calls a team
     * "Beta Team" - it surfaces as "No team 'Beta Team'."
     */
    it('selects on the id, and a name is not a selection', async () => {
      getOverview.mockResolvedValue({
        teams: [team(asTeamId('BetaTeam'), { name: 'Beta Team' })],
        managerName: asMemberId('Manager'),
      });

      const board = useConsoleStore();

      board.setActiveTeam(asTeamId('BetaTeam'));
      await board.refresh();

      expect(board.activeTeam?.name).toBe('Beta Team');

      // The NAME selects nothing. Rendering falls back to the stored string, which is why a wrong
      // call site looks almost right on screen while every request built from it 404s.
      board.setActiveTeam(asTeamId('Beta Team'));

      expect(board.activeTeam).toBeNull();
    });

    it('has no active team when there are no teams', async () => {
      getMessages.mockResolvedValue([]);
      getOverview.mockResolvedValueOnce({ teams: [], managerName: asMemberId('Manager') });

      const board = useConsoleStore();
      board.setActiveTeam(asTeamId('TestTeam'));
      await board.refresh();

      expect(board.activeTeamId).toBe('');
      expect(board.activeTeam).toBeNull();
    });

    it('reads the label for the heading and keeps the name for addressing', async () => {
      getMessages.mockResolvedValue([]);
      getOverview.mockResolvedValueOnce({
        ...overview,
        teams: [team(asTeamId('Alpha'), { name: 'Platform' })],
      });

      const board = useConsoleStore();
      await board.refresh();

      expect(board.activeTeamLabel).toBe('Platform');

      // The pair is the point. The heading moves and the identifier does not - the console binds to
      // `activeTeamId`, and a rename that moved it would point the console at a team that does
      // not exist.
      expect(board.activeTeamId).toBe('Alpha');
    });

    it('falls back to the name while the team list is empty', () => {
      const board = useConsoleStore();
      board.setActiveTeam(asTeamId('Alpha'));

      // Every frame between a reload and the first /api/overview, and the whole time after a
      // restart has emptied the server's teams. A heading that blanked here would read as "no
      // team" when a team is exactly what is selected.
      expect(board.teams).toHaveLength(0);
      expect(board.activeTeamLabel).toBe('Alpha');
    });
  });

  describe('closeTeamTab', () => {
    const alpha = asTeamId('Alpha');
    const beta = asTeamId('Beta');
    const gamma = asTeamId('Gamma');

    it('closing the active middle tab removes it', () => {
      const board = useConsoleStore();
      board.teams = [team(alpha), team(beta), team(gamma)];
      board.openTeamTabs = [alpha, beta, gamma];
      board.activeTeamId = beta;

      board.closeTeamTab(beta);

      expect(board.openTeamTabs).toEqual([alpha, gamma]);
    });

    it('closing the active tab picks the neighbour at the same index as new active', () => {
      const board = useConsoleStore();
      board.teams = [team(alpha), team(beta), team(gamma)];
      board.openTeamTabs = [alpha, beta, gamma];
      board.activeTeamId = beta;

      board.closeTeamTab(beta);

      expect(board.activeTeamId).toBe(gamma);
    });

    it('closing a non-active tab leaves activeTeamId untouched', () => {
      const board = useConsoleStore();
      board.teams = [team(alpha), team(beta), team(gamma)];
      board.openTeamTabs = [alpha, beta, gamma];
      board.activeTeamId = beta;

      board.closeTeamTab(alpha);

      expect(board.openTeamTabs).toEqual([beta, gamma]);
      expect(board.activeTeamId).toBe(beta);
    });

    /**
     * Closing the last team tab is NOT a no-op. An empty strip would leave no way back to a team,
     * but the Teams tab is permanent, so the strip cannot empty.
     */
    it('closing the last team tab lands on the Teams table with no active team', () => {
      const board = useConsoleStore();
      board.teams = [team(asTeamId('Alpha'))];
      board.openTeamTabs = [asTeamId('Alpha')];
      board.setActiveTeam(asTeamId('Alpha'));

      board.closeTeamTab(asTeamId('Alpha'));

      expect(board.openTeamTabs).toEqual([]);
      expect(board.activeTeamId).toBe('');
      expect(board.view).toBe('teams');
    });

    /**
     * AND THE SERVER IS TOLD. "The browser cleared it" and "the Concierge was moved" are two
     * claims, and only the second matters to the agent: `setActiveTeam` is this store's one writer of
     * the current team, so a `closeTeamTab` that assigned `activeTeamId` by hand would leave the
     * table showing no active team while the agent went on addressing the closed one - silently,
     * on `tell`, whose failure is a 200 and the wrong Manager woken.
     *
     * Asserted on the CLIENT MOCK rather than on state, because state cannot tell the two apart.
     */
    it('tells the server the current team cleared when the last tab closes', async () => {
      const board = useConsoleStore();
      board.teams = [team(asTeamId('Alpha'))];
      board.openTeamTabs = [asTeamId('Alpha')];
      board.setActiveTeam(asTeamId('Alpha'));
      setCurrentTeam.mockClear();

      board.closeTeamTab(asTeamId('Alpha'));
      await Promise.resolve();

      expect(setCurrentTeam).toHaveBeenCalledWith(null);
    });

    /** Closing a tab that was not active moves nothing, so it must say nothing. */
    it('says nothing to the server when the closed tab was not the active one', async () => {
      const board = useConsoleStore();
      board.teams = [team(alpha), team(beta)];
      board.openTeamTabs = [alpha, beta];
      board.setActiveTeam(beta);
      setCurrentTeam.mockClear();

      board.closeTeamTab(alpha);
      await Promise.resolve();

      expect(setCurrentTeam).not.toHaveBeenCalled();
    });

    it('reconcileActiveTeam still repairs an active team that exists but has no tab', () => {
      const board = useConsoleStore();
      board.teams = [team(alpha), team(beta)];
      board.openTeamTabs = [alpha];
      board.activeTeamId = beta;

      board.reconcileActiveTeam();

      expect(board.openTeamTabs).toEqual([alpha, beta]);
      expect(board.activeTeamId).toBe(beta);
    });

    it('reconcileActiveTeam still repopulates an empty tab list when teams exist', () => {
      const board = useConsoleStore();
      board.teams = [team(alpha), team(beta)];
      board.openTeamTabs = [];
      board.activeTeamId = '';

      board.reconcileActiveTeam();

      expect(board.openTeamTabs).toEqual([alpha]);
    });
  });

  /**
   * Duplicate rows, in one test.
   *
   * A container publishes a snapshot at least three times per instruction - on enqueue, on Running,
   * on Idle - and every push pulls messages. A pull that reads `lastSeq`, awaits a fetch, and only
   * advances the cursor once the response lands lets overlapping pulls fetch the same messages and
   * record them twice: every row paired, same text and same second.
   */
  it('records a message once even when two pulls overlap on the same cursor', async () => {
    const first = deferred<Message[]>();
    const second = deferred<Message[]>();

    getMessages.mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise);

    const board = useConsoleStore();

    const a = board.pullMessages();
    const b = board.pullMessages();

    // Both saw the same cursor, so both are handed the same batch.
    first.release([message(1), message(2)]);
    second.release([message(1), message(2)]);
    await Promise.all([a, b]);

    expect(board.activityFor('TestTeam', 'Manager').map((m) => m.seq)).toEqual([2, 1]);
  });

  it('advances the cursor so the next pull asks only for what is new', async () => {
    getMessages.mockResolvedValueOnce([message(1), message(2)]).mockResolvedValueOnce([]);

    const board = useConsoleStore();

    await board.pullMessages();
    expect(board.lastSeq).toBe(2);

    await board.pullMessages();
    // The cursor is what this spec is about; the second argument is the viewer's fetch window,
    // which comes from the display store and is irrelevant here.
    expect(getMessages).toHaveBeenLastCalledWith(2, expect.anything());
  });

  it('keeps the newest first', async () => {
    getMessages.mockResolvedValueOnce([message(1), message(2), message(3)]);

    const board = useConsoleStore();
    await board.pullMessages();

    expect(board.activityFor('TestTeam', 'Manager').map((m) => m.seq)).toEqual([3, 2, 1]);
  });

  /**
   * The permanently-blank feed, in one test.
   *
   * `ownerOf` buckets under the qualified `Team/Name`, because that is what the server puts in
   * Message.Source and in the instruction-type suffix. A card asking for a bare `Manager` gets
   * `[]` - every feed on the board, forever, with nothing failing anywhere.
   */
  it('files a card feed under the pair, not the bare name', async () => {
    getMessages.mockResolvedValueOnce([message(1)]);

    const board = useConsoleStore();
    await board.pullMessages();

    expect(board.activityFor('TestTeam', 'Manager').map((m) => m.seq)).toEqual([1]);

    // The bare name is not a bucket. Asserted so a getter that "helpfully" fell back to a name
    // lookup could not make this test pass while leaving two teams sharing one feed.
    expect(board.activityFor('', 'Manager')).toEqual([]);
  });

  /**
   * The other half of the pair. Two teams each hold a "Manager", and their feeds must not be one
   * feed - the same rule the qualified container identity enforces, on the activity side.
   */
  it('keeps two teams managers feeds apart', async () => {
    const alpha: Message = { ...message(1), source: 'Alpha/Manager' };
    const beta: Message = { ...message(2), source: 'Beta/Manager' };

    getMessages.mockResolvedValueOnce([alpha, beta]);

    const board = useConsoleStore();
    await board.pullMessages();

    expect(board.activityFor('Alpha', 'Manager').map((m) => m.seq)).toEqual([1]);
    expect(board.activityFor('Beta', 'Manager').map((m) => m.seq)).toEqual([2]);
  });

  /** An addressed instruction is filed on its ADDRESSEE's card, and the addressee is qualified. */
  it('files an addressed instruction under the qualified addressee', async () => {
    const instruction: Message = {
      ...message(1),
      type: 'agentContainer.instruction.Alpha/dev1',
      source: 'console',
    };

    getMessages.mockResolvedValueOnce([instruction]);

    const board = useConsoleStore();
    await board.pullMessages();

    expect(board.activityFor('Alpha', 'dev1').map((m) => m.seq)).toEqual([1]);
  });

  /**
   * THE END-TO-END HALF OF `ownerOf`'S TEAM RULE, and the reason that rule exists.
   *
   * `teamActivity` is what `teamLogFacts` reads to answer "is this workflow still open" for the
   * TAB CHIP. A close published by a person carries a bare user id as its source, so without the
   * team arm it lands in a bucket keyed `u-17` that this getter's `Team/` prefix never matches -
   * the chip goes on calling a closed workflow open while the Teams table beside it says Closed.
   *
   * AND NO CARD READS IT, asserted here rather than left implied: `activityFor` composes
   * `Team/Name` and a container name can never be empty, so a person's act reaches the team's
   * history without appearing on any member's feed.
   */
  it("puts a person's close in the team's history and on nobody's card", async () => {
    const closed: Message = {
      ...message(1),
      type: 'workflow.closed',
      payload: JSON.stringify({ team: 'Alpha', reason: 'nothing left to do' }),
      source: 'u-17',
    };

    getMessages.mockResolvedValueOnce([closed]);

    const board = useConsoleStore();
    await board.pullMessages();

    expect(board.teamActivity('Alpha').map((m) => m.seq)).toEqual([1]);
    expect(board.activityFor('Alpha', 'Manager')).toEqual([]);
  });

  /**
   * A container's identity is the PAIR (team, name), not the name alone. Two teams can each hold
   * a "Manager" - matching by name only finds the first hit across every team and lands the
   * snapshot on the wrong card.
   */
  it('applies a snapshot to the team it belongs to', () => {
    const board = useConsoleStore();
    board.teams = [
      team(asTeamId('Alpha'), { containers: [container({ team: asTeamId('Alpha'), id: 'Manager', queueDepth: 0 })] }),
      team(asTeamId('Beta'), { containers: [container({ team: asTeamId('Beta'), id: 'Manager', queueDepth: 0 })] }),
    ];

    board.applySnapshot(container({ team: asTeamId('Beta'), id: 'Manager', queueDepth: 3 }));

    expect(board.teams[0]!.containers[0]!.queueDepth).toBe(0);
    expect(board.teams[1]!.containers[0]!.queueDepth).toBe(3);
  });

  it('updates a background team from a snapshot while another tab is active', () => {
    const board = useConsoleStore();
    board.teams = [
      team(asTeamId('Alpha'), { containers: [container({ team: asTeamId('Alpha'), id: 'Manager', queueDepth: 0 })] }),
      team(asTeamId('Beta'), { containers: [container({ team: asTeamId('Beta'), id: 'Manager', queueDepth: 0 })] }),
    ];
    board.setActiveTeam(asTeamId('Alpha'));

    board.applySnapshot(container({ team: asTeamId('Beta'), id: 'Manager', queueDepth: 4 }));

    expect(board.activeTeamId).toBe('Alpha');
    expect(board.teams[1]!.containers[0]!.queueDepth).toBe(4);
  });

  it('keeps every team choosable while still updating snapshots', () => {
    const board = useConsoleStore();
    board.teams = [
      team(asTeamId('TenantCore'), {
        name: 'TenantCore',
        containers: [container({ team: asTeamId('TenantCore'), id: 'Auditor', queueDepth: 0 })],
      }),
      team(asTeamId('Alpha'), { containers: [container({ team: asTeamId('Alpha'), id: 'Manager', queueDepth: 0 })] }),
    ];
    board.reconcileActiveTeam();

    expect(board.choosableTeams.map((row) => row.id)).toEqual(['TenantCore', 'Alpha']);
    expect(board.openTeams.map((row) => row.id)).toEqual(['TenantCore']);

    board.applySnapshot(container({ team: asTeamId('TenantCore'), id: 'Auditor', queueDepth: 2 }));
    expect(board.teams[0]!.containers[0]!.queueDepth).toBe(2);
  });

  /**
   * The unbounded-reload-loop guard (per-team SignalR groups).
   *
   * With group routing the hub should never deliver a snapshot for a team this client does not
   * hold - but a miss branch that called `refresh()` unconditionally for ANY unrecognised team,
   * where `refresh()` fetches both /api/overview and /api/messages, would turn a single stray
   * snapshot for a team not in `board.teams` into an unbounded reload loop the moment it arrived
   * twice. It must be dropped instead.
   */
  it('drops a snapshot for a team it does not hold, rather than reloading', async () => {
    const board = useConsoleStore();
    board.teams = [team(asTeamId('Alpha'), { containers: [container({ team: asTeamId('Alpha') })] })];

    board.applySnapshot(container({ team: asTeamId('Ghost Team'), id: 'Manager', queueDepth: 9 }));

    // No reload was triggered: getOverview is only ever called from refresh(), so a call here
    // proves the miss branch ran an unconditional refresh().
    expect(getOverview).not.toHaveBeenCalled();

    // And nothing was fabricated onto a team this client does not hold.
    expect(board.teams).toHaveLength(1);
    expect(board.teams[0]!.containers[0]!.queueDepth).toBe(0);
  });

  describe('token totals', () => {
    beforeEach(() => {
      getOverview.mockReset();
      getOverview.mockResolvedValue(overview);
      getMessages.mockResolvedValue([]);
    });

    it('loads a team total from the log endpoint on refresh, not from the activity slice', async () => {
      getTeamTokens.mockResolvedValue({
        ...emptyTokens,
        tokensIn: 40,
        tokensOut: 8,
        runsWithUsage: 2,
        members: [{ member: 'Manager', brand: 'echo', tokensIn: 40, tokensOut: 8 }],
      });

      const board = useConsoleStore();
      await board.refresh();

      expect(getTeamTokens).toHaveBeenCalledWith(board.activeTeamId);
      expect(board.tokenUsage?.tokensIn).toBe(40);
      expect(board.tokenUsage?.tokensOut).toBe(8);
      expect(board.tokenUsageTeam).toBe(board.activeTeamId);
    });

    it('refetches the log total when a snapshot lands, rather than summing retained messages', async () => {
      getMessages.mockResolvedValue([]);
      const board = useConsoleStore();
      await board.refresh();

      let released!: () => void;
      const fetched = new Promise<void>((resolve) => {
        released = resolve;
      });
      getTeamTokens.mockImplementation(async () => {
        released();
        return emptyTokens;
      });

      board.applySnapshot({ ...board.teams[0]!.containers[0]!, state: 'Idle' });
      await fetched;

      expect(getTeamTokens).toHaveBeenCalledWith(board.activeTeamId);
    });
  });

  describe('switchHubTeam', () => {
    // A HubConnection is never constructed here - switchHubTeam only forwards it to joinTeam and
    // leaveTeam, both mocked above, so any object stands in for it.
    const connection = {} as HubConnection;

    it('joins the requested team', async () => {
      joinTeam.mockResolvedValue(undefined);

      const board = useConsoleStore();
      await board.switchHubTeam(connection, 'Beta');

      expect(joinTeam).toHaveBeenCalledWith(connection, 'Beta');
      expect(leaveTeam).not.toHaveBeenCalled();
      expect(board.error).toBe('');
    });

    it('leaves only the tab that closes', async () => {
      leaveTeam.mockResolvedValue(undefined);

      const board = useConsoleStore();
      await board.leaveHubTeam(connection, 'Beta');

      expect(leaveTeam).toHaveBeenCalledWith(connection, 'Beta');
      expect(leaveTeam).not.toHaveBeenCalledWith(connection, 'Alpha');
    });

    /**
     * The Task 5 review's Important finding: a failed JoinTeam invoke has no self-healing retry -
     * unlike a dropped `containerChanged` push, which the next change repairs, nothing re-attempts
     * a failed join until the person switches teams again or the connection reconnects. Silently
     * dropping it (the `pullMessages` precedent this store originally reached for) would leave the
     * board reporting "live" while never updating for the team just switched to - indistinguishable
     * from a team where nothing is happening, which is the exact symptom per-team groups exist to
     * remove. It must reach `board.error`.
     */
    it('surfaces a failed join as a board error', async () => {
      joinTeam.mockRejectedValueOnce(new Error('socket dropped mid-invoke'));
      getOverview.mockResolvedValue({
        ...overview,
        teams: [team(asTeamId('Beta'), { name: 'Platform' })],
      });

      const board = useConsoleStore();
      board.teams = [team(asTeamId('Beta'), { name: 'Platform' })];
      await board.switchHubTeam(connection, 'Beta');

      expect(board.error).not.toBe('');

      // Not the raw exception: HubException("No such team.") would read as "you don't have this
      // team", which is misleading for a connection that already passed that exact check once to
      // get here - the message names the actual failure mode instead.
      expect(board.error).toContain('Live updates are unavailable for Platform');
    });
  });

  /**
   * The current team, which is ONE fact with one owner - the server - and this store is its single
   * client-side writer.
   */
  describe('the current team', () => {
    beforeEach(() => setCurrentTeam.mockClear());

    it('tells the server on every tab switch', () => {
      const board = useConsoleStore();
      board.teams = [team(asTeamId('Alpha')), team(asTeamId('Beta'))];

      board.setActiveTeam(asTeamId('Alpha'));
      board.setActiveTeam(asTeamId('Beta'));

      expect(setCurrentTeam.mock.calls.map(([id]) => id)).toEqual(['Alpha', 'Beta']);
    });

    /**
     * The Teams overview's state. '' is sent as null rather than skipped: no team active is
     * something the server stores, not an absence it should keep the previous answer for.
     */
    it('sends null when no team is active', () => {
      const board = useConsoleStore();
      board.teams = [team(asTeamId('Alpha'))];
      board.setActiveTeam(asTeamId('Alpha'));
      setCurrentTeam.mockClear();

      board.setActiveTeam('');

      expect(setCurrentTeam).toHaveBeenCalledWith(null);
    });

    it('does not write when the team did not actually change', () => {
      const board = useConsoleStore();
      board.teams = [team(asTeamId('Alpha'))];
      board.setActiveTeam(asTeamId('Alpha'));
      setCurrentTeam.mockClear();

      board.setActiveTeam(asTeamId('Alpha'));

      expect(setCurrentTeam).not.toHaveBeenCalled();
    });

    it('follows a switch that arrived from somewhere else', () => {
      const board = useConsoleStore();
      board.teams = [team(asTeamId('Alpha')), team(asTeamId('Beta'))];
      board.overviewLanded = true; // Teams have been loaded from the overview
      board.setActiveTeam(asTeamId('Alpha'));

      board.applyCurrentTeam('Beta');

      expect(board.activeTeamId).toBe('Beta');
    });

    /**
     * EVERY CLICK WRITES AND EVERY WRITE ECHOES BACK, so the echo must not write again - a click
     * would otherwise write, hear itself, and write once more, and two devices could ping-pong a
     * team between them.
     *
     * What makes it true is `setActiveTeam`'s own no-change check rather than a second condition in
     * `applyCurrentTeam`; this test names the echo so that moving that check breaks a test about
     * the echo and not only tests about tab switching.
     */
    it('ignores the echo of its own write', () => {
      const board = useConsoleStore();
      board.teams = [team(asTeamId('Alpha'))];
      board.overviewLanded = true; // Teams have been loaded from the overview
      board.setActiveTeam(asTeamId('Alpha'));
      setCurrentTeam.mockClear();

      board.applyCurrentTeam('Alpha');

      expect(setCurrentTeam).not.toHaveBeenCalled();
    });

    /**
     * The other device is welcome to be somewhere this one is not. Making it active would put an id
     * in the tab strip with no row behind it, which renders as a tab that cannot be opened.
     */
    it('ignores a team this browser cannot see', () => {
      const board = useConsoleStore();
      board.teams = [team(asTeamId('Alpha'))];
      board.overviewLanded = true; // Teams have been loaded from the overview
      board.setActiveTeam(asTeamId('Alpha'));

      board.applyCurrentTeam('SomewhereElse');

      expect(board.activeTeamId).toBe('Alpha');
    });

    /**
     * Step 2 - When the team list loads with the announced team, the announcement is applied.
     *
     * applyCurrentTeam stores the announcement when teams is empty, and applies it when the list
     * loads.
     */
    it('applies a held announcement once the teams list loads', () => {
      const board = useConsoleStore();
      board.setActiveTeam(asTeamId('Alpha'));
      board.teams = []; // List has not loaded yet
      board.overviewLanded = false; // Ensure teams not loaded yet
      setCurrentTeam.mockClear();

      // Announcement arrives before list loads
      board.applyCurrentTeam('Beta');

      // Immediately after, team is still Alpha (announcement held, not applied)
      expect(board.activeTeamId).toBe('Alpha');
      // Verify the announcement was held in pendingCurrentTeam
      expect(board.pendingCurrentTeam).toBe('Beta');

      // Now the teams list loads with Beta in it
      board.teams = [team(asTeamId('Beta'))];
      board.overviewLanded = true;
      
      // Simulate what happens when the overview lands - apply pending announcements
      board.applyPendingCurrentTeam();

      // The held announcement is applied now
      expect(board.activeTeamId).toBe('Beta');
      // Verify the pending announcement was cleared
      expect(board.pendingCurrentTeam).toBeNull();
      // Also verify the server was told about the change
      expect(setCurrentTeam).toHaveBeenCalledWith(asTeamId('Beta'));
    });

    /**
     * A held announcement is REPLACED by a later one, not queued behind it.
     *
     * The current team is a fact, not a stream, and the newest one is the only true one.
     */
    it('replaces a held announcement with a newer one', () => {
      const board = useConsoleStore();
      board.setActiveTeam(asTeamId('Alpha'));
      board.teams = [];
      board.overviewLanded = false; // Teams not loaded yet
      setCurrentTeam.mockClear();

      // First announcement arrives: team Beta
      board.applyCurrentTeam('Beta');
      expect(board.pendingCurrentTeam).toBe('Beta');

      // Second announcement arrives before list loads: team Gamma (replaces Beta)
      board.applyCurrentTeam('Gamma');
      expect(board.pendingCurrentTeam).toBe('Gamma'); // Gamma replaced Beta, not queued
      expect(board.activeTeamId).toBe('Alpha'); // Still Alpha, nothing applied yet

      // Now the teams list loads - should apply only Gamma
      board.teams = [team(asTeamId('Gamma'))];
      board.overviewLanded = true;
      board.applyPendingCurrentTeam();

      expect(board.activeTeamId).toBe('Gamma'); // Only the latest one is applied
      expect(board.pendingCurrentTeam).toBeNull();
    });

    /**
     * A held announcement naming a team the list turns out not to contain is dropped
     * when the list arrives, not applied.
     */
    it('drops a held announcement when the announced team is not in the loaded list', () => {
      const board = useConsoleStore();
      board.setActiveTeam(asTeamId('Alpha'));
      board.teams = [];
      board.overviewLanded = false; // Teams not loaded yet
      setCurrentTeam.mockClear();

      // Announcement arrives for team Beta before list loads
      board.applyCurrentTeam('Beta');
      expect(board.pendingCurrentTeam).toBe('Beta');
      expect(board.activeTeamId).toBe('Alpha'); // Still Alpha, not applied yet

      // Teams list loads, but Beta is not in it (doesn't exist on this browser)
      board.teams = [team(asTeamId('Alpha')), team(asTeamId('Gamma'))];
      board.overviewLanded = true;
      board.applyPendingCurrentTeam();

      // Announcement should be dropped, not applied
      expect(board.activeTeamId).toBe('Alpha'); // Stays Alpha
      expect(board.pendingCurrentTeam).toBeNull(); // Pending cleared
      expect(setCurrentTeam).not.toHaveBeenCalled(); // Server not notified
    });

    /**
     * The reconnect window is covered by its own case, not only the startup one.
     *
     * During reconnect, refresh() fetches new teams. If an announcement arrives while the old
     * teams are still in place (before new teams are loaded), it follows the same logic as with
     * loaded teams - either applied or dropped based on whether it's in the list.
     *
     * This test verifies that after reconnect when new teams are loaded and a pending
     * announcement from before exists, it's applied if the team is in the new list.
     */
    it('applies pending announcement after reconnect when team appears in new list', () => {
      const board = useConsoleStore();
      board.setActiveTeam(asTeamId('Alpha'));
      board.teams = [team(asTeamId('Alpha'))];
      board.overviewLanded = true; // Initial load completed
      board.pendingCurrentTeam = 'Beta'; // Simulate announcement that was held during disconnect
      setCurrentTeam.mockClear();

      // Reconnect happens - refresh() fetches new teams that now include Beta
      board.teams = [team(asTeamId('Alpha')), team(asTeamId('Beta'))];
      // The pending announcement is applied when teams are available
      board.applyPendingCurrentTeam();

      expect(board.activeTeamId).toBe('Beta'); // Applied from pending
      expect(board.pendingCurrentTeam).toBeNull(); // Pending cleared
      expect(setCurrentTeam).toHaveBeenCalledWith(asTeamId('Beta'));
    });

    /**
     * RECONNECT WINDOW, WITHOUT A REFRESH IN FLIGHT
     *
     * After the first overview loads,
     * overviewLanded is true. When refresh() is called during reconnect, if an announcement
     * arrives WHILE refresh() is in flight (teams list is stale, not updated yet), the 
     * announcement is checked against the old list.
     *
     * If the announced team is not in the stale list, it's dropped (like the startup case).
     * But unlike startup, there's no pending holding mechanism because overviewLanded is already true.
     *
     * This test verifies that case: the announced team is NOT in the stale list and no refresh is
     * marked in flight, so it's dropped instead of held.
     */
    it('drops announcement when it arrives during reconnect while teams list is stale', () => {
      const board = useConsoleStore();
      board.setActiveTeam(asTeamId('Alpha'));
      board.teams = [team(asTeamId('Alpha'))]; // Stale list - doesn't have Beta yet
      board.overviewLanded = true; // First load completed - so NOT held by the startup guard
      setCurrentTeam.mockClear();

      // Announcement arrives while refresh() is in flight (teams still stale)
      board.applyCurrentTeam('Beta');

      // activeTeamId stays Alpha, and nothing is held for later
      expect(board.activeTeamId).toBe('Alpha'); // Announcement dropped
      expect(board.pendingCurrentTeam).toBeNull(); // Nothing held for reconnect
    });

    /**
     * RECONNECT WINDOW, WITH A REFRESH IN FLIGHT
     *
     * When an announcement arrives during refresh (stale list, overviewLanded true), it is held.
     * When the new list arrives, applyPendingCurrentTeam applies it.
     */
    it('holds announcement that arrives during reconnect refresh when list is stale', () => {
      const board = useConsoleStore();
      board.setActiveTeam(asTeamId('Alpha'));
      board.teams = [team(asTeamId('Alpha'))]; // Stale list
      board.overviewLanded = true; // First load completed
      board.isRefreshing = true; // Signal that we're in the middle of refresh
      setCurrentTeam.mockClear();

      // Announcement arrives while refresh is in flight
      board.applyCurrentTeam('Beta');

      // The announcement is held, not dropped
      expect(board.pendingCurrentTeam).toBe('Beta'); // Held for reconnect
      expect(board.activeTeamId).toBe('Alpha'); // Not applied yet

      // Simulate refresh completing and new list arriving
      board.isRefreshing = false;
      board.teams = [team(asTeamId('Alpha')), team(asTeamId('Beta'))];
      board.applyPendingCurrentTeam();

      // Should apply the held announcement
      expect(board.activeTeamId).toBe('Beta');
      expect(board.pendingCurrentTeam).toBeNull();
      expect(setCurrentTeam).toHaveBeenCalledWith(asTeamId('Beta'));
    });
  });

  describe('team create/delete pushes', () => {
    it('refreshes when a newly visible team is announced', async () => {
      const board = useConsoleStore();
      board.teams = [team(asTeamId('Alpha'))];

      const refreshed = vi.spyOn(board, 'refresh').mockResolvedValue(undefined as never);
      const rerolled = vi.spyOn(board, 'refreshRollupIfShowing').mockImplementation(() => {});

      await board.refreshForTeamCreated('Beta');

      expect(refreshed).toHaveBeenCalled();
      expect(rerolled).toHaveBeenCalled();
    });

    it('ignores the echo of a created team it already holds', async () => {
      const board = useConsoleStore();
      board.teams = [team(asTeamId('Alpha')), team(asTeamId('Beta'))];

      const refreshed = vi.spyOn(board, 'refresh').mockResolvedValue(undefined as never);
      const rerolled = vi.spyOn(board, 'refreshRollupIfShowing').mockImplementation(() => {});

      await board.refreshForTeamCreated('Beta');

      expect(refreshed).not.toHaveBeenCalled();
      expect(rerolled).not.toHaveBeenCalled();
    });

    it('refreshes when a visible team is deleted elsewhere', async () => {
      const board = useConsoleStore();
      board.teams = [team(asTeamId('Alpha')), team(asTeamId('Beta'))];

      const refreshed = vi.spyOn(board, 'refresh').mockResolvedValue(undefined as never);
      const rerolled = vi.spyOn(board, 'refreshRollupIfShowing').mockImplementation(() => {});

      await board.refreshForTeamDeleted('Alpha');

      expect(refreshed).toHaveBeenCalled();
      expect(rerolled).toHaveBeenCalled();
    });

    it('ignores the echo of a deleted team it already dropped', async () => {
      const board = useConsoleStore();
      board.teams = [team(asTeamId('Beta'))];

      const refreshed = vi.spyOn(board, 'refresh').mockResolvedValue(undefined as never);
      const rerolled = vi.spyOn(board, 'refreshRollupIfShowing').mockImplementation(() => {});

      await board.refreshForTeamDeleted('Alpha');

      expect(refreshed).not.toHaveBeenCalled();
      expect(rerolled).not.toHaveBeenCalled();
    });
  });

  describe('the view', () => {
    it('starts on the board', () => {
      const board = useConsoleStore();

      expect(board.view).toBe('board');
    });

    /** Selecting Teams clears the active team - unlike Kanban, which deliberately keeps it. */
    /**
   * Teams created through the API must appear without a HARD REFRESH. `pullRollup` answers what
   * each team is doing; the LIST comes from `/api/overview`, and nothing else re-reads it while the
   * console is open - so without this a team made in another browser, by the CLI, or by a clone
   * would be invisible.
   */
  it('re-reads the team list when the Teams view is shown, not only the timings', async () => {
    const board = useConsoleStore();

    getOverview.mockClear();
    board.showTeamsView();
    await Promise.resolve();

    expect(getOverview).toHaveBeenCalled();
  });

  it('clears the active team when the Teams view is shown', () => {
      const board = useConsoleStore();
      board.teams = [team(asTeamId('Alpha'))];
      board.setActiveTeam(asTeamId('Alpha'));

      board.showTeamsView();

      expect(board.view).toBe('teams');
      expect(board.activeTeamId).toBe('');
    });

    /**
     * THE RELOAD TRAP. `reconcileActiveTeam` forces an active team whenever none is set, and
     * that write PUTs to the server and moves the Concierge with it - so a reload from the Teams
     * view would silently land on a team. It must not while the Teams view is the one showing.
     */
    it('does not force an active team while the Teams view is showing', () => {
      const board = useConsoleStore();
      board.teams = [team(asTeamId('Alpha')), team(asTeamId('Beta'))];
      board.showTeamsView();

      board.reconcileActiveTeam();

      expect(board.activeTeamId).toBe('');
    });

    it('still falls back to a surviving tab while the board is showing', async () => {
      const board = useConsoleStore();
      board.teams = [team(asTeamId('Alpha'))];
      board.openTeamTabs = [asTeamId('Alpha')];
      board.activeTeamId = '';
      setCurrentTeam.mockClear();

      board.reconcileActiveTeam();
      await Promise.resolve();

      expect(board.activeTeamId).toBe('Alpha');

      // AND THE SERVER IS TOLD. A fallback that moved the browser's active team and said nothing
      // would leave `harness team current` answering "no team is active" while the strip showed
      // Alpha with its `team-` ribbon actions enabled.
      // State alone cannot tell "it fell back" from "it fell back and told nobody".
      expect(setCurrentTeam).toHaveBeenCalledWith('Alpha');
    });

    /**
     * THE REPRODUCTION, and it is a two-click path rather than an exotic one.
     *
     * Click Teams: the active team clears and the server is told null. Click Kanban: the VIEW
     * moves and the active team deliberately does not. Now anything that reconciles - a reload, or
     * any `board.refresh()` off a push - takes the `view !== 'teams'` arm, because the guard is on
     * the Teams view alone and Kanban is not it. The arm picks `openTeamTabs[0]`.
     *
     * Clicking the Alpha tab cannot repair it either: `setActiveTeam('Alpha')` sees no change
     * and no-ops, so the disagreement outlives every obvious attempt to fix it.
     */
    it('tells the server when it falls back with the kanban board showing', async () => {
      const board = useConsoleStore();
      board.teams = [team(asTeamId('Alpha'))];
      board.openTeamTabs = [asTeamId('Alpha')];
      board.showTeamsView();

      // What `kanban.showKanban` does to this store: the view moves, the active team does not.
      board.view = 'kanban';
      setCurrentTeam.mockClear();

      board.reconcileActiveTeam();
      await Promise.resolve();

      expect(board.activeTeamId).toBe('Alpha');
      expect(setCurrentTeam).toHaveBeenCalledWith('Alpha');
    });

    /**
     * THE OTHER ARM OF THE SAME METHOD. An instance whose last team was deleted clears the active
     * team, and that is a move like any other - assigning it by hand left the Concierge addressing
     * a team that no longer exists.
     */
    it('tells the server when the last team goes and the active team clears', async () => {
      getMessages.mockResolvedValue([]);

      const board = useConsoleStore();
      board.teams = [team(asTeamId('Alpha'))];
      board.openTeamTabs = [asTeamId('Alpha')];
      board.setActiveTeam(asTeamId('Alpha'));
      setCurrentTeam.mockClear();

      // THROUGH `refresh`, not by assigning `teams` and reconciling. The SERVER answering with no
      // teams is what this arm is for, and that answer is now the thing separating it from a fetch
      // that has not landed - so a test that skips the answer is testing the other case.
      getOverview.mockResolvedValueOnce({ teams: [], managerName: asMemberId('Manager') });
      await board.refresh();
      await Promise.resolve();

      expect(board.activeTeamId).toBe('');
      expect(board.openTeamTabs).toEqual([]);
      expect(setCurrentTeam).toHaveBeenCalledWith(null);
    });

    /**
     * AND THE OTHER HALF OF THAT ARM, WHICH WAS A LIVE DEFECT.
     *
     * `teams` is empty before the first `/api/overview` lands as well as after a restart really
     * emptied the server, and the arm above does not merely decline to pick a team: it CLEARS THE
     * TAB STRIP AND PERSISTS IT. Observed twice in a browser pass - the stored blob read
     * `open: []` while the store still held both tabs, so the next reload opened with no team tabs
     * at all.
     *
     * The strip is asserted in MEMORY and in STORAGE, because they are two claims and only the
     * second one is what a reload reads.
     */
    it('does not clear the tab strip before the first overview has landed', () => {
      localStorage.setItem(
        'harness.activeTeam',
        JSON.stringify({ open: ['Alpha', 'Beta'], active: 'Alpha', view: 'board' }),
      );

      const board = useConsoleStore();

      // No `refresh`: this is the state a page is in for as long as the first fetch is in flight.
      board.reconcileActiveTeam();

      expect(board.openTeamTabs).toEqual(['Alpha', 'Beta']);
      expect(board.activeTeamId).toBe('Alpha');
      expect(JSON.parse(localStorage.getItem('harness.activeTeam')!).open)
        .toEqual(['Alpha', 'Beta']);
    });

    it('remembers the view across a reload', () => {
      const board = useConsoleStore();
      board.teams = [team(asTeamId('Alpha'))];
      board.showTeamsView();

      expect(JSON.parse(localStorage.getItem('harness.activeTeam')!).view).toBe('teams');
    });
  });

  describe('the rollup', () => {
    /** A timing with sensible defaults - mirrors the `timing()` fixture in teamKpis.spec.ts. This
     *  file cannot import that one: it is a fixture, not a rule, so the literal is copied here. */
    const workflowTimingFixture = (overrides: Partial<TeamWorkflowTiming> = {}): TeamWorkflowTiming => ({
      available: true,
      correlation: 12,
      state: 'Running',
      startedAt: '2026-09-07T09:00:00Z',
      endedAt: null,
      serverNow: '2026-09-07T09:30:00Z',
      executionSeconds: 60,
      partial: false,
      runsCounted: 1,
      runsUnfinished: 0,
      blockedBy: null,
      runsFailed: 0,
      failedMembers: [],
      missing: null,
      members: [{ member: 'Manager', runs: 1, executionSeconds: 60, unfinished: 0 }],
      lastActivityAt: '2026-09-07T09:10:00Z',
      awaitingFrom: null,
      subject: null,
      pausedAt: null,
      pausedReason: null,
      pausedLimit: null,
      ...overrides,
    });

    /** The plural sibling of the fixture above - mirrors `workflows()` in teamsTable.spec.ts, and
     *  cannot import it for the same reason: a fixture, not a rule. */
    const openWorkflowsFixture = (overrides: Partial<TeamWorkflows> = {}): TeamWorkflows => ({
      available: true,
      openCount: 1,

      // OPEN AND CLOSED ALIKE, uncapped - the field the dialog's truncation line counts against.
      // `openCount` still means OPEN.
      totalCount: 1,
      earliestStartedAt: '2026-09-07T09:00:00Z',
      serverNow: '2026-09-07T09:30:00Z',
      workflows: [workflowTimingFixture()],
      missing: null,
      ...overrides,
    });

    it('keys each team\'s timing by team id', async () => {
      const board = useConsoleStore();
      getTeamsRollup.mockResolvedValue({
        teams: [{ team: 'Alpha', workflow: workflowTimingFixture(), openWorkflows: openWorkflowsFixture() }],
      });

      await board.pullRollup();

      expect(board.workflowFor('Alpha')?.available).toBe(true);
    });

    it('answers null for a team it has no row for', () => {
      expect(useConsoleStore().workflowFor('Nobody')).toBeNull();
    });

    /**
     * THE PLURAL PROJECTION, FOR A TEAM THAT WAS NEVER ACTIVE. This is the whole point of feeding
     * `openWorkflows` from the rollup rather than only from `pullWorkflows` (which never runs for a
     * team nobody has switched to): without it, the Teams table's Workflows column and the tab chip
     * are blank for every team but the one a person happens to have made active.
     */
    it('also keys each team\'s OPEN workflows by team id, for a team never made active', async () => {
      const board = useConsoleStore();
      getTeamsRollup.mockResolvedValue({
        teams: [{
          team: 'Alpha',
          workflow: workflowTimingFixture(),
          openWorkflows: openWorkflowsFixture({ openCount: 3 }),
        }],
      });

      await board.pullRollup();

      expect(board.workflowsFor('Alpha')?.openCount).toBe(3);
    });

    /** A slow answer must not overwrite a newer one - the board fires this on every hub push. This
     *  covers BOTH maps `pullRollup` writes, since one guard protects them both. */
    it('drops an answer that landed after a newer fetch', async () => {
      const board = useConsoleStore();
      let releaseFirst: (value: { teams: TeamRollupRow[] }) => void = () => {};
      getTeamsRollup
        .mockImplementationOnce(() => new Promise((resolve) => (releaseFirst = resolve)))
        .mockResolvedValueOnce({
          teams: [{
            team: 'Beta',
            workflow: workflowTimingFixture(),
            openWorkflows: openWorkflowsFixture(),
          }],
        });

      const slow = board.pullRollup();
      await board.pullRollup();
      releaseFirst({
        teams: [{ team: 'Alpha', workflow: workflowTimingFixture(), openWorkflows: openWorkflowsFixture() }],
      });
      await slow;

      expect(board.workflowFor('Alpha')).toBeNull();
      expect(board.workflowFor('Beta')).not.toBeNull();
      expect(board.workflowsFor('Alpha')).toBeNull();
      expect(board.workflowsFor('Beta')).not.toBeNull();
    });
  });

  /**
   * `toggleWorkflow` and `showWorkflow` write the same field and must not be confused for one
   * another - a "Show thread" button calling `toggleWorkflow` would clear the highlight on an
   * ALREADY-followed workflow instead of (re-)showing it, the opposite of the button's own label.
   */
  describe('following a workflow', () => {
    it('toggleWorkflow flips: clicking the one already followed clears it', () => {
      const board = useConsoleStore();

      board.toggleWorkflow(4);
      expect(board.highlightedWorkflow).toBe(4);

      board.toggleWorkflow(4);
      expect(board.highlightedWorkflow).toBeNull();
    });

    it('toggleWorkflow switches to a different workflow rather than clearing it', () => {
      const board = useConsoleStore();

      board.toggleWorkflow(4);
      board.toggleWorkflow(11);
      expect(board.highlightedWorkflow).toBe(11);
    });

    it('showWorkflow sets outright and never clears an already-followed one', () => {
      const board = useConsoleStore();

      board.showWorkflow(4);
      expect(board.highlightedWorkflow).toBe(4);

      // THE ASSERTION THIS TEST EXISTS FOR: a second call naming the SAME workflow must still
      // read as followed - a "Show thread" click on the one already shown must not turn it off.
      board.showWorkflow(4);
      expect(board.highlightedWorkflow).toBe(4);
    });

    it('showWorkflow moves the highlight to whatever it is called with', () => {
      const board = useConsoleStore();

      board.showWorkflow(4);
      board.showWorkflow(11);
      expect(board.highlightedWorkflow).toBe(11);
    });
  });

  describe('pullRepoStatus on setActiveTeam', () => {
    beforeEach(() => {
      getMessages.mockResolvedValue([]);
      getOverview.mockResolvedValue(overview);
    });

    it('calls pullRepoStatus when setActiveTeam changes the active team', async () => {
      const board = useConsoleStore();
      board.teams = [team(asTeamId('Alpha')), team(asTeamId('Beta'))];

      board.setActiveTeam(asTeamId('Alpha'));
      await Promise.resolve();

      expect(getRepoStatus).toHaveBeenCalledWith('Alpha');
      expect(board.repoStatusTeam).toBe('Alpha');
    });

    it('does not call pullRepoStatus when setActiveTeam is called with the same team', async () => {
      getRepoStatus.mockClear();
      const board = useConsoleStore();
      board.teams = [team(asTeamId('Alpha'))];
      board.setActiveTeam(asTeamId('Alpha'));
      await Promise.resolve();

      getRepoStatus.mockClear();
      board.setActiveTeam(asTeamId('Alpha'));
      await Promise.resolve();

      expect(getRepoStatus).not.toHaveBeenCalled();
    });

    it('clears repoStatus when setActiveTeam is called with an empty team id', async () => {
      const board = useConsoleStore();
      board.teams = [team(asTeamId('Alpha'))];
      board.setActiveTeam(asTeamId('Alpha'));
      await Promise.resolve();

      expect(board.repoStatus).not.toBeNull();

      board.setActiveTeam('');
      await Promise.resolve();

      expect(board.repoStatus).toBeNull();
      expect(board.repoStatusTeam).toBe('');
    });
  });

  /**
   * THE TEAMS TABLE IS LIVE FOR EVERY ROW IT SHOWS, not only for the teams that happen to have a
   * tab. Its Members and Status cells are the pushed snapshots and its Workflows and Last workflow
   * cells are re-read on each push, while the `2 running` beside them is the polled WIP ledger - so
   * a row whose team's group was never joined froze at whatever the last overview said while its
   * ledger line kept moving: IDLE beside "2 running", Members 1 when the team had two.
   */
  describe('which teams are live', () => {
    it('is every team while the Teams table is showing, tab or no tab', () => {
      const board = useConsoleStore();
      board.teams = [team(asTeamId('Alpha')), team(asTeamId('Beta'))];
      board.openTeamTabs = [];

      board.showTeamsView();

      expect(board.liveTeamIds).toEqual(['Alpha', 'Beta']);
    });

    it('is the open tabs on a team board, so a closed tab stops pushing', () => {
      const board = useConsoleStore();
      board.teams = [team(asTeamId('Alpha')), team(asTeamId('Beta'))];
      board.openTeamTabs = [asTeamId('Alpha')];
      board.view = 'board';

      expect(board.liveTeamIds).toEqual(['Alpha']);
    });
  });

  /**
   * AN ARCHIVED TEAM IS KEPT AND OUT OF THE WAY: still in `teams`, so the Teams list, Delete and
   * Clone find it, and never offered by the chooser or given a tab.
   */
  describe('archived teams', () => {
    const alpha = asTeamId('Alpha');
    const beta = asTeamId('Beta');

    it('are left out of the choosable teams and still held in teams', () => {
      const board = useConsoleStore();
      board.teams = [team(alpha), team(beta, { archived: true })];

      expect(board.choosableTeams.map((entry) => entry.id)).toEqual([alpha]);
      expect(board.teams.map((entry) => entry.id)).toEqual([alpha, beta]);
    });

    it('lose their tab when the overview marks them archived', () => {
      const board = useConsoleStore();
      board.overviewLanded = true;
      board.teams = [team(alpha), team(beta, { archived: true })];
      board.openTeamTabs = [alpha, beta];
      board.activeTeamId = alpha;
      board.view = 'board';

      board.reconcileActiveTeam();

      expect(board.openTeamTabs).toEqual([alpha]);
      expect(board.openTeams.map((entry) => entry.id)).toEqual([alpha]);
    });

    it('get no tab back even while one is the active team', () => {
      const board = useConsoleStore();
      board.overviewLanded = true;
      board.teams = [team(alpha), team(beta, { archived: true })];
      board.openTeamTabs = [alpha];
      board.activeTeamId = beta;
      board.view = 'board';

      board.reconcileActiveTeam();

      expect(board.openTeamTabs).toEqual([alpha]);
      expect(board.activeTeamId).toBe(alpha);
    });
  });
});
