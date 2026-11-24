// The team page's members as a grid of equal tiles, in headless Chrome against
// the phone-remote fake core (remote.html, tests/e2e/phone). At 1280 px: two tiles per row by default with New member
// last; the w / h handles resize every tile together and the size is kept in yawble.memberTiles across a reload (apart
// from the Teams list's size); each MemberPane fills its tile, its view scrolling inside; at the narrowest tile the title
// chips ellipsize, the tabs stay on one line and the ☰ menu is not clipped (light and dark); the New member form
// (validation, Adding…, a refused add keeps the form, Cancel). A Visitor's New member is disabled. A phone (375 px): one
// full-width tile per row, no handles, New member last.
//
//   npx playwright test team-grid      (E2E_SKIP_BUILD=1 reuses out/e2e/renderer)
// Screenshots go to $YAWBLE_TEAM_DOCS/team-grid (else test-results/team-grid).
import { expect, test, type Browser, type Page } from './phone/fixtures';
import { existsSync, mkdirSync } from 'node:fs';
import path from 'node:path';
import { createFakeCore, type Access } from './phone/fake-core';
import { ORIGIN, buildRenderer, builtDir } from './phone/server';

test.use({ channel: 'chrome', headless: true, actionTimeout: 8_000 });
test.setTimeout(90_000);

const SHOTS = path.join(process.env.YAWBLE_TEAM_DOCS || path.resolve(__dirname, '..', '..', 'test-results'), 'team-grid');

test.beforeAll(async () => { test.setTimeout(600_000); mkdirSync(SHOTS, { recursive: true }); await buildRenderer(); });

type Opts = { w?: number; h?: number; theme?: 'light' | 'dark'; access?: Access; tiles?: { w: number; h: number } };

async function open(browser: Browser, o: Opts = {}) {
  const { w = 1280, h = 800, theme = 'light', access = 'full' } = o;
  const core = createFakeCore();
  core.setAsks(false);
  core.setNeedsYou(false);
  core.setAccess(access);
  const phone = w < 760;
  let holdAdd = false;
  let refuseAdd = false;
  let held: (() => void) | null = null;
  const adds: any[] = [];
  const ctx = await browser.newContext({ viewport: { width: w, height: h }, isMobile: phone, hasTouch: phone, colorScheme: theme });
  await ctx.addInitScript(([t, tiles]) => {
    try {
      localStorage.setItem('yawble.remoteTheme', t as string);
      if (tiles && !sessionStorage.getItem('team-grid-seeded')) { localStorage.setItem('yawble.memberTiles', JSON.stringify(tiles)); sessionStorage.setItem('team-grid-seeded', '1'); }
    } catch { /* none */ }
  }, [theme, o.tiles ?? null] as const);
  await ctx.route(`${ORIGIN}/**`, async (route) => {
    const rel = decodeURIComponent(new URL(route.request().url()).pathname).replace(/^\/+/, '');
    const file = path.resolve(builtDir, rel || 'remote.html');
    if (!file.startsWith(builtDir) || !existsSync(file)) return route.fulfill({ status: 404, body: 'not found' });
    return route.fulfill({ path: file });
  });
  await ctx.routeWebSocket(/\/rpc$/, (ws) => {
    ws.onMessage((m) => {
      const msg = JSON.parse(String(m));
      if (msg.method === 'members.add' && access === 'full') {
        adds.push(msg.params);
        const out = refuseAdd
          ? JSON.stringify({ id: msg.id, error: { code: 'bad_name', message: 'A member called that is already on the team' } })
          : JSON.stringify({ id: msg.id, result: { id: 99 } });
        if (holdAdd) held = () => ws.send(out); else ws.send(out);
        return;
      }
      const out = core.answer(String(m));
      if (out) ws.send(out);
    });
  });
  const page = await ctx.newPage();
  const errors: string[] = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.goto(`${ORIGIN}/remote.html`);
  await expect(page.locator('[data-test=sidebar]')).toBeAttached({ timeout: 30_000 });
  await page.waitForTimeout(400);
  await goTeam(page, phone);
  return {
    page, ctx, errors, core, adds,
    hold: (on: boolean) => { holdAdd = on; }, refuse: (on: boolean) => { refuseAdd = on; }, release: () => { held?.(); held = null; },
  };
}

