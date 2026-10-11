// @vitest-environment happy-dom
//
// TEAM SETTINGS → SKILLS. The team's own skills, offered only to its members: listed from the team's
// route, created, edited and deleted through it - never through the instance-wide skill routes -
// and the server's refusal sentence shown as written.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises } from '@vue/test-utils';

const {
  listCatalog,
  getTeamEnv,
  listTeamSkills,
  createTeamSkill,
  updateTeamSkill,
  deleteTeamSkill,
  createSkill,
  updateSkill,
  deleteSkill,
} = vi.hoisted(() => ({
  listCatalog: vi.fn(),
  getTeamEnv: vi.fn(),
  listTeamSkills: vi.fn(),
  createTeamSkill: vi.fn(),
  updateTeamSkill: vi.fn(),
  deleteTeamSkill: vi.fn(),
  createSkill: vi.fn(),
  updateSkill: vi.fn(),
  deleteSkill: vi.fn(),
}));

vi.mock('quasar', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  useQuasar: () => ({ notify: vi.fn() }),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  listCatalog,
  getTeamEnv,
  listTeamSkills,
  createTeamSkill,
  updateTeamSkill,
  deleteTeamSkill,
  createSkill,
  updateSkill,
  deleteSkill,
}));

import TeamSettingsDialog from '../TeamSettingsDialog.vue';
import { useConsoleStore } from '../../stores/console';
import { useSessionStore } from '../../stores/session';
import type { SkillRecord, TeamId } from '../../api/types';
import { bodyText, mountDialog, resetBody } from '../../test/mountQuasar';
import { button, settle, type } from '../../test/formProbe';

const team = {
  id: 'job-hunt' as TeamId,
  name: 'Job Hunt',
  memberAgents: ['claude-headless'],
  additionalInstructions: null,
  maxConcurrent: null,
  effectiveMaxConcurrent: 4,
  paused: false,
  repos: [],
  env: {},
};

const playbook: SkillRecord = {
  id: 7,
  name: 'job-playbook',
  description: 'Running the job hunt',
  roles: ['manager'],
  kind: 'custom',
  body: 'Apply weekly.',
  updatedAt: '2026-09-21T10:00:00Z',
  updatedBy: 'admin@example.com',
  team: 'job-hunt' as TeamId,
};

beforeEach(() => {
  vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new Error('unexpected fetch')));
  listCatalog.mockReset();
  listCatalog.mockResolvedValue({ agents: [{ name: 'claude-headless', mode: 'Headless' }] });
  getTeamEnv.mockReset();
  getTeamEnv.mockResolvedValue({});
  listTeamSkills.mockReset();
  listTeamSkills.mockResolvedValue([playbook]);
  for (const fn of [createTeamSkill, updateTeamSkill]) {
    fn.mockReset();
    fn.mockResolvedValue(playbook);
  }
  deleteTeamSkill.mockReset();
  deleteTeamSkill.mockResolvedValue(undefined);
  for (const fn of [createSkill, updateSkill, deleteSkill]) fn.mockReset();
});

afterEach(() => {
  vi.unstubAllGlobals();
  resetBody();
});

async function openSkillsTab() {
  setActivePinia(createPinia());
  useConsoleStore().$patch({ teams: [team] as never, activeTeamId: team.id, overviewLanded: true });
  useSessionStore().$patch({ user: { id: 'u1', email: 'admin@example.com' } });

  const wrapper = await mountDialog(TeamSettingsDialog, { initialTab: 'skills' }, { pinia: false });
  await flushPromises();
  await settle();
  return wrapper;
}

function row(name: string): HTMLElement {
  const found = document.body.querySelector<HTMLElement>(`[data-team-skill="${name}"]`);
  if (!found) throw new Error(`no team skill row for ${name}`);
  return found;
}

describe('TeamSettingsDialog, Skills tab', () => {
  it("lists the team's own skills from the team's route", async () => {
    const wrapper = await openSkillsTab();

    expect(listTeamSkills).toHaveBeenCalledWith('job-hunt');
    expect(row('job-playbook').textContent).toContain('Running the job hunt');
    expect(row('job-playbook').textContent).toContain('Manager');
    expect(bodyText()).toContain('offered only to this team');

    wrapper.unmount();
  });

  it('says so when the team has none', async () => {
    listTeamSkills.mockResolvedValue([]);
    const wrapper = await openSkillsTab();

    expect(bodyText()).toContain('No team skills yet.');

    wrapper.unmount();
  });

  it('creates a team skill on this team and reads the list again', async () => {
    const wrapper = await openSkillsTab();

    button('Add a team skill').click();
    await settle();
    await type('Name', 'cover-letters');
    await type('Description', 'Writing cover letters');
    button('Save').click();
    await settle();

    expect(createTeamSkill).toHaveBeenCalledWith('job-hunt', {
      name: 'cover-letters',
      description: 'Writing cover letters',
      roles: ['member'],
      body: '',
    });
    expect(createSkill).not.toHaveBeenCalled();
    expect(listTeamSkills).toHaveBeenCalledTimes(2);

    wrapper.unmount();
  });

  it('edits and deletes a team skill through the team', async () => {
    const wrapper = await openSkillsTab();

    row('job-playbook').click();
    await settle();
    await type('Body', 'Apply every Monday.');
    button('Save').click();
    await settle();

    expect(updateTeamSkill).toHaveBeenCalledWith('job-hunt', 'job-playbook', {
      name: 'job-playbook',
      description: 'Running the job hunt',
      roles: ['manager'],
      body: 'Apply every Monday.',
    });

    row('job-playbook').click();
    await settle();
    button('Delete').click();
    await settle();

    expect(deleteTeamSkill).toHaveBeenCalledWith('job-hunt', 'job-playbook');
    expect(updateSkill).not.toHaveBeenCalled();
    expect(deleteSkill).not.toHaveBeenCalled();

    wrapper.unmount();
  });

  it("shows the server's sentence when a name is refused", async () => {
    createTeamSkill.mockRejectedValue(
      Object.assign(new Error("'worktrees' is a built-in skill's name. A team skill needs a name of its own."), { status: 409 }),
    );
    const wrapper = await openSkillsTab();

    button('Add a team skill').click();
    await settle();
    await type('Name', 'worktrees');
    await type('Description', 'Mine');
    button('Save').click();
    await settle();

    expect(bodyText()).toContain('A team skill needs a name of its own.');

    wrapper.unmount();
  });
});
