// @vitest-environment happy-dom
//
// THE BACKLOG SCREEN, MOUNTED CLOSED AND THEN OPENED.
//
// It loads from `watch(open)`, which fires on a TRANSITION and not on a first render that happens to
// be true - so mounting with `modelValue: true` would skip the load entirely and the table would
// come up empty, which reads as "the allowlist filtered everything out". `mountDialog` is the
// harness built for that.
//
// WHAT THESE CASES CAN SEE THAT A SOURCE SCAN CANNOT: that DELETE is absent from the Backlog tab and
// present on the Archived one. `lib/backlog.ts` has no opinion about it - it is a template
// condition - and a grep for the symbol passes over the defect, because the button is in the file
// either way. The control is not there at all until the item is archived, rather than one delete
// control behaving differently depending on whether the item had been dispatched.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, type VueWrapper } from '@vue/test-utils';

const {
  backlogItems, backlogItem, listCatalog, fileSystemRoots, dispatchBacklogItemToNewTeam,
  updateBacklogItem,
} = vi.hoisted(() => ({
  backlogItems: vi.fn(),
  backlogItem: vi.fn(),
  listCatalog: vi.fn(),
  fileSystemRoots: vi.fn(),
  dispatchBacklogItemToNewTeam: vi.fn(),
  updateBacklogItem: vi.fn(),
}));

vi.mock('quasar', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  useQuasar: () => ({ notify: vi.fn() }),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  backlogItems,
  backlogItem,
  listCatalog,
  fileSystemRoots,
  dispatchBacklogItemToNewTeam,
  updateBacklogItem,
}));

import BacklogDialog from '../BacklogDialog.vue';
import { useConsoleStore } from '../../stores/console';
import { useSessionStore } from '../../stores/session';
import { bodyText, mountDialog, resetBody } from '../../test/mountQuasar';

function item(over: Record<string, unknown> & { id: number }) {
  return {
    team: null,
    teamName: null,
    teamGone: false,
    title: `item ${over.id}`,
    body: '',
    state: 'pending',
    archivedAt: null,
    createdAt: '2026-09-01T00:00:00Z',
    updatedAt: '2026-09-02T00:00:00Z',
    createdBy: 'someone@example.com',
    inFlight: null,
    ...over,
  };
}

beforeEach(() => {
  backlogItems.mockReset();
  backlogItem.mockReset();
  dispatchBacklogItemToNewTeam.mockReset();
  updateBacklogItem.mockReset();
  updateBacklogItem.mockResolvedValue(item({ id: 1 }));
  backlogItem.mockResolvedValue({ item: item({ id: 1 }), dispatches: [], stats: [] });

  // What a new team would be made FROM. `applyDefaults` resolves a remembered name against the
  // live catalog, so a catalog is needed even when nothing is remembered.
  listCatalog.mockResolvedValue({
    agents: [{ name: 'claude-headless', mode: 'Headless', hidden: false }],
  });
  fileSystemRoots.mockResolvedValue({ roots: [] });
  localStorage.clear();
});

afterEach(resetBody);

/**
 * Seeds both stores, then mounts closed and opens - never mounted already open.
 *
 * ITS ITEMS ARE `ready`, because most of what is asserted through it is the DISPATCH path and the
 * Dispatch control on an item that is not ready is dead by design. The states either side
 * of that rule have their own fixtures in `BacklogDialog readiness`, where they are the subject
 * rather than the setup.
 */
async function open(archived = false) {
  setActivePinia(createPinia());

  const board = useConsoleStore();
  board.teams = [{ id: 'alpha', name: 'Alpha' }] as never;

  const session = useSessionStore();
  session.user = { email: 'admin@example.com' } as never;

  if (archived) {
    backlogItems.mockResolvedValue([
      item({ id: 7, title: 'archived work', archivedAt: '2026-09-10T00:00:00Z' }),
    ]);
  } else {
    backlogItems.mockResolvedValue([
      item({ id: 1, title: 'first thing', state: 'ready' }),
      item({ id: 2, title: 'second thing', team: 'alpha', teamName: 'Alpha', state: 'ready' }),
    ]);
  }

  const wrapper = await mountDialog(BacklogDialog, {}, { pinia: false });
  await flushPromises();

  return wrapper;
}

/** The in-flight marks on screen, wherever Quasar teleported the table to. */
function inFlightMarks(): HTMLElement[] {
  return [...document.body.querySelectorAll('.backlog-inflight')] as HTMLElement[];
}

/**
 * WHETHER AN ITEM IS BEING WORKED, ON THE LIST. Without it the Backlog screen looks exactly the
 * same after a dispatch as before it - Team, Added, Modified, Actions - and the only way to learn a
 * team is on an item is to open it and read its dispatch history.
 *
 * THE MARK IS DERIVED BY THE SERVER from what the platform already knows (the dispatch record and
 * whether its correlation is still an open workflow) and arrives on the row as `inFlight`. These
 * cases pin what a person READS from that field: the team and the workflow, on the row, and NOTHING
 * on a row whose field is null - the absence is the second half of the assertion, because a mark
 * drawn unconditionally would pass the first half.
 *
 * IT DOES NOT SIT IN THE TEAM COLUMN, and the fixture makes that visible: item 2 is LINKED to
 * `alpha` (who may see it) and in flight on `beta` (who is working it). The spec refuses to rewrite
 * the link on dispatch, so the two teams are two facts and the badge names the second one.
 */
describe('BacklogDialog in-flight marks', () => {
  async function openWithOneInFlight() {
    setActivePinia(createPinia());

    const board = useConsoleStore();
    board.teams = [{ id: 'alpha', name: 'Alpha' }, { id: 'beta', name: 'Beta' }] as never;

    const session = useSessionStore();
    session.user = { email: 'admin@example.com' } as never;

    backlogItems.mockResolvedValue([
      item({ id: 1, title: 'first thing' }),
      item({
        id: 2,
        title: 'second thing',
        team: 'alpha',
        teamName: 'Alpha',
        inFlight: { teamId: 'beta', teamName: 'Beta', correlation: 1402, running: true },
      }),
    ]);

    await mountDialog(BacklogDialog, {}, { pinia: false });
    await flushPromises();
  }

  it('marks a dispatched item whose workflow is open, naming the team and the workflow', async () => {
    await openWithOneInFlight();

    const marks = inFlightMarks();

    expect(marks).toHaveLength(1);
    expect(marks[0]!.textContent).toContain('Beta');
    expect(marks[0]!.textContent).toContain('#1402');

    // ON THE ROW OF THE ITEM IT DESCRIBES, and not on the other one.
    const row = marks[0]!.closest('tr');

    expect(row?.textContent).toContain('second thing');
    expect(row?.textContent).not.toContain('first thing');
  });

  it('marks nothing when no item has an open dispatch', async () => {
    await open();

    expect(inFlightMarks()).toHaveLength(0);
  });

  /**
   * RUNNING NOW AND OPEN-BUT-IDLE ARE TWO FACTS, and the mark says which. A workflow stays open
   * until its Manager declares it or a person closes it, so an idle open workflow is an ordinary
   * shape rather than a fault - but a person deciding whether to intervene wants to know whether
   * anybody is actually running at this moment.
   */
  it('says whether a member is running right now', async () => {
    await openWithOneInFlight();

    expect(inFlightMarks()[0]!.getAttribute('data-running')).toBe('yes');
  });
});

