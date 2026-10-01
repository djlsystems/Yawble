// @vitest-environment happy-dom
//
// MANAGE OUTCOMES. The tabs, each outcome's figures exactly as the route answers them with
// "+ N unmeasured runs", the "Accounting since" note, the always-shown No outcome row; and the
// actions: rename, confirm, retire and reactivate send their routes, Merge into… shows the route's
// preview before it merges, Reject is offered only with no links, and Move sends the link route.
//
// THE MOCK IS OF `api/outcomes`: what the Host does is pinned server-side (OutcomeTests).
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const api = vi.hoisted(() => ({
  listOutcomes: vi.fn(),
  getOutcome: vi.fn(),
  createOutcome: vi.fn(),
  editOutcome: vi.fn(),
  transitionOutcome: vi.fn(),
  previewMerge: vi.fn(),
  mergeOutcome: vi.fn(),
  rejectOutcome: vi.fn(),
  linkWorkflowOutcome: vi.fn(),
}));

vi.mock('../../api/outcomes', () => api);

import OutcomesDialog from '../OutcomesDialog.vue';
import type { Outcome, OutcomeDetail, OutcomeFigures, OutcomeList } from '../../api/outcomes';
import { bodyFind, bodyText, mountDialog, resetBody } from '../../test/mountQuasar';
import { button, settle, type } from '../../test/formProbe';
import type { VueWrapper } from '@vue/test-utils';

function figures(over: Partial<OutcomeFigures> = {}): OutcomeFigures {
  return {
    workflows: { open: 1, completed: 4, closed: 1, total: 6 },
    agentSeconds: 3 * 3600 + 5 * 60,
    waitingSeconds: 90,
    elapsed: { medianSeconds: 45 * 60, longestSeconds: 2 * 3600, workflows: 5 },
    tokens: { billable: 1_234_567, measuredRuns: 10, unmeasuredRuns: 2 },
    runs: 12,
    teams: [
      { id: 'alpha', name: 'Alpha', deleted: false },
      { id: 'gone', name: 'Old crew', deleted: true },
    ],
    lastWorkedAt: '2026-09-28T10:00:00Z',
    ...over,
  };
}

function outcome(id: string, name: string, status: Outcome['status'], over: Partial<Outcome> = {}): Outcome {
  return {
    id,
    name,
    description: `${name} described\nsecond line`,
    status,
    mergedInto: null,
    source: 'person',
    targetMetric: null,
    targetUnit: null,
    targetValue: null,
    createdBy: 'ada@example.com',
    createdByKind: 'person',
    createdAt: '2026-09-01T00:00:00Z',
    updatedAt: '2026-09-01T00:00:00Z',
    confirmedBy: null,
    confirmedAt: null,
    figures: figures(),
    ...over,
  };
}

const pipeline = outcome('o-1', 'Current job pipeline', 'active');
const proposal = outcome('o-2', '<b>Faster</b> triage', 'proposed', {
  createdByKind: 'member',
  createdBy: 'ops/Manager',
  figures: figures({ tokens: { billable: 0, measuredRuns: 0, unmeasuredRuns: 3 } }),
});
const retired = outcome('o-3', 'Old goal', 'retired');
const merged = outcome('o-4', 'Duplicate goal', 'merged', { mergedInto: 'o-1' });

function listing(over: Partial<OutcomeList> = {}): OutcomeList {
  return {
    ledgerStartedAt: '2026-08-15T00:00:00Z',
    outcomes: [pipeline, proposal, retired, merged],
    noOutcome: {
      name: 'No outcome',
      figures: figures({
        workflows: { open: 0, completed: 7, closed: 0, total: 7 },
        tokens: { billable: 5000, measuredRuns: 4, unmeasuredRuns: 1 },
        teams: [{ id: 'beta', name: 'Beta', deleted: false }],
      }),
    },
    ...over,
  };
}

