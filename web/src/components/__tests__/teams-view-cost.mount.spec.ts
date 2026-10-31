// @vitest-environment happy-dom
//
// THE TEAMS LIST'S COST · 8 WEEKS COLUMN, in place of the Activity column: each team's
// stacked weekly chart at row height, its period total and ⚠ when one of its outcomes is over
// budget, all from ONE call for the whole list; a click on it opens that team's Cost dialog rather
// than the team. The Activity column's Same time span switch and legend are gone.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';

const { getWip, listRemovals, getTeamsCost, getTeamCost, getTeamTokens } = vi.hoisted(() => ({
  getWip: vi.fn(),
  listRemovals: vi.fn(),
  getTeamsCost: vi.fn(),
  getTeamCost: vi.fn(),
  getTeamTokens: vi.fn(),
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
}));

import TeamsView from '../TeamsView.vue';
import TeamCostDialog from '../TeamCostDialog.vue';
import { useConsoleStore } from '../../stores/console';
import { asTeamId, type TeamId, type TeamTokenTotals } from '../../api/types';
import type { TeamsCost } from '../../api/outcomes';
import { resetBody } from '../../test/mountQuasar';
import { teamCost, Weeks, type CostOptions } from '../../test/costFixtures';

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
  resetBody();
});

async function mountView(latest: string | null = null, roster = members) {
  setActivePinia(createPinia());
  const board = useConsoleStore();
  board.$patch({
    teams: [
      { id: 'alpha' as TeamId, name: 'Alpha', paused: false, containers: roster },
      { id: 'beta' as TeamId, name: 'Beta', paused: false, containers: roster },
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

describe('the Cost · 8 weeks column', () => {
  it('replaces Activity, with no Same time span switch and no legend', async () => {
    const { view } = await mountView();

    const headings = view.findAll('thead th').map((th) => th.text().replace(/arrow_(up|down)ward/, '').replace(/\s+/g, ' ').trim());
    expect(headings).toEqual(['Team', 'Members', 'Status', 'Cost · 8 weeks', '']);
    expect(view.find('thead th[data-col="activity"]').exists()).toBe(false);
    expect(view.find('[data-activity-relative]').exists()).toBe(false);
    expect(view.find('[data-activity-legend-anchor]').exists()).toBe(false);
  });

  it('reads every team\'s figures in ONE call, never one per row', async () => {
    await mountView();

    expect(getTeamsCost).toHaveBeenCalledTimes(1);
    expect(getTeamCost).not.toHaveBeenCalled();
  });

  it('draws each team\'s stacked weekly chart at row height, with its period total beside it', async () => {
    const { view } = await mountView();

    const cell = row(view, 'alpha').find('[data-teams-cost]');
    expect(cell.find('[data-cost-chart]').attributes('data-size')).toBe('row');
    expect(cell.findAll('[data-cost-segment][data-week="0"]').map((s) => s.attributes('data-cost-segment')))
      .toEqual(['none', 'o-billing', 'o-docs', 'o-research']);
    expect(cell.find('[data-teams-cost-total]').text()).toBe('$1,080');
  });

  it('gives one outcome one colour on every row, whatever its place among each team\'s outcomes', async () => {
    // Alpha serves all three outcomes, Beta only Research: Research is third on Alpha's row and the
    // only one on Beta's, and its colour is its global rank's slot on both.
    const both = teamsCost();
    const beta = both.teams[1]!;
    beta.outcomes = beta.outcomes.filter((o) => o.id === null || o.id === 'o-research');
    getTeamsCost.mockResolvedValue(both);
    const { view } = await mountView();

    const fillOf = (team: string, key: string) =>
      row(view, team).find(`[data-cost-segment="${key}"][data-week="0"]`).attributes('style');
    expect(row(view, 'beta').findAll('[data-cost-segment][data-week="0"]').map((s) => s.attributes('data-cost-segment')))
      .toEqual(['none', 'o-research']);
    expect(fillOf('alpha', 'o-research')).toContain('var(--os-series-3)');
    expect(fillOf('beta', 'o-research')).toContain('var(--os-series-3)');
    expect(fillOf('beta', 'o-research')).not.toContain('var(--os-series-1)');
    expect(fillOf('beta', 'none')).toContain('var(--os-ink-faint)');
    expect(getTeamsCost).toHaveBeenCalledTimes(1);
  });

  it('shows ⚠ only on a team with an outcome over budget', async () => {
    const { view } = await mountView();

    expect(row(view, 'alpha').find('[data-teams-cost-over]').exists()).toBe(false);
    expect(row(view, 'beta').find('[data-teams-cost-over]').text()).toBe('⚠');
  });

  it('shows hours, never $0, without a rate', async () => {
    getTeamsCost.mockResolvedValue(teamsCost({ rate: null }));
    const { view } = await mountView();

    expect(row(view, 'alpha').find('[data-teams-cost-total]').text()).toBe('18.0 agent h');
    expect(row(view, 'alpha').find('[data-teams-cost]').text()).not.toContain('$');
  });

  it('shows a dash for a team the answer does not carry', async () => {
    getTeamsCost.mockResolvedValue({ ...teamsCost(), teams: [] });
    const { view } = await mountView();

    expect(row(view, 'alpha').find('[data-teams-cost-none]').text()).toBe('—');
  });

  it('opens that team\'s Cost dialog on a click, not the team', async () => {
    const { view, setActive } = await mountView();

    await row(view, 'beta').find('[data-teams-cost]').trigger('click');
    await flushPromises();

    expect(setActive).not.toHaveBeenCalled();
    const dialog = view.findComponent(TeamCostDialog);
    expect(dialog.props('modelValue')).toBe(true);
    expect(dialog.props('teamId')).toBe('beta');
    expect(document.body.querySelector('[data-cost-title]')?.textContent?.trim()).toBe('Cost · Beta');
    expect(document.body.querySelector('[data-cost-row="o-docs"] [data-cost-over]')).not.toBeNull();
    // Still the one call: the dialog reads the list's own answer.
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
