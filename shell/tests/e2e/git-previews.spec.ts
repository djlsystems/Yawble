// Card 465 (B073, shell): the Git dialog's clickable counts and previews in a real browser (headless Chrome) on remote.html
// against the FAKE core (tests/e2e/phone/fake-core.ts, setB073(true): git.commitRange, git.mergePreview, git.mergeDiff,
// git.deletePreview, as DeveloperTomas's YAWBLE_TEAM_DOCS\B073\contract.md has them).
//  - 1280 x 800: "2 commits ahead" lists the commits, a row opens the Commit view; Merge to main… shows the outcome, the
//    commits that land and the files with the combined diff before its Merge button; Delete… on DeveloperBo's branch marks
//    each commit "work of its own" / "already in main", and after the delete its last commit still opens; Sync now and
//    Bring main current list what came in.
//  - 390 x 844 (a phone): the counts' list and the merge preview do not scroll sideways.
//   npx playwright test card465-git-previews      (B037_SKIP_BUILD=1 reuses out/b037/renderer)
// Screenshots go to $YAWBLE_TEAM_DOCS/B073 (else test-results/B073).
import { expect, test, type Browser, type Page } from './phone/fixtures';
import { mkdirSync } from 'node:fs';
import path from 'node:path';
import { buildRenderer, builtDir, fakeRemote } from './phone/server';

test.use({ channel: 'chrome', headless: true, actionTimeout: 8_000 });
test.setTimeout(90_000);

const SHOTS = path.join(process.env.YAWBLE_TEAM_DOCS || path.resolve(__dirname, '..', '..', 'test-results'), 'B073');
const shot = (page: Page, name: string) => page.screenshot({ path: path.join(SHOTS, `card465-${name}.png`) });
const MERGE = 'def5678a9b0c1d2e3f405162738495a6b7c8d9e0';
const FEATURE = 'fea7c0de1a2b3c4d5e6f708192a3b4c5d6e7f809';

test.beforeAll(async () => { test.setTimeout(600_000); mkdirSync(SHOTS, { recursive: true }); await buildRenderer(); });
test.afterEach(() => { fakeRemote(builtDir).core.setB073(false); });

async function open(browser: Browser, w: number, h: number) {
  const s = fakeRemote(builtDir);
  s.core.setAccess('full');
  s.core.setAsks(false);
  s.core.setNeedsYou(false);
  s.core.setB073(true);
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
  return { s, page, ctx, errors };
}

const closeCommitDialog = (page: Page) =>
  page.locator('[data-test=commit-dialog]').locator('xpath=ancestor::section[@role="dialog"]').locator('[data-test=modal-close]').click();

async function noSideways(page: Page) {
  const m = await page.evaluate(() => {
    const vw = innerWidth;
    const past = [...document.querySelectorAll('[data-test=git-range] *, [data-test=git-merge-preview] *')].filter((e) => {
      if (e.closest('[data-test=diff-inline], [data-test=diff-split]')) return false;
      const r = e.getBoundingClientRect(); return r.width > 0 && r.height > 0 && r.right > vw + 0.5;
    }).map((e) => `${e.tagName}[${e.getAttribute('data-test') ?? ''}]`);
    return { vw, docScrollW: document.documentElement.scrollWidth, past: past.slice(0, 10) };
  });
  expect(m.docScrollW, 'page scroll width').toBeLessThanOrEqual(m.vw);
  expect(m.past, 'elements past the right edge').toEqual([]);
}

