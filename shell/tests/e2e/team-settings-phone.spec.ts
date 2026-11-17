// Card 328: the team Settings dialog's tab row on a phone, in a real browser (headless Chrome) against the FAKE core of
// phone-remote.spec.ts (remote.html, canned answers). At 390x844 (an iPhone) the eight tabs stay on one line and the row
// scrolls sideways: every manage-tab-* button is one line tall, no two tabs overlap, the last one (Delete) is reached by
// scrolling and its pane shows when tapped. The sheet's Close keeps its 44 px tap target, without a button box.
// At 1280x800 the row does not scroll and every tab is one line, as before.
//
//   npx playwright test team-settings-phone      (B037_SKIP_BUILD=1 reuses out/b037/renderer)
import { expect, test, type Browser, type Page } from '@playwright/test';
import path from 'node:path';
import { mkdirSync } from 'node:fs';
import { answers } from './phone/fake-core';
import { buildRenderer, builtDir, fakeRemote } from './phone/server';

// The Settings dialog's own calls (not in the shared fake): no environment variables, integrations, MCP servers or APIs.
answers['teams.env'] = () => ({});
answers['integrations.list'] = () => ({ integrations: [] });
answers['integrations.mcpList'] = () => [];
answers['integrations.apiList'] = () => [];

test.use({ channel: 'chrome', headless: true, actionTimeout: 8_000 });
test.setTimeout(60_000);

const SHOTS = path.join(process.env.YAWBLE_TEAM_DOCS || path.resolve(__dirname, '..', '..', 'test-results'), process.env.C328_SHOTS || 'C328-screens');

test.beforeAll(async () => { test.setTimeout(600_000); mkdirSync(SHOTS, { recursive: true }); await buildRenderer(); });

async function openSettings(browser: Browser, w: number, h: number) {
  const s = fakeRemote(builtDir);
  s.core.setAsks(false);
  const mobile = w < 760;
  const ctx = await browser.newContext({ viewport: { width: w, height: h }, isMobile: mobile, hasTouch: mobile, deviceScaleFactor: 3 });
  await s.attach(ctx);
  const page = await ctx.newPage();
  const errors: string[] = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.goto(`${s.url}#team=1`);
  await expect(page.locator('[data-test=team-title]')).toBeVisible({ timeout: 30_000 });
  // On a phone Settings is in the team page's ⋯ menu (B054).
  const manage = page.locator('[data-test=open-manage]').first();
  if (!(await manage.isVisible().catch(() => false))) {
    await page.locator('[data-test=team-more]').click();
    await expect(page.locator('[data-test=team-more-menu]')).toBeVisible();
  }
  await manage.click();
  await expect(page.locator('[data-test=manage-tabs]')).toBeVisible();
  await page.waitForTimeout(300);
  await expect(page.locator('[data-test=manage-error]')).toHaveCount(0);
  return { page, ctx, errors };
}

/** Each tab's box, its text's line height, and the row's scroll extent. */
const tabInfo = (page: Page) => page.evaluate(() => {
  const row = document.querySelector<HTMLElement>('[data-test=manage-tabs]')!;
  const tabs = [...row.querySelectorAll<HTMLElement>('button[data-test^=manage-tab-]')].map((b) => {
    const r = b.getBoundingClientRect(), cs = getComputedStyle(b);
    const line = parseFloat(cs.lineHeight) || parseFloat(cs.fontSize) * 1.3;
    // The text's own rectangles: one per line it is laid out on.
    const range = document.createRange();
    range.selectNodeContents(b);
    const lines = new Set([...range.getClientRects()].map((q) => Math.round(q.top))).size;
    // The label's own height (the button is 44 px tall on a phone, a tap target, whatever its text).
    const text = range.getBoundingClientRect().height;
    return { id: b.dataset.test!, left: r.left, right: r.right, top: r.top, bottom: r.bottom, h: r.height, w: r.width, text, line, lines, fits: b.scrollWidth <= b.clientWidth + 1 };
  });
  const rr = row.getBoundingClientRect();
  return { tabs, row: { left: rr.left, right: rr.right, scrollW: row.scrollWidth, clientW: row.clientWidth, scrollLeft: row.scrollLeft }, docW: document.documentElement.scrollWidth, vw: innerWidth };
});

