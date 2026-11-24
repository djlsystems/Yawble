// Card 519 (B115 verify), TesterKofi: what embed-mode.spec.ts and settings-gear.spec.ts leave out, in headless
// Chrome against the FAKE core of phone-remote.spec.ts (no cloud, no real core, no agent). A test parent page on the
// same origin frames remote.html?embed=1&theme=..., as the cloud page will.
//  - embedded at 1280 and 375 in light and dark (?theme=light too, not only dark), screenshots to $YAWBLE_TEAM_DOCS/B115;
//  - yawble.nav is posted again after a change in the app's state: a new chat, a team's state (dot and badge);
//  - chatAction delete: OK deletes (concierge.delete), the chat leaves the nav; rename: the in-frame EmbedRenameDialog,
//    Enter renames (concierge.rename with the trimmed title), Escape cancels (no call);
//  - place logs opens the Logs dialog, newTeam the New team dialog, as the sidebar's clicks;
//  - a Visitor (read-only): readOnly, canNewTeam false, no chat actions; newTeam and chatAction are ignored;
//  - a parent on ANOTHER origin: the frame posts nothing it can read, and its navigate / theme messages are ignored;
//  - File > Settings (the menu entry) opens Settings, plain and embedded, and an embedded phone offers no drawer menu.
//
//   npx playwright test card519-b115-verify --workers=1      (E2E_SKIP_BUILD=1 reuses out/e2e/renderer)
import { expect, test, type Browser, type Page } from './phone/fixtures';
import path from 'node:path';
import { answers } from './phone/fake-core';
import { ORIGIN, buildRenderer, ensureDir, fakeRemote } from './phone/server';

test.use({ channel: 'chrome', headless: true, actionTimeout: 8_000 });
test.setTimeout(60_000);

const shots = process.env.YAWBLE_TEAM_DOCS ? path.join(process.env.YAWBLE_TEAM_DOCS, 'B115') : null;
const OTHER = 'http://cloud-elsewhere.test';

test.beforeAll(async () => { test.setTimeout(600_000); if (shots) ensureDir(shots); await buildRenderer(); });

// The fake core's answers this spec replaces for one test, put back after it.
const saved = { ...answers };
test.afterEach(() => { for (const k of Object.keys(answers)) if (!(k in saved)) delete answers[k]; Object.assign(answers, saved); });

const parentHtml = (src: string) => `<!doctype html><html><head><meta name="viewport" content="width=device-width, initial-scale=1">
<style>html,body{margin:0;height:100%}iframe{display:block;width:100%;height:100%;border:0}</style></head><body>
<iframe id="f" title="Yawble" src="${src}"></iframe>
<script>
  window.got = []; window.any = [];
  const f = document.getElementById('f');
  window.addEventListener('message', (e) => {
    window.any.push(e.data);
    if (e.origin !== '${ORIGIN}' || e.source !== f.contentWindow) return;
    window.got.push(e.data);
  });
  window.send = (m) => f.contentWindow.postMessage(m, '*');
</script></body></html>`;

async function openEmbedded(browser: Browser, w: number, h: number, opts: { theme?: 'light' | 'dark'; access?: 'full' | 'read'; parent?: string } = {}) {
  const mobile = w < 760;
  const ctx = await browser.newContext({ viewport: { width: w, height: h }, isMobile: mobile, hasTouch: mobile, colorScheme: opts.theme === 'light' ? 'dark' : 'light', reducedMotion: 'reduce' });
  const s = fakeRemote();
  s.core.setAsks(false);
  if (opts.access) s.core.setAccess(opts.access);
  await s.attach(ctx);
  const parent = opts.parent ?? ORIGIN;
  const src = `${ORIGIN}/remote.html?embed=1&amp;theme=${opts.theme ?? 'dark'}`;
  await ctx.route(`${parent}/cloud-parent.html`, (route) => route.fulfill({ status: 200, contentType: 'text/html', body: parentHtml(src) }));
  const page = await ctx.newPage();
  const errors: string[] = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.goto(`${parent}/cloud-parent.html`);
  const frame = page.frameLocator('#f');
  await expect(frame.locator('.app.embed')).toBeAttached({ timeout: 30_000 });
  return { page, frame, errors, core: s.core, push: s.push };
}

