// @vitest-environment happy-dom
//
// TEAM SETTINGS TURNS THE TEAM PICTURE ON AND OFF for an existing team: the control opens on the
// team's own `teamPicture`, Save sends it on its own route (`PUT /api/teams/{team}/team-picture`),
// and only when it moved.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises } from '@vue/test-utils';

const { listCatalog, getTeamEnv, setTeamPicture, setTeamWorkflowBudget } = vi.hoisted(() => ({
  listCatalog: vi.fn(),
  getTeamEnv: vi.fn(),
  setTeamPicture: vi.fn(),
  setTeamWorkflowBudget: vi.fn(),
}));

vi.mock('quasar', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  useQuasar: () => ({ notify: vi.fn() }),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  listCatalog,
  getTeamEnv,
  setTeamPicture,
  setTeamWorkflowBudget,
}));

import TeamSettingsDialog from '../TeamSettingsDialog.vue';
import { useConsoleStore } from '../../stores/console';
import { useSessionStore } from '../../stores/session';
import type { TeamId } from '../../api/types';
import { mountDialog, resetBody } from '../../test/mountQuasar';
import { TeamPictureLabel } from '../../lib/teamPicture';

function team(teamPicture: boolean | undefined) {
  return {
    id: 'alpha' as TeamId,
    name: 'Alpha',
    memberAgents: ['claude-headless'],
    additionalInstructions: null,
    maxConcurrent: null,
    effectiveMaxConcurrent: 4,
    paused: false,
    repos: [],
    env: {},
    budgetTokens: 1000,
    ...(teamPicture === undefined ? {} : { teamPicture }),
  };
}

beforeEach(() => {
  vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new Error('unexpected fetch')));
  listCatalog.mockReset();
  listCatalog.mockResolvedValue({ agents: [{ name: 'claude-headless', mode: 'Headless' }] });
  getTeamEnv.mockReset();
  getTeamEnv.mockResolvedValue({});
  setTeamPicture.mockReset();
  setTeamPicture.mockResolvedValue({});
  setTeamWorkflowBudget.mockReset();
});

afterEach(() => {
  vi.unstubAllGlobals();
  resetBody();
});

async function mountSettings(teamPicture: boolean | undefined) {
  setActivePinia(createPinia());
  const board = useConsoleStore();
  board.$patch({ teams: [team(teamPicture)], activeTeamId: 'alpha' as TeamId, overviewLanded: true });
  vi.spyOn(board, 'refresh').mockResolvedValue();
  useSessionStore().$patch({ user: { id: 'u1', email: 'admin@example.com' } });
  return mountDialog(TeamSettingsDialog, {}, { pinia: false });
}

async function settled() {
  await flushPromises();
  await new Promise((resolve) => setTimeout(resolve, 10));
  await flushPromises();
}

function toggle(): HTMLElement {
  const found = document.body.querySelector<HTMLElement>('[data-team-picture]');
  if (!found) throw new Error('no Team picture control in Team settings');
  return found;
}

function saveButton(): HTMLButtonElement {
  const found = [...document.body.querySelectorAll('button')].find((b) => b.textContent?.trim() === 'Save changes');
  if (!found) throw new Error('no Save changes button');
  return found as HTMLButtonElement;
}

describe('Team settings: the Team picture control', () => {
  it('opens on the team\'s own choice, under its own heading on General', async () => {
    await mountSettings(true);
    expect(document.querySelector('[data-team-picture-option]')!.previousElementSibling!.textContent).toBe(TeamPictureLabel);
    expect(toggle().getAttribute('aria-checked')).toBe('true');
    resetBody();

    await mountSettings(undefined);
    expect(toggle().getAttribute('aria-checked')).toBe('false');
  });

  it('turns it on with Save, on its own route, and nothing else moves', async () => {
    await mountSettings(false);
    expect(saveButton().hasAttribute('disabled')).toBe(true);

    toggle().click();
    await settled();
    expect(saveButton().hasAttribute('disabled')).toBe(false);
    saveButton().click();
    await settled();

    expect(setTeamPicture).toHaveBeenCalledTimes(1);
    expect(setTeamPicture).toHaveBeenCalledWith('alpha', true);
    expect(setTeamWorkflowBudget).not.toHaveBeenCalled();
  });

  it('turns it off with Save', async () => {
    await mountSettings(true);
    toggle().click();
    await settled();
    saveButton().click();
    await settled();

    expect(setTeamPicture).toHaveBeenCalledWith('alpha', false);
  });

  it('sends nothing when it did not move', async () => {
    await mountSettings(true);
    toggle().click();
    await settled();
    toggle().click();
    await settled();

    expect(saveButton().hasAttribute('disabled')).toBe(true);
    expect(setTeamPicture).not.toHaveBeenCalled();
  });
});