async function goTeam(page: Page, phone: boolean) {
  if (phone) await page.locator('[data-test=drawer-toggle]').first().click();
  await page.locator('[data-test=sidebar] [data-test=nav-teams]').first().click();
  await page.locator('[data-test=team-open-payments]').click();
  await expect(page.locator('[data-test=member-grid]')).toBeVisible();
  await expect(page.locator('[data-test=member-cell]')).toHaveCount(3);
  await page.waitForTimeout(500);
}

/** Every cell of the grid (members and New member), in order, with its rect. */
const cells = (page: Page) => page.evaluate(() => [...document.querySelectorAll('[data-test=member-grid] > .member-cell')].map((c) => {
  const r = c.getBoundingClientRect();
  return { x: Math.round(r.x), y: Math.round(r.y), w: Math.round(r.width), h: Math.round(r.height), isNew: c.classList.contains('new-cell'),
    handles: c.querySelectorAll('[data-test^=member-tile-handle]').length };
}));
const rows = (cs: { y: number }[]) => new Set(cs.map((c) => c.y)).size;
const stored = (page: Page) => page.evaluate(() => localStorage.getItem('yawble.memberTiles'));

async function drag(page: Page, sel: string, dx: number, dy: number) {
  const g = page.locator(sel).first();
  await g.scrollIntoViewIfNeeded();
  const b = (await g.boundingBox())!;
  const x = b.x + b.width / 2, y = b.y + b.height / 2;
  await page.mouse.move(x, y);
  await page.mouse.down();
  await page.mouse.move(x + dx / 2, y + dy / 2, { steps: 4 });
  await page.mouse.move(x + dx, y + dy, { steps: 4 });
  await page.mouse.up();
  await page.waitForTimeout(200);
}

test('1280: two equal tiles per row by default, New member last, handles on every tile', async ({ browser }) => {
  const { page, ctx, errors } = await open(browser);
  const cs = await cells(page);
  await page.screenshot({ path: path.join(SHOTS, 'desktop-1280-default.png'), fullPage: true });
  expect(cs.length, 'three members + New member').toBe(4);
  expect(cs[cs.length - 1].isNew, 'New member is the last tile').toBe(true);
  expect(cs.filter((c) => c.isNew).length).toBe(1);
  expect(cs[0].y, 'tiles 1, 2 on row 1').toBe(cs[1].y);
  expect(cs[2].y, 'tiles 3, 4 on row 2').toBe(cs[3].y);
  expect(cs[2].y).toBeGreaterThan(cs[0].y);
  for (const c of cs) { expect(c.w, 'default width 460').toBe(460); expect(c.h, 'default height 340').toBe(340); }
  for (const c of cs.filter((x) => !x.isNew)) expect(c.handles, 'w and h handles on a member tile').toBe(2);
  await expect(page.locator('.add.row'), 'old add row gone').toHaveCount(0);
  expect(await stored(page), 'nothing stored until a drag').toBeNull();
  expect(errors).toEqual([]);
  await ctx.close();
});

