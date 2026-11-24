// @vitest-environment happy-dom
// Card 466 (B073 verify, TesterKofi): the Git dialog fed the REAL core's answers. tests/unit/fixtures/git-previews/*.json were
// written by core Card466PreviewVerifyTests (GIT_PREVIEWS_FIXTURE_OUT set) from fixture repositories (base trunk), serialized as
// UiRpc sends them. Checks: the shapes are api.ts's; every count asks for the exact pair git.status gave and shows that
// many rows, each opening the Commit view; the merge and delete previews show the core's words; Merge is offered only when
// the core says it merges; Sync and Merge list what came in; the deleted branch's commit opens as "it was on X". Faults
// found are pinned as F-tests (see YAWBLE_TEAM_DOCS\B073\verify.md); flip them when fixed.
import { flushPromises, mount } from '@vue/test-utils';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';
import GitDialog from '../../src/renderer/src/components/GitDialog.vue';
import CommitView from '../../src/renderer/src/components/CommitView.vue';
import { closeCommit, commitTarget } from '../../src/renderer/src/commitView';
import { mergePreviewView } from '../../src/renderer/src/gitRange';
import { LOCAL_MARK } from '../../src/renderer/src/localMark';
import type { BranchSyncResult, CommitRange, CommitShow, DeletePreview, MergePreview, TeamGitResult, TeamGitStatus, YawbleApi } from '../../src/shared/api';

const real = <T = any>(name: string): T => JSON.parse(readFileSync(path.join(__dirname, 'fixtures', 'git-previews', `${name}.json`), 'utf8'));

function fakeApi(answers: Record<string, (params: any) => unknown>) {
  const calls: { method: string; params: any }[] = [];
  const api: YawbleApi = {
    getInfo: async () => { throw new Error('no'); },
    getAvailableBackends: async () => [],
    call: async (method: string, params?: Record<string, unknown>) => {
      calls.push({ method, params });
      const answer = answers[method];
      if (!answer) throw Object.assign(new Error(`fake: ${method} is not faked`), { code: 'method_not_found' });
      return structuredClone(answer(params)) as any;
    },
    onEvent: () => () => {},
    openTerminal: async () => {},
    pickFolder: async () => null,
  };
  return { api, calls };
}
const of = (calls: { method: string; params: any }[], m: string) => calls.filter((c) => c.method === m);
const team = { id: 7, name: 'Alpha', repoPath: null, members: 2, running: 0, queued: 0 };
const wrappers: { unmount: () => void }[] = [];
afterEach(() => { closeCommit(); while (wrappers.length) wrappers.pop()!.unmount(); });
async function dialog(answers: Record<string, (params: any) => unknown>) {
  const f = fakeApi(answers);
  const w = mount(GitDialog, { props: { api: f.api, team }, attachTo: document.body });
  wrappers.push(w);
  await flushPromises();
  return { w, ...f };
}

const keys = (o: object) => Object.keys(o).sort();
const ROW = ['author', 'date', 'isMerge', 'notPushed', 'sha', 'shortSha', 'subject'];

