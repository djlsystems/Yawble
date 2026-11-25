// The overall time and the Timeline dialog in a real browser (headless Chrome), on remote.html against the
// FAKE core of phone-remote.spec.ts (the core's wire shapes, canned). Opened at #workflow=10 (the team's
// workflows page, #10's pane open): the pane's overall time opens the dialog with a summary row and one row per card. A Visitor sees the same.
// At 375 px the timeline's axis scrolls inside the dialog and the page never scrolls sideways. Screenshots go to
// $YAWBLE_TEAM_DOCS/timeline when that is set.
//
//   npx playwright test workflow-timeline      (E2E_SKIP_BUILD=1 reuses out/e2e/renderer)
import { expect, test, type Browser } from './phone/fixtures';
import path from 'node:path';
import { type Access } from './phone/fake-core';
import { buildRenderer, builtDir, fakeRemote } from './phone/server';

test.use({ channel: 'chrome', headless: true, actionTimeout: 8_000 });
test.setTimeout(60_000);

test.beforeAll(async () => { test.setTimeout(600_000); await buildRenderer(); });

const shots = process.env.YAWBLE_TEAM_DOCS ? path.join(process.env.YAWBLE_TEAM_DOCS, 'timeline') : null;

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
  // #workflow=10 opens the Workflows page with #10's pane (no dialog)
  await expect(page.locator('[data-test=workflows-view]')).toBeVisible({ timeout: 30_000 });
  await expect(page.locator('[data-test=workflow-pane]')).toHaveAttribute('data-id', '10', { timeout: 15_000 });
  await expect(page.locator('[role=dialog]')).toHaveCount(0);
  return { s, page, ctx, errors };
}

for (const [access, w, h, theme] of [['full', 1280, 800, 'light'], ['read', 1280, 800, 'dark'], ['full', 375, 667, 'light'], ['read', 375, 667, 'dark']] as const) {
  test(`${access} at ${w}x${h} (${theme}): the workflow's overall time opens its timeline`, async ({ browser }) => {
    const { s, page, ctx, errors } = await open(browser, access, w, h, theme);
    // the pane's Time block (on a phone the page's rows hide while the pane is open)
    const time = page.locator('[data-test=workflow-pane] [data-test=workflow-pane-time] [data-test=overall-time]');
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
