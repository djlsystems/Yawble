// @vitest-environment happy-dom
// Clickable counts and previews in the Git dialog, against the core's contract
// (git.commitRange, git.mergePreview, git.mergeDiff, git.deletePreview, git.status
// ranges / aheadRange / push, the commits / incoming of merge, bringCurrent and sync, CommitRow.notPushed). The words are
// tested pure (gitRange.ts, localMark.ts); the dialog mounted with a fake api.
import { flushPromises, mount } from '@vue/test-utils';
import { afterEach, describe, expect, it } from 'vitest';
import GitDialog from '../../src/renderer/src/components/GitDialog.vue';
import CommitView from '../../src/renderer/src/components/CommitView.vue';
import TidyPanel from '../../src/renderer/src/components/TidyPanel.vue';
import TeamSyncBanner from '../../src/renderer/src/components/TeamSyncBanner.vue';
import { closeCommit, commitTarget, whereLines } from '../../src/renderer/src/commitView';
import { MERGE_COMMIT, OWN_WORK, commitsInHeading, deletePreviewView, inBaseMark, mergePreviewView, moreText, rangeFor } from '../../src/renderer/src/gitRange';
const IN_MAIN = inBaseMark('main');
import { LOCAL_MARK, branchLocalOnly, commitLocalOnly, tidyLocalOnly } from '../../src/renderer/src/localMark';
import type { CommitFound, CommitRow, DeletePreview, MergePreview, TeamGitStatus, YawbleApi } from '../../src/shared/api';

function fakeApi(answers: Record<string, (params: any) => unknown>) {
  const calls: { method: string; params: any }[] = [];
  const api: YawbleApi = {
    getInfo: async () => { throw new Error('no'); },
    getAvailableBackends: async () => [],
    call: async (method: string, params?: Record<string, unknown>) => {
      calls.push({ method, params });
      const answer = answers[method];
      if (!answer) throw Object.assign(new Error(`fake: ${method} is not faked`), { code: 'method_not_found' });
      return answer(params) as any;
    },
    onEvent: () => () => {},
    openTerminal: async () => {},
    pickFolder: async () => null,
  };
  return { api, calls };
}
const of = (calls: { method: string; params: any }[], m: string) => calls.filter((c) => c.method === m);

const team = { id: 3, name: 'ops', repoPath: null, members: 2, running: 0, queued: 0 };
const A = 'a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1';
const B = 'b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2';
const C = 'c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3';
const D = 'd4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4';
const row = (sha: string, subject: string, notPushed = false): CommitRow =>
  ({ sha, shortSha: sha.slice(0, 12), subject, author: { name: 'DeveloperAda', email: 'ada@x' }, date: '2026-11-18T10:00:00+01:00', isMerge: false, notPushed });

const status: TeamGitStatus = {
  repoPath: 'C:/work/service', managed: false, baseBranch: 'main', checkedOut: 'main', dirty: false, origin: 'https://example.invalid/service.git',
  baseSha: A, originBaseSha: D, baseAheadOfOrigin: 2, baseBehindOrigin: 18,
  teamBranch: 'team/ops', teamSha: B, teamAheadOfBase: 11, teamBehindBase: 18,
  merge: 'merge', conflictFiles: [], members: [
    { branch: 'members/ops/ada', sha: C, aheadOfBase: 3, inTeamBranch: false, aheadRange: { from: D, to: C }, push: { branch: 'members/ops/ada', onOrigin: false, notPushed: 3 } },
    { branch: 'members/ops/ben', sha: A, aheadOfBase: 0, inTeamBranch: true, aheadRange: null, push: { branch: 'members/ops/ben', onOrigin: true, notPushed: 0 } },
    { branch: 'members/ops/cleo', sha: B, aheadOfBase: 1, inTeamBranch: true, aheadRange: { from: D, to: B }, push: { branch: 'members/ops/cleo', onOrigin: true, notPushed: 1 } },
  ],
  ranges: { baseAheadOfOrigin: { from: D, to: A }, baseBehindOrigin: { from: A, to: D }, teamAheadOfBase: { from: D, to: B }, teamBehindBase: { from: B, to: D } },
};
const older: TeamGitStatus = { ...status, ranges: undefined, members: status.members.map(({ aheadRange: _a, push: _p, ...m }) => m) };
const range = (p: any) => ({ from: p.from, to: p.to, fromSha: p.from, toSha: p.to, count: 2, truncated: false, hasOrigin: true, commits: [row(B, `on ${p.to}`, true), row(C, 'second')] });