describe('card 466: the real core\'s shapes are api.ts\'s', () => {
  it('CommitRow, CommitRange, MergePreview, DeletePreview, status ranges/push, sync incoming, merge commits', () => {
    const range = real<CommitRange>('range-team-ahead');
    expect(keys(range)).toEqual(['commits', 'count', 'from', 'fromSha', 'hasOrigin', 'to', 'toSha', 'truncated']);
    expect(keys(range.commits[0])).toEqual(ROW);
    expect(keys(range.commits[0].author)).toEqual(['email', 'name']);
    expect(range.commits[0].date).toMatch(/^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d[+-]\d\d:\d\d$/);
    expect(range.commits[0].shortSha).toBe(range.commits[0].sha.slice(0, 12));

    for (const n of ['merge-ff', 'merge-commit', 'merge-conflict', 'merge-refused', 'merge-up-to-date', 'merge-none', 'f1-merge-dirty']) {
      const p = real<MergePreview>(n);
      expect(keys(p), n).toEqual(['baseBranch', 'commits', 'conflictFiles', 'count', 'diffFrom', 'diffTo', 'files', 'from', 'hasOrigin', 'newSha', 'outcome',
        'summary', 'teamBranch', 'teamPush', 'text', 'to', 'truncated']);
      for (const f of p.files) expect(['added', 'binary', 'deleted', 'path', 'status']).toEqual(expect.arrayContaining(keys(f).filter((k) => k !== 'oldPath')));
    }
    for (const n of ['delete-pushed', 'delete-half-pushed', 'delete-with-merge', 'delete-merged']) {
      const d = real<DeletePreview>(n);
      expect(keys(d), n).toEqual(['baseBranch', 'branch', 'commits', 'exists', 'hasOrigin', 'inBase', 'lost', 'merged', 'own', 'push', 'sha', 'text', 'truncated']);
      for (const c of d.commits) expect(keys(c)).toEqual([...ROW, 'state'].sort());
      expect(keys(d.push!)).toEqual(['branch', 'notPushed', 'onOrigin']);
    }
    const s = real<TeamGitStatus>('status-counts');
    expect(keys(s.ranges!)).toEqual(['baseAheadOfOrigin', 'baseBehindOrigin', 'teamAheadOfBase', 'teamBehindBase']);
    for (const m of s.members) expect(keys(m)).toEqual(['aheadOfBase', 'aheadRange', 'branch', 'inTeamBranch', 'push', 'sha']);
    const sync = real<BranchSyncResult>('sync-ff');
    expect(sync).toHaveProperty('incoming');
    expect(sync).toHaveProperty('incomingCount');
    expect(sync).toHaveProperty('incomingTruncated');
    expect(sync).toHaveProperty('incomingRange');
    const done = real<TeamGitResult>('merge-commit-done');
    expect(done).toHaveProperty('commits');
    expect(done).toHaveProperty('commitCount');
    expect(done).toHaveProperty('range');
    expect((real<CommitShow>('commit-deleted-tip') as any).where.deletedFrom).toBe('members/alpha/ben');
  });
});

