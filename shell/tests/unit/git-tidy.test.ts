// @vitest-environment happy-dom
// The Git dialog's "Tidy…": git.tidyPlan grouped by class, merged and contained pre-selected, one yes
// deletes through git.deleteBranches (plan, then token), Realign shows old -> new and asks yes (refusal shown), and after
// each action the plan is read again and what was kept is listed with why.
import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import GitDialog from '../../src/renderer/src/components/GitDialog.vue';
import TidyPanel from '../../src/renderer/src/components/TidyPanel.vue';
import type { TeamGitStatus, TidyPlan, YawbleApi } from '../../src/shared/api';
import { defaultSelection, deleteRequests, groupTidy, keptReport, repoParam, rowKey, whereText } from '../../src/renderer/src/tidyPlan';

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
const coreError = (code: string, message: string) => Object.assign(new Error(message), { code });

const team = { id: 3, name: 'payments', repoPath: null, members: 2, running: 0, queued: 0 };
const plan = (): TidyPlan => ({ repos: [
  { repo: 'payments', baseBranch: 'main', branches: [
    { name: 'members/payments/ada', where: ['local', 'clone:Ada'], class: 'working', member: 'Ada' },
    { name: 'members/payments/old-ben', where: ['local', 'origin'], class: 'merged' },
    { name: 'members/payments/kofi', where: ['local'], class: 'contained', proof: 'git cherry main: - 1a2b3c4 Fix refunds (equivalent in main)' },
    { name: 'members/payments/marlo', where: ['local', 'origin', 'clone:Marlo'], class: 'moved', member: 'Marlo', teamSha: '5bbb6ba450df', cloneSha: 'e0f2a18aaaaa',
      commits: [{ sha: '7777777777ef', subject: 'Retry the upload' }], proof: 'the clone was reset or rebased; nothing is lost by realigning it (git cherry main: - 7777777777)' },
    { name: 'feature/x', where: ['local'], class: 'own-work', commits: [{ sha: '9999999999ab', subject: 'Half a feature' }, { sha: '8888888888cd', subject: 'More of it' }] },
  ] },
  { repo: 'payments-cloud', branches: [
    { name: 'old-idea', where: ['origin'], class: 'origin-only', commits: [{ sha: '6666666666aa', subject: 'Try a cache' }] },
    { name: 'scratch/try', where: ['clone:Ada'], class: 'scratch', member: 'Ada' },
    { name: 'members/payments/wren', where: ['origin'], class: 'merged' },
    { name: 'members/payments/only-in-clone', where: ['clone:Wren'], class: 'merged' },
  ], baseBranch: 'main', notes: ["Kofi's clone was not read: its .git is a file"] },
] });

describe('tidyPlan helpers', () => {
  it('groups by class in list order, leaves out empty groups, keeps the repository on each row', () => {
    const g = groupTidy(plan());
    expect(g.map((x) => x.cls)).toEqual(['merged', 'contained', 'moved', 'own-work', 'working', 'origin-only', 'scratch']);
    expect(g[0].rows.map((r) => `${r.repo}:${r.branch.name}`)).toEqual(['payments:members/payments/old-ben', 'payments-cloud:members/payments/wren', 'payments-cloud:members/payments/only-in-clone']);
    expect(groupTidy({ repos: [{ repo: 'r', branches: [{ name: 'a', where: ['local'], class: 'working' }] }] }).map((x) => x.cls)).toEqual(['working']);
    expect(groupTidy(null)).toEqual([]);
  });

  it('pre-selects merged and contained branches that are here or on origin, nothing else', () => {
    const s = defaultSelection(plan());
    expect([...s].sort()).toEqual([
      rowKey('payments', 'members/payments/kofi'), rowKey('payments', 'members/payments/old-ben'), rowKey('payments-cloud', 'members/payments/wren'),
    ].sort());
  });

  it('makes one delete request per repository and origin flag, main repository without a repo', () => {
    const p = plan();
    const q = deleteRequests(p, defaultSelection(p), 'payments');
    expect(q).toEqual([
      { repo: 'payments', repoParam: null, remote: true, branches: ['members/payments/old-ben'] },
      { repo: 'payments', repoParam: null, remote: false, branches: ['members/payments/kofi'] },
      { repo: 'payments-cloud', repoParam: 'payments-cloud', remote: true, branches: ['members/payments/wren'] },
    ]);
    // An unticked row and a non-deletable row (even if its key is in the set) are left out.
    const s = new Set([rowKey('payments', 'members/payments/kofi'), rowKey('payments', 'members/payments/ada')]);
    expect(deleteRequests(p, s, 'payments')).toEqual([{ repo: 'payments', repoParam: null, remote: false, branches: ['members/payments/kofi'] }]);
    expect(repoParam('main', null)).toBeNull();
    expect(repoParam('', 'x')).toBeNull();
    expect(repoParam('docs', 'payments')).toBe('docs');
  });

  it('says where and why each kept row is kept', () => {
    expect(whereText(['local', 'origin', 'clone:Ada'])).toBe("here, on origin, in Ada's clone");
    const p = plan();
    const kept = keptReport(p, new Set([rowKey('payments', 'members/payments/kofi')]));
    expect(kept).toContain("members/payments/ada (payments): Ada's working branch, not a leftover");
    expect(kept).toContain('members/payments/kofi (payments): kept by git.deleteBranches (see its plan)');
    expect(kept).toContain('members/payments/old-ben (payments): not selected');
    expect(kept).toContain("members/payments/marlo (payments): moved: team 5bbb6ba450, Marlo's clone e0f2a18aaa (Realign to fix)");
    expect(kept).toContain('feature/x (payments): has work of its own (2 commits), needs a look');
    expect(kept).toContain('old-idea (payments-cloud): only on origin, nobody has it here');
    expect(kept).toContain("scratch/try (payments-cloud): scratch branch in Ada's clone");
    expect(kept).toContain("members/payments/only-in-clone (payments-cloud): only in a member's clone");
  });
});

