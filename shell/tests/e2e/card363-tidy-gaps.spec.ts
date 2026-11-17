// Card 363 (verify B067, cards 361 + 362): gap tests for the Git dialog's "Tidy…" against a plan shaped exactly like the
// CORE's git.tidyPlan answer (UiRpc.TidyPlanJson: repo "main", baseBranch, notes, proof on every class, commits on moved
// and origin-only rows) instead of fake-core's own plan. The fixture is the card's fixture team: a cherry-picked member
// branch, a member clone reset to main (moved), a scratch/ branch, a merged branch, an origin-only branch, a working one.
//  - the yes of a Realign is bound to the commits shown (expectOld / expectNew, which the core checks: code "stale")
//  - a moved row shows the commits a realign drops (the core sends them), and the plan's notes are shown
//  - deleting a contained branch sends no unmergedOk; the main repository goes as "main" or no repo
//  - 1280 x 800 and 390 x 844: nothing scrolls sideways
//   npx playwright test card363-tidy-gaps     (screenshots: $YAWBLE_TEAM_DOCS/B067/card363-*.png)
import { expect, test, type Browser, type Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import path from 'node:path';
import { answers } from './phone/fake-core';
import { buildRenderer, fakeRemote } from './phone/server';

test.use({ channel: 'chrome', headless: true, actionTimeout: 8_000 });
test.setTimeout(90_000);

const OUT = path.resolve(__dirname, '..', '..', 'test-results');
const SHOTS = process.env.YAWBLE_TEAM_DOCS ? path.join(process.env.YAWBLE_TEAM_DOCS, 'B067') : OUT;
const BUILT = path.resolve(__dirname, '..', '..', 'out', 'card363', 'renderer');
const shot = (page: Page, name: string) => { mkdirSync(SHOTS, { recursive: true }); return page.screenshot({ path: path.join(SHOTS, `card363-${name}.png`), fullPage: true }); };

const TEAM_SHA = '5bbb6ba450df8fe26f82ff2d9171b7d234fd2db1';
const CLONE_SHA = 'e0f2a18c0ffee1234567890abcdef0123456789a';
const NOTE = "TesterKofi's clone was not read: its .git is a file (a gitdir pointer)";
/** What UiRpc.TidyPlanJson gives for the fixture team (field names and classes from TeamRepos.Tidy.cs). */
const corePlan = () => ({
  repos: [{
    repo: 'main', baseBranch: 'main', notes: [NOTE],
    branches: [
      { name: 'members/fixture/developermarlo', where: ['local', 'origin', 'clone:DeveloperMarlo'], class: 'moved', member: 'DeveloperMarlo',
        teamSha: TEAM_SHA, cloneSha: CLONE_SHA, commits: [{ sha: 'aa11bb22cc33dd44ee55ff6600112233445566aa', subject: 'Retry the upload three times' }],
        proof: "the team's copy (5bbb6ba450) has 1 commit(s) DeveloperMarlo's clone (e0f2a18c0f) does not have and that are not merged into main: the clone was reset or rebased; nothing is lost by realigning it (git cherry main: - aa11bb22cc)" },
      { name: 'members/fixture/developerines', where: ['local', 'origin', 'clone:DeveloperInes'], class: 'working', member: 'DeveloperInes',
        teamSha: '1111111111aaaaaaaaaa', cloneSha: '1111111111aaaaaaaaaa', proof: "DeveloperInes's working branch, checked out in its clone: in use, kept" },
      { name: 'members/fixture/testerkofi', where: ['local', 'origin'], class: 'contained', teamSha: '2222222222bbbbbbbbbb',
        proof: 'git cherry main members/fixture/testerkofi: - 3c4d5e6f70 Fix the refund rounding (an equivalent commit is in main)' },
      { name: 'members/fixture/old-cleo', where: ['local'], class: 'merged', teamSha: '3333333333cccccccccc', proof: 'in main' },
      { name: 'feature/origin-only-thing', where: ['origin'], class: 'origin-only', teamSha: '4444444444dddddddddd',
        commits: [{ sha: '4444444444dddddddddd0000000000000000000', subject: 'Try a new cache' }], proof: 'only on origin, nobody has it here; 1 commit(s) not in main: needs a look' },
      { name: 'scratch/try-a-thing', where: ['clone:DeveloperInes'], class: 'scratch', member: 'DeveloperInes', cloneSha: '5555555555eeeeeeeeee',
        proof: "only in DeveloperInes's clone; not in the team repository" },
    ],
  }],
});

const saved: Record<string, (typeof answers)[string]> = {};
const seen: { method: string; p: any }[] = [];
test.beforeAll(async () => {
  test.setTimeout(600_000);
  if (!process.env.CARD363_SKIP_BUILD) await buildRenderer(undefined, BUILT);
});
test.beforeEach(() => {
  seen.length = 0;
  for (const m of ['git.tidyPlan', 'git.deleteBranches', 'git.branchRealign']) saved[m] = answers[m];
  answers['git.tidyPlan'] = () => corePlan();
  answers['git.deleteBranches'] = (p) => {
    seen.push({ method: 'git.deleteBranches', p });
    return p?.confirm ? { result: `Deleted ${(p.branches ?? []).join(', ')}.` } : { plan: (p.branches ?? []).map((b: string) => `Delete ${b} here.`).join('\n'), token: 'tok', empty: false };
  };
  answers['git.branchRealign'] = (p) => {
    seen.push({ method: 'git.branchRealign', p });
    if (p?.member === 'DeveloperMarlo' && p?.refuse) throw Object.assign(new Error('x'), { code: 'running' });
    return { oldSha: TEAM_SHA, newSha: CLONE_SHA, branch: 'members/fixture/developermarlo', repo: 'main', member: p?.member, changed: true };
  };
});
test.afterEach(() => { for (const [m, f] of Object.entries(saved)) answers[m] = f; });

async function tidyAt(browser: Browser, w: number, h: number) {
  const s = fakeRemote(BUILT);
  s.core.setAccess('full'); s.core.setAsks(false); s.core.setNeedsYou(false); s.core.resetTidy();
  const mobile = w < 760;
  const ctx = await browser.newContext({ viewport: { width: w, height: h }, isMobile: mobile, hasTouch: mobile, deviceScaleFactor: mobile ? 3 : 1 });
  await s.attach(ctx);
  const page = await ctx.newPage();
  const errors: string[] = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.goto(`${s.url}#team=1`);
  await expect(page.locator('[data-test=team-title]')).toBeVisible({ timeout: 30_000 });
  if (await page.locator('[data-test=team-more]').count()) {
    await page.locator('[data-test=team-more]').click();
    await page.locator('[data-test=team-more-menu] [data-test=open-git]').click();
  } else await page.locator('[data-test=open-git]').click();
  await page.locator('[data-test=git-tidy]').click();
  const tidy = page.locator('[data-test=tidy]');
  await expect(tidy.locator('[data-test=tidy-group-moved]')).toBeVisible({ timeout: 15_000 });
  return { page, ctx, errors, tidy };
}

async function sideways(page: Page) {
  return page.evaluate(() => {
    const vw = innerWidth;
    const past = [...document.querySelectorAll('[role=dialog] *')].filter((e) => !e.closest('pre') && (() => { const r = e.getBoundingClientRect(); return r.width > 0 && r.right > vw + 0.5; })())
      .map((e) => `${e.tagName}[${e.getAttribute('data-test') ?? ''}]`);
    return { vw, doc: document.documentElement.scrollWidth, past: past.slice(0, 10) };
  });
}

test('1280 x 800, core-shaped plan: every class shown, contained proof, contained delete sends no unmergedOk', async ({ browser }) => {
  const { page, ctx, errors, tidy } = await tidyAt(browser, 1280, 800);
  for (const cls of ['merged', 'contained', 'moved', 'own-work', 'working', 'origin-only', 'scratch'].filter((c) => c !== 'own-work'))
    await expect(tidy.locator(`[data-test=tidy-group-${cls}]`)).toBeVisible();
  await expect(tidy.locator('[data-test=tidy-proof]')).toContainText('git cherry main members/fixture/testerkofi');
  await expect(tidy.locator('[data-test=tidy-group-working]')).toContainText("DeveloperInes's working branch, not a leftover");
  await expect(tidy.locator('[data-test=tidy-delete-selected]')).toContainText('Delete selected (2)');
  const m = await sideways(page);
  expect(m.doc).toBeLessThanOrEqual(m.vw);
  expect(m.past).toEqual([]);
  await shot(page, 'core-plan-1280');
  await tidy.locator('[data-test=tidy-delete-selected]').click();
  await tidy.locator('[data-test=tidy-delete-yes]').click();
  await expect(tidy.locator('[data-test=tidy-report]')).toBeVisible();
  const dels = seen.filter((s) => s.method === 'git.deleteBranches');
  expect(dels.length).toBeGreaterThanOrEqual(2);
  for (const d of dels) {
    expect(d.p.unmergedOk ?? d.p.unmerged_ok, 'contained needs no unmergedOk').toBeUndefined();
    expect([undefined, 'main']).toContain(d.p.repo);
  }
  // old-cleo is only here, testerkofi also on origin: one request each (remote false / true), plan then confirm.
  const asked = dels.filter((d) => !d.p.confirm).map((d) => `${d.p.branches.join(',')}:${d.p.remote}`).sort();
  expect(asked).toEqual(['members/fixture/old-cleo:false', 'members/fixture/testerkofi:true']);
  const kept = await tidy.locator('[data-test=tidy-kept]').allInnerTexts();
  await shot(page, 'core-plan-after-delete-1280');
  expect(kept.some((k) => k.startsWith('members/fixture/developerines') && k.includes('working branch'))).toBe(true);
  expect(errors).toEqual([]);
  await ctx.close();
});

test('the Realign yes is bound to the commits shown (expectOld / expectNew go to git.branchRealign)', async ({ browser }) => {
  const { page, ctx, tidy } = await tidyAt(browser, 1280, 800);
  await tidy.locator('[data-test="tidy-row-members/fixture/developermarlo"] [data-test=tidy-realign]').click();
  await expect(tidy.locator('[data-test=tidy-realign-shas]')).toHaveText('5bbb6ba450 → e0f2a18c0f');
  await shot(page, 'realign-confirm-1280');
  await tidy.locator('[data-test=tidy-realign-yes]').click();
  await expect(tidy.locator('[data-test=tidy-done]')).toContainText('Realigned members/fixture/developermarlo');
  const call = seen.find((s) => s.method === 'git.branchRealign');
  expect(call?.p.repo).toBe('main');
  expect(call?.p.member).toBe('DeveloperMarlo');
  expect(call?.p.expectOld, 'expectOld: the team commit the person saw').toBe(TEAM_SHA);
  expect(call?.p.expectNew, 'expectNew: the clone commit the person saw').toBe(CLONE_SHA);
  await ctx.close();
});

test('a moved row shows the commits a realign drops, with the core proof', async ({ browser }) => {
  const { page, ctx, tidy } = await tidyAt(browser, 1280, 800);
  const row = tidy.locator('[data-test="tidy-row-members/fixture/developermarlo"]');
  await shot(page, 'moved-row-1280');
  await expect(row, 'the dropped commit').toContainText('Retry the upload three times');
  await expect(row, 'the core proof').toContainText('nothing is lost by realigning it');
  await ctx.close();
});

test("the plan's notes (a clone that could not be read) are shown", async ({ browser }) => {
  const { ctx, tidy } = await tidyAt(browser, 1280, 800);
  await expect(tidy).toContainText(NOTE);
  await ctx.close();
});

test('the core refusal "running" is shown as Refused with the core message', async ({ browser }) => {
  answers['git.branchRealign'] = (p) => {
    seen.push({ method: 'git.branchRealign', p });
    throw Object.assign(new Error('DeveloperMarlo is running (run 42), so its clone may still move: realign after the run ends. Nothing changed.'), { code: 'running' });
  };
  const { page, ctx, tidy } = await tidyAt(browser, 1280, 800);
  await tidy.locator('[data-test="tidy-row-members/fixture/developermarlo"] [data-test=tidy-realign]').click();
  await tidy.locator('[data-test=tidy-realign-yes]').click();
  await expect(tidy.locator('[data-test=tidy-realign-error]')).toHaveText('Refused: DeveloperMarlo is running (run 42), so its clone may still move: realign after the run ends. Nothing changed.');
  await shot(page, 'realign-refused-1280');
  await ctx.close();
});

test('390 x 844, core-shaped plan: plan, realign and report do not scroll sideways', async ({ browser }) => {
  const { page, ctx, errors, tidy } = await tidyAt(browser, 390, 844);
  let m = await sideways(page);
  expect(m.doc).toBeLessThanOrEqual(m.vw);
  expect(m.past).toEqual([]);
  await shot(page, 'core-plan-390');
  await tidy.locator('[data-test="tidy-row-members/fixture/developermarlo"] [data-test=tidy-realign]').click();
  await tidy.locator('[data-test=tidy-realign-confirm]').scrollIntoViewIfNeeded();
  m = await sideways(page);
  expect(m.past).toEqual([]);
  await shot(page, 'realign-390');
  await tidy.locator('[data-test=tidy-realign-yes]').click();
  await expect(tidy.locator('[data-test=tidy-report]')).toBeVisible();
  m = await sideways(page);
  expect(m.doc).toBeLessThanOrEqual(m.vw);
  expect(m.past).toEqual([]);
  await shot(page, 'after-realign-390');
  expect(errors).toEqual([]);
  await ctx.close();
});

// ---- Re-check after DeveloperInes's fix d0347c4 (F2 stale flow, F3 confirm + origin-only/own-work, F4 notes above the list).
const NEW_TEAM = '6666666666ffffffffff0000000000000000aaaa';
const NEW_CLONE = '7777777777000000000011111111111111112222';
/** The core's stale refusal text (TeamRepos.Tidy.cs GitBranchRealignAsync), as the window gets it. */
const STALE = 'members/fixture/developermarlo changed since it was shown (now: team 6666666666, clone 7777777777). Nothing changed; look at the new plan.';
function movedTo(team: string, clone: string, cls = 'moved') {
  const p = corePlan();
  const b = p.repos[0].branches[0] as any;
  b.teamSha = team; b.cloneSha = clone; b.class = cls;
  if (cls !== 'moved') { delete b.commits; b.proof = "DeveloperMarlo's working branch, checked out in its clone: in use, kept"; }
  return p;
}

test('F2: a stale refusal shows "Refused: <reason>", re-reads the plan and binds a new yes to the new commits', async ({ browser }) => {
  let plans = 0;
  let moved = false;
  answers['git.tidyPlan'] = () => { plans++; return moved ? movedTo(NEW_TEAM, NEW_CLONE) : corePlan(); };
  answers['git.branchRealign'] = (p) => {
    seen.push({ method: 'git.branchRealign', p });
    if (p.expectOld !== NEW_TEAM || p.expectNew !== NEW_CLONE) { moved = true; throw Object.assign(new Error(STALE), { code: 'stale' }); }
    return { oldSha: NEW_TEAM, newSha: NEW_CLONE, branch: 'members/fixture/developermarlo', repo: 'main', member: p.member, changed: true };
  };
  const { page, ctx, tidy, errors } = await tidyAt(browser, 1280, 800);
  const before = plans;
  await tidy.locator('[data-test="tidy-row-members/fixture/developermarlo"] [data-test=tidy-realign]').click();
  await tidy.locator('[data-test=tidy-realign-yes]').click();
  await expect(tidy.locator('[data-test=tidy-realign-error]')).toHaveText(`Refused: ${STALE}`);
  expect(plans, 'the plan is read again after "stale"').toBeGreaterThan(before);
  await expect(tidy.locator('[data-test=tidy-realign-shas]'), 'the confirm reshows the new commits').toHaveText('6666666666 → 7777777777');
  await tidy.locator('[data-test=tidy-realign-confirm]').scrollIntoViewIfNeeded();
  await page.screenshot({ path: path.join(SHOTS, 'card363-realign-stale-1280.png') });
  await tidy.locator('[data-test=tidy-realign-yes]').click();
  await expect(tidy.locator('[data-test=tidy-done]')).toContainText('Realigned members/fixture/developermarlo');
  const calls = seen.filter((s) => s.method === 'git.branchRealign');
  expect(calls.length).toBe(2);
  expect([calls[0].p.expectOld, calls[0].p.expectNew], 'first yes: the commits first shown').toEqual([TEAM_SHA, CLONE_SHA]);
  expect([calls[1].p.expectOld, calls[1].p.expectNew], 'second yes: bound to the new commits').toEqual([NEW_TEAM, NEW_CLONE]);
  expect(errors).toEqual([]);
  await ctx.close();
});

test('F2: a stale refusal where the branch is no longer moved closes the confirm and reports the refusal', async ({ browser }) => {
  let moved = false;
  answers['git.tidyPlan'] = () => (moved ? movedTo(CLONE_SHA, CLONE_SHA, 'working') : corePlan());
  answers['git.branchRealign'] = (p) => { seen.push({ method: 'git.branchRealign', p }); moved = true; throw Object.assign(new Error(STALE), { code: 'stale' }); };
  const { ctx, tidy } = await tidyAt(browser, 1280, 800);
  await tidy.locator('[data-test="tidy-row-members/fixture/developermarlo"] [data-test=tidy-realign]').click();
  await tidy.locator('[data-test=tidy-realign-yes]').click();
  await expect(tidy.locator('[data-test=tidy-realign-confirm]')).toHaveCount(0);
  await expect(tidy.locator('[data-test=tidy-done]')).toContainText(`Refused: ${STALE}`);
  await ctx.close();
});

test('F3: the Realign confirm lists the commits it drops and the core proof', async ({ browser }) => {
  const { page, ctx, tidy } = await tidyAt(browser, 1280, 800);
  const row = tidy.locator('[data-test="tidy-row-members/fixture/developermarlo"]');
  await expect(row.locator('[data-test=tidy-commits]')).toContainText('aa11bb22cc Retry the upload three times');
  await expect(row).toContainText("A realign drops these from the team's copy (not in DeveloperMarlo's clone):");
  await expect(row.locator('[data-test=tidy-why]')).toContainText('nothing is lost by realigning it');
  await row.locator('[data-test=tidy-realign]').click();
  const box = tidy.locator('[data-test=tidy-realign-confirm]');
  await expect(box.locator('[data-test=tidy-realign-commits]')).toContainText('aa11bb22cc Retry the upload three times');
  await expect(box.locator('[data-test=tidy-realign-proof]')).toContainText('nothing is lost by realigning it (git cherry main: - aa11bb22cc)');
  await box.scrollIntoViewIfNeeded();
  await page.screenshot({ path: path.join(SHOTS, 'card363-realign-confirm-box-1280.png') });
  await ctx.close();
});

test('F3: origin-only and own-work rows list their commits under "Commits not in <base>:"', async ({ browser }) => {
  answers['git.tidyPlan'] = () => {
    const p = corePlan();
    p.repos[0].branches.push({ name: 'members/fixture/old-own', where: ['local', 'origin'], class: 'own-work', teamSha: '8888888888aaaaaaaaaa',
      commits: [{ sha: '8888888888aaaaaaaaaa0000000000000000000', subject: 'Half-done export' }], proof: '1 commit(s) not in main: needs a look' } as any);
    return p;
  };
  const { page, ctx, tidy } = await tidyAt(browser, 1280, 800);
  const oo = tidy.locator('[data-test="tidy-row-feature/origin-only-thing"]');
  await expect(oo).toContainText('Commits not in main:');
  await expect(oo.locator('[data-test=tidy-commits]')).toContainText('4444444444 Try a new cache');
  const own = tidy.locator('[data-test="tidy-row-members/fixture/old-own"]');
  await expect(own).toContainText('Commits not in main:');
  await expect(own.locator('[data-test=tidy-commits]')).toContainText('8888888888 Half-done export');
  await own.scrollIntoViewIfNeeded();
  await page.screenshot({ path: path.join(SHOTS, 'card363-own-work-origin-only-1280.png') });
  await ctx.close();
});

test('F4: the notes and "Merged and contained are measured against <base>" are shown above the list', async ({ browser }) => {
  const { page, ctx, tidy } = await tidyAt(browser, 1280, 800);
  await expect(tidy.locator('[data-test=tidy-note]')).toHaveText(NOTE);
  await expect(tidy.locator('[data-test=tidy-base]')).toHaveText('Merged and contained are measured against main.');
  const order = await tidy.evaluate((el) => {
    const at = (s: string) => el.querySelector(s)!.getBoundingClientRect().top;
    return { note: at('[data-test=tidy-notes]'), base: at('[data-test=tidy-base]'), first: at('section.group') };
  });
  expect(order.note).toBeLessThan(order.first);
  expect(order.base).toBeLessThan(order.first);
  await shot(page, 'notes-base-1280');
  await ctx.close();
});
