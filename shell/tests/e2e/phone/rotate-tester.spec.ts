// Card 335 (verify card 334, TesterKofi): rotations rotate.spec.ts does not cover, in WebKit on the remote view.
//  - back and forth twice (portrait -> landscape -> portrait -> landscape -> portrait) on a phone and an iPad;
//  - a rotation with a modal / phone sheet open (#workflow=10, the workflows dialog);
//  - a rotation with the phone drawer open.
// After every turn: no horizontal overflow, the app root exactly as tall as the viewport, the layout for the new width
// (phone below PHONE_BREAKPOINT, the desktop sidebar from it), and an open modal inside the viewport.
//
//   npx playwright test rotate-tester      (B037_SKIP_BUILD=1 reuses out/b037/renderer)
import { devices, expect, test, type Page } from './fixtures';
import { PHONE_BREAKPOINT } from '../../../src/renderer/src/phone';
import { buildRenderer, builtDir, fakeRemote } from './server';

test.use({ browserName: 'webkit', headless: true, actionTimeout: 8_000 });
test.setTimeout(90_000);
test.beforeAll(async () => { test.setTimeout(600_000); await buildRenderer(); });

type Size = { width: number; height: number };

async function open(browser: import('@playwright/test').Browser, device: string, hash: string, ready: string) {
  const { defaultBrowserType: _b, ...descriptor } = devices[device];
  const s = fakeRemote(builtDir);
  s.core.setAsks(false);
  const ctx = await browser.newContext(descriptor);
  await s.attach(ctx);
  const page = await ctx.newPage();
  const errors: string[] = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.goto(`${s.url}${hash}`);
  await expect(page.locator(ready).first()).toBeVisible({ timeout: 30_000 });
  await page.waitForTimeout(300);
  return { ctx, page, errors };
}

async function rotate(page: Page, to: Size) {
  await page.setViewportSize(to);
  await page.evaluate(() => new Promise((ok) => requestAnimationFrame(() => requestAnimationFrame(ok))));
  await page.waitForTimeout(300);
}

async function fits(page: Page, to: Size, step: string) {
  const m = await page.evaluate(() => {
    const app = document.querySelector<HTMLElement>('.app')!;
    const se = document.scrollingElement!;
    const r = app.getBoundingClientRect();
    const modal = document.querySelector<HTMLElement>('.modal')?.getBoundingClientRect() ?? null;
    return {
      innerWidth, innerHeight, scrollWidth: se.scrollWidth, scrollHeight: se.scrollHeight,
      appTop: r.top, appHeight: r.height, appWidth: r.width, phone: app.classList.contains('phone'),
      drawer: document.querySelector<HTMLElement>('[data-test=sidebar]')?.dataset.drawer ?? null,
      modal: modal && { left: modal.left, top: modal.top, right: modal.right, bottom: modal.bottom, width: modal.width, height: modal.height },
    };
  });
  const info = `${step} ${to.width}x${to.height}: ${JSON.stringify(m)}`;
  expect(m.innerWidth, info).toBe(to.width);
  expect(m.scrollWidth, `horizontal overflow, ${info}`).toBeLessThanOrEqual(m.innerWidth);
  expect(m.scrollHeight, `vertical overflow, ${info}`).toBeLessThanOrEqual(m.innerHeight);
  expect(m.appTop, info).toBe(0);
  expect(Math.abs(m.appHeight - m.innerHeight), `root height, ${info}`).toBeLessThanOrEqual(1);
  expect(Math.abs(m.appWidth - m.innerWidth), `root width, ${info}`).toBeLessThanOrEqual(1);
  expect(m.phone, `layout, ${info}`).toBe(to.width < PHONE_BREAKPOINT);
  if (m.modal) {
    expect(m.modal.left, `modal left, ${info}`).toBeGreaterThanOrEqual(0);
    expect(m.modal.top, `modal top, ${info}`).toBeGreaterThanOrEqual(0);
    expect(m.modal.right, `modal right, ${info}`).toBeLessThanOrEqual(m.innerWidth + 1);
    expect(m.modal.bottom, `modal bottom, ${info}`).toBeLessThanOrEqual(m.innerHeight + 1);
  }
  return m;
}

const turn = (v: Size): Size => ({ width: v.height, height: v.width });

for (const device of ['iPhone 13', 'iPhone 14 Pro Max', 'iPad Mini'] as const) {
  test(`${device}: back and forth twice on a team page, fits after every turn`, async ({ browser }) => {
    const { ctx, page, errors } = await open(browser, device, '#team=1', '[data-test=agent-DeveloperAda]');
    const portrait = devices[device].viewport;
    await fits(page, portrait, 'start');
    let v = portrait;
    for (let i = 1; i <= 4; i++) {
      v = turn(v);
      await rotate(page, v);
      await fits(page, v, `turn ${i}`);
      await expect(page.locator('[data-test=agent-DeveloperAda]').first()).toBeVisible();
    }
    expect(errors).toEqual([]);
    await ctx.close();
  });
}

