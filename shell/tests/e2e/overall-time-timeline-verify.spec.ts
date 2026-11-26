// The tester's own e2e of the Timeline dialog on remote.html against the phone fake core
// (tests/e2e/phone, the core's wire contract canned), beside deliverable-timeline.spec.ts:
// - a CARD (the board's card 3, which has a 38 min Stalled stretch) and a DELIVERABLE (#10, summary row + one row per
//   card) open the dialog from their overall time, in light and dark at 1280 and 375; screenshots of both;
// - the planned / started line ("planned … · started … · 20m before it started"), the Stalled band (hatched), markers;
// - a break mark on a long quiet gap (deliverable #12 with the expiry fixtures on: 66 h, mostly Idle);
// - a Visitor (read access) gets the same card dialog; timeline.get is asked;
// - keyboard: the overall time opens the dialog with Enter, Tab reaches the bands, a band's words show in the readout;
// - at 375 the axis scrolls inside the dialog and the page never scrolls sideways;
// - no tokens or cost anywhere in the dialog.
//
//   npx playwright test overall-time-timeline-verify      (E2E_SKIP_BUILD=1 reuses out/e2e/renderer)
// Screenshots go to $YAWBLE_TEAM_DOCS/timeline-verify (else test-results/timeline-verify).
import { expect, test, type Browser, type Page } from './phone/fixtures';
import { mkdirSync } from 'node:fs';
import path from 'node:path';
import { type Access } from './phone/fake-core';
import { buildRenderer, builtDir, fakeRemote } from './phone/server';

test.use({ channel: 'chrome', headless: true, actionTimeout: 8_000 });
test.setTimeout(90_000);

const SHOTS = path.join(process.env.YAWBLE_TEAM_DOCS || path.resolve(__dirname, '..', '..', 'test-results'), 'timeline-verify');

test.beforeAll(async () => { test.setTimeout(600_000); mkdirSync(SHOTS, { recursive: true }); await buildRenderer(); });

type Theme = 'light' | 'dark';

async function open(browser: Browser, o: { access?: Access; w?: number; h?: number; theme?: Theme; expiry?: boolean; hash?: string } = {}) {
  const { access = 'full', w = 1280, h = 800, theme = 'light', expiry = false, hash = '' } = o;
  const s = fakeRemote(builtDir);
  s.core.setAsks(false);
  s.core.setAccess(access);
  if (expiry) s.core.setExpiry(true);
  const mobile = w < 760;
  const ctx = await browser.newContext({ viewport: { width: w, height: h }, isMobile: mobile, hasTouch: mobile, colorScheme: theme });
  await ctx.addInitScript((t) => { try { localStorage.setItem('yawble.remoteTheme', t); } catch { /* none */ } }, theme);
  await s.attach(ctx);
  const page = await ctx.newPage();
  const errors: string[] = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.goto(`${s.url}${hash}`);
  await expect(page.locator('html')).toHaveAttribute('data-theme', theme);
  return { s, page, ctx, errors };
}

/** The Deliverables page at #deliverable=N: the page shows, deliverable N's pane is open; returns the pane's overall time.
 * (On a phone the page's rows hide while a pane is open, so the pane's Time block is the overall time at every width.) */
async function paneTime(page: Page, id: number) {
  await expect(page.locator('[data-test=deliverables-view]')).toBeVisible({ timeout: 30_000 });
  const pane = page.locator('[data-test=deliverable-pane]');
  await expect(pane).toHaveAttribute('data-id', String(id), { timeout: 15_000 });
  await expect(page.locator('[role=dialog]')).toHaveCount(0);
  return pane.locator('[data-test=deliverable-pane-time] [data-test=overall-time]');
}

async function board(page: Page) {
  await expect(page.locator('[data-test=sidebar]')).toBeAttached({ timeout: 30_000 });
  const nav = page.locator('[data-test=nav-board]').first();
  if (!(await nav.isVisible())) await page.locator('[aria-controls=app-drawer]').first().click();
  await nav.click();
  // a phone shows one lane at a time: card 3 is In Progress
  const tab = page.getByRole('tab', { name: /In Progress/ });
  if (await tab.isVisible()) await tab.click();
  await expect(page.locator('[data-test=board-card-3]')).toBeVisible({ timeout: 15_000 });
}