function detailOf(o: Outcome, links = 1): OutcomeDetail {
  return {
    outcome: o,
    resolvedTo: null,
    mergedFrom: [],
    workflows: links
      ? [
          {
            correlation: 4100,
            team: { id: 'alpha', name: 'Alpha', deleted: false },
            state: 'completed',
            startedAt: '2026-09-20T09:00:00Z',
            elapsedSeconds: 3600,
            agentSeconds: 5400,
            billableTokens: 20_000,
            unmeasuredRuns: 1,
            how: 'dispatch',
            setBy: 'ada@example.com',
            setByKind: 'person',
            setAt: '2026-09-20T09:00:00Z',
          },
        ]
      : [],
    history: links
      ? [
          {
            id: 1,
            correlation: 4100,
            outcomeId: o.id,
            teamId: 'alpha',
            teamNameAtLink: 'Alpha',
            outcomeNameAtLink: o.name,
            setBy: 'ada@example.com',
            setByKind: 'person',
            setAt: '2026-09-20T09:00:00Z',
            how: 'dispatch',
          },
        ]
      : [],
    events: [
      { seq: 10, at: '2026-09-01T08:00:00Z', action: 'outcome.created', outcomeId: o.id, name: 'Job pipeline', from: null, by: 'ada@example.com', detail: null },
      { seq: 11, at: '2026-09-02T08:00:00Z', action: 'outcome.renamed', outcomeId: o.id, name: o.name, from: 'Job pipeline', by: 'grace@example.com', detail: { name: o.name } },
      { seq: 12, at: '2026-09-03T08:00:00Z', action: 'outcome.retired', outcomeId: o.id, name: o.name, from: null, by: 'grace@example.com', detail: null },
      { seq: 13, at: '2026-09-04T08:00:00Z', action: 'outcome.reactivated', outcomeId: o.id, name: o.name, from: null, by: 'ada@example.com', detail: null },
    ],
    ledgerStartedAt: '2026-08-15T00:00:00Z',
  };
}

beforeEach(() => {
  for (const mock of Object.values(api)) mock.mockReset();
  api.listOutcomes.mockResolvedValue(listing());
  api.getOutcome.mockImplementation(async (id: string) => {
    const found = listing().outcomes.find((o) => o.id === id)!;
    return detailOf(found);
  });
  api.editOutcome.mockResolvedValue(pipeline);
  api.transitionOutcome.mockResolvedValue(pipeline);
  api.mergeOutcome.mockResolvedValue(pipeline);
  api.linkWorkflowOutcome.mockResolvedValue({});
});

afterEach(() => {
  resetBody();
});

async function open(props: Record<string, unknown> = {}) {
  const wrapper = await mountDialog(OutcomesDialog, props);
  await settle();
  return wrapper;
}

async function click(selector: string) {
  (bodyFind(selector) as HTMLElement).click();
  await settle();
}

/** Picks the option reading `option` in the q-select labelled `label`, as a person's choice does. */
async function choose(wrapper: VueWrapper, label: string, option: string) {
  const select = wrapper.findAllComponents({ name: 'QSelect' }).find((candidate) => candidate.props('label') === label);
  if (!select) throw new Error(`No select labelled "${label}"`);
  const offered = (select.props('options') as { label: string; value: unknown }[]).find((o) => o.label.includes(option));
  if (!offered) throw new Error(`No option "${option}" in "${label}"`);
  select.vm.$emit('update:modelValue', offered.value);
  await settle();
}

const row = (id: string) => bodyFind(`[data-outcome-row="${id}"]`)?.textContent ?? '';

