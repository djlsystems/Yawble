// @vitest-environment happy-dom
//
// NEW TEAM, B001F: "Create a local repository for this team" (shown only with no repository listed,
// ticked by default, unticked sends `localRepository: false`), and a refused repository check - the
// Host's sentence, ONLY the choices it listed for each URL, and each choice resent as its own code
// in `repoChoices` until the team is created.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, type VueWrapper } from '@vue/test-utils';

const { createTeam, fileSystemRoots, listCatalog, listLocalRepos } = vi.hoisted(() => ({
  createTeam: vi.fn(),
  fileSystemRoots: vi.fn(),
  listCatalog: vi.fn(),
  listLocalRepos: vi.fn(),
}));

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
}));

import CreateTeamDialog from '../CreateTeamDialog.vue';
import { ActionRefused } from '../../api/client';
import type { RepoCheckFailure } from '../../api/types';
import { useConsoleStore } from '../../stores/console';
import { useSessionStore } from '../../stores/session';
import { remember } from '../../lib/newTeamDefaults';
import { bodyText, mountDialog, resetBody } from '../../test/mountQuasar';

/** `createTeam`'s positional arguments these tests read. */
const REPOS = 4;
const LOCAL_REPOSITORY = 8;
const REPO_CHOICES = 9;

const MISSING = 'https://github.com/acme/job-tracker';
const OTHER = 'https://git.example.com/acme/other.git';

beforeEach(() => {
  localStorage.clear();
  vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new Error('unexpected fetch')));

  createTeam.mockReset();
  createTeam.mockResolvedValue({ id: 'beta', name: 'Beta' });
  fileSystemRoots.mockReset();
  fileSystemRoots.mockResolvedValue({ roots: [] });
  listCatalog.mockReset();
  listCatalog.mockResolvedValue({ agents: [{ name: 'claude-headless', mode: 'Headless' }] });
  listLocalRepos.mockReset();
  listLocalRepos.mockResolvedValue([]);
});

afterEach(() => {
  vi.unstubAllGlobals();
  resetBody();
});

async function open() {
  remember({ managerAgent: 'claude-headless', memberAgents: ['claude-headless'], root: null, repos: [] });
  setActivePinia(createPinia());
  useConsoleStore().$patch({ teams: [], overviewLanded: true });
  useSessionStore().$patch({ user: { id: 'u1', email: 'admin@example.com' } });

  const wrapper = await mountDialog(CreateTeamDialog, {}, { pinia: false });
  await field(wrapper, 'Team name').setValue('Beta');
  await settled();
  await showTab('Code');
  return wrapper;
}

function field(wrapper: VueWrapper, label: string) {
  const found = wrapper.findAllComponents({ name: 'QInput' }).find((input) => input.props('label') === label);
  if (!found) throw new Error(`no ${label} input in the rendered dialog`);
  return found;
}

function button(label: string): HTMLButtonElement {
  const found = [...document.body.querySelectorAll('button')].find((candidate) => candidate.textContent?.trim() === label);
  if (!found) throw new Error(`no ${label} button in the rendered dialog`);
  return found as HTMLButtonElement;
}

function checkbox(): HTMLElement | null {
  return document.body.querySelector<HTMLElement>('[data-local-repository-checkbox]');
}

/** The choice buttons the refusal shows, by code, in order. */
function offered(url?: string): string[] {
  const scope = url ? `[data-repo-check-url="${url}"] ` : '';
  return [...document.body.querySelectorAll<HTMLElement>(`${scope}[data-repo-choice]`)].map((b) => b.dataset.repoChoice!);
}

function choice(code: string, url?: string): HTMLElement {
  const scope = url ? `[data-repo-check-url="${url}"] ` : '';
  const found = document.body.querySelector<HTMLElement>(`${scope}[data-repo-choice="${code}"]`);
  if (!found) throw new Error(`no ${code} choice in the rendered refusal`);
  return found;
}

