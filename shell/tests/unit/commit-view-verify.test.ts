// @vitest-environment happy-dom
// Card 445 (B072 verify, TesterKofi): the Commit view fed the REAL core's answers. tests/unit/fixtures/card445/*.json
// are git.commitShow results the core wrote from a fixture repository (core Card445CommitViewVerifyTests with
// CARD445_FIXTURE_OUT set), so these check the shell against the core's actual contract, not a hand-made fake:
// not found is only {found, goneReason, sha}; merge.intoBranch/fromBranch null; made missing; where.branches with the
// team branch first and never main; codeShown false; a diff refused with remote_refused. Plus the faults round 1 found,
// flipped in round 2 (card 460) after the fixes (core f669165, shell 7ec603b): F1 a merge made on main now reads
// "into main" (was "into team/alpha"); F2 a commit only on feature/x now reads "On feature/x (not a team or member
// branch)" (was "a branch that held it was deleted"), from where.otherBranches.
import { flushPromises, mount } from '@vue/test-utils';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';
import CommitView from '../../src/renderer/src/components/CommitView.vue';
import { closeCommit, gitRequest, mergeLine, whereLines } from '../../src/renderer/src/commitView';
import type { CommitFound, CommitShow, YawbleApi } from '../../src/shared/api';

const real = (name: string): CommitShow => JSON.parse(readFileSync(path.join(__dirname, 'fixtures', 'card445', `${name}.json`), 'utf8'));

function core(answer: CommitShow, diff?: (params: any) => unknown) {
  const calls: { method: string; params: any }[] = [];
  const api: YawbleApi = {
    getInfo: async () => { throw new Error('no'); },
    getAvailableBackends: async () => [],
    call: async (method: string, params?: Record<string, unknown>) => {
      calls.push({ method, params });
      if (method === 'git.commitShow') return structuredClone(answer) as any;
      if (method === 'git.commitDiff') {
        if (!diff) throw new Error(`unexpected ${method}`);
        return diff(params) as any;
      }
      throw new Error(`unexpected ${method}`);
    },
    onEvent: () => () => {},
    openTerminal: async () => {},
    pickFolder: async () => null,
  };
  return { api, calls };
}

const wrappers: { unmount: () => void }[] = [];
afterEach(() => { closeCommit(); gitRequest.value = null; while (wrappers.length) wrappers.pop()!.unmount(); });

async function view(answer: CommitShow, diff?: (params: any) => unknown) {
  const c = core(answer, diff);
  const w = mount(CommitView, { props: { api: c.api, teams: ['Alpha'], sha: (answer as any).sha }, attachTo: document.body });
  wrappers.push(w);
  await flushPromises();
  return { w, ...c };
}
const whereTexts = (w: any) => w.findAll('[data-test=commit-where] li').map((l: any) => l.text());

const ALL = ['in-main-not-pushed', 'in-main-pushed', 'team-branch-only', 'team-branch-only-pushed', 'member-branch-only', 'merge-unnamed',
  'f1-merge-on-main', 'f2-own-branch', 'deleted-branch', 'not-found'];

