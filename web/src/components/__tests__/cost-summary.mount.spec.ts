// @vitest-environment happy-dom
//
// THE FOUR TILES ABOVE THE TEAMS LIST: Spend, Budgets, Where it goes and Right now, in every state
// the summary can be in - budgets inside, over budget, no budgets, no rate - each opening what it
// says, and every outcome in its one instance-wide colour.
import { afterEach, describe, expect, it } from 'vitest';
import { mount, type VueWrapper } from '@vue/test-utils';
import CostSummary from '../CostSummary.vue';
import { costSummary, type SummaryOptions } from '../../test/costSummaryFixtures';
import { NoOutcomeColour, colourForRank } from '../../lib/outcomeColours';

let wrapper: VueWrapper | undefined;

afterEach(() => {
  wrapper?.unmount();
  wrapper = undefined;
});

function mountTiles(options: SummaryOptions = {}) {
  wrapper = mount(CostSummary, { props: { summary: costSummary(options), currency: 'USD' } });
  return wrapper;
}

const tile = (view: VueWrapper, which: string) => view.get(`[data-summary-tile="${which}"]`);

describe('the Spend tile', () => {
  it('shows the total, its change on the prior eight weeks, the hours at the rate and a weekly column chart', () => {
    const spend = tile(mountTiles(), 'spend');

    expect(spend.text()).toContain('Spend');
    expect(spend.text()).toContain('8 wk');
    expect(spend.get('[data-summary-value]').text()).toBe('$2,200');
    expect(spend.get('[data-summary-change]').text()).toBe('▲ 22% vs prior 8 wk');
    expect(spend.get('[data-summary-change]').attributes('data-direction')).toBe('up');
    expect(spend.get('[data-summary-detail]').text()).toBe('36.7 agent h at $60/h');

    // One series, no legend, and a per-week hover text.
    expect(spend.findAll('[data-cost-segment]')).toHaveLength(8);
    expect(spend.find('[data-cost-legend]').exists()).toBe(false);
    expect(spend.get('[data-cost-week="0"] title').text()).toBe('Week of Sep 7: $275');
  });

  it('says a fall with ▼', () => {
    const summary = costSummary();
    summary.prior = { agentSeconds: 50 * 3600, cost: 3000 };
    wrapper = mount(CostSummary, { props: { summary, currency: 'USD' } });

    expect(tile(wrapper, 'spend').get('[data-summary-change]').text()).toBe('▼ 27% vs prior 8 wk');
  });

  it('without a rate, reads agent hours and "no rate set", never $0', () => {
    const spend = tile(mountTiles({ rate: null }), 'spend');

    expect(spend.get('[data-summary-value]').text()).toBe('36.7 agent h');
    expect(spend.get('[data-summary-detail]').text()).toBe('no rate set');
    expect(spend.get('[data-summary-change]').text()).toBe('▲ 22% vs prior 8 wk');
    expect(spend.text()).not.toContain('$');
  });

  it('opens Outcomes', async () => {
    const view = mountTiles();
    await tile(view, 'spend').trigger('click');

    expect(view.emitted('open-outcome')).toEqual([[null]]);
  });
});

describe('the Budgets tile', () => {
  it('inside budget: the share used, spent of budget, a bar, All inside budget and the unbudgeted spend', () => {
    const budgets = tile(mountTiles(), 'budgets');

    expect(budgets.attributes('data-state')).toBe('budgets');
    expect(budgets.get('[data-summary-count]').text()).toBe('2 outcomes');
    expect(budgets.get('[data-summary-value]').text()).toBe('64%');
    expect(budgets.get('[data-summary-budget-line]').text()).toBe('$1,600 of $2,500');
    expect(budgets.get('.cost-summary-bar-fill').attributes('style')).toContain('width: 64%');
    expect(budgets.get('[data-summary-status]').text()).toBe('All inside budget');
    expect(budgets.get('[data-summary-status]').attributes('data-over')).toBeUndefined();
    expect(budgets.get('[data-summary-unbudgeted]').text()).toBe('· $600 unbudgeted');
  });

  it('over budget: names the outcomes over, with ⚠, in the critical colour', () => {
    const budgets = tile(mountTiles({ over: true }), 'budgets');

    expect(budgets.attributes('data-state')).toBe('over');
    expect(budgets.get('[data-summary-value]').text()).toBe('72%');
    expect(budgets.get('[data-summary-budget-line]').text()).toBe('$1,800 of $2,500');
    expect(budgets.get('[data-summary-status]').text()).toBe('⚠ Docs refresh over');
    expect(budgets.get('[data-summary-status]').attributes('data-over')).toBe('true');
    expect(budgets.get('.cost-summary-bar-fill').attributes('data-over')).toBe('true');
  });

  it('no budgets: None set, and how to set one', () => {
    const budgets = tile(mountTiles({ budgets: false }), 'budgets');

    expect(budgets.attributes('data-state')).toBe('no-budgets');
    expect(budgets.get('[data-summary-value]').text()).toBe('None set');
    expect(budgets.get('[data-summary-note]').text()).toBe('Give an outcome a value to track it here.');
    expect(budgets.find('.cost-summary-bar').exists()).toBe(false);
  });

  it('no rate: a dash and how to set a rate, never $0', () => {
    const budgets = tile(mountTiles({ rate: null }), 'budgets');

    expect(budgets.attributes('data-state')).toBe('no-rate');
    expect(budgets.get('[data-summary-value]').text()).toBe('—');
    expect(budgets.get('[data-summary-note]').text()).toBe('Set an agent hourly rate in Outcomes to measure spend against budgets.');
    expect(budgets.text()).not.toContain('$');
  });

  it('opens Outcomes', async () => {
    const view = mountTiles();
    await tile(view, 'budgets').trigger('click');

    expect(view.emitted('open-outcome')).toEqual([[null]]);
  });
});

