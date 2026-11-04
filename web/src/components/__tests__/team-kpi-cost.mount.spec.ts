// @vitest-environment happy-dom
//
// THE TEAM PAGE'S ESTIMATED EXECUTION COST TILE AND ITS DIALOG, MOUNTED, over the period the Teams list and the team page
// share: the tile's figure, Agent Hours at the weighted rate, footnote and chart on the team's own scale; the dialog's
// headline, per-1M figure and breakdowns by outcome, by member (agent and plugin apart) and by workflow - each the
// period read's figure unchanged, for Daily, Monthly and a custom range, priced, unpriced and with no runs. The budget
// words still read the eight-week answer (`test/costFixtures.ts`); the period read is `test/teamPeriodFixtures.ts`.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const { getTeamCost, getTeamCostPeriod, getTeamCostWorkflows } = vi.hoisted(() => ({
  getTeamCost: vi.fn(),
  getTeamCostPeriod: vi.fn(),
  getTeamCostWorkflows: vi.fn(),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  getTeamActivity: () => new Promise(() => {}),
  getTeamTokenRuns: () => new Promise(() => {}),
}));
vi.mock('../../api/outcomes', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  getTeamCost,
  getTeamCostPeriod,
  getTeamCostWorkflows,
}));
vi.mock('quasar', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  useQuasar: () => ({ notify: vi.fn() }),
}));

import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import TeamKpiStrip from '../TeamKpiStrip.vue';
import CostWeeksChart from '../CostWeeksChart.vue';
import { useConsoleStore } from '../../stores/console';
import { useWorkflowNavStore } from '../../stores/workflowNav';
import { asTeamId, type TeamTokenTotals } from '../../api/types';
import type { TeamCostPeriod } from '../../api/outcomes';
import { resetBody } from '../../test/mountQuasar';
import { teamCost } from '../../test/costFixtures';
import { teamCostPeriod, type TeamPeriodOptions } from '../../test/teamPeriodFixtures';
import { misnamedOnTeamsList, readWords as readOff } from '../../test/estimateLabels';
import { TeamsPeriodKey, lastTwoMonths } from '../../lib/teamsPeriod';
import { perMillionText } from '../../lib/teamCost';

const teamId = asTeamId('alpha');
const squash = (text: string | null | undefined) => (text ?? '').replace(/\s+/g, ' ').trim();

/** An element's text nodes joined by spaces, as a person reads separate blocks. */
function words(element: Element | null | undefined): string {
  if (!element) return '';
  const parts: string[] = [];
  const walk = (node: Node) => {
    if (node.nodeType === 3) parts.push(node.textContent ?? '');
    else node.childNodes.forEach(walk);
  };
  walk(element);
  return squash(parts.join(' '));
}

const usage = {
  available: true, tokensIn: 1200, tokensOut: 340, tokensCachedIn: 5000, tokensCacheCreation: 80,
  tokensBillable: 2140, partial: false, runsWithUsage: 2, runsWithoutUsage: 0, missing: null,
  members: [{ member: 'dev', brand: 'claude-headless', tokensIn: 1200, tokensOut: 340, runs: 2, tokensCachedIn: 5000, tokensCacheCreation: 80, tokensBillable: 2140 }],
} as TeamTokenTotals;

let wrapper: VueWrapper | undefined;

beforeEach(() => {
  getTeamCost.mockReset();
  getTeamCostPeriod.mockReset();
  getTeamCostWorkflows.mockReset();
  getTeamCost.mockResolvedValue(teamCost());
  getTeamCostWorkflows.mockResolvedValue({ team: 'alpha', money: { currency: 'USD', agentHourlyRate: 60 }, workflows: [] });
  localStorage.clear();
});

afterEach(() => {
  wrapper?.unmount();
  wrapper = undefined;
  resetBody();
  localStorage.clear();
});

async function mountStrip(period: TeamCostPeriod | Error = teamCostPeriod()) {
  if (period instanceof Error) getTeamCostPeriod.mockRejectedValue(period);
  else getTeamCostPeriod.mockResolvedValue(period);
  setActivePinia(createPinia());
  useConsoleStore().$patch({
    teams: [{ id: teamId, name: 'Alpha', memberAgents: [], repos: [] }],
    activeTeamId: teamId,
    overviewLanded: true,
  } as never);

  wrapper = mount(TeamKpiStrip, {
    attachTo: document.body,
    props: { teamId, containers: [], usage, timing: null, workflows: null, findings: [], clockOffset: 0 },
  });
  await flushPromises();
  return wrapper;
}

const tile = () => wrapper!.find('[data-cost-tile]');
const dialog = () => document.body.querySelector<HTMLElement>('[data-cost-dialog]');

async function openDialog() {
  await tile().trigger('click');
  await flushPromises();
}

