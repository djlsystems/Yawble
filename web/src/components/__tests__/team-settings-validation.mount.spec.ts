// @vitest-environment happy-dom
//
// TEAM SETTINGS, MOUNTED CLOSED AND THEN OPENED: repository URLs carry `repoUrlRules` and
// environment rows carry `envNameRules`, and both Save buttons follow them. A bad URL or a
// `HARNESS_` name is refused on screen and the server is never asked - `fetch` is stubbed to fail
// loudly, and the write routes must not be called.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, type VueWrapper } from '@vue/test-utils';

const {
  listCatalog, getTeamEnv, setTeamEnv, setMemberAgents, setMemberPrompt, setTeamRepos,
} = vi.hoisted(() => ({
  listCatalog: vi.fn(),
  getTeamEnv: vi.fn(),
  setTeamEnv: vi.fn(),
  setMemberAgents: vi.fn(),
  setMemberPrompt: vi.fn(),
  setTeamRepos: vi.fn(),
}));

const fetchUnexpected = vi.fn();

vi.mock('quasar', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  useQuasar: () => ({ notify: vi.fn() }),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  listCatalog,
  getTeamEnv,
  setTeamEnv,
  setMemberAgents,
  setMemberPrompt,
  setTeamRepos,
}));

import TeamSettingsDialog from '../TeamSettingsDialog.vue';
import { useConsoleStore } from '../../stores/console';
import { useSessionStore } from '../../stores/session';
import type { TeamId } from '../../api/types';
import { bodyText, mountDialog, resetBody } from '../../test/mountQuasar';

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
  fetchUnexpected.mockReset();
  fetchUnexpected.mockRejectedValue(new Error('unexpected fetch'));
  vi.stubGlobal('fetch', fetchUnexpected);

  listCatalog.mockReset();
  listCatalog.mockResolvedValue({
    agents: [{ name: 'claude-headless', mode: 'Headless' }],
  });
  getTeamEnv.mockReset();
  getTeamEnv.mockResolvedValue({});
  setTeamEnv.mockReset();
  setTeamEnv.mockImplementation(async (_team: TeamId, env: Record<string, string>) => env);
  setMemberAgents.mockReset();
  setMemberPrompt.mockReset();
  setTeamRepos.mockReset();
  setTeamRepos.mockResolvedValue(undefined);
});

afterEach(() => {
  vi.unstubAllGlobals();
  resetBody();
});

async function open(initialTab: 'repos' | 'env', repos: string[] = []) {
  setActivePinia(createPinia());

  const board = useConsoleStore();
  board.$patch({ teams: [team(repos)], activeTeamId: 'alpha' as TeamId, overviewLanded: true });
  vi.spyOn(board, 'refresh').mockResolvedValue();
  useSessionStore().$patch({ user: { id: 'u1', email: 'admin@example.com' } });

  return mountDialog(TeamSettingsDialog, { initialTab }, { pinia: false });
}

function inputs(wrapper: VueWrapper, label: string) {
  return wrapper.findAllComponents({ name: 'QInput' })
    .filter((input) => input.props('label') === label
      || input.find('input').attributes('aria-label') === label);
}

function field(wrapper: VueWrapper, label: string, at = 0) {
  const found = inputs(wrapper, label)[at];

  if (!found) throw new Error(`no ${label} input ${at} in the rendered dialog`);

  return found;
}

/** The LAST button with this label: the Environment tab's own Save sits before the footer's. */
function button(label: string): HTMLButtonElement {
  const found = [...document.body.querySelectorAll('button')]
    .filter((candidate) => candidate.textContent?.trim() === label)
    .pop();

  if (!found) throw new Error(`no ${label} button in the rendered dialog`);

  return found as HTMLButtonElement;
}

async function validated() {
  await flushPromises();
  await new Promise((resolve) => setTimeout(resolve, 10));
  await flushPromises();
}