/**
 * THE TEAM FILTER, AND THE COLUMN IT FOLLOWS.
 *
 * WHAT CAN GO WRONG: a dropdown built from `createOptions` - the same list the *Add item* dialog
 * uses, which is EVERY TEAM ON THE BOARD - beside a `visible` that narrows on `item.team`, the
 * LINK, which says who may SEE an item and is deliberately never rewritten on dispatch. On an
 * instance whose backlog is all tenant items that makes every option but `All` a dead end, and
 * the screen contradicts itself: the row names the team working the item, and filtering by that
 * exact team empties the table.
 *
 * THESE CASES PIN BOTH HALVES, because either alone passes against the defect. Options built from
 * the ITEMS is one half; a filter that then narrows on the same fact is what makes the option mean
 * something.
 */
/**
 * THE STATE CONTROL AND THE DISPATCH GATE.
 *
 * WHAT THESE CASES CAN SEE THAT A SOURCE SCAN CANNOT: that the Dispatch control is disabled AND
 * that its reason is on the row rather than in a tooltip. A grep finds `q-tooltip` in the file
 * either way, and a tooltip inside a disabled `<button>` never opens - the browser fires no mouse
 * events on it at all. "Disabled and silent" and "disabled with a reason" are the same source
 * until somebody reads the DOM.
 */
describe('BacklogDialog readiness', () => {
  async function openWith(state: string) {
    setActivePinia(createPinia());

    const board = useConsoleStore();
    board.teams = [{ id: 'alpha', name: 'Alpha' }] as never;

    const session = useSessionStore();
    session.user = { email: 'admin@example.com' } as never;

    backlogItems.mockResolvedValue([item({ id: 1, title: 'first thing', state })]);
    backlogItem.mockResolvedValue({ item: item({ id: 1, state }), dispatches: [], stats: [] });

    const wrapper = await mountDialog(BacklogDialog, {}, { pinia: false });
    await flushPromises();

    return wrapper;
  }

  function sendButton(): HTMLButtonElement {
    return [...document.querySelectorAll('button')]
      .find((b) => b.innerHTML.includes('send')) as HTMLButtonElement;
  }

  /** Opens the detail panel, which is where the review control lives. */
  async function openTheItem() {
    [...document.querySelectorAll('tr')]
      .find((row) => row.textContent?.includes('first thing'))!
      .dispatchEvent(new Event('click', { bubbles: true }));
    await flushPromises();
  }

  it('leaves Dispatch live on a ready item', async () => {
    await openWith('ready');

    expect(sendButton().disabled).toBe(false);
    expect(bodyText()).not.toContain('mark it ready to dispatch');
  });

  it('disables Dispatch on a pending item and says why on the row', async () => {
    await openWith('pending');

    expect(sendButton().disabled).toBe(true);

    // THE WORDS, IN THE DOM, WITHOUT A HOVER. And they name the way out, not only the refusal.
    expect(bodyText()).toContain('Not reviewed - mark it ready to dispatch.');
  });

  it('disables Dispatch on an implemented item and says why', async () => {
    await openWith('implemented');

    expect(sendButton().disabled).toBe(true);
    expect(bodyText()).toContain('Implemented - mark it ready to dispatch it again.');
  });

  /**
   * THE CLICK IS REFUSED AS WELL AS THE BUTTON. A disabled attribute is a rendering; this asserts
   * the dispatch dialog does not open even if something reaches the handler - the same reason the
   * server refuses at BOTH routes rather than trusting the one the screen calls.
   */
  it('opens no dispatch dialog for an item that is not ready', async () => {
    await openWith('pending');

    sendButton().click();
    await flushPromises();

    expect(bodyText()).not.toContain('To a team that exists');
  });

  it('moves an item from pending to ready from the detail panel', async () => {
    updateBacklogItem.mockResolvedValue(item({ id: 1, state: 'ready' }));

    await openWith('pending');
    await openTheItem();

    expect(bodyText()).toContain('Pending means nobody has reviewed this yet.');

    [...document.querySelectorAll('button')]
      .find((b) => b.textContent?.trim() === 'Ready')!
      .click();
    await flushPromises();

    expect(updateBacklogItem).toHaveBeenCalledWith(1, { state: 'ready' });

    // THE PANEL FOLLOWS THE WRITE. `load()` re-reads the list, not the open detail, so without
    // that the toggle snapped straight back to Pending while the row behind it changed.
    expect(bodyText()).toContain('Ready: somebody has read this');
  });

  it('moves it back to pending', async () => {
    updateBacklogItem.mockResolvedValue(item({ id: 1, state: 'pending' }));

    await openWith('ready');
    await openTheItem();

    [...document.querySelectorAll('button')]
      .find((b) => b.textContent?.trim() === 'Pending')!
      .click();
    await flushPromises();

    expect(updateBacklogItem).toHaveBeenCalledWith(1, { state: 'pending' });
  });

  /**
   * `implemented` IS NOT SOMETHING THE REVIEW CONTROL SETS. It is the other axis and the platform
   * sets it itself when a workflow completes; one toggle for both decisions is how somebody marks
   * their own draft done by reaching for the nearest control.
   */
  it('offers only pending and ready on the review control', async () => {
    await openWith('pending');
    await openTheItem();

    const toggle = document.body.querySelector('.backlog-review');

    expect(toggle).not.toBeNull();
    expect(toggle!.textContent).toContain('Pending');
    expect(toggle!.textContent).toContain('Ready');
    expect(toggle!.textContent).not.toContain('implemented');
  });

  /**
   * ABSENT RATHER THAN DISABLED ON AN IMPLEMENTED ITEM - neither of its two values is the item's
   * state, so it would render with nothing selected and read as broken. The caption says so and
   * the button beside it is the way back.
   */
  it('hides the review control on an implemented item and names the way back', async () => {
    await openWith('implemented');
    await openTheItem();

    expect(document.body.querySelector('.backlog-review')).toBeNull();
    expect(bodyText()).toContain('Reopen it as pending');
    expect(bodyText()).toContain('Reopen as pending');
  });
});

