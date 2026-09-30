// @vitest-environment happy-dom
//
// RESET REPOSITORIES, MOUNTED. The box is off when the dialog opens; ticking it reads what would be
// removed and lists it BEFORE the person confirms - the ticked members' trees and branches, and the
// team branch only when every member is ticked; and the result names what was KEPT, with the
// reason, because a tree or branch that would have lost work is the person's to decide.
//
// The client is mocked: the preview is what the server would answer, and `resetTeam` answers the
// shape the route does. The board store's refresh is stubbed so no socket or fetch is made.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const { previewResetRepositories, resetTeam } = vi.hoisted(() => ({
  previewResetRepositories: vi.fn(),
  resetTeam: vi.fn(),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  previewResetRepositories,
  resetTeam,
}));

vi.mock('../../stores/console', () => ({
  useConsoleStore: () => ({ refresh: () => Promise.resolve() }),
}));

import { flushPromises } from '@vue/test-utils';
import ResetTeamDialog from '../ResetTeamDialog.vue';
import {
  asMemberId,
  asTeamId,
  type ContainerSnapshot,
  type RepositoryResetPreview,
  type Team,
  type TeamId,
  type TeamWasReset,
} from '../../api/types';
import { bodyText, mountDialog, resetBody } from '../../test/mountQuasar';

const container = (id: string): ContainerSnapshot => ({
  team: asTeamId('alpha'),
  name: id,
  agent: 'echo',
  state: 'Idle',
  queueDepth: 0,
  ceiling: 16,
  subscribes: [],
  currentCorrelation: null,
  sinceSeq: 0,
  id: asMemberId(id),
});

const team: Team = {
  id: 'alpha' as TeamId,
  name: 'Alpha',
  concierge: 'claude-interactive',
  memberAgents: null,
  additionalInstructions: null,
  root: null,
  containers: ['Manager', 'Digger'].map(container),
};

const preview: RepositoryResetPreview = {
  worktrees: [
    { repo: 'Widget', name: '/teams/alpha/repos/Widget/wt_Digger_c1', member: 'Digger' },
    { repo: 'Widget', name: '/teams/alpha/repos/Widget/wt_Manager_c2', member: 'Manager' },
  ],
  branches: [{ repo: 'Widget', name: 'digger/c1', member: 'Digger' }],
  teamBranch: 'team/alpha',
  defaultBranchNotKnown: [],
};

function box(label: string): HTMLElement {
  const found = [...document.body.querySelectorAll('.q-checkbox')].find(
    (candidate) => candidate.textContent?.trim() === label,
  );

  if (!found) throw new Error(`no "${label}" checkbox in the rendered dialog`);

  return found as HTMLElement;
}

function button(label: string): HTMLButtonElement {
  const found = [...document.body.querySelectorAll('button')].find(
    (candidate) => candidate.textContent?.trim() === label,
  );

  if (!found) throw new Error(`no ${label} button in the rendered dialog`);

  return found as HTMLButtonElement;
}

beforeEach(() => {
  previewResetRepositories.mockReset();
  previewResetRepositories.mockResolvedValue(preview);
  resetTeam.mockReset();
});

afterEach(resetBody);

describe('ResetTeamDialog, Reset repositories', () => {
  it('offers the box OFF, and reads nothing until it is ticked', async () => {
    const wrapper = await mountDialog(ResetTeamDialog, { team });

    expect(box('Reset repositories').getAttribute('aria-checked')).toBe('false');
    expect(previewResetRepositories).not.toHaveBeenCalled();
    expect(bodyText()).not.toContain('What is lost');

    wrapper.unmount();
  });

  it('lists what is lost once ticked: every ticked member\'s trees and branches, and the team branch with everyone ticked', async () => {
    const wrapper = await mountDialog(ResetTeamDialog, { team });

    box('Reset repositories').click();
    await flushPromises();

    expect(previewResetRepositories).toHaveBeenCalledWith('alpha');
    const text = bodyText();
    expect(text).toContain('Removes the ticked members\' worktrees and branches');
    expect(text).toContain('A branch is deleted when every commit on it is on a remote, merged or not.');
    expect(text).toContain('What is lost');
    expect(text).toContain('wt_Digger_c1');
    expect(text).toContain('wt_Manager_c2');
    expect(text).toContain('digger/c1');
    expect(text).toContain('The team branch team/alpha');

    // Unticking a member drops its lines, and the team branch: not every member is ticked now.
    box('Digger').click();
    await flushPromises();

    expect(bodyText()).not.toContain('wt_Digger_c1');
    expect(bodyText()).not.toContain('digger/c1');
    expect(bodyText()).toContain('wt_Manager_c2');
    expect(bodyText()).not.toContain('The team branch team/alpha');

    wrapper.unmount();
  });

  it('sends resetRepositories and names what was kept, with the reason', async () => {
    const done: TeamWasReset = {
      team: 'alpha',
      floor: 10,
      floored: ['Manager', 'Digger'],
      purged: 0,
      retained: 0,
      cleared: [],
      failures: [],
      repositories: {
        worktreesRemoved: [{ repo: 'Widget', name: '/teams/alpha/repos/Widget/wt_Manager_c2', member: 'Manager' }],
        worktreesKept: [
          {
            repo: 'Widget',
            name: '/teams/alpha/repos/Widget/wt_Digger_c1',
            member: 'Digger',
            reason: 'git worktree remove refused it: contains modified or untracked files',
          },
        ],
        branchesDeleted: [],
        branchesKept: [
          { repo: 'Widget', name: 'digger/c1', member: 'Digger', reason: 'It holds 2 commit(s) that are on no remote, so it was not deleted.' },
        ],
        teamBranchReset: [{ repo: 'Widget', name: 'team/alpha', reason: 'Reset to trunk.' }],
        teamBranchKept: [],
      },
    };
    resetTeam.mockResolvedValue(done);

    const wrapper = await mountDialog(ResetTeamDialog, { team });

    box('Reset repositories').click();
    await flushPromises();
    button('Reset').click();
    await flushPromises();

    expect(resetTeam).toHaveBeenCalledWith('alpha', expect.objectContaining({ resetRepositories: true }));

    const text = bodyText();
    expect(text).toContain('Removed 1 worktree(s)');
    expect(text).toContain('Kept, because removing them would lose work');
    expect(text).toContain('wt_Digger_c1 (Widget): git worktree remove refused it');
    expect(text).toContain('digger/c1 (Widget): It holds 2 commit(s) that are on no remote');
    expect(text).toContain('team/alpha (Widget): Reset to trunk.');

    wrapper.unmount();
  });
});