const got = (page: Page) => page.evaluate(() => (window as any).got as any[]);
const navs = async (page: Page) => (await got(page)).filter((m) => m.type === 'yawble.nav').map((m) => m.nav);
const lastNav = async (page: Page) => (await navs(page)).at(-1);
const send = (page: Page, m: unknown) => page.evaluate((x) => (window as any).send(x), m);
const frameWin = (page: Page) => page.frames().find((f) => f.url().includes('remote.html'))!;
async function navReady(page: Page) {
  await expect.poll(() => lastNav(page).then((n) => n?.teams?.length ?? 0), { timeout: 15_000 }).toBeGreaterThan(0);
  await expect.poll(() => lastNav(page).then((n) => n?.runSlots ?? null), { timeout: 15_000 }).toMatch(/^Run slots: /);
}

for (const [w, h] of [[1280, 800], [375, 740]] as const) {
  for (const theme of ['light', 'dark'] as const) {
    test(`embedded at ${w}px, ?theme=${theme} (OS the other way): that theme, no sidebar, drawer or theme switch, the gear kept`, async ({ browser }) => {
      const { page, frame, errors } = await openEmbedded(browser, w, h, { theme });
      await navReady(page);
      await expect(frame.locator('[data-test=sidebar]')).toHaveCount(0);
      await expect(frame.locator('[data-test=drawer-toggle]')).toHaveCount(0);
      await expect(frame.locator('[data-test=theme-toggle]')).toHaveCount(0);
      await expect(frame.getByTestId('settings-gear')).toBeVisible();
      const m = await frameWin(page).evaluate(() => {
        const g = document.querySelector('[data-testid=settings-gear]')!.getBoundingClientRect();
        return { theme: document.documentElement.getAttribute('data-theme'), right: window.innerWidth - g.right, top: g.top,
          sideways: document.scrollingElement!.scrollWidth > window.innerWidth + 1 };
      });
      expect(m.theme).toBe(theme);
      expect(m.right, 'the gear at the right').toBeLessThanOrEqual(24);
      expect(m.top, 'the gear in the top bar').toBeLessThanOrEqual(16);
      expect(m.sideways).toBe(false);
      // An embedded phone has no drawer and no menu bar: nothing from the drawer's menu group is offered.
      if (w < 760) {
        await expect(frame.locator('[data-test=menubar]')).toHaveCount(0);
        await expect(frame.locator('#app-drawer')).toHaveCount(0);
      }
      if (shots) await page.screenshot({ path: path.join(shots, `card519-embed-${w}-${theme}.png`) });
      expect(errors).toEqual([]);
    });
  }
}

test('embedded: yawble.nav is posted again after a change: a new chat, a team\'s state', async ({ browser }) => {
  const { page, push } = await openEmbedded(browser, 1280, 800);
  await navReady(page);
  const first = await lastNav(page);
  const pay = first.teams.find((t: any) => t.id === 1);
  expect(pay).toMatchObject({ name: 'payments', dot: 'running', badge: 1 });
  expect(first.places.find((p: any) => p.id === 'teams').badge).toBe(2);
  expect(first.canNewTeam).toBe(true);
  expect(first.readOnly).toBe(false);
  expect(first.chats.find((c: any) => c.id === 'c1').actions).toEqual([
    { id: 'rename', label: 'Rename', danger: false }, { id: 'delete', label: 'Delete', danger: true }]);
  const count = (await navs(page)).length;

  // A new chat appears in the core's list.
  const list = saved['concierge.list'];
  answers['concierge.list'] = (p, a) => [...(list(p, a) as any[]), { id: 'c9', title: 'A chat started elsewhere', cli: 'claude', teamId: null, team: null,
    createdAt: new Date().toISOString(), updatedAt: new Date().toISOString(), running: false, unread: 0, watching: 0 }];
  // The core tells the page its chats changed (as it does when a chat is started elsewhere).
  push('concierge.update', { conversationId: 'c9' });
  await expect.poll(async () => (await lastNav(page)).chats.map((c: any) => c.id), { timeout: 15_000 }).toContain('c9');

  // The payments team stops running: its dot goes idle and its badge goes.
  const idle = (rows: any[]) => rows.map((t) => (t.id === 1 ? { ...t, running: 0, queued: 0 } : t));
  const tl = saved['teams.list']; const ov = saved['app.overview'];
  answers['teams.list'] = (p, a) => idle(tl(p, a) as any[]);
  answers['app.overview'] = (p, a) => { const o = ov(p, a) as any; return { ...o, teams: idle(o.teams) }; };
  await expect.poll(async () => (await lastNav(page)).teams.find((t: any) => t.id === 1), { timeout: 15_000 })
    .toMatchObject({ dot: 'idle', badge: null });
  expect((await navs(page)).length).toBeGreaterThanOrEqual(count + 2);
  // Nothing posted twice in a row unchanged.
  const all = (await navs(page)).map((n) => JSON.stringify(n));
  for (let i = 1; i < all.length; i++) expect(all[i], `nav ${i} differs from the one before`).not.toBe(all[i - 1]);
});

