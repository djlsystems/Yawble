// THE OUTCOMES DASHBOARD'S WORDING. Each helper formats a figure the route answered and computes
// none: a cost with no rate is "not set", never $0; no time is no efficiency, never 0%; a share of
// the value needs both figures; a week says its cost, its agent time and its runs.
import { describe, expect, it } from 'vitest';

import {
  agentHoursText,
  backlogSegments,
  efficiencyText,
  moneyText,
  rateText,
  shareOfValueText,
  valueAmount,
  weekHeights,
  weekText,
} from '../outcomes';

describe('the dashboard wording', () => {
  it('reads money in the instance currency and a missing amount as not set, never 0', () => {
    expect(moneyText(1240, 'USD')).toBe('$1,240');
    expect(moneyText(12.5, 'USD')).toBe('$12.50');
    expect(moneyText(null, 'USD')).toBe('not set');
    expect(moneyText(20000, 'EUR')).toContain('20,000');
  });

  it('takes a value only when one was given', () => {
    expect(valueAmount('20000')).toBe(20000);
    expect(valueAmount(null)).toBeNull();
    expect(valueAmount('')).toBeNull();
  });

  it('gives the share of the value only with both figures and a value above 0', () => {
    expect(shareOfValueText(1240, 20000)).toBe('6%');
    expect(shareOfValueText(10, 20000)).toBe('<1%');
    expect(shareOfValueText(null, 20000)).toBe('');
    expect(shareOfValueText(1240, null)).toBe('');
    expect(shareOfValueText(1240, 0)).toBe('');
  });

  it('reads agent time in hours, and efficiency as a percentage or a dash, never 0%', () => {
    expect(agentHoursText(49_680)).toBe('13.8 agent h');
    expect(agentHoursText(2700)).toBe('45 agent min');
    expect(efficiencyText(0.78)).toBe('78%');
    expect(efficiencyText(null)).toBe('—');
  });

  it('splits the backlog into its three states in order, and draws nothing for no items', () => {
    expect(backlogSegments({ notStarted: 2, inProgress: 3, achieved: 5 }).map((s) => [s.key, s.count, s.share]))
      .toEqual([['notStarted', 2, 20], ['inProgress', 3, 30], ['achieved', 5, 50]]);
    expect(backlogSegments({ notStarted: 0, inProgress: 0, achieved: 0 })).toEqual([]);
  });

  it('draws weeks by cost when priced and by agent time when not', () => {
    const week = (cost: number | null, agentSeconds: number) =>
      ({ weekStart: '2026-09-28T00:00:00Z', agentSeconds, billable: 0, measuredRuns: 1, unmeasuredRuns: 0, cost });
    expect(weekHeights([week(50, 1), week(100, 1)])).toEqual([50, 100]);
    expect(weekHeights([week(null, 1800), week(null, 3600)])).toEqual([50, 100]);
    expect(weekHeights([week(null, 0)])).toEqual([0]);
  });

  it('says what a week cost, how long agents worked and how many runs it had', () => {
    expect(weekText({ weekStart: '2026-09-28T00:00:00Z', agentSeconds: 9720, billable: 0, measuredRuns: 2, unmeasuredRuns: 1, cost: 243 }, 'USD'))
      .toBe('Week of Sep 28: $243 · 2.7 agent h · 3 runs · + 1 unmeasured run');
  });

  it('names the rate, or that none is set', () => {
    expect(rateText({ currency: 'USD', agentHourlyRate: 90 })).toBe('Agent time at $90/h (USD)');
    expect(rateText({ currency: 'EUR', agentHourlyRate: null })).toBe('No rate set for agent time (EUR)');
  });
});
