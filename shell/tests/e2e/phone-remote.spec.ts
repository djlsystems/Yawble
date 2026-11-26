// Remote.html on a phone, end to end in a real browser (headless Chrome), against a FAKE core
// (tests/e2e/phone/fake-core.ts: canned answers over the page's socket, nothing real behind it).
//
// Every screen at 390 x 844 and 360 x 740: the page must not scroll sideways (scrollWidth > innerWidth), no visible
// control may sit off the screen's edge, and none may be smaller than 44 x 44 px. Plus the on-screen keyboard (a fake
// visualViewport), the Visitor chip, the dark Monitor and its forces panel, the 759 / 760 px edges and rotation.
//
//   npx playwright test phone-remote            (E2E_SKIP_BUILD=1 reuses out/e2e/renderer)
// The audit goes to test-results/phone-audit.ndjson (node tests/e2e/phone/summary.mjs prints it).
import { expect, test, type Browser, type Page } from './phone/fixtures';
import { appendFileSync, mkdirSync } from 'node:fs';
import path from 'node:path';
import { audit, type Audit } from './phone/audit';
import { resized, settled } from './settle';
import { buildRenderer, builtDir, fakeRemote } from './phone/server';

test.use({ channel: 'chrome', headless: true, actionTimeout: 8_000 });
test.setTimeout(60_000);

// One line per audit (each worker appends; Playwright empties test-results at the start of a run).
const RESULTS = path.resolve(__dirname, '..', '..', 'test-results', 'phone-audit.ndjson');
const results = { push(r: { screen: string; size: string; audit: Audit }) { mkdirSync(path.dirname(RESULTS), { recursive: true }); appendFileSync(RESULTS, JSON.stringify(r) + '\n'); } };

test.beforeAll(async () => {
  test.setTimeout(600_000);
  await buildRenderer();
});

type Size = { w: number; h: number };
const PHONES: Size[] = [{ w: 390, h: 844 }, { w: 360, h: 740 }];

interface OpenOpts { access?: 'full' | 'read'; theme?: 'light' | 'dark'; mobile?: boolean; asks?: boolean; sync?: boolean; needsYou?: boolean }
async function open(browser: Browser, size: Size, o: OpenOpts = {}) {
  const s = fakeRemote(builtDir);
  s.core.setAccess(o.access ?? 'full');
  s.core.setAsks(o.asks ?? true);
  s.core.setSync(o.sync ?? false);
  s.core.setNeedsYou(o.needsYou ?? true);
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
  await settled(page);
  return { page, s, ctx, errors };
}

const tag = (size: Size) => `${size.w}x${size.h}`;