async function showBreakdown(label: string) {
  const button = [...dialog()!.querySelectorAll<HTMLElement>('[data-cost-breakdowns] button')].find((b) => squash(b.textContent) === label)!;
  button.click();
  await flushPromises();
}

function table(selector: string) {
  return [...dialog()!.querySelectorAll(`${selector} tbody tr`)].map((tr) => [...tr.querySelectorAll('td')].map((td) => squash(td.textContent)));
}

describe('the team page\'s Estimated Execution Cost words', () => {
  // NO ESTIMATE IS TITLED SPEND, SPENT OR COST ALONE, and no value word sits near it: on the tile, its period control
  // and the dialog's headline and every breakdown, priced, unpriced and with no runs.
  it.each([
    ['priced', {}],
    ['unpriced', { unpriced: true }],
    ['with no runs', { empty: true }],
    ['priced in part at a reconstructed rate', { legacy: true }],
  ] as [string, TeamPeriodOptions][])('never misnames it, %s', async (_, options) => {
    await mountStrip(teamCostPeriod(options));

    const control = document.body.querySelector('[data-team-period]');
    expect(misnamedOnTeamsList(readOff(control))).toEqual([]);
    expect(misnamedOnTeamsList(readOff(tile().element), [tile().find('.team-kpi-label').text()])).toEqual([]);

    await openDialog();
    for (const label of ['By outcome', 'By member', 'By workflow']) {
      await showBreakdown(label);
      const titles = [...dialog()!.querySelectorAll('[data-cost-title], [data-cost-tab], [data-cost-figure] .text-caption, table th')]
        .map((e) => e.textContent ?? '');
      expect(misnamedOnTeamsList(readOff(dialog()), titles)).toEqual([]);
    }
  });
});

describe('the Estimated Execution Cost tile over the period', () => {
  it('reads the team\'s period for the Teams list\'s remembered choice and is titled Estimated Execution Cost · last two months', async () => {
    await mountStrip();

    expect(getTeamCostPeriod).toHaveBeenCalledWith('alpha', lastTwoMonths(), 'day');
    expect(squash(tile().find('.team-kpi-label').text())).toBe('Estimated Execution Cost · last two months');
  });

  it('shows the route\'s figure, its Agent Hours and its weighted rate unchanged, never the tenant setting', async () => {
    const period = teamCostPeriod();
    period.money.agentHourlyRate = 90;
    await mountStrip(period);

    expect(squash(tile().find('[data-cost-value]').text())).toBe('$750');
    expect(squash(tile().find('[data-cost-detail]').text())).toBe('12.0 Agent Hours at $60/h');
    expect(tile().find('[data-cost-footnote]').exists()).toBe(false);
  });

  it('follows Monthly from the shared choice: read by month, one column', async () => {
    localStorage.setItem(TeamsPeriodKey, JSON.stringify({ bucket: 'month', range: null }));
    await mountStrip(teamCostPeriod({ bucket: 'month' }));

    expect(getTeamCostPeriod).toHaveBeenCalledWith('alpha', lastTwoMonths(), 'month');
    expect(wrapper!.findComponent(CostWeeksChart).props('weeks')).toEqual(['2026-10-01']);
    expect(squash(tile().find('[data-cost-value]').text())).toBe('$750');
  });

  it('follows a custom range from the shared choice, named by its month', async () => {
    localStorage.setItem(TeamsPeriodKey, JSON.stringify({ bucket: 'day', range: { from: '2026-09-01', to: '2026-10-01' } }));
    await mountStrip();

    expect(getTeamCostPeriod).toHaveBeenCalledWith('alpha', { from: '2026-09-01', to: '2026-10-01' }, 'day');
    expect(squash(tile().find('.team-kpi-label').text())).toBe('Estimated Execution Cost · September 2026');
  });

  it('reads again when the period is changed on the team page, and remembers it for the Teams list', async () => {
    await mountStrip();
    getTeamCostPeriod.mockResolvedValue(teamCostPeriod({ bucket: 'month' }));

    const monthly = [...document.body.querySelectorAll<HTMLElement>('[data-team-period] [data-teams-bucket] button')]
      .find((b) => squash(b.textContent) === 'Monthly')!;
    monthly.click();
    await flushPromises();

    expect(getTeamCostPeriod).toHaveBeenLastCalledWith('alpha', lastTwoMonths(), 'month');
    expect(JSON.parse(localStorage.getItem(TeamsPeriodKey)!)).toEqual({ bucket: 'month', range: null });
  });

  it('draws the team\'s chart on the team\'s own scale, stacked by outcome, Unattributed at the bottom', async () => {
    await mountStrip();

    const chart = wrapper!.findComponent(CostWeeksChart);
    expect(chart.props('scaleMax')).toBeNull();
    expect(chart.props('weeks')).toEqual(['2026-10-01', '2026-10-02', '2026-10-03']);
    const first = tile().findAll('[data-cost-segment][data-week="0"]');
    expect(first.map((s) => s.attributes('data-cost-segment'))).toEqual(['none', 'o-billing', 'o-docs']);
    expect(first[0]!.attributes('style')).toContain('var(--os-ink-faint)');
    expect(first[1]!.attributes('style')).toContain('var(--os-series-1)');
  });

  it('footnotes a figure priced in part at a reconstructed rate', async () => {
    await mountStrip(teamCostPeriod({ legacy: true }));

    expect(squash(tile().find('[data-cost-value]').text())).toBe('$750*');
    expect(squash(tile().find('[data-cost-footnote]').text())).toBe('* Includes reconstructed rates');
  });

  it('unpriced: Estimate unavailable and the Agent Hours, never $0', async () => {
    await mountStrip(teamCostPeriod({ unpriced: true }));

    expect(squash(tile().find('[data-cost-value]').text())).toBe('Estimate unavailable');
    expect(squash(tile().find('[data-cost-detail]').text())).toBe('12.0 Agent Hours');
    expect(tile().find('[data-cost-value]').text()).not.toContain('$');
  });

  it('with no runs: a dash and 0.0 Agent Hours', async () => {
    await mountStrip(teamCostPeriod({ empty: true }));

    expect(squash(tile().find('[data-cost-value]').text())).toBe('—');
    expect(squash(tile().find('[data-cost-detail]').text())).toBe('0.0 Agent Hours');
  });

  it('keeps the budget bar and lines on the eight-week answer, unchanged', async () => {
    await mountStrip();

    expect(squash(tile().find('[data-cost-budget]').text())).toBe('$1,000 of $1,500 budget (67%)');
    expect(squash(tile().find('[data-cost-unbudgeted]').text())).toBe('+ $360 on work with no budget');
    expect(tile().find('[data-cost-bar]').exists()).toBe(true);
    expect(getTeamCost).toHaveBeenCalledWith('alpha');
  });

  it('says unavailable when the period cannot be read', async () => {
    await mountStrip(new Error('route down'));
    expect(tile().text()).toContain('unavailable');
  });
});