describe('Manage Outcomes: tabs and figures', () => {
  it('shows Active, Proposed (n) and Retired & merged, each with its outcomes', async () => {
    await open();

    expect(bodyFind('[data-outcome-tab="active"]')?.textContent).toContain('Active');
    expect(bodyFind('[data-outcome-tab="proposed"]')?.textContent).toContain('Proposed (1)');
    expect(bodyFind('[data-outcome-tab="ended"]')?.textContent).toContain('Retired & merged');

    expect(row('o-1')).toContain('Current job pipeline');
    expect(bodyFind('[data-outcome-row="o-2"]')).toBeNull();

    await click('[data-outcome-tab="proposed"]');
    expect(row('o-2')).toContain('<b>Faster</b> triage');
    expect(bodyFind('[data-outcome-row="o-1"]')).toBeNull();

    await click('[data-outcome-tab="ended"]');
    expect(row('o-3')).toContain('Old goal');
    expect(row('o-4')).toContain('merged into Current job pipeline');
    expect(bodyFind('[data-no-outcome-row]')).toBeNull();
  });

  it('asks the route once for every status, and each table has the eight resizable columns', async () => {
    await open();

    expect(api.listOutcomes).toHaveBeenCalledWith({ status: ['proposed', 'active', 'retired', 'merged'], from: null, to: null });

    const headers = [...document.body.querySelectorAll('[data-outcomes-table] thead th')] as HTMLElement[];
    expect(headers.map((th) => th.textContent?.trim())).toEqual([
      'Outcome', 'Teams', 'Workflows', 'Agent time', 'Waiting', 'Elapsed', 'Tokens', 'Last worked',
    ]);
    expect(headers.every((th) => th.querySelector('.os-col-resizer'))).toBe(true);
  });

  it('shows each figure as the route answers it, with "+ N unmeasured runs" and a gone team named deleted', async () => {
    await open();

    const text = row('o-1');
    expect(text).toContain('Current job pipeline described');
    expect(text).not.toContain('second line');
    expect(text).toContain('Alpha, Old crew (deleted)');
    expect(text).toContain('1 open · 4/6');
    expect(text).toContain('3h 5m');
    expect(text).toContain('1m');
    expect(text).toContain('45m · 2h');
    expect(text).toContain('1.2M');
    expect(text).toContain('+ 2 unmeasured runs');

    expect(bodyFind('[data-outcome-figures-note]')?.textContent).toContain('can make it exceed elapsed');
    expect(bodyFind('[data-outcome-figures-note]')?.textContent).toContain('never summed');
  });

  it('never shows an unmeasured run as 0 tokens', async () => {
    await open();
    await click('[data-outcome-tab="proposed"]');

    const tokens = bodyFind('[data-outcome-row="o-2"] [data-outcome-tokens]')?.textContent ?? '';
    expect(tokens).toContain('not measured');
    expect(tokens).toContain('+ 3 unmeasured runs');
    expect(tokens).not.toMatch(/(^|\s)0(\s|$)/);
  });

  it('renders an outcome name as text, never as markup', async () => {
    await open();
    await click('[data-outcome-tab="proposed"]');

    expect(bodyFind('[data-outcome-row="o-2"] b')).toBeNull();
    expect(row('o-2')).toContain('<b>Faster</b>');
  });

  it('closes Active with the No outcome row and its own figures, even with no outcomes', async () => {
    api.listOutcomes.mockResolvedValue(listing({ outcomes: [] }));
    await open();

    const none = bodyFind('[data-no-outcome-row]')?.textContent ?? '';
    expect(none).toContain('No outcome');
    expect(none).toContain('Beta');
    expect(none).toContain('0 open · 7/7');
    expect(none).toContain('5.0K');
    expect(none).toContain('+ 1 unmeasured run');

    const rows = [...document.body.querySelectorAll('[data-outcomes-table] tbody tr')];
    expect(rows.at(-1)?.hasAttribute('data-no-outcome-row')).toBe(true);
  });

  it('says "Accounting since" when the period reaches before the ledger began, and not after it', async () => {
    const wrapper = await open();
    const note = bodyFind('[data-accounting-since]')?.textContent ?? '';
    expect(note).toContain('Accounting since');
    expect(note).toContain('runs deleted by a Reset before then are not included');

    // A ledger that began before the last 30 days: that period does not reach before it.
    api.listOutcomes.mockResolvedValue(listing({ ledgerStartedAt: '2020-01-01T00:00:00Z' }));
    await choose(wrapper, 'Period', 'Last 30 days');
    expect(api.listOutcomes).toHaveBeenLastCalledWith(expect.objectContaining({ from: expect.any(String), to: null }));
    expect(bodyFind('[data-accounting-since]')).toBeNull();
  });
});

