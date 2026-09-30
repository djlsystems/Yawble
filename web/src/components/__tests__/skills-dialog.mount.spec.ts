// @vitest-environment happy-dom
//
// SKILLS. One table of two kinds: Custom by default, built-ins behind "Show built-in" and
// opened read-only. A new skill must choose its roles (member by default), its name follows the
// skill store's rule, and the server's refusal sentence is shown as written.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const { listSkillsPage, createSkill, updateSkill, deleteSkill, updateTeamSkill, deleteTeamSkill } = vi.hoisted(() => ({
  listSkillsPage: vi.fn(),
  createSkill: vi.fn(),
  updateSkill: vi.fn(),
  deleteSkill: vi.fn(),
  updateTeamSkill: vi.fn(),
  deleteTeamSkill: vi.fn(),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  listSkillsPage,
  createSkill,
  updateSkill,
  deleteSkill,
  updateTeamSkill,
  deleteTeamSkill,
}));

import SkillsDialog from '../SkillsDialog.vue';
import type { SkillKindFilter, SkillRecord, TeamId } from '../../api/types';
import { useConsoleStore } from '../../stores/console';
import { flushPromises } from '@vue/test-utils';
import { bodyText, mountDialog, resetBody } from '../../test/mountQuasar';
import { blur, button, field, hasError, isDisabled, settle, type } from '../../test/formProbe';

/** QVirtualScroll lays out its slice on a 35ms debounce, so a row is on the page only after it. */
async function layout() {
  await flushPromises();
  await new Promise((resolve) => setTimeout(resolve, 60));
  await flushPromises();
}

async function mountSkills() {
  const wrapper = await mountDialog(SkillsDialog);
  useConsoleStore().$patch({ teams: [{ id: 'job-hunt' as TeamId, name: 'Job Hunt' }] as never });
  await layout();
  return wrapper;
}

const custom: SkillRecord = {
  id: 20,
  name: 'code-review',
  description: 'Reviewing a diff',
  roles: ['member'],
  kind: 'custom',
  body: 'Read the diff.',
  updatedAt: '2026-09-20T10:00:00Z',
  updatedBy: 'kofi@example.com',
};

const teamSkill: SkillRecord = {
  id: 25,
  name: 'job-playbook',
  description: 'Running the job hunt',
  roles: ['manager'],
  kind: 'custom',
  body: 'Apply weekly.',
  updatedAt: '2026-09-21T10:00:00Z',
  updatedBy: 'kofi@example.com',
  team: 'job-hunt' as TeamId,
};

const builtIn: SkillRecord = {
  id: 3,
  name: 'worktrees',
  description: 'Cutting a tree',
  roles: ['member'],
  kind: 'builtin',
  body: 'Built-in body',
  updatedAt: null,
  updatedBy: null,
};

beforeEach(() => {
  listSkillsPage.mockReset();
  listSkillsPage.mockImplementation(async (query: { kind: SkillKindFilter }) =>
    query.kind === 'custom' ? [teamSkill, custom] : [teamSkill, custom, builtIn],
  );
  createSkill.mockReset();
  createSkill.mockResolvedValue(custom);
  updateSkill.mockReset();
  updateSkill.mockResolvedValue(custom);
  deleteSkill.mockReset();
  deleteSkill.mockResolvedValue(undefined);
  updateTeamSkill.mockReset();
  updateTeamSkill.mockResolvedValue(teamSkill);
  deleteTeamSkill.mockReset();
  deleteTeamSkill.mockResolvedValue(undefined);
});

afterEach(resetBody);

function headers(): string[] {
  return [...document.body.querySelectorAll('thead th')].map((th) => th.textContent?.trim() ?? '');
}

function row(name: string): HTMLElement {
  const found = document.body.querySelector<HTMLElement>(`tr[data-skill="${name}"]`);
  if (!found) throw new Error(`no row for ${name}`);
  return found;
}

function hasButton(label: string): boolean {
  try {
    button(label);
    return true;
  } catch {
    return false;
  }
}

async function toggleBuiltIn() {
  const toggle = [...document.body.querySelectorAll<HTMLElement>('.q-toggle')]
    .find((candidate) => candidate.textContent?.includes('Show built-in'));
  if (!toggle) throw new Error('no Show built-in toggle');
  toggle.click();
  await layout();
}

async function openCreate() {
  const wrapper = await mountSkills();
  button('Add a skill').click();
  await settle();
  return wrapper;
}