describe('card 466: every count opens exactly its commits', () => {
  it('each count link sends git.status\'s pair; the list has as many rows as the count; a row opens the Commit view', async () => {
    const s = real<TeamGitStatus>('status-counts');
    const ben = real<CommitRange>('range-member-ben');
    const { w, calls } = await dialog({
      'git.status': () => s,
      // The real list for Ben's count; for the others, a list of the size the core counts with the asked pair.
      'git.commitRange': (p) => (p.to === ben.toSha && p.from === ben.fromSha ? ben : { ...ben, from: p.from, to: p.to, fromSha: p.from, toSha: p.to }),
    });
    const benRow = s.members.find((m) => m.branch === 'members/alpha/ben')!;
    const cases: [string, { from: string; to: string } | null | undefined][] = [
      ['git-count-team-ahead', s.ranges!.teamAheadOfBase],
      ['git-count-team-behind', s.ranges!.teamBehindBase],
      ['git-count-behind', s.ranges!.teamBehindBase],
      ['git-count-base-ahead-of-origin', s.ranges!.baseAheadOfOrigin],
      ['git-count-member-members/alpha/ada', s.members.find((m) => m.branch === 'members/alpha/ada')!.aheadRange],
      ['git-count-member-members/alpha/ben', benRow.aheadRange],
    ];
    for (const [test, want] of cases) {
      const link = w.find(`[data-test="${test}"]`);
      expect(link.exists(), test).toBe(true);
      await link.trigger('click');
      await flushPromises();
      expect(of(calls, 'git.commitRange').at(-1)!.params, test).toEqual({ teamId: 7, from: want!.from, to: want!.to });
    }
    // base is not behind origin's: no link.
    expect(w.find('[data-test=git-count-base-behind-origin]').exists()).toBe(false);

    // Ben's (the last one opened): two rows (his Ahead number), both not pushed (origin has no members/alpha/ben).
    const rows = w.findAll('[data-test=git-range] [data-test=commit-list-row]');
    expect(rows).toHaveLength(benRow.aheadOfBase);
    expect(rows.map((r) => r.attributes('data-sha'))).toEqual(ben.commits.map((c) => c.sha));
    expect(rows.map((r) => r.find('[data-test=local-mark]').exists())).toEqual(ben.commits.map((c) => c.notPushed));
    await rows[1].find('[data-test=commit-link]').trigger('click');
    expect(commitTarget.value).toEqual({ teams: ['Alpha'], sha: ben.commits[1].sha });
  });

  it('member branches: Ada (on origin, nothing unpushed) has no mark; Ben (not on origin) has it', async () => {
    const { w } = await dialog({ 'git.status': () => real('status-counts') });
    const rows = w.findAll('[data-test=git-member-row]');
    const mark = (b: string) => rows.find((r) => r.text().includes(b))!.find('[data-test=local-mark]');
    expect(mark('members/alpha/ada').exists()).toBe(false);
    expect(mark('members/alpha/ben').exists()).toBe(true);
    expect(mark('members/alpha/ben').attributes('title')).toContain('origin has no members/alpha/ben');
  });

  it('origin\'s base ahead of the local one: "yours is 1 behind" opens origin\'s commit, pushed (no mark)', async () => {
    const s = real<TeamGitStatus>('status-ff-origin');
    const behind = real<CommitRange>('range-base-behind-origin');
    const { w, calls } = await dialog({ 'git.status': () => s, 'git.commitRange': () => behind });
    await w.find('[data-test=git-count-base-behind-origin]').trigger('click');
    await flushPromises();
    expect(of(calls, 'git.commitRange')[0].params).toEqual({ teamId: 7, ...s.ranges!.baseBehindOrigin });
    const rows = w.findAll('[data-test=git-range] [data-test=commit-list-row]');
    expect(rows).toHaveLength(s.baseBehindOrigin!);
    expect(rows[0].find('[data-test=local-mark]').exists()).toBe(false);
  });
});