test('1280: w and h handles resize every tile together; kept in yawble.memberTiles across a reload, apart from the Teams list', async ({ browser }) => {
  const { page, ctx, errors } = await open(browser);
  const teamsBefore = await page.evaluate(() => localStorage.getItem('yawble.teamTiles'));
  await drag(page, '[data-test=member-tile-handle-w]', -120, 0);
  let cs = await cells(page);
  for (const c of cs) expect(c.w, 'every tile narrower').toBe(340);
  expect(rows(cs), 'still two rows (2 x 340 fits, 3 x 340 + gaps = 1048 does not)').toBe(2);
  await drag(page, '[data-test=member-tile-handle-h]', 0, 100);
  cs = await cells(page);
  for (const c of cs) expect(c.h, 'every tile taller').toBe(440);
  expect(JSON.parse((await stored(page))!)).toEqual({ w: 340, h: 440 });
  await drag(page, '[data-test=member-tile-handle-w]', -60, 0);
  cs = await cells(page);
  for (const c of cs) expect(c.w).toBe(280);
  expect(rows(cs), '3 x 280 + 28 = 868 fits in 964: three per row').toBe(2);
  expect(cs[0].y).toBe(cs[2].y);
  await page.screenshot({ path: path.join(SHOTS, 'desktop-1280-280x440.png'), fullPage: true });
  // The Teams list's size is its own.
  expect(await page.evaluate(() => localStorage.getItem('yawble.teamTiles'))).toBe(teamsBefore);
  await page.reload();
  await expect(page.locator('[data-test=sidebar]')).toBeAttached({ timeout: 30_000 });
  await goTeam(page, false);
  cs = await cells(page);
  for (const c of cs) { expect(c.w, 'width kept after reload').toBe(280); expect(c.h, 'height kept after reload').toBe(440); }
  // The Teams list's tiles did not take the member size.
  await page.locator('[data-test=sidebar] [data-test=nav-teams]').first().click();
  await page.waitForTimeout(400);
  const teamTileW = await page.evaluate(() => Math.round((document.querySelector('[data-test^=team-tile], .team-grid > *') as HTMLElement | null)?.getBoundingClientRect().width ?? -1));
  expect(teamTileW, 'a Teams list tile is not 280 wide').not.toBe(280);
  // Clamped: a huge drag stops at the grid's width, never beyond.
  await goTeam(page, false);
  await drag(page, '[data-test=member-tile-handle-w]', 2000, 0);
  cs = await cells(page);
  const gridW = await page.evaluate(() => Math.round(document.querySelector('[data-test=member-grid]')!.clientWidth));
  for (const c of cs) expect(c.w).toBeLessThanOrEqual(gridW);
  expect(errors).toEqual([]);
  await ctx.close();
});

