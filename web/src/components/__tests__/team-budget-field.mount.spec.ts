// @vitest-environment happy-dom
//
// THE PER-WORKFLOW BUDGET, ON BOTH DIALOGS, MOUNTED.
//
// THE RULE: **it stores exactly what was typed, including a figure ABOVE the instance's own
// `WorkflowSpendLimit`.** A lower-only variant is deliberately not what this is - "a field that
// silently refuses what a person typed is a worse lie than a number they can change" - so a clamp
// here is not a safety net, it is the defect.
//
// A SOURCE READ CANNOT SETTLE ANY OF THIS. What a person sees in the box, and what leaves the
// browser when they press the button, are rendering questions - and a spec that greps for a string
// can stay green over a broken dialog.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises } from '@vue/test-utils';

const {
  createTeam,
  fileSystemRoots,
  listCatalog,
  getTeamEnv,
  setMemberAgents,
  setTeamAdditionalInstructions,
  setTeamRepos,
  setTeamWorkflowBudget,
} = vi.hoisted(() => ({
  createTeam: vi.fn(),
  fileSystemRoots: vi.fn(),
  listCatalog: vi.fn(),
  getTeamEnv: vi.fn(),
  setMemberAgents: vi.fn(),
  setTeamAdditionalInstructions: vi.fn(),
  setTeamRepos: vi.fn(),
  setTeamWorkflowBudget: vi.fn(),
}));

vi.mock('quasar', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  useQuasar: () => ({ notify: vi.fn() }),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  getInstanceId: async () => 'instance-a',
  createTeam,
  fileSystemRoots,
  listCatalog,
  getTeamEnv,
  setMemberAgents,
  setTeamAdditionalInstructions,
  setTeamRepos,
  setTeamWorkflowBudget,
}));

import CreateTeamDialog from '../CreateTeamDialog.vue';
import TeamSettingsDialog from '../TeamSettingsDialog.vue';
import { useConsoleStore } from '../../stores/console';
import { useSessionStore } from '../../stores/session';
import type { Team, TeamId } from '../../api/types';
import { remember } from '../../lib/newTeamDefaults';
import { BudgetFieldFractionRefusal } from '../../lib/teamBudget';
import { mountDialog, resetBody } from '../../test/mountQuasar';

/** The instance's own `WorkflowSpendLimit`, as `GET /api/overview` answers it. What both boxes are
 *  prefilled with when the team has chosen nothing. */
const InstanceLimit = 100_000_000;

const BudgetLabel = 'Budget for one workflow (tokens)';

function team(over: Partial<Team> = {}): Team {
  return {
    id: 'alpha' as TeamId,
    name: 'Alpha',
    memberAgents: ['claude-headless'],
    additionalInstructions: null,
    paused: false,
    repos: [],
    ...over,
  } as Team;
}

beforeEach(() => {
  createTeam.mockReset();
  createTeam.mockResolvedValue({ id: 'beta', name: 'Beta' });

  fileSystemRoots.mockReset();
  fileSystemRoots.mockResolvedValue({ roots: [] });

  listCatalog.mockReset();
  listCatalog.mockResolvedValue({
    agents: [{ name: 'claude-headless', mode: 'Headless' }],
  });

  getTeamEnv.mockReset();
  getTeamEnv.mockResolvedValue({});

  setMemberAgents.mockReset();
  setTeamAdditionalInstructions.mockReset();
  setTeamRepos.mockReset();

  setTeamWorkflowBudget.mockReset();
  setTeamWorkflowBudget.mockResolvedValue(team());
});

afterEach(() => {
  vi.unstubAllGlobals();
  resetBody();
});

function budgetInput(wrapper: { findAllComponents: (q: { name: string }) => unknown[] }) {
  const inputs = wrapper.findAllComponents({ name: 'QInput' }) as {
    props: (name: string) => unknown;
    setValue: (value: unknown) => Promise<void>;
  }[];

  const found = inputs.find((input) => input.props('label') === BudgetLabel);

  if (!found) throw new Error(`no "${BudgetLabel}" input in the rendered dialog`);

  return found;
}

/**
 * WAIT FOR THE FIELD TO HAVE SAID ITS PIECE. Quasar runs a `:rules` check through `debounce(fn, 0)`,
 * so the refusal lands one MACROTASK after the model changes - `flushPromises` alone drains
 * microtasks and can read the box before it has validated at all, which is a spec that passes on a
 * dialog that never refuses anything.
 */
async function validated(): Promise<void> {
  await flushPromises();
  await new Promise((resolve) => setTimeout(resolve, 0));
  await flushPromises();
}

function buttonLabelled(label: string): HTMLButtonElement {
  const found = [...document.body.querySelectorAll('button')]
    .find((candidate) => candidate.textContent?.trim() === label);

  if (!found) throw new Error(`no ${label} button in the rendered dialog`);

  return found as HTMLButtonElement;
}

