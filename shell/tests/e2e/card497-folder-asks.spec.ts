// Card 497 (B111): folder asks in a real browser (headless Chrome), on remote.html against the FAKE core of
// phone-remote.spec.ts with one read-write folder ask pending (setFolderAsks). At 1280 and 375 px, light and dark: the
// Needs-you banner shows the card (team / member, read-write, the path, the reason, the folder exists) with Approve,
// Approve read-only and Deny; Approve read-only sends folder.answer read and the banner says it applies from the next
// run; the page never scrolls sideways. A Visitor (read-only) sees the card with its answers held. Screenshots go to
// $YAWBLE_TEAM_DOCS/b111/.
//
//   npx playwright test card497-folder-asks      (B037_SKIP_BUILD=1 reuses out/b037/renderer)
import { expect, test, type Browser } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import path from 'node:path';
import { type Access } from './phone/fake-core';
import { buildRenderer, builtDir, fakeRemote } from './phone/server';

test.use({ channel: 'chrome', headless: true, actionTimeout: 8_000 });
test.setTimeout(60_000);

test.beforeAll(async () => { test.setTimeout(600_000); await buildRenderer(); });

const shots = process.env.YAWBLE_TEAM_DOCS ? path.join(process.env.YAWBLE_TEAM_DOCS, 'b111') : null;
if (shots) mkdirSync(shots, { recursive: true });

async function open(browser: Browser, access: Access, w: number, h: number, theme: 'light' | 'dark') {
  const s = fakeRemote(builtDir);
  s.core.setAsks(false);
  s.core.setFolderAsks(true);
  s.core.setAccess(access);
  const mobile = w < 760;
  const ctx = await browser.newContext({ viewport: { width: w, height: h }, isMobile: mobile, hasTouch: mobile, colorScheme: theme, reducedMotion: 'reduce' });
  await s.attach(ctx);
  const page = await ctx.newPage();
  const errors: string[] = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.goto(s.url);
  await expect(page.locator('[data-test=banners] [data-test=folder-ask]')).toBeVisible({ timeout: 30_000 });
  return { s, page, ctx, errors };
}
const noSideScroll = async (page: import('@playwright/test').Page) =>
  page.evaluate(() => document.scrollingElement!.scrollWidth <= window.innerWidth + 1);

for (const [w, h, theme] of [[1280, 800, 'light'], [1280, 800, 'dark'], [375, 740, 'light'], [375, 740, 'dark']] as const) {
  test(`at ${w}px (${theme}): the folder ask in Needs you, Approve read-only`, async ({ browser }) => {
    const { s, page, ctx, errors } = await open(browser, 'full', w, h, theme);
    const card = page.locator('[data-test=banners] [data-test=folder-ask]');
    await expect(card.locator('[data-test=folder-ask-head]')).toContainText('payments / TesterBen asks read-write on Z:\\smokesessions - read the smoke session recordings');
    await expect(card.locator('[data-test=folder-ask-exists]')).toHaveText('The folder exists.');
    await expect(card.locator('.buttons button')).toHaveText(['Approve', 'Approve read-only', 'Deny']);
    await expect(page.locator('[data-test=folder-asks] .title')).toContainText('Needs you: an agent asks for a folder outside its clone');
    expect(await noSideScroll(page), 'the page never scrolls sideways').toBe(true);
    if (w < 760) {
      // A phone: the buttons stack, each the card's width.
      const [a, b] = await card.locator('.buttons button').evaluateAll((els) => els.slice(0, 2).map((e) => e.getBoundingClientRect()));
      expect(b.top).toBeGreaterThan(a.top);
      expect(Math.abs(a.width - b.width)).toBeLessThan(2);
    }
    if (shots) await card.screenshot({ path: path.join(shots, `b111-folder-ask-${w}-${theme}.png`) });
    if (shots) await page.screenshot({ path: path.join(shots, `b111-needs-you-${w}-${theme}.png`) });

    await card.locator('[data-test=folder-ask-read]').click();
    await expect(page.locator('[data-test=folder-ask-answered]')).toContainText('Approved read-only for TesterBen on Z:\\smokesessions: applies from its next run.');
    await expect(page.locator('[data-test=folder-ask]')).toHaveCount(0);
    expect(s.core.folderAnswers().get(21)).toBe('read');
    if (shots) await page.locator('[data-test=folder-asks]').screenshot({ path: path.join(shots, `b111-answered-${w}-${theme}.png`) });
    expect(errors).toEqual([]);
    await ctx.close();
  });
}

test('a Visitor (read-only) sees the ask with its answers held, and sends nothing', async ({ browser }) => {
  const { s, page, ctx, errors } = await open(browser, 'read', 375, 740, 'light');
  const card = page.locator('[data-test=banners] [data-test=folder-ask]');
  await expect(card.locator('[data-test=folder-ask-head]')).toContainText('asks read-write on Z:\\smokesessions');
  for (const b of await card.locator('.buttons button').all()) await expect(b).toBeDisabled();
  expect(s.core.folderAnswers().size).toBe(0);
  if (shots) await card.screenshot({ path: path.join(shots, 'b111-folder-ask-visitor-375-light.png') });
  expect(errors).toEqual([]);
  await ctx.close();
});
