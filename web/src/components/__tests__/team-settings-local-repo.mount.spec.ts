// @vitest-environment happy-dom
//
// TEAM SETTINGS -> REPOSITORIES, B001F: "Create a local repository" beside Add for a team with no
// repository (POST /api/teams/{team}/local-repo), and an attach refused by the repository check -
// the Host's sentence, only its choices, and the list resent with `repoChoices`.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, type VueWrapper } from '@vue/test-utils';

const { listCatalog, getTeamEnv, setTeamRepos, listLocalRepos, createTeamLocalRepo } = vi.hoisted(() => ({
  listCatalog: vi.fn(),
  getTeamEnv: vi.fn(),
  setTeamRepos: vi.fn(),
  listLocalRepos: vi.fn(),
  createTeamLocalRepo: vi.fn(),
}));

vi.mock('quasar', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  useQuasar: () => ({ notify: vi.fn() }),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  listCatalog,
  getTeamEnv,
  setTeamRepos,
  listLocalRepos,
  createTeamLocalRepo,
}));

import TeamSettingsDialog from '../TeamSettingsDialog.vue';
import { ActionRefused } from '../../api/client';
import { useConsoleStore } from '../../stores/console';
import { useSessionStore } from '../../stores/session';
import type { TeamId } from '../../api/types';
import { bodyText, mountDialog, resetBody } from '../../test/mountQuasar';

const MISSING = 'https://github.com/acme/job-tracker';

function team(repos: string[] = []) {
  return {
    id: 'alpha' as TeamId,
    name: 'Alpha',
    memberAgents: ['claude-headless'],
    additionalInstructions: null,
    maxConcurrent: null,
    effectiveMaxConcurrent: 4,
    paused: false,
    repos,
    env: {},
  };
}

beforeEach(() => {
  vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new Error('unexpected fetch')));
  listCatalog.mockReset();
  listCatalog.mockResolvedValue({ agents: [{ name: 'claude-headless', mode: 'Headless' }] });
  getTeamEnv.mockReset();
  getTeamEnv.mockResolvedValue({});
  setTeamRepos.mockReset();
  listLocalRepos.mockReset();
  listLocalRepos.mockResolvedValue([]);
  createTeamLocalRepo.mockReset();
});

afterEach(() => {
  vi.unstubAllGlobals();
  resetBody();
});

async function open(repos: string[] = []) {
  setActivePinia(createPinia());
  const board = useConsoleStore();
  board.$patch({ teams: [team(repos)], activeTeamId: 'alpha' as TeamId, overviewLanded: true });
  vi.spyOn(board, 'refresh').mockResolvedValue();
  useSessionStore().$patch({ user: { id: 'u1', email: 'admin@example.com' } });

  const wrapper = await mountDialog(TeamSettingsDialog, { initialTab: 'repos' }, { pinia: false });
  await settled();
  return wrapper;
}

function field(wrapper: VueWrapper, label: string) {
  const found = wrapper.findAllComponents({ name: 'QInput' }).find((input) => input.props('label') === label);
  if (!found) throw new Error(`no ${label} input in the rendered dialog`);
  return found;
}

function button(label: string): HTMLButtonElement {
  const found = [...document.body.querySelectorAll('button')].filter((b) => b.textContent?.trim() === label).pop();
  if (!found) throw new Error(`no ${label} button in the rendered dialog`);
  return found as HTMLButtonElement;
}

function createLocalButton() {
  return document.body.querySelector<HTMLButtonElement>('[data-create-team-local-repo]');
}

function offered(): string[] {
  return [...document.body.querySelectorAll<HTMLElement>('[data-repo-choice]')].map((b) => b.dataset.repoChoice!);
}

async function settled() {
  await flushPromises();
  await new Promise((resolve) => setTimeout(resolve, 10));
  await flushPromises();
}