describe('CreateTeamDialog: the per-workflow budget', () => {
  /**
   * THE OTHER CHOICES, SEEDED THE WAY A RETURNING PERSON HAS THEM. `applyDefaults` preselects
   * NOTHING from an empty catalog history - by design, since a team created on whatever sorted
   * first is a team hiring on words nobody chose - so `formIsLegal()` is false and Create is
   * disabled until both are answered. Remembering them is what a second visit looks like, and
   * it leaves the budget as the only field these cases actually touch.
   */
  async function mountCreate() {
    remember({
      managerAgent: 'claude-headless',
      memberAgents: ['claude-headless'],
      root: null,
      repos: [],
    }, 'instance-a');

    setActivePinia(createPinia());

    const board = useConsoleStore();
    board.$patch({ teams: [], overviewLanded: true, workflowSpendLimit: InstanceLimit });

    const session = useSessionStore();
    session.$patch({ user: { id: 'u1', email: 'admin@example.com' } });

    const wrapper = await mountDialog(CreateTeamDialog, {}, { pinia: false });

    // THE BUDGET IS ON THE ADVANCED TAB, which renders only once it is opened.
    document.body.querySelector<HTMLElement>('[data-advanced-tab]')!.click();
    await vi.waitFor(() => {
      if (!document.body.querySelector('[data-advanced-settings]')) throw new Error('the Advanced panel has not rendered');
    });
    await validated();

    return wrapper;
  }

  /**
   * PREFILLED. Nobody is asked to invent a number: the field arrives
   * holding the figure already in force, and editing it is a deliberate act rather than the price
   * of creating a team. A blank box demanding a figure is a different control entirely.
   */
  it('arrives holding the instance figure', async () => {
    const wrapper = await mountCreate();

    expect(budgetInput(wrapper).props('modelValue')).toBe(InstanceLimit);

    wrapper.unmount();
  });

  /**
   * NO CLAMP, PINNED AS A TEST. A figure above the instance's own is submitted unchanged. The
   * route is `{team}`-gated and anyone who can reach the team can lift their own ceiling, so a
   * clamp would be no backstop - and a silent lowering here would be a lie.
   */
  it('submits a figure ABOVE the instance limit exactly as typed', async () => {
    const wrapper = await mountCreate();

    await budgetInput(wrapper).setValue(400_000_000);
    await flushPromises();

    buttonLabelled('Create team').click();
    await flushPromises();

    expect(createTeam).toHaveBeenCalledOnce();
    expect(createTeam.mock.calls[0]![5]).toBe(400_000_000);

    wrapper.unmount();
  });

  /** And a lower one, unchanged too - the point is that the box does no arithmetic at all. */
  it('submits a figure below the instance limit exactly as typed', async () => {
    const wrapper = await mountCreate();

    await budgetInput(wrapper).setValue(5_000_000);
    await flushPromises();

    buttonLabelled('Create team').click();
    await flushPromises();

    expect(createTeam.mock.calls[0]![5]).toBe(5_000_000);

    wrapper.unmount();
  });

  /**
   * A FRACTION IS REFUSED BY THE FIELD, BEFORE ANY REQUEST IS SENT. `BudgetTokens` on the route is
   * a `long?`, so 1.5 leaves the browser and comes back as a deserialiser's complaint about the
   * shape of the request - a message written for a developer, in a dialog a person is using. The
   * field says it itself, in the field's own words, and the button does not send.
   */
  it('refuses a fraction on the field and sends nothing', async () => {
    const wrapper = await mountCreate();

    await budgetInput(wrapper).setValue(1.5);
    await validated();

    expect(document.body.textContent).toContain(BudgetFieldFractionRefusal);
    expect(buttonLabelled('Create team').hasAttribute('disabled')).toBe(true);

    buttonLabelled('Create team').click();
    await flushPromises();

    expect(createTeam).not.toHaveBeenCalled();

    wrapper.unmount();
  });

  /** THE REGRESSION GUARD ON NO-CLAMP. Refusing a fraction must not introduce a clamp: a
   *  figure above the instance's own is legal and leaves unchanged. */
  it('still accepts a figure above the instance limit', async () => {
    const wrapper = await mountCreate();

    await budgetInput(wrapper).setValue(400_000_000);
    await validated();

    expect(document.body.textContent).not.toContain(BudgetFieldFractionRefusal);
    expect(buttonLabelled('Create team').hasAttribute('disabled')).toBe(false);

    wrapper.unmount();
  });

  /** And an empty box and a typed 0 - the two answers that are not figures at all - stay legal. */
  it('still accepts an empty box and an explicit zero', async () => {
    const wrapper = await mountCreate();

    await budgetInput(wrapper).setValue('');
    await validated();
    expect(buttonLabelled('Create team').hasAttribute('disabled')).toBe(false);

    await budgetInput(wrapper).setValue(0);
    await validated();
    expect(buttonLabelled('Create team').hasAttribute('disabled')).toBe(false);

    buttonLabelled('Create team').click();
    await flushPromises();

    expect(createTeam.mock.calls[0]![5]).toBe(0);

    wrapper.unmount();
  });

  /**
   * IN THE SAME REQUEST AS THE CREATE. A follow-up PUT leaves a window where the team exists on a
   * figure nobody chose, and loses the chosen one silently if it never lands.
   */
  it('never writes the budget in a second call', async () => {
    const wrapper = await mountCreate();

    await budgetInput(wrapper).setValue(400_000_000);
    await flushPromises();

    buttonLabelled('Create team').click();
    await flushPromises();

    expect(setTeamWorkflowBudget).not.toHaveBeenCalled();

    wrapper.unmount();
  });
});

