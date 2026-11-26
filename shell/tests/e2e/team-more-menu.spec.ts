// The team page's ⋯ menu on an iPhone-wide screen, in a real browser (headless Chrome)
// against the FAKE core (tests/e2e/phone/fake-core.ts). Measured, not guessed:
//  - 375 x 812: the open menu's box is inside the viewport; five items Documents, Deliverables, Triggers, Git, Settings,
//    each 44 px tall, their icons (18 px) and labels at one left edge; the menu draws over the member panes
//    (elementFromPoint at each item's centre is that item, and the menu overlaps a pane); nothing scrolls sideways.
//  - each item closes the menu; Deliverables opens the Deliverables page filtered to the team, the others their dialog.
//  - 1280 x 900: no ⋯; the header's five buttons.
//   npx playwright test team-more-menu     (measurements: test-results/team-more-menu-*.json; screenshots: test-results/team-more-menu-*.png)
import { expect, test, type Browser } from './phone/fixtures';
import { mkdirSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { buildRenderer, fakeRemote } from './phone/server';
import { settled } from './settle';

test.use({ channel: 'chrome', headless: true, actionTimeout: 8_000 });
test.setTimeout(60_000);

const OUT = path.resolve(__dirname, '..', '..', 'test-results');
const BUILT = path.resolve(__dirname, '..', '..', 'out', 'team-more-menu', 'renderer');
const save = (name: string, v: unknown) => { mkdirSync(OUT, { recursive: true }); writeFileSync(path.join(OUT, `team-more-menu-${name}.json`), JSON.stringify(v, null, 2)); };

test.afterEach(() => { fakeRemote(BUILT).core.setNeedsYou(true); });

test.beforeAll(async () => {
  test.setTimeout(600_000);
  if (!process.env.TEAM_MORE_MENU_SKIP_BUILD) await buildRenderer(undefined, BUILT);
});

async function teamPage(browser: Browser, w: number, h: number) {
  const s = fakeRemote(BUILT);
  s.core.setAccess('full');
  s.core.setAsks(false);
  // No integration ask and no decision (module-wide in the fake; put back after each test): the member panes come
  // right under Tell, so the open menu lies over them.
  s.core.setNeedsYou(false);
  const mobile = w < 760;
  const ctx = await browser.newContext({ viewport: { width: w, height: h }, isMobile: mobile, hasTouch: mobile, deviceScaleFactor: 3 });
  await s.attach(ctx);
  const page = await ctx.newPage();
  const errors: string[] = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.goto(`${s.url}#team=1`);
  await expect(page.locator('[data-test=team-title]')).toBeVisible({ timeout: 30_000 });
  await expect(page.locator('[data-test^=agent-]').first()).toBeVisible({ timeout: 30_000 });
  await settled(page);
  return { page, ctx, errors };
}

const ITEMS = ['open-documents-menu', 'open-deliverables-menu', 'open-triggers', 'open-git', 'open-manage'];

test('375 x 812: the open ⋯ menu is on screen, its items line up, and it draws over the member panes', async ({ browser }) => {
  const { page, ctx, errors } = await teamPage(browser, 375, 812);
  await page.locator('[data-test=team-more]').click();
  const menu = page.locator('[data-test=team-more-menu]');
  await expect(menu).toBeVisible();
  await page.screenshot({ path: path.join(OUT, 'team-more-menu-375.png') });
  const m = await menu.evaluate((el) => {
    const box = (e: Element) => { const b = e.getBoundingClientRect(); return { l: +b.left.toFixed(1), t: +b.top.toFixed(1), r: +b.right.toFixed(1), b: +b.bottom.toFixed(1), w: +b.width.toFixed(1), h: +b.height.toFixed(1) }; };
    const items = [...el.querySelectorAll('[role=menuitem]')].map((it) => {
      const r = it.getBoundingClientRect();
      const svg = it.querySelector('svg')!;
      const range = document.createRange(); range.selectNodeContents(it); const rects = [...range.getClientRects()];
      const text = [...it.childNodes].find((n) => n.nodeType === 3)!; const tr = document.createRange(); tr.selectNodeContents(text);
      const hit = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
      return { test: it.getAttribute('data-test'), text: (it as HTMLElement).innerText.trim(), box: box(it), icon: box(svg), label: box(tr), rects: rects.length,
        hitIsItem: !!hit && it.contains(hit), hit: hit ? `${hit.tagName}.${(hit as HTMLElement).className}` : null };
    });
    const mb = el.getBoundingClientRect();
    const panes = [...document.querySelectorAll('[data-test^=agent-]')].map((p) => p.getBoundingClientRect())
      .filter((p) => Math.min(p.right, mb.right) > Math.max(p.left, mb.left) && Math.min(p.bottom, mb.bottom) > Math.max(p.top, mb.top));
    return { menu: box(el), zIndex: getComputedStyle(el).zIndex, overflowY: getComputedStyle(el).overflowY, scrollH: el.scrollHeight, clientH: el.clientHeight,
      vw: innerWidth, vh: innerHeight, docScrollW: document.documentElement.scrollWidth, panesUnderMenu: panes.length, items };
  });
  save('menu-375', m);
  expect(m.items.map((i) => i.text)).toEqual(['Documents', 'Deliverables', 'Triggers', 'Git', 'Settings']);
  expect(m.items.map((i) => i.test)).toEqual(ITEMS);
  // inside the 375 px viewport
  expect(m.menu.l).toBeGreaterThanOrEqual(0);
  expect(m.menu.r).toBeLessThanOrEqual(m.vw);
  expect(m.menu.t).toBeGreaterThanOrEqual(0);
  expect(m.menu.b).toBeLessThanOrEqual(m.vh);
  expect(m.docScrollW).toBeLessThanOrEqual(m.vw);
  // items line up: 44 px rows stacked, one left edge for icons and one for labels, 18 px icons, label right of icon
  for (const it of m.items) {
    expect(it.box.h, it.text).toBeCloseTo(44, 0);
    expect(it.icon.w, it.text).toBeCloseTo(18, 0);
    expect(it.icon.l, it.text).toBeCloseTo(m.items[0]!.icon.l, 0);
    expect(it.label.l, it.text).toBeCloseTo(m.items[0]!.label.l, 0);
    expect(it.box.l, it.text).toBeCloseTo(m.items[0]!.box.l, 0);
    expect(it.label.l - it.icon.r, it.text).toBeCloseTo(10, 0);
    expect(it.label.r, it.text).toBeLessThanOrEqual(it.box.r); // label not clipped
    expect(Math.abs((it.icon.t + it.icon.h / 2) - (it.box.t + it.box.h / 2)), it.text).toBeLessThan(1.5); // icon centred in the row
  }
  for (let i = 1; i < m.items.length; i++) expect(m.items[i]!.box.t, m.items[i]!.text).toBeCloseTo(m.items[i - 1]!.box.b, 0);
  // drawn over the member panes
  expect(m.panesUnderMenu).toBeGreaterThan(0);
  for (const it of m.items) expect(it.hitIsItem, `${it.text} hit ${it.hit}`).toBe(true);
  expect(errors).toEqual([]);
  await ctx.close();
});

test('375 x 812: each ⋯ dialog item closes the menu and opens its dialog', async ({ browser }) => {
  const { page, ctx, errors } = await teamPage(browser, 375, 812);
  const titles: Record<string, string | null> = {};
  for (const id of ITEMS.filter((i) => i !== 'open-deliverables-menu')) {
    await page.locator('[data-test=team-more]').click();
    await expect(page.locator('[data-test=team-more-menu]')).toBeVisible();
    await page.locator(`[data-test=team-more-menu] [data-test=${id}]`).click();
    await expect(page.locator('[data-test=team-more-menu]')).toHaveCount(0);
    const dlg = page.locator('[role=dialog]');
    await expect(dlg).toHaveCount(1);
    titles[id] = await dlg.getAttribute('aria-label');
    await page.screenshot({ path: path.join(OUT, `team-more-menu-${id}-375.png`) });
    await page.locator('[data-test=modal-close]').click();
    await expect(dlg).toHaveCount(0);
  }
  save('dialogs-375', titles);
  expect(titles).toEqual({
    'open-documents-menu': 'payments · documents',
    'open-triggers': 'Triggers · payments', 'open-git': 'Git · payments', 'open-manage': 'Settings · payments',
  });
  expect(errors).toEqual([]);
  await ctx.close();
});

test('375 x 812: the ⋯ Deliverables item closes the menu and opens the Deliverables page filtered to the team, no dialog', async ({ browser }) => {
  const { page, ctx, errors } = await teamPage(browser, 375, 812);
  await page.locator('[data-test=team-more]').click();
  const item = page.locator('[data-test=team-more-menu] [data-test=open-deliverables-menu]');
  await expect(item).toHaveText('Deliverables');
  await expect(item).toHaveAttribute('title', /Deliverables page/);
  await item.click();
  await expect(page.locator('[data-test=team-more-menu]')).toHaveCount(0);
  await expect(page.locator('[data-test=deliverables-view]')).toBeVisible({ timeout: 15_000 });
  await expect(page.locator('[data-test=deliverables-team-filter]')).toHaveValue('1');
  await expect(page.locator('[data-test=team-title]')).toHaveCount(0);
  await expect(page.locator('[role=dialog]')).toHaveCount(0);
  await page.screenshot({ path: path.join(OUT, 'team-more-menu-open-deliverables-menu-375.png') });
  expect(errors).toEqual([]);
  await ctx.close();
});

test('1280 x 900: the header has its five buttons and no ⋯', async ({ browser }) => {
  const { page, ctx, errors } = await teamPage(browser, 1280, 900);
  await page.screenshot({ path: path.join(OUT, 'team-more-menu-header-1280.png') });
  await expect(page.locator('[data-test=team-more]')).toHaveCount(0);
  const ids = await page.locator('.teamhead .actions > button').evaluateAll((bs) => bs.map((b) => b.getAttribute('data-test')));
  expect(ids).toEqual(['open-documents', 'open-deliverables', 'open-triggers', 'open-git', 'open-manage']);
  for (const id of ids) await expect(page.locator(`[data-test=${id}]`)).toBeVisible();
  expect(errors).toEqual([]);
  await ctx.close();
});
