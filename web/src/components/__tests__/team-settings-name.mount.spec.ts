// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, type VueWrapper } from '@vue/test-utils';

const {
  listCatalog,
  getTeamEnv,
  setMemberAgents,
  setTeamAdditionalInstructions,
  setTeamRepos,
} = vi.hoisted(() => ({
  listCatalog: vi.fn(),
  getTeamEnv: vi.fn(),
  setMemberAgents: vi.fn(),
  setTeamAdditionalInstructions: vi.fn(),
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
  setMemberAgents,
  setTeamAdditionalInstructions,
  setTeamRepos,
}));

import TeamSettingsDialog from '../TeamSettingsDialog.vue';
import { useConsoleStore } from '../../stores/console';
import { useSessionStore } from '../../stores/session';
import type { TeamId } from '../../api/types';
import { mountDialog, resetBody } from '../../test/mountQuasar';
import {
  TeamInstructionsHint,
  TeamInstructionsLabel,
  TeamInstructionsTakesEffect,
} from '../../lib/additionalInstructions';

const team = {
  id: 'alpha' as TeamId,
  name: 'Alpha',
  memberAgents: ['claude-headless'],
  additionalInstructions: null,
  maxConcurrent: null,
  effectiveMaxConcurrent: 4,
  paused: false,
  repos: [],
  env: {},
};

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

  setMemberAgents.mockReset();
  setTeamAdditionalInstructions.mockReset();
  setTeamRepos.mockReset();
});

afterEach(() => {
  vi.unstubAllGlobals();
  resetBody();
});

async function mountSettings(initialTab?: 'general' | 'instructions') {
  setActivePinia(createPinia());

  const board = useConsoleStore();
  board.$patch({ teams: [team], activeTeamId: team.id, overviewLanded: true });
  const refresh = vi.spyOn(board, 'refresh').mockResolvedValue();

  const session = useSessionStore();
  session.$patch({ user: { id: 'u1', email: 'admin@example.com' } });

  return {
    wrapper: await mountDialog(TeamSettingsDialog, initialTab ? { initialTab } : {}, { pinia: false }),
    refresh,
  };
}

function saveButton(): HTMLButtonElement {
  const found = [...document.body.querySelectorAll('button')]
    .find((candidate) => candidate.textContent?.trim() === 'Save changes');

  if (!found) throw new Error('no Save changes button in the rendered dialog');

  return found as HTMLButtonElement;
}

/** The Team instructions box, found by its accessible name: the heading above it carries the words. */
function instructionsBox(wrapper: VueWrapper) {
  return wrapper.findAllComponents({ name: 'QInput' })
    .find((input) => input.find('textarea').exists()
      && input.find('textarea').attributes('aria-label') === TeamInstructionsLabel);
}

function tabLabels(): string[] {
  return [...document.body.querySelectorAll('.q-tab')]
    .map((tab) => tab.querySelector('.q-tab__label')?.textContent?.trim() ?? '');
}

async function chooseTab(label: string) {
  const found = [...document.body.querySelectorAll<HTMLElement>('.q-tab')]
    .find((tab) => tab.querySelector('.q-tab__label')?.textContent?.trim() === label);

  if (!found) throw new Error(`no ${label} tab in the rendered dialog`);

  found.click();
  await flushPromises();
  await new Promise((resolve) => setTimeout(resolve, 10));
  await flushPromises();
}

