// @vitest-environment happy-dom
//
// THE BUDGET LINE IS SHOWN WHILE SPEND IS HAPPENING, AND NOT OVER A FINISHED WORKFLOW.
//
// The budget is shown "while spend is happening". Two easy misreadings would invert that.
// `usePlural` sounds like "more than one" and means "the plural-capable rendering is usable"
// (`available && openCount > 0`), so gating on it bails out whenever any workflow is open. And the
// list is not open-only: `TeamWorkflows.workflows` is **"the open ones, or the newest closed one
// when none are open,"** so a closed workflow can satisfy a `length === 1` test.
//
// SO THE GATE IS `openCount`, WHICH IS THE QUESTION BEING ASKED. It is the uncapped count of open
// workflows; `workflows.length` is 1 in the closed-fallback case too.
//
// A PURE TEST CANNOT SEE ANY OF THIS. `budgetLine` can be correct and its own spec green while the
// tile shows nothing - what matters here is which props reach it, which is a question only a
// mounted component answers.
import { afterEach, describe, expect, it, vi } from 'vitest';

// The Activity tile reads `/activity` when it mounts. Nothing here is about it, so the read never
// answers, rather than reaching for a server that is not there.
vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  getTeamActivity: () => new Promise(() => {}),
}));
import { createPinia, setActivePinia } from 'pinia';
import { mount, flushPromises } from '@vue/test-utils';

import TeamKpiStrip from '../TeamKpiStrip.vue';
import { useConsoleStore } from '../../stores/console';
import { asTeamId } from '../../api/types';
import type { TeamWorkflows } from '../../api/types';
// Importing this installs the Quasar plugin at module level, which is what makes these components
// mountable at all. TeamKpiStrip is not a dialog, so nothing teleports and `wrapper.text()` works.
import { resetBody } from '../../test/mountQuasar';

const teamId = asTeamId('alpha');

const team = {
  id: teamId,
  name: 'Alpha',
  memberAgents: ['claude-headless'],
  repos: [],
};

/** One workflow, with a spend figure on it. `open` is expressed through `openCount`, because that is
 *  what the server means by it - the list itself carries closed rows in the fallback case. */
function workflows(openCount: number, tokensSpent: number | null): TeamWorkflows {
  const started = new Date(Date.now() - 60_000).toISOString();

  return {
    available: true,
    openCount,

    // One row in the list, open or closed. `openCount` still means OPEN; this is the uncapped total
    // the dialog's truncation line counts against, and the budget reads neither.
    totalCount: 1,
    earliestStartedAt: openCount > 0 ? started : null,
    serverNow: new Date().toISOString(),
    missing: null,
    workflows: [
      {
        correlationId: 1707,
        subject: 'Execute the plan',
        startedAt: started,
        endedAt: openCount > 0 ? null : new Date().toISOString(),
        spend: { tokensSpent, partial: false },

        // REQUIRED BY `workflowsTile`, which maps it. The fixture carries the shape the server
        // really sends rather than the minimum this spec reads - a fixture trimmed to what one
        // assertion touches is how a component that needs more comes up empty for a reason that
        // has nothing to do with the defect.
        members: [],
      },
    ],
  } as unknown as TeamWorkflows;
}

afterEach(resetBody);

