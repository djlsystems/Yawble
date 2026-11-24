// Card 320 (verify card 319): the Teams page's card buttons on an iPhone-wide screen, in a real browser (headless
// Chrome) against the FAKE core (tests/e2e/phone/fake-core.ts). Measured, not guessed:
//  - 375 x 812 (iPhone): Settings, Pause, Archive and Delete on ONE row, each a 44 x 44 icon button whose aria-label
//    is its label (title starts with it); the labels hidden; nothing off the card's edge.
//  - the Delete confirm row (tap Delete): one row, "Cancel" and "Delete" shown as words, the question ellipsized,
//    the buttons 44 px tall, nothing past the row's edge.
//  - 1280 x 900 (desktop): the labels shown on one row.
//   npx playwright test card320-teams-buttons     (measurements: test-results/card320-measure.json; screenshots: test-results/card320-*.png)
import { expect, test, type Browser, type Page } from './phone/fixtures';
import { mkdirSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { buildRenderer, fakeRemote } from './phone/server';

test.use({ channel: 'chrome', headless: true, actionTimeout: 8_000 });
test.setTimeout(60_000);

const OUT = path.resolve(__dirname, '..', '..', 'test-results');
const BUILT = path.resolve(__dirname, '..', '..', 'out', 'card320', 'renderer');
const measured = new Proxy({} as Record<string, unknown>, { set(o, k, v) { o[k as string] = v; mkdirSync(OUT, { recursive: true }); writeFileSync(path.join(OUT, `card320-measure-${String(k)}.json`), JSON.stringify(v, null, 2)); return true; } });

test.beforeAll(async () => {
  test.setTimeout(600_000);
  await buildRenderer(undefined, BUILT);
});

async function teamsPage(browser: Browser, w: number, h: number, tile?: { w: number; h: number }) {
  const s = fakeRemote(BUILT);
  s.core.setAccess('full');
  const mobile = w < 760;
  const ctx = await browser.newContext({ viewport: { width: w, height: h }, isMobile: mobile, hasTouch: mobile, deviceScaleFactor: 3 });
  if (tile) await ctx.addInitScript((t) => { localStorage.setItem('yawble.teamTiles', t); }, JSON.stringify(tile));
  await s.attach(ctx);
  const page = await ctx.newPage();
  await page.goto(s.url);
  await expect(page.locator('[data-test=sidebar]')).toBeAttached({ timeout: 30_000 });
  if (mobile) {
    await page.locator('[data-test=drawer-toggle]').click();
    await expect(page.locator('[data-test=sidebar]')).toHaveAttribute('data-drawer', 'open');
  }
  await page.locator('[data-test=sidebar] [data-test=nav-teams]').first().click();
  await expect(page.locator('[data-test=team-card]').first()).toBeVisible();
  await page.waitForTimeout(500);
  return { page, ctx };
}

/** The first team card's action row: the row's box and each child's box, text, aria-label, title, label visibility. */
async function row(page: Page) {
  return page.locator('[data-test=team-card]').first().locator('[data-test=card-actions]').evaluate((r) => {
    const box = (e: Element) => { const b = e.getBoundingClientRect(); return { x: +b.x.toFixed(1), y: +b.y.toFixed(1), w: +b.width.toFixed(1), h: +b.height.toFixed(1) }; };
    const card = r.closest('[data-test=team-card]')!;
    return {
      gridIconsOnly: !!r.closest('.icons-only'),
      flexWrap: getComputedStyle(r).flexWrap,
      row: box(r), card: box(card), scrollWidth: r.scrollWidth, clientWidth: r.clientWidth,
      items: [...r.children].map((c) => {
        const lbl = c.querySelector('.lbl');
        const el = c as HTMLElement;
        return {
          test: c.getAttribute('data-test'), cls: c.className, text: el.innerText.trim(), aria: c.getAttribute('aria-label'), title: c.getAttribute('title'),
          lblShown: lbl ? getComputedStyle(lbl).display !== 'none' : null, clipped: el.scrollWidth > el.clientWidth, ...box(c),
        };
      }),
    };
  });
}

test('375 px iPhone: the four buttons are 44 px icons on one row', async ({ browser }) => {
  const { page, ctx } = await teamsPage(browser, 375, 812);
  const m = await row(page);
  measured['phone375.normal'] = m;
  await page.locator('[data-test=team-card]').first().screenshot({ path: path.join(OUT, 'card320-phone375-normal.png') });
  expect(m.gridIconsOnly, 'icons-only switched on').toBe(true);
  expect(m.flexWrap).toBe('nowrap');
  const btns = m.items;
  expect(btns.map((b) => b.test)).toEqual(['card-settings', 'card-pause', 'card-archive', 'card-delete']);
  const labels = ['Settings', 'Pause', 'Archive', 'Delete'];
  btns.forEach((b, i) => {
    expect.soft(b.w, `${b.test} width`).toBe(44);
    expect.soft(b.h, `${b.test} height`).toBe(44);
    expect.soft(b.y, `${b.test} on the first button's row`).toBe(btns[0].y);
    expect.soft(b.aria, `${b.test} aria-label`).toBe(labels[i]);
    expect.soft(b.title ?? '', `${b.test} title`).toMatch(new RegExp(`^${labels[i]}`));
    expect.soft(b.lblShown, `${b.test} label hidden`).toBe(false);
    expect.soft(b.x + b.w, `${b.test} inside the row`).toBeLessThanOrEqual(m.row.x + m.row.w + 0.5);
  });
  expect.soft(m.scrollWidth, 'the row does not overflow').toBeLessThanOrEqual(m.clientWidth);

  await page.locator('[data-test=team-card]').first().locator('[data-test=card-delete]').click();
  await page.waitForTimeout(300);
  const c = await row(page);
  measured['phone375.confirm'] = c;
  await page.locator('[data-test=team-card]').first().screenshot({ path: path.join(OUT, 'card320-phone375-confirm.png') });
  const q = c.items.find((i) => i.cls.includes('confirm-q'))!;
  const yes = c.items.find((i) => i.test === 'card-delete-yes')!;
  const no = c.items.find((i) => i.test === 'card-delete-no')!;
  expect.soft(no.text, 'Cancel as a word').toBe('Cancel');
  expect.soft(yes.text, 'Delete as a word').toBe('Delete');
  expect.soft(no.clipped, 'Cancel not cut').toBe(false);
  expect.soft(yes.clipped, 'Delete not cut').toBe(false);
  expect.soft(yes.h).toBe(44);
  expect.soft(no.h).toBe(44);
  expect.soft(no.y, 'Cancel on the Delete button row').toBe(yes.y);
  expect.soft(q.y + q.h, 'the question on the same row (its bottom above the buttons\' bottom)').toBeLessThanOrEqual(yes.y + yes.h + 0.5);
  expect.soft(q.y, 'the question on the same row (its top at or below the buttons\' top)').toBeGreaterThanOrEqual(yes.y - 0.5);
  expect.soft(no.x + no.w, 'Cancel inside the row').toBeLessThanOrEqual(c.row.x + c.row.w + 0.5);
  expect.soft(c.scrollWidth, 'the confirm row does not overflow').toBeLessThanOrEqual(c.clientWidth);
  // The second team has a long name: its question must ellipsize, not push the buttons out.
  const second = page.locator('[data-test=team-card]').nth(1);
  await second.locator('[data-test=card-delete]').click();
  await page.waitForTimeout(300);
  const long = await second.locator('[data-test=card-actions]').evaluate((r) => {
    const q = r.querySelector('.confirm-q') as HTMLElement; const s = getComputedStyle(q);
    const no = r.querySelector('[data-test=card-delete-no]')!.getBoundingClientRect(); const rb = r.getBoundingClientRect();
    return { text: q.innerText, qScroll: q.scrollWidth, qClient: q.clientWidth, textOverflow: s.textOverflow, whiteSpace: s.whiteSpace, noRight: no.right, rowRight: rb.right, rowScroll: r.scrollWidth, rowClient: r.clientWidth };
  });
  measured['phone375.confirmLongName'] = long;
  await second.screenshot({ path: path.join(OUT, 'card320-phone375-confirm-longname.png') });
  expect.soft(long.qScroll, 'the long question is cut (ellipsized)').toBeGreaterThan(long.qClient);
  expect.soft(long.textOverflow).toBe('ellipsis');
  expect.soft(long.whiteSpace).toBe('nowrap');
  expect.soft(long.noRight, 'Cancel inside the row').toBeLessThanOrEqual(long.rowRight + 0.5);
  expect.soft(long.rowScroll).toBeLessThanOrEqual(long.rowClient);
  await ctx.close();
});

test('1280 px desktop, default 320 px tile: recorded (the labelled row needs more than the tile, as before card 319)', async ({ browser }) => {
  const { page, ctx } = await teamsPage(browser, 1280, 900);
  const m = await row(page);
  measured['desktop1280.tile320'] = m;
  await page.locator('[data-test=team-card]').first().screenshot({ path: path.join(OUT, 'card320-desktop1280-tile320.png') });
  // Not a phone: the buttons keep the desktop size (28 px tall), and the row stays inside the tile.
  for (const b of m.items) expect.soft(b.h, `${b.test} desktop height`).toBe(28);
  expect.soft(m.scrollWidth).toBeLessThanOrEqual(m.clientWidth);
  await ctx.close();
});

test('1280 px desktop, a 480 px tile: the labels are shown', async ({ browser }) => {
  const { page, ctx } = await teamsPage(browser, 1280, 900, { w: 480, h: 260 });
  const m = await row(page);
  measured['desktop1280.tile480'] = m;
  await page.locator('[data-test=team-card]').first().screenshot({ path: path.join(OUT, 'card320-desktop1280-tile480.png') });
  expect(m.gridIconsOnly).toBe(false);
  for (const b of m.items) {
    expect.soft(b.lblShown, `${b.test} label shown`).toBe(true);
    expect.soft(b.y, `${b.test} on one row`).toBe(m.items[0].y);
  }
  expect(m.items.map((b) => b.text)).toEqual(['Settings', 'Pause', 'Archive', 'Delete']);
  await ctx.close();
});