describe('TeamSettingsDialog, mounted, and the removed team rename', () => {
  it('ignores a typed team name and saves only the field route that actually changed', async () => {
    const { wrapper, refresh } = await mountSettings();

    const name = wrapper.findAllComponents({ name: 'QInput' })
      .find((input) => input.props('label') === 'Team name');

    expect(name, 'no team name input in the rendered dialog').toBeTruthy();
    expect(name!.props('readonly')).toBe(true);

    await name!.setValue('Beta Team');
    await flushPromises();

    expect(saveButton().hasAttribute('disabled')).toBe(true);

    await chooseTab('Team instructions');
    const instructions = instructionsBox(wrapper);
    expect(instructions, 'no Team instructions box in the rendered dialog').toBeTruthy();

    await instructions!.setValue('Prefer small commits.');
    await flushPromises();

    expect(saveButton().hasAttribute('disabled')).toBe(false);

    saveButton().click();
    await flushPromises();

    expect(fetchUnexpected).not.toHaveBeenCalled();
    expect(refresh).toHaveBeenCalledOnce();
    expect(setMemberAgents).not.toHaveBeenCalled();
    expect(setTeamAdditionalInstructions).toHaveBeenCalledOnce();
    expect(setTeamAdditionalInstructions).toHaveBeenCalledWith(team.id, 'Prefer small commits.');
    expect(setTeamRepos).not.toHaveBeenCalled();

    wrapper.unmount();
  });
});

/**
 * TEAM INSTRUCTIONS HAVE A TAB OF THEIR OWN, right after Members: every agent member reads them, the
 * Manager too, so a box on General beside Dynamic members read as a setting for hired members only.
 */
describe('TeamSettingsDialog, Team instructions', () => {
  it('orders the tabs General, Members, Team instructions, GitHub Repos, Environment', async () => {
    const { wrapper } = await mountSettings();

    const labels = tabLabels();
    expect(labels).toHaveLength(5);
    expect(labels[0]).toBe('General');
    expect(labels[1]).toMatch(/^Members \(\d+\)$/);
    expect(labels.slice(2)).toEqual(['Team instructions', 'GitHub Repos', 'Environment']);

    wrapper.unmount();
  });

  it('no longer has the box on General', async () => {
    const { wrapper } = await mountSettings('general');

    expect(document.body.textContent).toContain('Dynamic members');
    expect(instructionsBox(wrapper)).toBeUndefined();
    expect(document.body.querySelector('textarea')).toBeNull();
    expect(document.body.textContent).not.toContain(TeamInstructionsLabel);

    wrapper.unmount();
  });

  it('shows the heading, the shared hint and when it takes effect, and offers no Prompt picker', async () => {
    const { wrapper } = await mountSettings('instructions');

    const labels = wrapper.findAllComponents({ name: 'QSelect' }).map((select) => String(select.props('label')));
    expect(labels.some((label) => /prompt|told/i.test(label))).toBe(false);

    expect(document.body.textContent).toContain('Team instructions (optional)');
    const instructions = instructionsBox(wrapper);
    expect(instructions!.props('hint')).toBe(TeamInstructionsHint);
    expect(instructions!.props('hint')).toBe(
      "How this team works: its purpose, rules, conventions and playbook. Every member reads it: the Manager and every member, whether the Manager hired them or a person added them. It is added after each member's built-in prompt and its own instructions, and never replaces them.",
    );
    expect(document.body.textContent).toContain(TeamInstructionsTakesEffect);
    expect(TeamInstructionsTakesEffect).toBe("Takes effect on each member's next run.");

    wrapper.unmount();
  });

  it('opens on what the team has stored, and clearing it is a change Save sends through the same route', async () => {
    setActivePinia(createPinia());

    const board = useConsoleStore();
    board.$patch({
      teams: [{ ...team, additionalInstructions: 'Be brief.' }],
      activeTeamId: team.id,
      overviewLanded: true,
    });
    vi.spyOn(board, 'refresh').mockResolvedValue();
    useSessionStore().$patch({ user: { id: 'u1', email: 'admin@example.com' } });

    const wrapper = await mountDialog(TeamSettingsDialog, { initialTab: 'instructions' }, { pinia: false });
    const instructions = instructionsBox(wrapper);

    expect(instructions!.props('modelValue')).toBe('Be brief.');
    expect(saveButton().hasAttribute('disabled')).toBe(true);

    await instructions!.setValue('');
    await flushPromises();
    saveButton().click();
    await flushPromises();

    expect(setTeamAdditionalInstructions).toHaveBeenCalledOnce();
    expect(setTeamAdditionalInstructions).toHaveBeenCalledWith(team.id, '');
    expect(fetchUnexpected).not.toHaveBeenCalled();

    wrapper.unmount();
  });
});
