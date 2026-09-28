// @vitest-environment happy-dom
//
// AFTER A NUDGE OR A RESUME, THE BAR MUST COME DOWN.
//
// **THE RISK.** `TeamWorkflowTiming.spend` is filled from `GetWorkflowSpendAsync` — the WHOLE
// workflow, cumulative, and it never comes down — while the pump's own guard decides on
// `GetSpendSinceNudgeAsync`, which resets at every nudge. A bar drawn from `spend` would show a
// person who nudged a workflow back into life, or resumed a paused one, `> limit` over a workflow
// the server had already released. **The display would contradict the decision it is about**:
// a bar that is not measuring the figure in force.
//
// **WHY TWO FIELDS AND NOT ONE.** `spend` cannot simply become the window —
// `BacklogExecutionRecord.cs` reads it for *what did this backlog item cost*, which is
// genuinely a whole-workflow question. Two questions, two fields:
// `spend` stays cumulative, `spendSinceNudge` is the windowed figure, and **the bar measures the
// window** because that is what the guard decided on.
//
// **NEVER `budgetTokens`.** What the window is measured AGAINST is
// `effectiveWorkflowBudget`, the figure the server resolved. `team-kpi-effective-budget.mount.spec`
// pins that half; this file pins which SPEND reaches it.
//
// A PURE TEST CANNOT SEE ANY OF THIS. `budgetLine` and `budgetBar` can be correct while
// the wrong number is handed to them, and which number that is is a question only a mounted component
// answers.
import { afterEach, describe, expect, it } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, mount } from '@vue/test-utils';

import TeamKpiStrip from '../TeamKpiStrip.vue';
import { useConsoleStore } from '../../stores/console';
import { asTeamId } from '../../api/types';
import type { Team, TeamWorkflows } from '../../api/types';
import { resetBody } from '../../test/mountQuasar';

const teamId = asTeamId('alpha');

/** The bound in force. The team has chosen it, so the server resolved it — nothing here reads the
 *  instance figure, which is deliberately different below. */
const Budget = 50_000_000;

const team = {
  id: teamId,
  name: 'Alpha',
  memberAgents: ['claude-headless'],
  repos: [],
  budgetTokens: Budget,
  effectiveWorkflowBudget: Budget,
} as unknown as Team;

/**
 * One OPEN workflow carrying both spend figures. `spendSinceNudge` is passed as an explicit
 * argument rather than derived, because the whole point is that the two can differ wildly and the
 * tile has to pick the right one.
 */
function workflows(
  cumulativeTokens: number,
  windowTokens: number | null | undefined,
): TeamWorkflows {
  const started = new Date(Date.now() - 60_000).toISOString();

  const row: Record<string, unknown> = {
    correlation: 1707,
    subject: 'Execute B001F',
    startedAt: started,
    endedAt: null,
    spend: { tokensSpent: cumulativeTokens, runsWithMeasuredUsage: 4, runsWithoutUsage: 0 },
    members: [],
  };

  // ABSENT IS NOT NULL HERE EITHER: an older Host omits the key entirely, and that is the case the
  // fallback exists for. Writing `spendSinceNudge: undefined` would not be the same fixture.
  if (windowTokens !== undefined) {
    row.spendSinceNudge =
      windowTokens === null
        ? null
        : { tokensSpent: windowTokens, runsWithMeasuredUsage: 1, runsWithoutUsage: 0 };
  }

  return {
    available: true,
    openCount: 1,
    totalCount: 1,
    earliestStartedAt: started,
    serverNow: new Date().toISOString(),
    missing: null,
    workflows: [row],
  } as unknown as TeamWorkflows;
}

afterEach(resetBody);

function mountStrip(payload: TeamWorkflows) {
  setActivePinia(createPinia());

  const board = useConsoleStore();
  board.$patch({
    teams: [team],
    activeTeamId: teamId,
    overviewLanded: true,

    // Deliberately NOT the bound: the team chose its own, so a figure that leaked in from here
    // would be visible rather than accidentally right.
    workflowSpendLimit: 30_000_000,
  });

  return mount(TeamKpiStrip, {
    props: {
      teamId,
      containers: [],
      usage: null,
      timing: null,
      workflows: payload,
      findings: [],
      clockOffset: 0,
    },
  });
}

describe('the budget bar measures the window the guard decided on', () => {
  /**
   * **THE CASE THIS CARD EXISTS TO CLOSE.** 98,000,000 cumulative — long past a 50,000,000 bound —
   * and 2,000,000 since the nudge that released it. The server is letting this workflow run; the
   * tile must say so.
   */
  it('reads the windowed figure when a large total sits beside a small window', async () => {
    const wrapper = mountStrip(workflows(98_000_000, 2_000_000));
    await flushPromises();

    expect(wrapper.text()).toContain('2,000,000 of 50,000,000');
    expect(wrapper.text()).not.toContain('> limit');

    wrapper.unmount();
  });

  /** And the BAR comes down with the line, from the same figure, so the two cannot disagree. */
  it('draws the bar from the window, not from the cumulative total', async () => {
    const wrapper = mountStrip(workflows(98_000_000, 12_500_000));
    await flushPromises();

    const fill = wrapper.find('.team-kpi-budget-bar__fill');

    expect(fill.attributes('style')).toContain('width: 25%');
    expect(wrapper.find('.team-kpi-budget-bar--near').exists()).toBe(false);

    wrapper.unmount();
  });

  /**
   * A WINDOW OF ZERO IS A MEASUREMENT, not an absence — it is what the instant after a nudge looks
   * like. A `||` anywhere on this path falls straight back to the cumulative figure, which is
   * wrong in the one state where it is most visible.
   */
  it('reads a window of zero as zero rather than falling back', async () => {
    const wrapper = mountStrip(workflows(98_000_000, 0));
    await flushPromises();

    expect(wrapper.text()).toContain('0 of 50,000,000');
    expect(wrapper.text()).not.toContain('> limit');

    wrapper.unmount();
  });

  /** A workflow genuinely over its budget still reads over — this is a fix to WHICH figure is
   *  measured, never a softening of what the measurement says. */
  it('still says over when the window itself is past the budget', async () => {
    const wrapper = mountStrip(workflows(98_000_000, 62_000_000));
    await flushPromises();

    expect(wrapper.text()).toContain('> limit');

    wrapper.unmount();
  });

  /**
   * A HOST THAT DOES NOT SEND `spendSinceNudge` still gets a bar rather than nothing. Falling back
   * to `spend` keeps it; treating absence as no-measurement would take the bar away.
   */
  it('falls back to the cumulative total when the Host never sent the window', async () => {
    const wrapper = mountStrip(workflows(12_500_000, undefined));
    await flushPromises();

    expect(wrapper.text()).toContain('12,500,000 of 50,000,000');
    expect(wrapper.find('.team-kpi-budget-bar__fill').attributes('style')).toContain('width: 25%');

    wrapper.unmount();
  });
});
