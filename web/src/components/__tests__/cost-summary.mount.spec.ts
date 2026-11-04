// @vitest-environment happy-dom
//
// THE THREE TILES ABOVE THE TEAMS LIST: Estimated Execution Cost, Where it goes and Right now, in every state the
// summary can be in - priced, an outcome past its stored value, no stored values, no rate - each opening what it says,
// and every outcome in its one instance-wide colour. No tile shows an outcome budget.
import { afterEach, describe, expect, it } from 'vitest';
import { mount, type VueWrapper } from '@vue/test-utils';
import CostSummary from '../CostSummary.vue';
import { costSummary, type SummaryOptions } from '../../test/costSummaryFixtures';
import { NoOutcomeColour, colourForRank } from '../../lib/outcomeColours';
import { budgetWordsOn, misnamed, readOff, readWords } from '../../test/estimateLabels';

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

describe('what the estimate is called', () => {
  // AGENT HOURS TIMES THE CURRENT RATE IS AN ESTIMATE, never money paid: no tile titles it Spend,
  // Spent or Cost alone, or says `agent h`, priced or not, inside budget or over it.
  it.each([
    ['priced', {}],
    ['over budget', { over: true }],
    ['no budgets', { budgets: false }],
    ['no rate', { rate: null }],
  ] as [string, SummaryOptions][])('never names it spend, %s', (_, options) => {
    const view = mountTiles(options);

    expect(tile(view, 'spend').get('.cost-summary-title').text()).toBe('Estimated Execution Cost');
    expect(misnamed(view.findAll('.cost-summary-title').map((t) => t.text()), { titles: true })).toEqual([]);
    expect(misnamed(readOff(view.element))).toEqual([]);
  });
});

describe('no outcome budget on the tiles', () => {
  // AN OUTCOME'S STORED VALUE IS NO BUDGET ON THIS SCREEN, whatever the summary carries: no Budgets tile, no budget
  // tick or ⚠ over in Where it goes, no budget word, priced or not - even when an outcome is past its stored value.
  it.each([
    ['priced', {}],
    ['an outcome past its stored value', { over: true }],
    ['no stored values', { budgets: false }],
    ['no rate', { rate: null }],
  ] as [string, SummaryOptions][])('three tiles and no budget word, %s', (_, options) => {
    const view = mountTiles(options);

    expect(view.findAll('[data-summary-tile]').map((t) => t.attributes('data-summary-tile'))).toEqual(['spend', 'where', 'now']);
    expect(view.find('[data-summary-tile="budgets"]').exists()).toBe(false);
    expect(view.find('[data-summary-where-tick]').exists()).toBe(false);
    expect(view.find('[data-summary-where-over]').exists()).toBe(false);
    expect(budgetWordsOn(readWords(view.element))).toEqual([]);
    expect(view.text()).not.toContain('Give an outcome a value to track it here.');
    expect(view.text()).not.toContain('Set an agent hourly rate');
  });
});