async function addRepo(wrapper: VueWrapper, url: string) {
  await field(wrapper, 'GitHub Repos').setValue(url);
  button('Add').click();
  await settled();
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

async function settled() {
  await flushPromises();
  await new Promise((resolve) => setTimeout(resolve, 10));
  await flushPromises();
}

/** The 422 exactly as `send` throws it for the contract's refusal body. */
function refused(error: string, repos: RepoCheckFailure[]) {
  return Object.assign(new ActionRefused(error, { error, code: 'repo-check-failed', repos }), { status: 422 });
}

const notFoundSentence = `${MISSING} could not be read: remote: Repository not found. Nothing was created. `
  + 'Choose: create it on GitHub (private), or use a local repository instead.';

describe('New Team: Create a local repository for this team', () => {
  it('is shown ticked with no repository listed, and a create sends localRepository: true', async () => {
    await open();

    expect(checkbox()).not.toBeNull();
    expect(bodyText()).toContain('Create a local repository for this team');
    expect(checkbox()!.getAttribute('aria-checked')).toBe('true');

    button('Create team').click();
    await settled();

    expect(createTeam).toHaveBeenCalledTimes(1);
    expect(createTeam.mock.calls[0]![REPOS]).toEqual([]);
    expect(createTeam.mock.calls[0]![LOCAL_REPOSITORY]).toBe(true);
  });

  it('unticked, sends localRepository: false', async () => {
    await open();

    checkbox()!.click();
    await settled();
    expect(checkbox()!.getAttribute('aria-checked')).toBe('false');

    button('Create team').click();
    await settled();

    expect(createTeam.mock.calls[0]![LOCAL_REPOSITORY]).toBe(false);
  });

  it('is hidden once a repository URL is listed, which sends no localRepository at all', async () => {
    const wrapper = await open();

    await addRepo(wrapper, 'https://github.com/owner/app.git');
    expect(checkbox()).toBeNull();

    button('Create team').click();
    await settled();

    expect(createTeam.mock.calls[0]![REPOS]).toEqual(['https://github.com/owner/app.git']);
    expect(createTeam.mock.calls[0]![LOCAL_REPOSITORY]).toBeUndefined();
  });
});

describe('New Team: a URL typed but not added', () => {
  const TYPED = 'https://github.com/owner/typed.git';

  it('hides the local-repository box while the field holds text, and shows it again when cleared', async () => {
    const wrapper = await open();

    await field(wrapper, 'GitHub Repos').setValue(TYPED);
    await settled();
    expect(checkbox()).toBeNull();

    await field(wrapper, 'GitHub Repos').setValue('   ');
    await settled();
    expect(checkbox()).not.toBeNull();
  });

  it('Create sends it in repos, as if Add had been pressed, with no local repository requested', async () => {
    const wrapper = await open();

    await field(wrapper, 'GitHub Repos').setValue(`  ${TYPED}  `);
    await settled();
    button('Create team').click();
    await settled();

    expect(createTeam).toHaveBeenCalledTimes(1);
    expect(createTeam.mock.calls[0]![REPOS]).toEqual([TYPED]);
    expect(createTeam.mock.calls[0]![LOCAL_REPOSITORY]).toBeUndefined();
  });

  it('is added after the listed ones, and a refusal of it offers its choices', async () => {
    createTeam.mockRejectedValueOnce(refused(notFoundSentence, [
      { url: MISSING, failure: 'not-found', reason: 'remote: Repository not found.', choices: ['create-on-github', 'use-local'] },
    ]));

    const wrapper = await open();
    await addRepo(wrapper, OTHER);
    await field(wrapper, 'GitHub Repos').setValue(MISSING);
    await settled();
    button('Create team').click();
    await settled();

    expect(createTeam.mock.calls[0]![REPOS]).toEqual([OTHER, MISSING]);
    expect(offered(MISSING)).toEqual(['create-on-github', 'use-local']);

    choice('use-local', MISSING).click();
    await settled();

    expect(createTeam).toHaveBeenCalledTimes(2);
    expect(createTeam.mock.calls[1]![REPOS]).toEqual([OTHER, MISSING]);
    expect(createTeam.mock.calls[1]![REPO_CHOICES]).toEqual({ [MISSING]: 'use-local' });
    expect(wrapper.emitted('created')).toEqual([['beta']]);
  });
});

describe('New Team: a refused repository check', () => {
  it('shows the sentence and only the offered choices; Create it on GitHub resends create-on-github and creates', async () => {
    createTeam.mockRejectedValueOnce(refused(notFoundSentence, [
      { url: MISSING, failure: 'not-found', reason: 'remote: Repository not found.', choices: ['create-on-github', 'use-local'] },
    ]));

    const wrapper = await open();
    await addRepo(wrapper, MISSING);
    button('Create team').click();
    await settled();

    expect(bodyText()).toContain(notFoundSentence);
    expect(offered()).toEqual(['create-on-github', 'use-local']);
    expect(bodyText()).toContain('Create it on GitHub (private)');
    expect(bodyText()).toContain('Use a local repository instead');
    expect(bodyText()).not.toContain('Attach anyway');
    expect(wrapper.emitted('created')).toBeUndefined();

    choice('create-on-github').click();
    await settled();

    expect(createTeam).toHaveBeenCalledTimes(2);
    expect(createTeam.mock.calls[1]![REPOS]).toEqual([MISSING]);
    expect(createTeam.mock.calls[1]![REPO_CHOICES]).toEqual({ [MISSING]: 'create-on-github' });
    expect(wrapper.emitted('created')).toEqual([['beta']]);
  });

  it('Use a local repository instead resends use-local', async () => {
    createTeam.mockRejectedValueOnce(refused(notFoundSentence, [
      { url: MISSING, failure: 'not-found', reason: 'remote: Repository not found.', choices: ['create-on-github', 'use-local'] },
    ]));

    const wrapper = await open();
    await addRepo(wrapper, MISSING);
    button('Create team').click();
    await settled();

    choice('use-local').click();
    await settled();

    expect(createTeam.mock.calls[1]![REPO_CHOICES]).toEqual({ [MISSING]: 'use-local' });
    expect(wrapper.emitted('created')).toEqual([['beta']]);
  });

  it('a network failure offers Attach anyway, which resends attach-anyway', async () => {
    const sentence = `${OTHER} could not be read: Could not resolve host: git.example.com. Nothing was created.`;
    createTeam.mockRejectedValueOnce(refused(sentence, [
      { url: OTHER, failure: 'unreachable', reason: 'Could not resolve host: git.example.com', choices: ['use-local', 'attach-anyway'] },
    ]));

    const wrapper = await open();
    await addRepo(wrapper, OTHER);
    button('Create team').click();
    await settled();

    expect(bodyText()).toContain(sentence);
    expect(offered()).toEqual(['use-local', 'attach-anyway']);
    expect(bodyText()).not.toContain('Create it on GitHub (private)');

    choice('attach-anyway').click();
    await settled();

    expect(createTeam.mock.calls[1]![REPO_CHOICES]).toEqual({ [OTHER]: 'attach-anyway' });
    expect(wrapper.emitted('created')).toEqual([['beta']]);
  });

  it('brings the Code tab forward when a refused repository is answered while General shows', async () => {
    createTeam.mockRejectedValueOnce(refused('GitHub refused the create.', [
      { url: MISSING, failure: 'not-found', reason: 'GitHub refused.', choices: ['use-local'] },
    ]));

    const wrapper = await open();
    await addRepo(wrapper, MISSING);
    await showTab('General');
    button('Create team').click();
    await settled();

    expect(activeTab()).toBe('Code');
    expect(offered()).toEqual(['use-local']);
  });

  it('offers only a local repository when that is all the Host lists', async () => {
    createTeam.mockRejectedValueOnce(refused('GitHub refused the create.', [
      { url: MISSING, failure: 'not-found', reason: 'GitHub refused.', choices: ['use-local'] },
    ]));

    const wrapper = await open();
    await addRepo(wrapper, MISSING);
    button('Create team').click();
    await settled();

    expect(offered()).toEqual(['use-local']);
  });

  it('with two refused URLs, resends once both have a choice, carrying both', async () => {
    createTeam.mockRejectedValueOnce(refused('Two could not be read.', [
      { url: MISSING, failure: 'not-found', reason: 'not found', choices: ['create-on-github', 'use-local'] },
      { url: OTHER, failure: 'unreachable', reason: 'timed out', choices: ['use-local', 'attach-anyway'] },
    ]));

    const wrapper = await open();
    await addRepo(wrapper, MISSING);
    await addRepo(wrapper, OTHER);
    button('Create team').click();
    await settled();

    expect(offered(MISSING)).toEqual(['create-on-github', 'use-local']);
    expect(offered(OTHER)).toEqual(['use-local', 'attach-anyway']);

    choice('create-on-github', MISSING).click();
    await settled();
    expect(createTeam).toHaveBeenCalledTimes(1);

    choice('attach-anyway', OTHER).click();
    await settled();

    expect(createTeam).toHaveBeenCalledTimes(2);
    expect(createTeam.mock.calls[1]![REPO_CHOICES]).toEqual({ [MISSING]: 'create-on-github', [OTHER]: 'attach-anyway' });
    expect(wrapper.emitted('created')).toEqual([['beta']]);
  });

  it('a refusal that is not a repository check stays the plain sentence, with no choices', async () => {
    createTeam.mockRejectedValueOnce(Object.assign(new ActionRefused('Something else.', { error: 'Something else.' }), { status: 422 }));

    await open();
    button('Create team').click();
    await settled();

    expect(bodyText()).toContain('Something else.');
    expect(offered()).toEqual([]);
  });
});
