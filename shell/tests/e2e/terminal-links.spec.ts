// A terminal link (OSC 8) in the remote view (remote.html in headless Chrome
// against the FAKE core of phone-remote.spec.ts) asks through the app's in-page confirm dialog (confirm.ts), never the
// browser's confirm(): any page 'dialog' event fails the test.
//  - Clicking an https OSC 8 link shows [data-test=confirm-dialog] with xterm's question and OK labelled "Open".
//  - Cancel, and Escape, open nothing (no window.open call, no popup).
//  - Open opens the URL exactly once, in a new page (window.open observed and the popup counted; example.com is
//    answered by a route, no network).
//  - A non-http(s) link (file:, javascript:) neither asks nor opens.
// DeveloperAda's pane is on Terminal and its session is interactive (so the pane shows the xterm, not the transcript).
//
//   npx playwright test terminal-links   (E2E_SKIP_BUILD=1 reuses out/e2e/renderer)
import { expect, test, type Browser, type Page } from './phone/fixtures';
import path from 'node:path';
import { mkdirSync } from 'node:fs';
import { answers } from './phone/fake-core';
import { buildRenderer, builtDir, fakeRemote } from './phone/server';

test.use({ channel: 'chrome', headless: true, actionTimeout: 8_000 });
test.setTimeout(90_000);

const SHOTS = path.join(process.env.YAWBLE_TEAM_DOCS || path.resolve(__dirname, '..', '..', 'test-results'), 'terminal-links');
const URL_ = 'https://example.com/docs?q=1';
const osc8 = (uri: string, text: string) => `\x1b]8;;${uri}\x1b\\${text}\x1b]8;;\x1b\\`;
const REPLAY = [
  '$ cat links.txt',
  `web:  ${osc8(URL_, 'DOCSLINK')}`,
  `file: ${osc8('file:///etc/passwd', 'FILELINK')}`,
  `js:   ${osc8('javascript:alert(1)', 'JSLINK')}`,
  '',
].join('\r\n');
const replay = Buffer.from(REPLAY, 'utf8');

const saved: Record<string, unknown> = {};
const OVERRIDES: Record<string, (p?: any) => unknown> = {
  'terminal.banner': (p) => ({ memberId: p?.memberId, channel: `member:${p?.memberId}`, title: 'payments / DeveloperAda', details: [], loosened: [],
    state: 'running', queuePosition: null, queueLength: 0, runId: 41, interactive: true, lastRunState: null, lastRunReason: null }),
  'terminal.attach': (p) => ({ channel: p?.channel, replay: replay.toString('base64'), end: replay.length,
    session: { kind: 'interactive', runId: 41, startedAt: new Date().toISOString(), inputEnabled: false, command: 'claude', ended: false, exitCode: null, endReason: null } }),
  'terminal.resize': () => null,
  'terminal.events': () => ({ end: replay.length, chunks: [] }),
};

test.beforeAll(async () => {
  test.setTimeout(600_000);
  mkdirSync(SHOTS, { recursive: true });
  for (const k of Object.keys(OVERRIDES)) { saved[k] = (answers as any)[k]; (answers as any)[k] = OVERRIDES[k]; }
  await buildRenderer();
});
test.afterAll(() => { for (const k of Object.keys(saved)) { if (saved[k] === undefined) delete (answers as any)[k]; else (answers as any)[k] = saved[k]; } });

async function open(browser: Browser, w: number, h: number) {
  const s = fakeRemote(builtDir);
  s.core.setAsks(false);
  const mobile = w < 760;
  const ctx = await browser.newContext({ viewport: { width: w, height: h }, isMobile: mobile, hasTouch: mobile, deviceScaleFactor: 2 });
  await ctx.addInitScript(() => {
    try { localStorage.setItem('yawble.memberPaneView', JSON.stringify({ 'payments/DeveloperAda': 'terminal' })); } catch { /* none */ }
    // Observe window.open (still calling the real one, so a popup really opens).
    const real = window.open.bind(window);
    (window as any).__opens = [];
    window.open = ((...a: Parameters<typeof window.open>) => { (window as any).__opens.push(a.map((x) => (x === undefined ? null : String(x)))); return real(...a); }) as typeof window.open;
  });
  await s.attach(ctx);
  // The opened address is answered here: no network.
  await ctx.route('https://example.com/**', (r) => r.fulfill({ status: 200, contentType: 'text/html', body: '<title>example stub</title>stub' }));
  const popups: string[] = [];
  const page = await ctx.newPage();
  ctx.on('page', (p) => { if (p !== page) popups.push(p.url() || '(pending)'); });
  const errors: string[] = [];
  const dialogs: string[] = [];
  page.on('pageerror', (e) => errors.push(e.message));
  page.on('dialog', (d) => { dialogs.push(`${d.type()}: ${d.message()}`); void d.dismiss(); });
  await page.goto(`${s.url}#team=1`);
  const pane = page.locator('[data-test=agent-DeveloperAda]');
  if (mobile) {
    await expect(pane.locator('[data-test=member-view-report]')).toHaveAttribute('aria-selected', 'true', { timeout: 30_000 });
    await pane.locator('[data-test=member-view-terminal]').click();
  }
  await expect(pane.locator('[data-test=member-terminal]')).toBeVisible({ timeout: 30_000 });
  await expect(pane.locator('.xterm-rows')).toContainText('DOCSLINK', { timeout: 15_000 });
  await page.waitForTimeout(300);
  return { page, ctx, pane, errors, dialogs, popups };
}

