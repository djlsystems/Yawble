// Card 477 (B104): the Workflows page in a real browser (headless Chrome), on remote.html against the FAKE core of
// phone-remote.spec.ts answering core card 476's contract (workflows.list / workflows.get, canned). At 1280 and 375 px,
// light and dark, with full access and as a Visitor (read-only: no actions). Opened at #workflows (the page's own link):
// the table, the chips, a row's pane with its spec, cards, activity and time; on a phone the pane takes the page and
// Back returns to the list; the page never scrolls sideways. Screenshots go to $YAWBLE_TEAM_DOCS/B104/screenshots.
//
//   npx playwright test card477-workflows      (B037_SKIP_BUILD=1 reuses out/b037/renderer)
import { expect, test, type Browser } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import path from 'node:path';
import { type Access } from './phone/fake-core';
import { buildRenderer, builtDir, fakeRemote } from './phone/server';

test.use({ channel: 'chrome', headless: true, actionTimeout: 8_000 });
test.setTimeout(60_000);

test.beforeAll(async () => { test.setTimeout(600_000); await buildRenderer(); });

const shots = process.env.YAWBLE_TEAM_DOCS ? path.join(process.env.YAWBLE_TEAM_DOCS, 'B104', 'screenshots') : null;
if (shots) mkdirSync(shots, { recursive: true });

async function open(browser: Browser, access: Access, w: number, h: number, theme: 'light' | 'dark', hash = '#workflows') {
  const s = fakeRemote(builtDir);
  s.core.setAsks(false);
  s.core.setAccess(access);
  const mobile = w < 760;
  const ctx = await browser.newContext({ viewport: { width: w, height: h }, isMobile: mobile, hasTouch: mobile, colorScheme: theme, reducedMotion: 'reduce' });
  await s.attach(ctx);
  const page = await ctx.newPage();
  const errors: string[] = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.goto(`${s.url}${hash}`);
  await expect(page.locator('[data-test=workflows-view]')).toBeVisible({ timeout: 30_000 });
  await expect(page.locator('[data-test=workflows-row]').first()).toBeVisible();
  return { s, page, ctx, errors };
}
const noSideScroll = async (page: import('@playwright/test').Page) =>
  page.evaluate(() => document.scrollingElement!.scrollWidth <= window.innerWidth + 1);

for (const [access, w, h, theme] of [['full', 1280, 800, 'light'], ['full', 1280, 800, 'dark'], ['full', 375, 740, 'light'], ['full', 375, 740, 'dark'], ['read', 1280, 800, 'dark'], ['read', 375, 740, 'light']] as const) {
  test(`${access === 'read' ? 'a Visitor' : 'full access'} at ${w}px (${theme}): the table, the chips and a workflow's pane`, async ({ browser }) => {
    const { s, page, ctx, errors } = await open(browser, access, w, h, theme);
    const tag = `${access === 'read' ? 'visitor' : 'full'}-${w}-${theme}`;
    // Open (the default chip): needs you first, then running, then the other open ones
    await expect(page.locator('[data-test=workflows-row]')).toHaveCount(4);
    await expect(page.locator('[data-test=workflows-row]').first()).toHaveAttribute('data-wf', '12');
    await expect(page.locator('[data-test=workflows-chip-open]')).toContainText('4');
    await expect(page.locator('[data-test=workflows-summary]')).toContainText('4 open · 1 needs you · 1 completed today');
    await expect(page.locator('[data-test=nav-workflows]')).toHaveCount(1);
    expect(s.core.calls).toContain('workflows.list');
    expect(await noSideScroll(page), 'the page never scrolls sideways').toBe(true);
    if (shots) await page.screenshot({ path: path.join(shots, `b104-list-${tag}.png`) });

    // All: the ended ones too, after the open ones
    await page.locator('[data-test=workflows-chip-all]').click();
    await expect(page.locator('[data-test=workflows-row]')).toHaveCount(7);
    await expect(page.locator('[data-test=workflows-row]').last()).toHaveAttribute('data-status', /expired|closed/);
    if (shots && w === 1280) await page.screenshot({ path: path.join(shots, `b104-all-${tag}.png`) });

    // A row's pane: spec, cards, activity, time; actions for full access only
    await page.locator('[data-test=workflows-row][data-wf="10"]').click();
    const pane = page.locator('[data-test=workflow-pane]');
    await expect(pane).toBeVisible();
    await expect(pane.locator('[data-test=workflow-pane-title]')).toHaveText('Partial refunds');
    await expect(pane.locator('[data-test=workflow-pane-spec]')).toContainText('refunds are asked for every day');
    await expect(pane.locator('[data-test=workflow-pane-card]')).toHaveCount(6);
    await expect(pane.locator('[data-test=workflow-pane-message]').first()).toHaveAttribute('data-kind', 'progress');
    await expect(pane.locator('[data-test=workflow-pane-breakdown-row]').first()).toBeVisible();
    if (access === 'read') await expect(pane.locator('[data-test=workflow-pane-actions]')).toHaveCount(0);
    else await expect(pane.locator('[data-test=workflow-pane-stop]')).toBeVisible();
    expect(s.core.calls).toContain('workflows.get');
    expect(await noSideScroll(page), 'the page never scrolls sideways with the pane open').toBe(true);
    if (shots) await page.screenshot({ path: path.join(shots, `b104-pane-${tag}.png`) });

    if (w < 760) {
      // On a phone the pane took the page; Back returns to the list
      await expect(page.locator('[data-test=workflows-listwrap]')).toBeHidden();
      await pane.locator('[data-test=workflow-pane-back]').click();
      await expect(page.locator('[data-test=workflows-listwrap]')).toBeVisible();
    } else {
      // The elapsed time opens the Timeline dialog
      await page.locator('[data-test=workflows-row][data-wf="10"] [data-test=workflows-timeline]').click();
      await expect(page.locator('[data-test=timeline-dialog]')).toBeVisible();
      if (shots && access === 'full') await page.screenshot({ path: path.join(shots, `b104-timeline-${tag}.png`) });
    }
    expect(errors).toEqual([]);
    await ctx.close();
  });
}

test('the item link opens the Backlog at that item; the filters live in the address', async ({ browser }) => {
  const { page, ctx, errors } = await open(browser, 'full', 1280, 800, 'light', '#workflows?s=all&q=refund');
  await expect(page.locator('[data-test=workflows-chip-all]')).toHaveAttribute('aria-selected', 'true');
  await expect(page.locator('[data-test=workflows-search]')).toHaveValue('refund');
  await expect(page.locator('[data-test=workflows-hit]').first()).toBeVisible();
  if (shots) await page.screenshot({ path: path.join(shots, 'b104-filtered-full-1280-light.png') });
  await page.locator('[data-test=workflows-row][data-wf="12"] [data-test=workflow-item-link]').click();
  await expect(page.locator('[data-test=backlog-view]')).toBeVisible();
  await expect(page.locator('[data-test=backlog-item] .mono').first()).toHaveText('B012');
  expect(errors).toEqual([]);
  await ctx.close();
});