describe('TidyPanel', () => {
  it('lists the plan by class with proof, commits, team vs clone, and the deletable rows ticked', async () => {
    const { api, calls } = fakeApi({ 'git.tidyPlan': () => plan() });
    const w = mount(TidyPanel, { props: { api, team, mainName: 'payments' } });
    await flushPromises();
    expect(calls[0]).toEqual({ method: 'git.tidyPlan', params: { teamId: 3, team: 'payments' } });
    expect(w.find('[data-test=tidy-group-merged]').text()).toContain('members/payments/old-ben');
    expect(w.find('[data-test=tidy-group-working]').text()).toContain("Ada's working branch, not a leftover");
    expect(w.find('[data-test=tidy-proof]').text()).toContain('git cherry main: - 1a2b3c4');
    expect(w.find('[data-test="tidy-row-feature/x"] [data-test=tidy-commits]').text()).toContain('Half a feature');
    expect(w.find('[data-test=tidy-moved-shas]').text()).toBe("team 5bbb6ba450 · Marlo's clone e0f2a18aaa");
    const ticked = w.findAll('[data-test=tidy-select]').map((c) => (c.element as HTMLInputElement).checked);
    expect(ticked).toEqual([true, true, true]); // old-ben, wren, kofi; only-in-clone has no box
    expect(w.find('[data-test=tidy-delete-selected]').text()).toContain('Delete selected (3)');
    expect(w.find('[data-test="tidy-row-members/payments/only-in-clone"] [data-test=tidy-select]').exists()).toBe(false);
  });

  it('Delete selected plans each request, one yes confirms the tokens, then reloads and reports what was kept', async () => {
    let deleted = false;
    const { api, calls } = fakeApi({
      'git.tidyPlan': () => {
        const p = plan();
        if (deleted) for (const r of p.repos) r.branches = r.branches.filter((b) => b.class !== 'merged' || b.name.includes('only-in-clone'));
        return p;
      },
      'git.deleteBranches': (p) => (p.confirm ? (deleted = true, { result: `Deleted ${p.branches.join(', ')}.` })
        : { plan: `Delete ${p.branches.join(', ')}${p.remote ? ' here and on origin' : ''}`, token: `tok-${p.branches[0]}`, empty: false }),
    });
    const w = mount(TidyPanel, { props: { api, team, mainName: 'payments' } });
    await flushPromises();
    // Untick kofi: it stays.
    await w.find('[data-test="tidy-row-members/payments/kofi"] [data-test=tidy-select]').setValue(false);
    expect(w.find('[data-test=tidy-delete-selected]').text()).toContain('(2)');
    await w.find('[data-test=tidy-delete-selected]').trigger('click');
    await flushPromises();
    const plans = w.findAll('[data-test=tidy-delete-plan]').map((p) => p.text());
    expect(plans).toEqual(['Delete members/payments/old-ben here and on origin', 'Delete members/payments/wren here and on origin']);
    expect(calls.filter((c) => c.method === 'git.deleteBranches').map((c) => c.params)).toEqual([
      { teamId: 3, branches: ['members/payments/old-ben'], remote: true },
      { teamId: 3, repo: 'payments-cloud', branches: ['members/payments/wren'], remote: true },
    ]);
    await w.find('[data-test=tidy-delete-yes]').trigger('click');
    await flushPromises();
    const confirms = calls.filter((c) => c.method === 'git.deleteBranches' && c.params.confirm).map((c) => c.params.confirm);
    expect(confirms).toEqual(['tok-members/payments/old-ben', 'tok-members/payments/wren']);
    expect(w.findAll('[data-test=tidy-done]').map((d) => d.text())).toEqual(['Deleted members/payments/old-ben.', 'Deleted members/payments/wren.']);
    const kept = w.findAll('[data-test=tidy-kept]').map((k) => k.text());
    expect(kept).toContain('members/payments/kofi (payments): not selected');
    expect(kept).toContain("members/payments/ada (payments): Ada's working branch, not a leftover");
    expect(kept.some((k) => k.includes('old-ben'))).toBe(false);
    expect(w.find('[data-test=tidy-delete-confirm]').exists()).toBe(false);
    expect(w.emitted('changed')).toBeTruthy();
    expect(calls.filter((c) => c.method === 'git.tidyPlan')).toHaveLength(2);
  });

  it('a stale delete re-reads the plan and plans again with the note', async () => {
    const { api, calls } = fakeApi({
      'git.tidyPlan': () => plan(),
      'git.deleteBranches': (p) => { if (p.confirm) throw coreError('stale', 'The branches changed since the plan.'); return { plan: 'Delete it', token: 't', empty: false }; },
    });
    const w = mount(TidyPanel, { props: { api, team, mainName: 'payments' } });
    await flushPromises();
    await w.find('[data-test=tidy-delete-selected]').trigger('click');
    await flushPromises();
    await w.find('[data-test=tidy-delete-yes]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=tidy-delete-stale]').text()).toContain('The branches changed since the plan. Planned again below.');
    expect(w.findAll('[data-test=tidy-delete-plan]').length).toBeGreaterThan(0);
    expect(calls.filter((c) => c.method === 'git.tidyPlan')).toHaveLength(2);
  });

  it('nothing to delete: the plans are empty, no Delete button, Close', async () => {
    const { api } = fakeApi({ 'git.tidyPlan': () => plan(), 'git.deleteBranches': () => ({ plan: 'KEPT: in use', token: '', empty: true }) });
    const w = mount(TidyPanel, { props: { api, team, mainName: 'payments' } });
    await flushPromises();
    await w.find('[data-test=tidy-delete-selected]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=tidy-delete-empty]').exists()).toBe(true);
    expect(w.find('[data-test=tidy-delete-yes]').exists()).toBe(false);
    expect(w.find('[data-test=tidy-delete-cancel]').text()).toBe('Close');
  });

  it('Realign shows old -> new, the yes calls git.branchRealign, then reports old -> new and what was kept', async () => {
    let realigned = false;
    const { api, calls } = fakeApi({
      'git.tidyPlan': () => { const p = plan(); if (realigned) p.repos[0].branches[3] = { ...p.repos[0].branches[3], class: 'working', teamSha: null, cloneSha: null }; return p; },
      'git.branchRealign': () => { realigned = true; return { oldSha: '5bbb6ba450df8fe2', newSha: 'e0f2a18aaaaabbbb' }; },
    });
    const w = mount(TidyPanel, { props: { api, team, mainName: 'payments' } });
    await flushPromises();
    await w.find('[data-test=tidy-realign]').trigger('click');
    expect(w.find('[data-test=tidy-realign-shas]').text()).toBe('5bbb6ba450 → e0f2a18aaa');
    expect(w.find('[data-test=tidy-realign-confirm]').text()).toContain("and origin's copy");
    expect(calls.some((c) => c.method === 'git.branchRealign')).toBe(false);
    await w.find('[data-test=tidy-realign-yes]').trigger('click');
    await flushPromises();
    expect(calls.find((c) => c.method === 'git.branchRealign')!.params).toEqual({ teamId: 3, team: 'payments', repo: 'payments', member: 'Marlo', expectOld: '5bbb6ba450df', expectNew: 'e0f2a18aaaaa' });
    expect(w.find('[data-test=tidy-done]').text()).toBe('Realigned members/payments/marlo: 5bbb6ba450 → e0f2a18aaa (the old commit is recorded for recovery).');
    expect(w.findAll('[data-test=tidy-kept]').map((k) => k.text())).toContain("members/payments/marlo (payments): Marlo's working branch, not a leftover");
    expect(w.find('[data-test=tidy-group-moved]').exists()).toBe(false);
  });

  it('Realign refused while the member runs: the reason is shown and nothing reloads', async () => {
    const { api, calls } = fakeApi({
      'git.tidyPlan': () => plan(),
      'git.branchRealign': () => { throw coreError('running', 'Marlo is running: realign when the run ends.'); },
    });
    const w = mount(TidyPanel, { props: { api, team, mainName: 'payments' } });
    await flushPromises();
    await w.find('[data-test=tidy-realign]').trigger('click');
    await w.find('[data-test=tidy-realign-yes]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=tidy-realign-error]').text()).toBe('Refused: Marlo is running: realign when the run ends.');
    expect(w.find('[data-test=tidy-realign-confirm]').exists()).toBe(true);
    expect(w.find('[data-test=tidy-report]').exists()).toBe(false);
    expect(calls.filter((c) => c.method === 'git.tidyPlan')).toHaveLength(1);
  });

  it('Realign refused as stale: "Refused: <reason>", the plan is read again and the confirm shows the new commits', async () => {
    let reads = 0;
    const { api, calls } = fakeApi({
      'git.tidyPlan': () => { const p = plan(); if (reads++) p.repos[0].branches[3] = { ...p.repos[0].branches[3], cloneSha: 'f00dbeef0001' }; return p; },
      'git.branchRealign': () => { throw coreError('stale', 'members/payments/marlo changed since it was shown. Nothing changed; look at the new plan.'); },
    });
    const w = mount(TidyPanel, { props: { api, team, mainName: 'payments' } });
    await flushPromises();
    await w.find('[data-test=tidy-realign]').trigger('click');
    await w.find('[data-test=tidy-realign-yes]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=tidy-realign-error]').text()).toBe('Refused: members/payments/marlo changed since it was shown. Nothing changed; look at the new plan.');
    expect(calls.filter((c) => c.method === 'git.tidyPlan')).toHaveLength(2);
    expect(w.find('[data-test=tidy-realign-shas]').text()).toBe('5bbb6ba450 → f00dbeef00');
  });

  // A coded failure is a plain error, even when its text says "runs" or "running".
  async function realignError(code: string, message: string) {
    const { api } = fakeApi({ 'git.tidyPlan': () => plan(), 'git.branchRealign': () => { throw coreError(code, message); } });
    const w = mount(TidyPanel, { props: { api, team, mainName: 'payments' } });
    await flushPromises();
    await w.find('[data-test=tidy-realign]').trigger('click');
    await w.find('[data-test=tidy-realign-yes]').trigger('click');
    await flushPromises();
    return w.find('[data-test=tidy-realign-error]').text();
  }

  it.each([
    ['git_failed', 'git update-ref failed: hook runs refused the update (exit 1)'],
    ['bad_params', 'member is required; the realign runs on one member branch'],
    ['no_such_member', 'No member Marlo in payments; was the member running elsewhere?'],
    // A failed push or clone is a git failure, not the core saying no.
    ['push_failed', 'git push origin members/payments/marlo failed: remote rejected (a hook runs on the server)'],
    ['clone', 'git clone of payments failed: could not read from the remote repository'],
  ])('Realign answered %s: the error is shown plain, not as "Refused:"', async (code, message) => {
    expect(await realignError(code, message)).toBe(message);
  });

  it('Realign refused with a real refusal code still shows "Refused:"', async () => {
    expect(await realignError('running', 'Marlo has a run open.')).toBe('Refused: Marlo has a run open.');
  });

  it.each([
    ['moved', 'members/payments/marlo changed since it was shown.'],
    ['no_such_team', 'No team payments.'],
    ['no_repositories', 'Team payments has no repositories.'],
  ])('Realign refused with %s still shows "Refused:"', async (code, message) => {
    expect(await realignError(code, message)).toBe(`Refused: ${message}`);
  });

  it('Realign with an uncoded error that says the member runs still shows "Refused:"', async () => {
    const { api } = fakeApi({ 'git.tidyPlan': () => plan(), 'git.branchRealign': () => { throw new Error('Marlo runs now.'); } });
    const w = mount(TidyPanel, { props: { api, team, mainName: 'payments' } });
    await flushPromises();
    await w.find('[data-test=tidy-realign]').trigger('click');
    await w.find('[data-test=tidy-realign-yes]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=tidy-realign-error]').text()).toBe('Refused: Marlo runs now.');
  });

  it.each(['git_failed', 'bad_params', 'no_such_member'])('Delete answered %s: the error is shown plain, not as "Refused:"', async (code) => {
    const message = `push --delete failed while a hook runs (${code})`;
    const { api } = fakeApi({
      'git.tidyPlan': () => plan(),
      'git.deleteBranches': (p) => { if (p.confirm) throw coreError(code, message); return { plan: 'Delete it', token: 't', empty: false }; },
    });
    const w = mount(TidyPanel, { props: { api, team, mainName: 'payments' } });
    await flushPromises();
    await w.find('[data-test=tidy-delete-selected]').trigger('click');
    await flushPromises();
    await w.find('[data-test=tidy-delete-yes]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=tidy-delete-error]').text()).toBe(message);
  });

  it('a moved row shows the commits a realign drops and the proof; origin-only shows its commits', async () => {
    const { api } = fakeApi({ 'git.tidyPlan': () => plan() });
    const w = mount(TidyPanel, { props: { api, team, mainName: 'payments' } });
    await flushPromises();
    const moved = w.find('[data-test="tidy-row-members/payments/marlo"]');
    expect(moved.find('[data-test=tidy-commits]').text()).toContain('Retry the upload');
    expect(moved.find('[data-test=tidy-why]').text()).toContain('nothing is lost by realigning it');
    expect(w.find('[data-test="tidy-row-old-idea"] [data-test=tidy-commits]').text()).toContain('Try a cache');
    await moved.find('[data-test=tidy-realign]').trigger('click');
    expect(w.find('[data-test=tidy-realign-commits]').text()).toContain('Retry the upload');
    expect(w.find('[data-test=tidy-realign-proof]').text()).toContain('nothing is lost');
  });

  it("shows the plan's notes above the list and what merged/contained are measured against", async () => {
    const { api } = fakeApi({ 'git.tidyPlan': () => plan() });
    const w = mount(TidyPanel, { props: { api, team, mainName: 'payments' } });
    await flushPromises();
    expect(w.find('[data-test=tidy-note]').text()).toBe("payments-cloud: Kofi's clone was not read: its .git is a file");
    expect(w.find('[data-test=tidy-base]').text()).toBe('Merged and contained are measured against main.');
    expect(w.html().indexOf('tidy-notes')).toBeLessThan(w.html().indexOf('tidy-group-'));
  });

  it('a core without git.tidyPlan shows its error', async () => {
    const { api } = fakeApi({ 'git.tidyPlan': () => { throw coreError('method_not_found', 'Unknown method git.tidyPlan'); } });
    const w = mount(TidyPanel, { props: { api, team } });
    await flushPromises();
    expect(w.find('[data-test=tidy-error]').text()).toContain('git.tidyPlan');
  });
});