const preview = (over: Partial<MergePreview> = {}): MergePreview => ({
  baseBranch: 'main', teamBranch: 'team/ops', outcome: 'merge', summary: 'A merge commit will be made: main has 18 commits team/ops lacks.', text: '',
  from: D, to: B, newSha: null, conflictFiles: [], commits: [row(B, 'Partial refunds'), row(C, 'Refund webhook')], count: 11, truncated: true,
  files: [{ path: 'src/refund.ts', status: 'modified', added: 4, deleted: 1, binary: false }, { path: 'img/logo.png', status: 'added', added: 0, deleted: 0, binary: true }],
  diffFrom: D, diffTo: B, hasOrigin: true, teamPush: { branch: 'team/ops', onOrigin: true, notPushed: 2 }, ...over,
});
const diff = { path: 'src/refund.ts', status: 'modified', binary: false, tooLarge: false, added: 1, deleted: 1,
  hunks: [{ header: '@@ -1,2 +1,2 @@', oldStart: 1, oldCount: 2, newStart: 1, newCount: 2, lines: [
    { kind: 'context', old: 1, new: 1, text: 'export function refund() {' }, { kind: 'deleted', old: 2, text: '  return 0;' }, { kind: 'added', new: 2, text: '  return 1;' }] }] };
const delPreview = (over: Partial<DeletePreview> = {}): DeletePreview => ({
  branch: 'members/ops/ada', baseBranch: 'main', exists: true, sha: C, merged: false, own: 1, inBase: 1, truncated: false,
  commits: [{ ...row(C, 'Half-done export', true), state: 'own' }, { ...row(A, 'Fix rounding'), state: 'in-base' }],
  lost: 'Deleting it loses 1 commit of its own (c3c3c3c3c3c3); it is not on origin either.', text: '', hasOrigin: true,
  push: { branch: 'members/ops/ada', onOrigin: false, notPushed: 1 }, ...over,
});

afterEach(() => closeCommit());

