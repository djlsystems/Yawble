// @vitest-environment happy-dom
//
// THE SUMMARY ABOVE THE TEAMS LIST: the shared period control at the right of the Teams title, then the three
// tiles, each opening what it says. Estimated Execution Cost and Where it goes are the period's
// (`GET /api/teams/cost/period`); Right now is the summary's (`GET /api/teams/cost/summary`). No Budgets tile.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';

const { getWip, listRemovals, getTeamsCost, getTeamCost, getCostSummary, getTeamsCostPeriod } = vi.hoisted(() => ({
  getTeamsCostPeriod: vi.fn(),
  getWip: vi.fn(),
  listRemovals: vi.fn(),
  getTeamsCost: vi.fn(),
  getTeamCost: vi.fn(),
  getCostSummary: vi.fn(),
}));

vi.mock('quasar', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  useQuasar: () => ({ notify: vi.fn(), dark: { isActive: false } }),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  getWip,
  listRemovals,
}));

vi.mock('../../api/outcomes', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  getTeamsCost,
  getTeamCost,
  getCostSummary,
  getTeamsCostPeriod,
}));

import TeamsView from '../TeamsView.vue';
import { useConsoleStore } from '../../stores/console';
import { useWorkflowNavStore } from '../../stores/workflowNav';
import type { TeamId } from '../../api/types';
import type { TeamCostOutcome, TeamsCost } from '../../api/outcomes';
import { resetBody } from '../../test/mountQuasar';
import { teamCost, Weeks } from '../../test/costFixtures';
import { costSummary } from '../../test/costSummaryFixtures';
import { teamsCostPeriod } from '../../test/costPeriodFixtures';
import { budgetWordsOn, misnamedOnTeamsList, readWords } from '../../test/estimateLabels';

/** Every figure of an outcome times `by`: Beta works twice as much as Alpha. */
function scaled(outcome: TeamCostOutcome, by: number): TeamCostOutcome {
  return {
    ...outcome,
    weekly: outcome.weekly.map((w) => ({ ...w, agentSeconds: w.agentSeconds * by, cost: w.cost === null ? null : w.cost * by })),
  };
}

function teamsCost(): TeamsCost {
  const alpha = teamCost({ team: 'alpha' });
  const beta = teamCost({ team: 'beta' });
  return {
    money: alpha.money,
    weeks: Weeks,
    teams: [
      { team: 'alpha', outcomes: alpha.outcomes, totals: alpha.totals },
      { team: 'beta', outcomes: beta.outcomes.map((o) => scaled(o, 2)), totals: beta.totals },
    ],
  };
}

let wrapper: VueWrapper | undefined;

beforeEach(() => {
  localStorage.clear();
  getWip.mockResolvedValue({ running: [], waiting: [], limit: null });
  listRemovals.mockResolvedValue([]);
  getTeamsCost.mockResolvedValue(teamsCost());
  getCostSummary.mockResolvedValue(costSummary());
  getTeamsCostPeriod.mockResolvedValue(teamsCostPeriod());
  vi.stubGlobal('fetch', vi.fn(async () => new Response('{}', { status: 404 })));
});

afterEach(() => {
  wrapper?.unmount();
  wrapper = undefined;
  vi.unstubAllGlobals();
  vi.clearAllMocks();
  localStorage.clear();
  resetBody();
});

async function mountView() {
  setActivePinia(createPinia());
  const board = useConsoleStore();
  board.$patch({
    teams: [
      { id: 'alpha' as TeamId, name: 'Alpha', paused: false, containers: [] },
      { id: 'beta' as TeamId, name: 'Beta', paused: false, containers: [] },
    ],
  } as never);
  wrapper = mount(TeamsView, { attachTo: document.body });
  await flushPromises();
  return wrapper;
}

