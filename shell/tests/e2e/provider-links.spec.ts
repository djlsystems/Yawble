// The Git dialog's "Open on …" links and Team settings > Repositories' provider fields in a real
// browser (headless Chrome) against the FAKE core, answering on the core's contract:
//   git.webLink {teamId, team, repo?, kind, commit?|branch?|file?|from?+to?|pr?} -> { url | null }
//   teams.repos main / extras + provider (the override, null when detecting), providerDetected, linkPattern, prSupported, prUnavailable
//   teams.setRepo {teamId, name?, provider|null, linkPattern|null} -> the entry
// The fake answers null for a commit not on origin (DeveloperBen's unpushed one), like the core.
//  - GitLab (detected): base/team/member branch and commit links, compare, the conflicting file; no link for the unpushed
//    commit; the PR line; every git.webLink call carries teamId + team and no repo for the main one.
//  - The Bitbucket extra tab: its links are asked with repo; the PR line is the core's "not supported" text.
//  - Settings: detected GitLab shown; Provider none + a link pattern saved through teams.setRepo; the Git dialog then
//    says "Open in the browser" and uses the pattern's urls; "As detected" + blank clears (provider null, linkPattern null).
//   npx playwright test provider-links     (PROVIDER_LINKS_SKIP_BUILD=1 reuses out/provider-links/renderer)
import { expect, test, type Browser, type Page } from './phone/fixtures';
import path from 'node:path';
import { answers } from './phone/fake-core';
import { buildRenderer, fakeRemote } from './phone/server';

test.use({ channel: 'chrome', headless: true, actionTimeout: 8_000 });
test.setTimeout(90_000);

const BUILT = path.resolve(__dirname, '..', '..', 'out', 'provider-links', 'renderer');
const BASE = '1111111111aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';
const TEAM = '2222222222bbbbbbbbbbbbbbbbbbbbbbbbbbbbbb';
const ADA = '3333333333cccccccccccccccccccccccccccccc';
const BEN = '4444444444dddddddddddddddddddddddddddddd'; // not on origin
const TOOLS = '5555555555eeeeeeeeeeeeeeeeeeeeeeeeeeeeee';
const GL = 'https://gitlab.corp.example/grp/sub/payments';
const BB = 'https://bitbucket.org/acme/tools';
const BB_WHY = 'pull requests are not supported for Bitbucket from here (only GitHub through gh, GitLab through glab and Azure DevOps through az repos); tools\'s origin is on bitbucket.org. Push the branch and open the pull request (or merge request) on bitbucket.org';

type Info = { provider: string | null; providerDetected: string; linkPattern: string | null; prSupported: boolean; prUnavailable: string | null };
const state: { main: Info; tools: Info; calls: { method: string; params: any }[] } = { main: null!, tools: null!, calls: [] };
function reset() {
  state.main = { provider: null, providerDetected: 'gitlab', linkPattern: null, prSupported: true, prUnavailable: null };
  state.tools = { provider: null, providerDetected: 'bitbucket', linkPattern: null, prSupported: false, prUnavailable: BB_WHY };
  state.calls = [];
}
reset();

const status = (repo?: string) => ({
  repoPath: repo ? 'C:/repos/tools' : 'C:/repos/payments', managed: false, baseBranch: 'main', checkedOut: 'main', dirty: false,
  origin: repo ? 'git@bitbucket.org:acme/tools.git' : 'git@gitlab.corp.example:grp/sub/payments.git',
  baseSha: repo ? TOOLS : BASE, originBaseSha: repo ? TOOLS : BASE, baseAheadOfOrigin: 0, baseBehindOrigin: 0,
  teamBranch: 'team/payments', teamSha: repo ? null : TEAM, teamAheadOfBase: repo ? 0 : 2, teamBehindBase: repo ? 0 : 1,
  merge: repo ? 'none' : 'conflict', conflictFiles: repo ? [] : ['src/refund.ts'],
  members: repo ? [] : [
    { branch: 'members/payments/developerada', sha: ADA, aheadOfBase: 1, inTeamBranch: true },
    { branch: 'members/payments/developerben', sha: BEN, aheadOfBase: 1, inTeamBranch: false },
  ],
  oldBranches: [], blockingBranches: [],
});