describe('gitRange words (pure)', () => {
  it('each count asks for the exact pair git.status gave', () => {
    expect(rangeFor(status, 'team-ahead')).toEqual({ from: D, to: B, title: '11 commits on team/ops not in main' });
    expect(rangeFor(status, 'team-behind')).toEqual({ from: B, to: D, title: '18 commits on main not in team/ops' });
    expect(rangeFor(status, 'member-ahead', 'members/ops/cleo')).toEqual({ from: D, to: B, title: '1 commit on members/ops/cleo not in main' });
    expect(rangeFor(status, 'member-ahead', 'members/ops/ben')).toBeNull(); // aheadRange null: no list
    expect(rangeFor(status, 'member-ahead', 'members/ops/nobody')).toBeNull();
    expect(rangeFor(status, 'base-behind-origin')).toEqual({ from: A, to: D, title: "18 commits on origin's main not in yours" });
    expect(rangeFor(status, 'base-ahead-of-origin')).toEqual({ from: D, to: A, title: '2 commits on your main not on origin' });
    expect(rangeFor({ ...status, ranges: { ...status.ranges!, teamAheadOfBase: null } }, 'team-ahead')).toBeNull();
  });

  it('an older core without ranges: the full ref names', () => {
    expect(rangeFor(older, 'team-ahead')).toMatchObject({ from: 'refs/heads/main', to: 'refs/heads/team/ops' });
    expect(rangeFor(older, 'member-ahead', 'members/ops/ada')).toMatchObject({ from: 'refs/heads/main', to: 'refs/heads/members/ops/ada' });
    expect(rangeFor(older, 'base-behind-origin')).toMatchObject({ from: 'refs/heads/main', to: 'refs/remotes/origin/main' });
    expect(moreText(2, 11)).toBe('and 9 more');
    expect(moreText(2, 2)).toBeNull();
    expect(commitsInHeading(1, 'main')).toBe('1 commit came into main:');
  });

  it('merge preview: fast-forward, merge commit, conflict and refused', () => {
    const ff = mergePreviewView(preview({ outcome: 'fast-forward', summary: 'main will fast-forward to b2b2b2b2b2b2.', count: 2 }));
    expect(ff).toMatchObject({ tone: 'ok', canMerge: true, sentence: 'main will fast-forward to b2b2b2b2b2b2.', commitsHeading: '2 commits will land on main', filesHeading: '2 files changed, all together' });
    const m = mergePreviewView(preview({ summary: '' }));
    expect(m).toMatchObject({ tone: 'ok', canMerge: true, sentence: 'A merge commit will join team/ops into main (no rebase).', commitsHeading: '11 commits will land on main' });
    const c = mergePreviewView(preview({ outcome: 'conflict', summary: 'team/ops would conflict in src/refund.ts: nothing changes.', conflictFiles: ['src/refund.ts'], files: [] }));
    // Nothing lands on a conflict: the core's wording.
    expect(c).toMatchObject({ tone: 'bad', canMerge: false, sentence: 'team/ops would conflict in src/refund.ts: nothing changes.', filesHeading: null,
      commitsHeading: '11 commits on team/ops not in main' });
    expect(mergePreviewView(preview({ outcome: 'conflict', baseBranch: 'trunk', count: 1 })).commitsHeading).toBe('1 commit on team/ops not in trunk');
    const r = mergePreviewView(preview({ outcome: 'refused', summary: 'Your main has 1 commit(s) that origin does not have.' }));
    expect(r).toMatchObject({ tone: 'bad', canMerge: false });
    expect(mergePreviewView(preview({ outcome: 'up-to-date', count: 0, commits: [] }))).toMatchObject({ canMerge: false, commitsHeading: null, tone: 'plain' });
  });

  it('delete preview: already in main, work of its own, a merge commit; the core\'s lost line', () => {
    const v = deletePreviewView(delPreview({ commits: [...delPreview().commits, { ...row(D, 'Merge main'), isMerge: true, state: 'merge' }] }));
    expect(v.rows.map((r) => r.mark)).toEqual([OWN_WORK, IN_MAIN, MERGE_COMMIT]);
    expect(v.rows.map((r) => r.own)).toEqual([true, false, false]);
    // The in-base mark names the base.
    expect(IN_MAIN).toBe('already in main');
    expect(deletePreviewView(delPreview({ baseBranch: 'trunk' })).rows.map((r) => r.mark)).toEqual([OWN_WORK, 'already in trunk']);
    expect(v).toMatchObject({ loses: true, summary: 'Deleting it loses 1 commit of its own (c3c3c3c3c3c3); it is not on origin either.' });
    // Without the core's line: the dialog's own words.
    expect(deletePreviewView(delPreview({ lost: '', own: 2 })).summary).toBe('Deleting it loses 2 commits of its own.');
    expect(deletePreviewView(delPreview({ lost: '', own: 0, commits: [delPreview().commits[1]] }))).toMatchObject({ loses: false, summary: 'Nothing is lost: each of its 1 commit not in main already has the same change there.' });
    expect(deletePreviewView(delPreview({ lost: '', own: 0, inBase: 0, commits: [] })).summary).toBe('Nothing is lost: every commit of members/ops/ada is in main.');
    expect(deletePreviewView(delPreview({ exists: false, lost: '' }))).toMatchObject({ rows: [], loses: false, summary: 'There is no local branch members/ops/ada.' });
  });

  it('one local-only mark: commit notPushed and branch push, only with an origin; the Commit view uses its long text', () => {
    expect(commitLocalOnly({ notPushed: true }, true)).toBe(true);
    expect(commitLocalOnly({ notPushed: true }, false)).toBe(false); // no origin: said once over the list instead
    expect(commitLocalOnly({ notPushed: false }, true)).toBe(false);
    expect(commitLocalOnly({}, true)).toBe(false); // an older core's row
    expect(branchLocalOnly({ branch: 'x', onOrigin: false, notPushed: 0 }, true)).toBe(true);
    expect(branchLocalOnly({ branch: 'x', onOrigin: true, notPushed: 2 }, true)).toBe(true);
    expect(branchLocalOnly({ branch: 'x', onOrigin: true, notPushed: 0 }, true)).toBe(false);
    expect(branchLocalOnly({ branch: 'x', onOrigin: false, notPushed: 3 }, false)).toBe(false);
    expect(branchLocalOnly(undefined, true)).toBe(false);
    expect(tidyLocalOnly(['local'], true)).toBe(true);
    expect(tidyLocalOnly(['local'], false)).toBe(false);
    expect(tidyLocalOnly(['local', 'origin'], true)).toBe(false);
    const c = { where: { inMain: false, branches: ['team/ops'], pushed: false, tags: [] } } as unknown as CommitFound;
    expect(whereLines(c).find((w) => w.key === 'unpushed')!.text).toBe(LOCAL_MARK.long);
    const gone = { where: { inMain: false, branches: [], pushed: false, tags: [], deletedFrom: 'members/ops/ada' } } as unknown as CommitFound;
    expect(whereLines(gone)[0].text).toBe('On no branch: it was on members/ops/ada, which was deleted');
  });
});

