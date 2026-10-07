// @vitest-environment happy-dom
//
// NEW TEAM, MOUNTED CLOSED AND THEN OPENED: the name and every repository URL carry rules from
// `lib/rules`, and Create follows them. A URL that cannot be cloned is refused under its field and
// the server is never asked - `fetch` is stubbed to fail loudly, and `createTeam` must not be called.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, type VueWrapper } from '@vue/test-utils';

const { createTeam, fileSystemRoots, listCatalog, listLocalRepos, createLocalRepo } = vi.hoisted(() => ({
  createTeam: vi.fn(),
  fileSystemRoots: vi.fn(),
  listCatalog: vi.fn(),
  listLocalRepos: vi.fn(),
  createLocalRepo: vi.fn(),
}));

const fetchUnexpected = vi.fn();

vi.mock('quasar', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  useQuasar: () => ({ notify: vi.fn() }),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  createTeam,
  fileSystemRoots,
  listCatalog,
  listLocalRepos,
  createLocalRepo,
}));

import CreateTeamDialog from '../CreateTeamDialog.vue';
import { useConsoleStore } from '../../stores/console';
import { useSessionStore } from '../../stores/session';
import { remember } from '../../lib/newTeamDefaults';
import { bodyText, mountDialog, resetBody } from '../../test/mountQuasar';
import { TeamInstructionsLabel } from '../../lib/additionalInstructions';
import { refreshAgentInstallations } from '../../lib/useAgentInstallations';

beforeEach(() => {
  localStorage.clear();

  fetchUnexpected.mockReset();
  fetchUnexpected.mockRejectedValue(new Error('unexpected fetch'));
  vi.stubGlobal('fetch', fetchUnexpected);

  createTeam.mockReset();
  createTeam.mockResolvedValue({ id: 'beta', name: 'Beta' });
  fileSystemRoots.mockReset();
  fileSystemRoots.mockResolvedValue({ roots: [] });
  listCatalog.mockReset();
  listCatalog.mockResolvedValue({
    agents: [{ name: 'claude-headless', mode: 'Headless' }],
  });
  listLocalRepos.mockReset();
  listLocalRepos.mockResolvedValue([]);
  createLocalRepo.mockReset();
});

afterEach(() => {
  vi.unstubAllGlobals();
  resetBody();
});

/** `remembered` seeds the choices a returning person already made, so only the name is open.
 *  `workflowSpendLimit` is the instance's own budget figure, which the budget box opens holding. */
async function open(remembered = true, workflowSpendLimit: number | null = null) {
  if (remembered) {
    remember({
      managerAgent: 'claude-headless',
      memberAgents: ['claude-headless'],
      root: null,
      repos: [],
    });
  }

  setActivePinia(createPinia());
  useConsoleStore().$patch({ teams: [], overviewLanded: true, workflowSpendLimit });
  useSessionStore().$patch({ user: { id: 'u1', email: 'admin@example.com' } });

  return mountDialog(CreateTeamDialog, {}, { pinia: false });
}

function field(wrapper: VueWrapper, label: string) {
  const found = wrapper.findAllComponents({ name: 'QInput' })
    .find((input) => input.props('label') === label || input.attributes('aria-label') === label
      || input.find('input, textarea').attributes('aria-label') === label);

  if (!found) throw new Error(`no ${label} input in the rendered dialog`);

  return found;
}

function button(label: string): HTMLButtonElement {
  const found = [...document.body.querySelectorAll('button')]
    .find((candidate) => candidate.textContent?.trim() === label);

  if (!found) throw new Error(`no ${label} button in the rendered dialog`);

  return found as HTMLButtonElement;
}

function localRepo(name: string) {
  return { name, reference: `local:${name}`, sizeBytes: 1, defaultBranch: 'main', lastCommit: null, teams: [] };
}

function chip(label: string): HTMLElement {
  const found = document.body.querySelector<HTMLElement>(`[aria-label="${label}"]`);
  if (!found) throw new Error(`no ${label} chip in the rendered dialog`);
  return found;
}

/** Brings a tab of the dialog forward: the repositories are on Code, everything else on General. */
async function showTab(label: 'General' | 'Code') {
  const tab = [...document.body.querySelectorAll<HTMLElement>('.q-tab')].find((candidate) => candidate.textContent?.trim() === label);
  if (!tab) throw new Error(`no ${label} tab in the rendered dialog`);
  tab.click();
  await flushPromises();
  await new Promise((resolve) => setTimeout(resolve, 10));
  await flushPromises();
}