describe('card 466: the merge preview shows the core\'s words', () => {
  async function openPreview(p: MergePreview, status: TeamGitStatus = real('status-ff-origin')) {
    const d = await dialog({ 'git.status': () => status, 'git.mergePreview': () => p });
    await d.w.find('[data-test=git-merge]').trigger('click');
    await flushPromises();
    return d;
  }

  it('fast-forward from origin\'s base: the summary, 2 commits (one not pushed), the team branch marked, Merge offered', async () => {
    const p = real<MergePreview>('merge-ff');
    const { w } = await openPreview(p);
    const box = w.find('[data-test=git-merge-preview]');
    expect(box.find('[data-test=git-merge-outcome]').text()).toBe(p.summary);
    expect(box.find('[data-test=git-merge-commits-heading]').text()).toBe('2 commits will land on trunk');
    expect(box.findAll('[data-test=commit-list-row]').map((r) => r.find('[data-test=local-mark]').exists())).toEqual([true, false]);
    expect(box.find('[data-test=git-merge-team-push] [data-test=local-mark]').attributes('title')).toContain('1 commit not on origin');
    expect(w.find('[data-test=git-merge-yes]').attributes('disabled')).toBeUndefined();
    // The dialog's headline and the Concierge's text (the core's text) start with the same sentence.
    expect(p.text.split('\n')[0]).toBe(box.find('[data-test=git-merge-outcome]').text());
  });

  it('merge commit without origin: said once that every commit is local only, no mark per row; files with the combined diff', async () => {
    const p = real<MergePreview>('merge-commit');
    const { w, calls } = await openPreview(p, { ...real<TeamGitStatus>('status-ff-origin'), merge: 'merge' });
    const box = w.find('[data-test=git-merge-preview]');
    expect(box.find('[data-test=git-merge-outcome]').text()).toBe(p.summary);
    expect(box.find('[data-test=commit-list-no-origin]').text()).toBe(LOCAL_MARK.noOrigin);
    expect(box.findAll('[data-test=local-mark]')).toHaveLength(0);
    expect(box.findAll('[data-test=git-merge-file]').map((f) => f.attributes('data-path')).sort()).toEqual(['a.txt', 'team.txt']);
    await box.find('[data-test=git-merge-file-open]').trigger('click');
    await flushPromises();
    expect(of(calls, 'git.mergeDiff')[0]?.params).toMatchObject({ teamId: 7, from: p.diffFrom, to: p.diffTo });
    expect(w.find('[data-test=git-merge-yes]').attributes('disabled')).toBeUndefined();
  });

  it('refused, up-to-date and none: the core\'s sentence and no Merge', async () => {
    for (const n of ['merge-refused', 'merge-up-to-date', 'merge-none']) {
      const p = real<MergePreview>(n);
      const { w } = await openPreview(p);
      expect(w.find('[data-test=git-merge-outcome]').text(), n).toBe(p.summary);
      expect(w.find('[data-test=git-merge-yes]').attributes('disabled'), n).toBeDefined();
      w.unmount();
      wrappers.pop();
    }
  });

  it('conflict (the branches moved after git.status): the files, no Merge', async () => {
    const p = real<MergePreview>('merge-conflict');
    const { w } = await openPreview(p, { ...real<TeamGitStatus>('status-ff-origin'), merge: 'merge' });
    expect(w.find('[data-test=git-merge-outcome]').text()).toBe(p.summary);
    expect(w.find('[data-test=git-merge-conflicts]').text()).toContain('a.txt');
    expect(w.find('[data-test=git-merge-conflicts]').text()).toContain('README.md');
    expect(w.find('[data-test=git-merge-yes]').attributes('disabled')).toBeDefined();
  });

  it('F2 (fixed, Ines efac07d): on a conflict the dialog says the commits are "on team/alpha not in trunk", as the core and the Concierge do', async () => {
    const p = real<MergePreview>('merge-conflict');
    expect(p.text).toContain('1 commit on team/alpha not in trunk:');
    const { w } = await openPreview(p, { ...real<TeamGitStatus>('status-ff-origin'), merge: 'merge' });
    expect(w.find('[data-test=git-merge-commits-heading]').text()).toBe('1 commit on team/alpha not in trunk');
    expect(mergePreviewView(p).commitsHeading).toBe('1 commit on team/alpha not in trunk');
    expect(w.text()).not.toContain('will land');
  });

  it('F1 (fixed, Tomas 9214afe), seen in the dialog: base checked out with uncommitted changes, the core\'s refusal and no Merge', async () => {
    const p = real<MergePreview>('f1-merge-dirty');
    expect(p.outcome).toBe('refused');
    expect(p.summary).toMatch(/^trunk is checked out in .* with uncommitted changes\./);
    const { w } = await openPreview(p, { ...real<TeamGitStatus>('status-ff-origin'), dirty: true, checkedOut: 'trunk' });
    expect(w.find('[data-test=git-merge-preview]').attributes('data-outcome')).toBe('refused');
    expect(w.find('[data-test=git-merge-outcome]').text()).toBe(p.summary);
    expect(w.find('[data-test=git-merge-yes]').attributes('disabled')).toBeDefined();
  });

  it('Merge: what landed is the core\'s list, the same commits the preview showed', async () => {
    const p = real<MergePreview>('merge-commit');
    const done = real<TeamGitResult>('merge-commit-done');
    const { w, calls } = await dialog({
      'git.status': () => ({ ...real<TeamGitStatus>('status-ff-origin'), merge: 'merge' }), 'git.mergePreview': () => p, 'git.merge': () => done,
    });
    await w.find('[data-test=git-merge]').trigger('click');
    await flushPromises();
    await w.find('[data-test=git-merge-yes]').trigger('click');
    await flushPromises();
    expect(of(calls, 'git.commitRange')).toHaveLength(0);
    const c = w.find('[data-test=git-result-commits]');
    expect(c.text()).toContain('2 commits came into trunk:');
    expect(c.findAll('[data-test=commit-list-row]').map((r) => r.attributes('data-sha'))).toEqual(p.commits.map((x) => x.sha));
  });
});