describe('the Where it goes tile', () => {
  it('shows the top three outcomes, then Other / no outcome, on one scale with a budget tick', () => {
    const where = tile(mountTiles(), 'where');
    const bars = where.findAll('[data-summary-where]');

    expect(bars.map((b) => b.attributes('data-summary-where'))).toEqual(['o-billing', 'o-docs', 'o-research', 'other']);
    expect(bars.map((b) => b.get('[data-summary-where-amount]').text())).toEqual(['$1,200', '$400', '$300', '$300']);
    expect(bars[3]!.text()).toContain('Other / no outcome');

    // One scale: Ship billing's $2,000 budget is the top, so its $1,200 is 60% and its tick at 100%.
    expect(bars[0]!.get('[data-summary-where-fill]').attributes('style')).toContain('width: 60%');
    expect(bars[0]!.get('[data-summary-where-tick]').attributes('style')).toContain('left: 100%');
    expect(bars[1]!.get('[data-summary-where-fill]').attributes('style')).toContain('width: 20%');
    expect(bars[1]!.get('[data-summary-where-tick]').attributes('style')).toContain('left: 25%');
    expect(bars[2]!.find('[data-summary-where-tick]').exists()).toBe(false);
  });

  it("colours each outcome by its instance-wide rank, and Other in the neutral gray", () => {
    const bars = tile(mountTiles(), 'where').findAll('[data-summary-where-fill]');
    const background = (index: number) => bars[index]!.attributes('style');

    expect(background(0)).toContain(colourForRank(1));
    expect(background(1)).toContain(colourForRank(2));
    expect(background(2)).toContain(colourForRank(3));
    expect(background(3)).toContain(NoOutcomeColour);
  });

  it('puts ⚠ after an outcome over its budget', () => {
    const bars = tile(mountTiles({ over: true }), 'where').findAll('[data-summary-where]');

    expect(bars[1]!.attributes('data-summary-where')).toBe('o-docs');
    expect(bars[1]!.find('[data-summary-where-over]').exists()).toBe(true);
    expect(bars[0]!.find('[data-summary-where-over]').exists()).toBe(false);
  });

  it('without a rate, reads hours and draws no budget ticks', () => {
    const where = tile(mountTiles({ rate: null }), 'where');

    expect(where.findAll('[data-summary-where-amount]').map((a) => a.text()))
      .toEqual(['20.0 agent h', '6.7 agent h', '5.0 agent h', '5.0 agent h']);
    expect(where.find('[data-summary-where-tick]').exists()).toBe(false);
  });

  it('opens the outcome a bar is, and the Outcomes list from Other', async () => {
    const view = mountTiles();
    await view.get('[data-summary-where="o-docs"]').trigger('click');
    await view.get('[data-summary-where="other"]').trigger('click');

    expect(view.emitted('open-outcome')).toEqual([['o-docs'], [null]]);
  });
});

describe('the Right now tile', () => {
  it('counts open workflows, members running and what waits on you, in the warning colour', async () => {
    const view = mountTiles();
    const now = tile(view, 'now');

    expect(now.get('[data-summary-now="workflows"]').text()).toBe('4 open workflows');
    expect(now.get('[data-summary-now="running"]').text()).toBe('2 members running');
    expect(now.get('[data-summary-now="waiting"]').text()).toBe('3 ⚠ waiting on you');
    expect(now.get('[data-summary-now="waiting"]').attributes('data-waiting')).toBe('true');

    await now.get('[data-summary-now="waiting"]').trigger('click');
    expect(view.emitted('open-needs-you')).toHaveLength(1);
  });

  it('shows waiting on you at 0, not highlighted', () => {
    const waiting = tile(mountTiles({ waiting: 0 }), 'now').get('[data-summary-now="waiting"]');

    expect(waiting.text()).toBe('0 ⚠ waiting on you');
    expect(waiting.attributes('data-waiting')).toBeUndefined();
  });
});