describe('Git dialog counts', () => {
  it('every count is a link that opens the list of those commits; each row opens the Commit view', async () => {
    const { api, calls } = fakeApi({ 'git.status': () => status, 'git.commitRange': range });
    const w = mount(GitDialog, { props: { api, team } });
    await flushPromises();
    expect(w.find('[data-test=git-merge-text]').text()).toBe('team/ops is 11 commits ahead and 18 behind: a merge commit will join them (no rebase).');

    const cases: [string, { from: string; to: string }][] = [
      ['git-count-team-ahead', { from: D, to: B }],
      ['git-count-team-behind', { from: B, to: D }],
      ['git-count-behind', { from: B, to: D }],
      ['git-count-member-members/ops/ada', { from: D, to: C }],
      ['git-count-base-behind-origin', { from: A, to: D }],
      ['git-count-base-ahead-of-origin', { from: D, to: A }],
    ];
    for (const [test, want] of cases) {
      await w.find(`[data-test="${test}"]`).trigger('click');
      await flushPromises();
      expect(of(calls, 'git.commitRange').at(-1)!.params, test).toEqual({ teamId: 3, ...want });
      const r = w.find('[data-test=git-range]');
      expect(r.attributes('data-to'), test).toBe(want.to);
      expect(r.findAll('[data-test=commit-list-row]')).toHaveLength(2);
    }
    expect(w.find('[data-test=git-count-behind]').text()).toBe('18 commits behind');
    expect(w.find('[data-test="git-count-member-members/ops/ben"]').exists()).toBe(false); // nothing ahead: no link

    // The same link again closes the list.
    await w.find('[data-test=git-count-base-ahead-of-origin]').trigger('click');
    expect(w.find('[data-test=git-range]').exists()).toBe(false);

    // A row opens the Commit view at that commit; the unpushed one carries the mark.
    await w.find('[data-test=git-count-team-ahead]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=git-range-title]').text()).toBe('11 commits on team/ops not in main');
    await w.find('[data-test=git-range] [data-test=commit-link]').trigger('click');
    expect(commitTarget.value).toEqual({ teams: ['ops'], sha: B });
    const rows = w.findAll('[data-test=git-range] [data-test=commit-list-row]');
    expect(rows[0].find('[data-test=local-mark]').text()).toBe(LOCAL_MARK.short);
    expect(rows[1].find('[data-test=local-mark]').exists()).toBe(false);
  });

  it('a repository without origin says once that its commits are local only; a refused range says why', async () => {
    let refuse = false;
    const { api } = fakeApi({
      'git.status': () => status,
      'git.commitRange': (p) => { if (refuse) throw new Error('bad range'); return { ...range(p), hasOrigin: false }; },
    });
    const w = mount(GitDialog, { props: { api, team } });
    await flushPromises();
    await w.find('[data-test=git-count-team-ahead]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=commit-list-no-origin]').text()).toBe(LOCAL_MARK.noOrigin);
    expect(w.find('[data-test=git-range] [data-test=local-mark]').exists()).toBe(false);
    refuse = true;
    await w.find('[data-test=git-count-team-behind]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=git-range-error]').text()).toBe('bad range');
  });

  it('the member branches carry the mark when origin lacks them or some of their commits', async () => {
    const { api } = fakeApi({ 'git.status': () => status });
    const w = mount(GitDialog, { props: { api, team } });
    await flushPromises();
    const rows = w.findAll('[data-test=git-member-row]');
    expect(rows.map((r) => r.find('[data-test=local-mark]').exists())).toEqual([true, false, true]);
    expect(rows[0].find('[data-test=local-mark]').attributes('title')).toContain('origin has no members/ops/ada');
    expect(rows[2].find('[data-test=local-mark]').attributes('title')).toContain('1 commit not on origin');
  });

  it('an older core (no ranges): the counts ask with full ref names', async () => {
    const { api, calls } = fakeApi({ 'git.status': () => older, 'git.commitRange': range });
    const w = mount(GitDialog, { props: { api, team } });
    await flushPromises();
    await w.find('[data-test=git-count-team-ahead]').trigger('click');
    await flushPromises();
    expect(of(calls, 'git.commitRange')[0].params).toEqual({ teamId: 3, from: 'refs/heads/main', to: 'refs/heads/team/ops' });
    expect(w.findAll('[data-test=git-member-row] [data-test=local-mark]')).toHaveLength(0);
  });
});

