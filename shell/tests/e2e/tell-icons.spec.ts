// Card 344 (verify card 342): the Tell the Manager row's Attach and Send buttons draw monochrome SVG line icons, not
// emoji. In headless Chrome against the phone-remote fake core (tests/e2e/phone), at 375 px and 1280 px, light and dark:
// both icons are aria-hidden 20 px svgs stroked in the button's own text colour (so they follow the theme), centred
// in their buttons (44 px tall on the phone, B037), the row stays on one line (B054), the phone's Send is the plane only (aria-label
// "Tell Manager"), the desktop's is "Tell Manager" + plane, and "Sending…" shows while bus.tellManager is held.
//
//   npx playwright test tell-icons      (E2E_SKIP_BUILD=1 reuses out/e2e/renderer)
// Screenshots go to $YAWBLE_TEAM_DOCS/card344-tell-icons (else test-results/card344-tell-icons).
import { expect, test, type Browser } from './phone/fixtures';
import { mkdirSync } from 'node:fs';
import path from 'node:path';
import { createFakeCore } from './phone/fake-core';
import { ORIGIN, buildRenderer, builtDir } from './phone/server';
import { existsSync } from 'node:fs';

test.use({ channel: 'chrome', headless: true, actionTimeout: 8_000 });
test.setTimeout(60_000);

const SHOTS = path.join(process.env.YAWBLE_TEAM_DOCS || path.resolve(__dirname, '..', '..', 'test-results'), 'card344-tell-icons');

test.beforeAll(async () => { test.setTimeout(600_000); mkdirSync(SHOTS, { recursive: true }); await buildRenderer(); });