/** A small core: the provider's layout, or the pattern's templates (picked by their exact placeholders), null for BEN's commit. */
function webLink(p: any): { url: string | null } {
  const info = p.repo === 'tools' ? state.tools : state.main;
  const base = p.repo === 'tools' ? BB : GL;
  const kind: string = p.kind;
  if ((kind === 'commit' || kind === 'file') && p.commit === BEN) return { url: null };
  if (info.linkPattern) {
    const want: Record<string, string[]> = { commit: ['commit'], branch: ['branch'], file: ['commit', 'file'], compare: ['from', 'to'], pr: ['pr'] };
    for (const t of info.linkPattern.split(/\s+/).filter(Boolean)) {
      const used = [...t.matchAll(/\{(\w+)\}/g)].map((m) => m[1]).filter((u) => u !== 'base' && u !== 'path').sort();
      if (used.join() !== [...want[kind]].sort().join()) continue;
      return { url: t.replace(/\{(\w+)\}/g, (_m, k) => (k === 'base' ? base : k === 'path' ? new URL(base).pathname.slice(1) : String(p[k]))) };
    }
  }
  const provider = info.provider ?? info.providerDetected;
  if (provider === 'gitlab') {
    const u = { commit: `${base}/-/commit/${p.commit}`, branch: `${base}/-/tree/${p.branch}`, file: `${base}/-/blob/${p.commit}/${p.file}`, compare: `${base}/-/compare/${p.from}...${p.to}`, pr: `${base}/-/merge_requests/${p.pr}` } as Record<string, string>;
    return { url: u[kind] ?? null };
  }
  if (provider === 'bitbucket') {
    const u = { commit: `${base}/commits/${p.commit}`, branch: `${base}/src/${p.branch}` } as Record<string, string>;
    return { url: u[kind] ?? null };
  }
  return { url: null };
}

const OVERRIDES: Record<string, (p?: any) => unknown> = {
  'git.status': (p) => (p?.teamId == null ? { installed: true, version: '2.50', path: 'C:/fake/git.exe' } : status(p?.repo)),
  'git.webLink': (p) => { state.calls.push({ method: 'git.webLink', params: p }); return webLink(p); },
  'teams.repos': () => ({
    main: { name: 'payments', path: 'C:/repos/payments', baseBranch: 'main', origin: 'git@gitlab.corp.example:grp/sub/payments.git', managed: false, ...state.main },
    extras: [{ name: 'tools', path: 'C:/repos/tools', baseBranch: 'main', writable: true, source: 'git@bitbucket.org:acme/tools.git', ...state.tools }],
  }),
  'teams.setRepo': (p) => {
    state.calls.push({ method: 'teams.setRepo', params: p });
    const target = p?.name === 'tools' ? state.tools : state.main;
    target.provider = p?.provider ?? null;
    target.linkPattern = p?.linkPattern || null;
    const eff = target.provider ?? target.providerDetected;
    target.prSupported = eff === 'gitlab' || eff === 'github' || eff === 'azuredevops';
    target.prUnavailable = target.prSupported ? null : `pull requests are not supported for ${eff === 'none' ? 'gitlab.corp.example' : eff} from here`;
    return { name: p?.name === 'tools' ? 'tools' : 'payments', ...target };
  },
  'teams.env': () => ({}),
};
const saved: Record<string, unknown> = {};

test.beforeAll(async () => {
  test.setTimeout(600_000);
  for (const k of Object.keys(OVERRIDES)) { saved[k] = (answers as any)[k]; (answers as any)[k] = OVERRIDES[k]; }
  if (!process.env.PROVIDER_LINKS_SKIP_BUILD) await buildRenderer(BUILT);
});
test.afterAll(() => { for (const k of Object.keys(saved)) { if (saved[k] === undefined) delete (answers as any)[k]; else (answers as any)[k] = saved[k]; } });
test.beforeEach(() => reset());