describe('the summary above the teams list', () => {
  it('reads one call and shows the period control at the right of the Teams title, then the three tiles above the list', async () => {
    const view = await mountView();

    expect(getCostSummary).toHaveBeenCalledTimes(1);
    expect(getTeamsCostPeriod).toHaveBeenCalledTimes(1);
    expect(view.get('.teams-title').text()).toBe('Teams');
    expect(view.get('[data-teams-period]').text()).toMatch(/^Last two months · .+ UTC$/);
    expect(view.findAll('[data-summary-tile]').map((t) => t.attributes('data-summary-tile')))
      .toEqual(['spend', 'where', 'now']);

    const html = view.html();
    expect(html.indexOf('data-teams-period-control')).toBeLessThan(html.indexOf('data-cost-summary'));
    expect(html.indexOf('data-cost-summary')).toBeLessThan(html.indexOf('teams-table'));
  });

  it('shows the period\'s Estimated Execution Cost with its Agent Hours, its change and its days', async () => {
    const view = await mountView();

    const tile = view.get('[data-summary-tile="spend"]');
    expect(tile.get('[data-summary-value]').text()).toBe('$2,430');
    expect(tile.get('[data-summary-detail]').text()).toBe('40.5 Agent Hours at $60/h');
    expect(tile.get('[data-summary-change]').text()).toBe('▲ 100% vs prior period');
    expect(tile.get('[data-summary-aside]').text()).toBe('by day');
    expect(tile.findAll('[data-cost-week]').length).toBe(3);
  });

  it('reads Estimate unavailable with the period\'s Agent Hours when nothing was priced', async () => {
    getTeamsCostPeriod.mockResolvedValue(teamsCostPeriod({ rate: null }));
    const view = await mountView();

    const tile = view.get('[data-summary-tile="spend"]');
    expect(tile.get('[data-summary-value]').text()).toBe('Estimate unavailable');
    expect(tile.get('[data-summary-detail]').text()).toBe('40.5 Agent Hours');
    expect(tile.text()).not.toContain('$');
  });

  it('shows Where it goes for the period, labelled by outcome, with Unattributed for work serving none', async () => {
    const view = await mountView();

    const where = view.get('[data-summary-tile="where"]');
    expect(where.findAll('[data-summary-where]').map((b) => b.attributes('data-summary-where')))
      .toEqual(['o-billing', 'o-docs', 'o-research', 'unattributed']);
    expect(where.get('[data-summary-where="o-billing"] [data-summary-where-amount]').text()).toBe('$1,080');
    expect(where.get('[data-summary-where="unattributed"] [data-summary-where-amount]').text()).toBe('$540');
  });

  it('keeps Right now on the summary\'s own figures, and shows no Budgets tile or budget word though the answers carry budgets', async () => {
    const view = await mountView();

    expect(view.get('[data-summary-now="waiting"]').text()).toContain('3');
    expect(view.find('[data-summary-tile="budgets"]').exists()).toBe(false);
    expect(budgetWordsOn(readWords(view.get('[data-cost-summary]').element))).toEqual([]);
    expect(view.text()).not.toContain('Give an outcome a value to track it here.');
  });

  it('never names the period\'s tiles spend, a value word, or Cost alone', async () => {
    for (const options of [{}, { rate: null }]) {
      getTeamsCostPeriod.mockResolvedValue(teamsCostPeriod(options));
      const view = await mountView();
      for (const tile of ['spend', 'where']) {
        const element = view.get(`[data-summary-tile="${tile}"]`).element;
        const title = element.querySelector('.cost-summary-title')?.textContent ?? '';
        expect(misnamedOnTeamsList(readWords(element), [title])).toEqual([]);
      }
      view.unmount();
    }
  });

  it('falls back to the summary\'s eight weeks while the period has not been read', async () => {
    getTeamsCostPeriod.mockReturnValue(new Promise(() => {}));
    const view = await mountView();

    expect(view.get('[data-summary-tile="spend"] [data-summary-value]').text()).toBe('$2,200');
  });

  it('shows no tiles when the summary was not read, and keeps the period control', async () => {
    getCostSummary.mockRejectedValue(new Error('down'));
    const view = await mountView();

    expect(view.find('[data-cost-summary]').exists()).toBe(false);
    expect(view.find('[data-teams-period-control]').exists()).toBe(true);
    expect(view.find('.teams-table').exists()).toBe(true);
  });

  it('opens Outcomes from Estimated Execution Cost, an outcome from its bar, and Needs you from waiting on you', async () => {
    const view = await mountView();
    const nav = useWorkflowNavStore();

    await view.get('[data-summary-tile="spend"]').trigger('click');
    expect(nav.outcomeAsks).toBe(1);
    expect(nav.outcomeAt).toBeNull();

    await view.get('[data-summary-where="o-billing"]').trigger('click');
    expect(nav.outcomeAsks).toBe(2);
    expect(nav.outcomeAt).toBe('o-billing');

    await view.get('[data-summary-now="waiting"]').trigger('click');
    expect(nav.needsYouAsks).toBe(1);
  });
});
