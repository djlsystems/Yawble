// @vitest-environment happy-dom
//
// NEEDS YOU LISTS THE PROPOSED OUTCOMES: each one a Manager proposed and only a person settles,
// with its name, its proposer (`createdBy`), when, and the route's count of workflows linking it.
// The lane's count includes them; one click opens Manage outcomes on that outcome; and confirming,
// merging or rejecting it there takes it off the lane. The outcomes route is a fake that holds
// state, so what leaves the lane is what the route stops answering as proposed.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';

const api = vi.hoisted(() => ({
  listOutcomes: vi.fn(),
  listLiveOutcomes: vi.fn(),
  getOutcome: vi.fn(),
  transitionOutcome: vi.fn(),
  previewMerge: vi.fn(),
  mergeOutcome: vi.fn(),
  rejectOutcome: vi.fn(),
}));

vi.mock('../../api/outcomes', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  ...api,
}));

import KanbanBoard from '../KanbanBoard.vue';
import { useKanbanStore } from '../../stores/kanban';
import type { KanbanCard, KanbanLane } from '../../api/kanban';
import type { Outcome, OutcomeFigures, OutcomeListQuery, OutcomeStatus } from '../../api/outcomes';
import { when } from '../../lib/outcomes';
import { bodyFind, resetBody } from '../../test/mountQuasar';
import { settle } from '../../test/formProbe';

function figures(total: number): OutcomeFigures {
  return {
    workflows: { open: total, completed: 0, closed: 0, total },
    agentSeconds: 0,
    waitingSeconds: 0,
    elapsed: { medianSeconds: null, longestSeconds: null, workflows: 0 },
    tokens: { billable: 0, measuredRuns: 0, unmeasuredRuns: 0 },
    runs: 0,
    teams: [],
    lastWorkedAt: null,
    backlog: { notStarted: 0, inProgress: 0, achieved: 0 },
    blockedSeconds: 0,
    efficiency: null,
    weekly: [],
    cost: { amount: null, currency: 'USD' },
  };
}

function outcome(id: string, name: string, status: OutcomeStatus, over: Partial<Outcome> = {}): Outcome {
  return {
    id,
    name,
    description: '',
    status,
    mergedInto: null,
    source: 'agent',
    targetMetric: null,
    targetUnit: null,
    targetValue: null,
    value: null,
    createdBy: 'ada@example.com',
    createdByKind: 'person',
    createdAt: '2026-09-01T00:00:00Z',
    updatedAt: '2026-09-01T00:00:00Z',
    confirmedBy: null,
    confirmedAt: null,
    figures: figures(0),
    ...over,
  };
}

const ProposedAt = '2026-09-30T14:05:00Z';

/** A Manager's proposal with three workflows linked, and a name that is markup only as text. */
const hiring = () => outcome('o-p', '<b>Hiring</b> pipeline', 'proposed', {
  createdBy: 'alpha/Manager',
  createdByKind: 'member',
  createdAt: ProposedAt,
  figures: figures(3),
});

/** A proposal nothing links yet: the one Reject is offered on. */
const triage = () => outcome('o-q', 'Faster triage', 'proposed', {
  createdBy: 'beta/Manager',
  createdByKind: 'member',
  createdAt: ProposedAt,
});

const pipeline = () => outcome('o-1', 'Current job pipeline', 'active');

/** The route's state: what each read answers and each write changes. */
let held: Outcome[] = [];

function detailOf(o: Outcome) {
  return {
    outcome: o,
    resolvedTo: null,
    mergedFrom: [],
    workflows: [],
    // Reject is offered only with no links: a linked proposal carries its link rows.
    history: Array.from({ length: o.figures.workflows.total }, (_, i) => ({
      id: i + 1,
      correlation: 100 + i,
      outcomeId: o.id,
      teamId: 'alpha',
      teamNameAtLink: 'alpha',
      outcomeNameAtLink: o.name,
      setBy: o.createdBy,
      setByKind: 'member',
      setAt: o.createdAt,
      how: 'manager',
    })),
    events: [],
    ledgerStartedAt: null,
  };
}

function settled(id: string, status: OutcomeStatus, over: Partial<Outcome> = {}) {
  held = held.map((o) => (o.id === id ? { ...o, status, ...over } : o));
  return held.find((o) => o.id === id)!;
}

