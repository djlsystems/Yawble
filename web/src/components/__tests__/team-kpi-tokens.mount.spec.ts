// @vitest-environment happy-dom
//
// THE TOKENS TILE AND THE OVER-BUDGET TOOLTIP, MOUNTED. What a person reads is asserted here rather
// than by reading `TeamKpiStrip.vue`'s source, including the tooltip, which is
// opened through QTooltip's own `show()` because it is absent from the DOM until then.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';

vi.mock('quasar', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  useQuasar: () => ({ notify: vi.fn() }),
}));

import TeamKpiStrip from '../TeamKpiStrip.vue';
import { useConsoleStore } from '../../stores/console';
import { asTeamId } from '../../api/types';
import type { TeamTokenTotals, TeamWorkflows } from '../../api/types';
import { bodyText, resetBody } from '../../test/mountQuasar';

const teamId = asTeamId('alpha');

const team = { id: teamId, name: 'Alpha', memberAgents: ['claude-headless'], repos: [] };

function totals(over: Partial<TeamTokenTotals> = {}): TeamTokenTotals {
  return {
    available: true,
    tokensIn: 1200,
    tokensOut: 340,
    tokensCachedIn: 5000,
    tokensCacheCreation: 80,
    tokensBillable: 2140,
    partial: false,
    runsWithUsage: 2,
    runsWithoutUsage: 0,
    missing: null,
    members: [
      {
        member: 'dev', brand: 'claude-headless', tokensIn: 1200, tokensOut: 340, runs: 2,
        tokensCachedIn: 5000, tokensCacheCreation: 80, tokensBillable: 2140,
      },
      {
        member: 'tester', brand: 'claude-headless', tokensIn: null, tokensOut: null, runs: 3,
        tokensCachedIn: null, tokensCacheCreation: null, tokensBillable: null,
      },
    ],
    ...over,
  } as TeamTokenTotals;
}

/** One open workflow with `tokensSpent` against an instance limit of 50,000,000. */
function oneOpenWorkflow(tokensSpent: number): TeamWorkflows {
  const started = new Date(Date.now() - 60_000).toISOString();
  return {
    available: true,
    openCount: 1,
    totalCount: 1,
    earliestStartedAt: started,
    serverNow: new Date().toISOString(),
    missing: null,
    workflows: [{
      correlationId: 1707,
      subject: 'Execute B001F',
      startedAt: started,
      endedAt: null,
      spend: { tokensSpent, partial: false },
      members: [],
    }],
  } as unknown as TeamWorkflows;
}

let wrapper: VueWrapper | undefined;

afterEach(() => {
  wrapper?.unmount();
  wrapper = undefined;
  resetBody();
});

async function mountStrip(usage: TeamTokenTotals | null, workflows: TeamWorkflows | null = null) {
  setActivePinia(createPinia());
  useConsoleStore().$patch({
    teams: [team],
    activeTeamId: teamId,
    overviewLanded: true,
    workflowSpendLimit: 50_000_000,
  });

  wrapper = mount(TeamKpiStrip, {
    attachTo: document.body,
    props: { teamId, containers: [], usage, timing: null, workflows, findings: [], clockOffset: 0 },
  });
  await flushPromises();
  return wrapper;
}

async function openTokensDialog(strip: VueWrapper): Promise<string> {
  await strip.find('.team-kpi--tokens').trigger('click');
  await flushPromises();
  return document.body.querySelector('.token-dialog-card')?.textContent?.replace(/\s+/g, ' ') ?? '';
}

