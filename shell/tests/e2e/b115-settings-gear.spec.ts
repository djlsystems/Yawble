// B115 Part A (card 516): Settings is a gear at the top right, just right of the theme switch, in headless Chrome
// against the FAKE core of phone-remote.spec.ts. Two windows:
//  - the remote view: remote.html, its socket routed to the fake core (fakeRemote);
//  - the desktop window: index.html of the same build, with a fake window.yawble (the preload bridge) whose call() goes
//    to the same fake core. No Electron, no real core, no agent.
// At 1280x800 and 375x740, light and dark: the gear is in the top bar, right of the theme switch and the bar's last
// button, at the right edge, named Settings (title and aria-label), its icon drawn in the theme's colours; the sidebar
// has no Settings item and its run-slots line is still at its bottom; a click opens Settings. Ctrl+, still opens it.
// Screenshots go to $YAWBLE_TEAM_DOCS/B115/.
//
//   npx playwright test b115-settings-gear      (B037_SKIP_BUILD=1 reuses out/b037/renderer)
import { expect, test, type Browser, type Page } from './phone/fixtures';
import path from 'node:path';
import { createFakeCore } from './phone/fake-core';
import { ORIGIN, buildRenderer, builtDir, ensureDir, fakeRemote } from './phone/server';
import { existsSync } from 'node:fs';

test.use({ channel: 'chrome', headless: true, actionTimeout: 8_000 });
test.setTimeout(60_000);

const shots = process.env.YAWBLE_TEAM_DOCS ? path.join(process.env.YAWBLE_TEAM_DOCS, 'B115') : null;

test.beforeAll(async () => { test.setTimeout(600_000); if (shots) ensureDir(shots); await buildRenderer(); });

type Window = 'desktop' | 'remote';

async function open(browser: Browser, which: Window, w: number, h: number, theme: 'light' | 'dark') {
  // A phone is a remote view (index.html has no viewport tag: a desktop window narrowed to 375 px is not a touch device).
  const mobile = which === 'remote' && w < 760;
  const ctx = await browser.newContext({ viewport: { width: w, height: h }, isMobile: mobile, hasTouch: mobile, colorScheme: theme, reducedMotion: 'reduce' });
  let url: string;
  if (which === 'remote') {
    const s = fakeRemote(builtDir);
    s.core.setAsks(false);
    await s.attach(ctx);
    url = s.url;
  } else {
    // The desktop window: the built index.html with the preload's window.yawble faked; its calls reach the fake core.
    const core = createFakeCore();
    core.setAsks(false);
    let id = 0;
    await ctx.exposeBinding('__b115Call', (_src, method: string, params: unknown) => core.answer(JSON.stringify({ id: ++id, method, params })));
    await ctx.addInitScript(() => {
      const call = async (method: string, params?: unknown) => {
        const out = JSON.parse(await (window as any).__b115Call(method, params ?? null));
        if (out.error) throw Object.assign(new Error(out.error.message), { code: out.error.code });
        return out.result;
      };
      (window as any).yawble = {
        platform: 'win32',
        getInfo: () => call('core.getInfo'),
        getAvailableBackends: async () => [],
        call,
        onEvent: () => () => {},
        openTerminal: async () => {},
        pickFolder: async () => null,
        setTheme: () => {},
      };
    });
    await ctx.route(`${ORIGIN}/**`, async (route) => {
      const rel = decodeURIComponent(new URL(route.request().url()).pathname).replace(/^\/+/, '');
      const file = path.resolve(builtDir, rel || 'index.html');
      if (!file.startsWith(builtDir) || !existsSync(file)) return route.fulfill({ status: 404, body: 'not found' });
      return route.fulfill({ path: file });
    });
    url = `${ORIGIN}/index.html`;
  }
  const page = await ctx.newPage();
  const errors: string[] = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.goto(url);
  await expect(page.locator('[data-test=sidebar]')).toBeAttached({ timeout: 30_000 });
  await expect(page.locator('[data-test=slots]')).toContainText('Run slots', { timeout: 15_000 });
  return { page, ctx, errors };
}

