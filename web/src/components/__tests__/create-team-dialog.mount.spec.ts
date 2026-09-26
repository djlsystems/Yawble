// @vitest-environment happy-dom
//
// NEW TEAM, MOUNTED CLOSED AND THEN OPENED: the name and every repository URL carry rules from
// `lib/rules`, and Create follows them. A URL that cannot be cloned is refused under its field and
// the server is never asked - `fetch` is stubbed to fail loudly, and `createTeam` must not be called.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, type VueWrapper } from '@vue/test-utils';

const { createTeam, fileSystemRoots, listCatalog } = vi.hoisted(() => ({
  createTeam: vi.fn(),
  fileSystemRoots: vi.fn(),
  listCatalog: vi.fn(),
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
}));

import CreateTeamDialog from '../CreateTeamDialog.vue';
import { useConsoleStore } from '../../stores/console';
import { useSessionStore } from '../../stores/session';
import { remember } from '../../lib/newTeamDefaults';
import { bodyText, mountDialog, resetBody } from '../../test/mountQuasar';

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
});

afterEach(() => {
  vi.unstubAllGlobals();
  resetBody();
});

/** `remembered` seeds the choices a returning person already made, so only the name is open. */
async function open(remembered = true) {
  if (remembered) {
    remember({
      managerAgent: 'claude-headless',
      memberAgents: ['claude-headless'],
      root: null,
      repos: [],
    });
  }

  setActivePinia(createPinia());
  useConsoleStore().$patch({ teams: [], overviewLanded: true });
  useSessionStore().$patch({ user: { id: 'u1', email: 'admin@example.com' } });

  return mountDialog(CreateTeamDialog, {}, { pinia: false });
}

function field(wrapper: VueWrapper, label: string) {
  const found = wrapper.findAllComponents({ name: 'QInput' })
    .find((input) => input.props('label') === label || input.attributes('aria-label') === label
      || input.find('input').attributes('aria-label') === label);

  if (!found) throw new Error(`no ${label} input in the rendered dialog`);

  return found;
}

function button(label: string): HTMLButtonElement {
  const found = [...document.body.querySelectorAll('button')]
    .find((candidate) => candidate.textContent?.trim() === label);

  if (!found) throw new Error(`no ${label} button in the rendered dialog`);

  return found as HTMLButtonElement;
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

  it('offers Additional instructions, saying they are appended and never replace the role prompt', async () => {
    const wrapper = await open();

    const box = field(wrapper, 'Additional instructions (optional)');
    expect(box.props('hint')).toContain('Appended after the built-in role prompt');
    expect(box.props('hint')).toContain('never replaces');

    wrapper.unmount();
  });

  it('sends what was typed as additionalInstructions and no prompt', async () => {
    const wrapper = await open();
    await field(wrapper, 'Team name').setValue('Beta');
    await field(wrapper, 'Additional instructions (optional)').setValue('Prefer small commits.');
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