/** The audit of one screen; soft-fails on overflow, an off-screen control (outside a sideways scroller) or a small control. */
async function check(page: Page, screen: string, size: Size, o: { allowSmall?: (c: Audit['small'][number]) => boolean; full?: boolean } = {}) {
  await settled(page);
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
      await expect(page.locator('[data-test=concierge-thread]')).toBeVisible();
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
        await expect(page.locator(sel).first()).toBeVisible();
        await settled(page);
        await banners(name);
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
      // The drawer scrolls as one list: no inner box holds the Teams and Chats lists, the first chat comes on screen by
      // scrolling the drawer, and the run-slots line at its bottom is on screen or reachable by scrolling.
      const room = await page.locator('[data-test=sidebar]').evaluate((side) => {
        const inner = [...side.querySelectorAll<HTMLElement>('*')].find((e) => /(auto|scroll)/.test(getComputedStyle(e).overflowY) && e.querySelector('[data-test=chat-item]'));
        return { inner: !!inner, sideScrolls: /(auto|scroll)/.test(getComputedStyle(side).overflowY), sideOver: side.scrollHeight - side.clientHeight };
      });
      expect.soft(room.inner, `drawer @${tag(size)}: the lists are not a scrolling box of their own`).toBe(false);
      const chat = page.locator('[data-test=sidebar] [data-test=chat-item]').first();
      await chat.scrollIntoViewIfNeeded();
      const cb = (await chat.boundingBox())!;
      expect.soft(cb.y >= 0 && cb.y + cb.height <= size.h + 1, `drawer @${tag(size)}: the first chat on screen after scrolling`).toBe(true);
      const slots = page.locator('[data-test=sidebar] [data-test=slots]');
      await slots.scrollIntoViewIfNeeded().catch(() => {});
      const sb = (await slots.boundingBox())!;
      expect.soft(sb.y + sb.height, `drawer @${tag(size)}: the run slots on screen (drawer overflows by ${room.sideOver} px, scrolls: ${room.sideScrolls})`).toBeLessThanOrEqual(size.h + 1);
      await openFirstChat(page, size);
      await expect(page.locator('[data-test=concierge-thread]')).toBeVisible();
      await check(page, 'concierge-chat', size);
      // The composer stays above a keyboard that covers the bottom 320 px.
      const box = page.locator('[data-test=composer-text]');
      await box.click();
      await page.evaluate(() => (window as any).__keyboard(320));
      await expect(page.locator('[data-test=concierge]')).toHaveAttribute('style', /--keyboard:\s*320px/);
      await settled(page);
      const r = (await page.locator('[data-test=composer]').boundingBox())!;
      const send = (await page.locator('[data-test=composer-send]').boundingBox())!;
      expect.soft(r.y + r.height, 'composer bottom above the keyboard').toBeLessThanOrEqual(size.h - 320 + 1);
      expect.soft(r.y, 'composer top on screen').toBeGreaterThanOrEqual(0);
      expect.soft(send.y + send.height, 'Send above the keyboard').toBeLessThanOrEqual(size.h - 320 + 1);
      await page.evaluate(() => (window as any).__keyboard(0));
      await expect(page.locator('[data-test=concierge]')).toHaveAttribute('style', /--keyboard:\s*0px/);
      await settled(page);
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
      // On a phone a member's Code is in its pane's ⋯ menu (member-more): open the first one that has a clone.
      const code = page.locator('[data-test=open-code]:visible').first();
      if (!(await code.isVisible().catch(() => false))) {
        const mores = page.locator('[data-test=member-more]');
        for (let i = 0, n = await mores.count(); i < n; i++) {
          await mores.nth(i).click();
          await expect(page.locator('[data-test=member-more-menu]')).toBeVisible();
          if (await page.locator('[data-test=member-more-menu] [data-test=open-code]').count()) break;
          await mores.nth(i).click(); // closes it again
        }
      }
      await page.locator('[data-test=open-code]:visible').first().click();
      await expect(page.locator('.code-dialog, [data-test=code-dialog], [data-test=code-browser]').first()).toBeVisible();
      await check(page, 'code-browser', size);
      const file = page.locator('[data-test="code-entry-src/refund.ts"]').first();
      await file.click();
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
      await expect(page.locator('[data-test=backlog-item]')).toBeVisible();
      await check(page, 'backlog-item', size);
      expect(errors).toEqual([]);
    });

    test('Monitor: dark, compact controls, and the forces panel', async ({ browser }) => {
      const { page, errors } = await open(browser, size, { asks: false });
      await nav(page, 'nav-monitor');
      const mon = page.locator('[data-test=monitor]');
      await expect(mon).toBeVisible();
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

    test('Logs (a page): the list and an entry', async ({ browser }) => {
      const { page, errors } = await open(browser, size, { asks: false });
      await nav(page, 'nav-logs');
      const sheet = page.locator('[data-test=logs]').first();
      await expect(sheet).toBeVisible();
      await expect(page.locator('.modal, [role=dialog]')).toHaveCount(0);
      await settled(page);
      const b = (await sheet.boundingBox())!;
      expect.soft(Math.round(b.width), 'the page takes the width (less its padding)').toBeGreaterThanOrEqual(size.w - 40);
      await check(page, 'logs', size);
      const row = page.locator('[data-test=logs-cards] [data-test=logs-row]').first();
      if (await row.count()) {
        await row.click();
        await check(page, 'logs-entry', size);
      }
      expect(errors).toEqual([]);
    });

    test('Settings: the sections list and a section', async ({ browser }) => {
      const { page, errors } = await open(browser, size, { asks: false });
      // Settings is the gear at the right of the phone top bar.
      await page.getByTestId('settings-gear').click();
      await expect(page.locator('[data-test=settings]')).toBeVisible();
      await check(page, 'settings', size);
      await page.locator('[data-test=settings] nav button, [data-test=settings] .sections button').first().click();
      await expect(page.locator('[data-test=settings-pane]')).toBeVisible();
      await check(page, 'settings-section', size);
      expect(errors).toEqual([]);
    });

    test('Documents: the list and a document', async ({ browser }) => {
      const { page, errors } = await open(browser, size, { asks: false });
      await nav(page, 'nav-documents');
      await expect(page.locator('[data-test=documents-view]')).toBeVisible();
      await check(page, 'documents', size);
      await page.locator('[data-test=documents-view]').getByText('payments', { exact: true }).first().click();
      await expect(page.locator('[data-test=docs-browser]')).toBeVisible();
      await check(page, 'documents-team', size);
      const doc = page.locator('[data-test="doc-phone-report.md"]').first();
      await doc.click();
      await expect(page.locator('[data-test=docs-viewer]')).toBeVisible();
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

    // F7 re-verify: the Visitor's 150 px top-bar padding is kept in the shell's top bar only.
    test('Visitor (read-only) F7: the composer matches full access, the chip clears the top bar, Documents and Logs bars', async ({ browser }) => {
      // On a phone the team and agent pickers fold out above the bar (one button opens them).
      const composerBar = async (page: Page) => { await page.locator('[data-test=composer-pickers-toggle]').click(); return page.locator('[data-test=composer] .bar').evaluate((bar) => {
        const r = (sel: string) => { const e = bar.closest('[data-test=composer]')!.querySelector(sel); const b = e ? (e.closest('.pick') ?? e).getBoundingClientRect() : null; return b ? { x: b.x, w: b.width, h: b.height, right: b.right } : null; };
        const bb = bar.getBoundingClientRect();
        return { barRight: bb.right - parseFloat(getComputedStyle(bar).paddingRight), padR: getComputedStyle(bar).paddingRight, team: r('[data-test=composer-team]'), agent: r('[data-test=composer-agent]'), attach: r('[data-test=composer-attach]'), send: r('[data-test=composer-send]'), sw: document.documentElement.scrollWidth, iw: innerWidth };
      }); };
      const full = await open(browser, size, { asks: false });
      const f = await composerBar(full.page);
      await full.ctx.close();
      const { page, errors } = await open(browser, size, { access: 'read', asks: false });
      await expect(page.locator('[data-test=remote-access-chip]')).toBeVisible();
      const v = await composerBar(page);
      results.push({ screen: 'f7-composer', size: tag(size), audit: { full: f, visitor: v } as unknown as Audit });
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
      await page.locator('[data-test=documents-view]').getByText('payments', { exact: true }).first().click();
      await expect(page.locator('[data-test=docs-browser] > .bar').first()).toBeVisible();
      await settled(page);
      const d = await barInfo('[data-test=docs-browser] > .bar');
      results.push({ screen: 'f7-documents-bar', size: tag(size), audit: d as unknown as Audit });
      expect.soft(parseFloat(d.padR), 'Documents bar padding-right under 150').toBeLessThan(150);
      expect.soft(d.sw, 'Documents bar does not overflow').toBeLessThanOrEqual(d.cw + 1);
      expect.soft(d.maxRight, 'Documents bar controls inside the bar').toBeLessThanOrEqual(d.barRight + 1);
      await check(page, 'reverify2-visitor-documents', size);
      await nav(page, 'nav-logs');
      await expect(page.locator('[data-test=logs] .bar').first()).toBeVisible();
      await settled(page);
      const l = await barInfo('[data-test=logs] .bar');
      results.push({ screen: 'f7-logs-bar', size: tag(size), audit: l as unknown as Audit });
      expect.soft(parseFloat(l.padR), 'Logs bar padding-right under 150').toBeLessThan(150);
      expect.soft(l.sw, 'Logs bar does not overflow').toBeLessThanOrEqual(l.cw + 1);
      expect.soft(l.maxRight, 'Logs bar controls inside the bar').toBeLessThanOrEqual(l.barRight + 1);
      await check(page, 'reverify2-visitor-logs', size);
      // Logs is a page (no sheet, so no Close for the Visitor chip to cover).
      expect(errors).toEqual([]);
    });

    // F8 re-verify: every phone sheet a Visitor can open keeps Close and its title clear of the chip; with
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
      // Openers: each sheet the team page and its first agent card offer (when shown). The app's Logs is a page.
      const openers: { name: string; go: (page: Page) => Promise<boolean> }[] = [
        ...['open-code', 'open-settings', 'open-logs', 'open-documents', 'open-deliverables', 'open-triggers', 'open-git', 'open-manage'].map((id) => ({
          name: id.replace('open-', 'team-'),
          go: async (p: Page) => {
            await nav(p, 'nav-teams');
            await p.locator('[data-test=team-open-payments]').click();
            await expect(p.locator('[data-test=team-title]')).toBeVisible();
            // On a phone the team's Documents and Deliverables are in the drawer, not the header.
            if (id === 'open-documents' || id === 'open-deliverables') { await nav(p, id.replace('open-', 'drawer-team-')); return true; }
            const b = p.locator(`[data-test=${id}]`).first();
            // On a phone Triggers, Git and Settings are in the team page's ⋯ menu: open it first.
            const more = p.locator('[data-test=team-more]');
            if (!(await b.isVisible().catch(() => false)) && (await more.isVisible().catch(() => false))) {
              await more.click();
              await expect(p.locator('[data-test=team-more-menu]')).toBeVisible();
            }
            // On a phone a member's Code, Settings and Logs are in its pane's ⋯ menu (member-more).
            const memberMore = p.locator('[data-test=member-more]').first();
            if (!(await b.isVisible().catch(() => false)) && (await memberMore.isVisible().catch(() => false))) {
              if (await more.isVisible().catch(() => false) && (await p.locator('[data-test=team-more-menu]').count())) await more.click();
              await memberMore.click();
              await expect(p.locator('[data-test=member-more-menu]')).toBeVisible();
            }
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
          // A sheet slides in (or the opener opened something else: then no sheet comes and the row says so).
          await page.locator('.modal.sheet').last().waitFor({ state: 'visible', timeout: 3000 }).catch(() => {});
          await settled(page);
          const info = await sheetInfo(page);
          (seen[o.name] ??= {})[access] = info ?? 'not a sheet';
          if (!info) continue;
          results.push({ screen: `f8-sheet-${o.name}-${access}`, size: tag(size), audit: info as unknown as Audit });
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
          const sheets = page.locator('.modal.sheet');
          const closeTop = async () => {
            const n = await sheets.count();
            await sheets.locator('[data-test=modal-close]').last().click();
            await expect.poll(() => sheets.count(), { message: 'Close takes the top sheet away' }).toBeLessThan(n);
          };
          await closeTop();
          while (await sheets.count()) await closeTop();
          await settled(page);
        }
        expect(errors).toEqual([]);
        await ctx.close();
      }
      console.log(`F8 sheets @${tag(size)}: ${JSON.stringify(seen)}`);
      results.push({ screen: 'f8-sheets', size: tag(size), audit: seen as unknown as Audit });
      // The three sheets behind ⋯ are still checked on a phone (an opener that is gone is a fault, not a skip).
      for (const name of ['team-triggers', 'team-git', 'team-manage'])
        for (const access of ['read', 'full'] as const)
          expect.soft(typeof seen[name]?.[access], `${name} (${access}) @${tag(size)}: opened as a sheet (${JSON.stringify(seen[name]?.[access])})`).toBe('object');
    });

    test('dark theme: the shell is dark, the Monitor is dark', async ({ browser }) => {
      const { page } = await open(browser, size, { theme: 'dark', asks: false });
      await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
      expect.soft(await luminance(page, 'body'), 'dark page').toBeLessThan(0.2);
      await check(page, 'dark-concierge', size);
      await nav(page, 'nav-monitor');
      await expect(page.locator('[data-test=monitor]')).toBeVisible();
      await settled(page);
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
        await settled(page);
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
    await resized(page, 760, 900);
    await expect(page.locator('.app')).not.toHaveClass(/\bphone\b/);
    await expect(page.locator('[data-test=drawer-scrim]')).toHaveCount(0);
    await expect(page.locator('[data-test=lane-tabs]')).toHaveCount(0);
    await resized(page, 759, 900);
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
        await settled(page);
        // Landscape: 844 x 390 is wider than 760 (the desktop layout); 740 x 360 is not (still the phone layout).
        const landPhone = size.h < 760;
        await resized(page, size.h, size.w);
        const land = await audit(page);
        results.push({ screen: `rotate-${name}`, size: `${size.h}x${size.w}`, audit: land });
        expect.soft(land.overflow, `${name} landscape ${size.h}x${size.w}: sideways scroll ${land.scrollWidth}; ${land.wide.join(', ')}`).toBe(false);
        if (landPhone) await expect(page.locator('.app')).toHaveClass(/\bphone\b/);
        else await expect(page.locator('.app')).not.toHaveClass(/\bphone\b/);
        await resized(page, size.w, size.h);
        await expect(page.locator('.app')).toHaveClass(/\bphone\b/);
        const back = await audit(page);
        results.push({ screen: `rotate-back-${name}`, size: tag(size), audit: back });
        expect.soft(back.overflow, `${name} back to portrait: sideways scroll ${back.scrollWidth}; ${back.wide.join(', ')}`).toBe(false);
      }
      expect(errors).toEqual([]);
    });
  }
});

// iPhone SE sizes, portrait 375 x 501 and landscape 667 x 263 (both the phone layout). Per page:
// where the first content row starts (px from the top of the frame, the browser window; ~120 px is the portrait target,
// on screen in landscape), no nested vertical scrollers, no sideways scroll, every field 16 px / 44 px, the Settings
// back control after scrolling Guardrails to its bottom, and a picked skill in view.
const SE: Size[] = [{ w: 375, h: 501 }, { w: 667, h: 263 }];
type PageMeasure = { first: { sel: string; top: number; bottom: number } | null; rows: Record<string, { top: number; bottom: number } | null>; topBar: number;
  scrollers: string[]; inputs: { what: string; font: number; h: number }[]; overflow: boolean; sw: number };
const MEASURE_RESULTS = path.resolve(__dirname, '..', '..', 'test-results', 'phone-page-measure.ndjson');

/** Measures the screen: the first visible match of `first` (in order), the named rows, the inner vertical scrollers, the fields. */
function measure(page: Page, first: string[], rows: Record<string, string> = {}): Promise<PageMeasure> {
  return page.evaluate(([first, rows]) => {
    const shown = (el: Element) => { if (el.closest('[inert], [aria-hidden=true]')) return false; const r = el.getBoundingClientRect(); return r.width > 0 && r.height > 0 && (el as HTMLElement).checkVisibility?.({ checkVisibilityCSS: true } as any) !== false && r.right > 0 && r.left < innerWidth; };
    const box = (sel: string) => { const el = [...document.querySelectorAll(sel)].find(shown); if (!el) return null; const r = el.getBoundingClientRect(); return { top: Math.round(r.top), bottom: Math.round(r.bottom) }; };
    let f: PageMeasure['first'] = null;
    for (const s of first as string[]) { const b = box(s); if (b) { f = { sel: s, ...b }; break; } }
    const named: PageMeasure['rows'] = {};
    for (const [k, s] of Object.entries(rows as Record<string, string>)) named[k] = box(s);
    const bar = document.querySelector('.app.phone > .main > header.bar, .app > .main > header.bar');
    const describe = (el: Element) => `${el.tagName.toLowerCase()}${el.getAttribute('data-test') ? `[data-test=${el.getAttribute('data-test')}]` : ''}${typeof (el as HTMLElement).className === 'string' && (el as HTMLElement).className ? '.' + (el as HTMLElement).className.trim().split(/\s+/).slice(0, 3).join('.') : ''}`;
    const scrollers: string[] = [];
    for (const el of document.querySelectorAll<HTMLElement>('body *')) {
      if (el.tagName === 'TEXTAREA' || el.closest('[data-test=sidebar]')) continue;
      const s = getComputedStyle(el);
      if (!/(auto|scroll)/.test(s.overflowY) || el.scrollHeight <= el.clientHeight + 1 || !shown(el)) continue;
      scrollers.push(`${describe(el)} ${el.clientHeight}/${el.scrollHeight}`);
    }
    const inputs = [...document.querySelectorAll<HTMLElement>('input:not([type=checkbox]):not([type=radio]):not([type=hidden]):not([type=range]):not([type=color]):not([type=file]), select, textarea')]
      .filter(shown).filter((el) => !el.closest('[data-test=sidebar]'))
      .map((el) => ({ what: describe(el), font: parseFloat(getComputedStyle(el).fontSize), h: Math.round(el.getBoundingClientRect().height * 10) / 10 }));
    const sw = Math.max(document.documentElement.scrollWidth, document.body.scrollWidth);
    return { first: f, rows: named, topBar: bar ? Math.round(bar.getBoundingClientRect().bottom) : -1, scrollers, inputs, overflow: sw > innerWidth, sw };
  }, [first, rows] as const);
}

for (const size of SE) {
  const portrait = size.h > size.w;
  test.describe(`iPhone SE ${tag(size)} (${portrait ? 'portrait' : 'landscape'})`, () => {
    {
      test('Backlog, Team, Documents and every Settings section', async ({ browser }) => {
        test.setTimeout(240_000);
        const rec = (screen: string, m: PageMeasure, extra: Record<string, unknown> = {}) => {
          mkdirSync(path.dirname(MEASURE_RESULTS), { recursive: true });
          appendFileSync(MEASURE_RESULTS, JSON.stringify({ screen, size: tag(size), ...m, ...extra }) + '\n');
        };
        const where = (screen: string) => `${screen} @${tag(size)}`;
        /** The checks for a page; the first content row against the target (portrait) or on screen (landscape). */
        const judge = (screen: string, m: PageMeasure, o: { scrollersOk?: number; at?: boolean } = {}) => {
          expect.soft(m.first, `${where(screen)}: a first content row`).not.toBeNull();
          // at: false - a fold or field the user opened pushes the list down: not judged for where the row starts.
          if (m.first && o.at !== false) {
            if (portrait) expect.soft(m.first.top, `${where(screen)}: first content row (${m.first.sel}) within ~120 px of the top`).toBeLessThanOrEqual(120);
            else expect.soft(m.first.top < size.h && m.first.bottom > 0, `${where(screen)}: first content row (${m.first.sel}) on screen in landscape (top ${m.first.top})`).toBe(true);
          }
          expect.soft(m.scrollers.length, `${where(screen)}: vertical scrollers (the page's one only): ${m.scrollers.join(' | ')}`).toBeLessThanOrEqual(o.scrollersOk ?? 1);
          expect.soft(m.overflow, `${where(screen)}: sideways scroll (${m.sw} > ${size.w})`).toBe(false);
          for (const i of m.inputs) {
            expect.soft(i.font, `${where(screen)}: ${i.what} font-size`).toBeGreaterThanOrEqual(16);
            expect.soft(i.h, `${where(screen)}: ${i.what} height`).toBeGreaterThanOrEqual(44);
          }
        };
        // Each page opened below is closed for the next: its script errors are kept for the check at the end.
        const seen: string[] = [];
        let { page, ctx, errors } = await open(browser, size, { asks: false });

        // Backlog: the list, then the search-and-sort fold open (its fields).
        await nav(page, 'nav-backlog');
        await expect(page.locator('[data-test=backlog-row]').first()).toBeVisible();
        await settled(page);
        let m = await measure(page, ['[data-test=backlog-row]'], { head: '[data-test=backlog-phone-head], .backlog-place .head' });
        rec('backlog', m); judge('backlog', m);
        const tools = page.locator('[data-test=backlog-tools-toggle]');
        if (await tools.count()) { await tools.click(); await settled(page); }
        m = await measure(page, ['[data-test=backlog-row]'], { search: '[data-test=backlog-search]' });
        rec('backlog-tools', m); judge('backlog-tools', m, { at: false });
        expect.soft(m.inputs.some((i) => /backlog-search/.test(i.what)), `${where('backlog-tools')}: the search field shown`).toBe(true);

        // Team, without and with the sync bar: the Tell row is the first content row; the first member card measured too.
        // Three states: an integration ask and a Manager decision pending (above the members), the same with the sync bar,
        // and nothing pending (the members right under Tell).
        for (const [screen, sync, needsYou] of [['team', false, true], ['team-sync', true, true], ['team-quiet', false, false], ['team-quiet-sync', true, false]] as const) {
          if (screen !== 'team') { seen.push(...errors); await ctx.close(); ({ page, ctx, errors } = await open(browser, size, { asks: false, sync, needsYou })); }
          await nav(page, 'nav-teams');
          await page.locator('[data-test=team-open-payments]').click();
          await expect(page.locator('[data-test=team-title]')).toBeVisible();
          if (sync) await expect(page.locator('[data-test=team-sync]')).toBeVisible();
          await expect(page.locator('[data-test=agent-Manager]')).toBeAttached();
          await settled(page);
          m = await measure(page, ['.tell.row'], { title: '[data-test=team-title]', sync: '[data-test=team-sync]', tell: '.tell.row', member: '[data-test=agent-Manager]' });
          rec(screen, m); judge(screen, m);
          expect.soft(m.rows.tell && m.rows.tell.bottom - m.rows.tell.top, `${where(screen)}: the Tell row is one 44 px row`).toBeLessThanOrEqual(56);
          // Asks and decisions pending go above the members on purpose (Needs you first): there the members are recorded only.
          if (!portrait && !needsYou) expect.soft(m.rows.member && m.rows.member.top < size.h, `${where(screen)}: a member card on screen in landscape (top ${m.rows.member?.top})`).toBe(true);
          // Scrolled until the first member card shows: is it reachable (the page scrolls)?
          await page.locator('[data-test=agent-Manager]').scrollIntoViewIfNeeded();
          const mb = await page.locator('[data-test=agent-Manager]').boundingBox();
          rec(`${screen}-member-scrolled`, m, { memberAfterScroll: mb && { top: Math.round(mb.y), bottom: Math.round(mb.y + mb.height) } });
        }
        seen.push(...errors);
        await ctx.close();
        ({ page, ctx, errors } = await open(browser, size, { asks: false }));

        // Documents: the root (team folders), a team's folder, and its New file field.
        await nav(page, 'nav-documents');
        await expect(page.locator('[data-test=documents-view]')).toBeVisible();
        await settled(page);
        m = await measure(page, ['[data-test^=documents-team-]'], { title: '[data-test=documents-view] h2' });
        rec('documents-root', m); judge('documents-root', m);
        await page.locator('[data-test=documents-team-payments]').click();
        await expect(page.locator('[data-test=docs-browser]')).toBeVisible();
        await settled(page);
        const firstDoc = ['[data-test=docs-list] [data-test^="doc-"]:not([data-test^="doc-delete"])', '[data-test=docs-list] li'];
        m = await measure(page, firstDoc, { bar: '[data-test=docs-browser] > .bar' });
        rec('documents-folder', m); judge('documents-folder', m);
        const add = page.locator('[data-test=docs-add]');
        if (await add.isVisible().catch(() => false)) await add.click();
        await page.locator('[data-test=docs-new-file]').first().click();
        await expect(page.locator('[data-test=docs-name]')).toBeVisible();
        m = await measure(page, firstDoc, { name: '[data-test=docs-name]' });
        rec('documents-new-file', m); judge('documents-new-file', m, { at: false });
        expect.soft(m.inputs.some((i) => /docs-name/.test(i.what)), `${where('documents-new-file')}: the name field shown`).toBe(true);

        // Settings: the list, then each section; Guardrails scrolled to its bottom; Skills with a skill picked.
        // Settings opens from the gear at the top right.
        const openSettings = async () => {
          await page.getByTestId('settings-gear').click();
          await expect(page.locator('[data-test=settings]')).toBeVisible();
          await settled(page);
        };
        await openSettings();
        m = await measure(page, ['[data-test=settings] [role=tab]']);
        rec('settings', m); judge('settings', m);
        for (const id of ['open-general', 'open-agents', 'open-skills', 'open-global-policy', 'open-integrations', 'open-system']) {
          const tab = page.locator(`[data-test=settings] [data-test=${id}]`);
          if (!(await tab.isVisible().catch(() => false))) {
            const back = page.locator('[data-test=settings-back]');
            if (await back.isVisible().catch(() => false)) await back.click(); else await openSettings();
            await expect(tab).toBeVisible();
            await settled(page);
          }
          await tab.click();
          await settled(page);
          const screen = `settings-${id.replace('open-', '')}`;
          // Where the section opened (the page scroller keeps the last section's scroll, e.g. Guardrails at its bottom),
          // recorded; then measured from its top.
          const openedAt = await page.locator('[data-test=settings]').evaluate((s) => { const t = Math.round(s.scrollTop); s.scrollTop = 0; return t; });
          if (openedAt) rec(`${screen}-opened-scrolled`, await measure(page, []), { openedAt });
          await settled(page);
          m = await measure(page, ['[data-test=settings-pane] > section > *:not(.section-head)', '[data-test=settings-pane] > * > *:not(.section-head)', '[data-test=settings-pane] > *'],
            { head: '[data-test=settings] .section-head', back: '[data-test=settings-back]' });
          rec(screen, m); judge(screen, m);
          if (id === 'open-global-policy') {
            // Scroll the page's scroller to the bottom: "‹ Settings" still on screen and tappable.
            const r = await page.locator('[data-test=settings-pane]').evaluate((pane) => {
              let s: HTMLElement | null = pane as HTMLElement;
              while (s && !(/(auto|scroll)/.test(getComputedStyle(s).overflowY) && s.scrollHeight > s.clientHeight + 1)) s = s.parentElement;
              const sc = s ?? document.scrollingElement as HTMLElement;
              sc.scrollTop = sc.scrollHeight;
              return { scroller: sc.tagName + '.' + sc.className, scrollTop: Math.round(sc.scrollTop), max: sc.scrollHeight - sc.clientHeight };
            });
            await settled(page);
            const back = await page.evaluate(() => {
              const b = document.querySelector('[data-test=settings-back]');
              if (!b) return null;
              const r = b.getBoundingClientRect();
              const hit = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
              return { top: Math.round(r.top), bottom: Math.round(r.bottom), w: Math.round(r.width), h: Math.round(r.height), tappable: !!hit && (b === hit || b.contains(hit)) };
            });
            rec('settings-guardrails-bottom', await measure(page, []), { scroll: r, back });
            {
              expect.soft(r.max, `${where('guardrails')}: Guardrails is longer than the screen (to test the scroll)`).toBeGreaterThan(0);
              expect.soft(back && back.top >= 0 && back.bottom <= size.h && back.tappable, `${where('guardrails')}: ‹ Settings on screen and tappable at the bottom (${JSON.stringify(back)}, ${JSON.stringify(r)})`).toBe(true);
            }
          }
          if (id === 'open-skills') {
            const skill = page.locator('[data-test=skill-skill-10]');
            await skill.scrollIntoViewIfNeeded();
            await skill.click();
            await expect(page.locator('[data-test=skills-pane]')).toBeVisible();
            await settled(page);
            const pane = await page.locator('[data-test=skills-pane]').evaluate((p) => {
              const r = p.getBoundingClientRect(); const body = p.querySelector('.body')?.getBoundingClientRect();
              return { top: Math.round(r.top), bottom: Math.round(r.bottom), title: p.textContent!.includes('skill-10'), bodyTop: body ? Math.round(body.top) : null };
            });
            const sm = await measure(page, ['[data-test=skills-pane]']);
            rec('settings-skill-picked', sm, { pane });
            judge('settings-skill-picked', sm);
            expect.soft(pane.top >= 0 && pane.top < size.h - 40 && pane.title, `${where('skill picked')}: the skill's detail in view (${JSON.stringify(pane)})`).toBe(true);
            const sb = page.locator('[data-test=skills-back]');
            if (await sb.isVisible().catch(() => false)) await sb.click();
          }
        }
        expect([...seen, ...errors]).toEqual([]);
        await ctx.close();
      });
    }
  });
}