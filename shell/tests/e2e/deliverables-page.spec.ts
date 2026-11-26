// The Deliverables page in a real browser (headless Chrome), on remote.html against the FAKE core of
// phone-remote.spec.ts answering the core's deliverables contract (deliverables.list / deliverables.get, canned). At 1280 and 375 px,
// light and dark, with full access and as a Visitor (read-only: no actions). Opened at #deliverables (the page's own link):
// the table, the chips, a row's pane with its spec, cards, activity and time; on a phone the pane takes the page and
// Back returns to the list; the page never scrolls sideways. Screenshots go to $YAWBLE_TEAM_DOCS/deliverables-page.
//
//   npx playwright test deliverables-page      (E2E_SKIP_BUILD=1 reuses out/e2e/renderer)
import { expect, test, type Browser } from './phone/fixtures';
import { mkdirSync } from 'node:fs';
import path from 'node:path';
import { type Access } from './phone/fake-core';
import { buildRenderer, builtDir, fakeRemote } from './phone/server';

test.use({ channel: 'chrome', headless: true, actionTimeout: 8_000 });
test.setTimeout(60_000);

test.beforeAll(async () => { test.setTimeout(600_000); await buildRenderer(); });

const shots = process.env.YAWBLE_TEAM_DOCS ? path.join(process.env.YAWBLE_TEAM_DOCS, 'deliverables-page') : null;
if (shots) mkdirSync(shots, { recursive: true });

async function open(browser: Browser, access: Access, w: number, h: number, theme: 'light' | 'dark', hash = '#deliverables') {
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
  await expect(page.locator('[data-test=deliverables-view]')).toBeVisible({ timeout: 30_000 });
  await expect(page.locator('[data-test=deliverables-row]').first()).toBeVisible();
  return { s, page, ctx, errors };
}
const noSideScroll = async (page: import('@playwright/test').Page) =>
  page.evaluate(() => document.scrollingElement!.scrollWidth <= window.innerWidth + 1);

for (const [access, w, h, theme] of [['full', 1280, 800, 'light'], ['full', 1280, 800, 'dark'], ['full', 375, 740, 'light'], ['full', 375, 740, 'dark'], ['read', 1280, 800, 'dark'], ['read', 375, 740, 'light']] as const) {
  test(`${access === 'read' ? 'a Visitor' : 'full access'} at ${w}px (${theme}): the table, the chips and a deliverable's pane`, async ({ browser }) => {
    const { s, page, ctx, errors } = await open(browser, access, w, h, theme);
    const tag = `${access === 'read' ? 'visitor' : 'full'}-${w}-${theme}`;
    // Open (the default chip): needs you first, then running, then the other open ones
    await expect(page.locator('[data-test=deliverables-row]')).toHaveCount(4);
    await expect(page.locator('[data-test=deliverables-row]').first()).toHaveAttribute('data-wf', '12');
    await expect(page.locator('[data-test=deliverables-chip-open]')).toContainText('4');
    await expect(page.locator('[data-test=deliverables-summary]')).toContainText('4 open · 1 needs you · 1 completed today');
    await expect(page.locator('[data-test=nav-deliverables]')).toHaveCount(1);
    expect(s.core.calls).toContain('deliverables.list');
    expect(await noSideScroll(page), 'the page never scrolls sideways').toBe(true);
    if (shots) await page.screenshot({ path: path.join(shots, `list-${tag}.png`) });

    // All: the ended ones too, after the open ones
    await page.locator('[data-test=deliverables-chip-all]').click();
    await expect(page.locator('[data-test=deliverables-row]')).toHaveCount(7);
    await expect(page.locator('[data-test=deliverables-row]').last()).toHaveAttribute('data-status', /expired|closed/);
    if (shots && w === 1280) await page.screenshot({ path: path.join(shots, `all-${tag}.png`) });

    // A row's pane: spec, cards, activity, time; actions for full access only
    await page.locator('[data-test=deliverables-row][data-wf="10"]').click();
    const pane = page.locator('[data-test=deliverable-pane]');
    await expect(pane).toBeVisible();
    await expect(pane.locator('[data-test=deliverable-pane-title]')).toHaveText('Partial refunds');
    await expect(pane.locator('[data-test=deliverable-pane-spec]')).toContainText('refunds are asked for every day');
    await expect(pane.locator('[data-test=deliverable-pane-card]')).toHaveCount(6);
    await expect(pane.locator('[data-test=deliverable-pane-message]').first()).toHaveAttribute('data-kind', 'progress');
    await expect(pane.locator('[data-test=deliverable-pane-breakdown-row]').first()).toBeVisible();
    if (access === 'read') await expect(pane.locator('[data-test=deliverable-pane-actions]')).toHaveCount(0);
    else await expect(pane.locator('[data-test=deliverable-pane-stop]')).toBeVisible();
    expect(s.core.calls).toContain('deliverables.get');
    expect(await noSideScroll(page), 'the page never scrolls sideways with the pane open').toBe(true);
    if (shots) await page.screenshot({ path: path.join(shots, `pane-${tag}.png`) });

    if (w < 760) {
      // On a phone the pane took the page; Back returns to the list
      await expect(page.locator('[data-test=deliverables-listwrap]')).toBeHidden();
      await pane.locator('[data-test=deliverable-pane-back]').click();
      await expect(page.locator('[data-test=deliverables-listwrap]')).toBeVisible();
    } else {
      // The elapsed time opens the Timeline dialog
      await page.locator('[data-test=deliverables-row][data-wf="10"] [data-test=deliverables-timeline]').click();
      await expect(page.locator('[data-test=timeline-dialog]')).toBeVisible();
      if (shots && access === 'full') await page.screenshot({ path: path.join(shots, `timeline-${tag}.png`) });
    }
    expect(errors).toEqual([]);
    await ctx.close();
  });
}

test('the item link opens the Backlog at that item; the filters live in the address', async ({ browser }) => {
  const { page, ctx, errors } = await open(browser, 'full', 1280, 800, 'light', '#deliverables?s=all&q=refund');
  await expect(page.locator('[data-test=deliverables-chip-all]')).toHaveAttribute('aria-selected', 'true');
  await expect(page.locator('[data-test=deliverables-search]')).toHaveValue('refund');
  await expect(page.locator('[data-test=deliverables-hit]').first()).toBeVisible();
  if (shots) await page.screenshot({ path: path.join(shots, 'filtered-full-1280-light.png') });
  await page.locator('[data-test=deliverables-row][data-wf="12"] [data-test=deliverable-item-link]').click();
  await expect(page.locator('[data-test=backlog-view]')).toBeVisible();
  await expect(page.locator('[data-test=backlog-item] .mono').first()).toHaveText('B012');
  expect(errors).toEqual([]);
  await ctx.close();
});
