import { describe, expect, it } from 'vitest';
import {
  DefaultTeamsPeriod, bucketLabel, estimateFootnote, estimateText, hoursText, lastTwoMonths, periodHeading, periodPriced,
  periodSeries, periodTop, rangeOfChoice, readTeamsPeriod, writeTeamsPeriod,
} from '../teamsPeriod';
import { bucketAside, periodTile, whereBars } from '../costSummary';
import { teamsCostPeriod } from '../../test/costPeriodFixtures';

/**
 * THE TEAMS LIST'S PERIOD: the default range, the remembered choice, and the figures each row and tile reads -
 * every one the route's own, unchanged.
 */
describe('the period a person picks', () => {
  it('defaults to Daily over the last two months: the previous UTC month and the current one, to exclusive', () => {
    expect(DefaultTeamsPeriod).toEqual({ bucket: 'day', range: null });
    expect(lastTwoMonths(new Date(Date.UTC(2026, 10, 4, 23, 30)))).toEqual({ from: '2026-10-01', to: '2026-12-01' });
    expect(lastTwoMonths(new Date(Date.UTC(2026, 0, 15)))).toEqual({ from: '2025-12-01', to: '2026-02-01' });
    expect(rangeOfChoice(DefaultTeamsPeriod, new Date(Date.UTC(2026, 10, 4)))).toEqual({ from: '2026-10-01', to: '2026-12-01' });
  });

  it('round-trips, and reads anything else back as the default', () => {
    const custom = { bucket: 'month', range: { from: '2026-01-01', to: '2026-07-01' } } as const;
    expect(readTeamsPeriod(writeTeamsPeriod(custom))).toEqual(custom);
    expect(readTeamsPeriod(writeTeamsPeriod({ bucket: 'month', range: null }))).toEqual({ bucket: 'month', range: null });
    for (const raw of [null, '', '{', '{"bucket":"week","range":null}', '{"bucket":"day","range":{"from":"2026-07-01","to":"2026-01-01"}}',
      '{"bucket":"day","range":{"from":"July","to":"2026-08-01"}}']) {
      expect(readTeamsPeriod(raw)).toEqual(DefaultTeamsPeriod);
    }
  });

  it('names the period: the last two months with their days, or a custom range by its own', () => {
    const now = new Date(Date.UTC(2026, 10, 4));
    expect(periodHeading(DefaultTeamsPeriod, now)).toBe('Last two months · Oct 1, 2026 – Nov 30, 2026');
    expect(periodHeading({ bucket: 'day', range: { from: '2026-09-01', to: '2026-10-01' } }, now)).toBe('September 2026');
    expect(periodHeading({ bucket: 'day', range: { from: '2026-09-03', to: '2026-09-10' } }, now)).toBe('Sep 3, 2026 – Sep 9, 2026');
    expect(bucketLabel('2026-11-03', 'day')).toBe('Nov 3');
    expect(bucketLabel('2026-11-01', 'month')).toBe('Nov 2026');
    expect(bucketAside('day')).toBe('by day');
    expect(bucketAside('month')).toBe('by month');
  });
});

describe('a row\'s figures', () => {
  it('are the route\'s total and Agent Hours, unchanged, with Unattributed first in its chart', () => {
    const [alpha, , gamma] = teamsCostPeriod().teams;
    expect(estimateText(alpha!, 'USD')).toBe('$810');
    expect(hoursText(alpha!)).toBe('13.5');
    expect(periodSeries(alpha!).map((s) => s.key)).toEqual(['none', 'o-billing', 'o-docs', 'o-research']);
    expect(periodSeries(alpha!)[1]!.weekly.map((w) => w.cost)).toEqual([120, 120, 120]);
    expect(estimateText(gamma!, 'USD')).toBe('—');
    expect(hoursText(gamma!)).toBe('0.0');
  });

  it('read Estimate unavailable when the team ran and nothing was priced, and carry the footnote marker', () => {
    expect(estimateText(teamsCostPeriod({ rate: null }).teams[0]!, 'USD')).toBe('Estimate unavailable');
    const legacy = teamsCostPeriod({ legacy: true }).teams[0]!;
    expect(estimateText(legacy, 'USD')).toBe('$810*');
    expect(estimateFootnote(legacy)).toBe('* Includes reconstructed rates');
    expect(estimateFootnote(teamsCostPeriod().teams[0]!)).toBeNull();
  });

  it('are drawn on one relative scale: the tallest stacked bucket of any team, in money or else in hours', () => {
    const answer = teamsCostPeriod();
    expect(periodPriced(answer)).toBe(true);
    expect(periodTop(answer, true)).toBe(540);
    expect(periodTop(answer, false)).toBe(9);
    expect(periodPriced(teamsCostPeriod({ rate: null }))).toBe(false);
  });
});

describe('the period\'s tiles', () => {
  it('are the route\'s total, prior and buckets, and its outcomes with Unattributed', () => {
    const answer = teamsCostPeriod();
    const tile = periodTile(answer, 'USD');
    expect(tile.value).toBe('$2,430');
    expect(tile.detail).toBe('40.5 Agent Hours at $60/h');
    expect(tile.change).toBe('▲ 100% vs prior period');
    expect(tile.weeks).toEqual(['2026-10-01', '2026-10-02', '2026-10-03']);
    expect(tile.series[0]!.weekly.map((w) => w.cost)).toEqual([810, 810, 810]);

    const bars = whereBars(answer, 'USD');
    expect(bars.map((b) => [b.key, b.amount])).toEqual([
      ['o-billing', '$1,080'], ['o-docs', '$540'], ['o-research', '$270'], ['unattributed', '$540'],
    ]);
  });

  it('read Estimate unavailable with Agent Hours unpriced, and a dash with no runs', () => {
    const unpriced = periodTile(teamsCostPeriod({ rate: null }), 'USD');
    expect(unpriced.value).toBe('Estimate unavailable');
    expect(unpriced.detail).toBe('40.5 Agent Hours');

    const empty = teamsCostPeriod();
    const none = periodTile({ ...empty, total: { agentSeconds: 0, cost: null }, prior: { agentSeconds: 0, cost: null }, outcomes: [] }, 'USD');
    expect(none.value).toBe('—');
    expect(none.change).toBeNull();
  });
});
