// @vitest-environment happy-dom
//
// THE TEAMS LIST'S ESTIMATED EXECUTION COST AND AGENT HOURS COLUMNS over the shared period: each team's stacked
// chart by outcome at row height, every row on ONE relative scale, its period total and Agent Hours beside it and
// ⚠ when one of its outcomes is over budget, all from ONE call for the whole list; a click on the chart opens that
// team's Cost dialog rather than the team. Same scale and its stored key are gone; both figures sort.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';

const { getWip, listRemovals, getTeamsCost, getTeamCost, getTeamTokens, getTeamsCostPeriod, getTeamCostPeriod } = vi.hoisted(() => ({
  getTeamCostPeriod: vi.fn(),
  getWip: vi.fn(),
  listRemovals: vi.fn(),
  getTeamsCost: vi.fn(),
  getTeamCost: vi.fn(),
  getTeamTokens: vi.fn(),
  getTeamsCostPeriod: vi.fn(),
}));

vi.mock('quasar', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  useQuasar: () => ({ notify: vi.fn(), dark: { isActive: false } }),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  getWip,
  listRemovals,
  getTeamTokens,
  getTeamTokenRuns: () => new Promise(() => {}),
}));

vi.mock('../../api/outcomes', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  getTeamsCost,
  getTeamCost,
  getTeamsCostPeriod,
  getTeamCostPeriod,
}));

import TeamsView from '../TeamsView.vue';
import TeamCostDialog from '../TeamCostDialog.vue';
import { useConsoleStore } from '../../stores/console';
import { asTeamId, type TeamId, type TeamTokenTotals } from '../../api/types';
import type { TeamsCost } from '../../api/outcomes';
import { resetBody } from '../../test/mountQuasar';
import { teamCost, Weeks, type CostOptions } from '../../test/costFixtures';
import { misnamedOnTeamsList, readOff } from '../../test/estimateLabels';
import { teamsCostPeriod } from '../../test/costPeriodFixtures';
import { teamCostPeriod } from '../../test/teamPeriodFixtures';
import { lastTwoMonths } from '../../lib/teamsPeriod';
import CostWeeksChart from '../CostWeeksChart.vue';

const teamId = asTeamId('alpha');
const iso = (minutes: number) => new Date(Date.UTC(2026, 9, 7, 14, minutes)).toISOString();

const container = (id: string) => ({
  team: teamId, id, name: id, agent: 'claude-headless', state: 'Idle', queueDepth: 0, ceiling: 16,
  subscribes: [], currentCorrelation: null, sinceSeq: 0,
});

const members = [container('Manager'), container('Developer')];

/** `GET /api/teams/cost` for Alpha and Beta, Beta's Docs refresh over budget when asked. */
function teamsCost(options: CostOptions & { betaOver?: boolean } = {}): TeamsCost {
  const alpha = teamCost({ ...options, team: 'alpha' });
  const beta = teamCost({ ...options, team: 'beta', over: options.betaOver ?? false });
  return {
    money: alpha.money,
    weeks: Weeks,
    teams: [alpha, beta].map(({ team, outcomes, totals }) => ({ team, outcomes, totals })),
  };
}

let wrapper: VueWrapper | undefined;

