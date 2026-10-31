// @vitest-environment happy-dom
//
// THE COST TILE AND THE COST DIALOG, MOUNTED. The tile's four states - budgets, over budget,
// no budgets, no rate - and the dialog's two tabs, headline figures, chart, table and note, read off
// the screen. The cost route is a fixture in the pinned contract's shape (`test/costFixtures.ts`).
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const { getTeamCost } = vi.hoisted(() => ({ getTeamCost: vi.fn() }));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  getTeamActivity: () => new Promise(() => {}),
  getTeamTokenRuns: () => new Promise(() => {}),
}));
vi.mock('../../api/outcomes', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  getTeamCost,
}));
vi.mock('quasar', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  useQuasar: () => ({ notify: vi.fn() }),
}));

import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import TeamKpiStrip from '../TeamKpiStrip.vue';
import { useConsoleStore } from '../../stores/console';
import { useWorkflowNavStore } from '../../stores/workflowNav';
import { asTeamId, type TeamTokenTotals } from '../../api/types';
import { resetBody } from '../../test/mountQuasar';
import { teamCost, type CostOptions } from '../../test/costFixtures';

const teamId = asTeamId('alpha');
const squash = (text: string | null | undefined) => (text ?? '').replace(/\s+/g, ' ').trim();

/** An element's text nodes joined by spaces, as a person reads separate blocks: `$1,080 Spent`. */
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
});

afterEach(() => {
  wrapper?.unmount();
  wrapper = undefined;
  resetBody();
});