/** The dialog's text never names tokens or cost: time only. */
async function timeOnly(page: Page) {
  const text = (await page.locator('[data-test=timeline-dialog]').innerText()).toLowerCase();
  expect(text).not.toMatch(/token|cost|\$|usd|price/);
}

async function sideways(page: Page) {
  return page.evaluate(() => {
    const sc = document.querySelector('[data-test=timeline-scroll]') as HTMLElement;
    const before = sc.scrollLeft;
    sc.scrollLeft = 120;
    const moved = sc.scrollLeft - before;
    sc.scrollLeft = 0;
    window.scrollTo(200, window.scrollY);
    const pageX = window.scrollX;
    return { page: document.scrollingElement!.scrollWidth, view: window.innerWidth, scroll: sc.scrollWidth, box: sc.clientWidth, moved, pageX };
  });
}

for (const [w, h] of [[1280, 800], [375, 667]] as const) {
  for (const theme of ['light', 'dark'] as const) {
    test(`a card's overall time opens its timeline at ${w} (${theme})`, async ({ browser }) => {
      const { s, page, ctx, errors } = await open(browser, { w, h, theme });
      await board(page);
      const card = page.locator('[data-test=board-card-3]');
      const time = card.locator('[data-test=overall-time]');
      await expect(time).toBeVisible();
      await expect(time).toHaveText(/^\s*\dh \d\dm \d\ds\s*$/);
      // one quiet line in the bottom row
      const box = (await time.boundingBox())!;
      expect(box.height, 'the overall time stays one line').toBeLessThan(20);
      await time.click();
      const dlg = page.locator('[data-test=timeline-dialog]');
      await expect(dlg).toBeVisible();
      await expect(dlg).toHaveAttribute('data-kind', 'card');
      await expect(dlg).toHaveAttribute('data-id', '3');
      // the card did not open its own detail (the click stopped at the time)
      expect(s.core.calls).toContain('timeline.get');
      await expect(dlg.locator('[data-test=timeline-row]')).toHaveCount(1);
      await expect(dlg.locator('.lane.summary')).toHaveCount(0);
      await expect(dlg.locator('[data-test=timeline-started]')).toContainText(/planned \d\d:\d\d · started \d\d:\d\d/);
      await expect(dlg.locator('[data-test=timeline-before-start]')).toContainText('20m before it started');
      await expect(dlg.locator('[data-test=timeline-open]')).toBeVisible();
      await expect(dlg.locator('.band.stalled')).toHaveCount(1);
      await expect(dlg.locator('.band.stalled')).toHaveAttribute('aria-label', /^Stalled, .* to .*, 38m 00s$/);
      await expect(dlg.locator('[data-test=timeline-marker][data-kind=watchdog-end]')).toHaveCount(1);
      await expect(dlg.locator('[data-test=timeline-marker][data-kind=started]')).toHaveCount(1);
      // Stalled reads apart from Agent working: hatched, not the same flat fill
      const fills = await dlg.evaluate((d) => ({
        stalled: getComputedStyle(d.querySelector('.band.stalled')!).backgroundImage,
        working: getComputedStyle(d.querySelector('.band.agent-working')!).backgroundImage,
      }));
      expect(fills.stalled).toContain('repeating-linear-gradient');
      expect(fills.working).toBe('none');
      // the breakdown's shares add up to 100
      const pcts = await dlg.locator('[data-test=timeline-breakdown-row] td:nth-child(3)').allInnerTexts();
      expect(pcts.reduce((a, t) => a + Number(t.replace('%', '')), 0)).toBe(100);
      await timeOnly(page);
      const sz = await sideways(page);
      expect(sz.page, 'the page never scrolls sideways').toBeLessThanOrEqual(sz.view);
      expect(sz.pageX).toBe(0);
      if (w < 760) {
        expect(sz.scroll, 'the axis is wider than the dialog').toBeGreaterThan(sz.box);
        expect(sz.moved, 'the axis scrolls inside the dialog').toBeGreaterThan(0);
      }
      await page.screenshot({ path: path.join(SHOTS, `timeline-card-${w}-${theme}.png`) });
      expect(errors).toEqual([]);
      await ctx.close();
    });

    test(`a deliverable's overall time opens its timeline at ${w} (${theme})`, async ({ browser }) => {
      const { s, page, ctx, errors } = await open(browser, { w, h, theme, hash: '#deliverable=10' });
      const time = await paneTime(page, 10);
      await expect(time).toHaveText(/\dh \d\dm \d\ds/);
      await time.click();
      const dlg = page.locator('[data-test=timeline-dialog]');
      await expect(dlg).toBeVisible();
      await expect(dlg).toHaveAttribute('data-kind', 'deliverable');
      expect(s.core.calls).toContain('timeline.get');
      await expect(dlg.locator('.lane.summary')).toHaveCount(1);
      await expect(dlg.locator('.lane.summary')).toContainText('All cards');
      await expect(dlg.locator('[data-test=timeline-row]')).toHaveCount(7);
      await expect(dlg.locator('[data-test=timeline-started]')).toContainText(/first message \d\d:\d\d · first card started \d\d:\d\d/);
      await expect(dlg.locator('.lane:not(.summary) .band.stalled')).toHaveCount(1);
      expect(await dlg.locator('.lane.summary [data-test=timeline-band]').count()).toBeGreaterThan(1);
      await expect(dlg.locator('[data-test=timeline-legend]')).toContainText('Stalled');
      await timeOnly(page);
      const sz = await sideways(page);
      expect(sz.page).toBeLessThanOrEqual(sz.view);
      expect(sz.pageX).toBe(0);
      if (w < 760) expect(sz.moved).toBeGreaterThan(0);
      await page.screenshot({ path: path.join(SHOTS, `timeline-deliverable-${w}-${theme}.png`) });
      expect(errors).toEqual([]);
      await ctx.close();
    });
  }
}