async function openTeam(browser: Browser) {
  const s = fakeRemote(BUILT);
  s.core.setAccess('full');
  s.core.setAsks(false);
  s.core.setNeedsYou(false);
  const ctx = await browser.newContext({ viewport: { width: 1280, height: 800 } });
  await s.attach(ctx);
  const page = await ctx.newPage();
  const errors: string[] = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.goto(`${s.url}#team=1`);
  await expect(page.locator('[data-test=team-title]')).toBeVisible({ timeout: 30_000 });
  return { page, ctx, errors };
}
async function openFromTeam(page: Page, id: string) {
  const b = page.locator(`[data-test=${id}]`).first();
  if (!(await b.isVisible().catch(() => false))) {
    await page.locator('[data-test=team-more]').click();
    await page.locator(`[data-test=team-more-menu] [data-test=${id}]`).click();
  } else await b.click();
}
async function openGit(page: Page) {
  await openFromTeam(page, 'open-git');
  await expect(page.locator('[data-test=git-merge-state]')).toBeVisible({ timeout: 15_000 });
}
const href = (page: Page, sel: string) => page.locator(`[data-test="${sel}"]`).getAttribute('href');

test('Git dialog: GitLab links for what is on origin, none for the unpushed commit; the Bitbucket extra says no pull requests', async ({ browser }) => {
  const { page, ctx, errors } = await openTeam(browser);
  await openGit(page);
  const base = page.locator('[data-test=git-link-base-branch]');
  await expect(base).toHaveText('Open on GitLab');
  expect(await base.getAttribute('href')).toBe(`${GL}/-/tree/main`);
  expect(await base.getAttribute('target')).toBe('_blank');
  expect(await href(page, 'git-link-base-commit')).toBe(`${GL}/-/commit/${BASE}`);
  expect(await href(page, 'git-link-team-commit')).toBe(`${GL}/-/commit/${TEAM}`);
  expect(await href(page, 'git-link-team-branch')).toBe(`${GL}/-/tree/team/payments`);
  await expect(page.locator('[data-test=git-link-compare]')).toHaveText('Compare with main');
  expect(await href(page, 'git-link-compare')).toBe(`${GL}/-/compare/main...team/payments`);
  expect(await href(page, 'git-link-file-src/refund.ts')).toBe(`${GL}/-/blob/${TEAM}/src/refund.ts`);
  expect(await href(page, 'git-link-branch-members/payments/developerada')).toBe(`${GL}/-/tree/members/payments/developerada`);
  expect(await href(page, 'git-link-commit-members/payments/developerada')).toBe(`${GL}/-/commit/${ADA}`);
  // Ben's commit is not on origin: no link, the short sha as text.
  await expect(page.locator('[data-test="git-link-commit-members/payments/developerben"]')).toHaveCount(0);
  await expect(page.locator('[data-test=git-members]')).toContainText(BEN.slice(0, 10));
  await expect(page.locator('[data-test=git-prs]')).toContainText('Pull requests on GitLab');

  // The contract: every git.webLink call has teamId and team, a kind, and no repo for the main repository.
  const linkCalls = state.calls.filter((c) => c.method === 'git.webLink');
  expect(linkCalls.length).toBeGreaterThan(0);
  for (const c of linkCalls) {
    expect(c.params.teamId).toBe(1);
    expect(typeof c.params.team).toBe('string');
    expect(['commit', 'branch', 'file', 'compare', 'pr']).toContain(c.params.kind);
    expect('repo' in c.params).toBe(false);
  }
  const compare = linkCalls.find((c) => c.params.kind === 'compare')!;
  expect(compare.params).toMatchObject({ from: 'main', to: 'team/payments' });
  expect(linkCalls.find((c) => c.params.kind === 'file')!.params).toMatchObject({ commit: TEAM, file: 'src/refund.ts' });

  // The Bitbucket extra: asked with repo, Bitbucket's links, and the core's "not supported" text.
  state.calls = [];
  await page.locator('[data-test=git-repo-tools]').click();
  await expect(page.locator('[data-test=git-link-base-branch]')).toHaveText('Open on Bitbucket');
  expect(await href(page, 'git-link-base-commit')).toBe(`${BB}/commits/${TOOLS}`);
  await expect(page.locator('[data-test=git-prs]')).toHaveText(BB_WHY);
  expect(state.calls.filter((c) => c.method === 'git.webLink').every((c) => c.params.repo === 'tools')).toBe(true);
  expect(errors).toEqual([]);
  await ctx.close();
});

