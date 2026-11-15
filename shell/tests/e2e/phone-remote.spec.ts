// B037 verify (card 169): remote.html on a phone, end to end in a real browser (headless Chrome), against a FAKE core
// (tests/e2e/phone/fake-core.ts: canned answers over the page's socket, nothing real behind it).
//
// Every screen at 390 x 844 and 360 x 740: the page must not scroll sideways (scrollWidth > innerWidth), no visible
// control may sit off the screen's edge, and none may be smaller than 44 x 44 px. Plus the on-screen keyboard (a fake
// visualViewport), the Visitor chip, the dark Monitor and its forces panel, the 759 / 760 px edges, rotation, and at
// 1280 px the desktop layout compared with main b726212's.
//
//   npx playwright test phone-remote            (B037_SKIP_BUILD=1 reuses out/b037/renderer*; the base tree is out/b037/main, from `git archive b726212 shell code-browser`)
// Screenshots go to $YAWBLE_TEAM_DOCS/B037-screens (else test-results/B037-screens); the audit to test-results/b037-audit.ndjson (node tests/e2e/phone/summary.mjs prints it).
import { expect, test, type Browser, type Page } from '@playwright/test';
import { appendFileSync, mkdirSync } from 'node:fs';
import path from 'node:path';
import { audit, type Audit } from './phone/audit';
import { baseBuiltDir, baseShellDir, buildRenderer, builtDir, fakeRemote } from './phone/server';

test.use({ channel: 'chrome', headless: true, actionTimeout: 8_000 });
test.setTimeout(60_000);

const SHOTS = path.join(process.env.YAWBLE_TEAM_DOCS || path.resolve(__dirname, '..', '..', 'test-results'), 'B037-screens');
// One line per audit (each worker appends; Playwright empties test-results at the start of a run).
const RESULTS = path.resolve(__dirname, '..', '..', 'test-results', 'b037-audit.ndjson');
const results = { push(r: { screen: string; size: string; audit: Audit }) { mkdirSync(path.dirname(RESULTS), { recursive: true }); appendFileSync(RESULTS, JSON.stringify(r) + '\n'); } };

test.beforeAll(async () => {
  test.setTimeout(600_000);
  mkdirSync(SHOTS, { recursive: true });
  await buildRenderer();
  if (process.env.B037_NO_BASE !== '1') await buildRenderer(baseShellDir, baseBuiltDir);
});

type Size = { w: number; h: number };
const PHONES: Size[] = [{ w: 390, h: 844 }, { w: 360, h: 740 }];

interface OpenOpts { access?: 'full' | 'read'; theme?: 'light' | 'dark'; dir?: string; mobile?: boolean; asks?: boolean }
async function open(browser: Browser, size: Size, o: OpenOpts = {}) {
  const s = fakeRemote(o.dir ?? builtDir);
  s.core.setAccess(o.access ?? 'full');
  s.core.setAsks(o.asks ?? true);
  const mobile = o.mobile ?? size.w < 760;
  const ctx = await browser.newContext({ viewport: { width: size.w, height: size.h }, isMobile: mobile, hasTouch: mobile, deviceScaleFactor: 2, colorScheme: o.theme ?? 'light' });
  await ctx.addInitScript((theme) => {
    try { localStorage.setItem('yawble.remoteTheme', theme); } catch { /* none */ }
    // A visual viewport the test can shrink, as an on-screen keyboard does (window.__keyboard(px)).
    const vv = new EventTarget() as EventTarget & { height: number; width: number; offsetTop: number; offsetLeft: number; scale: number; pageTop: number; pageLeft: number };
    Object.assign(vv, { offsetTop: 0, offsetLeft: 0, scale: 1, pageTop: 0, pageLeft: 0 });
    let kb = 0;
    Object.defineProperty(vv, 'height', { get: () => window.innerHeight - kb });
    Object.defineProperty(vv, 'width', { get: () => window.innerWidth });
    Object.defineProperty(window, 'visualViewport', { configurable: true, get: () => vv });
    (window as any).__keyboard = (px: number) => { kb = px; vv.dispatchEvent(new Event('resize')); };
  }, o.theme ?? 'light');
  await s.attach(ctx);
  const page = await ctx.newPage();
  const errors: string[] = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.goto(s.url);
  await expect(page.locator('[data-test=sidebar]')).toBeAttached({ timeout: 30_000 });
  await page.waitForTimeout(600);
  return { page, s, ctx, errors };
}

const tag = (size: Size) => `${size.w}x${size.h}`;

/** Screenshot + audit; soft-fails on overflow, an off-screen control (outside a sideways scroller) or a small control. */
async function check(page: Page, screen: string, size: Size, o: { allowSmall?: (c: Audit['small'][number]) => boolean; full?: boolean } = {}) {
  await page.waitForTimeout(250);
  await page.screenshot({ path: path.join(SHOTS, `${screen}-${tag(size)}.png`), fullPage: o.full ?? false });
  const a = await audit(page);
  results.push({ screen, size: tag(size), audit: a });
  expect.soft(a.overflow, `${screen} @${tag(size)}: page scrolls sideways (${a.scrollWidth} > ${a.width}); widest: ${a.wide.join(', ')}`).toBe(false);
  const off = a.offScreen.filter((c) => !c.inScroller);
  expect.soft(off, `${screen} @${tag(size)}: controls off the screen's edge`).toEqual([]);
  const small = a.small.filter((c) => !o.allowSmall?.(c));
  expect.soft(small, `${screen} @${tag(size)}: controls under 44 px`).toEqual([]);
  return a;
}

