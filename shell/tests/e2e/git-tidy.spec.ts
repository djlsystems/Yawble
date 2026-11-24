// Card 362 (B067 shell): the Git dialog's "Tidy…" in a real browser (headless Chrome) against the FAKE core
// (tests/e2e/phone/fake-core.ts stubs git.tidyPlan, git.deleteBranches and git.branchRealign to the card's contract).
//  - 1280 x 800: Tidy… lists every class; merged and contained are pre-selected; Delete selected shows the core's plans,
//    one yes deletes them, the plan is read again and what was kept is listed with why; Realign on DeveloperAda's branch
//    is refused (she is running) with the reason; Realign on TesterBen's shows old -> new, one yes, done.
//  - 360 x 740 and 390 x 844 (through the team page's ⋯ menu), and 1280 x 800: nothing in the dialog scrolls sideways.
//   npx playwright test card362-tidy      (screenshots: $YAWBLE_TEAM_DOCS/B067 when set, else test-results/card362-*.png)
import { expect, test, type Browser, type Page } from './phone/fixtures';
import { mkdirSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { buildRenderer, fakeRemote } from './phone/server';

test.use({ channel: 'chrome', headless: true, actionTimeout: 8_000 });
test.setTimeout(90_000);

const OUT = path.resolve(__dirname, '..', '..', 'test-results');
const SHOTS = process.env.YAWBLE_TEAM_DOCS ? path.join(process.env.YAWBLE_TEAM_DOCS, 'B067') : OUT;
const BUILT = path.resolve(__dirname, '..', '..', 'out', 'card362', 'renderer');
const shot = (page: Page, name: string) => { mkdirSync(SHOTS, { recursive: true }); return page.screenshot({ path: path.join(SHOTS, `card362-${name}.png`) }); };
const save = (name: string, v: unknown) => { mkdirSync(OUT, { recursive: true }); writeFileSync(path.join(OUT, `card362-${name}.json`), JSON.stringify(v, null, 2)); };

test.beforeAll(async () => {
  test.setTimeout(600_000);
  if (!process.env.CARD362_SKIP_BUILD) await buildRenderer(undefined, BUILT);
});
test.afterEach(() => { const c = fakeRemote(BUILT).core; c.setNeedsYou(true); c.resetTidy(); });

async function gitDialog(browser: Browser, w: number, h: number) {
  const s = fakeRemote(BUILT);
  s.core.setAccess('full');
  s.core.setAsks(false);
  s.core.setNeedsYou(false);
  s.core.resetTidy();
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
  } else {
    await page.locator('[data-test=open-git]').click();
  }
  await expect(page.locator('[data-test=git-merge-state]')).toBeVisible({ timeout: 15_000 });
  return { page, ctx, errors };
}

/** Sideways scroll: the page, the dialog's own scroller, and any visible element in the dialog past the viewport's right
 * edge (a pre's own scroll area aside). */
async function sideways(page: Page) {
  return page.locator('[role=dialog]').evaluate((dlg) => {
    const vw = innerWidth;
    const scrollers = [dlg, ...dlg.querySelectorAll('*')].filter((e) => {
      const o = getComputedStyle(e).overflowX; return (o === 'auto' || o === 'scroll') && e.tagName !== 'PRE';
    }).map((e) => ({ tag: e.tagName, cls: (e as HTMLElement).className, sw: e.scrollWidth, cw: e.clientWidth })).filter((x) => x.sw > x.cw + 1);
    const past = [...dlg.querySelectorAll('*')].filter((e) => {
      if (e.closest('pre')) return false;
      const r = e.getBoundingClientRect(); return r.width > 0 && r.height > 0 && r.right > vw + 0.5;
    }).map((e) => `${e.tagName}.${(e as HTMLElement).className}[${e.getAttribute('data-test') ?? ''}] right=${e.getBoundingClientRect().right.toFixed(1)}`);
    return { vw, docScrollW: document.documentElement.scrollWidth, bodyScrollW: document.body.scrollWidth, scrollers, past: past.slice(0, 10) };
  });
}
function expectNoSideways(m: Awaited<ReturnType<typeof sideways>>) {
  expect(m.docScrollW, 'page scroll width').toBeLessThanOrEqual(m.vw);
  expect(m.scrollers, 'a scroller in the dialog scrolls sideways').toEqual([]);
  expect(m.past, 'elements past the right edge').toEqual([]);
}