test('1280 x 800: counts open their commits; merge and delete previews; sync and bring current list what came in', async ({ browser }) => {
  const { s, page, ctx, errors } = await open(browser, 1280, 800);
  const dialog = page.locator('[data-test=git-merge-state]').locator('xpath=ancestor::section[@role="dialog"]');

  // 1. The counts.
  await expect(page.locator('[data-test=git-merge-text]')).toHaveText('team/payments is 2 commits ahead and 1 behind: a merge commit will join them (no rebase).');
  await page.locator('[data-test=git-count-team-ahead]').click();
  const range = page.locator('[data-test=git-range]');
  await expect(range.locator('[data-test=git-range-title]')).toHaveText('2 commits on team/payments not in main');
  await expect(range.locator('[data-test=commit-list-row]')).toHaveCount(2);
  await expect(range.locator('[data-test=commit-list-row]').first().locator('[data-test=local-mark]')).toHaveText('not pushed');
  await expect(range.locator('[data-test=commit-list-row]').nth(1).locator('[data-test=local-mark]')).toHaveCount(0);
  await shot(page, 'range-1280');
  await range.locator('[data-test=commit-link]').nth(1).click();
  await expect(page.locator('[data-test=commit-title]')).toHaveText('Partial refunds: refund part of a payment');
  await closeCommitDialog(page);
  await page.locator('[data-test=git-count-base-behind-origin]').click();
  await expect(range).toHaveAttribute('data-to', '0f1e2d3c4b5a69788796a5b4c3d2e1f0a9b8c7d6'); // origin's main, the pair git.status gave
  await expect(range.locator('[data-test=commit-list-row]')).toHaveCount(1);
  await page.locator('[data-test="git-count-member-members/payments/developerbo"]').click();
  await expect(range.locator('[data-test=git-range-title]')).toHaveText('2 commits on members/payments/developerbo not in main');
  await expect(page.locator('[data-test=git-member-row]').filter({ hasText: 'developerbo' }).locator('[data-test=local-mark]')).toHaveCount(1);
  await expect(page.locator('[data-test=git-member-row]').filter({ hasText: 'developerada' }).locator('[data-test=local-mark]')).toHaveCount(0);
  await range.locator('[data-test=git-range-close]').click();

  // 2. Merge to main…: the preview before the Merge button.
  await page.locator('[data-test=git-merge]').click();
  const preview = page.locator('[data-test=git-merge-preview]');
  await expect(preview.locator('[data-test=git-merge-outcome]')).toHaveText('A merge commit will be made: main has 1 commit team/payments lacks.');
  await expect(preview.locator('[data-test=commit-list-row]')).toHaveCount(2);
  await preview.locator('[data-test=git-merge-file-open]').click();
  await expect(preview.locator('[data-test=diff-view]')).toBeVisible();
  const pBox = (await preview.boundingBox())!;
  const yesBox = (await page.locator('[data-test=git-merge-yes]').boundingBox())!;
  expect(pBox.y).toBeLessThan(yesBox.y);
  await shot(page, 'merge-preview-1280');
  await page.locator('[data-test=git-merge-cancel]').click();
  await expect(preview).toHaveCount(0);

  // 3. Delete… DeveloperBo's branch: each commit marked; after the delete its last commit still opens.
  await page.locator('[data-test="git-delete-members/payments/developerbo"]').click();
  const del = page.locator('[data-test=git-delete]');
  await expect(del.locator('[data-test=git-delete-summary]')).toHaveText('Deleting it loses 1 commit of its own (fea7c0de1a2b); it is not on origin either.');
  await expect(del.locator('[data-test=commit-list-mark]')).toHaveText(['work of its own', 'already in main']);
  await shot(page, 'delete-preview-1280');
  await del.locator('[data-test=git-delete-yes]').click();
  const rec = page.locator('[data-test=git-result-recorded]');
  await expect(rec).toContainText("members/payments/developerbo's last commit, kept on record:");
  await rec.locator('[data-test=commit-link]').click();
  await expect(page.locator('[data-test=commit-dialog]')).toHaveAttribute('data-sha', FEATURE);
  await expect(page.locator('[data-test=commit-title]')).toHaveText('Try a new receipt layout');
  await closeCommitDialog(page);

  // 4. Sync now: what came into the team branch (the core's incoming).
  await page.locator('[data-test=git-sync-now]').click();
  const came = page.locator('[data-test=git-result-commits]');
  await expect(came).toContainText('1 commit came into team/payments:');
  await came.locator('[data-test=commit-link]').click();
  await expect(page.locator('[data-test=commit-title]')).toHaveText('Release 2026.11.18.1');
  await closeCommitDialog(page);
  // Bring main current: the core's commits.
  await page.locator('[data-test=git-bring-current]').click();
  await expect(came).toContainText('1 commit came into main:');
  await expect(came.locator('[data-test=commit-link]')).toHaveAttribute('data-sha', FEATURE);
  await shot(page, 'came-in-1280');

  expect(s.core.calls).toEqual(expect.arrayContaining(['git.commitRange', 'git.mergePreview', 'git.mergeDiff', 'git.deletePreview']));
  const b073 = ['git.commitRange', 'git.mergePreview', 'git.mergeDiff', 'git.deletePreview', 'git.merge', 'git.sync', 'git.bringCurrent', 'git.deleteBranches'];
  expect([...s.core.unknown].filter((m) => b073.includes(m))).toEqual([]);
  await expect(dialog).toBeVisible();
  expect(errors).toEqual([]);
  await ctx.close();
});

test('390 x 844: the counts list and the merge preview fit a phone', async ({ browser }) => {
  const { page, ctx, errors } = await open(browser, 390, 844);
  await page.locator('[data-test=git-count-team-ahead]').click();
  await expect(page.locator('[data-test=git-range] [data-test=commit-list-row]')).toHaveCount(2);
  await noSideways(page);
  await page.locator('[data-test=git-merge]').click();
  await expect(page.locator('[data-test=git-merge-preview] [data-test=commit-list-row]')).toHaveCount(2);
  await page.locator('[data-test=git-merge-file-open]').click();
  await expect(page.locator('[data-test=git-merge-preview] [data-test=diff-view]')).toBeVisible();
  await noSideways(page);
  await shot(page, 'phone-390');
  await page.locator('[data-test=git-merge-preview] [data-test=commit-link]').first().click();
  await expect(page.locator('[data-test=commit-dialog]')).toHaveAttribute('data-sha', MERGE);
  expect(errors).toEqual([]);
  await ctx.close();
});

test('1280 x 800: the team page "1 commit behind main" opens its commits; Sync now lists what came in', async ({ browser }) => {
  const s = fakeRemote(builtDir);
  s.core.setAccess('full');
  s.core.setAsks(false);
  s.core.setNeedsYou(false);
  s.core.setB073(true);
  const ctx = await browser.newContext({ viewport: { width: 1280, height: 800 } });
  await s.attach(ctx);
  const page = await ctx.newPage();
  const errors: string[] = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.goto(`${s.url}#team=1`);
  const count = page.locator('[data-test=team-behind-count]');
  await expect(count).toHaveText('1 commit behind main', { timeout: 30_000 });
  await count.click();
  const list = page.locator('[data-test=team-behind-list]');
  await expect(list.locator('[data-test=commit-list-row]')).toHaveCount(1);
  await list.locator('[data-test=commit-link]').click();
  await expect(page.locator('[data-test=commit-title]')).toHaveText('Release 2026.11.18.1');
  await closeCommitDialog(page);
  await page.locator('[data-test=team-sync-now]').click();
  const came = page.locator('[data-test=team-sync-came-in]');
  await expect(came).toContainText('1 commit came into team/payments:');
  await expect(came.locator('[data-test=commit-link]')).toHaveAttribute('data-sha', /^[0-9a-f]{40}$/);
  await expect(page.locator('[data-test=team-behind]')).toHaveCount(0);
  expect(errors).toEqual([]);
  await ctx.close();
});