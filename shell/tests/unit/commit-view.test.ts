// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { afterEach, describe, expect, it, vi } from 'vitest';
import CommitView from '../../src/renderer/src/components/CommitView.vue';
import CommitDialog from '../../src/renderer/src/components/CommitDialog.vue';
import CommitLink from '../../src/renderer/src/components/CommitLink.vue';
import {
  closeCommit, commitParts, commitTarget, commitTeams, gitRequest, installCommitLinks, isCommitId, itsBranch, mergeLine, openCommit, whereLines,
} from '../../src/renderer/src/commitView';
import { renderMarkdown } from '../../src/renderer/src/markdown';
import { isUiMethod, type CommitDiff, type CommitFound, type CommitShow, type YawbleApi } from '../../src/shared/api';

// B072 (card 444): the Commit view against a fake core with the card's contract (git.commitShow, git.commitDiff).

const SHA = '3f9c2a1b7d4e5f60718293a4b5c6d7e8f9012345';
const base = (over: Partial<CommitFound> = {}): CommitFound => ({
  found: true, sha: SHA, shortSha: SHA.slice(0, 7), title: 'Add partial refunds', body: 'Refunds can be partial now.\n\nCard 12.',
  author: { name: 'DeveloperAda', email: 'ada@yawble.local', date: '2026-11-18T10:00:00Z' }, committer: null,
  isMerge: false, parents: ['1111111aaaaaaa'], made: { member: 'DeveloperAda', card: 12, workflow: 10 },
  where: { inMain: true, branches: ['team/payments'], pushed: true, tags: [] },
  files: [{ path: 'src/refund.ts', status: 'modified', added: 3, deleted: 1, binary: false }],
  codeShown: true, ...over,
});
const diffOf = (path: string, over: Partial<CommitDiff> = {}): CommitDiff => ({
  path, status: 'modified', binary: false, tooLarge: false, added: 2, deleted: 1,
  hunks: [{ header: '@@ -1,3 +1,4 @@ function refund', oldStart: 1, oldCount: 3, newStart: 1, newCount: 4, lines: [
    { kind: 'context', old: 1, new: 1, text: 'export function refund(amount: number) {' },
    { kind: 'deleted', old: 2, text: '  return amount > 0;' },
    { kind: 'added', new: 2, text: '  if (amount <= 0) return false;' },
    { kind: 'added', new: 3, text: '  return amount <= limit();' },
    { kind: 'context', old: 3, new: 4, text: '}' },
  ] }], ...over,
});

function fakeCore(commits: Record<string, CommitShow | Error>, diffs: Record<string, CommitDiff> = {}) {
  const calls: { method: string; params: any }[] = [];
  const api: YawbleApi = {
    getInfo: async () => { throw new Error('no'); },
    getAvailableBackends: async () => [],
    call: async (method: string, params?: Record<string, unknown>) => {
      calls.push({ method, params });
      if (method === 'git.commitShow') {
        const c = commits[String(params?.team)];
        if (!c) return { found: false, goneReason: 'not in this repository', sha: params?.sha } as any;
        if (c instanceof Error) throw c;
        return structuredClone(c) as any;
      }
      if (method === 'git.commitDiff') {
        const d = diffs[String(params?.path)];
        if (!d) throw Object.assign(new Error('commit 3f9c2a1b7d4e does not change that file'), { code: 'no_such_file' });
        return structuredClone(d) as any;
      }
      throw new Error(`unexpected ${method}`);
    },
    onEvent: () => () => {},
    openTerminal: async () => {},
    pickFolder: async () => null,
  };
  return { api, calls, called: (m: string) => calls.filter((c) => c.method === m) };
}

const wrappers: { unmount: () => void }[] = [];
afterEach(() => { closeCommit(); gitRequest.value = null; commitTeams.value = { current: null, all: [] }; while (wrappers.length) wrappers.pop()!.unmount(); });
async function view(core: ReturnType<typeof fakeCore>, teams = ['payments'], sha = SHA) {
  const w = mount(CommitView, { props: { api: core.api, teams, sha }, attachTo: document.body });
  wrappers.push(w);
  await flushPromises();
  return w;
}
const whereTexts = (w: Awaited<ReturnType<typeof view>>) => w.findAll('[data-test=commit-where] li').map((l) => l.text());