for (const device of ['iPhone 13', 'iPhone 14 Pro Max', 'iPad Pro 11'] as const) {
  test(`${device}: rotation with the workflows dialog (modal / phone sheet) open`, async ({ browser }) => {
    const { ctx, page, errors } = await open(browser, device, '#workflow=10', '[data-test=workflow-10]');
    await expect(page.locator('.modal')).toBeVisible();
    let v = devices[device].viewport;
    const m0 = await fits(page, v, 'start');
    expect(m0.modal, 'a modal is open').not.toBeNull();
    for (let i = 1; i <= 2; i++) {
      v = turn(v);
      await rotate(page, v);
      const m = await fits(page, v, `turn ${i}`);
      expect(m.modal, `the dialog stays open after turn ${i}`).not.toBeNull();
      // A phone sheet fills the width it now has (100%, not a stale 100vw).
      if (v.width < PHONE_BREAKPOINT) expect(Math.abs(m.modal!.width - v.width), `sheet width after turn ${i}`).toBeLessThanOrEqual(1);
      await expect(page.locator('[data-test=workflow-10]').first()).toBeVisible();
    }
    expect(errors).toEqual([]);
    await ctx.close();
  });
}

for (const device of ['iPhone 13', 'iPhone SE'] as const) {
  test(`${device}: rotation with the phone drawer open`, async ({ browser }) => {
    const { ctx, page, errors } = await open(browser, device, '', '.app');
    await page.locator('[data-test=drawer-toggle]').click();
    const side = page.locator('[data-test=sidebar]');
    await expect(side).toHaveAttribute('data-drawer', 'open');
    let v = devices[device].viewport;
    for (let i = 1; i <= 2; i++) {
      v = turn(v);
      await rotate(page, v);
      await fits(page, v, `drawer, turn ${i}`);
      // Still a phone width: the drawer is still the drawer, and inside the screen when open.
      const state = await side.getAttribute('data-drawer');
      expect(['open', 'closed'], `drawer state after turn ${i}`).toContain(state);
      if (state === 'open') {
        const b = (await side.boundingBox())!;
        expect(b.x, `drawer left after turn ${i}`).toBeGreaterThanOrEqual(0);
        expect(b.x + b.width, `drawer right after turn ${i}`).toBeLessThanOrEqual(v.width + 1);
        expect(b.y + b.height, `drawer bottom after turn ${i}`).toBeLessThanOrEqual(v.height + 1);
        // Settings reachable in the drawer at the new height.
        const settings = side.locator('[data-test=open-app-settings]');
        await settings.scrollIntoViewIfNeeded();
        const sb = (await settings.boundingBox())!;
        expect(sb.y + sb.height, `Settings on screen after turn ${i}`).toBeLessThanOrEqual(v.height + 1);
      }
    }
    expect(errors).toEqual([]);
    await ctx.close();
  });
}

test('1280 x 800 desktop: unchanged layout (268 px sidebar, main the rest, root 800 tall), and a resize to 1280 x 600 and back', async ({ browser }) => {
  const { ctx, page, errors } = await open(browser, 'Desktop Safari', '#team=1', '[data-test=agent-DeveloperAda]');
  for (const v of [{ width: 1280, height: 800 }, { width: 1280, height: 600 }, { width: 1280, height: 800 }]) {
    await rotate(page, v);
    await fits(page, v, 'desktop');
    const side = (await page.locator('[data-test=sidebar]').boundingBox())!;
    const main = (await page.locator('.main').boundingBox())!;
    expect(side.x + side.width, 'sidebar right edge').toBe(268);
    expect(Math.abs(side.height - v.height), 'sidebar full height').toBeLessThanOrEqual(1);
    expect(main.x, 'main starts after the sidebar').toBe(268);
    expect(Math.abs(main.width - (v.width - 268)), 'main takes the rest').toBeLessThanOrEqual(1);
    // At 800 px the sidebar itself does not scroll (only its lists do).
    if (v.height === 800) {
      const scrolls = await page.locator('[data-test=sidebar]').evaluate((e) => e.scrollHeight > e.clientHeight);
      expect(scrolls, 'the 1280x800 sidebar has no scrollbar of its own').toBe(false);
    }
  }
  expect(errors).toEqual([]);
  await ctx.close();
});

test('iPhone 14 Pro Max: drawer open in portrait, turned to the desktop width (814), gives the plain sidebar', async ({ browser }) => {
  const { ctx, page, errors } = await open(browser, 'iPhone 14 Pro Max', '', '.app');
  await page.locator('[data-test=drawer-toggle]').click();
  await expect(page.locator('[data-test=sidebar]')).toHaveAttribute('data-drawer', 'open');
  const land = devices['iPhone 14 Pro Max landscape'].viewport;
  await rotate(page, land);
  await fits(page, land, 'to desktop');
  expect(await page.locator('[data-test=sidebar]').getAttribute('data-drawer')).toBeNull();
  await expect(page.locator('.menu-btn')).toHaveCount(0);
  // The 380 px tall desktop sidebar scrolls (overflow-y: auto) so Settings is reachable.
  const settings = page.locator('[data-test=sidebar] [data-test=open-app-settings]');
  await settings.scrollIntoViewIfNeeded();
  const sb = (await settings.boundingBox())!;
  expect(sb.y + sb.height, 'Settings reachable at 814x380').toBeLessThanOrEqual(land.height + 1);
  // And back to portrait: the phone layout again, drawer closed or open but inside the screen.
  const port = devices['iPhone 14 Pro Max'].viewport;
  await rotate(page, port);
  await fits(page, port, 'back to phone');
  expect(['open', 'closed']).toContain(await page.locator('[data-test=sidebar]').getAttribute('data-drawer'));
  expect(errors).toEqual([]);
  await ctx.close();
});
