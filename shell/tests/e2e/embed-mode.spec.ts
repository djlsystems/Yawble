// B115 Part B (card 518, step 2): the remote view embedded in the cloud page, in headless Chrome against the FAKE core
// of phone-remote.spec.ts. A test parent page on the same origin frames remote.html?embed=1&theme=dark, as the cloud page
// will: it records what the frame posts (yawble.ready, yawble.nav) and sends yawble.navigate and yawble.theme back. At
// 1280x800 and 375x740: no sidebar, drawer, menu button or theme switch in the frame, the gear kept, the screens the
// frame's full width; the screens follow the parent's navigate messages and its theme messages set data-theme; the
// viewer's kept yawble.remoteTheme is neither read nor written. The remote view without embed=1 keeps its sidebar and
// theme switch. No cloud, no real core, no agent. Screenshots go to $YAWBLE_TEAM_DOCS/B115/.
//
//   npx playwright test b115-embed --workers=1      (B037_SKIP_BUILD=1 reuses out/b037/renderer)
import { expect, test, type Browser, type FrameLocator, type Page } from './phone/fixtures';
import path from 'node:path';
import { ORIGIN, buildRenderer, ensureDir, fakeRemote } from './phone/server';

test.use({ channel: 'chrome', headless: true, actionTimeout: 8_000 });
test.setTimeout(60_000);

const shots = process.env.YAWBLE_TEAM_DOCS ? path.join(process.env.YAWBLE_TEAM_DOCS, 'B115') : null;

test.beforeAll(async () => { test.setTimeout(600_000); if (shots) ensureDir(shots); await buildRenderer(); });

// The cloud page's side of the contract, as small as it can be: the frame at full size, the messages from the frame
// (origin and source checked, as docs/cloud-link.md asks), and a send() for the test.
const PARENT = `<!doctype html><html><head><meta name="viewport" content="width=device-width, initial-scale=1">
<style>html,body{margin:0;height:100%}iframe{display:block;width:100%;height:100%;border:0}</style></head><body>
<iframe id="f" title="Yawble" src="/remote.html?embed=1&amp;theme=dark"></iframe>
<script>
  window.got = [];
  const f = document.getElementById('f');
  window.addEventListener('message', (e) => {
    if (e.origin !== location.origin || e.source !== f.contentWindow) return;
    window.got.push(e.data);
  });
  window.send = (m) => f.contentWindow.postMessage(m, location.origin);
</script></body></html>`;

/** The remote view's kept theme: written before the page loads, and every read or write of it in the frame counted. */
const KEPT = 'light';

async function openEmbedded(browser: Browser, w: number, h: number) {
  const mobile = w < 760;
  const ctx = await browser.newContext({ viewport: { width: w, height: h }, isMobile: mobile, hasTouch: mobile, colorScheme: 'light', reducedMotion: 'reduce' });
  const s = fakeRemote();
  s.core.setAsks(false);
  await s.attach(ctx);
  // Registered after attach: Playwright tries the newest route first.
  await ctx.route(`${ORIGIN}/cloud-parent.html`, (route) => route.fulfill({ status: 200, contentType: 'text/html', body: PARENT }));
  await ctx.addInitScript((kept) => {
    if (window === window.top) return;
    localStorage.setItem('yawble.remoteTheme', kept);
    const w = window as any;
    w.__remoteThemeUse = 0;
    const get = Storage.prototype.getItem; const set = Storage.prototype.setItem; const rem = Storage.prototype.removeItem;
    Storage.prototype.getItem = function (k: string) { if (k === 'yawble.remoteTheme') w.__remoteThemeUse++; return get.call(this, k); };
    Storage.prototype.setItem = function (k: string, v: string) { if (k === 'yawble.remoteTheme') w.__remoteThemeUse++; return set.call(this, k, v); };
    Storage.prototype.removeItem = function (k: string) { if (k === 'yawble.remoteTheme') w.__remoteThemeUse++; return rem.call(this, k); };
  }, KEPT);
  const page = await ctx.newPage();
  const errors: string[] = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.goto(`${ORIGIN}/cloud-parent.html`);
  const frame = page.frameLocator('#f');
  await expect(frame.locator('.app.embed')).toBeAttached({ timeout: 30_000 });
  // The first nav with the fake core's teams and run slots.
  await expect.poll(() => lastNav(page).then((n) => n?.teams?.length ?? 0), { timeout: 15_000 }).toBeGreaterThan(0);
  await expect.poll(() => lastNav(page).then((n) => n?.runSlots ?? null), { timeout: 15_000 }).toMatch(/^Run slots: /);
  return { page, frame, errors };
}