/**
 * TWO CLAIMS ON ONE ROW, AND A PERSON HAS TO BE ABLE TO TELL THEM APART.
 *
 * THE REQUIREMENT: *a person reading the Backlog can tell the difference between "a Manager says
 * this is done" and "this is on main"*. An item can read `implemented`
 * while the only copy of the work sits unpushed in one team's clone, one destructive click from
 * being lost, and the product has to say so rather than leave it to a person reading the git state.
 *
 * SO THERE ARE TWO FIELDS AND THEY ARE RENDERED SEPARATELY. `state` is what somebody SAID, and
 * `declared` is the word for a Manager having said it. `landed` is what the REPOSITORY SHOWS, is
 * derived by the server and stored nowhere.
 *
 * WHAT THESE CASES CAN SEE THAT A SOURCE SCAN CANNOT: that `unknown` and `local` are two different
 * renderings in the DOM rather than one string with a different adjective. A grep finds both words
 * in `lib/backlog.ts` whether or not the template distinguishes them, and "we cannot tell" rendered
 * as "not landed" is precisely the substitution this item forbids.
 */
describe('BacklogDialog landing', () => {
  async function openWith(over: Record<string, unknown>) {
    setActivePinia(createPinia());

    const board = useConsoleStore();
    board.teams = [{ id: 'alpha', name: 'Alpha' }] as never;

    const session = useSessionStore();
    session.user = { email: 'admin@example.com' } as never;

    const row = item({ id: 1, title: 'first thing', ...over });

    backlogItems.mockResolvedValue([row]);
    backlogItem.mockResolvedValue({ item: row, dispatches: [], stats: [] });

    const wrapper = await mountDialog(BacklogDialog, {}, { pinia: false });
    await flushPromises();

    return wrapper;
  }

  /**
   * A LANDING SHAPED LIKE THE ONE THE SERVER ACTUALLY SENDS, `teamId` and all - three fields
   * on the wire and `WireShapeTests` pins the set. The id names the team whose clone
   * was asked, which is the team the row's own Team column shows; the screen carries it and
   * deliberately does not draw it.
   */
  function landing(state: string, detail = '') {
    return { state, teamId: 'alpha', detail };
  }

  /** The landing marks on screen, wherever Quasar teleported the table to. */
  function landingMarks(): HTMLElement[] {
    return [...document.body.querySelectorAll('.backlog-landed')] as HTMLElement[];
  }

  async function openTheItem() {
    [...document.querySelectorAll('tr')]
      .find((row) => row.textContent?.includes('first thing'))!
      .dispatchEvent(new Event('click', { bubbles: true }));
    await flushPromises();
  }

  function buttonLabelled(label: string): HTMLButtonElement | undefined {
    return [...document.querySelectorAll('button')]
      .find((b) => b.textContent?.trim() === label) as HTMLButtonElement | undefined;
  }

  /**
   * ONE MARK PER ROW, IN THE STATE'S OWN WORDS. `local` is the near-miss that raised the item: one
   * disk, no remote, a deletion away from gone. `unknown` is shown, never hidden: dropping the mark
   * would leave a `declared` item looking like today's screen, and rendering it as a negative
   * would have the product assert something nobody measured.
   */
  it.each([
    ['landed', 'Merged into main.', 'On the default branch'],
    ['pushed', 'On origin, not on main.', 'Pushed, not merged'],
    ['local', 'No branch on origin.', "Only in the team's clone"],
    ['unknown', 'No repository on this team.', 'Cannot tell'],
  ] as const)('marks a %s item with its own words', async (state, detail, words) => {
    await openWith({ state: 'declared', landed: landing(state, detail) });

    expect(landingMarks()).toHaveLength(1);
    expect(landingMarks()[0]!.getAttribute('data-landed')).toBe(state);
    expect(landingMarks()[0]!.textContent).toContain(words);
  });

  /**
   * THE MARK CARRIES THE TEAM ID AND DOES NOT DRAW IT, and the second half of the assertion is
   * what makes that a decision rather than a gap: the row names the team anyway, in its Team
   * column, under the NAME. The wire carries `landed.teamId` because the server sends it and
   * these types are the wire; drawing a second, less readable copy of one fact beside the mark
   * would add nothing the row does not already say.
   */
  it('does not print the team id beside the landing - the Team column already names the team', async () => {
    await openWith({
      state: 'declared',
      dispatchedTeam: 'unmistakable-team-id',
      dispatchedTeamName: 'Alpha',
      landed: { state: 'local', teamId: 'unmistakable-team-id', detail: 'Two commits on no remote.' },
    });

    const mark = landingMarks()[0]!;

    expect(mark.textContent).not.toContain('unmistakable-team-id');
    expect(mark.getAttribute('title')).not.toContain('unmistakable-team-id');

    // THE SERVER'S OWN SENTENCE STILL ARRIVES - leaving the id off is not leaving the detail off.
    expect(mark.getAttribute('title')).toContain('Two commits on no remote.');

    // AND THE ROW STILL SAYS WHOSE WORK IT IS.
    expect(mark.closest('tr')!.textContent).toContain('Alpha');
  });

  /**
   * THE ITEM'S OWN REQUIREMENT, ASSERTED ACROSS TWO MOUNTS. `unknown` renders a different marker
   * and different words from `local` - *nobody has said* and *it is not done* are different facts,
   * and this codebase already refuses to conflate them for spend.
   */
  it('renders `unknown` differently from `local` in the DOM', async () => {
    setActivePinia(createPinia());

    const board = useConsoleStore();
    board.teams = [{ id: 'alpha', name: 'Alpha' }] as never;

    const session = useSessionStore();
    session.user = { email: 'admin@example.com' } as never;

    // ONE LIST, BOTH ANSWERS, SO THE COMPARISON IS BETWEEN TWO ROWS OF ONE SCREEN.
    backlogItems.mockResolvedValue([
      item({ id: 1, title: 'nobody looked', state: 'declared', landed: landing('unknown', '') }),
      item({ id: 2, title: 'on one disk', state: 'declared', landed: landing('local', '') }),
    ]);

    await mountDialog(BacklogDialog, {}, { pinia: false });
    await flushPromises();

    const marks = landingMarks();

    expect(marks).toHaveLength(2);

    const byRow = new Map(marks.map((mark) => [
      mark.closest('tr')!.textContent!.includes('nobody looked') ? 'unknown' : 'local',
      mark,
    ]));

    expect(byRow.get('unknown')!.getAttribute('data-landed'))
      .not.toBe(byRow.get('local')!.getAttribute('data-landed'));
    expect(byRow.get('unknown')!.textContent!.trim())
      .not.toBe(byRow.get('local')!.textContent!.trim());
  });

  /**
   * NOTHING AT ALL WHEN THERE IS NO LANDING TO REPORT - the absence is the second half of the
   * assertion, because a mark drawn unconditionally passes every case above. An item no dispatch
   * has touched has no landing question to answer, which is not the same as being unable to answer
   * one.
   */
  it('marks nothing on an item the server sent no landing for', async () => {
    await openWith({ state: 'pending' });

    expect(landingMarks()).toHaveLength(0);
  });

  /**
   * THE SENTENCE. Both facts are on the row at once and they say different things - the state says
   * a Manager declared it, the landing says the work is on one disk. Either alone is a lie by
   * omission.
   */
  it('puts the claim and the landing on the same row, saying different things', async () => {
    await openWith({ state: 'declared', landed: landing('local', 'No branch on origin.') });

    const row = landingMarks()[0]!.closest('tr')!;

    expect(row.textContent).toContain('declared');
    expect(row.textContent).toContain("Only in the team's clone");

    // AND THE STATE IS NOT READ AS SHIPPED. The row explains what `declared` is, in words.
    expect(row.textContent).toContain('Manager');
  });

  it('leaves Dispatch dead on a declared item without calling it unreviewed', async () => {
    await openWith({ state: 'declared', landed: null });

    const send = [...document.querySelectorAll('button')]
      .find((b) => b.innerHTML.includes('send')) as HTMLButtonElement;

    expect(send.disabled).toBe(true);
    expect(bodyText()).not.toContain('Not reviewed - mark it ready to dispatch.');
  });

  /**
   * THE DETAIL PANEL READS `declared` AS A CLAIM. The review toggle is absent for the reason it is
   * absent on `implemented` - neither of its two values is the item's state - and the caption is
   * what carries the meaning instead.
   */
  it('reads a declared item as a Manager saying so, not as shipped', async () => {
    await openWith({ state: 'declared', landed: landing('unknown', '') });
    await openTheItem();

    expect(document.body.querySelector('.backlog-review')).toBeNull();
    expect(bodyText()).toContain('Manager');
  });

  /**
   * A PERSON MAY STILL SAY `implemented` BY HAND AND THE DERIVATION NEVER OVERRIDES THEM. On a
   * `declared` item this is the only control that can move it the rest of the way, so it has to be
   * ON THE SCREEN and it has to work - and the way back has to be there beside it.
   */
  it('keeps both hand controls reachable on a declared item', async () => {
    updateBacklogItem.mockResolvedValue(item({ id: 1, state: 'implemented' }));

    await openWith({ state: 'declared', landed: landing('landed', '') });
    await openTheItem();

    expect(buttonLabelled('Reopen as pending')).toBeDefined();

    buttonLabelled('Mark implemented')!.click();
    await flushPromises();

    expect(updateBacklogItem).toHaveBeenCalledWith(1, { state: 'implemented' });
  });

  /** And the way back off a declared item is a person's, exactly as it is off an implemented one. */
  it('reopens a declared item as pending', async () => {
    updateBacklogItem.mockResolvedValue(item({ id: 1, state: 'pending' }));

    await openWith({ state: 'declared', landed: null });
    await openTheItem();

    buttonLabelled('Reopen as pending')!.click();
    await flushPromises();

    expect(updateBacklogItem).toHaveBeenCalledWith(1, { state: 'pending' });
  });

  /**
   * AN IMPLEMENTED ITEM DOES NOT GROW A SECOND "Mark implemented" BUTTON. The control is reachable
   * from every state except the one it sets, which is what stops it reading as a no-op.
   */
  it('offers no "mark implemented" on an item already implemented', async () => {
    await openWith({ state: 'implemented', landed: landing('landed', '') });
    await openTheItem();

    expect(buttonLabelled('Mark implemented')).toBeUndefined();
    expect(buttonLabelled('Reopen as pending')).toBeDefined();
  });
});