function checkTabs(m: Awaited<ReturnType<typeof tabInfo>>) {
  expect(m.tabs.map((t) => t.id)).toEqual(['general', 'state', 'repos', 'env', 'network', 'integrations', 'reset', 'delete'].map((t) => `manage-tab-${t}`));
  for (const t of m.tabs) {
    expect.soft(t.lines, `${t.id}: one line of text`).toBe(1);
    expect.soft(t.text, `${t.id}: its label is one line tall (${t.text} px, line ${t.line} px)`).toBeLessThan(t.line * 1.5);
    expect.soft(t.h, `${t.id}: the button is no taller than a 44 px tap target`).toBeLessThanOrEqual(44.5);
    expect.soft(t.fits, `${t.id}: its label fits its box`).toBe(true);
  }
  for (let i = 0; i < m.tabs.length; i++)
    for (let j = i + 1; j < m.tabs.length; j++) {
      const a = m.tabs[i]!, b = m.tabs[j]!;
      const ow = Math.min(a.right, b.right) - Math.max(a.left, b.left), oh = Math.min(a.bottom, b.bottom) - Math.max(a.top, b.top);
      expect.soft(ow > 0.5 && oh > 0.5, `${a.id} and ${b.id} overlap`).toBe(false);
    }
  expect.soft(m.docW, 'the page does not scroll sideways').toBeLessThanOrEqual(m.vw);
}

test('phone 390x844: one line of tabs that scrolls sideways; Delete is reached and opens its pane', async ({ browser }) => {
  const { page, ctx, errors } = await openSettings(browser, 390, 844);
  await page.screenshot({ path: path.join(SHOTS, 'team-settings-390x844.png') });
  const m = await tabInfo(page);
  // eslint-disable-next-line no-console
  console.log(JSON.stringify(m));
  checkTabs(m);
  expect(m.row.scrollW, 'the eight tabs are wider than the screen: the row scrolls').toBeGreaterThan(m.row.clientW);

  // The sheet's Close: still a 44 px tap target.
  const close = await page.locator('[data-test=modal-close]').boundingBox();
  expect(close && `${Math.round(close.width)}x${Math.round(close.height)}`).toBe('44x44');

  // Scroll to Delete, tap it: its pane shows, and it is fully inside the row.
  const del = page.locator('[data-test=manage-tab-delete]');
  await del.scrollIntoViewIfNeeded();
  await del.tap();
  await expect(page.locator('[data-test=team-delete-pane]')).toBeVisible();
  await expect(del).toHaveClass(/\bon\b/);
  const after = await tabInfo(page);
  const d = after.tabs.find((t) => t.id === 'manage-tab-delete')!;
  expect(d.left, 'Delete inside the row (left)').toBeGreaterThanOrEqual(after.row.left - 1);
  expect(d.right, 'Delete inside the row (right)').toBeLessThanOrEqual(after.row.right + 1);
  await page.screenshot({ path: path.join(SHOTS, 'team-settings-390x844-delete.png') });

  // Choosing General (off screen to the left now) scrolls it back into the row.
  await page.evaluate(() => { const g = document.querySelector<HTMLElement>('[data-test=manage-tab-general]')!; g.click(); });
  await expect(page.locator('[data-test=team-general]')).toBeVisible();
  await expect.poll(async () => (await tabInfo(page)).row.scrollLeft, 'the row scrolled back to General').toBe(0);
  expect(errors).toEqual([]);
  await ctx.close();
});

test('desktop 1280x800: the tab row is as before (no sideways scroll, one line each)', async ({ browser }) => {
  const { page, ctx, errors } = await openSettings(browser, 1280, 800);
  await page.screenshot({ path: path.join(SHOTS, 'team-settings-1280x800.png') });
  const m = await tabInfo(page);
  checkTabs(m);
  expect(m.row.scrollW, 'the row does not scroll').toBeLessThanOrEqual(m.row.clientW);
  const ov = await page.locator('[data-test=manage-tabs]').evaluate((e) => getComputedStyle(e).overflowX);
  expect(ov, 'desktop row overflow unchanged').toBe('visible');
  expect(errors).toEqual([]);
  await ctx.close();
});