describe('card 445: the real core\'s answers', () => {
  it.each(ALL)('%s renders without "null" or "undefined"', async (name) => {
    const { w } = await view(real(name));
    expect(w.find('[data-test=commit-error]').exists()).toBe(false);
    expect(w.text()).not.toMatch(/\bnull\b|\bundefined\b|NaN|\[object/);
  });

  it('the fixtures are the contract: not found has only found, goneReason and sha; branches never list main, team first', () => {
    expect(Object.keys(real('not-found')).sort()).toEqual(['found', 'goneReason', 'sha']);
    for (const name of ALL.filter((n) => n !== 'not-found')) {
      const c = real(name) as CommitFound;
      expect(c.where.branches).not.toContain('main');
      const team = c.where.branches.indexOf('team/alpha');
      if (team >= 0) expect(team).toBe(0);
    }
    expect((real('in-main-not-pushed') as CommitFound).where.branches).toEqual(['team/alpha', 'members/alpha/ada']);
  });

  it('not found: says it is gone and why, from the core\'s three fields only', async () => {
    const answer = real('not-found');
    const { w } = await view(answer);
    expect(w.find('[data-test=commit-gone]').exists()).toBe(true);
    expect(w.find('[data-test=commit-gone-reason]').text()).toBe((answer as any).goneReason);
    expect(w.find('[data-test=commit-what]').exists()).toBe(false);
    expect(w.find('[data-test=commit-copy]').exists()).toBe(true);
  });

  it('where, in plain words: main and not pushed, then on origin', async () => {
    expect(whereTexts((await view(real('in-main-not-pushed'))).w)).toEqual(['In main', 'Not pushed: only on this computer']);
    expect(whereTexts((await view(real('in-main-pushed'))).w)).toEqual(['In main', 'On origin']);
  });

  it('where: the team branch only (pushed or not), and only a member\'s branch', async () => {
    expect(whereTexts((await view(real('team-branch-only'))).w)).toEqual(['In the team branch only, not in main yet', 'Not pushed: only on this computer']);
    expect(whereTexts((await view(real('team-branch-only-pushed'))).w)).toEqual(['In the team branch only, not in main yet', 'On origin']);
    const ada = await view(real('member-branch-only'));
    expect(whereTexts(ada.w)).toEqual(["Only on Ada's branch", 'Not pushed: only on this computer']);
    expect(ada.w.find('[data-test=commit-made]').text()).toBe('Ada');
  });

  it('a merge nobody can name: no "null", no Made by, the count', async () => {
    const c = real('merge-unnamed') as CommitFound;
    expect(c.merge).toEqual({ intoBranch: null, fromBranch: null, commitCount: 1 });
    expect('made' in c).toBe(false);
    expect(mergeLine(c)).toBe('Merged another branch, 1 commit');
    const { w } = await view(c);
    expect(w.find('[data-test=commit-merge]').text()).toBe('Merged another branch, 1 commit');
    expect(w.find('[data-test=commit-made]').exists()).toBe(false);
    expect(w.text()).toContain('against its first parent');
    // No branch holds it: Show its branch is off.
    expect(w.find('[data-test=commit-branch]').attributes('disabled')).toBeDefined();
  });

  it('F1 (core, fixed in card 458): a merge made on main with git\'s default message reads "into main"', async () => {
    const c = real('f1-merge-on-main') as CommitFound;
    expect(c.title).toBe("Merge branch 'feature/login'");
    expect(c.merge?.intoBranch).toBe('main');
    expect(mergeLine(c)).toBe('Merged feature/login into main, 1 commit'); // round 1 said "into team/alpha"
    const { w } = await view(c);
    expect(w.find('[data-test=commit-merge]').text()).toBe('Merged feature/login into main, 1 commit');
  });

  it('F2 (fixed in cards 458, 459): a commit only on feature/x reads "On feature/x (not a team or member branch)"', async () => {
    const c = real('f2-own-branch') as CommitFound;
    expect(c.where.branches).toEqual([]);
    expect(c.where.otherBranches).toEqual(['feature/x']);
    const own = await view(c);
    expect(whereTexts(own.w)[0]).toBe('On feature/x (not a team or member branch)'); // round 1 said "deleted"
    expect(own.w.find('[data-test=commit-branch]').attributes('disabled')).toBeDefined();
    // A member branch that really was deleted keeps the deleted wording: the two now read differently.
    const g = real('deleted-branch') as CommitFound;
    expect(g.where.otherBranches).toEqual([]);
    const gone = await view(g);
    expect(whereTexts(gone.w)[0]).toBe('On no branch (a branch that held it was deleted)');
  });

  it('F2: otherBranches is in every found answer from the real core, an array, never base/team/member branches', () => {
    for (const name of ALL.filter((n) => n !== 'not-found')) {
      const c = real(name) as CommitFound;
      expect(Array.isArray(c.where.otherBranches), name).toBe(true);
      for (const b of c.where.otherBranches!) expect(b === 'main' || b === 'team/alpha' || b.startsWith('members/'), `${name}: ${b}`).toBe(false);
    }
  });

  it('no write actions: only Copy ID, Compare with main and Show its branch', async () => {
    const { w } = await view(real('team-branch-only'));
    expect(w.findAll('[data-test=commit-actions] button').map((b) => b.text())).toEqual(['Copy ID', 'Compare with main', 'Show its branch']);
    // Every button in the view (the files' too): none writes.
    for (const b of w.findAll('button')) expect(b.text()).not.toMatch(/revert|cherry|reset|rebase|push|delete|merge|checkout/i);
  });

  it('Show its branch / Compare with main ask the Git dialog with the real branches', async () => {
    const { w } = await view(real('team-branch-only'));
    await w.find('[data-test=commit-branch]').trigger('click');
    expect(gitRequest.value).toMatchObject({ team: 'Alpha', focus: 'branch', branch: 'members/alpha/ada' });
    const m = await view(real('in-main-pushed'));
    await m.w.find('[data-test=commit-compare]').trigger('click');
    expect(gitRequest.value).toMatchObject({ team: 'Alpha', focus: 'compare', branch: null });
  });
});

describe('card 445: the Cloud Link with members\' code hidden', () => {
  it('codeShown false (as the core shapes it): files listed, nothing opens, no commitDiff call', async () => {
    const answer = { ...(real('team-branch-only') as CommitFound), codeShown: false };
    const { w, calls } = await view(answer, () => { throw new Error('must not be called'); });
    expect(w.find('[data-test=commit-code-hidden]').exists()).toBe(true);
    expect(w.findAll('[data-test=commit-file]').map((f) => f.attributes('data-path'))).toEqual(['team.txt']);
    expect(w.find('button.file').exists()).toBe(false);
    await w.find('[data-test=commit-file-open]').trigger('click');
    await flushPromises();
    expect(calls.filter((c) => c.method === 'git.commitDiff')).toEqual([]);
  });

  it('a diff the core refuses with remote_refused shows the core\'s reason', async () => {
    const reason = "members' code is not shown to cloud viewers; the person can turn it on in the app on the computer (Settings > Cloud Link)";
    const { w } = await view(real('team-branch-only'), () => { throw Object.assign(new Error(reason), { code: 'remote_refused' }); });
    await w.find('[data-test=commit-file-open]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=commit-diff-error]').text()).toBe(`The diff could not be read: ${reason}`);
  });
});

describe('card 445: wording helpers on edge shapes', () => {
  const base = real('team-branch-only') as CommitFound;
  it('mergeLine: into known, from unknown; from a member', () => {
    expect(mergeLine({ ...base, isMerge: true, merge: { intoBranch: 'team/alpha', fromBranch: null, commitCount: 2 } } as any))
      .toBe('Merged another branch into team/alpha, 2 commits');
    expect(mergeLine({ ...base, isMerge: true, merge: { intoBranch: null, fromBranch: 'members/alpha/ada', fromMember: 'Ada', commitCount: 3 } } as any))
      .toBe("Merged Ada's branch, 3 commits");
  });
  it('whereLines: tags read "Included in release"', () => {
    expect(whereLines({ ...base, where: { ...base.where, tags: ['v2.0'] } }).map((l) => l.text)).toContain('Included in release v2.0');
  });
});