describe('BacklogDialog team filter', () => {
  /**
   * Alpha holds the LINK on item 2, Beta is WORKING it, and Gamma is a board team no item has
   * anything to do with. Gamma is the one a board-wide dropdown would offer and could never satisfy.
   */
  async function openWithADispatchedItem() {
    setActivePinia(createPinia());

    const board = useConsoleStore();
    board.teams = [
      { id: 'alpha', name: 'Alpha' },
      { id: 'beta', name: 'Beta' },
      { id: 'gamma', name: 'Gamma' },
    ] as never;

    const session = useSessionStore();
    session.user = { email: 'admin@example.com' } as never;

    backlogItems.mockResolvedValue([
      item({ id: 1, title: 'first thing' }),
      item({
        id: 2,
        title: 'second thing',
        team: 'alpha',
        teamName: 'Alpha',
        inFlight: { teamId: 'beta', teamName: 'Beta', correlation: 1402, running: true },
      }),
    ]);

    const wrapper = await mountDialog(BacklogDialog, {}, { pinia: false });
    await flushPromises();

    return wrapper;
  }

  /** The toolbar's Team picker. The Add and Dispatch dialogs are shut, so it is the only QSelect. */
  function filter(wrapper: VueWrapper) {
    const select = wrapper.findAllComponents({ name: 'QSelect' })
      .find((entry) => entry.props('label') === 'Team');

    if (!select) throw new Error('No Team filter on the toolbar');

    return select;
  }

  function optionLabels(wrapper: VueWrapper): string[] {
    return (filter(wrapper).props('options') as Array<{ label: string }>).map((o) => o.label);
  }

  async function choose(wrapper: VueWrapper, label: string) {
    const options = filter(wrapper).props('options') as Array<{ label: string; value: unknown }>;
    const option = options.find((o) => o.label === label);

    if (!option) throw new Error(`No filter option ${label}. Offered: ${options.map((o) => o.label).join(', ')}`);

    filter(wrapper).vm.$emit('update:modelValue', option.value);
    await flushPromises();
  }

  /**
   * Beta is named on row 2 in words a person can read, so choosing Beta must not empty the table -
   * that would be the contradiction this guards.
   */
  it('narrows to the rows the chosen team is working', async () => {
    const wrapper = await openWithADispatchedItem();

    await choose(wrapper, 'Beta');

    expect(bodyText()).toContain('second thing');
    expect(bodyText()).not.toContain('Nothing in the backlog yet.');
    expect(bodyText()).not.toContain('first thing');
  });

  /**
   * AN OPTION THAT CAN ONLY EVER RETURN NOTHING IS NOT OFFERED. Gamma is on the board and on no
   * item, so it is not a choice; Alpha holds a LINK and is working nothing, so it is not one
   * either - the filter follows the column, and the column is the dispatched team.
   */
  it('offers only teams the items actually carry', async () => {
    const wrapper = await openWithADispatchedItem();

    expect(optionLabels(wrapper)).toContain('Beta');
    expect(optionLabels(wrapper)).not.toContain('Gamma');
    expect(optionLabels(wrapper)).not.toContain('Alpha');
  });

  /** The complement, offered only because there IS such a row. */
  it('narrows to the items nobody has been given', async () => {
    const wrapper = await openWithADispatchedItem();

    await choose(wrapper, 'Not dispatched');

    expect(bodyText()).toContain('first thing');
    expect(bodyText()).not.toContain('second thing');
  });

  /**
   * ORDER IS A PROPERTY OF THE WHOLE BACKLOG. A drop inside a filtered view lands between
   * neighbours that are not the item's real ones, so the reorder controls go dead and SAY SO.
   */
  it('disables reordering under a filter and says why', async () => {
    const wrapper = await openWithADispatchedItem();

    await choose(wrapper, 'Beta');

    expect(bodyText()).toContain('Clear the team filter to reorder');

    const arrows = [...document.querySelectorAll('button')]
      .filter((b) => b.innerHTML.includes('arrow_upward') || b.innerHTML.includes('arrow_downward'));

    expect(arrows.length).toBeGreaterThan(0);
    expect(arrows.every((b) => (b as HTMLButtonElement).disabled)).toBe(true);
  });
});

