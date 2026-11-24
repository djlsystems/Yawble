// Card 334: the remote view (remote.html, the fake core of phone-remote.spec.ts) follows a rotation in WebKit with the
// iPad and iPhone device descriptors: iPad landscape -> portrait, phone portrait -> landscape, rotated mid-test by
// setViewportSize, on the home page, a team page (#team=1) and a workflow (#workflow=10). After the rotation: no
// horizontal overflow, the app root is exactly as tall as the visible viewport (no gap, nothing cut off at the bottom),
// and the layout (the phone's drawer below 760 px, the desktop sidebar from 760 px) is the one for the new width.
//
// Playwright's WebKit keeps 100vh equal to the visible height (iOS Safari's 100vh is the height with its toolbars
// hidden), so the spec also reads the page's own stylesheet as WebKit parsed it: the app root's height is 100dvh, and
// the page asks for no text inflation in landscape (text-size-adjust: 100%). tests/unit/rotate-viewport.test.ts checks
// the same in the sources.
//
//   npx playwright test rotate      (E2E_SKIP_BUILD=1 reuses out/e2e/renderer)
import { devices, expect, test, type Page } from './fixtures';
import { PHONE_BREAKPOINT } from '../../../src/renderer/src/phone';
import { buildRenderer, builtDir, fakeRemote } from './server';

test.use({ browserName: 'webkit', headless: true, actionTimeout: 8_000 });
test.setTimeout(60_000);

test.beforeAll(async () => { test.setTimeout(600_000); await buildRenderer(); });

const ROTATIONS = [
  { device: 'iPad Pro 11', from: 'iPad Pro 11 landscape', to: 'iPad Pro 11' },
  { device: 'iPad Mini', from: 'iPad Mini landscape', to: 'iPad Mini' },
  { device: 'iPhone 13', from: 'iPhone 13', to: 'iPhone 13 landscape' },
  { device: 'iPhone SE', from: 'iPhone SE', to: 'iPhone SE landscape' },
  // Playwright's iPhone 13 and SE landscape stay below 760 px; this one crosses into the desktop layout (430 -> 814).
  { device: 'iPhone 14 Pro Max', from: 'iPhone 14 Pro Max', to: 'iPhone 14 Pro Max landscape' },
] as const;

const PLACES = [
  { place: 'home', hash: '', ready: '.app' },
  { place: 'team page', hash: '#team=1', ready: '[data-test=agent-DeveloperAda]' },
  { place: 'workflow', hash: '#workflow=10', ready: '[data-test=workflow-10]' },
] as const;

async function measure(page: Page) {
  return page.evaluate(() => {
    const app = document.querySelector<HTMLElement>('.app')!;
    const se = document.scrollingElement!;
    const r = app.getBoundingClientRect();
    return {
      innerWidth, innerHeight, vvHeight: visualViewport?.height ?? innerHeight,
      scrollWidth: se.scrollWidth, scrollHeight: se.scrollHeight, appTop: r.top, appHeight: r.height, appWidth: r.width,
      phone: app.classList.contains('phone'),
      // A phone's sidebar is the drawer (closed after the rotation); a desktop width's is the plain sidebar, on screen.
      drawer: document.querySelector<HTMLElement>('[data-test=sidebar]')?.dataset.drawer ?? null,
      sidebarRight: document.querySelector('[data-test=sidebar]')?.getBoundingClientRect().right ?? null,
    };
  });
}

/**
 * The page's own CSS as built and as WebKit parsed it: the .app rule's height and row; and the -webkit-text-size-adjust
 * on <body> as remote.html writes it (this desktop WebKit has no text-size-adjust, so its CSSOM drops the property).
 */
function parsedCss(page: Page) {
  return page.evaluate(() => {
    const rules = [...document.styleSheets].flatMap((s) => { try { return [...s.cssRules]; } catch { return []; } })
      .filter((r): r is CSSStyleRule => r instanceof CSSStyleRule);
    const app = rules.find((r) => /^\.app\[data-v-[^\]]+\]$/.test(r.selectorText));
    return {
      appHeight: app?.style.getPropertyValue('height') ?? null,
      appRows: app?.style.getPropertyValue('grid-template-rows') ?? null,
      textSizeAdjust: /-webkit-text-size-adjust:\s*100%/.test(document.body.getAttribute('style') ?? ''),
    };
  });
}

for (const r of ROTATIONS) for (const p of PLACES) {
  const from = devices[r.from].viewport;
  const to = devices[r.to].viewport;
  test(`${r.device}, ${p.place}: ${from.width}x${from.height} -> ${to.width}x${to.height} fits the screen after rotating`, async ({ browser }) => {
    const { defaultBrowserType: _b, ...descriptor } = devices[r.from];
    const s = fakeRemote(builtDir);
    s.core.setAsks(false);
    const ctx = await browser.newContext(descriptor);
    await s.attach(ctx);
    const page = await ctx.newPage();
    const errors: string[] = [];
    page.on('pageerror', (e) => errors.push(e.message));
    await page.goto(`${s.url}${p.hash}`);
    await expect(page.locator(p.ready).first()).toBeVisible({ timeout: 30_000 });
    await page.waitForTimeout(300);
    const before = await measure(page);
    expect(before.phone, `before: ${JSON.stringify(before)}`).toBe(from.width < PHONE_BREAKPOINT);

    await page.setViewportSize(to);
    await page.evaluate(() => new Promise((ok) => requestAnimationFrame(() => requestAnimationFrame(ok))));
    await page.waitForTimeout(300);
    const after = await measure(page);
    const info = JSON.stringify({ before, after });
    expect(after.innerWidth, info).toBe(to.width);
    expect(after.scrollWidth, `horizontal overflow: ${info}`).toBeLessThanOrEqual(after.innerWidth);
    expect(after.appTop, info).toBe(0);
    expect(Math.abs(after.appHeight - after.vvHeight), `app root height vs visible viewport: ${info}`).toBeLessThanOrEqual(1);
    expect(after.scrollHeight, `vertical overflow: ${info}`).toBeLessThanOrEqual(after.innerHeight);
    // The layout for the new width: a phone has the menu button and its sidebar a closed drawer; a desktop width the sidebar.
    const phone = to.width < PHONE_BREAKPOINT;
    expect(after.phone, info).toBe(phone);
    expect(after.drawer, info).toBe(phone ? 'closed' : null);
    if (phone) expect(after.sidebarRight!, info).toBeLessThanOrEqual(0);
    else expect(after.sidebarRight, info).toBe(268);
    await expect(page.locator('.menu-btn')).toHaveCount(phone ? 1 : 0);
    await expect(page.locator(p.ready).first()).toBeVisible();

    expect(await parsedCss(page)).toEqual({ appHeight: '100dvh', appRows: 'minmax(0px, 1fr)', textSizeAdjust: true });
    expect(errors).toEqual([]);
    await ctx.close();
  });
}