describe('SkillsDialog table, mounted', () => {
  it('is a table of Name, Description, Roles, Team, Kind and Last modified', async () => {
    const wrapper = await mountSkills();

    expect(headers()).toEqual(['Name', 'Description', 'Roles', 'Team', 'Kind', 'Last modified']);
    const cells = [...row('code-review').querySelectorAll('td')].map((td) => td.textContent?.trim());
    expect(cells[0]).toBe('code-review');
    expect(cells[1]).toBe('Reviewing a diff');
    expect(cells[2]).toBe('Member');
    expect(cells[3]).toBe('All teams');
    expect(cells[4]).toBe('Custom');
    expect(cells[5]).toContain('by kofi@example.com');

    wrapper.unmount();
  });

  // Every table's columns resize (lib/resizableColumns.ts); this pins that the Skills table is one.
  it('gives each column a resize handle and takes back the widths stored for this table', async () => {
    localStorage.setItem('harness.columns.skills', JSON.stringify({ Name: 220 }))
    try {
      const wrapper = await mountSkills()

      const cells = Array.from(document.body.querySelectorAll('thead th')) as HTMLElement[]
      expect(cells.length).toBe(6)
      expect(cells.every((th) => th.querySelector('.os-col-resizer'))).toBe(true)
      expect(cells[0]?.style.width).toBe('220px')

      wrapper.unmount()
    } finally {
      localStorage.removeItem('harness.columns.skills')
    }
  })

  it('asks for Custom skills by default, through the cursor', async () => {
    const wrapper = await mountSkills();

    expect(listSkillsPage).toHaveBeenCalled();
    const [query, before, take] = listSkillsPage.mock.calls[0]!;
    expect(query).toMatchObject({ kind: 'custom' });
    expect(before).toBeUndefined();
    expect(take).toBe(50);
    expect(bodyText()).not.toContain('worktrees');

    wrapper.unmount();
  });

  it('shows built-ins when asked, and a built-in opens read-only', async () => {
    const wrapper = await mountSkills();

    await toggleBuiltIn();

    expect(listSkillsPage.mock.calls.at(-1)![0]).toMatchObject({ kind: 'all' });
    expect(row('worktrees').textContent).toContain('Built-in');

    row('worktrees').click();
    await settle();

    expect(bodyText()).toContain('cannot be edited');
    expect(hasButton('Save')).toBe(false);
    expect(hasButton('Delete')).toBe(false);
    expect(field('Name').hasAttribute('readonly')).toBe(true);
    expect(field('Body').hasAttribute('readonly')).toBe(true);

    wrapper.unmount();
  });

  it('names the team next to a team skill, by the name a person reads', async () => {
    const wrapper = await mountSkills();

    const cells = [...row('job-playbook').querySelectorAll('td')].map((td) => td.textContent?.trim());
    expect(cells[3]).toBe('Job Hunt');

    wrapper.unmount();
  });

  it('writes a team skill through its team, never the instance-wide routes', async () => {
    const wrapper = await mountSkills();

    row('job-playbook').click();
    await settle();
    await type('Description', 'Running the job hunt well');
    button('Save').click();
    await settle();

    expect(updateTeamSkill).toHaveBeenCalledWith('job-hunt', 'job-playbook', {
      name: 'job-playbook',
      description: 'Running the job hunt well',
      roles: ['manager'],
      body: 'Apply weekly.',
    });
    expect(updateSkill).not.toHaveBeenCalled();

    row('job-playbook').click();
    await settle();
    button('Delete').click();
    await settle();

    expect(deleteTeamSkill).toHaveBeenCalledWith('job-hunt', 'job-playbook');
    expect(deleteSkill).not.toHaveBeenCalled();

    wrapper.unmount();
  });

  it('offers no Scope, team picker, gated or drift controls in the editor', async () => {
    const wrapper = await openCreate();

    const editor = [...document.body.querySelectorAll('.q-dialog .q-card')].at(-1)?.textContent ?? '';
    expect(editor).not.toContain('Scope');
    expect(editor).not.toContain('Team');
    expect(bodyText()).not.toMatch(/gated/i);
    expect(bodyText()).not.toContain('differs from this build');
    expect(hasButton('Reindex')).toBe(false);

    wrapper.unmount();
  });

  it('opens a custom skill for editing, with Delete', async () => {
    const wrapper = await mountSkills();

    row('code-review').click();
    await settle();

    expect(hasButton('Save')).toBe(true);
    await type('Description', 'Reviewing a diff carefully');
    button('Save').click();
    await settle();

    expect(updateSkill).toHaveBeenCalledWith('code-review', {
      name: 'code-review',
      description: 'Reviewing a diff carefully',
      roles: ['member'],
      body: 'Read the diff.',
    });

    wrapper.unmount();
  });

  it('deletes a custom skill', async () => {
    const wrapper = await mountSkills();

    row('code-review').click();
    await settle();
    button('Delete').click();
    await settle();

    expect(deleteSkill).toHaveBeenCalledWith('code-review');

    wrapper.unmount();
  });
});

describe('SkillsDialog editor, mounted', () => {
  it('disables Save while the form is empty', async () => {
    const wrapper = await openCreate();

    expect(isDisabled('Save')).toBe(true);

    wrapper.unmount();
  });

  it('creates a Custom skill for members unless told otherwise', async () => {
    const wrapper = await openCreate();

    await type('Name', 'release-notes');
    await type('Description', 'Writing release notes');
    expect(isDisabled('Save')).toBe(false);

    button('Save').click();
    await settle();

    expect(createSkill).toHaveBeenCalledWith({
      name: 'release-notes',
      description: 'Writing release notes',
      roles: ['member'],
      body: '',
    });

    wrapper.unmount();
  });

  it('refuses a name the skill store refuses, under the field', async () => {
    const wrapper = await openCreate();

    await type('Name', 'Code_Review');
    await blur('Name');
    await type('Description', 'Reviewing a diff');

    expect(hasError('Name')).toBe(true);
    expect(bodyText()).toContain('Skill names use lowercase letters');
    expect(isDisabled('Save')).toBe(true);

    wrapper.unmount();
  });

  it("shows the server's sentence and marks the name when it answers 409", async () => {
    createSkill.mockRejectedValue(
      Object.assign(new Error("'worktrees' is a built-in skill; choose another name."), { status: 409 }),
    );
    const wrapper = await openCreate();

    await type('Name', 'worktrees');
    await type('Description', 'Mine');
    button('Save').click();
    await settle();

    expect(createSkill).toHaveBeenCalledTimes(1);
    expect(hasError('Name')).toBe(true);
    expect(document.body.querySelector('.q-banner')?.textContent).toContain('is a built-in skill');

    wrapper.unmount();
  });
});