describe('BacklogDialog', () => {
  it('loads the backlog when it is opened, not when it is mounted', async () => {
    await open();

    expect(backlogItems).toHaveBeenCalledWith(false);
    expect(bodyText()).toContain('first thing');
    expect(bodyText()).toContain('second thing');
  });

  /**
   * THE PLATFORM ID CARRIES `B` AND THE POSITION `#` DOES NOT, which is the whole reason the two
   * can sit on one row. Two bare integers, one of which renumbers under you, is the confusion the
   * prefix prevents.
   */
  it('renders ids with an B prefix beside a bare position index', async () => {
    await open();

    // PADDED: `B0001`, not `B1`, so a list of labels sorts as strings in id order.
    expect(bodyText()).toContain('B0001');
    expect(bodyText()).toContain('B0002');
  });

  /**
   * AN ITEM NOBODY HAS BEEN GIVEN SHOWS NOTHING IN THE TEAM COLUMN.
   *
   * `(no team)` would be an answer to the wrong question: the column says who the item was
   * DISPATCHED to, and `(no team)` is a statement about the LINK. Reading "no
   * team" as "nobody is working this" happened to be true often enough to hide the difference.
   */
  it('leaves the team column empty on an item nobody has been given', async () => {
    await open();

    expect(bodyText()).not.toContain('(no team)');
  });

  /**
   * THE LINK STILL SHOWS, as a small marker and only on the item that carries one. It is a
   * different fact from the column beside it - who may SEE the item, not who was given it - and
   * the two are deliberately not merged.
   */
  it('marks the link on the one item that carries one', async () => {
    await open();

    const marks = [...document.body.querySelectorAll('.backlog-link')] as HTMLElement[];

    expect(marks).toHaveLength(1);
    expect(marks[0]!.textContent).toContain('Alpha');
    expect(marks[0]!.closest('tr')?.textContent).toContain('second thing');

    // AND IT SAYS WHAT IT IS FOR. Two teams on one row is exactly where a bare name misleads.
    expect(marks[0]!.getAttribute('title')).toContain('Visible to Alpha');
  });

  /**
   * DELETE IS OFFERED FROM THE ARCHIVE AND NOWHERE ELSE, and the ABSENCE is the assertion. A test
   * that only checked the archived one works would pass against a build that offered it from both.
   */
  //
  // ASSERTED ON THE ICON, NOT THE TOOLTIP. A `q-tooltip` renders nothing until it is shown, so a
  // check against its label passes on the archived tab for the wrong reason - it is absent there
  // too until somebody hovers. Quasar puts the icon NAME in the element's text, which is in the DOM
  // either way, so `delete` and `unarchive` are what separate the two tabs.
  it('offers no delete control on the backlog tab', async () => {
    await open();

    expect(bodyText()).not.toContain('delete');
    expect(bodyText()).not.toContain('unarchive');

    // And the backlog tab's OWN controls are there, so the case above is not passing because the
    // row rendered no actions at all.
    expect(bodyText()).toContain('inventory_2');
  });

  it('offers restore and delete on the archived tab', async () => {
    const wrapper = await open(true);

    // Switching tabs re-loads, and the archived list is what the mock now answers.
    await wrapper.findComponent({ name: 'QTabs' }).vm.$emit('update:modelValue', 'archived');
    await flushPromises();

    // The archive reads by cursor, from the top, and never by offset.
    expect(backlogItems).toHaveBeenCalledWith(true, { before: undefined, take: 50 });
    expect(bodyText()).toContain('delete');
    expect(bodyText()).toContain('unarchive');
  });

  it('reads older archived items below the lowest id it holds', async () => {
    const wrapper = await open(true);

    backlogItems.mockResolvedValueOnce(
      Array.from({ length: 50 }, (_, i) =>
        item({ id: 300 - i, title: `archived ${300 - i}`, archivedAt: '2026-09-10T00:00:00Z' }),
      ),
    );
    backlogItems.mockResolvedValueOnce([
      item({ id: 3, title: 'the oldest archived', archivedAt: '2026-01-01T00:00:00Z' }),
    ]);

    await wrapper.findComponent({ name: 'QTabs' }).vm.$emit('update:modelValue', 'archived');
    await flushPromises();

    const more = document.body.querySelector('.cursor-sentinel button') as HTMLElement;
    more.click();
    await flushPromises();

    expect(backlogItems).toHaveBeenLastCalledWith(true, { before: 251, take: 50 });
    expect(bodyText()).toContain('the oldest archived');
    expect(bodyText()).toContain('The oldest archived item.');

    // No reorder control on the archive, whatever the sort.
    expect(bodyText()).not.toContain('arrow_upward');
  });
});

/**
 * DISPATCHING INTO A TEAM THAT DOES NOT EXIST YET.
 *
 * WHAT THESE CASES CAN SEE THAT A SOURCE SCAN CANNOT: that the name field is PREFILLED from the
 * item that was clicked, and prefilled at OPEN rather than at mount. Deriving at mount would show
 * the previous item's name, which is the shape of defect a grep for `newTeamNameFor` passes over
 * entirely - the call is in the file either way.
 *
 * The other half is that the two modes are exclusive: the existing-team picker and the
 * name-plus-source pair must never both be on screen, because a person who filled in one and
 * submitted the other would create a team they did not mean to.
 */