describe('git dialog: Tidy…', () => {
  const status: TeamGitStatus = {
    repoPath: 'C:\\work\\payments', managed: false, baseBranch: 'main', checkedOut: 'main', dirty: false, origin: null,
    baseSha: 'aaaaaaaaaaaa', originBaseSha: null, baseAheadOfOrigin: null, baseBehindOrigin: null,
    teamBranch: 'team/payments', teamSha: 'bbbbbbbbbbbb', teamAheadOfBase: 0, teamBehindBase: 0, merge: 'up-to-date', conflictFiles: [], members: [],
  };
  it('opens the tidy panel with the main repository name from teams.repos, and Close hides it', async () => {
    const { api, calls } = fakeApi({
      'git.status': () => status,
      'teams.repos': () => ({ main: { name: 'payments' }, extras: [] }),
      'git.tidyPlan': () => plan(),
      'git.deleteBranches': () => ({ plan: 'p', token: 't', empty: false }),
    });
    const w = mount(GitDialog, { props: { api, team } });
    await flushPromises();
    expect(w.find('[data-test=tidy]').exists()).toBe(false);
    expect(calls.some((c) => c.method === 'git.tidyPlan')).toBe(false);
    await w.find('[data-test=git-tidy]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=tidy]').exists()).toBe(true);
    await w.find('[data-test=tidy-delete-selected]').trigger('click');
    await flushPromises();
    // main repository: no repo param
    expect(calls.find((c) => c.method === 'git.deleteBranches')!.params.repo).toBeUndefined();
    await w.find('[data-test=tidy-close]').trigger('click');
    expect(w.find('[data-test=tidy]').exists()).toBe(false);
  });
});