for (const theme of ['light', 'dark'] as const) {
  test(`1280 ${theme}: the pane fills its tile, the view scrolls inside, chips ellipsize, tabs one line, ☰ menu not clipped`, async ({ browser }) => {
    // The default, the narrowest at the default height, and the smallest tile (min w and min h).
    for (const tiles of [{ w: 460, h: 340 }, { w: 280, h: 340 }, { w: 280, h: 200 }]) {
      const { page, ctx, errors } = await open(browser, { theme, tiles });
      await page.screenshot({ path: path.join(SHOTS, `desktop-1280-${tiles.w}x${tiles.h}-${theme}.png`), fullPage: true });
      const m = await page.evaluate(() => [...document.querySelectorAll('[data-test=member-cell]')].map((c) => {
        const cell = c.getBoundingClientRect();
        const pane = c.querySelector('.pane') as HTMLElement;
        const pr = pane.getBoundingClientRect();
        const title = c.querySelector('.title') as HTMLElement;
        const tabs = c.querySelector('[data-test=member-tabs]') as HTMLElement | null;
        const body = c.querySelector('[data-test=member-pane-body]') as HTMLElement;
        const br = body.getBoundingClientRect();
        // Everything the title holds stays inside the pane horizontally.
        const out = [...title.querySelectorAll('*')].filter((e) => { const r = e.getBoundingClientRect(); return r.width > 0 && (r.right > pr.right + 1 || r.left < pr.left - 1); })
          .map((e) => (e as HTMLElement).className || e.tagName);
        const chips = [...title.querySelectorAll('.chip, strong, .label, .more')].map((e) => { const s = getComputedStyle(e); return { cut: (e as HTMLElement).scrollWidth > (e as HTMLElement).clientWidth, te: s.textOverflow, ws: s.whiteSpace }; });
        // The scroller is the body or its view inside it (output).
        const scrollers = [body, ...body.querySelectorAll('*')].filter((e) => { const s = getComputedStyle(e); return /auto|scroll/.test(s.overflowY) && (e as HTMLElement).scrollHeight > (e as HTMLElement).clientHeight + 1; });
        const tabsR = tabs?.getBoundingClientRect();
        const tabKids = tabs ? [...tabs.children].map((k) => Math.round(k.getBoundingClientRect().top)).filter((_t, i) => (tabs.children[i] as HTMLElement).offsetWidth > 0) : [];
        return {
          name: c.querySelector('[data-test^=agent-]')?.getAttribute('data-test'),
          cell: { w: Math.round(cell.width), h: Math.round(cell.height) }, pane: { w: Math.round(pr.width), h: Math.round(pr.height) },
          bodyInside: br.bottom <= pr.bottom + 1 && br.top >= pr.top, bodyH: Math.round(br.height), titleOut: out, chips,
          titleH: Math.round(title.getBoundingClientRect().height), tabsH: tabsR ? Math.round(tabsR.height) : null, tabTops: new Set(tabKids).size,
          scrolls: scrollers.length, contentTaller: body.scrollHeight > body.clientHeight + 1 || scrollers.length > 0,
          paneOverflow: pane.scrollWidth > pane.clientWidth + 1,
        };
      }));
      for (const p of m) {
        const where = `${theme} ${tiles.w}x${tiles.h} ${p.name}`;
        expect.soft(p.pane.w, `${where}: pane as wide as its cell`).toBe(p.cell.w);
        expect.soft(p.pane.h, `${where}: pane as tall as its cell`).toBe(p.cell.h);
        expect.soft(p.bodyInside, `${where}: the view inside the pane`).toBe(true);
        expect.soft(p.bodyH, `${where}: the view takes the rest (${JSON.stringify(p)})`).toBeGreaterThan(p.cell.h - 100);
        expect.soft(p.titleOut, `${where}: nothing in the title bar sticks out`).toEqual([]);
        expect.soft(p.titleH, `${where}: title one 30 px row`).toBeLessThanOrEqual(31);
        expect.soft(p.tabsH, `${where}: tabs on one line (one row, <= 30 px)`).toBeLessThanOrEqual(30);
        expect.soft(p.paneOverflow, `${where}: nothing wider than the pane`).toBe(false);
        for (const c of p.chips) if (c.cut) expect.soft(c.te, `${where}: a cut chip ends in …`).toBe('ellipsis');
        if (p.contentTaller) expect.soft(p.scrolls, `${where}: taller content scrolls inside`).toBeGreaterThan(0);
      }
      if (tiles.w === 280) {
        // At the narrowest tile at least one title chip is actually cut (with …) - the case the ellipsis is for.
        expect.soft(m.some((p) => p.chips.some((c) => c.cut && c.te === 'ellipsis')), `${theme}: a chip ellipsized at 280 (${JSON.stringify(m.map((p) => p.chips))})`).toBe(true);
      }
      {
        // The ☰ menu of the first tile and of the third (the right end of the row at 280) opens fully visible.
        for (const i of [0, 2]) {
          const cell = page.locator('[data-test=member-cell]').nth(i);
          await cell.locator('[data-test=member-more]').click();
          const menu = cell.locator('[data-test=member-more-menu]');
          await expect(menu).toBeVisible();
          await page.waitForTimeout(150);
          await page.screenshot({ path: path.join(SHOTS, `desktop-1280-${tiles.w}x${tiles.h}-menu${i}-${theme}.png`) });
          const vis = await menu.evaluate((el) => {
            const r = el.getBoundingClientRect();
            const pts = [[r.left + 4, r.top + 4], [r.right - 4, r.top + 4], [r.left + 4, r.bottom - 4], [r.right - 4, r.bottom - 4], [(r.left + r.right) / 2, (r.top + r.bottom) / 2]];
            const hit = pts.map(([x, y]) => { const e = document.elementFromPoint(x, y); return !!e && el.contains(e); });
            return { r: { l: Math.round(r.left), t: Math.round(r.top), r: Math.round(r.right), b: Math.round(r.bottom) }, hit, vw: innerWidth, vh: innerHeight,
              items: [...el.querySelectorAll('button')].map((b) => { const q = b.getBoundingClientRect(); const e = document.elementFromPoint(q.left + q.width / 2, q.top + q.height / 2); return !!e && b.contains(e); }) };
          });
          expect.soft(vis.hit, `${theme} ${tiles.w}x${tiles.h} tile ${i}: the ☰ menu's corners are on top, not clipped (${JSON.stringify(vis)})`).toEqual(vis.hit.map(() => true));
          expect.soft(vis.items.every(Boolean), `${theme} ${tiles.w}x${tiles.h} tile ${i}: every menu item can be clicked (${JSON.stringify(vis)})`).toBe(true);
          await page.keyboard.press('Escape');
          if (await menu.isVisible()) await cell.locator('[data-test=member-more]').click();
        }
      }
      expect(errors).toEqual([]);
      await ctx.close();
    }
  });
}