describe('Git dialog merge preview', () => {
  it('fast-forward: the outcome, the commits that land and the files with their combined diff, before Merge', async () => {
    const ff = { ...status, merge: 'fast-forward' as const, teamBehindBase: 0, teamAheadOfBase: 2 };
    const { api, calls } = fakeApi({
      'git.status': () => ff,
      'git.mergePreview': () => preview({ outcome: 'fast-forward', summary: 'main will fast-forward to b2b2b2b2b2b2.', count: 2, truncated: false, newSha: B }),
      'git.mergeDiff': () => diff,
      'git.merge': () => ({ outcome: 'fast-forward', message: 'Fast-forwarded.', changed: true, sha: B, files: null, status: { ...ff, merge: 'up-to-date', baseSha: B },
        commits: [row(B, 'Partial refunds'), row(C, 'Refund webhook')], commitCount: 2, commitsTruncated: false, range: { from: A, to: B } }),
    });
    const w = mount(GitDialog, { props: { api, team } });
    await flushPromises();
    expect(w.find('[data-test=git-merge-text]').text()).toBe('team/ops is 2 commits ahead: main will fast-forward to it.');
    await w.find('[data-test=git-merge]').trigger('click');
    await flushPromises();
    const p = w.find('[data-test=git-merge-preview]');
    expect(p.attributes('data-outcome')).toBe('fast-forward');
    expect(p.find('[data-test=git-merge-outcome]').text()).toBe('main will fast-forward to b2b2b2b2b2b2.');
    expect(p.find('[data-test=git-merge-team-push] [data-test=local-mark]').attributes('title')).toContain('2 commits not on origin');
    expect(p.find('[data-test=git-merge-commits-heading]').text()).toBe('2 commits will land on main');
    expect(p.findAll('[data-test=commit-list-row]')).toHaveLength(2);
    expect(p.find('[data-test=git-merge-files-heading]').text()).toBe('2 files changed, all together');
    const html = w.html();
    expect(html.indexOf('data-test="git-merge-preview"')).toBeLessThan(html.indexOf('data-test="git-merge-yes"'));

    await p.find('[data-test=git-merge-file-open]').trigger('click');
    await flushPromises();
    expect(of(calls, 'git.mergeDiff')[0].params).toEqual({ teamId: 3, from: D, to: B, path: 'src/refund.ts' });
    expect(w.find('[data-test=git-merge-preview] [data-test=diff-view]').exists()).toBe(true);

    await w.find('[data-test=git-merge-yes]').trigger('click');
    await flushPromises();
    expect(of(calls, 'git.merge')[0].params).toEqual({ teamId: 3 });
    expect(w.find('[data-test=git-merge-preview]').exists()).toBe(false);
    // What landed: the core's commits, no range read.
    expect(of(calls, 'git.commitRange')).toHaveLength(0);
    expect(w.find('[data-test=git-result-commits]').text()).toContain('2 commits came into main:');
  });

  it('merge commit: its summary and the more line when the core cut the list', async () => {
    const { api } = fakeApi({ 'git.status': () => status, 'git.mergePreview': () => preview() });
    const w = mount(GitDialog, { props: { api, team } });
    await flushPromises();
    await w.find('[data-test=git-merge]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=git-merge-outcome]').text()).toBe('A merge commit will be made: main has 18 commits team/ops lacks.');
    expect(w.find('[data-test=git-merge-preview] [data-test=commit-list-more]').text()).toBe('and 9 more');
    expect(w.find('[data-test=git-merge-yes]').attributes('disabled')).toBeUndefined();
  });

  it('conflict and refused: the reason and no Merge', async () => {
    let p = preview({ outcome: 'conflict', summary: 'team/ops would conflict in src/refund.ts: nothing changes.', conflictFiles: ['src/refund.ts'] });
    const { api } = fakeApi({ 'git.status': () => status, 'git.mergePreview': () => p });
    const w = mount(GitDialog, { props: { api, team } });
    await flushPromises();
    await w.find('[data-test=git-merge]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=git-merge-preview]').attributes('data-outcome')).toBe('conflict');
    expect(w.find('[data-test=git-merge-conflicts]').text()).toContain('src/refund.ts');
    expect(w.find('[data-test=git-merge-commits-heading]').text()).toBe('11 commits on team/ops not in main');
    expect(w.find('[data-test=git-merge-yes]').attributes('disabled')).toBeDefined();

    p = preview({ outcome: 'refused', summary: 'Your main has 1 commit(s) that origin does not have.' });
    await w.find('[data-test=git-merge-cancel]').trigger('click');
    await w.find('[data-test=git-merge]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=git-merge-outcome]').text()).toBe('Your main has 1 commit(s) that origin does not have.');
    expect(w.find('[data-test=git-merge-yes]').attributes('disabled')).toBeDefined();
  });

  it('an older core without git.mergePreview: a note, Merge offered, and what landed read with git.commitRange', async () => {
    const { api, calls } = fakeApi({
      'git.status': () => status, 'git.commitRange': range,
      'git.merge': () => ({ outcome: 'merge', message: 'Merged.', changed: true, sha: B, files: null, status: { ...status, baseSha: B } }),
    });
    const w = mount(GitDialog, { props: { api, team } });
    await flushPromises();
    await w.find('[data-test=git-merge]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=git-merge-preview-error]').exists()).toBe(true);
    await w.find('[data-test=git-merge-yes]').trigger('click');
    await flushPromises();
    expect(of(calls, 'git.merge')[0].params).toEqual({ teamId: 3 });
    expect(of(calls, 'git.commitRange')[0].params).toEqual({ teamId: 3, from: A, to: B });
    expect(w.find('[data-test=git-result-commits]').text()).toContain('2 commits came into main:');
  });
});