/** The gear's box, the theme switch's and the bar's, and the gear's colours. */
async function measure(page: Page) {
  return page.evaluate(() => {
    const box = (sel: string) => { const r = document.querySelector(sel)!.getBoundingClientRect(); return { x: r.x, y: r.y, w: r.width, h: r.height, right: r.right, bottom: r.bottom }; };
    const gear = document.querySelector('[data-testid=settings-gear]') as HTMLElement;
    const bar = gear.closest('header')!;
    const buttons = [...bar.querySelectorAll('button')].filter((b) => b.getBoundingClientRect().width > 0);
    return {
      gear: box('[data-testid=settings-gear]'), toggle: box('[data-test=theme-toggle]'), bar: box('header.bar'),
      prev: (gear.previousElementSibling as HTMLElement | null)?.getAttribute('data-test') ?? null,
      lastButton: buttons[buttons.length - 1] === gear,
      iconColor: getComputedStyle(gear).color, toggleColor: getComputedStyle(document.querySelector('[data-test=theme-toggle]')!).color,
      theme: document.documentElement.getAttribute('data-theme'),
      inner: window.innerWidth,
    };
  });
}

for (const which of ['desktop', 'remote'] as const) {
  for (const [w, h, theme] of [[1280, 800, 'light'], [1280, 800, 'dark'], [375, 740, 'light'], [375, 740, 'dark']] as const) {
    test(`${which} window at ${w}px (${theme}): the Settings gear is at the top right, right of the theme switch, and opens Settings`, async ({ browser }) => {
      const { page, errors } = await open(browser, which, w, h, theme);
      if (w < 760) await expect(page.locator('.app')).toHaveClass(/\bphone\b/);
      else await expect(page.locator('.app')).not.toHaveClass(/\bphone\b/);
      const gear = page.getByTestId('settings-gear');
      await expect(gear).toBeVisible();
      await expect(gear).toHaveAttribute('title', 'Settings');
      await expect(gear).toHaveAttribute('aria-label', 'Settings');
      await expect(page.getByRole('button', { name: 'Settings', exact: true })).toHaveCount(1);
      const m = await measure(page);
      expect(m.theme).toBe(theme);
      expect(m.prev, 'the gear comes right after the theme switch').toBe('theme-toggle');
      expect(m.lastButton, 'the gear is the bar\'s last button').toBe(true);
      expect(m.gear.x, 'right of the theme switch').toBeGreaterThanOrEqual(m.toggle.right - 1);
      expect(Math.abs((m.gear.y + m.gear.h / 2) - (m.toggle.y + m.toggle.h / 2)), 'on the theme switch\'s line').toBeLessThanOrEqual(2);
      expect(m.inner - m.gear.right, 'at the right edge of the window').toBeLessThanOrEqual(w < 760 ? 16 : 24);
      expect(m.gear.bottom, 'in the top bar').toBeLessThanOrEqual(m.bar.bottom + 1);
      expect(m.iconColor, 'drawn as the theme switch beside it').toBe(m.toggleColor);
      if (w < 760) { expect(m.gear.w).toBeGreaterThanOrEqual(44); expect(m.gear.h).toBeGreaterThanOrEqual(44); }
      // The sidebar: no Settings item (wide, or the phone's drawer), the run slots still at its bottom.
      await expect(page.locator('[data-test=sidebar] [data-test=open-app-settings]')).toHaveCount(0);
      const lastInSidebar = await page.locator('[data-test=sidebar]').evaluate((s) => s.lastElementChild?.lastElementChild?.querySelector('[data-test=slots]') !== null);
      expect(lastInSidebar, 'the run-slots line is the sidebar\'s last row').toBe(true);
      expect(await page.evaluate(() => document.scrollingElement!.scrollWidth <= window.innerWidth + 1), 'no sideways scroll').toBe(true);
      if (shots) await page.screenshot({ path: path.join(shots, `b115-gear-${which}-${w}-${theme}.png`) });
      if (shots) await page.locator('header.bar').screenshot({ path: path.join(shots, `b115-bar-${which}-${w}-${theme}.png`) });

      await gear.click();
      await expect(page.locator('[data-test=settings]')).toBeVisible();
      await expect(gear).toHaveAttribute('aria-current', 'page');
      if (w < 760) await expect(page.locator('[data-test=settings]')).toHaveAttribute('data-phone-screen', 'list');
      if (shots) await page.screenshot({ path: path.join(shots, `b115-settings-${which}-${w}-${theme}.png`) });
      expect(errors).toEqual([]);
    });
  }

  test(`${which} window: Ctrl+, still opens Settings`, async ({ browser }) => {
    const { page, errors } = await open(browser, which, 1280, 800, 'light');
    await expect(page.locator('[data-test=settings]')).toHaveCount(0);
    await page.keyboard.press('Control+Comma');
    await expect(page.locator('[data-test=settings]')).toBeVisible();
    expect(errors).toEqual([]);
  });
}
