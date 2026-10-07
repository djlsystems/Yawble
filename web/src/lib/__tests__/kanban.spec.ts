import { describe, expect, it } from 'vitest';
import type { KanbanBoard, KanbanCard, KanbanTrailEntry } from '../../api/kanban';
import type { Outcome } from '../../api/outcomes';
import {
  boardCountText,
  outcomeTeams,
  proposedMatchingFilters,
  cardShowsWaiting,
  laneOverLimit,
  swimlaneTeams,
  KanbanStatusColour,
  UnplacedLaneId,
  activeFilterCount,
  bodyToRender,
  cardsInLane,
  cardsMatchingText,
  colourFor,
  defaultKanbanFilters,
  effectiveFilters,
  lanesToRender,
  membersOf,
  sameFilters,
  teamsOf,
  trailRows,
  withFilters,
} from '../kanban';

const card = (overrides: Partial<KanbanCard> & Pick<KanbanCard, 'id'>): KanbanCard => ({
  workflowSeq: 1,
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

const board = (cards: KanbanCard[], lanes = ['todo', 'in-progress', 'done']): KanbanBoard => ({
  
  lanes: lanes.map((id) => ({ id, title: id })),
  cards,
});

describe('card colour', () => {
  /** The template owns the palette, so a colour the server sent is the colour that is drawn. */
  it('takes the server colour when this build can draw it', () => {
    expect(colourFor({ color: 'teal', status: 'running' })).toBe('teal');
  });

  /**
   * A word with no class behind it would render as a card with no colour at all and nothing
   * failing - the silent-drift shape the ribbon parser exists to avoid, one layer over.
   */
  it('falls back to the status when the colour word is one it cannot draw', () => {
    expect(colourFor({ color: 'chartreuse', status: 'blocked' })).toBe('amber');
  });

  it('falls back to the status when no colour was sent at all', () => {
    expect(colourFor({ color: null, status: 'failed' })).toBe('red');
    expect(colourFor({ status: 'done' })).toBe('green');
  });

  /** The palette the design brief locks. Pinned so a well-meant tidy cannot quietly repaint it. */
  it('keeps the locked status palette', () => {
    expect(KanbanStatusColour).toEqual({
      queued: 'slate',
      running: 'blue',
      blocked: 'amber',
      'needs-decision': 'orange',
      failed: 'red',

      // `handback` shares `completed`'s word rather than minting one: the palette is locked
      // and `KanbanColours` has a class per word, so a new colour would render a colourless
      // card. What tells a hand-back from a completion is the status and its label.
      handback: 'teal',
      completed: 'teal',
      done: 'green',
    });
  });

  /**
   * A HAND-BACK IS NOT PAINTED LIKE TROUBLE. The point of the verb is that a member which
   * FINISHED is not rendered as one that gave up, and colour is the first thing a person reads
   * off a board - so this is the palette half of that claim, held here beside the lock above.
   */
  it('does not paint a handback like a give-up', () => {
    expect(KanbanStatusColour.handback).not.toBe(KanbanStatusColour.blocked);
    expect(KanbanStatusColour.handback).not.toBe(KanbanStatusColour.failed);
    expect(KanbanStatusColour.handback).not.toBe(KanbanStatusColour['needs-decision']);
  });
});

describe('the default filter', () => {
  /** The board opens on the team you were looking at. That is the whole rule. */
  it('is the active team', () => {
    expect(defaultKanbanFilters('researchkanban')).toEqual({ team: 'researchkanban' });
  });

  /**
   * ONE spelling of "unset". `{ team: '' }` would be sent as `team=` and answered with no cards -
   * an empty board that looks exactly like a tenant with no work in it.
   */
  it('is empty rather than a team of empty string when nothing is active', () => {
    expect(defaultKanbanFilters('')).toEqual({});
  });
});

describe('filter updates', () => {
  it('drops a filter that was cleared rather than keeping it as an empty string', () => {
    expect(withFilters({ team: 'alpha', member: 'Dorian' }, { member: '' })).toEqual({
      team: 'alpha',
    });
  });

  it('drops a filter cleared as undefined, which is what a q-select emits', () => {
    expect(withFilters({ team: 'alpha' }, { team: undefined })).toEqual({});
  });

  it('leaves the original untouched, so a failed fetch cannot half-apply a change', () => {
    const before = { team: 'alpha' };
    withFilters(before, { member: 'Rosa' });

    expect(before).toEqual({ team: 'alpha' });
  });

  it('counts only the filters that are actually narrowing the board', () => {
    expect(activeFilterCount({ team: 'alpha', member: '', status: 'running' })).toBe(2);
    expect(activeFilterCount({})).toBe(0);
  });

  /**
   * THE SEARCH BOX IS COUNTED TOO, THOUGH IT IS NOT ONE OF THESE FILTERS.
   *
   * It lives outside `KanbanFilters` on purpose - it never reaches the server - but it narrows
   * what is on screen exactly as they do. Left out of the count, Clear disappears while the board
   * is still narrowed by a box somebody typed in and forgot, and the missing cards have nothing on
   * screen to explain them.
   */
  it('counts the search box, which narrows the board without being a filter', () => {
    expect(activeFilterCount({ team: 'alpha' }, 'rosa')).toBe(2);
    expect(activeFilterCount({}, 'rosa')).toBe(1);
  });

  /** Whitespace is nothing typed, which is the same rule the matcher applies. */
  it('does not count a search box holding only whitespace', () => {
    expect(activeFilterCount({}, '   ')).toBe(0);
    expect(activeFilterCount({}, '')).toBe(0);
    expect(activeFilterCount({})).toBe(0);
  });
});

/**
 * THE FREE-TEXT BOX, WHICH NARROWS WHAT IS RENDERED AND NOTHING ELSE.
 *
 * Client-side by decision: it is instant, it changes no route, and it composes with the three
 * filters that do reach the server. It is therefore the one narrowing in this feature that no
 * `harness kanban board` invocation can reproduce - which is why it is a pure function here,
 * with the fields it searches named in one place both it and the screen read.
 */
describe('the search box', () => {
  const cards = [
    card({ id: 'alpha-1', title: 'Ribbon action rewired', member: 'DeveloperDelia' }),
    card({
      id: 'beta-2',
      title: 'Board fetches on mount',
      body: 'The refresh lands on an empty board.',
      team: 'otherteam',
      member: null,
      status: 'blocked',
      item: 42,
    }),
  ];

  /** Nothing typed narrows nothing. The board is what the server answered with. */
  it('returns every card when nothing has been typed', () => {
    expect(cardsMatchingText(cards, '').map((entry) => entry.id)).toEqual(['alpha-1', 'beta-2']);
    expect(cardsMatchingText(cards, '   ').map((entry) => entry.id)).toEqual(['alpha-1', 'beta-2']);
  });

  it('matches the title, anywhere in it and in any case', () => {
    expect(cardsMatchingText(cards, 'RIBBON').map((entry) => entry.id)).toEqual(['alpha-1']);
    expect(cardsMatchingText(cards, 'on mount').map((entry) => entry.id)).toEqual(['beta-2']);
  });

  /** The body is half the instruction, and the half a person is likeliest to remember a phrase of. */
  it('matches the body', () => {
    expect(cardsMatchingText(cards, 'empty board').map((entry) => entry.id)).toEqual(['beta-2']);
  });

  it('matches the member, the team, the item, the status and the id', () => {
    expect(cardsMatchingText(cards, 'delia').map((entry) => entry.id)).toEqual(['alpha-1']);
    expect(cardsMatchingText(cards, 'otherteam').map((entry) => entry.id)).toEqual(['beta-2']);
    expect(cardsMatchingText(cards, '42').map((entry) => entry.id)).toEqual(['beta-2']);
    expect(cardsMatchingText(cards, 'blocked').map((entry) => entry.id)).toEqual(['beta-2']);
    expect(cardsMatchingText(cards, 'alpha-1').map((entry) => entry.id)).toEqual(['alpha-1']);
  });

  /** Typed with a stray space either side, which is what a paste gives you. */
  it('trims what was typed before matching', () => {
    expect(cardsMatchingText(cards, '  ribbon  ').map((entry) => entry.id)).toEqual(['alpha-1']);
  });

  it('answers an empty list rather than everything when nothing matches', () => {
    expect(cardsMatchingText(cards, 'nothing like this')).toEqual([]);
  });

  /**
   * AN UNASSIGNED CARD IS NOT A CARD WHOSE MEMBER IS THE WORD "NULL". A null member contributes no
   * text, the way it contributes no filter option - and the alternative is a search for `null`
   * quietly listing every unclaimed card.
   */
  it('reads no member text off a card nobody is assigned to', () => {
    expect(cardsMatchingText(cards, 'null')).toEqual([]);
  });

  /**
   * THE CARDS HANDED IN ARE NOT TOUCHED. The board is a derived read model and a second source of
   * truth for team work is the one thing the design brief forbids outright - so this narrows a
   * copy and the fetched array keeps every card it was answered with.
   */
  it('leaves the cards it was given exactly as they were', () => {
    const held = [...cards];
    cardsMatchingText(held, 'ribbon');

    expect(held).toHaveLength(2);
    expect(held.map((entry) => entry.id)).toEqual(['alpha-1', 'beta-2']);
  });

});

describe('lanes', () => {
  it('draws the template lanes, in the template order', () => {
    expect(lanesToRender(board([card({ id: 'a' })])).map((lane) => lane.id)).toEqual([
      'todo',
      'in-progress',
      'done',
    ]);
  });

  /**
   * A card in a lane the template does not have would otherwise be fetched, held and never drawn -
   * present in the payload, absent from the screen, and impossible to notice.
   */
  it('adds a home for a card whose lane the template does not have', () => {
    const lanes = lanesToRender(board([card({ id: 'a', laneId: 'nowhere' })]));

    expect(lanes.map((lane) => lane.id)).toContain('unplaced');
    expect(cardsInLane(board([card({ id: 'a', laneId: 'nowhere' })]), 'unplaced')).toHaveLength(
      1,
    );
  });

  /**
   * Pinned as a literal, not through the constant: an assertion that reads the value it is
   * checking passes for any value at all, including a NUL-prefixed id.
   * The id travels in DOM attributes and in move requests, so it has to stay plain ASCII.
   */
  it('names that lane with a plain ASCII id', () => {
    expect(UnplacedLaneId).toBe('unplaced');
  });

  it('adds no such lane when every card fits', () => {
    expect(lanesToRender(board([card({ id: 'a' })])).map((lane) => lane.id)).not.toContain(
      'unplaced',
    );
  });
});

describe('cards in a lane', () => {
  it('takes only the cards that lane holds', () => {
    const subject = board([
      card({ id: 'a', laneId: 'todo' }),
      card({ id: 'b', laneId: 'in-progress' }),
    ]);

    expect(cardsInLane(subject, 'todo').map((found) => found.id)).toEqual(['a']);
  });

  it('puts the most recently moved card first', () => {
    const subject = board([
      card({ id: 'old', updatedAt: '2026-09-06T09:00:00Z' }),
      card({ id: 'new', updatedAt: '2026-09-06T12:00:00Z' }),
    ]);

    expect(cardsInLane(subject, 'in-progress').map((found) => found.id)).toEqual(['new', 'old']);
  });

  /**
   * A list that reorders itself on every refetch reads, under a FLIP animation, as cards flying
   * about for no reason. Two cards touched in the same second must land the same way every time.
   */
  it('breaks a tie by id, so the order does not churn between refetches', () => {
    const subject = board([card({ id: 'b' }), card({ id: 'a' })]);

    expect(cardsInLane(subject, 'in-progress').map((found) => found.id)).toEqual(['a', 'b']);
  });

  it('does not mutate the board it was given', () => {
    const cards = [card({ id: 'b' }), card({ id: 'a' })];
    cardsInLane(board(cards), 'in-progress');

    expect(cards.map((found) => found.id)).toEqual(['b', 'a']);
  });
});

describe('filter options taken from the board', () => {
  it('lists each member once, sorted', () => {
    const subject = board([
      card({ id: 'a', member: 'Rosa' }),
      card({ id: 'b', member: 'Dorian' }),
      card({ id: 'c', member: 'Rosa' }),
    ]);

    expect(membersOf(subject)).toEqual(['Dorian', 'Rosa']);
  });

  it('lists each team once, sorted', () => {
    const subject = board([card({ id: 'a', team: 'beta' }), card({ id: 'b', team: 'alpha' })]);

    expect(teamsOf(subject)).toEqual(['alpha', 'beta']);
  });
});

describe('the rows a card panel draws under Trail', () => {
  const entry = (
    seq: number,
    type: string,
    text: string | null,
    payload?: string,
  ): KanbanTrailEntry => ({
    seq,
    type,
    source: 'alpha/Scout',
    occurredAt: '2026-09-08T10:00:00Z',
    text,
    ...(payload === undefined ? {} : { payload }),
  });

  /**
   * THE SERVER'S TRAIL IS THE NORMAL CASE. A panel that rendered only progress lines would show a
   * comment and a note - which are not progress lines - nowhere at all.
   */
  it('draws the trail the server sent, in the order it sent it', () => {
    const rows = trailRows(
      {
        trail: [
          entry(1, 'agentContainer.instruction.alpha/Scout', 'Fix the remaining holes'),
          entry(4, 'kanban.card.commented', 'why is this still queued?'),
        ],
      },
      card({ id: 'a', progress: [{ at: '2026-09-08T09:00:00Z', text: 'a progress line' }] }),
    );

    expect(rows.map((row) => row.text)).toEqual([
      'Fix the remaining holes',
      'why is this still queued?',
    ]);

    expect(rows.every((row) => row.fallback)).toBe(false);
  });

  /**
   * THE FALLBACK IS FOR OLD DATA AND IS NOW THE EXCEPTION. It stays because a card that narrated
   * its run should not read as a card with no history when the server answers no trail - but a
   * fallback that fires on every fetch is how the real thing stayed missing for so long.
   */
  it('falls back to the board card progress lines when the server sent no trail', () => {
    const rows = trailRows(
      {},
      card({
        id: 'a',
        progress: [
          { at: '2026-09-08T09:00:00Z', text: 'cloned the repository' },
          { at: '2026-09-08T09:05:00Z', text: 'ran the suite' },
        ],
      }),
    );

    expect(rows.map((row) => row.text)).toEqual(['cloned the repository', 'ran the suite']);
    expect(rows.every((row) => row.fallback)).toBe(true);
    expect(rows.every((row) => row.type === 'agentContainer.progress')).toBe(true);
  });

  it('falls back for an EMPTY trail too', () => {
    const rows = trailRows(
      { trail: [] },
      card({ id: 'a', progress: [{ at: '2026-09-08T09:00:00Z', text: 'ran the suite' }] }),
    );

    expect(rows.map((row) => row.text)).toEqual(['ran the suite']);
  });

  it('has nothing to draw when there is neither', () => {
    expect(trailRows(null, card({ id: 'a' }))).toEqual([]);
    expect(trailRows(undefined, undefined)).toEqual([]);
  });

  /** A row whose message carried no prose renders its payload rather than an empty line. */
  it('shows the payload when a row has no text of its own', () => {
    const rows = trailRows({ trail: [entry(2, 'agentContainer.started', '', '{"trigger":"tell"}')] }, undefined);

    expect(rows[0]?.text).toBe('{"trigger":"tell"}');
  });

  it('renders an empty row rather than the word undefined when a message carried neither', () => {
    const rows = trailRows({ trail: [entry(2, 'agentContainer.started', null)] }, undefined);

    expect(rows[0]?.text).toBe('');
  });

  /** Keys have to differ or Vue reuses a row's DOM node for a different message. */
  it('gives every row a key of its own', () => {
    const rows = trailRows(
      { trail: [entry(1, 'agentContainer.progress', 'one'), entry(2, 'agentContainer.progress', 'two')] },
      undefined,
    );

    expect(new Set(rows.map((row) => row.key)).size).toBe(2);
  });
});

/**
 * The description under a card's title.
 *
 * A card carries a body, and a panel that bound only the title would fetch half of every
 * instruction, hold it in the store, and show it to nobody. What is being pinned here is the DECISION - whether there is anything to draw, and in what shape - which
 * is why it is a function rather than a `v-if`.
 */
describe('the body a card panel draws', () => {
  it('is the body, verbatim, when the card has one', () => {
    expect(bodyToRender(card({ id: 'a', body: 'Check the changelog first.' }))).toBe(
      'Check the changelog first.',
    );
  });

  /**
   * NOT TRIMMED, NOT REFLOWED, NOT COLLAPSED. An instruction's list and its command examples are
   * shaped on purpose - the server keeps that shape through the split and has a test saying so -
   * so the last hop before the DOM must not be the one that flattens it. The renderer's job is
   * `white-space: pre-wrap`; this function's job is to hand it the characters unchanged.
   */
  it('keeps the newlines and the indentation the instruction was written with', () => {
    const body = '  - clone main\n  - run the tests\n\n    dotnet test --no-build';

    expect(bodyToRender(card({ id: 'a', body }))).toBe(body);
  });

  /**
   * NOTHING AT ALL, rather than an empty section under the title. A heading over nothing reads as
   * a body that failed to load, which is the very thing this feature is fixing.
   */
  it('is empty for a card whose whole instruction fitted in its title', () => {
    expect(bodyToRender(card({ id: 'a', body: '' }))).toBe('');
  });

  /** Whitespace is nothing to read, however many characters it is. */
  it('is empty for a body that holds only whitespace', () => {
    expect(bodyToRender(card({ id: 'a', body: '  \n\n\t ' }))).toBe('');
  });

  /**
   * The panel renders the board's copy of a card until the detail lands, and a host may
   * answer a card with no `body` field at all. Neither is a reason to render the word undefined.
   */
  it('is empty when there is no card, or a card from a host that sends no body', () => {
    expect(bodyToRender(null)).toBe('');
    expect(bodyToRender(undefined)).toBe('');
    expect(bodyToRender({} as KanbanCard)).toBe('');
  });
});

describe('swimlanes and limits', () => {
  it('keeps every console team, adds a team only a card names, and narrows by the team filter', () => {
    const teams = [{ id: 'a', name: 'A' }, { id: 'b', name: 'B' }];

    expect(swimlaneTeams(teams, [{ team: 'c' }, { team: 'a' }]).map((t) => t.id)).toEqual(['a', 'b', 'c']);
    expect(swimlaneTeams(teams, [], 'b').map((t) => t.id)).toEqual(['b']);
  });

  it('is over an advisory limit only when there is one and the count exceeds it', () => {
    expect(laneOverLimit(3, 2)).toBe(true);
    expect(laneOverLimit(2, 2)).toBe(false);
    expect(laneOverLimit(9, null)).toBe(false);
    expect(laneOverLimit(9, 0)).toBe(false);
  });

  it('shows the waiting mark only on live work', () => {
    expect(cardShowsWaiting({ status: 'queued' }, true)).toBe(true);
    expect(cardShowsWaiting({ status: 'running' }, true)).toBe(true);
    expect(cardShowsWaiting({ status: 'done' }, true)).toBe(false);
    expect(cardShowsWaiting({ status: 'queued' }, false)).toBe(false);
  });
});

/**
 * SWIMLANES SHOW EVERY TEAM: the team filter is kept but not applied there, so the filters
 * in effect - fetched and counted - omit it. Board keeps it. Member and status apply in both.
 */
describe('the filters in effect for a layout', () => {
  const filters = { team: 'alpha', member: 'Dev1', status: 'running' as const }

  it('omits the team for Swimlanes and keeps the rest', () => {
    expect(effectiveFilters(filters, 'swimlanes')).toEqual({ member: 'Dev1', status: 'running' })
  })

  it('keeps the team for Board', () => {
    expect(effectiveFilters(filters, 'board')).toEqual(filters)
  })

  it('leaves the kept filters untouched', () => {
    const kept = { team: 'alpha' }
    effectiveFilters(kept, 'swimlanes')

    expect(kept).toEqual({ team: 'alpha' })
  })

  it('counts without the kept team in Swimlanes and with it on Board', () => {
    expect(activeFilterCount(effectiveFilters({ team: 'alpha' }, 'swimlanes'))).toBe(0)
    expect(activeFilterCount(effectiveFilters({ team: 'alpha' }, 'board'))).toBe(1)
  })

  it('compares two filter sets by what they narrow', () => {
    expect(sameFilters({ team: 'alpha' }, { team: 'alpha' })).toBe(true)
    expect(sameFilters({ team: 'alpha', member: '' }, { team: 'alpha' })).toBe(true)
    expect(sameFilters({ team: 'alpha' }, {})).toBe(false)
    expect(sameFilters({ team: 'alpha' }, { team: 'beta' })).toBe(false)
  })
})

describe('the proposed outcomes Needs You draws', () => {
  const proposal = (id: string, createdBy: string, teams: string[] = [], createdByKind = 'member') =>
    ({
      id,
      name: `Outcome ${id}`,
      status: 'proposed',
      createdBy,
      createdByKind,
      figures: { teams: teams.map((team) => ({ id: team, name: team, deleted: false })) },
    }) as unknown as Outcome;

  const outcomes = [
    proposal('mine', 'quick-notes/Manager', ['quick-notes']),
    proposal('theirs', 'other-team/Manager'),
    proposal('linked', 'other-team/Manager', ['Quick-Notes']),
    proposal('concierge', 'concierge', [], 'concierge'),
  ];
  const ids = (list: Outcome[]) => list.map((outcome) => outcome.id);

  it('belongs to the proposer\'s team and to every team whose workflows it serves', () => {
    expect(outcomeTeams(outcomes[2]!).sort()).toEqual(['other-team', 'quick-notes']);
    expect(outcomeTeams(outcomes[3]!)).toEqual([]);
  });

  it('keeps every one with no filter', () => {
    expect(ids(proposedMatchingFilters(outcomes, {}))).toEqual(['mine', 'theirs', 'linked', 'concierge']);
  });

  it('keeps those of any picked team, ignoring case', () => {
    expect(ids(proposedMatchingFilters(outcomes, { team: 'QUICK-NOTES' }))).toEqual(['mine', 'linked']);
    expect(ids(proposedMatchingFilters(outcomes, { team: 'quick-notes,other-team' }))).toEqual(['mine', 'theirs', 'linked']);
  });

  it('keeps those a picked member proposed', () => {
    expect(ids(proposedMatchingFilters(outcomes, { member: 'manager' }))).toEqual(['mine', 'theirs', 'linked']);
    expect(ids(proposedMatchingFilters(outcomes, { member: 'Dev1' }))).toEqual([]);
  });

  it('keeps none under a status filter: a status is a card\'s', () => {
    expect(proposedMatchingFilters(outcomes, { status: 'blocked' })).toEqual([]);
  });

  it('keeps the picked outcomes, and none for No outcome', () => {
    expect(ids(proposedMatchingFilters(outcomes, { outcome: 'theirs' }))).toEqual(['theirs']);
    expect(proposedMatchingFilters(outcomes, { outcome: 'none' })).toEqual([]);
  });

  it('is narrowed by the search box as the cards are', () => {
    expect(ids(proposedMatchingFilters(outcomes, {}, ' outcome MINE '))).toEqual(['mine']);
  });
});

describe('the board header\'s count', () => {
  it('names the cards, and the proposed outcomes when there are any', () => {
    expect(boardCountText(1, 0)).toBe('1 card');
    expect(boardCountText(0, 0)).toBe('0 cards');
    expect(boardCountText(1, 1)).toBe('1 card, 1 proposed outcome');
    expect(boardCountText(3, 2)).toBe('3 cards, 2 proposed outcomes');
  });
});