function activeTab(): string | undefined {
  return document.body.querySelector<HTMLElement>('.q-tab--active')?.textContent?.trim();
}

async function validated() {
  await flushPromises();
  await new Promise((resolve) => setTimeout(resolve, 10));
  await flushPromises();
}

describe('CreateTeamDialog validation', () => {
  it('disables Create with nothing chosen, and with an empty name', async () => {
    const fresh = await open(false);

    expect(button('Create team').hasAttribute('disabled')).toBe(true);

    fresh.unmount();
    resetBody();

    const wrapper = await open();
    await field(wrapper, 'Team name').setValue('');
    await validated();

    expect(bodyText()).toContain('A team needs a name.');
    expect(button('Create team').hasAttribute('disabled')).toBe(true);

    await field(wrapper, 'Team name').setValue('Beta');
    await validated();

    expect(button('Create team').hasAttribute('disabled')).toBe(false);
  });

  it('refuses a URL that cannot be cloned, disables Create and asks nobody', async () => {
    const wrapper = await open();
    await field(wrapper, 'Team name').setValue('Beta');

    await showTab('Code');
    await field(wrapper, 'GitHub Repos').setValue('git@github.com:owner/repo.git');
    await validated();

    expect(bodyText()).toContain('Use an absolute http or https URL');
    expect(button('Create team').hasAttribute('disabled')).toBe(true);
    expect(button('Add').hasAttribute('disabled')).toBe(true);

    button('Create team').click();
    await validated();

    expect(createTeam).not.toHaveBeenCalled();
    expect(fetchUnexpected).not.toHaveBeenCalled();
  });

  it('marks a listed URL edited into a bad one, and a duplicate folder', async () => {
    const wrapper = await open();
    await field(wrapper, 'Team name').setValue('Beta');

    await showTab('Code');
    await field(wrapper, 'GitHub Repos').setValue('https://github.com/owner/app.git');
    button('Add').click();
    await validated();

    expect(button('Create team').hasAttribute('disabled')).toBe(false);

    await field(wrapper, 'GitHub Repos').setValue('https://gitlab.com/other/App');
    await validated();

    expect(bodyText()).toContain("Another URL in this list already clones into 'App'.");
    expect(button('Create team').hasAttribute('disabled')).toBe(true);

    await field(wrapper, 'GitHub Repos').setValue('');
    await field(wrapper, 'Primary repo URL').setValue('https://github.com/owner/');
    await validated();

    expect(bodyText()).toContain('The URL must end in the repository name.');
    expect(button('Create team').hasAttribute('disabled')).toBe(true);
    expect(createTeam).not.toHaveBeenCalled();
  });

  it('sends nothing for a listed duplicate, whether Create is clicked or the form is submitted', async () => {
    const wrapper = await open();
    await field(wrapper, 'Team name').setValue('Beta');

    for (const url of ['https://github.com/owner/app.git', 'https://github.com/owner/web.git']) {
      await showTab('Code');
      await field(wrapper, 'GitHub Repos').setValue(url);
      button('Add').click();
      await validated();
    }

    await field(wrapper, 'Repo 2 URL').setValue('https://gitlab.com/other/APP');
    await validated();

    expect(bodyText()).toContain("Another URL in this list already clones into 'APP'.");
    expect(button('Create team').hasAttribute('disabled')).toBe(true);

    button('Create team').click();
    // What Enter in a field does: the form's own submit, which a disabled button does not stop.
    document.body.querySelector('form')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    await validated();

    expect(createTeam).not.toHaveBeenCalled();
    expect(fetchUnexpected).not.toHaveBeenCalled();
  });

  it('creates with valid input and sends the listed repos', async () => {
    const wrapper = await open();
    await field(wrapper, 'Team name').setValue('Beta');
    await showTab('Code');
    await field(wrapper, 'GitHub Repos').setValue('https://github.com/owner/app.git');
    button('Add').click();
    await validated();

    button('Create team').click();
    await validated();

    expect(createTeam).toHaveBeenCalledTimes(1);
    expect(createTeam.mock.calls[0]![4]).toEqual(['https://github.com/owner/app.git']);
  });

  // The upstream rides the create itself, keyed by the repository it belongs to, so the clone
  // is made with its upstream remote; the fork itself is refused on the field.
  it('sends a repository\'s upstream inside the create, and refuses the fork as its own upstream', async () => {
    const wrapper = await open();
    await field(wrapper, 'Team name').setValue('Beta');
    await showTab('Code');
    await field(wrapper, 'GitHub Repos').setValue('https://github.com/fork-owner/app.git');
    button('Add').click();
    await validated();

    await field(wrapper, 'Upstream URL for https://github.com/fork-owner/app.git').setValue('https://github.com/fork-owner/app');
    await validated();
    expect(bodyText()).toContain('own URL');
    expect(button('Create team').hasAttribute('disabled')).toBe(true);

    await field(wrapper, 'Upstream URL for https://github.com/fork-owner/app.git').setValue('https://github.com/project/app.git');
    await validated();
    button('Create team').click();
    await validated();

    expect(createTeam).toHaveBeenCalledTimes(1);
    expect(createTeam.mock.calls[0]![7]).toEqual({
      'https://github.com/fork-owner/app.git': 'https://github.com/project/app.git',
    });
  });

  // A local repository joins the list as `local:<name>` - picked from the instance's beside the URL
  // field - is sent as it is, and offers no upstream: contributor mode does not apply.
  it('attaches an existing local repository and sends it as local:<name>', async () => {
    listLocalRepos.mockResolvedValue([localRepo('widget')]);

    const wrapper = await open();
    await field(wrapper, 'Team name').setValue('Beta');
    await validated();

    await showTab('Code');
    chip('Attach local:widget').click();
    await validated();
    expect(chip('local:widget is attached').classList.contains('disabled')).toBe(true);
    expect(() => field(wrapper, 'Upstream URL for local:widget')).toThrow();

    button('Create team').click();
    await validated();

    expect(createTeam).toHaveBeenCalledTimes(1);
    expect(createTeam.mock.calls[0]![4]).toEqual(['local:widget']);
  });

  // ONE WAY TO MAKE A LOCAL REPOSITORY: the box, which says what it does. A named Create beside it
  // was a second way to do the same thing, and made one even if the team was then cancelled.
  it('offers one way to make a local repository: the ticked box, which says what it makes', async () => {
    const wrapper = await open();
    await showTab('Code');

    expect(() => field(wrapper, 'Create a local repository')).toThrow();
    expect([...document.body.querySelectorAll('button')].some((candidate) => candidate.textContent?.trim() === 'Create')).toBe(false);
    expect(document.body.querySelectorAll('[data-local-repository-checkbox]')).toHaveLength(1);
    expect(document.body.querySelector('[data-local-repository-hint]')?.textContent?.replace(/\s+/g, ' ').trim()).toBe(
      'With no repository listed, the team gets a git repository of its own, named after it and kept on this instance only. Untick it for a team with no code.',
    );
    expect(createLocalRepo).not.toHaveBeenCalled();
  });

  it('puts the repositories on a Code tab after General, with the local repository ticked by default', async () => {
    const wrapper = await open();

    expect([...document.body.querySelectorAll('.q-tab')].map((tab) => tab.textContent?.trim())).toEqual(['General', 'Code']);
    expect(activeTab()).toBe('General');
    expect(() => field(wrapper, 'GitHub Repos')).toThrow();

    await showTab('Code');

    expect(field(wrapper, 'GitHub Repos')).toBeTruthy();
    expect(document.body.querySelector('[data-local-repository-checkbox] [aria-checked="true"], [data-local-repository-checkbox][aria-checked="true"]')).not.toBeNull();
  });

  it('brings General forward when a taken name is answered while Code shows', async () => {
    createTeam.mockRejectedValue(new Error("A team called 'Beta' already exists."));

    const wrapper = await open();
    await field(wrapper, 'Team name').setValue('Beta');
    await showTab('Code');
    button('Create team').click();
    await validated();

    expect(activeTab()).toBe('General');
    expect(field(wrapper, 'Team name').props('error')).toBe(true);
  });

  it('shows a taken name in the dialog and marks the name field', async () => {
    createTeam.mockRejectedValue(new Error("A team called 'Beta' already exists."));

    const wrapper = await open();
    await field(wrapper, 'Team name').setValue('Beta');
    await validated();

    button('Create team').click();
    await validated();

    expect(bodyText()).toContain("A team called 'Beta' already exists.");
    expect(field(wrapper, 'Team name').props('error')).toBe(true);
  });
});