describe('card 466: the delete preview shows the core\'s words', () => {
  const del = (p: any) => (p.confirm ? { result: `deleted ${p.branches.join(', ')}` } : { plan: `Delete ${p.branches[0]}.`, token: 't1', empty: false });

  async function openDelete(preview: DeletePreview, branch = 'members/alpha/ben') {
    const s = real<TeamGitStatus>('status-counts');
    const d = await dialog({ 'git.status': () => s, 'git.deleteBranches': del, 'git.deletePreview': () => preview, 'git.commitShow': () => real('commit-deleted-tip') });
    await d.w.find(`[data-test="git-delete-${branch}"]`).trigger('click');
    await flushPromises();
    return d;
  }

  it('a branch with a merge, a picked commit and own work: the core\'s lost line, each row marked, all not pushed', async () => {
    const p = real<DeletePreview>('delete-with-merge');
    const { w } = await openDelete(p);
    const box = w.find('[data-test=git-delete-preview]');
    expect(box.find('[data-test=git-delete-summary]').text()).toBe(p.lost);
    expect(box.findAll('[data-test=commit-list-row]').map((r) => r.attributes('data-sha'))).toEqual(p.commits.map((c) => c.sha));
    expect(box.findAll('[data-test=commit-list-mark]').map((m) => m.text())).toEqual(['a merge commit', 'already in trunk', 'work of its own', 'work of its own']);
    expect(box.findAll('[data-test=commit-list-row]').every((r) => r.find('[data-test=local-mark]').exists())).toBe(true);
  });

  it('F3 (fixed, Ines efac07d): the base is trunk, and the dialog marks a picked commit "already in trunk", as the core and the Concierge do', async () => {
    const p = real<DeletePreview>('delete-with-merge');
    expect(p.baseBranch).toBe('trunk');
    expect(p.text).toContain(': already in trunk, not pushed');
    const { w } = await openDelete(p);
    const marks = w.findAll('[data-test=git-delete-preview] [data-test=commit-list-mark]').map((m) => m.text());
    expect(marks).toContain('already in trunk');
    expect(marks).not.toContain('already in main');
  });

  it('a branch origin still has: no mark on the rows or the branch, the core says origin still has them', async () => {
    const p = real<DeletePreview>('delete-pushed');
    const { w } = await openDelete(p, 'members/alpha/ben');
    const box = w.find('[data-test=git-delete-preview]');
    expect(box.find('[data-test=git-delete-summary]').text()).toBe(p.lost);
    expect(p.lost).toContain('origin still has them');
    expect(box.findAll('[data-test=local-mark]')).toHaveLength(0);
  });

  it('after the delete its last commit opens, and the Commit view says the branch was deleted', async () => {
    const p = real<DeletePreview>('delete-with-merge');
    const { w } = await openDelete(p);
    await w.find('[data-test=git-delete-yes]').trigger('click');
    await flushPromises();
    const rec = w.find('[data-test=git-result-recorded]');
    expect(rec.text()).toContain("members/alpha/ben's last commit, kept on record:");
    await rec.find('[data-test=commit-link]').trigger('click');
    expect(commitTarget.value).toEqual({ teams: ['Alpha'], sha: p.sha });

    const shown = real<CommitShow>('commit-deleted-tip');
    const { api } = fakeApi({ 'git.commitShow': () => shown });
    const v = mount(CommitView, { props: { api, teams: ['Alpha'], sha: p.sha! }, attachTo: document.body });
    wrappers.push(v);
    await flushPromises();
    const where = v.findAll('[data-test=commit-where] li').map((l) => l.text());
    expect(where).toContain('On no branch: it was on members/alpha/ben, which was deleted');
    expect(v.text()).not.toMatch(/\bnull\b|\bundefined\b/);
  });
});