const got = (page: Page) => page.evaluate(() => (window as any).got as any[]);
const lastNav = async (page: Page) => (await got(page)).filter((m) => m.type === 'yawble.nav').at(-1)?.nav;
const send = (page: Page, m: unknown) => page.evaluate((x) => (window as any).send(x), m);
const frameWin = (page: Page) => page.frames().find((f) => f.url().includes('remote.html'))!;
const activePlace = async (page: Page) => (await lastNav(page))?.places.find((p: any) => p.active)?.id ?? null;

async function noSidebar(frame: FrameLocator) {
  await expect(frame.locator('[data-test=sidebar]')).toHaveCount(0);
  await expect(frame.locator('#app-drawer')).toHaveCount(0);
  await expect(frame.locator('[data-test=drawer-toggle]')).toHaveCount(0);
  await expect(frame.locator('[data-test=drawer-scrim]')).toHaveCount(0);
  await expect(frame.locator('[data-test=theme-toggle]')).toHaveCount(0);
  await expect(frame.getByTestId('settings-gear')).toBeVisible();
}

for (const [w, h] of [[1280, 800], [375, 740]] as const) {
  test(`embedded at ${w}px: no sidebar or theme switch, the gear kept, the screens full width`, async ({ browser }) => {
    const { page, frame, errors } = await openEmbedded(browser, w, h);
    await noSidebar(frame);
    if (w < 760) await expect(frame.locator('.app')).toHaveClass(/\bphone\b/);
    const m = await frameWin(page).evaluate(() => {
      const main = document.querySelector('.main')!.getBoundingClientRect();
      return { left: main.left, width: main.width, inner: window.innerWidth, theme: document.documentElement.getAttribute('data-theme'),
        sideways: document.scrollingElement!.scrollWidth > window.innerWidth + 1 };
    });
    expect(m.inner).toBe(w);
    expect(m.left, 'the screens start at the left edge').toBeLessThanOrEqual(0.5);
    expect(Math.abs(m.width - m.inner), 'the screens take the full width').toBeLessThanOrEqual(1);
    expect(m.theme, '?theme=dark over the kept light theme').toBe('dark');
    expect(m.sideways, 'no sideways scroll').toBe(false);
    // The gear still opens Settings.
    await frame.getByTestId('settings-gear').click();
    await expect(frame.locator('[data-test=settings]')).toBeVisible();
    if (shots) await page.screenshot({ path: path.join(shots, `b115-embed-${w}.png`) });
    expect(errors).toEqual([]);
  });
}

test('embedded: yawble.ready, then yawble.nav once and again on every change', async ({ browser }) => {
  const { page } = await openEmbedded(browser, 1280, 800);
  const all = await got(page);
  expect(all[0]).toEqual({ type: 'yawble.ready', version: 1 });
  expect(all.filter((m) => m.type === 'yawble.ready')).toHaveLength(1);
  const nav = await lastNav(page);
  expect(nav.places.map((p: any) => p.id)).toEqual(['newChat', 'concierge', 'teams', 'backlog', 'workflows', 'board', 'documents', 'monitor', 'scripts', 'logs']);
  expect(nav.teams.map((t: any) => t.name)).toContain('payments');
  expect(nav.chats.map((c: any) => c.title)).toContain('Payments today');
  const before = (await got(page)).length;
  await send(page, { type: 'yawble.navigate', to: { kind: 'place', id: 'monitor' } });
  await expect.poll(() => activePlace(page)).toBe('monitor');
  expect((await got(page)).length).toBeGreaterThan(before);
});

test('embedded at 375px: no Terminal or Help place; a terminal or help message does nothing', async ({ browser }) => {
  const { page, frame, errors } = await openEmbedded(browser, 375, 740);
  const ids = (await lastNav(page)).places.map((p: any) => p.id);
  expect(ids).not.toContain('terminal');
  expect(ids).not.toContain('help');
  await send(page, { type: 'yawble.navigate', to: { kind: 'place', id: 'terminal' } });
  await send(page, { type: 'yawble.navigate', to: { kind: 'place', id: 'help' } });
  await expect(frame.locator('[data-test=terminal-pane]')).toHaveCount(0);
  await expect(frame.locator('[data-test=about]')).toHaveCount(0);
  expect(errors).toEqual([]);
});