describe('TeamSettingsDialog: repository URLs', () => {
  it('disables Save untouched, and enables it once a valid URL is added', async () => {
    const wrapper = await open('repos');

    expect(button('Save changes').hasAttribute('disabled')).toBe(true);

    await field(wrapper, 'GitHub Repos').setValue('https://github.com/owner/app.git');
    button('Add').click();
    await validated();

    expect(button('Save changes').hasAttribute('disabled')).toBe(false);

    button('Save changes').click();
    await validated();

    expect(setTeamRepos).toHaveBeenCalledWith('alpha', ['https://github.com/owner/app.git']);
  });

  it('refuses a URL that cannot be cloned and asks nobody', async () => {
    const wrapper = await open('repos');

    await field(wrapper, 'GitHub Repos').setValue('github.com/owner/app');
    await validated();

    expect(bodyText()).toContain('Use an absolute http or https URL');
    expect(button('Add').hasAttribute('disabled')).toBe(true);
    expect(button('Save changes').hasAttribute('disabled')).toBe(true);

    button('Save changes').click();
    await validated();

    expect(setTeamRepos).not.toHaveBeenCalled();
    expect(fetchUnexpected).not.toHaveBeenCalled();
  });

  it('marks a listed URL edited into one that clones into the same folder', async () => {
    const wrapper = await open('repos', ['https://github.com/owner/app.git', 'https://github.com/owner/web.git']);

    await field(wrapper, 'Repo 2 URL').setValue('https://gitlab.com/other/app');
    await validated();

    expect(bodyText()).toContain("Another URL in this list already clones into 'app'.");
    expect(button('Save changes').hasAttribute('disabled')).toBe(true);
    expect(setTeamRepos).not.toHaveBeenCalled();
  });
});

describe('TeamSettingsDialog: a duplicate URL, submitted anyway', () => {
  it('sends nothing whether Save is clicked or the form is submitted', async () => {
    const wrapper = await open('repos', ['https://github.com/owner/app.git', 'https://github.com/owner/web.git']);

    await field(wrapper, 'Repo 2 URL').setValue('https://github.com/owner/app');
    await validated();

    expect(bodyText()).toContain("Another URL in this list already clones into 'app'.");
    expect(button('Save changes').hasAttribute('disabled')).toBe(true);

    button('Save changes').click();
    // What Enter in a field does: the form's own submit, which a disabled button does not stop.
    document.body.querySelector('form')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    await validated();

    expect(setTeamRepos).not.toHaveBeenCalled();
    expect(fetchUnexpected).not.toHaveBeenCalled();
  });
});

describe('TeamSettingsDialog: environment', () => {
  it('refuses a HARNESS_ name with a sentence and keeps its Save off', async () => {
    const wrapper = await open('env');

    button('Add').click();
    await flushPromises();

    await field(wrapper, 'Name').setValue('HARNESS_TOKEN');
    await field(wrapper, 'Value').setValue('x');
    await validated();

    expect(bodyText()).toContain("Names beginning HARNESS_ are the platform's own");
    expect(button('Save').hasAttribute('disabled')).toBe(true);

    button('Save').click();
    await validated();

    expect(setTeamEnv).not.toHaveBeenCalled();
    expect(fetchUnexpected).not.toHaveBeenCalled();
  });

  it('marks a name set twice, and saves once the rows are valid', async () => {
    const wrapper = await open('env');

    button('Add').click();
    button('Add').click();
    await flushPromises();

    await field(wrapper, 'Name', 0).setValue('TEST_EMAIL');
    await field(wrapper, 'Value', 0).setValue('a@example.com');
    await field(wrapper, 'Name', 1).setValue('TEST_EMAIL');
    await validated();

    expect(bodyText()).toContain("'TEST_EMAIL' is set twice.");
    expect(button('Save').hasAttribute('disabled')).toBe(true);

    await field(wrapper, 'Name', 1).setValue('TEST_PASSWORD');
    await field(wrapper, 'Value', 1).setValue('secret');
    await validated();

    expect(button('Save').hasAttribute('disabled')).toBe(false);

    button('Save').click();
    await validated();

    expect(setTeamEnv).toHaveBeenCalledWith('alpha', { TEST_EMAIL: 'a@example.com', TEST_PASSWORD: 'secret' });
  });
});