test('1280: New member opens the form; validation, Adding…, a refused add keeps the form, Cancel and Escape, an add closes it', async ({ browser }) => {
  const { page, ctx, errors, adds, hold, release, refuse } = await open(browser);
  const tile = page.locator('[data-test=member-card-new-tile]');
  await page.locator('[data-test=member-card-new]').click();
  const form = page.locator('[data-test=add-agent-form]');
  await expect(form).toBeVisible();
  await expect(page.locator('[data-test=add-agent-name]')).toBeFocused();
  const add = page.locator('[data-test=add-agent]');
  await expect(add, 'empty name: Add disabled').toBeDisabled();
  await page.locator('[data-test=add-agent-name]').fill('   ');
  await expect(add, 'blank name: Add disabled').toBeDisabled();
  // The form fits the default tile: Add and Cancel visible inside it.
  for (const b of ['add-agent', 'add-agent-cancel']) {
    const inside = await page.locator(`[data-test=${b}]`).evaluate((el) => {
      const t = el.closest('.new-tile')!.getBoundingClientRect(); const r = el.getBoundingClientRect();
      return r.top >= t.top && r.bottom <= t.bottom && r.left >= t.left && r.right <= t.right;
    });
    expect.soft(inside, `${b} inside the New member tile`).toBe(true);
  }
  await tile.screenshot({ path: path.join(SHOTS, 'new-member-form.png') });
  // Escape cancels.
  await page.locator('[data-test=add-agent-name]').fill('Developer Priya');
  await page.keyboard.press('Escape');
  await expect(form).toHaveCount(0);
  await expect(page.locator('[data-test=member-card-new]')).toBeVisible();
  // Cancel clears what was typed.
  await page.locator('[data-test=member-card-new]').click();
  await expect(page.locator('[data-test=add-agent-name]')).toHaveValue('');
  await page.locator('[data-test=add-agent-cancel]').click();
  await expect(form).toHaveCount(0);
  // A refused add: the form and the name stay, an error shows.
  await page.locator('[data-test=member-card-new]').click();
  await page.locator('[data-test=add-agent-name]').fill('Developer Priya');
  refuse(true);
  await add.click();
  await expect(page.getByText('already on the team')).toBeVisible();
  await expect(form).toBeVisible();
  await expect(page.locator('[data-test=add-agent-name]')).toHaveValue('Developer Priya');
  refuse(false);
  // Held: Adding… and the buttons disabled; released: the form closes.
  hold(true);
  await page.locator('[data-test=add-agent-cli]').selectOption({ index: 0 });
  await page.locator('[data-test=add-agent-name]').press('Enter');
  await expect(add).toContainText('Adding…');
  await expect(add).toBeDisabled();
  await expect(page.locator('[data-test=add-agent-cancel]')).toBeDisabled();
  await tile.screenshot({ path: path.join(SHOTS, 'new-member-adding.png') });
  release();
  await expect(form).toHaveCount(0);
  expect(adds.length).toBe(2);
  expect(adds[1]).toMatchObject({ teamId: 1, name: 'Developer Priya' });
  expect(errors).toEqual([]);
  await ctx.close();
});

