// Card 444 (B072, shell): the Commit view in a real browser (headless Chrome) on remote.html against the FAKE core
// (tests/e2e/phone/fake-core.ts answers git.commitShow and git.commitDiff to the card's contract).
//  - 1280 x 800: the Git dialog's member-branch commit opens the Commit view over it: what, where (team branch only, not
//    pushed), files (renamed, added, binary); a file's diff side by side and inline; the binary file's summary line;
//    Show its branch opens the Git dialog at that branch's row.
//  - the board: a hand-back in a card's trail names two commits; one opens, the other says it is gone and why.
//  - 390 x 844 (a phone): the same view as a sheet, nothing scrolls sideways but the diff itself; with members' code
//    hidden from cloud viewers the files are listed and no diff opens.
//   npx playwright test card444-commit-view      (E2E_SKIP_BUILD=1 reuses out/e2e/renderer)
// Screenshots go to $YAWBLE_TEAM_DOCS/B072 (else test-results/B072).
import { expect, test, type Browser, type Page } from './phone/fixtures';
import { mkdirSync } from 'node:fs';
import path from 'node:path';
import { buildRenderer, builtDir, fakeRemote } from './phone/server';

test.use({ channel: 'chrome', headless: true, actionTimeout: 8_000 });
test.setTimeout(90_000);

const SHOTS = path.join(process.env.YAWBLE_TEAM_DOCS || path.resolve(__dirname, '..', '..', 'test-results'), 'B072');
const shot = (page: Page, name: string) => page.screenshot({ path: path.join(SHOTS, `card444-${name}.png`) });
const ADA = '3f9c2a1b7d4e5f60718293a4b5c6d7e8f9012345';

test.beforeAll(async () => { test.setTimeout(600_000); mkdirSync(SHOTS, { recursive: true }); await buildRenderer(); });
test.afterEach(() => { fakeRemote(builtDir).core.setCodeShown(true); });

async function open(browser: Browser, w: number, h: number, hash = '#team=1') {
  const s = fakeRemote(builtDir);
  s.core.setAccess('full');
  s.core.setAsks(false);
  s.core.setNeedsYou(false);
  const mobile = w < 760;
  const ctx = await browser.newContext({ viewport: { width: w, height: h }, isMobile: mobile, hasTouch: mobile, deviceScaleFactor: mobile ? 3 : 1 });
  await s.attach(ctx);
  const page = await ctx.newPage();
  const errors: string[] = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.goto(`${s.url}${hash}`);
  return { s, page, ctx, errors };
}

async function gitDialog(page: Page) {
  await expect(page.locator('[data-test=team-title]')).toBeVisible({ timeout: 30_000 });
  if (await page.locator('[data-test=team-more]').count()) {
    await page.locator('[data-test=team-more]').click();
    await page.locator('[data-test=team-more-menu] [data-test=open-git]').click();
  } else {
    await page.locator('[data-test=open-git]').click();
  }
  await expect(page.locator('[data-test=git-merge-state]')).toBeVisible({ timeout: 15_000 });
}

/** The page and the commit dialog never scroll sideways; only the diff's own body may. */
async function sideways(page: Page) {
  return page.locator('[data-test=commit-dialog]').evaluate((box) => {
    const vw = innerWidth;
    const past = [...box.querySelectorAll('*')].filter((e) => {
      if (e.closest('[data-test=diff-inline], [data-test=diff-split]')) return false;
      const r = e.getBoundingClientRect(); return r.width > 0 && r.height > 0 && r.right > vw + 0.5;
    }).map((e) => `${e.tagName}[${e.getAttribute('data-test') ?? ''}] right=${e.getBoundingClientRect().right.toFixed(1)}`);
    return { vw, docScrollW: document.documentElement.scrollWidth, past: past.slice(0, 10) };
  });
}

