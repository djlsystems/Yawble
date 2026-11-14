// @vitest-environment happy-dom
// B020 (card 93): a members/<slug> branch that blocks the team's members/<slug>/<member> branches, as git.status lists it
// in blockingBranches (core card 92), is shown in its own section with Delete through the existing two-step delete.
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

const team = { id: 3, name: 'docs', repoPath: null, members: 2, running: 0, queued: 0 };
const base: TeamGitStatus = {
  repoPath: 'C:\\work\\service', managed: false, baseBranch: 'main', checkedOut: 'main', dirty: false, origin: 'https://github.com/x/service.git',
  baseSha: 'aaaaaaaaaaaa', originBaseSha: 'aaaaaaaaaaaa', baseAheadOfOrigin: 0, baseBehindOrigin: 0,
  teamBranch: 'team/docs', teamSha: 'bbbbbbbbbbbb', teamAheadOfBase: 0, teamBehindBase: 0,
  merge: 'up-to-date', conflictFiles: [], members: [],
};

describe('git dialog: blocking members/<slug> branches', () => {
  it('shows nothing when git.status has no blockingBranches, or an empty list', async () => {
    for (const status of [base, { ...base, blockingBranches: [] }]) {
      const { api } = fakeApi({ 'git.status': () => status });
      const w = mount(GitDialog, { props: { api, team } });
      await flushPromises();
      expect(w.find('[data-test=git-merge-state]').exists()).toBe(true);
      expect(w.find('[data-test=git-blocking-branch]').exists()).toBe(false);
    }
  });

  it('shows one section per blocking branch with a plain line and its repository, local and remote refs joined', async () => {
    const { api } = fakeApi({
      'git.status': () => ({ ...base, blockingBranches: [
        { name: 'members/docs', repo: 'service', location: 'local' },
        { name: 'members/docs', repo: 'service', location: 'remote' },
      ] }),
    });
    const w = mount(GitDialog, { props: { api, team } });
    await flushPromises();
    const sections = w.findAll('[data-test=git-blocking-branch]');
    expect(sections).toHaveLength(1);
    expect(sections[0].text()).toContain("members/docs blocks this team's member branches");
    expect(sections[0].text()).toContain('members/docs/…');
    expect(sections[0].find('[data-test=git-blocking-repo]').text()).toBe('service');
    expect(sections[0].text()).toContain('here and on origin');
  });

  it('Delete plans through git.deleteBranches, ticks to name it in unmergedOk, and confirms with the token', async () => {
    let blocking = true;
    const { api, calls } = fakeApi({
      'git.status': () => ({ ...base, blockingBranches: blocking ? [{ name: 'members/docs', repo: 'service', location: 'remote' }] : [] }),
      'git.deleteBranches': (p) => {
        if (p.confirm) { blocking = false; return { result: 'Deleted origin/members/docs.' }; }
        return p.unmergedOk ? { plan: 'Delete origin/members/docs (1 commit not merged, lost).', token: 'tok-2', empty: false }
          : { plan: 'origin/members/docs: KEPT, not merged into main', token: 'tok-1', empty: true };
      },
    });
    const w = mount(GitDialog, { props: { api, team } });
    await flushPromises();
    await w.find('[data-test="git-delete-blocking-members/docs"]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=git-delete-plan]').text()).toContain('KEPT');
    expect(w.find('[data-test=git-delete-yes]').exists()).toBe(false);
    expect(w.find('[data-test=git-delete-unmerged]').text()).toContain('even if the plan keeps it as not merged');
    await w.find('[data-test=git-delete-unmerged] input').setValue(true);
    await flushPromises();
    await w.find('[data-test=git-delete-yes]').trigger('click');
    await flushPromises();
    expect(calls.filter((c) => c.method === 'git.deleteBranches').map((c) => c.params)).toEqual([
      { teamId: 3, branches: ['members/docs'], remote: true },
      { teamId: 3, branches: ['members/docs'], remote: true, unmergedOk: ['members/docs'] },
      { teamId: 3, branches: ['members/docs'], remote: true, unmergedOk: ['members/docs'], confirm: 'tok-2' },
    ]);
    expect(w.find('[data-test=git-result]').text()).toBe('Deleted origin/members/docs.');
    expect(w.find('[data-test=git-blocking-branch]').exists()).toBe(false); // reloaded
  });

  it('a local-only blocking branch is deleted here only (remote false)', async () => {
    const { api, calls } = fakeApi({
      'git.status': () => ({ ...base, blockingBranches: [{ name: 'members/docs', repo: 'service', location: 'local' }] }),
      'git.deleteBranches': () => ({ plan: 'Delete members/docs.', token: 'tok', empty: false }),
    });
    const w = mount(GitDialog, { props: { api, team } });
    await flushPromises();
    expect(w.find('[data-test=git-blocking-branch]').text()).toContain('· here');
    await w.find('[data-test="git-delete-blocking-members/docs"]').trigger('click');
    await flushPromises();
    expect(calls.find((c) => c.method === 'git.deleteBranches')!.params).toEqual({ teamId: 3, branches: ['members/docs'], remote: false });
  });
});