const card = (id: string, laneId: string, status: string) =>
  ({
    id,
    team: 'alpha',
    member: 'DeveloperInes',
    item: null,
    workflowSeq: 2226,
    title: `Card ${id}`,
    body: '',
    status,
    laneId,
    progress: [],
    createdAt: '2026-09-18T00:00:00Z',
    updatedAt: '2026-09-18T00:00:00Z',
    awaitingManager: false,
    paused: false,
  }) as unknown as KanbanCard;

const lanes: KanbanLane[] = [
  { id: 'todo', title: 'To Do' },
  { id: 'blocked', title: 'Needs You' },
  { id: 'done', title: 'Done' },
];

let wrapper: VueWrapper | undefined;

beforeEach(() => {
  vi.stubGlobal('fetch', vi.fn(() => new Promise(() => {})));
  held = [hiring(), triage(), pipeline()];

  api.listOutcomes.mockReset().mockImplementation(async (query: OutcomeListQuery = {}) => ({
    ledgerStartedAt: null,
    outcomes: held.filter((o) => (query.status?.length ? query.status.includes(o.status) : o.status !== 'merged')),
    noOutcome: { name: 'No outcome', figures: figures(0) },
  }));
  api.listLiveOutcomes.mockReset().mockResolvedValue([]);
  api.getOutcome.mockReset().mockImplementation(async (id: string) => detailOf(held.find((o) => o.id === id)!));
  api.transitionOutcome.mockReset().mockImplementation(async (id: string, verb: string) =>
    settled(id, verb === 'confirm' ? 'active' : verb === 'retire' ? 'retired' : 'active'));
  api.previewMerge.mockReset().mockImplementation(async (id: string, into: string) => ({
    preview: true,
    from: { id, name: held.find((o) => o.id === id)!.name, status: 'proposed' },
    into: { id: into, name: held.find((o) => o.id === into)!.name, status: 'active' },
    refusal: null,
    moves: { links: 3, figures: figures(3) },
  }));
  api.mergeOutcome.mockReset().mockImplementation(async (id: string, into: string) => settled(id, 'merged', { mergedInto: into }));
  api.rejectOutcome.mockReset().mockImplementation(async (id: string) => {
    held = held.filter((o) => o.id !== id);
  });
});

afterEach(() => {
  wrapper?.unmount();
  wrapper = undefined;
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  resetBody();
});

async function mountBoard(cards: KanbanCard[] = [card('c1', 'blocked', 'blocked')], laneList = lanes) {
  setActivePinia(createPinia());
  const kanban = useKanbanStore();
  kanban.board = { lanes: laneList, cards };
  vi.spyOn(kanban, 'setFilters').mockImplementation(() => {});

  wrapper = mount(KanbanBoard, { attachTo: document.body });
  await flushPromises();
  await settle();
  return wrapper;
}

const entry = (id: string) => wrapper!.find(`[data-lane-id="blocked"] [data-needs-you-outcome="${id}"]`);
const needsYouCount = () => wrapper!.find('[data-lane-id="blocked"] .k-lane-count').text();

async function openEntry(id: string) {
  await entry(id).trigger('click');
  await settle();
}

async function click(selector: string) {
  (bodyFind(selector) as HTMLElement).click();
  await settle();
}

describe('Needs You lists the proposed outcomes', () => {
  it('reads the proposed outcomes from the outcomes route', async () => {
    await mountBoard();

    expect(api.listOutcomes).toHaveBeenCalledWith({ status: ['proposed'] });
  });

  it('shows each proposed outcome with its name, proposer, time and link count, as text', async () => {
    await mountBoard();

    const shown = entry('o-p');
    expect(shown.exists()).toBe(true);
    expect(shown.find('[data-proposed-name]').text()).toBe('<b>Hiring</b> pipeline');
    expect(shown.find('[data-proposed-name] b').exists()).toBe(false);
    expect(shown.find('[data-proposed-by]').text()).toBe('Proposed by alpha/Manager');
    expect(shown.find('[data-proposed-at]').text()).toBe(when(ProposedAt));
    expect(shown.find('[data-proposed-links]').text()).toBe('3 workflows linked');

    expect(entry('o-q').find('[data-proposed-links]').text()).toBe('0 workflows linked');

    // An active outcome waits on nobody, and no other lane lists outcomes.
    expect(entry('o-1').exists()).toBe(false);
    expect(wrapper!.findAll('[data-needs-you-outcome]')).toHaveLength(2);
  });

  it('keeps the lanes up for a proposal when the board has no card', async () => {
    await mountBoard([]);

    expect(entry('o-p').exists()).toBe(true);
    expect(needsYouCount()).toBe('2');
  });
});