test('1280 x 800: a member branch commit from the Git dialog: what, where, files, diffs, Show its branch', async ({ browser }) => {
  const { s, page, ctx, errors } = await open(browser, 1280, 800);
  await gitDialog(page);
  await page.locator('[data-test=git-members] [data-test=commit-link]').first().click();
  const view = page.locator('[data-test=commit-view]');
  await expect(view.locator('[data-test=commit-title]')).toHaveText('Partial refunds: refund part of a payment');
  await expect(view.locator('[data-test=commit-sha]')).toHaveText(ADA);
  await expect(view.locator('[data-test=commit-made]')).toHaveText('Developer Ada · card 3 · workflow #10');
  await expect(view.locator('[data-test=commit-where-team]')).toHaveText('In the team branch only, not in main yet');
  await expect(view.locator('[data-test=commit-where-unpushed]')).toHaveText('Not pushed: only on this computer');
  await expect(view.locator('[data-test=commit-file]')).toHaveCount(3);
  await expect(view.locator('[data-test=commit-file]').first()).toContainText('src/payments/refund.ts → src/payments/refunds.ts');

  // The renamed file's diff: side by side, then inline.
  await view.locator('[data-test=commit-file-open]').first().click();
  await expect(view.locator('[data-test=diff-split]')).toBeVisible();
  await expect(view.locator('[data-test=diff-row]').nth(1).locator('[data-test=diff-left]')).toContainText('return amount > 0;');
  await expect(view.locator('[data-test=diff-row]').nth(1).locator('[data-test=diff-right]')).toContainText('if (amount <= 0) return false;');
  // The long added line wraps inside the new side, which stays inside the diff's box (not pushed off to the right).
  const box = (await view.locator('[data-test=diff-split]').boundingBox())!;
  const right = (await view.locator('[data-test=diff-row]').nth(1).locator('[data-test=diff-right]').boundingBox())!;
  expect(right.x + right.width).toBeLessThanOrEqual(box.x + box.width + 1);
  expect(right.x).toBeGreaterThan(box.x + box.width / 3);
  await expect(view.locator('[data-test=commit-author]')).toContainText('developerada@yawble.local');
  await shot(page, 'split-1280');
  await view.locator('[data-test=diff-mode-inline]').click();
  await expect(view.locator('[data-test=diff-inline] [data-test=diff-line]')).toHaveCount(5);
  await shot(page, 'inline-1280');

  // The binary file: its summary line only.
  await view.locator('[data-test=commit-file-open]').nth(2).click();
  await expect(view.locator('[data-test=diff-summary]')).toHaveText('Binary file, 12 KB to 14 KB');
  expect(s.core.calls.filter((m) => m === 'git.commitDiff')).toHaveLength(2);

  // Show its branch: the Commit view closes and the Git dialog marks the member branch's row.
  await view.locator('[data-test=commit-branch]').click();
  await expect(page.locator('[data-test=commit-dialog]')).toHaveCount(0);
  await expect(page.locator('[data-test=git-member-row][data-focus=true]')).toContainText('members/payments/developerada');
  await shot(page, 'its-branch-1280');
  expect(errors).toEqual([]);
  await ctx.close();
});

// B074 (card 471): an ID is a link only once git.commitsResolve confirms it; the gone one is not a commit any more, so it
// stays text (the Commit view's gone state is still reached from a commit the app lists, and is unit-tested).
test('1280 x 800: a confirmed commit ID in a card\'s trail opens the view; a gone one stays text', async ({ browser }) => {
  const { page, ctx, errors } = await open(browser, 1280, 800, '');
  await expect(page.locator('[data-test=sidebar]')).toBeAttached({ timeout: 30_000 });
  await page.locator('[data-test=nav-board]').first().click();
  await page.locator('[data-test=board-card-5]').click();
  const line = page.locator('[data-test=card-trail-line]').filter({ hasText: 'Handed back' });
  await expect(line.locator('[data-test=commit-link]')).toHaveCount(1);
  await expect(line).toContainText('0dead00b1 is gone');
  await expect(line.locator('[data-test=commit-link]')).toHaveAttribute('title', /^Partial refunds: refund part of a payment\nNot pushed: only on this computer$/);
  await line.locator('[data-test=commit-link]').first().click();
  await expect(page.locator('[data-test=commit-title]')).toHaveText('Partial refunds: refund part of a payment');
  await shot(page, 'trail-confirmed-1280');
  expect(errors).toEqual([]);
  await ctx.close();
});

test('390 x 844: the view as a sheet, no sideways scroll; code hidden lists files and opens no diff', async ({ browser }) => {
  const { s, page, ctx, errors } = await open(browser, 390, 844);
  await gitDialog(page);
  await page.locator('[data-test=git-members] [data-test=commit-link]').first().click();
  const view = page.locator('[data-test=commit-view]');
  await expect(view.locator('[data-test=commit-title]')).toBeVisible();
  await expect(page.locator('[data-test=commit-dialog]').locator('xpath=ancestor::section[@role="dialog"]')).toHaveAttribute('data-sheet', 'true');
  await view.locator('[data-test=commit-file-open]').first().click();
  await expect(view.locator('[data-test=diff-inline]')).toBeVisible(); // a phone starts inline
  const m = await sideways(page);
  expect(m.docScrollW, 'page scroll width').toBeLessThanOrEqual(m.vw);
  expect(m.past, 'elements past the right edge').toEqual([]);
  await shot(page, 'phone-390');
  await page.locator('[data-test=modal-close]').last().click();
  await expect(page.locator('[data-test=commit-dialog]')).toHaveCount(0);

  s.core.setCodeShown(false);
  const before = s.core.calls.filter((c) => c === 'git.commitDiff').length;
  await page.locator('[data-test=git-members] [data-test=commit-link]').first().click();
  await expect(view.locator('[data-test=commit-code-hidden]')).toBeVisible();
  await expect(view.locator('[data-test=commit-file]')).toHaveCount(3);
  await expect(view.locator('button[data-test=commit-file-open]')).toHaveCount(0);
  await view.locator('[data-test=commit-file-open]').first().click();
  await expect(view.locator('[data-test=diff-view]')).toHaveCount(0);
  expect(s.core.calls.filter((c) => c === 'git.commitDiff').length).toBe(before);
  await shot(page, 'phone-390-code-hidden');
  expect(errors).toEqual([]);
  await ctx.close();
});