test('remote Visitor at 1280: same grid, New member last and disabled with the read-only title', async ({ browser }) => {
  const { page, ctx, errors, core } = await open(browser, { access: 'read' });
  await expect(page.locator('[data-test=remote-access-chip]')).toBeVisible();
  const cs = await cells(page);
  expect(cs.length).toBe(4);
  expect(cs[3].isNew).toBe(true);
  expect(rows(cs)).toBe(2);
  const btn = page.locator('[data-test=member-card-new]');
  await expect(btn).toBeDisabled();
  await expect(btn).toHaveAttribute('title', /read-only access/);
  await btn.click({ force: true });
  await expect(page.locator('[data-test=add-agent-form]')).toHaveCount(0);
  expect(core.calls).not.toContain('members.add');
  await page.screenshot({ path: path.join(SHOTS, 'desktop-1280-visitor.png'), fullPage: true });
  expect(errors).toEqual([]);
  await ctx.close();
});

for (const theme of ['light', 'dark'] as const) {
  test(`phone 375 ${theme}: one full-width tile per row, no handles, New member last, a saved size ignored`, async ({ browser }) => {
    const { page, ctx, errors } = await open(browser, { w: 375, h: 812, theme, tiles: { w: 280, h: 200 } });
    const cs = await cells(page);
    await page.screenshot({ path: path.join(SHOTS, `phone-375-${theme}.png`), fullPage: true });
    const gridW = await page.evaluate(() => Math.round(document.querySelector('[data-test=member-grid]')!.getBoundingClientRect().width));
    expect(cs.length).toBe(4);
    expect(cs[3].isNew, 'New member last').toBe(true);
    expect(rows(cs), 'one per row').toBe(4);
    for (const c of cs) { expect(c.w, 'full width').toBe(gridW); expect(c.handles, 'no handles').toBe(0); }
    await expect(page.locator('[data-test^=member-tile-handle]')).toHaveCount(0);
    // Each pane as tall as its content (not the saved 200): the view is not cut, the page scrolls.
    const p = await page.evaluate(() => [...document.querySelectorAll('[data-test=member-cell] .pane')].map((e) => {
      const s = getComputedStyle(e); const b = e.querySelector('[data-test=member-pane-body]') as HTMLElement;
      return { display: s.display, h: Math.round(e.getBoundingClientRect().height), bodyOverflow: getComputedStyle(b).overflowY, clipped: (e as HTMLElement).scrollHeight > (e as HTMLElement).clientHeight + 1 };
    }));
    for (const x of p) { expect.soft(x.display).toBe('block'); expect.soft(x.bodyOverflow).toBe('visible'); expect.soft(x.clipped).toBe(false); }
    const sw = await page.evaluate(() => document.documentElement.scrollWidth);
    expect(sw, 'no sideways scroll').toBeLessThanOrEqual(375);
    // The New member tile: a 64 px target; the form's fields 44 px.
    expect(cs[3].h).toBeGreaterThanOrEqual(64);
    await page.locator('[data-test=member-card-new]').click();
    for (const s of ['add-agent-name', 'add-agent-cli', 'add-agent', 'add-agent-cancel']) {
      const hh = await page.locator(`[data-test=${s}]`).evaluate((e) => e.getBoundingClientRect().height);
      expect.soft(hh, `${s} 44 px`).toBeGreaterThanOrEqual(44);
    }
    await page.locator('[data-test=member-card-new-tile]').screenshot({ path: path.join(SHOTS, `phone-375-new-form-${theme}.png`) });
    expect(errors).toEqual([]);
    await ctx.close();
  });
}