beforeEach(() => {
  localStorage.clear();
  getTeamsCostPeriod.mockResolvedValue(teamsCostPeriod());
  getTeamCostPeriod.mockResolvedValue(teamCostPeriod());
  getWip.mockResolvedValue({ running: [], waiting: [], limit: null });
  listRemovals.mockResolvedValue([]);
  getTeamsCost.mockResolvedValue(teamsCost({ betaOver: true }));
  getTeamTokens.mockResolvedValue({
    available: true, tokensIn: 9, tokensOut: 4, tokensCachedIn: 0, tokensCacheCreation: 0, tokensBillable: 13,
    partial: false, runsWithUsage: 1, runsWithoutUsage: 0, missing: null, members: [],
  } as unknown as TeamTokenTotals);
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

async function mountView(latest: string | null = null, roster = members) {
  setActivePinia(createPinia());
  const board = useConsoleStore();
  board.$patch({
    teams: [
      { id: 'alpha' as TeamId, name: 'Alpha', paused: false, containers: roster },
      { id: 'beta' as TeamId, name: 'Beta', paused: false, containers: roster },
      { id: 'gamma' as TeamId, name: 'Gamma', paused: false, containers: roster },
    ],
  } as never);
  if (latest !== null) {
    board.$patch({
      workflows: {
        alpha: {
          available: true, openCount: 0, totalCount: 1, earliestStartedAt: null, serverNow: iso(30),
          workflows: [{
            available: true, correlation: 1, state: latest, startedAt: iso(0), endedAt: iso(30), serverNow: iso(30),
            executionSeconds: 0, partial: false, runsCounted: 0, runsUnfinished: 0, blockedBy: null, runsFailed: 0,
            failedMembers: [], missing: null, members: [], lastActivityAt: iso(30), awaitingFrom: null, subject: null,
            pausedAt: null, pausedReason: null, pausedLimit: null,
          }],
          missing: null,
        },
      },
    } as never);
  }
  vi.spyOn(board, 'refresh').mockResolvedValue();
  vi.spyOn(board, 'refreshRollupIfShowing').mockImplementation(() => {});
  const setActive = vi.spyOn(board, 'setActiveTeam').mockResolvedValue(undefined as never);

  wrapper = mount(TeamsView, { attachTo: document.body });
  await flushPromises();
  return { view: wrapper, setActive };
}

const row = (view: VueWrapper, id: string) => view.find(`tr[data-team="${id}"]`);

const order = (view: VueWrapper) => view.findAll('tbody tr[data-team]').map((tr) => tr.attributes('data-team'));
const paths = (view: VueWrapper, id: string) => row(view, id).findAll('[data-cost-segment]').map((p) => p.attributes('d'));

describe('the Estimated Execution Cost column over the period', () => {
  it('is headed Estimated Execution Cost, with Agent Hours beside it, both sortable, and no Same scale', async () => {
    const { view } = await mountView();

    const headings = view.findAll('thead th').map((th) => th.text().replace(/arrow_(up|down)ward/, '').replace(/\s+/g, ' ').trim());
    expect(headings).toEqual(['Team', 'Members', 'Status', 'Estimated Execution Cost', 'Agent Hours', '']);
    expect(view.find('thead [aria-label="Sort by Estimated Execution Cost"]').exists()).toBe(true);
    expect(view.find('thead [aria-label="Sort by Agent Hours"]').exists()).toBe(true);
    expect(view.find('[data-teams-cost-scale]').exists()).toBe(false);
    expect(view.text()).not.toContain('Same scale');
    expect(view.find('thead th[data-col="activity"]').exists()).toBe(false);
    expect(localStorage.getItem('harness.teamsCostSameScale')).toBeNull();
  });

  it('reads every team\'s figures for the period in ONE call, the last two months by day unless chosen, never one per row', async () => {
    await mountView();

    expect(getTeamsCostPeriod).toHaveBeenCalledTimes(1);
    expect(getTeamsCostPeriod).toHaveBeenCalledWith(lastTwoMonths(), 'day');
    expect(getTeamsCost).toHaveBeenCalledTimes(1);
    expect(getTeamCost).not.toHaveBeenCalled();
  });

  it('draws each team\'s stacked chart by outcome at row height, with the route\'s total and Agent Hours beside it', async () => {
    const { view } = await mountView();

    const cell = row(view, 'alpha').find('[data-teams-cost]');
    expect(cell.find('[data-cost-chart]').attributes('data-size')).toBe('row');
    expect(cell.findAll('[data-cost-segment][data-week="0"]').map((s) => s.attributes('data-cost-segment')))
      .toEqual(['none', 'o-billing', 'o-docs', 'o-research']);
    expect(cell.find('[data-teams-cost-total]').text()).toBe('$810');
    expect(row(view, 'alpha').find('[data-teams-hours]').text()).toBe('13.5');
    expect(row(view, 'beta').find('[data-teams-cost-total]').text()).toBe('$1,620');
    expect(row(view, 'beta').find('[data-teams-hours]').text()).toBe('27.0');
  });

  it('puts every row on ONE relative scale: the tallest day of any team', async () => {
    const { view } = await mountView();

    const charts = view.findAllComponents(CostWeeksChart).filter((c) => c.props('size') === 'row');
    // Gamma ran nothing: its chart is empty, on the same scale.
    expect(charts.length).toBe(3);
    // Beta's day is $540, twice Alpha's: one top for all, so Alpha's bars stand half as tall.
    for (const chart of charts) expect(chart.props('scaleMax')).toBe(540);
    expect(paths(view, 'alpha')).not.toEqual(paths(view, 'beta'));
  });

  it('reads a team with no runs as a dash and 0.0 Agent Hours, and one never priced as Estimate unavailable', async () => {
    const { view } = await mountView();
    expect(row(view, 'gamma').find('[data-teams-cost-total]').text()).toBe('—');
    expect(row(view, 'gamma').find('[data-teams-hours]').text()).toBe('0.0');

    getTeamsCostPeriod.mockResolvedValue(teamsCostPeriod({ rate: null }));
    view.unmount();
    const again = (await mountView()).view;
    expect(row(again, 'alpha').find('[data-teams-cost-total]').text()).toBe('Estimate unavailable');
    expect(row(again, 'alpha').find('[data-teams-hours]').text()).toBe('13.5');
    expect(row(again, 'alpha').find('[data-teams-cost]').text()).not.toContain('$');
  });

  it('marks a total priced in part from a reconstructed rate with its footnote', async () => {
    getTeamsCostPeriod.mockResolvedValue(teamsCostPeriod({ legacy: true }));
    const { view } = await mountView();

    const total = row(view, 'alpha').get('[data-teams-cost-total]');
    expect(total.text()).toBe('$810*');
    expect(total.attributes('title')).toBe('* Includes reconstructed rates');
  });

  it('gives one outcome one colour on every row', async () => {
    const answer = teamsCostPeriod();
    answer.teams[1]!.outcomes = answer.teams[1]!.outcomes.filter((o) => o.id === null || o.id === 'o-research');
    getTeamsCostPeriod.mockResolvedValue(answer);
    const { view } = await mountView();

    const fillOf = (team: string, key: string) =>
      row(view, team).find(`[data-cost-segment="${key}"][data-week="0"]`).attributes('style');
    expect(fillOf('alpha', 'o-research')).toContain('var(--os-series-3)');
    expect(fillOf('beta', 'o-research')).toContain('var(--os-series-3)');
    expect(fillOf('beta', 'none')).toContain('var(--os-ink-faint)');
  });

  it('shows ⚠ only on a team with an outcome over budget', async () => {
    getTeamsCost.mockResolvedValue(teamsCost({ betaOver: true }));
    const { view } = await mountView();

    expect(row(view, 'alpha').find('[data-teams-cost-over]').exists()).toBe(false);
    expect(row(view, 'beta').find('[data-teams-cost-over]').text()).toBe('⚠');
  });

  it.each([['priced', {}], ['no rate', { rate: null }]] as [string, { rate?: number | null }][])(
    'never names the estimate spend, a value word or Cost alone, %s',
    async (_, options) => {
      getTeamsCostPeriod.mockResolvedValue(teamsCostPeriod(options));
      const { view } = await mountView();

      const titles = view.findAll('thead th').map((th) => th.text());
      expect(misnamedOnTeamsList(readOff(view.get('[data-teams-period-control]').element), titles)).toEqual([]);
      for (const cell of view.findAll('td.teams-cost, td[data-teams-hours]')) {
        expect(misnamedOnTeamsList(readOff(cell.element))).toEqual([]);
      }
      expect(row(view, 'alpha').get('[data-teams-cost]').attributes('aria-label'))
        .toMatch(/^Estimated Execution Cost of Alpha, Last two months · .+: /);
    },
  );

  it('shows a dash for a team the answer does not carry', async () => {
    getTeamsCostPeriod.mockResolvedValue({ ...teamsCostPeriod(), teams: [] });
    const { view } = await mountView();

    expect(row(view, 'alpha').find('[data-teams-cost-none]').text()).toBe('—');
    expect(row(view, 'alpha').find('[data-teams-hours]').text()).toBe('—');
  });

  it('opens that team\'s Cost dialog on a click, not the team, over the list\'s own period', async () => {
    getTeamsCost.mockResolvedValue(teamsCost({ betaOver: true }));
    getTeamCostPeriod.mockResolvedValue(teamCostPeriod({ team: 'beta', over: true }));
    const { view, setActive } = await mountView();

    await row(view, 'beta').find('[data-teams-cost]').trigger('click');
    await flushPromises();

    expect(setActive).not.toHaveBeenCalled();
    const dialog = view.findComponent(TeamCostDialog);
    expect(dialog.props('modelValue')).toBe(true);
    expect(dialog.props('teamId')).toBe('beta');
    expect(document.body.querySelector('[data-cost-title]')?.textContent?.trim()).toBe('Estimated Execution Cost · Beta');
    // THE DIALOG READS THE TEAM OVER THE LIST'S PERIOD: the row's figures, in more detail.
    expect(getTeamCostPeriod).toHaveBeenCalledWith('beta', lastTwoMonths(), 'day');
    expect(document.body.querySelector('[data-cost-row="o-billing"] [data-cost-over]')).not.toBeNull();
    expect(getTeamsCost).toHaveBeenCalledTimes(1);
    expect(getTeamCost).not.toHaveBeenCalled();
  });

  it('reads the team\'s token totals itself when its Tokens tab opens', async () => {
    const { view } = await mountView();

    await row(view, 'alpha').find('[data-teams-cost]').trigger('click');
    await flushPromises();
    document.body.querySelector<HTMLElement>('[data-cost-tab="tokens"]')!.click();
    await flushPromises();

    expect(getTeamTokens).toHaveBeenCalledWith('alpha');
    expect(document.body.querySelector('[data-token-part="in"]')?.textContent).toContain('9');
  });

  /**
   * THE WHOLE ROW OPENS THE TEAM: every cell, the status icon beside the chart and the room around
   * it, not only the name. The chart and the buttons keep their own clicks.
   */
  it('opens the team from anywhere on its row but the chart and the buttons', async () => {
    const { view, setActive } = await mountView('Completed');
    const alpha = row(view, 'alpha');
    const targets = [
      alpha.findAll('td')[0]!,
      alpha.findAll('td')[1]!,
      alpha.find('[data-col-status]'),
      alpha.find('td.teams-cost'),
      alpha.find('td.teams-cost [data-team-status]'),
      alpha.find('[data-teams-hours]'),
      alpha.find('[data-col-actions]'),
    ];

    for (const target of targets) {
      setActive.mockClear();
      await target.trigger('click');
      expect(setActive, target.html().slice(0, 80)).toHaveBeenCalledWith('alpha');
    }
  });

  it('keeps the team\'s status icon to the right of the chart', async () => {
    const { view } = await mountView('Completed');

    const cell = row(view, 'alpha').find('.teams-cost-cell');
    const children = cell.element.children;
    expect(children[0]!.hasAttribute('data-teams-cost')).toBe(true);
    expect(children[1]!.hasAttribute('data-team-status')).toBe(true);
    expect(cell.find('[data-team-status]').attributes('aria-label')).toBe('completed');
  });

  it('shows a running team as running', async () => {
    const { view } = await mountView('Completed', [{ ...container('Manager'), state: 'Running' }] as never);

    expect(row(view, 'alpha').find('td.teams-cost [data-team-status]').attributes('data-team-status')).toBe('running');
  });
});

describe('the shared period control', () => {
  it('switches to Monthly, reads again and remembers it in this browser', async () => {
    const { view } = await mountView();

    const monthly = view.findAll('[data-teams-bucket] button').find((b) => b.text() === 'Monthly')!;
    await monthly.trigger('click');
    await flushPromises();

    expect(getTeamsCostPeriod).toHaveBeenLastCalledWith(lastTwoMonths(), 'month');
    expect(JSON.parse(localStorage.getItem('harness.teamsCostPeriod')!)).toEqual({ bucket: 'month', range: null });
  });

  it('applies a custom range, last day inclusive, and goes back to the last two months', async () => {
    const { view } = await mountView();

    await view.get('[data-teams-period-custom]').trigger('click');
    await view.get('input[data-field="rangeFrom"]').setValue('2026-09-01');
    await view.get('input[data-field="rangeTo"]').setValue('2026-09-30');
    await view.get('[data-teams-range-apply]').trigger('click');
    await flushPromises();

    expect(getTeamsCostPeriod).toHaveBeenLastCalledWith({ from: '2026-09-01', to: '2026-10-01' }, 'day');
    expect(JSON.parse(localStorage.getItem('harness.teamsCostPeriod')!))
      .toEqual({ bucket: 'day', range: { from: '2026-09-01', to: '2026-10-01' } });
    expect(view.get('[data-teams-period]').text()).toBe('September 2026 UTC');

    await view.get('[data-teams-period-default]').trigger('click');
    await flushPromises();
    expect(getTeamsCostPeriod).toHaveBeenLastCalledWith(lastTwoMonths(), 'day');
  });

  it('refuses a range whose last day is before its first, and reads nothing', async () => {
    const { view } = await mountView();

    await view.get('[data-teams-period-custom]').trigger('click');
    await view.get('input[data-field="rangeFrom"]').setValue('2026-09-10');
    await view.get('input[data-field="rangeTo"]').setValue('2026-09-01');
    await view.get('[data-teams-range-apply]').trigger('click');
    await flushPromises();

    expect(view.get('[data-teams-range-error]').text()).toBe('Pick a first and a last day, the last on or after the first.');
    expect(getTeamsCostPeriod).toHaveBeenCalledTimes(1);
  });

  it('comes back from this browser as it was left', async () => {
    localStorage.setItem('harness.teamsCostPeriod', JSON.stringify({ bucket: 'month', range: { from: '2026-01-01', to: '2026-07-01' } }));
    await mountView();

    expect(getTeamsCostPeriod).toHaveBeenCalledWith({ from: '2026-01-01', to: '2026-07-01' }, 'month');
  });
});

describe('sorting by the period\'s figures', () => {
  it('sorts by Estimated Execution Cost, a team with no priced figure last both ways, ties by name', async () => {
    const { view } = await mountView();

    await view.get('thead [aria-label="Sort by Estimated Execution Cost"]').trigger('click');
    expect(order(view)).toEqual(['alpha', 'beta', 'gamma']);
    await view.get('thead [aria-label="Sort by Estimated Execution Cost"]').trigger('click');
    expect(order(view)).toEqual(['beta', 'alpha', 'gamma']);
    expect(view.get('thead th[data-col="cost"]').attributes('aria-sort')).toBe('descending');
  });

  it('sorts by Agent Hours, a team with no runs at 0', async () => {
    const { view } = await mountView();

    await view.get('thead [aria-label="Sort by Agent Hours"]').trigger('click');
    expect(order(view)).toEqual(['gamma', 'alpha', 'beta']);
    await view.get('thead [aria-label="Sort by Agent Hours"]').trigger('click');
    expect(order(view)).toEqual(['beta', 'alpha', 'gamma']);
    expect(JSON.parse(localStorage.getItem('harness.teamSort')!)).toEqual({ column: 'hours', descending: true });
  });
});
