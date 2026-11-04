import { describe, expect, it } from 'vitest';
import {
  NoRunsLine, NotPriced, activityLine, figureText, memberRows, outcomeRows, periodName, teamPeriodSeries, teamPeriodTile,
  tileTitle, workflowLine, workflowRows,
} from '../teamPeriod';
import { needsYouOnTeam } from '../needsYou';
import { teamCostPeriod } from '../../test/teamPeriodFixtures';
import type { NeedsYouItem } from '../../api/types';

// A TEAM PAGE'S PERIOD, WORDED: every figure the route's, never priced or added up here.

const now = new Date('2026-11-04T12:00:00Z');

describe('the period\'s name', () => {
  it('is the last two months, a whole month by name, or a first and last day', () => {
    expect(periodName({ bucket: 'day', range: null }, now)).toBe('last two months');
    expect(periodName({ bucket: 'month', range: { from: '2026-09-01', to: '2026-10-01' } }, now)).toBe('September 2026');
    expect(periodName({ bucket: 'day', range: { from: '2026-09-15', to: '2026-10-16' } }, now)).toBe('Sep 15, 2026 – Oct 15, 2026');
    expect(tileTitle({ bucket: 'day', range: null }, now)).toBe('Estimated Execution Cost · last two months');
  });
});

describe('the tile\'s words', () => {
  it('take the route\'s total, Agent Hours and weighted rate unchanged', () => {
    expect(teamPeriodTile(teamCostPeriod())).toEqual({ value: '$750', detail: '12.0 Agent Hours at $60/h', footnote: null });
    expect(teamPeriodTile(teamCostPeriod({ legacy: true }))).toEqual({
      value: '$750*', detail: '12.0 Agent Hours at $60/h', footnote: '* Includes reconstructed rates' });
  });

  it('say Estimate unavailable for a team that ran unpriced and a dash for one that did not run', () => {
    expect(teamPeriodTile(teamCostPeriod({ unpriced: true })).value).toBe('Estimate unavailable');
    expect(teamPeriodTile(teamCostPeriod({ empty: true })).value).toBe('—');
    expect(figureText({ cost: null, executions: 0, agentSeconds: 0 }, 'USD')).toBe('—');
    expect(figureText({ cost: 0, executions: 1, agentSeconds: 1, legacyRates: [] }, 'USD')).toBe('$0');
  });

  it('stack the chart by outcome in the route\'s order, Unattributed first', () => {
    const series = teamPeriodSeries(teamCostPeriod());
    expect(series.map((s) => s.key)).toEqual(['none', 'o-billing', 'o-docs']);
    expect(series[1]!.weekly.map((w) => w.cost)).toEqual([120, 120, 120]);
  });
});

describe('the breakdowns', () => {
  it('word each member\'s own figure, agents and plugins apart, an unpriced plugin Not priced', () => {
    const rows = memberRows(teamCostPeriod({ legacy: true }));
    expect(rows.agents.map((r) => [r.name, r.executions, r.hours, r.estimate])).toEqual([['Ana', 3, '6.0', '$360'], ['Dev', 3, '6.0', '$360*']]);
    expect(rows.plugins.map((r) => [r.name, r.executions, r.hours, r.estimate])).toEqual([['Bare', 1, '—', NotPriced], ['Tool', 1, '—', '$30']]);
    expect(rows.agents[1]!.footnote).toBe('* Includes reconstructed rates');
  });

  it('word each outcome and workflow as the route answers them', () => {
    expect(outcomeRows(teamCostPeriod({ legacy: true })).map((r) => [r.name, r.estimate])).toEqual([['Unattributed', '$210*'], ['Ship billing', '$360'], ['Docs refresh', '$180']]);
    expect(workflowRows(teamCostPeriod({ legacy: true })).map((r) => [r.name, r.executions, r.estimate])).toEqual([
      ['#42', 3, '$360'], ['#41', 2, '$180*'], ['#40', 1, '$180'], ['#39', 2, '$30']]);
  });
});

describe('the lines', () => {
  it('say a workflow\'s Agent Hours and Estimated Execution Cost', () => {
    const [first] = teamCostPeriod().workflows;
    expect(workflowLine(first!.figures, 'USD')).toBe('Agent Hours 6.0 · Estimated Execution Cost $360');
    expect(NoRunsLine).toBe('Agent Hours 0.0 · Estimated Execution Cost —');
  });

  it('say the Activity tile\'s executions, Agent Hours and active members, one of each in the singular', () => {
    expect(activityLine(teamCostPeriod().totals)).toBe('8 executions · 12.0 Agent Hours · 4 active members');
    expect(activityLine({ executions: 1, agentSeconds: 1800, activeMembers: 1 })).toBe('1 execution · 0.5 Agent Hours · 1 active member');
  });
});

describe('Needs you, one team\'s part', () => {
  const item = (team: string, holds: string[] = []): NeedsYouItem => ({
    kind: holds.length ? 'connection' : 'blocked', team: { id: team, name: team }, member: null, card: null, workflow: null,
    item: null, since: '2026-11-01T00:00:00Z', words: '', where: { view: 'workflow', team },
    connection: holds.length ? { id: 'c', name: 'c', holds: holds.map((h) => ({ team: { id: h, name: h }, members: [] })) } : null,
  } as NeedsYouItem);

  it('is the list\'s rows naming the team, any case, and a connection on each team it holds up', () => {
    const items = [item('alpha'), item('ALPHA'), item('beta'), item('beta', ['beta', 'alpha'])];
    expect(needsYouOnTeam(items, 'alpha')).toHaveLength(3);
    expect(needsYouOnTeam(items, 'beta')).toHaveLength(2);
    expect(needsYouOnTeam(items, 'gamma')).toHaveLength(0);
  });
});
