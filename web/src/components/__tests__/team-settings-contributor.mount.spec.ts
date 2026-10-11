// @vitest-environment happy-dom
//
// TEAM SETTINGS SETS EACH SAVED REPOSITORY'S CONTRIBUTOR SETTINGS: an upstream (contributor
// mode), the fork's owner, DCO sign-off and the CLA note. Only a repository whose settings moved is
// sent; clearing the upstream sends null; a URL that is not one, or is the fork itself, is refused
// on the field and nothing is asked of the server.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, type VueWrapper } from '@vue/test-utils';

const { listCatalog, getTeamEnv, setRepoContributor, setRepoDefaultBranch, setTeamRepos } = vi.hoisted(() => ({
  listCatalog: vi.fn(),
  getTeamEnv: vi.fn(),
  setRepoContributor: vi.fn(),
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
  setRepoContributor,
  setRepoDefaultBranch,
  setTeamRepos,
}));

import TeamSettingsDialog from '../TeamSettingsDialog.vue';
import { useConsoleStore } from '../../stores/console';
import { useSessionStore } from '../../stores/session';
import type { TeamId, TeamRepoContributor } from '../../api/types';
import { bodyText, mountDialog, resetBody } from '../../test/mountQuasar';

const upstream = 'https://github.com/project/Widget.git';

function team(contributors: TeamRepoContributor[]) {
  return {
    id: 'alpha' as TeamId,
    name: 'Alpha',
    memberAgents: ['claude-headless'],
    additionalInstructions: null,
    paused: false,
    repos: contributors.map((entry) => `https://github.com/fork-owner/${entry.repo}.git`),
    defaultBranches: contributors.map((entry) => ({ repo: entry.repo, branch: 'trunk', fromRemote: 'trunk', setByPerson: null })),
    contributors,
    env: {},
  };
}

beforeEach(() => {
  vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new Error('unexpected fetch')));
  listCatalog.mockReset();
  listCatalog.mockResolvedValue({ agents: [{ name: 'claude-headless', mode: 'Headless' }] });
  getTeamEnv.mockReset();
  getTeamEnv.mockResolvedValue({});
  setRepoContributor.mockReset();
  setRepoContributor.mockResolvedValue(undefined);
  setRepoDefaultBranch.mockReset();
  setTeamRepos.mockReset();
});

afterEach(() => {
  vi.unstubAllGlobals();
  resetBody();
});

async function open(contributors: TeamRepoContributor[]) {
  setActivePinia(createPinia());
  const board = useConsoleStore();
  board.$patch({ teams: [team(contributors)], activeTeamId: 'alpha' as TeamId, overviewLanded: true });
  vi.spyOn(board, 'refresh').mockResolvedValue();
  useSessionStore().$patch({ user: { id: 'u1', email: 'admin@example.com' } });
  return mountDialog(TeamSettingsDialog, { initialTab: 'repos' }, { pinia: false });
}

function field(wrapper: VueWrapper, label: string) {
  const found = wrapper.findAllComponents({ name: 'QInput' })
    .find((input) => input.find('input').attributes('aria-label') === label);
  if (!found) throw new Error(`no field labelled ${label}`);
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

const owned: TeamRepoContributor = { repo: 'Widget', upstreamUrl: null, forkOwner: null, dcoSignOff: false, claSignedNote: null };

describe('TeamSettingsDialog: contributing', () => {
  it('sends an upstream, DCO and the CLA note for the repository that moved', async () => {
    const wrapper = await open([owned]);

    expect(bodyText()).toContain('Sign off commits (DCO)');
    await field(wrapper, 'Upstream URL for Widget').setValue(upstream);
    await field(wrapper, 'CLA note for Widget').setValue('signed 2026-09-20');
    await settled();
    const toggle = wrapper.findAllComponents({ name: 'QToggle' })
      .find((candidate) => candidate.text().includes('Sign off commits (DCO)'));
    await toggle!.find('[role="switch"], .q-toggle').trigger('click');
    await settled();
    button('Save changes').click();
    await settled();

    expect(setRepoContributor).toHaveBeenCalledWith('alpha', 'Widget', {
      upstreamUrl: upstream,
      forkOwner: null,
      dcoSignOff: true,
      claSignedNote: 'signed 2026-09-20',
    });
    expect(setTeamRepos).not.toHaveBeenCalled();
  });

  it('clears an upstream with null, and the fork owner with it', async () => {
    const wrapper = await open([{ ...owned, upstreamUrl: upstream, forkOwner: 'fork-owner' }]);

    await field(wrapper, 'Upstream URL for Widget').setValue('');
    await settled();
    button('Save changes').click();
    await settled();

    expect(setRepoContributor).toHaveBeenCalledWith('alpha', 'Widget', {
      upstreamUrl: null, forkOwner: null, dcoSignOff: false, claSignedNote: null,
    });
  });

  it('refuses the fork itself as its upstream and asks nobody', async () => {
    const wrapper = await open([owned]);

    await field(wrapper, 'Upstream URL for Widget').setValue('https://github.com/fork-owner/Widget.git');
    await settled();

    expect(bodyText()).toContain('own URL');
    expect(button('Save changes').hasAttribute('disabled')).toBe(true);
    button('Save changes').click();
    await settled();
    expect(setRepoContributor).not.toHaveBeenCalled();
  });

  it('sends nothing for a repository nobody changed', async () => {
    const wrapper = await open([{ ...owned, upstreamUrl: upstream, forkOwner: 'fork-owner', dcoSignOff: true }]);

    expect(field(wrapper, 'Fork owner for Widget').find('input').element.value).toBe('fork-owner');
    expect(button('Save changes').hasAttribute('disabled')).toBe(true);
  });
});