describe("Needs You's count", () => {
  it('includes the proposed outcomes beside its cards', async () => {
    await mountBoard([card('c1', 'blocked', 'blocked'), card('c2', 'todo', 'queued')]);

    expect(needsYouCount()).toBe('3');
    expect(wrapper!.find('[data-lane-id="todo"] .k-lane-count').text()).toBe('1');
  });

  it('reads against the lane limit, amber once the proposals take it over', async () => {
    await mountBoard([card('c1', 'blocked', 'blocked')], [
      { id: 'todo', title: 'To Do' },
      { id: 'blocked', title: 'Needs You', wipLimit: 2 },
    ]);

    expect(needsYouCount()).toBe('3 / 2');
    expect(wrapper!.find('[data-lane-id="blocked"] .k-lane-head').classes()).toContain('k-lane-head--over');
  });
});

describe('an entry opens Manage outcomes on its outcome', () => {
  it('opens the dialog at that outcome', async () => {
    await mountBoard();
    expect(bodyFind('[data-outcomes-dialog]')).toBeNull();

    await openEntry('o-p');

    expect(bodyFind('[data-outcomes-dialog]')).not.toBeNull();
    expect(api.getOutcome).toHaveBeenCalledWith('o-p');
    expect(bodyFind('[data-outcome-detail]')?.getAttribute('data-outcome-id')).toBe('o-p');
    expect(bodyFind('[data-outcome-title]')?.textContent).toBe('<b>Hiring</b> pipeline');
    expect(wrapper!.emitted('open-outcome')).toEqual([['o-p']]);
  });
});

describe('a settled proposal leaves Needs You', () => {
  it('goes when a person confirms it', async () => {
    await mountBoard();
    await openEntry('o-p');

    await click('[data-outcome-action="confirm"]');

    expect(api.transitionOutcome).toHaveBeenCalledWith('o-p', 'confirm');
    expect(entry('o-p').exists()).toBe(false);
    expect(entry('o-q').exists()).toBe(true);
    expect(needsYouCount()).toBe('2');
  });

  it('goes when a person merges it into another outcome', async () => {
    await mountBoard();
    await openEntry('o-p');

    await click('[data-outcome-action="merge"]');
    const select = wrapper!
      .findAllComponents({ name: 'QSelect' })
      .find((candidate) => (candidate.props('options') as { value: unknown }[] | undefined)?.some((o) => o.value === 'o-1')
        && candidate.props('label') === 'Outcome');
    if (!select) throw new Error('No merge target select');
    select.vm.$emit('update:modelValue', 'o-1');
    await settle();
    await click('[data-outcome-merge-confirm]');

    expect(api.mergeOutcome).toHaveBeenCalledWith('o-p', 'o-1');
    expect(entry('o-p').exists()).toBe(false);
    expect(needsYouCount()).toBe('2');
  });

  it('goes when a person rejects it', async () => {
    await mountBoard();
    await openEntry('o-q');

    await click('[data-outcome-action="reject"]');

    expect(api.rejectOutcome).toHaveBeenCalledWith('o-q');
    expect(entry('o-q').exists()).toBe(false);
    expect(entry('o-p').exists()).toBe(true);
    expect(needsYouCount()).toBe('2');
  });

  it('goes when the board rereads after it was settled elsewhere', async () => {
    await mountBoard();
    const kanban = useKanbanStore();

    settled('o-p', 'retired');
    kanban.board = { lanes, cards: [card('c1', 'blocked', 'blocked')] };
    await flushPromises();
    await settle();

    expect(entry('o-p').exists()).toBe(false);
    expect(needsYouCount()).toBe('2');
  });
});