describe('Team settings: Create a local repository', () => {
  it('is offered beside Add for a team with no repository, and attaches the team\'s own', async () => {
    createTeamLocalRepo.mockResolvedValue({
      team: team(['local:alpha']),
      localRepository: { name: 'alpha', reference: 'local:alpha', created: true },
    });
    await open();
    // The real refresh re-reads the team, which now names its local repository.
    const board = useConsoleStore();
    vi.mocked(board.refresh).mockImplementation(async () => {
      board.$patch({ teams: [team(['local:alpha'])] });
    });

    const add = button('Add');
    const create = createLocalButton();
    expect(create).not.toBeNull();
    expect(create!.textContent?.trim()).toBe('Create a local repository');
    expect(create!.parentElement).toBe(add.parentElement);

    create!.click();
    await settled();

    expect(createTeamLocalRepo).toHaveBeenCalledWith('alpha');
    expect(bodyText()).toContain('Primary repo');
    expect(document.body.querySelector<HTMLInputElement>('[aria-label="Primary repo URL"]')!.value).toBe('local:alpha');
    expect(createLocalButton()).toBeNull();
    // Attached by the Host already: nothing is left for Save to send.
    expect(button('Save changes').hasAttribute('disabled')).toBe(true);
  });

  it('is not offered to a team that has a repository', async () => {
    await open(['https://github.com/owner/app.git']);

    expect(createLocalButton()).toBeNull();
  });

  it('shows the Host\'s refusal and attaches nothing', async () => {
    createTeamLocalRepo.mockRejectedValue(new Error("The local repository 'alpha' could not be created: disk full"));
    await open();

    createLocalButton()!.click();
    await settled();

    expect(bodyText()).toContain("The local repository 'alpha' could not be created: disk full");
    expect(createLocalButton()).not.toBeNull();
  });
});

describe('Team settings: an attach refused by the repository check', () => {
  const sentence = `${MISSING} could not be read: remote: Repository not found. Nothing was created. `
    + 'Choose: create it on GitHub (private), or use a local repository instead.';

  function refusal(choices: string[], failure = 'not-found') {
    return Object.assign(new ActionRefused(sentence, {
      error: sentence,
      code: 'repo-check-failed',
      repos: [{ url: MISSING, failure, reason: 'remote: Repository not found.', choices }],
    }), { status: 422 });
  }

  async function saveMissing() {
    const wrapper = await open();
    await field(wrapper, 'GitHub Repos').setValue(MISSING);
    button('Add').click();
    await settled();
    button('Save changes').click();
    await settled();
    return wrapper;
  }

  it('shows the sentence and only the offered choices; Use a local repository instead resends use-local', async () => {
    setTeamRepos.mockRejectedValueOnce(refusal(['create-on-github', 'use-local']));
    setTeamRepos.mockResolvedValueOnce(team(['local:alpha']));

    await saveMissing();

    expect(setTeamRepos).toHaveBeenNthCalledWith(1, 'alpha', [MISSING]);
    expect(bodyText()).toContain(sentence);
    expect(offered()).toEqual(['create-on-github', 'use-local']);
    expect(bodyText()).not.toContain('Attach anyway');

    document.body.querySelector<HTMLElement>('[data-repo-choice="use-local"]')!.click();
    await settled();

    expect(setTeamRepos).toHaveBeenNthCalledWith(2, 'alpha', [MISSING], { [MISSING]: 'use-local' });
    // What the Host stored replaces what was sent, and the refusal is gone.
    expect(document.body.querySelector<HTMLInputElement>('[aria-label="Primary repo URL"]')!.value).toBe('local:alpha');
    expect(offered()).toEqual([]);
  });

  it('Create it on GitHub resends create-on-github', async () => {
    setTeamRepos.mockRejectedValueOnce(refusal(['create-on-github', 'use-local']));
    setTeamRepos.mockResolvedValueOnce(team([MISSING]));

    await saveMissing();
    document.body.querySelector<HTMLElement>('[data-repo-choice="create-on-github"]')!.click();
    await settled();

    expect(setTeamRepos).toHaveBeenNthCalledWith(2, 'alpha', [MISSING], { [MISSING]: 'create-on-github' });
    expect(offered()).toEqual([]);
  });

  it('a network failure offers Attach anyway, which resends attach-anyway', async () => {
    setTeamRepos.mockRejectedValueOnce(refusal(['use-local', 'attach-anyway'], 'unreachable'));
    setTeamRepos.mockResolvedValueOnce(team([MISSING]));

    await saveMissing();

    expect(offered()).toEqual(['use-local', 'attach-anyway']);
    expect(bodyText()).not.toContain('Create it on GitHub (private)');

    document.body.querySelector<HTMLElement>('[data-repo-choice="attach-anyway"]')!.click();
    await settled();

    expect(setTeamRepos).toHaveBeenNthCalledWith(2, 'alpha', [MISSING], { [MISSING]: 'attach-anyway' });
  });
});