// The menu over the grips: at the smallest tile (280x200) the open ☰ menu is on top of every grip / tile it
// overlaps (elementFromPoint: its own tile's h grip in the gap), and - moved straight down into the tile below by a
// test-only transform, since the real menu only reaches the 14 px gap - above the next row's tile; with the menu closed nothing of a pane shows outside its tile (the
// gap just below / right of each tile is only its own grips). 1280 (three per row) and 900 (two per row, so a member tile
// with its grips sits right under the first tile).
for (const theme of ['light', 'dark'] as const) {
  test(`smallest tile ${theme}: 280x200 menu on top of the gap grips and (pushed) the next row; panes stay inside their tiles`, async ({ browser }) => {
    for (const vw of [1280, 900]) {
      const { page, ctx, errors } = await open(browser, { w: vw, theme, tiles: { w: 280, h: 200 } });
      const where = `${theme} ${vw}`;
      // Closed: nothing of a pane below or right of its tile (sample the 14 px gap); body within the cell.
      const spill = await page.evaluate(() => [...document.querySelectorAll('[data-test=member-cell]')].flatMap((c, i) => {
        const r = c.getBoundingClientRect();
        const body = c.querySelector('[data-test=member-pane-body]')!.getBoundingClientRect();
        const bad: string[] = [];
        if (body.bottom > r.bottom + 1 || body.right > r.right + 1) bad.push(`tile ${i}: body ${Math.round(body.bottom)} > cell ${Math.round(r.bottom)}`);
        for (let x = r.left + 4; x < r.right - 4; x += 20) {
          const e = document.elementFromPoint(x, r.bottom + 6);
          if (e && c.contains(e) && !e.closest('[data-test^=member-tile-handle]') && r.bottom + 6 < innerHeight) bad.push(`tile ${i}: ${(e as HTMLElement).className || e.tagName} below at x ${Math.round(x)}`);
        }
        for (let y = r.top + 4; y < Math.min(r.bottom - 4, innerHeight); y += 20) {
          const e = document.elementFromPoint(r.right + 6, y);
          if (e && c.contains(e) && !e.closest('[data-test^=member-tile-handle]')) bad.push(`tile ${i}: ${(e as HTMLElement).className || e.tagName} right at y ${Math.round(y)}`);
        }
        return bad;
      }));
      expect.soft(spill, `${where}: no pane content outside its tile`).toEqual([]);
      const cellsNow = await cells(page);
      for (const i of [0, 1]) {
        const cell = page.locator('[data-test=member-cell]').nth(i);
        await cell.locator('[data-test=member-more]').click();
        const menu = cell.locator('[data-test=member-more-menu]');
        await expect(menu).toBeVisible();
        await page.waitForTimeout(150);
        await page.screenshot({ path: path.join(SHOTS, `menu-over-grips-${vw}-280x200-menu${i}-${theme}.png`) });
        const o = await menu.evaluate((el, idx) => {
          const m = el.getBoundingClientRect();
          const own = el.closest('.member-cell')!;
          const cellR = own.getBoundingClientRect();
          // Every element of another cell (the cell itself, its pane, its handles) the menu overlaps.
          const others = [...document.querySelectorAll('[data-test=member-grid] > .member-cell')].filter((c) => c !== own);
          // ... and every resize grip, the menu's own tile's grips included (they sit in the gap the menu reaches into).
          const targets = [...others, ...document.querySelectorAll('[data-test=member-grid] [data-test^=member-tile-handle]')];
          const hits: { what: string; ok: boolean }[] = [];
          for (const t of targets) {
            const r = t.getBoundingClientRect();
            const l = Math.max(r.left, m.left), rt = Math.min(r.right, m.right), tp = Math.max(r.top, m.top), b = Math.min(r.bottom, m.bottom);
            if (rt - l < 2 || b - tp < 2) continue;
            const x = (l + rt) / 2, y = (tp + b) / 2;
            const e = document.elementFromPoint(x, y);
            hits.push({ what: (t.getAttribute('data-test') || (t as HTMLElement).className) + ` @${Math.round(x)},${Math.round(y)}`, ok: !!e && el.contains(e) });
          }
          return { idx, menuB: Math.round(m.bottom), cellB: Math.round(cellR.bottom), hits, handles: hits.filter((h) => h.what.startsWith('member-tile-handle')).length };
        }, i);
        console.log(`menu-over-grips ${where} tile ${i}: ${JSON.stringify(o)}`);
        // As it is, the menu reaches only into the 14 px gap (TesterBen's: bottom 372 vs tile 366, next row at 380);
        // where it passes its tile it must cover the grips there.
        if (o.menuB > o.cellB) expect.soft(o.hits.length, `${where} tile ${i}: the menu overlaps a grip past its tile (${JSON.stringify(o)})`).toBeGreaterThan(0);
        expect.soft(o.hits.filter((h) => !h.ok), `${where} tile ${i}: the menu is on top of every tile / grip it overlaps (${JSON.stringify(o)})`).toEqual([]);
        // Stacking probe: the real menu never reaches the next row, so move it straight down (test-only transform) into
        // the tile right below its own; it must still be on top there (the focused
        // cell's z-index 6). No probe when nothing is below (1280, tile 1).
        const probe = await menu.evaluate((el) => {
          const own = el.closest('.member-cell')!;
          const ownR = own.getBoundingClientRect();
          const below = [...document.querySelectorAll('[data-test=member-grid] > .member-cell')]
            .find((c) => { const r = c.getBoundingClientRect(); return r.top >= ownR.bottom && r.left < ownR.right && r.right > ownR.left; });
          if (!below) return null;
          const br = below.getBoundingClientRect();
          const m0 = el.getBoundingClientRect();
          // Straight down, its top 30 px into the tile below (not sideways: the pane clips sideways, overflow-x clip, so a
          // menu moved past its pane's right edge - where the w grips are - is cut there; the real menu is right-aligned
          // inside its pane and never gets there).
          (el as HTMLElement).style.transform = `translateY(${br.top + 30 - m0.top}px)`;
          const m = el.getBoundingClientRect();
          const targets = [below, ...below.querySelectorAll('[data-test^=member-tile-handle]')];
          const hits: { what: string; ok: boolean }[] = [];
          for (const t of targets) {
            const r = t.getBoundingClientRect();
            const l = Math.max(r.left, m.left), rt = Math.min(r.right, m.right), tp = Math.max(r.top, m.top), b = Math.min(r.bottom, m.bottom);
            if (rt - l < 2 || b - tp < 2) continue;
            const e = document.elementFromPoint((l + rt) / 2, (tp + b) / 2);
            hits.push({ what: t.getAttribute('data-test') || (t as HTMLElement).className, ok: !!e && el.contains(e), top: e ? `${e.tagName}.${(e as HTMLElement).className}[${e.getAttribute('data-test') ?? ''}]` : '' } as any);
          }
          (el as HTMLElement).style.transform = '';
          return { below: below.getAttribute('data-test') + (below.classList.contains('new-cell') ? ' (New member)' : ''), hits };
        });
        console.log(`menu-over-grips ${where} tile ${i} probe: ${JSON.stringify(probe)}`);
        if (probe) {
          expect.soft(probe.hits.length, `${where} tile ${i}: the moved menu overlaps the tile below`).toBeGreaterThan(0);
          expect.soft(probe.hits.filter((h) => !h.ok), `${where} tile ${i}: the moved menu is on top of the tile below and its grips (${JSON.stringify(probe)})`).toEqual([]);
        } else expect.soft(vw === 1280 && i === 1, `${where} tile ${i}: a tile below`).toBe(true);
        // Every item is clickable (on top).
        const items = await menu.evaluate((el) => [...el.querySelectorAll('button')].map((b) => { const q = b.getBoundingClientRect(); const e = document.elementFromPoint(q.left + q.width / 2, q.top + q.height / 2); return !!e && b.contains(e); }));
        expect.soft(items.every(Boolean), `${where} tile ${i}: every menu item is on top`).toBe(true);
        await page.keyboard.press('Escape');
        if (await menu.isVisible()) await cell.locator('[data-test=member-more]').click();
        await expect(menu).toBeHidden();
      }
      // Tiles unchanged by opening / closing menus.
      expect(await cells(page)).toEqual(cellsNow);
      expect(errors).toEqual([]);
      await ctx.close();
    }
  });
}