async function open(browser: Browser, w: number, h: number, theme: 'light' | 'dark') {
  const core = createFakeCore();
  core.setAsks(false);
  let held: ((out: string) => void) | null = null;
  let holdTell = false;
  const phone = w < 760;
  const ctx = await browser.newContext({ viewport: { width: w, height: h }, isMobile: phone, hasTouch: phone, deviceScaleFactor: 2, colorScheme: theme });
  await ctx.addInitScript((t) => { try { localStorage.setItem('yawble.remoteTheme', t); } catch { /* none */ } }, theme);
  await ctx.route(`${ORIGIN}/**`, async (route) => {
    const rel = decodeURIComponent(new URL(route.request().url()).pathname).replace(/^\/+/, '');
    const file = path.resolve(builtDir, rel || 'remote.html');
    if (!file.startsWith(builtDir) || !existsSync(file)) return route.fulfill({ status: 404, body: 'not found' });
    return route.fulfill({ path: file });
  });
  await ctx.routeWebSocket(/\/rpc$/, (ws) => {
    ws.onMessage((m) => {
      const msg = JSON.parse(String(m));
      if (msg.method === 'bus.tellManager') {
        const out = JSON.stringify({ id: msg.id, result: { workflow: 99 } });
        if (holdTell) held = () => ws.send(out); else ws.send(out);
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
  if (phone) {
    await page.locator('[data-test=drawer-toggle]').first().click();
  }
  await page.locator('[data-test=sidebar] [data-test=nav-teams]').first().click();
  await page.locator('[data-test=team-open-payments]').click();
  await expect(page.locator('[data-test=tell-send]')).toBeVisible();
  await page.waitForTimeout(300);
  return { page, ctx, errors, hold: (on: boolean) => { holdTell = on; }, release: () => { held?.(); held = null; } };
}

const EMOJI = /\p{Extended_Pictographic}/u;

for (const [w, h] of [[375, 812], [1280, 800]] as const) {
  for (const theme of ['light', 'dark'] as const) {
    test(`Tell row ${w}px ${theme}: monochrome centred icons, one line, labels, Sending…`, async ({ browser }) => {
      const { page, ctx, errors, hold, release } = await open(browser, w, h, theme);
      const phone = w < 760;
      const row = page.locator('.tell.row');
      await row.scrollIntoViewIfNeeded();
      await row.screenshot({ path: path.join(SHOTS, `tell-${w}-${theme}.png`) });
      await page.screenshot({ path: path.join(SHOTS, `team-${w}-${theme}.png`) });

      const m = await page.evaluate(() => {
        const rowEl = document.querySelector('.tell.row') as HTMLElement;
        const one = (sel: string) => {
          const b = document.querySelector(sel) as HTMLElement;
          const svg = b.querySelector('svg') as SVGSVGElement | null;
          const br = b.getBoundingClientRect();
          const sr = svg?.getBoundingClientRect();
          const strokes = svg ? [...svg.querySelectorAll('rect,circle,path')].map((e) => getComputedStyle(e).stroke) : [];
          const fills = svg ? [...svg.querySelectorAll('rect,circle,path')].map((e) => getComputedStyle(e).fill) : [];
          return {
            text: b.textContent ?? '', aria: b.getAttribute('aria-label'), color: getComputedStyle(b).color,
            svgs: b.querySelectorAll('svg').length, hidden: svg?.getAttribute('aria-hidden'), strokeAttr: svg?.getAttribute('stroke'),
            strokes, fills, bw: br.width, bh: br.height, bcy: br.top + br.height / 2,
            sw: sr?.width, sh: sr?.height, scy: sr ? sr.top + sr.height / 2 : null, sx: sr?.left ?? null,
            textRight: (() => { const r = document.createRange(); const t = [...b.querySelectorAll('*')].flatMap((e) => [...e.childNodes]).concat([...b.childNodes]).find((n) => n.nodeType === 3 && n.textContent!.trim()); if (!t) return null; r.selectNodeContents(t); return r.getBoundingClientRect().right; })(),
          };
        };
        const kids = [...rowEl.children].filter((c) => (c as HTMLElement).offsetParent !== null).map((c) => { const r = c.getBoundingClientRect(); return { tag: c.tagName, top: r.top, bottom: r.bottom }; });
        return { attach: one('[data-test=tell-attach]'), send: one('[data-test=tell-send]'), kids, rowH: rowEl.getBoundingClientRect().height, sw: document.documentElement.scrollWidth, iw: innerWidth };
      });
      console.log(`${w} ${theme}`, JSON.stringify(m));

      for (const [name, b] of [['attach', m.attach], ['send', m.send]] as const) {
        expect.soft(b.svgs, `${name}: one svg`).toBe(1);
        expect.soft(b.hidden, `${name}: svg aria-hidden`).toBe('true');
        expect.soft(b.strokeAttr, `${name}: stroke currentColor`).toBe('currentColor');
        expect.soft(EMOJI.test(b.text), `${name}: no emoji in "${b.text}"`).toBe(false);
        expect.soft(b.sw, `${name}: svg 20 px wide`).toBe(20);
        expect.soft(b.sh, `${name}: svg 20 px tall`).toBe(20);
        for (const s of b.strokes) expect.soft(s, `${name}: stroke follows the button colour`).toBe(b.color);
        for (const f of b.fills) expect.soft(f, `${name}: no fill`).toBe('none');
        if (phone) expect.soft(b.bh, `${name}: button >= 44 px tall`).toBeGreaterThanOrEqual(44);
        expect.soft(Math.abs(b.scy! - b.bcy), `${name}: icon centred vertically (svg ${b.scy} vs button ${b.bcy})`).toBeLessThanOrEqual(1.5);
      }
      // One line (B054): every visible child of the row shares the same band.
      const tops = m.kids.map((k) => k.top);
      expect.soft(Math.max(...tops) - Math.min(...tops), `row on one line: ${JSON.stringify(m.kids)}`).toBeLessThanOrEqual(4);
      expect.soft(m.sw, 'no sideways scroll').toBeLessThanOrEqual(m.iw);
      if (phone) {
        expect.soft(m.send.text.trim(), 'phone Send: plane only').toBe('');
        expect.soft(m.send.aria, 'phone Send aria-label').toBe('Tell Manager');
      } else {
        expect.soft(m.send.text.trim(), 'desktop Send text').toBe('Tell Manager');
        expect.soft(m.send.aria, 'desktop Send has no aria-label').toBeNull();
        expect.soft(m.send.textRight! <= m.send.sx!, 'desktop plane after the text').toBe(true);
      }
      expect.soft(m.attach.aria ?? (await page.locator('[data-test=tell-attach]').getAttribute('title')), 'attach title').toBe('Attach an image');

      // Busy: hold bus.tellManager and look at the button.
      hold(true);
      await page.locator('[data-test=tell-text]').fill('Card 344 busy check');
      await page.locator('[data-test=tell-send]').click();
      await expect(page.locator('[data-test=tell-send]')).toContainText('Sending…');
      await expect(page.locator('[data-test=tell-send]')).toHaveAttribute('aria-busy', 'true');
      const busyH = await page.locator('[data-test=tell-send]').evaluate((b) => b.getBoundingClientRect().height);
      if (phone) expect.soft(busyH, 'busy button still >= 44 px').toBeGreaterThanOrEqual(44);
      await row.screenshot({ path: path.join(SHOTS, `tell-${w}-${theme}-sending.png`) });
      release();
      await expect(page.locator('[data-test=tell-send]')).not.toContainText('Sending…');
      await expect(page.getByText('Sent to the Manager · workflow 99')).toBeVisible();
      expect(errors).toEqual([]);
      await ctx.close();
    });
  }
}

test('icons follow the theme: light and dark draw in different colours', async ({ browser }) => {
  const colours: Record<string, string> = {};
  for (const theme of ['light', 'dark'] as const) {
    const { page, ctx } = await open(browser, 375, 812, theme);
    colours[theme] = await page.locator('[data-test=tell-attach] svg rect').evaluate((e) => getComputedStyle(e).stroke);
    await ctx.close();
  }
  console.log('attach stroke', JSON.stringify(colours));
  expect(colours.light).not.toBe(colours.dark);
});
