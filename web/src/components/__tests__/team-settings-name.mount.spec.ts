// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises } from '@vue/test-utils';

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

async function mountSettings() {
  setActivePinia(createPinia());

  const board = useConsoleStore();
  board.$patch({ teams: [team], activeTeamId: team.id, overviewLanded: true });
  const refresh = vi.spyOn(board, 'refresh').mockResolvedValue();

  const session = useSessionStore();
  session.$patch({ user: { id: 'u1', email: 'admin@example.com' } });

  return { wrapper: await mountDialog(TeamSettingsDialog, {}, { pinia: false }), refresh };
}

function saveButton(): HTMLButtonElement {
  const found = [...document.body.querySelectorAll('button')]
    .find((candidate) => candidate.textContent?.trim() === 'Save changes');

  if (!found) throw new Error('no Save changes button in the rendered dialog');

  return found as HTMLButtonElement;
}

describe('TeamSettingsDialog, mounted, and the removed team rename', () => {
  it('ignores a typed team name and saves only the field route that actually changed', async () => {
    const { wrapper, refresh } = await mountSettings();

    const inputs = wrapper.findAllComponents({ name: 'QInput' });
    const name = inputs.find((input) => input.props('label') === 'Team name');
    const instructions = inputs.find((input) => input.props('label') === 'Additional instructions (optional)');

    expect(name, 'no team name input in the rendered dialog').toBeTruthy();
    expect(name!.props('readonly')).toBe(true);
    expect(instructions, 'no Additional instructions box in the rendered dialog').toBeTruthy();

    await name!.setValue('Beta Team');
    await flushPromises();

    expect(saveButton().hasAttribute('disabled')).toBe(true);

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

/** The team's prompt is the built-in role prompt; a person may only append to it. */
describe('TeamSettingsDialog, Additional instructions', () => {
  it('offers no Prompt picker, and hints that the box is appended and never replaces the role prompt', async () => {
    const { wrapper } = await mountSettings();

    const labels = wrapper.findAllComponents({ name: 'QSelect' }).map((select) => String(select.props('label')));
    expect(labels.some((label) => /prompt|told/i.test(label))).toBe(false);

    const instructions = wrapper.findAllComponents({ name: 'QInput' })
      .find((input) => input.props('label') === 'Additional instructions (optional)');
    expect(instructions!.props('hint')).toContain('Appended after the built-in role prompt');
    expect(instructions!.props('hint')).toContain('never replaces');

    wrapper.unmount();
  });

  it('opens on what the team has stored, and clearing it is a change Save sends', async () => {
    setActivePinia(createPinia());

    const board = useConsoleStore();
    board.$patch({
      teams: [{ ...team, additionalInstructions: 'Be brief.' }],
      activeTeamId: team.id,
      overviewLanded: true,
    });
    vi.spyOn(board, 'refresh').mockResolvedValue();
    useSessionStore().$patch({ user: { id: 'u1', email: 'admin@example.com' } });

    const wrapper = await mountDialog(TeamSettingsDialog, {}, { pinia: false });
    const instructions = wrapper.findAllComponents({ name: 'QInput' })
      .find((input) => input.props('label') === 'Additional instructions (optional)');

    expect(instructions!.props('modelValue')).toBe('Be brief.');
    expect(saveButton().hasAttribute('disabled')).toBe(true);

    await instructions!.setValue('');
    await flushPromises();
    saveButton().click();
    await flushPromises();

    expect(setTeamAdditionalInstructions).toHaveBeenCalledWith(team.id, '');

    wrapper.unmount();
  });
});