test('embedded: the screens follow the parent\'s navigate messages, as the sidebar\'s clicks', async ({ browser }) => {
  const { page, frame, errors } = await openEmbedded(browser, 1280, 800);
  await send(page, { type: 'yawble.navigate', to: { kind: 'place', id: 'board' } });
  await expect(frame.locator('[data-test=board]')).toBeVisible();
  await expect.poll(() => activePlace(page)).toBe('board');

  await send(page, { type: 'yawble.navigate', to: { kind: 'place', id: 'documents' } });
  await expect(frame.locator('[data-test=documents-view]')).toBeVisible();

  await send(page, { type: 'yawble.navigate', to: { kind: 'place', id: 'monitor' } });
  await expect(frame.locator('[data-test=monitor]')).toBeVisible();

  await send(page, { type: 'yawble.navigate', to: { kind: 'team', id: 1 } });
  await expect(frame.locator('section.team[data-commit-team="payments"]')).toBeVisible();
  await expect.poll(async () => (await lastNav(page)).teams.find((t: any) => t.id === 1)?.active).toBe(true);

  await send(page, { type: 'yawble.navigate', to: { kind: 'place', id: 'teams' } });
  await expect(frame.locator('.teams-list')).toBeVisible();

  await send(page, { type: 'yawble.navigate', to: { kind: 'chat', id: 'c1' } });
  await expect(frame.locator('[data-test=concierge]')).toBeVisible();
  await expect.poll(async () => (await lastNav(page)).chats.find((c: any) => c.id === 'c1')?.active).toBe(true);

  await send(page, { type: 'yawble.navigate', to: { kind: 'place', id: 'newChat' } });
  await expect(frame.locator('[data-test=concierge-hero]')).toBeVisible();

  // Delete asks in the frame first; Cancel leaves the chat.
  await send(page, { type: 'yawble.navigate', to: { kind: 'chatAction', id: 'c1', action: 'delete' } });
  await expect(frame.locator('[data-test=confirm-message]')).toHaveText('Delete this chat?');
  await frame.locator('[data-test=confirm-cancel]').click();
  await expect(frame.locator('[data-test=confirm-dialog]')).toHaveCount(0);
  expect((await lastNav(page)).chats.some((c: any) => c.id === 'c1')).toBe(true);

  // A target not in the nav, or a malformed message: nothing happens.
  await send(page, { type: 'yawble.navigate', to: { kind: 'team', id: 999 } });
  await send(page, { type: 'yawble.navigate', to: { kind: 'place', id: 'settings' } });
  await expect(frame.locator('[data-test=concierge-hero]')).toBeVisible();
  expect(errors).toEqual([]);
});

test('embedded: theme messages set data-theme; the kept yawble.remoteTheme is never read or written', async ({ browser }) => {
  const { page } = await openEmbedded(browser, 1280, 800);
  const f = frameWin(page);
  const theme = () => f.evaluate(() => document.documentElement.getAttribute('data-theme'));
  expect(await theme()).toBe('dark');
  await send(page, { type: 'yawble.theme', theme: 'light' });
  await expect.poll(theme).toBe('light');
  await send(page, { type: 'yawble.theme', theme: 'dark' });
  await expect.poll(theme).toBe('dark');
  await send(page, { type: 'yawble.theme', theme: 'purple' });
  // A message from the frame itself, not its parent: ignored.
  await f.evaluate(() => window.postMessage({ type: 'yawble.theme', theme: 'light' }, location.origin));
  await page.waitForTimeout(200);
  expect(await theme()).toBe('dark');
  // The counter is read before this check's own getItem (object literal order), so it counts the page's uses only.
  const kept = await f.evaluate(() => ({ use: (window as any).__remoteThemeUse, value: localStorage.getItem('yawble.remoteTheme') }));
  expect(kept.value).toBe(KEPT);
  expect(kept.use, 'the page itself never read or wrote yawble.remoteTheme').toBe(0);
});

for (const [w, h] of [[1280, 800], [375, 740]] as const) {
  test(`not embedded at ${w}px: the remote view keeps its sidebar and theme switch`, async ({ browser }) => {
    const mobile = w < 760;
    const ctx = await browser.newContext({ viewport: { width: w, height: h }, isMobile: mobile, hasTouch: mobile, colorScheme: 'light', reducedMotion: 'reduce' });
    const s = fakeRemote();
    s.core.setAsks(false);
    await s.attach(ctx);
    const page = await ctx.newPage();
    const errors: string[] = [];
    page.on('pageerror', (e) => errors.push(e.message));
    await page.goto(`${s.url}?theme=dark`);
    await expect(page.locator('[data-test=sidebar]')).toBeAttached({ timeout: 30_000 });
    await expect(page.locator('.app')).not.toHaveClass(/\bembed\b/);
    await expect(page.locator('[data-test=theme-toggle]')).toBeVisible();
    await expect(page.getByTestId('settings-gear')).toBeVisible();
    // ?theme= alone (no embed=1) changes nothing: the light OS; the switch keeps the viewer's choice as before.
    expect(await page.evaluate(() => document.documentElement.getAttribute('data-theme'))).toBe('light');
    await page.locator('[data-test=theme-toggle]').click();
    await expect.poll(() => page.evaluate(() => localStorage.getItem('yawble.remoteTheme'))).toBe('dark');
    if (mobile) {
      await expect(page.locator('[data-test=drawer-toggle]')).toBeVisible();
      await page.locator('[data-test=drawer-toggle]').click();
    }
    await expect(page.locator('[data-test=sidebar]')).toBeVisible();
    await expect(page.locator('[data-test=slots]')).toContainText('Run slots', { timeout: 15_000 });
    expect(errors).toEqual([]);
  });
}