describe('the Estimated Execution Cost dialog over the period', () => {
  it('is titled Estimated Execution Cost · <team>, with the rate line and the period control', async () => {
    await mountStrip();
    await openDialog();

    expect(squash(dialog()!.querySelector('[data-cost-title]')!.textContent)).toBe('Estimated Execution Cost · Alpha');
    expect(squash(dialog()!.querySelector('[data-cost-rate]')!.textContent)).toBe('Default agent rate $60/h (USD) · Change in Outcomes');
    expect(dialog()!.querySelector('[data-cost-period-row] [data-teams-period-control]')).not.toBeNull();

    const nav = useWorkflowNavStore();
    const asks = nav.outcomeRateAsks;
    dialog()!.querySelector<HTMLElement>('[data-cost-rate-change]')!.click();
    await flushPromises();
    expect(nav.outcomeRateAsks).toBe(asks + 1);
  });

  it('heads with the period\'s Estimated Execution Cost, Agent Hours, Budget used and Per 1M billable tokens', async () => {
    const period = teamCostPeriod();
    await mountStrip(period);
    await openDialog();

    const figure = (name: string) => words(dialog()!.querySelector(`[data-cost-figure="${name}"]`));
    expect(figure('estimate')).toBe('$750 Estimated Execution Cost · last two months');
    expect(figure('agent-hours')).toBe('12.0 Agent Hours 12.0 Agent Hours at $60/h');
    // Budget used still reads the eight-week answer's budgeted outcomes.
    expect(figure('budget-used')).toBe('67% Budget used · $1,000 of $1,500');
    expect(figure('per-million')).toBe(`${perMillionText(period.totals, 'USD')} Per 1M billable tokens`);
    expect(figure('per-million')).toBe('$166,667 Per 1M billable tokens');
  });

  it('draws the period\'s chart on the team\'s own scale with a column per day', async () => {
    await mountStrip();
    await openDialog();

    const charts = wrapper!.findAllComponents(CostWeeksChart);
    const large = charts.find((c) => c.props('size') === 'large')!;
    expect(large.props('scaleMax')).toBeNull();
    expect(large.props('weeks')).toEqual(['2026-10-01', '2026-10-02', '2026-10-03']);
    const legend = [...dialog()!.querySelectorAll('[data-cost-legend-item]')].map((i) => squash(i.textContent));
    expect(legend).toEqual(['Docs refresh', 'Ship billing', 'Unattributed']);
  });

  it('breaks it down by outcome: each outcome, then Unattributed, then the Team total, budgets as before', async () => {
    await mountStrip();
    await openDialog();

    expect(table('[data-cost-breakdown="outcome"]')).toEqual([
      ['Ship billing', '3', '6.0', '$360', '$900 / $2,000', '3,000', '$120,000'],
      ['Docs refresh', '1', '3.0', '$180', 'None set', '500', '$360,000'],
      ['Unattributed', '4', '3.0', '$210', '—', '1,000', '$210,000'],
      ['Team total', '8', '12.0', '$750', '', '4,500', '$166,667'],
    ]);
  });

  it('marks an outcome over its budget ⚠ over by its all-time total', async () => {
    await mountStrip(teamCostPeriod({ over: true }));
    await openDialog();

    const billing = dialog()!.querySelector('[data-cost-row="o-billing"] [data-cell="budget"]')!;
    expect(squash(billing.textContent)).toBe('$2,400 / $2,000 ⚠ over');
  });

  it('breaks it down by member, agent and plugin members apart: an unpriced plugin is Not priced, never the agent default', async () => {
    await mountStrip();
    await openDialog();
    await showBreakdown('By member');

    expect([...dialog()!.querySelectorAll('[data-cost-breakdown="agent-members"] th')].map((th) => squash(th.textContent)))
      .toEqual(['Agent member', 'Executions', 'Agent Hours', 'Estimated Agent Cost']);
    expect(table('[data-cost-breakdown="agent-members"]')).toEqual([
      ['Ana', '3', '6.0', '$360'],
      ['Dev', '3', '6.0', '$360'],
    ]);
    expect([...dialog()!.querySelectorAll('[data-cost-breakdown="plugin-members"] th')].map((th) => squash(th.textContent)))
      .toEqual(['Plugin member', 'Executions', 'Estimated Plugin Cost']);
    expect(table('[data-cost-breakdown="plugin-members"]')).toEqual([
      ['Bare', '1', 'Not priced'],
      ['Tool', '1', '$30'],
    ]);
  });

  it('breaks it down by workflow, newest first', async () => {
    await mountStrip();
    await openDialog();
    await showBreakdown('By workflow');

    expect(table('[data-cost-breakdown="workflow"]')).toEqual([
      ['#42', '3', '6.0', '$360'],
      ['#41', '2', '3.0', '$180'],
      ['#40', '1', '3.0', '$180'],
      ['#39', '2', '0.0', '$30'],
    ]);
  });

  it('unpriced: every figure Estimate unavailable or Not priced, no dollar anywhere in the breakdowns', async () => {
    await mountStrip(teamCostPeriod({ unpriced: true }));
    await openDialog();

    expect(words(dialog()!.querySelector('[data-cost-figure="estimate"]'))).toBe('Estimate unavailable Estimated Execution Cost · last two months');
    expect(dialog()!.querySelector('[data-cost-no-rate]')).not.toBeNull();
    await showBreakdown('By member');
    expect(table('[data-cost-breakdown="agent-members"]').map((r) => r[3])).toEqual(['Estimate unavailable', 'Estimate unavailable']);
    expect(table('[data-cost-breakdown="plugin-members"]').map((r) => r[2])).toEqual(['Not priced', 'Not priced']);
    await showBreakdown('By workflow');
    expect(dialog()!.querySelector('[data-cost-breakdown="workflow"]')!.textContent).not.toContain('$');
  });

  it('with no runs: a dash, and each breakdown says nothing ran', async () => {
    await mountStrip(teamCostPeriod({ empty: true }));
    await openDialog();

    expect(words(dialog()!.querySelector('[data-cost-figure="estimate"]'))).toBe('— Estimated Execution Cost · last two months');
    expect(table('[data-cost-breakdown="outcome"]')).toEqual([['Team total', '0', '0.0', '—', '', '0', '—']]);
    await showBreakdown('By member');
    expect(squash(dialog()!.querySelector('[data-cost-breakdown="agent-members"] tbody')!.textContent)).toBe('No agent member ran in this period.');
    await showBreakdown('By workflow');
    expect(squash(dialog()!.querySelector('[data-cost-breakdown="workflow"] tbody')!.textContent)).toBe('No workflow ran in this period.');
  });

  it('shows the Tokens dialog content on the Tokens tab', async () => {
    await mountStrip();
    await openDialog();
    dialog()!.querySelector<HTMLElement>('[data-cost-tab="tokens"]')!.click();
    await flushPromises();

    expect(dialog()!.querySelector('[data-tokens-panel]')).not.toBeNull();
    expect(words(dialog()!.querySelector('[data-token-part="cache-read"]'))).toBe('5,000 cache read');
    expect(dialog()!.querySelector('[data-cost-figures]')).toBeNull();
  });
});