describe('TeamKpiStrip token card', () => {
  it('says unavailable, and points to the Agents screen there', async () => {
    const strip = await mountStrip(totals({ available: false, missing: 'No usage on the log' }));

    expect(strip.find('.team-kpi--tokens').text()).toContain('unavailable');

    const dialog = await openTokensDialog(strip);
    expect(dialog).toContain('Unavailable. No usage on the log.');
    expect(dialog).toContain('Configure usage capture in the Agents screen.');
  });

  it('labels an available total as a team total', async () => {
    const strip = await mountStrip(totals());
    const tile = strip.find('.team-kpi--tokens').text();

    expect(tile).toContain('1,200');
    expect(tile).toContain('340');
    expect(tile).toContain('team total');
    expect(tile).not.toContain('partial');

    expect(await openTokensDialog(strip)).toContain('Team total from the message log.');
  });

  it('says partial when some runs have no usage', async () => {
    const strip = await mountStrip(totals({ partial: true, runsWithoutUsage: 1 }));

    expect(strip.find('.team-kpi-partial').text()).toBe('partial');
  });

  it('renders a member total the log does not have as the literal (unknown)', async () => {
    const strip = await mountStrip(totals());
    const dialog = await openTokensDialog(strip);

    const rows = [...document.body.querySelectorAll('[data-token-table="member"] tbody tr')].map((tr) =>
      [...tr.querySelectorAll('[data-cell]')].map((td) => (td.textContent ?? '').trim()),
    );
    // billable, in, cache read, cache write, out - one column each.
    expect(rows).toContainEqual(['2,140', '1,200', '5,000', '80', '340']);
    expect(rows).toContainEqual(['(unknown)', '(unknown)', '(unknown)', '(unknown)', '(unknown)']);
    expect(dialog).not.toContain('undefined');
  });

  // A member's name is shown whole, in a column of its own that wraps: the old row cut the name to
  // make room for the figures, and "De...416" named nobody.
  it('shows each member name in full, with its brand under it, and nothing cut', async () => {
    const long = 'DeveloperMireilleWithAVeryLongName';
    const base = totals();
    const strip = await mountStrip(totals({ members: [{ ...base.members[0]!, member: long }] }));
    await openTokensDialog(strip);

    const name = document.body.querySelector('[data-token-table="member"] tbody tr .token-name')!;
    expect(name.textContent).toContain(long);
    expect(name.textContent).toContain('claude-headless');
    expect(name.classList.contains('ellipsis')).toBe(false);
    expect(name.querySelector('.ellipsis')).toBeNull();
  });

  /** The retained feed is twenty messages a card still holds; summing it would count DOWN as the
   *  team got busier. The figure is the log total and nothing the containers carry. */
  it('shows the log total, not anything summed from containers', async () => {
    const strip = await mountStrip(totals({ tokensIn: 7, tokensOut: 3 }));

    expect(strip.find('.team-kpi--tokens').text()).toMatch(/7 in ·[\s\S]*· 3 out/);
  });
});

/**
 * THE TOOLTIP MUST NAME A CONTROL THAT EXISTS. The per-workflow budget is on Team settings;
 * this pins that the tooltip names it, and names Resume as the way back.
 */
describe('the over-budget tooltip on the workflow tile', () => {
  async function overBudgetTooltip(): Promise<string> {
    const strip = await mountStrip(null, oneOpenWorkflow(62_000_000));

    const budget = strip.find('.team-kpi-budget');
    expect(budget.exists(), 'no budget line on the workflow tile').toBe(true);

    const tooltip = budget.findComponent({ name: 'QTooltip' });
    expect(tooltip.exists(), 'no tooltip on an over-budget line').toBe(true);

    (tooltip.vm as unknown as { show: () => void }).show();
    await flushPromises();

    return document.body.querySelector('.q-tooltip')?.textContent?.replace(/\s+/g, ' ') ?? '';
  }

  it('names Team settings, where the per-workflow budget field now is', async () => {
    const text = await overBudgetTooltip();

    expect(text).toContain('set on Team settings, under General');
    expect(text).not.toContain("Raise the budget in the team's settings");
  });

  it('names Resume rather than teaching the reader the word nudge', async () => {
    const text = await overBudgetTooltip();

    expect(text).toContain('with a Resume button on its own row');
    expect(text).not.toMatch(/nudge/i);
  });

  it('has no tooltip while spend is under the budget', async () => {
    const strip = await mountStrip(null, oneOpenWorkflow(12_000_000));

    expect(strip.find('.team-kpi-budget').findComponent({ name: 'QTooltip' }).exists()).toBe(false);
  });
});