describe('the Estimated Execution Cost tile', () => {
  it('shows the total, its change on the prior eight weeks, the hours at the rate and a weekly column chart', () => {
    const spend = tile(mountTiles(), 'spend');

    expect(spend.text()).toContain('Estimated Execution Cost');
    expect(spend.text()).toContain('8 wk');
    expect(spend.get('[data-summary-value]').text()).toBe('$2,200');
    expect(spend.get('[data-summary-change]').text()).toBe('▲ 22% vs prior 8 wk');
    expect(spend.get('[data-summary-change]').attributes('data-direction')).toBe('up');
    expect(spend.get('[data-summary-detail]').text()).toBe('36.7 Agent Hours at $60/h');

    // One series, no legend, and a per-week hover text.
    expect(spend.findAll('[data-cost-segment]')).toHaveLength(8);
    expect(spend.find('[data-cost-legend]').exists()).toBe(false);
    expect(spend.get('[data-cost-week="0"] title').text()).toBe('Week of Sep 7: $275');
  });

  it('reads "at R/h" from the route\'s weighted agent rate, never the tenant setting', () => {
    const changed = tile(mountTiles({ rate: 60, tenantRate: 90 }), 'spend');
    expect(changed.get('[data-summary-value]').text()).toBe('$2,200');
    expect(changed.get('[data-summary-detail]').text()).toBe('36.7 Agent Hours at $60/h');

    // A member rate priced the runs while no tenant rate is set: dollars, not the no-rate state.
    const unset = tile(mountTiles({ rate: 60, tenantRate: null }), 'spend');
    expect(unset.get('[data-summary-value]').text()).toBe('$2,200');
    expect(unset.get('[data-summary-detail]').text()).toBe('36.7 Agent Hours at $60/h');
    expect(wrapper!.text()).not.toContain('Set an agent hourly rate');
  });

  it('footnotes a total priced in part at a reconstructed or fallback rate, and never an unpriced one', () => {
    const summary = costSummary();
    summary.total = { ...summary.total, legacyRates: ['reconstructed', 'fallback-at-upgrade'] };
    wrapper = mount(CostSummary, { props: { summary, currency: 'USD' } });

    const spend = tile(wrapper, 'spend');
    expect(spend.get('[data-summary-value]').text()).toBe('$2,200*');
    expect(spend.get('[data-summary-footnote]').text()).toBe('* Includes reconstructed and fallback rates');
    expect(spend.get('.cost-summary-title').text()).toBe('Estimated Execution Cost');
    wrapper.unmount();

    // Priced from snapshots alone: no marker and no footnote.
    const plain = tile(mountTiles(), 'spend');
    expect(plain.get('[data-summary-value]').text()).toBe('$2,200');
    expect(plain.find('[data-summary-footnote]').exists()).toBe(false);
  });

  it('with priced plugin time and no priced agent time, gives the hours without inventing a rate', () => {
    const summary = costSummary();
    summary.total = { ...summary.total, agentRate: null };
    wrapper = mount(CostSummary, { props: { summary, currency: 'USD' } });

    expect(tile(wrapper, 'spend').get('[data-summary-value]').text()).toBe('$2,200');
    expect(tile(wrapper, 'spend').get('[data-summary-detail]').text()).toBe('36.7 Agent Hours');
  });

  it('says a fall with ▼', () => {
    const summary = costSummary();
    summary.prior = { agentSeconds: 50 * 3600, cost: 3000 };
    wrapper = mount(CostSummary, { props: { summary, currency: 'USD' } });

    expect(tile(wrapper, 'spend').get('[data-summary-change]').text()).toBe('▼ 27% vs prior 8 wk');
  });

  it('without a rate, reads agent hours and "no rate set", never $0', () => {
    const spend = tile(mountTiles({ rate: null }), 'spend');

    expect(spend.get('[data-summary-value]').text()).toBe('36.7 Agent Hours');
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

describe('the Where it goes tile', () => {
  it('shows the top three outcomes, then Other outcomes, then Unattributed, on one scale of their own figures', () => {
    const where = tile(mountTiles(), 'where');
    const bars = where.findAll('[data-summary-where]');

    expect(bars.map((b) => b.attributes('data-summary-where'))).toEqual(['o-billing', 'o-docs', 'o-research', 'other', 'unattributed']);
    expect(bars.map((b) => b.get('[data-summary-where-amount]').text())).toEqual(['$1,200', '$400', '$300', '$120', '$180']);
    expect(bars[3]!.text()).toContain('Other outcomes');
    expect(bars[4]!.text()).toContain('Unattributed');
    expect(where.text()).not.toMatch(/no outcome/i);

    // One scale, the largest bar's: Ship billing's $1,200 is the top (its stored $2,000 no longer stretches it).
    const width = (index: number) => bars[index]!.get('[data-summary-where-fill]').attributes('style');
    expect(width(0)).toContain('width: 100%');
    expect(width(1)).toContain('width: 33.3');
    expect(width(2)).toContain('width: 25%');
    expect(width(3)).toContain('width: 10%');
    expect(width(4)).toContain('width: 15%');
    expect(where.find('[data-summary-where-tick]').exists()).toBe(false);
  });

  it("colours each outcome by its instance-wide rank, and Other in the neutral gray", () => {
    const bars = tile(mountTiles(), 'where').findAll('[data-summary-where-fill]');
    const background = (index: number) => bars[index]!.attributes('style');

    expect(background(0)).toContain(colourForRank(1));
    expect(background(1)).toContain(colourForRank(2));
    expect(background(2)).toContain(colourForRank(3));
    expect(background(3)).toContain(NoOutcomeColour);
  });

  it('marks no outcome past its stored value: no ⚠, and a bar reads only its name and amount', () => {
    const bars = tile(mountTiles({ over: true }), 'where').findAll('[data-summary-where]');

    expect(bars[1]!.attributes('data-summary-where')).toBe('o-docs');
    expect(bars[1]!.text()).not.toContain('⚠');
    expect(bars[1]!.attributes('aria-label')).toBe('Docs refresh: $600');
    expect(bars.some((b) => b.find('[data-summary-where-over]').exists())).toBe(false);
  });

  it('without a rate, reads hours', () => {
    const where = tile(mountTiles({ rate: null }), 'where');

    expect(where.findAll('[data-summary-where-amount]').map((a) => a.text()))
      .toEqual(['20.0 Agent Hours', '6.7 Agent Hours', '5.0 Agent Hours', '2.0 Agent Hours', '3.0 Agent Hours']);
    expect(where.find('[data-summary-where-tick]').exists()).toBe(false);
  });

  it('opens the outcome a bar is, and the Outcomes list from Other and Unattributed', async () => {
    const view = mountTiles();
    await view.get('[data-summary-where="o-docs"]').trigger('click');
    await view.get('[data-summary-where="other"]').trigger('click');
    await view.get('[data-summary-where="unattributed"]').trigger('click');

    expect(view.emitted('open-outcome')).toEqual([['o-docs'], [null], [null]]);
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