describe('TeamSettingsDialog: the per-workflow budget', () => {
  async function mountSettings(over: Partial<Team> = {}) {
    setActivePinia(createPinia());

    const board = useConsoleStore();
    board.$patch({
      teams: [team(over)],
      activeTeamId: 'alpha' as TeamId,
      overviewLanded: true,
      workflowSpendLimit: InstanceLimit,
    });
    const refresh = vi.spyOn(board, 'refresh').mockResolvedValue();

    const session = useSessionStore();
    session.$patch({ user: { id: 'u1', email: 'admin@example.com' } });

    return { wrapper: await mountDialog(TeamSettingsDialog, {}, { pinia: false }), refresh };
  }

  /** A team that has chosen nothing shows the figure it is actually running under. */
  it('prefills the instance figure for a team that has chosen nothing', async () => {
    const { wrapper } = await mountSettings({ budgetTokens: null });

    expect(budgetInput(wrapper).props('modelValue')).toBe(InstanceLimit);

    wrapper.unmount();
  });

  /** A team that has chosen shows ITS OWN stored figure, not the instance's. */
  it('prefills the team’s own stored figure', async () => {
    const { wrapper } = await mountSettings({ budgetTokens: 250_000_000 });

    expect(budgetInput(wrapper).props('modelValue')).toBe(250_000_000);

    wrapper.unmount();
  });

  /**
   * AN EXPLICIT ZERO RENDERS AS ZERO. 0 is the team having chosen UNLIMITED, which is a different
   * fact from having chosen nothing - and the dialog is the only reader of the raw stored value,
   * so if it renders 0 as the instance figure the distinction is invisible and a Save silently
   * converts unlimited into a bound.
   */
  it('renders a stored zero as zero rather than as the instance figure', async () => {
    const { wrapper } = await mountSettings({ budgetTokens: 0 });

    expect(budgetInput(wrapper).props('modelValue')).toBe(0);

    wrapper.unmount();
  });

  /** Nothing changed means nothing written - the rule every other field on this dialog follows. */
  it('writes nothing when the box is untouched', async () => {
    const { wrapper } = await mountSettings({ budgetTokens: 250_000_000 });

    expect(buttonLabelled('Save changes').hasAttribute('disabled')).toBe(true);

    wrapper.unmount();
  });

  it('saves exactly what was typed, above the instance figure and unclamped', async () => {
    const { wrapper } = await mountSettings({ budgetTokens: 250_000_000 });

    await budgetInput(wrapper).setValue(900_000_000);
    await flushPromises();

    expect(buttonLabelled('Save changes').hasAttribute('disabled')).toBe(false);

    buttonLabelled('Save changes').click();
    await flushPromises();

    expect(setTeamWorkflowBudget).toHaveBeenCalledOnce();
    expect(setTeamWorkflowBudget).toHaveBeenCalledWith('alpha', 900_000_000);

    wrapper.unmount();
  });

  /** Clearing the box is "this team chooses nothing", which is NULL on the wire and the instance
   *  figure in force - never 0, which is a different choice with a different meaning. */
  it('sends null when the box is cleared', async () => {
    const { wrapper } = await mountSettings({ budgetTokens: 250_000_000 });

    await budgetInput(wrapper).setValue('');
    await flushPromises();

    buttonLabelled('Save changes').click();
    await flushPromises();

    expect(setTeamWorkflowBudget).toHaveBeenCalledWith('alpha', null);

    wrapper.unmount();
  });

  /** The same arm on the other dialog, and it has to be the same arm: two dialogs each deciding
   *  what the field refuses is how they come to refuse different things. */
  it('refuses a fraction on the field and writes nothing', async () => {
    const { wrapper } = await mountSettings({ budgetTokens: 250_000_000 });

    await budgetInput(wrapper).setValue(1.5);
    await validated();

    expect(document.body.textContent).toContain(BudgetFieldFractionRefusal);
    expect(buttonLabelled('Save changes').hasAttribute('disabled')).toBe(true);

    buttonLabelled('Save changes').click();
    await flushPromises();

    expect(setTeamWorkflowBudget).not.toHaveBeenCalled();

    wrapper.unmount();
  });

  /** And a typed 0 reaches the server as 0: the team choosing unlimited for itself. */
  it('sends an explicit zero when somebody types one', async () => {
    const { wrapper } = await mountSettings({ budgetTokens: 250_000_000 });

    await budgetInput(wrapper).setValue(0);
    await flushPromises();

    buttonLabelled('Save changes').click();
    await flushPromises();

    expect(setTeamWorkflowBudget).toHaveBeenCalledWith('alpha', 0);

    wrapper.unmount();
  });
});