describe('Git dialog delete preview', () => {
  const del = (p: any) => (p.confirm ? { result: `Deleted ${p.branches.join(', ')}.` } : { plan: `Delete ${p.branches[0]} here and on origin.`, token: 't1', empty: false });

  it('each commit not in main is marked; the branch is marked not pushed; its last commit opens after the delete', async () => {
    const { api, calls } = fakeApi({ 'git.status': () => status, 'git.deleteBranches': del, 'git.deletePreview': () => delPreview() });
    const w = mount(GitDialog, { props: { api, team } });
    await flushPromises();
    await w.find('[data-test="git-delete-members/ops/ada"]').trigger('click');
    await flushPromises();
    expect(of(calls, 'git.deletePreview')[0].params).toEqual({ teamId: 3, branch: 'members/ops/ada' });
    const box = w.find('[data-test=git-delete]');
    expect(box.find('div [data-test=local-mark]').exists()).toBe(true);
    const p = box.find('[data-test=git-delete-preview]');
    expect(p.find('[data-test=git-delete-summary]').text()).toBe('Deleting it loses 1 commit of its own (c3c3c3c3c3c3); it is not on origin either.');
    expect(p.findAll('[data-test=commit-list-mark]').map((m) => m.text())).toEqual([OWN_WORK, IN_MAIN]);
    expect(p.findAll('[data-test=commit-list-row]')[0].find('[data-test=local-mark]').exists()).toBe(true);
    await w.find('[data-test=git-delete-yes]').trigger('click');
    await flushPromises();
    const rec = w.find('[data-test=git-result-recorded]');
    expect(rec.text()).toContain("members/ops/ada's last commit, kept on record:");
    await rec.find('[data-test=commit-link]').trigger('click');
    expect(commitTarget.value).toEqual({ teams: ['ops'], sha: C });
  });

  it('only commits already in main: nothing is lost; an older core: the plan alone, no error shown', async () => {
    const safe = delPreview({ own: 0, commits: [delPreview().commits[1]], lost: 'Nothing is lost: each of its 1 commits not in main already has the same change there.' });
    const { api } = fakeApi({ 'git.status': () => status, 'git.deleteBranches': del, 'git.deletePreview': () => safe });
    const w = mount(GitDialog, { props: { api, team } });
    await flushPromises();
    await w.find('[data-test="git-delete-members/ops/ada"]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=git-delete-summary]').classes()).not.toContain('loses');
    expect(w.find('[data-test=git-delete-preview] [data-test=commit-list-mark]').text()).toBe(IN_MAIN);

    const old = fakeApi({ 'git.status': () => status, 'git.deleteBranches': del });
    const w2 = mount(GitDialog, { props: { api: old.api, team } });
    await flushPromises();
    await w2.find('[data-test="git-delete-members/ops/ada"]').trigger('click');
    await flushPromises();
    expect(w2.find('[data-test=git-delete-plan]').exists()).toBe(true);
    expect(w2.find('[data-test=git-delete-preview]').exists()).toBe(false);
    expect(w2.find('[data-test=git-delete-preview-error]').exists()).toBe(false); // method_not_found: quiet
  });

  it('a base named trunk: the mark says "already in trunk"', async () => {
    const { api } = fakeApi({ 'git.status': () => ({ ...status, baseBranch: 'trunk' }), 'git.deleteBranches': del, 'git.deletePreview': () => delPreview({ baseBranch: 'trunk' }) });
    const w = mount(GitDialog, { props: { api, team } });
    await flushPromises();
    await w.find('[data-test="git-delete-members/ops/ada"]').trigger('click');
    await flushPromises();
    expect(w.findAll('[data-test=git-delete-preview] [data-test=commit-list-mark]').map((m) => m.text())).toEqual([OWN_WORK, 'already in trunk']);
  });
});