/** Taps the first chat in the open drawer; if it cannot be tapped (a fault), says so and opens it by a click event. */
async function openFirstChat(page: Page, size: Size) {
  const chat = page.locator('[data-test=sidebar] [data-test=chat-item]').first();
  if (await chat.click({ timeout: 3000 }).then(() => true, () => false)) return;
  expect.soft(false, `drawer @${tag(size)}: the first chat in the drawer cannot be tapped (covered or squeezed away)`).toBe(true);
  await chat.locator('button').first().dispatchEvent('click');
}

async function drawer(page: Page) {
  await page.locator('[data-test=drawer-toggle]').click();
  await expect(page.locator('[data-test=sidebar]')).toHaveAttribute('data-drawer', 'open');
}
async function nav(page: Page, id: string) {
  await drawer(page);
  await page.locator(`[data-test=sidebar] [data-test=${id}]`).first().click();
  await expect(page.locator('[data-test=sidebar]')).toHaveAttribute('data-drawer', 'closed');
}

for (const size of PHONES) {
  test.describe(`phone ${tag(size)}`, () => {
    test('Concierge with a network ask and a tool ask pending: the Needs-you banners leave room to chat', async ({ browser }) => {
      const { page, errors } = await open(browser, size);
      await expect(page.locator('[data-test=network-asks]')).toBeVisible();
      await check(page, 'concierge-with-asks', size);
      await drawer(page);
      await openFirstChat(page, size);
      await page.waitForTimeout(500);
      await check(page, 'concierge-chat-with-asks', size);
      const thread = await page.locator('[data-test=concierge-thread]').boundingBox();
      const composer = await page.locator('[data-test=composer]').boundingBox();
      expect.soft(thread && thread.height, 'the chat thread has room on screen with asks pending').toBeGreaterThan(80);
      expect.soft(composer && composer.y + composer.height, 'the composer is on screen with asks pending').toBeLessThanOrEqual(size.h + 1);
      expect.soft(composer && composer.height, 'the composer is not squeezed').toBeGreaterThan(40);
      // F1 re-verify: both banners together take at most 40% of the height (one box, data-test=banners) and scroll.
      const banners = async (name: string) => {
        const bb = await page.locator('[data-test=banners]').boundingBox();
        results.push({ screen: `${name}-banners-px`, size: tag(size), audit: { bannersH: bb?.height ?? -1 } as unknown as Audit });
        expect.soft(bb && bb.height, `${name} @${tag(size)}: banners' height (40% = ${Math.round(size.h * 0.4)} px)`).toBeLessThanOrEqual(size.h * 0.4 + 1);
        expect.soft(bb && bb.height, `${name} @${tag(size)}: banners shown`).toBeGreaterThan(40);
      };
      await banners('concierge');
      // The same two banners on the other screens (all but the board, which shows asks in its Needs You lane).
      for (const [id, name, sel] of [['nav-monitor', 'monitor', '[data-test=monitor]'], ['nav-teams', 'teams', '[data-test=team-grid]'], ['nav-documents', 'documents', '[data-test=documents-view]']] as const) {
        await nav(page, id);
        await page.waitForTimeout(500);
        await banners(name);
        await page.screenshot({ path: path.join(SHOTS, `${name}-with-asks-${tag(size)}.png`) });
        const b = await page.locator(sel).first().boundingBox();
        const shown = b ? Math.max(0, Math.min(b.y + b.height, size.h) - Math.max(b.y, 0)) : 0;
        expect.soft(shown, `${name} with asks pending: px of the screen left on screen`).toBeGreaterThan(size.h / 3);
      }
      expect(errors).toEqual([]);
    });

    test('Concierge: a new chat, a chat, the drawer, and the composer above the keyboard', async ({ browser }) => {
      const { page, errors } = await open(browser, size, { asks: false });
      await expect(page.locator('.app')).toHaveClass(/phone/);
      await check(page, 'concierge-new', size);
      await drawer(page);
      await check(page, 'drawer', size);
      // The drawer's Teams and Chats lists get room (two rows at least), and Settings is on screen or reachable by scrolling.
      const room = await page.locator('[data-test=sidebar]').evaluate((side) => {
        const lists = [...side.querySelectorAll<HTMLElement>('*')].find((e) => /(auto|scroll)/.test(getComputedStyle(e).overflowY) && e.querySelector('[data-test=chat-item]'));
        return { lists: lists ? lists.clientHeight : -1, sideScrolls: /(auto|scroll)/.test(getComputedStyle(side).overflowY), sideOver: side.scrollHeight - side.clientHeight };
      });
      expect.soft(room.lists, `drawer @${tag(size)}: px the Teams and Chats lists get`).toBeGreaterThanOrEqual(88);
      const settings = page.locator('[data-test=sidebar] [data-test=open-app-settings]');
      await settings.scrollIntoViewIfNeeded().catch(() => {});
      const sb = (await settings.boundingBox())!;
      expect.soft(sb.y + sb.height, `drawer @${tag(size)}: Settings on screen (drawer overflows by ${room.sideOver} px, scrolls: ${room.sideScrolls})`).toBeLessThanOrEqual(size.h + 1);
      await openFirstChat(page, size);
      await expect(page.locator('[data-test=concierge-thread]')).toBeVisible();
      await check(page, 'concierge-chat', size);
      // The composer stays above a keyboard that covers the bottom 320 px.
      const box = page.locator('[data-test=composer-text]');
      await box.click();
      await page.evaluate(() => (window as any).__keyboard(320));
      await page.waitForTimeout(300);
      const r = (await page.locator('[data-test=composer]').boundingBox())!;
      const send = (await page.locator('[data-test=composer-send]').boundingBox())!;
      await page.screenshot({ path: path.join(SHOTS, `concierge-keyboard-${tag(size)}.png`) });
      expect.soft(r.y + r.height, 'composer bottom above the keyboard').toBeLessThanOrEqual(size.h - 320 + 1);
      expect.soft(r.y, 'composer top on screen').toBeGreaterThanOrEqual(0);
      expect.soft(send.y + send.height, 'Send above the keyboard').toBeLessThanOrEqual(size.h - 320 + 1);
      await page.evaluate(() => (window as any).__keyboard(0));
      await page.waitForTimeout(200);
      const back = (await page.locator('[data-test=composer]').boundingBox())!;
      expect.soft(back.y + back.height, 'composer back at the bottom when the keyboard goes').toBeGreaterThan(size.h - 120);
      expect(errors).toEqual([]);
    });

    test('Teams: the list, a team, and the Code browser (list and file)', async ({ browser }) => {
      const { page, errors } = await open(browser, size, { asks: false });
      await nav(page, 'nav-teams');
      await expect(page.locator('[data-test=team-card]').first()).toBeVisible();
      await check(page, 'teams', size);
      await page.locator('[data-test=team-open-payments]').click();
      await expect(page.locator('[data-test=team-title]')).toBeVisible();
      await check(page, 'team', size);
      await page.screenshot({ path: path.join(SHOTS, `team-full-${tag(size)}.png`), fullPage: true });
      await page.locator('[data-test=open-code]').first().click();
      await expect(page.locator('.code-dialog, [data-test=code-dialog], [data-test=code-browser]').first()).toBeVisible();
      await page.waitForTimeout(500);
      await check(page, 'code-browser', size);
      const file = page.locator('[data-test="code-entry-src/refund.ts"]').first();
      await file.click();
      await page.waitForTimeout(500);
      await check(page, 'code-file', size);
      expect(errors).toEqual([]);
    });

    test('Kanban board: lane tabs, swipe, Needs-you cards, a card open', async ({ browser }) => {
      const { page, errors } = await open(browser, size);
      await nav(page, 'nav-board');
      await expect(page.locator('[data-test=lane-tabs]')).toBeVisible();
      await check(page, 'board-todo', size);
      for (const lane of ['in-progress', 'blocked', 'done']) {
        await page.locator(`[data-test=lane-tab-${lane}]`).click();
        await expect(page.locator(`[data-test=lane-tab-${lane}]`)).toHaveAttribute('aria-selected', 'true');
        await check(page, lane === 'blocked' ? 'board-needs-you' : `board-${lane}`, size);
        if (lane === 'blocked') await page.screenshot({ path: path.join(SHOTS, `board-needs-you-full-${tag(size)}.png`), fullPage: true });
      }
      // Swipe right twice from Done: Needs You, then In Progress (a real finger: touch pointer events on the lanes).
      const swipe = async (dx: number) => page.locator('[data-test=board-lanes]').evaluate((el, dx) => {
        const ev = (type: string, x: number) => new PointerEvent(type, { clientX: x, clientY: 300, pointerId: 5, isPrimary: true, pointerType: 'touch', bubbles: true });
        el.dispatchEvent(ev('pointerdown', 180)); el.dispatchEvent(ev('pointermove', 180 + dx / 2)); el.dispatchEvent(ev('pointerup', 180 + dx));
      }, dx);
      await swipe(120);
      await expect(page.locator('[data-test=lane-tab-blocked]')).toHaveAttribute('aria-selected', 'true');
      await swipe(-120);
      await expect(page.locator('[data-test=lane-tab-done]')).toHaveAttribute('aria-selected', 'true');
      await page.locator('[data-test=lane-tab-todo]').click();
      await page.locator('[data-test=board-card-1]').click();
      await expect(page.locator('[data-test=card-panel]')).toBeVisible();
      await check(page, 'board-card', size);
      await page.locator('[data-test=card-close]').click();
      await expect(page.locator('[data-test=card-panel]')).toHaveCount(0);
      expect(errors).toEqual([]);
    });

    test('Backlog: the list and a row menu', async ({ browser }) => {
      const { page, errors } = await open(browser, size, { asks: false });
      await nav(page, 'nav-backlog');
      await expect(page.locator('[data-test=backlog-row]').first()).toBeVisible();
      await check(page, 'backlog', size);
      const menu = page.locator('[data-test=backlog-row] [data-test=backlog-row-menu]').first();
      if (await menu.count()) {
        await menu.click();
        await check(page, 'backlog-row-menu', size);
        await page.keyboard.press('Escape');
      }
      await page.locator('[data-test=backlog-row]').first().click();
      await page.waitForTimeout(400);
      await check(page, 'backlog-item', size);
      expect(errors).toEqual([]);
    });

    test('Monitor: dark, compact controls, and the forces panel', async ({ browser }) => {
      const { page, errors } = await open(browser, size, { asks: false });
      await nav(page, 'nav-monitor');
      const mon = page.locator('[data-test=monitor]');
      await expect(mon).toBeVisible();
      await page.waitForTimeout(800);
      await check(page, 'monitor', size);
      expect.soft(await luminance(page, '[data-test=monitor]'), 'the Monitor is dark (light theme)').toBeLessThan(0.2);
      await page.locator('[data-test=monitor-layout-toggle]').click();
      const panel = page.locator('[data-test=monitor-layout]');
      await expect(panel).toBeVisible();
      await check(page, 'monitor-forces', size);
      const pb = (await panel.boundingBox())!;
      expect.soft(pb.x, 'forces panel left edge on screen').toBeGreaterThanOrEqual(0);
      expect.soft(pb.x + pb.width, 'forces panel right edge on screen').toBeLessThanOrEqual(size.w);
      expect.soft(pb.y + pb.height, 'forces panel bottom on screen').toBeLessThanOrEqual(size.h);
      expect.soft(await luminance(page, '[data-test=monitor-layout]'), 'the forces panel is dark').toBeLessThan(0.2);
      expect(errors).toEqual([]);
    });

    test('Logs (a dialog as a sheet): the list and an entry', async ({ browser }) => {
      const { page, errors } = await open(browser, size, { asks: false });
      await nav(page, 'nav-logs');
      const sheet = page.locator('.modal, [role=dialog]').first();
      await expect(sheet).toBeVisible();
      await page.waitForTimeout(600);
      const b = (await sheet.boundingBox())!;
      expect.soft(Math.round(b.width), 'the dialog is a full-width sheet').toBeGreaterThanOrEqual(size.w - 1);
      await check(page, 'logs', size);
      const row = page.locator('[data-test=logs-cards] [data-test=logs-row]').first();
      if (await row.count()) {
        await row.click();
        await page.waitForTimeout(300);
        await check(page, 'logs-entry', size);
      }
      expect(errors).toEqual([]);
    });

    test('Settings: the sections list and a section', async ({ browser }) => {
      const { page, errors } = await open(browser, size, { asks: false });
      await drawer(page);
      const settingsItem = page.locator('[data-test=sidebar] [data-test=open-app-settings]');
      if (!(await settingsItem.click({ timeout: 3000 }).then(() => true, () => false))) {
        expect.soft(false, `drawer @${tag(size)}: Settings cannot be tapped in the drawer (below the screen)`).toBe(true);
        await settingsItem.dispatchEvent('click');
      }
      await expect(page.locator('[data-test=settings]')).toBeVisible();
      await page.waitForTimeout(500);
      await check(page, 'settings', size);
      await page.locator('[data-test=settings] nav button, [data-test=settings] .sections button').first().click();
      await page.waitForTimeout(500);
      await check(page, 'settings-section', size);
      await page.screenshot({ path: path.join(SHOTS, `settings-section-full-${tag(size)}.png`), fullPage: true });
      expect(errors).toEqual([]);
    });

    test('Documents: the list and a document', async ({ browser }) => {
      const { page, errors } = await open(browser, size, { asks: false });
      await nav(page, 'nav-documents');
      await expect(page.locator('[data-test=documents-view]')).toBeVisible();
      await page.waitForTimeout(500);
      await check(page, 'documents', size);
      await page.locator('[data-test=documents-view]').getByText('payments', { exact: true }).first().click();
      await page.waitForTimeout(500);
      await check(page, 'documents-team', size);
      const doc = page.locator('[data-test="doc-B037-report.md"]').first();
      await doc.click();
      await page.waitForTimeout(500);
      await check(page, 'document', size);
      expect(errors).toEqual([]);
    });

    test('Visitor (read-only): the chip is visible and on screen', async ({ browser }) => {
      const { page } = await open(browser, size, { access: 'read', asks: false });
      const chip = page.locator('[data-test=remote-access-chip]');
      await expect(chip).toBeVisible();
      const r = (await chip.boundingBox())!;
      expect.soft(r.x).toBeGreaterThanOrEqual(0);
      expect.soft(r.x + r.width).toBeLessThanOrEqual(size.w);
      expect.soft(r.y + r.height).toBeLessThanOrEqual(size.h);
      await check(page, 'visitor', size);
      await nav(page, 'nav-board');
      await expect(chip).toBeVisible();
      await check(page, 'visitor-board', size);
    });

    // F7 re-verify (card 177): the Visitor's 150 px top-bar padding is kept in the shell's top bar only.
    test('Visitor (read-only) F7: the composer matches full access, the chip clears the top bar, Documents and Logs bars', async ({ browser }) => {
      const composerBar = (page: Page) => page.locator('[data-test=composer] .bar').evaluate((bar) => {
        const r = (sel: string) => { const e = bar.querySelector(sel); const b = e ? (e.closest('.pick') ?? e).getBoundingClientRect() : null; return b ? { x: b.x, w: b.width, h: b.height, right: b.right } : null; };
        const bb = bar.getBoundingClientRect();
        return { barRight: bb.right - parseFloat(getComputedStyle(bar).paddingRight), padR: getComputedStyle(bar).paddingRight, team: r('[data-test=composer-team]'), agent: r('[data-test=composer-agent]'), attach: r('[data-test=composer-attach]'), send: r('[data-test=composer-send]'), sw: document.documentElement.scrollWidth, iw: innerWidth };
      });
      const full = await open(browser, size, { asks: false });
      const f = await composerBar(full.page);
      await full.ctx.close();
      const { page, errors } = await open(browser, size, { access: 'read', asks: false });
      await expect(page.locator('[data-test=remote-access-chip]')).toBeVisible();
      const v = await composerBar(page);
      results.push({ screen: 'f7-composer', size: tag(size), audit: { full: f, visitor: v } as unknown as Audit });
      await page.screenshot({ path: path.join(SHOTS, `reverify2-visitor-${tag(size)}.png`) });
      expect.soft(Math.round(v.team!.w), 'Visitor team picker width = full access').toBe(Math.round(f.team!.w));
      expect.soft(Math.round(v.agent!.w), 'Visitor agent picker width = full access').toBe(Math.round(f.agent!.w));
      expect.soft(v.team!.w, 'team picker >= 44').toBeGreaterThanOrEqual(44);
      expect.soft(v.agent!.w, 'agent picker >= 44').toBeGreaterThanOrEqual(44);
      expect.soft(Math.round(v.attach!.w) + 'x' + Math.round(v.attach!.h), 'Attach 44x44').toBe('44x44');
      expect.soft(Math.abs(v.send!.right - v.barRight), `Send at the bar's right end (send ${v.send!.right}, bar ${v.barRight})`).toBeLessThanOrEqual(2);
      expect.soft(v.padR, "the Composer's bar has no 150 px padding").not.toBe(`150px`);
      expect.soft(v.sw, 'no sideways scroll').toBeLessThanOrEqual(v.iw);
      await check(page, 'reverify2-visitor-concierge', size);
      // The chip clears the top bar's title and menu button.
      const overlap = await page.evaluate(() => {
        const chip = document.querySelector('[data-test=remote-access-chip]')!.getBoundingClientRect();
        const hit = (sel: string) => { const e = document.querySelector(sel); if (!e) return 'missing'; const b = e.getBoundingClientRect();
          return !(b.right <= chip.left || chip.right <= b.left || b.bottom <= chip.top || chip.bottom <= b.top) ? `overlaps (${Math.round(b.left)}-${Math.round(b.right)} vs chip ${Math.round(chip.left)}-${Math.round(chip.right)})` : 'clear'; };
        const top = document.querySelector('.app.phone > .main > header.bar')!;
        return { title: hit('[data-test=phone-title]'), menu: hit('[data-test=drawer-toggle]'), topPad: getComputedStyle(top).paddingRight };
      });
      results.push({ screen: 'f7-chip', size: tag(size), audit: overlap as unknown as Audit });
      expect.soft(overlap.title, 'chip vs title').toBe('clear');
      expect.soft(overlap.menu, 'chip vs menu button').toBe('clear');
      expect.soft(parseFloat(overlap.topPad), "the shell's top bar keeps its 150 px room for the chip").toBeGreaterThanOrEqual(150);
      // Documents and Logs bars for a Visitor: no 150 px padding, controls inside the bar and the screen.
      const barInfo = (sel: string) => page.locator(sel).first().evaluate((bar) => {
        const bb = bar.getBoundingClientRect();
        const kids = [...bar.querySelectorAll<HTMLElement>('button, a, input, select')].filter((e) => e.offsetParent).map((e) => e.getBoundingClientRect());
        return { padR: getComputedStyle(bar).paddingRight, w: bb.width, sw: bar.scrollWidth, cw: bar.clientWidth, maxRight: Math.max(0, ...kids.map((k) => k.right)), barRight: bb.right, n: kids.length };
      });
      await nav(page, 'nav-documents');
      await expect(page.locator('[data-test=documents-view]')).toBeVisible();
      await page.waitForTimeout(500);
      await page.locator('[data-test=documents-view]').getByText('payments', { exact: true }).first().click();
      await page.waitForTimeout(500);
      const d = await barInfo('[data-test=docs-browser] > .bar');
      results.push({ screen: 'f7-documents-bar', size: tag(size), audit: d as unknown as Audit });
      expect.soft(parseFloat(d.padR), 'Documents bar padding-right under 150').toBeLessThan(150);
      expect.soft(d.sw, 'Documents bar does not overflow').toBeLessThanOrEqual(d.cw + 1);
      expect.soft(d.maxRight, 'Documents bar controls inside the bar').toBeLessThanOrEqual(d.barRight + 1);
      await check(page, 'reverify2-visitor-documents', size);
      await nav(page, 'nav-logs');
      await expect(page.locator('.modal, [role=dialog]').first()).toBeVisible();
      await page.waitForTimeout(600);
      const l = await barInfo('.modal .bar, [role=dialog] .bar');
      results.push({ screen: 'f7-logs-bar', size: tag(size), audit: l as unknown as Audit });
      expect.soft(parseFloat(l.padR), 'Logs bar padding-right under 150').toBeLessThan(150);
      expect.soft(l.sw, 'Logs bar does not overflow').toBeLessThanOrEqual(l.cw + 1);
      expect.soft(l.maxRight, 'Logs bar controls inside the bar').toBeLessThanOrEqual(l.barRight + 1);
      await check(page, 'reverify2-visitor-logs', size);
      // A dialog sheet's Close (top right) must not be drawn under the chip (the chip lets taps through, but hides the ✕).
      const close = await page.evaluate(() => {
        const c = document.querySelector('[data-test=modal-close]')!.getBoundingClientRect();
        const chip = document.querySelector('[data-test=remote-access-chip]')!.getBoundingClientRect();
        const ow = Math.max(0, Math.min(c.right, chip.right) - Math.max(c.left, chip.left)), oh = Math.max(0, Math.min(c.bottom, chip.bottom) - Math.max(c.top, chip.top));
        return { close: [Math.round(c.left), Math.round(c.top), Math.round(c.width), Math.round(c.height)], chip: [Math.round(chip.left), Math.round(chip.top), Math.round(chip.width), Math.round(chip.height)], coveredPct: Math.round((100 * ow * oh) / (c.width * c.height)) };
      });
      results.push({ screen: 'f8-logs-close', size: tag(size), audit: close as unknown as Audit });
      expect.soft(close.coveredPct, `Logs sheet's Close under the Visitor chip (close ${close.close}, chip ${close.chip})`).toBe(0);
      await page.locator('[data-test=modal-close]').click();
      await expect(page.locator('[data-test=modal-close]')).toHaveCount(0);
      expect(errors).toEqual([]);
    });

    // F8 re-verify (card 182): every phone sheet a Visitor can open keeps Close and its title clear of the chip; with
    // full access the sheet header is as before (6 px right padding, Close at the right edge).
    test('F8: every sheet - Close and title clear of the Visitor chip; full-access header unchanged', async ({ browser }) => {
      test.setTimeout(240_000);
      const sheetInfo = (page: Page) => page.evaluate(() => {
        const sheets = [...document.querySelectorAll<HTMLElement>('.modal.sheet')];
        const m = sheets[sheets.length - 1];
        if (!m) return null;
        const head = m.querySelector(':scope > header')!, h2 = head.querySelector('h2')!, c = head.querySelector('[data-test=modal-close]')!.getBoundingClientRect();
        const chipEl = document.querySelector('[data-test=remote-access-chip]');
        // The chip is position: fixed (offsetParent is always null): shown = a box and visible.
        const chipBox = chipEl?.getBoundingClientRect();
        const chip = chipBox && chipBox.width > 0 && chipEl!.checkVisibility() ? chipBox : null;
        const t = h2.getBoundingClientRect();
        const cover = (b: DOMRect) => { if (!chip) return 0; const ow = Math.max(0, Math.min(b.right, chip.right) - Math.max(b.left, chip.left)), oh = Math.max(0, Math.min(b.bottom, chip.bottom) - Math.max(b.top, chip.top)); return Math.round((100 * ow * oh) / (b.width * b.height)); };
        return { title: h2.textContent!.trim(), padR: getComputedStyle(head).paddingRight, close: [Math.round(c.left), Math.round(c.right), Math.round(c.width), Math.round(c.height)], chip: chip ? [Math.round(chip.left), Math.round(chip.right)] : null,
          closeCoveredPct: cover(c), titleCoveredPct: cover(t), titleW: Math.round(t.width), titleTruncated: h2.scrollWidth > h2.clientWidth + 1, sw: document.documentElement.scrollWidth, iw: innerWidth };
      });
      // Openers: Logs from the drawer, then each sheet the team page and its first agent card offer (when shown).
      const openers: { name: string; go: (page: Page) => Promise<boolean> }[] = [
        { name: 'logs', go: async (p) => { await nav(p, 'nav-logs'); return true; } },
        ...['open-code', 'open-settings', 'open-guardrail', 'open-logs', 'open-documents', 'open-workflows', 'open-triggers', 'open-git', 'open-manage'].map((id) => ({
          name: id.replace('open-', 'team-'),
          go: async (p: Page) => {
            await nav(p, 'nav-teams');
            await p.locator('[data-test=team-open-payments]').click();
            await expect(p.locator('[data-test=team-title]')).toBeVisible();
            const b = p.locator(`[data-test=${id}]`).first();
            if (!(await b.isVisible().catch(() => false))) return false;
            return b.click({ timeout: 3000 }).then(() => true, () => false);
          },
        })),
      ];
      const seen: Record<string, Record<string, unknown>> = {};
      for (const access of ['read', 'full'] as const) {
        const { page, errors, ctx } = await open(browser, size, { access, asks: false });
        for (const o of openers) {
          if (!(await o.go(page))) { (seen[o.name] ??= {})[access] = 'no opener'; continue; }
          await page.waitForTimeout(600);
          const info = await sheetInfo(page);
          (seen[o.name] ??= {})[access] = info ?? 'not a sheet';
          if (!info) continue;
          results.push({ screen: `f8-sheet-${o.name}-${access}`, size: tag(size), audit: info as unknown as Audit });
          await page.screenshot({ path: path.join(SHOTS, `reverify3-${access === 'read' ? 'visitor' : 'full'}-${o.name}-${tag(size)}.png`) });
          const where = `${o.name} sheet (${access}) @${tag(size)}`;
          expect.soft(info.sw, `${where}: sideways scroll`).toBeLessThanOrEqual(info.iw);
          expect.soft(info.close[2] + 'x' + info.close[3], `${where}: Close 44x44`).toBe('44x44');
          if (access === 'read') {
            expect.soft(info.chip, `${where}: chip shown`).not.toBeNull();
            expect.soft(info.closeCoveredPct, `${where}: Close under the chip (close ${info.close}, chip ${info.chip})`).toBe(0);
            expect.soft(info.titleCoveredPct, `${where}: title under the chip`).toBe(0);
            expect.soft(info.titleW, `${where}: title has room to be read`).toBeGreaterThanOrEqual(80);
          } else {
            expect.soft(info.padR, `${where}: full-access header padding-right as before`).toBe('6px');
            expect.soft(info.close[1], `${where}: Close at the right edge`).toBe(size.w - 6);
          }
          const close = page.locator('.modal.sheet [data-test=modal-close]').last();
          await close.click();
          await page.waitForTimeout(300);
          while (await page.locator('.modal.sheet').count()) { await page.locator('.modal.sheet [data-test=modal-close]').last().click(); await page.waitForTimeout(200); }
        }
        expect(errors).toEqual([]);
        await ctx.close();
      }
      console.log(`F8 sheets @${tag(size)}: ${JSON.stringify(seen)}`);
      results.push({ screen: 'f8-sheets', size: tag(size), audit: seen as unknown as Audit });
    });

    test('dark theme: the shell is dark, the Monitor is dark', async ({ browser }) => {
      const { page } = await open(browser, size, { theme: 'dark', asks: false });
      await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
      expect.soft(await luminance(page, 'body'), 'dark page').toBeLessThan(0.2);
      await check(page, 'dark-concierge', size);
      await nav(page, 'nav-monitor');
      await page.waitForTimeout(600);
      expect.soft(await luminance(page, '[data-test=monitor]')).toBeLessThan(0.2);
      await check(page, 'dark-monitor', size);
    });
  });
}