describe('Manage Outcomes: an outcome opened', () => {
  it('opens at the outcome it was given, with editable details and the target note', async () => {
    await open({ outcome: 'o-1' });

    expect(api.getOutcome).toHaveBeenCalledWith('o-1');
    expect(bodyFind('[data-outcome-detail]')?.getAttribute('data-outcome-id')).toBe('o-1');
    expect(bodyFind('[data-outcome-title]')?.textContent).toBe('Current job pipeline');
    expect(bodyFind('[data-outcome-target-note]')?.textContent).toContain('nothing measures it yet');
    expect(bodyFind('[data-outcome-workflow="4100"]')?.textContent).toContain('Alpha');
    expect(bodyFind('[data-outcome-workflow="4100"]')?.textContent).toContain('+ 1 unmeasured run');
    expect(bodyFind('[data-outcome-history]')?.textContent).toContain('Workflow 4100 (Alpha) linked');
  });

  it('lists renames and status changes from the route, each with who and when', async () => {
    await open({ outcome: 'o-1' });

    const lines = [...document.body.querySelectorAll('[data-outcome-history] li')].map((li) => li.textContent ?? '');
    const renamed = lines.find((l) => l.includes('Renamed from "Job pipeline" to "Current job pipeline"'));
    expect(renamed).toContain('by grace@example.com');
    expect(renamed).toContain(new Date('2026-09-02T08:00:00Z').toLocaleString());
    const retired = lines.find((l) => l.includes('Retired'));
    expect(retired).toContain('by grace@example.com');
    expect(retired).toContain(new Date('2026-09-03T08:00:00Z').toLocaleString());
    expect(lines.find((l) => l.includes('Reactivated'))).toContain('by ada@example.com');
    // Oldest first, links among them by time.
    expect(lines.findIndex((l) => l.includes('Retired'))).toBeLessThan(lines.findIndex((l) => l.includes('Workflow 4100')));
  });

  it('renames through the edit route, sending only what changed', async () => {
    await open({ outcome: 'o-1' });

    await type('Name', 'Qualified job pipeline');

    await click('[data-outcome-save]');
    expect(api.editOutcome).toHaveBeenCalledWith('o-1', { name: 'Qualified job pipeline' });
  });

  it('confirms a proposal, retires an active outcome and reactivates a retired one through their routes', async () => {
    await open({ outcome: 'o-2' });
    await click('[data-outcome-action="confirm"]');
    expect(api.transitionOutcome).toHaveBeenCalledWith('o-2', 'confirm');

    resetBody();
    await open({ outcome: 'o-1' });
    expect(bodyFind('[data-outcome-action="confirm"]')).toBeNull();
    await click('[data-outcome-action="retire"]');
    expect(api.transitionOutcome).toHaveBeenCalledWith('o-1', 'retire');

    resetBody();
    await open({ outcome: 'o-3' });
    expect(bodyFind('[data-outcome-action="retire"]')).toBeNull();
    expect(bodyFind('[data-outcome-action="merge"]')).toBeNull();
    await click('[data-outcome-action="reactivate"]');
    expect(api.transitionOutcome).toHaveBeenCalledWith('o-3', 'reactivate');
  });

  it('offers Reject on a proposal only when no link names it', async () => {
    await open({ outcome: 'o-2' });
    expect(bodyFind('[data-outcome-action="reject"]')).toBeNull();

    resetBody();
    api.getOutcome.mockResolvedValue(detailOf(proposal, 0));
    await open({ outcome: 'o-2' });
    await click('[data-outcome-action="reject"]');
    expect(api.rejectOutcome).toHaveBeenCalledWith('o-2');
  });

  it('shows a merged outcome read-only, naming what it was merged into', async () => {
    await open({ outcome: 'o-4' });

    expect(bodyFind('[data-outcome-merged]')?.textContent).toContain('Merged into Current job pipeline');
    expect(bodyFind('[data-outcome-save]')).toBeNull();
    expect(document.body.querySelectorAll('[data-outcome-action]').length).toBe(0);
    expect(bodyFind('[data-outcome-move]')).toBeNull();
  });

  it('shows the merge preview from the route before merging, and merges only on confirm', async () => {
    api.previewMerge.mockResolvedValue({
      preview: true,
      from: { id: 'o-2', name: '<b>Faster</b> triage', status: 'proposed' },
      into: { id: 'o-1', name: 'Current job pipeline', status: 'active' },
      refusal: null,
      moves: {
        links: 12,
        figures: figures({
          workflows: { open: 0, completed: 12, closed: 0, total: 12 },
          teams: [
            { id: 'a', name: 'A', deleted: false },
            { id: 'b', name: 'B', deleted: false },
            { id: 'c', name: 'C', deleted: false },
          ],
          tokens: { billable: 1_200_000, measuredRuns: 20, unmeasuredRuns: 0 },
          agentSeconds: 18 * 3600,
        }),
      },
    });
    const wrapper = await open({ outcome: 'o-2' });

    await click('[data-outcome-action="merge"]');
    expect(bodyFind('[data-outcome-merge-preview]')).toBeNull();
    expect(button('Merge').closest('[disabled], .disabled')).not.toBeNull();

    await choose(wrapper, 'Outcome', 'Current job pipeline');
    expect(api.previewMerge).toHaveBeenCalledWith('o-2', 'o-1');
    expect(api.mergeOutcome).not.toHaveBeenCalled();
    expect(bodyFind('[data-outcome-merge-preview]')?.textContent).toBe(
      '12 workflows, 3 teams, 1.2M tokens, 18h agent time move to Current job pipeline.',
    );

    await click('[data-outcome-merge-confirm]');
    expect(api.mergeOutcome).toHaveBeenCalledWith('o-2', 'o-1');
  });

  it('moves a workflow to another outcome through the link route', async () => {
    const wrapper = await open({ outcome: 'o-1' });

    await click('[data-outcome-move]');
    await choose(wrapper, 'To outcome', 'Faster');
    await click('[data-outcome-move-confirm]');

    expect(api.linkWorkflowOutcome).toHaveBeenCalledWith('alpha', 4100, 'o-2');
  });

  it('creates a new outcome through the create route', async () => {
    api.createOutcome.mockResolvedValue(pipeline);
    await open();

    await click('[data-outcome-new]');
    await type('Name', 'Current job pipeline');
    await click('[data-outcome-create-confirm]');

    expect(api.createOutcome).toHaveBeenCalledWith({ name: 'Current job pipeline', description: '' });
    expect(bodyText()).toContain('Current job pipeline');
  });
});