test('1280 x 800: Tidy… plan, Delete selected with one yes, report kept and why, Realign refused and done', async ({ browser }) => {
  const { page, ctx, errors } = await gitDialog(browser, 1280, 800);
  await shot(page, 'before-1280');
  await page.locator('[data-test=git-tidy]').click();
  const tidy = page.locator('[data-test=tidy]');
  await expect(tidy.locator('[data-test=tidy-group-merged]')).toBeVisible();
  for (const cls of ['merged', 'contained', 'moved', 'own-work', 'working', 'origin-only', 'scratch']) await expect(tidy.locator(`[data-test=tidy-group-${cls}]`)).toBeVisible();
  await expect(tidy.locator('[data-test=tidy-proof]')).toContainText('git cherry main members/payments/kofi');
  await expect(tidy.locator('[data-test=tidy-group-working]')).toContainText("DeveloperAda's working branch, not a leftover");
  await expect(tidy.locator('[data-test="tidy-row-feature/refund-limits"] [data-test=tidy-commits]')).toContainText('Refund limits per merchant');
  // Card 362 fix (F3, F4): the moved row's dropped commits and proof, origin-only's commits, the notes and the base.
  const benRow = tidy.locator('[data-test="tidy-row-members/payments/testerben"]');
  await expect(benRow.locator('[data-test=tidy-commits]')).toContainText('Retry the upload three times');
  await expect(benRow.locator('[data-test=tidy-why]')).toContainText('nothing is lost by realigning it');
  await expect(tidy.locator('[data-test="tidy-row-feature/old-idea"] [data-test=tidy-commits]')).toContainText('Try a new cache');
  await expect(tidy.locator('[data-test=tidy-note]')).toHaveText("payments-cloud: TesterBen's clone was not read: its .git is a file (a gitdir pointer)");
  await expect(tidy.locator('[data-test=tidy-base]')).toHaveText('Merged and contained are measured against main.');
  await expect(tidy.locator('[data-test=tidy-delete-selected]')).toContainText('Delete selected (3)');
  const ticked = await tidy.locator('[data-test=tidy-select]').evaluateAll((cs) => cs.map((c) => (c as HTMLInputElement).checked));
  expect(ticked).toEqual([true, true, true]);
  const m1280 = await sideways(page);
  save('sideways-1280', m1280);
  expectNoSideways(m1280);
  await shot(page, 'plan-1280');

  // Delete selected: the core's plans, then one yes.
  await tidy.locator('[data-test=tidy-delete-selected]').click();
  await expect(tidy.locator('[data-test=tidy-delete-plan]').first()).toContainText('Delete members/payments/old-cleo here and on origin');
  await expect(tidy.locator('[data-test=tidy-delete-plan]')).toHaveCount(2); // payments, payments-cloud
  await shot(page, 'delete-plan-1280');
  await tidy.locator('[data-test=tidy-delete-yes]').click();
  await expect(tidy.locator('[data-test=tidy-report]')).toBeVisible();
  await expect(tidy.locator('[data-test=tidy-done]')).toHaveCount(2);
  await expect(tidy.locator('[data-test=tidy-done]').first()).toContainText('Deleted members/payments/old-cleo, members/payments/kofi.');
  const kept = await tidy.locator('[data-test=tidy-kept]').allInnerTexts();
  save('kept-after-delete', kept);
  expect(kept.some((k) => k.includes('old-cleo') || k.includes('kofi') || k.includes('old-wren'))).toBe(false);
  expect(kept).toContain("feature/refund-limits (payments): has work of its own (1 commit), needs a look");
  expect(kept).toContain('feature/old-idea (payments-cloud): only on origin, nobody has it here');
  await expect(tidy.locator('[data-test=tidy-group-merged]')).toHaveCount(0);
  await expect(tidy.locator('[data-test=tidy-group-contained]')).toHaveCount(0);
  await shot(page, 'after-delete-1280');

  // Realign DeveloperAda's moved branch: refused while she runs.
  await tidy.locator('[data-test="tidy-row-members/payments/developerada-moved"] [data-test=tidy-realign]').click();
  await expect(tidy.locator('[data-test=tidy-realign-shas]')).toHaveText('5bbb6ba450 → e0f2a18c0f');
  await tidy.locator('[data-test=tidy-realign-yes]').click();
  await expect(tidy.locator('[data-test=tidy-realign-error]')).toHaveText('Refused: DeveloperAda is running: realign it when the run ends.');
  await shot(page, 'realign-refused-1280');
  await tidy.locator('[data-test=tidy-realign-cancel]').click();

  // Realign TesterBen's: old -> new, one yes, done; his branch is his working branch again.
  await tidy.locator('[data-test="tidy-row-members/payments/testerben"] [data-test=tidy-realign]').click();
  await expect(tidy.locator('[data-test=tidy-realign-confirm]')).toContainText("TesterBen's clone has");
  await tidy.locator('[data-test=tidy-realign-yes]').click();
  await expect(tidy.locator('[data-test=tidy-done]')).toHaveText('Realigned members/payments/testerben: 5bbb6ba450 → e0f2a18c0f (the old commit is recorded for recovery).');
  await expect(tidy.locator('[data-test=tidy-group-working]')).toContainText('members/payments/testerben');
  await expect(tidy.locator('[data-test=tidy-kept]').filter({ hasText: 'members/payments/developerada-moved' })).toContainText('Realign to fix');
  await shot(page, 'after-realign-1280');
  expectNoSideways(await sideways(page));
  expect(errors).toEqual([]);
  await ctx.close();
});