/** The relative luminance (0 black .. 1 white) of the first opaque background at or above `sel`. */
async function luminance(page: Page, sel: string): Promise<number> {
  return page.locator(sel).first().evaluate((start) => {
    for (let el: Element | null = start; el; el = el.parentElement) {
      const m = /rgba?\(([\d.]+),\s*([\d.]+),\s*([\d.]+)(?:,\s*([\d.]+))?/.exec(getComputedStyle(el).backgroundColor);
      if (!m || (m[4] !== undefined && Number(m[4]) < 0.5)) continue;
      const lin = (c: number) => { c /= 255; return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4; };
      return 0.2126 * lin(+m[1]) + 0.7152 * lin(+m[2]) + 0.0722 * lin(+m[3]);
    }
    return 1;
  });
}

test.describe('break tests: the 759 / 760 px edges and rotation', () => {
  for (const w of [759, 760]) {
    test(`${w} px wide: ${w < 760 ? 'the phone layout' : 'the desktop layout'}, no sideways scroll`, async ({ browser }) => {
      const size = { w, h: 900 };
      const { page } = await open(browser, size, { mobile: false, asks: false });
      const phone = w < 760;
      await expect(page.locator('.app')).toHaveClass(phone ? /phone/ : /^(?!.*\bphone\b).*/);
      await expect(page.locator('[data-test=drawer-toggle]')).toHaveCount(phone ? 1 : 0);
      if (phone) await expect(page.locator('[data-test=sidebar]')).toHaveAttribute('data-drawer', 'closed');
      else await expect(page.locator('[data-test=sidebar]')).not.toHaveAttribute('data-drawer', /./);
      for (const [id, name] of [['nav-board', 'board'], ['nav-monitor', 'monitor'], ['nav-teams', 'teams']] as const) {
        if (phone) await nav(page, id); else await page.locator(`[data-test=sidebar] [data-test=${id}]`).first().click();
        await page.waitForTimeout(400);
        await page.screenshot({ path: path.join(SHOTS, `edge-${name}-${w}.png`) });
        const a = await audit(page);
        results.push({ screen: `edge-${name}`, size: `${w}x900`, audit: a });
        expect.soft(a.overflow, `${name} @${w}: sideways scroll ${a.scrollWidth} > ${a.width}; ${a.wide.join(', ')}`).toBe(false);
        if (name === 'board') await expect(page.locator('[data-test=lane-tabs]')).toHaveCount(phone ? 1 : 0);
      }
    });
  }

  test('crossing 760 px both ways: the drawer closes, the layout follows, nothing scrolls sideways', async ({ browser }) => {
    const { page } = await open(browser, { w: 759, h: 900 }, { mobile: false, asks: false });
    await nav(page, 'nav-board');
    await drawer(page);
    await page.setViewportSize({ width: 760, height: 900 });
    await page.waitForTimeout(300);
    await expect(page.locator('.app')).not.toHaveClass(/\bphone\b/);
    await expect(page.locator('[data-test=drawer-scrim]')).toHaveCount(0);
    await expect(page.locator('[data-test=lane-tabs]')).toHaveCount(0);
    await page.setViewportSize({ width: 759, height: 900 });
    await page.waitForTimeout(300);
    await expect(page.locator('.app')).toHaveClass(/\bphone\b/);
    await expect(page.locator('[data-test=sidebar]')).toHaveAttribute('data-drawer', 'closed');
    await expect(page.locator('[data-test=lane-tabs]')).toHaveCount(1);
    expect((await audit(page)).overflow).toBe(false);
  });

  for (const size of PHONES) {
    test(`rotation ${tag(size)} -> landscape -> portrait on each screen`, async ({ browser }) => {
      const { page, errors } = await open(browser, size, { asks: false });
      for (const [id, name] of [['nav-board', 'board'], ['nav-monitor', 'monitor'], ['nav-backlog', 'backlog'], ['nav-documents', 'documents']] as const) {
        await nav(page, id);
        await page.waitForTimeout(300);
        // Landscape: 844 x 390 is wider than 760 (the desktop layout); 740 x 360 is not (still the phone layout).
        const landPhone = size.h < 760;
        await page.setViewportSize({ width: size.h, height: size.w });
        await page.waitForTimeout(400);
        await page.screenshot({ path: path.join(SHOTS, `rotate-${name}-${size.h}x${size.w}.png`) });
        const land = await audit(page);
        results.push({ screen: `rotate-${name}`, size: `${size.h}x${size.w}`, audit: land });
        expect.soft(land.overflow, `${name} landscape ${size.h}x${size.w}: sideways scroll ${land.scrollWidth}; ${land.wide.join(', ')}`).toBe(false);
        if (landPhone) await expect(page.locator('.app')).toHaveClass(/\bphone\b/);
        else await expect(page.locator('.app')).not.toHaveClass(/\bphone\b/);
        await page.setViewportSize({ width: size.w, height: size.h });
        await page.waitForTimeout(400);
        await expect(page.locator('.app')).toHaveClass(/\bphone\b/);
        const back = await audit(page);
        results.push({ screen: `rotate-back-${name}`, size: tag(size), audit: back });
        expect.soft(back.overflow, `${name} back to portrait: sideways scroll ${back.scrollWidth}; ${back.wide.join(', ')}`).toBe(false);
        await page.screenshot({ path: path.join(SHOTS, `rotate-back-${name}-${tag(size)}.png`) });
      }
      expect(errors).toEqual([]);
    });
  }
});

test.describe('1280 px: the desktop layout is unchanged from main b726212', () => {
  test.skip(process.env.B037_NO_BASE === '1', 'no base build');
  for (const [id, name] of [[null, 'concierge'], ['nav-teams', 'teams'], ['nav-board', 'board'], ['nav-backlog', 'backlog'], ['nav-monitor', 'monitor'], ['nav-documents', 'documents'], ['open-app-settings', 'settings'], ['nav-logs', 'logs']] as const) {
    test(`${name}: every [data-test] element in the same place and size`, async ({ browser }) => {
      const size = { w: 1280, h: 800 };
      const layouts: Record<string, Record<string, string>> = {};
      for (const [which, dir] of [['base', baseBuiltDir], ['branch', builtDir]] as const) {
        const { page } = await open(browser, size, { dir, mobile: false });
        if (id) await page.locator(`[data-test=sidebar] [data-test=${id}]`).first().click();
        await page.waitForTimeout(900);
        await page.screenshot({ path: path.join(SHOTS, `desktop-1280-${name}-${which}.png`) });
        layouts[which] = await page.evaluate(() => {
          const out: Record<string, string> = {};
          const seen = new Map<string, number>();
          for (const el of document.querySelectorAll('[data-test]')) {
            const k0 = el.getAttribute('data-test')!;
            const n = (seen.get(k0) ?? 0) + 1; seen.set(k0, n);
            const r = el.getBoundingClientRect();
            if (!r.width && !r.height) continue;
            out[`${k0}#${n}`] = `${Math.round(r.x)},${Math.round(r.y)} ${Math.round(r.width)}x${Math.round(r.height)}`;
          }
          return out;
        });
        await page.context().close();
      }
      // A data-test the branch adds (a hook main does not have) is not a layout change: listed, not compared.
      const diff: string[] = [];
      const added = Object.keys(layouts.branch!).filter((k) => !(k.replace(/#\d+$/, '#1') in layouts.base!));
      if (added.length) console.log(`${name} @1280: hooks new in B037 (not compared): ${added.join(', ')}`);
      for (const k of new Set([...Object.keys(layouts.base!), ...Object.keys(layouts.branch!)]))
        if (!added.includes(k) && layouts.base![k] !== layouts.branch![k]) diff.push(`${k}: main ${layouts.base![k] ?? '-'} / B037 ${layouts.branch![k] ?? '-'}`);
      expect.soft(diff, `${name} at 1280 px differs from main`).toEqual([]);
    });
  }
});