/** What a team is told is the built-in role prompt; a person may only append to it. */
describe('CreateTeamDialog prompts', () => {
  it('offers no Manager or Member prompt picker', async () => {
    const wrapper = await open();

    const labels = wrapper.findAllComponents({ name: 'QSelect' }).map((select) => String(select.props('label')));
    expect(labels.some((label) => /prompt|told/i.test(label))).toBe(false);

    wrapper.unmount();
  });

  it('gives Team instructions a section of their own, after the agent allowlist, with the shared hint', async () => {
    const wrapper = await open();

    const section = document.body.querySelector('[data-section="team-instructions"]');
    expect(section, 'no Team instructions section in the rendered dialog').not.toBeNull();
    expect(section!.textContent).toContain('Team instructions (optional)');
    expect(section!.textContent).not.toContain("Agents this team's members may use");

    const text = bodyText();
    expect(text.indexOf("Agents this team's members may use")).toBeGreaterThan(-1);
    expect(text.indexOf('Team instructions (optional)')).toBeGreaterThan(text.indexOf("Agents this team's members may use"));

    // DIRECTLY after: the agent allowlist is the last control before the section.
    const heading = [...document.body.querySelectorAll('.text-subtitle2')].map((node) => node.textContent?.trim());
    expect(heading.indexOf('Team instructions (optional)')).toBe(heading.indexOf("Agents this team's members may use") + 1);

    const box = field(wrapper, TeamInstructionsLabel);
    expect(section!.contains(box.element)).toBe(true);
    expect(box.props('hint')).toBe(
      "How this team works: its purpose, rules, conventions and playbook. Every member reads it: the Manager and every member, whether the Manager hired them or a person added them. It is added after each member's built-in prompt and its own instructions, and never replaces them.",
    );

    wrapper.unmount();
  });

  it('sends what was typed as additionalInstructions and no prompt', async () => {
    const wrapper = await open();
    await field(wrapper, 'Team name').setValue('Beta');
    await field(wrapper, TeamInstructionsLabel).setValue('Prefer small commits.');
    await validated();

    button('Create team').click();
    await validated();

    expect(createTeam).toHaveBeenCalledTimes(1);
    const args = createTeam.mock.calls[0]!;
    expect(args[6]).toBe('Prefer small commits.');
    expect(args.slice(0, 3)).toEqual(['Beta', 'claude-headless', ['claude-headless']]);

    wrapper.unmount();
  });
});