for (const [w, h, theme] of [[1280, 800, 'light'], [375, 667, 'dark']] as const) {
  test(`a long quiet gap is compressed behind a break mark at ${w} (${theme})`, async ({ browser }) => {
    const { page, ctx, errors } = await open(browser, { w, h, theme, expiry: true, hash: '#deliverable=12' });
    await (await paneTime(page, 12)).click();
    const dlg = page.locator('[data-test=timeline-dialog]');
    await expect(dlg).toBeVisible();
    await expect(dlg.locator('[data-test=timeline-break]').first()).toBeAttached();
    await expect(dlg.locator('.ticks .tb').first()).toContainText('≈');
    // the 40 min of work stays readable: its band is a real share of the axis, not a sliver
    const width = await dlg.locator('.band.agent-working').first().evaluate((b) => b.getBoundingClientRect().width / (b.parentElement as HTMLElement).getBoundingClientRect().width);
    expect(width).toBeGreaterThan(0.05);
    await page.screenshot({ path: path.join(SHOTS, `timeline-break-${w}-${theme}.png`) });
    expect(errors).toEqual([]);
    await ctx.close();
  });
}

// F1: at phone width the global touch rule (styles.css: button { min-height: 44px }) also hits the timeline's
// band and marker buttons: a marker is a 44 px square turned 45 deg (62 x 62 px on screen) and a band 44 px tall in a
// 26 px track, so the markers cover the bands and the start tick. Fixed: TimelineDialog.vue opts its
// .band / .mk out of the 44 px minimum.
test('F1: at 375 the bands stay inside their track and the markers keep their size', async ({ browser }) => {
  const { page, ctx } = await open(browser, { w: 375, h: 667, hash: '#deliverable=10' });
  await (await paneTime(page, 10)).click();
  await expect(page.locator('[data-test=timeline-band]').first()).toBeVisible();
  const m = await page.evaluate(() => {
    const box = (e: Element) => e.getBoundingClientRect();
    const mk = box(document.querySelector('.mk')!);
    const band = box(document.querySelector('.band')!);
    const track = box(document.querySelector('.track')!);
    return { mk: Math.round(Math.max(mk.width, mk.height)), band: Math.round(band.height), track: Math.round(track.height) };
  });
  await ctx.close();
  expect(m.band, 'a band is no taller than its track').toBeLessThanOrEqual(m.track);
  expect(m.mk, 'a marker stays about 14 px (20 px turned)').toBeLessThanOrEqual(22);
});

