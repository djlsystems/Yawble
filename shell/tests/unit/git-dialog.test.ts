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

  it('lists a nested members/<team>/<member> branch whole beside an old members/<member> one, and plans then confirms a delete of each', async () => {
    let branches = ['members/ops/ada', 'members/ada'];
    const { api, calls } = fakeApi({
      'git.status': () => ({ ...base,
        members: branches.includes('members/ops/ada') ? [{ branch: 'members/ops/ada', sha: 'dddddddddddd', aheadOfBase: 0, inTeamBranch: true }] : [],
        oldBranches: branches.includes('members/ada') ? [{ name: 'members/ada', local: false, remote: true, merged: true }] : [] }),
      'git.deleteBranches': (p) => {
        if (!p.confirm) return { plan: `Delete ${p.branches[0]}${p.remote ? ' here and on origin' : ''}.`, token: `tok-${p.branches[0]}`, empty: false };
        expect(p.confirm).toBe(`tok-${p.branches[0]}`);
        branches = branches.filter((b) => b !== p.branches[0]);
        return { result: `Deleted ${p.branches[0]}.` };
      },
    });
    const w = mount(GitDialog, { props: { api, team } });
    await flushPromises();
    const rows = () => w.findAll('[data-test=git-members] tr').slice(1).map((r) => r.findAll('td').map((c) => c.text()));
    expect(rows()).toEqual([
      ['members/ops/ada', 'dddddddddd', '0', 'yes', 'Delete…'],
      ['members/adaold', 'on origin', '—', 'merged', 'Delete…'],
    ]);
    expect(w.find('[data-test=git-old-branch] .chip').text()).toBe('old');

    // The new-style member branch: a plan first (nothing deleted yet), then Delete with its token.
    await w.find('[data-test="git-delete-members/ops/ada"]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=git-delete-plan]').text()).toBe('Delete members/ops/ada here and on origin.');
    expect(calls.filter((c) => c.method === 'git.deleteBranches').map((c) => c.params)).toEqual([
      { teamId: 3, branches: ['members/ops/ada'], remote: true },
    ]);
    await w.find('[data-test=git-delete-yes]').trigger('click');
    await flushPromises();
    expect(calls.filter((c) => c.method === 'git.deleteBranches').at(-1)!.params).toEqual(
      { teamId: 3, branches: ['members/ops/ada'], remote: true, confirm: 'tok-members/ops/ada' });
    expect(w.find('[data-test=git-result]').text()).toBe('Deleted members/ops/ada.');
    expect(w.find('[data-test=git-delete]').exists()).toBe(false);
    expect(rows().map((r) => r[0])).toEqual(['members/adaold']); // reloaded

    // The old one, only on origin: remote follows its remote flag.
    await w.find('[data-test="git-delete-old-members/ada"]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=git-delete-unmerged]').exists()).toBe(false); // merged: no choice to make
    await w.find('[data-test=git-delete-yes]').trigger('click');
    await flushPromises();
    expect(calls.filter((c) => c.method === 'git.deleteBranches').slice(-2).map((c) => c.params)).toEqual([
      { teamId: 3, branches: ['members/ada'], remote: true },
      { teamId: 3, branches: ['members/ada'], remote: true, confirm: 'tok-members/ada' },
    ]);
    expect(w.find('[data-test=git-result]').text()).toBe('Deleted members/ada.');
    expect(w.find('[data-test=git-old-branch]').exists()).toBe(false);
  });

  it('an unmerged old branch is deleted only after the explicit choice, which plans again with it in unmergedOk', async () => {
    const { api, calls } = fakeApi({
      'git.status': () => ({ ...base, oldBranches: [{ name: 'members/ada', local: true, remote: false, merged: false }] }),
      'git.deleteBranches': (p) => (p.confirm ? { result: 'Deleted members/ada.' }
        : p.unmergedOk ? { plan: 'Delete members/ada (2 commits not merged, lost).', token: 'tok-2', empty: false }
          : { plan: 'Keep members/ada: it has 2 commits not merged.', token: 'tok-1', empty: true }),
    });
    const w = mount(GitDialog, { props: { api, team } });
    await flushPromises();
    expect(w.find('[data-test=git-old-branch]').text()).toContain('here');
    expect(w.find('[data-test=git-old-branch]').text()).toContain('not merged');
    await w.find('[data-test="git-delete-old-members/ada"]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=git-delete-plan]').text()).toContain('Keep members/ada');
    expect(w.find('[data-test=git-delete-empty]').exists()).toBe(true);
    expect(w.find('[data-test=git-delete-yes]').exists()).toBe(false); // nothing to delete without the choice
    await w.find('[data-test=git-delete-unmerged] input').setValue(true);
    await flushPromises();
    expect(w.find('[data-test=git-delete-plan]').text()).toContain('lost');
    await w.find('[data-test=git-delete-yes]').trigger('click');
    await flushPromises();
    expect(calls.filter((c) => c.method === 'git.deleteBranches').map((c) => c.params)).toEqual([
      { teamId: 3, branches: ['members/ada'], remote: false },
      { teamId: 3, branches: ['members/ada'], remote: false, unmergedOk: ['members/ada'] },
      { teamId: 3, branches: ['members/ada'], remote: false, unmergedOk: ['members/ada'], confirm: 'tok-2' },
    ]);
  });

  it('a stale delete is shown and planned again; another error shows its own text', async () => {
    let n = 0;
    let fail: 'stale' | 'other' = 'stale';
    const { api, calls } = fakeApi({
      'git.status': () => ({ ...base, oldBranches: [{ name: 'members/ada', local: true, remote: true, merged: true }] }),
      'git.deleteBranches': (p) => {
        if (!p.confirm) return { plan: `Plan ${++n}.`, token: `tok-${n}`, empty: false };
        // The desktop's shape: Electron passes only the text, the code rides in the error's name.
        if (fail === 'stale') throw new Error("Error invoking remote method 'core:call': CoreRpcError[stale]: members/ada moved since the plan.");
        throw Object.assign(new Error('origin refused the push: permission denied'), { code: 'git_failed' });
      },
    });
    const w = mount(GitDialog, { props: { api, team } });
    await flushPromises();
    await w.find('[data-test="git-delete-old-members/ada"]').trigger('click');
    await flushPromises();
    await w.find('[data-test=git-delete-yes]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=git-delete-stale]').text()).toContain('members/ada moved since the plan.');
    expect(w.find('[data-test=git-delete-stale]').text()).not.toContain('CoreRpcError');
    expect(w.find('[data-test=git-delete-plan]').text()).toBe('Plan 2.'); // planned again
    fail = 'other';
    await w.find('[data-test=git-delete-yes]').trigger('click');
    await flushPromises();
    expect(calls.filter((c) => c.method === 'git.deleteBranches').at(-1)!.params.confirm).toBe('tok-2');
    expect(w.find('[data-test=git-delete-error]').text()).toBe('origin refused the push: permission denied');
    expect(w.find('[data-test=git-delete-plan]').text()).toBe('Plan 2.'); // not re-planned
    await w.find('[data-test=git-delete-cancel]').trigger('click');
    expect(w.find('[data-test=git-delete]').exists()).toBe(false);
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
