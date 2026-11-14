// @vitest-environment happy-dom
// B016 gap test (card 77): the Git dialog fed git.status as the core really sends it.
import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import GitDialog from '../../src/renderer/src/components/GitDialog.vue';
import type { YawbleApi, TeamGitStatus } from '../../src/shared/api';

function fakeApi(status: TeamGitStatus) {
  const api: YawbleApi = {
    getInfo: async () => { throw new Error('no'); },
    getAvailableBackends: async () => [],
    call: async (method: string) => {
      if (method === 'git.status') return status as any;
      throw new Error(`unexpected ${method}`);
    },
    onEvent: () => () => {},
    openTerminal: async () => {},
    pickFolder: async () => null,
  };
  return api;
}

const team = { id: 3, name: 'ops', repoPath: null, members: 2, running: 0, queued: 0 };

describe('git dialog (B016 gaps)', () => {
  // Card 75: git.status lists a LOCAL old-style members/<name> branch once. In members only while a member is still
  // recorded on it (not moved yet); otherwise in oldBranches only (TeamBranchGapTests.GitStatus_ListsAnOldStyleBranchOnce_...).
  // This is Ada after the move: members/ops/ada is Ada's, members/ada is old. It was shown twice before (card 77).
  it('a local old-style branch nobody is on, which git.status lists only in oldBranches, is shown once, as old', async () => {
    const status: TeamGitStatus = {
      repoPath: 'C:\\work\\service', managed: false, baseBranch: 'main', checkedOut: 'main', dirty: false, origin: null,
      baseSha: 'aaaaaaaaaaaa', originBaseSha: null, baseAheadOfOrigin: null, baseBehindOrigin: null,
      teamBranch: 'team/ops', teamSha: 'bbbbbbbbbbbb', teamAheadOfBase: 0, teamBehindBase: 0,
      merge: 'up-to-date', conflictFiles: [],
      members: [
        { branch: 'members/ops/ada', sha: 'cccccccccccc', aheadOfBase: 1, inTeamBranch: false },
      ],
      oldBranches: [{ name: 'members/ada', local: true, remote: false, merged: false }],
    };
    const w = mount(GitDialog, { props: { api: fakeApi(status), team } });
    await flushPromises();
    const names = w.findAll('[data-test=git-members] tr').slice(1).map((r) => r.find('code').text());
    expect(names.filter((n) => n === 'members/ada')).toHaveLength(1);
    expect(w.find('[data-test=git-old-branch] code').text()).toBe('members/ada');
    expect(names).toContain('members/ops/ada');
  });
});