test('embedded: chatAction delete (OK) deletes; rename asks in the frame, Enter renames, Escape cancels', async ({ browser }) => {
  const { page, frame, errors } = await openEmbedded(browser, 1280, 800);
  await navReady(page);
  let chats = (saved['concierge.list'](null, 'full') as any[]).map((c) => ({ ...c }));
  const renamed: any[] = []; const deleted: any[] = [];
  answers['concierge.list'] = () => chats;
  answers['concierge.rename'] = (p) => { renamed.push(p); chats = chats.map((c) => (c.id === p.id ? { ...c, title: p.title } : c)); return { ok: true }; };
  answers['concierge.delete'] = (p) => { deleted.push(p); chats = chats.filter((c) => c.id !== p.id); return { ok: true }; };

  // Rename: the dialog in the frame, filled with the title; Escape: nothing.
  await send(page, { type: 'yawble.navigate', to: { kind: 'chatAction', id: 'c1', action: 'rename' } });
  await expect(frame.locator('[data-test=embed-rename]')).toBeVisible();
  await expect(frame.locator('[data-test=embed-rename-input]')).toHaveValue('Payments today');
  if (shots) await page.screenshot({ path: path.join(shots, 'card519-embed-rename-1280.png') });
  await frame.locator('[data-test=embed-rename-input]').press('Escape');
  await expect(frame.locator('[data-test=embed-rename]')).toHaveCount(0);
  expect(renamed).toEqual([]);
  // Again; Enter renames with the trimmed title, and the nav follows.
  await send(page, { type: 'yawble.navigate', to: { kind: 'chatAction', id: 'c1', action: 'rename' } });
  await frame.locator('[data-test=embed-rename-input]').fill('  Refunds today  ');
  await frame.locator('[data-test=embed-rename-input]').press('Enter');
  await expect(frame.locator('[data-test=embed-rename]')).toHaveCount(0);
  await expect.poll(() => renamed).toEqual([{ id: 'c1', title: 'Refunds today' }]);
  await expect.poll(async () => (await lastNav(page)).chats.find((c: any) => c.id === 'c1')?.title, { timeout: 15_000 }).toBe('Refunds today');

  // Delete: the in-frame confirm; OK deletes c1 and it leaves the nav.
  await send(page, { type: 'yawble.navigate', to: { kind: 'chatAction', id: 'c1', action: 'delete' } });
  await expect(frame.locator('[data-test=confirm-message]')).toHaveText('Delete this chat?');
  if (shots) await page.screenshot({ path: path.join(shots, 'card519-embed-delete-1280.png') });
  await frame.locator('[data-test=confirm-ok]').click();
  await expect.poll(() => deleted).toEqual([{ id: 'c1' }]);
  await expect.poll(async () => (await lastNav(page)).chats.map((c: any) => c.id), { timeout: 15_000 }).not.toContain('c1');
  expect(errors).toEqual([]);
});

test('embedded at 375px: rename and delete dialogs fit the phone frame', async ({ browser }) => {
  const { page, frame } = await openEmbedded(browser, 375, 740, { theme: 'light' });
  await navReady(page);
  await send(page, { type: 'yawble.navigate', to: { kind: 'chatAction', id: 'c1', action: 'rename' } });
  const box = await frame.locator('[data-test=embed-rename]').boundingBox();
  expect(box!.x).toBeGreaterThanOrEqual(0);
  expect(box!.x + box!.width).toBeLessThanOrEqual(375);
  if (shots) await page.screenshot({ path: path.join(shots, 'card519-embed-rename-375-light.png') });
  await frame.locator('[data-test=embed-rename-cancel]').click();
  await expect(frame.locator('[data-test=embed-rename]')).toHaveCount(0);
});

test('embedded: place logs opens Logs and newTeam opens New team, as the sidebar\'s clicks', async ({ browser }) => {
  const { page, frame, errors } = await openEmbedded(browser, 1280, 800);
  await navReady(page);
  await send(page, { type: 'yawble.navigate', to: { kind: 'place', id: 'logs' } });
  await expect(frame.locator('[data-test=logs]')).toBeVisible();
  await frame.locator('[data-test=logs]').press('Escape');
  await expect(frame.locator('[data-test=logs]')).toHaveCount(0);
  await send(page, { type: 'yawble.navigate', to: { kind: 'newTeam' } });
  await expect(frame.locator('[data-test=team-name]')).toBeVisible();
  for (const id of ['concierge', 'backlog', 'workflows']) {
    await send(page, { type: 'yawble.navigate', to: { kind: 'place', id } });
  }
  await expect.poll(async () => (await lastNav(page)).places.find((p: any) => p.active)?.id).toBe('workflows');
  expect(errors).toEqual([]);
});