describe('card 466: Sync lists what came in', () => {
  it('the core\'s incoming: the commits the team-behind count listed before, each opening the Commit view', async () => {
    const before = real<TeamGitStatus>('status-before-sync');
    const listed = real<CommitRange>('range-team-behind');
    const sync = real<BranchSyncResult>('sync-ff');
    let synced = false;
    const { w, calls } = await dialog({
      'git.status': () => (synced ? { ...before, teamSha: sync.incomingRange!.to, teamBehindBase: 0, merge: 'up-to-date' } : before),
      'git.sync': () => { synced = true; return sync; },
    });
    await w.find('[data-test=git-sync-now]').trigger('click');
    await flushPromises();
    expect(of(calls, 'git.commitRange')).toHaveLength(0);
    const c = w.find('[data-test=git-result-commits]');
    expect(c.text()).toContain('2 commits came into team/alpha:');
    expect(c.findAll('[data-test=commit-list-row]').map((r) => r.attributes('data-sha'))).toEqual(listed.commits.map((x) => x.sha));
    await c.find('[data-test=commit-link]').trigger('click');
    expect(commitTarget.value?.sha).toBe(listed.commits[0].sha);
  });

  it('F4 (fixed, Ines efac07d): the commits that came in carry the "not pushed" mark, as the same rows in the count\'s list do', async () => {
    const before = real<TeamGitStatus>('status-before-sync');
    const listed = real<CommitRange>('range-team-behind');
    const sync = real<BranchSyncResult>('sync-ff');
    expect(sync.incoming!.every((c) => c.notPushed)).toBe(true);
    expect(before.origin).toBeTruthy();
    let synced = false;
    const { w } = await dialog({
      'git.status': () => (synced ? { ...before, teamSha: sync.incomingRange!.to, teamBehindBase: 0, merge: 'up-to-date' } : before),
      'git.sync': () => { synced = true; return sync; },
      'git.commitRange': () => listed,
    });
    await w.find('[data-test=git-count-behind]').trigger('click');
    await flushPromises();
    expect(w.findAll('[data-test=git-range] [data-test=local-mark]')).toHaveLength(2); // the count's list marks both
    await w.find('[data-test=git-sync-now]').trigger('click');
    await flushPromises();
    const came = w.findAll('[data-test=git-result-commits] [data-test=commit-list-row]');
    expect(came).toHaveLength(2);
    expect(came.map((r) => r.find('[data-test=local-mark]').exists())).toEqual(sync.incoming!.map((c) => c.notPushed));
  });

  it('F4, no origin: the commits that came in carry no per-row mark', async () => {
    const before = { ...real<TeamGitStatus>('status-before-sync'), origin: null };
    const sync = real<BranchSyncResult>('sync-ff');
    let synced = false;
    const { w } = await dialog({
      'git.status': () => (synced ? { ...before, teamSha: sync.incomingRange!.to, teamBehindBase: 0, merge: 'up-to-date' } : before),
      'git.sync': () => { synced = true; return sync; },
    });
    await w.find('[data-test=git-sync-now]').trigger('click');
    await flushPromises();
    expect(w.findAll('[data-test=git-result-commits] [data-test=commit-list-row]')).toHaveLength(2);
    expect(w.findAll('[data-test=git-result-commits] [data-test=local-mark]')).toHaveLength(0);
  });
});