describe('Git dialog sync and bring current list what came in', () => {
  it('Sync now: the core\'s incoming, each clickable', async () => {
    let synced = false;
    const { api, calls } = fakeApi({
      'git.status': () => (synced ? { ...status, teamSha: C, teamBehindBase: 0 } : status),
      'git.sync': () => { synced = true; return { outcome: 'synced', baseBranch: 'main', baseSha: A, behind: 0, moved: ['team/ops'], conflicts: [], note: null,
        incoming: [row(A, 'From main')], incomingCount: 1, incomingTruncated: false, incomingRange: { from: B, to: A } }; },
    });
    const w = mount(GitDialog, { props: { api, team } });
    await flushPromises();
    await w.find('[data-test=git-sync-now]').trigger('click');
    await flushPromises();
    expect(of(calls, 'git.commitRange')).toHaveLength(0);
    const c = w.find('[data-test=git-result-commits]');
    expect(c.text()).toContain('1 commit came into team/ops:');
    await c.find('[data-test=commit-link]').trigger('click');
    expect(commitTarget.value?.sha).toBe(A);
  });

  it('Sync now on an older core: read with git.commitRange from the team commit before to the one after', async () => {
    let synced = false;
    const { api, calls } = fakeApi({
      'git.status': () => (synced ? { ...status, teamSha: C, teamBehindBase: 0 } : status),
      'git.sync': () => { synced = true; return { outcome: 'synced', baseBranch: 'main', baseSha: A, behind: 0, moved: ['team/ops'], conflicts: [], note: null }; },
      'git.commitRange': range,
    });
    const w = mount(GitDialog, { props: { api, team } });
    await flushPromises();
    await w.find('[data-test=git-sync-now]').trigger('click');
    await flushPromises();
    expect(of(calls, 'git.commitRange')[0].params).toEqual({ teamId: 3, from: B, to: C });
    expect(w.find('[data-test=git-result-commits]').text()).toContain('2 commits came into team/ops:');
  });

  it('Bring main current: the core\'s commits; an empty list shows nothing', async () => {
    let list: CommitRow[] = [row(C, 'From origin')];
    const { api, calls } = fakeApi({
      'git.status': () => status,
      'git.bringCurrent': () => ({ outcome: 'fast-forward', message: 'main fast-forwarded to origin/main.', changed: true, sha: C, files: null,
        status: { ...status, baseSha: C, baseBehindOrigin: 0 }, commits: list, commitCount: list.length, commitsTruncated: false, range: null }),
    });
    const w = mount(GitDialog, { props: { api, team } });
    await flushPromises();
    await w.find('[data-test=git-bring-current]').trigger('click');
    await flushPromises();
    expect(of(calls, 'git.commitRange')).toHaveLength(0);
    const c = w.find('[data-test=git-result-commits]');
    expect(c.text()).toContain('1 commit came into main:');
    expect(c.findAll('[data-test=commit-list-row]')).toHaveLength(1);
    list = [];
    await w.find('[data-test=git-bring-current]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=git-result-commits]').exists()).toBe(false);
    expect(of(calls, 'git.commitRange')).toHaveLength(0);
  });

  it('what came in after Sync, Merge and Bring current marks not-pushed rows with an origin, and none without', async () => {
    const marked = async (st: TeamGitStatus): Promise<number[]> => {
      const { api } = fakeApi({
        'git.status': () => st,
        'git.sync': () => ({ outcome: 'synced', baseBranch: 'main', baseSha: A, behind: 0, moved: ['team/ops'], conflicts: [], note: null,
          incoming: [row(A, 'From main', true), row(D, 'Pushed')], incomingCount: 2, incomingTruncated: false, incomingRange: { from: B, to: A } }),
        'git.mergePreview': () => preview({ commits: [row(B, 'Partial refunds', true)], count: 1, truncated: false }),
        'git.merge': () => ({ outcome: 'merge', message: 'Merged.', changed: true, sha: B, files: null, status: st,
          commits: [row(B, 'Partial refunds', true)], commitCount: 1, commitsTruncated: false, range: null }),
        'git.bringCurrent': () => ({ outcome: 'fast-forward', message: 'main fast-forwarded to origin/main.', changed: true, sha: C, files: null,
          status: st, commits: [row(C, 'From origin', true)], commitCount: 1, commitsTruncated: false, range: null }),
      });
      const w = mount(GitDialog, { props: { api, team } });
      await flushPromises();
      const count = () => w.findAll('[data-test=git-result-commits] [data-test=local-mark]').length;
      const out: number[] = [];
      await w.find('[data-test=git-sync-now]').trigger('click');
      await flushPromises();
      out.push(count());
      await w.find('[data-test=git-merge]').trigger('click');
      await flushPromises();
      await w.find('[data-test=git-merge-yes]').trigger('click');
      await flushPromises();
      out.push(count());
      if (st.origin) { // Bring current is offered only with an origin
        await w.find('[data-test=git-bring-current]').trigger('click');
        await flushPromises();
        out.push(count());
      }
      return out;
    };
    expect(await marked(status)).toEqual([1, 1, 1]);
    expect(await marked({ ...status, origin: null })).toEqual([0, 0]);
  });

  it('a sync that changed nothing lists nothing', async () => {
    const { api, calls } = fakeApi({
      'git.status': () => ({ ...status, teamBehindBase: 1 }),
      'git.sync': () => ({ outcome: 'conflict', baseBranch: 'main', baseSha: A, behind: 1, moved: [], conflicts: ['a.txt'], note: 'Conflicts.', incoming: [], incomingCount: 0, incomingTruncated: false, incomingRange: null }),
    });
    const w = mount(GitDialog, { props: { api, team } });
    await flushPromises();
    await w.find('[data-test=git-sync-now]').trigger('click');
    await flushPromises();
    expect(of(calls, 'git.commitRange')).toHaveLength(0);
    expect(w.find('[data-test=git-result-commits]').exists()).toBe(false);
  });
});

describe('the one not-pushed mark elsewhere', () => {
  it('the Commit view shows it in its where list', async () => {
    const found = { found: true, sha: B, shortSha: 'b2b2b2b', title: 'T', body: '', author: { name: 'Ada', email: 'a@x', date: '2026-11-18T10:00:00Z' }, isMerge: false, parents: [],
      where: { inMain: false, branches: ['team/ops'], pushed: false, tags: [] }, files: [], codeShown: true };
    const { api } = fakeApi({ 'git.commitShow': () => found });
    const w = mount(CommitView, { props: { api, teams: ['ops'], sha: B } });
    await flushPromises();
    expect(w.find('[data-test=commit-where-unpushed] [data-test=local-mark]').text()).toBe(LOCAL_MARK.long);
  });

  it('the Tidy panel marks a branch only here in a repository with an origin', async () => {
    const { api } = fakeApi({ 'git.tidyPlan': () => ({ repos: [
      { repo: 'svc', baseBranch: 'main', branches: [
        { name: 'feature/a', where: ['local'], class: 'own-work', commits: [{ sha: C, subject: 'Half done' }] },
        { name: 'members/ops/old', where: ['local', 'origin'], class: 'merged' }] },
      { repo: 'no-origin', baseBranch: 'main', branches: [{ name: 'feature/b', where: ['local'], class: 'own-work', commits: [] }] },
    ] }) });
    const w = mount(TidyPanel, { props: { api, team } });
    await flushPromises();
    expect(w.find('[data-test="tidy-row-feature/a"] .line [data-test=local-mark]').exists()).toBe(true);
    expect(w.find('[data-test="tidy-row-members/ops/old"] [data-test=local-mark]').exists()).toBe(false);
    expect(w.find('[data-test="tidy-row-feature/b"] [data-test=local-mark]').exists()).toBe(false);
  });
});