for (const [w, h] of [[360, 740], [390, 844]] as const) {
  test(`${w} x ${h}: the Tidy… plan, delete plan, report and realign do not scroll sideways`, async ({ browser }) => {
    const { page, ctx, errors } = await gitDialog(browser, w, h);
    await shot(page, `before-${w}`);
    await page.locator('[data-test=git-tidy]').click();
    const tidy = page.locator('[data-test=tidy]');
    await expect(tidy.locator('[data-test=tidy-group-moved]')).toBeVisible();
    await tidy.scrollIntoViewIfNeeded();
    const plan = await sideways(page);
    save(`sideways-plan-${w}`, plan);
    expectNoSideways(plan);
    await shot(page, `plan-${w}`);
    await tidy.locator('[data-test="tidy-row-members/payments/testerben"] [data-test=tidy-realign]').click();
    await tidy.locator('[data-test=tidy-realign-confirm]').scrollIntoViewIfNeeded();
    expectNoSideways(await sideways(page));
    await shot(page, `realign-${w}`);
    await tidy.locator('[data-test=tidy-realign-cancel]').click();
    await tidy.locator('[data-test=tidy-delete-selected]').click();
    await tidy.locator('[data-test=tidy-delete-yes]').click();
    await expect(tidy.locator('[data-test=tidy-report]')).toBeVisible();
    await tidy.locator('[data-test=tidy-report]').scrollIntoViewIfNeeded();
    const after = await sideways(page);
    save(`sideways-after-${w}`, after);
    expectNoSideways(after);
    await shot(page, `after-delete-${w}`);
    expect(errors).toEqual([]);
    await ctx.close();
  });
}