test('Settings > Repositories: the override and link pattern save through teams.setRepo and are used; As detected clears', async ({ browser }) => {
  const { page, ctx, errors } = await openTeam(browser);
  await openFromTeam(page, 'open-manage');
  await page.locator('[data-test=manage-tab-repos]').click();
  await expect(page.locator('[data-test=repo-main-provider-detected]')).toHaveText('GitLab');
  await expect(page.locator('[data-test=repo-tools-provider-detected]')).toHaveText('Bitbucket');
  await expect(page.locator('[data-test=repo-main-link-pattern-hint]')).toContainText('compare {from} {to}');
  await expect(page.locator('[data-test=repo-main-provider-save]')).toBeDisabled();

  const pattern = 'https://mirror.example/{path}/c/{commit} {base}/b/{branch}';
  await page.locator('[data-test=repo-main-provider-select]').selectOption('none');
  await page.locator('[data-test=repo-main-link-pattern]').fill(pattern);
  await page.locator('[data-test=repo-main-provider-save]').click();
  await expect.poll(() => state.calls.filter((c) => c.method === 'teams.setRepo').length).toBe(1);
  expect(state.calls.find((c) => c.method === 'teams.setRepo')!.params).toEqual({ teamId: 1, provider: 'none', linkPattern: pattern });
  // Read back: the fields show what was saved.
  await expect(page.locator('[data-test=repo-main-provider-select]')).toHaveValue('none');
  await expect(page.locator('[data-test=repo-main-link-pattern]')).toHaveValue(pattern);
  await expect(page.locator('[data-test=repo-main-provider-save]')).toBeDisabled();

  // The Git dialog uses it: "Open in the browser", the pattern's urls, and no compare link (no template for it).
  const second = await ctx.newPage();
  await second.goto(page.url());
  await expect(second.locator('[data-test=team-title]')).toBeVisible({ timeout: 30_000 });
  await openGit(second);
  await expect(second.locator('[data-test=git-link-base-branch]')).toHaveText('Open in the browser');
  expect(await href(second, 'git-link-base-branch')).toBe(`${GL}/b/main`);
  expect(await href(second, 'git-link-base-commit')).toBe(`https://mirror.example/grp/sub/payments/c/${BASE}`);
  await expect(second.locator('[data-test=git-link-compare]')).toHaveCount(0);
  await expect(second.locator('[data-test=git-prs]')).toContainText('not supported');
  await second.close();

  // As detected + blank: provider null, linkPattern null.
  await page.locator('[data-test=repo-main-provider-select]').selectOption('');
  await page.locator('[data-test=repo-main-link-pattern]').fill('');
  await page.locator('[data-test=repo-main-provider-save]').click();
  await expect.poll(() => state.calls.filter((c) => c.method === 'teams.setRepo').length).toBe(2);
  expect(state.calls.filter((c) => c.method === 'teams.setRepo')[1].params).toEqual({ teamId: 1, provider: null, linkPattern: null });
  // The extra saves with its name.
  await page.locator('[data-test=repo-tools-provider-select]').selectOption('gitea');
  await page.locator('[data-test=repo-tools-provider-save]').click();
  await expect.poll(() => state.calls.filter((c) => c.method === 'teams.setRepo').length).toBe(3);
  expect(state.calls.filter((c) => c.method === 'teams.setRepo')[2].params).toEqual({ teamId: 1, name: 'tools', provider: 'gitea', linkPattern: null });
  expect(errors).toEqual([]);
  await ctx.close();
});