test('embedded Visitor (read-only): readOnly, no New team, no chat actions; those navigates are ignored', async ({ browser }) => {
  const { page, frame, core } = await openEmbedded(browser, 1280, 800, { access: 'read' });
  await navReady(page);
  await expect.poll(async () => (await lastNav(page)).readOnly, { timeout: 15_000 }).toBe(true);
  const nav = await lastNav(page);
  expect(nav.canNewTeam).toBe(false);
  for (const c of nav.chats) expect(c.actions).toEqual([]);
  await send(page, { type: 'yawble.navigate', to: { kind: 'newTeam' } });
  await send(page, { type: 'yawble.navigate', to: { kind: 'chatAction', id: 'c1', action: 'delete' } });
  await send(page, { type: 'yawble.navigate', to: { kind: 'chatAction', id: 'c1', action: 'rename' } });
  await page.waitForTimeout(400);
  await expect(frame.locator('[data-test=team-name]')).toHaveCount(0);
  await expect(frame.locator('[data-test=confirm-dialog]')).toHaveCount(0);
  await expect(frame.locator('[data-test=embed-rename]')).toHaveCount(0);
  expect(core.calls.filter((m) => m === 'concierge.delete' || m === 'concierge.rename')).toEqual([]);
  // A Visitor still moves between places.
  await send(page, { type: 'yawble.navigate', to: { kind: 'place', id: 'board' } });
  await expect(frame.locator('[data-test=board]')).toBeVisible();
});

test('a parent on another origin: it reads nothing the frame posts, and its navigate and theme messages are ignored', async ({ browser }) => {
  const { page, frame, errors } = await openEmbedded(browser, 1280, 800, { parent: OTHER });
  await expect(frame.locator('[data-test=concierge-hero], [data-test=concierge]').first()).toBeVisible({ timeout: 15_000 });
  await page.waitForTimeout(1500);
  expect(await page.evaluate(() => (window as any).any.length), 'posted to its own origin only').toBe(0);
  await send(page, { type: 'yawble.theme', theme: 'light' });
  await send(page, { type: 'yawble.navigate', to: { kind: 'place', id: 'monitor' } });
  await page.waitForTimeout(500);
  await expect(frame.locator('[data-test=monitor]')).toHaveCount(0);
  expect(await frame.locator('html').getAttribute('data-theme')).toBe('dark');
  expect(errors).toEqual([]);
});

test('File > Settings opens Settings: the plain remote view and embedded, at 1280px', async ({ browser }) => {
  const ctx = await browser.newContext({ viewport: { width: 1280, height: 800 }, reducedMotion: 'reduce' });
  const s = fakeRemote();
  s.core.setAsks(false);
  await s.attach(ctx);
  const plain = await ctx.newPage();
  await plain.goto(s.url);
  await expect(plain.locator('[data-test=sidebar]')).toBeAttached({ timeout: 30_000 });
  await plain.locator('[data-test=menu-file]').click();
  await plain.locator('[data-test=menuitem-settings]').click();
  await expect(plain.locator('[data-test=settings]')).toBeVisible();
  // The View menu has no Settings entry and never had one: File > Settings is the menu entry.
  await plain.locator('[data-test=menu-view]').click();
  await expect(plain.locator('[data-test=menuitem-terminal]')).toBeVisible();
  await expect(plain.locator('[data-test=menuitem-settings]')).toHaveCount(0);

  const { page, frame } = await openEmbedded(browser, 1280, 800);
  await navReady(page);
  await frame.locator('[data-test=menu-view]').click();
  await expect(frame.locator('[data-test=menuitem-sidebar]')).toHaveCount(0);
  await expect(frame.locator('[data-test=menuitem-toggleTheme]')).toHaveCount(0);
  await expect(frame.locator('[data-test=menuitem-terminal]')).toBeVisible();
  await frame.locator('[data-test=menu-view]').click();
  await expect(frame.locator('[data-test=menuitem-terminal]')).toHaveCount(0);
  await frame.locator('[data-test=menu-file]').click();
  await frame.locator('[data-test=menuitem-settings]').click();
  await expect(frame.locator('[data-test=settings]')).toBeVisible();
  await expect.poll(async () => (await lastNav(page)).places.some((p: any) => p.active)).toBe(false);
});
