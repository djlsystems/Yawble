// Card 430 (B086): the overall time and the Timeline dialog in a real browser (headless Chrome), on remote.html against the
// FAKE core of phone-remote.spec.ts (core card 429's wire contract, canned). Opened at #workflow=10 (the team's
// workflows): the row's overall time opens the dialog with a summary row and one row per card. A Visitor sees the same.
// At 375 px the timeline's axis scrolls inside the dialog and the page never scrolls sideways. Screenshots go to
// $YAWBLE_TEAM_DOCS/B086 when that is set.
//
//   npx playwright test card430-timeline      (B037_SKIP_BUILD=1 reuses out/b037/renderer)
import { expect, test, type Browser } from './phone/fixtures';
import path from 'node:path';
import { type Access } from './phone/fake-core';
import { buildRenderer, builtDir, fakeRemote } from './phone/server';

test.use({ channel: 'chrome', headless: true, actionTimeout: 8_000 });
test.setTimeout(60_000);

test.beforeAll(async () => { test.setTimeout(600_000); await buildRenderer(); });

const shots = process.env.YAWBLE_TEAM_DOCS ? path.join(process.env.YAWBLE_TEAM_DOCS, 'B086') : null;

async function open(browser: Browser, access: Access, w: number, h: number, theme: 'light' | 'dark' = 'light') {
  const s = fakeRemote(builtDir);
  s.core.setAsks(false);
  s.core.setAccess(access);
  const mobile = w < 760;
  const ctx = await browser.newContext({ viewport: { width: w, height: h }, isMobile: mobile, hasTouch: mobile, colorScheme: theme });
  await s.attach(ctx);
  const page = await ctx.newPage();
  const errors: string[] = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.goto(`${s.url}#workflow=10`);
  await expect(page.locator('[data-test=workflow-10]')).toBeVisible({ timeout: 30_000 });
  return { s, page, ctx, errors };
}

for (const [access, w, h, theme] of [['full', 1280, 800, 'light'], ['read', 1280, 800, 'dark'], ['full', 375, 667, 'light'], ['read', 375, 667, 'dark']] as const) {
  test(`${access} at ${w}x${h} (${theme}): the workflow's overall time opens its timeline`, async ({ browser }) => {
    const { s, page, ctx, errors } = await open(browser, access, w, h, theme);
    const time = page.locator('[data-test=workflow-10] [data-test=overall-time]');
    await expect(time).toBeVisible();
    await expect(time).toHaveText(/\dh \d\dm \d\ds/);
    await time.click();
    const dlg = page.locator('[data-test=timeline-dialog]');
    await expect(dlg).toBeVisible();
    await expect(dlg.locator('[data-test=timeline-row]')).toHaveCount(7);
    await expect(dlg.locator('[data-test=timeline-breakdown-row]').first()).toBeVisible();
    await expect(dlg.locator('[data-test=timeline-legend]')).toContainText('Stalled');
    expect(s.core.calls).toContain('timeline.get');
    // a band by keyboard: its words in the readout
    await dlg.locator('.band.stalled').first().focus();
    await expect(dlg.locator('[data-test=timeline-readout]')).toContainText('Stalled');
    const sizes = await page.evaluate(() => {
      const sc = document.querySelector('[data-test=timeline-scroll]') as HTMLElement;
      return { page: document.scrollingElement!.scrollWidth, view: window.innerWidth, scroll: sc.scrollWidth, box: sc.clientWidth };
    });
    expect(sizes.page, 'the page never scrolls sideways').toBeLessThanOrEqual(sizes.view);
    if (w < 760) expect(sizes.scroll, 'the axis scrolls inside the dialog').toBeGreaterThan(sizes.box);
    if (shots) await page.screenshot({ path: path.join(shots, `timeline-workflow-${access}-${w}-${theme}.png`), fullPage: false });
    expect(errors).toEqual([]);
    await ctx.close();
  });
}