describe('commit IDs in the app', () => {
  it('the core methods are allowed to the window', () => {
    expect(isUiMethod('git.commitShow')).toBe(true);
    expect(isUiMethod('git.commitDiff')).toBe(true);
  });

  it('isCommitId: 7 to 40 hex with a digit and a letter', () => {
    expect(isCommitId('c62216a')).toBe(true);
    expect(isCommitId(SHA)).toBe(true);
    expect(isCommitId('abc123')).toBe(false); // 6
    expect(isCommitId('1234567')).toBe(false); // a number
    expect(isCommitId('deadbeef')).toBe(false); // a word
    expect(isCommitId('g62216a')).toBe(false);
    expect(isCommitId(`${SHA}0`)).toBe(false); // 41
  });

  it('commitParts finds IDs in text, not inside words, paths or URLs', () => {
    expect(commitParts('Merged c62216a and 7c63337 into main.').filter((p) => p.sha).map((p) => p.sha)).toEqual(['c62216a', '7c63337']);
    expect(commitParts('see https://x.dev/commit/c62216a').some((p) => p.sha)).toBe(false);
    expect(commitParts('file abc1234def.ts or src/c62216a').some((p) => p.sha)).toBe(false);
    expect(commitParts('B072 card 444 on 2026-11-18').some((p) => p.sha)).toBe(false);
    expect(commitParts('CAFE1234 in capitals').some((p) => p.sha)).toBe(false);
    const parts = commitParts('at c62216a.');
    expect(parts.map((p) => p.text).join('')).toBe('at c62216a.');
  });

  it('rendered markdown links an ID in text and in inline code, never in a link or a code block', () => {
    const html = renderMarkdown('Commit 56b5597 on `aefd652`.\n\n```\ngit show 96ddd99\n```\n\n[see](https://x.dev/56b5597abc)');
    const box = document.createElement('div');
    box.innerHTML = html;
    expect([...box.querySelectorAll('[data-commit]')].map((a) => a.getAttribute('data-commit'))).toEqual(['56b5597', 'aefd652']);
    expect(box.querySelector('pre')!.textContent).toContain('git show 96ddd99');
    expect(box.querySelector('pre [data-commit]')).toBeNull();
    expect(box.querySelector('a[href] [data-commit], a[href][data-commit]')).toBeNull();
  });

  it('rendered markdown stays safe: the ID link is our markup only', () => {
    const html = renderMarkdown('<img src=x onerror=alert(1)> c62216a');
    expect(html).not.toContain('<img');
    expect(html).toContain('data-commit="c62216a"');
  });

  it('a click on a rendered ID opens the view in its place\'s team first, then the team on screen, then every team', async () => {
    const off = installCommitLinks(document);
    commitTeams.value = { current: 'web', all: ['web', 'payments', 'ops'] };
    const box = document.createElement('div');
    box.innerHTML = `<div data-commit-team="payments">${renderMarkdown('Done in c62216a.')}</div>`;
    document.body.appendChild(box);
    (box.querySelector('[data-commit]') as HTMLElement).click();
    expect(commitTarget.value).toEqual({ teams: ['payments', 'web', 'ops'], sha: 'c62216a' });
    closeCommit();
    const free = document.createElement('div');
    free.innerHTML = renderMarkdown('And 7c63337.');
    document.body.appendChild(free);
    (free.querySelector('[data-commit]') as HTMLElement).dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    expect(commitTarget.value).toEqual({ teams: ['web', 'payments', 'ops'], sha: '7c63337' });
    off();
    box.remove(); free.remove();
  });

  it('CommitLink shows the short ID and opens the view; a missing ID is plain text', async () => {
    const w = mount(CommitLink, { props: { team: 'payments', sha: SHA } });
    wrappers.push(w);
    expect(w.text()).toBe(SHA.slice(0, 10));
    await w.get('[data-test=commit-link]').trigger('click');
    expect(commitTarget.value).toEqual({ teams: ['payments'], sha: SHA });
    const none = mount(CommitLink, { props: { team: 'payments', sha: null } });
    wrappers.push(none);
    expect(none.find('[data-test=commit-link]').exists()).toBe(false);
    expect(none.text()).toBe('—');
  });

  it('openCommit ignores something that is not an ID; takes 4 to 64 hex as the core does', () => {
    openCommit('payments', 'main');
    expect(commitTarget.value).toBeNull();
    openCommit('payments', 'abc');
    expect(commitTarget.value).toBeNull();
    openCommit('payments', 'a'.repeat(65));
    expect(commitTarget.value).toBeNull();
    openCommit('payments', 'ABCD');
    expect(commitTarget.value).toEqual({ teams: ['payments'], sha: 'abcd' });
    openCommit('payments', 'f'.repeat(64));
    expect(commitTarget.value?.sha).toHaveLength(64);
  });
});