function mountStrip(payload: TeamWorkflows | null) {
  setActivePinia(createPinia());

  const board = useConsoleStore();
  // THE LIMIT IS THE INSTANCE'S, not the team's, so the figure these tests measure against
  // is seeded on the store.
  board.$patch({
    teams: [team],
    activeTeamId: teamId,
    overviewLanded: true,
    workflowSpendLimit: 50_000_000,
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

describe('TeamKpiStrip, the budget under the workflow tile', () => {
  /**
   * THE CENTRAL CASE: one workflow running and a budget set, so the tile must say so. This is the
   * whole point - a bound that is only visible after the run is a bound nobody can act on.
   */
  it('shows spend against the budget while ONE workflow is open', async () => {
    const wrapper = mountStrip(workflows(1, 12_000_000));
    await flushPromises();

    expect(wrapper.text()).toContain('12,000,000 of 50,000,000');

    wrapper.unmount();
  });

  /**
   * AND SAYS "> budget" ONCE PAST IT, rather than a number that keeps climbing. Past the bound the
   * pump refuses every wake under that workflow, so a rising figure would suggest work being done.
   */
  it('stops counting and says so when spend passes the budget', async () => {
    const wrapper = mountStrip(workflows(1, 62_000_000));
    await flushPromises();

    expect(wrapper.text()).toContain('> limit');
    expect(wrapper.text()).not.toContain('62,000,000');

    wrapper.unmount();
  });

  /**
   * NOTHING OPEN MEANS NOTHING TO BOUND. The payload
   * still carries a row here - `TeamWorkflows.workflows` is "the open ones, OR the newest closed one
   * when none are open" - so a length test passes and the workflow on it is finished. A budget over
   * a workflow that can no longer spend is noise at best, and at worst reads as a live bound.
   */
  it('shows nothing when no workflow is open, even though the payload still carries a row', async () => {
    const wrapper = mountStrip(workflows(0, 0));
    await flushPromises();

    expect(wrapper.text()).not.toContain('of 50,000,000');

    wrapper.unmount();
  });

  /** With several open there is no single spend to compare, and naming an arbitrary one is worse
   *  than naming none. */
  it('shows nothing when several workflows are open', async () => {
    const wrapper = mountStrip(workflows(3, 12_000_000));
    await flushPromises();

    expect(wrapper.text()).not.toContain('of 50,000,000');

    wrapper.unmount();
  });

  /**
   * THE BAR ACROSS THE TOP OF THE TILE, asked for so the budget is readable at a glance rather than
   * by parsing two long numbers. It is decoration over a labelled fact: the line beneath says the
   * same thing in words, which is what keeps this inside the never-colour-alone rule.
   */
  it('draws a bar filled to the fraction spent', async () => {
    const wrapper = mountStrip(workflows(1, 12_500_000));
    await flushPromises();

    const fill = wrapper.find('.team-kpi-budget-bar__fill');

    expect(fill.exists()).toBe(true);
    expect(fill.attributes('style')).toContain('width: 25%');

    wrapper.unmount();
  });

  /**
   * RED WITHIN 10%, ORANGE BEFORE IT. The class carries the tone rather than an inline colour, so
   * the two live in the stylesheet next to each other and `styles-match-templates` can see both.
   */
  it('is not marked near while under ninety per cent', async () => {
    const wrapper = mountStrip(workflows(1, 44_000_000));
    await flushPromises();

    expect(wrapper.find('.team-kpi-budget-bar--near').exists()).toBe(false);

    wrapper.unmount();
  });

  it('is marked near once within ten per cent of the budget', async () => {
    const wrapper = mountStrip(workflows(1, 45_000_000));
    await flushPromises();

    expect(wrapper.find('.team-kpi-budget-bar--near').exists()).toBe(true);

    wrapper.unmount();
  });

  /** CLAMPED. A bar wider than its tile either overflows or is cut and reads as exactly full; the
   *  overshoot is carried by the words beneath, which say `> budget`. */
  it('fills to exactly full when spend is past the budget, never beyond', async () => {
    const wrapper = mountStrip(workflows(1, 62_000_000));
    await flushPromises();

    const fill = wrapper.find('.team-kpi-budget-bar__fill');

    expect(fill.attributes('style')).toContain('width: 100%');
    expect(wrapper.find('.team-kpi-budget-bar--near').exists()).toBe(true);

    wrapper.unmount();
  });

  /** Nothing open, nothing to bound, no bar - the same gate the line follows, from one computed. */
  it('draws no bar when no workflow is open', async () => {
    const wrapper = mountStrip(workflows(0, 0));
    await flushPromises();

    expect(wrapper.find('.team-kpi-budget-bar').exists()).toBe(false);

    wrapper.unmount();
  });

  /**
   * **THE BOUND, MOUNTED.** A bar measured against the wrong bound can read `37,850,774 of 100,000,000`
   * at 38% while the pump is refusing every wake - a bar that reassures, which is worse than
   * silence.
   *
   * There is one bound, so what has to hold is that the ceiling REACHES this tile from the store and is what the
   * figure is measured against. The lib spec pins the arithmetic; only a mounted one shows the
   * wiring.
   */
  it('measures against the instance ceiling, which is the only bound', async () => {
    setActivePinia(createPinia());

    const board = useConsoleStore();
    board.$patch({
      teams: [team],
      activeTeamId: teamId,
      overviewLanded: true,
      workflowSpendLimit: 30_000_000,
    });

    const wrapper = mount(TeamKpiStrip, {
      props: {
        teamId, containers: [], usage: null, timing: null,
        workflows: workflows(1, 37_850_774), findings: [], clockOffset: 0,
      },
    });

    await flushPromises();

    expect(wrapper.text()).toContain('> limit');
    expect(wrapper.find('.team-kpi-budget-bar--near').exists()).toBe(true);

    wrapper.unmount();
  });

  /** Every team is bounded by the ceiling, and this rendered nothing at all before - which is how a
   *  team that looked unbounded got stopped. */
  it('shows the ceiling for an ordinary team', async () => {
    setActivePinia(createPinia());

    const board = useConsoleStore();
    board.$patch({
      teams: [team],
      activeTeamId: teamId,
      overviewLanded: true,
      workflowSpendLimit: 30_000_000,
    });

    const wrapper = mount(TeamKpiStrip, {
      props: {
        teamId, containers: [], usage: null, timing: null,
        workflows: workflows(1, 5_000_000), findings: [], clockOffset: 0,
      },
    });

    await flushPromises();

    expect(wrapper.text()).toContain('30,000,000');

    wrapper.unmount();
  });

  /**
   * **THE BUDGET IS READ OFF THE OPEN WORKFLOW, NOT OFF THE NEWEST ONE.**
   *
   * The list carries CLOSED workflows alongside open ones, newest first — so `workflows[0]`
   * is "the newest workflow", which is very often a finished one. Indexing it would draw a RUNNING
   * workflow's budget from a newer CLOSED workflow's spend: a figure that cannot move while the
   * pump keeps spending under the other one.
   *
   * `endedAt === null` is the open test, the same one `workflowTiming` uses.
   */
  it('reads spend from the OPEN workflow, not from a newer closed one above it', async () => {
    setActivePinia(createPinia());

    const board = useConsoleStore();
    board.$patch({
      teams: [team],
      activeTeamId: teamId,
      overviewLanded: true,
      workflowSpendLimit: 50_000_000,
    });

    const started = new Date(Date.now() - 60_000).toISOString();

    const payload = {
      available: true,

      // ONE open workflow, which is what gates the line at all - and it is the SECOND entry.
      openCount: 1,
      totalCount: 2,
      earliestStartedAt: started,
      serverNow: new Date().toISOString(),
      missing: null,
      workflows: [
        // Newest, and CLOSED. Its spend must not be the figure on the tile.
        {
          correlationId: 1708,
          subject: 'Already delivered',
          startedAt: started,
          endedAt: new Date().toISOString(),
          spend: { tokensSpent: 1_000_000, partial: false },
          members: [],
        },
        // Older, and OPEN. This is the workflow the bound applies to.
        {
          correlationId: 1707,
          subject: 'Still running',
          startedAt: started,
          endedAt: null,
          spend: { tokensSpent: 12_000_000, partial: false },
          members: [],
        },
      ],
    } as unknown as TeamWorkflows;

    const wrapper = mount(TeamKpiStrip, {
      props: {
        teamId, containers: [], usage: null, timing: null,
        workflows: payload, findings: [], clockOffset: 0,
      },
    });

    await flushPromises();

    expect(wrapper.text()).toContain('12,000,000 of 50,000,000');
    expect(wrapper.text()).not.toContain('1,000,000 of 50,000,000');

    wrapper.unmount();
  });

  /** An instance with no ceiling configured must stay quiet - a line that always shows is a line
   *  nobody reads. */
  it('shows nothing when the instance sets no limit', async () => {
    setActivePinia(createPinia());

    const board = useConsoleStore();
    board.$patch({
      teams: [team],
      activeTeamId: teamId,
      overviewLanded: true,
      workflowSpendLimit: null,
    });

    const wrapper = mount(TeamKpiStrip, {
      props: {
        teamId,
        containers: [],
        usage: null,
        timing: null,
        workflows: workflows(1, 12_000_000),
        findings: [],
        clockOffset: 0,
      },
    });

    await flushPromises();

    expect(wrapper.text()).not.toContain('12,000,000 of');

    wrapper.unmount();
  });
});