describe('the team page sync banner (teams.syncToBase behind.from / to, git.sync incoming)', () => {
  const sync = (behind: any) => () => ({ on: true, note: null, baseBranch: 'main', behind });
  it('its "behind" count opens git.commitRange with the exact pair, and closes again', async () => {
    const { api, calls } = fakeApi({
      'teams.syncToBase': sync({ teamBranch: 'team/ops', count: 2, text: '2 commits behind main', from: B, to: D }),
      'git.commitRange': range,
    });
    const w = mount(TeamSyncBanner, { props: { api, team, pollMs: 1e9 } });
    await flushPromises();
    await w.find('[data-test=team-behind-count]').trigger('click');
    await flushPromises();
    expect(of(calls, 'git.commitRange')[0].params).toEqual({ teamId: 3, from: B, to: D });
    expect(w.findAll('[data-test=team-behind-list] [data-test=commit-list-row]').map((r) => r.attributes('data-sha'))).toEqual([B, C]);
    await w.find('[data-test=team-behind-count]').trigger('click');
    expect(w.find('[data-test=team-behind-list]').exists()).toBe(false);
    w.unmount();
  });

  it('an older core (no from / to) keeps the count as text', async () => {
    const { api } = fakeApi({ 'teams.syncToBase': sync({ teamBranch: 'team/ops', count: 2, text: '2 commits behind main' }) });
    const w = mount(TeamSyncBanner, { props: { api, team, pollMs: 1e9 } });
    await flushPromises();
    expect(w.find('[data-test=team-behind-count]').exists()).toBe(false);
    expect(w.find('[data-test=team-behind]').text()).toContain('2 commits behind main');
    w.unmount();
  });

  it('Sync now lists the commits that came in, each opening the Commit view link', async () => {
    let synced = false;
    const { api } = fakeApi({
      'teams.syncToBase': () => sync(synced ? null : { teamBranch: 'team/ops', count: 1, text: '1 commit behind main', from: B, to: D })(),
      'git.sync': () => { synced = true; return { outcome: 'synced', baseBranch: 'main', baseSha: D, behind: 0, moved: ['team/ops'], conflicts: [], note: null,
        incoming: [row(D, 'Release')], incomingCount: 1, incomingTruncated: false, incomingRange: { from: B, to: D } }; },
    });
    const w = mount(TeamSyncBanner, { props: { api, team, pollMs: 1e9 } });
    await flushPromises();
    await w.find('[data-test=team-sync-now]').trigger('click');
    await flushPromises();
    const came = w.find('[data-test=team-sync-came-in]');
    expect(came.text()).toContain(commitsInHeading(1, 'team/ops'));
    expect(came.findAll('[data-test=commit-list-row]').map((r) => r.attributes('data-sha'))).toEqual([D]);
    w.unmount();
  });

  it('the commits that came in carry the not-pushed mark only in a repository with an origin (git.status origin)', async () => {
    const marked = async (st: (() => unknown) | null) => {
      const { api, calls } = fakeApi({
        'teams.syncToBase': sync({ teamBranch: 'team/ops', count: 2, text: '2 commits behind main', from: B, to: D }),
        'git.sync': () => ({ outcome: 'synced', baseBranch: 'main', baseSha: D, behind: 0, moved: ['team/ops'], conflicts: [], note: null,
          incoming: [row(D, 'Release', true), row(C, 'Pushed')], incomingCount: 2, incomingTruncated: false, incomingRange: { from: B, to: D } }),
        ...(st ? { 'git.status': st } : {}),
      });
      const w = mount(TeamSyncBanner, { props: { api, team, pollMs: 1e9 } });
      await flushPromises();
      await w.find('[data-test=team-sync-now]').trigger('click');
      await flushPromises();
      const came = w.find('[data-test=team-sync-came-in]');
      const rows = came.findAll('[data-test=commit-list-row]').map((r) => r.find('[data-test=local-mark]').exists());
      const status = of(calls, 'git.status').map((c) => c.params);
      w.unmount();
      return { rows, status };
    };
    // with an origin: the not-pushed commit is marked, the pushed one is not; status asked for this team.
    expect(await marked(() => status)).toEqual({ rows: [true, false], status: [{ teamId: 3 }] });
    // without one: nothing marked.
    expect((await marked(() => ({ ...status, origin: null }))).rows).toEqual([false, false]);
    // git.status unreadable (an older core, a failure): the list still shows, unmarked.
    expect((await marked(null)).rows).toEqual([false, false]);
  });
});