/** The text a person sees, with the folded part left out: `v-show` keeps it in the DOM. */
function visibleText(): string {
  const copy = document.body.cloneNode(true) as HTMLElement;
  copy.querySelectorAll<HTMLElement>('[style*="display: none"]').forEach((hidden) => hidden.remove());
  return (copy.textContent ?? '').replace(/\s+/g, ' ');
}

function advancedLink(): HTMLElement {
  const found = document.body.querySelector<HTMLElement>('[data-advanced-link]');
  if (!found) throw new Error('no Advanced... link in the rendered dialog');
  return found;
}

function advancedSettings(): HTMLElement {
  const found = document.body.querySelector<HTMLElement>('[data-advanced-settings]');
  if (!found) throw new Error('no advanced settings in the rendered dialog');
  return found;
}

function budgetWords(): string {
  return document.body.querySelector('[data-budget-words]')?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
}

/** A first-time person meets plain words; the folder, the machines, the agent terms and the budget
 *  wait behind Advanced.... */
describe('CreateTeamDialog for a first-time person', () => {
  afterEach(async () => {
    listCatalog.mockResolvedValue({ agents: [] });
    await refreshAgentInstallations();
  });

  it('says what a team starts with in plain words', async () => {
    await open();

    expect(visibleText()).toContain('Every team starts with a Manager: an agent that plans the work and hires members to do it.');
    expect(bodyText()).not.toContain('door into it');
  });

  it('labels the agent allowlist in plain words, with no preset or tag terms on show', async () => {
    const wrapper = await open();

    const select = wrapper.findAllComponents({ name: 'QSelect' }).map((candidate) => String(candidate.props('label')));
    expect(select).toContain('Add an agent');
    expect(select).not.toContain('Allowlist: add Agent');

    const shown = visibleText();
    expect(shown).toContain('Used when nobody chooses');
    expect(shown).not.toContain('Default when no tag is requested');
    expect(shown).not.toContain('Headless presets only');
    expect(shown).not.toMatch(/headless preset|\btag\b/i);
  });

  it('folds the folder, the machines, the agent terms and the budget behind Advanced..., hidden until it is clicked', async () => {
    listCatalog.mockResolvedValue({
      agents: [{ name: 'claude-headless', mode: 'Headless' }],
      installations: [{
        agent: 'claude-headless',
        command: 'claude',
        state: null,
        resolvedPath: '/usr/bin/claude',
        referenced: true,
        message: 'claude is installed on worker-1.',
        measuredOn: [{ worker: 'worker-1', installed: true, at: '2026-10-07T00:00:00Z' }],
      }],
    });
    await refreshAgentInstallations();

    fileSystemRoots.mockResolvedValue({ roots: [{ path: '/data', isInstance: true }] });

    const wrapper = await open(true, 100_000_000);
    await field(wrapper, 'Team name').setValue('QuickNotes');
    await validated();

    expect(advancedLink().textContent?.trim()).toBe('Advanced...');
    expect(advancedLink().getAttribute('aria-expanded')).toBe('false');
    expect(advancedSettings().style.display).toBe('none');

    const folded = visibleText();
    expect(folded).not.toContain('Place team in');
    expect(folded).not.toContain('New team folder will be /data/teams/QuickNotes');
    expect(folded).not.toContain('Installed on worker-1');
    expect(folded).not.toContain('Budget for one workflow');
    expect(folded).not.toContain('100000000');

    advancedLink().click();
    await validated();

    expect(advancedLink().getAttribute('aria-expanded')).toBe('true');
    expect(advancedSettings().style.display).toBe('');
    const shown = visibleText();
    expect(shown).toContain('Place team in');
    expect(shown).toContain('New team folder will be /data/teams/QuickNotes');
    expect(shown).toContain('Manager agent: Installed on worker-1');
    expect(shown).toContain('Only headless presets are offered');
    expect(advancedSettings().contains(field(wrapper, 'Budget for one workflow (tokens)').element)).toBe(true);
  });

  it('folds Advanced... again on every open', async () => {
    const wrapper = await open();
    advancedLink().click();
    await validated();
    expect(advancedSettings().style.display).toBe('');

    await wrapper.setProps({ modelValue: false });
    await wrapper.setProps({ modelValue: true });
    await validated();

    expect(advancedSettings().style.display).toBe('none');
  });

  it('says the budget in words: rounded to millions, and no limit for 0', async () => {
    const wrapper = await open(true, 100_000_000);
    advancedLink().click();
    await validated();

    expect(budgetWords()).toBe('Budget for one workflow: 100 million tokens');

    await field(wrapper, 'Budget for one workflow (tokens)').setValue(0);
    await validated();
    expect(budgetWords()).toBe('Budget for one workflow: no limit');

    await field(wrapper, 'Budget for one workflow (tokens)').setValue(2_345_678);
    await validated();
    expect(budgetWords()).toBe('Budget for one workflow: about 2.3 million tokens');

    await field(wrapper, 'Budget for one workflow (tokens)').setValue('');
    await validated();
    expect(budgetWords()).toBe("Budget for one workflow: 100 million tokens, the instance's own figure");
  });

  it('says no limit for an instance with no budget', async () => {
    const wrapper = await open(true, null);
    advancedLink().click();
    await validated();

    expect(budgetWords()).toBe("Budget for one workflow: no limit, the instance's own figure");
    wrapper.unmount();
  });

  it('keeps Advanced... open while the budget there is refused', async () => {
    const wrapper = await open(true, 100_000_000);
    advancedLink().click();
    await validated();
    await field(wrapper, 'Budget for one workflow (tokens)').setValue(1.5);
    await validated();

    advancedLink().click();
    await validated();

    expect(advancedSettings().style.display).toBe('');
    expect(button('Create team').hasAttribute('disabled')).toBe(true);
  });
});