describe('BacklogDialog dispatching to a new team', () => {
  /**
   * THROUGH THE BODY, NEVER THE WRAPPER. A QDialog teleports to `<body>`, so `wrapper.findAll`
   * returns nothing and the spec reads as "the dialog did not render" when it rendered perfectly.
   * The row control is an ICON button, so it is found by its icon rather than by text.
   */
  function sendButtons(): HTMLElement[] {
    return [...document.querySelectorAll('button')]
      .filter((b) => b.innerHTML.includes('send')) as HTMLElement[];
  }

  function openDispatchFromRow(title: string) {
    const row = [...document.querySelectorAll('tr')]
      .find((entry) => entry.textContent?.includes(title))!;
    const button = [...row.querySelectorAll('button')]
      .find((entry) => entry.innerHTML.includes('send'))! as HTMLElement;

    button.click();
  }

  function selectFields(wrapper: {
    findAllComponents: (selector: { name: string }) => {
      props: (name: string) => unknown
      vm: { $emit: (event: string, value: unknown) => void }
    }[]
  }) {
    return wrapper.findAllComponents({ name: 'QSelect' });
  }

  function setSelectValue(
    wrapper: {
      findAllComponents: (selector: { name: string }) => {
        props: (name: string) => unknown
        vm: { $emit: (event: string, value: unknown) => void }
      }[]
    },
    label: string,
    value: unknown,
  ) {
    const select = selectFields(wrapper)
      .find((entry) => entry.props('label') === label);

    if (!select) throw new Error(`No QSelect labelled ${label}`);

    select.vm.$emit('update:modelValue', value);
  }

  /**
   * SETTINGS THIS BROWSER REMEMBERS, used when a case wants to prove the "remembered" branch rather
   * than the fallback one. Seeded here rather than in `beforeEach` so the empty-browser path stays
   * visible in the cases that need it.
   */
  function rememberATeamWasMadeHere() {
    localStorage.setItem('harness.newTeamDefaults', JSON.stringify({
      managerAgent: 'claude-headless',
      memberAgents: ['claude-headless'],
      root: null,
      repos: ['https://github.com/example/Widget.git'],
    }));
  }

  /**
   * OPENS THE DIALOG AND CHOOSES THE NEW-TEAM MODE. The name field and the source picker are not
   * rendered until the mode is chosen, which is the point: the two shapes are exclusive, so a
   * person cannot fill in one and submit the other.
   */
  async function openDispatchFor(index: number) {
    rememberATeamWasMadeHere();

    const wrapper = await open();

    sendButtons()[index]!.click();
    await flushPromises();

    await chooseNewTeamMode();

    return wrapper;
  }

  async function openDispatchForInFlightItem() {
    rememberATeamWasMadeHere();

    setActivePinia(createPinia());

    const board = useConsoleStore();
    board.teams = [{ id: 'alpha', name: 'Alpha' }, { id: 'beta', name: 'Beta' }] as never;

    const session = useSessionStore();
    session.user = { email: 'admin@example.com' } as never;

    backlogItems.mockResolvedValue([
      item({ id: 1, title: 'first thing', state: 'ready' }),
      item({
        id: 2,
        title: 'second thing',
        team: 'alpha',
        teamName: 'Alpha',
        state: 'ready',
        inFlight: { teamId: 'beta', teamName: 'Beta', correlation: 1402, running: true },
      }),
    ]);

    await mountDialog(BacklogDialog, {}, { pinia: false });
    await flushPromises();

    openDispatchFromRow('second thing');
    await flushPromises();

    await chooseNewTeamMode();
  }

  /** The mode resets every time the dialog opens, so every open has to choose it again. */
  async function chooseNewTeamMode() {
    [...document.querySelectorAll('.q-radio')]
      .find((r) => r.textContent?.includes('new team'))!
      .dispatchEvent(new Event('click', { bubbles: true }));
    await flushPromises();
  }

  function button(label: string): HTMLElement {
    return [...document.querySelectorAll('button')]
      .find((entry) => entry.textContent?.trim() === label)! as HTMLElement;
  }

  it('prefills the new team name from the item that was clicked', async () => {
    await openDispatchFor(1);

    const field = [...document.querySelectorAll('input')]
      .find((i) => (i as HTMLInputElement).value.startsWith('b0002'));

    expect(field).toBeDefined();
    expect((field as HTMLInputElement).value).toBe('b0002-second-thing');

    // STILL A PREFILL, NOT A LABEL. A consistent spelling must not make the field read-only: a
    // person edits this before dispatching.
    expect((field as HTMLInputElement).readOnly).toBe(false);
    expect((field as HTMLInputElement).disabled).toBe(false);
  });

  it('derives from the item opened, not the one before it', async () => {
    await openDispatchFor(1);

    // Close, then open the OTHER item. A dialog that derived at mount would still say b0002.
    [...document.querySelectorAll('button')]
      .find((b) => b.textContent?.includes('Cancel'))!.click();
    await flushPromises();

    sendButtons()[0]!.click();
    await flushPromises();
    await chooseNewTeamMode();

    const values = [...document.querySelectorAll('input')].map((i) => (i as HTMLInputElement).value);

    expect(values).toContain('b0001-first-thing');
    expect(values).not.toContain('b0002-second-thing');
  });

  /**
   * IT NAMES WHAT IT WILL MAKE. A screen about to create a team has to say what kind - and these
   * are this browser's remembered values, not another team's and not a guess. A blank picker
   * while its model holds a value is how a dialog gets one click from creating something nobody
   * saw named.
   */
  it('names the Agent a new team would be made from, and no Prompt', async () => {
    await openDispatchFor(0);

    expect(bodyText()).toContain('claude-headless');
    expect(bodyText()).toContain('remembered');
    const summary = [...document.querySelectorAll('.text-caption')]
      .find((line) => line.textContent?.includes('Manager runs'));
    expect(summary?.textContent).not.toMatch(/told|prompt/i);
  });

  /**
   * A BROWSER WITH EMPTY LOCALSTORAGE CAN STILL CREATE A TEAM FROM HERE. Requiring a team already
   * made in this exact browser+origin pair would be backwards: the "start this spec" path is most
   * needed on a fresh device.
   *
   * IT ASSERTS THE DISPATCH CALL, not only that the radio can be clicked. Letting the mode open but
   * still dead-ending on submit would be the same bug wearing a different caption.
   */
  it('dispatches to a new team even when this browser remembers nothing', async () => {
    dispatchBacklogItemToNewTeam.mockResolvedValue({
      team: 'b1-first-thing', teamName: 'b1-first-thing', correlation: 9, dispatch: 1,
    });

    const wrapper = await open();

    sendButtons()[0]!.click();
    await flushPromises();

    const option = [...document.querySelectorAll('.q-radio')]
      .find((r) => r.textContent?.includes('new team'))!;

    expect(option.classList.contains('disabled')).toBe(false);

    await chooseNewTeamMode();

    expect(bodyText()).toContain('fallback');
    expect(bodyText()).toContain('Finish Team settings before Dispatch');

    const board = useConsoleStore();
    vi.spyOn(board, 'refresh').mockResolvedValue(undefined as never);
    vi.spyOn(board, 'setActiveTeam').mockImplementation(() => {});

    [...document.querySelectorAll('button')]
      .find((b) => b.textContent?.includes('Team settings'))!
      .click();
    await flushPromises();

    setSelectValue(wrapper, 'Agents members may run', ['claude-headless']);
    await flushPromises();

    [...document.querySelectorAll('button')]
      .find((b) => b.textContent?.trim() === 'Use these')!
      .click();
    await flushPromises();

    [...document.querySelectorAll('button')]
      .find((b) => b.textContent?.trim() === 'Dispatch')!
      .click();
    await flushPromises();

    expect(dispatchBacklogItemToNewTeam).toHaveBeenCalled();
    expect(dispatchBacklogItemToNewTeam).toHaveBeenCalledWith(
      1,
      'b0001-first-thing',
      expect.objectContaining({
        agent: 'claude-headless',
        memberAgents: ['claude-headless'],
      }),
    );
    expect(dispatchBacklogItemToNewTeam.mock.calls[0]![2]).not.toHaveProperty('managerPrompt');
    expect(dispatchBacklogItemToNewTeam.mock.calls[0]![2]).not.toHaveProperty('memberPrompt');
  });

  it('says when the Manager Agent was chosen here', async () => {
    listCatalog.mockResolvedValue({
      agents: [
        { name: 'claude-headless', mode: 'Headless', hidden: false },
        { name: 'gpt-headless', mode: 'Headless', hidden: false },
      ],
    });

    const wrapper = await openDispatchFor(0);

    [...document.querySelectorAll('button')]
      .find((b) => b.textContent?.includes('Team settings'))!
      .click();
    await flushPromises();

    setSelectValue(wrapper, 'Agent the Manager runs', 'gpt-headless');
    await flushPromises();

    [...document.querySelectorAll('button')]
      .find((b) => b.textContent?.trim() === 'Use these')!
      .click();
    await flushPromises();

    expect(bodyText()).toContain('gpt-headless');
    expect(bodyText()).toContain('chosen here');
  });

  /**
   * A TEAM THIS BROWSER JUST MADE IS ONE NOTHING WILL TELL IT ABOUT. `teamChanged` is pushed to
   * `Clients.Group(team.Id)` and this connection cannot be in the group of a team that did not
   * exist a moment ago - so the client that created it is the one client the push structurally
   * cannot reach, and without a refresh the team is real, running, and invisible until a hard reload.
   *
   * IT ASSERTS THE STORE, NOT THE NOTIFICATION. "It said it dispatched" and "the team is on the
   * screen" are different claims and only the second is what matters.
   */
  it('refreshes the team list and lands on the team it just made', async () => {
    dispatchBacklogItemToNewTeam.mockResolvedValue({
      team: 'b1-first-thing', teamName: 'b1-first-thing', correlation: 41, dispatch: 7,
    });

    await openDispatchFor(0);

    const board = useConsoleStore();

    // BOTH STORE ACTIONS ARE STUBBED, and that is the altitude rather than laziness. `setActiveTeam`
    // is the one writer that tells the server, so it fans out into six requests the moment it runs
    // for real - and what this case is about is whether the COMPONENT asks, not what the store then
    // does. Letting them run would test the store through the dialog and fill the output with
    // ECONNREFUSED, which is the noise that teaches people to stop reading it.
    const refreshed = vi
      .spyOn(board, 'refreshForTeamCreated')
      .mockResolvedValue(undefined as never);
    const activated = vi.spyOn(board, 'setActiveTeam').mockImplementation(() => {});

    [...document.querySelectorAll('button')]
      .find((b) => b.textContent?.trim() === 'Dispatch')!.click();
    await flushPromises();

    expect(dispatchBacklogItemToNewTeam).toHaveBeenCalled();
    expect(activated).toHaveBeenCalledWith('b1-first-thing');
    expect(refreshed).toHaveBeenCalled();
  });

  /**
   * THE REPOSITORIES TRAVEL.
   *
   * `applyDefaults` returns `repos: []` and puts the remembered URLs in `repoSuggestions`. A dialog
   * that does not read them creates every team dispatched into with NO REPOSITORY, and its Manager
   * plans cards, hires members and then blocks on `no repository attached`, after the spend.
   *
   * IT ASSERTS THE ARGUMENT, NOT THE SCREEN. What matters is what the dialog SENDS, and a case
   * reading the rendered chips would have passed against a field that displayed correctly and
   * dispatched an empty list.
   */
  it('carries the repositories this browser remembers into the new team', async () => {
    dispatchBacklogItemToNewTeam.mockResolvedValue({
      team: 'b1-first-thing', teamName: 'b1-first-thing', correlation: 9, dispatch: 1,
    });

    await openDispatchFor(0);

    const board = useConsoleStore();
    vi.spyOn(board, 'refreshForTeamCreated').mockResolvedValue(undefined as never);
    vi.spyOn(board, 'setActiveTeam').mockImplementation(() => {});

    [...document.querySelectorAll('button')]
      .find((b) => b.textContent?.trim() === 'Dispatch')!.click();
    await flushPromises();

    expect(dispatchBacklogItemToNewTeam).toHaveBeenCalled();

    const settings = dispatchBacklogItemToNewTeam.mock.calls[0]![2] as { repos: string[] };

    expect(settings.repos).toEqual(['https://github.com/example/Widget.git']);
  });

  /**
   * A DISPATCH TEACHES THIS BROWSER WHAT IT USED, which is what makes the prefill above worth
   * having. Without it, somebody who dispatches rather than using New Team would re-choose the same
   * repositories every time and their stored `repos` would stay empty.
   */
  it('remembers the settings a dispatch used, for the next one', async () => {
    dispatchBacklogItemToNewTeam.mockResolvedValue({
      team: 'b1-first-thing', teamName: 'b1-first-thing', correlation: 9, dispatch: 1,
    });

    await openDispatchFor(0);
    localStorage.removeItem('harness.newTeamDefaults');

    const board = useConsoleStore();
    vi.spyOn(board, 'refreshForTeamCreated').mockResolvedValue(undefined as never);
    vi.spyOn(board, 'setActiveTeam').mockImplementation(() => {});

    [...document.querySelectorAll('button')]
      .find((b) => b.textContent?.trim() === 'Dispatch')!.click();
    await flushPromises();

    const stored = JSON.parse(localStorage.getItem('harness.newTeamDefaults') ?? '{}');

    expect(stored.repos).toEqual(['https://github.com/example/Widget.git']);
    expect(stored.managerAgent).toBe('claude-headless');
  });

  it('asks for confirmation by naming the holding team before redispatching an in-flight item', async () => {
    dispatchBacklogItemToNewTeam.mockResolvedValue({
      team: 'b2-second-thing', teamName: 'b2-second-thing', correlation: 9, dispatch: 1,
    });

    await openDispatchForInFlightItem();
    dispatchBacklogItemToNewTeam.mockClear();

    const board = useConsoleStore();
    vi.spyOn(board, 'refresh').mockResolvedValue(undefined as never);
    vi.spyOn(board, 'setActiveTeam').mockImplementation(() => {});

    button('Dispatch').click();
    await flushPromises();

    expect(dispatchBacklogItemToNewTeam).not.toHaveBeenCalled();
    expect(bodyText()).toContain('already in flight on Beta');

    button('Dispatch anyway').click();
    await flushPromises();

    expect(dispatchBacklogItemToNewTeam).toHaveBeenCalledOnce();
  });

  it('raises no confirmation when the item is not in flight', async () => {
    dispatchBacklogItemToNewTeam.mockResolvedValue({
      team: 'b1-first-thing', teamName: 'b1-first-thing', correlation: 9, dispatch: 1,
    });

    await openDispatchFor(0);
    dispatchBacklogItemToNewTeam.mockClear();

    const board = useConsoleStore();
    vi.spyOn(board, 'refresh').mockResolvedValue(undefined as never);
    vi.spyOn(board, 'setActiveTeam').mockImplementation(() => {});

    button('Dispatch').click();
    await flushPromises();

    expect(bodyText()).not.toContain('already in flight on');
    expect(dispatchBacklogItemToNewTeam).toHaveBeenCalledOnce();
  });

  /**
   * THE SETTINGS ARE REACHABLE FROM THE SCREEN THAT DISPATCHES. Without it the dispatch dialog
   * offers no way to see or change what the team will be able to touch, and the only way to learn
   * a repo is missing is to watch a Manager block on it.
   */
  it('opens team settings from the dispatch dialog, prefilled', async () => {
    await openDispatchFor(0);

    const link = [...document.querySelectorAll('button')]
      .find((b) => b.textContent?.includes('Team settings'));

    expect(link).toBeDefined();

    link!.click();
    await flushPromises();

    expect(bodyText()).toContain('New team settings');
    expect(bodyText()).toContain('Repositories the platform clones');
    expect(bodyText()).toContain('https://github.com/example/Widget.git');
  });

  /**
   * THE LABEL MUST NOT SAY "CLONE". The mechanism is this browser's remembered settings, and
   * "cloned from one" sends a person with no teams looking for a team to clone - when what the
   * option wants is a previous team made IN THIS BROWSER.
   */
  it('does not call the new team a clone', async () => {
    await open();

    sendButtons()[0]!.click();
    await flushPromises();

    const option = [...document.querySelectorAll('.q-radio')]
      .find((r) => r.textContent?.includes('new team'))!;

    expect(option.textContent).not.toContain('clone');
  });

  /**
   * THE PANEL MUST CLOSE, which is the whole of this group.
   *
   * It is capped at 40vh, so on a laptop an open item takes nearly half the dialog and the list
   * above it shrinks to a handful of rows. Without a close control, and with a second click on the
   * row only re-fetching the same item, a person who opened an item to glance at it has no way back
   * to the list they were reading.
   *
   * MOUNTED RATHER THAN A `lib/` FUNCTION because both halves are template conditions: `v-if`
   * decides the panel and the ✕ is markup. A source scan finds `selected = null` either way.
   */
  describe('closing the item panel', () => {
    async function openList() {
      backlogItems.mockResolvedValue([
        item({ id: 1, title: 'first thing' }),
        item({ id: 2, title: 'second thing' }),
      ]);
      backlogItem.mockImplementation(async (id: number) => ({
        item: item({ id, title: id === 1 ? 'first thing' : 'second thing' }),
        dispatches: [],
        stats: [],
      }));

      const wrapper = await mountDialog(BacklogDialog, {}, { pinia: false });
      await flushPromises();

      return wrapper;
    }

    function clickRow(title: string) {
      [...document.querySelectorAll('tr')]
        .find((row) => row.textContent?.includes(title))!
        .dispatchEvent(new Event('click', { bubbles: true }));
    }

    /** The panel, told from the row by the editor that only the panel has. */
    const panelOpen = () => document.querySelectorAll('textarea').length > 0;

    it('opens the panel on a row click', async () => {
      await openList();

      clickRow('first thing');
      await flushPromises();

      expect(panelOpen()).toBe(true);
    });

    /**
     * CLICKING THE SAME ROW CLOSES IT - the affordance somebody tries first, and the one that used
     * to re-fetch the item it was already showing.
     */
    it('closes it again when the same row is clicked', async () => {
      await openList();

      clickRow('first thing');
      await flushPromises();
      clickRow('first thing');
      await flushPromises();

      expect(panelOpen()).toBe(false);
    });

    /**
     * AND A DIFFERENT ROW MOVES IT RATHER THAN CLOSING IT. Asserted because the toggle above is
     * one `if` away from closing the panel on every second click whatever was clicked, which would
     * make the list unusable in exactly the way this set out to fix.
     */
    it('moves to another item rather than closing when a different row is clicked', async () => {
      await openList();

      clickRow('first thing');
      await flushPromises();
      clickRow('second thing');
      await flushPromises();

      expect(panelOpen()).toBe(true);
      expect(bodyText()).toContain('second thing');
    });

    /**
     * THE ✕ IS NOT REDUNDANT WITH THE ROW TOGGLE. Re-sorting or filtering moves the row that
     * opened the panel, and a panel closable only by finding its own row again is the same trap in
     * a different shape.
     */
    it('closes from the panel control, without needing the row that opened it', async () => {
      await openList();

      clickRow('first thing');
      await flushPromises();

      const close = document.querySelector('[aria-label="Close this item"]') as HTMLElement | null;

      expect(close).not.toBeNull();

      close!.dispatchEvent(new Event('click', { bubbles: true }));
      await flushPromises();

      expect(panelOpen()).toBe(false);
    });
  });

});