describe('the Commit view', () => {
  it('an ordinary commit: what, who, where, files, actions', async () => {
    const core = fakeCore({ payments: base() });
    const w = await view(core);
    expect(core.called('git.commitShow')[0].params).toEqual({ team: 'payments', sha: SHA });
    expect(w.get('[data-test=commit-title]').text()).toBe('Add partial refunds');
    expect(w.get('[data-test=commit-body]').text()).toContain('Refunds can be partial now.');
    expect(w.get('[data-test=commit-author]').text()).toContain('DeveloperAda');
    expect(w.find('[data-test=commit-committer]').exists()).toBe(false);
    expect(w.get('[data-test=commit-made]').text()).toBe('Developer Ada · card 12 · workflow #10');
    expect(w.get('[data-test=commit-sha]').text()).toBe(SHA);
    expect(w.find('[data-test=commit-merge]').exists()).toBe(false);
    expect(whereTexts(w)).toEqual(['In main', 'On origin']);
    const files = w.findAll('[data-test=commit-file]');
    expect(files).toHaveLength(1);
    expect(files[0].text()).toContain('src/refund.ts');
    expect(files[0].get('[data-test=commit-file-status]').text()).toBe('changed');
    expect(files[0].text()).toContain('+3');
    expect(files[0].text()).toContain('−1');
    expect(w.findAll('[data-test=commit-actions] button').map((b) => b.text())).toEqual(['Copy ID', 'Compare with main', 'Show its branch']);
    expect(w.text()).not.toMatch(/revert|cherry/i);
  });

  it('a committer other than the author is shown', async () => {
    const w = await view(fakeCore({ payments: base({ committer: { name: 'Manager', email: 'm@yawble.local', date: '2026-11-18T11:00:00Z' } }) }));
    expect(w.get('[data-test=commit-committer]').text()).toContain('Manager');
  });

  it('a merge says what it brought in, and its files are against the first parent', async () => {
    const c = base({ isMerge: true, parents: ['aaaaaaa1', 'bbbbbbb2'], title: "Merge DeveloperMarlo's branch",
      merge: { intoBranch: 'team/yawble-engineering-team', fromBranch: 'members/yawble-engineering-team/developermarlo', fromMember: 'DeveloperMarlo', commitCount: 4 } });
    const w = await view(fakeCore({ payments: c }));
    expect(w.get('[data-test=commit-merge]').text()).toBe("Merged DeveloperMarlo's branch into team/yawble-engineering-team, 4 commits");
    expect(w.get('[data-test=commit-files] h4').text()).toContain('first parent');
    expect(mergeLine({ ...c, merge: { ...c.merge!, fromMember: null, commitCount: 1 } })).toBe('Merged members/yawble-engineering-team/developermarlo into team/yawble-engineering-team, 1 commit');
  });

  it('a merge whose branches git and the records cannot tell (null) never prints "null"', async () => {
    const c = base({ isMerge: true, parents: ['aaaaaaa1', 'bbbbbbb2'], merge: { intoBranch: null, fromBranch: null, commitCount: 3 } });
    const w = await view(fakeCore({ payments: c }));
    expect(w.get('[data-test=commit-merge]').text()).toBe('Merged another branch, 3 commits');
    expect(w.text()).not.toContain('null');
    expect(mergeLine({ ...c, merge: { intoBranch: 'team/payments', fromBranch: null, commitCount: 1 } })).toBe('Merged another branch into team/payments, 1 commit');
    expect(mergeLine({ ...c, merge: { intoBranch: null, fromBranch: 'members/payments/ada', commitCount: 2 } })).toBe('Merged members/payments/ada, 2 commits');
  });

  it('made missing: no "Made by" row; a team-branch merge credited to the Manager', async () => {
    const { made: _, ...noMade } = base();
    const w = await view(fakeCore({ payments: noMade as CommitFound }));
    expect(w.find('[data-test=commit-made]').exists()).toBe(false);
    expect(w.text()).not.toMatch(/undefined|null/);
    const m = await view(fakeCore({ payments: base({ isMerge: true, parents: ['a1a1a1a', 'b2b2b2b'], made: { member: 'Manager', workflow: 7 },
      merge: { intoBranch: 'team/payments', fromBranch: 'members/payments/developerada', fromMember: 'DeveloperAda', commitCount: 2 } }) }));
    expect(m.get('[data-test=commit-made]').text()).toBe('Manager · workflow #7');
  });

  it('a malformed ID: the core\'s bad_params message is shown', async () => {
    const w = await view(fakeCore({ payments: Object.assign(new Error('sha must be 4 to 64 hex digits'), { code: 'bad_params' }) }));
    expect(w.get('[data-test=commit-error]').text()).toContain('sha must be 4 to 64 hex digits');
  });

  it('a pure rename: the core\'s summary, no hunks, no mode buttons', async () => {
    const files = [{ path: 'src/refunds.ts', oldPath: 'src/refund.ts', status: 'renamed', added: 0, deleted: 0, binary: false }];
    const core = fakeCore({ payments: base({ files }) }, { 'src/refunds.ts': { path: 'src/refunds.ts', oldPath: 'src/refund.ts', status: 'renamed', binary: false, tooLarge: false,
      summary: 'Renamed from src/refund.ts; no line changed.', added: 0, deleted: 0, hunks: [] } });
    const w = await view(core);
    await w.get('[data-test=commit-file-open]').trigger('click');
    await flushPromises();
    expect(w.get('[data-test=diff-empty]').text()).toBe('Renamed from src/refund.ts; no line changed.');
    expect(w.find('[data-test=diff-line]').exists()).toBe(false);
  });

  it('a diff the Cloud Link refuses (remote_refused) shows its message', async () => {
    const api = fakeCore({ payments: base() }).api;
    const refusing: YawbleApi = { ...api, call: async (m: string, p?: Record<string, unknown>) => {
      if (m === 'git.commitDiff') throw Object.assign(new Error("Members' code is not shown to cloud viewers."), { code: 'remote_refused' });
      return api.call(m, p);
    } } as YawbleApi;
    const w = mount(CommitView, { props: { api: refusing, teams: ['payments'], sha: SHA }, attachTo: document.body });
    wrappers.push(w);
    await flushPromises();
    await w.get('[data-test=commit-file-open]').trigger('click');
    await flushPromises();
    expect(w.get('[data-test=commit-diff-error]').text()).toContain("Members' code is not shown to cloud viewers.");
  });

  it('a renamed file shows old → new and its diff opens side by side and inline', async () => {
    const files = [{ path: 'src/refunds.ts', oldPath: 'src/refund.ts', status: 'renamed', added: 2, deleted: 1, binary: false }];
    const core = fakeCore({ payments: base({ files }) }, { 'src/refunds.ts': diffOf('src/refunds.ts', { oldPath: 'src/refund.ts' }) });
    const w = await view(core);
    const f = w.get('[data-test=commit-file]');
    expect(f.text()).toContain('src/refund.ts → src/refunds.ts');
    expect(f.get('[data-test=commit-file-status]').text()).toBe('renamed');
    await f.get('[data-test=commit-file-open]').trigger('click');
    await flushPromises();
    expect(core.called('git.commitDiff')[0].params).toEqual({ team: 'payments', sha: SHA, path: 'src/refunds.ts' });
    expect(w.get('[data-test=diff-path]').text()).toBe('src/refund.ts → src/refunds.ts');
    // Side by side: the removed line beside the first added one; the second added has an empty left cell.
    expect(w.find('[data-test=diff-split]').exists()).toBe(true);
    const rows = w.findAll('[data-test=diff-row]');
    expect(rows).toHaveLength(4);
    expect(rows[1].get('[data-test=diff-left]').text()).toContain('return amount > 0;');
    expect(rows[1].get('[data-test=diff-right]').text()).toContain('if (amount <= 0) return false;');
    expect(rows[2].get('[data-test=diff-left]').classes()).toContain('empty');
    await w.get('[data-test=diff-mode-inline]').trigger('click');
    const lines = w.findAll('[data-test=diff-line]');
    expect(lines.map((l) => l.attributes('data-kind'))).toEqual([' ', '-', '+', '+', ' ']);
    expect(lines[3].text()).toContain('3'); // new line number 3
    // A second click closes it.
    await f.get('[data-test=commit-file-open]').trigger('click');
    expect(w.find('[data-test=diff-view]').exists()).toBe(false);
  });

  it('a binary file and a too-large diff show the summary line, not content', async () => {
    const files = [{ path: 'logo.png', status: 'modified', added: 0, deleted: 0, binary: true }, { path: 'big.json', status: 'modified', added: 90000, deleted: 10, binary: false }];
    const core = fakeCore({ payments: base({ files }) }, {
      'logo.png': { path: 'logo.png', status: 'modified', binary: true, tooLarge: false, summary: 'Binary file, 12 KB to 14 KB', added: 0, deleted: 0, hunks: [] },
      'big.json': { path: 'big.json', status: 'modified', binary: false, tooLarge: true, summary: 'Too large to show: 90,000 lines added, 10 removed', added: 90000, deleted: 10, hunks: [] },
    });
    const w = await view(core);
    const [png, big] = w.findAll('[data-test=commit-file]');
    expect(png.find('[data-test=commit-file-binary]').exists()).toBe(true);
    await png.get('[data-test=commit-file-open]').trigger('click');
    await flushPromises();
    expect(w.get('[data-test=diff-summary]').text()).toBe('Binary file, 12 KB to 14 KB');
    expect(w.find('[data-test=diff-line]').exists()).toBe(false);
    expect(w.find('[data-test=diff-mode-inline]').exists()).toBe(false);
    await big.get('[data-test=commit-file-open]').trigger('click');
    await flushPromises();
    expect(w.get('[data-test=diff-summary]').text()).toBe('Too large to show: 90,000 lines added, 10 removed');
  });

  it('an unpushed commit only on a member\'s branch says so', async () => {
    const w = await view(fakeCore({ payments: base({ where: { inMain: false, branches: ['members/payments/developerada'], onlyOnMember: 'DeveloperAda', pushed: false, tags: [] } }) }));
    expect(whereTexts(w)).toEqual(["Only on DeveloperAda's branch", 'Not pushed: only on this computer']);
  });

  it('a commit in the team branch only, not in main yet', async () => {
    const c = base({ where: { inMain: false, branches: ['team/payments', 'members/payments/developerada'], onlyOnMember: null, pushed: true, tags: [] } });
    const w = await view(fakeCore({ payments: c }));
    expect(whereTexts(w)).toEqual(['In the team branch only, not in main yet', 'On origin']);
    expect(itsBranch(c)).toBe('members/payments/developerada');
  });

  it('a tagged commit is included in a release', async () => {
    const w = await view(fakeCore({ payments: base({ where: { inMain: true, branches: [], pushed: true, tags: ['v2026.11.18.1'] } }) }));
    expect(w.get('[data-test=commit-where-tags]').text()).toBe('Included in release v2026.11.18.1');
  });

  it('a commit on no branch any more (its branch deleted) still opens and says so', () => {
    const lines = whereLines(base({ where: { inMain: false, branches: [], onlyOnMember: null, pushed: false, tags: [] } }));
    expect(lines[0].text).toBe('On no branch (a branch that held it was deleted)');
  });

  // Card 459 (F2 from verify card 445): where.otherBranches, the other local branches holding it.
  it('a commit only on the person\'s own branch names it, not as deleted; Show its branch stays off', async () => {
    const c = base({ where: { inMain: false, branches: [], otherBranches: ['feature/x'], onlyOnMember: null, pushed: false, tags: [] } });
    const w = await view(fakeCore({ payments: c }));
    expect(whereTexts(w)).toEqual(['On feature/x (not a team or member branch)', 'Not pushed: only on this computer']);
    expect(itsBranch(c)).toBeNull();
    expect(w.get('[data-test=commit-branch]').attributes('disabled')).toBeDefined();
  });

  it('several other branches are joined with commas', () => {
    const lines = whereLines(base({ where: { inMain: false, branches: [], otherBranches: ['feature/x', 'trial/y'], pushed: true, tags: [] } }));
    expect(lines[0]).toEqual({ key: 'other', text: 'On feature/x, trial/y (not a team or member branch)', tone: 'warn' });
  });

  it('no branch and no other branch keeps the deleted wording, also when otherBranches is missing (older core)', async () => {
    const empty = base({ where: { inMain: false, branches: [], otherBranches: [], onlyOnMember: null, pushed: false, tags: [] } });
    const w = await view(fakeCore({ payments: empty }));
    expect(whereTexts(w)[0]).toBe('On no branch (a branch that held it was deleted)');
    expect(w.get('[data-test=commit-branch]').attributes('disabled')).toBeDefined();
    const older = base({ where: { inMain: false, branches: [], pushed: false, tags: [] } });
    expect(whereLines(older)[0].text).toBe('On no branch (a branch that held it was deleted)');
  });

  it('otherBranches changes nothing when the commit is in main, the team branch or a member branch', () => {
    const other = ['feature/x'];
    expect(whereLines(base({ where: { inMain: true, branches: [], otherBranches: other, pushed: true, tags: [] } }))[0].text).toBe('In main');
    expect(whereLines(base({ where: { inMain: false, branches: ['team/payments'], otherBranches: other, pushed: true, tags: [] } }))[0].text)
      .toBe('In the team branch only, not in main yet');
    expect(whereLines(base({ where: { inMain: false, branches: ['members/payments/developerada'], otherBranches: other, onlyOnMember: 'DeveloperAda', pushed: true, tags: [] } }))[0].text)
      .toBe("Only on DeveloperAda's branch");
  });

  it('a gone commit says it is gone and why; only Copy ID is offered', async () => {
    // As the core sends it: only found, goneReason and sha.
    const gone: CommitShow = { found: false, goneReason: "Commit 3f9c2a1b7d4e was on members/payments/old-cleo (OldCleo's branch), and git no longer has it: that branch was deleted.", sha: SHA };
    const w = await view(fakeCore({ payments: gone }));
    expect(w.get('[data-test=commit-gone] h3').text()).toBe('This commit is gone');
    expect(w.get('[data-test=commit-gone-reason]').text()).toBe("Commit 3f9c2a1b7d4e was on members/payments/old-cleo (OldCleo's branch), and git no longer has it: that branch was deleted.");
    expect(w.find('[data-test=commit-compare]').exists()).toBe(false);
    expect(w.find('[data-test=commit-copy]').exists()).toBe(true);
  });

  it('code hidden (a cloud viewer): the commit and its files, no diff opens', async () => {
    const core = fakeCore({ payments: base({ codeShown: false }) }, { 'src/refund.ts': diffOf('src/refund.ts') });
    const w = await view(core);
    expect(w.get('[data-test=commit-code-hidden]').text()).toContain('not shown to cloud viewers');
    expect(w.get('[data-test=commit-file]').text()).toContain('src/refund.ts');
    expect(w.find('button[data-test=commit-file-open]').exists()).toBe(false);
    await w.get('[data-test=commit-file-open]').trigger('click');
    await flushPromises();
    expect(core.called('git.commitDiff')).toHaveLength(0);
    expect(w.find('[data-test=diff-view]').exists()).toBe(false);
  });

  it('several teams: the first whose repository has it is shown', async () => {
    const core = fakeCore({ web: { found: false, goneReason: 'not here', sha: SHA }, payments: base() });
    const w = await view(core, ['web', 'payments', 'ops']);
    expect(core.called('git.commitShow').map((c) => c.params.team)).toEqual(['web', 'payments']);
    expect(w.get('[data-test=commit-view]').attributes('data-team')).toBe('payments');
    expect(w.get('[data-test=commit-title]').text()).toBe('Add partial refunds');
  });

  it('a core error is shown plainly', async () => {
    const w = await view(fakeCore({ payments: new Error('git failed: bad object') }));
    expect(w.get('[data-test=commit-error]').text()).toContain('git failed: bad object');
  });

  it('a diff that fails to read says so', async () => {
    const w = await view(fakeCore({ payments: base() }));
    await w.get('[data-test=commit-file-open]').trigger('click');
    await flushPromises();
    expect(w.get('[data-test=commit-diff-error]').text()).toContain('commit 3f9c2a1b7d4e does not change that file');
  });

  it('Copy ID copies the full ID', async () => {
    const writeText = vi.fn(async () => {});
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
    const w = await view(fakeCore({ payments: base() }));
    await w.get('[data-test=commit-copy]').trigger('click');
    await flushPromises();
    expect(writeText).toHaveBeenCalledWith(SHA);
    expect(w.get('[data-test=commit-copy]').text()).toBe('Copied');
  });

  it('Compare with main and Show its branch ask for the team\'s Git dialog and close the view', async () => {
    const c = base({ where: { inMain: false, branches: ['team/payments', 'members/payments/developerada'], onlyOnMember: null, pushed: true, tags: [] } });
    const w = await view(fakeCore({ payments: c }));
    await w.get('[data-test=commit-compare]').trigger('click');
    expect(gitRequest.value).toEqual({ team: 'payments', focus: 'compare', branch: null, sha: SHA });
    expect(w.emitted('done')).toHaveLength(1);
    await w.get('[data-test=commit-branch]').trigger('click');
    expect(gitRequest.value).toEqual({ team: 'payments', focus: 'branch', branch: 'members/payments/developerada', sha: SHA });
  });

  it('the dialog shows the target commit, one at a time, and Close closes it', async () => {
    const core = fakeCore({ payments: base() });
    const d = mount(CommitDialog, { props: { api: core.api }, attachTo: document.body });
    wrappers.push(d);
    expect(document.querySelector('[data-test=commit-dialog]')).toBeNull();
    openCommit('payments', SHA);
    await flushPromises();
    expect(document.querySelector('[data-test=commit-dialog]')!.getAttribute('data-sha')).toBe(SHA);
    expect(document.querySelector('[role=dialog]')!.getAttribute('aria-label')).toBe(`Commit ${SHA.slice(0, 10)}`);
    openCommit('payments', 'c62216a');
    await flushPromises();
    expect(document.querySelectorAll('[data-test=commit-dialog]')).toHaveLength(1);
    expect(document.querySelector('[data-test=commit-dialog]')!.getAttribute('data-sha')).toBe('c62216a');
    (document.querySelector('[data-test=modal-close]') as HTMLElement).click();
    await flushPromises();
    expect(commitTarget.value).toBeNull();
    expect(document.querySelector('[data-test=commit-dialog]')).toBeNull();
  });
});