const opens = (page: Page) => page.evaluate(() => (window as any).__opens as (string | null)[][]);
const box = (page: Page) => page.locator('[data-test=confirm-dialog]');

/** Clicks the visible text of a link in the xterm (hover first: xterm finds an OSC 8 link on mouse move). */
async function clickLink(page: Page, text: string, mobile: boolean) {
  const span = page.locator('[data-test=agent-DeveloperAda] .xterm-rows span', { hasText: text }).first();
  const r = (await span.boundingBox())!;
  // The span may hold more than the link's text; aim at the link's first characters.
  const full = (await span.textContent()) ?? text;
  const at = Math.max(0, full.indexOf(text));
  const x = r.x + (r.width * (at + 2)) / Math.max(full.length, 1);
  const y = r.y + r.height / 2;
  await page.mouse.move(x - 30, y);
  await page.mouse.move(x, y, { steps: 4 });
  await page.waitForTimeout(150);
  if (mobile) { await page.mouse.down(); await page.mouse.up(); } else await page.mouse.click(x, y);
  await page.waitForTimeout(300);
}

for (const [w, h] of [[1280, 800], [390, 844]] as const) {
  const mobile = w < 760;
  test.describe(`${w}x${h}`, () => {
    test('https link: in-page dialog; Cancel opens nothing', async ({ browser }) => {
      const { page, ctx, errors, dialogs, popups } = await open(browser, w, h);
      await clickLink(page, 'DOCSLINK', mobile);
      await expect(box(page)).toBeVisible();
      await expect(page.locator('[data-test=confirm-message]')).toContainText(`Do you want to navigate to ${URL_}?`);
      await expect(page.locator('[data-test=confirm-ok]')).toHaveText('Open');
      await page.screenshot({ path: path.join(SHOTS, `dialog-${w}x${h}.png`) });
      await page.locator('[data-test=confirm-cancel]').click();
      await expect(box(page)).toHaveCount(0);
      await page.waitForTimeout(800);
      expect(await opens(page), 'Cancel: no window.open').toEqual([]);
      expect(popups, 'Cancel: no popup').toEqual([]);
      expect(dialogs, 'no browser dialog').toEqual([]);
      expect(errors).toEqual([]);
      await ctx.close();
    });

    test('https link: Escape opens nothing', async ({ browser }) => {
      const { page, ctx, errors, dialogs, popups } = await open(browser, w, h);
      await clickLink(page, 'DOCSLINK', mobile);
      await expect(box(page)).toBeVisible();
      await page.keyboard.press('Escape');
      await expect(box(page)).toHaveCount(0);
      await page.waitForTimeout(800);
      expect(await opens(page), 'Escape: no window.open').toEqual([]);
      expect(popups, 'Escape: no popup').toEqual([]);
      expect(dialogs, 'no browser dialog').toEqual([]);
      expect(errors).toEqual([]);
      await ctx.close();
    });

    test('https link: Open opens the URL once in a new page', async ({ browser }) => {
      const { page, ctx, errors, dialogs, popups } = await open(browser, w, h);
      await clickLink(page, 'DOCSLINK', mobile);
      await expect(box(page)).toBeVisible();
      const popup = ctx.waitForEvent('page', { timeout: 10_000 });
      await page.locator('[data-test=confirm-ok]').click();
      const p = await popup;
      await p.waitForLoadState('domcontentloaded').catch(() => {});
      await expect(box(page)).toHaveCount(0);
      await page.waitForTimeout(1500);
      expect(await opens(page), 'Open: window.open once, _blank, noopener').toEqual([[URL_, '_blank', 'noopener,noreferrer']]);
      expect(popups.length, 'one popup').toBe(1);
      expect(p.url(), 'the popup is the link').toBe(URL_);
      expect(await p.evaluate(() => window.opener), 'noopener: the popup cannot reach the app').toBeNull();
      expect(dialogs, 'no browser dialog').toEqual([]);
      expect(errors).toEqual([]);
      await ctx.close();
    });

    test('non-http(s) links (file:, javascript:) neither ask nor open', async ({ browser }) => {
      const { page, ctx, errors, dialogs, popups } = await open(browser, w, h);
      for (const t of ['FILELINK', 'JSLINK']) {
        await clickLink(page, t, mobile);
        await page.waitForTimeout(500);
        await expect(box(page), `${t}: no dialog`).toHaveCount(0);
      }
      await page.screenshot({ path: path.join(SHOTS, `non-http-${w}x${h}.png`) });
      expect(await opens(page), 'no window.open').toEqual([]);
      expect(popups, 'no popup').toEqual([]);
      expect(dialogs, 'no browser dialog').toEqual([]);
      expect(errors).toEqual([]);
      // Control: the https link on the same screen still asks (the clicks above did land on the terminal).
      await clickLink(page, 'DOCSLINK', mobile);
      await expect(box(page)).toBeVisible();
      await page.keyboard.press('Escape');
      await ctx.close();
    });
  });
}
