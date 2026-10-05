// @vitest-environment happy-dom
//
// WHAT THE BAR AND THE LINE MEASURE AGAINST, WHEN A TEAM CAN CHOOSE.
//
// **THE BAR EXISTS SO A CEILING IS NEVER INVISIBLE.** A team stopped by an instance ceiling that
// appears on no screen, while its tile displays a percentage of a bound that is not operating, is
// the failure. A bar that reassures is worse than no bar - so the one thing it may never measure
// against is a figure that is not in force.
//
// THERE ARE THREE NUMBERS AND ONLY ONE OF THEM IS THE ANSWER.
//
// - `Overview.workflowSpendLimit` - the INSTANCE figure, what applies to a team that has chosen
//   nothing.
// - `Team.budgetTokens` - the team's stored CHOICE. `null` = chosen nothing, `0` = chose unlimited,
//   `> 0` = the figure typed. **The bar must never read this.**
// - `Team.effectiveWorkflowBudget` - what the SERVER resolved from those two. `null` = unlimited,
//   and 0 never appears.
//
// The resolution is the server's and the browser must not repeat it. A second resolver here is how
// a bar and a pump come to disagree about the same workflow.
import { afterEach, describe, expect, it, vi } from 'vitest';

// The Activity tile reads `/activity` when it mounts. Nothing here is about it, so the read never
// answers, rather than reaching for a server that is not there.
vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  getTeamActivity: () => new Promise(() => {}),
}));
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, mount } from '@vue/test-utils';

import TeamKpiStrip from '../TeamKpiStrip.vue';
import { useConsoleStore } from '../../stores/console';
import { asTeamId } from '../../api/types';
import type { Team, TeamWorkflows } from '../../api/types';
import { resetBody } from '../../test/mountQuasar';

const teamId = asTeamId('alpha');

/** The INSTANCE figure. Deliberately different from every team figure below, so a test that passes
 *  by accident on a shared number cannot. */
const InstanceLimit = 50_000_000;

function team(over: Partial<Team> = {}): Team {
  return {
    id: teamId,
    name: 'Alpha',
    memberAgents: ['claude-headless'],
    repos: [],
    ...over,
  } as Team;
}

function workflows(tokensSpent: number): TeamWorkflows {
  const started = new Date(Date.now() - 60_000).toISOString();

  return {
    available: true,
    openCount: 1,
    totalCount: 1,
    earliestStartedAt: started,
    serverNow: new Date().toISOString(),
    missing: null,
    workflows: [
      {
        correlation: 1707,
        subject: 'Execute B001F',
        startedAt: started,
        endedAt: null,
        spend: { tokensSpent, partial: false },
        members: [],
      },
    ],
  } as unknown as TeamWorkflows;
}

afterEach(resetBody);

function mountStrip(teamRecord: Team, payload: TeamWorkflows) {
  setActivePinia(createPinia());

  const board = useConsoleStore();
  board.$patch({
    teams: [teamRecord],
    activeTeamId: teamId,
    overviewLanded: true,
    workflowSpendLimit: InstanceLimit,
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

describe('the workflow budget bar reads what the server resolved', () => {
  /**
   * THE TEAM'S OWN FIGURE, IN FORCE. A team that has chosen 200,000,000 is measured against
   * 200,000,000 even though the instance's own backstop is 50,000,000: the number is exactly what
   * the creator typed, and the instance figure does not override a team's higher one.
   */
  it('measures against the team’s effective budget, not the instance figure', async () => {
    const wrapper = mountStrip(
      team({ budgetTokens: 200_000_000, effectiveWorkflowBudget: 200_000_000 }),
      workflows(50_000_000),
    );
    await flushPromises();

    expect(wrapper.text()).toContain('50,000,000 of 200,000,000');

    // And NOT over: 50M of 200M is a quarter of the way through, where against the instance figure
    // it would read `> limit`. This is the case a wrong reading gets exactly backwards.
    expect(wrapper.text()).not.toContain('> limit');

    wrapper.unmount();
  });

  /**
   * NEVER `budgetTokens`. It is the stored CHOICE, not the resolved figure - a team that has
   * chosen nothing carries `null` there while a real bound is in force, and a team that chose
   * unlimited carries `0`, which `isUnlimited` would read as no bound at all. Both readings put a
   * wrong bar on the screen.
   */
  it('ignores the stored choice when the server resolved something else', async () => {
    const wrapper = mountStrip(
      team({ budgetTokens: null, effectiveWorkflowBudget: InstanceLimit }),
      workflows(12_500_000),
    );
    await flushPromises();

    expect(wrapper.text()).toContain('12,500,000 of 50,000,000');

    wrapper.unmount();
  });

  /**
   * A TEAM THAT CHOSE UNLIMITED DRAWS NOTHING. `budgetTokens: 0` is a real choice and the server
   * answers `effectiveWorkflowBudget: null` for it. A bar drawn from the instance figure here
   * would be a bound that is not operating.
   */
  it('draws nothing for a team the server resolved as unlimited', async () => {
    const wrapper = mountStrip(
      team({ budgetTokens: 0, effectiveWorkflowBudget: null }),
      workflows(80_000_000),
    );
    await flushPromises();

    expect(wrapper.text()).not.toContain('of 50,000,000');
    expect(wrapper.text()).not.toContain('> limit');
    expect(wrapper.find('.team-kpi-budget-bar__fill').exists()).toBe(false);

    wrapper.unmount();
  });

  /**
   * A HOST THAT DOES NOT SEND THE FIELD: absent is not the same answer as `null`. `null` is the
   * server saying UNLIMITED; `undefined` is a Host with nothing to say, in which case the one bound it does have - the instance figure - is the one in force.
   */
  it('falls back to the instance figure when the Host never sent an effective budget', async () => {
    const wrapper = mountStrip(team(), workflows(12_500_000));
    await flushPromises();

    expect(wrapper.text()).toContain('12,500,000 of 50,000,000');

    wrapper.unmount();
  });

  /** The bar is drawn from the same figure the line names, so the two can never disagree. */
  it('fills the bar against the effective budget', async () => {
    const wrapper = mountStrip(
      team({ budgetTokens: 200_000_000, effectiveWorkflowBudget: 200_000_000 }),
      workflows(50_000_000),
    );
    await flushPromises();

    expect(wrapper.find('.team-kpi-budget-bar__fill').attributes('style')).toContain('width: 25%');

    wrapper.unmount();
  });
});
