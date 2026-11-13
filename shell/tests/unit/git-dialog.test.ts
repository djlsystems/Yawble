// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import GitDialog from '../../src/renderer/src/components/GitDialog.vue';
import type { YawbleApi, TeamGitStatus } from '../../src/shared/api';

function fakeApi(answers: Record<string, (params: any) => unknown>) {
  const calls: { method: string; params: any }[] = [];
  const api: YawbleApi = {
    getInfo: async () => { throw new Error('no'); },
    getAvailableBackends: async () => [],
    call: async (method: string, params?: Record<string, unknown>) => {
      calls.push({ method, params });
      const answer = answers[method];
      if (!answer) throw new Error(`unexpected ${method}`);
      return answer(params) as any;
    },
    onEvent: () => () => {},
    openTerminal: async () => {},
    pickFolder: async () => null,
  };
  return { api, calls };
}

const team = { id: 3, name: 'ops', repoPath: null, members: 2, running: 0, queued: 0 };
const base: TeamGitStatus = {
  repoPath: 'C:\work\service', managed: false, baseBranch: 'main', checkedOut: 'main', dirty: false, origin: 'https://github.com/x/service.git',
  baseSha: 'aaaaaaaaaaaa', originBaseSha: 'aaaaaaaaaaaa', baseAheadOfOrigin: 0, baseBehindOrigin: 0,
  teamBranch: 'team/ops', teamSha: 'bbbbbbbbbbbb', teamAheadOfBase: 2, teamBehindBase: 0,
  merge: 'fast-forward', conflictFiles: [], members: [{ branch: 'members/ada', sha: 'cccccccccccc', aheadOfBase: 1, inTeamBranch: true }],
};

describe('git dialog', () => {
  it('shows the team branch against main and merges only after a confirmation', async () => {
    const { api, calls } = fakeApi({
      'git.status': () => base,
      'git.merge': () => ({ outcome: 'fast-forward', message: 'team/ops fast-forwarded into main and origin\'s main: now bbbbbbbbbb.', changed: true, sha: 'bbbbbbbbbbbb', files: null,
        status: { ...base, merge: 'up-to-date', baseSha: 'bbbbbbbbbbbb' } }),
    });
    const w = mount(GitDialog, { props: { api, team } });
    await flushPromises();
    expect(w.find('[data-test=git-merge-state]').text()).toContain('main will fast-forward');
    expect(w.find('[data-test=git-members]').text()).toContain('members/ada');
    await w.find('[data-test=git-merge]').trigger('click');
    expect(calls.some((c) => c.method === 'git.merge')).toBe(false);
    expect(w.find('[data-test=git-merge-confirm]').text()).toContain('push main to origin');
    await w.find('[data-test=git-merge-yes]').trigger('click');
    await flushPromises();
    expect(calls.find((c) => c.method === 'git.merge')!.params).toEqual({ teamId: 3 });
    expect(w.find('[data-test=git-result]').text()).toContain('fast-forwarded');
    expect(w.find('[data-test=git-merge]').exists()).toBe(false); // now up to date
  });

  it('shows how far the team branch is behind the base, and Sync now brings it up and reloads', async () => {
    let behind = 3;
    const { api, calls } = fakeApi({
      'git.status': () => ({ ...base, teamBehindBase: behind }),
      'git.sync': () => { behind = 0; return { outcome: 'synced', baseBranch: 'main', baseSha: 'aaaaaaaaaaaa', behind: 0, moved: ['team/ops'], conflicts: [], note: null }; },
    });
    const w = mount(GitDialog, { props: { api, team } });
    await flushPromises();
    expect(w.find('[data-test=git-behind]').text()).toContain('team/ops is 3 commits behind main');
    await w.find('[data-test=git-sync-now]').trigger('click');
    await flushPromises();
    expect(calls.find((c) => c.method === 'git.sync')!.params).toEqual({ teamId: 3 });
    expect(w.find('[data-test=git-result]').text()).toContain('Brought the branches up to main (team/ops)');
    expect(w.find('[data-test=git-behind]').exists()).toBe(false); // reloaded: in step now
  });

  it('a Sync now that conflicts says so with the files, and the behind-count stays', async () => {
    const { api } = fakeApi({
      'git.status': () => ({ ...base, teamBehindBase: 1 }),
      'git.sync': () => ({ outcome: 'conflict', baseBranch: 'main', baseSha: 'aaaaaaaaaaaa', behind: 1, moved: [], conflicts: ['README.md'],
        note: 'team/ops is 1 commit behind main and could not be synced automatically (conflicts in: README.md). Merge main into it first and resolve the conflicts before other work.' }),
    });
    const w = mount(GitDialog, { props: { api, team } });
    await flushPromises();
    expect(w.find('[data-test=git-behind]').text()).toContain('team/ops is 1 commit behind main'); // singular
    await w.find('[data-test=git-sync-now]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=git-result]').text()).toContain('could not be synced automatically');
    expect(w.find('[data-test=git-result]').text()).toContain('README.md');
    expect(w.find('[data-test=git-behind]').exists()).toBe(true);
  });

  it('shows no behind line, and no Sync now, for a branch that is in step', async () => {
    const { api } = fakeApi({ 'git.status': () => base });
    const w = mount(GitDialog, { props: { api, team } });
    await flushPromises();
    expect(w.find('[data-test=git-behind]').exists()).toBe(false);
    expect(w.find('[data-test=git-sync-now]').exists()).toBe(false);
  });

  it('a conflict offers Ask the team, not Merge, and names the files', async () => {
    const { api, calls } = fakeApi({
      'git.status': () => ({ ...base, merge: 'conflict', teamBehindBase: 1, conflictFiles: ['README.md'] }),
      'git.askTeam': () => ({ seq: 40, workflow: 40 }),
    });
    const w = mount(GitDialog, { props: { api, team } });
    await flushPromises();
    expect(w.find('[data-test=git-merge]').exists()).toBe(false);
    expect(w.find('[data-test=git-merge-state]').text()).toContain('README.md');
    // A conflict is shown as a conflict, never in the success colour (the merge state is its own class).
    expect(w.get('[data-test=git-merge-state]').classes()).toEqual(['merge', 'state-conflict']);
    await w.find('[data-test=git-ask-team]').trigger('click');
    await flushPromises();
    expect(calls.some((c) => c.method === 'git.askTeam')).toBe(true);
    expect(w.find('[data-test=git-result]').text()).toContain('Told the Manager');
  });
});