// F3 (fixed): deliverable #12's break mark sits about 96% along the axis; its centred label
// ("≈ 2d 17h") drew over the end label ("now"). Now it slides clear of the start / end labels, or hides if no room.
for (const [w, h] of [[1280, 800], [375, 667]] as const) {
  test(`F3: a break label near the axis end never meets the start / end labels at ${w}`, async ({ browser }) => {
    const { page, ctx, errors } = await open(browser, { w, h, expiry: true, hash: '#deliverable=12' });
    await (await paneTime(page, 12)).click();
    const dlg = page.locator('[data-test=timeline-dialog]');
    await expect(dlg.locator('[data-test=timeline-break-label]').first()).toBeAttached();
    await expect.poll(async () => page.evaluate(() => {
      const r = (e: Element) => e.getBoundingClientRect();
      const t0 = r(document.querySelector('.ticks .t0')!), t1 = r(document.querySelector('.ticks .t1')!);
      const row = r(document.querySelector('.ticks')!);
      const bad: string[] = [];
      for (const tb of document.querySelectorAll<HTMLElement>('.ticks .tb')) {
        if (getComputedStyle(tb).visibility === 'hidden') continue;
        const b = r(tb);
        if (b.left < t0.right) bad.push(`${tb.textContent} meets the start label`);
        if (b.right > t1.left) bad.push(`${tb.textContent} meets the end label`);
        if (b.left < row.left || b.right > row.right) bad.push(`${tb.textContent} leaves the axis`);
      }
      return bad;
    })).toEqual([]);
    // at 1280 there is room: the label shows (slid clear, not hidden) and still reads its length
    if (w === 1280) {
      const tb = dlg.locator('[data-test=timeline-break-label]').first();
      await expect(tb).toBeVisible();
      await expect(tb).toHaveText(/^≈ \d+d \d+h$/);
    }
    expect(errors).toEqual([]);
    await ctx.close();
  });
}
test('a Visitor gets the same card dialog (timeline.get is a read)', async ({ browser }) => {
  const { s, page, ctx, errors } = await open(browser, { access: 'read' });
  await board(page);
  await page.locator('[data-test=board-card-3] [data-test=overall-time]').click();
  const dlg = page.locator('[data-test=timeline-dialog]');
  await expect(dlg).toBeVisible();
  await expect(dlg.locator('[data-test=timeline-error]')).toHaveCount(0);
  await expect(dlg.locator('.band.stalled')).toHaveCount(1);
  expect(s.core.calls).toContain('timeline.get');
  await page.screenshot({ path: path.join(SHOTS, 'timeline-card-visitor-1280-light.png') });
  expect(errors).toEqual([]);
  await ctx.close();
});

test('keyboard: Enter on the overall time opens the dialog, Tab reaches the bands and the readout says them', async ({ browser }) => {
  const { page, ctx, errors } = await open(browser);
  await board(page);
  const time = page.locator('[data-test=board-card-3] [data-test=overall-time]');
  await time.focus();
  await expect(time).toBeFocused();
  await page.keyboard.press('Enter');
  const dlg = page.locator('[data-test=timeline-dialog]');
  await expect(dlg).toBeVisible();
  await expect(dlg.locator('[data-test=timeline-band]').first()).toBeVisible();
  const bands = await dlg.locator('[data-test=timeline-band]').count();
  const said: string[] = [];
  for (let i = 0; i < 40 && said.length < bands; i++) {
    await page.keyboard.press('Tab');
    const f = await page.evaluate(() => ({ test: (document.activeElement as HTMLElement | null)?.dataset?.test ?? '', label: document.activeElement?.getAttribute('aria-label') ?? '' }));
    if (f.test === 'timeline-band') {
      said.push(f.label);
      await expect(dlg.locator('[data-test=timeline-readout]')).toContainText(f.label);
    }
  }
  expect(said.length, 'every band is reached by Tab').toBe(bands);
  expect(said.some((l) => l.startsWith('Stalled'))).toBe(true);
  // the focus ring shows on a focused band
  const outline = await page.evaluate(() => getComputedStyle(document.activeElement!).outlineStyle);
  expect(outline).not.toBe('none');
  await page.keyboard.press('Escape');
  await expect(dlg).toHaveCount(0);
  expect(errors).toEqual([]);
  await ctx.close();
});