async function mountStrip(options: CostOptions = {}, cost = teamCost(options)) {
  getTeamCost.mockResolvedValue(cost);
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

describe('the Cost tile', () => {
  it('reads one cost route for its team, and is labelled Cost - last 8 weeks', async () => {
    await mountStrip();

    expect(getTeamCost).toHaveBeenCalledWith('alpha');
    expect(squash(tile().find('.team-kpi-label').text())).toBe('Cost - last 8 weeks');
  });

  it('with budgets: dollars, hours at the rate, the bar and spent of budget over the budgeted outcomes', async () => {
    await mountStrip();

    expect(squash(tile().find('[data-cost-value]').text())).toBe('$1,080');
    expect(squash(tile().find('[data-cost-detail]').text())).toBe('18.0 agent h at $60/h');
    // Ship billing's $700 and Docs refresh's $300 are their totals across ALL teams, not this team's part.
    expect(squash(tile().find('[data-cost-budget]').text())).toBe('$1,000 of $1,500 budget (67%)');
    expect(squash(tile().find('[data-cost-unbudgeted]').text())).toBe('+ $360 on work with no budget');
    expect(tile().find('[data-cost-bar]').exists()).toBe(true);
    expect(tile().find('[data-cost-bar]').attributes('data-over')).toBeUndefined();
    expect(tile().find('[data-cost-over]').exists()).toBe(false);
    expect(tile().find('[data-cost-note]').exists()).toBe(false);
  });

  it('draws the small stacked weekly chart, No outcome at the bottom of each stack in neutral gray', async () => {
    await mountStrip();

    const chart = tile().find('[data-cost-chart]');
    expect(chart.attributes('data-size')).toBe('tile');
    const first = chart.findAll('[data-cost-segment][data-week="0"]');
    expect(first.map((s) => s.attributes('data-cost-segment'))).toEqual(['none', 'o-billing', 'o-docs', 'o-research']);
    expect(first[0]!.attributes('style')).toContain('var(--os-ink-faint)');
    expect(first[1]!.attributes('style')).toContain('var(--os-series-1)');
    expect(first[2]!.attributes('style')).toContain('var(--os-series-2)');
  });

  it('colours an outcome by its global rank, not by its place among this team\'s outcomes', async () => {
    const cost = teamCost();
    cost.outcomes = cost.outcomes.filter((o) => o.id === null || o.id === 'o-docs');
    await mountStrip({}, cost);

    const first = tile().find('[data-cost-chart]').findAll('[data-cost-segment][data-week="0"]');
    expect(first.map((s) => s.attributes('data-cost-segment'))).toEqual(['none', 'o-docs']);
    expect(first[1]!.attributes('style')).toContain('var(--os-series-2)');
  });

  it('over budget: the bar in the critical colour and ⚠ 1 over', async () => {
    await mountStrip({ over: true });

    expect(squash(tile().find('[data-cost-budget]').text())).toBe('$1,350 of $1,500 budget (90%) ⚠ 1 over');
    expect(squash(tile().find('[data-cost-over]').text())).toBe('⚠ 1 over');
    expect(tile().find('[data-cost-bar]').attributes('data-over')).toBe('');
    expect(tile().find('[data-cost-bar]').classes()).toContain('team-kpi-cost-bar--over');
  });

  it('no budgets: no bar, and No outcome budget set', async () => {
    await mountStrip({ budgets: false });

    expect(squash(tile().find('[data-cost-value]').text())).toBe('$1,080');
    expect(tile().find('[data-cost-bar]').exists()).toBe(false);
    expect(tile().find('[data-cost-budget]').exists()).toBe(false);
    expect(squash(tile().find('[data-cost-note]').text())).toBe('No outcome budget set');
  });

  it('no rate: agent hours, never $0, and a pointer to the rate in Outcomes', async () => {
    await mountStrip({ rate: null });

    expect(squash(tile().find('[data-cost-value]').text())).toBe('18.0 agent h');
    expect(tile().text()).not.toContain('$');
    expect(tile().find('[data-cost-bar]').exists()).toBe(false);
    expect(squash(tile().find('[data-cost-note]').text())).toBe('Set an agent hourly rate in Outcomes to see dollars');
  });

  it('says unavailable when the route cannot be read', async () => {
    getTeamCost.mockRejectedValue(new Error('route down'));
    setActivePinia(createPinia());
    wrapper = mount(TeamKpiStrip, {
      attachTo: document.body,
      props: { teamId, containers: [], usage, timing: null, workflows: null, findings: [], clockOffset: 0 },
    });
    await flushPromises();

    expect(tile().text()).toContain('unavailable');
  });

  it('opens the Cost dialog on a click', async () => {
    await mountStrip();
    await openDialog();

    expect(dialog()).not.toBeNull();
  });
});

describe('the Cost dialog', () => {
  it('is titled Cost · <team>, with the rate line linking to the Outcomes rate', async () => {
    await mountStrip();
    await openDialog();

    expect(squash(dialog()!.querySelector('[data-cost-title]')!.textContent)).toBe('Cost · Alpha');
    expect(squash(dialog()!.querySelector('[data-cost-rate]')!.textContent)).toBe('Agent time at $60/h (USD) · Change in Outcomes');

    const nav = useWorkflowNavStore();
    const asks = nav.outcomeRateAsks;
    dialog()!.querySelector<HTMLElement>('[data-cost-rate-change]')!.click();
    await flushPromises();
    expect(nav.outcomeRateAsks).toBe(asks + 1);
  });

  it('opens on the Cost tab, with Tokens beside it', async () => {
    await mountStrip();
    await openDialog();

    const tabs = [...dialog()!.querySelectorAll('[data-cost-tab]')].map((t) => squash(t.textContent));
    expect(tabs).toEqual(['Cost', 'Tokens']);
    expect(dialog()!.querySelector('[data-cost-figures]')).not.toBeNull();
    expect(dialog()!.querySelector('[data-tokens-panel]')).toBeNull();
  });

  it('heads the Cost tab with Spent, Agent time, Budget used and Per 1M billable tokens', async () => {
    await mountStrip();
    await openDialog();

    const figure = (name: string) => words(dialog()!.querySelector(`[data-cost-figure="${name}"]`));
    expect(figure('spent')).toBe('$1,080 Spent');
    expect(figure('agent-time')).toBe('18.0 agent h Agent time');
    expect(figure('budget-used')).toBe('67% Budget used · $1,000 of $1,500');
    // $1,080 over 3.4M billable tokens.
    expect(figure('per-million')).toBe('$318 Per 1M billable tokens');
  });

  it('draws the large chart with a legend and a per-week tooltip of each outcome and the total', async () => {
    await mountStrip();
    await openDialog();

    const legend = [...dialog()!.querySelectorAll('[data-cost-legend-item]')].map((i) => squash(i.textContent));
    expect(legend).toEqual(['Research', 'Docs refresh', 'Ship billing', 'No outcome']);
    expect(dialog()!.querySelectorAll('[data-cost-tick]').length).toBeGreaterThan(1);

    dialog()!.querySelector('[data-cost-week="3"]')!.dispatchEvent(new Event('mouseenter'));
    await flushPromises();

    const tip = dialog()!.querySelector('[data-cost-tooltip]')!;
    expect(words(tip.querySelector('[data-cost-tooltip-row="o-billing"]'))).toBe('Ship billing $60 1.0 agent h');
    expect(words(tip.querySelector('[data-cost-tooltip-row="none"]'))).toBe('No outcome $30 0.5 agent h');
    expect(words(tip.querySelector('[data-cost-tooltip-total]'))).toBe('Week total $135 · 2.3 agent h');
  });

  it('tables each outcome, then No outcome and a Team total row', async () => {
    await mountStrip();
    await openDialog();

    const rows = [...dialog()!.querySelectorAll('[data-cost-row]')].map((tr) => ({
      row: tr.getAttribute('data-cost-row'),
      cells: [...tr.querySelectorAll('td')].map((td) => squash(td.textContent)),
    }));

    expect(rows).toEqual([
      { row: 'o-billing', cells: ['Ship billing', '8.0', '$480', '$700 / $1,000', '2,000,000', '$240'] },
      { row: 'o-docs', cells: ['Docs refresh', '4.0', '$240', '$300 / $500', '400,000', '$600'] },
      { row: 'o-research', cells: ['Research', '2.0', '$120', 'None set', '200,000', '$600'] },
      { row: 'none', cells: ['No outcome', '4.0', '$240', '—', '800,000', '$300'] },
      { row: 'total', cells: ['Team total', '18.0', '$1,080', '', '3,400,000', '$318'] },
    ]);
    expect(squash(dialog()!.querySelector('[data-cost-note]')!.textContent)).toContain('tokens are not billed here');
  });

  it('marks an outcome over its budget ⚠ over in the table', async () => {
    await mountStrip({ over: true });
    await openDialog();

    const docs = dialog()!.querySelector('[data-cost-row="o-docs"] [data-cell="budget"]')!;
    expect(squash(docs.textContent)).toBe('$650 / $500 ⚠ over');
    expect(docs.querySelector('[data-cost-over]')).not.toBeNull();
    expect(dialog()!.querySelector('[data-cost-row="o-billing"] [data-cost-over]')).toBeNull();
  });

  it('without a rate: hours, no Budget used, no dollar anywhere', async () => {
    await mountStrip({ rate: null });
    await openDialog();

    expect(dialog()!.querySelector('[data-cost-figure="budget-used"]')).toBeNull();
    expect(words(dialog()!.querySelector('[data-cost-figure="spent"]'))).toBe('— Spent · no rate set');
    expect(squash(dialog()!.querySelector('[data-cost-row="total"] [data-cell="cost"]')!.textContent)).toBe('—');
    expect(squash(dialog()!.querySelector('[data-cost-rate]')!.textContent)).toBe('No rate set for agent time (USD) · Change in Outcomes');
    expect(dialog()!.querySelector('[data-cost-no-rate]')).not.toBeNull();
    expect(dialog()!.querySelector('[data-cost-table]')!.textContent).not.toMatch(/\$\s*0\b/);
  });

  it('shows the Tokens dialog content on the Tokens tab', async () => {
    await mountStrip();
    await openDialog();
    dialog()!.querySelector<HTMLElement>('[data-cost-tab="tokens"]')!.click();
    await flushPromises();

    expect(dialog()!.querySelector('[data-tokens-panel]')).not.toBeNull();
    expect(dialog()!.querySelector('[data-tokens-chart]')).not.toBeNull();
    expect(dialog()!.querySelector('.tokens-metric')).not.toBeNull();
    expect(words(dialog()!.querySelector('[data-token-part="cache-read"]'))).toBe('5,000 cache read');
    expect(dialog()!.querySelector('[data-cost-figures]')).toBeNull();
  });
});
