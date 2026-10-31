// @vitest-environment happy-dom
//
// THE SUMMARY ABOVE THE TEAMS LIST: the period at the right of the Teams title, the four tiles from
// ONE call, each opening what it says, and the Cost column's Same scale switch, remembered in this
// browser.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';

const { getWip, listRemovals, getTeamsCost, getTeamCost, getCostSummary } = vi.hoisted(() => ({
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
}));

import TeamsView from '../TeamsView.vue';
import { useConsoleStore } from '../../stores/console';
import { useWorkflowNavStore } from '../../stores/workflowNav';
import type { TeamId } from '../../api/types';
import type { TeamCostOutcome, TeamsCost } from '../../api/outcomes';
import { resetBody } from '../../test/mountQuasar';
import { teamCost, Weeks } from '../../test/costFixtures';
import { costSummary } from '../../test/costSummaryFixtures';

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

/** A row's chart, as the paths it draws. */
const rowPaths = (view: VueWrapper, team: string) =>
  view.get(`[data-team="${team}"] [data-teams-cost]`).findAll('[data-cost-segment]').map((p) => p.attributes('d'));

describe('the summary above the teams list', () => {
  it('reads one call and shows the period at the right of the Teams title, then the four tiles above the list', async () => {
    const view = await mountView();

    expect(getCostSummary).toHaveBeenCalledTimes(1);
    expect(view.get('.teams-title').text()).toBe('Teams');
    expect(view.get('[data-teams-period]').text()).toBe('Last 8 weeks · Sep 7 – Nov 1');
    expect(view.findAll('[data-summary-tile]').map((t) => t.attributes('data-summary-tile')))
      .toEqual(['spend', 'budgets', 'where', 'now']);

    const html = view.html();
    expect(html.indexOf('data-cost-summary')).toBeLessThan(html.indexOf('teams-table'));
  });

  it('reads money in the cost route\'s currency', async () => {
    const view = await mountView();

    expect(view.get('[data-summary-tile="spend"] [data-summary-value]').text()).toBe('$2,200');
  });

  it('shows no tiles and no period when the summary was not read', async () => {
    getCostSummary.mockRejectedValue(new Error('down'));
    const view = await mountView();

    expect(view.find('[data-cost-summary]').exists()).toBe(false);
    expect(view.find('[data-teams-period]').exists()).toBe(false);
    expect(view.find('.teams-table').exists()).toBe(true);
  });

  it('opens Outcomes from Spend and Budgets, an outcome from its bar, and Needs you from waiting on you', async () => {
    const view = await mountView();
    const nav = useWorkflowNavStore();

    await view.get('[data-summary-tile="spend"]').trigger('click');
    expect(nav.outcomeAsks).toBe(1);
    expect(nav.outcomeAt).toBeNull();

    await view.get('[data-summary-where="o-billing"]').trigger('click');
    expect(nav.outcomeAsks).toBe(2);
    expect(nav.outcomeAt).toBe('o-billing');

    await view.get('[data-summary-tile="budgets"]').trigger('click');
    expect(nav.outcomeAsks).toBe(3);
    expect(nav.outcomeAt).toBeNull();

    await view.get('[data-summary-now="waiting"]').trigger('click');
    expect(nav.needsYouAsks).toBe(1);
  });
});

describe('the Cost column\'s Same scale switch', () => {
  it('is off by default: each row on its own scale', async () => {
    const view = await mountView();

    expect(view.get('[data-teams-cost-scale]').attributes('aria-checked')).toBe('false');
    // Alpha's weeks are half Beta's, yet on its own scale it fills its chart just as Beta does.
    expect(rowPaths(view, 'alpha')).toEqual(rowPaths(view, 'beta'));
  });

  it('on, puts every row on the largest team\'s scale, and is remembered in this browser', async () => {
    const view = await mountView();
    const ownScale = rowPaths(view, 'alpha');
    const beta = rowPaths(view, 'beta');
    const openTeam = vi.spyOn(useConsoleStore(), 'openTeam');

    await view.get('[data-teams-cost-scale]').trigger('click');
    await flushPromises();

    expect(view.get('[data-teams-cost-scale]').attributes('aria-checked')).toBe('true');
    expect(rowPaths(view, 'alpha')).not.toEqual(ownScale);
    expect(rowPaths(view, 'beta')).toEqual(beta);
    expect(localStorage.getItem('harness.teamsCostSameScale')).toBe('true');

    // Clicking the switch did not open a team.
    expect(openTeam).not.toHaveBeenCalled();
  });

  it('comes back on from this browser, and off again when switched off', async () => {
    localStorage.setItem('harness.teamsCostSameScale', 'true');
    const view = await mountView();

    expect(view.get('[data-teams-cost-scale]').attributes('aria-checked')).toBe('true');
    const shared = rowPaths(view, 'alpha');

    await view.get('[data-teams-cost-scale]').trigger('click');
    await flushPromises();

    expect(localStorage.getItem('harness.teamsCostSameScale')).toBe('false');
    expect(rowPaths(view, 'alpha')).not.toEqual(shared);
    expect(rowPaths(view, 'alpha')).toEqual(rowPaths(view, 'beta'));
  });
});
