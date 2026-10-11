// @vitest-environment happy-dom
//
// TEAM SETTINGS SETS OR CLEARS EACH REPOSITORY'S DEFAULT BRANCH. What a person types is sent
// as their choice; Clear sends null, which returns the repository to what origin's HEAD names. A
// repository nothing has read says so, and nothing here offers `main` in its place.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, type VueWrapper } from '@vue/test-utils';

const { listCatalog, getTeamEnv, setRepoDefaultBranch, setTeamRepos } = vi.hoisted(() => ({
  listCatalog: vi.fn(),
  getTeamEnv: vi.fn(),
  setRepoDefaultBranch: vi.fn(),
  setTeamRepos: vi.fn(),
}));

vi.mock('quasar', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  useQuasar: () => ({ notify: vi.fn() }),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  listCatalog,
  getTeamEnv,
  setRepoDefaultBranch,
  setTeamRepos,
}));

import TeamSettingsDialog from '../TeamSettingsDialog.vue';
import { useConsoleStore } from '../../stores/console';
import { useSessionStore } from '../../stores/session';
import type { TeamId, TeamRepoDefaultBranch } from '../../api/types';
import { bodyText, mountDialog, resetBody } from '../../test/mountQuasar';

function team(defaultBranches: TeamRepoDefaultBranch[]) {
  return {
    id: 'alpha' as TeamId,
    name: 'Alpha',
    memberAgents: ['claude-headless'],
    additionalInstructions: null,
    paused: false,
    repos: defaultBranches.map((entry) => `https://github.com/owner/${entry.repo}.git`),
    defaultBranches,
    env: {},
  };
}

beforeEach(() => {
  vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new Error('unexpected fetch')));
  listCatalog.mockReset();
  listCatalog.mockResolvedValue({ agents: [{ name: 'claude-headless', mode: 'Headless' }] });
  getTeamEnv.mockReset();
  getTeamEnv.mockResolvedValue({});
  setRepoDefaultBranch.mockReset();
  setRepoDefaultBranch.mockResolvedValue(undefined);
  setTeamRepos.mockReset();
});

afterEach(() => {
  vi.unstubAllGlobals();
  resetBody();
});

async function open(defaultBranches: TeamRepoDefaultBranch[]) {
  setActivePinia(createPinia());
  const board = useConsoleStore();
  board.$patch({ teams: [team(defaultBranches)], activeTeamId: 'alpha' as TeamId, overviewLanded: true });
  vi.spyOn(board, 'refresh').mockResolvedValue();
  useSessionStore().$patch({ user: { id: 'u1', email: 'admin@example.com' } });
  return mountDialog(TeamSettingsDialog, { initialTab: 'repos' }, { pinia: false });
}

function branchField(wrapper: VueWrapper, repo: string) {
  const found = wrapper.findAllComponents({ name: 'QInput' })
    .find((input) => input.find('input').attributes('aria-label') === `Default branch for ${repo}`);
  if (!found) throw new Error(`no default branch field for ${repo}`);
  return found;
}

function button(label: string): HTMLButtonElement {
  const found = [...document.body.querySelectorAll('button')]
    .filter((candidate) => candidate.textContent?.trim() === label)
    .pop();
  if (!found) throw new Error(`no ${label} button in the rendered dialog`);
  return found as HTMLButtonElement;
}

async function settled() {
  await flushPromises();
  await new Promise((resolve) => setTimeout(resolve, 10));
  await flushPromises();
}

describe('TeamSettingsDialog: default branch', () => {
  it('says a branch nothing has read is not known, and offers no main in its place', async () => {
    const wrapper = await open([{ repo: 'app', branch: null, fromRemote: null, setByPerson: null }]);

    expect(bodyText()).toContain('Not known. A Fetch reads it from origin, or set it here.');
    expect(branchField(wrapper, 'app').find('input').attributes('placeholder')).toBe('not known');
    expect(button('Save changes').hasAttribute('disabled')).toBe(true);
  });

  it('sends what a person types as their choice', async () => {
    const wrapper = await open([{ repo: 'app', branch: 'trunk', fromRemote: 'trunk', setByPerson: null }]);

    expect(bodyText()).toContain('From origin: trunk.');
    await branchField(wrapper, 'app').setValue('release');
    await settled();
    button('Save changes').click();
    await settled();

    expect(setRepoDefaultBranch).toHaveBeenCalledWith('alpha', 'app', 'release');
    expect(setTeamRepos).not.toHaveBeenCalled();
  });

  it('clears a person\'s choice with null', async () => {
    await open([{ repo: 'app', branch: 'release', fromRemote: 'trunk', setByPerson: 'release' }]);

    expect(bodyText()).toContain('kept across Fetches until cleared');
    button('Clear').click();
    await settled();
    button('Save changes').click();
    await settled();

    expect(setRepoDefaultBranch).toHaveBeenCalledWith('alpha', 'app', null);
  });

  it('refuses a name git would not take and asks nobody', async () => {
    const wrapper = await open([{ repo: 'app', branch: 'trunk', fromRemote: 'trunk', setByPerson: null }]);

    await branchField(wrapper, 'app').setValue('bad name');
    await settled();

    expect(bodyText()).toContain("'bad name' is not a branch name git accepts.");
    expect(button('Save changes').hasAttribute('disabled')).toBe(true);
    button('Save changes').click();
    await settled();
    expect(setRepoDefaultBranch).not.toHaveBeenCalled();
  });
});