/**
 * A DOCUMENT OPENS THE ADD FORM FILLED IN. The upload button reads the file in the browser and the
 * person checks the draft before it is saved - so what is asserted is the FORM, not a created item:
 * nothing reaches the server until Add is pressed.
 */
describe('BacklogDialog upload document', () => {
  function choose(file: File) {
    const input = document.body.querySelector('[data-test="backlog-document-input"]') as HTMLInputElement;
    Object.defineProperty(input, 'files', { value: [file], configurable: true });
    input.dispatchEvent(new Event('change'));
  }

  it('opens Add with the heading as the title and the whole document as the spec', async () => {
    const wrapper = await open();
    const text = '# Be able to watch headless output\n\n## Problem\n\nMinutes between progress rows.\n';

    choose(new File([text], 'watch.md', { type: 'text/markdown' }));
    await flushPromises();

    const inputs = [...document.body.querySelectorAll('.backlog-add-card textarea, .backlog-add-card input')] as HTMLInputElement[];
    expect(inputs.some((i) => i.value === 'Be able to watch headless output')).toBe(true);
    expect(inputs.some((i) => i.value === text)).toBe(true);
    wrapper.unmount();
  });

  it('says a file that is not text is not a document, and opens nothing', async () => {
    const wrapper = await open();

    choose(new File(['%PDF-1.7\u0000binary'], 'diagram.pdf', { type: 'application/pdf' }));
    await flushPromises();

    expect(bodyText()).toContain('diagram.pdf is not a text document');
    expect(document.body.querySelector('.backlog-add-card')).toBeNull();
    wrapper.unmount();
  });
});
